using Jaunt;
using Xunit;

namespace Jaunt.Tests;

public class AsyncIterationTests
{
    private static string Run(string src)
    {
        using var e = new JsEngine(new JsEngineOptions { Limits = new JsLimits { Timeout = TimeSpan.FromSeconds(10) } });
        e.Evaluate("var log = [];\n" + src + "\n.catch(e => log.push('E:' + e))");
        return e.Evaluate("log.join(',')").ToString();
    }

    [Theory]
    [InlineData("(async () => { async function* g() { yield 1; yield Promise.resolve(2); yield await 3; } for await (const v of g()) log.push(v); })()", "1,2,3")]
    [InlineData("(async () => { for await (const v of [Promise.resolve('a'), 'b']) log.push(v); })()", "a,b")]
    [InlineData("(async () => { async function* g() { try { yield 1; yield 2; } finally { log.push('fin'); } } for await (const v of g()) { log.push(v); break; } })()", "1,fin")]
    [InlineData("(async () => { async function* g() { yield 1; return 9; } async function* d() { const r = yield* g(); log.push('r' + r); yield* [10, 11]; } for await (const v of d()) log.push(v); })()", "1,r9,10,11")]
    [InlineData("(async () => { async function* g() { yield 1; yield 2; } const it = g(); log.push(JSON.stringify(await it.next()), JSON.stringify(await it.return(42)), JSON.stringify(await it.next())); })()",
        "{\"value\":1,\"done\":false},{\"value\":42,\"done\":true},{\"done\":true}")]
    [InlineData("(async () => { async function* g() { try { yield 1; } catch (e) { log.push('caught ' + e); yield 2; } } const it = g(); await it.next(); log.push((await it.throw('x')).value); })()", "caught x,2")]
    [InlineData("(async () => { async function* g() { yield 1; } const it = g(); const ps = [it.next(), it.next(), it.next()]; for (const p of ps) log.push(JSON.stringify(await p)); })()",
        "{\"value\":1,\"done\":false},{\"done\":true},{\"done\":true}")]
    [InlineData("(async () => { async function* g() { return Promise.resolve(5); } log.push((await g().next()).value); })()", "5")]
    [InlineData("(async () => { async function* g() { yield Promise.reject('bad'); } try { await g().next(); } catch (e) { log.push('rejected ' + e); } })()", "rejected bad")]
    [InlineData("(async () => { try { for await (const v of { [Symbol.asyncIterator]() { return { next() { return 1; } }; } }) {} } catch (e) { log.push(e.constructor.name); } })()", "TypeError")]
    [InlineData("(async () => { let closed = 0; const src = { [Symbol.iterator]() { return { next: () => ({ value: 1, done: false }), return() { closed++; return {}; } }; } }; try { for await (const v of src) throw 'boom'; } catch (e) { log.push(e, closed); } })()", "boom,1")]
    [InlineData("(async () => { async function* g() {} log.push(Object.getPrototypeOf(g).constructor.name, Object.prototype.toString.call(g()), typeof g().next, g()[Symbol.asyncIterator]() !== undefined); })()",
        "AsyncGeneratorFunction,[object AsyncGenerator],function,true")]
    [InlineData("(async () => { const AGF = Object.getPrototypeOf(async function* () {}).constructor; const f = new AGF('a', 'yield a; yield a * 2;'); for await (const v of f(3)) log.push(v); })()", "3,6")]
    public void AsyncGeneratorsAndForAwait(string src, string expected) => Assert.Equal(expected, Run(src));

    [Fact]
    public void ModuleTopLevelForAwait()
    {
        using var e = new JsEngine();
        var m = JsCompiler.CompileModule("export const out = []; for await (const v of [1, Promise.resolve(2)]) out.push(v);", new JsCompileOptions { SourceName = "m.js" });
        var inst = e.LoadModuleAsync(m).AsTask().GetAwaiter().GetResult();
        e.SetValue("ns", inst.Namespace);
        Assert.Equal("1,2", e.Evaluate("ns.out.join()").ToString());
    }
}
