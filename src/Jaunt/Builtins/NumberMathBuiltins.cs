using System;
using System.Globalization;

namespace Jaunt.Runtime;

internal static class NumberBuiltins
{
    public static void Setup(Realm r)
    {
        var proto = new JsPrimitiveWrapper(r, r.ObjectPrototype, JsValue.Zero);
        r.NumberPrototype = proto;
        var ctor = r.Constructor("Number", 1, static (in NativeArgs a) =>
        {
            double n = 0;
            if (a.Length > 0)
            {
                var prim = Operations.ToNumericValue(a.Realm, a[0]);
                n = prim.IsBigInt ? BigIntOps.ToDouble(prim.BigIntValue) : prim.Num;
            }
            if (!a.IsConstruct) return new JsValue(n);
            var p = Operations.GetPrototypeFromConstructor(a.Realm, a.NewTarget.AsObjectUnchecked, a.Realm.NumberPrototype);
            return JsValue.FromObject(new JsPrimitiveWrapper(a.Realm, p, new JsValue(n)));
        }, proto);
        r.DefineGlobal("Number", JsValue.FromObject(ctor));
        ctor.AddOwnProperty("MAX_SAFE_INTEGER", new JsValue(9007199254740991.0), PropFlags.None);
        ctor.AddOwnProperty("MIN_SAFE_INTEGER", new JsValue(-9007199254740991.0), PropFlags.None);
        ctor.AddOwnProperty("MAX_VALUE", new JsValue(double.MaxValue), PropFlags.None);
        ctor.AddOwnProperty("MIN_VALUE", new JsValue(double.Epsilon), PropFlags.None);
        ctor.AddOwnProperty("EPSILON", new JsValue(Math.Pow(2, -52)), PropFlags.None);
        ctor.AddOwnProperty("NaN", JsValue.NaN, PropFlags.None);
        ctor.AddOwnProperty("POSITIVE_INFINITY", new JsValue(double.PositiveInfinity), PropFlags.None);
        ctor.AddOwnProperty("NEGATIVE_INFINITY", new JsValue(double.NegativeInfinity), PropFlags.None);
        r.Method(ctor, "isInteger", 1, static (in NativeArgs a) => JsValue.FromBool(a[0].IsNumber && double.IsFinite(a[0].Num) && Math.Truncate(a[0].Num) == a[0].Num));
        r.Method(ctor, "isSafeInteger", 1, static (in NativeArgs a) => JsValue.FromBool(a[0].IsNumber && double.IsFinite(a[0].Num) && Math.Truncate(a[0].Num) == a[0].Num && Math.Abs(a[0].Num) <= 9007199254740991.0));
        r.Method(ctor, "isFinite", 1, static (in NativeArgs a) => JsValue.FromBool(a[0].IsNumber && double.IsFinite(a[0].Num)));
        r.Method(ctor, "isNaN", 1, static (in NativeArgs a) => JsValue.FromBool(a[0].IsNumber && double.IsNaN(a[0].Num)));
        // Number.parseFloat / parseInt are the same function objects as the globals
        r.Ensure(IntrinsicGroup.Globals);
        ctor.DefineHidden("parseFloat", r.GlobalObject.Get("parseFloat"));
        ctor.DefineHidden("parseInt", r.GlobalObject.Get("parseInt"));
        r.Method(proto, "toString", 1, static (in NativeArgs a) =>
        {
            double x = ThisNumber(a);
            int radix = a[0].IsUndefined ? 10 : (int)Operations.ToIntegerOrInfinity(a.Realm, a[0]);
            if (radix < 2 || radix > 36) throw JsErrors.RangeError(a.Realm, "toString() radix must be between 2 and 36");
            return new JsValue(NumberFormatting.ToRadixString(x, radix));
        });
        r.Method(proto, "toLocaleString", 0, static (in NativeArgs a) =>
        {
            double x = ThisNumber(a);
            // ECMA-402 20.2.1: format with a new %Intl.NumberFormat%(locales, options)
            if (a.Realm.Intl != null) return new JsValue(IntlNumberFormat.CreateRecord(a.Realm, a[0], a[1]).Format(x));
            return new JsValue(x.ToString("#,##0.###", CultureInfo.InvariantCulture));
        });
        r.Method(proto, "valueOf", 0, static (in NativeArgs a) => new JsValue(ThisNumber(a)));
        r.Method(proto, "toFixed", 1, static (in NativeArgs a) =>
        {
            double x = ThisNumber(a);
            double f = Operations.ToIntegerOrInfinity(a.Realm, a[0]);
            if (f < 0 || f > 100 || double.IsInfinity(f)) throw JsErrors.RangeError(a.Realm, "toFixed() digits argument must be between 0 and 100");
            if (double.IsNaN(x)) return new JsValue("NaN");
            if (Math.Abs(x) >= 1e21) return new JsValue(NumberFormatting.ToString(x));
            return new JsValue(ExactDecimal.ToFixed(x, (int)f));
        });
        r.Method(proto, "toPrecision", 1, static (in NativeArgs a) =>
        {
            double x = ThisNumber(a);
            if (a[0].IsUndefined) return new JsValue(NumberFormatting.ToString(x));
            double p = Operations.ToIntegerOrInfinity(a.Realm, a[0]);
            if (!double.IsFinite(x)) return new JsValue(NumberFormatting.ToString(x));
            if (p < 1 || p > 100) throw JsErrors.RangeError(a.Realm, "toPrecision() argument must be between 1 and 100");
            if (x == 0) return new JsValue(p == 1 ? "0" : "0." + new string('0', (int)p - 1));
            var (digits, e) = ExactDecimal.Significant(x, (int)p);
            string sign = x < 0 ? "-" : "";
            if (e < -6 || e >= p)
            {
                string m = digits.Length > 1 ? digits[0] + "." + digits.Substring(1) : digits;
                return new JsValue(sign + m + "e" + (e >= 0 ? "+" : "-") + Math.Abs(e));
            }
            if (e >= 0)
            {
                string ip = digits.Substring(0, e + 1);
                string fp = digits.Substring(e + 1);
                return new JsValue(sign + ip + (fp.Length > 0 ? "." + fp : ""));
            }
            return new JsValue(sign + "0." + new string('0', -e - 1) + digits);
        });
        r.Method(proto, "toExponential", 1, static (in NativeArgs a) =>
        {
            double x = ThisNumber(a);
            double f = Operations.ToIntegerOrInfinity(a.Realm, a[0]);
            if (!double.IsFinite(x)) return new JsValue(NumberFormatting.ToString(x));
            if (f < 0 || f > 100) throw JsErrors.RangeError(a.Realm, "toExponential() argument must be between 0 and 100");
            string s;
            if (a[0].IsUndefined)
            {
                var shortest = NumberFormatting.ToString(Math.Abs(x));
                s = Math.Abs(x).ToString("E16", CultureInfo.InvariantCulture);
                // use the shortest representation's digit count
                var digits = shortest.Replace(".", "").TrimStart('0');
                int ePos0 = digits.IndexOf('e');
                if (ePos0 >= 0) digits = digits.Substring(0, ePos0);
                digits = digits.TrimEnd('0');
                s = Math.Abs(x).ToString("E" + Math.Max(0, digits.Length - 1), CultureInfo.InvariantCulture);
            }
            else if (x == 0) s = (f > 0 ? "0." + new string('0', (int)f) : "0") + "E+0";
            else
            {
                var (dg, ex) = ExactDecimal.Significant(x, (int)f + 1);
                s = (dg.Length > 1 ? dg[0] + "." + dg.Substring(1) : dg) + "E" + ex.ToString(CultureInfo.InvariantCulture);
            }
            int ePos = s.IndexOf('E');
            int exp = int.Parse(s.AsSpan(ePos + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            return new JsValue((x < 0 ? "-" : "") + s.Substring(0, ePos) + "e" + (exp >= 0 ? "+" : "-") + Math.Abs(exp));
        });

        // Boolean
        var bproto = new JsPrimitiveWrapper(r, r.ObjectPrototype, JsValue.False);
        r.BooleanPrototype = bproto;
        var bctor = r.Constructor("Boolean", 1, static (in NativeArgs a) =>
        {
            bool b = Operations.ToBoolean(a[0]);
            if (!a.IsConstruct) return JsValue.FromBool(b);
            var p = Operations.GetPrototypeFromConstructor(a.Realm, a.NewTarget.AsObjectUnchecked, a.Realm.BooleanPrototype);
            return JsValue.FromObject(new JsPrimitiveWrapper(a.Realm, p, JsValue.FromBool(b)));
        }, bproto);
        r.DefineGlobal("Boolean", JsValue.FromObject(bctor));
        r.Method(bproto, "toString", 0, static (in NativeArgs a) => new JsValue(ThisBool(a) ? "true" : "false"));
        r.Method(bproto, "valueOf", 0, static (in NativeArgs a) => JsValue.FromBool(ThisBool(a)));
    }

    public static void LinkParseFunctions(Realm r) { }

    private static double ThisNumber(in NativeArgs a)
    {
        if (a.This.IsNumber) return a.This.Num;
        if (a.This.Ref is JsPrimitiveWrapper w && w.Value.IsNumber) return w.Value.Num;
        throw JsErrors.TypeError(a.Realm, "Number.prototype method requires that 'this' be a Number");
    }

    private static bool ThisBool(in NativeArgs a)
    {
        if (a.This.IsBoolean) return a.This.IsTrue;
        if (a.This.Ref is JsPrimitiveWrapper w && w.Value.IsBoolean) return w.Value.IsTrue;
        throw JsErrors.TypeError(a.Realm, "Boolean.prototype method requires that 'this' be a Boolean");
    }
}

internal static class MathBuiltins
{
    [ThreadStatic] private static Random? _random;

    public static double Pow(double a, double b)
    {
        if (double.IsNaN(b)) return double.NaN;
        if (b == 0) return 1;
        if ((a == 1 || a == -1) && double.IsInfinity(b)) return double.NaN;
        return Math.Pow(a, b);
    }

    public static void Setup(Realm r)
    {
        var m = new JsObject(r, r.ObjectPrototype);
        r.DefineGlobal("Math", JsValue.FromObject(m));
        m.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("Math"), PropFlags.Configurable);
        foreach (var (n, v) in new[] { ("E", Math.E), ("LN10", Math.Log(10)), ("LN2", Math.Log(2)), ("LOG10E", Math.Log10(Math.E)), ("LOG2E", Math.Log2(Math.E)), ("PI", Math.PI), ("SQRT1_2", Math.Sqrt(0.5)), ("SQRT2", Math.Sqrt(2)) })
            m.AddOwnProperty(n, new JsValue(v), PropFlags.None);

        void F1(string name, Func<double, double> f)
        {
            var fn = r.MethodEager(m, name, 1, static (in NativeArgs a) =>
            {
                var func = (Func<double, double>)a.Callee.State!;
                var x = a[0];
                return new JsValue(func(x.IsNumber ? x.Num : Operations.ToNumber(a.Realm, x)));
            });
            fn.State = f;
        }
        F1("abs", Math.Abs);
        F1("acos", Math.Acos); F1("acosh", Math.Acosh); F1("asin", Math.Asin); F1("asinh", Math.Asinh);
        F1("atan", Math.Atan); F1("atanh", Math.Atanh); F1("cbrt", Math.Cbrt); F1("ceil", Math.Ceiling);
        F1("cos", Math.Cos); F1("cosh", Math.Cosh); F1("exp", Math.Exp); F1("expm1", x => x == 0 ? x : Math.Abs(x) < 1e-5 ? x + x * x / 2 + x * x * x / 6 : Math.Exp(x) - 1);
        F1("floor", Math.Floor); F1("fround", x => (float)x); F1("log", Math.Log); F1("log1p", x => Math.Abs(x) < 1e-5 ? x - x * x / 2 + x * x * x / 3 : Math.Log(1 + x));
        F1("log10", Math.Log10); F1("log2", Math.Log2); F1("sign", x => double.IsNaN(x) ? double.NaN : x > 0 ? 1 : x < 0 ? -1 : x);
        F1("sin", Math.Sin); F1("sinh", Math.Sinh); F1("sqrt", Math.Sqrt); F1("tan", Math.Tan); F1("tanh", Math.Tanh);
        F1("trunc", Math.Truncate);
        F1("round", x =>
        {
            if (double.IsNaN(x) || double.IsInfinity(x)) return x;
            if (x == 0) return x;
            if (x > 0 && x < 0.5) return 0;
            if (x < 0 && x >= -0.5) return -0.0;
            double fl = Math.Floor(x);
            if (fl == x) return x;   // already integral (incl. values ≥ 2^52 where x + 0.5 is inexact)
            return x - fl >= 0.5 ? fl + 1 : fl;
        });
        F1("clz32", x => { uint u = (uint)Operations.DoubleToInt32(x); return u == 0 ? 32 : System.Numerics.BitOperations.LeadingZeroCount(u); });
        r.Method(m, "atan2", 2, static (in NativeArgs a) => new JsValue(Math.Atan2(Operations.ToNumber(a.Realm, a[0]), Operations.ToNumber(a.Realm, a[1]))));
        r.Method(m, "pow", 2, static (in NativeArgs a) => new JsValue(Pow(Operations.ToNumber(a.Realm, a[0]), Operations.ToNumber(a.Realm, a[1]))));
        r.Method(m, "imul", 2, static (in NativeArgs a) => new JsValue(Operations.ToInt32(a.Realm, a[0]) * Operations.ToInt32(a.Realm, a[1])));
        r.Method(m, "max", 2, static (in NativeArgs a) =>
        {
            double best = double.NegativeInfinity;
            bool nan = false;
            foreach (var v in a.Args)
            {
                double d = v.IsNumber ? v.Num : Operations.ToNumber(a.Realm, v);
                if (double.IsNaN(d)) nan = true;
                else if (d > best || (d == 0 && best == 0 && !double.IsNegative(d))) best = d;
            }
            return new JsValue(nan ? double.NaN : best);
        });
        r.Method(m, "min", 2, static (in NativeArgs a) =>
        {
            double best = double.PositiveInfinity;
            bool nan = false;
            foreach (var v in a.Args)
            {
                double d = v.IsNumber ? v.Num : Operations.ToNumber(a.Realm, v);
                if (double.IsNaN(d)) nan = true;
                else if (d < best || (d == 0 && best == 0 && double.IsNegative(d))) best = d;
            }
            return new JsValue(nan ? double.NaN : best);
        });
        Es2025Builtins.InstallMath(r, m);
        r.Method(m, "hypot", 2, static (in NativeArgs a) =>
        {
            double sum = 0;
            bool inf = false, nan = false;
            foreach (var v in a.Args)
            {
                double d = Operations.ToNumber(a.Realm, v);
                if (double.IsInfinity(d)) inf = true;
                if (double.IsNaN(d)) nan = true;
                sum += d * d;
            }
            return new JsValue(inf ? double.PositiveInfinity : nan ? double.NaN : Math.Sqrt(sum));
        });
        r.Method(m, "random", 0, static (in NativeArgs a) => new JsValue((_random ??= new Random()).NextDouble()));
    }
}
