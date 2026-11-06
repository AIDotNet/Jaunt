using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Jaunt.Compiler;
using Jaunt.Runtime;

namespace Jaunt;

/// <summary>
/// A stateful JavaScript execution instance: one realm (globals, intrinsics, module instances), a VM and a job
/// queue. An engine may be used from different threads sequentially, but only by one logical executor at a time;
/// concurrent entry fails fast with <see cref="InvalidOperationException"/>. Run independent engines for parallelism.
/// </summary>
public sealed partial class JsEngine : IDisposable
{
    private const int StateReady = 0, StateExecuting = 1, StateFaulted = 2, StateDisposed = 3;

    internal readonly Realm Realm;
    internal readonly Vm Vm;
    private readonly JsEngineOptions _options;
    private int _state;
    private RootState? _root;
    private long _rootCounter;
    private long _executions;
    private long _unhandled;
    private long _lastWork;
    private Exception? _faultCause;
    private readonly JsValue[] _args1 = new JsValue[1];
    private readonly JsValue[] _args2 = new JsValue[2];
    private readonly JsValue[] _args3 = new JsValue[3];

    /// <summary>Creates an engine with default options.</summary>
    public JsEngine() : this(null) { }

    /// <summary>Creates an engine.</summary>
    public JsEngine(JsEngineOptions? options)
    {
        _options = options ?? JsEngineOptions.Default;
        Realm = new Realm(_options.Profile == JsProfile.Hardened);
        Realm.Engine = this;
        Vm = Realm.Vm;
        var limits = _options.Limits;
        Realm.MaxTrackedBytes = limits.MaxTrackedBytes <= 0 ? long.MaxValue : limits.MaxTrackedBytes;
        Realm.MaxStringLength = limits.MaxStringLength;
        Realm.TimeZone = _options.TimeZone;
        if (_options.Clock != null) Realm.Clock = _options.Clock;
        Vm.MaxPendingJobs = limits.MaxPendingJobs;
        Vm.MaxDepth = limits.MaxStackDepth;
        Vm.CanBlock = _options.CanBlock;
    }

    /// <summary>The options this engine was created with.</summary>
    public JsEngineOptions Options => _options;

    /// <summary>
    /// Version ("major.minor") of the platform ICU library that backs <c>Intl</c> and Temporal's non-ISO calendars, or
    /// null when no ICU is available (then the <c>Intl</c> global is not installed).
    /// </summary>
    public static string? IcuVersion => Icu.Available ? Icu.Version : null;

    /// <summary>True after a termination or internal error; the engine must be discarded.</summary>
    public bool IsFaulted => Volatile.Read(ref _state) == StateFaulted;

    /// <summary>True when an execution is active.</summary>
    public bool IsExecuting => Volatile.Read(ref _state) == StateExecuting;

    // ------------------------------------------------------------------ root execution management

    internal sealed class RootState
    {
        public long Id;
        public JsExecutionMode Mode;
        public CancellationToken Token;
        public long MaxOutputBytes;
        public long Deadline = long.MaxValue;
        public readonly List<HostOperation> PendingOps = new();
        public CancellationTokenSource? OpsCts;
        public CancellationTokenRegistration TokenRegistration;
        public bool Closed;
    }

    internal RootState? CurrentRoot => _root;

    internal RootState BeginRoot(JsExecutionOptions? execOptions, CancellationToken token, JsExecutionMode mode)
    {
        int prev = Interlocked.CompareExchange(ref _state, StateExecuting, StateReady);
        if (prev != StateReady)
        {
            throw prev switch
            {
                StateExecuting => new InvalidOperationException("The engine is already executing; concurrent use of one JsEngine is not allowed (and re-entrant engine calls from host callbacks are rejected)."),
                StateFaulted => new JsEngineFaultedException("Faulted", "The engine is faulted and must be disposed.", _faultCause),
                _ => new ObjectDisposedException(nameof(JsEngine)),
            };
        }
        var limits = _options.Limits;
        TimeSpan? timeout = limits.Timeout;
        if (execOptions?.Timeout is { } et && (timeout == null || et < timeout)) timeout = et;
        long maxWork = limits.MaxInstructions;
        if (execOptions != null && execOptions.MaxInstructions > 0 && (maxWork <= 0 || execOptions.MaxInstructions < maxWork)) maxWork = execOptions.MaxInstructions;
        long maxOut = limits.MaxOutputBytes;
        if (execOptions != null && execOptions.MaxOutputBytes > 0 && execOptions.MaxOutputBytes < maxOut) maxOut = execOptions.MaxOutputBytes;
        var root = new RootState
        {
            Id = ++_rootCounter,
            Mode = mode,
            Token = token,
            MaxOutputBytes = maxOut,
            Deadline = timeout is { } t ? Stopwatch.GetTimestamp() + (long)(t.TotalSeconds * Stopwatch.Frequency) : long.MaxValue,
        };
        _root = root;
        Vm.BeginBudget(maxWork, timeout, token, limits.MaxStackDepth);
        return root;
    }

    /// <summary>Final checkpoint of a root: drain ready jobs and report unhandled rejections.</summary>
    internal void CheckpointRoot(RootState root)
    {
        Vm.RunJobs();
        ProcessCompletions(root);
        Vm.RunJobs();
        ReportRejections();
    }

    internal void CloseRoot(RootState root, bool requireNoPendingWork)
    {
        if (root.Closed) return;
        if (requireNoPendingWork)
        {
            bool pending = false;
            foreach (var op in root.PendingOps) if (!op.Completed) { pending = true; break; }
            if (pending)
            {
                CancelPendingOps(root);
                root.Closed = true;
                throw Fault(new JsTerminationException(TerminationKind.UnjoinedAsyncWork,
                    "The execution completed while asynchronous host operations were still pending (UnjoinedAsyncWork)."));
            }
        }
        root.Closed = true;
    }

    internal void EndRoot(RootState root)
    {
        root.Closed = true;
        Realm.ClearKeptObjects();
        root.TokenRegistration.Dispose();
        if (root.OpsCts != null)
        {
            bool pending = false;
            foreach (var op in root.PendingOps) if (!op.Completed) { pending = true; break; }
            if (pending) CancelPendingOps(root);
            else root.OpsCts.Dispose();
        }
        _lastWork = Vm.WorkUsed;
        Vm.EndBudget();
        if (ReferenceEquals(_root, root)) _root = null;
        _executions++;
        if (Vm.Depth != 0 || Vm.Current != null) Vm.ResetStack();
        Interlocked.CompareExchange(ref _state, StateReady, StateExecuting);
    }

    private static void CancelPendingOps(RootState root)
    {
        try { root.OpsCts?.Cancel(); } catch (AggregateException) { }
    }

    private void ReportRejections()
    {
        var list = Realm.UnhandledRejections;
        if (list.Count == 0) return;
        var copy = list.ToArray();
        list.Clear();
        foreach (var p in copy)
        {
            if (p.IsHandled) continue;
            _unhandled++;
            if (_options.UnhandledRejection != null)
            {
                string reason;
                try { reason = DescribeValue(p.Result); }
                catch (JsThrowException) { reason = "<unprintable>"; }
                _options.UnhandledRejection(new JsUnhandledRejection(reason));
            }
        }
    }

    internal string DescribeValue(JsValue v)
    {
        if (v.IsObject && v.Ref is JsError e) return e.Name + ": " + e.MessageText;
        if (v.IsObject) return "[object " + v.AsObjectUnchecked.ClassName + "]";
        return v.ToString();
    }

    /// <summary>Converts an exception that escaped JS execution into the public exception to throw.</summary>
    internal Exception MapException(Exception ex, RootState root)
    {
        switch (ex)
        {
            case JsThrowException jt:
            {
                // ordinary JS exception: run the root checkpoint before propagating
                try { CheckpointRoot(root); }
                catch (JsThrowException) { }
                catch (JsTerminationException te) { return Fault(te); }
                if (Vm.Depth != 0) Vm.ResetStack();
                return JsException.From(Realm, jt.Value);
            }
            case JsTerminationException te when te.Kind == TerminationKind.AsyncRequired:
                Vm.ResetStack();
                return new JsAsyncRequiredException(te.Message);
            case JsTerminationException te:
                return Fault(te);
            case JsCompileException:
            case JsConversionException:
            case JsAsyncRequiredException:
            case JsEngineFaultedException:
            case JsLimitExceededException:
            case ArgumentException:
            case ObjectDisposedException:
                if (Vm.Depth != 0) Vm.ResetStack();
                return ex;
            case InsufficientExecutionStackException:
                return Fault(new JsTerminationException(TerminationKind.StackDepth, "Native recursion is too deep to continue safely."));
            case OperationCanceledException oce when root.Token.IsCancellationRequested:
                Fault(new JsTerminationException(TerminationKind.Cancelled, "Execution was cancelled."));
                return new OperationCanceledException(oce.Message, oce, root.Token);
            default:
                return Fault(ex);
        }
    }

    internal Exception Fault(Exception cause)
    {
        _faultCause = cause;
        Volatile.Write(ref _state, StateFaulted);
        Vm.ResetStack();
        Vm.ClearJobs();
        var root = _root;
        if (root != null) CancelPendingOps(root);
        if (cause is JsTerminationException te)
        {
            return te.Kind switch
            {
                TerminationKind.Cancelled or TerminationKind.Interrupted => new OperationCanceledException(te.Message, te, root?.Token ?? default),
                TerminationKind.Timeout => new JsLimitExceededException(JsLimitKind.Timeout, te.Message, te.Observed, te.Limit),
                TerminationKind.Instructions => new JsLimitExceededException(JsLimitKind.Instructions, te.Message, te.Observed, te.Limit),
                TerminationKind.StackDepth => new JsLimitExceededException(JsLimitKind.StackDepth, te.Message, te.Observed, te.Limit),
                TerminationKind.TrackedBytes => new JsLimitExceededException(JsLimitKind.TrackedBytes, te.Message, te.Observed, te.Limit),
                TerminationKind.OutputBytes => new JsLimitExceededException(JsLimitKind.OutputBytes, te.Message, te.Observed, te.Limit),
                TerminationKind.PendingJobs => new JsLimitExceededException(JsLimitKind.PendingJobs, te.Message, te.Observed, te.Limit),
                TerminationKind.UnjoinedAsyncWork => new JsEngineFaultedException("UnjoinedAsyncWork", te.Message, te),
                _ => new JsEngineFaultedException("InternalError", te.Message, te),
            };
        }
        if (cause is JsLimitExceededException or JsEngineFaultedException or OperationCanceledException) return cause;
        return new JsEngineFaultedException("InternalError", "Internal engine error: " + cause.Message, cause);
    }

    /// <summary>
    /// Requests that the currently active execution stops at its next checkpoint. Has no effect (and leaves no
    /// sticky state) when the engine is idle.
    /// </summary>
    public void Interrupt()
    {
        // the state check and the request are atomic with respect to the end of the execution (which clears requests)
        Vm.RequestInterrupt(() => Volatile.Read(ref _state) == StateExecuting);
    }

    // ------------------------------------------------------------------ ownership

    internal void EnsureUsable()
    {
        int s = Volatile.Read(ref _state);
        if (s == StateDisposed) throw new ObjectDisposedException(nameof(JsEngine));
        if (s == StateFaulted) throw new JsEngineFaultedException("Faulted", "The engine is faulted and must be disposed.", _faultCause);
    }

    /// <summary>
    /// Rejects an object of another engine passed to a host API of <paramref name="realm"/>'s engine. Every host entry
    /// point that can put a value into this engine's object graph checks it (research doc §3.3: a partial foreign check is
    /// not one the host can rely on).
    /// </summary>
    internal static void CheckOwned(Realm realm, in JsValue v, string paramName)
    {
        if (v.IsEmpty) throw new ArgumentException("Invalid JS value.", paramName);
        if (v.IsObject && !ReferenceEquals(((JsObject)v.Ref!).Realm.Vm, realm.Vm))
            throw new ArgumentException("The value belongs to another engine (or a disposed one).", paramName);
    }

    internal void EnsureOwned(in JsValue v, string paramName)
    {
        if (v.IsEmpty) throw new ArgumentException("Invalid JS value.", paramName);
        if (v.IsObject && !ReferenceEquals(((JsObject)v.Ref!).Realm.Vm, Vm))
            throw new ArgumentException("The value belongs to another engine (or a disposed one).", paramName);
        if (v.IsObject && Volatile.Read(ref _state) == StateDisposed)
            throw new ObjectDisposedException(nameof(JsEngine));
    }

    // ------------------------------------------------------------------ script execution

    internal JsValue RunScriptTemplate(ScriptTemplate t, bool oneShot, Realm? realm = null)
    {
        realm ??= Realm;
        var saved = Vm.Realm;
        Vm.Realm = realm;
        JsClosure closure;
        try
        {
            realm.InstantiateScript(t);
            var fb = oneShot ? new Feedback(t.Body) : realm.FeedbackFor(t);
            closure = Vm.CreateClosure(t.Body, null, fb, null);
        }
        finally { Vm.Realm = saved; }
        var frame = Vm.PushFrame(closure, JsValue.FromObject(realm.GlobalObject), ReadOnlySpan<JsValue>.Empty, JsValue.Undefined, FrameFlags.None);
        return Vm.Run(frame);
    }

    /// <summary>Compiles and evaluates a script; returns its completion value. Pending promises are not awaited.</summary>
    public JsValue Evaluate(string source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var root = BeginRoot(null, cancellationToken, JsExecutionMode.Sync);
        try
        {
            ScriptTemplate t = JsCompiler.Compile(source, _options.CompileOptions, isModule: false, cancellationToken, vm: Vm);
            var r = RunScriptTemplate(t, oneShot: true);
            CheckpointRoot(root);
            CloseRoot(root, true);
            return r;
        }
        catch (Exception ex) when (ex is not JsCompileException) { throw MapException(ex, root); }
        finally { EndRoot(root); }
    }

    /// <summary>Evaluates a precompiled script in this engine.</summary>
    public JsValue Evaluate(JsScript script, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        var root = BeginRoot(null, cancellationToken, JsExecutionMode.Sync);
        try
        {
            var r = RunScriptTemplate(script.Template, oneShot: false);
            CheckpointRoot(root);
            CloseRoot(root, true);
            return r;
        }
        catch (Exception ex) { throw MapException(ex, root); }
        finally { EndRoot(root); }
    }

    /// <summary>Evaluates a script without returning a value.</summary>
    public void Execute(string source, CancellationToken cancellationToken = default) => Evaluate(source, cancellationToken);

    /// <summary>Starts an explicit root execution scope that shares one budget across several operations.</summary>
    public JsExecution BeginExecution(JsExecutionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var mode = options?.Mode ?? JsExecutionMode.Sync;
        var root = BeginRoot(options, cancellationToken, mode);
        return new JsExecution(this, root);
    }

    // ------------------------------------------------------------------ globals

    /// <summary>Defines or updates a global data property (not a lexical binding).</summary>
    public void SetValue(string name, JsValue value)
    {
        ArgumentNullException.ThrowIfNull(name);
        EnsureOwned(value, nameof(value));
        WithExclusive(() =>
        {
            Realm.EnsureGlobal(name);
            var g = Realm.GlobalObject;
            int idx = g.FindOwn(name, out var flags);
            if (idx >= 0)
            {
                if ((flags & PropFlags.Accessor) != 0 || (flags & PropFlags.Writable) == 0)
                    throw new InvalidOperationException($"Global '{name}' is an accessor or read-only property and cannot be replaced.");
                g.WriteSlot(idx, value);
                return;
            }
            g.CreateDataProperty(name, value);
            Realm.GlobalVersion++;
        });
    }

    /// <summary>Defines a global number.</summary>
    public void SetValue(string name, double value) => SetValue(name, JsValue.From(value));
    /// <summary>Defines a global number.</summary>
    public void SetValue(string name, int value) => SetValue(name, JsValue.From(value));
    /// <summary>Defines a global boolean.</summary>
    public void SetValue(string name, bool value) => SetValue(name, JsValue.From(value));
    /// <summary>Defines a global string (null becomes JS null).</summary>
    public void SetValue(string name, string? value) => SetValue(name, JsValue.From(value));

    /// <summary>Reads a global property without invoking getters (returns undefined for accessors).</summary>
    public JsValue GetGlobal(string name)
    {
        EnsureUsable();
        Realm.EnsureGlobal(name);
        if (Realm.GlobalLex.TryGetValue(name, out var cell)) return cell.Value.IsEmpty ? JsValue.Undefined : cell.Value;
        int idx = Realm.GlobalObject.FindOwn(name, out var flags);
        if (idx < 0 || (flags & PropFlags.Accessor) != 0) return JsValue.Undefined;
        return Realm.GlobalObject.ReadSlot(idx);
    }

    internal void WithExclusive(Action action)
    {
        int prev = Interlocked.CompareExchange(ref _state, StateExecuting, StateReady);
        if (prev != StateReady)
        {
            if (prev == StateExecuting) throw new InvalidOperationException("The engine is executing; it cannot be modified concurrently.");
            EnsureUsable();
        }
        try { action(); }
        finally { Interlocked.CompareExchange(ref _state, StateReady, StateExecuting); }
    }

    /// <summary>Returns a handle to a global function (the current function object; not a live binding).</summary>
    public JsFunction GetFunction(string globalName)
    {
        var v = GetGlobal(globalName);
        if (!v.IsFunction) throw new InvalidOperationException($"Global '{globalName}' is not a function.");
        return new JsFunction(this, (JsFunctionObject)v.Ref!);
    }

    // ------------------------------------------------------------------ invocation

    /// <summary>Invokes a function with <c>this</c> = undefined.</summary>
    public JsValue Invoke(JsFunction function) => InvokeCore(function, JsValue.Undefined, ReadOnlySpan<JsValue>.Empty);

    /// <summary>Invokes a function with one argument.</summary>
    public JsValue Invoke(JsFunction function, JsValue arg0)
    {
        EnsureOwned(arg0, nameof(arg0));
        _args1[0] = arg0;
        try { return InvokeCore(function, JsValue.Undefined, _args1); }
        finally { _args1[0] = default; }
    }

    /// <summary>Invokes a function with two arguments.</summary>
    public JsValue Invoke(JsFunction function, JsValue arg0, JsValue arg1)
    {
        EnsureOwned(arg0, nameof(arg0));
        EnsureOwned(arg1, nameof(arg1));
        _args2[0] = arg0;
        _args2[1] = arg1;
        try { return InvokeCore(function, JsValue.Undefined, _args2); }
        finally { _args2[0] = default; _args2[1] = default; }
    }

    /// <summary>Invokes a function with three arguments.</summary>
    public JsValue Invoke(JsFunction function, JsValue arg0, JsValue arg1, JsValue arg2)
    {
        EnsureOwned(arg0, nameof(arg0));
        EnsureOwned(arg1, nameof(arg1));
        EnsureOwned(arg2, nameof(arg2));
        _args3[0] = arg0;
        _args3[1] = arg1;
        _args3[2] = arg2;
        try { return InvokeCore(function, JsValue.Undefined, _args3); }
        finally { Array.Clear(_args3); }
    }

    /// <summary>Invokes a function with any number of arguments.</summary>
    public JsValue Invoke(JsFunction function, ReadOnlySpan<JsValue> args)
    {
        foreach (var a in args) EnsureOwned(a, nameof(args));
        return InvokeCore(function, JsValue.Undefined, args);
    }

    /// <summary>Invokes a function with an explicit <c>this</c> receiver.</summary>
    public JsValue InvokeWithThis(JsFunction function, JsValue thisValue, ReadOnlySpan<JsValue> args)
    {
        EnsureOwned(thisValue, nameof(thisValue));
        foreach (var a in args) EnsureOwned(a, nameof(args));
        return InvokeCore(function, thisValue, args);
    }

    /// <summary>Reads the live export binding and invokes it.</summary>
    public JsValue Invoke(JsExport export, params JsValue[] args)
    {
        ArgumentNullException.ThrowIfNull(export);
        foreach (var a in args) EnsureOwned(a, nameof(args));
        export.Check(this);
        var root = BeginRoot(null, default, JsExecutionMode.Sync);
        try
        {
            var fv = export.Read();
            var r = Vm.CallValue(fv, JsValue.Undefined, args);
            CheckpointRoot(root);
            CloseRoot(root, true);
            return r;
        }
        catch (Exception ex) { throw MapException(ex, root); }
        finally { EndRoot(root); }
    }

    private JsValue InvokeCore(JsFunction function, JsValue thisValue, ReadOnlySpan<JsValue> args)
    {
        ArgumentNullException.ThrowIfNull(function);
        function.Check(this);
        var root = BeginRoot(null, default, JsExecutionMode.Sync);
        try
        {
            var r = function.Target.Call(thisValue, args);
            CheckpointRoot(root);
            CloseRoot(root, true);
            return r;
        }
        catch (Exception ex) { throw MapException(ex, root); }
        finally { EndRoot(root); }
    }

    // ------------------------------------------------------------------ statistics and lifetime

    /// <summary>Returns a snapshot of engine counters.</summary>
    public JsEngineStatistics GetStatistics() => new()
    {
        TrackedBytes = Realm.TrackedBytes,
        LastWorkUnits = _lastWork,
        Executions = _executions,
        UnhandledRejections = _unhandled,
        Modules = _modules.Count,
    };

    /// <summary>
    /// Releases the engine. Never executes JS. Disposing while an asynchronous execution is in flight is not
    /// allowed; cancel it first.
    /// </summary>
    public void Dispose()
    {
        int prev = Interlocked.Exchange(ref _state, StateDisposed);
        if (prev == StateDisposed) return;
        if (prev == StateExecuting && _root != null)
        {
            CancelPendingOps(_root);
        }
        Vm.ClearJobs();
        Vm.ResetStack();
        _modules.Clear();
        Realm.GlobalLex.Clear();
    }
}
