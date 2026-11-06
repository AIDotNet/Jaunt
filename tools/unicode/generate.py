#!/usr/bin/env python3
"""Generates src/Jaunt/Runtime/Regex/UnicodeData.g.cs from the Unicode Character Database.

Provides the data needed by the RegExp engine:
  * \\p{...} property escapes (General_Category, Script, Script_Extensions and the
    ECMAScript binary properties),
  * v-flag properties of strings (emoji sequences),
  * simple case folding (CaseFolding.txt statuses C and S).

Usage: python3 tools/unicode/generate.py
Downloads are cached in tools/unicode/.cache/. Standard library only.
"""

import os
import subprocess
import sys
import urllib.error
import urllib.request

UNICODE_VERSION = "17.0.0"
UCD = f"https://www.unicode.org/Public/{UNICODE_VERSION}/ucd/"
EMOJI_SEQ = f"https://www.unicode.org/Public/{UNICODE_VERSION}/emoji/"

FILES = {
    "DerivedGeneralCategory.txt": UCD + "extracted/DerivedGeneralCategory.txt",
    "Scripts.txt": UCD + "Scripts.txt",
    "ScriptExtensions.txt": UCD + "ScriptExtensions.txt",
    "PropList.txt": UCD + "PropList.txt",
    "DerivedCoreProperties.txt": UCD + "DerivedCoreProperties.txt",
    "DerivedNormalizationProps.txt": UCD + "DerivedNormalizationProps.txt",
    "DerivedBinaryProperties.txt": UCD + "extracted/DerivedBinaryProperties.txt",
    "emoji-data.txt": UCD + "emoji/emoji-data.txt",
    "PropertyValueAliases.txt": UCD + "PropertyValueAliases.txt",
    "PropertyAliases.txt": UCD + "PropertyAliases.txt",
    "CaseFolding.txt": UCD + "CaseFolding.txt",
    "emoji-sequences.txt": EMOJI_SEQ + "emoji-sequences.txt",
    "emoji-zwj-sequences.txt": EMOJI_SEQ + "emoji-zwj-sequences.txt",
}

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CACHE = os.path.join(os.path.dirname(os.path.abspath(__file__)), ".cache")
OUT = os.path.join(ROOT, "src", "Jaunt", "Runtime", "Regex", "UnicodeData.g.cs")

MAX_CP = 0x10FFFF

# ECMAScript table-binary-unicode-properties: canonical name -> aliases accepted.
BINARY_PROPERTIES = [
    ("ASCII", []), ("ASCII_Hex_Digit", ["AHex"]), ("Alphabetic", ["Alpha"]), ("Any", []),
    ("Assigned", []), ("Bidi_Control", ["Bidi_C"]), ("Bidi_Mirrored", ["Bidi_M"]),
    ("Case_Ignorable", ["CI"]), ("Cased", []), ("Changes_When_Casefolded", ["CWCF"]),
    ("Changes_When_Casemapped", ["CWCM"]), ("Changes_When_Lowercased", ["CWL"]),
    ("Changes_When_NFKC_Casefolded", ["CWKCF"]), ("Changes_When_Titlecased", ["CWT"]),
    ("Changes_When_Uppercased", ["CWU"]), ("Dash", []), ("Default_Ignorable_Code_Point", ["DI"]),
    ("Deprecated", ["Dep"]), ("Diacritic", ["Dia"]), ("Emoji", []), ("Emoji_Component", ["EComp"]),
    ("Emoji_Modifier", ["EMod"]), ("Emoji_Modifier_Base", ["EBase"]),
    ("Emoji_Presentation", ["EPres"]), ("Extended_Pictographic", ["ExtPict"]),
    ("Extender", ["Ext"]), ("Grapheme_Base", ["Gr_Base"]), ("Grapheme_Extend", ["Gr_Ext"]),
    ("Hex_Digit", ["Hex"]), ("IDS_Binary_Operator", ["IDSB"]), ("IDS_Trinary_Operator", ["IDST"]),
    ("ID_Continue", ["IDC"]), ("ID_Start", ["IDS"]), ("Ideographic", ["Ideo"]),
    ("Join_Control", ["Join_C"]), ("Logical_Order_Exception", ["LOE"]), ("Lowercase", ["Lower"]),
    ("Math", []), ("Noncharacter_Code_Point", ["NChar"]), ("Pattern_Syntax", ["Pat_Syn"]),
    ("Pattern_White_Space", ["Pat_WS"]), ("Quotation_Mark", ["QMark"]), ("Radical", []),
    ("Regional_Indicator", ["RI"]), ("Sentence_Terminal", ["STerm"]), ("Soft_Dotted", ["SD"]),
    ("Terminal_Punctuation", ["Term"]), ("Unified_Ideograph", ["UIdeo"]), ("Uppercase", ["Upper"]),
    ("Variation_Selector", ["VS"]), ("White_Space", ["space"]), ("XID_Continue", ["XIDC"]),
    ("XID_Start", ["XIDS"]),
]

GC_GROUPS = {
    "L": ["Lu", "Ll", "Lt", "Lm", "Lo"],
    "LC": ["Lu", "Ll", "Lt"],
    "M": ["Mn", "Mc", "Me"],
    "N": ["Nd", "Nl", "No"],
    "P": ["Pc", "Pd", "Ps", "Pe", "Pi", "Pf", "Po"],
    "S": ["Sm", "Sc", "Sk", "So"],
    "Z": ["Zs", "Zl", "Zp"],
    "C": ["Cc", "Cf", "Cs", "Co", "Cn"],
}

STRING_PROPERTIES = [
    "Basic_Emoji", "Emoji_Keycap_Sequence", "RGI_Emoji_Modifier_Sequence",
    "RGI_Emoji_Flag_Sequence", "RGI_Emoji_Tag_Sequence", "RGI_Emoji_ZWJ_Sequence",
]


def fetch(name):
    os.makedirs(CACHE, exist_ok=True)
    path = os.path.join(CACHE, name)
    if not os.path.exists(path):
        url = FILES[name]
        print(f"downloading {url}", file=sys.stderr)
        try:
            with urllib.request.urlopen(url) as resp:
                data = resp.read()
        except urllib.error.URLError as e:
            # Some Python installs (e.g. python.org builds on macOS) lack a CA bundle; fall back to curl.
            print(f"urllib failed ({e.reason}); retrying with curl", file=sys.stderr)
            data = subprocess.run(["curl", "-fsSL", url], check=True, stdout=subprocess.PIPE).stdout
        with open(path + ".tmp", "wb") as f:
            f.write(data)
        os.replace(path + ".tmp", path)
    with open(path, "r", encoding="utf-8") as f:
        text = f.read()
    return text


def data_lines(name):
    for line in fetch(name).splitlines():
        line = line.split("#", 1)[0].strip()
        if line:
            yield [field.strip() for field in line.split(";")]


def parse_cp_range(s):
    if ".." in s:
        a, b = s.split("..")
        return int(a, 16), int(b, 16)
    v = int(s, 16)
    return v, v


def normalize(ranges):
    """Sorts and merges (adjacent or overlapping) inclusive ranges."""
    out = []
    for a, b in sorted(ranges):
        if out and a <= out[-1][1] + 1:
            if b > out[-1][1]:
                out[-1][1] = b
        else:
            out.append([a, b])
    return [(a, b) for a, b in out]


def complement(ranges):
    out = []
    nxt = 0
    for a, b in normalize(ranges):
        if a > nxt:
            out.append((nxt, a - 1))
        nxt = b + 1
    if nxt <= MAX_CP:
        out.append((nxt, MAX_CP))
    return out


# --- compact number encoding -------------------------------------------------
# Each unsigned integer is written in base 32, most significant digit first.
# Non-final digits use CONT, the final digit uses FINAL; so numbers are self-delimiting.
CONT = "ABCDEFGHIJKLMNOPQRSTUVWXYZ012345"
FINAL = "abcdefghijklmnopqrstuvwxyz6789+/"
assert len(CONT) == 32 and len(FINAL) == 32


def enc_num(n):
    assert n >= 0
    digits = []
    while True:
        digits.append(n & 31)
        n >>= 5
        if n == 0:
            break
    digits.reverse()
    return "".join(CONT[d] for d in digits[:-1]) + FINAL[digits[-1]]


def zigzag(n):
    return n * 2 if n >= 0 else -n * 2 - 1


def enc_ranges(ranges):
    # First: count; then for each range: (start - prevEnd - 2) [or start for the first], (end - start).
    ranges = normalize(ranges)
    parts = [enc_num(len(ranges))]
    prev = None
    for a, b in ranges:
        parts.append(enc_num(a if prev is None else a - prev - 2))
        parts.append(enc_num(b - a))
        prev = b
    return "".join(parts)


def enc_strings(seqs):
    # count, then per string: length, code points as zigzag deltas from the previous code point.
    parts = [enc_num(len(seqs))]
    prev = 0
    for seq in seqs:
        parts.append(enc_num(len(seq)))
        for cp in seq:
            parts.append(enc_num(zigzag(cp - prev)))
            prev = cp
    return "".join(parts)


# --- data loading -------------------------------------------------------------

def load_gc():
    gc = {}
    for f in data_lines("DerivedGeneralCategory.txt"):
        gc.setdefault(f[1], []).append(parse_cp_range(f[0]))
    return {k: normalize(v) for k, v in gc.items()}


def load_value_aliases():
    """Returns {prop: [[short, long, extra...], ...]}."""
    result = {}
    for f in data_lines("PropertyValueAliases.txt"):
        result.setdefault(f[0], []).append(f[1:])
    return result


def load_binary_props():
    props = {}
    for name in ("PropList.txt", "DerivedCoreProperties.txt", "DerivedNormalizationProps.txt",
                 "DerivedBinaryProperties.txt", "emoji-data.txt"):
        for f in data_lines(name):
            if len(f) != 2:
                continue
            props.setdefault(f[1], []).append(parse_cp_range(f[0]))
    return props


def main():
    fetch("PropertyAliases.txt")  # downloaded for reference / completeness
    gc = load_gc()
    aliases = load_value_aliases()
    binprops = load_binary_props()

    tables = []        # encoded strings
    table_index = {}   # encoded -> index

    def add_table(ranges):
        e = enc_ranges(ranges)
        if e not in table_index:
            table_index[e] = len(tables)
            tables.append(e)
        return table_index[e]

    # General_Category
    gc_names = {}  # alias -> table index
    for vals in aliases["gc"]:
        short = vals[0]
        if short in GC_GROUPS:
            rs = [r for m in GC_GROUPS[short] for r in gc.get(m, [])]
        else:
            rs = gc.get(short, [])
        idx = add_table(rs)
        for v in vals:
            gc_names[v] = idx
    assert set(GC_GROUPS) | set(gc) <= {v[0] for v in aliases["gc"]}

    # Script
    sc_ranges = {}
    for f in data_lines("Scripts.txt"):
        sc_ranges.setdefault(f[1], []).append(parse_cp_range(f[0]))
    long_to_short = {}
    for vals in aliases["sc"]:
        long_to_short[vals[1]] = vals[0]
    sc_by_short = {}
    for long_name, rs in sc_ranges.items():
        sc_by_short[long_to_short[long_name]] = normalize(rs)
    sc_by_short["Zzzz"] = complement([r for rs in sc_by_short.values() for r in rs])

    # Script_Extensions
    cp_script = {}
    for short, rs in sc_by_short.items():
        if short == "Zzzz":
            continue
        for a, b in rs:
            for cp in range(a, b + 1):
                cp_script[cp] = short
    scx_override = {}
    for f in data_lines("ScriptExtensions.txt"):
        a, b = parse_cp_range(f[0])
        scripts = f[1].split()
        for cp in range(a, b + 1):
            scx_override[cp] = scripts
    scx_by_short = {s: [] for s in sc_by_short}
    for short, rs in sc_by_short.items():
        for a, b in rs:
            for cp in range(a, b + 1):
                if cp not in scx_override:
                    scx_by_short[short].append((cp, cp))
    for cp, scripts in scx_override.items():
        for s in scripts:
            scx_by_short.setdefault(s, []).append((cp, cp))

    sc_names = {}
    scx_names = {}
    for vals in aliases["sc"]:
        short = vals[0]
        i1 = add_table(sc_by_short.get(short, []))
        i2 = add_table(scx_by_short.get(short, []))
        for v in vals:
            sc_names[v] = i1
            scx_names[v] = i2

    # Binary properties
    bin_names = {}
    for name, als in BINARY_PROPERTIES:
        if name == "Any":
            rs = [(0, MAX_CP)]
        elif name == "ASCII":
            rs = [(0, 0x7F)]
        elif name == "Assigned":
            rs = complement(gc["Cn"])
        else:
            if name not in binprops:
                raise SystemExit(f"missing binary property {name}")
            rs = binprops[name]
        idx = add_table(rs)
        for n in [name] + als:
            bin_names[n] = idx

    # Properties of strings
    str_props = {p: [] for p in STRING_PROPERTIES}
    seen = {p: set() for p in STRING_PROPERTIES}
    for fname in ("emoji-sequences.txt", "emoji-zwj-sequences.txt"):
        for f in data_lines(fname):
            prop = f[1]
            if prop not in str_props:
                raise SystemExit(f"unknown emoji sequence type {prop}")
            if ".." in f[0]:
                a, b = parse_cp_range(f[0])
                seqs = [(cp,) for cp in range(a, b + 1)]
            else:
                seqs = [tuple(int(x, 16) for x in f[0].split())]
            for s in seqs:
                if s not in seen[prop]:
                    seen[prop].add(s)
                    str_props[prop].append(s)
    str_encoded = [enc_strings(str_props[p]) for p in STRING_PROPERTIES]

    # Case folding (C + S)
    folds = []
    for f in data_lines("CaseFolding.txt"):
        if f[1] in ("C", "S"):
            folds.append((int(f[0], 16), int(f[2], 16)))
    folds.sort()
    parts = [enc_num(len(folds))]
    prev = -1
    for cp, to in folds:
        parts.append(enc_num(cp - prev - 1))
        parts.append(enc_num(zigzag(to - cp)))
        prev = cp
    fold_encoded = "".join(parts)

    write_output(tables, gc_names, sc_names, scx_names, bin_names, str_encoded, fold_encoded,
                 {p: len(v) for p, v in str_props.items()})


def cs_literal(s, width=110):
    chunks = [s[i:i + width] for i in range(0, len(s), width)] or [""]
    return "\n            + ".join(f'"{c}"' for c in chunks)


def switch(name, mapping):
    lines = [f"    private static int {name}(string name) => name switch", "    {"]
    for k in sorted(mapping, key=lambda k: (mapping[k], k)):
        lines.append(f'        "{k}" => {mapping[k]},')
    lines.append("        _ => -1,")
    lines.append("    };")
    return "\n".join(lines)


def write_output(tables, gc_names, sc_names, scx_names, bin_names, str_encoded, fold_encoded, str_counts):
    out = []
    w = out.append
    w("// <auto-generated/>")
    w(f"// Generated by tools/unicode/generate.py from the Unicode Character Database {UNICODE_VERSION}.")
    w("// Do not edit by hand; re-run the generator instead.")
    w("#nullable enable")
    w("")
    w("using System;")
    w("")
    w("namespace Jaunt.Runtime.Regex;")
    w("")
    w("internal static partial class UnicodeData")
    w("{")
    w(f'    /// <summary>The Unicode version this data was generated from.</summary>')
    w(f'    public const string UnicodeVersion = "{UNICODE_VERSION}";')
    w("")
    w(switch("GeneralCategoryTable", gc_names))
    w("")
    w(switch("ScriptTable", sc_names))
    w("")
    w(switch("ScriptExtensionsTable", scx_names))
    w("")
    w(switch("BinaryPropertyTable", bin_names))
    w("")
    w("    private static int StringPropertyTable(string name) => name switch")
    w("    {")
    for i, p in enumerate(STRING_PROPERTIES):
        w(f'        "{p}" => {i},')
    w("        _ => -1,")
    w("    };")
    w("")
    w("    // Counts (for reference): " + ", ".join(f"{p}={c}" for p, c in str_counts.items()))
    w(f"    private const int TableCount = {len(tables)};")
    w(f"    private const int StringTableCount = {len(STRING_PROPERTIES)};")
    w("")
    w("    private static string EncodedTable(int index) => index switch")
    w("    {")
    for i, t in enumerate(tables):
        w(f"        {i} => {cs_literal(t)},")
    w('        _ => throw new ArgumentOutOfRangeException(nameof(index)),')
    w("    };")
    w("")
    w("    private static string EncodedStringTable(int index) => index switch")
    w("    {")
    for i, t in enumerate(str_encoded):
        w(f"        {i} => {cs_literal(t)},")
    w('        _ => throw new ArgumentOutOfRangeException(nameof(index)),')
    w("    };")
    w("")
    w(f"    private const string EncodedCaseFolding =\n            {cs_literal(fold_encoded)};")
    w("}")
    w("")
    text = "\n".join(out)
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print(f"wrote {OUT} ({len(text.encode('utf-8'))} bytes, {len(tables)} tables)", file=sys.stderr)


if __name__ == "__main__":
    main()
