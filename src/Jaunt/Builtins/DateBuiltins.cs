using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Jaunt.Runtime;

internal sealed class JsDate : JsObject
{
    public double Time;
    public JsDate(Realm realm, JsObject proto, double t) : base(realm, proto) => Time = t;
    public override string ClassName => "Date";
}

internal sealed partial class Realm
{
    public TimeZoneInfo TimeZone = TimeZoneInfo.Utc;
    public Func<DateTimeOffset> Clock = static () => DateTimeOffset.UtcNow;
}

internal static class DateBuiltins
{
    private const double MsPerDay = 86400000;
    private static readonly string[] DayNames = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
    private static readonly string[] MonthNames = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

    // ------------------------------------------------------------------ spec date arithmetic (21.4.1)

    private static double Day(double t) => Math.Floor(t / MsPerDay);
    private static double TimeWithinDay(double t) { double r = t % MsPerDay; return r < 0 ? r + MsPerDay : r; }
    private static double DaysInYear(double y) => (y % 4 != 0) ? 365 : (y % 100 != 0) ? 366 : (y % 400 != 0) ? 365 : 366;
    private static double DayFromYear(double y) => 365 * (y - 1970) + Math.Floor((y - 1969) / 4) - Math.Floor((y - 1901) / 100) + Math.Floor((y - 1601) / 400);
    private static double TimeFromYear(double y) => MsPerDay * DayFromYear(y);

    /// <summary>Largest |time value| the integer calendar conversion handles (about 3 million years: far beyond the Date range).</summary>
    private const double CivilLimit = 1e17;

    /// <summary>Year, month (1-12) and day of month (1-31) of a count of days since 1970-01-01 (proleptic Gregorian).</summary>
    private static void CivilFromDays(long z, out long y, out int m, out int d)
    {
        z += 719468;
        long era = (z >= 0 ? z : z - 146096) / 146097;
        long doe = z - era * 146097;
        long yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
        y = yoe + era * 400;
        long doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
        long mp = (5 * doy + 2) / 153;
        d = (int)(doy - (153 * mp + 2) / 5 + 1);
        m = (int)(mp < 10 ? mp + 3 : mp - 9);
        if (m <= 2) y++;
    }

    public static double YearFromTime(double t)
    {
        if (Math.Abs(t) < CivilLimit)
        {
            CivilFromDays((long)Math.Floor(t / MsPerDay), out long cy, out _, out _);
            return cy;
        }
        double y = Math.Floor(t / (MsPerDay * 365.2425)) + 1970;
        while (TimeFromYear(y) > t) y--;
        while (TimeFromYear(y + 1) <= t) y++;
        return y;
    }

    private static bool InLeapYear(double t) => DaysInYear(YearFromTime(t)) == 366;
    private static double DayWithinYear(double t) => Day(t) - DayFromYear(YearFromTime(t));

    private static readonly int[] CumDays = { 0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334, 365 };

    public static double MonthFromTime(double t)
    {
        if (Math.Abs(t) < CivilLimit)
        {
            CivilFromDays((long)Math.Floor(t / MsPerDay), out _, out int cm, out _);
            return cm - 1;
        }
        double d = DayWithinYear(t);
        int leap = InLeapYear(t) ? 1 : 0;
        for (int m = 0; m < 12; m++)
        {
            double end = CumDays[m + 1] + (m >= 1 ? leap : 0);
            if (d < end) return m;
        }
        return 11;
    }

    public static double DateFromTime(double t)
    {
        if (Math.Abs(t) < CivilLimit)
        {
            CivilFromDays((long)Math.Floor(t / MsPerDay), out _, out _, out int cd);
            return cd;
        }
        double d = DayWithinYear(t);
        int m = (int)MonthFromTime(t);
        int leap = InLeapYear(t) && m >= 2 ? 1 : 0;
        return d - CumDays[m] - leap + 1;
    }

    private static double WeekDay(double t) { double r = (Day(t) + 4) % 7; return r < 0 ? r + 7 : r; }
    private static double HourFromTime(double t) => Math.Floor(TimeWithinDay(t) / 3600000) % 24;
    private static double MinFromTime(double t) => Math.Floor(TimeWithinDay(t) / 60000) % 60;
    private static double SecFromTime(double t) => Math.Floor(TimeWithinDay(t) / 1000) % 60;
    private static double MsFromTime(double t) => TimeWithinDay(t) % 1000;

    private static double ToInt(double d) => double.IsNaN(d) ? 0 : Math.Truncate(d);

    public static double MakeTime(double h, double m, double s, double ms)
    {
        if (!double.IsFinite(h) || !double.IsFinite(m) || !double.IsFinite(s) || !double.IsFinite(ms)) return double.NaN;
        return ToInt(h) * 3600000 + ToInt(m) * 60000 + ToInt(s) * 1000 + ToInt(ms);
    }

    public static double MakeDay(double year, double month, double date)
    {
        if (!double.IsFinite(year) || !double.IsFinite(month) || !double.IsFinite(date)) return double.NaN;
        double y = ToInt(year), m = ToInt(month), dt = ToInt(date);
        double ym = y + Math.Floor(m / 12);
        if (Math.Abs(ym) > 400000) return double.NaN;
        int mn = (int)(((m % 12) + 12) % 12);
        double day = DayFromYear(ym) + CumDays[mn] + (mn >= 2 && DaysInYear(ym) == 366 ? 1 : 0);
        return day + dt - 1;
    }

    public static double MakeDate(double day, double time) => !double.IsFinite(day) || !double.IsFinite(time) ? double.NaN : day * MsPerDay + time;

    public static double TimeClip(double t)
    {
        if (!double.IsFinite(t) || Math.Abs(t) > 8.64e15) return double.NaN;
        return ToInt(t) + 0.0;
    }

    private static double OffsetMs(Realm realm, double utc)
    {
        if (ReferenceEquals(realm.TimeZone, TimeZoneInfo.Utc) || double.IsNaN(utc)) return 0;
        double clamped = Math.Max(Math.Min(utc, 253402300799000), -62135596800000);
        var dt = DateTimeOffset.FromUnixTimeMilliseconds((long)clamped).UtcDateTime;
        return realm.TimeZone.GetUtcOffset(dt).TotalMilliseconds;
    }

    private static double LocalTime(Realm realm, double t) => t + OffsetMs(realm, t);

    private static double Utc(Realm realm, double local)
    {
        if (!double.IsFinite(local)) return double.NaN;
        double guess = local - OffsetMs(realm, local);
        return local - OffsetMs(realm, guess);
    }

    // ------------------------------------------------------------------ setup

    public static void Setup(Realm r)
    {
        var proto = new JsObject(r, r.ObjectPrototype);
        r.DatePrototype = proto;
        var ctor = r.Constructor("Date", 7, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (!a.IsConstruct) return new JsValue(ToDateString(realm, Now(realm)));
            double tv;
            if (a.Length == 0) tv = Now(realm);
            else if (a.Length == 1)
            {
                var v = a[0];
                if (v.Ref is JsDate d) tv = d.Time;
                else
                {
                    var p = Operations.ToPrimitive(realm, v);
                    tv = p.IsString ? Parse(realm, p.AsStringUnchecked) : TimeClip(Operations.ToNumber(realm, p));
                }
            }
            else tv = TimeClip(Utc(realm, FromComponents(realm, a)));
            var proto2 = Operations.GetPrototypeFromConstructor(realm, a.NewTarget.AsObjectUnchecked, realm.DatePrototype);
            return JsValue.FromObject(new JsDate(realm, proto2, TimeClip(tv)));
        }, proto);
        r.DefineGlobal("Date", JsValue.FromObject(ctor));
        r.Method(ctor, "now", 0, static (in NativeArgs a) => new JsValue(Now(a.Realm)));
        r.Method(ctor, "parse", 1, static (in NativeArgs a) => new JsValue(Parse(a.Realm, Operations.ToString(a.Realm, a[0]))));
        r.Method(ctor, "UTC", 7, static (in NativeArgs a) => new JsValue(TimeClip(FromComponents(a.Realm, a))));

        Getter(r, proto, "getTime", (realm, t) => t);
        Getter(r, proto, "valueOf", (realm, t) => t);
        Getter(r, proto, "getFullYear", (realm, t) => YearFromTime(LocalTime(realm, t)));
        Getter(r, proto, "getMonth", (realm, t) => MonthFromTime(LocalTime(realm, t)));
        Getter(r, proto, "getDate", (realm, t) => DateFromTime(LocalTime(realm, t)));
        Getter(r, proto, "getDay", (realm, t) => WeekDay(LocalTime(realm, t)));
        Getter(r, proto, "getHours", (realm, t) => HourFromTime(LocalTime(realm, t)));
        Getter(r, proto, "getMinutes", (realm, t) => MinFromTime(LocalTime(realm, t)));
        Getter(r, proto, "getSeconds", (realm, t) => SecFromTime(LocalTime(realm, t)));
        Getter(r, proto, "getMilliseconds", (realm, t) => MsFromTime(LocalTime(realm, t)));
        Getter(r, proto, "getUTCFullYear", (realm, t) => YearFromTime(t));
        Getter(r, proto, "getUTCMonth", (realm, t) => MonthFromTime(t));
        Getter(r, proto, "getUTCDate", (realm, t) => DateFromTime(t));
        Getter(r, proto, "getUTCDay", (realm, t) => WeekDay(t));
        Getter(r, proto, "getUTCHours", (realm, t) => HourFromTime(t));
        Getter(r, proto, "getUTCMinutes", (realm, t) => MinFromTime(t));
        Getter(r, proto, "getUTCSeconds", (realm, t) => SecFromTime(t));
        Getter(r, proto, "getUTCMilliseconds", (realm, t) => MsFromTime(t));
        Getter(r, proto, "getTimezoneOffset", (realm, t) => (t - LocalTime(realm, t)) / 60000);

        r.Method(proto, "setTime", 1, static (in NativeArgs a) =>
        {
            var d = ThisDate(a);
            d.Time = TimeClip(Operations.ToNumber(a.Realm, a[0]));
            return new JsValue(d.Time);
        });
        Setter(r, proto, "setMilliseconds", 1, local: true, 3);
        Setter(r, proto, "setUTCMilliseconds", 1, local: false, 3);
        Setter(r, proto, "setSeconds", 2, local: true, 2);
        Setter(r, proto, "setUTCSeconds", 2, local: false, 2);
        Setter(r, proto, "setMinutes", 3, local: true, 1);
        Setter(r, proto, "setUTCMinutes", 3, local: false, 1);
        Setter(r, proto, "setHours", 4, local: true, 0);
        Setter(r, proto, "setUTCHours", 4, local: false, 0);
        DateSetter(r, proto, "setDate", 1, local: true, 2);
        DateSetter(r, proto, "setUTCDate", 1, local: false, 2);
        DateSetter(r, proto, "setMonth", 2, local: true, 1);
        DateSetter(r, proto, "setUTCMonth", 2, local: false, 1);
        DateSetter(r, proto, "setFullYear", 3, local: true, 0);
        DateSetter(r, proto, "setUTCFullYear", 3, local: false, 0);

        // Annex B
        Getter(r, proto, "getYear", (realm, t) => YearFromTime(LocalTime(realm, t)) - 1900);
        r.Method(proto, "setYear", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var d = ThisDate(a);
            double t = double.IsNaN(d.Time) ? 0 : LocalTime(realm, d.Time);
            double y = Operations.ToNumber(realm, a[0]);
            if (double.IsNaN(y)) { d.Time = double.NaN; return JsValue.NaN; }
            double yi = Math.Truncate(y);
            if (yi >= 0 && yi <= 99) yi += 1900;
            double nd = MakeDate(MakeDay(yi, MonthFromTime(t), DateFromTime(t)), TimeWithinDay(t));
            d.Time = TimeClip(Utc(realm, nd));
            return new JsValue(d.Time);
        });
        r.Method(proto, "toTemporalInstant", 0, static (in NativeArgs a) =>
        {
            var d = ThisDate(a);
            if (double.IsNaN(d.Time)) throw JsErrors.RangeError(a.Realm, "Invalid time value");
            return JsValue.FromObject(Temporal.CreateInstant(a.Realm, (Int128)(long)d.Time * 1_000_000));
        });
        r.Method(proto, "toISOString", 0, static (in NativeArgs a) =>
        {
            var d = ThisDate(a);
            if (double.IsNaN(d.Time)) throw JsErrors.RangeError(a.Realm, "Invalid time value");
            return new JsValue(IsoString(d.Time));
        });
        r.Method(proto, "toJSON", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var o = Operations.ToObject(realm, a.This);
            var tv = Operations.ToPrimitive(realm, JsValue.FromObject(o), Operations.PreferredType.Number);
            if (tv.IsNumber && !double.IsFinite(tv.Num)) return JsValue.Null;
            return Operations.Invoke(realm, JsValue.FromObject(o), "toISOString", ReadOnlySpan<JsValue>.Empty);
        });
        r.Method(proto, "toString", 0, static (in NativeArgs a) => new JsValue(ToDateString(a.Realm, ThisDate(a).Time)));
        r.Method(proto, "toDateString", 0, static (in NativeArgs a) =>
        {
            double t = ThisDate(a).Time;
            if (double.IsNaN(t)) return new JsValue("Invalid Date");
            double lt = LocalTime(a.Realm, t);
            return new JsValue(DatePart(lt));
        });
        r.Method(proto, "toTimeString", 0, static (in NativeArgs a) =>
        {
            double t = ThisDate(a).Time;
            if (double.IsNaN(t)) return new JsValue("Invalid Date");
            return new JsValue(TimePart(a.Realm, t));
        });
        r.Method(proto, "toUTCString", 0, static (in NativeArgs a) =>
        {
            double t = ThisDate(a).Time;
            if (double.IsNaN(t)) return new JsValue("Invalid Date");
            return new JsValue($"{DayNames[(int)WeekDay(t)]}, {Pad2(DateFromTime(t))} {MonthNames[(int)MonthFromTime(t)]} {YearString(YearFromTime(t))} {Pad2(HourFromTime(t))}:{Pad2(MinFromTime(t))}:{Pad2(SecFromTime(t))} GMT");
        });
        proto.DefineHidden("toGMTString", proto.Get("toUTCString"));
        r.Method(proto, "toLocaleString", 0, static (in NativeArgs a) => new JsValue(ToLocale(a, 0, "G")));
        r.Method(proto, "toLocaleDateString", 0, static (in NativeArgs a) => new JsValue(ToLocale(a, 1, "d")));
        r.Method(proto, "toLocaleTimeString", 0, static (in NativeArgs a) => new JsValue(ToLocale(a, 2, "T")));
        r.Method(proto, JsSymbol.ToPrimitive, "[Symbol.toPrimitive]", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a.This.IsNotObject) throw JsErrors.TypeError(realm, "Date.prototype[@@toPrimitive] called on non-object");
            var hint = a[0].IsString ? a[0].AsStringUnchecked : "";
            var pt = hint switch
            {
                "string" or "default" => Operations.PreferredType.String,
                "number" => Operations.PreferredType.Number,
                _ => throw JsErrors.TypeError(realm, "Invalid hint"),
            };
            return Operations.OrdinaryToPrimitive(realm, a.This.AsObjectUnchecked, pt);
        }, PropFlags.Configurable);
    }

    private static double Now(Realm realm) => Math.Floor((double)realm.Clock().ToUnixTimeMilliseconds());

    private static JsDate ThisDate(in NativeArgs a)
    {
        if (a.This.Ref is JsDate d) return d;
        throw JsErrors.TypeError(a.Realm, "this is not a Date object.");
    }

    private static void Getter(Realm r, JsObject proto, string name, Func<Realm, double, double> f)
    {
        var fn = r.MethodEager(proto, name, 0, static (in NativeArgs a) =>
        {
            var d = ThisDate(a);
            if (double.IsNaN(d.Time)) return JsValue.NaN;
            return new JsValue(((Func<Realm, double, double>)a.Callee.State!)(a.Realm, d.Time));
        });
        fn.State = f;
    }

    private static void Setter(Realm r, JsObject proto, string name, int length, bool local, int first)
    {
        var fn = r.MethodEager(proto, name, length, static (in NativeArgs a) =>
        {
            var (local, first) = ((bool, int))a.Callee.State!;
            var realm = a.Realm;
            var d = ThisDate(a);
            double t0 = d.Time; // read before any argument coercion
            var given = new double[4];
            int count = Math.Max(1, Math.Min(a.Length, 4 - first));
            for (int i = 0; i < count; i++) given[i] = Operations.ToNumber(realm, a[i]);
            if (double.IsNaN(t0)) return JsValue.NaN;
            double t = local ? LocalTime(realm, t0) : t0;
            var parts = new[] { HourFromTime(t), MinFromTime(t), SecFromTime(t), MsFromTime(t) };
            for (int i = 0; i < count; i++) parts[first + i] = given[i];
            double nd = MakeDate(Day(t), MakeTime(parts[0], parts[1], parts[2], parts[3]));
            d.Time = TimeClip(local ? Utc(realm, nd) : nd);
            return new JsValue(d.Time);
        });
        fn.State = (local, first);
    }

    private static void DateSetter(Realm r, JsObject proto, string name, int length, bool local, int first)
    {
        var fn = r.MethodEager(proto, name, length, static (in NativeArgs a) =>
        {
            var (local, first) = ((bool, int))a.Callee.State!;
            var realm = a.Realm;
            var d = ThisDate(a);
            double t0 = d.Time; // read before any argument coercion
            var given = new double[3];
            int count = Math.Max(1, Math.Min(a.Length, 3 - first));
            for (int i = 0; i < count; i++) given[i] = Operations.ToNumber(realm, a[i]);
            double t;
            if (double.IsNaN(t0))
            {
                if (first != 0) return JsValue.NaN;
                t = 0; // setFullYear on an invalid date starts from +0
            }
            else t = local ? LocalTime(realm, t0) : t0;
            var parts = new[] { YearFromTime(t), MonthFromTime(t), DateFromTime(t) };
            for (int i = 0; i < count; i++) parts[first + i] = given[i];
            double nd = MakeDate(MakeDay(parts[0], parts[1], parts[2]), TimeWithinDay(t));
            d.Time = TimeClip(local ? Utc(realm, nd) : nd);
            return new JsValue(d.Time);
        });
        fn.State = (local, first);
    }

    private static double FromComponents(Realm realm, in NativeArgs a)
    {
        double y = Operations.ToNumber(realm, a[0]);
        double m = a.Length > 1 ? Operations.ToNumber(realm, a[1]) : 0;
        double dt = a.Length > 2 ? Operations.ToNumber(realm, a[2]) : 1;
        double h = a.Length > 3 ? Operations.ToNumber(realm, a[3]) : 0;
        double min = a.Length > 4 ? Operations.ToNumber(realm, a[4]) : 0;
        double s = a.Length > 5 ? Operations.ToNumber(realm, a[5]) : 0;
        double ms = a.Length > 6 ? Operations.ToNumber(realm, a[6]) : 0;
        if (!double.IsNaN(y))
        {
            double yi = ToInt(y);
            if (yi >= 0 && yi <= 99) y = 1900 + yi;
        }
        return MakeDate(MakeDay(y, m, dt), MakeTime(h, min, s, ms));
    }

    private static string Pad2(double v) => ((int)v).ToString("00", CultureInfo.InvariantCulture);

    private static string YearString(double y) => y >= 0 ? ((int)y).ToString("0000", CultureInfo.InvariantCulture) : "-" + ((int)-y).ToString("0000", CultureInfo.InvariantCulture);

    public static string IsoString(double t)
    {
        double tod = TimeWithinDay(t);
        CivilFromDays((long)Math.Floor(t / MsPerDay), out long y, out int mo, out int d);
        int msOfDay = (int)tod;
        int h = msOfDay / 3600000, mi = msOfDay / 60000 % 60, sec = msOfDay / 1000 % 60, ms = msOfDay % 1000;
        bool extended = y < 0 || y > 9999;
        int yearChars = extended ? 7 : 4;
        return string.Create(yearChars + 20, (y, mo, d, h, mi, sec, ms, extended), static (span, v) =>
        {
            int p = 0;
            long ay = Math.Abs(v.y);
            if (v.extended) { span[p++] = v.y < 0 ? '-' : '+'; Digits(span, ref p, (int)ay, 6); }
            else Digits(span, ref p, (int)ay, 4);
            span[p++] = '-'; Digits(span, ref p, v.mo, 2);
            span[p++] = '-'; Digits(span, ref p, v.d, 2);
            span[p++] = 'T'; Digits(span, ref p, v.h, 2);
            span[p++] = ':'; Digits(span, ref p, v.mi, 2);
            span[p++] = ':'; Digits(span, ref p, v.sec, 2);
            span[p++] = '.'; Digits(span, ref p, v.ms, 3);
            span[p] = 'Z';
        });
    }

    private static void Digits(Span<char> span, ref int p, int value, int width)
    {
        for (int i = width - 1; i >= 0; i--) { span[p + i] = (char)('0' + value % 10); value /= 10; }
        p += width;
    }

    private static string DatePart(double lt) => $"{DayNames[(int)WeekDay(lt)]} {MonthNames[(int)MonthFromTime(lt)]} {Pad2(DateFromTime(lt))} {YearString(YearFromTime(lt))}";

    private static string TimePart(Realm realm, double t)
    {
        double lt = LocalTime(realm, t);
        double off = (lt - t) / 60000;
        string sign = off >= 0 ? "+" : "-";
        off = Math.Abs(off);
        string tzName = ReferenceEquals(realm.TimeZone, TimeZoneInfo.Utc) ? "Coordinated Universal Time" : realm.TimeZone.StandardName;
        return $"{Pad2(HourFromTime(lt))}:{Pad2(MinFromTime(lt))}:{Pad2(SecFromTime(lt))} GMT{sign}{Pad2(Math.Floor(off / 60))}{Pad2(off % 60)} ({tzName})";
    }

    public static string ToDateString(Realm realm, double t)
    {
        if (double.IsNaN(t)) return "Invalid Date";
        return DatePart(LocalTime(realm, t)) + " " + TimePart(realm, t);
    }

    /// <summary>toLocale{,Date,Time}String: ECMA-402 formatting when Intl is available, else a fixed invariant format.</summary>
    private static string ToLocale(in NativeArgs a, int kind, string fallbackFormat)
    {
        double t = ThisDate(a).Time;
        if (a.Realm.Intl != null) return IntlDateTimeFormat.DateToLocaleString(a.Realm, t, a[0], a[1], kind);
        return LocaleString(a.Realm, t, fallbackFormat);
    }

    private static string LocaleString(Realm realm, double t, string format)
    {
        if (double.IsNaN(t)) return "Invalid Date";
        double lt = LocalTime(realm, t);
        if (lt < -62135596800000 || lt > 253402300799000) return ToDateString(realm, t);
        var dt = DateTimeOffset.FromUnixTimeMilliseconds((long)lt).UtcDateTime;
        return dt.ToString(format, CultureInfo.InvariantCulture);
    }

    private static readonly System.Text.RegularExpressions.Regex IsoRegex = new(
        @"^([+-]\d{6}|\d{4})(?:-(\d{2})(?:-(\d{2}))?)?(?:T(\d{2}):(\d{2})(?::(\d{2})(?:[.,](\d{1,9}))?)?(Z|[+-]\d{2}:\d{2})?)?$",
        RegexOptions.CultureInvariant);

    private static bool Dig(string s, int p, int n, out int v)
    {
        v = 0;
        if (p + n > s.Length) return false;
        for (int i = 0; i < n; i++)
        {
            int c = s[p + i] - '0';
            if ((uint)c > 9) return false;
            v = v * 10 + c;
        }
        return true;
    }

    /// <summary>
    /// The ECMAScript date-time string format, matched by hand (ASCII digits only), equivalent to <see cref="IsoRegex"/> but
    /// without a regex match and a dozen small allocations per parse. Returns false when <paramref name="s"/> is not in
    /// that format; the milliseconds are the exact first three fraction digits.
    /// </summary>
    private static bool TryParseIso(string s, out double y, out double mo, out double d, out double h, out double mi, out double sec, out double ms, out int zone, out double zoneMinutes, out bool hasTime, out bool negativeZeroYear)
    {
        y = 0; mo = 0; d = 1; h = 0; mi = 0; sec = 0; ms = 0; zone = 0; zoneMinutes = 0; hasTime = false; negativeZeroYear = false;
        int p = 0, n = s.Length, v;
        if (n < 4) return false;
        if (s[0] is '+' or '-')
        {
            if (!Dig(s, 1, 6, out v)) return false;
            negativeZeroYear = s[0] == '-' && v == 0;
            y = s[0] == '-' ? -v : v;
            p = 7;
        }
        else
        {
            if (!Dig(s, 0, 4, out v)) return false;
            y = v;
            p = 4;
        }
        if (p < n && s[p] == '-')
        {
            if (!Dig(s, p + 1, 2, out v)) return false;
            mo = v - 1; p += 3;
            if (p < n && s[p] == '-')
            {
                if (!Dig(s, p + 1, 2, out v)) return false;
                d = v; p += 3;
            }
        }
        if (p < n && s[p] == 'T')
        {
            hasTime = true;
            if (!Dig(s, p + 1, 2, out v)) return false;
            h = v; p += 3;
            if (p >= n || s[p] != ':' || !Dig(s, p + 1, 2, out v)) return false;
            mi = v; p += 3;
            if (p < n && s[p] == ':')
            {
                if (!Dig(s, p + 1, 2, out v)) return false;
                sec = v; p += 3;
                if (p < n && (s[p] == '.' || s[p] == ','))
                {
                    int q = p + 1, digits = 0, frac = 0;
                    while (q < n && (uint)(s[q] - '0') <= 9 && digits < 9) { if (digits < 3) frac = frac * 10 + (s[q] - '0'); digits++; q++; }
                    if (digits == 0) return false;
                    for (int i = digits; i < 3; i++) frac *= 10;
                    ms = frac; p = q;
                }
            }
            if (p < n && s[p] == 'Z') { zone = 1; p++; }
            else if (p < n && (s[p] == '+' || s[p] == '-'))
            {
                if (!Dig(s, p + 1, 2, out int oh) || p + 3 >= n || s[p + 3] != ':' || !Dig(s, p + 4, 2, out int om)) return false;
                zone = 2; zoneMinutes = (s[p] == '-' ? -1 : 1) * (oh * 60 + om); p += 6;
            }
        }
        return p == n;
    }

    public static double Parse(Realm realm, string s)
    {
        s = s.Trim();
        if (TryParseIso(s, out var fy, out var fmo, out var fd, out var fh, out var fmi, out var fsec, out var fms, out int fzone, out double fzm, out bool fTime, out bool fNegZero))
        {
            if (fNegZero) return double.NaN;
            if (fmo < 0 || fmo > 11 || fd < 1 || fd > 31 || fh > 24 || fmi > 59 || fsec > 59 || (fh == 24 && (fmi > 0 || fsec > 0 || fms > 0))) return double.NaN;
            double ft = MakeDate(MakeDay(fy, fmo, fd), MakeTime(fh, fmi, fsec, fms));
            if (fzone == 2) ft -= fzm * 60000;
            else if (fzone == 0 && fTime) ft = Utc(realm, ft);   // date-time without offset is local time
            return TimeClip(ft);
        }
        var m = IsoRegex.Match(s);
        if (m.Success)
        {
            double y = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (m.Groups[1].Value == "-000000") return double.NaN;
            double mo = m.Groups[2].Success ? double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) - 1 : 0;
            double d = m.Groups[3].Success ? double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 1;
            double h = m.Groups[4].Success ? double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 0;
            double mi = m.Groups[5].Success ? double.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture) : 0;
            double sec = m.Groups[6].Success ? double.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture) : 0;
            double ms = m.Groups[7].Success ? Math.Floor(double.Parse("0." + m.Groups[7].Value, CultureInfo.InvariantCulture) * 1000) : 0;
            if (mo < 0 || mo > 11 || d < 1 || d > 31 || h > 24 || mi > 59 || sec > 59 || (h == 24 && (mi > 0 || sec > 0 || ms > 0))) return double.NaN;
            double t = MakeDate(MakeDay(y, mo, d), MakeTime(h, mi, sec, ms));
            if (m.Groups[8].Success)
            {
                var z = m.Groups[8].Value;
                if (z != "Z")
                {
                    int sign = z[0] == '-' ? -1 : 1;
                    double oh = double.Parse(z.AsSpan(1, 2), CultureInfo.InvariantCulture), om = double.Parse(z.AsSpan(4, 2), CultureInfo.InvariantCulture);
                    t -= sign * (oh * 3600000 + om * 60000);
                }
            }
            else if (m.Groups[4].Success) t = Utc(realm, t); // date-time without offset is local time
            return TimeClip(t);
        }
        // toString / toUTCString formats and other common forms
        var cleaned = System.Text.RegularExpressions.Regex.Replace(s, @"\s*\([^)]*\)\s*$", "");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"GMT([+-]\d{2})(\d{2})", "$1:$2");
        cleaned = cleaned.Replace("GMT", "+00:00").Replace("UTC", "+00:00");
        if (DateTimeOffset.TryParse(cleaned, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var dto))
        {
            bool hasOffset = System.Text.RegularExpressions.Regex.IsMatch(cleaned, @"[+-]\d{2}:?\d{2}\s*$|Z$");
            double tv = dto.ToUnixTimeMilliseconds();
            if (!hasOffset) tv = Utc(realm, tv);
            return TimeClip(tv);
        }
        var parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 4 && Array.IndexOf(DayNames, parts[0]) >= 0)
        {
            var rest = string.Join(' ', parts, 1, parts.Length - 1);
            if (DateTimeOffset.TryParse(rest, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out dto))
                return TimeClip(dto.ToUnixTimeMilliseconds());
        }
        return double.NaN;
    }
}
