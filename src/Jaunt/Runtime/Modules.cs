using System;
using System.Collections.Generic;
using Jaunt.Compiler;

namespace Jaunt.Runtime;

/// <summary>Cyclic Module Record [[Status]]; <c>Errored</c> is "evaluated" with a non-empty [[EvaluationError]].</summary>
internal enum ModuleStatus : byte { New, Linking, Linked, Evaluating, EvaluatingAsync, Evaluated, Errored }

/// <summary>
/// Resolved binding of an import: a slot in another module's environment, or a fixed object (a namespace, deferred
/// namespace or module source object).
/// </summary>
internal sealed class ImportBinding
{
    public Env? Env;
    public int Slot;
    public JsObject? Namespace;
    /// <summary>A module-source binding of a module without a source representation (reading it throws).</summary>
    public ModuleInstance? MissingSourceOf;

    public JsValue Read(Realm realm, string name)
    {
        if (Namespace != null) return JsValue.FromObject(Namespace);
        if (MissingSourceOf != null) throw Modules.NoModuleSource(realm, MissingSourceOf);
        var v = Env!.Slots[Slot];
        if (v.IsEmpty) throw JsErrors.ReferenceError(realm, $"Cannot access '{name}' before initialization");
        return v;
    }
}

/// <summary>Engine-private instance of a compiled module template.</summary>
internal sealed class ModuleInstance
{
    public readonly string Id;
    public readonly ScriptTemplate Template;
    public readonly ModuleRecordInfo Info;
    public readonly Env Env;
    public readonly ImportBinding[] Imports;
    public readonly Dictionary<string, ModuleInstance> Requested = new(StringComparer.Ordinal);
    public ModuleStatus Status;
    public JsValue Error;
    public JsModuleNamespace? Namespace;
    /// <summary>[[DeferredNamespace]]: the namespace of <c>import defer</c> / <c>import.defer()</c>.</summary>
    public JsModuleNamespace? DeferredNamespace;
    /// <summary>[[ModuleSource]] object (host modules with a source representation only; created on first use).</summary>
    public JsObject? ModuleSource;
    public JsObject? Meta;
    public JsClosure? Body;
    public object? Resolver;

    // ECMA-262 16.2.1.5 Cyclic Module Record evaluation state
    public int DfsIndex, DfsAncestorIndex;
    public ModuleInstance? CycleRoot;
    /// <summary>[[AsyncEvaluation]] order (0 = false); records the order modules became async-evaluating.</summary>
    public long AsyncEvaluationOrder;
    public bool AsyncEvaluation => AsyncEvaluationOrder != 0;
    public JsPromise? TopLevelCapability;
    public List<ModuleInstance>? AsyncParentModules;
    public int PendingAsyncDependencies;
    public bool HasTla => Info.HasTopLevelAwait;

    public ModuleInstance(string id, ScriptTemplate template)
    {
        Id = id;
        Template = template;
        Info = template.Module!;
        Env = new Env(null, Info.EnvSize, Info.EnvSize) { Layout = Info.Layout };
        // var/function slots start undefined, lexical slots start in TDZ
        for (int i = 0; i < Info.EnvSize; i++) Env.Slots[i] = JsValue.Undefined;
        foreach (var s in Info.LexicalSlots) Env.Slots[s] = JsValue.Empty;
        Imports = new ImportBinding[Info.Imports.Count];
    }
}

/// <summary>
/// Module namespace exotic object (ECMA-262 10.4.6). A deferred namespace (<c>import defer</c>, [[Deferred]] true)
/// evaluates its module synchronously (EvaluateModuleSync) on the first operation that needs the export list, and
/// treats "then" like a symbol key (never an export).
/// </summary>
internal sealed class JsModuleNamespace : JsObject
{
    private readonly ModuleInstance _module;
    private SortedDictionary<string, ImportBinding> _exports;
    private readonly bool _deferred;

    public JsModuleNamespace(Realm realm, ModuleInstance module, SortedDictionary<string, ImportBinding> exports, bool deferred = false)
        : base(realm, null, exotic: true)
    {
        _module = module;
        _exports = exports;
        _deferred = deferred;
        AddOwnProperty(JsSymbol.ToStringTag, new JsValue(deferred ? "Deferred Module" : "Module"), PropFlags.None);
        Extensible = false;
    }

    public override string ClassName => "Module";
    public bool Deferred => _deferred;
    internal override bool CanCacheNamed(in PropertyKey key) => false;

    /// <summary>IsSymbolLikeNamespaceKey: keys handled by the ordinary object methods.</summary>
    private bool IsSymbolLike(in PropertyKey key) => key.IsSymbol || (_deferred && key.Value is "then");

    /// <summary>GetModuleExportsList: a deferred namespace evaluates its module first.</summary>
    private SortedDictionary<string, ImportBinding> Exports()
    {
        if (_deferred) Modules.EvaluateModuleSync(Realm, _module);
        return _exports;
    }

    public override bool GetOwnProperty(in PropertyKey key, out PropertyDescriptor desc)
    {
        if (IsSymbolLike(key)) return base.GetOwnProperty(key, out desc);
        var s = (string)key.Value;
        if (Exports().TryGetValue(s, out var b)) { desc = PropertyDescriptor.Data(b.Read(Realm, s), PropFlags.Writable | PropFlags.Enumerable); return true; }
        desc = default;
        return false;
    }

    public override bool DefineOwnProperty(in PropertyKey key, in PropertyDescriptor desc)
    {
        if (IsSymbolLike(key)) return OrdinaryDefineOwnProperty(key, desc);
        if (!GetOwnProperty(key, out var cur)) return false;
        if (desc.HasConfigurable && desc.Configurable) return false;
        if (desc.HasEnumerable && !desc.Enumerable) return false;
        if (desc.IsAccessor) return false;
        if (desc.HasWritable && !desc.Writable) return false;
        if (desc.HasValue) return Operations.SameValue(desc.Value, cur.Value);
        return true;
    }

    public override bool HasProperty(in PropertyKey key) => IsSymbolLike(key) ? base.HasProperty(key) : Exports().ContainsKey((string)key.Value);

    public override JsValue Get(in PropertyKey key, JsValue receiver)
    {
        if (IsSymbolLike(key)) return OrdinaryGet(key, receiver);
        var s = (string)key.Value;
        return Exports().TryGetValue(s, out var b) ? b.Read(Realm, s) : JsValue.Undefined;
    }

    public override bool Set(in PropertyKey key, JsValue value, JsValue receiver) => false;
    public override bool Delete(in PropertyKey key) => IsSymbolLike(key) ? base.Delete(key) : !Exports().ContainsKey((string)key.Value);
    public override bool SetPrototypeOf(JsObject? proto) => proto == null;
    public override bool PreventExtensions() => true;
    public override bool IsExtensibleCheck() => false;

    public override void OwnPropertyKeys(List<PropertyKey> keys)
    {
        foreach (var k in Exports().Keys) keys.Add(k);
        keys.Add(JsSymbol.ToStringTag);
    }

    public IEnumerable<string> ExportNames => _exports.Keys;
    internal void SetExports(SortedDictionary<string, ImportBinding> exports) => _exports = exports;
    public ImportBinding? GetBinding(string name) => _exports.TryGetValue(name, out var b) ? b : null;
    public ModuleInstance Module => _module;
}


/// <summary>Module graph loading, linking and evaluation.</summary>
internal static class Modules
{
    public static JsValue ReadImport(Realm realm, ModuleInstance module, int index)
    {
        var b = module.Imports[index];
        if (b.Namespace != null) return JsValue.FromObject(b.Namespace);
        return b.Env!.Slots[b.Slot];
    }

    public static JsObject ImportMeta(Realm realm, ModuleInstance module)
    {
        if (module.Meta != null) return module.Meta;
        var meta = new JsObject(realm, (JsObject?)null);
        meta.CreateDataProperty("url", new JsValue(module.Id));
        return module.Meta = meta;
    }

    /// <summary>True when request <paramref name="i"/> of a module takes part in linking and evaluation (source-phase requests do not).</summary>
    private static bool IsGraphEdge(ModuleRecordInfo info, int i) => info.RequestedPhases[i] != ModulePhase.Source;

    // ------------------------------------------------------------------ module sources (source phase imports)

    /// <summary>[[ModuleSource]] of a module, or null when it has no source representation (all source text modules).</summary>
    public static JsObject? GetModuleSource(Realm realm, ModuleInstance m)
    {
        if (m.Info.SourceKind is not { } kind) return null;
        return m.ModuleSource ??= ModuleSourceBuiltins.CreateModuleSource(realm, kind);
    }

    /// <summary>The SyntaxError of a source phase import of a module without a source representation.</summary>
    public static JsThrowException NoModuleSource(Realm realm, ModuleInstance m)
        => JsErrors.SyntaxError(realm, $"Module '{m.Id}' does not have a source phase representation");

    // ------------------------------------------------------------------ dynamic import

    public static JsValue DynamicImport(Realm realm, ModuleInstance? referrer, JsValue specifier, JsValue options, ModulePhase phase = ModulePhase.Evaluation)
    {
        var promise = new JsPromise(realm);
        string spec;
        try
        {
            spec = Operations.ToString(realm, specifier);
            if (!options.IsUndefined)
            {
                // import(specifier, { with: { type: "json" } })
                if (options.IsNotObject) throw JsErrors.TypeError(realm, "The second argument of import() must be an object");
                var with = options.AsObjectUnchecked.Get("with", options);
                if (!with.IsUndefined)
                {
                    if (with.IsNotObject) throw JsErrors.TypeError(realm, "The 'with' option of import() must be an object");
                    var wo = with.AsObjectUnchecked;
                    var keys = new List<PropertyKey>();
                    wo.OwnPropertyKeys(keys);
                    var attrs = new List<KeyValuePair<string, string>>();
                    foreach (var k in keys)
                    {
                        if (k.Value is not string ks) continue;
                        if (!wo.GetOwnProperty(k, out var d) || !d.Enumerable) continue;
                        var v = wo.Get(k, with);
                        if (!v.IsString) throw JsErrors.TypeError(realm, $"Import attribute '{ks}' must be a string");
                        attrs.Add(new(ks, v.AsStringUnchecked));
                    }
                    foreach (var kv in attrs)
                        if (kv.Key != "type") throw JsErrors.SyntaxError(realm, $"Unsupported import attribute '{kv.Key}'");
                    spec = JsCompiler.RequestKey(spec, attrs);
                }
            }
        }
        catch (JsThrowException ex)
        {
            promise.RejectWith(ex.Value);
            return JsValue.FromObject(promise);
        }
        var engine = realm.Engine!;
        realm.Vm.EnqueueJob(new CallbackJob(r =>
        {
            try
            {
                switch (phase)
                {
                    case ModulePhase.Source:
                    {
                        // import.source(): only the requested module is loaded (not its dependencies), nothing is linked
                        var sm = engine.ResolveModuleForSourceImport(spec, referrer);
                        promise.ResolveWith(JsValue.FromObject(GetModuleSource(r, sm) ?? throw NoModuleSource(r, sm)));
                        break;
                    }
                    case ModulePhase.Defer:
                    {
                        // import.defer(): evaluates only the asynchronous transitive dependencies, then hands out the
                        // deferred namespace (ContinueDynamicImport, defer phase)
                        var m = engine.LoadModuleForDynamicImport(spec, referrer);
                        var list = GatherAsynchronousTransitiveDependencies(m);
                        if (list.Count == 0) { promise.ResolveWith(JsValue.FromObject(GetNamespace(r, m, deferred: true))); break; }
                        var all = new DynamicImportContinuation(promise, m, deferred: true, list.Count);
                        foreach (var dep in list) Evaluate(r, dep).Then(all);
                        break;
                    }
                    default:
                    {
                        var m = engine.LoadModuleForDynamicImport(spec, referrer);
                        Evaluate(r, m).Then(new DynamicImportContinuation(promise, m, deferred: false, 1));
                        break;
                    }
                }
            }
            catch (JsThrowException ex)
            {
                promise.RejectWith(ex.Value);
            }
            catch (JaunModuleResolutionException mre)
            {
                promise.RejectWith(JsValue.FromObject(JsError.Create(r, ErrorKind.TypeError, mre.Message)));
            }
            catch (JsCompileException ce)
            {
                // a module (or one of its dependencies) failed to load: early errors reject with a SyntaxError
                var kind = ce.Code is "ModuleNotFound" or "ModuleTypeMismatch" ? ErrorKind.TypeError : ErrorKind.SyntaxError;
                promise.RejectWith(JsValue.FromObject(JsError.Create(r, kind, ce.Message)));
            }
        }));
        return JsValue.FromObject(promise);
    }

    /// <summary>
    /// Settles a dynamic import once its evaluation promise(s) settle: one Evaluate() promise for import(), all the
    /// asynchronous dependencies' promises for import.defer() (SafePerformPromiseAll: the first rejection wins).
    /// </summary>
    private sealed class DynamicImportContinuation : IPromiseContinuation
    {
        private readonly JsPromise _promise;
        private readonly ModuleInstance _module;
        private readonly bool _deferred;
        private int _remaining;
        public DynamicImportContinuation(JsPromise p, ModuleInstance m, bool deferred, int count) { _promise = p; _module = m; _deferred = deferred; _remaining = count; }
        public void OnSettled(Realm realm, bool fulfilled, JsValue value)
        {
            if (_remaining <= 0) return;
            if (!fulfilled) { _remaining = 0; _promise.RejectWith(value); return; }
            if (--_remaining == 0) _promise.ResolveWith(JsValue.FromObject(GetNamespace(realm, _module, _deferred)));
        }
    }

    // ------------------------------------------------------------------ export resolution

    private sealed class ResolveResult
    {
        public ModuleInstance? Module;
        public int Slot = -1;
        /// <summary>A whole-module binding: namespace, deferred namespace or module source (per <see cref="Phase"/>).</summary>
        public bool IsNamespace;
        public ModulePhase Phase;
        public bool Ambiguous;
    }

    private static ResolveResult? ResolveExport(ModuleInstance m, string name, List<(ModuleInstance, string)> resolveSet)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        foreach (var (rm, rn) in resolveSet)
            if (ReferenceEquals(rm, m) && rn == name) return null; // circular import request
        resolveSet.Add((m, name));
        foreach (var e in m.Info.LocalExports)
            if (e.ExportName == name) return new ResolveResult { Module = m, Slot = e.LocalEnvIndex };
        foreach (var e in m.Info.IndirectExports)
        {
            if (e.ExportName != name) continue;
            var imported = m.Requested[e.ModuleRequest!];
            if (e.ImportName == null) return new ResolveResult { Module = imported, IsNamespace = true, Phase = e.Phase };
            return ResolveExport(imported, e.ImportName, resolveSet);
        }
        if (name == "default") return null;
        ResolveResult? star = null;
        foreach (var e in m.Info.StarExports)
        {
            var imported = m.Requested[e.ModuleRequest!];
            var res = ResolveExport(imported, name, resolveSet);
            if (res == null) continue;
            if (res.Ambiguous) return res;
            if (star == null) star = res;
            else if (!ReferenceEquals(star.Module, res.Module) || star.Slot != res.Slot || star.IsNamespace != res.IsNamespace || star.Phase != res.Phase)
                return new ResolveResult { Ambiguous = true };
        }
        return star;
    }

    private static void GetExportedNames(ModuleInstance m, HashSet<ModuleInstance> visited, List<string> names)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        if (!visited.Add(m)) return;
        foreach (var e in m.Info.LocalExports) names.Add(e.ExportName!);
        foreach (var e in m.Info.IndirectExports) names.Add(e.ExportName!);
        foreach (var e in m.Info.StarExports)
        {
            var sub = new List<string>();
            GetExportedNames(m.Requested[e.ModuleRequest!], visited, sub);
            foreach (var n in sub) if (n != "default" && !names.Contains(n)) names.Add(n);
        }
    }

    /// <summary>The binding a resolved export (or a whole-module import) reads.</summary>
    private static ImportBinding BindingFor(Realm realm, ModuleInstance module, int slot, bool whole, ModulePhase phase)
    {
        if (!whole) return new ImportBinding { Env = module.Env, Slot = slot };
        return phase switch
        {
            ModulePhase.Defer => new ImportBinding { Namespace = GetNamespace(realm, module, deferred: true) },
            ModulePhase.Source => GetModuleSource(realm, module) is { } src
                ? new ImportBinding { Namespace = src }
                : new ImportBinding { MissingSourceOf = module },
            _ => new ImportBinding { Namespace = GetNamespace(realm, module) },
        };
    }

    /// <summary>GetModuleNamespace(module, phase): the (deferred) namespace object, created on first request.</summary>
    public static JsModuleNamespace GetNamespace(Realm realm, ModuleInstance m, bool deferred = false)
    {
        var existing = deferred ? m.DeferredNamespace : m.Namespace;
        if (existing != null) return existing;
        realm.Vm.EnsureNativeStack();
        // publish the (empty) namespace first so cyclic `export * as ns` graphs resolve to the same object
        var ns = new JsModuleNamespace(realm, m, new SortedDictionary<string, ImportBinding>(StringComparer.Ordinal), deferred);
        if (deferred) m.DeferredNamespace = ns;
        else m.Namespace = ns;
        var names = new List<string>();
        GetExportedNames(m, new HashSet<ModuleInstance>(ReferenceEqualityComparer.Instance), names);
        var exports = new SortedDictionary<string, ImportBinding>(StringComparer.Ordinal);
        foreach (var n in names)
        {
            if (deferred && n == "then") continue;   // never an export of a deferred namespace (not thenable)
            var res = ResolveExport(m, n, new List<(ModuleInstance, string)>());
            if (res == null || res.Ambiguous) continue;
            exports[n] = BindingFor(realm, res.Module!, res.Slot, res.IsNamespace, res.Phase);
        }
        ns.SetExports(exports);
        return ns;
    }

    // ------------------------------------------------------------------ linking

    /// <summary>InitializeEnvironment for every module of a loaded graph (DFS).</summary>
    public static void Link(Realm realm, ModuleInstance root)
    {
        var stack = new List<ModuleInstance>();
        LinkInner(realm, root, stack);
        foreach (var m in stack) m.Status = ModuleStatus.Linked;
    }

    private static void LinkInner(Realm realm, ModuleInstance m, List<ModuleInstance> stack)
    {
        realm.Vm.EnsureNativeStack();
        if (m.Status != ModuleStatus.New) return;
        m.Status = ModuleStatus.Linking;
        stack.Add(m);
        var info = m.Info;
        for (int i = 0; i < info.RequestedModules.Count; i++)
            if (IsGraphEdge(info, i)) LinkInner(realm, m.Requested[info.RequestedModules[i]], stack);
        // indirect export validity
        foreach (var e in info.IndirectExports)
        {
            if (e.ImportName == null) continue;
            var res = ResolveExport(m, e.ExportName!, new List<(ModuleInstance, string)>());
            if (res == null || res.Ambiguous)
                throw JsErrors.SyntaxError(realm, $"The requested module '{e.ModuleRequest}' does not provide an export named '{e.ImportName}'");
        }
        for (int i = 0; i < info.Imports.Count; i++)
        {
            var imp = info.Imports[i];
            var target = m.Requested[imp.ModuleRequest];
            ImportBinding binding;
            if (imp.ImportName == null) binding = BindingFor(realm, target, -1, true, imp.Phase);
            else
            {
                var res = ResolveExport(target, imp.ImportName, new List<(ModuleInstance, string)>());
                if (res == null) throw JsErrors.SyntaxError(realm, $"The requested module '{imp.ModuleRequest}' does not provide an export named '{imp.ImportName}'");
                if (res.Ambiguous) throw JsErrors.SyntaxError(realm, $"The requested module '{imp.ModuleRequest}' contains conflicting star exports for name '{imp.ImportName}'");
                binding = BindingFor(realm, res.Module!, res.Slot, res.IsNamespace, res.Phase);
            }
            // a source phase import of a module without a source representation is a link-time SyntaxError
            if (binding.MissingSourceOf != null) throw NoModuleSource(realm, binding.MissingSourceOf);
            m.Imports[i] = binding;
        }
        // module body closure and hoisted functions
        var fb = realm.FeedbackFor(m.Template);
        var body = realm.Vm.CreateClosure(m.Template.Body, m.Env, fb, null);
        body.Module = m;
        m.Body = body;
        foreach (var (fi, slot) in info.HoistedFunctions)
        {
            var t = m.Template.Body.Functions[fi];
            var fn = realm.Vm.CreateClosure(t, m.Env, fb.Child(fi), null);
            fn.Module = m;
            m.Env.Slots[slot] = JsValue.FromObject(fn);
        }
    }

    // ------------------------------------------------------------------ evaluation (ECMA-262 16.2.1.5.3)

    private static long s_asyncEvaluationCounter;

    /// <summary>True when any module in the not-yet-evaluated graph uses top-level await.</summary>
    public static bool RequiresAsync(ModuleInstance root)
    {
        var seen = new HashSet<ModuleInstance>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<ModuleInstance>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var m = stack.Pop();
            if (!seen.Add(m) || m.Status is ModuleStatus.Evaluated or ModuleStatus.Errored or ModuleStatus.EvaluatingAsync) continue;
            if (m.Info.HasTopLevelAwait) return true;
            var info = m.Info;
            for (int i = 0; i < info.RequestedModules.Count; i++)
                if (IsGraphEdge(info, i)) stack.Push(m.Requested[info.RequestedModules[i]]);
        }
        return false;
    }

    /// <summary>Evaluate(): returns the top-level promise of the module's cycle root.</summary>
    public static JsPromise Evaluate(Realm realm, ModuleInstance module)
    {
        if (module.Status is ModuleStatus.EvaluatingAsync or ModuleStatus.Evaluated or ModuleStatus.Errored)
            module = module.CycleRoot ?? module;
        if (module.TopLevelCapability != null) return module.TopLevelCapability;
        var stack = new List<ModuleInstance>();
        var capability = new JsPromise(realm);
        module.TopLevelCapability = capability;
        try
        {
            InnerModuleEvaluation(realm, module, stack, 0);
            if (!module.AsyncEvaluation) capability.ResolveWith(JsValue.Undefined);
        }
        catch (JsThrowException ex)
        {
            foreach (var m in stack)
            {
                m.Status = ModuleStatus.Errored;
                m.Error = ex.Value;
            }
            capability.RejectWith(ex.Value);
        }
        return capability;
    }

    /// <summary>Synchronously evaluates a module graph that has no top-level await (throws the evaluation error).</summary>
    public static void EvaluateSync(Realm realm, ModuleInstance root)
    {
        var p = Evaluate(realm, root);
        if (p.State == PromiseState.Rejected) throw new JsThrowException(p.Result);
    }

    public static JsPromise EvaluateAsync(Realm realm, ModuleInstance root) => Evaluate(realm, root);

    /// <summary>IsModuleSCCEvaluated: the module's strongly connected component finished evaluating (possibly with an error).</summary>
    private static bool IsSccEvaluated(ModuleInstance m)
        => (m.CycleRoot ?? m).Status is ModuleStatus.Evaluated or ModuleStatus.Errored;

    /// <summary>
    /// EvaluateModuleSync (import defer): evaluates a deferred module on first access. Throws a TypeError when the
    /// module cannot be evaluated synchronously (it is already being evaluated, or its graph needs top-level await).
    /// </summary>
    public static void EvaluateModuleSync(Realm realm, ModuleInstance m)
    {
        if (m.Status == ModuleStatus.Evaluated && (m.CycleRoot == null || m.CycleRoot.Status == ModuleStatus.Evaluated)) return;
        if (!ReadyForSyncExecution(m, new HashSet<ModuleInstance>(ReferenceEqualityComparer.Instance)))
            throw JsErrors.TypeError(realm, $"Deferred module '{m.Id}' cannot be evaluated synchronously: it is being evaluated or depends on top-level await");
        var p = Evaluate(realm, m);
        if (p.State == PromiseState.Rejected)
        {
            if (!p.IsHandled) { realm.TrackRejection(p, handled: true); p.IsHandled = true; }
            throw new JsThrowException(p.Result);
        }
    }

    private static bool ReadyForSyncExecution(ModuleInstance m, HashSet<ModuleInstance> seen)
    {
        EnsureStack();
        if (!seen.Add(m)) return true;
        if (IsSccEvaluated(m)) return true;
        if (m.Status is ModuleStatus.Evaluating or ModuleStatus.EvaluatingAsync) return false;
        if (m.HasTla) return false;
        var info = m.Info;
        for (int i = 0; i < info.RequestedModules.Count; i++)
            if (IsGraphEdge(info, i) && !ReadyForSyncExecution(m.Requested[info.RequestedModules[i]], seen)) return false;
        return true;
    }

    private static void EnsureStack() => System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();

    /// <summary>
    /// GatherAsynchronousTransitiveDependencies: the not-yet-evaluated top-level-await modules reachable from
    /// <paramref name="module"/> without passing through another such module (they are evaluated eagerly even when
    /// <paramref name="module"/> itself is deferred).
    /// </summary>
    public static List<ModuleInstance> GatherAsynchronousTransitiveDependencies(ModuleInstance module)
    {
        var result = new List<ModuleInstance>();
        Gather(module, new HashSet<ModuleInstance>(ReferenceEqualityComparer.Instance), result);
        return result;
    }

    private static void Gather(ModuleInstance m, HashSet<ModuleInstance> seen, List<ModuleInstance> result)
    {
        EnsureStack();
        if (!seen.Add(m)) return;
        if (m.Status == ModuleStatus.Evaluating || IsSccEvaluated(m)) return;
        if (m.HasTla)
        {
            if (!result.Contains(m)) result.Add(m);
            return;
        }
        var info = m.Info;
        for (int i = 0; i < info.RequestedModules.Count; i++)
            if (IsGraphEdge(info, i)) Gather(m.Requested[info.RequestedModules[i]], seen, result);
    }

    private static int InnerModuleEvaluation(Realm realm, ModuleInstance module, List<ModuleInstance> stack, int index)
    {
        realm.Vm.EnsureNativeStack();
        if (module.Status is ModuleStatus.EvaluatingAsync or ModuleStatus.Evaluated) return index;
        if (module.Status == ModuleStatus.Errored) throw new JsThrowException(module.Error);
        if (module.Status == ModuleStatus.Evaluating) return index;
        module.Status = ModuleStatus.Evaluating;
        module.DfsIndex = index;
        module.DfsAncestorIndex = index;
        module.PendingAsyncDependencies = 0;
        index++;
        var info = module.Info;
        // evaluation list: deferred requests contribute only their asynchronous transitive dependencies
        List<ModuleInstance>? evaluationList = null;
        if (info.RequestedPhases.Contains(ModulePhase.Defer))
        {
            evaluationList = new List<ModuleInstance>();
            for (int i = 0; i < info.RequestedModules.Count; i++)
            {
                var required = module.Requested[info.RequestedModules[i]];
                switch (info.RequestedPhases[i])
                {
                    case ModulePhase.Defer:
                        foreach (var additional in GatherAsynchronousTransitiveDependencies(required))
                            if (!evaluationList.Contains(additional)) evaluationList.Add(additional);
                        break;
                    case ModulePhase.Evaluation:
                        if (!evaluationList.Contains(required)) evaluationList.Add(required);
                        break;
                }
            }
        }
        stack.Add(module);
        if (evaluationList != null)
        {
            foreach (var required in evaluationList) index = EvaluateDependency(realm, module, required, stack, index);
        }
        else
        {
            for (int i = 0; i < info.RequestedModules.Count; i++)
                if (IsGraphEdge(info, i)) index = EvaluateDependency(realm, module, module.Requested[info.RequestedModules[i]], stack, index);
        }
        if (module.PendingAsyncDependencies > 0 || module.HasTla)
        {
            module.AsyncEvaluationOrder = ++s_asyncEvaluationCounter;
            if (module.PendingAsyncDependencies == 0) ExecuteAsyncModule(realm, module);
        }
        else
        {
            ExecuteModule(module);
        }
        if (module.DfsAncestorIndex == module.DfsIndex)
        {
            while (true)
            {
                var required = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                required.Status = required.AsyncEvaluation ? ModuleStatus.EvaluatingAsync : ModuleStatus.Evaluated;
                required.CycleRoot = module;
                if (ReferenceEquals(required, module)) break;
            }
        }
        return index;
    }

    private static int EvaluateDependency(Realm realm, ModuleInstance module, ModuleInstance required, List<ModuleInstance> stack, int index)
    {
        index = InnerModuleEvaluation(realm, required, stack, index);
        if (required.Status == ModuleStatus.Evaluating)
            module.DfsAncestorIndex = Math.Min(module.DfsAncestorIndex, required.DfsAncestorIndex);
        else
        {
            required = required.CycleRoot ?? required;
            if (required.Status == ModuleStatus.Errored) throw new JsThrowException(required.Error);
        }
        if (required.AsyncEvaluation)
        {
            module.PendingAsyncDependencies++;
            (required.AsyncParentModules ??= new List<ModuleInstance>()).Add(module);
        }
        return index;
    }

    private static void ExecuteModule(ModuleInstance module) => module.Body!.Call(JsValue.Undefined, ReadOnlySpan<JsValue>.Empty);

    private static void ExecuteAsyncModule(Realm realm, ModuleInstance module)
    {
        var p = Coroutines.StartAsyncBody(realm.Vm, module.Body!, JsValue.Undefined);
        p.Then(new AsyncModuleContinuation(module));
    }

    private sealed class AsyncModuleContinuation(ModuleInstance module) : IPromiseContinuation
    {
        public void OnSettled(Realm realm, bool fulfilled, JsValue value)
        {
            if (fulfilled) AsyncModuleExecutionFulfilled(realm, module);
            else AsyncModuleExecutionRejected(realm, module, value);
        }
    }

    private static void AsyncModuleExecutionFulfilled(Realm realm, ModuleInstance module)
    {
        if (module.Status == ModuleStatus.Errored) return;
        module.AsyncEvaluationOrder = 0;
        module.Status = ModuleStatus.Evaluated;
        module.TopLevelCapability?.ResolveWith(JsValue.Undefined);
        var execList = new List<ModuleInstance>();
        GatherAvailableAncestors(module, execList);
        execList.Sort((a, b) => a.AsyncEvaluationOrder.CompareTo(b.AsyncEvaluationOrder));
        foreach (var m in execList)
        {
            if (m.Status == ModuleStatus.Errored) continue;
            if (m.HasTla) { ExecuteAsyncModule(realm, m); continue; }
            try { ExecuteModule(m); }
            catch (JsThrowException ex)
            {
                AsyncModuleExecutionRejected(realm, m, ex.Value);
                continue;
            }
            m.AsyncEvaluationOrder = 0;
            m.Status = ModuleStatus.Evaluated;
            m.TopLevelCapability?.ResolveWith(JsValue.Undefined);
        }
    }

    private static void GatherAvailableAncestors(ModuleInstance module, List<ModuleInstance> execList)
    {
        if (module.AsyncParentModules == null) return;
        foreach (var m in module.AsyncParentModules)
        {
            if (execList.Contains(m) || (m.CycleRoot ?? m).Status == ModuleStatus.Errored) continue;
            m.PendingAsyncDependencies--;
            if (m.PendingAsyncDependencies == 0)
            {
                execList.Add(m);
                if (!m.HasTla) GatherAvailableAncestors(m, execList);
            }
        }
    }

    private static void AsyncModuleExecutionRejected(Realm realm, ModuleInstance module, JsValue error)
    {
        if (module.Status == ModuleStatus.Errored) return;
        module.Error = error;
        module.Status = ModuleStatus.Errored;
        // the module's own capability settles before its async parents (observable ordering)
        module.TopLevelCapability?.RejectWith(error);
        if (module.AsyncParentModules != null)
            foreach (var m in module.AsyncParentModules) AsyncModuleExecutionRejected(realm, m, error);
    }
}

internal sealed class JaunModuleResolutionException : Exception
{
    public JaunModuleResolutionException(string message) : base(message) { }
}
