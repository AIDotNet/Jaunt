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
            var budget = new InvocationBudget(_options.Execution);
            var instance = _module.Template.Module!.HasTopLevelAwait
                ? await engine.LoadModuleAsyncBudgeted(_module, _options.Resolver, budget.Next(engine, JsExecutionMode.Async), cancellationToken).ConfigureAwait(false)
                : engine.LoadModuleBudgeted(_module, _options.Resolver, null, budget.Next(engine, JsExecutionMode.Sync), cancellationToken);
            var export = instance.GetExport(_exportName);
            await using var execution = engine.BeginExecution(budget.Next(engine, JsExecutionMode.Async), cancellationToken);
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
            var budget = new InvocationBudget(_options.Execution);
            var instance = engine.LoadModuleBudgeted(_module, _options.Resolver, null, budget.Next(engine, JsExecutionMode.Sync), cancellationToken);
            var export = instance.GetExport(_exportName);
            using var execution = engine.BeginExecution(budget.Next(engine, JsExecutionMode.Sync), cancellationToken);
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

    /// <summary>
    /// One invocation's budget (<see cref="JsPluginOptions.Execution"/>) spread over its two engine roots, module
    /// initialization and the call: the call gets what initialization left of the time and work budgets.
    /// </summary>
    private struct InvocationBudget
    {
        private readonly JsExecutionOptions? _options;
        private readonly long _start;
        private long _workUsed;

        public InvocationBudget(JsExecutionOptions? options)
        {
            _options = options;
            _start = System.Diagnostics.Stopwatch.GetTimestamp();
            _workUsed = 0;
        }

        public JsExecutionOptions Next(JsEngine engine, JsExecutionMode mode)
        {
            var e = _options;
            if (e == null) return new JsExecutionOptions { Mode = mode };
            _workUsed += engine.GetStatistics().LastWorkUnits;
            TimeSpan? timeout = e.Timeout is { } t ? Max(t - System.Diagnostics.Stopwatch.GetElapsedTime(_start), TimeSpan.FromTicks(1)) : null;
            long work = e.MaxInstructions > 0 ? Math.Max(e.MaxInstructions - _workUsed, 1) : 0;
            return new JsExecutionOptions { Timeout = timeout, MaxInstructions = work, MaxOutputBytes = e.MaxOutputBytes, Mode = mode };
        }

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    }

    /// <inheritdoc />
    /// <remarks>
    /// New invocations are rejected afterwards; invocations already running complete normally (their engines are their
    /// own). The concurrency gate is not disposed, so an in-flight call can still release it; SemaphoreSlim holds no OS
    /// handle unless its AvailableWaitHandle is used.
    /// </remarks>
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }
}
