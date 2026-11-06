using Jaunt;
using Xunit;

namespace Jaunt.Tests;

/// <summary>Coverage for language and library features added after the first alpha (one assertion per feature area).</summary>
public class FeatureTests
{
    private static string Eval(string src)
    {
        using var e = new JsEngine(new JsEngineOptions { Limits = new JsLimits { Timeout = TimeSpan.FromSeconds(10) } });
        return e.Evaluate(src).ToString();
    }

    private static string RunAsync(string src)
    {
        using var e = new JsEngine(new JsEngineOptions { Limits = new JsLimits { Timeout = TimeSpan.FromSeconds(10) } });
        e.Evaluate("var log = [];\n" + src);
        return e.Evaluate("log.join(',')").ToString();
    }

    [Theory]
    // BigInt
    [InlineData("typeof 10n", "bigint")]
    [InlineData("String(2n ** 100n)", "1267650600228229401496703205376")]
    [InlineData("(-7n / 2n) + ',' + (-7n % 2n) + ',' + (5n >> 1n) + ',' + (~0n)", "-3,-1,2,-1")]
    [InlineData("[1n == 1, 1n === 1, 2n > 1, '10' == 10n, BigInt.asUintN(8, -1n)].join()", "true,false,true,true,255")]
    [InlineData("try { 1n + 1 } catch (e) { e.constructor.name }", "TypeError")]
    [InlineData("new BigInt64Array([1n, -2n]).join() + '|' + new DataView(new ArrayBuffer(8)).getBigUint64(0)", "1,-2|0")]
    // direct eval and with
    [InlineData("(function (a) { var b = 2; eval('var c = a + b'); return c; })(1)", "3")]
    [InlineData("var o = { x: 1 }; with (o) { x = 5; } o.x", "5")]
    [InlineData("(function () { 'use strict'; eval('var z = 1'); return typeof z; })()", "undefined")]
    // mapped arguments, parameter scopes, Annex B
    [InlineData("(function (a) { arguments[0] = 9; return a; })(1)", "9")]
    [InlineData("var probe; try { (function (a = probe = () => x) { var x = 'inside'; return probe(); })() } catch (e) { e.name }", "ReferenceError")]
    [InlineData("(function () { { function f() { return 1; } } return f(); })()", "1")]
    // RegExp: property escapes (Unicode 17), v-flag sets, modifiers, duplicate named groups
    [InlineData("/^\\p{Script=Greek}+$/u.test('αβγ') + ',' + /\\p{L}/u.test('1')", "true,false")]
    [InlineData("/^[\\p{L}--[a-z]]+$/v.test('ABC') + ',' + /^[\\p{L}--[a-z]]+$/v.test('abc')", "true,false")]
    [InlineData("/(?i:a)b/.test('Ab') + ',' + /(?i:a)b/.test('AB')", "true,false")]
    [InlineData("/(?<y>\\d{4})-x|x-(?<y>\\d{4})/.exec('x-2024').groups.y", "2024")]
    [InlineData("/^\\p{RGI_Emoji}$/v.test('👨‍👩‍👧')", "true")]
    // library additions
    [InlineData("[...Iterator.from([1, 2, 3, 4]).filter(x => x % 2).map(x => x * 10)].join()", "10,30")]
    [InlineData("new Set([1, 2, 3]).intersection(new Set([2, 3, 4])).size", "2")]
    [InlineData("new Uint8Array([72, 105]).toBase64() + ',' + Uint8Array.fromHex('4869').join()", "SGk=,72,105")]
    [InlineData("new Map().getOrInsertComputed('k', k => k + '!')", "k!")]
    [InlineData("RegExp.escape('a.b') + ',' + Error.isError(new TypeError()) + ',' + Math.sumPrecise([1e20, 1, -1e20])", "\\x61\\.b,true,1")]
    [InlineData("'a\\uD800'.toWellFormed().charCodeAt(1)", "65533")]
    [InlineData("var b = new ArrayBuffer(4, { maxByteLength: 16 }); var t = new Uint8Array(b); b.resize(12); t.length", "12")]
    [InlineData("var b = new ArrayBuffer(8); var c = b.transfer(); b.detached + ',' + c.byteLength", "true,8")]
    [InlineData("Math.f16round(1.337) + ',' + new Float16Array([0.1])[0]", "1.3369140625,0.0999755859375")]
    [InlineData("var s = new Int32Array(new SharedArrayBuffer(8)); Atomics.add(s, 0, 5); Atomics.load(s, 0)", "5")]
    [InlineData("typeof new WeakRef({}).deref()", "object")]
    public void Evaluates(string src, string expected) => Assert.Equal(expected, Eval(src));

    [Theory]
    // fast paths for array length / append / push must keep their exact semantics
    [InlineData("var a = [1, 2]; var s = 0; for (var i = 0; i < 3; i++) { s += a.length; a.push(i); } s + ',' + a.length", "9,5")]
    [InlineData("function len(x) { return x.length; } [len([1, 2]), len('abc'), len({ length: 7 }), len([]), len(new Array(5))].join()", "2,3,7,0,5")]
    [InlineData("var a = []; a.length = 3; a[a.length] = 'x'; a.length + ':' + a[3]", "4:x")]
    [InlineData("var log = ''; Object.defineProperty(Array.prototype, '1', { set(v) { log += 'set' + v + ';'; }, configurable: true }); var a = [0]; a[a.length] = 5; a.push(6); delete Array.prototype[1]; log + '|' + a.length", "set5;set6;|2")]
    [InlineData("'use strict'; var a = Object.freeze([1]); var r = []; try { a[a.length] = 2; } catch (e) { r.push(e.name); } try { a.push(2); } catch (e) { r.push(e.name); } r.join() + ',' + a.length", "TypeError,TypeError,1")]
    [InlineData("var a = [1]; Object.defineProperty(a, 'length', { writable: false }); var r; try { a.push(2); } catch (e) { r = e.name; } r + ',' + a.length", "TypeError,1")]
    [InlineData("var o = { length: 0, push: Array.prototype.push }; o.push('a'); o.push('b'); o.length + o[1]", "2b")]
    [InlineData("var a = []; for (var i = 0; i < 100; i++) a[a.length] = i; a.length + ',' + a[99] + ',' + a.indexOf(50)", "100,99,50")]
    public void ArrayFastPathsKeepSemantics(string src, string expected) => Assert.Equal(expected, Eval(src));

    [Theory]
    // proper tail calls (strict code): constant stack depth far beyond MaxStackDepth
    [InlineData("'use strict'; function f(n) { if (n === 0) return 'done'; return f(n - 1); } f(200000)", "done")]
    [InlineData("'use strict'; var g = n => n === 0 ? 'ok' : g(n - 1); g(200000)", "ok")]
    [InlineData("'use strict'; function a(n) { return n === 0 ? 'a' : b(n - 1); } function b(n) { return n === 0 ? 'b' : (0, a(n - 1)); } a(200001)", "b")]
    [InlineData("'use strict'; function h(n) { if (!n) throw 1; try { return h(n - 1); } catch (e) { return 'caught'; } } h(3)", "caught")]
    // JSON.parse source text access, JSON.rawJSON
    [InlineData("var s = []; JSON.parse('[1.0, \"x\", {\"a\": 1e3}]', function (k, v, ctx) { if (typeof v !== 'object') s.push(ctx.source); return v; }); s.join('|')", "1.0|\"x\"|1e3")]
    [InlineData("JSON.stringify({ big: JSON.rawJSON('12345678901234567890') }) + ',' + JSON.isRawJSON(JSON.rawJSON('1'))", "{\"big\":12345678901234567890},true")]
    // Iterator.zip / zipKeyed / chunks / windows / includes
    [InlineData("JSON.stringify([...Iterator.zip([[1, 2, 3], 'ab'.split('')])])", "[[1,\"a\"],[2,\"b\"]]")]
    [InlineData("JSON.stringify([...Iterator.zip([[1], [2, 3]], { mode: 'longest', padding: ['p', 'q'] })])", "[[1,2],[\"p\",3]]")]
    [InlineData("JSON.stringify([...Iterator.zipKeyed({ a: [1, 2], b: [3, 4] })])", "[{\"a\":1,\"b\":3},{\"a\":2,\"b\":4}]")]
    [InlineData("try { [...Iterator.zip([[1], [2, 3]], { mode: 'strict' })] } catch (e) { e.name }", "TypeError")]
    [InlineData("JSON.stringify([...[1, 2, 3, 4, 5].values().chunks(2)]) + JSON.stringify([...[1, 2, 3].values().windows(2)])", "[[1,2],[3,4],[5]][[1,2],[2,3]]")]
    [InlineData("[[1, NaN, 3].values().includes(NaN), [1, 2, 3].values().includes(1, 1)].join()", "true,false")]
    // semantics fixed in this pass
    [InlineData("String(0.000012345) + ',' + (25).toExponential(0) + ',' + (1000000000000000128).toFixed(0) + ',' + Number(2n ** 53n + 3n)", "0.000012345,3e+1,1000000000000000128,9007199254740996")]
    [InlineData("'ß'.toUpperCase() + 'AΣ'.toLowerCase() + /\u212a/i.test('k')", "SSaςfalse")]
    [InlineData("Number.parseInt === parseInt", "true")]
    [InlineData("var o = { x: 1 }; with (o) { var x = 2; } o.x", "2")]
    [InlineData("var a = { b() { return this.c; }, c: 7 }; (a?.b)()", "7")]
    [InlineData("var g; class F extends Function {} new F() instanceof F", "true")]
    public void LateFeatures(string src, string expected) => Assert.Equal(expected, Eval(src));

    [Fact]
    public void AtomicsNotifyWakesWaitAsyncWaiters()
    {
        const string src = """
            const ia = new Int32Array(new SharedArrayBuffer(8));
            const { async, value } = Atomics.waitAsync(ia, 0, 0);
            value.then(v => log.push(v));
            log.push(async, Atomics.notify(ia, 0));
            """;
        Assert.Equal("true,1,ok", RunAsync(src));
    }

    [Fact]
    public void UsingDeclarationsDisposeInReverseOrderWithSuppressedErrors()
    {
        const string src = """
            function res(n) { return { [Symbol.dispose]() { log.push('d' + n); } }; }
            { using a = res(1), b = res(2); log.push('body'); }
            try { using x = { [Symbol.dispose]() { throw 'dispose'; } }; throw 'body'; }
            catch (e) { log.push(e.constructor.name + ':' + e.error + '/' + e.suppressed); }
            const s = new DisposableStack(); s.defer(() => log.push('deferred')); s.dispose();
            (async () => { { await using q = { async [Symbol.asyncDispose]() { log.push('async'); } }; } log.push('done'); })();
            """;
        Assert.Equal("body,d2,d1,SuppressedError:dispose/body,deferred,async,done", RunAsync(src));
    }

    [Fact]
    public void AsyncIterationHelpers()
    {
        const string src = """
            Array.fromAsync([Promise.resolve(1), 2], x => x * 2).then(a => log.push(a.join()));
            Promise.try(() => 7).then(v => log.push(v));
            """;
        Assert.Equal("7,2,4", RunAsync(src));
    }

    [Fact]
    public void JsonModulesRequireTheTypeAttribute()
    {
        using var e = new JsEngine();
        var resolver = new JsDictionaryModuleResolver()
            .AddJson("data.json", """{ "answer": 42, "list": [1, 2] }""")
            .Add("main.js", "import d from './data.json' with { type: 'json' }; export const answer = d.answer + d.list.length;")
            .Add("bad.js", "import d from './data.json'; export default d;");
        var main = e.LoadModule(JsCompiler.CompileModule("export { answer } from './main.js';", new JsCompileOptions { SourceName = "root.js" }), resolver, "root.js");
        e.SetValue("ns", main.Namespace);
        Assert.Equal("44", e.Evaluate("ns.answer").ToString());

        using var e2 = new JsEngine();
        Assert.ThrowsAny<Exception>(() => e2.LoadModule(JsCompiler.CompileModule("import './bad.js';", new JsCompileOptions { SourceName = "r2.js" }), resolver, "r2.js"));
    }

    [Fact]
    public void JsonModuleRejectsInvalidJsonAtCompileTime()
    {
        var ex = Assert.Throws<JsCompileException>(() => JsCompiler.CompileJsonModule("{ bad json }"));
        Assert.Equal("SyntaxError", ex.Code);
    }

    [Fact]
    public void AtomicsWaitThrowsBecauseTheAgentCannotBlock()
    {
        Assert.Equal("TypeError", Eval("try { Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 0) } catch (e) { e.constructor.name }"));
    }

    [Fact]
    public void RegexBudgetStopsCatastrophicBacktracking()
    {
        // the native engine (v flag) counts steps against the instruction budget
        using var e = new JsEngine(new JsEngineOptions { Limits = new JsLimits { MaxInstructions = 200_000 } });
        Assert.Throws<JsLimitExceededException>(() => e.Evaluate("/^(a+)+$/v.test('a'.repeat(40) + 'b')"));
    }
}
