// Backtracking sudoku solver with constraint sets and most-constrained-cell heuristic.
function solve(g) {
  const rows = Array.from({ length: 9 }, () => new Set()), cols = Array.from({ length: 9 }, () => new Set()), boxes = Array.from({ length: 9 }, () => new Set());
  for (let i = 0; i < 81; i++) if (g[i]) { const r = (i / 9) | 0, c = i % 9, b = ((r / 3) | 0) * 3 + ((c / 3) | 0); rows[r].add(g[i]); cols[c].add(g[i]); boxes[b].add(g[i]); }
  let nodes = 0;
  function rec() {
    let best = -1, bestOpts = null;
    for (let i = 0; i < 81; i++) if (!g[i]) {
      const r = (i / 9) | 0, c = i % 9, b = ((r / 3) | 0) * 3 + ((c / 3) | 0), opts = [];
      for (let v = 1; v <= 9; v++) if (!rows[r].has(v) && !cols[c].has(v) && !boxes[b].has(v)) opts.push(v);
      if (!opts.length) return false;
      if (!bestOpts || opts.length < bestOpts.length) { best = i; bestOpts = opts; if (opts.length === 1) break; }
    }
    if (best < 0) return true;
    const r = (best / 9) | 0, c = best % 9, b = ((r / 3) | 0) * 3 + ((c / 3) | 0);
    for (const v of bestOpts) { nodes++; g[best] = v; rows[r].add(v); cols[c].add(v); boxes[b].add(v); if (rec()) return true; g[best] = 0; rows[r].delete(v); cols[c].delete(v); boxes[b].delete(v); }
    return false;
  }
  return rec() ? nodes : -1;
}
function main() {
  const puzzles = ['530070000600195000098000060800060003400803001700020006060000280000419005000080079', '000000907000420180000705026100904000050000040000507009920108000034059000507000000', '800000000003600000070090200050007000000045700000100030001000068008500010090000400'];
  let out = [];
  for (const p of puzzles) { const g = [...p].map(Number); const n = solve(g); out.push(n + ':' + g.slice(0, 9).join('')); }
  return out.join('|');
}
