using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jaunt.Compiler;
using Jaunt.Runtime;

namespace Jaunt;

/// <summary>A module returned by a resolver: canonical id plus compiled template.</summary>
public sealed class JsModuleSource
{
    /// <summary>Canonical module id (identity within the resolver namespace).</summary>
    public string Id { get; }
    /// <summary>Compiled module template.</summary>
    public JsModule Module { get; }

    /// <summary>Creates a module source.</summary>
    public JsModuleSource(string id, JsModule module)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Module = module ?? throw new ArgumentNullException(nameof(module));
    }
}

/// <summary>Synchronous module resolver. The default (null) resolver denies every import.</summary>
public interface IJsModuleResolver
{
    /// <summary>Resolves <paramref name="specifier"/> requested by <paramref name="referrerId"/>; returns null when not found.</summary>
    JsModuleSource? Resolve(string specifier, string? referrerId);

    /// <summary>
    /// Resolves a request that carries import attributes (e.g. <c>with { type: "json" }</c>). The default ignores the
    /// attributes; for <c>type: "json"</c> the engine requires the result to be a JSON module
    /// (<see cref="JsCompiler.CompileJsonModule"/>).
    /// </summary>
    JsModuleSource? Resolve(string specifier, string? referrerId, IReadOnlyDictionary<string, string> attributes) => Resolve(specifier, referrerId);
}

/// <summary>Asynchronous module resolver (used by <c>JsEngine.LoadModuleAsync</c>).</summary>
public interface IJsAsyncModuleResolver
{
    /// <summary>Resolves a module asynchronously; returns null when not found.</summary>
    ValueTask<JsModuleSource?> ResolveAsync(string specifier, string? referrerId, CancellationToken cancellationToken);

    /// <summary>Resolves a request with import attributes (see <see cref="IJsModuleResolver"/>).</summary>
    ValueTask<JsModuleSource?> ResolveAsync(string specifier, string? referrerId, IReadOnlyDictionary<string, string> attributes, CancellationToken cancellationToken)
        => ResolveAsync(specifier, referrerId, cancellationToken);
}

/// <summary>Resolver backed by an in-memory dictionary of module sources (keys are canonical ids).</summary>
public sealed class JsDictionaryModuleResolver : IJsModuleResolver
{
    private readonly Dictionary<string, JsModule> _modules = new(StringComparer.Ordinal);

    /// <summary>Adds (or replaces) a module compiled from source.</summary>
    public JsDictionaryModuleResolver Add(string id, string source)
    {
        _modules[id] = JsCompiler.CompileModule(source, new JsCompileOptions { SourceName = id });
        return this;
    }

    /// <summary>Adds (or replaces) a JSON module (imported with <c>with { type: "json" }</c>).</summary>
    public JsDictionaryModuleResolver AddJson(string id, string json)
    {
        _modules[id] = JsCompiler.CompileJsonModule(json, new JsCompileOptions { SourceName = id });
        return this;
    }

    /// <summary>Adds (or replaces) a text module (imported with <c>with { type: "text" }</c>).</summary>
    public JsDictionaryModuleResolver AddText(string id, string text)
    {
        _modules[id] = JsCompiler.CreateTextModule(text, new JsCompileOptions { SourceName = id });
        return this;
    }

    /// <summary>Adds (or replaces) a bytes module (imported with <c>with { type: "bytes" }</c>).</summary>
    public JsDictionaryModuleResolver AddBytes(string id, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        _modules[id] = JsCompiler.CreateBytesModule(bytes, new JsCompileOptions { SourceName = id });
        return this;
    }

    /// <summary>Adds (or replaces) a compiled module.</summary>
    public JsDictionaryModuleResolver Add(string id, JsModule module)
    {
        _modules[id] = module;
        return this;
    }

    /// <inheritdoc />
    public JsModuleSource? Resolve(string specifier, string? referrerId)
    {
        string id = specifier;
        if ((specifier.StartsWith("./", StringComparison.Ordinal) || specifier.StartsWith("../", StringComparison.Ordinal)) && referrerId != null)
            id = Combine(referrerId, specifier);
        return _modules.TryGetValue(id, out var m) ? new JsModuleSource(id, m) : null;
    }

    private static string Combine(string referrer, string relative)
    {
        var parts = new List<string>(referrer.Split('/'));
        if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
        foreach (var seg in relative.Split('/'))
        {
            if (seg == "." || seg.Length == 0) continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        return string.Join('/', parts);
    }
}

public sealed partial class JsEngine
{
    private readonly Dictionary<string, ModuleInstance> _modules = new(StringComparer.Ordinal);

    private ModuleInstance Instantiate(string id, JsModule module, object? resolver)
    {
        if (_modules.TryGetValue(id, out var existing))
        {
            if (!ReferenceEquals(existing.Template, module.Template))
                throw new InvalidOperationException($"Module id '{id}' is already bound to a different module template in this engine.");
            return existing;
        }
        if (_modules.Count >= _options.Limits.MaxModules)
            throw new JsLimitExceededException(JsLimitKind.Modules, $"Module graph exceeds the limit of {_options.Limits.MaxModules} modules.", _modules.Count + 1, _options.Limits.MaxModules);
        var m = new ModuleInstance(id, module.Template) { Resolver = resolver };
        _modules[id] = m;
        return m;
    }

    /// <summary>
    /// A <c>type: "json"</c> / <c>type: "text"</c> request must load a JSON / text module, and those modules need that
    /// attribute.
    /// </summary>
    private static void CheckModuleType(JsModuleSource src, Dictionary<string, string> attrs, bool dynamic = false)
    {
        attrs.TryGetValue("type", out var type);
        string? actual = src.Module.IsJsonModule ? "json" : src.Module.IsTextModule ? "text" : src.Module.IsBytesModule ? "bytes" : null;
        string? error = null;
        if (type != null && type != "json" && type != "text" && type != "bytes") error = $"Unsupported module type '{type}' for '{src.Id}'";
        else if (type != actual)
            error = type != null
                ? $"Module '{src.Id}' was imported with type '{type}' but is not a {type.ToUpperInvariant()} module"
                : $"{actual!.ToUpperInvariant()} module '{src.Id}' must be imported with {{ type: \"{actual}\" }}";
        if (error == null) return;
        if (dynamic) throw new JaunModuleResolutionException(error);
        throw new JsCompileException("ModuleTypeMismatch", error, src.Id, 0, 0);
    }

    private ModuleInstance LoadGraphSync(string rootId, JsModule module, IJsModuleResolver? resolver)
    {
        var root = Instantiate(rootId, module, resolver);
        var queue = new Queue<ModuleInstance>();
        queue.Enqueue(root);
        var seen = new HashSet<ModuleInstance>(ReferenceEqualityComparer.Instance);
        while (queue.Count > 0)
        {
            var m = queue.Dequeue();
            if (!seen.Add(m)) continue;
            for (int i = 0; i < m.Info.RequestedModules.Count; i++)
            {
                var spec = m.Info.RequestedModules[i];
                if (!m.Requested.TryGetValue(spec, out var dep))
                {
                    var (specifier, attrs) = JsCompiler.SplitRequestKey(spec);
                    var src = (attrs.Count == 0 ? resolver?.Resolve(specifier, m.Id) : resolver?.Resolve(specifier, m.Id, attrs))
                        ?? throw new JsCompileException("ModuleNotFound", $"Cannot resolve module '{specifier}' imported from '{m.Id}'", m.Id, 0, 0);
                    CheckModuleType(src, attrs);
                    dep = Instantiate(src.Id, src.Module, resolver);
                    m.Requested[spec] = dep;
                }
                // a source phase import loads only the module itself, not its dependencies
                if (m.Info.RequestedPhases[i] != ModulePhase.Source) queue.Enqueue(dep);
            }
        }
        return root;
    }

    private async ValueTask<ModuleInstance> LoadGraphAsync(string rootId, JsModule module, IJsAsyncModuleResolver resolver, CancellationToken ct)
    {
        var root = Instantiate(rootId, module, resolver);
        var queue = new Queue<ModuleInstance>();
        queue.Enqueue(root);
        var seen = new HashSet<ModuleInstance>(ReferenceEqualityComparer.Instance);
        while (queue.Count > 0)
        {
            var m = queue.Dequeue();
            if (!seen.Add(m)) continue;
            for (int i = 0; i < m.Info.RequestedModules.Count; i++)
            {
                var spec = m.Info.RequestedModules[i];
                if (!m.Requested.TryGetValue(spec, out var dep))
                {
                    var (specifier, attrs) = JsCompiler.SplitRequestKey(spec);
                    var src = await (attrs.Count == 0 ? resolver.ResolveAsync(specifier, m.Id, ct) : resolver.ResolveAsync(specifier, m.Id, attrs, ct)).ConfigureAwait(false)
                        ?? throw new JsCompileException("ModuleNotFound", $"Cannot resolve module '{specifier}' imported from '{m.Id}'", m.Id, 0, 0);
                    CheckModuleType(src, attrs);
                    dep = Instantiate(src.Id, src.Module, resolver);
                    m.Requested[spec] = dep;
                }
                // a source phase import loads only the module itself, not its dependencies
                if (m.Info.RequestedPhases[i] != ModulePhase.Source) queue.Enqueue(dep);
            }
        }
        return root;
    }

    /// <summary>
    /// Loads, links, instantiates and synchronously evaluates a module graph. Graphs that need asynchronous
    /// evaluation (top-level await) are rejected before any module code runs; use <c>LoadModuleAsync</c>.
    /// </summary>
    public JsModuleInstance LoadModule(JsModule module, IJsModuleResolver? resolver = null, string? id = null, CancellationToken cancellationToken = default)
        => LoadModuleBudgeted(module, resolver, id, null, cancellationToken);

    /// <summary>LoadModule under an execution budget (e.g. a plugin invocation's, which covers module initialization).</summary>
    internal JsModuleInstance LoadModuleBudgeted(JsModule module, IJsModuleResolver? resolver, string? id, JsExecutionOptions? budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(module);
        resolver ??= _options.ModuleResolver;
        var root = BeginRoot(budget, cancellationToken, JsExecutionMode.Sync);
        try
        {
            var m = LoadGraphSync(id ?? module.SourceName, module, resolver);
            if (m.Status == ModuleStatus.New) Modules.Link(Realm, m);
            if (Modules.RequiresAsync(m))
                throw new JsAsyncRequiredException($"Module graph of '{m.Id}' uses top-level await and must be loaded with LoadModuleAsync.");
            Modules.EvaluateSync(Realm, m);
            CheckpointRoot(root);
            CloseRoot(root, true);
            return new JsModuleInstance(this, m);
        }
        catch (Exception ex) { throw MapException(ex, root); }
        finally { EndRoot(root); }
    }

    /// <summary>Loads a module graph with a synchronous resolver, allowing top-level await.</summary>
    public ValueTask<JsModuleInstance> LoadModuleAsync(JsModule module, IJsModuleResolver? resolver = null, string? id = null, CancellationToken cancellationToken = default)
    {
        resolver ??= _options.ModuleResolver;
        return LoadModuleAsyncCore(module, resolver == null ? null : new SyncResolverAdapter(resolver), id, cancellationToken);
    }

    internal ValueTask<JsModuleInstance> LoadModuleAsyncBudgeted(JsModule module, IJsModuleResolver? resolver, JsExecutionOptions? budget, CancellationToken cancellationToken)
    {
        resolver ??= _options.ModuleResolver;
        return LoadModuleAsyncCore(module, resolver == null ? null : new SyncResolverAdapter(resolver), null, cancellationToken, budget);
    }

    /// <summary>Loads a module graph with an asynchronous resolver, allowing top-level await.</summary>
    public ValueTask<JsModuleInstance> LoadModuleAsync(JsModule module, IJsAsyncModuleResolver resolver, string? id = null, CancellationToken cancellationToken = default)
        => LoadModuleAsyncCore(module, resolver, id, cancellationToken);

    private sealed class SyncResolverAdapter : IJsAsyncModuleResolver, IJsModuleResolver
    {
        private readonly IJsModuleResolver _inner;
        public SyncResolverAdapter(IJsModuleResolver inner) => _inner = inner;
        public JsModuleSource? Resolve(string specifier, string? referrerId) => _inner.Resolve(specifier, referrerId);
        public JsModuleSource? Resolve(string specifier, string? referrerId, IReadOnlyDictionary<string, string> attributes)
            => _inner.Resolve(specifier, referrerId, attributes);   // dynamic import() keeps import attributes
        public ValueTask<JsModuleSource?> ResolveAsync(string specifier, string? referrerId, CancellationToken cancellationToken)
            => new(_inner.Resolve(specifier, referrerId));
        public ValueTask<JsModuleSource?> ResolveAsync(string specifier, string? referrerId, IReadOnlyDictionary<string, string> attributes, CancellationToken cancellationToken)
            => new(_inner.Resolve(specifier, referrerId, attributes));
    }

    private sealed class DenyResolver : IJsAsyncModuleResolver
    {
        public static readonly DenyResolver Instance = new();
        public ValueTask<JsModuleSource?> ResolveAsync(string specifier, string? referrerId, CancellationToken cancellationToken) => new((JsModuleSource?)null);
    }

    private async ValueTask<JsModuleInstance> LoadModuleAsyncCore(JsModule module, IJsAsyncModuleResolver? resolver, string? id, CancellationToken ct, JsExecutionOptions? budget = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        var root = BeginRoot(budget ?? new JsExecutionOptions { Mode = JsExecutionMode.Async }, ct, JsExecutionMode.Async);
        try
        {
            var m = await LoadGraphAsync(id ?? module.SourceName, module, resolver ?? DenyResolver.Instance, ct).ConfigureAwait(false);
            if (m.Status == ModuleStatus.New) Modules.Link(Realm, m);
            var p = Modules.EvaluateAsync(Realm, m);
            await PumpUntilSettledAsync(p, root).ConfigureAwait(false);
            if (p.State == PromiseState.Rejected) throw new JsThrowException(p.Result);
            CheckpointRoot(root);
            CloseRoot(root, true);
            return new JsModuleInstance(this, m);
        }
        catch (Exception ex) { throw MapException(ex, root); }
        finally { EndRoot(root); }
    }

    /// <summary>Dynamic import(): resolves through the referrer's resolver (synchronous resolvers only).</summary>
    internal ModuleInstance LoadModuleForDynamicImport(string specifier, ModuleInstance? referrer)
    {
        var (resolver, src) = ResolveForDynamicImport(specifier, referrer);
        var m = LoadGraphSync(src.Id, src.Module, resolver);
        if (m.Status == ModuleStatus.New) Modules.Link(Realm, m);
        return m;
    }

    /// <summary><c>import.source()</c>: resolves and instantiates the module only (no dependencies, no linking).</summary>
    internal ModuleInstance ResolveModuleForSourceImport(string specifier, ModuleInstance? referrer)
    {
        var (resolver, src) = ResolveForDynamicImport(specifier, referrer);
        return Instantiate(src.Id, src.Module, resolver);
    }

    private (IJsModuleResolver Resolver, JsModuleSource Source) ResolveForDynamicImport(string specifier, ModuleInstance? referrer)
    {
        var resolverObj = referrer?.Resolver ?? _options.ModuleResolver;
        IJsModuleResolver? resolver = resolverObj as IJsModuleResolver;
        if (resolver == null)
            throw new JaunModuleResolutionException(resolverObj == null
                ? $"Cannot resolve module '{specifier}': no module resolver is available for dynamic import"
                : $"Cannot resolve module '{specifier}': dynamic import requires a synchronous resolver");
        var (spec, attrs) = JsCompiler.SplitRequestKey(specifier);
        var src = (attrs.Count == 0 ? resolver.Resolve(spec, referrer?.Id) : resolver.Resolve(spec, referrer?.Id, attrs))
            ?? throw new JaunModuleResolutionException($"Cannot find module '{spec}'");
        CheckModuleType(src, attrs, dynamic: true);
        return (resolver, src);
    }
}
