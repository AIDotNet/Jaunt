using System;
using System.Globalization;
using System.Text;

namespace Jaunt.Runtime;

/// <summary>ECMAScript Number::toString and string→number conversions (culture-invariant).</summary>
internal static class NumberFormatting
{
    private static readonly string[] SmallInts = Build();
    private static string[] Build()
    {
        var a = new string[256];
        for (int i = 0; i < a.Length; i++) a[i] = i.ToString(CultureInfo.InvariantCulture);
        return a;
    }

    public static string ToString(double d)
    {
        if (d >= 0 && d < 256 && d == (int)d)
        {
            if (d == 0) return "0";
            return SmallInts[(int)d];
        }
        if (double.IsNaN(d)) return "NaN";
        if (d == 0) return "0";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
            return ((long)d).ToString(CultureInfo.InvariantCulture);
        double abs = Math.Abs(d);
        // in this range .NET's shortest round-trip formatting uses plain decimal notation, identical to JS
        if (abs >= 1e-4 && abs < 1e15) return d.ToString("R", CultureInfo.InvariantCulture);
        return FormatShortest(d);
    }

    private static string FormatShortest(double d)
    {
        var sb = new StringBuilder();
        if (d < 0) { sb.Append('-'); d = -d; }
        string r = d.ToString("R", CultureInfo.InvariantCulture);
        // decompose into digits and exponent n where value = 0.d1d2...dk × 10^n
        string mantissa = r;
        int exp = 0;
        int ePos = r.IndexOfAny(new[] { 'E', 'e' });
        if (ePos >= 0)
        {
            mantissa = r.Substring(0, ePos);
            exp = int.Parse(r.AsSpan(ePos + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        }
        int dot = mantissa.IndexOf('.');
        string intPart = dot >= 0 ? mantissa.Substring(0, dot) : mantissa;
        string fracPart = dot >= 0 ? mantissa.Substring(dot + 1) : "";
        string digits = (intPart + fracPart).TrimStart('0');
        int leadingZeros = (intPart + fracPart).Length - digits.Length;
        int n = intPart.Length + exp - leadingZeros;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) return "0";
        int k = digits.Length;
        if (k <= n && n <= 21)
        {
            sb.Append(digits).Append('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            sb.Append(digits, 0, n).Append('.').Append(digits, n, k - n);
        }
        else if (-6 < n && n <= 0)
        {
            sb.Append("0.").Append('0', -n).Append(digits);
        }
        else
        {
            sb.Append(digits[0]);
            if (k > 1) sb.Append('.').Append(digits, 1, k - 1);
            int e = n - 1;
            sb.Append('e').Append(e >= 0 ? '+' : '-').Append(Math.Abs(e).ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public static string ToRadixString(double value, int radix)
    {
        if (radix == 10) return ToString(value);
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";
        if (value == 0) return "0";
        bool neg = value < 0;
        if (neg) value = -value;
        double intPart = Math.Floor(value);
        double frac = value - intPart;
        const string chars = "0123456789abcdefghijklmnopqrstuvwxyz";
        // Integer digits are written backwards from `point`, fraction digits (at most 52) forwards from it. A double below
        // 2^53 has at most 53 integer digits in any radix; the largest finite double has 1024 in radix 2.
        Span<char> buf = intPart < 9007199254740992.0 ? stackalloc char[110] : new char[1080];
        int point = buf.Length - 54, start = point, end = point;
        if (intPart == 0) buf[--start] = '0';
        else if (intPart <= uint.MaxValue)
        {
            uint n = (uint)intPart, b = (uint)radix;
            while (n != 0) { buf[--start] = chars[(int)(n % b)]; n /= b; }
        }
        else
        {
            while (intPart >= 1)
            {
                double q = Math.Floor(intPart / radix);
                int digit = (int)(intPart - q * radix);
                buf[--start] = chars[digit];
                intPart = q;
            }
        }
        if (frac > 0)
        {
            buf[end++] = '.';
            int count = 0;
            while (frac > 0 && count < 52)
            {
                frac *= radix;
                int digit = (int)Math.Floor(frac);
                buf[end++] = chars[digit];
                frac -= digit;
                count++;
            }
        }
        if (neg) buf[--start] = '-';
        return new string(buf.Slice(start, end - start));
    }

    private static bool IsWhite(char c) => c is '\u0009' or '\u000B' or '\u000C' or ' ' or '\u00A0' or '\uFEFF' or '\n' or '\r' or '\u2028' or '\u2029'
        || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    public static string TrimJs(string s)
    {
        int a = 0, b = s.Length;
        while (a < b && IsWhite(s[a])) a++;
        while (b > a && IsWhite(s[b - 1])) b--;
        return a == 0 && b == s.Length ? s : s.Substring(a, b - a);
    }

    public static string TrimStartJs(string s) { int a = 0; while (a < s.Length && IsWhite(s[a])) a++; return s.Substring(a); }
    public static string TrimEndJs(string s) { int b = s.Length; while (b > 0 && IsWhite(s[b - 1])) b--; return s.Substring(0, b); }

    /// <summary>StringToNumber (ECMA-262 7.1.4.1.1).</summary>
    public static double StringToNumber(string input)
    {
        string s = TrimJs(input);
        if (s.Length == 0) return 0;
        if (s.Length > 2 && s[0] == '0')
        {
            char p = (char)(s[1] | 0x20);
            int radix = p == 'x' ? 16 : p == 'o' ? 8 : p == 'b' ? 2 : 0;
            if (radix != 0)
            {
                double v = 0;
                for (int i = 2; i < s.Length; i++)
                {
                    int dgt = DigitValue(s[i]);
                    if (dgt < 0 || dgt >= radix) return double.NaN;
                    v = v * radix + dgt;
                }
                return v;
            }
        }
        if (s == "Infinity" || s == "+Infinity") return double.PositiveInfinity;
        if (s == "-Infinity") return double.NegativeInfinity;
        // validate StrDecimalLiteral
        int idx = 0;
        if (s[idx] == '+' || s[idx] == '-') idx++;
        int digitsBefore = 0, digitsAfter = 0;
        while (idx < s.Length && char.IsAsciiDigit(s[idx])) { idx++; digitsBefore++; }
        if (idx < s.Length && s[idx] == '.')
        {
            idx++;
            while (idx < s.Length && char.IsAsciiDigit(s[idx])) { idx++; digitsAfter++; }
        }
        if (digitsBefore == 0 && digitsAfter == 0) return double.NaN;
        if (idx < s.Length && (s[idx] == 'e' || s[idx] == 'E'))
        {
            idx++;
            if (idx < s.Length && (s[idx] == '+' || s[idx] == '-')) idx++;
            int ed = 0;
            while (idx < s.Length && char.IsAsciiDigit(s[idx])) { idx++; ed++; }
            if (ed == 0) return double.NaN;
        }
        if (idx != s.Length) return double.NaN;
        return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    public static int DigitValue(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        c = (char)(c | 0x20);
        if (c >= 'a' && c <= 'z') return c - 'a' + 10;
        return -1;
    }

    public static double ParseFloat(string input)
    {
        string s = TrimStartJs(input);
        int idx = 0;
        if (idx < s.Length && (s[idx] == '+' || s[idx] == '-')) idx++;
        if (string.CompareOrdinal(s, idx, "Infinity", 0, 8) == 0)
            return s.Length > 0 && s[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
        int start = idx;
        int digits = 0;
        while (idx < s.Length && char.IsAsciiDigit(s[idx])) { idx++; digits++; }
        if (idx < s.Length && s[idx] == '.')
        {
            int save = idx;
            idx++;
            int fd = 0;
            while (idx < s.Length && char.IsAsciiDigit(s[idx])) { idx++; fd++; }
            digits += fd;
            if (fd == 0 && digits == 0) idx = save;
        }
        if (digits == 0) return double.NaN;
        if (idx < s.Length && (s[idx] == 'e' || s[idx] == 'E'))
        {
            int save = idx;
            idx++;
            if (idx < s.Length && (s[idx] == '+' || s[idx] == '-')) idx++;
            int ed = 0;
            while (idx < s.Length && char.IsAsciiDigit(s[idx])) { idx++; ed++; }
            if (ed == 0) idx = save;
        }
        var span = s.AsSpan(0, idx);
        if (span.Length > 0 && span[span.Length - 1] == '.') span = span.Slice(0, span.Length - 1);
        return double.Parse(span, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    public static double ParseInt(string input, int radix)
    {
        string s = TrimStartJs(input);
        int idx = 0;
        bool neg = false;
        if (idx < s.Length && (s[idx] == '+' || s[idx] == '-')) { neg = s[idx] == '-'; idx++; }
        bool stripPrefix = true;
        if (radix != 0)
        {
            if (radix < 2 || radix > 36) return double.NaN;
            if (radix != 16) stripPrefix = false;
        }
        else radix = 10;
        if (stripPrefix && idx + 1 < s.Length && s[idx] == '0' && (s[idx + 1] | 0x20) == 'x')
        {
            idx += 2;
            radix = 16;
        }
        int start = idx;
        double v = 0;
        while (idx < s.Length)
        {
            int d = DigitValue(s[idx]);
            if (d < 0 || d >= radix) break;
            v = v * radix + d;
            idx++;
        }
        if (idx == start) return double.NaN;
        if (radix == 10 && idx - start > 15)
            v = double.Parse(s.AsSpan(start, idx - start), NumberStyles.None, CultureInfo.InvariantCulture);
        return neg ? -v : v;
    }
}

/// <summary>
/// Exact decimal rounding of doubles for Number.prototype.toFixed / toExponential / toPrecision: the value is the
/// exact binary fraction m·2^e and ties round to the larger n (21.1.3.3, .2, .5), unlike .NET's formatting.
/// </summary>
internal static class ExactDecimal
{
    private static void Decompose(double x, out System.Numerics.BigInteger m, out int e2)
    {
        long bits = BitConverter.DoubleToInt64Bits(x);
        int exp = (int)((bits >> 52) & 0x7FF);
        long frac = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0) { m = frac; e2 = -1074; }
        else { m = frac | (1L << 52); e2 = exp - 1075; }
    }

    /// <summary>round_half_up(|x| · 10^k) for finite x.</summary>
    public static System.Numerics.BigInteger RoundScaled(double x, int k)
    {
        Decompose(Math.Abs(x), out var m, out int e2);
        System.Numerics.BigInteger num = m, den = System.Numerics.BigInteger.One;
        if (k >= 0) num *= System.Numerics.BigInteger.Pow(10, k); else den *= System.Numerics.BigInteger.Pow(10, -k);
        if (e2 >= 0) num <<= e2; else den <<= -e2;
        return (2 * num + den) / (2 * den);   // floor(num/den + 1/2)
    }

    /// <summary>Digits n (exactly <paramref name="digits"/> of them) and exponent e with n·10^(e-digits+1) ≈ |x| (x ≠ 0).</summary>
    public static (string Digits, int Exponent) Significant(double x, int digits)
    {
        // exact e = floor(log10 |x|): the floating-point estimate can be off by one near powers of ten
        int e = (int)Math.Floor(Math.Log10(Math.Abs(x)));
        while (ComparePow10(x, e) < 0) e--;          // |x| < 10^e
        while (ComparePow10(x, e + 1) >= 0) e++;     // |x| >= 10^(e+1)
        var n = RoundScaled(x, digits - 1 - e);
        string s = n.ToString(CultureInfo.InvariantCulture);
        if (s.Length > digits) { s = s.Substring(0, digits); e++; }   // rounded up to 10^digits
        return (s, e);
    }

    /// <summary>Sign of |x| − 10^k, computed exactly.</summary>
    private static int ComparePow10(double x, int k)
    {
        Decompose(Math.Abs(x), out var m, out int e2);
        System.Numerics.BigInteger lhs = m, rhs = System.Numerics.BigInteger.One;
        if (e2 >= 0) lhs <<= e2; else rhs <<= -e2;
        if (k >= 0) rhs *= System.Numerics.BigInteger.Pow(10, k); else lhs *= System.Numerics.BigInteger.Pow(10, -k);
        return lhs.CompareTo(rhs);
    }

    public static string ToFixed(double x, int f)
    {
        string n = RoundScaled(x, f).ToString(CultureInfo.InvariantCulture);
        if (f > 0)
        {
            if (n.Length <= f) n = new string('0', f - n.Length + 1) + n;
            n = n.Substring(0, n.Length - f) + "." + n.Substring(n.Length - f);
        }
        return (x < 0 && n.Trim('0', '.').Length > 0 ? "-" : "") + n;
    }
}
