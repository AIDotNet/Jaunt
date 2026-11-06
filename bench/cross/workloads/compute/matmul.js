// Dense matmul with Float64Array and with nested plain arrays.
function main() {
  const n = 48;
  const a = new Float64Array(n * n), b = new Float64Array(n * n), c = new Float64Array(n * n);
  for (let i = 0; i < n * n; i++) { a[i] = (i % 13) / 13; b[i] = (i % 7) / 7; }
  for (let i = 0; i < n; i++) for (let k = 0; k < n; k++) { const aik = a[i * n + k]; for (let j = 0; j < n; j++) c[i * n + j] += aik * b[k * n + j]; }
  const A = Array.from({ length: 32 }, (_, i) => Array.from({ length: 32 }, (_, j) => (i * 3 + j) % 11));
  const B = Array.from({ length: 32 }, (_, i) => Array.from({ length: 32 }, (_, j) => (i + j * 5) % 9));
  const C = A.map(() => new Array(32).fill(0));
  for (let i = 0; i < 32; i++) for (let j = 0; j < 32; j++) { let s = 0; for (let k = 0; k < 32; k++) s += A[i][k] * B[k][j]; C[i][j] = s; }
  let s = 0;
  for (let i = 0; i < n * n; i++) s += c[i];
  for (const r of C) for (const v of r) s += v;
  return s.toFixed(4);
}
