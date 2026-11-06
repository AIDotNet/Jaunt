// map/filter/reduce/some/every/find/forEach/flatMap chains on mid-size arrays with arrow closures.
function main() {
  const data = Array.from({ length: 10000 }, (_, i) => ({ id: i, g: i % 17, v: (i * 37) % 101, tags: ['a', 'b', 'c'].slice(0, i % 4) }));
  let t = 0;
  for (let r = 0; r < 3; r++) {
    const groups = data.filter(d => d.v > 20).reduce((acc, d) => { (acc[d.g] ||= []).push(d.v); return acc; }, {});
    const avg = Object.values(groups).map(a => a.reduce((s, x) => s + x, 0) / a.length);
    t += avg.reduce((s, x) => s + x, 0) + data.flatMap(d => d.tags).length + (data.some(d => d.v === 100) ? 1 : 0) + (data.every(d => d.v >= 0) ? 1 : 0) + data.find(d => d.v === 99).id;
    data.forEach(d => { t += d.g; });
  }
  return t.toFixed(4);
}
