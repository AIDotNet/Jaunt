using System;

namespace Jaunt.Runtime;

internal static partial class TemporalBuiltins
{
    private static readonly TUnit[] s_noUnits = Array.Empty<TUnit>();
    private static readonly TUnit[] s_weekDay = { TUnit.Week, TUnit.Day };
    private static readonly TUnit[] s_timeUnits = { TUnit.Hour, TUnit.Minute, TUnit.Second, TUnit.Millisecond, TUnit.Microsecond, TUnit.Nanosecond };

    private static IsoDate DateOf(in NativeArgs a, string name) => a.This.Ref switch
    {
        JsPlainDate d => d.Date,
        JsPlainDateTime dt => dt.DateTime.Date,
        JsZonedDateTime z => Temporal.GetISODateTimeFor(z.Tz, z.Ns).Date,
        JsPlainYearMonth ym => ym.Date,
        JsPlainMonthDay md => md.Date,
        _ => throw JsErrors.TypeError(a.Realm, $"{name} called on an incompatible receiver"),
    };

    private static CalendarDate CalOf(in NativeArgs a, string name) => Temporal.CalendarISOToDate(CalendarOf(a, name), DateOf(a, name));

    private static string CalendarOf(in NativeArgs a, string name) => a.This.Ref switch
    {
        JsObject o when Temporal.GetCalendarSlot(o) is { } c => c,
        _ => throw JsErrors.TypeError(a.Realm, $"{name} called on an incompatible receiver"),
    };

    /// <summary>Installs calendarId, era, eraYear, year, month, monthCode, day, dayOfWeek, ... on a prototype.</summary>
    private static void InstallDateGetters<T>(Realm r, JsObject proto) where T : JsObject
    {
        r.Getter(proto, "calendarId", "calendarId", static (in NativeArgs a) => { Check<T>(a, "calendarId"); return Str(CalendarOf(a, "calendarId")); });
        r.Getter(proto, "era", "era", static (in NativeArgs a) => { Check<T>(a, "era"); var e = CalOf(a, "era").Era; return e == null ? JsValue.Undefined : Str(e); });
        r.Getter(proto, "eraYear", "eraYear", static (in NativeArgs a) => { Check<T>(a, "eraYear"); var e = CalOf(a, "eraYear").EraYear; return e == null ? JsValue.Undefined : Num(e.Value); });
        r.Getter(proto, "year", "year", static (in NativeArgs a) => { Check<T>(a, "year"); return Num(CalOf(a, "year").Year); });
        r.Getter(proto, "month", "month", static (in NativeArgs a) => { Check<T>(a, "month"); return Num(CalOf(a, "month").Month); });
        r.Getter(proto, "monthCode", "monthCode", static (in NativeArgs a) => { Check<T>(a, "monthCode"); return Str(CalOf(a, "monthCode").MonthCode); });
        r.Getter(proto, "day", "day", static (in NativeArgs a) => { Check<T>(a, "day"); return Num(CalOf(a, "day").Day); });
        r.Getter(proto, "dayOfWeek", "dayOfWeek", static (in NativeArgs a) => { Check<T>(a, "dayOfWeek"); return Num(TemporalMath.DayOfWeek(DateOf(a, "dayOfWeek"))); });
        r.Getter(proto, "dayOfYear", "dayOfYear", static (in NativeArgs a) => { Check<T>(a, "dayOfYear"); return Num(CalOf(a, "dayOfYear").DayOfYear); });
        r.Getter(proto, "weekOfYear", "weekOfYear", static (in NativeArgs a) => { Check<T>(a, "weekOfYear"); return CalendarOf(a, "weekOfYear") != Temporal.Iso8601 ? JsValue.Undefined : Num(TemporalMath.WeekOfYear(DateOf(a, "weekOfYear")).Week); });
        r.Getter(proto, "yearOfWeek", "yearOfWeek", static (in NativeArgs a) => { Check<T>(a, "yearOfWeek"); return CalendarOf(a, "yearOfWeek") != Temporal.Iso8601 ? JsValue.Undefined : Num(TemporalMath.WeekOfYear(DateOf(a, "yearOfWeek")).Year); });
        r.Getter(proto, "daysInWeek", "daysInWeek", static (in NativeArgs a) => { Check<T>(a, "daysInWeek"); return Num(7); });
        r.Getter(proto, "daysInMonth", "daysInMonth", static (in NativeArgs a) => { Check<T>(a, "daysInMonth"); return Num(CalOf(a, "daysInMonth").DaysInMonth); });
        r.Getter(proto, "daysInYear", "daysInYear", static (in NativeArgs a) => { Check<T>(a, "daysInYear"); return Num(CalOf(a, "daysInYear").DaysInYear); });
        r.Getter(proto, "monthsInYear", "monthsInYear", static (in NativeArgs a) => { Check<T>(a, "monthsInYear"); return Num(CalOf(a, "monthsInYear").MonthsInYear); });
        r.Getter(proto, "inLeapYear", "inLeapYear", static (in NativeArgs a) => { Check<T>(a, "inLeapYear"); return JsValue.FromBool(CalOf(a, "inLeapYear").InLeapYear); });
    }

    private static T Check<T>(in NativeArgs a, string name) where T : JsObject =>
        a.This.Ref as T ?? throw JsErrors.TypeError(a.Realm, $"get {name} called on an incompatible receiver");

    private static TimeRec TimeOf(in NativeArgs a, string name) => a.This.Ref switch
    {
        JsPlainTime t => t.Time,
        JsPlainDateTime dt => dt.DateTime.Time,
        JsZonedDateTime z => Temporal.GetISODateTimeFor(z.Tz, z.Ns).Time,
        _ => throw JsErrors.TypeError(a.Realm, $"{name} called on an incompatible receiver"),
    };

    private static void InstallTimeGetters<T>(Realm r, JsObject proto) where T : JsObject
    {
        r.Getter(proto, "hour", "hour", static (in NativeArgs a) => { Check<T>(a, "hour"); return Num(TimeOf(a, "hour").Hour); });
        r.Getter(proto, "minute", "minute", static (in NativeArgs a) => { Check<T>(a, "minute"); return Num(TimeOf(a, "minute").Minute); });
        r.Getter(proto, "second", "second", static (in NativeArgs a) => { Check<T>(a, "second"); return Num(TimeOf(a, "second").Second); });
        r.Getter(proto, "millisecond", "millisecond", static (in NativeArgs a) => { Check<T>(a, "millisecond"); return Num(TimeOf(a, "millisecond").Millisecond); });
        r.Getter(proto, "microsecond", "microsecond", static (in NativeArgs a) => { Check<T>(a, "microsecond"); return Num(TimeOf(a, "microsecond").Microsecond); });
        r.Getter(proto, "nanosecond", "nanosecond", static (in NativeArgs a) => { Check<T>(a, "nanosecond"); return Num(TimeOf(a, "nanosecond").Nanosecond); });
    }

    private static int ToInt(Realm realm, in JsValue v, int defaultValue = 0) => v.IsUndefined ? defaultValue : ClampInt(TemporalOptions.ToIntegerWithTruncation(realm, v));

    private static int ClampInt(double d) => d > int.MaxValue ? int.MaxValue : d < int.MinValue ? int.MinValue : (int)d;

    // ------------------------------------------------------------------ PlainDate

    private static void SetupPlainDate(Realm r, JsObject temporal, TemporalProtos protos)
    {
        var proto = Proto(r, "PlainDate", out _);
        protos.PlainDate = proto;
        var ctor = r.Constructor("PlainDate", 3, static (in NativeArgs a) =>
        {
            RequireNew(a, "PlainDate");
            var realm = a.Realm;
            double y = TemporalOptions.ToIntegerWithTruncation(realm, a[0]);
            double m = TemporalOptions.ToIntegerWithTruncation(realm, a[1]);
            double d = TemporalOptions.ToIntegerWithTruncation(realm, a[2]);
            var cal = Temporal.CalendarFromConstructorArg(realm, a[3]);
            if (Math.Abs(y) > 1e9 || !TemporalMath.IsValidISODate((long)y, (long)m, (long)d)) throw JsErrors.RangeError(realm, "invalid date");
            return JsValue.FromObject(Temporal.CreatePlainDate(realm, new IsoDate((int)y, (int)m, (int)d), cal, a.NewTarget.AsObjectUnchecked));
        }, proto);
        DefineCtor(temporal, ctor, "PlainDate");
        r.Method(ctor, "from", 1, static (in NativeArgs a) => JsValue.FromObject(Temporal.ToTemporalDate(a.Realm, a[0], a[1])));
        r.Method(ctor, "compare", 2, static (in NativeArgs a) =>
        {
            var one = Temporal.ToTemporalDate(a.Realm, a[0], JsValue.Undefined);
            var two = Temporal.ToTemporalDate(a.Realm, a[1], JsValue.Undefined);
            return Num(TemporalMath.CompareISODate(one.Date, two.Date));
        });
        InstallDateGetters<JsPlainDate>(r, proto);
        r.Method(proto, "toPlainYearMonth", 0, static (in NativeArgs a) =>
        {
            var d = This<JsPlainDate>(a, "Temporal.PlainDate.prototype.toPlainYearMonth");
            var f = Temporal.DateToFields(d.Calendar, d.Date, CalendarType.Date);
            return JsValue.FromObject(Temporal.CreatePlainYearMonth(a.Realm, Temporal.YearMonthFromFields(a.Realm, d.Calendar, f, Overflow.Constrain), d.Calendar));
        });
        r.Method(proto, "toPlainMonthDay", 0, static (in NativeArgs a) =>
        {
            var d = This<JsPlainDate>(a, "Temporal.PlainDate.prototype.toPlainMonthDay");
            var f = Temporal.DateToFields(d.Calendar, d.Date, CalendarType.Date);
            return JsValue.FromObject(Temporal.CreatePlainMonthDay(a.Realm, Temporal.MonthDayFromFields(a.Realm, d.Calendar, f, Overflow.Constrain), d.Calendar));
        });
        r.Method(proto, "add", 1, static (in NativeArgs a) => JsValue.FromObject(PlainDateAdd(a, This<JsPlainDate>(a, "Temporal.PlainDate.prototype.add"), 1)));
        r.Method(proto, "subtract", 1, static (in NativeArgs a) => JsValue.FromObject(PlainDateAdd(a, This<JsPlainDate>(a, "Temporal.PlainDate.prototype.subtract"), -1)));
        r.Method(proto, "with", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var d = This<JsPlainDate>(a, "Temporal.PlainDate.prototype.with");
            if (!a[0].IsObject) throw JsErrors.TypeError(realm, "with requires an object");
            var item = a[0].AsObjectUnchecked;
            Temporal.RejectTemporalLike(realm, item);
            var fields = Temporal.DateToFields(d.Calendar, d.Date, CalendarType.Date);
            var partial = Temporal.PrepareFields(realm, d.Calendar, item, FieldSet.Date, FieldSet.None, partial: true);
            fields = Temporal.MergeFields(d.Calendar, fields, partial);
            var overflow = TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, a[1]));
            return JsValue.FromObject(Temporal.CreatePlainDate(realm, Temporal.DateFromFields(realm, d.Calendar, fields, overflow), d.Calendar));
        });
        r.Method(proto, "withCalendar", 1, static (in NativeArgs a) =>
        {
            var d = This<JsPlainDate>(a, "Temporal.PlainDate.prototype.withCalendar");
            if (a[0].IsUndefined) throw JsErrors.TypeError(a.Realm, "calendar is required");
            return JsValue.FromObject(Temporal.CreatePlainDate(a.Realm, d.Date, Temporal.ToCalendarIdentifier(a.Realm, a[0])));
        });
        r.Method(proto, "until", 1, static (in NativeArgs a) => JsValue.FromObject(PlainDateDiff(a, This<JsPlainDate>(a, "Temporal.PlainDate.prototype.until"), false)));
        r.Method(proto, "since", 1, static (in NativeArgs a) => JsValue.FromObject(PlainDateDiff(a, This<JsPlainDate>(a, "Temporal.PlainDate.prototype.since"), true)));
        r.Method(proto, "equals", 1, static (in NativeArgs a) =>
        {
            var d = This<JsPlainDate>(a, "Temporal.PlainDate.prototype.equals");
            var other = Temporal.ToTemporalDate(a.Realm, a[0], JsValue.Undefined);
            return JsValue.FromBool(TemporalMath.CompareISODate(d.Date, other.Date) == 0 && d.Calendar == other.Calendar);
        });
        r.Method(proto, "toPlainDateTime", 0, static (in NativeArgs a) =>
        {
            var d = This<JsPlainDate>(a, "Temporal.PlainDate.prototype.toPlainDateTime");
            var t = Temporal.ToTimeOrMidnight(a.Realm, a[0]);
            return JsValue.FromObject(Temporal.CreatePlainDateTime(a.Realm, new IsoDateTime(d.Date, t), d.Calendar));
        });
        r.Method(proto, "toZonedDateTime", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var d = This<JsPlainDate>(a, "Temporal.PlainDate.prototype.toZonedDateTime");
            TemporalTz tz;
            JsValue temporalTime = JsValue.Undefined;
            if (a[0].IsObject && a[0].AsObjectUnchecked is not JsZonedDateTime)
            {
                var item = a[0].AsObjectUnchecked;
                var tzLike = item.Get("timeZone");
                if (tzLike.IsUndefined) tz = Temporal.ToTimeZone(realm, a[0]);
                else
                {
                    tz = Temporal.ToTimeZone(realm, tzLike);
                    temporalTime = item.Get("plainTime");
                }
            }
            else tz = Temporal.ToTimeZone(realm, a[0]);
            Int128 ns;
            if (temporalTime.IsUndefined) ns = TemporalTimeZones.GetStartOfDay(realm, tz, d.Date);
            else
            {
                var t = Temporal.ToTemporalTime(realm, temporalTime, JsValue.Undefined).Time;
                var dt = new IsoDateTime(d.Date, t);
                if (!TemporalMath.ISODateTimeWithinLimits(dt)) throw JsErrors.RangeError(realm, "date-time is outside the supported range");
                ns = TemporalTimeZones.GetEpochNsFor(realm, tz, dt, Disambiguation.Compatible);
            }
            return JsValue.FromObject(Temporal.CreateZonedDateTime(realm, ns, tz, d.Calendar));
        });
        r.Method(proto, "toString", 0, static (in NativeArgs a) =>
        {
            var d = This<JsPlainDate>(a, "Temporal.PlainDate.prototype.toString");
            var show = TemporalOptions.GetShowCalendarName(a.Realm, TemporalOptions.GetOptionsObject(a.Realm, a[0]));
            return Str(Temporal.DateToString(d.Date) + Temporal.CalendarAnnotation(d.Calendar, show));
        });
        r.Method(proto, "toJSON", 0, static (in NativeArgs a) => { var d = This<JsPlainDate>(a, "Temporal.PlainDate.prototype.toJSON"); return Str(Temporal.DateToString(d.Date) + Temporal.CalendarAnnotation(d.Calendar, "auto")); });
        r.Method(proto, "toLocaleString", 0, static (in NativeArgs a) => { var d = This<JsPlainDate>(a, "Temporal.PlainDate.prototype.toLocaleString"); if (a.Realm.Intl != null) return Str(IntlDateTimeFormat.FormatTemporalToLocaleString(a.Realm, d, a[0], a[1])); return Str(Temporal.DateToString(d.Date) + Temporal.CalendarAnnotation(d.Calendar, "auto")); });
        r.Method(proto, "valueOf", 0, static (in NativeArgs a) => ValueOf(a));
    }

    private static JsPlainDate PlainDateAdd(in NativeArgs a, JsPlainDate d, int sign)
    {
        var realm = a.Realm;
        var dur = Temporal.ToTemporalDuration(realm, a[0]);
        var f = dur.F;
        if (sign < 0) { f = (double[])f.Clone(); for (int i = 0; i < 10; i++) f[i] = -f[i]; }
        var overflow = TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, a[1]));
        var dd = Temporal.ToDateDurationWithoutTime(realm, f);
        return Temporal.CreatePlainDate(realm, Temporal.CalendarDateAdd(realm, d.Calendar, d.Date, dd, overflow), d.Calendar);
    }

    private static JsDuration PlainDateDiff(in NativeArgs a, JsPlainDate d, bool since)
    {
        var realm = a.Realm;
        var other = Temporal.ToTemporalDate(realm, a[0], JsValue.Undefined);
        if (other.Calendar != d.Calendar) throw JsErrors.RangeError(realm, "cannot compute the difference between dates of different calendars");
        var opts = TemporalOptions.GetOptionsObject(realm, a[1]);
        var s = GetDifferenceSettings(realm, since, opts, TemporalOptions.UnitGroup.Date, s_noUnits, TUnit.Day, TUnit.Day);
        if (TemporalMath.CompareISODate(d.Date, other.Date) == 0) return Temporal.CreateDuration(realm, new double[10]);
        var dateDiff = Temporal.CalendarDateUntil(d.Calendar, d.Date, other.Date, s.LargestUnit);
        var duration = new InternalDur(dateDiff, 0);
        if (s.SmallestUnit != TUnit.Day || s.Increment != 1)
        {
            var start = new IsoDateTime(d.Date, TimeRec.Midnight);
            var dest = TemporalMath.GetUTCEpochNanoseconds(new IsoDateTime(other.Date, TimeRec.Midnight));
            duration = Temporal.RoundRelativeDuration(realm, d.Calendar, duration, dest, start, null, s.LargestUnit, s.Increment, s.SmallestUnit, s.Mode);
        }
        return NegateIf(realm, Temporal.FromInternal(realm, duration, TUnit.Day), since);
    }

    // ------------------------------------------------------------------ PlainTime

    private static void SetupPlainTime(Realm r, JsObject temporal, TemporalProtos protos)
    {
        var proto = Proto(r, "PlainTime", out _);
        protos.PlainTime = proto;
        var ctor = r.Constructor("PlainTime", 0, static (in NativeArgs a) =>
        {
            RequireNew(a, "PlainTime");
            var realm = a.Realm;
            var v = new double[6];
            for (int i = 0; i < 6; i++) v[i] = a[i].IsUndefined ? 0 : TemporalOptions.ToIntegerWithTruncation(realm, a[i]);
            if (!TemporalMath.IsValidTime(v[0], v[1], v[2], v[3], v[4], v[5])) throw JsErrors.RangeError(realm, "invalid time");
            return JsValue.FromObject(Temporal.CreatePlainTime(realm, new TimeRec((int)v[0], (int)v[1], (int)v[2], (int)v[3], (int)v[4], (int)v[5]), a.NewTarget.AsObjectUnchecked));
        }, proto);
        DefineCtor(temporal, ctor, "PlainTime");
        r.Method(ctor, "from", 1, static (in NativeArgs a) => JsValue.FromObject(Temporal.ToTemporalTime(a.Realm, a[0], a[1])));
        r.Method(ctor, "compare", 2, static (in NativeArgs a) =>
        {
            var one = Temporal.ToTemporalTime(a.Realm, a[0], JsValue.Undefined);
            var two = Temporal.ToTemporalTime(a.Realm, a[1], JsValue.Undefined);
            return Num(TemporalMath.CompareTime(one.Time, two.Time));
        });
        InstallTimeGetters<JsPlainTime>(r, proto);
        r.Method(proto, "add", 1, static (in NativeArgs a) => JsValue.FromObject(PlainTimeAdd(a, This<JsPlainTime>(a, "Temporal.PlainTime.prototype.add"), 1)));
        r.Method(proto, "subtract", 1, static (in NativeArgs a) => JsValue.FromObject(PlainTimeAdd(a, This<JsPlainTime>(a, "Temporal.PlainTime.prototype.subtract"), -1)));
        r.Method(proto, "with", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var t = This<JsPlainTime>(a, "Temporal.PlainTime.prototype.with");
            if (!a[0].IsObject) throw JsErrors.TypeError(realm, "with requires an object");
            var item = a[0].AsObjectUnchecked;
            Temporal.RejectTemporalLike(realm, item);
            var cur = new double[] { t.Time.Hour, t.Time.Minute, t.Time.Second, t.Time.Millisecond, t.Time.Microsecond, t.Time.Nanosecond };
            var v = Temporal.ToTimeRecord(realm, item, partial: true, cur);
            var overflow = TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, a[1]));
            return JsValue.FromObject(Temporal.CreatePlainTime(realm, Temporal.RegulateTime(realm, v[0], v[1], v[2], v[3], v[4], v[5], overflow)));
        });
        r.Method(proto, "until", 1, static (in NativeArgs a) => JsValue.FromObject(PlainTimeDiff(a, This<JsPlainTime>(a, "Temporal.PlainTime.prototype.until"), false)));
        r.Method(proto, "since", 1, static (in NativeArgs a) => JsValue.FromObject(PlainTimeDiff(a, This<JsPlainTime>(a, "Temporal.PlainTime.prototype.since"), true)));
        r.Method(proto, "round", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var t = This<JsPlainTime>(a, "Temporal.PlainTime.prototype.round");
            var roundTo = RoundToOptions(realm, a[0]);
            long increment = TemporalOptions.GetRoundingIncrement(realm, roundTo);
            var mode = TemporalOptions.GetRoundingMode(realm, roundTo, RoundingMode.HalfExpand);
            var smallest = TemporalOptions.GetUnit(realm, roundTo, "smallestUnit");
            if (smallest == TUnit.Unset) throw JsErrors.RangeError(realm, "smallestUnit is required");
            TemporalOptions.ValidateUnit(realm, smallest, TemporalOptions.UnitGroup.Time, "smallestUnit");
            TemporalOptions.ValidateIncrement(realm, increment, TemporalOptions.MaxIncrement(smallest), inclusive: false);
            var (_, rounded) = TemporalMath.RoundTime(t.Time, increment, smallest, mode);
            return JsValue.FromObject(Temporal.CreatePlainTime(realm, rounded));
        });
        r.Method(proto, "equals", 1, static (in NativeArgs a) =>
        {
            var t = This<JsPlainTime>(a, "Temporal.PlainTime.prototype.equals");
            var other = Temporal.ToTemporalTime(a.Realm, a[0], JsValue.Undefined);
            return JsValue.FromBool(TemporalMath.CompareTime(t.Time, other.Time) == 0);
        });
        r.Method(proto, "toString", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var t = This<JsPlainTime>(a, "Temporal.PlainTime.prototype.toString");
            var opts = TemporalOptions.GetOptionsObject(realm, a[0]);
            var (p, mode) = TimeStringOptions(realm, opts, out _, readCalendar: false);
            var (_, rounded) = TemporalMath.RoundTime(t.Time, p.Increment, p.Unit, mode);
            return Str(Temporal.TimeToString(rounded, p.Precision));
        });
        r.Method(proto, "toJSON", 0, static (in NativeArgs a) => Str(Temporal.TimeToString(This<JsPlainTime>(a, "Temporal.PlainTime.prototype.toJSON").Time, -1)));
        r.Method(proto, "toLocaleString", 0, static (in NativeArgs a) =>
        {
            var t = This<JsPlainTime>(a, "Temporal.PlainTime.prototype.toLocaleString");
            if (a.Realm.Intl != null) return Str(IntlDateTimeFormat.FormatTemporalToLocaleString(a.Realm, t, a[0], a[1]));
            return Str(Temporal.TimeToString(t.Time, -1));
        });
        r.Method(proto, "valueOf", 0, static (in NativeArgs a) => ValueOf(a));
    }

    private static JsPlainTime PlainTimeAdd(in NativeArgs a, JsPlainTime t, int sign)
    {
        var realm = a.Realm;
        var dur = Temporal.ToTemporalDuration(realm, a[0]);
        var internalDur = Temporal.ToInternal(dur.F);
        var (_, time) = TemporalMath.AddTime(t.Time, sign < 0 ? -internalDur.Time : internalDur.Time);
        return Temporal.CreatePlainTime(realm, time);
    }

    private static JsDuration PlainTimeDiff(in NativeArgs a, JsPlainTime t, bool since)
    {
        var realm = a.Realm;
        var other = Temporal.ToTemporalTime(realm, a[0], JsValue.Undefined);
        var opts = TemporalOptions.GetOptionsObject(realm, a[1]);
        var s = GetDifferenceSettings(realm, since, opts, TemporalOptions.UnitGroup.Time, s_noUnits, TUnit.Nanosecond, TUnit.Hour);
        Int128 diff = other.Time.TotalNs - t.Time.TotalNs;
        diff = Temporal.RoundTimeDuration(realm, diff, s.Increment, s.SmallestUnit, s.Mode);
        return NegateIf(realm, Temporal.FromInternal(realm, new InternalDur(DateDur.Zero, diff), s.LargestUnit), since);
    }

    // ------------------------------------------------------------------ PlainDateTime

    private static void SetupPlainDateTime(Realm r, JsObject temporal, TemporalProtos protos)
    {
        var proto = Proto(r, "PlainDateTime", out _);
        protos.PlainDateTime = proto;
        var ctor = r.Constructor("PlainDateTime", 3, static (in NativeArgs a) =>
        {
            RequireNew(a, "PlainDateTime");
            var realm = a.Realm;
            double y = TemporalOptions.ToIntegerWithTruncation(realm, a[0]);
            double mo = TemporalOptions.ToIntegerWithTruncation(realm, a[1]);
            double d = TemporalOptions.ToIntegerWithTruncation(realm, a[2]);
            var v = new double[6];
            for (int i = 0; i < 6; i++) v[i] = a[3 + i].IsUndefined ? 0 : TemporalOptions.ToIntegerWithTruncation(realm, a[3 + i]);
            var cal = Temporal.CalendarFromConstructorArg(realm, a[9]);
            if (Math.Abs(y) > 1e9 || !TemporalMath.IsValidISODate((long)y, (long)mo, (long)d)) throw JsErrors.RangeError(realm, "invalid date");
            if (!TemporalMath.IsValidTime(v[0], v[1], v[2], v[3], v[4], v[5])) throw JsErrors.RangeError(realm, "invalid time");
            var dt = new IsoDateTime(new IsoDate((int)y, (int)mo, (int)d), new TimeRec((int)v[0], (int)v[1], (int)v[2], (int)v[3], (int)v[4], (int)v[5]));
            return JsValue.FromObject(Temporal.CreatePlainDateTime(realm, dt, cal, a.NewTarget.AsObjectUnchecked));
        }, proto);
        DefineCtor(temporal, ctor, "PlainDateTime");
        r.Method(ctor, "from", 1, static (in NativeArgs a) => JsValue.FromObject(Temporal.ToTemporalDateTime(a.Realm, a[0], a[1])));
        r.Method(ctor, "compare", 2, static (in NativeArgs a) =>
        {
            var one = Temporal.ToTemporalDateTime(a.Realm, a[0], JsValue.Undefined);
            var two = Temporal.ToTemporalDateTime(a.Realm, a[1], JsValue.Undefined);
            return Num(TemporalMath.CompareISODateTime(one.DateTime, two.DateTime));
        });
        InstallDateGetters<JsPlainDateTime>(r, proto);
        InstallTimeGetters<JsPlainDateTime>(r, proto);
        r.Method(proto, "with", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var dt = This<JsPlainDateTime>(a, "Temporal.PlainDateTime.prototype.with");
            if (!a[0].IsObject) throw JsErrors.TypeError(realm, "with requires an object");
            var item = a[0].AsObjectUnchecked;
            Temporal.RejectTemporalLike(realm, item);
            var fields = Temporal.DateToFields(dt.Calendar, dt.DateTime.Date, CalendarType.Date);
            Temporal.SetTimeFields(fields, dt.DateTime.Time);
            var partial = Temporal.PrepareFields(realm, dt.Calendar, item, FieldSet.Date | FieldSet.Time, FieldSet.None, partial: true);
            fields = Temporal.MergeFields(dt.Calendar, fields, partial);
            var overflow = TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, a[1]));
            var date = Temporal.DateFromFields(realm, dt.Calendar, fields, overflow);
            var time = Temporal.TimeFromFields(realm, fields, overflow);
            return JsValue.FromObject(Temporal.CreatePlainDateTime(realm, new IsoDateTime(date, time), dt.Calendar));
        });
        r.Method(proto, "withPlainTime", 0, static (in NativeArgs a) =>
        {
            var dt = This<JsPlainDateTime>(a, "Temporal.PlainDateTime.prototype.withPlainTime");
            var t = Temporal.ToTimeOrMidnight(a.Realm, a[0]);
            return JsValue.FromObject(Temporal.CreatePlainDateTime(a.Realm, new IsoDateTime(dt.DateTime.Date, t), dt.Calendar));
        });
        r.Method(proto, "withCalendar", 1, static (in NativeArgs a) =>
        {
            var dt = This<JsPlainDateTime>(a, "Temporal.PlainDateTime.prototype.withCalendar");
            if (a[0].IsUndefined) throw JsErrors.TypeError(a.Realm, "calendar is required");
            return JsValue.FromObject(Temporal.CreatePlainDateTime(a.Realm, dt.DateTime, Temporal.ToCalendarIdentifier(a.Realm, a[0])));
        });
        r.Method(proto, "add", 1, static (in NativeArgs a) => JsValue.FromObject(PlainDateTimeAdd(a, This<JsPlainDateTime>(a, "Temporal.PlainDateTime.prototype.add"), 1)));
        r.Method(proto, "subtract", 1, static (in NativeArgs a) => JsValue.FromObject(PlainDateTimeAdd(a, This<JsPlainDateTime>(a, "Temporal.PlainDateTime.prototype.subtract"), -1)));
        r.Method(proto, "until", 1, static (in NativeArgs a) => JsValue.FromObject(PlainDateTimeDiff(a, This<JsPlainDateTime>(a, "Temporal.PlainDateTime.prototype.until"), false)));
        r.Method(proto, "since", 1, static (in NativeArgs a) => JsValue.FromObject(PlainDateTimeDiff(a, This<JsPlainDateTime>(a, "Temporal.PlainDateTime.prototype.since"), true)));
        r.Method(proto, "round", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var dt = This<JsPlainDateTime>(a, "Temporal.PlainDateTime.prototype.round");
            var roundTo = RoundToOptions(realm, a[0]);
            long increment = TemporalOptions.GetRoundingIncrement(realm, roundTo);
            var mode = TemporalOptions.GetRoundingMode(realm, roundTo, RoundingMode.HalfExpand);
            var smallest = TemporalOptions.GetUnit(realm, roundTo, "smallestUnit");
            if (smallest == TUnit.Unset) throw JsErrors.RangeError(realm, "smallestUnit is required");
            TemporalOptions.ValidateUnit(realm, smallest, TemporalOptions.UnitGroup.Time, "smallestUnit", extra: TUnit.Day);
            if (smallest == TUnit.Day) TemporalOptions.ValidateIncrement(realm, increment, 1, inclusive: true);
            else TemporalOptions.ValidateIncrement(realm, increment, TemporalOptions.MaxIncrement(smallest), inclusive: false);
            if (smallest == TUnit.Nanosecond && increment == 1) return JsValue.FromObject(Temporal.CreatePlainDateTime(realm, dt.DateTime, dt.Calendar));
            return JsValue.FromObject(Temporal.CreatePlainDateTime(realm, RoundISODateTime(dt.DateTime, increment, smallest, mode), dt.Calendar));
        });
        r.Method(proto, "equals", 1, static (in NativeArgs a) =>
        {
            var dt = This<JsPlainDateTime>(a, "Temporal.PlainDateTime.prototype.equals");
            var other = Temporal.ToTemporalDateTime(a.Realm, a[0], JsValue.Undefined);
            return JsValue.FromBool(TemporalMath.CompareISODateTime(dt.DateTime, other.DateTime) == 0 && dt.Calendar == other.Calendar);
        });
        r.Method(proto, "toPlainDate", 0, static (in NativeArgs a) =>
        {
            var dt = This<JsPlainDateTime>(a, "Temporal.PlainDateTime.prototype.toPlainDate");
            return JsValue.FromObject(Temporal.CreatePlainDate(a.Realm, dt.DateTime.Date, dt.Calendar));
        });
        r.Method(proto, "toPlainTime", 0, static (in NativeArgs a) =>
            JsValue.FromObject(Temporal.CreatePlainTime(a.Realm, This<JsPlainDateTime>(a, "Temporal.PlainDateTime.prototype.toPlainTime").DateTime.Time)));
        r.Method(proto, "toZonedDateTime", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var dt = This<JsPlainDateTime>(a, "Temporal.PlainDateTime.prototype.toZonedDateTime");
            var tz = Temporal.ToTimeZone(realm, a[0]);
            var d = TemporalOptions.GetDisambiguation(realm, TemporalOptions.GetOptionsObject(realm, a[1]));
            var ns = TemporalTimeZones.GetEpochNsFor(realm, tz, dt.DateTime, d);
            return JsValue.FromObject(Temporal.CreateZonedDateTime(realm, ns, tz, dt.Calendar));
        });
        r.Method(proto, "toString", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var dt = This<JsPlainDateTime>(a, "Temporal.PlainDateTime.prototype.toString");
            var opts = TemporalOptions.GetOptionsObject(realm, a[0]);
            var (p, mode) = TimeStringOptions(realm, opts, out var showCal, readCalendar: true);
            var rounded = RoundISODateTime(dt.DateTime, p.Increment, p.Unit, mode);
            if (!TemporalMath.ISODateTimeWithinLimits(rounded)) throw JsErrors.RangeError(realm, "rounded date-time is outside the supported range");
            return Str(Temporal.DateTimeToString(rounded, dt.Calendar, p.Precision, showCal));
        });
        r.Method(proto, "toJSON", 0, static (in NativeArgs a) => { var dt = This<JsPlainDateTime>(a, "Temporal.PlainDateTime.prototype.toJSON"); return Str(Temporal.DateTimeToString(dt.DateTime, dt.Calendar, -1, "auto")); });
        r.Method(proto, "toLocaleString", 0, static (in NativeArgs a) => { var dt = This<JsPlainDateTime>(a, "Temporal.PlainDateTime.prototype.toLocaleString"); if (a.Realm.Intl != null) return Str(IntlDateTimeFormat.FormatTemporalToLocaleString(a.Realm, dt, a[0], a[1])); return Str(Temporal.DateTimeToString(dt.DateTime, dt.Calendar, -1, "auto")); });
        r.Method(proto, "valueOf", 0, static (in NativeArgs a) => ValueOf(a));
    }

    internal static IsoDateTime RoundISODateTime(IsoDateTime dt, long increment, TUnit unit, RoundingMode mode)
    {
        var (days, time) = TemporalMath.RoundTime(dt.Time, increment, unit, mode);
        return new IsoDateTime(TemporalMath.AddDays(dt.Date, days), time);
    }

    private static JsPlainDateTime PlainDateTimeAdd(in NativeArgs a, JsPlainDateTime dt, int sign)
    {
        var realm = a.Realm;
        var dur = Temporal.ToTemporalDuration(realm, a[0]);
        var f = dur.F;
        if (sign < 0) { f = (double[])f.Clone(); for (int i = 0; i < 10; i++) f[i] = -f[i]; }
        var overflow = TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, a[1]));
        var internalDur = Temporal.ToInternalWith24HourDays(realm, f);
        var (days, time) = TemporalMath.AddTime(dt.DateTime.Time, internalDur.Time);
        var dateDur = internalDur.Date with { Days = days };
        var added = Temporal.CalendarDateAdd(realm, dt.Calendar, dt.DateTime.Date, dateDur, overflow);
        return Temporal.CreatePlainDateTime(realm, new IsoDateTime(added, time), dt.Calendar);
    }

    private static JsDuration PlainDateTimeDiff(in NativeArgs a, JsPlainDateTime dt, bool since)
    {
        var realm = a.Realm;
        var other = Temporal.ToTemporalDateTime(realm, a[0], JsValue.Undefined);
        if (other.Calendar != dt.Calendar) throw JsErrors.RangeError(realm, "cannot compute the difference between date-times of different calendars");
        var opts = TemporalOptions.GetOptionsObject(realm, a[1]);
        var s = GetDifferenceSettings(realm, since, opts, TemporalOptions.UnitGroup.DateTime, s_noUnits, TUnit.Nanosecond, TUnit.Day);
        if (TemporalMath.CompareISODateTime(dt.DateTime, other.DateTime) == 0) return Temporal.CreateDuration(realm, new double[10]);
        var internalDur = Temporal.DifferencePlainDateTimeWithRounding(realm, dt.Calendar, dt.DateTime, other.DateTime, s.LargestUnit, s.Increment, s.SmallestUnit, s.Mode);
        return NegateIf(realm, Temporal.FromInternal(realm, internalDur, s.LargestUnit), since);
    }

    // ------------------------------------------------------------------ PlainYearMonth

    private static void SetupPlainYearMonth(Realm r, JsObject temporal, TemporalProtos protos)
    {
        var proto = Proto(r, "PlainYearMonth", out _);
        protos.PlainYearMonth = proto;
        var ctor = r.Constructor("PlainYearMonth", 2, static (in NativeArgs a) =>
        {
            RequireNew(a, "PlainYearMonth");
            var realm = a.Realm;
            double y = TemporalOptions.ToIntegerWithTruncation(realm, a[0]);
            double m = TemporalOptions.ToIntegerWithTruncation(realm, a[1]);
            var cal = Temporal.CalendarFromConstructorArg(realm, a[2]);
            double refDay = a[3].IsUndefined ? 1 : TemporalOptions.ToIntegerWithTruncation(realm, a[3]);
            if (Math.Abs(y) > 1e9 || !TemporalMath.IsValidISODate((long)y, (long)m, (long)refDay)) throw JsErrors.RangeError(realm, "invalid year-month");
            return JsValue.FromObject(Temporal.CreatePlainYearMonth(realm, new IsoDate((int)y, (int)m, (int)refDay), cal, a.NewTarget.AsObjectUnchecked));
        }, proto);
        DefineCtor(temporal, ctor, "PlainYearMonth");
        r.Method(ctor, "from", 1, static (in NativeArgs a) => JsValue.FromObject(Temporal.ToTemporalYearMonth(a.Realm, a[0], a[1])));
        r.Method(ctor, "compare", 2, static (in NativeArgs a) =>
        {
            var one = Temporal.ToTemporalYearMonth(a.Realm, a[0], JsValue.Undefined);
            var two = Temporal.ToTemporalYearMonth(a.Realm, a[1], JsValue.Undefined);
            return Num(TemporalMath.CompareISODate(one.Date, two.Date));
        });
        r.Getter(proto, "calendarId", "calendarId", static (in NativeArgs a) => Str(This<JsPlainYearMonth>(a, "calendarId").Calendar));
        r.Getter(proto, "era", "era", static (in NativeArgs a) => { This<JsPlainYearMonth>(a, "era"); var e = CalOf(a, "era").Era; return e == null ? JsValue.Undefined : Str(e); });
        r.Getter(proto, "eraYear", "eraYear", static (in NativeArgs a) => { This<JsPlainYearMonth>(a, "eraYear"); var e = CalOf(a, "eraYear").EraYear; return e == null ? JsValue.Undefined : Num(e.Value); });
        r.Getter(proto, "year", "year", static (in NativeArgs a) => { This<JsPlainYearMonth>(a, "year"); return Num(CalOf(a, "year").Year); });
        r.Getter(proto, "month", "month", static (in NativeArgs a) => { This<JsPlainYearMonth>(a, "month"); return Num(CalOf(a, "month").Month); });
        r.Getter(proto, "monthCode", "monthCode", static (in NativeArgs a) => { This<JsPlainYearMonth>(a, "monthCode"); return Str(CalOf(a, "monthCode").MonthCode); });
        r.Getter(proto, "daysInMonth", "daysInMonth", static (in NativeArgs a) => { This<JsPlainYearMonth>(a, "daysInMonth"); return Num(CalOf(a, "daysInMonth").DaysInMonth); });
        r.Getter(proto, "daysInYear", "daysInYear", static (in NativeArgs a) => { This<JsPlainYearMonth>(a, "daysInYear"); return Num(CalOf(a, "daysInYear").DaysInYear); });
        r.Getter(proto, "monthsInYear", "monthsInYear", static (in NativeArgs a) => { This<JsPlainYearMonth>(a, "monthsInYear"); return Num(CalOf(a, "monthsInYear").MonthsInYear); });
        r.Getter(proto, "inLeapYear", "inLeapYear", static (in NativeArgs a) => { This<JsPlainYearMonth>(a, "inLeapYear"); return JsValue.FromBool(CalOf(a, "inLeapYear").InLeapYear); });
        r.Method(proto, "with", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var ym = This<JsPlainYearMonth>(a, "Temporal.PlainYearMonth.prototype.with");
            if (!a[0].IsObject) throw JsErrors.TypeError(realm, "with requires an object");
            var item = a[0].AsObjectUnchecked;
            Temporal.RejectTemporalLike(realm, item);
            var fields = Temporal.DateToFields(ym.Calendar, ym.Date, CalendarType.YearMonth);
            var partial = Temporal.PrepareFields(realm, ym.Calendar, item, FieldSet.YearMonth, FieldSet.None, partial: true);
            fields = Temporal.MergeFields(ym.Calendar, fields, partial);
            var overflow = TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, a[1]));
            return JsValue.FromObject(Temporal.CreatePlainYearMonth(realm, Temporal.YearMonthFromFields(realm, ym.Calendar, fields, overflow), ym.Calendar));
        });
        r.Method(proto, "add", 1, static (in NativeArgs a) => JsValue.FromObject(YearMonthAdd(a, This<JsPlainYearMonth>(a, "Temporal.PlainYearMonth.prototype.add"), 1)));
        r.Method(proto, "subtract", 1, static (in NativeArgs a) => JsValue.FromObject(YearMonthAdd(a, This<JsPlainYearMonth>(a, "Temporal.PlainYearMonth.prototype.subtract"), -1)));
        r.Method(proto, "until", 1, static (in NativeArgs a) => JsValue.FromObject(YearMonthDiff(a, This<JsPlainYearMonth>(a, "Temporal.PlainYearMonth.prototype.until"), false)));
        r.Method(proto, "since", 1, static (in NativeArgs a) => JsValue.FromObject(YearMonthDiff(a, This<JsPlainYearMonth>(a, "Temporal.PlainYearMonth.prototype.since"), true)));
        r.Method(proto, "equals", 1, static (in NativeArgs a) =>
        {
            var ym = This<JsPlainYearMonth>(a, "Temporal.PlainYearMonth.prototype.equals");
            var other = Temporal.ToTemporalYearMonth(a.Realm, a[0], JsValue.Undefined);
            return JsValue.FromBool(TemporalMath.CompareISODate(ym.Date, other.Date) == 0 && ym.Calendar == other.Calendar);
        });
        r.Method(proto, "toPlainDate", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var ym = This<JsPlainYearMonth>(a, "Temporal.PlainYearMonth.prototype.toPlainDate");
            if (!a[0].IsObject) throw JsErrors.TypeError(realm, "toPlainDate requires an object with a day");
            var fields = Temporal.DateToFields(ym.Calendar, ym.Date, CalendarType.YearMonth);
            var input = Temporal.PrepareFields(realm, ym.Calendar, a[0].AsObjectUnchecked, FieldSet.Day, FieldSet.None, partial: false);
            fields = Temporal.MergeFields(ym.Calendar, fields, input);
            return JsValue.FromObject(Temporal.CreatePlainDate(realm, Temporal.DateFromFields(realm, ym.Calendar, fields, Overflow.Constrain), ym.Calendar));
        });
        r.Method(proto, "toString", 0, static (in NativeArgs a) =>
        {
            var ym = This<JsPlainYearMonth>(a, "Temporal.PlainYearMonth.prototype.toString");
            var show = TemporalOptions.GetShowCalendarName(a.Realm, TemporalOptions.GetOptionsObject(a.Realm, a[0]));
            return Str(YearMonthToString(ym, show));
        });
        r.Method(proto, "toJSON", 0, static (in NativeArgs a) => Str(YearMonthToString(This<JsPlainYearMonth>(a, "Temporal.PlainYearMonth.prototype.toJSON"), "auto")));
        r.Method(proto, "toLocaleString", 0, static (in NativeArgs a) =>
        {
            var ym = This<JsPlainYearMonth>(a, "Temporal.PlainYearMonth.prototype.toLocaleString");
            if (a.Realm.Intl != null) return Str(IntlDateTimeFormat.FormatTemporalToLocaleString(a.Realm, ym, a[0], a[1]));
            return Str(YearMonthToString(ym, "auto"));
        });
        r.Method(proto, "valueOf", 0, static (in NativeArgs a) => ValueOf(a));
    }

    private static string YearMonthToString(JsPlainYearMonth ym, string show)
    {
        var s = Temporal.PadYear(ym.Date.Year) + "-" + ym.Date.Month.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        if (show is "always" or "critical" || ym.Calendar != Temporal.Iso8601)
            s += "-" + ym.Date.Day.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        return s + Temporal.CalendarAnnotation(ym.Calendar, show);
    }

    private static JsPlainYearMonth YearMonthAdd(in NativeArgs a, JsPlainYearMonth ym, int sign)
    {
        var realm = a.Realm;
        var dur = Temporal.ToTemporalDuration(realm, a[0]);
        var f = dur.F;
        if (sign < 0) { f = (double[])f.Clone(); for (int i = 0; i < 10; i++) f[i] = -f[i]; }
        var overflow = TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, a[1]));
        for (int i = 2; i < 10; i++)
            if (f[i] != 0) throw JsErrors.RangeError(realm, "only years and months can be added to a Temporal.PlainYearMonth");
        var fields = Temporal.DateToFields(ym.Calendar, ym.Date, CalendarType.YearMonth);
        fields.Day = 1;
        var intermediate = Temporal.DateFromFields(realm, ym.Calendar, fields, Overflow.Constrain);
        var added = Temporal.CalendarDateAdd(realm, ym.Calendar, intermediate, new DateDur(f[0], f[1], 0, 0), overflow);
        var addedFields = Temporal.DateToFields(ym.Calendar, added, CalendarType.YearMonth);
        return Temporal.CreatePlainYearMonth(realm, Temporal.YearMonthFromFields(realm, ym.Calendar, addedFields, overflow), ym.Calendar);
    }

    private static JsDuration YearMonthDiff(in NativeArgs a, JsPlainYearMonth ym, bool since)
    {
        var realm = a.Realm;
        var other = Temporal.ToTemporalYearMonth(realm, a[0], JsValue.Undefined);
        if (other.Calendar != ym.Calendar) throw JsErrors.RangeError(realm, "cannot compute the difference between year-months of different calendars");
        var opts = TemporalOptions.GetOptionsObject(realm, a[1]);
        var s = GetDifferenceSettings(realm, since, opts, TemporalOptions.UnitGroup.Date, s_weekDay, TUnit.Month, TUnit.Year);
        if (TemporalMath.CompareISODate(ym.Date, other.Date) == 0) return Temporal.CreateDuration(realm, new double[10]);
        var thisFields = Temporal.DateToFields(ym.Calendar, ym.Date, CalendarType.YearMonth);
        thisFields.Day = 1;
        var thisDate = Temporal.DateFromFields(realm, ym.Calendar, thisFields, Overflow.Constrain);
        var otherFields = Temporal.DateToFields(ym.Calendar, other.Date, CalendarType.YearMonth);
        otherFields.Day = 1;
        var otherDate = Temporal.DateFromFields(realm, ym.Calendar, otherFields, Overflow.Constrain);
        var dateDiff = Temporal.CalendarDateUntil(ym.Calendar, thisDate, otherDate, s.LargestUnit);
        var duration = new InternalDur(new DateDur(dateDiff.Years, dateDiff.Months, 0, 0), 0);
        if (s.SmallestUnit != TUnit.Month || s.Increment != 1)
        {
            var start = new IsoDateTime(thisDate, TimeRec.Midnight);
            var dest = TemporalMath.GetUTCEpochNanoseconds(new IsoDateTime(otherDate, TimeRec.Midnight));
            duration = Temporal.RoundRelativeDuration(realm, ym.Calendar, duration, dest, start, null, s.LargestUnit, s.Increment, s.SmallestUnit, s.Mode);
        }
        return NegateIf(realm, Temporal.FromInternal(realm, duration, TUnit.Day), since);
    }

    // ------------------------------------------------------------------ PlainMonthDay

    private static void SetupPlainMonthDay(Realm r, JsObject temporal, TemporalProtos protos)
    {
        var proto = Proto(r, "PlainMonthDay", out _);
        protos.PlainMonthDay = proto;
        var ctor = r.Constructor("PlainMonthDay", 2, static (in NativeArgs a) =>
        {
            RequireNew(a, "PlainMonthDay");
            var realm = a.Realm;
            double m = TemporalOptions.ToIntegerWithTruncation(realm, a[0]);
            double d = TemporalOptions.ToIntegerWithTruncation(realm, a[1]);
            var cal = Temporal.CalendarFromConstructorArg(realm, a[2]);
            double refYear = a[3].IsUndefined ? 1972 : TemporalOptions.ToIntegerWithTruncation(realm, a[3]);
            if (Math.Abs(refYear) > 1e9 || !TemporalMath.IsValidISODate((long)refYear, (long)m, (long)d)) throw JsErrors.RangeError(realm, "invalid month-day");
            return JsValue.FromObject(Temporal.CreatePlainMonthDay(realm, new IsoDate((int)refYear, (int)m, (int)d), cal, a.NewTarget.AsObjectUnchecked));
        }, proto);
        DefineCtor(temporal, ctor, "PlainMonthDay");
        r.Method(ctor, "from", 1, static (in NativeArgs a) => JsValue.FromObject(Temporal.ToTemporalMonthDay(a.Realm, a[0], a[1])));
        r.Getter(proto, "calendarId", "calendarId", static (in NativeArgs a) => Str(This<JsPlainMonthDay>(a, "calendarId").Calendar));
        r.Getter(proto, "monthCode", "monthCode", static (in NativeArgs a) => { This<JsPlainMonthDay>(a, "monthCode"); return Str(CalOf(a, "monthCode").MonthCode); });
        r.Getter(proto, "day", "day", static (in NativeArgs a) => { This<JsPlainMonthDay>(a, "day"); return Num(CalOf(a, "day").Day); });
        r.Method(proto, "with", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var md = This<JsPlainMonthDay>(a, "Temporal.PlainMonthDay.prototype.with");
            if (!a[0].IsObject) throw JsErrors.TypeError(realm, "with requires an object");
            var item = a[0].AsObjectUnchecked;
            Temporal.RejectTemporalLike(realm, item);
            var fields = Temporal.DateToFields(md.Calendar, md.Date, CalendarType.MonthDay);
            var partial = Temporal.PrepareFields(realm, md.Calendar, item, FieldSet.Date, FieldSet.None, partial: true);
            fields = Temporal.MergeFields(md.Calendar, fields, partial);
            var overflow = TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, a[1]));
            return JsValue.FromObject(Temporal.CreatePlainMonthDay(realm, Temporal.MonthDayFromFields(realm, md.Calendar, fields, overflow), md.Calendar));
        });
        r.Method(proto, "equals", 1, static (in NativeArgs a) =>
        {
            var md = This<JsPlainMonthDay>(a, "Temporal.PlainMonthDay.prototype.equals");
            var other = Temporal.ToTemporalMonthDay(a.Realm, a[0], JsValue.Undefined);
            return JsValue.FromBool(TemporalMath.CompareISODate(md.Date, other.Date) == 0 && md.Calendar == other.Calendar);
        });
        r.Method(proto, "toPlainDate", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var md = This<JsPlainMonthDay>(a, "Temporal.PlainMonthDay.prototype.toPlainDate");
            if (!a[0].IsObject) throw JsErrors.TypeError(realm, "toPlainDate requires an object with a year");
            var fields = Temporal.DateToFields(md.Calendar, md.Date, CalendarType.MonthDay);
            var input = Temporal.PrepareFields(realm, md.Calendar, a[0].AsObjectUnchecked, FieldSet.Year, FieldSet.None, partial: false);
            fields = Temporal.MergeFields(md.Calendar, fields, input);
            return JsValue.FromObject(Temporal.CreatePlainDate(realm, Temporal.DateFromFields(realm, md.Calendar, fields, Overflow.Constrain), md.Calendar));
        });
        r.Method(proto, "toString", 0, static (in NativeArgs a) =>
        {
            var md = This<JsPlainMonthDay>(a, "Temporal.PlainMonthDay.prototype.toString");
            var show = TemporalOptions.GetShowCalendarName(a.Realm, TemporalOptions.GetOptionsObject(a.Realm, a[0]));
            return Str(MonthDayToString(md, show));
        });
        r.Method(proto, "toJSON", 0, static (in NativeArgs a) => Str(MonthDayToString(This<JsPlainMonthDay>(a, "Temporal.PlainMonthDay.prototype.toJSON"), "auto")));
        r.Method(proto, "toLocaleString", 0, static (in NativeArgs a) =>
        {
            var md = This<JsPlainMonthDay>(a, "Temporal.PlainMonthDay.prototype.toLocaleString");
            if (a.Realm.Intl != null) return Str(IntlDateTimeFormat.FormatTemporalToLocaleString(a.Realm, md, a[0], a[1]));
            return Str(MonthDayToString(md, "auto"));
        });
        r.Method(proto, "valueOf", 0, static (in NativeArgs a) => ValueOf(a));
    }

    private static string MonthDayToString(JsPlainMonthDay md, string show)
    {
        var s = md.Date.Month.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + "-" + md.Date.Day.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        if (show is "always" or "critical" || md.Calendar != Temporal.Iso8601) s = Temporal.PadYear(md.Date.Year) + "-" + s;
        return s + Temporal.CalendarAnnotation(md.Calendar, show);
    }
}
