// Getters/setters, Object.defineProperty, freeze, descriptors.
function main() {
  let total = 0;
  for (let i = 0; i < 4000; i++) {
    let backing = i;
    const o = { get v() { return backing; }, set v(x) { backing = x * 2; } };
    o.v = o.v + 1;
    Object.defineProperty(o, 'k', { value: i, enumerable: false });
    const f = Object.freeze({ a: i, b: [i] });
    const d = Object.getOwnPropertyDescriptor(o, 'v');
    total += o.v + o.k + f.a + (Object.isFrozen(f) ? 1 : 0) + (typeof d.get === 'function' ? 1 : 0) + Object.getOwnPropertyNames(o).length;
  }
  return String(total);
}
