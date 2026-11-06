// Runs every workload in Node (vm context) and writes expected checksums: node node-expected.mjs ../workloads ../expected.json
import fs from 'node:fs'; import path from 'node:path'; import vm from 'node:vm';
const [dir, outFile] = process.argv.slice(2);
const files = fs.readdirSync(dir, { recursive: true }).filter(f => f.endsWith('.js')).sort();
const res = {};
for (const f of files) {
  const name = f.replace(/\.js$/, '').replaceAll(path.sep, '/');
  try {
    const ctx = vm.createContext({ console, TextEncoder, TextDecoder, structuredClone, queueMicrotask });
    vm.runInContext(fs.readFileSync(path.join(dir, f), 'utf8'), ctx, { filename: f });
    let r = vm.runInContext('main()', ctx);
    await new Promise(r => setTimeout(r, 5));
    if (vm.runInContext("typeof result === 'function'", ctx)) r = vm.runInContext('result()', ctx);
    res[name] = String(r);
  } catch (e) { res[name] = 'ERR ' + e.message; }
}
fs.writeFileSync(outFile, JSON.stringify(res, null, 1));
for (const [k, v] of Object.entries(res)) console.log(k.padEnd(34), v.length > 90 ? v.slice(0, 90) + '…' : v);
