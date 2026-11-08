using System.Diagnostics;
using System.Text;
using Jaunt;
using Jaunt.CrossBench;

// Jaunt NativeAOT consumer host.
//
// Every script this host executes is supplied at run time (files passed on the command line), so the
// published native binary has to parse, compile and execute JavaScript it has never seen at build time.
//
// Usage:
//   Jaunt.AotHost run <script.js>                 evaluate a script, print the completion value
//   Jaunt.AotHost verify <dir>                     run the full verification suite on scripts found in <dir>
//   Jaunt.AotHost bench <workloads-dir> [expected.json] [--filter s] [--samples n] [--target-ms n] [--warm-ms n] [--warm-max-ms n] [--json out.jsonl]
//                                                  hot-path throughput of every workload under the cross-engine protocol
//                                                  (bench/cross/dotnet/CrossProtocol.cs); engine = jaunt-aot or jaunt-jit depending on
//                                                  how this host runs, so the native binary and `dotnet Jaunt.AotHost.dll` can be compared
//                                                  (bench/cross/aot-vs-jit.sh)
//   Jaunt.AotHost bench <script.js> <iterations>   time repeated invocation of `main` exported by one script

return args.Length switch
{
    >= 2 when args[0] == "run" => Run(args[1]),
    >= 2 when args[0] == "verify" => await Verify(args[1]),
    >= 2 when args[0] == "bench" => Bench(args),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: Jaunt.AotHost run <script.js> | verify <dir> | bench <workloads-dir> [expected.json] [--filter s] [--samples n] [--target-ms n] [--warm-ms n] [--warm-max-ms n] [--json out.jsonl] | bench <script.js> <iterations>");
    return 2;
}

static int Run(string path)
{
    using var engine = new JsEngine();
    engine.SetFunction("print", (string s) => { Console.WriteLine(s); });
    var value = engine.Evaluate(File.ReadAllText(path));
    Console.WriteLine(value.IsString ? value.AsString() : value.ToString());
    return 0;
}

static int Bench(string[] args)
{
    if (args.Length >= 3 && File.Exists(args[1]) && int.TryParse(args[2], out int iterations)) return BenchOne(args[1], iterations);
    if (!Directory.Exists(args[1])) { Console.Error.WriteLine($"not a workload directory: {args[1]}"); return Usage(); }
    string? expectedPath = args.Length >= 3 && !args[2].StartsWith("--", StringComparison.Ordinal) ? args[2] : null;
    var options = CrossOptions.Parse(args);
    var expected = expectedPath is null ? new Dictionary<string, string>() : CrossProtocol.ReadExpected(expectedPath);
    // The same host measures both ways: the published native binary has no JIT, `dotnet Jaunt.AotHost.dll` runs on CoreCLR.
    // (RuntimeFeature.IsDynamicCodeSupported is false in both, because PublishAot=true writes that feature switch into the
    // runtimeconfig of the JIT build too; the JIT's compiled-method counter tells them apart.)
    long jitted = System.Runtime.JitInfo.GetCompiledMethodCount();
    string engine = jitted > 0 ? "jaunt-jit" : "jaunt-aot";
    string runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + (jitted > 0 ? " CoreCLR (JIT)" : " NativeAOT");
    Console.WriteLine($"# {engine}: {runtime}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, methods JIT-compiled so far: {jitted}");
    using var writer = options.JsonPath is null ? null : new StreamWriter(options.JsonPath, false);
    int bad = 0;
    foreach (var (name, file) in CrossProtocol.Workloads(args[1], options.Filter))
    {
        string src = File.ReadAllText(file);
        var rec = new Dictionary<string, object> { ["engine"] = engine, ["name"] = name, ["runtime"] = runtime };
        var compileOptions = new JsCompileOptions { SourceName = Path.GetFileName(file) };
        CrossProtocol.Measure(rec, () => { var s = JsCompiler.CompileScript(src, compileOptions); return () => new JauntCrossRunner(s); }, expected.GetValueOrDefault(name), options);
        writer?.WriteLine(CrossProtocol.ToJsonLine(rec)); writer?.Flush();
        Console.WriteLine(CrossProtocol.ConsoleLine(rec));
        if ((string)rec["status"] != "ok") bad++;
    }
    return bad == 0 ? 0 : 1;
}

static int BenchOne(string path, int iterations)
{
    var script = JsCompiler.CompileScript(File.ReadAllText(path), new JsCompileOptions { SourceName = Path.GetFileName(path) });
    using var engine = new JsEngine();
    engine.Evaluate(script);
    var main = engine.GetFunction("main");
    var sw = Stopwatch.StartNew();
    JsValue last = default;
    for (int i = 0; i < iterations; i++) last = engine.Invoke(main, JsValue.From(i));
    sw.Stop();
    Console.WriteLine($"{iterations} calls in {sw.Elapsed.TotalMilliseconds:F1} ms ({sw.Elapsed.TotalMilliseconds * 1000 / iterations:F2} µs/call), last={last}");
    return 0;
}

static async Task<int> Verify(string dir)
{
    int failures = 0;
    void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " — " + detail : "")}");
        if (!ok) failures++;
    }
    string Read(string file) => File.ReadAllText(Path.Combine(dir, file));

    // 1. dynamic script: parse + compile + execute + host functions
    try
    {
        using var engine = new JsEngine();
        engine.SetValue("price", 100.0);
        engine.SetFunction("tax", (double amount) => amount * 0.13);
        var r = engine.Evaluate(Read("script.js"));
        Check("dynamic script + typed host function", r.IsNumber && Math.Abs(r.AsNumber() - 2260) < 1e-9, r.ToString());
    }
    catch (Exception ex) { Check("dynamic script + typed host function", false, ex.ToString()); }

    // 2. language coverage script (classes, generators, destructuring, regexp, JSON, Map/Set, closures, try/finally)
    try
    {
        using var engine = new JsEngine();
        var r = engine.Evaluate(Read("language.js"));
        Check("language features", r.IsString && r.AsString() == "ok", r.ToString());
    }
    catch (Exception ex) { Check("language features", false, ex.ToString()); }

    // 2b. Temporal (non-ISO calendars), Intl through the platform ICU, realms and shared memory
    try
    {
        using var engine = new JsEngine();
        engine.SetFunction("newRealm", static (ref JsCallContext c) => c.CreateRealm(), 0);
        var r = engine.Evaluate("""
            var d = Temporal.PlainDate.from({ year: 5784, monthCode: "M05L", day: 1, calendar: "hebrew" });
            var parts = [d.toString(), Temporal.Duration.from("P1Y2M").total({ unit: "days", relativeTo: "2024-01-31" })];
            if (typeof Intl !== "undefined")
              parts.push(new Intl.NumberFormat("de-DE", { style: "currency", currency: "EUR" }).format(1234.5),
                         new Intl.DateTimeFormat("en-US", { dateStyle: "medium", timeZone: "UTC" }).format(0),
                         new Intl.PluralRules("en").select(1), ["b", "a"].sort(new Intl.Collator("en").compare).join(""));
            var g = newRealm();
            parts.push(g.Array !== Array);
            parts.join("|")
            """).ToString();
        bool ok = r.StartsWith("2024-02-10[u-ca=hebrew]|425|", StringComparison.Ordinal) && r.EndsWith("|true", StringComparison.Ordinal);
        Check("Temporal calendars, Intl (ICU), realms", ok, r);
    }
    catch (Exception ex) { Check("Temporal calendars, Intl (ICU), realms", false, ex.ToString()); }

    // 3. modules + live export + DTO (hand-written descriptors) + JSON channel inside one execution scope
    try
    {
        var resolver = new JsDictionaryModuleResolver().Add("util.mjs", Read("util.mjs"));
        var module = JsCompiler.CompileModule(Read("plugin.mjs"), new JsCompileOptions { SourceName = "plugin.mjs" });
        using var engine = new JsEngine();
        var instance = engine.LoadModule(module, resolver);
        var transform = instance.GetExport("transform");
        using var execution = engine.BeginExecution(new JsExecutionOptions { Timeout = TimeSpan.FromSeconds(5), MaxInstructions = 5_000_000, MaxOutputBytes = 1 << 20 });
        var dto = execution.FromObject(new Order("A-1", new List<Line> { new("apple", 2, 1.5), new("pear", 1, 2.25) }), Bindings.Order);
        var result = execution.Invoke(transform, dto);
        var summary = execution.ToObject(result, Bindings.Summary);
        var json = execution.ToJsonString(result);
        var parsed = execution.ParseJson(Encoding.UTF8.GetBytes("{\"id\":\"B-2\",\"lines\":[{\"name\":\"x\",\"qty\":3,\"price\":1}]}"));
        var summary2 = execution.ToObject(execution.Invoke(transform, parsed), Bindings.Summary);
        execution.Complete();
        Check("ESM + DTO + JSON channel", summary == new Summary("A-1", 3, 5.25, "APPLE,PEAR") && summary2.Total == 3 && json.Contains("\"total\":5.25"), $"{summary} / {json}");
    }
    catch (Exception ex) { Check("ESM + DTO + JSON channel", false, ex.ToString()); }

    // 4. budgets: instruction limit, timeout, tracked bytes, stack depth and cancellation terminate safely
    try
    {
        using var e1 = new JsEngine(new JsEngineOptions { Limits = new JsLimits { MaxInstructions = 100_000 } });
        var k1 = Assert<JsLimitExceededException>(() => e1.Evaluate(Read("loop.js"))).Kind;
        using var e2 = new JsEngine(new JsEngineOptions { Limits = new JsLimits { Timeout = TimeSpan.FromMilliseconds(30) } });
        var k2 = Assert<JsLimitExceededException>(() => e2.Evaluate(Read("loop.js"))).Kind;
        using var e3 = new JsEngine(new JsEngineOptions { Limits = new JsLimits { MaxTrackedBytes = 1 << 20 } });
        var k3 = Assert<JsLimitExceededException>(() => e3.Evaluate("const a = []; for (;;) a.push({ v: a.length })")).Kind;
        using var e4 = new JsEngine(new JsEngineOptions { Limits = new JsLimits { MaxStackDepth = 200 } });
        var k4 = Assert<JsLimitExceededException>(() => e4.Evaluate("function r() { return r() } r()")).Kind;
        using var e5 = new JsEngine();
        using var cts = new CancellationTokenSource(20);
        Assert<OperationCanceledException>(() => e5.Evaluate(Read("loop.js"), cts.Token));
        Check("limits & cancellation", k1 == JsLimitKind.Instructions && k2 == JsLimitKind.Timeout && k3 == JsLimitKind.TrackedBytes && k4 == JsLimitKind.StackDepth && e5.IsFaulted);
    }
    catch (Exception ex) { Check("limits & cancellation", false, ex.ToString()); }

    // 4b. deep native recursion ends in a catchable limit, never a process crash (join/toString of nested arrays,
    //     yield* delegation, deeply nested regex groups, JSON); run on a 1 MB thread to make it tight
    try
    {
        string[] attacks =
        {
            "let a=[];for(let i=0;i<20000;i++)a=[a];String(a)",
            "let a=[];for(let i=0;i<20000;i++)a=[a];a.toLocaleString()",
            "function* g(n){if(n>0)yield* g(n-1);else yield 1};[...g(100000)].length",
            "new RegExp('['.repeat(30000)+']'.repeat(30000),'v').test('')",
            "let o={};for(let i=0;i<200000;i++)o={o};JSON.stringify(o).length",
        };
        var outcomes = new List<string>();
        var t = new Thread(() =>
        {
            foreach (var src in attacks)
            {
                try { using var e = new JsEngine(); e.Evaluate(src); outcomes.Add("completed"); }
                catch (JsLimitExceededException lx) { outcomes.Add(lx.Kind.ToString()); }
                catch (JsException jx) { outcomes.Add(jx.Name); }
            }
        }, 1024 * 1024);
        t.Start();
        t.Join();
        Check("deep recursion is a catchable limit", outcomes.Count == attacks.Length && outcomes.All(o => o is "StackDepth" or "SyntaxError" or "RangeError"), string.Join(",", outcomes));
    }
    catch (Exception ex) { Check("deep recursion is a catchable limit", false, ex.ToString()); }

    // 5. async: host async function, promise pump, top-level await module, AsyncRequired
    try
    {
        using var engine = new JsEngine();
        engine.SetAsyncFunction("fetchValue", async (string key, CancellationToken ct) => { await Task.Delay(1, ct); return key.Length; });
        var r = await engine.EvaluateAsync(Read("async.js"));
        var tla = await engine.LoadModuleAsync(JsCompiler.CompileModule(Read("tla.mjs")), id: "tla.mjs");
        using var syncEngine = new JsEngine();
        syncEngine.SetAsyncFunction("fetchValue", (string key, CancellationToken ct) => new ValueTask<int>(1));
        Assert<JsAsyncRequiredException>(() => syncEngine.Evaluate("fetchValue('x')"));
        Check("async host + promises + TLA", r.AsNumber() == 14 && tla.GetValue("value").AsString() == "tla:6", $"{r} / {tla.GetValue("value")}");
    }
    catch (Exception ex) { Check("async host + promises + TLA", false, ex.ToString()); }

    // 6. JsPlugin: FreshRealm per call, parallel, no state leakage
    try
    {
        var module = JsCompiler.CompileModule(Read("stateful.mjs"));
        await using var plugin = JsPlugin.Create(module, "handle", Bindings.Order, Bindings.Summary);
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => plugin.InvokeAsync(new Order("O" + i, new List<Line> { new("n", i, 1) })).AsTask()));
        bool ok = results.Select((s, i) => s.Id == "O" + i && s.Names == "calls=1").All(x => x);
        Check("JsPlugin FreshRealm isolation", ok, string.Join(";", results.Take(3)));
    }
    catch (Exception ex) { Check("JsPlugin FreshRealm isolation", false, ex.ToString()); }

    // 7b. source-generated bindings (Jaunt.Generators) under NativeAOT
    try
    {
        var module = JsCompiler.CompileModule(Read("plugin.mjs"), new JsCompileOptions { SourceName = "plugin.mjs" });
        var resolver = new JsDictionaryModuleResolver().Add("util.mjs", Read("util.mjs"));
        await using var plugin = JsPlugin.Create(module, "transform", GeneratedBindings.Default.GenOrder, GeneratedBindings.Default.GenSummary,
            new JsPluginOptions { Resolver = resolver });
        var s = await plugin.InvokeAsync(new GenOrder("G-1", new List<GenLine> { new("kiwi", 4, 0.5) }));
        Check("source-generated DTO bindings", s == new GenSummary("G-1", 4, 2, "KIWI"), s.ToString());
    }
    catch (Exception ex) { Check("source-generated DTO bindings", false, ex.ToString()); }

    // 7. eval / new Function compile new bytecode at run time under AOT; Hardened blocks them
    try
    {
        using var standard = new JsEngine();
        var r = standard.Evaluate("new Function('a', 'b', 'return a * b')(6, 7) + eval('1 + 1')");
        using var hardened = new JsEngine(new JsEngineOptions { Profile = JsProfile.Hardened });
        var h = hardened.Evaluate("try { (()=>{}).constructor('return 1')(); 'allowed' } catch (e) { e.name }");
        Check("runtime code generation (eval/Function) & Hardened policy", r.AsNumber() == 44 && h.AsString() == "EvalError", $"{r} / {h}");
    }
    catch (Exception ex) { Check("runtime code generation (eval/Function) & Hardened policy", false, ex.ToString()); }

    Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
    Console.WriteLine($"runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, dynamic code supported: {System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}");
    return failures == 0 ? 0 : 1;
}

static T Assert<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T ex) { return ex; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}");
}

public sealed record GenLine(string Name, int Qty, double Price);
public sealed record GenOrder(string Id, List<GenLine> Lines);
public sealed record GenSummary(string Id, int Items, double Total, string Names);

[JsSerializable(typeof(GenLine))]
[JsSerializable(typeof(GenOrder))]
[JsSerializable(typeof(GenSummary))]
public partial class GeneratedBindings : JsBindingContext { }

public sealed record Line(string Name, int Qty, double Price);
public sealed record Order(string Id, List<Line> Lines);
public sealed record Summary(string Id, int Items, double Total, string Names);

/// <summary>Hand-written, AOT-safe descriptors (no reflection).</summary>
public static class Bindings
{
    public static readonly JsTypeInfo<Line> Line = JsTypeInfo.Object<Line>()
        .Property("name", x => x.Name, JsTypeInfo.String!)
        .Property("qty", x => x.Qty, JsTypeInfo.Int32)
        .Property("price", x => x.Price, JsTypeInfo.Double)
        .Factory(r => new Line(r.Required("name", JsTypeInfo.String)!, r.Required("qty", JsTypeInfo.Int32), r.Required("price", JsTypeInfo.Double)))
        .Build();

    public static readonly JsTypeInfo<Order> Order = JsTypeInfo.Object<Order>()
        .Property("id", x => x.Id, JsTypeInfo.String!)
        .Property("lines", x => x.Lines, JsTypeInfo.List(Line))
        .Factory(r => new Order(r.Required("id", JsTypeInfo.String)!, r.Required("lines", JsTypeInfo.List(Line))))
        .Build();

    public static readonly JsTypeInfo<Summary> Summary = JsTypeInfo.Object<Summary>()
        .Property("id", x => x.Id, JsTypeInfo.String!)
        .Property("items", x => x.Items, JsTypeInfo.Int32)
        .Property("total", x => x.Total, JsTypeInfo.Double)
        .Property("names", x => x.Names, JsTypeInfo.String!)
        .Factory(r => new Summary(r.Required("id", JsTypeInfo.String)!, r.Required("items", JsTypeInfo.Int32), r.Required("total", JsTypeInfo.Double), r.Required("names", JsTypeInfo.String)!))
        .Build();
}
