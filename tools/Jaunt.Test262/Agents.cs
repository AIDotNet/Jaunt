using System.Collections.Concurrent;
using System.Diagnostics;
using Jaunt;

/// <summary>
/// test262 <c>$262.agent</c> (INTERPRETING.md): each started agent is a JsEngine with [[CanBlock]] true on its own
/// thread; <c>broadcast</c> hands the SharedArrayBuffer's memory to every agent (JsSharedMemory) and blocks until all
/// of them have received it.
/// </summary>
sealed class AgentHost : IDisposable
{
    private static readonly long s_t0 = Stopwatch.GetTimestamp();
    private readonly List<Agent> _agents = new();
    private readonly ConcurrentQueue<string> _reports = new();
    private readonly CancellationTokenSource _cts = new();

    private sealed class Agent
    {
        public readonly BlockingCollection<JsSharedMemory> Inbox = new();
        public readonly SemaphoreSlim Received = new(0);
        public JsSharedMemory? Current;
        public volatile bool Done;
        public Thread? Thread;
    }

    public static double MonotonicNow() => Stopwatch.GetElapsedTime(s_t0).TotalMilliseconds;

    /// <summary>Installs the main thread's agent functions (used by the $262 bootstrap script).</summary>
    public void Install(JsEngine engine)
    {
        engine.SetFunction("__jauntAgentStart", (ref JsCallContext c) => { Start(c.GetString(0) ?? ""); return JsValue.Undefined; }, 1);
        engine.SetFunction("__jauntAgentBroadcast", (ref JsCallContext c) => { Broadcast(c.GetSharedMemory(c[0])); return JsValue.Undefined; }, 1);
        engine.SetFunction("__jauntAgentGetReport", (ref JsCallContext c) => _reports.TryDequeue(out var r) ? JsValue.From(r) : JsValue.Null, 0);
        InstallCommon(engine);
    }

    private static void InstallCommon(JsEngine engine)
    {
        engine.SetFunction("__jauntAgentSleep", static (ref JsCallContext c) =>
        {
            double ms = c.GetNumber(0);
            Thread.Sleep(double.IsFinite(ms) && ms > 0 ? (int)Math.Min(ms, 60_000) : 0);
            return JsValue.Undefined;
        }, 1);
        engine.SetFunction("__jauntMonotonicNow", static (ref JsCallContext c) => JsValue.From(MonotonicNow()), 0);
    }

    private void Start(string source)
    {
        var a = new Agent();
        a.Thread = new Thread(() => RunAgent(a, source), 16 * 1024 * 1024) { IsBackground = true, Name = "test262-agent" };
        lock (_agents) _agents.Add(a);
        a.Thread.Start();
    }

    private void Broadcast(JsSharedMemory memory)
    {
        Agent[] agents;
        lock (_agents) agents = _agents.ToArray();
        foreach (var a in agents) if (!a.Done) a.Inbox.Add(memory);
        foreach (var a in agents)
            if (!a.Done) a.Received.Wait(TimeSpan.FromSeconds(10), _cts.Token);
    }

    private void RunAgent(Agent a, string source)
    {
        bool leaving = false;
        try
        {
            using var engine = new JsEngine(new JsEngineOptions
            {
                CanBlock = true,
                Limits = new JsLimits { Timeout = TimeSpan.FromSeconds(20), MaxStackDepth = 3000 },
            });
            InstallCommon(engine);
            engine.SetFunction("__jauntAgentReport", (ref JsCallContext c) => { _reports.Enqueue(c.GetString(0) ?? "null"); return JsValue.Undefined; }, 1);
            engine.SetFunction("__jauntAgentLeaving", (ref JsCallContext c) => { leaving = true; return JsValue.Undefined; }, 0);
            engine.SetFunction("__jauntAgentTake", (ref JsCallContext c) => c.CreateSharedArrayBuffer(a.Current!), 0);
            engine.Evaluate("""
                var $262 = {
                  global: globalThis,
                  agent: {
                    receiveBroadcast: function (cb) { globalThis.__jauntAgentCallback = cb; },
                    report: function (v) { __jauntAgentReport(String(v)); },
                    leaving: function () { __jauntAgentLeaving(); },
                    sleep: function (ms) { __jauntAgentSleep(ms); },
                    monotonicNow: function () { return __jauntMonotonicNow(); },
                  },
                };
                """);
            engine.Evaluate(source, _cts.Token);
            while (!leaving && !_cts.IsCancellationRequested)
            {
                a.Current = a.Inbox.Take(_cts.Token);
                a.Received.Release();
                // a pending waitAsync keeps the execution alive until it settles (CanBlock agent)
                engine.Evaluate("__jauntAgentCallback(__jauntAgentTake(), 0)", _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!_cts.IsCancellationRequested) { _reports.Enqueue("agent error: " + ex.Message); }
        catch (Exception) { }
        finally
        {
            a.Done = true;
            a.Received.Release();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        Agent[] agents;
        lock (_agents) agents = _agents.ToArray();
        foreach (var a in agents) a.Thread?.Join(TimeSpan.FromSeconds(5));
    }
}
