// Classes: fields, private fields/methods, static, accessors, super calls, instanceof.
class Shape {
  static count = 0;
  #id;
  constructor(name) { this.name = name; this.#id = ++Shape.count; }
  get id() { return this.#id; }
  area() { return 0; }
  describe() { return `${this.name}#${this.#id}:${this.area().toFixed(1)}`; }
  static compare(a, b) { return a.area() - b.area(); }
}
class Circle extends Shape { #r; constructor(r) { super('circle'); this.#r = r; } area() { return Math.PI * this.#r ** 2; } get r() { return this.#r; } }
class Rect extends Shape { constructor(w, h) { super('rect'); this.w = w; this.h = h; } area() { return this.w * this.h; } }
class Square extends Rect { constructor(s) { super(s, s); this.name = 'square'; } #secret() { return this.w * 2; } perimeter() { return this.#secret() * 2; } }
function main() {
  Shape.count = 0;
  const shapes = [];
  for (let i = 1; i <= 3000; i++) shapes.push(i % 3 === 0 ? new Circle(i % 17 + 1) : i % 3 === 1 ? new Rect(i % 11 + 1, i % 5 + 1) : new Square(i % 13 + 1));
  shapes.sort(Shape.compare);
  let t = 0, sq = 0;
  for (const s of shapes) { t += s.area(); if (s instanceof Square) sq += s.perimeter(); }
  return shapes[0].describe() + '|' + shapes[2999].describe() + '|' + Math.round(t) + '|' + sq + '|' + Shape.count;
}
