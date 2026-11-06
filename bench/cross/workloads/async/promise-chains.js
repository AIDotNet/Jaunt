// Promise chains, Promise.all/allSettled/race, then/catch/finally, microtask ordering. Uses result().
let out;
function main() {
  out = { n: 0, order: [] };
  let p = Promise.resolve(0);
  for (let i = 0; i < 3000; i++) p = p.then(v => v + i);
  p.then(v => { out.n += v; });
  const all = Promise.all(Array.from({ length: 500 }, (_, i) => new Promise(r => r(i)).then(x => x * 2)));
  all.then(a => { out.n += a.reduce((s, x) => s + x, 0); });
  Promise.allSettled([Promise.reject(new Error('x')), Promise.resolve(1), 3]).then(r => { out.order.push(r.map(x => x.status[0]).join('')); });
  Promise.race([new Promise(r => r('fast')), new Promise(() => {})]).then(v => out.order.push(v));
  Promise.reject(new Error('boom')).catch(e => out.order.push(e.message)).finally(() => out.order.push('fin'));
  Promise.any([Promise.reject(1), Promise.resolve(2)]).then(v => out.order.push('any' + v));
  Promise.resolve().then(() => out.order.push('m1')); Promise.resolve().then(() => out.order.push('m2'));
}
function result() { return out.n + ':' + out.order.join(','); }
