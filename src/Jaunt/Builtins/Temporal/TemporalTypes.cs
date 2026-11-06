using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Jaunt.Runtime;

internal sealed class JsPlainDate : JsObject
{
    public readonly IsoDate Date;
    public readonly string Calendar;
    public JsPlainDate(Realm realm, JsObject proto, IsoDate date, string calendar) : base(realm, proto) { Date = date; Calendar = calendar; }
}

internal sealed class JsPlainTime : JsObject
{
    public readonly TimeRec Time;
    public JsPlainTime(Realm realm, JsObject proto, TimeRec time) : base(realm, proto) => Time = time;
}

internal sealed class JsPlainDateTime : JsObject
{
    public readonly IsoDateTime DateTime;
    public readonly string Calendar;
    public JsPlainDateTime(Realm realm, JsObject proto, IsoDateTime dt, string calendar) : base(realm, proto) { DateTime = dt; Calendar = calendar; }
}

internal sealed class JsPlainYearMonth : JsObject
{
    public readonly IsoDate Date;
    public readonly string Calendar;
    public JsPlainYearMonth(Realm realm, JsObject proto, IsoDate date, string calendar) : base(realm, proto) { Date = date; Calendar = calendar; }
}

internal sealed class JsPlainMonthDay : JsObject
{
    public readonly IsoDate Date;
    public readonly string Calendar;
    public JsPlainMonthDay(Realm realm, JsObject proto, IsoDate date, string calendar) : base(realm, proto) { Date = date; Calendar = calendar; }
}

internal sealed class JsInstant : JsObject
{
    public readonly Int128 Ns;
    public JsInstant(Realm realm, JsObject proto, Int128 ns) : base(realm, proto) => Ns = ns;
}

internal sealed class JsZonedDateTime : JsObject
{
    public readonly Int128 Ns;
    public readonly TemporalTz Tz;
    public readonly string Calendar;
    public JsZonedDateTime(Realm realm, JsObject proto, Int128 ns, TemporalTz tz, string calendar) : base(realm, proto) { Ns = ns; Tz = tz; Calendar = calendar; }
}

internal sealed class JsDuration : JsObject
{
    /// <summary>years, months, weeks, days, hours, minutes, seconds, milliseconds, microseconds, nanoseconds.</summary>
    public readonly double[] F;
    public JsDuration(Realm realm, JsObject proto, double[] fields) : base(realm, proto) => F = fields;
}

internal sealed class TemporalProtos
{
    public JsObject PlainDate = null!, PlainTime = null!, PlainDateTime = null!, PlainYearMonth = null!, PlainMonthDay = null!;
    public JsObject Instant = null!, ZonedDateTime = null!, Duration = null!;
}

internal sealed partial class Realm
{
    private TemporalProtos? _temporal;
    public TemporalProtos Temporal => _temporal ?? Ensure(IntrinsicGroup.Temporal)._temporal!;
    internal void SetTemporal(TemporalProtos p) => _temporal = p;
}

/// <summary>Fields read from a property bag (PrepareCalendarFields result); NaN = unset for numbers.</summary>
internal sealed class TemporalFields
{
    public double Year = double.NaN, Month = double.NaN, Day = double.NaN;
    public string? MonthCode;
    public double Hour = double.NaN, Minute = double.NaN, Second = double.NaN, Millisecond = double.NaN, Microsecond = double.NaN, Nanosecond = double.NaN;
    public string? Offset;
    public TemporalTz? TimeZone;
    public string? Era;
    public double EraYear = double.NaN;
}

[Flags]
internal enum FieldSet
{
    None = 0,
    Year = 1, Month = 2, MonthCode = 4, Day = 8,
    Hour = 16, Minute = 32, Second = 64, Millisecond = 128, Microsecond = 256, Nanosecond = 512,
    Offset = 1024, TimeZone = 2048, Era = 4096, EraYear = 8192,
    Date = Year | Month | MonthCode | Day,
    YearMonth = Year | Month | MonthCode,
    Time = Hour | Minute | Second | Millisecond | Microsecond | Nanosecond,
}

internal enum CalendarType { Date, YearMonth, MonthDay }

/// <summary>Temporal abstract operations shared by all types: creation, conversion, calendar and formatting.</summary>
internal static partial class Temporal
{
    public const string Iso8601 = "iso8601";

    // ------------------------------------------------------------------ creation (with range checks)

    public static JsPlainDate CreatePlainDate(Realm realm, IsoDate d, string cal, JsObject? newTarget = null)
    {
        if (!TemporalMath.ISODateWithinLimits(d)) throw JsErrors.RangeError(realm, "date is outside the supported range");
        var proto = newTarget == null ? realm.Temporal.PlainDate : Operations.GetPrototypeFromConstructor(realm, newTarget, realm.Temporal.PlainDate);
        return new JsPlainDate(realm, proto, d, cal);
    }

    public static JsPlainTime CreatePlainTime(Realm realm, TimeRec t, JsObject? newTarget = null)
    {
        var proto = newTarget == null ? realm.Temporal.PlainTime : Operations.GetPrototypeFromConstructor(realm, newTarget, realm.Temporal.PlainTime);
        return new JsPlainTime(realm, proto, t);
    }

    public static JsPlainDateTime CreatePlainDateTime(Realm realm, IsoDateTime dt, string cal, JsObject? newTarget = null)
    {
        if (!TemporalMath.ISODateTimeWithinLimits(dt)) throw JsErrors.RangeError(realm, "date-time is outside the supported range");
        var proto = newTarget == null ? realm.Temporal.PlainDateTime : Operations.GetPrototypeFromConstructor(realm, newTarget, realm.Temporal.PlainDateTime);
        return new JsPlainDateTime(realm, proto, dt, cal);
    }

    public static JsPlainYearMonth CreatePlainYearMonth(Realm realm, IsoDate d, string cal, JsObject? newTarget = null)
    {
        if (!TemporalMath.ISOYearMonthWithinLimits(d.Year, d.Month)) throw JsErrors.RangeError(realm, "year-month is outside the supported range");
        var proto = newTarget == null ? realm.Temporal.PlainYearMonth : Operations.GetPrototypeFromConstructor(realm, newTarget, realm.Temporal.PlainYearMonth);
        return new JsPlainYearMonth(realm, proto, d, cal);
    }

    public static JsPlainMonthDay CreatePlainMonthDay(Realm realm, IsoDate d, string cal, JsObject? newTarget = null)
    {
        if (!TemporalMath.ISODateWithinLimits(d)) throw JsErrors.RangeError(realm, "month-day is outside the supported range");
        var proto = newTarget == null ? realm.Temporal.PlainMonthDay : Operations.GetPrototypeFromConstructor(realm, newTarget, realm.Temporal.PlainMonthDay);
        return new JsPlainMonthDay(realm, proto, d, cal);
    }

    public static JsInstant CreateInstant(Realm realm, Int128 ns, JsObject? newTarget = null)
    {
        if (!TemporalMath.IsValidEpochNanoseconds(ns)) throw JsErrors.RangeError(realm, "instant is outside the supported range");
        var proto = newTarget == null ? realm.Temporal.Instant : Operations.GetPrototypeFromConstructor(realm, newTarget, realm.Temporal.Instant);
        return new JsInstant(realm, proto, ns);
    }

    public static JsZonedDateTime CreateZonedDateTime(Realm realm, Int128 ns, TemporalTz tz, string cal, JsObject? newTarget = null)
    {
        if (!TemporalMath.IsValidEpochNanoseconds(ns)) throw JsErrors.RangeError(realm, "instant is outside the supported range");
        var proto = newTarget == null ? realm.Temporal.ZonedDateTime : Operations.GetPrototypeFromConstructor(realm, newTarget, realm.Temporal.ZonedDateTime);
        return new JsZonedDateTime(realm, proto, ns, tz, cal);
    }

    // ------------------------------------------------------------------ calendars (ISO 8601 only, no ECMA-402)

    /// <summary>CanonicalizeCalendar: only "iso8601" is available (ASCII case-insensitive).</summary>
    public static string CanonicalizeCalendar(Realm realm, string id) =>
        TemporalCalendars.Canonicalize(id) ?? throw JsErrors.RangeError(realm, $"unsupported calendar {id}");

    private static bool IsAscii(string s) { foreach (char c in s) if (c > 0x7F) return false; return true; }

    /// <summary>ToTemporalCalendarIdentifier.</summary>
    public static string ToCalendarIdentifier(Realm realm, in JsValue v)
    {
        if (v.IsObject)
        {
            var c = GetCalendarSlot(v.AsObjectUnchecked);
            if (c != null) return c;
        }
        if (!v.IsString) throw JsErrors.TypeError(realm, "calendar must be a string or a Temporal object");
        var s = v.AsStringUnchecked;
        // a calendar identifier, or an ISO string with an optional [u-ca=...] annotation
        if (TemporalCalendars.Canonicalize(s) is { } known) return known;
        var r = ParseAny(s);
        if (r == null) throw JsErrors.RangeError(realm, $"invalid calendar {s}");
        return CanonicalizeCalendar(realm, r.Calendar ?? Iso8601);
    }

    private static IsoParseResult? ParseAny(string s) =>
        TemporalParser.Parse(s, ParseGoal.ZonedDateTime) ?? TemporalParser.Parse(s, ParseGoal.DateTime) ?? TemporalParser.Parse(s, ParseGoal.Instant)
        ?? TemporalParser.Parse(s, ParseGoal.Time) ?? TemporalParser.Parse(s, ParseGoal.MonthDay) ?? TemporalParser.Parse(s, ParseGoal.YearMonth);

    public static string? GetCalendarSlot(JsObject o) => o switch
    {
        JsPlainDate d => d.Calendar,
        JsPlainDateTime dt => dt.Calendar,
        JsPlainYearMonth ym => ym.Calendar,
        JsPlainMonthDay md => md.Calendar,
        JsZonedDateTime z => z.Calendar,
        _ => null,
    };

    /// <summary>GetTemporalCalendarIdentifierWithISODefault.</summary>
    public static string GetCalendarWithISODefault(Realm realm, JsObject item)
    {
        var c = GetCalendarSlot(item);
        if (c != null) return c;
        var v = item.Get("calendar");
        if (v.IsUndefined) return Iso8601;
        return ToCalendarIdentifier(realm, v);
    }

    public static string CalendarFromConstructorArg(Realm realm, in JsValue v)
    {
        if (v.IsUndefined) return Iso8601;
        if (!v.IsString) throw JsErrors.TypeError(realm, "calendar must be a string");
        return CanonicalizeCalendar(realm, v.AsStringUnchecked);
    }

    // ------------------------------------------------------------------ time zones

    /// <summary>ToTemporalTimeZoneIdentifier.</summary>
    public static TemporalTz ToTimeZone(Realm realm, in JsValue v)
    {
        if (v.IsObject && v.AsObjectUnchecked is JsZonedDateTime z) return z.Tz;
        if (!v.IsString) throw JsErrors.TypeError(realm, "time zone must be a string or a Temporal.ZonedDateTime");
        var s = v.AsStringUnchecked;
        if (TemporalParser.IsTimeZoneIdentifier(s))
            return TemporalTimeZones.Resolve(s) ?? throw JsErrors.RangeError(realm, $"unknown time zone {s}");
        var r = ParseAny(s);
        if (r == null) throw JsErrors.RangeError(realm, $"invalid time zone {s}");
        if (r.TimeZone != null) return TemporalTimeZones.Resolve(r.TimeZone) ?? throw JsErrors.RangeError(realm, $"unknown time zone {r.TimeZone}");
        if (r.Z) return TemporalTimeZones.Utc;
        if (r.Offset != null)
        {
            if (r.OffsetHasSubMinute) throw JsErrors.RangeError(realm, "time zone offsets must have minute precision");
            return TemporalTimeZones.FromOffset(r.OffsetNs);
        }
        throw JsErrors.RangeError(realm, $"invalid time zone {s}");
    }

    public static TemporalTz TimeZoneFromConstructorArg(Realm realm, in JsValue v)
    {
        if (!v.IsString) throw JsErrors.TypeError(realm, "time zone must be a string");
        return TemporalTimeZones.Resolve(v.AsStringUnchecked) ?? throw JsErrors.RangeError(realm, $"invalid time zone {v.AsStringUnchecked}");
    }

    public static IsoDateTime GetISODateTimeFor(TemporalTz tz, Int128 ns)
    {
        long off = TemporalTimeZones.GetOffsetNs(tz, ns);
        return TemporalMath.GetISOPartsFromEpoch(ns + off);
    }

    // ------------------------------------------------------------------ property bags

    private static readonly (string Name, FieldSet Flag)[] s_fieldOrder =
    {
        ("day", FieldSet.Day), ("era", FieldSet.Era), ("eraYear", FieldSet.EraYear), ("hour", FieldSet.Hour), ("microsecond", FieldSet.Microsecond), ("millisecond", FieldSet.Millisecond),
        ("minute", FieldSet.Minute), ("month", FieldSet.Month), ("monthCode", FieldSet.MonthCode), ("nanosecond", FieldSet.Nanosecond),
        ("offset", FieldSet.Offset), ("second", FieldSet.Second), ("timeZone", FieldSet.TimeZone), ("year", FieldSet.Year),
    };

    /// <summary>
    /// PrepareCalendarFields: reads the requested fields in code-unit order, converting each as it is read.
    /// <paramref name="partial"/> = requiredFieldNames "partial"; otherwise <paramref name="required"/> lists the
    /// required names and missing time fields default to 0.
    /// </summary>
    public static TemporalFields PrepareFields(Realm realm, string cal, JsObject bag, FieldSet names, FieldSet required, bool partial)
    {
        // CalendarExtraFields: era and eraYear accompany year in calendars with eras
        if ((names & FieldSet.Year) != 0 && TemporalCalendars.SupportsEra(cal)) names |= FieldSet.Era | FieldSet.EraYear;
        var f = new TemporalFields();
        bool any = false;
        foreach (var (name, flag) in s_fieldOrder)
        {
            if ((names & flag) == 0) continue;
            var v = bag.Get(name);
            if (!v.IsUndefined)
            {
                any = true;
                switch (flag)
                {
                    case FieldSet.Year: f.Year = TemporalOptions.ToIntegerWithTruncation(realm, v); break;
                    case FieldSet.Month: f.Month = TemporalOptions.ToPositiveIntegerWithTruncation(realm, v); break;
                    case FieldSet.Day: f.Day = TemporalOptions.ToPositiveIntegerWithTruncation(realm, v); break;
                    case FieldSet.MonthCode: f.MonthCode = ToMonthCode(realm, v); break;
                    case FieldSet.Hour: f.Hour = TemporalOptions.ToIntegerWithTruncation(realm, v); break;
                    case FieldSet.Minute: f.Minute = TemporalOptions.ToIntegerWithTruncation(realm, v); break;
                    case FieldSet.Second: f.Second = TemporalOptions.ToIntegerWithTruncation(realm, v); break;
                    case FieldSet.Millisecond: f.Millisecond = TemporalOptions.ToIntegerWithTruncation(realm, v); break;
                    case FieldSet.Microsecond: f.Microsecond = TemporalOptions.ToIntegerWithTruncation(realm, v); break;
                    case FieldSet.Nanosecond: f.Nanosecond = TemporalOptions.ToIntegerWithTruncation(realm, v); break;
                    case FieldSet.Offset: f.Offset = ToOffsetString(realm, v); break;
                    case FieldSet.TimeZone: f.TimeZone = ToTimeZone(realm, v); break;
                    case FieldSet.Era: f.Era = Operations.ToString(realm, v); break;
                    case FieldSet.EraYear: f.EraYear = TemporalOptions.ToIntegerWithTruncation(realm, v); break;
                }
            }
            else if (!partial)
            {
                if ((required & flag) != 0) throw JsErrors.TypeError(realm, $"required property '{name}' is missing or undefined");
                switch (flag)
                {
                    case FieldSet.Hour: f.Hour = 0; break;
                    case FieldSet.Minute: f.Minute = 0; break;
                    case FieldSet.Second: f.Second = 0; break;
                    case FieldSet.Millisecond: f.Millisecond = 0; break;
                    case FieldSet.Microsecond: f.Microsecond = 0; break;
                    case FieldSet.Nanosecond: f.Nanosecond = 0; break;
                }
            }
        }
        if (partial && !any) throw JsErrors.TypeError(realm, "the object has no recognized Temporal fields");
        return f;
    }

    private static string ToMonthCode(Realm realm, in JsValue v)
    {
        var p = Operations.ToPrimitive(realm, v, Operations.PreferredType.String);
        if (!p.IsString) throw JsErrors.TypeError(realm, "monthCode must be a string");
        var s = p.AsStringUnchecked;
        if (!IsMonthCodeSyntax(s)) throw JsErrors.RangeError(realm, $"invalid monthCode {s}");
        return s;
    }

    /// <summary>MonthCode grammar: M00L | M0[1-9]L? | M1[0-9]L?.</summary>
    private static bool IsMonthCodeSyntax(string s)
    {
        if (s.Length < 3 || s.Length > 4 || s[0] != 'M' || !char.IsAsciiDigit(s[1]) || !char.IsAsciiDigit(s[2])) return false;
        if (s.Length == 4 && s[3] != 'L') return false;
        if (s[1] == '0' && s[2] == '0' && s.Length == 3) return false;
        return true;
    }

    private static string ToOffsetString(Realm realm, in JsValue v)
    {
        var p = Operations.ToPrimitive(realm, v, Operations.PreferredType.String);
        if (!p.IsString) throw JsErrors.TypeError(realm, "offset must be a string");
        var s = p.AsStringUnchecked;
        if (!TemporalParser.TryParseOffset(s, true, out _, out _)) throw JsErrors.RangeError(realm, $"invalid offset {s}");
        return s;
    }

    public static long ParseOffset(Realm realm, string s)
    {
        if (!TemporalParser.TryParseOffset(s, true, out long ns, out _)) throw JsErrors.RangeError(realm, $"invalid offset {s}");
        return ns;
    }

    /// <summary>CalendarResolveFields.</summary>
    private static void ResolveFields(Realm realm, string cal, TemporalFields f, CalendarType type)
    {
        if (cal != Iso8601) { NonISOResolveFields(realm, cal, f, type); return; }
        if (type != CalendarType.MonthDay && double.IsNaN(f.Year)) throw JsErrors.TypeError(realm, "year is required");
        if (type != CalendarType.YearMonth && double.IsNaN(f.Day)) throw JsErrors.TypeError(realm, "day is required");
        if (f.MonthCode == null)
        {
            if (double.IsNaN(f.Month)) throw JsErrors.TypeError(realm, "month or monthCode is required");
            return;
        }
        var mc = f.MonthCode;
        int num = (mc[1] - '0') * 10 + (mc[2] - '0');
        if (mc.Length == 4 || num < 1 || num > 12) throw JsErrors.RangeError(realm, $"monthCode {mc} is not valid for the ISO 8601 calendar");
        if (!double.IsNaN(f.Month) && f.Month != num) throw JsErrors.RangeError(realm, "month and monthCode do not agree");
        f.Month = num;
    }

    private static bool YearInRange(double y) => Math.Abs(y) <= 400_000;

    /// <summary>NonISOResolveFields (Intl era and month code).</summary>
    private static void NonISOResolveFields(Realm realm, string cal, TemporalFields f, CalendarType type)
    {
        bool needsYear = type != CalendarType.MonthDay || f.MonthCode == null || !double.IsNaN(f.Month);
        bool needsDay = type != CalendarType.YearMonth;
        bool eras = TemporalCalendars.SupportsEra(cal);
        if (needsYear && double.IsNaN(f.Year))
        {
            if (!eras) throw JsErrors.TypeError(realm, "year is required");
            if (f.Era == null || double.IsNaN(f.EraYear)) throw JsErrors.TypeError(realm, "year, or era and eraYear, are required");
        }
        if (eras)
        {
            if (f.Era != null && double.IsNaN(f.EraYear)) throw JsErrors.TypeError(realm, "eraYear is required with era");
            if (!double.IsNaN(f.EraYear) && f.Era == null) throw JsErrors.TypeError(realm, "era is required with eraYear");
        }
        if (needsDay && double.IsNaN(f.Day)) throw JsErrors.TypeError(realm, "day is required");
        if (double.IsNaN(f.Month) && f.MonthCode == null) throw JsErrors.TypeError(realm, "month or monthCode is required");
        if (eras && !double.IsNaN(f.EraYear))
        {
            var era = TemporalCalendars.CanonicalizeEra(cal, f.Era!);
            if (era == null) throw JsErrors.RangeError(realm, $"{f.Era} is not an era of the {cal} calendar");
            double y = TemporalCalendars.ArithmeticYearForEraYear(cal, era, f.EraYear);
            if (!double.IsNaN(f.Year) && f.Year != y) throw JsErrors.RangeError(realm, "year and era/eraYear do not agree");
            f.Year = y;
        }
        f.Era = null;
        f.EraYear = double.NaN;
        if (f.MonthCode != null)
        {
            if (!TemporalCalendars.IsValidMonthCode(cal, f.MonthCode)) throw JsErrors.RangeError(realm, $"monthCode {f.MonthCode} is not valid for the {cal} calendar");
            if (!double.IsNaN(f.Year))
            {
                if (!YearInRange(f.Year)) throw JsErrors.RangeError(realm, "date is outside the supported range");
                int y = (int)f.Year;
                var constrained = TemporalCalendars.ConstrainMonthCode(cal, y, f.MonthCode, Overflow.Constrain)!;
                int month = TemporalCalendars.MonthCodeToOrdinal(cal, y, constrained);
                if (!double.IsNaN(f.Month) && f.Month != month) throw JsErrors.RangeError(realm, "month and monthCode do not agree");
                f.Month = month;
            }
        }
    }

    /// <summary>NonISOCalendarDateToISO.</summary>
    private static IsoDate NonISODateToISO(Realm realm, string cal, TemporalFields f, Overflow overflow)
    {
        if (!YearInRange(f.Year)) throw JsErrors.RangeError(realm, "date is outside the supported range");
        int year = (int)f.Year;
        if (f.MonthCode != null && TemporalCalendars.ConstrainMonthCode(cal, year, f.MonthCode, overflow) == null)
            throw JsErrors.RangeError(realm, $"monthCode {f.MonthCode} does not exist in year {year}");
        int miy = TemporalCalendars.MonthsInYear(cal, year);
        double month = f.Month;
        if (month > miy)
        {
            if (overflow == Overflow.Reject) throw JsErrors.RangeError(realm, "month is out of range");
            month = miy;
        }
        if (month < 1) throw JsErrors.RangeError(realm, "month is out of range");
        int dim = TemporalCalendars.DaysInMonth(cal, year, (int)month);
        double day = f.Day;
        if (day > dim)
        {
            if (overflow == Overflow.Reject) throw JsErrors.RangeError(realm, "day is out of range");
            day = dim;
        }
        if (day < 1) throw JsErrors.RangeError(realm, "day is out of range");
        return TemporalCalendars.IntegersToISO(cal, year, (int)month, (int)day);
    }

    /// <summary>RegulateISODate.</summary>
    public static IsoDate RegulateISODate(Realm realm, double y, double m, double d, Overflow overflow)
    {
        if (overflow == Overflow.Reject)
        {
            if (m < 1 || m > 12 || d < 1 || Math.Abs(y) > 1e9 || d > TemporalMath.DaysInMonth((long)y, (int)m))
                throw JsErrors.RangeError(realm, "invalid date");
            return new IsoDate((int)y, (int)m, (int)d);
        }
        if (Math.Abs(y) > 1e9) throw JsErrors.RangeError(realm, "date is outside the supported range");
        m = Math.Clamp(m, 1, 12);
        d = Math.Clamp(d, 1, TemporalMath.DaysInMonth((long)y, (int)m));
        return new IsoDate((int)y, (int)m, (int)d);
    }

    public static TimeRec RegulateTime(Realm realm, double h, double m, double s, double ms, double us, double ns, Overflow overflow)
    {
        if (overflow == Overflow.Constrain) return TemporalMath.ConstrainTime(h, m, s, ms, us, ns);
        if (!TemporalMath.IsValidTime(h, m, s, ms, us, ns)) throw JsErrors.RangeError(realm, "invalid time");
        return new TimeRec((int)h, (int)m, (int)s, (int)ms, (int)us, (int)ns);
    }

    /// <summary>CalendarDateFromFields.</summary>
    public static IsoDate DateFromFields(Realm realm, string cal, TemporalFields f, Overflow overflow)
    {
        ResolveFields(realm, cal, f, CalendarType.Date);
        var d = cal == Iso8601 ? RegulateISODate(realm, f.Year, f.Month, f.Day, overflow) : NonISODateToISO(realm, cal, f, overflow);
        if (!TemporalMath.ISODateWithinLimits(d)) throw JsErrors.RangeError(realm, "date is outside the supported range");
        return d;
    }

    /// <summary>CalendarYearMonthFromFields (reference day: the first day of the calendar month).</summary>
    public static IsoDate YearMonthFromFields(Realm realm, string cal, TemporalFields f, Overflow overflow)
    {
        ResolveFields(realm, cal, f, CalendarType.YearMonth);
        f.Day = 1;
        var d = cal == Iso8601 ? RegulateISODate(realm, f.Year, f.Month, 1, overflow) : NonISODateToISO(realm, cal, f, overflow);
        if (!TemporalMath.ISOYearMonthWithinLimits(d.Year, d.Month)) throw JsErrors.RangeError(realm, "year-month is outside the supported range");
        return d;
    }

    /// <summary>CalendarMonthDayFromFields (reference year 1972 for ISO; see NonISOMonthDayToISOReferenceDate otherwise).</summary>
    public static IsoDate MonthDayFromFields(Realm realm, string cal, TemporalFields f, Overflow overflow)
    {
        ResolveFields(realm, cal, f, CalendarType.MonthDay);
        if (cal != Iso8601) return NonISOMonthDayReference(realm, cal, f, overflow);
        double year = double.IsNaN(f.Year) ? 1972 : f.Year;
        var d = RegulateISODate(realm, year, f.Month, f.Day, overflow);
        return new IsoDate(1972, d.Month, d.Day);
    }

    /// <summary>NonISOMonthDayToISOReferenceDate.</summary>
    private static IsoDate NonISOMonthDayReference(Realm realm, string cal, TemporalFields f, Overflow overflow)
    {
        string monthCode;
        int daysInMonth;
        if (!double.IsNaN(f.Year))
        {
            if (!YearInRange(f.Year) || !TemporalMath.ISODateWithinLimits(TemporalCalendars.IntegersToISO(cal, (int)f.Year, 1, 1)))
                throw JsErrors.RangeError(realm, "year is outside the supported range");
            int year = (int)f.Year;
            int miy = TemporalCalendars.MonthsInYear(cal, year);
            double month = f.Month;
            if (month > miy)
            {
                if (overflow == Overflow.Reject) throw JsErrors.RangeError(realm, "month is out of range");
                month = miy;
            }
            if (month < 1) throw JsErrors.RangeError(realm, "month is out of range");
            if (f.MonthCode == null) monthCode = TemporalCalendars.MonthCodeOf(cal, year, (int)month);
            else monthCode = TemporalCalendars.ConstrainMonthCode(cal, year, f.MonthCode, overflow)
                ?? throw JsErrors.RangeError(realm, $"monthCode {f.MonthCode} does not exist in year {year}");
            daysInMonth = TemporalCalendars.DaysInMonth(cal, year, (int)month);
        }
        else
        {
            monthCode = f.MonthCode!;
            daysInMonth = cal is "chinese" or "dangi" ? 30 : MaxDaysInMonthCode(cal, monthCode);
        }
        double day = f.Day;
        if (day > daysInMonth)
        {
            if (overflow == Overflow.Reject) throw JsErrors.RangeError(realm, "day is out of range");
            day = daysInMonth;
        }
        if (day < 1) throw JsErrors.RangeError(realm, "day is out of range");
        var r = FindReferenceDate(cal, monthCode, (int)day);
        if (r == null && cal is "chinese" or "dangi")
        {
            if (overflow == Overflow.Reject) throw JsErrors.RangeError(realm, $"{monthCode} day {day} has no reference date");
            r = FindReferenceDate(cal, monthCode.Substring(0, 3), (int)day);
        }
        return r ?? throw JsErrors.RangeError(realm, $"{monthCode} day {day} has no reference date");
    }

    private static int MaxDaysInMonthCode(string cal, string code)
    {
        int max = 0;
        var a = TemporalCalendars.ISOToDate(cal, new IsoDate(1900, 1, 1)).Year;
        var b = TemporalCalendars.ISOToDate(cal, new IsoDate(2035, 12, 31)).Year;
        for (int y = a; y <= b; y++)
            if (TemporalCalendars.YearContainsMonthCode(cal, y, code))
                max = Math.Max(max, TemporalCalendars.DaysInMonth(cal, y, TemporalCalendars.MonthCodeToOrdinal(cal, y, code)));
        return max;
    }

    /// <summary>The latest ISO date in 1900–1972 with this month code and day, else the earliest in 1973–2035.</summary>
    private static IsoDate? FindReferenceDate(string cal, string code, int day)
    {
        int y1972 = TemporalCalendars.ISOToDate(cal, new IsoDate(1972, 12, 31)).Year;
        int y1900 = TemporalCalendars.ISOToDate(cal, new IsoDate(1900, 1, 1)).Year;
        int y2035 = TemporalCalendars.ISOToDate(cal, new IsoDate(2035, 12, 31)).Year;
        for (int y = y1972; y >= y1900; y--)
        {
            var d = Candidate(cal, y, code, day);
            if (d is { } iso && iso.Year >= 1900 && iso.Year <= 1972) return iso;
        }
        for (int y = y1972; y <= y2035; y++)
        {
            var d = Candidate(cal, y, code, day);
            if (d is { } iso && iso.Year >= 1973 && iso.Year <= 2035) return iso;
        }
        return null;
    }

    private static IsoDate? Candidate(string cal, int y, string code, int day)
    {
        if (!TemporalCalendars.YearContainsMonthCode(cal, y, code)) return null;
        int m = TemporalCalendars.MonthCodeToOrdinal(cal, y, code);
        if (day > TemporalCalendars.DaysInMonth(cal, y, m)) return null;
        return TemporalCalendars.IntegersToISO(cal, y, m, day);
    }

    public static TimeRec TimeFromFields(Realm realm, TemporalFields f, Overflow overflow) =>
        RegulateTime(realm, f.Hour, f.Minute, f.Second, f.Millisecond, f.Microsecond, f.Nanosecond, overflow);

    /// <summary>ToTemporalTimeRecord (partial allowed when <paramref name="partial"/>): fields read alphabetically.</summary>
    public static double[] ToTimeRecord(Realm realm, JsObject bag, bool partial, double[]? defaults = null)
    {
        // hour, microsecond, millisecond, minute, nanosecond, second
        var r = defaults != null ? (double[])defaults.Clone() : new double[6];
        bool any = false;
        string[] names = { "hour", "microsecond", "millisecond", "minute", "nanosecond", "second" };
        int[] slot = { 0, 4, 3, 1, 5, 2 };
        for (int i = 0; i < names.Length; i++)
        {
            var v = bag.Get(names[i]);
            if (v.IsUndefined) continue;
            any = true;
            r[slot[i]] = TemporalOptions.ToIntegerWithTruncation(realm, v);
        }
        if (!any) throw JsErrors.TypeError(realm, "the object has no time fields");
        _ = partial;
        return r;
    }

    /// <summary>RejectTemporalLikeObject (for with()).</summary>
    public static void RejectTemporalLike(Realm realm, JsObject o)
    {
        if (o is JsPlainDate or JsPlainDateTime or JsPlainMonthDay or JsPlainTime or JsPlainYearMonth or JsZonedDateTime)
            throw JsErrors.TypeError(realm, "a Temporal object is not a valid argument here");
        if (!o.Get("calendar").IsUndefined) throw JsErrors.TypeError(realm, "calendar is not allowed in a property bag here");
        if (!o.Get("timeZone").IsUndefined) throw JsErrors.TypeError(realm, "timeZone is not allowed in a property bag here");
    }

    /// <summary>CalendarMergeFields: keys present in the partial (and the keys they make ambiguous) replace the base.</summary>
    public static TemporalFields MergeFields(string cal, TemporalFields baseFields, TemporalFields partial)
    {
        var r = new TemporalFields
        {
            Year = baseFields.Year, Month = baseFields.Month, MonthCode = baseFields.MonthCode, Day = baseFields.Day,
            Hour = baseFields.Hour, Minute = baseFields.Minute, Second = baseFields.Second, Millisecond = baseFields.Millisecond,
            Microsecond = baseFields.Microsecond, Nanosecond = baseFields.Nanosecond, Offset = baseFields.Offset, TimeZone = baseFields.TimeZone,
            Era = baseFields.Era, EraYear = baseFields.EraYear,
        };
        bool month = !double.IsNaN(partial.Month), monthCode = partial.MonthCode != null, day = !double.IsNaN(partial.Day);
        bool year = !double.IsNaN(partial.Year), era = partial.Era != null, eraYear = !double.IsNaN(partial.EraYear);
        if (month || monthCode) { r.Month = double.NaN; r.MonthCode = null; }
        if (cal != Iso8601 && TemporalCalendars.SupportsEra(cal) && (year || era || eraYear))
        {
            r.Year = double.NaN; r.Era = null; r.EraYear = double.NaN;
        }
        if (cal == "japanese" && (day || month || monthCode)) { r.Era = null; r.EraYear = double.NaN; }
        if (year) r.Year = partial.Year;
        if (month) r.Month = partial.Month;
        if (monthCode) r.MonthCode = partial.MonthCode;
        if (day) r.Day = partial.Day;
        if (era) r.Era = partial.Era;
        if (eraYear) r.EraYear = partial.EraYear;
        if (!double.IsNaN(partial.Hour)) r.Hour = partial.Hour;
        if (!double.IsNaN(partial.Minute)) r.Minute = partial.Minute;
        if (!double.IsNaN(partial.Second)) r.Second = partial.Second;
        if (!double.IsNaN(partial.Millisecond)) r.Millisecond = partial.Millisecond;
        if (!double.IsNaN(partial.Microsecond)) r.Microsecond = partial.Microsecond;
        if (!double.IsNaN(partial.Nanosecond)) r.Nanosecond = partial.Nanosecond;
        if (partial.Offset != null) r.Offset = partial.Offset;
        if (partial.TimeZone != null) r.TimeZone = partial.TimeZone;
        return r;
    }

    /// <summary>ISODateToFields.</summary>
    public static TemporalFields DateToFields(string cal, IsoDate d, CalendarType type)
    {
        int year = d.Year, month = d.Month, day = d.Day;
        string code = MonthCode(d.Month);
        if (cal != Iso8601)
        {
            var c = TemporalCalendars.ISOToDate(cal, d);
            year = c.Year; month = c.Month; day = c.Day; code = c.MonthCode;
        }
        var f = new TemporalFields { MonthCode = code };
        if (type != CalendarType.MonthDay) f.Year = year;
        if (type != CalendarType.YearMonth) f.Day = day;
        // the month code identifies the month; an ordinal month would conflict after a year change
        _ = month;
        return f;
    }

    /// <summary>CalendarISOToDate (ISO or non-ISO).</summary>
    public static CalendarDate CalendarISOToDate(string cal, IsoDate d)
    {
        if (cal != Iso8601) return TemporalCalendars.ISOToDate(cal, d);
        int diy = TemporalMath.DaysInYear(d.Year);
        return new CalendarDate(null, null, d.Year, d.Month, MonthCode(d.Month), d.Day, TemporalMath.DayOfYear(d),
            TemporalMath.DaysInMonth(d.Year, d.Month), diy, 12, diy == 366);
    }

    public static void SetTimeFields(TemporalFields f, TimeRec t)
    {
        f.Hour = t.Hour; f.Minute = t.Minute; f.Second = t.Second;
        f.Millisecond = t.Millisecond; f.Microsecond = t.Microsecond; f.Nanosecond = t.Nanosecond;
    }

    public static string MonthCode(int month) => "M" + month.ToString("00", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ string parsing helpers

    public static IsoParseResult ParseOrThrow(Realm realm, string s, ParseGoal goal) =>
        TemporalParser.Parse(s, goal) ?? throw JsErrors.RangeError(realm, $"invalid ISO 8601 string: {s}");

    public static string CalendarFromParse(Realm realm, IsoParseResult r) => r.Calendar == null ? Iso8601 : CanonicalizeCalendar(realm, r.Calendar);

    // ------------------------------------------------------------------ formatting

    public static string PadYear(long y)
    {
        if (y >= 0 && y <= 9999) return y.ToString("0000", CultureInfo.InvariantCulture);
        return (y < 0 ? "-" : "+") + Math.Abs(y).ToString("000000", CultureInfo.InvariantCulture);
    }

    public static string DateToString(IsoDate d) =>
        PadYear(d.Year) + "-" + d.Month.ToString("00", CultureInfo.InvariantCulture) + "-" + d.Day.ToString("00", CultureInfo.InvariantCulture);

    /// <summary>Precision: -2 = minute, -1 = auto, 0..9 = fixed fractional digits.</summary>
    public static string TimeToString(TimeRec t, int precision)
    {
        var sb = new StringBuilder(18);
        sb.Append(t.Hour.ToString("00", CultureInfo.InvariantCulture)).Append(':').Append(t.Minute.ToString("00", CultureInfo.InvariantCulture));
        if (precision == -2) return sb.ToString();
        sb.Append(':').Append(t.Second.ToString("00", CultureInfo.InvariantCulture));
        long sub = t.Millisecond * 1_000_000L + t.Microsecond * 1000L + t.Nanosecond;
        sb.Append(FormatFraction(sub, precision));
        return sb.ToString();
    }

    public static string FormatFraction(long subSecondNs, int precision)
    {
        if (precision == 0) return "";
        var digits = subSecondNs.ToString("000000000", CultureInfo.InvariantCulture);
        if (precision == -1)
        {
            if (subSecondNs == 0) return "";
            return "." + digits.TrimEnd('0');
        }
        return "." + digits.Substring(0, precision);
    }

    public static string CalendarAnnotation(string cal, string show)
    {
        if (show == "never") return "";
        if (show == "auto" && cal == Iso8601) return "";
        return (show == "critical" ? "[!u-ca=" : "[u-ca=") + cal + "]";
    }

    public static string DateTimeToString(IsoDateTime dt, string cal, int precision, string showCalendar) =>
        DateToString(dt.Date) + "T" + TimeToString(dt.Time, precision) + CalendarAnnotation(cal, showCalendar);

    /// <summary>ToSecondsStringPrecisionRecord: (precision, unit, increment).</summary>
    public static (int Precision, TUnit Unit, long Increment) SecondsPrecision(TUnit smallestUnit, int digits)
    {
        switch (smallestUnit)
        {
            case TUnit.Minute: return (-2, TUnit.Minute, 1);
            case TUnit.Second: return (0, TUnit.Second, 1);
            case TUnit.Millisecond: return (3, TUnit.Millisecond, 1);
            case TUnit.Microsecond: return (6, TUnit.Microsecond, 1);
            case TUnit.Nanosecond: return (9, TUnit.Nanosecond, 1);
        }
        if (digits == -1) return (-1, TUnit.Nanosecond, 1);
        if (digits == 0) return (0, TUnit.Second, 1);
        if (digits <= 3) return (digits, TUnit.Millisecond, (long)Math.Pow(10, 3 - digits));
        if (digits <= 6) return (digits, TUnit.Microsecond, (long)Math.Pow(10, 6 - digits));
        return (digits, TUnit.Nanosecond, (long)Math.Pow(10, 9 - digits));
    }

    public static string FormatOffsetRounded(long offsetNs)
    {
        var r = (long)TemporalMath.RoundToIncrement(offsetNs, TemporalMath.NsPerMinute, RoundingMode.HalfExpand);
        return TemporalTimeZones.FormatOffsetId(r);
    }

    public static string FormatNumber(double d) => d.ToString("0", CultureInfo.InvariantCulture);

    public static JsValue BigIntValue(Int128 v) => JsValue.FromBigInteger((BigInteger)v);
}
