using System;
using System.Collections.Generic;

namespace Jaunt.Runtime;

internal static unsafe partial class Icu
{
    private static nint s_ulistOpen, s_ulistClose, s_ulistOpenResult, s_ulistCloseResult, s_ulistFormatToResult, s_ulistResultAsValue,
        s_ufmtvalGetString, s_ufmtvalNextPosition, s_ucfposOpen, s_ucfposClose, s_ucfposGetCategory, s_ucfposGetField, s_ucfposGetIndexes;

    public const int UFIELD_CATEGORY_NUMBER = 2, UFIELD_CATEGORY_LIST = 3, UFIELD_CATEGORY_RELATIVE_DATETIME = 4;

    public static nint ulistfmt_close_ptr => P(ref s_ulistClose, "ulistfmt_close");
    public static nint ulistfmt_openForType(byte* loc, int type, int width, int* err) =>
        ((delegate* unmanaged<byte*, int, int, int*, nint>)P(ref s_ulistOpen, "ulistfmt_openForType"))(loc, type, width, err);
    public static nint ulistfmt_openResult(int* err) => ((delegate* unmanaged<int*, nint>)P(ref s_ulistOpenResult, "ulistfmt_openResult"))(err);
    public static void ulistfmt_closeResult(nint r) => ((delegate* unmanaged<nint, void>)P(ref s_ulistCloseResult, "ulistfmt_closeResult"))(r);
    public static void ulistfmt_formatStringsToResult(nint fmt, char** strings, int* lens, int count, nint result, int* err) =>
        ((delegate* unmanaged<nint, char**, int*, int, nint, int*, void>)P(ref s_ulistFormatToResult, "ulistfmt_formatStringsToResult"))(fmt, strings, lens, count, result, err);
    public static nint ulistfmt_resultAsValue(nint r, int* err) =>
        ((delegate* unmanaged<nint, int*, nint>)P(ref s_ulistResultAsValue, "ulistfmt_resultAsValue"))(r, err);

    /// <summary>A field span of a UFormattedValue.</summary>
    public readonly record struct FieldSpan(int Category, int Field, int Start, int Limit);

    /// <summary>Reads the string of a UFormattedValue and (optionally) all of its field positions.</summary>
    public static string ReadFormattedValue(nint fv, List<FieldSpan>? spans)
    {
        int err = 0, len = 0;
        var p = ((delegate* unmanaged<nint, int*, int*, char*>)P(ref s_ufmtvalGetString, "ufmtval_getString"))(fv, &len, &err);
        if (Failure(err) || p == null) return "";
        var text = new string(p, 0, len);
        if (spans == null) return text;
        var pos = ((delegate* unmanaged<int*, nint>)P(ref s_ucfposOpen, "ucfpos_open"))(&err);
        if (Failure(err)) return text;
        try
        {
            var next = (delegate* unmanaged<nint, nint, int*, sbyte>)P(ref s_ufmtvalNextPosition, "ufmtval_nextPosition");
            var getCat = (delegate* unmanaged<nint, int*, int>)P(ref s_ucfposGetCategory, "ucfpos_getCategory");
            var getField = (delegate* unmanaged<nint, int*, int>)P(ref s_ucfposGetField, "ucfpos_getField");
            var getIdx = (delegate* unmanaged<nint, int*, int*, int*, void>)P(ref s_ucfposGetIndexes, "ucfpos_getIndexes");
            while (next(fv, pos, &err) != 0 && !Failure(err))
            {
                int s = 0, l = 0;
                getIdx(pos, &s, &l, &err);
                spans.Add(new FieldSpan(getCat(pos, &err), getField(pos, &err), s, l));
            }
        }
        finally { ((delegate* unmanaged<nint, void>)P(ref s_ucfposClose, "ucfpos_close"))(pos); }
        return text;
    }
}

/// <summary>An Intl.ListFormat instance ([[InitializedListFormat]]).</summary>
internal sealed class JsListFormat : JsObject
{
    public string Locale = "";
    public string DataLocale = "";
    public string Type = "conjunction";
    public string Style = "long";
    public IcuTextHandle? Handle;
    public JsListFormat(Realm realm, JsObject proto) : base(realm, proto) { }
}

/// <summary>
/// Intl.ListFormat (ECMA-402 §13) on ICU's ulistfmt API. Other services can format string lists with
/// <see cref="FormatList"/> / <see cref="FormatListToParts"/>.
/// </summary>
internal static class IntlListFormat
{
    private static readonly string[] s_types = { "conjunction", "disjunction", "unit" };
    private static readonly string[] s_styles = { "long", "short", "narrow" };
    private static readonly Dictionary<string, IcuTextHandle> s_cache = new(StringComparer.Ordinal);

    /// <summary>The (shared or owned) ICU list formatter for a locale (BCP 47), type and style.</summary>
    private static unsafe IcuTextHandle Formatter(Realm realm, string locale, string type, string style)
    {
        var icuLocale = IntlCore.ToIcuLocale(locale);
        var key = $"{icuLocale}|{type}|{style}";
        lock (s_cache)
            if (s_cache.TryGetValue(key, out var h)) return h;
        int t = type switch { "disjunction" => 1, "unit" => 2, _ => 0 };
        int w = style switch { "short" => 1, "narrow" => 2, _ => 0 };
        var loc = Icu.Utf8Z(icuLocale);
        int err = 0;
        nint fmt;
        fixed (byte* lp = loc) fmt = Icu.ulistfmt_openForType(lp, t, w, &err);
        if (fmt == 0 || Icu.Failure(err)) throw JsErrors.RangeError(realm, "Unable to create a list formatter for " + locale);
        var handle = new IcuTextHandle(fmt, Icu.ulistfmt_close_ptr);
        lock (s_cache)
        {
            if (s_cache.TryGetValue(key, out var existing)) return existing;
            if (s_cache.Count < IntlText.CacheLimit) s_cache[key] = handle.Pin();
        }
        return handle;
    }

    private static unsafe string Format(Realm realm, IcuTextHandle fmt, IReadOnlyList<string> list, List<Icu.FieldSpan>? spans)
    {
        int n = list.Count;
        int total = 0;
        for (int i = 0; i < n; i++) total += list[i].Length;
        var buf = new char[Math.Max(1, total)];
        var lens = new int[Math.Max(1, n)];
        var ptrs = new nint[Math.Max(1, n)];
        int off = 0;
        for (int i = 0; i < n; i++)
        {
            list[i].CopyTo(0, buf, off, list[i].Length);
            lens[i] = list[i].Length;
            off += list[i].Length;
        }
        int err = 0;
        nint result = Icu.ulistfmt_openResult(&err);
        if (Icu.Failure(err)) throw JsErrors.RangeError(realm, "List formatting failed");
        try
        {
            fixed (char* bp = buf) fixed (int* lp = lens) fixed (nint* pp = ptrs)
            {
                off = 0;
                for (int i = 0; i < n; i++) { pp[i] = (nint)(bp + off); off += lens[i]; }
                lock (fmt) Icu.ulistfmt_formatStringsToResult(fmt.Ptr, (char**)pp, lp, n, result, &err);
            }
            GC.KeepAlive(fmt);
            if (Icu.Failure(err)) throw JsErrors.RangeError(realm, "List formatting failed");
            var fv = Icu.ulistfmt_resultAsValue(result, &err);
            return Icu.ReadFormattedValue(fv, spans);
        }
        finally { Icu.ulistfmt_closeResult(result); }
    }

    /// <summary>
    /// FormatList: joins <paramref name="list"/> per the list patterns of <paramref name="locale"/> (a canonical BCP 47 tag),
    /// <paramref name="type"/> ("conjunction" / "disjunction" / "unit") and <paramref name="style"/> ("long" / "short" / "narrow").
    /// </summary>
    public static string FormatList(Realm realm, string locale, string type, string style, IReadOnlyList<string> list) =>
        Format(realm, Formatter(realm, locale, type, style), list, null);

    /// <summary>FormatListToParts: like <see cref="FormatList"/> but as ("element" | "literal", value) parts.</summary>
    public static List<(string Type, string Value)> FormatListToParts(Realm realm, string locale, string type, string style, IReadOnlyList<string> list) =>
        ToParts(realm, Formatter(realm, locale, type, style), list);

    private static List<(string Type, string Value)> ToParts(Realm realm, IcuTextHandle fmt, IReadOnlyList<string> list)
    {
        var spans = new List<Icu.FieldSpan>();
        var text = Format(realm, fmt, list, spans);
        var elements = new List<(int Start, int Limit)>();
        foreach (var s in spans)
            if (s.Category == Icu.UFIELD_CATEGORY_LIST && s.Field == 1) elements.Add((s.Start, s.Limit));
        elements.Sort();
        var parts = new List<(string, string)>();
        int pos = 0;
        foreach (var (start, limit) in elements)
        {
            if (start > pos) parts.Add(("literal", text.Substring(pos, start - pos)));
            parts.Add(("element", text.Substring(start, limit - start)));
            pos = limit;
        }
        if (pos < text.Length) parts.Add(("literal", text.Substring(pos)));
        return parts;
    }

    /// <summary>StringListFromIterable (13.5.3).</summary>
    private static List<string> StringListFromIterable(Realm realm, in JsValue iterable)
    {
        var list = new List<string>();
        if (iterable.IsUndefined) return list;
        var rec = IteratorRecord.Get(realm, iterable);
        while (rec.Step(out var v))
        {
            if (!v.IsString)
            {
                rec.CloseQuiet();
                throw JsErrors.TypeError(realm, "Iterable yielded a non-string value");
            }
            list.Add(v.AsStringUnchecked);
        }
        return list;
    }

    private static IcuTextHandle HandleOf(Realm realm, JsListFormat lf) => lf.Handle ??= Formatter(realm, lf.DataLocale, lf.Type, lf.Style);

    internal static void Setup(Realm r, JsObject intl, IntlProtos protos)
    {
        var proto = IntlText.Proto(r, "Intl.ListFormat");
        protos.ListFormatPrototype = proto;
        var ctor = r.Constructor("ListFormat", 0, static (in NativeArgs a) =>
        {
            IntlText.RequireNew(a, "ListFormat");
            var realm = a.Realm;
            var proto = IntlText.ProtoFromNewTarget(a, static p => p.ListFormatPrototype);
            var requested = IntlCore.CanonicalizeLocaleList(realm, a[0]);
            var options = IntlCore.GetOptionsObject(realm, a[1]);
            var matcher = IntlCore.GetStringOption(realm, options, "localeMatcher", IntlCore.s_matchers, "best fit");
            var res = IntlCore.ResolveLocale(realm, requested, matcher, new Dictionary<string, string?>(), Array.Empty<string>(), static (_, _) => IntlText.NoData);
            var lf = new JsListFormat(realm, proto) { Locale = res["locale"]!, DataLocale = res["dataLocale"]! };
            lf.Type = IntlCore.GetStringOption(realm, options, "type", s_types, "conjunction")!;
            lf.Style = IntlCore.GetStringOption(realm, options, "style", s_styles, "long")!;
            HandleOf(realm, lf);
            return JsValue.FromObject(lf);
        }, proto);
        intl.DefineHidden("ListFormat", JsValue.FromObject(ctor));
        r.Method(ctor, "supportedLocalesOf", 1, static (in NativeArgs a) => IntlCore.SupportedLocalesOf(a.Realm, a[0], a[1]));
        r.Method(proto, "format", 1, static (in NativeArgs a) =>
        {
            var lf = IntlCore.This<JsListFormat>(a, "Intl.ListFormat.prototype.format");
            var list = StringListFromIterable(a.Realm, a[0]);
            return new JsValue(Format(a.Realm, HandleOf(a.Realm, lf), list, null));
        });
        r.Method(proto, "formatToParts", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var lf = IntlCore.This<JsListFormat>(a, "Intl.ListFormat.prototype.formatToParts");
            var list = StringListFromIterable(realm, a[0]);
            var parts = ToParts(realm, HandleOf(realm, lf), list);
            var arr = new JsValue[parts.Count];
            for (int i = 0; i < parts.Count; i++)
            {
                var o = new JsObject(realm, realm.ObjectPrototype);
                IntlText.Data(o, "type", new JsValue(parts[i].Type));
                IntlText.Data(o, "value", new JsValue(parts[i].Value));
                arr[i] = JsValue.FromObject(o);
            }
            return JsValue.FromObject(Operations.CreateArrayFromList(realm, arr));
        });
        r.Method(proto, "resolvedOptions", 0, static (in NativeArgs a) =>
        {
            var lf = IntlCore.This<JsListFormat>(a, "Intl.ListFormat.prototype.resolvedOptions");
            var o = new JsObject(a.Realm, a.Realm.ObjectPrototype);
            IntlText.Data(o, "locale", new JsValue(lf.Locale));
            IntlText.Data(o, "type", new JsValue(lf.Type));
            IntlText.Data(o, "style", new JsValue(lf.Style));
            return JsValue.FromObject(o);
        });
    }
}

internal sealed partial class IntlProtos
{
    public JsObject ListFormatPrototype = null!;
}

internal static partial class IntlBuiltins
{
    static partial void SetupListFormat(Realm r, JsObject intl, IntlProtos protos) => IntlListFormat.Setup(r, intl, protos);
}
