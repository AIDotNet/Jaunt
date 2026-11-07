using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Jaunt.Runtime;

internal sealed partial class IntlProtos
{
    public JsObject NumberFormatPrototype = null!;
    public JsBuiltin NumberFormatConstructor = null!;
}

/// <summary>
/// An Intl mathematical value (ECMA-402 15.5.16 ToIntlMathematicalValue): either a Number (including NaN, ±∞ and -0) or an
/// exact finite decimal (from a BigInt or a numeric string) kept as an ASCII string in a form ICU's decimal parser accepts.
/// </summary>
internal readonly struct IntlMathValue
{
    public readonly double Number;
    /// <summary>Exact decimal digits ("-123.45E-7"), or null when the value is <see cref="Number"/>.</summary>
    public readonly string? Decimal;

    public IntlMathValue(double number) { Number = double.IsNaN(number) ? IntlNumberFormat.CanonicalNaN : number; Decimal = null; }
    public IntlMathValue(string dec) { Number = 0; Decimal = dec; }

    public bool IsNaN => Decimal == null && double.IsNaN(Number);
    public bool IsNegative => Decimal != null ? Decimal[0] == '-' : double.IsNegative(Number) && !double.IsNaN(Number);
    public bool IsInfinity => Decimal == null && double.IsInfinity(Number);

    /// <summary>The value as a decimal string ICU accepts (finite values only).</summary>
    public string ToDecimalString() => Decimal ?? (Number == 0 && double.IsNegative(Number) ? "-0" : Number.ToString("R", CultureInfo.InvariantCulture));
}

internal enum RoundingType : byte { FractionDigits, SignificantDigits, MorePrecision, LessPrecision }

/// <summary>The digit options shared by NumberFormat and PluralRules (SetNumberFormatDigitOptions, 15.1.3).</summary>
internal sealed class NumberDigitOptions
{
    public int MinimumIntegerDigits = 1;
    public int MinimumFractionDigits, MaximumFractionDigits, MinimumSignificantDigits, MaximumSignificantDigits;
    public RoundingType RoundingType;
    public string ComputedRoundingPriority = "auto";
    public int RoundingIncrement = 1;
    public string RoundingMode = "halfExpand";
    public string TrailingZeroDisplay = "auto";

    private static readonly string[] s_roundingModes = { "ceil", "floor", "expand", "trunc", "halfCeil", "halfFloor", "halfExpand", "halfTrunc", "halfEven" };
    private static readonly string[] s_roundingPriorities = { "auto", "morePrecision", "lessPrecision" };
    private static readonly string[] s_trailingZeroDisplays = { "auto", "stripIfInteger" };
    private static readonly int[] s_increments = { 1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 2500, 5000 };

    /// <summary>SetNumberFormatDigitOptions.</summary>
    public static NumberDigitOptions Read(Realm realm, JsObject options, int mnfdDefault, int mxfdDefault, string notation)
    {
        var d = new NumberDigitOptions();
        int mnid = IntlCore.GetNumberOption(realm, options, "minimumIntegerDigits", 1, 21, 1)!.Value;
        var mnfd = options.Get("minimumFractionDigits");
        var mxfd = options.Get("maximumFractionDigits");
        var mnsd = options.Get("minimumSignificantDigits");
        var mxsd = options.Get("maximumSignificantDigits");
        d.MinimumIntegerDigits = mnid;
        int roundingIncrement = IntlCore.GetNumberOption(realm, options, "roundingIncrement", 1, 5000, 1)!.Value;
        if (Array.IndexOf(s_increments, roundingIncrement) < 0) throw JsErrors.RangeError(realm, "roundingIncrement value is out of range");
        var roundingMode = IntlCore.GetStringOption(realm, options, "roundingMode", s_roundingModes, "halfExpand")!;
        var roundingPriority = IntlCore.GetStringOption(realm, options, "roundingPriority", s_roundingPriorities, "auto")!;
        var trailingZeroDisplay = IntlCore.GetStringOption(realm, options, "trailingZeroDisplay", s_trailingZeroDisplays, "auto")!;
        if (roundingIncrement != 1) mxfdDefault = mnfdDefault;
        d.RoundingIncrement = roundingIncrement;
        d.RoundingMode = roundingMode;
        d.TrailingZeroDisplay = trailingZeroDisplay;
        bool hasSd = !mnsd.IsUndefined || !mxsd.IsUndefined;
        bool hasFd = !mnfd.IsUndefined || !mxfd.IsUndefined;
        bool needSd = true, needFd = true;
        if (roundingPriority == "auto")
        {
            needSd = hasSd;
            if (needSd || (!hasFd && notation == "compact")) needFd = false;
        }
        if (needSd)
        {
            if (hasSd)
            {
                int mn = IntlCore.DefaultNumberOption(realm, mnsd, 1, 21, 1, "minimumSignificantDigits")!.Value;
                int mx = IntlCore.DefaultNumberOption(realm, mxsd, mn, 21, 21, "maximumSignificantDigits")!.Value;
                d.MinimumSignificantDigits = mn;
                d.MaximumSignificantDigits = mx;
            }
            else
            {
                d.MinimumSignificantDigits = 1;
                d.MaximumSignificantDigits = 21;
            }
        }
        if (needFd)
        {
            if (hasFd)
            {
                int? mn = IntlCore.DefaultNumberOption(realm, mnfd, 0, 100, null, "minimumFractionDigits");
                int? mx = IntlCore.DefaultNumberOption(realm, mxfd, 0, 100, null, "maximumFractionDigits");
                if (mn == null) mn = Math.Min(mnfdDefault, mx!.Value);
                else if (mx == null) mx = Math.Max(mxfdDefault, mn.Value);
                else if (mn.Value > mx.Value) throw JsErrors.RangeError(realm, "minimumFractionDigits is greater than maximumFractionDigits");
                d.MinimumFractionDigits = mn.Value;
                d.MaximumFractionDigits = mx!.Value;
            }
            else
            {
                d.MinimumFractionDigits = mnfdDefault;
                d.MaximumFractionDigits = mxfdDefault;
            }
        }
        if (!needSd && !needFd)
        {
            d.MinimumFractionDigits = 0;
            d.MaximumFractionDigits = 0;
            d.MinimumSignificantDigits = 1;
            d.MaximumSignificantDigits = 2;
            d.RoundingType = RoundingType.MorePrecision;
            d.ComputedRoundingPriority = "morePrecision";
        }
        else if (roundingPriority == "auto")
        {
            d.RoundingType = needSd ? RoundingType.SignificantDigits : RoundingType.FractionDigits;
            d.ComputedRoundingPriority = "auto";
        }
        else
        {
            d.RoundingType = roundingPriority == "morePrecision" ? RoundingType.MorePrecision : RoundingType.LessPrecision;
            d.ComputedRoundingPriority = roundingPriority;
        }
        if (roundingIncrement != 1)
        {
            if (d.RoundingType != RoundingType.FractionDigits) throw JsErrors.TypeError(realm, "roundingIncrement requires fraction digit rounding");
            if (d.MaximumFractionDigits != d.MinimumFractionDigits) throw JsErrors.RangeError(realm, "roundingIncrement requires maximumFractionDigits to equal minimumFractionDigits");
        }
        return d;
    }

    public bool HasFractionDigits => RoundingType != RoundingType.SignificantDigits;
    public bool HasSignificantDigits => RoundingType != RoundingType.FractionDigits;

    /// <summary>Appends the ICU skeleton stems for the precision, rounding mode and integer width.</summary>
    public void AppendSkeleton(StringBuilder sb)
    {
        if (RoundingIncrement != 1)
        {
            sb.Append("precision-increment/");
            AppendIncrement(sb, RoundingIncrement, MaximumFractionDigits);
        }
        else
        {
            switch (RoundingType)
            {
                case RoundingType.FractionDigits:
                    AppendFraction(sb);
                    break;
                case RoundingType.SignificantDigits:
                    AppendSignificant(sb);
                    break;
                default:
                    AppendFraction(sb);
                    sb.Append('/');
                    AppendSignificant(sb);
                    sb.Append(RoundingType == RoundingType.MorePrecision ? 'r' : 's');
                    break;
            }
        }
        if (TrailingZeroDisplay == "stripIfInteger") sb.Append("/w");
        sb.Append(' ').Append(RoundingMode switch
        {
            "ceil" => "rounding-mode-ceiling",
            "floor" => "rounding-mode-floor",
            "expand" => "rounding-mode-up",
            "trunc" => "rounding-mode-down",
            "halfCeil" => "rounding-mode-half-ceiling",
            "halfFloor" => "rounding-mode-half-floor",
            "halfTrunc" => "rounding-mode-half-down",
            "halfEven" => "rounding-mode-half-even",
            _ => "rounding-mode-half-up",
        });
        if (MinimumIntegerDigits > 1) sb.Append(" integer-width/*").Append('0', MinimumIntegerDigits);
    }

    private void AppendFraction(StringBuilder sb)
    {
        sb.Append('.').Append('0', MinimumFractionDigits).Append('#', MaximumFractionDigits - MinimumFractionDigits);
    }

    private void AppendSignificant(StringBuilder sb)
    {
        sb.Append('@', MinimumSignificantDigits).Append('#', MaximumSignificantDigits - MinimumSignificantDigits);
    }

    private static void AppendIncrement(StringBuilder sb, int increment, int fractionDigits)
    {
        var digits = increment.ToString(CultureInfo.InvariantCulture);
        if (fractionDigits == 0) { sb.Append(digits); return; }
        if (digits.Length <= fractionDigits) digits = new string('0', fractionDigits - digits.Length + 1) + digits;
        sb.Append(digits, 0, digits.Length - fractionDigits).Append('.').Append(digits, digits.Length - fractionDigits, fractionDigits);
    }

    /// <summary>Adds the digit properties of resolvedOptions (minimumIntegerDigits … maximumSignificantDigits).</summary>
    public void AddResolvedDigits(JsObject o)
    {
        o.AddOwnProperty("minimumIntegerDigits", new JsValue(MinimumIntegerDigits), PropFlags.Default);
        if (HasFractionDigits)
        {
            o.AddOwnProperty("minimumFractionDigits", new JsValue(MinimumFractionDigits), PropFlags.Default);
            o.AddOwnProperty("maximumFractionDigits", new JsValue(MaximumFractionDigits), PropFlags.Default);
        }
        if (HasSignificantDigits)
        {
            o.AddOwnProperty("minimumSignificantDigits", new JsValue(MinimumSignificantDigits), PropFlags.Default);
            o.AddOwnProperty("maximumSignificantDigits", new JsValue(MaximumSignificantDigits), PropFlags.Default);
        }
    }

    /// <summary>Adds roundingIncrement … trailingZeroDisplay of resolvedOptions.</summary>
    public void AddResolvedRounding(JsObject o)
    {
        o.AddOwnProperty("roundingIncrement", new JsValue(RoundingIncrement), PropFlags.Default);
        o.AddOwnProperty("roundingMode", new JsValue(RoundingMode), PropFlags.Default);
        o.AddOwnProperty("roundingPriority", new JsValue(ComputedRoundingPriority), PropFlags.Default);
        o.AddOwnProperty("trailingZeroDisplay", new JsValue(TrailingZeroDisplay), PropFlags.Default);
    }
}

/// <summary>One element of a formatToParts / formatRangeToParts result.</summary>
internal readonly record struct NumberPart(string Type, string Value, string? Source);

/// <summary>
/// The resolved options of an Intl.NumberFormat (its internal slots) plus the cached ICU formatter. Immutable after creation and
/// realm-independent, so records are shared (e.g. by the toLocaleString caches).
/// </summary>
internal sealed class NumberFormatRecord
{
    public string Locale = "", DataLocale = "", NumberingSystem = "latn";
    public string Style = "decimal";
    public string? Currency, CurrencyDisplay, CurrencySign, Unit, UnitDisplay;
    public NumberDigitOptions Digits = null!;
    public string Notation = "standard";
    public string? CompactDisplay;
    /// <summary>"always", "auto", "min2", or null for false.</summary>
    public string? UseGrouping = "auto";
    public string SignDisplay = "auto";
    public IcuNumberFormatter Formatter = null!;

    public string BuildSkeleton()
    {
        var sb = new StringBuilder(64);
        switch (Style)
        {
            case "percent": sb.Append("percent scale/100 "); break;
            case "currency":
                sb.Append("currency/").Append(Currency).Append(' ');
                sb.Append(CurrencyDisplay switch
                {
                    "code" => "unit-width-iso-code ",
                    "narrowSymbol" => "unit-width-narrow ",
                    "name" => "unit-width-full-name ",
                    _ => "unit-width-short ",
                });
                break;
            case "unit":
                sb.Append("unit/").Append(Unit).Append(' ');
                sb.Append(UnitDisplay switch
                {
                    "narrow" => "unit-width-narrow ",
                    "long" => "unit-width-full-name ",
                    _ => "unit-width-short ",
                });
                break;
        }
        Digits.AppendSkeleton(sb);
        AppendNotation(sb, Notation, CompactDisplay);
        sb.Append(UseGrouping switch
        {
            null => " group-off",
            "min2" => " group-min2",
            "always" => " group-on-aligned",
            _ => " group-auto",
        });
        bool accounting = CurrencySign == "accounting";
        sb.Append(' ').Append(SignDisplay switch
        {
            "never" => "sign-never",
            "always" => accounting ? "sign-accounting-always" : "sign-always",
            "exceptZero" => accounting ? "sign-accounting-except-zero" : "sign-except-zero",
            "negative" => accounting ? "sign-accounting-negative" : "sign-negative",
            _ => accounting ? "sign-accounting" : "sign-auto",
        });
        sb.Append(" numbering-system/").Append(NumberingSystem);
        return sb.ToString();
    }

    public static void AppendNotation(StringBuilder sb, string notation, string? compactDisplay)
    {
        switch (notation)
        {
            case "scientific": sb.Append(" scientific"); break;
            case "engineering": sb.Append(" engineering"); break;
            case "compact": sb.Append(compactDisplay == "long" ? " compact-long" : " compact-short"); break;
        }
    }

    // ------------------------------------------------------------------ formatting

    /// <summary>Formats into the per-thread ICU result and returns its UFormattedValue.</summary>
    internal unsafe nint FormatToValue(in IntlMathValue x)
    {
        var result = IcuNumberFormatter.NumberResult;
        int err = 0;
        IntlNumberFormat.FormatInto(Formatter.Ptr, x, result, &err);
        if (Icu.Failure(err)) throw Icu.Error("ICU number formatting failed (" + err + ")");
        var v = IcuNum.unumf_resultAsValue(result, &err);
        GC.KeepAlive(Formatter);
        return v;
    }

    /// <summary>FormatNumeric: the formatted string.</summary>
    public unsafe string Format(in IntlMathValue x)
    {
        var v = FormatToValue(x);
        int len = 0, err = 0;
        var s = IcuNum.ufmtval_getString(v, &len, &err);
        return new string(s, 0, len);
    }

    /// <summary>Fast path: formats a Number.</summary>
    public unsafe string Format(double x)
    {
        if (double.IsNaN(x)) x = IntlNumberFormat.CanonicalNaN;
        var result = IcuNumberFormatter.NumberResult;
        int err = 0;
        IcuNum.unumf_formatDouble(Formatter.Ptr, x, result, &err);
        var v = IcuNum.unumf_resultAsValue(result, &err);
        int len = 0;
        var s = IcuNum.ufmtval_getString(v, &len, &err);
        GC.KeepAlive(Formatter);
        if (Icu.Failure(err)) throw Icu.Error("ICU number formatting failed (" + err + ")");
        return new string(s, 0, len);
    }

    public List<NumberPart> FormatToParts(in IntlMathValue x) => IntlNumberFormat.ReadParts(FormatToValue(x), this, x, x, false);

    internal unsafe nint FormatRangeToValue(in IntlMathValue x, in IntlMathValue y)
    {
        var range = Formatter.Range;
        var result = IcuNumberFormatter.RangeResult;
        int err = 0;
        if (x.Decimal == null && y.Decimal == null)
            IcuNum.unumrf_formatDoubleRange(range, x.Number, y.Number, result, &err);
        else if (x.IsInfinity || y.IsInfinity)
            IcuNum.unumrf_formatDoubleRange(range, IntlNumberFormat.ToDouble(x), IntlNumberFormat.ToDouble(y), result, &err);
        else
        {
            var a = x.ToDecimalString();
            var b = y.ToDecimalString();
            byte* pa = stackalloc byte[a.Length];
            byte* pb = stackalloc byte[b.Length];
            for (int i = 0; i < a.Length; i++) pa[i] = (byte)a[i];
            for (int i = 0; i < b.Length; i++) pb[i] = (byte)b[i];
            IcuNum.unumrf_formatDecimalRange(range, pa, a.Length, pb, b.Length, result, &err);
        }
        if (Icu.Failure(err)) throw Icu.Error("ICU number range formatting failed (" + err + ")");
        var v = IcuNum.unumrf_resultAsValue(result, &err);
        GC.KeepAlive(Formatter);
        return v;
    }

    public unsafe string FormatRange(in IntlMathValue x, in IntlMathValue y)
    {
        var v = FormatRangeToValue(x, y);
        int len = 0, err = 0;
        var s = IcuNum.ufmtval_getString(v, &len, &err);
        return new string(s, 0, len);
    }

    public List<NumberPart> FormatRangeToParts(in IntlMathValue x, in IntlMathValue y) => IntlNumberFormat.ReadParts(FormatRangeToValue(x, y), this, x, y, true);
}

/// <summary>An Intl.NumberFormat instance ([[InitializedNumberFormat]]).</summary>
internal sealed class JsNumberFormat : JsObject
{
    public readonly NumberFormatRecord Record;
    public JsBuiltin? BoundFormat;
    public JsNumberFormat(Realm realm, JsObject proto, NumberFormatRecord record) : base(realm, proto) => Record = record;
}

/// <summary>Intl.NumberFormat (ECMA-402 §16) and the internal API other Intl services use to format numbers.</summary>
internal static class IntlNumberFormat
{
    private static readonly string[] s_styles = { "decimal", "percent", "currency", "unit" };
    private static readonly string[] s_currencyDisplays = { "code", "symbol", "narrowSymbol", "name" };
    private static readonly string[] s_currencySigns = { "standard", "accounting" };
    private static readonly string[] s_unitDisplays = { "short", "narrow", "long" };
    internal static readonly string[] s_notations = { "standard", "scientific", "engineering", "compact" };
    internal static readonly string[] s_compactDisplays = { "short", "long" };
    private static readonly string[] s_useGroupings = { "min2", "auto", "always", "true", "false" };
    private static readonly string[] s_signDisplays = { "auto", "never", "always", "exceptZero", "negative" };
    private static readonly string[] s_relevantKeys = { "nu" };
    /// <summary>A NaN without the sign bit (ICU prints "-NaN" for .NET's default NaN).</summary>
    internal static readonly double CanonicalNaN = BitConverter.Int64BitsToDouble(0x7FF8_0000_0000_0000);

    // ------------------------------------------------------------------ internal API (used by other Intl services)

    /// <summary>
    /// Creates a NumberFormat record from (locales, options) exactly like <c>new Intl.NumberFormat(locales, options)</c>
    /// (same option reads, same exceptions). Use <see cref="NumberFormatRecord.Format(double)"/> /
    /// <see cref="NumberFormatRecord.Format(in IntlMathValue)"/> / <see cref="NumberFormatRecord.FormatToParts"/> on the result.
    /// </summary>
    internal static NumberFormatRecord CreateRecord(Realm realm, in JsValue locales, in JsValue options)
    {
        if (options.IsUndefined && (locales.IsUndefined || locales.IsString)) return CachedRecord(realm, locales);
        return Initialize(realm, locales, options);
    }

    /// <summary>Creates an Intl.NumberFormat object (with the realm's %NumberFormat.prototype%).</summary>
    internal static JsNumberFormat Create(Realm realm, in JsValue locales, in JsValue options) =>
        new(realm, realm.Intl!.NumberFormatPrototype, CreateRecord(realm, locales, options));

    /// <summary>Formats a Number with an Intl.NumberFormat object.</summary>
    internal static string FormatToString(JsObject numberFormat, double x) => ((JsNumberFormat)numberFormat).Record.Format(x);

    /// <summary>Formats an exact decimal string (e.g. "-12.5", "1E+30") with an Intl.NumberFormat object.</summary>
    internal static string FormatToString(JsObject numberFormat, string decimalString) => ((JsNumberFormat)numberFormat).Record.Format(new IntlMathValue(decimalString));

    private static readonly ConcurrentDictionary<(string, string), NumberFormatRecord> s_recordCache = new();

    /// <summary>Default-options records keyed by locale string (toLocaleString without options is common and expensive otherwise).</summary>
    private static NumberFormatRecord CachedRecord(Realm realm, in JsValue locales)
    {
        // the default locale takes part in the key: unsupported requested locales fall back to it
        var key = (locales.IsUndefined ? "" : locales.AsStringUnchecked, realm.DefaultLocale);
        if (s_recordCache.TryGetValue(key, out var rec)) return rec;
        var r = Initialize(realm, locales, JsValue.Undefined);
        if (s_recordCache.Count >= 256) s_recordCache.Clear();
        s_recordCache[key] = r;
        return r;
    }

    // ------------------------------------------------------------------ InitializeNumberFormat

    private readonly record struct ResolvedLocale(string Locale, string DataLocale, string NumberingSystem);
    private static readonly ConcurrentDictionary<(string, string, string, string), ResolvedLocale> s_resolveCache = new();
    private static readonly ConcurrentDictionary<string, byte> s_validLocaleStrings = new(StringComparer.Ordinal);

    internal static NumberFormatRecord Initialize(Realm realm, in JsValue locales, in JsValue optionsArg)
    {
        // a string (or absent) locales argument is canonicalized without observable effects: its resolution is cached
        string? localesKey = locales.IsUndefined ? "" : locales.IsString ? locales.AsStringUnchecked : null;
        List<string>? requested = null;
        if (localesKey == null) requested = IntlCore.CanonicalizeLocaleList(realm, locales);
        else if (!s_validLocaleStrings.ContainsKey(localesKey))
        {
            requested = IntlCore.CanonicalizeLocaleList(realm, locales);   // throws for invalid tags (before any option read)
            if (s_validLocaleStrings.Count >= 1024) s_validLocaleStrings.Clear();
            s_validLocaleStrings[localesKey] = 0;
        }
        var options = IntlCore.CoerceOptionsToObject(realm, optionsArg);
        var matcher = IntlCore.GetStringOption(realm, options, "localeMatcher", IntlCore.s_matchers, "best fit");
        var numberingSystem = IntlCore.GetStringOption(realm, options, "numberingSystem", null, null);
        if (numberingSystem != null && !IntlCore.IsUnicodeType(numberingSystem)) throw JsErrors.RangeError(realm, $"Invalid numberingSystem: {numberingSystem}");
        numberingSystem = numberingSystem?.ToLowerInvariant();
        ResolvedLocale resolved;
        var cacheKey = (localesKey ?? "", numberingSystem ?? "", realm.DefaultLocale, matcher ?? "");
        if (localesKey == null || !s_resolveCache.TryGetValue(cacheKey, out resolved))
        {
            requested ??= IntlCore.CanonicalizeLocaleList(realm, locales);
            var opt = new Dictionary<string, string?>(StringComparer.Ordinal) { ["nu"] = numberingSystem };
            var r = IntlCore.ResolveLocale(realm, requested, matcher, opt, s_relevantKeys, NumberingSystemData);
            resolved = new ResolvedLocale(r["locale"]!, r["dataLocale"]!, r["nu"] ?? "latn");
            if (localesKey != null)
            {
                if (s_resolveCache.Count >= 1024) s_resolveCache.Clear();
                s_resolveCache[cacheKey] = resolved;
            }
        }
        var rec = new NumberFormatRecord
        {
            Locale = resolved.Locale,
            DataLocale = resolved.DataLocale,
            NumberingSystem = resolved.NumberingSystem,
        };
        // SetNumberFormatUnitOptions
        var style = IntlCore.GetStringOption(realm, options, "style", s_styles, "decimal")!;
        rec.Style = style;
        var currency = IntlCore.GetStringOption(realm, options, "currency", null, null);
        if (currency == null)
        {
            if (style == "currency") throw JsErrors.TypeError(realm, "Currency code is required with currency style");
        }
        else if (!IsWellFormedCurrencyCode(currency)) throw JsErrors.RangeError(realm, $"Invalid currency code: {currency}");
        var currencyDisplay = IntlCore.GetStringOption(realm, options, "currencyDisplay", s_currencyDisplays, "symbol");
        var currencySign = IntlCore.GetStringOption(realm, options, "currencySign", s_currencySigns, "standard");
        var unit = IntlCore.GetStringOption(realm, options, "unit", null, null);
        if (unit == null)
        {
            if (style == "unit") throw JsErrors.TypeError(realm, "Unit is required with unit style");
        }
        else if (!IsWellFormedUnitIdentifier(unit)) throw JsErrors.RangeError(realm, $"Invalid unit argument: {unit}");
        var unitDisplay = IntlCore.GetStringOption(realm, options, "unitDisplay", s_unitDisplays, "short");
        if (style == "currency")
        {
            rec.Currency = currency!.ToUpperInvariant();
            rec.CurrencyDisplay = currencyDisplay;
            rec.CurrencySign = currencySign;
        }
        if (style == "unit")
        {
            rec.Unit = unit;
            rec.UnitDisplay = unitDisplay;
        }
        var notation = IntlCore.GetStringOption(realm, options, "notation", s_notations, "standard")!;
        rec.Notation = notation;
        int mnfdDefault, mxfdDefault;
        if (style == "currency" && notation == "standard")
        {
            int cDigits = CurrencyDigits(rec.Currency!);
            mnfdDefault = cDigits;
            mxfdDefault = cDigits;
        }
        else
        {
            mnfdDefault = 0;
            mxfdDefault = style == "percent" ? 0 : 3;
        }
        rec.Digits = NumberDigitOptions.Read(realm, options, mnfdDefault, mxfdDefault, notation);
        var compactDisplay = IntlCore.GetStringOption(realm, options, "compactDisplay", s_compactDisplays, "short");
        if (notation == "compact") rec.CompactDisplay = compactDisplay;
        string defaultUseGrouping = notation == "compact" ? "min2" : "auto";
        rec.UseGrouping = GetUseGrouping(realm, options, defaultUseGrouping);
        rec.SignDisplay = IntlCore.GetStringOption(realm, options, "signDisplay", s_signDisplays, "auto")!;
        var skeleton = rec.BuildSkeleton();
        rec.Formatter = IcuNumberFormatter.Get(IcuLocale(rec.DataLocale), skeleton)
            ?? throw JsErrors.RangeError(realm, "Unsupported number format options");
        return rec;
    }

    /// <summary>GetBooleanOrStringNumberFormatOption for useGrouping, then the true/"true"/"false" mapping.</summary>
    private static string? GetUseGrouping(Realm realm, JsObject options, string fallback)
    {
        var v = options.Get("useGrouping");
        if (v.IsUndefined) return fallback;
        if (v.IsTrue) return "always";
        if (!Operations.ToBoolean(v)) return null;
        var s = Operations.ToString(realm, v);
        if (s == "true" || s == "false") return fallback;
        if (Array.IndexOf(s_useGroupings, s) < 0) throw JsErrors.RangeError(realm, $"Value {s} out of range for useGrouping");
        return s;
    }

    /// <summary>IsWellFormedCurrencyCode.</summary>
    internal static bool IsWellFormedCurrencyCode(string c) => c.Length == 3 && char.IsAsciiLetter(c[0]) && char.IsAsciiLetter(c[1]) && char.IsAsciiLetter(c[2]);

    /// <summary>The sanctioned single unit identifiers (ECMA-402 table 2).</summary>
    private static readonly HashSet<string> s_sanctionedUnits = new(StringComparer.Ordinal)
    {
        "acre", "bit", "byte", "celsius", "centimeter", "day", "degree", "fahrenheit", "fluid-ounce", "foot", "gallon", "gigabit",
        "gigabyte", "gram", "hectare", "hour", "inch", "kilobit", "kilobyte", "kilogram", "kilometer", "liter", "megabit", "megabyte",
        "meter", "microsecond", "mile", "mile-scandinavian", "milliliter", "millimeter", "millisecond", "minute", "month", "nanosecond",
        "ounce", "percent", "petabyte", "pound", "second", "stone", "terabit", "terabyte", "week", "yard", "year",
    };

    internal static bool IsSanctionedSingleUnitIdentifier(string unit) => s_sanctionedUnits.Contains(unit);

    /// <summary>IsWellFormedUnitIdentifier.</summary>
    internal static bool IsWellFormedUnitIdentifier(string unit)
    {
        if (s_sanctionedUnits.Contains(unit)) return true;
        int i = unit.IndexOf("-per-", StringComparison.Ordinal);
        if (i < 0) return false;
        return s_sanctionedUnits.Contains(unit.Substring(0, i)) && s_sanctionedUnits.Contains(unit.Substring(i + 5));
    }

    private static readonly ConcurrentDictionary<string, int> s_currencyDigits = new(StringComparer.Ordinal);

    /// <summary>CurrencyDigits (ISO 4217 minor units, from ICU's currency data).</summary>
    internal static unsafe int CurrencyDigits(string currency)
    {
        if (s_currencyDigits.TryGetValue(currency, out var d)) return d;
        char* buf = stackalloc char[4];
        for (int i = 0; i < 3; i++) buf[i] = currency[i];
        buf[3] = '\0';
        int err = 0;
        int digits = IcuNum.ucurr_getDefaultFractionDigits(buf, &err);
        if (Icu.Failure(err) || digits < 0) digits = 2;
        s_currencyDigits[currency] = digits;
        return digits;
    }

    // ------------------------------------------------------------------ numbering systems

    private static string[]? s_simpleNumberingSystems;
    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> s_nuData = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> s_icuLocales = new(StringComparer.Ordinal);

    /// <summary>Cached <see cref="IntlCore.ToIcuLocale"/> (BCP 47 tag → ICU locale ID).</summary>
    internal static string IcuLocale(string tag)
    {
        if (s_icuLocales.TryGetValue(tag, out var id)) return id;
        id = IntlCore.ToIcuLocale(tag);
        if (s_icuLocales.Count >= 1024) s_icuLocales.Clear();
        s_icuLocales[tag] = id;
        return id;
    }

    /// <summary>The numbering systems with simple digit mappings that ICU supports (sorted).</summary>
    internal static unsafe string[] SimpleNumberingSystems
    {
        get
        {
            var a = s_simpleNumberingSystems;
            if (a != null) return a;
            int err = 0;
            var names = Icu.ReadEnum(IcuNum.unumsys_openAvailableNames(&err));
            var list = new List<string>();
            foreach (var n in names)
            {
                var z = Icu.Utf8Z(n);
                int e = 0;
                nint ns;
                fixed (byte* p = z) ns = IcuNum.unumsys_openByName(p, &e);
                if (ns == 0) continue;
                if (!Icu.Failure(e) && !IcuNum.unumsys_isAlgorithmic(ns)) list.Add(n);
                IcuNum.unumsys_close(ns);
            }
            list.Sort(StringComparer.Ordinal);
            return s_simpleNumberingSystems = list.ToArray();
        }
    }

    /// <summary>The default numbering system of a locale.</summary>
    internal static unsafe string DefaultNumberingSystem(string locale)
    {
        var z = Icu.Utf8Z(IcuLocale(locale));
        int err = 0;
        nint ns;
        fixed (byte* p = z) ns = IcuNum.unumsys_open(p, &err);
        if (ns == 0) return "latn";
        string name = "latn";
        if (!Icu.Failure(err) && !IcuNum.unumsys_isAlgorithmic(ns)) name = Icu.FromCString(IcuNum.unumsys_getName(ns)) ?? "latn";
        IcuNum.unumsys_close(ns);
        return name;
    }

    /// <summary>[[LocaleData]] for "nu": the locale's default numbering system first, then all supported ones.</summary>
    internal static IReadOnlyList<string> NumberingSystemData(string locale, string key)
    {
        if (s_nuData.TryGetValue(locale, out var l)) return l;
        var def = DefaultNumberingSystem(locale);
        var list = new List<string> { def };
        foreach (var n in SimpleNumberingSystems) if (n != def) list.Add(n);
        s_nuData[locale] = list;
        return list;
    }

    // ------------------------------------------------------------------ ToIntlMathematicalValue

    /// <summary>ToIntlMathematicalValue (15.5.16).</summary>
    internal static IntlMathValue ToIntlMathematicalValue(Realm realm, in JsValue value)
    {
        if (value.IsNumber) return new IntlMathValue(value.Num);
        var prim = Operations.ToPrimitive(realm, value, Operations.PreferredType.Number);
        if (prim.IsBigInt) return new IntlMathValue(prim.BigIntValue.ToString(CultureInfo.InvariantCulture));
        if (prim.IsString) return FromNumericString(prim.AsStringUnchecked);
        return new IntlMathValue(Operations.ToNumber(realm, prim));
    }

    /// <summary>The exact mathematical value of a StringNumericLiteral (NaN when the string is not one).</summary>
    internal static IntlMathValue FromNumericString(string str)
    {
        double d = NumberFormatting.StringToNumber(str);
        // values that round to ±∞ or ±0 as a Number are those (RoundMVResult), everything else stays exact
        if (double.IsNaN(d) || double.IsInfinity(d) || d == 0) return new IntlMathValue(d);
        var s = NumberFormatting.TrimJs(str);
        if (s.Length == 0) return new IntlMathValue(0.0);
        if (s.Length > 2 && s[0] == '0' && (s[1] | 0x20) is 'x' or 'o' or 'b')
        {
            if (Math.Abs(d) < 9007199254740992.0) return new IntlMathValue(d);
            int radix = (s[1] | 0x20) == 'x' ? 16 : (s[1] | 0x20) == 'o' ? 8 : 2;
            BigInteger v = BigInteger.Zero;
            for (int i = 2; i < s.Length; i++) v = v * radix + NumberFormatting.DigitValue(s[i]);
            return new IntlMathValue(v.ToString(CultureInfo.InvariantCulture));
        }
        // StrDecimalLiteral: normalize to [-]digits[.digits][E[-]digits]
        var sb = new StringBuilder(s.Length + 2);
        int idx = 0;
        if (s[0] == '-') { sb.Append('-'); idx = 1; }
        else if (s[0] == '+') idx = 1;
        int intStart = idx;
        while (idx < s.Length && char.IsAsciiDigit(s[idx])) idx++;
        if (idx == intStart) sb.Append('0');
        else sb.Append(s, intStart, idx - intStart);
        if (idx < s.Length && s[idx] == '.')
        {
            idx++;
            int fs = idx;
            while (idx < s.Length && char.IsAsciiDigit(s[idx])) idx++;
            if (idx > fs) sb.Append('.').Append(s, fs, idx - fs);
        }
        if (idx < s.Length)
        {
            // exponent: e[+-]digits (validated by StringToNumber)
            idx++;
            bool neg = false;
            if (s[idx] == '+' || s[idx] == '-') { neg = s[idx] == '-'; idx++; }
            while (idx < s.Length - 1 && s[idx] == '0') idx++;
            var exp = s.AsSpan(idx);
            if (exp.Length > 9)
            {
                // beyond ICU's exponent range: the double conversion (0 or ±∞) is the only sensible result
                return new IntlMathValue(d);
            }
            sb.Append('E');
            if (neg) sb.Append('-');
            sb.Append(exp);
        }
        return new IntlMathValue(sb.ToString());
    }

    internal static double ToDouble(in IntlMathValue x) =>
        x.Decimal == null ? x.Number : double.Parse(x.Decimal, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Formats a value into an ICU UFormattedNumber.</summary>
    internal static unsafe void FormatInto(nint formatter, in IntlMathValue x, nint result, int* err)
    {
        if (x.Decimal == null)
        {
            IcuNum.unumf_formatDouble(formatter, x.Number, result, err);
            return;
        }
        var s = x.Decimal;
        if (s.Length <= 512)
        {
            byte* p = stackalloc byte[s.Length];
            for (int i = 0; i < s.Length; i++) p[i] = (byte)s[i];
            IcuNum.unumf_formatDecimal(formatter, p, s.Length, result, err);
        }
        else
        {
            var b = Encoding.ASCII.GetBytes(s);
            fixed (byte* p = b) IcuNum.unumf_formatDecimal(formatter, p, b.Length, result, err);
        }
    }

    // ------------------------------------------------------------------ parts

    /// <summary>Converts the fields of an ICU formatted value into ECMA-402 parts (PartitionNumberPattern / PartitionNumberRangePattern).</summary>
    internal static unsafe List<NumberPart> ReadParts(nint fmtval, NumberFormatRecord rec, in IntlMathValue x, in IntlMathValue y, bool range)
    {
        int err = 0, len = 0;
        char* chars = IcuNum.ufmtval_getString(fmtval, &len, &err);
        var str = new string(chars, 0, len);
        var owner = new int[len];        // index into spans + 1 (0 = literal)
        var ownerLen = new int[len];
        int[]? side = null;
        if (range) { side = new int[len]; Array.Fill(side, -1); }
        var spans = new List<(int Field, int Start, int Limit)>();
        var pos = IcuNumberFormatter.FieldPosition;
        IcuNum.ucfpos_reset(pos, &err);
        while (IcuNum.ufmtval_nextPosition(fmtval, pos, &err))
        {
            if (Icu.Failure(err)) break;
            int cat = IcuNum.ucfpos_getCategory(pos, &err);
            int field = IcuNum.ucfpos_getField(pos, &err);
            int start = 0, limit = 0;
            IcuNum.ucfpos_getIndexes(pos, &start, &limit, &err);
            if (start < 0 || limit > len) continue;
            if (cat == IcuNum.UFIELD_CATEGORY_NUMBER_RANGE_SPAN)
            {
                if (side != null) for (int i = start; i < limit; i++) side[i] = field;
            }
            else if (cat == IcuNum.UFIELD_CATEGORY_NUMBER)
            {
                spans.Add((field, start, limit));
                int id = spans.Count;
                int l = limit - start;
                for (int i = start; i < limit; i++)
                    if (owner[i] == 0 || l < ownerLen[i]) { owner[i] = id; ownerLen[i] = l; }
            }
        }
        var parts = new List<NumberPart>();
        int k = 0;
        while (k < len)
        {
            int o = owner[k];
            int sd = side?[k] ?? -1;
            int e = k + 1;
            while (e < len && owner[e] == o && (side == null || side[e] == sd)) e++;
            string type;
            if (o == 0) type = "literal";
            else
            {
                ref readonly var v = ref (sd == 1 ? ref y : ref x);
                type = FieldType(spans[o - 1].Field, rec, v);
            }
            string? source = range ? sd switch { 0 => "startRange", 1 => "endRange", _ => "shared" } : null;
            parts.Add(new NumberPart(type, str.Substring(k, e - k), source));
            k = e;
        }
        return parts;
    }

    private static string FieldType(int field, NumberFormatRecord rec, in IntlMathValue v) => field switch
    {
        0 => v.IsNaN ? "nan" : v.IsInfinity ? "infinity" : "integer",
        1 => "fraction",
        2 => "decimal",
        3 => "exponentSeparator",
        4 => "exponentMinusSign",
        5 => "exponentInteger",
        6 => "group",
        7 => "currency",
        8 => rec.Style == "unit" ? "unit" : "percentSign",
        9 => "percentSign",
        10 => v.IsNegative ? "minusSign" : "plusSign",
        11 => "unit",
        12 => "compact",
        13 => "approximatelySign",
        _ => "literal",
    };

    internal static JsValue PartsToArray(Realm realm, List<NumberPart> parts)
    {
        var arr = realm.NewArray(parts.Count);
        for (int i = 0; i < parts.Count; i++)
        {
            var o = realm.NewObject();
            o.AddOwnProperty("type", new JsValue(parts[i].Type), PropFlags.Default);
            o.AddOwnProperty("value", new JsValue(parts[i].Value), PropFlags.Default);
            if (parts[i].Source != null) o.AddOwnProperty("source", new JsValue(parts[i].Source!), PropFlags.Default);
            arr.CreateDataPropertyOrThrow(PropertyKey.FromIndex((uint)i), JsValue.FromObject(o));
        }
        return JsValue.FromObject(arr);
    }

    // ------------------------------------------------------------------ legacy constructor support (normative optional)

    /// <summary>UnwrapNumberFormat (15.5.2).</summary>
    internal static JsNumberFormat Unwrap(in NativeArgs a, string method)
    {
        var realm = a.Realm;
        var nf = a.This;
        if (!nf.IsObject) throw JsErrors.TypeError(realm, $"{method} called on an incompatible receiver");
        if (nf.Ref is JsNumberFormat direct) return direct;
        var intl = realm.Intl!;
        if (Operations.OrdinaryHasInstance(realm, intl.NumberFormatConstructor, nf))
        {
            var fallback = nf.AsObjectUnchecked.Get(intl.FallbackSymbol);
            if (fallback.Ref is JsNumberFormat f) return f;
        }
        throw JsErrors.TypeError(realm, $"{method} called on an incompatible receiver");
    }

    internal static JsObject ProtoFromCtor(Realm realm, JsObject newTarget, Func<IntlProtos, JsObject> select)
    {
        var p = newTarget.Get("prototype");
        if (p.IsObject) return p.AsObjectUnchecked;
        var fr = Operations.GetFunctionRealm(realm, newTarget);
        return select(fr.Intl ?? realm.Intl!);
    }

    // ------------------------------------------------------------------ resolvedOptions

    internal static JsValue ResolvedOptions(Realm realm, NumberFormatRecord r)
    {
        var o = realm.NewObject();
        o.AddOwnProperty("locale", new JsValue(r.Locale), PropFlags.Default);
        o.AddOwnProperty("numberingSystem", new JsValue(r.NumberingSystem), PropFlags.Default);
        o.AddOwnProperty("style", new JsValue(r.Style), PropFlags.Default);
        if (r.Currency != null)
        {
            o.AddOwnProperty("currency", new JsValue(r.Currency), PropFlags.Default);
            o.AddOwnProperty("currencyDisplay", new JsValue(r.CurrencyDisplay!), PropFlags.Default);
            o.AddOwnProperty("currencySign", new JsValue(r.CurrencySign!), PropFlags.Default);
        }
        if (r.Unit != null)
        {
            o.AddOwnProperty("unit", new JsValue(r.Unit), PropFlags.Default);
            o.AddOwnProperty("unitDisplay", new JsValue(r.UnitDisplay!), PropFlags.Default);
        }
        r.Digits.AddResolvedDigits(o);
        o.AddOwnProperty("useGrouping", r.UseGrouping == null ? JsValue.False : new JsValue(r.UseGrouping), PropFlags.Default);
        o.AddOwnProperty("notation", new JsValue(r.Notation), PropFlags.Default);
        if (r.CompactDisplay != null) o.AddOwnProperty("compactDisplay", new JsValue(r.CompactDisplay), PropFlags.Default);
        o.AddOwnProperty("signDisplay", new JsValue(r.SignDisplay), PropFlags.Default);
        r.Digits.AddResolvedRounding(o);
        return JsValue.FromObject(o);
    }
}

internal static partial class IntlBuiltins
{
    static partial void SetupNumberFormat(Realm r, JsObject intl, IntlProtos protos)
    {
        var proto = new JsObject(r, r.ObjectPrototype);
        protos.NumberFormatPrototype = proto;
        proto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("Intl.NumberFormat"), PropFlags.Configurable);
        var ctor = r.Constructor("NumberFormat", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var intlProtos = realm.Intl!;
            var newTarget = a.IsConstruct ? a.NewTarget.AsObjectUnchecked : intlProtos.NumberFormatConstructor;
            var p = IntlNumberFormat.ProtoFromCtor(realm, newTarget, static x => x.NumberFormatPrototype);
            var record = IntlNumberFormat.Initialize(realm, a[0], a[1]);
            var nf = new JsNumberFormat(realm, p, record);
            // ChainNumberFormat (normative optional legacy behaviour)
            if (!a.IsConstruct && a.This.IsObject && Operations.OrdinaryHasInstance(realm, intlProtos.NumberFormatConstructor, a.This))
            {
                var desc = PropertyDescriptor.Data(JsValue.FromObject(nf), PropFlags.None);
                if (!a.This.AsObjectUnchecked.DefineOwnProperty(intlProtos.FallbackSymbol, desc))
                    throw JsErrors.TypeError(realm, "Cannot define the legacy NumberFormat property");
                return a.This;
            }
            return JsValue.FromObject(nf);
        }, proto);
        protos.NumberFormatConstructor = ctor;
        intl.DefineHidden("NumberFormat", JsValue.FromObject(ctor));
        r.Method(ctor, "supportedLocalesOf", 1, static (in NativeArgs a) => IntlCore.SupportedLocalesOf(a.Realm, a[0], a[1]));

        r.Getter(proto, "format", "format", static (in NativeArgs a) =>
        {
            var nf = IntlNumberFormat.Unwrap(a, "get Intl.NumberFormat.prototype.format");
            var f = nf.BoundFormat;
            if (f == null)
            {
                f = new JsBuiltin(a.Realm, "", 1, static (in NativeArgs b) =>
                {
                    var rec = ((JsNumberFormat)b.Callee.State!).Record;
                    var v = b[0];
                    if (v.IsNumber) return new JsValue(rec.Format(v.Num));
                    return new JsValue(rec.Format(IntlNumberFormat.ToIntlMathematicalValue(b.Realm, v)));
                }) { State = nf };
                nf.BoundFormat = f;
            }
            return JsValue.FromObject(f);
        });
        r.Method(proto, "formatToParts", 1, static (in NativeArgs a) =>
        {
            var nf = IntlCore.This<JsNumberFormat>(a, "Intl.NumberFormat.prototype.formatToParts");
            var x = IntlNumberFormat.ToIntlMathematicalValue(a.Realm, a[0]);
            return IntlNumberFormat.PartsToArray(a.Realm, nf.Record.FormatToParts(x));
        });
        r.Method(proto, "formatRange", 2, static (in NativeArgs a) =>
        {
            var nf = IntlCore.This<JsNumberFormat>(a, "Intl.NumberFormat.prototype.formatRange");
            var (x, y) = RangeArgs(a);
            return new JsValue(nf.Record.FormatRange(x, y));
        });
        r.Method(proto, "formatRangeToParts", 2, static (in NativeArgs a) =>
        {
            var nf = IntlCore.This<JsNumberFormat>(a, "Intl.NumberFormat.prototype.formatRangeToParts");
            var (x, y) = RangeArgs(a);
            return IntlNumberFormat.PartsToArray(a.Realm, nf.Record.FormatRangeToParts(x, y));
        });
        r.Method(proto, "resolvedOptions", 0, static (in NativeArgs a) =>
        {
            var nf = IntlNumberFormat.Unwrap(a, "Intl.NumberFormat.prototype.resolvedOptions");
            return IntlNumberFormat.ResolvedOptions(a.Realm, nf.Record);
        });
    }

    private static (IntlMathValue, IntlMathValue) RangeArgs(in NativeArgs a)
    {
        if (a[0].IsUndefined || a[1].IsUndefined) throw JsErrors.TypeError(a.Realm, "start and end are required");
        var x = IntlNumberFormat.ToIntlMathematicalValue(a.Realm, a[0]);
        var y = IntlNumberFormat.ToIntlMathematicalValue(a.Realm, a[1]);
        if (x.IsNaN || y.IsNaN) throw JsErrors.RangeError(a.Realm, "Range values must not be NaN");
        return (x, y);
    }
}
