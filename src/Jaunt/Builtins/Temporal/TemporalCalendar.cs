using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Jaunt.Runtime;

/// <summary>Calendar Date Record (ECMA-402 Temporal integration, Table 5).</summary>
internal readonly record struct CalendarDate(
    string? Era, int? EraYear, int Year, int Month, string MonthCode, int Day, int DayOfYear,
    int DaysInMonth, int DaysInYear, int MonthsInYear, bool InLeapYear);

/// <summary>
/// Non-ISO calendars for Temporal (Intl era and month code). Gregorian-structured calendars (gregory, japanese,
/// buddhist, roc) are ISO with a year offset; the others are mapped through ICU's calendars once per year into a
/// cached table of month starts, so date conversions are table lookups.
/// </summary>
internal static unsafe class TemporalCalendars
{
    public static readonly string[] Supported =
    {
        "buddhist", "chinese", "coptic", "dangi", "ethioaa", "ethiopic", "gregory", "hebrew", "indian",
        "islamic-civil", "islamic-tbla", "islamic-umalqura", "iso8601", "japanese", "persian", "roc",
    };

    /// <summary>Canonical calendar id, or null when unsupported. Input is compared ASCII-case-insensitively.</summary>
    public static string? Canonicalize(string id)
    {
        foreach (char c in id) if (c > 0x7F) return null;
        var lower = id.ToLowerInvariant();
        if (lower == "islamicc") return "islamic-civil";
        if (lower == "ethiopic-amete-alem") return "ethioaa";
        if (lower != "iso8601" && !Icu.Available) return null;
        return Array.IndexOf(Supported, lower) >= 0 ? lower : null;
    }

    public static bool IsGregorianLike(string cal) => cal is "gregory" or "japanese" or "buddhist" or "roc";
    private static bool IsIslamic(string cal) => cal.StartsWith("islamic", StringComparison.Ordinal);

    private static int EpochIsoYear(string cal) => cal switch
    {
        "buddhist" => -543, "coptic" => 283, "ethioaa" => -5493, "ethiopic" => 7, "hebrew" => -3761,
        "indian" => 78, "persian" => 621, "roc" => 1911, _ when IsIslamic(cal) => 621, _ => 0,
    };

    // ------------------------------------------------------------------ eras

    public static bool SupportsEra(string cal) => cal is not ("chinese" or "dangi" or "iso8601");

    private readonly record struct EraRow(string Calendar, string Era, string? Alias, int Kind, int Offset);   // kind 0 epoch, 1 negative, 2 offset

    private static readonly EraRow[] s_eras =
    {
        new("buddhist", "be", null, 0, 0), new("coptic", "am", null, 0, 0), new("ethioaa", "aa", null, 0, 0),
        new("ethiopic", "am", null, 0, 0), new("ethiopic", "aa", null, 2, -5499),
        new("gregory", "ce", "ad", 0, 0), new("gregory", "bce", "bc", 1, 0),
        new("hebrew", "am", null, 0, 0), new("indian", "shaka", null, 0, 0),
        new("islamic-civil", "ah", null, 0, 0), new("islamic-civil", "bh", null, 1, 0),
        new("islamic-tbla", "ah", null, 0, 0), new("islamic-tbla", "bh", null, 1, 0),
        new("islamic-umalqura", "ah", null, 0, 0), new("islamic-umalqura", "bh", null, 1, 0),
        new("japanese", "reiwa", null, 2, 2019), new("japanese", "heisei", null, 2, 1989), new("japanese", "showa", null, 2, 1926),
        new("japanese", "taisho", null, 2, 1912), new("japanese", "meiji", null, 2, 1868),
        new("japanese", "ce", "ad", 0, 0), new("japanese", "bce", "bc", 1, 0),
        new("persian", "ap", null, 0, 0), new("roc", "roc", null, 0, 0), new("roc", "broc", null, 1, 0),
    };

    /// <summary>CanonicalizeEraInCalendar (null when not an era of the calendar).</summary>
    public static string? CanonicalizeEra(string cal, string era)
    {
        foreach (var r in s_eras)
            if (r.Calendar == cal && (r.Era == era || r.Alias == era)) return r.Era;
        return null;
    }

    /// <summary>CalendarDateArithmeticYearForEraYear.</summary>
    public static double ArithmeticYearForEraYear(string cal, string era, double eraYear)
    {
        foreach (var r in s_eras)
        {
            if (r.Calendar != cal || r.Era != era) continue;
            return r.Kind switch { 0 => eraYear, 1 => 1 - eraYear, _ => r.Offset + eraYear - 1 };
        }
        return eraYear;
    }

    private static readonly (IsoDate Start, string Era, int Offset)[] s_japanese =
    {
        (new IsoDate(2019, 5, 1), "reiwa", 2018), (new IsoDate(1989, 1, 8), "heisei", 1988), (new IsoDate(1926, 12, 25), "showa", 1925),
        (new IsoDate(1912, 7, 30), "taisho", 1911), (new IsoDate(1873, 1, 1), "meiji", 1867),
    };

    private static (string? Era, int? EraYear) EraOf(string cal, int year, IsoDate iso)
    {
        switch (cal)
        {
            case "gregory": return year >= 1 ? ("ce", year) : ("bce", 1 - year);
            case "japanese":
                foreach (var (start, era, offset) in s_japanese)
                    if (TemporalMath.CompareISODate(iso, start) >= 0) return (era, year - offset);
                return year >= 1 ? ("ce", year) : ("bce", 1 - year);
            case "roc": return year >= 1 ? ("roc", year) : ("broc", 1 - year);
            case "ethiopic": return year >= 1 ? ("am", year) : ("aa", year + 5500);
            case "chinese" or "dangi" or "iso8601": return (null, null);
            default:
                if (IsIslamic(cal)) return year >= 1 ? ("ah", year) : ("bh", 1 - year);
                return (s_eras.First(cal).Era, year);
        }
    }

    private static EraRow First(this EraRow[] rows, string cal)
    {
        foreach (var r in rows) if (r.Calendar == cal) return r;
        throw new InvalidOperationException(cal);
    }

    // ------------------------------------------------------------------ month codes

    public static bool IsValidMonthCode(string cal, string code)
    {
        // syntax already checked: M\d\d(L)?
        int n = (code[1] - '0') * 10 + (code[2] - '0');
        bool leap = code.Length == 4;
        if (!leap && n >= 1 && n <= 12) return true;
        return cal switch
        {
            "chinese" or "dangi" => leap && n >= 1 && n <= 12,
            "coptic" or "ethiopic" or "ethioaa" => !leap && n == 13,
            "hebrew" => leap && n == 5,
            _ => false,
        };
    }

    public static string CreateMonthCode(int n, bool leap) => "M" + n.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + (leap ? "L" : "");

    // ------------------------------------------------------------------ year tables (ICU-backed calendars)

    private sealed class YearInfo
    {
        public long[] MonthStart = null!;     // epoch day of day 1 of each ordinal month (index 0 = month 1)
        public long NextYearStart;
        public string[] MonthCodes = null!;
        public int MonthsInYear => MonthStart.Length;
        public int DaysInMonth(int m) => (int)((m < MonthStart.Length ? MonthStart[m] : NextYearStart) - MonthStart[m - 1]);
    }

    private static readonly ConcurrentDictionary<(string, int), YearInfo> s_years = new();
    private static readonly ConcurrentDictionary<string, object> s_locks = new();

    private const int UCAL_MONTH = 2, UCAL_DATE = 5, UCAL_EXTENDED_YEAR = 19, UCAL_IS_LEAP_MONTH = 22;
    private const int UCAL_DAY_OF_YEAR = 6;

    private static nint s_open, s_setMillis, s_getMillis, s_get, s_getLimit;

    [ThreadStatic] private static Dictionary<string, nint>? t_cals;

    private static nint Cal(string cal)
    {
        var d = t_cals ??= new Dictionary<string, nint>();
        if (d.TryGetValue(cal, out var h)) return h;
        var open = (delegate* unmanaged<char*, int, byte*, int, int*, nint>)Icu.P(ref s_open, "ucal_open");
        var loc = Icu.Utf8Z("und@calendar=" + (cal == "ethioaa" ? "ethiopic-amete-alem" : cal));
        int err = 0;
        fixed (char* z = "UTC")
        fixed (byte* l = loc)
            h = open(z, 3, l, 0, &err);
        if (Icu.Failure(err) || h == 0) throw new InvalidOperationException("ICU calendar unavailable: " + cal);
        d[cal] = h;   // kept for the thread's lifetime
        return h;
    }

    private static void SetMillis(nint c, double ms) { int e = 0; ((delegate* unmanaged<nint, double, int*, void>)Icu.P(ref s_setMillis, "ucal_setMillis"))(c, ms, &e); }
    private static double GetMillis(nint c) { int e = 0; return ((delegate* unmanaged<nint, int*, double>)Icu.P(ref s_getMillis, "ucal_getMillis"))(c, &e); }
    private static int Get(nint c, int f) { int e = 0; return ((delegate* unmanaged<nint, int, int*, int>)Icu.P(ref s_get, "ucal_get"))(c, f, &e); }
    private static int GetLimit(nint c, int f, int type) { int e = 0; return ((delegate* unmanaged<nint, int, int, int*, int>)Icu.P(ref s_getLimit, "ucal_getLimit"))(c, f, type, &e); }

    private static double NoonMs(long epochDay) => epochDay * 86_400_000.0 + 43_200_000.0;
    private static long DayOf(double ms) => (long)Math.Floor(ms / 86_400_000.0);

    /// <summary>Month code of the ICU calendar's current date.</summary>
    private static string IcuMonthCode(string cal, nint c)
    {
        int m = Get(c, UCAL_MONTH);
        if (cal == "hebrew") return m < 5 ? CreateMonthCode(m + 1, false) : m == 5 ? "M05L" : CreateMonthCode(m, false);
        if (cal is "chinese" or "dangi") return CreateMonthCode(m + 1, Get(c, UCAL_IS_LEAP_MONTH) != 0);
        return CreateMonthCode(m + 1, false);
    }

    private static double MeanYear(string cal) => cal switch
    {
        _ when IsIslamic(cal) => 354.36667,
        "hebrew" => 365.2468,
        "coptic" or "ethiopic" or "ethioaa" => 365.25,
        _ => 365.2422,
    };

    /// <summary>The arithmetic year containing 2000-06-01 in each ICU-backed calendar.</summary>
    private static int AnchorYear(string cal) => cal switch
    {
        "chinese" or "dangi" => 2000,
        "coptic" => 1716,
        "ethiopic" => 1992,
        "ethioaa" => 7492,
        "hebrew" => 5760,
        "indian" => 1922,
        "persian" => 1379,
        _ => 1421,   // islamic
    };

    private static readonly long s_anchorDay = TemporalMath.EpochDays(2000, 6, 1);
    private static readonly ConcurrentDictionary<string, int> s_offsets = new();

    /// <summary>
    /// ICU's extended-year numbering differs between calendars and ICU versions (e.g. cycle-based Chinese years,
    /// Amete Mihret years for ethioaa); calibrate it once against a known date: arithmetic = extended - offset.
    /// </summary>
    private static int Offset(string cal, nint c) => s_offsets.GetOrAdd(cal, _ =>
    {
        SetMillis(c, NoonMs(s_anchorDay));
        return Get(c, UCAL_EXTENDED_YEAR) - AnchorYear(cal);
    });

    /// <summary>Moves the ICU calendar to a day in the given arithmetic year and returns ICU's extended year for it.</summary>
    private static int IcuExtendedYear(string cal, nint c, int year)
    {
        int offset = Offset(cal, c);
        int ext = year + offset;
        long ed = s_anchorDay + (long)((year - AnchorYear(cal)) * MeanYear(cal));
        for (int i = 0; i < 64; i++)
        {
            SetMillis(c, NoonMs(ed));
            int got = Get(c, UCAL_EXTENDED_YEAR);
            if (got == ext) break;
            int diff = ext - got;
            // full steps while far away, then half-year steps so a mid-year target cannot be overshot repeatedly
            ed += Math.Abs(diff) > 1 ? (long)(diff * MeanYear(cal)) : diff * (long)(MeanYear(cal) / 2);
        }
        return ext;
    }

    // ------------------------------------------------------------------ Hebrew: exact arithmetic
    // The Hebrew calendar is fully arithmetic (molad of Tishri plus the four postponement rules), so it is computed
    // directly instead of through ICU, whose HebrewCalendar is wrong for years <= 0 (negative-division rounding).

    private const long HebrewEpochDay = -1373427 - 719163;   // 1 Tishri AM 1 (R.D. -1373427) as days since 1970-01-01

    private static long FloorDiv(long a, long b) => a / b - ((a % b != 0) && ((a < 0) != (b < 0)) ? 1 : 0);

    private static long HebrewElapsedDays(long year)
    {
        long months = FloorDiv(235 * year - 234, 19);
        long parts = 12084 + 13753 * months;
        long days = 29 * months + FloorDiv(parts, 25920);
        return ((3 * (days + 1)) % 7 + 7) % 7 < 3 ? days + 1 : days;
    }

    private static long HebrewNewYear(long year)
    {
        long prev = HebrewElapsedDays(year - 1), cur = HebrewElapsedDays(year), next = HebrewElapsedDays(year + 1);
        long correction = next - cur == 356 ? 2 : cur - prev == 382 ? 1 : 0;
        return HebrewEpochDay + cur + correction;
    }

    private static bool HebrewLeap(long year) => ((7 * year + 1) % 19 + 19) % 19 < 7;

    private static YearInfo HebrewYear(int year)
    {
        long start = HebrewNewYear(year), next = HebrewNewYear(year + 1);
        int length = (int)(next - start);
        bool leap = HebrewLeap(year);
        int cheshvan = length % 10 == 5 ? 30 : 29, kislev = length % 10 == 3 ? 29 : 30;
        int[] lengths = leap
            ? [30, cheshvan, kislev, 29, 30, 30, 29, 30, 29, 30, 29, 30, 29]
            : [30, cheshvan, kislev, 29, 30, 29, 30, 29, 30, 29, 30, 29];
        var starts = new long[lengths.Length];
        var codes = new string[lengths.Length];
        long day = start;
        for (int i = 0; i < lengths.Length; i++)
        {
            starts[i] = day;
            day += lengths[i];
            codes[i] = !leap || i < 5 ? CreateMonthCode(i + 1, false) : i == 5 ? "M05L" : CreateMonthCode(i, false);
        }
        return new YearInfo { MonthStart = starts, NextYearStart = next, MonthCodes = codes };
    }

    private static int HebrewYearOfEpochDay(long epochDay)
    {
        long year = (long)Math.Floor((epochDay - HebrewEpochDay) / 365.2468) + 1;
        while (HebrewNewYear(year + 1) <= epochDay) year++;
        while (HebrewNewYear(year) > epochDay) year--;
        return checked((int)year);
    }

    private static YearInfo GetYear(string cal, int year)
    {
        if (s_years.TryGetValue((cal, year), out var info)) return info;
        if (cal == "hebrew")
        {
            info = HebrewYear(year);
            if (s_years.Count > 20_000) s_years.Clear();
            s_years[(cal, year)] = info;
            return info;
        }
        lock (s_locks.GetOrAdd(cal, _ => new object()))
        {
            if (s_years.TryGetValue((cal, year), out info)) return info;
            var c = Cal(cal);
            int ext = IcuExtendedYear(cal, c, year);   // leaves the calendar on a day of that year
            long probe = DayOf(GetMillis(c));
            // find the first day of the year by stepping back month by month (DAY_OF_YEAR is not reliable for every
            // calendar in every ICU version)
            long day = probe - (Get(c, UCAL_DATE) - 1);
            for (int guard = 0; guard < 15; guard++)
            {
                SetMillis(c, NoonMs(day - 1));
                if (Get(c, UCAL_EXTENDED_YEAR) != ext) break;
                day = day - 1 - (Get(c, UCAL_DATE) - 1);
            }
            var starts = new List<long>(13);
            var codes = new List<string>(13);
            long nextStart;
            while (true)
            {
                SetMillis(c, NoonMs(day));
                if (Get(c, UCAL_EXTENDED_YEAR) != ext || starts.Count > 14) { nextStart = day; break; }
                starts.Add(day);
                codes.Add(IcuMonthCode(cal, c));
                day += GetLimit(c, UCAL_DATE, 5);   // UCAL_ACTUAL_MAXIMUM
            }
            if (starts.Count < 12 && cal is "chinese" or "dangi")
            {
                // far outside the astronomically meaningful range ICU's lunisolar data degenerates: approximate the
                // year as 12 mean lunar months from the estimated new year (the spec permits approximation here)
                long start = starts.Count > 0 ? starts[0] : probe;
                starts.Clear();
                codes.Clear();
                for (int m = 0; m < 12; m++)
                {
                    starts.Add(start + (long)Math.Round(m * 29.530589));
                    codes.Add(CreateMonthCode(m + 1, false));
                }
                nextStart = start + (long)Math.Round(12 * 29.530589);
            }
            info = new YearInfo { MonthStart = starts.ToArray(), NextYearStart = nextStart, MonthCodes = codes.ToArray() };
            if (s_years.Count > 20_000) s_years.Clear();
            s_years[(cal, year)] = info;
            return info;
        }
    }

    private static int YearOfEpochDay(string cal, long epochDay, IsoDate iso)
    {
        _ = iso;
        if (cal == "hebrew") return HebrewYearOfEpochDay(epochDay);
        lock (s_locks.GetOrAdd(cal, _ => new object()))
        {
            var c = Cal(cal);
            int offset = Offset(cal, c);
            SetMillis(c, NoonMs(epochDay));
            return Get(c, UCAL_EXTENDED_YEAR) - offset;
        }
    }

    // ------------------------------------------------------------------ public operations

    /// <summary>CalendarISOToDate for a non-ISO calendar.</summary>
    public static CalendarDate ISOToDate(string cal, IsoDate iso)
    {
        if (IsGregorianLike(cal))
        {
            int y = iso.Year - EpochIsoYear(cal);
            var (era, eraYear) = EraOf(cal, y, iso);
            int diy = TemporalMath.DaysInYear(iso.Year);
            return new CalendarDate(era, eraYear, y, iso.Month, CreateMonthCode(iso.Month, false), iso.Day, TemporalMath.DayOfYear(iso),
                TemporalMath.DaysInMonth(iso.Year, iso.Month), diy, 12, diy == 366);
        }
        long ed = TemporalMath.EpochDays(iso);
        int year = YearOfEpochDay(cal, ed, iso);
        var info = GetYear(cal, year);
        int m = info.MonthStart.Length;
        while (m > 1 && info.MonthStart[m - 1] > ed) m--;
        int day = (int)(ed - info.MonthStart[m - 1]) + 1;
        int daysInYear = (int)(info.NextYearStart - info.MonthStart[0]);
        var (e, ey) = EraOf(cal, year, iso);
        return new CalendarDate(e, ey, year, m, info.MonthCodes[m - 1], day, (int)(ed - info.MonthStart[0]) + 1,
            info.DaysInMonth(m), daysInYear, info.MonthsInYear, InLeapYear(cal, info.MonthsInYear, daysInYear));
    }

    private static bool InLeapYear(string cal, int monthsInYear, int daysInYear) => cal switch
    {
        "hebrew" or "chinese" or "dangi" => monthsInYear == 13,
        _ when IsIslamic(cal) => daysInYear == 355,
        _ => daysInYear == 366,
    };

    public static int MonthsInYear(string cal, int year) => IsGregorianLike(cal) ? 12 : GetYear(cal, year).MonthsInYear;

    public static int DaysInMonth(string cal, int year, int month)
    {
        if (IsGregorianLike(cal)) return TemporalMath.DaysInMonth(year + EpochIsoYear(cal), month);
        return GetYear(cal, year).DaysInMonth(month);
    }

    /// <summary>CalendarIntegersToISO (caller guarantees a valid date).</summary>
    public static IsoDate IntegersToISO(string cal, int year, int month, int day)
    {
        if (IsGregorianLike(cal)) return new IsoDate(year + EpochIsoYear(cal), month, day);
        return TemporalMath.FromEpochDays(GetYear(cal, year).MonthStart[month - 1] + day - 1);
    }

    public static bool YearContainsMonthCode(string cal, int year, string code)
    {
        if (code.Length == 3) return true;
        if (IsGregorianLike(cal)) return false;
        return Array.IndexOf(GetYear(cal, year).MonthCodes, code) >= 0;
    }

    /// <summary>ConstrainMonthCode; null = reject failure.</summary>
    public static string? ConstrainMonthCode(string cal, int year, string code, Overflow overflow)
    {
        if (YearContainsMonthCode(cal, year, code)) return code;
        if (overflow == Overflow.Reject) return null;
        if (cal == "hebrew") return "M06";
        return code.Substring(0, 3);
    }

    public static int MonthCodeToOrdinal(string cal, int year, string code)
    {
        if (IsGregorianLike(cal)) return (code[1] - '0') * 10 + (code[2] - '0');
        int i = Array.IndexOf(GetYear(cal, year).MonthCodes, code);
        return i + 1;
    }

    public static string MonthCodeOf(string cal, int year, int ordinal) =>
        IsGregorianLike(cal) ? CreateMonthCode(ordinal, false) : GetYear(cal, year).MonthCodes[ordinal - 1];

    /// <summary>BalanceNonISODate.</summary>
    public static (int Year, int Month, int Day) Balance(string cal, long year, long month, long day)
    {
        int y = (int)year;
        long m = month;
        int miy = MonthsInYear(cal, y);
        while (m <= 0) { y--; miy = MonthsInYear(cal, y); m += miy; }
        while (m > miy) { m -= miy; y++; miy = MonthsInYear(cal, y); }
        // days: go through epoch days for speed
        if (day >= 1 && day <= DaysInMonth(cal, y, (int)m)) return (y, (int)m, (int)day);
        long ed = TemporalMath.EpochDays(IntegersToISO(cal, y, (int)m, 1)) + day - 1;
        var r = ISOToDate(cal, TemporalMath.FromEpochDays(ed));
        return (r.Year, r.Month, r.Day);
    }
}
