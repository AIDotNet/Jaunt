using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Jaunt.Compiler;

namespace Jaunt.Runtime;

internal static class JsonBuiltins
{
    public static void Setup(Realm r)
    {
        var json = new JsObject(r, r.ObjectPrototype);
        r.DefineGlobal("JSON", JsValue.FromObject(json));
        json.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("JSON"), PropFlags.Configurable);
        r.Method(json, "parse", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var text = Operations.ToString(realm, a[0]);
            if (a[1].IsFunction)
            {
                // reviver: keep parse records so the reviver's context can expose primitive source text, if it can see the context
                bool context = ReviverSeesContext(a[1]);
                var p = new JsonParser(realm, text, 512) { BuildRecords = context };
                p.SkipWs();
                var pv = p.ParseValue(0);
                var rec = p.LastRecord;
                p.SkipWs();
                if (p.Pos < text.Length) p.Fail($"Unexpected token '{text[p.Pos]}'");
                if (p.Error != null) return realm.Vm.Raise(p.ErrorValue());
                var root = realm.NewObject();
                root.CreateDataProperty("", pv);
                return Internalize(realm, root, "", a[1], rec, text, new JsValue[3]);
            }
            // a syntax error is raised without a CLR throw: JSON.parse of untrusted input inside try/catch is common
            return TryParse(realm, text, 512, out var v, out var error) ? v : realm.Vm.Raise(error);
        });
        r.Method(json, "rawJSON", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var text = Operations.ToString(realm, a[0]);
            if (text.Length == 0 || IsJsonWs(text[0]) || IsJsonWs(text[^1]))
                throw JsErrors.SyntaxError(realm, "JSON.rawJSON: invalid raw JSON text");
            if (text[0] is '{' or '[') throw JsErrors.SyntaxError(realm, "JSON.rawJSON: raw JSON text cannot be an object or array");
            Parse(realm, text, maxDepth: 1);   // must be a valid JSON primitive
            var o = new JsRawJson(realm);
            o.CreateDataProperty("rawJSON", new JsValue(text));
            ObjectBuiltins.SetIntegrityLevel(realm, o, frozen: true);
            return JsValue.FromObject(o);
        });
        r.Method(json, "isRawJSON", 1, static (in NativeArgs a) => JsValue.FromBool(a[0].Ref is JsRawJson));
        r.Method(json, "stringify", 3, static (in NativeArgs a) =>
        {
            var s = Stringify(a.Realm, a[0], a[1], a[2], long.MaxValue);
            return s == null ? JsValue.Undefined : new JsValue(s);
        });
    }

    /// <summary>Whether a call of the reviver can observe its third argument: a plain function with fewer parameters and no use of arguments can't.</summary>
    private static bool ReviverSeesContext(in JsValue reviver) =>
        !(reviver.Ref is JsClosure c && c.Template.ParamCount < 3 && (c.Template.Flags & FunctionFlags.NeedsArgsArray) == 0);

    private static JsValue Internalize(Realm realm, JsObject holder, string name, JsValue reviver, bool seesContext, JsonParseRecord? record, string source, JsValue[] args)
    {
        var val = holder.Get(name);
        // InternalizeJSONProperty (JSON.parse source text access): records apply only while the value is unchanged.
        // The context object is fresh per call (a reviver may keep it); a primitive's context has its source text.
        JsObject? context;
        List<JsonParseRecord?>? elements = null;
        JsonParseRecord? entries = null;
        if (!seesContext) context = null;
        else if (record != null && Operations.SameValue(record.Value, val))
        {
            if (!val.IsObject)
            {
                context = new JsObject(realm, realm.JsonContextShape, 1);
                context.Slots[0] = new JsValue(source.Substring(record.Start, record.End - record.Start));
            }
            else context = realm.NewObject();
            elements = record.Elements;
            if (record.Entries != null) entries = record;
        }
        else context = realm.NewObject();
        if (val.IsObject)
        {
            var o = val.AsObjectUnchecked;
            if (ArrayBuiltins.IsArray(realm, val))
            {
                long len = Operations.LengthOfArrayLike(realm, o);
                for (long i = 0; i < len; i++)
                    Revive(realm, o, PropertyKey.IndexToString((uint)i), reviver, seesContext, elements != null && i < elements.Count ? elements[(int)i] : null, source, args);
            }
            else
            {
                // EnumerableOwnProperties is computed once, before any reviver call
                List<string> names;
                if (!o.IsExotic && o.Dict == null && !o.Shape.HasIndexKeys)
                {
                    var shape = o.Shape;
                    names = new List<string>(shape.Count);
                    for (int i = 0; i < shape.Count; i++)
                        if (shape.Keys[i].Value is string ks && (shape.Flags[i] & PropFlags.Enumerable) != 0) names.Add(ks);
                }
                else
                {
                    var keys = new List<PropertyKey>();
                    o.OwnPropertyKeys(keys);
                    names = new List<string>(keys.Count);
                    foreach (var k in keys)
                        if (k.Value is string ks && o.GetOwnProperty(k, out var d) && d.Enumerable) names.Add(ks);
                }
                // the parsed entries are in key order too: the cursor usually hits without a scan
                int cursor = 0;
                foreach (var ks in names)
                    Revive(realm, o, ks, reviver, seesContext, entries != null ? FindEntry(entries, ks, ref cursor) : null, source, args);
            }
        }
        args[0] = new JsValue(name);
        args[1] = val;
        args[2] = JsValue.FromObject(context);
        var result = realm.Vm.Call(reviver, JsValue.FromObject(holder), args);
        args[1] = default;
        args[2] = default;
        return result;
    }

    /// <summary>
    /// The record of <paramref name="key"/>. Without duplicate keys the entries are in property order and the cursor hits;
    /// otherwise the last entry with the key is the one whose value the object kept.
    /// </summary>
    private static JsonParseRecord? FindEntry(JsonParseRecord record, string key, ref int cursor)
    {
        var entries = record.Entries!;
        if (!record.DuplicateKeys && cursor < entries.Count && entries[cursor].Key == key) return entries[cursor++].Value;
        for (int j = entries.Count - 1; j >= 0; j--) if (entries[j].Key == key) return entries[j].Value;
        return null;
    }

    private static bool IsJsonWs(char c) => c is ' ' or '\t' or '\n' or '\r';

    private static void Revive(Realm realm, JsObject o, string key, JsValue reviver, bool seesContext, JsonParseRecord? record, string source, JsValue[] args)
    {
        realm.Vm.EnterNative();
        try
        {
            var nv = Internalize(realm, o, key, reviver, seesContext, record, source, args);
            if (nv.IsUndefined) o.Delete(key);
            else o.CreateDataProperty(key, nv);
        }
        finally { realm.Vm.ExitNative(); }
    }

    // ------------------------------------------------------------------ parse

    public static JsValue Parse(Realm realm, string text, int maxDepth)
    {
        if (!TryParse(realm, text, maxDepth, out var v, out var error)) throw new JsThrowException(error);
        return v;
    }

    /// <summary>
    /// Parses <paramref name="text"/>; false with the SyntaxError in <paramref name="error"/> instead of a throw.
    /// (Not a nullable JsValue: the implicit string conversion turns a bare <c>null</c> into <see cref="JsValue.Null"/>.)
    /// </summary>
    private static bool TryParse(Realm realm, string text, int maxDepth, out JsValue value, out JsValue error)
    {
        var p = new JsonParser(realm, text, maxDepth);
        p.SkipWs();
        value = p.ParseValue(0);
        p.SkipWs();
        if (p.Pos != text.Length) p.Fail("Unexpected non-whitespace character after JSON");
        if (p.Error != null) { error = p.ErrorValue(); value = default; return false; }
        error = default;
        return true;
    }

    private struct JsonParser
    {
        private readonly Realm _realm;
        private readonly string _s;
        private readonly int _maxDepth;
        public int Pos;
        private string?[]? _keyCache; // small direct-mapped cache: repeated keys are not re-allocated

        public JsonParser(Realm realm, string s, int maxDepth)
        {
            _realm = realm;
            _s = s;
            _maxDepth = maxDepth;
            Pos = 0;
            _keyCache = null;
        }

        /// <summary>First syntax error seen, with its position; set by <see cref="Fail"/>.</summary>
        public string? Error;
        public int ErrorPos;

        /// <summary>
        /// Records the first syntax error and moves to the end of the input, after which every loop ends and every caller
        /// returns on its own "unexpected end" check: the parse finishes without a CLR throw and its result is discarded.
        /// Each caller returns right after calling this.
        /// </summary>
        public JsValue Fail(string msg)
        {
            if (Error == null) { Error = msg; ErrorPos = Pos; }
            Pos = _s.Length;
            return default;
        }

        private string FailString(string msg) { Fail(msg); return ""; }
        private double FailNumber(string msg) { Fail(msg); return 0; }

        public JsValue ErrorValue() => JsValue.FromObject(JsError.Create(_realm, ErrorKind.SyntaxError, $"{Error} in JSON at position {ErrorPos}"));

        public void SkipWs()
        {
            var s = _s;
            int p = Pos;
            while (p < s.Length)
            {
                char c = s[p];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') p++;
                else break;
            }
            Pos = p;
        }

        /// <summary>When set, each ParseValue leaves its <see cref="JsonParseRecord"/> in <see cref="LastRecord"/>.</summary>
        public bool BuildRecords;
        public JsonParseRecord? LastRecord;
        private List<JsonParseRecord?>? _pendingElements;
        private List<KeyValuePair<string, JsonParseRecord>>? _pendingEntries;
        private bool _pendingDuplicateKeys;

        public JsValue ParseValue(int depth)
        {
            if (!BuildRecords) return ParseValueCore(depth);
            int start = Pos;
            _pendingElements = null;
            _pendingEntries = null;
            _pendingDuplicateKeys = false;
            var v = ParseValueCore(depth);
            LastRecord = new JsonParseRecord { Value = v, Start = start, End = Pos, Elements = _pendingElements, Entries = _pendingEntries, DuplicateKeys = _pendingDuplicateKeys };
            _pendingElements = null;
            _pendingEntries = null;
            _pendingDuplicateKeys = false;
            return v;
        }

        private JsValue ParseValueCore(int depth)
        {
            if (Pos >= _s.Length) return Fail("Unexpected end of JSON input");
            char c = _s[Pos];
            switch (c)
            {
                case '{': return ParseObject(depth + 1);
                case '[': return ParseArray(depth + 1);
                case '"': return new JsValue(ParseString());
                case 't': Expect("true"); return JsValue.True;
                case 'f': Expect("false"); return JsValue.False;
                case 'n': Expect("null"); return JsValue.Null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return new JsValue(ParseNumber());
                    return Fail($"Unexpected token '{c}'");
            }
        }

        private void Expect(string word)
        {
            if (string.CompareOrdinal(_s, Pos, word, 0, word.Length) != 0) { Fail("Unexpected token"); return; }
            Pos += word.Length;
        }

        private JsValue ParseObject(int depth)
        {
            if (depth > _maxDepth || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack()) throw JsErrors.RangeError(_realm, "JSON nesting exceeds the depth limit");
            Pos++;
            var o = _realm.NewObject();
            var entries = BuildRecords ? new List<KeyValuePair<string, JsonParseRecord>>() : null;
            SkipWs();
            if (Pos < _s.Length && _s[Pos] == '}') { Pos++; _pendingEntries = entries; return JsValue.FromObject(o); }
            while (true)
            {
                SkipWs();
                if (Pos >= _s.Length || _s[Pos] != '"') return Fail("Expected property name");
                var key = ParseKey();
                SkipWs();
                if (Pos >= _s.Length || _s[Pos] != ':') return Fail("Expected ':' after property name");
                Pos++;
                SkipWs();
                var v = ParseValue(depth);
                entries?.Add(new KeyValuePair<string, JsonParseRecord>(key, LastRecord!));   // duplicate keys: the last one wins (see FindEntry)
                o.CreateDataProperty(new PropertyKey(key), v);
                _realm.Vm.Tick();
                SkipWs();
                if (Pos >= _s.Length) return Fail("Unexpected end of JSON input");
                char c = _s[Pos++];
                if (c == '}') break;
                if (c != ',') { Pos--; return Fail("Expected ',' or '}' after property value"); }
            }
            _pendingEntries = entries;
            // fewer own properties than entries: a key was repeated (FindEntry then searches instead of walking in order)
            if (entries != null) _pendingDuplicateKeys = entries.Count != (o.Dict != null ? o.Dict.Count : o.Shape.Count);
            return JsValue.FromObject(o);
        }

        private JsValue ParseArray(int depth)
        {
            if (depth > _maxDepth || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack()) throw JsErrors.RangeError(_realm, "JSON nesting exceeds the depth limit");
            Pos++;
            var a = _realm.NewArray();
            var elements = BuildRecords ? new List<JsonParseRecord?>() : null;
            SkipWs();
            if (Pos < _s.Length && _s[Pos] == ']') { Pos++; _pendingElements = elements; return JsValue.FromObject(a); }
            while (true)
            {
                SkipWs();
                a.Push(ParseValue(depth));
                elements?.Add(LastRecord);
                _realm.Vm.Tick();
                SkipWs();
                if (Pos >= _s.Length) return Fail("Unexpected end of JSON input");
                char c = _s[Pos++];
                if (c == ']') break;
                if (c != ',') { Pos--; return Fail("Expected ',' or ']' after array element"); }
            }
            _pendingElements = elements;
            return JsValue.FromObject(a);
        }

        /// <summary>Object key: plain keys (no escapes, short) come from a per-parse cache without allocating.</summary>
        private string ParseKey()
        {
            var s = _s;
            int start = Pos + 1, p = start;
            uint h = 2166136261;
            while (p < s.Length)
            {
                char c = s[p];
                if (c == '"' || c == '\\' || c < 0x20) break;
                h = (h ^ c) * 16777619;
                p++;
            }
            int len = p - start;
            if (p >= s.Length || s[p] != '"' || len > 32) return ParseString();
            var cache = _keyCache ??= new string?[64];
            int slot = (int)((h ^ (uint)len) & 63);
            var hit = cache[slot];
            if (hit != null && hit.Length == len && s.AsSpan(start, len).SequenceEqual(hit))
            {
                Pos = p + 1;
                return hit;
            }
            var key = s.Substring(start, len);
            cache[slot] = key;
            Pos = p + 1;
            return key;
        }

        private string ParseString()
        {
            Pos++;
            int start = Pos;
            var s = _s;
            while (Pos < s.Length)
            {
                char c = s[Pos];
                if (c == '"') { var r = s.Substring(start, Pos - start); Pos++; return Charge(r); }
                if (c == '\\') break;
                if (c < 0x20) return FailString("Bad control character in string literal");
                Pos++;
            }
            var sb = new StringBuilder();
            sb.Append(s, start, Pos - start);
            while (true)
            {
                if (Pos >= s.Length) return FailString("Unterminated string");
                char c = s[Pos++];
                if (c == '"') break;
                if (c < 0x20) { Pos--; return FailString("Bad control character in string literal"); }
                if (c != '\\') { sb.Append(c); continue; }
                if (Pos >= s.Length) return FailString("Bad escaped character");
                char e = s[Pos++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (Pos + 4 > s.Length) return FailString("Bad Unicode escape");
                        int v = 0;
                        for (int i = 0; i < 4; i++)
                        {
                            int d = NumberFormatting.DigitValue(s[Pos + i]);
                            if (d < 0 || d > 15) return FailString("Bad Unicode escape");
                            v = v * 16 + d;
                        }
                        Pos += 4;
                        sb.Append((char)v);
                        break;
                    default: Pos--; return FailString("Bad escaped character");
                }
            }
            return Charge(sb.ToString());
        }

        /// <summary>Parsed string payloads count against the tracked-bytes budget (JSON.parse and host ParseJson).</summary>
        private string Charge(string str) => str.Length < 64 ? str : _realm.Charged(str);

        private double ParseNumber()
        {
            int start = Pos;
            var s = _s;
            if (s[Pos] == '-') Pos++;
            if (Pos >= s.Length) return FailNumber("No number after minus sign");
            if (s[Pos] == '0') Pos++;
            else if (s[Pos] >= '1' && s[Pos] <= '9') { while (Pos < s.Length && char.IsAsciiDigit(s[Pos])) Pos++; }
            else return FailNumber("No number after minus sign");
            bool simple = true;
            if (Pos < s.Length && s[Pos] == '.')
            {
                simple = false;
                Pos++;
                if (Pos >= s.Length || !char.IsAsciiDigit(s[Pos])) return FailNumber("Unterminated fractional number");
                while (Pos < s.Length && char.IsAsciiDigit(s[Pos])) Pos++;
            }
            if (Pos < s.Length && (s[Pos] == 'e' || s[Pos] == 'E'))
            {
                simple = false;
                Pos++;
                if (Pos < s.Length && (s[Pos] == '+' || s[Pos] == '-')) Pos++;
                if (Pos >= s.Length || !char.IsAsciiDigit(s[Pos])) return FailNumber("Exponent part is missing a number");
                while (Pos < s.Length && char.IsAsciiDigit(s[Pos])) Pos++;
            }
            int len = Pos - start;
            if (simple && len <= 9)
            {
                int acc = 0, i = start;
                bool neg = s[i] == '-';
                if (neg) i++;
                for (; i < Pos; i++) acc = acc * 10 + (s[i] - '0');
                return neg ? (acc == 0 ? -0.0 : -acc) : acc;
            }
            return double.Parse(s.AsSpan(start, len), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }

    // ------------------------------------------------------------------ stringify

    public static string? Stringify(Realm realm, JsValue value, JsValue replacer, JsValue space, long maxOutput)
    {
        var st = new StringifyState(realm, maxOutput);
        if (replacer.IsFunction) st.ReplacerFn = replacer;
        else if (ArrayBuiltins.IsArray(realm, replacer))
        {
            var ro = replacer.AsObjectUnchecked;
            long len = Operations.LengthOfArrayLike(realm, ro);
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (long i = 0; i < len; i++)
            {
                var v = ro.GetIndex((uint)i, replacer);
                string? item = null;
                if (v.IsString) item = v.AsStringUnchecked;
                else if (v.IsNumber) item = NumberFormatting.ToString(v.Num);
                else if (v.Ref is JsStringObject or JsPrimitiveWrapper { Value.Tag: JsTag.Number }) item = Operations.ToString(realm, v);
                if (item != null && seen.Add(item)) list.Add(item);
            }
            st.PropertyList = list;
        }
        if (space.IsObject)
        {
            if (space.Ref is JsPrimitiveWrapper { Value.Tag: JsTag.Number }) space = new JsValue(Operations.ToNumber(realm, space));
            else if (space.Ref is JsStringObject) space = new JsValue(Operations.ToString(realm, space));
        }
        if (space.IsNumber)
        {
            int n = (int)Math.Min(10, Math.Max(0, Operations.ToIntegerOrInfinity(realm, space)));
            st.Gap = new string(' ', n);
        }
        else if (space.IsString)
        {
            var s = space.AsStringUnchecked;
            st.Gap = s.Length > 10 ? s.Substring(0, 10) : s;
        }
        var wrapper = realm.NewObject();
        wrapper.CreateDataProperty("", value);
        try
        {
            if (!st.SerializeProperty(wrapper, "", value)) return null;
            return st.Sb.ToString();
        }
        finally { st.Release(); }
    }

    private sealed class StringifyState
    {
        public readonly Realm Realm;
        public readonly StringBuilder Sb = AcquireBuilder();

        // one cached builder per thread (nested stringify calls from toJSON simply allocate their own)
        [ThreadStatic] private static StringBuilder? t_cachedBuilder;

        private static StringBuilder AcquireBuilder()
        {
            var sb = t_cachedBuilder;
            if (sb == null) return new StringBuilder(256);
            t_cachedBuilder = null;
            return sb;
        }

        public void Release()
        {
            if (Sb.Capacity <= 256 * 1024)
            {
                Sb.Clear();
                t_cachedBuilder = Sb;
            }
        }
        public JsValue ReplacerFn;
        public List<string>? PropertyList;
        public string Gap = "";
        private string _indent = "";
        private readonly List<JsObject> _stack = new();
        private readonly long _maxOutput;

        public StringifyState(Realm realm, long maxOutput)
        {
            Realm = realm;
            _maxOutput = maxOutput;
        }

        private int _protoEpoch = -1;
        private bool _objectProtoHasToJson, _arrayProtoHasToJson;

        /// <summary>
        /// toJSON lookup with a fast negative answer for plain objects/arrays whose own layout has no "toJSON" and whose
        /// prototype is the (unmodified-since-check) intrinsic prototype.
        /// </summary>
        private JsValue GetToJson(JsObject o, JsValue value)
        {
            var realm = Realm;
            if (_protoEpoch != realm.ProtoEpoch)
            {
                _protoEpoch = realm.ProtoEpoch;
                _objectProtoHasToJson = realm.ObjectPrototype.HasProperty("toJSON");
                _arrayProtoHasToJson = realm.ArrayPrototype.HasProperty("toJSON");
            }
            if (o.Dict == null && !o.IsExotic && o.Shape.Role == ProtoRole.RealmObjectPrototype && !_objectProtoHasToJson)
            {
                if (o.Shape.IndexOf("toJSON") < 0) return JsValue.Undefined;
            }
            else if (o is JsArray arr && arr.Dict == null && arr.Shape.Role == ProtoRole.RealmArrayPrototype && !_arrayProtoHasToJson)
            {
                if (arr.Shape.Count == 0) return JsValue.Undefined;
            }
            return o.Get("toJSON", value);
        }

        private int _charged;

        /// <summary>Quotes a string, charging and checking a long one before escaping it (its output is at least as long).</summary>
        private void QuoteChecked(string str)
        {
            if (str.Length > 4096)
            {
                // exact quoted length first (escapes can make it up to 6x longer), so limits trip before building it
                long quoted = QuotedLength(str);
                if (Sb.Length + quoted > _maxOutput)
                    throw new JsTerminationException(TerminationKind.OutputBytes, $"JSON output exceeded its limit of {_maxOutput}.", Sb.Length + quoted, _maxOutput);
                if (Sb.Length + quoted > Realm.MaxStringLength) throw JsErrors.RangeError(Realm, "Invalid string length");
                Realm.ChargeOutput(quoted);
                _charged += (int)quoted;
            }
            Quote(Sb, str);
            CheckSize();
        }

        private void CheckSize()
        {
            // output is charged to the tracked-bytes budget (and VM work) as it grows
            if (Sb.Length - _charged >= 4096) { Realm.ChargeOutput(Sb.Length - _charged); _charged = Sb.Length; }
            if (Sb.Length > _maxOutput)
                throw new JsTerminationException(TerminationKind.OutputBytes, $"JSON output exceeded its limit of {_maxOutput}.", Sb.Length, _maxOutput);
            if (Sb.Length > Realm.MaxStringLength) throw JsErrors.RangeError(Realm, "Invalid string length");
        }

        /// <summary>SerializeJSONProperty; returns false when the result is undefined (nothing appended).</summary>
        public bool SerializeProperty(JsObject holder, string? key, JsValue value, uint index = 0)
        {
            // an array element's key string ("0", "1", ...) is only built when toJSON or a replacer asks for it
            var realm = Realm;
            realm.Vm.Tick();
            if (value.IsObject)
            {
                var toJson = GetToJson(value.AsObjectUnchecked, value);
                if (toJson.IsFunction) value = realm.Vm.Call(toJson, value, new[] { new JsValue(key ?? PropertyKey.IndexToString(index)) });
            }
            else if (value.IsBigInt)
            {
                var toJson = realm.BigIntPrototype.Get("toJSON", value);
                if (toJson.IsFunction) value = realm.Vm.Call(toJson, value, new[] { new JsValue(key ?? PropertyKey.IndexToString(index)) });
            }
            if (!ReplacerFn.IsUndefined)
                value = realm.Vm.Call(ReplacerFn, JsValue.FromObject(holder), new[] { new JsValue(key ?? PropertyKey.IndexToString(index)), value });
            if (value.Ref is JsRawJson raw)
            {
                // raw JSON text is emitted verbatim
                Sb.Append(raw.Get("rawJSON").AsStringUnchecked);
                CheckSize();
                return true;
            }
            if (value.IsObject)
            {
                switch (value.Ref)
                {
                    case JsPrimitiveWrapper w when w.Value.IsNumber: value = new JsValue(Operations.ToNumber(realm, value)); break;
                    case JsStringObject: value = new JsValue(Operations.ToString(realm, value)); break;
                    case JsPrimitiveWrapper w when w.Value.IsBoolean: value = w.Value; break;
                    case JsPrimitiveWrapper w when w.Value.IsBigInt: value = w.Value; break;
                }
            }
            switch (value.Tag)
            {
                case JsTag.Null: Sb.Append("null"); CheckSize(); return true;
                case JsTag.Boolean: Sb.Append(value.IsTrue ? "true" : "false"); CheckSize(); return true;
                case JsTag.String: QuoteChecked(value.AsStringUnchecked); return true;
                case JsTag.Number:
                {
                    double d = value.Num;
                    // integers format straight into the builder (no intermediate string)
                    if (d == Math.Floor(d) && Math.Abs(d) < 1e15 && !(d == 0 && double.IsNegative(d))) Sb.Append((long)d);
                    else Sb.Append(double.IsFinite(d) ? NumberFormatting.ToString(d) : "null");
                }
                    CheckSize();
                    return true;
                case JsTag.BigInt:
                    throw JsErrors.TypeError(realm, "Do not know how to serialize a BigInt");
                case JsTag.Object:
                    if (value.Ref is JsFunctionObject) return false;
                    if (ArrayBuiltins.IsArray(realm, value)) SerializeArray(value.AsObjectUnchecked);
                    else SerializeObject(value.AsObjectUnchecked);
                    return true;
                default:
                    return false;
            }
        }

        private void Enter(JsObject o)
        {
            foreach (var s in _stack)
                if (ReferenceEquals(s, o)) throw JsErrors.TypeError(Realm, "Converting circular structure to JSON");
            _stack.Add(o);
            Realm.Vm.EnterNative();
        }

        private void Exit()
        {
            _stack.RemoveAt(_stack.Count - 1);
            Realm.Vm.ExitNative();
        }

        private void SerializeObject(JsObject o)
        {
            Enter(o);
            try
            {
                if (PropertyList == null && ReplacerFn.IsUndefined && Gap.Length == 0 && !o.IsExotic && o.Dict == null
                    && !o.Shape.HasIndexKeys && !o.Shape.HasSymbolKeys && !o.Shape.HasSpecialProps)
                {
                    // plain data properties: iterate the layout directly (no key list, no lookups)
                    var shape = o.Shape;
                    Sb.Append('{');
                    bool first = true;
                    for (int i = 0; i < shape.Count; i++)
                    {
                        if ((shape.Flags[i] & PropFlags.Enumerable) == 0) continue;
                        int mark = Sb.Length;
                        if (!first) Sb.Append(',');
                        Quote(Sb, (string)shape.Keys[i].Value);
                        Sb.Append(':');
                        var key = (string)shape.Keys[i].Value;
                        // keys were fixed at the start; if a toJSON/getter changed the layout, read through [[Get]]
                        var v = ReferenceEquals(o.Shape, shape) ? o.Slots[i] : o.Get(key);
                        if (!SerializeProperty(o, key, v)) { Sb.Length = mark; continue; }
                        first = false;
                    }
                    Sb.Append('}');
                    CheckSize();
                    return;
                }
                string stepback = _indent;
                _indent += Gap;
                IEnumerable<string> keys;
                if (PropertyList != null) keys = PropertyList;
                else
                {
                    var list = new List<string>();
                    if (!o.IsExotic && o.Dict == null && !o.Shape.HasIndexKeys)
                    {
                        var shape = o.Shape;
                        for (int i = 0; i < shape.Count; i++)
                            if (shape.Keys[i].Value is string s && (shape.Flags[i] & PropFlags.Enumerable) != 0) list.Add(s);
                    }
                    else
                    {
                        var pk = new List<PropertyKey>();
                        o.OwnPropertyKeys(pk);
                        foreach (var k in pk)
                            if (k.Value is string s && o.GetOwnProperty(k, out var d) && d.Enumerable) list.Add(s);
                    }
                    keys = list;
                }
                Sb.Append('{');
                bool any = false;
                var self = JsValue.FromObject(o);
                foreach (var k in keys)
                {
                    int mark = Sb.Length;
                    if (any) Sb.Append(',');
                    if (Gap.Length > 0) Sb.Append('\n').Append(_indent);
                    Quote(Sb, k);
                    Sb.Append(':');
                    if (Gap.Length > 0) Sb.Append(' ');
                    var v = o.Get(new PropertyKey(k), self);
                    if (!SerializeProperty(o, k, v)) { Sb.Length = mark; continue; }
                    any = true;
                }
                if (any && Gap.Length > 0) Sb.Append('\n').Append(stepback);
                Sb.Append('}');
                _indent = stepback;
                CheckSize();
            }
            finally { Exit(); }
        }

        private void SerializeArray(JsObject o)
        {
            Enter(o);
            try
            {
                string stepback = _indent;
                _indent += Gap;
                long len = Operations.LengthOfArrayLike(Realm, o);
                Sb.Append('[');
                var self = JsValue.FromObject(o);
                for (long i = 0; i < len; i++)
                {
                    // per element: a holey array of length 2**32-1 must hit the output/tracked-bytes limits as it grows
                    CheckSize();
                    Realm.Vm.Tick();
                    if (i > 0) Sb.Append(',');
                    if (Gap.Length > 0) Sb.Append('\n').Append(_indent);
                    JsValue v;
                    if (!(o is JsArray da && da.TryGetDense((uint)i, out v))) v = o.GetIndex((uint)i, self);
                    if (!SerializeProperty(o, null, v, (uint)i)) Sb.Append("null");
                }
                if (len > 0 && Gap.Length > 0) Sb.Append('\n').Append(stepback);
                Sb.Append(']');
                _indent = stepback;
                CheckSize();
            }
            finally { Exit(); }
        }
    }

    /// <summary>Length of <see cref="Quote"/>'s output for <paramref name="s"/>.</summary>
    private static long QuotedLength(string s)
    {
        long n = s.Length + 2;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c < 0x20) n += c is '\b' or '\f' or '\n' or '\r' or '\t' ? 1 : 5;
            else if (c == '"' || c == '\\') n += 1;
            else if (char.IsSurrogate(c))
            {
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
                else n += 5;   // lone surrogate: \uXXXX
            }
        }
        return n;
    }

    public static void Quote(StringBuilder sb, string s)
    {
        // Printable ASCII without quote or backslash (nearly every key and most values) needs no escaping: one append.
        var span = s.AsSpan();
        if (span.IndexOfAnyExceptInRange((char)0x20, (char)0x7E) < 0 && span.IndexOfAny('"', '\\') < 0)
        {
            sb.Append('"').Append(s).Append('"');
            return;
        }
        sb.Append('"');
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            string? esc = null;
            if (c < 0x20 || c == '"' || c == '\\')
            {
                esc = c switch
                {
                    '"' => "\\\"",
                    '\\' => "\\\\",
                    '\b' => "\\b",
                    '\f' => "\\f",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _ => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
                };
            }
            else if (char.IsSurrogate(c))
            {
                bool paired = char.IsHighSurrogate(c) ? i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) : i > 0 && char.IsHighSurrogate(s[i - 1]);
                if (!paired) esc = "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture);
            }
            if (esc != null)
            {
                sb.Append(s, start, i - start).Append(esc);
                start = i + 1;
            }
        }
        sb.Append(s, start, s.Length - start);
        sb.Append('"');
    }
}

/// <summary>JSON Parse Record: a parsed value, its source range and the records of its elements / entries.</summary>
internal sealed class JsonParseRecord
{
    public JsValue Value;
    public int Start, End;
    public List<JsonParseRecord?>? Elements;
    public List<KeyValuePair<string, JsonParseRecord>>? Entries;
    public bool DuplicateKeys;
}

/// <summary>Object created by JSON.rawJSON ([[IsRawJSON]]): null prototype, frozen, single "rawJSON" property.</summary>
internal sealed class JsRawJson : JsObject
{
    public JsRawJson(Realm realm) : base(realm, null) { }
}
