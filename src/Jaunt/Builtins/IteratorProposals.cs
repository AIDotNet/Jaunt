using System;
using System.Collections.Generic;

namespace Jaunt.Runtime;

/// <summary>
/// Iterator additions standardized after ES2025: Iterator.zip / zipKeyed (joint iteration),
/// Iterator.prototype.chunks / windows (iterator chunking) and Iterator.prototype.includes.
/// </summary>
internal static class IteratorProposals
{
    public static void Setup(Realm r, JsObject iterProto)
    {
        var ctor = r.IteratorConstructor!;
        r.Method(ctor, "zip", 1, static (in NativeArgs a) => Zip(a, keyed: false));
        r.Method(ctor, "zipKeyed", 1, static (in NativeArgs a) => Zip(a, keyed: true));
        r.Method(iterProto, "chunks", 1, static (in NativeArgs a) => Chunks(a, windows: false));
        r.Method(iterProto, "windows", 1, static (in NativeArgs a) => Chunks(a, windows: true));
        r.Method(iterProto, "includes", 1, static (in NativeArgs a) => Includes(a));
    }

    // ------------------------------------------------------------------ IteratorCloseAll

    /// <summary>IteratorCloseAll(iters, throw completion): closes in reverse order, keeping the first error.</summary>
    private static JsThrowException CloseAllThrow(Realm realm, List<DirectIterator> iters, JsThrowException error)
    {
        for (int i = iters.Count - 1; i >= 0; i--) iters[i].CloseQuiet(realm);
        return error;
    }

    /// <summary>IteratorCloseAll(iters, normal/return completion): the first error from a return() wins.</summary>
    private static void CloseAll(Realm realm, List<DirectIterator> iters)
    {
        JsThrowException? first = null;
        for (int i = iters.Count - 1; i >= 0; i--)
        {
            if (first == null)
            {
                try { iters[i].Close(realm); }
                catch (JsThrowException ex) { first = ex; }
            }
            else iters[i].CloseQuiet(realm);
        }
        if (first != null) throw first;
    }

    /// <summary>GetIterator(obj, sync) as an iterator record.</summary>
    private static DirectIterator GetIterator(Realm realm, in JsValue obj)
    {
        var method = Operations.GetV(realm, obj, JsSymbol.Iterator);
        if (!method.IsFunction) throw JsErrors.TypeError(realm, $"{JsErrors.Describe(obj)} is not iterable");
        var it = realm.Vm.Call(method, obj, ReadOnlySpan<JsValue>.Empty);
        if (it.IsNotObject) throw JsErrors.TypeError(realm, "Result of the Symbol.iterator method is not an object");
        return new DirectIterator(it.AsObjectUnchecked, it.AsObjectUnchecked.Get("next", it));
    }

    private static JsObject GetOptionsObject(Realm realm, in JsValue options)
    {
        if (options.IsUndefined) return new JsObject(realm, (JsObject?)null);
        if (options.IsObject) return options.AsObjectUnchecked;
        throw JsErrors.TypeError(realm, "options must be an object or undefined");
    }

    // ------------------------------------------------------------------ zip / zipKeyed

    private enum ZipMode { Shortest, Longest, Strict }

    private static JsValue Zip(in NativeArgs a, bool keyed)
    {
        var realm = a.Realm;
        var iterables = a[0];
        if (iterables.IsNotObject) throw JsErrors.TypeError(realm, "Iterator.zip requires an object of iterables");
        var options = GetOptionsObject(realm, a[1]);
        var modeV = options.Get("mode");
        ZipMode mode;
        if (modeV.IsUndefined) mode = ZipMode.Shortest;
        else if (modeV.IsString && modeV.AsStringUnchecked == "shortest") mode = ZipMode.Shortest;
        else if (modeV.IsString && modeV.AsStringUnchecked == "longest") mode = ZipMode.Longest;
        else if (modeV.IsString && modeV.AsStringUnchecked == "strict") mode = ZipMode.Strict;
        else throw JsErrors.TypeError(realm, "mode must be \"shortest\", \"longest\" or \"strict\"");
        JsValue paddingOption = default;
        if (mode == ZipMode.Longest)
        {
            paddingOption = options.Get("padding");
            if (!paddingOption.IsUndefined && paddingOption.IsNotObject) throw JsErrors.TypeError(realm, "padding must be an object");
        }
        var iters = new List<DirectIterator>();
        var padding = new List<JsValue>();
        List<PropertyKey>? keys = null;
        if (!keyed)
        {
            var inputRec = GetIterator(realm, iterables);
            while (true)
            {
                JsValue next;
                try { if (!inputRec.Step(realm, out next)) break; }
                catch (JsThrowException ex) { throw CloseAllThrow(realm, iters, ex); }
                try { iters.Add(IteratorHelpers.GetIteratorFlattenable(realm, next, iterateStrings: false)); }
                catch (JsThrowException ex)
                {
                    var all = new List<DirectIterator> { inputRec };
                    all.AddRange(iters);
                    throw CloseAllThrow(realm, all, ex);
                }
                realm.Vm.Tick();
            }
        }
        else
        {
            var src = iterables.AsObjectUnchecked;
            var allKeys = new List<PropertyKey>();
            src.OwnPropertyKeys(allKeys);
            keys = new List<PropertyKey>();
            foreach (var key in allKeys)
            {
                try
                {
                    if (!src.GetOwnProperty(key, out var desc) || !desc.Enumerable) continue;
                    var value = src.Get(key, iterables);
                    if (value.IsUndefined) continue;
                    keys.Add(key);
                    iters.Add(IteratorHelpers.GetIteratorFlattenable(realm, value, iterateStrings: false));
                }
                catch (JsThrowException ex) { throw CloseAllThrow(realm, iters, ex); }
            }
        }
        int count = iters.Count;
        if (mode == ZipMode.Longest)
        {
            if (paddingOption.IsUndefined) for (int i = 0; i < count; i++) padding.Add(JsValue.Undefined);
            else if (!keyed)
            {
                DirectIterator padIter;
                try
                {
                    padIter = GetIterator(realm, paddingOption);
                }
                catch (JsThrowException ex) { throw CloseAllThrow(realm, iters, ex); }
                bool usingIterator = true;
                for (int i = 0; i < count; i++)
                {
                    if (usingIterator)
                    {
                        try
                        {
                            if (padIter.Step(realm, out var pv)) padding.Add(pv);
                            else usingIterator = false;
                        }
                        catch (JsThrowException ex) { throw CloseAllThrow(realm, iters, ex); }
                    }
                    if (!usingIterator) padding.Add(JsValue.Undefined);
                }
                if (usingIterator)
                {
                    try { padIter.Close(realm); }
                    catch (JsThrowException ex) { throw CloseAllThrow(realm, iters, ex); }
                }
            }
            else
            {
                var po = paddingOption.AsObjectUnchecked;
                foreach (var key in keys!)
                {
                    try { padding.Add(po.Get(key, paddingOption)); }
                    catch (JsThrowException ex) { throw CloseAllThrow(realm, iters, ex); }
                }
            }
        }

        var slots = new DirectIterator?[count];
        for (int i = 0; i < count; i++) slots[i] = iters[i];
        var open = new List<DirectIterator>(iters);
        var dummy = new DirectIterator(realm.NewObject(), JsValue.Undefined);
        var helper = new JsIteratorHelper(realm, dummy, HelperKind.Custom, JsValue.Undefined, 0);
        helper.StepFn = (Realm rr, out JsValue result) =>
        {
            result = default;
            if (count == 0) return false;
            var values = new JsValue[count];
            for (int i = 0; i < count; i++)
            {
                var it = slots[i];
                if (it == null) { values[i] = padding[i]; continue; }
                bool has;
                JsValue v;
                try { has = it.Step(rr, out v); }
                catch (JsThrowException ex) { open.Remove(it); throw CloseAllThrow(rr, open, ex); }
                if (has) { values[i] = v; continue; }
                open.Remove(it);
                switch (mode)
                {
                    case ZipMode.Shortest:
                        CloseAll(rr, open);
                        open.Clear();
                        return false;
                    case ZipMode.Strict:
                        if (i != 0) throw CloseAllThrow(rr, open, JsErrors.TypeError(rr, "Iterator.zip: iterables have different lengths (strict mode)"));
                        for (int k = 1; k < count; k++)
                        {
                            var other = slots[k]!;
                            bool more;
                            try { more = other.Step(rr, out _); }
                            catch (JsThrowException ex) { open.Remove(other); throw CloseAllThrow(rr, open, ex); }
                            if (!more) open.Remove(other);
                            else throw CloseAllThrow(rr, open, JsErrors.TypeError(rr, "Iterator.zip: iterables have different lengths (strict mode)"));
                        }
                        return false;
                    default:
                        if (open.Count == 0) return false;
                        slots[i] = null;
                        values[i] = padding[i];
                        break;
                }
            }
            if (keys == null) result = JsValue.FromObject(Operations.CreateArrayFromList(rr, values));
            else
            {
                var o = new JsObject(rr, (JsObject?)null);
                for (int i = 0; i < count; i++) o.CreateDataProperty(keys[i], values[i]);
                result = JsValue.FromObject(o);
            }
            return true;
        };
        helper.ReturnFn = rr =>
        {
            var toClose = new List<DirectIterator>(open);
            open.Clear();
            CloseAll(rr, toClose);
        };
        return JsValue.FromObject(helper);
    }

    // ------------------------------------------------------------------ chunks / windows

    private static JsValue Chunks(in NativeArgs a, bool windows)
    {
        var realm = a.Realm;
        if (a.This.IsNotObject) throw JsErrors.TypeError(realm, "Iterator.prototype method called on a non-object");
        var o = a.This.AsObjectUnchecked;
        var size = a[0];
        if (!size.IsNumber || !double.IsFinite(size.Num) || Math.Truncate(size.Num) != size.Num)
            throw CloseWith(realm, o, JsErrors.TypeError(realm, "size must be an integer"));
        if (size.Num < 1 || size.Num > uint.MaxValue)
            throw CloseWith(realm, o, JsErrors.RangeError(realm, "size must be between 1 and 2^32 - 1"));
        bool allowPartial = false;
        if (windows)
        {
            var u = a[1];
            if (u.IsUndefined || (u.IsString && u.AsStringUnchecked == "only-full")) allowPartial = false;
            else if (u.IsString && u.AsStringUnchecked == "allow-partial") allowPartial = true;
            else throw CloseWith(realm, o, JsErrors.TypeError(realm, "undersized must be \"only-full\" or \"allow-partial\""));
        }
        var iterated = IteratorHelpers.GetIteratorDirect(realm, o);
        long n = (long)size.Num;
        var buffer = new List<JsValue>();
        bool finished = false;
        var helper = new JsIteratorHelper(realm, iterated, HelperKind.Custom, JsValue.Undefined, 0);
        helper.StepFn = (Realm rr, out JsValue result) =>
        {
            result = default;
            if (finished) return false;
            while (true)
            {
                if (!iterated.Step(rr, out var v))
                {
                    finished = true;
                    bool emit = windows ? allowPartial && buffer.Count > 0 && buffer.Count < n : buffer.Count > 0;
                    if (!emit) return false;
                    result = JsValue.FromObject(Operations.CreateArrayFromList(rr, buffer.ToArray()));
                    buffer.Clear();
                    return true;
                }
                if (windows && buffer.Count == n) buffer.RemoveAt(0);
                buffer.Add(v);
                rr.Vm.Tick();
                if (buffer.Count == n)
                {
                    result = JsValue.FromObject(Operations.CreateArrayFromList(rr, buffer.ToArray()));
                    if (!windows) buffer.Clear();
                    return true;
                }
            }
        };
        // a return() after the final partial chunk (source already exhausted) has nothing to close
        helper.ReturnFn = rr => { if (!finished) iterated.Close(rr); };
        return JsValue.FromObject(helper);
    }

    private static JsThrowException CloseWith(Realm realm, JsObject iterator, JsThrowException error)
    {
        DirectIterator.CloseQuiet(realm, iterator);
        return error;
    }

    // ------------------------------------------------------------------ includes

    private static JsValue Includes(in NativeArgs a)
    {
        var realm = a.Realm;
        if (a.This.IsNotObject) throw JsErrors.TypeError(realm, "Iterator.prototype.includes called on a non-object");
        var o = a.This.AsObjectUnchecked;
        double toSkip = 0;
        var skippedArg = a[1];
        if (!skippedArg.IsUndefined)
        {
            if (!skippedArg.IsNumber || double.IsNaN(skippedArg.Num) || (double.IsFinite(skippedArg.Num) && Math.Truncate(skippedArg.Num) != skippedArg.Num))
                throw CloseWith(realm, o, JsErrors.TypeError(realm, "skippedElements must be an integer"));
            toSkip = skippedArg.Num;
        }
        if (toSkip < 0) throw CloseWith(realm, o, JsErrors.RangeError(realm, "skippedElements must not be negative"));
        if (double.IsFinite(toSkip) && toSkip > 9007199254740991.0) throw CloseWith(realm, o, JsErrors.RangeError(realm, "skippedElements is too large"));
        var iterated = IteratorHelpers.GetIteratorDirect(realm, o);
        var search = a[0];
        double skipped = 0;
        while (true)
        {
            if (!iterated.Step(realm, out var v)) return JsValue.False;
            realm.Vm.Tick();
            if (skipped < toSkip) { skipped++; continue; }
            if (Operations.SameValueZero(v, search))
            {
                iterated.Close(realm);
                return JsValue.True;
            }
        }
    }
}
