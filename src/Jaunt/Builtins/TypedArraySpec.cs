using System;
using System.Collections.Generic;

namespace Jaunt.Runtime;

/// <summary>%TypedArray% algorithms that depend on species / constructors (ECMA-262 23.2).</summary>
internal static class TypedArraySpec
{
    public static JsTypedArray Validate(Realm realm, in JsValue v)
    {
        if (v.IsObject && v.Ref is JsTypedArray t)
        {
            if (t.IsOutOfBounds) throw JsErrors.TypeError(realm, "TypedArray is out of bounds or its buffer is detached");
            return t;
        }
        throw JsErrors.TypeError(realm, "this is not a typed array");
    }

    /// <summary>ValidateTypedArray(O, seq-cst, write): also rejects typed arrays backed by an immutable buffer.</summary>
    public static JsTypedArray ValidateWrite(Realm realm, in JsValue v)
    {
        if (v.Ref is JsTypedArray { Buffer.Immutable: true }) throw JsErrors.TypeError(realm, "Cannot modify a TypedArray backed by an immutable ArrayBuffer");
        return Validate(realm, v);
    }

    private static JsObject DefaultConstructor(Realm realm, TypedKind kind) => realm.TypedArrayConstructors![(int)kind];

    /// <summary>TypedArrayCreateFromConstructor (23.2.4.2).</summary>
    public static JsTypedArray CreateFromConstructor(Realm realm, in JsValue ctor, ReadOnlySpan<JsValue> args, bool write = false)
    {
        var obj = realm.Vm.Construct(ctor, args);
        var ta = write ? ValidateWrite(realm, obj) : Validate(realm, obj);
        if (args.Length == 1 && args[0].IsNumber && ta.Length < args[0].Num)
            throw JsErrors.TypeError(realm, "Derived TypedArray constructor created an array which was too small");
        return ta;
    }

    /// <summary>TypedArraySpeciesCreate (23.2.4.1).</summary>
    public static JsTypedArray SpeciesCreate(Realm realm, JsTypedArray exemplar, ReadOnlySpan<JsValue> args, bool write = false)
    {
        var def = DefaultConstructor(realm, exemplar.Kind);
        var c = Operations.SpeciesConstructorDefault(realm, exemplar, def);
        var result = CreateFromConstructor(realm, JsValue.FromObject(c), args, write);
        if (result.IsBigIntKind != exemplar.IsBigIntKind) throw JsErrors.TypeError(realm, "TypedArray species has a different content type");
        return result;
    }

    private static JsTypedArray CreateSameType(Realm realm, JsTypedArray exemplar, int length)
    {
        var ctor = (JsObject)DefaultConstructor(realm, exemplar.Kind);
        var proto = (JsObject)ctor.Get("prototype").Ref!;
        return TypedArrayBuiltins.Create(realm, proto, exemplar.Kind, length);
    }

    private static int ToLengthArg(Realm realm, long n) => n > realm.MaxArrayLength ? throw JsErrors.RangeError(realm, "Invalid typed array length") : (int)n;

    public static void Install(Realm r, JsBuiltin ta, JsObject taProto)
    {
        r.Getter(ta, JsSymbol.Species, "[Symbol.species]", static (in NativeArgs a) => a.This);

        r.Method(ta, "from", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var c = a.This;
            if (!Operations.IsConstructor(c)) throw JsErrors.TypeError(realm, $"{c} is not a constructor");
            var mapfn = a[1];
            bool mapping = !mapfn.IsUndefined;
            if (mapping && !mapfn.IsFunction) throw JsErrors.TypeError(realm, $"{mapfn} is not a function");
            var source = a[0];
            var usingIterator = Operations.GetV(realm, source, JsSymbol.Iterator);
            if (!usingIterator.IsNullOrUndefined)
            {
                if (!usingIterator.IsFunction) throw JsErrors.TypeError(realm, "Symbol.iterator is not a function");
                List<JsValue> values;
                if (source.Ref is JsArray srcArr && realm.IsArrayIterationPristine(srcArr)) values = Operations.IterableToList(realm, source);
                else
                {
                    // GetIteratorFromMethod: the already-read @@iterator method is not looked up again
                    var itv = realm.Vm.Call(usingIterator, source, ReadOnlySpan<JsValue>.Empty);
                    if (itv.IsNotObject) throw JsErrors.TypeError(realm, "Result of the Symbol.iterator method is not an object");
                    var rec = IteratorRecord.FromIterator(realm, itv.AsObjectUnchecked);
                    values = new List<JsValue>();
                    while (rec.Step(out var item)) { values.Add(item); realm.Vm.Tick(); }
                }
                var len = new JsValue(values.Count);
                var target = CreateFromConstructor(realm, c, new ReadOnlySpan<JsValue>(in len), write: true);
                var margs = new JsValue[2];
                for (int k = 0; k < values.Count; k++)
                {
                    var v = values[k];
                    if (mapping) { margs[0] = v; margs[1] = new JsValue(k); v = realm.Vm.Call(mapfn, a[2], margs); }
                    target.Set(PropertyKey.FromIndex((uint)k), v, JsValue.FromObject(target));
                    realm.Vm.Tick();
                }
                return JsValue.FromObject(target);
            }
            var arrayLike = Operations.ToObject(realm, source);
            long n = Operations.LengthOfArrayLike(realm, arrayLike);
            var lenV = new JsValue((double)n);
            var t2 = CreateFromConstructor(realm, c, new ReadOnlySpan<JsValue>(in lenV), write: true);
            var args2 = new JsValue[2];
            for (long k = 0; k < n; k++)
            {
                var v = arrayLike.Get(PropertyKey.FromIndex((uint)k), JsValue.FromObject(arrayLike));
                if (mapping) { args2[0] = v; args2[1] = new JsValue((double)k); v = realm.Vm.Call(mapfn, a[2], args2); }
                t2.Set(PropertyKey.FromIndex((uint)k), v, JsValue.FromObject(t2));
                realm.Vm.Tick();
            }
            return JsValue.FromObject(t2);
        });

        r.Method(ta, "of", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var c = a.This;
            if (!Operations.IsConstructor(c)) throw JsErrors.TypeError(realm, $"{c} is not a constructor");
            var len = new JsValue(a.Length);
            var target = CreateFromConstructor(realm, c, new ReadOnlySpan<JsValue>(in len), write: true);
            for (int k = 0; k < a.Length; k++) target.Set(PropertyKey.FromIndex((uint)k), a.Args[k], JsValue.FromObject(target));
            return JsValue.FromObject(target);
        });

        InstallIteration(r, taProto);
        r.Method(taProto, "set", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var t = a.This.Ref as JsTypedArray ?? throw JsErrors.TypeError(realm, "this is not a typed array");
            if (t.Buffer.Immutable) throw JsErrors.TypeError(realm, "Cannot modify a TypedArray backed by an immutable ArrayBuffer");
            double off = Operations.ToIntegerOrInfinity(realm, a[1]);
            if (off < 0) throw JsErrors.RangeError(realm, "offset is out of bounds");
            if (t.IsOutOfBounds) throw JsErrors.TypeError(realm, "TypedArray is out of bounds or its buffer is detached");
            if (a[0].IsObject && a[0].Ref is JsTypedArray src)
            {
                if (src.IsOutOfBounds) throw JsErrors.TypeError(realm, "Source TypedArray is out of bounds or its buffer is detached");
                if (src.IsBigIntKind != t.IsBigIntKind) throw JsErrors.TypeError(realm, "Content types of source and target typed arrays differ");
                if (double.IsInfinity(off) || src.Length + off > t.Length) throw JsErrors.RangeError(realm, "offset is out of bounds");
                int to = (int)off;
                if (src.Kind == t.Kind)
                {
                    int es = JsTypedArray.ElementSize(t.Kind);
                    Array.Copy(src.Buffer.Data, src.ByteOffset, t.Buffer.Data, t.ByteOffset + to * es, src.Length * es);
                    return JsValue.Undefined;
                }
                var tmp = new JsValue[src.Length];
                for (int i = 0; i < tmp.Length; i++) tmp[i] = src.GetElement(i);
                for (int i = 0; i < tmp.Length; i++) t.SetElement(to + i, tmp[i].IsBigInt ? tmp[i] : new JsValue(tmp[i].Num));
                return JsValue.Undefined;
            }
            int targetLength = t.Length;   // read before the source's length getter can resize the buffer
            var obj = Operations.ToObject(realm, a[0]);
            long len = Operations.LengthOfArrayLike(realm, obj);
            if (double.IsInfinity(off) || len + off > targetLength) throw JsErrors.RangeError(realm, "offset is out of bounds");
            for (long i = 0; i < len; i++)
            {
                var v = obj.Get(PropertyKey.FromIndex((uint)i), JsValue.FromObject(obj));
                var conv = t.ToElementValue(v);
                long idx = (long)off + i;
                if (idx < t.Length) t.SetElement((int)idx, conv);
                realm.Vm.Tick();
            }
            return JsValue.Undefined;
        });

        r.Method(taProto, "subarray", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var t = a.This.Ref as JsTypedArray ?? throw JsErrors.TypeError(realm, "this is not a typed array");
            long srcLength = t.Length;
            long begin = Operations.RelativeIndex(realm, a[0], srcLength, 0);
            long end = a[1].IsUndefined ? srcLength : Operations.RelativeIndex(realm, a[1], srcLength, srcLength);
            int es = JsTypedArray.ElementSize(t.Kind);
            var beginByte = new JsValue((double)(t.ByteOffset + begin * es));
            JsValue[] args = t.IsLengthTracking && a[1].IsUndefined
                ? new[] { JsValue.FromObject(t.Buffer), beginByte }
                : new[] { JsValue.FromObject(t.Buffer), beginByte, new JsValue((double)Math.Max(end - begin, 0)) };
            return JsValue.FromObject(SpeciesCreate(realm, t, args));
        });

        r.Method(taProto, "slice", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var t = Validate(realm, a.This);
            int len = t.Length;   // srcArrayLength: fixed before the arguments are coerced
            long k = Operations.RelativeIndex(realm, a[0], len, 0);
            long final = Operations.RelativeIndex(realm, a[1], len, len);
            long count = Math.Max(final - k, 0);
            var cnt = new JsValue((double)count);
            var res = SpeciesCreate(realm, t, new ReadOnlySpan<JsValue>(in cnt), write: true);
            if (count > 0)
            {
                if (t.IsOutOfBounds) throw JsErrors.TypeError(realm, "TypedArray is out of bounds or its buffer is detached");
                final = Math.Min(final, t.Length);
                if (res.Kind == t.Kind)
                {
                    // byte-wise forward copy (observable when the species result shares the source buffer)
                    int es = JsTypedArray.ElementSize(t.Kind);
                    long srcByte = t.ByteOffset + k * es, dstByte = res.ByteOffset;
                    long limit = Math.Min(t.ByteOffset + final * es, (long)t.Buffer.Data.Length);
                    var src = t.Buffer.Data;
                    var dst = res.Buffer.Data;
                    while (srcByte < limit && dstByte < dst.Length) dst[dstByte++] = src[srcByte++];
                }
                else
                {
                    int n = 0;
                    for (; k < final; k++, n++)
                        res.Set(PropertyKey.FromIndex((uint)n), t.GetElement((int)k), JsValue.FromObject(res));
                }
            }
            return JsValue.FromObject(res);
        });

        r.Method(taProto, "map", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var t = Validate(realm, a.This);
            if (!a[0].IsFunction) throw JsErrors.TypeError(realm, $"{a[0]} is not a function");
            int count = t.Length;
            var len = new JsValue(count);
            var res = SpeciesCreate(realm, t, new ReadOnlySpan<JsValue>(in len), write: true);
            var args = new JsValue[3];
            args[2] = a.This;
            for (int i = 0; i < count; i++)
            {
                args[0] = t.GetElement(i);
                args[1] = new JsValue(i);
                var mapped = realm.Vm.Call(a[0], a[1], args);
                res.Set(PropertyKey.FromIndex((uint)i), mapped, JsValue.FromObject(res));
            }
            return JsValue.FromObject(res);
        });

        r.Method(taProto, "filter", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var t = Validate(realm, a.This);
            if (!a[0].IsFunction) throw JsErrors.TypeError(realm, $"{a[0]} is not a function");
            var kept = new List<JsValue>();
            var args = new JsValue[3];
            args[2] = a.This;
            int count0 = t.Length;
            for (int i = 0; i < count0; i++)
            {
                var v = t.GetElement(i);
                args[0] = v;
                args[1] = new JsValue(i);
                if (Operations.ToBoolean(realm.Vm.Call(a[0], a[1], args))) kept.Add(v);
            }
            var cnt = new JsValue(kept.Count);
            var res = SpeciesCreate(realm, t, new ReadOnlySpan<JsValue>(in cnt), write: true);
            for (int i = 0; i < kept.Count; i++) res.Set(PropertyKey.FromIndex((uint)i), kept[i], JsValue.FromObject(res));
            return JsValue.FromObject(res);
        });

        r.Method(taProto, "fill", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var t = ValidateWrite(realm, a.This);
            int len0 = t.Length;
            var v = t.ToElementValue(a[0]);
            long k = Operations.RelativeIndex(realm, a[1], len0, 0);
            long end = Operations.RelativeIndex(realm, a[2], len0, len0);
            t = Validate(realm, a.This); // conversions may have detached or shrunk the buffer
            end = Math.Min(end, t.Length);
            for (long i = k; i < end; i++) t.SetElement((int)i, v);
            return a.This;
        });

        r.Method(taProto, "sort", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var cmp = a[0];
            if (!cmp.IsUndefined && !cmp.IsFunction) throw JsErrors.TypeError(realm, "The comparison function must be either a function or undefined");
            var t = ValidateWrite(realm, a.This);
            var sorted = SortedValues(realm, t, cmp);
            for (int i = 0; i < sorted.Length && i < t.Length; i++) t.SetElement(i, sorted[i]);
            return a.This;
        });

        r.Method(taProto, "toSorted", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var cmp = a[0];
            if (!cmp.IsUndefined && !cmp.IsFunction) throw JsErrors.TypeError(realm, "The comparison function must be either a function or undefined");
            var t = Validate(realm, a.This);
            var res = CreateSameType(realm, t, t.Length);
            var sorted = SortedValues(realm, t, cmp);
            for (int i = 0; i < sorted.Length; i++) res.SetElement(i, sorted[i]);
            return JsValue.FromObject(res);
        });

        r.Method(taProto, "toReversed", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var t = Validate(realm, a.This);
            var res = CreateSameType(realm, t, t.Length);
            for (int i = 0; i < t.Length; i++) res.SetElement(i, t.GetElement(t.Length - 1 - i));
            return JsValue.FromObject(res);
        });

        r.Method(taProto, "with", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var t = Validate(realm, a.This);
            int len = t.Length;
            double rel = Operations.ToIntegerOrInfinity(realm, a[0]);
            double idx = rel >= 0 ? rel : len + rel;
            var v = t.ToElementValue(a[1]);
            // IsValidIntegerIndex against the current (possibly resized) length
            if (!(idx >= 0 && idx < t.Length)) throw JsErrors.RangeError(realm, "Invalid typed array index");
            var res = CreateSameType(realm, t, len);
            for (int i = 0; i < len; i++)
                res.SetElement(i, i == (int)idx ? v : res.ToElementValue(t.GetElement(i)));   // Get: undefined when out of bounds
            return JsValue.FromObject(res);
        });
    }

    /// <summary>Callback / search methods with %TypedArray% semantics (length fixed at entry, Get on each index).</summary>
    public static void InstallIteration(Realm r, JsObject taProto)
    {
        r.Method(taProto, "forEach", 1, static (in NativeArgs a) => Callback(a, 0));
        r.Method(taProto, "every", 1, static (in NativeArgs a) => Callback(a, 1));
        r.Method(taProto, "some", 1, static (in NativeArgs a) => Callback(a, 2));
        r.Method(taProto, "find", 1, static (in NativeArgs a) => Callback(a, 3));
        r.Method(taProto, "findIndex", 1, static (in NativeArgs a) => Callback(a, 4));
        r.Method(taProto, "findLast", 1, static (in NativeArgs a) => Callback(a, 5));
        r.Method(taProto, "findLastIndex", 1, static (in NativeArgs a) => Callback(a, 6));
        r.Method(taProto, "reduce", 1, static (in NativeArgs a) => ReduceTa(a, right: false));
        r.Method(taProto, "reduceRight", 1, static (in NativeArgs a) => ReduceTa(a, right: true));
        r.Method(taProto, "join", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var t = Validate(realm, a.This);
            int len = t.Length;
            string sep = a[0].IsUndefined ? "," : Operations.ToString(realm, a[0]);
            var sb = new System.Text.StringBuilder();
            int charged = 0;
            for (int k = 0; k < len; k++)
            {
                if (k > 0) sb.Append(sep);
                var e = t.GetElement(k);
                if (!e.IsUndefined) sb.Append(Operations.ToString(realm, e));
                if (sb.Length > realm.MaxStringLength) throw JsErrors.RangeError(realm, "Invalid string length");
                realm.Vm.Tick();
                if (sb.Length - charged >= 4096) { realm.ChargeOutput(sb.Length - charged); charged = sb.Length; }
            }
            realm.ChargeOutput(sb.Length - charged);
            return new JsValue(sb.ToString());
        });
        r.Method(taProto, "indexOf", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var t = Validate(realm, a.This);
            int len = t.Length;
            if (len == 0) return new JsValue(-1);
            double n = Operations.ToIntegerOrInfinity(realm, a[1]);
            if (double.IsPositiveInfinity(n)) return new JsValue(-1);
            long k = n >= 0 ? (long)n : Math.Max(len + (long)Math.Max(n, -len - 1.0), 0);
            for (; k < len; k++)
            {
                if (k >= t.Length) break; // HasProperty: shrunk / detached
                if (Operations.StrictEquals(t.GetElement((int)k), a[0])) return new JsValue((double)k);
            }
            return new JsValue(-1);
        });
        r.Method(taProto, "lastIndexOf", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var t = Validate(realm, a.This);
            int len = t.Length;
            if (len == 0) return new JsValue(-1);
            double n = a.Length > 1 ? Operations.ToIntegerOrInfinity(realm, a[1]) : len - 1;
            if (double.IsNegativeInfinity(n)) return new JsValue(-1);
            long k = n >= 0 ? (long)Math.Min(n, len - 1) : (long)(len + n);
            for (; k >= 0; k--)
            {
                if (k >= t.Length) continue;
                if (Operations.StrictEquals(t.GetElement((int)k), a[0])) return new JsValue((double)k);
            }
            return new JsValue(-1);
        });
        r.Method(taProto, "includes", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var t = Validate(realm, a.This);
            int len = t.Length;
            if (len == 0) return JsValue.False;
            double n = Operations.ToIntegerOrInfinity(realm, a[1]);
            if (double.IsPositiveInfinity(n)) return JsValue.False;
            long k = n >= 0 ? (long)n : Math.Max(len + (long)Math.Max(n, -len - 1.0), 0);
            for (; k < len; k++)
                if (Operations.SameValueZero(t.GetElement((int)k), a[0])) return JsValue.True;
            return JsValue.False;
        });
        r.Method(taProto, "at", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var t = Validate(realm, a.This);
            int len = t.Length;
            double rel = Operations.ToIntegerOrInfinity(realm, a[0]);
            double k = rel >= 0 ? rel : len + rel;
            if (k < 0 || k >= len) return JsValue.Undefined;
            return t.GetElement((int)k);
        });
        r.Method(taProto, "reverse", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var t = ValidateWrite(realm, a.This);
            int len = t.Length;
            for (int lo = 0, hi = len - 1; lo < hi; lo++, hi--)
            {
                var lv = t.GetElement(lo);
                var hv = t.GetElement(hi);
                t.SetElement(lo, hv);
                t.SetElement(hi, lv);
            }
            return a.This;
        });
        r.Method(taProto, "copyWithin", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var t = ValidateWrite(realm, a.This);
            long len = t.Length;
            long to = Operations.RelativeIndex(realm, a[0], len, 0);
            long from = Operations.RelativeIndex(realm, a[1], len, 0);
            long final = Operations.RelativeIndex(realm, a[2], len, len);
            long count = Math.Min(final - from, len - to);
            if (count > 0)
            {
                if (t.IsOutOfBounds) throw JsErrors.TypeError(realm, "TypedArray is out of bounds or its buffer is detached");
                len = t.Length;
                int es = JsTypedArray.ElementSize(t.Kind);
                long limit = len * es + t.ByteOffset;
                long toByte = to * es + t.ByteOffset, fromByte = from * es + t.ByteOffset;
                long bytes = Math.Min(count * es, Math.Min(limit - fromByte, limit - toByte));
                if (bytes > 0) Array.Copy(t.Buffer.Data, fromByte, t.Buffer.Data, toByte, bytes);
            }
            return a.This;
        });
    }

    /// <summary>kind: 0 forEach, 1 every, 2 some, 3 find, 4 findIndex, 5 findLast, 6 findLastIndex.</summary>
    private static JsValue Callback(in NativeArgs a, int kind)
    {
        var realm = a.Realm;
        var t = Validate(realm, a.This);
        int len = t.Length;
        var cb = a[0];
        if (!cb.IsFunction) throw JsErrors.TypeError(realm, $"{JsErrors.Describe(cb)} is not a function");
        var args = new JsValue[3];
        args[2] = a.This;
        bool reverse = kind is 5 or 6;
        for (int n = 0; n < len; n++)
        {
            int k = reverse ? len - 1 - n : n;
            var v = t.GetElement(k);
            args[0] = v;
            args[1] = new JsValue(k);
            var r = realm.Vm.Call(cb, a[1], args);
            switch (kind)
            {
                case 1: if (!Operations.ToBoolean(r)) return JsValue.False; break;
                case 2: if (Operations.ToBoolean(r)) return JsValue.True; break;
                case 3: case 5: if (Operations.ToBoolean(r)) return v; break;
                case 4: case 6: if (Operations.ToBoolean(r)) return new JsValue(k); break;
            }
            realm.Vm.Tick();
        }
        return kind switch { 1 => JsValue.True, 2 => JsValue.False, 4 or 6 => new JsValue(-1), _ => JsValue.Undefined };
    }

    private static JsValue ReduceTa(in NativeArgs a, bool right)
    {
        var realm = a.Realm;
        var t = Validate(realm, a.This);
        int len = t.Length;
        var cb = a[0];
        if (!cb.IsFunction) throw JsErrors.TypeError(realm, $"{JsErrors.Describe(cb)} is not a function");
        int k = 0;
        JsValue acc;
        if (a.Length >= 2) acc = a[1];
        else
        {
            if (len == 0) throw JsErrors.TypeError(realm, "Reduce of empty array with no initial value");
            acc = t.GetElement(right ? len - 1 : 0);
            k = 1;
        }
        var args = new JsValue[4];
        args[3] = a.This;
        for (; k < len; k++)
        {
            int i = right ? len - 1 - k : k;
            args[0] = acc;
            args[1] = t.GetElement(i);
            args[2] = new JsValue(i);
            acc = realm.Vm.Call(cb, JsValue.Undefined, args);
            realm.Vm.Tick();
        }
        return acc;
    }

    private static void Define(Realm r, JsObject target, string name, int length, NativeFn fn)
    {
        var f = new JsBuiltin(r, name, length, fn);
        target.DefinePropertyOrThrow(name, PropertyDescriptor.Data(JsValue.FromObject(f), PropFlags.Writable | PropFlags.Configurable));
    }

    private static JsValue[] SortedValues(Realm realm, JsTypedArray t, JsValue cmp)
    {
        var vals = new JsValue[t.Length];
        for (int i = 0; i < vals.Length; i++) vals[i] = t.GetElement(i);
        Comparison<JsValue> comparison;
        if (cmp.IsFunction)
        {
            var args = new JsValue[2];
            comparison = (x, y) =>
            {
                args[0] = x;
                args[1] = y;
                double rr = Operations.ToNumber(realm, realm.Vm.Call(cmp, JsValue.Undefined, args));
                return rr < 0 ? -1 : rr > 0 ? 1 : 0;
            };
        }
        else if (t.IsBigIntKind) comparison = static (x, y) => x.BigIntValue.CompareTo(y.BigIntValue);
        else comparison = static (x, y) =>
        {
            double a = x.Num, b = y.Num;
            if (double.IsNaN(a)) return double.IsNaN(b) ? 0 : 1;
            if (double.IsNaN(b)) return -1;
            if (a < b) return -1;
            if (a > b) return 1;
            if (a == 0 && b == 0) return double.IsNegative(a) == double.IsNegative(b) ? 0 : double.IsNegative(a) ? -1 : 1;
            return 0;
        };
        // stable merge sort (List.Sort is not stable)
        MergeSort(vals, new JsValue[vals.Length], 0, vals.Length, comparison);
        return vals;
    }

    private static void MergeSort(JsValue[] a, JsValue[] tmp, int lo, int hi, Comparison<JsValue> cmp)
    {
        if (hi - lo < 2) return;
        int mid = (lo + hi) >> 1;
        MergeSort(a, tmp, lo, mid, cmp);
        MergeSort(a, tmp, mid, hi, cmp);
        int i = lo, j = mid, k = lo;
        while (i < mid && j < hi) tmp[k++] = cmp(a[j], a[i]) < 0 ? a[j++] : a[i++];
        while (i < mid) tmp[k++] = a[i++];
        while (j < hi) tmp[k++] = a[j++];
        Array.Copy(tmp, lo, a, lo, hi - lo);
    }
}
