using System.Diagnostics;
using Jaunt;
using Jaunt.Runtime;
using Xunit;

namespace Jaunt.Tests;

/// <summary>
/// Host-boundary guarantees from design/01-research.md §3.3–§3.4 and §6: foreign values, budgets that cannot be escaped
/// (realms, compilation, the .NET RegExp backend, plugin module initialization), interrupts that cannot hit the next
/// execution, and output measured in bytes.
/// </summary>
public class HostBoundaryTests
{
    [Fact]
    public void ForeignValuesAreRejectedByEveryHostEntryPoint()
    {
        using var a = new JsEngine();
        using var b = new JsEngine();
        var foreignObj = a.Evaluate("({ who: 'A' })");
        var foreignFn = a.Evaluate("(function () { return 'A-engine' })");
        var rejected = new List<string>();
        b.SetFunction("probe", (ref JsCallContext c) =>
        {
            var own = c.CreateObject();
            try { c.SetProperty(own, "inner", foreignObj); } catch (ArgumentException) { rejected.Add("SetProperty"); }
            try { c.CreateArray(new[] { foreignObj }); } catch (ArgumentException) { rejected.Add("CreateArray"); }
            try { c.Call(foreignFn, default, ReadOnlySpan<JsValue>.Empty); } catch (ArgumentException) { rejected.Add("Call"); }
            try { c.Call(c.CreateObject(), foreignObj, ReadOnlySpan<JsValue>.Empty); } catch (ArgumentException) { rejected.Add("CallThis"); }
            try { c.GetOwnDataProperty(foreignObj, "who"); } catch (ArgumentException) { rejected.Add("GetOwnDataProperty"); }
            return JsValue.Undefined;
        });
        b.Evaluate("probe()");
        Assert.Equal(new[] { "SetProperty", "CreateArray", "Call", "CallThis", "GetOwnDataProperty" }, rejected);

        // DTO conversion contexts (JsTypeInfo.Write) check the values they put into the graph as well
        var info = new ForeignWriter(foreignObj);
        using var exec = b.BeginExecution();
        Assert.ThrowsAny<Exception>(() => exec.FromObject(1, info));
        Assert.True(info.Rejected);
    }

    private sealed class ForeignWriter(JsValue foreign) : JsTypeInfo<int>
    {
        public bool Rejected;
        public override JsValue Write(JsConversionContext context, int value)
        {
            var o = context.CreateObject();
            try { context.SetProperty(o, "x", foreign); }
            catch (ArgumentException) { Rejected = true; throw; }
            return o;
        }
        public override int Read(JsConversionContext context, JsValue value) => 0;
    }

    [Fact]
    public void NewRealmsShareTheTrackedBytesBudget()
    {
        using var engine = new JsEngine(new JsEngineOptions { Limits = new JsLimits { MaxTrackedBytes = 4 * 1024 * 1024 } });
        var ex = Assert.Throws<JsLimitExceededException>(() => engine.Evaluate("""
            const keep = [];
            for (let i = 0; i < 20; i++) {
                const r = new ShadowRealm();
                r.evaluate("globalThis.a = Array.from({ length: 100000 }, (_, i) => ({ i })); 1");
                keep.push(r);
            }
            """));
        Assert.Equal(JsLimitKind.TrackedBytes, ex.Kind);

        // creating realms is charged too: thousands of empty realms cannot be made for free
        using var e2 = new JsEngine(new JsEngineOptions { Limits = new JsLimits { MaxTrackedBytes = 1024 * 1024 } });
        var ex2 = Assert.Throws<JsLimitExceededException>(() => e2.Evaluate("const k = []; for (let i = 0; i < 100000; i++) k.push(new ShadowRealm());"));
        Assert.Equal(JsLimitKind.TrackedBytes, ex2.Kind);
    }

    [Fact]
    public void OutputBudgetCountsUtf8Bytes()
    {
        using var engine = new JsEngine(new JsEngineOptions { Limits = new JsLimits { MaxOutputBytes = 100 } });
        var small = engine.Evaluate("'€'.repeat(30)");   // 92 UTF-8 bytes with the quotes
        using (var exec = engine.BeginExecution()) { Assert.Equal(92, exec.ToJsonUtf8(small).Length); exec.Complete(); }
        var big = engine.Evaluate("'€'.repeat(60)");     // 62 UTF-16 units but 182 UTF-8 bytes
        using var exec2 = engine.BeginExecution();
        var ex = Assert.Throws<JsLimitExceededException>(() => exec2.ToJsonUtf8(big));
        Assert.Equal(JsLimitKind.OutputBytes, ex.Kind);
    }

    [Fact]
    public async Task PluginExecutionBudgetCoversModuleInitialization()
    {
        var module = JsCompiler.CompileModule("while (true) {} export function transform(i) { return i }");
        var options = new JsPluginOptions { Execution = new JsExecutionOptions { Timeout = TimeSpan.FromMilliseconds(100) } };
        await using var plugin = JsPlugin.Create(module, "transform", ApiTests.InputInfo, ApiTests.InputInfo, options);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<JsLimitExceededException>(() => plugin.Invoke(new ApiTests.PluginInput("x", 1), cts.Token));
        Assert.Equal(JsLimitKind.Timeout, ex.Kind);
        var ex2 = await Assert.ThrowsAsync<JsLimitExceededException>(async () => await plugin.InvokeAsync(new ApiTests.PluginInput("x", 1), cts.Token));
        Assert.Equal(JsLimitKind.Timeout, ex2.Kind);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
    }

    [Fact]
    public void InterruptRequestedForOneExecutionNeverStopsTheNext()
    {
        using var engine = new JsEngine();
        var vm = engine.Vm;
        vm.BeginBudget(0, null, default, 1000);
        Task? ending = null;
        // the execution ends while the request is being made (between "is it active?" and "set the flag")
        vm.RequestInterrupt(() =>
        {
            ending = Task.Run(vm.EndBudget);
            Thread.Sleep(50);
            return true;
        });
        ending!.Wait();
        vm.BeginBudget(0, null, default, 1000);
        vm.Checkpoint();   // the next execution is not interrupted
        vm.EndBudget();
        Assert.Equal(2, engine.Evaluate("1 + 1").AsInt32());
    }

    [Fact]
    public void CompilationObeysTheExecutionTimeoutAndInterrupt()
    {
        // ~6.6M chars, ~3.6M AST nodes: below the default 4M node budget (eval always uses it), so only the timeout or
        // Interrupt can stop the compilation however late a coarse timer fires; still far longer to compile than 30 ms
        var src = string.Concat(Enumerable.Repeat("x = 1 + 2;\n", 600_000));
        var compileOptions = new JsCompileOptions { MaxSourceLength = 64 * 1024 * 1024 };
        using (var engine = new JsEngine(new JsEngineOptions { CompileOptions = compileOptions, Limits = new JsLimits { Timeout = TimeSpan.FromMilliseconds(30) } }))
        {
            var ex = Assert.Throws<JsLimitExceededException>(() => engine.Evaluate(src));
            Assert.Equal(JsLimitKind.Timeout, ex.Kind);
        }
        using (var engine = new JsEngine(new JsEngineOptions { Limits = new JsLimits { Timeout = TimeSpan.FromMilliseconds(30) } }))
        {
            engine.SetValue("src", JsValue.From(src));
            var ex = Assert.Throws<JsLimitExceededException>(() => engine.Evaluate("eval(src)"));
            Assert.Equal(JsLimitKind.Timeout, ex.Kind);
        }
        using (var engine = new JsEngine(new JsEngineOptions { CompileOptions = compileOptions }))
        {
            // a dedicated thread, not a thread-pool timer (parallel blocking tests can starve the pool)
            var interrupter = new Thread(() => { Thread.Sleep(30); engine.Interrupt(); }) { IsBackground = true };
            interrupter.Start();
            Assert.Throws<OperationCanceledException>(() => engine.Evaluate(src));
            interrupter.Join();
        }
    }

    [Fact]
    public void DotnetRegexBackendObeysTheExecutionBudget()
    {
        const string script = "/^(?:a|aa)+$/.test('a'.repeat(40) + '!')";
        using (var engine = new JsEngine(new JsEngineOptions { Limits = new JsLimits { Timeout = TimeSpan.FromMilliseconds(100) } }))
        {
            var sw = Stopwatch.StartNew();
            var ex = Assert.Throws<JsLimitExceededException>(() => engine.Evaluate(script));
            Assert.Equal(JsLimitKind.Timeout, ex.Kind);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"took {sw.Elapsed}");   // was a fixed 5 s .NET timeout; CI runners are slow and shared
        }
        using (var engine = new JsEngine())
        {
            // cancelled from a dedicated thread: a CancellationTokenSource timer fires on the thread pool, which other
            // (blocking) tests running in parallel can starve for seconds on a small CI runner
            using var cts = new CancellationTokenSource();
            var canceller = new Thread(() => { Thread.Sleep(100); cts.Cancel(); }) { IsBackground = true };
            var sw = Stopwatch.StartNew();
            canceller.Start();
            Assert.Throws<OperationCanceledException>(() => engine.Evaluate(script, cts.Token));
            canceller.Join();
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"took {sw.Elapsed}");   // was a fixed 5 s .NET timeout; CI runners are slow and shared
        }
    }
}

public class AmbientCapabilityTests
{
    [Fact]
    public async Task BareEngineHasNoAmbientCapabilities()
    {
        using var engine = new JsEngine();
        var globals = engine.Evaluate("""
            ['setTimeout', 'setInterval', 'fetch', 'XMLHttpRequest', 'WebSocket', 'console', 'require',
             'process', 'Deno', 'Bun', 'importScripts', 'Worker', 'navigator', 'location', 'document']
              .filter(n => typeof globalThis[n] !== 'undefined').join()
            """);
        Assert.Equal("", globals.ToString());
        // no module loader: importing a file or built-in module is rejected, nothing is read
        var r = await engine.EvaluateAsync("import('fs').then(() => 'loaded', e => e.constructor.name)");
        Assert.Equal("TypeError", r.ToString());
    }

    [Fact]
    public void ConcurrentEntryFromAnotherThreadFailsFast()
    {
        using var engine = new JsEngine();
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        engine.SetFunction("block", () => { inside.Set(); release.Wait(TimeSpan.FromSeconds(30)); });
        var running = Task.Run(() => engine.Evaluate("block(); 42"));
        Assert.True(inside.Wait(TimeSpan.FromSeconds(30)));
        Exception? second = null;
        var other = new Thread(() => { try { engine.Evaluate("1"); } catch (Exception ex) { second = ex; } });
        other.Start();
        Assert.True(other.Join(TimeSpan.FromSeconds(30)));   // fails at once instead of waiting for a lock
        release.Set();
        Assert.IsType<InvalidOperationException>(second);
        Assert.Equal(42, running.Result.AsInt32());
        Assert.Equal(2, engine.Evaluate("1 + 1").AsInt32());
    }

    [Fact]
    public void RetainSourceFalseKeepsNoSourceText()
    {
        const string src = "function secret(a) { return a + 'token-123'; } secret.toString()";
        var script = JsCompiler.CompileScript(src, new JsCompileOptions { RetainSource = false });
        using var engine = new JsEngine();
        Assert.Equal("function secret() { [native code] }", engine.Evaluate(script).ToString());
        using var e2 = new JsEngine();
        Assert.Contains("token-123", e2.Evaluate(src).ToString());
    }
}

/// <summary>Tests that change the process-wide .NET RegExp time slice run alone.</summary>
[CollectionDefinition("RegexTimeSlice", DisableParallelization = true)]
public sealed class RegexTimeSliceCollection;

[Collection("RegexTimeSlice")]
public class RegexTimeSliceTests
{
    [Fact]
    public void SlicedDotnetMatchesFallBackToTheNativeEngineWithTheSameGroups()
    {
        var saved = RegExpBuiltins.MatchTimeout;
        RegExpBuiltins.MatchTimeout = TimeSpan.FromTicks(1);   // every .NET match of a newly compiled pattern times out
        try
        {
            using var engine = new JsEngine();
            // unique source text so the pattern is compiled (and cached) with the tiny slice
            var r = engine.Evaluate("""
                const m = /(?<year>\d{4})-(\d{2})-(?<day>\d{2})(?:x{0})/.exec('on 2026-11-07!');
                [m.index, m[0], m[1], m[2], m[3], m.groups.year, m.groups.day, m.length].join('|')
                """);
            Assert.Equal("3|2026-11-07|2026|11|07|2026|07|4", r.ToString());
        }
        finally { RegExpBuiltins.MatchTimeout = saved; }
    }
}
