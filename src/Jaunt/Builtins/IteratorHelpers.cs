using System;
using System.Collections.Generic;

namespace Jaunt.Runtime;

/// <summary>Iterator record created by GetIteratorDirect (iterator object + cached next method).</summary>
internal sealed class DirectIterator
{
    public readonly JsObject Iterator;
    public readonly JsValue Next;
    public bool Done;

    public DirectIterator(JsObject iterator, JsValue next)
    {
        Iterator = iterator;
        Next = next;
    }

    /// <summary>IteratorStepValue: false when done. Abrupt completions mark the record done.</summary>
    public bool Step(Realm realm, out JsValue value)
    {
        Done = true;
        var r = realm.Vm.Call(Next, JsValue.FromObject(Iterator), ReadOnlySpan<JsValue>.Empty);
        if (r.IsNotObject) throw JsErrors.TypeError(realm, $"Iterator result {r} is not an object");
        var ro = r.AsObjectUnchecked;
        if (Operations.ToBoolean(ro.Get("done", r))) { value = default; return false; }
        value = ro.Get("value", r);
        Done = false;
        return true;
    }

    /// <summary>IteratorClose with a normal completion.</summary>
    public void Close(Realm realm)
    {
        var ret = Iterator.Get("return");
        if (ret.IsNullOrUndefined) return;
        if (!ret.IsFunction) throw JsErrors.TypeError(realm, "iterator.return is not a function");
        var r = realm.Vm.Call(ret, JsValue.FromObject(Iterator), ReadOnlySpan<JsValue>.Empty);
        if (r.IsNotObject) throw JsErrors.TypeError(realm, "Iterator result is not an object");
    }

    /// <summary>IteratorClose with a throw completion: errors from return() are suppressed.</summary>
    public void CloseQuiet(Realm realm) => CloseQuiet(realm, Iterator);

    public static void CloseQuiet(Realm realm, JsObject iterator)
    {
        try
        {
            var ret = iterator.Get("return");
            if (ret.IsNullOrUndefined) return;
            realm.Vm.Call(ret, JsValue.FromObject(iterator), ReadOnlySpan<JsValue>.Empty);
        }
        catch (JsThrowException) { }
    }
}

internal enum HelperKind : byte { Map, Filter, Take, Drop, FlatMap, Concat, Custom }

/// <summary>%IteratorHelperPrototype% instances (27.1.2.1): a generator-like state machine in native code.</summary>
internal sealed class JsIteratorHelper : JsObject
{
    public readonly DirectIterator Underlying; // Concat: unused placeholder
    public readonly HelperKind Kind;
    /// <summary>Iterator.concat: (iterable, @@iterator method) pairs and the index of the current one.</summary>
    public List<(JsValue Iterable, JsValue Method)>? Sources;
    public int SourceIndex;
    public readonly JsValue Fn;
    public double Remaining;
    public double Counter;
    public DirectIterator? Inner;
    public GeneratorState State = GeneratorState.SuspendedStart;
    /// <summary>Custom helpers (zip, chunks, windows): produce the next value (false = done) / close on return().</summary>
    public CustomStep? StepFn;
    public Action<Realm>? ReturnFn;
    public delegate bool CustomStep(Realm realm, out JsValue value);

    public JsIteratorHelper(Realm realm, DirectIterator underlying, HelperKind kind, JsValue fn, double limit)
        : base(realm, realm.IteratorHelperPrototype!)
    {
        Underlying = underlying;
        Kind = kind;
        Fn = fn;
        Remaining = limit;
    }

    public override string ClassName => "Iterator Helper";
}

/// <summary>%WrapForValidIteratorPrototype% instances (Iterator.from).</summary>
internal sealed class JsWrappedIterator : JsObject
{
    public readonly DirectIterator Iterated;
    public JsWrappedIterator(Realm realm, DirectIterator iterated) : base(realm, realm.WrapForValidIteratorPrototype!) => Iterated = iterated;
}

/// <summary>Iterator constructor, Iterator.from and Iterator.prototype helpers (ECMA-262 2025, 27.1).</summary>
internal static class IteratorHelpers
{
    public static void Setup(Realm r, JsObject iterProto)
    {
        var ctor = r.Constructor("Iterator", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (!a.IsConstruct || ReferenceEquals(a.NewTarget.Ref, a.Callee))
                throw JsErrors.TypeError(realm, "Abstract class Iterator not directly constructable");
            var p = Operations.GetPrototypeFromConstructor(realm, a.NewTarget.AsObjectUnchecked, realm.IteratorPrototype);
            return JsValue.FromObject(new JsObject(realm, p));
        }, iterProto);
        r.IteratorConstructor = ctor;
        r.DefineGlobal("Iterator", JsValue.FromObject(ctor));

        // Iterator.prototype.constructor / @@toStringTag are accessors that ignore assignments on the prototype itself
        iterProto.Delete("constructor");
        r.Getter(iterProto, "constructor", "constructor",
            static (in NativeArgs a) => JsValue.FromObject(a.Realm.IteratorConstructor!),
            static (in NativeArgs a) => { SetterIgnoringPrototype(a, "constructor"); return JsValue.Undefined; });
        r.Getter(iterProto, JsSymbol.ToStringTag, "[Symbol.toStringTag]",
            static (in NativeArgs a) => new JsValue("Iterator"),
            static (in NativeArgs a) => { SetterIgnoringPrototype(a, JsSymbol.ToStringTag); return JsValue.Undefined; });

        r.Method(ctor, "concat", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var sources = new List<(JsValue, JsValue)>();
            foreach (var item in a.Args)
            {
                if (item.IsNotObject) throw JsErrors.TypeError(realm, "Iterator.concat arguments must be objects");
                var method = item.AsObjectUnchecked.Get(JsSymbol.Iterator, item);
                if (!method.IsFunction) throw JsErrors.TypeError(realm, "Iterator.concat arguments must be iterable");
                sources.Add((item, method));
            }
            var dummy = new DirectIterator(realm.NewObject(), JsValue.Undefined);
            return JsValue.FromObject(new JsIteratorHelper(realm, dummy, HelperKind.Concat, JsValue.Undefined, 0) { Sources = sources });
        });
        r.Method(ctor, "from", 1, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var rec = GetIteratorFlattenable(realm, a[0], iterateStrings: true);
            if (Operations.OrdinaryHasInstance(realm, realm.IteratorConstructor!, JsValue.FromObject(rec.Iterator)))
                return JsValue.FromObject(rec.Iterator);
            return JsValue.FromObject(new JsWrappedIterator(realm, rec));
        });

        var wrapProto = new JsObject(r, iterProto);
        r.WrapForValidIteratorPrototype = wrapProto;
        r.Method(wrapProto, "next", 0, static (in NativeArgs a) =>
        {
            if (a.This.Ref is not JsWrappedIterator w) throw JsErrors.TypeError(a.Realm, "next called on incompatible receiver");
            return a.Realm.Vm.Call(w.Iterated.Next, JsValue.FromObject(w.Iterated.Iterator), ReadOnlySpan<JsValue>.Empty);
        });
        r.Method(wrapProto, "return", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            if (a.This.Ref is not JsWrappedIterator w) throw JsErrors.TypeError(realm, "return called on incompatible receiver");
            var it = w.Iterated.Iterator;
            var ret = it.Get("return");
            if (ret.IsNullOrUndefined) return Coroutines.CreateIterResult(realm, JsValue.Undefined, true);
            if (!ret.IsFunction) throw JsErrors.TypeError(realm, "iterator.return is not a function");
            return realm.Vm.Call(ret, JsValue.FromObject(it), ReadOnlySpan<JsValue>.Empty);
        });

        var helperProto = new JsObject(r, iterProto);
        r.IteratorHelperPrototype = helperProto;
        r.Method(helperProto, "next", 0, static (in NativeArgs a) => HelperNext(a));
        r.Method(helperProto, "return", 0, static (in NativeArgs a) => HelperReturn(a));
        helperProto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("Iterator Helper"), PropFlags.Configurable);

        r.Method(iterProto, "map", 1, static (in NativeArgs a) => CreateHelper(a, HelperKind.Map));
        r.Method(iterProto, "filter", 1, static (in NativeArgs a) => CreateHelper(a, HelperKind.Filter));
        r.Method(iterProto, "take", 1, static (in NativeArgs a) => CreateHelper(a, HelperKind.Take));
        r.Method(iterProto, "drop", 1, static (in NativeArgs a) => CreateHelper(a, HelperKind.Drop));
        r.Method(iterProto, "flatMap", 1, static (in NativeArgs a) => CreateHelper(a, HelperKind.FlatMap));
        r.Method(iterProto, "reduce", 1, static (in NativeArgs a) => Reduce(a));
        r.Method(iterProto, "toArray", 0, static (in NativeArgs a) =>
        {
            var realm = a.Realm;
            var it = ThisIterator(a, closeOn: default);
            var rec = GetIteratorDirect(realm, it);
            var arr = realm.NewArray();
            while (rec.Step(realm, out var v)) { arr.Push(v); realm.Vm.Tick(); }
            return JsValue.FromObject(arr);
        });
        r.Method(iterProto, "forEach", 1, static (in NativeArgs a) => Consume(a, 0));
        r.Method(iterProto, "some", 1, static (in NativeArgs a) => Consume(a, 1));
        r.Method(iterProto, "every", 1, static (in NativeArgs a) => Consume(a, 2));
        r.Method(iterProto, "find", 1, static (in NativeArgs a) => Consume(a, 3));
        r.Method(iterProto, "join", 1, static (in NativeArgs a) => Join(a));
    }

    /// <summary>Iterator.prototype.join (proposal-iterator-join).</summary>
    private static JsValue Join(in NativeArgs a)
    {
        var realm = a.Realm;
        var o = ThisIterator(a, default);
        string sep;
        if (a[0].IsUndefined) sep = ",";
        else
        {
            try { sep = Operations.ToString(realm, a[0]); }
            catch (JsThrowException ex) { throw CloseWithError(realm, o, ex); }
        }
        var rec = GetIteratorDirect(realm, o);
        int charged = 0;
        var sb = new System.Text.StringBuilder();
        bool first = true;
        while (rec.Step(realm, out var v))
        {
            if (!first) sb.Append(sep);
            first = false;
            if (!v.IsNullOrUndefined)
            {
                if (v.IsString) sb.Append(v.AsStringUnchecked);
                else
                {
                    try { sb.Append(Operations.ToString(realm, v)); }
                    catch (JsThrowException) { rec.CloseQuiet(realm); throw; }
                }
            }
            if (sb.Length > realm.MaxStringLength) { rec.CloseQuiet(realm); throw JsErrors.RangeError(realm, "Invalid string length"); }
            // a budget termination ends the execution; the iterator is not closed (no further JS runs)
            if (sb.Length - charged >= 4096) { realm.ChargeOutput(sb.Length - charged); charged = sb.Length; }
            realm.Vm.Tick();
        }
        realm.ChargeOutput(sb.Length - charged);
        return new JsValue(sb.ToString());
    }

    private static void SetterIgnoringPrototype(in NativeArgs a, PropertyKey key)
    {
        var realm = a.Realm;
        if (a.This.IsNotObject) throw JsErrors.TypeError(realm, "setter called on non-object");
        var o = a.This.AsObjectUnchecked;
        if (ReferenceEquals(o, realm.IteratorPrototype)) throw JsErrors.TypeError(realm, "Cannot assign to a read-only property of Iterator.prototype");
        if (!o.GetOwnProperty(key, out _)) o.CreateDataPropertyOrThrow(key, a[0]);
        else if (!o.Set(key, a[0], a.This)) throw JsErrors.TypeError(realm, $"Cannot assign to read only property '{key}'");
    }

    internal static DirectIterator GetIteratorDirect(Realm realm, JsObject obj) => new(obj, obj.Get("next"));

    /// <summary>GetIteratorFlattenable (7.4.13).</summary>
    internal static DirectIterator GetIteratorFlattenable(Realm realm, in JsValue obj, bool iterateStrings)
    {
        if (obj.IsNotObject && !(iterateStrings && obj.IsString)) throw JsErrors.TypeError(realm, $"{JsErrors.Describe(obj)} is not an object");
        var method = Operations.GetV(realm, obj, JsSymbol.Iterator);
        JsValue iterator;
        if (method.IsNullOrUndefined) iterator = obj;
        else
        {
            if (!method.IsFunction) throw JsErrors.TypeError(realm, "Symbol.iterator is not a function");
            iterator = realm.Vm.Call(method, obj, ReadOnlySpan<JsValue>.Empty);
        }
        if (iterator.IsNotObject) throw JsErrors.TypeError(realm, "iterator is not an object");
        return GetIteratorDirect(realm, iterator.AsObjectUnchecked);
    }

    /// <summary>Validates the receiver; on a later argument validation failure the receiver iterator is closed.</summary>
    private static JsObject ThisIterator(in NativeArgs a, JsValue closeOn)
    {
        if (a.This.IsNotObject) throw JsErrors.TypeError(a.Realm, "Iterator.prototype method called on a non-object");
        return a.This.AsObjectUnchecked;
    }

    private static JsThrowException CloseWithError(Realm realm, JsObject iterator, JsThrowException error)
    {
        DirectIterator.CloseQuiet(realm, iterator);
        return error;
    }

    private static JsValue CreateHelper(in NativeArgs a, HelperKind kind)
    {
        var realm = a.Realm;
        var o = ThisIterator(a, default);
        JsValue fn = default;
        double limit = 0;
        if (kind is HelperKind.Take or HelperKind.Drop)
        {
            double num;
            try { num = Operations.ToNumber(realm, a[0]); }
            catch (JsThrowException ex) { throw CloseWithError(realm, o, ex); }
            if (double.IsNaN(num)) throw CloseWithError(realm, o, JsErrors.RangeError(realm, $"{a[0]} must be a number"));
            limit = Operations.ToIntegerOrInfinity(realm, new JsValue(num));
            if (limit < 0 || (!double.IsPositiveInfinity(limit) && limit > 9007199254740991))
                throw CloseWithError(realm, o, JsErrors.RangeError(realm, $"{a[0]} must be a non-negative safe integer or Infinity"));
        }
        else
        {
            fn = a[0];
            if (!fn.IsFunction) throw CloseWithError(realm, o, JsErrors.TypeError(realm, $"{JsErrors.Describe(fn)} is not a function"));
        }
        var rec = GetIteratorDirect(realm, o);
        return JsValue.FromObject(new JsIteratorHelper(realm, rec, kind, fn, limit));
    }

    private static JsIteratorHelper ThisHelper(in NativeArgs a)
    {
        if (a.This.Ref is JsIteratorHelper h) return h;
        throw JsErrors.TypeError(a.Realm, "Iterator Helper method called on incompatible receiver");
    }

    private static JsValue HelperNext(in NativeArgs a)
    {
        var realm = a.Realm;
        var h = ThisHelper(a);
        if (h.State == GeneratorState.Executing) throw JsErrors.TypeError(realm, "Generator is already running");
        if (h.State == GeneratorState.Completed) return Coroutines.CreateIterResult(realm, JsValue.Undefined, true);
        h.State = GeneratorState.Executing;
        try
        {
            if (Step(realm, h, out var value))
            {
                h.State = GeneratorState.SuspendedYield;
                return Coroutines.CreateIterResult(realm, value, false);
            }
        }
        catch
        {
            h.State = GeneratorState.Completed;
            throw;
        }
        h.State = GeneratorState.Completed;
        return Coroutines.CreateIterResult(realm, JsValue.Undefined, true);
    }

    private static JsValue HelperReturn(in NativeArgs a)
    {
        var realm = a.Realm;
        var h = ThisHelper(a);
        if (h.State == GeneratorState.Executing) throw JsErrors.TypeError(realm, "Generator is already running");
        if (h.State == GeneratorState.Completed) return Coroutines.CreateIterResult(realm, JsValue.Undefined, true);
        var wasStart = h.State == GeneratorState.SuspendedStart;
        if (wasStart) h.State = GeneratorState.Completed;
        else h.State = GeneratorState.Executing;   // GeneratorResumeAbrupt: closing runs inside the generator
        try
        {
            if (h.Kind == HelperKind.Custom)
            {
                // closes [[UnderlyingIterators]] (also from suspended-start)
                h.ReturnFn?.Invoke(realm);
                return Coroutines.CreateIterResult(realm, JsValue.Undefined, true);
            }
            if (h.Kind == HelperKind.Concat)
            {
                var inner = h.Inner;
                h.Inner = null;
                if (!wasStart && inner != null) inner.Close(realm);
                return Coroutines.CreateIterResult(realm, JsValue.Undefined, true);
            }
            if (!wasStart && h.Inner != null)
            {
                // flatMap suspended inside an inner iterator: close the inner one first, then the outer one
                try { h.Inner.Close(realm); }
                catch (JsThrowException) { h.Underlying.CloseQuiet(realm); throw; }
            }
            h.Underlying.Close(realm);
            return Coroutines.CreateIterResult(realm, JsValue.Undefined, true);
        }
        finally { h.State = GeneratorState.Completed; }
    }

    /// <summary>Runs the helper's closure until the next yield. Returns false when it completes.</summary>
    private static bool Step(Realm realm, JsIteratorHelper h, out JsValue result)
    {
        if (h.Kind == HelperKind.Custom) return h.StepFn!(realm, out result);
        var it = h.Underlying;
        var args = new JsValue[2];
        if (h.Kind == HelperKind.Concat)
        {
            var src = h.Sources!;
            while (h.SourceIndex < src.Count)
            {
                if (h.Inner == null)
                {
                    var (iterable, method) = src[h.SourceIndex];
                    var iter = realm.Vm.Call(method, iterable, ReadOnlySpan<JsValue>.Empty);
                    if (iter.IsNotObject) throw JsErrors.TypeError(realm, "iterator is not an object");
                    h.Inner = new DirectIterator(iter.AsObjectUnchecked, iter.AsObjectUnchecked.Get("next", iter));
                }
                if (h.Inner.Step(realm, out var v)) { result = v; return true; }
                h.Inner = null;
                h.SourceIndex++;
                realm.Vm.Tick();
            }
            result = default;
            return false;
        }
        switch (h.Kind)
        {
            case HelperKind.Map:
            {
                if (!it.Step(realm, out var v)) { result = default; return false; }
                args[0] = v; args[1] = new JsValue(h.Counter);
                try { result = realm.Vm.Call(h.Fn, JsValue.Undefined, args); }
                catch (JsThrowException) { it.CloseQuiet(realm); throw; }
                h.Counter++;
                return true;
            }
            case HelperKind.Filter:
                while (true)
                {
                    if (!it.Step(realm, out var v)) { result = default; return false; }
                    args[0] = v; args[1] = new JsValue(h.Counter);
                    bool keep;
                    try { keep = Operations.ToBoolean(realm.Vm.Call(h.Fn, JsValue.Undefined, args)); }
                    catch (JsThrowException) { it.CloseQuiet(realm); throw; }
                    h.Counter++;
                    realm.Vm.Tick();
                    if (keep) { result = v; return true; }
                }
            case HelperKind.Take:
            {
                if (h.Remaining == 0)
                {
                    it.Close(realm);
                    result = default;
                    return false;
                }
                if (!double.IsPositiveInfinity(h.Remaining)) h.Remaining--;
                if (!it.Step(realm, out var v)) { result = default; return false; }
                result = v;
                return true;
            }
            case HelperKind.Drop:
            {
                while (h.Remaining > 0)
                {
                    if (!double.IsPositiveInfinity(h.Remaining)) h.Remaining--;
                    if (!it.Step(realm, out _)) { result = default; return false; }
                    realm.Vm.Tick();
                }
                if (!it.Step(realm, out var v)) { result = default; return false; }
                result = v;
                return true;
            }
            default: // FlatMap
                while (true)
                {
                    if (h.Inner == null)
                    {
                        if (!it.Step(realm, out var v)) { result = default; return false; }
                        args[0] = v; args[1] = new JsValue(h.Counter);
                        try
                        {
                            var mapped = realm.Vm.Call(h.Fn, JsValue.Undefined, args);
                            h.Inner = GetIteratorFlattenable(realm, mapped, iterateStrings: false);
                        }
                        catch (JsThrowException) { it.CloseQuiet(realm); throw; }
                        h.Counter++;
                    }
                    bool has;
                    JsValue iv;
                    try { has = h.Inner.Step(realm, out iv); }
                    catch (JsThrowException) { it.CloseQuiet(realm); throw; }
                    if (has) { result = iv; return true; }
                    h.Inner = null;
                    realm.Vm.Tick();
                }
        }
    }

    private static JsValue Reduce(in NativeArgs a)
    {
        var realm = a.Realm;
        var o = ThisIterator(a, default);
        var reducer = a[0];
        if (!reducer.IsFunction) throw CloseWithError(realm, o, JsErrors.TypeError(realm, $"{JsErrors.Describe(reducer)} is not a function"));
        var rec = GetIteratorDirect(realm, o);
        JsValue acc;
        double counter;
        if (a.Length < 2)
        {
            if (!rec.Step(realm, out acc)) throw JsErrors.TypeError(realm, "Reduce of empty iterator with no initial value");
            counter = 1;
        }
        else
        {
            acc = a[1];
            counter = 0;
        }
        var args = new JsValue[3];
        while (rec.Step(realm, out var v))
        {
            args[0] = acc; args[1] = v; args[2] = new JsValue(counter);
            try { acc = realm.Vm.Call(reducer, JsValue.Undefined, args); }
            catch (JsThrowException) { rec.CloseQuiet(realm); throw; }
            counter++;
            realm.Vm.Tick();
        }
        return acc;
    }

    /// <summary>kind: 0 forEach, 1 some, 2 every, 3 find.</summary>
    private static JsValue Consume(in NativeArgs a, int kind)
    {
        var realm = a.Realm;
        var o = ThisIterator(a, default);
        var fn = a[0];
        if (!fn.IsFunction) throw CloseWithError(realm, o, JsErrors.TypeError(realm, $"{JsErrors.Describe(fn)} is not a function"));
        var rec = GetIteratorDirect(realm, o);
        var args = new JsValue[2];
        double counter = 0;
        while (rec.Step(realm, out var v))
        {
            args[0] = v; args[1] = new JsValue(counter);
            JsValue r;
            try { r = realm.Vm.Call(fn, JsValue.Undefined, args); }
            catch (JsThrowException) { rec.CloseQuiet(realm); throw; }
            switch (kind)
            {
                case 1: if (Operations.ToBoolean(r)) { rec.Close(realm); return JsValue.True; } break;
                case 2: if (!Operations.ToBoolean(r)) { rec.Close(realm); return JsValue.False; } break;
                case 3: if (Operations.ToBoolean(r)) { rec.Close(realm); return v; } break;
            }
            counter++;
            realm.Vm.Tick();
        }
        return kind switch { 1 => JsValue.False, 2 => JsValue.True, _ => JsValue.Undefined };
    }
}
