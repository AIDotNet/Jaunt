using System;
using Jaunt.Runtime;

namespace Jaunt;

/// <summary>
/// Statically-typed conversions for basic CLR types used by typed host function overloads. Resolved once per
/// closed generic type with <c>typeof</c> checks (no reflection, no runtime code generation); unsupported types
/// are rejected at registration time.
/// </summary>
internal static class JsConvert<T>
{
    public static readonly Func<Realm, JsValue, int, string, T>? From;
    public static readonly Func<Realm, T, JsValue>? To;

    static JsConvert()
    {
        var t = typeof(T);
        if (t == typeof(double)) { From = (Func<Realm, JsValue, int, string, T>)(object)(Func<Realm, JsValue, int, string, double>)FromDouble; To = (Func<Realm, T, JsValue>)(object)(Func<Realm, double, JsValue>)((r, v) => new JsValue(v)); }
        else if (t == typeof(float)) { From = (Func<Realm, JsValue, int, string, T>)(object)(Func<Realm, JsValue, int, string, float>)((r, v, i, n) => (float)FromDouble(r, v, i, n)); To = (Func<Realm, T, JsValue>)(object)(Func<Realm, float, JsValue>)((r, v) => new JsValue(v)); }
        else if (t == typeof(int)) { From = (Func<Realm, JsValue, int, string, T>)(object)(Func<Realm, JsValue, int, string, int>)FromInt32; To = (Func<Realm, T, JsValue>)(object)(Func<Realm, int, JsValue>)((r, v) => new JsValue(v)); }
        else if (t == typeof(long)) { From = (Func<Realm, JsValue, int, string, T>)(object)(Func<Realm, JsValue, int, string, long>)FromInt64; To = (Func<Realm, T, JsValue>)(object)(Func<Realm, long, JsValue>)ToInt64; }
        else if (t == typeof(bool)) { From = (Func<Realm, JsValue, int, string, T>)(object)(Func<Realm, JsValue, int, string, bool>)FromBool; To = (Func<Realm, T, JsValue>)(object)(Func<Realm, bool, JsValue>)((r, v) => JsValue.FromBool(v)); }
        else if (t == typeof(string)) { From = (Func<Realm, JsValue, int, string, T>)(object)(Func<Realm, JsValue, int, string, string?>)FromString; To = (Func<Realm, T, JsValue>)(object)(Func<Realm, string?, JsValue>)((r, v) => JsValue.From(v is null ? null : r.Charged(v))); }   // host-made strings enter the tracked budget
        else if (t == typeof(JsValue)) { From = (Func<Realm, JsValue, int, string, T>)(object)(Func<Realm, JsValue, int, string, JsValue>)((r, v, i, n) => v); To = (Func<Realm, T, JsValue>)(object)(Func<Realm, JsValue, JsValue>)ToJsValue; }
    }

    public static bool Supported => From != null && To != null;

    public static void EnsureSupported()
    {
        if (!Supported)
            throw new NotSupportedException($"Type '{typeof(T)}' is not supported by typed host function adapters. Use double/float/int/long/bool/string/JsValue, a JsTypeInfo<T> overload, or a raw JsBuiltin callback.");
    }

    private static double FromDouble(Realm realm, JsValue v, int index, string fn)
    {
        if (v.IsNumber) return v.Num;
        throw JsErrors.TypeError(realm, $"Argument {index + 1} of '{fn}' must be a number, got {v.TypeName}");
    }

    private static int FromInt32(Realm realm, JsValue v, int index, string fn)
    {
        if (v.IsNumber && v.Num == Math.Floor(v.Num) && v.Num >= int.MinValue && v.Num <= int.MaxValue) return (int)v.Num;
        throw JsErrors.TypeError(realm, $"Argument {index + 1} of '{fn}' must be an Int32 integer, got {v}");
    }

    private static long FromInt64(Realm realm, JsValue v, int index, string fn)
    {
        if (v.IsNumber && v.Num == Math.Floor(v.Num) && Math.Abs(v.Num) <= JsValue.MaxSafeInteger) return (long)v.Num;
        throw JsErrors.TypeError(realm, $"Argument {index + 1} of '{fn}' must be a safe integer, got {v}");
    }

    private static bool FromBool(Realm realm, JsValue v, int index, string fn)
    {
        if (v.IsBoolean) return v.IsTrue;
        throw JsErrors.TypeError(realm, $"Argument {index + 1} of '{fn}' must be a boolean, got {v.TypeName}");
    }

    private static string? FromString(Realm realm, JsValue v, int index, string fn)
    {
        if (v.IsString) return v.AsStringUnchecked;
        if (v.IsNullOrUndefined) return null;
        throw JsErrors.TypeError(realm, $"Argument {index + 1} of '{fn}' must be a string, got {v.TypeName}");
    }

    private static JsValue ToInt64(Realm realm, long v)
    {
        if (v > JsValue.MaxSafeInteger || v < -JsValue.MaxSafeInteger)
            throw JsErrors.RangeError(realm, $"Host result {v} is outside the JavaScript safe integer range");
        return new JsValue(v);
    }

    private static JsValue ToJsValue(Realm realm, JsValue v)
    {
        // another realm of the same engine (ShadowRealm, CreateRealm) is fine; another engine is not
        if (v.IsObject && !ReferenceEquals(((JsObject)v.Ref!).Realm.Vm, realm.Vm))
            throw JsErrors.TypeError(realm, "Host function returned a value owned by another engine");
        if (v.Ref is JsSymbol { RegistryKey: { } key } sym && (!realm.SymbolRegistry.TryGetValue(key, out var own) || !ReferenceEquals(own, sym)))
            throw JsErrors.TypeError(realm, "Host function returned a registered symbol of another engine");
        return v;
    }
}
