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
        throw new JsThrowException(NotCallableError(fv));
    }

    /// <summary>
    /// Runs <c>f.call(thisArg, ...)</c>, <c>f.apply(thisArg, array)</c> and a call of a bound function as an in-loop frame push when
    /// the target is a plain closure, instead of entering the builtin, <see cref="JsFunctionObject.Call"/> and a nested
    /// <c>RunLoop</c>. Returns the pushed frame (the caller sets its RetIndex and switches to it) or null when the call has to
    /// take the general path: targets that are generators, async functions or class constructors (which throw), bound
    /// functions whose target is not a closure, <c>apply</c> with anything but a dense hole-free array. <paramref name="callee"/>
    /// is the function object being invoked (the call/apply builtin or the bound function), <paramref name="receiver"/> its
    /// this value (for call/apply: the target). The interpreter has already stored the continuation pc in the caller frame.
    /// </summary>
    private Frame? TryPushRedirectedCall(JsFunctionObject callee, in JsValue receiver, JsValue[] regs, int argStart, int argc)
    {
        if (callee is JsBuiltin b)
        {
            if (receiver.IsNotObject || receiver.Ref is not JsClosure target || (target.Template.Flags & InlineCallMask) != 0) return null;
            if (ReferenceEquals(b.Fn, FunctionBuiltins.CallFn))
            {
                if (--_ticks <= 0) Checkpoint();
                // the target's arguments are the caller's arguments after the first; the this register (argStart) stays below
                // the new window, which is what PushCallFrame requires
                return argc == 0 ? PushCallFrame(target, regs, -1, argStart, 0) : PushCallFrame(target, regs, argStart, argStart + 1, argc - 1);
            }
            if (ReferenceEquals(b.Fn, FunctionBuiltins.ApplyFn))
            {
                var thisArg = argc > 0 ? regs[argStart] : JsValue.Undefined;
                var list = argc > 1 ? regs[argStart + 1] : JsValue.Undefined;
                ReadOnlySpan<JsValue> args;
                if (list.IsNullOrUndefined) args = ReadOnlySpan<JsValue>.Empty;
                else if (list.Ref is JsArray arr && arr.Dense && arr.Length <= arr.Items.Length && Array.IndexOf(arr.Items, JsValue.Empty, 0, (int)arr.Length) < 0)
                    args = new ReadOnlySpan<JsValue>(arr.Items, 0, (int)arr.Length);
                else return null;
                if (--_ticks <= 0) Checkpoint();
                return PushFrame(target, thisArg, args, default, FrameFlags.None);
            }
            return null;
        }
        if (callee is JsBoundFunction bf && bf.Target is JsClosure bt && (bt.Template.Flags & InlineCallMask) == 0)
        {
            if (--_ticks <= 0) Checkpoint();
            var bound = bf.BoundArgs;
            if (bound.Length == 0) return PushFrame(bt, bf.BoundThis, new ReadOnlySpan<JsValue>(regs, argStart, argc), default, FrameFlags.None);
            // stage bound + call arguments in the tail-call buffer: PushFrame copies them before any user code runs
            int n = bound.Length + argc;
            if (_tailArgs.Length < n) _tailArgs = new JsValue[Math.Max(n, _tailArgs.Length * 2)];
            bound.CopyTo(_tailArgs, 0);
            Array.Copy(regs, argStart, _tailArgs, bound.Length, argc);
            var nf = PushFrame(bt, bf.BoundThis, new ReadOnlySpan<JsValue>(_tailArgs, 0, n), default, FrameFlags.None);
            Array.Clear(_tailArgs, 0, n);
            return nf;
        }
        return null;
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

    /// <summary>
    /// Object spread / rest from an ordinary shaped object whose properties are all plain data properties in insertion
    /// order (no accessors, index or symbol keys): nothing observable runs, so the keys and values are read straight from
    /// the shape and slots. An empty target with the same prototype takes the source's shape and a copy of its slots.
    /// </summary>
    private bool TryCopyPlainDataProperties(JsObject target, JsObject so, ReadOnlySpan<JsValue> excluded)
    {
        if (so.GetType() != typeof(JsObject) || so.Dict != null) return false;
        var shape = so.Shape;
        if (shape.IsUnique || shape.HasSpecialProps || shape.HasIndexKeys || shape.HasSymbolKeys) return false;
        int n = shape.Count;
        var slots = so.Slots;
        if (target.GetType() == typeof(JsObject) && target.Dict == null && target.Extensible && excluded.Length == 0)
        {
            var ts = target.Shape;
            if (ts.Count == 0 && ts.Role == shape.Role && ReferenceEquals(ts.Prototype, shape.Prototype) && !ts.IsUnique)
            {
                bool allDefault = true;
                var flags = shape.Flags;
                for (int i = 0; i < n; i++) if (flags[i] != PropFlags.Default) { allDefault = false; break; }
                if (allDefault)
                {
                    if (n > 0)
                    {
                        var copy = new JsValue[n];
                        Array.Copy(slots, copy, n);
                        target.Slots = copy;
                        target.Shape = shape;
                        Realm.ChargeProperties(n);
                    }
                    Tick(n + 1);
                    return true;
                }
            }
        }
        for (int i = 0; i < n; i++)
        {
            if ((shape.Flags[i] & PropFlags.Enumerable) == 0) continue;
            var key = shape.Keys[i];
            bool skip = false;
            foreach (var ex in excluded)
            {
                if (ex.IsString && key.Value is string ks && ks == ex.AsStringUnchecked) { skip = true; break; }
                if (ex.IsSymbol && ReferenceEquals(ex.Ref, key.Value)) { skip = true; break; }
                if (ex.IsNumber && key.Value is string kn && kn == NumberFormatting.ToString(ex.Num)) { skip = true; break; }   // array-index keys stay numeric
            }
            if (skip) continue;
            target.CreateDataPropertyOrThrow(key, slots[i]);
            Tick();
        }
        return true;
    }

    // ------------------------------------------------------------------ named properties

    // Error values for the faults the interpreter raises itself. Raising them with `thrown = ...; goto Unwind` avoids
    // a .NET exception (several microseconds each); the same messages back the throwing slow paths below.
    private JsValue ErrorValue(ErrorKind kind, string message) => JsValue.FromObject(JsError.Create(Realm, kind, message));

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal JsValue NullishReadError(in JsValue ov, in JsValue kv) =>
        ErrorValue(ErrorKind.TypeError, $"Cannot read properties of {ov} (reading '{(kv.IsObject || kv.IsSymbol ? JsErrors.Describe(kv) : Operations.ToString(Realm, kv))}')");

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal JsValue NullishSetError(in JsValue ov, in JsValue kv) =>
        ErrorValue(ErrorKind.TypeError, $"Cannot set properties of {ov} (setting '{Operations.ToString(Realm, kv)}')");

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal JsValue NotCallableError(in JsValue fv) => ErrorValue(ErrorKind.TypeError, $"{JsErrors.Describe(fv)} is not a function");

    internal JsValue GetPropSlow(in JsValue ov, in JsValue keyValue, ref IcEntry ic)
    {
        var key = new PropertyKey(keyValue.AsStringUnchecked);
        if (ov.IsNotObject)
        {
            if (ov.IsNullOrUndefined)
                throw new JsThrowException(NullishReadError(ov, keyValue));
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
        if (obj.Dict != null || !obj.CanCacheNamed(key)) return obj.Get(key, ov);

        if (ic.Kind == IcKind.Megamorphic)
        {
            // a site that has seen many shapes: Vm-wide (shape, name) → lookup result, so the lookup is a hash probe
            // instead of a walk of the prototype chain with a dictionary lookup per object
            if (keyValue.Ref is string ks && !obj.Shape.IsUnique)
            {
                var shape = obj.Shape;
                int h = NamedHash(shape, ks);
                if (ReferenceEquals(_npShape[h], shape) && ReferenceEquals(_npKey[h], ks))
                {
                    byte ck = _npKind[h];
                    if (ck is IcKind.OwnData or IcKind.OwnGetter || _npEpoch[h] == Realm.ProtoEpoch)
                        return NamedResult(ck, obj, _npHolder[h], _npSlot[h], ov);
                }
                if (!LookupNamed(obj, key, out var mk, out var mh, out var ms, out bool mc)) return obj.Get(key, ov);
                if (mc || mk is IcKind.OwnData or IcKind.OwnGetter)
                {
                    _npShape[h] = shape; _npKey[h] = ks; _npKind[h] = mk; _npHolder[h] = mh; _npSlot[h] = ms; _npEpoch[h] = Realm.ProtoEpoch;
                }
                return NamedResult(mk, obj, mh, ms, ov);
            }
            return obj.Get(key, ov);
        }

        if (!LookupNamed(obj, key, out var kind, out var holder, out var slot, out bool cacheable)) return obj.Get(key, ov);
        if (cacheable || kind is IcKind.OwnData or IcKind.OwnGetter) FillIc(ref ic, kind, obj.Shape, holder, slot);
        return NamedResult(kind, obj, holder, slot, ov);
    }

    /// <summary>
    /// Resolves a named property on an ordinary (shape-mode) receiver the way the inline caches model it: own data,
    /// own accessor, data or accessor on a shape-mode prototype, or absent. False when an exotic prototype must answer
    /// (the caller falls back to the generic [[Get]]). <paramref name="cacheable"/> is false when a dictionary-mode
    /// object on the chain makes a prototype result or an absence unsafe to cache.
    /// </summary>
    private static bool LookupNamed(JsObject obj, in PropertyKey key, out byte kind, out JsObject? holder, out int slot, out bool cacheable)
    {
        holder = null;
        cacheable = true;
        slot = obj.FindOwn(key, out var flags);
        if (slot >= 0)
        {
            kind = (flags & PropFlags.Accessor) != 0 ? IcKind.OwnGetter : IcKind.OwnData;
            return true;
        }
        for (var p = obj.Proto; p != null; p = p.Proto)
        {
            if (p.IsExotic && !p.CanCacheNamed(key)) { kind = IcKind.None; return false; }
            if (p.Dict != null) cacheable = false;
            int ps = p.FindOwn(key, out var pf);
            if (ps >= 0)
            {
                holder = p;
                slot = ps;
                kind = (pf & PropFlags.Accessor) != 0 ? IcKind.ProtoGetter : IcKind.ProtoData;
                return true;
            }
        }
        kind = IcKind.NotFound;
        return true;
    }

    /// <summary>The value of a <see cref="LookupNamed"/> result (runs the getter for accessor kinds).</summary>
    private JsValue NamedResult(byte kind, JsObject obj, JsObject? holder, int slot, in JsValue receiver)
    {
        switch (kind)
        {
            case IcKind.OwnData: return obj.ReadSlot(slot);
            case IcKind.ProtoData: return holder!.ReadSlot(slot);
            case IcKind.OwnGetter: return CallCachedGetter((Accessor)obj.ReadSlot(slot).Ref!, receiver);
            case IcKind.ProtoGetter: return CallCachedGetter((Accessor)holder!.ReadSlot(slot).Ref!, receiver);
            default: return JsValue.Undefined;
        }
    }

    // Vm-wide named-property cache behind megamorphic sites: (shape, name) → kind/holder/slot (+ ProtoEpoch for
    // prototype results and absences). A shape is an immutable layout and names are the sites' constant strings
    // (matched by reference), so an entry is either exact or a miss.
    private const int NamedCacheSize = 1024;
    private readonly Shape?[] _npShape = new Shape?[NamedCacheSize];
    private readonly object?[] _npKey = new object?[NamedCacheSize];
    private readonly byte[] _npKind = new byte[NamedCacheSize];
    private readonly JsObject?[] _npHolder = new JsObject?[NamedCacheSize];
    private readonly int[] _npSlot = new int[NamedCacheSize];
    private readonly int[] _npEpoch = new int[NamedCacheSize];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int NamedHash(Shape shape, string key) => (RuntimeHelpers.GetHashCode(shape) * 31 + RuntimeHelpers.GetHashCode(key)) & (NamedCacheSize - 1);

    /// <summary>Invokes the getter of a cached accessor slot (an <see cref="Accessor"/> value) on <paramref name="receiver"/>.</summary>
    internal JsValue CallCachedGetter(Accessor accessor, in JsValue receiver)
    {
        var getter = accessor.Getter;
        return getter == null ? JsValue.Undefined : Call(JsValue.FromObject(getter), receiver, ReadOnlySpan<JsValue>.Empty);
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
            throw new JsThrowException(NullishReadError(ov, kv));
        return Operations.GetPrimitiveProperty(Realm, ov, Operations.ToPropertyKey(Realm, kv));
    }

    // ------------------------------------------------------------------ computed-key (keyed) access cache
    //
    // obj[k] on an ordinary object: the site IC remembers one (shape, key) → slot; a site that cycles through many
    // keys (obj[d.g] in a loop, objects used as maps) would miss on every access, so behind it sits a Vm-wide
    // direct-mapped cache of (shape, key) → slot, like V8's megamorphic stub cache. A shape is an immutable key→slot
    // layout, so an entry can never go stale; keys are matched by reference (the same string instance, or the same
    // array index), anything else takes the generic lookup and refreshes the entry.

    private const int KeyedCacheSize = 1024;
    private readonly Shape?[] _kcShape = new Shape?[KeyedCacheSize];
    private readonly object?[] _kcKey = new object?[KeyedCacheSize];
    private readonly int[] _kcIndex = new int[KeyedCacheSize];
    private readonly int[] _kcSlot = new int[KeyedCacheSize];
    private readonly bool[] _kcWritable = new bool[KeyedCacheSize];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int KeyedHash(Shape shape, object? keyRef, int index)
    {
        int h = RuntimeHelpers.GetHashCode(shape) * 31;
        h += keyRef != null ? RuntimeHelpers.GetHashCode(keyRef) : index;
        return h & (KeyedCacheSize - 1);
    }

    /// <summary>The cacheable form of a computed key: a string instance, or an array index (keyRef null). False otherwise.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool KeyedForm(in JsValue kv, out object? keyRef, out int index)
    {
        keyRef = null; index = -1;
        if (kv.IsNumber)
        {
            double d = kv.Num;
            int i = (int)d;
            if (i == d && i >= 0) { index = i; return true; }
            return false;
        }
        if (kv.IsString && kv.Ref is string s) { keyRef = s; return true; }
        return false;
    }

    /// <summary>Finds the own data slot for (obj, key) through the Vm-wide cache, falling back to the shape lookup; -1 when not an own data property.</summary>
    private int KeyedSlot(JsObject obj, object? keyRef, int index, out bool writable)
    {
        var shape = obj.Shape;
        int h = KeyedHash(shape, keyRef, index);
        if (ReferenceEquals(_kcShape[h], shape) && ReferenceEquals(_kcKey[h], keyRef) && _kcIndex[h] == index)
        {
            writable = _kcWritable[h];
            return _kcSlot[h];
        }
        var key = keyRef != null ? new PropertyKey((string)keyRef) : PropertyKey.FromIndex((uint)index);
        int slot = obj.FindOwn(key, out var flags);
        writable = (flags & PropFlags.Writable) != 0;
        if (slot < 0 || (flags & PropFlags.Accessor) != 0) return -1;
        _kcShape[h] = shape; _kcKey[h] = keyRef; _kcIndex[h] = index; _kcSlot[h] = slot; _kcWritable[h] = writable;
        return slot;
    }

    private void FillKeyedIc(ref IcEntry ic, JsObject obj, object? keyRef, int index, int slot)
    {
        if (ic.Kind == IcKind.Megamorphic) return;
        if (ic.Kind != IcKind.None && (!ReferenceEquals(ic.Shape, obj.Shape) || !ReferenceEquals(ic.Cell, keyRef) || ic.Epoch != index))
        {
            if (++ic.Misses > MaxIcMisses) { ic.Kind = IcKind.Megamorphic; ic.Shape = null; ic.Holder = null; ic.Cell = null; return; }
        }
        ic.Kind = IcKind.KeyedOwnData;
        ic.Shape = obj.Shape;
        ic.Holder = null;
        ic.Slot = slot;
        ic.Cell = keyRef;
        ic.Epoch = index;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool KeyedCacheable(JsObject obj) => obj.GetType() == typeof(JsObject) && obj.Dict == null && !obj.Shape.IsUnique;

    /// <summary>GetElem on an object receiver: cached own-data read when possible, the generic read otherwise.</summary>
    internal JsValue GetElemKeyed(in JsValue ov, in JsValue kv, ref IcEntry ic)
    {
        var obj = ov.AsObjectUnchecked;
        if (KeyedCacheable(obj) && KeyedForm(kv, out var keyRef, out int index))
        {
            int slot = KeyedSlot(obj, keyRef, index, out _);
            if (slot >= 0)
            {
                var v = obj.Slots[slot];
                if (v.IsNotEmpty) { FillKeyedIc(ref ic, obj, keyRef, index, slot); return v; }
            }
        }
        return GetElemSlow(ov, kv);
    }

    /// <summary>SetElem on an object receiver: cached own writable data write when possible, the generic write otherwise.</summary>
    internal void SetElemKeyed(in JsValue ov, in JsValue kv, JsValue value, bool strict, ref IcEntry ic)
    {
        var obj = ov.AsObjectUnchecked;
        if (KeyedCacheable(obj) && KeyedForm(kv, out var keyRef, out int index))
        {
            int slot = KeyedSlot(obj, keyRef, index, out bool writable);
            if (slot >= 0 && writable)
            {
                FillKeyedIc(ref ic, obj, keyRef, index, slot);
                obj.Slots[slot] = value;
                return;
            }
        }
        SetElemSlow(ov, kv, value, strict);
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
        if (source.IsObject && source.Ref is JsObject so && TryCopyPlainDataProperties(target, so, excluded)) return;
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
                if (ex.IsNumber && key.Value is string kn && kn == NumberFormatting.ToString(ex.Num)) { skip = true; break; }   // array-index keys stay numeric
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
