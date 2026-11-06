// Tight integer loop with bit ops and modulo.
function main() {
  let s = 0;
  for (let i = 0; i < 300000; i++) s = (s + (i ^ (i >>> 3)) * 7) % 1000003;
  return String(s);
}
