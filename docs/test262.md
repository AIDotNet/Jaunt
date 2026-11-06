# test262 conformance

Measured 2026-11-06 with `tools/Jaunt.Test262` (follows tc39/test262 `INTERPRETING.md`: sloppy and strict
variants, `onlyStrict`/`noStrict`/`raw`/`module`/`async` flags, `negative` phase/type, includes, `$DONE`,
`$262.evalScript`, `$262.detachArrayBuffer`, `$262.createRealm`, `$262.agent`). Full generated report: [`test262-summary.md`](test262-summary.md).

| Item | Value |
| --- | --- |
| test262 revision | `045bf6f9966ce3291b8fbc1e0403cd97b9201b00` (the revision MoeJS reports against, for comparability) |
| Engine | Jaunt `0.1.0-alpha.1`, profile Standard, interpreter, CoreCLR .NET 10.0.12, osx-arm64 |
| Scope | `test/**` except `staging/` (ECMA-262, Annex B, Temporal and ECMA-402 `intl402/`) — 52,112 files |
| Variants | 100,169 total, all executed: pass 100,163 · fail 6 · skipped 0 |
| Skipped variants | none — every feature tested at this revision is implemented (`host-gc-required` tests exist only in `staging/`) |
| **Pass rate** | **99.994 %** (ECMA-262, Annex B, Temporal and all stage-3 proposals: 100 %; `intl402/`: 6,720 / 6,726 on ICU 78) |
| Wall time | ~210 s on 10 cores on an idle machine (one fresh `JsEngine` per variant; harness scripts compiled once and shared; multi-agent Atomics tests run after the main pass with at most 4 in parallel). On a heavily loaded machine some heavy RegExp property-escape / `decodeURI` tests exceed the 10 s per-test limit; they pass when rerun |

How to read this (research doc §3.6): a pass rate is only meaningful together with its scope. Skipped
tests are **not** passes; every skip reason is listed as unsupported in [`FEATURES.md`](FEATURES.md): proposals
that have not reached stage 4 at the pinned revision, `IsHTMLDDA`, legacy `caller`. Known failures remain failures;
they are not filtered out. `intl402/` runs against the platform ICU (ICU 78 / CLDR 48 on the reference machine).

## Progress during implementation

| Milestone | Executed | Pass | Pass rate |
| --- | ---: | ---: | ---: |
| first alpha (no async generators, BigInt, direct eval, `with`) | 62,688 | 59,607 | 95.09 % |
| + async generators, `for await`, parameter TDZ | 72,423 | 69,413 | 95.84 % |
| + direct `eval`, `with`, Annex B.3.3/B.3.4, BigInt, %TypedArray% species | 74,415 | 72,928 | 98.00 % |
| + resizable/transferable buffers, Atomics, iterator helpers, Set methods, completion values, mapped `arguments` | 80,049 | 79,711 | 99.58 % |
| + native RegExp engine (Unicode 17 properties, `v`, modifiers), JSON modules, `using`, base64, upsert, `Array.fromAsync` | 81,552 | 81,207 | 99.58 % |
| + typed-array/array-length fixes, compile and JSON performance work (re-verified) | 81,552 | 81,237 | 99.61 % |
| + spec async module evaluation, proper tail calls, JSON source text access, `Iterator.zip`/`chunks`/`windows`/`includes`, `waitAsync` + `notify`, full Unicode case mapping, exact number formatting, and the remaining Annex B / evaluation-order fixes | 82,028 | 82,028 | 100.00 % |
| + multiple realms (`$262.createRealm`), multi-agent `Atomics` (`$262.agent`, blocking `Atomics.wait`, `CanBlockIsTrue`), Temporal (ISO 8601 calendar; UTC, offset and IANA time zones) | 91,853 | 91,853 | 100.00 % |
| + ECMA-402 on the platform ICU (all Intl services), Temporal non-ISO calendars, `intl402/` added to the scope | 98,579 | 98,573 | 99.99 % |
| + all stage-3 proposals tested at this revision, `IsHTMLDDA`, legacy `caller`; nothing skipped | 100,169 | 100,163 | 99.994 % |

## Remaining failures

Six variants (three files) in `intl402/`, all caused by locale data or ICU behaviour rather than engine semantics:

- `DateTimeFormat/prototype/format/german-weekday-no-year.js` — CLDR 48 formats `de` `MMMMEd` as `Sa. 24. Dezember`;
  the test expects `Sa., 24. Dezember`.
- `DateTimeFormat/prototype/formatToParts/era.js` — ICU's Islamic calendars have a single era (AH); the test expects a
  distinct era name before the Hijra.
- `Temporal/PlainDate/from/calc-epoch-year-hebrew.js` — ICU's Hebrew calendar gives different Cheshvan/Kislev lengths
  for years 0–10 than the test's reference values.

Nothing is skipped. With an older platform ICU (e.g. Ubuntu 24.04's ICU 74 / CLDR 44) about 27 more `intl402/` files
fail because they encode newer locale data, tzdata links or ICU 76+ Chinese/Hebrew calendar results; they are listed
with an `icu<78` prefix in `tools/Jaunt.Test262/known-failures.txt`, which `tools/Jaunt.Test262/check-results.py`
(used by CI) applies according to the ICU version the run reports.

Other runtimes: on CoreCLR .NET 8.0.33 (self-contained) the same suite passes all 82,028 executed variants with
`--threads 4` (139 s). With 10 threads on a loaded machine one throw-heavy test (`decodeURI/S15.1.3.1_A1.11_T1`,
~65k `URIError`s) can exceed the 10 s per-test limit, because CLR exceptions are several times slower on .NET 8. `.github/workflows/verify.yml` runs the suite on Linux x64 (ICU 74),
Windows x64 (system ICU 77) and macOS ARM64 (ICU 76) for both runtimes; all three pass, judged by
`tools/Jaunt.Test262/check-results.py` against the reviewed `known-failures.txt`.

Harness notes: async tests run through `JsEngine.Evaluate` and complete via `$DONE` (awaiting the completion value
would add observable `then` calls); module tests load through `LoadModuleAsync` because any dependency may use
top-level await; metadata is parsed with every line terminator (some files use CR only).

## Robustness findings fixed while bringing the suite up

Running the whole suite in one process (10 parallel engines) surfaced engine-level defects, all fixed and covered
by unit tests:

1. A builtin reinterpreted a primitive receiver as an object through an unchecked cast (memory corruption,
   SIGSEGV). Unchecked accessors are now type-checked.
2. Several `Array.prototype` loops over array-likes with `length: 2**32-1` skipped holes before reaching a
   budget checkpoint, so `Timeout`/`MaxInstructions` could not stop them. Every builtin loop now ticks.
3. `export * as ns from './self.js'` recursed until the process died. Namespaces are published before being
   filled, and all recursive module-graph algorithms check the native stack.
4. A Proxy revoked from inside its own trap left the invariant checks with a null target. Traps now capture
   target and handler on entry, as the specification does.
5. Typed-array element access after a user callback shrank or detached the buffer indexed past the data.
   Element reads/writes are bounds-checked against the live buffer (invalid index ⇒ `undefined` / no-op).
6. Closures created in a `for (let … of <expr>)` head referenced an environment that did not exist at run time.
   The head now gets its own TDZ environment (ForIn/OfHeadEvaluation).
