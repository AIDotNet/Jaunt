// Legacy prototype inheritance, Object.create, method lookup depth, call/apply/bind.
function Base(n) { this.n = n; }
Base.prototype.inc = function (d) { this.n += d; return this; };
Base.prototype.get = function () { return this.n; };
function Mid(n) { Base.call(this, n); }
Mid.prototype = Object.create(Base.prototype); Mid.prototype.constructor = Mid;
Mid.prototype.dbl = function () { this.n *= 2; return this; };
function Leaf(n) { Mid.call(this, n); }
Leaf.prototype = Object.create(Mid.prototype); Leaf.prototype.constructor = Leaf;
Leaf.prototype.get = function () { return Mid.prototype.get.call(this) + 1; };
function main() {
  let t = 0;
  const bound = Base.prototype.inc.bind(null);
  for (let i = 0; i < 20000; i++) {
    const l = new Leaf(i);
    l.inc(1).dbl().inc(2);
    t = (t + l.get()) % 1000003;
    Base.prototype.inc.apply(l, [3]);
    t = (t + l.n) % 1000003;
    if (i % 100 === 0) bound.call(l, 1);
  }
  return String(t);
}
