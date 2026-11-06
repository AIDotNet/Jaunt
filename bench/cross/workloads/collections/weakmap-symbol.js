// WeakMap/WeakSet metadata, Symbols as keys, Symbol.iterator/toPrimitive, well-known symbols.
const meta = new WeakMap(), seen = new WeakSet();
const ID = Symbol('id');
class Temp { constructor(c) { this.c = c; this[ID] = c * 2; } [Symbol.toPrimitive](hint) { return hint === 'string' ? `${this.c}C` : this.c; } *[Symbol.iterator]() { yield this.c; yield this[ID]; } }
function main() {
  let t = 0;
  const objs = [];
  for (let i = 0; i < 4000; i++) { const o = new Temp(i); objs.push(o); meta.set(o, { n: i }); if (i % 2) seen.add(o); }
  for (const o of objs) { t += meta.get(o).n + (seen.has(o) ? 1 : 0) + +o + `${o}`.length + [...o].length + o[ID]; }
  const reg = Symbol.for('app.key');
  return [t, Symbol.keyFor(reg), Object.getOwnPropertySymbols(objs[0]).length, typeof Symbol.asyncIterator].join(',');
}
