using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Jaunt.Runtime;

internal sealed class SameValueZeroComparer : IEqualityComparer<JsValue>
{
    public static readonly SameValueZeroComparer Instance = new();
    public bool Equals(JsValue x, JsValue y) => Operations.SameValueZero(x, y);
    public int GetHashCode(JsValue v) => v.Tag switch
    {
        JsTag.Number => v.Num == 0 ? 0 : double.IsNaN(v.Num) ? 1 : v.Num.GetHashCode(),
        JsTag.String => v.AsStringUnchecked.GetHashCode(),
        JsTag.BigInt => v.BigIntValue.GetHashCode(),
        JsTag.Boolean => (int)v.Bits + 7,
        JsTag.Undefined or JsTag.Null => (int)v.Tag,
        _ => v.Ref is JsObject o ? o.IdentityHash : RuntimeHelpers.GetHashCode(v.Ref!),
    };
}

internal sealed class TableCursor { public int Index; public TableEpoch Epoch = null!; }

/// <summary>
/// One generation of an <see cref="OrderedTable"/>'s slot numbering. Compaction closes the current epoch by recording how
/// old slot indices map to new ones and linking the next epoch; a cursor that is behind replays those maps on its next
/// step. Epochs are only reachable from cursors, so they are collected as soon as no iterator is behind.
/// </summary>
internal sealed class TableEpoch { public int[]? Remap; public TableEpoch? Next; }

/// <summary>
/// Insertion-ordered hash table with live-iteration semantics (Map/Set). Entries live in parallel arrays in insertion
/// order (a deleted entry leaves a dead slot until the next compaction); a chained hash index over the slots replaces
/// the previous <c>Dictionary&lt;JsValue, int&gt;</c>: ~40 bytes per entry instead of ~70 (fewer and smaller
/// large-object-heap arrays for big collections), the hash is computed once per operation with number/string fast
/// paths, and a Set stores no value array.
/// </summary>
internal sealed class OrderedTable
{
    private JsValue[] _keys = new JsValue[8];
    /// <summary>null for a Set: the key is the value.</summary>
    private JsValue[]? _values;
    /// <summary>31-bit hash of each slot's key; -1 marks a dead (deleted) slot.</summary>
    private int[] _hashes = new int[8];
    /// <summary>Next slot in the same bucket's chain, or -1.</summary>
    private int[] _next = new int[8];
    /// <summary>Slot index + 1 of the first entry in each chain (0 = empty); the length is a power of two.</summary>
    private int[] _buckets = new int[16];
    private int _count;
    private int _liveCount;
    private TableEpoch _epoch = new();
    /// <summary>Every slot before this index is dead, so a cursor may skip straight to it.</summary>
    private int _head;

    public OrderedTable(bool isSet)
    {
        if (!isSet) _values = new JsValue[8];
    }

    public int Size => _liveCount;
    public int End => _count;

    /// <summary>SameValueZero hash (31-bit): numbers by value (-0 and 0 alike, every NaN alike), strings by content
    /// (the runtime's randomized ordinal hash, so attacker-chosen keys cannot be made to collide), objects by identity.</summary>
    private static int HashOf(in JsValue v)
    {
        uint h;
        if (v.IsNumber)
        {
            double d = v.Num;
            if (d == 0) return 0;
            if (double.IsNaN(d)) return 1;
            long bits = BitConverter.DoubleToInt64Bits(d);
            h = (uint)bits ^ (uint)(bits >> 32);
        }
        else if (v.IsString) return v.AsStringUnchecked.GetHashCode() & 0x7FFFFFFF;   // Marvin: already well mixed
        else if (v.IsObject) h = (uint)Unsafe.As<JsObject>(v.Ref!).IdentityHash;
        else h = (uint)SameValueZeroComparer.Instance.GetHashCode(v);
        // the buckets are indexed by the low bits: an integer-valued double differs from its neighbours only in its
        // high bits, so the raw bits are mixed (MurmurHash3 finalizer) before use
        h ^= h >> 16;
        h *= 0x85EBCA6B;
        h ^= h >> 13;
        h *= 0xC2B2AE35;
        h ^= h >> 16;
        return (int)(h & 0x7FFFFFFF);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool KeyEquals(in JsValue a, in JsValue b)
    {
        if (a.IsNumber) return b.IsNumber && (a.Num == b.Num || (double.IsNaN(a.Num) && double.IsNaN(b.Num)));
        if (a.IsString) return b.IsString && a.StringLength == b.StringLength && string.Equals(a.AsStringUnchecked, b.AsStringUnchecked);
        if (a.IsObject) return ReferenceEquals(a.Ref, b.Ref);
        return Operations.SameValueZero(a, b);
    }

    private int Find(in JsValue key, int h)
    {
        var buckets = _buckets;
        var hashes = _hashes;
        var keys = _keys;
        for (int i = buckets[h & (buckets.Length - 1)] - 1; i >= 0; i = _next[i])
            if (hashes[i] == h && KeyEquals(keys[i], key)) return i;
        return -1;
    }

    public bool TryGet(in JsValue key, out JsValue value)
    {
        int i = Find(key, HashOf(key));
        if (i >= 0) { value = _values == null ? _keys[i] : _values[i]; return true; }
        value = default;
        return false;
    }

    public bool Has(in JsValue key) => Find(key, HashOf(key)) >= 0;

    public void Set(Realm realm, JsValue key, JsValue value)
    {
        if (key.IsNumber && key.Num == 0) key = JsValue.Zero; // normalize -0
        int h = HashOf(key);
        int i = Find(key, h);
        if (i >= 0) { if (_values != null) _values[i] = value; return; }
        if (_count == _keys.Length) Grow();
        realm.ChargeBytes(64);
        i = _count++;
        _keys[i] = key;
        if (_values != null) _values[i] = value;
        _hashes[i] = h;
        int b = h & (_buckets.Length - 1);
        _next[i] = _buckets[b] - 1;
        _buckets[b] = i + 1;
        _liveCount++;
    }

    public bool Delete(in JsValue key)
    {
        int h = HashOf(key);
        int b = h & (_buckets.Length - 1);
        int prev = -1;
        for (int i = _buckets[b] - 1; i >= 0; prev = i, i = _next[i])
        {
            if (_hashes[i] != h || !KeyEquals(_keys[i], key)) continue;
            if (prev < 0) _buckets[b] = _next[i] + 1;
            else _next[prev] = _next[i];
            _hashes[i] = -1;
            _keys[i] = default;
            if (_values != null) _values[i] = default;
            _liveCount--;
            if (i == _head) { while (_head < _count && _hashes[_head] < 0) _head++; }
            return true;
        }
        return false;
    }

    public void Clear()
    {
        for (int i = 0; i < _count; i++) { _hashes[i] = -1; _keys[i] = default; }
        if (_values != null) Array.Clear(_values, 0, _count);
        Array.Clear(_buckets);
        _liveCount = 0;
        _head = _count;
    }

    public TableCursor CreateCursor() => new TableCursor { Epoch = _epoch };

    /// <summary>Advances the cursor to the next live entry.</summary>
    public bool Next(TableCursor c, out JsValue key, out JsValue value)
    {
        while (c.Epoch != _epoch)
        {
            var remap = c.Epoch.Remap!;
            c.Index = remap[Math.Min(c.Index, remap.Length - 1)];
            c.Epoch = c.Epoch.Next!;
        }
        if (c.Index < _head) c.Index = _head;
        while (c.Index < _count)
        {
            int i = c.Index++;
            if (_hashes[i] >= 0) { key = _keys[i]; value = _values == null ? key : _values[i]; return true; }
        }
        key = default;
        value = default;
        return false;
    }

    /// <summary>Makes room for one more slot: compacts when at least half the slots are dead, otherwise doubles.</summary>
    private void Grow()
    {
        if (_liveCount < _count / 2) { Compact(); return; }
        int n = _keys.Length * 2;
        Array.Resize(ref _keys, n);
        if (_values != null) Array.Resize(ref _values, n);
        Array.Resize(ref _hashes, n);
        Array.Resize(ref _next, n);
        RebuildIndex(n * 2);
    }

    private void RebuildIndex(int bucketCount)
    {
        var buckets = _buckets.Length == bucketCount ? _buckets : new int[bucketCount];
        if (ReferenceEquals(buckets, _buckets)) Array.Clear(buckets);
        int mask = bucketCount - 1;
        for (int i = 0; i < _count; i++)
        {
            int h = _hashes[i];
            if (h < 0) continue;
            int b = h & mask;
            _next[i] = buckets[b] - 1;
            buckets[b] = i + 1;
        }
        _buckets = buckets;
    }

    private void Compact()
    {
        var remap = new int[_count + 1];
        int w = 0;
        for (int r = 0; r < _count; r++)
        {
            remap[r] = w;
            if (_hashes[r] < 0) continue;
            _keys[w] = _keys[r];
            if (_values != null) _values[w] = _values[r];
            _hashes[w] = _hashes[r];
            w++;
        }
        remap[_count] = w;
        for (int i = w; i < _count; i++) { _keys[i] = default; _hashes[i] = -1; }
        if (_values != null) Array.Clear(_values, w, _count - w);
        _count = w;
        _head = 0;
        RebuildIndex(_buckets.Length);
        var next = new TableEpoch();
        _epoch.Remap = remap;
        _epoch.Next = next;
        _epoch = next;
    }
}

internal sealed class JsMap : JsObject
{
    public readonly OrderedTable Table;
    public readonly bool IsSet;
    public JsMap(Realm realm, JsObject proto, bool isSet) : base(realm, proto) { IsSet = isSet; Table = new OrderedTable(isSet); }
    public override string ClassName => IsSet ? "Set" : "Map";
}

internal sealed class JsMapIterator : JsObject
{
    public OrderedTable? Table;
    public readonly TableCursor Cursor;
    public readonly byte Kind; // 0 keys, 1 values, 2 entries
    public JsMapIterator(Realm realm, JsObject proto, OrderedTable table, byte kind) : base(realm, proto)
    {
        Table = table;
        Cursor = table.CreateCursor();
        Kind = kind;
    }
}

internal sealed class JsWeakCollection : JsObject
{
    public readonly ConditionalWeakTable<object, StrongBox<JsValue>> Table = new();
    public readonly bool IsSet;
    public JsWeakCollection(Realm realm, JsObject proto, bool isSet) : base(realm, proto) => IsSet = isSet;
}

internal static class MapSetBuiltins
{
    public static void Setup(Realm r)
    {
        SetupMap(r);
        SetupSet(r);
        SetupWeak(r);
        WeakRefBuiltins.Setup(r);
    }

    internal static JsMap ThisMap(in NativeArgs a, bool set, string method)
    {
        if (a.This.Ref is JsMap m && m.IsSet == set) return m;
        throw JsErrors.TypeError(a.Realm, $"Method {(set ? "Set" : "Map")}.prototype.{method} called on incompatible receiver {a.This}");
    }

    private static void SetupMap(Realm r)
    {
        var proto = new JsObject(r, r.ObjectPrototype);
        r.MapPrototype = proto;
        var ctor = r.Constructor("Map", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (!a.IsConstruct) throw JsErrors.TypeError(realm, "Constructor Map requires 'new'");
            var p = Operations.GetPrototypeFromConstructor(realm, a.NewTarget.AsObjectUnchecked, realm.MapPrototype);
            var map = new JsMap(realm, p, false);
            if (!a[0].IsNullOrUndefined)
            {
                var adder = map.Get("set");
                if (!adder.IsFunction) throw JsErrors.TypeError(realm, "'set' is not a function");
                bool fast = ReferenceEquals(adder.Ref, realm.MapSetFunction);
                var it = IteratorRecord.Get(realm, a[0]);
                var args = new JsValue[2];
                while (it.Step(out var entry))
                {
                    try
                    {
                        if (entry.IsNotObject) throw JsErrors.TypeError(realm, $"Iterator value {entry} is not an entry object");
                        var eo = entry.AsObjectUnchecked;
                        var k = eo.GetIndex(0, entry);
                        var v = eo.GetIndex(1, entry);
                        if (fast) map.Table.Set(realm, k, v);
                        else { args[0] = k; args[1] = v; realm.Vm.Call(adder, JsValue.FromObject(map), args); }
                    }
                    catch (JsThrowException) { it.CloseQuiet(); throw; }
                    realm.Vm.Tick();
                }
            }
            return JsValue.FromObject(map);
        }, proto);
        r.DefineGlobal("Map", JsValue.FromObject(ctor));
        Es2026Builtins.InstallMap(r, proto);
        r.Getter(ctor, JsSymbol.Species, "[Symbol.species]", static (in NativeArgs a) => a.This);
        r.Method(ctor, "groupBy", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var cb = a[1];
            if (!cb.IsFunction) throw JsErrors.TypeError(realm, "callback is not a function");
            var map = new JsMap(realm, realm.MapPrototype, false);
            var it = IteratorRecord.Get(realm, a[0]);
            double k = 0;
            var args = new JsValue[2];
            while (it.Step(out var v))
            {
                args[0] = v; args[1] = new JsValue(k++);
                var key = realm.Vm.Call(cb, JsValue.Undefined, args);
                if (!map.Table.TryGet(key, out var existing))
                {
                    existing = JsValue.FromObject(realm.NewArray());
                    map.Table.Set(realm, key, existing);
                }
                ((JsArray)existing.Ref!).Push(v);
            }
            return JsValue.FromObject(map);
        });
        r.Method(proto, "get", 1, static (in NativeArgs a) => ThisMap(a, false, "get").Table.TryGet(a[0], out var v) ? v : JsValue.Undefined);
        r.MapSetFunction = r.MethodEager(proto, "set", 2, static (in NativeArgs a) => { ThisMap(a, false, "set").Table.Set(a.Realm, a[0], a[1]); return a.This; });
        r.Method(proto, "has", 1, static (in NativeArgs a) => JsValue.FromBool(ThisMap(a, false, "has").Table.Has(a[0])));
        r.Method(proto, "delete", 1, static (in NativeArgs a) => JsValue.FromBool(ThisMap(a, false, "delete").Table.Delete(a[0])));
        r.Method(proto, "clear", 0, static (in NativeArgs a) => { ThisMap(a, false, "clear").Table.Clear(); return JsValue.Undefined; });
        r.Getter(proto, "size", "size", static (in NativeArgs a) => new JsValue(ThisMap(a, false, "size").Table.Size));
        r.Method(proto, "forEach", 1, static (in NativeArgs a) =>
        {
            var m = ThisMap(a, false, "forEach");
            if (!a[0].IsFunction) throw JsErrors.TypeError(a.Realm, $"{a[0]} is not a function");
            var cursor = m.Table.CreateCursor();
            var args = new JsValue[3];
            args[2] = a.This;
            while (m.Table.Next(cursor, out var k, out var v))
            {
                args[0] = v; args[1] = k;
                a.Realm.Vm.Call(a[0], a[1], args);
                a.Realm.Vm.Tick();
            }
            return JsValue.Undefined;
        });
        var iterProto = new JsObject(r, r.IteratorPrototype);
        r.MapIteratorPrototype = iterProto;
        r.MapIteratorNext = r.MethodEager(iterProto, "next", 0, static (in NativeArgs a) => IterNext(a));
        iterProto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("Map Iterator"), PropFlags.Configurable);
        r.Method(proto, "keys", 0, static (in NativeArgs a) => JsValue.FromObject(new JsMapIterator(a.Realm, a.Realm.MapIteratorPrototype, ThisMap(a, false, "keys").Table, 0)));
        r.Method(proto, "values", 0, static (in NativeArgs a) => JsValue.FromObject(new JsMapIterator(a.Realm, a.Realm.MapIteratorPrototype, ThisMap(a, false, "values").Table, 1)));
        var entries = r.MethodEager(proto, "entries", 0, static (in NativeArgs a) => JsValue.FromObject(new JsMapIterator(a.Realm, a.Realm.MapIteratorPrototype, ThisMap(a, false, "entries").Table, 2)));
        r.MapProtoEntries = entries;
        proto.AddOwnProperty(JsSymbol.Iterator, JsValue.FromObject(entries), PropFlags.Hidden);
        proto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("Map"), PropFlags.Configurable);
    }

    private static JsValue IterNext(in NativeArgs a)
    {
        var realm = a.Realm;
        if (a.This.Ref is not JsMapIterator it) throw JsErrors.TypeError(realm, "next method called on incompatible receiver");
        if (it.Table == null || !it.Table.Next(it.Cursor, out var k, out var v))
        {
            it.Table = null;
            return Coroutines.CreateIterResult(realm, JsValue.Undefined, true);
        }
        switch (it.Kind)
        {
            case 0: return Coroutines.CreateIterResult(realm, k, false);
            case 1: return Coroutines.CreateIterResult(realm, v, false);
            default:
                var pair = realm.NewArray(2);
                pair.Push(k);
                pair.Push(v);
                return Coroutines.CreateIterResult(realm, JsValue.FromObject(pair), false);
        }
    }

    private static void SetupSet(Realm r)
    {
        var proto = new JsObject(r, r.ObjectPrototype);
        r.SetPrototype = proto;
        var ctor = r.Constructor("Set", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (!a.IsConstruct) throw JsErrors.TypeError(realm, "Constructor Set requires 'new'");
            var p = Operations.GetPrototypeFromConstructor(realm, a.NewTarget.AsObjectUnchecked, realm.SetPrototype);
            var set = new JsMap(realm, p, true);
            if (!a[0].IsNullOrUndefined)
            {
                var adder = set.Get("add");
                if (!adder.IsFunction) throw JsErrors.TypeError(realm, "'add' is not a function");
                bool fast = ReferenceEquals(adder.Ref, realm.SetAddFunction);
                var it = IteratorRecord.Get(realm, a[0]);
                while (it.Step(out var v))
                {
                    if (fast) set.Table.Set(realm, v, v);
                    else
                    {
                        try { realm.Vm.Call(adder, JsValue.FromObject(set), new ReadOnlySpan<JsValue>(in v)); }
                        catch (JsThrowException) { it.CloseQuiet(); throw; }
                    }
                    realm.Vm.Tick();
                }
            }
            return JsValue.FromObject(set);
        }, proto);
        r.DefineGlobal("Set", JsValue.FromObject(ctor));
        r.Getter(ctor, JsSymbol.Species, "[Symbol.species]", static (in NativeArgs a) => a.This);
        r.SetAddFunction = r.MethodEager(proto, "add", 1, static (in NativeArgs a) => { ThisMap(a, true, "add").Table.Set(a.Realm, a[0], a[0]); return a.This; });
        r.Method(proto, "has", 1, static (in NativeArgs a) => JsValue.FromBool(ThisMap(a, true, "has").Table.Has(a[0])));
        r.Method(proto, "delete", 1, static (in NativeArgs a) => JsValue.FromBool(ThisMap(a, true, "delete").Table.Delete(a[0])));
        r.Method(proto, "clear", 0, static (in NativeArgs a) => { ThisMap(a, true, "clear").Table.Clear(); return JsValue.Undefined; });
        r.Getter(proto, "size", "size", static (in NativeArgs a) => new JsValue(ThisMap(a, true, "size").Table.Size));
        r.Method(proto, "forEach", 1, static (in NativeArgs a) =>
        {
            var m = ThisMap(a, true, "forEach");
            if (!a[0].IsFunction) throw JsErrors.TypeError(a.Realm, $"{a[0]} is not a function");
            var cursor = m.Table.CreateCursor();
            var args = new JsValue[3];
            args[2] = a.This;
            while (m.Table.Next(cursor, out var k, out _))
            {
                args[0] = k; args[1] = k;
                a.Realm.Vm.Call(a[0], a[1], args);
                a.Realm.Vm.Tick();
            }
            return JsValue.Undefined;
        });
        var iterProto = new JsObject(r, r.IteratorPrototype);
        r.SetIteratorPrototype = iterProto;
        r.SetIteratorNext = r.MethodEager(iterProto, "next", 0, static (in NativeArgs a) => IterNext(a));
        iterProto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("Set Iterator"), PropFlags.Configurable);
        var values = r.MethodEager(proto, "values", 0, static (in NativeArgs a) => JsValue.FromObject(new JsMapIterator(a.Realm, a.Realm.SetIteratorPrototype, ThisMap(a, true, "values").Table, 1)));
        r.SetProtoValues = values;
        proto.DefineHidden("keys", JsValue.FromObject(values));
        r.Method(proto, "entries", 0, static (in NativeArgs a) => JsValue.FromObject(new JsMapIterator(a.Realm, a.Realm.SetIteratorPrototype, ThisMap(a, true, "entries").Table, 2)));
        proto.AddOwnProperty(JsSymbol.Iterator, JsValue.FromObject(values), PropFlags.Hidden);
        proto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("Set"), PropFlags.Configurable);
        // set algebra (ES2025)
        r.Method(proto, "union", 1, static (in NativeArgs a) => SetOp(a, 0));
        r.Method(proto, "intersection", 1, static (in NativeArgs a) => SetOp(a, 1));
        r.Method(proto, "difference", 1, static (in NativeArgs a) => SetOp(a, 2));
        r.Method(proto, "symmetricDifference", 1, static (in NativeArgs a) => SetOp(a, 3));
        r.Method(proto, "isSubsetOf", 1, static (in NativeArgs a) => SetOp(a, 4));
        r.Method(proto, "isSupersetOf", 1, static (in NativeArgs a) => SetOp(a, 5));
        r.Method(proto, "isDisjointFrom", 1, static (in NativeArgs a) => SetOp(a, 6));
    }

    private readonly struct SetRecord
    {
        public readonly JsObject Set;
        public readonly double Size;
        public readonly JsValue Has;
        public readonly JsValue Keys;
        public SetRecord(JsObject set, double size, JsValue has, JsValue keys) { Set = set; Size = size; Has = has; Keys = keys; }
    }

    /// <summary>GetSetRecord (24.2.1.2).</summary>
    private static SetRecord GetSetRecord(Realm realm, in JsValue obj)
    {
        if (obj.IsNotObject) throw JsErrors.TypeError(realm, "Set operation argument must be an object");
        var o = obj.AsObjectUnchecked;
        var rawSize = o.Get("size", obj);
        double numSize = Operations.ToNumber(realm, rawSize);
        if (double.IsNaN(numSize)) throw JsErrors.TypeError(realm, "The 'size' property must be a number");
        double intSize = Operations.ToIntegerOrInfinity(realm, new JsValue(numSize));
        if (intSize < 0) throw JsErrors.RangeError(realm, "The 'size' property must not be negative");
        var has = o.Get("has", obj);
        if (!has.IsFunction) throw JsErrors.TypeError(realm, "The 'has' property must be a function");
        var keys = o.Get("keys", obj);
        if (!keys.IsFunction) throw JsErrors.TypeError(realm, "The 'keys' property must be a function");
        return new SetRecord(o, intSize, has, keys);
    }

    private sealed class KeysIterator
    {
        private readonly Realm _realm;
        private readonly JsValue _iter;
        private readonly JsValue _next;
        public KeysIterator(Realm realm, in SetRecord rec)
        {
            _realm = realm;
            _iter = realm.Vm.Call(rec.Keys, JsValue.FromObject(rec.Set), ReadOnlySpan<JsValue>.Empty);
            if (_iter.IsNotObject) throw JsErrors.TypeError(realm, "keys() result is not an object");
            _next = _iter.AsObjectUnchecked.Get("next", _iter);
        }
        public bool Step(out JsValue value)
        {
            var r = _realm.Vm.Call(_next, _iter, ReadOnlySpan<JsValue>.Empty);
            if (r.IsNotObject) throw JsErrors.TypeError(_realm, $"Iterator result {r} is not an object");
            var ro = r.AsObjectUnchecked;
            if (Operations.ToBoolean(ro.Get("done", r))) { value = default; return false; }
            value = ro.Get("value", r);
            if (value.IsNumber && value.Num == 0) value = JsValue.Zero; // canonicalize -0
            _realm.Vm.Tick();
            return true;
        }
        public void Close()
        {
            var ret = _iter.AsObjectUnchecked.Get("return", _iter);
            if (ret.IsNullOrUndefined) return;
            var r = _realm.Vm.Call(ret, _iter, ReadOnlySpan<JsValue>.Empty);
            if (r.IsNotObject) throw JsErrors.TypeError(_realm, "Iterator result is not an object");
        }
    }

    private static bool OtherHas(Realm realm, in SetRecord rec, in JsValue e)
        => Operations.ToBoolean(realm.Vm.Call(rec.Has, JsValue.FromObject(rec.Set), new ReadOnlySpan<JsValue>(in e)));

    private static List<JsValue> Elements(JsMap set)
    {
        var list = new List<JsValue>();
        var c = set.Table.CreateCursor();
        while (set.Table.Next(c, out var k, out _)) list.Add(k);
        return list;
    }

    private static JsMap CopySet(Realm realm, JsMap set)
    {
        var res = new JsMap(realm, realm.SetPrototype, true);
        var c = set.Table.CreateCursor();
        while (set.Table.Next(c, out var k, out _)) res.Table.Set(realm, k, k);
        return res;
    }

    private static JsValue SetOp(in NativeArgs a, int op)
    {
        var realm = a.Realm;
        var self = ThisMap(a, true, "setOperation");
        var other = GetSetRecord(realm, a[0]);
        int thisSize = self.Table.Size;
        switch (op)
        {
            case 0: // union
            {
                var it = new KeysIterator(realm, other);
                var res = CopySet(realm, self);
                while (it.Step(out var v)) res.Table.Set(realm, v, v);
                return JsValue.FromObject(res);
            }
            case 1: // intersection
            {
                var res = new JsMap(realm, realm.SetPrototype, true);
                if (thisSize <= other.Size)
                {
                    var c = self.Table.CreateCursor();
                    while (self.Table.Next(c, out var e, out _))
                        if (OtherHas(realm, other, e)) res.Table.Set(realm, e, e);
                }
                else
                {
                    var it = new KeysIterator(realm, other);
                    while (it.Step(out var v))
                        if (self.Table.Has(v)) res.Table.Set(realm, v, v);
                }
                return JsValue.FromObject(res);
            }
            case 2: // difference
            {
                var res = CopySet(realm, self);
                if (thisSize <= other.Size)
                {
                    foreach (var e in Elements(self))
                        if (res.Table.Has(e) && OtherHas(realm, other, e)) res.Table.Delete(e);
                }
                else
                {
                    var it = new KeysIterator(realm, other);
                    while (it.Step(out var v)) res.Table.Delete(v);
                }
                return JsValue.FromObject(res);
            }
            case 3: // symmetricDifference
            {
                var it = new KeysIterator(realm, other);
                var res = CopySet(realm, self);
                while (it.Step(out var v))
                {
                    if (self.Table.Has(v)) res.Table.Delete(v);
                    else res.Table.Set(realm, v, v);
                }
                return JsValue.FromObject(res);
            }
            case 4: // isSubsetOf
            {
                if (thisSize > other.Size) return JsValue.False;
                var c = self.Table.CreateCursor();
                while (self.Table.Next(c, out var e, out _))
                    if (!OtherHas(realm, other, e)) return JsValue.False;
                return JsValue.True;
            }
            case 5: // isSupersetOf
            {
                if (thisSize < other.Size) return JsValue.False;
                var it = new KeysIterator(realm, other);
                while (it.Step(out var v))
                    if (!self.Table.Has(v)) { it.Close(); return JsValue.False; }
                return JsValue.True;
            }
            default: // isDisjointFrom
            {
                if (thisSize <= other.Size)
                {
                    var c = self.Table.CreateCursor();
                    while (self.Table.Next(c, out var e, out _))
                        if (OtherHas(realm, other, e)) return JsValue.False;
                }
                else
                {
                    var it = new KeysIterator(realm, other);
                    while (it.Step(out var v))
                        if (self.Table.Has(v)) { it.Close(); return JsValue.False; }
                }
                return JsValue.True;
            }
        }
    }

    internal static object WeakKey(in NativeArgs a, JsValue v)
    {
        if (v.IsObject) return v.Ref!;
        if (v.IsSymbol && ((JsSymbol)v.Ref!).RegistryKey == null) return v.Ref!;
        throw JsErrors.TypeError(a.Realm, $"Invalid value used as weak collection key");
    }

    internal static JsWeakCollection ThisWeak(in NativeArgs a, bool set)
    {
        if (a.This.Ref is JsWeakCollection w && w.IsSet == set) return w;
        throw JsErrors.TypeError(a.Realm, $"Method called on incompatible receiver {a.This}");
    }

    private static void SetupWeak(Realm r)
    {
        var wmProto = new JsObject(r, r.ObjectPrototype);
        r.WeakMapPrototype = wmProto;
        var wm = r.Constructor("WeakMap", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (!a.IsConstruct) throw JsErrors.TypeError(realm, "Constructor WeakMap requires 'new'");
            var p = Operations.GetPrototypeFromConstructor(realm, a.NewTarget.AsObjectUnchecked, realm.WeakMapPrototype);
            var m = new JsWeakCollection(realm, p, false);
            if (!a[0].IsNullOrUndefined)
            {
                var adder = m.Get("set");
                if (!adder.IsFunction) throw JsErrors.TypeError(realm, "'set' is not a function");
                var it = IteratorRecord.Get(realm, a[0]);
                while (it.Step(out var entry))
                {
                    if (entry.IsNotObject) { it.CloseQuiet(); throw JsErrors.TypeError(realm, "Iterator value is not an entry object"); }
                    try
                    {
                        var eo = entry.AsObjectUnchecked;
                        var k = eo.GetIndex(0, entry);
                        var v = eo.GetIndex(1, entry);
                        realm.Vm.Call(adder, JsValue.FromObject(m), new[] { k, v });
                    }
                    catch (JsThrowException) { it.CloseQuiet(); throw; }
                }
            }
            return JsValue.FromObject(m);
        }, wmProto);
        r.DefineGlobal("WeakMap", JsValue.FromObject(wm));
        r.Method(wmProto, "get", 1, static (in NativeArgs a) =>
        {
            var w = ThisWeak(a, false);
            if (a[0].IsNotObject && a[0].Tag != JsTag.Symbol) return JsValue.Undefined;
            return w.Table.TryGetValue(a[0].Ref!, out var box) ? box.Value : JsValue.Undefined;
        });
        r.Method(wmProto, "set", 2, static (in NativeArgs a) =>
        {
            var w = ThisWeak(a, false);
            var key = WeakKey(a, a[0]);
            w.Table.AddOrUpdate(key, new StrongBox<JsValue>(a[1]));
            a.Realm.ChargeBytes(64);
            return a.This;
        });
        r.Method(wmProto, "has", 1, static (in NativeArgs a) =>
        {
            var w = ThisWeak(a, false);
            return JsValue.FromBool((a[0].IsObject || a[0].IsSymbol) && w.Table.TryGetValue(a[0].Ref!, out _));
        });
        r.Method(wmProto, "delete", 1, static (in NativeArgs a) =>
        {
            var w = ThisWeak(a, false);
            return JsValue.FromBool((a[0].IsObject || a[0].IsSymbol) && w.Table.Remove(a[0].Ref!));
        });
        wmProto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("WeakMap"), PropFlags.Configurable);
        Es2026Builtins.InstallWeakMap(r, wmProto);

        var wsProto = new JsObject(r, r.ObjectPrototype);
        r.WeakSetPrototype = wsProto;
        var ws = r.Constructor("WeakSet", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (!a.IsConstruct) throw JsErrors.TypeError(realm, "Constructor WeakSet requires 'new'");
            var p = Operations.GetPrototypeFromConstructor(realm, a.NewTarget.AsObjectUnchecked, realm.WeakSetPrototype);
            var s = new JsWeakCollection(realm, p, true);
            if (!a[0].IsNullOrUndefined)
            {
                var adder = s.Get("add");
                if (!adder.IsFunction) throw JsErrors.TypeError(realm, "'add' is not a function");
                var it = IteratorRecord.Get(realm, a[0]);
                while (it.Step(out var v))
                {
                    try { realm.Vm.Call(adder, JsValue.FromObject(s), new ReadOnlySpan<JsValue>(in v)); }
                    catch (JsThrowException) { it.CloseQuiet(); throw; }
                }
            }
            return JsValue.FromObject(s);
        }, wsProto);
        r.DefineGlobal("WeakSet", JsValue.FromObject(ws));
        r.Method(wsProto, "add", 1, static (in NativeArgs a) =>
        {
            var w = ThisWeak(a, true);
            w.Table.AddOrUpdate(WeakKey(a, a[0]), new StrongBox<JsValue>(JsValue.True));
            return a.This;
        });
        r.Method(wsProto, "has", 1, static (in NativeArgs a) =>
        {
            var w = ThisWeak(a, true);
            return JsValue.FromBool((a[0].IsObject || a[0].IsSymbol) && w.Table.TryGetValue(a[0].Ref!, out _));
        });
        r.Method(wsProto, "delete", 1, static (in NativeArgs a) =>
        {
            var w = ThisWeak(a, true);
            return JsValue.FromBool((a[0].IsObject || a[0].IsSymbol) && w.Table.Remove(a[0].Ref!));
        });
        wsProto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("WeakSet"), PropFlags.Configurable);
    }
}
