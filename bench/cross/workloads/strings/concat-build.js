// String concatenation in loops, template literals, array join, repeat, padStart.
function main() {
  let s = '';
  for (let i = 0; i < 6000; i++) s += 'item-' + i + ';';
  const parts = [];
  for (let i = 0; i < 3000; i++) parts.push(`${i}:${(i * 7).toString(16).padStart(5, '0')}`);
  const j = parts.join('|');
  return s.length + ':' + j.length + ':' + 'ab'.repeat(500).length + ':' + s.charCodeAt(1234) + ':' + j.slice(-12);
}
