// Classic n-body: object property reads/writes in float-heavy loops.
const PI = Math.PI, SOLAR_MASS = 4 * PI * PI, DPY = 365.24;
function Body(x, y, z, vx, vy, vz, mass) { this.x = x; this.y = y; this.z = z; this.vx = vx; this.vy = vy; this.vz = vz; this.mass = mass; }
function bodies() {
  return [
    new Body(0, 0, 0, 0, 0, 0, SOLAR_MASS),
    new Body(4.84143144246472090e+00, -1.16032004402742839e+00, -1.03622044471123109e-01, 1.66007664274403694e-03 * DPY, 7.69901118419740425e-03 * DPY, -6.90460016972063023e-05 * DPY, 9.54791938424326609e-04 * SOLAR_MASS),
    new Body(8.34336671824457987e+00, 4.12479856412430479e+00, -4.03523417114321381e-01, -2.76742510726862411e-03 * DPY, 4.99852801234917238e-03 * DPY, 2.30417297573763929e-05 * DPY, 2.85885980666130812e-04 * SOLAR_MASS),
    new Body(1.28943695621391310e+01, -1.51111514016986312e+01, -2.23307578892655734e-01, 2.96460137564761618e-03 * DPY, 2.37847173959480950e-03 * DPY, -2.96589568540237556e-05 * DPY, 4.36624404335156298e-05 * SOLAR_MASS),
    new Body(1.53796971148509165e+01, -2.59193146099879641e+01, 1.79258772950371181e-01, 2.68067772490389322e-03 * DPY, 1.62824170038242295e-03 * DPY, -9.51592254519715870e-05 * DPY, 5.15138902046611451e-05 * SOLAR_MASS),
  ];
}
function offset(b) { let px = 0, py = 0, pz = 0; for (const o of b) { px += o.vx * o.mass; py += o.vy * o.mass; pz += o.vz * o.mass; } b[0].vx = -px / SOLAR_MASS; b[0].vy = -py / SOLAR_MASS; b[0].vz = -pz / SOLAR_MASS; }
function advance(b, dt) {
  const n = b.length;
  for (let i = 0; i < n; i++) {
    const bi = b[i];
    for (let j = i + 1; j < n; j++) {
      const bj = b[j];
      const dx = bi.x - bj.x, dy = bi.y - bj.y, dz = bi.z - bj.z;
      const d2 = dx * dx + dy * dy + dz * dz, mag = dt / (d2 * Math.sqrt(d2));
      bi.vx -= dx * bj.mass * mag; bi.vy -= dy * bj.mass * mag; bi.vz -= dz * bj.mass * mag;
      bj.vx += dx * bi.mass * mag; bj.vy += dy * bi.mass * mag; bj.vz += dz * bi.mass * mag;
    }
  }
  for (const o of b) { o.x += dt * o.vx; o.y += dt * o.vy; o.z += dt * o.vz; }
}
function energy(b) {
  let e = 0;
  for (let i = 0; i < b.length; i++) {
    const bi = b[i];
    e += 0.5 * bi.mass * (bi.vx * bi.vx + bi.vy * bi.vy + bi.vz * bi.vz);
    for (let j = i + 1; j < b.length; j++) { const bj = b[j]; const dx = bi.x - bj.x, dy = bi.y - bj.y, dz = bi.z - bj.z; e -= bi.mass * bj.mass / Math.sqrt(dx * dx + dy * dy + dz * dz); }
  }
  return e;
}
function main() { const b = bodies(); offset(b); for (let i = 0; i < 5000; i++) advance(b, 0.01); return energy(b).toFixed(9); }
