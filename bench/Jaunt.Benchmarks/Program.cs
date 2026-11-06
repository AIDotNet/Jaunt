using System.Diagnostics;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Jaunt;
using Jaunt.Benchmarks;

if (args.Length > 1 && args[0] == "--eval")
{
    using var eng = new Jaunt.JsEngine();
    foreach (var code in args.Skip(1))
    {
        try { Console.WriteLine(eng.Evaluate(File.Exists(code) ? File.ReadAllText(code) : code).ToString()); }
        catch (Exception ex) { Console.WriteLine($"{ex.GetType().Name}: {ex.Message}"); }
    }
    return;
}
if (args.Length > 1 && args[0] == "--micro")
{
    var msrc = File.ReadAllText(args[1]);
    using var eng = new Jaunt.JsEngine();
    eng.Evaluate(Jaunt.JsCompiler.CompileScript(msrc));
    var jint = new Jint.Engine();
    jint.Execute(msrc);
    foreach (var fname in args.Skip(2))
    {
        var jf = eng.GetFunction(fname);
        var jv = jint.GetValue(fname);
        for (int i = 0; i < 300; i++) { eng.Invoke(jf); jint.Invoke(jv); }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 2000; i++) eng.Invoke(jf);
        double tj = sw.Elapsed.TotalMicroseconds / 2000;
        sw.Restart();
        for (int i = 0; i < 2000; i++) jint.Invoke(jv);
        double ti = sw.Elapsed.TotalMicroseconds / 2000;
        Console.WriteLine($"{fname,-12} jaunt {tj,8:F1} µs   jint {ti,8:F1} µs   {ti / tj:F2}x");
    }
    return;
}
if (args.Length > 1 && args[0] == "--run-loop")
{
    var wsrc = File.Exists(args[1]) ? File.ReadAllText(args[1]) : args[1] switch { "arrays" => Jaunt.Benchmarks.Workloads.Arrays, "strings" => Jaunt.Benchmarks.Workloads.Strings, "json" => Jaunt.Benchmarks.Workloads.Json, "objects" => Jaunt.Benchmarks.Workloads.Objects, _ => throw new ArgumentException(args[1]) };
    using var eng = new Jaunt.JsEngine();
    eng.Evaluate(Jaunt.JsCompiler.CompileScript(wsrc));
    var fmain = eng.GetFunction(args.Length > 2 ? args[2] : "main");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    int n = 0;
    while (sw.Elapsed.TotalSeconds < 8) { eng.Invoke(fmain, Jaunt.JsValue.From(n)); n++; }
    Console.WriteLine($"{sw.Elapsed.TotalMicroseconds / n:F1} µs/run  runs={n} gc0={GC.CollectionCount(0)} gc1={GC.CollectionCount(1)} gc2={GC.CollectionCount(2)} pause={GC.GetTotalPauseDuration().TotalMilliseconds:F0}ms");
    return;
}
if (args.Length > 0 && args[0] == "--compile-loop")
{
    var src = string.Concat(Enumerable.Range(0, 40).Select(i => Jaunt.Benchmarks.Workloads.Classes.Replace("Account", "Account" + i).Replace("Savings", "Savings" + i).Replace("function main", "function main" + i)));
    var sw = System.Diagnostics.Stopwatch.StartNew();
    int n = 0;
    while (sw.Elapsed.TotalSeconds < 8) { Jaunt.JsCompiler.CompileScript(src); n++; }
    Console.WriteLine($"{sw.Elapsed.TotalMicroseconds / n:F1} µs/compile");
    return;
}
if (args.Length > 0 && args[0] == "--compile")
{
    CompileProfile.Run();
    return;
}
if (args.Length > 0 && args[0] == "--realm")
{
    RealmProfile.Run();
    return;
}
if (args.Length > 2 && args[0] == "--cross")
{
    CrossBench.Run(args.Skip(1).ToArray());
    return;
}
if (args.Length > 0 && args[0] == "--quick")
{
    QuickBench.Run(args.Skip(1).ToArray());
    return;
}
BenchmarkSwitcher.FromAssembly(typeof(Workloads).Assembly).Run(args, DefaultConfig.Instance.WithOptions(ConfigOptions.DisableOptimizationsValidator));

namespace Jaunt.Benchmarks
{
    /// <summary>Fast stopwatch harness for development iterations (not a substitute for BenchmarkDotNet results).</summary>
    public static class QuickBench
    {
        private static readonly (string Name, string Source, int Iterations)[] Cases =
        {
            ("numeric-loop", Workloads.NumericLoop, 60),
            ("fib(20)", Workloads.Fib, 60),
            ("objects", Workloads.Objects, 60),
            ("arrays", Workloads.Arrays, 60),
            ("strings", Workloads.Strings, 60),
            ("classes", Workloads.Classes, 60),
            ("json", Workloads.Json, 60),
            ("small-call", Workloads.Small, 200_000),
        };

        public static void Run(string[] filter)
        {
            Console.WriteLine($"{"workload",-14} {"Jaunt (µs/op)",14} {"Jint (µs/op)",14} {"ratio",7}  alloc Jaunt/Jint (KB/op)");
            foreach (var (name, src, iters) in Cases)
            {
                if (filter.Length > 0 && !filter.Any(f => name.Contains(f))) continue;
                var (jt, ja, jr) = MeasureJaunt(src, iters);
                var (it, ia, ir) = MeasureJint(src, iters);
                string check = jr == ir ? "" : $"  RESULT MISMATCH {jr} vs {ir}";
                Console.WriteLine($"{name,-14} {jt,14:F2} {it,14:F2} {it / jt,6:F2}x  {ja / 1024.0,8:F1} / {ia / 1024.0,-8:F1}{check}");
            }
            // C02: compile cost (Jaunt: parse + analyze + bytecode; Jint: PrepareScript)
            {
                var src = string.Concat(Enumerable.Range(0, 40).Select(i => Workloads.Classes.Replace("Account", "Account" + i).Replace("Savings", "Savings" + i).Replace("function main", "function main" + i)));
                double jt = Time(() => { JsCompiler.CompileScript(src); return ""; }, 200, out var ja, out _);
                double it = Time(() => { global::Jint.Engine.PrepareScript(src); return ""; }, 200, out var ia, out _);
                Console.WriteLine($"{"compile-" + src.Length / 1024 + "KB",-14} {jt,14:F2} {it,14:F2} {it / jt,6:F2}x  {ja / 1024.0,8:F1} / {ia / 1024.0,-8:F1}");
            }
            // JS -> host typed callbacks
            {
                const string cb = "function main(n) { let s = 0; for (let i = 0; i < 10000; i++) s += hostAdd(i, 1); return s; }";
                using var je = new JsEngine();
                je.SetFunction("hostAdd", (double a, double b) => a + b);
                je.Evaluate(cb);
                var jm = je.GetFunction("main");
                double jt = Time(() => je.Invoke(jm, JsValue.From(1)).ToString(), 300, out var ja, out var jr);
                using var ie = new global::Jint.Engine();
                ie.SetValue("hostAdd", new Func<double, double, double>((a, b) => a + b));
                ie.Execute(cb);
                var im = ie.GetValue("main");
                double it = Time(() => ie.Invoke(im, 1).ToString(), 300, out var ia, out var ir);
                Console.WriteLine($"{"host-callback",-14} {jt,14:F2} {it,14:F2} {it / jt,6:F2}x  {ja / 1024.0,8:F1} / {ia / 1024.0,-8:F1}{(jr == ir ? "" : $"  MISMATCH {jr} vs {ir}")}");
            }
            // engine creation / FreshRealm
            {
                const int n = 2000;
                var warm = Stopwatch.StartNew();
                for (int i = 0; warm.ElapsedMilliseconds < 1000 || i < 200; i++) { using var e = new JsEngine(); }
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < n; i++) { using var e = new JsEngine(); }
                double jaunt = sw.Elapsed.TotalMicroseconds / n;
                double jauntAlloc = (GC.GetAllocatedBytesForCurrentThread() - a0) / (double)n;
                warm.Restart();
                for (int i = 0; warm.ElapsedMilliseconds < 1000 || i < 200; i++) { var e = new global::Jint.Engine(); e.Execute("1"); e.Dispose(); }
                a0 = GC.GetAllocatedBytesForCurrentThread();
                sw.Restart();
                for (int i = 0; i < n; i++) { var e = new global::Jint.Engine(); e.Execute("1"); e.Dispose(); }
                double jint = sw.Elapsed.TotalMicroseconds / n;
                double jintAlloc = (GC.GetAllocatedBytesForCurrentThread() - a0) / (double)n;
                Console.WriteLine($"{"new-engine",-14} {jaunt,14:F2} {jint,14:F2} {jint / jaunt,6:F2}x  {jauntAlloc / 1024.0,8:F1} / {jintAlloc / 1024.0,-8:F1}");
            }
            // plugin end-to-end: fresh engine + module init + JSON in + call + JSON out
            {
                var module = JsCompiler.CompileModule(Workloads.PluginModule);
                var input = Encoding.UTF8.GetBytes(Workloads.PluginInputJson);
                string RunJaunt()
                {
                    using var engine = new JsEngine();
                    var inst = engine.LoadModule(module);
                    var fn = inst.GetExport("transform");
                    using var ex = engine.BeginExecution();
                    var r = ex.Invoke(fn, ex.ParseJson(input));
                    var s = ex.ToJsonString(r);
                    ex.Complete();
                    return s;
                }
                var jintModule = global::Jint.Engine.PrepareModule(Workloads.PluginModule, "plugin");
                string RunJint()
                {
                    using var engine = new global::Jint.Engine();
                    engine.Modules.Add("plugin", b => b.AddModule(jintModule));
                    var ns = engine.Modules.Import("plugin");
                    var fn = ns.Get("transform");
                    var parsed = new global::Jint.Native.Json.JsonParser(engine).Parse(Encoding.UTF8.GetString(input));
                    var r = engine.Invoke(fn, parsed);
                    return new global::Jint.Native.Json.JsonSerializer(engine).Serialize(r).ToString();
                }
                double jt = Time(RunJaunt, 3000, out var ja, out var jr);
                double it = Time(RunJint, 3000, out var ia, out var ir);
                Console.WriteLine($"{"plugin-e2e",-14} {jt,14:F2} {it,14:F2} {it / jt,6:F2}x  {ja / 1024.0,8:F1} / {ia / 1024.0,-8:F1}{(jr == ir ? "" : $"  MISMATCH {jr} vs {ir}")}");
            }
        }

        private static double Time(Func<string> f, int n, out double alloc, out string result)
        {
            var warm = Stopwatch.StartNew();
            for (int i = 0; warm.ElapsedMilliseconds < 1500 || i < n / 10; i++) f();
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            result = "";
            for (int i = 0; i < n; i++) result = f();
            double t = sw.Elapsed.TotalMicroseconds / n;
            alloc = (GC.GetAllocatedBytesForCurrentThread() - a0) / (double)n;
            return t;
        }

        private static (double us, double alloc, string result) MeasureJaunt(string src, int iters)
        {
            var script = JsCompiler.CompileScript(src);
            using var engine = new JsEngine();
            engine.Evaluate(script);
            var main = engine.GetFunction("main");
            JsValue r = default;
            var warm = Stopwatch.StartNew();
            for (int i = 0; warm.ElapsedMilliseconds < 1500 || i < 50; i++) r = engine.Invoke(main, JsValue.From(i));
            var samples = new List<double>();
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            int total = 0;
            for (int round = 0; round < 5; round++)
            {
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < iters; i++) r = engine.Invoke(main, JsValue.From(i));
                samples.Add(sw.Elapsed.TotalMicroseconds / iters);
                total += iters;
            }
            double alloc = (GC.GetAllocatedBytesForCurrentThread() - a0) / (double)total;
            samples.Sort();
            return (samples[2], alloc, r.ToString());
        }

        private static (double us, double alloc, string result) MeasureJint(string src, int iters)
        {
            var prepared = global::Jint.Engine.PrepareScript(src);
            using var engine = new global::Jint.Engine();
            engine.Execute(prepared);
            var main = engine.GetValue("main");
            global::Jint.Native.JsValue r = global::Jint.Native.JsValue.Undefined;
            var warm = Stopwatch.StartNew();
            for (int i = 0; warm.ElapsedMilliseconds < 1500 || i < 50; i++) r = engine.Invoke(main, i);
            var samples = new List<double>();
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            int total = 0;
            for (int round = 0; round < 5; round++)
            {
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < iters; i++) r = engine.Invoke(main, i);
                samples.Add(sw.Elapsed.TotalMicroseconds / iters);
                total += iters;
            }
            double alloc = (GC.GetAllocatedBytesForCurrentThread() - a0) / (double)total;
            samples.Sort();
            return (samples[2], alloc, r.ToString());
        }
    }

    /// <summary>BenchmarkDotNet suite: warm invocation of prepared workloads in Jaunt and Jint.</summary>
    [MemoryDiagnoser]
    public class WarmInvocation
    {
        [Params("numeric-loop", "fib", "objects", "arrays", "strings", "classes", "json")]
        public string Workload { get; set; } = "";

        private JsEngine _jaunt = null!;
        private JsFunction _jauntMain = null!;
        private global::Jint.Engine _jint = null!;
        private global::Jint.Native.JsValue _jintMain = null!;

        private string Source => Workload switch
        {
            "numeric-loop" => Workloads.NumericLoop,
            "fib" => Workloads.Fib,
            "objects" => Workloads.Objects,
            "arrays" => Workloads.Arrays,
            "strings" => Workloads.Strings,
            "classes" => Workloads.Classes,
            _ => Workloads.Json,
        };

        [GlobalSetup]
        public void Setup()
        {
            _jaunt = new JsEngine();
            _jaunt.Evaluate(JsCompiler.CompileScript(Source));
            _jauntMain = _jaunt.GetFunction("main");
            _jint = new global::Jint.Engine();
            _jint.Execute(global::Jint.Engine.PrepareScript(Source));
            _jintMain = _jint.GetValue("main");
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _jaunt.Dispose();
            _jint.Dispose();
        }

        [Benchmark(Baseline = true)]
        public object Jint() => _jint.Invoke(_jintMain, 1);

        [Benchmark]
        public JsValue Jaunt() => _jaunt.Invoke(_jauntMain, JsValue.From(1));
    }

    /// <summary>Fixed-arity scalar host→JS calls (H01).</summary>
    [MemoryDiagnoser]
    public class ScalarCalls
    {
        private JsEngine _jaunt = null!;
        private JsFunction _jauntMain = null!;
        private global::Jint.Engine _jint = null!;
        private global::Jint.Native.JsValue _jintMain = null!;

        [GlobalSetup]
        public void Setup()
        {
            _jaunt = new JsEngine();
            _jaunt.Evaluate(Workloads.Small);
            _jauntMain = _jaunt.GetFunction("main");
            _jint = new global::Jint.Engine();
            _jint.Execute(Workloads.Small);
            _jintMain = _jint.GetValue("main");
        }

        [Benchmark(Baseline = true)]
        public object Jint() => _jint.Invoke(_jintMain, 21);

        [Benchmark]
        public JsValue Jaunt() => _jaunt.Invoke(_jauntMain, JsValue.From(21));
    }

    /// <summary>Instance creation (FreshRealm cost) and plugin end-to-end request.</summary>
    [MemoryDiagnoser]
    public class Isolation
    {
        private JsModule _module = null!;
        private byte[] _input = null!;
        private global::Jint.Prepared<Acornima.Ast.Module> _jintModule;

        [GlobalSetup]
        public void Setup()
        {
            _module = JsCompiler.CompileModule(Workloads.PluginModule);
            _input = Encoding.UTF8.GetBytes(Workloads.PluginInputJson);
            _jintModule = global::Jint.Engine.PrepareModule(Workloads.PluginModule, "plugin");
        }

        [Benchmark]
        public void JauntNewEngine() { using var e = new JsEngine(); }

        [Benchmark]
        public void JintNewEngine() { using var e = new global::Jint.Engine(); e.Execute("1"); }

        [Benchmark]
        public string JauntPluginRequest()
        {
            using var engine = new JsEngine();
            var fn = engine.LoadModule(_module).GetExport("transform");
            using var ex = engine.BeginExecution();
            var s = ex.ToJsonString(ex.Invoke(fn, ex.ParseJson(_input)));
            ex.Complete();
            return s;
        }

        [Benchmark]
        public string JintPluginRequest()
        {
            using var engine = new global::Jint.Engine();
            engine.Modules.Add("plugin", b => b.AddModule(_jintModule));
            var fn = engine.Modules.Import("plugin").Get("transform");
            var parsed = new global::Jint.Native.Json.JsonParser(engine).Parse(Encoding.UTF8.GetString(_input));
            return new global::Jint.Native.Json.JsonSerializer(engine).Serialize(engine.Invoke(fn, parsed)).ToString();
        }
    }
}
