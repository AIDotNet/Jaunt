using Jaunt;
using Xunit;

namespace Jaunt.Tests;

/// <summary>
/// Behavior that the performance fast paths must keep exactly: lazily concatenated strings (ropes), Map/Set iterator
/// cursors across compaction, errors the interpreter raises without a .NET exception, unwinding across interpreter entries, and the RegExp test/replace fast
/// paths with their "unmodified regexp" guard. Expected values were produced by Node (V8) from the same script; the only
/// deliberate difference is the wording of "x is not a function", which is compared by kind only.
/// </summary>
public class FastPathSemanticsTests
{
    // Array builtins and sort; the expected values were produced by Node from the same script.
    private const string ArrayScript = """
const out = {};
const shape = a => { const r = []; for (let i = 0; i < a.length; i++) r.push(i in a ? a[i] : 'hole'); return r; };
const noisy = n => { const a = []; let x = 12345; for (let i = 0; i < n; i++) { x = (x * 1103515245 + 12345) & 0x7fffffff; a.push(x % 1000); } return a; };
const hash = a => { let h = 7; for (const v of a) h = (h * 31 + (typeof v === 'number' ? v : String(v).length)) % 1000003; return h; };
// fill
out.fillHoles = [shape(new Array(5).fill(7, 1, 3)), shape(new Array(3).fill(1)), new Array(2000).fill(0).length, shape([1, , 3].fill(9, 1))];
out.fillSetterOnProto = (() => { const log = []; Object.defineProperty(Array.prototype, '1', { set(v) { log.push(v); }, get() { return 'p'; }, configurable: true }); try { const a = new Array(3); a.fill(5); const r = [log.slice(), Object.keys(a)]; return r; } finally { delete Array.prototype[1]; } })();
out.fillShrinkInArg = (() => { const a = [1, 2, 3, 4, 5]; a.fill(0, { valueOf() { a.length = 1; return 0; } }); return [a.length, shape(a)]; })();
out.fillEndGrow = (() => { const a = new Array(4); a.fill('x', 0, { valueOf() { a.length = 2; return 4; } }); return shape(a); })();
out.fillNonExtensible = (() => { const a = new Array(2); Object.preventExtensions(a); try { a.fill(1); return 'no'; } catch (e) { return e.constructor.name; } })();
out.fillNonExtensibleDense = (() => { const a = [1, 2]; Object.preventExtensions(a); a.fill(3); return a; })();
out.fillFrozen = (() => { const a = Object.freeze([1, 2]); try { a.fill(1); return 'no'; } catch (e) { return [e.constructor.name, a]; } })();
out.fillLengthReadonly = (() => { const a = new Array(2); Object.defineProperty(a, 'length', { writable: false }); a.fill(1); return [a, a.length]; })();
out.fillHugeLength = (() => { const a = new Array(4294967295).fill(1, 0, 3); return [a.length, a[0], a[2], a[3], 3 in a]; })();
out.fillObjectValue = (() => { const o = {}, a = new Array(100).fill(o); return [a[0] === a[99], a[50] === o]; })();
out.fillArrayLike = Array.prototype.fill.call({ length: 2 }, 1);
out.declaredLength = (() => { const a = new Array(3000); a[2000] = 1; const b = new Array(3000); for (let i = 0; i < 3000; i++) b[i] = i; const c = new Array(2000); c[1999] = 1; c.length = 1; return [Object.keys(a), a.length, 1999 in a, 2001 in a, hash(b), shape(c)]; })();
out.declaredLengthProto = (() => { const log = []; Object.defineProperty(Array.prototype, '1500', { set(v) { log.push(v); }, configurable: true }); try { const a = new Array(3000); a[1500] = 3; return [log, 1500 in a, Object.keys(a).length]; } finally { delete Array.prototype[1500]; } })();
// sort: the comparators that need not be called
out.numCmpMixed = [[3, '10', 2, '1'].sort((a, b) => a - b), [3, '10', 2, '1'].sort((a, b) => b - a), [5n, 1n, 3n].sort((a, b) => Number(a - b)).map(String), (() => { try { [5n, 1n, 3n].sort((a, b) => a - b); return 'no'; } catch (e) { return e.constructor.name; } })()];
out.numCmpBigIntMixed = (() => { try { [1, 2n].sort((a, b) => a - b); return 'no'; } catch (e) { return e.constructor.name; } })();
out.numCmpValueOf = (() => { let calls = 0; const o = { valueOf() { calls++; return 2; } }; const a = [3, o, 1].sort((a, b) => a - b); return [a[0], a[1] === o, a[2], calls > 0]; })();
out.numCmpThrowingValueOf = (() => { const o = { valueOf() { throw new EvalError('v'); } }, a = [3, o, 1]; try { a.sort((a, b) => a - b); return 'no'; } catch (e) { return [e.constructor.name, a[0], a[2]]; } })();
out.numCmpSpecials = (() => { const f = x => Object.is(x, -0) ? '-0' : String(x); return [[Infinity, 1, -Infinity, Infinity, 0].sort((a, b) => a - b).map(f), [Infinity, 1, -Infinity].sort((a, b) => b - a).map(f), [0, -0, 0, -0].sort((a, b) => a - b).map(f), [-0, 0, -0].sort((a, b) => b - a).map(f), [1, undefined, 0, , undefined, -1].sort((a, b) => a - b).map(f)]; })();
out.numCmpNaNPermutation = (() => { const a = [3, NaN, 1, NaN, 2, 0, NaN, 5, 4]; a.sort((x, y) => x - y); return [a.length, a.filter(x => x !== x).length, a.filter(x => x === x).reduce((s, x) => s + x, 0)]; })();
out.numCmpForms = (() => { const src = [3, 1, 2]; return [src.slice().sort(function (a, b) { return a - b; }), src.slice().sort(function (a, b) { return b - a; }), src.slice().sort((a, b) => { return a - b; }), src.slice().sort(function (p, q) { 'use strict'; return q - p; }), src.slice().sort((a, b) => a - a), src.slice().sort((a, b) => b - b), src.slice().sort((a, b = 0) => a - b), src.slice().sort((a, ...b) => a - b[0]), src.slice().sort(function (a, b) { return arguments[1] - arguments[0]; }), src.slice().sort(function (a, a2, c) { return a - a2; }), src.toSorted((a, b) => b - a)]; })();
out.numCmpShadow = (() => { const a = [1, 2, 3], b = 100; return a.slice().sort((x, y) => y - b + x - x).length; })();
out.numCmpSubclass = (() => { class A extends Array {} const a = A.from([3, 1, 2]); a.sort((x, y) => x - y); return [a instanceof A, Array.from(a)]; })();
out.numCmpArrayLike = Array.prototype.sort.call({ length: 3, 0: 3, 1: 1, 2: 2 }, (a, b) => a - b);
out.numCmpProtoHole = (() => { Array.prototype[1] = 0; try { const a = [5, , 3].sort((x, y) => x - y); return [shape(a), Object.keys(a)]; } finally { delete Array.prototype[1]; } })();
// sort: run detection, insertion, merge
out.sortShapes = (() => { const r = {}; const n = 3000; const up = Array.from({ length: n }, (_, i) => i), down = up.slice().reverse(); const saw = up.map(i => i % 64), two = up.map(i => i < n / 2 ? i : i - n / 2), eq = up.map(() => 1), nearly = up.slice(); nearly[10] = 2990; nearly[2990] = 10; const lens = [0, 1, 2, 3, 15, 16, 31, 32, 33, 63, 64, 65, 127, 128, 129, 1000, 4097]; for (const [k, a] of Object.entries({ up, down, saw, two, eq, nearly, noisy: noisy(n) })) { const s = a.map((v, i) => ({ v, i })).sort((x, y) => x.v - y.v); let ok = true; for (let i = 1; i < s.length; i++) if (s[i - 1].v > s[i].v || (s[i - 1].v === s[i].v && s[i - 1].i > s[i].i)) ok = false; r[k] = [ok, hash(s.map(e => e.v))]; } r.lens = lens.map(l => { const a = noisy(l), asc = a.map((v, i) => ({ v, i })).sort((x, y) => x.v - y.v || x.i - y.i).map(e => e.v), plain = a.slice().sort((x, y) => x - y), desc = a.slice().sort((x, y) => y - x); return [hash(plain), hash(asc) === hash(plain), hash(desc), plain[0] ?? null]; }); return r; })();
out.sortDescendingStable = (() => { const a = []; for (let i = 0; i < 500; i++) a.push({ k: 500 - (i >> 2), i }); a.sort((p, q) => p.k - q.k); let ok = true; for (let i = 1; i < a.length; i++) if (a[i - 1].k > a[i].k || (a[i - 1].k === a[i].k && a[i - 1].i > a[i].i)) ok = false; return ok; })();
out.sortInconsistentLarge = (() => { const r = []; for (const c of [() => 1, () => -1, () => 0, () => NaN, (a, b) => (a % 7) - (b % 3), (a, b) => a < b, () => Infinity, () => undefined, () => '-1']) { const a = noisy(2000), before = a.slice().sort((x, y) => x - y); a.sort(c); r.push(a.length === 2000 && a.slice().sort((x, y) => x - y).every((v, i) => v === before[i])); } return r; })();
out.sortMutatingLarge = (() => { const a = noisy(500); let n = 0; a.sort((p, q) => { if (n++ === 100) { a.length = 3; a[1000] = 'x'; } return p - q; }); let ok = true; for (let i = 1; i < a.length; i++) if (a[i - 1] > a[i]) ok = false; return [a.length, ok]; })();
out.sortThrowsLate = (() => { const a = noisy(500), b = a.slice(); let n = 0; try { a.sort((p, q) => { if (++n === 1000) throw new RangeError('late'); return p - q; }); return 'no'; } catch (e) { return [e.constructor.name, a.every((v, i) => v === b[i])]; } })();
out.sortReentrantLarge = (() => { const a = noisy(300), b = noisy(300); a.sort((p, q) => { b.slice(0, 40).sort((x, y) => y - x); return p - q; }); return hash(a); })();
out.sortFrozen = (() => { const r = []; for (const a of [Object.freeze([2, 1]), Object.freeze([]), Object.seal([2, , 1]), Object.preventExtensions([2, , 1])]) { try { a.sort(); r.push(['ok', a.length, Object.keys(a)]); } catch (e) { r.push([e.constructor.name, Object.keys(a)]); } } return r; })();
// default sort: keys computed once
out.defaultMixed = (() => { const a = [10, 9, 1, 'b', 'a', undefined, null, true, false, 1n, -1, '1', NaN, -0, 1e21, 0.5, Infinity, '', undefined, , 'B']; a.sort(); return a.map(x => typeof x === 'bigint' ? x + 'n' : Object.is(x, -0) ? '-0' : x === undefined ? 'undef' : x !== x ? 'nan' : x === null ? 'null' : typeof x === 'string' ? 's:' + x : x); })();
out.defaultStable = (() => { const a = [1, '1', 1n, '1', 1, 'a', null, 'null', true, 'true']; return a.sort().map(x => typeof x); })();
out.defaultHoles = (() => { const a = [3, , 1, undefined, , 2]; a.sort(); return [shape(a), Object.keys(a)]; })();
out.defaultSymbol = (() => { try { [1, Symbol('x'), 2].sort(); return 'no'; } catch (e) { return e.constructor.name; } })();
out.defaultSingleSymbol = (() => { const s = Symbol('x'), a = [s, undefined].sort(); return [a[0] === s, a[1]]; })();
out.defaultObjects = (() => { let calls = 0; const o = { toString() { calls++; return '5'; } }; const a = [10, o, 1, 'z'].sort(); return [a[0], a[1], a[2] === o, a[3], calls > 0]; })();
out.defaultObjectMutates = (() => { const a = [3, 1, { toString() { a.length = 0; return '2'; } }]; a.sort(); return [a.length, a[0], a[2]]; })();
out.defaultStrings = (() => { let r = ''; for (let i = 0; i < 300; i++) r += 'x'; const a = [r + 'b', r + 'a', '😀', '￿', 'é', 'e', 'E', 'z', 'a\0', 'a']; return a.sort().map(s => s.length + ':' + s.charCodeAt(s.length - 1)); })();
out.defaultLarge = (() => { const a = noisy(5000); a.sort(); let ok = true; for (let i = 1; i < a.length; i++) if (String(a[i - 1]) > String(a[i])) ok = false; const s = noisy(2000).map(v => 'k' + v).sort(); return [ok, hash(a), s[0], s[1999], [10, 9, 1].toSorted(), [1, 2, 10, 20].sort()]; })();
out.defaultBudgetNotUserVisible = [undefined, undefined].sort();
// slice, splice, concat, Array.from: one copy instead of element by element
out.sliceHoles = [shape([1, , 3, , 5].slice(1, 4)), shape(new Array(3).slice()), [1, 2, 3].slice(5), [1, 2, 3].slice(2, 1), shape([, 1].slice(0, 1))];
out.sliceProto = (() => { Array.prototype[1] = 'p'; try { return [shape([0, , 2].slice()), Object.keys([0, , 2].slice())]; } finally { delete Array.prototype[1]; } })();
out.sliceShrinkInCtor = (() => { const a = [1, 2, 3, 4]; let n = 0; Object.defineProperty(a, 'constructor', { get() { a.length = 1; return undefined; } }); const r = a.slice(); return [r.length, shape(r), a.length]; })();
out.sliceSpecies = (() => { class M extends Array { static get [Symbol.species]() { return M; } } const r = M.from([1, 2, 3]).slice(1); const log = []; class P extends Array { static get [Symbol.species]() { return function (n) { const x = new Proxy([], { defineProperty(t, k, d) { log.push(k); return Reflect.defineProperty(t, k, d); } }); return x; }; } } const q = new P(1, 2, 3).slice(1); return [r instanceof M, r.length, log, q.length]; })();
out.sliceSameObjectSpecies = (() => { const a = [1, 2, 3, 4]; a.constructor = { [Symbol.species]: function () { return a; } }; const r = a.slice(1, 3); return [r === a, Array.from(r)]; })();
out.sliceLarge = (() => { const a = Array.from({ length: 70000 }, (_, i) => i), s = a.slice(5), t = a.slice(); t[0] = -1; return [s.length, s[0], s[69994], a[0], t[0], hash(t.slice(69000))]; })();
out.spliceBasics = (() => { const a = [1, , 3, 4, 5], r = a.splice(0, 3, 'x'); const b = [1, 2, 3], r2 = b.splice(1, 0); const c = [1, 2, 3], r3 = c.splice(1); const d = [1, 2, 3], r4 = d.splice(); return [shape(r), shape(a), r2, b, r3, c, r4, d]; })();
out.spliceShrinkInCtor = (() => { const a = [1, 2, 3, 4, 5, 6]; Object.defineProperty(a, 'constructor', { get() { a.length = 2; return undefined; } }); const r = a.splice(1, 3, 'x'); return [shape(r), shape(a), a.length]; })();
out.concatHoles = [shape([1, , 2].concat([, 3], 4, [[5]], [,])), [].concat(), [].concat([], []), [1].concat('ab', null, undefined).length, shape(new Array(2).concat(new Array(1)))];
out.concatSpreadable = (() => { const a = [1, 2]; a[Symbol.isConcatSpreadable] = false; const o = { length: 2, 0: 'x', [Symbol.isConcatSpreadable]: true }; return [[0].concat(a).length, [0].concat(o), [0].concat(a, o, [9]).length]; })();
out.concatMutatingGetter = (() => { const b = [1, 2, 3]; const o = { get [Symbol.isConcatSpreadable]() { b.length = 1; return false; } }; const r = [0].concat(o, b); return [r.length, r[2]]; })();
out.concatSelf = (() => { const a = [1, 2]; return a.concat(a, a); })();
out.concatProto = (() => { Array.prototype[1] = 'p'; try { return shape([0, , 2].concat([3, , 5])); } finally { delete Array.prototype[1]; } })();
out.concatSpecies = (() => { class M extends Array {} const r = new M(1, 2, 3).concat([4]); const t = [1]; t.constructor = { [Symbol.species]: function () { return t; } }; const s = t.concat([2]); return [r instanceof M, Array.from(r), s === t, s.length]; })();
out.fromArrayLike = (() => { const log = []; const o = { get length() { log.push('len'); return 3; }, get 0() { log.push('0'); return 'a'; }, 2: 'c' }; const r = Array.from(o, (v, i) => { log.push('m' + i); return v; }); return [r, log, 1 in r]; })();
out.fromMutatedInMap = (() => { const src = [1, 2, 3]; const r = Array.from(src, (v, i) => { if (i === 0) src.push(9); return v * 2; }); const s2 = [1, 2, 3]; const r2 = Array.from(s2, (v, i) => { if (i === 0) s2.length = 1; return v; }); return [r, r2]; })();
out.fromTooLong = (() => { try { Array.from({ length: 2 ** 32 }); return 'no'; } catch (e) { return e.constructor.name; } })();
out.fromThis = (() => { const seen = []; Array.from([1], function (v) { seen.push(this.x); return v; }, { x: 7 }); Array.from({ length: 1 }, function () { seen.push(typeof this); }, 3); return seen; })();
out.fromIterThrow = (() => { let closed = 0; const it = { [Symbol.iterator]() { return { next() { return { value: 1, done: false }; }, return() { closed++; return {}; } }; } }; try { Array.from(it, () => { throw new RangeError('x'); }); } catch (e) { return [e.constructor.name, closed]; } })();
// long arrays used from the front (queue mode): every operation must see the same array
const snapshot = a => { let h = a.length; for (let i = 0; i < a.length; i++) h = (h * 33 + (i in a ? (typeof a[i] === 'number' ? a[i] + 1 : String(a[i]).length) : 7)) % 1000000007; return h; };
const mix = (seed, steps, every, ops) => { let x = seed; const r = n => { x = (x * 1103515245 + 12345) & 0x7fffffff; return (x >>> 8) % n; }; const a = []; for (let i = 0; i < 200; i++) a.push(i); const log = []; for (let s = 0; s < steps; s++) { const op = ops[r(ops.length)]; log.push(op(a, r)); if (s % every === 0) log.push(snapshot(a)); } log.push(snapshot(a)); return hash(log.map(v => v === undefined ? -1 : typeof v === 'number' ? v : Array.isArray(v) ? v.length : 1)); };
const front = [a => a.shift(), a => a.shift(), a => a.shift(), (a, r) => a.push(r(1000)), (a, r) => a.push(r(1000), r(1000)), a => a.pop(), (a, r) => a.unshift(r(1000)), (a, r) => a.unshift(r(1000), r(1000), r(1000)), (a, r) => a.splice(r(20), r(4)), (a, r) => a.splice(r(20), 1, r(9), r(9)), (a, r) => a.splice(r(a.length + 1), r(4), r(9)), (a, r) => a.splice(-r(20), r(3)), (a, r) => a.splice(r(30), 0, 1, 2, 3, 4, 5, 6, 7)];
const touching = [(a, r) => a[r(a.length + 1)], (a, r) => { a[r(a.length + 1)] = r(100); }, (a, r) => { delete a[r(a.length + 1)]; }, (a, r) => r(a.length + 1) in a, a => a.length, a => a[0], a => a[a.length - 1], (a, r) => a.indexOf(r(1000)), (a, r) => a.lastIndexOf(r(1000)), (a, r) => a.includes(r(1000)), (a, r) => a.slice(r(50), r(100)), a => a.concat([1], a).length, a => [...a].length, a => Object.keys(a).length, a => a.join().length, a => JSON.stringify(a).length, a => { let n = 0; for (const v of a) n += v === undefined ? 1 : 0; return n; }, a => { let n = 0; for (const k in a) n++; return n; }, a => a.map(v => v).length, a => a.filter(v => v).length, a => a.reduce((s, v) => s + 1, 0), a => { a.reverse(); }, a => { a.sort((p, q) => p - q); }, (a, r) => { a.fill(r(9), r(10), r(20)); }, (a, r) => { a.copyWithin(r(10), r(20), r(30)); }, a => Array.from(a).length, a => a.toSorted((p, q) => q - p).length, a => a.at(-1), (a, r) => { if (a.length > 5) a.length -= r(5); }, (a, r) => { a.length += r(3); }, (a, r) => { a[a.length + r(3)] = 1; }, a => Object.getOwnPropertyDescriptor(a, '3')?.value, a => a.hasOwnProperty(2), a => Object.entries(a).length, a => Math.max.apply(null, a) > 0, a => Reflect.ownKeys(a).length];
out.queueFrontOnly = [1, 2, 3].map(seed => mix(seed, 5000, 500, front));
out.queueMix = [11, 12, 13, 14].map(seed => mix(seed, 4000, 7, front.concat(touching)));
out.queueShrinkAndRefill = (() => { const a = []; const log = []; for (let round = 0; round < 3; round++) { for (let i = 0; i < 100; i++) a.push(i + round); while (a.length > 5) log.push(a.shift()); log.push(snapshot(a)); for (let i = 0; i < 100; i++) a.unshift(i); while (a.length > 10) a.pop(); log.push(snapshot(a)); } return hash(log); })();
out.queueHoles = (() => { const a = new Array(100); a[1] = 'x'; a[99] = 'y'; const f = [a.shift(), a.shift(), a.shift(), a.pop(), a.pop(), a.length, 0 in a]; Array.prototype[2] = 'proto'; try { f.push(a[2], a.shift(), a.shift(), a.shift(), a.length, 0 in a); } finally { delete Array.prototype[2]; } return f; })();
out.queueProtoAfterShift = (() => { const a = new Array(100).fill(1); delete a[3]; delete a[50]; a.shift(); Array.prototype[2] = 'p'; try { const r = [a.indexOf('p'), a[2], a.slice(0, 4), a.shift(), a.shift(), a.shift(), a.length, a[46]]; return r; } finally { delete Array.prototype[2]; } })();
out.queueFreeze = (() => { const a = Array.from({ length: 100 }, (_, i) => i); a.shift(); a.shift(); Object.freeze(a); const r = [Object.isFrozen(a), a[0], a.length]; for (const f of [() => a.shift(), () => a.push(1), () => a.unshift(1), () => a.pop(), () => a.splice(0, 1), () => { 'use strict'; a[0] = 1; }]) { try { f(); r.push('ok'); } catch (e) { r.push(e.constructor.name); } } r.push(a[0], a.length); return r; })();
out.queueSeal = (() => { const a = Array.from({ length: 100 }, (_, i) => i); a.shift(); Object.seal(a); const r = [a[0], a.length]; for (const f of [() => a.shift(), () => a.push(1), () => a.pop()]) { try { f(); r.push('ok'); } catch (e) { r.push(e.constructor.name); } } r.push(a[0], a.length); return r; })();
out.queuePreventExtensions = (() => { const a = Array.from({ length: 100 }, (_, i) => i); a.shift(); Object.preventExtensions(a); const r = []; for (const f of [() => a.push(1), () => a.unshift(1), () => a.shift(), () => a.pop(), () => { a[0] = 'w'; return a[0]; }, () => a.length]) { try { r.push(f()); } catch (e) { r.push(e.constructor.name); } } return r; })();
out.queueLengthReadonly = (() => { const a = Array.from({ length: 100 }, (_, i) => i); a.shift(); Object.defineProperty(a, 'length', { writable: false }); const r = [a.length]; for (const f of [() => a.shift(), () => a.push(1), () => a.pop(), () => a.unshift(0), () => a.splice(0, 1)]) { try { r.push(f()); } catch (e) { r.push(e.constructor.name); } } r.push(a.length, a[0], a[98]); return r; })();
out.queueAccessorDefined = (() => { const a = Array.from({ length: 100 }, (_, i) => i); a.shift(); let got = 0; Object.defineProperty(a, 5, { get() { got++; return 'g'; }, configurable: true }); const r = [a[5], a[4], a[6], a.length, got]; try { r.push(a.shift()); } catch (e) { r.push(e.constructor.name); } r.push(a[4], a.length, got); return r; })();
out.queueCallbackShifts = (() => { const a = Array.from({ length: 100 }, (_, i) => i); const seen = []; a.forEach((v, i) => { if (i % 10 === 0) seen.push(v, a.shift()); }); const b = Array.from({ length: 100 }, (_, i) => i); const m = b.map(v => { b.shift(); return v; }); return [seen, a.length, m.length, m[0], m[1], 60 in m, b.length]; })();
out.queueIterator = (() => { const a = Array.from({ length: 100 }, (_, i) => i); const it = a[Symbol.iterator](); const r = [it.next().value]; a.shift(); r.push(it.next().value); a.unshift('u'); r.push(it.next().value); a.shift(); a.shift(); r.push(it.next().value, [...it].length); return r; })();
out.queueNamedProps = (() => { const a = Array.from({ length: 100 }, (_, i) => i); a.x = 1; a.shift(); a.y = 2; return [a.x, a.y, Object.keys(a).slice(-2), a.length, a[0], 'x' in a, a.shift(), delete a.x, 'x' in a]; })();
out.queueSpliceSpecies = (() => { class M extends Array {} const a = M.from({ length: 100 }, (_, i) => i); a.shift(); const r = a.splice(0, 2); const b = a.slice(0, 2); return [r instanceof M, r.length, r[0], b instanceof M, b[0], a.length, a[0]]; })();
out.queueGrow = (() => { const a = Array.from({ length: 64 }, (_, i) => i); let t = 0; for (let i = 0; i < 5000; i++) { a.push(i); a.push(i + 1); t += a.shift(); } return [a.length, t, a[0], a[a.length - 1], snapshot(a)]; })();
// ToInt32 of doubles outside the int range
out.toInt32Edges = (() => { const v = [2147483648, 2147483647.5, -2147483648.5, -2147483649, 4294967295, 4294967296, 4294967297, -4294967297, 1e10, -1e10, 1.5, -1.5, 0.9, -0.9, 2 ** 52, 2 ** 52 + 2, 2 ** 53, 2 ** 53 + 2, 2 ** 62 + 2 ** 40, 2 ** 83, 2 ** 84, 2 ** 84 + 2 ** 33, 3 * 2 ** 82, -(2 ** 83), 2 ** 1023, 1.7976931348623157e308, 5e-324, -5e-324, 1e21, 1e300, -1e300, 0, -0, NaN, Infinity, -Infinity, 123456789012345680000, 9007199254740991, -9007199254740991, 1103515245 * 1103515245]; return v.map(x => [x | 0, x >>> 0, ~x, x << 1, x >> 1]); })();
JSON.stringify(out)
""";

    private const string ArrayExpected = """
{"fillHoles":[["hole",7,7,"hole","hole"],[1,1,1],2000,[1,9,9]],"fillSetterOnProto":[[5],["0","2"]],"fillShrinkInArg":[5,[0,0,0,0,0]],"fillEndGrow":["x","x","x","x"],"fillNonExtensible":"TypeError","fillNonExtensibleDense":[3,3],"fillFrozen":["TypeError",[1,2]],"fillLengthReadonly":[[1,1],2],"fillHugeLength":[4294967295,1,1,null,false],"fillObjectValue":[true,true],"fillArrayLike":{"0":1,"1":1,"length":2},"declaredLength":[["2000"],3000,false,false,809166,["hole"]],"declaredLengthProto":[[3],true,0],"numCmpMixed":[["1",2,3,"10"],["10",3,2,"1"],["1","3","5"],"TypeError"],"numCmpBigIntMixed":"TypeError","numCmpValueOf":[1,true,3,true],"numCmpThrowingValueOf":["EvalError",3,1],"numCmpSpecials":[["-Infinity","0","1","Infinity","Infinity"],["Infinity","1","-Infinity"],["0","-0","0","-0"],["-0","0","-0"],["-1","0","1","undefined","undefined",null]],"numCmpNaNPermutation":[9,3,15],"numCmpForms":[[1,2,3],[3,2,1],[1,2,3],[3,2,1],[3,1,2],[3,1,2],[1,2,3],[1,2,3],[3,2,1],[1,2,3],[3,2,1]],"numCmpShadow":3,"numCmpSubclass":[true,[1,2,3]],"numCmpArrayLike":{"0":1,"1":2,"2":3,"length":3},"numCmpProtoHole":[[0,3,5],["0","1","2"]],"sortShapes":{"up":[true,809166],"down":[true,809166],"saw":[true,884250],"two":[true,1633],"eq":[true,210386],"nearly":[true,809166],"noisy":[true,901975],"lens":[[7,true,7,null],[823,true,823,606],[26321,true,32381,606],[816887,true,133684,606],[670689,true,801180,0],[890100,true,952076,0],[177361,true,438682,0],[946387,true,343909,0],[763725,true,625049,0],[266579,true,266724,0],[86206,true,696747,0],[192564,true,845358,0],[434038,true,871766,0],[187117,true,443515,0],[190556,true,875990,0],[76719,true,47395,0],[30276,true,933330,0]]},"sortDescendingStable":true,"sortInconsistentLarge":[true,true,true,true,true,true,true,true,true],"sortMutatingLarge":[1001,true],"sortThrowsLate":["RangeError",true],"sortReentrantLarge":929417,"sortFrozen":[["TypeError",["0","1"]],["ok",0,[]],["TypeError",["0","2"]],["TypeError",["0","2"]]],"defaultMixed":["s:",-1,"-0",0.5,1,"1n","s:1",10,1e+21,9,"s:B",null,"nan","s:a","s:b",false,"null",true,"undef","undef",null],"defaultStable":["number","string","bigint","string","number","string","object","string","boolean","string"],"defaultHoles":[[1,2,3,null,"hole","hole"],["0","1","2","3"]],"defaultSymbol":"TypeError","defaultSingleSymbol":[true,null],"defaultObjects":[1,10,true,"z",true],"defaultObjectMutates":[3,1,3],"defaultStrings":["1:69","1:97","2:0","1:101","301:97","301:98","1:122","1:233","2:56832","1:65535"],"defaultLarge":[true,779376,"k0","k992",[1,10,9],[1,10,2,20]],"defaultBudgetNotUserVisible":[null,null],"sliceHoles":[["hole",3,"hole"],["hole","hole","hole"],[],[],["hole"]],"sliceProto":[[0,"p",2],["0","1","2"]],"sliceShrinkInCtor":[4,[1,"hole","hole","hole"],1],"sliceSpecies":[true,2,["0","1","length"],2],"sliceSameObjectSpecies":[true,[2,3]],"sliceLarge":[69995,5,69999,0,-1,637936],"spliceBasics":[[1,"hole",3],["x",4,5],[],[1,2,3],[2,3],[1],[],[1,2,3]],"spliceShrinkInCtor":[[2,"hole","hole"],[1,"x","hole","hole"],4],"concatHoles":[[1,"hole",2,"hole",3,4,[5],"hole"],[],[],4,["hole","hole","hole"]],"concatSpreadable":[2,[0,"x",null],5],"concatMutatingGetter":[3,1],"concatSelf":[1,2,1,2,1,2],"concatProto":[0,"p",2,3,"p",5],"concatSpecies":[true,[1,2,3,4],true,2],"fromArrayLike":[["a",null,"c"],["len","0","m0","m1","m2"],true],"fromMutatedInMap":[[2,4,6,18],[1]],"fromTooLong":"RangeError","fromThis":[7,"object"],"fromIterThrow":["RangeError",1],"queueFrontOnly":[923970,285540,288357],"queueMix":[755489,412550,587631,182592],"queueShrinkAndRefill":917665,"queueHoles":[null,"x",null,"y",null,95,false,"proto",null,null,"proto",92,true],"queueProtoAfterShift":[2,"p",[1,1,"p",1],1,1,"p",96,null],"queueFreeze":[true,2,98,"TypeError","TypeError","TypeError","TypeError","TypeError","TypeError",2,98],"queueSeal":[1,99,"TypeError","TypeError","TypeError",2,99],"queuePreventExtensions":["TypeError","TypeError",1,99,"w",97],"queueLengthReadonly":[99,"TypeError","TypeError","TypeError","TypeError","TypeError",99,2,null],"queueAccessorDefined":["g",5,7,99,1,"TypeError","g",99,2],"queueCallbackShifts":[[0,0,11,1,22,2,33,3,44,4,55,5,66,6,77,7,88,8,99,9],90,100,0,2,false,50],"queueIterator":[0,2,2,5,94],"queueNamedProps":[1,2,["x","y"],99,1,true,1,true,false],"queueSpliceSpecies":[true,2,1,true,3,97,3],"queueGrow":[5064,6093040,2468,5000,218651513],"toInt32Edges":[[-2147483648,2147483648,2147483647,0,-1073741824],[2147483647,2147483647,-2147483648,-2,1073741823],[-2147483648,2147483648,2147483647,0,-1073741824],[2147483647,2147483647,-2147483648,-2,1073741823],[-1,4294967295,0,-2,-1],[0,0,-1,0,0],[1,1,-2,2,0],[-1,4294967295,0,-2,-1],[1410065408,1410065408,-1410065409,-1474836480,705032704],[-1410065408,2884901888,1410065407,1474836480,-705032704],[1,1,-2,2,0],[-1,4294967295,0,-2,-1],[0,0,-1,0,0],[0,0,-1,0,0],[0,0,-1,0,0],[2,2,-3,4,1],[0,0,-1,0,0],[2,2,-3,4,1],[0,0,-1,0,0],[0,0,-1,0,0],[0,0,-1,0,0],[0,0,-1,0,0],[0,0,-1,0,0],[0,0,-1,0,0],[0,0,-1,0,0],[0,0,-1,0,0],[0,0,-1,0,0],[0,0,-1,0,0],[-559939584,3735027712,559939583,-1119879168,-279969792],[0,0,-1,0,0],[0,0,-1,0,0],[0,0,-1,0,0],[0,0,-1,0,0],[0,0,-1,0,0],[0,0,-1,0,0],[0,0,-1,0,0],[792100864,792100864,-792100865,1584201728,396050432],[-1,4294967295,0,-2,-1],[1,1,-2,2,0],[-1029531136,3265436160,1029531135,-2059062272,-514765568]]}
""";

    [Fact]
    public void ArrayFastPathsMatchReferenceBehavior()
    {
        using var engine = new JsEngine();
        var exp = System.Text.Json.JsonDocument.Parse(ArrayExpected).RootElement;
        var got = System.Text.Json.JsonDocument.Parse(engine.Evaluate(ArrayScript).AsString()).RootElement;
        foreach (var p in exp.EnumerateObject())
        {
            Assert.True(got.TryGetProperty(p.Name, out var g), $"missing case {p.Name}");
            Assert.True(p.Value.GetRawText() == g.GetRawText(), $"{p.Name}: expected {p.Value.GetRawText()} but got {g.GetRawText()}");
        }
        Assert.Equal(exp.EnumerateObject().Count(), got.EnumerateObject().Count());
    }



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
// unwinding across interpreter entries (native callbacks, accessors, constructors, tail calls)
out.unwindThroughNative = (() => { const log = []; try { [1, 2].forEach(x => { [3].forEach(y => { try { throw new Error('a' + x + y); } catch (e) { log.push(e.message); throw new RangeError('b' + x); } finally { log.push('f' + x); } }); }); } catch (e) { log.push(e.constructor.name + e.message); } return log; })();
out.unwindCatchesInCaller = (() => { const log = []; for (let i = 0; i < 3; i++) { try { [i].map(v => { if (v !== 1) throw v; return v; }); log.push('ok' + i); } catch (e) { log.push('c' + e); } } return log; })();
out.unwindRethrownByNestedLoops = (() => { const f = n => { if (n === 0) throw 'deep'; return [n].map(m => { try { return f(m - 1); } finally { log.push(m); } })[0]; }; const log = []; try { f(4); } catch (e) { log.push(e); } return log; })();
out.unwindBuiltinRaises = (() => { const r = []; try { [1].forEach(() => null.x); } catch (e) { r.push(e.constructor.name); } try { [1].forEach(() => { (void 0)(); }); } catch (e) { r.push(e.constructor.name); } try { const o = {}; o.m(); } catch (e) { r.push(e.constructor.name); } try { new (() => {})(); } catch (e) { r.push(e.constructor.name); } try { new 1(); } catch (e) { r.push(e.constructor.name); } try { Symbol() + ''; } catch (e) { r.push(e.constructor.name); } try { [].reduce((a, b) => a); } catch (e) { r.push(e.constructor.name); } return r; })();
out.unwindAccessors = (() => { const o = { get g() { throw new EvalError('g'); }, set s(v) { throw new URIError('s' + v); } }; const r = []; try { o.g; } catch (e) { r.push(e.message); } try { o.s = 1; } catch (e) { r.push(e.message); } try { Object.keys(new Proxy({}, { ownKeys() { throw 'trap'; } })); } catch (e) { r.push(e); } return r; })();
out.unwindConstruct = (() => { class B { constructor(v) { if (v) return v; this.b = 1; } } class D extends B { constructor(v, skip) { if (!skip) super(v); else return v; } } const r = []; const t = f => { try { const x = f(); r.push(typeof x === 'object' ? JSON.stringify(x) : x); } catch (e) { r.push(e.constructor.name); } }; t(() => new B()); t(() => new B({ o: 1 })); t(() => new B(5)); t(() => new D()); t(() => new D({ p: 1 })); t(() => new D(1, true)); t(() => new D(undefined, true)); t(() => new D({ q: 1 }, true)); t(() => Reflect.construct(D, [1, true])); t(() => Reflect.construct(D, [undefined, true])); t(() => Reflect.construct(D, [{ q: 2 }, true])); t(() => [1].map(() => new D(undefined, true))); try { try { new D(1, true); } finally { r.push('fin'); } } catch (e) { r.push(e.constructor.name); } return r; })();
out.unwindGenerators = (() => { function* g() { try { yield 1; yield 2; } catch (e) { yield 'c' + e; } finally { yield 'f'; } } const a = g(); const r = [a.next().value, a.throw('x').value, a.next().value, a.next().done]; function* h() { yield 1; throw 'gen'; } try { for (const v of h()) r.push(v); } catch (e) { r.push(e); } const it = g(); it.next(); try { [1].forEach(() => it.throw('y')); r.push(it.next().value); } catch (e) { r.push('no'); } return r; })();
out.unwindTailCalls = (() => { 'use strict'; const o = { m(n) { return n === 0 ? thrower() : this.m(n - 1); } }; function thrower() { throw 'tail'; } const countdown = n => { return n === 0 ? 'done' : countdown(n - 1); }; const r = [countdown(1000)]; try { o.m(50); } catch (e) { r.push(e); } const notFn = {}; try { (() => { return notFn.x(); })(); } catch (e) { r.push(e.constructor.name); } try { (() => { return notFn(); })(); } catch (e) { r.push(e.constructor.name); } const viaMap = [3].map(n => { return countdown(n); }); r.push(viaMap[0]); return r; })();
out.unwindRedirected = (() => { const r = []; function f(a, b) { 'use strict'; if (a === 'throw') throw 'in f'; return [this, a, b]; } r.push(f.call(1, 2, 3), f.apply(4, [5, 6]), f.bind(7, 8)(9), f.call()); const t = g => { try { g(); } catch (e) { r.push(e); } }; t(() => f.call(0, 'throw')); t(() => f.apply(0, ['throw'])); t(() => f.bind(0, 'throw')()); return r; })();
out.unwindFinallyReturn = (() => { const r = []; r.push([1].map(() => { try { throw 1; } catch (e) { return "c"; } finally { r.push("f"); } })[0]); const z = (() => { try { [1].forEach(() => { throw 'lost'; }); } finally { return 'finally wins'; } })(); r.push(z); return r; })();
// strict equality and numeric boundaries in the interpreter's inline paths
out.strictEqAcrossTypes = (() => { const s = Symbol(), o = {}, big = 2n ** 70n; const vals = [0, -0, NaN, 1, '1', 'ab', 'a' + String.fromCharCode(98), true, false, null, undefined, s, o, big, 2n ** 70n, 1n, Infinity, -Infinity]; const m = []; for (const a of vals) { let row = ''; for (const b of vals) { let viaJump; if (a === b) viaJump = 1; else viaJump = 0; const viaValue = a === b; const ne = a !== b; if (viaValue !== (viaJump === 1) || ne === viaValue) return 'inconsistent'; row += viaJump; } m.push(row); } return m; })();
out.strictEqRopes = (() => { let a = 'q'.repeat(300), b = 'q'.repeat(300); a += 'x'; b += 'x'; const c = a; let r; switch (a) { case b: r = 'b'; break; default: r = 'no'; } return [a === b, a === c, a !== b, r, a === a + '', (a + 'y') === (b + 'y')]; })();
out.numberEncodingBoundaries = (() => { const f = new Float64Array(1), u = new Uint32Array(f.buffer); const r = []; for (const [hi, lo] of [[0x7FFFFFFF, 0xFFFFFFF0], [0x7FFFFFFF, 0xFFFFFFFF], [0x7FF80000, 0x10], [0xFFFFFFFF, 0xFFFFFFFF], [0x7FF00000, 0], [0x7FEFFFFF, 0xFFFFFFFF], [0x80000000, 0]]) { u[0] = lo; u[1] = hi; const v = f[0]; r.push([typeof v, Number.isNaN(v), v === v, String(v), Object.is(v, -0), [v].includes(v), new Map([[v, 1]]).has(v)]); } const dv = new DataView(new ArrayBuffer(8)); dv.setUint32(0, 0x7FFFFFFF); dv.setUint32(4, 0xFFFFFFF0); const w = dv.getFloat64(0); r.push([typeof w, w !== w, JSON.stringify([w]), w ? 1 : 0, (w + 1) !== (w + 1)]); const g = new Float32Array(new Uint32Array([0x7FFFFFFF]).buffer)[0]; r.push([typeof g, g !== g]); return r; })();
out.compareBranches = (() => { const r = []; for (const [a, b] of [[1, 2], [2, 1], [1, 1], [NaN, 1], [1, NaN], ['a', 'b'], ['b', 'a'], ['2', 10], [1n, 2], [null, 0], [undefined, 0], [{ valueOf() { return 3; } }, 2]]) { let s = ''; if (a < b) s += 'L'; if (a <= b) s += 'l'; if (a > b) s += 'G'; if (a >= b) s += 'g'; s += [a < b, a <= b, a > b, a >= b].map(Number).join(''); r.push(s); } return r; })();
out.bitOpBoundaries = (() => { const v = [0, 1, -1, 2147483647, 2147483648, -2147483648, 4294967295, 4294967296, 1.9, -1.9, NaN, Infinity, 1e21]; const r = []; for (const a of v) r.push([a & 255, a | 0, a ^ 1, a << 1, a >> 1, a >>> 1, 1 << a, 1 >> a, 1 >>> a, a % 7, a * 2, a / 2, a - 1].join()); let b = 5n; r.push(String(b & 3n), String(b | 2n), String(b ^ 1n), String(b << 2n), String(b >> 1n), String(b % 3n), String(b * b), String(b / 2n), String(b - 7n)); try { 1n >>> 1n; } catch (e) { r.push(e.constructor.name); } try { 1n & 1; } catch (e) { r.push(e.constructor.name); } r.push({ valueOf() { return 6; } } & 3, '7' | 8, null ^ 1, undefined >>> 0); return r; })();
// accessors entered from the interpreter loop
out.getterFrames = (() => { const log = []; class A { #v = 3; get v() { return this.#v; } get self() { return this; } get boom() { throw new RangeError('boom'); } get viaMap() { return [1, 2].map(x => x * this.#v); } static get s() { return this === A; } get gen() { return (function* () { yield 1; })().next().value; } get rec() { return this.depth-- > 0 ? this.rec + 1 : 0; } } const a = new A(); a.depth = 50; let t = 0; for (let i = 0; i < 3; i++) t += a.v; log.push(t, a.self === a, a.viaMap, A.s, a.gen, a.rec); for (let i = 0; i < 3; i++) { try { const x = a.boom; log.push('no' + x); } catch (e) { log.push(e.message + i); } finally { log.push('f'); } } return log; })();
out.getterReceivers = (() => { const p = { get strict() { 'use strict'; return typeof this; }, get sloppy() { return typeof this; } }; const o = Object.create(p); const r = [o.strict, o.sloppy]; Object.defineProperty(Number.prototype, 'nStrict', { get() { 'use strict'; return typeof this; }, configurable: true }); Object.defineProperty(Number.prototype, 'nSloppy', { get() { return typeof this; }, configurable: true }); r.push((1).nStrict, (1).nSloppy); delete Number.prototype.nStrict; delete Number.prototype.nSloppy; return r; })();
out.getterRedefined = (() => { const o = { get g() { return 1; } }; const read = x => x.g; const r = [read(o), read(o)]; Object.defineProperty(o, 'g', { get() { return 2; }, configurable: true }); r.push(read(o)); Object.defineProperty(o, 'g', { get: undefined, configurable: true }); r.push(read(o)); Object.defineProperty(o, 'g', { get: async function () { return 3; }, configurable: true }); r.push(read(o) instanceof Promise); Object.defineProperty(o, 'g', { get: function* () { yield 4; }, configurable: true }); r.push(read(o).next().value); Object.defineProperty(o, 'g', { get: Math.random.bind ? (() => 5).bind(null) : null, configurable: true }); r.push(read(o)); const p = new Proxy(function () { return 6; }, {}); Object.defineProperty(o, 'g', { get: p, configurable: true }); r.push(read(o)); class K { } Object.defineProperty(o, 'g', { get: K, configurable: true }); try { read(o); } catch (e) { r.push(e.constructor.name); } return r; })();
out.getterInExpression = (() => { const o = { get a() { return 2; }, get b() { return this.a * 3; } }; let x = o.a; x = o.b + o.a; const arr = [o.a, o.b]; const nested = { get n() { return { get m() { return x; } }; } }; return [x, arr, nested.n.m, `${o.a}${o.b}`, o.a ? o.b : 0]; })();
out.truthiness = (() => { const vals = [0, -0, NaN, 1, -1, Infinity, '', 'x', true, false, null, undefined, 0n, 1n, {}, [], Symbol(), () => 0]; const r = []; for (const v of vals) { let a = 0; if (v) a = 1; let b = 0; if (!v) b = 1; const c = v ? 1 : 0; const d = !v; const e = !!v; r.push('' + a + b + c + Number(d) + Number(e) + (v && 1 ? 1 : 0) + (v || 0 ? 1 : 0)); } let i = 3, n = 0; while (i) { i--; n++; } do { n++; } while (NaN); return [r, n]; })();
JSON.stringify(out)
""";

    private const string Expected = """
{"ropeBasic":[10890,"ab0ab1ab2ab3","98ab1999",97,10884],"ropeDeep":[100300,"y",299],"ropeLeftRight":[3190,"998996","997999"],"ropeEquality":[true,true,1,2,true,0,2,1],"ropeNumbers":"12.5nullundefinedtrue1,2!","ropeKeys":[true,true,336],"ropeJson":[305,true],"ropeRegex":[true,"acacac",401,400],"ropeSwitch":"hit","mapLive":[55,[0,1,2,3,4,30,31,32],[137,138,139]],"mapLruHead":[256,12502500,[5000,5001,5002]],"mapClear":[9],"mapDeleteAll":[["a"],1],"setCompact":[70,[90,91,92,93]],"mapForEachMutate":[1,3,4],"manyIterators":20000,"errGetProp":["TypeError: Cannot read properties of undefined (reading 'x')","TypeError: Cannot read properties of null (reading 'y')","TypeError: Cannot read properties of null (reading 'k1')","TypeError: Cannot read properties of undefined (reading '0')"],"errSetProp":["TypeError: Cannot set properties of undefined (setting 'x')","TypeError: Cannot set properties of null (setting 'k')","TypeError: Cannot set properties of undefined (setting '3')"],"errCall":["TypeError (not a function)","TypeError (not a function)","TypeError: Cannot read properties of undefined (reading 'm')","TypeError (not a function)"],"errCaught":100,"errFinally":["f","TypeError"],"errNested":[true,"Cannot read properties of undefined (reading 'x')"],"reOwnExec":[false,"aaa"],"reProtoExec":["xbxb",3,true],"reProtoRestored":["xbxb",true],"reProtoGlobal":"baa","reAfterRestore":"bbb","reReplaceForms":["a#b#c#","a[b|b|a|c|$]c","06/05/2024","a05-b25-c45","-a-b-c-","-😀-a-"],"reLastIndex":["bbb",0,"baa",2],"reTest":[true,1,true,2,false,0,true,1,true,2,false,0,1],"reLegacy":[["1","2","12"],"3","4","34"],"reFnMutates":"zzz","reSubclass":"bXb","keyCompound":[[1,12,103],["3","1.5","-1","4294967295","ts"],"f","n","max","obj",true],"keyComputedLiteral":[["7","7.5","x"],"7",1],"keyObjectRest":["b","a",["2","x"]],"keyTypedRmw":[[0,1.5,3,4.5],null,null,null,["0","1","2","3"]],"spreadReserve":[[1,2,3,4,5],[1,2,3,1,2,3,0],[1],["a","b","c"],[1,2,3,9],4,true,["0","1","2","3"]],"spreadCustomIter":[["i",3],1],"sortStable":[true,0,199],"sortUndefHoles":[6,1,2,3,null,null,false,true],"sortInconsistent":[100,4990],"sortMutating":[1,2,3,4,5],"sortNonClosure":[[3,1,2],[3,1,2],[1,2,3],[3,2,1]],"sortReentrant":[[1,2,3],[1,2,3,4,5,6,7,8,9]],"sortThrows":["RangeError",[3,1,2]],"sortDefault":[[1,10,9,"a","b",null,null],[3,2,1],[1,2,3]],"reLiteralFresh":[50,50],"reLiteralProto":3,"reLiteralFlags":["true:false","true:true"],"keyTypedOob":[0,null,null,1,0,null,7,0,2,7,0],"keyTypedTracking":[4,2,3,4,null,4,8,6,0,0,5,5,0,1,0,null,1,null,0,0],"icGetterProto":5650,"icGetterOwn":210,"icGetterThrows":["RangeError",20],"icTypedLength":160,"icMapSize":465,"icNotFound":[480,2],"icNotFoundProxyProto":"pppppppppppppppppppp","icMegamorphic":2719,"icMegamorphicProto":260,"icStringMethod":9957,"strForOf":[150,[1,2,1,1],4,2,1],"strForOfPatched":[["X","X"],["a","b"],0,["a","b"]],"strForOfBreak":[3,"hey"],"callExtraArgs":[[1,3,2,3],[1,null,null],[null,0,null,null],[1,2,3,4,5,6],[1,[2,3,4,5,6],6]],"callParamMutation":[197,1,2,5,1],"callNestedArgs":[40,15,1314,[4,8]],"callLiveAcross":["a0-0a0a1-1a1a2-2a2a3-3a3a4-4a4",120,[1,2,3],{"a":"x","b":["y","z"]},"1-2",7],"callThrowCatch":["p0",2,"tp1p1",2,"p2",2,"tp3p3",2,"bottom"],"callThis":[true,null,"object","string",false,"number",true,true,false],"callLegacyCaller":[true,null],"callStack":[true,true,true],"callFromGenerator":[[10,11,12],[10,11,12],true],"callNew":[1,null,1,3,1,[2,3],7,8,1,5,1,null],"callTail":[12502500,false,true],"tableDelIter":[64,64,["n58","n60","n62"],false,true,62],"tableCompact":[550,549,["k1299",1299],4,1299,false],"tableZeroNaN":[2,"c","b",true,4,true,true,1,true,0],"tableKeys":[1,2,3,4,5,6,7,8,9,null,null,10,10],"tableClearIter":[[9,10],[10,11],2],"tableBig":[13333,70000,6667],"reviveDupes":"{\"a\":\"2|2\",\"b\":[\"3|3\"],\"d\":\"1.5|1.50\",\"e\":\"A|\\\"\\\\u0041\\\"\"}","reviveIndexKeys":"{\"1\":\"1:4\",\"2\":\"2:2\",\"b\":\"b:1\",\"a\":\"a:3\"}","reviveMutate":[["a","b","c",""],{"c":3,"z":9}],"reviveArrays":[10,[20,[30,[40]]],{"x":[50]}],"reviveSourceObj":[0,1],"reviveHolderIdentity":[true,true,true],"concatInt":["k5","5k","a0","a0","0a","a2147483647","a2147483648","a-2147483648","a-2147483649","a1.5","a1e+21","aNaN","a-1",true,3,"a1000000000","a123456789",true],"sinkSpecies":[true,3,"1,2,3"],"sinkReentrant":[1,2,1,3,1,4,2,3,2,4,3,4],"sinkThrows":["RangeError",[2,3]],"sinkLarge":[23334,69999,140,69000,7,0,2],"unwindThroughNative":["a13","f1","RangeErrorb1"],"unwindCatchesInCaller":["c0","ok1","c2"],"unwindRethrownByNestedLoops":[1,2,3,4,"deep"],"unwindBuiltinRaises":["TypeError","TypeError","TypeError","TypeError","TypeError","TypeError","TypeError"],"unwindAccessors":["g","s1","trap"],"unwindConstruct":["{\"b\":1}","{\"o\":1}","{}","{\"b\":1}","{\"p\":1}","TypeError","ReferenceError","{\"q\":1}","TypeError","ReferenceError","{\"q\":2}","ReferenceError","fin","TypeError"],"unwindGenerators":[1,"cx","f",true,1,"gen","f"],"unwindTailCalls":["done","tail","TypeError","TypeError","done"],"unwindRedirected":[[1,2,3],[4,5,6],[7,8,9],[null,null,null],"in f","in f","in f"],"unwindFinallyReturn":["f","c","finally wins"],"strictEqAcrossTypes":["110000000000000000","110000000000000000","000000000000000000","000100000000000000","000010000000000000","000001100000000000","000001100000000000","000000010000000000","000000001000000000","000000000100000000","000000000010000000","000000000001000000","000000000000100000","000000000000011000","000000000000011000","000000000000000100","000000000000000010","000000000000000001"],"strictEqRopes":[true,true,false,"b",true,true],"numberEncodingBoundaries":[["number",true,false,"NaN",false,true,true],["number",true,false,"NaN",false,true,true],["number",true,false,"NaN",false,true,true],["number",true,false,"NaN",false,true,true],["number",false,true,"Infinity",false,true,true],["number",false,true,"1.7976931348623157e+308",false,true,true],["number",false,true,"0",true,true,true],["number",true,"[null]",0,true],["number",true]],"compareBranches":["Ll1100","Gg0011","lg0101","0000","0000","Ll1100","Gg0011","Ll1100","Ll1100","lg0101","0000","Gg0011"],"bitOpBoundaries":["0,0,1,0,0,0,1,1,1,0,0,0,-1","1,1,0,2,0,0,2,0,0,1,2,0.5,0","255,-1,-2,-2,-1,2147483647,-2147483648,0,0,-1,-2,-0.5,-2","255,2147483647,2147483646,-2,1073741823,1073741823,-2147483648,0,0,1,4294967294,1073741823.5,2147483646","0,-2147483648,-2147483647,0,-1073741824,1073741824,1,1,1,2,4294967296,1073741824,2147483647","0,-2147483648,-2147483647,0,-1073741824,1073741824,1,1,1,-2,-4294967296,-1073741824,-2147483649","255,-1,-2,-2,-1,2147483647,-2147483648,0,0,3,8589934590,2147483647.5,4294967294","0,0,1,0,0,0,1,1,1,4,8589934592,2147483648,4294967295","1,1,0,2,0,0,2,0,0,1.9,3.8,0.95,0.8999999999999999","255,-1,-2,-2,-1,2147483647,-2147483648,0,0,-1.9,-3.8,-0.95,-2.9","0,0,1,0,0,0,1,1,1,NaN,NaN,NaN,NaN","0,0,1,0,0,0,1,1,1,NaN,Infinity,Infinity,Infinity","0,-559939584,-559939583,-1119879168,-279969792,1867513856,1,1,1,6,2e+21,500000000000000000000,1e+21","1","7","4","20","2","2","25","2","-2","TypeError","TypeError",2,15,1,0],"getterFrames":[9,true,[3,6],true,1,50,"boom0","f","boom1","f","boom2","f"],"getterReceivers":["object","object","number","object"],"getterRedefined":[1,1,2,null,true,4,5,6,"TypeError"],"getterInExpression":[8,[2,6],8,"26",6],"truthiness":[["0101000","0101000","0101000","1010111","1010111","1010111","0101000","1010111","1010111","0101000","0101000","0101000","0101000","1010111","1010111","1010111","1010111","1010111"],4]}
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
// a store site that cycles through many shapes (Vm-wide store cache)
out.megaStore = (() => {
  const set = (o, v) => { o.x = v; return o; }, seen = [];
  class Base { constructor() { set(this, 'b'); } }
  const classes = []; for (let i = 0; i < 12; i++) classes.push(class extends Base {});
  for (let round = 0; round < 3; round++) for (const C of classes) seen.push(Object.keys(new C()).join());
  const r = [seen.every((k) => k === 'x')];
  // same shape as the cached append, each with a different say in adding x
  const fresh = () => ({ m: 1 }); set(fresh(), 1); set(fresh(), 1);
  const ne = Object.preventExtensions(fresh()); set(ne, 1); r.push(ne.x);
  const fz = Object.freeze({ x: 1 }); set(fz, 2); r.push(fz.x);
  const proto = fresh(), kid = Object.create(proto), read = (o) => o.x; r.push(read(kid)); set(proto, 'via proto'); r.push(read(kid));
  class G { set x(v) { this.hit = v; } } const g = new G(); set(g, 3); r.push(g.hit, Object.keys(g));
  class H {} const h1 = new H(), h2 = new H(); set(h1, 1); Object.defineProperty(H.prototype, 'x', { set(v) { this.viaSetter = v; }, configurable: true }); set(h2, 2); r.push(Object.keys(h1), Object.keys(h2));
  class R {} const r1 = new R(), r2 = new R(); set(r1, 1); Object.defineProperty(R.prototype, 'x', { value: 'ro', writable: false }); set(r2, 2); r.push(r1.x, r2.x, Object.keys(r2));
  const ow = { x: 0 }; set(ow, 1); set(ow, 2); r.push(ow.x); Object.defineProperty(ow, 'x', { writable: false }); set(ow, 3); r.push(ow.x);
  const arr = []; set(arr, 1); r.push(arr.x, arr.length); const pr = new Proxy({}, { set(t, k, v) { t[k] = v + 1; return true; } }); set(pr, 1); r.push(pr.x);
  const many = {}; for (let i = 0; i < 70; i++) many['k' + i] = i; set(many, 'dict'); r.push(many.x, Object.keys(many).length);
  return r;
})();
// the "prototype" of a function is created when first read
out.lazyProto = (() => {
  const r = [], mk = () => function F() {};
  const a = mk(), d = Object.getOwnPropertyDescriptor(a, 'prototype');
  r.push(d.writable, d.enumerable, d.configurable, typeof d.value, d.value.constructor === a, Object.keys(d.value), Object.getOwnPropertyDescriptor(d.value, 'constructor').enumerable, a.prototype === d.value);
  const b = mk(), read = (f) => f.prototype; r.push(read(mk()) !== read(mk()), read(b) === b.prototype, 'prototype' in b, b.hasOwnProperty('prototype'), Object.getOwnPropertyNames(b));
  const c = mk(); c.prototype = { x: 1 }; r.push(new c().x, Object.getPrototypeOf(new c()) === c.prototype);
  const e = mk(); const i1 = new e(); r.push(Object.getPrototypeOf(i1) === e.prototype, i1 instanceof e, i1.constructor === e);
  const f = mk(); Object.defineProperty(f, 'prototype', { writable: false }); const fp = f.prototype; f.prototype = 1; r.push(f.prototype === fp, typeof fp);
  const g = mk(); Object.freeze(g); r.push(typeof g.prototype, Object.isFrozen(g), g.prototype.constructor === g);
  const h = mk(); Object.defineProperty(h, 'prototype', { value: 5 }); r.push(h.prototype, new h() instanceof Object);
  const k = Object.create(mk()); const kp = k.prototype; r.push(typeof kp, kp === Object.getPrototypeOf(k).prototype);
  const m = mk(); const o = Object.create(m); o.prototype = 3; r.push(o.prototype, typeof m.prototype, Object.keys(o));
  const n = mk(); const bound = n.bind(); r.push(new bound() instanceof n, 'prototype' in bound);
  class C extends mk() {} r.push(typeof C.prototype, Object.getPrototypeOf(C.prototype) === Object.getPrototypeOf(C).prototype);
  r.push(Reflect.ownKeys(mk()), JSON.stringify(mk()), Object.entries(mk()).length, typeof (() => {}).prototype, typeof ({ m() {} }).m.prototype, typeof (async function () {}).prototype, typeof (function* () {}).prototype);
  const p = mk(); delete p.prototype; r.push(delete p.prototype, typeof p.prototype);
  const q = mk(); q.extra = 1; q.more = 2; q.prop3 = 3; r.push(Object.keys(q), typeof q.prototype);
  const s = mk(); for (let i = 0; i < 40; i++) s['x' + i] = i; delete s.x0; r.push(typeof s.prototype, s.prototype.constructor === s);
  return r;
})();
// new and super() of class constructors run in the caller's interpreter loop
out.classFrames = (() => {
  const log = [], msg = (f) => { try { return f(); } catch (e) { return e.constructor.name + ': ' + (e instanceof ReferenceError ? 'ref' : 'other'); } };
  class A { x = log.push('A.x'); constructor(v) { log.push('A', new.target.name, v); this.v = v; } }
  class B extends A { y = log.push('B.y'); constructor(v) { log.push('B before'); super(v + 1); log.push('B after', this.x, this.y); } }
  class C extends B { z = log.push('C.z'); }
  const c = new C(1); log.push(Object.keys(c), c instanceof A, Object.getPrototypeOf(c) === C.prototype);
  class Twice extends A { constructor() { super(1); super(2); } }
  class NoSuper extends A { constructor() { } }
  class ThisFirst extends A { constructor() { this.a = 1; super(); } }
  class RetObj extends A { constructor() { return { ret: 1 }; } }
  class RetBad extends A { constructor() { super(); return 1; } }
  class Thrower { constructor() { throw new RangeError('t'); } }
  class Catches extends Thrower { constructor() { try { super(); } catch (e) { log.push('caught ' + e.name); } return { caught: true }; } }
  class Uncaught extends Thrower { }
  class ArrowSuper extends A { constructor() { const s = () => super(5); s(); log.push(this.v); const g = () => this.v; log.push(g()); } }
  class EvalSuper extends A { constructor() { eval('super(6)'); log.push(this.v); } }
  class SuperInArgs extends A { constructor() { super((() => 7)()); } }
  class Field extends A { p = this.v * 2; constructor(a, ...rest) { super(a); this.rest = rest; } }
  class Spread extends A { constructor(...a) { super(...a); } }
  function Old(v) { this.old = v; return v === 'obj' ? { replaced: 1 } : 3; } Old.prototype.m = () => 'm';
  class FromOld extends Old { }
  class FromOldInit extends Old { f = 1; constructor(v) { super(v); this.after = this.f; } }
  class Nullish extends null { constructor() { return Object.create(Nullish.prototype); } }
  class NullishSuper extends null { constructor() { super(); } }
  class Swap extends A { } Object.setPrototypeOf(Swap, class Other { constructor() { this.other = 1; } });
  class ToArrow extends A { } Object.setPrototypeOf(ToArrow, () => {});
  class ToBound extends A { } Object.setPrototypeOf(ToBound, A.bind(null, 'bound'));
  class ToProxy extends A { } const trap = []; Object.setPrototypeOf(ToProxy, new Proxy(A, { construct(t, a, nt) { trap.push('construct'); return Reflect.construct(t, a, nt); } }));
  class Getter { constructor() { this.hit = 1; } }
  const nt = function () {}.bind(); Object.defineProperty(nt, 'prototype', { get() { log.push('proto read'); return Array.prototype; } });
  class WithNt extends Getter { }
  const r = [c.x, c.y, c.z];
  r.push(msg(() => new Twice()), msg(() => new NoSuper()), msg(() => new ThisFirst()), msg(() => new RetObj()), msg(() => new RetBad()), msg(() => new Catches()), msg(() => new Uncaught()));
  r.push(msg(() => new ArrowSuper().v), msg(() => new EvalSuper().v), new SuperInArgs().v, new Field(4, 5, 6), new Spread(8).v, msg(() => C()));
  r.push(new FromOld(1).old, new FromOld(1).m(), new FromOld('obj'), new FromOldInit(2), msg(() => new Nullish() instanceof Nullish), msg(() => new NullishSuper()));
  r.push(new Swap(), msg(() => new ToArrow()), new ToBound().v, new ToProxy(9).v, trap, Reflect.construct(WithNt, [], nt) instanceof Array, Reflect.construct(A, [1], Old) instanceof Old);
  let depth = 0; class Deep extends A { constructor(n) { super(n); depth++; if (n > 0) new Deep(n - 1); } } new Deep(50); r.push(depth);
  return [r, log.slice(0, 40)];
})();
// object literals with constant keys, cached per site
out.literals = (() => {
  const r = [], sym = Symbol('s');
  for (let i = 0; i < 3; i++) {
    const proto = { inherited: i };
    const mk = [
      { a: i, b: 2, c: 3 },
      { a: 1, a: i, b: 2 },
      { a: 1, b: 2, a: i },
      { __proto__: proto, a: i },
      { a: i, __proto__: proto, b: 2 },
      { ...{ x: i, a: 7 }, a: i, y: 1 },
      { a: i, ...(i % 2 ? { a: 'spread' } : null), b: 2 },
      { get a() { return i; }, b: 2, set a(v) { } },
      { a: 1, get a() { return i; } },
      { get a() { return 1; }, a: i },
      { [i % 2 ? 'a' : 'b']: i, a: 'k', b: 'l' },
      { 1: i, b: 2, 0: 3 },
      { '__proto__'() { return i; }, m() { return i; }, async n() { }, *o() { }, async *p() { } },
      { m() { return super.toString === Object.prototype.toString; }, ['c' + 'k']() { } },
      { [sym]: i, a: 1 },
    ];
    r.push(mk.map((o) => [Reflect.ownKeys(o).map(String), Object.getOwnPropertyDescriptor(o, 'a')?.value, Object.getPrototypeOf(o) === proto, o.inherited, Object.keys(o).length]));
    const names = mk[12], ms = mk[13];
    r.push(Object.keys(names), names.m.name, names.__proto__.name, names.__proto__(), Object.getOwnPropertyDescriptor(names, 'm').enumerable, Object.getOwnPropertyDescriptor(names, 'm').writable, names.m.hasOwnProperty('prototype'), ms.m(), ms.ck.name);
    const big = { k0: 0, k1: 1, k2: 2, k3: 3, k4: 4, k5: 5, k6: 6, k7: 7, k8: 8, k9: 9, k10: 10, k11: 11, k12: 12 }; big.added = i; r.push(Object.keys(big).length, big.k12, big.added);
  }
  return r;
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
{"sloppy":{"alias":[10,10,20,2],"extra":["x","y",2,["0","1","3"]],"missing":[null,1,false],"deleteRemap":[null,2,3,["0"]],"definedRO":[2,1],"definedValue":[7,8],"getter":[41,1,41],"frozen":[9,5,true],"sealed":[1,9,true],"desc":[{"value":3,"writable":true,"enumerable":true,"configurable":true},{"value":1,"writable":true,"enumerable":false,"configurable":true},"function"],"named":[[2,0,1],["0","1","length","callee","extra","Symbol(Symbol.iterator)"],1],"stringKey":["new","new",true],"inherited":[2,1,5,2,["0"]],"iter":["p",2,[2],["p",2]],"tostr":["[object Arguments]",true],"icShare":["L","plain",1,"live"],"manyArgs":[["z",15,30,45,60],70,"q"]},"strict":[1,"b",2,["TypeError","TypeError"],true],"freeze":[1,1,5,1,1,null,1,true,true,false,false,1,{"value":1,"writable":false,"enumerable":true,"configurable":false},9,false,true,false,{"value":9,"writable":true,"enumerable":true,"configurable":false},{"a":1,"b":2},true,["a","b"],"{\"a\":1,\"b\":2}","function",true,true,false,true,false],"protoFreeze":[[2,["x"]],[1,[]],1,"getter"],"redefine":[2,2,["a","c"],"{\"a\":1,\"c\":3}",{"a":1,"c":3},{"value":2,"writable":true,"enumerable":false,"configurable":true},"g",true,["a","c"],"d",["a","b","c"],[["a",1],["b","d"],["c",3]],["b","c","d"],{"k":1,"m":2},{"k":1,"m":6},false,"n",9,["length","name"],2,null],"bigShape":[39,null,7,"dyn","s1",null,false,true,false,39,41],"megaStore":[true,null,1,null,"via proto",3,["hit"],["x"],["viaSetter"],1,"ro",[],2,2,1,0,2,"dict",71],"lazyProto":[true,false,false,"object",true,[],false,true,true,true,true,true,["length","name","arguments","caller","prototype"],1,true,true,true,true,true,"object","object",true,true,5,true,"object",true,3,"object",["prototype"],true,false,"object",true,["length","name","arguments","caller","prototype"],null,0,"undefined","undefined","undefined","object",false,"object",["extra","more","prop3"],"object","object",true],"classFrames":[[2,6,10,"ReferenceError: ref","ReferenceError: ref","ReferenceError: ref",{"ret":1},"TypeError: other",{"caught":true},"RangeError: other",5,6,7,{"x":42,"v":4,"p":8,"rest":[5,6]},8,"TypeError: other",1,"m",{"replaced":1},{"old":2,"f":1,"after":1},true,"TypeError: other",{"other":1},"TypeError: other","bound",9,["construct"],true,true,51],["B before","A.x","A","C",2,"B.y","B after",2,6,"C.z",["x","v","y","z"],true,true,"A.x","A","Twice",1,"A.x","A","Twice",2,"A.x","A","RetBad",null,"caught RangeError","A.x","A","ArrowSuper",5,5,5,"A.x","A","EvalSuper",6,6,"A.x","A","SuperInArgs"]],"literals":[[[["a","b","c"],0,false,null,3],[["a","b"],0,false,null,2],[["a","b"],0,false,null,2],[["a"],0,true,0,1],[["a","b"],0,true,0,2],[["x","a","y"],0,false,null,3],[["a","b"],0,false,null,2],[["a","b"],null,false,null,2],[["a"],null,false,null,1],[["a"],0,false,null,1],[["b","a"],"k",false,null,2],[["0","1","b"],null,false,null,3],[["__proto__","m","n","o","p"],null,false,null,5],[["m","ck"],null,false,null,2],[["a","Symbol(s)"],1,false,null,1]],["__proto__","m","n","o","p"],"m","__proto__",0,true,true,false,true,"ck",14,12,0,[[["a","b","c"],1,false,null,3],[["a","b"],1,false,null,2],[["a","b"],1,false,null,2],[["a"],1,true,1,1],[["a","b"],1,true,1,2],[["x","a","y"],1,false,null,3],[["a","b"],"spread",false,null,2],[["a","b"],null,false,null,2],[["a"],null,false,null,1],[["a"],1,false,null,1],[["a","b"],"k",false,null,2],[["0","1","b"],null,false,null,3],[["__proto__","m","n","o","p"],null,false,null,5],[["m","ck"],null,false,null,2],[["a","Symbol(s)"],1,false,null,1]],["__proto__","m","n","o","p"],"m","__proto__",1,true,true,false,true,"ck",14,12,1,[[["a","b","c"],2,false,null,3],[["a","b"],2,false,null,2],[["a","b"],2,false,null,2],[["a"],2,true,2,1],[["a","b"],2,true,2,2],[["x","a","y"],2,false,null,3],[["a","b"],2,false,null,2],[["a","b"],null,false,null,2],[["a"],null,false,null,1],[["a"],2,false,null,1],[["b","a"],"k",false,null,2],[["0","1","b"],null,false,null,3],[["__proto__","m","n","o","p"],null,false,null,5],[["m","ck"],null,false,null,2],[["a","Symbol(s)"],1,false,null,1]],["__proto__","m","n","o","p"],"m","__proto__",2,true,true,false,true,"ck",14,12,2],"privates":[[3,"m3",6],true,false,"TypeError","TypeError","TypeError","TypeError","TypeError",["y","z","w","v","u"],"TypeError","TypeError",[],"{}",7],"withScope":[[5,"outer","outer","has:log","has:a","get:Symbol(Symbol.unscopables)","has:a","get:a",1,"getter","V",true,"I","J",["inh"]],5,5]}
""";

    // Long arrays in page mode (filter, flat, flatMap, Array.from and push past one page of elements) and Map/Set tables
    // across their page boundaries: every way of reading and changing them. The expected values were produced by Node.
    // Array patterns of plain local targets are one instruction (Op.DestructArray) that reads the iteration values first;
    // private fields are read and written in the dispatch loop; ** has a number fast path. Expected output from Node.
    private const string PatternsAndPrivatesScript = """
const out = [];
const log = (...a) => out.push(JSON.stringify(a));
{ const [a, b] = [1, 2]; const [c, , d] = [1, 2, 3]; const [e, f] = [1]; const [g] = []; let h, i; [h, i] = [5, 6]; log('basic', a, b, c, d, e, f, g, h, i); }
{ const [a, b] = 'xy'; const [c, d] = new Set([7, 8]); const [e, f] = new Map([[1, 2]]); log('iterables', a, b, c, d, e, f); }
{ const arr = [1, 2, 3]; const log2 = []; arr[Symbol.iterator] = function* () { log2.push('iter'); yield 10; yield 20; yield 30; }; const [a, b] = arr; log('customIter', a, b, log2); }
{ const log2 = []; const it = { [Symbol.iterator]() { let i = 0; return { next() { log2.push('next'); return { value: i++, done: false }; }, return() { log2.push('return'); return {}; } }; } }; const [a, b] = it; log('close', a, b, log2); }
{ const log2 = []; const it = { [Symbol.iterator]() { let i = 0; return { next() { log2.push('next'); return { value: i++, done: i > 1 }; }, return() { log2.push('return'); return {}; } }; } }; const [a, b, c] = it; log('exhausted', a, b, c, log2); }
{ const log2 = []; const it = { [Symbol.iterator]() { return { next() { log2.push('next'); throw new TypeError('nx'); }, return() { log2.push('return'); return {}; } }; } }; try { const [a] = it; } catch (e) { log('nextThrows', e.constructor.name, log2); } }
{ const it = { [Symbol.iterator]() { return { next() { return { value: 1, done: false }; }, return() { throw new RangeError('rt'); } }; } }; try { const [a] = it; log('no'); } catch (e) { log('returnThrows', e.constructor.name); } }
{ const a = [1,,3]; const [x, y, z] = a; log('holes', x, y, z); Array.prototype[1] = 'proto'; const [p, q] = [1,,3]; delete Array.prototype[1]; log('protoHole', p, q); }
{ try { const [a] = null; } catch (e) { log('null', e.constructor.name); } try { const [a] = 5; } catch (e) { log('num', e.constructor.name); } try { const [a] = {}; } catch (e) { log('obj', e.constructor.name); } }
{ const a = [1, 2]; const [x = 9, y = 8, z = 7] = a; log('defaults', x, y, z); }
{ let x = 1, y = 2; [x, y] = [y, x]; log('swap', x, y); }
{ const fns = []; for (const [i, v] of [[0, 'a'], [1, 'b']]) fns.push(() => i + v); log('capturedLoop', fns.map(f => f())); }
{ const r = []; for (const [k, v] of new Map([['a', 1], ['b', 2]])) r.push(k + v); log('mapEntries', r); }
{ function f([a, b], [c] = [3]) { return [a, b, c]; } log('params', f([1, 2]), f([1, 2], [9])); }
{ const o = {}; [o.a, o.b] = [1, 2]; log('memberTargets', o); }
{ const arr = [1, 2, 3]; const [a, ...rest] = arr; log('rest', a, rest); }
{ function* g() { const [a, b] = yield 1; return a + b; } const it = g(); it.next(); log('genPattern', it.next([2, 3]).value); }
{ let [a, b] = [1, 2]; { let [a, b] = [3, 4]; log('shadow', a, b); } log('outer', a, b); }
{ const [a, b] = new Array(5); log('newArray', a, b); const big = []; big[4000] = 1; const [c, d] = big; log('sparseBig', c, d); }
{ const q = [0, 1, 2, 3, 4, 5, 6, 7]; for (let i = 0; i < 5; i++) q.shift(); q.push(8); const [a, b, c, d] = q; log('queueMode', a, b, c, d); }
{ const [a, b, c] = [1, 2, 3].entries(); log('entriesIter', a, b, c); }
{ const [a, b, c, d, e, f, g, h, i, j, k, l, m, n, o, p, q] = Array.from({ length: 20 }, (_, i) => i); log('seventeen', a, q); }
{ try { eval('const [a, b] = [1, 2]; log("evalPattern", a, b)'); } catch (e) { log('eval', e.message); } }
{ with ({ w: 0 }) { const [w2, w3] = [1, 2]; log('withPattern', w2, w3); } }
{ var [va, vb] = [1, 2]; log('varPattern', va, vb); }
{ class A { #x = 1; #m() { return 2; } get #g() { return 3; } set #s(v) { this.v = v; } static #sx = 9; read() { return [this.#x, this.#m(), this.#g, (this.#s = 5, this.v), A.#sx]; } write(v) { this.#x = v; return this.#x; } has(o) { return #x in o; } } const a = new A(); log('private', a.read(), a.write(7), a.has(a), a.has({}), a.has(Object.freeze(new A()))); }
{ class A { #x = 1; static read(o) { return o.#x; } static write(o, v) { o.#x = v; } } try { A.read({}); } catch (e) { log('missingGet', e.constructor.name); } try { A.write({}, 1); } catch (e) { log('missingSet', e.constructor.name); } try { A.read(5); } catch (e) { log('primitiveGet', e.constructor.name); } const f = Object.freeze(new A()); A.write(f, 3); log('frozenPrivate', A.read(f)); }
{ class A { #m() { return 1; } static set(o) { o.#m = 2; } get #g() { return 1; } static setG(o) { o.#g = 2; } } try { A.set(new A()); } catch (e) { log('methodWrite', e.constructor.name); } try { A.setG(new A()); } catch (e) { log('noSetter', e.constructor.name); } }
{ class B { #a = 1; #b = 2; #c = 3; #d = 4; #e = 5; sum() { return this.#a + this.#b + this.#c + this.#d + this.#e; } } log('manyPrivate', new B().sum()); }
{ class P { #p = 1; static get(o) { return o.#p; } } const px = new Proxy(new P(), {}); try { P.get(px); log('no'); } catch (e) { log('proxyPrivate', e.constructor.name); } }
{ class Q { #v = 0; bump() { return ++this.#v; } } const q = new Q(); q.bump(); q.bump(); log('compound', q.bump()); }
{ log('exp', 2 ** 10, 2 ** -1, (-8) ** (1/3), 2 ** 0.5 === Math.SQRT2, String(10n ** 3n), (-2) ** 2, 0 ** 0, NaN ** 0, 1 ** Infinity, '3' ** '2'); try { 2n ** -1n; } catch (e) { log('expNeg', e.constructor.name); } try { 2 ** 2n; } catch (e) { log('expMixed', e.constructor.name); } }
out.join('\n')
""";

    private const string PatternsAndPrivatesExpected = """
["basic",1,2,1,3,1,null,null,5,6]
["iterables","x","y",7,8,[1,2],null]
["customIter",10,20,["iter"]]
["close",0,1,["next","next","return"]]
["exhausted",0,null,null,["next","next"]]
["nextThrows","TypeError",["next"]]
["returnThrows","RangeError"]
["holes",1,null,3]
["protoHole",1,"proto"]
["null","TypeError"]
["num","TypeError"]
["obj","TypeError"]
["defaults",1,2,7]
["swap",2,1]
["capturedLoop",["0a","1b"]]
["mapEntries",["a1","b2"]]
["params",[1,2,3],[1,2,9]]
["memberTargets",{"a":1,"b":2}]
["rest",1,[2,3]]
["genPattern",5]
["shadow",3,4]
["outer",1,2]
["newArray",null,null]
["sparseBig",null,null]
["queueMode",5,6,7,8]
["entriesIter",[0,1],[1,2],[2,3]]
["seventeen",0,16]
["evalPattern",1,2]
["withPattern",1,2]
["varPattern",1,2]
["private",[1,2,3,5,9],7,true,false,true]
["missingGet","TypeError"]
["missingSet","TypeError"]
["primitiveGet","TypeError"]
["frozenPrivate",3]
["methodWrite","TypeError"]
["noSetter","TypeError"]
["manyPrivate",15]
["proxyPrivate","TypeError"]
["compound",3]
["exp",1024,0.5,null,true,"1000",4,1,1,null,9]
["expNeg","RangeError"]
["expMixed","TypeError"]
""";

    [Fact]
    public void ArrayPatternsPrivateFieldsAndExponentMatchReferenceBehavior()
    {
        using var engine = new JsEngine();
        var got = engine.Evaluate(PatternsAndPrivatesScript).AsString().Split('\n');
        var expected = PatternsAndPrivatesExpected.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        Assert.Equal(expected.Length, got.Length);
        for (int i = 0; i < expected.Length; i++) Assert.True(expected[i] == got[i], $"line {i}: expected {expected[i]} but got {got[i]}");
    }

    // Closures, environments and objects with their rare parts apart, Object.assign into an empty object, new and instanceof
    // reading the prototype slot, class fields with a cache, and eval reusing compiled code. Expected output from Node.
    private const string LayoutAndCachesScript = """
const out = [];
const log = (...a) => out.push(JSON.stringify(a));
const keys = o => Reflect.ownKeys(o).map(String);
// --- closure and object layout
{ const f = function (a) { return a; }; f.x = 1; f.y = 2; f.z = 3; log('fnProps', Object.keys(f), f.name, f.length, typeof f.prototype); }
{ function F() {} delete F.name; log('fnDelete', F.name, Object.keys(F)); Object.defineProperty(F, 'name', { value: 'G' }); log(F.name); }
{ let o = {}; for (let i = 0; i < 80; i++) o['k' + i] = i; delete o.k5; log('dict', Object.keys(o).length, o.k79, o.k5, 'k5' in o); o.k5 = 1; log(Object.keys(o).at(-1)); }
{ const o = { a: 1 }; o.b = 2; o.c = 3; o.d = 4; o.e = 5; o.f = 6; o.g = 7; log('grow', o, Object.keys(o)); }
{ const s = { a: 1, b: 2 }; const c = { ...s, c: 3, d: 4, e: 5 }; const e = { ...s }; e.x = 1; const w = { z: 0, ...s, a: 9 }; log('spread', c, e, w, s); }
{ function F() { return (() => new.target)(); } log('nt', new F() === undefined, typeof F(), F() === undefined); function G() { const ar = () => new.target; return { v: ar() === G }; } log(new G().v, G().v); }
{ class B { m() { return 'b'; } static s() { return 'sb'; } } class D extends B { m() { const f = () => super.m(); return f() + 'd'; } static s() { return super.s() + 'd'; } } log('home', new D().m(), D.s()); const o = { __proto__: { h() { return 1; } }, h() { return super.h() + 1; } }; log(o.h()); }
{ class B { constructor() { this.a = 1; } } class D extends B { constructor() { const f = () => { super(); }; f(); this.b = 2; } } log('thisbox', new D()); }
// --- with and eval environments
{ const o = { p: 1 }; with (o) { p = 2; var q = p; } log('with', o, q); function f() { eval('var ev = 1'); return ev; } log(f()); function g(a) { eval('a = 5'); return a; } log(g(1)); function h() { let l = 1; return eval('l + 1'); } log(h()); }
{ function f() { eval('var x = 1'); return () => x; } log('evalClosure', f()()); const fs = []; for (let i = 0; i < 3; i++) { eval(''); fs.push(() => i); } log(fs.map(g => g())); }
// --- Object.assign
{ const s = { a: 1, b: 2 }; const t = Object.assign({}, s); t.c = 3; s.a = 9; log('assign', t, s, Object.assign({}, s, { c: 1 }), Object.assign({}, null, undefined, 'ab', 5, [7])); }
{ const seen = []; Object.defineProperty(Object.prototype, 'tr', { set(v) { seen.push(v); }, configurable: true }); const r = Object.assign({}, { tr: 1, u: 2 }); delete Object.prototype.tr; log('protoSetter', seen, r, Object.keys(r)); }
{ Object.defineProperty(Object.prototype, 'ro', { value: 1, writable: false, configurable: true }); let e = 'no'; try { Object.assign({}, { ro: 2 }); } catch (x) { e = x.constructor.name; } delete Object.prototype.ro; log('protoReadonly', e); }
{ const s = JSON.parse('{"__proto__": {"x": 1}, "y": 2}'); const r = Object.assign({}, s); log('protoKey', Object.keys(r), r.x, Object.getPrototypeOf(r) === Object.prototype); }
{ const g = { get a() { return 1; }, b: 2 }; log('getterSrc', Object.getOwnPropertyDescriptor(Object.assign({}, g), 'a')); const h = Object.defineProperty({ v: 1 }, 'hid', { value: 2, enumerable: false }); log(Object.keys(Object.assign({}, h)), 'hid' in Object.assign({}, h)); const ro = Object.freeze({ a: 1 }); const c = Object.assign({}, ro); c.a = 2; log(c, Object.isFrozen(c)); const sy = Symbol('s'); log(Object.getOwnPropertySymbols(Object.assign({}, { [sy]: 1 })).length, Object.assign({}, { 1: 'a', 0: 'b' })); }
{ let e = 'no'; try { Object.assign(Object.preventExtensions({}), { a: 1 }); } catch (x) { e = x.constructor.name; } log('nonExt', e); const n = Object.create(null); log(Object.keys(Object.assign(n, { a: 1 })), Object.getPrototypeOf(n)); class C { set a(v) { this.z = v; } } log(Object.assign(new C(), { a: 1 }).z); const a = []; log(Object.assign(a, { 0: 'x', length: 3 }).length, Object.keys(Object.assign({ k: 1 }, { j: 2 }))); const p = { pp: 1 }; const ch = Object.create(p); ch.own = 1; log(Object.assign({}, ch)); const self = { a: 1 }; log(Object.assign(self, self)); log(Object.assign({}, {}), Object.keys(Object.assign({}, {}, { q: 1 }))); }
// --- new and the prototype property
{ function F() {} F.prototype = { k: 1 }; log('proto', new F().k); F.prototype = 5; log(Object.getPrototypeOf(new F()) === Object.prototype); F.prototype = null; log(Object.getPrototypeOf(new F()) === Object.prototype); function G() {} const p0 = G.prototype; log(Object.getPrototypeOf(new G()) === p0, G.prototype.constructor === G); }
{ function F() {} F.a = 1; delete F.a; for (let i = 0; i < 70; i++) F['k' + i] = i; delete F.k1; F.prototype = { dict: 1 }; log('dictCtor', new F().dict, new F() instanceof F); class K {} log(Object.getOwnPropertyDescriptor(K, 'prototype').writable, new K() instanceof K); const tgt = function () {}; const r = Reflect.construct(function () { return this; }, [], tgt); log(Object.getPrototypeOf(r) === tgt.prototype); const px = new Proxy(function () {}, { get(t, k) { return k === 'prototype' ? { viaProxy: 1 } : t[k]; } }); log(new px().viaProxy, Object.getPrototypeOf(Reflect.construct(function () {}, [], px)).viaProxy); }
{ function F() {} Object.defineProperty(F, 'prototype', { value: { fixed: 1 }, writable: false }); log('roProto', new F().fixed); F.prototype = {}; log(new F().fixed); const arrow = () => {}; log(arrow.prototype, arrow.hasOwnProperty('prototype')); arrow.prototype = { a: 1 }; arrow.other = 1; log(({}) instanceof Object); let err = 'no'; try { new arrow(); } catch (x) { err = x.constructor.name; } log(err); }
// --- instanceof
{ class A {} class B extends A {} log('io', new B() instanceof A, new A() instanceof B, 1 instanceof A, null instanceof Object, ({}) instanceof Object, [] instanceof Array, (() => {}) instanceof Function); }
{ class E { static [Symbol.hasInstance](v) { return v === 1; } } class D extends E {} log('hasInst', 1 instanceof E, 2 instanceof E, 1 instanceof D, new D() instanceof D); const f = function () {}; Object.defineProperty(f, Symbol.hasInstance, { value: () => true }); log(0 instanceof f); }
{ const b = function () {}.bind(); function F() {} const bf = F.bind(); log('bound', new F() instanceof bf, new bf() instanceof F); const px = new Proxy(F, {}); log(new F() instanceof px); let e = 'no'; try { ({}) instanceof {}; } catch (x) { e = x.constructor.name; } log(e); e = 'no'; try { ({}) instanceof 5; } catch (x) { e = x.constructor.name; } log(e); e = 'no'; try { const ar = () => {}; ({}) instanceof ar; } catch (x) { e = x.constructor.name; } log(e); }
{ function F() {} const o = new F(); F.prototype = {}; log('protoChange', o instanceof F, new F() instanceof F); const g = function () {}; Object.setPrototypeOf(g, { [Symbol.hasInstance]: () => 'y' }); log(1 instanceof g); const h = function () {}; Object.setPrototypeOf(h, null); log(Function.prototype[Symbol.hasInstance].call(h, {}), Object.getOwnPropertyDescriptor(Function.prototype, Symbol.hasInstance).writable); let e = 'no'; try { new Object() instanceof h; } catch (x) { e = x.constructor.name; } log(e); const pr = new Proxy({}, { getPrototypeOf() { return F.prototype; } }); log(pr instanceof F); }
{ function F() {} F.prototype = 5; let e = 'no'; try { ({}) instanceof F; } catch (x) { e = x.constructor.name; } log('badProto', e, 1 instanceof F); }
// --- class fields
{ class A { a = 1; b = this.a + 1; 'c d' = 3; 0 = 'zero'; [`x${1}`] = 4; __proto__ = 5; } const o = new A(); log('fields', Object.keys(o), o.b, o['c d'], o[0], o.x1, Object.getOwnPropertyDescriptor(o, '__proto__'), Object.getPrototypeOf(o) === A.prototype); }
{ class B { constructor(r) { return r; } } class D extends B { f = 1; g = 2; } log('ret', new D({ z: 0 })); const fr = Object.preventExtensions({}); let e = 'no'; try { new D(fr); } catch (x) { e = x.constructor.name; } log(e, Object.keys(fr)); for (let i = 0; i < 2; i++) { log(new D({ z: i })); } e = 'no'; try { new D(Object.freeze({})); } catch (x) { e = x.constructor.name; } log(e); const logp = []; const px = new Proxy({}, { defineProperty(t, k, d) { logp.push(k); return Reflect.defineProperty(t, k, d); } }); new D(px); log(logp); const arr = new D([]); log(Array.isArray(arr), arr.f, arr.length); class Idx extends B { 0 = 'x'; 1 = 'y'; } const ar2 = new Idx([]); log(ar2.length, ar2[1], ar2); log(new Idx({})); const ro = {}; Object.defineProperty(ro, 'f', { value: 0, configurable: false, writable: false }); e = 'no'; try { new D(ro); } catch (x) { e = x.constructor.name; } log(e); const ov = { f: 'old' }; log(new D(ov), Object.getOwnPropertyDescriptor(ov, 'f').writable); }
{ class S { set a(v) { throw new Error('no setter call'); } } class T extends S { a = 1; } log('fieldNotSet', Object.keys(new T())); class P { static s = 1; static t = P.s + 1; v = 1; } log(P.s, P.t, new P().v, Object.keys(P)); for (let i = 0; i < 12; i++) { class Q { [`k${i % 2}`] = i; n = i; } log(new Q()); } }
// --- eval reuses compiled code
{ const fs = []; for (let i = 0; i < 3; i++) fs.push((0, eval)('(function () { return 1; })')); log('evalFresh', fs[0] === fs[1], fs[0]() + fs[1]()); const tags = []; const tag = s => s; for (let i = 0; i < 2; i++) { globalThis.__t = tag; tags.push((0, eval)('__t`x`')); } delete globalThis.__t; log(tags[0] === tags[1]); const rs = []; for (let i = 0; i < 2; i++) rs.push(eval('/a/g')); rs[0].lastIndex = 5; log(rs[0] === rs[1], rs[1].lastIndex); }
{ function s() { return eval('var v1 = 1; typeof v1 + (function () { return this === undefined; })()'); } function t() { 'use strict'; return eval('var v1 = 1; typeof v1 + (function () { return this === undefined; })()'); } log('evalStrict', s(), s(), t(), t()); function u() { 'use strict'; eval('var leak = 1'); return typeof leak; } function w() { eval('var leak = 1'); return typeof leak; } log(u(), w(), u(), w()); const src = 'with ({}) {}'; const r = []; function a() { return eval(src); } function b() { 'use strict'; try { return eval(src); } catch (e) { return e.constructor.name; } } r.push(a(), b(), a(), b()); log(r); }
{ class C { m() { return eval('super.toString === Object.prototype.toString'); } } let e = 'no'; const code = 'super.x'; function f() { try { return eval(code); } catch (x) { return x.constructor.name; } } log('evalSuper', new C().m(), new C().m(), f(), f()); function nt() { return eval('new.target === undefined'); } log(nt(), nt()); let e2 = []; for (let i = 0; i < 2; i++) { try { (0, eval)('new.target'); } catch (x) { e2.push(x.constructor.name); } } log(e2); for (let i = 0; i < 2; i++) { try { eval('1 +'); } catch (x) { e2.push(x.constructor.name); } } log(e2); class P { #p = 1; g() { return eval('this.#p'); } } class Q { #q = 2; g() { return eval('this.#q'); } } log(new P().g(), new Q().g()); let e3 = 'no'; try { class R { g() { return eval('this.#p'); } } new R().g(); } catch (x) { e3 = x.constructor.name; } log(e3); }
{ let n = 0; for (let i = 0; i < 200; i++) n += eval('1 + ' + (i % 90)); log('evalMany', n); const v = []; for (let i = 0; i < 3; i++) { v.push(eval('let q = ' + i + '; q')); let x = i; v.push(eval('x * 2')); } log(v); let big = 'var bb = 0;'; for (let i = 0; i < 3000; i++) big += ' bb += ' + i + ';'; log(eval(big + ' bb'), eval(big + ' bb')); }
out.join('\n')
""";

    private const string LayoutAndCachesExpected = """
["fnProps",["x","y","z"],"f",1,"object"]
["fnDelete","",[]]
["G"]
["dict",79,79,null,false]
["k5"]
["grow",{"a":1,"b":2,"c":3,"d":4,"e":5,"f":6,"g":7},["a","b","c","d","e","f","g"]]
["spread",{"a":1,"b":2,"c":3,"d":4,"e":5},{"a":1,"b":2,"x":1},{"z":0,"a":9,"b":2},{"a":1,"b":2}]
["nt",false,"undefined",true]
[true,false]
["home","bd","sbd"]
[2]
["thisbox",{"a":1,"b":2}]
["with",{"p":2},2]
[1]
[5]
[2]
["evalClosure",1]
[[0,1,2]]
["assign",{"a":1,"b":2,"c":3},{"a":9,"b":2},{"a":9,"b":2,"c":1},{"0":7,"1":"b"}]
["protoSetter",[1],{"u":2},["u"]]
["protoReadonly","TypeError"]
["protoKey",["y"],1,false]
["getterSrc",{"value":1,"writable":true,"enumerable":true,"configurable":true}]
[["v"],false]
[{"a":2},false]
[1,{"0":"b","1":"a"}]
["nonExt","TypeError"]
[["a"],null]
[1]
[3,["k","j"]]
[{"own":1}]
[{"a":1}]
[{},["q"]]
["proto",1]
[true]
[true]
[true,true]
["dictCtor",1,true]
[false,true]
[true]
[1,1]
["roProto",1]
[1]
[null,false]
[true]
["TypeError"]
["io",true,false,false,false,true,true,true]
["hasInst",true,false,true,false]
[true]
["bound",true,true]
[true]
["TypeError"]
["TypeError"]
["TypeError"]
["protoChange",false,true]
[true]
[false,false]
["no"]
[true]
["badProto","TypeError",false]
["fields",["0","a","b","c d","x1","__proto__"],2,3,"zero",4,{"value":5,"writable":true,"enumerable":true,"configurable":true},true]
["ret",{"z":0,"f":1,"g":2}]
["TypeError",[]]
[{"z":0,"f":1,"g":2}]
[{"z":1,"f":1,"g":2}]
["TypeError"]
[["f","g"]]
[true,1,0]
[2,"y",["x","y"]]
[{"0":"x","1":"y"}]
["TypeError"]
[{"f":1,"g":2},true]
["fieldNotSet",["a"]]
[1,2,1,["s","t"]]
[{"k0":0,"n":0}]
[{"k1":1,"n":1}]
[{"k0":2,"n":2}]
[{"k1":3,"n":3}]
[{"k0":4,"n":4}]
[{"k1":5,"n":5}]
[{"k0":6,"n":6}]
[{"k1":7,"n":7}]
[{"k0":8,"n":8}]
[{"k1":9,"n":9}]
[{"k0":10,"n":10}]
[{"k1":11,"n":11}]
["evalFresh",false,2]
[false]
[false,0]
["evalStrict","numberfalse","numberfalse","numbertrue","numbertrue"]
["undefined","number","undefined","number"]
[[null,"SyntaxError",null,"SyntaxError"]]
["evalSuper",true,true,"SyntaxError","SyntaxError"]
[true,true]
[["SyntaxError","SyntaxError"]]
[["SyntaxError","SyntaxError","SyntaxError","SyntaxError"]]
[1,2]
["SyntaxError"]
["evalMany",8400]
[[0,0,1,2,2,4]]
[4498500,4498500]
""";

    [Fact]
    public void LayoutsAssignInstanceofFieldsAndEvalCacheMatchReferenceBehavior()
    {
        using var engine = new JsEngine();
        var got = engine.Evaluate(LayoutAndCachesScript).AsString().Split('\n');
        var expected = LayoutAndCachesExpected.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        Assert.Equal(expected.Length, got.Length);
        for (int i = 0; i < expected.Length; i++) Assert.True(expected[i] == got[i], $"line {i}: expected {expected[i]} but got {got[i]}");
    }

    private const string PagedScript = """
const out = {};
const N = 9000;
const hash = a => { let h = 7; for (let i = 0; i < a.length; i++) { const v = a[i]; h = (h * 31 + (typeof v === 'number' ? v : v === undefined ? 3 : String(v).length)) % 1000003; } return h; };
const src = Array.from({ length: N }, (_, i) => i);
const long = () => src.filter(x => x % 10 !== 9);   // 8100 elements
const longFlat = () => src.flatMap(x => x % 3 ? [x] : [x, x + 1]);
const longFrom = () => Array.from({ length: N }, (_, i) => i * 2);
const longIter = () => Array.from(new Set(src));
const report = a => [a.length, a[0], a[1], a[4095], a[4096], a[a.length - 1], a[a.length], hash(a)];
// each way of making a long result, read every way
out.makers = [long, longFlat, longFrom, longIter, () => src.flat(), () => [src.slice(0, 5000), src.slice(5000)].flat(), () => src.filter(() => true), () => [[src], [src]].flat(2)].map(f => report(f()));
out.sizesAroundPage = [4095, 4096, 4097, 5311, 5312, 5313, 8191, 8192, 8193, 12288, 12289].map(n => { const a = Array.from({ length: n }, (_, i) => i), f = a.filter(() => true), g = a.flatMap(x => [x]); return [f.length, hash(f), f[n - 1], f[n], g.length, hash(g), g[n - 1], g[n]]; });
out.builtinChain = (() => { const a = long(); return [a.map(x => x + 1).length, a.filter(x => x & 1).length, a.reduce((s, x) => s + x, 0), a.reduceRight((s, x) => s * 2 % 1009 + x, 0), a.some(x => x === 8998), a.every(x => x >= 0), a.find(x => x > 5000), a.findLast(x => x < 5000), a.findIndex(x => x > 5000), a.findLastIndex(x => x < 5000)]; })();
out.forEachArgs = (() => { const a = long(); let ok = true, n = 0; a.forEach(function (v, i, self) { if (self !== a || this !== 7 && Number(this) !== 7 || a[i] !== v) ok = false; n++; }, 7); return [ok, n]; })();
out.ops = (() => { const r = {};
  r.forOf = (() => { let s = 0, n = 0; for (const x of long()) { s += x; n++; } return [s, n]; })();
  r.spread = (() => { const c = [...long()]; return [c.length, hash(c)]; })();
  r.spreadCall = Math.max(...long());
  r.entries = (() => { let s = 0; for (const [i, v] of long().entries()) s += i ^ v; return s; })();
  r.keys = Array.from(long().keys()).length;
  r.objectKeys = Object.keys(long()).length;
  r.has = (() => { const a = long(); return [0 in a, 8099 in a, 8100 in a, a.hasOwnProperty(5000), Object.getOwnPropertyDescriptor(a, '4096')]; })();
  r.join = long().join('').length;
  r.json = JSON.stringify(long()).length;
  r.json2 = JSON.stringify({ a: longFlat() }).length;
  r.slice = (() => { const a = long(); const b = a.slice(4090, 4100); return [b, a.length]; })();
  r.concat = (() => { const a = long(), b = a.concat(a, [1]); return [b.length, hash(b)]; })();
  r.concat2 = (() => { const b = [1].concat(long()); return [b.length, hash(b)]; })();
  r.indexOf = (() => { const a = long(); return [a.indexOf(5000), a.lastIndexOf(5000), a.includes(8999), a.at(-1), a.at(4096)]; })();
  r.sort = (() => { const a = long(); a.sort((x, y) => y - x); return [a[0], a[8099], hash(a)]; })();
  r.sortDefault = (() => { const a = long(); a.sort(); return [a[0], a[1], a[8099], hash(a)]; })();
  r.toSorted = (() => { const a = long(), b = a.toSorted((x, y) => y - x); return [a[0], b[0], b.length]; })();
  r.reverse = (() => { const a = long(); a.reverse(); return [a[0], a[8099], hash(a)]; })();
  r.fill = (() => { const a = long(); a.fill(5, 4000, 4200); return hash(a); })();
  r.copyWithin = (() => { const a = long(); a.copyWithin(0, 4000, 4200); return hash(a); })();
  r.with = (() => { const a = long(), b = a.with(4096, 'x'); return [a[4096], b[4096], b.length]; })();
  r.apply = Math.max.apply(null, long());
  r.reflectApply = Reflect.apply((...x) => x.length, null, long());
  r.ownKeys = Reflect.ownKeys(long()).length;
  r.typed = new Float64Array(long()).reduce((s, x) => s + x, 0);
  r.destructure = (() => { const [p, q, ...rest] = long(); return [p, q, rest.length, hash(rest)]; })();
  r.setFrom = new Set(long()).size;
  r.objectAssign = Object.keys(Object.assign({}, longFlat())).length;
  r.objectEntries = Object.entries(long()).length;
  r.stringFromCodes = String.fromCharCode.apply(null, long().slice(0, 100)).length;
  return r; })();
out.indexed = (() => { const a = long(); let s = 0; for (let i = 0; i < a.length; i++) s += a[i]; for (let i = 0; i < a.length; i++) a[i] = a[i] + 1; return [s, hash(a), a[8099]]; })();
out.fewIndexed = (() => { const a = long(); a[5000] = 'w'; a[4096] = 'v'; return [a[5000], a[4096], a[4095], a.length, hash(a)]; })();
out.writeHole = (() => { const a = long(); delete a[4100]; return [4100 in a, a[4100], a.length, hash(a), a.filter(() => true).length, a.reduce((n) => n + 1, 0)]; })();
out.holeProto = (() => { const a = long(); delete a[4100]; Array.prototype[4100] = 'p'; try { let seen = 0; a.forEach(v => { if (v === 'p') seen++; }); return [a[4100], seen, 4100 in a]; } finally { delete Array.prototype[4100]; } })();
out.protoIndexOnLong = (() => { Array.prototype[9000] = 'q'; try { const a = long(); return [a[9000], a.length, a[8099], a[8100]]; } finally { delete Array.prototype[9000]; } })();
out.mutators = (() => { const r = {};
  r.push = (() => { const a = long(); a.push(1, 2); return [a.length, a[8100], a[8101], hash(a)]; })();
  r.pop = (() => { const a = long(); return [a.pop(), a.pop(), a.length, hash(a)]; })();
  r.shift = (() => { const a = long(); return [a.shift(), a.shift(), a.length, a[0], hash(a)]; })();
  r.unshift = (() => { const a = long(); a.unshift(5, 6); return [a.length, a[0], a[2], hash(a)]; })();
  r.unshift0 = (() => { const a = long(); return [a.unshift(), a.length, hash(a)]; })();
  r.splice = (() => { const a = long(); const d = a.splice(4090, 10, 'x'); return [d, a.length, a[4090], a[4091], hash(a)]; })();
  r.spliceFront = (() => { const a = long(); const d = a.splice(1, 2); return [d, a.length, a[1], hash(a)]; })();
  r.lengthDown = (() => { const a = long(); a.length = 4097; return [a.length, a[4096], a[4097], hash(a)]; })();
  r.lengthUp = (() => { const a = long(); a.length = 8200; return [a.length, a[8099], 8100 in a, a[8150]]; })();
  r.lengthZero = (() => { const a = long(); a.length = 0; a.push(1); return [a.length, a[0]]; })();
  r.writeEnd = (() => { const a = long(); a[a.length] = 1; a[a.length + 2] = 2; return [a.length, a[8100], 8101 in a, a[8102]]; })();
  r.writeFar = (() => { const a = long(); a[5000000] = 1; return [a.length, a[8099], a[5000000], Object.keys(a).length]; })();
  r.defineAccessor = (() => { const a = long(); let g = 0; Object.defineProperty(a, 4096, { get() { g++; return 'g'; } }); return [a[4096], g, a.filter(x => x === 'g').length, a[4095]]; })();
  r.defineRO = (() => { 'use strict'; const a = long(); Object.defineProperty(a, 3, { value: 'ro', writable: false }); try { a[3] = 1; return 'no'; } catch (e) { return [e.constructor.name, a[3]]; } })();
  r.named = (() => { const a = long(); a.x = 1; a.y = 2; return [a.x, a.y, a.length, Object.keys(a).length, a[8099]]; })();
  r.freeze = (() => { 'use strict'; const a = Object.freeze(long()); const o = []; for (const f of [() => { a[0] = 1; }, () => a.push(1), () => a.pop(), () => { a.length = 0; }]) { try { f(); o.push('no'); } catch (e) { o.push(e.constructor.name); } } return [o, Object.isFrozen(a), a[4096], a.length, a.filter(() => true).length]; })();
  r.seal = (() => { 'use strict'; const a = Object.seal(long()); a[1] = 'w'; let e1 = 'no'; try { a.push(1); } catch (e) { e1 = e.constructor.name; } return [e1, a[1], a.length]; })();
  r.preventExtensions = (() => { 'use strict'; const a = Object.preventExtensions(long()); a[4096] = 'w'; let e1 = 'no'; try { a.push(1); } catch (e) { e1 = e.constructor.name; } return [e1, a[4096], a.length, a.pop(), a.length]; })();
  r.lengthReadonly = (() => { 'use strict'; const a = long(); Object.defineProperty(a, 'length', { writable: false }); let e1 = 'no'; try { a.push(1); } catch (e) { e1 = e.constructor.name; } a[0] = 'w'; return [e1, a[0], a.length]; })();
  return r; })();
// callbacks that change the array they run over
out.callbackMutates = (() => { const r = {};
  r.grow = (() => { const a = long(); let n = 0; a.forEach((v, i) => { n++; if (i === 10) a.push(1); }); return [n, a.length]; })();
  r.shrink = (() => { const a = long(); let n = 0; a.forEach((v, i) => { n++; if (i === 4200) a.length = 4300; }); return [n, a.length]; })();
  r.shrinkMap = (() => { const a = long(); const m = a.map((v, i) => { if (i === 4200) a.length = 4300; return v + 1; }); return [m.length, 4299 in m, 4300 in m, m[4299], m[4300]]; })();
  r.deleteAhead = (() => { const a = long(); const seen = []; a.forEach((v, i) => { if (i === 4096) delete a[4097]; if (i > 4095 && i < 4100) seen.push(i); }); return seen; })();
  r.shiftDuring = (() => { const a = long(); let s = 0, n = 0; a.forEach((v, i) => { n++; s += v; if (i === 4095) a.shift(); }); return [s, n, a.length]; })();
  r.writeAhead = (() => { const a = long(); let saw; a.filter((v, i) => { if (i === 0) a[5000] = 'w'; if (i === 5000) saw = v; return false; }); return saw; })();
  r.reduceShrink = (() => { const a = long(); return a.reduce((s, v, i) => { if (i === 5000) a.length = 5001; return s + 1; }, 0); })();
  r.reduceThrow = (() => { const a = long(); try { a.reduce((s, v, i) => { if (i === 5000) throw new RangeError('x'); return s; }, 0); return 'no'; } catch (e) { return [e.constructor.name, a.length, a[5000]]; } })();
  r.filterThrow = (() => { try { src.filter((v, i) => { if (i === 8000) throw new EvalError('x'); return true; }); return 'no'; } catch (e) { return [e.constructor.name, src.filter(x => x < 3)]; } })();
  r.fromThrow = (() => { try { Array.from({ length: 9000 }, (v, i) => { if (i === 8000) throw new EvalError('x'); return i; }); return 'no'; } catch (e) { return e.constructor.name; } })();
  r.fromIterThrow = (() => { let closed = 0; const it = { [Symbol.iterator]() { let i = 0; return { next() { return { value: i++, done: i > 9000 }; }, return() { closed++; return {}; } }; } }; try { Array.from(it, v => { if (v === 8000) throw new EvalError('x'); return v; }); return 'no'; } catch (e) { return [e.constructor.name, closed]; } })();
  r.nested = (() => { const out2 = src.filter(x => { if (x !== 4500) return x % 2 === 0; const inner = src.filter(y => y % 2 === 1); return inner.length === 4500; }); return [out2.length, hash(out2)]; })();
  r.nestedFlat = (() => { const o2 = src.flatMap(x => x === 6000 ? src.filter(y => y < 7000) : [x]); return [o2.length, hash(o2)]; })();
  r.generatorCallback = (() => { function* g() { const a = long().filter(x => x > 8000); yield a.length; yield a[0]; yield long().map(x => x * 2).length; } return [...g()]; })();
  r.iteratorDuringMutation = (() => { const a = long(); const it = a[Symbol.iterator](); const x = [it.next().value]; a.shift(); x.push(it.next().value); a.length = 2; x.push(it.next().done, it.next().done); a.push(9, 9, 9); x.push(it.next().done); return x; })();
  return r; })();
// species and subclasses never get pages from the sink; proxies read through traps
out.species = (() => { const r = {}; class A extends Array {} const a = A.from(src); const f = a.filter(x => x > 10); r.sub = [f instanceof A, f.length, hash(f)]; const g = a.flatMap(x => [x]); r.subFlat = [g instanceof A, g.length]; r.fromSub = [A.from({ length: 6000 }, (_, i) => i) instanceof A, A.from({ length: 6000 }, (_, i) => i).length]; class B extends Array { static get [Symbol.species]() { return Array; } } const h = B.from(src).filter(() => true); r.speciesArray = [h instanceof B, Array.isArray(h), h.length, h[8999]]; let touched = 0; class C extends Array { constructor(n) { super(n); touched++; } } r.ctor = [C.from(src).filter(() => true).length > 0, touched]; const p = new Proxy(src, {}); r.proxy = [p.filter(() => true).length, Array.prototype.filter.call(p, x => x > 8990).length, Array.isArray(Array.prototype.filter.call(p, () => true))]; const log = []; const q = new Proxy(long(), { get(t, k, rc) { if (k === 4096 || k === '4096') log.push('get'); return Reflect.get(t, k, rc); }, has(t, k) { if (k === '4096') log.push('has'); return Reflect.has(t, k); } }); q.forEach(() => {}); r.proxyTrap = log; r.arrayLike = Array.prototype.filter.call({ length: 7000, 6000: 'a', 3: 'b' }, () => true); return r; })();
// arrays built by push across the page size
out.pushBuilt = (() => { const r = {}; const mk = n => { const a = []; for (let i = 0; i < n; i++) a.push(i); return a; }; r.lens = [4095, 4096, 4097, 5312, 5313, 8192, 8193, 20000].map(n => { const a = mk(n); return [a.length, a[0], a[n - 1], a[n], hash(a), a.reduce((s, x) => s + x, 0)]; }); r.popAll = (() => { const a = mk(6000); let s = 0; while (a.length) s += a.pop(); return [s, a.length]; })(); r.pushPop = (() => { const a = mk(6000); for (let i = 0; i < 5000; i++) { a.push(i); a.pop(); a.pop(); } return [a.length, hash(a)]; })(); r.shiftAll = (() => { const a = mk(6000); let s = 0; while (a.length > 10) s += a.shift(); return [s, a.length, a[0]]; })(); r.literal = (() => { const a = [...mk(6000), 1, ...mk(10)]; return [a.length, hash(a)]; })(); r.fromSet = (() => { const a = [...new Set(mk(7000))]; return [a.length, hash(a)]; })(); r.holeThenPush = (() => { const a = []; a[4095] = 1; a.push(2); a.push(3); return [a.length, 0 in a, a[4095], a[4096], a[4097], a.indexOf(3), a.filter(() => true)]; })(); r.lengthThenPush = (() => { const a = []; a.length = 6000; a.push(1); return [a.length, a[6000], 5 in a]; })(); r.newArrayPush = (() => { const a = new Array(4096); a.push(1); return [a.length, a[4096], 0 in a]; })(); r.fullLiteralPush = (() => { const a = Array.from({ length: 4096 }, () => 1); a.push(2); a.push(3); return [a.length, a[4097], hash(a)]; })(); r.pushMany = (() => { const a = mk(4090); a.push(...mk(20)); return [a.length, a[4100], hash(a)]; })(); r.indexAfterPush = (() => { const a = []; let s = 0; for (let i = 0; i < 10000; i++) { a.push(i); s += a[i >> 1]; } return [s, a.length]; })(); r.setAfterPush = (() => { const a = mk(10000); for (let i = 0; i < 10000; i += 3) a[i] = -i; return hash(a); })(); r.applyPushed = Math.max.apply(null, mk(6000)); r.spreadPushed = Math.max(...mk(6000)); r.mapPushed = mk(6000).map(x => x + 1)[5999]; r.jsonPushed = JSON.stringify(mk(6000)).length; r.sortPushed = mk(6000).sort((x, y) => y - x)[0]; return r; })();
// Map and Set across page boundaries
out.tables = (() => { const r = {}; const N2 = 13000;
  const m = new Map(); for (let i = 0; i < N2; i++) m.set(i * 7, i); r.size = [m.size, m.get(0), m.get(7 * 4096), m.get(7 * 12999), m.get(1), m.has(7 * 8192)];
  r.order = (() => { let h = 0, n = 0; for (const [k, v] of m) { h = (h * 31 + k + v) % 1000003; n++; } return [h, n]; })();
  r.update = (() => { const a = new Map(m); a.set(7 * 4096, 'x'); return [a.get(7 * 4096), a.size, [...a.keys()][4096], [...a.values()][4096]]; })();
  r.deleteRecompact = (() => { const a = new Map(m); for (let i = 0; i < N2; i++) if (i % 3) a.delete(i * 7); for (let i = 0; i < 9000; i++) a.set('n' + i, i); const ks = [...a.keys()]; return [a.size, ks[0], ks[1], ks[4333], ks[4334], ks[a.size - 1], a.get('n8999'), a.get(7 * 3), a.get(7)]; })();
  r.deleteDuringIter = (() => { const a = new Map(m), seen = []; let n = 0; for (const [k] of a) { if (n === 4095) { a.delete(7 * 4096); a.delete(7 * 4097); } if (n >= 4094 && n < 4098) seen.push(k); n++; } return [seen, n, a.size]; })();
  r.addDuringIter = (() => { const s = new Set([1, 2, 3]); let n = 0; for (const v of s) { if (n < 9000) s.add(v + 3); n++; } return [n, s.size]; })();
  r.compactDuringIter = (() => { const s = new Set(); for (let i = 0; i < 9000; i++) s.add(i); const it = s.values(); for (let i = 0; i < 5000; i++) it.next(); for (let i = 0; i < 8990; i++) s.delete(i); for (let i = 0; i < 9000; i++) s.add('x' + i); const a = []; for (let i = 0; i < 3; i++) a.push(it.next().value); return [a, s.size]; })();
  r.clearDuringIter = (() => { const s = new Set(); for (let i = 0; i < 9000; i++) s.add(i); const it = s.values(); for (let i = 0; i < 5000; i++) it.next(); s.clear(); s.add('a'); s.add('b'); return [it.next().value, it.next().value, it.next().done, s.size, s.has(1), [...s]]; })();
  r.clearRefill = (() => { const a = new Map(m); a.clear(); for (let i = 0; i < 9000; i++) a.set(i, -i); return [a.size, a.get(8999), a.get(4096), [...a.keys()][8999]]; })();
  r.sameValueZero = (() => { const a = new Map(); for (let i = 0; i < 5000; i++) a.set(i + 0.5, i); a.set(NaN, 'nan'); a.set(0, 'zero'); a.set(-0, 'mz'); a.set('0', 'str'); a.set(0n, 'big'); const o = {}; a.set(o, 'o'); a.set(null, 'n'); a.set(undefined, 'u'); a.set(true, 't'); const ks = [...a.keys()]; return [a.get(NaN), a.get(0), a.get(-0), Object.is(ks[5001], 0), a.get('0'), a.get(0n), a.get(o), a.get({}), a.get(null), a.get(undefined), a.get(true), a.size]; })();
  r.setKeysAreValues = (() => { const s = new Set(); for (let i = 0; i < 5000; i++) s.add(i); const e = [...s.entries()][4500]; let f = 0; s.forEach((v, k, self) => { if (v === k && self === s) f++; }); return [e, f, [...s.values()][4099], s.delete(4099), s.has(4099), s.size]; })();
  r.forEachMutate = (() => { const a = new Map(); for (let i = 0; i < 5000; i++) a.set(i, i); let n = 0; a.forEach((v, k) => { n++; if (k === 4095) a.delete(4096); if (k === 4999) a.set(5000, 0); }); return [n, a.size]; })();
  r.setOps = (() => { const s = new Set(); for (let i = 0; i < 6000; i++) s.add(i); const t = new Set(); for (let i = 3000; i < 9000; i++) t.add(i); return [s.union(t).size, s.intersection(t).size, s.difference(t).size, s.symmetricDifference(t).size, s.isSubsetOf(t), [...s.intersection(t)][4095]]; })();
  r.groupBy = (() => { const g = Map.groupBy(src, x => x % 5000); return [g.size, g.get(4095), g.get(1)]; })();
  return r; })();
JSON.stringify(out)
""";

    private const string PagedExpected = """
{"makers":[[8100,0,1,4550,4551,8998,null,865477],[12000,0,1,3071,3072,8999,null,153421],[9000,0,2,8190,8192,17998,null,815636],[9000,0,1,4095,4096,8999,null,424977],[9000,0,1,4095,4096,8999,null,424977],[9000,0,1,4095,4096,8999,null,424977],[9000,0,1,4095,4096,8999,null,424977],[18000,0,1,4095,4096,8999,null,864508]],"sizesAroundPage":[[4095,643196,4094,null,4095,643196,4094,null],[4096,943114,4095,null,4096,943114,4095,null],[4097,240543,4096,null,4097,240543,4096,null],[5311,237067,5310,null,5311,237067,5310,null],[5312,354367,5311,null,5312,354367,5311,null],[5313,990659,5312,null,5313,990659,5312,null],[8191,512747,8190,null,8191,512747,8190,null],[8192,903303,8191,null,8192,903303,8191,null],[8193,10501,8192,null,8193,10501,8192,null],[12288,968925,12287,null,12288,968925,12287,null],[12289,48873,12288,null,12289,48873,12288,null]],"builtinChain":[8100,3600,36441900,994,true,true,5001,4998,4501,4499],"forEachArgs":[true,8100],"ops":{"forOf":[36441900,8100],"spread":[8100,865477],"spreadCall":8998,"entries":22713726,"keys":8100,"objectKeys":8100,"has":[true,true,false,true,{"value":4551,"writable":true,"enumerable":true,"configurable":true}],"join":31401,"json":39502,"json2":58528,"slice":[[4544,4545,4546,4547,4548,4550,4551,4552,4553,4554],8100],"concat":[16201,167447],"concat2":[8101,210765],"indexOf":[4500,4500,false,8998,4551],"sort":[8998,0,796462],"sortDefault":[0,1,998,672576],"toSorted":[0,8998,8100],"reverse":[8998,0,796462],"fill":133362,"copyWithin":425017,"with":[4551,"x",8100],"apply":8998,"reflectApply":8100,"ownKeys":8101,"typed":36441900,"destructure":[0,1,8098,557761],"setFrom":8100,"objectAssign":12000,"objectEntries":8100,"stringFromCodes":100},"indexed":[36441900,876590,8999],"fewIndexed":["w","v",4550,8100,641596],"writeHole":[false,null,8100,193602,8099,8099],"holeProto":["p",1,true],"protoIndexOnLong":["q",8100,8998,null],"mutators":{"push":[8102,1,2,720937],"pop":[8998,8997,8098,133796],"shift":[0,1,8098,2,557761],"unshift":[8102,5,0,922066],"unshift0":[8100,8100,865477],"splice":[[4544,4545,4546,4547,4548,4550,4551,4552,4553,4554],8091,"x",4555,200905],"spliceFront":[[1,2],8098,3,837052],"lengthDown":[4097,4551,null,443120],"lengthUp":[8200,8998,false,null],"lengthZero":[1,1],"writeEnd":[8104,1,false,null],"writeFar":[5000001,8998,1,8101],"defineAccessor":["g",1,1,4550],"defineRO":["TypeError","ro"],"named":[1,2,8100,8102,8998],"freeze":[["TypeError","TypeError","TypeError","TypeError"],true,4551,8100,8100],"seal":["TypeError","w",8100],"preventExtensions":["TypeError","w",8100,8998,8099],"lengthReadonly":["TypeError","w",8100]},"callbackMutates":{"grow":[8100,8101],"shrink":[4300,4300],"shrinkMap":[8100,true,false,4777,null],"deleteAhead":[4096,4098,4099],"shiftDuring":[36437349,8099,8099],"writeAhead":"w","reduceShrink":5001,"reduceThrow":["RangeError",8100,5555],"filterThrow":["EvalError",[0,1,2]],"fromThrow":"EvalError","fromIterThrow":["EvalError",1],"nested":[4500,815017],"nestedFlat":[15999,607996],"generatorCallback":[899,8001,8100],"iteratorDuringMutation":[0,2,true,true,true]},"species":{"sub":[true,8989,521012],"subFlat":[true,9000],"fromSub":[true,6000],"speciesArray":[false,true,9000,8999],"ctor":[true,2],"proxy":[9000,9,true],"proxyTrap":["has","get"],"arrayLike":["b","a"]},"pushBuilt":{"lens":[[4095,0,4094,null,643196,8382465],[4096,0,4095,null,943114,8386560],[4097,0,4096,null,240543,8390656],[5312,0,5311,null,354367,14106016],[5313,0,5312,null,990659,14111328],[8192,0,8191,null,903303,33550336],[8193,0,8192,null,10501,33558528],[20000,0,19999,null,928452,199990000]],"popAll":[17997000,0],"pushPop":[1000,781287],"shiftAll":[17937055,10,5990],"literal":[6011,77201],"fromSet":[7000,687131],"holeThenPush":[4098,false,1,2,3,4097,[1,2,3]],"lengthThenPush":[6001,1,false],"newArrayPush":[4097,1,false],"fullLiteralPush":[4098,3,815844],"pushMany":[4110,10,146522],"indexAfterPush":[24995000,10000],"setAfterPush":726342,"applyPushed":5999,"spreadPushed":5999,"mapPushed":6000,"jsonPushed":28891,"sortPushed":5999},"tables":{"size":[13000,0,4096,12999,null,true],"order":[874438,13000],"update":["x",13000,28672,"x"],"deleteRecompact":[13334,0,21,90993,"n0","n8999",8999,3,null],"deleteDuringIter":[[28658,28665,28686,28693],12998,12998],"addDuringIter":[9003,9003],"compactDuringIter":[[8990,8991,8992],9010],"clearDuringIter":["a","b",true,2,false,["a","b"]],"clearRefill":[9000,-8999,-4096,8999],"sameValueZero":["nan","mz","mz",true,"str","big","o",null,"n","u","t",5008],"setKeysAreValues":[[4500,4500],5000,4099,true,false,4999],"forEachMutate":[5000,5000],"setOps":[9000,3000,3000,6000,false,null],"groupBy":[5000,[4095],[1,5001]]}}
""";

    [Fact]
    public void PagedArraysAndTablesMatchReferenceBehavior()
    {
        using var engine = new JsEngine();
        var exp = System.Text.Json.JsonDocument.Parse(PagedExpected).RootElement;
        var got = System.Text.Json.JsonDocument.Parse(engine.Evaluate(PagedScript).AsString()).RootElement;
        foreach (var p in exp.EnumerateObject())
        {
            Assert.True(got.TryGetProperty(p.Name, out var g), $"missing case {p.Name}");
            Assert.True(p.Value.GetRawText() == g.GetRawText(), $"{p.Name}: expected {p.Value.GetRawText()} but got {g.GetRawText()}");
        }
        Assert.Equal(exp.EnumerateObject().Count(), got.EnumerateObject().Count());
    }

    [Fact]
    public void PagedResultsAreChargedToTheBudgetWhileTheyGrow()
    {
        using var engine = new JsEngine(new JsEngineOptions { Limits = new JsLimits { MaxTrackedBytes = 4 * 1024 * 1024 } });
        Assert.Throws<JsLimitExceededException>(() => engine.Evaluate("Array.from({ length: 1e9 }, () => 1).length"));
        using var engine2 = new JsEngine(new JsEngineOptions { Limits = new JsLimits { MaxTrackedBytes = 4 * 1024 * 1024 } });
        Assert.Throws<JsLimitExceededException>(() => engine2.Evaluate("const a = []; while (true) a.push(1);"));
    }

    [Fact]
    public void RecursionThroughNewAndSuperHitsTheDepthLimit()
    {
        using var engine = new JsEngine();
        Assert.Throws<JsLimitExceededException>(() => engine.Evaluate("class A { } class B extends A { constructor() { super(); new B(); } } new B();"));
    }

    [Fact]
    public void ObjectLiteralsAreChargedToTheBudget()
    {
        using var engine = new JsEngine(new JsEngineOptions { Limits = new JsLimits { MaxTrackedBytes = 4 * 1024 * 1024 } });
        Assert.Throws<JsLimitExceededException>(() => engine.Evaluate("const a = []; while (true) a.push({ p: 1, q: 2, r: 3, s() {} });"));
    }

    [Fact]
    public void PrototypeCachesTellRealmsApart()
    {
        using var engine = new JsEngine();
        engine.SetFunction("newRealm", static (ref JsCallContext c) => c.CreateRealm(), 0);
        var r = engine.Evaluate("""
            const g = newRealm();
            g.Object.prototype.foo = 'other'; Object.prototype.foo = 'home';
            g.Array.prototype.bar = 'other'; Array.prototype.bar = 'home';
            function read(o) { return o.foo; }
            function readBar(o) { return o.bar; }
            function own(o) { return o.foo; }
            const results = [];
            for (let i = 0; i < 3; i++) {
              results.push(read({}), read(new g.Object()), read(g.Object.create(g.Object.prototype)), read(g.eval('({})')));
              results.push(readBar([]), readBar(new g.Array()), readBar(g.eval('[]')));
            }
            results.join();
            """).AsString();
        Assert.Equal(string.Join(",", Enumerable.Repeat("home,other,other,other,home,other,other", 3)), r);
    }

    [Fact]
    public void AddTransitionCacheBumpsTheEpochOfAPrototype()
    {
        using var engine = new JsEngine();
        // the store site caches the append on the shape of `{}`, which `p` has as well; `read` caches the absence of x on c
        var r = engine.Evaluate("""
            function set(o) { o.x = 1; }
            set({}); set({});
            var p = {}, c = Object.create(p), read = function (o) { return o.x; };
            var before = read(c); read(c);
            set(p);
            String(before) + ',' + read(c);
            """).AsString();
        Assert.Equal("undefined,1", r);
    }

    [Fact]
    public void JsonReviverContextReachesEveryReviverThatCanSeeIt()
    {
        using var engine = new JsEngine();
        // the source text access proposal: V8 of the reference Node has no context, so this is not compared with it
        var r = engine.Evaluate("""
            const seen = [];
            JSON.parse('[1.50]', function (k, v, c) { seen.push(c.source); return v; });
            JSON.parse('[2.50]', function (k, v) { seen.push(arguments[2].source); return v; });
            JSON.parse('[3.50]', function (k, v) { seen.push(eval('arguments[2].source')); return v; });
            JSON.parse('[4.50]', (...a) => { seen.push(a[2].source); return a[1]; });
            JSON.parse('[5.50]', function (k, v, { source }) { seen.push(source); return v; });
            JSON.parse('[6.50]', function (k, v, c = 0) { seen.push(c.source); return v; });
            JSON.parse('[7.50]', function (k, v) { return v; }.bind(null));
            const two = JSON.parse('[8.50,{"a":"x"}]', (k, v) => k === 'a' ? v + '!' : v);
            seen.join() + '|' + JSON.stringify(two) + '|' + JSON.parse('[1]', (k, v) => k === '0' ? undefined : v).length;
            """).AsString();
        Assert.Equal("""1.50,,2.50,,3.50,,4.50,,5.50,,6.50,|[8.5,{"a":"x!"}]|1""", r);
    }

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

    // String builtins (case mapping, replace, split, pad, repeat, radix) and ropes; expected values were produced by Node from the same script.
    private const string StringScript = """
const out = {};
const hash = s => { let h = 7; for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) % 1000003; return [s.length, h]; };
const esc = s => Array.from({ length: s.length }, (_, i) => s.charCodeAt(i).toString(16)).join(' ');
const err = f => { try { return f(); } catch (e) { return e.constructor.name; } };
const rope = (...parts) => parts.reduce((a, b) => a + b, '') ;
const long = (c, n) => { let s = ''; for (let i = 0; i < n; i++) s += c; return s; };
// case mapping: every position relative to the vector width, boundaries of the ASCII letter ranges, non-ASCII in any position
out.caseBoundaries = (() => { const all = Array.from({ length: 128 }, (_, i) => String.fromCharCode(i)).join(''); return [esc(all.toLowerCase()), esc(all.toUpperCase())]; })();
out.caseLengths = (() => { const r = []; for (let n = 0; n < 70; n++) { const s = 'aZ@[`{09'.repeat(9).slice(0, n); r.push(hash(s.toLowerCase())[1], hash(s.toUpperCase())[1]); } return r; })();
out.caseNonAsciiAt = (() => { const r = []; for (let at = 0; at < 40; at += 3) for (const c of ['ß', 'İ', 'ı', 'Σ', 'é', 'É', 'ﬁ', '\u{10428}', '\u{10400}', '\ud801', '\udc00', '\x7f', '\x80', 'ÿ', 'µ']) { const s = 'abcXYZ'.repeat(8).slice(0, at) + c + 'qRs'; r.push(hash(s.toLowerCase())[1], hash(s.toUpperCase())[1]); } return r; })();
out.caseSigma = ['ΑΣ', 'ΑΣ ', 'Σ', 'ΑΣΑ', 'A.Σ', 'AΣ.', 'aΣb', 'ΑΣ\u{10400}'].map(s => s.toLowerCase());
out.caseSpecial = ['straße', 'ŉ', 'ǰ', 'ﬃ', 'ΐ'].map(s => [s.toUpperCase(), s.toLowerCase()]);
out.caseUnchanged = ['', 'abc', 'ABC', '123 -_'].map(s => [s.toLowerCase(), s.toUpperCase(), s.toLowerCase() === s, s.toUpperCase() === s]);
out.caseRope = (() => { const s = long('aB', 500); return [hash(s.toLowerCase()), hash(s.toUpperCase()), hash((s + 'É').toLowerCase()), hash((s + 'é').toUpperCase())]; })();
out.caseThis = [String.prototype.toLowerCase.call(12), String.prototype.toUpperCase.call(true), String.prototype.toLowerCase.call({ toString() { return 'OBJ'; } }), new String('AbC').toLowerCase(), err(() => String.prototype.toUpperCase.call(null)), err(() => String.prototype.toLowerCase.call(undefined)), err(() => String.prototype.toLowerCase.call(Symbol()))];
out.caseLong = (() => { const s = 'The Quick Brown Fox;'.repeat(5000); return [hash(s.toLowerCase()), hash(s.toUpperCase()), s.toUpperCase().toLowerCase() === s.toLowerCase()]; })();
// replace / replaceAll with a literal text
out.replaceBasic = ['aXbXc'.replace('X', 'yy'), 'aXbXc'.replaceAll('X', 'yy'), 'aXbXc'.replaceAll('X', ''), 'XXX'.replaceAll('X', 'X'), 'abc'.replaceAll('z', 'y'), ''.replaceAll('', 'y'), ''.replace('', 'y'), 'abc'.replaceAll('', '-'), 'abc'.replace('', '-'), 'aaa'.replaceAll('aa', 'b'), 'aaaa'.replaceAll('aa', 'b'), 'abcabc'.replaceAll('abc', ''), 'xabc'.replaceAll('abc', 'long replacement'), 'abcx'.replaceAll('abc', 'long replacement')];
out.replaceDollar = ['aXb'.replace('X', '$$'), 'aXb'.replace('X', '$&$&'), 'aXb'.replace('X', '[$`]'), 'aXb'.replace('X', "[$']"), 'aXb'.replace('X', '$1'), 'aXb'.replace('X', '$'), 'aXb'.replace('X', '$<n>'), 'aXbXc'.replaceAll('X', "<$`|$&|$'>"), 'XX'.replaceAll('X', '$$$$')];
out.replaceFn = (() => { const log = []; const r = 'aXbX'.replaceAll('X', function (m, i, s) { log.push([m, i, s, this === undefined || this === globalThis]); return i; }); return [r, log, 'aXb'.replace('X', () => '$&'), 'aXb'.replace('X', () => ({ toString() { return 'obj'; } })), 'aXbXc'.replaceAll('X', () => 'aXbXc'.replaceAll('b', '!'))]; })();
out.replaceFnThrow = (() => { let n = 0; const a = err(() => 'XXXX'.replaceAll('X', () => { if (++n === 2) throw new EvalError('e'); return ''; })); return [a, n, 'XXXX'.replaceAll('X', 'y')]; })();
out.replaceCoerce = (() => { const log = []; const s = { toString() { log.push('this'); return 'a1b1'; } }, q = { toString() { log.push('search'); return '1'; } }, v = { toString() { log.push('value'); return '2'; } }; return [String.prototype.replaceAll.call(s, q, v), log, 'a1b'.replace(1, 2), 'undefined'.replace(undefined, undefined), 'null'.replaceAll(null, null), err(() => String.prototype.replace.call(null, 'a', 'b'))]; })();
out.replaceSymbol = (() => { const o = { [Symbol.replace](s, v) { return ['called', s, v]; } }; const sym = { [Symbol.replace]: null, toString() { return 'b'; } }; return ['abc'.replace(o, 'x'), 'abc'.replaceAll(o, 'x'), 'abc'.replaceAll(sym, 'x'), 'abc'.replace({ [Symbol.replace]: undefined, toString: () => 'c' }, 'x'), err(() => 'a'.replace({ [Symbol.replace]: 1 }, 'x')), err(() => 'a'.replaceAll(/a/, 'x')), 'aa'.replaceAll(/a/g, 'x'), 'aa'.replace(/a/, 'x')]; })();
out.replacePatched = (() => { const orig = String.prototype.replaceAll; String.prototype.replaceAll = function () { return 'patched'; }; try { return ['a'.replaceAll('a', 'b'), orig.call('a', 'a', 'b')]; } finally { String.prototype.replaceAll = orig; } })();
out.replaceSurrogates = [esc('a\u{1F600}b'.replaceAll('\ud83d', 'X')), esc('a\u{1F600}b'.replaceAll('\ude00', 'X')), esc('a\u{1F600}b'.replaceAll('', '-')), esc('\u{1F600}\u{1F600}'.replaceAll('\u{1F600}', '\ud83d'))];
out.replaceMany = (() => { const s = long('ab,', 3000); return [hash(s.replaceAll(',', ';')), hash(s.replaceAll(',', '')), hash(s.replaceAll(',', '<long>')), hash(s.replaceAll('a', '$&$&')), hash(s.replaceAll('', '.')), hash(s.replace(',', ';')), hash(s.replaceAll('zz', 'y')), hash(s.replaceAll(rope('a', 'b'), rope('x', 'y')))]; })();
out.replaceRopeThis = (() => { let s = long('q', 300); const t = s + 'needle' + s; return [hash(t.replaceAll('needle', 'n')), hash(t.replace(t, 'x')), hash('x'.replace('x', t))]; })();
// split
out.split = ['a,b'.split(','), ',a,,b,'.split(','), 'abc'.split(''), 'abc'.split('', 2), 'abc'.split(undefined), 'abc'.split(undefined, 0), ''.split(','), ''.split(''), 'a,b,c'.split(',', 2), 'a,b,c'.split(',', -1).length, 'a--b'.split('--'), 'aaa'.split('aa'), esc('\u{1F600}'.split('')[0]), 'a1b'.split(1), 'anullb'.split(null)];
out.splitSymbol = (() => { const log = []; const o = { [Symbol.split](s, l) { log.push(s, l); return 'called'; } }; const orig = String.prototype[Symbol.split]; return ['ab'.split(o, 7), log, 'a,b'.split({ [Symbol.split]: null, toString() { return ','; } }), err(() => 'a'.split({ [Symbol.split]: 1 })), orig === undefined]; })();
out.splitRope = (() => { const s = long('word ', 400); const parts = s.split(' '); return [parts.length, parts[399], parts[400], hash(parts.join('-'))]; })();
// padStart / padEnd
out.pad = ['a'.padStart(5, 'xy'), 'a'.padEnd(5, 'xy'), 'abc'.padStart(2), 'abc'.padEnd(3), 'abc'.padStart(4, ''), 'a'.padStart(3), 'a'.padEnd(3), 'a'.padStart(8, 'xyz'), 'a'.padEnd(8, 'xyz'), 'a'.padStart(3.9, 'z'), 'a'.padEnd(-1, 'z'), 'a'.padStart(NaN, 'z'), 'a'.padStart('4', 1), ''.padStart(3, 'ab'), ''.padEnd(0, 'ab'), 'a'.padEnd(3, undefined), 'a'.padEnd(3, null), esc('a'.padStart(3, '\u{1F600}')), esc('a'.padEnd(4, '\u{1F600}'))];
out.padCoerce = (() => { const log = []; const r = String.prototype.padStart.call({ toString() { log.push('this'); return 'q'; } }, { valueOf() { log.push('len'); return 3; } }, { toString() { log.push('fill'); return 'z'; } }); return [r, log, err(() => 'a'.padStart(2 ** 31)), err(() => 'a'.padEnd(Infinity)), 'a'.padEnd(Infinity, ''), err(() => String.prototype.padEnd.call(null, 1))]; })();
out.padLong = (() => { const s = long('r', 300); return [hash(s.padStart(1000, 'ab')), hash(s.padEnd(1000, 'abc')), hash('z'.padStart(5000)), hash('z'.padEnd(5000, 'xy'))]; })();
// repeat
out.repeat = ['ab'.repeat(0), 'ab'.repeat(1), 'ab'.repeat(2), 'ab'.repeat(3), 'abc'.repeat(7), 'a'.repeat(5), ''.repeat(1e9), 'a'.repeat(2.9), 'a'.repeat('2'), 'a'.repeat(-0.5), 'a'.repeat(NaN), esc('\u{1F600}'.repeat(2)), err(() => 'a'.repeat(-1)), err(() => 'a'.repeat(Infinity)), err(() => 'a'.repeat(2 ** 31)), err(() => 'ab'.repeat(2 ** 30)), err(() => String.prototype.repeat.call(undefined, 1))];
out.repeatLengths = (() => { const r = []; for (const u of ['x', 'xy', 'xyz', 'abcdefg']) for (const n of [4, 5, 8, 9, 15, 16, 17, 100, 1023, 1024, 1025]) r.push(hash(u.repeat(n))); return r; })();
// toString(radix)
out.radix = (() => { const r = []; for (const v of [0, -0, 1, -1, 7, 255, -255, 65535, 4294967295, 4294967296, -4294967296, 4294967297, 2 ** 40, 2 ** 53 - 1, 2 ** 53, 0.5, -0.5, 0.25, 1.5, 255.5, -255.125, 4294967296.5, NaN, Infinity, -Infinity]) for (const b of [2, 8, 16, 32, 36]) r.push(v.toString(b)); return r; })();
out.radixLong = [(2 ** 53).toString(2).length, (2 ** 64).toString(2), (2 ** 100).toString(2).length, (2 ** 100).toString(16), (2 ** 1023).toString(2).length, (-(2 ** 1023)).toString(2).length, (2 ** 1023).toString(32).length, err(() => (1).toString(1)), err(() => (1).toString(37)), (255).toString(undefined), (255).toString(16.9)];
// ropes: every shape, nodes shared and flattened in the middle
out.ropeShapes = (() => { const n = 400; let left = '', right = ''; for (let i = 0; i < n; i++) { left += 'item' + i + ';'; right = 'item' + i + ';' + right; } let tree = 'x'.repeat(300); for (let i = 0; i < 8; i++) tree = tree + tree; const mixed = (left + right) + (right + left); return [hash(left), hash(right), hash(tree), hash(mixed), hash(left + right), hash(right + left)]; })();
out.ropeShared = (() => { const a = long('a', 300), b = long('b', 300); const ab = a + b, ba = b + a; const c = ab + ba + ab; const before = hash(c); const mid = ab.charAt(299) + ab.charAt(300); const d = c + ab; return [before, mid, hash(c), hash(d), hash(ab + c + ba), c === (ab + ba + ab), c.length]; })();
out.ropeFlattenThenGrow = (() => { let s = long('p', 300); const r = []; for (let i = 0; i < 6; i++) { s += String(i).repeat(100); r.push(s.charCodeAt(s.length - 1)); s = 'q' + s; r.push(s.length, s.charCodeAt(0)); } return [r, hash(s)]; })();
out.ropeSurrogateSeam = (() => { const hi = long('x', 299) + '\ud83d', lo = '\ude00' + long('y', 299); const j = hi + lo; return [j.length, j.codePointAt(299), [...j].length, j.isWellFormed?.(), esc(j.slice(298, 302)), hash(j.toUpperCase()), j.indexOf('\u{1F600}')]; })();
out.ropeDeep = (() => { let a = '', b = ''; for (let i = 0; i < 200000; i++) { a += 'ab'; } for (let i = 0; i < 20000; i++) { b = 'cd' + b; } let c = ''; for (let i = 0; i < 20000; i++) { c = 'e' + c + 'f'; } return [hash(a), hash(b), hash(c)]; })();
JSON.stringify(out);
""";

    private const string StringExpected = """
{"caseBoundaries":["0 1 2 3 4 5 6 7 8 9 a b c d e f 10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f 20 21 22 23 24 25 26 27 28 29 2a 2b 2c 2d 2e 2f 30 31 32 33 34 35 36 37 38 39 3a 3b 3c 3d 3e 3f 40 61 62 63 64 65 66 67 68 69 6a 6b 6c 6d 6e 6f 70 71 72 73 74 75 76 77 78 79 7a 5b 5c 5d 5e 5f 60 61 62 63 64 65 66 67 68 69 6a 6b 6c 6d 6e 6f 70 71 72 73 74 75 76 77 78 79 7a 7b 7c 7d 7e 7f","0 1 2 3 4 5 6 7 8 9 a b c d e f 10 11 12 13 14 15 16 17 18 19 1a 1b 1c 1d 1e 1f 20 21 22 23 24 25 26 27 28 29 2a 2b 2c 2d 2e 2f 30 31 32 33 34 35 36 37 38 39 3a 3b 3c 3d 3e 3f 40 41 42 43 44 45 46 47 48 49 4a 4b 4c 4d 4e 4f 50 51 52 53 54 55 56 57 58 59 5a 5b 5c 5d 5e 5f 60 41 42 43 44 45 46 47 48 49 4a 4b 4c 4d 4e 4f 50 51 52 53 54 55 56 57 58 59 5a 7b 7c 7d 7e 7f"],"caseLengths":[7,7,314,282,9856,8832,305600,273856,473664,489603,683638,177744,192838,510172,978011,815335,318308,275367,867618,536418,896202,629000,782245,499007,249614,469263,738109,547207,881436,963492,324483,868213,59000,914582,829094,352023,701961,912773,760792,295943,584574,174297,121836,403288,777030,502015,87906,562468,725137,436514,479278,531960,857698,490802,588624,214881,247381,661384,668886,502940,735529,591218,801381,327752,842796,160339,126695,970562,927658,87422,757378,710140,478740,14365,840994,445411,70859,807825,196671,42548,96840,319042,2128,890340,66090,600549,48848,617029,514376,127933,945707,966010,316953,946346,825564,336687,592466,437324,366489,557070,361248,269209,198719,345519,160362,711150,971306,45680,110519,416200,426128,902212,209986,968548,509645,24963,799072,773943,771224,992228,907966,759069,146958,531166,555809,466221,230076,452857,132392,38582,104237,196104,231460,79296,175303,458234,434469,205303,468596,364471],"caseNonAsciiAt":[220243,608582,573069,601532,663099,719832,265517,280432,518153,533068,518153,533068,851416,304935,318955,95542,318955,95542,924857,893081,400957,369181,360316,328540,390107,358331,173552,746478,969027,71895,152109,323409,460921,818140,594965,936440,197383,497040,450019,749676,450019,749676,783282,19762,206807,810372,206807,810372,856723,109686,332823,585789,292182,545148,321973,574939,105418,963086,900893,288503,611048,978166,687988,549,53901,118849,656322,679452,908958,932088,908958,932088,242218,674519,433874,465126,433874,465126,315659,292098,791762,768201,751121,727560,780912,757351,564357,145495,359829,470915,588938,567323,2578,987299,31791,105596,634212,666199,886848,918835,886848,918835,220108,263676,748467,54283,748467,54283,293549,278845,769652,754948,729011,714307,758802,744098,542247,132242,337719,457662,144745,398339,232637,401201,587601,519501,190019,80101,442655,332737,442655,332737,775918,94692,978526,885302,978526,885302,849359,692750,325459,168850,284818,128209,314609,158000,98054,546147,893529,871567,997941,992988,681635,775223,440794,893523,43212,454123,295848,706759,295848,706759,629111,689341,427521,479948,427521,479948,702552,66769,178652,542872,138011,502231,167802,532022,951250,920169,746722,245586,716566,346311,959037,464039,159419,582339,761840,142939,14473,395575,14473,395575,347736,42664,704923,833274,704923,833274,421177,755588,897280,231688,856639,191047,886430,220838,669875,608985,465347,934405,66244,31487,799115,776465,509100,894765,111518,455365,364154,708001,364154,708001,697417,727843,545001,518450,545001,518450,770858,68011,246958,544114,206317,503473,236108,533264,19553,921411,815028,246828,614507,355952,795217,464350,57360,582650,659781,143250,912417,395886,912417,395886,245677,52305,541103,842915,541103,842915,319118,755899,795221,231999,754580,191358,784371,221149,567816,609296,363288,934716,635698,245657,452135,41436,78551,159736,680972,720339,933608,972975,933608,972975,266868,942013,198021,732620,198021,732620,340309,332985,816412,809088,775771,768447,805562,798238,589007,186382,384479,511802,167726,675282,945048,281102,610582,399402,213000,960005,465636,212638,465636,212638,798899,371635,690934,162242,690934,162242,872340,572651,348440,48751,307799,8110,337590,37901,121035,426048,916510,751468,622857,377148,54064,916648,65710,34945,668131,595548,920767,848184,920767,848184,254027,73501,799953,864111,799953,864111,327468,208194,803571,684297,762930,643656,792721,673447,576166,61591,371638,387011,622644,911912,47461,643575,65497,761875,667918,322475,920554,575111,920554,575111,253814,608265,793350,398872,793350,398872,327255,935124,803358,411224,762717,370583,792508,400374,575953,788521,371425,113938,44336,800331,119967,317394,487192,435694,89610,996297,342246,248930,342246,248930,675509,496684,865856,287291,865856,287291,748950,608943,225050,85043,184409,44402,214200,74193,997648,462340,793120,787760],"caseSigma":["ας","ας ","σ","ασα","a.ς","aς.","aσb","ασ𐐨"],"caseSpecial":[["STRASSE","straße"],["ʼN","ŉ"],["J̌","ǰ"],["FFI","ﬃ"],["Ϊ́","ΐ"]],"caseUnchanged":[["","",true,true],["abc","ABC",true,false],["abc","ABC",false,true],["123 -_","123 -_",true,true]],"caseRope":[[1000,496891],[1000,689074],[1001,403809],[1001,361432]],"caseThis":["12","TRUE","obj","abc","TypeError","TypeError","TypeError"],"caseLong":[[100000,530592],[100000,780634],true],"replaceBasic":["ayybXc","ayybyyc","abc","XXX","abc","y","y","-a-b-c-","-abc","ba","bb","","xlong replacement","long replacementx"],"replaceDollar":["a$b","aXXb","a[a]b","a[b]b","a$1b","a$b","a$<n>b","a<a|X|bXc>b<aXb|X|c>c","$$$$"],"replaceFn":["a1b3",[["X",1,"aXbX",true],["X",3,"aXbX",true]],"a$&b","aobjb","aaX!XcbaX!Xcc"],"replaceFnThrow":["EvalError",2,"yyyy"],"replaceCoerce":["a2b2",["this","search","value"],"a2b","undefined","null","TypeError"],"replaceSymbol":[["called","abc","x"],["called","abc","x"],"axc","abx","TypeError","TypeError","xx","xa"],"replacePatched":["patched","b"],"replaceSurrogates":["61 58 de00 62","61 d83d 58 62","2d 61 2d d83d 2d de00 2d 62 2d","d83d d83d"],"replaceMany":[[9000,64897],[6000,412184],[24000,405405],[12000,937509],[18001,481880],[9000,968366],[9000,229116],[9000,975154]],"replaceRopeThis":[[601,573574],[1,337],[606,846614]],"split":[["a","b"],["","a","","b",""],["a","b","c"],["a","b"],["abc"],[],[""],[],["a","b"],3,["a","b"],["","a"],"d83d",["a","b"],["a","b"]],"splitSymbol":["called",["ab",7],["a","b"],"TypeError",true],"splitRope":[401,"word","",[2000,606903]],"pad":["xyxya","axyxy","abc","abc","abc","  a","a  ","xyzxyzxa","axyzxyzx","zza","a","a","111a","aba","","a  ","anu","d83d de00 61","61 d83d de00 d83d"],"padCoerce":["zzq",["this","len","fill"],"RangeError","RangeError","a","TypeError"],"padLong":[[1000,762542],[1000,213471],[5000,908520],[5000,28835]],"repeat":["","ab","abab","ababab","abcabcabcabcabcabcabc","aaaaa","","aa","aa","","","d83d de00 d83d de00","RangeError","RangeError","RangeError","RangeError","TypeError"],"repeatLengths":[[4,158697],[5,919715],[8,266528],[9,262464],[15,322853],[16,8533],[17,264643],[100,871397],[1023,39199],[1024,215286],[1025,673968],[8,692028],[10,40754],[16,436376],[18,359920],[30,259487],[32,370101],[34,669837],[200,474715],[2046,688655],[2048,799313],[2050,141330],[12,464873],[15,109189],[24,660591],[27,726637],[45,691119],[48,183555],[51,389794],[300,196903],[3069,56988],[3072,843610],[3075,29307],[28,843127],[35,645372],[56,477085],[63,452956],[105,113098],[112,643889],[119,23249],[700,294430],[7161,226950],[7168,339226],[7175,274877]],"radix":["0","0","0","0","0","0","0","0","0","0","1","1","1","1","1","-1","-1","-1","-1","-1","111","7","7","7","7","11111111","377","ff","7v","73","-11111111","-377","-ff","-7v","-73","1111111111111111","177777","ffff","1vvv","1ekf","11111111111111111111111111111111","37777777777","ffffffff","3vvvvvv","1z141z3","100000000000000000000000000000000","40000000000","100000000","4000000","1z141z4","-100000000000000000000000000000000","-40000000000","-100000000","-4000000","-1z141z4","100000000000000000000000000000001","40000000001","100000001","4000001","1z141z5","10000000000000000000000000000000000000000","20000000000000","10000000000","100000000","e13wu1og","11111111111111111111111111111111111111111111111111111","377777777777777777","1fffffffffffff","7vvvvvvvvvv","2gosa7pa2gv","100000000000000000000000000000000000000000000000000000","400000000000000000","20000000000000","80000000000","2gosa7pa2gw","0.1","0.4","0.8","0.g","0.i","-0.1","-0.4","-0.8","-0.g","-0.i","0.01","0.2","0.4","0.8","0.9","1.1","1.4","1.8","1.g","1.i","11111111.1","377.4","ff.8","7v.g","73.i","-11111111.001","-377.1","-ff.2","-7v.4","-73.4i","100000000000000000000000000000000.1","40000000000.4","100000000.8","4000000.g","1z141z4.i","NaN","NaN","NaN","NaN","NaN","Infinity","Infinity","Infinity","Infinity","Infinity","-Infinity","-Infinity","-Infinity","-Infinity","-Infinity"],"radixLong":[54,"10000000000000000000000000000000000000000000000000000000000000000",101,"10000000000000000000000000",1024,1025,205,"RangeError","RangeError","255","ff"],"ropeShapes":[[3090,544297],[3090,178493],[76800,63119],[12360,725961],[6180,188861],[6180,676827]],"ropeShared":[[1800,603852],"ab",[1800,603852],[2400,744896],[3000,936917],true,1800],"ropeFlattenThenGrow":[[48,401,113,49,502,113,50,603,113,51,704,113,52,805,113,53,906,113],[906,912678]],"ropeSurrogateSeam":[600,128512,599,true,"78 d83d de00 79",[600,232584],299],"ropeDeep":[[400000,696585],[40000,375384],[40000,339157]]}
""";

    [Fact]
    public void StringFastPathsMatchReferenceBehavior()
    {
        using var engine = new JsEngine();
        var exp = System.Text.Json.JsonDocument.Parse(StringExpected).RootElement;
        var got = System.Text.Json.JsonDocument.Parse(engine.Evaluate(StringScript).AsString()).RootElement;
        foreach (var p in exp.EnumerateObject())
        {
            Assert.True(got.TryGetProperty(p.Name, out var g), $"missing case {p.Name}");
            Assert.True(p.Value.GetRawText() == g.GetRawText(), $"case {p.Name}: expected {p.Value.GetRawText()}, got {g.GetRawText()}");
        }
    }

    [Theory]
    [InlineData("const s = 'ab'.repeat(1 << 20); let t = []; while (true) t.push(s.toUpperCase());")]
    [InlineData("const s = 'ab,'.repeat(1 << 18); let t = []; while (true) t.push(s.replaceAll(',', ';'));")]
    [InlineData("let t = []; for (let i = 0; ; i++) t.push('x'.padStart(1 << 20, 'ab' + i));")]
    [InlineData("let t = []; for (let i = 0; ; i++) t.push(('ab' + i).repeat(1 << 18));")]
    public void StringBuiltinOutputCountsAgainstTheBudget(string script)
    {
        using var engine = new JsEngine(new JsEngineOptions { Limits = new JsLimits { MaxTrackedBytes = 16 * 1024 * 1024 } });
        Assert.Throws<JsLimitExceededException>(() => engine.Evaluate(script));
    }

    // Intl.NumberFormat integers and Intl.PluralRules selection without ICU's number formatter; expected values were produced by Node from the same script.
    private const string IntlScript = """
const out = {};
const vals = [0, -0, 1, -1, 12, 999, 1000, -1000, 1234, 9999, 10000, 12345, 123456, 1234567, -1234567, 123456789, 1e9, 999999999999999, -999999999999999, 1e15, 2 ** 53, 1.5, -1.5, NaN, Infinity];
for (const l of ['en-US', 'de-DE', 'en-IN', 'es', 'pl', 'fr', 'sv', 'pt-PT', 'ja', 'hi', 'ar-EG', 'th-u-nu-thai']) {
  // ar-EG's name for NaN differs between CLDR versions, so it would pin this test to one ICU.
  out['int:' + l] = (l === 'ar-EG' ? vals.filter(v => v === v) : vals).map(v => new Intl.NumberFormat(l).format(v));
  out['tls:' + l] = [1234567, -1234, 0].map(v => v.toLocaleString(l));
}
out.intOptions = [{ useGrouping: false }, { useGrouping: 'always' }, { useGrouping: 'min2' }, { maximumFractionDigits: 0 }, { minimumFractionDigits: 2 }, { minimumIntegerDigits: 3 }, { maximumSignificantDigits: 2 }, { signDisplay: 'always' }, { signDisplay: 'never' }, { roundingMode: 'floor' }, { notation: 'scientific' }, { style: 'percent' }, { style: 'currency', currency: 'USD' }, { trailingZeroDisplay: 'stripIfInteger' }, { roundingIncrement: 5, maximumFractionDigits: 0 }].map(o => [1, -1, 1234, 12345, 1234567].map(v => new Intl.NumberFormat('en-US', o).format(v)));
out.partsAgree = (() => { const bad = []; for (const l of ['en-US', 'de-DE', 'en-IN', 'es', 'ar-EG', 'th-u-nu-thai', 'fr']) { const f = new Intl.NumberFormat(l); for (const v of vals) if (f.format(v) !== f.formatToParts(v).map(p => p.value).join('')) bad.push([l, v]); } return bad; })();
const ints = [0, 1, 2, 3, 4, 5, 10, 11, 12, 13, 20, 21, 22, 100, 101, 102, 111, 1000, 1023, 1024, 1025, 1000000, 2000000, 2147483647, 2147483648, -1, -0, 1.5, 0.5, NaN, Infinity];
for (const l of ['en', 'ru', 'pl', 'ar', 'fr', 'ja', 'cy', 'he']) {
  const p = new Intl.PluralRules(l), o = new Intl.PluralRules(l, { type: 'ordinal' });
  out['plural:' + l] = [ints.map(v => p.select(v)), ints.map(v => p.select(v)), ints.map(v => o.select(v))];
}
out.pluralOptions = [{ minimumFractionDigits: 1 }, { maximumFractionDigits: 0 }, { minimumIntegerDigits: 2 }, { maximumSignificantDigits: 1 }, { notation: 'compact' }, { roundingIncrement: 5, maximumFractionDigits: 0 }].map(o => { const p = new Intl.PluralRules('en', o); return [0, 1, 2, 1000, 1000000, 1.5].map(v => p.select(v)); });
out.pluralCoerce = (() => { const p = new Intl.PluralRules('en'); return [p.select('1'), p.select({ valueOf() { return 1; } }), p.select(true), p.select(null), p.select(undefined), (() => { try { p.select(1n); return 'no'; } catch (e) { return e.constructor.name; } })(), (() => { try { p.select(Symbol()); return 'no'; } catch (e) { return e.constructor.name; } })()]; })();
out.patched = (() => { const orig = Intl.NumberFormat.prototype.formatToParts; const f = new Intl.NumberFormat('en-US'); const a = f.format(1234); Intl.NumberFormat.prototype.formatToParts = () => { throw new Error('x'); }; try { return [a, f.format(1234), (1234).toLocaleString('en-US')]; } finally { Intl.NumberFormat.prototype.formatToParts = orig; } })();
JSON.stringify(out);
""";

    private const string IntlExpected = """
{"int:en-US":["0","-0","1","-1","12","999","1,000","-1,000","1,234","9,999","10,000","12,345","123,456","1,234,567","-1,234,567","123,456,789","1,000,000,000","999,999,999,999,999","-999,999,999,999,999","1,000,000,000,000,000","9,007,199,254,740,992","1.5","-1.5","NaN","∞"],"tls:en-US":["1,234,567","-1,234","0"],"int:de-DE":["0","-0","1","-1","12","999","1.000","-1.000","1.234","9.999","10.000","12.345","123.456","1.234.567","-1.234.567","123.456.789","1.000.000.000","999.999.999.999.999","-999.999.999.999.999","1.000.000.000.000.000","9.007.199.254.740.992","1,5","-1,5","NaN","∞"],"tls:de-DE":["1.234.567","-1.234","0"],"int:en-IN":["0","-0","1","-1","12","999","1,000","-1,000","1,234","9,999","10,000","12,345","1,23,456","12,34,567","-12,34,567","12,34,56,789","1,00,00,00,000","99,99,99,99,99,99,999","-99,99,99,99,99,99,999","1,00,00,00,00,00,00,000","9,00,71,99,25,47,40,992","1.5","-1.5","NaN","∞"],"tls:en-IN":["12,34,567","-1,234","0"],"int:es":["0","-0","1","-1","12","999","1000","-1000","1234","9999","10.000","12.345","123.456","1.234.567","-1.234.567","123.456.789","1.000.000.000","999.999.999.999.999","-999.999.999.999.999","1.000.000.000.000.000","9.007.199.254.740.992","1,5","-1,5","NaN","∞"],"tls:es":["1.234.567","-1234","0"],"int:pl":["0","-0","1","-1","12","999","1000","-1000","1234","9999","10 000","12 345","123 456","1 234 567","-1 234 567","123 456 789","1 000 000 000","999 999 999 999 999","-999 999 999 999 999","1 000 000 000 000 000","9 007 199 254 740 992","1,5","-1,5","NaN","∞"],"tls:pl":["1 234 567","-1234","0"],"int:fr":["0","-0","1","-1","12","999","1 000","-1 000","1 234","9 999","10 000","12 345","123 456","1 234 567","-1 234 567","123 456 789","1 000 000 000","999 999 999 999 999","-999 999 999 999 999","1 000 000 000 000 000","9 007 199 254 740 992","1,5","-1,5","NaN","∞"],"tls:fr":["1 234 567","-1 234","0"],"int:sv":["0","−0","1","−1","12","999","1 000","−1 000","1 234","9 999","10 000","12 345","123 456","1 234 567","−1 234 567","123 456 789","1 000 000 000","999 999 999 999 999","−999 999 999 999 999","1 000 000 000 000 000","9 007 199 254 740 992","1,5","−1,5","NaN","∞"],"tls:sv":["1 234 567","−1 234","0"],"int:pt-PT":["0","-0","1","-1","12","999","1000","-1000","1234","9999","10 000","12 345","123 456","1 234 567","-1 234 567","123 456 789","1 000 000 000","999 999 999 999 999","-999 999 999 999 999","1 000 000 000 000 000","9 007 199 254 740 992","1,5","-1,5","NaN","∞"],"tls:pt-PT":["1 234 567","-1234","0"],"int:ja":["0","-0","1","-1","12","999","1,000","-1,000","1,234","9,999","10,000","12,345","123,456","1,234,567","-1,234,567","123,456,789","1,000,000,000","999,999,999,999,999","-999,999,999,999,999","1,000,000,000,000,000","9,007,199,254,740,992","1.5","-1.5","NaN","∞"],"tls:ja":["1,234,567","-1,234","0"],"int:hi":["0","-0","1","-1","12","999","1,000","-1,000","1,234","9,999","10,000","12,345","1,23,456","12,34,567","-12,34,567","12,34,56,789","1,00,00,00,000","99,99,99,99,99,99,999","-99,99,99,99,99,99,999","1,00,00,00,00,00,00,000","9,00,71,99,25,47,40,992","1.5","-1.5","NaN","∞"],"tls:hi":["12,34,567","-1,234","0"],"int:ar-EG":["٠","؜-٠","١","؜-١","١٢","٩٩٩","١٬٠٠٠","؜-١٬٠٠٠","١٬٢٣٤","٩٬٩٩٩","١٠٬٠٠٠","١٢٬٣٤٥","١٢٣٬٤٥٦","١٬٢٣٤٬٥٦٧","؜-١٬٢٣٤٬٥٦٧","١٢٣٬٤٥٦٬٧٨٩","١٬٠٠٠٬٠٠٠٬٠٠٠","٩٩٩٬٩٩٩٬٩٩٩٬٩٩٩٬٩٩٩","؜-٩٩٩٬٩٩٩٬٩٩٩٬٩٩٩٬٩٩٩","١٬٠٠٠٬٠٠٠٬٠٠٠٬٠٠٠٬٠٠٠","٩٬٠٠٧٬١٩٩٬٢٥٤٬٧٤٠٬٩٩٢","١٫٥","؜-١٫٥","∞"],"tls:ar-EG":["١٬٢٣٤٬٥٦٧","؜-١٬٢٣٤","٠"],"int:th-u-nu-thai":["๐","-๐","๑","-๑","๑๒","๙๙๙","๑,๐๐๐","-๑,๐๐๐","๑,๒๓๔","๙,๙๙๙","๑๐,๐๐๐","๑๒,๓๔๕","๑๒๓,๔๕๖","๑,๒๓๔,๕๖๗","-๑,๒๓๔,๕๖๗","๑๒๓,๔๕๖,๗๘๙","๑,๐๐๐,๐๐๐,๐๐๐","๙๙๙,๙๙๙,๙๙๙,๙๙๙,๙๙๙","-๙๙๙,๙๙๙,๙๙๙,๙๙๙,๙๙๙","๑,๐๐๐,๐๐๐,๐๐๐,๐๐๐,๐๐๐","๙,๐๐๗,๑๙๙,๒๕๔,๗๔๐,๙๙๒","๑.๕","-๑.๕","NaN","∞"],"tls:th-u-nu-thai":["๑,๒๓๔,๕๖๗","-๑,๒๓๔","๐"],"intOptions":[["1","-1","1234","12345","1234567"],["1","-1","1,234","12,345","1,234,567"],["1","-1","1234","12,345","1,234,567"],["1","-1","1,234","12,345","1,234,567"],["1.00","-1.00","1,234.00","12,345.00","1,234,567.00"],["001","-001","1,234","12,345","1,234,567"],["1","-1","1,200","12,000","1,200,000"],["+1","-1","+1,234","+12,345","+1,234,567"],["1","1","1,234","12,345","1,234,567"],["1","-1","1,234","12,345","1,234,567"],["1E0","-1E0","1.234E3","1.235E4","1.235E6"],["100%","-100%","123,400%","1,234,500%","123,456,700%"],["$1.00","-$1.00","$1,234.00","$12,345.00","$1,234,567.00"],["1","-1","1,234","12,345","1,234,567"],["0","-0","1,235","12,345","1,234,565"]],"partsAgree":[],"plural:en":[["other","one","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","one","other","other","other","other","other"],["other","one","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","one","other","other","other","other","other"],["other","one","two","few","other","other","other","other","other","other","other","one","two","other","one","two","other","other","few","other","other","other","other","other","other","one","other","other","other","other","other"]],"plural:ru":[["many","one","few","few","few","many","many","many","many","many","many","one","few","many","one","few","many","many","few","few","many","many","many","many","many","one","many","other","other","other","other"],["many","one","few","few","few","many","many","many","many","many","many","one","few","many","one","few","many","many","few","few","many","many","many","many","many","one","many","other","other","other","other"],["other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other"]],"plural:pl":[["many","one","few","few","few","many","many","many","many","many","many","many","few","many","many","few","many","many","few","few","many","many","many","many","many","one","many","other","other","other","other"],["many","one","few","few","few","many","many","many","many","many","many","many","few","many","many","few","many","many","few","few","many","many","many","many","many","one","many","other","other","other","other"],["other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other"]],"plural:ar":[["zero","one","two","few","few","few","few","many","many","many","many","many","many","other","other","other","many","other","many","many","many","other","other","many","many","one","zero","other","other","other","other"],["zero","one","two","few","few","few","few","many","many","many","many","many","many","other","other","other","many","other","many","many","many","other","other","many","many","one","zero","other","other","other","other"],["other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other"]],"plural:fr":[["one","one","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","many","many","other","other","one","one","one","one","other","other"],["one","one","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","many","many","other","other","one","one","one","one","other","other"],["other","one","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","one","other","other","other","other","other"]],"plural:ja":[["other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other"],["other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other"],["other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other"]],"plural:cy":[["zero","one","two","few","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","one","zero","other","other","other","other"],["zero","one","two","few","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","one","zero","other","other","other","other"],["zero","one","two","few","few","many","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","one","zero","other","other","other","other"]],"plural:he":[["other","one","two","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","one","other","other","one","other","other"],["other","one","two","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","one","other","other","one","other","other"],["other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other","other"]],"pluralOptions":[["other","other","other","other","other","other"],["other","one","other","other","other","other"],["other","one","other","other","other","other"],["other","one","other","other","other","other"],["other","one","other","other","other","other"],["other","other","other","other","other","other"]],"pluralCoerce":["one","one","one","other","other","TypeError","TypeError"],"patched":["1,234","1,234","1,234"]}
""";

    [Fact]
    public void IntlIntegerFastPathsMatchReferenceBehavior()
    {
        if (JsEngine.IcuVersion == null) return;
        using var engine = new JsEngine();
        var exp = System.Text.Json.JsonDocument.Parse(IntlExpected).RootElement;
        var got = System.Text.Json.JsonDocument.Parse(engine.Evaluate(IntlScript).AsString()).RootElement;
        foreach (var p in exp.EnumerateObject())
        {
            Assert.True(got.TryGetProperty(p.Name, out var g), $"missing case {p.Name}");
            Assert.True(p.Value.GetRawText() == g.GetRawText(), $"case {p.Name}: expected {p.Value.GetRawText()}, got {g.GetRawText()}");
        }
    }

    [Fact]
    public void ShiftQueueIsLinear()
    {
        using var engine = new JsEngine();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var n = engine.Evaluate("const q = []; for (let i = 0; i < 300000; i++) q.push(i); let t = 0; while (q.length) { t += q.shift(); if (t % 3 === 0 && q.length > 100000) q.push(1); } t").AsNumber();
        Assert.True(n > 0);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"took {sw.ElapsedMilliseconds} ms (quadratic shift?)");
    }

    [Fact]
    public void MapIteratorsAreCheapToCreate()
    {
        using var engine = new JsEngine();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        engine.Evaluate("const m = new Map([[1, 1]]); let n = 0; for (let i = 0; i < 200000; i++) n += m.keys().next().value; n");
        Assert.True(sw.ElapsedMilliseconds < 3000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void LazyClosurePrototypeCountsAgainstTheBudgetWhenCreated()
    {
        using var engine = new JsEngine(new JsEngineOptions { Limits = new JsLimits { MaxTrackedBytes = 4 * 1024 * 1024 } });
        Assert.Throws<JsLimitExceededException>(() => engine.Evaluate("const a = []; while (true) { const f = function () {}; f.prototype; a.push(f); }"));
    }

    // Literal int32 right operands in the instruction (AddI, SubI, the compare-and-jumps), and return of a conditional without a
    // common Ret: every kind of left operand, and every way out of a function. Expected values were produced by Node from the same script.
    private const string LiteralOperandScript = """
const out = {};
const err = f => { try { return f(); } catch (e) { return e.constructor.name; } };
const show = x => typeof x === 'bigint' ? x + 'n' : Object.is(x, -0) ? '-0' : x === undefined ? 'undef' : x !== x ? 'nan' : x === Infinity ? 'inf' : x === -Infinity ? '-inf' : x;
// literal int32 right operands on every kind of left operand
const lefts = [0, -0, 1, 1.5, -2, NaN, Infinity, -Infinity, 2147483647, -2147483648, 1e300, '', '5', 'abc', ' 7 ', true, false, null, undefined, [], [3], {}];
out.add = lefts.map(x => show(x + 1));
out.addZero = lefts.map(x => show(x + 0));
out.sub = lefts.map(x => show(x - 1));
out.subZero = lefts.map(x => show(x - 0));
out.addBig = lefts.map(x => show(x + 2147483647));
out.subNeg = lefts.map(x => show(x - 2147483647));
out.notInt32 = lefts.map(x => [show(x + 2147483648), show(x - 4294967296), show(x + 0.5)]);
out.lt = lefts.map(x => { if (x < 2) return 1; return 0; });
out.le = lefts.map(x => { if (x <= 1) return 1; return 0; });
out.gt = lefts.map(x => { if (x > 0) return 1; return 0; });
out.ge = lefts.map(x => { if (x >= 0) return 1; return 0; });
out.whileCond = lefts.map(x => { let n = 0; while (x < 2 && n < 3) n++; return n; });
out.ternary = lefts.map(x => x >= 2 ? 'y' : 'n');
out.andOr = lefts.map(x => (x > 0 && x < 10) || x <= -1 ? 1 : 0);
out.notCond = lefts.map(x => !(x < 2) ? 1 : 0);
// BigInt and Symbol on the left
out.bigint = [err(() => 1n + 1), err(() => 1n - 1), (() => { if (5n > 2) return 'gt'; return 'le'; })(), (() => { if (5n < 2) return 'lt'; return 'ge'; })(), (() => { if (2n <= 2) return 'le'; return 'gt'; })(), (() => { if (2n >= 3) return 'ge'; return 'lt'; })()];
out.symbol = [err(() => Symbol() + 1), err(() => Symbol() - 1), err(() => { if (Symbol() < 1) return 1; return 0; }), err(() => { if (Symbol() >= 1) return 1; return 0; })];
// conversions: hints, how many times, in which order
out.hints = (() => { const log = []; const o = { [Symbol.toPrimitive](h) { log.push(h); return 3; } }; const r = [o + 1, o - 1]; if (o < 4) r.push('lt'); if (o <= 2) r.push('le'); if (o > 4) r.push('gt'); if (o >= 3) r.push('ge'); return [r, log]; })();
out.valueOfOrder = (() => { const log = []; const o = n => ({ valueOf() { log.push('v' + n); return n; }, toString() { log.push('s' + n); return 'x'; } }); const r = [o(1) + 1, o(2) - 1, `${o(3)}`]; return [r, log]; })();
out.throwingValueOf = [err(() => ({ valueOf() { throw new RangeError('r'); } }) + 1), err(() => ({ valueOf() { throw new EvalError('e'); } }) - 1), err(() => { if ({ valueOf() { throw new URIError('u'); } } < 1) return 1; return 0; })];
out.leftEffects = (() => { let i = 1; const a = (i = i * 5) + 1; const b = i++ - 1; let c = 0; if (i++ < 7) c = i; return [a, b, c, i]; })();
out.dateAdd = (() => { const d = new Date(0); return [typeof (d + 1), typeof (d - 1), d - 1]; })();
out.stringConcat = (() => { let s = 'a'; for (let i = 0; i < 3; i++) s = s + 1; let t = 'b'; t += 2; return [s, t]; })();
// in loops: literal operands no longer take a register, others still do
out.loops = (() => { let a = 0, b = 100, c = 0; for (let i = 0; i < 5; i = i + 1) { a = a + 2; b = b - 3; c += i * 2 + 0.5; if (a > 6) c = c - 1; } let n = 0; for (let i = 10; i >= 0; i = i - 2) for (let j = 0; j <= 2; j = j + 1) n = n + 1; return [a, b, c, n]; })();
out.sameRegister = (() => { let x = 1; x = x + 1; let y = x - 1; x = y + x; const f = () => x; x = x + 1; return [x, y, f()]; })();
out.captured = (() => { let k = 5; const inc = () => { k = k + 1; return k - 1; }; const r = [inc(), inc()]; if (k > 6) r.push('big'); return [r, k]; })();
out.tdz = err(() => { const r = q + 1; let q = 1; return r; });
out.undeclared = [err(() => nope + 1), err(() => { if (nope < 1) return 1; return 0; })];
out.global = (() => { globalThis.gv = '4'; const r = [gv - 1, gv + 1]; if (gv > 3) r.push('gt'); delete globalThis.gv; return r; })();
// return with a conditional: on every way out of a function
const pick = c => { return c ? 'a' : 'b'; };
function nested(x) { return x < 0 ? 'neg' : x === 0 ? 'zero' : x < 10 ? (x % 2 ? 'odd' : 'even') : 'big'; }
out.condReturn = [pick(1), pick(0), [-1, 0, 3, 4, 11].map(nested), (function () { return this ? typeof this : 'none'; }).call(5), (function () { 'use strict'; return this ? typeof this : 'none'; }).call(0)];
out.condReturnFinally = (() => { const log = []; const f = c => { try { return c ? (log.push('a'), 1) : (log.push('b'), 2); } finally { log.push('f'); } }; const g = c => { try { return c ? 1 : 2; } finally { if (c) return 9; } }; const h = c => { for (;;) { try { return c ? 1 : 2; } finally { break; } } return 3; }; return [f(1), f(0), g(1), g(0), h(1), log]; })();
out.condReturnThrowInArm = (() => { const f = c => { try { return c ? null.x : 1; } catch (e) { return e.constructor.name; } finally { } }; return [f(1), f(0)]; })();
out.condReturnIterClose = (() => { const log = []; const it = { [Symbol.iterator]() { return { next() { return { value: 1, done: false }; }, return() { log.push('close'); return {}; } }; } }; const f = c => { for (const x of it) return c ? x + 1 : x - 1; }; return [f(1), f(0), log]; })();
out.condReturnBlockEnv = (() => { const f = c => { { let y = c ? 1 : 2; const g = () => y; return y > 1 ? g() : g() + 10; } }; const h = c => { for (let i = 0; i < 3; i++) { const g = () => i; if (i === 1) return c ? g : g(); } }; return [f(1), f(0), h(1)(), h(0)]; })();
out.condReturnCtor = (() => { function A(c) { this.v = 1; return c ? { v: 2 } : 3; } class B { constructor(c) { this.v = 4; return c ? { v: 5 } : undefined; } } class C extends B { constructor(c, r) { super(0); return c ? r : undefined; } } return [new A(1).v, new A(0).v, new B(1).v, new B(0).v, new C(0).v, new C(1, { v: 6 }).v, err(() => new C(1, 7)), new C(1, undefined).v]; })();
out.condReturnGenerator = (() => { function* g(c) { return c ? yield 1 : yield 2; } const a = g(1), b = g(0); const r = [a.next(), a.next('x'), a.next(), b.next(), b.next('y')]; function* h(c) { try { return c ? 1 : 2; } finally { yield 'f'; } } const i = h(1); return [r, i.next(), i.next(), i.next()]; })();
out.condReturnArguments = (() => { function f(a) { return arguments.length > 1 ? arguments[1] : a; } function g(a) { a = a + 1; return arguments.length ? arguments[0] : a; } function h(a) { 'use strict'; a = a + 1; return arguments.length > 0 ? arguments[0] : a; } return [f(1), f(1, 2), g(1), g(), h(1)]; })();
// frames: recursion through the literal forms, tail position in strict and sloppy code, throws across frames
function fib(n) { return n < 2 ? n : fib(n - 1) + fib(n - 2); }
function depth(n) { return n <= 0 ? 0 : 1 + depth(n - 1); }
function tail(n, acc) { 'use strict'; return n <= 0 ? acc : tail(n - 1, acc + 1); }
function thrower(n) { if (n < 1) throw new TypeError('at ' + n); return thrower(n - 1) + 1; }
function catcher(n) { try { return n > 0 ? catcher(n - 1) + 1 : thrower(3); } catch (e) { return n > 2 ? e.message + ':' + n : (() => { throw e; })(); } }
function missing(a, b, c) { return c === undefined ? (b === undefined ? a - 1 : b + 1) : c - 1; }
out.frames = [fib(15), depth(2000), tail(2000, 0), err(() => thrower(50)), catcher(5), missing(1), missing(1, 2), missing(1, 2, 3), missing(1, 2, 3, 4)];
out.gen = (() => { function* count(n) { while (n > 0) { yield n; n = n - 1; } return n - 1; } const r = []; const g = count(2); for (let x = g.next(); ; x = g.next()) { r.push(x.value); if (x.done) break; } return r; })();
out.methodThis = (() => { const o = { n: 3, down() { return this.n <= 0 ? 'done' : (this.n = this.n - 1, this.down()); } }; return [o.down(), o.n]; })();
out.valueOfThrowInFrame = (() => { const o = { valueOf() { return thrower(1); } }; const f = () => { try { return o + 1; } catch (e) { return e.message; } }; const g = () => { try { if (o < 1) return 'lt'; return 'ge'; } catch (e) { return e.message; } }; return [f(), g()]; })();
out.generatorInValueOf = (() => { function* g() { const a = yield 1; return a + 1; } const it = g(); const o = { valueOf() { return it.next().value; } }; return [o + 1, o - 1]; })();
JSON.stringify(out);
""";

    private const string LiteralOperandExpected = """
{"add":[1,1,2,2.5,-1,"nan","inf","-inf",2147483648,-2147483647,1e+300,"1","51","abc1"," 7 1",2,1,1,"nan","1","31","[object Object]1"],"addZero":[0,0,1,1.5,-2,"nan","inf","-inf",2147483647,-2147483648,1e+300,"0","50","abc0"," 7 0",1,0,0,"nan","0","30","[object Object]0"],"sub":[-1,-1,0,0.5,-3,"nan","inf","-inf",2147483646,-2147483649,1e+300,-1,4,"nan",6,0,-1,-1,"nan",-1,2,"nan"],"subZero":[0,"-0",1,1.5,-2,"nan","inf","-inf",2147483647,-2147483648,1e+300,0,5,"nan",7,1,0,0,"nan",0,3,"nan"],"addBig":[2147483647,2147483647,2147483648,2147483648.5,2147483645,"nan","inf","-inf",4294967294,-1,1e+300,"2147483647","52147483647","abc2147483647"," 7 2147483647",2147483648,2147483647,2147483647,"nan","2147483647","32147483647","[object Object]2147483647"],"subNeg":[-2147483647,-2147483647,-2147483646,-2147483645.5,-2147483649,"nan","inf","-inf",0,-4294967295,1e+300,-2147483647,-2147483642,"nan",-2147483640,-2147483646,-2147483647,-2147483647,"nan",-2147483647,-2147483644,"nan"],"notInt32":[[2147483648,-4294967296,0.5],[2147483648,-4294967296,0.5],[2147483649,-4294967295,1.5],[2147483649.5,-4294967294.5,2],[2147483646,-4294967298,-1.5],["nan","nan","nan"],["inf","inf","inf"],["-inf","-inf","-inf"],[4294967295,-2147483649,2147483647.5],[0,-6442450944,-2147483647.5],[1e+300,1e+300,1e+300],["2147483648",-4294967296,"0.5"],["52147483648",-4294967291,"50.5"],["abc2147483648","nan","abc0.5"],[" 7 2147483648",-4294967289," 7 0.5"],[2147483649,-4294967295,1.5],[2147483648,-4294967296,0.5],[2147483648,-4294967296,0.5],["nan","nan","nan"],["2147483648",-4294967296,"0.5"],["32147483648",-4294967293,"30.5"],["[object Object]2147483648","nan","[object Object]0.5"]],"lt":[1,1,1,1,1,0,0,1,0,1,0,1,0,0,0,1,1,1,0,1,0,0],"le":[1,1,1,0,1,0,0,1,0,1,0,1,0,0,0,1,1,1,0,1,0,0],"gt":[0,0,1,1,0,0,1,0,1,0,1,0,1,0,1,1,0,0,0,0,1,0],"ge":[1,1,1,1,0,0,1,0,1,0,1,1,1,0,1,1,1,1,0,1,1,0],"whileCond":[3,3,3,3,3,0,0,3,0,3,0,3,0,0,0,3,3,3,0,3,0,0],"ternary":["n","n","n","n","n","n","y","n","y","n","y","n","y","n","y","n","n","n","n","n","y","n"],"andOr":[0,0,1,1,1,0,0,1,0,1,0,0,1,0,1,1,0,0,0,0,1,0],"notCond":[0,0,0,0,0,1,1,0,1,0,1,0,1,1,1,0,0,0,1,0,1,1],"bigint":["TypeError","TypeError","gt","ge","le","lt"],"symbol":["TypeError","TypeError","TypeError","TypeError"],"hints":[[4,2,"lt","ge"],["default","number","number","number","number","number"]],"valueOfOrder":[[2,1,"x"],["v1","v2","s3"]],"throwingValueOf":["RangeError","EvalError","URIError"],"leftEffects":[6,4,7,7],"dateAdd":["string","number",-1],"stringConcat":["a111","b2"],"loops":[10,85,20.5,18],"sameRegister":[4,1,4],"captured":[[5,6,"big"],7],"tdz":"ReferenceError","undeclared":["ReferenceError","ReferenceError"],"global":[3,"41","gt"],"condReturn":["a","b",["neg","zero","odd","even","big"],"object","none"],"condReturnFinally":[1,2,9,2,3,["a","f","b","f"]],"condReturnThrowInArm":["TypeError",1],"condReturnIterClose":[2,0,["close","close"]],"condReturnBlockEnv":[11,2,1,1],"condReturnCtor":[2,1,5,4,4,6,"TypeError",4],"condReturnGenerator":[[{"value":1,"done":false},{"value":"x","done":true},{"done":true},{"value":2,"done":false},{"value":"y","done":true}],{"value":"f","done":false},{"value":1,"done":true},{"done":true}],"condReturnArguments":[1,2,2,null,1],"frames":[610,2000,2000,"TypeError","at 0:311",0,3,2,2],"gen":[2,1,-1],"methodThis":["done",0],"valueOfThrowInFrame":["at 0","at 0"],"generatorInValueOf":[2,null]}
""";

    [Fact]
    public void LiteralOperandsAndConditionalReturnsMatchReferenceBehavior()
    {
        using var engine = new JsEngine();
        var exp = System.Text.Json.JsonDocument.Parse(LiteralOperandExpected).RootElement;
        var got = System.Text.Json.JsonDocument.Parse(engine.Evaluate(LiteralOperandScript).AsString()).RootElement;
        foreach (var p in exp.EnumerateObject())
        {
            Assert.True(got.TryGetProperty(p.Name, out var g), $"missing case {p.Name}");
            Assert.True(p.Value.ToString() == g.ToString(), $"{p.Name}: expected {p.Value} got {g}");
        }
    }

    [Fact]
    public void RecursionThroughLiteralOperandsHitsTheDepthLimit()
    {
        using var engine = new JsEngine(new JsEngineOptions { Limits = new JsLimits { MaxStackDepth = 500 } });
        Assert.Equal(400, engine.Evaluate("function d(n) { return n <= 0 ? 0 : 1 + d(n - 1); } d(400)").AsNumber());
        var ex = Assert.Throws<JsLimitExceededException>(() => engine.Evaluate("d(2000)"));
        Assert.Equal(JsLimitKind.StackDepth, ex.Kind);
        Assert.True(engine.IsFaulted);
    }

    // Strict comparison with a literal constant in the instruction (conditions and switch cases), and the string comparison
    // behind it. Expected values were produced by Node from the same script.
    private const string ConstantCompareScript = """
const out = {};
const vals = ['a', 'b', '', 'ab', 'a' + 'b', ['a', 'b'].join(''), 0, -0, 1, 1.5, NaN, true, false, null, undefined, 1n, 0n, [], {}, new String('a'), new Number(1), Symbol.iterator];
out.eqA = vals.map(x => { if (x === 'a') return 1; return 0; });
out.neA = vals.map(x => { if (x !== 'a') return 1; return 0; });
out.eqAb = vals.map(x => x === 'ab' ? 1 : 0);
out.eqEmpty = vals.map(x => x === '' ? 1 : 0);
out.eqZero = vals.map(x => { if (x === 0) return 1; return 0; });
out.neZero = vals.map(x => { if (x !== 0) return 1; return 0; });
out.eqOne = vals.map(x => { if (x === 1) return 1; return 0; });
out.eqFrac = vals.map(x => { if (x === 1.5) return 1; return 0; });
out.negZeroLiteral = vals.map(x => { if (x === -0) return 1; return 0; });
out.rope = (() => { let s = ''; for (let i = 0; i < 40; i++) s += 'xy'; const t = 'xy'.repeat(40); const u = s + 'z'; const r = []; if (s === 'xyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxy') r.push('eq'); if (u !== 'xyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxyxy') r.push('ne'); r.push(s === t, u === t, s + '' === t, 'p' + s === 'p' + t, 'q' + s === 'p' + t); return r; })();
out.sameLengthLastChar = ['abcd' === 'abce', 'abcd' === 'abcd', 'abcd' === 'xbcd', 'é' === 'é', '😀' === '😁', (a => { if (a === 'abcz') return 1; return 0; })('abc' + 'y')];
const sw = x => { switch (x) { case 'a': return 'A'; case 'b': return 'B'; case '': return 'E'; case 0: return 'Z'; case 1: return 'O'; case 1.5: return 'F'; case 'ab': return 'AB'; default: return 'D'; } };
out.switchLiterals = vals.map(sw);
out.switchFallThrough = ['a', 'b', 'c', 'd', 1].map(x => { const r = []; switch (x) { case 'a': r.push(1); case 'b': r.push(2); break; default: r.push(3); case 'd': r.push(4); case 1: r.push(5); } return r; });
out.switchDefaultFirst = ['a', 'z'].map(x => { const r = []; switch (x) { default: r.push('d'); case 'a': r.push('a'); } return r; });
out.switchMixedTests = (() => { const log = []; const t = (n, v) => { log.push(n); return v; }; const f = x => { switch (x) { case t('p', 'q'): return 1; case 'b': return 2; case t('r', 3): return 3; case 3 + 1: return 4; default: return 5; } }; return [f('q'), f('b'), f(3), f(4), f('n'), log]; })();
out.switchLexical = (x => { switch (x) { case 'a': let y = 1; const g = () => y; y = y + 1; return g(); case 'b': return typeof y; } }).call(null, 'a');
out.switchTdz = (() => { try { return (x => { switch (x) { case 'a': let y = 1; case 'b': return y; } })('b'); } catch (e) { return e.constructor.name; } })();
out.switchDiscOnce = (() => { let n = 0; const d = () => { n++; return 'b'; }; switch (d()) { case 'a': n += 10; case 'b': n += 100; case 'c': n += 1000; } return n; })();
out.switchInLoop = (() => { let s = ''; for (const c of 'abcab') { switch (c) { case 'a': s += '1'; continue; case 'b': s += '2'; break; default: s += '3'; } s += '|'; } return s; })();
out.switchNoCases = (() => { let n = 0; switch (n++) { } return n; })();
out.leftEffects = (() => { const log = []; const f = v => { log.push(v); return v; }; const r = []; if (f('a') === 'a') r.push(1); if (f('b') !== 'b') r.push(2); else r.push(3); while (f('c') === 'd') r.push(4); return [r, log]; })();
out.undeclared = (() => { try { if (nope === 'a') return 1; return 0; } catch (e) { return e.constructor.name; } })();
out.tdz = (() => { try { if (q === 1) return 1; let q = 1; return 0; } catch (e) { return e.constructor.name; } })();
out.inLoop = (() => { const r = []; for (let i = 0; i < 6; i++) { const k = i % 3 === 0 ? 'x' : i % 3 === 1 ? 'y' : 'z'; const isX = k === 'x'; if (k === 'y') r.push('Y' + i); else if (k !== 'z') r.push(isX); } return r; })();
out.typeofForm = vals.map(x => { if (typeof x === 'string') return 's'; if (typeof x === 'number') return 'n'; if (typeof x !== 'object') return 'o'; return 'O'; });
out.frames = (() => { const tag = n => n % 2 === 0 ? 'even' : 'odd'; const rec = n => { switch (tag(n)) { case 'even': return n === 0 ? 'done' : rec(n - 1); case 'odd': try { if (n === 1) throw new Error('one'); return rec(n - 1); } catch (e) { return e.message; } } }; return [rec(4), rec(5), rec(0)]; })();
out.generator = (() => { function* g(x) { switch (x) { case 'a': yield 1; case 'b': yield 2; break; case 'c': return 3; } yield 4; } return [[...g('a')], [...g('b')], [...g('c')], [...g('d')]]; })();
JSON.stringify(out, (k, v) => typeof v === 'symbol' ? 'sym' : v);
""";

    private const string ConstantCompareExpected = """
{"eqA":[1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0],"neA":[0,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1],"eqAb":[0,0,0,1,1,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0],"eqEmpty":[0,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0],"eqZero":[0,0,0,0,0,0,1,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0],"neZero":[1,1,1,1,1,1,0,0,1,1,1,1,1,1,1,1,1,1,1,1,1,1],"eqOne":[0,0,0,0,0,0,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0],"eqFrac":[0,0,0,0,0,0,0,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0],"negZeroLiteral":[0,0,0,0,0,0,1,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0],"rope":["eq","ne",true,false,true,true,false],"sameLengthLastChar":[false,true,false,false,false,0],"switchLiterals":["A","B","E","AB","AB","AB","Z","Z","O","F","D","D","D","D","D","D","D","D","D","D","D","D"],"switchFallThrough":[[1,2],[2],[3,4,5],[4,5],[5]],"switchDefaultFirst":[["a"],["d","a"]],"switchMixedTests":[1,2,3,4,5,["p","p","p","r","p","r","p","r"]],"switchLexical":2,"switchTdz":"ReferenceError","switchDiscOnce":1101,"switchInLoop":"12|3|12|","switchNoCases":1,"leftEffects":[[1,3],["a","b","c"]],"undeclared":"ReferenceError","tdz":"ReferenceError","inLoop":[true,"Y1",true,"Y4"],"typeofForm":["s","s","s","s","s","s","n","n","n","n","n","o","o","O","o","o","o","O","O","O","O","o"],"frames":["one","one","done"],"generator":[[1,2,4],[2,4],[],[4]]}
""";

    [Fact]
    public void ConstantComparesAndSwitchesMatchReferenceBehavior()
    {
        using var engine = new JsEngine();
        var exp = System.Text.Json.JsonDocument.Parse(ConstantCompareExpected).RootElement;
        var got = System.Text.Json.JsonDocument.Parse(engine.Evaluate(ConstantCompareScript).AsString()).RootElement;
        foreach (var p in exp.EnumerateObject())
        {
            Assert.True(got.TryGetProperty(p.Name, out var g), $"missing case {p.Name}");
            Assert.True(p.Value.ToString() == g.ToString(), $"{p.Name}: expected {p.Value} got {g}");
        }
    }

    // A pooled frame reused by another function at the same depth. Expected values were produced by Node from the same script.
    private const string FrameReuseScript = """
const out = {};
// one depth, different functions: each must see its own code, constants and inline caches
const a = o => 'a:' + o.x, b = o => 'b:' + o.y, c = function (o) { return 'c:' + (o.x + o.y); };
out.alternate = (() => { const r = []; const fs = [a, b, c, a, a, c, b]; for (const f of fs) r.push(f({ x: 1, y: 2 })); return r; })();
// one template, different closures: same code, different environments
const mk = k => n => n + k;
out.sameTemplate = (() => { const fs = [mk(1), mk(10), mk(100)]; const r = []; for (let i = 0; i < 7; i++) r.push(fs[i % 3](i)); return r; })();
// inline caches are per function: a shape seen in one must not answer in the other
out.ics = (() => { const g1 = o => o.v, g2 = o => o.v; const p = { v: 1 }, q = { w: 0, v: 2 }; const r = []; for (let i = 0; i < 4; i++) { r.push(g1(p)); r.push(g2(q)); } r.push(g1(q), g2(p)); return r; })();
// frames left by a throw, a generator, an async function, eval and new Function, then the depth used again
out.afterThrow = (() => { const t = () => { throw new RangeError('x'); }; const s = () => 'ok'; const r = []; for (let i = 0; i < 3; i++) { try { r.push(t()); } catch (e) { r.push(e.message); } r.push(s()); } return r; })();
out.generators = (() => { function* g1() { const v = yield 'p'; yield 'q' + v; } function* g2() { yield 'r'; yield 's'; } const x = g1(), y = g2(); return [x.next().value, y.next().value, x.next(1).value, y.next().value, x.next().done, y.next().done]; })();
out.evalAndFunction = (() => { const f = new Function('a', 'return "nf" + a'); const r = []; for (let i = 0; i < 3; i++) { r.push(f(i)); r.push(eval('"ev" + i')); r.push(a({ x: i })); } return r; })();
out.classes = (() => { class A { constructor() { this.k = 'A'; } m() { return 'mA' + this.k; } } class B extends A { constructor() { super(); this.k += 'B'; } m() { return 'mB' + super.m(); } } return [new A().m(), new B().m(), new A().m()]; })();
out.accessors = (() => { const o = { get g() { return 'gv'; }, set g(v) { this._ = 'sv' + v; } }; o.g = 1; return [o.g, o._, [1, 2].map(x => x + 1), [3, 1, 2].sort((p, q) => q - p)]; })();
out.errorKinds = (() => { const r = []; const f1 = () => null.p, f2 = () => undefined(), f3 = () => zz; for (const f of [f1, f2, f3, f1]) { try { f(); } catch (e) { r.push(e.constructor.name); } } return r; })();
out.recursionMix = (() => { const even = n => n === 0 ? 'even' : odd(n - 1); const odd = n => n === 0 ? 'odd' : even(n - 1); return [even(10), even(7), odd(301)]; })();
JSON.stringify(out);
""";

    private const string FrameReuseExpected = """
{"alternate":["a:1","b:2","c:3","a:1","a:1","c:3","b:2"],"sameTemplate":[1,11,102,4,14,105,7],"ics":[1,2,1,2,1,2,1,2,2,1],"afterThrow":["x","ok","x","ok","x","ok"],"generators":["p","r","q1","s",true,true],"evalAndFunction":["nf0","ev0","a:0","nf1","ev1","a:1","nf2","ev2","a:2"],"classes":["mAA","mBmAAB","mAA"],"accessors":["gv","sv1",[2,3],[3,2,1]],"errorKinds":["TypeError","TypeError","ReferenceError","TypeError"],"recursionMix":["even","odd","even"]}
""";

    [Fact]
    public void FramesReusedByOtherFunctionsMatchReferenceBehavior()
    {
        using var engine = new JsEngine();
        var exp = System.Text.Json.JsonDocument.Parse(FrameReuseExpected).RootElement;
        var got = System.Text.Json.JsonDocument.Parse(engine.Evaluate(FrameReuseScript).AsString()).RootElement;
        foreach (var p in exp.EnumerateObject())
        {
            Assert.True(got.TryGetProperty(p.Name, out var g), $"missing case {p.Name}");
            Assert.True(p.Value.ToString() == g.ToString(), $"{p.Name}: expected {p.Value} got {g}");
        }
    }

    private const string RegExpTestPrelude = """
const o=[];const S=()=>[RegExp.$1,RegExp.$2,RegExp['$+'],RegExp.lastMatch,RegExp['$`'],RegExp["$'"],RegExp.input].join(',');const T=f=>{try{return String(f())}catch(e){return e.name}};
""";

    private const string RegExpTestSnippets = """
o.push(/\d+/.test('abc123'), S()); o.push(/z/.test('abc123'), S()); o.push(/(\d)(\d)/.test('abc123'), S()); o.push(/(z)/.test('q'), S());
o.push(/(a)|(b)/.test('xb'), S(), /(a)|(b)/.test('xa'), S()); o.push(/(a)(b)?/.test('a'), S());
o.push(/(a)(b)(c)(d)(e)(f)(g)(h)(i)(j)(k)/.test('abcdefghijk'), RegExp.$9, RegExp['$+'], RegExp.lastParen, RegExp.lastMatch);
o.push(/(?<y>\d{4})-(?<m>\d\d)/.test('on 2024-05!'), S(), RegExp.$3);
o.push(/(?<a>x)|(?<a>y)/.test('y'), S()); o.push(/(?:(a)|b)+/.test('ab'), S(), RegExp.$1 === '');
o.push(/(?<=(a))b/.test('zab'), S()); o.push(/(\w)\1/.test('xaab'), S()); o.push(/\p{Nd}+/u.test('x٣'), S()); o.push(/(\p{Nd})/u.test('x٣'), S());
const g = /(\d)/g; for (let i = 0; i < 5; i++) o.push(g.test('1a2b3'), g.lastIndex, RegExp.$1, RegExp['$`']);
const g2 = /(\d)/g; o.push(g2.test('1a2'), g2.lastIndex); o.push(g2.test('1a2'), g2.lastIndex); o.push(RegExp.$1, RegExp.$2, RegExp.lastMatch); o.push(g2.test('zz'), g2.lastIndex, S());
const y = /(b)/y; o.push(y.test('abc'), y.lastIndex, S()); y.lastIndex = 1; o.push(y.test('abc'), y.lastIndex, S()); o.push(y.test('abc'), y.lastIndex, S());
const y2 = /b/y; y2.lastIndex = 1; o.push(y2.test('abc'), y2.lastIndex, S()); o.push(y2.test('abc'), y2.lastIndex); const gy = /b/gy; gy.lastIndex = 1; o.push(gy.test('abb'), gy.lastIndex, gy.test('abb'), gy.lastIndex, gy.test('abb'), gy.lastIndex);
const b = /b/g; for (const li of [5, 1.5, -1, NaN, Infinity, '1', null, undefined, true, 2 ** 40]) { b.lastIndex = li; o.push(T(() => b.test('abcb')), b.lastIndex); }
const c = /a/g; const log = []; c.lastIndex = { valueOf() { log.push('valueOf'); return 2; } }; o.push(c.test('aaaa'), c.lastIndex, log.length); const d = /a/; d.lastIndex = { valueOf() { log.push('nong'); return 9; } }; o.push(d.test('a'), typeof d.lastIndex, log.join());
const e = /a/g; e.lastIndex = { valueOf() { return Symbol(); } }; o.push(T(() => e.test('a')));  const f = /a/; f.lastIndex = { valueOf() { throw new RangeError('x'); } }; o.push(T(() => f.test('a')));
const h = /a/g; h.lastIndex = { valueOf() { h.compile('b', 'g'); return 0; } }; o.push(h.test('ab'), h.lastIndex, h.source, S());
const n = /(a)/; n.lastIndex = { valueOf() { n.compile('(b)'); return 0; } }; o.push(n.test('ab'), n.source, S());
const u = /(a)/; o.push(u.test('xa')); u.compile('(b)'); o.push(S(), u.test('xa'), u.test('xb'), S());
const v = /(a)/; v.test('xa'); const w = /(q)/; o.push(w.test('zz'), S(), /(r)/.exec('r')[0], S(), /n/.test('n'), S());
/(a)/.test('a'); RegExp.input = 'over'; o.push(RegExp.input, RegExp.$_, RegExp.$1, RegExp.lastMatch); /(z)/.test('z'); o.push(RegExp.input, RegExp.$_, RegExp.$1);
/(a)/.test('a'); RegExp.$_ = 5; o.push(RegExp.input); /b/.test('b'); o.push(RegExp.input, RegExp.$1, RegExp.lastMatch);
o.push(RegExp.$1, RegExp.lastMatch, RegExp.input, RegExp['$+']); o.push(/()/.test(''), S()); o.push(/(x*)/.test('aa'), S(), RegExp['$`'], RegExp["$'"]);
/(\d)/.test('a1'); const s1 = RegExp.$1; /(\d)/.test('a2'); const s2 = RegExp.$1; /\d/.test('a3'); const s3 = RegExp.$1; /(x)/.test('zx'); o.push(s1, s2, s3, RegExp.$1, RegExp.lastMatch, RegExp['$`']);
o.push('a1b2'.replace(/(\d)/g, m => { /(x)/.test('zxz'); return RegExp.$1 + RegExp.lastMatch; }), S()); o.push('a1'.replace(/(\d)/, () => RegExp.$1), S()); o.push(/(q)/.test('q') && 'p1'.split(/(\d)/).length, S());
o.push(/k/i.test('K'), /K/i.test('k'), /k/i.test('K'), /(k)/i.test('é K'), S(), /é/i.test('É'), S()); const ig = /k/gi; ig.lastIndex = 1; o.push(ig.test('kék'), ig.lastIndex, S());
const P = RegExp.prototype, orig = P.exec; const r = /a/; const out = () => [r.test('a'), r.test('b')]; o.push(out()); P.exec = function (s) { o.push('patched', s); return orig.call(this, s); }; o.push(out(), S()); P.exec = function () { return null; }; o.push(out()); P.exec = function () { return { 0: 'x' }; }; o.push(out()); P.exec = function () { return 1; }; o.push(T(() => r.test('a'))); P.exec = function () { return undefined; }; o.push(T(() => r.test('a'))); P.exec = 5; o.push(out()); P.exec = null; o.push(out()); P.exec = orig; o.push(out());
const P2 = RegExp.prototype, orig2 = P2.exec, desc = Object.getOwnPropertyDescriptor(P2, 'exec'); const r2 = /(a)/g; Object.defineProperty(P2, 'exec', { get() { o.push('get'); return orig2; }, configurable: true }); o.push(r2.test('a'), r2.lastIndex, RegExp.$1); Object.defineProperty(P2, 'exec', desc); o.push(r2.test('a'), r2.lastIndex); delete P2.exec; o.push(/(b)/.test('b'), S(), T(() => /b/.exec)); Object.defineProperty(P2, 'exec', desc); o.push(/c/.test('c'), S());
const P3 = RegExp.prototype, orig3 = P3.exec; delete P3.exec; P3.zzz = 1; P3.exec = orig3; o.push(/(b)/.test('b'), S()); P3.exec = function () { return null; }; o.push(/(b)/.test('b')); delete P3.zzz; o.push(/(c)/.test('c')); P3.exec = orig3; o.push(/(d)/.test('d'), S());
const i1 = /a/; i1.exec = function () { o.push('own'); return null; }; o.push(i1.test('a'), /a/.test('a')); const i2 = /a/; i2.extra = 1; o.push(i2.test('a'), i2.extra, S()); const i3 = /a/; Object.defineProperty(i3, 'exec', { get() { o.push('ga'); return () => ({}); } }); o.push(i3.test('z'));
const q1 = /a/; Object.setPrototypeOf(q1, null); o.push(T(() => RegExp.prototype.test.call(q1, 'a')));
const q2 = /a/; Object.setPrototypeOf(q2, { exec() { return null; } }); o.push(RegExp.prototype.test.call(q2, 'a')); const q3 = /a/; Object.setPrototypeOf(q3, Object.create(RegExp.prototype)); o.push(q3.test('a'), S());
class A extends RegExp {} const a = new A('b(c)', 'g'); o.push(a.test('xbc'), a.lastIndex, a.test('xbc'), a.lastIndex, a instanceof A);
class B extends RegExp { exec(s) { o.push('B.exec', s); return super.exec(s); } } const bb = new B('(z)'); o.push(bb.test('z'), bb.test('y'));
class C extends RegExp { get lastIndex() { return 1; } } const cc = new C('b', 'g'); o.push(cc.test('ab'), cc.lastIndex);
class D extends RegExp { exec() { return null; } } const dd = new D('a'); Object.setPrototypeOf(dd, RegExp.prototype); o.push(dd.test('a'), dd.lastIndex);
const T1 = RegExp.prototype.test; o.push(T(() => T1.call(1, 'a')), T(() => T1.call({}, 'a')), T(() => T1.call(Object.create(RegExp.prototype), 'a')), T1.call({ exec() { return {}; } }, 'a'), T1.call({ exec() { return null; } }, 'a'), T(() => T1.call({ exec: 1 }, 'a')));
o.push(/undefined/.test(), /null/.test(null), /1/.test(1), /x/.test({ toString() { return 'x'; } }), T(() => /x/.test(Symbol()))); const log2 = []; const r3 = /a/g; r3.lastIndex = { valueOf() { log2.push('li'); return 0; } }; r3.test({ toString() { log2.push('str'); return 'a'; } }); o.push(log2.join());
const fz = Object.freeze(/a/); o.push(fz.test('a'), fz.test('b'), S()); const fg = Object.freeze(/a/g); o.push(T(() => fg.test('a')), fg.lastIndex, T(() => fg.test('b')), S()); const fy = Object.freeze(/a/y); o.push(T(() => fy.test('a')), T(() => fy.test('b'))); o.push(/a/g.test('a'), Object.isFrozen(/a/));
const ro = /(a)/g; Object.defineProperty(ro, 'lastIndex', { writable: false, value: 0 }); o.push(T(() => ro.test('xa')), ro.lastIndex, T(() => ro.test('b')), ro.lastIndex); const ro2 = /(a)/g; Object.defineProperty(ro2, 'lastIndex', { writable: false, value: 9 }); o.push(T(() => ro2.test('a')), ro2.lastIndex); const pe = Object.preventExtensions(/a/g); o.push(pe.test('a'), pe.lastIndex); const se = Object.seal(/a/y); o.push(se.test('a'), se.lastIndex);
function lit() { return /a/g; } const l1 = lit(), l2 = lit(); o.push(l1 === l2, l1.test('a'), l1.lastIndex, l2.lastIndex, Object.getOwnPropertyNames(l1), JSON.stringify(Object.getOwnPropertyDescriptor(l2, 'lastIndex'))); l1.a = 1; l1.b = 2; l1.c = 3; l1.d = 4; l1.e = 5; o.push(Object.keys(l1), l1.e, l1.lastIndex, Object.keys(l2), Object.getOwnPropertyNames(lit()));
const fr = []; for (let i = 0; i < 3; i++) { const x = /a/g; if (i === 1) Object.freeze(x); fr.push(Object.isFrozen(x), T(() => x.test('a')), x.lastIndex); } o.push(fr); const nr = new RegExp('a', 'g'); nr.z = 1; o.push(Object.getOwnPropertyNames(nr), new RegExp('b').lastIndex, Object.getOwnPropertyNames(new RegExp(/c/g)), delete nr.z, T(() => delete nr.lastIndex), RegExp('a') instanceof RegExp);
function NT() {} NT.prototype = Object.create(RegExp.prototype); const nt = Reflect.construct(RegExp, ['(a)'], NT); o.push(Object.getPrototypeOf(nt) === NT.prototype, nt.test('a'), nt.lastIndex); const rp = Reflect.construct(RegExp, ['a'], RegExp); o.push(rp.test('a'), Object.getOwnPropertyNames(rp));
const x1 = /b+/.exec('abbc'); o.push(x1.index, x1.input, x1[0], x1.length, x1.groups, S()); const x2 = /b/g; o.push(x2.exec('abb').index, x2.lastIndex, x2.exec('abb').index, x2.exec('abb'), x2.lastIndex); o.push('abcabc'.replace(/b/g, 'X'), 'abc'.replace(/b/, '[$&$`$\']'), 'a1b2'.split(/\d/), 'a,b'.split(/,/, 1), 'abc'.split(/(?:)/), ''.split(/x/), ''.split(/(?:)/), 'aXbX'.match(/X/g), 'aXb'.search(/b/), [...'a1b2'.matchAll(/\d/g)].map(m => m.index), S());
const dI = /b/d.exec('abc'); o.push(dI.indices[0], /(?<=a)b/.exec('ab').index); const lb = /(?<=a)b/g; lb.lastIndex = 1; o.push(lb.test('ab'), lb.lastIndex, S()); const lc = /(?<!a)b/y; lc.lastIndex = 1; o.push(lc.test('ab'), lc.lastIndex); const hat = /^b/g; hat.lastIndex = 1; o.push(hat.test('ab'), hat.lastIndex); const hm = /^(b)/gm; hm.lastIndex = 1; o.push(hm.test('a\nb'), hm.lastIndex, S()); const wb = /\bb/g; wb.lastIndex = 1; o.push(wb.test('ab'), wb.lastIndex); const em = /(?:)/g; em.lastIndex = 2; o.push(em.test('abc'), em.lastIndex, S()); const us = /./gu; us.lastIndex = 0; o.push(us.test('\u{1F600}'), us.lastIndex, RegExp.lastMatch.length);
const t = 'x'.repeat(5000) + '7'; o.push(/(\d)/.test(t), RegExp.$1, RegExp['$`'].length, /\d/.test(t), RegExp.lastMatch, RegExp["$'"]); let cnt = 0; for (let i = 0; i < 2000; i++) { if (/(\d)/.test('a' + (i % 10))) cnt += +RegExp.$1; if (/\d/.test('b')) cnt = -1; } o.push(cnt, RegExp.$1);
let c2 = 0; const P4 = RegExp.prototype, o4 = P4.exec; for (let i = 0; i < 40; i++) { if (i === 20) P4.exec = function () { c2 += 100; return null; }; if (i === 30) P4.exec = o4; if (/a/.test('a')) c2++; } o.push(c2, RegExp.lastMatch);
const O = Object.getOwnPropertyDescriptor(RegExp, '$1'); o.push(typeof O.get, O.get.call(RegExp) === RegExp.$1); /(a)/.test('a'); o.push(O.get.call(RegExp), Object.getOwnPropertyDescriptor(RegExp.prototype, 'test').value.length);
""";

    private const string RegExpTestExpected = """
[true,",,,123,abc,,abc123",false,",,,123,abc,,abc123",true,"1,2,2,12,abc,3,abc123",false,"1,2,2,12,abc,3,abc123"]
[true,",b,b,b,x,,xb",true,"a,,,a,x,,xa",true,"a,,,a,,,a"]
[true,"i","k","k","abcdefghijk"]
[true,"2024,05,05,2024-05,on ,!,on 2024-05!",""]
[true,",y,y,y,,,y",true,",,,ab,,,ab",true]
[true,"a,,a,b,za,,zab",true,"a,,a,aa,x,b,xaab",true,",,,٣,x,,x٣",true,"٣,,٣,٣,x,,x٣"]
[true,1,"1","",true,3,"2","1a",true,5,"3","1a2b",false,0,"3","1a2b",true,1,"1",""]
[true,1,true,3,"2","","2",false,0,"2,,2,2,1a,,1a2"]
[false,0,",,,,,,",true,2,"b,,b,b,a,c,abc",false,0,"b,,b,b,a,c,abc"]
[true,2,",,,b,a,c,abc",false,0,true,2,true,3,false,0]
["false",0,"true",2,"true",2,"true",2,"false",0,"true",2,"true",2,"true",2,"true",2,"false",0]
[true,3,1,true,"object","valueOf,nong"]
["TypeError","RangeError"]
[true,2,"b",",,,b,a,,ab"]
[true,"(b)","b,,b,b,a,,ab"]
[true,"a,,a,a,x,,xa",false,true,"b,,b,b,x,,xb"]
[false,"a,,a,a,x,,xa","r","r,,r,r,,,r",true,",,,n,,,n"]
["over","over","a","a","z","z","z"]
["5","b","","b"]
["","","","",true,",,,,,,",true,",,,,,aa,aa","","aa"]
["1","2","","x","x","z"]
["axxbxx","x,,x,x,z,z,zxz","a1","1,,1,1,a,,a1",3,"1,,1,1,p,,p1"]
[false,false,true,true,"K,,K,K,é ,,é K",true,",,,É,,,É",true,3,",,,k,ké,,kék"]
[[true,false],"patched","a","patched","b",[true,false],",,,a,,,a",[false,false],[true,true],"TypeError","TypeError",[true,false],[true,false],[true,false]]
["get",true,1,"a",false,0,true,"b,,b,b,,,b","undefined",true,",,,c,,,c"]
[true,"b,,b,b,,,b",false,false,true,"d,,d,d,,,d"]
["own",false,true,true,1,",,,a,,,a","ga",true]
["true"]
[false,true,",,,a,,,a"]
[true,3,false,0,true]
["B.exec","z","B.exec","y",true,false]
[true,2]
[true,0]
["TypeError","TypeError","TypeError",true,false,"TypeError"]
[true,true,true,true,"TypeError","str,li"]
[true,false,",,,a,,,a","TypeError",0,"TypeError",",,,a,,,a","TypeError","TypeError",true,false]
["TypeError",0,"TypeError",0,"TypeError",9,true,1,true,1]
[false,true,1,0,["lastIndex"],"{\"value\":0,\"writable\":true,\"enumerable\":false,\"configurable\":false}",["a","b","c","d","e"],5,1,[],["lastIndex"]]
[[false,"true",1,true,"TypeError",0,false,"true",1],["lastIndex","z"],0,["lastIndex"],true,"false",true]
[true,true,0,true,["lastIndex"]]
[1,"abbc","bb",1,null,",,,bb,a,c,abbc",1,2,2,null,0,"aXcaXc","a[bac]c",["a","b",""],["a"],["a","b","c"],[""],[],["X","X"],2,[1,3],",,,2,a1b,,a1b2"]
[[1,2],1,true,2,",,,b,a,,ab",false,0,false,0,true,3,"b,,b,b,a\n,,a\nb",false,0,true,2,",,,,ab,c,abc",true,2,2]
[true,"7",5000,true,"7","",9000,"9"]
[1030,"a"]
["function",true,"a",1]
""";

    /// <summary>Each line runs in a new engine: the first read of a capture changes how <c>test</c> records them.</summary>
    [Fact]
    public void RegExpTestAndLegacyStaticsMatchReferenceBehavior()
    {
        var snippets = RegExpTestSnippets.ReplaceLineEndings("\n").Split('\n');
        var expected = RegExpTestExpected.ReplaceLineEndings("\n").Split('\n');
        Assert.Equal(expected.Length, snippets.Length);
        for (int i = 0; i < snippets.Length; i++)
        {
            using var engine = new JsEngine();
            var got = engine.Evaluate(RegExpTestPrelude + "\n" + snippets[i] + "\nJSON.stringify(o)").AsString();
            Assert.True(expected[i] == got, $"line {i}: {snippets[i]}\nexpected {expected[i]}\nbut got  {got}");
        }
    // Promise.prototype.then, reaction jobs and the job queue. Expected output generated by Node 24.15.0.
    private const string PromiseJobsScript = """
const log = globalThis.__log = [];
const say = (...a) => log.push(a.map(x => typeof x === 'string' ? x : x === undefined ? 'undefined' : typeof x === 'function' ? 'fn' : x instanceof Error ? x.name : String(x)).join(' '));
const hash = a => a.reduce((h, v) => (h * 31 + v) % 1000003, 7);
// Scenarios run one after another, each draining fully, so that a tick counter is exact for each.
const queue = [];
const scenario = (name, f) => queue.push([name, f]);
// tick(): how many microtask hops have passed since the scenario began.
let hops = 0, ticking = false;
const startTicker = n => { hops = 0; let p = Promise.resolve(); for (let i = 0; i < n; i++) p = p.then(() => { hops++; }); return p; };
const at = (name, what) => say(name, what, '@', hops);

// ---- await and resolve tick counts
scenario('await primitive', async n => { startTicker(8); await 1; at(n, 'a'); await undefined; at(n, 'b'); });
scenario('await native', async n => { startTicker(8); await Promise.resolve(1); at(n, 'a'); });
scenario('await pending native', async n => { startTicker(8); let r; const p = new Promise(x => { r = x; }); r(1); await p; at(n, 'a'); });
scenario('await thenable', async n => { startTicker(8); await { then(f) { at(n, 'then'); f(1); } }; at(n, 'a'); });
scenario('await then derived', async n => { startTicker(8); await Promise.resolve(1).then(v => v); at(n, 'a'); });
scenario('await patched own constructor', async n => { startTicker(10); const p = Promise.resolve(1); p.constructor = function () {}; await p; at(n, 'a'); });
scenario('await constructor getter', async n => { startTicker(10); const p = Promise.resolve(1); Object.defineProperty(p, 'constructor', { get() { at(n, 'getter'); return Promise; } }); await p; at(n, 'a'); });
scenario('await subclass', async n => { class S extends Promise {} startTicker(10); await S.resolve(1); at(n, 'a'); });
scenario('async return value', async n => { startTicker(8); const f = async () => 1; f().then(() => at(n, 'a')); });
scenario('async return promise', async n => { startTicker(8); const f = async () => Promise.resolve(1); f().then(() => at(n, 'a')); });
scenario('async return thenable', async n => { startTicker(8); const f = async () => ({ then(g) { g(1); } }); f().then(() => at(n, 'a')); });
scenario('resolve with promise', n => { startTicker(8); new Promise(r => r(Promise.resolve(1))).then(() => at(n, 'a')); });
scenario('resolve with derived', n => { startTicker(8); new Promise(r => r(Promise.resolve(1).then(v => v))).then(() => at(n, 'a')); });
scenario('handler returns promise', n => { startTicker(8); Promise.resolve().then(() => Promise.resolve(1)).then(() => at(n, 'a')); });
scenario('handler returns pending then settled', n => { startTicker(10); let r; const q = new Promise(x => { r = x; }); Promise.resolve().then(() => q).then(v => at(n, 'a', v)); Promise.resolve().then(() => r(5)); });
scenario('handler returns thenable', n => { startTicker(8); Promise.resolve().then(() => ({ then(f) { at(n, 'then'); f(2); } })).then(v => at(n, 'a', v)); });
scenario('pass through', n => { startTicker(8); Promise.resolve(1).then().then(null).then(undefined, undefined).then(3, 'x').then(v => at(n, 'a', v)); });
scenario('rejection through', n => { startTicker(8); Promise.reject(1).then(() => at(n, 'no')).then(() => at(n, 'no')).catch(v => at(n, 'a', v)); });
scenario('finally ticks', n => { startTicker(12); Promise.resolve(1).finally(() => at(n, 'f')).then(v => at(n, 'a', v)); });
scenario('finally rejected', n => { startTicker(12); Promise.reject(2).finally(() => at(n, 'f')).catch(v => at(n, 'a', v)); });
scenario('all race any settled', n => { startTicker(14);
  Promise.all([1, Promise.resolve(2)]).then(v => at(n, 'all', v));
  Promise.race([1, Promise.resolve(2)]).then(v => at(n, 'race', v));
  Promise.any([Promise.reject(1), 2]).then(v => at(n, 'any', v));
  Promise.allSettled([1, Promise.reject(2)]).then(v => at(n, 'settled', v.length)); });
scenario('async generator', async n => { startTicker(30); async function* g() { at(n, 'start'); const x = yield 1; at(n, 'x', x); yield Promise.resolve(2); return 3; } const it = g(); at(n, 'v', (await it.next()).value); at(n, 'v', (await it.next('s')).value); at(n, 'v', (await it.next()).value); });
scenario('for await', async n => { startTicker(30); for await (const v of [1, Promise.resolve(2), { then(f) { f(3); } }]) at(n, 'v', v); });

// ---- interleaving
scenario('interleave', n => {
  const o = [];
  const a = async () => { o.push('a1'); await null; o.push('a2'); await Promise.resolve(); o.push('a3'); };
  const b = () => Promise.resolve().then(() => o.push('b1')).then(() => o.push('b2')).then(() => o.push('b3'));
  a(); b(); o.push('sync');
  queueMicrotaskOr(() => o.push('q1'));
  return new Promise(r => { let p = Promise.resolve(); for (let i = 0; i < 12; i++) p = p.then(() => {}); p.then(() => { say(n, o.join()); r(); }); });
});
function queueMicrotaskOr(f) { Promise.resolve().then(f); }
scenario('reaction order pending', n => {
  const o = []; let res; const p = new Promise(r => { res = r; });
  for (let i = 0; i < 5; i++) p.then(() => { o.push(i); Promise.resolve().then(() => o.push('n' + i)); });
  p.catch(() => o.push('never')); p.finally(() => o.push('fin'));
  res(1); res(2);
  p.then(v => o.push('late' + v));
  return new Promise(r => { let q = Promise.resolve(); for (let i = 0; i < 8; i++) q = q.then(() => {}); q.then(() => { say(n, o.join()); r(); }); });
});
scenario('rejected order pending', n => {
  const o = []; let rej; const p = new Promise((_, r) => { rej = r; });
  for (let i = 0; i < 3; i++) p.then(() => o.push('f' + i), () => o.push('r' + i));
  p.catch(() => o.push('c'));
  rej(1); rej(2);
  return Promise.resolve().then(() => {}).then(() => {}).then(() => say(n, o.join()));
});

// ---- bursts that cross any internal queue boundary, in order and with jobs added while draining
for (const size of [1, 15, 16, 17, 31, 33, 2047, 2048, 2049, 4097, 10000, 20000]) scenario('burst ' + size, n => {
  const p = Promise.resolve(); const o = []; const out = [];
  for (let i = 0; i < size; i++) out.push(p.then(() => { o.push(i); return i; }));
  return Promise.all(out).then(r => { let ok = true; for (let i = 0; i < size; i++) if (o[i] !== i || r[i] !== i) ok = false; say(n, ok, o.length, hash(o)); });
});
scenario('burst pending', n => {
  let res; const p = new Promise(r => { res = r; }); const o = [];
  for (let i = 0; i < 5000; i++) p.then(v => { o.push(i + v); });
  res(1);
  return Promise.resolve().then(() => { say(n, 'first', o.length); return new Promise(r => { let q = Promise.resolve(); q.then(() => { say(n, o.length, hash(o)); r(); }); }); });
});
scenario('ping pong', n => {
  // a queue that never empties, so its front and back move through any block boundary
  const o = []; let live = 3;
  return new Promise(done => {
    const step = (id, left) => { o.push(id); if (left === 0) { if (--live === 0) { say(n, o.length, hash(o)); done(); } return; } Promise.resolve().then(() => step(id, left - 1)); };
    for (let id = 0; id < 3; id++) Promise.resolve().then(() => step(id, 3000 + id));
  });
});
scenario('growing fan out', n => {
  const o = []; let d = 0;
  const spawn = level => { o.push(level); if (level < 12) { Promise.resolve(level + 1).then(spawn); Promise.resolve(level + 1).then(spawn); } };
  spawn(0);
  return new Promise(r => { const check = () => { if (o.length === 8191) { say(n, o.length, hash(o)); r(); } else Promise.resolve().then(check); }; check(); });
});
scenario('long chain', n => { let p = Promise.resolve(0); for (let i = 0; i < 5000; i++) p = p.then(v => v + i); return p.then(v => say(n, v)); });
scenario('deep await chain', async n => { const f = async d => d === 0 ? 0 : 1 + await f(d - 1); say(n, await f(300)); });

// ---- handler calls
scenario('handler call', n => {
  'use strict';
  const rec = [];
  return Promise.resolve(7).then(function (...a) { rec.push(this === undefined, a.length, a[0]); return 1; })
    .then(function () { rec.push(arguments.length); })
    .then(function sloppyLike() { rec.push(typeof this); })
    .then(() => say(n, rec.join()));
});
scenario('sloppy handler this', n => new Function('say,n', 'return Promise.resolve().then(function () { say(n, this === globalThis, arguments.length); })')(say, n));
scenario('handler kinds', n => {
  class K { constructor() {} }
  const bound = function (a, b) { return [this.x, a, b].join(); }.bind({ x: 1 }, 2);
  return Promise.resolve(3).then(bound).then(v => say(n, 'bound', v))
    .then(K).catch(e => say(n, 'class', e.name))
    .then(async v => { await 1; return 9; }).then(v => say(n, 'async', v))
    .then(function* () { yield 1; }).then(g => say(n, 'gen', typeof g.next))
    .then(Math.max).then(v => say(n, 'native', v))
    .then(new Proxy(() => 11, {})).then(v => say(n, 'proxy', v))
    .then(new Proxy(() => 12, { apply() { throw new RangeError('p'); } })).catch(e => say(n, 'proxy throws', e.name))
    .then(Promise).catch(e => say(n, 'Promise as handler', e.name));
});
scenario('handler throws', n => Promise.resolve().then(() => { throw 1; }).then(() => say(n, 'no'), v => { say(n, 'r', v); throw 2; }).catch(v => { say(n, 'c', v); return 3; }).then(v => say(n, 'v', v)));
scenario('both handlers one runs', n => { let a = 0, b = 0; return Promise.resolve().then(() => { a++; throw 1; }, () => { b++; }).then(() => { a += 10; }, () => { b += 10; }).then(() => say(n, a, b)); });
scenario('cycle', n => { const d = Promise.resolve().then(() => d); return d.catch(e => say(n, e.name, e instanceof TypeError)); });
scenario('derived identity', n => {
  const p = Promise.resolve(1); const d = p.then(); say(n, d !== p, d instanceof Promise, Object.getPrototypeOf(d) === Promise.prototype, Object.keys(d).length, Object.getOwnPropertyNames(d).length, Object.getOwnPropertySymbols(d).length, Object.prototype.toString.call(d), String(p.then.length), p.then.name);
  return d;
});
scenario('derived mutated while pending', n => {
  const o = []; const p = Promise.resolve(1);
  const d1 = p.then(v => v + 1); d1.extra = 5; d1.then(v => o.push('d1', v, d1.extra));
  const d2 = p.then(v => v + 2); Object.freeze(d2); d2.then(v => o.push('d2', v, Object.isFrozen(d2)));
  const d3 = p.then(v => v + 3); Object.setPrototypeOf(d3, null); Promise.prototype.then.call(d3, v => o.push('d3', v));
  const d4 = p.then(v => v + 4); d4.then = function (f) { o.push('own then'); return Promise.prototype.then.call(this, f); }; d4.then(v => o.push('d4', v));
  const d5 = p.then(v => v + 5); Promise.resolve().then(() => d5).then(v => o.push('d5', v));
  const d6 = p.then(); const d7 = d6.then(); const d8 = d7.then(); d8.then(v => o.push('d8', v));
  const d9 = p.then(v => { d9.late = 1; return d9.late + v; }); d9.then(v => o.push('d9', v));
  class Q extends Promise {} const d10 = p.then(v => v + 10); Object.setPrototypeOf(d10, Q.prototype); const d11 = d10.then(v => v); o.push(d11 instanceof Q); d11.then(v => o.push('d11', v));
  return new Promise(r => { let q = Promise.resolve(); for (let i = 0; i < 12; i++) q = q.then(() => {}); q.then(() => { say(n, o.join()); r(); }); });
});
scenario('then many on derived while pending', n => { const o = []; const d = Promise.resolve(1).then(v => v); for (let i = 0; i < 40; i++) d.then(() => o.push(i)); return Promise.resolve().then(() => {}).then(() => {}).then(() => {}).then(() => say(n, hash(o), o.length)); });
scenario('resolve derived with itself chained', n => { const o = []; let r0; const a = new Promise(r => { r0 = r; }); const b = a.then(v => { o.push('b', v); return b2; }); const b2 = Promise.resolve(2).then(v => v * 2); return (r0(1), b.then(v => say(n, o.join(), v))); });

// ---- species and patching: each undoes what it patched
const patched = (obj, key, desc, f) => { const old = Object.getOwnPropertyDescriptor(obj, key); Object.defineProperty(obj, key, desc); try { return f(); } finally { if (old) Object.defineProperty(obj, key, old); else delete obj[key]; } };
scenario('prototype constructor patched', n => {
  const seen = [];
  class C extends Promise { constructor(e) { seen.push('C'); super(e); } }
  const r = patched(Promise.prototype, 'constructor', { value: C, writable: true, configurable: true }, () => { const d = Promise.resolve(1).then(v => v + 1); seen.push(d instanceof C); return d; });
  return r.then(v => say(n, v, seen.join())).then(() => say(n, 'after', Promise.resolve().then() instanceof C));
});
scenario('prototype constructor accessor', n => {
  let reads = 0; let d;
  patched(Promise.prototype, 'constructor', { get() { reads++; return Promise; }, configurable: true }, () => { d = Promise.resolve(1).then(v => v); d.then(); });
  say(n, reads);
  return d;
});
scenario('prototype constructor undefined and bad', n => {
  const a = patched(Promise.prototype, 'constructor', { value: undefined, writable: true, configurable: true }, () => Promise.resolve(1).then(v => v));
  say(n, a instanceof Promise);
  for (const bad of [null, 1, 'x', true]) { try { patched(Promise.prototype, 'constructor', { value: bad, writable: true, configurable: true }, () => Promise.resolve(1).then(v => v)); say(n, 'ok', bad); } catch (e) { say(n, 'threw', bad, e.name); } }
  return a;
});
scenario('prototype constructor throws', n => {
  try { patched(Promise.prototype, 'constructor', { get() { throw new RangeError('x'); }, configurable: true }, () => Promise.resolve(1).then(() => say(n, 'handler ran'))); } catch (e) { say(n, e.name); }
});
scenario('species patched', n => {
  const seen = [];
  class C extends Promise {}
  const g = patched(Promise, Symbol.species, { get() { seen.push('species'); return C; }, configurable: true }, () => Promise.resolve(1).then(v => v));
  say(n, g instanceof C, seen.join());
  const h = patched(Promise, Symbol.species, { value: undefined, configurable: true }, () => Promise.resolve(1).then(v => v));
  say(n, h instanceof Promise, h instanceof C);
  const i = patched(Promise, Symbol.species, { value: null, configurable: true }, () => Promise.resolve(1).then(v => v));
  say(n, i instanceof Promise);
  try { patched(Promise, Symbol.species, { value: 1, configurable: true }, () => Promise.resolve(1).then(v => v)); say(n, 'no throw'); } catch (e) { say(n, e.name); }
  try { patched(Promise, Symbol.species, { value: () => {}, configurable: true }, () => Promise.resolve(1).then(v => v)); say(n, 'no throw'); } catch (e) { say(n, e.name); }
  return Promise.all([g, h, i]).then(v => say(n, v.join()));
});
scenario('species capability shapes', n => {
  const t = (name, C) => { try { const p = Promise.resolve(1); p.constructor = { [Symbol.species]: C }; const r = p.then(v => v); say(n, name, typeof r); return r; } catch (e) { say(n, name, e.name, e.message.length > 0); } };
  const o = [];
  t('not callable resolve', function (e) { e(1, 2); });
  t('never calls', function () {});
  t('twice', function (e) { e(undefined, undefined); e(() => {}, () => {}); return {}; });
  t('twice bad', function (e) { e(() => {}, () => {}); e(() => {}, () => {}); });
  const r = t('records', function (e) { e(v => { o.push('res', v); }, v => { o.push('rej', v); }); this.tag = 1; });
  say(n, r.tag);
  const p2 = Promise.reject(4); p2.constructor = { [Symbol.species]: function (e) { e(v => { o.push('res', v); }, v => { o.push('rej', v); }); } }; p2.then();
  return Promise.resolve().then(() => {}).then(() => {}).then(() => say(n, o.join()));
});
scenario('own constructor and accessors', n => {
  const seen = [];
  const p = Promise.resolve(1); Object.defineProperty(p, 'constructor', { get() { seen.push('own'); return Promise; }, configurable: true });
  p.then(); p.then(); say(n, seen.length);
  const q = Promise.resolve(1); q.constructor = undefined; say(n, q.then() instanceof Promise);
  const u = Promise.resolve(1); u.constructor = 5; try { u.then(); say(n, 'no'); } catch (e) { say(n, e.name); }
  const w = Promise.resolve(1); Object.setPrototypeOf(w, null); say(n, Promise.prototype.then.call(w) instanceof Promise, Object.getPrototypeOf(w));
  const x = Promise.resolve(1); Object.setPrototypeOf(x, Object.create(Promise.prototype)); say(n, x.then() instanceof Promise);
  const z = Promise.resolve(1); Object.setPrototypeOf(z, { constructor: { [Symbol.species]: class extends Promise { get tag() { return 'z'; } } }, then: Promise.prototype.then }); say(n, z.then().tag);
  const fz = Object.freeze(Promise.resolve(1)); return fz.then(v => say(n, 'frozen', v));
});
scenario('subclass', n => {
  const seen = [];
  class A extends Promise { constructor(e) { seen.push('new A'); super(e); } then(f, r) { seen.push('then A'); return super.then(f, r); } static get [Symbol.species]() { seen.push('species A'); return this; } }
  class B extends Promise { static get [Symbol.species]() { return Promise; } }
  const a = A.resolve(1).then(v => v + 1).then(v => { seen.push(v); return v; });
  const b = new B(r => r(2)); const bd = b.then(v => v); seen.push(bd instanceof B, bd instanceof Promise);
  const c = a.catch(() => {}); const d = a.finally(() => seen.push('fin'));
  return Promise.all([a, bd, c, d]).then(v => { say(n, v.join()); say(n, seen.join()); });
});
scenario('then overridden', n => {
  const o = [];
  const orig = Promise.prototype.then;
  let d;
  try {
    Promise.prototype.then = function (f, r) { o.push('patched'); return orig.call(this, f, r); };
    Promise.resolve(1).catch(() => {}); o.push('a');
    Promise.resolve(1).finally(() => {}); o.push('b');
    new Promise(r => r(Promise.resolve(1))); o.push('c');
    Promise.all([Promise.resolve(1)]); o.push('d');
    Promise.race([Promise.resolve(1)]); o.push('e');
    (async () => { await Promise.resolve(1); o.push('await'); })(); o.push('f');
    d = Promise.resolve().then(() => Promise.resolve(2)); o.push('g');
  } finally { /* the jobs above run later, while the patch is gone or still there, as in the spec */ }
  return orig.call(Promise.resolve(), () => {}).then(() => { Promise.prototype.then = orig; }).then(() => orig.call(d, v => {})).then(() => say(n, o.join()));
});
scenario('then replaced by non function', n => {
  const orig = Promise.prototype.then; const p = Promise.resolve(1);
  try { Promise.prototype.then = 5; const d = new Promise(r => r(p)); return orig.call(d, v => say(n, v === p)).then(() => { Promise.prototype.then = orig; }); } catch (e) { Promise.prototype.then = orig; say(n, e.name); }
});
scenario('then deleted', n => {
  const d0 = Object.getOwnPropertyDescriptor(Promise.prototype, 'then'); const orig = d0.value; const p = Promise.resolve(1);
  delete Promise.prototype.then; say(n, typeof p.then);
  const d = new Promise(r => r(p));
  Object.defineProperty(Promise.prototype, 'then', d0);
  return d.then(v => say(n, v === p, v));
});
scenario('then getter', n => {
  let reads = 0; const orig = Object.getOwnPropertyDescriptor(Promise.prototype, 'then');
  Object.defineProperty(Promise.prototype, 'then', { get() { reads++; return orig.value; }, configurable: true });
  const p = Promise.resolve(1); const d = new Promise(r => r(p)); const c = p.catch(() => {});
  (async () => { await p; })();
  const seen = reads;
  Object.defineProperty(Promise.prototype, 'then', orig);
  return d.then(() => say(n, seen, reads));
});
scenario('then getter throws', n => {
  const p = Promise.resolve(1); Object.defineProperty(p, 'then', { get() { throw new RangeError('g'); } });
  return new Promise(r => r(p)).catch(e => say(n, e.name));
});
scenario('own then on resolution', n => {
  const o = []; const p = Promise.resolve(1); p.then = (f, r) => { o.push('own', typeof f, typeof r); f(9); };
  const d = new Promise(r => r(p));
  return d.then(v => say(n, v, o.join()));
});
scenario('then receivers', n => {
  const then = Promise.prototype.then;
  for (const [k, v] of [['undefined', undefined], ['object', {}], ['number', 1], ['thenable', { then() {} }], ['proxy', new Proxy(Promise.resolve(), {})], ['subclass-less', Object.create(Promise.prototype)]]) {
    try { then.call(v); say(n, k, 'ok'); } catch (e) { say(n, k, e.name); }
  }
  try { Promise.resolve().then.call(new Proxy(Promise.resolve(), {})); say(n, 'no'); } catch (e) { say(n, e.name); }
  say(n, typeof new Proxy(Promise.resolve(), {}).then);
});
scenario('patched then restored', n => {
  const orig = Promise.prototype.then; const o = [];
  Promise.prototype.then = function (f, r) { o.push('x'); return orig.call(this, f, r); };
  Promise.prototype.then = orig;
  return Promise.resolve(1).then(v => { o.push(v); }).then(() => say(n, o.join()));
});
scenario('unrelated prototype property', n => {
  Promise.prototype.someOther = 1;
  return Promise.resolve(1).then(v => v + Promise.resolve().someOther).then(v => { delete Promise.prototype.someOther; say(n, v); });
});
scenario('withResolvers', n => {
  const { promise, resolve, reject } = Promise.withResolvers();
  const o = [promise.then(v => v + 1), promise.then(v => v + 2)];
  resolve(1); reject(9); resolve(5);
  say(n, resolve.length, resolve.name === '', reject.length);
  return Promise.all(o).then(v => say(n, v.join()));
});
scenario('settled then alternation', n => {
  const o = []; const a = Promise.resolve('a'), b = Promise.reject('b');
  for (let i = 0; i < 40; i++) { (i % 2 ? b : a).then(v => o.push(v + i), v => o.push('r' + v + i)); }
  return Promise.resolve().then(() => {}).then(() => say(n, o.join()));
});

const run = async () => {
  for (const [name, f] of queue) {
    try { await f(name); } catch (e) { say(name, 'threw', e); }
    // let everything the scenario queued, its ticker included, finish before the next
    let p = Promise.resolve(); for (let i = 0; i < 40; i++) p = p.then(() => {});
    await p;
  }
  say('done');
};
run();
""";

    private const string PromiseJobsExpected = """
await primitive a @ 1
await primitive b @ 2
await native a @ 1
await pending native a @ 1
await thenable then @ 1
await thenable a @ 2
await then derived a @ 2
await patched own constructor a @ 3
await constructor getter getter @ 0
await constructor getter a @ 1
await subclass a @ 3
async return value a @ 1
async return promise a @ 3
async return thenable a @ 2
resolve with promise a @ 3
resolve with derived a @ 3
handler returns promise a @ 4
handler returns pending then settled a @ 4
handler returns thenable then @ 2
handler returns thenable a @ 3
pass through a @ 5
rejection through a @ 3
finally ticks f @ 1
finally ticks a @ 4
finally rejected f @ 1
finally rejected a @ 4
all race any settled all @ 2
all race any settled race @ 2
all race any settled any @ 2
all race any settled settled @ 2
async generator start @ 0
async generator v @ 2
async generator x @ 2
async generator v @ 4
async generator v @ 6
for await v @ 2
for await v @ 4
for await v @ 7
interleave a1,sync,a2,b1,q1,a3,b2,b3
reaction order pending 0,1,2,3,4,fin,late1,n0,n1,n2,n3,n4
rejected order pending r0,r1,r2,c
burst 1 true 1 217
burst 15 true 15 394174
burst 16 true 16 219373
burst 17 true 17 800561
burst 31 true 31 400996
burst 33 true 33 356994
burst 2047 true 2047 131749
burst 2048 true 2048 86254
burst 2049 true 2049 675916
burst 4097 true 4097 240543
burst 10000 true 10000 733
burst 20000 true 20000 928452
burst pending first 5000
burst pending 5000 614196
ping pong 9006 72494
growing fan out 8191 581512
long chain 12497500
deep await chain 300
handler call true,1,7,1,undefined
sloppy handler this true 1
handler kinds bound 1,2,3
handler kinds class TypeError
handler kinds async 9
handler kinds gen function
handler kinds native 65
handler kinds proxy 11
handler kinds proxy throws RangeError
handler kinds Promise as handler TypeError
handler throws r 1
handler throws c 2
handler throws v 3
both handlers one runs 1 10
cycle TypeError true
derived identity true true true 0 0 0 [object Promise] 2 then
derived mutated while pending own then,true,d1,2,5,d2,3,true,d3,4,d4,5,d9,2,d11,11,d5,6,d8,1
then many on derived while pending 870456 40
resolve derived with itself chained b,1 4
prototype constructor patched 2 C,true,C,C,C
prototype constructor patched after false
prototype constructor accessor 2
prototype constructor undefined and bad true
prototype constructor undefined and bad threw null TypeError
prototype constructor undefined and bad threw 1 TypeError
prototype constructor undefined and bad threw x TypeError
prototype constructor undefined and bad threw true TypeError
prototype constructor throws RangeError
species patched true species
species patched true false
species patched true
species patched TypeError
species patched TypeError
species patched 1,1,1
species capability shapes not callable resolve TypeError true
species capability shapes never calls TypeError true
species capability shapes twice object
species capability shapes twice bad TypeError true
species capability shapes records object
species capability shapes 1
species capability shapes res,1,rej,4
own constructor and accessors 2
own constructor and accessors true
own constructor and accessors TypeError
own constructor and accessors true null
own constructor and accessors true
own constructor and accessors z
own constructor and accessors frozen 1
subclass 2,2,2,2
subclass new A,then A,species A,new A,then A,species A,new A,false,true,then A,species A,new A,species A,then A,species A,new A,then A,species A,new A,then A,species A,new A,then A,species A,new A,2,fin,new A,then A,species A,new A,then A,species A,new A
then overridden patched,a,patched,b,c,patched,d,patched,e,f,patched,g,patched,patched,patched,patched,patched,await,patched,patched
then replaced by non function TypeError
then replaced by non function true
then deleted undefined
then deleted true [object Promise]
then getter 2 2
then getter throws RangeError
own then on resolution 9 own,function,function
then receivers undefined TypeError
then receivers object TypeError
then receivers number TypeError
then receivers thenable TypeError
then receivers proxy TypeError
then receivers subclass-less TypeError
then receivers TypeError
then receivers function
patched then restored 1
unrelated prototype property 2
withResolvers 1 true 1
withResolvers 2,3
settled then alternation a0,rb1,a2,rb3,a4,rb5,a6,rb7,a8,rb9,a10,rb11,a12,rb13,a14,rb15,a16,rb17,a18,rb19,a20,rb21,a22,rb23,a24,rb25,a26,rb27,a28,rb29,a30,rb31,a32,rb33,a34,rb35,a36,rb37,a38,rb39
done
""";

    [Fact]
    public void PromiseThenAndJobOrderingMatchReferenceBehavior()
    {
        using var engine = new JsEngine();
        engine.Evaluate(PromiseJobsScript);
        var got = engine.Evaluate("__log.join('\\n')").AsString().Split('\n');
        var expected = PromiseJobsExpected.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        for (int i = 0; i < Math.Min(expected.Length, got.Length); i++) Assert.True(expected[i] == got[i], $"line {i}: expected {expected[i]} but got {got[i]}");
        Assert.Equal(expected.Length, got.Length);
    }
}
