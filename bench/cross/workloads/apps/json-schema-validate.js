// Recursive schema validator over generated JSON documents (type checks, required, patterns, enums, nested arrays, error paths).
function validate(v, s, path, errs) {
  if (s.type) { const t = Array.isArray(v) ? 'array' : v === null ? 'null' : typeof v; if (t !== s.type && !(s.type === 'integer' && Number.isInteger(v))) { errs.push(path + ': type ' + t); return; } }
  if (s.enum && !s.enum.includes(v)) errs.push(path + ': enum');
  if (s.pattern && !new RegExp(s.pattern).test(v)) errs.push(path + ': pattern');
  if (s.minimum !== undefined && v < s.minimum) errs.push(path + ': min');
  if (s.properties) { for (const r of s.required || []) if (!(r in v)) errs.push(path + '.' + r + ': required'); for (const k of Object.keys(s.properties)) if (k in v) validate(v[k], s.properties[k], path + '.' + k, errs); }
  if (s.items && Array.isArray(v)) v.forEach((e, i) => validate(e, s.items, path + '[' + i + ']', errs));
}
const schema = { type: 'object', required: ['id', 'email', 'role'], properties: { id: { type: 'integer', minimum: 0 }, email: { type: 'string', pattern: '^[^@\\s]+@[^@\\s]+\\.\\w+$' }, role: { enum: ['admin', 'user', 'guest'] }, tags: { type: 'array', items: { type: 'string' } }, profile: { type: 'object', required: ['age'], properties: { age: { type: 'integer', minimum: 18 } } } } };
function main() {
  let bad = 0, errCount = 0, first = '';
  for (let i = 0; i < 2500; i++) {
    const doc = { id: i % 40 === 0 ? 'x' : i, email: i % 13 === 0 ? 'nope' : `u${i}@e.com`, role: ['admin', 'user', 'guest', 'root'][i % 4], tags: i % 9 ? ['a', 'b'] : ['a', 5], profile: i % 17 ? { age: 18 + i % 50 } : { age: 10 } };
    if (i % 11 === 0) delete doc.role;
    const errs = []; validate(doc, schema, '$', errs);
    if (errs.length) { bad++; errCount += errs.length; if (!first) first = errs[0]; }
  }
  return [bad, errCount, first].join(',');
}
