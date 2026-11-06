// BigInt arithmetic: factorial, fibonacci, modpow, string conversion, asIntN.
function fact(n) { let r = 1n; for (let i = 2n; i <= n; i++) r *= i; return r; }
function fibBig(n) { let a = 0n, b = 1n; for (let i = 0; i < n; i++) [a, b] = [b, a + b]; return a; }
function modpow(b, e, m) { let r = 1n; b %= m; while (e > 0n) { if (e & 1n) r = r * b % m; b = b * b % m; e >>= 1n; } return r; }
function main() {
  const f = fact(300n), g = fibBig(1500);
  const p = modpow(3n, 10n ** 40n + 12345n, 2n ** 127n - 1n);
  const s = f.toString();
  return [s.length, s.slice(0, 10), g.toString().length, p.toString(16), BigInt.asUintN(64, -1n), (f / fact(290n)).toString(), BigInt(Number.MAX_SAFE_INTEGER) * 3n].join(',');
}
