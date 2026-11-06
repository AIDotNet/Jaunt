// eval / new Function / Function.prototype.toString / indirect eval / with-free dynamic evaluation.
function main() {
  let t = 0;
  for (let i = 0; i < 400; i++) {
    t += eval('(' + i + ' * 2 + 1)');
    const f = new Function('a', 'b', 'return a * b + ' + i);
    t += f(i, 2);
    const g = (0, eval)('(function (x) { return x + ' + i + ' })');
    t += g(1);
  }
  const fnSrc = (function sample(a, b) { return a + b; }).toString();
  const arrow = ((x) => x * 2).toString();
  return [t, fnSrc.length, arrow, typeof globalThis.eval, eval('var __v = 5; __v * 2')].join(',');
}
