using System;
using System.Collections.Generic;

namespace Jaunt.Runtime;

internal sealed partial class IntlProtos
{
    public JsObject LocalePrototype = null!;
}

/// <summary>An Intl.Locale instance ([[InitializedLocale]]); all fields derive from the canonical <see cref="Locale"/>.</summary>
internal sealed class JsLocale : JsObject
{
    public readonly string Locale;
    public readonly LanguageTag Tag;
    public readonly string? Calendar, Collation, HourCycle, CaseFirst, NumberingSystem, FirstDayOfWeek;
    public readonly bool Numeric;

    public JsLocale(Realm realm, JsObject proto, string locale) : base(realm, proto)
    {
        Locale = locale;
        Tag = IntlCore.Parse(locale)!;
        Calendar = Keyword("ca");
        Collation = Keyword("co");
        HourCycle = Keyword("hc");
        CaseFirst = Keyword("kf");
        NumberingSystem = Keyword("nu");
        FirstDayOfWeek = Keyword("fw");
        var kn = Keyword("kn");
        Numeric = kn is "" or "true";
    }

    private string? Keyword(string key)
    {
        foreach (var kw in Tag.UKeywords) if (kw.Key == key) return kw.Value;
        return null;
    }
}

internal static unsafe partial class Icu
{
    private static nint s_ucalKeywordValues, s_ucolKeywordValues_L, s_udatpgOpen_L, s_udatpgClose_L, s_udatpgHourCycle,
        s_unumsysOpen_L, s_unumsysName, s_unumsysClose_L, s_tzEnum, s_ianaId, s_isRtl, s_ucalOpen, s_ucalClose, s_ucalGetAttribute, s_ucalDayType;

    /// <summary>ucal_getKeywordValuesForLocale / ucol_getKeywordValuesForLocale (ICU legacy type names).</summary>
    public static List<string> KeywordValuesForLocale(bool collation, string key, string locale, bool commonlyUsed)
    {
        var k = Utf8Z(key);
        var l = Utf8Z(locale);
        int err = 0;
        nint en;
        fixed (byte* kp = k, lp = l)
        {
            var f = (delegate* unmanaged<byte*, byte*, sbyte, int*, nint>)(collation
                ? P(ref s_ucolKeywordValues_L, "ucol_getKeywordValuesForLocale")
                : P(ref s_ucalKeywordValues, "ucal_getKeywordValuesForLocale"));
            en = f(kp, lp, (sbyte)(commonlyUsed ? 1 : 0), &err);
        }
        if (Failure(err)) return new List<string>();
        return ReadEnum(en);
    }


    /// <summary>udatpg_getDefaultHourCycle: 0 = h11, 1 = h12, 2 = h23, 3 = h24; -1 on error.</summary>
    public static int DefaultHourCycle(string locale)
    {
        var l = Utf8Z(locale);
        int err = 0;
        nint g;
        fixed (byte* lp = l) g = ((delegate* unmanaged<byte*, int*, nint>)P(ref s_udatpgOpen_L, "udatpg_open"))(lp, &err);
        if (Failure(err) || g == 0) return -1;
        try
        {
            if (!Has("udatpg_getDefaultHourCycle")) return HourCycleFromPattern(BestDatePattern(locale, "j"));
            int e2 = 0;
            int hc = ((delegate* unmanaged<nint, int*, int>)P(ref s_udatpgHourCycle, "udatpg_getDefaultHourCycle"))(g, &e2);
            return Failure(e2) ? -1 : hc;
        }
        finally { ((delegate* unmanaged<nint, void>)P(ref s_udatpgClose_L, "udatpg_close"))(g); }
    }

    /// <summary>The default numbering system name of a locale (unumsys_open + unumsys_getName).</summary>
    public static string? LocaleDefaultNumberingSystem(string locale)
    {
        var l = Utf8Z(locale);
        int err = 0;
        nint ns;
        fixed (byte* lp = l) ns = ((delegate* unmanaged<byte*, int*, nint>)P(ref s_unumsysOpen_L, "unumsys_open"))(lp, &err);
        if (Failure(err) || ns == 0) return null;
        try { return FromCString(((delegate* unmanaged<nint, byte*>)P(ref s_unumsysName, "unumsys_getName"))(ns)); }
        finally { ((delegate* unmanaged<nint, void>)P(ref s_unumsysClose_L, "unumsys_close"))(ns); }
    }

    /// <summary>ucal_openTimeZoneIDEnumeration (zoneType 0 = any, 1 = canonical, 2 = canonical location).</summary>
    public static List<string> TimeZoneIds(int zoneType, string? region)
    {
        var r = region == null ? null : Utf8Z(region);
        int err = 0;
        nint en;
        fixed (byte* rp = r) en = ((delegate* unmanaged<int, byte*, int*, int*, nint>)P(ref s_tzEnum, "ucal_openTimeZoneIDEnumeration"))(zoneType, rp, null, &err);
        if (Failure(err)) return new List<string>();
        return ReadEnum(en);
    }

    private static int s_hasIana;   // 0 = unknown, 1 = yes, -1 = no

    /// <summary>ucal_getIanaTimeZoneID (ICU 74+): the IANA primary id for an ICU/CLDR canonical id; returns the input otherwise.</summary>
    public static string IanaTimeZoneId(string id)
    {
        if (s_hasIana == 0)
        {
            try { P(ref s_ianaId, "ucal_getIanaTimeZoneID"); s_hasIana = 1; }
            catch (EntryPointNotFoundException) { s_hasIana = -1; }
        }
        if (s_hasIana < 0) return id;
        var f = (delegate* unmanaged<char*, int, char*, int, int*, int>)s_ianaId;
        var r = CallUChars((buf, cap, err) => { fixed (char* ip = id) return f(ip, id.Length, buf, cap, err); });
        return string.IsNullOrEmpty(r) ? id : r;
    }

    public static bool IsRightToLeft(string locale)
    {
        var l = Utf8Z(locale);
        fixed (byte* lp = l) return ((delegate* unmanaged<byte*, sbyte>)P(ref s_isRtl, "uloc_isRightToLeft"))(lp) != 0;
    }

    /// <summary>Week data of a locale: first day (ICU: 1 = Sunday … 7 = Saturday) and the weekend days (ICU numbering).</summary>
    public static bool WeekData(string locale, out int firstDay, out List<int> weekend)
    {
        firstDay = 1;
        weekend = new List<int>();
        var l = Utf8Z(locale);
        int err = 0;
        nint cal;
        fixed (byte* lp = l) cal = ((delegate* unmanaged<char*, int, byte*, int, int*, nint>)P(ref s_ucalOpen, "ucal_open"))(null, 0, lp, 1 /* UCAL_GREGORIAN */, &err);
        if (Failure(err) || cal == 0) return false;
        try
        {
            firstDay = ((delegate* unmanaged<nint, int, int>)P(ref s_ucalGetAttribute, "ucal_getAttribute"))(cal, 1 /* UCAL_FIRST_DAY_OF_WEEK */);
            var dayType = (delegate* unmanaged<nint, int, int*, int>)P(ref s_ucalDayType, "ucal_getDayOfWeekType");
            for (int d = 1; d <= 7; d++)
            {
                int e = 0;
                int t = dayType(cal, d, &e);
                if (!Failure(e) && t != 0 /* UCAL_WEEKDAY */) weekend.Add(d);
            }
            return true;
        }
        finally { ((delegate* unmanaged<nint, void>)P(ref s_ucalClose, "ucal_close"))(cal); }
    }
}

internal static partial class IntlCore
{
    /// <summary>The prototype for an Intl constructor's new instance: NewTarget's "prototype", else the intrinsic of NewTarget's realm.</summary>
    public static JsObject ProtoFromCtor(Realm realm, JsObject? newTarget, Func<IntlProtos, JsObject> intrinsic)
    {
        var fallback = intrinsic(realm.Intl!);
        if (newTarget == null) return fallback;
        var p = newTarget.Get("prototype");
        if (p.IsObject) return p.AsObjectUnchecked;
        var fr = Operations.GetFunctionRealm(realm, newTarget);
        return fr.Intl is { } fp ? intrinsic(fp) : fallback;
    }

    /// <summary>Add Likely Subtags (UTS 35) on a language id via ICU; returns false when ICU signals an error.</summary>
    public static bool AddLikelySubtags(ref string language, ref string? script, ref string? region)
    {
        var id = language + (script != null ? "_" + script : "") + (region != null ? "_" + region : "");
        var max = Icu.AddLikelySubtags(id);
        if (max == null) return false;
        return ReadIcuLanguageId(max, ref language, ref script, ref region, false);
    }

    /// <summary>Remove Likely Subtags (UTS 35, favoring region) on a language id via ICU.</summary>
    public static bool RemoveLikelySubtags(ref string language, ref string? script, ref string? region)
    {
        var id = language + (script != null ? "_" + script : "") + (region != null ? "_" + region : "");
        var min = Icu.MinimizeSubtags(id);
        if (min == null) return false;
        return ReadIcuLanguageId(min, ref language, ref script, ref region, true);
    }

    private static bool ReadIcuLanguageId(string icu, ref string language, ref string? script, ref string? region, bool replaceAll)
    {
        int at = icu.IndexOf('@');
        if (at >= 0) icu = icu.Substring(0, at);
        var parts = icu.Split('_');
        if (parts.Length == 0 || parts[0].Length == 0) return false;
        string lang = parts[0].ToLowerInvariant();
        string? s = null, r = null;
        int i = 1;
        if (i < parts.Length && IsScriptSubtag(parts[i])) { s = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1).ToLowerInvariant(); i++; }
        if (i < parts.Length && IsRegionSubtag(parts[i])) { r = parts[i].ToUpperInvariant(); i++; }
        if (!IsLanguageSubtag(lang) && lang != "und") return false;
        language = lang;
        if (replaceAll) { script = s; region = r; }
        else { script = s ?? script; region = r ?? region; }
        return true;
    }
}

internal static partial class IntlBuiltins
{
    private static readonly string[] s_hourCycles = { "h11", "h12", "h23", "h24" };
    private static readonly string[] s_caseFirsts = { "upper", "lower", "false" };

    static partial void SetupLocale(Realm r, JsObject intl, IntlProtos protos)
    {
        IntlCore.IsLocaleObjectHook = static v => v.Ref is JsLocale;
        IntlCore.LocaleTagHook = static v => ((JsLocale)v.Ref!).Locale;

        var proto = new JsObject(r, r.ObjectPrototype);
        protos.LocalePrototype = proto;
        proto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("Intl.Locale"), PropFlags.Configurable);
        var ctor = r.Constructor("Locale", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (!a.IsConstruct) throw JsErrors.TypeError(realm, "Constructor Intl.Locale requires 'new'");
            var p = IntlCore.ProtoFromCtor(realm, a.NewTarget.AsObjectUnchecked, static ip => ip.LocalePrototype);
            var tagArg = a[0];
            if (!tagArg.IsString && !tagArg.IsObject) throw JsErrors.TypeError(realm, "First argument to Intl.Locale constructor can't be empty or missing");
            string tag = tagArg.Ref is JsLocale l ? l.Locale : Operations.ToString(realm, tagArg);
            var options = IntlCore.CoerceOptionsToObject(realm, a[1]);
            return JsValue.FromObject(new JsLocale(realm, p, CreateLocaleTag(realm, tag, options)));
        }, proto);
        intl.DefineHidden("Locale", JsValue.FromObject(ctor));

        r.Method(proto, "maximize", 0, static (in NativeArgs a) =>
        {
            var loc = IntlCore.This<JsLocale>(a, "Intl.Locale.prototype.maximize");
            var t = IntlCore.Parse(loc.Locale)!;
            string lang = t.Language;
            string? script = t.Script, region = t.Region;
            if (IntlCore.AddLikelySubtags(ref lang, ref script, ref region)) { t.Language = lang; t.Script = script; t.Region = region; }
            return JsValue.FromObject(new JsLocale(a.Realm, a.Realm.Intl!.LocalePrototype, IntlCore.Canonicalize(IntlCore.Format(t))));
        });
        r.Method(proto, "minimize", 0, static (in NativeArgs a) =>
        {
            var loc = IntlCore.This<JsLocale>(a, "Intl.Locale.prototype.minimize");
            var t = IntlCore.Parse(loc.Locale)!;
            string lang = t.Language;
            string? script = t.Script, region = t.Region;
            if (IntlCore.RemoveLikelySubtags(ref lang, ref script, ref region)) { t.Language = lang; t.Script = script; t.Region = region; }
            return JsValue.FromObject(new JsLocale(a.Realm, a.Realm.Intl!.LocalePrototype, IntlCore.Canonicalize(IntlCore.Format(t))));
        });
        r.Method(proto, "toString", 0, static (in NativeArgs a) => new JsValue(IntlCore.This<JsLocale>(a, "Intl.Locale.prototype.toString").Locale));

        r.Getter(proto, "baseName", "baseName", static (in NativeArgs a) => new JsValue(IntlCore.This<JsLocale>(a, "baseName").Tag.BaseName()));
        r.Getter(proto, "calendar", "calendar", static (in NativeArgs a) => Str(IntlCore.This<JsLocale>(a, "calendar").Calendar));
        r.Getter(proto, "caseFirst", "caseFirst", static (in NativeArgs a) => Str(IntlCore.This<JsLocale>(a, "caseFirst").CaseFirst));
        r.Getter(proto, "collation", "collation", static (in NativeArgs a) => Str(IntlCore.This<JsLocale>(a, "collation").Collation));
        r.Getter(proto, "firstDayOfWeek", "firstDayOfWeek", static (in NativeArgs a) => Str(IntlCore.This<JsLocale>(a, "firstDayOfWeek").FirstDayOfWeek));
        r.Getter(proto, "hourCycle", "hourCycle", static (in NativeArgs a) => Str(IntlCore.This<JsLocale>(a, "hourCycle").HourCycle));
        r.Getter(proto, "numeric", "numeric", static (in NativeArgs a) => JsValue.FromBool(IntlCore.This<JsLocale>(a, "numeric").Numeric));
        r.Getter(proto, "numberingSystem", "numberingSystem", static (in NativeArgs a) => Str(IntlCore.This<JsLocale>(a, "numberingSystem").NumberingSystem));
        r.Getter(proto, "language", "language", static (in NativeArgs a) => new JsValue(IntlCore.This<JsLocale>(a, "language").Tag.Language));
        r.Getter(proto, "script", "script", static (in NativeArgs a) => Str(IntlCore.This<JsLocale>(a, "script").Tag.Script));
        r.Getter(proto, "region", "region", static (in NativeArgs a) => Str(IntlCore.This<JsLocale>(a, "region").Tag.Region));
        r.Getter(proto, "variants", "variants", static (in NativeArgs a) =>
        {
            var v = IntlCore.This<JsLocale>(a, "variants").Tag.Variants;
            return v.Count == 0 ? JsValue.Undefined : new JsValue(string.Join("-", v));
        });

        // Intl Locale Info
        r.Method(proto, "getCalendars", 0, static (in NativeArgs a) => Arr(a.Realm, GetCalendars(IntlCore.This<JsLocale>(a, "Intl.Locale.prototype.getCalendars"))));
        r.Method(proto, "getCollations", 0, static (in NativeArgs a) => Arr(a.Realm, GetCollations(IntlCore.This<JsLocale>(a, "Intl.Locale.prototype.getCollations"))));
        r.Method(proto, "getHourCycles", 0, static (in NativeArgs a) => Arr(a.Realm, GetHourCycles(IntlCore.This<JsLocale>(a, "Intl.Locale.prototype.getHourCycles"))));
        r.Method(proto, "getNumberingSystems", 0, static (in NativeArgs a) => Arr(a.Realm, GetNumberingSystems(IntlCore.This<JsLocale>(a, "Intl.Locale.prototype.getNumberingSystems"))));
        r.Method(proto, "getTimeZones", 0, static (in NativeArgs a) =>
        {
            var loc = IntlCore.This<JsLocale>(a, "Intl.Locale.prototype.getTimeZones");
            if (loc.Tag.Region == null) return JsValue.Undefined;
            return Arr(a.Realm, GetTimeZones(loc.Tag.Region));
        });
        r.Method(proto, "getTextInfo", 0, static (in NativeArgs a) =>
        {
            var loc = IntlCore.This<JsLocale>(a, "Intl.Locale.prototype.getTextInfo");
            var o = new JsObject(a.Realm, a.Realm.ObjectPrototype);
            o.CreateDataProperty("direction", new JsValue(Icu.IsRightToLeft(IntlCore.ToIcuLocale(loc.Tag.BaseName())) ? "rtl" : "ltr"));
            return JsValue.FromObject(o);
        });
        r.Method(proto, "getWeekInfo", 0, static (in NativeArgs a) =>
        {
            var loc = IntlCore.This<JsLocale>(a, "Intl.Locale.prototype.getWeekInfo");
            var realm = a.Realm;
            var icu = PreferenceLocale(loc);
            if (loc.FirstDayOfWeek is { } fw && s_weekdays.Contains(fw)) icu += "@fw=" + fw;
            Icu.WeekData(icu, out int first, out var weekend);
            var iso = new List<int>();
            foreach (var d in weekend) iso.Add(IsoDay(d));
            iso.Sort();
            var days = new List<JsValue>();
            foreach (var d in iso) days.Add(new JsValue((double)d));
            var o = new JsObject(realm, realm.ObjectPrototype);
            o.CreateDataProperty("firstDay", new JsValue((double)IsoDay(first)));
            o.CreateDataProperty("weekend", JsValue.FromObject(Operations.CreateArrayFromList(realm, days.ToArray())));
            return JsValue.FromObject(o);
        });
    }

    private static readonly HashSet<string> s_weekdays = new(StringComparer.Ordinal) { "mon", "tue", "wed", "thu", "fri", "sat", "sun" };

    /// <summary>ICU day (1 = Sunday) → ISO day (1 = Monday … 7 = Sunday).</summary>
    private static int IsoDay(int icuDay) => icuDay == 1 ? 7 : icuDay - 1;

    private static JsValue Str(string? s) => s == null ? JsValue.Undefined : new JsValue(s);

    private static JsValue Arr(Realm realm, List<string> items)
    {
        var vals = new JsValue[items.Count];
        for (int i = 0; i < vals.Length; i++) vals[i] = new JsValue(items[i]);
        return JsValue.FromObject(Operations.CreateArrayFromList(realm, vals));
    }

    /// <summary>The Intl.Locale constructor steps after obtaining tag and options: validation, canonicalization, UpdateLanguageId, MakeLocaleRecord.</summary>
    private static string CreateLocaleTag(Realm realm, string tag, JsObject options)
    {
        var t = IntlCore.Parse(tag) ?? throw JsErrors.RangeError(realm, $"Incorrect locale information provided: {tag}");
        IntlCore.CanonicalizeTag(t);
        var canonical = IntlCore.Format(t);
        var baseName = t.BaseName();

        // UpdateLanguageId
        var language = IntlCore.GetStringOption(realm, options, "language", null, null);
        if (language != null && !IntlCore.IsLanguageSubtag(language)) throw JsErrors.RangeError(realm, $"Invalid language: {language}");
        var script = IntlCore.GetStringOption(realm, options, "script", null, null);
        if (script != null && !IntlCore.IsScriptSubtag(script)) throw JsErrors.RangeError(realm, $"Invalid script: {script}");
        var region = IntlCore.GetStringOption(realm, options, "region", null, null);
        if (region != null && !IntlCore.IsRegionSubtag(region)) throw JsErrors.RangeError(realm, $"Invalid region: {region}");
        var variants = IntlCore.GetStringOption(realm, options, "variants", null, null);
        List<string>? variantList = null;
        if (variants != null)
        {
            if (variants.Length == 0) throw JsErrors.RangeError(realm, "Invalid variants");
            variantList = new List<string>(variants.ToLowerInvariant().Split('-'));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in variantList)
                if (!IntlCore.IsVariantSubtag(v) || !seen.Add(v)) throw JsErrors.RangeError(realm, $"Invalid variants: {variants}");
        }
        var sb = new System.Text.StringBuilder(language ?? t.Language);
        if ((script ?? t.Script) is { } s) sb.Append('-').Append(s);
        if ((region ?? t.Region) is { } rg) sb.Append('-').Append(rg);
        foreach (var v in variantList ?? t.Variants) sb.Append('-').Append(v);
        sb.Append(canonical, baseName.Length, canonical.Length - baseName.Length);
        var newTag = sb.ToString();

        // relevant extension keys
        string? calendar = IntlCore.GetStringOption(realm, options, "calendar", null, null);
        if (calendar != null && !IntlCore.IsUnicodeType(calendar)) throw JsErrors.RangeError(realm, $"Invalid calendar: {calendar}");
        string? collation = IntlCore.GetStringOption(realm, options, "collation", null, null);
        if (collation != null && !IntlCore.IsUnicodeType(collation)) throw JsErrors.RangeError(realm, $"Invalid collation: {collation}");
        string? fw = IntlCore.GetStringOption(realm, options, "firstDayOfWeek", null, null);
        if (fw != null)
        {
            fw = WeekdayToString(fw);
            if (!IntlCore.IsUnicodeType(fw)) throw JsErrors.RangeError(realm, $"Invalid firstDayOfWeek: {fw}");
        }
        string? hc = IntlCore.GetStringOption(realm, options, "hourCycle", s_hourCycles, null);
        string? kf = IntlCore.GetStringOption(realm, options, "caseFirst", s_caseFirsts, null);
        bool? knb = IntlCore.GetBoolOption(realm, options, "numeric", null);
        string? kn = knb == null ? null : knb.Value ? "true" : "false";
        string? nu = IntlCore.GetStringOption(realm, options, "numberingSystem", null, null);
        if (nu != null && !IntlCore.IsUnicodeType(nu)) throw JsErrors.RangeError(realm, $"Invalid numberingSystem: {nu}");

        // MakeLocaleRecord + InsertUnicodeExtensionAndCanonicalize
        var nt = IntlCore.Parse(newTag) ?? throw JsErrors.RangeError(realm, $"Incorrect locale information provided: {newTag}");
        Override(nt, "ca", calendar);
        Override(nt, "co", collation);
        Override(nt, "fw", fw);
        Override(nt, "hc", hc);
        Override(nt, "kf", kf);
        Override(nt, "kn", kn);
        Override(nt, "nu", nu);
        IntlCore.CanonicalizeTag(nt);
        return IntlCore.Format(nt);
    }

    private static void Override(LanguageTag t, string key, string? value)
    {
        if (value == null) return;
        value = value.ToLowerInvariant();
        for (int i = 0; i < t.UKeywords.Count; i++)
            if (t.UKeywords[i].Key == key) { t.UKeywords[i] = new(key, value); return; }
        t.UKeywords.Add(new(key, value));
    }

    /// <summary>WeekdayToString (Intl Locale Info).</summary>
    private static string WeekdayToString(string fw) => fw switch
    {
        "0" or "7" => "sun",
        "1" => "mon",
        "2" => "tue",
        "3" => "wed",
        "4" => "thu",
        "5" => "fri",
        "6" => "sat",
        _ => fw,
    };

    // ------------------------------------------------------------------ Locale Info helpers

    /// <summary>
    /// RegionPreference: the region used for region-dependent preferences — the "rg" override, else the region subtag,
    /// else the "sd" subdivision's region, else the likely region, else "001".
    /// </summary>
    private static string PreferredRegion(JsLocale loc)
    {
        string? fromKey(string key)
        {
            foreach (var kw in loc.Tag.UKeywords)
                if (kw.Key == key && kw.Value.Length >= 3)
                {
                    var v = kw.Value;
                    var reg = char.IsAsciiDigit(v[0]) ? v.Substring(0, 3) : v.Substring(0, 2).ToUpperInvariant();
                    return IntlCore.IsRegionSubtag(reg) ? reg : null;
                }
            return null;
        }
        if (fromKey("rg") is { } rg) return rg;
        if (loc.Tag.Region != null) return loc.Tag.Region;
        if (fromKey("sd") is { } sd) return sd;
        string lang = loc.Tag.Language;
        string? script = loc.Tag.Script, region = null;
        if (IntlCore.AddLikelySubtags(ref lang, ref script, ref region) && region != null) return region;
        return "001";
    }

    /// <summary>An ICU locale id (language, script, preferred region) for region-dependent preference lookups.</summary>
    private static string PreferenceLocale(JsLocale loc)
    {
        var t = loc.Tag;
        return t.Language + (t.Script != null ? "_" + t.Script : "") + "_" + PreferredRegion(loc);
    }

    private static List<string> GetCalendars(JsLocale loc)
    {
        if (loc.Calendar != null) return new List<string> { loc.Calendar };
        var result = new List<string>();
        foreach (var c in Icu.KeywordValuesForLocale(false, "calendar", PreferenceLocale(loc), true))
        {
            var bcp = Icu.ToUnicodeLocaleType("calendar", c) ?? c;
            if (bcp == "islamicc") bcp = "islamic-civil";
            else if (bcp == "ethiopic-amete-alem") bcp = "ethioaa";
            if (!result.Contains(bcp)) result.Add(bcp);
        }
        if (result.Count == 0) result.Add("gregory");
        return result;
    }

    private static List<string> GetCollations(JsLocale loc)
    {
        if (loc.Collation != null) return new List<string> { loc.Collation };
        var result = new List<string>();
        foreach (var c in Icu.KeywordValuesForLocale(true, "collation", IntlCore.ToIcuLocale(loc.Tag.BaseName()), true))
        {
            var bcp = Icu.ToUnicodeLocaleType("collation", c) ?? c;
            if (bcp is "standard" or "search") continue;
            if (!result.Contains(bcp)) result.Add(bcp);
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static List<string> GetHourCycles(JsLocale loc)
    {
        if (loc.HourCycle != null) return new List<string> { loc.HourCycle };
        int hc = Icu.DefaultHourCycle(PreferenceLocale(loc));
        return new List<string> { hc is >= 0 and <= 3 ? s_hourCycles[hc] : "h23" };
    }

    private static List<string> GetNumberingSystems(JsLocale loc)
    {
        if (loc.NumberingSystem != null) return new List<string> { loc.NumberingSystem };
        return new List<string> { Icu.LocaleDefaultNumberingSystem(IntlCore.ToIcuLocale(loc.Tag.BaseName())) ?? "latn" };
    }

    private static List<string> GetTimeZones(string region)
    {
        var result = new List<string>();
        foreach (var id in Icu.TimeZoneIds(2, region))
        {
            var iana = Icu.IanaTimeZoneId(id);
            if (!result.Contains(iana)) result.Add(iana);
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }
}
