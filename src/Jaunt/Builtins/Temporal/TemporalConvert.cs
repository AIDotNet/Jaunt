using System;
using System.Numerics;

namespace Jaunt.Runtime;

internal enum OffsetBehaviour { Option, Exact, Wall }

internal static partial class Temporal
{
    private static readonly JsValue s_undefined = JsValue.Undefined;

    /// <summary>InterpretISODateTimeOffset; <paramref name="time"/> null = start of day.</summary>
    public static Int128 InterpretISODateTimeOffset(Realm realm, IsoDate date, TimeRec? time, OffsetBehaviour ob, long offsetNs, TemporalTz tz,
        Disambiguation disambiguation, OffsetOption offsetOption, bool matchMinutes)
    {
        if (time == null) return TemporalTimeZones.GetStartOfDay(realm, tz, date);
        var dt = new IsoDateTime(date, time.Value);
        if (ob == OffsetBehaviour.Wall || (ob == OffsetBehaviour.Option && offsetOption == OffsetOption.Ignore))
            return TemporalTimeZones.GetEpochNsFor(realm, tz, dt, disambiguation);
        if (ob == OffsetBehaviour.Exact || offsetOption == OffsetOption.Use)
        {
            var (days, t) = TemporalMath.AddTime(dt.Time, -offsetNs);
            var balanced = new IsoDateTime(TemporalMath.AddDays(dt.Date, days), t);
            CheckISODaysRange(realm, balanced.Date);
            var ns = TemporalMath.GetUTCEpochNanoseconds(balanced);
            if (!TemporalMath.IsValidEpochNanoseconds(ns)) throw JsErrors.RangeError(realm, "instant is outside the supported range");
            return ns;
        }
        CheckISODaysRange(realm, dt.Date);
        var utc = TemporalMath.GetUTCEpochNanoseconds(dt);
        var possible = TemporalTimeZones.GetPossibleEpochNs(realm, tz, dt);
        foreach (var candidate in possible)
        {
            var candidateOffset = utc - candidate;
            if (candidateOffset == offsetNs) return candidate;
            if (matchMinutes)
            {
                var rounded = TemporalMath.RoundToIncrement(candidateOffset, TemporalMath.NsPerMinute, RoundingMode.HalfExpand);
                if (rounded == offsetNs) return candidate;
            }
        }
        if (offsetOption == OffsetOption.Reject) throw JsErrors.RangeError(realm, "the offset is not valid for this date-time in the time zone");
        return TemporalTimeZones.Disambiguate(realm, possible, tz, dt, disambiguation);
    }

    public static void CheckISODaysRange(Realm realm, IsoDate d)
    {
        if (Math.Abs(TemporalMath.EpochDays(d)) > 100_000_000) throw JsErrors.RangeError(realm, "date is outside the supported range");
    }

    // ------------------------------------------------------------------ ToTemporal*

    public static JsPlainDate ToTemporalDate(Realm realm, in JsValue item, in JsValue options)
    {
        if (item.IsObject)
        {
            var o = item.AsObjectUnchecked;
            switch (o)
            {
                case JsPlainDate pd:
                    TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, options));
                    return CreatePlainDate(realm, pd.Date, pd.Calendar);
                case JsZonedDateTime z:
                {
                    var dt = GetISODateTimeFor(z.Tz, z.Ns);
                    TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, options));
                    return CreatePlainDate(realm, dt.Date, z.Calendar);
                }
                case JsPlainDateTime pdt:
                    TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, options));
                    return CreatePlainDate(realm, pdt.DateTime.Date, pdt.Calendar);
            }
            var cal = GetCalendarWithISODefault(realm, o);
            var fields = PrepareFields(realm, cal, o, FieldSet.Date, FieldSet.None, partial: false);
            var overflow = TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, options));
            return CreatePlainDate(realm, DateFromFields(realm, cal, fields, overflow), cal);
        }
        if (!item.IsString) throw JsErrors.TypeError(realm, "cannot convert to Temporal.PlainDate");
        var r = ParseOrThrow(realm, item.AsStringUnchecked, ParseGoal.DateTime);
        var calendar = CalendarFromParse(realm, r);
        TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, options));
        return CreatePlainDate(realm, new IsoDate(r.Year, r.Month, r.Day), calendar);
    }

    public static JsPlainDateTime ToTemporalDateTime(Realm realm, in JsValue item, in JsValue options)
    {
        if (item.IsObject)
        {
            var o = item.AsObjectUnchecked;
            switch (o)
            {
                case JsPlainDateTime pdt:
                    TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, options));
                    return CreatePlainDateTime(realm, pdt.DateTime, pdt.Calendar);
                case JsZonedDateTime z:
                {
                    var dt = GetISODateTimeFor(z.Tz, z.Ns);
                    TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, options));
                    return CreatePlainDateTime(realm, dt, z.Calendar);
                }
                case JsPlainDate pd:
                    TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, options));
                    return CreatePlainDateTime(realm, new IsoDateTime(pd.Date, TimeRec.Midnight), pd.Calendar);
            }
            var cal = GetCalendarWithISODefault(realm, o);
            var fields = PrepareFields(realm, cal, o, FieldSet.Date | FieldSet.Time, FieldSet.None, partial: false);
            var overflow = TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, options));
            var date = DateFromFields(realm, cal, fields, overflow);
            var time = TimeFromFields(realm, fields, overflow);
            return CreatePlainDateTime(realm, new IsoDateTime(date, time), cal);
        }
        if (!item.IsString) throw JsErrors.TypeError(realm, "cannot convert to Temporal.PlainDateTime");
        var r = ParseOrThrow(realm, item.AsStringUnchecked, ParseGoal.DateTime);
        var calendar = CalendarFromParse(realm, r);
        TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, options));
        return CreatePlainDateTime(realm, new IsoDateTime(new IsoDate(r.Year, r.Month, r.Day), r.HasTime ? r.Time : TimeRec.Midnight), calendar);
    }

    public static JsPlainTime ToTemporalTime(Realm realm, in JsValue item, in JsValue options)
    {
        if (item.IsObject)
        {
            var o = item.AsObjectUnchecked;
            switch (o)
            {
                case JsPlainTime pt:
                    TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, options));
                    return CreatePlainTime(realm, pt.Time);
                case JsPlainDateTime pdt:
                    TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, options));
                    return CreatePlainTime(realm, pdt.DateTime.Time);
                case JsZonedDateTime z:
                {
                    var dt = GetISODateTimeFor(z.Tz, z.Ns);
                    TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, options));
                    return CreatePlainTime(realm, dt.Time);
                }
            }
            var t = ToTimeRecord(realm, o, partial: false);
            var overflow = TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, options));
            return CreatePlainTime(realm, RegulateTime(realm, t[0], t[1], t[2], t[3], t[4], t[5], overflow));
        }
        if (!item.IsString) throw JsErrors.TypeError(realm, "cannot convert to Temporal.PlainTime");
        var r = ParseOrThrow(realm, item.AsStringUnchecked, ParseGoal.Time);
        TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, options));
        return CreatePlainTime(realm, r.Time);
    }

    /// <summary>ToTimeRecordOrMidnight.</summary>
    public static TimeRec ToTimeOrMidnight(Realm realm, in JsValue item) =>
        item.IsUndefined ? TimeRec.Midnight : ToTemporalTime(realm, item, s_undefined).Time;

    public static JsPlainYearMonth ToTemporalYearMonth(Realm realm, in JsValue item, in JsValue options)
    {
        if (item.IsObject)
        {
            var o = item.AsObjectUnchecked;
            if (o is JsPlainYearMonth ym)
            {
                TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, options));
                return CreatePlainYearMonth(realm, ym.Date, ym.Calendar);
            }
            var cal = GetCalendarWithISODefault(realm, o);
            var fields = PrepareFields(realm, cal, o, FieldSet.YearMonth, FieldSet.None, partial: false);
            var overflow = TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, options));
            return CreatePlainYearMonth(realm, YearMonthFromFields(realm, cal, fields, overflow), cal);
        }
        if (!item.IsString) throw JsErrors.TypeError(realm, "cannot convert to Temporal.PlainYearMonth");
        var r = ParseOrThrow(realm, item.AsStringUnchecked, ParseGoal.YearMonth);
        var calendar = CalendarFromParse(realm, r);
        TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, options));
        if (!r.HasDay && calendar != Iso8601) throw JsErrors.RangeError(realm, "a year-month string without a day requires the ISO 8601 calendar");
        if (calendar != Iso8601)
        {
            var iso = new IsoDate(r.Year, r.Month, r.Day);
            if (!TemporalMath.ISODateWithinLimits(iso)) throw JsErrors.RangeError(realm, "date is outside the supported range");
            return CreatePlainYearMonth(realm, YearMonthFromFields(realm, calendar, DateToFields(calendar, iso, CalendarType.YearMonth), Overflow.Constrain), calendar);
        }
        // the reference day is always 1 for the ISO calendar
        var d = new IsoDate(r.Year, r.Month, 1);
        if (!TemporalMath.ISOYearMonthWithinLimits(d.Year, d.Month)) throw JsErrors.RangeError(realm, "year-month is outside the supported range");
        return CreatePlainYearMonth(realm, d, calendar);
    }

    public static JsPlainMonthDay ToTemporalMonthDay(Realm realm, in JsValue item, in JsValue options)
    {
        if (item.IsObject)
        {
            var o = item.AsObjectUnchecked;
            if (o is JsPlainMonthDay md)
            {
                TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, options));
                return CreatePlainMonthDay(realm, md.Date, md.Calendar);
            }
            var cal = GetCalendarWithISODefault(realm, o);
            var fields = PrepareFields(realm, cal, o, FieldSet.Date, FieldSet.None, partial: false);
            var overflow = TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, options));
            return CreatePlainMonthDay(realm, MonthDayFromFields(realm, cal, fields, overflow), cal);
        }
        if (!item.IsString) throw JsErrors.TypeError(realm, "cannot convert to Temporal.PlainMonthDay");
        var r = ParseOrThrow(realm, item.AsStringUnchecked, ParseGoal.MonthDay);
        var calendar = CalendarFromParse(realm, r);
        TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, options));
        if (!r.HasYear && calendar != Iso8601) throw JsErrors.RangeError(realm, "a month-day string without a year requires the ISO 8601 calendar");
        if (calendar == Iso8601) return CreatePlainMonthDay(realm, new IsoDate(1972, r.Month, r.Day), calendar);
        var iso = new IsoDate(r.Year, r.Month, r.Day);
        if (!TemporalMath.ISODateWithinLimits(iso)) throw JsErrors.RangeError(realm, "date is outside the supported range");
        return CreatePlainMonthDay(realm, MonthDayFromFields(realm, calendar, DateToFields(calendar, iso, CalendarType.MonthDay), Overflow.Constrain), calendar);
    }

    public static JsInstant ToTemporalInstant(Realm realm, JsValue item)
    {
        if (item.IsObject)
        {
            var o = item.AsObjectUnchecked;
            if (o is JsInstant i) return CreateInstant(realm, i.Ns);
            if (o is JsZonedDateTime z) return CreateInstant(realm, z.Ns);
            item = Operations.ToPrimitive(realm, item, Operations.PreferredType.String);
        }
        if (!item.IsString) throw JsErrors.TypeError(realm, "cannot convert to Temporal.Instant");
        var r = ParseOrThrow(realm, item.AsStringUnchecked, ParseGoal.Instant);
        long offset = r.Z ? 0 : r.OffsetNs;
        var (days, t) = TemporalMath.AddTime(r.Time, -offset);
        var date = TemporalMath.BalanceISODate(r.Year, r.Month, r.Day + days);
        CheckISODaysRange(realm, date);
        var ns = TemporalMath.GetUTCEpochNanoseconds(new IsoDateTime(date, t));
        return CreateInstant(realm, ns);
    }

    public static JsZonedDateTime ToTemporalZonedDateTime(Realm realm, in JsValue item, in JsValue options)
    {
        var ob = OffsetBehaviour.Option;
        bool matchMinutes = false;
        TemporalTz tz;
        string? offsetString;
        string cal;
        IsoDate date;
        TimeRec? time;
        Disambiguation disambiguation;
        OffsetOption offsetOption;
        if (item.IsObject)
        {
            var o = item.AsObjectUnchecked;
            if (o is JsZonedDateTime z)
            {
                var opts0 = TemporalOptions.GetOptionsObject(realm, options);
                TemporalOptions.GetDisambiguation(realm, opts0);
                TemporalOptions.GetOffsetOption(realm, opts0, OffsetOption.Reject);
                TemporalOptions.GetOverflow(realm, opts0);
                return CreateZonedDateTime(realm, z.Ns, z.Tz, z.Calendar);
            }
            cal = GetCalendarWithISODefault(realm, o);
            var fields = PrepareFields(realm, cal, o, FieldSet.Date | FieldSet.Time | FieldSet.Offset | FieldSet.TimeZone, FieldSet.TimeZone, partial: false);
            tz = fields.TimeZone!;
            offsetString = fields.Offset;
            var opts = TemporalOptions.GetOptionsObject(realm, options);
            disambiguation = TemporalOptions.GetDisambiguation(realm, opts);
            offsetOption = TemporalOptions.GetOffsetOption(realm, opts, OffsetOption.Reject);
            var overflow = TemporalOptions.GetOverflow(realm, opts);
            date = DateFromFields(realm, cal, fields, overflow);
            time = TimeFromFields(realm, fields, overflow);
            if (offsetString == null) ob = OffsetBehaviour.Wall;
        }
        else
        {
            if (!item.IsString) throw JsErrors.TypeError(realm, "cannot convert to Temporal.ZonedDateTime");
            var r = ParseOrThrow(realm, item.AsStringUnchecked, ParseGoal.ZonedDateTime);
            tz = TemporalTimeZones.Resolve(r.TimeZone!) ?? throw JsErrors.RangeError(realm, $"unknown time zone {r.TimeZone}");
            offsetString = r.Offset;
            if (r.Z) ob = OffsetBehaviour.Exact;
            else if (offsetString == null) ob = OffsetBehaviour.Wall;
            cal = CalendarFromParse(realm, r);
            matchMinutes = !r.OffsetHasSubMinute;
            var opts = TemporalOptions.GetOptionsObject(realm, options);
            disambiguation = TemporalOptions.GetDisambiguation(realm, opts);
            offsetOption = TemporalOptions.GetOffsetOption(realm, opts, OffsetOption.Reject);
            TemporalOptions.GetOverflow(realm, opts);
            date = new IsoDate(r.Year, r.Month, r.Day);
            time = r.HasTime ? r.Time : null;
        }
        long offsetNs = ob == OffsetBehaviour.Option ? ParseOffset(realm, offsetString!) : 0;
        var ns = InterpretISODateTimeOffset(realm, date, time, ob, offsetNs, tz, disambiguation, offsetOption, matchMinutes);
        return CreateZonedDateTime(realm, ns, tz, cal);
    }

    public static JsDuration ToTemporalDuration(Realm realm, in JsValue item)
    {
        if (item.IsObject && item.AsObjectUnchecked is JsDuration d) return CreateDuration(realm, (double[])d.F.Clone());
        if (!item.IsObject)
        {
            if (!item.IsString) throw JsErrors.TypeError(realm, "cannot convert to Temporal.Duration");
            return CreateDuration(realm, ParseDuration(realm, item.AsStringUnchecked));
        }
        return CreateDuration(realm, ToPartialDuration(realm, item.AsObjectUnchecked, null));
    }

    private static readonly (string Name, int Index)[] s_durationFields =
    {
        ("days", 3), ("hours", 4), ("microseconds", 8), ("milliseconds", 7), ("minutes", 5),
        ("months", 1), ("nanoseconds", 9), ("seconds", 6), ("weeks", 2), ("years", 0),
    };

    /// <summary>ToTemporalPartialDurationRecord (merged onto <paramref name="baseFields"/> when given).</summary>
    public static double[] ToPartialDuration(Realm realm, JsObject o, double[]? baseFields)
    {
        var f = baseFields != null ? (double[])baseFields.Clone() : new double[10];
        bool any = false;
        foreach (var (name, idx) in s_durationFields)
        {
            var v = o.Get(name);
            if (v.IsUndefined) continue;
            any = true;
            f[idx] = TemporalOptions.ToIntegerIfIntegral(realm, v);
        }
        if (!any) throw JsErrors.TypeError(realm, "the object has no duration fields");
        return f;
    }

    /// <summary>GetTemporalRelativeToOption: returns (plainDate, zoned) — at most one is non-null.</summary>
    public static (JsPlainDate? Plain, JsZonedDateTime? Zoned) GetRelativeTo(Realm realm, JsObject? options)
    {
        if (options == null) return (null, null);
        var value = options.Get("relativeTo");
        if (value.IsUndefined) return (null, null);
        var ob = OffsetBehaviour.Option;
        bool matchMinutes = false;
        TemporalTz? tz;
        string? offsetString;
        string cal;
        IsoDate date;
        TimeRec? time;
        if (value.IsObject)
        {
            var o = value.AsObjectUnchecked;
            switch (o)
            {
                case JsZonedDateTime z: return (null, z);
                case JsPlainDate pd: return (pd, null);
                case JsPlainDateTime pdt: return (CreatePlainDate(realm, pdt.DateTime.Date, pdt.Calendar), null);
            }
            cal = GetCalendarWithISODefault(realm, o);
            var fields = PrepareFields(realm, cal, o, FieldSet.Date | FieldSet.Time | FieldSet.Offset | FieldSet.TimeZone, FieldSet.None, partial: false);
            date = DateFromFields(realm, cal, fields, Overflow.Constrain);
            time = TimeFromFields(realm, fields, Overflow.Constrain);
            tz = fields.TimeZone;
            offsetString = fields.Offset;
            if (offsetString == null) ob = OffsetBehaviour.Wall;
        }
        else
        {
            if (!value.IsString) throw JsErrors.TypeError(realm, "relativeTo must be a Temporal object, property bag or string");
            var r = ParseOrThrow(realm, value.AsStringUnchecked, ParseGoal.RelativeTo);
            offsetString = r.Offset;
            if (r.TimeZone == null) tz = null;
            else
            {
                tz = TemporalTimeZones.Resolve(r.TimeZone) ?? throw JsErrors.RangeError(realm, $"unknown time zone {r.TimeZone}");
                if (r.Z) ob = OffsetBehaviour.Exact;
                else if (offsetString == null) ob = OffsetBehaviour.Wall;
                matchMinutes = !r.OffsetHasSubMinute;
            }
            cal = CalendarFromParse(realm, r);
            date = new IsoDate(r.Year, r.Month, r.Day);
            time = r.HasTime ? r.Time : null;
        }
        if (tz == null) return (CreatePlainDate(realm, date, cal), null);
        long offsetNs = ob == OffsetBehaviour.Option ? ParseOffset(realm, offsetString!) : 0;
        var ns = InterpretISODateTimeOffset(realm, date, time, ob, offsetNs, tz, Disambiguation.Compatible, OffsetOption.Reject, matchMinutes);
        return (null, CreateZonedDateTime(realm, ns, tz, cal));
    }

    public static BigInteger ToBigIntValue(Realm realm, in JsValue v) => BigIntOps.ToBigInt(realm, v);
}
