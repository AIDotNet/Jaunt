// Object literal / constructor allocation, property add/read, polymorphic shapes.
function P(x, y) { this.x = x; this.y = y; }
P.prototype.len2 = function () { return this.x * this.x + this.y * this.y; };
function main() {
  let total = 0;
  for (let i = 0; i < 15000; i++) {
    const p = new P(i, i + 1);
    const o = { a: p.x, b: p.y, c: i };
    if (i % 3 === 0) o.d = i; else if (i % 3 === 1) o.e = i;
    const q = i % 2 ? { ...o, z: 1 } : Object.assign({}, o);
    total += p.len2() % 7 + o.a - o.b + o.c + (q.d || 0) + (q.e || 0) + (q.z || 0);
  }
  return String(total);
}
