// switch, labeled break/continue, do-while, for-in/for-of, comma, ternary chains, short-circuit.
function classify(n) { switch (n % 8) { case 0: return 'a'; case 1: case 2: return 'b'; case 3: return n > 50 ? 'c' : 'd'; default: return 'e'; } }
function main() {
  let t = 0;
  const counts = { a: 0, b: 0, c: 0, d: 0, e: 0 };
  for (let i = 0; i < 40000; i++) counts[classify(i)]++;
  outer: for (let i = 0; i < 200; i++) { for (let j = 0; j < 200; j++) { if (j > i) continue outer; if (i * j > 15000) break outer; t += (i ^ j) & 3; } }
  let k = 0; do { k += 3; t += k % 5; } while (k < 20000);
  const o = { a: 1, b: 2, c: 3 };
  for (let r = 0; r < 3000; r++) for (const key in o) t += o[key];
  for (let i = 0, j = 10; i < j * 1000; i += 2, t++) t += i && j ? 1 : 0;
  return JSON.stringify(counts) + t;
}
