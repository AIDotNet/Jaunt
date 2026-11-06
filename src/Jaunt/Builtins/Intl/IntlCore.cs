using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Jaunt.Runtime;

/// <summary>Per-realm Intl intrinsics; each service declares its own fields in its file (partial).</summary>
internal sealed partial class IntlProtos
{
    public JsObject Intl = null!;
    /// <summary>%Intl%.[[FallbackSymbol]]: the per-realm symbol of legacy-constructed NumberFormat / DateTimeFormat objects.</summary>
    public readonly JsSymbol FallbackSymbol = new("IntlLegacyConstructedSymbol");
}

internal sealed partial class Realm
{
    private IntlProtos? _intl;
    /// <summary>Intl intrinsics (null when ICU is unavailable).</summary>
    public IntlProtos? Intl => _intl ?? (Icu.Available ? Ensure(IntrinsicGroup.Intl)._intl : null);
    internal void SetIntl(IntlProtos p) => _intl = p;
    /// <summary>The default locale of the host (DefaultLocale, ECMA-402 6.2.4).</summary>
    public string DefaultLocale = "en-US";
}

/// <summary>A parsed unicode_locale_id (UTS 35) in canonical letter case.</summary>
internal sealed class LanguageTag
{
    public string Language = "";
    public string? Script;
    public string? Region;
    public List<string> Variants = new();
    /// <summary>Extensions other than -u- and -x-, keyed by singleton, as written (lowercase).</summary>
    public SortedDictionary<char, string> OtherExtensions = new();
    public List<string> UAttributes = new();
    /// <summary>-u- keywords in order of appearance (value "" = no type).</summary>
    public List<KeyValuePair<string, string>> UKeywords = new();
    public string? PrivateUse;

    public string BaseName()
    {
        var sb = new StringBuilder(Language);
        if (Script != null) sb.Append('-').Append(Script);
        if (Region != null) sb.Append('-').Append(Region);
        foreach (var v in Variants) sb.Append('-').Append(v);
        return sb.ToString();
    }
}

internal static partial class IntlCore
{
    // ------------------------------------------------------------------ language tags (UTS 35 / ECMA-402 6.2)

    private static bool IsAlpha(string s) { foreach (char c in s) if (!char.IsAsciiLetter(c)) return false; return true; }
    private static bool IsDigit(string s) { foreach (char c in s) if (!char.IsAsciiDigit(c)) return false; return true; }
    private static bool IsAlnum(string s) { foreach (char c in s) if (!char.IsAsciiLetterOrDigit(c)) return false; return true; }

    public static bool IsLanguageSubtag(string s) => IsAlpha(s) && (s.Length is 2 or 3 || (s.Length >= 5 && s.Length <= 8));
    public static bool IsScriptSubtag(string s) => s.Length == 4 && IsAlpha(s);
    public static bool IsRegionSubtag(string s) => (s.Length == 2 && IsAlpha(s)) || (s.Length == 3 && IsDigit(s));
    public static bool IsVariantSubtag(string s) => IsAlnum(s) && ((s.Length >= 5 && s.Length <= 8) || (s.Length == 4 && char.IsAsciiDigit(s[0])));

    /// <summary>IsStructurallyValidLanguageTag; also returns the parsed tag (subtags lower-cased).</summary>
    public static LanguageTag? Parse(string tag)
    {
        if (tag.Length == 0) return null;
        foreach (char c in tag) if (c > 0x7F) return null;
        var parts = tag.Split('-');
        foreach (var p in parts) if (p.Length == 0 || p.Length > 8 || !IsAlnum(p)) return null;
        int i = 0;
        var t = new LanguageTag();
        var lang = parts[0].ToLowerInvariant();
        if (!IsLanguageSubtag(lang)) return null;
        t.Language = lang;
        i = 1;
        if (i < parts.Length && IsScriptSubtag(parts[i])) { t.Script = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1).ToLowerInvariant(); i++; }
        if (i < parts.Length && IsRegionSubtag(parts[i])) { t.Region = parts[i].ToUpperInvariant(); i++; }
        while (i < parts.Length && IsVariantSubtag(parts[i]))
        {
            var v = parts[i].ToLowerInvariant();
            if (t.Variants.Contains(v)) return null;
            t.Variants.Add(v);
            i++;
        }
        var seenSingletons = new HashSet<char>();
        while (i < parts.Length)
        {
            var sub = parts[i].ToLowerInvariant();
            if (sub.Length != 1) return null;
            char singleton = sub[0];
            if (singleton == 'x')
            {
                if (i + 1 >= parts.Length) return null;
                for (int k = i + 1; k < parts.Length; k++) if (parts[k].Length < 1 || parts[k].Length > 8) return null;
                t.PrivateUse = "x-" + string.Join("-", parts.Skip(i + 1)).ToLowerInvariant();
                return t;
            }
            if (!seenSingletons.Add(singleton)) return null;
            i++;
            int start = i;
            if (singleton == 'u')
            {
                while (i < parts.Length && parts[i].Length >= 3 && parts[i].Length <= 8)
                {
                    t.UAttributes.Add(parts[i].ToLowerInvariant());
                    i++;
                }
                while (i < parts.Length && parts[i].Length == 2)
                {
                    var key = parts[i].ToLowerInvariant();
                    if (!char.IsAsciiLetterOrDigit(key[0]) || !char.IsAsciiLetter(key[1])) return null;
                    i++;
                    var types = new List<string>();
                    while (i < parts.Length && parts[i].Length >= 3 && parts[i].Length <= 8) { types.Add(parts[i].ToLowerInvariant()); i++; }
                    t.UKeywords.Add(new KeyValuePair<string, string>(key, string.Join("-", types)));
                }
                if (i == start) return null;
            }
            else if (singleton == 't')
            {
                var tparts = new List<string>();
                // tlang
                if (i < parts.Length && IsLanguageSubtag(parts[i]))
                {
                    tparts.Add(parts[i].ToLowerInvariant());
                    i++;
                    if (i < parts.Length && IsScriptSubtag(parts[i])) { tparts.Add(parts[i].ToLowerInvariant()); i++; }
                    if (i < parts.Length && IsRegionSubtag(parts[i])) { tparts.Add(parts[i].ToLowerInvariant()); i++; }
                    var tvariants = new HashSet<string>();
                    while (i < parts.Length && IsVariantSubtag(parts[i]))
                    {
                        var v = parts[i].ToLowerInvariant();
                        if (!tvariants.Add(v)) return null;
                        tparts.Add(v);
                        i++;
                    }
                }
                while (i < parts.Length && parts[i].Length == 2 && char.IsAsciiLetter(parts[i][0]) && char.IsAsciiDigit(parts[i][1]))
                {
                    tparts.Add(parts[i].ToLowerInvariant());
                    i++;
                    int vs = i;
                    while (i < parts.Length && parts[i].Length >= 3 && parts[i].Length <= 8) { tparts.Add(parts[i].ToLowerInvariant()); i++; }
                    if (i == vs) return null;
                }
                if (tparts.Count == 0) return null;
                t.OtherExtensions['t'] = string.Join("-", tparts);
            }
            else
            {
                var oparts = new List<string>();
                while (i < parts.Length && parts[i].Length >= 2 && parts[i].Length <= 8) { oparts.Add(parts[i].ToLowerInvariant()); i++; }
                if (oparts.Count == 0) return null;
                t.OtherExtensions[singleton] = string.Join("-", oparts);
            }
        }
        return t;
    }

    public static bool IsStructurallyValid(string tag) => Parse(tag) != null;

    private static readonly Dictionary<string, string> s_canonicalCache = new(StringComparer.Ordinal);

    /// <summary>CanonicalizeUnicodeLocaleId (UTS 35 canonicalization with the CLDR alias data, then canonical extension order).</summary>
    public static string Canonicalize(string tag) => TryCanonicalize(tag, out var c) ? c : tag.ToLowerInvariant();

    /// <summary>
    /// IsStructurallyValidLanguageTag + CanonicalizeUnicodeLocaleId in one step (results cached); false when
    /// <paramref name="tag"/> is not structurally valid.
    /// </summary>
    public static bool TryCanonicalize(string tag, out string canonical)
    {
        lock (s_canonicalCache)
            if (s_canonicalCache.TryGetValue(tag, out canonical!)) return true;
        var parsed = Parse(tag);
        if (parsed == null) { canonical = ""; return false; }
        CanonicalizeTag(parsed);
        canonical = Format(parsed);
        lock (s_canonicalCache)
        {
            if (s_canonicalCache.Count > 10_000) s_canonicalCache.Clear();
            s_canonicalCache[tag] = canonical;
        }
        return true;
    }

    /// <summary>Serializes a tag in canonical form: sorted -u- keywords (dropping duplicates and "true"), sorted singletons.</summary>
    public static string Format(LanguageTag t)
    {
        var sb = new StringBuilder(t.BaseName());
        var exts = new SortedDictionary<char, string>(t.OtherExtensions);
        if (t.UAttributes.Count > 0 || t.UKeywords.Count > 0)
        {
            var u = new StringBuilder();
            foreach (var a in t.UAttributes.Distinct().OrderBy(x => x, StringComparer.Ordinal))
            {
                if (u.Length > 0) u.Append('-');
                u.Append(a);
            }
            var seen = new HashSet<string>();
            foreach (var kw in t.UKeywords.Where(k => seen.Add(k.Key)).OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (u.Length > 0) u.Append('-');
                u.Append(kw.Key);
                if (kw.Value.Length > 0 && kw.Value != "true") u.Append('-').Append(kw.Value);
            }
            exts['u'] = u.ToString();
        }
        foreach (var e in exts) sb.Append('-').Append(e.Key).Append('-').Append(e.Value);
        if (t.PrivateUse != null) sb.Append('-').Append(t.PrivateUse);
        return sb.ToString();
    }

    /// <summary>Removes the -u- extension (RemoveUnicodeExtension semantics on canonical tags).</summary>
    public static string RemoveUnicodeExtension(string tag, out string extension)
    {
        extension = "";
        int x = tag.IndexOf("-x-", StringComparison.Ordinal);
        string head = x >= 0 ? tag.Substring(0, x) : tag;
        string tail = x >= 0 ? tag.Substring(x) : "";
        int u = head.IndexOf("-u-", StringComparison.Ordinal);
        if (u < 0) return tag;
        // the -u- extension runs until the next singleton
        int end = u + 3;
        var parts = head.Substring(u + 1).Split('-');
        int len = 1;   // "u"
        for (int k = 1; k < parts.Length; k++)
        {
            if (parts[k].Length == 1) break;
            len += 1 + parts[k].Length;
        }
        end = u + 1 + len;
        extension = head.Substring(u, end - u);
        return head.Substring(0, u) + head.Substring(end) + tail;
    }

    // ------------------------------------------------------------------ locale lists and negotiation

    /// <summary>CanonicalizeLocaleList (9.2.1).</summary>
    public static List<string> CanonicalizeLocaleList(Realm realm, in JsValue locales)
    {
        var seen = new List<string>();
        if (locales.IsUndefined) return seen;
        JsObject o;
        if (locales.IsString || IsLocaleObject(locales)) o = Operations.CreateArrayFromList(realm, new[] { locales });
        else o = Operations.ToObject(realm, locales);
        long len = Operations.LengthOfArrayLike(realm, o);
        for (long k = 0; k < len; k++)
        {
            var pk = ArrayBuiltins.Key(k);
            if (!o.HasProperty(pk)) continue;
            var kValue = o.Get(pk);
            if (!kValue.IsString && !kValue.IsObject) throw JsErrors.TypeError(realm, "locale must be a string or an object");
            string tag = IsLocaleObject(kValue) ? LocaleObjectTag(kValue) : Operations.ToString(realm, kValue);
            if (!TryCanonicalize(tag, out var canonical)) throw JsErrors.RangeError(realm, $"Incorrect locale information provided: {tag}");
            if (!seen.Contains(canonical)) seen.Add(canonical);
        }
        return seen;
    }

    /// <summary>Hook for Intl.Locale objects ([[InitializedLocale]]); implemented by the Locale service.</summary>
    public static Func<JsValue, bool> IsLocaleObjectHook = static _ => false;
    public static Func<JsValue, string> LocaleTagHook = static _ => "";
    private static bool IsLocaleObject(in JsValue v) => v.IsObject && IsLocaleObjectHook(v);
    private static string LocaleObjectTag(in JsValue v) => LocaleTagHook(v);

    private static HashSet<string>? s_available;

    /// <summary>The available locales (ICU's list as canonical BCP 47 tags).</summary>
    public static HashSet<string> AvailableLocales
    {
        get
        {
            var a = s_available;
            if (a != null) return a;
            a = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in Icu.AvailableLocaleIds())
            {
                var t = Icu.ToLanguageTag(id);
                if (t == null || !IsStructurallyValid(t)) continue;
                a.Add(Canonicalize(t));
                // add the likely-script-less and region-less forms ICU resolves anyway
                var parsed = Parse(t)!;
                if (parsed.Script != null)
                {
                    var noScript = new LanguageTag { Language = parsed.Language, Region = parsed.Region };
                    noScript.Variants.AddRange(parsed.Variants);
                    var max = Icu.AddLikelySubtags(Icu.ForLanguageTag(noScript.BaseName()) ?? "");
                    var maxTag = max == null ? null : Icu.ToLanguageTag(max);
                    if (maxTag != null && Parse(maxTag)?.Script == parsed.Script) a.Add(noScript.BaseName());
                }
            }
            a.Add("en-US");
            s_available = a;
            return a;
        }
    }

    public readonly record struct LocaleMatch(string Locale, string Extension);

    /// <summary>LookupMatchingLocaleByPrefix (also used for "best fit").</summary>
    public static LocaleMatch? LookupMatch(HashSet<string> available, IEnumerable<string> requested)
    {
        foreach (var locale in requested)
        {
            var noExt = RemoveUnicodeExtension(locale, out var ext);
            // private use and other extensions do not take part in matching
            var prefix = StripExtensions(noExt);
            while (prefix.Length > 0)
            {
                if (available.Contains(prefix)) return new LocaleMatch(prefix, ext);
                int pos = prefix.LastIndexOf('-');
                if (pos < 0) break;
                if (pos >= 2 && prefix[pos - 2] == '-') pos -= 2;
                prefix = prefix.Substring(0, pos);
            }
        }
        return null;
    }

    private static string StripExtensions(string tag)
    {
        var parts = tag.Split('-');
        for (int i = 1; i < parts.Length; i++)
            if (parts[i].Length == 1) return string.Join("-", parts.Take(i));
        return tag;
    }

    /// <summary>Supported values for a relevant extension key in a locale (first = default).</summary>
    public delegate IReadOnlyList<string> LocaleDataFn(string locale, string key);

    /// <summary>ResolveLocale (9.2.7). <paramref name="optionValues"/> holds options overriding keys (e.g. "ca").</summary>
    public static Dictionary<string, string?> ResolveLocale(Realm realm, List<string> requested, string? matcher, Dictionary<string, string?> optionValues,
        string[] relevantKeys, LocaleDataFn localeData, HashSet<string>? available = null)
    {
        available ??= AvailableLocales;
        var r = LookupMatch(available, requested) ?? new LocaleMatch(LookupMatch(available, new[] { realm.DefaultLocale })?.Locale ?? "en-US", "");
        var foundLocale = r.Locale;
        var keywords = new List<KeyValuePair<string, string>>();
        if (r.Extension.Length > 0)
        {
            var parsed = Parse("und" + r.Extension);
            if (parsed != null) keywords = parsed.UKeywords;
        }
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        var supported = new List<KeyValuePair<string, string>>();
        foreach (var key in relevantKeys)
        {
            var keyData = localeData(foundLocale, key);
            string? value = keyData.Count > 0 ? keyData[0] : null;
            KeyValuePair<string, string>? supportedKeyword = null;
            var entry = keywords.FirstOrDefault(k => k.Key == key);
            if (entry.Key != null)
            {
                var requestedValue = entry.Value;
                if (requestedValue.Length > 0)
                {
                    if (keyData.Contains(requestedValue))
                    {
                        value = requestedValue;
                        supportedKeyword = new(key, value);
                    }
                }
                else if (keyData.Contains("true"))
                {
                    value = "true";
                    supportedKeyword = new(key, "");
                }
            }
            if (optionValues.TryGetValue(key, out var optionsValue) && optionsValue != null)
            {
                var ov = optionsValue == "" ? "true" : optionsValue;
                if (ov != value && keyData.Contains(ov))
                {
                    value = ov;
                    supportedKeyword = null;
                }
            }
            if (supportedKeyword != null) supported.Add(supportedKeyword.Value);
            result[key] = value;
        }
        if (supported.Count > 0)
        {
            // the resolved locale carries only the supported keywords: attributes are dropped (supportedAttributes is empty)
            var t = Parse(foundLocale)!;
            t.UKeywords.AddRange(supported);
            foundLocale = Format(t);
        }
        result["locale"] = foundLocale;
        result["dataLocale"] = r.Locale;
        return result;
    }

    /// <summary>FilterLocales / SupportedLocales: the requested locales that have a match.</summary>
    public static JsValue SupportedLocalesOf(Realm realm, in JsValue locales, in JsValue options, HashSet<string>? available = null)
    {
        var requested = CanonicalizeLocaleList(realm, locales);
        var opts = CoerceOptionsToObject(realm, options);
        GetStringOption(realm, opts, "localeMatcher", s_matchers, "best fit");
        available ??= AvailableLocales;
        var result = new List<JsValue>();
        foreach (var l in requested)
            if (LookupMatch(available, new[] { l }) != null) result.Add(new JsValue(l));
        return JsValue.FromObject(Operations.CreateArrayFromList(realm, result.ToArray()));
    }

    // ------------------------------------------------------------------ options

    public static readonly string[] s_matchers = { "lookup", "best fit" };

    /// <summary>CoerceOptionsToObject.</summary>
    public static JsObject CoerceOptionsToObject(Realm realm, in JsValue options) =>
        options.IsUndefined ? new JsObject(realm, null) : Operations.ToObject(realm, options);

    /// <summary>GetOptionsObject.</summary>
    public static JsObject GetOptionsObject(Realm realm, in JsValue options)
    {
        if (options.IsUndefined) return new JsObject(realm, null);
        if (options.IsObject) return options.AsObjectUnchecked;
        throw JsErrors.TypeError(realm, "options must be an object");
    }

    /// <summary>GetOption with type "string".</summary>
    public static string? GetStringOption(Realm realm, JsObject options, string name, string[]? values, string? fallback)
    {
        var v = options.Get(name);
        if (v.IsUndefined) return fallback;
        var s = Operations.ToString(realm, v);
        if (values != null && Array.IndexOf(values, s) < 0) throw JsErrors.RangeError(realm, $"Value {s} out of range for {name}");
        return s;
    }

    /// <summary>GetOption with type "boolean" (null = undefined and no fallback).</summary>
    public static bool? GetBoolOption(Realm realm, JsObject options, string name, bool? fallback)
    {
        var v = options.Get(name);
        if (v.IsUndefined) return fallback;
        return Operations.ToBoolean(v);
    }

    /// <summary>DefaultNumberOption.</summary>
    public static int? DefaultNumberOption(Realm realm, in JsValue v, int min, int max, int? fallback, string name)
    {
        if (v.IsUndefined) return fallback;
        double d = Operations.ToNumber(realm, v);
        if (double.IsNaN(d) || d < min || d > max) throw JsErrors.RangeError(realm, $"{name} value is out of range");
        return (int)Math.Floor(d);
    }

    /// <summary>GetNumberOption.</summary>
    public static int? GetNumberOption(Realm realm, JsObject options, string name, int min, int max, int? fallback) =>
        DefaultNumberOption(realm, options.Get(name), min, max, fallback, name);

    /// <summary>Validates a Unicode extension type option (3*8alphanum *("-" 3*8alphanum)).</summary>
    public static bool IsUnicodeType(string s)
    {
        if (s.Length == 0) return false;
        foreach (var p in s.Split('-'))
            if (p.Length < 3 || p.Length > 8 || !IsAlnum(p)) return false;
        return true;
    }

    // ------------------------------------------------------------------ misc

    public static string ToIcuLocale(string tag) => Icu.ForLanguageTag(tag) ?? tag.Replace('-', '_');

    /// <summary>Unicode extension value of a canonical tag (null when absent; "" when present without a type).</summary>
    public static string? GetUnicodeKeyword(string tag, string key)
    {
        var t = Parse(tag);
        if (t == null) return null;
        foreach (var kw in t.UKeywords) if (kw.Key == key) return kw.Value;
        return null;
    }

    public static T This<T>(in NativeArgs a, string method) where T : JsObject =>
        a.This.Ref as T ?? throw JsErrors.TypeError(a.Realm, $"{method} called on an incompatible receiver");
}

/// <summary>The Intl namespace object; each service adds its constructor through a partial Setup method.</summary>
internal static partial class IntlBuiltins
{
    public static void Setup(Realm r)
    {
        if (!Icu.Available) return;
        var protos = new IntlProtos();
        r.SetIntl(protos);
        var intl = new JsObject(r, r.ObjectPrototype);
        protos.Intl = intl;
        intl.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("Intl"), PropFlags.Configurable);
        r.DefineGlobal("Intl", JsValue.FromObject(intl));
        r.Method(intl, "getCanonicalLocales", 1, static (in NativeArgs a) =>
        {
            var list = IntlCore.CanonicalizeLocaleList(a.Realm, a[0]);
            return JsValue.FromObject(Operations.CreateArrayFromList(a.Realm, list.Select(s => new JsValue(s)).ToArray()));
        });
        SetupSupportedValuesOf(r, intl);
        SetupCollator(r, intl, protos);
        SetupNumberFormat(r, intl, protos);
        SetupPluralRules(r, intl, protos);
        SetupDateTimeFormat(r, intl, protos);
        SetupRelativeTimeFormat(r, intl, protos);
        SetupListFormat(r, intl, protos);
        SetupDisplayNames(r, intl, protos);
        SetupSegmenter(r, intl, protos);
        SetupLocale(r, intl, protos);
        SetupDurationFormat(r, intl, protos);
    }

    static partial void SetupSupportedValuesOf(Realm r, JsObject intl);
    static partial void SetupCollator(Realm r, JsObject intl, IntlProtos protos);
    static partial void SetupNumberFormat(Realm r, JsObject intl, IntlProtos protos);
    static partial void SetupPluralRules(Realm r, JsObject intl, IntlProtos protos);
    static partial void SetupDateTimeFormat(Realm r, JsObject intl, IntlProtos protos);
    static partial void SetupRelativeTimeFormat(Realm r, JsObject intl, IntlProtos protos);
    static partial void SetupListFormat(Realm r, JsObject intl, IntlProtos protos);
    static partial void SetupDisplayNames(Realm r, JsObject intl, IntlProtos protos);
    static partial void SetupSegmenter(Realm r, JsObject intl, IntlProtos protos);
    static partial void SetupLocale(Realm r, JsObject intl, IntlProtos protos);
    static partial void SetupDurationFormat(Realm r, JsObject intl, IntlProtos protos);
}
