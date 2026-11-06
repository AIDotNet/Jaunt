using System;

namespace Jaunt.Runtime;

/// <summary>Option-bag readers of Temporal (GetOptionsObject, GetOption and the typed wrappers).</summary>
internal static class TemporalOptions
{
    public static JsObject? GetOptionsObject(Realm realm, in JsValue v)
    {
        if (v.IsUndefined) return null;
        if (v.IsObject) return v.AsObjectUnchecked;
        throw JsErrors.TypeError(realm, "Options must be an object or undefined");
    }

    /// <summary>GetOption with type string: returns null when the property is undefined.</summary>
    public static string? GetString(Realm realm, JsObject? options, string name, string[] allowed)
    {
        if (options == null) return null;
        var v = options.Get(name);
        if (v.IsUndefined) return null;
        var s = Operations.ToString(realm, v);
        if (Array.IndexOf(allowed, s) < 0) throw JsErrors.RangeError(realm, $"{s} is not a valid value for {name}");
        return s;
    }

    private static readonly string[] s_overflow = { "constrain", "reject" };
    private static readonly string[] s_disambiguation = { "compatible", "earlier", "later", "reject" };
    private static readonly string[] s_offset = { "prefer", "use", "ignore", "reject" };
    private static readonly string[] s_roundingModes = { "ceil", "floor", "expand", "trunc", "halfCeil", "halfFloor", "halfExpand", "halfTrunc", "halfEven" };
    private static readonly string[] s_calendarName = { "auto", "always", "never", "critical" };
    private static readonly string[] s_timeZoneName = { "auto", "never", "critical" };
    private static readonly string[] s_showOffset = { "auto", "never" };

    public static Overflow GetOverflow(Realm realm, JsObject? options) =>
        GetString(realm, options, "overflow", s_overflow) == "reject" ? Overflow.Reject : Overflow.Constrain;

    public static Disambiguation GetDisambiguation(Realm realm, JsObject? options) =>
        GetString(realm, options, "disambiguation", s_disambiguation) switch
        {
            "earlier" => Disambiguation.Earlier,
            "later" => Disambiguation.Later,
            "reject" => Disambiguation.Reject,
            _ => Disambiguation.Compatible,
        };

    public static OffsetOption GetOffsetOption(Realm realm, JsObject? options, OffsetOption fallback) =>
        GetString(realm, options, "offset", s_offset) switch
        {
            "prefer" => OffsetOption.Prefer,
            "use" => OffsetOption.Use,
            "ignore" => OffsetOption.Ignore,
            "reject" => OffsetOption.Reject,
            _ => fallback,
        };

    public static RoundingMode GetRoundingMode(Realm realm, JsObject? options, RoundingMode fallback) =>
        GetString(realm, options, "roundingMode", s_roundingModes) switch
        {
            null => fallback,
            "ceil" => RoundingMode.Ceil,
            "floor" => RoundingMode.Floor,
            "expand" => RoundingMode.Expand,
            "trunc" => RoundingMode.Trunc,
            "halfCeil" => RoundingMode.HalfCeil,
            "halfFloor" => RoundingMode.HalfFloor,
            "halfExpand" => RoundingMode.HalfExpand,
            "halfTrunc" => RoundingMode.HalfTrunc,
            _ => RoundingMode.HalfEven,
        };

    /// <summary>"auto" | "always" | "never" | "critical".</summary>
    public static string GetShowCalendarName(Realm realm, JsObject? options) => GetString(realm, options, "calendarName", s_calendarName) ?? "auto";
    public static string GetShowTimeZoneName(Realm realm, JsObject? options) => GetString(realm, options, "timeZoneName", s_timeZoneName) ?? "auto";
    public static string GetShowOffset(Realm realm, JsObject? options) => GetString(realm, options, "offset", s_showOffset) ?? "auto";

    /// <summary>GetRoundingIncrementOption: an integer in [1, 10^9].</summary>
    public static long GetRoundingIncrement(Realm realm, JsObject? options)
    {
        if (options == null) return 1;
        var v = options.Get("roundingIncrement");
        if (v.IsUndefined) return 1;
        double d = ToIntegerWithTruncation(realm, v);
        if (d < 1 || d > 1e9) throw JsErrors.RangeError(realm, "roundingIncrement must be between 1 and 10^9");
        return (long)d;
    }

    /// <summary>GetTemporalFractionalSecondDigitsOption: -1 = "auto", otherwise 0..9.</summary>
    public static int GetFractionalSecondDigits(Realm realm, JsObject? options)
    {
        if (options == null) return -1;
        var v = options.Get("fractionalSecondDigits");
        if (v.IsUndefined) return -1;
        if (!v.IsNumber)
        {
            var s = Operations.ToString(realm, v);
            if (s != "auto") throw JsErrors.RangeError(realm, $"{s} is not a valid value for fractionalSecondDigits");
            return -1;
        }
        double d = v.Num;
        if (double.IsNaN(d) || double.IsInfinity(d)) throw JsErrors.RangeError(realm, "fractionalSecondDigits must be finite");
        d = Math.Floor(d);
        if (d < 0 || d > 9) throw JsErrors.RangeError(realm, "fractionalSecondDigits must be between 0 and 9");
        return (int)d;
    }

    private static readonly string[] s_unitNames =
    {
        "year", "month", "week", "day", "hour", "minute", "second", "millisecond", "microsecond", "nanosecond",
    };

    public static string UnitName(TUnit u) => s_unitNames[(int)u];
    public static string UnitPlural(TUnit u) => s_unitNames[(int)u] + "s";

    /// <summary>GetTemporalUnitValuedOption: returns <see cref="TUnit.Unset"/> when undefined.</summary>
    public static TUnit GetUnit(Realm realm, JsObject? options, string key)
    {
        if (options == null) return TUnit.Unset;
        var v = options.Get(key);
        if (v.IsUndefined) return TUnit.Unset;
        var s = Operations.ToString(realm, v);
        if (s == "auto") return TUnit.Auto;
        for (int i = 0; i < s_unitNames.Length; i++)
            if (s == s_unitNames[i] || (s.Length == s_unitNames[i].Length + 1 && s.EndsWith('s') && s.StartsWith(s_unitNames[i], StringComparison.Ordinal)))
                return (TUnit)i;
        throw JsErrors.RangeError(realm, $"{s} is not a valid value for {key}");
    }

    public enum UnitGroup { Date, Time, DateTime }

    /// <summary>ValidateTemporalUnitValue.</summary>
    public static void ValidateUnit(Realm realm, TUnit unit, UnitGroup group, string key, bool allowAuto = false, TUnit? extra = null)
    {
        if (unit == TUnit.Unset) return;
        if (unit == TUnit.Auto)
        {
            if (allowAuto) return;
            throw JsErrors.RangeError(realm, $"auto is not a valid value for {key}");
        }
        if (extra == unit) return;
        bool ok = group switch
        {
            UnitGroup.Date => unit <= TUnit.Day,
            UnitGroup.Time => unit >= TUnit.Hour,
            _ => true,
        };
        if (!ok) throw JsErrors.RangeError(realm, $"{UnitName(unit)} is not a valid value for {key}");
    }

    /// <summary>MaximumTemporalDurationRoundingIncrement: 0 = unset (no maximum).</summary>
    public static long MaxIncrement(TUnit unit) => unit switch
    {
        TUnit.Hour => 24,
        TUnit.Minute or TUnit.Second => 60,
        TUnit.Millisecond or TUnit.Microsecond or TUnit.Nanosecond => 1000,
        _ => 0,
    };

    /// <summary>ValidateTemporalRoundingIncrement.</summary>
    public static void ValidateIncrement(Realm realm, long increment, long dividend, bool inclusive)
    {
        long max = inclusive ? dividend : dividend - 1;
        if (increment > max) throw JsErrors.RangeError(realm, $"roundingIncrement {increment} is out of range");
        if (dividend % increment != 0) throw JsErrors.RangeError(realm, $"roundingIncrement {increment} does not divide evenly");
    }

    public static double ToIntegerWithTruncation(Realm realm, in JsValue v)
    {
        double d = Operations.ToNumber(realm, v);
        if (double.IsNaN(d) || double.IsInfinity(d)) throw JsErrors.RangeError(realm, "value must be a finite number");
        d = Math.Truncate(d);
        return d == 0 ? 0 : d;
    }

    public static double ToPositiveIntegerWithTruncation(Realm realm, in JsValue v)
    {
        double d = ToIntegerWithTruncation(realm, v);
        if (d <= 0) throw JsErrors.RangeError(realm, "value must be a positive integer");
        return d;
    }

    public static double ToIntegerIfIntegral(Realm realm, in JsValue v)
    {
        double d = Operations.ToNumber(realm, v);
        if (double.IsNaN(d) || double.IsInfinity(d) || Math.Truncate(d) != d) throw JsErrors.RangeError(realm, "value must be an integer");
        return d == 0 ? 0 : d;
    }
}
