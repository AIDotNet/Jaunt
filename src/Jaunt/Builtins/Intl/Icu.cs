using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Jaunt.Runtime;

/// <summary>
/// Run-time binding to the platform's ICU (the library ECMA-402 implementations are built on): the system
/// <c>libicucore</c> on macOS, <c>icu.dll</c> on Windows 10+, and the newest <c>libicuuc.so.N</c> /
/// <c>libicui18n.so.N</c> (versioned symbols) on Linux. Function pointers are resolved lazily with
/// <see cref="NativeLibrary"/>, which works under NativeAOT. When no ICU is found <see cref="Available"/> is false and
/// the <c>Intl</c> global is not installed.
/// </summary>
internal static unsafe partial class Icu
{
    /// <summary>
    /// An ICU call failed (e.g. a date outside ICU's range in formatRange): during an execution this is a catchable JS
    /// RangeError of the running realm, not an internal error that faults the engine.
    /// </summary>
    internal static Exception Error(string message)
        => Vm.OnThread is { } vm ? JsErrors.RangeError(vm.Realm, "Intl: " + message) : new InvalidOperationException(message);

    public const int U_ZERO_ERROR = 0;
    public const int U_BUFFER_OVERFLOW_ERROR = 15;
    public const int U_STRING_NOT_TERMINATED_WARNING = -124;

    private static readonly object s_lock = new();
    private static volatile bool s_initialized;
    private static bool s_available;
    private static nint s_uc, s_i18n;
    private static string s_suffix = "";

    public static bool Available
    {
        get
        {
            if (!s_initialized) Init();
            return s_available;
        }
    }

    public static string Version { get; private set; } = "";

    private static void Init()
    {
        lock (s_lock)
        {
            if (s_initialized) return;
            try { s_available = TryLoad(); }
            catch (Exception) { s_available = false; }
            if (s_available && NativeLibrary.TryGetExport(s_uc, "u_getVersion" + s_suffix, out var gv))
            {
                var v = stackalloc byte[4];
                ((delegate* unmanaged<byte*, void>)gv)(v);
                Version = $"{v[0]}.{v[1]}";
            }
            s_initialized = true;
        }
    }

    /// <summary>
    /// ICU functions Jaunt binds unconditionally. A library missing any of them (e.g. the older system icu.dll of some
    /// Windows versions) is not used; optional functions with fallbacks are probed with <see cref="Has"/>.
    /// </summary>
    private static readonly string[] s_required =
    {
        "u_strToLower", "u_strToUpper", "ubrk_close", "ubrk_following", "ubrk_getRuleStatus", "ubrk_open",
        "ubrk_preceding", "ubrk_setText", "ucal_clone", "ucal_close", "ucal_get", "ucal_getAttribute",
        "ucal_getCanonicalTimeZoneID", "ucal_getDayOfWeekType", "ucal_getKeywordValuesForLocale", "ucal_getLimit", "ucal_getMillis", "ucal_getTimeZoneTransitionDate",
        "ucal_open", "ucal_openTimeZoneIDEnumeration", "ucal_openTimeZones", "ucal_setGregorianChange", "ucal_setMillis", "ucfpos_close",
        "ucfpos_constrainField", "ucfpos_getCategory", "ucfpos_getField", "ucfpos_getIndexes", "ucfpos_open", "ucfpos_reset",
        "ucol_close", "ucol_getAttribute", "ucol_getKeywordValues", "ucol_getKeywordValuesForLocale", "ucol_open", "ucol_setAttribute",
        "ucol_strcoll", "ucurr_getDefaultFractionDigits", "ucurr_getName", "ucurr_openISOCurrencies", "udat_close", "udat_format",
        "udat_formatForFields", "udat_getCalendar", "udat_open", "udat_setCalendar", "udat_toPattern", "udatpg_close",
        "udatpg_getBestPattern", "udatpg_getBestPatternWithOptions", "udatpg_getFieldDisplayName", "udatpg_getSkeleton", "udatpg_open", "udtitvfmt_close",
        "udtitvfmt_closeResult", "udtitvfmt_formatCalendarToResult", "udtitvfmt_open", "udtitvfmt_openResult", "udtitvfmt_resultAsValue", "uenum_close",
        "uenum_next", "uenum_unext", "ufieldpositer_close", "ufieldpositer_next", "ufieldpositer_open", "ufmtval_getString",
        "ufmtval_nextPosition", "uldn_close", "uldn_keyValueDisplayName", "uldn_localeDisplayName", "uldn_openForContext", "uldn_regionDisplayName",
        "uldn_scriptDisplayName", "ulistfmt_close", "ulistfmt_closeResult", "ulistfmt_formatStringsToResult", "ulistfmt_openForType", "ulistfmt_openResult",
        "ulistfmt_resultAsValue", "uloc_addLikelySubtags", "uloc_canonicalize", "uloc_countAvailable", "uloc_forLanguageTag", "uloc_getAvailable",
        "uloc_isRightToLeft", "uloc_minimizeSubtags", "uloc_toLanguageTag", "uloc_toLegacyType", "uloc_toUnicodeLocaleType", "unum_open",
        "unum_setAttribute", "unumf_close", "unumf_closeResult", "unumf_formatDecimal", "unumf_formatDouble", "unumf_openForSkeletonAndLocale",
        "unumf_openResult", "unumf_resultAsValue", "unumf_resultGetAllFieldPositions", "unumf_resultToString", "unumrf_close", "unumrf_formatDecimalRange",
        "unumrf_formatDoubleRange", "unumrf_openForSkeletonWithCollapseAndIdentityFallback", "unumrf_openResult", "unumrf_resultAsValue", "unumsys_close", "unumsys_getDescription",
        "unumsys_getName", "unumsys_getRadix", "unumsys_isAlgorithmic", "unumsys_open", "unumsys_openAvailableNames", "unumsys_openByName",
        "uplrules_close", "uplrules_getKeywords", "uplrules_openForType", "uplrules_selectFormatted", "uplrules_selectForRange", "ureldatefmt_close",
        "ureldatefmt_closeResult", "ureldatefmt_formatNumericToResult", "ureldatefmt_formatToResult", "ureldatefmt_open", "ureldatefmt_openResult", "ureldatefmt_resultAsValue",
    };

    private static bool Complete(nint uc, nint i18n, string suffix)
    {
        foreach (var n in s_required)
            if (!NativeLibrary.TryGetExport(uc, n + suffix, out _) && (i18n == uc || !NativeLibrary.TryGetExport(i18n, n + suffix, out _))) return false;
        return true;
    }

    private static bool TryLoad()
    {
        if (Environment.GetEnvironmentVariable("JAUNT_DISABLE_ICU") == "1") return false;
        foreach (var (uc, i18n, suffix) in Candidates())
        {
            if (!NativeLibrary.TryGetExport(uc, "u_getVersion" + suffix, out _) || !Complete(uc, i18n, suffix)) continue;
            s_uc = uc;
            s_i18n = i18n;
            s_suffix = suffix;
            return true;
        }
        return false;
    }

    /// <summary>ICU libraries to try, best first: app-local versioned DLLs on Windows (Microsoft.ICU.ICU4C.Runtime), then the system ICU.</summary>
    private static System.Collections.Generic.IEnumerable<(nint Uc, nint I18n, string Suffix)> Candidates()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst())
        {
            if (NativeLibrary.TryLoad("/usr/lib/libicucore.dylib", out var h)) yield return (h, h, "");
            yield break;
        }
        if (OperatingSystem.IsWindows())
        {
            // app-local ICU resolved through the app's native search paths (deps.json / next to the executable)
            var asm = typeof(Icu).Assembly;
            for (int v = 99; v >= 50; v--)
            {
                if (NativeLibrary.TryLoad($"icuuc{v}.dll", asm, null, out var uc) && NativeLibrary.TryLoad($"icuin{v}.dll", asm, null, out var i18n))
                    yield return (uc, i18n, "_" + v);
            }
            if (NativeLibrary.TryLoad("icu.dll", out var sys)) yield return (sys, sys, "");
            yield break;
        }
        // Linux and other Unix: newest libicuuc.so.N with its matching libicui18n
        for (int v = 99; v >= 50; v--)
        {
            if (!NativeLibrary.TryLoad($"libicuuc.so.{v}", out var uc)) continue;
            if (!NativeLibrary.TryLoad($"libicui18n.so.{v}", out var i18n)) continue;
            if (NativeLibrary.TryGetExport(uc, "u_getVersion_" + v, out _)) yield return (uc, i18n, "_" + v);
            else yield return (uc, i18n, "");
        }
    }

    /// <summary>Resolves an ICU C API function (with the library's version suffix, if any).</summary>
    public static nint Sym(string name)
    {
        if (!s_initialized) Init();
        var full = name + s_suffix;
        if (NativeLibrary.TryGetExport(s_uc, full, out var p)) return p;
        if (s_i18n != s_uc && NativeLibrary.TryGetExport(s_i18n, full, out p)) return p;
        throw new EntryPointNotFoundException("ICU function not found: " + full);
    }

    /// <summary>True when the platform ICU exports the function (some, e.g. Windows' icu.dll, omit newer APIs).</summary>
    public static bool Has(string name)
    {
        if (!s_initialized) Init();
        if (!s_available) return false;
        var full = name + s_suffix;
        return NativeLibrary.TryGetExport(s_uc, full, out _) || (s_i18n != s_uc && NativeLibrary.TryGetExport(s_i18n, full, out _));
    }

    /// <summary>Hour cycle from a locale's preferred hour pattern character ('j' skeleton): 0 h11, 1 h12, 2 h23, 3 h24.</summary>
    public static int HourCycleFromPattern(string? pattern)
    {
        if (pattern == null) return 2;
        bool quoted = false;
        foreach (char c in pattern)
        {
            if (c == '\'') { quoted = !quoted; continue; }
            if (quoted) continue;
            switch (c) { case 'K': return 0; case 'h': return 1; case 'H': return 2; case 'k': return 3; }
        }
        return 2;
    }

    /// <summary>Lazily resolves into a cache slot.</summary>
    public static nint P(ref nint slot, string name)
    {
        var p = slot;
        if (p == 0) slot = p = Sym(name);
        return p;
    }

    public static bool Failure(int err) => err > U_ZERO_ERROR;

    // ------------------------------------------------------------------ string helpers

    /// <summary>NUL-terminated ASCII/UTF-8 bytes for a char* argument.</summary>
    public static byte[] Utf8Z(string s)
    {
        var b = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, 0, s.Length, b, 0);
        return b;
    }

    public delegate int CharCall(byte* buf, int cap, int* err);
    public delegate int UCharCall(char* buf, int cap, int* err);

    /// <summary>Calls an ICU function that fills a char buffer (retrying on overflow).</summary>
    public static string? CallChars(CharCall call)
    {
        int cap = 128;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var buf = new byte[cap + 1];
            int err = 0;
            int n;
            fixed (byte* p = buf) n = call(p, cap, &err);
            if (err == U_BUFFER_OVERFLOW_ERROR || n > cap) { cap = n + 1; continue; }
            if (Failure(err)) return null;
            return Encoding.UTF8.GetString(buf, 0, n);
        }
        return null;
    }

    /// <summary>Calls an ICU function that fills a UChar buffer (retrying on overflow).</summary>
    public static string? CallUChars(UCharCall call)
    {
        int cap = 256;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var buf = new char[cap + 1];
            int err = 0;
            int n;
            fixed (char* p = buf) n = call(p, cap, &err);
            if (err == U_BUFFER_OVERFLOW_ERROR || n > cap) { cap = n + 1; continue; }
            if (Failure(err)) return null;
            return new string(buf, 0, n);
        }
        return null;
    }

    public static string? FromCString(byte* p) => p == null ? null : Marshal.PtrToStringUTF8((nint)p);

    // ------------------------------------------------------------------ uloc (locale identifiers)

    private static nint s_forLanguageTag, s_toLanguageTag, s_addLikely, s_minimize, s_countAvailable, s_getAvailable, s_canonicalize,
        s_uenumNext, s_uenumUnext, s_uenumClose;

    /// <summary>BCP 47 language tag → ICU locale ID (null on error).</summary>
    public static string? ForLanguageTag(string tag)
    {
        var t = Utf8Z(tag);
        var f = (delegate* unmanaged<byte*, byte*, int, int*, int*, int>)P(ref s_forLanguageTag, "uloc_forLanguageTag");
        int parsed = 0;
        var r = CallChars((buf, cap, err) => { fixed (byte* tp = t) { int pl; int n = f(tp, buf, cap, &pl, err); parsed = pl; return n; } });
        if (r == null || parsed != tag.Length) return null;
        return r;
    }

    /// <summary>ICU locale ID → BCP 47 language tag.</summary>
    public static string? ToLanguageTag(string localeId)
    {
        var l = Utf8Z(localeId);
        var f = (delegate* unmanaged<byte*, byte*, int, sbyte, int*, int>)P(ref s_toLanguageTag, "uloc_toLanguageTag");
        return CallChars((buf, cap, err) => { fixed (byte* lp = l) return f(lp, buf, cap, 0, err); });
    }

    public static string? Canonicalize(string localeId)
    {
        var l = Utf8Z(localeId);
        var f = (delegate* unmanaged<byte*, byte*, int, int*, int>)P(ref s_canonicalize, "uloc_canonicalize");
        return CallChars((buf, cap, err) => { fixed (byte* lp = l) return f(lp, buf, cap, err); });
    }

    public static string? AddLikelySubtags(string localeId)
    {
        var l = Utf8Z(localeId);
        var f = (delegate* unmanaged<byte*, byte*, int, int*, int>)P(ref s_addLikely, "uloc_addLikelySubtags");
        return CallChars((buf, cap, err) => { fixed (byte* lp = l) return f(lp, buf, cap, err); });
    }

    public static string? MinimizeSubtags(string localeId)
    {
        var l = Utf8Z(localeId);
        var f = (delegate* unmanaged<byte*, byte*, int, int*, int>)P(ref s_minimize, "uloc_minimizeSubtags");
        return CallChars((buf, cap, err) => { fixed (byte* lp = l) return f(lp, buf, cap, err); });
    }

    public static string[] AvailableLocaleIds()
    {
        var count = ((delegate* unmanaged<int>)P(ref s_countAvailable, "uloc_countAvailable"))();
        var get = (delegate* unmanaged<int, byte*>)P(ref s_getAvailable, "uloc_getAvailable");
        var r = new string[count];
        for (int i = 0; i < count; i++) r[i] = FromCString(get(i)) ?? "";
        return r;
    }

    /// <summary>Reads (and closes) a UEnumeration of char* strings.</summary>
    public static System.Collections.Generic.List<string> ReadEnum(nint en)
    {
        var list = new System.Collections.Generic.List<string>();
        if (en == 0) return list;
        var next = (delegate* unmanaged<nint, int*, int*, byte*>)P(ref s_uenumNext, "uenum_next");
        var close = (delegate* unmanaged<nint, void>)P(ref s_uenumClose, "uenum_close");
        try
        {
            while (true)
            {
                int len = 0, err = 0;
                var p = next(en, &len, &err);
                if (p == null || Failure(err)) break;
                list.Add(Encoding.UTF8.GetString(p, len));
            }
        }
        finally { close(en); }
        return list;
    }

    /// <summary>Reads (and closes) a UEnumeration of UChar* strings.</summary>
    public static System.Collections.Generic.List<string> ReadUEnum(nint en)
    {
        var list = new System.Collections.Generic.List<string>();
        if (en == 0) return list;
        var unext = (delegate* unmanaged<nint, int*, int*, char*>)P(ref s_uenumUnext, "uenum_unext");
        var close = (delegate* unmanaged<nint, void>)P(ref s_uenumClose, "uenum_close");
        try
        {
            while (true)
            {
                int len = 0, err = 0;
                var p = unext(en, &len, &err);
                if (p == null || Failure(err)) break;
                list.Add(new string(p, 0, len));
            }
        }
        finally { close(en); }
        return list;
    }
}
