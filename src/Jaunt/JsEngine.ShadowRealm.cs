using System;
using System.Collections.Generic;
using Jaunt.Runtime;

namespace Jaunt;

public sealed partial class JsEngine
{
    /// <summary>
    /// Loads and links a module graph in a ShadowRealm's realm (ShadowRealm.prototype.importValue). Each such realm
    /// has its own module map, so the modules are instantiated (and later evaluated) separately from the engine's.
    /// Specifiers resolve through the engine's module resolver, without a referrer.
    /// </summary>
    internal ModuleInstance LoadModuleForRealm(Realm realm, string specifier)
    {
        if (_options.ModuleResolver is not IJsModuleResolver resolver)
            throw new JaunModuleResolutionException($"Cannot resolve module '{specifier}': no module resolver is available");
        var src = resolver.Resolve(specifier, null) ?? throw new JaunModuleResolutionException($"Cannot find module '{specifier}'");
        var noAttributes = new Dictionary<string, string>(StringComparer.Ordinal);
        CheckModuleType(src, noAttributes, dynamic: true);
        var map = realm.ModuleMap ??= new Dictionary<string, ModuleInstance>(StringComparer.Ordinal);
        ModuleInstance InstantiateIn(string id, JsModule module)
        {
            if (map.TryGetValue(id, out var existing)) return existing;
            if (map.Count >= _options.Limits.MaxModules)
                throw new JsLimitExceededException(JsLimitKind.Modules, $"Module graph exceeds the limit of {_options.Limits.MaxModules} modules.", map.Count + 1, _options.Limits.MaxModules);
            var m = new ModuleInstance(id, module.Template) { Resolver = resolver };
            map[id] = m;
            return m;
        }
        var root = InstantiateIn(src.Id, src.Module);
        var queue = new Queue<ModuleInstance>();
        queue.Enqueue(root);
        var seen = new HashSet<ModuleInstance>(ReferenceEqualityComparer.Instance);
        while (queue.Count > 0)
        {
            var m = queue.Dequeue();
            if (!seen.Add(m)) continue;
            foreach (var spec in m.Info.RequestedModules)
            {
                if (m.Requested.ContainsKey(spec)) continue;
                var (depSpecifier, attrs) = JsCompiler.SplitRequestKey(spec);
                var dsrc = (attrs.Count == 0 ? resolver.Resolve(depSpecifier, m.Id) : resolver.Resolve(depSpecifier, m.Id, attrs))
                    ?? throw new JsCompileException("ModuleNotFound", $"Cannot resolve module '{depSpecifier}' imported from '{m.Id}'", m.Id, 0, 0);
                CheckModuleType(dsrc, attrs);
                var dep = InstantiateIn(dsrc.Id, dsrc.Module);
                m.Requested[spec] = dep;
                queue.Enqueue(dep);
            }
        }
        if (root.Status == ModuleStatus.New)
        {
            // module closures (and namespace objects) belong to the shadow realm
            var saved = Vm.Realm;
            Vm.Realm = realm;
            try { Modules.Link(realm, root); }
            finally { Vm.Realm = saved; }
        }
        return root;
    }
}
