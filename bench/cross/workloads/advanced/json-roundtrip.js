// JSON.stringify/parse of nested data with replacer, reviver, indentation, toJSON, unicode escapes.
function main() {
  const data = { users: [], meta: { v: 1, tags: ['x', 'y'], nested: { a: [1, 2, { b: null }] } } };
  for (let i = 0; i < 400; i++) data.users.push({ id: i, name: 'User ' + i + ' "q" é你', email: `u${i}@ex.com`, score: i * 1.37, active: i % 2 === 0, roles: i % 3 ? ['r'] : ['r', 'admin'], addr: { zip: String(10000 + i), geo: [i / 10, -i / 10] }, created: new Date(1700000000000 + i * 1000) });
  let t = 0;
  for (let r = 0; r < 5; r++) {
    const s = JSON.stringify(data);
    const back = JSON.parse(s, (k, v) => typeof v === 'string' && /^\d{4}-\d\d-\d\dT/.test(v) ? new Date(v) : v);
    t += s.length + back.users.length + back.users[399].created.getTime() % 1000;
    const pretty = JSON.stringify(data.meta, null, 2), filtered = JSON.stringify(data.users[5], ['id', 'name', 'roles']);
    const rep = JSON.stringify(data.users.slice(0, 20), (k, v) => k === 'geo' ? undefined : v);
    t += pretty.length + filtered.length + rep.length;
  }
  return String(t);
}
