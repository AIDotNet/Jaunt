// Generators, custom iterables, spread of iterables, yield*, return/throw.
function* range(a, b) { for (let i = a; i < b; i++) yield i; }
function* mapG(it, f) { for (const x of it) yield f(x); }
function* filterG(it, f) { for (const x of it) if (f(x)) yield x; }
function* takeG(it, n) { if (n <= 0) return; for (const x of it) { yield x; if (--n <= 0) return; } }
function* tree(d) { if (d === 0) { yield 1; return; } yield* tree(d - 1); yield d; yield* tree(d - 1); }
class Coll { constructor(n) { this.n = n; } [Symbol.iterator]() { let i = 0, n = this.n; return { next: () => i < n ? { value: i++, done: false } : { value: undefined, done: true } }; } }
function main() {
  let t = 0;
  for (const x of takeG(filterG(mapG(range(0, 1e9), x => x * 3), x => x % 2 === 0), 8000)) t += x;
  for (const v of tree(11)) t += v;
  t += [...new Coll(5000)].length + Math.max(...new Coll(3000));
  const gen = (function* () { try { const x = yield 1; yield x * 2; } finally { t += 1000; } })();
  gen.next(); t += gen.next(21).value; gen.return(0);
  return String(t);
}
