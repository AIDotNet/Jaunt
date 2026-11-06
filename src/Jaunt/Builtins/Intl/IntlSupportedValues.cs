using System;
using System.Collections.Generic;

namespace Jaunt.Runtime;

internal static unsafe partial class Icu
{
    private static nint s_ucolKeywordValuesAll, s_isoCurrencies, s_numsysNames, s_numsysByName, s_numsysAlgorithmic, s_numsysRadix, s_numsysDesc;

    /// <summary>ucol_getKeywordValues: all collation types (ICU legacy names).</summary>
    public static List<string> CollationKeywordValues()
    {
        var k = Utf8Z("collation");
        int err = 0;
        nint en;
        fixed (byte* kp = k) en = ((delegate* unmanaged<byte*, int*, nint>)P(ref s_ucolKeywordValuesAll, "ucol_getKeywordValues"))(kp, &err);
        return Failure(err) ? new List<string>() : ReadEnum(en);
    }

    /// <summary>ucurr_openISOCurrencies with the given UCurrCurrencyType mask.</summary>
    public static List<string> IsoCurrencies(uint mask)
    {
        int err = 0;
        var en = ((delegate* unmanaged<uint, int*, nint>)P(ref s_isoCurrencies, "ucurr_openISOCurrencies"))(mask, &err);
        return Failure(err) ? new List<string>() : ReadEnum(en);
    }

    /// <summary>The numbering systems with a simple (non-algorithmic, radix 10) digit mapping, with their ten digits.</summary>
    public static List<(string Name, string Digits)> SimpleNumberingSystems()
    {
        var result = new List<(string, string)>();
        int err = 0;
        var en = ((delegate* unmanaged<int*, nint>)P(ref s_numsysNames, "unumsys_openAvailableNames"))(&err);
        if (Failure(err)) return result;
        var byName = (delegate* unmanaged<byte*, int*, nint>)P(ref s_numsysByName, "unumsys_openByName");
        var isAlg = (delegate* unmanaged<nint, sbyte>)P(ref s_numsysAlgorithmic, "unumsys_isAlgorithmic");
        var radix = (delegate* unmanaged<nint, int>)P(ref s_numsysRadix, "unumsys_getRadix");
        var desc = (delegate* unmanaged<nint, char*, int, int*, int>)P(ref s_numsysDesc, "unumsys_getDescription");
        var close = (delegate* unmanaged<nint, void>)P(ref s_unumsysClose_L, "unumsys_close");
        foreach (var name in ReadEnum(en))
        {
            var n = Utf8Z(name);
            int e = 0;
            nint ns;
            fixed (byte* np = n) ns = byName(np, &e);
            if (Failure(e) || ns == 0) continue;
            try
            {
                if (isAlg(ns) != 0 || radix(ns) != 10) continue;
                var digits = CallUChars((buf, cap, er) => desc(ns, buf, cap, er));
                if (digits == null) continue;
                int count = 0;
                for (int i = 0; i < digits.Length; i++) { if (char.IsHighSurrogate(digits[i])) i++; count++; }
                if (count == 10) result.Add((name, digits));
            }
            finally { close(ns); }
        }
        return result;
    }
}

internal static partial class IntlCore
{
    /// <summary>
    /// Calendars supported by Intl (AvailableCalendars): the calendars required by the Intl era/monthCode rules, all of
    /// which ICU implements. Sorted.
    /// </summary>
    public static readonly string[] SupportedCalendars =
    {
        "buddhist", "chinese", "coptic", "dangi", "ethioaa", "ethiopic", "gregory", "hebrew", "indian", "islamic-civil",
        "islamic-tbla", "islamic-umalqura", "iso8601", "japanese", "persian", "roc",
    };

    /// <summary>Sanctioned single unit identifiers (ECMA-402 table "Simple units sanctioned for use"). Sorted.</summary>
    public static readonly string[] SanctionedUnits =
    {
        "acre", "bit", "byte", "celsius", "centimeter", "day", "degree", "fahrenheit", "fluid-ounce", "foot", "gallon",
        "gigabit", "gigabyte", "gram", "hectare", "hour", "inch", "kilobit", "kilobyte", "kilogram", "kilometer", "liter",
        "megabit", "megabyte", "meter", "microsecond", "mile", "mile-scandinavian", "milliliter", "millimeter",
        "millisecond", "minute", "month", "nanosecond", "ounce", "percent", "petabyte", "pound", "second", "stone",
        "terabit", "terabyte", "week", "yard", "year",
    };

    private static string[]? s_collations, s_currencies, s_numberingSystems, s_timeZones;
    private static Dictionary<string, string>? s_numberingSystemDigits;

    /// <summary>Collation types (BCP 47) available in ICU, excluding "standard" and "search". Sorted.</summary>
    public static string[] SupportedCollations => s_collations ??= LoadCollations();

    /// <summary>ISO 4217 currency codes (common, non-deprecated) known to ICU. Sorted.</summary>
    public static string[] SupportedCurrencies => s_currencies ??= LoadCurrencies();

    /// <summary>Numbering systems with simple digit mappings. Sorted.</summary>
    public static string[] SupportedNumberingSystems => s_numberingSystems ??= LoadNumberingSystems();

    /// <summary>The ten digits of each supported numbering system (keyed by BCP 47 name).</summary>
    public static IReadOnlyDictionary<string, string> NumberingSystemDigits
    {
        get
        {
            if (s_numberingSystemDigits == null) LoadNumberingSystems();
            return s_numberingSystemDigits!;
        }
    }

    /// <summary>Primary IANA time zone identifiers (AvailableNamedTimeZoneIdentifiers, primary only) with "UTC". Sorted.</summary>
    public static string[] SupportedTimeZones => s_timeZones ??= LoadTimeZones();

    private static string[] LoadCollations()
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var c in Icu.CollationKeywordValues())
        {
            var bcp = Icu.ToUnicodeLocaleType("collation", c) ?? c;
            if (bcp is "standard" or "search" || !IsUnicodeType(bcp)) continue;
            set.Add(bcp);
        }
        return new List<string>(set).ToArray();
    }

    private static string[] LoadCurrencies()
    {
        // every code Intl.DisplayNames can name (so the two agree), including historic currencies
        var set = new SortedSet<string>(StringComparer.Ordinal);
        Span<char> code = stackalloc char[3];
        for (char a = 'A'; a <= 'Z'; a++)
            for (char b = 'A'; b <= 'Z'; b++)
                for (char c = 'A'; c <= 'Z'; c++)
                {
                    code[0] = a; code[1] = b; code[2] = c;
                    var s = new string(code);
                    if (IntlDisplayNames.CurrencyName("en", s, "long") != null) set.Add(s);
                }
        return new List<string>(set).ToArray();
    }

    private static string[] LoadNumberingSystems()
    {
        var digits = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, d) in Icu.SimpleNumberingSystems()) digits[name] = d;
        s_numberingSystemDigits = digits;
        var names = new List<string>(digits.Keys);
        names.Sort(StringComparer.Ordinal);
        return names.ToArray();
    }

    private static string[] LoadTimeZones()
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var id in Icu.TimeZoneIds(1 /* UCAL_ZONE_TYPE_CANONICAL */, null))
        {
            if (id.StartsWith("SystemV/", StringComparison.Ordinal)) continue;   // ICU-only legacy ids, not in the IANA database
            var iana = Icu.IanaTimeZoneId(id);
            if (iana is "Etc/UTC" or "Etc/GMT" or "GMT" or "Etc/Unknown") iana = "UTC";
            set.Add(TemporalTimeZones.PrimaryOfLegacyZone(iana));
        }
        set.Add("UTC");
        return new List<string>(set).ToArray();
    }
}

internal static partial class IntlBuiltins
{
    static partial void SetupSupportedValuesOf(Realm r, JsObject intl)
    {
        r.Method(intl, "supportedValuesOf", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var key = Operations.ToString(realm, a[0]);
            string[] list = key switch
            {
                "calendar" => IntlCore.SupportedCalendars,
                "collation" => IntlCore.SupportedCollations,
                "currency" => IntlCore.SupportedCurrencies,
                "numberingSystem" => IntlCore.SupportedNumberingSystems,
                "timeZone" => IntlCore.SupportedTimeZones,
                "unit" => IntlCore.SanctionedUnits,
                _ => throw JsErrors.RangeError(realm, $"Invalid key: {key}"),
            };
            var vals = new JsValue[list.Length];
            for (int i = 0; i < vals.Length; i++) vals[i] = new JsValue(list[i]);
            return JsValue.FromObject(Operations.CreateArrayFromList(realm, vals));
        });
    }
}
