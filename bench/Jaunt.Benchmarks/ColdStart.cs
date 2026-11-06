using System.Diagnostics;

namespace Jaunt.Benchmarks;

/// <summary>
/// `--cold [runs]`: what the first engine in a process costs compared with every later one (research doc §3.5: a "new
/// runtime" figure is not a cold start, and process-wide shared costs must not disappear from the denominator).
/// Each run is a fresh child process (`--cold-child`), so type loading, JIT and Jaunt's process-wide shared state
/// (realm-independent shapes, predicted builtin layouts, Unicode/case tables touched by the script) are all paid by
/// engine #1; engines #2…#N reuse them. Reported: wall time and allocation of engine #1 vs the mean of #2…#N, and the
/// managed memory still held by the process once every engine is gone (= shared state).
/// </summary>
public static class ColdStart
{
    private const int Engines = 50;

    public static void Run(string[] args)
    {
        int runs = args.Length > 0 ? int.Parse(args[0]) : 10;
        var self = Environment.ProcessPath!;
        var dll = typeof(ColdStart).Assembly.Location;
        var rows = new List<double[]>();
        for (int r = 0; r < runs; r++)
        {
            var psi = new ProcessStartInfo(self) { RedirectStandardOutput = true, UseShellExecute = false };
            if (Path.GetFileNameWithoutExtension(self) == "dotnet") psi.ArgumentList.Add(dll);
            psi.ArgumentList.Add("--cold-child");
            using var p = Process.Start(psi)!;
            var line = p.StandardOutput.ReadToEnd().Trim().Split('\n')[^1];
            p.WaitForExit();
            rows.Add(line.Split(' ').Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray());
        }
        double Med(int i) { var v = rows.Select(x => x[i]).OrderBy(x => x).ToArray(); return v[v.Length / 2]; }
        Console.WriteLine($"{runs} fresh processes (CoreCLR {Environment.Version}), medians; each engine evaluates `1` then the `classes` workload");
        Console.WriteLine($"process start → Main:                         {Med(0),9:F1} ms");
        Console.WriteLine($"engine #1 (cold: type load, JIT, shared state): {Med(1),9:F1} ms   {Med(2),9:F1} KB allocated");
        Console.WriteLine($"engine #2..#{Engines} mean (warm):                   {Med(3),9:F3} ms   {Med(4),9:F1} KB allocated");
        Console.WriteLine($"managed memory held after all engines are gone (process-wide shared state): {Med(5),7:F1} KB");
    }

    public static void Child()
    {
        double startToMain = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        long baseline = GC.GetTotalMemory(true);
        var src = Workloads.Classes + "\nmain(1)";
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        using (var e = new JsEngine()) { e.Evaluate("1"); e.Evaluate(src); }
        double first = sw.Elapsed.TotalMilliseconds;
        double firstKb = (GC.GetAllocatedBytesForCurrentThread() - a0) / 1024.0;
        long a1 = GC.GetAllocatedBytesForCurrentThread();
        sw.Restart();
        for (int i = 1; i < Engines; i++) { using var e = new JsEngine(); e.Evaluate("1"); e.Evaluate(src); }
        double rest = sw.Elapsed.TotalMilliseconds / (Engines - 1);
        double restKb = (GC.GetAllocatedBytesForCurrentThread() - a1) / 1024.0 / (Engines - 1);
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        double sharedKb = (GC.GetTotalMemory(true) - baseline) / 1024.0;
        Console.WriteLine(string.Join(' ', new[] { startToMain, first, firstKb, rest, restKb, sharedKb }
            .Select(v => v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))));
    }
}
