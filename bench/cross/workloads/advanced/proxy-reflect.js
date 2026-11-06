// Proxy traps (get/set/has/deleteProperty/ownKeys/apply/construct) and Reflect, e.g. reactive/validating wrappers.
function reactive(target, log) {
  return new Proxy(target, {
    get(t, k, r) { log.g++; const v = Reflect.get(t, k, r); return typeof v === 'object' && v !== null ? reactive(v, log) : v; },
    set(t, k, v, r) { log.s++; return Reflect.set(t, k, v, r); },
    has(t, k) { log.h++; return Reflect.has(t, k); },
    deleteProperty(t, k) { log.d++; return Reflect.deleteProperty(t, k); },
    ownKeys(t) { log.k++; return Reflect.ownKeys(t); },
  });
}
function main() {
  const log = { g: 0, s: 0, h: 0, d: 0, k: 0 };
  const state = reactive({ a: 1, nested: { b: 2, arr: [1, 2, 3] } }, log);
  let t = 0;
  for (let i = 0; i < 5000; i++) {
    state.a = i; t += state.a + state.nested.b + state.nested.arr[i % 3];
    state.nested.b = i % 5; if ('a' in state) t++;
    if (i % 100 === 0) { t += Object.keys(state.nested).length; delete state.tmp; }
  }
  const fn = new Proxy(function (a, b) { return a + b; }, { apply: (tg, th, args) => tg(...args) * 2, construct: (tg, args) => ({ made: args.length }) });
  for (let i = 0; i < 2000; i++) t += fn(i, 1) + new fn(1, 2, 3).made;
  const revocable = Proxy.revocable({}, {}); revocable.revoke();
  let revoked = false; try { revocable.proxy.x; } catch (e) { revoked = e instanceof TypeError; }
  return [t, log.g, log.s, log.h, log.d, log.k, revoked, Reflect.ownKeys({ a: 1, [Symbol.iterator]: 1 }).length].join(',');
}
