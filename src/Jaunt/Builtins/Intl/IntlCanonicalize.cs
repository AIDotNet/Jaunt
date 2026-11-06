using System;
using System.Collections.Generic;
using System.Text;

namespace Jaunt.Runtime;

/// <summary>
/// Lazily unpacked views of the CLDR alias tables in <c>IntlLocaleData.g.cs</c> (sorted key/value arrays searched
/// with ordinal binary search).
/// </summary>
internal static partial class IntlLocaleData
{
    internal sealed class Table
    {
        public readonly string[] Keys;
        public readonly string[] Values;

        public Table(string packed)
        {
            var entries = packed.Length == 0 ? Array.Empty<string>() : packed.Split(';');
            Keys = new string[entries.Length];
            Values = new string[entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                int gt = entries[i].IndexOf('>');
                Keys[i] = entries[i].Substring(0, gt);
                Values[i] = entries[i].Substring(gt + 1);
            }
        }

        public string? Lookup(string key)
        {
            int i = Array.BinarySearch(Keys, key, StringComparer.Ordinal);
            return i >= 0 ? Values[i] : null;
        }
    }

    private static Table? s_script, s_territory, s_variant, s_subdivision, s_utype, s_ttype;
    public static Table Script => s_script ??= new Table(ScriptAliases);
    public static Table Territory => s_territory ??= new Table(TerritoryAliases);
    public static Table Variant => s_variant ??= new Table(VariantAliases);
    public static Table Subdivision => s_subdivision ??= new Table(SubdivisionAliases);
    public static Table UType => s_utype ??= new Table(UTypeAliases);
    public static Table TType => s_ttype ??= new Table(TTypeAliases);

    /// <summary>A unicode_language_id (fields in canonical case) used by the languageAlias rules.</summary>
    internal sealed class LangId
    {
        public string Language = "und";
        public string? Script;
        public string? Region;
        public string[] Variants = Array.Empty<string>();

        public static LangId Parse(string s)
        {
            var parts = s.Split('-');
            var id = new LangId { Language = parts[0] };
            int i = 1;
            if (i < parts.Length && IntlCore.IsScriptSubtag(parts[i])) id.Script = parts[i++];
            if (i < parts.Length && IntlCore.IsRegionSubtag(parts[i])) id.Region = parts[i++];
            if (i < parts.Length) id.Variants = parts[i..];
            return id;
        }

        /// <summary>Number of fields the rule constrains (higher = more specific).</summary>
        public int Specificity => (Language == "und" ? 0 : 1) + (Script != null ? 1 : 0) + (Region != null ? 1 : 0) + Variants.Length;
    }

    internal sealed class LanguageRules
    {
        /// <summary>Rules whose type is a bare language, keyed by that language.</summary>
        public readonly string[] SimpleKeys;
        public readonly LangId[] SimpleValues;
        /// <summary>Rules with script/region/variants or an "und" language (few), most specific first.</summary>
        public readonly (LangId Type, LangId Replacement)[] Complex;

        public LanguageRules()
        {
            var t = new Table(LanguageAliases);
            var keys = new List<string>();
            var values = new List<LangId>();
            var complex = new List<(LangId, LangId)>();
            for (int i = 0; i < t.Keys.Length; i++)
            {
                var type = LangId.Parse(t.Keys[i]);
                var rep = LangId.Parse(t.Values[i]);
                if (type.Language != "und" && type.Script == null && type.Region == null && type.Variants.Length == 0)
                {
                    keys.Add(type.Language);
                    values.Add(rep);
                }
                else complex.Add((type, rep));
            }
            SimpleKeys = keys.ToArray();   // already sorted: the packed table is sorted and a bare language sorts before its extensions
            SimpleValues = values.ToArray();
                        Complex = complex.ToArray();
        }

        public LangId? Simple(string language)
        {
            int i = Array.BinarySearch(SimpleKeys, language, StringComparer.Ordinal);
            return i >= 0 ? SimpleValues[i] : null;
        }
    }

    private static LanguageRules? s_language;
    public static LanguageRules Language => s_language ??= new LanguageRules();
}

internal static partial class IntlCore
{
    /// <summary>
    /// Applies the UTS 35 (Annex C) alias replacement to a unicode_language_id in place: languageAlias rules (most
    /// specific matching rule first), scriptAlias, territoryAlias (choosing among several replacements by likely
    /// subtags), variantAlias; repeated until stable. Variants end up sorted. Fields must be in canonical case.
    /// </summary>
    public static void CanonicalizeLanguageId(ref string language, ref string? script, ref string? region, List<string> variants)
    {
        var rules = IntlLocaleData.Language;
        for (int guard = 0; guard < 64; guard++)
        {
            // languageAlias
            IntlLocaleData.LangId? matchType = null, matchRep = null;
            foreach (var (type, rep) in rules.Complex)
            {
                if (type.Language != "und" && type.Language != language) continue;
                if (type.Script != null && type.Script != script) continue;
                if (type.Region != null && type.Region != region) continue;
                bool all = true;
                foreach (var v in type.Variants) if (!variants.Contains(v)) { all = false; break; }
                if (!all) continue;
                if (matchType == null || type.Specificity > matchType.Specificity) { matchType = type; matchRep = rep; }
            }
            if ((matchType == null || matchType.Specificity <= 1) && rules.Simple(language) is { } simple)
            {
                matchType = new IntlLocaleData.LangId { Language = language };
                matchRep = simple;
            }
            if (matchType != null)
            {
                var rep = matchRep!;
                if (rep.Language != "und") language = rep.Language;
                else if (matchType.Language != "und") language = "und";
                if (matchType.Script != null || script == null) script = rep.Script;   // replace/remove a matched field, or add
                if (matchType.Region != null || region == null) region = rep.Region;
                foreach (var v in matchType.Variants) variants.Remove(v);
                foreach (var v in rep.Variants) if (!variants.Contains(v)) variants.Add(v);
                continue;
            }
            // scriptAlias
            if (script != null && IntlLocaleData.Script.Lookup(script) is { } s) { script = s; continue; }
            // territoryAlias
            if (region != null && IntlLocaleData.Territory.Lookup(region) is { } r)
            {
                region = ChooseRegion(language, script, r);
                continue;
            }
            // variantAlias
            bool changed = false;
            for (int i = 0; i < variants.Count; i++)
            {
                if (IntlLocaleData.Variant.Lookup(variants[i]) is { } v)
                {
                    if (variants.Contains(v)) variants.RemoveAt(i--);
                    else variants[i] = v;
                    changed = true;
                }
            }
            if (changed) continue;
            break;
        }
        variants.Sort(StringComparer.Ordinal);
    }

    /// <summary>Territory alias with several replacements: the likely region of language+script, if listed, else the first.</summary>
    private static string ChooseRegion(string language, string? script, string replacements)
    {
        int sp = replacements.IndexOf(' ');
        if (sp < 0) return replacements;
        var first = replacements.Substring(0, sp);
        var max = Icu.AddLikelySubtags(script != null ? language + "_" + script : language);
        if (max != null)
        {
            var parts = max.Split('_');
            for (int i = 1; i < parts.Length && i <= 2; i++)
            {
                if (IsRegionSubtag(parts[i]))
                {
                    foreach (var cand in replacements.Split(' ')) if (cand == parts[i]) return cand;
                    break;
                }
            }
        }
        return first;
    }

    /// <summary>Canonicalizes the language id of a parsed tag in place (UTS 35 alias replacement).</summary>
    public static void CanonicalizeLanguageId(LanguageTag t)
    {
        string lang = t.Language;
        string? script = t.Script, region = t.Region;
        CanonicalizeLanguageId(ref lang, ref script, ref region, t.Variants);
        t.Language = lang;
        t.Script = script;
        t.Region = region;
    }

    /// <summary>Canonicalizes a -u- type value for <paramref name="key"/> (bcp47 type aliases, rg/sd subdivision aliases).</summary>
    public static string CanonicalizeUnicodeType(string key, string value)
    {
        if (value.Length == 0) return value;
        if (key is "rg" or "sd")
        {
            var rep = IntlLocaleData.Subdivision.Lookup(value);
            if (rep != null) return IsRegionSubtag(rep) ? rep.ToLowerInvariant() + "zzzz" : rep;
            return value;
        }
        return IntlLocaleData.UType.Lookup(key + ":" + value) ?? value;
    }

    /// <summary>Canonicalizes the body of a -t- extension (lower case): tlang alias replacement, sorted tfields, tvalue aliases.</summary>
    private static string CanonicalizeTransformed(string body)
    {
        var parts = body.Split('-');
        var sb = new StringBuilder();
        int i = 0;
        if (i < parts.Length && IsLanguageSubtag(parts[i]))
        {
            string lang = parts[i++];
            string? script = null, region = null;
            var variants = new List<string>();
            if (i < parts.Length && IsScriptSubtag(parts[i])) { script = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1); i++; }
            if (i < parts.Length && IsRegionSubtag(parts[i])) { region = parts[i].ToUpperInvariant(); i++; }
            while (i < parts.Length && IsVariantSubtag(parts[i])) variants.Add(parts[i++]);
            CanonicalizeLanguageId(ref lang, ref script, ref region, variants);
            sb.Append(lang);
            if (script != null) sb.Append('-').Append(script.ToLowerInvariant());
            if (region != null) sb.Append('-').Append(region.ToLowerInvariant());
            foreach (var v in variants) sb.Append('-').Append(v);
        }
        var fields = new List<KeyValuePair<string, string>>();
        while (i < parts.Length)
        {
            var key = parts[i++];
            int start = i;
            while (i < parts.Length && parts[i].Length != 2) i++;
            var value = string.Join("-", parts, start, i - start);
            value = IntlLocaleData.TType.Lookup(key + ":" + value) ?? value;
            fields.Add(new(key, value));
        }
        fields.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        foreach (var f in fields)
        {
            if (sb.Length > 0) sb.Append('-');
            sb.Append(f.Key).Append('-').Append(f.Value);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Full UTS 35 canonicalization of a parsed (structurally valid) tag in place: language id aliases, -u- keyword
    /// value aliases, -t- extension canonicalization. Serialize the result with <see cref="Format"/>.
    /// </summary>
    public static void CanonicalizeTag(LanguageTag t)
    {
        CanonicalizeLanguageId(t);
        for (int k = 0; k < t.UKeywords.Count; k++)
        {
            var kw = t.UKeywords[k];
            var v = CanonicalizeUnicodeType(kw.Key, kw.Value);
            if (!ReferenceEquals(v, kw.Value)) t.UKeywords[k] = new(kw.Key, v);
        }
        if (t.OtherExtensions.TryGetValue('t', out var tbody)) t.OtherExtensions['t'] = CanonicalizeTransformed(tbody);
    }
}
