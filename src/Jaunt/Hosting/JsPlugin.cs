using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jaunt;

/// <summary>Options for <see cref="JsPlugin{TInput, TOutput}"/>.</summary>
public sealed class JsPluginOptions
{
    /// <summary>Maximum concurrent invocations (each runs in its own fresh engine).</summary>
    public int MaxConcurrency { get; init; } = Environment.ProcessorCount;
    /// <summary>Options for every fresh engine.</summary>
    public JsEngineOptions? EngineOptions { get; init; }
    /// <summary>Installs explicitly authorized host capabilities into each fresh engine.</summary>
    public Action<JsEngine>? ConfigureEngine { get; init; }
    /// <summary>Resolver for the plugin module's imports (default: deny).</summary>
    public IJsModuleResolver? Resolver { get; init; }
    /// <summary>Per-invocation execution budget (covers input conversion, module init, call, await and output).</summary>
    public JsExecutionOptions? Execution { get; init; }
}

/// <summary>Factory for plugins.</summary>
public static class JsPlugin
{
    /// <summary>Creates a plugin that maps <typeparamref name="TInput"/> → JS export call → <typeparamref name="TOutput"/>.</summary>
    public static JsPlugin<TInput, TOutput> Create<TInput, TOutput>(JsModule module, string exportName,
        JsTypeInfo<TInput> inputInfo, JsTypeInfo<TOutput> outputInfo, JsPluginOptions? options = null)
        => new(module, exportName, inputInfo, outputInfo, options ?? new JsPluginOptions());
}

/// <summary>
/// Production plugin wrapper. Every invocation gets a FreshRealm (new globals, intrinsics, module instances
/// and closures); only the immutable compiled module is shared. Inputs and outputs are detached .NET data —
/// no JS handle escapes. Concurrency is bounded and waiting is cancellable.
/// </summary>
public sealed class JsPlugin<TInput, TOutput> : IAsyncDisposable, IDisposable
{
    private readonly JsModule _module;
    private readonly string _exportName;
    private readonly JsTypeInfo<TInput> _input;
    private readonly JsTypeInfo<TOutput> _output;
    private readonly JsPluginOptions _options;
    private readonly SemaphoreSlim _gate;
    private int _disposed;

    internal JsPlugin(JsModule module, string exportName, JsTypeInfo<TInput> input, JsTypeInfo<TOutput> output, JsPluginOptions options)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));
        _exportName = exportName ?? throw new ArgumentNullException(nameof(exportName));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _options = options;
        _gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrency));
    }

    /// <summary>Invokes the plugin asynchronously (async host functions and promises are supported).</summary>
    public async ValueTask<TOutput> InvokeAsync(TInput input, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(JsPlugin<TInput, TOutput>));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var engine = new JsEngine(_options.EngineOptions);
            _options.ConfigureEngine?.Invoke(engine);
            var instance = _module.Template.Module!.HasTopLevelAwait
                ? await engine.LoadModuleAsync(_module, _options.Resolver, cancellationToken: cancellationToken).ConfigureAwait(false)
                : engine.LoadModule(_module, _options.Resolver, cancellationToken: cancellationToken);
            var export = instance.GetExport(_exportName);
            var execOptions = _options.Execution is { } e
                ? new JsExecutionOptions { Timeout = e.Timeout, MaxInstructions = e.MaxInstructions, MaxOutputBytes = e.MaxOutputBytes, Mode = JsExecutionMode.Async }
                : new JsExecutionOptions { Mode = JsExecutionMode.Async };
            await using var execution = engine.BeginExecution(execOptions, cancellationToken);
            var arg = execution.FromObject(input, _input);
            var result = await execution.InvokeAsync(export, arg).ConfigureAwait(false);
            var output = execution.ToObject(result, _output);
            execution.Complete();
            return output;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Synchronous invocation (asynchronous host functions are rejected).</summary>
    public TOutput Invoke(TInput input, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(JsPlugin<TInput, TOutput>));
        _gate.Wait(cancellationToken);
        try
        {
            using var engine = new JsEngine(_options.EngineOptions);
            _options.ConfigureEngine?.Invoke(engine);
            var instance = engine.LoadModule(_module, _options.Resolver, cancellationToken: cancellationToken);
            var export = instance.GetExport(_exportName);
            var e = _options.Execution;
            using var execution = engine.BeginExecution(e == null ? null : new JsExecutionOptions { Timeout = e.Timeout, MaxInstructions = e.MaxInstructions, MaxOutputBytes = e.MaxOutputBytes }, cancellationToken);
            var arg = execution.FromObject(input, _input);
            var result = execution.Invoke(export, arg);
            var output = execution.ToObject(result, _output);
            execution.Complete();
            return output;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _gate.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }
}
