using System;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Jaunt.Runtime;

/// <summary>Smaller ES2024–ES2026 additions: Promise.try, RegExp.escape, Error.isError, Math.sumPrecise, Math.f16round, String toWellFormed.</summary>
internal static class Es2025Builtins
{
    public static void InstallString(Realm r, JsObject proto)
    {
        r.Method(proto, "toWellFormed", 0, static (in NativeArgs a) =>
        {
            if (a.This.IsNullOrUndefined) throw JsErrors.TypeError(a.Realm, "String.prototype.toWellFormed called on null or undefined");
            var s = Operations.ToString(a.Realm, a.This);
            StringBuilder? sb = null;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool lone = char.IsHighSurrogate(c) ? !(i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) : char.IsLowSurrogate(c);
                if (char.IsHighSurrogate(c) && !lone) { sb?.Append(c).Append(s[i + 1]); i++; continue; }
                if (lone)
                {
                    sb ??= new StringBuilder(s, 0, i, s.Length);
                    sb.Append('�');
                }
                else sb?.Append(c);
            }
            return new JsValue(sb?.ToString() ?? s);
        });
    }

    public static void InstallPromise(Realm r, JsObject ctor)
    {
        r.Method(ctor, "try", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a.This.IsNotObject) throw JsErrors.TypeError(realm, "Promise.try called on non-object");
            var cap = PromiseBuiltins.NewCapability(realm, a.This);
            var rest = a.Length > 1 ? a.Args.Slice(1).ToArray() : Array.Empty<JsValue>();
            JsValue result;
            try { result = realm.Vm.Call(a[0], JsValue.Undefined, rest); }
            catch (JsThrowException ex)
            {
                cap.DoReject(realm, ex.Value);
                return JsValue.FromObject(cap.Promise);
            }
            // a promise created by the same constructor is returned as is (no extra wrapping)
            if (result.IsObject && result.Ref is JsPromise && ReferenceEquals(result.AsObjectUnchecked.Get("constructor").Ref, a.This.Ref)) return result;
            cap.DoResolve(realm, result);
            return JsValue.FromObject(cap.Promise);
        });
    }

    public static void InstallError(Realm r, JsObject ctor)
    {
        r.Method(ctor, "isError", 1, static (in NativeArgs a) => JsValue.FromBool(a[0].IsObject && a[0].Ref is JsError));
    }

    // ------------------------------------------------------------------ RegExp.escape (22.2.5.1)

    public static void InstallRegExp(Realm r, JsObject ctor)
    {
        r.Method(ctor, "escape", 1, static (in NativeArgs a) =>
        {
            if (!a[0].IsString) throw JsErrors.TypeError(a.Realm, "RegExp.escape requires a string");
            var s = a[0].AsStringUnchecked;
            var sb = new StringBuilder(s.Length * 2);
            for (int i = 0; i < s.Length; i++)
            {
                int cp = s[i];
                bool lone = false;
                if (char.IsHighSurrogate(s[i]))
                {
                    if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { cp = char.ConvertToUtf32(s[i], s[i + 1]); i++; }
                    else lone = true;
                }
                else if (char.IsLowSurrogate(s[i])) lone = true;
                if (sb.Length == 0 && cp < 128 && (char.IsAsciiDigit((char)cp) || char.IsAsciiLetter((char)cp)))
                {
                    sb.Append("\\x").Append(cp.ToString("x2", CultureInfo.InvariantCulture));
                    continue;
                }
                EncodeForEscape(sb, cp, lone);
            }
            return new JsValue(sb.ToString());
        });
    }

    private static void EncodeForEscape(StringBuilder sb, int c, bool lone)
    {
        if ("^$\\.*+?()[]{}|/".IndexOf((char)c) >= 0 && c < 128) { sb.Append('\\').Append((char)c); return; }
        switch (c)
        {
            case 0x9: sb.Append("\\t"); return;
            case 0xA: sb.Append("\\n"); return;
            case 0xB: sb.Append("\\v"); return;
            case 0xC: sb.Append("\\f"); return;
            case 0xD: sb.Append("\\r"); return;
        }
        bool punct = c < 128 && ",-=<>#&!%:;@~'`\"".IndexOf((char)c) >= 0;
        bool white = c is 0x20 or 0xA0 or 0xFEFF or 0x2028 or 0x2029
            || (c <= 0xFFFF && !lone && CharUnicodeInfo.GetUnicodeCategory((char)c) == UnicodeCategory.SpaceSeparator);
        if (punct || white || lone)
        {
            if (c <= 0xFF) { sb.Append("\\x").Append(c.ToString("x2", CultureInfo.InvariantCulture)); return; }
            if (c > 0xFFFF)
            {
                var str = char.ConvertFromUtf32(c);
                foreach (var cu in str) sb.Append("\\u").Append(((int)cu).ToString("x4", CultureInfo.InvariantCulture));
                return;
            }
            sb.Append("\\u").Append(c.ToString("x4", CultureInfo.InvariantCulture));
            return;
        }
        if (c > 0xFFFF) sb.Append(char.ConvertFromUtf32(c));
        else sb.Append((char)c);
    }

    // ------------------------------------------------------------------ Math

    public static void InstallMath(Realm r, JsObject math)
    {
        r.Method(math, "f16round", 1, static (in NativeArgs a) =>
        {
            double x = Operations.ToNumber(a.Realm, a[0]);
            return JsValue.FromExternalDouble((double)(Half)x);
        });
        r.Method(math, "sumPrecise", 1, static (in NativeArgs a) => SumPrecise(a.Realm, a[0]));
    }

    /// <summary>Math.sumPrecise: exact sum of all finite values, rounded once (scaled-integer arithmetic).</summary>
    private static JsValue SumPrecise(Realm realm, JsValue items)
    {
        if (items.IsNullOrUndefined) throw JsErrors.TypeError(realm, "Math.sumPrecise requires an iterable");
        var it = IteratorRecord.Get(realm, items);
        const int MinusZero = 0, Finite = 1, PlusInf = 2, MinusInf = 3, NotANumber = 4;
        int state = MinusZero;
        var sum = BigInteger.Zero;
        long count = 0;
        while (it.Step(out var v))
        {
            if (++count > 9007199254740991) { it.CloseQuiet(); throw JsErrors.RangeError(realm, "Too many values"); }
            if (!v.IsNumber) { it.CloseQuiet(); throw JsErrors.TypeError(realm, "Math.sumPrecise: value is not a number"); }
            realm.Vm.Tick();
            if (state == NotANumber) continue;
            double d = v.Num;
            if (double.IsNaN(d)) { state = NotANumber; continue; }
            if (double.IsPositiveInfinity(d)) { state = state == MinusInf ? NotANumber : PlusInf; continue; }
            if (double.IsNegativeInfinity(d)) { state = state == PlusInf ? NotANumber : MinusInf; continue; }
            if (state is PlusInf or MinusInf) continue;
            if (d == 0 && double.IsNegative(d)) continue;
            state = Finite;
            sum += Scaled(d);
        }
        return state switch
        {
            NotANumber => JsValue.NaN,
            PlusInf => new JsValue(double.PositiveInfinity),
            MinusInf => new JsValue(double.NegativeInfinity),
            MinusZero => new JsValue(-0.0),
            _ => new JsValue(Unscale(sum)),
        };
    }

    /// <summary>d * 2^1074 as an exact integer.</summary>
    private static BigInteger Scaled(double d)
    {
        long bits = BitConverter.DoubleToInt64Bits(d);
        bool neg = bits < 0;
        int exp = (int)((bits >> 52) & 0x7FF);
        long mant = bits & 0xF_FFFF_FFFF_FFFFL;
        if (exp == 0) exp = 1; else mant |= 1L << 52;
        var v = new BigInteger(mant) << (exp - 1);
        return neg ? -v : v;
    }

    /// <summary>Rounds sum * 2^-1074 to the nearest double (ties to even).</summary>
    private static double Unscale(BigInteger s)
    {
        if (s.IsZero) return 0.0;
        bool neg = s.Sign < 0;
        var m = BigInteger.Abs(s);
        long bitLen = (long)m.GetBitLength();
        double result;
        if (bitLen <= 53) result = Math.ScaleB((double)(long)m, -1074);
        else
        {
            int shift = (int)(bitLen - 53);
            var mant = m >> shift;
            var rem = m - (mant << shift);
            var half = BigInteger.One << (shift - 1);
            int cmp = rem.CompareTo(half);
            if (cmp > 0 || (cmp == 0 && !mant.IsEven)) mant += 1;
            if (mant.GetBitLength() > 53) { mant >>= 1; shift++; }
            result = Math.ScaleB((double)(long)mant, shift - 1074);
        }
        return neg ? -result : result;
    }
}
