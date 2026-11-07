using Jaunt;
using Xunit;

namespace Jaunt.Tests;

/// <summary>
/// Behavior that the performance fast paths must keep exactly: lazily concatenated strings (ropes), Map/Set iterator
/// cursors across compaction, errors the interpreter raises without a .NET exception, and the RegExp test/replace fast
/// paths with their "unmodified regexp" guard. Expected values were produced by Node (V8) from the same script; the only
/// deliberate difference is the wording of "x is not a function", which is compared by kind only.
/// </summary>
public class FastPathSemanticsTests
{
    private const string Script = """
const out = {};
// ropes
out.ropeBasic = (() => { let s = ''; for (let i = 0; i < 2000; i++) s += 'ab' + i; return [s.length, s.slice(0, 12), s.slice(-8), s.charCodeAt(1000), s.indexOf('ab1999')]; })();
out.ropeDeep = (() => { let s = 'x'.repeat(300); for (let i = 0; i < 100000; i++) s += 'y'; return [s.length, s.charAt(s.length - 1), s.lastIndexOf('x')]; })();
out.ropeLeftRight = (() => { let s = 'z'.repeat(300); for (let i = 0; i < 1000; i++) s = (i % 2 ? s + i : i + s); return [s.length, s.slice(0, 6), s.slice(-6)]; })();
out.ropeEquality = (() => { let a = 'q'.repeat(200), b = 'q'.repeat(200); for (let i = 0; i < 50; i++) { a += 'k'; b = b + 'k'; } const m = new Map([[a, 1]]); const o = { [a]: 2 }; return [a === b, a == b, m.get(b), o[b], a < b + 'x', a.localeCompare(b), [a, b].sort().length, new Set([a, b]).size]; })();
out.ropeNumbers = (() => { let s = 'p'.repeat(300); s += 1; s += 2.5; s += null; s += undefined; s += true; s += [1, 2]; s += { toString() { return '!' } }; return s.slice(300); })();
out.ropeKeys = (() => { let k = 'key'.repeat(100); for (let i = 0; i < 20; i++) k += i; const o = {}; o[k] = 1; return [Object.keys(o)[0] === k, k in o, JSON.stringify({ [k]: 1 }).length]; })();
out.ropeJson = (() => { let s = 'a'.repeat(300); s += '"'; s += 'b'; return [JSON.stringify(s).length, JSON.parse(JSON.stringify(s)) === s]; })();
out.ropeRegex = (() => { let s = ''; for (let i = 0; i < 400; i++) s += 'ab'; return [/^(ab)+$/.test(s), s.replace(/b/g, 'c').slice(0, 6), s.split('a').length, s.match(/ab/g).length]; })();
out.ropeSwitch = (() => { let s = 'm'.repeat(300); const t = s + 'n'; let r; switch (t) { case 'm'.repeat(300) + 'n': r = 'hit'; break; default: r = 'miss'; } return r; })();
// map / set cursors
out.mapLive = (() => { const m = new Map(); for (let i = 0; i < 40; i++) m.set(i, i); const it = m.keys(); const seen = []; for (let i = 0; i < 5; i++) seen.push(it.next().value); for (let i = 0; i < 30; i++) m.delete(i); for (let i = 100; i < 140; i++) m.set(i, i); let r; while (!(r = it.next()).done) seen.push(r.value); return [seen.length, seen.slice(0, 8), seen.slice(-3)]; })();
out.mapLruHead = (() => { const m = new Map(); for (let i = 0; i < 256; i++) m.set(i, i); let acc = 0; for (let i = 0; i < 5000; i++) { m.delete(i); m.set(256 + i, i); acc += m.keys().next().value; } return [m.size, acc, [...m.keys()].slice(0, 3)]; })();
out.mapClear = (() => { const m = new Map([[1, 1], [2, 2], [3, 3]]); const it = m.keys(); it.next(); m.clear(); m.set(9, 9); const r = []; let x; while (!(x = it.next()).done) r.push(x.value); return r; })();
out.mapDeleteAll = (() => { const m = new Map(); for (let i = 0; i < 20; i++) m.set(i, i); for (let i = 0; i < 20; i++) m.delete(i); m.set('a', 1); return [[...m.keys()], m.size]; })();
out.setCompact = (() => { const s = new Set(); for (let i = 0; i < 100; i++) s.add(i); const it = s.values(); it.next(); it.next(); for (let i = 0; i < 90; i++) s.delete(i); for (let i = 200; i < 260; i++) s.add(i); const r = []; let x; while (!(x = it.next()).done) r.push(x.value); return [r.length, r.slice(0, 4)]; })();
out.mapForEachMutate = (() => { const m = new Map([[1, 1], [2, 2], [3, 3]]); const seen = []; m.forEach((v, k) => { seen.push(k); if (k === 1) { m.delete(2); m.set(4, 4); } }); return seen; })();
out.manyIterators = (() => { const m = new Map([[1, 'a'], [2, 'b']]); let n = 0; for (let i = 0; i < 20000; i++) { const it = m.entries(); n += it.next().value[0]; } return n; })();
// engine-raised errors
const msg = (f) => { try { f(); return 'no error'; } catch (e) { return e.constructor.name + ': ' + e.message; } };
out.errGetProp = [msg(() => { let o; o.x; }), msg(() => null.y), msg(() => { const o = null; return o['k' + 1]; }), msg(() => undefined[0])];
out.errSetProp = [msg(() => { let o; o.x = 1; }), msg(() => { null['k'] = 1; }), msg(() => { undefined[3] = 1; })];
const kind = (f) => { try { f(); return 'no error'; } catch (e) { return e.constructor.name + (/is not a function$/.test(e.message) ? ' (not a function)' : ': ' + e.message); } };
out.errCall = [kind(() => { const o = {}; o.f(); }), kind(() => { const f = 5; f(); }), kind(() => { let u; u.m(); }), kind(() => { const o = { f: 1 }; o.f(); })];
out.errCaught = (() => { let n = 0; for (let i = 0; i < 100; i++) { try { let u; u.x; } catch (e) { if (e instanceof TypeError) n++; } } return n; })();
out.errFinally = (() => { const log = []; try { try { let u; u.x; } finally { log.push('f'); } } catch (e) { log.push(e.constructor.name); } return log; })();
out.errNested = (() => { function g() { let u; return u.x; } function f() { return g(); } try { f(); return 'none'; } catch (e) { return [e instanceof TypeError, e.message]; } })();
// regexp
out.reOwnExec = (() => { const r = /a/; r.exec = () => null; const q = /a/g; q.exec = () => null; return [r.test('a'), 'aaa'.replace(q, 'b')]; })();
out.reProtoExec = (() => { const o = RegExp.prototype.exec; let n = 0; RegExp.prototype.exec = function (s) { n++; return o.call(this, s); }; try { return ['xaxa'.replace(/a/g, 'b'), n, /a/.test('a')]; } finally { RegExp.prototype.exec = o; } })();
out.reProtoRestored = ['xaxa'.replace(/a/g, 'b'), /a/.test('a')];
out.reProtoGlobal = (() => { const d = Object.getOwnPropertyDescriptor(RegExp.prototype, 'global'); Object.defineProperty(RegExp.prototype, 'global', { get() { return false; }, configurable: true }); try { return 'aaa'.replace(/a/g, 'b'); } finally { Object.defineProperty(RegExp.prototype, 'global', d); } })();
out.reAfterRestore = 'aaa'.replace(/a/g, 'b');
out.reReplaceForms = ['a1b22c333'.replace(/\d+/g, '#'), 'abc'.replace(/(b)/, "[$1|$&|$`|$'|$$]"), '2024-05-06'.replace(/(?<y>\d+)-(?<m>\d+)-(?<d>\d+)/, '$<d>/$<m>/$<y>'), 'a-b-c'.replace(/(\w)/g, (m, p1, pos, str) => `${p1}${pos}${str.length}`), 'abc'.replace(/x*/g, '-'), '\u{1F600}a'.replace(/(?:)/gu, '-')];
out.reLastIndex = (() => { const a = /a/g; a.lastIndex = 5; const x = 'aaa'.replace(a, 'b'); const b = /a/; b.lastIndex = 2; const y = 'aaa'.replace(b, 'b'); return [x, a.lastIndex, y, b.lastIndex]; })();
out.reTest = (() => { const r = /a/g; const res = []; for (let i = 0; i < 4; i++) res.push(r.test('aa'), r.lastIndex); const s = /b/y; s.lastIndex = 1; res.push(s.test('ab'), s.lastIndex, s.test('ab'), s.lastIndex); const v = /a/; let n = 0; v.lastIndex = { valueOf() { n++; return 0; } }; v.test('a'); res.push(n); return res; })();
out.reLegacy = (() => { /(\d)(\d)/.test('a12b'); const t = [RegExp.$1, RegExp.$2, RegExp.lastMatch]; 'a34b'.replace(/(\d)(\d)/g, 'x'); return [t, RegExp.$1, RegExp.$2, RegExp.lastMatch]; })();
out.reFnMutates = (() => { const r = /a/g; return 'aaa'.replace(r, () => { r.lastIndex = 0; return 'z'; }); })();
out.reSubclass = (() => { class R extends RegExp { exec(s) { return super.exec(s); } } return 'aXa'.replace(new R('a', 'g'), 'b'); })();
JSON.stringify(out)
""";

    private const string Expected = """
{"ropeBasic":[10890,"ab0ab1ab2ab3","98ab1999",97,10884],"ropeDeep":[100300,"y",299],"ropeLeftRight":[3190,"998996","997999"],"ropeEquality":[true,true,1,2,true,0,2,1],"ropeNumbers":"12.5nullundefinedtrue1,2!","ropeKeys":[true,true,336],"ropeJson":[305,true],"ropeRegex":[true,"acacac",401,400],"ropeSwitch":"hit","mapLive":[55,[0,1,2,3,4,30,31,32],[137,138,139]],"mapLruHead":[256,12502500,[5000,5001,5002]],"mapClear":[9],"mapDeleteAll":[["a"],1],"setCompact":[70,[90,91,92,93]],"mapForEachMutate":[1,3,4],"manyIterators":20000,"errGetProp":["TypeError: Cannot read properties of undefined (reading 'x')","TypeError: Cannot read properties of null (reading 'y')","TypeError: Cannot read properties of null (reading 'k1')","TypeError: Cannot read properties of undefined (reading '0')"],"errSetProp":["TypeError: Cannot set properties of undefined (setting 'x')","TypeError: Cannot set properties of null (setting 'k')","TypeError: Cannot set properties of undefined (setting '3')"],"errCall":["TypeError (not a function)","TypeError (not a function)","TypeError: Cannot read properties of undefined (reading 'm')","TypeError (not a function)"],"errCaught":100,"errFinally":["f","TypeError"],"errNested":[true,"Cannot read properties of undefined (reading 'x')"],"reOwnExec":[false,"aaa"],"reProtoExec":["xbxb",3,true],"reProtoRestored":["xbxb",true],"reProtoGlobal":"baa","reAfterRestore":"bbb","reReplaceForms":["a#b#c#","a[b|b|a|c|$]c","06/05/2024","a05-b25-c45","-a-b-c-","-😀-a-"],"reLastIndex":["bbb",0,"baa",2],"reTest":[true,1,true,2,false,0,true,1,true,2,false,0,1],"reLegacy":[["1","2","12"],"3","4","34"],"reFnMutates":"zzz","reSubclass":"bXb"}
""";

    [Fact]
    public void FastPathsMatchReferenceBehavior()
    {
        using var engine = new JsEngine();
        var actual = engine.Evaluate(Script).AsString();
        // compare case by case so that a failure names the case
        var exp = System.Text.Json.JsonDocument.Parse(Expected).RootElement;
        var got = System.Text.Json.JsonDocument.Parse(actual).RootElement;
        foreach (var p in exp.EnumerateObject())
        {
            Assert.True(got.TryGetProperty(p.Name, out var g), $"missing case {p.Name}");
            Assert.True(p.Value.GetRawText() == g.GetRawText(), $"{p.Name}: expected {p.Value.GetRawText()} but got {g.GetRawText()}");
        }
        Assert.Equal(exp.EnumerateObject().Count(), got.EnumerateObject().Count());
    }

    [Fact]
    public void StringDoublingStillTripsTheTrackedBytesBudget()
    {
        using var engine = new JsEngine(new JsEngineOptions { Limits = new JsLimits { MaxTrackedBytes = 4 * 1024 * 1024 } });
        Assert.Throws<JsLimitExceededException>(() => engine.Evaluate("let s = 'x'.repeat(300); while (true) s += s"));
    }

    [Fact]
    public void LongConcatenationChainIsLinear()
    {
        using var engine = new JsEngine();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var n = engine.Evaluate("let s = ''; for (let i = 0; i < 200000; i++) s += 'ab'; s.length").AsNumber();
        Assert.Equal(400000, n);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"took {sw.ElapsedMilliseconds} ms (quadratic concatenation?)");
    }

    [Fact]
    public void MapIteratorsAreCheapToCreate()
    {
        using var engine = new JsEngine();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        engine.Evaluate("const m = new Map([[1, 1]]); let n = 0; for (let i = 0; i < 200000; i++) n += m.keys().next().value; n");
        Assert.True(sw.ElapsedMilliseconds < 3000, $"took {sw.ElapsedMilliseconds} ms");
    }
}
