using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace Jaunt.Runtime;

/// <summary>Recently standardized library additions: Array.fromAsync, Uint8Array base64/hex, Map/WeakMap getOrInsert.</summary>
internal static class Es2026Builtins
{
    // ------------------------------------------------------------------ Map / WeakMap upsert

    public static void InstallMap(Realm r, JsObject mapProto)
    {
        r.Method(mapProto, "getOrInsert", 2, static (in NativeArgs a) =>
        {
            var m = MapSetBuiltins.ThisMap(a, false, "getOrInsert");
            var key = a[0].IsNumber && a[0].Num == 0 ? JsValue.Zero : a[0];
            if (m.Table.TryGet(key, out var v)) return v;
            m.Table.Set(a.Realm, key, a[1]);
            return a[1];
        });
        r.Method(mapProto, "getOrInsertComputed", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var m = MapSetBuiltins.ThisMap(a, false, "getOrInsertComputed");
            if (!a[1].IsFunction) throw JsErrors.TypeError(realm, $"{JsErrors.Describe(a[1])} is not a function");
            var key = a[0].IsNumber && a[0].Num == 0 ? JsValue.Zero : a[0];
            if (m.Table.TryGet(key, out var v)) return v;
            var value = realm.Vm.Call(a[1], JsValue.Undefined, new ReadOnlySpan<JsValue>(in key));
            m.Table.Set(realm, key, value); // the callback may have inserted the key: overwrite
            return value;
        });
    }

    public static void InstallWeakMap(Realm r, JsObject wmProto)
    {
        r.Method(wmProto, "getOrInsert", 2, static (in NativeArgs a) =>
        {
            var w = MapSetBuiltins.ThisWeak(a, false);
            var key = MapSetBuiltins.WeakKey(a, a[0]);
            if (w.Table.TryGetValue(key, out var box)) return box.Value;
            w.Table.AddOrUpdate(key, new StrongBox<JsValue>(a[1]));
            return a[1];
        });
        r.Method(wmProto, "getOrInsertComputed", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var w = MapSetBuiltins.ThisWeak(a, false);
            var key = MapSetBuiltins.WeakKey(a, a[0]);
            if (!a[1].IsFunction) throw JsErrors.TypeError(realm, $"{JsErrors.Describe(a[1])} is not a function");
            if (w.Table.TryGetValue(key, out var box)) return box.Value;
            var kv = a[0];
            var value = realm.Vm.Call(a[1], JsValue.Undefined, new ReadOnlySpan<JsValue>(in kv));
            w.Table.AddOrUpdate(key, new StrongBox<JsValue>(value));
            return value;
        });
    }

    // ------------------------------------------------------------------ Uint8Array base64 / hex

    private const string Std = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    private const string Url = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    public static void InstallUint8Array(Realm r, JsObject ctor, JsObject proto)
    {
        r.Method(ctor, "fromBase64", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (!a[0].IsString) throw JsErrors.TypeError(realm, "Uint8Array.fromBase64 requires a string");
            var opts = GetOptions(realm, a[1]);
            bool url = GetAlphabet(realm, opts);
            int handling = GetLastChunkHandling(realm, opts);
            var (_, bytes, error) = FromBase64(a[0].AsStringUnchecked, url, handling, long.MaxValue);
            if (error) throw JsErrors.SyntaxError(realm, "Invalid base64 string");
            return JsValue.FromObject(NewUint8(realm, bytes));
        });
        r.Method(ctor, "fromHex", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (!a[0].IsString) throw JsErrors.TypeError(realm, "Uint8Array.fromHex requires a string");
            var (_, bytes, error) = FromHex(a[0].AsStringUnchecked, long.MaxValue);
            if (error) throw JsErrors.SyntaxError(realm, "Invalid hex string");
            return JsValue.FromObject(NewUint8(realm, bytes));
        });
        r.Method(proto, "toBase64", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var ta = ThisUint8(a);
            var opts = GetOptions(realm, a[0]);
            bool url = GetAlphabet(realm, opts);
            bool omitPadding = opts != null && Operations.ToBoolean(opts.Get("omitPadding"));
            var bytes = Bytes(realm, ta);
            var alphabet = url ? Url : Std;
            var sb = new StringBuilder((bytes.Length + 2) / 3 * 4);
            int i = 0;
            for (; i + 2 < bytes.Length; i += 3)
            {
                int n = bytes[i] << 16 | bytes[i + 1] << 8 | bytes[i + 2];
                sb.Append(alphabet[n >> 18]).Append(alphabet[(n >> 12) & 63]).Append(alphabet[(n >> 6) & 63]).Append(alphabet[n & 63]);
            }
            if (bytes.Length - i == 1)
            {
                int n = bytes[i] << 16;
                sb.Append(alphabet[n >> 18]).Append(alphabet[(n >> 12) & 63]);
                if (!omitPadding) sb.Append("==");
            }
            else if (bytes.Length - i == 2)
            {
                int n = bytes[i] << 16 | bytes[i + 1] << 8;
                sb.Append(alphabet[n >> 18]).Append(alphabet[(n >> 12) & 63]).Append(alphabet[(n >> 6) & 63]);
                if (!omitPadding) sb.Append('=');
            }
            return new JsValue(a.Realm.Charged(sb.ToString()));
        });
        r.Method(proto, "toHex", 0, static (in NativeArgs a) =>
        {
            var bytes = Bytes(a.Realm, ThisUint8(a));
            return new JsValue(a.Realm.Charged(Convert.ToHexString(bytes).ToLowerInvariant()));
        });
        r.Method(proto, "setFromBase64", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var ta = ThisUint8(a);
            if (ta.Buffer.Immutable) throw JsErrors.TypeError(realm, "Cannot modify a Uint8Array backed by an immutable ArrayBuffer");
            if (!a[0].IsString) throw JsErrors.TypeError(realm, "setFromBase64 requires a string");
            var opts = GetOptions(realm, a[1]);
            bool url = GetAlphabet(realm, opts);
            int handling = GetLastChunkHandling(realm, opts);
            if (ta.IsOutOfBounds) throw JsErrors.TypeError(realm, "TypedArray is out of bounds or its buffer is detached");
            var (read, bytes, error) = FromBase64(a[0].AsStringUnchecked, url, handling, ta.Length);
            Write(ta, bytes);
            if (error) throw JsErrors.SyntaxError(realm, "Invalid base64 string");
            return ReadWritten(realm, read, bytes.Count);
        });
        r.Method(proto, "setFromHex", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var ta = ThisUint8(a);
            if (ta.Buffer.Immutable) throw JsErrors.TypeError(realm, "Cannot modify a Uint8Array backed by an immutable ArrayBuffer");
            if (!a[0].IsString) throw JsErrors.TypeError(realm, "setFromHex requires a string");
            if (ta.IsOutOfBounds) throw JsErrors.TypeError(realm, "TypedArray is out of bounds or its buffer is detached");
            var (read, bytes, error) = FromHex(a[0].AsStringUnchecked, ta.Length);
            Write(ta, bytes);
            if (error) throw JsErrors.SyntaxError(realm, "Invalid hex string");
            return ReadWritten(realm, read, bytes.Count);
        });
    }

    private static JsTypedArray ThisUint8(in NativeArgs a) =>
        a.This.Ref is JsTypedArray { Kind: TypedKind.Uint8 } t ? t : throw JsErrors.TypeError(a.Realm, "Receiver is not a Uint8Array");

    private static byte[] Bytes(Realm realm, JsTypedArray ta)
    {
        if (ta.IsOutOfBounds) throw JsErrors.TypeError(realm, "TypedArray is out of bounds or its buffer is detached");
        var b = new byte[ta.Length];
        Array.Copy(ta.Buffer.Data, ta.ByteOffset, b, 0, b.Length);
        return b;
    }

    private static void Write(JsTypedArray ta, List<byte> bytes)
    {
        for (int i = 0; i < bytes.Count; i++) ta.Buffer.Data[ta.ByteOffset + i] = bytes[i];
    }

    private static JsValue ReadWritten(Realm realm, int read, int written)
    {
        var o = realm.NewObject();
        o.CreateDataProperty("read", new JsValue(read));
        o.CreateDataProperty("written", new JsValue(written));
        return JsValue.FromObject(o);
    }

    private static JsTypedArray NewUint8(Realm realm, List<byte> bytes)
    {
        var ta = TypedArrayBuiltins.Create(realm, realm.Uint8ArrayPrototype, TypedKind.Uint8, bytes.Count);
        Write(ta, bytes);
        return ta;
    }

    private static JsObject? GetOptions(Realm realm, in JsValue v)
    {
        if (v.IsUndefined) return null;
        if (v.IsNotObject) throw JsErrors.TypeError(realm, "options must be an object");
        return v.AsObjectUnchecked;
    }

    private static bool GetAlphabet(Realm realm, JsObject? opts)
    {
        if (opts == null) return false;
        var v = opts.Get("alphabet");
        if (v.IsUndefined) return false;
        if (v.IsString && v.AsStringUnchecked == "base64") return false;
        if (v.IsString && v.AsStringUnchecked == "base64url") return true;
        throw JsErrors.TypeError(realm, "alphabet must be \"base64\" or \"base64url\"");
    }

    /// <summary>0 loose, 1 strict, 2 stop-before-partial.</summary>
    private static int GetLastChunkHandling(Realm realm, JsObject? opts)
    {
        if (opts == null) return 0;
        var v = opts.Get("lastChunkHandling");
        if (v.IsUndefined) return 0;
        if (v.IsString)
        {
            switch (v.AsStringUnchecked)
            {
                case "loose": return 0;
                case "strict": return 1;
                case "stop-before-partial": return 2;
            }
        }
        throw JsErrors.TypeError(realm, "lastChunkHandling must be \"loose\", \"strict\" or \"stop-before-partial\"");
    }

    private static int SkipWs(string s, int i)
    {
        while (i < s.Length && s[i] is '\t' or '\n' or '\f' or '\r' or ' ') i++;
        return i;
    }

    private static void DecodeChunk(string chunk, List<byte> bytes, int count)
    {
        int n = 0;
        foreach (var c in chunk) n = n << 6 | Std.IndexOf(c);
        var b = new[] { (byte)(n >> 16), (byte)(n >> 8), (byte)n };
        for (int i = 0; i < count; i++) bytes.Add(b[i]);
    }

    /// <summary>FromBase64 (spec algorithm, including partial results).</summary>
    private static (int read, List<byte> bytes, bool error) FromBase64(string s, bool url, int handling, long maxLength)
    {
        var bytes = new List<byte>();
        if (maxLength == 0) return (0, bytes, false);
        int read = 0, index = 0, length = s.Length;
        var chunk = new StringBuilder(4);
        while (true)
        {
            index = SkipWs(s, index);
            if (index == length)
            {
                if (chunk.Length > 0)
                {
                    if (handling == 2) return (read, bytes, false);
                    if (handling == 0)
                    {
                        if (chunk.Length == 1) return (read, bytes, true);
                        if (!DecodeFinal(chunk.ToString(), bytes, throwOnExtraBits: false)) return (read, bytes, true);
                    }
                    else return (read, bytes, true);
                }
                return (length, bytes, false);
            }
            char c = s[index++];
            if (c == '=')
            {
                if (chunk.Length < 2) return (read, bytes, true);
                index = SkipWs(s, index);
                if (chunk.Length == 2)
                {
                    if (index == length)
                    {
                        if (handling == 2) return (read, bytes, false);
                        return (read, bytes, true);
                    }
                    if (s[index] == '=') index = SkipWs(s, index + 1);
                }
                if (index < length) return (read, bytes, true);
                if (!DecodeFinal(chunk.ToString(), bytes, throwOnExtraBits: handling == 1)) return (read, bytes, true);
                return (length, bytes, false);
            }
            if (url)
            {
                if (c is '+' or '/') return (read, bytes, true);
                if (c == '-') c = '+';
                else if (c == '_') c = '/';
            }
            if (Std.IndexOf(c) < 0) return (read, bytes, true);
            long remaining = maxLength - bytes.Count;
            if ((remaining == 1 && chunk.Length == 2) || (remaining == 2 && chunk.Length == 3)) return (read, bytes, false);
            chunk.Append(c);
            if (chunk.Length == 4)
            {
                DecodeChunk(chunk.ToString(), bytes, 3);
                chunk.Clear();
                read = index;
                if (bytes.Count == maxLength) return (read, bytes, false);
            }
        }
    }

    private static bool DecodeFinal(string chunk, List<byte> bytes, bool throwOnExtraBits)
    {
        if (chunk.Length == 2)
        {
            int n = Std.IndexOf(chunk[0]) << 18 | Std.IndexOf(chunk[1]) << 12;
            if (throwOnExtraBits && (n & 0xFFFF) != 0) return false;
            bytes.Add((byte)(n >> 16));
            return true;
        }
        int m = Std.IndexOf(chunk[0]) << 18 | Std.IndexOf(chunk[1]) << 12 | Std.IndexOf(chunk[2]) << 6;
        if (throwOnExtraBits && (m & 0xFF) != 0) return false;
        bytes.Add((byte)(m >> 16));
        bytes.Add((byte)(m >> 8));
        return true;
    }

    private static int Hex(char c) => c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;

    private static (int read, List<byte> bytes, bool error) FromHex(string s, long maxLength)
    {
        var bytes = new List<byte>();
        if (s.Length % 2 != 0) return (0, bytes, true);
        int read = 0;
        while (read < s.Length && bytes.Count < maxLength)
        {
            int hi = Hex(s[read]), lo = Hex(s[read + 1]);
            if (hi < 0 || lo < 0) return (read, bytes, true);
            read += 2;
            bytes.Add((byte)(hi << 4 | lo));
        }
        return (read, bytes, false);
    }

    // ------------------------------------------------------------------ Array.fromAsync

    public static void InstallArray(Realm r, JsObject arrayCtor)
    {
        r.Method(arrayCtor, "fromAsync", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var promise = new JsPromise(realm);
            var st = new FromAsyncState(realm, promise, a.This, a[0], a[1], a[2]);
            try { st.Start(); }
            catch (JsThrowException ex) { st.Reject(ex.Value); }
            return JsValue.FromObject(promise);
        });
    }

    /// <summary>Array.fromAsync as a native async state machine (each Await is a promise continuation).</summary>
    private sealed class FromAsyncState : IPromiseContinuation
    {
        private enum Phase { Next, Mapped, Element, ElementMapped, Close }

        private readonly Realm _realm;
        private readonly JsPromise _promise;
        private readonly JsValue _c, _items, _mapfn, _thisArg;
        private readonly bool _mapping;
        private JsObject _a = null!;
        private JsValue _iter, _next;
        private JsObject? _arrayLike;
        private long _len, _k;
        private Phase _phase;
        private JsValue _pendingError;
        private bool _done;

        public FromAsyncState(Realm realm, JsPromise promise, JsValue c, JsValue items, JsValue mapfn, JsValue thisArg)
        {
            _realm = realm;
            _promise = promise;
            _c = c;
            _items = items;
            _mapfn = mapfn;
            _thisArg = thisArg;
            _mapping = !mapfn.IsUndefined;
        }

        public void Reject(JsValue v)
        {
            if (_done) return;
            _done = true;
            _promise.RejectWith(v);
        }

        private void Resolve(JsValue v)
        {
            if (_done) return;
            _done = true;
            _promise.ResolveWith(v);
        }

        private static JsValue GetMethod(Realm realm, in JsValue o, JsSymbol sym)
        {
            var m = Operations.GetV(realm, o, sym);
            if (m.IsNullOrUndefined) return JsValue.Undefined;
            if (!m.IsFunction) throw JsErrors.TypeError(realm, $"{sym} is not a function");
            return m;
        }

        public void Start()
        {
            var realm = _realm;
            if (_mapping && !_mapfn.IsFunction) throw JsErrors.TypeError(realm, $"{JsErrors.Describe(_mapfn)} is not a function");
            if (_items.IsNullOrUndefined) throw JsErrors.TypeError(realm, $"{_items} is not iterable");
            var usingAsync = GetMethod(realm, _items, JsSymbol.AsyncIterator);
            var usingSync = usingAsync.IsUndefined ? GetMethod(realm, _items, JsSymbol.Iterator) : JsValue.Undefined;
            if (!usingAsync.IsUndefined || !usingSync.IsUndefined)
            {
                _a = Operations.IsConstructor(_c) ? realm.Vm.Construct(_c, ReadOnlySpan<JsValue>.Empty).AsObjectUnchecked : realm.NewArray();
                if (!usingAsync.IsUndefined)
                {
                    _iter = realm.Vm.Call(usingAsync, _items, ReadOnlySpan<JsValue>.Empty);
                    if (_iter.IsNotObject) throw JsErrors.TypeError(realm, "Result of the Symbol.asyncIterator method is not an object");
                }
                else
                {
                    var sync = realm.Vm.Call(usingSync, _items, ReadOnlySpan<JsValue>.Empty);
                    if (sync.IsNotObject) throw JsErrors.TypeError(realm, "Result of the Symbol.iterator method is not an object");
                    var sn = sync.AsObjectUnchecked.Get("next", sync);
                    _iter = JsValue.FromObject(new AsyncGenerators.JsAsyncFromSyncIterator(realm, sync.AsObjectUnchecked, sn));
                }
                _next = _iter.AsObjectUnchecked.Get("next", _iter);
                StepIterator();
                return;
            }
            _arrayLike = Operations.ToObject(realm, _items);
            _len = Operations.LengthOfArrayLike(realm, _arrayLike);
            var lenV = new JsValue((double)_len);
            if (Operations.IsConstructor(_c)) _a = realm.Vm.Construct(_c, new ReadOnlySpan<JsValue>(in lenV)).AsObjectUnchecked;
            else
            {
                // ArrayCreate(len)
                if (_len > uint.MaxValue) throw JsErrors.RangeError(realm, "Invalid array length");
                var arr = realm.NewArray();
                arr.SetLength((uint)_len);
                _a = arr;
            }
            StepArrayLike();
        }

        private void Await(JsValue v, Phase phase)
        {
            _phase = phase;
            JsPromise.Resolve(_realm, v).Then(this);
        }

        private void StepIterator()
        {
            if (_k >= 9007199254740991)
            {
                CloseThenReject(JsErrors.TypeError(_realm, "Array too long").Value);
                return;
            }
            var r = _realm.Vm.Call(_next, _iter, ReadOnlySpan<JsValue>.Empty);
            Await(r, Phase.Next);
        }

        private void StepArrayLike()
        {
            if (_k >= _len)
            {
                _a.SetOrThrow("length", new JsValue((double)_len));
                Resolve(JsValue.FromObject(_a));
                return;
            }
            var v = _arrayLike!.Get(ArrayBuiltins.Key(_k), JsValue.FromObject(_arrayLike));
            Await(v, Phase.Element);
        }

        private void CloseThenReject(JsValue error)
        {
            _pendingError = error;
            try
            {
                var ret = _iter.AsObjectUnchecked.Get("return", _iter);
                if (ret.IsNullOrUndefined) { Reject(error); return; }
                var r = _realm.Vm.Call(ret, _iter, ReadOnlySpan<JsValue>.Empty);
                Await(r, Phase.Close);
            }
            catch (JsThrowException) { Reject(error); }
        }

        public void OnSettled(Realm realm, bool fulfilled, JsValue value)
        {
            if (_done) return;
            try
            {
                switch (_phase)
                {
                    case Phase.Close:
                        Reject(_pendingError);
                        return;
                    case Phase.Next:
                    {
                        if (!fulfilled) { Reject(value); return; }
                        if (value.IsNotObject) throw JsErrors.TypeError(realm, "Iterator result is not an object");
                        var ro = value.AsObjectUnchecked;
                        if (Operations.ToBoolean(ro.Get("done", value)))
                        {
                            _a.SetOrThrow("length", new JsValue((double)_k));
                            Resolve(JsValue.FromObject(_a));
                            return;
                        }
                        var v = ro.Get("value", value);
                        if (_mapping)
                        {
                            JsValue mapped;
                            try { mapped = realm.Vm.Call(_mapfn, _thisArg, new[] { v, new JsValue((double)_k) }); }
                            catch (JsThrowException ex) { CloseThenReject(ex.Value); return; }
                            Await(mapped, Phase.Mapped);
                            return;
                        }
                        AddFromIterator(v);
                        return;
                    }
                    case Phase.Mapped:
                        if (!fulfilled) { CloseThenReject(value); return; }
                        AddFromIterator(value);
                        return;
                    case Phase.Element:
                    {
                        if (!fulfilled) { Reject(value); return; }
                        if (_mapping)
                        {
                            var mapped = realm.Vm.Call(_mapfn, _thisArg, new[] { value, new JsValue((double)_k) });
                            Await(mapped, Phase.ElementMapped);
                            return;
                        }
                        AddFromArrayLike(value);
                        return;
                    }
                    case Phase.ElementMapped:
                        if (!fulfilled) { Reject(value); return; }
                        AddFromArrayLike(value);
                        return;
                }
            }
            catch (JsThrowException ex) { Reject(ex.Value); }
        }

        private void AddFromIterator(JsValue v)
        {
            try { _a.CreateDataPropertyOrThrow(ArrayBuiltins.Key(_k), v); }
            catch (JsThrowException ex) { CloseThenReject(ex.Value); return; }
            _k++;
            StepIterator();
        }

        private void AddFromArrayLike(JsValue v)
        {
            _a.CreateDataPropertyOrThrow(ArrayBuiltins.Key(_k), v);
            _k++;
            StepArrayLike();
        }
    }
}
