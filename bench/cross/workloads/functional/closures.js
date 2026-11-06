// Closures: counters, memoize, curry/compose, IIFE modules, captured loop vars.
function memo(fn) { const c = new Map(); return n => c.has(n) ? c.get(n) : (c.set(n, fn(n)), c.get(n)); }
const compose = (...fs) => x => fs.reduceRight((a, f) => f(a), x);
const curry = fn => function c(...a) { return a.length >= fn.length ? fn(...a) : (...b) => c(...a, ...b); };
function main() {
  const counters = [];
  for (let i = 0; i < 2000; i++) { let n = i; counters.push(() => ++n); }
  let t = 0;
  for (let r = 0; r < 5; r++) for (const c of counters) t += c();
  const fibm = memo(n => n < 2 ? n : fibm(n - 1) + fibm(n - 2));
  const add3 = curry((a, b, c) => a + b + c);
  const pipeline = compose(x => x + 1, x => x * 2, x => x - 3);
  for (let i = 0; i < 6000; i++) t += pipeline(i) + add3(i)(1)(2) + add3(i, 1)(2);
  const mod = (() => { let priv = 0; return { inc: () => ++priv, get: () => priv }; })();
  for (let i = 0; i < 5000; i++) mod.inc();
  return [t, fibm(60), mod.get()].join(',');
}
