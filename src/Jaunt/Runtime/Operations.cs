using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Jaunt.Runtime;

/// <summary>Abstract operations (ECMA-262 section 7).</summary>
internal static class Operations
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ToBoolean(in JsValue v)
    {
        if (v.IsNumber) return Math.Abs(v.Num) > 0;   // false for 0, -0 and NaN
        switch (v.Tag)
        {
            case JsTag.Boolean: return v.IsTrue;
            case JsTag.String: return v.StringLength != 0;
            case JsTag.Object: return v.Ref is not JsHtmlDda;
            case JsTag.Symbol: return true;
            case JsTag.BigInt: return !v.BigIntValue.IsZero;
            default: return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToNumber(Realm realm, in JsValue v)
    {
        if (v.IsNumber) return v.Num;
        return ToNumberSlow(realm, v);
    }

    private static double ToNumberSlow(Realm realm, JsValue v)
    {
        switch (v.Tag)
        {
            case JsTag.Undefined: return double.NaN;
            case JsTag.Null: return 0;
            case JsTag.Boolean: return v.IsTrue ? 1 : 0;
            case JsTag.String: return NumberFormatting.StringToNumber(v.AsStringUnchecked);
            case JsTag.Symbol: throw JsErrors.TypeError(realm, "Cannot convert a Symbol value to a number");
            case JsTag.BigInt: throw JsErrors.TypeError(realm, "Cannot convert a BigInt value to a number");
            case JsTag.Object: return ToNumber(realm, ToPrimitive(realm, v, PreferredType.Number));
            default: return double.NaN;
        }
    }

    public static double ToNumeric(Realm realm, in JsValue v) => ToNumber(realm, v);

    /// <summary>ToNumeric (7.1.3): a number or a BigInt value.</summary>
    public static JsValue ToNumericValue(Realm realm, in JsValue v)
    {
        if (v.IsNumber || v.IsBigInt) return v;
        var p = ToPrimitive(realm, v, PreferredType.Number);
        if (p.IsBigInt) return p;
        return new JsValue(ToNumber(realm, p));
    }

    public static string ToString(Realm realm, in JsValue v)
    {
        switch (v.Tag)
        {
            case JsTag.String: return v.AsStringUnchecked;
            case JsTag.Number: return NumberFormatting.ToString(v.Num);
            case JsTag.Undefined: return "undefined";
            case JsTag.Null: return "null";
            case JsTag.Boolean: return v.IsTrue ? "true" : "false";
            case JsTag.Symbol: throw JsErrors.TypeError(realm, "Cannot convert a Symbol value to a string");
            case JsTag.BigInt: return BigIntOps.ToString(v.BigIntValue, 10, realm.Vm);
            case JsTag.Object: return ToString(realm, ToPrimitive(realm, v, PreferredType.String));
            default: return "";
        }
    }

    public static PropertyKey ToPropertyKey(Realm realm, in JsValue v)
    {
        if (v.IsString) return new PropertyKey(v.AsStringUnchecked);
        if (v.IsSymbol) return new PropertyKey((JsSymbol)v.Ref!);
        if (v.IsNumber)
        {
            double d = v.Num;
            if (d >= 0 && d < uint.MaxValue && d == (uint)d) return PropertyKey.FromIndex((uint)d);
            return new PropertyKey(NumberFormatting.ToString(d));
        }
        var p = ToPrimitive(realm, v, PreferredType.String);
        if (p.IsSymbol) return new PropertyKey((JsSymbol)p.Ref!);
        return new PropertyKey(ToString(realm, p));
    }

    /// <summary>
    /// Converts to a property-key value: a string or symbol, or a number that is a valid array index (kept numeric so
    /// the element fast paths of GetElem/SetElem still apply; every consumer converts it the same way ToPropertyKey
    /// would, and a number has no observable conversion side effects).
    /// </summary>
    public static JsValue ToPropertyKeyValue(Realm realm, in JsValue v)
    {
        if (v.Tag is JsTag.String or JsTag.Symbol) return v;
        if (v.IsNumber && IsArrayIndexNumber(v.Num)) return v;
        return ToPropertyKey(realm, v).ToJsValue();
    }

    /// <summary>Whether <paramref name="d"/> is an integer in [0, 2^32 - 2] (an array index as a double).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsArrayIndexNumber(double d) { uint u = (uint)d; return u == d && u != uint.MaxValue; }

    public enum PreferredType { Default, Number, String }

    public static JsValue ToPrimitive(Realm realm, in JsValue v, PreferredType hint = PreferredType.Default)
    {
        if (v.IsNotObject) return v;
        // object → primitive can recurse through builtins on the native stack (e.g. join of a 5,000-deep nested array:
        // join → ToString → toString → join …), so it is a native-stack guard point
        realm.Vm.EnsureNativeStack();
        var obj = v.AsObjectUnchecked;
        var exotic = obj.Get(JsSymbol.ToPrimitive, v);
        if (!exotic.IsNullOrUndefined)
        {
            if (!exotic.IsFunction) throw JsErrors.TypeError(realm, "Symbol.toPrimitive is not a function");
            var hintStr = new JsValue(hint switch { PreferredType.Number => "number", PreferredType.String => "string", _ => "default" });
            var r = realm.Vm.Call(exotic, v, new ReadOnlySpan<JsValue>(in hintStr));
            if (r.IsObject) throw JsErrors.TypeError(realm, "Cannot convert object to primitive value");
            return r;
        }
        return OrdinaryToPrimitive(realm, obj, hint == PreferredType.String ? PreferredType.String : PreferredType.Number);
    }

    public static JsValue OrdinaryToPrimitive(Realm realm, JsObject obj, PreferredType hint)
    {
        var self = JsValue.FromObject(obj);
        string first = hint == PreferredType.String ? "toString" : "valueOf";
        string second = hint == PreferredType.String ? "valueOf" : "toString";
        var f = obj.Get(first, self);
        if (f.IsFunction)
        {
            var r = realm.Vm.Call(f, self, ReadOnlySpan<JsValue>.Empty);
            if (r.IsNotObject) return r;
        }
        f = obj.Get(second, self);
        if (f.IsFunction)
        {
            var r = realm.Vm.Call(f, self, ReadOnlySpan<JsValue>.Empty);
            if (r.IsNotObject) return r;
        }
        throw JsErrors.TypeError(realm, "Cannot convert object to primitive value");
    }

    public static JsObject ToObject(Realm realm, in JsValue v)
    {
        switch (v.Tag)
        {
            case JsTag.Object: return v.AsObjectUnchecked;
            case JsTag.Undefined:
            case JsTag.Null: throw JsErrors.TypeError(realm, $"Cannot convert {v} to object");
            case JsTag.Boolean: return new JsPrimitiveWrapper(realm, realm.BooleanPrototype, v);
            case JsTag.Number: return new JsPrimitiveWrapper(realm, realm.NumberPrototype, v);
            case JsTag.String: return new JsStringObject(realm, v.AsStringUnchecked);
            case JsTag.Symbol: return new JsPrimitiveWrapper(realm, realm.SymbolPrototype, v);
            case JsTag.BigInt: return new JsPrimitiveWrapper(realm, realm.BigIntPrototype, v);
            default: throw JsErrors.TypeError(realm, "Cannot convert value to object");
        }
    }

    public static int ToInt32(Realm realm, in JsValue v)
    {
        if (v.IsNumber) return DoubleToInt32(v.Num);
        return DoubleToInt32(ToNumber(realm, v));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DoubleToInt32(double d)
    {
        int i = (int)d;
        if (i == d) return i;
        return DoubleToInt32Slow(d);
    }

    /// <summary>ToInt32 of a double that is no int: the low 32 bits of the truncated magnitude, from the IEEE fields (a floating-point remainder is far slower).</summary>
    private static int DoubleToInt32Slow(double d)
    {
        long bits = BitConverter.DoubleToInt64Bits(d);
        int exp = (int)((bits >> 52) & 0x7FF) - 1023;
        if (exp < 0 || exp >= 84) return 0;   // |d| < 1, or a multiple of 2^32 (this includes NaN and the infinities)
        ulong m = ((ulong)bits & 0xF_FFFF_FFFF_FFFFUL) | (1UL << 52);
        uint low = exp <= 52 ? (uint)(m >> (52 - exp)) : (uint)(m << (exp - 52));
        return unchecked(bits < 0 ? -(int)low : (int)low);
    }

    public static uint ToUint32(Realm realm, in JsValue v) => (uint)ToInt32(realm, v);

    public static double ToIntegerOrInfinity(Realm realm, in JsValue v)
    {
        double d = ToNumber(realm, v);
        if (double.IsNaN(d) || d == 0) return 0;
        if (double.IsInfinity(d)) return d;
        return Math.Truncate(d);
    }

    public static long ToLength(Realm realm, in JsValue v)
    {
        double len = ToIntegerOrInfinity(realm, v);
        if (len <= 0) return 0;
        return (long)Math.Min(len, JsValue.MaxSafeInteger);
    }

    public static long LengthOfArrayLike(Realm realm, JsObject o)
    {
        if (o is JsArray a) return a.Length;   // own non-configurable length: always exact
        return ToLength(realm, o.Get("length"));   // typed arrays: `length` may be shadowed or redefined
    }

    /// <summary>Relative index helper used by slice/at etc.</summary>
    public static long RelativeIndex(Realm realm, in JsValue v, long len, long defaultValue)
    {
        if (v.IsUndefined) return defaultValue;
        double rel = ToIntegerOrInfinity(realm, v);
        if (rel < 0) return (long)Math.Max(len + rel, 0);
        return (long)Math.Min(rel, len);
    }

    public static bool SameValue(in JsValue a, in JsValue b)
    {
        if (a.IsNumber && b.IsNumber)
        {
            if (double.IsNaN(a.Num) && double.IsNaN(b.Num)) return true;
            if (a.Num == 0 && b.Num == 0) return double.IsNegative(a.Num) == double.IsNegative(b.Num);
            return a.Num == b.Num;
        }
        return StrictEquals(a, b);
    }

    public static bool SameValueZero(in JsValue a, in JsValue b)
    {
        if (a.IsNumber && b.IsNumber)
            return a.Num == b.Num || (double.IsNaN(a.Num) && double.IsNaN(b.Num));
        return StrictEquals(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool StrictEquals(in JsValue a, in JsValue b)
    {
        if (a.Tag != b.Tag) return false;
        switch (a.Tag)
        {
            case JsTag.Number: return a.Num == b.Num;
            case JsTag.String: return a.StringLength == b.StringLength && string.Equals(a.AsStringUnchecked, b.AsStringUnchecked);
            case JsTag.Boolean: return a.Bits == b.Bits;
            case JsTag.Undefined:
            case JsTag.Null: return true;
            case JsTag.BigInt: return a.BigIntValue == b.BigIntValue;
            default: return ReferenceEquals(a.Ref, b.Ref);
        }
    }

    public static bool LooseEquals(Realm realm, JsValue a, JsValue b)
    {
        while (true)
        {
            if (a.Tag == b.Tag) return StrictEquals(a, b);
            if (a.IsNullOrUndefined && b.IsNullOrUndefined) return true;
            // Annex B.3.6.2: [[IsHTMLDDA]] objects are loosely equal to null and undefined
            if ((a.IsNullOrUndefined && b.Ref is JsHtmlDda) || (b.IsNullOrUndefined && a.Ref is JsHtmlDda)) return true;
            if (a.IsNullOrUndefined || b.IsNullOrUndefined) return false;
            if (a.IsNumber && b.IsString) return a.Num == NumberFormatting.StringToNumber(b.AsStringUnchecked);
            if (a.IsString && b.IsNumber) return NumberFormatting.StringToNumber(a.AsStringUnchecked) == b.Num;
            if (a.IsBigInt && b.IsString) { var n = BigIntOps.StringToBigInt(b.AsStringUnchecked); return n.HasValue && n.Value == a.BigIntValue; }
            if (a.IsString && b.IsBigInt) { var n = BigIntOps.StringToBigInt(a.AsStringUnchecked); return n.HasValue && n.Value == b.BigIntValue; }
            if (a.IsBoolean) { a = new JsValue(a.IsTrue ? 1.0 : 0.0); continue; }
            if (b.IsBoolean) { b = new JsValue(b.IsTrue ? 1.0 : 0.0); continue; }
            if (b.IsObject && a.Tag is JsTag.Number or JsTag.String or JsTag.Symbol or JsTag.BigInt) { b = ToPrimitive(realm, b); continue; }
            if (a.IsObject && b.Tag is JsTag.Number or JsTag.String or JsTag.Symbol or JsTag.BigInt) { a = ToPrimitive(realm, a); continue; }
            if (a.IsBigInt && b.IsNumber) return BigIntOps.CompareWithNumber(a.BigIntValue, b.Num) == 0;
            if (a.IsNumber && b.IsBigInt) return BigIntOps.CompareWithNumber(b.BigIntValue, a.Num) == 0;
            return false;
        }
    }

    public static string TypeOf(in JsValue v) => v.Tag switch
    {
        JsTag.Undefined => "undefined",
        JsTag.Null => "object",
        JsTag.Boolean => "boolean",
        JsTag.Number => "number",
        JsTag.String => "string",
        JsTag.Symbol => "symbol",
        JsTag.BigInt => "bigint",
        JsTag.Object => v.Ref is JsHtmlDda ? "undefined" : v.Ref is JsFunctionObject ? "function" : "object",
        _ => "undefined",
    };

    public static JsValue Add(Realm realm, in JsValue a, in JsValue b)
    {
        if (a.IsNumber && b.IsNumber) return new JsValue(a.Num + b.Num);
        if (a.IsString && b.IsString) return ConcatParts(realm, a.Ref!, b.Ref!);
        var pa = ToPrimitive(realm, a);
        var pb = ToPrimitive(realm, b);
        if (pa.IsString || pb.IsString)
        {
            // string + integer ("k" + i, i + "px"): the digits are written straight into the result, no digit string
            if (pb.IsNumber && pa.Ref is string sa && TryConcatInt(realm, sa, pb.Num, numberFirst: false, out var r)) return r;
            if (pa.IsNumber && pb.Ref is string sb && TryConcatInt(realm, sb, pa.Num, numberFirst: true, out r)) return r;
            // A string operand stays as it is (it may be a rope); only the other side is converted.
            object left = pa.IsString ? pa.Ref! : ToString(realm, pa);
            object right = pb.IsString ? pb.Ref! : ToString(realm, pb);
            return ConcatParts(realm, left, right);
        }
        if (pa.IsBigInt || pb.IsBigInt) return BigIntOps.Binary(realm, Jaunt.Compiler.Op.Add, ToNumericValue(realm, pa), ToNumericValue(realm, pb));
        return new JsValue(ToNumber(realm, pa) + ToNumber(realm, pb));
    }

    public static JsValue Concat(Realm realm, string a, string b) => ConcatParts(realm, a, b);

    /// <summary>
    /// Concatenation of a flat string and a number that is a 32-bit integer, built in one allocation when the result is
    /// below the rope threshold. Same text as ToString(number) (-0 prints as "0").
    /// </summary>
    private static bool TryConcatInt(Realm realm, string s, double d, bool numberFirst, out JsValue result)
    {
        result = default;
        if (d != (int)d) return false;   // also false for NaN and out-of-range values
        int n = (int)d;
        uint mag = n < 0 ? (uint)(-(long)n) : (uint)n;
        int digits = n < 0 ? 2 : 1;
        while (mag >= 10) { mag /= 10; digits++; }
        long len = (long)s.Length + digits;
        if (len >= JsRope.MinLength || len > realm.MaxStringLength) return false;
        realm.ChargeBytes(len * 2 + 24);
        result = new JsValue(string.Create((int)len, (s, n, digits, numberFirst), static (span, st) =>
        {
            var (str, num, nd, first) = st;
            if (first)
            {
                num.TryFormat(span.Slice(0, nd), out _, default, CultureInfo.InvariantCulture);
                str.AsSpan().CopyTo(span.Slice(nd));
            }
            else
            {
                str.AsSpan().CopyTo(span);
                num.TryFormat(span.Slice(str.Length), out _, default, CultureInfo.InvariantCulture);
            }
        }));
        return true;
    }

    private static JsValue FromPart(object part) => part is string s ? new JsValue(s) : new JsValue((JsRope)part);

    /// <summary>
    /// String concatenation of two parts, each a <see cref="string"/> or a <see cref="JsRope"/>. Short results are copied
    /// flat; long ones become a rope node, so repeated appends do not copy the accumulated text each time.
    /// </summary>
    internal static JsValue ConcatParts(Realm realm, object a, object b)
    {
        int la = JsRope.LengthOf(a), lb = JsRope.LengthOf(b);
        long len = (long)la + lb;
        if (len > realm.MaxStringLength) throw JsErrors.RangeError(realm, "Invalid string length");
        if (la == 0) return FromPart(b);
        if (lb == 0) return FromPart(a);
        if (len < JsRope.MinLength)
        {
            realm.ChargeBytes(len * 2 + 24);   // a flat copy
            return new JsValue(string.Concat(a as string ?? ((JsRope)a).Flatten(), b as string ?? ((JsRope)b).Flatten()));
        }
        // A rope node copies nothing: it is charged the smaller part, which is the growth of a doubling (`s += s` is still
        // charged its full size, so allocation bombs hit the limit) but only the added text for an append or prepend
        // (building a string piecewise stays linear). The characters themselves are charged when a rope is flattened.
        realm.ChargeBytes(Math.Min(la, lb) * 2L + 48);
        return new JsValue(new JsRope(a, b, (int)len));
    }

    /// <summary>IsLessThan (ECMA-262 7.2.13). Returns null for undefined (NaN involved).</summary>
    public static bool? LessThan(Realm realm, JsValue x, JsValue y, bool leftFirst)
    {
        JsValue px, py;
        if (leftFirst) { px = ToPrimitive(realm, x, PreferredType.Number); py = ToPrimitive(realm, y, PreferredType.Number); }
        else { py = ToPrimitive(realm, y, PreferredType.Number); px = ToPrimitive(realm, x, PreferredType.Number); }
        if (px.IsString && py.IsString)
            return string.CompareOrdinal(px.AsStringUnchecked, py.AsStringUnchecked) < 0;
        if (px.IsBigInt && py.IsString)
        {
            var ny2 = BigIntOps.StringToBigInt(py.AsStringUnchecked);
            return ny2.HasValue ? px.BigIntValue < ny2.Value : null;
        }
        if (px.IsString && py.IsBigInt)
        {
            var nx2 = BigIntOps.StringToBigInt(px.AsStringUnchecked);
            return nx2.HasValue ? nx2.Value < py.BigIntValue : null;
        }
        var cx = ToNumericValue(realm, px);
        var cy = ToNumericValue(realm, py);
        if (cx.IsBigInt && cy.IsBigInt) return cx.BigIntValue < cy.BigIntValue;
        if (cx.IsBigInt) return double.IsNaN(cy.Num) ? null : BigIntOps.CompareWithNumber(cx.BigIntValue, cy.Num) < 0;
        if (cy.IsBigInt) return double.IsNaN(cx.Num) ? null : BigIntOps.CompareWithNumber(cy.BigIntValue, cx.Num) > 0;
        px = cx; py = cy;
        double nx = ToNumber(realm, px), ny = ToNumber(realm, py);
        if (double.IsNaN(nx) || double.IsNaN(ny)) return null;
        return nx < ny;
    }

    public static bool InstanceOf(Realm realm, in JsValue v, in JsValue target)
    {
        if (target.IsNotObject) throw JsErrors.TypeError(realm, "Right-hand side of 'instanceof' is not an object");
        var t = target.AsObjectUnchecked;
        var instOf = t.Get(JsSymbol.HasInstance, target);
        if (!instOf.IsNullOrUndefined)
        {
            if (!ReferenceEquals(instOf.Ref, realm.FunctionPrototypeHasInstance))
                return ToBoolean(realm.Vm.Call(instOf, target, new ReadOnlySpan<JsValue>(in v)));
        }
        if (t is not JsFunctionObject) throw JsErrors.TypeError(realm, "Right-hand side of 'instanceof' is not callable");
        return OrdinaryHasInstance(realm, t, v);
    }

    public static bool OrdinaryHasInstance(Realm realm, JsObject c, in JsValue o)
    {
        if (c is not JsFunctionObject) return false;
        if (c is JsBoundFunction bf) return InstanceOf(realm, o, JsValue.FromObject(bf.Target));
        if (o.IsNotObject) return false;
        var p = c.Get("prototype");
        if (p.IsNotObject) throw JsErrors.TypeError(realm, "Function has non-object prototype in instanceof check");
        var proto = p.AsObjectUnchecked;
        var obj = o.AsObjectUnchecked.GetPrototypeOf();
        while (obj != null)
        {
            if (ReferenceEquals(obj, proto)) return true;
            obj = obj.GetPrototypeOf();
        }
        return false;
    }

    public static bool HasProperty(Realm realm, in JsValue target, in JsValue key)
    {
        if (target.IsNotObject) throw JsErrors.TypeError(realm, "Cannot use 'in' operator to search for a key in a non-object");
        return target.AsObjectUnchecked.HasProperty(ToPropertyKey(realm, key));
    }

    public static bool IsCallable(in JsValue v) => v.IsObject && v.Ref is JsFunctionObject;
    public static bool IsConstructor(in JsValue v) => v.IsObject && v.Ref is JsFunctionObject f && f.IsConstructor;

    public static JsValue GetV(Realm realm, in JsValue v, in PropertyKey key)
    {
        if (v.IsObject) return v.AsObjectUnchecked.Get(key, v);
        return GetPrimitiveProperty(realm, v, key);
    }

    public static JsValue GetPrimitiveProperty(Realm realm, in JsValue v, in PropertyKey key)
    {
        JsObject proto;
        switch (v.Tag)
        {
            case JsTag.String:
            {
                var s = v.AsStringUnchecked;
                if (key.Value is string ks)
                {
                    if (ks == "length") return new JsValue(s.Length);
                    if (PropertyKey.TryParseArrayIndex(ks, out uint idx))
                        return idx < (uint)s.Length ? new JsValue(realm.CharString(s[(int)idx])) : JsValue.Undefined;
                }
                proto = realm.StringPrototype;
                break;
            }
            case JsTag.Number: proto = realm.NumberPrototype; break;
            case JsTag.Boolean: proto = realm.BooleanPrototype; break;
            case JsTag.Symbol: proto = realm.SymbolPrototype; break;
            case JsTag.BigInt: proto = realm.BigIntPrototype; break;
            default:
                throw JsErrors.TypeError(realm, $"Cannot read properties of {v} (reading '{key}')");
        }
        return proto.Get(key, v);
    }

    public static JsValue Invoke(Realm realm, in JsValue v, in PropertyKey key, ReadOnlySpan<JsValue> args)
    {
        var f = GetV(realm, v, key);
        if (!f.IsFunction) throw JsErrors.TypeError(realm, $"{key} is not a function");
        return realm.Vm.Call(f, v, args);
    }

    public static JsObject SpeciesConstructorDefault(Realm realm, JsObject o, JsObject defaultCtor)
    {
        var c = o.Get("constructor");
        if (c.IsUndefined) return defaultCtor;
        if (c.IsNotObject) throw JsErrors.TypeError(realm, "object.constructor is not an object");
        var s = c.AsObjectUnchecked.Get(JsSymbol.Species);
        if (s.IsNullOrUndefined) return defaultCtor;
        if (IsConstructor(s)) return s.AsObjectUnchecked;
        throw JsErrors.TypeError(realm, "object.constructor[Symbol.species] is not a constructor");
    }

    public static JsObject GetPrototypeFromConstructor(Realm realm, JsObject? newTarget, JsObject fallback)
    {
        if (newTarget == null) return fallback;
        var p = newTarget.Get("prototype");
        if (p.IsObject) return p.AsObjectUnchecked;
        var fr = GetFunctionRealm(realm, newTarget);
        return ReferenceEquals(fr, realm) ? fallback : realm.TranslateIntrinsic(fallback, fr);
    }

    /// <summary>GetFunctionRealm (7.3.24).</summary>
    public static Realm GetFunctionRealm(Realm realm, JsObject obj)
    {
        for (int depth = 0; depth < 100_000; depth++)
        {
            switch (obj)
            {
                case JsBoundFunction bf: obj = bf.Target; continue;
                case JsProxyFunction pf:
                    if (pf.Inner.Handler == null) throw JsErrors.TypeError(realm, "Cannot perform operation on a revoked proxy");
                    obj = pf.Inner.Target!;
                    continue;
                case JsProxy px:
                    if (px.Handler == null) throw JsErrors.TypeError(realm, "Cannot perform operation on a revoked proxy");
                    obj = px.Target!;
                    continue;
                case JsFunctionObject fo: return fo.FunctionRealm;
                default: return realm;
            }
        }
        realm.Vm.EnsureNativeStack();
        return realm;
    }

    /// <summary>
    /// HasProperty(o, key) followed by Get(o, key) when true, in one walk of the chain when it consists of ordinary objects,
    /// where those steps are not observable apart from running a getter, which this leaves to Get.
    /// </summary>
    private static bool DescriptorField(JsObject o, in PropertyKey key, out JsValue value)
    {
        for (var p = o; p != null; p = p.Proto)
        {
            if (p.GetType() != typeof(JsObject) && p is not JsImmutableProtoObject)
            {
                bool has = o.HasProperty(key);
                value = has ? o.Get(key) : default;
                return has;
            }
            int i = p.FindOwn(key, out var flags);
            if (i >= 0)
            {
                value = (flags & PropFlags.Accessor) != 0 ? o.Get(key) : p.ReadSlot(i);
                return true;
            }
        }
        value = default;
        return false;
    }

    public static PropertyDescriptor ToPropertyDescriptor(Realm realm, in JsValue v)
    {
        if (v.IsNotObject) throw JsErrors.TypeError(realm, "Property description must be an object");
        var o = v.AsObjectUnchecked;
        var d = new PropertyDescriptor();
        if (DescriptorField(o, "enumerable", out var field)) { d.HasEnumerable = true; d.Enumerable = ToBoolean(field); }
        if (DescriptorField(o, "configurable", out field)) { d.HasConfigurable = true; d.Configurable = ToBoolean(field); }
        if (DescriptorField(o, "value", out field)) { d.HasValue = true; d.Value = field; }
        if (DescriptorField(o, "writable", out field)) { d.HasWritable = true; d.Writable = ToBoolean(field); }
        if (DescriptorField(o, "get", out field))
        {
            if (!field.IsUndefined && !field.IsFunction) throw JsErrors.TypeError(realm, "Getter must be a function");
            d.HasGet = true; d.Getter = field.IsUndefined ? null : field.AsObjectUnchecked;
        }
        if (DescriptorField(o, "set", out field))
        {
            if (!field.IsUndefined && !field.IsFunction) throw JsErrors.TypeError(realm, "Setter must be a function");
            d.HasSet = true; d.Setter = field.IsUndefined ? null : field.AsObjectUnchecked;
        }
        if ((d.HasGet || d.HasSet) && (d.HasValue || d.HasWritable))
            throw JsErrors.TypeError(realm, "Invalid property descriptor. Cannot both specify accessors and a value or writable attribute");
        return d;
    }

    public static JsValue FromPropertyDescriptor(Realm realm, in PropertyDescriptor d)
    {
        var o = new JsObject(realm, d.IsAccessor ? realm.Shapes.AccessorDescriptor : realm.Shapes.DataDescriptor, 4);
        realm.ChargeProperties(4);
        var slots = o.Slots;
        if (d.IsAccessor)
        {
            slots[0] = d.Getter != null ? JsValue.FromObject(d.Getter) : JsValue.Undefined;
            slots[1] = d.Setter != null ? JsValue.FromObject(d.Setter) : JsValue.Undefined;
        }
        else
        {
            slots[0] = d.Value;
            slots[1] = JsValue.FromBool(d.Writable);
        }
        slots[2] = JsValue.FromBool(d.Enumerable);
        slots[3] = JsValue.FromBool(d.Configurable);
        return JsValue.FromObject(o);
    }

    public static JsValue FromPropertyDescriptorPartial(Realm realm, in PropertyDescriptor d)
    {
        var o = new JsObject(realm, realm.ObjectPrototype);
        if (d.HasValue) o.FastAdd("value", d.Value);
        if (d.HasWritable) o.FastAdd("writable", JsValue.FromBool(d.Writable));
        if (d.HasGet) o.FastAdd("get", d.Getter != null ? JsValue.FromObject(d.Getter) : JsValue.Undefined);
        if (d.HasSet) o.FastAdd("set", d.Setter != null ? JsValue.FromObject(d.Setter) : JsValue.Undefined);
        if (d.HasEnumerable) o.FastAdd("enumerable", JsValue.FromBool(d.Enumerable));
        if (d.HasConfigurable) o.FastAdd("configurable", JsValue.FromBool(d.Configurable));
        return JsValue.FromObject(o);
    }

    public static JsArray CreateArrayFromList(Realm realm, ReadOnlySpan<JsValue> items)
    {
        var a = new JsArray(realm, items.Length);
        for (int i = 0; i < items.Length; i++) a.Items[i] = items[i];
        a.Length = (uint)items.Length;
        return a;
    }

    public static System.Collections.Generic.List<JsValue> IterableToList(Realm realm, in JsValue iterable)
    {
        var list = new System.Collections.Generic.List<JsValue>();
        var it = IteratorRecord.Get(realm, iterable);
        while (it.Step(out var v))
        {
            list.Add(v);
            realm.Vm.Tick();
        }
        return list;
    }

    public static System.Collections.Generic.List<JsValue> CreateListFromArrayLike(Realm realm, in JsValue v)
    {
        if (v.IsNotObject) throw JsErrors.TypeError(realm, "CreateListFromArrayLike called on non-object");
        var o = v.AsObjectUnchecked;
        long len = LengthOfArrayLike(realm, o);
        if (len > realm.MaxArrayLength) throw JsErrors.RangeError(realm, "Too many arguments");
        var list = new System.Collections.Generic.List<JsValue>((int)len);
        for (long i = 0; i < len; i++) { list.Add(o.GetIndex((uint)i, v)); realm.Vm.Tick(); }
        return list;
    }
}
