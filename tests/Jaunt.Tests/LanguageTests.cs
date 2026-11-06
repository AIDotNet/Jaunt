using Jaunt;
using Xunit;

namespace Jaunt.Tests;

public class LanguageTests
{
    [Fact]
    public void LegacyCallerOfSloppyFunctions()
    {
        Assert.Equal("true,null,null,true,false", Eval("""
            function inner() { return inner.caller; }
            function outer() { return inner(); }
            function strictOuter() { "use strict"; return inner(); }
            var d = Object.getOwnPropertyDescriptor(inner, "caller");
            [outer() === outer, String(strictOuter()), String(inner.caller), d.configurable, Object.hasOwn(() => 0, "caller")].join()
            """));
    }

    [Fact]
    public void LoopHoistedConstantsDoNotLeakIntoNestedFunctions()
    {
        // `1` is hoisted into a register for the loop test; the arrow must load its own constant
        Assert.Equal("1,1", Eval("var out = []; for (var i = 2; i >= 1; i--) { out.push((() => ({ day: 1 }).day)()); } out.join()"));
    }

    internal static string Eval(string src)
    {
        using var e = new JsEngine();
        return e.Evaluate(src).ToString();
    }

    [Theory]
    // literals & operators
    [InlineData("1 + 2 * 3", "7")]
    [InlineData("(1 + 2) * 3", "9")]
    [InlineData("7 % 3", "1")]
    [InlineData("-7 % 3", "-1")]
    [InlineData("2 ** 10", "1024")]
    [InlineData("1 / 0", "Infinity")]
    [InlineData("0 / 0", "NaN")]
    [InlineData("Object.is(-0, 0 * -1)", "true")]
    [InlineData("1 / (-0)", "-Infinity")]
    [InlineData("'5' * '2'", "10")]
    [InlineData("'5' + 2", "52")]
    [InlineData("5 + null", "5")]
    [InlineData("5 + undefined", "NaN")]
    [InlineData("true + 1", "2")]
    [InlineData("[] + {}", "[object Object]")]
    [InlineData("[1,2] + ''", "1,2")]
    [InlineData("1 < 2 && 'b' > 'a'", "true")]
    [InlineData("'10' < '9'", "true")]
    [InlineData("10 < '9'", "false")]
    [InlineData("null == undefined", "true")]
    [InlineData("null === undefined", "false")]
    [InlineData("NaN == NaN", "false")]
    [InlineData("'1' == 1", "true")]
    [InlineData("0 == ''", "true")]
    [InlineData("~5", "-6")]
    [InlineData("5 & 3 | 8 ^ 1", "9")]
    [InlineData("-1 >>> 28", "15")]
    [InlineData("1 << 31", "-2147483648")]
    [InlineData("-16 >> 2", "-4")]
    [InlineData("typeof null", "object")]
    [InlineData("typeof function(){}", "function")]
    [InlineData("typeof Symbol()", "symbol")]
    [InlineData("void 0", "undefined")]
    [InlineData("!!''", "false")]
    [InlineData("null ?? 'd'", "d")]
    [InlineData("0 ?? 'd'", "0")]
    [InlineData("0 || 'd'", "d")]
    [InlineData("1 && 2", "2")]
    [InlineData("let a = null; a ??= 5; a", "5")]
    [InlineData("let a = 1; a ||= 5; a", "1")]
    [InlineData("let a = 1; a &&= 5; a", "5")]
    [InlineData("let x = 5; x += 2; x *= 3; x -= 1; x /= 4; x", "5")]
    [InlineData("let i = 0; i++ + ++i", "2")]
    [InlineData("let o = {a: 1}; o.a++; o.a", "2")]
    [InlineData("let o = {a: 1}; ++o['a']", "2")]
    [InlineData("1, 2, 3", "3")]
    [InlineData("true ? 'y' : 'n'", "y")]
    [InlineData("0.1 + 0.2", "0.30000000000000004")]
    [InlineData("123e-20", "1.23e-18")]
    [InlineData("1e21", "1e+21")]
    [InlineData("100000000000000000000", "100000000000000000000")]
    [InlineData("0.000001", "0.000001")]
    [InlineData("0.0000001", "1e-7")]
    [InlineData("-1.5e300 * 1e10", "-Infinity")]
    [InlineData("(255).toString(16)", "ff")]
    [InlineData("(0.5).toString(2)", "0.1")]
    [InlineData("'b' in {b: 1}", "true")]
    [InlineData("delete ({a:1}).a", "true")]
    // variables & scoping
    [InlineData("var x = 1; { let x = 2; } x", "1")]
    [InlineData("let x = 1; { let x = 2; x = 3; } x", "1")]
    [InlineData("function f() { return typeof g; function g() {} } f()", "function")]
    [InlineData("function f() { return v; var v = 3; } f()", "undefined")]
    [InlineData("try { (function(){ x; let x = 1; })() } catch (e) { e.name }", "ReferenceError")]
    [InlineData("try { const c = 1; c = 2; } catch (e) { e.name }", "TypeError")]
    [InlineData("var fns = []; for (var i = 0; i < 3; i++) fns.push(() => i); fns.map(f => f()).join()", "3,3,3")]
    [InlineData("var fns = []; for (let i = 0; i < 3; i++) fns.push(() => i); fns.map(f => f()).join()", "0,1,2")]
    [InlineData("var fns = []; for (const k of ['a','b']) fns.push(() => k); fns.map(f => f()).join()", "a,b")]
    [InlineData("var fns = []; for (const k in {p:1, q:2}) fns.push(() => k); fns.map(f => f()).join()", "p,q")]
    [InlineData("function counter() { let c = 0; return () => ++c; } const c1 = counter(); c1(); c1(); c1()", "3")]
    [InlineData("(function f(n) { return n <= 1 ? 1 : n * f(n - 1); })(10)", "3628800")]
    [InlineData("var f = function g() { return typeof g; }; f()", "function")]
    [InlineData("const f = () => {}; f.name", "f")]
    [InlineData("let o = { m() {} }; o.m.name", "m")]
    [InlineData("let o = { ['x' + 1]: () => 1 }; o.x1.name", "x1")]
    [InlineData("(function(a, b = 2) {}).length", "1")]
    [InlineData("(function(...r) {}).length", "0")]
    [InlineData("function f(a, b = a + 1) { return b; } f(5)", "6")]
    [InlineData("function f(...r) { return r.length; } f(1, 2, 3)", "3")]
    [InlineData("function f() { return arguments.length; } f(1, 2, 3)", "3")]
    [InlineData("function f() { return arguments[1]; } f(1, 2, 3)", "2")]
    [InlineData("function f() { return [...arguments].join(); } f(1, 2)", "1,2")]
    [InlineData("function outer() { const inner = () => arguments[0]; return inner(); } outer(9)", "9")]
    [InlineData("var o = { v: 1, f() { return () => this.v; } }; o.f()()", "1")]
    [InlineData("function f() { 'use strict'; return this; } f() === undefined", "true")]
    [InlineData("function f() { return this; } f() === globalThis", "true")]
    [InlineData("function f() { return typeof this; } f.call(5)", "object")]
    [InlineData("function f() { 'use strict'; return typeof this; } f.call(5)", "number")]
    // control flow
    [InlineData("let s = 0; for (let i = 0; i < 10; i++) { if (i % 2) continue; s += i; } s", "20")]
    [InlineData("let s = 0; let i = 0; while (true) { if (++i > 5) break; s += i; } s", "15")]
    [InlineData("let i = 0; do { i++ } while (i < 5); i", "5")]
    [InlineData("let r = ''; outer: for (let i = 0; i < 3; i++) { for (let j = 0; j < 3; j++) { if (j == 2) continue outer; if (i == 2) break outer; r += i + '' + j + ' '; } } r", "00 01 10 11 ")]
    [InlineData("let r = 0; block: { r = 1; break block; r = 2; } r", "1")]
    [InlineData("let r = ''; for (const k in {a: 1, b: 2}) r += k; r", "ab")]
    [InlineData("let r = ''; for (const k in [5, 6]) r += k; r", "01")]
    [InlineData("let r = 0; for (const v of [1, 2, 3]) r += v; r", "6")]
    [InlineData("let r = ''; for (const c of 'héllo') r += c + '.'; r", "h.é.l.l.o.")]
    [InlineData("let r = ''; for (const [k, v] of new Map([['a', 1], ['b', 2]])) r += k + v; r", "a1b2")]
    [InlineData("switch ('b') { case 'a': 1; case 'b': 2; case 'c': 3; break; default: 4 }", "3")]
    [InlineData("let r = ''; switch (5) { default: r += 'd'; case 1: r += '1'; break; case 2: r += '2'; } r", "d1")]
    [InlineData("let r; switch (1) { case 1: let x = 'scoped'; r = x; } r", "scoped")]
    // exceptions & finally
    [InlineData("try { throw new Error('boom') } catch (e) { e.message }", "boom")]
    [InlineData("try { throw 42 } catch (e) { e }", "42")]
    [InlineData("try { throw {code: 7} } catch ({code}) { code }", "7")]
    [InlineData("function f() { try { return 'try' } finally { return 'finally' } } f()", "finally")]
    [InlineData("function f() { try { throw 1 } finally { return 'swallowed' } } f()", "swallowed")]
    [InlineData("let log = []; function f() { for (let i = 0; i < 3; i++) { try { if (i == 1) continue; if (i == 2) break; log.push(i) } finally { log.push('f' + i) } } return log.join() } f()", "0,f0,f1,f2")]
    [InlineData("let log = []; function f() { try { try { return 'x' } finally { log.push(1) } } finally { log.push(2) } } f() + log.join()", "x1,2")]
    [InlineData("let r = ''; try { try { throw 'a' } finally { r += 'f' } } catch (e) { r += e } r", "fa")]
    [InlineData("let r = ''; for (const x of [1,2,3]) { try { if (x === 2) break; r += x } finally { r += '!' } } r", "1!!")]
    [InlineData("function f() { try { throw new TypeError('t') } catch (e) { return e instanceof TypeError && e instanceof Error } } f()", "true")]
    [InlineData("new RangeError('r').name", "RangeError")]
    [InlineData("String(new TypeError('m'))", "TypeError: m")]
    [InlineData("try { undefinedFn() } catch (e) { e instanceof ReferenceError }", "true")]
    [InlineData("try { (void 0).x } catch (e) { e.message }", "Cannot read properties of undefined (reading 'x')")]
    [InlineData("try { null.f() } catch (e) { e.name }", "TypeError")]
    [InlineData("try { ({}).f() } catch (e) { e.message }", "object.f is not a function")]
    [InlineData("new Error('x', { cause: 'c' }).cause", "c")]
    // destructuring & spread
    [InlineData("const [a, [b, c] = [2, 3], ...d] = [1, undefined, 4, 5]; a + b + c + d.length", "8")]
    [InlineData("const { a, b: { c = 5 } = {}, ...rest } = { a: 1, x: 2, y: 3 }; a + c + Object.keys(rest).join()", "6x,y")]
    [InlineData("let a = 1, b = 2; [a, b] = [b, a]; a + '' + b", "21")]
    [InlineData("const o = {}; ({ x: o.p, y: o['q'] } = { x: 1, y: 2 }); o.p + o.q", "3")]
    [InlineData("function f({ a, b = 2 } = {}) { return a + b } f({ a: 1 })", "3")]
    [InlineData("function f([x, y]) { return x * y } f([3, 4])", "12")]
    [InlineData("Math.max(...[1, 5, 3], 4)", "5")]
    [InlineData("[...'abc', ...[1, 2]].join()", "a,b,c,1,2")]
    [InlineData("const o = { ...{ a: 1, b: 2 }, b: 3 }; o.a + o.b", "4")]
    [InlineData("[1, , 3].length", "3")]
    [InlineData("1 in [1, , 3]", "false")]
    [InlineData("new Array(5).length", "5")]
    [InlineData("const [x = 10] = []; x", "10")]
    // objects
    [InlineData("const o = { a: 1, get b() { return this.a + 1 }, set b(v) { this.a = v } }; o.b = 10; o.b", "11")]
    [InlineData("const k = 'dyn'; ({ [k + 1]: 5 }).dyn1", "5")]
    [InlineData("const a = 1, b = 2; const o = { a, b }; o.a + o.b", "3")]
    [InlineData("({ __proto__: { inherited: 'yes' } }).inherited", "yes")]
    [InlineData("Object.keys({ z: 1, 2: 1, a: 1, 1: 1 }).join()", "1,2,z,a")]
    [InlineData("const o = {}; o.x = 1; delete o.x; 'x' in o", "false")]
    [InlineData("const o = Object.freeze({ a: 1 }); o.a = 2; o.a", "1")]
    [InlineData("'use strict'; const o = Object.freeze({ a: 1 }); try { o.a = 2 } catch (e) { e.name }", "TypeError")]
    [InlineData("const o = {}; Object.defineProperty(o, 'x', { value: 1, enumerable: false }); Object.keys(o).length + o.x", "1")]
    [InlineData("Object.getOwnPropertyDescriptor({ get g() { return 1 } }, 'g').get !== undefined", "true")]
    [InlineData("Object.entries({ a: 1, b: 2 }).map(([k, v]) => k + v).join()", "a1,b2")]
    [InlineData("Object.fromEntries([['a', 1]]).a", "1")]
    [InlineData("Object.assign({}, { a: 1 }, null, { b: 2 }).b", "2")]
    [InlineData("Object.getPrototypeOf(Object.create(null))", "null")]
    [InlineData("({}).toString()", "[object Object]")]
    [InlineData("Object.prototype.toString.call([])", "[object Array]")]
    [InlineData("Object.prototype.toString.call(null)", "[object Null]")]
    [InlineData("({ [Symbol.toStringTag]: 'Custom' }).toString()", "[object Custom]")]
    [InlineData("const o = { valueOf() { return 42 } }; o + 1", "43")]
    [InlineData("const o = { toString() { return 'str' } }; `${o}`", "str")]
    [InlineData("const o = { [Symbol.toPrimitive](h) { return h } }; `${o}` + (o + '') + (+{ [Symbol.toPrimitive]: () => 7 })", "stringdefault7")]
    [InlineData("const s = Symbol('d'); const o = { [s]: 1 }; o[s] + Object.getOwnPropertySymbols(o).length", "2")]
    [InlineData("Symbol('desc').description", "desc")]
    [InlineData("Symbol.for('k') === Symbol.for('k')", "true")]
    [InlineData("const o = { a: 1 }; const p = Object.create(o); p.a = 2; o.a", "1")]
    [InlineData("const proto = { set v(x) { this._v = x * 2 } }; const o = Object.create(proto); o.v = 5; o._v", "10")]
    [InlineData("const o = {}; o[1] = 'one'; o['1']", "one")]
    [InlineData("const o = {a:1}; for (let i = 0; i < 100; i++) o['k' + i] = i; o.k99 + o.a", "100")]
    // classes
    [InlineData("class A { static s = 5; #p = 1; get p() { return this.#p } inc() { this.#p++; return this } } new A().inc().inc().p + A.s", "8")]
    [InlineData("class A { constructor() { this.n = 'a' } hi() { return 'A' + this.n } } class B extends A { constructor() { super(); this.n = 'b' } hi() { return 'B' + super.hi() } } new B().hi()", "BAb")]
    [InlineData("class A { static create() { return new this() } } class B extends A {} B.create() instanceof B", "true")]
    [InlineData("class A { #x = 1; static has(o) { return #x in o } } A.has(new A()) + ',' + A.has({})", "true,false")]
    [InlineData("class A { #m() { return 'priv' } call() { return this.#m() } } new A().call()", "priv")]
    [InlineData("class A { static #count = 0; static inc() { return ++A.#count } } A.inc(); A.inc()", "2")]
    [InlineData("class A { x = this.constructor.name; } new A().x", "A")]
    [InlineData("class A { static { this.init = 'done' } } A.init", "done")]
    [InlineData("try { class A {}; A() } catch (e) { e.name }", "TypeError")]
    [InlineData("class A { constructor() { return { custom: 1 } } } new A().custom", "1")]
    [InlineData("class A {} class B extends A { constructor() { try { this.x } catch (e) { super(); return { e: e.name } } } } new B().e", "ReferenceError")]
    [InlineData("class E extends Error { constructor(m) { super(m); this.name = 'E' } } const e = new E('msg'); e instanceof Error && e.message === 'msg' && String(e)", "E: msg")]
    [InlineData("class M extends Map { getOr(k, d) { return this.has(k) ? this.get(k) : d } } new M([[1, 2]]).getOr(3, 'none')", "none")]
    [InlineData("class A { get [Symbol.toStringTag]() { return 'AA' } } String(new A())", "[object AA]")]
    [InlineData("typeof class {}", "function")]
    [InlineData("class A { static m() { return 1 } } class B extends A { static m() { return super.m() + 1 } } B.m()", "2")]
    [InlineData("const o = { m() { return 'o' } }; const p = { __proto__: o, m() { return 'p' + super.m() } }; p.m()", "po")]
    [InlineData("class A { constructor(...args) { this.args = args } } class B extends A {} new B(1, 2).args.join()", "1,2")]
    [InlineData("class Point { constructor(x, y) { this.x = x; this.y = y } toString() { return `(${this.x}, ${this.y})` } } `${new Point(1, 2)}`", "(1, 2)")]
    // generators & iterators
    [InlineData("function* g() { const x = yield 1; yield x * 2 } const it = g(); it.next(); it.next(21).value", "42")]
    [InlineData("function* g() { try { yield 1 } finally { globalThis.cleaned = true } } const it = g(); it.next(); it.return(5).value + '' + cleaned", "5true")]
    [InlineData("function* g() { try { yield 1 } catch (e) { yield 'caught ' + e } } const it = g(); it.next(); it.throw('x').value", "caught x")]
    [InlineData("function* inner() { yield 1; yield 2; return 'r' } function* outer() { const r = yield* inner(); yield r } [...outer()].join()", "1,2,r")]
    [InlineData("function* nat() { let i = 0; while (true) yield i++ } const [a, b, c] = nat(); a + b + c", "3")]
    [InlineData("const it = { [Symbol.iterator]() { let i = 0; return { next: () => ({ done: i >= 3, value: i++ }) } } }; [...it].join()", "0,1,2")]
    [InlineData("let closed = false; const it = { [Symbol.iterator]() { return { next: () => ({ done: false, value: 1 }), return() { closed = true; return {} } } } }; for (const x of it) break; closed", "true")]
    [InlineData("const g = function*(){ yield* [1, 2] }; Array.from(g()).length", "2")]
    [InlineData("function* g() { yield 1 } g().toString()", "[object Generator]")]
    [InlineData("[...[1, 2, 3].entries()].map(([i, v]) => i * v).join()", "0,2,6")]
    [InlineData("[...new Set([1, 1, 2]).values()].join()", "1,2")]
    // template literals
    [InlineData("const n = 3; `n=${n}, sq=${n * n}`", "n=3, sq=9")]
    [InlineData("function tag(s, ...v) { return s.raw.join('|') + v.join('+') } tag`a${1}b${2}c`", "a|b|c1+2")]
    [InlineData("String.raw`x\\ny`", "x\\ny")]
    [InlineData("`multi\nline`.length", "10")]
    // optional chaining
    [InlineData("const o = { a: { b: 1 } }; o?.a?.b + (o.x?.y === undefined ? 1 : 0)", "2")]
    [InlineData("const o = null; o?.f()", "undefined")]
    [InlineData("const o = { f() { return 3 } }; o.f?.() + (o.g?.() ?? 4)", "7")]
    [InlineData("const a = null; a?.[0]", "undefined")]
    // getters/setters/defineProperty on arrays
    [InlineData("const a = [1, 2, 3]; a.length = 1; a.join()", "1")]
    [InlineData("const a = []; a[5] = 1; a.length", "6")]
    [InlineData("const a = [1, 2, 3]; delete a[1]; a.join() + a.length", "1,,33")]
    [InlineData("const a = []; a[1e6] = 1; a.length", "1000001")]
    [InlineData("const a = Object.freeze([1]); try { 'use strict'; a.push(2) } catch (e) { e.name }", "TypeError")]
    [InlineData("Array.isArray([]) && !Array.isArray({})", "true")]
    // eval / Function
    [InlineData("eval('1 + 2')", "3")]
    [InlineData("new Function('a', 'b', 'return a * b')(6, 7)", "42")]
    [InlineData("eval('var evalVar = 5'); evalVar", "5")]
    // misc
    [InlineData("globalThis.x1 = 5; x1", "5")]
    [InlineData("var gv = 1; globalThis.gv", "1")]
    [InlineData("let lexical = 1; globalThis.lexical", "undefined")]
    [InlineData("sloppyGlobal = 7; globalThis.sloppyGlobal", "7")]
    [InlineData("'use strict'; try { undeclared = 1 } catch (e) { e.name }", "ReferenceError")]
    [InlineData("typeof globalThis", "object")]
    [InlineData("(() => { 'use strict'; return this })() === globalThis", "true")]
    [InlineData("[1, 2, 3].includes(NaN) + ',' + [NaN].includes(NaN) + ',' + [NaN].indexOf(NaN)", "false,true,-1")]
    [InlineData("function f() { return new.target === undefined } f()", "true")]
    [InlineData("function F() { return new.target === F } new F()", "[object Object]")]
    [InlineData("function F() { this.ok = new.target === F } new F().ok", "true")]
    public void Evaluates(string source, string expected) => Assert.Equal(expected, Eval(source));

    [Theory]
    [InlineData("let x = 1; let x = 2;")]
    [InlineData("function (")]
    [InlineData("1 +")]
    [InlineData("break;")]
    public void RejectsAtCompileTime(string source)
    {
        using var e = new JsEngine();
        Assert.Throws<JsCompileException>(() => e.Evaluate(source));
    }

    [Fact]
    public void CompileErrorsCarryLocation()
    {
        var ex = Assert.Throws<JsCompileException>(() => JsCompiler.CompileScript("let a = 1;\nlet b = ;", new JsCompileOptions { SourceName = "x.js" }));
        Assert.Equal("SyntaxError", ex.Code);
        Assert.Equal(2, ex.Line);
        Assert.Equal("x.js", ex.SourceName);
    }

    [Fact]
    public void JsStackTraceHasSourcePositions()
    {
        using var e = new JsEngine(new JsEngineOptions { CompileOptions = new JsCompileOptions { SourceName = "app.js" } });
        var ex = Assert.Throws<JsException>(() => e.Evaluate("function inner() {\n  throw new Error('deep');\n}\nfunction outer() { inner(); }\nouter();"));
        Assert.Equal("Error", ex.Name);
        Assert.Equal("deep", ex.ErrorMessage);
        Assert.Contains("inner (app.js:2:", ex.JsStackTrace);
        Assert.Contains("outer (app.js:4:", ex.JsStackTrace);
    }

    [Fact]
    public void GlobalLexicalBindingsPersistAcrossScripts()
    {
        using var e = new JsEngine();
        e.Evaluate("let counter = 1; const K = 10; function bump() { return ++counter }");
        Assert.Equal(2.0, e.Evaluate("bump()").AsNumber());
        Assert.Equal(12.0, e.Evaluate("counter + K").AsNumber());
        Assert.Throws<JsException>(() => e.Evaluate("let counter = 5;"));
    }

    [Fact]
    public void DeepJsRecursionIsBoundedByStackLimit()
    {
        using var e = new JsEngine(new JsEngineOptions { Limits = new JsLimits { MaxStackDepth = 500 } });
        var ex = Assert.Throws<JsLimitExceededException>(() => e.Evaluate("function f(n) { return f(n + 1) } f(0)"));
        Assert.Equal(JsLimitKind.StackDepth, ex.Kind);
        Assert.True(e.IsFaulted);
    }

    [Fact]
    public void DeepButLegalRecursionWorks()
    {
        using var e = new JsEngine();
        Assert.Equal(5000.0, e.Evaluate("function f(n) { return n == 0 ? 0 : 1 + f(n - 1) } f(5000)").AsNumber());
    }
}
