// Unicode: surrogate pairs, normalize, codePointAt, iterating by code point, Intl.Segmenter-free ops.
function utf8(s) { const o = []; for (const ch of s) { const c = ch.codePointAt(0); if (c < 0x80) o.push(c); else if (c < 0x800) o.push(0xc0 | c >> 6, 0x80 | c & 63); else if (c < 0x10000) o.push(0xe0 | c >> 12, 0x80 | c >> 6 & 63, 0x80 | c & 63); else o.push(0xf0 | c >> 18, 0x80 | c >> 12 & 63, 0x80 | c >> 6 & 63, 0x80 | c & 63); } return o; }
function unutf8(b) { let r = '', i = 0; while (i < b.length) { const x = b[i]; let c, n; if (x < 0x80) { c = x; n = 1; } else if (x < 0xe0) { c = (x & 31) << 6 | b[i + 1] & 63; n = 2; } else if (x < 0xf0) { c = (x & 15) << 12 | (b[i + 1] & 63) << 6 | b[i + 2] & 63; n = 3; } else { c = (x & 7) << 18 | (b[i + 1] & 63) << 12 | (b[i + 2] & 63) << 6 | b[i + 3] & 63; n = 4; } r += String.fromCodePoint(c); i += n; } return r; }
function main() {
  const base = 'héllo wörld 你好,世界 😀🎉 ñandú ǅ ﬁ';
  let s = '';
  for (let i = 0; i < 400; i++) s += base + i;
  let cp = 0, astral = 0;
  for (const ch of s) { cp++; if (ch.codePointAt(0) > 0xffff) astral++; }
  const nfd = s.normalize('NFD'), nfc = nfd.normalize('NFC');
  const up = s.toUpperCase();
  const enc = utf8(s), dec = unutf8(enc);
  const esc = encodeURIComponent(base);
  return [s.length, cp, astral, nfd.length, nfc === s, up.length, enc.length, dec === s, decodeURIComponent(esc) === base, [...base].reverse().join('').length].join(',');
}
