using System;
using System.Collections.Generic;

namespace Jaunt.Runtime.Regex;

/// <summary>
/// Native ECMAScript regular expression: pattern compiled to a small bytecode run by a backtracking VM with an
/// explicit choice stack (no CLR recursion proportional to the input). Used for patterns the .NET Regex adaptation
/// cannot express exactly: Unicode property escapes, v-flag class sets, modifiers and duplicate named groups.
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
                    if (ch.IgnoreCase) { Code.Add(OpCharI | b); Code.Add(RegexCase.Canon(ch.C, Unicode)); }
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

        private int AddSet(RSet set)
        {
            var s = set.Set;
            if (set.IgnoreCase && !set.Canonical) s = RegexCase.Close(s, Unicode);
            Sets.Add(s);
            return Sets.Count - 1;
        }

        private void EmitSet(RSet set, int b)
        {
            Code.Add(OpSet | b);
            Code.Add(AddSet(set));
            Code.Add((set.IgnoreCase ? 1 : 0) | (set.Negate ? 2 : 0));
        }

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
                    case RChar ch when ch.IgnoreCase: Code.Add(1); Code.Add(RegexCase.Canon(ch.C, Unicode)); Code.Add(0); break;
                    case RChar ch: Code.Add(0); Code.Add(ch.C); Code.Add(0); break;
                    case RAny any: Code.Add(2); Code.Add(any.DotAll ? 1 : 0); Code.Add(0); break;
                    case RSet set: Code.Add(3); Code.Add(AddSet(set)); Code.Add((set.IgnoreCase ? 1 : 0) | (set.Negate ? 2 : 0)); break;
                }
                Code.Add(rp.Min);
                Code.Add(rp.Max);
                Code.Add(rp.Greedy ? 1 : 0);
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

    private struct Choice
    {
        public int Kind;   // 0 plain, 1 greedy simple loop, 2 lazy simple loop
        public int Pc;
        public int Pos;
        public int Undo;
        public int Count;
        public int LoopPc; // simple loop op address (bounds + matcher)
    }

    /// <summary>Per-match mutable state (not thread-safe; one instance per execution).</summary>
    public sealed class Matcher
    {
        private readonly JsRegex _re;
        private readonly string _s;
        private readonly Action? _tick;
        public readonly int[] Caps;
        private readonly int[] _regs;
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

        /// <summary>RegExpBuiltinExec matcher loop: tries positions from <paramref name="start"/>. Caps hold [start,end) pairs.</summary>
        public bool Search(int start, bool sticky)
        {
            int len = _s.Length;
            if (_re._unicode && start > 0 && start < len && char.IsLowSurrogate(_s[start]) && char.IsHighSurrogate(_s[start - 1])) start--;
            for (int at = start; at <= len;)
            {
                Array.Fill(Caps, -1);
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
                at += _re._unicode && at + 1 < len && char.IsHighSurrogate(_s[at]) && char.IsLowSurrogate(_s[at + 1]) ? 2 : 1;
            }
            return false;
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

        private static bool IsLineTerminator(int c) => c is 10 or 13 or 0x2028 or 0x2029;

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
            return unicodeIgnoreCase && (c == 'ſ' || c == 'K');
        }

        /// <summary>Matches one character with a simple-loop / single-char matcher description.</summary>
        private bool MatchOne(int kind, int arg, int flags, int c)
        {
            switch (kind)
            {
                case 0: return c == arg;
                case 1: return RegexCase.Canon(c, _re._unicode) == arg;
                case 2: return arg != 0 || !IsLineTerminator(c);
                default:
                {
                    var set = _re._sets[arg];
                    bool hit = (flags & 1) != 0 ? set.Contains(RegexCase.Canon(c, _re._unicode)) : set.Contains(c);
                    return hit != ((flags & 2) != 0);
                }
            }
        }

        private void Step()
        {
            if (++_steps >= 4096)
            {
                _steps = 0;
                _tick?.Invoke();
            }
        }

        /// <summary>Runs from <paramref name="pc"/> at <paramref name="pos"/>; returns the end position or -1.</summary>
        private int Run(int pc, int pos)
        {
            var code = _re._code;
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
                    case OpCharI:
                    {
                        int w = Read(pos, back, out int c);
                        if (w == 0) goto Fail;
                        if ((op & 0xFF) == OpCharI) c = RegexCase.Canon(c, _re._unicode);
                        if (c != code[pc + 1]) goto Fail;
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
                        int w = Read(pos, back, out int c);
                        if (w == 0 || !MatchOne(3, code[pc + 1], code[pc + 2], c)) goto Fail;
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
                        if (!(pos == 0 || (code[pc + 1] != 0 && IsLineTerminator(_s[pos - 1])))) goto Fail;
                        pc += 2;
                        continue;
                    case OpEol:
                        if (!(pos == _s.Length || (code[pc + 1] != 0 && IsLineTerminator(_s[pos])))) goto Fail;
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
                            if (pos + len > _s.Length || !SubstringEquals(gs, pos, len, ic)) goto Fail;
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
                        int next = pc + 7;
                        int count = 0;
                        while (count < min)
                        {
                            int w = Read(pos, back, out int c);
                            if (w == 0 || !MatchOne(kind, arg, flags, c)) goto Fail;
                            pos += back ? -w : w;
                            count++;
                            Step();
                        }
                        if (greedy)
                        {
                            while (max == -1 || count < max)
                            {
                                int w = Read(pos, back, out int c);
                                if (w == 0 || !MatchOne(kind, arg, flags, c)) break;
                                pos += back ? -w : w;
                                count++;
                                Step();
                            }
                            if (count > min) PushChoice(1, next, pos, count - min, pc);
                        }
                        else if (max == -1 || count < max) PushChoice(2, next, pos, count, pc);
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
                    if (ch.Kind == 1)
                    {
                        // give back one character
                        int w = Read(ch.Pos, !lback, out _);
                        ch.Pos += lback ? w : -w;
                        pos = ch.Pos;
                        pc = ch.Pc;
                        if (--ch.Count == 0) _choiceCount--;
                        break;
                    }
                    else
                    {
                        // lazy: take one more character
                        int kind = code[ch.LoopPc + 1], arg = code[ch.LoopPc + 2], flags = code[ch.LoopPc + 3], max = code[ch.LoopPc + 5];
                        int w = Read(ch.Pos, lback, out int c);
                        if (w == 0 || !MatchOne(kind, arg, flags, c)) { _choiceCount--; continue; }
                        ch.Pos += lback ? -w : w;
                        ch.Count++;
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
