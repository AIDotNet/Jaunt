// CRC32 table + base64 encode/decode implemented by hand, plus builtin btoa/atob if present.
const table = new Uint32Array(256);
for (let i = 0; i < 256; i++) { let c = i; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; table[i] = c >>> 0; }
function crc32(bytes) { let c = 0xffffffff; for (let i = 0; i < bytes.length; i++) c = table[(c ^ bytes[i]) & 255] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; }
const ALPHA = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
function b64(bytes) {
  let out = '';
  for (let i = 0; i < bytes.length; i += 3) {
    const n = (bytes[i] << 16) | ((bytes[i + 1] || 0) << 8) | (bytes[i + 2] || 0);
    out += ALPHA[n >> 18] + ALPHA[(n >> 12) & 63] + (i + 1 < bytes.length ? ALPHA[(n >> 6) & 63] : '=') + (i + 2 < bytes.length ? ALPHA[n & 63] : '=');
  }
  return out;
}
function main() {
  const data = new Uint8Array(30000);
  for (let i = 0; i < data.length; i++) data[i] = (i * 17 + (i >> 3)) & 255;
  const enc = b64(data);
  return crc32(data) + ':' + enc.length + ':' + crc32(Uint8Array.from(enc, c => c.charCodeAt(0))) + ':' + enc.slice(0, 16);
}
