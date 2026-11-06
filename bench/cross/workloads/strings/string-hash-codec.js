// Char-code arithmetic: FNV hash, caesar shift, run-length encoding, string comparison.
function fnv(s) { let h = 0x811c9dc5; for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 0x01000193) >>> 0; } return h; }
function rle(s) { let out = '', i = 0; while (i < s.length) { let j = i; while (j < s.length && s[j] === s[i]) j++; out += s[i] + (j - i); i = j; } return out; }
function main() {
  let text = '', x = 7;
  for (let i = 0; i < 20000; i++) { x = (x * 1103515245 + 12345) & 0x7fffffff; text += String.fromCharCode(97 + (x >> 12) % (i % 50 === 0 ? 26 : 3)); }
  let sh = '';
  for (let i = 0; i < text.length; i++) sh += String.fromCharCode((text.charCodeAt(i) - 97 + 3) % 26 + 97);
  const r = rle(text);
  return [fnv(text), fnv(sh), r.length, fnv(r), text < sh, text.localeCompare('a') > 0].join(',');
}
