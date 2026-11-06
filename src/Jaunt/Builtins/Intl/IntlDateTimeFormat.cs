using System;
using System.Collections.Generic;
using System.Text;

namespace Jaunt.Runtime;

internal sealed partial class IntlProtos
{
    public JsObject DateTimeFormatPrototype = null!;
    public JsBuiltin DateTimeFormatConstructor = null!;
    /// <summary>Cached formatters for Date.prototype.toLocale{,Date,Time}String without arguments.</summary>
    internal readonly JsDateTimeFormat?[] DateToLocaleCache = new JsDateTimeFormat?[3];
    /// <summary>Format records shared by the realm's DateTimeFormat objects, keyed by zone, ICU locale and pattern.</summary>
    internal readonly Dictionary<string, DtfFormat> DtfFormatCache = new(StringComparer.Ordinal);
    /// <summary>Resolved formatters by their resolution inputs (locales, option values, time zone, default locale).</summary>
    internal readonly Dictionary<string, JsDateTimeFormat> DtfResolveCache = new(StringComparer.Ordinal);
}

/// <summary>
/// A resolved DateTime format record: an ICU pattern plus the ICU objects that format it (opened lazily and released
/// by the finalizer). Plain (Temporal wall-clock) formats always format in UTC.
/// </summary>
internal sealed unsafe class DtfFormat
{
    public readonly string Pattern;
    public readonly bool Plain;
    private readonly string _icuLocale, _tz;
    private string?[]? _fields;
    private nint _udat, _itv, _cal1, _cal2;
    private bool _finalizable;

    public DtfFormat(string icuLocale, string tz, string pattern, bool plain)
    {
        _icuLocale = icuLocale;
        _tz = plain ? "UTC" : tz;
        Pattern = pattern;
        Plain = plain;
        GC.SuppressFinalize(this);
    }

    /// <summary>The format's components (indexes of <see cref="IntlDateTimeFormat.FieldNames"/>).</summary>
    public string?[] Fields => _fields ??= IntlDateTimeFormat.ParsePatternFields(Pattern);

    public bool HasHour => Fields[IntlDateTimeFormat.FHour] != null;

    /// <summary>
    /// Calendars whose ICU eras differ from ECMA-402's for years before the epoch (see IntlDateTimeFormat.ApplyEraOverride):
    /// 1 = Islamic (ICU numbers them 0, -1, … in AH; CLDR's era 1 "BH" applies), 2 = Coptic (ICU switches to an unnamed
    /// era; ECMA-402 keeps "am" with negative years); 0 = none, or the pattern has no era.
    /// </summary>
    public int EraOverride => _eraOverride ??= Fields[IntlDateTimeFormat.FEra] == null ? 0
        : _icuLocale.Contains("calendar=islamic", StringComparison.Ordinal) ? 1
        : _icuLocale.Contains("calendar=coptic", StringComparison.Ordinal) ? 2 : 0;
    private int? _eraOverride;

    private DtfFormat? _gregorianYear, _eraTemplate;

    /// <summary>A UTC format of just this pattern's year field in the Gregorian calendar (same locale and numbering).</summary>
    public DtfFormat GregorianYear => _gregorianYear ??= new DtfFormat(
        System.Text.RegularExpressions.Regex.Replace(_icuLocale, "calendar=[a-z-]+", "calendar=gregorian"), "UTC",
        Fields[IntlDateTimeFormat.FYear] == "2-digit" ? "yy" : "y", true);

    public const char EraMark = '\uE000', YearMark = '\uE001';

    /// <summary>This format with its era and year fields replaced by the literal marks <see cref="EraMark"/> / <see cref="YearMark"/>.</summary>
    public DtfFormat EraTemplate => _eraTemplate ??= new DtfFormat(_icuLocale, _tz, MarkEraAndYear(Pattern), Plain);

    private static string MarkEraAndYear(string pattern)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < pattern.Length;)
        {
            char c = pattern[i];
            if (c == '\'')
            {
                int j = pattern.IndexOf('\'', i + 1);
                j = j < 0 ? pattern.Length : j + 1;
                sb.Append(pattern, i, j - i);
                i = j;
                continue;
            }
            int n = 1;
            while (i + n < pattern.Length && pattern[i + n] == c) n++;
            if (c == 'G') sb.Append('\'').Append(EraMark).Append('\'');
            else if (c == 'y') sb.Append('\'').Append(YearMark).Append('\'');
            else sb.Append(pattern, i, n);
            i += n;
        }
        return sb.ToString();
    }

    private DtfFormat? _hourOnly;

    /// <summary>
    /// A format of just this pattern's hour field when it uses the h11 or h24 cycle ('K'/'k'), which ICU's interval
    /// formatter does not support (it formats those hours as h12/h23); null otherwise.
    /// </summary>
    public DtfFormat? HourOnly
    {
        get
        {
            if (_hourOnly != null) return _hourOnly;
            bool quoted = false;
            for (int i = 0; i < Pattern.Length; i++)
            {
                char c = Pattern[i];
                if (c == '\'') { quoted = !quoted; continue; }
                if (quoted || c is not ('K' or 'k')) continue;
                int n = 1;
                while (i + n < Pattern.Length && Pattern[i + n] == c) n++;
                return _hourOnly = new DtfFormat(_icuLocale, _tz, new string(c, n), Plain);
            }
            return null;
        }
    }

    public nint Udat()
    {
        if (_udat != 0) return _udat;
        _udat = IntlDateTimeFormat.OpenUdat(_icuLocale, _tz, Pattern);
        RegisterFinalizer();
        return _udat;
    }

    public nint Interval(out nint cal1, out nint cal2)
    {
        if (_itv == 0)
        {
            var skeleton = IntlDateTimeFormat.SkeletonOf(Pattern);
            _cal1 = IntlDateTimeFormat.OpenCalendar(_icuLocale, _tz);
            _cal2 = IntlDateTimeFormat.OpenCalendar(_icuLocale, _tz);
            _itv = IntlDateTimeFormat.OpenInterval(_icuLocale, skeleton, _tz);
            RegisterFinalizer();
        }
        cal1 = _cal1;
        cal2 = _cal2;
        return _itv;
    }

    private void RegisterFinalizer()
    {
        if (_finalizable) return;
        _finalizable = true;
        GC.ReRegisterForFinalize(this);
    }

    ~DtfFormat()
    {
        if (_udat != 0) { IcuDtf.udat_close(_udat); _udat = 0; }
        if (_itv != 0) { IcuDtf.udtitvfmt_close(_itv); _itv = 0; }
        if (_cal1 != 0) { IcuDtf.ucal_close(_cal1); _cal1 = 0; }
        if (_cal2 != 0) { IcuDtf.ucal_close(_cal2); _cal2 = 0; }
    }
}

/// <summary>An Intl.DateTimeFormat instance ([[InitializedDateTimeFormat]]).</summary>
internal sealed class JsDateTimeFormat : JsObject
{
    public JsDateTimeFormat(Realm realm, JsObject proto) : base(realm, proto) { }

    public string Locale = "";
    public string Calendar = "gregory";
    public string NumberingSystem = "latn";
    public string TimeZone = "UTC";
    public string? HourCycle;
    public string? DateStyle, TimeStyle;
    /// <summary>ICU locale ID with calendar and numbering system keywords.</summary>
    public string IcuLocale = "en";
    /// <summary>The time zone as ICU understands it (offset zones as "GMT±hh:mm").</summary>
    public string IcuTimeZone = "UTC";

    public DtfFormat Format = null!;
    public DtfFormat? PlainDateFormat, PlainYearMonthFormat, PlainMonthDayFormat, PlainTimeFormat;
    public DtfFormat PlainDateTimeFormat = null!, InstantFormat = null!;
    public JsBuiltin? BoundFormat;
    /// <summary>Copies the resolved state (not the bound format function) of another instance.</summary>
    public void CopyResolved(JsDateTimeFormat o)
    {
        Locale = o.Locale; Calendar = o.Calendar; NumberingSystem = o.NumberingSystem; TimeZone = o.TimeZone; HourCycle = o.HourCycle;
        DateStyle = o.DateStyle; TimeStyle = o.TimeStyle; IcuLocale = o.IcuLocale; IcuTimeZone = o.IcuTimeZone;
        Format = o.Format; PlainDateFormat = o.PlainDateFormat; PlainYearMonthFormat = o.PlainYearMonthFormat;
        PlainMonthDayFormat = o.PlainMonthDayFormat; PlainTimeFormat = o.PlainTimeFormat; PlainDateTimeFormat = o.PlainDateTimeFormat;
        InstantFormat = o.InstantFormat;
    }

    /// <summary>For the per-realm Date.prototype.toLocaleString cache: the default locale it was created for.</summary>
    public string? CacheLocale;
}

/// <summary>Intl.DateTimeFormat (ECMA-402 chapter 11 with the Temporal amendments).</summary>
internal static unsafe partial class IntlDateTimeFormat
{
    // ------------------------------------------------------------------ the components table

    public const int FWeekday = 0, FEra = 1, FYear = 2, FMonth = 3, FDay = 4, FDayPeriod = 5, FHour = 6, FMinute = 7, FSecond = 8, FFsd = 9, FTzName = 10, FCount = 11;

    public static readonly string[] FieldNames =
        { "weekday", "era", "year", "month", "day", "dayPeriod", "hour", "minute", "second", "fractionalSecondDigits", "timeZoneName" };

    private static readonly string[] s_textValues = { "narrow", "short", "long" };
    private static readonly string[] s_numValues = { "2-digit", "numeric" };
    private static readonly string[] s_monthValues = { "2-digit", "numeric", "narrow", "short", "long" };
    private static readonly string[] s_tzNameValues = { "short", "long", "shortOffset", "longOffset", "shortGeneric", "longGeneric" };
    private static readonly string[]?[] s_fieldValues =
        { s_textValues, s_textValues, s_numValues, s_monthValues, s_numValues, s_textValues, s_numValues, s_numValues, s_numValues, null, s_tzNameValues };

    private static readonly string[] s_hcValues = { "h11", "h12", "h23", "h24" };
    private static readonly string[] s_styleValues = { "full", "long", "medium", "short" };
    private static readonly string[] s_formatMatcherValues = { "basic", "best fit" };
    private static readonly string[] s_relevantKeys = { "ca", "hc", "nu" };

    internal enum Req { Date, Time, YearMonth, MonthDay, Any }
    internal enum Def { Date, Time, YearMonth, MonthDay, ZonedDateTime, All }

    private static readonly int[] s_reqDate = { FWeekday, FYear, FMonth, FDay };
    private static readonly int[] s_reqTime = { FDayPeriod, FHour, FMinute, FSecond, FFsd };
    private static readonly int[] s_reqYearMonth = { FYear, FMonth };
    private static readonly int[] s_reqMonthDay = { FMonth, FDay };
    private static readonly int[] s_reqAny = { FWeekday, FYear, FMonth, FDay, FDayPeriod, FHour, FMinute, FSecond, FFsd };
    private static readonly int[] s_defDate = { FYear, FMonth, FDay };
    private static readonly int[] s_defTime = { FHour, FMinute, FSecond };
    private static readonly int[] s_defAll = { FYear, FMonth, FDay, FHour, FMinute, FSecond };

    private static readonly int[] s_allowDate = { FWeekday, FEra, FYear, FMonth, FDay };
    private static readonly int[] s_allowYearMonth = { FEra, FYear, FMonth };
    private static readonly int[] s_allowMonthDay = { FMonth, FDay };
    private static readonly int[] s_allowTime = { FDayPeriod, FHour, FMinute, FSecond, FFsd };
    private static readonly int[] s_allowDateTime = { FWeekday, FEra, FYear, FMonth, FDay, FDayPeriod, FHour, FMinute, FSecond, FFsd };

    /// <summary>Calendars usable with DateTimeFormat (those Temporal knows of, minus the ICU-only "islamic"/"islamic-rgsa").</summary>
    internal static readonly string[] SupportedCalendars =
    {
        "buddhist", "chinese", "coptic", "dangi", "ethioaa", "ethiopic", "gregory", "hebrew", "indian",
        "islamic-civil", "islamic-tbla", "islamic-umalqura", "iso8601", "japanese", "persian", "roc",
    };

    /// <summary>Earliest instant for the Julian/Gregorian switch, making ICU's Gregorian calendar proleptic.</summary>
    private const double GregorianChange = -8.70e15;

    // ------------------------------------------------------------------ setup

    internal static void Setup(Realm r, JsObject intl, IntlProtos protos)
    {
        var proto = new JsObject(r, r.ObjectPrototype);
        protos.DateTimeFormatPrototype = proto;
        var ctor = r.Constructor("DateTimeFormat", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var newTarget = a.IsConstruct ? a.NewTarget.AsObjectUnchecked : a.Callee;
            var dtf = Create(realm, ProtoFromConstructor(realm, newTarget), a[0], a[1], Req.Any, Def.Date);
            // ChainDateTimeFormat (normative optional legacy constructor mode)
            if (!a.IsConstruct && a.This.IsObject && Operations.OrdinaryHasInstance(realm, realm.Intl!.DateTimeFormatConstructor, a.This))
            {
                a.This.AsObjectUnchecked.DefinePropertyOrThrow(realm.Intl!.FallbackSymbol,
                    PropertyDescriptor.Data(JsValue.FromObject(dtf), PropFlags.None));
                return a.This;
            }
            return JsValue.FromObject(dtf);
        }, proto);
        protos.DateTimeFormatConstructor = ctor;
        intl.DefineHidden("DateTimeFormat", JsValue.FromObject(ctor));
        r.Method(ctor, "supportedLocalesOf", 1, static (in NativeArgs a) => IntlCore.SupportedLocalesOf(a.Realm, a[0], a[1]));

        proto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("Intl.DateTimeFormat"), PropFlags.Configurable);
        r.Getter(proto, "format", "format", static (in NativeArgs a) =>
        {
            var dtf = Unwrap(a, "Intl.DateTimeFormat.prototype.format");
            dtf.BoundFormat ??= new JsBuiltin(a.Realm, "", 1, static (in NativeArgs b) =>
            {
                var d = (JsDateTimeFormat)b.Callee.State!;
                var x = b[0].IsUndefined ? new JsValue(Now(b.Realm)) : ToDateTimeFormattable(b.Realm, b[0]);
                return new JsValue(FormatDateTime(b.Realm, d, x));
            }) { State = dtf };
            return JsValue.FromObject(dtf.BoundFormat);
        });
        r.Method(proto, "formatToParts", 1, static (in NativeArgs a) =>
        {
            var dtf = IntlCore.This<JsDateTimeFormat>(a, "Intl.DateTimeFormat.prototype.formatToParts");
            var x = a[0].IsUndefined ? new JsValue(Now(a.Realm)) : ToDateTimeFormattable(a.Realm, a[0]);
            return JsValue.FromObject(PartsToArray(a.Realm, FormatDateTimeToParts(a.Realm, dtf, x), null));
        });
        r.Method(proto, "formatRange", 2, static (in NativeArgs a) =>
        {
            var dtf = IntlCore.This<JsDateTimeFormat>(a, "Intl.DateTimeFormat.prototype.formatRange");
            var (x, y) = RangeArgs(a);
            var sb = new StringBuilder();
            foreach (var p in FormatRangeParts(a.Realm, dtf, x, y)) sb.Append(p.Value);
            return new JsValue(sb.ToString());
        });
        r.Method(proto, "formatRangeToParts", 2, static (in NativeArgs a) =>
        {
            var dtf = IntlCore.This<JsDateTimeFormat>(a, "Intl.DateTimeFormat.prototype.formatRangeToParts");
            var (x, y) = RangeArgs(a);
            var parts = FormatRangeParts(a.Realm, dtf, x, y);
            var list = new List<(string, string)>(parts.Count);
            var sources = new List<string>(parts.Count);
            foreach (var p in parts) { list.Add((p.Type, p.Value)); sources.Add(p.Source); }
            return JsValue.FromObject(PartsToArray(a.Realm, list, sources));
        });
        r.Method(proto, "resolvedOptions", 0, static (in NativeArgs a) =>
            JsValue.FromObject(ResolvedOptions(a.Realm, Unwrap(a, "Intl.DateTimeFormat.prototype.resolvedOptions"))));
    }

    private static (JsValue, JsValue) RangeArgs(in NativeArgs a)
    {
        if (a[0].IsUndefined || a[1].IsUndefined) throw JsErrors.TypeError(a.Realm, "formatRange requires a start and an end date");
        var x = ToDateTimeFormattable(a.Realm, a[0]);
        var y = ToDateTimeFormattable(a.Realm, a[1]);
        return (x, y);
    }

    private static double Now(Realm realm) => Math.Floor((double)realm.Clock().ToUnixTimeMilliseconds());

    private static JsObject ProtoFromConstructor(Realm realm, JsObject newTarget)
    {
        var p = newTarget.Get("prototype");
        if (p.IsObject) return p.AsObjectUnchecked;
        var fr = Operations.GetFunctionRealm(realm, newTarget);
        return (fr.Intl ?? realm.Intl!).DateTimeFormatPrototype;
    }

    /// <summary>UnwrapDateTimeFormat + RequireInternalSlot.</summary>
    private static JsDateTimeFormat Unwrap(in NativeArgs a, string method)
    {
        var realm = a.Realm;
        var v = a.This;
        if (!v.IsObject) throw JsErrors.TypeError(realm, $"{method} called on a non-object");
        if (v.Ref is JsDateTimeFormat d) return d;
        if (Operations.OrdinaryHasInstance(realm, realm.Intl!.DateTimeFormatConstructor, v))
            v = v.AsObjectUnchecked.Get(realm.Intl!.FallbackSymbol);
        return v.Ref as JsDateTimeFormat ?? throw JsErrors.TypeError(realm, $"{method} called on an incompatible receiver");
    }

    /// <summary>ToDateTimeFormattable.</summary>
    private static JsValue ToDateTimeFormattable(Realm realm, in JsValue v)
    {
        if (IsTemporalObject(v)) return v;
        return new JsValue(Operations.ToNumber(realm, v));
    }

    private static bool IsTemporalObject(in JsValue v) =>
        v.Ref is JsPlainDate or JsPlainTime or JsPlainDateTime or JsZonedDateTime or JsPlainYearMonth or JsPlainMonthDay or JsInstant;

    // ------------------------------------------------------------------ CreateDateTimeFormat

    internal static JsDateTimeFormat Create(Realm realm, JsObject proto, in JsValue locales, in JsValue options, Req required, Def defaults, string? toLocaleStringTimeZone = null)
    {
        var dtf = new JsDateTimeFormat(realm, proto);
        var requested = IntlCore.CanonicalizeLocaleList(realm, locales);
        var opts = IntlCore.CoerceOptionsToObject(realm, options);
        var matcher = IntlCore.GetStringOption(realm, opts, "localeMatcher", IntlCore.s_matchers, "best fit");
        var calendar = IntlCore.GetStringOption(realm, opts, "calendar", null, null);
        if (calendar != null)
        {
            if (!IntlCore.IsUnicodeType(calendar)) throw JsErrors.RangeError(realm, $"Invalid calendar : {calendar}");
            calendar = CanonicalCalendar(calendar.ToLowerInvariant());
        }
        var numberingSystem = IntlCore.GetStringOption(realm, opts, "numberingSystem", null, null);
        if (numberingSystem != null)
        {
            if (!IntlCore.IsUnicodeType(numberingSystem)) throw JsErrors.RangeError(realm, $"Invalid numberingSystem : {numberingSystem}");
            numberingSystem = numberingSystem.ToLowerInvariant();
        }
        var hour12 = IntlCore.GetBoolOption(realm, opts, "hour12", null);
        var hourCycle = IntlCore.GetStringOption(realm, opts, "hourCycle", s_hcValues, null);
        if (hour12 != null) hourCycle = null;
        // (ResolveLocale is not observable; it runs below, once all options have been read)

        var tzValue = opts.Get("timeZone");
        string timeZone;
        if (tzValue.IsUndefined) timeZone = toLocaleStringTimeZone ?? SystemTimeZoneIdentifier(realm);
        else
        {
            if (toLocaleStringTimeZone != null) throw JsErrors.TypeError(realm, "the timeZone option is not allowed in ZonedDateTime.prototype.toLocaleString");
            timeZone = Operations.ToString(realm, tzValue);
        }
        var tz = ResolveTimeZone(timeZone) ?? throw JsErrors.RangeError(realm, $"Invalid time zone specified: {timeZone}");

        var formatOptions = new string?[FCount];
        bool hasExplicit = false;
        for (int i = 0; i < FCount; i++)
        {
            string? value;
            if (i == FFsd)
            {
                var n = IntlCore.GetNumberOption(realm, opts, "fractionalSecondDigits", 1, 3, null);
                value = n?.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else value = IntlCore.GetStringOption(realm, opts, FieldNames[i], s_fieldValues[i], null);
            formatOptions[i] = value;
            if (value != null) hasExplicit = true;
        }
        IntlCore.GetStringOption(realm, opts, "formatMatcher", s_formatMatcherValues, "best fit");
        var dateStyle = IntlCore.GetStringOption(realm, opts, "dateStyle", s_styleValues, null);
        var timeStyle = IntlCore.GetStringOption(realm, opts, "timeStyle", s_styleValues, null);
        if (dateStyle != null || timeStyle != null)
        {
            if (hasExplicit) throw JsErrors.TypeError(realm, "dateStyle and timeStyle cannot be combined with explicit date/time components");
            if (required == Req.Date && timeStyle != null) throw JsErrors.TypeError(realm, "timeStyle is not allowed here");
            if (required == Req.Time && dateStyle != null) throw JsErrors.TypeError(realm, "dateStyle is not allowed here");
        }

        // everything below depends only on these inputs: reuse an identical earlier resolution of the realm
        var keySb = new StringBuilder(96);
        foreach (var l in requested) keySb.Append(l).Append(',');
        keySb.Append('\u0001').Append(realm.DefaultLocale).Append('\u0001').Append(calendar).Append('\u0001').Append(numberingSystem)
            .Append('\u0001').Append(hour12 == null ? 'u' : hour12.Value ? 't' : 'f').Append(hourCycle).Append('\u0001').Append(tz.Id)
            .Append('\u0001').Append(dateStyle).Append('\u0001').Append(timeStyle).Append('\u0001').Append((int)required).Append((int)defaults)
            .Append(toLocaleStringTimeZone != null ? 'z' : '-');
        foreach (var v in formatOptions) keySb.Append('\u0001').Append(v);
        var key = keySb.ToString();
        var resolveCache = realm.Intl!.DtfResolveCache;
        if (resolveCache.TryGetValue(key, out var template))
        {
            dtf.CopyResolved(template);
            return dtf;
        }

        var optionValues = new Dictionary<string, string?>(StringComparer.Ordinal) { ["ca"] = calendar, ["nu"] = numberingSystem, ["hc"] = hourCycle };
        var r = IntlCore.ResolveLocale(realm, requested, matcher, optionValues, s_relevantKeys, LocaleData);
        var locale = r["locale"]!;
        if (hour12 != null && IntlCore.GetUnicodeKeyword(locale, "hc") != null) locale = RemoveKeyword(locale, "hc");
        dtf.Locale = locale;
        var resolvedCalendar = r["ca"] ?? "gregory";
        dtf.Calendar = resolvedCalendar;
        dtf.NumberingSystem = r["nu"] ?? "latn";
        var dataLocale = r["dataLocale"]!;
        var icuBase = IcuBaseLocale(dataLocale);
        var hcData = HourCycleData(icuBase);
        string hc;
        if (hour12 == true) hc = hcData.Hc12;
        else if (hour12 == false) hc = "h23";
        else
        {
            var rhc = r["hc"];
            hc = string.IsNullOrEmpty(rhc) ? hcData.Default : rhc;
        }
        dtf.TimeZone = tz.Id;
        dtf.IcuTimeZone = tz.IsOffset ? "GMT" + tz.Id : tz.Id;
        dtf.DateStyle = dateStyle;
        dtf.TimeStyle = timeStyle;

        string icuLocale = icuBase + "@calendar=" + CalendarToIcu(resolvedCalendar) + ";numbers=" + dtf.NumberingSystem;
        dtf.IcuLocale = icuLocale;
        // format records (and their ICU formatters) are shared by all formatters of the realm with the same locale/zone/pattern
        var cache = realm.Intl!.DtfFormatCache;
        string icuTimeZone = dtf.IcuTimeZone;
        DtfFormat Fmt(string? pattern, bool plain)
        {
            var fkey = (plain ? "P\u0001" : "I\u0001" + icuTimeZone + "\u0001") + icuLocale + "\u0001" + pattern;
            if (!cache.TryGetValue(fkey, out var f))
            {
                if (cache.Count >= 256) cache.Clear();
                cache[fkey] = f = new DtfFormat(icuLocale, icuTimeZone, pattern!, plain);
            }
            return f;
        }

        string bestPattern;
        if (dateStyle != null || timeStyle != null)
        {
            bestPattern = StylePattern(icuLocale, dateStyle, timeStyle, hc);
            var baseFields = ParsePatternFields(bestPattern);
            if (dateStyle != null)
            {
                dtf.PlainDateFormat = Fmt(AdjustStyle(icuLocale, bestPattern, baseFields, s_allowDate, hc), true);
                dtf.PlainYearMonthFormat = Fmt(AdjustStyle(icuLocale, bestPattern, baseFields, s_allowYearMonth, hc), true);
                dtf.PlainMonthDayFormat = Fmt(AdjustStyle(icuLocale, bestPattern, baseFields, s_allowMonthDay, hc), true);
            }
            if (timeStyle != null) dtf.PlainTimeFormat = Fmt(AdjustStyle(icuLocale, bestPattern, baseFields, s_allowTime, hc), true);
            dtf.PlainDateTimeFormat = Fmt(AdjustStyle(icuLocale, bestPattern, baseFields, s_allowDateTime, hc), true);
            dtf.InstantFormat = Fmt(bestPattern, false);
        }
        else
        {
            bestPattern = PatternFor(icuLocale, GetDateTimeFormat(formatOptions, hc, required, defaults, inheritAll: true)!, hc);
            string? P(string?[]? f) => f == null ? null : PatternFor(icuLocale, f, hc);
            var pd = P(GetDateTimeFormat(formatOptions, hc, Req.Date, Def.Date, false));
            if (pd != null) dtf.PlainDateFormat = Fmt(pd, true);
            var pym = P(GetDateTimeFormat(formatOptions, hc, Req.YearMonth, Def.YearMonth, false));
            if (pym != null) dtf.PlainYearMonthFormat = Fmt(pym, true);
            var pmd = P(GetDateTimeFormat(formatOptions, hc, Req.MonthDay, Def.MonthDay, false));
            if (pmd != null) dtf.PlainMonthDayFormat = Fmt(pmd, true);
            var pt = P(GetDateTimeFormat(formatOptions, hc, Req.Time, Def.Time, false));
            if (pt != null) dtf.PlainTimeFormat = Fmt(pt, true);
            dtf.PlainDateTimeFormat = Fmt(P(GetDateTimeFormat(formatOptions, hc, Req.Any, Def.All, false)), true);
            var instant = toLocaleStringTimeZone != null
                ? P(GetDateTimeFormat(formatOptions, hc, Req.Any, Def.ZonedDateTime, true))
                : P(GetDateTimeFormat(formatOptions, hc, Req.Any, Def.All, true));
            dtf.InstantFormat = Fmt(instant, false);
        }
        dtf.Format = Fmt(bestPattern, false);
        if (dtf.Format.HasHour) dtf.HourCycle = hc;
        if (resolveCache.Count >= 256) resolveCache.Clear();
        resolveCache[key] = dtf;
        return dtf;
    }

    /// <summary>GetDateTimeFormat: the component record (null when nothing relevant was requested).</summary>
    private static string?[]? GetDateTimeFormat(string?[] options, string hc, Req required, Def defaults, bool inheritAll)
    {
        var requiredOptions = required switch
        {
            Req.Date => s_reqDate, Req.Time => s_reqTime, Req.YearMonth => s_reqYearMonth, Req.MonthDay => s_reqMonthDay, _ => s_reqAny,
        };
        var defaultOptions = defaults switch
        {
            Def.Date => s_defDate, Def.Time => s_defTime, Def.YearMonth => s_reqYearMonth, Def.MonthDay => s_reqMonthDay, _ => s_defAll,
        };
        string?[] formatOptions;
        if (inheritAll) formatOptions = (string?[])options.Clone();
        else
        {
            formatOptions = new string?[FCount];
            if (required is Req.Date or Req.YearMonth or Req.Any) formatOptions[FEra] = options[FEra];
        }
        bool anyPresent = false;
        foreach (var i in s_reqAny) if (options[i] != null) { anyPresent = true; break; }
        bool needDefaults = true;
        foreach (var i in requiredOptions)
        {
            if (options[i] != null)
            {
                formatOptions[i] = options[i];
                needDefaults = false;
            }
        }
        if (needDefaults)
        {
            if (anyPresent && !inheritAll) return null;
            foreach (var i in defaultOptions) formatOptions[i] = "numeric";
            if (defaults == Def.ZonedDateTime && formatOptions[FTzName] == null) formatOptions[FTzName] = "short";
        }
        return formatOptions;
    }

    /// <summary>AdjustDateTimeStyleFormat.</summary>
    private static string AdjustStyle(string icuLocale, string basePattern, string?[] baseFields, int[] allowed, string hc)
    {
        bool conflict = false;
        for (int i = 0; i < FCount; i++)
            if (baseFields[i] != null && Array.IndexOf(allowed, i) < 0) { conflict = true; break; }
        if (!conflict) return basePattern;
        var f = new string?[FCount];
        foreach (var i in allowed) f[i] = baseFields[i];
        return PatternFor(icuLocale, f, hc);
    }

    private static string RemoveKeyword(string tag, string key)
    {
        var t = IntlCore.Parse(tag);
        if (t == null) return tag;
        t.UKeywords.RemoveAll(k => k.Key == key);
        return IntlCore.Format(t);
    }

    private static string CanonicalCalendar(string c) => c switch
    {
        "islamicc" => "islamic-civil",
        "ethiopic-amete-alem" => "ethioaa",
        "gregorian" => "gregory",
        _ => c,
    };

    private static string CalendarToIcu(string c) => c switch
    {
        "gregory" => "gregorian",
        "ethioaa" => "ethiopic-amete-alem",
        _ => c,
    };

    private static string CalendarFromIcu(string c) => c switch
    {
        "gregorian" => "gregory",
        "ethiopic-amete-alem" => "ethioaa",
        _ => c,
    };

    // ------------------------------------------------------------------ time zones

    /// <summary>SystemTimeZoneIdentifier: the engine's configured time zone.</summary>
    internal static string SystemTimeZoneIdentifier(Realm realm)
    {
        var tz = realm.TimeZone;
        if (ReferenceEquals(tz, TimeZoneInfo.Utc)) return "UTC";
        var id = tz.Id;
        if (OperatingSystem.IsWindows() && TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana)) id = iana;
        return ResolveTimeZone(id)?.Id ?? "UTC";
    }

    /// <summary>Validates and case-normalizes a time zone identifier (offsets are normalized to ±HH:MM).</summary>
    private static (string Id, bool IsOffset)? ResolveTimeZone(string id)
    {
        var tz = TemporalTimeZones.Resolve(id);
        if (tz == null) return null;
        if (tz.IsOffset || tz.Id == "UTC") return (tz.Id, tz.IsOffset);
        return (IcuZoneCase(id) ?? tz.Id, false);
    }

    private static Dictionary<string, string>? s_icuZones;

    /// <summary>The identifier of a zone as spelled in ICU's list of time zones (null when ICU does not know it).</summary>
    private static string? IcuZoneCase(string id)
    {
        var map = s_icuZones;
        if (map == null)
        {
            map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int err = 0;
            foreach (var z in Icu.ReadEnum(IcuDtf.ucal_openTimeZones(&err))) map[z] = z;
            s_icuZones = map;
        }
        return map.TryGetValue(id, out var c) ? c : null;
    }

    // ------------------------------------------------------------------ locale data

    private static readonly Dictionary<string, string> s_icuBase = new(StringComparer.Ordinal);

    private static string IcuBaseLocale(string tag)
    {
        lock (s_icuBase)
        {
            if (s_icuBase.TryGetValue(tag, out var v)) return v;
            v = IntlCore.ToIcuLocale(tag);
            s_icuBase[tag] = v;
            return v;
        }
    }

    private static readonly Dictionary<string, string[]> s_calendarData = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string[]> s_nuData = new(StringComparer.Ordinal);
    private static readonly string[] s_hcKeyData = { "", "h11", "h12", "h23", "h24" };
    private static string[]? s_simpleNumberingSystems;

    private static IReadOnlyList<string> LocaleData(string locale, string key)
    {
        if (key == "hc") return s_hcKeyData;
        if (key == "ca")
        {
            lock (s_calendarData)
            {
                if (s_calendarData.TryGetValue(locale, out var list)) return list;
                var def = DefaultCalendar(IcuBaseLocale(locale));
                var l = new List<string> { def };
                foreach (var c in SupportedCalendars) if (c != def) l.Add(c);
                list = l.ToArray();
                s_calendarData[locale] = list;
                return list;
            }
        }
        lock (s_nuData)
        {
            if (s_nuData.TryGetValue(locale, out var list)) return list;
            var def = DefaultNumberingSystem(IcuBaseLocale(locale));
            var l = new List<string> { def };
            foreach (var n in SimpleNumberingSystems()) if (n != def) l.Add(n);
            list = l.ToArray();
            s_nuData[locale] = list;
            return list;
        }
    }

    private static string DefaultCalendar(string icuLocale)
    {
        int err = 0;
        var key = Icu.Utf8Z("calendar");
        var loc = Icu.Utf8Z(icuLocale);
        nint en;
        fixed (byte* k = key) fixed (byte* l = loc) en = IcuDtf.ucal_getKeywordValuesForLocale(k, l, 1, &err);
        if (Icu.Failure(err)) return "gregory";
        var values = Icu.ReadEnum(en);
        if (values.Count == 0) return "gregory";
        var c = CalendarFromIcu(values[0]);
        return Array.IndexOf(SupportedCalendars, c) >= 0 ? c : "gregory";
    }

    private static string DefaultNumberingSystem(string icuLocale)
    {
        int err = 0;
        var loc = Icu.Utf8Z(icuLocale);
        nint ns;
        fixed (byte* l = loc) ns = IcuDtf.unumsys_open(l, &err);
        if (Icu.Failure(err) || ns == 0) return "latn";
        try
        {
            if (IcuDtf.unumsys_isAlgorithmic(ns) != 0) return "latn";
            return Icu.FromCString(IcuDtf.unumsys_getName(ns)) ?? "latn";
        }
        finally { IcuDtf.unumsys_close(ns); }
    }

    /// <summary>Numbering systems with simple decimal digits (usable as "nu" values).</summary>
    internal static string[] SimpleNumberingSystems()
    {
        var s = s_simpleNumberingSystems;
        if (s != null) return s;
        var list = new List<string>();
        int err = 0;
        var names = Icu.ReadEnum(IcuDtf.unumsys_openAvailableNames(&err));
        foreach (var name in names)
        {
            var b = Icu.Utf8Z(name);
            int e = 0;
            nint ns;
            fixed (byte* p = b) ns = IcuDtf.unumsys_openByName(p, &e);
            if (Icu.Failure(e) || ns == 0) continue;
            if (IcuDtf.unumsys_isAlgorithmic(ns) == 0) list.Add(name);
            IcuDtf.unumsys_close(ns);
        }
        if (!list.Contains("latn")) list.Add("latn");
        s_simpleNumberingSystems = s = list.ToArray();
        return s;
    }

    private readonly record struct HcData(string Default, string Hc12);
    private static readonly Dictionary<string, HcData> s_hcData = new(StringComparer.Ordinal);

    /// <summary>[[hourCycle]] and [[hourCycle12]] of a locale ([[hourCycle24]] is always "h23").</summary>
    private static HcData HourCycleData(string icuBase)
    {
        lock (s_hcData)
            if (s_hcData.TryGetValue(icuBase, out var d)) return d;
        string def = "h23", hc12 = "h12";
        int c;
        if (Icu.Has("udatpg_getDefaultHourCycle"))
        {
            lock (s_generators)
            {
                var g = Generator(icuBase);
                int err = 0;
                c = IcuDtf.udatpg_getDefaultHourCycle(g, &err);
                if (Icu.Failure(err)) c = 2;
            }
        }
        else c = Icu.HourCycleFromPattern(BestPattern(icuBase, "j"));   // ICU without the API (e.g. Windows' icu.dll)
        def = c switch { 0 => "h11", 1 => "h12", 3 => "h24", _ => "h23" };
        var p = BestPattern(icuBase, "hm");
        if (HourChar(p) == 'K') hc12 = "h11";
        if (def is "h11" or "h12") def = hc12;
        var result = new HcData(def, hc12);
        lock (s_hcData) s_hcData[icuBase] = result;
        return result;
    }

    // ------------------------------------------------------------------ patterns

    private static readonly Dictionary<string, nint> s_generators = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> s_patternCache = new(StringComparer.Ordinal);

    /// <summary>The (cached) pattern generator of an ICU locale; callers hold the lock on s_generators.</summary>
    private static nint Generator(string icuLocale)
    {
        if (s_generators.TryGetValue(icuLocale, out var g)) return g;
        int err = 0;
        var loc = Icu.Utf8Z(icuLocale);
        fixed (byte* l = loc) g = IcuDtf.udatpg_open(l, &err);
        if (Icu.Failure(err) || g == 0) throw new InvalidOperationException("udatpg_open failed: " + err);
        s_generators[icuLocale] = g;
        return g;
    }

    private const int UDATPG_MATCH_HOUR_FIELD_LENGTH = 1 << 11;

    private static string BestPattern(string icuLocale, string skeleton)
    {
        var key = icuLocale + "\u0001" + skeleton;
        lock (s_patternCache)
            if (s_patternCache.TryGetValue(key, out var cached)) return cached;
        string result;
        lock (s_generators)
        {
            var g = Generator(icuLocale);
            result = Icu.CallUChars((buf, cap, err) =>
            {
                fixed (char* s = skeleton) return IcuDtf.udatpg_getBestPatternWithOptions(g, s, skeleton.Length, UDATPG_MATCH_HOUR_FIELD_LENGTH, buf, cap, err);
            }) ?? skeleton;
        }
        result = WebCompatSpaces(result);
        lock (s_patternCache) s_patternCache[key] = result;
        return result;
    }

    /// <summary>The skeleton of a pattern (udatpg_getSkeleton).</summary>
    internal static string SkeletonOf(string pattern) =>
        Icu.CallUChars((buf, cap, err) => { fixed (char* p = pattern) return IcuDtf.udatpg_getSkeleton(0, p, pattern.Length, buf, cap, err); }) ?? pattern;

    /// <summary>The pattern for a component record (BestFitFormatMatcher via the ICU pattern generator).</summary>
    private static string PatternFor(string icuLocale, string?[] f, string hc)
    {
        bool h12 = hc is "h11" or "h12";
        var sb = new StringBuilder();
        void Add(char c, int n) => sb.Append(c, n);
        static int TextLen(string v) => v switch { "narrow" => 5, "long" => 4, _ => 1 };
        if (f[FEra] is { } era) Add('G', TextLen(era));
        if (f[FYear] is { } year) Add('y', year == "2-digit" ? 2 : 1);
        if (f[FMonth] is { } month) Add('M', month switch { "2-digit" => 2, "numeric" => 1, "narrow" => 5, "short" => 3, _ => 4 });
        if (f[FWeekday] is { } wd) Add('E', wd switch { "narrow" => 5, "long" => 4, _ => 3 });
        if (f[FDay] is { } day) Add('d', day == "2-digit" ? 2 : 1);
        if (f[FDayPeriod] is { } dp && (f[FHour] == null || h12)) Add('B', TextLen(dp));
        if (f[FHour] is { } hour) Add(h12 ? 'h' : 'H', hour == "2-digit" ? 2 : 1);
        if (f[FMinute] is { } minute) Add('m', minute == "2-digit" ? 2 : 1);
        if (f[FSecond] is { } second) Add('s', second == "2-digit" ? 2 : 1);
        if (f[FFsd] is { } fsd) Add('S', fsd[0] - '0');
        if (f[FTzName] is { } tzn)
            sb.Append(tzn switch { "short" => "z", "long" => "zzzz", "shortOffset" => "O", "longOffset" => "OOOO", "shortGeneric" => "v", _ => "vvvv" });
        var pattern = BestPattern(icuLocale, sb.ToString());
        return ApplyHourCycle(icuLocale, pattern, hc);
    }

    private static readonly Dictionary<string, string> s_styleCache = new(StringComparer.Ordinal);

    private static int StyleIndex(string? s) => s switch { "full" => IcuDtf.UDAT_FULL, "long" => IcuDtf.UDAT_LONG, "medium" => IcuDtf.UDAT_MEDIUM, "short" => IcuDtf.UDAT_SHORT, _ => IcuDtf.UDAT_NONE };

    private static string RawStylePattern(string icuLocale, string? dateStyle, string? timeStyle)
    {
        var key = icuLocale + "\u0001" + dateStyle + "\u0001" + timeStyle;
        lock (s_styleCache)
            if (s_styleCache.TryGetValue(key, out var cached)) return cached;
        int err = 0;
        var loc = Icu.Utf8Z(icuLocale);
        const string utc = "UTC";
        nint f;
        fixed (byte* l = loc) fixed (char* t = utc) f = IcuDtf.udat_open(StyleIndex(timeStyle), StyleIndex(dateStyle), l, t, utc.Length, null, 0, &err);
        if (Icu.Failure(err) || f == 0) throw new InvalidOperationException("udat_open failed: " + err);
        string pattern;
        try { pattern = Icu.CallUChars((buf, cap, e) => IcuDtf.udat_toPattern(f, 0, buf, cap, e)) ?? ""; }
        finally { IcuDtf.udat_close(f); }
        pattern = WebCompatSpaces(pattern);
        lock (s_styleCache) s_styleCache[key] = pattern;
        return pattern;
    }

    /// <summary>DateTimeStyleFormat, with the hour cycle applied to the time part.</summary>
    private static string StylePattern(string icuLocale, string? dateStyle, string? timeStyle, string hc)
    {
        var pattern = RawStylePattern(icuLocale, dateStyle, timeStyle);
        if (timeStyle == null) return pattern;
        if (dateStyle == null) return ApplyHourCycle(icuLocale, pattern, hc);
        var timePattern = RawStylePattern(icuLocale, null, timeStyle);
        var fixedTime = ApplyHourCycle(icuLocale, timePattern, hc);
        if (fixedTime == timePattern) return pattern;
        int idx = pattern.IndexOf(timePattern, StringComparison.Ordinal);
        if (idx >= 0) return pattern.Substring(0, idx) + fixedTime + pattern.Substring(idx + timePattern.Length);
        return ApplyHourCycle(icuLocale, pattern, hc);
    }

    /// <summary>
    /// Replaces U+202F NARROW NO-BREAK SPACE (used by CLDR 42+ before day periods) with U+0020, as browsers do for web
    /// compatibility.
    /// </summary>
    private static string WebCompatSpaces(string s) => s.Replace('\u202F', ' ');

    /// <summary>The hour pattern letter of a pattern ('\0' when it has none).</summary>
    private static char HourChar(string pattern)
    {
        bool quoted = false;
        foreach (char c in pattern)
        {
            if (c == '\'') { quoted = !quoted; continue; }
            if (!quoted && c is 'h' or 'H' or 'k' or 'K') return c;
        }
        return '\0';
    }

    /// <summary>Makes a pattern use the hour cycle <paramref name="hc"/> (regenerating it when it switches between 12 and 24 hours).</summary>
    private static string ApplyHourCycle(string icuLocale, string pattern, string hc)
    {
        char hch = HourChar(pattern);
        if (hch == '\0') return pattern;
        bool want12 = hc is "h11" or "h12";
        bool is12 = hch is 'h' or 'K';
        if (want12 != is12)
        {
            var skeleton = SkeletonOf(pattern);
            var sb = new StringBuilder(skeleton.Length + 1);
            foreach (char c in skeleton)
            {
                if (c is 'h' or 'H' or 'k' or 'K') sb.Append(want12 ? 'h' : 'H');
                else if (!want12 && c is 'a' or 'b' or 'B') continue;
                else sb.Append(c);
            }
            pattern = BestPattern(icuLocale, sb.ToString());
        }
        char target = hc switch { "h11" => 'K', "h12" => 'h', "h24" => 'k', _ => 'H' };
        var chars = pattern.ToCharArray();
        bool quoted = false;
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (c == '\'') { quoted = !quoted; continue; }
            if (!quoted && c is 'h' or 'H' or 'k' or 'K') chars[i] = target;
        }
        return new string(chars);
    }

    /// <summary>The components (resolvedOptions values) present in a pattern.</summary>
    internal static string?[] ParsePatternFields(string pattern)
    {
        var f = new string?[FCount];
        int i = 0;
        while (i < pattern.Length)
        {
            char c = pattern[i];
            if (c == '\'')
            {
                i++;
                while (i < pattern.Length && pattern[i] != '\'') i++;
                i++;
                continue;
            }
            int n = 1;
            while (i + n < pattern.Length && pattern[i + n] == c) n++;
            i += n;
            static string Text(int n) => n == 4 ? "long" : n == 5 ? "narrow" : "short";
            switch (c)
            {
                case 'G': f[FEra] = Text(n); break;
                case 'y': case 'Y': case 'u': case 'U': case 'r': f[FYear] ??= n == 2 ? "2-digit" : "numeric"; break;
                case 'M': case 'L': f[FMonth] = n switch { 1 => "numeric", 2 => "2-digit", 3 => "short", 4 => "long", _ => "narrow" }; break;
                case 'd': f[FDay] = n == 2 ? "2-digit" : "numeric"; break;
                case 'E': case 'c': case 'e': f[FWeekday] = n == 6 ? "short" : Text(n); break;
                case 'B': f[FDayPeriod] = Text(n); break;
                case 'h': case 'H': case 'k': case 'K': f[FHour] = n == 2 ? "2-digit" : "numeric"; break;
                case 'm': f[FMinute] = n == 2 ? "2-digit" : "numeric"; break;
                case 's': f[FSecond] = n == 2 ? "2-digit" : "numeric"; break;
                case 'S': f[FFsd] = Math.Min(n, 3).ToString(System.Globalization.CultureInfo.InvariantCulture); break;
                case 'z': f[FTzName] = n >= 4 ? "long" : "short"; break;
                case 'O': f[FTzName] = n >= 4 ? "longOffset" : "shortOffset"; break;
                case 'v': f[FTzName] = n >= 4 ? "longGeneric" : "shortGeneric"; break;
                case 'V': f[FTzName] = n >= 4 ? "longGeneric" : "shortGeneric"; break;
                case 'Z': case 'x': case 'X': f[FTzName] = n >= 4 ? "longOffset" : "shortOffset"; break;
            }
        }
        return f;
    }

    // ------------------------------------------------------------------ ICU objects

    internal static nint OpenUdat(string icuLocale, string tz, string pattern)
    {
        int err = 0;
        var loc = Icu.Utf8Z(icuLocale);
        nint f;
        fixed (byte* l = loc) fixed (char* t = tz) fixed (char* p = pattern)
            f = IcuDtf.udat_open(IcuDtf.UDAT_PATTERN, IcuDtf.UDAT_PATTERN, l, t, tz.Length, p, pattern.Length, &err);
        if (Icu.Failure(err) || f == 0) throw new InvalidOperationException("udat_open failed: " + err);
        err = 0;
        var cal = IcuDtf.ucal_clone(IcuDtf.udat_getCalendar(f), &err);
        if (!Icu.Failure(err) && cal != 0)
        {
            int e = 0;
            IcuDtf.ucal_setGregorianChange(cal, GregorianChange, &e);
            IcuDtf.udat_setCalendar(f, cal);
            IcuDtf.ucal_close(cal);
        }
        return f;
    }

    internal static nint OpenCalendar(string icuLocale, string tz)
    {
        int err = 0;
        var loc = Icu.Utf8Z(icuLocale);
        nint cal;
        fixed (byte* l = loc) fixed (char* t = tz) cal = IcuDtf.ucal_open(t, tz.Length, l, 0, &err);
        if (Icu.Failure(err) || cal == 0) throw new InvalidOperationException("ucal_open failed: " + err);
        int e = 0;
        IcuDtf.ucal_setGregorianChange(cal, GregorianChange, &e);
        return cal;
    }

    internal static nint OpenInterval(string icuLocale, string skeleton, string tz)
    {
        int err = 0;
        var loc = Icu.Utf8Z(icuLocale);
        nint f;
        fixed (byte* l = loc) fixed (char* s = skeleton) fixed (char* t = tz)
            f = IcuDtf.udtitvfmt_open(l, s, skeleton.Length, t, tz.Length, &err);
        if (Icu.Failure(err) || f == 0) throw new InvalidOperationException("udtitvfmt_open failed: " + err);
        return f;
    }

    // ------------------------------------------------------------------ formatting

    /// <summary>HandleDateTimeValue: the format and the epoch milliseconds to format.</summary>
    private static (DtfFormat Format, double Ms) HandleDateTimeValue(Realm realm, JsDateTimeFormat dtf, in JsValue x)
    {
        switch (x.Ref)
        {
            case JsPlainDate d:
                CheckCalendar(realm, d.Calendar, dtf, true);
                return (dtf.PlainDateFormat ?? throw NoOverlap(realm, "Temporal.PlainDate"), PlainMs(new IsoDateTime(d.Date, Noon)));
            case JsPlainYearMonth ym:
                CheckCalendar(realm, ym.Calendar, dtf, false);
                return (dtf.PlainYearMonthFormat ?? throw NoOverlap(realm, "Temporal.PlainYearMonth"), PlainMs(new IsoDateTime(ym.Date, Noon)));
            case JsPlainMonthDay md:
                CheckCalendar(realm, md.Calendar, dtf, false);
                return (dtf.PlainMonthDayFormat ?? throw NoOverlap(realm, "Temporal.PlainMonthDay"), PlainMs(new IsoDateTime(md.Date, Noon)));
            case JsPlainTime t:
                return (dtf.PlainTimeFormat ?? throw NoOverlap(realm, "Temporal.PlainTime"), PlainMs(new IsoDateTime(new IsoDate(1970, 1, 1), t.Time)));
            case JsPlainDateTime dt:
                CheckCalendar(realm, dt.Calendar, dtf, true);
                return (dtf.PlainDateTimeFormat, PlainMs(dt.DateTime));
            case JsInstant i:
                return (dtf.InstantFormat, NsToMs(i.Ns));
            case JsZonedDateTime:
                throw JsErrors.TypeError(realm, "Temporal.ZonedDateTime cannot be formatted with Intl.DateTimeFormat; use toLocaleString");
        }
        double t2 = DateBuiltins.TimeClip(x.AsNumber());
        if (double.IsNaN(t2)) throw JsErrors.RangeError(realm, "Invalid time value");
        return (dtf.Format, t2 + 0.0);
    }

    private static readonly TimeRec Noon = new(12, 0, 0, 0, 0, 0);

    private static double PlainMs(IsoDateTime dt) => NsToMs(TemporalMath.GetUTCEpochNanoseconds(dt));

    private static double NsToMs(Int128 ns)
    {
        var ms = ns / 1_000_000;
        if (ns < 0 && ms * 1_000_000 != ns) ms -= 1;
        return (double)ms;
    }

    private static void CheckCalendar(Realm realm, string calendar, JsDateTimeFormat dtf, bool allowIso)
    {
        if (calendar == dtf.Calendar || (allowIso && calendar == "iso8601")) return;
        throw JsErrors.RangeError(realm, $"calendar {calendar} does not match the formatter's calendar {dtf.Calendar}");
    }

    private static JsThrowException NoOverlap(Realm realm, string type) =>
        JsErrors.TypeError(realm, $"the formatter's options have no fields in common with {type}");

    internal static string FormatDateTime(Realm realm, JsDateTimeFormat dtf, in JsValue x)
    {
        var (fmt, ms) = HandleDateTimeValue(realm, dtf, x);
        return FormatMs(dtf, fmt, ms);
    }

    private static string FormatMs(JsDateTimeFormat dtf, DtfFormat fmt, double ms)
    {
        try { return FormatMsCore(dtf, fmt, ms); }
        finally { GC.KeepAlive(fmt); }
    }

    private static string FormatMsCore(JsDateTimeFormat dtf, DtfFormat fmt, double ms)
    {
        if (fmt.EraOverride != 0 && IcuYear(fmt, ms) < 1)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var (_, v) in FormatMsToParts(fmt, ms)) sb.Append(v);
            return sb.ToString();
        }
        var f = fmt.Udat();
        char* stack = stackalloc char[256];
        int err = 0;
        int n = IcuDtf.udat_format(f, ms, stack, 256, 0, &err);
        if (err == Icu.U_BUFFER_OVERFLOW_ERROR || n > 256)
        {
            var buf = new char[n + 1];
            err = 0;
            fixed (char* p = buf) n = IcuDtf.udat_format(f, ms, p, buf.Length, 0, &err);
            if (Icu.Failure(err)) throw new InvalidOperationException("udat_format failed: " + err);
            return new string(buf, 0, n);
        }
        if (Icu.Failure(err)) throw new InvalidOperationException("udat_format failed: " + err);
        return new string(stack, 0, n);
    }

    private static List<(string Type, string Value)> FormatDateTimeToParts(Realm realm, JsDateTimeFormat dtf, in JsValue x)
    {
        var (fmt, ms) = HandleDateTimeValue(realm, dtf, x);
        return FormatMsToParts(fmt, ms);
    }

    private static List<(string Type, string Value)> FormatMsToParts(DtfFormat fmt, double ms)
    {
        var f = fmt.Udat();
        int err = 0;
        var iter = IcuDtf.ufieldpositer_open(&err);
        if (Icu.Failure(err)) throw new InvalidOperationException("ufieldpositer_open failed");
        try
        {
            string? s = null;
            int cap = 256;
            for (int attempt = 0; attempt < 3 && s == null; attempt++)
            {
                var buf = new char[cap + 1];
                err = 0;
                int n;
                fixed (char* p = buf) n = IcuDtf.udat_formatForFields(f, ms, p, cap, iter, &err);
                if (err == Icu.U_BUFFER_OVERFLOW_ERROR || n > cap)
                {
                    cap = n + 1;
                    IcuDtf.ufieldpositer_close(iter);
                    err = 0;
                    iter = IcuDtf.ufieldpositer_open(&err);
                    continue;
                }
                if (Icu.Failure(err)) throw new InvalidOperationException("udat_formatForFields failed: " + err);
                s = new string(buf, 0, n);
            }
            s ??= "";
            var fields = new List<(int Start, int End, int Field)>();
            while (true)
            {
                int b, e;
                int field = IcuDtf.ufieldpositer_next(iter, &b, &e);
                if (field < 0) break;
                fields.Add((b, e, field));
            }
            fields.Sort((a, c) => a.Start.CompareTo(c.Start));
            var parts = new List<(string, string)>();
            int pos = 0;
            foreach (var (start, end, field) in fields)
            {
                if (start < pos) continue;
                if (start > pos) parts.Add(("literal", s.Substring(pos, start - pos)));
                parts.Add((FieldType(field), s.Substring(start, end - start)));
                pos = end;
            }
            if (pos < s.Length) parts.Add(("literal", s.Substring(pos)));
            if (fmt.EraOverride != 0) parts = ApplyEraOverride(fmt, ms, parts);
            return parts;
        }
        finally { IcuDtf.ufieldpositer_close(iter); GC.KeepAlive(fmt); }
    }

    private const int UCAL_EXTENDED_YEAR = 19;

    /// <summary>The (extended) year ICU's calendar of the format assigns to an instant.</summary>
    private static int IcuYear(DtfFormat fmt, double ms)
    {
        int err = 0;
        var cal = IcuDtf.ucal_clone(IcuDtf.udat_getCalendar(fmt.Udat()), &err);
        if (Icu.Failure(err) || cal == 0) return 1;
        try
        {
            IcuDtf.ucal_setMillis(cal, ms, &err);
            int y = IcuDtf.ucal_get(cal, UCAL_EXTENDED_YEAR, &err);
            return Icu.Failure(err) ? 1 : y;
        }
        finally { IcuDtf.ucal_close(cal); GC.KeepAlive(fmt); }
    }

    /// <summary>
    /// Before the epoch ICU's eras differ from ECMA-402/Temporal's: its Islamic calendars keep "AH" with years 0, -1, …
    /// although CLDR names the "Before Hijrah" era (era 1; Temporal "bh", year 1 - y), and its Coptic calendar switches
    /// to an era without a name where ECMA-402 keeps "am" with negative years. Such dates are formatted from the pattern
    /// with era/year replaced by marks, filling in the locale's era name (of the pattern's width) and year digits.
    /// </summary>
    private static List<(string Type, string Value)> ApplyEraOverride(DtfFormat fmt, double ms, List<(string Type, string Value)> parts)
    {
        int y = IcuYear(fmt, ms);
        if (y >= 1 || !Icu.Has("udat_getSymbols") || !Icu.Has("udat_countSymbols")) return parts;
        var f = fmt.Udat();
        int type = fmt.Fields[FEra] == "long" ? IcuDtf.UDAT_ERA_NAMES : IcuDtf.UDAT_ERAS;
        if (IcuDtf.udat_countSymbols(f, type) < 2) return parts;
        var era = Icu.CallUChars((buf, cap, err) => IcuDtf.udat_getSymbols(f, type, 1, buf, cap, err));
        GC.KeepAlive(fmt);
        if (string.IsNullOrEmpty(era)) return parts;
        long shown = fmt.EraOverride == 1 ? 1L - y : y;   // Islamic: BH year; Coptic: negative AM year
        string year = FormatMsCore(null!, fmt.GregorianYear, TemporalMath.EpochDays(Math.Abs(shown), 7, 1) * 86_400_000.0 + 43_200_000.0);
        if (shown < 0) year = "-" + year;
        var result = new List<(string Type, string Value)>();
        foreach (var (t, v) in FormatMsToParts(fmt.EraTemplate, ms))
        {
            if (t != "literal" || v.IndexOfAny([DtfFormat.EraMark, DtfFormat.YearMark]) < 0) { result.Add((t, v)); continue; }
            int pos = 0;
            for (int i = 0; i < v.Length; i++)
            {
                if (v[i] != DtfFormat.EraMark && v[i] != DtfFormat.YearMark) continue;
                if (i > pos) AddLiteral(result, v.Substring(pos, i - pos));
                result.Add(v[i] == DtfFormat.EraMark ? ("era", era) : ("year", year));
                pos = i + 1;
            }
            if (pos < v.Length) AddLiteral(result, v.Substring(pos));
        }
        return result;

        static void AddLiteral(List<(string Type, string Value)> list, string text)
        {
            if (list.Count > 0 && list[^1].Type == "literal") list[^1] = ("literal", list[^1].Value + text);
            else list.Add(("literal", text));
        }
    }

    /// <summary>Maps a UDateFormatField to a part type.</summary>
    private static string FieldType(int field) => field switch
    {
        0 => "era",
        1 or 18 or 20 => "year",
        2 or 26 => "month",
        3 => "day",
        4 or 5 or 15 or 16 => "hour",
        6 => "minute",
        7 => "second",
        8 => "fractionalSecond",
        9 or 19 or 25 => "weekday",
        14 or 35 or 36 => "dayPeriod",
        17 or 23 or 24 or 29 or 31 or 32 or 33 => "timeZoneName",
        30 => "yearName",
        34 => "relatedYear",
        _ => "unknown",
    };

    private static JsArray PartsToArray(Realm realm, List<(string Type, string Value)> parts, List<string>? sources)
    {
        var items = new JsValue[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            var o = realm.NewObject();
            o.CreateDataPropertyOrThrow("type", new JsValue(parts[i].Type));
            o.CreateDataPropertyOrThrow("value", new JsValue(parts[i].Value));
            if (sources != null) o.CreateDataPropertyOrThrow("source", new JsValue(sources[i]));
            items[i] = JsValue.FromObject(o);
        }
        return Operations.CreateArrayFromList(realm, items);
    }

    private readonly record struct RangePart(string Type, string Value, string Source);

    private static Type? TemporalType(in JsValue v) => IsTemporalObject(v) ? v.Ref!.GetType() : null;

    /// <summary>PartitionDateTimeRangePattern.</summary>
    private static List<RangePart> FormatRangeParts(Realm realm, JsDateTimeFormat dtf, in JsValue x, in JsValue y)
    {
        var tx = TemporalType(x);
        var ty = TemporalType(y);
        if ((tx != null || ty != null) && tx != ty) throw JsErrors.TypeError(realm, "formatRange arguments must be of the same type");
        var (fmt, xms) = HandleDateTimeValue(realm, dtf, x);
        var (_, yms) = HandleDateTimeValue(realm, dtf, y);
        var itv = fmt.Interval(out var cal1, out var cal2);
        int err = 0;
        IcuDtf.ucal_setMillis(cal1, xms, &err);
        IcuDtf.ucal_setMillis(cal2, yms, &err);
        var result = IcuDtf.udtitvfmt_openResult(&err);
        nint cfpos = 0;
        try
        {
            IcuDtf.udtitvfmt_formatCalendarToResult(itv, cal1, cal2, result, &err);
            if (Icu.Failure(err)) throw new InvalidOperationException("udtitvfmt_formatCalendarToResult failed: " + err);
            var value = IcuDtf.udtitvfmt_resultAsValue(result, &err);
            int len = 0;
            var chars = IcuDtf.ufmtval_getString(value, &len, &err);
            var s = WebCompatSpaces(new string(chars, 0, len));
            cfpos = IcuDtf.ucfpos_open(&err);
            var fields = new List<(int Start, int End, int Field)>();
            int span0Start = -1, span0End = -1, span1Start = -1, span1End = -1;
            while (IcuDtf.ufmtval_nextPosition(value, cfpos, &err) != 0 && !Icu.Failure(err))
            {
                int cat = IcuDtf.ucfpos_getCategory(cfpos, &err);
                int field = IcuDtf.ucfpos_getField(cfpos, &err);
                int start, limit;
                IcuDtf.ucfpos_getIndexes(cfpos, &start, &limit, &err);
                if (cat == 1) fields.Add((start, limit, field));
                else if (cat == 0x1005)
                {
                    if (field == 0) { span0Start = start; span0End = limit; }
                    else { span1Start = start; span1End = limit; }
                }
            }
            if (span0Start < 0 && span1Start < 0)
            {
                // no field differs: the collapsed result is the single-date format (PartitionDateTimeRangePattern step 12)
                var single = FormatMsToParts(fmt, xms);
                var collapsed = new List<RangePart>(single.Count);
                foreach (var (type, val) in single) collapsed.Add(new RangePart(type, val, "shared"));
                return collapsed;
            }
            fields.Sort((a, c) => a.Start.CompareTo(c.Start));
            string SourceAt(int i) =>
                i >= span0Start && i < span0End ? "startRange" : i >= span1Start && i < span1End ? "endRange" : "shared";
            var parts = new List<RangePart>();
            int pos = 0;
            void AddLiteral(int from, int to)
            {
                // split literals at span boundaries
                int a = from;
                while (a < to)
                {
                    var src = SourceAt(a);
                    int b = a + 1;
                    while (b < to && SourceAt(b) == src) b++;
                    parts.Add(new RangePart("literal", s.Substring(a, b - a), src));
                    a = b;
                }
            }
            var hourOnly = fmt.HourOnly;
            foreach (var (start, end, field) in fields)
            {
                if (start < pos) continue;
                if (start > pos) AddLiteral(pos, start);
                var type = FieldType(field);
                var src = SourceAt(start);
                var text = s.Substring(start, end - start);
                if (hourOnly != null && type == "hour") text = FormatMs(dtf, hourOnly, src == "endRange" ? yms : xms);
                parts.Add(new RangePart(type, text, src));
                pos = end;
            }
            if (pos < s.Length) AddLiteral(pos, s.Length);
            if (fmt.EraOverride != 0 && (IcuYear(fmt, xms) < 1 || IcuYear(fmt, yms) < 1))
            {
                // ICU's interval formatter shows its own eras (see ApplyEraOverride): use the corrected single-date
                // formats of both ends, joined by the locale's separator taken from ICU's interval result
                var sep = new StringBuilder();
                if (span0End >= 0 && span1Start >= span0End) sep.Append(s, span0End, span1Start - span0End);
                if (sep.Length == 0) sep.Append(" – ");
                // ICU's start span can leave out the space before an era it did not name (Coptic): one space per run
                var separator = System.Text.RegularExpressions.Regex.Replace(sep.ToString(), @"(\s)\s+", "$1");
                var composed = new List<RangePart>();
                foreach (var (type, val) in FormatMsToParts(fmt, xms)) composed.Add(new RangePart(type, val, "startRange"));
                composed.Add(new RangePart("literal", separator, "shared"));
                foreach (var (type, val) in FormatMsToParts(fmt, yms)) composed.Add(new RangePart(type, val, "endRange"));
                return composed;
            }
            return parts;
        }
        finally
        {
            if (cfpos != 0) IcuDtf.ucfpos_close(cfpos);
            IcuDtf.udtitvfmt_closeResult(result);
            GC.KeepAlive(fmt);
        }
    }

    // ------------------------------------------------------------------ resolvedOptions

    private static JsObject ResolvedOptions(Realm realm, JsDateTimeFormat dtf)
    {
        var o = realm.NewObject();
        o.CreateDataPropertyOrThrow("locale", new JsValue(dtf.Locale));
        o.CreateDataPropertyOrThrow("calendar", new JsValue(dtf.Calendar));
        o.CreateDataPropertyOrThrow("numberingSystem", new JsValue(dtf.NumberingSystem));
        o.CreateDataPropertyOrThrow("timeZone", new JsValue(dtf.TimeZone));
        if (dtf.HourCycle != null)
        {
            o.CreateDataPropertyOrThrow("hourCycle", new JsValue(dtf.HourCycle));
            o.CreateDataPropertyOrThrow("hour12", JsValue.FromBool(dtf.HourCycle is "h11" or "h12"));
        }
        if (dtf.DateStyle == null && dtf.TimeStyle == null)
        {
            var f = dtf.Format.Fields;
            for (int i = 0; i < FCount; i++)
            {
                if (f[i] == null) continue;
                o.CreateDataPropertyOrThrow(FieldNames[i], i == FFsd ? new JsValue((double)(f[i]![0] - '0')) : new JsValue(f[i]!));
            }
        }
        if (dtf.DateStyle != null) o.CreateDataPropertyOrThrow("dateStyle", new JsValue(dtf.DateStyle));
        if (dtf.TimeStyle != null) o.CreateDataPropertyOrThrow("timeStyle", new JsValue(dtf.TimeStyle));
        return o;
    }

    // ------------------------------------------------------------------ toLocaleString integrations

    /// <summary>Date.prototype.toLocaleString (kind 0), toLocaleDateString (1) and toLocaleTimeString (2) per ECMA-402.</summary>
    internal static string DateToLocaleString(Realm realm, double t, in JsValue locales, in JsValue options, int kind)
    {
        if (double.IsNaN(t)) return "Invalid Date";
        var intl = realm.Intl!;
        JsDateTimeFormat dtf;
        if (locales.IsUndefined && options.IsUndefined)
        {
            var cached = intl.DateToLocaleCache[kind];
            var tzId = SystemTimeZoneIdentifier(realm);
            if (cached != null && cached.TimeZone == tzId && cached.CacheLocale == realm.DefaultLocale) dtf = cached;
            else
            {
                dtf = CreateForKind(realm, locales, options, kind);
                dtf.CacheLocale = realm.DefaultLocale;
                intl.DateToLocaleCache[kind] = dtf;
            }
        }
        else dtf = CreateForKind(realm, locales, options, kind);
        return FormatMs(dtf, dtf.Format, t);
    }

    private static JsDateTimeFormat CreateForKind(Realm realm, in JsValue locales, in JsValue options, int kind) => kind switch
    {
        1 => Create(realm, realm.Intl!.DateTimeFormatPrototype, locales, options, Req.Date, Def.Date),
        2 => Create(realm, realm.Intl!.DateTimeFormatPrototype, locales, options, Req.Time, Def.Time),
        _ => Create(realm, realm.Intl!.DateTimeFormatPrototype, locales, options, Req.Any, Def.All),
    };

    /// <summary>The ECMA-402 toLocaleString of the Temporal types (PlainDate, PlainTime, PlainDateTime, PlainYearMonth, PlainMonthDay, Instant, ZonedDateTime).</summary>
    internal static string FormatTemporalToLocaleString(Realm realm, JsObject temporalObject, JsValue locales, JsValue options)
    {
        var proto = realm.Intl!.DateTimeFormatPrototype;
        switch (temporalObject)
        {
            case JsZonedDateTime zdt:
            {
                var dtf = Create(realm, proto, locales, options, Req.Any, Def.All, zdt.Tz.Id);
                if (zdt.Calendar != "iso8601" && zdt.Calendar != dtf.Calendar)
                    throw JsErrors.RangeError(realm, $"calendar {zdt.Calendar} does not match the formatter's calendar {dtf.Calendar}");
                return FormatMs(dtf, dtf.InstantFormat, NsToMs(zdt.Ns));
            }
            case JsPlainDate or JsPlainYearMonth or JsPlainMonthDay:
                return FormatDateTime(realm, Create(realm, proto, locales, options, Req.Date, Def.Date), JsValue.FromObject(temporalObject));
            case JsPlainTime:
                return FormatDateTime(realm, Create(realm, proto, locales, options, Req.Time, Def.Time), JsValue.FromObject(temporalObject));
            default:
                return FormatDateTime(realm, Create(realm, proto, locales, options, Req.Any, Def.All), JsValue.FromObject(temporalObject));
        }
    }
}

internal static partial class IntlBuiltins
{
    static partial void SetupDateTimeFormat(Realm r, JsObject intl, IntlProtos protos) => IntlDateTimeFormat.Setup(r, intl, protos);
}
