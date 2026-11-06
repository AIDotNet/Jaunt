using Jaunt;
using Xunit;

namespace Jaunt.Tests;

/// <summary>ShadowRealm (callable boundary, importValue) and decorators (2023-05 semantics, auto-accessors).</summary>
public class ShadowRealmDecoratorTests
{
    private static JsEngine NewEngine(IJsModuleResolver? resolver = null) =>
        new(new JsEngineOptions { ModuleResolver = resolver, Limits = new JsLimits { Timeout = TimeSpan.FromSeconds(10) } });

    private static string Eval(string src)
    {
        using var e = NewEngine();
        return e.Evaluate(src).ToString();
    }

    [Theory]
    // evaluate: primitives cross, globals are separate, lexical declarations are per evaluation
    [InlineData("var r = new ShadowRealm(); r.evaluate('var x = 1; let y = 2; x + y')", "3")]
    [InlineData("globalThis.v = 'outer'; var r = new ShadowRealm(); r.evaluate('globalThis.v = \"inner\"'); v + ',' + r.evaluate('v')", "outer,inner")]
    [InlineData("var r = new ShadowRealm(); r.evaluate('let z = 1'); r.evaluate('typeof z')", "undefined")]
    [InlineData("var r = new ShadowRealm(); r.evaluate('Array') === Array", "false")]
    // only primitives and callables cross; errors become TypeErrors of the caller realm
    [InlineData("var r = new ShadowRealm(); try { r.evaluate('({})') } catch (e) { e.constructor === TypeError }", "true")]
    [InlineData("var r = new ShadowRealm(); try { r.evaluate('throw new RangeError(\"x\")') } catch (e) { e.constructor === TypeError }", "true")]
    [InlineData("var r = new ShadowRealm(); try { r.evaluate('1 +') } catch (e) { e.constructor === SyntaxError }", "true")]
    // wrapped functions: name/length copied, arguments and results wrapped, fresh wrapper per crossing
    [InlineData("var r = new ShadowRealm(); var f = r.evaluate('(function add(a, b) { return a + b; })'); f.name + f.length + f(2, 3)", "add25")]
    [InlineData("var r = new ShadowRealm(); var f = r.evaluate('(x) => x'); try { f({}) } catch (e) { e.constructor === TypeError }", "true")]
    [InlineData("var r = new ShadowRealm(); var call = r.evaluate('(cb) => cb(20) + 1'); call(x => x * 2)", "41")]
    [InlineData("var r = new ShadowRealm(); var f = r.evaluate('(f) => f'); var g = () => 1; f(g) !== g && f(g)() === 1", "true")]
    [InlineData("var r = new ShadowRealm(); var f = r.evaluate('() => { throw new Error(\"boom\") }'); try { f() } catch (e) { e.constructor === TypeError }", "true")]
    [InlineData("var r = new ShadowRealm(); Object.getPrototypeOf(r.evaluate('() => 1')) === Function.prototype", "true")]
    [InlineData("Object.prototype.toString.call(new ShadowRealm()) + ShadowRealm.prototype.evaluate.length + ShadowRealm.prototype.importValue.length", "[object ShadowRealm]12")]
    [InlineData("try { ShadowRealm() } catch (e) { e.constructor.name }", "TypeError")]
    [InlineData("try { ShadowRealm.prototype.evaluate.call({}, '1') } catch (e) { e.constructor.name }", "TypeError")]
    public void ShadowRealmEvaluate(string src, string expected) => Assert.Equal(expected, Eval(src));

    [Fact]
    public void ShadowRealmImportValue()
    {
        var resolver = new JsDictionaryModuleResolver()
            .Add("lib", "export const x = 42; export function twice(v) { return v * 2; } export const obj = {}; globalThis.loaded = true;")
            .Add("bad", "throw new Error('nope');");
        using var e = NewEngine(resolver);
        e.Evaluate("""
            var log = [];
            var r = new ShadowRealm();
            r.importValue('lib', 'x').then(v => log.push('x=' + v));
            r.importValue('lib', 'twice').then(f => log.push('twice=' + f(21)));
            r.importValue('lib', 'obj').catch(err => log.push('obj:' + (err.constructor === TypeError)));
            r.importValue('lib', 'missing').catch(err => log.push('missing:' + (err.constructor === TypeError)));
            r.importValue('bad', 'x').catch(err => log.push('bad:' + (err.constructor === TypeError)));
            r.importValue('nowhere', 'x').catch(err => log.push('nowhere:' + (err.constructor === TypeError)));
            """);
        var log = e.Evaluate("log.sort().join(',') + '|' + (typeof loaded) + '|' + r.evaluate('loaded')").ToString();
        Assert.Equal("bad:true,missing:true,nowhere:true,obj:true,twice=42,x=42|undefined|true", log);
        Assert.Equal("TypeError", e.Evaluate("try { r.importValue('lib', 1) } catch (e) { e.constructor.name }").ToString());
    }

    [Fact]
    public void ShadowRealmIsDisabledInHardenedProfile()
    {
        using var engine = new JsEngine(new JsEngineOptions { Profile = JsProfile.Hardened });
        Assert.Equal("TypeError", engine.Evaluate("try { new ShadowRealm() } catch (e) { e.name }").AsString());
    }

    [Theory]
    // auto-accessors (public, private, static), accessor as an identifier
    [InlineData("class C { accessor x = 1; static accessor y = 2; } var c = new C(); c.x = c.x + 10; [c.x, C.y, Object.hasOwn(c, 'x'), typeof Object.getOwnPropertyDescriptor(C.prototype, 'x').get].join()", "11,2,false,function")]
    [InlineData("class C { accessor #x = 5; get() { return this.#x; } set(v) { this.#x = v; } } var c = new C(); c.set(7); c.get()", "7")]
    [InlineData("class C { accessor x; } Object.getOwnPropertyDescriptor(C.prototype, 'x').get.name + '/' + Object.getOwnPropertyDescriptor(C.prototype, 'x').set.name", "get x/set x")]
    [InlineData("class C { accessor = 3; accessor() { return 1; } } new C().accessor", "3")]
    [InlineData("try { eval('class C { accessor #x; #x; }') } catch (e) { e.name }", "SyntaxError")]
    // decorator evaluation order: decorators and computed keys in document order, application bottom-up
    [InlineData("""
        var log = [];
        function d(tag) { log.push('eval ' + tag); return (v, ctx) => { log.push('apply ' + tag + ' ' + ctx.kind); }; }
        @d('class') class C { @d('a') @d('b') static [(log.push('key'), 'm')]() {} @d('f') static f; }
        log.join()
        """, "eval class,eval a,eval b,key,eval f,apply b method,apply a method,apply f field,apply class class")]
    // method, getter, field and class decorators replacing their targets
    [InlineData("""
        const twice = (m) => function (...a) { return m.apply(this, a) * 2; };
        const plus1 = () => v => v + 1;
        const tagged = (C, ctx) => class extends C { tag = ctx.name; };
        @tagged class C { @twice m() { return 21; } @plus1 f = 1; @plus1 #p = 9; get p() { return this.#p; } }
        var c = new C(); [c.m(), c.f, c.p, c.tag, C === c.constructor].join()
        """, "42,2,10,C,true")]
    // accessor decorators: get/set/init
    [InlineData("""
        const logged = ({ get, set }) => ({ get() { return get.call(this) * 10; }, set(v) { set.call(this, v + 1); }, init(v) { return v + 100; } });
        class C { @logged accessor x = 1; }
        var c = new C(); var a = c.x; c.x = 5; a + ',' + c.x
        """, "1010,60")]
    // context: name/static/private/access/metadata/addInitializer
    [InlineData("""
        var acc, log = [];
        const grab = (v, ctx) => { acc = ctx.access; log.push(ctx.name + ':' + ctx.static + ':' + ctx.private); };
        const meta = (v, ctx) => { ctx.metadata[ctx.name] = ctx.kind; };
        const init = (v, ctx) => { ctx.addInitializer(function () { log.push('init ' + typeof this); }); };
        class B { @meta static base() {} }
        class C extends B { @grab #x = 1; @meta @init m() {} @init static s() {} }
        var c = new C();
        var md = C[Symbol.metadata];
        log.push(acc.get(c), acc.has(c), acc.has({}), Object.getPrototypeOf(md) === B[Symbol.metadata], md.m, md.base);
        log.join()
        """, "#x:false:true,init function,init object,1,true,false,true,method,method")]
    [InlineData("try { class C { @(() => 1) m() {} } } catch (e) { e.constructor.name }", "TypeError")]
    [InlineData("var add; class C { @((v, ctx) => { add = ctx.addInitializer; }) m() {} } try { add(() => {}) } catch (e) { e.constructor.name }", "TypeError")]
    [InlineData("class C {} C[Symbol.metadata] === undefined && typeof Symbol.metadata", "symbol")]
    public void Decorators(string src, string expected) => Assert.Equal(expected, Eval(src));
}
