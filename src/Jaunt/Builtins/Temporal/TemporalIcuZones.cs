using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Jaunt.Runtime;

/// <summary>
/// Named time zones through ICU's time zone data (second-precision historical offsets, exact transitions and
/// canonical identifiers). Used when ICU is available; <see cref="TimeZoneInfo"/> is the fallback.
/// </summary>
internal static unsafe class TemporalIcuZones
{
    private const int UCAL_ZONE_OFFSET = 15, UCAL_DST_OFFSET = 16;
    private const int TRANSITION_NEXT = 0, TRANSITION_PREVIOUS = 2;
    private static nint s_open, s_setMillis, s_get, s_transition, s_canonical;

    [ThreadStatic] private static Dictionary<string, nint>? t_cals;
    private static readonly ConcurrentDictionary<string, string?> s_canonicalIds = new(StringComparer.Ordinal);

    /// <summary>ICU's canonical id (links resolved), or null when ICU does not know the zone.</summary>
    public static string? CanonicalId(string id) => s_canonicalIds.GetOrAdd(id, static z =>
    {
        if (!Icu.Available) return null;
        var f = (delegate* unmanaged<char*, int, char*, int, sbyte*, int*, int>)Icu.P(ref s_canonical, "ucal_getCanonicalTimeZoneID");
        sbyte isSystem = 0;
        var r = Icu.CallUChars((buf, cap, err) => { fixed (char* zp = z) { sbyte s; int n = f(zp, z.Length, buf, cap, &s, err); isSystem = s; return n; } });
        return string.IsNullOrEmpty(r) || isSystem == 0 ? null : r;
    });

    private static nint Cal(string id)
    {
        var d = t_cals ??= new Dictionary<string, nint>(StringComparer.Ordinal);
        if (d.TryGetValue(id, out var h)) return h;
        var open = (delegate* unmanaged<char*, int, byte*, int, int*, nint>)Icu.P(ref s_open, "ucal_open");
        var loc = Icu.Utf8Z("und@calendar=gregorian");
        int err = 0;
        fixed (char* z = id)
        fixed (byte* l = loc)
            h = open(z, id.Length, l, 1, &err);
        if (Icu.Failure(err) || h == 0) throw new InvalidOperationException("ICU time zone unavailable: " + id);
        d[id] = h;
        return h;
    }

    private static double ClampMs(Int128 ns)
    {
        var ms = TemporalMath.FloorDiv(ns, 1_000_000);
        const long lim = 8_640_000_000_000_000L;   // ±10^8 days in ms
        return ms > lim ? lim : ms < -lim ? -lim : (double)(long)ms;
    }

    public static long OffsetNs(string id, Int128 epochNs)
    {
        var c = Cal(id);
        int e = 0;
        ((delegate* unmanaged<nint, double, int*, void>)Icu.P(ref s_setMillis, "ucal_setMillis"))(c, ClampMs(epochNs), &e);
        var get = (delegate* unmanaged<nint, int, int*, int>)Icu.P(ref s_get, "ucal_get");
        long ms = (long)get(c, UCAL_ZONE_OFFSET, &e) + get(c, UCAL_DST_OFFSET, &e);
        return ms * 1_000_000;
    }

    private static double? Transition(string id, double fromMs, int type)
    {
        var c = Cal(id);
        int e = 0;
        ((delegate* unmanaged<nint, double, int*, void>)Icu.P(ref s_setMillis, "ucal_setMillis"))(c, fromMs, &e);
        double t;
        var f = (delegate* unmanaged<nint, int, double*, int*, sbyte>)Icu.P(ref s_transition, "ucal_getTimeZoneTransitionDate");
        if (f(c, type, &t, &e) == 0 || Icu.Failure(e)) return null;
        return t;
    }

    /// <summary>The first instant after <paramref name="epochNs"/> at which the UTC offset changes.</summary>
    public static Int128? Next(string id, Int128 epochNs)
    {
        double from = ClampMs(epochNs);
        for (int guard = 0; guard < 1000; guard++)
        {
            var t = Transition(id, from, TRANSITION_NEXT);
            if (t == null) return null;
            Int128 tns = (Int128)(long)t.Value * 1_000_000;
            if (tns > TemporalMath.NsMaxInstant) return null;
            if (tns > epochNs && OffsetNs(id, tns) != OffsetNs(id, tns - 1)) return tns;
            from = t.Value;
        }
        return null;
    }

    /// <summary>The last instant before <paramref name="epochNs"/> at which the UTC offset changes.</summary>
    public static Int128? Previous(string id, Int128 epochNs)
    {
        double from = ClampMs(epochNs + 999_999);   // ceiling in ms
        for (int guard = 0; guard < 1000; guard++)
        {
            var t = Transition(id, from, TRANSITION_PREVIOUS);
            if (t == null) return null;
            Int128 tns = (Int128)(long)t.Value * 1_000_000;
            if (tns < TemporalMath.NsMinInstant) return null;
            if (tns < epochNs && OffsetNs(id, tns) != OffsetNs(id, tns - 1)) return tns;
            from = t.Value;
        }
        return null;
    }
}
