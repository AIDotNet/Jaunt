// Array builtins: push/pop/shift/splice/slice/concat/flat/flatMap/includes/indexOf/at/fill/copyWithin/reverse/toSorted.
function main() {
  const a = [];
  for (let i = 0; i < 6000; i++) a.push(i * 3 % 1013);
  let t = 0;
  for (let i = 0; i < 500; i++) { t += a.pop() + a.shift(); a.unshift(i); a.splice(100, 1, i, i + 1); a.splice(200, 2); }
  const b = a.slice(10, 3000).concat([1, 2, 3], a.slice(0, 50));
  const c = [[1, [2, 3]], [4], [[5, [6]]]].flat(Infinity).concat(b.flatMap(x => x % 5 === 0 ? [x, x] : []));
  const d = b.toSorted((x, y) => y - x).toReversed();
  t += c.length + d[0] + d.at(-1) + (b.indexOf(500) + 1) + (b.includes(1012) ? 1 : 0) + b.lastIndexOf(7);
  const e = new Array(2000).fill(0).map((_, i) => i).copyWithin(0, 1000, 1500).reverse();
  t += e[0] + e[1999] + (e.findLast(x => x < 10) ?? 0) + e.findIndex(x => x === 7);
  return [a.length, b.length, c.length, t, Object.groupBy(b.slice(0, 500), x => x % 3 === 0 ? 'z' : 'o').z.length].join(',');
}
