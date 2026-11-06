using System;

namespace Jaunt.Runtime;

internal static partial class TemporalBuiltins
{
    private static void SetupZonedDateTime(Realm r, JsObject temporal, TemporalProtos protos)
    {
        var proto = Proto(r, "ZonedDateTime", out _);
        protos.ZonedDateTime = proto;
        var ctor = r.Constructor("ZonedDateTime", 2, static (in NativeArgs a) =>
        {
            RequireNew(a, "ZonedDateTime");
            var realm = a.Realm;
            var ns = BigIntOps.ToBigInt(realm, a[0]);
            if (System.Numerics.BigInteger.Abs(ns) > (System.Numerics.BigInteger)TemporalMath.NsMaxInstant) throw JsErrors.RangeError(realm, "instant is outside the supported range");
            var tz = Temporal.TimeZoneFromConstructorArg(realm, a[1]);
            var cal = Temporal.CalendarFromConstructorArg(realm, a[2]);
            return JsValue.FromObject(Temporal.CreateZonedDateTime(realm, (Int128)ns, tz, cal, a.NewTarget.AsObjectUnchecked));
        }, proto);
        DefineCtor(temporal, ctor, "ZonedDateTime");
        r.Method(ctor, "from", 1, static (in NativeArgs a) => JsValue.FromObject(Temporal.ToTemporalZonedDateTime(a.Realm, a[0], a[1])));
        r.Method(ctor, "compare", 2, static (in NativeArgs a) =>
        {
            var one = Temporal.ToTemporalZonedDateTime(a.Realm, a[0], JsValue.Undefined);
            var two = Temporal.ToTemporalZonedDateTime(a.Realm, a[1], JsValue.Undefined);
            return Num(one.Ns.CompareTo(two.Ns) switch { < 0 => -1, > 0 => 1, _ => 0 });
        });
        InstallDateGetters<JsZonedDateTime>(r, proto);
        InstallTimeGetters<JsZonedDateTime>(r, proto);
        r.Getter(proto, "timeZoneId", "timeZoneId", static (in NativeArgs a) => Str(This<JsZonedDateTime>(a, "timeZoneId").Tz.Id));
        r.Getter(proto, "epochMilliseconds", "epochMilliseconds", static (in NativeArgs a) =>
            Num((double)TemporalMath.FloorDiv(This<JsZonedDateTime>(a, "epochMilliseconds").Ns, 1_000_000)));
        r.Getter(proto, "epochNanoseconds", "epochNanoseconds", static (in NativeArgs a) => Temporal.BigIntValue(This<JsZonedDateTime>(a, "epochNanoseconds").Ns));
        r.Getter(proto, "offsetNanoseconds", "offsetNanoseconds", static (in NativeArgs a) =>
        {
            var z = This<JsZonedDateTime>(a, "offsetNanoseconds");
            return Num(TemporalTimeZones.GetOffsetNs(z.Tz, z.Ns));
        });
        r.Getter(proto, "offset", "offset", static (in NativeArgs a) =>
        {
            var z = This<JsZonedDateTime>(a, "offset");
            return Str(TemporalTimeZones.FormatOffsetNs(TemporalTimeZones.GetOffsetNs(z.Tz, z.Ns)));
        });
        r.Getter(proto, "hoursInDay", "hoursInDay", static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var z = This<JsZonedDateTime>(a, "hoursInDay");
            var today = Temporal.GetISODateTimeFor(z.Tz, z.Ns).Date;
            var tomorrow = TemporalMath.AddDays(today, 1);
            var start = TemporalTimeZones.GetStartOfDay(realm, z.Tz, today);
            var end = TemporalTimeZones.GetStartOfDay(realm, z.Tz, tomorrow);
            return Num(Temporal.TotalTimeDuration(end - start, TUnit.Hour));
        });
        r.Method(proto, "with", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var z = This<JsZonedDateTime>(a, "Temporal.ZonedDateTime.prototype.with");
            if (!a[0].IsObject) throw JsErrors.TypeError(realm, "with requires an object");
            var item = a[0].AsObjectUnchecked;
            Temporal.RejectTemporalLike(realm, item);
            long offsetNs = TemporalTimeZones.GetOffsetNs(z.Tz, z.Ns);
            var dt = Temporal.GetISODateTimeFor(z.Tz, z.Ns);
            var fields = Temporal.DateToFields(z.Calendar, dt.Date, CalendarType.Date);
            Temporal.SetTimeFields(fields, dt.Time);
            fields.Offset = TemporalTimeZones.FormatOffsetNs(offsetNs);
            var partial = Temporal.PrepareFields(realm, z.Calendar, item, FieldSet.Date | FieldSet.Time | FieldSet.Offset, FieldSet.None, partial: true);
            fields = Temporal.MergeFields(z.Calendar, fields, partial);
            var opts = TemporalOptions.GetOptionsObject(realm, a[1]);
            var disambiguation = TemporalOptions.GetDisambiguation(realm, opts);
            var offsetOption = TemporalOptions.GetOffsetOption(realm, opts, OffsetOption.Prefer);
            var overflow = TemporalOptions.GetOverflow(realm, opts);
            var date = Temporal.DateFromFields(realm, z.Calendar, fields, overflow);
            var time = Temporal.TimeFromFields(realm, fields, overflow);
            long newOffset = Temporal.ParseOffset(realm, fields.Offset!);
            var ns = Temporal.InterpretISODateTimeOffset(realm, date, time, OffsetBehaviour.Option, newOffset, z.Tz, disambiguation, offsetOption, matchMinutes: false);
            return JsValue.FromObject(Temporal.CreateZonedDateTime(realm, ns, z.Tz, z.Calendar));
        });
        r.Method(proto, "withPlainTime", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var z = This<JsZonedDateTime>(a, "Temporal.ZonedDateTime.prototype.withPlainTime");
            var date = Temporal.GetISODateTimeFor(z.Tz, z.Ns).Date;
            Int128 ns;
            if (a[0].IsUndefined) ns = TemporalTimeZones.GetStartOfDay(realm, z.Tz, date);
            else
            {
                var t = Temporal.ToTemporalTime(realm, a[0], JsValue.Undefined).Time;
                ns = TemporalTimeZones.GetEpochNsFor(realm, z.Tz, new IsoDateTime(date, t), Disambiguation.Compatible);
            }
            return JsValue.FromObject(Temporal.CreateZonedDateTime(realm, ns, z.Tz, z.Calendar));
        });
        r.Method(proto, "withTimeZone", 1, static (in NativeArgs a) =>
        {
            var z = This<JsZonedDateTime>(a, "Temporal.ZonedDateTime.prototype.withTimeZone");
            var tz = Temporal.ToTimeZone(a.Realm, a[0]);
            return JsValue.FromObject(Temporal.CreateZonedDateTime(a.Realm, z.Ns, tz, z.Calendar));
        });
        r.Method(proto, "withCalendar", 1, static (in NativeArgs a) =>
        {
            var z = This<JsZonedDateTime>(a, "Temporal.ZonedDateTime.prototype.withCalendar");
            if (a[0].IsUndefined) throw JsErrors.TypeError(a.Realm, "calendar is required");
            return JsValue.FromObject(Temporal.CreateZonedDateTime(a.Realm, z.Ns, z.Tz, Temporal.ToCalendarIdentifier(a.Realm, a[0])));
        });
        r.Method(proto, "add", 1, static (in NativeArgs a) => JsValue.FromObject(ZonedAdd(a, This<JsZonedDateTime>(a, "Temporal.ZonedDateTime.prototype.add"), 1)));
        r.Method(proto, "subtract", 1, static (in NativeArgs a) => JsValue.FromObject(ZonedAdd(a, This<JsZonedDateTime>(a, "Temporal.ZonedDateTime.prototype.subtract"), -1)));
        r.Method(proto, "until", 1, static (in NativeArgs a) => JsValue.FromObject(ZonedDiff(a, This<JsZonedDateTime>(a, "Temporal.ZonedDateTime.prototype.until"), false)));
        r.Method(proto, "since", 1, static (in NativeArgs a) => JsValue.FromObject(ZonedDiff(a, This<JsZonedDateTime>(a, "Temporal.ZonedDateTime.prototype.since"), true)));
        r.Method(proto, "round", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var z = This<JsZonedDateTime>(a, "Temporal.ZonedDateTime.prototype.round");
            var roundTo = RoundToOptions(realm, a[0]);
            long increment = TemporalOptions.GetRoundingIncrement(realm, roundTo);
            var mode = TemporalOptions.GetRoundingMode(realm, roundTo, RoundingMode.HalfExpand);
            var smallest = TemporalOptions.GetUnit(realm, roundTo, "smallestUnit");
            if (smallest == TUnit.Unset) throw JsErrors.RangeError(realm, "smallestUnit is required");
            TemporalOptions.ValidateUnit(realm, smallest, TemporalOptions.UnitGroup.Time, "smallestUnit", extra: TUnit.Day);
            if (smallest == TUnit.Day) TemporalOptions.ValidateIncrement(realm, increment, 1, inclusive: true);
            else TemporalOptions.ValidateIncrement(realm, increment, TemporalOptions.MaxIncrement(smallest), inclusive: false);
            if (smallest == TUnit.Nanosecond && increment == 1) return JsValue.FromObject(Temporal.CreateZonedDateTime(realm, z.Ns, z.Tz, z.Calendar));
            var dt = Temporal.GetISODateTimeFor(z.Tz, z.Ns);
            Int128 ns;
            if (smallest == TUnit.Day)
            {
                var start = TemporalTimeZones.GetStartOfDay(realm, z.Tz, dt.Date);
                var end = TemporalTimeZones.GetStartOfDay(realm, z.Tz, TemporalMath.AddDays(dt.Date, 1));
                var dayLength = end - start;
                var progress = z.Ns - start;
                if (progress >= dayLength)
                {
                    // the next date started before this instant (a backward shift repeats the date's start):
                    // round by the wall-clock time of day
                    var r = TemporalMath.RoundToIncrement(dt.Time.TotalNs, TemporalMath.NsPerDay, mode);
                    ns = r == 0 ? start : end;
                }
                else ns = start + TemporalMath.RoundToIncrement(progress, dayLength, mode);
            }
            else
            {
                var rdt = RoundISODateTime(dt, increment, smallest, mode);
                long offsetNs = TemporalTimeZones.GetOffsetNs(z.Tz, z.Ns);
                ns = Temporal.InterpretISODateTimeOffset(realm, rdt.Date, rdt.Time, OffsetBehaviour.Option, offsetNs, z.Tz, Disambiguation.Compatible, OffsetOption.Prefer, matchMinutes: false);
            }
            return JsValue.FromObject(Temporal.CreateZonedDateTime(realm, ns, z.Tz, z.Calendar));
        });
        r.Method(proto, "equals", 1, static (in NativeArgs a) =>
        {
            var z = This<JsZonedDateTime>(a, "Temporal.ZonedDateTime.prototype.equals");
            var other = Temporal.ToTemporalZonedDateTime(a.Realm, a[0], JsValue.Undefined);
            return JsValue.FromBool(z.Ns == other.Ns && TemporalTimeZones.Equals(z.Tz, other.Tz) && z.Calendar == other.Calendar);
        });
        r.Method(proto, "startOfDay", 0, static (in NativeArgs a) =>
        {
            var z = This<JsZonedDateTime>(a, "Temporal.ZonedDateTime.prototype.startOfDay");
            var date = Temporal.GetISODateTimeFor(z.Tz, z.Ns).Date;
            return JsValue.FromObject(Temporal.CreateZonedDateTime(a.Realm, TemporalTimeZones.GetStartOfDay(a.Realm, z.Tz, date), z.Tz, z.Calendar));
        });
        r.Method(proto, "getTimeZoneTransition", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var z = This<JsZonedDateTime>(a, "Temporal.ZonedDateTime.prototype.getTimeZoneTransition");
            if (a[0].IsUndefined) throw JsErrors.TypeError(realm, "getTimeZoneTransition requires a direction");
            JsObject opts;
            if (a[0].IsString)
            {
                opts = new JsObject(realm, null);
                opts.CreateDataPropertyOrThrow("direction", a[0]);
            }
            else opts = TemporalOptions.GetOptionsObject(realm, a[0])!;
            var dir = TemporalOptions.GetString(realm, opts, "direction", new[] { "next", "previous" });
            if (dir == null) throw JsErrors.RangeError(realm, "direction is required");
            if (z.Tz.IsFixed) return JsValue.Null;
            var t = dir == "next" ? TemporalTimeZones.GetNextTransition(z.Tz, z.Ns) : TemporalTimeZones.GetPreviousTransition(z.Tz, z.Ns);
            if (t == null || !TemporalMath.IsValidEpochNanoseconds(t.Value)) return JsValue.Null;
            return JsValue.FromObject(Temporal.CreateZonedDateTime(realm, t.Value, z.Tz, z.Calendar));
        });
        r.Method(proto, "toInstant", 0, static (in NativeArgs a) =>
            JsValue.FromObject(Temporal.CreateInstant(a.Realm, This<JsZonedDateTime>(a, "Temporal.ZonedDateTime.prototype.toInstant").Ns)));
        r.Method(proto, "toPlainDate", 0, static (in NativeArgs a) =>
        {
            var z = This<JsZonedDateTime>(a, "Temporal.ZonedDateTime.prototype.toPlainDate");
            return JsValue.FromObject(Temporal.CreatePlainDate(a.Realm, Temporal.GetISODateTimeFor(z.Tz, z.Ns).Date, z.Calendar));
        });
        r.Method(proto, "toPlainTime", 0, static (in NativeArgs a) =>
        {
            var z = This<JsZonedDateTime>(a, "Temporal.ZonedDateTime.prototype.toPlainTime");
            return JsValue.FromObject(Temporal.CreatePlainTime(a.Realm, Temporal.GetISODateTimeFor(z.Tz, z.Ns).Time));
        });
        r.Method(proto, "toPlainDateTime", 0, static (in NativeArgs a) =>
        {
            var z = This<JsZonedDateTime>(a, "Temporal.ZonedDateTime.prototype.toPlainDateTime");
            return JsValue.FromObject(Temporal.CreatePlainDateTime(a.Realm, Temporal.GetISODateTimeFor(z.Tz, z.Ns), z.Calendar));
        });
        r.Method(proto, "toString", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var z = This<JsZonedDateTime>(a, "Temporal.ZonedDateTime.prototype.toString");
            var opts = TemporalOptions.GetOptionsObject(realm, a[0]);
            var showCal = TemporalOptions.GetShowCalendarName(realm, opts);
            int digits = TemporalOptions.GetFractionalSecondDigits(realm, opts);
            var showOffset = TemporalOptions.GetShowOffset(realm, opts);
            var mode = TemporalOptions.GetRoundingMode(realm, opts, RoundingMode.Trunc);
            var smallest = TemporalOptions.GetUnit(realm, opts, "smallestUnit");
            var showTz = TemporalOptions.GetShowTimeZoneName(realm, opts);
            TemporalOptions.ValidateUnit(realm, smallest, TemporalOptions.UnitGroup.Time, "smallestUnit");
            if (smallest == TUnit.Hour) throw JsErrors.RangeError(realm, "hour is not a valid value for smallestUnit");
            var p = Temporal.SecondsPrecision(smallest, digits);
            return Str(ZonedToString(realm, z, p.Precision, showCal, showTz, showOffset, p.Increment, p.Unit, mode));
        });
        r.Method(proto, "toJSON", 0, static (in NativeArgs a) =>
            Str(ZonedToString(a.Realm, This<JsZonedDateTime>(a, "Temporal.ZonedDateTime.prototype.toJSON"), -1, "auto", "auto", "auto", 1, TUnit.Nanosecond, RoundingMode.Trunc)));
        r.Method(proto, "toLocaleString", 0, static (in NativeArgs a) =>
        {
            var z = This<JsZonedDateTime>(a, "Temporal.ZonedDateTime.prototype.toLocaleString");
            if (a.Realm.Intl != null) return Str(IntlDateTimeFormat.FormatTemporalToLocaleString(a.Realm, z, a[0], a[1]));
            return Str(ZonedToString(a.Realm, z, -1, "auto", "auto", "auto", 1, TUnit.Nanosecond, RoundingMode.Trunc));
        });
        r.Method(proto, "valueOf", 0, static (in NativeArgs a) => ValueOf(a));
    }

    private static string ZonedToString(Realm realm, JsZonedDateTime z, int precision, string showCal, string showTz, string showOffset, long increment, TUnit unit, RoundingMode mode)
    {
        var ns = RoundAsIfPositive(z.Ns, (Int128)TemporalMath.UnitNs(unit) * increment, mode);
        if (!TemporalMath.IsValidEpochNanoseconds(ns)) throw JsErrors.RangeError(realm, "rounded instant is outside the supported range");
        long offset = TemporalTimeZones.GetOffsetNs(z.Tz, ns);
        var dt = TemporalMath.GetISOPartsFromEpoch(ns + offset);
        var s = Temporal.DateTimeToString(dt, Temporal.Iso8601, precision, "never");
        if (showOffset != "never") s += Temporal.FormatOffsetRounded(offset);
        if (showTz != "never") s += (showTz == "critical" ? "[!" : "[") + z.Tz.Id + "]";
        return s + Temporal.CalendarAnnotation(z.Calendar, showCal);
    }

    private static JsZonedDateTime ZonedAdd(in NativeArgs a, JsZonedDateTime z, int sign)
    {
        var realm = a.Realm;
        var dur = Temporal.ToTemporalDuration(realm, a[0]);
        var f = dur.F;
        if (sign < 0) { f = (double[])f.Clone(); for (int i = 0; i < 10; i++) f[i] = -f[i]; }
        var overflow = TemporalOptions.GetOverflow(realm, TemporalOptions.GetOptionsObject(realm, a[1]));
        var internalDur = Temporal.ToInternal(f);
        var ns = Temporal.AddZonedDateTime(realm, z.Calendar, z.Ns, z.Tz, internalDur, overflow);
        return Temporal.CreateZonedDateTime(realm, ns, z.Tz, z.Calendar);
    }

    private static JsDuration ZonedDiff(in NativeArgs a, JsZonedDateTime z, bool since)
    {
        var realm = a.Realm;
        var other = Temporal.ToTemporalZonedDateTime(realm, a[0], JsValue.Undefined);
        if (other.Calendar != z.Calendar) throw JsErrors.RangeError(realm, "cannot compute the difference between date-times of different calendars");
        var opts = TemporalOptions.GetOptionsObject(realm, a[1]);
        var s = GetDifferenceSettings(realm, since, opts, TemporalOptions.UnitGroup.DateTime, s_noUnits, TUnit.Nanosecond, TUnit.Hour);
        if (s.LargestUnit > TUnit.Day)
        {
            var d = Temporal.DifferenceInstant(realm, z.Ns, other.Ns, s.Increment, s.SmallestUnit, s.Mode);
            return NegateIf(realm, Temporal.FromInternal(realm, d, s.LargestUnit), since);
        }
        if (!TemporalTimeZones.Equals(z.Tz, other.Tz)) throw JsErrors.RangeError(realm, "cannot compute calendar differences between different time zones");
        if (z.Ns == other.Ns) return Temporal.CreateDuration(realm, new double[10]);
        var internalDur = Temporal.DifferenceZonedDateTimeWithRounding(realm, z.Calendar, z.Ns, other.Ns, z.Tz, s.LargestUnit, s.Increment, s.SmallestUnit, s.Mode);
        return NegateIf(realm, Temporal.FromInternal(realm, internalDur, TUnit.Hour), since);
    }
}
