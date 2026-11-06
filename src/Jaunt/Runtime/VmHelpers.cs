using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Jaunt.Compiler;

namespace Jaunt.Runtime;

internal sealed partial class Vm
{
    private const int MaxIcMisses = 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal JsValue CallValue(in JsValue fv, in JsValue thisValue, ReadOnlySpan<JsValue> args)
    {
        if (fv.IsObject && fv.Ref is JsFunctionObject fo) return fo.Call(thisValue, args);
        throw JsErrors.TypeError(Realm, $"{JsErrors.Describe(fv)} is not a function");
    }

    private static string DescribeReceiver(in JsValue v) => v.Tag switch
    {
        JsTag.Object => v.Ref is JsFunctionObject f ? f.DebugName : "object",
        JsTag.String => "string",
        _ => v.ToString(),
    };

    private static void GrowSlots(JsObject obj, int slot)
    {
        int n = Math.Max(4, obj.Slots.Length * 2);
        while (n <= slot) n *= 2;
        Array.Resize(ref obj.Slots, n);
    }

    // ------------------------------------------------------------------ globals

    private JsValue GetGlobalSlow(string name, ref IcEntry ic, bool typeofMode)
    {
        var realm = Realm;
        realm.EnsureGlobal(name);
        if (realm.GlobalLex.TryGetValue(name, out var cell))
        {
            ic.Kind = IcKind.GlobalCell;
            ic.Cell = cell;
            if (cell.Value.IsEmpty) throw JsErrors.ReferenceError(realm, $"Cannot access '{name}' before initialization");
            return cell.Value;
        }
        var g = realm.GlobalObject;
        var key = new PropertyKey(name);
        int idx = g.FindOwn(key, out var flags);
        if (idx >= 0 && (flags & PropFlags.Accessor) == 0)
        {
            if (g.Dict != null)
            {
                ic.Kind = IcKind.GlobalProp;
                ic.Slot = idx;
                ic.Epoch = realm.GlobalVersion;
            }
            return g.ReadSlot(idx);
        }
        if (g.HasProperty(key)) return g.Get(key);
        if (typeofMode) return JsValue.Undefined;
        throw JsErrors.ReferenceError(realm, $"{name} is not defined");
    }

    private void SetGlobalSlow(string name, JsValue value, ref IcEntry ic, bool strict)
    {
        var realm = Realm;
        realm.EnsureGlobal(name);
        if (realm.GlobalLex.TryGetValue(name, out var cell))
        {
            if (cell.Value.IsEmpty) throw JsErrors.ReferenceError(realm, $"Cannot access '{name}' before initialization");
            if (cell.IsConst) throw JsErrors.TypeError(realm, "Assignment to constant variable.");
            cell.Value = value;
            return;
        }
        var g = realm.GlobalObject;
        var key = new PropertyKey(name);
        int idx = g.FindOwn(key, out var flags);
        if (idx >= 0 && (flags & (PropFlags.Accessor | PropFlags.Writable)) == PropFlags.Writable)
        {
            g.WriteSlot(idx, value);
            if (g.Dict != null)
            {
                ic.Kind = IcKind.GlobalProp;
                ic.Slot = idx;
                ic.Epoch = realm.GlobalVersion;
            }
            return;
        }
        if (idx < 0 && !g.HasProperty(key))
        {
            if (strict) throw JsErrors.ReferenceError(realm, $"{name} is not defined");
            g.CreateDataProperty(key, value);
            return;
        }
        if (!g.Set(key, value, JsValue.FromObject(g)) && strict)
            throw JsErrors.TypeError(realm, $"Cannot assign to read only property '{name}' of object");
    }

    // ------------------------------------------------------------------ named properties

    internal JsValue GetPropSlow(in JsValue ov, in JsValue keyValue, ref IcEntry ic)
    {
        var key = new PropertyKey(keyValue.AsStringUnchecked);
        if (ov.IsNotObject)
        {
            if (ov.IsNullOrUndefined)
                throw JsErrors.TypeError(Realm, $"Cannot read properties of {ov} (reading '{key}')");
            if (ov.IsString && ic.Kind != IcKind.Megamorphic)
            {
                if (key.Value is string ks && ks == "length")
                {
                    ic.Kind = IcKind.StringLength;
                    return new JsValue(ov.StringLength);
                }
                var sp = Realm.StringPrototype;
                if (sp.Dict == null && sp.CanCacheNamed(key))
                {
                    int ps = sp.FindOwn(key, out var pf);
                    if (ps >= 0 && (pf & PropFlags.Accessor) == 0)
                    {
                        var v = sp.ReadSlot(ps);
                        ic.Kind = IcKind.StringProtoData;
                        ic.Shape = sp.Shape;
                        ic.Holder = sp;
                        ic.Slot = ps;
                        ic.Epoch = Realm.ProtoEpoch;
                        return v;
                    }
                }
            }
            return Operations.GetPrimitiveProperty(Realm, ov, key);
        }
        var obj = ov.AsObjectUnchecked;
        if (obj is JsArray arrObj && ic.Kind != IcKind.Megamorphic && key.Value is string lk && lk == "length")
        {
            ic.Kind = IcKind.ArrayLength;
            ic.Shape = null;
            ic.Holder = null;
            return new JsValue((double)arrObj.Length);
        }
        if (ic.Kind == IcKind.Megamorphic || obj.Dict != null || !obj.CanCacheNamed(key))
            return obj.Get(key, ov);

        int slot = obj.FindOwn(key, out var flags);
        if (slot >= 0)
        {
            var v = obj.ReadSlot(slot);
            if ((flags & PropFlags.Accessor) != 0)
            {
                var getter = ((Accessor)v.Ref!).Getter;
                return getter == null ? JsValue.Undefined : Call(JsValue.FromObject(getter), ov, ReadOnlySpan<JsValue>.Empty);
            }
            FillIc(ref ic, IcKind.OwnData, obj.Shape, null, slot);
            return v;
        }
        // prototype chain
        bool cacheable = true;
        for (var p = obj.Proto; p != null; p = p.Proto)
        {
            if (p.IsExotic && !p.CanCacheNamed(key)) return p.Get(key, ov);
            if (p.Dict != null) cacheable = false;
            int ps = p.FindOwn(key, out var pf);
            if (ps >= 0)
            {
                var v = p.ReadSlot(ps);
                if ((pf & PropFlags.Accessor) != 0)
                {
                    var getter = ((Accessor)v.Ref!).Getter;
                    return getter == null ? JsValue.Undefined : Call(JsValue.FromObject(getter), ov, ReadOnlySpan<JsValue>.Empty);
                }
                if (cacheable) FillIc(ref ic, IcKind.ProtoData, obj.Shape, p, ps);
                return v;
            }
        }
        return JsValue.Undefined;
    }

    private void FillIc(ref IcEntry ic, byte kind, Shape shape, JsObject? holder, int slot)
    {
        if (ic.Kind != IcKind.None && !ReferenceEquals(ic.Shape, shape))
        {
            if (++ic.Misses > MaxIcMisses) { ic.Kind = IcKind.Megamorphic; ic.Shape = null; ic.Holder = null; return; }
        }
        ic.Kind = kind;
        ic.Shape = shape;
        ic.Holder = holder;
        ic.Slot = slot;
        ic.Epoch = Realm.ProtoEpoch;
    }

    internal void SetPropSlow(in JsValue ov, in JsValue keyValue, JsValue value, ref IcEntry ic, bool strict)
    {
        var key = new PropertyKey(keyValue.AsStringUnchecked);
        if (ov.IsNotObject)
        {
            SetOnPrimitive(ov, key, value, strict);
            return;
        }
        var obj = ov.AsObjectUnchecked;
        if (ic.Kind != IcKind.Megamorphic && obj.Dict == null && obj.CanCacheNamed(key))
        {
            int slot = obj.FindOwn(key, out var flags);
            if (slot >= 0)
            {
                if ((flags & (PropFlags.Accessor | PropFlags.Writable)) == PropFlags.Writable)
                {
                    obj.Slots[slot] = value;
                    FillIc(ref ic, IcKind.OwnData, obj.Shape, null, slot);
                    return;
                }
            }
            else if (obj.Extensible && !obj.IsPrototypeObject)
            {
                // check the prototype chain for setters / read-only properties
                bool plain = true;
                for (var p = obj.Proto; p != null; p = p.Proto)
                {
                    if (p.IsExotic && !p.CanCacheNamed(key)) { plain = false; break; }
                    if (p.Dict != null) { plain = false; break; }
                    int ps = p.FindOwn(key, out var pf);
                    if (ps >= 0) { plain = false; break; }
                }
                if (plain)
                {
                    var oldShape = obj.Shape;
                    obj.AddOwnProperty(key, value, PropFlags.Default);
                    if (obj.Dict == null)
                    {
                        FillIc(ref ic, IcKind.AddTransition, oldShape, null, oldShape.Count);
                        ic.NewShape = obj.Shape;
                    }
                    return;
                }
            }
        }
        if (!obj.Set(key, value, ov) && strict)
            throw JsErrors.TypeError(Realm, $"Cannot assign to read only property '{key}' of object");
    }

    private void SetOnPrimitive(in JsValue ov, in PropertyKey key, JsValue value, bool strict)
    {
        if (ov.IsNullOrUndefined) throw JsErrors.TypeError(Realm, $"Cannot set properties of {ov} (setting '{key}')");
        // PutValue: ToObject(base).[[Set]](key, value, base) — setters and proxies on the prototype chain see the
        // primitive receiver; a data write fails (the receiver is not an object)
        var wrapper = Operations.ToObject(Realm, ov);
        if (!wrapper.Set(key, value, ov) && strict)
            throw JsErrors.TypeError(Realm, $"Cannot create property '{key}' on {ov.TypeName} '{ov}'");
    }

    // ------------------------------------------------------------------ keyed properties

    internal JsValue GetElemSlow(in JsValue ov, in JsValue kv)
    {
        if (ov.IsObject)
        {
            var obj = ov.AsObjectUnchecked;
            if (kv.IsNumber)
            {
                double d = kv.Num;
                uint idx = (uint)d;
                if (idx == d && idx != uint.MaxValue) return obj.GetIndex(idx, ov);
            }
            return obj.Get(Operations.ToPropertyKey(Realm, kv), ov);
        }
        if (ov.IsString && kv.IsNumber)
        {
            var s = ov.AsStringUnchecked;
            double d = kv.Num;
            int idx = (int)d;
            if (idx == d && (uint)idx < (uint)s.Length) return new JsValue(Realm.CharString(s[idx]));
        }
        if (ov.IsNullOrUndefined)   // the key is not coerced first (no user code runs)
            throw JsErrors.TypeError(Realm, $"Cannot read properties of {ov} (reading '{(kv.IsObject || kv.IsSymbol ? JsErrors.Describe(kv) : Operations.ToString(Realm, kv))}')");
        return Operations.GetPrimitiveProperty(Realm, ov, Operations.ToPropertyKey(Realm, kv));
    }

    internal void SetElemSlow(in JsValue ov, in JsValue kv, JsValue value, bool strict)
    {
        if (ov.IsObject)
        {
            var obj = ov.AsObjectUnchecked;
            bool ok;
            PropertyKey key = default;
            bool isIndex = kv.IsNumber && kv.Num == (uint)kv.Num && kv.Num != uint.MaxValue;
            if (isIndex) ok = obj.SetIndex((uint)kv.Num, value, ov);
            else
            {
                key = Operations.ToPropertyKey(Realm, kv);
                ok = obj.Set(key, value, ov);
            }
            if (!ok && strict)
                throw JsErrors.TypeError(Realm, $"Cannot assign to read only property '{(isIndex ? NumberFormatting.ToString(kv.Num) : key.ToString())}' of object");
            return;
        }
        SetOnPrimitive(ov, Operations.ToPropertyKey(Realm, kv), value, strict);
    }

    // ------------------------------------------------------------------ object literal / class helpers

    private void DefineAccessorOp(JsObject obj, JsValue keyValue, JsValue fnValue, int flags)
    {
        var key = Operations.ToPropertyKey(Realm, keyValue);
        int kind = flags & 3;
        bool enumerable = (flags & 4) != 0;
        bool setName = (flags & 8) != 0;
        var fn = fnValue.AsObjectUnchecked;
        if (setName || kind != 2) SetFunctionName(fn, key, kind == 0 ? "get" : kind == 1 ? "set" : null, onlyIfAnonymous: kind == 2);
        if (kind == 2)
        {
            obj.DefinePropertyOrThrow(key, PropertyDescriptor.Data(fnValue, PropFlags.Writable | PropFlags.Configurable | (enumerable ? PropFlags.Enumerable : 0)));
            return;
        }
        var desc = new PropertyDescriptor
        {
            HasEnumerable = true, Enumerable = enumerable,
            HasConfigurable = true, Configurable = true,
        };
        if (kind == 0) { desc.HasGet = true; desc.Getter = fn; }
        else { desc.HasSet = true; desc.Setter = fn; }
        obj.DefinePropertyOrThrow(key, desc);
    }

    internal static void SetFunctionName(JsObject fn, in PropertyKey key, string? prefix, bool onlyIfAnonymous = false)
    {
        if (fn is not JsFunctionObject) return;
        if (onlyIfAnonymous)
        {
            int ni = fn.FindOwn("name", out _);
            if (ni >= 0 && fn.ReadSlot(ni) is { Tag: JsTag.String } nv && nv.AsStringUnchecked.Length > 0) return;
            if (fn is JsClosure cl && cl.IsClassConstructor && cl.Template.Name.Length > 0) return;
        }
        string name = key.Value is JsSymbol sym ? (sym.Description == null ? "" : "[" + sym.Description + "]") : (string)key.Value;
        if (prefix != null) name = prefix + " " + name;
        int idx = fn.FindOwn("name", out _);
        if (idx >= 0) fn.WriteSlot(idx, new JsValue(name));
        else fn.DefineOwnProperty("name", PropertyDescriptor.Data(new JsValue(name), PropFlags.Configurable));
    }

    internal void CopyDataProperties(JsObject target, in JsValue source, ReadOnlySpan<JsValue> excluded)
    {
        if (source.IsNullOrUndefined) return;
        var from = Operations.ToObject(Realm, source);
        var keys = new List<PropertyKey>();
        from.OwnPropertyKeys(keys);
        foreach (var key in keys)
        {
            bool skip = false;
            foreach (var ex in excluded)
            {
                if (ex.IsString && key.Value is string ks && ks == ex.AsStringUnchecked) { skip = true; break; }
                if (ex.IsSymbol && ReferenceEquals(ex.Ref, key.Value)) { skip = true; break; }
            }
            if (skip) continue;
            if (from.GetOwnProperty(key, out var desc) && desc.Enumerable)
            {
                var v = from.Get(key, source.IsObject ? source : JsValue.FromObject(from));
                target.CreateDataPropertyOrThrow(key, v);
            }
            Tick();
        }
    }

    private JsObject? SuperBase(Frame f)
    {
        var home = f.Callee.HomeObject;
        if (home == null) throw JsErrors.SyntaxError(Realm, "'super' keyword unexpected here");
        return home.GetPrototypeOf();
    }

    private JsValue SuperCall(Frame f, ReadOnlySpan<JsValue> args)
    {
        var ctor = f.Callee;
        var box = f.ThisBox;
        var ntv = f.NewTarget;
        if (!ctor.IsDerived || (f.Flags & FrameFlags.Construct) == 0)
        {
            // super() from an arrow function (or eval) inside a derived constructor
            if (box == null) throw JsErrors.SyntaxError(Realm, "'super' keyword unexpected here");
            ctor = box.Ctor;
            ntv = box.NewTarget;
        }
        var parent = ctor.GetPrototypeOf();
        if (parent is not JsFunctionObject pf || !pf.IsConstructor) throw JsErrors.TypeError(Realm, "Super constructor is not a constructor");
        var nt = ntv.IsObject ? ntv.AsObjectUnchecked : ctor;
        var result = pf.Construct(args, nt);
        bool bound = box != null ? box.Value.IsNotEmpty : !f.This.IsEmpty;
        if (bound) throw JsErrors.ReferenceError(Realm, "Super constructor may only be called once");
        f.This = result;
        if (box != null) box.Value = result;
        if (ctor.FieldInit != null) ctor.FieldInit.Call(result, ReadOnlySpan<JsValue>.Empty);
        return result;
    }

    private void ClassCreate(Frame f, JsValue[] regs, int bp, int ctorDst, int protoDst, int fi, int superReg, int hasHeritage)
    {
        var realm = Realm;
        JsObject? protoParent = realm.ObjectPrototype;
        JsObject ctorParent = realm.FunctionPrototype;
        if (hasHeritage != 0)
        {
            var sv = regs[bp + superReg];
            if (sv.IsNull) protoParent = null;
            else
            {
                if (!Operations.IsConstructor(sv)) throw JsErrors.TypeError(realm, $"Class extends value {JsErrors.Describe(sv)} is not a constructor or null");
                var pp = sv.AsObjectUnchecked.Get("prototype");
                if (pp.IsObject) protoParent = pp.AsObjectUnchecked;
                else if (pp.IsNull) protoParent = null;
                else throw JsErrors.TypeError(realm, "Class extends value does not have valid prototype property");
                ctorParent = sv.AsObjectUnchecked;
            }
        }
        var proto = new JsObject(realm, protoParent);
        var t = f.T.Functions[fi];
        var ctor = CreateClosure(t, f.Env, f.Feedback.Child(fi), f, ctorParent);
        ctor.AddOwnProperty("prototype", JsValue.FromObject(proto), PropFlags.None);
        proto.AddOwnProperty("constructor", JsValue.FromObject(ctor), PropFlags.Hidden);
        ctor.HomeObject = proto;
        regs[bp + ctorDst] = JsValue.FromObject(ctor);
        regs[bp + protoDst] = JsValue.FromObject(proto);
    }

    // ------------------------------------------------------------------ private names

    private void PrivateDefine(in JsValue target, JsPrivateName name, JsValue value)
    {
        var obj = target.AsObjectUnchecked;
        var pe = obj.PrivateElements;
        if (pe != null && pe.ContainsKey(name))
            throw JsErrors.TypeError(Realm, $"Cannot initialize {name.Description} twice on the same object");
        // proposal-nonextensible-applies-to-private: private elements cannot be added to non-extensible objects
        if (!obj.IsExtensibleCheck())
            throw JsErrors.TypeError(Realm, $"Cannot define private member {name.Description} on a non-extensible object");
        (obj.PrivateElements ??= new Dictionary<object, JsValue>(ReferenceEqualityComparer.Instance)).Add(name, value);
    }

    private JsValue PrivateGet(in JsValue target, JsPrivateName name)
    {
        if (target.IsNotObject || target.AsObjectUnchecked.PrivateElements is not { } pe || !pe.TryGetValue(name, out var v))
            throw JsErrors.TypeError(Realm, $"Cannot read private member {name.Description} from an object whose class did not declare it");
        switch (name.Kind)
        {
            case 0: return v;
            case 1: return JsValue.FromObject(name.Method!);
            default:
                if (name.Getter == null) throw JsErrors.TypeError(Realm, $"'{name.Description}' was defined without a getter");
                return ((JsFunctionObject)name.Getter).Call(target, ReadOnlySpan<JsValue>.Empty);
        }
    }

    private void PrivateSet(in JsValue target, JsPrivateName name, JsValue value)
    {
        if (target.IsNotObject || target.AsObjectUnchecked.PrivateElements is not { } pe || !pe.ContainsKey(name))
            throw JsErrors.TypeError(Realm, $"Cannot write private member {name.Description} to an object whose class did not declare it");
        switch (name.Kind)
        {
            case 0: pe[name] = value; return;
            case 1: throw JsErrors.TypeError(Realm, $"Private method {name.Description} is not writable");
            default:
                if (name.Setter == null) throw JsErrors.TypeError(Realm, $"'{name.Description}' was defined without a setter");
                ((JsFunctionObject)name.Setter).Call(target, new ReadOnlySpan<JsValue>(in value));
                return;
        }
    }

    // ------------------------------------------------------------------ misc

    private JsArray CreateTemplateObject(TemplateSite site)
    {
        var realm = Realm;
        var cooked = new JsArray(realm, site.Cooked.Length);
        var raw = new JsArray(realm, site.Raw.Length);
        for (int i = 0; i < site.Cooked.Length; i++)
        {
            cooked.Push(site.Cooked[i] is { } c ? new JsValue(c) : JsValue.Undefined);
            raw.Push(new JsValue(site.Raw[i]));
        }
        ObjectBuiltins.SetIntegrityLevel(realm, raw, frozen: true);
        cooked.AddOwnProperty("raw", JsValue.FromObject(raw), PropFlags.None);
        ObjectBuiltins.SetIntegrityLevel(realm, cooked, frozen: true);
        return cooked;
    }

    private JsObject CreateMappedArgumentsObject(Frame f, int[] paramSlots)
    {
        var realm = Realm;
        var args = f.Args ?? Array.Empty<JsValue>();
        var map = new int[Math.Min(args.Length, paramSlots.Length)];
        Array.Copy(paramSlots, map, map.Length);
        var obj = new JsMappedArguments(realm, f.Env!, map);
        for (int i = 0; i < args.Length; i++) obj.AddOwnProperty(PropertyKey.FromIndex((uint)i), args[i], PropFlags.Default);
        obj.AddOwnProperty("length", new JsValue(args.Length), PropFlags.Hidden);
        obj.AddOwnProperty(JsSymbol.Iterator, JsValue.FromObject(realm.ArrayProtoValues), PropFlags.Hidden);
        obj.AddOwnProperty("callee", JsValue.FromObject(f.Callee), PropFlags.Hidden);
        return obj;
    }

    private JsObject CreateArgumentsObject(Frame f)
    {
        var realm = Realm;
        var args = f.Args ?? Array.Empty<JsValue>();
        var obj = new JsArguments(realm);
        for (int i = 0; i < args.Length; i++) obj.AddOwnProperty(PropertyKey.FromIndex((uint)i), args[i], PropFlags.Default);
        obj.AddOwnProperty("length", new JsValue(args.Length), PropFlags.Hidden);
        obj.AddOwnProperty(JsSymbol.Iterator, JsValue.FromObject(realm.ArrayProtoValues), PropFlags.Hidden);
        // CreateUnmappedArgumentsObject (strict code or non-simple parameters): callee is the %ThrowTypeError% accessor
        obj.DefineAccessor("callee", realm.ThrowTypeErrorFunction, realm.ThrowTypeErrorFunction, PropFlags.None);
        return obj;
    }
}

internal sealed class JsArguments : JsObject
{
    public JsArguments(Realm realm) : base(realm, realm.ObjectPrototype) { }
    public override string ClassName => "Arguments";
}

/// <summary>Arguments exotic object with parameter map (10.4.4).</summary>
internal sealed class JsMappedArguments : JsObject
{
    private readonly Env _env;
    private readonly int[] _map; // index → env slot, -1 when unmapped

    public JsMappedArguments(Realm realm, Env env, int[] map) : base(realm, realm.ObjectPrototype, exotic: true)
    {
        _env = env;
        _map = map;
    }

    public override string ClassName => "Arguments";

    private int MappedSlot(in PropertyKey key)
    {
        if (!key.TryGetArrayIndex(out uint i) || i >= (uint)_map.Length) return -1;
        return _map[i];
    }

    private void Unmap(in PropertyKey key)
    {
        if (key.TryGetArrayIndex(out uint i) && i < (uint)_map.Length) _map[i] = -1;
    }

    public override bool GetOwnProperty(in PropertyKey key, out PropertyDescriptor desc)
    {
        if (!base.GetOwnProperty(key, out desc)) return false;
        int slot = MappedSlot(key);
        if (slot >= 0) desc.Value = _env.Slots[slot];
        return true;
    }

    public override bool DefineOwnProperty(in PropertyKey key, in PropertyDescriptor desc)
    {
        int slot = MappedSlot(key);
        var newDesc = desc;
        if (slot >= 0 && !desc.IsAccessor && !desc.HasValue && desc.HasWritable && !desc.Writable)
        {
            newDesc.Value = _env.Slots[slot];
            newDesc.HasValue = true;
        }
        if (!OrdinaryDefineOwnProperty(key, newDesc)) return false;
        if (slot >= 0)
        {
            if (desc.IsAccessor) Unmap(key);
            else
            {
                if (desc.HasValue) _env.Slots[slot] = desc.Value;
                if (desc.HasWritable && !desc.Writable) Unmap(key);
            }
        }
        return true;
    }

    public override JsValue Get(in PropertyKey key, JsValue receiver)
    {
        int slot = MappedSlot(key);
        return slot >= 0 ? _env.Slots[slot] : OrdinaryGet(key, receiver);
    }

    public override bool Set(in PropertyKey key, JsValue value, JsValue receiver)
    {
        int slot = receiver.IsObject && ReferenceEquals(receiver.Ref, this) ? MappedSlot(key) : -1;
        if (slot >= 0) _env.Slots[slot] = value;
        return OrdinarySet(key, value, receiver);
    }

    public override bool Delete(in PropertyKey key)
    {
        bool ok = base.Delete(key);
        if (ok) Unmap(key);
        return ok;
    }

    public override JsValue GetIndex(uint index, JsValue receiver) => Get(PropertyKey.FromIndex(index), receiver);
    public override bool SetIndex(uint index, JsValue value, JsValue receiver) => Set(PropertyKey.FromIndex(index), value, receiver);
}
