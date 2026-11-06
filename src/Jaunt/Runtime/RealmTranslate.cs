using System;

namespace Jaunt.Runtime;

internal sealed partial class Realm
{
    private static readonly Func<Realm, JsObject?>[] s_protoIntrinsics =
    {
        static r => r.ObjectPrototype, static r => r.FunctionPrototype, static r => r.ArrayPrototype,
        static r => r.StringPrototype, static r => r.NumberPrototype, static r => r.BooleanPrototype,
        static r => r.SymbolPrototype, static r => r.BigIntPrototype, static r => r.IteratorPrototype,
        static r => r.GeneratorFunctionPrototype, static r => r.AsyncFunctionPrototype,
        static r => r.AsyncGeneratorFunctionPrototype, static r => r.PromisePrototype, static r => r.MapPrototype,
        static r => r.SetPrototype, static r => r.WeakMapPrototype, static r => r.WeakSetPrototype,
        static r => r.RegExpPrototype, static r => r.DatePrototype, static r => r.ArrayBufferPrototype,
        static r => r.Ensure(IntrinsicGroup.Iterator).DisposableStackPrototype,
        static r => r.Ensure(IntrinsicGroup.Iterator).AsyncDisposableStackPrototype,
        static r => r.Ensure(IntrinsicGroup.TypedArray).SharedArrayBufferPrototype,
        static r => r.Ensure(IntrinsicGroup.MapSet).WeakRefPrototype,
        static r => r.Ensure(IntrinsicGroup.MapSet).FinalizationRegistryPrototype,
        static r => r.Ensure(IntrinsicGroup.ShadowRealm).ShadowRealmPrototype,
    };

    /// <summary>
    /// Maps an intrinsic prototype of this realm to the same intrinsic of <paramref name="target"/>
    /// (GetPrototypeFromConstructor step 4: the fallback comes from new.target's function realm).
    /// </summary>
    internal JsObject TranslateIntrinsic(JsObject proto, Realm target)
    {
        foreach (var sel in s_protoIntrinsics)
            if (ReferenceEquals(sel(this), proto)) return sel(target) ?? proto;
        var errs = ErrorPrototypes;
        for (int i = 0; i < errs.Length; i++)
            if (ReferenceEquals(errs[i], proto)) return target.ErrorPrototypes[i];
        Ensure(IntrinsicGroup.TypedArray);
        target.Ensure(IntrinsicGroup.TypedArray);
        if (TypedArrayConstructors != null)
            for (int i = 0; i < TypedArrayConstructors.Length; i++)
                if (ReferenceEquals(TypedArrayConstructors[i].Get("prototype").Ref, proto))
                    return (JsObject)target.TypedArrayConstructors![i].Get("prototype").Ref!;
        // remaining constructors with a global binding (e.g. DataView): match by the original global name
        foreach (var name in s_globalCtorNames)
        {
            if (GlobalObject.Get(name).Ref is JsObject c && ReferenceEquals(c.Get("prototype").Ref, proto)
                && target.GlobalObject.Get(name).Ref is JsObject tc && tc.Get("prototype").Ref is JsObject tp)
                return tp;
        }
        return proto;
    }

    private static readonly string[] s_globalCtorNames = { "DataView", "Iterator", "ArrayBuffer", "SharedArrayBuffer" };
}
