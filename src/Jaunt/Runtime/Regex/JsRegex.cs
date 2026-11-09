using System;
using System.Collections.Generic;

namespace Jaunt.Runtime.Regex;

/// <summary>
/// Native ECMAScript regular expression: pattern compiled to a small bytecode run by a backtracking VM with an
/// explicit choice stack (no CLR recursion proportional to the input). Runs every pattern it can compile. Before
/// matching, a literal prefix or first-character set skips start positions that cannot match, and single-character
/// loops consume in a tight loop, skip positions their continuation cannot start at, and drop useless backtracking.
/// </summary>
internal sealed class JsRegex
{
    // opcodes (low byte); Back marks right-to-left matching inside lookbehind
    private const int OpMatch = 0, OpChar = 1, OpCharI = 2, OpAny = 3, OpSet = 4, OpSplit = 5, OpSplitPrefer = 6, OpJmp = 7,
        OpSaveStart = 8, OpSaveEnd = 9, OpResetCaps = 10, OpBol = 11, OpEol = 12, OpWordB = 13, OpBackRef = 14, OpLook = 15,
        OpRegSet = 16, OpRegSetPos = 17, OpLoopCheck = 18, OpEmptyCheck = 19, OpRegInc = 20, OpSimpleLoop = 21, OpFail = 22;
    private const int Back = 0x100;

    private readonly int[] _code;
    private readonly CharSet[] _sets;
    private readonly bool _unicode;
    public readonly int CaptureCount;      // including group 0
    public readonly string?[] GroupNames;  // per group index
    private readonly int _regCount;
    /// <summary>The pattern uses semantics the .NET Regex backend cannot reproduce exactly.</summary>
    public bool RequiresNative;

    /// <summary>Approximate memory of the compiled code alone (character sets are charged as they are built).</summary>
    public long CodeBytes => 64 + _code.Length * 4L;

    /// <summary>Approximate memory of the compiled program (code + character sets), for budgets and the pattern cache.</summary>
    public long ApproxBytes
    {
        get
        {
            long n = 64 + _code.Length * 4L;
            foreach (var set in _sets) n += 32 + set.Ranges.Length * 4L;
            return n;
        }
    }

    private JsRegex(int[] code, CharSet[] sets, bool unicode, int captureCount, string?[] names, int regCount)
    {
        _code = code;
        _sets = sets;
        _unicode = unicode;
        CaptureCount = captureCount;
        GroupNames = names;
        _regCount = regCount;
    }

    public static JsRegex Compile(string pattern, string flags)
    {
        var parser = new RegexParser(pattern, flags);
        var tree = RegexParser.Resolve(parser.Parse(), parser);
        var c = new Compiler(flags.Contains('u') || flags.Contains('v'));
        c.Emit(tree, back: false);
        c.Code.Add(OpMatch);
        var re = new JsRegex(c.Code.ToArray(), c.Sets.ToArray(), c.Unicode, parser.CaptureCount, parser.GroupNames.ToArray(), c.RegCount);
        re.AnalyseLoops();
        re.AnalyseStart(tree);
        bool dupNames = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in parser.GroupNames) if (n != null && !seen.Add(n)) dupNames = true;
        bool unicodeIgnoreCase = (flags.Contains('u') || flags.Contains('v')) && flags.Contains('i');
        re.RequiresNative = flags.Contains('v') || dupNames || parser.NeedsNative || unicodeIgnoreCase || pattern.Contains("\\p{") || pattern.Contains("\\P{")
            || (pattern.Contains("(?<") && pattern.Contains("\\u")) || NeedsNative(tree);
        return re;
    }

    private static bool NeedsNative(RNode n) => n switch
    {
        RRepeat r => (r.CapFrom < r.CapTo && r.Max != 1) || r.Body is RLook || NeedsNative(r.Body),
        RSeq s => s.Items.Exists(NeedsNative),
        RAlt a => a.Alts.Exists(NeedsNative),
        RGroup g => NeedsNative(g.Body),
        RLook l => NeedsNative(l.Body),
        _ => false,
    };

    // ------------------------------------------------------------------ analysis (optimisations that never change a result)

    private static int InstrLen(int[] code, int pc) => (code[pc] & 0xFF) switch
    {
        OpMatch or OpFail => 1,
        OpSet or OpResetCaps or OpRegSet or OpLook => 3,
        OpBackRef => 3 + code[pc + 2],
        OpLoopCheck => 6,
        OpEmptyCheck => 4,
        OpSimpleLoop => 9,
        _ => 2,
    };

    private static bool IsLineTerminator(int c) => c is 10 or 13 or 0x2028 or 0x2029;

    /// <summary>Matches one character with a simple-loop / single-char matcher description.</summary>
    private bool MatchOne(int kind, int arg, int flags, int c)
    {
        switch (kind)
        {
            case 0: return c == arg;
            case 1: return RegexCase.Canon(c, _unicode) == arg;
            case 2: return arg != 0 || !IsLineTerminator(c);
            default:
            {
                var set = _sets[arg];
                bool hit = (flags & 1) != 0 ? set.Contains(RegexCase.Canon(c, _unicode)) : set.Contains(c);
                return hit != ((flags & 2) != 0);
            }
        }
    }

    /// <summary>
    /// A forward simple loop whose continuation starts with a literal character (past capture saves) records it: a
    /// position where that character is absent cannot continue, so backtracking skips it. When the loop cannot match
    /// the character itself, or the continuation is an end anchor the loop cannot satisfy by giving characters back,
    /// giving back never helps and the loop pushes no choice (it is atomic).
    /// </summary>
    private void AnalyseLoops()
    {
        var code = _code;
        for (int pc = 0; pc < code.Length; pc += InstrLen(code, pc))
        {
            if (code[pc] != OpSimpleLoop) continue;   // forward loops only
            int q = pc + 9;
            for (int hops = 0; hops < 16; hops++)
            {
                int op = code[q];
                if (op == OpSaveStart || op == OpSaveEnd) q += 2;
                else if (op == OpJmp) q = code[q + 1];
                else break;
            }
            int kind = code[pc + 1], arg = code[pc + 2], flags = code[pc + 3];
            bool greedy = code[pc + 6] != 0;
            if (code[q] == OpChar)
            {
                int c = code[q + 1];
                if (c < 0xD800 || (c > 0xDFFF && c <= 0xFFFF))
                {
                    code[pc + 7] = c;
                    if (greedy && !MatchOne(kind, arg, flags, c)) code[pc + 8] = 1;
                }
            }
            else if (code[q] == OpEol && greedy)
            {
                if (code[q + 1] == 0 || !(MatchOne(kind, arg, flags, 10) || MatchOne(kind, arg, flags, 13)
                    || MatchOne(kind, arg, flags, 0x2028) || MatchOne(kind, arg, flags, 0x2029)))
                    code[pc + 8] = 1;
            }
        }
    }

    /// <summary>The pattern can only match at position 0 (a leading <c>^</c> without the m flag).</summary>
    private bool _anchored;
    /// <summary>Literal text every match begins with (BMP, no surrogates), searched for with IndexOf.</summary>
    private string? _prefix;
    /// <summary>Code units that can begin a match (or, with <see cref="_firstExcept"/>, the ones that cannot).</summary>
    private System.Buffers.SearchValues<char>? _firstValues;
    private bool _firstExcept;
    /// <summary>Code points that can begin a match, checked one position at a time when no vectorised form applies.</summary>
    private CharSet? _firstSet;
    private bool HasStartScan => _prefix != null || _firstValues != null || _firstSet != null;

    private void AnalyseStart(RNode tree)
    {
        var items = tree is RSeq seq ? seq.Items : new List<RNode> { tree };
        if (items.Count > 0 && items[0] is RAssert { Kind: AssertKind.Bol, Multiline: false })
        {
            _anchored = true;
            return;
        }
        var sb = new System.Text.StringBuilder();
        foreach (var it in items)
        {
            if (it is RAssert or RLook) continue;   // zero-width: the consumed text is unchanged
            if (it is RChar { IgnoreCase: false } ch && (ch.C < 0xD800 || (ch.C > 0xDFFF && ch.C <= 0xFFFF)))
            {
                sb.Append((char)ch.C);
                continue;
            }
            break;
        }
        if (sb.Length > 0)
        {
            _prefix = sb.ToString();
            return;
        }
        var (set, nullable) = First(tree, _unicode);
        if (set == null || nullable || set.IsEmpty) return;
        var excluded = set.Complement(0x10FFFF);
        if (excluded.IsEmpty) return;   // every position can begin a match
        if (excluded.Ranges[^1] < 128)
        {
            // only ASCII characters are excluded (a negated class, ignore-case): every other code unit is a candidate,
            // and in Unicode mode the high half of a pair is found before its low half
            _firstValues = System.Buffers.SearchValues.Create(Chars(excluded));
            _firstExcept = true;
            return;
        }
        bool supplementary = set.Ranges[^1] > 0xFFFF;
        var bmp = set.Intersect(CharSet.Range(0, 0xFFFF));
        bool surrogates = !bmp.Intersect(CharSet.Range(0xD800, 0xDFFF)).IsEmpty;
        if (!(_unicode && supplementary))
        {
            // in non-Unicode mode a code point above U+FFFF never matches a single code unit: ignored
            var non = bmp.Complement(0xFFFF);
            if (bmp.Count <= 256 && !(_unicode && surrogates))
            {
                _firstValues = System.Buffers.SearchValues.Create(Chars(bmp));
                return;
            }
            if (non.Count <= 256 && non.Intersect(CharSet.Range(0xD800, 0xDFFF)).IsEmpty)
            {
                if (non.IsEmpty) return;   // every code unit can begin a match
                _firstValues = System.Buffers.SearchValues.Create(Chars(non));
                _firstExcept = true;
                return;
            }
        }
        _firstSet = set;
    }

    /// <summary>The ASCII characters that satisfy <paramref name="matches"/>, plus every non-ASCII code point when <paramref name="nonAscii"/>.</summary>
    private static CharSet IgnoreCaseFirst(Func<int, bool> matches, bool nonAscii)
    {
        var list = new List<(int, int)>();
        for (int x = 0; x < 128; x++) if (matches(x)) list.Add((x, x));
        if (nonAscii) list.Add((128, 0x10FFFF));
        return CharSet.FromRanges(list);
    }

    private static char[] Chars(CharSet set)
    {
        var a = new char[set.Count];
        int k = 0;
        foreach (var c in set.CodePoints()) a[k++] = (char)c;
        return a;
    }

    /// <summary>
    /// The characters that can begin a match of <paramref name="n"/> (null: any) and whether it can match empty.
    /// Over-approximates: '.', back references and class strings give up, and ignore-case may add every non-ASCII character.
    /// </summary>
    private static (CharSet? Set, bool Nullable) First(RNode n, bool unicode)
    {
        switch (n)
        {
            case REmpty:
            case RAssert:
            case RLook:
                return (CharSet.Empty, true);
            case RChar ch when !ch.IgnoreCase:
                return (CharSet.Single(ch.C), false);
            case RChar ch:
            {
                // matches x when Canonicalize(x) == Canonicalize(C); without u/v no non-ASCII character canonicalizes to ASCII
                int k = RegexCase.Canon(ch.C, unicode);
                return (IgnoreCaseFirst(x => RegexCase.Canon(x, unicode) == k, unicode || k >= 128), false);
            }
            case RSet set when !set.IgnoreCase:
                return (set.Negate ? set.Set.Complement() : set.Set, false);
            case RSet set:
            {
                // as compiled: membership of Canonicalize(x) in the closed set
                var closed = set.Canonical ? set.Set : RegexCase.Close(set.Set, unicode);
                bool negate = set.Negate;
                bool nonAscii = unicode || negate || (!closed.IsEmpty && closed.Ranges[^1] >= 128);
                return (IgnoreCaseFirst(x => closed.Contains(RegexCase.Canon(x, unicode)) != negate, nonAscii), false);
            }
            case RGroup g:
                return First(g.Body, unicode);
            case RRepeat r:
            {
                if (r.Max == 0) return (CharSet.Empty, true);
                var (bs, bn) = First(r.Body, unicode);
                return (bs, bn || r.Min == 0);
            }
            case RSeq s:
            {
                var acc = CharSet.Empty;
                foreach (var item in s.Items)
                {
                    var (fs, fn) = First(item, unicode);
                    if (fs == null) return (null, false);
                    acc = acc.Union(fs);
                    if (!fn) return (acc, false);
                }
                return (acc, true);
            }
            case RAlt a:
            {
                var acc = CharSet.Empty;
                bool nullable = false;
                foreach (var alt in a.Alts)
                {
                    var (fs, fn) = First(alt, unicode);
                    if (fs == null) return (null, false);
                    acc = acc.Union(fs);
                    nullable |= fn;
                }
                return (acc, nullable);
            }
            default:
                return (null, false);
        }
    }

    // ------------------------------------------------------------------ compiler

    private sealed class Compiler
    {
        public readonly List<int> Code = new();
        public readonly List<CharSet> Sets = new();
        public readonly bool Unicode;
        public int RegCount;

        public Compiler(bool unicode) => Unicode = unicode;

        private int Here => Code.Count;
        private void Patch(int at, int target) => Code[at] = target;

        public void Emit(RNode n, bool back)
        {
            int b = back ? Back : 0;
            switch (n)
            {
                case REmpty:
                    break;
                case RSeq s:
                    if (back) for (int i = s.Items.Count - 1; i >= 0; i--) Emit(s.Items[i], back);
                    else foreach (var item in s.Items) Emit(item, back);
                    break;
                case RAlt a:
                {
                    var ends = new List<int>();
                    for (int i = 0; i < a.Alts.Count; i++)
                    {
                        if (i < a.Alts.Count - 1)
                        {
                            Code.Add(OpSplit);
                            int slot = Here;
                            Code.Add(0);
                            Emit(a.Alts[i], back);
                            Code.Add(OpJmp);
                            ends.Add(Here);
                            Code.Add(0);
                            Patch(slot, Here);
                        }
                        else Emit(a.Alts[i], back);
                    }
                    foreach (var e in ends) Patch(e, Here);
                    break;
                }
                case RChar ch:
                    if (LegacyIgnoreCaseChars(ch) is { } same)
                    {
                        if (same.Count == 1) { Code.Add(OpChar | b); Code.Add(same.Ranges[0]); }
                        else { Code.Add(OpSet | b); Code.Add(AddSet(same)); Code.Add(0); }
                    }
                    else if (ch.IgnoreCase) { Code.Add(OpCharI | b); Code.Add(RegexCase.Canon(ch.C, Unicode)); }
                    else { Code.Add(OpChar | b); Code.Add(ch.C); }
                    break;
                case RAny any:
                    Code.Add(OpAny | b);
                    Code.Add(any.DotAll ? 1 : 0);
                    break;
                case RSet set:
                    EmitSet(set, b);
                    break;
                case RStrings rs:
                {
                    // alternatives: each string (longest first), then the single characters
                    var ends = new List<int>();
                    foreach (var str in rs.Strings)
                    {
                        Code.Add(OpSplit);
                        int slot = Here;
                        Code.Add(0);
                        var cps = CodePoints(str);
                        if (back) cps.Reverse();
                        foreach (var cp in cps)
                        {
                            if (rs.IgnoreCase) { Code.Add(OpCharI | b); Code.Add(RegexCase.Canon(cp, true)); }
                            else { Code.Add(OpChar | b); Code.Add(cp); }
                        }
                        Code.Add(OpJmp);
                        ends.Add(Here);
                        Code.Add(0);
                        Patch(slot, Here);
                    }
                    EmitSet(new RSet { Set = rs.Chars, IgnoreCase = rs.IgnoreCase, Canonical = rs.IgnoreCase }, b);
                    foreach (var e in ends) Patch(e, Here);
                    break;
                }
                case RGroup g:
                    Code.Add(back ? OpSaveEnd : OpSaveStart);
                    Code.Add(g.Index);
                    Emit(g.Body, back);
                    Code.Add(back ? OpSaveStart : OpSaveEnd);
                    Code.Add(g.Index);
                    break;
                case RBackRef br:
                    Code.Add(OpBackRef | b);
                    Code.Add(br.IgnoreCase ? 1 : 0);
                    Code.Add(br.Groups.Length);
                    foreach (var gi in br.Groups) Code.Add(gi);
                    break;
                case RAssert asr:
                    switch (asr.Kind)
                    {
                        case AssertKind.Bol: Code.Add(OpBol); Code.Add(asr.Multiline ? 1 : 0); break;
                        case AssertKind.Eol: Code.Add(OpEol); Code.Add(asr.Multiline ? 1 : 0); break;
                        default:
                            Code.Add(OpWordB);
                            Code.Add((asr.Kind == AssertKind.NotWordB ? 1 : 0) | (asr.UnicodeIgnoreCase ? 2 : 0));
                            break;
                    }
                    break;
                case RLook lk:
                {
                    Code.Add(OpLook);
                    Code.Add(lk.Negate ? 1 : 0);
                    int lenSlot = Here;
                    Code.Add(0);
                    Emit(lk.Body, lk.Behind);
                    Code.Add(OpMatch);
                    Patch(lenSlot, Here);
                    break;
                }
                case RRepeat rp:
                    EmitRepeat(rp, back);
                    break;
                default:
                    throw new InvalidOperationException("Jaunt internal error: unknown regex node.");
            }
        }

        private static List<int> CodePoints(string s)
        {
            var l = new List<int>();
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { l.Add(char.ConvertToUtf32(s[i], s[i + 1])); i++; }
                else l.Add(s[i]);
            }
            return l;
        }

        /// <summary>
        /// The set and matcher flags of a class. Ignore-case matching (Canonicalize(x) in the closed set) becomes plain
        /// membership in the preimage of the closed set: the same characters, no case mapping per character.
        /// </summary>
        private (int Index, int Flags) AddSet(RSet set)
        {
            var s = set.Set;
            if (set.IgnoreCase)
            {
                if (!set.Canonical) s = RegexCase.Close(s, Unicode);
                s = RegexCase.Preimage(s, Unicode);
            }
            Sets.Add(s);
            return (Sets.Count - 1, set.Negate ? 2 : 0);
        }

        private int AddSet(CharSet s)
        {
            Sets.Add(s);
            return Sets.Count - 1;
        }

        private void EmitSet(RSet set, int b)
        {
            var (index, flags) = AddSet(set);
            Code.Add(OpSet | b);
            Code.Add(index);
            Code.Add(flags);
        }

        /// <summary>
        /// Without u/v, an ignore-case character is compiled as the set of characters with the same canonical form (the
        /// case table is small); with u/v it stays a canonicalizing comparison, which needs no table.
        /// </summary>
        private CharSet? LegacyIgnoreCaseChars(RChar ch) =>
            ch.IgnoreCase && !Unicode ? RegexCase.Preimage(CharSet.Single(RegexCase.Canon(ch.C, false)), false) : null;

        private static bool IsSingleChar(RNode n) => n is RChar or RAny or RSet;

        private void EmitRepeat(RRepeat rp, bool back)
        {
            if (rp.Max == 0) return;
            int b = back ? Back : 0;
            if (IsSingleChar(rp.Body) && rp.CapFrom >= rp.CapTo)
            {
                // single-character body: specialised loop that backtracks one character at a time
                Code.Add(OpSimpleLoop | b);
                switch (rp.Body)
                {
                    case RChar ch when LegacyIgnoreCaseChars(ch) is { } same: Code.Add(3); Code.Add(AddSet(same)); Code.Add(0); break;
                    case RChar ch when ch.IgnoreCase: Code.Add(1); Code.Add(RegexCase.Canon(ch.C, Unicode)); Code.Add(0); break;
                    case RChar ch: Code.Add(0); Code.Add(ch.C); Code.Add(0); break;
                    case RAny any: Code.Add(2); Code.Add(any.DotAll ? 1 : 0); Code.Add(0); break;
                    case RSet set:
                    {
                        var (index, flags) = AddSet(set);
                        Code.Add(3); Code.Add(index); Code.Add(flags);
                        break;
                    }
                }
                Code.Add(rp.Min);
                Code.Add(rp.Max);
                Code.Add(rp.Greedy ? 1 : 0);
                Code.Add(-1);   // follow character, set by AnalyseLoops
                Code.Add(0);    // atomic, set by AnalyseLoops
                return;
            }
            int r = RegCount++, p = RegCount++;
            Code.Add(OpRegSet); Code.Add(r); Code.Add(0);
            int loop = Here;
            Code.Add(OpLoopCheck); Code.Add(r); Code.Add(rp.Min); Code.Add(rp.Max); Code.Add(rp.Greedy ? 1 : 0);
            int exitSlot = Here;
            Code.Add(0);
            Code.Add(OpRegSetPos); Code.Add(p);
            if (rp.CapFrom < rp.CapTo) { Code.Add(OpResetCaps); Code.Add(rp.CapFrom); Code.Add(rp.CapTo); }
            Emit(rp.Body, back);
            Code.Add(OpEmptyCheck); Code.Add(r); Code.Add(rp.Min); Code.Add(p);
            Code.Add(OpRegInc); Code.Add(r);
            Code.Add(OpJmp); Code.Add(loop);
            Patch(exitSlot, Here);
        }
    }

    // ------------------------------------------------------------------ matcher

    internal struct Choice
    {
        public int Kind;   // 0 plain, 1 greedy simple loop, 2 lazy simple loop
        public int Pc;
        public int Pos;
        public int Undo;
        public int Count;
        public int LoopPc; // simple loop op address (bounds + matcher)
    }

    /// <summary>
    /// Per-search mutable state (not thread-safe). A matcher can be kept for later searches (<see cref="Reset"/>): its
    /// backtracking stacks and capture buffer are reused, their contents never outlive a search.
    /// </summary>
    public sealed class Matcher
    {
        private JsRegex _re;
        private string _s;
        private readonly Action? _tick;
        /// <summary>[start,end) pairs of the last search; longer than 2 * CaptureCount when the matcher was reused (see <see cref="CopyCaps"/>).</summary>
        public int[] Caps;
        private int[] _regs;
        private int[] _undoSlot = new int[64];
        private int[] _undoOld = new int[64];
        private int _undoCount;
        private Choice[] _choices = new Choice[64];
        private int _choiceCount;
        private int _steps;

        public Matcher(JsRegex re, string input, Action? tick)
        {
            _re = re;
            _s = input;
            _tick = tick;
            Caps = new int[re.CaptureCount * 2];
            _regs = new int[Math.Max(1, re._regCount)];
        }

        /// <summary>Prepares a kept matcher for a search of <paramref name="re"/> in <paramref name="input"/>.</summary>
        public void Reset(JsRegex re, string input)
        {
            _re = re;
            _s = input;
            if (Caps.Length < re.CaptureCount * 2) Caps = new int[re.CaptureCount * 2];
            if (_regs.Length < re._regCount) _regs = new int[re._regCount];
        }

        /// <summary>Drops the input reference after a search; false when the stacks grew too large to keep this matcher.</summary>
        public bool Release()
        {
            _s = "";
            return _choices.Length <= MaxKeptStack && _undoSlot.Length <= MaxKeptStack;
        }

        private const int MaxKeptStack = 4096;

        /// <summary>The captures of the last successful search, exactly 2 * CaptureCount long.</summary>
        public int[] CopyCaps() => Caps.AsSpan(0, _re.CaptureCount * 2).ToArray();

        /// <summary>RegExpBuiltinExec matcher loop: tries positions from <paramref name="start"/>. Caps hold [start,end) pairs.</summary>
        public bool Search(int start, bool sticky)
        {
            var re = _re;
            var s = _s;
            int len = s.Length;
            if (re._unicode && start > 0 && start < len && char.IsLowSurrogate(s[start]) && char.IsHighSurrogate(s[start - 1])) start--;
            if (re._anchored)
            {
                // a leading ^ (no m flag) only matches at 0: the other positions need not be tried
                if (start != 0) return false;
                sticky = true;
            }
            bool scan = !sticky && re.HasStartScan;
            int capLen = re.CaptureCount * 2;
            for (int at = start; at <= len;)
            {
                if (scan)
                {
                    at = NextStart(at);
                    if (at < 0) return false;
                }
                Caps.AsSpan(0, capLen).Fill(-1);
                _undoCount = 0;
                _choiceCount = 0;
                int end = Run(0, at);
                if (end >= 0)
                {
                    Caps[0] = at;
                    Caps[1] = end;
                    return true;
                }
                if (sticky) return false;
                at += re._unicode && at + 1 < len && char.IsHighSurrogate(s[at]) && char.IsLowSurrogate(s[at + 1]) ? 2 : 1;
            }
            return false;
        }

        /// <summary>The first position at or after <paramref name="at"/> where a match can begin (a superset), or -1.</summary>
        private int NextStart(int at)
        {
            var re = _re;
            var span = _s.AsSpan(at);
            int i;
            if (re._prefix is { } p) i = p.Length == 1 ? span.IndexOf(p[0]) : span.IndexOf(p.AsSpan());
            else if (re._firstValues is { } v) i = re._firstExcept ? span.IndexOfAnyExcept(v) : span.IndexOfAny(v);
            else return ScanFirstSet(at);
            return i < 0 ? -1 : at + i;
        }

        private int ScanFirstSet(int at)
        {
            var s = _s;
            var set = _re._firstSet!;
            bool unicode = _re._unicode;
            for (int i = at; i < s.Length; i++)
            {
                int c = s[i];
                if (unicode && (uint)(c - 0xD800) <= 0x7FF)
                {
                    if (c <= 0xDBFF && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) c = char.ConvertToUtf32((char)c, s[i + 1]);
                    else if (c >= 0xDC00 && i > at && char.IsHighSurrogate(s[i - 1])) continue;   // inside a pair
                }
                if (set.Contains(c)) return i;
            }
            return -1;
        }

        private void SetCap(int slot, int v)
        {
            PushUndo(slot, Caps[slot]);
            Caps[slot] = v;
        }

        private void SetReg(int r, int v)
        {
            PushUndo(~r, _regs[r]);
            _regs[r] = v;
        }

        private void PushUndo(int slot, int old)
        {
            if (_undoCount == _undoSlot.Length)
            {
                Array.Resize(ref _undoSlot, _undoCount * 2);
                Array.Resize(ref _undoOld, _undoCount * 2);
            }
            _undoSlot[_undoCount] = slot;
            _undoOld[_undoCount++] = old;
        }

        private void UndoTo(int depth)
        {
            while (_undoCount > depth)
            {
                _undoCount--;
                int slot = _undoSlot[_undoCount];
                if (slot >= 0) Caps[slot] = _undoOld[_undoCount];
                else _regs[~slot] = _undoOld[_undoCount];
            }
        }

        private void PushChoice(int kind, int pc, int pos, int count = 0, int loopPc = 0)
        {
            if (_choiceCount == _choices.Length) Array.Resize(ref _choices, _choiceCount * 2);
            _choices[_choiceCount++] = new Choice { Kind = kind, Pc = pc, Pos = pos, Undo = _undoCount, Count = count, LoopPc = loopPc };
        }

        /// <summary>Reads the character at pos (forward) or before pos (backward). Returns the width, 0 at the edge.</summary>
        private int Read(int pos, bool back, out int c)
        {
            var s = _s;
            if (!back)
            {
                if (pos >= s.Length) { c = -1; return 0; }
                c = s[pos];
                if (_re._unicode && char.IsHighSurrogate((char)c) && pos + 1 < s.Length && char.IsLowSurrogate(s[pos + 1]))
                {
                    c = char.ConvertToUtf32((char)c, s[pos + 1]);
                    return 2;
                }
                return 1;
            }
            if (pos <= 0) { c = -1; return 0; }
            c = s[pos - 1];
            if (_re._unicode && char.IsLowSurrogate((char)c) && pos - 2 >= 0 && char.IsHighSurrogate(s[pos - 2]))
            {
                c = char.ConvertToUtf32(s[pos - 2], (char)c);
                return 2;
            }
            return 1;
        }

        private bool IsWordChar(int e, bool unicodeIgnoreCase)
        {
            if (e < 0 || e >= _s.Length) return false;
            char c = _s[e];
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_') return true;
            return unicodeIgnoreCase && (c == 'ſ' || c == 'K');   // long s, Kelvin sign
        }

        private void Step()
        {
            if (++_steps >= 4096)
            {
                _steps = 0;
                _tick?.Invoke();
            }
        }

        /// <summary><paramref name="n"/> steps at once: the same ticks as as many <see cref="Step"/> calls.</summary>
        private void Steps(int n)
        {
            _steps += n;
            while (_steps >= 4096)
            {
                _steps -= 4096;
                _tick?.Invoke();
            }
        }

        /// <summary>Runs from <paramref name="pc"/> at <paramref name="pos"/>; returns the end position or -1.</summary>
        private int Run(int pc, int pos)
        {
            var re = _re;
            var code = re._code;
            var s = _s;
            bool unicode = re._unicode;
            int baseChoice = _choiceCount;
            while (true)
            {
                Step();
                int op = code[pc];
                bool back = (op & Back) != 0;
                switch (op & 0xFF)
                {
                    case OpMatch:
                        return pos;
                    case OpChar:
                    {
                        int want = code[pc + 1];
                        if (!back && (uint)(want - 0xD800) > 0x7FF && want <= 0xFFFF)
                        {
                            // a BMP character that is not a surrogate is one code unit in either mode
                            if (pos >= s.Length || s[pos] != want) goto Fail;
                            pos++;
                            pc += 2;
                            continue;
                        }
                        int w = Read(pos, back, out int c);
                        if (w == 0 || c != want) goto Fail;
                        pos += back ? -w : w;
                        pc += 2;
                        continue;
                    }
                    case OpCharI:
                    {
                        int w = Read(pos, back, out int c);
                        if (w == 0 || RegexCase.Canon(c, unicode) != code[pc + 1]) goto Fail;
                        pos += back ? -w : w;
                        pc += 2;
                        continue;
                    }
                    case OpAny:
                    {
                        int w = Read(pos, back, out int c);
                        if (w == 0 || (code[pc + 1] == 0 && IsLineTerminator(c))) goto Fail;
                        pos += back ? -w : w;
                        pc += 2;
                        continue;
                    }
                    case OpSet:
                    {
                        int flags = code[pc + 2];
                        if (!back && (flags & 1) == 0 && pos < s.Length && (!unicode || (uint)(s[pos] - 0xD800) > 0x7FF))
                        {
                            // one code unit, no case folding
                            if (re._sets[code[pc + 1]].Contains(s[pos]) == ((flags & 2) != 0)) goto Fail;
                            pos++;
                            pc += 3;
                            continue;
                        }
                        int w = Read(pos, back, out int c);
                        if (w == 0 || !re.MatchOne(3, code[pc + 1], flags, c)) goto Fail;
                        pos += back ? -w : w;
                        pc += 3;
                        continue;
                    }
                    case OpSplit:
                        PushChoice(0, code[pc + 1], pos);
                        pc += 2;
                        continue;
                    case OpSplitPrefer:
                        PushChoice(0, pc + 2, pos);
                        pc = code[pc + 1];
                        continue;
                    case OpJmp:
                        pc = code[pc + 1];
                        continue;
                    case OpSaveStart:
                        SetCap(code[pc + 1] * 2, pos);
                        pc += 2;
                        continue;
                    case OpSaveEnd:
                        SetCap(code[pc + 1] * 2 + 1, pos);
                        pc += 2;
                        continue;
                    case OpResetCaps:
                        for (int g = code[pc + 1]; g < code[pc + 2]; g++)
                        {
                            if (Caps[2 * g] != -1) SetCap(2 * g, -1);
                            if (Caps[2 * g + 1] != -1) SetCap(2 * g + 1, -1);
                        }
                        pc += 3;
                        continue;
                    case OpBol:
                        if (!(pos == 0 || (code[pc + 1] != 0 && IsLineTerminator(s[pos - 1])))) goto Fail;
                        pc += 2;
                        continue;
                    case OpEol:
                        if (!(pos == s.Length || (code[pc + 1] != 0 && IsLineTerminator(s[pos])))) goto Fail;
                        pc += 2;
                        continue;
                    case OpWordB:
                    {
                        bool uic = (code[pc + 1] & 2) != 0;
                        bool a = IsWordChar(pos - 1, uic), bb = IsWordChar(pos, uic);
                        bool boundary = a != bb;
                        if (boundary == ((code[pc + 1] & 1) != 0)) goto Fail;
                        pc += 2;
                        continue;
                    }
                    case OpBackRef:
                    {
                        bool ic = code[pc + 1] != 0;
                        int n = code[pc + 2];
                        int gs = -1, ge = -1;
                        for (int k = 0; k < n; k++)
                        {
                            int g = code[pc + 3 + k];
                            if (Caps[2 * g] >= 0 && Caps[2 * g + 1] >= 0) { gs = Caps[2 * g]; ge = Caps[2 * g + 1]; break; }
                        }
                        pc += 3 + n;
                        if (gs < 0) continue; // unmatched group: matches empty
                        int len = ge - gs;
                        if (!back)
                        {
                            if (pos + len > s.Length || !SubstringEquals(gs, pos, len, ic)) goto Fail;
                            pos += len;
                        }
                        else
                        {
                            if (pos - len < 0 || !SubstringEquals(gs, pos - len, len, ic)) goto Fail;
                            pos -= len;
                        }
                        continue;
                    }
                    case OpLook:
                    {
                        bool neg = code[pc + 1] != 0;
                        int after = code[pc + 2];
                        int undo = _undoCount;
                        int choices = _choiceCount;
                        int r = Run(pc + 3, pos);
                        _choiceCount = choices; // lookarounds are atomic
                        if (neg)
                        {
                            UndoTo(undo);
                            if (r >= 0) goto Fail;
                        }
                        else if (r < 0) goto Fail;
                        pc = after;
                        continue;
                    }
                    case OpRegSet:
                        SetReg(code[pc + 1], code[pc + 2]);
                        pc += 3;
                        continue;
                    case OpRegSetPos:
                        SetReg(code[pc + 1], pos);
                        pc += 2;
                        continue;
                    case OpRegInc:
                        SetReg(code[pc + 1], _regs[code[pc + 1]] + 1);
                        pc += 2;
                        continue;
                    case OpLoopCheck:
                    {
                        int count = _regs[code[pc + 1]];
                        int min = code[pc + 2], max = code[pc + 3];
                        bool greedy = code[pc + 4] != 0;
                        int exit = code[pc + 5];
                        int body = pc + 6;
                        if (count < min) { pc = body; continue; }
                        if (max != -1 && count >= max) { pc = exit; continue; }
                        if (greedy) { PushChoice(0, exit, pos); pc = body; }
                        else { PushChoice(0, body, pos); pc = exit; }
                        continue;
                    }
                    case OpEmptyCheck:
                    {
                        // an iteration beyond the minimum that consumed nothing fails (RepeatMatcher step 2.b)
                        if (_regs[code[pc + 1]] >= code[pc + 2] && pos == _regs[code[pc + 3]]) goto Fail;
                        pc += 4;
                        continue;
                    }
                    case OpSimpleLoop:
                    {
                        int kind = code[pc + 1], arg = code[pc + 2], flags = code[pc + 3];
                        int min = code[pc + 4], max = code[pc + 5];
                        bool greedy = code[pc + 6] != 0;
                        int next = pc + 9;
                        int count = 0;
                        if (!back && kind == 3 && (flags & 1) == 0)
                        {
                            // forward class without case folding: a tight loop over code units (a surrogate stops it in
                            // Unicode mode, and the general loop below goes on from there)
                            var set = re._sets[arg];
                            bool negate = (flags & 2) != 0;
                            int limit = greedy ? (max == -1 ? int.MaxValue : max) : min;
                            while (count < limit && pos < s.Length)
                            {
                                int c = s[pos];
                                if ((unicode && (uint)(c - 0xD800) <= 0x7FF) || set.Contains(c) == negate) break;
                                pos++;
                                count++;
                            }
                            Steps(count);
                        }
                        while (count < min)
                        {
                            int w = Read(pos, back, out int c);
                            if (w == 0 || !re.MatchOne(kind, arg, flags, c)) goto Fail;
                            pos += back ? -w : w;
                            count++;
                            Step();
                        }
                        if (greedy)
                        {
                            while (max == -1 || count < max)
                            {
                                int w = Read(pos, back, out int c);
                                if (w == 0 || !re.MatchOne(kind, arg, flags, c)) break;
                                pos += back ? -w : w;
                                count++;
                                Step();
                            }
                            // an atomic loop (see AnalyseLoops) has no use for the characters it could give back
                            if (count > min && code[pc + 8] == 0) PushChoice(1, next, pos, count - min, pc);
                        }
                        else
                        {
                            int follow = code[pc + 7];
                            if (follow >= 0)
                            {
                                // the continuation starts with `follow`: take characters until it is next (forward loops only)
                                while (pos >= s.Length || s[pos] != follow)
                                {
                                    if (max != -1 && count >= max) goto Fail;
                                    int w = Read(pos, false, out int c);
                                    if (w == 0 || !re.MatchOne(kind, arg, flags, c)) goto Fail;
                                    pos += w;
                                    count++;
                                    Step();
                                }
                            }
                            if (max == -1 || count < max) PushChoice(2, next, pos, count, pc);
                        }
                        pc = next;
                        continue;
                    }
                    case OpFail:
                        goto Fail;
                    default:
                        throw new InvalidOperationException("Jaunt internal error: bad regex opcode.");
                }

            Fail:
                while (true)
                {
                    if (_choiceCount == baseChoice) return -1;
                    ref var ch = ref _choices[_choiceCount - 1];
                    UndoTo(ch.Undo);
                    if (ch.Kind == 0)
                    {
                        pc = ch.Pc;
                        pos = ch.Pos;
                        _choiceCount--;
                        break;
                    }
                    int lop = code[ch.LoopPc];
                    bool lback = (lop & Back) != 0;
                    int follow = code[ch.LoopPc + 7];
                    if (ch.Kind == 1)
                    {
                        // give back one character; with a known follow character, go on to the first position that has it
                        bool resume;
                        while (true)
                        {
                            int w = Read(ch.Pos, !lback, out _);
                            ch.Pos += lback ? w : -w;
                            bool last = --ch.Count == 0;
                            resume = follow < 0 || s[ch.Pos] == follow;
                            if (last) _choiceCount--;
                            if (resume || last) break;
                            Step();
                        }
                        if (!resume) continue;
                        pos = ch.Pos;
                        pc = ch.Pc;
                        break;
                    }
                    else
                    {
                        // lazy: take one more character (and, with a follow character, up to the next position that has it)
                        int kind = code[ch.LoopPc + 1], arg = code[ch.LoopPc + 2], flags = code[ch.LoopPc + 3], max = code[ch.LoopPc + 5];
                        int w = Read(ch.Pos, lback, out int c);
                        if (w == 0 || !re.MatchOne(kind, arg, flags, c)) { _choiceCount--; continue; }
                        ch.Pos += lback ? -w : w;
                        ch.Count++;
                        if (follow >= 0)
                        {
                            bool dead = false;
                            while (ch.Pos >= s.Length || s[ch.Pos] != follow)
                            {
                                if (max != -1 && ch.Count >= max) { dead = true; break; }
                                w = Read(ch.Pos, false, out c);
                                if (w == 0 || !re.MatchOne(kind, arg, flags, c)) { dead = true; break; }
                                ch.Pos += w;
                                ch.Count++;
                                Step();
                            }
                            if (dead) { _choiceCount--; continue; }
                        }
                        pos = ch.Pos;
                        pc = ch.Pc;
                        if (max != -1 && ch.Count >= max) _choiceCount--;
                        break;
                    }
                }
            }
        }

        private bool SubstringEquals(int a, int b, int len, bool ignoreCase)
        {
            var s = _s;
            if (!ignoreCase) return string.CompareOrdinal(s, a, s, b, len) == 0;
            int i = 0;
            while (i < len)
            {
                int ca = s[a + i], cb = s[b + i];
                int w = 1;
                if (_re._unicode && char.IsHighSurrogate((char)ca) && i + 1 < len && char.IsLowSurrogate(s[a + i + 1])
                    && char.IsHighSurrogate((char)cb) && char.IsLowSurrogate(s[b + i + 1]))
                {
                    ca = char.ConvertToUtf32((char)ca, s[a + i + 1]);
                    cb = char.ConvertToUtf32((char)cb, s[b + i + 1]);
                    w = 2;
                }
                if (RegexCase.Canon(ca, _re._unicode) != RegexCase.Canon(cb, _re._unicode)) return false;
                i += w;
            }
            return true;
        }
    }
}
