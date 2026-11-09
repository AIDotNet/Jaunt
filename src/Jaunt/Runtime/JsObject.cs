using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Jaunt.Runtime;

/// <summary>Placeholder stored in a slot until a builtin method is first read. Data slots holding one read as Empty, so every read that takes the slot as is must check for that and go through <see cref="JsObject.ReadSlot"/>.</summary>
internal sealed class LazyFunction
{
    public readonly string Name;
    public readonly int Length;
    public readonly NativeFn Fn;
    public LazyFunction(string name, int length, NativeFn fn) { Name = name; Length = length; Fn = fn; }

    /// <summary>In the "prototype" slot of a closure: the object is created when first read, most functions never get there.</summary>
    public static readonly LazyFunction ClosurePrototype = new("prototype", 0, null!);
    public static readonly JsValue ClosurePrototypeSlot = new(JsTag.Empty, 0, ClosurePrototype);
}

/// <summary>The less common state of a <see cref="JsObject"/>.</summary>
internal sealed class ObjectExtras
{
    /// <summary>Root of the objects that have this one as their prototype.</summary>
    public Shape? ChildRootShape;
    /// <summary>Layout (length, name) of the functions that have this one as their prototype.</summary>
    public Shape? FunctionChildShape;
    /// <summary>Private class elements (#x): found by a scan for the name's identity, the end marked by a null name or the array's end.</summary>
    public PrivateElement[]? PrivateElements;
}

internal struct PrivateElement
{
    public JsPrivateName? Name;
    public JsValue Value;
}

/// <summary>
/// Ordinary JS object. Exotic objects override the virtual internal methods (ECMA-262 [[Get]], [[Set]], ...).
/// Ordinary objects store named properties in shape+slot layout and fall back to a dictionary when they get
/// large, delete properties, change attributes or change prototype.
/// </summary>
internal class JsObject
{
    internal Shape Shape;
    internal JsValue[] Slots;
    internal PropertyMap? Dict;
    internal readonly Realm Realm;
    internal bool Extensible = true;
    /// <summary>Set once this object is used as a prototype: layout changes then invalidate prototype ICs.</summary>
    internal bool IsPrototypeObject;
    /// <summary>True for exotic objects whose [[Get]]/[[Set]]/... are not purely shape-driven.</summary>
    internal readonly bool IsExotic;
    private int _identityHash;

    /// <summary>Stable per-object hash for Map/Set keys and other identity tables (see <see cref="Realm.NextIdentityHash"/>).</summary>
    internal int IdentityHash
    {
        get
        {
            int h = _identityHash;
            if (h == 0) h = _identityHash = Realm.NextIdentityHash();
            return h;
        }
    }
    /// <summary>What only some objects need, apart so that the rest stay small (see <see cref="ObjectExtras"/>).</summary>
    internal ObjectExtras? Extras;
    internal PrivateElement[]? PrivateElements => Extras?.PrivateElements;

    private static readonly JsValue[] NoSlots = Array.Empty<JsValue>();

    /// <summary>The index in <see cref="PrivateElements"/> of the element named <paramref name="name"/>, or -1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int FindPrivate(JsPrivateName name)
    {
        var pe = Extras?.PrivateElements;
        if (pe != null)
        {
            for (int i = 0; i < pe.Length; i++)
            {
                if (ReferenceEquals(pe[i].Name, name)) return i;
            }
        }
        return -1;
    }

    /// <summary>Appends a private element whose name is known to be absent.</summary>
    internal void AddPrivate(JsPrivateName name, JsValue value)
    {
        var extras = Extras ??= new ObjectExtras();
        var pe = extras.PrivateElements;
        int i = 0;
        if (pe == null) extras.PrivateElements = pe = new PrivateElement[2];
        else
        {
            while (i < pe.Length && pe[i].Name != null) i++;
            if (i == pe.Length)
            {
                Array.Resize(ref pe, i * 2);
                extras.PrivateElements = pe;
            }
        }
        pe[i].Name = name;
        pe[i].Value = value;
    }

    public JsObject(Realm realm, JsObject? proto)
    {
        Realm = realm;
        Shape = realm.RootShapeFor(proto);
        Slots = NoSlots;
        realm.ChargeObject();
    }

    protected JsObject(Realm realm, JsObject? proto, bool exotic, bool uniqueShape = true)
    {
        Realm = realm;
        IsExotic = exotic;
        Shape = exotic && uniqueShape ? Shape.CreateUnique(proto) : realm.RootShapeFor(proto);
        if (exotic && proto != null) proto.IsPrototypeObject = true;
        Slots = NoSlots;
        realm.ChargeObject();
    }

    /// <summary>Constructor used for objects created with a pre-computed shape (object literals, function objects).</summary>
    internal JsObject(Realm realm, Shape shape, int slotCapacity)
    {
        Realm = realm;
        Shape = shape;
        Slots = slotCapacity == 0 ? NoSlots : new JsValue[slotCapacity];
        realm.ChargeObject();
    }

    /// <summary>Constructor used for function objects, which come with their slots (see <see cref="JsClosure"/>).</summary>
    internal JsObject(Realm realm, Shape shape, JsValue[] slots)
    {
        Realm = realm;
        Shape = shape;
        Slots = slots;
        realm.ChargeObject();
    }

    /// <summary>Constructor used for exotic objects created with a fully built layout (arguments objects).</summary>
    protected JsObject(Realm realm, Shape shape, JsValue[] slots, bool exotic)
    {
        Realm = realm;
        IsExotic = exotic;
        Shape = shape;
        Slots = slots;
        realm.ChargeObject();
        realm.ChargeProperties(shape.Count);
    }

    public virtual string ClassName => "Object";

    public JsObject? Proto
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var s = Shape;
            return s.Prototype ?? (s.Role == ProtoRole.Explicit ? null : Realm.RoleProto(s.Role));
        }
    }

    // ------------------------------------------------------------------ own property primitives (ordinary)

    /// <summary>Finds an own named property in ordinary storage. Returns the slot (or dictionary index) or -1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int FindOwn(in PropertyKey key, out PropFlags flags)
    {
        if (Dict == null)
        {
            int i = Shape.IndexOf(key);
            flags = i >= 0 ? Shape.Flags[i] : PropFlags.None;
            return i;
        }
        int d = Dict.IndexOf(key);
        flags = d >= 0 ? Dict.Flags[d] : PropFlags.None;
        return d;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal JsValue ReadSlot(int i)
    {
        var v = Dict == null ? Slots[i] : Dict.Values[i];
        if (v.IsEmpty && v.Ref is LazyFunction) return Materialize(i);
        return v;
    }

    /// <summary>Creates the function object of a lazily defined builtin method and stores it in its slot.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal JsValue Materialize(int i)
    {
        var v = Dict == null ? Slots[i] : Dict.Values[i];
        if (v.Ref is not LazyFunction lf) return v;
        var fn = JsValue.FromObject(ReferenceEquals(lf, LazyFunction.ClosurePrototype) ? ((JsClosure)this).CreatePrototypeObject() : Realm.CreateBuiltinUncharged(lf));
        if (Dict == null) Slots[i] = fn;
        else Dict.Values[i] = fn;
        return fn;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteSlot(int i, JsValue v)
    {
        if (Dict == null) Slots[i] = v;
        else Dict.Values[i] = v;
    }

    internal void AddOwnProperty(in PropertyKey key, JsValue value, PropFlags flags)
    {
        Realm.ChargeProperty();
        if (Realm.BulkObjects != null)
        {
            if (Realm.TryBulkFill(this, key, value, flags)) return;
            if (Dict == null && !Shape.IsUnique) BeginBulk();
        }
        if (Dict != null)
        {
            Dict.Add(key, value, flags);
            if (IsPrototypeObject) Realm.ProtoEpoch++;
            return;
        }
        var next = Shape.AddProperty(key, flags);
        if (next == null)
        {
            ConvertToDictionary();
            Dict!.Add(key, value, flags);
            if (IsPrototypeObject) Realm.ProtoEpoch++;
            return;
        }
        int slot = Shape.Count;
        if (slot >= Slots.Length)
        {
            int n = Slots.Length == 0 ? 4 : Slots.Length * 2;
            Array.Resize(ref Slots, n);
        }
        Slots[slot] = value;
        Shape = next;
        if (IsPrototypeObject) Realm.ProtoEpoch++;
    }

    /// <summary>Fast-path append used by object literals and builtins setup: the key must not already exist.</summary>
    internal void FastAdd(in PropertyKey key, JsValue value, PropFlags flags = PropFlags.Default) => AddOwnProperty(key, value, flags);

    /// <summary>Intrinsic setup: collect properties in a dictionary and compact into one shape afterwards.</summary>
    private void BeginBulk()
    {
        ConvertToDictionary();
        Realm.BulkObjects!.Add(this);
    }

    /// <summary>Bulk-fill support: install a predicted (shared) final layout with no values filled yet.</summary>
    internal void InstallPredictedLayout(Shape shape, int filled)
    {
        var slots = new JsValue[shape.Count];
        System.Array.Copy(Slots, slots, filled);
        Slots = slots;
        Shape = shape;
    }

    /// <summary>Bulk-fill fallback: keep only the first <paramref name="filled"/> properties and continue in dictionary mode.</summary>
    internal void AbandonPredictedLayout(int filled)
    {
        var map = new PropertyMap(filled + 8);
        for (int i = 0; i < filled; i++) map.Add(Shape.Keys[i], Slots[i], Shape.Flags[i]);
        var proto = Proto;
        Dict = map;
        Slots = System.Array.Empty<JsValue>();
        Shape = Shape.CreateUnique(proto);
    }

    /// <summary>Converts a dictionary-mode object back to a single cacheable shape (used after intrinsic setup).</summary>
    internal void CompactToShape(Func<PropertyKey[], PropFlags[], (PropertyKey[], PropFlags[])>? shareArrays)
    {
        var map = Dict;
        if (map == null || map.LiveCount > Shape.MaxShapeProperties * 2) return;
        var proto = Proto;
        var keys = new PropertyKey[map.LiveCount];
        var flags = new PropFlags[map.LiveCount];
        var values = new JsValue[map.LiveCount];
        int w = 0;
        for (int i = 0; i < map.Count; i++)
        {
            if (!map.Live[i]) continue;
            keys[w] = map.Keys[i];
            flags[w] = map.Flags[i];
            values[w] = map.Values[i];
            w++;
        }
        if (shareArrays != null) (keys, flags) = shareArrays(keys, flags);
        Shape = Shape.CreateFromArrays(proto, keys, flags);
        Slots = values;
        Dict = null;
    }

    internal void ConvertToDictionary()
    {
        if (Dict != null) return;
        var map = new PropertyMap(Shape.Count + 4);
        for (int i = 0; i < Shape.Count; i++) map.Add(Shape.Keys[i], Slots[i], Shape.Flags[i]);
        Dict = map;
        Slots = NoSlots;
        Shape = Shape.CreateUnique(Proto);
        if (IsPrototypeObject) Realm.ProtoEpoch++;
    }

    private void SetFlags(int idx, PropFlags flags)
    {
        if (Dict == null)
        {
            if (Shape.Flags[idx] == flags) return;
            var next = Shape.WithFlags(idx, flags);
            if (next != null)
            {
                Shape = next;
                if (IsPrototypeObject) Realm.ProtoEpoch++;
                return;
            }
            ConvertToDictionary();
        }
        Dict!.Flags[idx] = flags;
        if (IsPrototypeObject) Realm.ProtoEpoch++;
    }

    // ------------------------------------------------------------------ internal methods

    public virtual JsObject? GetPrototypeOf() => Proto;

    public virtual bool SetPrototypeOf(JsObject? proto)
    {
        var current = Proto;
        if (ReferenceEquals(current, proto)) return true;
        if (!Extensible) return false;
        for (var p = proto; p != null; p = p.Proto)
        {
            if (ReferenceEquals(p, this)) return false;
            if (p is JsProxy) break;
        }
        ConvertToDictionary();
        Shape = Shape.CreateUnique(proto);
        if (proto != null) proto.IsPrototypeObject = true;
        Realm.ProtoEpoch++;
        return true;
    }

    public virtual bool IsExtensibleCheck() => Extensible;

    public virtual bool PreventExtensions()
    {
        Extensible = false;
        if (IsPrototypeObject) Realm.ProtoEpoch++;
        return true;
    }

    public virtual bool GetOwnProperty(in PropertyKey key, out PropertyDescriptor desc)
    {
        int i = FindOwn(key, out var f);
        if (i < 0) { desc = default; return false; }
        var v = ReadSlot(i);
        if ((f & PropFlags.Accessor) != 0)
        {
            var acc = (Accessor)v.Ref!;
            desc = PropertyDescriptor.AccessorDesc(acc.Getter, acc.Setter, f);
        }
        else desc = PropertyDescriptor.Data(v, f);
        return true;
    }

    public virtual bool DefineOwnProperty(in PropertyKey key, in PropertyDescriptor desc) => OrdinaryDefineOwnProperty(key, desc);

    internal bool OrdinaryDefineOwnProperty(in PropertyKey key, in PropertyDescriptor desc)
    {
        int i = FindOwn(key, out var cf);
        if (i < 0)
        {
            if (!IsExtensibleCheck()) return false;
            if (desc.IsAccessor)
            {
                var f = PropFlags.Accessor;
                if (desc.Enumerable) f |= PropFlags.Enumerable;
                if (desc.Configurable) f |= PropFlags.Configurable;
                AddOwnProperty(key, new JsValue(JsTag.Empty, 0, new Accessor(desc.Getter, desc.Setter)), f);
            }
            else
            {
                var f = PropFlags.None;
                if (desc.Writable) f |= PropFlags.Writable;
                if (desc.Enumerable) f |= PropFlags.Enumerable;
                if (desc.Configurable) f |= PropFlags.Configurable;
                AddOwnProperty(key, desc.HasValue ? desc.Value : JsValue.Undefined, f);
            }
            return true;
        }
        return ValidateAndApply(i, cf, desc);
    }

    private bool ValidateAndApply(int i, PropFlags cf, in PropertyDescriptor desc)
    {
        // { value } alone on a writable data property changes nothing else (Reflect.set through a receiver, for one)
        if ((cf & (PropFlags.Accessor | PropFlags.Writable)) == PropFlags.Writable && desc.HasValue && !desc.HasWritable && !desc.HasGet && !desc.HasSet && !desc.HasEnumerable && !desc.HasConfigurable)
        {
            WriteSlot(i, desc.Value);
            return true;
        }
        bool curConfigurable = (cf & PropFlags.Configurable) != 0;
        bool curEnumerable = (cf & PropFlags.Enumerable) != 0;
        bool curAccessor = (cf & PropFlags.Accessor) != 0;
        var curValue = ReadSlot(i);
        if (!curConfigurable)
        {
            if (desc.HasConfigurable && desc.Configurable) return false;
            if (desc.HasEnumerable && desc.Enumerable != curEnumerable) return false;
            if (!desc.IsGeneric && desc.IsAccessor != curAccessor) return false;
            if (curAccessor)
            {
                var acc = (Accessor)curValue.Ref!;
                if (desc.HasGet && !ReferenceEquals(desc.Getter, acc.Getter)) return false;
                if (desc.HasSet && !ReferenceEquals(desc.Setter, acc.Setter)) return false;
                return true;
            }
            if ((cf & PropFlags.Writable) == 0)
            {
                if (desc.HasWritable && desc.Writable) return false;
                if (desc.HasValue && !Operations.SameValue(desc.Value, curValue)) return false;
                return true;
            }
        }
        // apply
        var nf = PropFlags.None;
        bool enumerable = desc.HasEnumerable ? desc.Enumerable : curEnumerable;
        bool configurable = desc.HasConfigurable ? desc.Configurable : curConfigurable;
        if (enumerable) nf |= PropFlags.Enumerable;
        if (configurable) nf |= PropFlags.Configurable;
        if (desc.IsAccessor || (desc.IsGeneric && curAccessor))
        {
            nf |= PropFlags.Accessor;
            Accessor acc;
            if (curAccessor)
            {
                var old = (Accessor)curValue.Ref!;
                acc = new Accessor(desc.HasGet ? desc.Getter : old.Getter, desc.HasSet ? desc.Setter : old.Setter);
            }
            else acc = new Accessor(desc.Getter, desc.Setter);
            SetFlags(i, nf);
            i = FindOwn(Dict == null ? Shape.Keys[i] : Dict.Keys[i], out _);
            WriteSlot(i, new JsValue(JsTag.Empty, 0, acc));
        }
        else
        {
            bool writable = desc.HasWritable ? desc.Writable : (!curAccessor && (cf & PropFlags.Writable) != 0);
            if (writable) nf |= PropFlags.Writable;
            var key = Dict == null ? Shape.Keys[i] : Dict.Keys[i];
            SetFlags(i, nf);
            i = FindOwn(key, out _);
            if (desc.HasValue) WriteSlot(i, desc.Value);
            else if (curAccessor) WriteSlot(i, JsValue.Undefined);
        }
        return true;
    }

    public virtual bool HasProperty(in PropertyKey key)
    {
        JsObject? o = this;
        while (o != null)
        {
            if (o.IsExotic && !ReferenceEquals(o, this)) return o.HasProperty(key);
            if (o.IsExotic)
            {
                if (o.GetOwnProperty(key, out _)) return true;
            }
            else if (o.FindOwn(key, out _) >= 0) return true;
            o = o.GetPrototypeOf();
        }
        return false;
    }

    public virtual JsValue Get(in PropertyKey key, JsValue receiver) => OrdinaryGet(key, receiver);

    internal JsValue OrdinaryGet(in PropertyKey key, JsValue receiver)
    {
        JsObject o = this;
        while (true)
        {
            int i = o.FindOwn(key, out var f);
            if (i >= 0)
            {
                var v = o.ReadSlot(i);
                if ((f & PropFlags.Accessor) != 0)
                {
                    var getter = ((Accessor)v.Ref!).Getter;
                    if (getter == null) return JsValue.Undefined;
                    return Realm.Vm.Call(JsValue.FromObject(getter), receiver, ReadOnlySpan<JsValue>.Empty);
                }
                return v;
            }
            var p = o.Proto;
            if (p == null) return JsValue.Undefined;
            if (p.IsExotic) return p.Get(key, receiver);
            o = p;
        }
    }

    public virtual bool Set(in PropertyKey key, JsValue value, JsValue receiver) => OrdinarySet(key, value, receiver);

    internal bool OrdinarySet(in PropertyKey key, JsValue value, JsValue receiver)
    {
        JsObject o = this;
        bool first = true;
        while (true)
        {
            if (!first && o.IsExotic) return o.Set(key, value, receiver);
            PropertyDescriptor ownDesc;
            bool found;
            int i = -1;
            PropFlags f = PropFlags.None;
            if (o.IsExotic)
            {
                found = o.GetOwnProperty(key, out ownDesc);
            }
            else
            {
                i = o.FindOwn(key, out f);
                found = i >= 0;
                ownDesc = default;
            }
            if (found)
            {
                bool isAccessor = o.IsExotic ? ownDesc.IsAccessor : (f & PropFlags.Accessor) != 0;
                if (isAccessor)
                {
                    var setter = o.IsExotic ? ownDesc.Setter : ((Accessor)o.ReadSlot(i).Ref!).Setter;
                    if (setter == null) return false;
                    Realm.Vm.Call(JsValue.FromObject(setter), receiver, new ReadOnlySpan<JsValue>(in value));
                    return true;
                }
                bool writable = o.IsExotic ? ownDesc.Writable : (f & PropFlags.Writable) != 0;
                if (!writable) return false;
                if (!o.IsExotic && receiver.IsObject && ReferenceEquals(receiver.Ref, o))
                {
                    o.WriteSlot(i, value);
                    return true;
                }
                break;
            }
            var p = o.GetPrototypeOf();
            if (p == null) break;
            o = p;
            first = false;
        }
        if (receiver.IsNotObject) return false;
        var recv = receiver.AsObjectUnchecked;
        if (recv.GetOwnProperty(key, out var existing))
        {
            if (existing.IsAccessor) return false;
            if (!existing.Writable) return false;
            var vd = new PropertyDescriptor { Value = value, HasValue = true };
            return recv.DefineOwnProperty(key, vd);
        }
        return recv.CreateDataProperty(key, value);
    }

    internal bool CreateDataProperty(in PropertyKey key, JsValue value)
    {
        if (!IsExotic && Dict == null && Extensible)
        {
            int i = FindOwn(key, out var f);
            if (i < 0) { AddOwnProperty(key, value, PropFlags.Default); return true; }
            if (f == PropFlags.Default) { WriteSlot(i, value); return true; }
        }
        return DefineOwnProperty(key, PropertyDescriptor.Data(value, PropFlags.Default));
    }

    public virtual bool Delete(in PropertyKey key)
    {
        int i = FindOwn(key, out var f);
        if (i < 0) return true;
        if ((f & PropFlags.Configurable) == 0) return false;
        ConvertToDictionary();
        i = Dict!.IndexOf(key);
        Dict.Remove(i);
        if (IsPrototypeObject) Realm.ProtoEpoch++;
        return true;
    }

    /// <summary>Appends own keys in spec order: integer indices ascending, strings in creation order, symbols in creation order.</summary>
    public virtual void OwnPropertyKeys(List<PropertyKey> keys) => OrdinaryOwnKeys(keys);

    internal void OrdinaryOwnKeys(List<PropertyKey> keys)
    {
        int start = keys.Count;
        bool hasIndex, hasSymbol;
        if (Dict == null)
        {
            hasIndex = Shape.HasIndexKeys;
            hasSymbol = Shape.HasSymbolKeys;
            if (!hasIndex && !hasSymbol)
            {
                for (int i = 0; i < Shape.Count; i++) keys.Add(Shape.Keys[i]);
                return;
            }
        }
        else
        {
            hasIndex = Dict.HasIndexKeys;
            hasSymbol = Dict.HasSymbolKeys;
        }
        var all = new List<PropertyKey>();
        if (Dict == null) for (int i = 0; i < Shape.Count; i++) all.Add(Shape.Keys[i]);
        else for (int i = 0; i < Dict.Count; i++) if (Dict.Live[i]) all.Add(Dict.Keys[i]);
        if (hasIndex)
        {
            var idx = new List<(uint, PropertyKey)>();
            foreach (var k in all) if (k.TryGetArrayIndex(out uint n)) idx.Add((n, k));
            idx.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            foreach (var (_, k) in idx) keys.Add(k);
        }
        foreach (var k in all) if (k.IsString && (!hasIndex || !k.TryGetArrayIndex(out _))) keys.Add(k);
        if (hasSymbol) foreach (var k in all) if (k.IsSymbol) keys.Add(k);
    }

    // ------------------------------------------------------------------ indexed fast paths (overridden by arrays)

    public virtual JsValue GetIndex(uint index, JsValue receiver) => Get(PropertyKey.FromIndex(index), receiver);
    public virtual bool SetIndex(uint index, JsValue value, JsValue receiver) => Set(PropertyKey.FromIndex(index), value, receiver);

    // ------------------------------------------------------------------ helpers

    internal JsValue Get(in PropertyKey key) => Get(key, JsValue.FromObject(this));

    internal void SetOrThrow(in PropertyKey key, JsValue value)
    {
        if (!Set(key, value, JsValue.FromObject(this)))
            throw JsErrors.TypeError(Realm.Vm.Realm, $"Cannot assign to read only property '{key}' of object");
    }

    internal void DefinePropertyOrThrow(in PropertyKey key, in PropertyDescriptor desc)
    {
        if (!DefineOwnProperty(key, desc))
            throw JsErrors.TypeError(Realm.Vm.Realm, $"Cannot redefine property: {key}");
    }

    internal void CreateDataPropertyOrThrow(in PropertyKey key, JsValue value)
    {
        if (!CreateDataProperty(key, value))
            throw JsErrors.TypeError(Realm.Vm.Realm, $"Cannot define property {key}, object is not extensible");
    }

    /// <summary>Defines a builtin method/value property during intrinsic setup.</summary>
    internal void DefineHidden(in PropertyKey key, JsValue value) => AddOwnProperty(key, value, PropFlags.Hidden);

    internal void DefineReadOnly(in PropertyKey key, JsValue value) => AddOwnProperty(key, value, PropFlags.None);

    internal void DefineAccessor(in PropertyKey key, JsObject? getter, JsObject? setter, PropFlags flags = PropFlags.Configurable)
        => AddOwnProperty(key, new JsValue(JsTag.Empty, 0, new Accessor(getter, setter)), flags | PropFlags.Accessor);

    internal bool HasOwnProperty(in PropertyKey key)
    {
        // a miss on the global object may name a builtin of a not yet materialized group (fresh realms)
        if (!IsExotic) return FindOwn(key, out _) >= 0 || (this is JsClosure c && c.IsLegacyKey(key)) || (this is JsGlobalObject && GetOwnProperty(key, out _));
        return GetOwnProperty(key, out _);
    }

    internal bool IsCallable => this is JsFunctionObject;

    /// <summary>Whether a named-property lookup of <paramref name="key"/> on this object is fully described by its shape.</summary>
    internal virtual bool CanCacheNamed(in PropertyKey key) => !IsExotic;
}
