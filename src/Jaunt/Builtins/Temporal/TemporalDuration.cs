using System;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Jaunt.Runtime;

internal readonly record struct DateDur(double Years, double Months, double Weeks, double Days)
{
    public static readonly DateDur Zero = default;
    public int Sign => Years != 0 ? Math.Sign(Years) : Months != 0 ? Math.Sign(Months) : Weeks != 0 ? Math.Sign(Weeks) : Math.Sign(Days);
}

/// <summary>Internal Duration Record: a date duration plus an exact time duration in nanoseconds.</summary>
internal readonly record struct InternalDur(DateDur Date, Int128 Time)
{
    public int Sign { get { int s = Date.Sign; return s != 0 ? s : Time.CompareTo(Int128.Zero) switch { < 0 => -1, > 0 => 1, _ => 0 }; } }
}

internal readonly record struct NudgeResult(InternalDur Duration, Int128 NudgedEpochNs, bool DidExpandCalendarUnit);

internal static partial class Temporal
{
    // ------------------------------------------------------------------ Duration records

    private static readonly BigInteger s_maxSecondsTotal = BigInteger.Pow(2, 53);

    /// <summary>IsValidDuration.</summary>
    public static bool IsValidDuration(double[] f)
    {
        int sign = 0;
        foreach (var v in f)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return false;
            int s = Math.Sign(v);
            if (s != 0)
            {
                if (sign != 0 && s != sign) return false;
                sign = s;
            }
        }
        if (Math.Abs(f[0]) >= 4294967296.0 || Math.Abs(f[1]) >= 4294967296.0 || Math.Abs(f[2]) >= 4294967296.0) return false;
        // normalized seconds = days*86400 + hours*3600 + minutes*60 + seconds + ms*10^-3 + us*10^-6 + ns*10^-9 < 2^53
        foreach (var v in f) if (Math.Abs(v) > 1e40) return false;
        var totalNs = (new BigInteger(f[3]) * 86400 + new BigInteger(f[4]) * 3600 + new BigInteger(f[5]) * 60 + new BigInteger(f[6])) * 1_000_000_000
            + new BigInteger(f[7]) * 1_000_000 + new BigInteger(f[8]) * 1000 + new BigInteger(f[9]);
        return BigInteger.Abs(totalNs) < s_maxSecondsTotal * 1_000_000_000;
    }

    public static JsDuration CreateDuration(Realm realm, double[] f, JsObject? newTarget = null)
    {
        if (!IsValidDuration(f)) throw JsErrors.RangeError(realm, "invalid duration");
        for (int i = 0; i < 10; i++) if (f[i] == 0) f[i] = 0;   // no negative zeros
        var proto = newTarget == null ? realm.Temporal.Duration : Operations.GetPrototypeFromConstructor(realm, newTarget, realm.Temporal.Duration);
        return new JsDuration(realm, proto, f);
    }

    public static int DurationSign(double[] f)
    {
        foreach (var v in f) if (v != 0) return v < 0 ? -1 : 1;
        return 0;
    }

    /// <summary>DefaultTemporalLargestUnit.</summary>
    public static TUnit DefaultLargestUnit(double[] f)
    {
        for (int i = 0; i < 10; i++) if (f[i] != 0) return (TUnit)i;
        return TUnit.Nanosecond;
    }

    public static Int128 TimeFromComponents(double h, double m, double s, double ms, double us, double ns) =>
        (Int128)h * TemporalMath.NsPerHour + (Int128)m * TemporalMath.NsPerMinute + (Int128)s * TemporalMath.NsPerSecond
        + (Int128)ms * 1_000_000 + (Int128)us * 1000 + (Int128)ns;

    public static InternalDur ToInternal(double[] f) =>
        new(new DateDur(f[0], f[1], f[2], f[3]), TimeFromComponents(f[4], f[5], f[6], f[7], f[8], f[9]));

    public static InternalDur ToInternalWith24HourDays(Realm realm, double[] f)
    {
        var t = TimeFromComponents(f[4], f[5], f[6], f[7], f[8], f[9]);
        t = Add24HourDays(realm, t, f[3]);
        return new InternalDur(new DateDur(f[0], f[1], f[2], 0), t);
    }

    public static Int128 CheckTime(Realm realm, Int128 t)
    {
        if (t > TemporalMath.MaxTimeDuration || t < -TemporalMath.MaxTimeDuration) throw JsErrors.RangeError(realm, "duration is out of range");
        return t;
    }

    public static Int128 Add24HourDays(Realm realm, Int128 t, double days) => CheckTime(realm, t + (Int128)days * TemporalMath.NsPerDay);

    /// <summary>ToDateDurationRecordWithoutTime.</summary>
    public static DateDur ToDateDurationWithoutTime(Realm realm, double[] f)
    {
        var internalDur = ToInternalWith24HourDays(realm, f);
        var days = internalDur.Time / TemporalMath.NsPerDay;   // truncation
        return new DateDur(f[0], f[1], f[2], (double)days);
    }

    /// <summary>TemporalDurationFromInternal.</summary>
    public static JsDuration FromInternal(Realm realm, InternalDur d, TUnit largestUnit)
    {
        var t = d.Time;
        int sign = t < 0 ? -1 : t > 0 ? 1 : 0;
        var ns = t < 0 ? -t : t;
        Int128 days = 0, hours = 0, minutes = 0, seconds = 0, ms = 0, us = 0;
        if (largestUnit <= TUnit.Day)
        {
            us = ns / 1000; ns %= 1000; ms = us / 1000; us %= 1000; seconds = ms / 1000; ms %= 1000;
            minutes = seconds / 60; seconds %= 60; hours = minutes / 60; minutes %= 60; days = hours / 24; hours %= 24;
        }
        else if (largestUnit == TUnit.Hour)
        {
            us = ns / 1000; ns %= 1000; ms = us / 1000; us %= 1000; seconds = ms / 1000; ms %= 1000;
            minutes = seconds / 60; seconds %= 60; hours = minutes / 60; minutes %= 60;
        }
        else if (largestUnit == TUnit.Minute)
        {
            us = ns / 1000; ns %= 1000; ms = us / 1000; us %= 1000; seconds = ms / 1000; ms %= 1000;
            minutes = seconds / 60; seconds %= 60;
        }
        else if (largestUnit == TUnit.Second)
        {
            us = ns / 1000; ns %= 1000; ms = us / 1000; us %= 1000; seconds = ms / 1000; ms %= 1000;
        }
        else if (largestUnit == TUnit.Millisecond)
        {
            us = ns / 1000; ns %= 1000; ms = us / 1000; us %= 1000;
        }
        else if (largestUnit == TUnit.Microsecond)
        {
            us = ns / 1000; ns %= 1000;
        }
        var f = new double[]
        {
            d.Date.Years, d.Date.Months, d.Date.Weeks, d.Date.Days + TemporalMath.ToDouble(days * sign),
            TemporalMath.ToDouble(hours * sign), TemporalMath.ToDouble(minutes * sign), TemporalMath.ToDouble(seconds * sign),
            TemporalMath.ToDouble(ms * sign), TemporalMath.ToDouble(us * sign), TemporalMath.ToDouble(ns * sign),
        };
        return CreateDuration(realm, f);
    }

    public static Int128 RoundTimeDuration(Realm realm, Int128 t, long increment, TUnit unit, RoundingMode mode) =>
        CheckTime(realm, TemporalMath.RoundToIncrement(t, (Int128)TemporalMath.UnitNs(unit) * increment, mode));

    public static double TotalTimeDuration(Int128 t, TUnit unit) =>
        TemporalMath.RationalToDouble((BigInteger)t, TemporalMath.UnitNs(unit));

    // ------------------------------------------------------------------ calendar arithmetic (ISO 8601)

    /// <summary>CalendarDateAdd.</summary>
    public static IsoDate CalendarDateAdd(Realm realm, string cal, IsoDate date, DateDur d, Overflow overflow)
    {
        if (cal != Iso8601) return NonISODateAdd(realm, cal, date, d, overflow);
        var (y, m) = TemporalMath.BalanceISOYearMonth(date.Year + (long)d.Years, date.Month + (long)d.Months);
        if (Math.Abs(y) > 1_000_000) throw JsErrors.RangeError(realm, "date is outside the supported range");
        var intermediate = RegulateISODate(realm, y, m, date.Day, overflow);
        double days = d.Days + 7 * d.Weeks;
        if (Math.Abs(days) > 1e10) throw JsErrors.RangeError(realm, "date is outside the supported range");
        var result = TemporalMath.AddDays(intermediate, (long)days);
        if (!TemporalMath.ISODateWithinLimits(result)) throw JsErrors.RangeError(realm, "date is outside the supported range");
        return result;
    }

    private static bool Surpasses(int sign, long y1, long m1, long d1, IsoDate two)
    {
        d1 = Math.Min(d1, TemporalMath.DaysInMonth(y1, (int)m1));
        if (y1 != two.Year) return sign * (y1 - two.Year) > 0;
        if (m1 != two.Month) return sign * (m1 - two.Month) > 0;
        if (d1 != two.Day) return sign * (d1 - two.Day) > 0;
        return false;
    }

    /// <summary>CalendarDateUntil.</summary>
    public static DateDur CalendarDateUntil(string cal, IsoDate one, IsoDate two, TUnit largestUnit)
    {
        if (cal != Iso8601) return NonISODateUntil(cal, one, two, largestUnit);
        int sign = -TemporalMath.CompareISODate(one, two);
        if (sign == 0) return DateDur.Zero;
        long years = 0, months = 0;
        if (largestUnit is TUnit.Year or TUnit.Month)
        {
            long candidateYears = two.Year - one.Year;
            if (candidateYears != 0) candidateYears -= sign;
            while (!Surpasses(sign, one.Year + candidateYears, one.Month, one.Day, two))
            {
                years = candidateYears;
                candidateYears += sign;
            }
            long candidateMonths = sign;
            var (iy, im) = TemporalMath.BalanceISOYearMonth(one.Year + years, one.Month + candidateMonths);
            while (!Surpasses(sign, iy, im, one.Day, two))
            {
                months = candidateMonths;
                candidateMonths += sign;
                (iy, im) = TemporalMath.BalanceISOYearMonth(iy, im + sign);
            }
            if (largestUnit == TUnit.Month)
            {
                months += years * 12;
                years = 0;
            }
        }
        var (y2, m2) = TemporalMath.BalanceISOYearMonth(one.Year + years, one.Month + months);
        int dim = TemporalMath.DaysInMonth(y2, m2);
        var constrained = new IsoDate((int)y2, m2, Math.Min(one.Day, dim));
        long days = TemporalMath.EpochDays(two) - TemporalMath.EpochDays(constrained);
        long weeks = 0;
        if (largestUnit == TUnit.Week)
        {
            weeks = days / 7;
            days %= 7;
        }
        return new DateDur(years, months, weeks, days);
    }

    // ------------------------------------------------------------------ non-ISO calendar arithmetic

    /// <summary>NonISODateAdd.</summary>
    private static IsoDate NonISODateAdd(Realm realm, string cal, IsoDate date, DateDur d, Overflow overflow)
    {
        var parts = TemporalCalendars.ISOToDate(cal, date);
        double y0d = parts.Year + d.Years;
        if (Math.Abs(y0d) > 400_000 || Math.Abs(d.Months) > 5_000_000 || Math.Abs(d.Days + 7 * d.Weeks) > 1e9)
            throw JsErrors.RangeError(realm, "date is outside the supported range");
        int y0 = (int)y0d;
        var code = TemporalCalendars.ConstrainMonthCode(cal, y0, parts.MonthCode, overflow)
            ?? throw JsErrors.RangeError(realm, $"monthCode {parts.MonthCode} does not exist in year {y0}");
        int m0 = TemporalCalendars.MonthCodeToOrdinal(cal, y0, code);
        var endOfMonth = BalanceEndOfMonth(cal, y0, m0 + (long)d.Months);
        int regulatedDay = parts.Day;
        if (regulatedDay > endOfMonth.Day)
        {
            if (overflow == Overflow.Reject) throw JsErrors.RangeError(realm, "day is out of range");
            regulatedDay = endOfMonth.Day;
        }
        long ed = TemporalMath.EpochDays(TemporalCalendars.IntegersToISO(cal, endOfMonth.Year, endOfMonth.Month, regulatedDay)) + (long)(7 * d.Weeks + d.Days);
        var result = TemporalMath.FromEpochDays(ed);
        if (!TemporalMath.ISODateWithinLimits(result)) throw JsErrors.RangeError(realm, "date is outside the supported range");
        return result;
    }

    /// <summary>BalanceNonISODate(year, month + 1, 0): the last day of the (balanced) month.</summary>
    private static (int Year, int Month, int Day) BalanceEndOfMonth(string cal, int year, long month)
    {
        var (y, m, _) = TemporalCalendars.Balance(cal, year, month, 1);
        return (y, m, TemporalCalendars.DaysInMonth(cal, y, m));
    }

    private static bool CompareSurpasses(int sign, int year, string? monthCode, int month, int day, CalendarDate target)
    {
        if (year != target.Year) return sign * (year - target.Year) > 0;
        if (monthCode != null)
        {
            if (monthCode != target.MonthCode)
            {
                int c = string.CompareOrdinal(monthCode, target.MonthCode);
                return sign > 0 ? c > 0 : c < 0;
            }
        }
        else if (month != target.Month) return sign * (month - target.Month) > 0;
        if (day != target.Day) return sign * (day - target.Day) > 0;
        return false;
    }

    /// <summary>NonISODateSurpasses (years/months only; weeks and days are handled by epoch-day arithmetic).</summary>
    private static bool NonISOSurpasses(string cal, int sign, CalendarDate parts, CalendarDate cal2, long years, long months)
    {
        int y0 = (int)(parts.Year + years);
        if (CompareSurpasses(sign, y0, parts.MonthCode, 0, parts.Day, cal2)) return true;
        int m0 = TemporalCalendars.MonthCodeToOrdinal(cal, y0, TemporalCalendars.ConstrainMonthCode(cal, y0, parts.MonthCode, Overflow.Constrain)!);
        var (ay, am, _) = TemporalCalendars.Balance(cal, y0, m0 + months, 1);
        return CompareSurpasses(sign, ay, null, am, parts.Day, cal2);
    }

    /// <summary>NonISODateUntil.</summary>
    private static DateDur NonISODateUntil(string cal, IsoDate one, IsoDate two, TUnit largestUnit)
    {
        int sign = -TemporalMath.CompareISODate(one, two);
        if (sign == 0) return DateDur.Zero;
        var parts = TemporalCalendars.ISOToDate(cal, one);
        var cal2 = TemporalCalendars.ISOToDate(cal, two);
        long years = 0, months = 0;
        if (largestUnit == TUnit.Year)
        {
            long candidate = cal2.Year - parts.Year;
            if (candidate != 0) candidate -= sign;
            if (candidate * sign < 0) candidate = 0;
            years = candidate - sign;
            if (years * sign < 0) years = 0;
            for (candidate = years + sign; !NonISOSurpasses(cal, sign, parts, cal2, candidate, 0); candidate += sign) years = candidate;
            if (years != 0 && NonISOSurpasses(cal, sign, parts, cal2, years, 0)) years = 0;   // estimate overshot: restart
            if (years == 0)
                for (candidate = sign; !NonISOSurpasses(cal, sign, parts, cal2, candidate, 0); candidate += sign) years = candidate;
        }
        if (largestUnit is TUnit.Year or TUnit.Month)
        {
            long candidate = sign;
            if (largestUnit == TUnit.Month)
            {
                // start near the answer: about 12.4 months per year at most
                long est = (long)((cal2.Year - parts.Year) * 12.0) - 2 * sign;
                if (est * sign > 1) candidate = est;
                if (NonISOSurpasses(cal, sign, parts, cal2, years, candidate - sign)) candidate = sign;
                else months = candidate - sign;
            }
            for (; !NonISOSurpasses(cal, sign, parts, cal2, years, candidate); candidate += sign) months = candidate;
        }
        // weeks and days: from the regulated intermediate date by epoch days
        int y0 = (int)(parts.Year + years);
        int m0 = TemporalCalendars.MonthCodeToOrdinal(cal, y0, TemporalCalendars.ConstrainMonthCode(cal, y0, parts.MonthCode, Overflow.Constrain)!);
        var (ey, em, edim) = BalanceEndOfMonth(cal, y0, m0 + months);
        int regulated = Math.Min(parts.Day, edim);
        long start = TemporalMath.EpochDays(TemporalCalendars.IntegersToISO(cal, ey, em, regulated));
        long days = TemporalMath.EpochDays(two) - start;
        long weeks = 0;
        if (largestUnit == TUnit.Week) { weeks = days / 7; days %= 7; }
        return new DateDur(years, months, weeks, days);
    }

    /// <summary>DifferenceISODateTime.</summary>
    public static InternalDur DifferenceISODateTime(Realm realm, string cal, IsoDateTime dt1, IsoDateTime dt2, TUnit largestUnit)
    {
        Int128 timeDuration = dt2.Time.TotalNs - dt1.Time.TotalNs;
        int timeSign = timeDuration < 0 ? -1 : timeDuration > 0 ? 1 : 0;
        int dateSign = TemporalMath.CompareISODate(dt2.Date, dt1.Date);
        var adjusted = dt2.Date;
        if (timeSign == -dateSign)
        {
            adjusted = TemporalMath.AddDays(adjusted, timeSign);
            timeDuration = Add24HourDays(realm, timeDuration, -timeSign);
        }
        var dateLargest = TemporalMath.Larger(TUnit.Day, largestUnit);
        var dateDiff = CalendarDateUntil(cal, dt1.Date, adjusted, dateLargest);
        if (largestUnit != dateLargest)
        {
            timeDuration = Add24HourDays(realm, timeDuration, dateDiff.Days);
            dateDiff = dateDiff with { Days = 0 };
        }
        return new InternalDur(dateDiff, timeDuration);
    }

    public static InternalDur DifferencePlainDateTimeWithRounding(Realm realm, string cal, IsoDateTime dt1, IsoDateTime dt2, TUnit largestUnit, long increment, TUnit smallestUnit, RoundingMode mode)
    {
        if (TemporalMath.CompareISODateTime(dt1, dt2) == 0) return default;
        if (!TemporalMath.ISODateTimeWithinLimits(dt1) || !TemporalMath.ISODateTimeWithinLimits(dt2)) throw JsErrors.RangeError(realm, "date-time is outside the supported range");
        var diff = DifferenceISODateTime(realm, cal, dt1, dt2, largestUnit);
        if (smallestUnit == TUnit.Nanosecond && increment == 1) return diff;
        var dest = TemporalMath.GetUTCEpochNanoseconds(dt2);
        return RoundRelativeDuration(realm, cal, diff, dest, dt1, null, largestUnit, increment, smallestUnit, mode);
    }

    public static double DifferencePlainDateTimeWithTotal(Realm realm, string cal, IsoDateTime dt1, IsoDateTime dt2, TUnit unit)
    {
        if (TemporalMath.CompareISODateTime(dt1, dt2) == 0) return 0;
        if (!TemporalMath.ISODateTimeWithinLimits(dt1) || !TemporalMath.ISODateTimeWithinLimits(dt2)) throw JsErrors.RangeError(realm, "date-time is outside the supported range");
        var diff = DifferenceISODateTime(realm, cal, dt1, dt2, unit);
        if (unit == TUnit.Nanosecond) return TemporalMath.ToDouble(diff.Time);
        var dest = TemporalMath.GetUTCEpochNanoseconds(dt2);
        return TotalRelativeDuration(realm, cal, diff, dest, dt1, null, unit);
    }

    /// <summary>DifferenceZonedDateTime.</summary>
    public static InternalDur DifferenceZonedDateTime(Realm realm, string cal, Int128 ns1, Int128 ns2, TemporalTz tz, TUnit largestUnit)
    {
        if (ns1 == ns2) return default;
        var start = GetISODateTimeFor(tz, ns1);
        var end = GetISODateTimeFor(tz, ns2);
        if (TemporalMath.CompareISODate(start.Date, end.Date) == 0) return new InternalDur(DateDur.Zero, ns2 - ns1);
        int sign = ns2 < ns1 ? -1 : 1;
        int maxDayCorrection = sign == 1 ? 2 : 1;
        int dayCorrection = 0;
        Int128 timeDuration = end.Time.TotalNs - start.Time.TotalNs;
        if ((timeDuration < 0 ? -1 : timeDuration > 0 ? 1 : 0) == -sign) dayCorrection++;
        bool success = false;
        IsoDate intermediateDate = default;
        while (dayCorrection <= maxDayCorrection && !success)
        {
            intermediateDate = TemporalMath.AddDays(end.Date, dayCorrection * -sign);
            var intermediateDt = new IsoDateTime(intermediateDate, start.Time);
            var intermediateNs = TemporalTimeZones.GetEpochNsFor(realm, tz, intermediateDt, Disambiguation.Compatible);
            timeDuration = ns2 - intermediateNs;
            int timeSign = timeDuration < 0 ? -1 : timeDuration > 0 ? 1 : 0;
            if (sign != -timeSign) success = true;
            dayCorrection++;
        }
        if (!success) throw JsErrors.RangeError(realm, "could not compute the difference in this time zone");
        var dateLargest = TemporalMath.Larger(largestUnit, TUnit.Day);
        var dateDiff = CalendarDateUntil(cal, start.Date, intermediateDate, dateLargest);
        return new InternalDur(dateDiff, timeDuration);
    }

    public static InternalDur DifferenceInstant(Realm realm, Int128 ns1, Int128 ns2, long increment, TUnit smallestUnit, RoundingMode mode)
    {
        var t = RoundTimeDuration(realm, ns2 - ns1, increment, smallestUnit, mode);
        return new InternalDur(DateDur.Zero, t);
    }

    public static InternalDur DifferenceZonedDateTimeWithRounding(Realm realm, string cal, Int128 ns1, Int128 ns2, TemporalTz tz, TUnit largestUnit, long increment, TUnit smallestUnit, RoundingMode mode)
    {
        if (largestUnit > TUnit.Day) return DifferenceInstant(realm, ns1, ns2, increment, smallestUnit, mode);
        var diff = DifferenceZonedDateTime(realm, cal, ns1, ns2, tz, largestUnit);
        if (smallestUnit == TUnit.Nanosecond && increment == 1) return diff;
        var dt = GetISODateTimeFor(tz, ns1);
        return RoundRelativeDuration(realm, cal, diff, ns2, dt, tz, largestUnit, increment, smallestUnit, mode, ns1);
    }

    public static double DifferenceZonedDateTimeWithTotal(Realm realm, string cal, Int128 ns1, Int128 ns2, TemporalTz tz, TUnit unit)
    {
        if (unit > TUnit.Day) return TotalTimeDuration(ns2 - ns1, unit);
        var diff = DifferenceZonedDateTime(realm, cal, ns1, ns2, tz, unit);
        var dt = GetISODateTimeFor(tz, ns1);
        return TotalRelativeDuration(realm, cal, diff, ns2, dt, tz, unit, ns1);
    }

    public static Int128 AddInstant(Realm realm, Int128 ns, Int128 t)
    {
        var r = ns + t;
        if (!TemporalMath.IsValidEpochNanoseconds(r)) throw JsErrors.RangeError(realm, "instant is outside the supported range");
        return r;
    }

    /// <summary>AddZonedDateTime.</summary>
    public static Int128 AddZonedDateTime(Realm realm, string cal, Int128 ns, TemporalTz tz, InternalDur d, Overflow overflow)
    {
        if (d.Date.Sign == 0) return AddInstant(realm, ns, d.Time);
        var dt = GetISODateTimeFor(tz, ns);
        var added = CalendarDateAdd(realm, cal, dt.Date, d.Date, overflow);
        var intermediate = new IsoDateTime(added, dt.Time);
        if (!TemporalMath.ISODateTimeWithinLimits(intermediate)) throw JsErrors.RangeError(realm, "date-time is outside the supported range");
        var intermediateNs = TemporalTimeZones.GetEpochNsFor(realm, tz, intermediate, Disambiguation.Compatible);
        return AddInstant(realm, intermediateNs, d.Time);
    }

    // ------------------------------------------------------------------ relative rounding

    private static Int128 EpochNsFor(Realm realm, TemporalTz? tz, IsoDateTime dt) =>
        tz == null ? TemporalMath.GetUTCEpochNanoseconds(dt) : TemporalTimeZones.GetEpochNsFor(realm, tz, dt, Disambiguation.Compatible);

    private static long TruncToIncrement(double x, long increment)
    {
        long v = (long)x;
        return v - v % increment;
    }

    /// <summary>NudgeToCalendarUnit; also returns the exact total as a rational (num/den).</summary>
    private static NudgeResult NudgeToCalendarUnit(Realm realm, string cal, int sign, InternalDur d, Int128 destEpochNs, IsoDateTime dt, TemporalTz? tz,
        long increment, TUnit unit, RoundingMode mode, out BigInteger totalNum, out BigInteger totalDen, Int128? originNs = null)
    {
        long r1, r2;
        DateDur startDur, endDur;
        var date = d.Date;
        switch (unit)
        {
            case TUnit.Year:
                r1 = TruncToIncrement(date.Years, increment);
                r2 = r1 + increment * sign;
                startDur = new DateDur(r1, 0, 0, 0);
                endDur = new DateDur(r2, 0, 0, 0);
                break;
            case TUnit.Month:
                r1 = TruncToIncrement(date.Months, increment);
                r2 = r1 + increment * sign;
                startDur = new DateDur(date.Years, r1, 0, 0);
                endDur = new DateDur(date.Years, r2, 0, 0);
                break;
            case TUnit.Week:
            {
                var yearsMonths = new DateDur(date.Years, date.Months, 0, 0);
                var weeksStart = CalendarDateAdd(realm, cal, dt.Date, yearsMonths, Overflow.Constrain);
                var weeksEnd = TemporalMath.AddDays(weeksStart, (long)date.Days);
                var untilResult = CalendarDateUntil(cal, weeksStart, weeksEnd, TUnit.Week);
                r1 = TruncToIncrement(date.Weeks + untilResult.Weeks, increment);
                r2 = r1 + increment * sign;
                startDur = new DateDur(date.Years, date.Months, r1, 0);
                endDur = new DateDur(date.Years, date.Months, r2, 0);
                break;
            }
            default:
                r1 = TruncToIncrement(date.Days, increment);
                r2 = r1 + increment * sign;
                startDur = new DateDur(date.Years, date.Months, date.Weeks, r1);
                endDur = new DateDur(date.Years, date.Months, date.Weeks, r2);
                break;
        }
        var start = CalendarDateAdd(realm, cal, dt.Date, startDur, Overflow.Constrain);
        var end = CalendarDateAdd(realm, cal, dt.Date, endDur, Overflow.Constrain);
        // a zero start duration is the origin itself (which may be the second of two equal wall-clock times)
        var startNs = originNs != null && startDur == DateDur.Zero ? originNs.Value : EpochNsFor(realm, tz, new IsoDateTime(start, dt.Time));
        var endNs = EpochNsFor(realm, tz, new IsoDateTime(end, dt.Time));
        if (sign == 1 ? (startNs > destEpochNs || destEpochNs > endNs) : (startNs < destEpochNs || destEpochNs < endNs))
            throw JsErrors.RangeError(realm, "the duration cannot be rounded relative to this date (calendar arithmetic is not monotonic)");
        if (endNs == startNs) throw JsErrors.RangeError(realm, "zero-length rounding interval");
        BigInteger num = (BigInteger)(destEpochNs - startNs), den = (BigInteger)(endNs - startNs);
        // total = r1 + progress * increment * sign
        totalNum = (BigInteger)r1 * den + num * increment * sign;
        totalDen = den;
        if (totalDen.Sign < 0) { totalNum = -totalNum; totalDen = -totalDen; }
        var um = TemporalMath.UnsignedMode(mode, sign < 0);
        BigInteger roundedUnit;
        if (num == den) roundedUnit = BigInteger.Abs(r2);
        else roundedUnit = TemporalMath.ApplyUnsignedRounding(BigInteger.Abs(totalNum), totalDen, BigInteger.Abs(r1), BigInteger.Abs(r2), um);
        if (roundedUnit == BigInteger.Abs(r2))
            return new NudgeResult(new InternalDur(endDur, 0), endNs, true);
        return new NudgeResult(new InternalDur(startDur, 0), startNs, false);
    }

    private static NudgeResult NudgeToZonedTime(Realm realm, string cal, int sign, InternalDur d, IsoDateTime dt, TemporalTz tz, long increment, TUnit unit, RoundingMode mode)
    {
        var start = CalendarDateAdd(realm, cal, dt.Date, d.Date, Overflow.Constrain);
        var startDt = new IsoDateTime(start, dt.Time);
        var endDate = TemporalMath.AddDays(start, sign);
        var endDt = new IsoDateTime(endDate, dt.Time);
        var startNs = TemporalTimeZones.GetEpochNsFor(realm, tz, startDt, Disambiguation.Compatible);
        var endNs = TemporalTimeZones.GetEpochNsFor(realm, tz, endDt, Disambiguation.Compatible);
        var daySpan = endNs - startNs;
        Int128 unitLength = TemporalMath.UnitNs(unit);
        var rounded = TemporalMath.RoundToIncrement(d.Time, unitLength * increment, mode);
        var beyond = rounded - daySpan;
        int beyondSign = beyond < 0 ? -1 : beyond > 0 ? 1 : 0;
        bool didRoundBeyondDay;
        int dayDelta;
        Int128 nudged;
        if (beyondSign != -sign)
        {
            didRoundBeyondDay = true;
            dayDelta = sign;
            rounded = TemporalMath.RoundToIncrement(beyond, unitLength * increment, mode);
            nudged = rounded + endNs;
        }
        else
        {
            didRoundBeyondDay = false;
            dayDelta = 0;
            nudged = rounded + startNs;
        }
        var dateDur = d.Date with { Days = d.Date.Days + dayDelta };
        return new NudgeResult(new InternalDur(dateDur, CheckTime(realm, rounded)), nudged, didRoundBeyondDay);
    }

    private static NudgeResult NudgeToDayOrTime(Realm realm, InternalDur d, Int128 destEpochNs, TUnit largestUnit, long increment, TUnit smallestUnit, RoundingMode mode)
    {
        var time = Add24HourDays(realm, d.Time, d.Date.Days);
        Int128 unitLength = TemporalMath.UnitNs(smallestUnit);
        var rounded = TemporalMath.RoundToIncrement(time, unitLength * increment, mode);
        var diffTime = rounded - time;
        var wholeDays = time / TemporalMath.NsPerDay;
        var roundedWholeDays = rounded / TemporalMath.NsPerDay;
        var dayDelta = roundedWholeDays - wholeDays;
        int dayDeltaSign = dayDelta < 0 ? -1 : dayDelta > 0 ? 1 : 0;
        int timeSign = time < 0 ? -1 : time > 0 ? 1 : 0;
        bool didExpandDays = dayDeltaSign == timeSign;
        var nudged = diffTime + destEpochNs;
        Int128 days = 0;
        var remainder = rounded;
        if (largestUnit <= TUnit.Day)
        {
            days = roundedWholeDays;
            remainder = rounded - roundedWholeDays * TemporalMath.NsPerDay;
        }
        var dateDur = d.Date with { Days = (double)days };
        return new NudgeResult(new InternalDur(dateDur, CheckTime(realm, remainder)), nudged, didExpandDays);
    }

    private static InternalDur BubbleRelativeDuration(Realm realm, string cal, int sign, InternalDur d, Int128 nudgedEpochNs, IsoDateTime dt, TemporalTz? tz, TUnit largestUnit, TUnit smallestUnit)
    {
        if (smallestUnit == largestUnit) return d;
        for (int unitIndex = (int)smallestUnit - 1; unitIndex >= (int)largestUnit; unitIndex--)
        {
            var unit = (TUnit)unitIndex;
            if (unit == TUnit.Week && largestUnit != TUnit.Week) continue;
            var date = d.Date;
            DateDur endDur = unit switch
            {
                TUnit.Year => new DateDur(date.Years + sign, 0, 0, 0),
                TUnit.Month => new DateDur(date.Years, date.Months + sign, 0, 0),
                TUnit.Week => new DateDur(date.Years, date.Months, date.Weeks + sign, 0),
                _ => new DateDur(date.Years, date.Months, date.Weeks, date.Days + sign),
            };
            var endDate = CalendarDateAdd(realm, cal, dt.Date, endDur, Overflow.Constrain);
            var endNs = EpochNsFor(realm, tz, new IsoDateTime(endDate, dt.Time));
            var beyond = nudgedEpochNs - endNs;
            int beyondSign = beyond < 0 ? -1 : beyond > 0 ? 1 : 0;
            if (beyondSign != -sign) d = new InternalDur(endDur, 0);
            else break;
        }
        return d;
    }

    /// <summary>RoundRelativeDuration.</summary>
    public static InternalDur RoundRelativeDuration(Realm realm, string cal, InternalDur d, Int128 destEpochNs, IsoDateTime dt, TemporalTz? tz,
        TUnit largestUnit, long increment, TUnit smallestUnit, RoundingMode mode, Int128? originNs = null)
    {
        bool irregular = TemporalMath.IsCalendarUnit(smallestUnit) || (tz != null && smallestUnit == TUnit.Day);
        int sign = d.Sign < 0 ? -1 : 1;
        NudgeResult r;
        if (irregular) r = NudgeToCalendarUnit(realm, cal, sign, d, destEpochNs, dt, tz, increment, smallestUnit, mode, out _, out _, originNs);
        else if (tz != null) r = NudgeToZonedTime(realm, cal, sign, d, dt, tz, increment, smallestUnit, mode);
        else r = NudgeToDayOrTime(realm, d, destEpochNs, largestUnit, increment, smallestUnit, mode);
        var result = r.Duration;
        if (r.DidExpandCalendarUnit && smallestUnit != TUnit.Week)
        {
            var startUnit = TemporalMath.Larger(smallestUnit, TUnit.Day);
            result = BubbleRelativeDuration(realm, cal, sign, result, r.NudgedEpochNs, dt, tz, largestUnit, startUnit);
        }
        return result;
    }

    /// <summary>TotalRelativeDuration.</summary>
    public static double TotalRelativeDuration(Realm realm, string cal, InternalDur d, Int128 destEpochNs, IsoDateTime dt, TemporalTz? tz, TUnit unit, Int128? originNs = null)
    {
        if (TemporalMath.IsCalendarUnit(unit) || (tz != null && unit == TUnit.Day))
        {
            int sign = d.Sign < 0 ? -1 : 1;
            NudgeToCalendarUnit(realm, cal, sign, d, destEpochNs, dt, tz, 1, unit, RoundingMode.Trunc, out var num, out var den, originNs);
            return TemporalMath.RationalToDouble(num, den);
        }
        var time = Add24HourDays(realm, d.Time, d.Date.Days);
        return TotalTimeDuration(time, unit);
    }

    // ------------------------------------------------------------------ string form

    /// <summary>TemporalDurationToString (precision: -1 auto, 0..9 digits).</summary>
    public static string DurationToString(double[] f, int precision)
    {
        int sign = DurationSign(f);
        var date = new StringBuilder();
        if (f[0] != 0) date.Append(FormatNumber(Math.Abs(f[0]))).Append('Y');
        if (f[1] != 0) date.Append(FormatNumber(Math.Abs(f[1]))).Append('M');
        if (f[2] != 0) date.Append(FormatNumber(Math.Abs(f[2]))).Append('W');
        if (f[3] != 0) date.Append(FormatNumber(Math.Abs(f[3]))).Append('D');
        var time = new StringBuilder();
        if (f[4] != 0) time.Append(FormatNumber(Math.Abs(f[4]))).Append('H');
        if (f[5] != 0) time.Append(FormatNumber(Math.Abs(f[5]))).Append('M');
        var secondsDur = TimeFromComponents(0, 0, f[6], f[7], f[8], f[9]);
        if (secondsDur < 0) secondsDur = -secondsDur;
        bool zeroMinutesAndHigher = f[0] == 0 && f[1] == 0 && f[2] == 0 && f[3] == 0 && f[4] == 0 && f[5] == 0;
        if (secondsDur != 0 || zeroMinutesAndHigher || precision != -1)
        {
            var whole = secondsDur / TemporalMath.NsPerSecond;
            long sub = (long)(secondsDur % TemporalMath.NsPerSecond);
            time.Append(whole.ToString(CultureInfo.InvariantCulture)).Append(FormatFraction(sub, precision)).Append('S');
        }
        var sb = new StringBuilder();
        if (sign < 0) sb.Append('-');
        sb.Append('P').Append(date);
        if (time.Length > 0) sb.Append('T').Append(time);
        return sb.ToString();
    }

    /// <summary>ParseTemporalDurationString into a validated field array.</summary>
    public static double[] ParseDuration(Realm realm, string s)
    {
        if (!TemporalParser.TryParseDuration(s, out var f, out bool negative, out var fracNs, out int fracUnit))
            throw JsErrors.RangeError(realm, $"invalid duration string {s}");
        if (fracUnit >= 0)
        {
            // distribute the fraction of the last time unit over the smaller units
            var ns = fracNs;
            if (fracUnit == 4) { f[5] = (double)(ns / TemporalMath.NsPerMinute); ns %= TemporalMath.NsPerMinute; }
            if (fracUnit <= 5) { f[6] = (double)(ns / TemporalMath.NsPerSecond); ns %= TemporalMath.NsPerSecond; }
            f[7] = (double)(ns / 1_000_000); ns %= 1_000_000;
            f[8] = (double)(ns / 1000);
            f[9] = (double)(ns % 1000);
        }
        if (negative) for (int i = 0; i < 10; i++) f[i] = -f[i];
        if (!IsValidDuration(f)) throw JsErrors.RangeError(realm, $"duration {s} is out of range");
        return f;
    }
}
