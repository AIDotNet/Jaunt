using System.Collections.Generic;
using System.Linq;
using Jaunt;
using Xunit;

namespace Jaunt.Tests;

/// <summary>
/// Promise and async fast paths: microtask ordering, and every lookup they skip being observed again once user code
/// patches <c>then</c>, <c>constructor</c> or <c>Symbol.species</c>. Each case runs in a fresh engine. Expected values were
/// produced by Node (V8) from the same script.
/// </summary>
public class PromiseFastPathTests
{
    private static readonly Dictionary<string, (string Script, string Expected)> Cases = new()
    {
        ["ordering"] = ("""
const tick = n => { let p = Promise.resolve(); for (let i = 0; i < n; i++) p = p.then(() => log.push('t' + i)); };
tick(6);
(async () => { await 1; log.push('await prim'); })();
(async () => { await Promise.resolve(); log.push('await native'); })();
(async () => { await { then(r) { log.push('then called'); r(); } }; log.push('await thenable'); })();
(async () => { await {}; log.push('await plain'); })();
(async () => { return Promise.resolve(1); })().then(() => log.push('async return promise'));
(async () => { return 1; })().then(() => log.push('async return prim'));
new Promise(r => r(Promise.resolve())).then(() => log.push('resolve(promise)'));
Promise.resolve().then(() => Promise.resolve()).then(() => log.push('then returns promise'));
Promise.reject(1).catch(() => log.push('catch')).finally(() => log.push('finally'));
(async () => { throw 1; })().catch(() => log.push('async throw'));
(async () => { try { await Promise.reject(2); } catch (e) { log.push('caught ' + e); } })();
(async () => { await null; throw 3; })().catch(e => log.push('late throw ' + e));
""", """
t0,await prim,await native,then called,await plain,async return prim,catch,async throw,caught 2,t1,await thenable,finally,late throw 3,t2,async return promise,resolve(promise),t3,then returns promise,t4,t5
"""),
        ["samePromiseManyWaiters"] = ("""
let res; const p = new Promise(r => res = r);
(async () => { log.push('a' + await p); })();
p.then(v => log.push('b' + v));
(async () => { log.push('c' + await p); })();
p.then(v => log.push('d' + v), () => log.push('no')).then(() => log.push('e'));
res(1);
const q = Promise.reject(9); q.catch(v => log.push('f' + v)); q.then(null, v => log.push('g' + v)); q.then(() => 0).catch(v => log.push('h' + v));
log.push('sync');
""", """
sync,a1,b1,c1,d1,f9,g9,e,h9
"""),
        ["patchedThenSeenByResolveNotAwait"] = ("""
const orig = Promise.prototype.then;
Promise.prototype.then = function (a, b) { log.push('then'); return orig.call(this, a, b); };
const p = Promise.resolve(1);
(async () => { await p; log.push('after await'); })();
new Promise(r => r(p)).then(() => log.push('resolved'));
(async () => p)().then(() => log.push('async returned'));
Promise.all([p]).then(() => log.push('all'));
Promise.race([p]).then(() => log.push('race'));
Promise.allSettled([p]).then(() => log.push('allSettled'));
Promise.any([p]).then(() => log.push('any'));
""", """
then,then,then,then,then,then,then,then,then,then,after await,then,then,all,race,allSettled,any,resolved,async returned
"""),
        ["ownThenAndConstructor"] = ("""
const p = Promise.resolve(1);
p.then = function (a, b) { log.push('own then'); return Promise.prototype.then.call(this, a, b); };
new Promise(r => r(p)).then(() => log.push('resolved'));
Promise.all([p]).then(() => log.push('all'));
const q = Promise.resolve(2);
let n = 0;
Object.defineProperty(q, 'constructor', { get() { log.push('ctor get ' + (++n)); return Promise; } });
(async () => { await q; log.push('await q'); })();
Promise.resolve(q) === q && log.push('same');
q.then(() => log.push('then q'));
const r = Promise.resolve(3);
r.constructor = Object;
(async () => { await r; log.push('await r'); })();
log.push(Promise.resolve(r) === r);
try { r.then(() => {}); log.push('no error'); } catch (e) { log.push(e.name); }
""", """
own then,ctor get 1,ctor get 2,same,ctor get 3,false,no error,own then,await q,then q,all,resolved,await r
"""),
        ["constructorPatched"] = ("""
const p = Promise.resolve(1);
class X extends Promise {}
Promise.prototype.constructor = X;
log.push(p.then(() => {}) instanceof X);
log.push(Promise.resolve(p) === p);
(async () => { await p; log.push('await'); })();
Promise.resolve().then(() => log.push('t1')).then(() => log.push('t2')).then(() => log.push('t3')).then(() => log.push('t4'));
Promise.prototype.constructor = Promise;
log.push(p.then(() => {}) instanceof X, Promise.resolve(p) === p);
""", """
true,false,false,true,t1,t2,await,t3,t4
"""),
        ["speciesPatched"] = ("""
class Y extends Promise { constructor(f) { log.push('Y'); super(f); } }
let got = 0;
Object.defineProperty(Promise, Symbol.species, { get() { got++; return Y; }, configurable: true });
const p = Promise.resolve(1);
log.push(p.then(() => {}) instanceof Y, got);
new Promise(r => r(p)).then(() => log.push('r ' + got));
Promise.all([p]).then(() => log.push('all ' + got));
""", """
Y,true,1,Y,Y,Y,Y,all 5,r 5
"""),
        ["speciesPatchedAfterResolve"] = ("""
const p = Promise.resolve(1);
class Y extends Promise { constructor(f) { log.push('Y'); super(f); } }
const q = new Promise(r => r(p));
q.then(() => log.push('q'));
Object.defineProperty(Promise, Symbol.species, { value: Y, configurable: true });
""", """
Y,q
"""),
        ["thenPatchedAfterResolve"] = ("""
const p = Promise.resolve(1);
const q = new Promise(r => r(p));
q.then(() => log.push('q'));
const o = Promise.prototype.then;
Promise.prototype.then = function (a, b) { log.push('patched'); return o.call(this, a, b); };
""", """
q
"""),
        ["thenAccessor"] = ("""
let n = 0; const o = Promise.prototype.then;
Object.defineProperty(Promise.prototype, 'then', { get() { n++; return o; }, configurable: true });
const p = Promise.resolve(1);
new Promise(r => r(p)).then;
Promise.all([p]);
(async () => { await p; log.push('awaited ' + n); })();
log.push(n);
""", """
3,awaited 3
"""),
        ["thenDeleted"] = ("""
const o = Promise.prototype.then;
const p = Promise.resolve(1);
delete Promise.prototype.then;
const q = new Promise(r => r(p));
o.call(q, v => log.push(v === p));
Promise.prototype.then = o;
log.push(p.then === o);
""", """
true,true
"""),
        ["unrelatedAdditionThenPatch"] = ("""
Promise.prototype.extra = 1; Promise.extra = 2;
const o = Promise.prototype.then;
const p = Promise.resolve(1);
p.then(() => log.push('one'));
Promise.prototype.then = function (a, b) { log.push('patched'); return o.call(this, a, b); };
new Promise(r => r(p)).then(() => log.push('two'));
""", """
patched,one,patched,two
"""),
        ["setProtoAndSubclass"] = ("""
class P extends Promise { then(a, b) { log.push('P.then'); return super.then(a, b); } }
const p = P.resolve(1);
log.push(p instanceof P, p.then(() => {}) instanceof P);
(async () => { await p; log.push('awaited'); })();
Promise.all([p]).then(() => log.push('all'));
P.all([1, Promise.resolve(2)]).then(v => log.push(v.join(), 'P.all'));
const q = Promise.resolve(2);
Object.setPrototypeOf(q, { then(a) { log.push('proto then'); a(5); }, constructor: Promise });
new Promise(r => r(q)).then(v => log.push(v));
const n = Reflect.construct(Promise, [r => r(1)], Object);
log.push(Object.getPrototypeOf(n) === Object.prototype);
function F() {} F.prototype = Promise.prototype;
log.push(Reflect.construct(Promise, [r => r(1)], F) instanceof Promise);
""", """
P.then,true,true,P.then,P.then,P.then,true,true,P.then,P.then,proto then,5,awaited,all,1,2,P.all
"""),
        ["combinatorResolvePatched"] = ("""
const o = Promise.resolve;
let n = 0;
Promise.resolve = function (x) { n++; return o.call(this, x); };
Promise.all([1, 2]).then(v => log.push(v.join(), n));
Promise.resolve = function (x) { return { then(a, b) { log.push(typeof a, typeof b, a.length, b.length, a.name === '', b.name); a(x); a('again'); b('x'); } }; };
Promise.all([7]).then(v => log.push('all', v[0]));
Promise.allSettled([8]).then(v => log.push(JSON.stringify(v)));
Promise.any([9]).then(v => log.push('any', v));
Promise.race([10]).then(v => log.push('race', v));
""", """
function,function,1,1,true,,function,function,1,1,true,,function,function,1,1,true,,function,function,1,1,true,,[{"status":"fulfilled","value":8}],any,9,race,10,1,2,2
"""),
        ["combinatorSameCapabilityFunctions"] = ("""
const seen = [];
Promise.resolve = function (x) { return { then(a, b) { seen.push(a, b); } }; };
Promise.race([1, 2]); log.push(seen[0] === seen[2], seen[1] === seen[3]);
seen.length = 0; Promise.all([1, 2]); log.push(seen[0] === seen[2], seen[1] === seen[3]);
seen.length = 0; Promise.any([1, 2]); log.push(seen[0] === seen[2], seen[1] === seen[3]);
seen.length = 0; Promise.allSettled([1, 2]); log.push(seen[0] === seen[2], seen[1] === seen[3]);
""", """
true,true,false,true,true,false,false,false
"""),
        ["combinatorMixedDirectAndFunctions"] = ("""
const native = Promise.reject('first');
const custom = { then(a, b) { Promise.resolve().then(() => { b('second'); a('third'); }); } };
Promise.all([native, custom]).catch(e => log.push('all', e));
Promise.race([custom, native]).catch(e => log.push('race', e));
Promise.any([native, custom]).then(v => log.push('any', v), e => log.push(e.errors.join()));
Promise.allSettled([native, custom, 3]).then(v => log.push(JSON.stringify(v)));
Promise.all([]).then(v => log.push('empty', v.length));
Promise.any([]).catch(e => log.push(e.name, e.errors.length));
const late = {}; const fulfilled = Promise.resolve(late);
late.then = (a) => { log.push('late then'); a('unwrapped'); };
Promise.race([fulfilled, Promise.resolve('other')]).then(v => log.push('race2', v));
const hang = { then() { log.push('hang then'); } }; const f2 = Promise.resolve({}); f2.then(o => { });
const lateHang = {}; const f3 = Promise.resolve(lateHang); lateHang.then = () => log.push('hang');
Promise.race([f3, Promise.reject('ignored')]).then(v => log.push('no', v), e => log.push('no', e));
""", """
empty,0,AggregateError,0,all,first,race,first,late then,hang,race2,unwrapped,first,second,[{"status":"rejected","reason":"first"},{"status":"rejected","reason":"second"},{"status":"fulfilled","value":3}]
"""),
        ["combinatorOrder"] = ("""
const tick = n => { let p = Promise.resolve(); for (let i = 0; i < n; i++) p = p.then(() => log.push('t' + i)); };
tick(8);
Promise.all([Promise.resolve(1), 2]).then(() => log.push('all'));
Promise.allSettled([Promise.reject(1), 2]).then(() => log.push('allSettled'));
Promise.any([Promise.reject(1), 2]).then(() => log.push('any'));
Promise.any([Promise.reject(1)]).catch(() => log.push('any rejected'));
Promise.race([Promise.resolve(1), 2]).then(() => log.push('race'));
Promise.all([Promise.reject(1), 2]).catch(() => log.push('all rejected'));
Promise.all([new Promise(r => r(Promise.resolve(5)))]).then(v => log.push('nested ' + v[0]));
""", """
t0,t1,all,allSettled,any,any rejected,race,all rejected,t2,t3,nested 5,t4,t5,t6,t7
"""),
        ["awaitThrowingConstructor"] = ("""
const p = Promise.resolve(1);
Object.defineProperty(p, 'constructor', { get() { throw new Error('boom'); } });
(async () => { try { await p; log.push('no'); } catch (e) { log.push('caught ' + e.message); } await p; log.push('no2'); })().catch(e => log.push('rejected ' + e.message));
async function* g() { try { await p; } catch (e) { log.push('gen ' + e.message); yield 1; } await p; }
const it = g(); it.next().then(v => { log.push(v.value); return it.next(); }).catch(e => log.push('gen rejected ' + e.message));
log.push('sync');
""", """
caught boom,gen boom,sync,rejected boom,1,gen rejected boom
"""),
        ["throwsAtEveryPhase"] = ("""
async function f(a = (() => { throw new Error('param'); })()) {}
f().catch(e => log.push(e.message));
async function* ag(a = (() => { throw new Error('agparam'); })()) {}
try { ag(); log.push('no'); } catch (e) { log.push('sync ' + e.message); }
async function h() { throw new TypeError('body'); }
h().catch(e => log.push(e.name, e.message));
async function i() { await 0; null.x; }
i().catch(e => log.push(e.name));
async function* j() { yield 1; throw new Error('gen body'); }
const it = j(); it.next().then(() => it.next()).catch(e => log.push(e.message)).then(() => it.next()).then(r => log.push(JSON.stringify(r)));
async function k() { try { throw 1; } catch { await 0; throw 2; } finally { log.push('fin'); } }
k().catch(e => log.push(e));
function* sync() { throw new Error('sync gen'); }
try { sync().next(); } catch (e) { log.push(e.message); }
const nested = async () => { const inner = () => { throw new Error('deep'); }; await 0; inner(); };
nested().catch(e => log.push(e.message));
""", """
sync agparam,sync gen,param,TypeError,body,fin,TypeError,2,deep,gen body,{"done":true}
"""),
    };

    public static IEnumerable<object[]> CaseNames => Cases.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void MatchesReferenceBehavior(string name)
    {
        var (script, expected) = Cases[name];
        using var e = new JsEngine();
        e.Evaluate("var log = [];\n" + script);
        Assert.Equal(expected, e.Evaluate("log.join(',')").ToString());
    }
}
