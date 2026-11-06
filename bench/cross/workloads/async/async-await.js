// async/await functions, sequential/parallel awaits, try/catch around await, for await, async generators. Uses result().
let out;
const tick = v => Promise.resolve(v);
async function step(i) { const a = await tick(i); const b = await tick(a + 1); return a + b; }
async function failing(i) { await tick(0); if (i % 4 === 0) throw new Error('e' + i); return i; }
async function* agen(n) { for (let i = 0; i < n; i++) { await tick(0); yield i; } }
async function run() {
  let t = 0;
  for (let i = 0; i < 1500; i++) t += await step(i);
  t += (await Promise.all(Array.from({ length: 300 }, (_, i) => step(i)))).reduce((a, b) => a + b, 0);
  let errs = 0;
  for (let i = 0; i < 800; i++) { try { t += await failing(i); } catch (e) { errs++; } }
  for await (const v of agen(1000)) t += v;
  return t + ':' + errs;
}
function main() { out = 'pending'; run().then(v => { out = v; }); }
function result() { return out; }
