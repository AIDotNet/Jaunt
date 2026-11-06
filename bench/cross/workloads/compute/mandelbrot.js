// Escape-time iteration, float loops with early exit.
function main() {
  const w = 64, h = 48, maxIter = 100;
  let total = 0;
  for (let py = 0; py < h; py++) for (let px = 0; px < w; px++) {
    const x0 = px / w * 3.5 - 2.5, y0 = py / h * 2 - 1;
    let x = 0, y = 0, i = 0;
    while (x * x + y * y <= 4 && i < maxIter) { const t = x * x - y * y + x0; y = 2 * x * y + y0; x = t; i++; }
    total += i;
  }
  return String(total);
}
