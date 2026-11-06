using System;
using System.Collections.Generic;

namespace Jaunt.Runtime;

/// <summary>Runtime helpers reached through <see cref="Jaunt.Compiler.Op.Intrinsic"/> (auto-accessors and decorators).</summary>
internal enum ClassIntrinsic
{
    /// <summary>(latin1 string) → Uint8Array over an immutable ArrayBuffer holding those bytes (bytes modules).</summary>
    BytesModule,
    /// <summary>(storage, key) → getter of an auto-accessor.</summary>
    AutoAccessorGetter,
    /// <summary>(storage, key) → setter of an auto-accessor.</summary>
    AutoAccessorSetter,
    /// <summary>(F, proto, classDecorators|undefined, superclass|undefined, className) → decoration state.</summary>
    DecoratorsBegin,
    /// <summary>(state, kind | static&lt;&lt;3, key, value, value2, decorators): records a decorated element.</summary>
    DecoratorsAddElement,
    /// <summary>(state): calls the element decorators and defines the decorated methods and accessors.</summary>
    DecoratorsApplyElements,
    /// <summary>(state) → the class, as replaced by the class decorators.</summary>
    DecoratorsApplyClass,
    /// <summary>(state): runs the static extra initializers.</summary>
    DecoratorsStaticExtra,
    /// <summary>(state): runs the extra initializers added by class decorators.</summary>
    DecoratorsClassExtra,
    /// <summary>(state, this): runs the instance extra initializers added by method decorators.</summary>
    DecoratorsInstanceExtra,
    /// <summary>(state, index, this, value) → the initial value transformed by the field's decorator initializers.</summary>
    DecoratorsFieldInit,
    /// <summary>(state, index, this): runs the extra initializers of a field or accessor after its definition.</summary>
    DecoratorsFieldExtra,
}

/// <summary>A decorated class element (ClassElementDefinition record of the decorators proposal).</summary>
internal sealed class DecoratedElement
{
    public const int Field = 0, Accessor = 1, Method = 2, Getter = 3, Setter = 4;
    public int Kind;
    public bool IsStatic;
    public PropertyKey Key;
    public JsPrivateName? Private;
    public JsValue Value, Value2;
    public JsArray Decorators = null!;
    public List<JsValue>? Initializers;
    public List<JsValue>? ExtraInitializers;
}

/// <summary>Per class evaluation state of a decorated class. Internal only: never reachable from script.</summary>
internal sealed class DecorationState : JsObject
{
    public JsObject F = null!, Prototype = null!, Metadata = null!;
    public JsObject? NewF;
    public JsArray? ClassDecorators;
    public JsValue Name;
    public readonly List<DecoratedElement> Elements = new();
    /// <summary>Decorated fields and accessors, in the order the compiler indexes them.</summary>
    public readonly List<DecoratedElement> Fields = new();
    public readonly List<JsValue> InstanceExtra = new(), StaticExtra = new(), ClassExtra = new();
    public DecorationState(Realm realm) : base(realm, null) { }
}

internal sealed partial class Vm
{
    private sealed class DecorationStatus { public bool Finished; }

    private JsValue Intrinsic(ClassIntrinsic id, JsValue[] a)
    {
        var realm = Realm;
        switch (id)
        {
            case ClassIntrinsic.BytesModule:
            {
                var str = a[0].AsStringUnchecked;
                var bytes = new byte[str.Length];
                for (int i = 0; i < str.Length; i++) bytes[i] = (byte)str[i];
                realm.Ensure(IntrinsicGroup.TypedArray);
                var buffer = new JsArrayBuffer(realm, realm.ArrayBufferPrototype, bytes, bytes.Length);
                var proto = (JsObject)realm.TypedArrayConstructors![(int)TypedKind.Uint8].Get("prototype").Ref!;
                return JsValue.FromObject(new JsTypedArray(realm, proto, TypedKind.Uint8, buffer, 0, bytes.Length));
            }
            case ClassIntrinsic.AutoAccessorGetter:
            case ClassIntrinsic.AutoAccessorSetter:
            {
                var storage = (JsPrivateName)a[0].Ref!;
                string name = a[1].Ref is JsPrivateName pn ? pn.Description : KeyName(Operations.ToPropertyKey(realm, a[1]));
                bool get = id == ClassIntrinsic.AutoAccessorGetter;
                var fn = get
                    ? new JsBuiltin(realm, "get " + name, 0, static (in NativeArgs x) => x.Realm.Vm.PrivateGet(x.This, (JsPrivateName)x.Callee.State!))
                    : new JsBuiltin(realm, "set " + name, 1, static (in NativeArgs x) =>
                    {
                        x.Realm.Vm.PrivateSet(x.This, (JsPrivateName)x.Callee.State!, x[0]);
                        return JsValue.Undefined;
                    });
                fn.State = storage;
                return JsValue.FromObject(fn);
            }
            case ClassIntrinsic.DecoratorsBegin:
            {
                var st = new DecorationState(realm)
                {
                    F = a[0].AsObjectUnchecked,
                    Prototype = a[1].AsObjectUnchecked,
                    ClassDecorators = a[2].Ref as JsArray,
                    Name = a[4].IsString && a[4].AsStringUnchecked.Length > 0 ? a[4] : JsValue.Undefined,
                };
                JsObject? parent = null;
                if (a[3].IsObject && a[3].AsObjectUnchecked.Get(JsSymbol.Metadata) is { IsObject: true } pm) parent = pm.AsObjectUnchecked;
                st.Metadata = new JsObject(realm, parent);
                return JsValue.FromObject(st);
            }
            case ClassIntrinsic.DecoratorsAddElement:
            {
                var st = (DecorationState)a[0].AsObjectUnchecked;
                int flags = (int)a[1].AsNumber();
                var el = new DecoratedElement
                {
                    Kind = flags & 7,
                    IsStatic = (flags & 8) != 0,
                    Value = a[3],
                    Value2 = a[4],
                    Decorators = (JsArray)a[5].AsObjectUnchecked,
                };
                if (a[2].Ref is JsPrivateName pn) el.Private = pn;
                else
                {
                    el.Key = Operations.ToPropertyKey(realm, a[2]);
                    if (el.Kind >= DecoratedElement.Method)
                        SetFunctionName(el.Value.AsObjectUnchecked, el.Key,
                            el.Kind == DecoratedElement.Getter ? "get" : el.Kind == DecoratedElement.Setter ? "set" : null,
                            onlyIfAnonymous: el.Kind == DecoratedElement.Method);
                }
                st.Elements.Add(el);
                if (el.Kind <= DecoratedElement.Accessor) st.Fields.Add(el);
                return JsValue.Undefined;
            }
            case ClassIntrinsic.DecoratorsApplyElements:
            {
                var st = (DecorationState)a[0].AsObjectUnchecked;
                // static methods/accessors, instance methods/accessors, static fields, instance fields
                for (int pass = 0; pass < 4; pass++)
                {
                    bool fields = pass >= 2, isStatic = (pass & 1) == 0;
                    foreach (var el in st.Elements)
                        if (el.IsStatic == isStatic && (el.Kind == DecoratedElement.Field) == fields) ApplyElementDecorators(st, el);
                }
                return JsValue.Undefined;
            }
            case ClassIntrinsic.DecoratorsApplyClass:
            {
                var st = (DecorationState)a[0].AsObjectUnchecked;
                var classDef = JsValue.FromObject(st.F);
                if (st.ClassDecorators is { } decs)
                {
                    for (int i = (int)decs.Length - 1; i >= 0; i--)
                    {
                        var status = new DecorationStatus();
                        var ctx = new JsObject(realm, realm.ObjectPrototype);
                        ctx.CreateDataPropertyOrThrow("kind", new JsValue("class"));
                        ctx.CreateDataPropertyOrThrow("name", st.Name);
                        ctx.CreateDataPropertyOrThrow("metadata", JsValue.FromObject(st.Metadata));
                        ctx.CreateDataPropertyOrThrow("addInitializer", JsValue.FromObject(AddInitializerFunction(status, st.ClassExtra)));
                        var res = CallDecorator(decs.GetIndex((uint)i, JsValue.FromObject(decs)), classDef, ctx);
                        status.Finished = true;
                        if (res.IsUndefined) continue;
                        if (!Operations.IsCallable(res)) throw JsErrors.TypeError(realm, "A class decorator must return a function or undefined");
                        classDef = res;
                    }
                }
                // The metadata proposal leaves the attributes open; follow the shipped transpilers (TypeScript 5.x, Babel
                // applyDecs2311): an enumerable, configurable property on the class the class decorators returned.
                st.NewF = classDef.AsObjectUnchecked;
                st.NewF.DefinePropertyOrThrow(JsSymbol.Metadata, PropertyDescriptor.Data(JsValue.FromObject(st.Metadata), PropFlags.Default));
                return classDef;
            }
            case ClassIntrinsic.DecoratorsStaticExtra:
            {
                var st = (DecorationState)a[0].AsObjectUnchecked;
                RunInitializers(st.StaticExtra, JsValue.FromObject(st.F));
                return JsValue.Undefined;
            }
            case ClassIntrinsic.DecoratorsClassExtra:
            {
                var st = (DecorationState)a[0].AsObjectUnchecked;
                RunInitializers(st.ClassExtra, JsValue.FromObject(st.NewF ?? st.F));
                return JsValue.Undefined;
            }
            case ClassIntrinsic.DecoratorsInstanceExtra:
            {
                var st = (DecorationState)a[0].AsObjectUnchecked;
                RunInitializers(st.InstanceExtra, a[1]);
                return JsValue.Undefined;
            }
            case ClassIntrinsic.DecoratorsFieldInit:
            {
                var st = (DecorationState)a[0].AsObjectUnchecked;
                var el = st.Fields[(int)a[1].AsNumber()];
                var v = a[3];
                if (el.Initializers != null)
                    foreach (var init in el.Initializers) v = ((JsFunctionObject)init.Ref!).Call(a[2], new ReadOnlySpan<JsValue>(in v));
                return v;
            }
            case ClassIntrinsic.DecoratorsFieldExtra:
            {
                var st = (DecorationState)a[0].AsObjectUnchecked;
                var el = st.Fields[(int)a[1].AsNumber()];
                if (el.ExtraInitializers != null) RunInitializers(el.ExtraInitializers, a[2]);
                return JsValue.Undefined;
            }
        }
        throw new InvalidOperationException("unknown intrinsic");
    }

    private static string KeyName(in PropertyKey key) =>
        key.Value is JsSymbol sym ? (sym.Description == null ? "" : "[" + sym.Description + "]") : (string)key.Value;

    private static void RunInitializers(List<JsValue> list, JsValue thisValue)
    {
        // initializers may not add initializers (addInitializer is closed by then), so the list is stable
        foreach (var init in list) ((JsFunctionObject)init.Ref!).Call(thisValue, ReadOnlySpan<JsValue>.Empty);
    }

    private JsValue CallDecorator(JsValue decorator, JsValue value, JsObject context)
    {
        if (!Operations.IsCallable(decorator)) throw JsErrors.TypeError(Realm, $"Decorator {JsErrors.Describe(decorator)} is not a function");
        return ((JsFunctionObject)decorator.Ref!).Call(JsValue.Undefined, new[] { value, JsValue.FromObject(context) });
    }

    private JsBuiltin AddInitializerFunction(DecorationStatus status, List<JsValue> target)
    {
        return new JsBuiltin(Realm, "addInitializer", 1, static (in NativeArgs x) =>
        {
            var (s, list) = ((DecorationStatus, List<JsValue>))x.Callee.State!;
            if (s.Finished) throw JsErrors.TypeError(x.Realm, "addInitializer cannot be called after the decorator has returned");
            if (!Operations.IsCallable(x[0])) throw JsErrors.TypeError(x.Realm, "An initializer must be a function");
            list.Add(x[0]);
            return JsValue.Undefined;
        }) { State = (status, target) };
    }

    private void ApplyElementDecorators(DecorationState st, DecoratedElement el)
    {
        var realm = Realm;
        var decs = el.Decorators;
        bool fieldLike = el.Kind <= DecoratedElement.Accessor;
        List<JsValue> extra;
        if (fieldLike) extra = el.ExtraInitializers ??= new List<JsValue>();
        else extra = el.IsStatic ? st.StaticExtra : st.InstanceExtra;
        string kindName = el.Kind switch
        {
            DecoratedElement.Field => "field", DecoratedElement.Accessor => "accessor", DecoratedElement.Method => "method",
            DecoratedElement.Getter => "getter", _ => "setter",
        };
        for (int i = (int)decs.Length - 1; i >= 0; i--)
        {
            var status = new DecorationStatus();
            var ctx = new JsObject(realm, realm.ObjectPrototype);
            ctx.CreateDataPropertyOrThrow("kind", new JsValue(kindName));
            ctx.CreateDataPropertyOrThrow("access", JsValue.FromObject(CreateAccessObject(el)));
            ctx.CreateDataPropertyOrThrow("static", JsValue.FromBool(el.IsStatic));
            ctx.CreateDataPropertyOrThrow("private", JsValue.FromBool(el.Private != null));
            ctx.CreateDataPropertyOrThrow("name", el.Private != null ? new JsValue(el.Private.Description) : el.Key.ToJsValue());
            ctx.CreateDataPropertyOrThrow("metadata", JsValue.FromObject(st.Metadata));
            ctx.CreateDataPropertyOrThrow("addInitializer", JsValue.FromObject(AddInitializerFunction(status, extra)));
            JsValue value;
            if (el.Kind == DecoratedElement.Field) value = JsValue.Undefined;
            else if (el.Kind == DecoratedElement.Accessor)
            {
                var pair = new JsObject(realm, realm.ObjectPrototype);
                pair.CreateDataPropertyOrThrow("get", el.Value);
                pair.CreateDataPropertyOrThrow("set", el.Value2);
                value = JsValue.FromObject(pair);
            }
            else value = el.Value;
            var res = CallDecorator(decs.GetIndex((uint)i, JsValue.FromObject(decs)), value, ctx);
            status.Finished = true;
            if (res.IsUndefined) continue;
            switch (el.Kind)
            {
                case DecoratedElement.Field:
                    if (!Operations.IsCallable(res)) throw JsErrors.TypeError(realm, "A field decorator must return a function or undefined");
                    (el.Initializers ??= new List<JsValue>()).Add(res);
                    break;
                case DecoratedElement.Accessor:
                {
                    if (!res.IsObject) throw JsErrors.TypeError(realm, "An accessor decorator must return an object or undefined");
                    var o = res.AsObjectUnchecked;
                    var g = o.Get("get");
                    if (Operations.IsCallable(g)) el.Value = g;
                    else if (!g.IsUndefined) throw JsErrors.TypeError(realm, "Accessor decorator 'get' must be a function or undefined");
                    var s = o.Get("set");
                    if (Operations.IsCallable(s)) el.Value2 = s;
                    else if (!s.IsUndefined) throw JsErrors.TypeError(realm, "Accessor decorator 'set' must be a function or undefined");
                    var init = o.Get("init");
                    if (Operations.IsCallable(init)) (el.Initializers ??= new List<JsValue>()).Add(init);
                    else if (!init.IsUndefined) throw JsErrors.TypeError(realm, "Accessor decorator 'init' must be a function or undefined");
                    break;
                }
                default:
                    if (!Operations.IsCallable(res)) throw JsErrors.TypeError(realm, $"A {kindName} decorator must return a function or undefined");
                    el.Value = res;
                    break;
            }
        }
        // define the (possibly replaced) methods and accessors
        if (el.Kind == DecoratedElement.Field) return;
        var target = el.IsStatic ? st.F : st.Prototype;
        var fn = el.Value.AsObjectUnchecked;
        if (el.Private is { } pn)
        {
            switch (el.Kind)
            {
                case DecoratedElement.Method: pn.Method = fn; break;
                case DecoratedElement.Getter: pn.Getter = fn; break;
                case DecoratedElement.Setter: pn.Setter = fn; break;
                default: pn.Getter = fn; pn.Setter = el.Value2.AsObjectUnchecked; break;
            }
            return;
        }
        var desc = new PropertyDescriptor { HasEnumerable = true, Enumerable = false, HasConfigurable = true, Configurable = true };
        switch (el.Kind)
        {
            case DecoratedElement.Method:
                target.DefinePropertyOrThrow(el.Key, PropertyDescriptor.Data(el.Value, PropFlags.Writable | PropFlags.Configurable));
                return;
            case DecoratedElement.Getter: desc.HasGet = true; desc.Getter = fn; break;
            case DecoratedElement.Setter: desc.HasSet = true; desc.Setter = fn; break;
            default:
                desc.HasGet = true; desc.Getter = fn;
                desc.HasSet = true; desc.Setter = el.Value2.AsObjectUnchecked;
                break;
        }
        target.DefinePropertyOrThrow(el.Key, desc);
    }

    /// <summary>The context.access object: get (all kinds but setter), set (field, accessor, setter), has.</summary>
    private JsObject CreateAccessObject(DecoratedElement el)
    {
        var realm = Realm;
        var access = new JsObject(realm, realm.ObjectPrototype);
        if (el.Kind != DecoratedElement.Setter)
        {
            var get = new JsBuiltin(realm, "get", 1, static (in NativeArgs x) =>
            {
                var e = (DecoratedElement)x.Callee.State!;
                var o = x[0];
                if (e.Private != null) return x.Realm.Vm.PrivateGet(o, e.Private);
                return Operations.ToObject(x.Realm, o).Get(e.Key, o);
            }) { State = el };
            access.CreateDataPropertyOrThrow("get", JsValue.FromObject(get));
        }
        if (el.Kind is DecoratedElement.Field or DecoratedElement.Accessor or DecoratedElement.Setter)
        {
            var set = new JsBuiltin(realm, "set", 2, static (in NativeArgs x) =>
            {
                var e = (DecoratedElement)x.Callee.State!;
                var o = x[0];
                if (e.Private != null) x.Realm.Vm.PrivateSet(o, e.Private, x[1]);
                else if (!Operations.ToObject(x.Realm, o).Set(e.Key, x[1], o))
                    throw JsErrors.TypeError(x.Realm, $"Cannot assign to property '{e.Key}'");
                return JsValue.Undefined;
            }) { State = el };
            access.CreateDataPropertyOrThrow("set", JsValue.FromObject(set));
        }
        var has = new JsBuiltin(realm, "has", 1, static (in NativeArgs x) =>
        {
            var e = (DecoratedElement)x.Callee.State!;
            if (!x[0].IsObject) throw JsErrors.TypeError(x.Realm, "access.has called on a non-object");
            var o = x[0].AsObjectUnchecked;
            if (e.Private != null) return JsValue.FromBool(o.PrivateElements is { } pe && pe.ContainsKey(e.Private));
            return JsValue.FromBool(o.HasProperty(e.Key));
        }) { State = el };
        access.CreateDataPropertyOrThrow("has", JsValue.FromObject(has));
        return access;
    }
}
