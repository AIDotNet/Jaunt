using Jaunt;
using Jaunt.CrossBench;

namespace Jaunt.Benchmarks;

/// <summary>
/// Cross-engine harness: runs the shared <c>bench/cross/workloads/**/*.js</c> corpus on Jaunt and Jint and emits one JSON
/// line per (engine, workload). The protocol itself lives in <c>bench/cross/dotnet/CrossProtocol.cs</c>, which is also linked into
/// <c>samples/Jaunt.AotHost</c> (its <c>bench</c> subcommand measures Jaunt under NativeAOT the same way). The Go runner
/// (<c>bench/cross/moejs-runner</c>) uses the identical protocol for moejs.
/// </summary>
public static class CrossBench
{
    private sealed class JintRunner : ICrossRunner
    {
        private readonly Jint.Engine _e = new(); private readonly bool _hasResult;
        public JintRunner(Jint.Prepared<Acornima.Ast.Script> script) { _e.Execute(script); _hasResult = _e.GetValue("result").Type == Jint.Runtime.Types.Object; }
        public string Call() { var r = _e.Invoke("main"); _e.Advanced.ProcessTasks(); if (_hasResult) r = _e.Invoke("result"); return r.ToString(); }
        public void Dispose() => _e.Dispose();
    }

    public static int Run(string[] args)
    {
        string dir = args[0], expectedPath = args[1];
        string[] engines = CrossOptions.Opt(args, "--engines", "jaunt,jint")!.Split(',');
        var o = CrossOptions.Parse(args);
        var expected = CrossProtocol.ReadExpected(expectedPath);
        using var writer = o.JsonPath is null ? null : new StreamWriter(o.JsonPath, false);
        foreach (var engine in engines)
        {
            foreach (var (name, file) in CrossProtocol.Workloads(dir, o.Filter))
            {
                string src = File.ReadAllText(file);
                var rec = new Dictionary<string, object> { ["engine"] = engine, ["name"] = name };
                Func<Func<ICrossRunner>> compile = engine == "jaunt"
                    ? () => { var s = JsCompiler.CompileScript(src); return () => new JauntCrossRunner(s); }
                    : () => { var s = Jint.Engine.PrepareScript(src); return () => new JintRunner(s); };
                CrossProtocol.Measure(rec, compile, expected.GetValueOrDefault(name), o);
                string line = CrossProtocol.ToJsonLine(rec);
                writer?.WriteLine(line); writer?.Flush();
                Console.WriteLine(CrossProtocol.ConsoleLine(rec));
            }
        }
        return 0;
    }
}
