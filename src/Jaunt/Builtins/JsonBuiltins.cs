using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

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
                // reviver: keep parse records so the reviver's context can expose primitive source text
                var p = new JsonParser(realm, text, 512) { BuildRecords = true };
                p.SkipWs();
                var pv = p.ParseValue(0);
                var rec = p.LastRecord;
                p.SkipWs();
                if (p.Pos < text.Length) p.Fail($"Unexpected token '{text[p.Pos]}'");
                var root = realm.NewObject();
                root.CreateDataProperty("", pv);
                return Internalize(realm, root, "", a[1], rec, text);
            }
            return Parse(realm, text, maxDepth: 512);
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

    private static JsValue Internalize(Realm realm, JsObject holder, string name, JsValue reviver, JsonParseRecord? record, string source)
    {
        var val = holder.Get(name);
        // InternalizeJSONProperty (JSON.parse source text access): records apply only while the value is unchanged
        var context = realm.NewObject();
        List<JsonParseRecord?>? elements = null;
        Dictionary<string, JsonParseRecord>? entries = null;
        if (record != null && Operations.SameValue(record.Value, val))
        {
            if (!val.IsObject) context.CreateDataProperty("source", new JsValue(source.Substring(record.Start, record.End - record.Start)));
            elements = record.Elements;
            entries = record.Entries;
        }
        if (val.IsObject)
        {
            var o = val.AsObjectUnchecked;
            if (ArrayBuiltins.IsArray(realm, val))
            {
                long len = Operations.LengthOfArrayLike(realm, o);
                for (long i = 0; i < len; i++)
                    Revive(realm, o, PropertyKey.IndexToString((uint)i), reviver, elements != null && i < elements.Count ? elements[(int)i] : null, source);
            }
            else
            {
                // EnumerableOwnProperties is computed once, before any reviver call
                var keys = new List<PropertyKey>();
                o.OwnPropertyKeys(keys);
                var names = new List<string>(keys.Count);
                foreach (var k in keys)
                    if (k.Value is string ks && o.GetOwnProperty(k, out var d) && d.Enumerable) names.Add(ks);
                foreach (var ks in names)
                    Revive(realm, o, ks, reviver, entries != null && entries.TryGetValue(ks, out var er) ? er : null, source);
            }
        }
        return realm.Vm.Call(reviver, JsValue.FromObject(holder), new[] { new JsValue(name), val, JsValue.FromObject(context) });
    }

    private static bool IsJsonWs(char c) => c is ' ' or '\t' or '\n' or '\r';

    private static void Revive(Realm realm, JsObject o, string key, JsValue reviver, JsonParseRecord? record, string source)
    {
        realm.Vm.EnterNative();
        try
        {
            var nv = Internalize(realm, o, key, reviver, record, source);
            if (nv.IsUndefined) o.Delete(key);
            else o.CreateDataProperty(key, nv);
        }
        finally { realm.Vm.ExitNative(); }
    }

    // ------------------------------------------------------------------ parse

    public static JsValue Parse(Realm realm, string text, int maxDepth)
    {
        var p = new JsonParser(realm, text, maxDepth);
        p.SkipWs();
        var v = p.ParseValue(0);
        p.SkipWs();
        if (p.Pos != text.Length) p.Fail("Unexpected non-whitespace character after JSON");
        return v;
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

        public void Fail(string msg) => throw JsErrors.SyntaxError(_realm, $"{msg} in JSON at position {Pos}");

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
        private Dictionary<string, JsonParseRecord>? _pendingEntries;

        public JsValue ParseValue(int depth)
        {
            if (!BuildRecords) return ParseValueCore(depth);
            int start = Pos;
            _pendingElements = null;
            _pendingEntries = null;
            var v = ParseValueCore(depth);
            LastRecord = new JsonParseRecord { Value = v, Start = start, End = Pos, Elements = _pendingElements, Entries = _pendingEntries };
            _pendingElements = null;
            _pendingEntries = null;
            return v;
        }

        private JsValue ParseValueCore(int depth)
        {
            if (Pos >= _s.Length) Fail("Unexpected end of JSON input");
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
                    Fail($"Unexpected token '{c}'");
                    return default;
            }
        }

        private void Expect(string word)
        {
            if (string.CompareOrdinal(_s, Pos, word, 0, word.Length) != 0) Fail("Unexpected token");
            Pos += word.Length;
        }

        private JsValue ParseObject(int depth)
        {
            if (depth > _maxDepth || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack()) throw JsErrors.RangeError(_realm, "JSON nesting exceeds the depth limit");
            Pos++;
            var o = _realm.NewObject();
            var entries = BuildRecords ? new Dictionary<string, JsonParseRecord>(StringComparer.Ordinal) : null;
            SkipWs();
            if (Pos < _s.Length && _s[Pos] == '}') { Pos++; _pendingEntries = entries; return JsValue.FromObject(o); }
            while (true)
            {
                SkipWs();
                if (Pos >= _s.Length || _s[Pos] != '"') Fail("Expected property name");
                var key = ParseKey();
                SkipWs();
                if (Pos >= _s.Length || _s[Pos] != ':') Fail("Expected ':' after property name");
                Pos++;
                SkipWs();
                var v = ParseValue(depth);
                if (entries != null) entries[key] = LastRecord!;   // duplicate keys: the last one wins
                o.CreateDataProperty(new PropertyKey(key), v);
                _realm.Vm.Tick();
                SkipWs();
                if (Pos >= _s.Length) Fail("Unexpected end of JSON input");
                char c = _s[Pos++];
                if (c == '}') break;
                if (c != ',') { Pos--; Fail("Expected ',' or '}' after property value"); }
            }
            _pendingEntries = entries;
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
                if (Pos >= _s.Length) Fail("Unexpected end of JSON input");
                char c = _s[Pos++];
                if (c == ']') break;
                if (c != ',') { Pos--; Fail("Expected ',' or ']' after array element"); }
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
                if (c == '"') { var r = s.Substring(start, Pos - start); Pos++; return r; }
                if (c == '\\') break;
                if (c < 0x20) Fail("Bad control character in string literal");
                Pos++;
            }
            var sb = new StringBuilder();
            sb.Append(s, start, Pos - start);
            while (true)
            {
                if (Pos >= s.Length) Fail("Unterminated string");
                char c = s[Pos++];
                if (c == '"') break;
                if (c < 0x20) { Pos--; Fail("Bad control character in string literal"); }
                if (c != '\\') { sb.Append(c); continue; }
                if (Pos >= s.Length) Fail("Bad escaped character");
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
                        if (Pos + 4 > s.Length) Fail("Bad Unicode escape");
                        int v = 0;
                        for (int i = 0; i < 4; i++)
                        {
                            int d = NumberFormatting.DigitValue(s[Pos + i]);
                            if (d < 0 || d > 15) Fail("Bad Unicode escape");
                            v = v * 16 + d;
                        }
                        Pos += 4;
                        sb.Append((char)v);
                        break;
                    default: Pos--; Fail("Bad escaped character"); break;
                }
            }
            return sb.ToString();
        }

        private double ParseNumber()
        {
            int start = Pos;
            var s = _s;
            if (s[Pos] == '-') Pos++;
            if (Pos >= s.Length) Fail("No number after minus sign");
            if (s[Pos] == '0') Pos++;
            else if (s[Pos] >= '1' && s[Pos] <= '9') { while (Pos < s.Length && char.IsAsciiDigit(s[Pos])) Pos++; }
            else Fail("No number after minus sign");
            bool simple = true;
            if (Pos < s.Length && s[Pos] == '.')
            {
                simple = false;
                Pos++;
                if (Pos >= s.Length || !char.IsAsciiDigit(s[Pos])) Fail("Unterminated fractional number");
                while (Pos < s.Length && char.IsAsciiDigit(s[Pos])) Pos++;
            }
            if (Pos < s.Length && (s[Pos] == 'e' || s[Pos] == 'E'))
            {
                simple = false;
                Pos++;
                if (Pos < s.Length && (s[Pos] == '+' || s[Pos] == '-')) Pos++;
                if (Pos >= s.Length || !char.IsAsciiDigit(s[Pos])) Fail("Exponent part is missing a number");
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

        private void CheckSize()
        {
            // output is charged to the tracked-bytes budget (and VM work) as it grows
            if (Sb.Length - _charged >= 4096) { Realm.ChargeOutput(Sb.Length - _charged); _charged = Sb.Length; }
            if (Sb.Length > _maxOutput)
                throw new JsTerminationException(TerminationKind.OutputBytes, $"JSON output exceeded its limit of {_maxOutput}.", Sb.Length, _maxOutput);
            if (Sb.Length > Realm.MaxStringLength) throw JsErrors.RangeError(Realm, "Invalid string length");
        }

        /// <summary>SerializeJSONProperty; returns false when the result is undefined (nothing appended).</summary>
        public bool SerializeProperty(JsObject holder, string key, JsValue value)
        {
            var realm = Realm;
            realm.Vm.Tick();
            if (value.IsObject)
            {
                var toJson = GetToJson(value.AsObjectUnchecked, value);
                if (toJson.IsFunction) value = realm.Vm.Call(toJson, value, new[] { new JsValue(key) });
            }
            else if (value.IsBigInt)
            {
                var toJson = realm.BigIntPrototype.Get("toJSON", value);
                if (toJson.IsFunction) value = realm.Vm.Call(toJson, value, new[] { new JsValue(key) });
            }
            if (!ReplacerFn.IsUndefined)
                value = realm.Vm.Call(ReplacerFn, JsValue.FromObject(holder), new[] { new JsValue(key), value });
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
                case JsTag.String: Quote(Sb, value.AsStringUnchecked); CheckSize(); return true;
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
                    if (i > 0) Sb.Append(',');
                    if (Gap.Length > 0) Sb.Append('\n').Append(_indent);
                    var v = o.GetIndex((uint)i, self);
                    if (!SerializeProperty(o, PropertyKey.IndexToString((uint)i), v)) Sb.Append("null");
                }
                if (len > 0 && Gap.Length > 0) Sb.Append('\n').Append(stepback);
                Sb.Append(']');
                _indent = stepback;
                CheckSize();
            }
            finally { Exit(); }
        }
    }

    public static void Quote(StringBuilder sb, string s)
    {
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
    public Dictionary<string, JsonParseRecord>? Entries;
}

/// <summary>Object created by JSON.rawJSON ([[IsRawJSON]]): null prototype, frozen, single "rawJSON" property.</summary>
internal sealed class JsRawJson : JsObject
{
    public JsRawJson(Realm realm) : base(realm, null) { }
}
