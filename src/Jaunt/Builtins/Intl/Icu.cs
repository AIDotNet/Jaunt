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

    private static bool TryLoad()
    {
        if (Environment.GetEnvironmentVariable("JAUNT_DISABLE_ICU") == "1") return false;
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst())
        {
            if (NativeLibrary.TryLoad("/usr/lib/libicucore.dylib", out var h))
            {
                s_uc = s_i18n = h;
                return NativeLibrary.TryGetExport(h, "u_getVersion", out _);
            }
            return false;
        }
        if (OperatingSystem.IsWindows())
        {
            if (NativeLibrary.TryLoad("icu.dll", out var h))
            {
                s_uc = s_i18n = h;
                return NativeLibrary.TryGetExport(h, "u_getVersion", out _);
            }
            return false;
        }
        // Linux and other Unix: newest libicuuc.so.N with its matching libicui18n
        for (int v = 99; v >= 50; v--)
        {
            if (!NativeLibrary.TryLoad($"libicuuc.so.{v}", out var uc)) continue;
            if (!NativeLibrary.TryLoad($"libicui18n.so.{v}", out var i18n)) continue;
            s_uc = uc;
            s_i18n = i18n;
            if (NativeLibrary.TryGetExport(uc, "u_getVersion_" + v, out _)) { s_suffix = "_" + v; return true; }
            if (NativeLibrary.TryGetExport(uc, "u_getVersion", out _)) { s_suffix = ""; return true; }
        }
        return false;
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
