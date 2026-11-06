// Small ray tracer: Vec3 class (allocation heavy), spheres, shadows, reflection.
class V { constructor(x, y, z) { this.x = x; this.y = y; this.z = z; } add(o) { return new V(this.x + o.x, this.y + o.y, this.z + o.z); } sub(o) { return new V(this.x - o.x, this.y - o.y, this.z - o.z); } mul(k) { return new V(this.x * k, this.y * k, this.z * k); } dot(o) { return this.x * o.x + this.y * o.y + this.z * o.z; } norm() { return this.mul(1 / Math.sqrt(this.dot(this))); } }
class Sphere { constructor(c, r, col, refl) { this.c = c; this.r = r; this.col = col; this.refl = refl; } hit(o, d) { const oc = o.sub(this.c), b = oc.dot(d), c = oc.dot(oc) - this.r * this.r, disc = b * b - c; if (disc < 0) return Infinity; const t = -b - Math.sqrt(disc); return t > 1e-4 ? t : Infinity; } }
const scene = [new Sphere(new V(0, -1000, 0), 999, new V(.5, .5, .5), .2), new Sphere(new V(0, 0, 4), 1, new V(1, .2, .2), .4), new Sphere(new V(2, 0.3, 5), 1.3, new V(.2, .2, 1), .5), new Sphere(new V(-2, 0, 3.5), .8, new V(.2, 1, .2), .1)];
const light = new V(5, 8, -3);
function trace(o, d, depth) {
  let best = Infinity, obj = null;
  for (const s of scene) { const t = s.hit(o, d); if (t < best) { best = t; obj = s; } }
  if (!obj) return new V(.1, .1, .2);
  const p = o.add(d.mul(best)), n = p.sub(obj.c).norm(), l = light.sub(p).norm();
  let shadow = false; for (const s of scene) if (s !== obj && s.hit(p, l) < Infinity) { shadow = true; break; }
  let col = obj.col.mul(shadow ? 0.2 : Math.max(0.2, n.dot(l)));
  if (depth > 0 && obj.refl > 0) { const r = d.sub(n.mul(2 * d.dot(n))); col = col.mul(1 - obj.refl).add(trace(p, r, depth - 1).mul(obj.refl)); }
  return col;
}
function main() {
  const W = 40, H = 30; let sum = 0;
  for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) { const c = trace(new V(0, 0.5, -3), new V((x - W / 2) / W, (H / 2 - y) / W, 1).norm(), 3); sum += Math.min(255, c.x * 255 | 0) + Math.min(255, c.y * 255 | 0) * 3 + Math.min(255, c.z * 255 | 0) * 7; }
  return String(sum);
}
