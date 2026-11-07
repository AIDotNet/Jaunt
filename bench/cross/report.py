#!/usr/bin/env python3
"""Merge cross-engine .jsonl results: print a table and write a summary JSON.
usage: report.py out.json in1.jsonl [in2.jsonl ...]  (several passes of the same engine => per-field minimum)"""
import json, sys, math
out, ins = sys.argv[1], sys.argv[2:]
import os
CPU = os.environ.get('METRIC') == 'cpu'   # METRIC=cpu: compare CPU time per call (robust when the machine is busy)
rows = {}
for p in ins:
    for line in open(p):
        r = json.loads(line)
        if CPU and r.get('cpuUs'): r['medianUs'] = r['minUs'] = r['cpuUs']
        cur = rows.setdefault(r['name'], {}).get(r['engine'])
        if cur is None or cur['status'] not in ('ok', 'mismatch'): rows[r['name']][r['engine']] = r; continue
        if r['status'] in ('ok', 'mismatch'):  # best-of-passes: keep the minimum of every timing field (robust to background load)
            for k in ('medianUs', 'minUs', 'coldUs', 'compileUs', 'allocB'): cur[k] = min(cur[k], r[k])
engines = ['jaunt', 'moejs', 'jint']
def v(r, k): return r.get(k) if r and r.get('status') in ('ok', 'mismatch') else None
def fmt(x): return '   —   ' if x is None else (f'{x/1000:9.2f}ms' if x >= 1000 else f'{x:9.1f}µs')
print(f"{'workload':34}" + ''.join(f'{e:>12}' for e in engines) + '   moejs/jaunt  jint/jaunt  notes')
data = []; g = {'moejs': [], 'jint': []}; gc = {'moejs': [], 'jint': []}
for name in sorted(rows):
    d = rows[name]; notes = []
    for e in engines:
        r = d.get(e)
        if r and r['status'] == 'mismatch': notes.append(f'{e}:MISMATCH')
        if r and r['status'] == 'error': notes.append(f"{e}:ERR({r.get('error','')[:40]})")
    j = v(d.get('jaunt'), 'medianUs'); ent = {'name': name, 'category': name.split('/')[0]}
    for e in engines:
        ent[e] = {k: v(d.get(e), k) for k in ('medianUs', 'minUs', 'coldUs', 'compileUs', 'allocB')}; ent[e]['status'] = d.get(e, {}).get('status')
    rat = []
    for e in ('moejs', 'jint'):
        x = v(d.get(e), 'medianUs'); rr = x / j if x and j else None; rat.append(rr)
        if rr: g[e].append(rr)
        c, cj = v(d.get(e), 'coldUs'), v(d.get('jaunt'), 'coldUs')
        if c and cj: gc[e].append(c / cj)
    data.append(ent)
    f = lambda x: '   —  ' if x is None else f'{x:6.2f}x'
    print(f'{name:34}' + ''.join(f'{fmt(v(d.get(e),"medianUs")):>12}' for e in engines) + f'   {f(rat[0]):>10}  {f(rat[1]):>10}  ' + ' '.join(notes))
gm = lambda xs: math.exp(sum(map(math.log, xs)) / len(xs)) if xs else None
summary = {e: {'warm_geomean_vs_jaunt': gm(g[e]), 'cold_geomean_vs_jaunt': gm(gc[e]), 'n': len(g[e])} for e in g}
print('\nGeomean time relative to Jaunt (>1 = slower than Jaunt):'); print(json.dumps(summary, indent=1))
json.dump({'workloads': data, 'summary': summary}, open(out, 'w'), indent=1)
