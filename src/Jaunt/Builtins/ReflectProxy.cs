using System;
using System.Collections.Generic;

namespace Jaunt.Runtime;

internal static class ReflectBuiltins
{
    public static void Setup(Realm r)
    {
        var reflect = new JsObject(r, r.ObjectPrototype);
        r.DefineGlobal("Reflect", JsValue.FromObject(reflect));
        reflect.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("Reflect"), PropFlags.Configurable);
        r.Method(reflect, "apply", 3, static (in NativeArgs a) =>
        {
            if (!a[0].IsFunction) throw JsErrors.TypeError(a.Realm, "Reflect.apply target is not callable");
            var args = Operations.CreateListFromArrayLike(a.Realm, a[2]);
            return a.Realm.Vm.Call(a[0], a[1], args.ToArray());
        });
        r.Method(reflect, "construct", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (!Operations.IsConstructor(a[0])) throw JsErrors.TypeError(realm, "Reflect.construct target is not a constructor");
            var nt = a.Length > 2 ? a[2] : a[0];
            if (!Operations.IsConstructor(nt)) throw JsErrors.TypeError(realm, "Reflect.construct newTarget is not a constructor");
            var args = Operations.CreateListFromArrayLike(realm, a[1]);
            return realm.Vm.Construct(a[0], args.ToArray(), nt.AsObjectUnchecked);
        });
        r.Method(reflect, "defineProperty", 3, static (in NativeArgs a) =>
        {
            var o = Target(a);
            return JsValue.FromBool(o.DefineOwnProperty(Operations.ToPropertyKey(a.Realm, a[1]), Operations.ToPropertyDescriptor(a.Realm, a[2])));
        });
        r.Method(reflect, "deleteProperty", 2, static (in NativeArgs a) => JsValue.FromBool(Target(a).Delete(Operations.ToPropertyKey(a.Realm, a[1]))));
        r.Method(reflect, "get", 2, static (in NativeArgs a) =>
        {
            var o = Target(a);
            return o.Get(Operations.ToPropertyKey(a.Realm, a[1]), a.Length > 2 ? a[2] : a[0]);
        });
        r.Method(reflect, "set", 3, static (in NativeArgs a) =>
        {
            var o = Target(a);
            return JsValue.FromBool(o.Set(Operations.ToPropertyKey(a.Realm, a[1]), a[2], a.Length > 3 ? a[3] : a[0]));
        });
        r.Method(reflect, "getOwnPropertyDescriptor", 2, static (in NativeArgs a) =>
        {
            var o = Target(a);
            return o.GetOwnProperty(Operations.ToPropertyKey(a.Realm, a[1]), out var d) ? Operations.FromPropertyDescriptor(a.Realm, d) : JsValue.Undefined;
        });
        r.Method(reflect, "getPrototypeOf", 1, static (in NativeArgs a) =>
        {
            var p = Target(a).GetPrototypeOf();
            return p == null ? JsValue.Null : JsValue.FromObject(p);
        });
        r.Method(reflect, "setPrototypeOf", 2, static (in NativeArgs a) =>
        {
            var o = Target(a);
            if (a[1].IsNotObject && !a[1].IsNull) throw JsErrors.TypeError(a.Realm, "Object prototype may only be an Object or null");
            return JsValue.FromBool(o.SetPrototypeOf(a[1].IsNull ? null : a[1].AsObjectUnchecked));
        });
        r.Method(reflect, "has", 2, static (in NativeArgs a) => JsValue.FromBool(Target(a).HasProperty(Operations.ToPropertyKey(a.Realm, a[1]))));
        r.Method(reflect, "isExtensible", 1, static (in NativeArgs a) => JsValue.FromBool(Target(a).IsExtensibleCheck()));
        r.Method(reflect, "preventExtensions", 1, static (in NativeArgs a) => JsValue.FromBool(Target(a).PreventExtensions()));
        r.Method(reflect, "ownKeys", 1, static (in NativeArgs a) =>
        {
            var keys = new List<PropertyKey>();
            Target(a).OwnPropertyKeys(keys);
            var arr = a.Realm.NewArray(keys.Count);
            foreach (var k in keys) arr.Push(k.ToJsValue());
            return JsValue.FromObject(arr);
        });
    }

    private static JsObject Target(in NativeArgs a)
    {
        if (a[0].IsNotObject) throw JsErrors.TypeError(a.Realm, "Reflect method called on non-object");
        return a[0].AsObjectUnchecked;
    }
}

/// <summary>Proxy exotic object (ECMA-262 10.5).</summary>
internal sealed class JsProxy : JsObject
{
    public JsObject? Target;
    public JsObject? Handler;
    private readonly bool _callable;
    private readonly bool _constructor;

    public JsProxy(Realm realm, JsObject target, JsObject handler) : base(realm, null, exotic: true)
    {
        Target = target;
        Handler = handler;
        _callable = target is JsFunctionObject;
        _constructor = target is JsFunctionObject f && f.IsConstructor;
    }

    public override string ClassName => Target?.ClassName ?? "Object";
    public bool IsConstructorProxy => _constructor;
    internal bool IsCallableProxy => _callable;
    internal override bool CanCacheNamed(in PropertyKey key) => false;

    public bool IsArrayTarget()
    {
        CheckRevoked();
        return Target is JsArray || (Target is JsProxy p && p.IsArrayTarget());
    }

    private void CheckRevoked()
    {
        if (Handler == null) throw JsErrors.TypeError(Realm.Vm.Realm, "Cannot perform operation on a revoked proxy");
    }

    private JsValue Trap(string name, out JsObject target, out JsObject handler)
    {
        Realm.Vm.EnsureNativeStack();
        CheckRevoked();
        target = Target!;
        handler = Handler!;
        var t = handler.Get(name);
        if (t.IsNullOrUndefined) return JsValue.Undefined;
        if (!t.IsFunction) throw JsErrors.TypeError(Realm.Vm.Realm, $"proxy trap '{name}' is not a function");
        return t;
    }

    private JsValue CallTrap(JsObject handler, JsValue trap, JsValue a0) => Realm.Vm.Call(trap, JsValue.FromObject(handler), new ReadOnlySpan<JsValue>(in a0));

    private JsValue CallTrap(JsObject handler, JsValue trap, JsValue a0, JsValue a1)
    {
        TrapArgs args = default;
        args[0] = a0; args[1] = a1;
        return Realm.Vm.Call(trap, JsValue.FromObject(handler), ((ReadOnlySpan<JsValue>)args).Slice(0, 2));
    }

    private JsValue CallTrap(JsObject handler, JsValue trap, JsValue a0, JsValue a1, JsValue a2)
    {
        TrapArgs args = default;
        args[0] = a0; args[1] = a1; args[2] = a2;
        return Realm.Vm.Call(trap, JsValue.FromObject(handler), ((ReadOnlySpan<JsValue>)args).Slice(0, 3));
    }

    private JsValue CallTrap(JsObject handler, JsValue trap, JsValue a0, JsValue a1, JsValue a2, JsValue a3)
    {
        TrapArgs args = default;
        args[0] = a0; args[1] = a1; args[2] = a2; args[3] = a3;
        return Realm.Vm.Call(trap, JsValue.FromObject(handler), args);
    }

    [System.Runtime.CompilerServices.InlineArray(4)]
    private struct TrapArgs { private JsValue _first; }

    public override JsObject? GetPrototypeOf()
    {
        var trap = Trap("getPrototypeOf", out var target, out var handler);
        if (trap.IsUndefined) return target.GetPrototypeOf();
        var p = CallTrap(handler, trap, JsValue.FromObject(target));
        if (p.IsNotObject && !p.IsNull) throw JsErrors.TypeError(Realm.Vm.Realm, "getPrototypeOf trap returned neither object nor null");
        var result = p.IsNull ? null : p.AsObjectUnchecked;
        if (!target.IsExtensibleCheck() && !ReferenceEquals(result, target.GetPrototypeOf()))
            throw JsErrors.TypeError(Realm.Vm.Realm, "getPrototypeOf trap result does not match the non-extensible target's prototype");
        return result;
    }

    public override bool SetPrototypeOf(JsObject? proto)
    {
        var trap = Trap("setPrototypeOf", out var target, out var handler);
        if (trap.IsUndefined) return target.SetPrototypeOf(proto);
        bool ok = Operations.ToBoolean(CallTrap(handler, trap, JsValue.FromObject(target), proto == null ? JsValue.Null : JsValue.FromObject(proto)));
        if (!ok) return false;
        if (!target.IsExtensibleCheck() && !ReferenceEquals(proto, target.GetPrototypeOf()))
            throw JsErrors.TypeError(Realm.Vm.Realm, "setPrototypeOf trap returned true for non-extensible target with different prototype");
        return true;
    }

    public override bool IsExtensibleCheck()
    {
        var trap = Trap("isExtensible", out var target, out var handler);
        if (trap.IsUndefined) return target.IsExtensibleCheck();
        bool r = Operations.ToBoolean(CallTrap(handler, trap, JsValue.FromObject(target)));
        if (r != target.IsExtensibleCheck()) throw JsErrors.TypeError(Realm.Vm.Realm, "isExtensible trap result does not reflect the target");
        return r;
    }

    public override bool PreventExtensions()
    {
        var trap = Trap("preventExtensions", out var target, out var handler);
        if (trap.IsUndefined) return target.PreventExtensions();
        bool r = Operations.ToBoolean(CallTrap(handler, trap, JsValue.FromObject(target)));
        if (r && target.IsExtensibleCheck()) throw JsErrors.TypeError(Realm.Vm.Realm, "preventExtensions trap returned true but the target is extensible");
        return r;
    }

    public override bool GetOwnProperty(in PropertyKey key, out PropertyDescriptor desc)
    {
        var trap = Trap("getOwnPropertyDescriptor", out var target, out var handler);
        if (trap.IsUndefined) return target.GetOwnProperty(key, out desc);
        var r = CallTrap(handler, trap, JsValue.FromObject(target), key.ToJsValue());
        bool targetHas = target.GetOwnProperty(key, out var targetDesc);
        if (r.IsUndefined)
        {
            if (targetHas && !targetDesc.Configurable) throw JsErrors.TypeError(Realm.Vm.Realm, $"getOwnPropertyDescriptor trap returned undefined for non-configurable property '{key}'");
            if (targetHas && !target.IsExtensibleCheck()) throw JsErrors.TypeError(Realm.Vm.Realm, "getOwnPropertyDescriptor trap returned undefined for property of non-extensible target");
            desc = default;
            return false;
        }
        if (r.IsNotObject) throw JsErrors.TypeError(Realm.Vm.Realm, "getOwnPropertyDescriptor trap returned neither object nor undefined");
        desc = Operations.ToPropertyDescriptor(Realm.Vm.Realm, r);
        if (!desc.IsAccessor) { desc.HasValue = true; desc.HasWritable = true; }
        else { desc.HasGet = true; desc.HasSet = true; }
        desc.HasEnumerable = true;
        desc.HasConfigurable = true;
        if (!PropertyDescriptor.IsCompatible(target.IsExtensibleCheck(), desc, targetHas, targetDesc))
            throw JsErrors.TypeError(Realm.Vm.Realm, "getOwnPropertyDescriptor trap result is incompatible with the target property");
        if (!desc.Configurable)
        {
            if (!targetHas || targetDesc.Configurable)
                throw JsErrors.TypeError(Realm.Vm.Realm, "getOwnPropertyDescriptor trap reported non-configurability for a configurable or missing property");
            if (desc.HasWritable && !desc.Writable && targetDesc.IsData && targetDesc.Writable)
                throw JsErrors.TypeError(Realm.Vm.Realm, "getOwnPropertyDescriptor trap reported a non-writable, non-configurable property that is writable on the target");
        }
        return true;
    }

    public override bool DefineOwnProperty(in PropertyKey key, in PropertyDescriptor desc)
    {
        var trap = Trap("defineProperty", out var target, out var handler);
        if (trap.IsUndefined) return target.DefineOwnProperty(key, desc);
        var descObj = Operations.FromPropertyDescriptorPartial(Realm.Vm.Realm, desc);
        bool ok = Operations.ToBoolean(CallTrap(handler, trap, JsValue.FromObject(target), key.ToJsValue(), descObj));
        if (!ok) return false;
        bool targetHas = target.GetOwnProperty(key, out var targetDesc);
        bool settingNonConfig = desc.HasConfigurable && !desc.Configurable;
        if (!targetHas)
        {
            if (!target.IsExtensibleCheck()) throw JsErrors.TypeError(Realm.Vm.Realm, "defineProperty trap added a property to a non-extensible target");
            if (settingNonConfig) throw JsErrors.TypeError(Realm.Vm.Realm, "defineProperty trap defined a non-configurable property that does not exist on the target");
        }
        else
        {
            if (!PropertyDescriptor.IsCompatible(target.IsExtensibleCheck(), desc, true, targetDesc))
                throw JsErrors.TypeError(Realm.Vm.Realm, "defineProperty trap result is incompatible with the target property");
            if (settingNonConfig && targetDesc.Configurable)
                throw JsErrors.TypeError(Realm.Vm.Realm, "defineProperty trap result is inconsistent with the target property");
            if (targetDesc.IsData && !targetDesc.Configurable && targetDesc.Writable && desc.HasWritable && !desc.Writable)
                throw JsErrors.TypeError(Realm.Vm.Realm, "defineProperty trap made a non-configurable writable property non-writable");
        }
        return true;
    }

    public override bool HasProperty(in PropertyKey key)
    {
        var trap = Trap("has", out var target, out var handler);
        if (trap.IsUndefined) return target.HasProperty(key);
        bool r = Operations.ToBoolean(CallTrap(handler, trap, JsValue.FromObject(target), key.ToJsValue()));
        if (!r && target.GetOwnProperty(key, out var td))
        {
            if (!td.Configurable) throw JsErrors.TypeError(Realm.Vm.Realm, $"has trap returned false for non-configurable property '{key}'");
            if (!target.IsExtensibleCheck()) throw JsErrors.TypeError(Realm.Vm.Realm, "has trap returned false for property of non-extensible target");
        }
        return r;
    }

    public override JsValue Get(in PropertyKey key, JsValue receiver)
    {
        var trap = Trap("get", out var target, out var handler);
        if (trap.IsUndefined) return target.Get(key, receiver);
        var v = CallTrap(handler, trap, JsValue.FromObject(target), key.ToJsValue(), receiver);
        if (target.GetOwnProperty(key, out var td) && !td.Configurable)
        {
            if (td.IsData && !td.Writable && !Operations.SameValue(v, td.Value))
                throw JsErrors.TypeError(Realm.Vm.Realm, $"get trap result differs from non-writable, non-configurable property '{key}'");
            if (td.IsAccessor && td.Getter == null && !v.IsUndefined)
                throw JsErrors.TypeError(Realm.Vm.Realm, $"get trap returned a value for a non-configurable accessor without getter '{key}'");
        }
        return v;
    }

    public override bool Set(in PropertyKey key, JsValue value, JsValue receiver)
    {
        var trap = Trap("set", out var target, out var handler);
        if (trap.IsUndefined) return target.Set(key, value, receiver);
        bool ok = Operations.ToBoolean(CallTrap(handler, trap, JsValue.FromObject(target), key.ToJsValue(), value, receiver));
        if (!ok) return false;
        if (target.GetOwnProperty(key, out var td) && !td.Configurable)
        {
            if (td.IsData && !td.Writable && !Operations.SameValue(value, td.Value))
                throw JsErrors.TypeError(Realm.Vm.Realm, $"set trap succeeded for non-writable, non-configurable property '{key}'");
            if (td.IsAccessor && td.Setter == null)
                throw JsErrors.TypeError(Realm.Vm.Realm, $"set trap succeeded for non-configurable accessor without setter '{key}'");
        }
        return true;
    }

    public override bool Delete(in PropertyKey key)
    {
        var trap = Trap("deleteProperty", out var target, out var handler);
        if (trap.IsUndefined) return target.Delete(key);
        bool ok = Operations.ToBoolean(CallTrap(handler, trap, JsValue.FromObject(target), key.ToJsValue()));
        if (!ok) return false;
        if (target.GetOwnProperty(key, out var td))
        {
            if (!td.Configurable) throw JsErrors.TypeError(Realm.Vm.Realm, $"deleteProperty trap returned true for non-configurable property '{key}'");
            if (!target.IsExtensibleCheck()) throw JsErrors.TypeError(Realm.Vm.Realm, "deleteProperty trap returned true for property of non-extensible target");
        }
        return true;
    }

    public override void OwnPropertyKeys(List<PropertyKey> keys)
    {
        var trap = Trap("ownKeys", out var target, out var handler);
        if (trap.IsUndefined) { target.OwnPropertyKeys(keys); return; }
        var r = CallTrap(handler, trap, JsValue.FromObject(target));
        var list = Operations.CreateListFromArrayLike(Realm.Vm.Realm, r);
        var seen = new HashSet<PropertyKey>(PropertyKeyComparer.Instance);
        var result = new List<PropertyKey>();
        foreach (var v in list)
        {
            if (v.IsNotString && v.Tag != JsTag.Symbol) throw JsErrors.TypeError(Realm.Vm.Realm, "ownKeys trap result contains a non-property-key");
            var k = v.IsString ? new PropertyKey(v.AsStringUnchecked) : new PropertyKey((JsSymbol)v.Ref!);
            if (!seen.Add(k)) throw JsErrors.TypeError(Realm.Vm.Realm, "ownKeys trap result contains duplicate entries");
            result.Add(k);
        }
        var targetKeys = new List<PropertyKey>();
        target.OwnPropertyKeys(targetKeys);
        bool extensible = target.IsExtensibleCheck();
        foreach (var tk in targetKeys)
        {
            if (target.GetOwnProperty(tk, out var td) && !td.Configurable && !seen.Contains(tk))
                throw JsErrors.TypeError(Realm.Vm.Realm, $"ownKeys trap result must include non-configurable key '{tk}'");
            if (!extensible && !seen.Contains(tk)) throw JsErrors.TypeError(Realm.Vm.Realm, "ownKeys trap result must include all keys of a non-extensible target");
        }
        if (!extensible && result.Count != targetKeys.Count) throw JsErrors.TypeError(Realm.Vm.Realm, "ownKeys trap result has extra keys for a non-extensible target");
        keys.AddRange(result);
    }

    public JsValue CallProxy(JsValue thisValue, ReadOnlySpan<JsValue> args)
    {
        if (!_callable) throw JsErrors.TypeError(Realm.Vm.Realm, "proxy is not a function");
        var trap = Trap("apply", out var target, out var handler);
        if (trap.IsUndefined) return ((JsFunctionObject)target).Call(thisValue, args);
        var arr = Operations.CreateArrayFromList(Realm.Vm.Realm, args);
        return CallTrap(handler, trap, JsValue.FromObject(target), thisValue, JsValue.FromObject(arr));
    }

    public JsValue ConstructProxy(ReadOnlySpan<JsValue> args, JsObject newTarget)
    {
        if (!_constructor) throw JsErrors.TypeError(Realm.Vm.Realm, "proxy is not a constructor");
        var trap = Trap("construct", out var target, out var handler);
        if (trap.IsUndefined) return ((JsFunctionObject)target).Construct(args, newTarget);
        var arr = Operations.CreateArrayFromList(Realm.Vm.Realm, args);
        var r = CallTrap(handler, trap, JsValue.FromObject(target), JsValue.FromObject(arr), JsValue.FromObject(newTarget));
        if (r.IsNotObject) throw JsErrors.TypeError(Realm.Vm.Realm, "construct trap returned a non-object");
        return r;
    }
}

/// <summary>Callable proxy: a function object delegating every internal method to a <see cref="JsProxy"/>.</summary>
internal sealed class JsProxyFunction : JsFunctionObject
{
    public readonly JsProxy Inner;
    public JsProxyFunction(Realm realm, JsProxy inner) : base(realm, null, exotic: true) => Inner = inner;
    public override string ClassName => "Function";
    public override string DebugName => "proxy";
    public override bool IsConstructor => Inner.IsConstructorProxy;
    internal override bool CanCacheNamed(in PropertyKey key) => false;
    public override JsValue Call(JsValue thisValue, ReadOnlySpan<JsValue> args) => Inner.CallProxy(thisValue, args);
    public override JsValue Construct(ReadOnlySpan<JsValue> args, JsObject newTarget) => Inner.ConstructProxy(args, newTarget);
    public override JsObject? GetPrototypeOf() => Inner.GetPrototypeOf();
    public override bool SetPrototypeOf(JsObject? proto) => Inner.SetPrototypeOf(proto);
    public override bool IsExtensibleCheck() => Inner.IsExtensibleCheck();
    public override bool PreventExtensions() => Inner.PreventExtensions();
    public override bool GetOwnProperty(in PropertyKey key, out PropertyDescriptor desc) => Inner.GetOwnProperty(key, out desc);
    public override bool DefineOwnProperty(in PropertyKey key, in PropertyDescriptor desc) => Inner.DefineOwnProperty(key, desc);
    public override bool HasProperty(in PropertyKey key) => Inner.HasProperty(key);
    public override JsValue Get(in PropertyKey key, JsValue receiver) => Inner.Get(key, receiver);
    public override bool Set(in PropertyKey key, JsValue value, JsValue receiver) => Inner.Set(key, value, receiver);
    public override bool Delete(in PropertyKey key) => Inner.Delete(key);
    public override void OwnPropertyKeys(List<PropertyKey> keys) => Inner.OwnPropertyKeys(keys);
}

internal static class ProxyBuiltins
{
    public static void Setup(Realm r)
    {
        var ctor = r.Function("Proxy", 2, static (in NativeArgs a) =>
        {
            if (!a.IsConstruct) throw JsErrors.TypeError(a.Realm, "Constructor Proxy requires 'new'");
            return Wrap(a.Realm, Create(a.Realm, a[0], a[1]));
        }, constructor: true);
        r.DefineGlobal("Proxy", JsValue.FromObject(ctor));
        r.Method(ctor, "revocable", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var p = Create(realm, a[0], a[1]);
            var revoke = new JsBuiltin(realm, "", 0, static (in NativeArgs b) =>
            {
                if (b.Callee.State is JsProxy px) { px.Target = null; px.Handler = null; b.Callee.State = null; }
                return JsValue.Undefined;
            }) { State = p };
            var o = realm.NewObject();
            o.FastAdd("proxy", Wrap(realm, p));
            o.FastAdd("revoke", JsValue.FromObject(revoke));
            return JsValue.FromObject(o);
        });
    }

    private static JsValue Wrap(Realm realm, JsProxy p) => p.IsCallableProxy ? JsValue.FromObject(new JsProxyFunction(realm, p)) : JsValue.FromObject(p);

    private static JsProxy Create(Realm realm, JsValue target, JsValue handler)
    {
        if (target.IsNotObject || handler.IsNotObject) throw JsErrors.TypeError(realm, "Cannot create proxy with a non-object as target or handler");
        return new JsProxy(realm, target.AsObjectUnchecked, handler.AsObjectUnchecked);
    }
}
