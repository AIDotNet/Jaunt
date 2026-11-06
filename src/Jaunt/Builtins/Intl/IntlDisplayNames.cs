using System;
using System.Collections.Generic;

namespace Jaunt.Runtime;

internal static unsafe partial class Icu
{
    private static nint s_uldnOpenCtx, s_uldnClose, s_uldnLocale, s_uldnRegion, s_uldnScript, s_uldnKeyValue, s_ucurrGetName,
        s_udatpgOpen, s_udatpgClose, s_udatpgFieldName;

    public const int U_USING_DEFAULT_WARNING = -127;

    public static nint uldn_close_ptr => P(ref s_uldnClose, "uldn_close");
    public static nint uldn_openForContext(byte* loc, int* contexts, int length, int* err) =>
        ((delegate* unmanaged<byte*, int*, int, int*, nint>)P(ref s_uldnOpenCtx, "uldn_openForContext"))(loc, contexts, length, err);
    public static nint uldn_localeDisplayName_ptr => P(ref s_uldnLocale, "uldn_localeDisplayName");
    public static nint uldn_regionDisplayName_ptr => P(ref s_uldnRegion, "uldn_regionDisplayName");
    public static nint uldn_scriptDisplayName_ptr => P(ref s_uldnScript, "uldn_scriptDisplayName");
    public static nint uldn_keyValueDisplayName_ptr => P(ref s_uldnKeyValue, "uldn_keyValueDisplayName");
    public static char* ucurr_getName(char* currency, byte* loc, int style, sbyte* isChoice, int* len, int* err) =>
        ((delegate* unmanaged<char*, byte*, int, sbyte*, int*, int*, char*>)P(ref s_ucurrGetName, "ucurr_getName"))(currency, loc, style, isChoice, len, err);
    public static nint udatpg_close_ptr => P(ref s_udatpgClose, "udatpg_close");
    public static nint udatpg_open(byte* loc, int* err) => ((delegate* unmanaged<byte*, int*, nint>)P(ref s_udatpgOpen, "udatpg_open"))(loc, err);
    public static int udatpg_getFieldDisplayName(nint g, int field, int width, char* buf, int cap, int* err) =>
        ((delegate* unmanaged<nint, int, int, char*, int, int*, int>)P(ref s_udatpgFieldName, "udatpg_getFieldDisplayName"))(g, field, width, buf, cap, err);
}

/// <summary>An Intl.DisplayNames instance ([[InitializedDisplayNames]]).</summary>
internal sealed class JsDisplayNames : JsObject
{
    public string Locale = "";
    public string DataLocale = "";
    public string Style = "long";
    public string Type = "";
    public string Fallback = "code";
    public string? LanguageDisplay;
    public JsDisplayNames(Realm realm, JsObject proto) : base(realm, proto) { }
}

/// <summary>Intl.DisplayNames (ECMA-402 §12) on ICU's uldn, ucurr and udatpg APIs.</summary>
internal static class IntlDisplayNames
{
    private static readonly string[] s_styles = { "narrow", "short", "long" };
    private static readonly string[] s_types = { "language", "region", "script", "currency", "calendar", "dateTimeField" };
    private static readonly string[] s_fallbacks = { "code", "none" };
    private static readonly string[] s_languageDisplays = { "dialect", "standard" };
    private static readonly Dictionary<string, int> s_fields = new(StringComparer.Ordinal)
    {
        ["era"] = 0, ["year"] = 1, ["quarter"] = 2, ["month"] = 3, ["weekOfYear"] = 4, ["weekday"] = 6, ["day"] = 9,
        ["dayPeriod"] = 10, ["hour"] = 11, ["minute"] = 12, ["second"] = 13, ["timeZoneName"] = 15,
    };
    private static readonly Dictionary<string, IcuTextHandle> s_cache = new(StringComparer.Ordinal);

    private static unsafe IcuTextHandle? LocaleDisplayNames(string locale, bool shortNames, bool dialect)
    {
        var icuLocale = IntlCore.ToIcuLocale(locale);
        var key = $"ldn|{icuLocale}|{shortNames}|{dialect}";
        lock (s_cache)
            if (s_cache.TryGetValue(key, out var h)) return h;
        var ctx = stackalloc int[4];
        ctx[0] = dialect ? 1 : 0;                 // UDISPCTX_DIALECT_NAMES / UDISPCTX_STANDARD_NAMES
        ctx[1] = shortNames ? 513 : 512;          // UDISPCTX_LENGTH_SHORT / UDISPCTX_LENGTH_FULL
        ctx[2] = 769;                             // UDISPCTX_NO_SUBSTITUTE
        ctx[3] = 256;                             // UDISPCTX_CAPITALIZATION_NONE
        var loc = Icu.Utf8Z(icuLocale);
        int err = 0;
        nint ldn;
        fixed (byte* lp = loc) ldn = Icu.uldn_openForContext(lp, ctx, 4, &err);
        if (ldn == 0 || Icu.Failure(err)) return null;
        var handle = new IcuTextHandle(ldn, Icu.uldn_close_ptr);
        lock (s_cache)
        {
            if (s_cache.TryGetValue(key, out var existing)) return existing;
            if (s_cache.Count < IntlText.CacheLimit) s_cache[key] = handle.Pin();
        }
        return handle;
    }

    private static unsafe IcuTextHandle? PatternGenerator(string locale)
    {
        var icuLocale = IntlCore.ToIcuLocale(locale);
        var key = "dtpg|" + icuLocale;
        lock (s_cache)
            if (s_cache.TryGetValue(key, out var h)) return h;
        var loc = Icu.Utf8Z(icuLocale);
        int err = 0;
        nint g;
        fixed (byte* lp = loc) g = Icu.udatpg_open(lp, &err);
        if (g == 0 || Icu.Failure(err)) return null;
        var handle = new IcuTextHandle(g, Icu.udatpg_close_ptr);
        lock (s_cache)
        {
            if (s_cache.TryGetValue(key, out var existing)) return existing;
            if (s_cache.Count < IntlText.CacheLimit) s_cache[key] = handle.Pin();
        }
        return handle;
    }

    /// <summary>Calls a uldn_xxxDisplayName(ldn, const char* code, UChar* buf, int cap, UErrorCode*) function.</summary>
    private static unsafe string? LdnName(IcuTextHandle? ldn, nint fn, string code)
    {
        if (ldn == null) return null;
        var c = Icu.Utf8Z(code);
        string? r;
        lock (ldn)
        {
            r = Icu.CallUChars((buf, cap, err) =>
            {
                fixed (byte* cp = c) return ((delegate* unmanaged<nint, byte*, char*, int, int*, int>)fn)(ldn.Ptr, cp, buf, cap, err);
            });
        }
        GC.KeepAlive(ldn);
        return string.IsNullOrEmpty(r) ? null : r;
    }

    private static unsafe string? KeyValueName(IcuTextHandle? ldn, string key, string value)
    {
        if (ldn == null) return null;
        var k = Icu.Utf8Z(key);
        var v = Icu.Utf8Z(value);
        var fn = (delegate* unmanaged<nint, byte*, byte*, char*, int, int*, int>)Icu.uldn_keyValueDisplayName_ptr;
        string? r;
        lock (ldn)
        {
            r = Icu.CallUChars((buf, cap, err) =>
            {
                fixed (byte* kp = k) fixed (byte* vp = v) return fn(ldn.Ptr, kp, vp, buf, cap, err);
            });
        }
        GC.KeepAlive(ldn);
        return string.IsNullOrEmpty(r) ? null : r;
    }

    internal static unsafe string? CurrencyName(string locale, string code, string style)
    {
        var loc = Icu.Utf8Z(IntlCore.ToIcuLocale(locale));
        int nameStyle = style switch { "short" => 0, "narrow" => 2, _ => 1 };   // UCURR_SYMBOL_NAME, UCURR_NARROW_SYMBOL_NAME, UCURR_LONG_NAME
        int err = 0, len = 0;
        sbyte isChoice = 0;
        char* p;
        fixed (char* cp = code) fixed (byte* lp = loc) p = Icu.ucurr_getName(cp, lp, nameStyle, &isChoice, &len, &err);
        if (p == null || Icu.Failure(err) || err == Icu.U_USING_DEFAULT_WARNING) return null;
        return new string(p, 0, len);
    }

    private static unsafe string? FieldName(string locale, int field, string style)
    {
        var g = PatternGenerator(locale);
        if (g == null) return null;
        int width = style switch { "short" => 1, "narrow" => 2, _ => 0 };
        string? r;
        lock (g) r = Icu.CallUChars((buf, cap, err) => Icu.udatpg_getFieldDisplayName(g.Ptr, field, width, buf, cap, err));
        GC.KeepAlive(g);
        return string.IsNullOrEmpty(r) ? null : r;
    }

    /// <summary>CanonicalCodeForDisplayNames (12.5.1).</summary>
    private static string CanonicalCode(Realm realm, string type, string code)
    {
        switch (type)
        {
            case "language":
                var t = IntlCore.Parse(code);
                if (t == null || t.OtherExtensions.Count > 0 || t.UKeywords.Count > 0 || t.UAttributes.Count > 0 || t.PrivateUse != null)
                    throw JsErrors.RangeError(realm, $"Invalid language code: {code}");
                return IntlCore.Canonicalize(code);
            case "region":
                if (!IntlCore.IsRegionSubtag(code)) throw JsErrors.RangeError(realm, $"Invalid region code: {code}");
                return code.ToUpperInvariant();
            case "script":
                if (!IntlCore.IsScriptSubtag(code)) throw JsErrors.RangeError(realm, $"Invalid script code: {code}");
                return char.ToUpperInvariant(code[0]) + code.Substring(1).ToLowerInvariant();
            case "calendar":
                if (!IntlCore.IsUnicodeType(code)) throw JsErrors.RangeError(realm, $"Invalid calendar code: {code}");
                return code.ToLowerInvariant();
            case "dateTimeField":
                if (!s_fields.ContainsKey(code)) throw JsErrors.RangeError(realm, $"Invalid dateTimeField code: {code}");
                return code;
            default: // currency
                if (code.Length != 3 || !char.IsAsciiLetter(code[0]) || !char.IsAsciiLetter(code[1]) || !char.IsAsciiLetter(code[2]))
                    throw JsErrors.RangeError(realm, $"Invalid currency code: {code}");
                return code.ToUpperInvariant();
        }
    }

    private static string? Lookup(JsDisplayNames dn, string code)
    {
        bool shortNames = dn.Style != "long";
        switch (dn.Type)
        {
            case "language":
                return LdnName(LocaleDisplayNames(dn.DataLocale, shortNames, dn.LanguageDisplay != "standard"), Icu.uldn_localeDisplayName_ptr, IntlCore.ToIcuLocale(code));
            case "region":
                return LdnName(LocaleDisplayNames(dn.DataLocale, shortNames, true), Icu.uldn_regionDisplayName_ptr, code);
            case "script":
                return LdnName(LocaleDisplayNames(dn.DataLocale, shortNames, true), Icu.uldn_scriptDisplayName_ptr, code);
            case "calendar":
                return KeyValueName(LocaleDisplayNames(dn.DataLocale, shortNames, true), "calendar", Icu.ToLegacyType("ca", code) ?? code);
            case "dateTimeField":
                return FieldName(dn.DataLocale, s_fields[code], dn.Style);
            default:
                return CurrencyName(dn.DataLocale, code, dn.Style);
        }
    }

    internal static void Setup(Realm r, JsObject intl, IntlProtos protos)
    {
        var proto = IntlText.Proto(r, "Intl.DisplayNames");
        protos.DisplayNamesPrototype = proto;
        var ctor = r.Constructor("DisplayNames", 2, static (in NativeArgs a) =>
        {
            IntlText.RequireNew(a, "DisplayNames");
            var realm = a.Realm;
            var proto = IntlText.ProtoFromNewTarget(a, static p => p.DisplayNamesPrototype);
            var requested = IntlCore.CanonicalizeLocaleList(realm, a[0]);
            var options = IntlCore.GetOptionsObject(realm, a[1]);
            var matcher = IntlCore.GetStringOption(realm, options, "localeMatcher", IntlCore.s_matchers, "best fit");
            var res = IntlCore.ResolveLocale(realm, requested, matcher, new Dictionary<string, string?>(), Array.Empty<string>(), static (_, _) => IntlText.NoData);
            var dn = new JsDisplayNames(realm, proto) { Locale = res["locale"]!, DataLocale = res["dataLocale"]! };
            dn.Style = IntlCore.GetStringOption(realm, options, "style", s_styles, "long")!;
            dn.Type = IntlCore.GetStringOption(realm, options, "type", s_types, null) ?? throw JsErrors.TypeError(realm, "Intl.DisplayNames requires a type option");
            dn.Fallback = IntlCore.GetStringOption(realm, options, "fallback", s_fallbacks, "code")!;
            var languageDisplay = IntlCore.GetStringOption(realm, options, "languageDisplay", s_languageDisplays, "dialect")!;
            if (dn.Type == "language") dn.LanguageDisplay = languageDisplay;
            return JsValue.FromObject(dn);
        }, proto);
        intl.DefineHidden("DisplayNames", JsValue.FromObject(ctor));
        r.Method(ctor, "supportedLocalesOf", 1, static (in NativeArgs a) => IntlCore.SupportedLocalesOf(a.Realm, a[0], a[1]));
        r.Method(proto, "of", 1, static (in NativeArgs a) =>
        {
            var dn = IntlCore.This<JsDisplayNames>(a, "Intl.DisplayNames.prototype.of");
            var code = CanonicalCode(a.Realm, dn.Type, Operations.ToString(a.Realm, a[0]));
            var name = Lookup(dn, code);
            if (name != null) return new JsValue(name);
            return dn.Fallback == "code" ? new JsValue(code) : JsValue.Undefined;
        });
        r.Method(proto, "resolvedOptions", 0, static (in NativeArgs a) =>
        {
            var dn = IntlCore.This<JsDisplayNames>(a, "Intl.DisplayNames.prototype.resolvedOptions");
            var o = new JsObject(a.Realm, a.Realm.ObjectPrototype);
            IntlText.Data(o, "locale", new JsValue(dn.Locale));
            IntlText.Data(o, "style", new JsValue(dn.Style));
            IntlText.Data(o, "type", new JsValue(dn.Type));
            IntlText.Data(o, "fallback", new JsValue(dn.Fallback));
            if (dn.LanguageDisplay != null) IntlText.Data(o, "languageDisplay", new JsValue(dn.LanguageDisplay));
            return JsValue.FromObject(o);
        });
    }
}

internal sealed partial class IntlProtos
{
    public JsObject DisplayNamesPrototype = null!;
}

internal static partial class IntlBuiltins
{
    static partial void SetupDisplayNames(Realm r, JsObject intl, IntlProtos protos) => IntlDisplayNames.Setup(r, intl, protos);
}
