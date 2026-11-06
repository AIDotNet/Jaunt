using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Jaunt.Runtime;

/// <summary>
/// Shared Data Block of a SharedArrayBuffer (25.2): the storage that every agent's SharedArrayBuffer object for the
/// same memory refers to, plus its WaiterList. One lock per block is the WaiterList critical section and also
/// serializes Atomics operations on the block, which makes them sequentially consistent across agents (plain
/// TypedArray reads and writes stay unsynchronized, as the memory model allows).
/// </summary>
internal sealed class SharedBlock
{
    public readonly object Lock = new();
    /// <summary>Current storage (replaced on grow); views copy this reference.</summary>
    public byte[] Data;
    public readonly int MaxByteLength;
    /// <summary>True once a SharedArrayBuffer for this block exists in more than one agent.</summary>
    public volatile bool CrossAgent;
    /// <summary>Waiters in FIFO order (25.4.3.x WaiterList), guarded by <see cref="Lock"/>.</summary>
    public readonly List<AtomicsWaiter> Waiters = new();
    private readonly List<WeakReference<JsArrayBuffer>> _views = new();

    public SharedBlock(byte[] data, int maxByteLength)
    {
        Data = data;
        MaxByteLength = maxByteLength;
    }

    public void AddView(JsArrayBuffer view)
    {
        lock (Lock) _views.Add(new WeakReference<JsArrayBuffer>(view));
    }

    /// <summary>Grows the block (SharedArrayBuffer.prototype.grow) and repoints every agent's view.</summary>
    public void Grow(int newLength)
    {
        lock (Lock)
        {
            if (newLength <= Data.Length) return;
            var n = new byte[newLength];
            Buffer.BlockCopy(Data, 0, n, 0, Data.Length);
            Data = n;
            for (int i = _views.Count - 1; i >= 0; i--)
            {
                if (_views[i].TryGetTarget(out var v)) v.Data = n;
                else _views.RemoveAt(i);
            }
        }
    }
}

/// <summary>A waiter record: a blocked agent (Atomics.wait) or a pending Atomics.waitAsync promise.</summary>
internal sealed class AtomicsWaiter
{
    public required SharedBlock Block;
    public required int ByteIndex;
    /// <summary>Owning Vm of an async waiter (null for a synchronous, blocked waiter).</summary>
    public Vm? Owner;
    public JsPromise? Promise;
    /// <summary>Stopwatch timestamp; long.MaxValue = no timeout.</summary>
    public long Deadline = long.MaxValue;
    /// <summary>Set (under the block lock) when Atomics.notify removed this waiter.</summary>
    public volatile bool Notified;
}

internal static class AtomicsWaiting
{
    public static long DeadlineFrom(double timeoutMs) =>
        double.IsPositiveInfinity(timeoutMs) || timeoutMs > 1e12
            ? long.MaxValue
            : Stopwatch.GetTimestamp() + (long)(timeoutMs / 1000.0 * Stopwatch.Frequency);

    /// <summary>
    /// DoWait, synchronous mode: the caller holds <c>block.Lock</c> and has checked the value. Blocks the thread until
    /// notified or timed out, polling the Vm's budget (timeout, cancellation, interrupt) so a parked agent can still
    /// be terminated.
    /// </summary>
    public static bool BlockingWait(Vm vm, SharedBlock block, int byteIndex, double timeoutMs)
    {
        var w = new AtomicsWaiter { Block = block, ByteIndex = byteIndex, Deadline = DeadlineFrom(timeoutMs) };
        block.Waiters.Add(w);
        try
        {
            while (!w.Notified)
            {
                long remaining = w.Deadline == long.MaxValue ? long.MaxValue : w.Deadline - Stopwatch.GetTimestamp();
                if (remaining <= 0) break;
                int ms = remaining == long.MaxValue ? 20 : (int)Math.Clamp(remaining * 1000 / Stopwatch.Frequency + 1, 1, 20);
                Monitor.Wait(block.Lock, ms);
                if (!w.Notified) vm.Checkpoint();
            }
            return w.Notified;
        }
        finally
        {
            if (!w.Notified) block.Waiters.Remove(w);
        }
    }

    /// <summary>DoWait, asynchronous mode: registers a waiter (caller holds the block lock).</summary>
    public static AtomicsWaiter AddAsync(Vm vm, Realm realm, SharedBlock block, int byteIndex, double timeoutMs)
    {
        var w = new AtomicsWaiter { Block = block, ByteIndex = byteIndex, Owner = vm, Promise = new JsPromise(realm), Deadline = DeadlineFrom(timeoutMs) };
        block.Waiters.Add(w);
        vm.AddAsyncWaiter(w);
        return w;
    }

    /// <summary>Atomics.notify: removes up to <paramref name="count"/> waiters on the location in FIFO order.</summary>
    public static int Notify(Vm current, SharedBlock block, int byteIndex, double count)
    {
        int n = 0;
        bool pulse = false;
        lock (block.Lock)
        {
            var list = block.Waiters;
            long now = Stopwatch.GetTimestamp();
            for (int i = 0; i < list.Count && n < count;)
            {
                var w = list[i];
                // an async waiter whose timeout elapsed is already timed out (its agent resolves it)
                if (w.ByteIndex != byteIndex || (w.Owner != null && w.Deadline <= now)) { i++; continue; }
                list.RemoveAt(i);
                w.Notified = true;
                n++;
                if (w.Owner == null) pulse = true;
                else if (!ReferenceEquals(w.Owner, current)) w.Owner.SignalWake();
            }
            if (pulse) Monitor.PulseAll(block.Lock);
        }
        // waiters of this agent are resolved now (their reactions run as jobs)
        if (n > 0) current.ProcessAsyncWaiters();
        return n;
    }
}
