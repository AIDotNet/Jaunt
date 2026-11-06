using System;

namespace Jaunt.Runtime;

internal enum GeneratorState : byte { SuspendedStart, SuspendedYield, Executing, Completed }

internal sealed class JsGenerator : JsObject
{
    public Frame? Frame;
    public GeneratorState State;

    public JsGenerator(Realm realm, JsObject proto) : base(realm, proto) { }
    public override string ClassName => "Generator";
}

/// <summary>Generator and async function drivers built on suspendable heap frames.</summary>
internal static class Coroutines
{
    public const int ModeNext = 0, ModeThrow = 1, ModeReturn = 2;

    public static JsValue StartGenerator(Vm vm, JsClosure c, JsValue thisValue, ReadOnlySpan<JsValue> args)
    {
        var realm = c.Realm;
        var frame = vm.PushFrame(c, thisValue, args, JsValue.Undefined, FrameFlags.OwnsRegs | FrameFlags.Coroutine);
        // runs parameter initialization up to the InitialYield; the generator object (and its prototype lookup)
        // is created afterwards (FunctionDeclarationInstantiation precedes OrdinaryCreateFromConstructor)
        vm.Run(frame);
        var protoV = c.Get("prototype");
        var proto = protoV.IsObject ? protoV.AsObjectUnchecked : realm.GeneratorPrototype;
        var gen = new JsGenerator(realm, proto) { Frame = frame, State = GeneratorState.SuspendedStart };
        return JsValue.FromObject(gen);
    }

    public static JsValue CreateIterResult(Realm realm, JsValue value, bool done)
    {
        var o = new JsObject(realm, realm.IterResultShape, 2);
        o.Slots[0] = value;
        o.Slots[1] = JsValue.FromBool(done);
        return JsValue.FromObject(o);
    }

    public static JsValue Resume(Realm realm, JsValue thisValue, int mode, JsValue value, string method)
    {
        if (thisValue.IsNotObject || thisValue.Ref is not JsGenerator gen)
            throw JsErrors.TypeError(realm, $"{method} method called on incompatible receiver {thisValue}");
        var vm = realm.Vm;
        switch (gen.State)
        {
            case GeneratorState.Executing:
                throw JsErrors.TypeError(realm, "Generator is already running");
            case GeneratorState.Completed:
                if (mode == ModeThrow) throw new JsThrowException(value);
                return CreateIterResult(realm, mode == ModeReturn ? value : JsValue.Undefined, true);
            case GeneratorState.SuspendedStart when mode != ModeNext:
                gen.State = GeneratorState.Completed;
                gen.Frame = null;
                if (mode == ModeThrow) throw new JsThrowException(value);
                return CreateIterResult(realm, value, true);
        }
        vm.EnsureNativeStack();
        var frame = gen.Frame!;
        frame.Regs[frame.ResumeSentReg] = value;
        frame.Regs[frame.ResumeModeReg] = new JsValue(mode);
        gen.State = GeneratorState.Executing;
        vm.AttachFrame(frame);
        JsValue r;
        try
        {
            r = vm.Run(frame);
        }
        catch
        {
            gen.State = GeneratorState.Completed;
            gen.Frame = null;
            throw;
        }
        switch (frame.Suspend)
        {
            case SuspendKind.Yield:
                gen.State = GeneratorState.SuspendedYield;
                return CreateIterResult(realm, r, false);
            case SuspendKind.YieldRaw:
                gen.State = GeneratorState.SuspendedYield;
                return r;
            case SuspendKind.Await:
                gen.State = GeneratorState.Completed;
                throw new InvalidOperationException("Jaunt internal error: await in generator.");
            default:
                gen.State = GeneratorState.Completed;
                gen.Frame = null;
                return CreateIterResult(realm, r, true);
        }
    }

    // ------------------------------------------------------------------ async functions

    private sealed class AsyncState : IPromiseContinuation
    {
        public required Frame Frame;
        public required PromiseCapability Capability;

        public void OnSettled(Realm realm, bool fulfilled, JsValue value) => Step(realm, this, fulfilled ? ModeNext : ModeThrow, value, first: false);
    }

    public static JsValue StartAsync(Vm vm, JsClosure c, JsValue thisValue, ReadOnlySpan<JsValue> args)
    {
        var realm = c.Realm;
        var promise = new JsPromise(realm);
        var frame = vm.PushFrame(c, thisValue, args, JsValue.Undefined, FrameFlags.OwnsRegs | FrameFlags.Coroutine);
        var state = new AsyncState { Frame = frame, Capability = new PromiseCapability(promise) };
        Step(realm, state, ModeNext, JsValue.Undefined, first: true);
        return JsValue.FromObject(promise);
    }

    /// <summary>Starts an async body (module with top-level await) and returns its completion promise.</summary>
    public static JsPromise StartAsyncBody(Vm vm, JsClosure c, JsValue thisValue)
    {
        var realm = c.Realm;
        var promise = new JsPromise(realm);
        var frame = vm.PushFrame(c, thisValue, ReadOnlySpan<JsValue>.Empty, JsValue.Undefined, FrameFlags.OwnsRegs | FrameFlags.Coroutine);
        var state = new AsyncState { Frame = frame, Capability = new PromiseCapability(promise) };
        Step(realm, state, ModeNext, JsValue.Undefined, first: true);
        return promise;
    }

    private static void Step(Realm realm, AsyncState state, int mode, JsValue value, bool first)
    {
        var vm = realm.Vm;
        vm.EnsureNativeStack();
        var frame = state.Frame;
        if (!first)
        {
            frame.Regs[frame.ResumeSentReg] = value;
            frame.Regs[frame.ResumeModeReg] = new JsValue(mode);
            vm.AttachFrame(frame);
        }
        JsValue r;
        try
        {
            r = vm.Run(frame);
        }
        catch (JsThrowException ex)
        {
            state.Capability.DoReject(realm, ex.Value);
            return;
        }
        if (frame.Suspend == SuspendKind.Await)
        {
            JsPromise awaited;
            try
            {
                awaited = JsPromise.Resolve(realm, r);
            }
            catch (JsThrowException ex)
            {
                // resume with a throw completion
                Step(realm, state, ModeThrow, ex.Value, first: false);
                return;
            }
            awaited.Then(state);
            return;
        }
        state.Capability.DoResolve(realm, r);
    }
}
