using System;
using System.Collections.Generic;
using System.Text;

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
            var result = realm.NewArray();
            var args = new JsValue[2];
            if (items.IsNullOrUndefined) throw JsErrors.TypeError(realm, $"{items} is not iterable");
            var usingIterator = Operations.GetV(realm, items, JsSymbol.Iterator);
            if (!usingIterator.IsNullOrUndefined)
            {
                var it = IteratorRecord.Get(realm, items);
                double k = 0;
                while (it.Step(out var v))
                {
                    if (mapping)
                    {
                        args[0] = v; args[1] = new JsValue(k);
                        try { v = realm.Vm.Call(mapFn, thisArg, args); }
                        catch (JsThrowException) { it.CloseQuiet(); throw; }
                    }
                    result.Push(v);
                    k++;
                    realm.Vm.Tick();
                }
                return JsValue.FromObject(result);
            }
            var o = Operations.ToObject(realm, items);
            long len = Operations.LengthOfArrayLike(realm, o);
            for (long i = 0; i < len; i++)
            {
                var v = GetAt(o, i, JsValue.FromObject(o));
                if (mapping) { args[0] = v; args[1] = new JsValue(i); v = realm.Vm.Call(mapFn, thisArg, args); }
                result.Push(v);
                realm.Vm.Tick();
            }
            return JsValue.FromObject(result);
        });

        r.Method(proto, "push", 1, PushFn);
        r.Method(proto, "pop", 0, static (in NativeArgs a) =>
        {
            if (a.This.Ref is JsArray arr && arr.Dense && arr.LengthWritable)
            {
                if (arr.Length == 0) return JsValue.Undefined;
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
            if (a.This.Ref is JsArray arr && arr.Dense && arr.LengthWritable && arr.Extensible && arr.Length <= arr.Items.Length && !JsArray.ProtoChainMayHaveIndices(arr.Proto))
            {
                if (arr.Length == 0) return JsValue.Undefined;
                var first = arr.Items[0];
                Array.Copy(arr.Items, 1, arr.Items, 0, (int)arr.Length - 1);
                arr.Items[arr.Length - 1] = JsValue.Empty;
                arr.Length--;
                realm.Vm.Tick((int)Math.Min(arr.Length / 64, 1000));
                return first.IsEmpty ? JsValue.Undefined : first;
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
                if (o is JsArray arr && arr.Dense && arr.Extensible && arr.LengthWritable && arr.Length <= arr.Items.Length && !JsArray.ProtoChainMayHaveIndices(arr.Proto))
                {
                    arr.EnsureCapacity(arr.Length + (uint)n);
                    Array.Copy(arr.Items, 0, arr.Items, n, (int)arr.Length);
                    for (int i = 0; i < n; i++) arr.Items[i] = a.Args[i];
                    arr.Length += (uint)n;
                    return new JsValue(arr.Length);
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
            var resObj = ArraySpeciesCreate(realm, o, count);
            if (resObj is JsArray res && res.Dense && res.Length == count && o is JsArray arr && arr.Dense && end <= arr.Items.Length && !JsArray.ProtoChainMayHaveIndices(arr.Proto))
            {
                res.SetLength(0);
                for (long i = k; i < end; i++) { var v = arr.Items[i]; if (v.IsEmpty) res.PushHole(); else res.Push(v); }
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
            var removedObj = ArraySpeciesCreate(realm, o, delCount);
            if (removedObj is JsArray removed && removed.Dense && o is JsArray arr && arr.Dense && arr.Extensible && arr.LengthWritable && arr.Length <= arr.Items.Length && !JsArray.ProtoChainMayHaveIndices(arr.Proto))
            {
                removed.SetLength(0);
                for (long i = 0; i < delCount; i++) { var v = arr.Items[start + i]; if (v.IsEmpty) removed.PushHole(); else removed.Push(v); }
                long newLen = len - delCount + items.Length;
                if (items.Length != delCount)
                {
                    arr.EnsureCapacity((uint)Math.Max(newLen, len));
                    Array.Copy(arr.Items, start + delCount, arr.Items, start + items.Length, len - start - delCount);
                    for (long i = newLen; i < len; i++) arr.Items[i] = JsValue.Empty;
                }
                for (int i = 0; i < items.Length; i++) arr.Items[start + i] = items[i];
                arr.Length = (uint)newLen;
                realm.Vm.Tick((int)Math.Min(len / 64, 1000));
                return JsValue.FromObject(removed);
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
            var species = ArraySpeciesCreate(realm, o, 0);
            var res = species as JsArray ?? realm.NewArray();
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
                if (e is JsArray ea && ea.Dense && ea.Length <= ea.Items.Length && !JsArray.ProtoChainMayHaveIndices(ea.Proto))
                {
                    res.EnsureCapacity(res.Length + ea.Length);
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
                    if (res.TryGetDense(i, out var v)) species.CreateDataPropertyOrThrow(PropertyKey.FromIndex(i), v);
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
            var o = Operations.ToObject(realm, a.This);
            long len = Operations.LengthOfArrayLike(realm, o);
            var sb = new StringBuilder();
            for (long i = 0; i < len; i++)
            {
                realm.Vm.Tick();
                if (i > 0) sb.Append(',');
                var v = GetAt(o, i, JsValue.FromObject(o));
                if (!v.IsNullOrUndefined) sb.Append(Operations.ToString(realm, Operations.Invoke(realm, v, "toLocaleString", localeArgs)));
            }
            return new JsValue(sb.ToString());
        });
        r.Method(proto, "reverse", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var o = Operations.ToObject(realm, a.This);
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
            for (long i = k; i >= 0; i--)
            {
                var key = Key(i);
                if (o.HasProperty(key) && Operations.StrictEquals(o.Get(key), a[0])) return new JsValue(i);
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
            for (long i = k; i < end; i++) { SetAt(o, i, a[0], JsValue.FromObject(o)); realm.Vm.Tick(); }
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
            var res = CreateArray(realm, len);
            for (long k = 0; k < len; k++)
            {
                res.CreateDataPropertyOrThrow(Key(k), GetAt(o, len - k - 1, JsValue.FromObject(o)));
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
            var res = CreateArray(realm, (long)newLen);
            long i = 0;
            for (; i < start; i++) { res.CreateDataPropertyOrThrow(Key(i), GetAt(o, i, JsValue.FromObject(o))); realm.Vm.Tick(); }
            for (int j = 2; j < a.Length; j++) res.CreateDataPropertyOrThrow(Key(i++), a.Args[j]);
            for (long r2 = start + skip; i < (long)newLen; i++, r2++) { res.CreateDataPropertyOrThrow(Key(i), GetAt(o, r2, JsValue.FromObject(o))); realm.Vm.Tick(); }
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
            var res = CreateArray(realm, len);
            for (long k = 0; k < len; k++)
            {
                res.CreateDataPropertyOrThrow(Key(k), k == (long)idx ? a[1] : GetAt(o, k, JsValue.FromObject(o)));
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
            var res = ArraySpeciesCreate(realm, o, 0);
            long index = 0;
            Flatten(realm, res, ref index, o, len, depth, JsValue.Undefined, JsValue.Undefined);
            return JsValue.FromObject(res);
        });
        r.Method(proto, "flatMap", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var o = Operations.ToObject(realm, a.This);
            long len = Operations.LengthOfArrayLike(realm, o);
            if (!a[0].IsFunction) throw JsErrors.TypeError(realm, "flatMap mapper function is not callable");
            var res = ArraySpeciesCreate(realm, o, 0);
            long index = 0;
            Flatten(realm, res, ref index, o, len, 1, a[0], a[1]);
            return JsValue.FromObject(res);
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
    public static JsObject ArraySpeciesCreate(Realm realm, JsObject original, long length)
    {
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
        return realm.Vm.Construct(c, new ReadOnlySpan<JsValue>(in lenV)).AsObjectUnchecked;
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

    private static bool SetAt(JsObject o, long i, JsValue v, JsValue receiver) => i < uint.MaxValue ? o.SetIndex((uint)i, v, receiver) : o.Set(Key(i), v, receiver);

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

    public static string Join(Realm realm, JsObject o, string sep, long len = -1)
    {
        if (!realm.JoinStack.Add(o)) return "";
        try
        {
            if (len < 0) len = Operations.LengthOfArrayLike(realm, o);
            var sb = new StringBuilder();
            for (long i = 0; i < len; i++)
            {
                if (i > 0) sb.Append(sep);
                var v = GetAt(o, i, JsValue.FromObject(o));
                if (!v.IsNullOrUndefined) sb.Append(v.IsString ? v.AsStringUnchecked : Operations.ToString(realm, v));
                if (sb.Length > realm.MaxStringLength) throw JsErrors.RangeError(realm, "Invalid string length");
                realm.Vm.Tick();
            }
            realm.ChargeBytes(sb.Length * 2L);
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
        JsObject? result = kind == 1 ? ArraySpeciesCreate(realm, o, len) : kind == 2 ? ArraySpeciesCreate(realm, o, 0) : null;
        long outIndex = 0;
        var args = new JsValue[3];
        args[2] = JsValue.FromObject(o);
        var vm = realm.Vm;
        for (long i = 0; i < len; i++)
        {
            vm.Tick();
            JsValue v;
            if (o is JsArray arr && arr.TryGetDense((uint)i, out v)) { }
            else
            {
                var key = Key(i);
                if (!o.HasProperty(key)) continue;
                v = o.Get(key);
            }
            args[0] = v;
            args[1] = new JsValue(i);
            var r = fn.Call(thisArg, args);
            switch (kind)
            {
                case 1:
                    if (result is JsArray ra && ra.Dense && i < ra.Items.Length && i < ra.Length) ra.Items[i] = r;
                    else result!.CreateDataPropertyOrThrow(Key(i), r);
                    break;
                case 2: if (Operations.ToBoolean(r)) AppendResult(result!, ref outIndex, v); break;
                case 3: if (Operations.ToBoolean(r)) return JsValue.True; break;
                case 4: if (!Operations.ToBoolean(r)) return JsValue.False; break;
            }
        }
        return kind switch
        {
            1 or 2 => JsValue.FromObject(result!),
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
        var args = new JsValue[3];
        args[2] = JsValue.FromObject(o);
        for (long n = 0; n < len; n++)
        {
            long i = fromEnd ? len - 1 - n : n;
            var v = GetAt(o, i, JsValue.FromObject(o));
            args[0] = v;
            args[1] = new JsValue(i);
            if (Operations.ToBoolean(realm.Vm.Call(cb, a[1], args))) return index ? new JsValue(i) : v;
            realm.Vm.Tick();
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
        var args = new JsValue[4];
        args[3] = self;
        for (; k < len; k++)
        {
            realm.Vm.Tick();
            long i = right ? len - 1 - k : k;
            JsValue v;
            if (o is JsArray arr && arr.TryGetDense((uint)i, out v)) { }
            else
            {
                var key = Key(i);
                if (!o.HasProperty(key)) continue;
                v = o.Get(key);
            }
            args[0] = acc;
            args[1] = v;
            args[2] = new JsValue(i);
            acc = realm.Vm.Call(cb, JsValue.Undefined, args);
        }
        return acc;
    }

    private static void SortObject(Realm realm, JsObject o, JsValue cmp)
    {
        long len = Operations.LengthOfArrayLike(realm, o);
        var items = new List<JsValue>((int)Math.Min(len, 1 << 20));
        long holes = 0;
        for (long i = 0; i < len; i++)
        {
            realm.Vm.Tick();
            var key = Key(i);
            if (o is JsArray arr && arr.TryGetDense((uint)i, out var dv)) items.Add(dv);
            else if (o.HasProperty(key)) items.Add(o.Get(key));
            else holes++;
        }
        var args = new JsValue[2];
        Comparison<JsValue> comparison = (x, y) =>
        {
            if (x.IsUndefined) return y.IsUndefined ? 0 : 1;
            if (y.IsUndefined) return -1;
            realm.Vm.Tick();
            if (!cmp.IsUndefined)
            {
                args[0] = x; args[1] = y;
                double v = Operations.ToNumber(realm, realm.Vm.Call(cmp, JsValue.Undefined, args));
                return double.IsNaN(v) ? 0 : v < 0 ? -1 : v > 0 ? 1 : 0;
            }
            if (x.IsNumber && y.IsNumber)
            {
                // default sort compares string forms; fast path for integers keeps correct ordering only if equal strings
                return string.CompareOrdinal(NumberFormatting.ToString(x.Num), NumberFormatting.ToString(y.Num));
            }
            return string.CompareOrdinal(Operations.ToString(realm, x), Operations.ToString(realm, y));
        };
        var arr2 = items.ToArray();
        MergeSort(arr2, comparison);
        for (int i = 0; i < arr2.Length; i++) { SetAt(o, i, arr2[i], JsValue.FromObject(o)); realm.Vm.Tick(); }
        for (long i = arr2.Length; i < len; i++) { o.Delete(Key(i)); realm.Vm.Tick(); }
        _ = holes;
    }

    /// <summary>Stable merge sort tolerant of inconsistent comparators.</summary>
    private static void MergeSort(JsValue[] a, Comparison<JsValue> cmp)
    {
        if (a.Length < 2) return;
        var tmp = new JsValue[a.Length];
        for (int width = 1; width < a.Length; width *= 2)
        {
            for (int lo = 0; lo < a.Length - width; lo += 2 * width)
            {
                int mid = lo + width, hi = Math.Min(lo + 2 * width, a.Length);
                int i = lo, j = mid, k = lo;
                while (i < mid && j < hi) tmp[k++] = cmp(a[j], a[i]) < 0 ? a[j++] : a[i++];
                while (i < mid) tmp[k++] = a[i++];
                while (j < hi) tmp[k++] = a[j++];
                Array.Copy(tmp, lo, a, lo, hi - lo);
            }
        }
    }

    /// <summary>FlattenIntoArray (23.1.3.13.1).</summary>
    private static void Flatten(Realm realm, JsObject target, ref long targetIndex, JsObject source, long len, double depth, JsValue mapper, JsValue thisArg)
    {
        var args = new JsValue[3];
        for (long i = 0; i < len; i++)
        {
            var key = Key(i);
            if (!source.HasProperty(key)) continue;
            var v = source.Get(key);
            if (!mapper.IsUndefined)
            {
                args[0] = v; args[1] = new JsValue((double)i); args[2] = JsValue.FromObject(source);
                v = realm.Vm.Call(mapper, thisArg, args);
            }
            if (depth > 0 && IsArray(realm, v))
            {
                var inner = v.AsObjectUnchecked;
                long innerLen = Operations.LengthOfArrayLike(realm, inner);
                realm.Vm.EnterNative();
                try { Flatten(realm, target, ref targetIndex, inner, innerLen, depth - 1, JsValue.Undefined, JsValue.Undefined); }
                finally { realm.Vm.ExitNative(); }
            }
            else
            {
                if (targetIndex >= 9007199254740991) throw JsErrors.TypeError(realm, "Array too long");
                target.CreateDataPropertyOrThrow(Key(targetIndex), v);
                targetIndex++;
            }
            realm.Vm.Tick();
        }
    }

    private static void DeleteOrThrow(Realm realm, JsObject o, in PropertyKey key)
    {
        if (!o.Delete(key)) throw JsErrors.TypeError(realm, $"Cannot delete property '{key}' of {o.ClassName}");
    }
}
