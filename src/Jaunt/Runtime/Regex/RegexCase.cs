using System;
using System.Collections.Generic;
using System.Threading;

namespace Jaunt.Runtime.Regex;

/// <summary>Canonicalize (22.2.2.7.3): simple case folding for u/v patterns, legacy upper-casing otherwise.</summary>
internal static class RegexCase
{
    private static (int[] From, int[] To)? s_unicode, s_legacy;
    private static CharSet? s_unicodeNonCanonical;

    public static int Canon(int c, bool unicode)
    {
        if (unicode) return UnicodeData.SimpleCaseFold(c);
        if (c > 0xFFFF) return c;
        int u = char.ToUpperInvariant((char)c);
        if (c >= 128 && u < 128) return c;
        return u;
    }

    private static (int[] From, int[] To) Pairs(bool unicode)
    {
        var cached = unicode ? s_unicode : s_legacy;
        if (cached != null) return cached.Value;
        var from = new List<int>();
        var to = new List<int>();
        int max = unicode ? 0x10FFFF : 0xFFFF;
        for (int c = 0; c <= max; c++)
        {
            int k = Canon(c, unicode);
            if (k != c) { from.Add(c); to.Add(k); }
        }
        var p = (from.ToArray(), to.ToArray());
        if (unicode) s_unicode = p; else s_legacy = p;
        return p;
    }

    /// <summary>S ∪ { Canonicalize(x) | x ∈ S }: membership of Canonicalize(input) then implements case-insensitive matching.</summary>
    public static CharSet Close(CharSet set, bool unicode)
    {
        if (set.IsEmpty) return set;
        var (from, to) = Pairs(unicode);
        var extra = new List<(int, int)>();
        for (int i = 0; i < from.Length; i++)
            if (set.Contains(from[i])) extra.Add((to[i], to[i]));
        if (extra.Count == 0) return set;
        for (int i = 0; i < set.Ranges.Length; i += 2) extra.Add((set.Ranges[i], set.Ranges[i + 1]));
        return CharSet.FromRanges(extra);
    }

    /// <summary>Code points that are not their own simple case folding (u/v mode).</summary>
    public static CharSet NonCanonical
    {
        get
        {
            var s = s_unicodeNonCanonical;
            if (s != null) return s;
            var (from, _) = Pairs(true);
            var list = new List<(int, int)>(from.Length);
            foreach (var c in from) list.Add((c, c));
            s = CharSet.FromRanges(list);
            Interlocked.CompareExchange(ref s_unicodeNonCanonical, s, null);
            return s_unicodeNonCanonical!;
        }
    }

    private static CharSet? s_legacyNonCanonical;

    private static CharSet NonCanonicalOf(bool unicode)
    {
        if (unicode) return NonCanonical;
        var s = s_legacyNonCanonical;
        if (s != null) return s;
        var (from, _) = Pairs(false);
        var list = new List<(int, int)>(from.Length);
        foreach (var c in from) list.Add((c, c));
        s = CharSet.FromRanges(list);
        Interlocked.CompareExchange(ref s_legacyNonCanonical, s, null);
        return s_legacyNonCanonical!;
    }

    /// <summary>
    /// { x | Canonicalize(x) ∈ <paramref name="canonical"/> }: membership of x in it is ignore-case matching against a
    /// closed set, without canonicalizing x.
    /// </summary>
    public static CharSet Preimage(CharSet canonical, bool unicode)
    {
        var (from, to) = Pairs(unicode);
        var own = canonical.Subtract(NonCanonicalOf(unicode));   // characters that are their own canonical form
        var list = new List<(int, int)>(own.Ranges.Length / 2 + 8);
        for (int i = 0; i < own.Ranges.Length; i += 2) list.Add((own.Ranges[i], own.Ranges[i + 1]));
        for (int i = 0; i < from.Length; i++)
            if (canonical.Contains(to[i])) list.Add((from[i], from[i]));
        return CharSet.FromRanges(list);
    }

    /// <summary>MaybeSimpleCaseFolding for v-mode: the set of canonical characters of S.</summary>
    public static CharSet FoldCanonical(CharSet set) => Close(set, true).Subtract(NonCanonical);

    public static string FoldString(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            int c = s[i];
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { c = char.ConvertToUtf32(s[i], s[i + 1]); i++; }
            int f = UnicodeData.SimpleCaseFold(c);
            if (f > 0xFFFF) sb.Append(char.ConvertFromUtf32(f)); else sb.Append((char)f);
        }
        return sb.ToString();
    }
}
