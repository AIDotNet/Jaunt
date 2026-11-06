// Grid simulation over Uint8Array with neighbor counting.
function main() {
  const W = 48, H = 48;
  let cur = new Uint8Array(W * H), nxt = new Uint8Array(W * H);
  let x = 99;
  for (let i = 0; i < W * H; i++) { x = (x * 1103515245 + 12345) & 0x7fffffff; cur[i] = (x >> 8) % 3 === 0 ? 1 : 0; }
  for (let gen = 0; gen < 30; gen++) {
    for (let y = 0; y < H; y++) for (let xx = 0; xx < W; xx++) {
      let n = 0;
      for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
        if (dx === 0 && dy === 0) continue;
        n += cur[((y + dy + H) % H) * W + (xx + dx + W) % W];
      }
      const alive = cur[y * W + xx];
      nxt[y * W + xx] = n === 3 || (alive && n === 2) ? 1 : 0;
    }
    [cur, nxt] = [nxt, cur];
  }
  let c = 0;
  for (let i = 0; i < W * H; i++) c += cur[i];
  return String(c);
}
