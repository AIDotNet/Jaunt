using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Jaunt.Runtime;

internal static unsafe partial class Icu
{
    private static nint s_ubrkOpen, s_ubrkClose, s_ubrkClone, s_ubrkSetText, s_ubrkFollowing, s_ubrkPreceding, s_ubrkRuleStatus;
    private static int s_ubrkCloneMissing;

    public static nint ubrk_close_ptr => P(ref s_ubrkClose, "ubrk_close");
    public static nint ubrk_open(int type, byte* loc, char* text, int len, int* err) =>
        ((delegate* unmanaged<int, byte*, char*, int, int*, nint>)P(ref s_ubrkOpen, "ubrk_open"))(type, loc, text, len, err);

    /// <summary>ubrk_clone (ICU 69+); returns 0 when the function is unavailable.</summary>
    public static nint ubrk_clone(nint bi, int* err)
    {
        if (s_ubrkCloneMissing != 0) return 0;
        if (s_ubrkClone == 0)
        {
            try { s_ubrkClone = Sym("ubrk_clone"); }
            catch (EntryPointNotFoundException) { s_ubrkCloneMissing = 1; return 0; }
        }
        return ((delegate* unmanaged<nint, int*, nint>)s_ubrkClone)(bi, err);
    }

    public static void ubrk_setText(nint bi, char* text, int len, int* err) =>
        ((delegate* unmanaged<nint, char*, int, int*, void>)P(ref s_ubrkSetText, "ubrk_setText"))(bi, text, len, err);
    public static int ubrk_following(nint bi, int offset) =>
        ((delegate* unmanaged<nint, int, int>)P(ref s_ubrkFollowing, "ubrk_following"))(bi, offset);
    public static int ubrk_preceding(nint bi, int offset) =>
        ((delegate* unmanaged<nint, int, int>)P(ref s_ubrkPreceding, "ubrk_preceding"))(bi, offset);
    public static int ubrk_getRuleStatus(nint bi) =>
        ((delegate* unmanaged<nint, int>)P(ref s_ubrkRuleStatus, "ubrk_getRuleStatus"))(bi);
}

/// <summary>An Intl.Segmenter instance ([[InitializedSegmenter]]).</summary>
internal sealed class JsSegmenter : JsObject
{
    public string Locale = "";
    public string DataLocale = "";
    public string Granularity = "grapheme";
    public JsSegmenter(Realm realm, JsObject proto) : base(realm, proto) { }
}

/// <summary>
/// A break iterator over a native copy of a string (ICU keeps a pointer to the text, so the copy lives as long as the
/// iterator).
/// </summary>
internal sealed unsafe class SegmentText
{
    public readonly string Text;
    public readonly bool Word;
    private readonly IcuTextHandle _handle;

    public SegmentText(IcuTextHandle handle, string text, bool word)
    {
        _handle = handle;
        Text = text;
        Word = word;
    }

    /// <summary>FindBoundary(..., before): the last boundary at or before <paramref name="index"/>.</summary>
    public int Before(int index)
    {
        if (index == 0) return 0;
        int b = Icu.ubrk_preceding(_handle.Ptr, index + 1);
        GC.KeepAlive(_handle);
        return b < 0 ? 0 : b;
    }

    /// <summary>FindBoundary(..., after): the first boundary after <paramref name="index"/>; also reports whether the segment ending there is word-like.</summary>
    public int After(int index, out bool wordLike)
    {
        int b = Icu.ubrk_following(_handle.Ptr, index);
        wordLike = false;
        if (b < 0) b = Text.Length;
        else if (Word)
        {
            // UBRK_WORD_NONE is [0, 100): spaces and punctuation; numbers, letters, kana and ideographs are word-like
            int status = Icu.ubrk_getRuleStatus(_handle.Ptr);
            wordLike = status >= 100;
        }
        GC.KeepAlive(_handle);
        return b;
    }
}

/// <summary>A %Segments% instance.</summary>
internal sealed class JsSegments : JsObject
{
    public readonly JsSegmenter Segmenter;
    public readonly SegmentText Text;
    public JsSegments(Realm realm, JsObject proto, JsSegmenter segmenter, SegmentText text) : base(realm, proto) { Segmenter = segmenter; Text = text; }
}

/// <summary>A %SegmentIterator% instance.</summary>
internal sealed class JsSegmentIterator : JsObject
{
    public readonly JsSegmenter Segmenter;
    public readonly SegmentText Text;
    public int Position;
    public JsSegmentIterator(Realm realm, JsObject proto, JsSegmenter segmenter, SegmentText text) : base(realm, proto) { Segmenter = segmenter; Text = text; }
}

/// <summary>Intl.Segmenter (ECMA-402 §18) on ICU's ubrk API.</summary>
internal static class IntlSegmenter
{
    private static readonly string[] s_granularities = { "grapheme", "word", "sentence" };
    private static readonly Dictionary<string, IcuTextHandle> s_templates = new(StringComparer.Ordinal);

    private static unsafe IcuTextHandle? Template(string dataLocale, int type)
    {
        var key = type + "|" + dataLocale;
        lock (s_templates)
        {
            if (s_templates.TryGetValue(key, out var t)) return t;
            if (s_templates.Count >= IntlText.CacheLimit) return null;
            var loc = Icu.Utf8Z(IntlCore.ToIcuLocale(dataLocale));
            int err = 0;
            char empty = '\0';
            nint bi;
            fixed (byte* lp = loc) bi = Icu.ubrk_open(type, lp, &empty, 0, &err);
            if (bi == 0 || Icu.Failure(err)) return null;
            t = new IcuTextHandle(bi, Icu.ubrk_close_ptr).Pin();
            s_templates[key] = t;
            return t;
        }
    }

    /// <summary>A break iterator of the segmenter's granularity over a native copy of <paramref name="s"/>.</summary>
    public static unsafe SegmentText CreateText(Realm realm, JsSegmenter seg, string s)
    {
        int type = seg.Granularity switch { "word" => 1, "sentence" => 3, _ => 0 };
        var mem = (char*)NativeMemory.Alloc((nuint)Math.Max(1, s.Length) * 2);
        s.AsSpan().CopyTo(new Span<char>(mem, s.Length));
        nint bi = 0;
        int err = 0;
        var template = Template(seg.DataLocale, type);
        if (template != null)
            lock (template) bi = Icu.ubrk_clone(template.Ptr, &err);
        if (bi == 0 || Icu.Failure(err))
        {
            err = 0;
            var loc = Icu.Utf8Z(IntlCore.ToIcuLocale(seg.DataLocale));
            fixed (byte* lp = loc) bi = Icu.ubrk_open(type, lp, mem, s.Length, &err);
        }
        else Icu.ubrk_setText(bi, mem, s.Length, &err);
        if (bi == 0 || Icu.Failure(err))
        {
            NativeMemory.Free(mem);
            throw JsErrors.RangeError(realm, "Unable to create a break iterator");
        }
        return new SegmentText(new IcuTextHandle(bi, Icu.ubrk_close_ptr, (nint)mem), s, type == 1);
    }

    /// <summary>CreateSegmentDataObject (18.7.1).</summary>
    private static JsValue SegmentData(Realm realm, SegmentText text, int start, int end, bool wordLike)
    {
        var o = new JsObject(realm, realm.ObjectPrototype);
        IntlText.Data(o, "segment", new JsValue(text.Text.Substring(start, end - start)));
        IntlText.Data(o, "index", new JsValue(start));
        IntlText.Data(o, "input", new JsValue(text.Text));
        if (text.Word) IntlText.Data(o, "isWordLike", wordLike ? JsValue.True : JsValue.False);
        return JsValue.FromObject(o);
    }

    internal static void Setup(Realm r, JsObject intl, IntlProtos protos)
    {
        var proto = IntlText.Proto(r, "Intl.Segmenter");
        protos.SegmenterPrototype = proto;
        var ctor = r.Constructor("Segmenter", 0, static (in NativeArgs a) =>
        {
            IntlText.RequireNew(a, "Segmenter");
            var realm = a.Realm;
            var proto = IntlText.ProtoFromNewTarget(a, static p => p.SegmenterPrototype);
            var requested = IntlCore.CanonicalizeLocaleList(realm, a[0]);
            var options = IntlCore.GetOptionsObject(realm, a[1]);
            var matcher = IntlCore.GetStringOption(realm, options, "localeMatcher", IntlCore.s_matchers, "best fit");
            var res = IntlCore.ResolveLocale(realm, requested, matcher, new Dictionary<string, string?>(), Array.Empty<string>(), static (_, _) => IntlText.NoData);
            var seg = new JsSegmenter(realm, proto) { Locale = res["locale"]!, DataLocale = res["dataLocale"]! };
            seg.Granularity = IntlCore.GetStringOption(realm, options, "granularity", s_granularities, "grapheme")!;
            return JsValue.FromObject(seg);
        }, proto);
        intl.DefineHidden("Segmenter", JsValue.FromObject(ctor));
        r.Method(ctor, "supportedLocalesOf", 1, static (in NativeArgs a) => IntlCore.SupportedLocalesOf(a.Realm, a[0], a[1]));
        r.Method(proto, "resolvedOptions", 0, static (in NativeArgs a) =>
        {
            var s = IntlCore.This<JsSegmenter>(a, "Intl.Segmenter.prototype.resolvedOptions");
            var o = new JsObject(a.Realm, a.Realm.ObjectPrototype);
            IntlText.Data(o, "locale", new JsValue(s.Locale));
            IntlText.Data(o, "granularity", new JsValue(s.Granularity));
            return JsValue.FromObject(o);
        });
        r.Method(proto, "segment", 1, static (in NativeArgs a) =>
        {
            var seg = IntlCore.This<JsSegmenter>(a, "Intl.Segmenter.prototype.segment");
            var s = Operations.ToString(a.Realm, a[0]);
            return JsValue.FromObject(new JsSegments(a.Realm, a.Realm.Intl!.SegmentsPrototype, seg, CreateText(a.Realm, seg, s)));
        });

        var segmentsProto = new JsObject(r, r.ObjectPrototype);
        protos.SegmentsPrototype = segmentsProto;
        r.Method(segmentsProto, "containing", 1, static (in NativeArgs a) =>
        {
            var segs = IntlCore.This<JsSegments>(a, "%Segments.prototype%.containing");
            double n = Operations.ToIntegerOrInfinity(a.Realm, a[0]);
            var text = segs.Text;
            if (n < 0 || n >= text.Text.Length) return JsValue.Undefined;
            int i = (int)n;
            int start = text.Before(i);
            int end = text.After(i, out bool wordLike);
            return SegmentData(a.Realm, text, start, end, wordLike);
        });
        r.Method(segmentsProto, JsSymbol.Iterator, "[Symbol.iterator]", 0, static (in NativeArgs a) =>
        {
            var segs = IntlCore.This<JsSegments>(a, "%Segments.prototype%[@@iterator]");
            return JsValue.FromObject(new JsSegmentIterator(a.Realm, a.Realm.Intl!.SegmentIteratorPrototype, segs.Segmenter, segs.Text));
        });

        var iterProto = new JsObject(r, r.IteratorPrototype);
        protos.SegmentIteratorPrototype = iterProto;
        r.Method(iterProto, "next", 0, static (in NativeArgs a) =>
        {
            var it = IntlCore.This<JsSegmentIterator>(a, "%SegmentIterator.prototype%.next");
            var text = it.Text;
            int start = it.Position;
            if (start >= text.Text.Length) return Coroutines.CreateIterResult(a.Realm, JsValue.Undefined, true);
            int end = text.After(start, out bool wordLike);
            it.Position = end;
            return Coroutines.CreateIterResult(a.Realm, SegmentData(a.Realm, text, start, end, wordLike), false);
        });
        iterProto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("Segmenter String Iterator"), PropFlags.Configurable);
    }
}

internal sealed partial class IntlProtos
{
    public JsObject SegmenterPrototype = null!;
    public JsObject SegmentsPrototype = null!;
    public JsObject SegmentIteratorPrototype = null!;
}

internal static partial class IntlBuiltins
{
    static partial void SetupSegmenter(Realm r, JsObject intl, IntlProtos protos) => IntlSegmenter.Setup(r, intl, protos);
}
