using System;
using System.Collections.Generic;

namespace Jaunt.Runtime;

internal static unsafe partial class Icu
{
    private static nint s_ucolOpen, s_ucolClose, s_ucolSetAttr, s_ucolGetAttr, s_ucolStrcoll, s_ucolKeywordValues,
        s_ulocToUnicodeType, s_ulocToLegacyType;

    public const int UCOL_ALTERNATE_HANDLING = 1, UCOL_CASE_FIRST = 2, UCOL_CASE_LEVEL = 3, UCOL_NORMALIZATION_MODE = 4,
        UCOL_STRENGTH = 5, UCOL_NUMERIC_COLLATION = 7;
    public const int UCOL_PRIMARY = 0, UCOL_SECONDARY = 1, UCOL_TERTIARY = 2, UCOL_OFF = 16, UCOL_ON = 17, UCOL_SHIFTED = 20,
        UCOL_NON_IGNORABLE = 21, UCOL_LOWER_FIRST = 24, UCOL_UPPER_FIRST = 25;

    public static nint ucol_open(byte* loc, int* err) => ((delegate* unmanaged<byte*, int*, nint>)P(ref s_ucolOpen, "ucol_open"))(loc, err);
    private static nint s_uStrToUpper, s_uStrToLower;
    public static nint u_strToUpper_ptr => P(ref s_uStrToUpper, "u_strToUpper");
    public static nint u_strToLower_ptr => P(ref s_uStrToLower, "u_strToLower");
    public static nint ucol_close_ptr => P(ref s_ucolClose, "ucol_close");
    public static void ucol_setAttribute(nint c, int attr, int value, int* err) =>
        ((delegate* unmanaged<nint, int, int, int*, void>)P(ref s_ucolSetAttr, "ucol_setAttribute"))(c, attr, value, err);
    public static int ucol_getAttribute(nint c, int attr, int* err) =>
        ((delegate* unmanaged<nint, int, int*, int>)P(ref s_ucolGetAttr, "ucol_getAttribute"))(c, attr, err);
    public static int ucol_strcoll(nint c, char* s, int sl, char* t, int tl) =>
        ((delegate* unmanaged<nint, char*, int, char*, int, int>)P(ref s_ucolStrcoll, "ucol_strcoll"))(c, s, sl, t, tl);
    public static nint ucol_getKeywordValuesForLocale(byte* key, byte* loc, sbyte commonly, int* err) =>
        ((delegate* unmanaged<byte*, byte*, sbyte, int*, nint>)P(ref s_ucolKeywordValues, "ucol_getKeywordValuesForLocale"))(key, loc, commonly, err);

    /// <summary>uloc_toUnicodeLocaleType: ICU keyword value → BCP 47 type (null when unknown).</summary>
    public static string? ToUnicodeLocaleType(string key, string value)
    {
        var k = Utf8Z(key);
        var v = Utf8Z(value);
        fixed (byte* kp = k) fixed (byte* vp = v)
            return FromCString(((delegate* unmanaged<byte*, byte*, byte*>)P(ref s_ulocToUnicodeType, "uloc_toUnicodeLocaleType"))(kp, vp));
    }

    /// <summary>uloc_toLegacyType: BCP 47 type → ICU keyword value (null when unknown).</summary>
    public static string? ToLegacyType(string key, string value)
    {
        var k = Utf8Z(key);
        var v = Utf8Z(value);
        fixed (byte* kp = k) fixed (byte* vp = v)
            return FromCString(((delegate* unmanaged<byte*, byte*, byte*>)P(ref s_ulocToLegacyType, "uloc_toLegacyType"))(kp, vp));
    }
}

/// <summary>
/// An ICU object owned by managed code: closes the native object (and frees an optional native buffer) when collected.
/// Objects kept in the shared caches are never released.
/// </summary>
internal sealed unsafe class IcuTextHandle
{
    public nint Ptr;
    private readonly nint _close;
    private nint _mem;

    public IcuTextHandle(nint ptr, nint closeFn, nint mem = 0)
    {
        Ptr = ptr;
        _close = closeFn;
        _mem = mem;
    }

    ~IcuTextHandle()
    {
        if (Ptr != 0 && _close != 0) ((delegate* unmanaged<nint, void>)_close)(Ptr);
        Ptr = 0;
        if (_mem != 0) System.Runtime.InteropServices.NativeMemory.Free((void*)_mem);
        _mem = 0;
    }

    /// <summary>Marks the handle as shared (kept alive for the process lifetime) so the finalizer does nothing.</summary>
    public IcuTextHandle Pin()
    {
        GC.SuppressFinalize(this);
        return this;
    }
}

/// <summary>Helpers shared by the text-oriented Intl services (Collator, Segmenter, ListFormat, DisplayNames, RelativeTimeFormat).</summary>
internal static class IntlText
{
    /// <summary>Upper bound for the process-wide caches of immutable ICU formatters.</summary>
    public const int CacheLimit = 256;

    /// <summary>GetPrototypeFromConstructor with an Intl intrinsic fallback taken from new.target's realm.</summary>
    public static JsObject ProtoFromNewTarget(in NativeArgs a, Func<IntlProtos, JsObject> select)
    {
        var realm = a.Realm;
        if (a.NewTarget.IsNotObject) return select(realm.Intl!);
        var nt = a.NewTarget.AsObjectUnchecked;
        if (ReferenceEquals(nt, a.Callee)) return select(realm.Intl!);
        var p = nt.Get("prototype");
        if (p.IsObject) return p.AsObjectUnchecked;
        var fr = Operations.GetFunctionRealm(realm, nt);
        return select(fr.Intl ?? realm.Intl!);
    }

    /// <summary>A prototype object with an @@toStringTag of <paramref name="tag"/>.</summary>
    public static JsObject Proto(Realm r, string tag)
    {
        var proto = new JsObject(r, r.ObjectPrototype);
        proto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue(tag), PropFlags.Configurable);
        return proto;
    }

    public static void RequireNew(in NativeArgs a, string name)
    {
        if (!a.IsConstruct) throw JsErrors.TypeError(a.Realm, $"Constructor Intl.{name} requires 'new'");
    }

    public static void Data(JsObject o, string key, JsValue v) => o.CreateDataPropertyOrThrow(key, v);

    public static readonly IReadOnlyList<string> NoData = Array.Empty<string>();
}

/// <summary>An Intl.Collator instance ([[InitializedCollator]]).</summary>
internal sealed class JsCollator : JsObject
{
    public string Locale = "";
    public string Usage = "sort";
    public string Sensitivity = "variant";
    public bool IgnorePunctuation;
    public string Collation = "default";
    public bool Numeric;
    public string CaseFirst = "false";
    public IcuTextHandle Handle = null!;
    public JsBuiltin? BoundCompare;
    public JsCollator(Realm realm, JsObject proto) : base(realm, proto) { }

    /// <summary>CompareStrings (10.3.3.2).</summary>
    public unsafe int Compare(string x, string y)
    {
        if (ReferenceEquals(x, y) || string.Equals(x, y, StringComparison.Ordinal)) return 0;
        int r;
        fixed (char* px = x) fixed (char* py = y)
            r = Icu.ucol_strcoll(Handle.Ptr, px, x.Length, py, y.Length);
        GC.KeepAlive(Handle);
        return r;
    }
}

/// <summary>Intl.Collator (ECMA-402 §10) on ICU's ucol API.</summary>
internal static partial class IntlCollator
{
    private static readonly string[] s_usages = { "sort", "search" };
    private static readonly string[] s_sensitivities = { "base", "accent", "case", "variant" };
    private static readonly string[] s_caseFirsts = { "upper", "lower", "false" };
    private static readonly string[] s_keys = { "co", "kf", "kn" };
    private static readonly string?[] s_bool = { "false", "true" };

    private static readonly Dictionary<string, IReadOnlyList<string>> s_collations = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (string CaseFirst, bool Shifted)> s_localeDefaults = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IcuTextHandle> s_cache = new(StringComparer.Ordinal);

    /// <summary>The supported collation types of a locale as BCP 47 values, after a leading null (the default).</summary>
    private static unsafe IReadOnlyList<string> Collations(string locale)
    {
        lock (s_collations)
            if (s_collations.TryGetValue(locale, out var c)) return c;
        var list = new List<string> { null! };
        var key = Icu.Utf8Z("collation");
        var loc = Icu.Utf8Z(IntlCore.ToIcuLocale(locale));
        int err = 0;
        nint en;
        fixed (byte* kp = key) fixed (byte* lp = loc) en = Icu.ucol_getKeywordValuesForLocale(kp, lp, 0, &err);
        if (!Icu.Failure(err))
        {
            foreach (var v in Icu.ReadEnum(en))
            {
                if (v is "standard" or "search" || v.StartsWith("private-", StringComparison.Ordinal)) continue;
                var t = Icu.ToUnicodeLocaleType("co", v) ?? v;
                if (!list.Contains(t)) list.Add(t);
            }
        }
        lock (s_collations) s_collations[locale] = list;
        return list;
    }

    /// <summary>The locale's default case-first setting and whether punctuation is ignored by default.</summary>
    private static unsafe (string CaseFirst, bool Shifted) LocaleDefaults(string locale)
    {
        lock (s_localeDefaults)
            if (s_localeDefaults.TryGetValue(locale, out var d)) return d;
        var loc = Icu.Utf8Z(IntlCore.ToIcuLocale(locale));
        int err = 0;
        nint c;
        fixed (byte* lp = loc) c = Icu.ucol_open(lp, &err);
        (string, bool) r = ("false", false);
        if (c != 0)
        {
            int e2 = 0;
            int cf = Icu.ucol_getAttribute(c, Icu.UCOL_CASE_FIRST, &e2);
            int alt = Icu.ucol_getAttribute(c, Icu.UCOL_ALTERNATE_HANDLING, &e2);
            r = (cf == Icu.UCOL_UPPER_FIRST ? "upper" : cf == Icu.UCOL_LOWER_FIRST ? "lower" : "false", alt == Icu.UCOL_SHIFTED);
            ((delegate* unmanaged<nint, void>)Icu.ucol_close_ptr)(c);
        }
        lock (s_localeDefaults) s_localeDefaults[locale] = r;
        return r;
    }

    private static IReadOnlyList<string> LocaleData(string locale, string key)
    {
        switch (key)
        {
            case "co": return Collations(locale);
            case "kn": return s_bool!;
            case "kf":
                var def = LocaleDefaults(locale).CaseFirst;
                return def switch { "upper" => new[] { "upper", "false", "lower" }, "lower" => new[] { "lower", "false", "upper" }, _ => new[] { "false", "lower", "upper" } };
        }
        return IntlText.NoData;
    }

    /// <summary>InitializeCollator (10.1.2).</summary>
    public static JsCollator Create(Realm realm, JsObject proto, in JsValue locales, in JsValue optionsArg)
    {
        var requested = IntlCore.CanonicalizeLocaleList(realm, locales);
        var options = IntlCore.CoerceOptionsToObject(realm, optionsArg);
        var usage = IntlCore.GetStringOption(realm, options, "usage", s_usages, "sort")!;
        var opt = new Dictionary<string, string?>(StringComparer.Ordinal);
        var matcher = IntlCore.GetStringOption(realm, options, "localeMatcher", IntlCore.s_matchers, "best fit");
        var collation = IntlCore.GetStringOption(realm, options, "collation", null, null);
        if (collation != null && !IntlCore.IsUnicodeType(collation)) throw JsErrors.RangeError(realm, $"Invalid collation: {collation}");
        opt["co"] = collation;
        var numeric = IntlCore.GetBoolOption(realm, options, "numeric", null);
        opt["kn"] = numeric == null ? null : numeric.Value ? "true" : "false";
        opt["kf"] = IntlCore.GetStringOption(realm, options, "caseFirst", s_caseFirsts, null);
        var r = IntlCore.ResolveLocale(realm, requested, matcher, opt, s_keys, LocaleData);
        var dataLocale = r["dataLocale"]!;
        var c = new JsCollator(realm, proto)
        {
            Usage = usage,
            Locale = r["locale"]!,
            Collation = r["co"] ?? "default",
            Numeric = r["kn"] == "true",
            CaseFirst = r["kf"] ?? "false",
        };
        var defaults = LocaleDefaults(dataLocale);
        c.Sensitivity = IntlCore.GetStringOption(realm, options, "sensitivity", s_sensitivities, "variant")!;
        c.IgnorePunctuation = IntlCore.GetBoolOption(realm, options, "ignorePunctuation", defaults.Shifted)!.Value;
        c.Handle = OpenCollator(realm, dataLocale, c);
        return c;
    }

    private static unsafe IcuTextHandle OpenCollator(Realm realm, string dataLocale, JsCollator c)
    {
        var icuLocale = IntlCore.ToIcuLocale(dataLocale);
        int at = icuLocale.IndexOf('@');
        if (at >= 0) icuLocale = icuLocale.Substring(0, at);
        if (c.Usage == "search") icuLocale += "@collation=search";
        else if (c.Collation != "default") icuLocale += "@collation=" + (Icu.ToLegacyType("co", c.Collation) ?? c.Collation);
        var key = $"{icuLocale}|{c.Sensitivity}|{c.IgnorePunctuation}|{c.Numeric}|{c.CaseFirst}";
        lock (s_cache)
            if (s_cache.TryGetValue(key, out var h)) return h;
        var loc = Icu.Utf8Z(icuLocale);
        int err = 0;
        nint coll;
        fixed (byte* lp = loc) coll = Icu.ucol_open(lp, &err);
        if (coll == 0 || Icu.Failure(err)) throw JsErrors.RangeError(realm, "Unable to create a collator for " + dataLocale);
        err = 0;
        int strength = c.Sensitivity switch { "base" or "case" => Icu.UCOL_PRIMARY, "accent" => Icu.UCOL_SECONDARY, _ => Icu.UCOL_TERTIARY };
        Icu.ucol_setAttribute(coll, Icu.UCOL_STRENGTH, strength, &err);
        Icu.ucol_setAttribute(coll, Icu.UCOL_CASE_LEVEL, c.Sensitivity == "case" ? Icu.UCOL_ON : Icu.UCOL_OFF, &err);
        Icu.ucol_setAttribute(coll, Icu.UCOL_ALTERNATE_HANDLING, c.IgnorePunctuation ? Icu.UCOL_SHIFTED : Icu.UCOL_NON_IGNORABLE, &err);
        Icu.ucol_setAttribute(coll, Icu.UCOL_NUMERIC_COLLATION, c.Numeric ? Icu.UCOL_ON : Icu.UCOL_OFF, &err);
        Icu.ucol_setAttribute(coll, Icu.UCOL_CASE_FIRST,
            c.CaseFirst == "upper" ? Icu.UCOL_UPPER_FIRST : c.CaseFirst == "lower" ? Icu.UCOL_LOWER_FIRST : Icu.UCOL_OFF, &err);
        // canonically equivalent strings must compare equal (10.3.3.2)
        Icu.ucol_setAttribute(coll, Icu.UCOL_NORMALIZATION_MODE, Icu.UCOL_ON, &err);
        var handle = new IcuTextHandle(coll, Icu.ucol_close_ptr);
        lock (s_cache)
        {
            if (s_cache.TryGetValue(key, out var existing)) return existing;   // handle is finalized
            if (s_cache.Count < IntlText.CacheLimit) s_cache[key] = handle.Pin();
        }
        return handle;
    }

    /// <summary>
    /// String.prototype.localeCompare (19.1.1): compares with a collator built from <paramref name="locales"/> and
    /// <paramref name="options"/> (the per-realm default collator when both are undefined).
    /// </summary>
    public static int LocaleCompare(Realm realm, string s, string t, in JsValue locales, in JsValue options)
    {
        var protos = realm.Intl!;
        JsCollator coll;
        if (locales.IsUndefined && options.IsUndefined)
        {
            coll = protos.DefaultCollator!;
            if (coll == null || protos.DefaultCollatorLocale != realm.DefaultLocale)
            {
                coll = protos.DefaultCollator = Create(realm, protos.CollatorPrototype, locales, options);
                protos.DefaultCollatorLocale = realm.DefaultLocale;
            }
        }
        else coll = Create(realm, protos.CollatorPrototype, locales, options);
        return coll.Compare(s, t);
    }

    private static readonly HashSet<string> s_caseLocales = new(StringComparer.Ordinal) { "az", "el", "lt", "tr" };

    /// <summary>TransformCase (19.1.2/19.1.3): locale-sensitive case mapping via ICU for az, el, lt and tr.</summary>
    public static unsafe string TransformCase(Realm realm, string s, in JsValue locales, bool upper)
    {
        string requested;
        if (locales.IsUndefined) requested = realm.DefaultLocale;
        else
        {
            var list = IntlCore.CanonicalizeLocaleList(realm, locales);
            requested = list.Count > 0 ? list[0] : realm.DefaultLocale;
        }
        var noExt = IntlCore.RemoveUnicodeExtension(requested, out _);
        var match = IntlCore.LookupMatch(s_caseLocales, new[] { noExt });
        if (match == null) return upper ? CaseMapping.ToUpper(s) : CaseMapping.ToLower(s);
        var loc = Icu.Utf8Z(match.Value.Locale);
        var fn = upper ? Icu.u_strToUpper_ptr : Icu.u_strToLower_ptr;
        var r = Icu.CallUChars((buf, cap, err) =>
        {
            fixed (char* sp = s) fixed (byte* lp = loc)
                return ((delegate* unmanaged<char*, int, char*, int, byte*, int*, int>)fn)(buf, cap, sp, s.Length, lp, err);
        });
        return r ?? (upper ? CaseMapping.ToUpper(s) : CaseMapping.ToLower(s));
    }

    internal static void Setup(Realm r, JsObject intl, IntlProtos protos)
    {
        var proto = IntlText.Proto(r, "Intl.Collator");
        protos.CollatorPrototype = proto;
        var ctor = r.Constructor("Collator", 0, static (in NativeArgs a) =>
        {
            var proto = IntlText.ProtoFromNewTarget(a, static p => p.CollatorPrototype);
            return JsValue.FromObject(Create(a.Realm, proto, a[0], a[1]));
        }, proto);
        intl.DefineHidden("Collator", JsValue.FromObject(ctor));
        r.Method(ctor, "supportedLocalesOf", 1, static (in NativeArgs a) => IntlCore.SupportedLocalesOf(a.Realm, a[0], a[1]));
        r.Getter(proto, "compare", "compare", static (in NativeArgs a) =>
        {
            var c = IntlCore.This<JsCollator>(a, "Intl.Collator.prototype.compare");
            if (c.BoundCompare == null)
            {
                c.BoundCompare = new JsBuiltin(a.Realm, "", 2, static (in NativeArgs b) =>
                {
                    var coll = (JsCollator)b.Callee.State!;
                    var x = b[0].IsString ? b[0].AsStringUnchecked : Operations.ToString(b.Realm, b[0]);
                    var y = b[1].IsString ? b[1].AsStringUnchecked : Operations.ToString(b.Realm, b[1]);
                    return new JsValue(coll.Compare(x, y));
                }) { State = c };
            }
            return JsValue.FromObject(c.BoundCompare);
        });
        r.Method(proto, "resolvedOptions", 0, static (in NativeArgs a) =>
        {
            var c = IntlCore.This<JsCollator>(a, "Intl.Collator.prototype.resolvedOptions");
            var o = new JsObject(a.Realm, a.Realm.ObjectPrototype);
            IntlText.Data(o, "locale", new JsValue(c.Locale));
            IntlText.Data(o, "usage", new JsValue(c.Usage));
            IntlText.Data(o, "sensitivity", new JsValue(c.Sensitivity));
            IntlText.Data(o, "ignorePunctuation", c.IgnorePunctuation ? JsValue.True : JsValue.False);
            IntlText.Data(o, "collation", new JsValue(c.Collation));
            IntlText.Data(o, "numeric", c.Numeric ? JsValue.True : JsValue.False);
            IntlText.Data(o, "caseFirst", new JsValue(c.CaseFirst));
            return JsValue.FromObject(o);
        });
    }
}

internal sealed partial class IntlProtos
{
    public JsObject CollatorPrototype = null!;
    /// <summary>Collator used by String.prototype.localeCompare without locales and options.</summary>
    public JsCollator? DefaultCollator;
    public string? DefaultCollatorLocale;
}

internal static partial class IntlBuiltins
{
    static partial void SetupCollator(Realm r, JsObject intl, IntlProtos protos) => IntlCollator.Setup(r, intl, protos);
}
