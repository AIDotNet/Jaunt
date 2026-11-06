// ES2025 iterator helpers: Iterator.from / map / filter / take / drop / flatMap / reduce / toArray (not implemented by moejs yet).
function* range(a, b) { for (let i = a; i < b; i++) yield i; }
function main() {
  let t = Iterator.from(range(0, 50000)).map(x => x * 2).filter(x => x % 3 === 0).take(5000).reduce((a, b) => a + b, 0);
  t += range(0, 3000).drop(10).flatMap(x => [x, x + 1]).toArray().length + range(0, 4000).some(x => x === 3999);
  return String(t);
}
