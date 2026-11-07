#!/usr/bin/env python3
"""Fails (exit 1) when a test262 run is not the expected complete, unskipped run with only the reviewed failures.

Checks, against the pinned test262 revision used by CI:
  * every variant ran: at least EXPECTED_VARIANTS results (a runner crash or a silently dropped directory fails),
  * nothing was skipped (Jaunt implements every feature tested at this revision; a new skip must be a reviewed change),
  * every failure or timeout is listed in known-failures.txt.

known-failures.txt lines:
  `<test path>`            expected on every platform,
  `icu<NN <test path>`     expected only when the platform ICU major version is below NN (older locale data than the
                           reference ICU 78 / CLDR 48),
  `macos <test path>`      expected only on macOS (Apple's ICU ships locale data that differs from upstream CLDR).
The variant floor can be overridden with --expected-variants N (e.g. for a filtered local run: 0)."""
import json, os, sys

sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")

# test262 045bf6f9966ce3291b8fbc1e0403cd97b9201b00, `test/**` without `staging/`, sloppy/strict/module/raw variants
EXPECTED_VARIANTS = 100169

args = sys.argv[1:]
expected_variants = EXPECTED_VARIANTS
if "--expected-variants" in args:
    i = args.index("--expected-variants")
    expected_variants = int(args[i + 1])
    del args[i:i + 2]
out = args[0]
here = os.path.dirname(os.path.abspath(__file__))
icu = open(os.path.join(out, "icu-version.txt")).read().strip()
icu_major = int(icu.split(".")[0]) if icu[:1].isdigit() else 0
on_macos = sys.platform == "darwin"
known = set()
for line in open(os.path.join(here, "known-failures.txt")):
    line = line.strip()
    if not line or line.startswith("#"):
        continue
    if line.startswith("icu<"):
        bound, path = line[4:].split(None, 1)
        if icu_major < int(bound):
            known.add(path.strip())
    elif line.startswith("macos "):
        if on_macos:
            known.add(line[len("macos "):].strip())
    else:
        known.add(line)
results = json.load(open(os.path.join(out, "results.json")))
bad = sorted({r["file"] + " (" + r["outcome"] + "): " + r["detail"][:160] for r in results
              if r["outcome"] in ("fail", "timeout") and r["file"] not in known})
skipped = sorted({r["file"] + ": " + r.get("detail", "")[:120] for r in results if r["outcome"] == "skip"})
problems = []
if len(results) < expected_variants:
    problems.append(f"only {len(results)} of {expected_variants} variants have a result (incomplete run)")
if skipped:
    problems.append(f"{len(skipped)} skipped variant(s); nothing is expected to be skipped")
print(f"ICU {icu}; variants: {len(results)}; unexpected failures: {len(bad)}; skipped: {len(skipped)}")
print("\n".join(bad))
print("\n".join(skipped[:50]))
print("\n".join(problems))
sys.exit(1 if bad or problems else 0)
