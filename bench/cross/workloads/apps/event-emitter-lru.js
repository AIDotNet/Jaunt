// App-style infrastructure: EventEmitter with once/off, LRU cache on Map, observable pipeline, dependency injection container.
class Emitter { #h = new Map(); on(e, f) { (this.#h.get(e) ?? this.#h.set(e, []).get(e)).push(f); return () => this.off(e, f); } once(e, f) { const w = (...a) => { this.off(e, w); f(...a); }; return this.on(e, w); } off(e, f) { const l = this.#h.get(e); if (l) { const i = l.indexOf(f); if (i >= 0) l.splice(i, 1); } } emit(e, ...a) { const l = this.#h.get(e); if (!l) return 0; for (const f of [...l]) f(...a); return l.length; } }
class LRU { constructor(n) { this.n = n; this.m = new Map(); this.hits = 0; this.miss = 0; } get(k) { if (!this.m.has(k)) { this.miss++; return undefined; } const v = this.m.get(k); this.m.delete(k); this.m.set(k, v); this.hits++; return v; } set(k, v) { this.m.delete(k); this.m.set(k, v); if (this.m.size > this.n) this.m.delete(this.m.keys().next().value); } }
class Container { #f = new Map(); #i = new Map(); reg(n, f) { this.#f.set(n, f); return this; } get(n) { if (!this.#i.has(n)) this.#i.set(n, this.#f.get(n)(this)); return this.#i.get(n); } }
function main() {
  const em = new Emitter(); let t = 0;
  for (let i = 0; i < 50; i++) em.on('tick', v => { t += v + i; });
  em.once('tick', v => { t += 1000; });
  for (let i = 0; i < 400; i++) t += em.emit('tick', i);
  const lru = new LRU(256); let x = 3;
  for (let i = 0; i < 20000; i++) { x = (x * 1103515245 + 12345) & 0x7fffffff; const k = (x >> 8) % (i % 5 ? 300 : 3000); if (lru.get(k) === undefined) lru.set(k, k * 2); }
  const c = new Container().reg('cfg', () => ({ base: 10 })).reg('svc', c => ({ f: v => v + c.get('cfg').base })).reg('app', c => ({ run: v => c.get('svc').f(v) }));
  for (let i = 0; i < 5000; i++) t += c.get('app').run(i);
  return [t, lru.hits, lru.miss, lru.m.size].join(',');
}
