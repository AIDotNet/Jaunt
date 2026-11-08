using System;
using System.Collections.Generic;

namespace Jaunt.Runtime;

internal enum AsyncGenState : byte { SuspendedStart, SuspendedYield, Executing, AwaitingReturn, Completed }

internal struct AsyncGenRequest
{
    public int Mode;
    public JsValue Value;
    public JsPromise Promise;
}

internal sealed class JsAsyncGenerator : JsObject
{
    public Frame? Frame;
    public AsyncGenState State;
    public readonly Queue<AsyncGenRequest> Queue = new();
    /// <summary>The reaction of the body's awaits (one at a time).</summary>
    public PromiseReaction? AwaitReaction;

    public JsAsyncGenerator(Realm realm, JsObject proto) : base(realm, proto) { }
    public override string ClassName => "AsyncGenerator";
}

/// <summary>Async generator objects (27.6), async iteration helpers and %AsyncFromSyncIteratorPrototype% (27.1.6).</summary>
internal static class AsyncGenerators
{
    public static JsValue Start(Vm vm, JsClosure c, JsValue thisValue, ReadOnlySpan<JsValue> args)
    {
        var realm = c.Realm;
        var frame = vm.PushFrame(c, thisValue, args, JsValue.Undefined, FrameFlags.OwnsRegs | FrameFlags.Coroutine | FrameFlags.ThrowAsResult);
        var r = vm.Run(frame); // parameter initialization up to InitialYield (errors are thrown synchronously)
        if (frame.Suspend == SuspendKind.Thrown) throw new JsThrowException(r);
        var protoV = c.Get("prototype");   // read after parameter initialization
        var proto = protoV.IsObject ? protoV.AsObjectUnchecked : realm.AsyncGeneratorPrototype;
        var gen = new JsAsyncGenerator(realm, proto) { Frame = frame, State = AsyncGenState.SuspendedStart };
        return JsValue.FromObject(gen);
    }

    /// <summary>AsyncGenerator.prototype.next/return/throw.</summary>
    public static JsValue Enqueue(Realm realm, JsValue thisValue, int mode, JsValue value, string method)
    {
        var promise = new JsPromise(realm);
        if (thisValue.IsNotObject || thisValue.Ref is not JsAsyncGenerator gen)
        {
            promise.RejectWith(JsErrors.TypeError(realm, $"{method} method called on incompatible receiver {thisValue}").Value);
            return JsValue.FromObject(promise);
        }
        gen.Queue.Enqueue(new AsyncGenRequest { Mode = mode, Value = value, Promise = promise });
        if (gen.State != AsyncGenState.Executing && gen.State != AsyncGenState.AwaitingReturn) ResumeNext(realm, gen);
        return JsValue.FromObject(promise);
    }

    private static void CompleteStep(Realm realm, JsAsyncGenerator gen, bool normal, JsValue value, bool done)
    {
        var req = gen.Queue.Dequeue();
        if (!normal) req.Promise.RejectWith(value);
        else req.Promise.ResolveWith(Coroutines.CreateIterResult(realm, value, done));
    }

    /// <summary>Drains the request queue while the generator is not running (AsyncGeneratorResumeNext / AwaitReturn / DrainQueue).</summary>
    private static void ResumeNext(Realm realm, JsAsyncGenerator gen)
    {
        while (true)
        {
            if (gen.State is AsyncGenState.Executing or AsyncGenState.AwaitingReturn) return;
            if (gen.Queue.Count == 0) return;
            var req = gen.Queue.Peek();
            if (req.Mode != Coroutines.ModeNext)
            {
                if (gen.State == AsyncGenState.SuspendedStart)
                {
                    gen.State = AsyncGenState.Completed;
                    gen.Frame = null;
                }
                if (gen.State == AsyncGenState.Completed)
                {
                    if (req.Mode == Coroutines.ModeReturn)
                    {
                        gen.State = AsyncGenState.AwaitingReturn;
                        JsPromise p;
                        try { p = JsPromise.Resolve(realm, req.Value); }
                        catch (JsThrowException ex)
                        {
                            gen.State = AsyncGenState.Completed;
                            CompleteStep(realm, gen, false, ex.Value, true);
                            continue;
                        }
                        p.Then(new AwaitReturn(gen));
                        return;
                    }
                    CompleteStep(realm, gen, false, req.Value, true);
                    continue;
                }
            }
            else if (gen.State == AsyncGenState.Completed)
            {
                CompleteStep(realm, gen, true, JsValue.Undefined, true);
                continue;
            }
            gen.State = AsyncGenState.Executing;
            Step(realm, gen, req.Mode, req.Value);
        }
    }

    private sealed class AwaitReturn : IPromiseContinuation
    {
        private readonly JsAsyncGenerator _gen;
        public AwaitReturn(JsAsyncGenerator gen) => _gen = gen;
        public void OnSettled(Realm realm, bool fulfilled, JsValue value)
        {
            _gen.State = AsyncGenState.Completed;
            CompleteStep(realm, _gen, fulfilled, value, true);
            ResumeNext(realm, _gen);
        }
    }

    private sealed class AwaitResume : PromiseReaction
    {
        private readonly JsAsyncGenerator _gen;
        public AwaitResume(JsAsyncGenerator gen) => _gen = gen;
        public override void Run(Realm realm, bool fulfilled, in JsValue argument)
        {
            Step(realm, _gen, fulfilled ? Coroutines.ModeNext : Coroutines.ModeThrow, argument);
            ResumeNext(realm, _gen);
        }
    }

    /// <summary>Runs the body until the next yield / await / completion. Leaves the state consistent for ResumeNext.</summary>
    private static void Step(Realm realm, JsAsyncGenerator gen, int mode, JsValue value)
    {
        var vm = realm.Vm;
        while (true)
        {
            vm.EnsureNativeStack();
            var frame = gen.Frame!;
            frame.Regs[frame.ResumeSentReg] = value;
            frame.Regs[frame.ResumeModeReg] = new JsValue(mode);
            vm.AttachFrame(frame);
            var r = vm.Run(frame);
            switch (frame.Suspend)
            {
                case SuspendKind.Thrown:
                    gen.State = AsyncGenState.Completed;
                    gen.Frame = null;
                    CompleteStep(realm, gen, false, r, true);
                    return;
                case SuspendKind.Await:
                    try
                    {
                        JsPromise.Await(realm, r, gen.AwaitReaction ??= new AwaitResume(gen));
                        return; // still Executing
                    }
                    catch (JsThrowException ex)
                    {
                        mode = Coroutines.ModeThrow;
                        value = ex.Value;
                    }
                    continue;
                case SuspendKind.Yield:
                case SuspendKind.YieldRaw:
                    gen.State = AsyncGenState.SuspendedYield;
                    CompleteStep(realm, gen, true, r, false);
                    return;
                default:
                    gen.State = AsyncGenState.Completed;
                    gen.Frame = null;
                    CompleteStep(realm, gen, true, r, true);
                    return;
            }
        }
    }

    // ------------------------------------------------------------------ async iteration helpers

    private static JsValue GetMethod(Realm realm, JsValue v, in PropertyKey key)
    {
        var m = Operations.GetV(realm, v, key);
        if (m.IsNullOrUndefined) return JsValue.Undefined;
        if (!m.IsFunction) throw JsErrors.TypeError(realm, $"{key} is not a function");
        return m;
    }

    /// <summary>GetIterator(obj, async): returns the async iterator object (wrapping a sync iterator when needed).</summary>
    public static JsValue GetAsyncIterator(Realm realm, JsValue obj)
    {
        var method = GetMethod(realm, obj, JsSymbol.AsyncIterator);
        if (method.IsUndefined)
        {
            var syncMethod = GetMethod(realm, obj, JsSymbol.Iterator);
            if (syncMethod.IsUndefined) throw JsErrors.TypeError(realm, $"{JsErrors.Describe(obj)} is not async iterable");
            var syncIt = realm.Vm.Call(syncMethod, obj, ReadOnlySpan<JsValue>.Empty);
            if (syncIt.IsNotObject) throw JsErrors.TypeError(realm, "Result of the Symbol.iterator method is not an object");
            var next = syncIt.AsObjectUnchecked.Get("next", syncIt);
            return JsValue.FromObject(new JsAsyncFromSyncIterator(realm, syncIt.AsObjectUnchecked, next));
        }
        var it = realm.Vm.Call(method, obj, ReadOnlySpan<JsValue>.Empty);
        if (it.IsNotObject) throw JsErrors.TypeError(realm, "Result of the Symbol.asyncIterator method is not an object");
        return it;
    }

    internal sealed class JsAsyncFromSyncIterator : JsObject
    {
        public readonly JsObject SyncIterator;
        public readonly JsValue NextMethod;
        public JsAsyncFromSyncIterator(Realm realm, JsObject syncIterator, JsValue next) : base(realm, realm.AsyncFromSyncIteratorPrototype)
        {
            SyncIterator = syncIterator;
            NextMethod = next;
        }
    }

    private sealed class FromSyncContinuation : IPromiseContinuation
    {
        public required PromiseCapability Capability;
        public required JsObject SyncIterator;
        public bool Done;
        public bool CloseOnRejection;

        public void OnSettled(Realm realm, bool fulfilled, JsValue value)
        {
            if (fulfilled)
            {
                Capability.DoResolve(realm, Coroutines.CreateIterResult(realm, value, Done));
                return;
            }
            if (!Done && CloseOnRejection)
            {
                // IteratorClose(syncIteratorRecord, ThrowCompletion(error)): errors from return() are suppressed
                try { CloseSync(realm, SyncIterator); } catch (JsThrowException) { }
            }
            Capability.DoReject(realm, value);
        }
    }

    private static void CloseSync(Realm realm, JsObject it)
    {
        var ret = it.Get("return");
        if (ret.IsNullOrUndefined) return;
        var r = realm.Vm.Call(ret, JsValue.FromObject(it), ReadOnlySpan<JsValue>.Empty);
        if (r.IsNotObject) throw JsErrors.TypeError(realm, "Iterator result is not an object");
    }

    private static void Continuation(Realm realm, JsValue result, PromiseCapability cap, JsObject syncIt, bool closeOnRejection)
    {
        bool done;
        JsValue value;
        JsPromise wrapper;
        try
        {
            var ro = result.AsObjectUnchecked;
            done = Operations.ToBoolean(ro.Get("done", result));
            value = ro.Get("value", result);
        }
        catch (JsThrowException ex) { cap.DoReject(realm, ex.Value); return; }
        try { wrapper = JsPromise.Resolve(realm, value); }
        catch (JsThrowException ex)
        {
            if (!done && closeOnRejection)
            {
                try { CloseSync(realm, syncIt); } catch (JsThrowException) { }
            }
            cap.DoReject(realm, ex.Value);
            return;
        }
        wrapper.Then(new FromSyncContinuation { Capability = cap, SyncIterator = syncIt, Done = done, CloseOnRejection = closeOnRejection });
    }

    private static JsValue FromSyncStep(in NativeArgs a, int mode)
    {
        var realm = a.Realm;
        var promise = new JsPromise(realm);
        var cap = new PromiseCapability(promise);
        var self = (JsAsyncFromSyncIterator)a.This.Ref!;
        var syncIt = self.SyncIterator;
        var syncV = JsValue.FromObject(syncIt);
        JsValue result;
        try
        {
            switch (mode)
            {
                case Coroutines.ModeNext:
                    result = a.Length > 0
                        ? realm.Vm.Call(self.NextMethod, syncV, a.Args.Slice(0, 1))
                        : realm.Vm.Call(self.NextMethod, syncV, ReadOnlySpan<JsValue>.Empty);
                    break;
                case Coroutines.ModeReturn:
                {
                    var ret = GetMethod(realm, syncV, "return");
                    if (ret.IsUndefined)
                    {
                        cap.DoResolve(realm, Coroutines.CreateIterResult(realm, a[0], true));
                        return JsValue.FromObject(promise);
                    }
                    result = a.Length > 0
                        ? realm.Vm.Call(ret, syncV, a.Args.Slice(0, 1))
                        : realm.Vm.Call(ret, syncV, ReadOnlySpan<JsValue>.Empty);
                    break;
                }
                default:
                {
                    var thr = GetMethod(realm, syncV, "throw");
                    if (thr.IsUndefined)
                    {
                        CloseSync(realm, syncIt);
                        throw JsErrors.TypeError(realm, "The iterator does not provide a 'throw' method");
                    }
                    result = a.Length > 0
                        ? realm.Vm.Call(thr, syncV, a.Args.Slice(0, 1))
                        : realm.Vm.Call(thr, syncV, ReadOnlySpan<JsValue>.Empty);
                    break;
                }
            }
            if (result.IsNotObject) throw JsErrors.TypeError(realm, $"Iterator result {result} is not an object");
        }
        catch (JsThrowException ex)
        {
            cap.DoReject(realm, ex.Value);
            return JsValue.FromObject(promise);
        }
        Continuation(realm, result, cap, syncIt, closeOnRejection: mode != Coroutines.ModeReturn);
        return JsValue.FromObject(promise);
    }

    // ------------------------------------------------------------------ intrinsics

    /// <summary>Part of the Iterator group: %AsyncIteratorPrototype%, %AsyncGeneratorPrototype%, %AsyncFromSyncIteratorPrototype%.</summary>
    public static void Setup(Realm r)
    {
        var asyncIterProto = new JsObject(r, r.ObjectPrototype);
        r.AsyncIteratorPrototype = asyncIterProto;
        r.Method(asyncIterProto, JsSymbol.AsyncIterator, "[Symbol.asyncIterator]", 0, static (in NativeArgs a) => a.This);

        var genProto = new JsObject(r, asyncIterProto);
        r.AsyncGeneratorPrototype = genProto;
        r.AsyncGeneratorFunctionPrototype.AddOwnProperty("prototype", JsValue.FromObject(genProto), PropFlags.Configurable);
        genProto.AddOwnProperty("constructor", JsValue.FromObject(r.AsyncGeneratorFunctionPrototype), PropFlags.Configurable);
        r.Method(genProto, "next", 1, static (in NativeArgs a) => Enqueue(a.Realm, a.This, Coroutines.ModeNext, a[0], "AsyncGenerator.prototype.next"));
        r.Method(genProto, "return", 1, static (in NativeArgs a) => Enqueue(a.Realm, a.This, Coroutines.ModeReturn, a[0], "AsyncGenerator.prototype.return"));
        r.Method(genProto, "throw", 1, static (in NativeArgs a) => Enqueue(a.Realm, a.This, Coroutines.ModeThrow, a[0], "AsyncGenerator.prototype.throw"));
        genProto.AddOwnProperty(JsSymbol.ToStringTag, new JsValue("AsyncGenerator"), PropFlags.Configurable);

        var fromSync = new JsObject(r, asyncIterProto);
        r.AsyncFromSyncIteratorPrototype = fromSync;
        r.Method(fromSync, "next", 1, static (in NativeArgs a) => FromSyncStep(a, Coroutines.ModeNext));
        r.Method(fromSync, "return", 1, static (in NativeArgs a) => FromSyncStep(a, Coroutines.ModeReturn));
        r.Method(fromSync, "throw", 1, static (in NativeArgs a) => FromSyncStep(a, Coroutines.ModeThrow));
    }
}
