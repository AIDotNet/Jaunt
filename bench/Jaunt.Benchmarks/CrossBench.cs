using System.Diagnostics;
using System.Text.Json;
using Jaunt;

namespace Jaunt.Benchmarks;

/// <summary>
/// Cross-engine harness: runs the shared <c>bench/cross/workloads/**/*.js</c> corpus on Jaunt and Jint and emits one JSON
/// line per (engine, workload). The Go runner (<c>bench/cross/moejs-runner</c>) uses the identical protocol for moejs.
/// Protocol: compile (median) -> cold (new engine + load + first call, verified against Node's output) ->
/// warm-up -> N samples of k calls each (a sample is ~target ms) -> report median/min per call and bytes allocated per call.
/// </summary>
public static class CrossBench
{
    private interface IRunner : IDisposable { string Call(); }

    private sealed class JauntRunner : IRunner
    {
        private readonly JsEngine _e = new(); private readonly JsFunction _main; private readonly JsFunction? _result;
        public JauntRunner(JsScript script) { _e.Evaluate(script); _main = _e.GetFunction("main"); try { _result = _e.GetFunction("result"); } catch { } }
        public string Call() { var r = _e.Invoke(_main); if (_result is not null) r = _e.Invoke(_result); return r.ToString(); }
        public void Dispose() => _e.Dispose();
    }

    private sealed class JintRunner : IRunner
    {
        private readonly Jint.Engine _e = new(); private readonly bool _hasResult;
        public JintRunner(Jint.Prepared<Acornima.Ast.Script> script) { _e.Execute(script); _hasResult = _e.GetValue("result").Type == Jint.Runtime.Types.Object; }
        public string Call() { var r = _e.Invoke("main"); _e.Advanced.ProcessTasks(); if (_hasResult) r = _e.Invoke("result"); return r.ToString(); }
        public void Dispose() => _e.Dispose();
    }

    public static int Run(string[] args)
    {
        string dir = args[0], expectedPath = args[1];
        string[] engines = Opt(args, "--engines", "jaunt,jint").Split(',');
        string filter = Opt(args, "--filter", "");
        double targetMs = double.Parse(Opt(args, "--target-ms", "60"));
        int samples = int.Parse(Opt(args, "--samples", "7"));
        double warmMs = double.Parse(Opt(args, "--warm-ms", "800"));
        double warmMaxMs = double.Parse(Opt(args, "--warm-max-ms", "4000"));
        string? outPath = Opt(args, "--json", null!);
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(expectedPath))!;
        var files = Directory.GetFiles(dir, "*.js", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();
        using var writer = outPath is null ? null : new StreamWriter(outPath, false);
        foreach (var engine in engines)
        {
            foreach (var file in files)
            {
                string name = Path.ChangeExtension(Path.GetRelativePath(dir, file), null)!.Replace('\\', '/');
                if (filter.Length > 0 && !name.Contains(filter)) continue;
                string src = File.ReadAllText(file);
                var rec = Measure(engine, name, src, expected.GetValueOrDefault(name), targetMs, samples, warmMs, warmMaxMs);
                string line = JsonSerializer.Serialize(rec);
                writer?.WriteLine(line); writer?.Flush();
                Console.WriteLine($"{engine,-6} {name,-34} {rec["status"],-9} {(rec.TryGetValue("medianUs", out var m) ? $"{m:F1} µs" : "")}");
            }
        }
        return 0;
    }

    private static string Opt(string[] a, string key, string def) { int i = Array.IndexOf(a, key); return i >= 0 && i + 1 < a.Length ? a[i + 1] : def; }

    private static Dictionary<string, object> Measure(string engine, string name, string src, string? expected, double targetMs, int samples, double warmMs, double warmMaxMs)
    {
        var rec = new Dictionary<string, object> { ["engine"] = engine, ["name"] = name };
        try
        {
            Func<IRunner> make; double compileUs;
            if (engine == "jaunt")
            {
                var s = JsCompiler.CompileScript(src);
                compileUs = TimeMedian(() => JsCompiler.CompileScript(src));
                make = () => new JauntRunner(s);
            }
            else
            {
                var s = Jint.Engine.PrepareScript(src);
                compileUs = TimeMedian(() => Jint.Engine.PrepareScript(src));
                make = () => new JintRunner(s);
            }
            rec["compileUs"] = compileUs;
            // cold: fresh engine + load + first call
            var sw = Stopwatch.StartNew();
            using var r = make();
            string first = r.Call();
            rec["coldUs"] = sw.Elapsed.TotalMicroseconds;
            rec["result"] = first;
            rec["ok"] = expected is null || first == expected;
            // Warm-up until the JIT has settled. .NET tiers code up (tier-0, instrumented, tier-1) behind timers of ~100 ms, so a
            // fixed short warm-up measures half-optimized code. Run batches of ~targetMs until at least warmMs have passed and
            // the best of the last three batches is within 4% of the best of the three before.
            var warm = Stopwatch.StartNew(); var batches = new List<double>(); int k = 1;
            while (true)
            {
                var bs = Stopwatch.StartNew();
                for (int j = 0; j < k; j++) r.Call();
                double t = bs.Elapsed.TotalMilliseconds / k;
                batches.Add(t);
                k = Math.Max(1, (int)(targetMs / Math.Max(t, 0.001)));
                if (warm.Elapsed.TotalMilliseconds >= warmMaxMs) break;
                if (warm.Elapsed.TotalMilliseconds >= warmMs && batches.Count >= 6)
                {
                    double last = batches.TakeLast(3).Min(), prev = batches.Skip(batches.Count - 6).Take(3).Min();
                    if (Math.Abs(last - prev) <= 0.04 * prev) break;
                }
            }
            var per = new double[samples]; var cpu = new double[samples]; long alloc = 0;
            var proc = Process.GetCurrentProcess();
            for (int i = 0; i < samples; i++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers();
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                proc.Refresh(); var c0 = proc.TotalProcessorTime;
                sw.Restart();
                for (int j = 0; j < k; j++) r.Call();
                per[i] = sw.Elapsed.TotalMicroseconds / k;
                proc.Refresh(); cpu[i] = (proc.TotalProcessorTime - c0).TotalMicroseconds / k;
                alloc += GC.GetAllocatedBytesForCurrentThread() - a0;
            }
            Array.Sort(per); Array.Sort(cpu);
            rec["cpuUs"] = cpu[samples / 2];
            rec["medianUs"] = per[samples / 2]; rec["minUs"] = per[0]; rec["allocB"] = alloc / ((long)samples * k); rec["inner"] = k;
            rec["status"] = (bool)rec["ok"] ? "ok" : "mismatch";
        }
        catch (Exception ex)
        {
            rec["status"] = "error"; rec["error"] = ex.GetType().Name + ": " + ex.Message.Split('\n')[0];
        }
        return rec;
    }

    private static double TimeMedian(Action a)
    {
        var xs = new List<double>(); var total = Stopwatch.StartNew(); var sw = new Stopwatch();
        a();
        while (xs.Count < 5 || (xs.Count < 200 && total.ElapsedMilliseconds < 300)) { sw.Restart(); a(); xs.Add(sw.Elapsed.TotalMicroseconds); }
        xs.Sort(); return xs[xs.Count / 2];
    }
}
