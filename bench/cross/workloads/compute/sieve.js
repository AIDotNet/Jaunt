// Array of booleans, nested loops.
function main() {
  const n = 300000, a = new Array(n + 1).fill(true);
  a[0] = a[1] = false;
  for (let i = 2; i * i <= n; i++) if (a[i]) for (let j = i * i; j <= n; j += i) a[j] = false;
  let c = 0;
  for (let i = 0; i <= n; i++) if (a[i]) c++;
  return String(c);
}
