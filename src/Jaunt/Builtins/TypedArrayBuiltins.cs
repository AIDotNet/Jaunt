using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Collections.Generic;

namespace Jaunt.Runtime;

internal sealed class JsArrayBuffer : JsObject
{
    /// <summary>Byte storage; its length is always the current byte length (resize reallocates, detach empties).</summary>
    public byte[] Data;
    /// <summary>Maximum byte length of a resizable buffer, or -1 for fixed-length buffers.</summary>
    public readonly int MaxByteLength;
    public bool Detached;
    /// <summary>SharedArrayBuffer.</summary>
    public readonly bool Shared;
    /// <summary>Shared Data Block of a SharedArrayBuffer (may be shared with other agents).</summary>
    public readonly SharedBlock? Block;
    /// <summary>[[ArrayBufferIsImmutable]] (proposal-immutable-arraybuffer): fixed-length, never detached or written.</summary>
    public readonly bool Immutable;

    /// <summary>AllocateImmutableArrayBuffer: an immutable buffer holding a copy of <paramref name="bytes"/>.</summary>
    public JsArrayBuffer(Realm realm, JsObject proto, ReadOnlySpan<byte> bytes, int byteLength) : this(realm, proto, byteLength)
    {
        bytes.CopyTo(Data);
        Immutable = true;
    }

    public JsArrayBuffer(Realm realm, JsObject proto, int length, int maxByteLength = -1, bool shared = false) : base(realm, proto)
    {
        realm.ChargeBytes(length);
        Data = new byte[length];
        MaxByteLength = maxByteLength;
        Shared = shared;
        if (shared)
        {
            Block = new SharedBlock(Data, maxByteLength);
            if (maxByteLength >= 0) Block.AddView(this);
        }
    }

    /// <summary>A SharedArrayBuffer of this agent over an existing Shared Data Block (e.g. received from another agent).</summary>
    public JsArrayBuffer(Realm realm, JsObject proto, SharedBlock block) : base(realm, proto)
    {
        Block = block;
        Shared = true;
        MaxByteLength = block.MaxByteLength;
        lock (block.Lock) Data = block.Data;
        if (block.MaxByteLength >= 0) block.AddView(this);
    }

    public bool Resizable => MaxByteLength >= 0;
    public int ByteLength => Data.Length;
    public override string ClassName => Shared ? "SharedArrayBuffer" : "ArrayBuffer";

    /// <summary>DetachArrayBuffer (25.1.3.5).</summary>
    public void Detach()
    {
        Data = Array.Empty<byte>();
        Detached = true;
    }

    public void Resize(int newLength)
    {
        if (Block != null)
        {
            if (newLength > Data.Length) Realm.ChargeBytes(newLength - Data.Length);
            Block.Grow(newLength);
            return;
        }
        if (newLength > Data.Length) Realm.ChargeBytes(newLength - Data.Length);
        var n = new byte[newLength];
        Array.Copy(Data, n, Math.Min(newLength, Data.Length));
        Data = n;
    }
}

internal sealed class JsDataView : JsObject
{
    public readonly JsArrayBuffer Buffer;
    public readonly int ByteOffset;
    private readonly int _byteLength; // -1: tracks the length of a resizable buffer
    public JsDataView(Realm realm, JsObject proto, JsArrayBuffer buffer, int offset, int length) : base(realm, proto)
    {
        Buffer = buffer;
        ByteOffset = offset;
        _byteLength = length;
    }

    /// <summary>IsViewOutOfBounds (25.3.1.3).</summary>
    public bool IsOutOfBounds
    {
        get
        {
            if (Buffer.Detached) return true;
            int bl = Buffer.Data.Length;
            return _byteLength < 0 ? ByteOffset > bl : (long)ByteOffset + _byteLength > bl;
        }
    }

    /// <summary>GetViewByteLength (assumes the view is in bounds).</summary>
    public int ByteLength => _byteLength < 0 ? Buffer.Data.Length - ByteOffset : _byteLength;
    public override string ClassName => "DataView";
}

internal enum TypedKind : byte { Int8, Uint8, Uint8Clamped, Int16, Uint16, Int32, Uint32, Float32, Float64, BigInt64, BigUint64, Float16 }

/// <summary>Integer-indexed exotic object over an ArrayBuffer.</summary>
internal sealed class JsTypedArray : JsObject
{
    public readonly JsArrayBuffer Buffer;
    public readonly int ByteOffset;
    private readonly int _length; // -1: length-tracking view over a resizable buffer
    public readonly TypedKind Kind;

    public JsTypedArray(Realm realm, JsObject proto, TypedKind kind, JsArrayBuffer buffer, int byteOffset, int length)
        : base(realm, proto, exotic: true, uniqueShape: false)
    {
        Kind = kind;
        Buffer = buffer;
        ByteOffset = byteOffset;
        _length = length;
    }

    /// <summary>TypedArrayLength; 0 when the view is out of bounds or its buffer is detached.</summary>
    public int Length
    {
        get
        {
            var data = Buffer.Data;
            if (_length >= 0)
                return (long)ByteOffset + (long)_length * ElementSize(Kind) <= data.Length && !Buffer.Detached ? _length : 0;
            if (Buffer.Detached || ByteOffset > data.Length) return 0;
            return (data.Length - ByteOffset) / ElementSize(Kind);
        }
    }

    public bool IsLengthTracking => _length < 0;

    /// <summary>IsTypedArrayOutOfBounds (10.4.5.12), including detached buffers.</summary>
    public bool IsOutOfBounds
    {
        get
        {
            if (Buffer.Detached) return true;
            int bl = Buffer.Data.Length;
            return _length < 0 ? ByteOffset > bl : (long)ByteOffset + (long)_length * ElementSize(Kind) > bl;
        }
    }

    public static int ElementSize(TypedKind k) => k switch
    {
        TypedKind.Int8 or TypedKind.Uint8 or TypedKind.Uint8Clamped => 1,
        TypedKind.Int16 or TypedKind.Uint16 or TypedKind.Float16 => 2,
        TypedKind.Int32 or TypedKind.Uint32 or TypedKind.Float32 => 4,
        _ => 8,
    };

    public override string ClassName => Kind + "Array";
    internal override bool CanCacheNamed(in PropertyKey key) => !(key.Value is string s && IsCanonicalNumeric(s));

    private static bool IsCanonicalNumeric(string s)
    {
        if (s == "-0") return true;
        if (s.Length == 0) return false;
        char c = s[0];
        if (!(char.IsAsciiDigit(c) || c == '-' || c == 'I' || c == 'N')) return false;
        double d = NumberFormatting.StringToNumber(s);
        return NumberFormatting.ToString(d) == s;
    }

    /// <summary>Interpreter fast path for <c>ta[key]</c> with a number key: an integer index inside a live view.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetElementFast(double key, out JsValue value)
    {
        int idx = (int)key;
        if (idx == key && (uint)idx < (uint)Length) { value = GetElement(idx); return true; }
        value = default;
        return false;
    }

    /// <summary>
    /// Interpreter fast path for <c>ta[key] = v</c> with number key and value on a non-BigInt view: a numeric key that is
    /// not a valid integer index (fractional, negative, out of range) is ignored, exactly as the exotic [[Set]] does
    /// after converting the value, which for a number has no effect. False leaves everything to the generic path.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TrySetElementFast(double key, in JsValue v)
    {
        if (!v.IsNumber || Kind is TypedKind.BigInt64 or TypedKind.BigUint64 || Buffer.Immutable) return false;
        int idx = (int)key;
        if (idx == key && (uint)idx < (uint)Length) SetElement(idx, v.Num);
        return true;
    }

    public JsValue GetElement(int index)
    {
        int es = ElementSize(Kind);
        long byteIndex = ByteOffset + (long)index * es;
        // the buffer may have shrunk or been detached by user code since the index was validated
        if (index < 0 || index >= Length || byteIndex + es > Buffer.Data.Length) return JsValue.Undefined;
        var span = Buffer.Data.AsSpan((int)byteIndex);
        return Kind switch
        {
            TypedKind.Int8 => new JsValue((sbyte)span[0]),
            TypedKind.Uint8 or TypedKind.Uint8Clamped => new JsValue(span[0]),
            TypedKind.Int16 => new JsValue(BinaryPrimitives.ReadInt16LittleEndian(span)),
            TypedKind.Uint16 => new JsValue(BinaryPrimitives.ReadUInt16LittleEndian(span)),
            TypedKind.Int32 => new JsValue(BinaryPrimitives.ReadInt32LittleEndian(span)),
            TypedKind.Uint32 => new JsValue(BinaryPrimitives.ReadUInt32LittleEndian(span)),
            TypedKind.Float32 => JsValue.FromExternalDouble(BinaryPrimitives.ReadSingleLittleEndian(span)),
            TypedKind.Float16 => JsValue.FromExternalDouble((double)BinaryPrimitives.ReadHalfLittleEndian(span)),
            TypedKind.BigInt64 => JsValue.FromBigInteger(BinaryPrimitives.ReadInt64LittleEndian(span)),
            TypedKind.BigUint64 => JsValue.FromBigInteger(BinaryPrimitives.ReadUInt64LittleEndian(span)),
            _ => JsValue.FromExternalDouble(BinaryPrimitives.ReadDoubleLittleEndian(span)),
        };
    }

    public bool IsBigIntKind => Kind is TypedKind.BigInt64 or TypedKind.BigUint64;

    /// <summary>ToBigInt for BigInt64/BigUint64 arrays, ToNumber otherwise (may run user code).</summary>
    public JsValue ToElementValue(in JsValue v) => IsBigIntKind
        ? JsValue.FromBigInteger(BigIntOps.ToBigInt(Realm.Vm.Realm, v))
        : new JsValue(Operations.ToNumber(Realm.Vm.Realm, v));

    /// <summary>Writes an already converted element value (number or BigInt matching the content type).</summary>
    public void SetElement(int index, in JsValue v)
    {
        if (v.IsBigInt != IsBigIntKind)
            throw JsErrors.TypeError(Realm.Vm.Realm, v.IsBigInt ? "Cannot convert a BigInt value to a number" : $"Cannot convert {v} to a BigInt");
        if (index < 0 || index >= Length) return; // IntegerIndexedElementSet: no-op for invalid indices
        if (v.IsBigInt)
        {
            var span = Buffer.Data.AsSpan(ByteOffset + index * 8);
            var u = (ulong)(v.BigIntValue & ulong.MaxValue);
            BinaryPrimitives.WriteUInt64LittleEndian(span, u);
            return;
        }
        SetElement(index, v.Num);
    }

    public void SetElement(int index, double v)
    {
        if (index < 0 || index >= Length) return;
        var span = Buffer.Data.AsSpan(ByteOffset + index * ElementSize(Kind));
        switch (Kind)
        {
            case TypedKind.Int8: span[0] = (byte)(sbyte)Operations.DoubleToInt32(v); break;
            case TypedKind.Uint8: span[0] = (byte)Operations.DoubleToInt32(v); break;
            case TypedKind.Uint8Clamped: span[0] = double.IsNaN(v) ? (byte)0 : (byte)Math.Clamp(Math.Round(v, MidpointRounding.ToEven), 0, 255); break;
            case TypedKind.Int16: BinaryPrimitives.WriteInt16LittleEndian(span, (short)Operations.DoubleToInt32(v)); break;
            case TypedKind.Uint16: BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)Operations.DoubleToInt32(v)); break;
            case TypedKind.Int32: BinaryPrimitives.WriteInt32LittleEndian(span, Operations.DoubleToInt32(v)); break;
            case TypedKind.Uint32: BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)Operations.DoubleToInt32(v)); break;
            case TypedKind.Float32: BinaryPrimitives.WriteSingleLittleEndian(span, (float)v); break;
            case TypedKind.Float16: BinaryPrimitives.WriteHalfLittleEndian(span, (Half)v); break;
            default: BinaryPrimitives.WriteDoubleLittleEndian(span, v); break;
        }
    }

    private bool TryIndex(in PropertyKey key, out int index, out bool isNumeric)
    {
        index = -1;
        isNumeric = false;
        if (key.Value is not string s) return false;
        if (PropertyKey.TryParseArrayIndex(s, out uint u)) { isNumeric = true; if (u < (uint)Length) { index = (int)u; return true; } return false; }
        isNumeric = IsCanonicalNumeric(s);
        return false;
    }

    public override bool GetOwnProperty(in PropertyKey key, out PropertyDescriptor desc)
    {
        if (TryIndex(key, out int i, out bool numeric))
        {
            desc = PropertyDescriptor.Data(GetElement(i), Buffer.Immutable ? PropFlags.Enumerable : PropFlags.Default);
            return true;
        }
        if (numeric) { desc = default; return false; }
        return base.GetOwnProperty(key, out desc);
    }

    public override bool DefineOwnProperty(in PropertyKey key, in PropertyDescriptor desc)
    {
        if (TryIndex(key, out int i, out bool numeric))
        {
            // immutable buffers: elements are non-writable, non-configurable data properties
            if (Buffer.Immutable)
                return PropertyDescriptor.IsCompatible(false, desc, true, PropertyDescriptor.Data(GetElement(i), PropFlags.Enumerable));
            if (desc.IsAccessor || (desc.HasConfigurable && !desc.Configurable) || (desc.HasEnumerable && !desc.Enumerable) || (desc.HasWritable && !desc.Writable)) return false;
            if (desc.HasValue) SetElement(i, ToElementValue(desc.Value));
            return true;
        }
        if (numeric) return false;
        return OrdinaryDefineOwnProperty(key, desc);
    }

    public override JsValue Get(in PropertyKey key, JsValue receiver)
    {
        if (TryIndex(key, out int i, out bool numeric)) return GetElement(i);
        if (numeric) return JsValue.Undefined;
        return OrdinaryGet(key, receiver);
    }

    public override JsValue GetIndex(uint index, JsValue receiver) => index < (uint)Length ? GetElement((int)index) : JsValue.Undefined;

    // [[Set]] (10.4.5.5): numeric keys write through only when the receiver is this array; the index is validated
    // against the current length after the value conversion (which may resize or detach the buffer)
    public override bool Set(in PropertyKey key, JsValue value, JsValue receiver)
    {
        if (key.Value is string s)
        {
            if (PropertyKey.TryParseArrayIndex(s, out uint u)) return SetIndex(u, value, receiver);
            if (IsCanonicalNumeric(s))
            {
                if (Buffer.Immutable) return false;
                // never a valid integer index ("-0", "1.5", "-1", …)
                if (receiver.IsObject && ReferenceEquals(receiver.Ref, this)) ToElementValue(value);
                return true;
            }
        }
        return OrdinarySet(key, value, receiver);
    }

    public override bool SetIndex(uint index, JsValue value, JsValue receiver)
    {
        if (receiver.IsObject && ReferenceEquals(receiver.Ref, this))
        {
            if (Buffer.Immutable) return false;
            var d = ToElementValue(value);
            if (index < (uint)Length) SetElement((int)index, d);
            return true;
        }
        if (Buffer.Immutable) return false;
        if (index >= (uint)Length) return true;
        return OrdinarySet(PropertyKey.FromIndex(index), value, receiver);
    }

    /// <summary>[[PreventExtensions]] (10.4.5.x): fails for arrays that are not fixed-length (resizable, non-shared buffers).</summary>
    public override bool PreventExtensions()
    {
        if (IsLengthTracking || (Buffer.Resizable && !Buffer.Shared)) return false;
        return base.PreventExtensions();
    }

    public override bool HasProperty(in PropertyKey key)
    {
        if (TryIndex(key, out _, out bool numeric)) return true;
        if (numeric) return false;
        return base.HasProperty(key);
    }

    public override bool Delete(in PropertyKey key)
    {
        if (TryIndex(key, out _, out bool numeric)) return false;
        if (numeric) return true;
        return base.Delete(key);
    }

    public override void OwnPropertyKeys(List<PropertyKey> keys)
    {
        for (int i = 0; i < Length; i++) keys.Add(PropertyKey.FromIndex((uint)i));
        OrdinaryOwnKeys(keys);
    }
}

internal static class TypedArrayBuiltins
{
    public static void Setup(Realm r)
    {
        // ArrayBuffer
        var abProto = new JsObject(r, r.ObjectPrototype);
        r.ArrayBufferPrototype = abProto;
        var ab = r.Constructor("ArrayBuffer", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (!a.IsConstruct) throw JsErrors.TypeError(realm, "Constructor ArrayBuffer requires 'new'");
            long len = ToIndex(realm, a[0]);
            long max = -1;
            if (a[1].IsObject)
            {
                var mv = a[1].AsObjectUnchecked.Get("maxByteLength");
                if (!mv.IsUndefined) max = ToIndex(realm, mv);
            }
            if (max >= 0 && len > max) throw JsErrors.RangeError(realm, "byteLength exceeds maxByteLength");   // before OrdinaryCreateFromConstructor
            var p = Operations.GetPrototypeFromConstructor(realm, a.NewTarget.AsObjectUnchecked, realm.ArrayBufferPrototype);
            if (len > realm.MaxArrayLength || max > int.MaxValue) throw JsErrors.RangeError(realm, "Array buffer allocation failed");
            return JsValue.FromObject(new JsArrayBuffer(realm, p, (int)len, (int)max));
        }, abProto);
        r.DefineGlobal("ArrayBuffer", JsValue.FromObject(ab));
        r.Getter(ab, JsSymbol.Species, "[Symbol.species]", static (in NativeArgs a) => a.This);
        r.Method(ab, "isView", 1, static (in NativeArgs a) => JsValue.FromBool(a[0].Ref is JsTypedArray or JsDataView));
        r.Getter(abProto, "byteLength", "byteLength", static (in NativeArgs a) => new JsValue(ThisBuffer(a).Data.Length));
        r.Getter(abProto, "maxByteLength", "maxByteLength", static (in NativeArgs a) =>
        {
            var b = ThisBuffer(a);
            return new JsValue(b.Detached ? 0 : b.Resizable ? b.MaxByteLength : b.Data.Length);
        });
        r.Getter(abProto, "resizable", "resizable", static (in NativeArgs a) => JsValue.FromBool(ThisBuffer(a).Resizable));
        r.Getter(abProto, "detached", "detached", static (in NativeArgs a) => JsValue.FromBool(ThisBuffer(a).Detached));
        r.Getter(abProto, "immutable", "immutable", static (in NativeArgs a) => JsValue.FromBool(ThisBuffer(a).Immutable));
        r.Method(abProto, "transferToImmutable", 0, static (in NativeArgs a) => CopyAndDetach(a, preserveResizability: false, immutable: true));
        r.Method(abProto, "sliceToImmutable", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var b = ThisBuffer(a);
            if (b.Detached) throw JsErrors.TypeError(realm, "ArrayBuffer is detached");
            long len = b.Data.Length;
            long first = Operations.RelativeIndex(realm, a[0], len, 0);
            long final = Operations.RelativeIndex(realm, a[1], len, len);
            long newLen = Math.Max(final - first, 0);
            if (b.Detached) throw JsErrors.TypeError(realm, "ArrayBuffer is detached");
            if (b.Data.Length < final) throw JsErrors.RangeError(realm, "ArrayBuffer was shrunk below the slice end");
            return JsValue.FromObject(new JsArrayBuffer(realm, realm.ArrayBufferPrototype, b.Data.AsSpan((int)first, (int)newLen), (int)newLen));
        });
        r.Method(abProto, "resize", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var b = ThisBuffer(a);
            if (!b.Resizable) throw JsErrors.TypeError(realm, "ArrayBuffer.prototype.resize called on a fixed-length buffer");
            long n = ToIndex(realm, a[0]);
            if (b.Detached) throw JsErrors.TypeError(realm, "ArrayBuffer is detached");
            if (n > b.MaxByteLength) throw JsErrors.RangeError(realm, "Invalid length for resizable ArrayBuffer");
            b.Resize((int)n);
            return JsValue.Undefined;
        });
        r.Method(abProto, "transfer", 0, static (in NativeArgs a) => CopyAndDetach(a, preserveResizability: true));
        r.Method(abProto, "transferToFixedLength", 0, static (in NativeArgs a) => CopyAndDetach(a, preserveResizability: false));
        r.Method(abProto, "slice", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var b = ThisBuffer(a);
            if (b.Detached) throw JsErrors.TypeError(realm, "ArrayBuffer is detached");
            long len = b.Data.Length;
            long first = Operations.RelativeIndex(realm, a[0], len, 0);
            long final = Operations.RelativeIndex(realm, a[1], len, len);
            long newLen = Math.Max(final - first, 0);
            var c = Operations.SpeciesConstructorDefault(realm, b, realm.ArrayBufferConstructor!);
            var lenV = new JsValue((double)newLen);
            var created = realm.Vm.Construct(JsValue.FromObject(c), new ReadOnlySpan<JsValue>(in lenV));
            if (created.Ref is not JsArrayBuffer nb) throw JsErrors.TypeError(realm, "Species constructor did not return an ArrayBuffer");
            if (nb.Detached) throw JsErrors.TypeError(realm, "Species constructor returned a detached ArrayBuffer");
            if (nb.Immutable) throw JsErrors.TypeError(realm, "Species constructor returned an immutable ArrayBuffer");
            if (ReferenceEquals(nb, b)) throw JsErrors.TypeError(realm, "Species constructor returned the same ArrayBuffer");
            if (nb.Data.Length < newLen) throw JsErrors.TypeError(realm, "Species constructor returned a too small ArrayBuffer");
            if (b.Detached) throw JsErrors.TypeError(realm, "ArrayBuffer is detached");
            long cur = b.Data.Length;
            if (first < cur)
            {
                long count = Math.Min(newLen, cur - first);
                Array.Copy(b.Data, first, nb.Data, 0, count);
            }
            return JsValue.FromObject(nb);
        });
        r.ArrayBufferConstructor = ab;
        abProto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("ArrayBuffer"), PropFlags.Configurable);

        // %TypedArray%
        var taProto = new JsObject(r, r.ObjectPrototype);
        var ta = r.Constructor("TypedArray", 0, static (in NativeArgs a) => throw JsErrors.TypeError(a.Realm, "Abstract class TypedArray not directly constructable"), taProto);
        r.Getter(taProto, "length", "length", static (in NativeArgs a) => new JsValue(ThisUnchecked(a).Length));
        r.Getter(taProto, "byteLength", "byteLength", static (in NativeArgs a) => new JsValue(ThisUnchecked(a).Length * JsTypedArray.ElementSize(ThisUnchecked(a).Kind)));
        r.Getter(taProto, "byteOffset", "byteOffset", static (in NativeArgs a) => new JsValue(ThisUnchecked(a).IsOutOfBounds ? 0 : ThisUnchecked(a).ByteOffset));
        r.Getter(taProto, "buffer", "buffer", static (in NativeArgs a) => JsValue.FromObject(ThisUnchecked(a).Buffer));
        r.Getter(taProto, JsSymbol.ToStringTag, "[Symbol.toStringTag]", static (in NativeArgs a) => a.This.Ref is JsTypedArray t ? new JsValue(t.ClassName) : JsValue.Undefined);
        // generic Array algorithms reused for typed arrays, behind a receiver check (internal length is used)
        // %TypedArray%.prototype.toLocaleString (23.2.3.32): like Array's, but uses the internal length
        r.Method(taProto, "toLocaleString", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            // ECMA-402 19.5.1 / 23.2.3.32: element toLocaleString receives (locales, options) when Intl is present
            ReadOnlySpan<JsValue> localeArgs = realm.Intl != null ? new[] { a[0], a[1] } : ReadOnlySpan<JsValue>.Empty;
            var t = TypedArraySpec.Validate(realm, a.This);
            int len = t.Length;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < len; i++)
            {
                realm.Vm.Tick();
                if (i > 0) sb.Append(',');
                var v = t.Get(PropertyKey.FromIndex((uint)i), a.This);
                if (!v.IsNullOrUndefined) sb.Append(Operations.ToString(realm, Operations.Invoke(realm, v, "toLocaleString", localeArgs)));
            }
            return new JsValue(sb.ToString());
        });
        foreach (var name in new[] { "keys", "entries" })
        {
            var target = r.ArrayPrototype.Get(name);
            var w = r.MethodEager(taProto, name, ((JsObject)target.Ref!).Get("length").IsNumber ? (int)((JsObject)target.Ref!).Get("length").Num : 0, static (in NativeArgs a) =>
            {
                This(a);
                return a.Realm.Vm.Call((JsValue)a.Callee.State!, a.This, a.Args);
            });
            w.State = target;
        }
        taProto.DefineHidden("toString", r.ArrayPrototype.Get("toString"));
        var valuesTarget = r.ArrayPrototype.Get("values");
        var values = r.MethodEager(taProto, "values", 0, static (in NativeArgs a) =>
        {
            This(a);
            return a.Realm.Vm.Call((JsValue)a.Callee.State!, a.This, a.Args);
        });
        values.State = valuesTarget;
        taProto.AddOwnProperty(JsSymbol.Iterator, JsValue.FromObject(values), PropFlags.Hidden);
        TypedArraySpec.Install(r, ta, taProto);
        r.TypedArrayConstructors = new JsObject[Enum.GetValues<TypedKind>().Length];

        foreach (TypedKind kind in Enum.GetValues<TypedKind>())
        {
            var proto = new JsObject(r, taProto);
            var ctor = r.Constructor(kind + "Array", 3, static (in NativeArgs a) =>
            {
                var realm = a.Realm;
                var kind = (TypedKind)a.Callee.State!;
                if (!a.IsConstruct) throw JsErrors.TypeError(realm, $"Constructor {kind}Array requires 'new'");
                int es = JsTypedArray.ElementSize(kind);
                var arg = a[0];
                if (arg.IsNotObject)
                {
                    // ToIndex precedes AllocateTypedArray (and its prototype lookup) for non-object arguments
                    long len = ToIndex(realm, arg);
                    var pn = Operations.GetPrototypeFromConstructor(realm, a.NewTarget.AsObjectUnchecked, (JsObject)a.Callee.Get("prototype").Ref!);
                    if (len * es > realm.MaxArrayLength) throw JsErrors.RangeError(realm, "Invalid typed array length");
                    return JsValue.FromObject(Create(realm, pn, kind, (int)len));
                }
                var p = Operations.GetPrototypeFromConstructor(realm, a.NewTarget.AsObjectUnchecked, (JsObject)a.Callee.Get("prototype").Ref!);
                if (arg.Ref is JsArrayBuffer buf)
                {
                    long off = ToIndex(realm, a[1]);
                    if (off % es != 0) throw JsErrors.RangeError(realm, "Invalid typed array offset");
                    long newLength = a[2].IsUndefined ? -1 : ToIndex(realm, a[2]);
                    if (buf.Detached) throw JsErrors.TypeError(realm, "ArrayBuffer is detached");
                    int bl = buf.Data.Length;
                    if (newLength < 0 && buf.Resizable)
                    {
                        if (off > bl) throw JsErrors.RangeError(realm, "Invalid typed array offset");
                        return JsValue.FromObject(new JsTypedArray(realm, p, kind, buf, (int)off, -1));
                    }
                    long len;
                    if (newLength < 0)
                    {
                        if (bl % es != 0) throw JsErrors.RangeError(realm, "Invalid typed array length");
                        long newByteLength = bl - off;
                        if (newByteLength < 0) throw JsErrors.RangeError(realm, "Invalid typed array offset");
                        len = newByteLength / es;
                    }
                    else
                    {
                        len = newLength;
                        if (off + len * es > bl) throw JsErrors.RangeError(realm, "Invalid typed array length");
                    }
                    return JsValue.FromObject(new JsTypedArray(realm, p, kind, buf, (int)off, (int)len));
                }
                if (arg.Ref is JsTypedArray srcTa)
                {
                    if (srcTa.IsOutOfBounds) throw JsErrors.TypeError(realm, "Source TypedArray is out of bounds or its buffer is detached");
                    if (srcTa.IsBigIntKind != (kind is TypedKind.BigInt64 or TypedKind.BigUint64))
                        throw JsErrors.TypeError(realm, "Content types of source and target typed arrays differ");
                    var copy = Create(realm, p, kind, srcTa.Length);
                    if (srcTa.Kind == kind) Array.Copy(srcTa.Buffer.Data, srcTa.ByteOffset, copy.Buffer.Data, 0, srcTa.Length * es);
                    else for (int i = 0; i < srcTa.Length; i++) copy.SetElement(i, srcTa.GetElement(i));
                    return JsValue.FromObject(copy);
                }
                List<JsValue> items;
                var iterMethod = arg.AsObjectUnchecked.Get(JsSymbol.Iterator);
                if (!iterMethod.IsNullOrUndefined) items = Operations.IterableToList(realm, arg);
                else items = Operations.CreateListFromArrayLike(realm, arg);
                var res = Create(realm, p, kind, items.Count);
                for (int i = 0; i < items.Count; i++) res.SetElement(i, res.ToElementValue(items[i]));
                return JsValue.FromObject(res);
            }, proto, ta);
            ctor.State = kind;
            ctor.AddOwnProperty("BYTES_PER_ELEMENT", new JsValue(JsTypedArray.ElementSize(kind)), PropFlags.None);
            proto.AddOwnProperty("BYTES_PER_ELEMENT", new JsValue(JsTypedArray.ElementSize(kind)), PropFlags.None);
            r.DefineGlobal(kind + "Array", JsValue.FromObject(ctor));
            r.TypedArrayConstructors[(int)kind] = ctor;
            if (kind == TypedKind.Uint8)
            {
                r.Uint8ArrayPrototype = proto;
                Es2026Builtins.InstallUint8Array(r, ctor, proto);
            }
        }
        SetupDataView(r);
        SharedBuiltins.Setup(r);
    }

    private static void SetupDataView(Realm r)
    {
        var proto = new JsObject(r, r.ObjectPrototype);
        var ctor = r.Constructor("DataView", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (!a.IsConstruct) throw JsErrors.TypeError(realm, "Constructor DataView requires 'new'");
            if (a[0].Ref is not JsArrayBuffer buf) throw JsErrors.TypeError(realm, "First argument to DataView constructor must be an ArrayBuffer");
            long off = ToIndex(realm, a[1]);
            if (buf.Detached) throw JsErrors.TypeError(realm, "ArrayBuffer is detached");
            long bl = buf.Data.Length;
            if (off > bl) throw JsErrors.RangeError(realm, "Start offset is outside the bounds of the buffer");
            bool tracking = a[2].IsUndefined && buf.Resizable;
            long len = 0;
            if (!tracking)
            {
                if (a[2].IsUndefined) len = bl - off;
                else
                {
                    len = ToIndex(realm, a[2]);
                    if (off + len > bl) throw JsErrors.RangeError(realm, "Invalid DataView length");
                }
            }
            var p = Operations.GetPrototypeFromConstructor(realm, a.NewTarget.AsObjectUnchecked, (JsObject)a.Callee.Get("prototype").Ref!);
            if (buf.Detached) throw JsErrors.TypeError(realm, "ArrayBuffer is detached");
            bl = buf.Data.Length;
            if (off > bl) throw JsErrors.RangeError(realm, "Start offset is outside the bounds of the buffer");
            if (!tracking && !a[2].IsUndefined && off + len > bl) throw JsErrors.RangeError(realm, "Invalid DataView length");
            return JsValue.FromObject(new JsDataView(realm, p, buf, (int)off, tracking ? -1 : (int)len));
        }, proto);
        r.DefineGlobal("DataView", JsValue.FromObject(ctor));
        proto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("DataView"), PropFlags.Configurable);
        r.Getter(proto, "buffer", "buffer", static (in NativeArgs a) => JsValue.FromObject(View(a).Buffer));
        r.Getter(proto, "byteLength", "byteLength", static (in NativeArgs a) =>
        {
            var v = View(a);
            if (v.IsOutOfBounds) throw JsErrors.TypeError(a.Realm, "DataView is out of bounds");
            return new JsValue(v.ByteLength);
        });
        r.Getter(proto, "byteOffset", "byteOffset", static (in NativeArgs a) =>
        {
            var v = View(a);
            if (v.IsOutOfBounds) throw JsErrors.TypeError(a.Realm, "DataView is out of bounds");
            return new JsValue(v.ByteOffset);
        });
        foreach (TypedKind kind in Enum.GetValues<TypedKind>())
        {
            if (kind == TypedKind.Uint8Clamped) continue;
            var g = r.MethodEager(proto, "get" + kind, 1, static (in NativeArgs a) =>
            {
                var realm = a.Realm;
                var kind = (TypedKind)a.Callee.State!;
                var v = View(a);
                double idx = Operations.ToIntegerOrInfinity(realm, a[0]);
                if (idx < 0 || idx > 9007199254740991) throw JsErrors.RangeError(realm, "Offset is outside the bounds of the DataView");
                bool little = Operations.ToBoolean(a[1]);
                int size = JsTypedArray.ElementSize(kind);
                if (v.IsOutOfBounds) throw JsErrors.TypeError(realm, "DataView is out of bounds");
                if (idx + size > v.ByteLength) throw JsErrors.RangeError(realm, "Offset is outside the bounds of the DataView");
                Span<byte> tmp = stackalloc byte[8];
                v.Buffer.Data.AsSpan(v.ByteOffset + (int)idx, size).CopyTo(tmp);
                if (!little) tmp.Slice(0, size).Reverse();
                return kind switch
                {
                    TypedKind.Int8 => new JsValue((sbyte)tmp[0]),
                    TypedKind.Uint8 => new JsValue(tmp[0]),
                    TypedKind.Int16 => new JsValue(BinaryPrimitives.ReadInt16LittleEndian(tmp)),
                    TypedKind.Uint16 => new JsValue(BinaryPrimitives.ReadUInt16LittleEndian(tmp)),
                    TypedKind.Int32 => new JsValue(BinaryPrimitives.ReadInt32LittleEndian(tmp)),
                    TypedKind.Uint32 => new JsValue(BinaryPrimitives.ReadUInt32LittleEndian(tmp)),
                    TypedKind.Float32 => JsValue.FromExternalDouble(BinaryPrimitives.ReadSingleLittleEndian(tmp)),
                    TypedKind.Float16 => JsValue.FromExternalDouble((double)BinaryPrimitives.ReadHalfLittleEndian(tmp)),
                    TypedKind.BigInt64 => JsValue.FromBigInteger(BinaryPrimitives.ReadInt64LittleEndian(tmp)),
                    TypedKind.BigUint64 => JsValue.FromBigInteger(BinaryPrimitives.ReadUInt64LittleEndian(tmp)),
                    _ => JsValue.FromExternalDouble(BinaryPrimitives.ReadDoubleLittleEndian(tmp)),
                };
            });
            g.State = kind;
            var st = r.MethodEager(proto, "set" + kind, 2, static (in NativeArgs a) =>
            {
                var realm = a.Realm;
                var kind = (TypedKind)a.Callee.State!;
                var v = View(a);
                if (v.Buffer.Immutable) throw JsErrors.TypeError(realm, "Cannot write to a DataView over an immutable ArrayBuffer");
                double idx = Operations.ToIntegerOrInfinity(realm, a[0]);
                if (idx < 0 || idx > 9007199254740991) throw JsErrors.RangeError(realm, "Offset is outside the bounds of the DataView");
                bool big = kind is TypedKind.BigInt64 or TypedKind.BigUint64;
                var bigVal = big ? BigIntOps.ToBigInt(realm, a[1]) : default;
                double num = big ? 0 : Operations.ToNumber(realm, a[1]);
                bool little = Operations.ToBoolean(a[2]);
                int size = JsTypedArray.ElementSize(kind);
                if (v.IsOutOfBounds) throw JsErrors.TypeError(realm, "DataView is out of bounds");
                if (idx + size > v.ByteLength) throw JsErrors.RangeError(realm, "Offset is outside the bounds of the DataView");
                Span<byte> tmp = stackalloc byte[8];
                switch (kind)
                {
                    case TypedKind.Int8:
                    case TypedKind.Uint8: tmp[0] = (byte)Operations.DoubleToInt32(num); break;
                    case TypedKind.Int16:
                    case TypedKind.Uint16: BinaryPrimitives.WriteUInt16LittleEndian(tmp, (ushort)Operations.DoubleToInt32(num)); break;
                    case TypedKind.Int32:
                    case TypedKind.Uint32: BinaryPrimitives.WriteUInt32LittleEndian(tmp, (uint)Operations.DoubleToInt32(num)); break;
                    case TypedKind.Float32: BinaryPrimitives.WriteSingleLittleEndian(tmp, (float)num); break;
                    case TypedKind.Float16: BinaryPrimitives.WriteHalfLittleEndian(tmp, (Half)num); break;
                    case TypedKind.BigInt64:
                    case TypedKind.BigUint64: BinaryPrimitives.WriteUInt64LittleEndian(tmp, (ulong)(bigVal & ulong.MaxValue)); break;
                    default: BinaryPrimitives.WriteDoubleLittleEndian(tmp, num); break;
                }
                if (!little) tmp.Slice(0, size).Reverse();
                tmp.Slice(0, size).CopyTo(v.Buffer.Data.AsSpan(v.ByteOffset + (int)idx, size));
                return JsValue.Undefined;
            });
            st.State = kind;
        }
    }

    private static JsDataView View(in NativeArgs a) =>
        a.This.Ref as JsDataView ?? throw JsErrors.TypeError(a.Realm, "Receiver is not a DataView");

    public static JsTypedArray Create(Realm realm, JsObject proto, TypedKind kind, int length)
    {
        var buf = new JsArrayBuffer(realm, realm.ArrayBufferPrototype, length * JsTypedArray.ElementSize(kind));
        return new JsTypedArray(realm, proto, kind, buf, 0, length);
    }

    /// <summary>ValidateTypedArray: a typed array whose buffer is attached and which is in bounds.</summary>
    private static JsTypedArray This(in NativeArgs a)
    {
        if (a.This.Ref is JsTypedArray t)
        {
            if (t.IsOutOfBounds) throw JsErrors.TypeError(a.Realm, "TypedArray is out of bounds or its buffer is detached");
            return t;
        }
        throw JsErrors.TypeError(a.Realm, "this is not a typed array");
    }

    private static JsTypedArray ThisUnchecked(in NativeArgs a) =>
        a.This.Ref as JsTypedArray ?? throw JsErrors.TypeError(a.Realm, "this is not a typed array");

    private static JsArrayBuffer ThisBuffer(in NativeArgs a) =>
        a.This.Ref is JsArrayBuffer { Shared: false } b ? b : throw JsErrors.TypeError(a.Realm, "Receiver is not an ArrayBuffer");

    /// <summary>ToIndex (7.1.22).</summary>
    internal static long ToIndex(Realm realm, in JsValue v)
    {
        double d = Operations.ToIntegerOrInfinity(realm, v);
        if (d < 0 || d > 9007199254740991) throw JsErrors.RangeError(realm, "Invalid index");
        return (long)d;
    }

    /// <summary>ArrayBufferCopyAndDetach (25.1.3.3).</summary>
    private static JsValue CopyAndDetach(in NativeArgs a, bool preserveResizability, bool immutable = false)
    {
        var realm = a.Realm;
        var b = ThisBuffer(a);
        long newLen = a[0].IsUndefined ? b.Data.Length : ToIndex(realm, a[0]);
        if (b.Detached) throw JsErrors.TypeError(realm, "ArrayBuffer is detached");
        if (b.Immutable) throw JsErrors.TypeError(realm, "Cannot detach an immutable ArrayBuffer");
        if (immutable)
        {
            if (newLen > realm.MaxArrayLength) throw JsErrors.RangeError(realm, "Array buffer allocation failed");
            var ib = new JsArrayBuffer(realm, realm.ArrayBufferPrototype, b.Data.AsSpan(0, (int)Math.Min(newLen, b.Data.Length)), (int)newLen);
            b.Detach();
            return JsValue.FromObject(ib);
        }
        int newMax = preserveResizability && b.Resizable ? b.MaxByteLength : -1;
        if (newMax >= 0 && newLen > newMax) throw JsErrors.RangeError(realm, "Invalid length for resizable ArrayBuffer");
        if (newLen > realm.MaxArrayLength) throw JsErrors.RangeError(realm, "Array buffer allocation failed");
        var nb = new JsArrayBuffer(realm, realm.ArrayBufferPrototype, (int)newLen, newMax);
        Array.Copy(b.Data, nb.Data, Math.Min(newLen, b.Data.Length));
        b.Detach();
        return JsValue.FromObject(nb);
    }
}
