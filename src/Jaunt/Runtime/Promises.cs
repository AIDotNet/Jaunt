using System.Threading;
using System.Diagnostics;
using System;
using System.Collections.Generic;

namespace Jaunt.Runtime;

internal enum PromiseState : byte { Pending, Fulfilled, Rejected }

/// <summary>Internal continuation invoked when a promise settles (used by await and host bridges).</summary>
internal interface IPromiseContinuation
{
    void OnSettled(Realm realm, bool fulfilled, JsValue value);
}

internal sealed class PromiseCapability
{
    public readonly JsObject Promise;
    public readonly JsValue Resolve;
    public readonly JsValue Reject;

    public PromiseCapability(JsObject promise, JsValue resolve, JsValue reject)
    {
        Promise = promise;
        Resolve = resolve;
        Reject = reject;
    }

    /// <summary>Capability over an intrinsic promise without materialized resolving functions.</summary>
    public PromiseCapability(JsPromise promise)
    {
        Promise = promise;
    }

    public void DoResolve(Realm realm, JsValue value)
    {
        if (Resolve.IsUndefined && Promise is JsPromise p) { p.ResolveWith(value); return; }
        realm.Vm.Call(Resolve, JsValue.Undefined, new ReadOnlySpan<JsValue>(in value));
    }

    public void DoReject(Realm realm, JsValue reason)
    {
        if (Reject.IsUndefined && Promise is JsPromise p) { p.RejectWith(reason); return; }
        realm.Vm.Call(Reject, JsValue.Undefined, new ReadOnlySpan<JsValue>(in reason));
    }
}

internal sealed class PromiseReaction
{
    public PromiseCapability? Capability;
    public bool IsFulfillReaction;
    public JsValue Handler;
    public IPromiseContinuation? Continuation;
}

internal abstract class Job
{
    public abstract void Run(Realm realm);
}

internal sealed class PromiseReactionJob : Job
{
    private readonly PromiseReaction _reaction;
    private readonly JsValue _argument;
    public PromiseReactionJob(PromiseReaction reaction, JsValue argument) { _reaction = reaction; _argument = argument; }

    public override void Run(Realm realm)
    {
        var r = _reaction;
        if (r.Continuation != null)
        {
            r.Continuation.OnSettled(realm, r.IsFulfillReaction, _argument);
            return;
        }
        JsValue result;
        bool ok;
        if (r.Handler.IsUndefined)
        {
            result = _argument;
            ok = r.IsFulfillReaction;
        }
        else
        {
            try
            {
                result = realm.Vm.Call(r.Handler, JsValue.Undefined, new ReadOnlySpan<JsValue>(in _argument));
                ok = true;
            }
            catch (JsThrowException ex)
            {
                result = ex.Value;
                ok = false;
            }
        }
        if (r.Capability == null) return;
        if (ok) r.Capability.DoResolve(realm, result);
        else r.Capability.DoReject(realm, result);
    }
}

internal sealed class PromiseResolveThenableJob : Job
{
    private readonly JsPromise _promise;
    private readonly JsValue _thenable;
    private readonly JsValue _then;
    public PromiseResolveThenableJob(JsPromise p, JsValue thenable, JsValue then) { _promise = p; _thenable = thenable; _then = then; }

    public override void Run(Realm realm)
    {
        var (res, rej) = _promise.CreateResolvingFunctions();
        try
        {
            var args = new[] { JsValue.FromObject(res), JsValue.FromObject(rej) };
            realm.Vm.Call(_then, _thenable, args);
        }
        catch (JsThrowException ex)
        {
            rej.Call(JsValue.Undefined, new ReadOnlySpan<JsValue>(in ex.Value));
        }
    }
}

internal sealed class CallbackJob : Job
{
    private readonly Action<Realm> _action;
    public CallbackJob(Action<Realm> action) => _action = action;
    public override void Run(Realm realm) => _action(realm);
}

/// <summary>Promise instance (ECMA-262 27.2).</summary>
internal sealed class JsPromise : JsObject
{
    public PromiseState State;
    public JsValue Result;
    private List<PromiseReaction>? _fulfill;
    private List<PromiseReaction>? _reject;
    public bool IsHandled;

    public JsPromise(Realm realm, JsObject? proto = null) : base(realm, proto ?? realm.PromisePrototype) { }

    public override string ClassName => "Promise";

    /// <summary>CreateResolvingFunctions (27.2.1.3).</summary>
    public (JsBuiltin resolve, JsBuiltin reject) CreateResolvingFunctions()
    {
        var shared = new bool[1];
        var resolve = new JsBuiltin(Realm, "", 1, static (in NativeArgs a) =>
        {
            var (p, done) = ((JsPromise, bool[]))a.Callee.State!;
            if (done[0]) return JsValue.Undefined;
            done[0] = true;
            p.ResolveWith(a[0]);
            return JsValue.Undefined;
        }) { State = (this, shared) };
        var reject = new JsBuiltin(Realm, "", 1, static (in NativeArgs a) =>
        {
            var (p, done) = ((JsPromise, bool[]))a.Callee.State!;
            if (done[0]) return JsValue.Undefined;
            done[0] = true;
            p.RejectWith(a[0]);
            return JsValue.Undefined;
        }) { State = (this, shared) };
        return (resolve, reject);
    }

    /// <summary>Promise Resolve Functions steps 7-16.</summary>
    public void ResolveWith(JsValue resolution)
    {
        if (State != PromiseState.Pending) return;
        if (resolution.IsObject && ReferenceEquals(resolution.Ref, this))
        {
            RejectWith(JsValue.FromObject(JsError.Create(Realm, ErrorKind.TypeError, "Chaining cycle detected for promise")));
            return;
        }
        if (resolution.IsNotObject)
        {
            Fulfill(resolution);
            return;
        }
        var obj = resolution.AsObjectUnchecked;
        JsValue then;
        try
        {
            // fast path: intrinsic promise with untouched then
            then = obj.Get("then", resolution);
        }
        catch (JsThrowException ex)
        {
            RejectWith(ex.Value);
            return;
        }
        if (!then.IsFunction)
        {
            Fulfill(resolution);
            return;
        }
        Realm.Vm.EnqueueJob(new PromiseResolveThenableJob(this, resolution, then));
    }

    public void Fulfill(JsValue value)
    {
        if (State != PromiseState.Pending) return;
        var reactions = _fulfill;
        Result = value;
        State = PromiseState.Fulfilled;
        _fulfill = null;
        _reject = null;
        if (reactions != null) foreach (var r in reactions) Realm.Vm.EnqueueJob(new PromiseReactionJob(r, value));
    }

    public void RejectWith(JsValue reason)
    {
        if (State != PromiseState.Pending) return;
        var reactions = _reject;
        Result = reason;
        State = PromiseState.Rejected;
        _fulfill = null;
        _reject = null;
        if (!IsHandled) Realm.TrackRejection(this, handled: false);
        if (reactions != null) foreach (var r in reactions) Realm.Vm.EnqueueJob(new PromiseReactionJob(r, reason));
    }

    /// <summary>PerformPromiseThen (27.2.5.4.1).</summary>
    public void Then(PromiseReaction onFulfilled, PromiseReaction onRejected)
    {
        switch (State)
        {
            case PromiseState.Pending:
                (_fulfill ??= new()).Add(onFulfilled);
                (_reject ??= new()).Add(onRejected);
                break;
            case PromiseState.Fulfilled:
                Realm.Vm.EnqueueJob(new PromiseReactionJob(onFulfilled, Result));
                break;
            default:
                if (!IsHandled) Realm.TrackRejection(this, handled: true);
                Realm.Vm.EnqueueJob(new PromiseReactionJob(onRejected, Result));
                break;
        }
        IsHandled = true;
    }

    public void Then(IPromiseContinuation continuation)
    {
        Then(new PromiseReaction { IsFulfillReaction = true, Continuation = continuation },
             new PromiseReaction { IsFulfillReaction = false, Continuation = continuation });
    }

    /// <summary>PromiseResolve(%Promise%, x).</summary>
    public static JsPromise Resolve(Realm realm, JsValue x)
    {
        if (x.IsObject && x.Ref is JsPromise p)
        {
            var ctor = p.Get("constructor");
            if (ctor.IsObject && ReferenceEquals(ctor.Ref, realm.PromiseConstructor)) return p;
        }
        var np = new JsPromise(realm);
        np.ResolveWith(x);
        return np;
    }

    public static JsPromise Rejected(Realm realm, JsValue reason)
    {
        var p = new JsPromise(realm);
        p.RejectWith(reason);
        return p;
    }
}

internal sealed partial class Vm
{
    private readonly Queue<Job> _jobs = new();
    public int MaxPendingJobs = 1_000_000;

    public int PendingJobCount => _jobs.Count;

    public void EnqueueJob(Job job)
    {
        if (_jobs.Count >= MaxPendingJobs)
            throw new JsTerminationException(TerminationKind.PendingJobs, $"Pending job queue exceeded its limit of {MaxPendingJobs}.", _jobs.Count + 1, MaxPendingJobs);
        _jobs.Enqueue(job);
    }

    /// <summary>Runs ready microtasks until the queue is empty (each job is budgeted).</summary>
    public void RunJobs()
    {
        int n = 0;
        while (true)
        {
            while (_jobs.Count > 0)
            {
                // notified waiters, and (every 64 jobs) timed-out ones while a job loop keeps the queue busy
                if (_wakePending != 0 || (_asyncWaiters is { Count: > 0 } && (++n & 63) == 0)) ProcessAsyncWaiters();
                var job = _jobs.Dequeue();
                Tick();
                job.Run(Realm);
            }
            // Atomics.waitAsync waiters: notified by another agent, or timed out without a host timer
            if (_asyncWaiters is not { Count: > 0 }) break;
            if (ProcessAsyncWaiters()) continue;
            if (!CanBlock || !AwaitWaiterProgress()) break;
        }
    }

    // ------------------------------------------------------------------ Atomics.waitAsync waiters of this agent

    /// <summary>[[CanBlock]] of this agent: Atomics.wait may park the thread, and a drained job queue waits for this
    /// agent's pending waitAsync promises that can still settle.</summary>
    public bool CanBlock;
    private List<AtomicsWaiter>? _asyncWaiters;
    private volatile int _wakePending;
    private ManualResetEventSlim? _wakeEvent;

    internal void AddAsyncWaiter(AtomicsWaiter w)
    {
        _wakeEvent ??= new ManualResetEventSlim(false);
        (_asyncWaiters ??= new List<AtomicsWaiter>()).Add(w);
    }

    /// <summary>Called by another agent's Atomics.notify (any thread).</summary>
    internal void SignalWake()
    {
        _wakePending = 1;
        _wakeEvent?.Set();
    }

    /// <summary>Resolves this agent's notified or timed-out waitAsync promises; true when any was resolved.</summary>
    internal bool ProcessAsyncWaiters()
    {
        _wakePending = 0;
        var list = _asyncWaiters;
        if (list == null || list.Count == 0) return false;
        long now = Stopwatch.GetTimestamp();
        bool any = false;
        for (int i = 0; i < list.Count;)
        {
            var w = list[i];
            bool ok;
            if (w.Notified) ok = true;
            else if (w.Deadline <= now)
            {
                lock (w.Block.Lock)
                {
                    ok = w.Notified;
                    if (!ok) w.Block.Waiters.Remove(w);
                }
            }
            else { i++; continue; }
            list.RemoveAt(i);
            w.Promise!.ResolveWith(new JsValue(ok ? "ok" : "timed-out"));
            any = true;
        }
        return any;
    }

    /// <summary>
    /// Blocks until a pending waiter can make progress (notify from another agent or the earliest timeout). Returns
    /// false when no waiter can ever settle (infinite timeout on memory no other agent shares).
    /// </summary>
    private bool AwaitWaiterProgress()
    {
        long earliest = long.MaxValue;
        bool shared = false;
        foreach (var w in _asyncWaiters!)
        {
            if (w.Deadline < earliest) earliest = w.Deadline;
            if (w.Block.CrossAgent) shared = true;
        }
        if (earliest == long.MaxValue && !shared) return false;
        while (_wakePending == 0)
        {
            long now = Stopwatch.GetTimestamp();
            if (earliest != long.MaxValue && now >= earliest) break;
            int ms = earliest == long.MaxValue ? 20 : (int)Math.Clamp((earliest - now) * 1000 / Stopwatch.Frequency + 1, 1, 20);
            _wakeEvent!.Wait(ms);
            _wakeEvent.Reset();
            Checkpoint();
        }
        return true;
    }

    public void ClearJobs() => _jobs.Clear();
}
