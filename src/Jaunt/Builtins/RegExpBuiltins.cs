using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Acornima;
using Jaunt.Runtime.Regex;
using SysRegex = System.Text.RegularExpressions.Regex;

namespace Jaunt.Runtime;

internal sealed class JsRegExp : JsObject
{
    /// <summary>.NET backend (patterns Acornima can adapt exactly); null when <see cref="Native"/> is used.</summary>
    public SysRegex? Regex;
    /// <summary>Native ECMAScript backend for patterns .NET cannot express (property escapes, v sets, modifiers...).</summary>
    public JsRegex? Native;
    /// <summary>
    /// Non-Unicode ignore-case patterns: .NET case equivalence differs from ECMAScript Canonicalize for some
    /// non-ASCII characters (e.g. U+212A KELVIN SIGN ~ k), so subjects containing non-ASCII text use this engine.
    /// </summary>
    public JsRegex? NativeIgnoreCase;
    /// <summary>Group indices per name (duplicate named groups map to several indices).</summary>
    public int[][] NameGroups = Array.Empty<int[]>();
    /// <summary>.NET backend: highest group number (named groups are numbered after the numbered ones).</summary>
    public int DotnetMaxGroup;
    /// <summary>
    /// .NET backend: the same pattern on the native engine, used when a .NET match exceeds its time slice (the native
    /// engine charges the execution budget, so Timeout / cancellation / Interrupt apply). <see cref="SliceFallbackGroups"/>
    /// gives, per native group, the extra .NET number of a named group (0 for unnamed). Null when the native engine
    /// cannot compile the pattern.
    /// </summary>
    public JsRegex? SliceFallback;
    public int[]? SliceFallbackGroups;
    public string Source = "";
    private string _flags = "";
    private bool _global, _sticky, _unicode, _hasIndices;
    /// <summary>The flags string; the individual flags are cached because every exec reads them.</summary>
    public string Flags
    {
        get => _flags;
        set
        {
            _flags = value;
            _global = value.Contains('g');
            _sticky = value.Contains('y');
            _unicode = value.Contains('u') || value.Contains('v');
            _hasIndices = value.Contains('d');
        }
    }
    /// <summary>lastIndex is the first (and, being non-configurable, permanent) own property: its value lives in slot 0.</summary>
    internal bool LastIndexInSlot0;
    public int GroupCount;
    public string[] GroupNames = Array.Empty<string>();
    /// <summary>[[LegacyFeaturesEnabled]]: false for instances allocated with a new.target other than %RegExp%.</summary>
    public bool LegacyFeatures = true;

    public JsRegExp(Realm realm, JsObject proto) : base(realm, proto) { }
    public override string ClassName => "RegExp";

    public bool Global => _global;
    public bool Sticky => _sticky;
    public bool Unicode => _unicode;
    public bool HasIndices => _hasIndices;

    /// <summary>[[Get]] of lastIndex without a property lookup in the common case (an ordinary object with a data slot).</summary>
    internal JsValue GetLastIndex() => LastIndexInSlot0 && Dict == null ? Slots[0] : Get("lastIndex");
}

internal static class RegExpBuiltins
{
    private sealed class CacheEntry { public SysRegex? Regex; public JsRegex? Native; public JsRegex? NativeIgnoreCase; public int Groups; public string[] Names = Array.Empty<string>(); public int[][] NameGroups = Array.Empty<int[]>(); public int DotnetMaxGroup; public JsRegex? SliceFallback; public int[]? SliceFallbackGroups; public long Bytes; }
    private static readonly Dictionary<(string, string), CacheEntry> s_cache = new();
    private const int CacheLimit = 512;
    private const int MaxCachedPatternLength = 16 * 1024;
    private const long MaxCachedProgramBytes = 1L << 20, MaxCacheBytes = 32L << 20;
    private static long s_cacheBytes;   // guarded by s_cache
    /// <summary>
    /// Time slice of a .NET-backend match. .NET matching cannot observe the execution budget, so a match that runs longer
    /// is redone on the native engine, which does (see <see cref="JsRegExp.SliceFallback"/>).
    /// </summary>
    public static TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    /// <summary>Timeout of the rare .NET-only patterns the native engine cannot compile (no fallback exists).</summary>
    private static readonly TimeSpan s_longMatchTimeout = TimeSpan.FromSeconds(5);

    public static void Setup(Realm r)
    {
        var proto = new JsObject(r, r.ObjectPrototype);
        r.RegExpPrototype = proto;
        var ctor = r.Constructor("RegExp", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var pattern = a[0];
            var flags = a[1];
            bool patternIsRegExp = IsRegExpObject(pattern);
            if (!a.IsConstruct && patternIsRegExp && flags.IsUndefined)
            {
                var pc = pattern.AsObjectUnchecked.Get("constructor");
                if (ReferenceEquals(pc.Ref, a.Callee)) return pattern;
            }
            string p, f;
            if (pattern.Ref is JsRegExp re)
            {
                p = re.Source;
                f = flags.IsUndefined ? re.Flags : Operations.ToString(realm, flags);
            }
            else if (patternIsRegExp)
            {
                p = Operations.ToString(realm, pattern.AsObjectUnchecked.Get("source"));
                f = flags.IsUndefined ? Operations.ToString(realm, pattern.AsObjectUnchecked.Get("flags")) : Operations.ToString(realm, flags);
            }
            else
            {
                p = pattern.IsUndefined ? "" : Operations.ToString(realm, pattern);
                f = flags.IsUndefined ? "" : Operations.ToString(realm, flags);
            }
            var nt = a.IsConstruct ? a.NewTarget.AsObjectUnchecked : a.Callee;
            var proto2 = Operations.GetPrototypeFromConstructor(realm, nt, realm.RegExpPrototype);
            var created = Create(realm, p, f, proto2);
            if (!ReferenceEquals(nt, realm.RegExpConstructor)) created.LegacyFeatures = false;
            return JsValue.FromObject(created);
        }, proto);
        r.DefineGlobal("RegExp", JsValue.FromObject(ctor));
        r.RegExpConstructor = ctor;
        Es2025Builtins.InstallRegExp(r, ctor);
        r.Getter(ctor, JsSymbol.Species, "[Symbol.species]", static (in NativeArgs a) => a.This);
        InstallLegacyStatics(r, ctor);

        r.Method(proto, "exec", 1, static (in NativeArgs a) =>
        {
            var re = ThisRegExp(a, "exec");
            return Exec(a.Realm, re, Operations.ToString(a.Realm, a[0]));
        });
        r.Method(proto, "test", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var s = Operations.ToString(realm, a[0]);
            if (a.This.Ref is JsRegExp re && IsPristineExec(realm, re))
                return JsValue.FromBool(ExecMatch(realm, re, s) != null);
            return JsValue.FromBool(!RegExpExec(realm, a.This, s).IsNull);
        });
        r.Method(proto, "toString", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a.This.IsNotObject) throw JsErrors.TypeError(realm, "RegExp.prototype.toString called on non-object");
            var o = a.This.AsObjectUnchecked;
            return new JsValue("/" + Operations.ToString(realm, o.Get("source")) + "/" + Operations.ToString(realm, o.Get("flags")));
        });
        r.Method(proto, "compile", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a.This.Ref is not JsRegExp re) throw JsErrors.TypeError(realm, "RegExp.prototype.compile called on incompatible receiver");
            if (!ReferenceEquals(re.Realm, realm) || !re.LegacyFeatures)
                throw JsErrors.TypeError(realm, "RegExp.prototype.compile called on a RegExp subclass or cross-realm instance");
            string p, f;
            if (a[0].Ref is JsRegExp src)
            {
                if (!a[1].IsUndefined) throw JsErrors.TypeError(realm, "Cannot supply flags when constructing one RegExp from another");
                p = src.Source; f = src.Flags;
            }
            else
            {
                p = a[0].IsUndefined ? "" : Operations.ToString(realm, a[0]);
                f = a[1].IsUndefined ? "" : Operations.ToString(realm, a[1]);
            }
            var fresh = Create(realm, p, f);
            re.Regex = fresh.Regex; re.Native = fresh.Native; re.NativeIgnoreCase = fresh.NativeIgnoreCase; re.Source = fresh.Source; re.Flags = fresh.Flags; re.GroupCount = fresh.GroupCount; re.GroupNames = fresh.GroupNames; re.NameGroups = fresh.NameGroups; re.DotnetMaxGroup = fresh.DotnetMaxGroup; re.SliceFallback = fresh.SliceFallback; re.SliceFallbackGroups = fresh.SliceFallbackGroups;
            re.SetOrThrow("lastIndex", JsValue.Zero);
            return a.This;
        });
        r.Getter(proto, "source", "source", static (in NativeArgs a) =>
        {
            if (a.This.Ref is JsRegExp re) return new JsValue(re.Source.Length == 0 ? "(?:)" : a.Realm.Charged(EscapeSource(re.Source)));
            if (ReferenceEquals(a.This.Ref, a.Realm.RegExpPrototype)) return new JsValue("(?:)");
            throw JsErrors.TypeError(a.Realm, "RegExp.prototype.source getter called on non-RegExp");
        });
        r.Getter(proto, "flags", "flags", static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a.This.IsNotObject) throw JsErrors.TypeError(realm, "RegExp.prototype.flags getter called on non-object");
            var o = a.This.AsObjectUnchecked;
            var sb = new StringBuilder();
            foreach (var (ch, name) in new[] { ('d', "hasIndices"), ('g', "global"), ('i', "ignoreCase"), ('m', "multiline"), ('s', "dotAll"), ('u', "unicode"), ('v', "unicodeSets"), ('y', "sticky") })
                if (Operations.ToBoolean(o.Get(name))) sb.Append(ch);
            return new JsValue(sb.ToString());
        });
        void FlagGetter(string name, char ch)
        {
            var g = r.Function("get " + name, 0, static (in NativeArgs a) =>
            {
                char c = (char)a.Callee.State!;
                if (a.This.Ref is JsRegExp re) return JsValue.FromBool(re.Flags.IndexOf(c) >= 0);
                if (ReferenceEquals(a.This.Ref, a.Realm.RegExpPrototype)) return JsValue.Undefined;
                throw JsErrors.TypeError(a.Realm, "RegExp flag getter called on non-RegExp");
            });
            g.State = ch;
            proto.DefineAccessor(name, g, null, PropFlags.Configurable);
        }
        FlagGetter("global", 'g'); FlagGetter("ignoreCase", 'i'); FlagGetter("multiline", 'm'); FlagGetter("dotAll", 's');
        FlagGetter("unicode", 'u'); FlagGetter("unicodeSets", 'v'); FlagGetter("sticky", 'y'); FlagGetter("hasIndices", 'd');

        r.Method(proto, JsSymbol.Match, "[Symbol.match]", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a.This.IsNotObject) throw JsErrors.TypeError(realm, "RegExp.prototype[@@match] called on non-object");
            var rx = a.This.AsObjectUnchecked;
            var s = Operations.ToString(realm, a[0]);
            var flags = Operations.ToString(realm, rx.Get("flags"));
            if (!flags.Contains('g')) return RegExpExec(realm, a.This, s);
            bool fullUnicode = flags.Contains('u') || flags.Contains('v');
            rx.SetOrThrow("lastIndex", JsValue.Zero);
            var res = realm.NewArray();
            while (true)
            {
                var r2 = RegExpExec(realm, a.This, s);
                if (r2.IsNull) return res.Length == 0 ? JsValue.Null : JsValue.FromObject(res);
                var matchStr = Operations.ToString(realm, r2.AsObjectUnchecked.GetIndex(0, r2));
                res.Push(new JsValue(matchStr));
                if (matchStr.Length == 0)
                {
                    long li = Operations.ToLength(realm, rx.Get("lastIndex"));
                    rx.SetOrThrow("lastIndex", new JsValue(AdvanceIndex(s, li, fullUnicode)));
                }
                realm.Vm.Tick();
            }
        });
        r.Method(proto, JsSymbol.MatchAll, "[Symbol.matchAll]", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a.This.IsNotObject) throw JsErrors.TypeError(realm, "RegExp.prototype[@@matchAll] called on non-object");
            var s = Operations.ToString(realm, a[0]);
            var rx = a.This.AsObjectUnchecked;
            var c = Operations.SpeciesConstructorDefault(realm, rx, (JsObject)realm.GlobalObject.Get("RegExp").Ref!);
            var flags = Operations.ToString(realm, rx.Get("flags"));
            var matcher = realm.Vm.Construct(JsValue.FromObject(c), new[] { a.This, new JsValue(flags) }).AsObjectUnchecked;
            long li = Operations.ToLength(realm, rx.Get("lastIndex"));
            matcher.SetOrThrow("lastIndex", new JsValue(li));
            var it = new JsRegExpStringIterator(realm, matcher, s, flags.Contains('g'), flags.Contains('u') || flags.Contains('v'));
            return JsValue.FromObject(it);
        });
        r.Method(proto, JsSymbol.Search, "[Symbol.search]", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a.This.IsNotObject) throw JsErrors.TypeError(realm, "RegExp.prototype[@@search] called on non-object");
            var rx = a.This.AsObjectUnchecked;
            var s = Operations.ToString(realm, a[0]);
            var prev = rx.Get("lastIndex");
            if (!Operations.SameValue(prev, JsValue.Zero)) rx.SetOrThrow("lastIndex", JsValue.Zero);
            var result = RegExpExec(realm, a.This, s);
            var cur = rx.Get("lastIndex");
            if (!Operations.SameValue(cur, prev)) rx.SetOrThrow("lastIndex", prev);
            return result.IsNull ? new JsValue(-1) : result.AsObjectUnchecked.Get("index");
        });
        r.Method(proto, JsSymbol.Split, "[Symbol.split]", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a.This.IsNotObject) throw JsErrors.TypeError(realm, "RegExp.prototype[@@split] called on a non-object");
            var rxo = a.This.AsObjectUnchecked;
            var s = Operations.ToString(realm, a[0]);
            var c = Operations.SpeciesConstructorDefault(realm, rxo, realm.RegExpConstructor!);
            var flagsStr = Operations.ToString(realm, rxo.Get("flags", a.This));
            // the fast path reuses `this` as the splitter: only valid when constructing the splitter (IsRegExp reads
            // @@match) and coercing the limit cannot run user code that recompiles it
            bool fast = ReferenceEquals(c, realm.RegExpConstructor) && a.This.Ref is JsRegExp jr && IsPristineExec(realm, jr) && flagsStr == jr.Flags
                && !a[1].IsObject && !jr.HasOwnProperty(new PropertyKey(JsSymbol.Match));
            if (!fast) return SplitGeneric(realm, c, a.This, s, flagsStr, a[1]);
            var re = (JsRegExp)a.This.Ref!;
            uint lim = a[1].IsUndefined ? uint.MaxValue : Operations.ToUint32(realm, a[1]);
            var res = realm.NewArray();
            if (lim == 0) return JsValue.FromObject(res);
            if (s.Length == 0)
            {
                var m0 = SafeMatch(realm, re, s, 0);
                if (!m0.Success) res.Push(new JsValue(s));
                else RecordLegacy(realm, m0, re.GroupCount);   // the splitter is a %RegExp% instance of this realm
                return JsValue.FromObject(res);
            }
            int p = 0;
            int q = 0;
            bool unicode = re.Unicode;
            while (q < s.Length)
            {
                var m = SafeMatch(realm, re, s, q);
                if (!m.Success || m.Index >= s.Length) break;
                RecordLegacy(realm, m, re.GroupCount);
                int e = m.Index + m.Length;
                if (e == p) { q = (int)AdvanceIndex(s, m.Index, unicode); continue; }
                res.Push(new JsValue(s.Substring(p, m.Index - p)));
                if (res.Length == lim) return JsValue.FromObject(res);
                for (int g = 1; g <= re.GroupCount; g++)
                {
                    res.Push(m.Group(g));
                    if (res.Length == lim) return JsValue.FromObject(res);
                }
                p = e;
                q = p;
                realm.Vm.Tick();
            }
            res.Push(new JsValue(s.Substring(p)));
            return JsValue.FromObject(res);
        });
        r.Method(proto, JsSymbol.Replace, "[Symbol.replace]", 2, static (in NativeArgs a) => SymbolReplace(a));

        SnapshotPristine(r);

        var iterProto = new JsObject(r, r.IteratorPrototype);
        r.RegExpStringIteratorPrototype = iterProto;
        r.Method(iterProto, "next", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a.This.Ref is not JsRegExpStringIterator it) throw JsErrors.TypeError(realm, "next called on incompatible receiver");
            if (it.Done) return Coroutines.CreateIterResult(realm, JsValue.Undefined, true);
            var m = RegExpExec(realm, JsValue.FromObject(it.Matcher), it.Input);
            if (m.IsNull) { it.Done = true; return Coroutines.CreateIterResult(realm, JsValue.Undefined, true); }
            if (!it.Global) { it.Done = true; return Coroutines.CreateIterResult(realm, m, false); }
            var matchStr = Operations.ToString(realm, m.AsObjectUnchecked.GetIndex(0, m));
            if (matchStr.Length == 0)
            {
                long li = Operations.ToLength(realm, it.Matcher.Get("lastIndex"));
                it.Matcher.SetOrThrow("lastIndex", new JsValue(AdvanceIndex(it.Input, li, it.Unicode)));
            }
            return Coroutines.CreateIterResult(realm, m, false);
        });
        iterProto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("RegExp String Iterator"), PropFlags.Configurable);
    }

    private static string EscapeSource(string s) => s.Replace("/", "\\/").Replace("\n", "\\n");

    public static bool IsRegExpObject(in JsValue v)
    {
        if (v.IsNotObject) return false;
        var m = v.AsObjectUnchecked.Get(JsSymbol.Match);
        if (!m.IsUndefined) return Operations.ToBoolean(m);
        return v.Ref is JsRegExp;
    }

    private static JsRegExp ThisRegExp(in NativeArgs a, string name)
    {
        if (a.This.Ref is JsRegExp re) return re;
        throw JsErrors.TypeError(a.Realm, $"RegExp.prototype.{name} called on incompatible receiver {a.This}");
    }

    public static JsRegExp Create(Realm realm, string pattern, string flags, JsObject? proto = null)
    {
        foreach (char c in flags)
        {
            if ("dgimsuvy".IndexOf(c) < 0 || flags.IndexOf(c) != flags.LastIndexOf(c))
                throw JsErrors.SyntaxError(realm, $"Invalid regular expression flags '{flags}'");
        }
        CacheEntry? entry;
        lock (s_cache)
        {
            s_cache.TryGetValue((pattern, flags), out entry);
        }
        if (entry == null)
        {
            RegExpParseResult result;
            try
            {
                // huge patterns go to the native engine only: it validates the syntax itself, and its compilation is
                // charged and interruptible, whereas the .NET adaptation/compile of a multi-megabyte pattern is neither
                result = pattern.Length > MaxCachedPatternLength ? default
                    : Tokenizer.AdaptRegExp(pattern, flags, false, MatchTimeout, false, EcmaVersion.Latest, ExperimentalESFeatures.All);
            }
            catch (ParseErrorException pe)
            {
                throw JsErrors.SyntaxError(realm, $"Invalid regular expression: /{pattern}/{flags}: {pe.Description}");
            }
            // property escapes go to the native engine: its Unicode tables match the ECMAScript version (17.0)
            // the native engine handles what the .NET adaptation would get subtly wrong: Unicode 17 property escapes,
            // capture resets inside quantified groups, duplicate / escaped group names
            JsRegex? preparsed = null;
            bool forceNative = false;
            if (result.Success && result.Regex != null)
            {
                try
                {
                    preparsed = JsRegex.Compile(pattern, flags);
                    forceNative = preparsed.RequiresNative;
                    // ignore-case without u/v: non-ASCII pattern text or named groups always use the native engine
                    if (flags.Contains('i') && !flags.Contains('u') && !flags.Contains('v')
                        && (pattern.AsSpan().ContainsAnyExceptInRange((char)0, (char)127) || pattern.Contains("(?<", StringComparison.Ordinal)
                            || MayDenoteNonAscii(pattern)))
                        forceNative = true;
                }
                catch (RegexSyntaxException) { preparsed = null; }
            }
            if (result.Success && result.Regex != null && !forceNative)
            {
                var regex = result.Regex;
                var names = new List<string>();
                var nameGroups = new List<int[]>();
                foreach (var n in regex.GetGroupNames())
                {
                    if (int.TryParse(n, out _)) continue;
                    names.Add(n);
                    nameGroups.Add(new[] { regex.GroupNumberFromName(n) });
                }
                // ActualRegexGroupCount counts group 0 (the whole match); JS capture count excludes it
                int maxNum = 0;
                foreach (var gn in regex.GetGroupNumbers()) if (gn > maxNum) maxNum = gn;
                entry = new CacheEntry { Regex = regex, Groups = Math.Max(0, result.ActualRegexGroupCount - 1), Names = names.ToArray(), NameGroups = nameGroups.ToArray(), DotnetMaxGroup = maxNum };
                if (preparsed != null && preparsed.CaptureCount - 1 == entry.Groups)
                {
                    // Acornima's adaptation wraps every named group in an unnamed one, so .NET group g is JS group g
                    // (source order, like the native engine); the named group itself gets a further .NET number
                    var map = new int[preparsed.CaptureCount];
                    bool ok = true;
                    for (int g = 1; g < map.Length && ok; g++)
                    {
                        var n = preparsed.GroupNames[g];
                        if (n == null) continue;
                        map[g] = regex.GroupNumberFromName(n);
                        ok = map[g] > 0;
                    }
                    if (ok) { entry.SliceFallback = preparsed; entry.SliceFallbackGroups = map; }
                }
                if (entry.SliceFallback == null)
                    entry.Regex = new SysRegex(regex.ToString(), regex.Options, s_longMatchTimeout);
                if (flags.Contains('i') && !flags.Contains('u') && !flags.Contains('v') && preparsed != null && names.Count == 0)
                    entry.NativeIgnoreCase = preparsed;
            }
            else
            {
                // valid pattern that .NET cannot express exactly: native ECMAScript engine
                JsRegex native;
                try { native = preparsed ?? JsRegex.Compile(pattern, flags); }
                catch (RegexSyntaxException rx) { throw JsErrors.SyntaxError(realm, rx.Message); }
                var names = new List<string>();
                var nameGroups = new List<int[]>();
                for (int g = 1; g < native.GroupNames.Length; g++)
                {
                    var n = native.GroupNames[g];
                    if (n == null) continue;
                    int at = names.IndexOf(n);
                    if (at < 0) { names.Add(n); nameGroups.Add(new[] { g }); }
                    else nameGroups[at] = [.. nameGroups[at], g];
                }
                entry = new CacheEntry { Native = native, Groups = native.CaptureCount - 1, Names = names.ToArray(), NameGroups = nameGroups.ToArray() };
            }
            // compiling is an allocation of this execution (charged once, at compile time; cache hits share the program)
            var nativeProgram = entry.Native ?? entry.SliceFallback ?? entry.NativeIgnoreCase;
            long programBytes = nativeProgram?.ApproxBytes ?? 0;
            long dotnetBytes = entry.Regex != null ? 256 + pattern.Length * 32L : 0;   // .NET program: rough estimate
            programBytes += dotnetBytes;
            realm.ChargeBytes((nativeProgram?.CodeBytes ?? 0) + dotnetBytes);   // sets were charged as they were built
            entry.Bytes = programBytes;
            // the cache is process-wide and outlives engines: bounded by entries and by bytes, huge programs not kept
            if (pattern.Length <= MaxCachedPatternLength && programBytes <= MaxCachedProgramBytes)
            lock (s_cache)
            {
                if (s_cache.Count >= CacheLimit || s_cacheBytes + programBytes > MaxCacheBytes) { s_cache.Clear(); s_cacheBytes = 0; }
                if (s_cache.TryGetValue((pattern, flags), out var old)) s_cacheBytes -= old.Bytes;
                s_cache[(pattern, flags)] = entry;
                s_cacheBytes += programBytes;
            }
        }
        var re = new JsRegExp(realm, proto ?? realm.RegExpPrototype)
        {
            Regex = entry.Regex,
            Native = entry.Native,
            NativeIgnoreCase = entry.NativeIgnoreCase,
            NameGroups = entry.NameGroups,
            DotnetMaxGroup = entry.DotnetMaxGroup,
            SliceFallback = entry.SliceFallback,
            SliceFallbackGroups = entry.SliceFallbackGroups,
            Source = pattern,
            Flags = flags,
            GroupCount = entry.Groups,
            GroupNames = entry.Names,
        };
        re.AddOwnProperty("lastIndex", JsValue.Zero, PropFlags.Writable);
        re.LastIndexInSlot0 = true;
        return re;
    }

    /// <summary>Backend-independent match result: [start, end) per group, -1 when the group did not participate.</summary>
    internal sealed class RegexMatch
    {
        public bool Success;
        public int[] Caps = Array.Empty<int>();
        public string Input = "";
        public int Index => Caps[0];
        public int Length => Caps[1] - Caps[0];
        public string Value => Input.Substring(Caps[0], Caps[1] - Caps[0]);
        public bool Has(int g) => g >= 0 && 2 * g < Caps.Length && Caps[2 * g] >= 0;
        public JsValue Group(int g) => Has(g) ? new JsValue(Input.Substring(Caps[2 * g], Caps[2 * g + 1] - Caps[2 * g])) : JsValue.Undefined;
    }

    private static readonly RegexMatch s_noMatch = new();

    /// <summary>Escapes that can denote non-ASCII characters (\u, \x, legacy octal) — ASCII-only text otherwise.</summary>
    private static bool MayDenoteNonAscii(string pattern)
    {
        for (int i = 0; i + 1 < pattern.Length; i++)
        {
            if (pattern[i] != '\\') continue;
            char c = pattern[++i];
            if (c is 'u' or 'x' || (c >= '0' && c <= '9')) return true;
        }
        return false;
    }

    private static RegexMatch SafeMatch(Realm realm, JsRegExp re, string s, int start)
    {
        realm.Vm.Tick(Math.Max(1, (s.Length - start) / 256));
        var native = re.Native;
        if (native == null && re.NativeIgnoreCase != null && s.AsSpan(Math.Min(start, s.Length)).ContainsAnyExceptInRange((char)0, (char)127))
            native = re.NativeIgnoreCase;
        if (native != null)
        {
            var m = new JsRegex.Matcher(native, s, realm.RegexTick ??= () => realm.Vm.Tick(64));
            if (!m.Search(start, sticky: false)) return s_noMatch;
            return new RegexMatch { Success = true, Caps = m.Caps, Input = s };
        }
        System.Text.RegularExpressions.Match dm;
        try { dm = re.Regex!.Match(s, start); }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            realm.Vm.CheckTimeLimits();
            if (re.SliceFallback is { } fb) return FallbackMatch(realm, re, fb, s, start);
            // a pattern only .NET can run (the native engine rejected it, e.g. nesting too deep): with an execution
            // deadline the match may use the time that is left (then exceeding it is that Timeout); without one the 5 s
            // cap is a catchable RangeError, not an engine fault
            if (!realm.Vm.HasDeadline)
                throw JsErrors.RangeError(realm, $"Regular expression evaluation exceeded {s_longMatchTimeout.TotalSeconds:0} s");
            var remaining = realm.Vm.RemainingTime;
            try
            {
                if (remaining <= TimeSpan.Zero) throw new System.Text.RegularExpressions.RegexMatchTimeoutException();
                dm = new SysRegex(re.Regex!.ToString(), re.Regex.Options, remaining).Match(s, start);
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                throw new JsTerminationException(TerminationKind.Timeout, "Regular expression evaluation exceeded its time budget.");
            }
        }
        if (!dm.Success) return s_noMatch;
        int top = Math.Max(re.GroupCount, re.DotnetMaxGroup);
        var caps = new int[(top + 1) * 2];
        caps[0] = dm.Index; caps[1] = dm.Index + dm.Length;   // group 0 is the match itself: no GroupCollection needed
        for (int g = 1; g <= top; g++)
        {
            var grp = dm.Groups[g];
            if (grp.Success) { caps[2 * g] = grp.Index; caps[2 * g + 1] = grp.Index + grp.Length; }
            else { caps[2 * g] = -1; caps[2 * g + 1] = -1; }
        }
        return new RegexMatch { Success = true, Caps = caps, Input = s };
    }

    /// <summary>A .NET-backend match redone on the native engine (budgeted), captures in the .NET group numbering.</summary>
    private static RegexMatch FallbackMatch(Realm realm, JsRegExp re, JsRegex native, string s, int start)
    {
        var m = new JsRegex.Matcher(native, s, () => realm.Vm.Tick(64));
        if (!m.Search(start, sticky: false)) return s_noMatch;
        int top = Math.Max(re.GroupCount, re.DotnetMaxGroup);
        var caps = new int[(top + 1) * 2];
        Array.Fill(caps, -1);
        caps[0] = m.Caps[0];
        caps[1] = m.Caps[1];
        var map = re.SliceFallbackGroups!;
        for (int g = 1; g < map.Length; g++)
        {
            caps[2 * g] = m.Caps[2 * g];
            caps[2 * g + 1] = m.Caps[2 * g + 1];
            if (map[g] > 0)
            {
                caps[2 * map[g]] = m.Caps[2 * g];
                caps[2 * map[g] + 1] = m.Caps[2 * g + 1];
            }
        }
        return new RegexMatch { Success = true, Caps = caps, Input = s };
    }

    /// <summary>RegExp.prototype[@@split] (22.2.6.14), generic species path.</summary>
    private static JsValue SplitGeneric(Realm realm, JsObject c, JsValue rx, string s, string flags, JsValue limit)
    {
        bool unicode = flags.Contains('u') || flags.Contains('v');
        string newFlags = flags.Contains('y') ? flags : flags + "y";
        var splitter = realm.Vm.Construct(JsValue.FromObject(c), new[] { rx, new JsValue(newFlags) });
        var arr = realm.NewArray();
        uint lim = limit.IsUndefined ? uint.MaxValue : Operations.ToUint32(realm, limit);
        if (lim == 0) return JsValue.FromObject(arr);
        var so = splitter.AsObjectUnchecked;
        if (s.Length == 0)
        {
            if (RegExpExec(realm, splitter, s).IsNull) arr.Push(new JsValue(s));
            return JsValue.FromObject(arr);
        }
        int size = s.Length, p = 0, q = 0;
        while (q < size)
        {
            so.SetOrThrow("lastIndex", new JsValue(q));
            var z = RegExpExec(realm, splitter, s);
            if (z.IsNull) { q = (int)AdvanceIndex(s, q, unicode); continue; }
            long e = Math.Min(Operations.ToLength(realm, so.Get("lastIndex", splitter)), size);
            if (e == p) { q = (int)AdvanceIndex(s, q, unicode); continue; }
            arr.Push(new JsValue(s.Substring(p, q - p)));
            if (arr.Length == lim) return JsValue.FromObject(arr);
            p = (int)e;
            var zo = z.AsObjectUnchecked;
            long n = Math.Max(Operations.LengthOfArrayLike(realm, zo) - 1, 0);
            for (long i = 1; i <= n; i++)
            {
                arr.Push(zo.Get(PropertyKey.FromIndex((uint)i), z));
                if (arr.Length == lim) return JsValue.FromObject(arr);
            }
            q = p;
            realm.Vm.Tick();
        }
        arr.Push(new JsValue(s.Substring(p)));
        return JsValue.FromObject(arr);
    }

    private static readonly string[] s_pristineKeys = { "exec", "flags", "global", "ignoreCase", "multiline", "dotAll", "unicode", "unicodeSets", "sticky", "hasIndices" };

    private static void FindPristineSlots(Realm realm)
    {
        var slots = new int[s_pristineKeys.Length];
        for (int i = 0; i < slots.Length; i++) slots[i] = realm.RegExpPrototype.FindOwn(s_pristineKeys[i], out _);
        realm.RegExpPristineSlots = slots;
        realm.RegExpPristineEpoch = realm.ProtoEpoch;
    }

    /// <summary>Remembers the original <c>exec</c>, <c>flags</c> and flag accessors of %RegExp.prototype% (call after setup).</summary>
    private static void SnapshotPristine(Realm realm)
    {
        var proto = realm.RegExpPrototype;
        proto.IsPrototypeObject = true;   // so that adding or deleting a property bumps ProtoEpoch
        FindPristineSlots(realm);
        var refs = new object?[s_pristineKeys.Length];
        for (int i = 0; i < refs.Length; i++) refs[i] = realm.RegExpPristineSlots![i] >= 0 ? proto.ReadSlot(realm.RegExpPristineSlots[i]).Ref : null;
        realm.RegExpPristineRefs = refs;
    }

    /// <summary>
    /// True when exec, flags and every flag accessor are the originals, so a built-in operation can skip the observable
    /// property reads of the generic algorithms. The receiver must be an unmodified instance (nothing but lastIndex of its
    /// own). Slot numbers are re-resolved when a prototype's shape changes (ProtoEpoch); the slot contents are compared on
    /// every call, because assigning to <c>exec</c> or redefining an accessor with the same attributes bumps no epoch.
    /// </summary>
    private static bool IsRegExpPristine(Realm realm, JsRegExp re)
    {
        if (!ReferenceEquals(re.Proto, realm.RegExpPrototype) || re.Dict != null || re.Shape.Count != 1) return false;
        if (realm.RegExpPristineEpoch != realm.ProtoEpoch) FindPristineSlots(realm);
        var slots = realm.RegExpPristineSlots;
        var refs = realm.RegExpPristineRefs;
        if (slots == null || refs == null) return false;
        var proto = realm.RegExpPrototype;
        for (int i = 0; i < slots.Length; i++)
            if (slots[i] < 0 || !ReferenceEquals(proto.ReadSlot(slots[i]).Ref, refs[i])) return false;
        return true;
    }

    private static bool IsPristineExec(Realm realm, JsRegExp re) => IsRegExpPristine(realm, re);

    /// <summary>RegExpExec (22.2.7.1).</summary>
    public static JsValue RegExpExec(Realm realm, JsValue r, string s)
    {
        if (r.IsNotObject) throw JsErrors.TypeError(realm, "RegExp exec method called on a non-object");
        var ro = r.AsObjectUnchecked;
        var exec = ro.Get("exec", r);
        if (exec.IsFunction && !(exec.Ref is JsBuiltin nf && nf.Name == "exec" && r.Ref is JsRegExp))
        {
            var result = realm.Vm.Call(exec, r, new[] { new JsValue(s) });
            if (result.IsNotObject && !result.IsNull) throw JsErrors.TypeError(realm, "exec result must be an object or null");
            return result;
        }
        if (r.Ref is not JsRegExp re) throw JsErrors.TypeError(realm, "RegExp exec called on incompatible receiver");
        return Exec(realm, re, s);
    }

    /// <summary>RegExpBuiltinExec (22.2.7.2).</summary>
    public static JsValue Exec(Realm realm, JsRegExp re, string s)
    {
        var m = ExecMatch(realm, re, s);
        return m == null ? JsValue.Null : BuildExecResult(realm, re, m, s);
    }

    /// <summary>
    /// RegExpBuiltinExec up to and including the lastIndex / legacy-static bookkeeping, without building the result
    /// array: null when there is no match. <c>test</c> and similar callers stop here.
    /// </summary>
    internal static RegexMatch? ExecMatch(Realm realm, JsRegExp re, string s)
    {
        bool global = re.Global, sticky = re.Sticky;
        var liv = re.GetLastIndex();   // always read (observable)
        long lastIndex = liv.IsNumber && liv.Num >= 0 && liv.Num <= int.MaxValue ? (long)liv.Num : Operations.ToLength(realm, liv);
        if (!global && !sticky) lastIndex = 0;
        if (global || sticky)
        {
            if (lastIndex > s.Length)
            {
                re.SetOrThrow("lastIndex", JsValue.Zero);
                return null;
            }
        }
        var m = SafeMatch(realm, re, s, (int)lastIndex);
        if (!m.Success || (sticky && m.Index != lastIndex))
        {
            if (global || sticky) re.SetOrThrow("lastIndex", JsValue.Zero);
            return null;
        }
        if (global || sticky) re.SetOrThrow("lastIndex", new JsValue(m.Index + m.Length));
        // legacy static properties: only the match is recorded here, the values are computed when read
        if (ReferenceEquals(re.Realm, realm))
        {
            if (re.LegacyFeatures) RecordLegacy(realm, m, re.GroupCount);
            else { realm.LegacyInvalid = true; realm.LegacyMatch = null; realm.LegacyInput = null; }
        }
        return m;
    }

    private static JsValue BuildExecResult(Realm realm, JsRegExp re, RegexMatch m, string s)
    {
        var arr = new JsArray(realm, re.GroupCount + 1);
        arr.Push(new JsValue(m.Value));
        for (int g = 1; g <= re.GroupCount; g++) arr.Push(m.Group(g));
        arr.CreateDataProperty("index", new JsValue(m.Index));
        arr.CreateDataProperty("input", new JsValue(s));
        JsValue groups = JsValue.Undefined;
        if (re.GroupNames.Length > 0)
        {
            var go = new JsObject(realm, (JsObject?)null);
            for (int ni = 0; ni < re.GroupNames.Length; ni++)
            {
                JsValue gv = JsValue.Undefined;
                foreach (var g in re.NameGroups[ni]) if (m.Has(g)) { gv = m.Group(g); break; }
                go.CreateDataProperty(re.GroupNames[ni], gv);
            }
            groups = JsValue.FromObject(go);
        }
        arr.CreateDataProperty("groups", groups);
        if (re.HasIndices)
        {
            var indices = realm.NewArray();
            for (int g = 0; g <= re.GroupCount; g++)
            {
                if (!m.Has(g)) { indices.Push(JsValue.Undefined); continue; }
                var pair = realm.NewArray(2);
                pair.Push(new JsValue(m.Caps[2 * g]));
                pair.Push(new JsValue(m.Caps[2 * g + 1]));
                indices.Push(JsValue.FromObject(pair));
            }
            JsValue indexGroups = JsValue.Undefined;
            if (re.GroupNames.Length > 0)
            {
                var ig = new JsObject(realm, (JsObject?)null);
                for (int ni = 0; ni < re.GroupNames.Length; ni++)
                {
                    JsValue pv = JsValue.Undefined;
                    foreach (var g in re.NameGroups[ni])
                    {
                        if (!m.Has(g)) continue;
                        var pair = realm.NewArray(2);
                        pair.Push(new JsValue(m.Caps[2 * g]));
                        pair.Push(new JsValue(m.Caps[2 * g + 1]));
                        pv = JsValue.FromObject(pair);
                        break;
                    }
                    ig.CreateDataProperty(re.GroupNames[ni], pv);
                }
                indexGroups = JsValue.FromObject(ig);
            }
            indices.CreateDataProperty("groups", indexGroups);
            arr.CreateDataProperty("indices", JsValue.FromObject(indices));
        }
        return JsValue.FromObject(arr);
    }

    private static void RecordLegacy(Realm realm, RegexMatch m, int groupCount)
    {
        realm.LegacyMatch = m;
        realm.LegacyGroupCount = groupCount;
        realm.LegacyInput = null;
        realm.LegacyInvalid = false;
    }

    // ------------------------------------------------------------------ legacy static properties (Annex B proposal)

    private const int LegacyInputSlot = 0, LegacyLastMatch = 10, LegacyLastParen = 11, LegacyLeft = 12, LegacyRight = 13;

    private static void InstallLegacyStatics(Realm r, JsObject ctor)
    {
        NativeFn getInput = static (in NativeArgs a) => LegacyGet(a, LegacyInputSlot);
        NativeFn setInput = static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (!ReferenceEquals(a.This.Ref, realm.RegExpConstructor)) throw JsErrors.TypeError(realm, "RegExp legacy static setter called on incompatible receiver");
            realm.LegacyInput = Operations.ToString(realm, a[0]);
            return JsValue.Undefined;
        };
        r.Getter(ctor, "input", "input", getInput, setInput);
        r.Getter(ctor, "$_", "$_", getInput, setInput);
        r.Getter(ctor, "lastMatch", "lastMatch", static (in NativeArgs a) => LegacyGet(a, LegacyLastMatch));
        r.Getter(ctor, "$&", "$&", static (in NativeArgs a) => LegacyGet(a, LegacyLastMatch));
        r.Getter(ctor, "lastParen", "lastParen", static (in NativeArgs a) => LegacyGet(a, LegacyLastParen));
        r.Getter(ctor, "$+", "$+", static (in NativeArgs a) => LegacyGet(a, LegacyLastParen));
        r.Getter(ctor, "leftContext", "leftContext", static (in NativeArgs a) => LegacyGet(a, LegacyLeft));
        r.Getter(ctor, "$`", "$`", static (in NativeArgs a) => LegacyGet(a, LegacyLeft));
        r.Getter(ctor, "rightContext", "rightContext", static (in NativeArgs a) => LegacyGet(a, LegacyRight));
        r.Getter(ctor, "$'", "$'", static (in NativeArgs a) => LegacyGet(a, LegacyRight));
        r.Getter(ctor, "$1", "$1", static (in NativeArgs a) => LegacyGet(a, 1));
        r.Getter(ctor, "$2", "$2", static (in NativeArgs a) => LegacyGet(a, 2));
        r.Getter(ctor, "$3", "$3", static (in NativeArgs a) => LegacyGet(a, 3));
        r.Getter(ctor, "$4", "$4", static (in NativeArgs a) => LegacyGet(a, 4));
        r.Getter(ctor, "$5", "$5", static (in NativeArgs a) => LegacyGet(a, 5));
        r.Getter(ctor, "$6", "$6", static (in NativeArgs a) => LegacyGet(a, 6));
        r.Getter(ctor, "$7", "$7", static (in NativeArgs a) => LegacyGet(a, 7));
        r.Getter(ctor, "$8", "$8", static (in NativeArgs a) => LegacyGet(a, 8));
        r.Getter(ctor, "$9", "$9", static (in NativeArgs a) => LegacyGet(a, 9));
    }

    /// <summary>GetLegacyRegExpStaticProperty: the receiver must be this realm's %RegExp%; invalidated values throw.</summary>
    private static JsValue LegacyGet(in NativeArgs a, int slot)
    {
        var realm = a.Realm;
        if (!ReferenceEquals(a.This.Ref, realm.RegExpConstructor)) throw JsErrors.TypeError(realm, "RegExp legacy static getter called on incompatible receiver");
        if (slot == LegacyInputSlot && realm.LegacyInput != null) return new JsValue(realm.LegacyInput);
        if (realm.LegacyInvalid) throw JsErrors.TypeError(realm, "RegExp legacy static properties are unavailable after a match by a RegExp subclass");
        var m = realm.LegacyMatch;
        if (m == null) return JsValue.EmptyString;
        string s = m.Input;
        JsValue Cap(int g) => m.Group(g) is { IsString: true } v ? v : JsValue.EmptyString;
        return slot switch
        {
            LegacyInputSlot => new JsValue(s),
            LegacyLastMatch => new JsValue(m.Value),
            LegacyLastParen => realm.LegacyGroupCount > 0 ? Cap(realm.LegacyGroupCount) : JsValue.EmptyString,
            LegacyLeft => new JsValue(s.Substring(0, m.Index)),
            LegacyRight => new JsValue(s.Substring(m.Index + m.Length)),
            _ => slot <= realm.LegacyGroupCount ? Cap(slot) : JsValue.EmptyString,
        };
    }

    public static long AdvanceIndex(string s, long index, bool unicode)
    {
        if (!unicode || index + 1 >= s.Length) return index + 1;
        return char.IsHighSurrogate(s[(int)index]) && char.IsLowSurrogate(s[(int)index + 1]) ? index + 2 : index + 1;
    }

    private static JsValue SymbolReplace(in NativeArgs a)
    {
        var realm = a.Realm;
        if (a.This.IsNotObject) throw JsErrors.TypeError(realm, "RegExp.prototype[@@replace] called on non-object");
        var rx = a.This.AsObjectUnchecked;
        var s = Operations.ToString(realm, a[0]);
        var replaceValue = a[1];
        bool functional = replaceValue.IsFunction;
        string replaceStr = functional ? "" : Operations.ToString(realm, replaceValue);
        if (rx is JsRegExp pre && IsRegExpPristine(realm, pre)) return FastReplace(realm, pre, s, replaceValue, functional, replaceStr);
        var flags = Operations.ToString(realm, rx.Get("flags"));
        bool global = flags.Contains('g');
        bool fullUnicode = flags.Contains('u') || flags.Contains('v');
        if (global) rx.SetOrThrow("lastIndex", JsValue.Zero);
        var results = new List<JsObject>();
        while (true)
        {
            var result = RegExpExec(realm, a.This, s);
            if (result.IsNull) break;
            results.Add(result.AsObjectUnchecked);
            if (!global) break;
            var matchStr = Operations.ToString(realm, result.AsObjectUnchecked.GetIndex(0, result));
            if (matchStr.Length == 0)
            {
                long li = Operations.ToLength(realm, rx.Get("lastIndex"));
                rx.SetOrThrow("lastIndex", new JsValue(AdvanceIndex(s, li, fullUnicode)));
            }
            realm.Vm.Tick();
        }
        var sb = new StringBuilder();
        int charged = 0;
        int nextSourcePosition = 0;
        foreach (var result in results)
        {
            var rv = JsValue.FromObject(result);
            long nCaptures = Math.Max(Operations.LengthOfArrayLike(realm, result) - 1, 0);
            var matched = Operations.ToString(realm, result.GetIndex(0, rv));
            int position = (int)Math.Max(Math.Min(Operations.ToIntegerOrInfinity(realm, result.Get("index")), s.Length), 0);
            var captures = new JsValue[nCaptures];
            for (int n = 1; n <= nCaptures; n++)
            {
                var cap = result.GetIndex((uint)n, rv);
                captures[n - 1] = cap.IsUndefined ? cap : new JsValue(Operations.ToString(realm, cap));
            }
            var namedCaptures = result.Get("groups");
            string replacement;
            if (functional)
            {
                var args = new List<JsValue> { new JsValue(matched) };
                args.AddRange(captures);
                args.Add(new JsValue(position));
                args.Add(new JsValue(s));
                if (!namedCaptures.IsUndefined) args.Add(namedCaptures);
                replacement = Operations.ToString(realm, realm.Vm.Call(replaceValue, JsValue.Undefined, args.ToArray()));
            }
            else
            {
                if (!namedCaptures.IsUndefined) namedCaptures = JsValue.FromObject(Operations.ToObject(realm, namedCaptures));
                replacement = GetSubstitution(matched, s, position, captures, namedCaptures, replaceStr, realm);
            }
            if (position >= nextSourcePosition)
            {
                sb.Append(s, nextSourcePosition, position - nextSourcePosition).Append(replacement);
                nextSourcePosition = position + matched.Length;
            }
            if (sb.Length > realm.MaxStringLength) throw JsErrors.RangeError(realm, "Invalid string length");
            // the result is charged as it grows (tracked bytes + work), not after it is complete
            if (sb.Length - charged >= 4096) { realm.ChargeOutput(sb.Length - charged); charged = sb.Length; }
        }
        if (nextSourcePosition < s.Length) sb.Append(s, nextSourcePosition, s.Length - nextSourcePosition);
        realm.ChargeOutput(sb.Length - charged);
        return new JsValue(sb.ToString());
    }

    /// <summary>GetSubstitution (22.1.3.19.1).</summary>
    /// <summary>
    /// RegExp.prototype[@@replace] for an unmodified regexp: the same matches, lastIndex updates and callback calls as the
    /// generic algorithm, without the flags accessors or the intermediate result arrays.
    /// </summary>
    private static JsValue FastReplace(Realm realm, JsRegExp re, string s, JsValue replaceValue, bool functional, string replaceStr)
    {
        bool global = re.Global, fullUnicode = re.Unicode;
        if (global) re.SetOrThrow("lastIndex", JsValue.Zero);
        var matches = new List<RegexMatch>();
        while (true)
        {
            var m = ExecMatch(realm, re, s);
            if (m == null) break;
            matches.Add(m);
            if (!global) break;
            if (m.Length == 0)
            {
                long li = Operations.ToLength(realm, re.GetLastIndex());
                re.SetOrThrow("lastIndex", new JsValue(AdvanceIndex(s, li, fullUnicode)));
            }
            realm.Vm.Tick();
        }
        if (matches.Count == 0) return new JsValue(s);
        bool plain = !functional && replaceStr.IndexOf('$') < 0;
        var sb = new StringBuilder();
        int charged = 0;
        int nextSourcePosition = 0;
        foreach (var m in matches)
        {
            int position = m.Index;
            var matched = m.Value;
            string replacement;
            if (plain) replacement = replaceStr;
            else
            {
                var captures = new JsValue[re.GroupCount];
                for (int g = 1; g <= re.GroupCount; g++) captures[g - 1] = m.Group(g);
                JsValue namedCaptures = JsValue.Undefined;
                if (re.GroupNames.Length > 0)
                {
                    var go = new JsObject(realm, (JsObject?)null);
                    for (int ni = 0; ni < re.GroupNames.Length; ni++)
                    {
                        JsValue gv = JsValue.Undefined;
                        foreach (var g in re.NameGroups[ni]) if (m.Has(g)) { gv = m.Group(g); break; }
                        go.CreateDataProperty(re.GroupNames[ni], gv);
                    }
                    namedCaptures = JsValue.FromObject(go);
                }
                if (functional)
                {
                    var args = new JsValue[captures.Length + 3 + (namedCaptures.IsUndefined ? 0 : 1)];
                    args[0] = new JsValue(matched);
                    Array.Copy(captures, 0, args, 1, captures.Length);
                    args[captures.Length + 1] = new JsValue(position);
                    args[captures.Length + 2] = new JsValue(s);
                    if (!namedCaptures.IsUndefined) args[captures.Length + 3] = namedCaptures;
                    replacement = Operations.ToString(realm, realm.Vm.Call(replaceValue, JsValue.Undefined, args));
                }
                else replacement = GetSubstitution(matched, s, position, captures, namedCaptures, replaceStr, realm);
            }
            if (position >= nextSourcePosition)
            {
                sb.Append(s, nextSourcePosition, position - nextSourcePosition).Append(replacement);
                nextSourcePosition = position + matched.Length;
            }
            if (sb.Length > realm.MaxStringLength) throw JsErrors.RangeError(realm, "Invalid string length");
            // the result is charged as it grows (tracked bytes + work), like the general path
            if (sb.Length - charged >= 4096) { realm.ChargeOutput(sb.Length - charged); charged = sb.Length; }
        }
        if (nextSourcePosition < s.Length) sb.Append(s, nextSourcePosition, s.Length - nextSourcePosition);
        realm.ChargeOutput(sb.Length - charged);
        return new JsValue(sb.ToString());
    }

    public static string GetSubstitution(string matched, string str, int position, JsValue[] captures, JsValue namedCaptures, string replacement, Realm realm)
    {
        if (replacement.IndexOf('$') < 0) return replacement;
        var sb = new StringBuilder();
        int m = captures.Length;
        int tailPos = Math.Min(position + matched.Length, str.Length);
        for (int i = 0; i < replacement.Length; i++)
        {
            char c = replacement[i];
            if (c != '$' || i + 1 >= replacement.Length) { sb.Append(c); continue; }
            char n = replacement[i + 1];
            switch (n)
            {
                case '$': sb.Append('$'); i++; break;
                case '&': sb.Append(matched); i++; break;
                case '`': sb.Append(str, 0, position); i++; break;
                case '\'': if (tailPos < str.Length) sb.Append(str, tailPos, str.Length - tailPos); i++; break;
                case '<':
                {
                    if (namedCaptures.IsUndefined) { sb.Append('$'); break; }
                    int close = replacement.IndexOf('>', i + 2);
                    if (close < 0) { sb.Append('$'); break; }
                    string groupName = replacement.Substring(i + 2, close - i - 2);
                    var cap = namedCaptures.AsObjectUnchecked.Get(groupName);
                    if (!cap.IsUndefined) sb.Append(Operations.ToString(realm, cap));
                    i = close;
                    break;
                }
                default:
                    if (char.IsAsciiDigit(n))
                    {
                        int d1 = n - '0';
                        if (i + 2 < replacement.Length && char.IsAsciiDigit(replacement[i + 2]))
                        {
                            int d2 = d1 * 10 + (replacement[i + 2] - '0');
                            if (d2 >= 1 && d2 <= m)
                            {
                                var cap = captures[d2 - 1];
                                if (!cap.IsUndefined) sb.Append(cap.AsStringUnchecked);
                                i += 2;
                                break;
                            }
                        }
                        if (d1 >= 1 && d1 <= m)
                        {
                            var cap = captures[d1 - 1];
                            if (!cap.IsUndefined) sb.Append(cap.AsStringUnchecked);
                            i++;
                            break;
                        }
                    }
                    sb.Append('$');
                    break;
            }
        }
        return sb.ToString();
    }
}

internal sealed class JsRegExpStringIterator : JsObject
{
    public readonly JsObject Matcher;
    public readonly string Input;
    public readonly bool Global;
    public readonly bool Unicode;
    public bool Done;

    public JsRegExpStringIterator(Realm realm, JsObject matcher, string input, bool global, bool unicode) : base(realm, realm.RegExpStringIteratorPrototype)
    {
        Matcher = matcher;
        Input = input;
        Global = global;
        Unicode = unicode;
    }
}
