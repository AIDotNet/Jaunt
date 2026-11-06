using System;
using System.Globalization;
using System.Text;

namespace Jaunt.Runtime;

internal static class StringBuiltins
{
    private static string TryNormalizeNfc(string s)
    {
        try { return s.IsNormalized(NormalizationForm.FormC) ? s : s.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return s; }   // lone surrogates
    }

    public static void Setup(Realm r)
    {
        var proto = new JsStringObject(r, "", r.ObjectPrototype);
        r.StringPrototype = proto;
        var ctor = r.Constructor("String", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            string s;
            if (a.Length == 0) s = "";
            else if (!a.IsConstruct && a[0].IsSymbol) s = ((JsSymbol)a[0].Ref!).ToString();
            else s = Operations.ToString(realm, a[0]);
            if (!a.IsConstruct) return new JsValue(s);
            var p = Operations.GetPrototypeFromConstructor(realm, a.NewTarget.AsObjectUnchecked, realm.StringPrototype);
            return JsValue.FromObject(new JsStringObject(realm, s, p));
        }, proto);
        r.DefineGlobal("String", JsValue.FromObject(ctor));
        r.Method(ctor, "fromCharCode", 1, static (in NativeArgs a) =>
        {
            if (a.Length == 1) return new JsValue(a.Realm.CharString((char)Operations.ToUint32(a.Realm, a[0])));
            var sb = new StringBuilder(a.Length);
            foreach (var v in a.Args) sb.Append((char)(ushort)Operations.ToUint32(a.Realm, v));
            return new JsValue(sb.ToString());
        });
        r.Method(ctor, "fromCodePoint", 1, static (in NativeArgs a) =>
        {
            var sb = new StringBuilder(a.Length);
            foreach (var v in a.Args)
            {
                double d = Operations.ToNumber(a.Realm, v);
                if (d != Math.Floor(d) || d < 0 || d > 0x10FFFF) throw JsErrors.RangeError(a.Realm, $"Invalid code point {Operations.ToString(a.Realm, v)}");
                sb.Append(CharExtensions.ConvertFromUtf32IfValid((int)d));
            }
            return new JsValue(sb.ToString());
        });
        r.Method(ctor, "raw", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var cooked = Operations.ToObject(realm, a[0]);
            var raw = Operations.ToObject(realm, cooked.Get("raw"));
            long len = Operations.LengthOfArrayLike(realm, raw);
            var sb = new StringBuilder();
            for (long i = 0; i < len; i++)
            {
                sb.Append(Operations.ToString(realm, raw.GetIndex((uint)i, JsValue.FromObject(raw))));
                if (i + 1 < len && i + 1 < a.Length) sb.Append(Operations.ToString(realm, a[(int)i + 1]));
            }
            return new JsValue(sb.ToString());
        });

        r.Method(proto, "toString", 0, static (in NativeArgs a) => new JsValue(ThisString(a, "toString")));
        r.Method(proto, "valueOf", 0, static (in NativeArgs a) => new JsValue(ThisString(a, "valueOf")));
        r.Method(proto, "charAt", 1, static (in NativeArgs a) =>
        {
            var s = Str(a);
            double p = Operations.ToIntegerOrInfinity(a.Realm, a[0]);
            return p < 0 || p >= s.Length ? JsValue.EmptyString : new JsValue(a.Realm.CharString(s[(int)p]));
        });
        r.Method(proto, "charCodeAt", 1, static (in NativeArgs a) =>
        {
            var s = Str(a);
            var pv = a[0];
            double p = pv.IsNumber && pv.Num == (int)pv.Num ? pv.Num : Operations.ToIntegerOrInfinity(a.Realm, pv);
            return p < 0 || p >= s.Length ? JsValue.NaN : new JsValue((double)s[(int)p]);
        });
        r.Method(proto, "codePointAt", 1, static (in NativeArgs a) =>
        {
            var s = Str(a);
            double p = Operations.ToIntegerOrInfinity(a.Realm, a[0]);
            if (p < 0 || p >= s.Length) return JsValue.Undefined;
            int i = (int)p;
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) return new JsValue(char.ConvertToUtf32(s[i], s[i + 1]));
            return new JsValue((double)s[i]);
        });
        r.Method(proto, "at", 1, static (in NativeArgs a) =>
        {
            var s = Str(a);
            double rel = Operations.ToIntegerOrInfinity(a.Realm, a[0]);
            double k = rel >= 0 ? rel : s.Length + rel;
            return k < 0 || k >= s.Length ? JsValue.Undefined : new JsValue(a.Realm.CharString(s[(int)k]));
        });
        r.Method(proto, "indexOf", 1, static (in NativeArgs a) =>
        {
            var s = Str(a);
            var search = Operations.ToString(a.Realm, a[0]);
            double pos = Math.Min(Math.Max(Operations.ToIntegerOrInfinity(a.Realm, a[1]), 0), s.Length);
            return new JsValue(s.IndexOf(search, (int)pos, StringComparison.Ordinal));
        });
        r.Method(proto, "lastIndexOf", 1, static (in NativeArgs a) =>
        {
            var s = Str(a);
            var search = Operations.ToString(a.Realm, a[0]);
            double n = Operations.ToNumber(a.Realm, a[1]);
            double pos = double.IsNaN(n) ? double.PositiveInfinity : Math.Truncate(n);
            int start = (int)Math.Min(Math.Max(pos, 0), s.Length);
            for (int i = Math.Min(start, s.Length - search.Length); i >= 0; i--)
                if (string.CompareOrdinal(s, i, search, 0, search.Length) == 0) return new JsValue(i);
            return new JsValue(-1);
        });
        r.Method(proto, "includes", 1, static (in NativeArgs a) =>
        {
            var s = Str(a);
            if (IsRegExp(a.Realm, a[0])) throw JsErrors.TypeError(a.Realm, "First argument to String.prototype.includes must not be a regular expression");
            var search = Operations.ToString(a.Realm, a[0]);
            double pos = Math.Min(Math.Max(Operations.ToIntegerOrInfinity(a.Realm, a[1]), 0), s.Length);
            return JsValue.FromBool(s.IndexOf(search, (int)pos, StringComparison.Ordinal) >= 0);
        });
        r.Method(proto, "startsWith", 1, static (in NativeArgs a) =>
        {
            var s = Str(a);
            if (IsRegExp(a.Realm, a[0])) throw JsErrors.TypeError(a.Realm, "First argument to String.prototype.startsWith must not be a regular expression");
            var search = Operations.ToString(a.Realm, a[0]);
            int pos = (int)Math.Min(Math.Max(Operations.ToIntegerOrInfinity(a.Realm, a[1]), 0), s.Length);
            return JsValue.FromBool(pos + search.Length <= s.Length && string.CompareOrdinal(s, pos, search, 0, search.Length) == 0);
        });
        r.Method(proto, "endsWith", 1, static (in NativeArgs a) =>
        {
            var s = Str(a);
            if (IsRegExp(a.Realm, a[0])) throw JsErrors.TypeError(a.Realm, "First argument to String.prototype.endsWith must not be a regular expression");
            var search = Operations.ToString(a.Realm, a[0]);
            int end = a[1].IsUndefined ? s.Length : (int)Math.Min(Math.Max(Operations.ToIntegerOrInfinity(a.Realm, a[1]), 0), s.Length);
            int start = end - search.Length;
            return JsValue.FromBool(start >= 0 && string.CompareOrdinal(s, start, search, 0, search.Length) == 0);
        });
        r.Method(proto, "slice", 2, static (in NativeArgs a) =>
        {
            var s = Str(a);
            long from = Operations.RelativeIndex(a.Realm, a[0], s.Length, 0);
            long to = Operations.RelativeIndex(a.Realm, a[1], s.Length, s.Length);
            return from >= to ? JsValue.EmptyString : new JsValue(Sub(a.Realm, s, (int)from, (int)(to - from)));
        });
        r.Method(proto, "substring", 2, static (in NativeArgs a) =>
        {
            var s = Str(a);
            double start = Math.Min(Math.Max(Operations.ToIntegerOrInfinity(a.Realm, a[0]), 0), s.Length);
            double end = a[1].IsUndefined ? s.Length : Math.Min(Math.Max(Operations.ToIntegerOrInfinity(a.Realm, a[1]), 0), s.Length);
            int from = (int)Math.Min(start, end), to = (int)Math.Max(start, end);
            return new JsValue(Sub(a.Realm, s, from, to - from));
        });
        r.Method(proto, "substr", 2, static (in NativeArgs a) =>
        {
            var s = Str(a);
            long start = Operations.RelativeIndex(a.Realm, a[0], s.Length, 0);
            double len = a[1].IsUndefined ? s.Length : Operations.ToIntegerOrInfinity(a.Realm, a[1]);
            long end = (long)Math.Min(start + Math.Max(len, 0), s.Length);
            return start >= end ? JsValue.EmptyString : new JsValue(Sub(a.Realm, s, (int)start, (int)(end - start)));
        });
        r.Method(proto, "toUpperCase", 0, static (in NativeArgs a) => new JsValue(CaseMapping.ToUpper(Str(a))));
        r.Method(proto, "toLowerCase", 0, static (in NativeArgs a) => new JsValue(CaseMapping.ToLower(Str(a))));
        r.Method(proto, "toLocaleUpperCase", 0, static (in NativeArgs a) =>
        {
            var s = Str(a);
            return new JsValue(a.Realm.Intl == null ? CaseMapping.ToUpper(s) : IntlCollator.TransformCase(a.Realm, s, a[0], upper: true));
        });
        r.Method(proto, "toLocaleLowerCase", 0, static (in NativeArgs a) =>
        {
            var s = Str(a);
            return new JsValue(a.Realm.Intl == null ? CaseMapping.ToLower(s) : IntlCollator.TransformCase(a.Realm, s, a[0], upper: false));
        });
        r.Method(proto, "trim", 0, static (in NativeArgs a) => new JsValue(NumberFormatting.TrimJs(Str(a))));
        r.Method(proto, "trimStart", 0, static (in NativeArgs a) => new JsValue(NumberFormatting.TrimStartJs(Str(a))));
        r.Method(proto, "trimEnd", 0, static (in NativeArgs a) => new JsValue(NumberFormatting.TrimEndJs(Str(a))));
        proto.DefineHidden("trimLeft", proto.Get("trimStart"));
        proto.DefineHidden("trimRight", proto.Get("trimEnd"));
        r.Method(proto, "padStart", 1, static (in NativeArgs a) => new JsValue(Pad(a, start: true)));
        r.Method(proto, "padEnd", 1, static (in NativeArgs a) => new JsValue(Pad(a, start: false)));
        r.Method(proto, "repeat", 1, static (in NativeArgs a) =>
        {
            var s = Str(a);
            double n = Operations.ToIntegerOrInfinity(a.Realm, a[0]);
            if (n < 0 || double.IsInfinity(n)) throw JsErrors.RangeError(a.Realm, "Invalid count value: " + NumberFormatting.ToString(n));
            if (s.Length == 0 || n == 0) return JsValue.EmptyString;
            if (s.Length * n > a.Realm.MaxStringLength) throw JsErrors.RangeError(a.Realm, "Invalid string length");
            a.Realm.ChargeBytes((long)(s.Length * n * 2));
            var sb = new StringBuilder((int)(s.Length * n));
            for (int i = 0; i < n; i++) sb.Append(s);
            return new JsValue(sb.ToString());
        });
        r.Method(proto, "concat", 1, static (in NativeArgs a) =>
        {
            var sb = new StringBuilder(Str(a));
            foreach (var v in a.Args) sb.Append(Operations.ToString(a.Realm, v));
            if (sb.Length > a.Realm.MaxStringLength) throw JsErrors.RangeError(a.Realm, "Invalid string length");
            return new JsValue(sb.ToString());
        });
        r.Method(proto, "split", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (!a.This.IsNullOrUndefined && a[0].IsObject)
            {
                var splitter = a[0].AsObjectUnchecked.Get(JsSymbol.Split);
                if (!splitter.IsNullOrUndefined) return realm.Vm.Call(splitter, a[0], new[] { a.This, a[1] });
            }
            var s = Str(a);
            uint lim = a[1].IsUndefined ? uint.MaxValue : Operations.ToUint32(realm, a[1]);
            var sep = Operations.ToString(realm, a[0]);   // before the limit check (observable)
            var res = realm.NewArray();
            if (lim == 0) return JsValue.FromObject(res);
            if (a[0].IsUndefined) { res.Push(new JsValue(s)); return JsValue.FromObject(res); }
            if (sep.Length == 0)
            {
                for (int i = 0; i < s.Length && res.Length < lim; i++) res.Push(new JsValue(realm.CharString(s[i])));
                return JsValue.FromObject(res);
            }
            int pos = 0;
            while (res.Length < lim)
            {
                int idx = s.IndexOf(sep, pos, StringComparison.Ordinal);
                if (idx < 0) { res.Push(new JsValue(s.Substring(pos))); break; }
                res.Push(new JsValue(s.Substring(pos, idx - pos)));
                pos = idx + sep.Length;
                realm.Vm.Tick();
            }
            return JsValue.FromObject(res);
        });
        r.Method(proto, "replace", 2, static (in NativeArgs a) => Replace(a, all: false));
        r.Method(proto, "replaceAll", 2, static (in NativeArgs a) => Replace(a, all: true));
        r.Method(proto, "match", 1, static (in NativeArgs a) => DelegateRegExp(a, JsSymbol.Match, "match", flags: null));
        r.Method(proto, "matchAll", 1, static (in NativeArgs a) =>
        {
            if (a[0].IsObject && RegExpBuiltins.IsRegExpObject(a[0]))
            {
                var flags = Operations.ToString(a.Realm, a[0].AsObjectUnchecked.Get("flags"));
                if (!flags.Contains('g')) throw JsErrors.TypeError(a.Realm, "String.prototype.matchAll called with a non-global RegExp argument");
            }
            return DelegateRegExp(a, JsSymbol.MatchAll, "matchAll", flags: "g");
        });
        r.Method(proto, "search", 1, static (in NativeArgs a) => DelegateRegExp(a, JsSymbol.Search, "search", flags: null));
        r.Method(proto, "localeCompare", 1, static (in NativeArgs a) =>
        {
            var s = Str(a);
            var t = Operations.ToString(a.Realm, a[0]);
            if (a.Realm.Intl != null) return new JsValue(IntlCollator.LocaleCompare(a.Realm, s, t, a[1], a[2]));
            // canonically equivalent strings must compare equal (§22.1.3.12): compare NFC forms
            s = TryNormalizeNfc(s);
            t = TryNormalizeNfc(t);
            if (string.Equals(s, t, StringComparison.Ordinal)) return new JsValue(0);
            return new JsValue(Math.Sign(string.Compare(s, t, CultureInfo.InvariantCulture, CompareOptions.None)));
        });
        r.Method(proto, "normalize", 0, static (in NativeArgs a) =>
        {
            var s = Str(a);
            var f = a[0].IsUndefined ? "NFC" : Operations.ToString(a.Realm, a[0]);
            var form = f switch
            {
                "NFC" => NormalizationForm.FormC,
                "NFD" => NormalizationForm.FormD,
                "NFKC" => NormalizationForm.FormKC,
                "NFKD" => NormalizationForm.FormKD,
                _ => throw JsErrors.RangeError(a.Realm, "The normalization form should be one of NFC, NFD, NFKC, NFKD."),
            };
            try { return new JsValue(s.Normalize(form)); }
            catch (ArgumentException) { return new JsValue(s); }
        });
        r.Method(proto, "isWellFormed", 0, static (in NativeArgs a) =>
        {
            var s = Str(a);
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i])) { if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; } return JsValue.False; }
                if (char.IsLowSurrogate(s[i])) return JsValue.False;
            }
            return JsValue.True;
        });
        Es2025Builtins.InstallString(r, proto);
        // Annex B HTML methods
        foreach (var (name, tag, attr) in new[]
        {
            ("anchor", "a", "name"), ("big", "big", ""), ("blink", "blink", ""), ("bold", "b", ""), ("fixed", "tt", ""),
            ("fontcolor", "font", "color"), ("fontsize", "font", "size"), ("italics", "i", ""), ("link", "a", "href"),
            ("small", "small", ""), ("strike", "strike", ""), ("sub", "sub", ""), ("sup", "sup", ""),
        })
        {
            var f = r.MethodEager(proto, name, attr.Length > 0 ? 1 : 0, static (in NativeArgs a) =>
            {
                var (tag, attr) = ((string, string))a.Callee.State!;
                var s = Str(a);
                var sb = new StringBuilder("<").Append(tag);
                if (attr.Length > 0)
                    sb.Append(' ').Append(attr).Append("=\"").Append(Operations.ToString(a.Realm, a[0]).Replace("\"", "&quot;")).Append('"');
                sb.Append('>').Append(s).Append("</").Append(tag).Append('>');
                return new JsValue(sb.ToString());
            });
            f.State = (tag, attr);
        }
        r.Method(proto, JsSymbol.Iterator, "[Symbol.iterator]", 0, static (in NativeArgs a) => JsValue.FromObject(new JsStringIterator(a.Realm, Str(a))));
    }

    private static string ThisString(in NativeArgs a, string method)
    {
        if (a.This.IsString) return a.This.AsStringUnchecked;
        if (a.This.Ref is JsStringObject so) return so.Value;
        throw JsErrors.TypeError(a.Realm, $"String.prototype.{method} requires that 'this' be a String");
    }

    private static string Str(in NativeArgs a)
    {
        var t = a.This;
        if (t.IsString) return t.AsStringUnchecked;
        if (t.IsNullOrUndefined) throw JsErrors.TypeError(a.Realm, "String.prototype method called on null or undefined");
        return Operations.ToString(a.Realm, t);
    }

    private static string Sub(Realm realm, string s, int start, int len)
    {
        if (len == 1) return realm.CharString(s[start]);
        if (start == 0 && len == s.Length) return s;
        return s.Substring(start, len);
    }

    private static bool IsRegExp(Realm realm, in JsValue v)
    {
        if (v.IsNotObject) return false;
        var m = v.AsObjectUnchecked.Get(JsSymbol.Match);
        if (!m.IsUndefined) return Operations.ToBoolean(m);
        return v.Ref is JsRegExp;
    }

    private static string Pad(in NativeArgs a, bool start)
    {
        var s = Str(a);
        double max = Operations.ToLength(a.Realm, a[0]);
        if (max <= s.Length) return s;
        string filler = a[1].IsUndefined ? " " : Operations.ToString(a.Realm, a[1]);
        if (filler.Length == 0) return s;
        if (max > a.Realm.MaxStringLength) throw JsErrors.RangeError(a.Realm, "Invalid string length");
        int fill = (int)max - s.Length;
        var sb = new StringBuilder((int)max);
        if (!start) sb.Append(s);
        while (fill > 0)
        {
            int n = Math.Min(fill, filler.Length);
            sb.Append(filler, 0, n);
            fill -= n;
        }
        if (start) sb.Append(s);
        return sb.ToString();
    }

    private static JsValue DelegateRegExp(in NativeArgs a, JsSymbol sym, string name, string? flags)
    {
        var realm = a.Realm;
        if (a.This.IsNullOrUndefined) throw JsErrors.TypeError(realm, $"String.prototype.{name} called on null or undefined");
        var rx = a[0];
        if (rx.IsObject)
        {
            var m = Operations.GetV(realm, rx, sym);
            if (!m.IsNullOrUndefined) return realm.Vm.Call(m, rx, new ReadOnlySpan<JsValue>(in a.This));
        }
        var s = Operations.ToString(realm, a.This);
        var re = RegExpBuiltins.Create(realm, rx.IsUndefined ? "" : Operations.ToString(realm, rx), flags ?? "");
        var method = re.Get(sym);
        var sv = new JsValue(s);
        return realm.Vm.Call(method, JsValue.FromObject(re), new ReadOnlySpan<JsValue>(in sv));
    }

    private static JsValue Replace(in NativeArgs a, bool all)
    {
        var realm = a.Realm;
        if (a.This.IsNullOrUndefined) throw JsErrors.TypeError(realm, "String.prototype.replace called on null or undefined");
        var searchValue = a[0];
        var replaceValue = a[1];
        if (searchValue.IsObject)
        {
            if (all && RegExpBuiltins.IsRegExpObject(searchValue))
            {
                var flags = Operations.ToString(realm, searchValue.AsObjectUnchecked.Get("flags"));
                if (!flags.Contains('g')) throw JsErrors.TypeError(realm, "replaceAll must be called with a global RegExp");
            }
            var replacer = Operations.GetV(realm, searchValue, JsSymbol.Replace);
            if (!replacer.IsNullOrUndefined) return realm.Vm.Call(replacer, searchValue, new[] { a.This, replaceValue });
        }
        var s = Operations.ToString(realm, a.This);
        var search = Operations.ToString(realm, searchValue);
        bool functional = replaceValue.IsFunction;
        string replaceStr = functional ? "" : Operations.ToString(realm, replaceValue);
        var positions = new System.Collections.Generic.List<int>();
        int advance = Math.Max(1, search.Length);
        int p = s.IndexOf(search, 0, StringComparison.Ordinal);
        while (p >= 0)
        {
            positions.Add(p);
            if (!all) break;
            p = p + advance > s.Length ? -1 : s.IndexOf(search, p + advance, StringComparison.Ordinal);
            realm.Vm.Tick();
        }
        if (positions.Count == 0) return new JsValue(s);
        var sb = new StringBuilder();
        int end = 0;
        foreach (int idx in positions)
        {
            sb.Append(s, end, idx - end);
            string rep = functional
                ? Operations.ToString(realm, realm.Vm.Call(replaceValue, JsValue.Undefined, new[] { new JsValue(search), new JsValue(idx), new JsValue(s) }))
                : RegExpBuiltins.GetSubstitution(search, s, idx, Array.Empty<JsValue>(), JsValue.Undefined, replaceStr, realm);
            sb.Append(rep);
            end = idx + search.Length;
            if (sb.Length > realm.MaxStringLength) throw JsErrors.RangeError(realm, "Invalid string length");
        }
        if (end < s.Length) sb.Append(s, end, s.Length - end);
        return new JsValue(sb.ToString());
    }
}

internal static class CharExtensions
{
    public static string ConvertFromUtf32IfValid(int cp) => cp is >= 0xD800 and <= 0xDFFF ? ((char)cp).ToString() : char.ConvertFromUtf32(cp);
}
