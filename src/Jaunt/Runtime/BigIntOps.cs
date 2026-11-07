using System;
using System.Globalization;
using System.Numerics;
using Jaunt.Compiler;

namespace Jaunt.Runtime;

/// <summary>BigInt arithmetic and conversions (ECMA-262 6.1.6.2, 7.1.13–7.1.15).</summary>
internal static class BigIntOps
{
    /// <summary>Upper bound on BigInt magnitude, in bits (V8 uses 2^30; Jaunt keeps a smaller, budget-friendly cap).</summary>
    public const int MaxBits = 1 << 24;

    private static JsValue Checked(Realm realm, BigInteger v)
    {
        if (v.IsZero) return JsValue.FromBigInteger(v);
        long bits = (long)v.GetBitLength();
        if (bits > MaxBits) throw JsErrors.RangeError(realm, "Maximum BigInt size exceeded");
        if (bits > 4096) realm.ChargeBytes(bits / 8 + 32);
        return JsValue.FromBigInteger(v);
    }

    /// <summary>Applies a binary numeric operator after ToNumeric on both operands.</summary>
    public static JsValue Arith(Realm realm, Op op, in JsValue a, in JsValue b)
    {
        var x = Operations.ToNumericValue(realm, a);
        var y = Operations.ToNumericValue(realm, b);
        if (x.IsNumber && y.IsNumber) return NumberOp(realm, op, x.Num, y.Num);
        return Binary(realm, op, x, y);
    }

    private static JsValue NumberOp(Realm realm, Op op, double x, double y)
    {
        switch (op)
        {
            case Op.Add: return new JsValue(x + y);
            case Op.Sub: return new JsValue(x - y);
            case Op.Mul: return new JsValue(x * y);
            case Op.Div: return new JsValue(x / y);
            case Op.Mod: return new JsValue(x % y);
            case Op.Exp: return new JsValue(MathBuiltins.Pow(x, y));
        }
        int ix = Operations.DoubleToInt32(x), iy = Operations.DoubleToInt32(y);
        return op switch
        {
            Op.BitAnd => new JsValue(ix & iy),
            Op.BitOr => new JsValue(ix | iy),
            Op.BitXor => new JsValue(ix ^ iy),
            Op.Shl => new JsValue(ix << (iy & 31)),
            Op.Shr => new JsValue(ix >> (iy & 31)),
            Op.UShr => new JsValue((double)((uint)ix >> (iy & 31))),
            _ => throw new InvalidOperationException("Jaunt internal error: not a numeric operator."),
        };
    }

    /// <summary>Binary operator on numeric values where at least one is a BigInt.</summary>
    public static JsValue Binary(Realm realm, Op op, in JsValue x, in JsValue y)
    {
        if (!x.IsBigInt || !y.IsBigInt)
            throw JsErrors.TypeError(realm, "Cannot mix BigInt and other types, use explicit conversions");
        var a = x.BigIntValue;
        var b = y.BigIntValue;
        switch (op)
        {
            case Op.Add: return Checked(realm, a + b);
            case Op.Sub: return Checked(realm, a - b);
            case Op.Mul:
                if ((long)a.GetBitLength() + (long)b.GetBitLength() > MaxBits + 1) throw JsErrors.RangeError(realm, "Maximum BigInt size exceeded");
                return Checked(realm, a * b);
            case Op.Div:
                if (b.IsZero) throw JsErrors.RangeError(realm, "Division by zero");
                return JsValue.FromBigInteger(BigInteger.Divide(a, b));
            case Op.Mod:
                if (b.IsZero) throw JsErrors.RangeError(realm, "Division by zero");
                return JsValue.FromBigInteger(BigInteger.Remainder(a, b));
            case Op.Exp:
            {
                if (b.Sign < 0) throw JsErrors.RangeError(realm, "Exponent must be non-negative");
                if (b.IsZero) return JsValue.FromBigInteger(BigInteger.One);
                if (a.IsZero || a.IsOne) return x;
                if (a == BigInteger.MinusOne) return JsValue.FromBigInteger(b.IsEven ? BigInteger.One : BigInteger.MinusOne);
                if (b > MaxBits || (long)a.GetBitLength() * (long)b > MaxBits + 64) throw JsErrors.RangeError(realm, "Maximum BigInt size exceeded");
                return Checked(realm, BigInteger.Pow(a, (int)b));
            }
            case Op.BitAnd: return JsValue.FromBigInteger(a & b);
            case Op.BitOr: return JsValue.FromBigInteger(a | b);
            case Op.BitXor: return JsValue.FromBigInteger(a ^ b);
            case Op.Shl: return ShiftLeft(realm, a, b);
            case Op.Shr: return ShiftLeft(realm, a, -b);
            case Op.UShr: throw JsErrors.TypeError(realm, "BigInts have no unsigned right shift, use >> instead");
            default: throw new InvalidOperationException("Jaunt internal error: not a numeric operator.");
        }
    }

    private static JsValue ShiftLeft(Realm realm, BigInteger a, BigInteger shift)
    {
        if (a.IsZero) return JsValue.FromBigInteger(a);
        if (shift.Sign >= 0)
        {
            if (shift > MaxBits) throw JsErrors.RangeError(realm, "Maximum BigInt size exceeded");
            return Checked(realm, a << (int)shift);
        }
        var right = -shift;
        if (right > int.MaxValue) return JsValue.FromBigInteger(a.Sign < 0 ? BigInteger.MinusOne : BigInteger.Zero);
        return JsValue.FromBigInteger(a >> (int)right); // arithmetic shift: rounds toward -infinity
    }

    public static JsValue Negate(Realm realm, in JsValue v)
    {
        var n = Operations.ToNumericValue(realm, v);
        return n.IsBigInt ? JsValue.FromBigInteger(-n.BigIntValue) : new JsValue(-n.Num);
    }

    public static JsValue BitNot(Realm realm, in JsValue v)
    {
        var n = Operations.ToNumericValue(realm, v);
        return n.IsBigInt ? JsValue.FromBigInteger(-n.BigIntValue - BigInteger.One) : new JsValue(~Operations.DoubleToInt32(n.Num));
    }

    public static JsValue Increment(Realm realm, in JsValue v, int delta)
    {
        var n = Operations.ToNumericValue(realm, v);
        return n.IsBigInt ? Checked(realm, n.BigIntValue + delta) : new JsValue(n.Num + delta);
    }

    /// <summary>Compares a BigInt with a (non-NaN) number: -1, 0 or 1.</summary>
    public static int CompareWithNumber(BigInteger a, double d)
    {
        if (double.IsNaN(d)) return 2; // unordered: never equal
        if (double.IsPositiveInfinity(d)) return -1;
        if (double.IsNegativeInfinity(d)) return 1;
        var fl = Math.Floor(d);
        var bi = new BigInteger(fl);
        int c = a.CompareTo(bi);
        if (c != 0) return c;
        return fl < d ? -1 : 0;
    }

    /// <summary>The VM whose time limits long conversions check (set by <see cref="StringToBigInt(string, Vm?)"/>).</summary>
    [ThreadStatic] private static Vm? t_vm;

    /// <summary>StringToBigInt with the execution's time limits checked during long conversions.</summary>
    public static BigInteger? StringToBigInt(string s, Vm? vm)
    {
        var saved = t_vm;
        t_vm = vm;
        try { return StringToBigInt(s); }
        finally { t_vm = saved; }
    }

    /// <summary>StringToBigInt (7.1.14). Returns null when the string is not a valid StringIntegerLiteral.</summary>
    public static BigInteger? StringToBigInt(string s)
    {
        var t = NumberFormatting.TrimJs(s).AsSpan();
        if (t.Length == 0) return BigInteger.Zero;
        int radix = 10;
        if (t.Length > 2 && t[0] == '0')
        {
            char p = (char)(t[1] | 0x20);
            if (p == 'x') radix = 16;
            else if (p == 'o') radix = 8;
            else if (p == 'b') radix = 2;
            if (radix != 10) t = t[2..];
        }
        bool neg = false;
        if (radix == 10 && (t[0] == '+' || t[0] == '-'))
        {
            neg = t[0] == '-';
            t = t[1..];
            if (t.Length == 0) return null;
        }
        var r = ParseDigits(t, radix);
        if (r == null) return null;
        return neg ? -r.Value : r.Value;
    }

    private static BigInteger? ParseDigits(ReadOnlySpan<char> t, int radix)
    {
        if (t.Length == 0) return null;
        if (radix == 10)
        {
            foreach (var c in t) if (c < '0' || c > '9') return null;
            return t.Length > 1000 ? ParseLarge(t, 10, t_vm) : BigInteger.Parse(t, NumberStyles.None, CultureInfo.InvariantCulture);
        }
        foreach (var c in t)
        {
            int d = c >= '0' && c <= '9' ? c - '0' : (c | 0x20) >= 'a' && (c | 0x20) <= 'z' ? (c | 0x20) - 'a' + 10 : 99;
            if (d >= radix) return null;
        }
        return ParseLarge(t, radix, t_vm);
    }

    /// <summary>ToBigInt (7.1.13).</summary>
    public static BigInteger ToBigInt(Realm realm, in JsValue v)
    {
        var p = Operations.ToPrimitive(realm, v, Operations.PreferredType.Number);
        switch (p.Tag)
        {
            case JsTag.BigInt: return p.BigIntValue;
            case JsTag.Boolean: return p.IsTrue ? BigInteger.One : BigInteger.Zero;
            case JsTag.String:
                return StringToBigInt(p.AsStringUnchecked, realm.Vm) ?? throw JsErrors.SyntaxError(realm, $"Cannot convert {p.AsStringUnchecked} to a BigInt");
            case JsTag.Number: throw JsErrors.TypeError(realm, $"Cannot convert {p} to a BigInt");
            case JsTag.Symbol: throw JsErrors.TypeError(realm, "Cannot convert a Symbol value to a BigInt");
            default: throw JsErrors.TypeError(realm, $"Cannot convert {p} to a BigInt");
        }
    }

    /// <summary>NumberToBigInt (21.2.1.1.1).</summary>
    public static BigInteger NumberToBigInt(Realm realm, double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d) || Math.Floor(d) != d)
            throw JsErrors.RangeError(realm, $"The number {NumberFormatting.ToString(d)} cannot be converted to a BigInt because it is not an integer");
        return new BigInteger(d);
    }

    /// <summary>Number(bigint): rounds to the nearest double (ties to even).</summary>
    /// <summary>𝔽(ℝ(v)): correctly rounded (ties to even); .NET's explicit conversion truncates beyond 2^53.</summary>
    public static double ToDouble(BigInteger v)
    {
        if (v.Sign == 0) return 0;
        var a = BigInteger.Abs(v);
        long bits = (long)a.GetBitLength();
        if (bits <= 53) return (double)v;
        if (bits > 1024) return v.Sign < 0 ? double.NegativeInfinity : double.PositiveInfinity;
        int shift = (int)(bits - 54);   // keep 54 bits: 53 significant + 1 rounding bit
        var top = a >> shift;
        bool sticky = !(a & ((BigInteger.One << shift) - 1)).IsZero;
        long t = (long)top;
        long mant = t >> 1;
        bool round = (t & 1) != 0 && (sticky || (mant & 1) != 0);
        if (round) mant++;
        double r = Math.ScaleB(mant, shift + 1);
        return v.Sign < 0 ? -r : r;
    }

    public static string ToString(BigInteger v, int radix) => ToString(v, radix, null);

    /// <summary>
    /// Number → string in <paramref name="radix"/>. Large values (more than ~4,000 bits) are converted divide-and-conquer
    /// (split by precomputed powers of the radix): far faster than digit-by-digit or the runtime's quadratic formatter,
    /// and <paramref name="vm"/>'s time limits (Timeout, cancellation, Interrupt) are checked between the large divisions,
    /// so converting a million-digit BigInt cannot run unbounded.
    /// </summary>
    public static string ToString(BigInteger v, int radix, Vm? vm)
    {
        if (v.GetBitLength() > 4096) return ToStringLarge(v, radix, vm);
        if (radix == 10) return v.ToString(CultureInfo.InvariantCulture);
        if (v.IsZero) return "0";
        bool neg = v.Sign < 0;
        var m = BigInteger.Abs(v);
        var sb = new System.Text.StringBuilder();
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        while (!m.IsZero)
        {
            m = BigInteger.DivRem(m, radix, out var rem);
            sb.Append(digits[(int)rem]);
        }
        if (neg) sb.Append('-');
        var arr = sb.ToString().ToCharArray();
        Array.Reverse(arr);
        return new string(arr);
    }

    private const string DigitChars = "0123456789abcdefghijklmnopqrstuvwxyz";

    private static string ToStringLarge(BigInteger v, int radix, Vm? vm)
    {
        bool neg = v.Sign < 0;
        var m = BigInteger.Abs(v);
        // leaf chunk: the largest power of the radix below 2^62, so a leaf fits in a long
        int chunkDigits = 0;
        long chunk = 1;
        while (chunk <= long.MaxValue / radix / 2) { chunk *= radix; chunkDigits++; }
        // powers[i] = chunk^(2^i), with digit counts, until a power exceeds sqrt(m)
        var powers = new System.Collections.Generic.List<BigInteger> { chunk };
        var powerDigits = new System.Collections.Generic.List<int> { chunkDigits };
        long bits = (long)m.GetBitLength();
        while ((long)powers[^1].GetBitLength() * 2 <= bits + 64)
        {
            vm?.CheckTimeLimits();
            powers.Add(powers[^1] * powers[^1]);
            powerDigits.Add(powerDigits[^1] * 2);
        }
        var sb = new System.Text.StringBuilder();
        if (neg) sb.Append('-');
        Convert(m, powers.Count - 1, false);
        return sb.ToString();

        void Convert(BigInteger n, int level, bool pad)
        {
            if (level < 0)
            {
                long leaf = (long)n;
                Span<char> buf = stackalloc char[64];
                int len = 0;
                do { buf[len++] = DigitChars[(int)(leaf % radix)]; leaf /= radix; } while (leaf != 0);
                if (pad) for (int i = len; i < chunkDigits; i++) sb.Append('0');
                for (int i = len - 1; i >= 0; i--) sb.Append(buf[i]);
                return;
            }
            if (!pad && n < powers[level]) { Convert(n, level - 1, false); return; }
            if (n.GetBitLength() > 20_000) vm?.CheckTimeLimits();
            var hi = BigInteger.DivRem(n, powers[level], out var lo);
            Convert(hi, level - 1, pad);
            Convert(lo, level - 1, true);
        }
    }

    /// <summary>Digits → number, divide and conquer for long inputs (see <see cref="ToString(BigInteger, int, Vm?)"/>).</summary>
    private static BigInteger ParseLarge(ReadOnlySpan<char> t, int radix, Vm? vm)
    {
        if (t.Length <= 1000)
        {
            if (radix == 10) return BigInteger.Parse(t, NumberStyles.None, CultureInfo.InvariantCulture);
            var r = BigInteger.Zero;
            foreach (var c in t) r = r * radix + Digit(c);
            return r;
        }
        int lowLen = t.Length / 2;
        var high = ParseLarge(t[..^lowLen], radix, vm);
        var low = ParseLarge(t[^lowLen..], radix, vm);
        vm?.CheckTimeLimits();
        return high * BigInteger.Pow(radix, lowLen) + low;
    }

    private static int Digit(char c) => c >= '0' && c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

    public static BigInteger AsIntN(int bits, BigInteger v)
    {
        if (bits == 0) return BigInteger.Zero;
        var mod = BigInteger.One << bits;
        var r = BigInteger.Remainder(v, mod);
        if (r.Sign < 0) r += mod;
        return r >= (mod >> 1) ? r - mod : r;
    }

    public static BigInteger AsUintN(int bits, BigInteger v)
    {
        if (bits == 0) return BigInteger.Zero;
        var mod = BigInteger.One << bits;
        var r = BigInteger.Remainder(v, mod);
        if (r.Sign < 0) r += mod;
        return r;
    }
}
