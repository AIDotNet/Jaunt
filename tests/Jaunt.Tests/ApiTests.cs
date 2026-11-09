using System.Text;
using Jaunt;
using Xunit;

namespace Jaunt.Tests;

public class ApiTests
{
    [Fact]
    public void EnginesRunningASharedScriptAreCollected()
    {
        // a compiled script shared by many engines must not keep them alive (per-realm feedback must not be rooted
        // through the long-lived script)
        var script = JsCompiler.CompileScript("var o = { a: 1 }; function f(x) { return x.a + [1, 2].length; } f(o) + f(o)");
        var script2 = JsCompiler.CompileScript("f(o) + [3].concat([4]).length");
        var refs = new List<WeakReference>();
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        void Run()
        {
            for (int i = 0; i < 20; i++)
            {
                var engine = new JsEngine();
                engine.Evaluate(script);
                engine.Evaluate(script2);
                engine.Evaluate(script);
                // source compiled at run time goes through the per-thread parser cache, which must not keep the engine
                engine.Evaluate("eval('1 + 1') + new Function('return 1')()");
                refs.Add(new WeakReference(engine));
                engine.Dispose();
            }
        }
        Run();
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        GC.KeepAlive(script);
        GC.KeepAlive(script2);
        Assert.True(refs.Count(r => r.IsAlive) == 0, $"{refs.Count(r => r.IsAlive)} of {refs.Count} engines are still alive");
    }

    [Fact]
    public void HtmlDdaObjectBehavesLikeDocumentAll()
    {
        using var engine = new JsEngine();
        engine.SetFunction("makeDda", static (ref JsCallContext c) => c.CreateHtmlDdaObject(), 0);
        var r = engine.Evaluate("var all = makeDda(); [typeof all, !all, all == null, all == undefined, all === undefined, (all ?? 1) === all, all()].map(String).join()").ToString();
        Assert.Equal("undefined,true,true,true,false,true,null", r);
    }

    [Fact]
    public void SharedMemoryAndAtomicsWorkAcrossEngines()
    {
        using var main = new JsEngine();
        JsSharedMemory? mem = null;
        main.SetFunction("share", (ref JsCallContext c) => { mem = c.GetSharedMemory(c[0]); return JsValue.Undefined; }, 1);
        main.Evaluate("var i32 = new Int32Array(new SharedArrayBuffer(16)); share(i32.buffer);");
        Assert.Equal(16, mem!.ByteLength);
        Assert.Contains("cannot block", Assert.Throws<JsException>(() => main.Evaluate("Atomics.wait(i32, 0, 0, 0)")).Message);
        string? result = null;
        var worker = new Thread(() =>
        {
            using var engine = new JsEngine(new JsEngineOptions { CanBlock = true, Limits = new JsLimits { Timeout = TimeSpan.FromSeconds(20) } });
            engine.SetFunction("take", (ref JsCallContext c) => c.CreateSharedArrayBuffer(mem), 0);
            result = engine.Evaluate("""
                var a = new Int32Array(take());
                var t = Atomics.wait(a, 0, 1, 0);          // not-equal
                Atomics.add(a, 1, 1);
                [t, Atomics.wait(a, 0, 0), a[2], Atomics.wait(a, 3, 0, 10)].join()
                """).ToString();
        });
        worker.Start();
        main.Evaluate("while (Atomics.load(i32, 1) !== 1) {} i32[2] = 7; while (Atomics.notify(i32, 0) === 0) {}");
        Assert.True(worker.Join(TimeSpan.FromSeconds(20)));
        Assert.Equal("not-equal,ok,7,timed-out", result);
    }

    [Fact]
    public void CreateRealmGivesSeparateIntrinsicsSharingObjects()
    {
        using var engine = new JsEngine();
        engine.SetFunction("newRealm", static (ref JsCallContext c) => c.CreateRealm(), 0);
        engine.SetFunction("evalIn", static (ref JsCallContext c) => c.EvaluateScript(c[0], c.GetString(1) ?? ""), 2);
        var r = engine.Evaluate("""
            var g = newRealm();
            var OArray = evalIn(g, "var x = 41; Array");
            var arr = new OArray(1, 2);
            var C = evalIn(g, "(class extends Object { constructor() { return 1; } })");
            var err; try { new C(); } catch (e) { err = e; }
            [OArray !== Array, Array.isArray(arr), Object.getPrototypeOf(arr) === g.Array.prototype,
             !(arr instanceof Array), g.x + 1, Symbol.for("k") === g.Symbol.for("k"),
             Object.getPrototypeOf(Reflect.construct(Map, [], evalIn(g, "var F = function () {}; F.prototype = null; F"))) === g.Map.prototype,
             err instanceof TypeError, g.eval("this") === g].join()
            """).ToString();
        Assert.Equal("true,true,true,true,42,true,true,true,true", r);
    }

    [Fact]
    public void SimpleEvaluateWithHostValuesAndFunctions()
    {
        using var engine = new JsEngine();
        engine.SetValue("price", 100.0);
        engine.SetValue("quantity", 2);
        engine.SetFunction("tax", (double amount) => amount * 0.13);
        double total = engine.Evaluate("price * quantity + tax(price * quantity)").AsNumber();
        Assert.Equal(226.0, total, 6);
    }

    [Fact]
    public void CompiledScriptIsSharedAcrossEnginesWithPrivateState()
    {
        var script = JsCompiler.CompileScript("var calls = (globalThis.calls || 0) + 1; globalThis.calls = calls; price * quantity + calls",
            new JsCompileOptions { SourceName = "pricing.js" });
        var results = new double[16];
        Parallel.For(0, 16, i =>
        {
            using var e = new JsEngine();
            e.SetValue("price", 10.0 * i);
            e.SetValue("quantity", 2);
            e.Evaluate(script);
            results[i] = e.Evaluate(script).AsNumber();
        });
        for (int i = 0; i < 16; i++) Assert.Equal(20.0 * i + 2, results[i]);
    }

    [Fact]
    public void RawCallbackAndCallContext()
    {
        using var engine = new JsEngine();
        engine.SetFunction("sum", static (ref JsCallContext call) => JsValue.From(call.GetNumber(0) + call.GetNumber(1)));
        engine.SetFunction("makePoint", static (ref JsCallContext call) =>
        {
            var o = call.CreateObject();
            call.SetProperty(o, "x", call[0]);
            call.SetProperty(o, "y", call[1]);
            return o;
        });
        engine.SetFunction("apply", static (ref JsCallContext call) => call.Call(call[0], JsValue.Undefined, call.Arguments[1..]));
        Assert.Equal(7.0, engine.Evaluate("sum(3, 4)").AsNumber());
        Assert.Equal(3.0, engine.Evaluate("makePoint(1, 2).x + makePoint(1, 2).y").AsNumber());
        Assert.Equal(10.0, engine.Evaluate("apply((a, b) => a * b, 2, 5)").AsNumber());
        Assert.Equal("TypeError", engine.Evaluate("try { sum('a', 1) } catch (e) { e.name }").AsString());
    }

    [Fact]
    public void TypedHostFunctionsValidateArguments()
    {
        using var engine = new JsEngine();
        engine.SetFunction("repeat", (string s, int n) => string.Concat(Enumerable.Repeat(s, n)));
        engine.SetFunction("flag", (bool b) => !b);
        engine.SetFunction("big", () => 9007199254740991L);
        Assert.Equal("abab", engine.Evaluate("repeat('ab', 2)").AsString());
        Assert.False(engine.Evaluate("flag(true)").AsBoolean());
        Assert.Equal("TypeError", engine.Evaluate("try { repeat('a', 1.5) } catch (e) { e.name }").AsString());
        Assert.Equal(9007199254740991.0, engine.Evaluate("big()").AsNumber());
        Assert.Throws<NotSupportedException>(() => engine.SetFunction("bad", (decimal d) => d));
    }

    [Fact]
    public void HostExceptionsBecomeCatchableErrorsWithHiddenMessages()
    {
        using var engine = new JsEngine();
        engine.SetFunction("fail", (Func<double>)(() => throw new InvalidOperationException("secret detail")));
        Assert.Equal("Host function 'fail' failed.", engine.Evaluate("try { fail() } catch (e) { e.message }").AsString());
        var ex = Assert.Throws<JsException>(() => engine.Evaluate("fail()"));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.False(engine.IsFaulted);
        Assert.Equal(1.0, engine.Evaluate("1").AsNumber());
    }

    [Fact]
    public void JsExceptionDoesNotFaultEngineAndStateIsNotRolledBack()
    {
        using var engine = new JsEngine();
        var ex = Assert.Throws<JsException>(() => engine.Evaluate("globalThis.before = 1; throw new TypeError('bad')"));
        Assert.Equal("TypeError", ex.Name);
        Assert.Equal("bad", ex.ErrorMessage);
        Assert.False(engine.IsFaulted);
        Assert.Equal(1.0, engine.Evaluate("before").AsNumber());
    }

    [Fact]
    public void ValuesFromAnotherEngineAreRejected()
    {
        using var a = new JsEngine();
        using var b = new JsEngine();
        var obj = a.Evaluate("({ x: 1 })");
        Assert.Throws<ArgumentException>(() => b.SetValue("o", obj));
        var fn = a.GetFunction("Array");
        Assert.Throws<ArgumentException>(() => b.Invoke(fn));
    }

    [Fact]
    public void DefaultJsValueIsUndefinedAndAccessorsDoNotCoerce()
    {
        Assert.True(default(JsValue).IsUndefined);
        Assert.Throws<InvalidCastException>(() => JsValue.From("1").AsNumber());
        Assert.Throws<InvalidCastException>(() => JsValue.From(1.5).AsInt32());
        Assert.Throws<ArgumentOutOfRangeException>(() => JsValue.From(long.MaxValue));
        Assert.Equal(3, JsValue.From(3.0).AsInt32());
    }

    [Fact]
    public void ConcurrentEntryFailsFast()
    {
        using var engine = new JsEngine();
        Exception? inner = null;
        engine.SetFunction("reenter", () =>
        {
            try { engine.Evaluate("1"); }
            catch (Exception ex) { inner = ex; }
            return 0.0;
        });
        engine.Evaluate("reenter()");
        Assert.IsType<InvalidOperationException>(inner);
    }

    [Fact]
    public void InfiniteLoopIsStoppedByInstructionBudget()
    {
        using var engine = new JsEngine(new JsEngineOptions { Limits = new JsLimits { MaxInstructions = 100_000 } });
        var ex = Assert.Throws<JsLimitExceededException>(() => engine.Evaluate("while (true) {}"));
        Assert.Equal(JsLimitKind.Instructions, ex.Kind);
        Assert.True(engine.IsFaulted);
        Assert.Throws<JsEngineFaultedException>(() => engine.Evaluate("1"));
    }

    [Fact]
    public void TimeoutStopsExecutionAndJsCannotCatchIt()
    {
        using var engine = new JsEngine(new JsEngineOptions { Limits = new JsLimits { Timeout = TimeSpan.FromMilliseconds(50) } });
        var ex = Assert.Throws<JsLimitExceededException>(() => engine.Evaluate("for (;;) { try { while (true) {} } catch (e) {} finally { } }"));
        Assert.Equal(JsLimitKind.Timeout, ex.Kind);
    }

    [Fact]
    public void CancellationTokenStopsExecution()
    {
        using var engine = new JsEngine();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        Assert.Throws<OperationCanceledException>(() => engine.Evaluate("while (true) {}", cts.Token));
        Assert.True(engine.IsFaulted);
    }

    [Fact]
    public void InterruptStopsOnlyTheActiveExecution()
    {
        using var engine = new JsEngine();
        engine.Interrupt(); // idle: must not leave a sticky flag
        Assert.Equal(2.0, engine.Evaluate("1 + 1").AsNumber());
        engine.SetFunction("selfInterrupt", () => { engine.Interrupt(); return 0.0; });
        Assert.Throws<OperationCanceledException>(() => engine.Evaluate("selfInterrupt(); while (true) {}"));
    }

    [Fact]
    public void TrackedBytesBudgetStopsAllocationBombs()
    {
        using var engine = new JsEngine(new JsEngineOptions { Limits = new JsLimits { MaxTrackedBytes = 4 * 1024 * 1024 } });
        var ex = Assert.Throws<JsLimitExceededException>(() => engine.Evaluate("const a = []; while (true) a.push({ x: 1, y: 'abc' })"));
        Assert.Equal(JsLimitKind.TrackedBytes, ex.Kind);
        using var e2 = new JsEngine(new JsEngineOptions { Limits = new JsLimits { MaxTrackedBytes = 4 * 1024 * 1024 } });
        Assert.Throws<JsLimitExceededException>(() => e2.Evaluate("let s = 'x'; while (true) s += s"));
    }

    [Theory]
    [InlineData("Array.prototype.forEach.call({ length: 4294967295 }, () => {})")]
    [InlineData("Array.prototype.reduce.call({ length: 4294967295, 0: 1 }, (a) => a)")]
    [InlineData("Array.prototype.map.call({ length: 4294967295 }, x => x)")]
    [InlineData("Array.prototype.sort.call({ length: 4294967295 })")]
    [InlineData("Function.prototype.apply.call(Math.max, null, { length: 90000000 })")]
    public void BuiltinLoopsOverHugeArrayLikesAreBudgeted(string source)
    {
        using var engine = new JsEngine(new JsEngineOptions { Limits = new JsLimits { Timeout = TimeSpan.FromMilliseconds(200) } });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<JsLimitExceededException>(() => engine.Evaluate(source));
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData("Function.prototype.apply.call(Math.max, null, { length: 90000000 })")]
    [InlineData("Reflect.apply(Math.max, null, { length: 90000000 })")]
    public void ArgumentListsOfHugeArrayLikesGrowWithTheBudget(string source)
    {
        // the list must not be allocated for the claimed length before the first budget check
        using (var engine = new JsEngine(new JsEngineOptions { Limits = new JsLimits { MaxInstructions = 10_000 } }))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<JsLimitExceededException>(() => engine.Evaluate(source));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < 32 * 1024 * 1024, $"allocated {allocated} bytes");
        }
        using (var engine = new JsEngine(new JsEngineOptions { Limits = new JsLimits { MaxTrackedBytes = 4 * 1024 * 1024 } }))
        {
            var ex = Assert.Throws<JsLimitExceededException>(() => engine.Evaluate(source));
            Assert.Equal(JsLimitKind.TrackedBytes, ex.Kind);
        }
    }

    [Fact]
    public void ArgumentListsFromArrayLikesKeepTheirValues()
    {
        using var engine = new JsEngine();
        Assert.Equal("70000,69999,3", engine.Evaluate("""
            const big = { length: 70000 };
            for (let i = 0; i < 70000; i++) big[i] = i;
            const seen = Reflect.apply((...a) => a, null, big);
            [seen.length, Math.max.apply(null, big), Reflect.apply((a, b, c) => c, null, { length: 3, 2: 3 })].join()
            """).AsString());
        Assert.Equal(0.0, engine.Evaluate("Reflect.apply((...a) => a.length, null, { length: 0 })").AsNumber());
    }

    [Fact]
    public void RegExpMethodsRejectPrimitiveReceiversSafely()
    {
        using var engine = new JsEngine();
        Assert.Equal("TypeError", engine.Evaluate("Object.prototype.test = RegExp.prototype.test; try { '.'.test('x') } catch (e) { e.name }").AsString());
        Assert.Equal("TypeError", engine.Evaluate("try { RegExp.prototype[Symbol.match].call('a', 'b') } catch (e) { e.name }").AsString());
        Assert.False(engine.IsFaulted);
    }

    [Fact]
    public void ExecutionScopeSharesOneBudgetAcrossOperations()
    {
        using var engine = new JsEngine();
        var module = JsCompiler.CompileModule("export function transform(input) { return { text: input.prompt.trim(), count: input.count + 1 } }");
        var instance = engine.LoadModule(module);
        var transform = instance.GetExport("transform");
        byte[] response;
        using (var execution = engine.BeginExecution(new JsExecutionOptions { Timeout = TimeSpan.FromSeconds(5), MaxInstructions = 1_000_000, MaxOutputBytes = 1024 }))
        {
            var input = execution.ParseJson(Encoding.UTF8.GetBytes("{\"prompt\": \"  hi  \", \"count\": 1}"));
            var output = execution.Invoke(transform, input);
            response = execution.ToJsonUtf8(output);
            execution.Complete();
            Assert.Throws<InvalidOperationException>(() => engine.Evaluate("1"));
        }
        Assert.Equal("{\"text\":\"hi\",\"count\":2}", Encoding.UTF8.GetString(response));
        Assert.Equal(1.0, engine.Evaluate("1").AsNumber());
    }

    [Fact]
    public void OutputBudgetIsEnforced()
    {
        using var engine = new JsEngine();
        var big = engine.Evaluate("Array.from({ length: 1000 }, (_, i) => 'item' + i)");
        using var execution = engine.BeginExecution(new JsExecutionOptions { MaxOutputBytes = 100 });
        var ex = Assert.Throws<JsLimitExceededException>(() => execution.ToJsonUtf8(big));
        Assert.Equal(JsLimitKind.OutputBytes, ex.Kind);
    }

    [Fact]
    public void JsonChannelFollowsStringifySemantics()
    {
        using var engine = new JsEngine();
        var v = engine.Evaluate("({ a: 1, b: undefined, c: () => 1, d: [undefined, NaN], e: { toJSON() { return 'custom' } }, f: new Date(0) })");
        Assert.Equal("{\"a\":1,\"d\":[null,null],\"e\":\"custom\",\"f\":\"1970-01-01T00:00:00.000Z\"}", engine.ToJsonString(v));
        Assert.Throws<JsConversionException>(() => engine.ToJsonString(JsValue.Undefined));
        var cyc = engine.Evaluate("const o = {}; o.self = o; o");
        Assert.Throws<JsException>(() => engine.ToJsonString(cyc));
        Assert.Throws<JsException>(() => engine.ParseJson("{bad json}"));
        var parsed = engine.ParseJson("{\"__proto__\": 1, \"x\": [1, 2.5e3, \"\\u0041\"]}");
        Assert.Equal("{\"__proto__\":1,\"x\":[1,2500,\"A\"]}", engine.ToJsonString(parsed));
    }

    public sealed record PluginInput(string Prompt, int Count);
    public sealed record PluginOutput(string Text, int Count);

    internal static readonly JsTypeInfo<PluginInput> InputInfo = JsTypeInfo.Object<PluginInput>()
        .Property("prompt", x => x.Prompt, JsTypeInfo.String!)
        .Property("count", x => x.Count, JsTypeInfo.Int32)
        .Factory(r => new PluginInput(r.Required("prompt", JsTypeInfo.String)!, r.Required("count", JsTypeInfo.Int32)))
        .Build();

    internal static readonly JsTypeInfo<PluginOutput> OutputInfo = JsTypeInfo.Object<PluginOutput>()
        .Property("text", x => x.Text, JsTypeInfo.String!)
        .Property("count", x => x.Count, JsTypeInfo.Int32)
        .Factory(r => new PluginOutput(r.Required("text", JsTypeInfo.String)!, r.Required("count", JsTypeInfo.Int32)))
        .Build();

    [Fact]
    public void DtoMappingIsDataOnly()
    {
        using var engine = new JsEngine();
        using var execution = engine.BeginExecution();
        var input = execution.FromObject(new PluginInput(" hello ", 2), InputInfo);
        var fn = JsFunction.FromValue(engine, execution.Evaluate("(i) => ({ text: i.prompt.trim(), count: i.count * 2 })"));
        var output = execution.ToObject(execution.Invoke(fn, input), OutputInfo);
        Assert.Equal(new PluginOutput("hello", 4), output);
        var getter = execution.Evaluate("({ get text() { return 'x' }, count: 1 })");
        var ex = Assert.Throws<JsConversionException>(() => execution.ToObject(getter, OutputInfo));
        Assert.Contains("accessor", ex.Message);
        var missing = execution.Evaluate("({ count: 1 })");
        Assert.Throws<JsConversionException>(() => execution.ToObject(missing, OutputInfo));
        var wrongType = execution.Evaluate("({ text: 'a', count: 1.5 })");
        var ex2 = Assert.Throws<JsConversionException>(() => execution.ToObject(wrongType, OutputInfo));
        Assert.Equal("$.count", ex2.Path);
        execution.Complete();
    }

    [Fact]
    public void HardenedProfileFreezesIntrinsicsAndBlocksCodeGeneration()
    {
        using var engine = new JsEngine(new JsEngineOptions { Profile = JsProfile.Hardened });
        Assert.Equal("EvalError", engine.Evaluate("try { eval('1') } catch (e) { e.name }").AsString());
        Assert.Equal("EvalError", engine.Evaluate("try { Function('return 1')() } catch (e) { e.name }").AsString());
        Assert.Equal("EvalError", engine.Evaluate("try { (function(){}).constructor('return 1') } catch (e) { e.name }").AsString());
        Assert.Equal("EvalError", engine.Evaluate("try { (function*(){}).constructor('yield 1') } catch (e) { e.name }").AsString());
        Assert.Equal("EvalError", engine.Evaluate("try { (async function(){}).constructor('') } catch (e) { e.name }").AsString());
        Assert.True(engine.Evaluate("Array.prototype.push = 1; typeof Array.prototype.push === 'function' && Object.isFrozen(Object.prototype)").AsBoolean());
        using var standard = new JsEngine();
        Assert.Equal(3.0, standard.Evaluate("eval('1 + 2')").AsNumber());
    }

    [Fact]
    public void StandardProfileHasMutableIntrinsicsPerEngine()
    {
        using var a = new JsEngine();
        using var b = new JsEngine();
        a.Evaluate("Array.prototype.sum = function () { return this.reduce((x, y) => x + y, 0) }");
        Assert.Equal(6.0, a.Evaluate("[1, 2, 3].sum()").AsNumber());
        Assert.Equal("undefined", b.Evaluate("typeof [].sum").AsString());
    }

    [Fact]
    public void ModulesLiveBindingsCyclesAndNamespaces()
    {
        var resolver = new JsDictionaryModuleResolver()
            .Add("counter.mjs", "export let count = 0; export function inc() { count++ } export default 'dflt';")
            .Add("a.mjs", "import { b } from './b.mjs'; export const a = 'A'; export function getB() { return b }")
            .Add("b.mjs", "import { a } from './a.mjs'; export const b = 'B'; export function getA() { return a }")
            .Add("reexport.mjs", "export * from './counter.mjs'; export { a as renamed } from './a.mjs'; export * as ns from './b.mjs';");
        var main = JsCompiler.CompileModule("""
            import def, { count, inc } from 'counter.mjs';
            import * as counter from 'counter.mjs';
            import { getB } from 'a.mjs';
            import { renamed, ns, count as c2 } from 'reexport.mjs';
            inc(); inc();
            export const result = [def, count, counter.count, getB(), renamed, ns.b, c2].join();
            export let mutable = 1;
            export function bump() { mutable++ }
            """, new JsCompileOptions { SourceName = "main.mjs" });
        using var engine = new JsEngine();
        var instance = engine.LoadModule(main, resolver);
        Assert.Equal("dflt,2,2,B,A,B,2", instance.GetValue("result").AsString());
        var fixedFn = instance.GetFunction("bump");
        engine.Invoke(fixedFn);
        Assert.Equal(2.0, instance.GetValue("mutable").AsNumber());
        Assert.Equal(new[] { "bump", "mutable", "result" }, instance.ExportNames.ToArray());
    }

    [Fact]
    public void SelfReferencingNamespaceExportDoesNotRecurse()
    {
        var resolver = new JsDictionaryModuleResolver().Add("self.mjs", "export * as me from 'self.mjs'; export const v = 1;");
        using var engine = new JsEngine();
        var m = engine.LoadModule(JsCompiler.CompileModule("import { me } from 'self.mjs'; export const ok = me.me.me.v === 1;"), resolver);
        Assert.True(m.GetValue("ok").AsBoolean());
    }

    [Fact]
    public void ModuleLoadFailures()
    {
        using var engine = new JsEngine();
        var missing = JsCompiler.CompileModule("import x from 'nope'; export default x;");
        Assert.Throws<JsCompileException>(() => engine.LoadModule(missing));
        var resolver = new JsDictionaryModuleResolver().Add("dep", "export const y = 1;");
        var badImport = JsCompiler.CompileModule("import { z } from 'dep';");
        using var e2 = new JsEngine();
        var ex = Assert.Throws<JsException>(() => e2.LoadModule(badImport, resolver));
        Assert.Equal("SyntaxError", ex.Name);
        var tla = JsCompiler.CompileModule("await 1; export const v = 2;");
        Assert.True(tla.HasTopLevelAwait);
        using var e3 = new JsEngine();
        Assert.Throws<JsAsyncRequiredException>(() => e3.LoadModule(tla));
    }

    [Fact]
    public async Task TopLevelAwaitAndDynamicImport()
    {
        var resolver = new JsDictionaryModuleResolver()
            .Add("lazy.mjs", "export const lazy = 'loaded';")
            .Add("main.mjs", "const m = await import('lazy.mjs'); export const v = m.lazy + '!'; ");
        using var engine = new JsEngine();
        var instance = await engine.LoadModuleAsync(JsCompiler.CompileModule("export { v } from 'main.mjs';"), resolver);
        Assert.Equal("loaded!", instance.GetValue("v").AsString());
    }

    [Fact]
    public async Task AsyncFunctionsAndHostAsyncBindings()
    {
        using var engine = new JsEngine();
        engine.SetAsyncFunction("lookup", async (string key, CancellationToken token) =>
        {
            await Task.Delay(5, token);
            return "value:" + key;
        });
        var v = await engine.EvaluateAsync("(async () => { const a = await lookup('x'); const b = await lookup('y'); return a + ',' + b })()");
        Assert.Equal("value:x,value:y", v.AsString());
        var all = await engine.EvaluateAsync("Promise.all([lookup('1'), lookup('2'), 3]).then(r => r.join('|'))");
        Assert.Equal("value:1|value:2|3", all.AsString());
        var caught = await engine.EvaluateAsync("(async () => { try { await Promise.reject(new Error('no')) } catch (e) { return e.message } })()");
        Assert.Equal("no", caught.AsString());
    }

    [Fact]
    public void SyncEntryRejectsAsyncHostFunctionBeforeInvokingIt()
    {
        using var engine = new JsEngine();
        int invoked = 0;
        engine.SetAsyncFunction("work", (CancellationToken ct) => { invoked++; return new ValueTask<double>(1.0); });
        Assert.Throws<JsAsyncRequiredException>(() => engine.Evaluate("globalThis.side = 1; work()"));
        Assert.Equal(0, invoked);
        Assert.False(engine.IsFaulted);
        Assert.Equal(1.0, engine.Evaluate("side").AsNumber());
    }

    [Fact]
    public async Task UnjoinedHostWorkFaultsTheEngine()
    {
        using var engine = new JsEngine();
        var gate = new TaskCompletionSource<double>();
        CancellationToken observed = default;
        engine.SetAsyncFunction("slow", async (CancellationToken ct) => { observed = ct; return await gate.Task.WaitAsync(ct); });
        engine.SetAsyncFunction("fast", (CancellationToken ct) => new ValueTask<double>(1.0));
        var ex = await Assert.ThrowsAsync<JsEngineFaultedException>(async () => await engine.EvaluateAsync("Promise.race([slow(), fast()])"));
        Assert.Equal("UnjoinedAsyncWork", ex.Code);
        Assert.True(engine.IsFaulted);
        Assert.True(observed.IsCancellationRequested);
    }

    [Fact]
    public async Task NeverSettledPromiseIsBoundedByTimeout()
    {
        using var engine = new JsEngine();
        var ex = await Assert.ThrowsAsync<JsLimitExceededException>(async () =>
            await engine.EvaluateAsync("new Promise(() => {})", new JsExecutionOptions { Timeout = TimeSpan.FromMilliseconds(50) }));
        Assert.Equal(JsLimitKind.Timeout, ex.Kind);
    }

    [Fact]
    public void PendingPromiseFromSyncEntryCanBeAwaitedLater()
    {
        using var engine = new JsEngine();
        var p = engine.Evaluate("globalThis.resolveIt = null; new Promise(r => { globalThis.resolveIt = r })");
        Assert.True(p.IsPromise);
        engine.Evaluate("resolveIt(42)");
        var v = engine.AwaitAsync(p).AsTask().GetAwaiter().GetResult();
        Assert.Equal(42.0, v.AsNumber());
    }

    [Fact]
    public void UnhandledRejectionsAreReportedAtRootCheckpoint()
    {
        var reasons = new List<string>();
        using var engine = new JsEngine(new JsEngineOptions { UnhandledRejection = r => reasons.Add(r.Reason) });
        engine.Evaluate("Promise.reject(new Error('lost')); Promise.reject(1).catch(() => {})");
        Assert.Equal(new[] { "Error: lost" }, reasons);
        Assert.Equal(1, engine.GetStatistics().UnhandledRejections);
    }

    [Fact]
    public async Task PluginUsesFreshRealmPerInvocation()
    {
        var module = JsCompiler.CompileModule("""
            let calls = 0;
            export function transform(input) {
                calls++;
                globalThis.leak = (globalThis.leak || 0) + 1;
                return { text: input.prompt.trim() + ':' + calls + ':' + globalThis.leak, count: input.count };
            }
            """, new JsCompileOptions { SourceName = "plugin.mjs" });
        await using var plugin = JsPlugin.Create(module, "transform", InputInfo, OutputInfo, new JsPluginOptions { MaxConcurrency = 4 });
        var outputs = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => plugin.InvokeAsync(new PluginInput(" p" + i + " ", i)).AsTask()));
        for (int i = 0; i < 20; i++) Assert.Equal(new PluginOutput("p" + i + ":1:1", i), outputs[i]);
        Assert.Equal(new PluginOutput("x:1:1", 9), plugin.Invoke(new PluginInput("x", 9)));
    }

    [Fact]
    public async Task PluginSupportsAsyncHostCapabilities()
    {
        var module = JsCompiler.CompileModule("export async function transform(i) { const t = await upper(i.prompt); return { text: t, count: i.count } }");
        await using var plugin = JsPlugin.Create(module, "transform", InputInfo, OutputInfo, new JsPluginOptions
        {
            ConfigureEngine = e => e.SetAsyncFunction("upper", async (string s, CancellationToken ct) => { await Task.Yield(); return s.ToUpperInvariant(); }),
        });
        Assert.Equal(new PluginOutput("ABC", 1), await plugin.InvokeAsync(new PluginInput("abc", 1)));
    }

    [Fact]
    public void ExecutionScopeAbandonedWithoutCompleteReleasesEngine()
    {
        using var engine = new JsEngine();
        using (var execution = engine.BeginExecution())
        {
            execution.Evaluate("globalThis.v = 1");
        }
        Assert.Equal(1.0, engine.Evaluate("v").AsNumber());
    }

    [Fact]
    public void DisposedEngineRejectsUse()
    {
        var engine = new JsEngine();
        var fn = engine.GetFunction("Array");
        engine.Dispose();
        engine.Dispose();
        Assert.Throws<ObjectDisposedException>(() => engine.Evaluate("1"));
        Assert.ThrowsAny<Exception>(() => engine.Invoke(fn));
    }

    [Fact]
    public void FunctionHandleDoesNotFollowReassignmentButExportDoes()
    {
        using var engine = new JsEngine();
        var m = engine.LoadModule(JsCompiler.CompileModule("export let f = () => 1; export function swap() { f = () => 2 }"));
        var fixedF = m.GetFunction("f");
        var live = m.GetExport("f");
        engine.Invoke(m.GetFunction("swap"));
        Assert.Equal(1.0, engine.Invoke(fixedF).AsNumber());
        Assert.Equal(2.0, engine.Invoke(live).AsNumber());
    }

    [Fact]
    public void StatisticsAreReported()
    {
        using var engine = new JsEngine();
        engine.Evaluate("let s = 0; for (let i = 0; i < 10000; i++) s += i;");
        var stats = engine.GetStatistics();
        Assert.True(stats.LastWorkUnits >= 10000);
        Assert.Equal(1, stats.Executions);
    }

    [Fact]
    public void ParserNestingAttackIsRejectedSafely()
    {
        var deep = new string('(', 200_000) + "1" + new string(')', 200_000);
        var ex = Assert.Throws<JsCompileException>(() => JsCompiler.CompileScript(deep));
        Assert.Equal("NestingTooDeep", ex.Code);
        var unary = new string('!', 500_000) + "x";
        Assert.Throws<JsCompileException>(() => JsCompiler.CompileScript(unary));
    }

    [Fact]
    public void ParserNestingAttacksAreRejectedOnSmallStacks()
    {
        string[] attacks =
        [
            new string('[', 100_000),
            string.Concat(Enumerable.Repeat("{a:", 100_000)),
            string.Concat(Enumerable.Repeat("x=>", 100_000)) + "1",
            string.Concat(Enumerable.Repeat("function f(){", 50_000)),
            string.Concat(Enumerable.Repeat("`${", 100_000)),
            string.Concat(Enumerable.Repeat("if(1)", 100_000)) + ";",
            string.Concat(Enumerable.Repeat("a?", 100_000)) + "b",
            string.Concat(Enumerable.Repeat("-", 200_000)) + "x",
            string.Concat(Enumerable.Repeat("class A extends (", 50_000)),
            "var [" + string.Concat(Enumerable.Repeat("[", 100_000)) + "] = 0",
            string.Concat(Enumerable.Repeat("{", 100_000)),
            string.Concat(Enumerable.Repeat("new ", 100_000)) + "x",
            string.Concat(Enumerable.Repeat("a=", 100_000)) + "1",
        ];
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                foreach (var src in attacks)
                {
                    Assert.Throws<JsCompileException>(() => JsCompiler.CompileScript(src));
                    Assert.Throws<JsCompileException>(() => JsCompiler.CompileModule(src));
                }
            }
            catch (Exception e) { failure = e; }
        }, 256 * 1024);
        t.Start();
        t.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void CompileBudgets()
    {
        Assert.Equal("SourceTooLarge", Assert.Throws<JsCompileException>(() => JsCompiler.CompileScript("1;1;1;", new JsCompileOptions { MaxSourceLength = 3 })).Code);
        Assert.Equal("TooManyNodes", Assert.Throws<JsCompileException>(() => JsCompiler.CompileScript("1+1+1+1+1+1", new JsCompileOptions { MaxNodes = 5 })).Code);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => JsCompiler.CompileScript(string.Concat(Enumerable.Repeat("a = 1;", 5000)), null, cts.Token));
    }
}
