using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Jaunt.Runtime;

/// <summary>A resolved Temporal time zone: UTC offset (minute precision) or a named zone.</summary>
internal sealed class TemporalTz
{
    /// <summary>Identifier as reported by timeZoneId (case-normalized; offsets as ±HH:MM).</summary>
    public required string Id;
    public bool IsOffset;
    public long OffsetNs;
    /// <summary>Named zone data; null for UTC and offset zones.</summary>
    public TimeZoneInfo? Info;
    /// <summary>ICU id of a named zone (preferred over <see cref="Info"/> when present).</summary>
    public string? IcuId;
    /// <summary>Primary IANA identifier used by TimeZoneEquals.</summary>
    public string? Primary;

    public bool IsFixed => Info == null && IcuId == null;
}

/// <summary>Time zone operations of Temporal (offsets, possible instants, disambiguation, transitions).</summary>
internal static class TemporalTimeZones
{
    public static readonly TemporalTz Utc = new() { Id = "UTC" };
    private static readonly ConcurrentDictionary<string, TemporalTz?> s_named = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string>? s_canonicalCase;
    private static readonly Dictionary<string, string> s_ianaLinks2024b = new(StringComparer.Ordinal)
    {
        ["CST6CDT"] = "America/Chicago", ["EST"] = "America/Panama", ["EST5EDT"] = "America/New_York", ["HST"] = "Pacific/Honolulu",
        ["MST"] = "America/Phoenix", ["MST7MDT"] = "America/Denver", ["PST8PDT"] = "America/Los_Angeles",
    };
    /// <summary>The primary zone of a legacy zone that IANA 2024b made a link (identity otherwise).</summary>
    public static string PrimaryOfLegacyZone(string id) => s_ianaLinks2024b.TryGetValue(id, out var p) ? p : id;

    private static readonly HashSet<string> s_nonIana = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACT", "AET", "AGT", "ART", "AST", "BET", "BST", "CAT", "CNT", "CST", "CTT", "EAT", "ECT", "IET", "IST", "JST",
        "MIT", "NET", "NST", "PLT", "PNT", "PRT", "PST", "SST", "VST",
    };

    /// <summary>ParseTimeZoneIdentifier + GetAvailableNamedTimeZoneIdentifier; null when not a valid/available zone.</summary>
    public static TemporalTz? Resolve(string id)
    {
        if (!TemporalParser.IsTimeZoneIdentifier(id)) return null;
        if (id[0] is '+' or '-')
        {
            if (!TemporalParser.TryParseOffset(id, false, out long ns, out _)) return null;
            return FromOffset(ns);
        }
        if (string.Equals(id, "UTC", StringComparison.OrdinalIgnoreCase)) return Utc;
        // ICU's legacy non-IANA links (from icuzones) are not time zone identifiers
        if (s_nonIana.Contains(id) || id.StartsWith("SystemV/", StringComparison.OrdinalIgnoreCase)) return null;
        return s_named.GetOrAdd(id, static n => LoadNamed(n));
    }

    public static TemporalTz FromOffset(long ns) => new() { Id = FormatOffsetId(ns), IsOffset = true, OffsetNs = ns };

    private static TemporalTz? LoadNamed(string id)
    {
        string canonical = CanonicalCase(id) ?? id;
        if (canonical is "Etc/UTC" or "Etc/GMT" or "GMT") return new TemporalTz { Id = canonical, Primary = "UTC" };
        if (Icu.Available)
        {
            var icu = TemporalIcuZones.CanonicalId(canonical);
            if (icu != null)
            {
                var primary = Icu.IanaTimeZoneId(icu);
                if (primary is "Etc/UTC" or "Etc/GMT") primary = "UTC";
                // IANA 2024b turned these legacy zones into links (ICU may still list them as zones)
                if (s_ianaLinks2024b.TryGetValue(primary, out var linked)) primary = linked;
                return new TemporalTz { Id = canonical, IcuId = canonical, Primary = primary };
            }
        }
        try
        {
            var info = TimeZoneInfo.FindSystemTimeZoneById(id);
            return new TemporalTz { Id = canonical, Info = info, Primary = info.Id };
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException or System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return null;
        }
    }

    private static string? CanonicalCase(string id)
    {
        var map = s_canonicalCase;
        if (map == null)
        {
            map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try { foreach (var z in TimeZoneInfo.GetSystemTimeZones()) map[z.Id] = z.Id; }
            catch (Exception) { }
            if (Icu.Available)
                foreach (var z in Icu.TimeZoneIds(0, null)) map.TryAdd(z, z);
            s_canonicalCase = map;
        }
        return map.TryGetValue(id, out var c) ? c : null;
    }

    /// <summary>FormatOffsetTimeZoneIdentifier: ±HH:MM.</summary>
    public static string FormatOffsetId(long ns)
    {
        long minutes = ns / TemporalMath.NsPerMinute;
        char sign = minutes < 0 ? '-' : '+';
        minutes = Math.Abs(minutes);
        return $"{sign}{minutes / 60:00}:{minutes % 60:00}";
    }

    /// <summary>FormatUTCOffsetNanoseconds: ±HH:MM[:SS[.fffffffff]].</summary>
    public static string FormatOffsetNs(long ns)
    {
        char sign = ns < 0 ? '-' : '+';
        ns = Math.Abs(ns);
        long h = ns / TemporalMath.NsPerHour, m = ns / TemporalMath.NsPerMinute % 60, s = ns / TemporalMath.NsPerSecond % 60, f = ns % TemporalMath.NsPerSecond;
        var str = $"{sign}{h:00}:{m:00}";
        if (s != 0 || f != 0)
        {
            str += $":{s:00}";
            if (f != 0) str += "." + f.ToString("000000000").TrimEnd('0');
        }
        return str;
    }

    public static bool Equals(TemporalTz a, TemporalTz b)
    {
        if (ReferenceEquals(a, b) || a.Id == b.Id) return true;
        if (a.IsOffset || b.IsOffset) return a.IsOffset && b.IsOffset && a.OffsetNs == b.OffsetNs;
        string pa = a.Primary ?? a.Id, pb = b.Primary ?? b.Id;
        return string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ offsets

    public static long GetOffsetNs(TemporalTz tz, Int128 epochNs)
    {
        if (tz.IsOffset) return tz.OffsetNs;
        if (tz.IcuId != null) return TemporalIcuZones.OffsetNs(tz.IcuId, epochNs);
        if (tz.Info == null) return 0;
        return NamedOffset(tz.Info, epochNs);
    }

    private static readonly long s_minTicksEpoch = (DateTime.MinValue.Ticks - DateTime.UnixEpoch.Ticks);
    private static readonly long s_maxTicksEpoch = (DateTime.MaxValue.Ticks - DateTime.UnixEpoch.Ticks);

    private static long NamedOffset(TimeZoneInfo info, Int128 epochNs)
    {
        var ticks = TemporalMath.FloorDiv(epochNs, 100);
        long t = ticks < s_minTicksEpoch + TimeSpan.TicksPerDay ? s_minTicksEpoch + TimeSpan.TicksPerDay
            : ticks > s_maxTicksEpoch - TimeSpan.TicksPerDay ? s_maxTicksEpoch - TimeSpan.TicksPerDay : (long)ticks;
        var utc = new DateTime(DateTime.UnixEpoch.Ticks + t, DateTimeKind.Utc);
        return info.GetUtcOffset(utc).Ticks * 100;
    }

    /// <summary>GetPossibleEpochNanoseconds (sorted ascending).</summary>
    public static List<Int128> GetPossibleEpochNs(Realm realm, TemporalTz tz, IsoDateTime dt)
    {
        var result = new List<Int128>(2);
        var local = TemporalMath.GetUTCEpochNanoseconds(dt);
        if (tz.IsFixed)
        {
            var e = local - (tz.IsOffset ? tz.OffsetNs : 0);
            if (!TemporalMath.IsValidEpochNanoseconds(e)) throw JsErrors.RangeError(realm, "date-time is outside the representable range");
            result.Add(e);
            return result;
        }
        long before = GetOffsetNs(tz, local - TemporalMath.NsPerDay);
        long after = GetOffsetNs(tz, local + TemporalMath.NsPerDay);
        Span<long> cands = stackalloc long[2] { before, after };
        foreach (var off in cands)
        {
            var e = local - off;
            if (GetOffsetNs(tz, e) == off && !result.Contains(e)) result.Add(e);
        }
        result.Sort();
        foreach (var e in result)
            if (!TemporalMath.IsValidEpochNanoseconds(e)) throw JsErrors.RangeError(realm, "date-time is outside the representable range");
        return result;
    }

    /// <summary>GetEpochNanosecondsFor = DisambiguatePossibleEpochNanoseconds(GetPossibleEpochNanoseconds(...)).</summary>
    public static Int128 GetEpochNsFor(Realm realm, TemporalTz tz, IsoDateTime dt, Disambiguation d)
    {
        var possible = GetPossibleEpochNs(realm, tz, dt);
        return Disambiguate(realm, possible, tz, dt, d);
    }

    public static Int128 Disambiguate(Realm realm, List<Int128> possible, TemporalTz tz, IsoDateTime dt, Disambiguation d)
    {
        if (possible.Count == 1) return possible[0];
        if (possible.Count > 1)
        {
            if (d is Disambiguation.Compatible or Disambiguation.Earlier) return possible[0];
            if (d == Disambiguation.Later) return possible[^1];
            throw JsErrors.RangeError(realm, "the date-time is ambiguous in this time zone");
        }
        if (d == Disambiguation.Reject) throw JsErrors.RangeError(realm, "the date-time does not exist in this time zone");
        // gap: use the offsets before and after the transition
        var utc = TemporalMath.GetUTCEpochNanoseconds(dt);
        var dayBefore = utc - TemporalMath.NsPerDay;
        var dayAfter = utc + TemporalMath.NsPerDay;
        if (!TemporalMath.IsValidEpochNanoseconds(dayBefore) || !TemporalMath.IsValidEpochNanoseconds(dayAfter))
            throw JsErrors.RangeError(realm, "date-time is outside the representable range");
        long offBefore = GetOffsetNs(tz, dayBefore);
        long offAfter = GetOffsetNs(tz, dayAfter);
        long gap = offAfter - offBefore;
        if (d == Disambiguation.Earlier)
        {
            var earlier = TemporalMath.AddTime(dt.Time, -gap);
            var edt = new IsoDateTime(TemporalMath.AddDays(dt.Date, earlier.Days), earlier.Time);
            return GetPossibleEpochNs(realm, tz, edt)[0];
        }
        var later = TemporalMath.AddTime(dt.Time, gap);
        var ldt = new IsoDateTime(TemporalMath.AddDays(dt.Date, later.Days), later.Time);
        var p = GetPossibleEpochNs(realm, tz, ldt);
        return p[^1];
    }

    /// <summary>GetStartOfDay.</summary>
    public static Int128 GetStartOfDay(Realm realm, TemporalTz tz, IsoDate date)
    {
        var dt = new IsoDateTime(date, TimeRec.Midnight);
        var possible = GetPossibleEpochNs(realm, tz, dt);
        if (possible.Count > 0) return possible[0];
        // midnight is skipped: the day starts at the transition
        var utc = TemporalMath.GetUTCEpochNanoseconds(dt);
        var t = GetNextTransition(tz, utc - TemporalMath.NsPerDay);
        if (t == null) throw JsErrors.RangeError(realm, "start of day does not exist");
        return t.Value;
    }

    // ------------------------------------------------------------------ transitions

    public static Int128? GetNextTransition(TemporalTz tz, Int128 epochNs)
    {
        if (tz.IsFixed) return null;
        if (tz.IcuId != null) return TemporalIcuZones.Next(tz.IcuId, epochNs);
        var info = tz.Info!;
        Int128 step = TemporalMath.NsPerDay * 7;
        var limit = TemporalMath.NsMaxInstant;
        var cur = epochNs;
        long off = NamedOffset(info, cur);
        int guard = 0;
        while (cur < limit && guard++ < 20000)
        {
            var next = cur + step > limit ? limit : cur + step;
            long o2 = NamedOffset(info, next);
            if (o2 != off) return Bisect(info, cur, next, off);
            cur = next;
            if (cur > (Int128)s_maxTicksEpoch * 100) break;
        }
        return null;
    }

    public static Int128? GetPreviousTransition(TemporalTz tz, Int128 epochNs)
    {
        if (tz.IsFixed) return null;
        if (tz.IcuId != null) return TemporalIcuZones.Previous(tz.IcuId, epochNs);
        var info = tz.Info!;
        Int128 step = TemporalMath.NsPerDay * 7;
        var limit = TemporalMath.NsMinInstant;
        var cur = epochNs - 1;
        long off = NamedOffset(info, cur);
        int guard = 0;
        while (cur > limit && guard++ < 20000)
        {
            var prev = cur - step < limit ? limit : cur - step;
            long o2 = NamedOffset(info, prev);
            if (o2 != off) return Bisect(info, prev, cur, o2);
            cur = prev;
            if (cur < (Int128)s_minTicksEpoch * 100) break;
        }
        return null;
    }

    /// <summary>Finds the first instant in (lo, hi] whose offset differs from <paramref name="offLo"/>.</summary>
    private static Int128 Bisect(TimeZoneInfo info, Int128 lo, Int128 hi, long offLo)
    {
        while (hi - lo > 1)
        {
            var mid = lo + (hi - lo) / 2;
            if (NamedOffset(info, mid) == offLo) lo = mid; else hi = mid;
        }
        return hi;
    }
}
