using System.Collections.Generic;

namespace Jaunt.Runtime;

/// <summary>
/// A Module Source object (source phase imports proposal, 28.1): the source-phase representation of a host module
/// (<see cref="JsCompiler.CreateSourceModule"/>). Its [[Prototype]] is a per-kind prototype whose [[Prototype]] is
/// %AbstractModuleSource%.prototype.
/// </summary>
internal sealed class JsModuleSourceObject : JsObject
{
    /// <summary>GetModuleSourceKind() of the module record (the <c>Symbol.toStringTag</c> getter's result).</summary>
    public readonly string Kind;
    public JsModuleSourceObject(Realm realm, JsObject proto, string kind) : base(realm, proto) => Kind = kind;
}

/// <summary>%AbstractModuleSource% and Module Source objects (created lazily: the intrinsic has no global name).</summary>
internal static class ModuleSourceBuiltins
{
    /// <summary>%AbstractModuleSource% of the realm (exposed to hosts, e.g. test262's <c>$262.AbstractModuleSource</c>).</summary>
    public static JsBuiltin Constructor(Realm r)
    {
        if (r.AbstractModuleSource != null) return r.AbstractModuleSource;
        var proto = new JsObject(r, r.ObjectPrototype);
        var ctor = r.Constructor("AbstractModuleSource", 0,
            static (in NativeArgs a) => throw JsErrors.TypeError(a.Realm, "AbstractModuleSource is an abstract class and cannot be constructed"), proto);
        // get %AbstractModuleSource%.prototype[@@toStringTag]: HostGetModuleSourceModuleRecord(this).GetModuleSourceKind()
        r.Getter(proto, JsSymbol.ToStringTag, "[Symbol.toStringTag]",
            static (in NativeArgs a) => a.This.Ref is JsModuleSourceObject m ? new JsValue(m.Kind) : JsValue.Undefined);
        return r.AbstractModuleSource = ctor;
    }

    /// <summary>Creates the Module Source object of a host module whose GetModuleSourceKind() is <paramref name="kind"/>.</summary>
    public static JsObject CreateModuleSource(Realm r, string kind)
    {
        var protos = r.ModuleSourcePrototypes ??= new Dictionary<string, JsObject>(System.StringComparer.Ordinal);
        if (!protos.TryGetValue(kind, out var proto))
        {
            var abstractProto = Constructor(r).Get("prototype", JsValue.Undefined).AsObjectUnchecked;
            protos[kind] = proto = new JsObject(r, abstractProto);
        }
        return new JsModuleSourceObject(r, proto, kind);
    }
}
