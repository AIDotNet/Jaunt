// Optional catch, logical assignment, numeric separators, tagged templates, private in, static blocks, Array.at, Object.groupBy, Set methods.
class Cfg { static #inst; static { Cfg.#inst = new Cfg(); } #v = 1; static has(o) { return #v in o; } static get() { return Cfg.#inst; } }
const tag = (s, ...v) => s.raw.join('|') + v.join(',');
function main() {
  let t = 0;
  for (let i = 0; i < 3000; i++) {
    let a = null, b = 0, c = 5;
    a ??= i; b ||= 2; c &&= c + 1;
    t += a + b + c + 1_000;
    try { JSON.parse('{'); } catch { t++; }
    t += tag`x${i}y${i + 1}z`.length + [1, 2, 3].at(-1) + (Cfg.has(Cfg.get()) ? 1 : 0);
  }
  const sc = { a: [1, { b: new Map([[1, 2]]) }], s: new Set([1]) };
  const A = new Set([1, 2, 3, 4]), B = new Set([3, 4, 5]);
  return [t, sc.a[1].b.get(1), sc.s.size, [...A.union(B)].join(''), [...A.intersection(B)].join(''), [...A.difference(B)].join(''), Object.groupBy([1, 2, 3, 4], x => x % 2 ? 'o' : 'e').o.length, 'abc'.at(-1), Array.from({ length: 3 }, (_, i) => i).with(1, 9).join('')].join(',');
}
