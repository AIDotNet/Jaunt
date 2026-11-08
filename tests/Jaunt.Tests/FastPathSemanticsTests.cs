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
// numeric keys stay numeric through ToPropertyKey (compound/logical assignment, computed object keys, object rest)
out.keyCompound = (() => { const a = [1, 2, 3]; a[1] += 10; a[2] ||= 5; const o = {}; o[3] ??= 'x'; o[1.5] = 'f'; o[-1] = 'n'; o[4294967295] = 'max'; const k = { toString() { return 'ts'; } }; o[k] ??= 'obj'; let n = 0; const side = { toString() { n++; return '2'; } }; a[side] += 100; return [a, Object.keys(o), o[1.5], o['-1'], o['4294967295'], o.ts, n >= 1]; })();   // key conversion count: Jaunt converts once, current spec text twice (not covered by test262)
out.keyComputedLiteral = (() => { const i = 7; const o = { [i]: 'a', [i + 0.5]: 'b', ['x']: 'c' }; const named = { [i]: function () {} }; return [Object.keys(o), named[7].name, { [2 ** 32]: 1 }[4294967296]]; })();
out.keyObjectRest = (() => { const o = { 0: 'a', 1: 'b', 2: 'c', x: 'd' }; const k = 1; const { [k]: one, [0]: zero, ...rest } = o; return [one, zero, Object.keys(rest)]; })();
out.keyTypedRmw = (() => { const t = new Float64Array(4); for (let i = 0; i < 4; i++) t[i] += i * 1.5; t[1.5] += 1; t[-1] = 9; t[10] = 9; return [Array.from(t), t[1.5], t[-1], t[10], Object.keys(t)]; })();
// array literal spread reserves room for trailing elements
out.spreadReserve = (() => { const a = [1, 2, 3]; const b = [...a, 4, 5]; const c = [...a, ...a, 0]; const d = [...[], 1]; const e = [...'ab', 'c']; a.push(9); const g = [...a]; const holes = [1, , 3]; const h = [...holes, 4]; return [b, c, d, e, g, h.length, 1 in h, Object.keys(h)]; })();
out.spreadCustomIter = (() => { const a = [1, 2]; const orig = Array.prototype[Symbol.iterator]; let n = 0; Array.prototype[Symbol.iterator] = function* () { n++; yield 'i'; }; try { return [[...a, 3], n]; } finally { Array.prototype[Symbol.iterator] = orig; } })();
// sort: stability, undefined/holes, inconsistent and mutating comparators, non-closure comparators, re-entrant sort
out.sortStable = (() => { const a = []; for (let i = 0; i < 200; i++) a.push({ k: i % 5, i }); a.sort((p, q) => p.k - q.k); let ok = true; for (let i = 1; i < a.length; i++) if (a[i - 1].k === a[i].k && a[i - 1].i > a[i].i) ok = false; return [ok, a[0].i, a[199].i]; })();
out.sortUndefHoles = (() => { const a = [3, undefined, , 1, undefined, 2]; a.sort((p, q) => p - q); return [a.length, a[0], a[1], a[2], a[3], a[4], 5 in a, 4 in a]; })();
out.sortInconsistent = (() => { const a = []; for (let i = 0; i < 100; i++) a.push((i * 7919) % 101); let x = 0; a.sort(() => (x++ % 3) - 1); return [a.length, a.reduce((s, v) => s + v, 0)]; })();
out.sortMutating = (() => { const a = [5, 4, 3, 2, 1]; a.sort((p, q) => { a.length = 0; a.push(42); return p - q; }); return a; })();
out.sortNonClosure = (() => { const a = [3, 1, 2]; const b = a.slice().sort(Math.min.bind(null)); const c = a.slice().sort(function* () {}); const d = a.slice().sort(new Proxy((p, q) => p - q, {})); const e = a.slice().sort((p, q) => ({ valueOf() { return q - p; } })); return [b, c, d, e]; })();
out.sortReentrant = (() => { const a = [3, 1, 2], b = [9, 8, 7, 6, 5, 4, 3, 2, 1]; a.sort((p, q) => { b.sort((x, y) => x - y); return p - q; }); return [a, b]; })();
out.sortThrows = (() => { const a = [3, 1, 2]; try { a.sort(() => { throw new RangeError('cmp'); }); return 'no'; } catch (e) { return [e.constructor.name, a]; } })();
out.sortDefault = [[10, 9, 1, 'b', 'a', undefined, null].sort(), [1, 2, 3].toSorted((p, q) => q - p), [1, 2, 3].sort(undefined)];
// regexp literal evaluated repeatedly (per-site program cache): fresh object each time, lastIndex independent, prototype swap honored
out.reLiteralFresh = (() => { const seen = new Set(); let eq = 0; for (let i = 0; i < 50; i++) { const r = /a/g; seen.add(r); r.test('aa'); if (r.lastIndex === 1) eq++; } return [seen.size, eq]; })();
out.reLiteralProto = (() => { const o = RegExp.prototype.exec; let n = 0; RegExp.prototype.exec = function (s) { n++; return o.call(this, s); }; try { for (let i = 0; i < 3; i++) /b/.test('b'); } finally { RegExp.prototype.exec = o; } return n; })();
out.reLiteralFlags = (() => { const f = (s) => /x/i.test(s) + ':' + /x/.test(s); return [f('X'), f('x')]; })();
out.keyTypedOob = (() => { const rab = new ArrayBuffer(16, { maxByteLength: 32 }); const fixed = new Float64Array(rab, 0, 2); const tracking = new Float64Array(rab); fixed[1] = 5; rab.resize(8); const r = [fixed.length, fixed[0], fixed[1], tracking.length, tracking[0]]; fixed[0] = 9; tracking[0] = 7; r.push(fixed[0], tracking[0], fixed.length); rab.resize(16); r.push(fixed.length, fixed[0], fixed[1]); return r; })();
out.keyTypedTracking = (() => { const rab = new ArrayBuffer(16, { maxByteLength: 64 }); const t = new Int32Array(rab); const off = new Int32Array(rab, 8); const r = []; for (let i = 0; i < 4; i++) t[i] = i + 1; r.push(t.length, off.length, off[0], off[1]); t[4] = 99; t[-1] = 99; r.push(t[4], t.length); rab.resize(32); r.push(t.length, off.length, t[4], off[2]); t[7] = 7; off[5] = 5; r.push(t[7], off[5], t[5]); rab.resize(4); r.push(t.length, off.length, off[0], t[0], t[1]); t[1] = 3; off[0] = 3; rab.resize(16); r.push(t[1], off[0]); return r; })();
// getter inline caches: own and prototype accessors, redefinition, prototype mutation (epoch), absence caching
out.icGetterProto = (() => { class P { get v() { return this.b * 2; } } const o = new P(); o.b = 3; let t = 0; for (let i = 0; i < 50; i++) t += o.v; Object.defineProperty(P.prototype, 'v', { get() { return 100; }, configurable: true }); for (let i = 0; i < 50; i++) t += o.v; Object.defineProperty(P.prototype, 'v', { value: 7, configurable: true, writable: true }); for (let i = 0; i < 50; i++) t += o.v; return t; })();
out.icGetterOwn = (() => { const o = { get g() { return this.n; }, n: 1 }; let t = 0; for (let i = 0; i < 40; i++) { t += o.g; if (i === 19) o.n = 10; } Object.defineProperty(o, 'g', { get() { return -1; } }); for (let i = 0; i < 10; i++) t += o.g; return t; })();
out.icGetterThrows = (() => { const o = { get g() { if (this.k) throw new RangeError('g'); return 1; } }; let t = 0; for (let i = 0; i < 20; i++) t += o.g; o.k = true; try { o.g; return 'no'; } catch (e) { return [e.constructor.name, t]; } })();
out.icTypedLength = (() => { const rab = new ArrayBuffer(16, { maxByteLength: 64 }); const a = new Float64Array(rab); const b = new Float64Array(rab, 0, 1); let t = 0; for (let i = 0; i < 20; i++) t += a.length + b.length; rab.resize(32); for (let i = 0; i < 20; i++) t += a.length + b.length; rab.resize(0); for (let i = 0; i < 20; i++) t += a.length + b.length; return t; })();
out.icMapSize = (() => { const m = new Map(); let t = 0; for (let i = 0; i < 30; i++) { m.set(i, i); t += m.size; } m.clear(); for (let i = 0; i < 5; i++) t += m.size; return t; })();
out.icNotFound = (() => { const proto = { a: 1 }; const o = Object.create(proto); let t = 0; for (let i = 0; i < 30; i++) t += o.b === undefined ? 1 : 0; proto.b = 5; for (let i = 0; i < 30; i++) t += o.b; Object.prototype.b = 9; delete proto.b; for (let i = 0; i < 30; i++) t += o.b; delete Object.prototype.b; for (let i = 0; i < 30; i++) t += o.b === undefined ? 1 : 0; o.b = 2; return [t, o.b]; })();
out.icNotFoundProxyProto = (() => { const p = new Proxy({}, { get(tg, k) { return k === 'z' ? 'proxied' : undefined; }, has() { return true; } }); const o = Object.create(p); let t = ''; for (let i = 0; i < 20; i++) t += o.z === undefined ? '-' : o.z[0]; return t; })();
out.icMegamorphic = (() => { const objs = []; for (let i = 0; i < 12; i++) { const o = {}; o['p' + i] = i; o.k = i; objs.push(o); } let t = 0; for (let r = 0; r < 20; r++) for (const o of objs) t += o.k + (o.missing === undefined ? 1 : 0); objs[3].k = 100; Object.defineProperty(objs[4], 'k', { get() { return 1000; } }); for (const o of objs) t += o.k; return t; })();
out.icMegamorphicProto = (() => { class A { get z() { return 1; } } class B extends A {} class C extends A { get z() { return 3; } } const list = []; for (let i = 0; i < 12; i++) { const o = i % 3 === 0 ? new A() : i % 3 === 1 ? new B() : new C(); o['q' + i] = 1; list.push(o); } let t = 0; for (let r = 0; r < 10; r++) for (const o of list) t += o.z; A.prototype.z2 = 5; for (const o of list) t += o.z2; return t; })();
out.icStringMethod = (() => { let s = ''; for (let i = 0; i < 300; i++) s += 'ab'; let t = 0; for (let i = 0; i < 100; i++) t += s.charCodeAt(i) + s.at(-1).length; const o = String.prototype.charCodeAt; String.prototype.charCodeAt = function () { return 1; }; try { for (let i = 0; i < 10; i++) t += s.charCodeAt(i); } finally { String.prototype.charCodeAt = o; } return t + s.charCodeAt(0); })();
// string for-of fast path: code points, surrogate pairs, ropes, and the pristine guard
out.strForOf = (() => { let s = ''; for (let i = 0; i < 50; i++) s += 'a\u{1F600}b'; const parts = []; let n = 0; for (const c of s) { n++; if (parts.length < 4) parts.push(c.length); } const u = [...'x😀y\uD83D']; return [n, parts, u.length, u[1].length, u[3].length]; })();
out.strForOfPatched = (() => { const orig = String.prototype[Symbol.iterator]; String.prototype[Symbol.iterator] = function* () { yield 'X'; }; let r; try { r = [...'abc']; for (const c of 'zz') r.push(c); } finally { String.prototype[Symbol.iterator] = orig; } const after = [...'ab']; const nextOrig = Object.getPrototypeOf(''[Symbol.iterator]()).next; Object.getPrototypeOf(''[Symbol.iterator]()).next = function () { return { done: true }; }; let r2; try { r2 = [...'abc'].length; } finally { Object.getPrototypeOf(''[Symbol.iterator]()).next = nextOrig; } return [r, after, r2, [...'ab']]; })();
out.strForOfBreak = (() => { let n = 0; for (const c of 'abcdef') { n++; if (c === 'c') break; } const o = new String('hey'); let m = ''; for (const c of o) m += c; return [n, m]; })();
// call frames: overlapping register windows (arguments stay in place, the callee may clobber the caller's registers above them), extra and missing arguments, nested calls in argument lists, sloppy/strict this, legacy caller, stack traces, coroutine callers, new
out.callExtraArgs = (() => { function f(a) { return [a, arguments.length, arguments[1], arguments[2]]; } function g(a, b, c) { return [a, b, c]; } return [f(1, 2, 3), g(1), f(), (function (...r) { return r; })(1, 2, 3, 4, 5, 6), (function (a, ...r) { return [a, r, arguments.length]; })(1, 2, 3, 4, 5, 6)]; })();
out.callParamMutation = (() => { let x = 1; const y = 2; function f(a, b) { a = 99; b = 98; return a + b; } const r = f(x, y); function m(a) { arguments[0] = 5; return a; } function s(a) { 'use strict'; arguments[0] = 5; return a; } return [r, x, y, m(1), s(1)]; })();
out.callNestedArgs = (() => { function g(a, b, c) { return a * 100 + b * 10 + c; } function h(n) { return n === 0 ? 0 : g(h(n - 1) % 10, n, h(n - 1) % 10); } function k(a, b) { return a + b; } return [h(4), k(k(1, 2), k(k(3, 4), 5)), g(k(1, 1), g(1, 1, 1), k(2, 2)), [k(1, 2), k(3, 4)].map((v) => k(v, 1))]; })();
out.callLiveAcross = (() => { function id(v) { return v; } let acc = ''; for (let i = 0; i < 5; i++) { const a = id('a' + i), b = id(a + '-'), c = id(b + i); acc += c + id(a); } const o = { m(x) { return x + this.k; }, k: 1 }; let t = 0; for (let i = 0; i < 10; i++) { const v = o.m(i); t += v + o.m(v); } const arr = [id(1), id(2), id(3)]; const obj = { a: id('x'), b: [id('y'), id('z')] }; return [acc, t, arr, obj, `${id(1)}-${id(2)}`, id(1) + id(2) * id(3)]; })();
out.callThrowCatch = (() => { function thrower(a, b) { if (b) throw new RangeError('t' + a); return a; } const log = []; for (let i = 0; i < 4; i++) { const pre = 'p' + i; try { log.push(thrower(pre, i % 2)); } catch (e) { log.push(e.message + pre); } finally { log.push(pre.length); } } function deep(n) { return n ? deep(n - 1) : (() => { throw new TypeError('bottom'); })(); } try { deep(50); } catch (e) { log.push(e.message); } return log; })();
out.callThis = (() => { function sloppy() { return this === globalThis; } function strict() { 'use strict'; return this; } String.prototype.kind = function () { return typeof this; }; String.prototype.kindStrict = function () { 'use strict'; return typeof this; }; const r = [sloppy(), strict(), 'x'.kind(), 'x'.kindStrict(), sloppy.call(5), typeof strict.call(5), sloppy.call(null), (0, sloppy)(), ({ f: sloppy }).f()]; delete String.prototype.kind; delete String.prototype.kindStrict; return r; })();
out.callLegacyCaller = (() => { function outer() { return inner(); } function inner() { return inner.caller === outer; } return [outer(), inner.caller]; })();
out.callStack = (() => { function a() { return b(); } function b() { return new Error('x').stack; } const s = a(); return [/at b \(/.test(s), /at a \(/.test(s), s.indexOf('at b') < s.indexOf('at a')]; })();
out.callFromGenerator = (() => { function add(a, b) { return a + b; } function* g() { let i = 0; while (i < 3) yield add(i++, 10); } const r = [...g()]; function* d() { yield* g(); } async function af() { return add(1, 2); } const p = af(); return [r, [...d()], p instanceof Promise]; })();
out.callNew = (() => { class P { constructor(x, y) { this.x = x; this.y = y; this.n = arguments.length; } } function F(a) { this.a = a; this.rest = Array.prototype.slice.call(arguments, 1); } const p = new P(1); const q = new P(1, 2, 3); const f = new F(1, 2, 3); class D extends P { constructor() { super(7, 8); this.d = 1; } } const d = new D(); function R() { return { r: 1 }; } return [p.x, p.y, p.n, q.n, f.a, f.rest, d.x, d.y, d.d, new P(...[4, 5]).y, new R().r, new F().a]; })();
out.callTail = (() => { 'use strict'; function loop(n, acc) { return n === 0 ? acc : loop(n - 1, acc + n); } function even(n) { return n === 0 ? true : odd(n - 1); } function odd(n) { return n === 0 ? false : even(n - 1); } return [loop(5000, 0), even(5001), odd(5001)]; })();
// Map/Set table (chained hash index): deletion and insertion during iteration, compaction, -0/NaN, rope and number keys, clear
out.tableDelIter = (() => { const m = new Map(); for (let i = 0; i < 64; i++) m.set(i, i); const seen = []; for (const [k] of m) { seen.push(k); if (k % 2 === 0) { m.delete(k + 1); m.set('n' + k, k); } } return [seen.length, m.size, [...m.keys()].slice(-3), m.has(1), m.has(0), m.get('n62')]; })();
out.tableCompact = (() => { const m = new Map(); for (let i = 0; i < 1000; i++) m.set('k' + i, i); for (let i = 0; i < 1000; i++) if (i % 4) m.delete('k' + i); const it = m.entries(); it.next(); for (let i = 1000; i < 1300; i++) m.set('k' + i, i); let n = 0, last; for (const e of it) { n++; last = e; } return [m.size, n, last, m.get('k4'), m.get('k1299'), m.has('k5')]; })();
out.tableZeroNaN = (() => { const m = new Map([[0, 'a'], [NaN, 'b']]); m.set(-0, 'c'); const s = new Set([-0, 0, NaN, NaN, 'NaN', '0']); return [m.size, m.get(-0), m.get(NaN), Object.is([...m.keys()][0], 0), s.size, s.has(-0), m.delete(-0), m.size, m.delete(NaN), m.size]; })();
out.tableKeys = (() => { let rope = 'x'.repeat(300); for (let i = 0; i < 20; i++) rope += 'y'; const flat = 'x'.repeat(300) + 'y'.repeat(20); const m = new Map(); m.set(rope, 1); m.set(1.5, 2); m.set(2 ** 40, 3); m.set(true, 4); m.set(null, 5); m.set(undefined, 6); m.set(Symbol.iterator, 7); m.set(10n, 8); const o = {}; m.set(o, 9); m.set('1', 10); return [m.get(flat), m.get(1.5), m.get(2 ** 40), m.get(true), m.get(null), m.get(undefined), m.get(Symbol.iterator), m.get(10n), m.get(o), m.get({}), m.get(1), m.get('1'), m.size]; })();
out.tableClearIter = (() => { const s = new Set([1, 2, 3, 4]); const it = s.values(); it.next(); s.clear(); s.add(9); s.add(10); const r = [...it]; s.delete(9); s.add(11); return [r, [...s], s.size]; })();
out.tableBig = (() => { const m = new Map(); for (let i = 0; i < 20000; i++) m.set(i * 7919 % 20011, i); let t = 0; for (let i = 0; i < 20011; i++) if (m.has(i)) t += m.get(i) & 7; for (let i = 0; i < 20000; i += 3) m.delete(i * 7919 % 20011); let n = 0; for (const v of m.values()) n += v & 1; return [m.size, t, n]; })();
// JSON.parse reviver: duplicate keys, context.source, holder mutation during revive, nested arrays, key order with index-like keys
out.reviveDupes = JSON.stringify(JSON.parse('{"a":1,"b":[1,{"c":"x"}],"a":2,"d":1.50,"e":"\\u0041","b":[3]}', (k, v, ctx) => typeof v !== 'object' || v === null ? String(v) + '|' + ctx.source : v));
out.reviveIndexKeys = JSON.stringify(JSON.parse('{"b":1,"2":2,"a":3,"1":4}', (k, v, ctx) => typeof v === 'number' ? k + ':' + ctx.source : v));
out.reviveMutate = (() => { const log = []; const r = JSON.parse('{"a":1,"b":2,"c":3}', function (k, v) { if (k === 'a') { delete this.b; this.z = 9; } log.push(k); return v === 1 ? undefined : v; }); return [log, r]; })();
out.reviveArrays = JSON.parse('[1,[2,[3,[4]]],{"x":[5]}]', (k, v) => typeof v === 'number' ? v * 10 : v);
out.reviveSourceObj = (() => { let objCtx; const r = JSON.parse('{"o":{"p":1}}', (k, v, ctx) => { if (k === 'o') objCtx = Object.keys(ctx).length; return v; }); return [objCtx, r.o.p]; })();
out.reviveHolderIdentity = (() => { const seen = []; JSON.parse('{"a":{"b":1}}', function (k, v, ctx) { seen.push(typeof ctx === 'object' && ctx !== null && !Array.isArray(ctx)); return v; }); return seen; })();
// string + integer concatenation
out.concatInt = ['k' + 5, 5 + 'k', 'a' + 0, 'a' + -0, -0 + 'a', 'a' + 2147483647, 'a' + 2147483648, 'a' + -2147483648, 'a' + -2147483649, 'a' + 1.5, 'a' + 1e21, 'a' + NaN, 'a' + -1, 'x'.repeat(300) + 7 === 'x'.repeat(300) + '7', ('p' + 12).length, 'a' + 1e9, 'a' + 123456789, ('' + 42) + '' === '42'];
// filter/flat/flatMap collect results in a scratch buffer: species, re-entrancy, throwing callbacks, large results
out.sinkSpecies = (() => { let keep; class A extends Array { static get [Symbol.species]() { return function () { return keep = []; }; } } const r = A.from([1, 2, 3]).filter(x => { if (keep) keep.push('s' + keep.length); return true; }); return [r === keep, r.length, r.join()]; })();
out.sinkReentrant = (() => { const inner = [1, 2, 3, 4]; const r = [1, 2, 3].flatMap(x => inner.filter(y => y > x).flatMap(y => [x, y])); return r; })();
out.sinkThrows = (() => { try { [1, 2, 3].filter(x => { if (x === 2) throw new RangeError('f'); return true; }); return 'no'; } catch (e) { const ok = [1, 2, 3].filter(x => x > 1); return [e.constructor.name, ok]; } })();
out.sinkLarge = (() => { const a = Array.from({ length: 70000 }, (_, i) => i); const f = a.filter(x => x % 3 === 0); const fm = a.flatMap(x => x % 1000 === 0 ? [x, x] : []); const fl = [a.slice(0, 5), [a.slice(5, 7)]].flat(2); return [f.length, f[f.length - 1], fm.length, fm[fm.length - 1], fl.length, [].flatMap(x => x).length, [1, , 3].flat().length]; })();
JSON.stringify(out)
""";

    private const string Expected = """
{"ropeBasic":[10890,"ab0ab1ab2ab3","98ab1999",97,10884],"ropeDeep":[100300,"y",299],"ropeLeftRight":[3190,"998996","997999"],"ropeEquality":[true,true,1,2,true,0,2,1],"ropeNumbers":"12.5nullundefinedtrue1,2!","ropeKeys":[true,true,336],"ropeJson":[305,true],"ropeRegex":[true,"acacac",401,400],"ropeSwitch":"hit","mapLive":[55,[0,1,2,3,4,30,31,32],[137,138,139]],"mapLruHead":[256,12502500,[5000,5001,5002]],"mapClear":[9],"mapDeleteAll":[["a"],1],"setCompact":[70,[90,91,92,93]],"mapForEachMutate":[1,3,4],"manyIterators":20000,"errGetProp":["TypeError: Cannot read properties of undefined (reading 'x')","TypeError: Cannot read properties of null (reading 'y')","TypeError: Cannot read properties of null (reading 'k1')","TypeError: Cannot read properties of undefined (reading '0')"],"errSetProp":["TypeError: Cannot set properties of undefined (setting 'x')","TypeError: Cannot set properties of null (setting 'k')","TypeError: Cannot set properties of undefined (setting '3')"],"errCall":["TypeError (not a function)","TypeError (not a function)","TypeError: Cannot read properties of undefined (reading 'm')","TypeError (not a function)"],"errCaught":100,"errFinally":["f","TypeError"],"errNested":[true,"Cannot read properties of undefined (reading 'x')"],"reOwnExec":[false,"aaa"],"reProtoExec":["xbxb",3,true],"reProtoRestored":["xbxb",true],"reProtoGlobal":"baa","reAfterRestore":"bbb","reReplaceForms":["a#b#c#","a[b|b|a|c|$]c","06/05/2024","a05-b25-c45","-a-b-c-","-😀-a-"],"reLastIndex":["bbb",0,"baa",2],"reTest":[true,1,true,2,false,0,true,1,true,2,false,0,1],"reLegacy":[["1","2","12"],"3","4","34"],"reFnMutates":"zzz","reSubclass":"bXb","keyCompound":[[1,12,103],["3","1.5","-1","4294967295","ts"],"f","n","max","obj",true],"keyComputedLiteral":[["7","7.5","x"],"7",1],"keyObjectRest":["b","a",["2","x"]],"keyTypedRmw":[[0,1.5,3,4.5],null,null,null,["0","1","2","3"]],"spreadReserve":[[1,2,3,4,5],[1,2,3,1,2,3,0],[1],["a","b","c"],[1,2,3,9],4,true,["0","1","2","3"]],"spreadCustomIter":[["i",3],1],"sortStable":[true,0,199],"sortUndefHoles":[6,1,2,3,null,null,false,true],"sortInconsistent":[100,4990],"sortMutating":[1,2,3,4,5],"sortNonClosure":[[3,1,2],[3,1,2],[1,2,3],[3,2,1]],"sortReentrant":[[1,2,3],[1,2,3,4,5,6,7,8,9]],"sortThrows":["RangeError",[3,1,2]],"sortDefault":[[1,10,9,"a","b",null,null],[3,2,1],[1,2,3]],"reLiteralFresh":[50,50],"reLiteralProto":3,"reLiteralFlags":["true:false","true:true"],"keyTypedOob":[0,null,null,1,0,null,7,0,2,7,0],"keyTypedTracking":[4,2,3,4,null,4,8,6,0,0,5,5,0,1,0,null,1,null,0,0],"icGetterProto":5650,"icGetterOwn":210,"icGetterThrows":["RangeError",20],"icTypedLength":160,"icMapSize":465,"icNotFound":[480,2],"icNotFoundProxyProto":"pppppppppppppppppppp","icMegamorphic":2719,"icMegamorphicProto":260,"icStringMethod":9957,"strForOf":[150,[1,2,1,1],4,2,1],"strForOfPatched":[["X","X"],["a","b"],0,["a","b"]],"strForOfBreak":[3,"hey"],"callExtraArgs":[[1,3,2,3],[1,null,null],[null,0,null,null],[1,2,3,4,5,6],[1,[2,3,4,5,6],6]],"callParamMutation":[197,1,2,5,1],"callNestedArgs":[40,15,1314,[4,8]],"callLiveAcross":["a0-0a0a1-1a1a2-2a2a3-3a3a4-4a4",120,[1,2,3],{"a":"x","b":["y","z"]},"1-2",7],"callThrowCatch":["p0",2,"tp1p1",2,"p2",2,"tp3p3",2,"bottom"],"callThis":[true,null,"object","string",false,"number",true,true,false],"callLegacyCaller":[true,null],"callStack":[true,true,true],"callFromGenerator":[[10,11,12],[10,11,12],true],"callNew":[1,null,1,3,1,[2,3],7,8,1,5,1,null],"callTail":[12502500,false,true],"tableDelIter":[64,64,["n58","n60","n62"],false,true,62],"tableCompact":[550,549,["k1299",1299],4,1299,false],"tableZeroNaN":[2,"c","b",true,4,true,true,1,true,0],"tableKeys":[1,2,3,4,5,6,7,8,9,null,null,10,10],"tableClearIter":[[9,10],[10,11],2],"tableBig":[13333,70000,6667],"reviveDupes":"{\"a\":\"2|2\",\"b\":[\"3|3\"],\"d\":\"1.5|1.50\",\"e\":\"A|\\\"\\\\u0041\\\"\"}","reviveIndexKeys":"{\"1\":\"1:4\",\"2\":\"2:2\",\"b\":\"b:1\",\"a\":\"a:3\"}","reviveMutate":[["a","b","c",""],{"c":3,"z":9}],"reviveArrays":[10,[20,[30,[40]]],{"x":[50]}],"reviveSourceObj":[0,1],"reviveHolderIdentity":[true,true,true],"concatInt":["k5","5k","a0","a0","0a","a2147483647","a2147483648","a-2147483648","a-2147483649","a1.5","a1e+21","aNaN","a-1",true,3,"a1000000000","a123456789",true],"sinkSpecies":[true,3,"1,2,3"],"sinkReentrant":[1,2,1,3,1,4,2,3,2,4,3,4],"sinkThrows":["RangeError",[2,3]],"sinkLarge":[23334,69999,140,69000,7,0,2]}
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

    // Object model fast paths: shape-mode arguments objects (mapping, attribute changes, ICs), attribute changes that keep
    // an object in shape mode, big-shape lookup, array-backed private elements, `with`. Expected value from Node (V8), except
    // for the has:a before get:a under a proxy `with` object: GetBindingValue calls HasProperty (9.1.1.2.6), which V8 omits.
    private const string ObjectModelScript = """
const out = {};
const sloppy = new Function(`
  const r = {};
  r.alias = (function (a, b) { arguments[0] = 10; b = 20; return [a, arguments[0], arguments[1], arguments.length]; })(1, 2);
  r.extra = (function (a) { arguments[1] = 'x'; arguments[3] = 'y'; return [arguments[1], arguments[3], arguments.length, Object.keys(arguments)]; })(1, 2);
  r.missing = (function (a, b) { b = 5; return [arguments[1], arguments.length, 1 in arguments]; })(1);
  r.deleteRemap = (function (a) { delete arguments[0]; a = 2; const u = arguments[0]; arguments[0] = 3; return [u, a, arguments[0], Object.keys(arguments)]; })(1);
  r.definedRO = (function (a) { Object.defineProperty(arguments, '0', { writable: false }); a = 2; arguments[0] = 3; return [a, arguments[0]]; })(1);
  r.definedValue = (function (a) { Object.defineProperty(arguments, '0', { value: 7 }); const x = a; a = 8; return [x, arguments[0]]; })(1);
  r.getter = (function (a) { let n = 0; Object.defineProperty(arguments, '0', { get() { return ++n; }, set(v) { n = v; } }); arguments[0]; arguments[0] = 40; return [arguments[0], a, n]; })(1);
  r.frozen = (function (a) { Object.freeze(arguments); a = 9; arguments[0] = 1; return [a, arguments[0], Object.isFrozen(arguments)]; })(5);
  r.sealed = (function (a) { Object.seal(arguments); a = 9; const v = arguments[0]; arguments[0] = 1; return [a, v, Object.isSealed(arguments)]; })(5);
  r.desc = (function (a) { a = 3; const d = Object.getOwnPropertyDescriptor(arguments, '0'); const l = Object.getOwnPropertyDescriptor(arguments, 'length'); return [d, l, typeof Object.getOwnPropertyDescriptor(arguments, 'callee').value]; })(1);
  r.named = (function (a) { const o = []; for (let i = 0; i < 3; i++) { o.push(arguments.length); arguments.length = i; } arguments.extra = 1; return [o, Reflect.ownKeys(arguments).map(String), arguments['0']]; })(1, 2);
  r.stringKey = (function (a) { a = 'new'; const k = '0'; return [arguments[k], arguments['0'], k in arguments]; })('old');
  r.inherited = (function (a) { const c = Object.create(arguments); a = 2; return [c[0], c.length, (c[0] = 5, c[0]), arguments[0], Object.keys(c)]; })(1);
  r.iter = (function (a, b) { a = 'p'; return [...arguments, Array.prototype.slice.call(arguments, 1), Array.from(arguments)]; })(1, 2);
  r.tostr = (function () { return [Object.prototype.toString.call(arguments), arguments[Symbol.iterator] === Array.prototype.values]; })();
  r.icShare = (function () { const get = (o) => o.length, read = (o) => o[0]; const res = []; const plain = {}; plain[0] = 'plain'; Object.defineProperty(plain, 'length', { value: 'L', writable: true, configurable: true }); res.push(get(plain), read(plain)); (function (a) { a = 'live'; res.push(get(arguments), read(arguments)); })('stale'); return res; })();
  r.manyArgs = (function (a, b) { a = 'z'; const n = []; for (let i = 0; i < arguments.length; i += 15) n.push(arguments[i]); arguments[69] = 'q'; return [n, arguments.length, arguments[69]]; }).apply(null, Array.from({ length: 70 }, (_, i) => i));
  return r;
`)();
out.sloppy = sloppy;
out.strict = (function (a) { 'use strict'; a = 2; arguments[1] = 'b'; const e = []; try { arguments.callee; } catch (x) { e.push(x.constructor.name); } Object.freeze(arguments); try { arguments[0] = 9; } catch (x) { e.push(x.constructor.name); } return [arguments[0], arguments[1], arguments.length, e, Object.isFrozen(arguments)]; })(1, 'x');
// attribute changes that keep the object's shape
out.freeze = (() => {
  const read = (o) => o.a, write = (o, v) => { o.a = v; return o.a; }, add = (o) => { o.z = 1; return o.z; };
  const plain = { a: 1, b: 2 }, frozen = Object.freeze({ a: 1, b: 2 }), r = [];
  r.push(read(plain), read(frozen), write(plain, 5), write(frozen, 5), add({ a: 1, b: 2 }), add(frozen), add({ a: 1, b: 2 }));
  r.push(Object.isFrozen(frozen), Object.isSealed(frozen), Object.isFrozen(plain), delete frozen.a, frozen.a, Object.getOwnPropertyDescriptor(frozen, 'a'));
  const sealed = Object.seal({ a: 1, get b() { return 2; } });
  r.push(write(sealed, 9), delete sealed.a, Object.isSealed(sealed), Object.isFrozen(sealed), Object.getOwnPropertyDescriptor(sealed, 'a'));
  r.push({ ...frozen }, Object.getOwnPropertyDescriptor({ ...frozen }, 'a').writable, Object.keys(frozen), JSON.stringify(frozen));
  const acc = Object.freeze({ get g() { return 1; }, set g(v) { this.hit = v; } });
  r.push(typeof Object.getOwnPropertyDescriptor(acc, 'g').set, Object.isFrozen(acc));
  r.push(Object.isFrozen(Object.preventExtensions({})), Object.isFrozen(Object.preventExtensions({ x: 1 })), Object.isFrozen(Object.freeze({})), Object.isExtensible(Object.freeze({})));
  return r;
})();
out.protoFreeze = (() => {
  const proto = { x: 1 }, child = Object.create(proto), set = (o) => { o.x = 2; return [o.x, Object.keys(o)]; };
  const r = [set(Object.create(proto))];
  Object.freeze(proto);
  r.push(set(child));
  const p2 = { y: 1 }, read = (o) => o.y, c2 = Object.create(p2);
  r.push(read(c2));
  Object.defineProperty(p2, 'y', { get() { return 'getter'; } });
  r.push(read(c2));
  return r;
})();
out.redefine = (() => {
  const o = { a: 1, b: 2, c: 3 }, read = (x) => x.b, r = [read(o)];
  Object.defineProperty(o, 'b', { enumerable: false });
  r.push(read(o), Object.keys(o), JSON.stringify(o), { ...o }, Object.getOwnPropertyDescriptor(o, 'b'));
  Object.defineProperty(o, 'b', { get() { return 'g'; }, configurable: true });
  r.push(read(o), Object.getOwnPropertyDescriptor(o, 'b').set === undefined, Object.keys(o));
  Object.defineProperty(o, 'b', { value: 'd', writable: true, enumerable: true });
  r.push(read(o), Object.keys(o), Object.entries(o));
  o.d = 4; delete o.a; r.push(Object.keys(o));
  const same1 = {}, same2 = {};
  for (const s of [same1, same2]) { s.k = 1; Object.defineProperty(s, 'k', { writable: false }); s.m = 2; }
  same1.k = 5; same2.m = 6;
  r.push(same1, same2, Object.getOwnPropertyDescriptor(same2, 'k').writable);
  const fn = function () {}; Object.defineProperty(fn, 'name', { value: 'n', configurable: false }); Object.defineProperty(fn, 'length', { writable: true });
  fn.length = 9; r.push(fn.name, fn.length, Object.getOwnPropertyNames(fn).slice(0, 2));
  const sym = Symbol('s'), so = { [sym]: 1, z: 2 }; Object.defineProperty(so, sym, { enumerable: false }); r.push(Reflect.ownKeys(so).length, Object.assign({}, so)[sym]);
  return r;
})();
// property lookup in shapes with many keys
out.bigShape = (() => {
  const s1 = Symbol('a'), s2 = Symbol('b');
  const mk = () => { const o = {}; for (let i = 0; i < 40; i++) o['key' + i] = i; o[s1] = 's1'; o['k' + 'ey7' + 'x'] = 'dyn'; return o; };
  const o = mk();
  return [o['key' + 39], o['key' + 40], o.key7, o.key7x, o[s1], o[s2], s2 in o, 'key0' in o, '' in o, ({ ...o }).key39, Object.keys(o).length];
})();
// private elements
out.privates = (() => {
  class A { #x = 1; #m() { return 'm' + this.#x; } get #g() { return this.#x * 2; } set #g(v) { this.#x = v; } static has(o) { return #x in o; }
    bump() { this.#g = this.#g + 1; return [this.#x, this.#m(), this.#g]; }
    static read(o) { return o.#x; } static write(o, v) { o.#x = v; } static callm(o) { return o.#m(); } setm() { this.#m = 1; } }
  class Ret { constructor(o) { return o; } }
  class B extends Ret { #y = 'y'; #z = 'z'; #w = 'w'; #v = 'v'; #u = 'u'; static get(o) { return [o.#y, o.#z, o.#w, o.#v, o.#u]; } }
  const msg = (f) => { try { return f(); } catch (e) { return e.constructor.name; } };
  const a = new A(), plain = {}, r = [a.bump(), A.has(a), A.has(plain), msg(() => A.read(plain)), msg(() => A.write(plain, 1)), msg(() => A.callm(plain)), msg(() => a.setm()), msg(() => A.has(1))];
  new B(plain); r.push(B.get(plain), msg(() => new B(plain)), msg(() => B.get({})));
  r.push(Object.keys(plain), JSON.stringify(a));
  const fr = Object.freeze(new A()); r.push(msg(() => { A.write(fr, 7); return A.read(fr); }));
  return r;
})();
// with
out.withScope = new Function(`
  const log = [];
  const o = { p: 1, q: 2, [Symbol.unscopables]: { q: true } };
  var q = 'outer', r = 'outer';
  with (o) { p = 5; log.push(p, q, r); var r2 = p; }
  const px = new Proxy({ a: 1 }, { has(t, k) { log.push('has:' + String(k)); return k in t; }, get(t, k) { log.push('get:' + String(k)); return t[k]; } });
  with (px) { log.push(a); }
  const g = { get v() { log.push('getter'); return 'V'; }, m() { return this === g; } };
  with (g) { log.push(v, m()); }
  const c = Object.create({ inh: 'I' }); with (c) { log.push(inh); inh = 'J'; log.push(inh, Object.keys(c)); }
  return [log, o.p, r2];
`)();
JSON.stringify(out);
""";

    private const string ObjectModelExpected = """
{"sloppy":{"alias":[10,10,20,2],"extra":["x","y",2,["0","1","3"]],"missing":[null,1,false],"deleteRemap":[null,2,3,["0"]],"definedRO":[2,1],"definedValue":[7,8],"getter":[41,1,41],"frozen":[9,5,true],"sealed":[1,9,true],"desc":[{"value":3,"writable":true,"enumerable":true,"configurable":true},{"value":1,"writable":true,"enumerable":false,"configurable":true},"function"],"named":[[2,0,1],["0","1","length","callee","extra","Symbol(Symbol.iterator)"],1],"stringKey":["new","new",true],"inherited":[2,1,5,2,["0"]],"iter":["p",2,[2],["p",2]],"tostr":["[object Arguments]",true],"icShare":["L","plain",1,"live"],"manyArgs":[["z",15,30,45,60],70,"q"]},"strict":[1,"b",2,["TypeError","TypeError"],true],"freeze":[1,1,5,1,1,null,1,true,true,false,false,1,{"value":1,"writable":false,"enumerable":true,"configurable":false},9,false,true,false,{"value":9,"writable":true,"enumerable":true,"configurable":false},{"a":1,"b":2},true,["a","b"],"{\"a\":1,\"b\":2}","function",true,true,false,true,false],"protoFreeze":[[2,["x"]],[1,[]],1,"getter"],"redefine":[2,2,["a","c"],"{\"a\":1,\"c\":3}",{"a":1,"c":3},{"value":2,"writable":true,"enumerable":false,"configurable":true},"g",true,["a","c"],"d",["a","b","c"],[["a",1],["b","d"],["c",3]],["b","c","d"],{"k":1,"m":2},{"k":1,"m":6},false,"n",9,["length","name"],2,null],"bigShape":[39,null,7,"dyn","s1",null,false,true,false,39,41],"privates":[[3,"m3",6],true,false,"TypeError","TypeError","TypeError","TypeError","TypeError",["y","z","w","v","u"],"TypeError","TypeError",[],"{}",7],"withScope":[[5,"outer","outer","has:log","has:a","get:Symbol(Symbol.unscopables)","has:a","get:a",1,"getter","V",true,"I","J",["inh"]],5,5]}
""";

    [Fact]
    public void ObjectModelFastPathsMatchReferenceBehavior()
    {
        using var engine = new JsEngine();
        var exp = System.Text.Json.JsonDocument.Parse(ObjectModelExpected).RootElement;
        var got = System.Text.Json.JsonDocument.Parse(engine.Evaluate(ObjectModelScript).AsString()).RootElement;
        foreach (var p in exp.EnumerateObject())
        {
            Assert.True(got.TryGetProperty(p.Name, out var g), $"missing case {p.Name}");
            Assert.Equal(p.Value.GetRawText(), g.GetRawText());
        }
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
