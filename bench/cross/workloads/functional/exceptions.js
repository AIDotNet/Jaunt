// throw/catch/finally cost, Error subclasses, stack-less checks, error cause, nested rethrow.
class AppError extends Error { constructor(m, code) { super(m); this.name = 'AppError'; this.code = code; } }
function risky(i) { if (i % 3 === 0) throw new AppError('bad ' + i, i % 7); if (i % 5 === 0) throw new TypeError('type ' + i); return i; }
function main() {
  let ok = 0, app = 0, type = 0, fin = 0, codes = 0;
  for (let i = 0; i < 12000; i++) {
    try { ok += risky(i); }
    catch (e) { if (e instanceof AppError) { app++; codes += e.code; } else if (e instanceof TypeError) type++; else throw e; }
    finally { fin++; }
  }
  let nested = 0;
  for (let i = 0; i < 1500; i++) { try { try { null.x; } catch (e) { throw new Error('wrapped', { cause: e }); } } catch (e) { nested += e.cause instanceof TypeError ? 1 : 0; } }
  return [ok, app, type, fin, codes, nested].join(',');
}
