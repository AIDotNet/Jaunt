using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Jaunt.Runtime;

namespace Jaunt.Runtime;

/// <summary>Creates the intrinsic objects and global bindings of a realm.</summary>
internal enum IntrinsicGroup
{
    Object, Function, Error, Symbol, Iterator, Array, String, Number, Math, Json, MapSet, Promise, RegExp, Date, Reflect, Proxy, TypedArray, Globals, BigInt, FunctionKinds, Temporal, Intl, ShadowRealm,
    Count_,
}

/// <summary>
/// Creates the intrinsic objects and global bindings of a realm. Only the Object/Function core is created
/// eagerly; every other builtin group is materialized on first use (internal reference or first lookup of one of
/// its global names), which keeps FreshRealm creation cheap. Semantics are unchanged: a group's globals exist
/// before any user code can observe them.
/// </summary>
internal static class Intrinsics
{
    internal static readonly Dictionary<string, IntrinsicGroup> GlobalGroups = BuildGlobalGroups();

    private static Dictionary<string, IntrinsicGroup> BuildGlobalGroups()
    {
        var d = new Dictionary<string, IntrinsicGroup>(StringComparer.Ordinal)
        {
            ["Error"] = IntrinsicGroup.Error, ["TypeError"] = IntrinsicGroup.Error, ["RangeError"] = IntrinsicGroup.Error,
            ["ReferenceError"] = IntrinsicGroup.Error, ["SyntaxError"] = IntrinsicGroup.Error, ["EvalError"] = IntrinsicGroup.Error,
            ["URIError"] = IntrinsicGroup.Error, ["AggregateError"] = IntrinsicGroup.Error, ["SuppressedError"] = IntrinsicGroup.Error,
            ["DisposableStack"] = IntrinsicGroup.Iterator, ["AsyncDisposableStack"] = IntrinsicGroup.Iterator,
            ["Symbol"] = IntrinsicGroup.Symbol,
            ["Array"] = IntrinsicGroup.Array,
            ["String"] = IntrinsicGroup.String,
            ["Number"] = IntrinsicGroup.Number, ["Boolean"] = IntrinsicGroup.Number,
            ["Math"] = IntrinsicGroup.Math,
            ["JSON"] = IntrinsicGroup.Json,
            ["Map"] = IntrinsicGroup.MapSet, ["Set"] = IntrinsicGroup.MapSet, ["WeakMap"] = IntrinsicGroup.MapSet, ["WeakSet"] = IntrinsicGroup.MapSet,
            ["Promise"] = IntrinsicGroup.Promise,
            ["RegExp"] = IntrinsicGroup.RegExp,
            ["Date"] = IntrinsicGroup.Date,
            ["Reflect"] = IntrinsicGroup.Reflect,
            ["Proxy"] = IntrinsicGroup.Proxy,
            ["BigInt"] = IntrinsicGroup.BigInt,
            ["Iterator"] = IntrinsicGroup.Iterator,
            ["WeakRef"] = IntrinsicGroup.MapSet, ["FinalizationRegistry"] = IntrinsicGroup.MapSet,
            ["ArrayBuffer"] = IntrinsicGroup.TypedArray, ["DataView"] = IntrinsicGroup.TypedArray,
            ["SharedArrayBuffer"] = IntrinsicGroup.TypedArray, ["Atomics"] = IntrinsicGroup.TypedArray,
            ["parseInt"] = IntrinsicGroup.Globals, ["parseFloat"] = IntrinsicGroup.Globals, ["isNaN"] = IntrinsicGroup.Globals,
            ["isFinite"] = IntrinsicGroup.Globals, ["eval"] = IntrinsicGroup.Globals, ["encodeURIComponent"] = IntrinsicGroup.Globals,
            ["encodeURI"] = IntrinsicGroup.Globals, ["decodeURIComponent"] = IntrinsicGroup.Globals, ["decodeURI"] = IntrinsicGroup.Globals,
            ["escape"] = IntrinsicGroup.Globals, ["unescape"] = IntrinsicGroup.Globals,
            ["Temporal"] = IntrinsicGroup.Temporal,
            ["Intl"] = IntrinsicGroup.Intl,
            ["ShadowRealm"] = IntrinsicGroup.ShadowRealm,
        };
        foreach (var k in Enum.GetValues<TypedKind>()) d[k + "Array"] = IntrinsicGroup.TypedArray;
        return d;
    }

    public static void Setup(Realm r)
    {
        r.ObjectPrototype = new JsImmutableProtoObject(r);
        r.ObjectPrototype.IsPrototypeObject = true;
        r.ObjectRootShape = Shape.SharedObjectRoot;
        r.IterResultShape = Shape.SharedIterResultShape;
        r.FunctionPrototype = new JsBuiltin(r, "", 0, static (in NativeArgs a) => JsValue.Undefined, proto: r.ObjectPrototype);
        r.FunctionPrototype.IsPrototypeObject = true;

        var global = new JsGlobalObject(r);
        r.GlobalObject = global;
        r.MarkGroupInitialized(IntrinsicGroup.Object);
        r.MarkGroupInitialized(IntrinsicGroup.Function);
        ObjectBuiltins.Setup(r);
        FunctionBuiltins.Setup(r);

        global.DefineHidden("globalThis", JsValue.FromObject(global));
        global.AddOwnProperty("undefined", JsValue.Undefined, PropFlags.None);
        global.AddOwnProperty("NaN", JsValue.NaN, PropFlags.None);
        global.AddOwnProperty("Infinity", new JsValue(double.PositiveInfinity), PropFlags.None);
        r.GlobalVersion++;
    }

    public static void SetupGroup(Realm r, IntrinsicGroup g)
    {
        switch (g)
        {
            case IntrinsicGroup.Error: ErrorBuiltins.Setup(r); break;
            case IntrinsicGroup.Symbol: SymbolBuiltins.Setup(r); break;
            case IntrinsicGroup.Iterator: IteratorBuiltins.Setup(r); break;
            case IntrinsicGroup.Array: ArrayBuiltins.Setup(r); break;
            case IntrinsicGroup.String: StringBuiltins.Setup(r); break;
            case IntrinsicGroup.Number: NumberBuiltins.Setup(r); break;
            case IntrinsicGroup.Math: MathBuiltins.Setup(r); break;
            case IntrinsicGroup.Json: JsonBuiltins.Setup(r); break;
            case IntrinsicGroup.MapSet: MapSetBuiltins.Setup(r); break;
            case IntrinsicGroup.Promise: PromiseBuiltins.Setup(r); break;
            case IntrinsicGroup.RegExp: RegExpBuiltins.Setup(r); break;
            case IntrinsicGroup.Date: DateBuiltins.Setup(r); break;
            case IntrinsicGroup.Reflect: ReflectBuiltins.Setup(r); break;
            case IntrinsicGroup.Proxy: ProxyBuiltins.Setup(r); break;
            case IntrinsicGroup.TypedArray: TypedArrayBuiltins.Setup(r); break;
            case IntrinsicGroup.Globals: GlobalFunctions.Setup(r); break;
            case IntrinsicGroup.BigInt: BigIntBuiltins.Setup(r); break;
            case IntrinsicGroup.FunctionKinds: FunctionBuiltins.SetupFunctionKinds(r); break;
            case IntrinsicGroup.Temporal: TemporalBuiltins.Setup(r); break;
            case IntrinsicGroup.Intl: IntlBuiltins.Setup(r); break;
            case IntrinsicGroup.ShadowRealm: ShadowRealms.Setup(r); break;
        }
    }

    /// <summary>Hardened profile: freeze intrinsics; dynamic code generation is rejected by eval/Function.</summary>
    public static void Harden(Realm r)
    {
        r.EnsureAllGroups();
        var seen = new HashSet<JsObject>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<JsObject>();
        var keys = new List<PropertyKey>();
        foreach (var k in GlobalNames(r))
        {
            var v = r.GlobalObject.Get(k);
            if (v.IsObject) stack.Push(v.AsObjectUnchecked);
        }
        stack.Push(r.ObjectPrototype);
        stack.Push(r.FunctionPrototype);
        // intrinsics reachable only through instances
        stack.Push(r.GeneratorFunctionPrototype);
        stack.Push(r.AsyncFunctionPrototype);
        stack.Push(r.AsyncGeneratorFunctionPrototype);
        stack.Push(r.AsyncFromSyncIteratorPrototype);
        if (r.IteratorHelperPrototype != null) stack.Push(r.IteratorHelperPrototype);
        if (r.WrapForValidIteratorPrototype != null) stack.Push(r.WrapForValidIteratorPrototype);
        while (stack.Count > 0)
        {
            var o = stack.Pop();
            if (!seen.Add(o)) continue;
            if (ReferenceEquals(o, r.GlobalObject)) continue;
            keys.Clear();
            o.OwnPropertyKeys(keys);
            foreach (var key in keys)
            {
                if (!o.GetOwnProperty(key, out var d)) continue;
                if (d.IsAccessor)
                {
                    if (d.Getter != null) stack.Push(d.Getter);
                    if (d.Setter != null) stack.Push(d.Setter);
                }
                else if (d.Value.IsObject) stack.Push(d.Value.AsObjectUnchecked);
            }
            var p = o.GetPrototypeOf();
            if (p != null) stack.Push(p);
            ObjectBuiltins.SetIntegrityLevel(r, o, frozen: true);
        }
    }

    private static List<PropertyKey> GlobalNames(Realm r)
    {
        var keys = new List<PropertyKey>();
        r.GlobalObject.OwnPropertyKeys(keys);
        return keys;
    }
}

/// <summary>Global object: materializes lazily initialized builtin groups when their names are touched.</summary>
internal sealed class JsGlobalObject : JsObject
{
    public JsGlobalObject(Realm realm) : base(realm, realm.ObjectPrototype)
    {
        ConvertToDictionary();
    }

    public override string ClassName => "global";

    private void Ensure(in PropertyKey key)
    {
        if (key.Value is string s) Realm.EnsureGlobal(s);
    }

    public override bool GetOwnProperty(in PropertyKey key, out PropertyDescriptor desc) { Ensure(key); return base.GetOwnProperty(key, out desc); }
    public override bool DefineOwnProperty(in PropertyKey key, in PropertyDescriptor desc) { Ensure(key); return base.DefineOwnProperty(key, desc); }
    public override bool HasProperty(in PropertyKey key) { Ensure(key); return base.HasProperty(key); }
    public override JsValue Get(in PropertyKey key, JsValue receiver) { Ensure(key); return base.Get(key, receiver); }
    public override bool Set(in PropertyKey key, JsValue value, JsValue receiver) { Ensure(key); return base.Set(key, value, receiver); }
    public override bool Delete(in PropertyKey key) { Ensure(key); return base.Delete(key); }
    public override void OwnPropertyKeys(List<PropertyKey> keys) { Realm.EnsureAllGroups(); base.OwnPropertyKeys(keys); }
}

/// <summary>%Object.prototype% is an immutable prototype exotic object.</summary>
internal sealed class JsImmutableProtoObject : JsObject
{
    public JsImmutableProtoObject(Realm realm) : base(realm, (JsObject?)null) { }
    public override bool SetPrototypeOf(JsObject? proto) => ReferenceEquals(proto, GetPrototypeOf());
}

internal static class GlobalFunctions
{
    public static void Setup(Realm r)
    {
        var g = r.GlobalObject;
        r.Method(g, "parseInt", 2, static (in NativeArgs a) =>
        {
            var s = Operations.ToString(a.Realm, a[0]);
            int radix = Operations.ToInt32(a.Realm, a[1]);
            return new JsValue(NumberFormatting.ParseInt(s, radix));
        });
        r.Method(g, "parseFloat", 1, static (in NativeArgs a) => new JsValue(NumberFormatting.ParseFloat(Operations.ToString(a.Realm, a[0]))));
        r.Method(g, "isNaN", 1, static (in NativeArgs a) => JsValue.FromBool(double.IsNaN(Operations.ToNumber(a.Realm, a[0]))));
        r.Method(g, "isFinite", 1, static (in NativeArgs a) => JsValue.FromBool(double.IsFinite(Operations.ToNumber(a.Realm, a[0]))));
        r.EvalFunction = r.MethodEager(g, "eval", 1, static (in NativeArgs a) => Eval(a.Realm, a[0]));
        r.Method(g, "encodeURIComponent", 1, static (in NativeArgs a) => new JsValue(Encode(a.Realm, Operations.ToString(a.Realm, a[0]), UriUnreservedComponent)));
        r.Method(g, "encodeURI", 1, static (in NativeArgs a) => new JsValue(Encode(a.Realm, Operations.ToString(a.Realm, a[0]), UriUnreservedFull)));
        r.Method(g, "decodeURIComponent", 1, static (in NativeArgs a) => new JsValue(Decode(a.Realm, Operations.ToString(a.Realm, a[0]), "")));
        r.Method(g, "decodeURI", 1, static (in NativeArgs a) => new JsValue(Decode(a.Realm, Operations.ToString(a.Realm, a[0]), ";/?:@&=+$,#")));
        // Annex B
        r.Method(g, "escape", 1, static (in NativeArgs a) =>
        {
            var s = Operations.ToString(a.Realm, a[0]);
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsAsciiLetterOrDigit(c) || "@*_+-./".IndexOf(c) >= 0) sb.Append(c);
                else if (c < 256) sb.Append('%').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
                else sb.Append("%u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            }
            return new JsValue(sb.ToString());
        });
        r.Method(g, "unescape", 1, static (in NativeArgs a) =>
        {
            var s = Operations.ToString(a.Realm, a[0]);
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '%')
                {
                    if (i + 5 < s.Length && s[i + 1] == 'u' && TryHex(s, i + 2, 4, out int u)) { sb.Append((char)u); i += 5; continue; }
                    if (i + 2 < s.Length && TryHex(s, i + 1, 2, out int b)) { sb.Append((char)b); i += 2; continue; }
                }
                sb.Append(c);
            }
            return new JsValue(sb.ToString());
        });
    }

    private static bool TryHex(string s, int start, int count, out int value)
    {
        value = 0;
        for (int i = 0; i < count; i++)
        {
            int d = NumberFormatting.DigitValue(s[start + i]);
            if (d < 0 || d > 15) return false;
            value = value * 16 + d;
        }
        return true;
    }

    /// <summary>Indirect (global) eval. Direct calls <c>eval(...)</c> are handled by the CallEval opcode.</summary>
    internal static JsValue Eval(Realm realm, JsValue x)
    {
        // indirect eval runs in the realm of the eval function, not the caller's
        var vm = realm.Vm;
        var saved = vm.Realm;
        vm.Realm = realm;
        try { return vm.PerformEval(x, null, strictCaller: false, allowNewTarget: false); }
        finally { vm.Realm = saved; }
    }

    private const string UriUnreservedComponent = "-_.!~*'()";
    private const string UriUnreservedFull = "-_.!~*'();/?:@&=+$,#";

    private static string Encode(Realm realm, string s, string unescaped)
    {
        var sb = new StringBuilder(s.Length);
        Span<byte> buf = stackalloc byte[4];
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsAsciiLetterOrDigit(c) || unescaped.IndexOf(c) >= 0) { sb.Append(c); continue; }
            int cp;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) throw JsErrors.UriError(realm, "URI malformed");
                cp = char.ConvertToUtf32(c, s[++i]);
            }
            else if (char.IsLowSurrogate(c)) throw JsErrors.UriError(realm, "URI malformed");
            else cp = c;
            int n = new Rune(cp).EncodeToUtf8(buf);
            for (int j = 0; j < n; j++) sb.Append('%').Append(buf[j].ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static string Decode(Realm realm, string s, string reserved)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c != '%') { sb.Append(c); continue; }
            int start = i;
            int b = HexByte(realm, s, i);
            i += 2;
            if (b < 0x80)
            {
                char ch = (char)b;
                if (reserved.IndexOf(ch) >= 0) sb.Append(s, start, 3);
                else sb.Append(ch);
                continue;
            }
            int n = (b & 0xE0) == 0xC0 ? 2 : (b & 0xF0) == 0xE0 ? 3 : (b & 0xF8) == 0xF0 ? 4 : 0;
            if (n == 0) throw JsErrors.UriError(realm, "URI malformed");
            var bytes = new byte[n];
            bytes[0] = (byte)b;
            for (int j = 1; j < n; j++)
            {
                if (i + 1 >= s.Length || s[i + 1] != '%') throw JsErrors.UriError(realm, "URI malformed");
                int nb = HexByte(realm, s, i + 1);
                if ((nb & 0xC0) != 0x80) throw JsErrors.UriError(realm, "URI malformed");
                bytes[j] = (byte)nb;
                i += 3;
            }
            if (Rune.DecodeFromUtf8(bytes, out var rune, out int consumed) != System.Buffers.OperationStatus.Done || consumed != n)
                throw JsErrors.UriError(realm, "URI malformed");
            sb.Append(rune.ToString());
        }
        return sb.ToString();
    }

    private static int HexByte(Realm realm, string s, int i)
    {
        if (i + 2 >= s.Length) throw JsErrors.UriError(realm, "URI malformed");
        int h = NumberFormatting.DigitValue(s[i + 1]), l = NumberFormatting.DigitValue(s[i + 2]);
        if (h < 0 || h > 15 || l < 0 || l > 15) throw JsErrors.UriError(realm, "URI malformed");
        return h * 16 + l;
    }
}
