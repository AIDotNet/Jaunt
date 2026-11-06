// Float math: sqrt / sin / division in a loop.
function main() {
  let s = 0;
  for (let i = 1; i <= 200000; i++) s += Math.sqrt(i) / (i + 0.5) + Math.abs(Math.sin(i)) * 1e-3;
  return s.toFixed(6);
}
