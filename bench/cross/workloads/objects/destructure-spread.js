// Destructuring (object/array/defaults/nested), rest/spread, optional chaining, nullish.
function f({ a, b: { c = 5, ...restB } = {}, ...rest }, [x, , y = 9, ...tail] = []) {
  return a + c + (restB.d ?? 0) + (rest.e?.f ?? 1) + x + y + tail.length;
}
function main() {
  let t = 0;
  for (let i = 0; i < 8000; i++) {
    const arr = [i, 1, undefined, 4, 5, 6];
    t += f({ a: i, b: { d: i % 4 ? i : undefined }, e: i % 2 ? { f: 2 } : null }, arr);
    const merged = { ...{ p: 1, q: 2 }, ...{ q: i, r: 3 } };
    const [first, ...others] = [...arr, ...[7, 8]];
    t += merged.q + merged.r + first + others.length + Math.max(...arr.filter(Boolean));
  }
  return String(t);
}
