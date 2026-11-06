using System;
using System.Collections.Generic;

namespace Jaunt.Runtime;

internal static unsafe partial class Icu
{
    private static nint s_rtfOpen, s_rtfClose, s_rtfOpenResult, s_rtfCloseResult, s_rtfFormatNumeric, s_rtfFormat, s_rtfResultAsValue,
        s_rtfUnumOpen, s_rtfUnumSetAttr, s_rtfNsOpen, s_rtfNsClose, s_rtfNsName, s_rtfNsAlgorithmic, s_rtfNsAvailable, s_rtfNsOpenByName;

    public static nint ureldatefmt_close_ptr => P(ref s_rtfClose, "ureldatefmt_close");
    public static nint ureldatefmt_open(byte* loc, nint nf, int width, int capitalization, int* err) =>
        ((delegate* unmanaged<byte*, nint, int, int, int*, nint>)P(ref s_rtfOpen, "ureldatefmt_open"))(loc, nf, width, capitalization, err);
    public static nint ureldatefmt_openResult(int* err) => ((delegate* unmanaged<int*, nint>)P(ref s_rtfOpenResult, "ureldatefmt_openResult"))(err);
    public static void ureldatefmt_closeResult(nint r) => ((delegate* unmanaged<nint, void>)P(ref s_rtfCloseResult, "ureldatefmt_closeResult"))(r);
    public static void ureldatefmt_formatNumericToResult(nint f, double offset, int unit, nint result, int* err) =>
        ((delegate* unmanaged<nint, double, int, nint, int*, void>)P(ref s_rtfFormatNumeric, "ureldatefmt_formatNumericToResult"))(f, offset, unit, result, err);
    public static void ureldatefmt_formatToResult(nint f, double offset, int unit, nint result, int* err) =>
        ((delegate* unmanaged<nint, double, int, nint, int*, void>)P(ref s_rtfFormat, "ureldatefmt_formatToResult"))(f, offset, unit, result, err);
    public static nint ureldatefmt_resultAsValue(nint r, int* err) =>
        ((delegate* unmanaged<nint, int*, nint>)P(ref s_rtfResultAsValue, "ureldatefmt_resultAsValue"))(r, err);

    public static nint unum_open_decimal(byte* loc, int* err) =>
        ((delegate* unmanaged<int, char*, int, byte*, void*, int*, nint>)P(ref s_rtfUnumOpen, "unum_open"))(1 /* UNUM_DECIMAL */, null, 0, loc, null, err);
    public static void unum_setAttribute(nint f, int attr, int value) =>
        ((delegate* unmanaged<nint, int, int, void>)P(ref s_rtfUnumSetAttr, "unum_setAttribute"))(f, attr, value);

    /// <summary>The default numbering system of an ICU locale ("latn" when unknown).</summary>
    public static string DefaultNumberingSystem(string icuLocale)
    {
        var loc = Utf8Z(icuLocale);
        int err = 0;
        nint ns;
        fixed (byte* lp = loc) ns = ((delegate* unmanaged<byte*, int*, nint>)P(ref s_rtfNsOpen, "unumsys_open"))(lp, &err);
        if (ns == 0 || Failure(err)) return "latn";
        try { return FromCString(((delegate* unmanaged<nint, byte*>)P(ref s_rtfNsName, "unumsys_getName"))(ns)) ?? "latn"; }
        finally { ((delegate* unmanaged<nint, void>)P(ref s_rtfNsClose, "unumsys_close"))(ns); }
    }

    /// <summary>The names of the non-algorithmic (digit-mapping) numbering systems ICU supports.</summary>
    public static List<string> DigitNumberingSystems()
    {
        int err = 0;
        var names = ReadEnum(((delegate* unmanaged<int*, nint>)P(ref s_rtfNsAvailable, "unumsys_openAvailableNames"))(&err));
        var result = new List<string>();
        var openByName = (delegate* unmanaged<byte*, int*, nint>)P(ref s_rtfNsOpenByName, "unumsys_openByName");
        var isAlg = (delegate* unmanaged<nint, sbyte>)P(ref s_rtfNsAlgorithmic, "unumsys_isAlgorithmic");
        var close = (delegate* unmanaged<nint, void>)P(ref s_rtfNsClose, "unumsys_close");
        foreach (var n in names)
        {
            if (n is "native" or "traditio" or "finance") continue;
            var b = Utf8Z(n);
            int e = 0;
            nint ns;
            fixed (byte* bp = b) ns = openByName(bp, &e);
            if (ns == 0 || Failure(e)) continue;
            if (isAlg(ns) == 0) result.Add(n);
            close(ns);
        }
        return result;
    }
}

/// <summary>An Intl.RelativeTimeFormat instance ([[InitializedRelativeTimeFormat]]).</summary>
internal sealed class JsRelativeTimeFormat : JsObject
{
    public string Locale = "";
    public string DataLocale = "";
    public string NumberingSystem = "latn";
    public string Style = "long";
    public string Numeric = "always";
    public IcuTextHandle Handle = null!;
    public JsRelativeTimeFormat(Realm realm, JsObject proto) : base(realm, proto) { }
}

/// <summary>Intl.RelativeTimeFormat (ECMA-402 §17) on ICU's ureldatefmt API with an adopted decimal formatter.</summary>
internal static class IntlRelativeTimeFormat
{
    private static readonly string[] s_styles = { "long", "short", "narrow" };
    private static readonly string[] s_numerics = { "always", "auto" };
    private static readonly string[] s_keys = { "nu" };
    private static readonly Dictionary<string, IcuTextHandle> s_cache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IReadOnlyList<string>> s_nuData = new(StringComparer.Ordinal);
    private static List<string>? s_digitSystems;

    private static IReadOnlyList<string> LocaleData(string locale, string key)
    {
        if (key != "nu") return IntlText.NoData;
        lock (s_nuData)
        {
            if (s_nuData.TryGetValue(locale, out var d)) return d;
            s_digitSystems ??= Icu.DigitNumberingSystems();
            var def = Icu.DefaultNumberingSystem(IntlCore.ToIcuLocale(locale));
            var list = new List<string> { def };
            foreach (var n in s_digitSystems) if (n != def) list.Add(n);
            s_nuData[locale] = list;
            return list;
        }
    }

    private static unsafe IcuTextHandle Formatter(Realm realm, string dataLocale, string nu, string style)
    {
        var icuLocale = IntlCore.ToIcuLocale(dataLocale);
        int at = icuLocale.IndexOf('@');
        if (at >= 0) icuLocale = icuLocale.Substring(0, at);
        icuLocale += "@numbers=" + nu;
        var key = icuLocale + "|" + style;
        lock (s_cache)
            if (s_cache.TryGetValue(key, out var h)) return h;
        var loc = Icu.Utf8Z(icuLocale);
        int err = 0;
        nint nf, f;
        fixed (byte* lp = loc)
        {
            nf = Icu.unum_open_decimal(lp, &err);
            if (nf == 0 || Icu.Failure(err)) throw JsErrors.RangeError(realm, "Unable to create a number formatter for " + dataLocale);
            // Intl.NumberFormat defaults: 0..3 fraction digits, grouping, half-expand rounding
            Icu.unum_setAttribute(nf, 7 /* UNUM_MIN_FRACTION_DIGITS */, 0);
            Icu.unum_setAttribute(nf, 6 /* UNUM_MAX_FRACTION_DIGITS */, 3);
            Icu.unum_setAttribute(nf, 11 /* UNUM_ROUNDING_MODE */, 6 /* UNUM_ROUND_HALFUP */);
            int width = style switch { "short" => 1, "narrow" => 2, _ => 0 };
            f = Icu.ureldatefmt_open(lp, nf, width, 256 /* UDISPCTX_CAPITALIZATION_NONE */, &err);   // adopts nf
        }
        if (f == 0 || Icu.Failure(err)) throw JsErrors.RangeError(realm, "Unable to create a relative time formatter for " + dataLocale);
        var handle = new IcuTextHandle(f, Icu.ureldatefmt_close_ptr);
        lock (s_cache)
        {
            if (s_cache.TryGetValue(key, out var existing)) return existing;
            if (s_cache.Count < IntlText.CacheLimit) s_cache[key] = handle.Pin();
        }
        return handle;
    }

    /// <summary>SingularRelativeTimeUnit (17.5.1) → UDateRelativeDateTimeFormatterUnit and the singular name.</summary>
    private static (int Unit, string Name) SingularUnit(Realm realm, string unit) => unit switch
    {
        "second" or "seconds" => (7, "second"),
        "minute" or "minutes" => (6, "minute"),
        "hour" or "hours" => (5, "hour"),
        "day" or "days" => (4, "day"),
        "week" or "weeks" => (3, "week"),
        "month" or "months" => (2, "month"),
        "quarter" or "quarters" => (1, "quarter"),
        "year" or "years" => (0, "year"),
        _ => throw JsErrors.RangeError(realm, $"Invalid unit argument: {unit}"),
    };

    private static unsafe string Format(Realm realm, JsRelativeTimeFormat rtf, in JsValue valueArg, in JsValue unitArg, List<Icu.FieldSpan>? spans, out string unitName)
    {
        double value = Operations.ToNumber(realm, valueArg);
        var unitStr = Operations.ToString(realm, unitArg);
        if (double.IsNaN(value) || double.IsInfinity(value)) throw JsErrors.RangeError(realm, "Invalid value: must be a finite number");
        var (unit, name) = SingularUnit(realm, unitStr);
        unitName = name;
        int err = 0;
        nint result = Icu.ureldatefmt_openResult(&err);
        if (Icu.Failure(err)) throw JsErrors.RangeError(realm, "Relative time formatting failed");
        try
        {
            var h = rtf.Handle;
            lock (h)
            {
                if (rtf.Numeric == "auto") Icu.ureldatefmt_formatToResult(h.Ptr, value, unit, result, &err);
                else Icu.ureldatefmt_formatNumericToResult(h.Ptr, value, unit, result, &err);
            }
            GC.KeepAlive(h);
            if (Icu.Failure(err)) throw JsErrors.RangeError(realm, "Relative time formatting failed");
            var fv = Icu.ureldatefmt_resultAsValue(result, &err);
            return Icu.ReadFormattedValue(fv, spans);
        }
        finally { Icu.ureldatefmt_closeResult(result); }
    }

    private static string? NumberPartType(int field) => field switch
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
        10 => "minusSign",
        11 => "unit",
        12 => "compact",
        13 => "approximatelySign",
        _ => null,
    };

    private static JsValue FormatToParts(Realm realm, JsRelativeTimeFormat rtf, in JsValue value, in JsValue unit)
    {
        var spans = new List<Icu.FieldSpan>();
        var text = Format(realm, rtf, value, unit, spans, out var unitName);
        var types = new string?[text.Length];
        // the integer span covers its grouping separators: assign wider spans first, nested ones override
        bool anyNumber = false;
        foreach (var s in spans)
        {
            if (s.Category != Icu.UFIELD_CATEGORY_NUMBER || s.Field != 0) continue;
            for (int i = s.Start; i < s.Limit; i++) types[i] = "integer";
            anyNumber = true;
        }
        foreach (var s in spans)
        {
            if (s.Category != Icu.UFIELD_CATEGORY_NUMBER || s.Field == 0) continue;
            var t = NumberPartType(s.Field);
            if (t == null) continue;
            for (int i = s.Start; i < s.Limit; i++) types[i] = t;
            anyNumber = true;
        }
        if (!anyNumber)
            foreach (var s in spans)
                if (s.Category == Icu.UFIELD_CATEGORY_RELATIVE_DATETIME && s.Field == 1)
                    for (int i = s.Start; i < s.Limit; i++) types[i] = "integer";
        var parts = new List<JsValue>();
        int pos = 0;
        while (pos < text.Length)
        {
            var t = types[pos];
            int end = pos + 1;
            while (end < text.Length && types[end] == t) end++;
            var o = new JsObject(realm, realm.ObjectPrototype);
            IntlText.Data(o, "type", new JsValue(t ?? "literal"));
            IntlText.Data(o, "value", new JsValue(text.Substring(pos, end - pos)));
            if (t != null) IntlText.Data(o, "unit", new JsValue(unitName));
            parts.Add(JsValue.FromObject(o));
            pos = end;
        }
        return JsValue.FromObject(Operations.CreateArrayFromList(realm, parts.ToArray()));
    }

    internal static void Setup(Realm r, JsObject intl, IntlProtos protos)
    {
        var proto = IntlText.Proto(r, "Intl.RelativeTimeFormat");
        protos.RelativeTimeFormatPrototype = proto;
        var ctor = r.Constructor("RelativeTimeFormat", 0, static (in NativeArgs a) =>
        {
            IntlText.RequireNew(a, "RelativeTimeFormat");
            var realm = a.Realm;
            var proto = IntlText.ProtoFromNewTarget(a, static p => p.RelativeTimeFormatPrototype);
            var requested = IntlCore.CanonicalizeLocaleList(realm, a[0]);
            var options = IntlCore.CoerceOptionsToObject(realm, a[1]);
            var matcher = IntlCore.GetStringOption(realm, options, "localeMatcher", IntlCore.s_matchers, "best fit");
            var nu = IntlCore.GetStringOption(realm, options, "numberingSystem", null, null);
            if (nu != null && !IntlCore.IsUnicodeType(nu)) throw JsErrors.RangeError(realm, $"Invalid numberingSystem: {nu}");
            var opt = new Dictionary<string, string?>(StringComparer.Ordinal) { ["nu"] = nu };
            var res = IntlCore.ResolveLocale(realm, requested, matcher, opt, s_keys, LocaleData);
            var rtf = new JsRelativeTimeFormat(realm, proto)
            {
                Locale = res["locale"]!,
                DataLocale = res["dataLocale"]!,
                NumberingSystem = res["nu"] ?? "latn",
            };
            rtf.Style = IntlCore.GetStringOption(realm, options, "style", s_styles, "long")!;
            rtf.Numeric = IntlCore.GetStringOption(realm, options, "numeric", s_numerics, "always")!;
            rtf.Handle = Formatter(realm, rtf.DataLocale, rtf.NumberingSystem, rtf.Style);
            return JsValue.FromObject(rtf);
        }, proto);
        intl.DefineHidden("RelativeTimeFormat", JsValue.FromObject(ctor));
        r.Method(ctor, "supportedLocalesOf", 1, static (in NativeArgs a) => IntlCore.SupportedLocalesOf(a.Realm, a[0], a[1]));
        r.Method(proto, "format", 2, static (in NativeArgs a) =>
        {
            var rtf = IntlCore.This<JsRelativeTimeFormat>(a, "Intl.RelativeTimeFormat.prototype.format");
            return new JsValue(Format(a.Realm, rtf, a[0], a[1], null, out _));
        });
        r.Method(proto, "formatToParts", 2, static (in NativeArgs a) =>
        {
            var rtf = IntlCore.This<JsRelativeTimeFormat>(a, "Intl.RelativeTimeFormat.prototype.formatToParts");
            return FormatToParts(a.Realm, rtf, a[0], a[1]);
        });
        r.Method(proto, "resolvedOptions", 0, static (in NativeArgs a) =>
        {
            var rtf = IntlCore.This<JsRelativeTimeFormat>(a, "Intl.RelativeTimeFormat.prototype.resolvedOptions");
            var o = new JsObject(a.Realm, a.Realm.ObjectPrototype);
            IntlText.Data(o, "locale", new JsValue(rtf.Locale));
            IntlText.Data(o, "style", new JsValue(rtf.Style));
            IntlText.Data(o, "numeric", new JsValue(rtf.Numeric));
            IntlText.Data(o, "numberingSystem", new JsValue(rtf.NumberingSystem));
            return JsValue.FromObject(o);
        });
    }
}

internal sealed partial class IntlProtos
{
    public JsObject RelativeTimeFormatPrototype = null!;
}

internal static partial class IntlBuiltins
{
    static partial void SetupRelativeTimeFormat(Realm r, JsObject intl, IntlProtos protos) => IntlRelativeTimeFormat.Setup(r, intl, protos);
}
