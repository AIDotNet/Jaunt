#!/usr/bin/env python3
"""Generates src/Jaunt/Builtins/Intl/IntlLocaleData.g.cs: the CLDR alias data used by UTS 35 locale
canonicalization (ECMA-402 CanonicalizeUnicodeLocaleId).

Sources (CLDR release given by CLDR_VERSION):
  * common/supplemental/supplementalMetadata.xml: languageAlias, scriptAlias, territoryAlias, variantAlias,
    subdivisionAlias,
  * common/bcp47/*.xml: -u- and -t- key/type aliases (deprecated types with a preferred value, and type
    aliases that are themselves syntactically valid BCP 47 types, e.g. ca-islamicc, ks-primary, tz-est).

Likely subtags (needed for territory aliases with several replacements) come from ICU at run time.

Usage: python3 tools/unicode/cldr_locale.py   (downloads are cached in tools/unicode/.cache/cldr-<version>/)
Standard library only.
"""

import os
import re
import sys
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import generate  # noqa: E402

CLDR_VERSION = "48"
BASE = f"https://raw.githubusercontent.com/unicode-org/cldr/release-{CLDR_VERSION}/common/"
BCP47_FILES = [
    "calendar", "collation", "currency", "measure", "number", "segmentation", "timezone", "variant",
    "transform", "transform-destination", "transform_hybrid", "transform_ime", "transform_keyboard",
    "transform_mt", "transform_private_use",
]
PREFIX = f"cldr-{CLDR_VERSION}/"
generate.FILES[PREFIX + "supplementalMetadata.xml"] = BASE + "supplemental/supplementalMetadata.xml"
for _f in BCP47_FILES:
    generate.FILES[PREFIX + f"bcp47-{_f}.xml"] = BASE + f"bcp47/{_f}.xml"

OUT = os.path.join(generate.ROOT, "src", "Jaunt", "Builtins", "Intl", "IntlLocaleData.g.cs")

LANG = re.compile(r"^([a-z]{2,3}|[a-z]{5,8})$")
SCRIPT = re.compile(r"^[A-Z][a-z]{3}$")
REGION = re.compile(r"^([A-Z]{2}|[0-9]{3})$")
VARIANT = re.compile(r"^([a-z0-9]{5,8}|[0-9][a-z0-9]{3})$")
TYPE = re.compile(r"^[a-z0-9]{3,8}(-[a-z0-9]{3,8})*$")


def load(name):
    generate.fetch(PREFIX + name)  # ensure cached
    return ET.parse(os.path.join(generate.CACHE, PREFIX + name)).getroot()


def parse_lang_id(s):
    """Parses an underscore-separated CLDR language id; None when it is not a valid unicode_language_id."""
    parts = s.split("_")
    lang = parts[0].lower()
    if lang != "und" and not LANG.match(lang):
        return None
    i, script, region, variants = 1, None, None, []
    if i < len(parts) and SCRIPT.match(parts[i]):
        script = parts[i]
        i += 1
    if i < len(parts) and REGION.match(parts[i]):
        region = parts[i]
        i += 1
    while i < len(parts) and VARIANT.match(parts[i].lower()):
        variants.append(parts[i].lower())
        i += 1
    if i != len(parts):
        return None
    return lang, script, region, variants


def fmt_lang_id(p):
    lang, script, region, variants = p
    return "-".join([lang] + ([script] if script else []) + ([region] if region else []) + variants)


def main():
    meta = load("supplementalMetadata.xml")
    alias = meta.find("metadata/alias")

    language = []   # (type, replacement)
    for e in alias.findall("languageAlias"):
        t, r = parse_lang_id(e.get("type")), parse_lang_id(e.get("replacement"))
        if t is None or r is None:
            continue  # unreachable from structurally valid tags (e.g. i_ami, zh_min_nan) or private-use results
        language.append((fmt_lang_id(t), fmt_lang_id(r)))
    script = [(e.get("type"), e.get("replacement")) for e in alias.findall("scriptAlias")
              if SCRIPT.match(e.get("type")) and SCRIPT.match(e.get("replacement"))]
    territory = []
    for e in alias.findall("territoryAlias"):
        t = e.get("type")
        reps = [x for x in e.get("replacement").split() if REGION.match(x)]
        if REGION.match(t) and reps:
            territory.append((t, " ".join(reps)))
    variant = [(e.get("type").lower(), e.get("replacement").lower()) for e in alias.findall("variantAlias")
               if VARIANT.match(e.get("type").lower()) and VARIANT.match(e.get("replacement").lower())]
    subdivision = [(e.get("type"), e.get("replacement").split()[0]) for e in alias.findall("subdivisionAlias")]

    # -u- / -t- type aliases, as "key-alias" -> preferred type
    utypes, ttypes = {}, {}
    for f in BCP47_FILES:
        root = load(f"bcp47-{f}.xml")
        for k in root.iter("key"):
            key = k.get("name")
            target = ttypes if (k.get("extension") == "t") else utypes
            names = {t.get("name") for t in k.findall("type")}
            for t in k.findall("type"):
                name = t.get("name")
                if t.get("deprecated") == "true":
                    pref = t.get("preferred")
                    if pref:
                        target[key + ":" + name] = pref
                    continue
                for a in (t.get("alias") or "").split():
                    a = a.lower()
                    if TYPE.match(a) and a != name and a not in names:
                        target.setdefault(key + ":" + a, name)

    def table(name, pairs, doc):
        pairs = sorted(set(pairs), key=lambda p: p[0])
        keys = [p[0] for p in pairs]
        assert len(keys) == len(set(keys)), name
        body = ";".join(f"{k}>{v}" for k, v in pairs)
        lines = [f"    /// <summary>{doc} ({len(pairs)} entries, \"type&gt;replacement\" separated by ';', sorted by type).</summary>",
                 f"    internal const string {name} ="]
        chunk = []
        for i in range(0, len(body), 110):
            chunk.append("\"" + body[i:i + 110] + "\"")
        lines.append("        " + ("\n        + ".join(chunk) if chunk else "\"\""))
        lines[-1] += ";"
        return "\n".join(lines)

    out = [
        f"// <auto-generated> by tools/unicode/cldr_locale.py from CLDR {CLDR_VERSION}; do not edit. </auto-generated>",
        "// Unicode CLDR data: Copyright © Unicode, Inc. License: https://www.unicode.org/license.txt (see THIRD_PARTY_NOTICES.md)",
        "namespace Jaunt.Runtime;",
        "",
        "internal static partial class IntlLocaleData",
        "{",
        f"    internal const string CldrVersion = \"{CLDR_VERSION}\";",
        table("LanguageAliases", language, "languageAlias rules as BCP 47 language ids (und = any language)"),
        table("ScriptAliases", script, "scriptAlias"),
        table("TerritoryAliases", territory, "territoryAlias (replacements separated by spaces)"),
        table("VariantAliases", variant, "variantAlias"),
        table("SubdivisionAliases", subdivision, "subdivisionAlias (first replacement only)"),
        table("UTypeAliases", list(utypes.items()), "-u- type aliases keyed by \"key:alias\""),
        table("TTypeAliases", list(ttypes.items()), "-t- tfield value aliases keyed by \"key:alias\""),
        "}",
        "",
    ]
    with open(OUT, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(out))
    print(f"wrote {OUT}: {len(language)} language, {len(script)} script, {len(territory)} territory, "
          f"{len(variant)} variant, {len(subdivision)} subdivision, {len(utypes)} u-type, {len(ttypes)} t-type aliases")


if __name__ == "__main__":
    main()
