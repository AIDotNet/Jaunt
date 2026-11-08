using System.Runtime.CompilerServices;
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
    public JsValue Resolve;
    public JsValue Reject;
    private readonly bool _intrinsic;
    /// <summary>[[AlreadyResolved]] of an intrinsic capability (the resolving functions, once materialized, share it).</summary>
    private bool _alreadyResolved;

    public PromiseCapability(JsObject promise, JsValue resolve, JsValue reject)
    {
        Promise = promise;
        Resolve = resolve;
        Reject = reject;
    }

    /// <summary>Capability over an intrinsic promise; its resolving functions are created only when asked for.</summary>
    public PromiseCapability(JsPromise promise)
    {
        Promise = promise;
        _intrinsic = true;
    }

    public bool IsIntrinsic => _intrinsic;

    public void DoResolve(Realm realm, JsValue value)
    {
        if (_intrinsic)
        {
            if (_alreadyResolved) return;
            _alreadyResolved = true;
            ((JsPromise)Promise).ResolveWith(value);
            return;
        }
        realm.Vm.Call(Resolve, JsValue.Undefined, new ReadOnlySpan<JsValue>(in value));
    }

    public void DoReject(Realm realm, JsValue reason)
    {
        if (_intrinsic)
        {
            if (_alreadyResolved) return;
            _alreadyResolved = true;
            ((JsPromise)Promise).RejectWith(reason);
            return;
        }
        realm.Vm.Call(Reject, JsValue.Undefined, new ReadOnlySpan<JsValue>(in reason));
    }

    /// <summary>The capability's resolving functions (CreateResolvingFunctions on first use for an intrinsic capability).</summary>
    public (JsValue resolve, JsValue reject) Functions(Realm realm)
    {
        if (Resolve.IsUndefined)
        {
            Resolve = JsValue.FromObject(new JsBuiltin(realm, "", 1, static (in NativeArgs a) =>
            {
                ((PromiseCapability)a.Callee.State!).DoResolve(a.Realm, a[0]);
                return JsValue.Undefined;
            }) { State = this });
            Reject = JsValue.FromObject(new JsBuiltin(realm, "", 1, static (in NativeArgs a) =>
            {
                ((PromiseCapability)a.Callee.State!).DoReject(a.Realm, a[0]);
                return JsValue.Undefined;
            }) { State = this });
        }
        return (Resolve, Reject);
    }
}

/// <summary>
/// A microtask. The queue stores the job with its argument, so a reaction, which is a job, needs no second object to
/// be scheduled.
/// </summary>
internal abstract class Job
{
    public abstract void Run(Realm realm, bool fulfilled, in JsValue argument);
}

/// <summary>
/// PromiseReaction: runs when the promise it was added to settles. It sits in at most one promise's list at a time
/// (<see cref="Next"/> is intrusive), which is what lets an async function reuse one object for all its awaits.
/// </summary>
internal abstract class PromiseReaction : Job
{
    public PromiseReaction? Next;
}

/// <summary>The reaction pair of one <c>then</c> call: the handlers (null when not callable) and the derived promise.</summary>
internal sealed class ThenReaction : PromiseReaction
{
    private readonly JsFunctionObject? _onFulfilled;
    private readonly JsFunctionObject? _onRejected;
    /// <summary>A <see cref="JsPromise"/> (intrinsic derived promise, nothing else can resolve it) or a <see cref="PromiseCapability"/>.</summary>
    private readonly object _target;

    public ThenReaction(JsFunctionObject? onFulfilled, JsFunctionObject? onRejected, object target)
    {
        _onFulfilled = onFulfilled;
        _onRejected = onRejected;
        _target = target;
    }

    public override void Run(Realm realm, bool fulfilled, in JsValue argument)
    {
        var handler = fulfilled ? _onFulfilled : _onRejected;
        JsValue result;
        bool ok;
        if (handler == null)
        {
            result = argument;
            ok = fulfilled;
        }
        else
        {
            try
            {
                result = handler.Call(JsValue.Undefined, new ReadOnlySpan<JsValue>(in argument));
                ok = true;
            }
            catch (JsThrowException ex)
            {
                result = ex.Value;
                ok = false;
            }
        }
        if (_target is JsPromise p)
        {
            if (ok) p.ResolveWith(result);
            else p.RejectWith(result);
            return;
        }
        var cap = (PromiseCapability)_target;
        if (ok) cap.DoResolve(realm, result);
        else cap.DoReject(realm, result);
    }
}

/// <summary>Adapts an <see cref="IPromiseContinuation"/>, which unlike a reaction may wait on several promises at once.</summary>
internal sealed class ContinuationReaction : PromiseReaction
{
    private readonly IPromiseContinuation _continuation;
    public ContinuationReaction(IPromiseContinuation continuation) => _continuation = continuation;
    public override void Run(Realm realm, bool fulfilled, in JsValue argument) => _continuation.OnSettled(realm, fulfilled, argument);
}

/// <summary>
/// The resolving functions of a <see cref="PromiseResolveThenableJob"/> when its thenable is a pristine intrinsic promise:
/// no one else can reach them, so they need no objects and no [[AlreadyResolved]].
/// </summary>
internal sealed class ForwardReaction : PromiseReaction
{
    private readonly JsPromise _promise;
    public ForwardReaction(JsPromise promise) => _promise = promise;

    public override void Run(Realm realm, bool fulfilled, in JsValue argument)
    {
        if (fulfilled) _promise.ResolveWith(argument);
        else _promise.RejectWith(argument);
    }
}

internal sealed class PromiseResolveThenableJob : Job
{
    private readonly JsPromise _promise;
    private readonly JsValue _thenable;
    private readonly JsValue _then;
    public PromiseResolveThenableJob(JsPromise p, JsValue thenable, JsValue then) { _promise = p; _thenable = thenable; _then = then; }

    public override void Run(Realm realm, bool fulfilled, in JsValue argument)
    {
        // Calling the intrinsic then on a pristine promise observes nothing, and its derived promise is unreachable.
        if (_thenable.Ref is JsPromise thenable && ReferenceEquals(_then.Ref, thenable.Realm.PromiseThenFunction) && thenable.Realm.IsPristinePromise(thenable))
        {
            thenable.AddReaction(new ForwardReaction(_promise));
            return;
        }
        var (res, rej) = _promise.CreateResolvingFunctions();
        try
        {
            ReadOnlySpan<JsValue> args = [JsValue.FromObject(res), JsValue.FromObject(rej)];
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
    public override void Run(Realm realm, bool fulfilled, in JsValue argument) => _action(realm);
}

/// <summary>Promise instance (ECMA-262 27.2).</summary>
internal sealed class JsPromise : JsObject
{
    public PromiseState State;
    public bool IsHandled;
    public JsValue Result;
    /// <summary>While pending: the reactions, most recently added first.</summary>
    private PromiseReaction? _reactions;

    public JsPromise(Realm realm, JsObject? proto = null) : base(realm, proto ?? realm.PromisePrototype) { }

    public override string ClassName => "Promise";

    /// <summary>CreateResolvingFunctions (27.2.1.3).</summary>
    public (JsBuiltin resolve, JsBuiltin reject) CreateResolvingFunctions()
    {
        var (resolve, reject) = new PromiseCapability(this).Functions(Realm);
        return ((JsBuiltin)resolve.Ref!, (JsBuiltin)reject.Ref!);
    }

    /// <summary>Promise Resolve Functions steps 7-16.</summary>
    public void ResolveWith(JsValue resolution)
    {
        if (State != PromiseState.Pending) return;
        if (resolution.IsNotObject)
        {
            Fulfill(resolution);
            return;
        }
        var obj = resolution.AsObjectUnchecked;
        if (ReferenceEquals(obj, this))
        {
            RejectWith(JsValue.FromObject(JsError.Create(Realm, ErrorKind.TypeError, "Chaining cycle detected for promise")));
            return;
        }
        JsValue then;
        if (obj is JsPromise p && p.Realm.IsPristinePromise(p)) then = JsValue.FromObject(p.Realm.PromiseThenFunction);
        else
        {
            try
            {
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
        }
        Realm.Vm.EnqueueJob(new PromiseResolveThenableJob(this, resolution, then));
    }

    public void Fulfill(JsValue value)
    {
        if (State != PromiseState.Pending) return;
        var reactions = _reactions;
        Result = value;
        State = PromiseState.Fulfilled;
        if (reactions != null) Trigger(reactions, true, value);
    }

    public void RejectWith(JsValue reason)
    {
        if (State != PromiseState.Pending) return;
        var reactions = _reactions;
        Result = reason;
        State = PromiseState.Rejected;
        if (!IsHandled) Realm.TrackRejection(this, handled: false);
        if (reactions != null) Trigger(reactions, false, reason);
    }

    /// <summary>TriggerPromiseReactions: schedules the reactions in the order they were added.</summary>
    private void Trigger(PromiseReaction reactions, bool fulfilled, in JsValue value)
    {
        _reactions = null;
        PromiseReaction? ordered = null;
        for (PromiseReaction? r = reactions; r != null;)
        {
            var next = r.Next;
            r.Next = ordered;
            ordered = r;
            r = next;
        }
        var vm = Realm.Vm;
        while (ordered != null)
        {
            var next = ordered.Next;
            ordered.Next = null;
            vm.EnqueueJob(ordered, fulfilled, value);
            ordered = next;
        }
    }

    /// <summary>PerformPromiseThen (27.2.5.4.1).</summary>
    public void AddReaction(PromiseReaction reaction)
    {
        switch (State)
        {
            case PromiseState.Pending:
                reaction.Next = _reactions;
                _reactions = reaction;
                break;
            case PromiseState.Fulfilled:
                Realm.Vm.EnqueueJob(reaction, true, Result);
                break;
            default:
                if (!IsHandled) Realm.TrackRejection(this, handled: true);
                Realm.Vm.EnqueueJob(reaction, false, Result);
                break;
        }
        IsHandled = true;
    }

    public void Then(IPromiseContinuation continuation) => AddReaction(new ContinuationReaction(continuation));

    /// <summary>PromiseResolve(%Promise%, x).</summary>
    public static JsPromise Resolve(Realm realm, JsValue x)
    {
        if (x.IsObject && x.Ref is JsPromise p)
        {
            if (realm.IsPristinePromise(p)) return p;
            var ctor = p.Get("constructor");
            if (ctor.IsObject && ReferenceEquals(ctor.Ref, realm.PromiseConstructor)) return p;
        }
        var np = new JsPromise(realm);
        np.ResolveWith(x);
        return np;
    }

    /// <summary>Await's PromiseResolve + PerformPromiseThen. For a primitive the intermediate promise would be observed by no one.</summary>
    public static void Await(Realm realm, JsValue x, PromiseReaction reaction)
    {
        if (x.IsNotObject) realm.Vm.EnqueueJob(reaction, true, x);
        else Resolve(realm, x).AddReaction(reaction);
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
    private struct JobEntry
    {
        public Job Job;
        public JsValue Argument;
        public bool Fulfilled;
    }

    /// <summary>Ring buffer of pending jobs (the capacity is a power of two).</summary>
    private JobEntry[] _jobs = Array.Empty<JobEntry>();
    private int _jobHead;
    private int _jobCount;
    public int MaxPendingJobs = 1_000_000;

    public int PendingJobCount => _jobCount;

    public void EnqueueJob(Job job) => EnqueueJob(job, false, default);

    public void EnqueueJob(Job job, bool fulfilled, in JsValue argument)
    {
        int count = _jobCount;
        if (count >= MaxPendingJobs)
            throw new JsTerminationException(TerminationKind.PendingJobs, $"Pending job queue exceeded its limit of {MaxPendingJobs}.", count + 1, MaxPendingJobs);
        var jobs = _jobs;
        if (count == jobs.Length) jobs = GrowJobs();
        ref var e = ref jobs[(_jobHead + count) & (jobs.Length - 1)];
        e.Job = job;
        e.Argument = argument;
        e.Fulfilled = fulfilled;
        _jobCount = count + 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private JobEntry[] GrowJobs()
    {
        var old = _jobs;
        var jobs = new JobEntry[Math.Max(old.Length * 2, 16)];
        for (int i = 0; i < _jobCount; i++) jobs[i] = old[(_jobHead + i) & (old.Length - 1)];
        _jobHead = 0;
        return _jobs = jobs;
    }

    /// <summary>Runs ready microtasks until the queue is empty (each job is budgeted).</summary>
    public void RunJobs()
    {
        int n = 0;
        while (true)
        {
            while (_jobCount > 0)
            {
                // notified waiters, and (every 64 jobs) timed-out ones while a job loop keeps the queue busy
                if (_wakePending != 0 || (_asyncWaiters is { Count: > 0 } && (++n & 63) == 0)) ProcessAsyncWaiters();
                var jobs = _jobs;
                ref var e = ref jobs[_jobHead];
                var job = e.Job;
                var argument = e.Argument;
                bool fulfilled = e.Fulfilled;
                e = default;
                _jobHead = (_jobHead + 1) & (jobs.Length - 1);
                _jobCount--;
                Tick();
                job.Run(Realm, fulfilled, argument);
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

    public void ClearJobs()
    {
        _jobs = Array.Empty<JobEntry>();
        _jobHead = 0;
        _jobCount = 0;
    }

    /// <summary>An idle engine does not keep the buffer of a burst of jobs.</summary>
    private void TrimJobs()
    {
        if (_jobCount == 0 && _jobs.Length > 1 << 12) ClearJobs();
    }
}
