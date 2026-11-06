using System;
using System.Numerics;

namespace Jaunt.Runtime;

/// <summary>Temporal units, largest first (ordering matters: a smaller value is a larger unit).</summary>
internal enum TUnit
{
    Year = 0, Month = 1, Week = 2, Day = 3, Hour = 4, Minute = 5, Second = 6, Millisecond = 7, Microsecond = 8, Nanosecond = 9,
    Auto = 100, Unset = 101,
}

internal enum RoundingMode { Ceil, Floor, Expand, Trunc, HalfCeil, HalfFloor, HalfExpand, HalfTrunc, HalfEven }

internal enum Overflow { Constrain, Reject }

internal enum Disambiguation { Compatible, Earlier, Later, Reject }

internal enum OffsetOption { Prefer, Use, Ignore, Reject }

internal readonly record struct IsoDate(int Year, int Month, int Day);

internal readonly record struct TimeRec(int Hour, int Minute, int Second, int Millisecond, int Microsecond, int Nanosecond)
{
    public static readonly TimeRec Midnight = default;
    public long TotalNs => ((((Hour * 60L + Minute) * 60 + Second) * 1000 + Millisecond) * 1000 + Microsecond) * 1000 + Nanosecond;

    public static TimeRec FromNs(long ns)
    {
        int nano = (int)(ns % 1000); ns /= 1000;
        int micro = (int)(ns % 1000); ns /= 1000;
        int milli = (int)(ns % 1000); ns /= 1000;
        int sec = (int)(ns % 60); ns /= 60;
        int min = (int)(ns % 60); ns /= 60;
        return new TimeRec((int)ns, min, sec, milli, micro, nano);
    }
}

internal readonly record struct IsoDateTime(IsoDate Date, TimeRec Time);

/// <summary>Calendar-independent ISO 8601 date/time arithmetic and the shared numeric helpers of Temporal.</summary>
internal static class TemporalMath
{
    public const long NsPerDay = 86_400_000_000_000L;
    public const long NsPerHour = 3_600_000_000_000L;
    public const long NsPerMinute = 60_000_000_000L;
    public const long NsPerSecond = 1_000_000_000L;
    /// <summary>nsMaxInstant = 10^8 days.</summary>
    public static readonly Int128 NsMaxInstant = (Int128)8_640_000_000_000_000_000L * 1000;
    public static readonly Int128 NsMinInstant = -NsMaxInstant;
    /// <summary>Upper bound of a normalized time duration: 2^53 seconds (exclusive).</summary>
    public static readonly Int128 MaxTimeDuration = (Int128)9_007_199_254_740_992L * NsPerSecond - 1;

    public static long UnitNs(TUnit u) => u switch
    {
        TUnit.Day => NsPerDay,
        TUnit.Hour => NsPerHour,
        TUnit.Minute => NsPerMinute,
        TUnit.Second => NsPerSecond,
        TUnit.Millisecond => 1_000_000,
        TUnit.Microsecond => 1_000,
        TUnit.Nanosecond => 1,
        _ => throw new InvalidOperationException("not a time unit"),
    };

    public static bool IsDateUnit(TUnit u) => u <= TUnit.Day;
    public static bool IsCalendarUnit(TUnit u) => u <= TUnit.Week;
    public static TUnit Larger(TUnit a, TUnit b) => a <= b ? a : b;

    // ------------------------------------------------------------------ calendar math (proleptic Gregorian)

    public static bool IsLeapYear(long y) => (y % 4 == 0 && y % 100 != 0) || y % 400 == 0;
    public static int DaysInYear(long y) => IsLeapYear(y) ? 366 : 365;

    public static int DaysInMonth(long y, int m) => m switch
    {
        2 => IsLeapYear(y) ? 29 : 28,
        4 or 6 or 9 or 11 => 30,
        _ => 31,
    };

    /// <summary>Days since 1970-01-01 of a (possibly unbalanced in day) ISO date.</summary>
    public static long EpochDays(long y, long m, long d)
    {
        // balance the month first
        y += FloorDiv(m - 1, 12);
        m = FloorMod(m - 1, 12) + 1;
        // days_from_civil
        y -= m <= 2 ? 1 : 0;
        long era = FloorDiv(y, 400);
        long yoe = y - era * 400;
        long mp = (m + 9) % 12;
        long doy = (153 * mp + 2) / 5;
        long doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        return era * 146097 + doe - 719468 + (d - 1);
    }

    public static long EpochDays(IsoDate d) => EpochDays(d.Year, d.Month, d.Day);

    public static IsoDate FromEpochDays(long z)
    {
        z += 719468;
        long era = FloorDiv(z, 146097);
        long doe = z - era * 146097;
        long yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
        long y = yoe + era * 400;
        long doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
        long mp = (5 * doy + 2) / 153;
        long d = doy - (153 * mp + 2) / 5 + 1;
        long m = mp < 10 ? mp + 3 : mp - 9;
        if (m <= 2) y++;
        return new IsoDate((int)y, (int)m, (int)d);
    }

    public static long FloorDiv(long a, long b) { long q = a / b; if ((a % b != 0) && ((a < 0) != (b < 0))) q--; return q; }
    public static long FloorMod(long a, long b) { long r = a % b; if (r != 0 && ((r < 0) != (b < 0))) r += b; return r; }
    public static Int128 FloorDiv(Int128 a, Int128 b) { var q = a / b; if ((a % b != 0) && ((a < 0) != (b < 0))) q--; return q; }
    public static Int128 FloorMod(Int128 a, Int128 b) { var r = a % b; if (r != 0 && ((r < 0) != (b < 0))) r += b; return r; }

    public static bool IsValidISODate(long y, long m, long d) => m >= 1 && m <= 12 && d >= 1 && d <= DaysInMonth(y, (int)m);

    /// <summary>BalanceISODate: day may be any integer.</summary>
    public static IsoDate BalanceISODate(long y, long m, long d) => FromEpochDays(EpochDays(y, m, 1) + d - 1);

    public static (long Year, int Month) BalanceISOYearMonth(long y, long m) => (y + FloorDiv(m - 1, 12), (int)(FloorMod(m - 1, 12) + 1));

    public static IsoDate AddDays(IsoDate d, long days) => FromEpochDays(EpochDays(d) + days);

    public static int DayOfWeek(IsoDate d) => (int)(FloorMod(EpochDays(d) + 3, 7) + 1);   // 1970-01-01 was a Thursday (4)

    public static int DayOfYear(IsoDate d) => (int)(EpochDays(d) - EpochDays(d.Year, 1, 1) + 1);

    /// <summary>ISO 8601 week number and week-year.</summary>
    public static (int Week, int Year) WeekOfYear(IsoDate d)
    {
        int doy = DayOfYear(d);
        int dow = DayOfWeek(d);
        int week = (doy - dow + 10) / 7;
        int year = d.Year;
        if (week < 1)
        {
            year--;
            week = WeeksInYear(year);
        }
        else if (week > WeeksInYear(year))
        {
            year++;
            week = 1;
        }
        return (week, year);
    }

    private static int WeeksInYear(int y)
    {
        // a year has 53 weeks when Jan 1 is a Thursday, or a Wednesday in a leap year
        int jan1 = DayOfWeek(new IsoDate(y, 1, 1));
        return jan1 == 4 || (jan1 == 3 && IsLeapYear(y)) ? 53 : 52;
    }

    public static int CompareISODate(IsoDate a, IsoDate b)
    {
        if (a.Year != b.Year) return a.Year < b.Year ? -1 : 1;
        if (a.Month != b.Month) return a.Month < b.Month ? -1 : 1;
        if (a.Day != b.Day) return a.Day < b.Day ? -1 : 1;
        return 0;
    }

    public static int CompareTime(TimeRec a, TimeRec b) => a.TotalNs.CompareTo(b.TotalNs) switch { < 0 => -1, > 0 => 1, _ => 0 };

    public static int CompareISODateTime(IsoDateTime a, IsoDateTime b)
    {
        int c = CompareISODate(a.Date, b.Date);
        return c != 0 ? c : CompareTime(a.Time, b.Time);
    }

    // ------------------------------------------------------------------ limits

    /// <summary>ISODateWithinLimits: noon of the date is within the representable range (±(10^8 + 1) days).</summary>
    public static bool ISODateWithinLimits(IsoDate d) => ISODateTimeWithinLimits(new IsoDateTime(d, new TimeRec(12, 0, 0, 0, 0, 0)));

    public static bool ISODateWithinLimits(long y, long m, long d)
    {
        if (y < -300000 || y > 300000) return false;
        return ISODateTimeWithinLimits(new IsoDateTime(new IsoDate((int)y, (int)m, (int)d), new TimeRec(12, 0, 0, 0, 0, 0)));
    }

    public static bool ISODateTimeWithinLimits(IsoDateTime dt)
    {
        if (Math.Abs((long)dt.Date.Year) > 300000) return false;
        var ns = GetUTCEpochNanoseconds(dt);
        return ns > NsMinInstant - NsPerDay && ns < NsMaxInstant + NsPerDay;
    }

    public static bool ISOYearMonthWithinLimits(long y, int m)
    {
        if (y < -271821 || y > 275760) return false;
        if (y == -271821 && m < 4) return false;
        if (y == 275760 && m > 9) return false;
        return true;
    }

    public static bool IsValidEpochNanoseconds(Int128 ns) => ns >= NsMinInstant && ns <= NsMaxInstant;

    // ------------------------------------------------------------------ epoch conversions

    public static Int128 GetUTCEpochNanoseconds(IsoDateTime dt) => (Int128)EpochDays(dt.Date) * NsPerDay + dt.Time.TotalNs;

    public static IsoDateTime GetISOPartsFromEpoch(Int128 ns)
    {
        long days = (long)FloorDiv(ns, NsPerDay);
        long rem = (long)(ns - (Int128)days * NsPerDay);
        return new IsoDateTime(FromEpochDays(days), TimeRec.FromNs(rem));
    }

    // ------------------------------------------------------------------ time balancing

    /// <summary>BalanceTime on an arbitrary nanosecond count from midnight: (whole days, time of day).</summary>
    public static (long Days, TimeRec Time) BalanceTime(Int128 ns)
    {
        var days = FloorDiv(ns, NsPerDay);
        long rem = (long)(ns - days * NsPerDay);
        return ((long)days, TimeRec.FromNs(rem));
    }

    public static (long Days, TimeRec Time) BalanceTime(double h, double min, double s, double ms, double us, double ns)
    {
        Int128 total = (Int128)h * NsPerHour + (Int128)min * NsPerMinute + (Int128)s * NsPerSecond + (Int128)ms * 1_000_000 + (Int128)us * 1000 + (Int128)ns;
        return BalanceTime(total);
    }

    public static (long Days, TimeRec Time) AddTime(TimeRec t, Int128 ns) => BalanceTime(t.TotalNs + ns);

    public static bool IsValidTime(double h, double m, double s, double ms, double us, double ns) =>
        h >= 0 && h <= 23 && m >= 0 && m <= 59 && s >= 0 && s <= 59 && ms >= 0 && ms <= 999 && us >= 0 && us <= 999 && ns >= 0 && ns <= 999;

    public static TimeRec ConstrainTime(double h, double m, double s, double ms, double us, double ns) => new(
        (int)Math.Clamp(h, 0, 23), (int)Math.Clamp(m, 0, 59), (int)Math.Clamp(s, 0, 59),
        (int)Math.Clamp(ms, 0, 999), (int)Math.Clamp(us, 0, 999), (int)Math.Clamp(ns, 0, 999));

    /// <summary>RoundTime: rounds a time of day; returns the day carry.</summary>
    public static (long Days, TimeRec Time) RoundTime(TimeRec t, long increment, TUnit unit, RoundingMode mode)
    {
        Int128 quantum = (Int128)UnitNs(unit) * increment;
        var rounded = RoundToIncrement(t.TotalNs, quantum, mode);
        return BalanceTime(rounded);
    }

    // ------------------------------------------------------------------ rounding

    /// <summary>RoundNumberToIncrement on an exact integer (increment &gt; 0).</summary>
    public static Int128 RoundToIncrement(Int128 x, Int128 increment, RoundingMode mode)
    {
        var q = FloorDiv(x, increment);
        var r = x - q * increment;   // 0 <= r < increment
        if (r == 0) return x;
        bool neg = x < 0;
        // r1 = q (floor), r2 = q + 1 (ceil)
        var um = UnsignedMode(mode, neg);
        Int128 chosen;
        switch (um)
        {
            case UnsignedRounding.Zero: chosen = neg ? q + 1 : q; break;     // toward zero
            case UnsignedRounding.Infinity: chosen = neg ? q : q + 1; break;  // away from zero
            default:
            {
                var twice = r * 2;
                int cmp = twice < increment ? -1 : twice > increment ? 1 : 0;
                // distance relative to floor: cmp < 0 → closer to floor
                if (cmp < 0) chosen = q;
                else if (cmp > 0) chosen = q + 1;
                else
                {
                    chosen = um switch
                    {
                        UnsignedRounding.HalfZero => neg ? q + 1 : q,
                        UnsignedRounding.HalfInfinity => neg ? q : q + 1,
                        _ => (q % 2 == 0) ? q : q + 1,   // half-even
                    };
                }
                break;
            }
        }
        return chosen * increment;
    }

    internal enum UnsignedRounding { Zero, Infinity, HalfZero, HalfInfinity, HalfEven }

    /// <summary>GetUnsignedRoundingMode (13.x): the rounding direction in terms of magnitude.</summary>
    public static UnsignedRounding UnsignedMode(RoundingMode mode, bool negative) => mode switch
    {
        RoundingMode.Ceil => negative ? UnsignedRounding.Zero : UnsignedRounding.Infinity,
        RoundingMode.Floor => negative ? UnsignedRounding.Infinity : UnsignedRounding.Zero,
        RoundingMode.Expand => UnsignedRounding.Infinity,
        RoundingMode.Trunc => UnsignedRounding.Zero,
        RoundingMode.HalfCeil => negative ? UnsignedRounding.HalfZero : UnsignedRounding.HalfInfinity,
        RoundingMode.HalfFloor => negative ? UnsignedRounding.HalfInfinity : UnsignedRounding.HalfZero,
        RoundingMode.HalfExpand => UnsignedRounding.HalfInfinity,
        RoundingMode.HalfTrunc => UnsignedRounding.HalfZero,
        _ => UnsignedRounding.HalfEven,
    };

    /// <summary>
    /// ApplyUnsignedRoundingMode on an exact non-negative rational x = num/den lying between r1 and r2 = r1 + 1
    /// (in increments); returns r1 or r2.
    /// </summary>
    public static BigInteger ApplyUnsignedRounding(BigInteger num, BigInteger den, BigInteger r1, BigInteger r2, UnsignedRounding um)
    {
        // x == r1 exactly?
        if (num == r1 * den) return r1;
        if (um == UnsignedRounding.Zero) return r1;
        if (um == UnsignedRounding.Infinity) return r2;
        // compare x - r1 with r2 - x  ⇔ 2x vs r1 + r2
        var lhs = 2 * num;
        var rhs = (r1 + r2) * den;
        if (lhs < rhs) return r1;
        if (lhs > rhs) return r2;
        if (um == UnsignedRounding.HalfZero) return r1;
        if (um == UnsignedRounding.HalfInfinity) return r2;
        return r1.IsEven ? r1 : r2;
    }

    public static RoundingMode Negate(RoundingMode m) => m switch
    {
        RoundingMode.Ceil => RoundingMode.Floor,
        RoundingMode.Floor => RoundingMode.Ceil,
        RoundingMode.HalfCeil => RoundingMode.HalfFloor,
        RoundingMode.HalfFloor => RoundingMode.HalfCeil,
        _ => m,
    };

    /// <summary>Correctly rounded conversion of the rational num/den to a double.</summary>
    public static double RationalToDouble(BigInteger num, BigInteger den)
    {
        if (den.Sign < 0) { num = -num; den = -den; }
        if (num.IsZero) return 0;
        bool neg = num.Sign < 0;
        if (neg) num = -num;
        // scale so the integer quotient has at least 64 significant bits, then round via the sticky remainder
        long shift = 64 - ((long)num.GetBitLength() - (long)den.GetBitLength());
        BigInteger q, rem;
        if (shift > 0) q = BigInteger.DivRem(num << (int)shift, den, out rem);
        else q = BigInteger.DivRem(num, den << (int)(-shift), out rem);
        if (!rem.IsZero) q = (q << 1) | 1; else q <<= 1;   // sticky bit
        double d = BigIntOps.ToDouble(q);                   // correctly rounded (ties to even) with sticky bit
        d = Math.ScaleB(d, (int)(-shift - 1));
        return neg ? -d : d;
    }

    public static double ToDouble(Int128 x) => BigIntOps.ToDouble((BigInteger)x);
}
