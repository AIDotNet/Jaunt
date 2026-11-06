using System;
using System.Collections.Generic;

namespace Jaunt.Runtime.Regex;

/// <summary>Immutable set of code points stored as sorted, merged inclusive ranges.</summary>
internal sealed class CharSet
{
    public readonly int[] Ranges; // [lo0, hi0, lo1, hi1, ...]
    public static readonly CharSet Empty = new(Array.Empty<int>());
    public static readonly CharSet AllCodePoints = new(new[] { 0, 0x10FFFF });

    public CharSet(int[] ranges) => Ranges = ranges;

    public static CharSet Single(int c) => new(new[] { c, c });
    public static CharSet Range(int lo, int hi) => new(new[] { lo, hi });

    public bool IsEmpty => Ranges.Length == 0;

    public bool Contains(int c)
    {
        var r = Ranges;
        int lo = 0, hi = r.Length / 2 - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (c < r[2 * mid]) hi = mid - 1;
            else if (c > r[2 * mid + 1]) lo = mid + 1;
            else return true;
        }
        return false;
    }

    public static CharSet FromRanges(List<(int lo, int hi)> list)
    {
        if (list.Count == 0) return Empty;
        list.Sort((a, b) => a.lo.CompareTo(b.lo));
        var outp = new List<int>(list.Count * 2);
        int cl = list[0].lo, ch = list[0].hi;
        for (int i = 1; i < list.Count; i++)
        {
            var (lo, hi) = list[i];
            if (lo <= ch + 1) { if (hi > ch) ch = hi; }
            else { outp.Add(cl); outp.Add(ch); cl = lo; ch = hi; }
        }
        outp.Add(cl); outp.Add(ch);
        return new CharSet(outp.ToArray());
    }

    public CharSet Union(CharSet o)
    {
        if (o.IsEmpty) return this;
        if (IsEmpty) return o;
        var list = new List<(int, int)>(Ranges.Length / 2 + o.Ranges.Length / 2);
        for (int i = 0; i < Ranges.Length; i += 2) list.Add((Ranges[i], Ranges[i + 1]));
        for (int i = 0; i < o.Ranges.Length; i += 2) list.Add((o.Ranges[i], o.Ranges[i + 1]));
        return FromRanges(list);
    }

    public CharSet Complement(int max = 0x10FFFF)
    {
        var outp = new List<int>();
        int next = 0;
        for (int i = 0; i < Ranges.Length; i += 2)
        {
            int lo = Ranges[i], hi = Ranges[i + 1];
            if (lo > max) break;
            if (lo > next) { outp.Add(next); outp.Add(lo - 1); }
            next = hi + 1;
        }
        if (next <= max) { outp.Add(next); outp.Add(max); }
        return new CharSet(outp.ToArray());
    }

    public CharSet Intersect(CharSet o)
    {
        var outp = new List<int>();
        int i = 0, j = 0;
        var a = Ranges;
        var b = o.Ranges;
        while (i < a.Length && j < b.Length)
        {
            int lo = Math.Max(a[i], b[j]), hi = Math.Min(a[i + 1], b[j + 1]);
            if (lo <= hi) { outp.Add(lo); outp.Add(hi); }
            if (a[i + 1] < b[j + 1]) i += 2; else j += 2;
        }
        return new CharSet(outp.ToArray());
    }

    public CharSet Subtract(CharSet o) => Intersect(o.Complement());

    public IEnumerable<int> CodePoints()
    {
        for (int i = 0; i < Ranges.Length; i += 2)
            for (int c = Ranges[i]; c <= Ranges[i + 1]; c++) yield return c;
    }

    public int Count
    {
        get
        {
            long n = 0;
            for (int i = 0; i < Ranges.Length; i += 2) n += Ranges[i + 1] - Ranges[i] + 1;
            return (int)Math.Min(n, int.MaxValue);
        }
    }
}
