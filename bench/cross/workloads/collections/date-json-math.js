// Date arithmetic/formatting (UTC), Math functions, Number formatting and parsing.
function main() {
  let t = 0, last = '';
  for (let i = 0; i < 3000; i++) {
    const d = new Date(Date.UTC(2000, 0, 1) + i * 86400000 * 3.7);
    t += d.getUTCDay() + d.getUTCMonth() + d.getUTCDate() + d.getUTCFullYear() % 100;
    if (i % 500 === 0) last = d.toISOString();
    t += Date.parse(d.toISOString()) % 7;
  }
  let m = 0;
  for (let i = 1; i < 20000; i++) m += Math.floor(Math.log2(i)) + Math.hypot(i, 3) % 3 + Math.trunc(Math.cbrt(i)) + Math.sign(i - 100) + Math.clz32(i) + Math.round(i / 7);
  const nums = [];
  for (let i = 0; i < 2000; i++) nums.push(parseFloat((i / 7).toFixed(3)) + parseInt(String(i % 7), 7) + Number('0x' + i.toString(16)));
  return [t, last, m.toFixed(3), nums.reduce((a, b) => a + b, 0).toFixed(3), (1234.5678).toFixed(2), (0.000001234).toExponential(2), (255).toString(2)].join(',');
}
