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
}

internal enum SuspendKind : byte { None, Yield, YieldRaw, Await, Initial }

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
    /// <summary>Realm that was current when this frame was entered (restored on exit).</summary>
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
        _interrupt = 0;
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
        _interrupt = 0;
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

    public void RequestInterrupt() => _interrupt = 1;

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

    private JsValue[] AllocRegs(int count, out int baseIndex, out int chunkIndex)
    {
        var chunk = _chunks[_chunkIndex];
        if (_top + count > chunk.Length)
        {
            int prevLen = chunk.Length;
            if (_top == 0 && _chunkIndex == 0 && prevLen < MaxChunkSize)
            {
                // nothing live in the first chunk yet: just grow it
                _chunks[0] = chunk = new JsValue[Math.Max(prevLen == 0 ? InitialChunkSize : Math.Min(prevLen * 4, MaxChunkSize), count)];
                baseIndex = 0;
                chunkIndex = 0;
                _top = count;
                return chunk;
            }
            _chunkIndex++;
            int size = Math.Max(Math.Min(prevLen * 2, MaxChunkSize), count);
            if (_chunkIndex == _chunks.Count) _chunks.Add(new JsValue[size]);
            else if (_chunks[_chunkIndex].Length < count) _chunks[_chunkIndex] = new JsValue[size];
            chunk = _chunks[_chunkIndex];
            _top = 0;
        }
        baseIndex = _top;
        chunkIndex = _chunkIndex;
        _top += count;
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
            f.Regs = AllocRegs(t.RegisterCount, out f.Base, out f.ChunkIndex);
        }
        int n = Math.Min(args.Length, t.ParamCount);
        args.Slice(0, n).CopyTo(f.Regs.AsSpan(f.Base, n));
        f.Args = (t.Flags & FunctionFlags.NeedsArgsArray) != 0 ? args.ToArray() : null;
        SetupFrame(f, c, thisValue, newTarget, flags);
        return f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetupFrame(Frame f, JsClosure c, JsValue thisValue, JsValue newTarget, FrameFlags flags)
    {
        var t = c.Template;
        f.T = t;
        f.Code = t.Code;
        f.Pc = 0;
        f.Env = c.Env;
        f.EnvDepth = 0;
        f.Callee = c;
        f.Feedback = c.Feedback;
        f.Flags = flags;
        f.Caller = Current;
        f.Suspend = SuspendKind.None;
        f.ThisBox = c.ThisBox;
        f.CallerRealm = Realm;
        Realm = c.Realm;
        if ((t.Flags & FunctionFlags.Arrow) != 0)
        {
            f.This = c.BoundThis;
            f.NewTarget = c.BoundNewTarget;
        }
        else
        {
            f.NewTarget = newTarget;
            if ((t.Flags & FunctionFlags.Strict) == 0 && thisValue.IsNotObject)
            {
                if (thisValue.IsNullOrUndefined) thisValue = JsValue.FromObject(Realm.GlobalObject);
                else if (thisValue.IsNotEmpty) thisValue = JsValue.FromObject(Operations.ToObject(Realm, thisValue));
            }
            f.This = thisValue;
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
        Realm = f.CallerRealm;
        if ((f.Flags & FrameFlags.OwnsRegs) == 0)
        {
            Array.Clear(f.Regs, f.Base, f.T.RegisterCount);
            _chunkIndex = f.ChunkIndex;
            _top = f.Base;
            f.Args = null;
            f.Env = null;
            f.Callee = null!;
            f.Caller = null;
            f.This = default;
            f.NewTarget = default;
        }
    }

    /// <summary>Detaches a suspended coroutine frame from the active stack (registers are private).</summary>
    internal void DetachFrame(Frame f)
    {
        Depth--;
        Current = f.Caller;
        Realm = f.CallerRealm;
        f.Caller = null;
    }

    internal void AttachFrame(Frame f)
    {
        if (Depth + 1 > MaxDepth) StackOverflowFrame();
        f.Caller = Current;
        f.CallerRealm = Realm;
        Realm = f.Callee.Realm;
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

    private void EnsureStack()
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
            throw new JsTerminationException(TerminationKind.StackDepth, "Native recursion is too deep to continue safely.", Depth, MaxDepth);
    }

    /// <summary>Applies [[Construct]] return semantics.</summary>
    internal JsValue ConstructResult(Frame f, JsValue value)
    {
        if (value.IsObject) return value;
        if (f.This.IsEmpty && f.ThisBox != null) f.This = f.ThisBox.Value;
        if (f.Callee.IsDerived)
        {
            // thrown after the callee context is removed: the caller's realm
            if (!value.IsUndefined) throw JsErrors.TypeError(f.CallerRealm, "Derived constructors may only return object or undefined");
            if (f.This.IsEmpty) throw JsErrors.ReferenceError(f.CallerRealm, "Must call super constructor in derived class before accessing 'this' or returning from derived constructor");
        }
        return f.This;
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

    // ------------------------------------------------------------------ diagnostics

    public string CaptureStackTrace(int maxFrames = 32)
    {
        var sb = new StringBuilder();
        int n = 0;
        for (var f = Current; f != null && n < maxFrames; f = f.Caller, n++)
        {
            var (line, col) = f.T.GetPosition(Math.Max(0, f.Pc - 1));
            string name = f.T.Name;
            if ((f.T.Flags & (FunctionFlags.ScriptBody | FunctionFlags.ModuleBody)) != 0) name = "";
            sb.Append("\n    at ");
            if (name.Length > 0) sb.Append(name).Append(" (");
            sb.Append(f.T.Script.SourceName).Append(':').Append(line).Append(':').Append(col);
            if (name.Length > 0) sb.Append(')');
        }
        return sb.ToString();
    }

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
        for (int i = 0; i < _framePool.Length; i++) _framePool[i] = null;
        _chunkIndex = 0;
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
