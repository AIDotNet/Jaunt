// Dictionary-mode objects: computed keys, delete, in, Object.keys/entries/fromEntries, hasOwn.
function main() {
  const o = {};
  for (let i = 0; i < 4000; i++) o['key' + (i * 7919 % 4001)] = i;
  let del = 0;
  for (let i = 0; i < 4000; i += 3) if (delete o['key' + i]) del++;
  const keys = Object.keys(o);
  let s = 0;
  for (const k of keys) s += o[k];
  const e = Object.entries(o).filter(([k, v]) => v % 2 === 0);
  const back = Object.fromEntries(e);
  let present = 0;
  for (let i = 0; i < 4001; i++) if (('key' + i) in back) present++;
  return [del, keys.length, s, e.length, Object.keys(back).length, present, Object.hasOwn(o, 'key1')].join(',');
}
