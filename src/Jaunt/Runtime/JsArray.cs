using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Jaunt.Runtime;

/// <summary>
/// Array exotic object. Elements live in a dense <see cref="Items"/> buffer (holes are the internal Empty
/// value) until a sparse write, a non-default attribute or a very large index forces dictionary mode, where
/// elements are stored as ordinary properties.
/// </summary>
internal sealed class JsArray : JsObject
{
    internal JsValue[] Items;
    internal uint Length;
    /// <summary>False once elements moved to ordinary (sparse) storage.</summary>
    internal bool Dense = true;
    internal bool LengthWritable = true;

    private const uint MaxDenseGap = 1024;
    private const uint MaxPresizeOnWrite = 1 << 20;

    /// <summary>
    /// Queue mode. A long array used from the front (shift, unshift, splice near the start) keeps its elements in
    /// <see cref="Store"/> from <see cref="Head"/> on, so those operations move a few elements or none instead of the whole
    /// array. <see cref="Items"/> stays the same field the interpreter reads without an offset: it is empty meanwhile, every
    /// fast path misses, and the slow paths of this class leave queue mode (one move, what the shift would have cost anyway)
    /// or serve the access from here. Code outside this class that takes an index beyond <see cref="Items"/> for a hole
    /// calls <see cref="LeaveQueueMode"/> first. Invariants: Head + Length &lt;= Store.Length, every other slot is a hole,
    /// and Length &gt;= MinQueueLength / 2.
    /// </summary>
    private sealed class QueueState(JsValue[] store)
    {
        public JsValue[] Store = store;
        public int Head;
        /// <summary>Reads and writes served from the queue since the last front operation; many of them means it is not used as a queue.</summary>
        public int Accesses;
    }

    private QueueState? _queue;
    internal const int MinQueueLength = 64;

    internal bool IsQueue => _queue != null;

    internal void LeaveQueueMode()
    {
        var q = _queue;
        if (q == null) return;
        _queue = null;
        var s = q.Store;
        int h = q.Head, n = (int)Length;
        if (h > 0)
        {
            Array.Copy(s, h, s, 0, n);
            s.AsSpan(Math.Max(n, h), Math.Min(n, h)).Fill(JsValue.Empty);
        }
        Items = s;
    }

    private bool TryEnterQueueMode()
    {
        if (Length < MinQueueLength || Length > (uint)Items.Length) return false;
        _queue = new QueueState(Items);
        Items = Array.Empty<JsValue>();
        return true;
    }

    /// <summary>Removes the first element, which may be a hole (Empty). False when the array is too short for queue mode: the caller moves the elements. The caller has checked Dense, the prototypes, and that Length &gt; 0.</summary>
    internal bool TryQueueShift(out JsValue first)
    {
        if (_queue == null && !TryEnterQueueMode()) { first = default; return false; }
        var q = _queue!;
        ref var slot = ref q.Store[q.Head];
        first = slot;
        slot = JsValue.Empty;
        q.Head++;
        q.Accesses = 0;
        Length--;
        if (Length < MinQueueLength / 2) LeaveQueueMode();
        return true;
    }

    /// <summary>Prepends into the free slots before the head, when there are enough.</summary>
    internal bool TryQueueUnshift(ReadOnlySpan<JsValue> values)
    {
        var q = _queue;
        if (q == null || q.Head < values.Length) return false;
        q.Head -= values.Length;
        q.Accesses = 0;
        values.CopyTo(q.Store.AsSpan(q.Head));
        Length += (uint)values.Length;
        return true;
    }

    /// <summary>Removes the last element in queue mode unless it is a hole (which reads through the prototype chain).</summary>
    internal bool TryQueuePop(out JsValue last)
    {
        var q = _queue;
        if (q == null) { last = default; return false; }
        ref var slot = ref q.Store[q.Head + (int)Length - 1];
        last = slot;
        if (last.IsEmpty) return false;
        slot = JsValue.Empty;
        Length--;
        if (Length < MinQueueLength / 2) LeaveQueueMode();
        return true;
    }

    private void QueuePush(JsValue v)
    {
        var q = _queue!;
        var s = q.Store;
        int n = (int)Length, end = q.Head + n;
        if (end == s.Length)
        {
            if (q.Head >= s.Length / 2)
            {
                // the free half at the front pays for this move
                Array.Copy(s, q.Head, s, 0, n);
                s.AsSpan(Math.Max(n, q.Head), Math.Min(n, q.Head)).Fill(JsValue.Empty);
            }
            else
            {
                long cap = Math.Max((long)n * 2, 8);
                if (cap > Realm.MaxArrayLength) cap = Math.Max(n + 1L, Realm.MaxArrayLength);
                Realm.ChargeBytes((cap - s.Length) * 24L);
                var grown = new JsValue[cap];
                Array.Copy(s, q.Head, grown, 0, n);
                MarkHoles(grown, n);
                q.Store = s = grown;
            }
            q.Head = 0;
            end = n;
        }
        s[end] = v;
        Length++;
    }

    private bool TryQueueGet(uint index, out JsValue value)
    {
        var q = _queue!;
        value = default;
        if (index >= Length || ++q.Accesses > Length / 8 + 8) return false;
        value = q.Store[q.Head + (int)index];
        return value.IsNotEmpty;
    }

    private bool TryQueueSet(uint index, in JsValue value)
    {
        var q = _queue!;
        if (index >= Length || ++q.Accesses > Length / 8 + 8) return false;
        ref var slot = ref q.Store[q.Head + (int)index];
        if (slot.IsEmpty) return false;
        slot = value;
        return true;
    }

    /// <summary>
    /// The element moves of a splice, for an array the caller has checked (Dense, extensible, writable length, no prototype
    /// elements). <paramref name="removed"/> is a fresh array of length <paramref name="deleteCount"/>. Moves whichever side of the splice is shorter when that can be done in place.
    /// </summary>
    internal void SpliceDense(int start, int deleteCount, ReadOnlySpan<JsValue> insert, JsArray removed)
    {
        var q = _queue;
        var s = q != null ? q.Store : Items;
        int h = q != null ? q.Head : 0, n = (int)Length, delta = insert.Length - deleteCount, tail = n - start - deleteCount;
        if (deleteCount > 0)
        {
            removed.EnsureCapacity((uint)deleteCount);
            Array.Copy(s, h + start, removed.Items, 0, deleteCount);
        }
        if (delta != 0)
        {
            if (n >= MinQueueLength && start < tail && (delta < 0 ? (q != null || TryEnterQueueMode()) : q != null && h >= delta))
            {
                q = _queue!;
                Array.Copy(s, h, s, h - delta, start);
                if (delta < 0) s.AsSpan(h, -delta).Fill(JsValue.Empty);
                q.Head = h -= delta;
                q.Accesses = 0;
            }
            else
            {
                if (h + n + delta > s.Length)
                {
                    LeaveQueueMode();
                    q = null;
                    h = 0;
                    EnsureCapacity((uint)(n + delta));
                    s = Items;
                }
                Array.Copy(s, h + start + deleteCount, s, h + start + insert.Length, tail);
                if (delta < 0) s.AsSpan(h + n + delta, -delta).Fill(JsValue.Empty);
            }
        }
        insert.CopyTo(s.AsSpan(h + start));
        Length = (uint)(n + delta);
        if (q != null && Length < MinQueueLength / 2) LeaveQueueMode();
    }

    public JsArray(Realm realm, int capacity = 0, JsObject? proto = null)
        : base(realm, proto ?? realm.ArrayPrototype, exotic: true, uniqueShape: false)
    {
        if (capacity == 0) Items = Array.Empty<JsValue>();
        else
        {
            Items = new JsValue[capacity];
            MarkHoles(Items, 0); // slots beyond Length are holes
            realm.ChargeBytes(capacity * 24L);
        }
    }

    public override string ClassName => "Array";

    internal override bool CanCacheNamed(in PropertyKey key) => !(key.Value is string s && (s == "length" || PropertyKey.TryParseArrayIndex(s, out _)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetDense(uint index, out JsValue value)
    {
        if (Dense && index < Length && index < (uint)Items.Length)
        {
            value = Items[index];
            return value.IsNotEmpty;
        }
        value = default;
        return false;
    }

    internal void EnsureCapacity(uint needed)
    {
        if (needed <= (uint)Items.Length) return;
        LeaveQueueMode();
        if (needed <= (uint)Items.Length) return;
        long newCap = Math.Max(Math.Max(8, (long)Items.Length * 2), needed);
        if (newCap > Realm.MaxArrayLength) newCap = Math.Max(needed, Realm.MaxArrayLength);
        Realm.ChargeBytes((newCap - Items.Length) * 24L);
        var n = new JsValue[newCap];
        Items.AsSpan().CopyTo(n);
        MarkHoles(n, Items.Length);
        Items = n;
    }

    /// <summary>
    /// Turns the freshly allocated (all zero) slots from <paramref name="start"/> on into holes. A hole differs from a
    /// zeroed slot only in its non-reference half, so this writes that half alone: a per-element <c>Fill</c> runs a GC
    /// write barrier for every null reference.
    /// </summary>
    private static void MarkHoles(JsValue[] items, int start)
    {
        for (int i = start; i < items.Length; i++) Unsafe.AsRef(in items[i].Bits) = JsValue.EmptyBits;
    }

    /// <summary>Fast append used by array literals, push and builtins.</summary>
    internal void Push(JsValue v)
    {
        if (!Dense || !LengthWritable || !Extensible || Length >= uint.MaxValue - 1)
        {
            SetIndex(Length, v, JsValue.FromObject(this));
            return;
        }
        if (Length >= (uint)Items.Length)
        {
            if (_queue != null) { QueuePush(v); return; }
            EnsureCapacity(Length + 1);
        }
        Items[Length++] = v;
    }

    /// <summary>
    /// Appends every element of <paramref name="src"/> in one copy (array spread of a pristine array). False when the
    /// element-by-element path is needed: either array is not dense, or <paramref name="src"/> has holes, which read
    /// through the prototype chain.
    /// </summary>
    internal bool TryAppendDense(JsArray src, uint reserve = 0)
    {
        if (!Dense || !LengthWritable || !Extensible || !src.Dense) return false;
        LeaveQueueMode();
        src.LeaveQueueMode();
        uint n = src.Length;
        var items = src.Items;
        if (n > (uint)items.Length || (ulong)Length + n >= uint.MaxValue - 1) return false;
        for (uint i = 0; i < n; i++) if (items[i].IsEmpty) return false;
        if (n == 0) return true;
        uint start = Length;
        // reserve: elements the caller will append right after (an array literal's trailing elements), so the
        // append does not grow an exactly sized backing store a second time
        ulong want = (ulong)start + n + reserve;
        if (want > (uint)Items.Length) EnsureCapacity((uint)Math.Min(want, uint.MaxValue - 1));
        Array.Copy(items, 0, Items, start, n);
        Length = start + n;
        return true;
    }

    internal void PushHole()
    {
        if (!Dense) { SetLength(Length + 1); return; }
        LeaveQueueMode();
        if (Length >= (uint)Items.Length) EnsureCapacity(Length + 1);
        Items[Length++] = JsValue.Empty;
    }

    internal void ConvertToSparse()
    {
        if (!Dense) return;
        LeaveQueueMode();
        Dense = false;
        ConvertToDictionary();
        for (uint i = 0; i < Length && i < (uint)Items.Length; i++)
        {
            var v = Items[i];
            if (v.IsNotEmpty) Dict!.Add(PropertyKey.FromIndex(i), v, PropFlags.Default);
        }
        Items = Array.Empty<JsValue>();
    }

    public override bool GetOwnProperty(in PropertyKey key, out PropertyDescriptor desc)
    {
        if (key.Value is string s)
        {
            if (s == "length")
            {
                desc = PropertyDescriptor.Data(new JsValue(Length), LengthWritable ? PropFlags.Writable : PropFlags.None);
                return true;
            }
            if (Dense && PropertyKey.TryParseArrayIndex(s, out uint idx))
            {
                LeaveQueueMode();
                if (TryGetDense(idx, out var v)) { desc = PropertyDescriptor.Data(v, PropFlags.Default); return true; }
                desc = default;
                return false;
            }
        }
        return base.GetOwnProperty(key, out desc);
    }

    public override bool DefineOwnProperty(in PropertyKey key, in PropertyDescriptor desc)
    {
        if (key.Value is string s)
        {
            if (s == "length") return DefineLength(desc);
            if (PropertyKey.TryParseArrayIndex(s, out uint idx))
            {
                if (idx >= Length && !LengthWritable) return false;
                if (Dense)
                {
                    LeaveQueueMode();
                    bool exists = TryGetDense(idx, out _);
                    if (exists)
                    {
                        bool plain = !desc.IsAccessor && (!desc.HasWritable || desc.Writable)
                            && (!desc.HasEnumerable || desc.Enumerable) && (!desc.HasConfigurable || desc.Configurable);
                        if (plain)
                        {
                            if (desc.HasValue) Items[idx] = desc.Value;
                            return true;
                        }
                    }
                    else
                    {
                        if (!Extensible) return false;
                        bool defaults = !desc.IsAccessor && desc.Writable && desc.Enumerable && desc.Configurable;
                        if (defaults && !(idx >= Length && idx - Length > MaxDenseGap && idx >= (uint)Items.Length))
                        {
                            EnsureCapacity(idx + 1);
                            Items[idx] = desc.HasValue ? desc.Value : JsValue.Undefined;
                            if (idx >= Length) Length = idx + 1;
                            return true;
                        }
                    }
                }
                ConvertToSparse();
                if (!OrdinaryDefineOwnProperty(key, desc)) return false;
                if (idx >= Length) Length = idx + 1;
                return true;
            }
        }
        return OrdinaryDefineOwnProperty(key, desc);
    }

    private bool DefineLength(in PropertyDescriptor desc)
    {
        uint newLen = 0;
        if (desc.HasValue)
        {
            // ArraySetLength: ToUint32 then ToNumber, before any other validation
            newLen = Operations.ToUint32(Realm.Vm.Realm, desc.Value);
            double nd = Operations.ToNumber(Realm.Vm.Realm, desc.Value);
            if (newLen != nd) throw JsErrors.RangeError(Realm.Vm.Realm, "Invalid array length");
        }
        if (desc.IsAccessor) return false;
        if (desc.HasConfigurable && desc.Configurable) return false;
        if (desc.HasEnumerable && desc.Enumerable) return false;
        if (!desc.HasValue)
        {
            if (desc.HasWritable && desc.Writable && !LengthWritable) return false;
            if (desc.HasWritable && !desc.Writable) LengthWritable = false;
            return true;
        }
        if (newLen == Length)
        {
            if (desc.HasWritable && desc.Writable && !LengthWritable) return false;
            if (desc.HasWritable && !desc.Writable) LengthWritable = false;
            return true;
        }
        if (!LengthWritable) return false;
        bool ok = SetLength(newLen);
        if (desc.HasWritable && !desc.Writable) LengthWritable = false;
        return ok;
    }

    internal bool SetLength(uint newLen)
    {
        if (Dense)
        {
            LeaveQueueMode();
            if (newLen < Length)
            {
                int end = (int)Math.Min(Length, (uint)Items.Length);
                for (int i = (int)newLen; i < end; i++) Items[i] = JsValue.Empty;
                Length = newLen;
                if (Items.Length > 64 && newLen < Items.Length / 4) Array.Resize(ref Items, Math.Max((int)newLen, 8));
                return true;
            }
            // indices in [Items.Length, newLen) are implicit holes
            Length = newLen;
            return true;
        }
        if (newLen < Length)
        {
            var keys = new List<(uint, PropertyKey)>();
            for (int i = 0; i < Dict!.Count; i++)
            {
                if (!Dict.Live[i]) continue;
                if (Dict.Keys[i].TryGetArrayIndex(out uint k) && k >= newLen) keys.Add((k, Dict.Keys[i]));
            }
            keys.Sort((a, b) => b.Item1.CompareTo(a.Item1));
            foreach (var (k, pk) in keys)
            {
                if (!base.Delete(pk)) { Length = k + 1; return false; }
            }
        }
        Length = newLen;
        return true;
    }

    public override JsValue Get(in PropertyKey key, JsValue receiver)
    {
        if (key.Value is string s)
        {
            if (s == "length") return new JsValue(Length);
            if (Dense && PropertyKey.TryParseArrayIndex(s, out uint idx))
            {
                LeaveQueueMode();
                if (TryGetDense(idx, out var v)) return v;
                var p = Proto;
                return p == null ? JsValue.Undefined : p.Get(key, receiver);
            }
        }
        return OrdinaryGet(key, receiver);
    }

    public override JsValue GetIndex(uint index, JsValue receiver)
    {
        if (TryGetDense(index, out var v)) return v;
        if (_queue != null)
        {
            if (TryQueueGet(index, out v)) return v;
            LeaveQueueMode();
            if (TryGetDense(index, out v)) return v;
        }
        if (Dense)
        {
            var p = Proto;
            if (p == null || !ProtoChainMayHaveIndices(p)) return JsValue.Undefined;
        }
        return Get(PropertyKey.FromIndex(index), receiver);
    }

    internal static bool ProtoChainMayHaveIndices(JsObject? p)
    {
        while (p != null)
        {
            if (p.IsExotic && p is not JsArray) return true;
            if (p is JsArray pa && pa.Length > 0) return true;
            if (p.Dict != null ? p.Dict.HasIndexKeys : p.Shape.HasIndexKeys) return true;
            p = p.Proto;
        }
        return false;
    }

    public override bool Set(in PropertyKey key, JsValue value, JsValue receiver)
    {
        if (key.Value is string s)
        {
            if (s == "length" && receiver.IsObject && ReferenceEquals(receiver.Ref, this))
            {
                if (!LengthWritable) return false;
                return DefineLength(new PropertyDescriptor { Value = value, HasValue = true });
            }
            if (PropertyKey.TryParseArrayIndex(s, out uint idx)) return SetIndex(idx, value, receiver);
        }
        return OrdinarySet(key, value, receiver);
    }

    public override bool SetIndex(uint index, JsValue value, JsValue receiver)
    {
        if (Dense && receiver.IsObject && ReferenceEquals(receiver.Ref, this))
        {
            if (index < Length && index < (uint)Items.Length && Items[index].IsNotEmpty)
            {
                Items[index] = value;
                return true;
            }
            if (_queue != null)
            {
                if (TryQueueSet(index, value)) return true;
                LeaveQueueMode();
                if (index < Length && index < (uint)Items.Length && Items[index].IsNotEmpty)
                {
                    Items[index] = value;
                    return true;
                }
            }
            if (Extensible && (index < Length || LengthWritable) && !ProtoChainMayHaveIndices(Proto))
            {
                if (index >= Length && index - Length > MaxDenseGap && index >= (uint)Items.Length)
                {
                    ConvertToSparse();
                    return OrdinarySet(PropertyKey.FromIndex(index), value, receiver) && FixLength(index);
                }
                if (index == uint.MaxValue) return OrdinarySet(PropertyKey.FromIndex(index), value, receiver);
                // filling an array whose length was declared up front (new Array(n)): the length is the size it needs
                if (index >= (uint)Items.Length) EnsureCapacity(index < Length && Length <= MaxPresizeOnWrite ? Length : index + 1);
                Items[index] = value;
                if (index >= Length) Length = index + 1;
                return true;
            }
        }
        return OrdinarySet(PropertyKey.FromIndex(index), value, receiver);
    }

    private bool FixLength(uint idx)
    {
        if (idx >= Length) Length = idx + 1;
        return true;
    }

    public override bool HasProperty(in PropertyKey key)
    {
        if (key.Value is string s)
        {
            if (s == "length") return true;
            if (Dense && PropertyKey.TryParseArrayIndex(s, out uint idx))
            {
                LeaveQueueMode();
                if (TryGetDense(idx, out _)) return true;
                return Proto?.HasProperty(key) ?? false;
            }
        }
        return base.HasProperty(key);
    }

    public override bool Delete(in PropertyKey key)
    {
        if (key.Value is string s)
        {
            if (s == "length") return false;
            if (Dense && PropertyKey.TryParseArrayIndex(s, out uint idx))
            {
                LeaveQueueMode();
                if (idx < Length && idx < (uint)Items.Length) Items[idx] = JsValue.Empty;
                return true;
            }
        }
        return base.Delete(key);
    }

    public override void OwnPropertyKeys(List<PropertyKey> keys)
    {
        if (Dense)
        {
            LeaveQueueMode();
            uint n = Math.Min(Length, (uint)Items.Length);
            for (uint i = 0; i < n; i++) if (Items[i].IsNotEmpty) keys.Add(PropertyKey.FromIndex(i));
            keys.Add("length");
            OrdinaryOwnKeys(keys);
            return;
        }
        var rest = new List<PropertyKey>();
        OrdinaryOwnKeys(rest);
        foreach (var k in rest) if (k.TryGetArrayIndex(out _)) keys.Add(k);
        keys.Add("length");
        foreach (var k in rest) if (!k.TryGetArrayIndex(out _)) keys.Add(k);
    }

    public override bool PreventExtensions()
    {
        Extensible = false;
        return true;
    }
}
