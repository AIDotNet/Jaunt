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
    public Frame? Caller;
    public int RetIndex;
    public FrameFlags Flags;
    public int ChunkIndex;
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
    private Frame?[] _framePool = new Frame?[16];
    public Frame? Current;
    public int Depth;

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
        var saved = t_current;
        if (ReferenceEquals(saved, this)) return RunLoop(entry);   // nested native→JS call: already current
        t_current = this;
        try { return RunLoop(entry); }
        finally { t_current = saved; }
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
        if (++Depth > MaxDepth) StackOverflow();
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) StackOverflow();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExitNative() => Depth--;

    /// <summary>Guards native (CLR-stack) recursion: nested interpreter runs, bound/proxy chains, recursive builtins.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnsureNativeStack()
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) NativeStackOverflow();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void NativeStackOverflow() =>
        throw new JsTerminationException(TerminationKind.StackDepth, "Native recursion is too deep to continue safely.", Depth, MaxDepth);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void StackOverflow()
    {
        Depth--;
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
    /// an uninitialized register shows up in test results (JAUNT_POISON_REGISTERS=1).
    /// </summary>
    internal static readonly bool PoisonRegisters = Environment.GetEnvironmentVariable("JAUNT_POISON_REGISTERS") == "1";
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
            _chunks[0] = _chunk = chunk = new JsValue[Math.Max(prevLen == 0 ? InitialChunkSize : Math.Min(prevLen * 4, MaxChunkSize), count)];
            baseIndex = 0;
            chunkIndex = 0;
            _top = count;
            _hwm = count;   // the previous chunk 0 is gone
            return chunk;
        }
        _chunkIndex++;
        int size = Math.Max(Math.Min(prevLen * 2, MaxChunkSize), count);
        if (_chunkIndex == _chunks.Count) _chunks.Add(new JsValue[size]);
        else if (_chunks[_chunkIndex].Length < count) _chunks[_chunkIndex] = new JsValue[size];
        _chunk = chunk = _chunks[_chunkIndex];
        _top = 0;
        baseIndex = 0;
        chunkIndex = _chunkIndex;
        _top = count;
        if (_chunkIndex > _chunkHwm) _chunkHwm = _chunkIndex;
        return chunk;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Frame RentFrame()
    {
        int d = Depth;
        if (d >= _framePool.Length) Array.Resize(ref _framePool, _framePool.Length * 2);
        var f = _framePool[d];
        if (f == null) _framePool[d] = f = new Frame();
        return f;
    }

    /// <summary>Pushes a frame for a closure call; arguments are copied into the new register window.</summary>
    internal Frame PushFrame(JsClosure c, JsValue thisValue, ReadOnlySpan<JsValue> args, JsValue newTarget, FrameFlags flags)
    {
        if (Depth + 1 > MaxDepth) StackOverflowFrame();
        var t = c.Template;
        Frame f;
        if ((flags & FrameFlags.OwnsRegs) != 0)
        {
            f = new Frame();
            f.Regs = new JsValue[t.RegisterCount];
            f.Base = 0;
            f.ChunkIndex = -1;
        }
        else
        {
            f = RentFrame();
            var regs = AllocRegs(t.RegisterCount, out f.Base, out f.ChunkIndex);
            if (!ReferenceEquals(f.Regs, regs)) f.Regs = regs;   // a pooled frame usually sits in the same chunk as last time
        }
        int n = Math.Min(args.Length, t.ParamCount);
        {
            var regs = f.Regs; int b = f.Base;
            if (n <= 4)
            {
                // element stores beat the bulk copy helper for the usual one to four arguments
                for (int i = 0; i < n; i++) regs[b + i] = args[i];
            }
            else args.Slice(0, n).CopyTo(regs.AsSpan(b, n));
            // Registers are not cleared when a frame is popped (the pooled stack is cleared when the root execution
            // ends); the callee's function-scope bindings and missing parameters must start as undefined, every other
            // register is written before it is read (see FunctionTemplate.InitUndefEnd; PoisonRegisters checks it).
            int initEnd = t.InitUndefEnd < 0 ? t.RegisterCount : t.InitUndefEnd;
            for (int i = n; i < initEnd; i++) regs[b + i] = default;
            if (PoisonRegisters) for (int i = initEnd; i < t.RegisterCount; i++) regs[b + i] = s_poison;
        }
        if ((t.Flags & FunctionFlags.NeedsArgsArray) != 0) f.Args = args.ToArray();   // otherwise still null from PopFrame
        SetupFrame(f, c, thisValue, newTarget, flags);
        return f;
    }

    /// <summary>
    /// <see cref="PushFrame"/> for the interpreter's Call/CallMethod: <c>this</c> and the arguments are read straight from
    /// the caller's register window (no span and no 16-byte value copies cross the call), new.target is undefined and
    /// the frame has no flags.
    /// </summary>
    internal Frame PushCallFrame(JsClosure c, JsValue[] callerRegs, int thisIndex, int argStart, int argc)
    {
        if (Depth + 1 > MaxDepth) StackOverflowFrame();
        var t = c.Template;
        var f = RentFrame();
        var regs = AllocRegs(t.RegisterCount, out f.Base, out f.ChunkIndex);
        if (!ReferenceEquals(f.Regs, regs)) f.Regs = regs;
        int b = f.Base;
        int n = argc < t.ParamCount ? argc : t.ParamCount;
        if (n <= 4) { for (int i = 0; i < n; i++) regs[b + i] = callerRegs[argStart + i]; }
        else Array.Copy(callerRegs, argStart, regs, b, n);
        int initEnd = t.InitUndefEnd < 0 ? t.RegisterCount : t.InitUndefEnd;
        for (int i = n; i < initEnd; i++) regs[b + i] = default;
        if (PoisonRegisters) for (int i = initEnd; i < t.RegisterCount; i++) regs[b + i] = s_poison;
        if ((t.Flags & FunctionFlags.NeedsArgsArray) != 0) f.Args = new ReadOnlySpan<JsValue>(callerRegs, argStart, argc).ToArray();
        SetupFrame(f, c, thisIndex < 0 ? default : callerRegs[thisIndex], default, FrameFlags.None);
        return f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetupFrame(Frame f, JsClosure c, JsValue thisValue, JsValue newTarget, FrameFlags flags)
    {
        // Reference stores into the (heap-allocated, pooled) frame each cost a GC write barrier, so a field is stored
        // only when its value changes. A pooled frame keeps its callee, environment and this between calls (recursion
        // and callbacks in loops re-enter the same closure at the same depth); ReleasePooledFrames drops them when the
        // root execution ends. PopFrame clears Args, Caller and NewTarget, and a freshly allocated frame is clear.
        var t = c.Template;
        if (!ReferenceEquals(f.Callee, c))
        {
            f.Callee = c;
            f.T = t;
            f.Code = t.Code;
            f.Feedback = c.Feedback;
        }
        f.Pc = 0;
        var env = c.Env;
        if (!ReferenceEquals(f.Env, env)) f.Env = env;
        f.EnvDepth = 0;
        f.Caller = Current;
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
        Depth++;
        Current = f;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void StackOverflowFrame() =>
        throw new JsTerminationException(TerminationKind.StackDepth, $"Maximum call stack depth ({MaxDepth}) exceeded.", Depth + 1, MaxDepth);

    /// <summary>Pops a frame, clearing its registers so the pooled stack does not retain object graphs.</summary>
    internal void PopFrame(Frame f)
    {
        Depth--;
        Current = f.Caller;
        var flags = f.Flags;
        if ((flags & FrameFlags.RealmSwitch) != 0) Realm = f.CallerRealm;
        if ((flags & FrameFlags.OwnsRegs) == 0)
        {
            if (_chunkIndex != f.ChunkIndex) { _chunkIndex = f.ChunkIndex; _chunk = _chunks[_chunkIndex]; }
            _top = f.Base;
            f.Args = null;
            f.Caller = null;
            f.NewTarget = default;
        }
    }

    /// <summary>
    /// Drops the references the pooled frames keep between calls (see <see cref="SetupFrame"/>), so an idle engine does not
    /// retain the closures, environments and receivers of its last execution.
    /// </summary>
    internal void ReleasePooledFrames()
    {
        ClearRegisterStack();
        if (_sortScratch != null && _sortScratch.Length > 1 << 12) _sortScratch = null;   // an idle engine keeps at most 64 KB of sort scratch
        var pool = _framePool;
        for (int i = 0; i < pool.Length; i++)
        {
            var f = pool[i];
            if (f == null) break;
            f.Callee = null!;
            f.T = null!;
            f.Code = null!;
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
        Depth--;
        Current = f.Caller;
        if ((f.Flags & FrameFlags.RealmSwitch) != 0) Realm = f.CallerRealm;
        f.Caller = null;
    }

    internal void AttachFrame(Frame f)
    {
        if (Depth + 1 > MaxDepth) StackOverflowFrame();
        f.Caller = Current;
        var realm = Realm;
        var calleeRealm = f.Callee.Realm;
        if (!ReferenceEquals(realm, calleeRealm))
        {
            f.CallerRealm = realm;
            Realm = calleeRealm;
            f.Flags |= FrameFlags.RealmSwitch;
        }
        else f.Flags &= ~FrameFlags.RealmSwitch;
        Depth++;
        Current = f;
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
            var obj = new JsObject(Realm, proto);
            thisValue = JsValue.FromObject(obj);
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

    /// <summary>A merge buffer of at least <paramref name="n"/> elements; taken out of the Vm while in use (re-entrant sorts allocate their own).</summary>
    internal JsValue[] TakeSortScratch(int n)
    {
        var s = _sortScratch;
        _sortScratch = null;
        if (s == null || s.Length < n) s = new JsValue[Math.Max(n, 64)];
        return s;
    }

    /// <summary>Returns the buffer cleared of references; buffers over 64K elements (1 MB) are not retained.</summary>
    internal void ReturnSortScratch(JsValue[] s, int used)
    {
        Array.Clear(s, 0, used);
        if (s.Length <= 1 << 16) _sortScratch = s;
    }

    private void EnsureStack()
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
            throw new JsTerminationException(TerminationKind.StackDepth, "Native recursion is too deep to continue safely.", Depth, MaxDepth);
    }

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
        return true;
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
            var p = new JsObject(realm, realm.ObjectPrototype);
            p.AddOwnProperty("constructor", JsValue.FromObject(c), PropFlags.Hidden);
            c.AddOwnProperty("prototype", JsValue.FromObject(p), PropFlags.Writable);
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
        int n = 0;
        for (var f = Current; f != null && n < maxFrames; f = f.Caller) n++;
        if (n == 0) return Array.Empty<StackFrameRecord>();
        var frames = new StackFrameRecord[n];
        n = 0;
        for (var f = Current; f != null && n < frames.Length; f = f.Caller) frames[n++] = new StackFrameRecord(f.T, Math.Max(0, f.Pc - 1));
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
        for (int i = 0; i < _framePool.Length; i++) _framePool[i] = null;
        _chunkIndex = 0;
        _chunk = _chunks[0];
        _top = 0;
        Current = null;
        Realm = _homeRealm;
        Depth = 0;
    }

    /// <summary>Drops oversized register chunks so a single deep request does not keep memory alive.</summary>
    internal void TrimBuffers()
    {
        if (_chunks.Count > 4) _chunks.RemoveRange(4, _chunks.Count - 4);
    }
}
