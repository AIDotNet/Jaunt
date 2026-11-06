using System;
using System.Collections.Generic;

namespace Jaunt.Runtime;

/// <summary>Result of parsing an ISO 8601 / RFC 9557 Temporal string.</summary>
internal sealed class IsoParseResult
{
    public bool HasYear = true;
    public int Year;
    public int Month = 1;
    public bool HasDay = true;
    public int Day = 1;
    public bool HasTime;
    public TimeRec Time;
    public bool Z;
    /// <summary>Offset text as written (null when none); <see cref="OffsetNs"/> is its value.</summary>
    public string? Offset;
    public long OffsetNs;
    public bool OffsetHasSubMinute;
    /// <summary>Bracketed time zone annotation (identifier text), if any.</summary>
    public string? TimeZone;
    public string? Calendar;
}

internal enum ParseGoal { DateTime, ZonedDateTime, Instant, Time, YearMonth, MonthDay, RelativeTo, TimeZone, Calendar }

/// <summary>Hand-written parser for the Temporal string grammar (ISO 8601 + RFC 9557 annotations).</summary>
internal ref struct TemporalParser
{
    private readonly string _s;
    private int _p;

    private TemporalParser(string s) { _s = s; _p = 0; }

    private bool End => _p >= _s.Length;
    private char Peek(int k = 0) => _p + k < _s.Length ? _s[_p + k] : '\0';
    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    private bool TryDigits(int n, out int value)
    {
        value = 0;
        if (_p + n > _s.Length) return false;
        for (int i = 0; i < n; i++)
        {
            char c = _s[_p + i];
            if (!IsDigit(c)) return false;
            value = value * 10 + (c - '0');
        }
        _p += n;
        return true;
    }

    private bool Eat(char c)
    {
        if (Peek() == c) { _p++; return true; }
        return false;
    }

    // ------------------------------------------------------------------ public entry points

    public static IsoParseResult? Parse(string s, ParseGoal goal)
    {
        // all productions are ASCII; reject anything else early (e.g. U+2212 minus sign)
        foreach (char c in s) if (c > 0x7F) return null;
        try { return ParseCore(s, goal); }
        catch (FormatException) { return null; }   // critical-flag violations in annotations
    }

    private static IsoParseResult? ParseCore(string s, ParseGoal goal)
    {
        switch (goal)
        {
            case ParseGoal.Time: return ParseTime(s);
            case ParseGoal.YearMonth:
                return TryWhole(s, static (ref TemporalParser p, IsoParseResult r) => p.YearMonthBody(r)) ?? ParseDateTimeGoal(s, goal);
            case ParseGoal.MonthDay:
                return TryWhole(s, static (ref TemporalParser p, IsoParseResult r) => p.MonthDayBody(r)) ?? ParseDateTimeGoal(s, goal);
            default: return ParseDateTimeGoal(s, goal);
        }
    }

    private delegate bool Body(ref TemporalParser p, IsoParseResult r);

    private static IsoParseResult? TryWhole(string s, Body body)
    {
        var p = new TemporalParser(s);
        var r = new IsoParseResult();
        if (!body(ref p, r)) return null;
        if (!p.Annotations(r, allowTimeZone: true)) return null;
        return p.End ? r : null;
    }

    private static IsoParseResult? ParseDateTimeGoal(string s, ParseGoal goal)
    {
        var p = new TemporalParser(s);
        var r = new IsoParseResult();
        if (!p.DateSpec(r)) return null;
        if (p.Peek() is 'T' or 't' or ' ')
        {
            p._p++;
            if (!p.TimeSpec(r)) return null;
            r.HasTime = true;
            p.OptionalOffset(r);
        }
        else if (goal is ParseGoal.Instant) return null;
        if (!p.Annotations(r, allowTimeZone: true)) return null;
        if (!p.End) return null;
        switch (goal)
        {
            case ParseGoal.Instant:
                if (!r.Z && r.Offset == null) return null;
                break;
            case ParseGoal.ZonedDateTime:
                if (r.TimeZone == null) return null;
                break;
            case ParseGoal.DateTime:
            case ParseGoal.YearMonth:
            case ParseGoal.MonthDay:
                if (r.Z) return null;
                break;
            case ParseGoal.RelativeTo:
                if (r.Z && r.TimeZone == null) return null;
                break;
        }
        return r;
    }

    private static IsoParseResult? ParseTime(string s)
    {
        // AnnotatedTime: with designator
        if (s.Length > 0 && (s[0] == 'T' || s[0] == 't'))
        {
            var p = new TemporalParser(s) { _p = 1 };
            var r = new IsoParseResult { HasYear = false };
            if (!p.TimeSpec(r)) return null;
            r.HasTime = true;
            p.OptionalOffset(r);
            if (r.Z) return null;
            if (!p.Annotations(r, allowTimeZone: true) || !p.End) return null;
            return r;
        }
        // without designator: must not be ambiguous with a month-day or year-month
        {
            var p = new TemporalParser(s);
            var r = new IsoParseResult { HasYear = false };
            if (p.TimeSpec(r))
            {
                r.HasTime = true;
                int timeEnd = p._p;
                p.OptionalOffset(r);
                int offsetEnd = p._p;
                if (!r.Z && p.Annotations(r, allowTimeZone: true) && p.End)
                {
                    var core = s.Substring(0, offsetEnd);
                    if (!IsAmbiguousWithDate(core)) return r;
                    return null;
                }
                _ = timeEnd;
            }
        }
        // AnnotatedDateTimeTimeRequired
        var dt = ParseDateTimeGoal(s, ParseGoal.DateTime);
        if (dt == null || !dt.HasTime) return null;
        return dt;
    }

    private static bool IsAmbiguousWithDate(string core)
    {
        var p = new TemporalParser(core);
        var r = new IsoParseResult();
        if (p.MonthDayBody(r) && p.End) return true;
        p = new TemporalParser(core);
        r = new IsoParseResult();
        if (p.YearMonthBody(r) && p.End) return true;
        return false;
    }

    // ------------------------------------------------------------------ productions

    private bool Year(out int year)
    {
        year = 0;
        char c = Peek();
        if (c == '+' || c == '-')
        {
            _p++;
            if (!TryDigits(6, out int y)) return false;
            if (c == '-' && y == 0) return false;   // -000000 is not allowed
            year = c == '-' ? -y : y;
            return true;
        }
        return TryDigits(4, out year);
    }

    private bool DateSpec(IsoParseResult r)
    {
        if (!Year(out r.Year)) return false;
        bool ext = Eat('-');
        if (!TryDigits(2, out r.Month)) return false;
        if (ext) { if (!Eat('-')) return false; }
        else if (Peek() == '-') return false;
        if (!TryDigits(2, out r.Day)) return false;
        if (r.Month < 1 || r.Month > 12 || r.Day < 1 || r.Day > TemporalMath.DaysInMonth(r.Year, r.Month)) return false;
        return true;
    }

    private bool YearMonthBody(IsoParseResult r)
    {
        int save = _p;
        if (!Year(out r.Year)) { _p = save; return false; }
        Eat('-');
        if (!TryDigits(2, out r.Month) || r.Month < 1 || r.Month > 12) { _p = save; return false; }
        r.HasDay = false;
        return true;
    }

    private bool MonthDayBody(IsoParseResult r)
    {
        int save = _p;
        if (Peek() == '-' && Peek(1) == '-') _p += 2;
        if (!TryDigits(2, out r.Month)) { _p = save; return false; }
        Eat('-');
        if (!TryDigits(2, out r.Day)) { _p = save; return false; }
        if (r.Month < 1 || r.Month > 12 || r.Day < 1 || r.Day > TemporalMath.DaysInMonth(1972, r.Month)) { _p = save; return false; }
        r.HasYear = false;
        return true;
    }

    private bool TimeSpec(IsoParseResult r)
    {
        if (!TryDigits(2, out int h) || h > 23) return false;
        int m = 0, sec = 0, frac = 0;
        if (Peek() == ':')
        {
            _p++;
            if (!TryDigits(2, out m) || m > 59) return false;
            if (Peek() == ':')
            {
                _p++;
                if (!TryDigits(2, out sec) || sec > 60) return false;
                if (!Fraction(out frac)) return false;
            }
        }
        else if (IsDigit(Peek()))
        {
            if (!TryDigits(2, out m) || m > 59) return false;
            if (IsDigit(Peek()))
            {
                if (!TryDigits(2, out sec) || sec > 60) return false;
                if (!Fraction(out frac)) return false;
            }
        }
        if (sec == 60) sec = 59;
        r.Time = new TimeRec(h, m, sec, frac / 1_000_000, frac / 1000 % 1000, frac % 1000);
        return true;
    }

    /// <summary>Optional TemporalDecimalFraction; value in nanoseconds (0..999,999,999).</summary>
    private bool Fraction(out int ns)
    {
        ns = 0;
        if (Peek() != '.' && Peek() != ',') return true;
        _p++;
        int digits = 0;
        while (IsDigit(Peek()))
        {
            if (digits == 9) return false;
            ns = ns * 10 + (_s[_p] - '0');
            _p++;
            digits++;
        }
        if (digits == 0) return false;
        for (int i = digits; i < 9; i++) ns *= 10;
        return true;
    }

    private void OptionalOffset(IsoParseResult r)
    {
        if (Peek() is 'Z' or 'z') { _p++; r.Z = true; return; }
        if (Peek() is '+' or '-')
        {
            int save = _p;
            if (UtcOffset(allowSubMinute: true, out long ns, out bool sub))
            {
                r.Offset = _s.Substring(save, _p - save);
                r.OffsetNs = ns;
                r.OffsetHasSubMinute = sub;
            }
            else _p = save;
        }
    }

    /// <summary>UTCOffset: ±HH[:MM[:SS[.fff]]] or the basic form ±HH[MM[SS[.fff]]].</summary>
    private bool UtcOffset(bool allowSubMinute, out long ns, out bool hasSubMinute)
    {
        ns = 0;
        hasSubMinute = false;
        char sign = Peek();
        if (sign != '+' && sign != '-') return false;
        _p++;
        if (!TryDigits(2, out int h) || h > 23) return false;
        int m = 0, sec = 0, frac = 0;
        if (Peek() == ':')
        {
            _p++;
            if (!TryDigits(2, out m) || m > 59) return false;
            if (Peek() == ':')
            {
                if (!allowSubMinute) return false;
                _p++;
                if (!TryDigits(2, out sec) || sec > 59) return false;
                if (!Fraction(out frac)) return false;
                hasSubMinute = true;
            }
        }
        else if (IsDigit(Peek()))
        {
            if (!TryDigits(2, out m) || m > 59) return false;
            if (IsDigit(Peek()))
            {
                if (!allowSubMinute) return false;
                if (!TryDigits(2, out sec) || sec > 59) return false;
                if (!Fraction(out frac)) return false;
                hasSubMinute = true;
            }
        }
        ns = ((h * 60L + m) * 60 + sec) * TemporalMath.NsPerSecond + frac;
        if (sign == '-') ns = -ns;
        return true;
    }

    private bool Annotations(IsoParseResult r, bool allowTimeZone)
    {
        bool first = true;
        bool calendarSeen = false, calendarCritical = false;
        while (Peek() == '[')
        {
            int start = _p;
            _p++;
            bool critical = Eat('!');
            // a time zone annotation may only come first
            int keyStart = _p;
            int eq = _s.IndexOf('=', _p);
            int close = _s.IndexOf(']', _p);
            if (close < 0) return false;
            if (eq < 0 || eq > close)
            {
                if (!first || !allowTimeZone) return false;
                var id = _s.Substring(_p, close - _p);
                if (!IsTimeZoneIdentifier(id)) return false;
                r.TimeZone = id;
                _p = close + 1;
                first = false;
                continue;
            }
            first = false;
            var key = _s.Substring(keyStart, eq - keyStart);
            var value = _s.Substring(eq + 1, close - eq - 1);
            if (!IsAnnotationKey(key) || !IsAnnotationValue(value)) return false;
            _p = close + 1;
            if (key == "u-ca")
            {
                if (!calendarSeen) { r.Calendar = value; calendarSeen = true; calendarCritical = critical; }
                else if (critical || calendarCritical) throw new FormatException("multiple calendar annotations with a critical flag");
            }
            else if (critical) throw new FormatException("unknown critical annotation " + key);
            _ = start;
        }
        return true;
    }

    private static bool IsAnnotationKey(string k)
    {
        if (k.Length == 0) return false;
        char c0 = k[0];
        if (!((c0 >= 'a' && c0 <= 'z') || c0 == '_')) return false;
        foreach (char c in k)
            if (!((c >= 'a' && c <= 'z') || IsDigit(c) || c == '_' || c == '-')) return false;
        return true;
    }

    private static bool IsAnnotationValue(string v)
    {
        if (v.Length == 0) return false;
        foreach (var part in v.Split('-'))
        {
            if (part.Length == 0) return false;
            foreach (char c in part) if (!char.IsAsciiLetterOrDigit(c)) return false;
        }
        return true;
    }

    /// <summary>TimeZoneIdentifier: a UTC offset with minute precision, or an IANA-style name.</summary>
    public static bool IsTimeZoneIdentifier(string id)
    {
        if (id.Length == 0) return false;
        if (id[0] is '+' or '-')
        {
            var p = new TemporalParser(id);
            return p.UtcOffset(allowSubMinute: false, out _, out _) && p.End;
        }
        foreach (var comp in id.Split('/'))
        {
            if (comp.Length == 0 || comp == "." || comp == "..") return false;
            char c0 = comp[0];
            if (!(char.IsAsciiLetter(c0) || c0 == '.' || c0 == '_')) return false;
            foreach (char c in comp)
                if (!(char.IsAsciiLetterOrDigit(c) || c == '.' || c == '_' || c == '-' || c == '+')) return false;
        }
        return true;
    }

    /// <summary>Parses a UTC offset string (ParseDateTimeUTCOffset). Returns false if not an offset.</summary>
    public static bool TryParseOffset(string s, bool allowSubMinute, out long ns, out bool hasSubMinute)
    {
        ns = 0;
        hasSubMinute = false;
        foreach (char c in s) if (c > 0x7F) return false;
        var p = new TemporalParser(s);
        return p.UtcOffset(allowSubMinute, out ns, out hasSubMinute) && p.End;
    }

    // ------------------------------------------------------------------ durations

    /// <summary>ParseTemporalDurationString: fields as exact values (fractions of the last time unit are folded down).</summary>
    public static bool TryParseDuration(string s, out double[] fields, out bool negative, out Int128 fractionalNs, out int fractionalUnit)
    {
        fields = new double[10];
        negative = false;
        fractionalNs = 0;
        fractionalUnit = -1;
        int p = 0;
        foreach (char ch in s) if (ch > 0x7F) return false;
        if (p < s.Length && (s[p] == '+' || s[p] == '-')) { negative = s[p] == '-'; p++; }
        if (p >= s.Length || (s[p] != 'P' && s[p] != 'p')) return false;
        p++;
        bool inTime = false, any = false, anyTime = false;
        int lastIndex = -1;
        bool fractionSeen = false;
        while (p < s.Length)
        {
            if (s[p] == 'T' || s[p] == 't')
            {
                if (inTime) return false;
                inTime = true;
                p++;
                continue;
            }
            if (fractionSeen) return false;   // a fraction must be on the last component
            int start = p;
            while (p < s.Length && IsDigit(s[p])) p++;
            if (p == start) return false;
            var intPart = s.Substring(start, p - start);
            string? frac = null;
            if (p < s.Length && (s[p] == '.' || s[p] == ','))
            {
                p++;
                int fs = p;
                while (p < s.Length && IsDigit(s[p])) p++;
                if (p == fs || p - fs > 9) return false;
                frac = s.Substring(fs, p - fs);
            }
            if (p >= s.Length) return false;
            char d = char.ToUpperInvariant(s[p]);
            p++;
            int idx = inTime
                ? d switch { 'H' => 4, 'M' => 5, 'S' => 6, _ => -1 }
                : d switch { 'Y' => 0, 'M' => 1, 'W' => 2, 'D' => 3, _ => -1 };
            if (idx < 0 || idx <= lastIndex) return false;
            if (frac != null && !inTime) return false;
            lastIndex = idx;
            any = true;
            if (inTime) anyTime = true;
            fields[idx] = ParseDecimalInteger(intPart);
            if (frac != null)
            {
                fractionSeen = true;
                fractionalUnit = idx;
                // fraction of an hour/minute/second, in nanoseconds of that unit
                long unitNs = idx switch { 4 => TemporalMath.NsPerHour, 5 => TemporalMath.NsPerMinute, _ => TemporalMath.NsPerSecond };
                Int128 num = Int128.Parse(frac.PadRight(9, '0'));
                fractionalNs = num * unitNs / 1_000_000_000;
            }
        }
        if (!any) return false;
        if (inTime && !anyTime) return false;
        return true;
    }

    private static double ParseDecimalInteger(string digits) =>
        double.Parse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
}
