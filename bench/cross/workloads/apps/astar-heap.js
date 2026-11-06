// A* on a grid with a binary heap priority queue, Map/Set bookkeeping, and path reconstruction.
class Heap { constructor(cmp) { this.a = []; this.cmp = cmp; } push(x) { const a = this.a; a.push(x); let i = a.length - 1; while (i > 0) { const p = (i - 1) >> 1; if (this.cmp(a[i], a[p]) >= 0) break; [a[i], a[p]] = [a[p], a[i]]; i = p; } } pop() { const a = this.a, top = a[0], last = a.pop(); if (a.length) { a[0] = last; let i = 0; for (;;) { let l = 2 * i + 1, r = l + 1, m = i; if (l < a.length && this.cmp(a[l], a[m]) < 0) m = l; if (r < a.length && this.cmp(a[r], a[m]) < 0) m = r; if (m === i) break; [a[i], a[m]] = [a[m], a[i]]; i = m; } } return top; } get size() { return this.a.length; } }
function main() {
  const N = 70, grid = [], seed = { x: 42 };
  const rnd = () => (seed.x = (seed.x * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff;
  for (let y = 0; y < N; y++) { grid.push([]); for (let x = 0; x < N; x++) grid[y].push(rnd() < 0.25 ? 1 : 0); }
  grid[0][0] = grid[N - 1][N - 1] = 0;
  const key = (x, y) => y * N + x, h = (x, y) => Math.abs(N - 1 - x) + Math.abs(N - 1 - y);
  const open = new Heap((a, b) => a.f - b.f || a.g - b.g), g = new Map([[0, 0]]), came = new Map(), closed = new Set();
  open.push({ x: 0, y: 0, g: 0, f: h(0, 0) });
  let expanded = 0, found = -1;
  while (open.size) {
    const c = open.pop(), k = key(c.x, c.y);
    if (closed.has(k)) continue; closed.add(k); expanded++;
    if (c.x === N - 1 && c.y === N - 1) { found = c.g; break; }
    for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
      const nx = c.x + dx, ny = c.y + dy;
      if (nx < 0 || ny < 0 || nx >= N || ny >= N || grid[ny][nx]) continue;
      const nk = key(nx, ny), ng = c.g + 1;
      if (ng < (g.get(nk) ?? Infinity)) { g.set(nk, ng); came.set(nk, k); open.push({ x: nx, y: ny, g: ng, f: ng + h(nx, ny) }); }
    }
  }
  let len = 0; for (let k = key(N - 1, N - 1); came.has(k); k = came.get(k)) len++;
  return [found, expanded, len].join(',');
}
