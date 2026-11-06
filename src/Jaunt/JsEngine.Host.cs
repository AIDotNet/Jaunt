using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Jaunt.Runtime;

namespace Jaunt;

/// <summary>Raw synchronous host callback. The context and its argument span are only valid during the call.</summary>
public delegate JsValue JsNativeFunction(ref JsCallContext context);

/// <summary>
/// Opaque, thread-safe handle to the Shared Data Block of a <c>SharedArrayBuffer</c>, used to share memory between
/// engines (see <see cref="JsCallContext.GetSharedMemory"/>).
/// </summary>
public sealed class JsSharedMemory
{
    internal readonly SharedBlock Block;
    internal readonly Vm Owner;

    internal JsSharedMemory(SharedBlock block, Vm owner)
    {
        Block = block;
        Owner = owner;
    }

    /// <summary>Current byte length of the shared memory.</summary>
    public int ByteLength => Volatile.Read(ref Block.Data).Length;
}

/// <summary>Context of a raw host callback (stack-only; never store it or use it after returning).</summary>
public ref struct JsCallContext
{
    internal readonly Realm Realm;
    private readonly ReadOnlySpan<JsValue> _args;
    private readonly string _name;

    internal JsCallContext(Realm realm, JsValue thisValue, ReadOnlySpan<JsValue> args, string name)
    {
        Realm = realm;
        This = thisValue;
        _args = args;
        _name = name;
    }

    /// <summary>
    /// Evaluates a classic script in the calling realm as part of the running execution (ScriptEvaluation: its
    /// top-level declarations become global bindings). Compile errors are thrown into JS as <c>SyntaxError</c>.
    /// </summary>
    public readonly JsValue EvaluateScript(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Jaunt.Compiler.ScriptTemplate t;
        try { t = JsCompiler.Compile(source, JsCompileOptions.Default, isModule: false, Realm.Vm.Token, vm: Realm.Vm); }
        catch (JsCompileException ce) { throw JsErrors.SyntaxError(Realm, ce.Description); }
        return Realm.Engine!.RunScriptTemplate(t, oneShot: true, Realm);
    }

    /// <summary>
    /// Creates a new realm (fresh global object and intrinsics) in the same agent as the running execution and returns
    /// its global object. Objects of both realms can be passed freely between them; each function keeps the realm it
    /// was created in (GetFunctionRealm). Used, for example, to host test262's <c>$262.createRealm</c>.
    /// </summary>
    public readonly JsValue CreateRealm()
    {
        if (Realm.Hardened) throw JsErrors.TypeError(Realm, "Creating realms is disabled (Hardened profile)");
        var r = new Realm(false, Realm);
        return JsValue.FromObject(r.GlobalObject);
    }

    /// <summary>Evaluates a classic script in the realm whose global object is <paramref name="realmGlobal"/> (see <see cref="CreateRealm"/>).</summary>
    public readonly JsValue EvaluateScript(JsValue realmGlobal, string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (realmGlobal.Ref is not JsObject g || !ReferenceEquals(g.Realm.GlobalObject, g) || !ReferenceEquals(g.Realm.Vm, Realm.Vm))
            throw JsErrors.TypeError(Realm, "EvaluateScript: not the global object of a realm of this engine");
        var target = g.Realm;
        Jaunt.Compiler.ScriptTemplate t;
        try { t = JsCompiler.Compile(source, JsCompileOptions.Default, isModule: false, Realm.Vm.Token, vm: Realm.Vm); }
        catch (JsCompileException ce) { throw JsErrors.SyntaxError(target, ce.Description); }
        return Realm.Engine!.RunScriptTemplate(t, oneShot: true, target);
    }

    /// <summary>
    /// Returns a handle to the memory of a <c>SharedArrayBuffer</c> that another engine (agent, typically on another
    /// thread) can wrap with <see cref="CreateSharedArrayBuffer"/>: both buffers then share the same bytes, and
    /// <c>Atomics</c> operations, <c>Atomics.wait</c> and <c>Atomics.notify</c> work across the engines.
    /// </summary>
    public readonly JsSharedMemory GetSharedMemory(JsValue sharedArrayBuffer)
    {
        JsEngine.CheckOwned(Realm, sharedArrayBuffer, nameof(sharedArrayBuffer));
        if (sharedArrayBuffer.Ref is not JsArrayBuffer { Block: { } block }) throw JsErrors.TypeError(Realm, "GetSharedMemory: not a SharedArrayBuffer");
        return new JsSharedMemory(block, Realm.Vm);
    }

    /// <summary>Creates a <c>SharedArrayBuffer</c> of the current realm over memory obtained from <see cref="GetSharedMemory"/>.</summary>
    public readonly JsValue CreateSharedArrayBuffer(JsSharedMemory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var r = Realm;
        var proto = r.Ensure(IntrinsicGroup.TypedArray).SharedArrayBufferPrototype!;
        var b = new JsArrayBuffer(r, proto, memory.Block);
        if (!ReferenceEquals(memory.Owner, r.Vm)) memory.Block.CrossAgent = true;
        return JsValue.FromObject(b);
    }

    /// <summary>
    /// Creates an object with the [[IsHTMLDDA]] internal slot (ECMA-262 Annex B.3.6; e.g. a browser's
    /// <c>document.all</c>, test262's <c>$262.IsHTMLDDA</c>): <c>typeof</c> is "undefined", it is falsy and loosely equal
    /// to null/undefined; calling it returns null.
    /// </summary>
    public readonly JsValue CreateHtmlDdaObject() => JsValue.FromObject(new JsHtmlDda(Realm));

    /// <summary>
    /// Returns the current realm's <c>%AbstractModuleSource%</c> constructor (source phase imports; it has no global
    /// name), e.g. for test262's <c>$262.AbstractModuleSource</c>.
    /// </summary>
    public readonly JsValue GetAbstractModuleSource() => JsValue.FromObject(ModuleSourceBuiltins.Constructor(Realm));

    /// <summary>Detaches an <c>ArrayBuffer</c> (DetachArrayBuffer), e.g. after transferring its contents to the host.</summary>
    public readonly void DetachArrayBuffer(JsValue buffer)
    {
        JsEngine.CheckOwned(Realm, buffer, nameof(buffer));
        if (buffer.Ref is not JsArrayBuffer { Shared: false } b) throw JsErrors.TypeError(Realm, "DetachArrayBuffer: not an ArrayBuffer");
        if (b.Immutable) throw JsErrors.TypeError(Realm, "DetachArrayBuffer: an immutable ArrayBuffer cannot be detached");
        b.Detach();
    }

    /// <summary>The <c>this</c> value of the call.</summary>
    public JsValue This { get; }
    /// <summary>Number of arguments passed.</summary>
    public readonly int ArgumentCount => _args.Length;
    /// <summary>Argument by index (undefined when missing).</summary>
    public readonly JsValue this[int index] => (uint)index < (uint)_args.Length ? _args[index] : default;
    /// <summary>Borrowed view of the arguments (valid only during the call).</summary>
    public readonly ReadOnlySpan<JsValue> Arguments => _args;

    /// <summary>Reads a number argument or throws a JS TypeError.</summary>
    public readonly double GetNumber(int index) => JsConvert<double>.From!(Realm, this[index], index, _name);
    /// <summary>Reads an Int32 argument or throws a JS TypeError.</summary>
    public readonly int GetInt32(int index) => JsConvert<int>.From!(Realm, this[index], index, _name);
    /// <summary>Reads a string argument (null for null/undefined) or throws a JS TypeError.</summary>
    public readonly string? GetString(int index) => JsConvert<string>.From!(Realm, this[index], index, _name);
    /// <summary>Reads a boolean argument or throws a JS TypeError.</summary>
    public readonly bool GetBoolean(int index) => JsConvert<bool>.From!(Realm, this[index], index, _name);

    /// <summary>Creates a JS TypeError to return as a thrown completion: <c>throw context.TypeError("...")</c>.</summary>
    public readonly Exception TypeError(string message) => JsErrors.TypeError(Realm, message);

    /// <summary>Creates an empty plain object in the current realm.</summary>
    public readonly JsValue CreateObject() => JsValue.FromObject(Realm.NewObject());

    /// <summary>Creates an array from values.</summary>
    public readonly JsValue CreateArray(ReadOnlySpan<JsValue> items)
    {
        foreach (var v in items) JsEngine.CheckOwned(Realm, v, nameof(items));
        return JsValue.FromObject(Operations.CreateArrayFromList(Realm, items));
    }

    /// <summary>Defines a data property on an object created in this realm.</summary>
    public readonly void SetProperty(JsValue target, string name, JsValue value)
    {
        if (target.IsNotObject || !ReferenceEquals(((JsObject)target.Ref!).Realm, Realm)) throw new ArgumentException("Target is not an object of this engine.", nameof(target));
        JsEngine.CheckOwned(Realm, value, nameof(value));
        ((JsObject)target.Ref!).CreateDataPropertyOrThrow(name, value);
    }

    /// <summary>Reads an own data property without running getters (undefined when missing or an accessor).</summary>
    public readonly JsValue GetOwnDataProperty(JsValue target, string name)
    {
        if (target.IsNotObject) return default;
        JsEngine.CheckOwned(Realm, target, nameof(target));
        var o = (JsObject)target.Ref!;
        if (o.IsExotic) return o.GetOwnProperty(name, out var d) && d.IsData ? d.Value : default;
        int i = o.FindOwn(name, out var f);
        return i >= 0 && (f & PropFlags.Accessor) == 0 ? o.ReadSlot(i) : default;
    }

    /// <summary>Calls a JS function within the current execution (same budget; no new root).</summary>
    public readonly JsValue Call(JsValue function, JsValue thisValue, ReadOnlySpan<JsValue> args)
    {
        JsEngine.CheckOwned(Realm, function, nameof(function));
        JsEngine.CheckOwned(Realm, thisValue, nameof(thisValue));
        foreach (var v in args) JsEngine.CheckOwned(Realm, v, nameof(args));
        return Realm.Vm.Call(function, thisValue, args);
    }
}

public sealed partial class JsEngine
{
    // ------------------------------------------------------------------ host function registration

    private void DefineHostFunction(string name, int length, NativeFn fn, object state, bool isAsync = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        WithExclusive(() =>
        {
            var f = new JsBuiltin(Realm, name, length, fn) { State = state, IsAsyncHost = isAsync };
            Realm.EnsureGlobal(name);
            var g = Realm.GlobalObject;
            int idx = g.FindOwn(name, out var flags);
            if (idx >= 0 && ((flags & PropFlags.Accessor) != 0 || (flags & PropFlags.Writable) == 0))
                throw new InvalidOperationException($"Global '{name}' is an accessor or read-only property and cannot be replaced.");
            if (idx >= 0) g.WriteSlot(idx, JsValue.FromObject(f));
            else { g.CreateDataProperty(name, JsValue.FromObject(f)); Realm.GlobalVersion++; }
        });
    }

    internal Exception HostError(Realm realm, string name, Exception ex)
    {
        if (ex is OperationCanceledException && _root != null && _root.Token.IsCancellationRequested)
            return new JsTerminationException(TerminationKind.Cancelled, "Execution was cancelled.");
        string msg = _options.ExposeHostExceptionMessages ? ex.Message : $"Host function '{name}' failed.";
        var err = JsError.Create(realm, ErrorKind.Error, msg);
        err.HostException = ex;
        return new JsThrowException(JsValue.FromObject(err));
    }

    private static bool IsHostException(Exception ex) => ex is not (JsThrowException or JsTerminationException);

    /// <summary>Registers a raw host callback.</summary>
    public void SetFunction(string name, JsNativeFunction callback, int length = 0)
    {
        ArgumentNullException.ThrowIfNull(callback);
        DefineHostFunction(name, length, static (in NativeArgs a) =>
        {
            var (cb, nm, engine) = ((JsNativeFunction, string, JsEngine))a.Callee.State!;
            var ctx = new JsCallContext(a.Realm, a.This, a.Args, nm);
            JsValue r;
            try { r = cb(ref ctx); }
            catch (Exception ex) when (IsHostException(ex)) { throw engine.HostError(a.Realm, nm, ex); }
            engine.EnsureOwned(r, "return");
            return r;
        }, (callback, name, this));
    }

    /// <summary>Registers a host action without arguments.</summary>
    public void SetFunction(string name, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        DefineHostFunction(name, 0, static (in NativeArgs a) =>
        {
            var (fn, nm, engine) = ((Action, string, JsEngine))a.Callee.State!;
            try { fn(); }
            catch (Exception ex) when (IsHostException(ex)) { throw engine.HostError(a.Realm, nm, ex); }
            return default;
        }, (action, name, this));
    }

    /// <summary>Registers a typed host action with one argument.</summary>
    public void SetFunction<T1>(string name, Action<T1> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        JsConvert<T1>.EnsureSupported();
        DefineHostFunction(name, 1, static (in NativeArgs a) =>
        {
            var (fn, nm, engine) = ((Action<T1>, string, JsEngine))a.Callee.State!;
            var x1 = JsConvert<T1>.From!(a.Realm, a[0], 0, nm);
            try { fn(x1); }
            catch (Exception ex) when (IsHostException(ex)) { throw engine.HostError(a.Realm, nm, ex); }
            return default;
        }, (action, name, this));
    }

    /// <summary>Registers a typed host function without arguments.</summary>
    public void SetFunction<TResult>(string name, Func<TResult> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        JsConvert<TResult>.EnsureSupported();
        DefineHostFunction(name, 0, static (in NativeArgs a) =>
        {
            var (fn, nm, engine) = ((Func<TResult>, string, JsEngine))a.Callee.State!;
            TResult r;
            try { r = fn(); }
            catch (Exception ex) when (IsHostException(ex)) { throw engine.HostError(a.Realm, nm, ex); }
            return JsConvert<TResult>.To!(a.Realm, r);
        }, (function, name, this));
    }

    /// <summary>Registers a typed host function with one argument (adapter built at registration; no DynamicInvoke).</summary>
    public void SetFunction<T1, TResult>(string name, Func<T1, TResult> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        JsConvert<T1>.EnsureSupported();
        JsConvert<TResult>.EnsureSupported();
        DefineHostFunction(name, 1, static (in NativeArgs a) =>
        {
            var (fn, nm, engine) = ((Func<T1, TResult>, string, JsEngine))a.Callee.State!;
            var x1 = JsConvert<T1>.From!(a.Realm, a[0], 0, nm);
            TResult r;
            try { r = fn(x1); }
            catch (Exception ex) when (IsHostException(ex)) { throw engine.HostError(a.Realm, nm, ex); }
            return JsConvert<TResult>.To!(a.Realm, r);
        }, (function, name, this));
    }

    /// <summary>Registers a typed host function with two arguments.</summary>
    public void SetFunction<T1, T2, TResult>(string name, Func<T1, T2, TResult> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        JsConvert<T1>.EnsureSupported();
        JsConvert<T2>.EnsureSupported();
        JsConvert<TResult>.EnsureSupported();
        DefineHostFunction(name, 2, static (in NativeArgs a) =>
        {
            var (fn, nm, engine) = ((Func<T1, T2, TResult>, string, JsEngine))a.Callee.State!;
            var x1 = JsConvert<T1>.From!(a.Realm, a[0], 0, nm);
            var x2 = JsConvert<T2>.From!(a.Realm, a[1], 1, nm);
            TResult r;
            try { r = fn(x1, x2); }
            catch (Exception ex) when (IsHostException(ex)) { throw engine.HostError(a.Realm, nm, ex); }
            return JsConvert<TResult>.To!(a.Realm, r);
        }, (function, name, this));
    }

    /// <summary>Registers a typed host function with three arguments.</summary>
    public void SetFunction<T1, T2, T3, TResult>(string name, Func<T1, T2, T3, TResult> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        JsConvert<T1>.EnsureSupported();
        JsConvert<T2>.EnsureSupported();
        JsConvert<T3>.EnsureSupported();
        JsConvert<TResult>.EnsureSupported();
        DefineHostFunction(name, 3, static (in NativeArgs a) =>
        {
            var (fn, nm, engine) = ((Func<T1, T2, T3, TResult>, string, JsEngine))a.Callee.State!;
            var x1 = JsConvert<T1>.From!(a.Realm, a[0], 0, nm);
            var x2 = JsConvert<T2>.From!(a.Realm, a[1], 1, nm);
            var x3 = JsConvert<T3>.From!(a.Realm, a[2], 2, nm);
            TResult r;
            try { r = fn(x1, x2, x3); }
            catch (Exception ex) when (IsHostException(ex)) { throw engine.HostError(a.Realm, nm, ex); }
            return JsConvert<TResult>.To!(a.Realm, r);
        }, (function, name, this));
    }

    /// <summary>Registers a typed host function with four arguments.</summary>
    public void SetFunction<T1, T2, T3, T4, TResult>(string name, Func<T1, T2, T3, T4, TResult> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        JsConvert<T1>.EnsureSupported();
        JsConvert<T2>.EnsureSupported();
        JsConvert<T3>.EnsureSupported();
        JsConvert<T4>.EnsureSupported();
        JsConvert<TResult>.EnsureSupported();
        DefineHostFunction(name, 4, static (in NativeArgs a) =>
        {
            var (fn, nm, engine) = ((Func<T1, T2, T3, T4, TResult>, string, JsEngine))a.Callee.State!;
            var x1 = JsConvert<T1>.From!(a.Realm, a[0], 0, nm);
            var x2 = JsConvert<T2>.From!(a.Realm, a[1], 1, nm);
            var x3 = JsConvert<T3>.From!(a.Realm, a[2], 2, nm);
            var x4 = JsConvert<T4>.From!(a.Realm, a[3], 3, nm);
            TResult r;
            try { r = fn(x1, x2, x3, x4); }
            catch (Exception ex) when (IsHostException(ex)) { throw engine.HostError(a.Realm, nm, ex); }
            return JsConvert<TResult>.To!(a.Realm, r);
        }, (function, name, this));
    }

    /// <summary>Registers a host function whose argument and result are mapped through explicit DTO descriptors.</summary>
    public void SetFunction<TArg, TResult>(string name, Func<TArg, TResult> function, JsTypeInfo<TArg> argumentInfo, JsTypeInfo<TResult> resultInfo)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(argumentInfo);
        ArgumentNullException.ThrowIfNull(resultInfo);
        DefineHostFunction(name, 1, static (in NativeArgs a) =>
        {
            var (fn, nm, engine, ai, ri) = ((Func<TArg, TResult>, string, JsEngine, JsTypeInfo<TArg>, JsTypeInfo<TResult>))a.Callee.State!;
            TArg arg;
            try { arg = ai.Read(new JsConversionContext(a.Realm, long.MaxValue), a[0]); }
            catch (JsConversionException ce) { throw JsErrors.TypeError(a.Realm, $"Argument 1 of '{nm}': {ce.Message}"); }
            TResult r;
            try { r = fn(arg); }
            catch (Exception ex) when (IsHostException(ex)) { throw engine.HostError(a.Realm, nm, ex); }
            return ri.Write(new JsConversionContext(a.Realm, long.MaxValue), r);
        }, (function, name, this, argumentInfo, resultInfo));
    }

    // ------------------------------------------------------------------ asynchronous host functions

    internal sealed class HostOperation
    {
        public required RootState Root;
        public required JsPromise Promise;
        public bool Completed;
    }

    private readonly ConcurrentQueue<Action> _completions = new();
    private readonly SemaphoreSlim _signal = new(0);

    private void Post(Action completion)
    {
        _completions.Enqueue(completion);
        _signal.Release();
    }

    internal void ProcessCompletions(RootState root)
    {
        while (_completions.TryDequeue(out var c)) c();
    }

    internal bool IsAsyncExecution => _root is { Mode: JsExecutionMode.Async };

    /// <summary>A promise fulfilled with <paramref name="value"/> after <paramref name="milliseconds"/> (async roots only).</summary>
    internal JsValue StartDelay(Realm realm, string name, double milliseconds, string value)
    {
        int ms = milliseconds >= int.MaxValue ? int.MaxValue : (int)Math.Ceiling(milliseconds);
        return StartHostOperation<string>(realm, name, async ct =>
        {
            await Task.Delay(ms, ct).ConfigureAwait(false);
            return value;
        });
    }

    private JsValue StartHostOperation<TResult>(Realm realm, string name, Func<CancellationToken, ValueTask<TResult>> start)
    {
        var root = _root;
        if (root == null || root.Mode != JsExecutionMode.Async)
            throw new JsTerminationException(TerminationKind.AsyncRequired,
                $"Asynchronous host function '{name}' was called from a synchronous execution; use an Async entry point (the host function was not invoked).");
        root.OpsCts ??= CancellationTokenSource.CreateLinkedTokenSource(root.Token);
        var promise = new JsPromise(realm);
        var op = new HostOperation { Root = root, Promise = promise };
        root.PendingOps.Add(op);
        ValueTask<TResult> vt;
        try { vt = start(root.OpsCts.Token); }
        catch (Exception ex) when (IsHostException(ex))
        {
            op.Completed = true;
            throw HostError(realm, name, ex);
        }
        if (vt.IsCompletedSuccessfully)
        {
            op.Completed = true;
            promise.ResolveWith(JsConvert<TResult>.To!(realm, vt.Result));
            return JsValue.FromObject(promise);
        }
        _ = AwaitHostAsync(vt, op, realm, name);
        return JsValue.FromObject(promise);
    }

    private async Task AwaitHostAsync<TResult>(ValueTask<TResult> vt, HostOperation op, Realm realm, string name)
    {
        TResult result = default!;
        Exception? error = null;
        try { result = await vt.ConfigureAwait(false); }
        catch (Exception ex) { error = ex; }
        Post(() =>
        {
            // late completions of closed roots are isolated and dropped
            if (op.Completed || op.Root.Closed || Volatile.Read(ref _state) is StateDisposed or StateFaulted) { op.Completed = true; return; }
            op.Completed = true;
            if (error != null)
            {
                var thrown = HostError(realm, name, error);
                if (thrown is JsThrowException jt) op.Promise.RejectWith(jt.Value);
                else op.Promise.RejectWith(JsValue.FromObject(JsError.Create(realm, ErrorKind.Error, "Operation was cancelled.")));
            }
            else
            {
                try { op.Promise.ResolveWith(JsConvert<TResult>.To!(realm, result)); }
                catch (JsThrowException ex) { op.Promise.RejectWith(ex.Value); }
            }
        });
    }

    /// <summary>Registers an asynchronous host function (callable only from Async executions).</summary>
    public void SetAsyncFunction<TResult>(string name, Func<CancellationToken, ValueTask<TResult>> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        JsConvert<TResult>.EnsureSupported();
        DefineHostFunction(name, 0, static (in NativeArgs a) =>
        {
            var (fn, nm, engine) = ((Func<CancellationToken, ValueTask<TResult>>, string, JsEngine))a.Callee.State!;
            return engine.StartHostOperation(a.Realm, nm, fn);
        }, (function, name, this), isAsync: true);
    }

    /// <summary>Registers an asynchronous host function with one argument. Arguments are snapshotted before the first await.</summary>
    public void SetAsyncFunction<T1, TResult>(string name, Func<T1, CancellationToken, ValueTask<TResult>> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        JsConvert<T1>.EnsureSupported();
        JsConvert<TResult>.EnsureSupported();
        DefineHostFunction(name, 1, static (in NativeArgs a) =>
        {
            var (fn, nm, engine) = ((Func<T1, CancellationToken, ValueTask<TResult>>, string, JsEngine))a.Callee.State!;
            if (engine._root == null || engine._root.Mode != JsExecutionMode.Async) return engine.StartHostOperation<TResult>(a.Realm, nm, static _ => default);
            var x1 = JsConvert<T1>.From!(a.Realm, a[0], 0, nm);
            return engine.StartHostOperation(a.Realm, nm, ct => fn(x1, ct));
        }, (function, name, this), isAsync: true);
    }

    /// <summary>Registers an asynchronous host function with two arguments.</summary>
    public void SetAsyncFunction<T1, T2, TResult>(string name, Func<T1, T2, CancellationToken, ValueTask<TResult>> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        JsConvert<T1>.EnsureSupported();
        JsConvert<T2>.EnsureSupported();
        JsConvert<TResult>.EnsureSupported();
        DefineHostFunction(name, 2, static (in NativeArgs a) =>
        {
            var (fn, nm, engine) = ((Func<T1, T2, CancellationToken, ValueTask<TResult>>, string, JsEngine))a.Callee.State!;
            if (engine._root == null || engine._root.Mode != JsExecutionMode.Async) return engine.StartHostOperation<TResult>(a.Realm, nm, static _ => default);
            var x1 = JsConvert<T1>.From!(a.Realm, a[0], 0, nm);
            var x2 = JsConvert<T2>.From!(a.Realm, a[1], 1, nm);
            return engine.StartHostOperation(a.Realm, nm, ct => fn(x1, x2, ct));
        }, (function, name, this), isAsync: true);
    }

    // ------------------------------------------------------------------ async pump

    internal async ValueTask PumpUntilSettledAsync(JsPromise p, RootState root)
    {
        while (true)
        {
            Vm.RunJobs();
            ProcessCompletions(root);
            if (Vm.PendingJobCount > 0) continue;
            if (p.State != PromiseState.Pending) return;
            await WaitForSignalAsync(root).ConfigureAwait(false);
        }
    }

    private async ValueTask WaitForSignalAsync(RootState root)
    {
        TimeSpan wait = Timeout.InfiniteTimeSpan;
        if (root.Deadline != long.MaxValue)
        {
            long remaining = root.Deadline - System.Diagnostics.Stopwatch.GetTimestamp();
            if (remaining <= 0) throw new JsTerminationException(TerminationKind.Timeout, "Execution exceeded its timeout while waiting for asynchronous work.");
            wait = TimeSpan.FromSeconds((double)remaining / System.Diagnostics.Stopwatch.Frequency);
        }
        bool signaled;
        try { signaled = await _signal.WaitAsync(wait, root.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw new JsTerminationException(TerminationKind.Cancelled, "Execution was cancelled."); }
        if (!signaled) throw new JsTerminationException(TerminationKind.Timeout, "Execution exceeded its timeout while waiting for asynchronous work.");
    }

    /// <summary>Promise/thenable assimilation for async entry points (like JS <c>await</c>).</summary>
    internal JsPromise Assimilate(JsValue v) => JsPromise.Resolve(Realm, v);

    internal async ValueTask<JsValue> AwaitInRootAsync(JsValue value, RootState root)
    {
        var p = Assimilate(value);
        await PumpUntilSettledAsync(p, root).ConfigureAwait(false);
        if (p.State == PromiseState.Rejected)
        {
            p.IsHandled = true;
            Realm.UnhandledRejections.Remove(p);
            throw new JsThrowException(p.Result);
        }
        return p.Result;
    }

    /// <summary>Evaluates a script asynchronously, awaiting its completion value like JS <c>await</c>.</summary>
    public async ValueTask<JsValue> EvaluateAsync(string source, JsExecutionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var root = BeginRoot(options, cancellationToken, JsExecutionMode.Async);
        try
        {
            var t = JsCompiler.Compile(source, _options.CompileOptions, isModule: false, cancellationToken, vm: Vm);
            var r = RunScriptTemplate(t, oneShot: true);
            r = await AwaitInRootAsync(r, root).ConfigureAwait(false);
            CheckpointRoot(root);
            CloseRoot(root, true);
            return r;
        }
        catch (Exception ex) when (ex is not JsCompileException) { throw MapException(ex, root); }
        finally { EndRoot(root); }
    }

    /// <summary>Evaluates a precompiled script asynchronously.</summary>
    public async ValueTask<JsValue> EvaluateAsync(JsScript script, JsExecutionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        var root = BeginRoot(options, cancellationToken, JsExecutionMode.Async);
        try
        {
            var r = RunScriptTemplate(script.Template, oneShot: false);
            r = await AwaitInRootAsync(r, root).ConfigureAwait(false);
            CheckpointRoot(root);
            CloseRoot(root, true);
            return r;
        }
        catch (Exception ex) { throw MapException(ex, root); }
        finally { EndRoot(root); }
    }

    /// <summary>Invokes a function and awaits its (promise) result.</summary>
    public async ValueTask<JsValue> InvokeAsync(JsFunction function, JsValue[]? args = null, JsExecutionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(function);
        function.Check(this);
        var snapshot = args == null ? Array.Empty<JsValue>() : (JsValue[])args.Clone();
        foreach (var a in snapshot) EnsureOwned(a, nameof(args));
        var root = BeginRoot(options, cancellationToken, JsExecutionMode.Async);
        try
        {
            var r = function.Target.Call(JsValue.Undefined, snapshot);
            r = await AwaitInRootAsync(r, root).ConfigureAwait(false);
            CheckpointRoot(root);
            CloseRoot(root, true);
            return r;
        }
        catch (Exception ex) { throw MapException(ex, root); }
        finally { EndRoot(root); }
    }

    /// <summary>Reads a live export, invokes it and awaits its result.</summary>
    public async ValueTask<JsValue> InvokeAsync(JsExport export, JsValue[]? args = null, JsExecutionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(export);
        export.Check(this);
        var snapshot = args == null ? Array.Empty<JsValue>() : (JsValue[])args.Clone();
        foreach (var a in snapshot) EnsureOwned(a, nameof(args));
        var root = BeginRoot(options, cancellationToken, JsExecutionMode.Async);
        try
        {
            var r = Vm.CallValue(export.Read(), JsValue.Undefined, snapshot);
            r = await AwaitInRootAsync(r, root).ConfigureAwait(false);
            CheckpointRoot(root);
            CloseRoot(root, true);
            return r;
        }
        catch (Exception ex) { throw MapException(ex, root); }
        finally { EndRoot(root); }
    }

    /// <summary>Awaits an already obtained promise (or value) without re-running any code.</summary>
    public async ValueTask<JsValue> AwaitAsync(JsValue promiseOrValue, JsExecutionOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureOwned(promiseOrValue, nameof(promiseOrValue));
        var root = BeginRoot(options, cancellationToken, JsExecutionMode.Async);
        try
        {
            var r = await AwaitInRootAsync(promiseOrValue, root).ConfigureAwait(false);
            CheckpointRoot(root);
            CloseRoot(root, true);
            return r;
        }
        catch (Exception ex) { throw MapException(ex, root); }
        finally { EndRoot(root); }
    }
}
