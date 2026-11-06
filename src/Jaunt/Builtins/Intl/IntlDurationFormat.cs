using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace Jaunt.Runtime;

internal sealed partial class IntlProtos
{
    public JsObject DurationFormatPrototype = null!;
}

/// <summary>An Intl.DurationFormat instance ([[InitializedDurationFormat]]).</summary>
internal sealed class JsDurationFormat : JsObject
{
    public string Locale = "";
    public string DataLocale = "";
    /// <summary>ICU locale id of <see cref="DataLocale"/>.</summary>
    public string IcuLocale = "en_US";
    public string NumberingSystem = "latn";
    public string Style = "short";
    /// <summary>Per unit (years … nanoseconds): style ("long", "short", "narrow", "numeric", "2-digit", "fractional").</summary>
    public readonly string[] UnitStyle = new string[10];
    /// <summary>Per unit: display ("auto" or "always").</summary>
    public readonly string[] UnitDisplay = new string[10];
    public int? FractionalDigits;
    public string HourMinuteSeparator = ":";
    public string MinuteSecondSeparator = ":";

    public JsDurationFormat(Realm realm, JsObject proto) : base(realm, proto) { }
}

/// <summary>Small private ICU helpers for DurationFormat: number skeleton formatting to parts and unit list formatting.</summary>
internal static unsafe partial class Icu
{
    private static nint s_unumfOpen_L, s_unumfClose_L, s_unumfOpenResult_L, s_unumfCloseResult_L, s_unumfFormatDouble_L, s_unumfFormatDecimal_L,
        s_unumfResultToString, s_unumfFieldPositions, s_fpiOpen, s_fpiNext, s_fpiClose,
        s_ulistOpenForType, s_ulistClose_L, s_ulistOpenResult_L, s_ulistCloseResult_L, s_ulistFormatToResult_L, s_ulistAsValue,
        s_ufmtvalGetString_L, s_ufmtvalNextPosition_L, s_ucfposOpen_L, s_ucfposClose_L, s_ucfposConstrainField, s_ucfposGetIndexes_L, s_udatpgBestPattern;

    private static readonly ConcurrentDictionary<string, nint> s_numberFormatters = new(StringComparer.Ordinal);

    /// <summary>A cached UNumberFormatter for a skeleton and locale (immutable and thread-safe); <paramref name="owned"/> = caller must free it.</summary>
    public static nint NumberFormatter(string skeleton, string locale, out bool owned)
    {
        owned = false;
        var key = locale + "\u0001" + skeleton;
        if (s_numberFormatters.TryGetValue(key, out var f)) return f;
        var l = Utf8Z(locale);
        int err = 0;
        nint h;
        fixed (char* sp = skeleton)
        fixed (byte* lp = l)
            h = ((delegate* unmanaged<char*, int, byte*, int*, nint>)P(ref s_unumfOpen_L, "unumf_openForSkeletonAndLocale"))(sp, skeleton.Length, lp, &err);
        if (Failure(err) || h == 0) throw new InvalidOperationException("ICU number skeleton rejected: " + skeleton);
        if (s_numberFormatters.Count >= 512) { owned = true; return h; }   // cache full: the caller frees it with CloseNumberFormatter
        var cached = s_numberFormatters.GetOrAdd(key, h);
        if (cached != h) ((delegate* unmanaged<nint, void>)P(ref s_unumfClose_L, "unumf_close"))(h);
        return cached;
    }

    public static void CloseNumberFormatter(nint formatter) => ((delegate* unmanaged<nint, void>)P(ref s_unumfClose_L, "unumf_close"))(formatter);

    /// <summary>
    /// Formats a number (a double, or an exact decimal string when <paramref name="decimalValue"/> is not null) and returns the
    /// string plus ECMA-402 parts (type, start, end).
    /// </summary>
    public static string FormatNumberToParts(nint formatter, double value, string? decimalValue, List<(string Type, int Start, int End)> parts)
    {
        int err = 0;
        var res = ((delegate* unmanaged<int*, nint>)P(ref s_unumfOpenResult_L, "unumf_openResult"))(&err);
        if (Failure(err)) throw new InvalidOperationException("unumf_openResult failed");
        try
        {
            if (decimalValue != null)
            {
                var d = Utf8Z(decimalValue);
                fixed (byte* dp = d)
                    ((delegate* unmanaged<nint, byte*, int, nint, int*, void>)P(ref s_unumfFormatDecimal_L, "unumf_formatDecimal"))(formatter, dp, d.Length - 1, res, &err);
            }
            else ((delegate* unmanaged<nint, double, nint, int*, void>)P(ref s_unumfFormatDouble_L, "unumf_formatDouble"))(formatter, value, res, &err);
            if (Failure(err)) throw new InvalidOperationException("unumf_format failed");
            var toStr = (delegate* unmanaged<nint, char*, int, int*, int>)P(ref s_unumfResultToString, "unumf_resultToString");
            var s = CallUChars((buf, cap, e) => toStr(res, buf, cap, e)) ?? "";
            // field positions → innermost field per UTF-16 unit
            var field = new int[s.Length];
            var span = new int[s.Length];
            Array.Fill(field, -1);
            Array.Fill(span, int.MaxValue);
            int e2 = 0;
            var it = ((delegate* unmanaged<int*, nint>)P(ref s_fpiOpen, "ufieldpositer_open"))(&e2);
            if (!Failure(e2))
            {
                try
                {
                    ((delegate* unmanaged<nint, nint, int*, void>)P(ref s_unumfFieldPositions, "unumf_resultGetAllFieldPositions"))(res, it, &e2);
                    var next = (delegate* unmanaged<nint, int*, int*, int>)P(ref s_fpiNext, "ufieldpositer_next");
                    while (true)
                    {
                        int b, en;
                        int fid = next(it, &b, &en);
                        if (fid < 0) break;
                        for (int i = Math.Max(0, b); i < Math.Min(en, s.Length); i++)
                            if (en - b < span[i]) { span[i] = en - b; field[i] = fid; }
                    }
                }
                finally { ((delegate* unmanaged<nint, void>)P(ref s_fpiClose, "ufieldpositer_close"))(it); }
            }
            int start = 0;
            for (int i = 1; i <= s.Length; i++)
            {
                if (i < s.Length && field[i] == field[start] && field[i] != 6 /* grouping: one part each */ && field[start] != 6) continue;
                parts.Add((NumberFieldType(field[start], s, start, i), start, i));
                start = i;
            }
            return s;
        }
        finally { ((delegate* unmanaged<nint, void>)P(ref s_unumfCloseResult_L, "unumf_closeResult"))(res); }
    }

    private static string NumberFieldType(int field, string s, int start, int end) => field switch
    {
        0 => "integer",
        1 => "fraction",
        2 => "decimal",
        3 => "exponentSeparator",
        4 => "exponentMinusSign",
        5 => "exponentInteger",
        6 => "group",
        7 => "currency",
        8 => "percentSign",
        10 => s.AsSpan(start, end - start).IndexOfAny('-', '−') >= 0 ? "minusSign" : "plusSign",
        11 => "unit",
        12 => "compact",
        13 => "approximatelySign",
        _ => "literal",
    };

    private static readonly ConcurrentDictionary<string, nint> s_listFormatters = new(StringComparer.Ordinal);

    /// <summary>
    /// Formats strings as a unit list (ulistfmt, type UNITS) of width 0 = wide, 1 = short, 2 = narrow; returns the
    /// formatted string and the [start, end) range of every element, in order.
    /// </summary>
    public static string FormatUnitList(string locale, int width, string[] items, List<(int Start, int End)> elements)
    {
        var key = locale + "\u0001" + width;
        if (!s_listFormatters.TryGetValue(key, out var lf))
        {
            var l = Utf8Z(locale);
            int e0 = 0;
            fixed (byte* lp = l) lf = ((delegate* unmanaged<byte*, int, int, int*, nint>)P(ref s_ulistOpenForType, "ulistfmt_openForType"))(lp, 2 /* ULISTFMT_TYPE_UNITS */, width, &e0);
            if (Failure(e0) || lf == 0) throw new InvalidOperationException("ulistfmt_openForType failed");
            var cached = s_listFormatters.GetOrAdd(key, lf);
            if (cached != lf) ((delegate* unmanaged<nint, void>)P(ref s_ulistClose_L, "ulistfmt_close"))(lf);
            lf = cached;
        }
        int err = 0;
        var res = ((delegate* unmanaged<int*, nint>)P(ref s_ulistOpenResult_L, "ulistfmt_openResult"))(&err);
        if (Failure(err)) throw new InvalidOperationException("ulistfmt_openResult failed");
        var handles = new System.Runtime.InteropServices.GCHandle[items.Length];
        try
        {
            var ptrs = new nint[items.Length];
            var lens = new int[items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                handles[i] = System.Runtime.InteropServices.GCHandle.Alloc(items[i], System.Runtime.InteropServices.GCHandleType.Pinned);
                ptrs[i] = handles[i].AddrOfPinnedObject();
                lens[i] = items[i].Length;
            }
            fixed (nint* pp = ptrs)
            fixed (int* lp = lens)
                ((delegate* unmanaged<nint, nint*, int*, int, nint, int*, void>)P(ref s_ulistFormatToResult_L, "ulistfmt_formatStringsToResult"))(lf, pp, lp, items.Length, res, &err);
            if (Failure(err)) throw new InvalidOperationException("ulistfmt_formatStringsToResult failed");
            var val = ((delegate* unmanaged<nint, int*, nint>)P(ref s_ulistAsValue, "ulistfmt_resultAsValue"))(res, &err);
            int len = 0;
            var str = ((delegate* unmanaged<nint, int*, int*, char*>)P(ref s_ufmtvalGetString_L, "ufmtval_getString"))(val, &len, &err);
            if (Failure(err) || str == null) throw new InvalidOperationException("ufmtval_getString failed");
            var result = new string(str, 0, len);
            var pos = ((delegate* unmanaged<int*, nint>)P(ref s_ucfposOpen_L, "ucfpos_open"))(&err);
            try
            {
                ((delegate* unmanaged<nint, int, int, int*, void>)P(ref s_ucfposConstrainField, "ucfpos_constrainField"))(pos, 3 /* UFIELD_CATEGORY_LIST */, 1 /* ULISTFMT_ELEMENT_FIELD */, &err);
                var next = (delegate* unmanaged<nint, nint, int*, sbyte>)P(ref s_ufmtvalNextPosition_L, "ufmtval_nextPosition");
                var idx = (delegate* unmanaged<nint, int*, int*, int*, void>)P(ref s_ucfposGetIndexes_L, "ucfpos_getIndexes");
                while (next(val, pos, &err) != 0 && !Failure(err))
                {
                    int s, e;
                    idx(pos, &s, &e, &err);
                    elements.Add((s, e));
                }
            }
            finally { ((delegate* unmanaged<nint, void>)P(ref s_ucfposClose_L, "ucfpos_close"))(pos); }
            return result;
        }
        finally
        {
            foreach (var h in handles) if (h.IsAllocated) h.Free();
            ((delegate* unmanaged<nint, void>)P(ref s_ulistCloseResult_L, "ulistfmt_closeResult"))(res);
        }
    }

    /// <summary>udatpg_getBestPattern for a skeleton (null on error).</summary>
    public static string? BestDatePattern(string locale, string skeleton)
    {
        var l = Utf8Z(locale);
        int err = 0;
        nint g;
        fixed (byte* lp = l) g = ((delegate* unmanaged<byte*, int*, nint>)P(ref s_udatpgOpen_L, "udatpg_open"))(lp, &err);
        if (Failure(err) || g == 0) return null;
        try
        {
            var f = (delegate* unmanaged<nint, char*, int, char*, int, int*, int>)P(ref s_udatpgBestPattern, "udatpg_getBestPattern");
            return CallUChars((buf, cap, e) => { fixed (char* sp = skeleton) return f(g, sp, skeleton.Length, buf, cap, e); });
        }
        finally { ((delegate* unmanaged<nint, void>)P(ref s_udatpgClose_L, "udatpg_close"))(g); }
    }
}

internal static partial class IntlBuiltins
{
    private static readonly string[] s_dfUnits = { "years", "months", "weeks", "days", "hours", "minutes", "seconds", "milliseconds", "microseconds", "nanoseconds" };
    private static readonly string[] s_dfNfUnits = { "year", "month", "week", "day", "hour", "minute", "second", "millisecond", "microsecond", "nanosecond" };
    private static readonly string[] s_dfDateStyles = { "long", "short", "narrow" };
    private static readonly string[] s_dfTimeStyles = { "long", "short", "narrow", "numeric", "2-digit" };
    private static readonly string[] s_dfSubsecondStyles = { "long", "short", "narrow", "numeric" };
    private static readonly string[] s_dfBaseStyles = { "long", "short", "narrow", "digital" };
    private static readonly string[] s_dfDisplays = { "auto", "always" };
    private static readonly string[] s_nuKey = { "nu" };
    private static readonly ConcurrentDictionary<string, (string Hm, string Ms)> s_dfSeparators = new(StringComparer.Ordinal);

    /// <summary>Temporal.Duration.prototype.toLocaleString: format with a new DurationFormat(locales, options).</summary>
    internal static string FormatDuration(Realm realm, double[] duration, in JsValue locales, in JsValue options)
    {
        var df = new JsDurationFormat(realm, realm.Intl!.DurationFormatPrototype);
        InitializeDurationFormat(realm, df, locales, options);
        var sb = new StringBuilder();
        foreach (var part in PartitionDurationFormatPattern(df, duration)) sb.Append(part.Value);
        return sb.ToString();
    }

    static partial void SetupDurationFormat(Realm r, JsObject intl, IntlProtos protos)
    {
        var proto = new JsObject(r, r.ObjectPrototype);
        protos.DurationFormatPrototype = proto;
        proto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("Intl.DurationFormat"), PropFlags.Configurable);
        var ctor = r.Constructor("DurationFormat", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (!a.IsConstruct) throw JsErrors.TypeError(realm, "Constructor Intl.DurationFormat requires 'new'");
            var p = IntlCore.ProtoFromCtor(realm, a.NewTarget.AsObjectUnchecked, static ip => ip.DurationFormatPrototype);
            var df = new JsDurationFormat(realm, p);
            InitializeDurationFormat(realm, df, a[0], a[1]);
            return JsValue.FromObject(df);
        }, proto);
        intl.DefineHidden("DurationFormat", JsValue.FromObject(ctor));
        r.Method(ctor, "supportedLocalesOf", 1, static (in NativeArgs a) => IntlCore.SupportedLocalesOf(a.Realm, a[0], a[1]));

        r.Method(proto, "resolvedOptions", 0, static (in NativeArgs a) =>
        {
            var df = IntlCore.This<JsDurationFormat>(a, "Intl.DurationFormat.prototype.resolvedOptions");
            var realm = a.Realm;
            var o = new JsObject(realm, realm.ObjectPrototype);
            o.CreateDataProperty("locale", new JsValue(df.Locale));
            o.CreateDataProperty("numberingSystem", new JsValue(df.NumberingSystem));
            o.CreateDataProperty("style", new JsValue(df.Style));
            for (int i = 0; i < 10; i++)
            {
                var st = df.UnitStyle[i] == "fractional" ? "numeric" : df.UnitStyle[i];
                o.CreateDataProperty(s_dfUnits[i], new JsValue(st));
                o.CreateDataProperty(s_dfUnits[i] + "Display", new JsValue(df.UnitDisplay[i]));
            }
            if (df.FractionalDigits is { } fd) o.CreateDataProperty("fractionalDigits", new JsValue((double)fd));
            return JsValue.FromObject(o);
        });
        r.Method(proto, "format", 1, static (in NativeArgs a) =>
        {
            var df = IntlCore.This<JsDurationFormat>(a, "Intl.DurationFormat.prototype.format");
            var record = Temporal.ToTemporalDuration(a.Realm, a[0]).F;
            var sb = new StringBuilder();
            foreach (var part in PartitionDurationFormatPattern(df, record)) sb.Append(part.Value);
            return new JsValue(sb.ToString());
        });
        r.Method(proto, "formatToParts", 1, static (in NativeArgs a) =>
        {
            var df = IntlCore.This<JsDurationFormat>(a, "Intl.DurationFormat.prototype.formatToParts");
            var realm = a.Realm;
            var record = Temporal.ToTemporalDuration(realm, a[0]).F;
            var parts = PartitionDurationFormatPattern(df, record);
            var vals = new JsValue[parts.Count];
            for (int i = 0; i < vals.Length; i++)
            {
                var o = new JsObject(realm, realm.ObjectPrototype);
                o.CreateDataProperty("type", new JsValue(parts[i].Type));
                o.CreateDataProperty("value", new JsValue(parts[i].Value));
                if (parts[i].Unit != null) o.CreateDataProperty("unit", new JsValue(parts[i].Unit!));
                vals[i] = JsValue.FromObject(o);
            }
            return JsValue.FromObject(Operations.CreateArrayFromList(realm, vals));
        });
    }

    /// <summary>The numbering systems a DurationFormat locale supports (default first).</summary>
    private static IReadOnlyList<string> NumberingSystemLocaleData(string locale, string key)
    {
        var list = new List<string>();
        var def = Icu.LocaleDefaultNumberingSystem(IntlCore.ToIcuLocale(locale));
        if (def == null || def is "native" or "traditio" or "finance" || !IntlCore.NumberingSystemDigits.ContainsKey(def)) def = "latn";
        list.Add(def);
        foreach (var n in IntlCore.SupportedNumberingSystems) if (n != def) list.Add(n);
        return list;
    }

    private static void InitializeDurationFormat(Realm realm, JsDurationFormat df, in JsValue locales, in JsValue optionsArg)
    {
        var requested = IntlCore.CanonicalizeLocaleList(realm, locales);
        var options = IntlCore.GetOptionsObject(realm, optionsArg);
        var matcher = IntlCore.GetStringOption(realm, options, "localeMatcher", IntlCore.s_matchers, "best fit");
        var nu = IntlCore.GetStringOption(realm, options, "numberingSystem", null, null);
        if (nu != null && !IntlCore.IsUnicodeType(nu)) throw JsErrors.RangeError(realm, $"Invalid numberingSystem: {nu}");
        var opt = new Dictionary<string, string?>(StringComparer.Ordinal) { ["nu"] = nu?.ToLowerInvariant() };
        var r = IntlCore.ResolveLocale(realm, requested, matcher, opt, s_nuKey, NumberingSystemLocaleData);
        df.Locale = r["locale"]!;
        df.DataLocale = r["dataLocale"]!;
        df.IcuLocale = IntlCore.ToIcuLocale(df.DataLocale);
        df.NumberingSystem = r["nu"] ?? "latn";
        var seps = s_dfSeparators.GetOrAdd(df.DataLocale, static l => ComputeSeparators(l));
        df.HourMinuteSeparator = seps.Hm;
        df.MinuteSecondSeparator = seps.Ms;

        var style = IntlCore.GetStringOption(realm, options, "style", s_dfBaseStyles, "short")!;
        df.Style = style;
        string prevStyle = "";
        for (int i = 0; i < 10; i++)
        {
            var styles = i < 4 ? s_dfDateStyles : i < 7 ? s_dfTimeStyles : s_dfSubsecondStyles;
            var digitalBase = i < 4 ? "short" : "numeric";
            GetDurationUnitOptions(realm, s_dfUnits[i], options, style, styles, digitalBase, prevStyle, false, out var st, out var disp);
            df.UnitStyle[i] = st;
            df.UnitDisplay[i] = disp;
            if (i >= 4 && i <= 8) prevStyle = st;
        }
        df.FractionalDigits = IntlCore.GetNumberOption(realm, options, "fractionalDigits", 0, 9, null);
    }

    /// <summary>The digital-format separators of a locale, from its "Hm" and "ms" time patterns (default ":").</summary>
    private static (string, string) ComputeSeparators(string locale)
    {
        static string Between(string? pattern, char a, char b)
        {
            if (pattern == null) return ":";
            int i = pattern.LastIndexOf(a);
            int j = pattern.IndexOf(b, i + 1);
            if (i < 0 || j < 0) return ":";
            var sep = pattern.Substring(i + 1, j - i - 1).Replace("'", "");
            return sep.Length == 0 ? ":" : sep;
        }
        var icu = IntlCore.ToIcuLocale(locale);
        return (Between(Icu.BestDatePattern(icu, "Hm"), 'H', 'm'), Between(Icu.BestDatePattern(icu, "ms"), 'm', 's'));
    }

    /// <summary>GetDurationUnitOptions + ValidateDurationUnitStyle.</summary>
    private static void GetDurationUnitOptions(Realm realm, string unit, JsObject options, string baseStyle, string[] stylesList, string digitalBase,
        string prevStyle, bool twoDigitHours, out string style, out string display)
    {
        var s = IntlCore.GetStringOption(realm, options, unit, stylesList, null);
        string displayDefault = "always";
        bool fractionalUnit = unit is "milliseconds" or "microseconds" or "nanoseconds";
        if (s == null)
        {
            if (baseStyle == "digital")
            {
                s = digitalBase;
                if (unit is not ("hours" or "minutes" or "seconds")) displayDefault = "auto";
            }
            else if (prevStyle is "fractional" or "numeric" or "2-digit")
            {
                s = "numeric";
                if (unit is not ("minutes" or "seconds")) displayDefault = "auto";
            }
            else
            {
                s = baseStyle;
                displayDefault = "auto";
            }
        }
        if (s == "numeric" && fractionalUnit)
        {
            s = "fractional";
            displayDefault = "auto";
        }
        display = IntlCore.GetStringOption(realm, options, unit + "Display", s_dfDisplays, displayDefault)!;
        if (display == "always" && s == "fractional") throw JsErrors.RangeError(realm, $"{unit}Display cannot be \"always\" when {unit} is fractional");
        if (prevStyle == "fractional" && s != "fractional") throw JsErrors.RangeError(realm, $"{unit} must be numeric after a fractional unit");
        if (prevStyle is "numeric" or "2-digit" && s is not ("fractional" or "numeric" or "2-digit"))
            throw JsErrors.RangeError(realm, $"{unit} style \"{s}\" cannot follow a numeric unit");
        if (unit == "hours" && twoDigitHours) s = "2-digit";
        if (unit is "minutes" or "seconds" && prevStyle is "numeric" or "2-digit") s = "2-digit";
        style = s;
    }

    // ------------------------------------------------------------------ formatting

    private readonly record struct DfPart(string Type, string Value, string? Unit);

    private static int DurationSign(double[] d)
    {
        foreach (var v in d) { if (v < 0) return -1; if (v > 0) return 1; }
        return 0;
    }

    /// <summary>A number to format: a double (possibly -0) or an exact decimal string.</summary>
    private readonly record struct DfNumber(double Value, string? Decimal);

    /// <summary>value + ComputeFractionalDigits(durationFormat, duration) for the unit at index <paramref name="unit"/>, exactly.</summary>
    private static DfNumber WithFraction(JsDurationFormat df, double[] d, int unit)
    {
        BigInteger num = BigInteger.Zero;
        int exponent = 0;
        for (int i = 7; i <= 9; i++)
        {
            if (df.UnitStyle[i] != "fractional") continue;
            exponent += 3;
            num = num * 1000 + new BigInteger(d[i]);   // re-scale previous terms as the exponent grows
        }
        // the loop above accumulates terms of increasing exponent: Σ v_k / 10^(3k) = (Σ v_k · 10^(3(n-k))) / 10^(3n)
        if (exponent == 0 || num.IsZero) return new DfNumber(d[unit], null);
        var total = new BigInteger(d[unit]) * BigInteger.Pow(10, exponent) + num;
        bool neg = total.Sign < 0;
        var digits = BigInteger.Abs(total).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(exponent + 1, '0');
        var str = (neg ? "-" : "") + digits.Substring(0, digits.Length - exponent) + "." + digits.Substring(digits.Length - exponent);
        return new DfNumber(0, str);
    }

    private static string NumberSkeleton(JsDurationFormat df, string? unitStyle, string? nfUnit, bool fraction, bool twoDigit, bool signNever, bool grouping)
    {
        var sb = new StringBuilder();
        sb.Append("numbering-system/").Append(df.NumberingSystem);
        if (nfUnit != null)
        {
            sb.Append(" measure-unit/duration-").Append(nfUnit);
            sb.Append(unitStyle switch { "long" => " unit-width-full-name", "narrow" => " unit-width-narrow", _ => " unit-width-short" });
        }
        if (fraction)
        {
            if (df.FractionalDigits is { } fd) sb.Append(fd == 0 ? " precision-integer" : " ." + new string('0', fd));
            else sb.Append(" .#########");
            sb.Append(" rounding-mode-down");
        }
        else sb.Append(" .### rounding-mode-half-up");
        if (twoDigit) sb.Append(" integer-width/*00");
        if (signNever) sb.Append(" sign-never");
        if (!grouping) sb.Append(" group-off");
        return sb.ToString();
    }

    private static void FormatNumber(JsDurationFormat df, string skeleton, DfNumber n, string unit, List<DfPart> into)
    {
        var f = Icu.NumberFormatter(skeleton, df.IcuLocale, out bool owned);
        var ranges = new List<(string Type, int Start, int End)>();
        string s;
        try { s = Icu.FormatNumberToParts(f, n.Value, n.Decimal, ranges); }
        finally { if (owned) Icu.CloseNumberFormatter(f); }
        foreach (var (type, start, end) in ranges) into.Add(new DfPart(type, s.Substring(start, end - start), unit));
    }

    /// <summary>FormatNumericUnits (hours/minutes/seconds in numeric or 2-digit style).</summary>
    private static List<DfPart> FormatNumericUnits(JsDurationFormat df, double[] d, int firstNumericUnit, bool signDisplayed)
    {
        var result = new List<DfPart>();
        double hoursValue = d[4], minutesValue = d[5];
        var secondsValue = (d[7] != 0 || d[8] != 0 || d[9] != 0) ? WithFraction(df, d, 6) : new DfNumber(d[6], null);
        bool hoursFormatted = firstNumericUnit == 4 && (hoursValue != 0 || df.UnitDisplay[4] == "always");
        bool secondsNonZero = secondsValue.Decimal != null || secondsValue.Value != 0;
        bool secondsFormatted = secondsNonZero || df.UnitDisplay[6] == "always";
        bool minutesFormatted = false;
        if (firstNumericUnit is 4 or 5)
        {
            if (hoursFormatted && secondsFormatted) minutesFormatted = true;
            else if (minutesValue != 0 || df.UnitDisplay[5] == "always") minutesFormatted = true;
        }
        int sign = DurationSign(d);
        if (hoursFormatted)
        {
            if (signDisplayed && hoursValue == 0 && sign == -1) hoursValue = -0.0;
            FormatNumber(df, NumberSkeleton(df, null, null, false, df.UnitStyle[4] == "2-digit", !signDisplayed, false), new DfNumber(hoursValue, null), "hour", result);
            signDisplayed = false;
        }
        if (minutesFormatted)
        {
            if (signDisplayed && minutesValue == 0 && sign == -1) minutesValue = -0.0;
            if (hoursFormatted) result.Add(new DfPart("literal", df.HourMinuteSeparator, null));
            FormatNumber(df, NumberSkeleton(df, null, null, false, df.UnitStyle[5] == "2-digit", !signDisplayed, false), new DfNumber(minutesValue, null), "minute", result);
            signDisplayed = false;
        }
        if (secondsFormatted)
        {
            if (minutesFormatted) result.Add(new DfPart("literal", df.MinuteSecondSeparator, null));
            if (signDisplayed && !secondsNonZero && sign == -1) secondsValue = new DfNumber(-0.0, null);
            FormatNumber(df, NumberSkeleton(df, null, null, true, df.UnitStyle[6] == "2-digit", !signDisplayed, false), secondsValue, "second", result);
        }
        return result;
    }

    /// <summary>PartitionDurationFormatPattern.</summary>
    private static List<DfPart> PartitionDurationFormatPattern(JsDurationFormat df, double[] d)
    {
        var result = new List<List<DfPart>>();
        bool signDisplayed = true;
        int sign = DurationSign(d);
        for (int i = 0; i < 10; i++)
        {
            var style = df.UnitStyle[i];
            if (style is "numeric" or "2-digit")
            {
                var numeric = FormatNumericUnits(df, d, i, signDisplayed);
                if (numeric.Count > 0) result.Add(numeric);
                break;
            }
            bool nextFractional = (i == 6 && df.UnitStyle[7] == "fractional") || (i == 7 && df.UnitStyle[8] == "fractional") || (i == 8 && df.UnitStyle[9] == "fractional");
            var value = nextFractional ? WithFraction(df, d, i) : new DfNumber(d[i], null);
            bool nonZero = value.Decimal != null || value.Value != 0;
            if (df.UnitDisplay[i] == "always" || nonZero)
            {
                bool signNever = false;
                if (signDisplayed)
                {
                    signDisplayed = false;
                    if (!nonZero && sign == -1) value = new DfNumber(-0.0, null);
                }
                else signNever = true;
                var list = new List<DfPart>();
                FormatNumber(df, NumberSkeleton(df, style, s_dfNfUnits[i], nextFractional, false, signNever, true), value, s_dfNfUnits[i], list);
                result.Add(list);
            }
            if (nextFractional) break;
        }
        return ListFormatParts(df, result);
    }

    /// <summary>ListFormatParts.</summary>
    private static List<DfPart> ListFormatParts(JsDurationFormat df, List<List<DfPart>> partitioned)
    {
        var flattened = new List<DfPart>();
        if (partitioned.Count == 0) return flattened;
        var strings = new string[partitioned.Count];
        for (int i = 0; i < strings.Length; i++)
        {
            var sb = new StringBuilder();
            foreach (var p in partitioned[i]) sb.Append(p.Value);
            strings[i] = sb.ToString();
        }
        int width = df.Style switch { "long" => 0, "narrow" => 2, _ => 1 };
        var elements = new List<(int Start, int End)>();
        var s = Icu.FormatUnitList(df.IcuLocale, width, strings, elements);
        int pos = 0, k = 0;
        foreach (var (start, end) in elements)
        {
            if (start > pos) flattened.Add(new DfPart("literal", s.Substring(pos, start - pos), null));
            if (k < partitioned.Count) flattened.AddRange(partitioned[k++]);
            pos = end;
        }
        if (pos < s.Length) flattened.Add(new DfPart("literal", s.Substring(pos), null));
        return flattened;
    }
}
