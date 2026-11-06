// Array.prototype.sort with comparators, numeric and by key.
function main() {
  const n = 20000, a = new Array(n);
  let x = 2463534242;
  for (let i = 0; i < n; i++) { x ^= x << 13; x ^= x >>> 17; x ^= x << 5; x >>>= 0; a[i] = x % 1000000; }
  const b = a.slice().sort((p, q) => p - q);
  const objs = a.slice(0, 5000).map((v, i) => ({ k: v % 100, i }));
  objs.sort((p, q) => p.k - q.k || p.i - q.i);
  const strs = a.slice(0, 3000).map(v => 'k' + v).sort();
  let s = 0;
  for (let i = 0; i < n; i += 7) s = (s + b[i]) % 1000003;
  return s + ':' + objs[0].i + ':' + objs[4999].i + ':' + strs[0] + ':' + strs[2999];
}
