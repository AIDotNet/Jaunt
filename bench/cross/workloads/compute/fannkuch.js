// Permutation flipping: array swaps and index arithmetic.
function fannkuch(n) {
  const perm = new Array(n), perm1 = new Array(n), count = new Array(n);
  let maxFlips = 0, checksum = 0, permCount = 0, r = n;
  for (let i = 0; i < n; i++) perm1[i] = i;
  while (true) {
    while (r !== 1) { count[r - 1] = r; r--; }
    for (let i = 0; i < n; i++) perm[i] = perm1[i];
    let flips = 0, k;
    while ((k = perm[0]) !== 0) {
      const k2 = (k + 1) >> 1;
      for (let i = 0; i < k2; i++) { const t = perm[i]; perm[i] = perm[k - i]; perm[k - i] = t; }
      flips++;
    }
    if (flips > maxFlips) maxFlips = flips;
    checksum += permCount % 2 === 0 ? flips : -flips;
    while (true) {
      if (r === n) return checksum + ':' + maxFlips;
      const p0 = perm1[0];
      for (let i = 0; i < r; i++) perm1[i] = perm1[i + 1];
      perm1[r] = p0;
      count[r]--;
      if (count[r] > 0) break;
      r++;
    }
    permCount++;
  }
}
function main() { return fannkuch(8); }
