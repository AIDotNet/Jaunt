using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace Jaunt.Runtime;

internal sealed partial class IntlProtos
{
    public JsObject PluralRulesPrototype = null!;
}

/// <summary>A cached ICU <c>UPluralRules</c> for one (locale, type) pair, freed by the finalizer.</summary>
internal sealed unsafe class IcuPluralRules
{
    public readonly nint Ptr;
    /// <summary>The plural categories in ECMA-402 order (zero, one, two, few, many, other).</summary>
    public readonly string[] Categories;

    private IcuPluralRules(nint ptr, string[] categories) { Ptr = ptr; Categories = categories; }

    ~IcuPluralRules()
    {
        if (Ptr != 0) IcuNum.uplrules_close(Ptr);
    }

    private static readonly ConcurrentDictionary<(string, int), IcuPluralRules> s_cache = new();
    private static readonly string[] s_order = { "zero", "one", "two", "few", "many", "other" };

    public static IcuPluralRules Get(string icuLocale, bool ordinal)
    {
        var key = (icuLocale, ordinal ? 1 : 0);
        if (s_cache.TryGetValue(key, out var r)) return r;
        var loc = Icu.Utf8Z(icuLocale);
        int err = 0;
        nint p;
        fixed (byte* l = loc) p = IcuNum.uplrules_openForType(l, ordinal ? 1 : 0, &err);
        if (Icu.Failure(err) || p == 0) throw new InvalidOperationException("ICU plural rules could not be created");
        int e2 = 0;
        var keywords = Icu.ReadUEnum(IcuNum.uplrules_getKeywords(p, &e2));
        var cats = new List<string>();
        foreach (var c in s_order) if (keywords.Contains(c)) cats.Add(c);
        var rules = new IcuPluralRules(p, cats.ToArray());
        if (s_cache.Count >= 256) s_cache.Clear();
        return s_cache.GetOrAdd(key, rules);   // a losing duplicate is freed by its finalizer
    }
}

/// <summary>An Intl.PluralRules instance ([[InitializedPluralRules]]).</summary>
internal sealed class JsPluralRules : JsObject
{
    public string Locale = "", Type = "cardinal", Notation = "standard";
    public string? CompactDisplay;
    public NumberDigitOptions Digits = null!;
    public IcuNumberFormatter Formatter = null!;
    public IcuPluralRules Rules = null!;

    public JsPluralRules(Realm realm, JsObject proto) : base(realm, proto) { }

    /// <summary>ResolvePlural: the plural category of a Number.</summary>
    public unsafe string Select(double n)
    {
        if (!double.IsFinite(n)) return "other";
        var result = IcuNumberFormatter.NumberResult;
        int err = 0;
        IcuNum.unumf_formatDouble(Formatter.Ptr, n, result, &err);
        char* buf = stackalloc char[16];
        int len = IcuNum.uplrules_selectFormatted(Rules.Ptr, result, buf, 16, &err);
        GC.KeepAlive(Formatter);
        GC.KeepAlive(Rules);
        if (Icu.Failure(err)) return "other";
        return Category(buf, len);
    }

    /// <summary>ResolvePluralRange.</summary>
    public unsafe string SelectRange(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return "other";
        var result = IcuNumberFormatter.RangeResult;
        int err = 0;
        IcuNum.unumrf_formatDoubleRange(Formatter.Range, x, y, result, &err);
        char* buf = stackalloc char[16];
        int len = IcuNum.uplrules_selectForRange(Rules.Ptr, result, buf, 16, &err);
        GC.KeepAlive(Formatter);
        GC.KeepAlive(Rules);
        if (Icu.Failure(err)) return "other";
        return Category(buf, len);
    }

    private static unsafe string Category(char* p, int len)
    {
        var span = new ReadOnlySpan<char>(p, len);
        return span switch
        {
            "zero" => "zero",
            "one" => "one",
            "two" => "two",
            "few" => "few",
            "many" => "many",
            _ => "other",
        };
    }
}

internal static partial class IntlBuiltins
{
    private static readonly string[] s_pluralTypes = { "cardinal", "ordinal" };

    static partial void SetupPluralRules(Realm r, JsObject intl, IntlProtos protos)
    {
        var proto = new JsObject(r, r.ObjectPrototype);
        protos.PluralRulesPrototype = proto;
        proto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("Intl.PluralRules"), PropFlags.Configurable);
        var ctor = r.Constructor("PluralRules", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (!a.IsConstruct) throw JsErrors.TypeError(realm, "Constructor Intl.PluralRules requires 'new'");
            var p = IntlNumberFormat.ProtoFromCtor(realm, a.NewTarget.AsObjectUnchecked, static x => x.PluralRulesPrototype);
            var pr = new JsPluralRules(realm, p);
            InitializePluralRules(realm, pr, a[0], a[1]);
            return JsValue.FromObject(pr);
        }, proto);
        intl.DefineHidden("PluralRules", JsValue.FromObject(ctor));
        r.Method(ctor, "supportedLocalesOf", 1, static (in NativeArgs a) => IntlCore.SupportedLocalesOf(a.Realm, a[0], a[1]));

        r.Method(proto, "select", 1, static (in NativeArgs a) =>
        {
            var pr = IntlCore.This<JsPluralRules>(a, "Intl.PluralRules.prototype.select");
            var v = a[0];
            double n = v.IsNumber ? v.Num : Operations.ToNumber(a.Realm, v);
            return new JsValue(pr.Select(n));
        });
        r.Method(proto, "selectRange", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var pr = IntlCore.This<JsPluralRules>(a, "Intl.PluralRules.prototype.selectRange");
            if (a[0].IsUndefined || a[1].IsUndefined) throw JsErrors.TypeError(realm, "start and end are required");
            double x = Operations.ToNumber(realm, a[0]);
            double y = Operations.ToNumber(realm, a[1]);
            if (double.IsNaN(x) || double.IsNaN(y)) throw JsErrors.RangeError(realm, "Range values must not be NaN");
            return new JsValue(pr.SelectRange(x, y));
        });
        r.Method(proto, "resolvedOptions", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var pr = IntlCore.This<JsPluralRules>(a, "Intl.PluralRules.prototype.resolvedOptions");
            var o = realm.NewObject();
            o.AddOwnProperty("locale", new JsValue(pr.Locale), PropFlags.Default);
            o.AddOwnProperty("type", new JsValue(pr.Type), PropFlags.Default);
            o.AddOwnProperty("notation", new JsValue(pr.Notation), PropFlags.Default);
            if (pr.CompactDisplay != null) o.AddOwnProperty("compactDisplay", new JsValue(pr.CompactDisplay), PropFlags.Default);
            pr.Digits.AddResolvedDigits(o);
            var cats = pr.Rules.Categories;
            var items = new JsValue[cats.Length];
            for (int i = 0; i < cats.Length; i++) items[i] = new JsValue(cats[i]);
            o.AddOwnProperty("pluralCategories", JsValue.FromObject(Operations.CreateArrayFromList(realm, items)), PropFlags.Default);
            pr.Digits.AddResolvedRounding(o);
            return JsValue.FromObject(o);
        });
    }

    private static readonly string[] s_pluralRelevantKeys = Array.Empty<string>();

    /// <summary>InitializePluralRules (17.1.2).</summary>
    private static void InitializePluralRules(Realm realm, JsPluralRules pr, in JsValue locales, in JsValue optionsArg)
    {
        var requested = IntlCore.CanonicalizeLocaleList(realm, locales);
        var options = IntlCore.CoerceOptionsToObject(realm, optionsArg);
        var matcher = IntlCore.GetStringOption(realm, options, "localeMatcher", IntlCore.s_matchers, "best fit");
        pr.Type = IntlCore.GetStringOption(realm, options, "type", s_pluralTypes, "cardinal")!;
        var notation = IntlCore.GetStringOption(realm, options, "notation", IntlNumberFormat.s_notations, "standard")!;
        pr.Notation = notation;
        var compactDisplay = IntlCore.GetStringOption(realm, options, "compactDisplay", IntlNumberFormat.s_compactDisplays, "short");
        if (notation == "compact") pr.CompactDisplay = compactDisplay;
        pr.Digits = NumberDigitOptions.Read(realm, options, 0, 3, notation);
        var r = IntlCore.ResolveLocale(realm, requested, matcher, new Dictionary<string, string?>(), s_pluralRelevantKeys,
            static (_, _) => Array.Empty<string>());
        pr.Locale = r["locale"]!;
        var icuLocale = IntlNumberFormat.IcuLocale(r["dataLocale"]!);
        var sb = new StringBuilder(48);
        pr.Digits.AppendSkeleton(sb);
        NumberFormatRecord.AppendNotation(sb, notation, pr.CompactDisplay);
        sb.Append(" group-off");
        pr.Formatter = IcuNumberFormatter.Get(icuLocale, sb.ToString()) ?? throw JsErrors.RangeError(realm, "Unsupported plural rules options");
        pr.Rules = IcuPluralRules.Get(icuLocale, pr.Type == "ordinal");
    }
}
