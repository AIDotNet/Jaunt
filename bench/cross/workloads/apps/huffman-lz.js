// Compression: Huffman coding (tree build, code table, encode, decode) + LZW round trip over text.
function huffman(text) {
  const freq = new Map(); for (const ch of text) freq.set(ch, (freq.get(ch) || 0) + 1);
  let nodes = [...freq].map(([ch, n], i) => ({ ch, n, id: i }));
  let id = nodes.length;
  while (nodes.length > 1) { nodes.sort((a, b) => a.n - b.n || a.id - b.id); const [a, b] = nodes.splice(0, 2); nodes.push({ n: a.n + b.n, l: a, r: b, id: id++ }); }
  const codes = new Map(); (function walk(n, p) { if (n.ch !== undefined) codes.set(n.ch, p || '0'); else { walk(n.l, p + '0'); walk(n.r, p + '1'); } })(nodes[0], '');
  let bits = ''; for (const ch of text) bits += codes.get(ch);
  let out = '', cur = nodes[0];
  for (const b of bits) { cur = b === '0' ? cur.l : cur.r; if (cur.ch !== undefined) { out += cur.ch; cur = nodes[0]; } }
  return { bits: bits.length, ok: out === text, symbols: codes.size };
}
function lzw(text) {
  const dict = new Map(); for (let i = 0; i < 256; i++) dict.set(String.fromCharCode(i), i);
  let w = '', out = [];
  for (const c of text) { const wc = w + c; if (dict.has(wc)) w = wc; else { out.push(dict.get(w)); dict.set(wc, dict.size); w = c; } }
  if (w) out.push(dict.get(w));
  const rd = new Map(); for (let i = 0; i < 256; i++) rd.set(i, String.fromCharCode(i));
  let prev = rd.get(out[0]), res = prev;
  for (let i = 1; i < out.length; i++) { const e = rd.has(out[i]) ? rd.get(out[i]) : prev + prev[0]; res += e; rd.set(rd.size, prev + e[0]); prev = e; }
  return { codes: out.length, ok: res === text };
}
function main() {
  const words = ['the', 'quick', 'brown', 'fox', 'jumps', 'over', 'lazy', 'dog', 'lorem', 'ipsum'];
  let text = '', x = 5; for (let i = 0; i < 6000; i++) { x = (x * 1103515245 + 12345) & 0x7fffffff; text += words[(x >> 8) % 10] + ' '; }
  const h = huffman(text), l = lzw(text);
  return [h.bits, h.ok, h.symbols, l.codes, l.ok].join(',');
}
