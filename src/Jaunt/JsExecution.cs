using System;
using System.Buffers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jaunt.Runtime;

namespace Jaunt;

/// <summary>
/// An explicit root execution scope: holds the engine's exclusive right and one shared budget (deadline,
/// work, output) for every operation performed through it. Call <see cref="Complete"/> to run the final
/// checkpoint; results should be published only after it succeeds. <see cref="Dispose"/> never runs JS.
/// </summary>
public sealed class JsExecution : IDisposable, IAsyncDisposable
{
    private readonly JsEngine _engine;
    private readonly JsEngine.RootState _root;
    private bool _completed;
    private bool _disposed;
    private int _busy;

    internal JsExecution(JsEngine engine, JsEngine.RootState root)
    {
        _engine = engine;
        _root = root;
    }

    /// <summary>The engine this scope executes in.</summary>
    public JsEngine Engine => _engine;

    private void Enter()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(JsExecution));
        if (_completed) throw new InvalidOperationException("The execution scope has already been completed.");
        if (_engine.IsFaulted) throw new JsEngineFaultedException("Faulted", "The engine is faulted.");
        if (Interlocked.Exchange(ref _busy, 1) != 0) throw new InvalidOperationException("Operations of one JsExecution scope must not run concurrently.");
    }

    private void Exit() => Volatile.Write(ref _busy, 0);

    private Exception Map(Exception ex) => _engine.MapException(ex, _root);

    /// <summary>Evaluates source within this scope.</summary>
    public JsValue Evaluate(string source)
    {
        Enter();
        try
        {
            var t = JsCompiler.Compile(source, _engine.Options.CompileOptions, isModule: false, _root.Token, vm: _engine.Vm);
            return _engine.RunScriptTemplate(t, oneShot: true);
        }
        catch (Exception ex) when (ex is not JsCompileException) { throw Map(ex); }
        finally { Exit(); }
    }

    /// <summary>Evaluates a compiled script within this scope.</summary>
    public JsValue Evaluate(JsScript script)
    {
        Enter();
        try { return _engine.RunScriptTemplate(script.Template, oneShot: false); }
        catch (Exception ex) { throw Map(ex); }
        finally { Exit(); }
    }

    /// <summary>Invokes a function within this scope.</summary>
    public JsValue Invoke(JsFunction function, params JsValue[] args)
    {
        function.Check(_engine);
        foreach (var a in args) _engine.EnsureOwned(a, nameof(args));
        Enter();
        try { return function.Target.Call(JsValue.Undefined, args); }
        catch (Exception ex) { throw Map(ex); }
        finally { Exit(); }
    }

    /// <summary>Reads a live export and invokes it within this scope.</summary>
    public JsValue Invoke(JsExport export, params JsValue[] args)
    {
        export.Check(_engine);
        foreach (var a in args) _engine.EnsureOwned(a, nameof(args));
        Enter();
        try { return _engine.Vm.CallValue(export.Read(), JsValue.Undefined, args); }
        catch (Exception ex) { throw Map(ex); }
        finally { Exit(); }
    }

    /// <summary>Reads an export's current function object within this scope.</summary>
    public JsFunction GetFunction(JsModuleInstance instance, string name)
    {
        Enter();
        try { return instance.GetFunction(name); }
        finally { Exit(); }
    }

    /// <summary>Invokes asynchronously and awaits the result (scope must use <see cref="JsExecutionMode.Async"/>).</summary>
    public async ValueTask<JsValue> InvokeAsync(JsFunction function, params JsValue[] args)
    {
        function.Check(_engine);
        var snapshot = (JsValue[])args.Clone();
        foreach (var a in snapshot) _engine.EnsureOwned(a, nameof(args));
        RequireAsync();
        Enter();
        try
        {
            var r = function.Target.Call(JsValue.Undefined, snapshot);
            return await _engine.AwaitInRootAsync(r, _root).ConfigureAwait(false);
        }
        catch (Exception ex) { throw Map(ex); }
        finally { Exit(); }
    }

    /// <summary>Invokes a live export asynchronously and awaits the result.</summary>
    public async ValueTask<JsValue> InvokeAsync(JsExport export, params JsValue[] args)
    {
        export.Check(_engine);
        var snapshot = (JsValue[])args.Clone();
        foreach (var a in snapshot) _engine.EnsureOwned(a, nameof(args));
        RequireAsync();
        Enter();
        try
        {
            var r = _engine.Vm.CallValue(export.Read(), JsValue.Undefined, snapshot);
            return await _engine.AwaitInRootAsync(r, _root).ConfigureAwait(false);
        }
        catch (Exception ex) { throw Map(ex); }
        finally { Exit(); }
    }

    /// <summary>Awaits a promise obtained earlier in this scope.</summary>
    public async ValueTask<JsValue> AwaitAsync(JsValue promiseOrValue)
    {
        _engine.EnsureOwned(promiseOrValue, nameof(promiseOrValue));
        RequireAsync();
        Enter();
        try { return await _engine.AwaitInRootAsync(promiseOrValue, _root).ConfigureAwait(false); }
        catch (Exception ex) { throw Map(ex); }
        finally { Exit(); }
    }

    private void RequireAsync()
    {
        if (_root.Mode != JsExecutionMode.Async)
            throw new InvalidOperationException("Async operations require JsExecutionOptions.Mode = JsExecutionMode.Async.");
    }

    // ------------------------------------------------------------------ JSON channel

    /// <summary>Parses JSON text (full validation; budgeted).</summary>
    public JsValue ParseJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        Enter();
        try { return JsonBuiltins.Parse(_engine.Realm, json, maxDepth: 512); }
        catch (Exception ex) { throw Map(ex); }
        finally { Exit(); }
    }

    /// <summary>Parses UTF-8 JSON. The engine takes its own copy; the caller may reuse the buffer afterwards.</summary>
    public JsValue ParseJson(ReadOnlySpan<byte> utf8Json)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(utf8Json); }
        catch (DecoderFallbackException) { throw new JsConversionException("$", "Input is not valid UTF-8"); }
        return ParseJson(text);
    }

    /// <summary>
    /// Serializes with <c>JSON.stringify</c> semantics (toJSON/getters run inside this scope's budget) into an
    /// independent UTF-8 array. Values without a JSON representation (e.g. undefined) are an error.
    /// </summary>
    public byte[] ToJsonUtf8(JsValue value)
    {
        var s = ToJsonString(value);
        return Encoding.UTF8.GetBytes(s);
    }

    /// <summary>Serializes with <c>JSON.stringify</c> semantics to a string.</summary>
    public string ToJsonString(JsValue value)
    {
        _engine.EnsureOwned(value, nameof(value));
        Enter();
        try
        {
            // the serializer stops early once the output has more UTF-16 units than the budget (a lower bound of its
            // UTF-8 size); the exact UTF-8 size is checked on the result
            var s = JsonBuiltins.Stringify(_engine.Realm, value, JsValue.Undefined, JsValue.Undefined, _root.MaxOutputBytes);
            if (s == null) throw new JsConversionException("$", "Value has no JSON representation");
            long bytes = s.Length > _root.MaxOutputBytes / 3 ? Encoding.UTF8.GetByteCount(s) : s.Length;
            if (bytes > _root.MaxOutputBytes)
                throw new JsTerminationException(TerminationKind.OutputBytes, $"JSON output exceeded its limit of {_root.MaxOutputBytes} UTF-8 bytes.", bytes, _root.MaxOutputBytes);
            return s;
        }
        catch (Exception ex) when (ex is not JsConversionException) { throw Map(ex); }
        finally { Exit(); }
    }

    /// <summary>Writes JSON to a buffer writer. On failure the writer may contain partial output.</summary>
    public void WriteJson(JsValue value, IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var bytes = ToJsonUtf8(value);
        writer.Write(bytes);
    }

    // ------------------------------------------------------------------ DTO channel

    /// <summary>Maps a CLR value to JS data through an explicit descriptor.</summary>
    public JsValue FromObject<T>(T value, JsTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        Enter();
        try { return typeInfo.Write(new JsConversionContext(_engine.Realm, _root.MaxOutputBytes), value); }
        catch (Exception ex) when (ex is not JsConversionException) { throw Map(ex); }
        finally { Exit(); }
    }

    /// <summary>Maps JS data to a new CLR value (data-only: no getters, proxies, functions or promises).</summary>
    public T ToObject<T>(JsValue value, JsTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        _engine.EnsureOwned(value, nameof(value));
        Enter();
        try { return typeInfo.Read(new JsConversionContext(_engine.Realm, _root.MaxOutputBytes), value); }
        catch (Exception ex) when (ex is not JsConversionException) { throw Map(ex); }
        finally { Exit(); }
    }

    // ------------------------------------------------------------------ completion

    /// <summary>
    /// Final checkpoint: drains ready jobs, reports unhandled rejections and verifies that no external host
    /// operation is still pending (otherwise <c>UnjoinedAsyncWork</c> faults the engine).
    /// </summary>
    public void Complete()
    {
        Enter();
        try
        {
            _engine.CheckpointRoot(_root);
            _engine.CloseRoot(_root, requireNoPendingWork: true);
            _completed = true;
        }
        catch (Exception ex) when (ex is not JsEngineFaultedException) { throw Map(ex); }
        finally { Exit(); }
    }

    /// <summary>
    /// Releases the scope. Does not run JS. Abandoning an incomplete scope cancels its host work; if host work
    /// was still pending the engine is faulted.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_completed && !_engine.IsFaulted)
        {
            bool pending = false;
            foreach (var op in _root.PendingOps) if (!op.Completed) { pending = true; break; }
            if (pending) _engine.Fault(new JsTerminationException(TerminationKind.UnjoinedAsyncWork, "Execution scope abandoned with pending host work."));
        }
        _engine.EndRoot(_root);
    }

    /// <inheritdoc cref="Dispose"/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }
}

public sealed partial class JsEngine
{
    /// <summary>Parses JSON into a JS value owned by this engine (own budget).</summary>
    public JsValue ParseJson(string json)
    {
        using var ex = BeginExecution();
        var v = ex.ParseJson(json);
        ex.Complete();
        return v;
    }

    /// <summary>Serializes a value with JSON.stringify semantics into UTF-8 (own budget).</summary>
    public byte[] ToJsonUtf8(JsValue value)
    {
        using var ex = BeginExecution();
        var b = ex.ToJsonUtf8(value);
        ex.Complete();
        return b;
    }

    /// <summary>Serializes a value with JSON.stringify semantics (own budget).</summary>
    public string ToJsonString(JsValue value)
    {
        using var ex = BeginExecution();
        var s = ex.ToJsonString(value);
        ex.Complete();
        return s;
    }
}
