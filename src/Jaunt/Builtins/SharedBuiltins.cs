using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Jaunt.Runtime;

/// <summary>
/// SharedArrayBuffer and Atomics (25.2, 25.4). Each engine is one agent. A Shared Data Block can be handed to other
/// engines (agents) through <see cref="JsSharedMemory"/>; Atomics operations on it are serialized by the block lock and
/// Atomics.notify wakes waiters of every agent. [[CanBlock]] is false unless <see cref="JsEngineOptions.CanBlock"/> is
/// set: then Atomics.wait throws a TypeError, because an embedded engine must not park the host thread by default.
/// </summary>
internal static class SharedBuiltins
{
    public static void Setup(Realm r)
    {
        var sabProto = new JsObject(r, r.ObjectPrototype);
        r.SharedArrayBufferPrototype = sabProto;
        var sab = r.Constructor("SharedArrayBuffer", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (!a.IsConstruct) throw JsErrors.TypeError(realm, "Constructor SharedArrayBuffer requires 'new'");
            long len = TypedArrayBuiltins.ToIndex(realm, a[0]);
            long max = -1;
            if (a[1].IsObject)
            {
                var mv = a[1].AsObjectUnchecked.Get("maxByteLength");
                if (!mv.IsUndefined) max = TypedArrayBuiltins.ToIndex(realm, mv);
            }
            if (max >= 0 && len > max) throw JsErrors.RangeError(realm, "byteLength exceeds maxByteLength");   // before OrdinaryCreateFromConstructor
            var p = Operations.GetPrototypeFromConstructor(realm, a.NewTarget.AsObjectUnchecked, realm.SharedArrayBufferPrototype!);
            if (len > realm.MaxArrayLength || max > int.MaxValue) throw JsErrors.RangeError(realm, "Array buffer allocation failed");
            return JsValue.FromObject(new JsArrayBuffer(realm, p, (int)len, (int)max, shared: true));
        }, sabProto);
        r.DefineGlobal("SharedArrayBuffer", JsValue.FromObject(sab));
        r.Getter(sab, JsSymbol.Species, "[Symbol.species]", static (in NativeArgs a) => a.This);
        r.Getter(sabProto, "byteLength", "byteLength", static (in NativeArgs a) => new JsValue(ThisShared(a).Data.Length));
        r.Getter(sabProto, "growable", "growable", static (in NativeArgs a) => JsValue.FromBool(ThisShared(a).Resizable));
        r.Getter(sabProto, "maxByteLength", "maxByteLength", static (in NativeArgs a) =>
        {
            var b = ThisShared(a);
            return new JsValue(b.Resizable ? b.MaxByteLength : b.Data.Length);
        });
        r.Method(sabProto, "grow", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var b = ThisShared(a);
            if (!b.Resizable) throw JsErrors.TypeError(realm, "SharedArrayBuffer.prototype.grow called on a non-growable buffer");
            long n = TypedArrayBuiltins.ToIndex(realm, a[0]);
            if (n > b.MaxByteLength || n < b.Data.Length) throw JsErrors.RangeError(realm, "Invalid length for growable SharedArrayBuffer");
            if (n > b.Data.Length) b.Resize((int)n);
            return JsValue.Undefined;
        });
        r.Method(sabProto, "slice", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var b = ThisShared(a);
            long len = b.Data.Length;
            long first = Operations.RelativeIndex(realm, a[0], len, 0);
            long final = Operations.RelativeIndex(realm, a[1], len, len);
            long newLen = Math.Max(final - first, 0);
            var c = Operations.SpeciesConstructorDefault(realm, b, realm.SharedArrayBufferConstructor!);
            var lenV = new JsValue((double)newLen);
            var created = realm.Vm.Construct(JsValue.FromObject(c), new ReadOnlySpan<JsValue>(in lenV));
            if (created.Ref is not JsArrayBuffer { Shared: true } nb) throw JsErrors.TypeError(realm, "Species constructor did not return a SharedArrayBuffer");
            if (ReferenceEquals(nb, b)) throw JsErrors.TypeError(realm, "Species constructor returned the same SharedArrayBuffer");
            if (nb.Data.Length < newLen) throw JsErrors.TypeError(realm, "Species constructor returned a too small SharedArrayBuffer");
            if (newLen > 0) Array.Copy(b.Data, first, nb.Data, 0, newLen);
            return JsValue.FromObject(nb);
        });
        sabProto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("SharedArrayBuffer"), PropFlags.Configurable);
        r.SharedArrayBufferConstructor = sab;

        var atomics = new JsObject(r, r.ObjectPrototype);
        r.DefineGlobal("Atomics", JsValue.FromObject(atomics));
        atomics.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("Atomics"), PropFlags.Configurable);
        r.Method(atomics, "add", 3, static (in NativeArgs a) => ReadModifyWrite(a, 0));
        r.Method(atomics, "and", 3, static (in NativeArgs a) => ReadModifyWrite(a, 1));
        r.Method(atomics, "exchange", 3, static (in NativeArgs a) => ReadModifyWrite(a, 2));
        r.Method(atomics, "or", 3, static (in NativeArgs a) => ReadModifyWrite(a, 3));
        r.Method(atomics, "sub", 3, static (in NativeArgs a) => ReadModifyWrite(a, 4));
        r.Method(atomics, "xor", 3, static (in NativeArgs a) => ReadModifyWrite(a, 5));
        r.Method(atomics, "load", 2, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var ta = ValidateIntegerTypedArray(realm, a[0], waitable: false);
            int i = ValidateAtomicAccess(realm, ta, a[1]);
            Revalidate(realm, ta, i);
            if (ta.Buffer.Block is { } blk) lock (blk.Lock) return ta.GetElement(i);
            return ta.GetElement(i);
        });
        r.Method(atomics, "store", 3, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var ta = ValidateIntegerTypedArray(realm, a[0], waitable: false, write: true);
            int i = ValidateAtomicAccess(realm, ta, a[1]);
            var v = ConvertValue(realm, ta, a[2]);
            Revalidate(realm, ta, i);
            if (ta.Buffer.Block is { } blk) lock (blk.Lock) ta.SetElement(i, v);
            else ta.SetElement(i, v);
            return v;
        });
        r.Method(atomics, "compareExchange", 4, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var ta = ValidateIntegerTypedArray(realm, a[0], waitable: false, write: true);
            int i = ValidateAtomicAccess(realm, ta, a[1]);
            var expected = ConvertValue(realm, ta, a[2]);
            var replacement = ConvertValue(realm, ta, a[3]);
            Revalidate(realm, ta, i);
            if (ta.Buffer.Block is { } blk) lock (blk.Lock) return CompareExchange(ta, i, expected, replacement);
            return CompareExchange(ta, i, expected, replacement);
        });
        r.Method(atomics, "isLockFree", 1, static (in NativeArgs a) =>
        {
            double n = Operations.ToIntegerOrInfinity(a.Realm, a[0]);
            return JsValue.FromBool(n is 1 or 2 or 4 or 8);
        });
        r.Method(atomics, "wait", 4, static (in NativeArgs a) => Wait(a, async: false));
        r.Method(atomics, "waitAsync", 4, static (in NativeArgs a) => Wait(a, async: true));
        r.Method(atomics, "notify", 3, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var ta = ValidateIntegerTypedArray(realm, a[0], waitable: true);
            int idx = ValidateAtomicAccess(realm, ta, a[1]);
            double count = double.PositiveInfinity;
            if (!a[2].IsUndefined) count = Math.Max(Operations.ToIntegerOrInfinity(realm, a[2]), 0);
            if (ta.Buffer.Block is not { } blk) return new JsValue(0);
            return new JsValue(AtomicsWaiting.Notify(realm.Vm, blk, ta.ByteOffset + idx * JsTypedArray.ElementSize(ta.Kind), count));
        });
        r.Method(atomics, "pause", 0, static (in NativeArgs a) =>
        {
            var n = a[0];
            if (!n.IsUndefined && !(n.IsNumber && double.IsFinite(n.Num) && Math.Truncate(n.Num) == n.Num))
                throw JsErrors.TypeError(a.Realm, "Atomics.pause argument must be an integral number");
            Thread.SpinWait(1);
            return JsValue.Undefined;
        });
    }

    private static JsArrayBuffer ThisShared(in NativeArgs a) =>
        a.This.Ref is JsArrayBuffer { Shared: true } b ? b : throw JsErrors.TypeError(a.Realm, "Receiver is not a SharedArrayBuffer");

    /// <summary>ValidateIntegerTypedArray (25.4.3.1).</summary>
    private static JsTypedArray ValidateIntegerTypedArray(Realm realm, in JsValue v, bool waitable, bool write = false)
    {
        if (v.Ref is not JsTypedArray ta) throw JsErrors.TypeError(realm, "Atomics operation requires an integer typed array");
        if (write && ta.Buffer.Immutable) throw JsErrors.TypeError(realm, "Atomics cannot modify an immutable ArrayBuffer");
        if (ta.IsOutOfBounds) throw JsErrors.TypeError(realm, "TypedArray is out of bounds or its buffer is detached");
        bool ok = waitable
            ? ta.Kind is TypedKind.Int32 or TypedKind.BigInt64
            : ta.Kind is TypedKind.Int8 or TypedKind.Uint8 or TypedKind.Int16 or TypedKind.Uint16 or TypedKind.Int32 or TypedKind.Uint32 or TypedKind.BigInt64 or TypedKind.BigUint64;
        if (!ok) throw JsErrors.TypeError(realm, "Atomics operation is not supported on this typed array type");
        return ta;
    }

    private static int ValidateAtomicAccess(Realm realm, JsTypedArray ta, in JsValue index)
    {
        long len = ta.Length;
        long i = TypedArrayBuiltins.ToIndex(realm, index);
        if (i >= len) throw JsErrors.RangeError(realm, "Atomics access index out of range");
        return (int)i;
    }

    private static void Revalidate(Realm realm, JsTypedArray ta, int i)
    {
        if (ta.IsOutOfBounds) throw JsErrors.TypeError(realm, "TypedArray is out of bounds or its buffer is detached");
        if (i >= ta.Length) throw JsErrors.RangeError(realm, "Atomics access index out of range");
    }

    private static JsValue ConvertValue(Realm realm, JsTypedArray ta, in JsValue v)
    {
        if (ta.IsBigIntKind) return JsValue.FromBigInteger(BigIntOps.ToBigInt(realm, v));
        double d = Operations.ToIntegerOrInfinity(realm, v);
        return new JsValue(d == 0 ? 0.0 : d);
    }

    private static JsValue CompareExchange(JsTypedArray ta, int i, in JsValue expected, in JsValue replacement)
    {
        var old = ta.GetElement(i);
        // compare the raw element bytes
        if (RawEquals(ta, old, expected)) ta.SetElement(i, replacement);
        return old;
    }

    private static long ToElementInt(JsTypedArray ta, in JsValue v)
    {
        if (v.IsBigInt) return (long)(ulong)(v.BigIntValue & ulong.MaxValue);
        double d = v.Num;
        if (double.IsInfinity(d) || double.IsNaN(d)) return 0;
        return (long)(ulong)(new BigInteger(d) & ulong.MaxValue);
    }

    private static bool RawEquals(JsTypedArray ta, in JsValue current, in JsValue expected)
    {
        int bits = JsTypedArray.ElementSize(ta.Kind) * 8;
        ulong mask = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
        return ((ulong)ToElementInt(ta, current) & mask) == ((ulong)ToElementInt(ta, expected) & mask);
    }

    /// <summary>op: 0 add, 1 and, 2 exchange, 3 or, 4 sub, 5 xor.</summary>
    private static JsValue ReadModifyWrite(in NativeArgs a, int op)
    {
        var realm = a.Realm;
        var ta = ValidateIntegerTypedArray(realm, a[0], waitable: false, write: true);
        int i = ValidateAtomicAccess(realm, ta, a[1]);
        var v = ConvertValue(realm, ta, a[2]);
        Revalidate(realm, ta, i);
        if (ta.Buffer.Block is { } blk) lock (blk.Lock) return Rmw(ta, i, v, op);
        return Rmw(ta, i, v, op);
    }

    private static JsValue Rmw(JsTypedArray ta, int i, in JsValue v, int op)
    {
        var old = ta.GetElement(i);
        if (ta.IsBigIntKind)
        {
            var x = old.BigIntValue;
            var y = v.BigIntValue;
            var res = op switch { 0 => x + y, 1 => x & y, 2 => y, 3 => x | y, 4 => x - y, _ => x ^ y };
            ta.SetElement(i, JsValue.FromBigInteger(BigIntOps.AsUintN(64, res)));
        }
        else
        {
            long x = ToElementInt(ta, old), y = ToElementInt(ta, v);
            long res = op switch { 0 => x + y, 1 => x & y, 2 => y, 3 => x | y, 4 => x - y, _ => x ^ y };
            ta.SetElement(i, new JsValue((double)(uint)res)); // SetElement wraps to the element type
        }
        return old;
    }

    private static JsValue Wait(in NativeArgs a, bool async)
    {
        var realm = a.Realm;
        var ta = ValidateIntegerTypedArray(realm, a[0], waitable: true);
        if (!ta.Buffer.Shared) throw JsErrors.TypeError(realm, "Atomics.wait requires a shared typed array");
        int i = ValidateAtomicAccess(realm, ta, a[1]);
        JsValue v = ta.IsBigIntKind
            ? JsValue.FromBigInteger(BigIntOps.AsIntN(64, BigIntOps.ToBigInt(realm, a[2])))
            : new JsValue(Operations.ToInt32(realm, a[2]));
        double q = Operations.ToNumber(realm, a[3]);
        double t = double.IsNaN(q) ? double.PositiveInfinity : Math.Max(q, 0);
        var vm = realm.Vm;
        if (!async && !vm.CanBlock) throw JsErrors.TypeError(realm, "Atomics.wait cannot be called in this context (the agent cannot block)");
        var block = ta.Buffer.Block!;
        int byteIndex = ta.ByteOffset + i * JsTypedArray.ElementSize(ta.Kind);
        if (!async)
        {
            bool woken;
            lock (block.Lock)
            {
                if (!Operations.StrictEquals(ta.GetElement(i), v)) return new JsValue("not-equal");
                woken = AtomicsWaiting.BlockingWait(vm, block, byteIndex, t);
            }
            return new JsValue(woken ? "ok" : "timed-out");
        }
        var result = realm.NewObject();
        AtomicsWaiter w;
        lock (block.Lock)
        {
            bool equal = Operations.StrictEquals(ta.GetElement(i), v);
            if (!equal || t == 0)
            {
                result.FastAdd("async", JsValue.False);
                result.FastAdd("value", new JsValue(equal ? "timed-out" : "not-equal"));
                return JsValue.FromObject(result);
            }
            w = AtomicsWaiting.AddAsync(vm, realm, block, byteIndex, t);
        }
        if (w.Deadline != long.MaxValue && realm.Engine!.IsAsyncExecution)
        {
            var timer = realm.Engine.StartDelay(realm, "Atomics.waitAsync", t, "timed-out");
            ((JsPromise)timer.Ref!).Then(new TimeoutContinuation());
        }
        result.FastAdd("async", JsValue.True);
        result.FastAdd("value", JsValue.FromObject(w.Promise!));
        return JsValue.FromObject(result);
    }

    private sealed class TimeoutContinuation : IPromiseContinuation
    {
        public void OnSettled(Realm realm, bool fulfilled, JsValue value) => realm.Vm.ProcessAsyncWaiters();
    }
}
