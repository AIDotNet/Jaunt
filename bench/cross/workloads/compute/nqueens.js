// Backtracking with bitmasks and recursion.
function solve(n, row, cols, d1, d2) {
  if (row === n) return 1;
  let count = 0, avail = ~(cols | d1 | d2) & ((1 << n) - 1);
  while (avail) {
    const bit = avail & -avail;
    avail ^= bit;
    count += solve(n, row + 1, cols | bit, ((d1 | bit) << 1) & ((1 << n) - 1), (d2 | bit) >> 1);
  }
  return count;
}
function main() { return String(solve(8, 0, 0, 0, 0) + solve(9, 0, 0, 0, 0)); }
