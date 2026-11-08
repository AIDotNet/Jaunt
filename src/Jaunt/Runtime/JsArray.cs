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
    private bool _dense = true;
    /// <summary>
    /// Queue mode, entered by <c>shift</c> on a long array so that it need not move all elements: the elements start at
    /// <c>Items[_queueHead]</c> (and <see cref="_dense"/> is false). Only shift and push understand it; everything else
    /// reads <see cref="Dense"/>, which moves the elements back to the start of <see cref="Items"/>. That costs what the
    /// one shift would have cost, so alternating shift with other operations is no slower than without queue mode.
    /// </summary>
    private int _queueHead;
    private const uint MinQueueModeLength = 32;

    /// <summary>
    /// True when the elements live in <see cref="Items"/>; false once they moved to ordinary (sparse) storage.
    /// </summary>
    internal bool Dense
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _dense || LeaveQueueMode();
    }

    /// <summary>Whether <see cref="Push"/> appends directly: <see cref="Dense"/> without leaving queue mode.</summary>
    internal bool DenseOrQueue
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _dense || _queueHead != 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool LeaveQueueMode()
    {
        int h = _queueHead, n = (int)Length;
        if (h == 0) return false;
        Array.Copy(Items, h, Items, 0, n);
        Items.AsSpan(n, h).Fill(JsValue.Empty);
        _queueHead = 0;
        _dense = true;
        return true;
    }

    /// <summary><c>shift</c> without moving the remaining elements. False when the generic algorithm is needed, or a short array's plain element move is as good.</summary>
    internal bool TryShiftQueue(out JsValue first)
    {
        int h = _queueHead;
        if ((h != 0 || (_dense && Length >= MinQueueModeLength && Length <= (uint)Items.Length)) && LengthWritable && Extensible && !ProtoChainMayHaveIndices(Proto))
        {
            first = Items[h];
            Items[h] = default;
            if (first.IsEmpty) first = JsValue.Undefined;
            _queueHead = h + 1;
            _dense = false;
            if (--Length == 0) LeaveQueueMode();
            return true;
        }
        first = default;
        return false;
    }
    internal bool LengthWritable = true;

    private const uint MaxDenseGap = 1024;

    public JsArray(Realm realm, int capacity = 0, JsObject? proto = null)
        : base(realm, proto ?? realm.ArrayPrototype, exotic: true, uniqueShape: false)
    {
        if (capacity == 0) Items = Array.Empty<JsValue>();
        else
        {
            Items = new JsValue[capacity];
            Items.AsSpan().Fill(JsValue.Empty); // slots beyond Length are holes
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
        long newCap = Math.Max(Math.Max(8, (long)Items.Length * 2), needed);
        if (newCap > Realm.MaxArrayLength) newCap = Math.Max(needed, Realm.MaxArrayLength);
        Realm.ChargeBytes((newCap - Items.Length) * 24L);
        var n = new JsValue[newCap];
        Items.AsSpan().CopyTo(n);
        n.AsSpan(Items.Length).Fill(JsValue.Empty);
        Items = n;
    }

    /// <summary>Fast append used by array literals, push and builtins.</summary>
    internal void Push(JsValue v)
    {
        if (_queueHead != 0 && LengthWritable && Extensible && _queueHead + (long)Length < Items.Length)
        {
            Items[_queueHead + Length++] = v;
            return;
        }
        if (!Dense || !LengthWritable || !Extensible || Length >= uint.MaxValue - 1)
        {
            SetIndex(Length, v, JsValue.FromObject(this));
            return;
        }
        if (Length >= (uint)Items.Length) EnsureCapacity(Length + 1);
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
        if (Length >= (uint)Items.Length) EnsureCapacity(Length + 1);
        Items[Length++] = JsValue.Empty;
    }

    internal void ConvertToSparse()
    {
        if (!Dense) return;
        _dense = false;
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
            if (Extensible && (index < Length || LengthWritable) && !ProtoChainMayHaveIndices(Proto))
            {
                if (index >= Length && index - Length > MaxDenseGap && index >= (uint)Items.Length)
                {
                    ConvertToSparse();
                    return OrdinarySet(PropertyKey.FromIndex(index), value, receiver) && FixLength(index);
                }
                if (index == uint.MaxValue) return OrdinarySet(PropertyKey.FromIndex(index), value, receiver);
                EnsureCapacity(index + 1);
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
