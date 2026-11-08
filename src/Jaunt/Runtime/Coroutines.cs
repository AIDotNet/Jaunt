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
        // try/finally rather than catch + rethrow: with .NET's exception model a catch block runs on top of the
        // unwinding stack, so a rethrow at every level of a deep `yield*` chain stacks up until the process overflows
        bool completed = false;
        try
        {
            r = vm.Run(frame);
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                gen.State = GeneratorState.Completed;
                gen.Frame = null;
            }
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

    /// <summary>A running async function; the same object is the reaction of each of its awaits.</summary>
    private sealed class AsyncState : PromiseReaction
    {
        public readonly Frame Frame;
        public readonly JsPromise Promise;
        public AsyncState(Frame frame, JsPromise promise) { Frame = frame; Promise = promise; }

        public override void Run(Realm realm, bool fulfilled, in JsValue argument)
        {
            var frame = Frame;
            frame.Regs[frame.ResumeSentReg] = argument;
            frame.Regs[frame.ResumeModeReg] = new JsValue(fulfilled ? ModeNext : ModeThrow);
            var vm = realm.Vm;
            vm.EnsureNativeStack();
            vm.AttachFrame(frame);
            Step(realm, this);
        }
    }

    public static JsValue StartAsync(Vm vm, JsClosure c, JsValue thisValue, ReadOnlySpan<JsValue> args)
    {
        var promise = new JsPromise(c.Realm);
        vm.EnsureNativeStack();
        var frame = vm.PushFrame(c, thisValue, args, JsValue.Undefined, FrameFlags.OwnsRegs | FrameFlags.Coroutine | FrameFlags.ThrowAsResult);
        Step(c.Realm, new AsyncState(frame, promise));
        return JsValue.FromObject(promise);
    }

    /// <summary>Starts an async body (module with top-level await) and returns its completion promise.</summary>
    public static JsPromise StartAsyncBody(Vm vm, JsClosure c, JsValue thisValue)
    {
        var promise = new JsPromise(c.Realm);
        vm.EnsureNativeStack();
        var frame = vm.PushFrame(c, thisValue, ReadOnlySpan<JsValue>.Empty, JsValue.Undefined, FrameFlags.OwnsRegs | FrameFlags.Coroutine | FrameFlags.ThrowAsResult);
        Step(c.Realm, new AsyncState(frame, promise));
        return promise;
    }

    /// <summary>Runs the attached frame until its next await or its completion.</summary>
    private static void Step(Realm realm, AsyncState state)
    {
        var vm = realm.Vm;
        var frame = state.Frame;
        while (true)
        {
            var r = vm.Run(frame);
            switch (frame.Suspend)
            {
                case SuspendKind.Await:
                    try
                    {
                        JsPromise.Await(realm, r, state);
                        return;
                    }
                    catch (JsThrowException ex)
                    {
                        // resume with a throw completion
                        frame.Regs[frame.ResumeSentReg] = ex.Value;
                        frame.Regs[frame.ResumeModeReg] = new JsValue(ModeThrow);
                        vm.EnsureNativeStack();
                        vm.AttachFrame(frame);
                    }
                    break;
                case SuspendKind.Thrown:
                    state.Promise.RejectWith(r);
                    return;
                default:
                    state.Promise.ResolveWith(r);
                    return;
            }
        }
    }
}
