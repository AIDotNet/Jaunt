using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Text;
using Jaunt.Compiler;

namespace Jaunt.Runtime;

internal static class ArrayBuiltins
{
    /// <summary>Array.prototype.push; the interpreter recognizes this delegate for its single-argument fast path.</summary>
    internal static readonly NativeFn PushFn = static (in NativeArgs a) =>
    {
        if (a.This.Ref is JsArray arr && arr.Dense && arr.LengthWritable && arr.Extensible && (long)arr.Length + a.Length < uint.MaxValue && !JsArray.ProtoChainMayHaveIndices(arr.Proto))
        {
            foreach (var v in a.Args) arr.Push(v);
            return new JsValue(arr.Length);
        }
        var realm = a.Realm;
        var o = Operations.ToObject(realm, a.This);
        long len = Operations.LengthOfArrayLike(realm, o);
        CheckLength(realm, (double)len + a.Length);
        foreach (var v in a.Args) o.SetOrThrow(len < uint.MaxValue ? Key(len++) : new PropertyKey(NumberFormatting.ToString(len++)), v);
        o.SetOrThrow("length", new JsValue(len));
        return new JsValue(len);
    };

    public static void Setup(Realm r)
    {
        // Array.prototype is itself an Array exotic object (length 0)
        var proto = new JsArray(r, 0, r.ObjectPrototype);
        proto.IsPrototypeObject = true;
        r.ArrayPrototype = proto;
        var ctor = r.Constructor("Array", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var nt = a.IsConstruct ? a.NewTarget.AsObjectUnchecked : a.Callee;
            var p = Operations.GetPrototypeFromConstructor(realm, nt, realm.ArrayPrototype);
            if (a.Length == 1 && a[0].IsNumber)
            {
                double d = a[0].Num;
                uint len = (uint)d;
                if (len != d) throw JsErrors.RangeError(realm, "Invalid array length");
                var arr = new JsArray(realm, (int)Math.Min(len, 1024), p);
                arr.SetLength(len);
                return JsValue.FromObject(arr);
            }
            var res = new JsArray(realm, a.Length, p);
            foreach (var v in a.Args) res.Push(v);
            return JsValue.FromObject(res);
        }, proto);
        r.ArrayConstructor = ctor;
        r.DefineGlobal("Array", JsValue.FromObject(ctor));
        r.Getter(ctor, JsSymbol.Species, "[Symbol.species]", static (in NativeArgs a) => a.This);

        Es2026Builtins.InstallArray(r, ctor);
        r.Method(ctor, "isArray", 1, static (in NativeArgs a) => JsValue.FromBool(IsArray(a.Realm, a[0])));
        r.Method(ctor, "of", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a.This.IsObject && !ReferenceEquals(a.This.Ref, realm.ArrayConstructor) && Operations.IsConstructor(a.This))
            {
                var lenV = new JsValue(a.Length);
                var obj = realm.Vm.Construct(a.This, new ReadOnlySpan<JsValue>(in lenV)).AsObjectUnchecked;
                for (int k = 0; k < a.Length; k++) obj.CreateDataPropertyOrThrow(Key(k), a.Args[k]);
                obj.SetOrThrow("length", lenV);
                return JsValue.FromObject(obj);
            }
            var arr = realm.NewArray(a.Length);
            foreach (var v in a.Args) arr.Push(v);
            return JsValue.FromObject(arr);
        });
        r.Method(ctor, "from", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var items = a[0];
            var mapFn = a[1];
            var thisArg = a[2];
            bool mapping = !mapFn.IsUndefined;
            if (mapping && !mapFn.IsFunction) throw JsErrors.TypeError(realm, "Array.from: when provided, the second argument must be a function");
            if (a.This.IsObject && !ReferenceEquals(a.This.Ref, realm.ArrayConstructor) && Operations.IsConstructor(a.This))
                return FromWithConstructor(realm, a.This, items, mapFn, thisArg);
            var args = new JsValue[2];
            if (items.IsNullOrUndefined) throw JsErrors.TypeError(realm, $"{items} is not iterable");
            var usingIterator = Operations.GetV(realm, items, JsSymbol.Iterator);
            JsClosure? fast = null;
            if (mapping) realm.Vm.TryPrepareFastCalls(mapFn, out fast);
            if (!usingIterator.IsNullOrUndefined)
            {
                // an array source's current length is only a capacity hint: the result still takes what the iterator yields
                var result = new ResultSink(realm, realm.NewArray(), buffered: true, items.Ref is JsArray ia ? ia.Length : 0);
                var it = IteratorRecord.Get(realm, items);
                double k = 0;
                while (it.Step(out var v))
                {
                    if (mapping)
                    {
                        try
                        {
                            var kv = new JsValue(k);
                            if (fast != null) v = realm.Vm.CallCallback(fast, thisArg, v, kv, kv, kv, 2);
                            else { args[0] = v; args[1] = kv; v = realm.Vm.Call(mapFn, thisArg, args); }
                        }
                        catch (JsThrowException) { it.CloseQuiet(); throw; }
                    }
                    result.Append(v);
                    k++;
                    realm.Vm.Tick();
                }
                return JsValue.FromObject(result.Finish());
            }
            var o = Operations.ToObject(realm, items);
            long len = Operations.LengthOfArrayLike(realm, o);
            if (len > uint.MaxValue) throw JsErrors.RangeError(realm, "Invalid array length");
            var res = new ResultSink(realm, realm.NewArray(), buffered: true, len);
            for (long i = 0; i < len; i++)
            {
                var v = GetAt(o, i, JsValue.FromObject(o));
                if (mapping)
                {
                    var iv = new JsValue(i);
                    if (fast != null) v = realm.Vm.CallCallback(fast, thisArg, v, iv, iv, iv, 2);
                    else { args[0] = v; args[1] = iv; v = realm.Vm.Call(mapFn, thisArg, args); }
                }
                res.Append(v);
                realm.Vm.Tick();
            }
            return JsValue.FromObject(res.Finish());
        });

        r.Method(proto, "push", 1, PushFn);
        r.Method(proto, "pop", 0, static (in NativeArgs a) =>
        {
            if (a.This.Ref is JsArray arr && arr.Dense && arr.LengthWritable)
            {
                if (arr.Length == 0) return JsValue.Undefined;
                if (arr.TryQueuePop(out var qv)) return qv;
                uint i = arr.Length - 1;
                if (arr.TryGetDense(i, out var dv))
                {
                    arr.SetLength(i);
                    return dv;
                }
            }
            var realm = a.Realm;
            var o = Operations.ToObject(realm, a.This);
            long len = Operations.LengthOfArrayLike(realm, o);
            if (len == 0) { o.SetOrThrow("length", JsValue.Zero); return JsValue.Undefined; }
            var key = Key((len - 1));
            var res = o.Get(key);
            if (!o.Delete(key)) throw JsErrors.TypeError(realm, "Cannot delete property");
            o.SetOrThrow("length", new JsValue(len - 1));
            return res;
        });
        r.Method(proto, "shift", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a.This.Ref is JsArray arr && arr.Dense && arr.LengthWritable && arr.Extensible && !JsArray.ProtoChainMayHaveIndices(arr.Proto))
            {
                if (arr.Length == 0) return JsValue.Undefined;
                if (arr.TryQueueShift(out var first)) return first.IsEmpty ? JsValue.Undefined : first;
                if (arr.Length <= arr.Items.Length)
                {
                    first = arr.Items[0];
                    Array.Copy(arr.Items, 1, arr.Items, 0, (int)arr.Length - 1);
                    arr.Items[arr.Length - 1] = JsValue.Empty;
                    arr.Length--;
                    realm.Vm.Tick((int)Math.Min(arr.Length / 64, 1000));
                    return first.IsEmpty ? JsValue.Undefined : first;
                }
            }
            var o = Operations.ToObject(realm, a.This);
            long len = Operations.LengthOfArrayLike(realm, o);
            if (len == 0) { o.SetOrThrow("length", JsValue.Zero); return JsValue.Undefined; }
            var f = o.GetIndex(0, a.This);
            for (long k = 1; k < len; k++)
            {
                var from = Key(k);
                var to = Key((k - 1));
                if (o.HasProperty(from)) o.SetOrThrow(to, o.Get(from));
                else o.Delete(to);
                realm.Vm.Tick();
            }
            o.Delete(Key((len - 1)));
            o.SetOrThrow("length", new JsValue(len - 1));
            return f;
        });
        r.Method(proto, "unshift", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var o = Operations.ToObject(realm, a.This);
            long len = Operations.LengthOfArrayLike(realm, o);
            int n = a.Length;
            if (n > 0)
            {
                CheckLength(realm, (double)len + n);
                if (o is JsArray arr && arr.Dense && arr.Extensible && arr.LengthWritable && !JsArray.ProtoChainMayHaveIndices(arr.Proto))
                {
                    if (arr.TryQueueUnshift(a.Args)) return new JsValue(arr.Length);
                    arr.LeaveQueueMode();
                    if (arr.Length <= arr.Items.Length)
                    {
                        arr.EnsureCapacity(arr.Length + (uint)n);
                        Array.Copy(arr.Items, 0, arr.Items, n, (int)arr.Length);
                        for (int i = 0; i < n; i++) arr.Items[i] = a.Args[i];
                        arr.Length += (uint)n;
                        return new JsValue(arr.Length);
                    }
                }
                for (long k = len; k > 0; k--)
                {
                    var from = Key((k - 1));
                    var to = Key((k + n - 1));
                    if (o.HasProperty(from)) o.SetOrThrow(to, o.Get(from));
                    else o.Delete(to);
                    realm.Vm.Tick();
                }
                for (int j = 0; j < n; j++) o.SetOrThrow(Key(j), a.Args[j]);
            }
            o.SetOrThrow("length", new JsValue(len + n));
            return new JsValue(len + n);
        });
        r.Method(proto, "slice", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var o = Operations.ToObject(realm, a.This);
            long len = Operations.LengthOfArrayLike(realm, o);
            long k = Operations.RelativeIndex(realm, a[0], len, 0);
            long end = Operations.RelativeIndex(realm, a[1], len, len);
            long count = Math.Max(end - k, 0);
            var resObj = ArraySpeciesCreate(realm, o, count, out bool intrinsic);
            if (o is JsArray qa) qa.LeaveQueueMode();
            // a fresh array nobody has seen: one copy into the store it already has; a hole stays a hole
            if (intrinsic && resObj is JsArray res && o is JsArray arr && arr.Dense && end <= arr.Items.Length && !JsArray.ProtoChainMayHaveIndices(arr.Proto))
            {
                if (count > 0)
                {
                    res.EnsureCapacity((uint)count);
                    arr.Items.AsSpan((int)k, (int)count).CopyTo(res.Items);
                }
                realm.Vm.Tick((int)Math.Min(count / 64, 1000));
                return JsValue.FromObject(res);
            }
            long n = 0;
            for (long i = k; i < end; i++, n++)
            {
                realm.Vm.Tick();
                var key = Key(i);
                if (o.HasProperty(key)) resObj.CreateDataPropertyOrThrow(Key(n), o.Get(key));
            }
            resObj.SetOrThrow("length", new JsValue((double)n));
            return JsValue.FromObject(resObj);
        });
        r.Method(proto, "splice", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var o = Operations.ToObject(realm, a.This);
            long len = Operations.LengthOfArrayLike(realm, o);
            long start = Operations.RelativeIndex(realm, a[0], len, 0);
            long delCount;
            if (a.Length == 0) delCount = 0;
            else if (a.Length == 1) delCount = len - start;
            else delCount = (long)Math.Min(Math.Max(Operations.ToIntegerOrInfinity(realm, a[1]), 0), len - start);
            var items = a.Length > 2 ? a.Args.Slice(2).ToArray() : Array.Empty<JsValue>();
            CheckLength(realm, (double)len + items.Length - delCount);
            var removedObj = ArraySpeciesCreate(realm, o, delCount, out bool intrinsic);
            if (intrinsic && o is JsArray arr && arr.Dense && arr.Extensible && arr.LengthWritable && arr.Length == len && !JsArray.ProtoChainMayHaveIndices(arr.Proto) && (arr.IsQueue || len <= arr.Items.Length))
            {
                arr.SpliceDense((int)start, (int)delCount, items, (JsArray)removedObj);
                realm.Vm.Tick((int)Math.Min(Math.Min(start, len - start - delCount) / 64, 1000));
                return JsValue.FromObject(removedObj);
            }
            for (long i = 0; i < delCount; i++)
            {
                realm.Vm.Tick();
                var from = Key((start + i));
                if (o.HasProperty(from)) removedObj.CreateDataPropertyOrThrow(Key(i), o.Get(from));
            }
            removedObj.SetOrThrow("length", new JsValue((double)delCount));
            int itemCount = items.Length;
            if (itemCount < delCount)
            {
                for (long k = start; k < len - delCount; k++)
                {
                    realm.Vm.Tick();
                    var from = Key((k + delCount));
                    var to = Key((k + itemCount));
                    if (o.HasProperty(from)) o.SetOrThrow(to, o.Get(from)); else o.Delete(to);
                }
                for (long k = len; k > len - delCount + itemCount; k--) { o.Delete(Key((k - 1))); realm.Vm.Tick(); }
            }
            else if (itemCount > delCount)
            {
                for (long k = len - delCount; k > start; k--)
                {
                    realm.Vm.Tick();
                    var from = Key((k + delCount - 1));
                    var to = Key((k + itemCount - 1));
                    if (o.HasProperty(from)) o.SetOrThrow(to, o.Get(from)); else o.Delete(to);
                }
            }
            for (int i = 0; i < itemCount; i++) o.SetOrThrow(Key((start + i)), items[i]);
            o.SetOrThrow("length", new JsValue(len - delCount + itemCount));
            return JsValue.FromObject(removedObj);
        });
        r.Method(proto, "concat", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var o = Operations.ToObject(realm, a.This);
            var species = ArraySpeciesCreate(realm, o, 0, out bool intrinsic);
            // gathered in a fresh array; a species-created result (which may be anything, even non-empty) is written at the end
            var res = intrinsic ? (JsArray)species : realm.NewArray();
            // room for the common case of array arguments; reading their lengths observes nothing
            long hint = o is JsArray oa ? oa.Length : 0;
            foreach (var v in a.Args) hint += v.Ref is JsArray va ? va.Length : 1;
            if (hint > 0 && hint <= realm.MaxArrayLength) res.EnsureCapacity((uint)hint);
            void Add(Realm realm, JsArray res, JsValue item)
            {
                bool spreadable = false;
                if (item.IsObject)
                {
                    var sp = item.AsObjectUnchecked.Get(JsSymbol.IsConcatSpreadable);
                    spreadable = sp.IsUndefined ? IsArray(realm, item) : Operations.ToBoolean(sp);
                }
                if (!spreadable) { res.Push(item); return; }
                var e = item.AsObjectUnchecked;
                if (e is JsArray qa) qa.LeaveQueueMode();
                if (e is JsArray ea && ea.Dense && ea.Length <= ea.Items.Length && !JsArray.ProtoChainMayHaveIndices(ea.Proto))
                {
                    if (res.Dense && (ulong)res.Length + ea.Length <= int.MaxValue)
                    {
                        // one copy; a hole stays a hole
                        uint at = res.Length;
                        res.LeaveQueueMode();
                        res.EnsureCapacity(at + ea.Length);
                        Array.Copy(ea.Items, 0, res.Items, at, ea.Length);
                        res.Length = at + ea.Length;
                        realm.Vm.Tick((int)Math.Min(ea.Length / 64, 1000));
                        return;
                    }
                    for (uint i = 0; i < ea.Length; i++) { var v = ea.Items[i]; if (v.IsEmpty) res.PushHole(); else res.Push(v); }
                    return;
                }
                long len = Operations.LengthOfArrayLike(realm, e);
                if (res.Length + len > 9007199254740991L) throw JsErrors.TypeError(realm, "Array length exceeds the maximum safe integer");
                uint start = res.Length;
                for (long i = 0; i < len; i++)
                {
                    var key = Key(i);
                    if (e.HasProperty(key)) res.CreateDataPropertyOrThrow(Key((start + i)), e.Get(key));
                    realm.Vm.Tick();
                }
                res.SetLength((uint)(start + len));
            }
            Add(realm, res, JsValue.FromObject(o));
            foreach (var v in a.Args) Add(realm, res, v);
            if (!ReferenceEquals(species, res))
            {
                // non-array species result: copy element-wise with CreateDataPropertyOrThrow
                long n = 0;
                for (uint i = 0; i < res.Length; i++, n++)
                    if (res.TryGetElement(i, out var v)) species.CreateDataPropertyOrThrow(PropertyKey.FromIndex(i), v);
                species.SetOrThrow("length", new JsValue((double)n));
                return JsValue.FromObject(species);
            }
            return JsValue.FromObject(res);
        });
        r.Method(proto, "join", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var o = Operations.ToObject(realm, a.This);
            long len = Operations.LengthOfArrayLike(realm, o);   // before the separator is converted (observable)
            string sep = a[0].IsUndefined ? "," : Operations.ToString(realm, a[0]);
            return new JsValue(Join(realm, o, sep, len));
        });
        r.Method(proto, "toString", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var o = Operations.ToObject(realm, a.This);
            var join = o.Get("join");
            if (join.IsFunction) return realm.Vm.Call(join, JsValue.FromObject(o), ReadOnlySpan<JsValue>.Empty);
            return new JsValue(ObjectBuiltins.ObjectToString(realm, a.This));
        });
        r.Method(proto, "toLocaleString", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            // ECMA-402 19.5.1 / 23.2.3.32: element toLocaleString receives (locales, options) when Intl is present
            ReadOnlySpan<JsValue> localeArgs = realm.Intl != null ? new[] { a[0], a[1] } : ReadOnlySpan<JsValue>.Empty;
            realm.Vm.EnsureNativeStack();   // recursive for nested arrays
            var o = Operations.ToObject(realm, a.This);
            long len = Operations.LengthOfArrayLike(realm, o);
            var sb = new StringBuilder();
            int charged = 0;
            for (long i = 0; i < len; i++)
            {
                realm.Vm.Tick();
                if (i > 0) sb.Append(',');
                var v = GetAt(o, i, JsValue.FromObject(o));
                if (!v.IsNullOrUndefined) sb.Append(Operations.ToString(realm, Operations.Invoke(realm, v, "toLocaleString", localeArgs)));
                if (sb.Length > realm.MaxStringLength) throw JsErrors.RangeError(realm, "Invalid string length");
                if (sb.Length - charged >= 4096) { realm.ChargeOutput(sb.Length - charged); charged = sb.Length; }
            }
            realm.ChargeOutput(sb.Length - charged);
            return new JsValue(sb.ToString());
        });
        r.Method(proto, "reverse", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var o = Operations.ToObject(realm, a.This);
            if (o is JsArray qa) qa.LeaveQueueMode();
            if (o is JsArray arr && arr.Dense && arr.Length <= arr.Items.Length && Array.IndexOf(arr.Items, JsValue.Empty, 0, (int)arr.Length) < 0 && arr.Extensible && arr.LengthWritable)
            {
                Array.Reverse(arr.Items, 0, (int)arr.Length);
                return a.This;
            }
            long len = Operations.LengthOfArrayLike(realm, o);
            for (long lower = 0, upper = len - 1; lower < upper; lower++, upper--)
            {
                var lk = Key(lower);
                var uk = Key(upper);
                bool le = o.HasProperty(lk);
                var lv = le ? o.Get(lk) : default;
                bool ue = o.HasProperty(uk);
                var uv = ue ? o.Get(uk) : default;
                if (le || ue)   // both absent: nothing happens (23.1.3.26 step 7.l)
                {
                    if (ue) o.SetOrThrow(lk, uv); else DeleteOrThrow(realm, o, lk);
                    if (le) o.SetOrThrow(uk, lv); else DeleteOrThrow(realm, o, uk);
                }
                realm.Vm.Tick();
            }
            return JsValue.FromObject(o);
        });
        r.Method(proto, "indexOf", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var o = Operations.ToObject(realm, a.This);
            long len = Operations.LengthOfArrayLike(realm, o);
            if (len == 0) return new JsValue(-1);
            long n = (long)Math.Max(Math.Min(Operations.ToIntegerOrInfinity(realm, a[1]), len), -len - 1);
            long k = n >= 0 ? n : Math.Max(len + n, 0);
            var target = a[0];
            if (o is JsArray arr && arr.Dense && !JsArray.ProtoChainMayHaveIndices(arr.Proto))
            {
                arr.LeaveQueueMode();
                long end = Math.Min(len, arr.Items.Length);
                for (long i = k; i < end; i++)
                {
                    var v = arr.Items[i];
                    if (v.IsNotEmpty && Operations.StrictEquals(v, target)) return new JsValue(i);
                }
                realm.Vm.Tick((int)Math.Min(len / 64, 1000));
                return new JsValue(-1);
            }
            for (long i = k; i < len; i++)
            {
                var key = Key(i);
                if (o.HasProperty(key) && Operations.StrictEquals(o.Get(key), target)) return new JsValue(i);
                realm.Vm.Tick();
            }
            return new JsValue(-1);
        });
        r.Method(proto, "lastIndexOf", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var o = Operations.ToObject(realm, a.This);
            long len = Operations.LengthOfArrayLike(realm, o);
            if (len == 0) return new JsValue(-1);
            double n = a.Length > 1 ? Operations.ToIntegerOrInfinity(realm, a[1]) : len - 1;
            long k = n >= 0 ? (long)Math.Min(n, len - 1) : (long)(len + n);
            var target = a[0];
            if (o is JsArray qa) qa.LeaveQueueMode();
            if (o is JsArray arr && arr.Dense && len <= arr.Items.Length && !JsArray.ProtoChainMayHaveIndices(arr.Proto))
            {
                for (long i = k; i >= 0; i--)
                {
                    var v = arr.Items[i];
                    if (v.IsNotEmpty && Operations.StrictEquals(v, target)) return new JsValue(i);
                }
                realm.Vm.Tick((int)Math.Min(len / 64, 1000));
                return new JsValue(-1);
            }
            for (long i = k; i >= 0; i--)
            {
                var key = Key(i);
                if (o.HasProperty(key) && Operations.StrictEquals(o.Get(key), target)) return new JsValue(i);
                realm.Vm.Tick();
            }
            return new JsValue(-1);
        });
        r.Method(proto, "includes", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var o = Operations.ToObject(realm, a.This);
            long len = Operations.LengthOfArrayLike(realm, o);
            if (len == 0) return JsValue.False;
            double n = Operations.ToIntegerOrInfinity(realm, a[1]);
            long k = n >= 0 ? (long)Math.Min(n, len) : (long)Math.Max(len + n, 0);
            for (long i = k; i < len; i++)
            {
                if (Operations.SameValueZero(GetAt(o, i, JsValue.FromObject(o)), a[0])) return JsValue.True;
                realm.Vm.Tick();
            }
            return JsValue.False;
        });
        r.Method(proto, "at", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var o = Operations.ToObject(realm, a.This);
            long len = Operations.LengthOfArrayLike(realm, o);
            double rel = Operations.ToIntegerOrInfinity(realm, a[0]);
            double k = rel >= 0 ? rel : len + rel;
            if (k < 0 || k >= len) return JsValue.Undefined;
            return GetAt(o, (long)k, JsValue.FromObject(o));
        });
        r.Method(proto, "fill", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var o = Operations.ToObject(realm, a.This);
            long len = Operations.LengthOfArrayLike(realm, o);
            long k = Operations.RelativeIndex(realm, a[1], len, 0);
            long end = Operations.RelativeIndex(realm, a[2], len, len);
            // every target is an own data element or a hole that no prototype can intercept (rechecked here: converting
            // the arguments ran user code); one exactly sized store instead of growing element by element
            if (end > k && o is JsArray fa && fa.Dense && end <= fa.Length && end <= realm.MaxArrayLength && fa.Extensible && !JsArray.ProtoChainMayHaveIndices(fa.Proto))
            {
                fa.EnsureCapacity((uint)end);
                fa.Items.AsSpan((int)k, (int)(end - k)).Fill(a[0]);
                realm.Vm.Tick((int)Math.Min((end - k) / 64, 1000));
                return JsValue.FromObject(o);
            }
            for (long i = k; i < end; i++) { SetAtOrThrow(realm, o, i, a[0]); realm.Vm.Tick(); }
            return JsValue.FromObject(o);
        });
        r.Method(proto, "copyWithin", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var o = Operations.ToObject(realm, a.This);
            long len = Operations.LengthOfArrayLike(realm, o);
            long to = Operations.RelativeIndex(realm, a[0], len, 0);
            long from = Operations.RelativeIndex(realm, a[1], len, 0);
            long final = Operations.RelativeIndex(realm, a[2], len, len);
            long count = Math.Min(final - from, len - to);
            if (o is JsArray qa) qa.LeaveQueueMode();
            if (count > 0 && o is JsArray arr && arr.Dense && len <= arr.Items.Length && arr.Extensible && arr.LengthWritable && !JsArray.ProtoChainMayHaveIndices(arr.Proto))
            {
                Array.Copy(arr.Items, from, arr.Items, to, count);   // overlap-safe
                realm.Vm.Tick((int)Math.Min(count / 64, 1000));
                return JsValue.FromObject(o);
            }
            int dir = 1;
            if (from < to && to < from + count) { dir = -1; from += count - 1; to += count - 1; }
            while (count-- > 0)
            {
                var fk = Key(from);
                var tk = Key(to);
                if (o.HasProperty(fk)) o.SetOrThrow(tk, o.Get(fk)); else DeleteOrThrow(realm, o, tk);
                from += dir; to += dir;
                realm.Vm.Tick();
            }
            return JsValue.FromObject(o);
        });

        // callback-based methods
        r.Method(proto, "forEach", 1, static (in NativeArgs a) => { Iterate(a, 0); return JsValue.Undefined; });
        r.Method(proto, "map", 1, static (in NativeArgs a) => Iterate(a, 1));
        r.Method(proto, "filter", 1, static (in NativeArgs a) => Iterate(a, 2));
        r.Method(proto, "some", 1, static (in NativeArgs a) => Iterate(a, 3));
        r.Method(proto, "every", 1, static (in NativeArgs a) => Iterate(a, 4));
        r.Method(proto, "find", 1, static (in NativeArgs a) => Find(a, fromEnd: false, index: false));
        r.Method(proto, "findIndex", 1, static (in NativeArgs a) => Find(a, fromEnd: false, index: true));
        r.Method(proto, "findLast", 1, static (in NativeArgs a) => Find(a, fromEnd: true, index: false));
        r.Method(proto, "findLastIndex", 1, static (in NativeArgs a) => Find(a, fromEnd: true, index: true));
        r.Method(proto, "reduce", 1, static (in NativeArgs a) => Reduce(a, right: false));
        r.Method(proto, "reduceRight", 1, static (in NativeArgs a) => Reduce(a, right: true));
        r.Method(proto, "sort", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var cmp = a[0];
            if (!cmp.IsUndefined && !cmp.IsFunction) throw JsErrors.TypeError(realm, "The comparison function must be either a function or undefined");
            var o = Operations.ToObject(realm, a.This);
            SortObject(realm, o, cmp);
            return JsValue.FromObject(o);
        });
        r.Method(proto, "toSorted", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var cmp = a[0];
            if (!cmp.IsUndefined && !cmp.IsFunction) throw JsErrors.TypeError(realm, "The comparison function must be either a function or undefined");
            var o = Operations.ToObject(realm, a.This);
            var copy = CopyToArray(realm, o);
            SortObject(realm, copy, cmp);
            return JsValue.FromObject(copy);
        });
        r.Method(proto, "toReversed", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var o = Operations.ToObject(realm, a.This);
            long len = Operations.LengthOfArrayLike(realm, o);
            var res = CreateArrayForAppend(realm, len);
            for (long k = 0; k < len; k++)
            {
                res.Push(GetAt(o, len - k - 1, JsValue.FromObject(o)));
                realm.Vm.Tick();
            }
            return JsValue.FromObject(res);
        });
        r.Method(proto, "toSpliced", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var o = Operations.ToObject(realm, a.This);
            long len = Operations.LengthOfArrayLike(realm, o);
            long start = Operations.RelativeIndex(realm, a[0], len, 0);
            long insert = Math.Max(a.Length - 2, 0);
            long skip;
            if (a.Length == 0) skip = 0;
            else if (a.Length == 1) skip = len - start;
            else skip = (long)Math.Min(Math.Max(Operations.ToIntegerOrInfinity(realm, a[1]), 0), len - start);
            double newLen = (double)len + insert - skip;
            if (newLen > 9007199254740991) throw JsErrors.TypeError(realm, "Invalid array length");
            var res = CreateArrayForAppend(realm, (long)newLen);
            long i = 0;
            for (; i < start; i++) { res.Push(GetAt(o, i, JsValue.FromObject(o))); realm.Vm.Tick(); }
            for (int j = 2; j < a.Length; j++) { res.Push(a.Args[j]); i++; }
            for (long r2 = start + skip; i < (long)newLen; i++, r2++) { res.Push(GetAt(o, r2, JsValue.FromObject(o))); realm.Vm.Tick(); }
            return JsValue.FromObject(res);
        });
        r.Method(proto, "with", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var o = Operations.ToObject(realm, a.This);
            long len = Operations.LengthOfArrayLike(realm, o);
            double rel = Operations.ToIntegerOrInfinity(realm, a[0]);
            double idx = rel >= 0 ? rel : len + rel;
            if (idx < 0 || idx >= len) throw JsErrors.RangeError(realm, "Invalid index");
            var res = CreateArrayForAppend(realm, len);
            for (long k = 0; k < len; k++)
            {
                res.Push(k == (long)idx ? a[1] : GetAt(o, k, JsValue.FromObject(o)));
                realm.Vm.Tick();
            }
            return JsValue.FromObject(res);
        });
        r.Method(proto, "flat", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var o = Operations.ToObject(realm, a.This);
            long len = Operations.LengthOfArrayLike(realm, o);
            double depth = 1;
            if (!a[0].IsUndefined) { depth = Operations.ToIntegerOrInfinity(realm, a[0]); if (depth < 0) depth = 0; }
            var sink = new ResultSink(realm, ArraySpeciesCreate(realm, o, 0, out bool intrinsic), intrinsic, len);
            Flatten(realm, sink, o, len, depth, JsValue.Undefined, JsValue.Undefined);
            return JsValue.FromObject(sink.Finish());
        });
        r.Method(proto, "flatMap", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var o = Operations.ToObject(realm, a.This);
            long len = Operations.LengthOfArrayLike(realm, o);
            if (!a[0].IsFunction) throw JsErrors.TypeError(realm, "flatMap mapper function is not callable");
            var sink = new ResultSink(realm, ArraySpeciesCreate(realm, o, 0, out bool intrinsic), intrinsic, len);
            Flatten(realm, sink, o, len, 1, a[0], a[1]);
            return JsValue.FromObject(sink.Finish());
        });
        r.Method(proto, "keys", 0, static (in NativeArgs a) => JsValue.FromObject(new JsArrayIterator(a.Realm, Operations.ToObject(a.Realm, a.This), 0)));
        r.Method(proto, "entries", 0, static (in NativeArgs a) => JsValue.FromObject(new JsArrayIterator(a.Realm, Operations.ToObject(a.Realm, a.This), 2)));
        var values = r.MethodEager(proto, "values", 0, static (in NativeArgs a) => JsValue.FromObject(new JsArrayIterator(a.Realm, Operations.ToObject(a.Realm, a.This), 1)));
        r.ArrayProtoValues = values;
        proto.AddOwnProperty(JsSymbol.Iterator, JsValue.FromObject(values), PropFlags.Hidden);
        var unscopables = new JsObject(r, (JsObject?)null);
        foreach (var n in new[] { "at", "copyWithin", "entries", "fill", "find", "findIndex", "findLast", "findLastIndex", "flat", "flatMap", "includes", "keys", "toReversed", "toSorted", "toSpliced", "values" })
            unscopables.FastAdd(n, JsValue.True);
        proto.AddOwnProperty(JsSymbol.Unscopables, JsValue.FromObject(unscopables), PropFlags.Configurable);
    }

    /// <summary>ArraySpeciesCreate (23.1.3.1.1): honours Symbol.species so Array subclasses get subclass results.</summary>
    public static JsObject ArraySpeciesCreate(Realm realm, JsObject original, long length) => ArraySpeciesCreate(realm, original, length, out _);

    /// <summary><paramref name="intrinsic"/>: the result is a fresh intrinsic Array that no user code has seen.</summary>
    public static JsObject ArraySpeciesCreate(Realm realm, JsObject original, long length, out bool intrinsic)
    {
        intrinsic = true;
        // the usual case: reading "constructor" finds this realm's %Array%, which is used as is
        if (original is JsArray oa && realm.IsArraySpeciesPristine(oa)) return CreateArray(realm, length);
        if (!IsArray(realm, JsValue.FromObject(original))) return CreateArray(realm, length);
        var c = original.Get("constructor");
        if (Operations.IsConstructor(c))
        {
            // an Array constructor of another realm is treated as undefined
            var cr = Operations.GetFunctionRealm(realm, c.AsObjectUnchecked);
            if (!ReferenceEquals(cr, realm) && ReferenceEquals(c.Ref, cr.ArrayConstructor)) c = JsValue.Undefined;
        }
        if (c.IsObject)
        {
            if (ReferenceEquals(c.Ref, realm.ArrayConstructor)) return CreateArray(realm, length);
            c = c.AsObjectUnchecked.Get(JsSymbol.Species);
            if (c.IsNull) c = JsValue.Undefined;
        }
        if (c.IsUndefined) return CreateArray(realm, length);
        if (!Operations.IsConstructor(c)) throw JsErrors.TypeError(realm, "Array species is not a constructor");
        var lenV = new JsValue((double)length);
        intrinsic = false;
        return realm.Vm.Construct(c, new ReadOnlySpan<JsValue>(in lenV)).AsObjectUnchecked;
    }

    /// <summary>
    /// Collects the elements of a result array whose final length is not known up front (filter, flat, flatMap). For a
    /// fresh intrinsic Array the elements are gathered in the Vm's scratch buffer and the array gets an exactly sized
    /// element store at the end: a result that grows by doubling allocates about twice its final size. Elements beyond
    /// one page go in further pages, and a result too long for a store below the large object threshold keeps them as
    /// they are, with nothing copied (see <see cref="JsArray.PageSize"/>). A species-created result (user code may hold
    /// it) is appended to as elements are produced.
    /// </summary>
    private sealed class ResultSink
    {
        private readonly Realm _realm;
        private readonly JsObject _target;
        /// <summary>The page being filled: the Vm's scratch buffer until it is a full page that gets kept, or the first one to outgrow it.</summary>
        private JsValue[]? _buf;
        private int _n;
        /// <summary>Elements that fit in <see cref="_buf"/>: its length, at most a page (the scratch buffer can be longer).</summary>
        private int _limit;
        private List<JsValue[]>? _full;
        private long _index;

        public ResultSink(Realm realm, JsObject target, bool buffered, long expected)
        {
            _realm = realm;
            _target = target;
            if (buffered)
            {
                _buf = realm.Vm.TakeScratch((int)Math.Min(Math.Max(expected, 8), JsArray.PageSize));
                _limit = Math.Min(_buf.Length, JsArray.PageSize);
            }
        }

        private long BufferedCount => (_full != null ? (long)_full.Count << JsArray.PageShift : 0) + _n;

        public long Index => _buf != null ? BufferedCount : _index;

        public void Append(JsValue v)
        {
            var buf = _buf;
            if (buf != null)
            {
                if (_n == _limit) buf = NextBuffer();
                buf[_n++] = v;
            }
            else AppendResult(_target, ref _index, v);
        }

        private JsValue[] NextBuffer()
        {
            var buf = _buf!;
            if (_limit < JsArray.PageSize)
            {
                _limit = Math.Min(_limit * 2, JsArray.PageSize);
                Array.Resize(ref buf, _limit);
                _buf = buf;
                return buf;
            }
            if (BufferedCount >= _realm.MaxArrayLength) throw JsErrors.RangeError(_realm, "Invalid array length");
            // only a buffer of exactly one page can be kept as it is
            _realm.ChargeBytes(JsArray.PageSize * 24L);
            (_full ??= new()).Add(buf.Length == JsArray.PageSize ? buf : buf.AsSpan(0, JsArray.PageSize).ToArray());
            _n = 0;
            if (buf.Length == JsArray.PageSize) _buf = buf = new JsValue[JsArray.PageSize];
            return buf;
        }

        public JsObject Finish()
        {
            var buf = _buf;
            if (buf != null)
            {
                var arr = (JsArray)_target;
                long total = BufferedCount;
                if (total > 0)
                {
                    _realm.ChargeBytes(_n * 24L);
                    if (total > JsArray.MaxSmallStore)
                    {
                        var pages = new JsValue[_full!.Count + (_n > 0 ? 1 : 0)][];
                        _full.CopyTo(pages);
                        if (_n > 0) pages[^1] = buf.AsSpan(0, _n).ToArray();
                        arr.AdoptPages(pages, (uint)total);
                    }
                    else
                    {
                        var items = new JsValue[total];
                        int at = 0;
                        if (_full != null) foreach (var page in _full) { Array.Copy(page, 0, items, at, page.Length); at += page.Length; }
                        Array.Copy(buf, 0, items, at, _n);
                        arr.Items = items;
                        arr.Length = (uint)total;
                    }
                }
                _realm.Vm.ReturnScratch(buf, _full != null && buf.Length != JsArray.PageSize ? _limit : _n);
                _buf = null;
            }
            return _target;
        }
    }

    /// <summary>ArrayCreate(length) for results that are then filled front to back with Push: capacity reserved, length 0.</summary>
    private static JsArray CreateArrayForAppend(Realm realm, long length)
    {
        if (length > uint.MaxValue) throw JsErrors.RangeError(realm, "Invalid array length");
        return new JsArray(realm, (int)Math.Min(length, 1 << 16));
    }

    private static JsArray CreateArray(Realm realm, long length)
    {
        if (length > uint.MaxValue) throw JsErrors.RangeError(realm, "Invalid array length");
        var a = new JsArray(realm, (int)Math.Min(length, 1 << 16));
        if (length > 0) a.SetLength((uint)length);
        return a;
    }

    /// <summary>Appends at index n of a species-created result (fast path for intrinsic dense arrays).</summary>
    private static void AppendResult(JsObject res, ref long n, JsValue v)
    {
        if (res is JsArray arr && arr.Dense && arr.Length == n && arr.Extensible && arr.LengthWritable) arr.Push(v);
        else res.CreateDataPropertyOrThrow(Key(n), v);
        n++;
    }

    private static void SetResultLength(JsObject res, long n)
    {
        if (res is JsArray arr && arr.Length == n) return;
        res.SetOrThrow("length", new JsValue((double)n));
    }

    private static void CheckLength(Realm realm, double newLength)
    {
        if (newLength > JsValue.MaxSafeInteger) throw JsErrors.TypeError(realm, "Array length exceeds 2^53 - 1");
    }

    /// <summary>Array.from with a constructor receiver other than %Array% (23.1.2.1).</summary>
    private static JsValue FromWithConstructor(Realm realm, JsValue c, JsValue items, JsValue mapFn, JsValue thisArg)
    {
        bool mapping = !mapFn.IsUndefined;
        var args = new JsValue[2];
        var usingIterator = Operations.GetV(realm, items, JsSymbol.Iterator);
        if (!usingIterator.IsNullOrUndefined)
        {
            if (!usingIterator.IsFunction) throw JsErrors.TypeError(realm, "Symbol.iterator is not a function");
            var obj = realm.Vm.Construct(c, ReadOnlySpan<JsValue>.Empty).AsObjectUnchecked;
            var it = IteratorRecord.Get(realm, items);
            long k = 0;
            while (it.Step(out var v))
            {
                try
                {
                    if (mapping) { args[0] = v; args[1] = new JsValue((double)k); v = realm.Vm.Call(mapFn, thisArg, args); }
                    obj.CreateDataPropertyOrThrow(Key(k), v);
                }
                catch (JsThrowException) { it.CloseQuiet(); throw; }
                k++;
                realm.Vm.Tick();
            }
            obj.SetOrThrow("length", new JsValue((double)k));
            return JsValue.FromObject(obj);
        }
        var arrayLike = Operations.ToObject(realm, items);
        long len = Operations.LengthOfArrayLike(realm, arrayLike);
        var lenV = new JsValue((double)len);
        var target = realm.Vm.Construct(c, new ReadOnlySpan<JsValue>(in lenV)).AsObjectUnchecked;
        for (long k = 0; k < len; k++)
        {
            var v = GetAt(arrayLike, k, JsValue.FromObject(arrayLike));
            if (mapping) { args[0] = v; args[1] = new JsValue((double)k); v = realm.Vm.Call(mapFn, thisArg, args); }
            target.CreateDataPropertyOrThrow(Key(k), v);
            realm.Vm.Tick();
        }
        target.SetOrThrow("length", lenV);
        return JsValue.FromObject(target);
    }

    /// <summary>Property key for an integer index up to 2^53-1 (array index when below 2^32-1).</summary>
    internal static PropertyKey Key(long i) => i < uint.MaxValue ? PropertyKey.FromIndex((uint)i) : new PropertyKey(NumberFormatting.ToString(i));

    private static JsValue GetAt(JsObject o, long i, JsValue receiver) => i < uint.MaxValue ? o.GetIndex((uint)i, receiver) : o.Get(Key(i), receiver);

    /// <summary>Set(O, i, v, true).</summary>
    private static void SetAtOrThrow(Realm realm, JsObject o, long i, JsValue v)
    {
        var self = JsValue.FromObject(o);
        if (!(i < uint.MaxValue ? o.SetIndex((uint)i, v, self) : o.Set(Key(i), v, self)))
            throw JsErrors.TypeError(realm, $"Cannot assign to element {i} of {o.ClassName}");
    }

    public static bool IsArray(Realm realm, in JsValue v)
    {
        if (v.IsNotObject) return false;
        if (v.Ref is JsArray) return true;
        if (v.Ref is JsProxy p) return p.IsArrayTarget();
        return ReferenceEquals(v.Ref, realm.ArrayPrototype);
    }

    private static JsArray CopyToArray(Realm realm, JsObject o)
    {
        long len = Operations.LengthOfArrayLike(realm, o);
        if (len > realm.MaxArrayLength) throw JsErrors.RangeError(realm, "Invalid array length");
        var res = new JsArray(realm, (int)len);
        for (long i = 0; i < len; i++) { res.Push(GetAt(o, i, JsValue.FromObject(o))); realm.Vm.Tick(); }
        return res;
    }

    /// <summary>
    /// <c>join</c> of a dense array of strings, no join in progress: sized in one pass and built in one allocation, instead of
    /// growing a StringBuilder and copying it out. Reading such elements and converting strings cannot be observed, so the
    /// result, the RangeError and the charges are those of the general loop. Returns null for anything else.
    /// </summary>
    private static string? TryJoinStrings(Realm realm, JsObject o, string sep, long len)
    {
        if (o is not JsArray arr || !arr.Dense || len != arr.Length || len > arr.Items.Length || len < 2 || realm.JoinStack.Count != 0) return null;
        var items = arr.Items;
        long total = (len - 1) * sep.Length;
        for (int i = 0; i < len; i++)
        {
            if (!items[i].IsString) return null;
            total += items[i].StringLength;
        }
        if (total > realm.MaxStringLength) throw JsErrors.RangeError(realm, "Invalid string length");
        realm.Vm.Tick((int)len);
        realm.ChargeOutput(total);
        return string.Create((int)total, (items, (int)len, sep), static (span, st) =>
        {
            var (xs, n, sp) = st;
            int pos = 0;
            for (int i = 0; i < n; i++)
            {
                if (i > 0) { sp.AsSpan().CopyTo(span.Slice(pos)); pos += sp.Length; }
                var s = xs[i].AsStringUnchecked;
                s.AsSpan().CopyTo(span.Slice(pos));
                pos += s.Length;
            }
        });
    }

    public static string Join(Realm realm, JsObject o, string sep, long len = -1)
    {
        if (len >= 0 && TryJoinStrings(realm, o, sep, len) is { } joined) return joined;
        if (!realm.JoinStack.Add(o)) return "";
        realm.Vm.EnsureNativeStack();
        try
        {
            if (len < 0) len = Operations.LengthOfArrayLike(realm, o);
            var sb = new StringBuilder();
            int charged = 0;
            for (long i = 0; i < len; i++)
            {
                if (i > 0) sb.Append(sep);
                var v = GetAt(o, i, JsValue.FromObject(o));
                if (!v.IsNullOrUndefined) sb.Append(v.IsString ? v.AsStringUnchecked : Operations.ToString(realm, v));
                if (sb.Length > realm.MaxStringLength) throw JsErrors.RangeError(realm, "Invalid string length");
                realm.Vm.Tick();
                // charge as the result grows (not once at the end), so a huge join stops at MaxTrackedBytes
                if (sb.Length - charged >= 4096) { realm.ChargeOutput(sb.Length - charged); charged = sb.Length; }
            }
            realm.ChargeOutput(sb.Length - charged);
            return sb.ToString();
        }
        finally { realm.JoinStack.Remove(o); }
    }

    /// <summary>kind: 0 forEach, 1 map, 2 filter, 3 some, 4 every.</summary>
    private static JsValue Iterate(in NativeArgs a, int kind)
    {
        var realm = a.Realm;
        var o = Operations.ToObject(realm, a.This);
        long len = Operations.LengthOfArrayLike(realm, o);
        var cb = a[0];
        if (!cb.IsFunction) throw JsErrors.TypeError(realm, $"{JsErrors.Describe(cb)} is not a function");
        var fn = (JsFunctionObject)cb.Ref!;
        var thisArg = a[1];
        JsObject? result = kind == 1 ? ArraySpeciesCreate(realm, o, len) : null;
        ResultSink? sink = null;
        if (kind == 2) { result = ArraySpeciesCreate(realm, o, 0, out bool intrinsic); sink = new ResultSink(realm, result, intrinsic, len); }
        var self = JsValue.FromObject(o);
        var vm = realm.Vm;
        vm.TryPrepareFastCalls(cb, out var fast);
        var args = fast == null ? new JsValue[3] : null;
        if (args != null) args[2] = self;
        for (long i = 0; i < len; i++)
        {
            vm.Tick();
            JsValue v;
            if (o is JsArray arr && arr.TryGetElement((uint)i, out v)) { }
            else
            {
                var key = Key(i);
                if (!o.HasProperty(key)) continue;
                v = o.Get(key);
            }
            JsValue r;
            var iv = new JsValue(i);
            if (fast != null) r = vm.CallCallback(fast, thisArg, v, iv, self, self, 3);
            else
            {
                args![0] = v;
                args[1] = iv;
                r = fn.Call(thisArg, args);
            }
            switch (kind)
            {
                case 1:
                    if (result is JsArray ra && ra.Dense && i < ra.Items.Length && i < ra.Length) ra.Items[i] = r;
                    else result!.CreateDataPropertyOrThrow(Key(i), r);
                    break;
                case 2: if (Operations.ToBoolean(r)) sink!.Append(v); break;
                case 3: if (Operations.ToBoolean(r)) return JsValue.True; break;
                case 4: if (!Operations.ToBoolean(r)) return JsValue.False; break;
            }
        }
        return kind switch
        {
            1 => JsValue.FromObject(result!),
            2 => JsValue.FromObject(sink!.Finish()),
            3 => JsValue.False,
            4 => JsValue.True,
            _ => JsValue.Undefined,
        };
    }

    private static JsValue Find(in NativeArgs a, bool fromEnd, bool index)
    {
        var realm = a.Realm;
        var o = Operations.ToObject(realm, a.This);
        long len = Operations.LengthOfArrayLike(realm, o);
        var cb = a[0];
        if (!cb.IsFunction) throw JsErrors.TypeError(realm, $"{JsErrors.Describe(cb)} is not a function");
        var self = JsValue.FromObject(o);
        var vm = realm.Vm;
        vm.TryPrepareFastCalls(cb, out var fast);
        var args = fast == null ? new JsValue[3] : null;
        if (args != null) args[2] = self;
        var thisArg = a[1];
        for (long n = 0; n < len; n++)
        {
            long i = fromEnd ? len - 1 - n : n;
            var v = o is JsArray arr && arr.TryGetElement((uint)i, out var dv) ? dv : GetAt(o, i, self);
            JsValue r;
            var iv = new JsValue(i);
            if (fast != null) r = vm.CallCallback(fast, thisArg, v, iv, self, self, 3);
            else
            {
                args![0] = v;
                args[1] = iv;
                r = vm.Call(cb, thisArg, args);
            }
            if (Operations.ToBoolean(r)) return index ? new JsValue(i) : v;
            vm.Tick();
        }
        return index ? new JsValue(-1) : JsValue.Undefined;
    }

    private static JsValue Reduce(in NativeArgs a, bool right)
    {
        var realm = a.Realm;
        var o = Operations.ToObject(realm, a.This);
        long len = Operations.LengthOfArrayLike(realm, o);
        var cb = a[0];
        if (!cb.IsFunction) throw JsErrors.TypeError(realm, $"{JsErrors.Describe(cb)} is not a function");
        long k = 0;
        JsValue acc = default;
        bool have = a.Length >= 2;
        if (have) acc = a[1];
        var self = JsValue.FromObject(o);
        while (!have && k < len)
        {
            realm.Vm.Tick();
            long i = right ? len - 1 - k : k;
            var key = Key(i);
            if (o.HasProperty(key)) { acc = o.Get(key); have = true; }
            k++;
        }
        if (!have) throw JsErrors.TypeError(realm, "Reduce of empty array with no initial value");
        var vm = realm.Vm;
        vm.TryPrepareFastCalls(cb, out var fast);
        var args = fast == null ? new JsValue[4] : null;
        if (args != null) args[3] = self;
        for (; k < len; k++)
        {
            vm.Tick();
            long i = right ? len - 1 - k : k;
            JsValue v;
            if (o is JsArray arr && arr.TryGetElement((uint)i, out v)) { }
            else
            {
                var key = Key(i);
                if (!o.HasProperty(key)) continue;
                v = o.Get(key);
            }
            var iv = new JsValue(i);
            if (fast != null) acc = vm.CallCallback(fast, JsValue.Undefined, acc, v, iv, self, 4);
            else
            {
                args![0] = acc;
                args[1] = v;
                args[2] = iv;
                acc = vm.Call(cb, JsValue.Undefined, args);
            }
        }
        return acc;
    }

    private static void SortObject(Realm realm, JsObject o, JsValue cmp)
    {
        long len = Operations.LengthOfArrayLike(realm, o);
        var vm = realm.Vm;
        // SortIndexedProperties: collect the present values first (the comparator may mutate the object), sort the
        // list, write back. The list is one exactly sized buffer for ordinary lengths; the merge buffer is the Vm's
        // reusable scratch (taken out for the duration, so a comparator that sorts another array gets its own).
        var items = new JsValue[(int)Math.Min(len, 1 << 20)];
        int count = 0;
        for (long i = 0; i < len; i++)
        {
            vm.Tick();
            JsValue v;
            if (o is JsArray arr && arr.TryGetElement((uint)i, out v)) { }
            else
            {
                var key = Key(i);
                if (!o.HasProperty(key)) continue;
                v = o.Get(key);
            }
            if (count == items.Length) Array.Resize(ref items, items.Length * 2);
            items[count++] = v;
        }
        if (count > 1)
        {
            var tmp = vm.TakeScratch(count);
            try
            {
                if (cmp.IsUndefined)
                {
                    if (!TrySortByStringKeys(realm, items, tmp, count)) { var c = new DefaultSortCmp(realm); StableSort(items, tmp, count, ref c); }
                }
                else if (vm.TryPrepareFastCalls(cmp, out var closure))
                {
                    var c = new ClosureSortCmp(vm, closure);
                    int numeric = MatchNumericComparator(closure.Template);
                    if (numeric != 0) { var nc = new NumericSortCmp(c, vm, numeric < 0); StableSort(items, tmp, count, ref nc); }
                    else StableSort(items, tmp, count, ref c);
                }
                else { var c = new GenericSortCmp(realm, cmp, new JsValue[2]); StableSort(items, tmp, count, ref c); }
            }
            finally { vm.ReturnScratch(tmp, count); }
        }
        for (int i = 0; i < count; i++) { SetAtOrThrow(realm, o, i, items[i]); vm.Tick(); }
        for (long i = count; i < len; i++) { DeleteOrThrow(realm, o, Key(i)); vm.Tick(); }
    }

    private interface ISortCmp<T> { int Compare(in T x, in T y); }

    /// <summary>Undefined sorts last; returns false when both are defined (the comparator decides).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool UndefinedOrder(in JsValue x, in JsValue y, out int r)
    {
        if (x.IsUndefined) { r = y.IsUndefined ? 0 : 1; return true; }
        if (y.IsUndefined) { r = -1; return true; }
        r = 0;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Sign(double v) => (v > 0 ? 1 : 0) - (v < 0 ? 1 : 0);   // no branch on the data; NaN gives 0

    /// <summary>A plain closure comparator: pushed and run directly (no delegate, no per-call native stack probe).</summary>
    private struct ClosureSortCmp(Vm vm, JsClosure closure) : ISortCmp<JsValue>
    {
        public int Compare(in JsValue x, in JsValue y)
        {
            if (UndefinedOrder(x, y, out int r)) return r;
            vm.Tick();
            var v = vm.CallCallback(closure, JsValue.Undefined, x, y, y, y, 2);
            return Sign(v.IsNumber ? v.Num : Operations.ToNumber(vm.Realm, v));
        }
    }

    private struct GenericSortCmp(Realm realm, JsValue cmp, JsValue[] args) : ISortCmp<JsValue>
    {
        public int Compare(in JsValue x, in JsValue y)
        {
            if (UndefinedOrder(x, y, out int r)) return r;
            realm.Vm.Tick();
            args[0] = x; args[1] = y;
            return Sign(Operations.ToNumber(realm, realm.Vm.Call(cmp, JsValue.Undefined, args)));
        }
    }

    private struct DefaultSortCmp(Realm realm) : ISortCmp<JsValue>
    {
        public int Compare(in JsValue x, in JsValue y)
        {
            if (UndefinedOrder(x, y, out int r)) return r;
            realm.Vm.Tick();
            return string.CompareOrdinal(Operations.ToString(realm, x), Operations.ToString(realm, y));
        }
    }

    /// <summary>
    /// Whether a comparator is exactly <c>(a, b) =&gt; a - b</c> (1) or <c>(a, b) =&gt; b - a</c> (-1), else 0: the bytecode is
    /// <c>Sub dst, r0, r1; Ret dst</c> with the two parameters in r0 and r1 and nothing before it. Subtracting two numbers
    /// runs no user code and observes nothing, so such a comparator need not be called while both operands are numbers
    /// (SpiderMonkey's array sort recognizes the same two comparators).
    /// </summary>
    private static int MatchNumericComparator(FunctionTemplate t)
    {
        var code = t.Code;
        if (t.ParamCount != 2 || (t.Flags & FunctionFlags.HasRest) != 0 || code.Length < 6 || t.Handlers.Length != 0) return 0;
        if ((Op)code[0] != Op.Sub || (Op)code[4] != Op.Ret || code[5] != code[1]) return 0;
        if (code[2] == 0 && code[3] == 1) return 1;
        if (code[2] == 1 && code[3] == 0) return -1;
        return 0;
    }

    /// <summary>A comparator matched by <see cref="MatchNumericComparator"/>; any operand but a number (a string, a BigInt, an object with valueOf) calls the closure.</summary>
    private struct NumericSortCmp(ClosureSortCmp call, Vm vm, bool reversed) : ISortCmp<JsValue>
    {
        private ClosureSortCmp _call = call;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(in JsValue x, in JsValue y)
        {
            if (x.IsNumber && y.IsNumber)
            {
                vm.Tick();
                return Sign(reversed ? y.Num - x.Num : x.Num - y.Num);
            }
            return _call.Compare(x, y);
        }
    }

    /// <summary>
    /// Default sort when every value is a primitive other than a symbol. Their string forms can be computed once and in any
    /// order, since no user code runs and nothing can throw; a number's default order otherwise formats two strings per
    /// comparison. Sorts positions rather than values because equal strings can come from different values (1 and "1").
    /// False when some value's ToString is observable (object) or throws (symbol): SortCompare converts per comparison then.
    /// </summary>
    private static bool TrySortByStringKeys(Realm realm, JsValue[] items, JsValue[] tmp, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var tag = items[i].Tag;
            if (tag == JsTag.Object || tag == JsTag.Symbol) return false;
        }
        var keys = new string[count];
        var order = new int[count];
        int defined = 0;
        for (int i = 0; i < count; i++)
        {
            ref readonly var v = ref items[i];
            if (v.IsUndefined) continue;   // these sort last, and all are alike
            keys[i] = v.IsString ? v.AsStringUnchecked : Operations.ToString(realm, v);
            order[defined++] = i;
        }
        var cmp = new KeySortCmp(realm.Vm, keys);
        StableSort(order, new int[defined], defined, ref cmp);
        Array.Copy(items, tmp, count);
        for (int i = 0; i < defined; i++) items[i] = tmp[order[i]];
        Array.Clear(items, defined, count - defined);
        return true;
    }

    private struct KeySortCmp(Vm vm, string[] keys) : ISortCmp<int>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(in int x, in int y)
        {
            vm.Tick();
            return string.CompareOrdinal(keys[x], keys[y]);
        }
    }

    private const int MaxMinRun = 32;

    /// <summary>
    /// Stable merge sort of <c>a[0..n)</c> with natural runs (TimSort's run detection, run extension by binary insertion and
    /// merge policy, without galloping): an ordered or reversed input takes n comparisons, and a random one fewer comparisons
    /// and far fewer element copies than merging from single elements. <paramref name="tmp"/> holds at least n elements.
    /// A comparator's result only steers indices that stay in bounds, so an inconsistent comparator leaves a permutation.
    /// </summary>
    private static void StableSort<T, TCmp>(T[] a, T[] tmp, int n, ref TCmp cmp) where TCmp : struct, ISortCmp<T>
    {
        if (n < 2) return;
        int minRun = n, extra = 0;
        while (minRun >= MaxMinRun) { extra |= minRun & 1; minRun >>= 1; }
        minRun += extra;
        Span<int> runStart = stackalloc int[64], runLen = stackalloc int[64];   // run lengths grow at least like Fibonacci numbers
        int sp = 0;
        for (int lo = 0; lo < n;)
        {
            int run = 1;
            if (lo + 1 < n)
            {
                run = 2;
                if (cmp.Compare(a[lo + 1], a[lo]) < 0)
                {
                    while (lo + run < n && cmp.Compare(a[lo + run], a[lo + run - 1]) < 0) run++;
                    Array.Reverse(a, lo, run);   // strictly descending, so no equal elements change order
                }
                else
                {
                    while (lo + run < n && cmp.Compare(a[lo + run], a[lo + run - 1]) >= 0) run++;
                }
            }
            if (run < minRun)
            {
                int end = Math.Min(lo + minRun, n);
                BinaryInsertion(a, lo, lo + run, end, ref cmp);
                run = end - lo;
            }
            runStart[sp] = lo;
            runLen[sp++] = run;
            lo += run;
            // merge policy with the invariant fix of de Gouw et al. (2015)
            while (sp > 1)
            {
                int i = sp - 2;
                if ((i > 0 && runLen[i - 1] <= runLen[i] + runLen[i + 1]) || (i > 1 && runLen[i - 2] <= runLen[i - 1] + runLen[i]))
                {
                    if (runLen[i - 1] < runLen[i + 1]) i--;
                }
                else if (runLen[i] > runLen[i + 1]) break;
                MergeAt(a, tmp, runStart, runLen, ref sp, i, ref cmp);
            }
        }
        while (sp > 1)
        {
            int i = sp - 2;
            if (i > 0 && runLen[i - 1] < runLen[i + 1]) i--;
            MergeAt(a, tmp, runStart, runLen, ref sp, i, ref cmp);
        }
    }

    private static void MergeAt<T, TCmp>(T[] a, T[] tmp, Span<int> runStart, Span<int> runLen, ref int sp, int i, ref TCmp cmp) where TCmp : struct, ISortCmp<T>
    {
        Merge(a, tmp, runStart[i], runStart[i + 1], runStart[i + 1] + runLen[i + 1], ref cmp);
        runLen[i] += runLen[i + 1];
        if (i == sp - 3) { runStart[i + 1] = runStart[i + 2]; runLen[i + 1] = runLen[i + 2]; }
        sp--;
    }

    /// <summary>Extends the sorted <c>a[lo..start)</c> to <c>a[lo..end)</c>. Which half a comparison selects is unpredictable by nature, so it does not take a branch.</summary>
    private static void BinaryInsertion<T, TCmp>(T[] a, int lo, int start, int end, ref TCmp cmp) where TCmp : struct, ISortCmp<T>
    {
        for (int i = start; i < end; i++)
        {
            T pivot = a[i];
            int l = lo, r = i;
            while (l < r)
            {
                int m = (l + r) >>> 1;
                int below = cmp.Compare(pivot, a[m]) >> 31;   // -1 when pivot < a[m], else 0
                r = (m & below) | (r & ~below);
                l = (l & below) | ((m + 1) & ~below);
            }
            a.AsSpan(l, i - l).CopyTo(a.AsSpan(l + 1));
            a[l] = pivot;
        }
    }

    /// <summary>Merges the sorted <c>a[lo..mid)</c> and <c>a[mid..hi)</c>; the left element wins ties. Selects without a branch, like <see cref="BinaryInsertion"/>.</summary>
    private static void Merge<T, TCmp>(T[] a, T[] tmp, int lo, int mid, int hi, ref TCmp cmp) where TCmp : struct, ISortCmp<T>
    {
        if (cmp.Compare(a[mid], a[mid - 1]) >= 0) return;   // already in order
        Array.Copy(a, lo, tmp, lo, hi - lo);
        int i = lo, j = mid, k = lo;
        while (i < mid && j < hi)
        {
            int right = cmp.Compare(tmp[j], tmp[i]) >> 31;   // -1 when the right element comes first, else 0
            a[k++] = tmp[i + ((j - i) & right)];
            j -= right;
            i += 1 + right;
        }
        // what remains of the right run is already in place: j == k once the left run is used up
        if (i < mid) Array.Copy(tmp, i, a, k, mid - i);
    }

    /// <summary>FlattenIntoArray (23.1.3.13.1).</summary>
    private static void Flatten(Realm realm, ResultSink sink, JsObject source, long len, double depth, JsValue mapper, JsValue thisArg)
    {
        var vm = realm.Vm;
        JsValue[]? args = null;
        JsClosure? fast = null;
        if (!mapper.IsUndefined)
        {
            vm.TryPrepareFastCalls(mapper, out fast);
            if (fast == null)
            {
                args = new JsValue[3];
                args[2] = JsValue.FromObject(source);
            }
        }
        for (long i = 0; i < len; i++)
        {
            // a present own element of a dense array is exactly what HasProperty + Get would observe (re-checked per
            // element: the mapper may change the array)
            JsValue v;
            if (source is JsArray sa && sa.TryGetElement((uint)i, out v)) { }
            else
            {
                var key = Key(i);
                if (!source.HasProperty(key)) continue;
                v = source.Get(key);
            }
            if (fast != null)
            {
                var iv = new JsValue((double)i);
                var self = JsValue.FromObject(source);
                v = vm.CallCallback(fast, thisArg, v, iv, self, self, 3);
            }
            else if (args != null)
            {
                args[0] = v; args[1] = new JsValue((double)i);
                v = vm.Call(mapper, thisArg, args);
            }
            if (depth > 0 && IsArray(realm, v))
            {
                var inner = v.AsObjectUnchecked;
                long innerLen = Operations.LengthOfArrayLike(realm, inner);
                realm.Vm.EnterNative();
                try { Flatten(realm, sink, inner, innerLen, depth - 1, JsValue.Undefined, JsValue.Undefined); }
                finally { realm.Vm.ExitNative(); }
            }
            else
            {
                if (sink.Index >= 9007199254740991) throw JsErrors.TypeError(realm, "Array too long");
                sink.Append(v);
            }
            vm.Tick();
        }
    }

    private static void DeleteOrThrow(Realm realm, JsObject o, in PropertyKey key)
    {
        if (!o.Delete(key)) throw JsErrors.TypeError(realm, $"Cannot delete property '{key}' of {o.ClassName}");
    }
}
