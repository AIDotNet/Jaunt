// Function-call heavy float code.
function A(i, j) { return 1 / ((i + j) * (i + j + 1) / 2 + i + 1); }
function Au(u, v) { for (let i = 0; i < u.length; i++) { let t = 0; for (let j = 0; j < u.length; j++) t += A(i, j) * u[j]; v[i] = t; } }
function Atu(u, v) { for (let i = 0; i < u.length; i++) { let t = 0; for (let j = 0; j < u.length; j++) t += A(j, i) * u[j]; v[i] = t; } }
function AtAu(u, v, w) { Au(u, w); Atu(w, v); }
function main() {
  const n = 60, u = new Float64Array(n).fill(1), v = new Float64Array(n), w = new Float64Array(n);
  for (let i = 0; i < 6; i++) { AtAu(u, v, w); AtAu(v, u, w); }
  let vBv = 0, vv = 0;
  for (let i = 0; i < n; i++) { vBv += u[i] * v[i]; vv += v[i] * v[i]; }
  return Math.sqrt(vBv / vv).toFixed(9);
}
