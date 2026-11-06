// Intl: NumberFormat, DateTimeFormat (UTC), PluralRules, Collator, ListFormat, toLocaleString.
function main() {
  let t = 0, sample = '';
  const nf = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' });
  const pct = new Intl.NumberFormat('de-DE', { style: 'percent', maximumFractionDigits: 1 });
  const dtf = new Intl.DateTimeFormat('en-US', { timeZone: 'UTC', dateStyle: 'medium', timeStyle: 'short' });
  const pr = new Intl.PluralRules('en'), col = new Intl.Collator('en', { sensitivity: 'base' });
  for (let i = 0; i < 1500; i++) {
    const a = nf.format(i * 1234.567), b = pct.format(i / 997), c = dtf.format(new Date(Date.UTC(2024, 0, 1) + i * 3600000 * 7));
    t += a.length + b.length + c.length + (pr.select(i) === 'one' ? 1 : 0) + col.compare('a' + i, 'A' + i) + (i).toLocaleString('en-US').length;
    if (i === 1499) sample = a + '|' + b + '|' + c;
  }
  return t + ':' + sample + ':' + new Intl.ListFormat('en').format(['a', 'b', 'c']);
}
