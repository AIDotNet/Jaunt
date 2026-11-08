using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Jaunt.Compiler;

namespace Jaunt.Runtime;

[Flags]
internal enum FrameFlags : byte
{
    None = 0,
    Construct = 1,
    OwnsRegs = 2,
    Coroutine = 4,
    /// <summary>The callee's realm differs from the caller's: <see cref="Frame.CallerRealm"/> is set and restored on exit.</summary>
    RealmSwitch = 8,
    /// <summary>Entry frame of a <c>RunLoop</c> invocation: returning from or unwinding past it leaves the loop.</summary>
    Entry = 16,
}

internal enum SuspendKind : byte { None, Yield, YieldRaw, Await, Initial }

/// <summary>One captured stack frame: the function and the pc of the instruction it was executing.</summary>
internal readonly struct StackFrameRecord
{
    public readonly FunctionTemplate Template;
    public readonly int Pc;
    public StackFrameRecord(FunctionTemplate template, int pc) { Template = template; Pc = pc; }
}

/// <summary>Activation record of a bytecode function. Frames are pooled except for coroutines.</summary>
internal sealed class Frame
{
    public FunctionTemplate T = null!;
    public int[] Code = null!;
    /// <summary><c>T.Constants</c>, at hand so a call or return reloads the interpreter's constant pool with one load.</summary>
    public JsValue[] K = null!;
    public JsValue[] Regs = null!;
    public int Base;
    public int Pc;
    public Env? Env;
    public int EnvDepth;
    public JsValue This;
    public JsValue NewTarget;
    /// <summary>Shared this-binding of a derived constructor once it created arrow functions (or of such an arrow).</summary>
    public ThisBox? ThisBox;
    public JsClosure Callee = null!;
    public Feedback Feedback = null!;
    public JsValue[]? Args;
    /// <summary>
    /// Coroutine frames only: the pooled frame this heap frame displaced from <c>Vm._frames</c> at its depth while it is
    /// attached (restored when it detaches). The active frames are addressed by depth, see <see cref="Vm.Current"/>.
    /// </summary>
    public Frame? Displaced;
    public int RetIndex;
    public FrameFlags Flags;
    public int ChunkIndex;
    /// <summary>The register stack position (<c>Vm._chunkIndex</c>, <c>Vm._top</c>) before this frame was pushed; restored when it is popped.</summary>
    public int SavedChunkIndex;
    public int SavedTop;
    /// <summary>
    /// Realm that was current when this frame was entered; only valid with <see cref="FrameFlags.RealmSwitch"/> (otherwise it
    /// is the callee's own realm, see <see cref="Vm.CallerRealmOf"/>). Not written on same-realm calls: every reference store
    /// into a pooled frame costs a GC write barrier, and calls are the hottest path of the VM.
    /// </summary>
    public Realm CallerRealm = null!;
    // coroutine state
    public int ResumeSentReg = -1;
    public int ResumeModeReg = -1;
    public SuspendKind Suspend;
}

/// <summary>
/// Per-engine virtual machine: register stack, frame stack, execution budget and job queue. A Vm is used by
/// at most one logical executor at a time.
/// </summary>
internal sealed partial class Vm
{
    /// <summary>The current realm: the realm of the running function (switched on frame entry and exit).</summary>
    public Realm Realm;
    /// <summary>Prototype-mutation epoch shared by all realms of this Vm (see <see cref="Realm.ProtoEpoch"/>).</summary>
    public int ProtoEpoch;
    private readonly Realm _homeRealm;
    private readonly List<JsValue[]> _chunks = new();
    /// <summary><c>_chunks[_chunkIndex]</c>, kept at hand so a call does not go through the list.</summary>
    private JsValue[] _chunk = Array.Empty<JsValue>();
    private int _chunkIndex;
    private int _top;
    private const int InitialChunkSize = 128;
    private const int MaxChunkSize = 64 * 1024;
    /// <summary>
    /// The frame stack, addressed by depth: <c>_frames[d]</c> is the frame running at depth <c>d</c> for
    /// <c>d &lt; Depth</c>, and a pooled frame reused by the next call at that depth above it. A coroutine (heap) frame
    /// takes the slot while it is attached and gives it back to the pooled frame it displaced. Addressing frames by depth
    /// instead of linking them (Caller) and tracking the innermost one in a field saves three GC write barriers per
    /// call and return, which is the hottest path of the VM.
    /// </summary>
    private Frame?[] _frames = new Frame?[16];
    /// <summary>Number of active bytecode frames: <c>_frames[0..Sp)</c>.</summary>
    public int Sp;
    /// <summary>Native (CLR) nesting levels entered through <see cref="EnterNative"/>; they count toward <see cref="MaxDepth"/>.</summary>
    private int _nativeDepth;
    /// <summary>Call depth charged against <see cref="MaxDepth"/>: bytecode frames plus native nesting levels.</summary>
    public int Depth => Sp + _nativeDepth;
    /// <summary>The innermost active frame, or null when no bytecode is running.</summary>
    public Frame? Current => Sp == 0 ? null : _frames[Sp - 1];
    /// <summary>The frame at the given depth (0 is the outermost); valid for <c>depth &lt; Depth</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Frame FrameAt(int depth) => _frames[depth]!;

    // ---- budget
    public int MaxDepth = 10_000;
    private int _ticks = int.MaxValue;
    private int _tickChunk = int.MaxValue;
    private long _workUsed;
    private long _maxWork = long.MaxValue;
    private long _deadline = long.MaxValue;
    private CancellationToken _token;
    private volatile int _interrupt;
    /// <summary>
    /// Orders <see cref="RequestInterrupt"/> against the start and end of executions, so that a request made for one
    /// execution is cleared when it ends and can never stop the next one.
    /// </summary>
    private readonly object _interruptLock = new();
    private bool _budgetActive;
    private TimeSpan _timeout;

    // ---- suspension
    public SuspendKind LastSuspend;

    public Vm(Realm realm)
    {
        Realm = _homeRealm = realm;
        _chunks.Add(Array.Empty<JsValue>());
    }

    // ------------------------------------------------------------------ budget

    public void BeginBudget(long maxWork, TimeSpan? timeout, CancellationToken token, int maxDepth)
    {
        _workUsed = 0;
        _maxWork = maxWork <= 0 ? long.MaxValue : maxWork;
        _deadline = timeout is { } t && t != Timeout.InfiniteTimeSpan ? Stopwatch.GetTimestamp() + (long)(t.TotalSeconds * Stopwatch.Frequency) : long.MaxValue;
        _timeout = timeout ?? TimeSpan.Zero;
        _token = token;
        lock (_interruptLock) _interrupt = 0;
        MaxDepth = maxDepth;
        _budgetActive = true;
        ResetTicks();
    }

    public void EndBudget()
    {
        _budgetActive = false;
        _token = default;
        _maxWork = long.MaxValue;
        _deadline = long.MaxValue;
        lock (_interruptLock) _interrupt = 0;
        _ticks = int.MaxValue;
    }

    public long WorkUsed => _workUsed + (_tickChunk - _ticks);

    private void ResetTicks()
    {
        long remaining = _maxWork - _workUsed;
        int chunk = (int)Math.Min(1024, Math.Max(1, remaining));
        _tickChunk = chunk;
        _ticks = chunk;
    }

    /// <summary>Interrupts the active execution if <paramref name="isActive"/> holds at the time of the request.</summary>
    public void RequestInterrupt(Func<bool> isActive)
    {
        lock (_interruptLock)
        {
            if (isActive()) _interrupt = 1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Tick()
    {
        if (--_ticks <= 0) Checkpoint();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Tick(int n)
    {
        _ticks -= n;
        if (_ticks <= 0) Checkpoint();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Checkpoint()
    {
        _workUsed += _tickChunk - _ticks;
        if (!_budgetActive) { _workUsed = 0; ResetTicks(); return; }
        if (_workUsed > _maxWork)
            throw new JsTerminationException(TerminationKind.Instructions, $"Execution exceeded the instruction budget of {_maxWork}.", _workUsed, _maxWork);
        if (_token.IsCancellationRequested)
            throw new JsTerminationException(TerminationKind.Cancelled, "Execution was cancelled.");
        if (_interrupt != 0)
            throw new JsTerminationException(TerminationKind.Interrupted, "Execution was interrupted.");
        if (_deadline != long.MaxValue && Stopwatch.GetTimestamp() > _deadline)
            throw new JsTerminationException(TerminationKind.Timeout, $"Execution exceeded its timeout of {_timeout}.");
        ResetTicks();
    }

    public CancellationToken Token => _token;

    [ThreadStatic] private static Vm? t_current;

    /// <summary>
    /// The VM running JS on this thread (null outside any execution). Library code without a realm at hand — the RegExp
    /// compiler, Temporal calendar arithmetic, BigInt conversion, eval setup — uses it to charge work and reach the
    /// execution's Timeout/cancellation/Interrupt checkpoints. Scoped to <see cref="Run"/>, so it never outlives a run.
    /// </summary>
    internal static Vm? OnThread => t_current;

    /// <summary>Charges <paramref name="ints"/> 32-bit words built by a compiler (e.g. RegExp character sets): bytes and work.</summary>
    internal void ChargeCompileWork(int ints)
    {
        Realm.ChargeBytes(32 + ints * 4L);
        Tick(1 + ints / 64);
    }

    internal JsValue Run(Frame entry)
    {
        return _onThread ? RunGuarded(entry) : RunOnThread(entry);   // nested native→JS call: already current
    }

    /// <summary>
    /// Whether this Vm is <see cref="t_current"/>: a thread-static read per native→JS call costs several nanoseconds, this
    /// field a load. It is only ever true on the thread running the Vm's outermost <see cref="Run"/>.
    /// </summary>
    private bool _onThread;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue RunOnThread(Frame entry)
    {
        var saved = t_current;
        if (saved != null) saved._onThread = false;   // a host function of that Vm entered this one
        t_current = this;
        _onThread = true;
        try { return RunGuarded(entry); }
        finally
        {
            _onThread = false;
            t_current = saved;
            if (saved != null) saved._onThread = true;
            else _stackCheckedTo = nuint.MaxValue;   // the next execution may run on another thread
        }
    }

    /// <summary>Whether the active execution has a wall-clock deadline.</summary>
    public bool HasDeadline => _budgetActive && _deadline != long.MaxValue;

    /// <summary>Time left until the active execution's deadline (meaningful only when <see cref="HasDeadline"/>).</summary>
    public TimeSpan RemainingTime
    {
        get
        {
            long now = Stopwatch.GetTimestamp();
            return _deadline > now ? TimeSpan.FromSeconds((_deadline - now) / (double)Stopwatch.Frequency) : TimeSpan.Zero;
        }
    }

    public void ThrowIfInterruptPending()
    {
        if (_interrupt != 0) throw new JsTerminationException(TerminationKind.Interrupted, "Execution was interrupted.");
    }

    /// <summary>The time-like limits of the active execution (cancellation, Interrupt, deadline) without charging work; used while compiling.</summary>
    public void CheckTimeLimits()
    {
        if (!_budgetActive) return;
        if (_token.IsCancellationRequested)
            throw new JsTerminationException(TerminationKind.Cancelled, "Execution was cancelled.");
        if (_interrupt != 0)
            throw new JsTerminationException(TerminationKind.Interrupted, "Execution was interrupted.");
        if (_deadline != long.MaxValue && Stopwatch.GetTimestamp() > _deadline)
            throw new JsTerminationException(TerminationKind.Timeout, $"Execution exceeded its timeout of {_timeout}.");
    }

    // ------------------------------------------------------------------ native boundary

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnterNative()
    {
        if (Sp + ++_nativeDepth > MaxDepth) StackOverflow();
        if (!StackIsSufficient()) StackOverflow();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExitNative() => _nativeDepth--;

    /// <summary>Guards native (CLR-stack) recursion: nested interpreter runs, bound/proxy chains, recursive builtins.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnsureNativeStack()
    {
        if (!StackIsSufficient()) NativeStackOverflow();
    }

    /// <summary>
    /// Lowest stack address known to be sufficient (see <see cref="StackIsSufficient"/>); <c>nuint.MaxValue</c> for "none", which
    /// is its value whenever no execution is running, since the next one may be on another thread.
    /// </summary>
    private nuint _stackCheckedTo = nuint.MaxValue;

    /// <summary>How far below a successful probe the stack may grow without another one; the runtime's probe asks for 64 KB (128 KB on 64-bit) of room.</summary>
    private const int StackProbeReach = 16 * 1024;

    /// <summary>
    /// <see cref="RuntimeHelpers.TryEnsureSufficientExecutionStack"/> for the native→JS call path: the runtime's probe reads thread-local
    /// state (several nanoseconds under NativeAOT), so one successful probe also covers the <see cref="StackProbeReach"/> below it,
    /// which leaves at least 48 KB of the room it asks for. A loop of callbacks at one depth probes once.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe bool StackIsSufficient()
    {
        byte marker;
        return (nuint)(&marker) >= _stackCheckedTo || ProbeStack((nuint)(&marker));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool ProbeStack(nuint at)
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) return false;
        if (_onThread) _stackCheckedTo = at - StackProbeReach;
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void NativeStackOverflow() =>
        throw new JsTerminationException(TerminationKind.StackDepth, "Native recursion is too deep to continue safely.", Depth, MaxDepth);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void StackOverflow()
    {
        _nativeDepth--;
        throw new JsTerminationException(TerminationKind.StackDepth, $"Maximum call stack depth ({MaxDepth}) exceeded.", Depth + 1, MaxDepth);
    }

    // ------------------------------------------------------------------ register stack and frames

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private JsValue[] AllocRegs(int count, out int baseIndex, out int chunkIndex)
    {
        var chunk = _chunk;
        int top = _top;
        if (top + count > chunk.Length) return AllocRegsSlow(count, out baseIndex, out chunkIndex);
        baseIndex = top;
        chunkIndex = _chunkIndex;
        top += count;
        _top = top;
        if (top > _hwm) _hwm = top;
        return chunk;
    }

    /// <summary>Highest register index used in the current chunk since the last clear (see <see cref="ClearRegisterStack"/>).</summary>
    private int _hwm;
    private int _chunkHwm;

    /// <summary>
    /// Debug aid: fill the registers a call does not initialize with a recognizable string, so that a bytecode read of
    /// an uninitialized register shows up in test results (JAUNT_POISON_REGISTERS=1). Read once per process into
    /// <see cref="RegisterPoison"/>, then copied into this instance field: the check runs on every pushed frame, and an
    /// instance field is a plain load, whereas a static of a class whose static constructor reads the environment
    /// cannot be pre-initialized by the NativeAOT compiler and costs a class-initialization check per read there (the JIT
    /// folds the static to a constant, so CoreCLR never paid for it).
    /// </summary>
    private readonly bool _poisonRegisters = RegisterPoison.Enabled;
    private static readonly JsValue s_poison = new("<<uninitialized register>>");

    /// <summary>
    /// Clears the register stack up to its high-water mark. Popped frames leave their registers behind (clearing per
    /// call costs more than it saves); the root execution clears them once so an idle engine retains no object graph.
    /// </summary>
    internal void ClearRegisterStack()
    {
        if (_chunks.Count > 0 && _chunks[0].Length > 0) Array.Clear(_chunks[0], 0, Math.Min(_hwm, _chunks[0].Length));
        for (int i = 1; i <= _chunkHwm && i < _chunks.Count; i++) Array.Clear(_chunks[i]);
        _hwm = 0;
        _chunkHwm = 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue[] AllocRegsSlow(int count, out int baseIndex, out int chunkIndex)
    {
        var chunk = _chunk;
        int prevLen = chunk.Length;
        if (_top == 0 && _chunkIndex == 0 && prevLen < MaxChunkSize)
        {
            // nothing live in the first chunk yet: just grow it
            _chunks[0] = _chunk = chunk = NewChunk(Math.Max(prevLen == 0 ? InitialChunkSize : Math.Min(prevLen * 4, MaxChunkSize), count));
            baseIndex = 0;
            chunkIndex = 0;
            _top = count;
            _hwm = count;   // the previous chunk 0 is gone
            return chunk;
        }
        _chunkIndex++;
        int size = Math.Max(Math.Min(prevLen * 2, MaxChunkSize), count);
        if (_chunkIndex == _chunks.Count) _chunks.Add(NewChunk(size));
        else if (_chunks[_chunkIndex].Length < count) _chunks[_chunkIndex] = NewChunk(size);
        _chunk = chunk = _chunks[_chunkIndex];
        _top = 0;
        baseIndex = 0;
        chunkIndex = _chunkIndex;
        _top = count;
        if (_chunkIndex > _chunkHwm) _chunkHwm = _chunkIndex;
        return chunk;
    }

    /// <summary>
    /// A register chunk whose elements are 16-byte aligned. The GC aligns objects to 8 bytes, and with the elements at 8 mod 16 a
    /// register load right after the store of the same register (the interpreter's usual pattern) straddles what the
    /// store wrote as a pair: the same workload ran up to 35% slower depending on what was allocated before the chunk.
    /// An array's size is 8 mod 16, so the next object after a misaligned array is aligned.
    /// </summary>
    private static JsValue[] NewChunk(int size)
    {
        var chunk = new JsValue[size];
        for (int i = 0; i < 8 && !IsAligned(chunk); i++) chunk = new JsValue[size];
        return chunk;
    }

    private static unsafe bool IsAligned(JsValue[] chunk) => chunk.Length == 0 || ((nuint)Unsafe.AsPointer(ref chunk[0]) & 15) == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Frame RentFrame()
    {
        int d = Sp;
        var frames = _frames;
        if ((uint)d < (uint)frames.Length)
        {
            var f = frames[d];
            if (f != null) return f;
        }
        return RentFrameSlow();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Frame RentFrameSlow()
    {
        int d = Sp;
        if (d >= _frames.Length) Array.Resize(ref _frames, Math.Max(_frames.Length * 2, d + 1));
        return _frames[d] ??= new Frame();
    }

    /// <summary>Puts a coroutine (heap) frame into the stack slot at the current depth, displacing the pooled frame there.</summary>
    private void PlaceHeapFrame(Frame f)
    {
        int d = Sp;
        if (d >= _frames.Length) Array.Resize(ref _frames, Math.Max(_frames.Length * 2, d + 1));
        f.Displaced = _frames[d];
        _frames[d] = f;
    }

    /// <summary>Removes the coroutine frame at depth <see cref="Depth"/> - 1 from the stack, restoring the pooled frame.</summary>
    private void RemoveHeapFrame(Frame f)
    {
        int d = Sp - 1;
        Debug.Assert(ReferenceEquals(_frames[d], f));
        _frames[d] = f.Displaced;
        f.Displaced = null;
    }

    /// <summary>Pushes a frame for a closure call; arguments are copied into the new register window.</summary>
    internal Frame PushFrame(JsClosure c, JsValue thisValue, ReadOnlySpan<JsValue> args, JsValue newTarget, FrameFlags flags)
    {
        if (Depth + 1 > MaxDepth) StackOverflowFrame();
        var t = c.Template;
        Frame f;
        int savedTop = _top, savedChunk = _chunkIndex;
        if ((flags & FrameFlags.OwnsRegs) != 0)
        {
            f = new Frame();
            f.Regs = new JsValue[t.RegisterCount];
            f.Base = 0;
            f.ChunkIndex = -1;
            PlaceHeapFrame(f);
        }
        else
        {
            f = RentFrame();
            var regs = AllocRegs(t.RegisterCount, out f.Base, out f.ChunkIndex);
            if (!ReferenceEquals(f.Regs, regs)) f.Regs = regs;   // a pooled frame usually sits in the same chunk as last time
        }
        f.SavedTop = savedTop;
        f.SavedChunkIndex = savedChunk;
        int n = Math.Min(args.Length, t.ParamCount);
        {
            var regs = f.Regs; int b = f.Base;
            if (n <= 4)
            {
                // element stores beat the bulk copy helper for the usual one to four arguments
                for (int i = 0; i < n; i++) JsValue.Store(ref regs[b + i], args[i]);
            }
            else args.Slice(0, n).CopyTo(regs.AsSpan(b, n));
            // Registers are not cleared when a frame is popped (the pooled stack is cleared when the root execution
            // ends); the callee's function-scope bindings and missing parameters must start as undefined, every other
            // register is written before it is read (see FunctionTemplate.InitUndefEnd; _poisonRegisters checks it).
            int initEnd = t.InitUndefEnd < 0 ? t.RegisterCount : t.InitUndefEnd;
            for (int i = n; i < initEnd; i++) regs[b + i] = default;
            if (_poisonRegisters) for (int i = initEnd; i < t.RegisterCount; i++) regs[b + i] = s_poison;
        }
        if ((t.Flags & FunctionFlags.NeedsArgsArray) != 0) f.Args = args.ToArray();   // otherwise still null from PopFrame
        SetupFrame(f, c, thisValue, newTarget, flags);
        return f;
    }

    /// <summary>
    /// <see cref="PushFrame"/> for the interpreter's Call/CallMethod. The callee's register window starts at the caller's
    /// first argument register, so the arguments are already in place and nothing is copied: the compiler evaluates a
    /// call's arguments into the topmost registers that are live at the call (<c>CodeGen.Arguments</c>), so the callee
    /// may clobber every caller register from <paramref name="argStart"/> up (<see cref="_poisonRegisters"/> checks that the
    /// caller never reads them again). The window does not overlap when the caller's registers are not the shared stack
    /// (a coroutine frame owns its registers) or the callee does not fit in the chunk: those calls copy as PushFrame does.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Frame PushCallFrame(JsClosure c, JsValue[] callerRegs, int thisIndex, int argStart, int argc)
    {
        if (Depth + 1 > MaxDepth) StackOverflowFrame();
        var f = RentFrame();
        BindCallWindow(f, c.Template, callerRegs, argStart, argc);
        // the this register is below argStart (the compiler evaluates the receiver before the arguments): still intact
        SetupCallFrame(f, c, callerRegs, thisIndex);
        return f;
    }

    /// <summary><see cref="PushCallFrame"/> for the paths other than the interpreter's own call instruction, which is the one place it is inlined.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Frame PushCallFrameOutOfLine(JsClosure c, JsValue[] callerRegs, int thisIndex, int argStart, int argc) =>
        PushCallFrame(c, callerRegs, thisIndex, argStart, argc);

    /// <summary><see cref="PushCallFrame"/> for the interpreter's New: the frame constructs <paramref name="obj"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal Frame PushNewFrame(JsClosure c, JsValue[] callerRegs, int argStart, int argc, JsObject obj, JsValue newTarget)
    {
        return PushConstructFrame(c, callerRegs, argStart, argc, JsValue.FromObject(obj), newTarget);
    }

    private Frame PushConstructFrame(JsClosure c, JsValue[] callerRegs, int argStart, int argc, JsValue thisValue, JsValue newTarget)
    {
        if (Depth + 1 > MaxDepth) StackOverflowFrame();
        var f = RentFrame();
        BindCallWindow(f, c.Template, callerRegs, argStart, argc);
        SetupFrame(f, c, thisValue, newTarget, FrameFlags.Construct);
        return f;
    }

    /// <summary>
    /// Gives <paramref name="f"/> its register window for a call made by the interpreter (see <see cref="PushCallFrame"/>):
    /// overlapping the caller's argument registers when possible, otherwise a fresh window the arguments are copied to.
    /// Initializes the registers above the arguments and the arguments array.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void BindCallWindow(Frame f, FunctionTemplate t, JsValue[] callerRegs, int argStart, int argc)
    {
        int count = t.RegisterCount;
        f.SavedTop = _top;
        f.SavedChunkIndex = _chunkIndex;
        int n = argc < t.ParamCount ? argc : t.ParamCount;
        JsValue[] regs;
        int b;
        int end = argStart + count;
        if (ReferenceEquals(callerRegs, _chunk) && end <= callerRegs.Length)
        {
            regs = callerRegs;
            b = argStart;
            f.ChunkIndex = _chunkIndex;
            _top = end;
            if (end > _hwm) _hwm = end;
        }
        else
        {
            regs = BindFreshWindow(f, count, callerRegs, argStart, n);
            b = f.Base;
        }
        f.Base = b;
        if (!ReferenceEquals(f.Regs, regs)) f.Regs = regs;
        // before the registers above the parameters are initialized: with an overlapping window the extra arguments live there
        if ((t.Flags & FunctionFlags.NeedsArgsArray) != 0) f.Args = new ReadOnlySpan<JsValue>(callerRegs, argStart, argc).ToArray();
        int initEnd = t.InitUndefEnd < 0 ? count : t.InitUndefEnd;
        for (int i = n; i < initEnd; i++) regs[b + i] = default;
        if (_poisonRegisters) for (int i = initEnd; i < count; i++) regs[b + i] = s_poison;
    }

    /// <summary>A register window that cannot overlap the caller's (see <see cref="PushCallFrame"/>), with the arguments copied in.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue[] BindFreshWindow(Frame f, int count, JsValue[] callerRegs, int argStart, int n)
    {
        var regs = AllocRegs(count, out int b, out f.ChunkIndex);
        f.Base = b;
        Array.Copy(callerRegs, argStart, regs, b, n);
        return regs;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetupFrame(Frame f, JsClosure c, JsValue thisValue, JsValue newTarget, FrameFlags flags)
    {
        // Reference stores into the (heap-allocated, pooled) frame each cost a GC write barrier, so a field is stored
        // only when its value changes. A pooled frame keeps its callee, environment and this between calls (recursion
        // and callbacks in loops re-enter the same closure at the same depth); ReleasePooledFrames drops them when the
        // root execution ends. PopFrame clears Args and NewTarget, and a freshly allocated frame is clear. The frame's
        // place in the stack is its depth (see _frames): no caller link and no "current frame" field are written.
        var t = c.Template;
        if (!ReferenceEquals(f.Callee, c))
        {
            f.Callee = c;
            f.T = t;
            f.Code = t.Code;
            f.K = t.Constants;
            f.Feedback = c.Feedback;
        }
        f.Pc = 0;
        // compared even for the same callee: a frame that left through a throw or a return from inside a block keeps
        // the block environment it had pushed
        var env = c.Env;
        if (!ReferenceEquals(f.Env, env)) f.Env = env;
        f.EnvDepth = 0;
        f.Suspend = SuspendKind.None;
        var box = c.ThisBox;
        if (box != null || f.ThisBox != null) f.ThisBox = box;
        var realm = Realm;
        var calleeRealm = c.Realm;
        if (!ReferenceEquals(realm, calleeRealm))
        {
            f.CallerRealm = realm;
            Realm = calleeRealm;
            flags |= FrameFlags.RealmSwitch;
        }
        f.Flags = flags;
        if ((t.Flags & FunctionFlags.Arrow) != 0)
        {
            f.This = c.BoundThis;
            var nt = c.BoundNewTarget;
            if (nt.Ref != null) f.NewTarget = nt;
        }
        else
        {
            if (newTarget.Ref != null) f.NewTarget = newTarget;
            if ((t.Flags & FunctionFlags.Strict) == 0 && thisValue.IsNotObject)
            {
                if (thisValue.IsNullOrUndefined) thisValue = JsValue.FromObject(calleeRealm.GlobalObject);
                else if (thisValue.IsNotEmpty) thisValue = JsValue.FromObject(Operations.ToObject(calleeRealm, thisValue));
            }
            if (!ReferenceEquals(f.This.Ref, thisValue.Ref) || f.This.Bits != thisValue.Bits) f.This = thisValue;
        }
        Sp++;
    }

    /// <summary>
    /// <see cref="SetupFrame"/> for <see cref="PushCallFrame"/>: new.target is undefined, there are no flags, and
    /// <c>this</c> is either absent (<paramref name="thisIndex"/> &lt; 0: undefined, which a sloppy function sees as its
    /// realm's global object) or read in place from the caller's registers, so no 16-byte value passes through a local.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetupCallFrame(Frame f, JsClosure c, JsValue[] callerRegs, int thisIndex)
    {
        var t = c.Template;
        if (!ReferenceEquals(f.Callee, c))
        {
            f.Callee = c;
            f.T = t;
            f.Code = t.Code;
            f.K = t.Constants;
            f.Feedback = c.Feedback;
        }
        f.Pc = 0;
        var env = c.Env;
        if (!ReferenceEquals(f.Env, env)) f.Env = env;
        f.EnvDepth = 0;
        f.Suspend = SuspendKind.None;
        var box = c.ThisBox;
        if (box != null || f.ThisBox != null) f.ThisBox = box;
        var realm = Realm;
        var calleeRealm = c.Realm;
        var flags = FrameFlags.None;
        if (!ReferenceEquals(realm, calleeRealm))
        {
            f.CallerRealm = realm;
            Realm = calleeRealm;
            flags = FrameFlags.RealmSwitch;
        }
        f.Flags = flags;
        if ((t.Flags & FunctionFlags.Arrow) != 0)
        {
            f.This = c.BoundThis;
            var nt = c.BoundNewTarget;
            if (nt.Ref != null) f.NewTarget = nt;
        }
        else if (thisIndex < 0)
        {
            if ((t.Flags & FunctionFlags.Strict) != 0)
            {
                if (f.This.Ref != null || f.This.Bits != JsValue.UndefinedBits) f.This = default;
            }
            else
            {
                var g = calleeRealm.GlobalObject;
                if (!ReferenceEquals(f.This.Ref, g)) f.This = JsValue.FromObject(g);
            }
        }
        else
        {
            ref readonly var tv = ref callerRegs[thisIndex];
            if ((t.Flags & FunctionFlags.Strict) != 0 || tv.IsObject)
            {
                if (!ReferenceEquals(f.This.Ref, tv.Ref) || f.This.Bits != tv.Bits) f.This = tv;
            }
            else f.This = CoerceSloppyThis(calleeRealm, tv);
        }
        Sp++;
    }

    /// <summary>OrdinaryCallBindThis for a sloppy function and a primitive, null or undefined receiver.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static JsValue CoerceSloppyThis(Realm calleeRealm, in JsValue thisValue)
    {
        if (thisValue.IsNullOrUndefined) return JsValue.FromObject(calleeRealm.GlobalObject);
        if (thisValue.IsEmpty) return thisValue;
        return JsValue.FromObject(Operations.ToObject(calleeRealm, thisValue));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void StackOverflowFrame() =>
        throw new JsTerminationException(TerminationKind.StackDepth, $"Maximum call stack depth ({MaxDepth}) exceeded.", Depth + 1, MaxDepth);

    /// <summary>Pops a frame: releases its register window and drops the per-call references the pooled frame holds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void PopFrame(Frame f)
    {
        var flags = f.Flags;
        if ((flags & FrameFlags.RealmSwitch) != 0) Realm = f.CallerRealm;
        if ((flags & FrameFlags.OwnsRegs) == 0)
        {
            int ci = f.SavedChunkIndex;
            if (_chunkIndex != ci) { _chunkIndex = ci; _chunk = _chunks[ci]; }
            _top = f.SavedTop;
            if (_poisonRegisters) PoisonReleasedWindow(f);
            f.Args = null;
            f.NewTarget = default;
        }
        else RemoveHeapFrame(f);
        Sp--;
    }

    /// <summary>
    /// Debug aid (see <see cref="_poisonRegisters"/>): after a call whose window overlapped the caller's registers, the
    /// caller's registers from the first argument up are poisoned. The compiler guarantees they are dead at the call;
    /// a read of one afterwards is a codegen bug that this makes visible in test results.
    /// </summary>
    private static void PoisonReleasedWindow(Frame f)
    {
        if (f.ChunkIndex != f.SavedChunkIndex) return;
        var regs = f.Regs;
        for (int i = f.Base; i < f.SavedTop; i++) regs[i] = s_poison;
    }

    /// <summary>
    /// Drops the references the pooled frames keep between calls (see <see cref="SetupFrame"/>), so an idle engine does not
    /// retain the closures, environments and receivers of its last execution.
    /// </summary>
    internal void ReleasePooledFrames()
    {
        ClearRegisterStack();
        if (_sortScratch != null && _sortScratch.Length > 1 << 12) _sortScratch = null;   // an idle engine keeps at most 64 KB of sort scratch
        // a compacting GC may have moved the chunk to 8 mod 16; it is empty now, and is rarely moved again once it is old
        if (_chunkIndex == 0 && !IsAligned(_chunk)) _chunks[0] = _chunk = NewChunk(_chunk.Length);
        var pool = _frames;   // Sp is 0 here: every slot holds a pooled frame (or nothing)
        for (int i = 0; i < pool.Length; i++)
        {
            var f = pool[i];
            if (f == null) break;
            f.Callee = null!;
            f.T = null!;
            f.Code = null!;
            f.K = null!;
            f.Feedback = null!;
            f.Env = null;
            f.ThisBox = null;
            f.Args = null;
            f.CallerRealm = null!;
            f.This = default;
        }
    }

    /// <summary>The realm that was current when <paramref name="f"/> was entered.</summary>
    internal static Realm CallerRealmOf(Frame f) => (f.Flags & FrameFlags.RealmSwitch) != 0 ? f.CallerRealm : f.Callee.Realm;

    /// <summary>Detaches a suspended coroutine frame from the active stack (registers are private).</summary>
    internal void DetachFrame(Frame f)
    {
        RemoveHeapFrame(f);
        Sp--;
        if ((f.Flags & FrameFlags.RealmSwitch) != 0) Realm = f.CallerRealm;
    }

    internal void AttachFrame(Frame f)
    {
        if (Depth + 1 > MaxDepth) StackOverflowFrame();
        PlaceHeapFrame(f);
        var realm = Realm;
        var calleeRealm = f.Callee.Realm;
        if (!ReferenceEquals(realm, calleeRealm))
        {
            f.CallerRealm = realm;
            Realm = calleeRealm;
            f.Flags |= FrameFlags.RealmSwitch;
        }
        else f.Flags &= ~FrameFlags.RealmSwitch;
        Sp++;
    }

    // ------------------------------------------------------------------ calls

    public JsValue Call(in JsValue f, in JsValue thisValue, ReadOnlySpan<JsValue> args)
    {
        if (f.IsObject && f.Ref is JsFunctionObject fo) return fo.Call(thisValue, args);
        throw JsErrors.TypeError(Realm, $"{JsErrors.Describe(f)} is not a function");
    }

    public JsValue Construct(in JsValue f, ReadOnlySpan<JsValue> args, JsObject? newTarget = null)
    {
        if (f.IsObject && f.Ref is JsFunctionObject fo && fo.IsConstructor) return fo.Construct(args, newTarget ?? fo);
        throw JsErrors.TypeError(Realm, $"{JsErrors.Describe(f)} is not a constructor");
    }

    internal JsValue CallClosure(JsClosure c, JsValue thisValue, ReadOnlySpan<JsValue> args, JsValue newTarget)
    {
        var t = c.Template;
        if ((t.Flags & (FunctionFlags.Generator | FunctionFlags.Async)) != 0)
        {
            if ((t.Flags & FunctionFlags.Generator) != 0)
                return (t.Flags & FunctionFlags.Async) != 0 ? AsyncGenerators.Start(this, c, thisValue, args) : Coroutines.StartGenerator(this, c, thisValue, args);
            return Coroutines.StartAsync(this, c, thisValue, args);
        }
        EnsureStack();
        var f = PushFrame(c, thisValue, args, newTarget, FrameFlags.None);
        return Run(f);
    }

    internal JsValue ConstructClosure(JsClosure c, ReadOnlySpan<JsValue> args, JsObject newTarget)
    {
        EnsureStack();
        JsValue thisValue;
        if (c.IsDerived) thisValue = JsValue.Empty;
        else
        {
            var proto = Operations.GetPrototypeFromConstructor(Realm, newTarget, Realm.ObjectPrototype);
            thisValue = JsValue.FromObject(NewInstance(newTarget, proto));
            if (c.FieldInit != null) c.FieldInit.Call(thisValue, ReadOnlySpan<JsValue>.Empty);
        }
        var f = PushFrame(c, thisValue, args, JsValue.FromObject(newTarget), FrameFlags.Construct);
        return Run(f);
    }

    /// <summary>
    /// Prepares repeated calls of <paramref name="fn"/> from a builtin loop (sort comparators, callbacks): true when it is
    /// a plain closure that <see cref="CallClosureFast"/> may run; the native stack is probed once here, so the loop
    /// does not probe per call (each native recursion level still probes on entry).
    /// </summary>
    internal bool TryPrepareFastCalls(in JsValue fn, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out JsClosure? closure)
    {
        if (fn.IsObject && fn.Ref is JsClosure c && (c.Template.Flags & (FunctionFlags.Generator | FunctionFlags.Async)) == 0 && !c.IsClassConstructor)
        {
            EnsureStack();
            closure = c;
            return true;
        }
        closure = null;
        return false;
    }

    /// <summary>Calls a closure accepted by <see cref="TryPrepareFastCalls"/>: push and run, nothing else.</summary>
    internal JsValue CallClosureFast(JsClosure c, JsValue thisValue, ReadOnlySpan<JsValue> args)
    {
        var f = PushFrame(c, thisValue, args, JsValue.Undefined, FrameFlags.None);
        return Run(f);
    }

    private JsValue[]? _sortScratch;

    /// <summary>A scratch buffer of at least <paramref name="n"/> elements (sort merges, result-array collection); taken out of the Vm while in use, so a re-entrant use allocates its own.</summary>
    internal JsValue[] TakeScratch(int n)
    {
        var s = _sortScratch;
        _sortScratch = null;
        if (s == null || s.Length < n) s = new JsValue[Math.Max(n, 64)];
        return s;
    }

    /// <summary>Returns the buffer cleared of references; buffers over 64K elements (1 MB) are not retained.</summary>
    internal void ReturnScratch(JsValue[] s, int used)
    {
        Array.Clear(s, 0, used);
        if (s.Length <= 1 << 16) _sortScratch = s;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureStack() => EnsureNativeStack();

    /// <summary>Applies [[Construct]] return semantics.</summary>
    internal JsValue ConstructResult(Frame f, JsValue value)
    {
        if (!TryConstructResult(f, ref value, out var error)) throw new JsThrowException(error);
        return value;
    }

    /// <summary>
    /// Applies [[Construct]] return semantics without throwing: false with <paramref name="error"/> set when a derived
    /// constructor returned a non-object or never called super() (the error belongs to the caller's realm, as it is raised
    /// after the callee context is removed).
    /// </summary>
    internal bool TryConstructResult(Frame f, ref JsValue value, out JsValue error)
    {
        error = default;
        if (value.IsObject) return true;
        if (f.This.IsEmpty && f.ThisBox != null) f.This = f.ThisBox.Value;
        if (f.Callee.IsDerived)
        {
            if (!value.IsUndefined) { error = JsValue.FromObject(JsError.Create(CallerRealmOf(f), ErrorKind.TypeError, "Derived constructors may only return object or undefined")); return false; }
            if (f.This.IsEmpty) { error = JsValue.FromObject(JsError.Create(CallerRealmOf(f), ErrorKind.ReferenceError, "Must call super constructor in derived class before accessing 'this' or returning from derived constructor")); return false; }
        }
        value = f.This;
        // instances of this constructor will be about this big: spares them the growth from no slots, or empty slots
        if (f.NewTarget.Ref is JsClosure nt && value.Ref is JsObject o)
        {
            int n = o.Shape.Count;
            if (n > nt.Feedback.InstanceSlots && n <= MaxInstanceSlots) nt.Feedback.InstanceSlots = n;
        }
        return true;
    }

    internal const int MaxInstanceSlots = 32;

    /// <summary>The object a [[Construct]] of <paramref name="newTarget"/> starts with, slots sized by experience.</summary>
    private JsObject NewInstance(JsObject newTarget, JsObject proto)
    {
        var realm = Realm;
        return newTarget is JsClosure c ? new JsObject(realm, realm.RootShapeFor(proto), c.Feedback.InstanceSlots) : new JsObject(realm, proto);
    }

    // ------------------------------------------------------------------ closures

    internal JsClosure CreateClosure(FunctionTemplate t, Env? env, Feedback fb, Frame? creator, JsObject? protoOverride = null)
    {
        var realm = Realm;
        JsObject fproto = protoOverride ?? (t.IsGenerator ? (t.IsAsync ? realm.AsyncGeneratorFunctionPrototype : realm.GeneratorFunctionPrototype) : t.IsAsync ? realm.AsyncFunctionPrototype : realm.FunctionPrototype);
        var c = new JsClosure(realm, t, env, fb, fproto);
        if (creator != null)
        {
            c.Module = creator.Callee?.Module;
            if (t.IsArrow)
            {
                // arrows in a derived constructor share its this-binding (super() may run later, even in the arrow)
                if (creator.ThisBox != null || (creator.Callee != null && creator.Callee.IsDerived && (creator.Flags & FrameFlags.Construct) != 0))
                    c.ThisBox = creator.ThisBox ??= new ThisBox { Value = creator.This, Ctor = creator.Callee!, NewTarget = creator.NewTarget };
                c.BoundThis = creator.This;
                c.BoundNewTarget = creator.NewTarget;
                c.HomeObject = creator.Callee?.HomeObject;
            }
        }
        if (t.IsGenerator)
        {
            var p = new JsObject(realm, t.IsAsync ? realm.AsyncGeneratorPrototype : realm.GeneratorPrototype);
            c.AddOwnProperty("prototype", JsValue.FromObject(p), PropFlags.Writable);
        }
        else if (t.IsConstructor && !t.IsClassConstructor && (t.Flags & FunctionFlags.NoPrototype) == 0)
        {
            c.AddOwnProperty("prototype", LazyFunction.ClosurePrototypeSlot, PropFlags.Writable);
        }
        return c;
    }

    // ------------------------------------------------------------------ errors raised by builtins without a CLR throw

    /// <summary>
    /// Error value a builtin raised through <see cref="Raise"/>, waiting for the caller that observes the
    /// <see cref="JsValue.Empty"/> return to take it (the interpreter unwinds to a handler; <see cref="JsBuiltin.Call"/>
    /// throws it for native callers).
    /// </summary>
    internal JsValue Pending;

    /// <summary>
    /// Raises <paramref name="error"/> from a builtin without a CLR exception. The builtin returns this method's result
    /// (<see cref="JsValue.Empty"/>, which no function ever returns as a value) and its caller dispatches the throw. A CLR
    /// throw and catch costs microseconds; a JS <c>try/catch</c> around a failing builtin should cost what a JS throw does.
    /// </summary>
    internal JsValue Raise(JsValue error)
    {
        Pending = error;
        return JsValue.Empty;
    }

    internal JsValue TakePending()
    {
        var v = Pending;
        Pending = default;
        return v;
    }

    // ------------------------------------------------------------------ diagnostics

    /// <summary>
    /// The active frames, innermost first, as (template, pc) pairs. Error objects capture this at creation and format the
    /// trace only when <c>stack</c> is read: most errors are caught and never inspected.
    /// </summary>
    public StackFrameRecord[] CaptureStackFrames(int maxFrames = 32)
    {
        int n = Math.Min(Sp, maxFrames);
        if (n == 0) return Array.Empty<StackFrameRecord>();
        var frames = new StackFrameRecord[n];
        for (int i = 0; i < n; i++)
        {
            var f = _frames[Sp - 1 - i]!;
            frames[i] = new StackFrameRecord(f.T, Math.Max(0, f.Pc - 1));
        }
        return frames;
    }

    public static string FormatStackTrace(StackFrameRecord[] frames)
    {
        if (frames.Length == 0) return "";
        var sb = new StringBuilder();
        foreach (var fr in frames)
        {
            var t = fr.Template;
            var (line, col) = t.GetPosition(fr.Pc);
            string name = t.Name;
            if ((t.Flags & (FunctionFlags.ScriptBody | FunctionFlags.ModuleBody)) != 0) name = "";
            sb.Append("\n    at ");
            if (name.Length > 0) sb.Append(name).Append(" (");
            sb.Append(t.Script.SourceName).Append(':').Append(line).Append(':').Append(col);
            if (name.Length > 0) sb.Append(')');
        }
        return sb.ToString();
    }

    public string CaptureStackTrace(int maxFrames = 32) => FormatStackTrace(CaptureStackFrames(maxFrames));

    public (string source, int line, int column)? CurrentLocation()
    {
        var f = Current;
        if (f == null) return null;
        var (line, col) = f.T.GetPosition(Math.Max(0, f.Pc - 1));
        return (f.T.Script.SourceName, line, col);
    }

    /// <summary>Resets the stack after a fault or termination (frames abandoned by a CLR exception).</summary>
    internal void ResetStack()
    {
        for (int i = 0; i < _chunks.Count; i++) Array.Clear(_chunks[i]);
        _hwm = 0;
        _chunkHwm = 0;
        for (int i = 0; i < _frames.Length; i++)
        {
            // an abandoned coroutine frame may still be held by its generator: do not let it retain the pooled frame
            var f = _frames[i];
            if (f == null) break;
            f.Displaced = null;
            _frames[i] = null;
        }
        _chunkIndex = 0;
        _chunk = _chunks[0];
        _top = 0;
        Realm = _homeRealm;
        Sp = 0;
        _nativeDepth = 0;
    }

    /// <summary>Drops oversized register chunks so a single deep request does not keep memory alive.</summary>
    internal void TrimBuffers()
    {
        if (_chunks.Count > 4) _chunks.RemoveRange(4, _chunks.Count - 4);
    }
}

/// <summary>Process-wide switch for <c>JAUNT_POISON_REGISTERS=1</c>; kept out of <see cref="Vm"/> so that class has no environment-dependent static initializer.</summary>
internal static class RegisterPoison
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("JAUNT_POISON_REGISTERS") == "1";
}
