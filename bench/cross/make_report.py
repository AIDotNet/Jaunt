#!/usr/bin/env python3
"""Build the self-contained benchmark report HTML (data and d3 inlined, works offline).
usage: make_report.py summary.json d3.min.js out.html   (summary.json comes from report.py)"""
import json, sys
summary, d3p, out = sys.argv[1:4]
S = json.load(open(summary))
r3 = lambda x: None if x is None else round(x, 3)
rows = []
for w in S['workloads']:
    def ms(e, k): return r3(e[k] / 1000) if e.get(k) else None
    j, m, i = w['jaunt'], w['moejs'], w['jint']
    rows.append({'name': w['name'], 'category': w['category'],
                 'jaunt': ms(j, 'medianUs'), 'moejs': ms(m, 'medianUs'), 'jint': ms(i, 'medianUs'),
                 'jaunt_cold': ms(j, 'coldUs'), 'moejs_cold': ms(m, 'coldUs'), 'jint_cold': ms(i, 'coldUs'),
                 'jaunt_compile': ms(j, 'compileUs'), 'moejs_compile': ms(m, 'compileUs'), 'jint_compile': ms(i, 'compileUs')})
html = open(__file__.replace('make_report.py', 'report-template.html')).read()
html = html.replace('__DATA__', json.dumps(rows, separators=(',', ':'))).replace('__D3__', open(d3p).read().replace('</script>', '<\\/script>'))
open(out, 'w').write(html)
print('wrote', out, len(html) // 1024, 'KB')
