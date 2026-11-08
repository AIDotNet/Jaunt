using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Jaunt;

namespace Jaunt.CrossBench;

/// <summary>
/// The cross-engine measurement protocol, shared (as a linked source file) by <c>bench/Jaunt.Benchmarks --cross</c> and
/// <c>samples/Jaunt.AotHost bench</c>, so the CoreCLR harness and the NativeAOT host measure the same thing the same way:
/// compile (median) -> cold (new engine + load + first call, checked against Node's output) -> warm-up until the tiers settle
/// -> N samples of k calls each (a sample is ~target ms) -> median/min wall time, median CPU time and bytes allocated per call.
/// The Go runner (<c>bench/cross/moejs-runner</c>) implements the identical protocol for moejs. Everything here is reflection-free
/// (the AOT host builds with IL trimming and AOT warnings as errors).
/// </summary>
public sealed class CrossOptions
{
    public string Filter = "";
    public double TargetMs = 60;
    public int Samples = 7;
    public double WarmMs = 800;
    public double WarmMaxMs = 4000;
    public string? JsonPath;

    public static CrossOptions Parse(string[] args) => new()
    {
        Filter = Opt(args, "--filter", "")!,
        TargetMs = double.Parse(Opt(args, "--target-ms", "60")!, System.Globalization.CultureInfo.InvariantCulture),
        Samples = int.Parse(Opt(args, "--samples", "7")!, System.Globalization.CultureInfo.InvariantCulture),
        WarmMs = double.Parse(Opt(args, "--warm-ms", "800")!, System.Globalization.CultureInfo.InvariantCulture),
        WarmMaxMs = double.Parse(Opt(args, "--warm-max-ms", "4000")!, System.Globalization.CultureInfo.InvariantCulture),
        JsonPath = Opt(args, "--json", null),
    };

    public static string? Opt(string[] a, string key, string? def) { int i = Array.IndexOf(a, key); return i >= 0 && i + 1 < a.Length ? a[i + 1] : def; }
}

public interface ICrossRunner : IDisposable { string Call(); }

/// <summary>Jaunt under the protocol: one engine per workload, <c>main()</c> per call, then <c>result()</c> when the workload defines it.</summary>
public sealed class JauntCrossRunner : ICrossRunner
{
    private readonly JsEngine _e = new(); private readonly JsFunction _main; private readonly JsFunction? _result;
    public JauntCrossRunner(JsScript script) { _e.Evaluate(script); _main = _e.GetFunction("main"); try { _result = _e.GetFunction("result"); } catch { } }
    public string Call() { var r = _e.Invoke(_main); if (_result is not null) r = _e.Invoke(_result); return r.ToString(); }
    public void Dispose() => _e.Dispose();
}

public static class CrossProtocol
{
    /// <summary>Workloads under <paramref name="dir"/> as (name, path), name = relative path without extension, ordinal order.</summary>
    public static IEnumerable<(string Name, string Path)> Workloads(string dir, string filter)
    {
        foreach (var file in Directory.GetFiles(dir, "*.js", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.ChangeExtension(Path.GetRelativePath(dir, file), null)!.Replace('\\', '/');
            if (filter.Length == 0 || name.Contains(filter)) yield return (name, file);
        }
    }

    /// <summary>Reads <c>expected.json</c> (workload name -> Node's result string) without reflection.</summary>
    public static Dictionary<string, string> ReadExpected(string path)
    {
        var map = new Dictionary<string, string>();
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var p in doc.RootElement.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.String) map[p.Name] = p.Value.GetString()!;
        return map;
    }

    /// <summary>
    /// Runs the protocol for one workload and fills <paramref name="rec"/> (compileUs, coldUs, result, ok, medianUs, minUs, cpuUs,
    /// allocB, inner, status or error). <paramref name="compile"/> compiles the source and returns the runner factory; it is called again
    /// repeatedly to time compilation.
    /// </summary>
    public static void Measure(Dictionary<string, object> rec, Func<Func<ICrossRunner>> compile, string? expected, CrossOptions o)
    {
        try
        {
            var make = compile();
            rec["compileUs"] = TimeMedianUs(() => compile());
            // cold: fresh engine + load + first call
            var sw = Stopwatch.StartNew();
            using var r = make();
            string first = r.Call();
            rec["coldUs"] = sw.Elapsed.TotalMicroseconds;
            rec["result"] = first;
            bool ok = expected is null || first == expected;
            rec["ok"] = ok;
            // Warm-up until the JIT has settled. .NET tiers code up (tier-0, instrumented, tier-1) behind timers of ~100 ms, so a
            // fixed short warm-up measures half-optimized code. Run batches of ~targetMs until at least warmMs have passed and
            // the best of the last three batches is within 4% of the best of the three before. (NativeAOT has no tiers; the same
            // loop then simply runs for warmMs, which keeps the protocol identical.)
            var warm = Stopwatch.StartNew(); var batches = new List<double>(); int k = 1;
            while (true)
            {
                var bs = Stopwatch.StartNew();
                for (int j = 0; j < k; j++) r.Call();
                double t = bs.Elapsed.TotalMilliseconds / k;
                batches.Add(t);
                k = Math.Max(1, (int)(o.TargetMs / Math.Max(t, 0.001)));
                if (warm.Elapsed.TotalMilliseconds >= o.WarmMaxMs) break;
                if (warm.Elapsed.TotalMilliseconds >= o.WarmMs && batches.Count >= 6)
                {
                    double last = batches.TakeLast(3).Min(), prev = batches.Skip(batches.Count - 6).Take(3).Min();
                    if (Math.Abs(last - prev) <= 0.04 * prev) break;
                }
            }
            int samples = o.Samples;
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
            rec["status"] = ok ? "ok" : "mismatch";
        }
        catch (Exception ex)
        {
            rec["status"] = "error"; rec["error"] = ex.GetType().Name + ": " + ex.Message.Split('\n')[0];
        }
    }

    public static double TimeMedianUs(Action a)
    {
        var xs = new List<double>(); var total = Stopwatch.StartNew(); var sw = new Stopwatch();
        a();
        while (xs.Count < 5 || (xs.Count < 200 && total.ElapsedMilliseconds < 300)) { sw.Restart(); a(); xs.Add(sw.Elapsed.TotalMicroseconds); }
        xs.Sort(); return xs[xs.Count / 2];
    }

    /// <summary>One JSON line per record (the <c>.jsonl</c> that <c>bench/cross/report.py</c> and <c>report_aot.py</c> read), reflection-free.</summary>
    public static string ToJsonLine(Dictionary<string, object> rec)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var (key, v) in rec)
            {
                switch (v)
                {
                    case string s: w.WriteString(key, s); break;
                    case bool b: w.WriteBoolean(key, b); break;
                    case int i: w.WriteNumber(key, i); break;
                    case long l: w.WriteNumber(key, l); break;
                    case double d: w.WriteNumber(key, d); break;
                    default: w.WriteString(key, v.ToString()); break;
                }
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string ConsoleLine(Dictionary<string, object> rec)
    {
        string status = (string)rec["status"];
        string timing = rec.TryGetValue("medianUs", out var m)
            ? $"{(double)m,10:F1} µs  cpu {(double)rec["cpuUs"],10:F1} µs  {(long)rec["allocB"],10} B/call"
            : rec.TryGetValue("error", out var e) ? e.ToString()! : "";
        return $"{rec["engine"],-10} {rec["name"],-34} {status,-9} {timing}";
    }
}
