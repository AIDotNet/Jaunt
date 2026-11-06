using System;
using System.Numerics;

namespace Jaunt.Runtime;

/// <summary>BigInt constructor and prototype (ECMA-262 21.2).</summary>
internal static class BigIntBuiltins
{
    public static void Setup(Realm r)
    {
        var proto = new JsObject(r, r.ObjectPrototype);
        r.BigIntPrototype = proto;
        var ctor = r.Constructor("BigInt", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a.IsConstruct) throw JsErrors.TypeError(realm, "BigInt is not a constructor");
            var prim = Operations.ToPrimitive(realm, a[0], Operations.PreferredType.Number);
            if (prim.IsNumber) return JsValue.FromBigInteger(BigIntOps.NumberToBigInt(realm, prim.Num));
            return JsValue.FromBigInteger(BigIntOps.ToBigInt(realm, prim));
        }, proto);
        r.DefineGlobal("BigInt", JsValue.FromObject(ctor));
        r.Method(ctor, "asIntN", 2, static (in NativeArgs a) =>
        {
            int bits = ToIndexBits(a.Realm, a[0]);
            var v = BigIntOps.ToBigInt(a.Realm, a[1]);
            return JsValue.FromBigInteger(BigIntOps.AsIntN(bits, v));
        });
        r.Method(ctor, "asUintN", 2, static (in NativeArgs a) =>
        {
            int bits = ToIndexBits(a.Realm, a[0]);
            var v = BigIntOps.ToBigInt(a.Realm, a[1]);
            if (bits > BigIntOps.MaxBits && v.Sign < 0) throw JsErrors.RangeError(a.Realm, "Maximum BigInt size exceeded");
            return JsValue.FromBigInteger(BigIntOps.AsUintN(bits, v));
        });
        r.Method(proto, "toString", 0, static (in NativeArgs a) =>
        {
            var x = ThisBigInt(a, "BigInt.prototype.toString");
            int radix = 10;
            if (!a[0].IsUndefined)
            {
                double rd = Operations.ToIntegerOrInfinity(a.Realm, a[0]);
                if (rd < 2 || rd > 36) throw JsErrors.RangeError(a.Realm, "toString() radix must be between 2 and 36");
                radix = (int)rd;
            }
            return new JsValue(BigIntOps.ToString(x, radix));
        });
        r.Method(proto, "toLocaleString", 0, static (in NativeArgs a) =>
        {
            var x = ThisBigInt(a, "BigInt.prototype.toLocaleString");
            // ECMA-402 20.3.1: format the exact value with a new %Intl.NumberFormat%(locales, options)
            if (a.Realm.Intl != null)
                return new JsValue(IntlNumberFormat.CreateRecord(a.Realm, a[0], a[1]).Format(new IntlMathValue(BigIntOps.ToString(x, 10))));
            return new JsValue(BigIntOps.ToString(x, 10));
        });
        r.Method(proto, "valueOf", 0, static (in NativeArgs a) => JsValue.FromBigInteger(ThisBigInt(a, "BigInt.prototype.valueOf")));
        proto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("BigInt"), PropFlags.Configurable);
    }

    private static int ToIndexBits(Realm realm, in JsValue v)
    {
        double d = Operations.ToIntegerOrInfinity(realm, v);
        if (d < 0 || d > 9007199254740991) throw JsErrors.RangeError(realm, "Invalid value: not (convertible to) a safe integer");
        if (d > int.MaxValue) d = int.MaxValue;
        return (int)d;
    }

    private static BigInteger ThisBigInt(in NativeArgs a, string method)
    {
        var t = a.This;
        if (t.IsBigInt) return t.BigIntValue;
        if (t.IsObject && t.Ref is JsPrimitiveWrapper w && w.Value.IsBigInt) return w.Value.BigIntValue;
        throw JsErrors.TypeError(a.Realm, $"{method} requires that 'this' be a BigInt");
    }
}
