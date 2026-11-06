using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Jaunt.Runtime.Regex;

internal sealed class RegexSyntaxException : Exception
{
    public RegexSyntaxException(string message) : base(message) { }
}

// ---------------------------------------------------------------------- pattern tree

internal abstract class RNode { }
internal sealed class RSeq : RNode { public readonly List<RNode> Items = new(); }
internal sealed class RAlt : RNode { public readonly List<RNode> Alts = new(); }
internal sealed class REmpty : RNode { }
internal sealed class RChar : RNode { public int C; public bool IgnoreCase; }
internal sealed class RAny : RNode { public bool DotAll; }
internal sealed class RSet : RNode { public required CharSet Set; public bool Negate; public bool IgnoreCase; public bool Canonical; }
internal sealed class RStrings : RNode { public required CharSet Chars; public required List<string> Strings; public bool IgnoreCase; }
internal sealed class RGroup : RNode { public required RNode Body; public int Index; }
internal sealed class RBackRef : RNode { public required int[] Groups; public bool IgnoreCase; }
internal sealed class RLook : RNode { public required RNode Body; public bool Behind; public bool Negate; }
internal sealed class RRepeat : RNode { public required RNode Body; public int Min; public int Max; public bool Greedy; public int CapFrom; public int CapTo; }
internal enum AssertKind { Bol, Eol, WordB, NotWordB }
internal sealed class RAssert : RNode { public AssertKind Kind; public bool Multiline; public bool UnicodeIgnoreCase; }

/// <summary>v-mode class value: code points plus strings (ClassSetExpression semantics).</summary>
internal sealed class VClass
{
    public CharSet Chars = CharSet.Empty;
    public HashSet<string> Strings = new(StringComparer.Ordinal);
}

/// <summary>ECMAScript 2025 RegExp pattern parser (22.2.1), including Annex B syntax for non-unicode patterns.</summary>
internal sealed class RegexParser
{
    private readonly string _p;
    private int _i;
    private readonly bool _unicode;      // u or v
    private readonly bool _unicodeSets;  // v
    private bool _ignoreCase, _multiline, _dotAll;
    private readonly bool _hasNamedGroups;
    private readonly int _totalGroups;
    private int _groupCount;             // captures assigned so far (group 0 excluded)
    public readonly List<string?> GroupNames = new() { null };
    private readonly List<(int at, string name)> _pendingNamedRefs = new();

    public int CaptureCount => _groupCount + 1;
    /// <summary>The pattern contains modifier groups or non-ASCII group names.</summary>
    public bool NeedsNative;

    public RegexParser(string pattern, string flags)
    {
        _p = pattern;
        _unicodeSets = flags.Contains('v');
        _unicode = _unicodeSets || flags.Contains('u');
        _ignoreCase = flags.Contains('i');
        _multiline = flags.Contains('m');
        _dotAll = flags.Contains('s');
        (_totalGroups, _hasNamedGroups) = Prescan();
    }

    private RegexSyntaxException Error(string msg) => new($"Invalid regular expression: /{_p}/: {msg}");

    public RNode Parse()
    {
        var n = ParseDisjunction();
        if (_i < _p.Length) throw Error(_p[_i] == ')' ? "Unmatched ')'" : "Unexpected character");
        return n;
    }

    /// <summary>Counts capturing groups and detects named groups (skipping escapes and classes).</summary>
    private (int count, bool named) Prescan()
    {
        int count = 0;
        bool named = false;
        int depth = 0;
        for (int i = 0; i < _p.Length; i++)
        {
            char c = _p[i];
            if (c == '\\') { i++; continue; }
            if (c == '[') { depth++; continue; }
            if (c == ']') { if (depth > 0) depth--; continue; }
            if (depth > 0) continue;
            if (c != '(') continue;
            if (i + 1 < _p.Length && _p[i + 1] == '?')
            {
                if (i + 2 < _p.Length && _p[i + 2] == '<' && i + 3 < _p.Length && _p[i + 3] != '=' && _p[i + 3] != '!')
                {
                    count++;
                    named = true;
                }
                continue;
            }
            count++;
        }
        return (count, named);
    }

    private bool More => _i < _p.Length;
    private char Peek(int o = 0) => _i + o < _p.Length ? _p[_i + o] : '\0';
    private bool Eat(char c) { if (More && _p[_i] == c) { _i++; return true; } return false; }

    private int ReadPatternChar()
    {
        char c = _p[_i++];
        if (_unicode && char.IsHighSurrogate(c) && More && char.IsLowSurrogate(_p[_i])) return char.ConvertToUtf32(c, _p[_i++]);
        return c;
    }

    private RNode ParseDisjunction()
    {
        var first = ParseAlternative();
        if (!(More && Peek() == '|')) return first;
        var alt = new RAlt();
        alt.Alts.Add(first);
        while (Eat('|')) alt.Alts.Add(ParseAlternative());
        return alt;
    }

    private RNode ParseAlternative()
    {
        var seq = new RSeq();
        while (More && Peek() != '|' && Peek() != ')')
        {
            var t = ParseTerm();
            if (t != null) seq.Items.Add(t);
        }
        return seq.Items.Count == 1 ? seq.Items[0] : seq;
    }

    private RNode? ParseTerm()
    {
        char c = Peek();
        int capBefore = _groupCount;
        RNode atom;
        bool quantifiable = true;
        switch (c)
        {
            case '^':
                _i++;
                return new RAssert { Kind = AssertKind.Bol, Multiline = _multiline };
            case '$':
                _i++;
                return new RAssert { Kind = AssertKind.Eol, Multiline = _multiline };
            case '\\' when Peek(1) == 'b' || Peek(1) == 'B':
            {
                bool neg = Peek(1) == 'B';
                _i += 2;
                return new RAssert { Kind = neg ? AssertKind.NotWordB : AssertKind.WordB, UnicodeIgnoreCase = _unicode && _ignoreCase };
            }
            case '(':
                atom = ParseGroup(out quantifiable);
                break;
            case '.':
                _i++;
                atom = new RAny { DotAll = _dotAll };
                break;
            case '[':
                atom = ParseClass();
                break;
            case '\\':
                atom = ParseAtomEscape();
                break;
            case '*' or '+' or '?':
                throw Error("Nothing to repeat");
            case '{':
                if (_unicode || TryQuantifierBraces(out _, out _, peekOnly: true)) throw Error("Nothing to repeat");
                _i++;
                atom = Lit('{');
                break;
            default:
                atom = Lit(ReadPatternChar());
                break;
        }
        if (!quantifiable) return atom;
        return ParseQuantifier(atom, capBefore);
    }

    private RChar Lit(int c) => new() { C = c, IgnoreCase = _ignoreCase };

    private RNode ParseQuantifier(RNode atom, int capBefore)
    {
        if (!More) return atom;
        int min, max;
        switch (Peek())
        {
            case '*': _i++; min = 0; max = -1; break;
            case '+': _i++; min = 1; max = -1; break;
            case '?': _i++; min = 0; max = 1; break;
            case '{':
                if (!TryQuantifierBraces(out min, out max, peekOnly: false))
                {
                    if (_unicode) throw Error("Incomplete quantifier");
                    return atom;
                }
                break;
            default:
                return atom;
        }
        bool greedy = !Eat('?');
        if (max != -1 && min > max) throw Error("numbers out of order in {} quantifier");
        return new RRepeat { Body = atom, Min = min, Max = max, Greedy = greedy, CapFrom = capBefore + 1, CapTo = _groupCount + 1 };
    }

    private bool TryQuantifierBraces(out int min, out int max, bool peekOnly)
    {
        int save = _i;
        min = max = 0;
        if (!Eat('{')) { _i = save; return false; }
        if (!TryDigits(out min)) { _i = save; return false; }
        if (Eat('}')) { max = min; if (peekOnly) _i = save; return true; }
        if (!Eat(',')) { _i = save; return false; }
        if (Eat('}')) { max = -1; if (peekOnly) _i = save; return true; }
        if (!TryDigits(out max)) { _i = save; return false; }
        if (!Eat('}')) { _i = save; return false; }
        if (peekOnly) _i = save;
        return true;
    }

    private bool TryDigits(out int value)
    {
        value = 0;
        int start = _i;
        long v = 0;
        while (More && char.IsAsciiDigit(Peek())) { v = Math.Min(v * 10 + (Peek() - '0'), int.MaxValue); _i++; }
        value = (int)v;
        return _i > start;
    }

    private RNode ParseGroup(out bool quantifiable)
    {
        quantifiable = true;
        _i++; // (
        if (Eat('?'))
        {
            char k = Peek();
            if (k == '=' || k == '!')
            {
                _i++;
                var body = ParseDisjunction();
                Expect(')');
                quantifiable = !_unicode; // Annex B: QuantifiableAssertion
                return new RLook { Body = body, Negate = k == '!' };
            }
            if (k == '<' && (Peek(1) == '=' || Peek(1) == '!'))
            {
                bool neg = Peek(1) == '!';
                _i += 2;
                var body = ParseDisjunction();
                Expect(')');
                quantifiable = false;
                return new RLook { Body = body, Behind = true, Negate = neg };
            }
            if (k == ':')
            {
                _i++;
                var body = ParseDisjunction();
                Expect(')');
                return body;
            }
            if (k == '<')
            {
                _i++;
                var name = ParseGroupName();
                int index = ++_groupCount;
                GroupNames.Add(name);
                var body = ParseDisjunction();
                Expect(')');
                return new RGroup { Body = body, Index = index };
            }
            // modifiers: (?ims-ims:...)
            NeedsNative = true;
            bool add = true;
            bool? ic = null, ml = null, da = null;
            while (More && Peek() != ':')
            {
                char f = _p[_i++];
                switch (f)
                {
                    case '-': add = false; break;
                    case 'i': ic = add; break;
                    case 'm': ml = add; break;
                    case 's': da = add; break;
                    default: throw Error("Invalid group");
                }
            }
            Expect(':');
            var saved = (_ignoreCase, _multiline, _dotAll);
            if (ic.HasValue) _ignoreCase = ic.Value;
            if (ml.HasValue) _multiline = ml.Value;
            if (da.HasValue) _dotAll = da.Value;
            var mbody = ParseDisjunction();
            (_ignoreCase, _multiline, _dotAll) = saved;
            Expect(')');
            return mbody;
        }
        int idx = ++_groupCount;
        GroupNames.Add(null);
        var b = ParseDisjunction();
        Expect(')');
        return new RGroup { Body = b, Index = idx };
    }

    private void Expect(char c)
    {
        if (!Eat(c)) throw Error($"Expected '{c}'");
    }

    private string ParseGroupName()
    {
        var sb = new StringBuilder();
        while (More && Peek() != '>')
        {
            if (Peek() == '\\')
            {
                _i++;
                if (!Eat('u')) throw Error("Invalid capture group name");
                int cp = ParseUnicodeEscapeBody(forceUnicode: true);
                sb.Append(char.ConvertFromUtf32(cp));
                continue;
            }
            sb.Append(_p[_i++]);
        }
        Expect('>');
        if (sb.Length == 0) throw Error("Invalid capture group name");
        foreach (var ch in sb.ToString()) if (!(char.IsAsciiLetterOrDigit(ch) || ch == '_')) NeedsNative = true;
        return sb.ToString();
    }

    // ------------------------------------------------------------------ escapes

    private RNode ParseAtomEscape()
    {
        _i++; // backslash
        if (!More) throw Error("\\ at end of pattern");
        char c = Peek();
        if (c >= '1' && c <= '9')
        {
            int save = _i;
            TryDigits(out int n);
            if (n <= _totalGroups) return new RBackRef { Groups = new[] { n }, IgnoreCase = _ignoreCase };
            if (_unicode) throw Error("Invalid escape");
            _i = save;
            if (c >= '8') { _i++; return Lit(c); }
            return Lit(ParseLegacyOctal());
        }
        if (c == 'k' && (_unicode || _hasNamedGroups))
        {
            _i++;
            if (!Eat('<')) throw Error("Invalid named reference");
            var name = ParseGroupName();
            var groups = new List<int>();
            // all groups with that name (duplicate names live in different alternatives); resolved after parsing
            return new RNamedRef(this, name, _ignoreCase);
        }
        if (TryClassEscape(out var set)) return set;
        int ch = ParseCharacterEscape(inClass: false);
        return Lit(ch);
    }

    /// <summary>\k&lt;name&gt; placeholder resolved once all groups are known.</summary>
    private sealed class RNamedRef : RNode
    {
        public readonly RegexParser Parser;
        public readonly string Name;
        public readonly bool IgnoreCase;
        public RNamedRef(RegexParser p, string name, bool ic) { Parser = p; Name = name; IgnoreCase = ic; }
    }

    public static RNode Resolve(RNode n, RegexParser p)
    {
        switch (n)
        {
            case RNamedRef nr:
            {
                var list = new List<int>();
                for (int g = 1; g < p.GroupNames.Count; g++) if (p.GroupNames[g] == nr.Name) list.Add(g);
                if (list.Count == 0) throw p.Error("Invalid named capture referenced");
                return new RBackRef { Groups = list.ToArray(), IgnoreCase = nr.IgnoreCase };
            }
            case RSeq s:
                for (int i = 0; i < s.Items.Count; i++) s.Items[i] = Resolve(s.Items[i], p);
                return s;
            case RAlt a:
                for (int i = 0; i < a.Alts.Count; i++) a.Alts[i] = Resolve(a.Alts[i], p);
                return a;
            case RGroup g: g.Body = Resolve(g.Body, p); return g;
            case RLook l: l.Body = Resolve(l.Body, p); return l;
            case RRepeat r: r.Body = Resolve(r.Body, p); return r;
            default: return n;
        }
    }

    private int ParseLegacyOctal()
    {
        // LegacyOctalEscapeSequence: up to three octal digits, value <= 0o377
        int v = Peek() - '0';
        _i++;
        if (More && Peek() >= '0' && Peek() <= '7')
        {
            v = v * 8 + (Peek() - '0');
            _i++;
            if (v < 32 && More && Peek() >= '0' && Peek() <= '7') { v = v * 8 + (Peek() - '0'); _i++; }
        }
        return v;
    }

    private static readonly CharSet s_digits = CharSet.Range('0', '9');
    private static readonly CharSet s_word = CharSet.FromRanges(new List<(int, int)> { ('0', '9'), ('A', 'Z'), ('_', '_'), ('a', 'z') });
    private static readonly CharSet s_wordUnicodeIgnoreCase = s_word.Union(CharSet.Single(0x17F)).Union(CharSet.Single(0x212A));
    private static readonly CharSet s_space = CharSet.FromRanges(new List<(int, int)>
    {
        (9, 13), (0x20, 0x20), (0xA0, 0xA0), (0x1680, 0x1680), (0x2000, 0x200A), (0x2028, 0x2029), (0x202F, 0x202F),
        (0x205F, 0x205F), (0x3000, 0x3000), (0xFEFF, 0xFEFF),
    });

    private int MaxChar => _unicode ? 0x10FFFF : 0xFFFF;

    /// <summary>CharacterClassEscape (\d \D \s \S \w \W \p \P). Returns false when the escape is not a class escape.</summary>
    private bool TryClassEscape(out RNode node)
    {
        node = null!;
        if (!TryClassEscapeSet(out var vc)) return false;
        if (vc.Strings.Count > 0)
        {
            node = MakeStringsNode(vc);
            return true;
        }
        node = new RSet { Set = vc.Chars, IgnoreCase = _ignoreCase, Canonical = _unicodeSets && _ignoreCase };
        return true;
    }

    private bool TryClassEscapeSet(out VClass vc)
    {
        vc = new VClass();
        char c = Peek();
        CharSet set;
        switch (c)
        {
            case 'd': set = s_digits; break;
            case 'D': set = Complement(s_digits); break;
            case 's': set = s_space; break;
            case 'S': set = Complement(s_space); break;
            case 'w': set = WordSet; break;
            case 'W': set = Complement(WordSet); break;
            case 'p' or 'P' when _unicode:
            {
                _i++;
                bool neg = c == 'P';
                if (!Eat('{')) throw Error("Invalid property name");
                int start = _i;
                while (More && Peek() != '}') _i++;
                var body = _p.Substring(start, _i - start);
                Expect('}');
                string name = body;
                string? value = null;
                int eq = body.IndexOf('=');
                if (eq >= 0) { name = body[..eq]; value = body[(eq + 1)..]; }
                if (UnicodeData.TryGetProperty(name, value, out var ranges))
                {
                    var ps = new CharSet(ranges);
                    if (_unicodeSets && _ignoreCase) ps = RegexCase.FoldCanonical(ps);
                    vc.Chars = neg ? Complement(ps) : ps;
                    return true;
                }
                if (_unicodeSets && value == null && !neg && UnicodeData.TryGetStringProperty(name, out var strings))
                {
                    var singles = new List<(int, int)>();
                    foreach (var s in strings)
                    {
                        var f = _ignoreCase ? RegexCase.FoldString(s) : s;
                        if (IsSingleCodePoint(f, out int cp)) singles.Add((cp, cp));
                        else vc.Strings.Add(f);
                    }
                    vc.Chars = CharSet.FromRanges(singles);
                    return true;
                }
                throw Error("Invalid property name");
            }
            default:
                return false;
        }
        _i++;
        if (_unicodeSets && _ignoreCase) set = RegexCase.FoldCanonical(set);
        vc.Chars = set;
        return true;
    }

    private CharSet WordSet => _unicode && _ignoreCase ? s_wordUnicodeIgnoreCase : s_word;

    /// <summary>CharacterComplement: relative to all characters (v+i: all canonical characters).</summary>
    private CharSet Complement(CharSet s)
    {
        if (_unicodeSets && _ignoreCase) return RegexCase.NonCanonical.Union(RegexCase.FoldCanonical(s)).Complement(MaxChar);
        return s.Complement(MaxChar);
    }

    private static bool IsSingleCodePoint(string s, out int cp)
    {
        cp = 0;
        if (s.Length == 1) { cp = s[0]; return true; }
        if (s.Length == 2 && char.IsHighSurrogate(s[0]) && char.IsLowSurrogate(s[1])) { cp = char.ConvertToUtf32(s[0], s[1]); return true; }
        return false;
    }

    /// <summary>CharacterEscape (and ClassEscape-only forms when <paramref name="inClass"/>). Backslash already consumed.</summary>
    private int ParseCharacterEscape(bool inClass)
    {
        char c = _p[_i++];
        switch (c)
        {
            case 't': return 9;
            case 'n': return 10;
            case 'v': return 11;
            case 'f': return 12;
            case 'r': return 13;
            case 'c':
                if (More && char.IsAsciiLetter(Peek())) return _p[_i++] % 32;
                if (!_unicode && inClass && More && (char.IsAsciiDigit(Peek()) || Peek() == '_')) return _p[_i++] % 32;
                if (_unicode) throw Error("Invalid unicode escape");
                _i--; // Annex B: "\c" is a literal backslash; 'c' is parsed next
                return '\\';
            case '0' when !(More && char.IsAsciiDigit(Peek())):
                return 0;
            case 'x':
            {
                if (TryHex(2, out int v)) return v;
                if (_unicode) throw Error("Invalid escape");
                return 'x';
            }
            case 'u':
            {
                int save = _i;
                if (TryParseUnicodeEscapeBody(out int cp)) return cp;
                if (_unicode) throw Error("Invalid unicode escape");
                _i = save;
                return 'u';
            }
            default:
                if (!_unicode && c >= '0' && c <= '7') { _i--; return ParseLegacyOctal(); }
                if (_unicode && char.IsHighSurrogate(c) && More && char.IsLowSurrogate(Peek())) return char.ConvertToUtf32(c, _p[_i++]);
                return c;
        }
    }

    private bool TryHex(int n, out int v)
    {
        v = 0;
        if (_i + n > _p.Length) return false;
        for (int k = 0; k < n; k++)
        {
            int d = HexVal(_p[_i + k]);
            if (d < 0) return false;
            v = v * 16 + d;
        }
        _i += n;
        return true;
    }

    private static int HexVal(char c) => c >= '0' && c <= '9' ? c - '0' : (c | 0x20) >= 'a' && (c | 0x20) <= 'f' ? (c | 0x20) - 'a' + 10 : -1;

    private bool TryParseUnicodeEscapeBody(out int cp)
    {
        cp = 0;
        if (_unicode && Peek() == '{')
        {
            int save = _i;
            _i++;
            long v = 0;
            int digits = 0;
            while (More && HexVal(Peek()) >= 0) { v = v * 16 + HexVal(_p[_i++]); digits++; if (v > 0x10FFFF) break; }
            if (digits == 0 || v > 0x10FFFF || !Eat('}')) { _i = save; return false; }
            cp = (int)v;
            return true;
        }
        if (!TryHex(4, out int u)) return false;
        cp = u;
        // \uLead\uTrail forms one code point in unicode mode
        if (_unicode && char.IsHighSurrogate((char)u) && Peek() == '\\' && Peek(1) == 'u')
        {
            int save = _i;
            _i += 2;
            if (TryHex(4, out int t) && char.IsLowSurrogate((char)t)) cp = char.ConvertToUtf32((char)u, (char)t);
            else _i = save;
        }
        return true;
    }

    private int ParseUnicodeEscapeBody(bool forceUnicode)
    {
        if (Peek() == '{')
        {
            _i++;
            long v = 0;
            while (More && HexVal(Peek()) >= 0) { v = v * 16 + HexVal(_p[_i++]); if (v > 0x10FFFF) throw Error("Invalid unicode escape"); }
            Expect('}');
            return (int)v;
        }
        if (!TryHex(4, out int u)) throw Error("Invalid unicode escape");
        if (char.IsHighSurrogate((char)u) && Peek() == '\\' && Peek(1) == 'u')
        {
            int save = _i;
            _i += 2;
            if (TryHex(4, out int t) && char.IsLowSurrogate((char)t)) return char.ConvertToUtf32((char)u, (char)t);
            _i = save;
        }
        return u;
    }

    // ------------------------------------------------------------------ character classes

    private RNode ParseClass()
    {
        _i++; // [
        if (_unicodeSets)
        {
            bool negV = Eat('^');
            var vc = ParseClassSetExpression();
            Expect(']');
            if (negV)
            {
                if (vc.Strings.Count > 0) throw Error("Negated character class may contain strings");
                return new RSet { Set = Complement(vc.Chars), IgnoreCase = _ignoreCase, Canonical = _ignoreCase };
            }
            if (vc.Strings.Count > 0) return MakeStringsNode(vc);
            return new RSet { Set = vc.Chars, IgnoreCase = _ignoreCase, Canonical = _ignoreCase };
        }
        bool neg = Eat('^');
        var ranges = new List<(int, int)>();
        CharSet extra = CharSet.Empty;
        while (More && Peek() != ']')
        {
            var (a, aSet) = ParseClassAtom();
            if (Peek() == '-' && Peek(1) != ']' && _i + 1 < _p.Length)
            {
                int save = _i;
                _i++;
                var (b, bSet) = ParseClassAtom();
                if (aSet == null && bSet == null)
                {
                    if (a > b) throw Error("Range out of order in character class");
                    ranges.Add((a, b));
                    continue;
                }
                if (_unicode) throw Error("Invalid character class");
                // Annex B: a class escape in a range makes '-' literal
                if (aSet != null) extra = extra.Union(aSet); else ranges.Add((a, a));
                ranges.Add(('-', '-'));
                if (bSet != null) extra = extra.Union(bSet); else ranges.Add((b, b));
                continue;
            }
            if (aSet != null) extra = extra.Union(aSet);
            else ranges.Add((a, a));
        }
        Expect(']');
        var set = CharSet.FromRanges(ranges).Union(extra);
        return new RSet { Set = set, Negate = neg, IgnoreCase = _ignoreCase };
    }

    /// <summary>ClassAtom for non-v classes: a single character or a class escape set.</summary>
    private (int ch, CharSet? set) ParseClassAtom()
    {
        if (Peek() != '\\') return (ReadPatternChar(), null);
        _i++;
        if (!More) throw Error("\\ at end of pattern");
        char c = Peek();
        if (c == 'b') { _i++; return (8, null); }
        if (_unicode && c == '-') { _i++; return ('-', null); }
        if (TryClassEscapeSet(out var vc)) return (0, vc.Chars);
        if (!_unicode && c >= '1' && c <= '9')
        {
            if (c >= '8') { _i++; return (c, null); }
            return (ParseLegacyOctal(), null);
        }
        return (ParseCharacterEscape(inClass: true), null);
    }

    private RNode MakeStringsNode(VClass vc)
    {
        var list = new List<string>(vc.Strings);
        // longest strings first (by code point count)
        list.Sort((x, y) => CodePointLength(y).CompareTo(CodePointLength(x)));
        return new RStrings { Chars = vc.Chars, Strings = list, IgnoreCase = _ignoreCase };
    }

    private static int CodePointLength(string s)
    {
        int n = 0;
        for (int i = 0; i < s.Length; i++) { if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++; n++; }
        return n;
    }

    // v-mode: ClassSetExpression (22.2.1 ClassContents[+UnicodeSetsMode])
    private VClass ParseClassSetExpression()
    {
        if (Peek() == ']') return new VClass();
        var first = ParseClassSetOperand(out bool firstIsChar, out int firstChar);
        if (Peek() == '&' && Peek(1) == '&')
        {
            var acc = first;
            while (Peek() == '&' && Peek(1) == '&')
            {
                _i += 2;
                var o = ParseClassSetOperand(out _, out _);
                acc = new VClass { Chars = acc.Chars.Intersect(o.Chars), Strings = Intersect(acc.Strings, o.Strings) };
            }
            return acc;
        }
        if (Peek() == '-' && Peek(1) == '-')
        {
            var acc = first;
            while (Peek() == '-' && Peek(1) == '-')
            {
                _i += 2;
                var o = ParseClassSetOperand(out _, out _);
                var strings = new HashSet<string>(acc.Strings, StringComparer.Ordinal);
                strings.ExceptWith(o.Strings);
                acc = new VClass { Chars = acc.Chars.Subtract(o.Chars), Strings = strings };
            }
            return acc;
        }
        // ClassUnion
        var union = new VClass();
        var pendingRanges = new List<(int, int)>();
        bool haveFirst = true;
        VClass operand = first;
        bool isChar = firstIsChar;
        int ch = firstChar;
        while (true)
        {
            if (haveFirst) haveFirst = false;
            else
            {
                if (Peek() == ']' || !More) break;
                operand = ParseClassSetOperand(out isChar, out ch);
            }
            if (isChar && Peek() == '-' && Peek(1) != '-')
            {
                _i++;
                ParseClassSetOperand(out bool isChar2, out int ch2);
                if (!isChar2) throw Error("Invalid character class");
                if (ch > ch2) throw Error("Range out of order in character class");
                var r = CharSet.Range(ch, ch2);
                if (_ignoreCase) r = RegexCase.FoldCanonical(r);
                union.Chars = union.Chars.Union(r);
                continue;
            }
            union.Chars = union.Chars.Union(operand.Chars);
            union.Strings.UnionWith(operand.Strings);
        }
        return union;
    }

    private static HashSet<string> Intersect(HashSet<string> a, HashSet<string> b)
    {
        var r = new HashSet<string>(a, StringComparer.Ordinal);
        r.IntersectWith(b);
        return r;
    }

    private VClass ParseClassSetOperand(out bool isChar, out int ch)
    {
        isChar = false;
        ch = 0;
        if (Peek() == '[')
        {
            _i++;
            bool neg = Eat('^');
            var inner = ParseClassSetExpression();
            Expect(']');
            if (neg)
            {
                if (inner.Strings.Count > 0) throw Error("Negated character class may contain strings");
                return new VClass { Chars = Complement(inner.Chars) };
            }
            return inner;
        }
        if (Peek() == '\\')
        {
            _i++;
            if (Peek() == 'q' && Peek(1) == '{')
            {
                _i += 2;
                var vc = new VClass();
                var singles = new List<(int, int)>();
                var sb = new StringBuilder();
                while (true)
                {
                    if (!More) throw Error("Invalid escape");
                    if (Peek() == '}' || Peek() == '|')
                    {
                        var s = _ignoreCase ? RegexCase.FoldString(sb.ToString()) : sb.ToString();
                        if (IsSingleCodePoint(s, out int cp)) singles.Add((cp, cp));
                        else vc.Strings.Add(s);
                        sb.Clear();
                        if (_p[_i++] == '}') break;
                        continue;
                    }
                    int c;
                    if (Peek() == '\\') { _i++; c = ParseClassSetCharacterEscape(); }
                    else c = ReadPatternChar();
                    sb.Append(char.ConvertFromUtf32(c));
                }
                vc.Chars = CharSet.FromRanges(singles);
                return vc;
            }
            if (TryClassEscapeSet(out var esc)) return esc;
            isChar = true;
            ch = ParseClassSetCharacterEscape();
        }
        else
        {
            isChar = true;
            ch = ReadPatternChar();
        }
        var set = CharSet.Single(ch);
        if (_ignoreCase) set = RegexCase.FoldCanonical(set);
        return new VClass { Chars = set };
    }

    private int ParseClassSetCharacterEscape()
    {
        char c = Peek();
        if (c == 'b') { _i++; return 8; }
        if ("&-!#%,:;<=>@`~".IndexOf(c) >= 0) { _i++; return c; }
        return ParseCharacterEscape(inClass: true);
    }
}
