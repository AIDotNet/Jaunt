// Unicode: surrogate pairs, normalize, codePointAt, iterating by code point, Intl.Segmenter-free ops.
function main() {
  const base = 'héllo wörld 你好,世界 😀🎉 ñandú ǅ ﬁ';
  let s = '';
  for (let i = 0; i < 400; i++) s += base + i;
  let cp = 0, astral = 0;
  for (const ch of s) { cp++; if (ch.codePointAt(0) > 0xffff) astral++; }
  const nfd = s.normalize('NFD'), nfc = nfd.normalize('NFC');
  const up = s.toUpperCase();
  const enc = new TextEncoder().encode(s);
  const dec = new TextDecoder().decode(enc);
  const esc = encodeURIComponent(base);
  return [s.length, cp, astral, nfd.length, nfc === s, up.length, enc.length, dec === s, decodeURIComponent(esc) === base, [...base].reverse().join('').length].join(',');
}
