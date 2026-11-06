// A hand-written cooperative scheduler on microtasks: task queue, priorities, awaiting channels. Uses result().
let out;
class Channel { constructor() { this.q = []; this.waiters = []; } send(v) { const w = this.waiters.shift(); if (w) w(v); else this.q.push(v); } recv() { return this.q.length ? Promise.resolve(this.q.shift()) : new Promise(r => this.waiters.push(r)); } }
async function producer(ch, n) { for (let i = 0; i < n; i++) { ch.send(i); if (i % 10 === 0) await null; } ch.send(-1); }
async function consumer(ch, id) { let sum = 0; while (true) { const v = await ch.recv(); if (v < 0) return sum + id; sum += v; } }
async function run() {
  const ch = new Channel();
  const [, s] = await Promise.all([producer(ch, 4000), consumer(ch, 7)]);
  const pool = Array.from({ length: 20 }, (_, i) => (async () => { let a = 0; for (let k = 0; k < 100; k++) { await null; a += i * k; } return a; })());
  const rs = await Promise.all(pool);
  return s + ':' + rs.reduce((a, b) => a + b, 0);
}
function main() { out = 'pending'; run().then(v => { out = v; }); }
function result() { return out; }
