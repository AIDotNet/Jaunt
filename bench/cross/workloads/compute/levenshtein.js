// Dynamic programming over 2D arrays + string char access.
function lev(a, b) {
  const dp = Array.from({ length: a.length + 1 }, (_, i) => { const r = new Array(b.length + 1).fill(0); r[0] = i; return r; });
  for (let j = 0; j <= b.length; j++) dp[0][j] = j;
  for (let i = 1; i <= a.length; i++) for (let j = 1; j <= b.length; j++) {
    const c = a.charCodeAt(i - 1) === b.charCodeAt(j - 1) ? 0 : 1;
    dp[i][j] = Math.min(dp[i - 1][j] + 1, dp[i][j - 1] + 1, dp[i - 1][j - 1] + c);
  }
  return dp[a.length][b.length];
}
function main() {
  let s = '', t = '', x = 12345;
  for (let i = 0; i < 220; i++) { x = (x * 1103515245 + 12345) & 0x7fffffff; s += String.fromCharCode(97 + x % 7); x = (x * 1103515245 + 12345) & 0x7fffffff; t += String.fromCharCode(97 + x % 7); }
  return String(lev(s, t));
}
