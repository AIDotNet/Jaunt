// split / indexOf / includes / replaceAll / startsWith / slice / trim / case conversion on a ~100KB text.
function main() {
  const words = ['alpha', 'beta', 'Gamma', 'delta', 'epsilon', 'zeta', 'eta', 'theta'];
  let text = '';
  for (let i = 0; i < 12000; i++) text += words[(i * 7 + (i >> 2)) % 8] + (i % 13 === 0 ? ',\n' : ' ');
  const lines = text.split('\n');
  let hits = 0, pos = 0;
  while ((pos = text.indexOf('eta ', pos)) !== -1) { hits++; pos += 4; }
  const up = text.toUpperCase(), low = up.toLowerCase();
  const rep = text.replaceAll('alpha', 'A').replaceAll(',', ';');
  let sw = 0;
  for (const l of lines) { const t = l.trim(); if (t.startsWith('beta')) sw++; if (t.includes('theta')) sw += 2; if (t.endsWith('eta')) sw += 3; }
  return [lines.length, hits, up.length, low === text.toLowerCase(), rep.length, sw].join(',');
}
