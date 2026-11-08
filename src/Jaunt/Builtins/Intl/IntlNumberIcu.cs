using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Jaunt.Runtime;

/// <summary>
/// ICU bindings used by Intl.NumberFormat and Intl.PluralRules (unumf/unumrf/ufmtval/ucfpos/uplrules/unumsys/ucurr). Kept in a
/// class of their own (rather than the shared partial <see cref="Icu"/>) so other services can bind the same functions freely.
/// </summary>
internal static unsafe class IcuNum
{
    private static nint P(ref nint slot, string name) => Icu.P(ref slot, name);

    private static nint s_unumfOpen, s_unumfOpenResult, s_unumfFormatDouble, s_unumfFormatDecimal, s_unumfResultAsValue, s_unumfClose, s_unumfCloseResult,
        s_ufmtvalGetString, s_ufmtvalNextPosition, s_ucfposOpen, s_ucfposReset, s_ucfposGetCategory, s_ucfposGetField, s_ucfposGetIndexes,
        s_unumrfOpen, s_unumrfOpenResult, s_unumrfFormatDoubleRange, s_unumrfFormatDecimalRange, s_unumrfResultAsValue, s_unumrfClose,
        s_ucurrDigits, s_unumsysOpen, s_unumsysOpenByName, s_unumsysClose, s_unumsysAvailable, s_unumsysGetName, s_unumsysIsAlgorithmic,
        s_uplOpenForType, s_uplSelect, s_uplSelectFormatted, s_uplSelectForRange, s_uplGetKeywords, s_uplClose;

    public static nint unumf_openForSkeletonAndLocale(char* skeleton, int len, byte* locale, int* err) =>
        ((delegate* unmanaged<char*, int, byte*, int*, nint>)P(ref s_unumfOpen, "unumf_openForSkeletonAndLocale"))(skeleton, len, locale, err);
    public static nint unumf_openResult(int* err) => ((delegate* unmanaged<int*, nint>)P(ref s_unumfOpenResult, "unumf_openResult"))(err);
    public static void unumf_formatDouble(nint f, double v, nint result, int* err) =>
        ((delegate* unmanaged<nint, double, nint, int*, void>)P(ref s_unumfFormatDouble, "unumf_formatDouble"))(f, v, result, err);
    public static void unumf_formatDecimal(nint f, byte* v, int len, nint result, int* err) =>
        ((delegate* unmanaged<nint, byte*, int, nint, int*, void>)P(ref s_unumfFormatDecimal, "unumf_formatDecimal"))(f, v, len, result, err);
    public static nint unumf_resultAsValue(nint result, int* err) => ((delegate* unmanaged<nint, int*, nint>)P(ref s_unumfResultAsValue, "unumf_resultAsValue"))(result, err);
    public static void unumf_close(nint f) => ((delegate* unmanaged<nint, void>)P(ref s_unumfClose, "unumf_close"))(f);
    public static void unumf_closeResult(nint r) => ((delegate* unmanaged<nint, void>)P(ref s_unumfCloseResult, "unumf_closeResult"))(r);

    public static char* ufmtval_getString(nint v, int* len, int* err) => ((delegate* unmanaged<nint, int*, int*, char*>)P(ref s_ufmtvalGetString, "ufmtval_getString"))(v, len, err);
    public static bool ufmtval_nextPosition(nint v, nint pos, int* err) => ((delegate* unmanaged<nint, nint, int*, sbyte>)P(ref s_ufmtvalNextPosition, "ufmtval_nextPosition"))(v, pos, err) != 0;
    public static nint ucfpos_open(int* err) => ((delegate* unmanaged<int*, nint>)P(ref s_ucfposOpen, "ucfpos_open"))(err);
    public static void ucfpos_reset(nint pos, int* err) => ((delegate* unmanaged<nint, int*, void>)P(ref s_ucfposReset, "ucfpos_reset"))(pos, err);
    public static int ucfpos_getCategory(nint pos, int* err) => ((delegate* unmanaged<nint, int*, int>)P(ref s_ucfposGetCategory, "ucfpos_getCategory"))(pos, err);
    public static int ucfpos_getField(nint pos, int* err) => ((delegate* unmanaged<nint, int*, int>)P(ref s_ucfposGetField, "ucfpos_getField"))(pos, err);
    public static void ucfpos_getIndexes(nint pos, int* start, int* limit, int* err) =>
        ((delegate* unmanaged<nint, int*, int*, int*, void>)P(ref s_ucfposGetIndexes, "ucfpos_getIndexes"))(pos, start, limit, err);

    public static nint unumrf_openForSkeletonWithCollapseAndIdentityFallback(char* skeleton, int len, int collapse, int identityFallback, byte* locale, int* err) =>
        ((delegate* unmanaged<char*, int, int, int, byte*, void*, int*, nint>)P(ref s_unumrfOpen, "unumrf_openForSkeletonWithCollapseAndIdentityFallback"))(
            skeleton, len, collapse, identityFallback, locale, null, err);
    public static nint unumrf_openResult(int* err) => ((delegate* unmanaged<int*, nint>)P(ref s_unumrfOpenResult, "unumrf_openResult"))(err);
    public static void unumrf_formatDoubleRange(nint f, double a, double b, nint result, int* err) =>
        ((delegate* unmanaged<nint, double, double, nint, int*, void>)P(ref s_unumrfFormatDoubleRange, "unumrf_formatDoubleRange"))(f, a, b, result, err);
    public static void unumrf_formatDecimalRange(nint f, byte* a, int alen, byte* b, int blen, nint result, int* err) =>
        ((delegate* unmanaged<nint, byte*, int, byte*, int, nint, int*, void>)P(ref s_unumrfFormatDecimalRange, "unumrf_formatDecimalRange"))(f, a, alen, b, blen, result, err);
    public static nint unumrf_resultAsValue(nint result, int* err) => ((delegate* unmanaged<nint, int*, nint>)P(ref s_unumrfResultAsValue, "unumrf_resultAsValue"))(result, err);
    public static void unumrf_close(nint f) => ((delegate* unmanaged<nint, void>)P(ref s_unumrfClose, "unumrf_close"))(f);

    public static int ucurr_getDefaultFractionDigits(char* currency, int* err) =>
        ((delegate* unmanaged<char*, int*, int>)P(ref s_ucurrDigits, "ucurr_getDefaultFractionDigits"))(currency, err);

    public static nint unumsys_open(byte* locale, int* err) => ((delegate* unmanaged<byte*, int*, nint>)P(ref s_unumsysOpen, "unumsys_open"))(locale, err);
    public static nint unumsys_openByName(byte* name, int* err) => ((delegate* unmanaged<byte*, int*, nint>)P(ref s_unumsysOpenByName, "unumsys_openByName"))(name, err);
    public static void unumsys_close(nint ns) => ((delegate* unmanaged<nint, void>)P(ref s_unumsysClose, "unumsys_close"))(ns);
    public static nint unumsys_openAvailableNames(int* err) => ((delegate* unmanaged<int*, nint>)P(ref s_unumsysAvailable, "unumsys_openAvailableNames"))(err);
    public static byte* unumsys_getName(nint ns) => ((delegate* unmanaged<nint, byte*>)P(ref s_unumsysGetName, "unumsys_getName"))(ns);
    public static bool unumsys_isAlgorithmic(nint ns) => ((delegate* unmanaged<nint, sbyte>)P(ref s_unumsysIsAlgorithmic, "unumsys_isAlgorithmic"))(ns) != 0;

    public static nint uplrules_openForType(byte* locale, int type, int* err) => ((delegate* unmanaged<byte*, int, int*, nint>)P(ref s_uplOpenForType, "uplrules_openForType"))(locale, type, err);
    public static int uplrules_select(nint pr, double number, char* buf, int cap, int* err) =>
        ((delegate* unmanaged<nint, double, char*, int, int*, int>)P(ref s_uplSelect, "uplrules_select"))(pr, number, buf, cap, err);
    public static int uplrules_selectFormatted(nint pr, nint formatted, char* buf, int cap, int* err) =>
        ((delegate* unmanaged<nint, nint, char*, int, int*, int>)P(ref s_uplSelectFormatted, "uplrules_selectFormatted"))(pr, formatted, buf, cap, err);
    public static int uplrules_selectForRange(nint pr, nint formatted, char* buf, int cap, int* err) =>
        ((delegate* unmanaged<nint, nint, char*, int, int*, int>)P(ref s_uplSelectForRange, "uplrules_selectForRange"))(pr, formatted, buf, cap, err);
    public static nint uplrules_getKeywords(nint pr, int* err) => ((delegate* unmanaged<nint, int*, nint>)P(ref s_uplGetKeywords, "uplrules_getKeywords"))(pr, err);
    public static void uplrules_close(nint pr) => ((delegate* unmanaged<nint, void>)P(ref s_uplClose, "uplrules_close"))(pr);

    // UFieldCategory values
    public const int UFIELD_CATEGORY_NUMBER = 2;
    public const int UFIELD_CATEGORY_NUMBER_RANGE_SPAN = 0x1000 + 2;
}

/// <summary>
/// A cached ICU <c>UNumberFormatter</c> (and lazily its <c>UNumberRangeFormatter</c>) for one (locale, skeleton) pair. ICU
/// formatters are immutable and thread-safe; instances are shared by every Intl object with the same resolved options and
/// freed by the finalizer once no longer referenced.
/// </summary>
internal sealed unsafe class IcuNumberFormatter
{
    public readonly nint Ptr;
    private nint _range;
    private readonly string _skeleton;
    private readonly byte[] _locale;

    private IcuNumberFormatter(nint ptr, string skeleton, byte[] locale)
    {
        Ptr = ptr;
        _skeleton = skeleton;
        _locale = locale;
    }

    ~IcuNumberFormatter()
    {
        if (Ptr != 0) IcuNum.unumf_close(Ptr);
        if (_range != 0) IcuNum.unumrf_close(_range);
    }

    private static readonly ConcurrentDictionary<(string, string), IcuNumberFormatter> s_cache = new();

    /// <summary>Returns a (shared) formatter; null when ICU rejects the skeleton.</summary>
    public static IcuNumberFormatter? Get(string icuLocale, string skeleton)
    {
        var key = (icuLocale, skeleton);
        if (s_cache.TryGetValue(key, out var f)) return f;
        var loc = Icu.Utf8Z(icuLocale);
        int err = 0;
        nint p;
        fixed (char* s = skeleton)
        fixed (byte* l = loc)
            p = IcuNum.unumf_openForSkeletonAndLocale(s, skeleton.Length, l, &err);
        if (Icu.Failure(err) || p == 0)
        {
            if (p != 0) IcuNum.unumf_close(p);
            return null;
        }
        var fmt = new IcuNumberFormatter(p, skeleton, loc);
        if (s_cache.Count >= 512) s_cache.Clear();
        return s_cache.GetOrAdd(key, fmt);   // a losing duplicate is freed by its finalizer
    }

    private IntegerLayout? _integerLayout;
    private bool _integerLayoutProbed;

    /// <summary>How this formatter writes plain integers, when that can be done without ICU; probed once per formatter.</summary>
    public IntegerLayout? IntegerLayout
    {
        get
        {
            if (!_integerLayoutProbed)
            {
                _integerLayout = Jaunt.Runtime.IntegerLayout.Probe(this);
                _integerLayoutProbed = true;
            }
            return _integerLayout;
        }
    }

    /// <summary>The text ICU produces for a Number.</summary>
    public string FormatDouble(double x)
    {
        var result = NumberResult;
        int err = 0;
        IcuNum.unumf_formatDouble(Ptr, x, result, &err);
        var v = IcuNum.unumf_resultAsValue(result, &err);
        int len = 0;
        var s = IcuNum.ufmtval_getString(v, &len, &err);
        GC.KeepAlive(this);
        if (Icu.Failure(err)) throw Icu.Error("ICU number formatting failed (" + err + ")");
        return new string(s, 0, len);
    }

    /// <summary>The range formatter (collapse "auto", identity fallback "approximately").</summary>
    public nint Range
    {
        get
        {
            var r = _range;
            if (r != 0) return r;
            int err = 0;
            fixed (char* s = _skeleton)
            fixed (byte* l = _locale)
                r = IcuNum.unumrf_openForSkeletonWithCollapseAndIdentityFallback(s, _skeleton.Length, 0, 2, l, &err);
            if (Icu.Failure(err) || r == 0) throw Icu.Error("ICU number range formatter could not be created");
            if (Interlocked.CompareExchange(ref _range, r, 0) != 0)
            {
                IcuNum.unumrf_close(r);
                r = _range;
            }
            GC.KeepAlive(this);
            return r;
        }
    }

    // ------------------------------------------------------------------ per-thread result objects

    [ThreadStatic] private static ResultHolder? t_results;

    /// <summary>Per-thread reusable ICU result objects (freed when the thread's holder is collected).</summary>
    private sealed class ResultHolder
    {
        public nint Number, Range, Pos;
        ~ResultHolder()
        {
            if (Number != 0) IcuNum.unumf_closeResult(Number);
            if (Range != 0) ((delegate* unmanaged<nint, void>)Icu.Sym("unumrf_closeResult"))(Range);
            if (Pos != 0) ((delegate* unmanaged<nint, void>)Icu.Sym("ucfpos_close"))(Pos);
        }
    }

    private static ResultHolder Results => t_results ??= new ResultHolder();

    public static nint NumberResult
    {
        get
        {
            var h = Results;
            if (h.Number == 0) { int err = 0; h.Number = IcuNum.unumf_openResult(&err); }
            return h.Number;
        }
    }

    public static nint RangeResult
    {
        get
        {
            var h = Results;
            if (h.Range == 0) { int err = 0; h.Range = IcuNum.unumrf_openResult(&err); }
            return h.Range;
        }
    }

    public static nint FieldPosition
    {
        get
        {
            var h = Results;
            if (h.Pos == 0) { int err = 0; h.Pos = IcuNum.ucfpos_open(&err); }
            return h.Pos;
        }
    }
}

/// <summary>
/// The way one ICU decimal formatter writes integers: a sign prefix, Latin digits and a grouping separator placed by primary,
/// secondary and minimum-grouping sizes. Those are all the inputs ICU's own grouping has, so the layout is read from what the
/// formatter prints and then checked against it for every digit count in range; a formatter that prints anything else has no
/// layout and stays with ICU. Formatting an integer this way is several times cheaper than a round trip through ICU's formatter.
/// </summary>
internal sealed class IntegerLayout
{
    /// <summary>Exclusive bound of the magnitudes covered: every digit count below it is checked by <see cref="Probe"/>.</summary>
    public const double Limit = 1e15;
    private const int MaxPrefix = 8;

    private readonly string _negativePrefix;
    private readonly char _separator;   // '\0' when the formatter does not group
    private readonly int _primary, _secondary, _minGrouping;

    private IntegerLayout(string negativePrefix, char separator, int primary, int secondary, int minGrouping)
    {
        _negativePrefix = negativePrefix;
        _separator = separator;
        _primary = primary;
        _secondary = secondary;
        _minGrouping = minGrouping;
    }

    public static bool Covers(double x) => Math.Abs(x) < Limit && x == Math.Floor(x) && (x != 0 || !double.IsNegative(x));

    /// <summary>The text of an integer accepted by <see cref="Covers"/>.</summary>
    public string Format(double x)
    {
        Span<char> buf = stackalloc char[MaxPrefix + 15 + 7];
        ulong n = (ulong)Math.Abs(x);
        int digits = 1;
        for (ulong m = n; m >= 10; m /= 10) digits++;
        bool grouped = _separator != '\0' && digits >= _primary + _minGrouping;
        int pos = buf.Length, written = 0, next = _primary;
        do
        {
            buf[--pos] = (char)('0' + (int)(n % 10));
            n /= 10;
            if (grouped && ++written == next && n != 0)
            {
                buf[--pos] = _separator;
                next += _secondary;
            }
        }
        while (n != 0);
        if (x < 0)
        {
            pos -= _negativePrefix.Length;
            _negativePrefix.AsSpan().CopyTo(buf.Slice(pos));
        }
        return new string(buf.Slice(pos));
    }

    /// <summary>Reads the layout of a formatter and verifies it; null when the formatter does anything else.</summary>
    public static IntegerLayout? Probe(IcuNumberFormatter f)
    {
        if (f.FormatDouble(1) != "1") return null;
        var minusOne = f.FormatDouble(-1);
        if (minusOne.Length is 0 or > MaxPrefix + 1 || minusOne[^1] != '1') return null;
        var prefix = minusOne.Substring(0, minusOne.Length - 1);

        char separator = '\0';
        int primary = 0, secondary = 0, minGrouping = 0;
        var wide = f.FormatDouble(123456789012345);
        int last = LastNonDigit(wide, wide.Length);
        if (last >= 0)
        {
            int previous = LastNonDigit(wide, last);
            if (previous < 0) return null;
            separator = wide[last];
            primary = wide.Length - 1 - last;
            secondary = last - previous - 1;
            if (primary < 2 || secondary < 2) return null;
            double value = Math.Pow(10, primary);
            for (minGrouping = 1; minGrouping <= 3; minGrouping++, value *= 10)
                if (f.FormatDouble(value).Contains(separator)) break;
            if (minGrouping > 3) return null;
        }
        var layout = new IntegerLayout(prefix, separator, primary, secondary, minGrouping);
        if (f.FormatDouble(0) != "0") return null;
        // both digit strings cover all ten digits; every length up to 15 digits, with either sign
        foreach (double digits in new double[] { 123456789012345, 987654320987654 })
            for (double v = digits; v >= 1; v = Math.Floor(v / 10))
                if (layout.Format(v) != f.FormatDouble(v) || layout.Format(-v) != f.FormatDouble(-v)) return null;
        return layout;
    }

    private static int LastNonDigit(string s, int end)
    {
        for (int i = end - 1; i >= 0; i--)
            if (s[i] is < '0' or > '9') return i;
        return -1;
    }
}
