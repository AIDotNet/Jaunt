using System;
using System.Collections.Generic;

namespace Jaunt.Runtime;

internal sealed class JsWeakRef : JsObject
{
    public readonly WeakReference<object> Target;
    public JsWeakRef(Realm realm, JsObject proto, object target) : base(realm, proto) => Target = new WeakReference<object>(target);
    public override string ClassName => "WeakRef";
}

internal sealed class JsFinalizationRegistry : JsObject
{
    public sealed class Cell
    {
        public required WeakReference<object> Target;
        public JsValue HeldValue;
        public object? UnregisterToken;
    }

    public readonly JsValue Cleanup;
    public readonly List<Cell> Cells = new();

    public JsFinalizationRegistry(Realm realm, JsObject proto, JsValue cleanup) : base(realm, proto) => Cleanup = cleanup;
    public override string ClassName => "FinalizationRegistry";
}

/// <summary>WeakRef and FinalizationRegistry (26.1, 26.2). Liveness follows the .NET GC; collection is never observable synchronously.</summary>
internal static class WeakRefBuiltins
{
    /// <summary>CanBeHeldWeakly (9.13): objects and symbols not in the global registry.</summary>
    public static object? WeakTarget(in JsValue v)
    {
        if (v.IsObject) return v.Ref;
        if (v.IsSymbol && ((JsSymbol)v.Ref!).RegistryKey == null) return v.Ref;
        return null;
    }

    private static JsValue FromTarget(object o) => o is JsObject jo ? JsValue.FromObject(jo) : new JsValue(JsTag.Symbol, 0, o);

    public static void Setup(Realm r)
    {
        var wrProto = new JsObject(r, r.ObjectPrototype);
        r.WeakRefPrototype = wrProto;
        var wr = r.Constructor("WeakRef", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (!a.IsConstruct) throw JsErrors.TypeError(realm, "Constructor WeakRef requires 'new'");
            var target = WeakTarget(a[0]) ?? throw JsErrors.TypeError(realm, "WeakRef: invalid target");
            var p = Operations.GetPrototypeFromConstructor(realm, a.NewTarget.AsObjectUnchecked, realm.WeakRefPrototype!);
            realm.KeepAlive(target);
            return JsValue.FromObject(new JsWeakRef(realm, p, target));
        }, wrProto);
        r.DefineGlobal("WeakRef", JsValue.FromObject(wr));
        r.Method(wrProto, "deref", 0, static (in NativeArgs a) =>
        {
            if (a.This.Ref is not JsWeakRef w) throw JsErrors.TypeError(a.Realm, "WeakRef.prototype.deref called on incompatible receiver");
            if (!w.Target.TryGetTarget(out var t)) return JsValue.Undefined;
            a.Realm.KeepAlive(t);
            return FromTarget(t);
        });
        wrProto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("WeakRef"), PropFlags.Configurable);

        var frProto = new JsObject(r, r.ObjectPrototype);
        r.FinalizationRegistryPrototype = frProto;
        var fr = r.Constructor("FinalizationRegistry", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (!a.IsConstruct) throw JsErrors.TypeError(realm, "Constructor FinalizationRegistry requires 'new'");
            if (!a[0].IsFunction) throw JsErrors.TypeError(realm, "FinalizationRegistry: cleanup must be callable");
            var p = Operations.GetPrototypeFromConstructor(realm, a.NewTarget.AsObjectUnchecked, realm.FinalizationRegistryPrototype!);
            var reg = new JsFinalizationRegistry(realm, p, a[0]);
            (realm.Registries ??= new()).Add(new WeakReference<JsFinalizationRegistry>(reg));
            return JsValue.FromObject(reg);
        }, frProto);
        r.DefineGlobal("FinalizationRegistry", JsValue.FromObject(fr));
        r.Method(frProto, "register", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a.This.Ref is not JsFinalizationRegistry reg) throw JsErrors.TypeError(realm, "FinalizationRegistry.prototype.register called on incompatible receiver");
            var target = WeakTarget(a[0]) ?? throw JsErrors.TypeError(realm, "FinalizationRegistry.prototype.register: invalid target");
            if (Operations.SameValue(a[0], a[1])) throw JsErrors.TypeError(realm, "FinalizationRegistry.prototype.register: target and holdings must not be same");
            object? token = WeakTarget(a[2]);
            if (token == null && !a[2].IsUndefined) throw JsErrors.TypeError(realm, "FinalizationRegistry.prototype.register: invalid unregister token");
            reg.Cells.Add(new JsFinalizationRegistry.Cell { Target = new WeakReference<object>(target), HeldValue = a[1], UnregisterToken = token });
            return JsValue.Undefined;
        });
        r.Method(frProto, "unregister", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a.This.Ref is not JsFinalizationRegistry reg) throw JsErrors.TypeError(realm, "FinalizationRegistry.prototype.unregister called on incompatible receiver");
            var token = WeakTarget(a[0]) ?? throw JsErrors.TypeError(realm, "FinalizationRegistry.prototype.unregister: invalid token");
            int removed = reg.Cells.RemoveAll(c => ReferenceEquals(c.UnregisterToken, token));
            return JsValue.FromBool(removed > 0);
        });
        frProto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("FinalizationRegistry"), PropFlags.Configurable);
    }

    /// <summary>Enqueues cleanup jobs for registered targets the GC has collected (host hook, called between jobs).</summary>
    public static void EnqueueCleanup(Realm realm)
    {
        var regs = realm.Registries!;
        for (int i = regs.Count - 1; i >= 0; i--)
        {
            if (!regs[i].TryGetTarget(out var reg)) { regs.RemoveAt(i); continue; }
            List<JsValue>? dead = null;
            for (int j = reg.Cells.Count - 1; j >= 0; j--)
            {
                if (reg.Cells[j].Target.TryGetTarget(out _)) continue;
                (dead ??= new List<JsValue>()).Add(reg.Cells[j].HeldValue);
                reg.Cells.RemoveAt(j);
            }
            if (dead == null) continue;
            var cb = reg.Cleanup;
            foreach (var held in dead)
            {
                var h = held;
                realm.Vm.EnqueueJob(new CallbackJob(rr => rr.Vm.Call(cb, JsValue.Undefined, new ReadOnlySpan<JsValue>(in h))));
            }
        }
    }
}
