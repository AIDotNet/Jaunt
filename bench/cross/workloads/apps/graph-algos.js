// Graph algorithms with Map/Set/arrays: BFS, DFS topological sort, Dijkstra (linear-scan PQ), union-find, connected components.
function main() {
  const N = 1500, adj = Array.from({ length: N }, () => []), edges = []; let x = 17;
  const rnd = () => (x = (x * 1103515245 + 12345) & 0x7fffffff);
  for (let i = 0; i < N * 3; i++) { const a = rnd() % N, b = rnd() % N, w = rnd() % 20 + 1; if (a !== b) { adj[a].push([b, w]); adj[b].push([a, w]); edges.push([a, b, w]); } }
  const dist = new Array(N).fill(Infinity); dist[0] = 0; const q = [0], seen = new Set([0]);
  for (let qi = 0; qi < q.length; qi++) for (const [v] of adj[q[qi]]) if (!seen.has(v)) { seen.add(v); q.push(v); }
  const D = new Array(N).fill(Infinity), done = new Uint8Array(N); D[0] = 0;
  for (let it = 0; it < N; it++) { let u = -1; for (let i = 0; i < N; i++) if (!done[i] && (u < 0 || D[i] < D[u])) u = i; if (D[u] === Infinity) break; done[u] = 1; for (const [v, w] of adj[u]) if (D[u] + w < D[v]) D[v] = D[u] + w; }
  const parent = Array.from({ length: N }, (_, i) => i), find = a => { while (parent[a] !== a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; };
  edges.sort((a, b) => a[2] - b[2]); let mst = 0, merged = 0;
  for (const [a, b, w] of edges) { const ra = find(a), rb = find(b); if (ra !== rb) { parent[ra] = rb; mst += w; merged++; } }
  const dag = Array.from({ length: 400 }, (_, i) => []); for (let i = 0; i < 1200; i++) { const a = rnd() % 400, b = rnd() % 400; if (a < b) dag[a].push(b); }
  const vis = new Uint8Array(400), order = []; const dfs = u => { vis[u] = 1; for (const v of dag[u]) if (!vis[v]) dfs(v); order.push(u); };
  for (let i = 0; i < 400; i++) if (!vis[i]) dfs(i);
  return [seen.size, D.filter(d => d < Infinity).reduce((a, b) => a + b, 0), mst, merged, order.slice(0, 5).join('-')].join(',');
}
