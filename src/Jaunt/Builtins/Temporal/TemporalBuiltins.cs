using System;
using System.Numerics;

namespace Jaunt.Runtime;

/// <summary>The Temporal namespace object and its constructors (Temporal proposal, ISO 8601 calendar only).</summary>
internal static partial class TemporalBuiltins
{
    public static void Setup(Realm r)
    {
        var protos = new TemporalProtos();
        r.SetTemporal(protos);
        var temporal = new JsObject(r, r.ObjectPrototype);
        temporal.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("Temporal"), PropFlags.Configurable);
        r.DefineGlobal("Temporal", JsValue.FromObject(temporal));

        SetupDuration(r, temporal, protos);
        SetupInstant(r, temporal, protos);
        SetupPlainDate(r, temporal, protos);
        SetupPlainTime(r, temporal, protos);
        SetupPlainDateTime(r, temporal, protos);
        SetupPlainYearMonth(r, temporal, protos);
        SetupPlainMonthDay(r, temporal, protos);
        SetupZonedDateTime(r, temporal, protos);
        SetupNow(r, temporal);
    }

    // ------------------------------------------------------------------ helpers

    private static T This<T>(in NativeArgs a, string method) where T : JsObject =>
        a.This.Ref as T ?? throw JsErrors.TypeError(a.Realm, $"{method} called on an incompatible receiver");

    private static JsObject Proto(Realm r, string name, out JsObject proto)
    {
        proto = new JsObject(r, r.ObjectPrototype);
        proto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("Temporal." + name), PropFlags.Configurable);
        return proto;
    }

    private static void DefineCtor(JsObject temporal, JsBuiltin ctor, string name) =>
        temporal.DefineHidden(name, JsValue.FromObject(ctor));

    private static JsValue Str(string s) => new(s);
    private static JsValue Num(double d) => new(d);

    private static void RequireNew(in NativeArgs a, string name)
    {
        if (!a.IsConstruct) throw JsErrors.TypeError(a.Realm, $"Constructor Temporal.{name} requires 'new'");
    }

    private static JsValue ValueOf(in NativeArgs a) => throw JsErrors.TypeError(a.Realm, "Temporal objects cannot be converted to primitives; use compare() or equals()");

    internal readonly record struct DiffSettings(TUnit SmallestUnit, TUnit LargestUnit, RoundingMode Mode, long Increment);

    /// <summary>GetDifferenceSettings.</summary>
    internal static DiffSettings GetDifferenceSettings(Realm realm, bool since, JsObject? options, TemporalOptions.UnitGroup group,
        TUnit[] disallowed, TUnit fallbackSmallest, TUnit smallestLargestDefault)
    {
        var largest = TemporalOptions.GetUnit(realm, options, "largestUnit");
        long increment = TemporalOptions.GetRoundingIncrement(realm, options);
        var mode = TemporalOptions.GetRoundingMode(realm, options, RoundingMode.Trunc);
        var smallest = TemporalOptions.GetUnit(realm, options, "smallestUnit");
        TemporalOptions.ValidateUnit(realm, largest, group, "largestUnit", allowAuto: true);
        if (largest == TUnit.Unset) largest = TUnit.Auto;
        if (Array.IndexOf(disallowed, largest) >= 0) throw JsErrors.RangeError(realm, $"{TemporalOptions.UnitName(largest)} is not a valid value for largestUnit");
        TemporalOptions.ValidateUnit(realm, smallest, group, "smallestUnit");
        if (smallest == TUnit.Unset) smallest = fallbackSmallest;
        if (Array.IndexOf(disallowed, smallest) >= 0) throw JsErrors.RangeError(realm, $"{TemporalOptions.UnitName(smallest)} is not a valid value for smallestUnit");
        var defaultLargest = TemporalMath.Larger(smallestLargestDefault, smallest);
        if (largest == TUnit.Auto) largest = defaultLargest;
        if (TemporalMath.Larger(largest, smallest) != largest) throw JsErrors.RangeError(realm, "largestUnit must be at least as large as smallestUnit");
        long max = TemporalOptions.MaxIncrement(smallest);
        if (max != 0) TemporalOptions.ValidateIncrement(realm, increment, max, inclusive: false);
        if (since) mode = TemporalMath.Negate(mode);
        return new DiffSettings(smallest, largest, mode, increment);
    }

    private static JsDuration NegateIf(Realm r, JsDuration d, bool since)
    {
        if (!since) return d;
        var f = (double[])d.F.Clone();
        for (int i = 0; i < 10; i++) f[i] = -f[i];
        return Temporal.CreateDuration(r, f);
    }

    /// <summary>Reads options for toString of types with a time part: (precision record, rounding mode).</summary>
    private static ((int Precision, TUnit Unit, long Increment) Precision, RoundingMode Mode) TimeStringOptions(Realm realm, JsObject? opts, out string showCalendar, bool readCalendar)
    {
        showCalendar = readCalendar ? TemporalOptions.GetShowCalendarName(realm, opts) : "auto";
        int digits = TemporalOptions.GetFractionalSecondDigits(realm, opts);
        var mode = TemporalOptions.GetRoundingMode(realm, opts, RoundingMode.Trunc);
        var smallest = TemporalOptions.GetUnit(realm, opts, "smallestUnit");
        TemporalOptions.ValidateUnit(realm, smallest, TemporalOptions.UnitGroup.Time, "smallestUnit");
        if (smallest == TUnit.Hour) throw JsErrors.RangeError(realm, "hour is not a valid value for smallestUnit");
        return (Temporal.SecondsPrecision(smallest, digits), mode);
    }

    /// <summary>Reads (roundingIncrement, roundingMode, smallestUnit) for round(); a string argument is the smallestUnit.</summary>
    private static JsObject RoundToOptions(Realm realm, in JsValue roundTo)
    {
        if (roundTo.IsUndefined) throw JsErrors.TypeError(realm, "round requires an options argument");
        if (roundTo.IsString)
        {
            var o = new JsObject(realm, null);
            o.CreateDataPropertyOrThrow("smallestUnit", roundTo);
            return o;
        }
        return TemporalOptions.GetOptionsObject(realm, roundTo)!;
    }

    // ------------------------------------------------------------------ Duration

    private static readonly string[] s_durationGetters =
        { "years", "months", "weeks", "days", "hours", "minutes", "seconds", "milliseconds", "microseconds", "nanoseconds" };

    private static void SetupDuration(Realm r, JsObject temporal, TemporalProtos protos)
    {
        var proto = Proto(r, "Duration", out _);
        protos.Duration = proto;
        var ctor = r.Constructor("Duration", 0, static (in NativeArgs a) =>
        {
            RequireNew(a, "Duration");
            var f = new double[10];
            for (int i = 0; i < 10; i++)
                f[i] = a[i].IsUndefined ? 0 : TemporalOptions.ToIntegerIfIntegral(a.Realm, a[i]);
            return JsValue.FromObject(Temporal.CreateDuration(a.Realm, f, a.NewTarget.AsObjectUnchecked));
        }, proto);
        DefineCtor(temporal, ctor, "Duration");
        r.Method(ctor, "from", 1, static (in NativeArgs a) => JsValue.FromObject(Temporal.ToTemporalDuration(a.Realm, a[0])));
        r.Method(ctor, "compare", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var one = Temporal.ToTemporalDuration(realm, a[0]);
            var two = Temporal.ToTemporalDuration(realm, a[1]);
            var opts = TemporalOptions.GetOptionsObject(realm, a[2]);
            var (plain, zoned) = Temporal.GetRelativeTo(realm, opts);
            bool same = true;
            for (int i = 0; i < 10; i++) if (one.F[i] != two.F[i]) { same = false; break; }
            if (same) return new JsValue(0);
            var lu1 = Temporal.DefaultLargestUnit(one.F);
            var lu2 = Temporal.DefaultLargestUnit(two.F);
            var d1 = Temporal.ToInternal(one.F);
            var d2 = Temporal.ToInternal(two.F);
            if (zoned != null && (lu1 <= TUnit.Day || lu2 <= TUnit.Day))
            {
                var after1 = Temporal.AddZonedDateTime(realm, zoned.Calendar, zoned.Ns, zoned.Tz, d1, Overflow.Constrain);
                var after2 = Temporal.AddZonedDateTime(realm, zoned.Calendar, zoned.Ns, zoned.Tz, d2, Overflow.Constrain);
                return new JsValue(after1.CompareTo(after2) switch { < 0 => -1, > 0 => 1, _ => 0 });
            }
            double days1, days2;
            if (TemporalMath.IsCalendarUnit(lu1) || TemporalMath.IsCalendarUnit(lu2))
            {
                if (plain == null) throw JsErrors.RangeError(realm, "relativeTo is required to compare durations with calendar units");
                days1 = DateDurationDays(realm, d1.Date, plain);
                days2 = DateDurationDays(realm, d2.Date, plain);
            }
            else
            {
                days1 = one.F[3];
                days2 = two.F[3];
            }
            var t1 = Temporal.Add24HourDays(realm, d1.Time, days1);
            var t2 = Temporal.Add24HourDays(realm, d2.Time, days2);
            return new JsValue(t1.CompareTo(t2) switch { < 0 => -1, > 0 => 1, _ => 0 });
        });
        for (int i = 0; i < 10; i++)
        {
            int idx = i;
            r.Getter(proto, s_durationGetters[i], s_durationGetters[i], idx switch
            {
                0 => static (in NativeArgs a) => Num(This<JsDuration>(a, "years").F[0]),
                1 => static (in NativeArgs a) => Num(This<JsDuration>(a, "months").F[1]),
                2 => static (in NativeArgs a) => Num(This<JsDuration>(a, "weeks").F[2]),
                3 => static (in NativeArgs a) => Num(This<JsDuration>(a, "days").F[3]),
                4 => static (in NativeArgs a) => Num(This<JsDuration>(a, "hours").F[4]),
                5 => static (in NativeArgs a) => Num(This<JsDuration>(a, "minutes").F[5]),
                6 => static (in NativeArgs a) => Num(This<JsDuration>(a, "seconds").F[6]),
                7 => static (in NativeArgs a) => Num(This<JsDuration>(a, "milliseconds").F[7]),
                8 => static (in NativeArgs a) => Num(This<JsDuration>(a, "microseconds").F[8]),
                _ => static (in NativeArgs a) => Num(This<JsDuration>(a, "nanoseconds").F[9]),
            });
        }
        r.Getter(proto, "sign", "sign", static (in NativeArgs a) => Num(Temporal.DurationSign(This<JsDuration>(a, "sign").F)));
        r.Getter(proto, "blank", "blank", static (in NativeArgs a) => JsValue.FromBool(Temporal.DurationSign(This<JsDuration>(a, "blank").F) == 0));
        r.Method(proto, "with", 1, static (in NativeArgs a) =>
        {
            var d = This<JsDuration>(a, "Temporal.Duration.prototype.with");
            if (!a[0].IsObject) throw JsErrors.TypeError(a.Realm, "with requires an object");
            return JsValue.FromObject(Temporal.CreateDuration(a.Realm, Temporal.ToPartialDuration(a.Realm, a[0].AsObjectUnchecked, d.F)));
        });
        r.Method(proto, "negated", 0, static (in NativeArgs a) =>
        {
            var d = This<JsDuration>(a, "Temporal.Duration.prototype.negated");
            var f = (double[])d.F.Clone();
            for (int i = 0; i < 10; i++) f[i] = -f[i];
            return JsValue.FromObject(Temporal.CreateDuration(a.Realm, f));
        });
        r.Method(proto, "abs", 0, static (in NativeArgs a) =>
        {
            var d = This<JsDuration>(a, "Temporal.Duration.prototype.abs");
            var f = (double[])d.F.Clone();
            for (int i = 0; i < 10; i++) f[i] = Math.Abs(f[i]);
            return JsValue.FromObject(Temporal.CreateDuration(a.Realm, f));
        });
        r.Method(proto, "add", 1, static (in NativeArgs a) => JsValue.FromObject(AddDurations(a, This<JsDuration>(a, "Temporal.Duration.prototype.add"), 1)));
        r.Method(proto, "subtract", 1, static (in NativeArgs a) => JsValue.FromObject(AddDurations(a, This<JsDuration>(a, "Temporal.Duration.prototype.subtract"), -1)));
        r.Method(proto, "round", 1, static (in NativeArgs a) => JsValue.FromObject(DurationRound(a, This<JsDuration>(a, "Temporal.Duration.prototype.round"))));
        r.Method(proto, "total", 1, static (in NativeArgs a) => Num(DurationTotal(a, This<JsDuration>(a, "Temporal.Duration.prototype.total"))));
        r.Method(proto, "toString", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var d = This<JsDuration>(a, "Temporal.Duration.prototype.toString");
            var opts = TemporalOptions.GetOptionsObject(realm, a[0]);
            int digits = TemporalOptions.GetFractionalSecondDigits(realm, opts);
            var mode = TemporalOptions.GetRoundingMode(realm, opts, RoundingMode.Trunc);
            var smallest = TemporalOptions.GetUnit(realm, opts, "smallestUnit");
            TemporalOptions.ValidateUnit(realm, smallest, TemporalOptions.UnitGroup.Time, "smallestUnit");
            if (smallest is TUnit.Hour or TUnit.Minute) throw JsErrors.RangeError(realm, $"{TemporalOptions.UnitName(smallest)} is not a valid value for smallestUnit");
            var precision = Temporal.SecondsPrecision(smallest, digits);
            var f = d.F;
            if (precision.Unit != TUnit.Nanosecond || precision.Increment != 1)
            {
                var internalDur = Temporal.ToInternal(f);
                var largest = Temporal.DefaultLargestUnit(f);
                var time = Temporal.RoundTimeDuration(realm, internalDur.Time, precision.Increment, precision.Unit, mode);
                var rounded = Temporal.FromInternal(realm, new InternalDur(internalDur.Date, time), TemporalMath.Larger(largest, TUnit.Second));
                f = rounded.F;
            }
            return Str(Temporal.DurationToString(f, precision.Precision));
        });
        r.Method(proto, "toJSON", 0, static (in NativeArgs a) => Str(Temporal.DurationToString(This<JsDuration>(a, "Temporal.Duration.prototype.toJSON").F, -1)));
        r.Method(proto, "toLocaleString", 0, static (in NativeArgs a) =>
        {
            var d = This<JsDuration>(a, "Temporal.Duration.prototype.toLocaleString");
            if (a.Realm.Intl != null) return Str(IntlBuiltins.FormatDuration(a.Realm, d.F, a[0], a[1]));
            return Str(Temporal.DurationToString(d.F, -1));
        });
        r.Method(proto, "valueOf", 0, static (in NativeArgs a) => ValueOf(a));
    }

    private static double DateDurationDays(Realm realm, DateDur d, JsPlainDate relativeTo)
    {
        var ymw = new DateDur(d.Years, d.Months, d.Weeks, 0);
        if (ymw.Sign == 0) return d.Days;
        var later = Temporal.CalendarDateAdd(realm, relativeTo.Calendar, relativeTo.Date, ymw, Overflow.Constrain);
        return d.Days + (TemporalMath.EpochDays(later) - TemporalMath.EpochDays(relativeTo.Date));
    }

    private static JsDuration AddDurations(in NativeArgs a, JsDuration duration, int sign)
    {
        var realm = a.Realm;
        var other = Temporal.ToTemporalDuration(realm, a[0]);
        var of = other.F;
        if (sign < 0) { of = (double[])of.Clone(); for (int i = 0; i < 10; i++) of[i] = -of[i]; }
        var lu1 = Temporal.DefaultLargestUnit(duration.F);
        var lu2 = Temporal.DefaultLargestUnit(of);
        var largest = TemporalMath.Larger(lu1, lu2);
        if (TemporalMath.IsCalendarUnit(largest)) throw JsErrors.RangeError(realm, "durations with years, months or weeks cannot be added without a relative date");
        var d1 = Temporal.ToInternalWith24HourDays(realm, duration.F);
        var d2 = Temporal.ToInternalWith24HourDays(realm, of);
        var t = Temporal.CheckTime(realm, d1.Time + d2.Time);
        return Temporal.FromInternal(realm, new InternalDur(DateDur.Zero, t), largest);
    }

    private static JsDuration DurationRound(in NativeArgs a, JsDuration duration)
    {
        var realm = a.Realm;
        var roundTo = RoundToOptions(realm, a[0]);
        bool smallestPresent = true, largestPresent = true;
        var largest = TemporalOptions.GetUnit(realm, roundTo, "largestUnit");
        var (plain, zoned) = Temporal.GetRelativeTo(realm, roundTo);
        long increment = TemporalOptions.GetRoundingIncrement(realm, roundTo);
        var mode = TemporalOptions.GetRoundingMode(realm, roundTo, RoundingMode.HalfExpand);
        var smallest = TemporalOptions.GetUnit(realm, roundTo, "smallestUnit");
        TemporalOptions.ValidateUnit(realm, smallest, TemporalOptions.UnitGroup.DateTime, "smallestUnit");
        if (smallest == TUnit.Unset) { smallestPresent = false; smallest = TUnit.Nanosecond; }
        var existingLargest = Temporal.DefaultLargestUnit(duration.F);
        var defaultLargest = TemporalMath.Larger(existingLargest, smallest);
        if (largest == TUnit.Unset) { largestPresent = false; largest = defaultLargest; }
        else if (largest == TUnit.Auto) largest = defaultLargest;
        if (!smallestPresent && !largestPresent) throw JsErrors.RangeError(realm, "round requires smallestUnit or largestUnit");
        if (TemporalMath.Larger(largest, smallest) != largest) throw JsErrors.RangeError(realm, "largestUnit must be at least as large as smallestUnit");
        long max = TemporalOptions.MaxIncrement(smallest);
        if (max != 0) TemporalOptions.ValidateIncrement(realm, increment, max, inclusive: false);
        if (increment > 1 && largest != smallest && TemporalMath.IsDateUnit(smallest))
            throw JsErrors.RangeError(realm, "roundingIncrement > 1 requires largestUnit to equal smallestUnit for date units");
        if (zoned != null)
        {
            var internalDur = Temporal.ToInternal(duration.F);
            var target = Temporal.AddZonedDateTime(realm, zoned.Calendar, zoned.Ns, zoned.Tz, internalDur, Overflow.Constrain);
            internalDur = Temporal.DifferenceZonedDateTimeWithRounding(realm, zoned.Calendar, zoned.Ns, target, zoned.Tz, largest, increment, smallest, mode);
            if (largest <= TUnit.Day) largest = TUnit.Hour;
            return Temporal.FromInternal(realm, internalDur, largest);
        }
        if (plain != null)
        {
            var internalDur = Temporal.ToInternalWith24HourDays(realm, duration.F);
            var (days, targetTime) = TemporalMath.AddTime(TimeRec.Midnight, internalDur.Time);
            var dateDur = internalDur.Date with { Days = days };
            var targetDate = Temporal.CalendarDateAdd(realm, plain.Calendar, plain.Date, dateDur, Overflow.Constrain);
            var start = new IsoDateTime(plain.Date, TimeRec.Midnight);
            var target = new IsoDateTime(targetDate, targetTime);
            internalDur = Temporal.DifferencePlainDateTimeWithRounding(realm, plain.Calendar, start, target, largest, increment, smallest, mode);
            return Temporal.FromInternal(realm, internalDur, largest);
        }
        if (TemporalMath.IsCalendarUnit(existingLargest) || TemporalMath.IsCalendarUnit(largest))
            throw JsErrors.RangeError(realm, "relativeTo is required to round durations with calendar units");
        var d = Temporal.ToInternalWith24HourDays(realm, duration.F);
        if (smallest == TUnit.Day)
        {
            var roundedNs = TemporalMath.RoundToIncrement(d.Time, (Int128)TemporalMath.NsPerDay * increment, mode);
            double days = (double)(roundedNs / TemporalMath.NsPerDay);
            d = new InternalDur(new DateDur(0, 0, 0, days), 0);
        }
        else d = new InternalDur(DateDur.Zero, Temporal.RoundTimeDuration(realm, d.Time, increment, smallest, mode));
        return Temporal.FromInternal(realm, d, largest);
    }

    private static double DurationTotal(in NativeArgs a, JsDuration duration)
    {
        var realm = a.Realm;
        if (a[0].IsUndefined) throw JsErrors.TypeError(realm, "total requires an options argument");
        JsObject totalOf;
        if (a[0].IsString)
        {
            totalOf = new JsObject(realm, null);
            totalOf.CreateDataPropertyOrThrow("unit", a[0]);
        }
        else totalOf = TemporalOptions.GetOptionsObject(realm, a[0])!;
        var (plain, zoned) = Temporal.GetRelativeTo(realm, totalOf);
        var unit = TemporalOptions.GetUnit(realm, totalOf, "unit");
        if (unit == TUnit.Unset) throw JsErrors.RangeError(realm, "unit is required");
        TemporalOptions.ValidateUnit(realm, unit, TemporalOptions.UnitGroup.DateTime, "unit");
        if (zoned != null)
        {
            var internalDur = Temporal.ToInternal(duration.F);
            var target = Temporal.AddZonedDateTime(realm, zoned.Calendar, zoned.Ns, zoned.Tz, internalDur, Overflow.Constrain);
            return Temporal.DifferenceZonedDateTimeWithTotal(realm, zoned.Calendar, zoned.Ns, target, zoned.Tz, unit);
        }
        if (plain != null)
        {
            var internalDur = Temporal.ToInternalWith24HourDays(realm, duration.F);
            var (days, targetTime) = TemporalMath.AddTime(TimeRec.Midnight, internalDur.Time);
            var dateDur = internalDur.Date with { Days = days };
            var targetDate = Temporal.CalendarDateAdd(realm, plain.Calendar, plain.Date, dateDur, Overflow.Constrain);
            return Temporal.DifferencePlainDateTimeWithTotal(realm, plain.Calendar, new IsoDateTime(plain.Date, TimeRec.Midnight), new IsoDateTime(targetDate, targetTime), unit);
        }
        var largest = Temporal.DefaultLargestUnit(duration.F);
        if (TemporalMath.IsCalendarUnit(largest) || TemporalMath.IsCalendarUnit(unit))
            throw JsErrors.RangeError(realm, "relativeTo is required for durations with calendar units");
        var d = Temporal.ToInternalWith24HourDays(realm, duration.F);
        return Temporal.TotalTimeDuration(d.Time, unit);
    }

    // ------------------------------------------------------------------ Instant

    private static void SetupInstant(Realm r, JsObject temporal, TemporalProtos protos)
    {
        var proto = Proto(r, "Instant", out _);
        protos.Instant = proto;
        var ctor = r.Constructor("Instant", 1, static (in NativeArgs a) =>
        {
            RequireNew(a, "Instant");
            var ns = BigIntOps.ToBigInt(a.Realm, a[0]);
            if (BigInteger.Abs(ns) > (BigInteger)TemporalMath.NsMaxInstant) throw JsErrors.RangeError(a.Realm, "instant is outside the supported range");
            return JsValue.FromObject(Temporal.CreateInstant(a.Realm, (Int128)ns, a.NewTarget.AsObjectUnchecked));
        }, proto);
        DefineCtor(temporal, ctor, "Instant");
        r.Method(ctor, "from", 1, static (in NativeArgs a) => JsValue.FromObject(Temporal.ToTemporalInstant(a.Realm, a[0])));
        r.Method(ctor, "fromEpochMilliseconds", 1, static (in NativeArgs a) =>
        {
            double ms = Operations.ToNumber(a.Realm, a[0]);
            if (double.IsNaN(ms) || double.IsInfinity(ms) || Math.Truncate(ms) != ms) throw JsErrors.RangeError(a.Realm, "epoch milliseconds must be an integer");
            if (Math.Abs(ms) > 8.64e15) throw JsErrors.RangeError(a.Realm, "instant is outside the supported range");
            return JsValue.FromObject(Temporal.CreateInstant(a.Realm, (Int128)(long)ms * 1_000_000));
        });
        r.Method(ctor, "fromEpochNanoseconds", 1, static (in NativeArgs a) =>
        {
            var ns = BigIntOps.ToBigInt(a.Realm, a[0]);
            if (BigInteger.Abs(ns) > (BigInteger)TemporalMath.NsMaxInstant) throw JsErrors.RangeError(a.Realm, "instant is outside the supported range");
            return JsValue.FromObject(Temporal.CreateInstant(a.Realm, (Int128)ns));
        });
        r.Method(ctor, "compare", 2, static (in NativeArgs a) =>
        {
            var one = Temporal.ToTemporalInstant(a.Realm, a[0]);
            var two = Temporal.ToTemporalInstant(a.Realm, a[1]);
            return new JsValue(one.Ns.CompareTo(two.Ns) switch { < 0 => -1, > 0 => 1, _ => 0 });
        });
        r.Getter(proto, "epochMilliseconds", "epochMilliseconds", static (in NativeArgs a) =>
            Num((double)TemporalMath.FloorDiv(This<JsInstant>(a, "epochMilliseconds").Ns, 1_000_000)));
        r.Getter(proto, "epochNanoseconds", "epochNanoseconds", static (in NativeArgs a) => Temporal.BigIntValue(This<JsInstant>(a, "epochNanoseconds").Ns));
        r.Method(proto, "add", 1, static (in NativeArgs a) => JsValue.FromObject(InstantAdd(a, This<JsInstant>(a, "Temporal.Instant.prototype.add"), 1)));
        r.Method(proto, "subtract", 1, static (in NativeArgs a) => JsValue.FromObject(InstantAdd(a, This<JsInstant>(a, "Temporal.Instant.prototype.subtract"), -1)));
        r.Method(proto, "until", 1, static (in NativeArgs a) => JsValue.FromObject(InstantDiff(a, This<JsInstant>(a, "Temporal.Instant.prototype.until"), false)));
        r.Method(proto, "since", 1, static (in NativeArgs a) => JsValue.FromObject(InstantDiff(a, This<JsInstant>(a, "Temporal.Instant.prototype.since"), true)));
        r.Method(proto, "round", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var i = This<JsInstant>(a, "Temporal.Instant.prototype.round");
            var roundTo = RoundToOptions(realm, a[0]);
            long increment = TemporalOptions.GetRoundingIncrement(realm, roundTo);
            var mode = TemporalOptions.GetRoundingMode(realm, roundTo, RoundingMode.HalfExpand);
            var smallest = TemporalOptions.GetUnit(realm, roundTo, "smallestUnit");
            if (smallest == TUnit.Unset) throw JsErrors.RangeError(realm, "smallestUnit is required");
            TemporalOptions.ValidateUnit(realm, smallest, TemporalOptions.UnitGroup.Time, "smallestUnit");
            long max = TemporalMath.NsPerDay / TemporalMath.UnitNs(smallest);
            TemporalOptions.ValidateIncrement(realm, increment, max, inclusive: true);
            var ns = RoundAsIfPositive(i.Ns, (Int128)TemporalMath.UnitNs(smallest) * increment, mode);
            return JsValue.FromObject(Temporal.CreateInstant(realm, ns));
        });
        r.Method(proto, "equals", 1, static (in NativeArgs a) =>
        {
            var i = This<JsInstant>(a, "Temporal.Instant.prototype.equals");
            var other = Temporal.ToTemporalInstant(a.Realm, a[0]);
            return JsValue.FromBool(i.Ns == other.Ns);
        });
        r.Method(proto, "toString", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var i = This<JsInstant>(a, "Temporal.Instant.prototype.toString");
            var opts = TemporalOptions.GetOptionsObject(realm, a[0]);
            int digits = TemporalOptions.GetFractionalSecondDigits(realm, opts);
            var mode = TemporalOptions.GetRoundingMode(realm, opts, RoundingMode.Trunc);
            var smallest = TemporalOptions.GetUnit(realm, opts, "smallestUnit");
            var tzv = opts == null ? JsValue.Undefined : opts.Get("timeZone");
            TemporalOptions.ValidateUnit(realm, smallest, TemporalOptions.UnitGroup.Time, "smallestUnit");
            if (smallest == TUnit.Hour) throw JsErrors.RangeError(realm, "hour is not a valid value for smallestUnit");
            TemporalTz? tz = tzv.IsUndefined ? null : Temporal.ToTimeZone(realm, tzv);
            var precision = Temporal.SecondsPrecision(smallest, digits);
            var ns = RoundAsIfPositive(i.Ns, (Int128)TemporalMath.UnitNs(precision.Unit) * precision.Increment, mode);
            if (!TemporalMath.IsValidEpochNanoseconds(ns)) throw JsErrors.RangeError(realm, "rounded instant is outside the supported range");
            return Str(InstantToString(ns, tz, precision.Precision));
        });
        r.Method(proto, "toJSON", 0, static (in NativeArgs a) => Str(InstantToString(This<JsInstant>(a, "Temporal.Instant.prototype.toJSON").Ns, null, -1)));
        r.Method(proto, "toLocaleString", 0, static (in NativeArgs a) =>
        {
            var i = This<JsInstant>(a, "Temporal.Instant.prototype.toLocaleString");
            if (a.Realm.Intl != null) return Str(IntlDateTimeFormat.FormatTemporalToLocaleString(a.Realm, i, a[0], a[1]));
            return Str(InstantToString(i.Ns, null, -1));
        });
        r.Method(proto, "valueOf", 0, static (in NativeArgs a) => ValueOf(a));
        r.Method(proto, "toZonedDateTimeISO", 1, static (in NativeArgs a) =>
        {
            var i = This<JsInstant>(a, "Temporal.Instant.prototype.toZonedDateTimeISO");
            var tz = Temporal.ToTimeZone(a.Realm, a[0]);
            return JsValue.FromObject(Temporal.CreateZonedDateTime(a.Realm, i.Ns, tz, Temporal.Iso8601));
        });
    }

    /// <summary>RoundNumberToIncrementAsIfPositive.</summary>
    internal static Int128 RoundAsIfPositive(Int128 x, Int128 increment, RoundingMode mode)
    {
        var um = TemporalMath.UnsignedMode(mode, negative: false);
        var q = TemporalMath.FloorDiv(x, increment);
        if (q * increment == x) return x;
        var chosen = TemporalMath.ApplyUnsignedRounding((BigInteger)x, (BigInteger)increment, (BigInteger)q, (BigInteger)q + 1, um);
        return (Int128)chosen * increment;
    }

    internal static string InstantToString(Int128 ns, TemporalTz? tz, int precision)
    {
        var outTz = tz ?? TemporalTimeZones.Utc;
        long offset = TemporalTimeZones.GetOffsetNs(outTz, ns);
        var dt = TemporalMath.GetISOPartsFromEpoch(ns + offset);
        var s = Temporal.DateTimeToString(dt, Temporal.Iso8601, precision, "never");
        return s + (tz == null ? "Z" : Temporal.FormatOffsetRounded(offset));
    }

    private static JsInstant InstantAdd(in NativeArgs a, JsInstant i, int sign)
    {
        var realm = a.Realm;
        var d = Temporal.ToTemporalDuration(realm, a[0]);
        var f = d.F;
        if (sign < 0) { f = (double[])f.Clone(); for (int k = 0; k < 10; k++) f[k] = -f[k]; }
        var largest = Temporal.DefaultLargestUnit(f);
        if (largest <= TUnit.Day) throw JsErrors.RangeError(realm, "durations with date units cannot be added to an Instant");
        var internalDur = Temporal.ToInternal(f);
        return Temporal.CreateInstant(realm, Temporal.AddInstant(realm, i.Ns, internalDur.Time));
    }

    private static JsDuration InstantDiff(in NativeArgs a, JsInstant i, bool since)
    {
        var realm = a.Realm;
        var other = Temporal.ToTemporalInstant(realm, a[0]);
        var opts = TemporalOptions.GetOptionsObject(realm, a[1]);
        var s = GetDifferenceSettings(realm, since, opts, TemporalOptions.UnitGroup.Time, Array.Empty<TUnit>(), TUnit.Nanosecond, TUnit.Second);
        var internalDur = Temporal.DifferenceInstant(realm, i.Ns, other.Ns, s.Increment, s.SmallestUnit, s.Mode);
        return NegateIf(realm, Temporal.FromInternal(realm, internalDur, s.LargestUnit), since);
    }

    // ------------------------------------------------------------------ Now

    private static void SetupNow(Realm r, JsObject temporal)
    {
        var now = new JsObject(r, r.ObjectPrototype);
        now.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("Temporal.Now"), PropFlags.Configurable);
        temporal.DefineHidden("Now", JsValue.FromObject(now));
        r.Method(now, "timeZoneId", 0, static (in NativeArgs a) => Str(SystemTimeZone(a.Realm).Id));
        r.Method(now, "instant", 0, static (in NativeArgs a) => JsValue.FromObject(Temporal.CreateInstant(a.Realm, SystemNs(a.Realm))));
        r.Method(now, "plainDateTimeISO", 0, static (in NativeArgs a) =>
            JsValue.FromObject(Temporal.CreatePlainDateTime(a.Realm, SystemDateTime(a.Realm, a[0]), Temporal.Iso8601)));
        r.Method(now, "plainDateISO", 0, static (in NativeArgs a) =>
            JsValue.FromObject(Temporal.CreatePlainDate(a.Realm, SystemDateTime(a.Realm, a[0]).Date, Temporal.Iso8601)));
        r.Method(now, "plainTimeISO", 0, static (in NativeArgs a) =>
            JsValue.FromObject(Temporal.CreatePlainTime(a.Realm, SystemDateTime(a.Realm, a[0]).Time)));
        r.Method(now, "zonedDateTimeISO", 0, static (in NativeArgs a) =>
        {
            var tz = a[0].IsUndefined ? SystemTimeZone(a.Realm) : Temporal.ToTimeZone(a.Realm, a[0]);
            return JsValue.FromObject(Temporal.CreateZonedDateTime(a.Realm, SystemNs(a.Realm), tz, Temporal.Iso8601));
        });
    }

    private static TemporalTz SystemTimeZone(Realm realm)
    {
        var info = realm.TimeZone;
        if (info == TimeZoneInfo.Utc || info.Id is "UTC" or "Etc/UTC") return TemporalTimeZones.Utc;
        return TemporalTimeZones.Resolve(info.Id) ?? TemporalTimeZones.FromOffset(info.BaseUtcOffset.Ticks * 100);
    }

    private static Int128 SystemNs(Realm realm)
    {
        var now = realm.Clock();
        return (Int128)(now.UtcTicks - DateTime.UnixEpoch.Ticks) * 100;
    }

    private static IsoDateTime SystemDateTime(Realm realm, in JsValue tzLike)
    {
        var tz = tzLike.IsUndefined ? SystemTimeZone(realm) : Temporal.ToTimeZone(realm, tzLike);
        return Temporal.GetISODateTimeFor(tz, SystemNs(realm));
    }
}
