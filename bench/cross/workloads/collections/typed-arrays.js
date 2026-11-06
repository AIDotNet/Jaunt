// TypedArrays, DataView, ArrayBuffer slicing, endian conversions, subarray, set, sort.
function main() {
  const buf = new ArrayBuffer(8192), dv = new DataView(buf), u8 = new Uint8Array(buf), f64 = new Float64Array(buf, 0, 512), i32 = new Int32Array(buf, 4096, 256);
  for (let i = 0; i < 512; i++) f64[i] = i * 0.5 - 100;
  for (let i = 0; i < 256; i++) i32[i] = (i * 2654435761) | 0;
  let t = 0;
  for (let r = 0; r < 20; r++) {
    for (let i = 0; i < 8192; i += 4) t = (t + dv.getUint32(i, r & 1 ? true : false)) % 1000003;
    for (let i = 0; i < 256; i++) dv.setInt32(4096 + i * 4, dv.getInt32(4096 + i * 4, true) ^ r, true);
  }
  const sorted = Int32Array.from(i32).sort();
  const sub = u8.subarray(100, 200), cp = new Uint8Array(100); cp.set(sub);
  const u16 = new Uint16Array(u8.slice(0, 64).buffer), clamp = new Uint8ClampedArray([300, -5, 1.5, 254.5]);
  return [t, sorted[0], sorted[255], cp.reduce((a, b) => a + b, 0), u16[3], clamp.join('-')].join(',');
}
