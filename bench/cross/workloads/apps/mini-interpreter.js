// Tokenizer + recursive-descent parser + AST tree-walking interpreter for a tiny language with closures & recursion.
function tokenize(src) {
  const re = /\s*(?:(\d+\.?\d*)|([A-Za-z_]\w*)|(==|!=|<=|>=|&&|\|\||[-+*\/%<>=!(){},;]))/y, out = [];
  let m; re.lastIndex = 0;
  while (re.lastIndex < src.length && (m = re.exec(src))) out.push(m[1] !== undefined ? { t: 'num', v: +m[1] } : m[2] !== undefined ? { t: /^(let|fn|if|else|while|return)$/.test(m[2]) ? m[2] : 'id', v: m[2] } : { t: m[3], v: m[3] });
  out.push({ t: 'eof' }); return out;
}
function parse(toks) {
  let p = 0; const peek = () => toks[p], next = () => toks[p++], eat = t => { if (peek().t !== t) throw new SyntaxError('expected ' + t + ' got ' + peek().t); return next(); };
  const prec = { '||': 1, '&&': 2, '==': 3, '!=': 3, '<': 4, '>': 4, '<=': 4, '>=': 4, '+': 5, '-': 5, '*': 6, '/': 6, '%': 6 };
  function primary() {
    const t = next();
    if (t.t === 'num') return { k: 'num', v: t.v };
    if (t.t === 'fn') { eat('('); const ps = []; while (peek().t !== ')') { ps.push(eat('id').v); if (peek().t === ',') next(); } eat(')'); return { k: 'fn', ps, body: block() }; }
    if (t.t === 'id') { let e = { k: 'var', n: t.v }; while (peek().t === '(') { next(); const as = []; while (peek().t !== ')') { as.push(expr(0)); if (peek().t === ',') next(); } eat(')'); e = { k: 'call', f: e, as }; } return e; }
    if (t.t === '(') { const e = expr(0); eat(')'); return e; }
    if (t.t === '-') return { k: 'bin', o: '-', l: { k: 'num', v: 0 }, r: primary() };
    throw new SyntaxError('unexpected ' + t.t);
  }
  function expr(min) { let l = primary(); while (prec[peek().t] >= min && prec[peek().t] !== undefined) { const o = next().t; l = { k: 'bin', o, l, r: expr(prec[o] + 1) }; } return l; }
  function block() { eat('{'); const b = []; while (peek().t !== '}') b.push(stmt()); eat('}'); return b; }
  function stmt() {
    const t = peek();
    if (t.t === 'let') { next(); const n = eat('id').v; eat('='); const e = expr(0); eat(';'); return { k: 'let', n, e }; }
    if (t.t === 'return') { next(); const e = expr(0); eat(';'); return { k: 'ret', e }; }
    if (t.t === 'if') { next(); eat('('); const c = expr(0); eat(')'); const a = block(); let b = []; if (peek().t === 'else') { next(); b = block(); } return { k: 'if', c, a, b }; }
    if (t.t === 'while') { next(); eat('('); const c = expr(0); eat(')'); return { k: 'while', c, b: block() }; }
    const n = peek().t === 'id' && toks[p + 1].t === '=' ? (next().v) : null;
    if (n) { next(); const e = expr(0); eat(';'); return { k: 'set', n, e }; }
    const e = expr(0); eat(';'); return { k: 'expr', e };
  }
  const prog = []; while (peek().t !== 'eof') prog.push(stmt()); return prog;
}
class Ret { constructor(v) { this.v = v; } }
function run(prog) {
  const bin = { '+': (a, b) => a + b, '-': (a, b) => a - b, '*': (a, b) => a * b, '/': (a, b) => a / b, '%': (a, b) => a % b, '<': (a, b) => +(a < b), '>': (a, b) => +(a > b), '<=': (a, b) => +(a <= b), '>=': (a, b) => +(a >= b), '==': (a, b) => +(a === b), '!=': (a, b) => +(a !== b) };
  const find = (env, n) => { for (let e = env; e; e = e.up) if (n in e.vars) return e; throw new ReferenceError(n); };
  function ev(e, env) {
    switch (e.k) {
      case 'num': return e.v;
      case 'var': return find(env, e.n).vars[e.n];
      case 'fn': return { ps: e.ps, body: e.body, env };
      case 'bin': if (e.o === '&&') return ev(e.l, env) && ev(e.r, env); if (e.o === '||') return ev(e.l, env) || ev(e.r, env); return bin[e.o](ev(e.l, env), ev(e.r, env));
      case 'call': { const f = ev(e.f, env), ne = { vars: {}, up: f.env }; f.ps.forEach((p, i) => { ne.vars[p] = ev(e.as[i], env); }); const r = exec(f.body, ne); return r instanceof Ret ? r.v : 0; }
    }
  }
  function exec(b, env) {
    for (const s of b) {
      switch (s.k) {
        case 'let': env.vars[s.n] = ev(s.e, env); break;
        case 'set': find(env, s.n).vars[s.n] = ev(s.e, env); break;
        case 'expr': ev(s.e, env); break;
        case 'ret': return new Ret(ev(s.e, env));
        case 'if': { const r = exec(ev(s.c, env) ? s.a : s.b, env); if (r) return r; break; }
        case 'while': while (ev(s.c, env)) { const r = exec(s.b, env); if (r) return r; } break;
      }
    }
  }
  const g = { vars: {}, up: null }; exec(prog, g); return g.vars.result;
}
const SRC = `
let fib = fn(n) { if (n < 2) { return n; } return fib(n - 1) + fib(n - 2); };
let sum = 0; let i = 0;
while (i < 300) { sum = sum + (i * i) % 7; i = i + 1; }
let mk = fn(a) { return fn(b) { return a * b + sum; }; };
let result = fib(12) + mk(3)(4) + sum;`;
function main() { return String(run(parse(tokenize(SRC)))); }
