using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using Jaunt.Runtime;

namespace Jaunt;

/// <summary>Internal value tag. <c>default</c> (0) is <see cref="Undefined"/>.</summary>
internal enum JsTag : byte
{
    Undefined = 0,
    Null = 1,
    Boolean = 2,
    Number = 3,
    String = 4,
    Symbol = 5,
    Object = 6,
    /// <summary>Internal sentinel: TDZ / array hole. Never escapes to user code or public API.</summary>
    Empty = 7,
    BigInt = 8,
}

/// <summary>
/// A JavaScript value. Primitive values (undefined, null, boolean, number, string) are freely copyable.
/// Object values (objects, functions, promises, ...) belong to a specific <see cref="JsEngine"/> realm and
/// become invalid once that engine is disposed.
/// </summary>
/// <remarks>
/// <c>default(JsValue)</c> is <c>undefined</c>. The managed reference is kept in a dedicated field that is
/// always visible to the GC; numbers are never encoded together with references.
/// The struct layout is not part of the public ABI.
/// </remarks>
public readonly struct JsValue : IEquatable<JsValue>
{
    // Layout (16 bytes): a GC-visible reference field plus a 64-bit payload that never contains references.
    //   Bits in [0, 15]  → non-number tag (0 undefined, 1 null, 2 false, 8 true, 3 bigint, 4 string, 5 symbol, 6 object, 7 empty)
    //   Bits  > 15       → a number; Bits = IEEE754(d) XOR NumberXor. The XOR keeps default(JsValue) == undefined and
    //                      maps the tag range onto NaN payloads (0x7FF8_0000_0000_0010..1F) that are canonicalized away
    //                      at every boundary where arbitrary double bits can enter.
    internal readonly object? Ref;
    internal readonly ulong Bits;

    internal const ulong NumberXor = 0x7FF8_0000_0000_0010UL;
    internal const ulong UndefinedBits = 0, NullBits = 1, FalseBits = 2, BigIntBits = 3, StringBits = 4, SymbolBits = 5, ObjectBits = 6, EmptyBits = 7, TrueBits = 8;
    private static ReadOnlySpan<byte> TagMap => new byte[] { 0, 1, 2, 8, 4, 5, 6, 7, 2, 7, 7, 7, 7, 7, 7, 7 };

    internal JsTag Tag
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Bits > 15 ? JsTag.Number : (JsTag)TagMap[(int)Bits];
    }

    /// <summary>The number payload (only meaningful when <see cref="IsNumber"/>).</summary>
    internal double Num
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => BitConverter.Int64BitsToDouble((long)(Bits ^ NumberXor));
    }

    internal bool IsTrue => Bits == TrueBits;

    /// <summary>
    /// <c>slot = value</c> that skips the GC write barrier when the value holds no reference: storing a null never needs
    /// one, and the barrier (a call, a store-release and several loads of GC globals) is a large part of copying a number.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Store(ref JsValue slot, in JsValue value)
    {
        if (value.Ref == null) slot = new JsValue((object?)null, value.Bits);
        else slot = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private JsValue(object? reference, ulong bits)
    {
        Ref = reference;
        Bits = bits;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal JsValue(JsTag tag, double num, object? reference)
    {
        Ref = reference;
        Bits = tag switch
        {
            JsTag.Number => (ulong)BitConverter.DoubleToInt64Bits(num) ^ NumberXor,
            JsTag.Boolean => num != 0 ? TrueBits : FalseBits,
            _ => (ulong)tag,
        };
    }

    /// <summary>Internal number constructor. Callers guarantee <paramref name="number"/> is not a payload-carrying NaN
    /// from the reserved range (true for all arithmetic results); external doubles go through <see cref="From(double)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal JsValue(double number)
    {
        Ref = null;
        Bits = (ulong)BitConverter.DoubleToInt64Bits(number) ^ NumberXor;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal JsValue(string value)
    {
        Ref = value;
        Bits = StringBits;
    }

    /// <summary>A lazily concatenated string; see <see cref="JsRope"/>.</summary>
    internal JsValue(JsRope value)
    {
        Ref = value;
        Bits = StringBits;
    }

    /// <summary>Creates a JS BigInt.</summary>
    public static JsValue FromBigInteger(System.Numerics.BigInteger value) => new(value, BigIntBits);

    /// <summary>Whether the value is a BigInt.</summary>
    public bool IsBigInt => Bits == BigIntBits;

    internal System.Numerics.BigInteger BigIntValue => (System.Numerics.BigInteger)Ref!;

    /// <summary>Returns the BigInt value. Throws when the value is not a BigInt (no JS coercion is performed).</summary>
    public System.Numerics.BigInteger AsBigInteger()
    {
        if (!IsBigInt) ThrowType("bigint");
        return BigIntValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal JsValue(JsObject value)
    {
        Ref = value;
        Bits = ObjectBits;
    }

    /// <summary>Number from arbitrary bits (typed arrays, host values): canonicalizes NaN.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static JsValue FromExternalDouble(double d) => double.IsNaN(d) ? NaN : new JsValue(d);

    /// <summary>The <c>undefined</c> value.</summary>
    public static JsValue Undefined => default;
    /// <summary>The <c>null</c> value.</summary>
    public static JsValue Null => new(null, NullBits);
    /// <summary>The boolean <c>true</c>.</summary>
    public static JsValue True => new(null, TrueBits);
    /// <summary>The boolean <c>false</c>.</summary>
    public static JsValue False => new(null, FalseBits);
    internal static JsValue Empty => new(null, EmptyBits);
    internal static readonly JsValue NaN = new(BitConverter.Int64BitsToDouble(0x7FF8_0000_0000_0000));
    internal static readonly JsValue Zero = new(0.0);
    internal static readonly JsValue EmptyString = new(string.Empty);

    /// <summary>Creates a JS number.</summary>
    public static JsValue From(double value) => FromExternalDouble(value);
    /// <summary>Creates a JS number from an <see cref="int"/>.</summary>
    public static JsValue From(int value) => new(value);
    /// <summary>Creates a JS number from a <see cref="float"/>.</summary>
    public static JsValue From(float value) => FromExternalDouble(value);
    /// <summary>Creates a JS boolean.</summary>
    public static JsValue From(bool value) => value ? True : False;
    /// <summary>Creates a JS string, or <c>null</c> when <paramref name="value"/> is null.</summary>
    public static JsValue From(string? value) => value is null ? Null : new JsValue(value);

    /// <summary>
    /// Creates a JS number from a <see cref="long"/>. Values outside the safe integer range (±2^53-1) are
    /// rejected instead of silently losing precision.
    /// </summary>
    public static JsValue From(long value)
    {
        if (value > MaxSafeInteger || value < -MaxSafeInteger)
            throw new ArgumentOutOfRangeException(nameof(value), "Value is outside the JavaScript safe integer range; choose an explicit lossy conversion (FromLossy) instead.");
        return new JsValue(value);
    }

    /// <summary>Creates a JS number from a <see cref="long"/>, explicitly accepting precision loss.</summary>
    public static JsValue FromLossy(long value) => new(value);

    /// <summary>Creates a JS number from a <see cref="decimal"/>, explicitly accepting precision loss.</summary>
    public static JsValue FromLossy(decimal value) => new((double)value);

    internal const long MaxSafeInteger = 9007199254740991L;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static JsValue FromBool(bool b) => new(null, b ? TrueBits : FalseBits);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static JsValue FromObject(JsObject o) => new(o);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static JsValue FromSymbol(JsSymbol s) => new(s, SymbolBits);

    /// <summary>True for <c>undefined</c>.</summary>
    public bool IsUndefined => Bits == UndefinedBits;
    /// <summary>True for <c>null</c>.</summary>
    public bool IsNull => Bits == NullBits;
    /// <summary>True for <c>null</c> or <c>undefined</c>.</summary>
    public bool IsNullOrUndefined => Bits <= NullBits;
    /// <summary>True for booleans.</summary>
    public bool IsBoolean => Bits == TrueBits || Bits == FalseBits;
    /// <summary>True for numbers.</summary>
    public bool IsNumber => Bits > 15;
    /// <summary>True for strings.</summary>
    public bool IsString => Bits == StringBits;
    /// <summary>True for symbols.</summary>
    public bool IsSymbol => Bits == SymbolBits;
    /// <summary>True for objects (including functions, arrays and promises).</summary>
    public bool IsObject => Bits == ObjectBits;
    /// <summary>True for callable objects.</summary>
    public bool IsFunction => Bits == ObjectBits && Ref is JsFunctionObject;
    /// <summary>True for arrays.</summary>
    public bool IsArray => Bits == ObjectBits && Ref is JsArray;
    /// <summary>True for promise objects.</summary>
    public bool IsPromise => Bits == ObjectBits && Ref is JsPromise;
    internal bool IsEmpty => Bits == EmptyBits;
    internal bool IsNotNumber => Bits <= 15;
    internal bool IsNotObject => Bits != ObjectBits;
    internal bool IsNotString => Bits != StringBits;
    internal bool IsNotUndefined => Bits != UndefinedBits;
    internal bool IsNotEmpty => Bits != EmptyBits;
    internal bool IsNotNullish => Bits > NullBits;

    /// <summary>Object payload. Type-checked cast: misuse faults the engine instead of corrupting memory.</summary>
    internal JsObject AsObjectUnchecked => (JsObject)Ref!;
    /// <summary>String payload (type-checked).</summary>
    internal string AsStringUnchecked => Ref is string s ? s : Unsafe.As<JsRope>(Ref!).Flatten();

    /// <summary>Length of a string value without flattening a rope. The value must be a string.</summary>
    internal int StringLength => Ref is string s ? s.Length : Unsafe.As<JsRope>(Ref!).Length;

    /// <summary>Returns the number. Throws when the value is not a number (no JS coercion is performed).</summary>
    public double AsNumber()
    {
        if (Tag != JsTag.Number) ThrowType("number");
        return Num;
    }

    /// <summary>Returns the boolean. Throws when the value is not a boolean (no JS coercion is performed).</summary>
    public bool AsBoolean()
    {
        if (!IsBoolean) ThrowType("boolean");
        return Bits == TrueBits;
    }

    /// <summary>Returns the string. Throws when the value is not a string (no JS coercion is performed).</summary>
    public string AsString()
    {
        if (Tag != JsTag.String) ThrowType("string");
        return AsStringUnchecked;
    }

    /// <summary>
    /// Returns the value as an <see cref="int"/>. The number must be an integer inside the Int32 range; truncation
    /// or overflow is never applied implicitly.
    /// </summary>
    public int AsInt32()
    {
        double d = AsNumber();
        if (d != Math.Floor(d) || d < int.MinValue || d > int.MaxValue || double.IsNaN(d))
            throw new InvalidCastException($"JS number {NumberFormatting.ToString(d)} is not an Int32 integer.");
        return (int)d;
    }

    /// <summary>
    /// Returns the value as an <see cref="long"/>. The number must be an integer in the safe integer range.
    /// </summary>
    public long AsInt64()
    {
        double d = AsNumber();
        if (d != Math.Floor(d) || Math.Abs(d) > MaxSafeInteger || double.IsNaN(d))
            throw new InvalidCastException($"JS number {NumberFormatting.ToString(d)} is not a safe integer.");
        return (long)d;
    }

    /// <summary>Returns a descriptive name of the JS type (as <c>typeof</c> would, with "null" for null).</summary>
    public string TypeName => Tag switch
    {
        JsTag.Undefined => "undefined",
        JsTag.Null => "null",
        JsTag.Boolean => "boolean",
        JsTag.Number => "number",
        JsTag.String => "string",
        JsTag.Symbol => "symbol",
        JsTag.BigInt => "bigint",
        JsTag.Object => Ref is JsHtmlDda ? "undefined" : Ref is JsFunctionObject ? "function" : "object",
        _ => "empty",
    };

    private void ThrowType(string expected) =>
        throw new InvalidCastException($"JS value is {TypeName}, not {expected}.");

    /// <inheritdoc />
    public bool Equals(JsValue other)
    {
        if (Tag != other.Tag) return false;
        return Tag switch
        {
            JsTag.Number => Num.Equals(other.Num),
            JsTag.Boolean => Bits == other.Bits,
            JsTag.String => StringLength == other.StringLength && string.Equals(AsStringUnchecked, other.AsStringUnchecked, StringComparison.Ordinal),
            JsTag.BigInt => BigIntValue == other.BigIntValue,
            JsTag.Undefined or JsTag.Null or JsTag.Empty => true,
            _ => ReferenceEquals(Ref, other.Ref),
        };
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is JsValue v && Equals(v);

    /// <inheritdoc />
    public override int GetHashCode() => Tag switch
    {
        JsTag.Number => Num.GetHashCode(),
        JsTag.Boolean => Bits.GetHashCode(),
        JsTag.String => StringComparer.Ordinal.GetHashCode(AsStringUnchecked),
        JsTag.BigInt => BigIntValue.GetHashCode(),
        JsTag.Undefined or JsTag.Null or JsTag.Empty => (int)Tag,
        _ => Ref is Runtime.JsObject o ? o.IdentityHash : RuntimeHelpers.GetHashCode(Ref!),
    };

    /// <summary>Equality operator (value equality for primitives, identity for objects).</summary>
    public static bool operator ==(JsValue left, JsValue right) => left.Equals(right);
    /// <summary>Inequality operator.</summary>
    public static bool operator !=(JsValue left, JsValue right) => !left.Equals(right);

    /// <summary>
    /// Debug-friendly description. Primitives are rendered like JS <c>String(v)</c>; objects are rendered as a
    /// type tag. This never executes JavaScript.
    /// </summary>
    public override string ToString() => Tag switch
    {
        JsTag.Undefined => "undefined",
        JsTag.Null => "null",
        JsTag.Boolean => Bits == TrueBits ? "true" : "false",
        JsTag.Number => NumberFormatting.ToString(Num),
        JsTag.String => AsStringUnchecked,
        JsTag.Symbol => ((JsSymbol)Ref!).ToString(),
        JsTag.BigInt => BigIntValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        JsTag.Object => "[object " + ((JsObject)Ref!).ClassName + "]",
        _ => "<empty>",
    };

    /// <summary>Implicit conversion from double.</summary>
    public static implicit operator JsValue(double value) => FromExternalDouble(value);
    /// <summary>Implicit conversion from int.</summary>
    public static implicit operator JsValue(int value) => new(value);
    /// <summary>Implicit conversion from bool.</summary>
    public static implicit operator JsValue(bool value) => FromBool(value);
    /// <summary>Implicit conversion from string (null becomes JS null).</summary>
    public static implicit operator JsValue(string? value) => From(value);
}
