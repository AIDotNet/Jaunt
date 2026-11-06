using Jaunt;
using Xunit;

namespace Jaunt.Tests;

/// <summary>Stage-3 proposals covered by test262: iterator join, await-dictionary, immutable ArrayBuffer, legacy RegExp
/// statics, the Error.prototype.stack accessor and nonextensible-applies-to-private.</summary>
public class ProposalBuiltinsTests
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
    // Iterator.prototype.join
    [InlineData("['a', null, 'b', undefined, 1].values().join('-')", "a--b--1")]
    [InlineData("[1, 2, 3].values().map(x => x * 2).join()", "2,4,6")]
    [InlineData("var closed = false; var it = { next() { return { done: false, value: { toString() { throw 1; } } }; }, return() { closed = true; return {}; } };"
        + " try { Iterator.prototype.join.call(it) } catch (e) {} closed", "true")]
    // immutable ArrayBuffer
    [InlineData("var b = new ArrayBuffer(4); new Uint8Array(b).set([1, 2, 3, 4]); var ib = b.transferToImmutable();"
        + " [ib.immutable, b.detached, ib.byteLength, new Uint8Array(ib).join('')].join()", "true,true,4,1234")]
    [InlineData("var ib = new Uint8Array([1, 2, 3]).buffer.sliceToImmutable(1); var u = new Uint8Array(ib); u[0] = 9;"
        + " [u.join(''), Reflect.set(u, 0, 9), Object.getOwnPropertyDescriptor(u, 0).writable, Object.isFrozen(Object.freeze(u))].join()", "23,false,false,true")]
    [InlineData("var u = new Uint8Array(new ArrayBuffer(2).transferToImmutable()); var r = [];"
        + " for (var f of [() => u.fill(1), () => u.set([1]), () => new DataView(u.buffer).setUint8(0, 1), () => Atomics.store(u, 0, 1), () => u.buffer.transfer()])"
        + " try { f(); r.push('ok'); } catch (e) { r.push(e.name); } r.join()", "TypeError,TypeError,TypeError,TypeError,TypeError")]
    [InlineData("'use strict'; var u = new Uint8Array(new ArrayBuffer(2).transferToImmutable()); try { u[0] = 1; 'no' } catch (e) { e.name }", "TypeError")]
    // legacy RegExp static properties
    [InlineData("/(\\d+)-(\\d+)/.exec('ab12-34cd'); [RegExp.$1, RegExp.$2, RegExp.$3, RegExp.lastMatch, RegExp['$+'], RegExp.leftContext, RegExp[\"$'\"], RegExp.input].join('|')",
        "12|34||12-34|34|ab|cd|ab12-34cd")]
    [InlineData("'x1y2'.replace(/\\d/g, 'D'); RegExp['$&'] + RegExp.$_", "2x1y2")]
    [InlineData("'a,b'.split(/(,)/); RegExp.$1 + RegExp.leftContext", ",a")]
    [InlineData("class R extends RegExp {} new R('a').exec('a'); try { RegExp.$1; 'no' } catch (e) { e.name }", "TypeError")]
    [InlineData("try { RegExp.prototype.compile.call(new (class extends RegExp {})('a')); 'no' } catch (e) { e.name }", "TypeError")]
    // Error.prototype.stack accessor
    [InlineData("var e = new TypeError('m'); [Object.hasOwn(e, 'stack'), typeof e.stack, e.stack.startsWith('TypeError: m')].join()", "false,string,true")]
    [InlineData("var d = Object.getOwnPropertyDescriptor(Error.prototype, 'stack'); var e = new Error('x'); e.stack = 's';"
        + " [d.get.call({}), e.stack, Object.hasOwn(e, 'stack'), typeof d.get.call(e)].join()", ",s,true,string")]
    // private elements and non-extensible objects
    [InlineData("class B { constructor(o) { return o; } } class C extends B { #x = 1; } try { new C(Object.preventExtensions({})); 'no' } catch (e) { e.name }", "TypeError")]
    public void Evaluates(string src, string expected) => Assert.Equal(expected, Eval(src));

    [Fact]
    public void PromiseAllKeyed()
    {
        Assert.Equal("true,a:1,b:2", RunAsync(
            "Promise.allKeyed({ a: Promise.resolve(1), b: 2 }).then(r => log.push(Object.getPrototypeOf(r) === null, 'a:' + r.a, 'b:' + r.b));"));
        Assert.Equal("fulfilled:1,rejected:e", RunAsync(
            "Promise.allSettledKeyed({ x: 1, y: Promise.reject('e') }).then(r => log.push(r.x.status + ':' + r.x.value, r.y.status + ':' + r.y.reason));"));
        Assert.Equal("TypeError", RunAsync("Promise.allKeyed(1).catch(e => log.push(e.name));"));
    }
}
