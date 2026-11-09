using System.Diagnostics;
using Jaunt.Runtime;

namespace Jaunt.Benchmarks;

public static class RealmProfile
{
    public static void Run()
    {
        const int n = 20000;
        for (int i = 0; i < 2000; i++) new Realm(false);
        Measure("new Realm (core)", n, () => new Realm(false));
        foreach (IntrinsicGroup g in Enum.GetValues<IntrinsicGroup>())
        {
            if (g == IntrinsicGroup.Count_ || g == IntrinsicGroup.Object || g == IntrinsicGroup.Function) continue;
            var gg = g;
            double baseT = Time(n, () => new Realm(false), out double baseA);
            double t = Time(n, () => new Realm(false).Ensure(gg), out double a);
            Console.WriteLine($"  + {g,-12} {t - baseT,8:F2} µs {(a - baseA) / 1024,8:F1} KB");
        }
    }

    private static double Time(int n, Action a, out double alloc)
    {
        for (int i = 0; i < n / 10; i++) a();
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++) a();
        alloc = (GC.GetAllocatedBytesForCurrentThread() - a0) / (double)n;
        return sw.Elapsed.TotalMicroseconds / n;
    }

    private static void Measure(string name, int n, Action a)
    {
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++) a();
        Console.WriteLine($"{name,-16} {sw.Elapsed.TotalMicroseconds / n,8:F2} µs {(GC.GetAllocatedBytesForCurrentThread() - a0) / (double)n / 1024,8:F1} KB");
    }
}

public static class CompileProfile
{
    public static void Run()
    {
        var src = string.Concat(Enumerable.Range(0, 40).Select(i => Workloads.Classes.Replace("Account", "Account" + i).Replace("Savings", "Savings" + i).Replace("function main", "function main" + i)));
        var plain = new Acornima.ParserOptions { EcmaVersion = Acornima.EcmaVersion.Latest };
        var tokenHooked = new Acornima.ParserOptions
        {
            EcmaVersion = Acornima.EcmaVersion.Latest,
            RegExpParseMode = Acornima.RegExpParseMode.Validate,
            OnToken = (in Acornima.Token _) => { System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack(); },
            OnNode = (Acornima.Ast.Node _, in Acornima.OnNodeContext _) => { },
        };
        for (int i = 0; i < 50; i++) { new Acornima.Parser(plain).ParseScript(src); JsCompiler.CompileScript(src); }
        Measure("parse (plain)", 200, () => new Acornima.Parser(plain).ParseScript(src));
        Measure("parse (hooks)", 200, () => new Acornima.Parser(tokenHooked).ParseScript(src));
        Measure("CompileScript", 200, () => JsCompiler.CompileScript(src));
        // allocation split (single run after warm-up)
        var program = new Acornima.Parser(plain).ParseScript(src);
        long b0 = GC.GetAllocatedBytesForCurrentThread();
        var analyzer = new Jaunt.Compiler.ScopeAnalyzer(null, 1000, null) { MayContainEval = false };
        analyzer.AnalyzeProgram(program, false);
        long b1 = GC.GetAllocatedBytesForCurrentThread();
        var script = new Jaunt.Compiler.ScriptTemplate { Body = null!, SourceName = "x", IsModule = false, Strict = false };
        var gen = new Jaunt.Compiler.CodeGen(script, src, analyzer, 1000, null);
        script.Body = gen.CompileProgram(program, false);
        long b2 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++)
        {
            new Jaunt.Compiler.ScopeAnalyzer(null, 1000, null) { MayContainEval = false }.AnalyzeProgram(program, false);
        }
        Console.WriteLine($"analyze          {sw.Elapsed.TotalMicroseconds / 200,8:F1} µs");
        sw.Restart();
        for (int i = 0; i < 200; i++)
        {
            var an = new Jaunt.Compiler.ScopeAnalyzer(null, 1000, null) { MayContainEval = false };
            an.AnalyzeProgram(program, false);
            var st = new Jaunt.Compiler.ScriptTemplate { Body = null!, SourceName = "x", IsModule = false, Strict = false };
            st.Body = new Jaunt.Compiler.CodeGen(st, src, an, 1000, null).CompileProgram(program, false);
        }
        if (Environment.GetEnvironmentVariable("JAUNT_ALLOC") == "1")
        {
            using var sampler = new AllocSampler();
            Thread.Sleep(200);
            sampler.On = true;
            for (int i = 0; i < 300; i++) JsCompiler.CompileScript(src);
            sampler.On = false;
            sampler.Print();
        }
        Console.WriteLine($"analyze+codegen  {sw.Elapsed.TotalMicroseconds / 200,8:F1} µs");
        Console.WriteLine($"analyze alloc {(b1 - b0) / 1024.0,8:F1} KB   codegen alloc {(b2 - b1) / 1024.0,8:F1} KB");
    }

    private static void Measure(string name, int n, Action a)
    {
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++) a();
        Console.WriteLine($"{name,-16} {sw.Elapsed.TotalMicroseconds / n,8:F1} µs {(GC.GetAllocatedBytesForCurrentThread() - a0) / (double)n / 1024,8:F1} KB");
    }
}

/// <summary>Samples GC AllocationTick events (~every 100 KB) to attribute allocations to types.</summary>
public sealed class AllocSampler : System.Diagnostics.Tracing.EventListener
{
    public readonly Dictionary<string, long> Counts = new();
    public bool On;
    protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource s)
    {
        if (s.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(s, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1);
    }
    protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
    {
        if (!On || e.EventName == null || !e.EventName.StartsWith("GCAllocationTick")) return;
        int i = e.PayloadNames!.IndexOf("TypeName");
        var t = i >= 0 ? e.Payload![i] as string ?? "?" : "?";
        lock (Counts) Counts[t] = Counts.GetValueOrDefault(t) + 1;
    }
    public void Print(int top = 25)
    {
        long total = Counts.Values.Sum();
        foreach (var kv in Counts.OrderByDescending(k => k.Value).Take(top))
            Console.WriteLine($"{kv.Value * 100.0 / total,6:F1}%  {kv.Key}");
    }
}
