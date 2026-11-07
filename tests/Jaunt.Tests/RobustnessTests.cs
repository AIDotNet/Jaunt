using System.Diagnostics;
using Jaunt;
using Xunit;

namespace Jaunt.Tests;

/// <summary>
/// Failure behaviour required by design/01-research.md §3.3/§3.4/§5/§6: deep recursion, disposal and reads from other
/// threads, limits hit while mapping exceptions, interrupts, compile budgets for runtime-compiled code, large single
/// builtin operations and DTO conversion budgets all end in a defined, catchable outcome.
/// </summary>
public class RobustnessTests
{
    public static TheoryData<string> DeepRecursion => new()
    {
        "let a=[];for(let i=0;i<20000;i++)a=[a];a.join()",
        "let a=[];for(let i=0;i<20000;i++)a=[a];String(a)",
        "let a=[];for(let i=0;i<20000;i++)a=[a];''+a",
        "let a=[];for(let i=0;i<20000;i++)a=[a];a.toLocaleString()",
        "let a=[];for(let i=0;i<200000;i++)a=[a];JSON.stringify(a).length",
        "let a=[1];for(let i=0;i<200000;i++)a=[a];a.flat(Infinity).length",
        "new RegExp('['.repeat(30000)+']'.repeat(30000),'v').test('')",
        "new RegExp('(?='.repeat(30000)+')'.repeat(30000),'u').test('')",
        "let p={x:1};for(let i=0;i<200000;i++)p=new Proxy(p,{});p.x",
        "function f(n){return [1].map(()=>f(n+1))};f(0)",
        "function f(s){return s.replace(/a/,()=>f(s))};f('a')",
        "const o={[Symbol.toPrimitive](){return `${o}`}};`${o}`",
        "function* g(n){if(n>0)yield* g(n-1);else yield 1};[...g(100000)].length",
        "function* g(n){try{if(n>0)yield* g(n-1);else throw 1}catch(e){throw e}};[...g(100000)]",
        "async function* g(n){if(n>0)yield* g(n-1);else yield 1};(async()=>{for await(const x of g(100000));})();1",
        "let C=class{};for(let i=0;i<20000;i++)C=class extends C{constructor(){super()}};new C()",
        "function f(){return Reflect.apply(f,null,[])};f()",
    };

    [Theory]
    [MemberData(nameof(DeepRecursion))]
    public void DeepNativeRecursionEndsInACatchableOutcome(string source)
    {
        // a 1 MB stack makes the attacks tight; an unguarded path would crash the whole test process
        Exception? outcome = null;
        var t = new Thread(() =>
        {
            try { using var e = new JsEngine(); e.Evaluate(source); }
            catch (Exception ex) { outcome = ex; }
        }, 1024 * 1024);
        t.Start();
        t.Join();
        Assert.True(outcome is null or JsLimitExceededException { Kind: JsLimitKind.StackDepth } or JsException,
            outcome?.GetType().Name + ": " + outcome?.Message);
    }

    [Fact]
    public void DisposeFromAnotherThreadStopsTheExecutionInsteadOfCorruptingIt()
    {
        var engine = new JsEngine();
        using var started = new ManualResetEventSlim();
        engine.SetFunction("started", () => started.Set());
        Exception? outcome = null;
        JsValue result = default;
        var runner = new Thread(() =>
        {
            try { result = engine.Evaluate("started(); let s = 0; for (let i = 0; i < 3e9; i++) s += i % 3; s"); }
            catch (Exception ex) { outcome = ex; }
        });
        runner.Start();
        Assert.True(started.Wait(TimeSpan.FromSeconds(30)));
        engine.Dispose();
        Assert.True(runner.Join(TimeSpan.FromSeconds(30)));
        Assert.IsType<ObjectDisposedException>(outcome);
        Assert.Throws<ObjectDisposedException>(() => engine.Evaluate("1"));
        engine.Dispose();   // idempotent
    }

    [Fact]
    public void ReadsFromAnotherThreadDuringAnExecutionFailFastButHostCallbacksMayRead()
    {
        using var engine = new JsEngine();
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        JsValue readInCallback = default;
        engine.Evaluate("var shared = 41");
        engine.SetFunction("block", () => { readInCallback = engine.GetGlobal("shared"); inside.Set(); release.Wait(TimeSpan.FromSeconds(30)); });
        var running = Task.Run(() => engine.Evaluate("block(); shared + 1"));
        Assert.True(inside.Wait(TimeSpan.FromSeconds(30)));
        Exception? fromOther = null;
        var other = new Thread(() => { try { engine.GetGlobal("shared"); } catch (Exception ex) { fromOther = ex; } });
        other.Start();
        other.Join();
        release.Set();
        Assert.Equal(42, running.Result.AsInt32());
        Assert.Equal(41, readInCallback.AsInt32());
        Assert.IsType<InvalidOperationException>(fromOther);
    }

    [Fact]
    public void ALimitHitWhileReadingAThrownValueFaultsTheEngineCleanly()
    {
        using var engine = new JsEngine(new JsEngineOptions { Limits = new JsLimits { Timeout = TimeSpan.FromMilliseconds(100) } });
        var ex = Assert.Throws<JsLimitExceededException>(() => engine.Evaluate("throw { get message() { for (;;) {} } }"));
        Assert.Equal(JsLimitKind.Timeout, ex.Kind);
        Assert.True(engine.IsFaulted);
    }

    [Fact]
    public void InterruptDuringAHostCallbackStillStopsTheExecution()
    {
        using var engine = new JsEngine();
        engine.SetFunction("interruptMe", () => engine.Interrupt());
        Assert.Throws<OperationCanceledException>(() => engine.Evaluate("interruptMe(); 1"));
    }

    [Fact]
    public void InterruptByIdOnlyStopsThatExecution()
    {
        using var engine = new JsEngine();
        long id = 0;
        engine.SetFunction("capture", () => { id = engine.CurrentExecutionId; });
        Assert.Equal(1, engine.Evaluate("capture(); 1").AsInt32());
        Assert.NotEqual(0, id);
        Assert.Equal(0, engine.CurrentExecutionId);
        Assert.False(engine.Interrupt(id));                 // that execution has ended: nothing happens
        bool hitOther = true;
        engine.SetFunction("lateInterrupt", () => { hitOther = engine.Interrupt(id); });
        Assert.Equal(2, engine.Evaluate("lateInterrupt(); 2").AsInt32());   // a later execution is not stopped
        Assert.False(hitOther);
        engine.SetFunction("selfInterrupt", () => engine.Interrupt(engine.CurrentExecutionId));
        Assert.Throws<OperationCanceledException>(() => engine.Evaluate("selfInterrupt(); 3"));
    }

    [Fact]
    public void ExecutionScopeEvaluateCompilesUnderTheScopeTimeout()
    {
        var src = string.Concat(Enumerable.Repeat("x = 1 + 2;\n", 600_000));
        using var engine = new JsEngine();
        using var exec = engine.BeginExecution(new JsExecutionOptions { Timeout = TimeSpan.FromMilliseconds(30) });
        var ex = Assert.Throws<JsLimitExceededException>(() => exec.Evaluate(src));
        Assert.Equal(JsLimitKind.Timeout, ex.Kind);
    }

    [Fact]
    public void HostCompileLimitsAlsoBindRuntimeCompiledCode()
    {
        using var engine = new JsEngine(new JsEngineOptions { CompileOptions = new JsCompileOptions { MaxNodes = 2_000, MaxNestingDepth = 50 } });
        engine.SetValue("big", JsValue.From(string.Concat(Enumerable.Repeat("x = 1 + 2;", 2_000))));
        engine.SetValue("deep", JsValue.From(new string('[', 200) + "1" + new string(']', 200)));
        var r = engine.Evaluate("""
            const out = [];
            for (const f of [() => eval(big), () => new Function(big), () => eval(deep), () => Function('return ' + deep)]) {
              try { f(); out.push('ok'); } catch (e) { out.push(e.name); }
            }
            out.join()
            """);
        Assert.Equal("SyntaxError,SyntaxError,SyntaxError,SyntaxError", r.ToString());
    }

    [Theory]
    [InlineData("'x'.padEnd(2e8).length")]
    [InlineData("'x'.padStart(2e8).length")]
    [InlineData("'x'.repeat(1e8).length")]
    [InlineData("let b = 'y'.repeat(1e6); for (let i = 0; i < 1000; i++) b.concat(b); 1")]
    [InlineData("'ab'.repeat(5e6).replaceAll('a', 'xxxxxxxxxx').length")]
    [InlineData("Array(3e7).join('ab').length")]
    [InlineData("JSON.stringify(Array(3e6).fill('xxxxxxxx')).length")]
    [InlineData("'x'.repeat(1e5).replace(/x/g, 'y'.repeat(2000)).length")]
    [InlineData("'x'.repeat(1e5).replaceAll(/x/g, 'y'.repeat(2000)).length")]
    [InlineData("'x'.repeat(1e5).replace(/x/g, () => 'y'.repeat(2000)).length")]
    [InlineData("JSON.stringify('\\u0001'.repeat(5e6)).length")]
    [InlineData("const a = []; a.length = 2 ** 32 - 1; JSON.stringify(a).length")]
    [InlineData("const a = []; a.length = 2 ** 32 - 1; a.toLocaleString().length")]
    [InlineData("encodeURIComponent('é'.repeat(1e7)).length")]
    [InlineData("escape('é'.repeat(1e7)).length")]
    [InlineData("new Uint8Array(1e7).join(',').length")]
    [InlineData("let s = 'abc'.repeat(1e6), n = 0; for (let i = 0; i < 100; i++) n += s.toUpperCase().length; n")]
    [InlineData("Iterator.from({ next() { return { done: false, value: 'xxxxxxxx' } } }).take(1e7).join('').length")]
    public void LargeStringOperationsAreChargedBeforeTheyAllocate(string source)
    {
        using var engine = new JsEngine(new JsEngineOptions { Limits = new JsLimits { MaxTrackedBytes = 16 * 1024 * 1024 } });
        // this thread only: other tests allocate concurrently (the engine runs on the calling thread)
        long before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<JsLimitExceededException>(() => engine.Evaluate(source));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(JsLimitKind.TrackedBytes, ex.Kind);
        Assert.True(allocated < 200L * 1024 * 1024, $"allocated {allocated / 1048576} MB before the 16 MB budget stopped it");
    }

    [Fact]
    public void DtoConversionBudgetCountsStringsAndSparseArrays()
    {
        using var engine = new JsEngine(new JsEngineOptions { Limits = new JsLimits { MaxOutputBytes = 1000 } });
        var big = engine.Evaluate("'s'.repeat(5e6)");
        using (var exec = engine.BeginExecution())
        {
            var ex = Assert.Throws<JsLimitExceededException>(() => exec.ToObject(big, JsTypeInfo.String));
            Assert.Equal(JsLimitKind.OutputBytes, ex.Kind);
        }
        using var e2 = new JsEngine();
        var sparse = e2.Evaluate("const a = []; a.length = 2e8; a");
        var huge = e2.Evaluate("const b = []; b.length = 2 ** 32 - 1; b");
        using var exec2 = e2.BeginExecution();
        var sw = Stopwatch.StartNew();
        Assert.ThrowsAny<Exception>(() => exec2.ToObject(sparse, JsTypeInfo.List(JsTypeInfo.Int32)));
        Assert.Throws<JsConversionException>(() => exec2.ToObject(huge, JsTypeInfo.List(JsTypeInfo.Int32)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    [Fact]
    public void RegisteredSymbolsOfAnotherEngineAreRejected()
    {
        using var a = new JsEngine();
        using var b = new JsEngine();
        var sym = a.Evaluate("Symbol.for('k')");
        Assert.Throws<ArgumentException>(() => b.SetValue("s", sym));
        var wellKnown = a.Evaluate("Symbol.iterator");
        b.SetValue("w", wellKnown);   // well-known symbols are shared by all engines
        Assert.Equal("true", b.Evaluate("w === Symbol.iterator").ToString());
    }

    [Fact]
    public void ConcatenatedStringsCanBeSharedAcrossEnginesAndThreads()
    {
        // a concatenated string (a rope until first read) may be used by several engines and the host at once
        using var a = new JsEngine();
        var errors = new List<Exception>();
        for (int round = 0; round < 200; round++)
        {
            var rope = a.Evaluate("'p'.repeat(300) + 'q'.repeat(300) + String(Math.random())");
            var threads = Enumerable.Range(0, 4).Select(i => new Thread(() =>
            {
                try
                {
                    if (i % 2 == 0) { using var b = new JsEngine(); b.SetValue("s", rope); b.Evaluate("s.length + s.charCodeAt(5)"); }
                    else _ = rope.AsString().Length;
                }
                catch (Exception ex) { lock (errors) errors.Add(ex); }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());
        }
        Assert.Empty(errors);
    }

    [Fact]
    public async Task DisposingAPluginDuringAnInvocationLetsItComplete()
    {
        var module = JsCompiler.CompileModule("export async function transform(i) { await wait(); return { text: i.prompt, count: i.count } }");
        var release = new TaskCompletionSource();
        var plugin = JsPlugin.Create(module, "transform", ApiTests.InputInfo, ApiTests.OutputInfo, new JsPluginOptions
        {
            ConfigureEngine = e => e.SetAsyncFunction("wait", async (CancellationToken ct) => { await release.Task; return 0; }),
        });
        var call = plugin.InvokeAsync(new ApiTests.PluginInput("x", 1)).AsTask();
        await Task.Delay(50);
        plugin.Dispose();
        release.SetResult();
        Assert.Equal(new ApiTests.PluginOutput("x", 1), await call);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await plugin.InvokeAsync(new ApiTests.PluginInput("y", 2)));
    }

    [Fact]
    public void ConversionBudgetsCountPrimitiveValuesAndHostStrings()
    {
        using var engine = new JsEngine(new JsEngineOptions { Limits = new JsLimits { MaxOutputBytes = 1000, MaxTrackedBytes = 4 * 1024 * 1024 } });
        using (var exec = engine.BeginExecution())
        {
            var ex = Assert.Throws<JsLimitExceededException>(() => exec.FromObject(Enumerable.Range(0, 100_000).ToList(), JsTypeInfo.List(JsTypeInfo.Int32)));
            Assert.Equal(JsLimitKind.OutputBytes, ex.Kind);
        }
        using var e2 = new JsEngine(new JsEngineOptions { Limits = new JsLimits { MaxTrackedBytes = 4 * 1024 * 1024 } });
        e2.SetFunction("big", () => new string('h', 5_000_000));
        var ex2 = Assert.Throws<JsLimitExceededException>(() => e2.Evaluate("big().length"));
        Assert.Equal(JsLimitKind.TrackedBytes, ex2.Kind);
    }
}
