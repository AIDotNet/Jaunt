// Map / Set with number, string, and object keys; iteration order; delete.
function main() {
  const m = new Map(), s = new Set(), om = new Map(), keys = [];
  for (let i = 0; i < 8000; i++) { m.set(i * 31 % 8191, i); s.add('k' + (i % 3000)); const k = { i }; keys.push(k); om.set(k, i); }
  let t = 0;
  for (let i = 0; i < 8191; i++) if (m.has(i)) t += m.get(i);
  for (let i = 0; i < 8000; i += 2) m.delete(i * 31 % 8191);
  for (const [k, v] of m) t += (k ^ v) & 15;
  for (const v of s) t += v.length;
  for (let i = 0; i < 8000; i += 5) t += om.get(keys[i]);
  const u = new Set([...s].filter(x => x.endsWith('7')));
  const inter = [...u].filter(x => s.has(x)).length;
  return [m.size, s.size, om.size, t, u.size, inter].join(',');
}
