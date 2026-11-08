#!/usr/bin/env python3
"""Jaunt hot-path throughput, NativeAOT vs CoreCLR JIT: merge the .jsonl written by `Jaunt.AotHost bench` (engine
jaunt-aot from the native binary, jaunt-jit from `dotnet Jaunt.AotHost.dll`), print a table and write a summary JSON.
usage: report_aot.py out.json in1.jsonl [in2.jsonl ...]   (several passes of one engine => per-field minimum)"""
import json, sys, math, os
out, ins = sys.argv[1], sys.argv[2:]
CPU = os.environ.get('METRIC') == 'cpu'   # METRIC=cpu: compare CPU time per call (robust when the machine is busy)
rows, runtimes = {}, {}
for p in ins:
    for line in open(p):
        r = json.loads(line)
        if CPU and r.get('cpuUs'): r['medianUs'] = r['minUs'] = r['cpuUs']
        runtimes.setdefault(r['engine'], r.get('runtime'))
        cur = rows.setdefault(r['name'], {}).get(r['engine'])
        if cur is None or cur['status'] not in ('ok', 'mismatch'): rows[r['name']][r['engine']] = r; continue
        if r['status'] in ('ok', 'mismatch'):
            for k in ('medianUs', 'minUs', 'cpuUs', 'coldUs', 'compileUs', 'allocB'): cur[k] = min(cur[k], r[k])
JIT, AOT = 'jaunt-jit', 'jaunt-aot'
def v(r, k): return r.get(k) if r and r.get('status') in ('ok', 'mismatch') else None
def fmt(x): return '    —    ' if x is None else (f'{x/1000:8.2f}ms' if x >= 1000 else f'{x:8.1f}µs')
def ratio(x): return '   —  ' if x is None else f'{x:6.2f}x'
def gm(xs): return math.exp(sum(map(math.log, xs)) / len(xs)) if xs else None
for e in (JIT, AOT): print(f'{e}: {runtimes.get(e, "(no results)")}')
print(f"\n{'workload':34}{'JIT warm':>11}{'AOT warm':>11}  AOT/JIT {'JIT cpu':>11}{'AOT cpu':>11}  AOT/JIT {'JIT cold':>11}{'AOT cold':>11}  notes")
data, warm, cpu, cold, alloc, by_cat = [], [], [], [], [], {}
for name in sorted(rows):
    d = rows[name]; j, a = d.get(JIT), d.get(AOT); notes = []
    for e, r in ((JIT, j), (AOT, a)):
        if r and r['status'] == 'mismatch': notes.append(f'{e}:MISMATCH')
        if r and r['status'] == 'error': notes.append(f"{e}:ERR({r.get('error', '')[:50]})")
    if j and a and v(j, 'result') is not None and v(a, 'result') is not None and j['result'] != a['result']: notes.append('AOT≠JIT result')
    rw = v(a, 'medianUs') / v(j, 'medianUs') if v(a, 'medianUs') and v(j, 'medianUs') else None
    rc = v(a, 'cpuUs') / v(j, 'cpuUs') if v(a, 'cpuUs') and v(j, 'cpuUs') else None
    rk = v(a, 'coldUs') / v(j, 'coldUs') if v(a, 'coldUs') and v(j, 'coldUs') else None
    ra = v(a, 'allocB') / v(j, 'allocB') if v(a, 'allocB') and v(j, 'allocB') else None
    cat = name.split('/')[0]
    if rw: warm.append(rw); by_cat.setdefault(cat, []).append(rw)
    if rc: cpu.append(rc)
    if rk: cold.append(rk)
    if ra: alloc.append(ra)
    ent = {'name': name, 'category': cat, 'aot_over_jit': {'warm': rw, 'cpu': rc, 'cold': rk, 'alloc': ra}}
    for e, r in ((JIT, j), (AOT, a)):
        ent[e] = {k: v(r, k) for k in ('medianUs', 'minUs', 'cpuUs', 'coldUs', 'compileUs', 'allocB')}; ent[e]['status'] = (r or {}).get('status')
    data.append(ent)
    print(f'{name:34}{fmt(v(j, "medianUs")):>11}{fmt(v(a, "medianUs")):>11}  {ratio(rw)} {fmt(v(j, "cpuUs")):>11}{fmt(v(a, "cpuUs")):>11}  {ratio(rc)} {fmt(v(j, "coldUs")):>11}{fmt(v(a, "coldUs")):>11}  ' + ' '.join(notes))
summary = {'warm_geomean_aot_over_jit': gm(warm), 'cpu_geomean_aot_over_jit': gm(cpu), 'cold_geomean_aot_over_jit': gm(cold),
           'alloc_geomean_aot_over_jit': gm(alloc), 'n': len(warm), 'by_category_warm': {c: gm(x) for c, x in sorted(by_cat.items())},
           'runtimes': runtimes}
print('\nGeomean AOT time / JIT time (>1 = NativeAOT slower than CoreCLR JIT); warm = per call after warm-up, cold = new engine + load + first call:')
print(json.dumps(summary, indent=1))
json.dump({'workloads': data, 'summary': summary}, open(out, 'w'), indent=1)
