using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using Microsoft.ClearScript.V8.FastProxy;
using OkojoJs = Okojo.JavaScript;
using OkojoEmbedding = Okojo.JavaScript.Embedding;
using OkojoExec = Okojo.JavaScript.Execution;

namespace Jaunt.Benchmarks;

// Cross-engine comparisons required by design/01-research.md §1/§2/§4: Jint (stable 4.17.0, prepared scripts),
// Okojo (0.2.0-preview.2 — the snapshot the research reviewed; prerelease, .NET 10+) and ClearScript/V8 7.5.1.1
// (native V8: its convenience host binding and its FastProxy binding are measured separately, and its native heap is
// reported separately from the managed heap). Same JS source for every engine; each engine is used the way its
// documentation recommends for repeated calls (prepared/compiled once, function handle cached).

internal static class EngineSources
{
    public static string For(string workload) => workload switch
    {
        "numeric-loop" => Workloads.NumericLoop,
        "fib" => Workloads.Fib,
        "objects" => Workloads.Objects,
        "arrays" => Workloads.Arrays,
        "strings" => Workloads.Strings,
        "classes" => Workloads.Classes,
        _ => Workloads.Json,
    };

    public const string HostCalls = """
        function main(n) { let s = 0; for (let i = 0; i < 10000; i++) s = add(s, i) % 1000003; return s; }
        """;
}

/// <summary>Warm invocation of `main(1)` for each workload on Jaunt, Jint, Okojo and ClearScript/V8.</summary>
[MemoryDiagnoser]
public class EngineWarmInvocation
{
    [Params("numeric-loop", "fib", "objects", "arrays", "strings", "classes", "json")]
    public string Workload { get; set; } = "";

    private JsEngine _jaunt = null!;
    private JsFunction _jauntMain = null!;
    private global::Jint.Engine _jint = null!;
    private global::Jint.Native.JsValue _jintMain = null!;
    private OkojoEmbedding.JsRuntime _okojo = null!;
    private OkojoExec.JsRealm _okojoRealm = null!;
    private OkojoJs.JsValue _okojoMain;
    private V8ScriptEngine _v8 = null!;
    private ScriptObject _v8Main = null!;

    [GlobalSetup]
    public void Setup()
    {
        var src = EngineSources.For(Workload);
        _jaunt = new JsEngine();
        _jaunt.Evaluate(JsCompiler.CompileScript(src));
        _jauntMain = _jaunt.GetFunction("main");
        _jint = new global::Jint.Engine();
        _jint.Execute(global::Jint.Engine.PrepareScript(src));
        _jintMain = _jint.GetValue("main");
        _okojo = OkojoEmbedding.JsRuntime.Create();
        _okojoRealm = _okojo.MainRealm;
        _okojoRealm.Execute(src);
        _okojoMain = _okojoRealm.Eval("main");
        _v8 = new V8ScriptEngine();
        _v8.Execute(src);
        _v8Main = (ScriptObject)_v8.Script.main;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _jaunt.Dispose();
        _jint.Dispose();
        _okojo.Dispose();
        _v8.Dispose();
    }

    [Benchmark(Baseline = true)]
    public object Jint() => _jint.Invoke(_jintMain, 1);

    [Benchmark]
    public JsValue Jaunt() => _jaunt.Invoke(_jauntMain, JsValue.From(1));

    [Benchmark]
    public OkojoJs.JsValue Okojo() => _okojoRealm.Call(_okojoMain, OkojoJs.JsValue.Undefined, [1]);

    [Benchmark]
    public object ClearScriptV8() => _v8Main.InvokeAsFunction(1);
}

/// <summary>
/// JS → host calls: `main` calls a host `add(a, b)` 10,000 times. ClearScript is measured with its convenience binding
/// (a delegate via AddHostObject) and with its FastProxy binding (V8FastHostFunction).
/// </summary>
[MemoryDiagnoser]
public class EngineHostCalls
{
    private JsEngine _jauntTyped = null!, _jauntRaw = null!;
    private JsFunction _jauntTypedMain = null!, _jauntRawMain = null!;
    private global::Jint.Engine _jint = null!;
    private global::Jint.Native.JsValue _jintMain = null!;
    private OkojoEmbedding.JsRuntime _okojo = null!;
    private OkojoExec.JsRealm _okojoRealm = null!;
    private OkojoJs.JsValue _okojoMain;
    private V8ScriptEngine _v8 = null!, _v8Fast = null!;
    private ScriptObject _v8Main = null!, _v8FastMain = null!;

    [GlobalSetup]
    public void Setup()
    {
        var src = EngineSources.HostCalls;
        _jauntTyped = new JsEngine();
        _jauntTyped.SetFunction("add", (double a, double b) => a + b);
        _jauntTyped.Evaluate(src);
        _jauntTypedMain = _jauntTyped.GetFunction("main");
        _jauntRaw = new JsEngine();
        _jauntRaw.SetFunction("add", static (ref JsCallContext c) => JsValue.From(c.GetNumber(0) + c.GetNumber(1)), 2);
        _jauntRaw.Evaluate(src);
        _jauntRawMain = _jauntRaw.GetFunction("main");
        _jint = new global::Jint.Engine();
        _jint.SetValue("add", new Func<double, double, double>((a, b) => a + b));
        _jint.Execute(src);
        _jintMain = _jint.GetValue("main");
        _okojo = OkojoEmbedding.JsRuntime.Create();
        _okojoRealm = _okojo.MainRealm;
        _okojoRealm.InstallGlobals(g => g.Function("add", 2,
            static info => new OkojoJs.JsValue(info.GetArgumentDouble(0) + info.GetArgumentDouble(1)), false));
        _okojoRealm.Execute(src);
        _okojoMain = _okojoRealm.Eval("main");
        _v8 = new V8ScriptEngine();
        _v8.AddHostObject("add", new Func<double, double, double>((a, b) => a + b));
        _v8.Execute(src);
        _v8Main = (ScriptObject)_v8.Script.main;
        _v8Fast = new V8ScriptEngine();
        _v8Fast.AddHostObject("add", new V8FastHostFunction("add", 2, static (bool asConstructor, in V8FastArgs args, in V8FastResult result) =>
        {
            args.TryGet(0, out double a);
            args.TryGet(1, out double b);
            result.Set(a + b);
        }));
        _v8Fast.Execute(src);
        _v8FastMain = (ScriptObject)_v8Fast.Script.main;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _jauntTyped.Dispose();
        _jauntRaw.Dispose();
        _jint.Dispose();
        _okojo.Dispose();
        _v8.Dispose();
        _v8Fast.Dispose();
    }

    [Benchmark(Baseline = true)]
    public object Jint() => _jint.Invoke(_jintMain, 1);

    [Benchmark]
    public JsValue JauntTyped() => _jauntTyped.Invoke(_jauntTypedMain, JsValue.From(1));

    [Benchmark]
    public JsValue JauntRaw() => _jauntRaw.Invoke(_jauntRawMain, JsValue.From(1));

    [Benchmark]
    public OkojoJs.JsValue Okojo() => _okojoRealm.Call(_okojoMain, OkojoJs.JsValue.Undefined, [1]);

    [Benchmark]
    public object ClearScriptV8Convenience() => _v8Main.InvokeAsFunction(1);

    [Benchmark]
    public object ClearScriptV8FastProxy() => _v8FastMain.InvokeAsFunction(1);
}

/// <summary>
/// Creating an isolated instance and evaluating `1` (the same work for every engine). ClearScript is measured both with a
/// new V8 runtime (isolate) per engine and with engines created from one shared V8Runtime.
/// </summary>
[MemoryDiagnoser]
public class EngineCreation
{
    private V8Runtime _sharedRuntime = null!;

    [GlobalSetup]
    public void Setup() => _sharedRuntime = new V8Runtime();

    [GlobalCleanup]
    public void Cleanup() => _sharedRuntime.Dispose();

    [Benchmark(Baseline = true)]
    public object Jint() { using var e = new global::Jint.Engine(); return e.Evaluate("1"); }

    [Benchmark]
    public JsValue Jaunt() { using var e = new JsEngine(); return e.Evaluate("1"); }

    [Benchmark]
    public OkojoJs.JsValue Okojo() { using var r = OkojoEmbedding.JsRuntime.Create(); return r.MainRealm.Eval("1"); }

    [Benchmark]
    public object ClearScriptV8NewRuntime() { using var e = new V8ScriptEngine(); return e.Evaluate("1"); }

    [Benchmark]
    public object ClearScriptV8SharedRuntime() { using var e = _sharedRuntime.CreateScriptEngine(); return e.Evaluate("1"); }
}

/// <summary>`--engines-check`: every engine computes the same `main(1)` for every workload (the benchmarks compare equal work).</summary>
public static class EngineCheck
{
    public static void Run()
    {
        bool ok = true;
        foreach (var w in new[] { "numeric-loop", "fib", "objects", "arrays", "strings", "classes", "json" })
        {
            var b = new EngineWarmInvocation { Workload = w };
            b.Setup();
            var results = new[] { b.Jaunt().ToString(), b.Jint().ToString(), Okojo(b.Okojo()), Convert.ToString(b.ClearScriptV8(), System.Globalization.CultureInfo.InvariantCulture) };
            b.Cleanup();
            bool same = results.All(r => r == results[0]);
            ok &= same;
            Console.WriteLine($"{w,-13} {(same ? "same" : "DIFFERENT")}  jaunt={results[0]} jint={results[1]} okojo={results[2]} v8={results[3]}");
        }
        var h = new EngineHostCalls();
        h.Setup();
        var hr = new[] { h.JauntTyped().ToString(), h.JauntRaw().ToString(), h.Jint().ToString(), Okojo(h.Okojo()), Convert.ToString(h.ClearScriptV8Convenience(), System.Globalization.CultureInfo.InvariantCulture), Convert.ToString(h.ClearScriptV8FastProxy(), System.Globalization.CultureInfo.InvariantCulture) };
        h.Cleanup();
        ok &= hr.All(r => r == hr[0]);
        Console.WriteLine($"host-calls    {(hr.All(r => r == hr[0]) ? "same" : "DIFFERENT")}  {string.Join(" / ", hr)}");
        if (!ok) Environment.ExitCode = 1;
    }

    private static string Okojo(OkojoJs.JsValue v) => v.IsString ? v.AsString() : v.NumberValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// `--engines-memory`: retained memory of N live instances per engine after evaluating `1` (and, separately, after
/// loading the `classes` workload). Managed heap (GC.GetTotalMemory after full collections) and, for ClearScript, the
/// V8 native heaps (V8Runtime.GetHeapInfo) are reported separately; process working-set deltas include both and
/// everything else (code pages, allocator slack). Not a BenchmarkDotNet measurement.
/// </summary>
public static class EngineMemory
{
    public static void Run(string[] args)
    {
        int n = args.Length > 0 ? int.Parse(args[0]) : 100;
        Console.WriteLine($"{n} live instances each; per-instance averages");
        Console.WriteLine($"{"engine",-24} {"workload",-8} {"managed KiB",12} {"V8 heap KiB",12} {"working set KiB",16}");
        foreach (var workload in new[] { "empty", "classes" })
        {
            var src = workload == "empty" ? "1" : Workloads.Classes + "\nmain(1)";
            Measure("Jaunt", workload, n, () => { var e = new JsEngine(); e.Evaluate(src); return e; }, null);
            Measure("Jint 4.17.0", workload, n, () => { var e = new global::Jint.Engine(); e.Evaluate(src); return e; }, null);
            Measure("Okojo 0.2.0-preview.2", workload, n, () => { var r = OkojoEmbedding.JsRuntime.Create(); r.MainRealm.Eval(src); return r; }, null);
            Measure("ClearScript V8 7.5.1.1", workload, n, () => { var e = new V8ScriptEngine(); e.Evaluate(src); return e; },
                o => (long)((V8ScriptEngine)o).GetRuntimeHeapInfo().TotalHeapSize);
        }
    }

    private static void Measure(string name, string workload, int n, Func<IDisposable> create, Func<IDisposable, long>? nativeHeap)
    {
        create().Dispose();   // warm up type loading / JIT
        Collect();
        long managed0 = GC.GetTotalMemory(true);
        long ws0 = Process.GetCurrentProcess().WorkingSet64;
        var live = new List<IDisposable>(n);
        for (int i = 0; i < n; i++) live.Add(create());
        Collect();
        long managed = GC.GetTotalMemory(true) - managed0;
        long ws = Process.GetCurrentProcess().WorkingSet64 - ws0;
        long native = 0;
        if (nativeHeap != null) foreach (var o in live) native += nativeHeap(o);
        Console.WriteLine($"{name,-24} {workload,-8} {managed / 1024.0 / n,12:F1} {(nativeHeap == null ? "-" : (native / 1024.0 / n).ToString("F1")),12} {ws / 1024.0 / n,16:F1}");
        foreach (var o in live) o.Dispose();
        GC.KeepAlive(live);
    }

    private static void Collect()
    {
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
    }
}
