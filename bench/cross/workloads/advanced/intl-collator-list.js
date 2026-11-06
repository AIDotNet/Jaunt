// Intl.Collator / ListFormat / RelativeTimeFormat sorting & formatting (not implemented by moejs yet -> reported as unsupported).
function main() {
  const col = new Intl.Collator('en', { sensitivity: 'base', numeric: true }), lf = new Intl.ListFormat('en'), rtf = new Intl.RelativeTimeFormat('en', { numeric: 'auto' });
  const words = Array.from({ length: 1500 }, (_, i) => (i % 3 ? 'Item' : 'item') + (i * 7919 % 1000));
  words.sort(col.compare);
  let t = 0;
  for (let i = 0; i < 800; i++) t += lf.format(['a' + i, 'b', 'c']).length + rtf.format(i - 400, 'day').length;
  return t + ':' + words[0] + ':' + words[1499];
}
