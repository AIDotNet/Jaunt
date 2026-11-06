#!/usr/bin/env python3
"""Fails (exit 1) when a test262 run has failures or timeouts not listed in known-failures.txt.

known-failures.txt lines: `<test path>` (expected with any ICU) or `icu<NN <test path>` (expected only when the platform
ICU major version is below NN, i.e. older locale data than the reference ICU 78 / CLDR 48)."""
import json, os, sys

sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")

out = sys.argv[1]
here = os.path.dirname(os.path.abspath(__file__))
icu = open(os.path.join(out, "icu-version.txt")).read().strip()
icu_major = int(icu.split(".")[0]) if icu[:1].isdigit() else 0
known = set()
for line in open(os.path.join(here, "known-failures.txt")):
    line = line.strip()
    if not line or line.startswith("#"):
        continue
    if line.startswith("icu<"):
        bound, path = line[4:].split(None, 1)
        if icu_major < int(bound):
            known.add(path.strip())
    else:
        known.add(line)
results = json.load(open(os.path.join(out, "results.json")))
bad = sorted({r["file"] + " (" + r["outcome"] + "): " + r["detail"][:160] for r in results
              if r["outcome"] in ("fail", "timeout") and r["file"] not in known})
print(f"ICU {icu}; unexpected failures: {len(bad)}")
print("\n".join(bad))
sys.exit(1 if bad else 0)
