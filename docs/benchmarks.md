# Benchmarks

Measured 2026-11-06 with `bench/Jaunt.Benchmarks` against **Jint 4.17.0** (stable NuGet release), using each
engine's optimized usage: Jint `PrepareScript`/`PrepareModule` and cached function handles, Jaunt compiled
`JsScript`/`JsModule` shared across engines. These are local measurements, not performance commitments.

## Environment

```text
BenchmarkDotNet v0.15.8, macOS Tahoe 26.6.2 (25G83) [Darwin 25.6.0]
Apple M5, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.401, runtime .NET 10.0.12, Arm64 RyuJIT (CoreCLR, tiered PGO default)
Job=ShortRun  IterationCount=3  LaunchCount=1  WarmupCount=3
```

The machine was a developer workstation with other processes running; `ShortRun` error bars are wide. Read
**ratios**, not absolute times. Reproduce with:

```bash
dotnet run -c Release --project bench/Jaunt.Benchmarks -- --filter '*' --job short
dotnet run -c Release --project bench/Jaunt.Benchmarks -- --quick     # in-process alternating harness
dotnet run -c Release --project bench/Jaunt.Benchmarks -- --realm     # engine-creation cost per intrinsic group
dotnet run -c Release --project bench/Jaunt.Benchmarks -- --compile   # compile phases (JAUNT_ALLOC=1 samples allocation types)
dotnet run -c Release --project bench/Jaunt.Benchmarks -- --micro file.js fnA fnB   # per-function Jaunt vs Jint
dotnet run -c Release --project bench/Jaunt.Benchmarks -- --run-loop arrays    # 8 s loop for dotnet-trace
```

## Warm invocation (`WarmInvocation`)

One engine per framework, script loaded once; each operation calls the exported workload function.
Ratio < 1 means Jaunt is faster.

| Workload | Jint | Jaunt | Ratio | Jint alloc | Jaunt alloc |
| --- | ---: | ---: | ---: | ---: | ---: |
| fib (recursive calls) | 2,151 µs | 803 µs | **0.37** | 1.2 KB | 0.1 KB |
| numeric loop | 4,281 µs | 1,518 µs | **0.35** | 9.2 MB | 0.1 KB |
| classes (fields, private, inheritance) | 5,138 µs | 1,944 µs | **0.38** | 3.9 MB | 2.0 MB |
| objects (alloc + property access) | 8,280 µs | 3,613 µs | **0.44** | 11.1 MB | 6.6 MB |
| arrays (higher-order builtins) | 1,487 µs | 1,069 µs | **0.72** | 746 KB | 798 KB |
| strings | 397 µs | 301 µs | **0.76** | 685 KB | 625 KB |
| JSON stringify + parse | 125 µs | 100 µs | **0.80** | 178 KB | 255 KB |

## Host → JS scalar call (`ScalarCalls`)

Fixed-arity call of a cached JS function with two numbers, returning a number.

| | Mean | Ratio | Allocated |
| --- | ---: | ---: | ---: |
| Jint | 71.3 ns | 1.00 | 104 B |
| Jaunt | 39.9 ns | **0.56** | 120 B |

## Isolation (`Isolation`)

| | Mean | Allocated |
| --- | ---: | ---: |
| Jaunt new engine (FreshRealm) | 1,280 ns | 14.3 KB |
| Jint new engine | 671 ns | 9.5 KB |
| Jaunt plugin request (new engine + module instantiate + JSON in + call + JSON out) | 4,467 ns | 30.6 KB |
| Jint plugin request (same steps) | 5,679 ns | 40.5 KB |

## Reading the results

These are single `ShortRun` measurements of CoreCLR .NET 10 on one busy developer workstation; they describe these
workloads on this machine, not Jaunt's performance in general (research doc §3.5/§7: no fixed multiples, no
"necessarily faster"). NativeAOT start-up is measured in "Cold start and shared costs" below, NativeAOT hot-path throughput in
"NativeAOT vs CoreCLR JIT" (the AOT gate itself checks behaviour, not speed).

* In these runs the interpreter-bound workloads (calls, numeric loops, classes, objects) took 0.35–0.44× of Jint's
  time; the register VM keeps numbers unboxed in 16-byte values and JS→JS calls don't recurse on the CLR stack.
* The builtin-heavy workloads (arrays, strings, JSON) took 0.72–0.80× of Jint's time; time is dominated by builtin
  code rather than dispatch.
* Creating a bare `JsEngine` is the one measurement where Jaunt is slower than Jint (about 2× here). Note the two
  "new engine" figures in this document measure different things: `Isolation.JauntNewEngine` only constructs the
  engine (1,280 ns), while the quick harness and `EngineCreation` also evaluate `1` (which initializes the intrinsic
  groups a script touches). In the end-to-end plugin request — the workload FreshRealm exists for — Jaunt took
  0.79× of Jint's time in `Isolation` (BenchmarkDotNet) and 0.46× in the quick harness (alternating in-process loop,
  later code); the two harnesses differ in warm-up and GC accounting, so read each ratio within its own table.
* History: the first implementation took 224 µs to create an engine and 840 µs per plugin request; lazy
  intrinsics, realm-independent shapes, predicted builtin layouts and removing per-realm dictionaries brought
  that to the numbers above.

## Cold start and shared costs

Research doc §3.5: a "new engine" figure is not a cold start, and process-wide shared costs must not vanish from the
denominator. Measured 2026-11-07 on the same machine (load average 12–24 during these runs; medians).

Whole process, start to printed result (`samples/Jaunt.AotHost run <file>`; 20 runs each; the NativeAOT binary is
6.4 MB, osx-arm64, net10.0; CoreCLR is the same host as a framework-dependent `dotnet Jaunt.AotHost.dll`):

| Script | NativeAOT | CoreCLR (JIT) |
| --- | ---: | ---: |
| `1 + 1` | 7.9 ms | 98.5 ms |
| `classes` workload + `main(1)` | 11.7 ms | 192.8 ms |

Inside one CoreCLR process (`--cold 15`: 15 fresh child processes; each creates 50 engines that evaluate `1` and
then the `classes` workload):

| | Time | Allocated |
| --- | ---: | ---: |
| process start → `Main` | 42.9 ms | — |
| engine #1 (type loading, JIT, process-wide shared state) | 155.7 ms | 2,129 KB |
| engines #2…#50, mean | 9.4 ms | 2,050 KB |
| managed memory still held once every engine is disposed (process-wide shared state: realm-independent shapes, predicted builtin layouts, tables) | — | 197 KB |

So on CoreCLR the first engine in a process costs ~17× a later one, almost all of it JIT and type loading;
NativeAOT removes that (the whole native process finishes in under 12 ms). The process-wide state Jaunt shares
between engines is about 0.2 MB. Reproduce:

```bash
dotnet run -c Release --project bench/Jaunt.Benchmarks -- --cold 15
dotnet publish samples/Jaunt.AotHost -f net10.0 -r osx-arm64 -c Release -o out && out/Jaunt.AotHost run file.js
```

## Other engines: Okojo and ClearScript/V8 (`EngineWarmInvocation`, `EngineHostCalls`, `EngineCreation`)

Research doc §1/§2/§4 names Okojo (managed register VM, the closest architecture) and ClearScript/V8 (native V8) as
required comparisons. `bench/Jaunt.Benchmarks/EngineComparison.cs` runs the same JS on all four engines, each used
the way its documentation recommends for repeated calls (compiled once, function handle cached):

* Jint **4.17.0** (stable), `PrepareScript`;
* Okojo **0.2.0-preview.2** (NuGet `Okojo.JavaScript.Embedding`; the snapshot the research reviewed; prerelease,
  .NET 10+), `realm.Execute` + `realm.Call`;
* ClearScript **7.5.1.1** (`Microsoft.ClearScript.V8` + native package; V8 JIT tiers enabled), `ScriptObject.InvokeAsFunction`.
  Host functions are measured both with the convenience binding (a delegate via `AddHostObject`) and with the
  FastProxy binding (`V8FastHostFunction`).

`--engines-check` verifies that all engines compute the same `main(1)` for every workload (they do). Measured
2026-11-07, same machine and job as above; the machine was loaded (load average 16.6 → 6.3 during the run), so
absolute times are inflated — **compare ratios** (engine time ÷ Jint time; < 1 is faster than Jint).

| Workload | Jaunt | Okojo | ClearScript V8 |
| --- | ---: | ---: | ---: |
| numeric loop | 0.42 | 0.49 | 0.12 |
| fib (recursive calls) | 0.40 | 0.31 | 0.02 |
| classes | 0.50 | 0.67 | 0.06 |
| objects | 0.37 | 0.63 | 0.009 |
| arrays | 0.74 | 1.16 | 0.13 |
| strings | 0.64 | 1.15 | 0.24 |
| JSON stringify + parse | 0.64 | 1.69 | 0.41 |

| JS → host calls (10,000 × `add(a, b)`) | Ratio to Jint | Managed allocation per run |
| --- | ---: | ---: |
| Jint (`Func<double,double,double>`) | 1.00 | 1.3 MB |
| Jaunt typed (`SetFunction((double a, double b) => …)`) | 0.28 | 120 B |
| Jaunt raw (`JsNativeFunction`) | 0.29 | 120 B |
| Okojo (`JsHostFunctionBody`) | 0.55 | 0 B |
| ClearScript V8, convenience (delegate host object) | 33.8 | 100.7 MB |
| ClearScript V8, FastProxy (`V8FastHostFunction`) | 0.97 | 297 B |

| Create an instance and evaluate `1` | Ratio to Jint | Managed allocation |
| --- | ---: | ---: |
| Jint | 1.00 | 9.5 KB |
| Jaunt | 2.6 | 15.1 KB |
| Okojo | 213 | 1,215 KB |
| ClearScript V8, new `V8ScriptEngine` (new isolate) | 515 | 9.5 KB (+ native) |
| ClearScript V8, engine from a shared `V8Runtime` | 303 | 5.9 KB (+ native) |

Retained memory per live instance (`--engines-memory 50`: 50 live instances, after full collections; ClearScript's
native V8 heap reported separately via `V8Runtime.GetHeapInfo().TotalHeapSize`; working-set deltas are noisy and
include code pages and allocator slack):

| Engine | Managed, empty | V8 heap, empty | Managed, `classes` loaded | V8 heap, `classes` loaded |
| --- | ---: | ---: | ---: | ---: |
| Jaunt | 22 KB | — | 53 KB | — |
| Jint 4.17.0 | 19 KB | — | 279 KB | — |
| Okojo 0.2.0-preview.2 | 1,373 KB | — | 1,608 KB | — |
| ClearScript V8 7.5.1.1 | 18 KB | 768 KB | 21 KB | 2,877 KB |

Reading: on these warm loops V8's optimizing JIT is 1.6× (JSON) to 41× (objects) faster than the fastest managed engine, at the cost of a native
isolate per engine (sub-millisecond to create, ~0.8–2.9 MB native heap each) and of a JS↔.NET boundary whose
convenience binding is expensive (FastProxy brings host calls to Jint's level). Among the managed engines, Jaunt and
Okojo are close on interpreter-bound code (Okojo ahead on recursive calls, Jaunt on object allocation and builtins);
Okojo creates its realm eagerly (≈1.2 MB per instance), Jaunt and Jint lazily. Reproduce:

```bash
dotnet run -c Release --project bench/Jaunt.Benchmarks -- --engines-check
dotnet run -c Release --project bench/Jaunt.Benchmarks -- --filter '*Engine*' --job short
dotnet run -c Release --project bench/Jaunt.Benchmarks -- --engines-memory 50
```

## Quick harness after the feature-complete pass

`--quick` (warm-up ≥ 1.5 s, alternating in one process; **ratio = Jint time / Jaunt time, > 1 means Jaunt is
faster**), same machine, after adding BigInt, direct `eval`/`with`, async generators, the native RegExp engine and the
remaining ES2025 library, and after the follow-up performance pass described below. The machine had a background
process (`cloudd`) holding one core busy; ratios from the alternating harness are stable, absolute times are not.

| Workload | Jaunt µs/op | Jint µs/op | Ratio | Alloc Jaunt / Jint (KB/op) |
| --- | ---: | ---: | ---: | ---: |
| numeric loop | 1,426 | 3,895 | 2.73× | 0.1 / 9,231 |
| fib(20) | 792 | 2,122 | 2.68× | 0.1 / 1.2 |
| objects | 3,453 | 8,358 | 2.42× | 6,563 / 11,056 |
| arrays | 1,101 | 1,569 | 1.43× | 798 / 746 |
| strings | 264 | 399 | 1.51× | 625 / 685 |
| classes | 1,920 | 7,390 | 3.85× | 1,992 / 3,940 |
| JSON stringify + parse | 130 | 170 | 1.31× | 191 / 178 |
| scalar host→JS call | 0.07 | 0.16 | 2.18× | 0.1 / 0.3 |
| JS→host typed callbacks | 198 | 1,053 | 5.32× | 0.2 / 703 |
| compile 18 KB | 706 | 677 | 0.96× | 915 / 740 |
| new engine | 2.9 | 2.7 | 0.92× | 8.8 / 9.5 |
| plugin request (end to end) | 10.8 | 23.6 | 2.18× | 27.4 / 40.5 |

What changed in the performance pass (each verified against the full unit and test262 suites):

* **Engine creation** (0.64× → 0.92×, 13.2 KB → 8.8 KB, now allocating less than Jint): the register stack is
  allocated on first call instead of eagerly (4 KB), and the frame pool starts smaller.
* **Compilation** (0.85× → 0.96×, 1.93 MB → 0.92 MB per 18 KB): one Acornima parser is cached per thread and
  option set (its string pool and scope buffers survive between parses); analysis maps are pre-sized from node
  counts gathered during parsing; `ResolvedRef` became a struct; per-statement closures and boxed statement-list
  enumerators were removed; the verifier reuses its scratch buffers; the per-token native-stack probe was dropped
  (Acornima's own `StackGuard` raises `InsufficientExecutionStackException`, still reported as
  `NestingTooDeep` — covered by a test that compiles 13 nesting attacks on a 256 KB thread).
  Profiling showed about two thirds of compile CPU time is GC, so allocation volume is what drives compile speed.
* **Arrays**: an inline-cache kind for `array.length` (was always a slow-path lookup); an append fast path for
  `a[a.length] = v`; a direct path for `arr.push(v)` on plain arrays with a clean prototype chain; vectorized
  fill/copy when growing backing stores.

Remaining gaps: compile is still slightly slower than Jint's `PrepareScript` (Jaunt additionally does scope
analysis, register allocation and bytecode verification up front, and the AST allocated by the shared parser is now
the largest item), and creating a bare engine is at rough parity. A JS function call costs about 23 ns (JS→JS
or native→JS); that is the main cost left in callback-heavy builtins such as `map`/`filter`.

## Cross-engine pass driven by `design/08-performance-optimization.md` (2026-11-07)

Same machine and harness as `bench/cross` (Apple M4, .NET SDK 10.0.401, Workstation concurrent GC; MoeJS
v0.1.0-alpha.11 on Go 1.27.3; Jint 4.17.0), two rounds, best of both. **The relative geometric mean did not move
(MoeJS/Jaunt 0.974 → 0.976, Jint/Jaunt 2.58 → 2.59): Jaunt's own geometric mean got 2.3% faster, and so did MoeJS in the
same session (machine state).** The targeted workloads moved; the untouched ones scatter within ±4%, which is this
machine's run-to-run noise.

| Workload | Jaunt before | Jaunt after | Δ | MoeJS/Jaunt before → after | Jaunt alloc/call |
| --- | ---: | ---: | ---: | ---: | ---: |
| compute/matmul | 9.03 ms | 5.39 ms | −40% | 0.58 → 0.91 | −91% |
| functional/higher-order | 12.85 ms | 10.38 ms | −19% | 0.54 → 0.66 | −33% |
| apps/graph-algos | 23.3 ms | 20.4 ms | −12% | 0.71 → 0.80 | |
| compute/fib | 3.99 ms | 3.56 ms | −11% | 0.60 → 0.66 | |
| compute/sha256 | 2.91 ms | 2.63 ms | −10% | 0.94 → 1.03 | |
| compute/game-of-life / sieve | 28.4 / 16.8 ms | 26.4 / 15.7 ms | −7% / −6% | 1.01 → 1.14 / 0.86 → 0.93 | |
| compute/sort-compare | 14.65 ms | 13.74 ms | −6% | 0.66 → 0.69 | −22% |
| collections/array-ops | 3.98 ms | 3.82 ms | −4% | 0.67 → 0.68 | −27% |
| apps/state-reducer | 64.3 ms | 62.8 ms | −2% | 0.90 → 0.87 | −37% |

Isolated micro-workloads (appendix A of the design document; MoeJS/Jaunt > 1 means Jaunt is faster):

| Micro | Jaunt before | Jaunt after | MoeJS | MoeJS/Jaunt | Bytes/call before → after |
| --- | ---: | ---: | ---: | ---: | ---: |
| `c[i] += 0.5*b[i]` on Float64Array (115k) | 6.81 ms | 3.10 ms | 3.66 ms | 1.18 | 2.0 MB → 37 KB |
| Float64Array read (115k) | 1.52 ms | 1.10 ms | 0.91 ms | 0.83 | |
| `[...items, x]` ×1500 | 2.79 ms | 1.40 ms | 10.9 ms | 7.8 | 53 MB → 18 MB |
| `flatMap` over 10k objects ×3 | 6.26 ms | 3.64 ms | 3.30 ms | 0.91 | 10.9 MB → 6.7 MB |
| plain object, numeric keys (100k) | 6.91 ms | 4.27 ms | 3.25 ms | 0.76 | |
| plain object, string keys (100k) | 7.42 ms | 4.83 ms | 8.19 ms | 1.69 | |
| regex literal `.test` in a function (10k) | 1.71 ms | 1.48 ms | 1.24 ms | 0.84 | |
| JS→JS call (100k) | 23.5 ns | 21.7 ns | 19.2 ns | 0.89 | |
| builtin → JS callback (`forEach`, 100k) | 26.6 ns | 24.1 ns | 16.8 ns | 0.70 | |

What changed (every item verified against the unit tests, the unit tests with `JAUNT_POISON_REGISTERS=1`, the
full test262 run at the pinned revision — 100,167/100,169, the two known Apple ICU data differences — and the
NativeAOT gate):

* **Index keys stay numeric.** `ToPropertyKey` returns an array-index number unchanged (it has no observable conversion)
  and the opcode is in the hot loop; compound and logical assignment on `a[i]` therefore keep the element fast paths
  instead of converting the index to a string per access (the 2 MB/call of matmul).
* **Typed arrays** read and write their backing store directly for the eight number kinds (one bounds check for the
  whole view; length-tracking and out-of-bounds views, Float16 and BigInt views take the checked path).
* **Computed-key inline cache** for ordinary objects: a per-site (shape, key) → slot entry, backed by a Vm-wide
  direct-mapped (shape, key) cache for sites that cycle through many keys (objects used as maps).
* **Array literal spread** reserves room for the elements that follow (`[...a, x]` allocated 3× its size).
* **Sort** collects into one exactly sized buffer and merges through a reusable Vm scratch (no `List` + `ToArray` +
  temporary — four LOH allocations per 20k-element sort became one); a closure comparator is pushed and run directly.
* **Regexp literals** cache the compiled program at the evaluating site (no process-wide lock per evaluation).
* **Dense fast paths** in `flat`/`flatMap`, `lastIndexOf`, `copyWithin`, `toReversed`/`with`/`toSpliced`; callback
  builtins (`map`/`filter`/`forEach`/`some`/`every`/`find*`/`reduce*`/`flatMap`) call a plain closure without the
  per-element delegate and native-stack probe.
* **Calls:** registers are no longer cleared when a frame returns (the stack is cleared once when the root execution
  ends; a function's own-scope registers are initialized on entry, see `FunctionTemplate.InitUndefEnd`), and
  `Call`/`CallMethod` push the callee frame straight from the caller's register window. JS→JS call: 23.5 → 21.7 ns.

Remaining gap to MoeJS (fib, sort-compare, spectral-norm, higher-order, huffman-lz, crc-base64, levenshtein,
json-roundtrip, classes-inheritance; 1.3–1.5×): the per-call cost (~21 ns vs 16–19 ns) and dispatch density
(~5 ns per instruction). Stages B/C of `design/08` (frame setup folding, call-site IC, unchecked operand access
justified by the verifier, superinstructions, JSON output buffering) target these.

## Second cross-engine pass: inline caches for accessors and absences, string iteration (2026-11-07)

Same harness, two rounds, best of both; MoeJS measured the same as in the previous pass (geometric mean ×0.999),
so this time the relative numbers are comparable. **MoeJS/Jaunt geometric mean 0.976 → 1.013** (Jaunt now
slightly ahead overall; Jint/Jaunt 2.59 → 2.68). Jaunt alone: ×0.963. Fastest per workload: Jaunt 26, MoeJS 30, Jint 3.

| Workload | Jaunt before | Jaunt after | Δ | MoeJS/Jaunt before → after |
| --- | ---: | ---: | ---: | ---: |
| apps/huffman-lz | 24.4 ms | 14.7 ms | −40% | 0.69 → 1.17 |
| compute/crc-base64 | 14.9 ms | 10.9 ms | −27% | 0.68 → 0.93 |
| compute/spectral-norm | 7.95 ms | 5.84 ms | −27% | 0.73 → 0.98 |
| strings/unicode-text | 3.82 ms | 2.84 ms | −26% | 0.93 → 1.26 |
| compute/levenshtein | 6.74 ms | 5.24 ms | −22% | 0.70 → 0.90 |
| objects/alloc-shapes / classes-inheritance | 7.80 / 7.50 ms | 7.32 / 7.06 ms | −6% | 1.11 → 1.18 / 0.71 → 0.75 |
| advanced/json-roundtrip | 10.1 ms | 9.6 ms | −4% | 0.71 → 0.74 |

Per category (MoeJS/Jaunt): apps 0.92 → 0.97, compute 0.92 → 0.97, strings 1.06 → 1.14, objects 1.10 → 1.13,
collections 0.97 → 0.98, advanced 0.97 → 0.99, functional 1.17 → 1.16, async 0.83 → 0.82.

Found by aggregating the receivers and IC states of every `GetPropSlow` call (a temporary counter): the profiler
had shown `GetPropSlow` at 33% of spectral-norm, 26% of crc-base64 and 16% of levenshtein, but not why.

* **Accessor properties never entered the inline caches.** `u.length` on a typed array, `map.size`, every class
  getter and every `get` on a prototype did a full lookup (string hash, prototype walk) on each access, then called
  the getter. The getter slot is now cached like a data slot (`IcKind.OwnGetter`/`ProtoGetter`, guarded by the shape
  and the prototype epoch; the slot is re-read on every hit, so a redefined getter is seen). Typed-array `length`
  read: 28 → 12 ns.
* **A string receiver's methods missed in `GetProp`.** The `StringProtoData` cache kind was honoured by `CallMethod`
  only; `a.charCodeAt(i)` compiled through `GetProp` + `Call` took the slow path 64 million times per levenshtein run.
* **Absent properties were not cached.** `node.ch` on a tree whose leaves have `ch` and inner nodes do not walked
  the chain every time. `IcKind.NotFound` caches the absence for a receiver shape while the prototype epoch is
  unchanged (and every object on the chain is in shape mode).
* **Megamorphic sites stayed slow for good.** After eight shapes a site did a full lookup per access. A Vm-wide
  direct-mapped (shape, name) → lookup-result cache now sits behind megamorphic sites (own/prototype data, getters,
  absences); 4-shape polymorphic access: 23 ns.
* **`for (const c of str)`** walked the iterator protocol (a result object and two property reads per code point).
  With String.prototype[Symbol.iterator] and %StringIteratorPrototype%.next unmodified (the same pristine guard the
  array and Map paths use), the record yields code points directly. huffman-lz −31% from this alone.
* **RegExp objects** carry one reference to the shared, immutable compiled program (backends, group tables, source,
  flags, pre-parsed flag bits) instead of a copy of eleven fields and four flag scans per literal evaluation:
  regex-literal micro 1.48 → 1.27 ms, 420 → 340 bytes per evaluation (the rest is the match result).

Tried and reverted (no measurable gain): reading operands and registers in the interpreter loop without bounds
checks (justified by the verifier) — dispatch-dense workloads moved −3…+7%, within noise; the checks are
predicted and effectively free on this machine.

Verified: unit tests (plain and `JAUNT_POISON_REGISTERS=1`), 13 new `FastPathSemanticsTests` cases (getter
redefinition and prototype mutation, absence then addition on the prototype and on `Object.prototype`, a Proxy
on the chain, megamorphic sites with later redefinition, patched `String.prototype.charCodeAt`, string iteration
with surrogate pairs and patched `Symbol.iterator`/`next`), test262 100,167/100,169, NativeAOT gate unchanged.

## NativeAOT vs CoreCLR JIT: hot-path throughput (2026-11-07)

Every other table in this document runs Jaunt on CoreCLR with the JIT. This one runs the same host twice: `samples/Jaunt.AotHost
bench` as the published NativeAOT binary (`PublishAot=true`, 7.1 MB, osx-arm64) and as the framework-dependent
`dotnet Jaunt.AotHost.dll`, over the `bench/cross/workloads` corpus under the cross-engine protocol
(`bench/cross/dotnet/CrossProtocol.cs`: compile, cold call checked against Node, warm-up until the tiers settle, 7 samples of
~60 ms, wall and CPU time per call). Script: `bench/cross/aot-vs-jit.sh`; raw results `artifacts/cross/jaunt-{aot,jit}.jsonl`.

Environment: Apple M4 developer workstation (load average ~3 during the run), .NET SDK 10.0.300; CoreCLR runtime
**10.0.8**, ILCompiler/runtime pack **10.0.14** for the native binary — the two sides therefore run slightly different BCL
builds. The host sets `InvariantGlobalization=true`, so `String.prototype.normalize` is a no-op for non-ASCII text on both
sides (`strings/unicode-text` is flagged `mismatch` against Node on both for that reason; its timings are still comparable
between AOT and JIT). Engine source as of this commit plus the uncommitted working tree of that day. 59 workloads, one pass.

| Geometric mean, AOT time / JIT time | |
| --- | ---: |
| warm per-call wall time | **1.10** |
| warm per-call CPU time | 1.10 |
| cold (new engine + load + first call) | **0.70** |
| bytes allocated per call | 1.00 |

Per category (warm): strings 1.04, collections 1.07, async 1.08, apps 1.08, compute 1.09, objects 1.12, advanced 1.13,
functional 1.26. 20 of 59 workloads were within ±3% either way; AOT was faster on 14 (best: compute/float-loop 0.94,
compute/matmul 0.96, strings/string-hash-codec 0.97, apps/huffman-lz 0.98).

Largest gaps:

| Workload | JIT | NativeAOT | AOT/JIT |
| --- | ---: | ---: | ---: |
| compute/fib | 3.05 ms | 6.16 ms | 2.02 |
| functional/control-flow | 4.37 ms | 7.54 ms | 1.73 |
| apps/state-reducer | 47.3 ms | 75.8 ms | 1.60 |
| advanced/iterator-helpers | 3.42 ms | 4.71 ms | 1.37 |
| objects/prototype-chain | 6.87 ms | 8.95 ms | 1.30 |
| advanced/dynamic-code | 1.81 ms | 2.28 ms | 1.26 |
| compute/sieve | 16.1 ms | 20.0 ms | 1.24 |
| async/event-loop-scheduler | 2.39 ms | 2.91 ms | 1.22 |
| compute/sort-compare | 13.9 ms | 16.7 ms | 1.21 |

Reading: on these workloads NativeAOT costs about 10% of warm throughput overall and gains 30% on the cold call (no JIT, no
type loading). Allocation is identical, so the gap is code quality, not behaviour: NativeAOT compiles once, without the
tiered PGO the JIT applies on CoreCLR (guarded devirtualization of interface and virtual calls, hot/cold block layout,
profile-driven inlining). The workloads that lose most are the ones dominated by interpreter dispatch and call/return
(`fib`, `control-flow`, `state-reducer`, `prototype-chain`); the numeric kernels whose time is in straight-line arithmetic
(`float-loop`, `matmul`, `mandelbrot`, `nqueens`) are even. A follow-up that wants to close the gap should look at the
Vm call path and the interpreter's dispatch for virtual/interface calls that PGO devirtualizes on CoreCLR but AOT cannot
(static PGO via `dotnet publish` with MIBC data, or making those call sites monomorphic in the source).

## Third cross-engine pass: the call path (2026-11-07)

Same harness, two rounds, best of both; MoeJS measured the same as in the previous pass (×0.999). **MoeJS/Jaunt
geometric mean 1.013 → 1.028**, Jint/Jaunt 2.68 → 2.72; Jaunt alone ×0.985. Fastest per workload: Jaunt 28, MoeJS 29,
Jint 2. Per category (MoeJS/Jaunt): apps 0.97 → 1.00, compute 0.97 → 1.00, functional 1.16 → 1.21, the rest within ±0.01.

| Workload | Jaunt before | Jaunt after | Δ | MoeJS/Jaunt before → after |
| --- | ---: | ---: | ---: | ---: |
| compute/fib | 3.57 ms | 3.00 ms | −16% | 0.64 → 0.78 |
| compute/sha256 | 2.51 ms | 2.28 ms | −9% | 1.08 → 1.18 |
| apps/raytracer | 6.58 ms | 5.98 ms | −9% | 1.10 → 1.23 |
| functional/higher-order | 10.9 ms | 9.9 ms | −9% | 0.62 → 0.68 |
| compute/nqueens | 1.06 ms | 0.99 ms | −6% | 1.18 → 1.27 |
| objects/prototype-chain | 7.25 ms | 6.83 ms | −6% | 1.44 → 1.53 |
| apps/mini-interpreter | 1.57 ms | 1.50 ms | −5% | 0.87 → 0.90 |
| compute/spectral-norm / sort-compare | 5.84 / 13.8 ms | 5.61 / 13.3 ms | −4% | 0.98 → 1.02 / 0.69 → 0.72 |

Workloads with few JS→JS calls moved within the usual ±4% (sieve +4%, map-set +3%, classes-inheritance +2%,
destructure-spread +2%: noise, the code they run is unchanged).

What changed (`design/08`, §10; every step measured on four call micro-benchmarks and the JIT's disassembly of
`PushCallFrame`):

* **Overlapping register windows.** The callee's register window now starts at the caller's first argument
  register for `Call`, `CallMethod` and `New`, so arguments are never copied (each copy was ~20 instructions plus a
  GC write barrier). This relies on the compiler's stack-shaped register allocation: a call's arguments are the
  topmost live registers at the call, so the callee may clobber everything above them. Coroutine frames own their
  registers and keep the copying path, as do callees that do not fit in the current chunk. Frames record the stack
  position they were pushed at and restore it exactly on return. The `JAUNT_POISON_REGISTERS=1` mode now also
  poisons the caller's registers from the first argument up when such a frame returns, so a later read would fail
  a test; unit tests and test262 pass in that mode.
* **Frames addressed by depth.** The frame stack is an array indexed by depth instead of a linked list with a
  "current frame" field: three fewer reference stores (write barriers) per call and return. Measured −3% on fib:
  the barriers were a small part of the cost, which is why the design's C5 (frames as structs) is not pursued.
  The depth budget now counts bytecode frames and native nesting separately (`Sp + _nativeDepth`).
* Smaller items: `this` for a plain call of a sloppy function is compared against the realm's global object
  directly instead of being materialized and compared as a 16-byte value; the constant pool is cached on the frame;
  `PopFrame` is inlined into the interpreter loop; the class-constructor check folded into the template flag mask
  (the design's C3: no measurable gain on its own).

Micro-benchmarks (`--run-loop`, µs per run): fib(24) 3498 → 2862 (−18%), 100k plain calls 2160 → 1704 (−21%),
100k method calls 2976 → ~2850 (−4%), 600k two-argument calls 14247 → 9977 (−30%), 100k `new P(x, y)` 5335 → 4990
(−6.5%). JS→JS call ≈ 21.7 → 17 ns (fib), inside the 16–19 ns MoeJS range; the remaining gap on fib is the
dispatch of the arithmetic and comparison around the call.

Verified: unit tests 578/578 plain and poisoned, 11 new `FastPathSemanticsTests` cases for the call path (extra
and missing arguments with `arguments`/rest, parameter mutation, nested calls inside argument lists, temporaries
live across calls, catch/finally in the caller, sloppy/strict `this` with primitive receivers, legacy `caller`,
stack traces, calls from generators and async functions, `new` with extra arguments and derived classes, proper
tail calls), test262 100,167/100,169 in both modes.

## Fourth cross-engine pass: Map/Set tables, JSON reviver, result arrays, GC guidance (2026-11-08)

Same harness, two rounds, best of both; MoeJS measured the same as in the previous pass (×1.001). **MoeJS/Jaunt
geometric mean 1.028 → 1.049**, Jint/Jaunt 2.72 → 2.77; Jaunt alone ×0.982. Fastest per workload: Jaunt 31, MoeJS 26,
Jint 2. Per category (MoeJS/Jaunt): collections 0.97 → 1.02, advanced 0.99 → 1.04, apps 1.00 → 1.03, the rest within ±0.02.

| Workload | Jaunt before | Jaunt after | Δ | MoeJS/Jaunt before → after |
| --- | ---: | ---: | ---: | ---: |
| apps/state-reducer | 61.1 ms | 49.3 ms | −19% | 0.91 → 1.12 |
| collections/map-set | 4.04 ms | 3.28 ms | −19% | 0.72 → 0.89 |
| advanced/json-roundtrip | 9.68 ms | 7.94 ms | −18% | 0.73 → 0.90 |
| strings/concat-build | 1.07 ms | 0.99 ms | −7% | 0.97 → 1.05 |
| objects/dynamic-props | 1.68 ms | 1.56 ms | −7% | 1.05 → 1.13 |
| compute/crc-base64 / apps/huffman-lz | 10.9 / 14.6 ms | 10.3 / 13.9 ms | −5% | 0.93 → 0.98 / 1.15 → 1.22 |
| functional/higher-order | 9.94 ms | 10.8 ms | +8% (see below) | 0.68 → 0.63 |

Sampling (`dotnet-trace`) of map-set, json-roundtrip and higher-order showed that none of them is bound where the
design expected: **GC pauses are 20–26% of wall time** (`--run-loop` pause counters: 2.06 s, 2.10 s and 1.57 s of 8 s),
with a gen2 collection on nearly every run, because an 8000-entry Map or Set and the doubling chain of a 10000-element
result array are large-object-heap allocations and the LOH budget triggers gen2. What changed (`design/08`, §11):

* **Map/Set tables** (`OrderedTable`): the `Dictionary<JsValue, int>` index is replaced by an inline chained hash index
  over the insertion-ordered slots (three `int` arrays), the hash is computed once per operation with number, string
  and object fast paths, and a Set stores no value array: ~70 → ~40 bytes per entry, fewer and smaller LOH arrays.
  Numbers and object identities go through a MurmurHash3 finalizer before bucketing (an integer-valued double differs
  from its neighbours only in its high bits; the first version without it was 6.6× slower on integer keys); strings
  keep the runtime's randomized ordinal hash, so attacker-chosen keys cannot be made to collide. String-key micro:
  4.38 → 2.78 ms (−37%, together with the concatenation change below); integer-key micro unchanged at ~20 ns per
  `has`/`get`, which is the method call, not the lookup.
* **JSON.parse with a reviver** (A4, the measured bottleneck of json-roundtrip; stringify was 14% of it): the per-property
  context object uses a shared one-slot shape instead of a fresh object plus a shape transition, keys of shape-mode
  objects come straight from the layout, the argument array is reused, and the source-text records keep an ordered
  list matched by a cursor instead of a `Dictionary` per object (a repeated key, detected from the property count,
  falls back to a search). json-roundtrip −16% in `--run-loop`. The design's chunked output buffer was not built:
  the serializer already reuses a thread-cached `StringBuilder`, and `Quote`/`Append` were under 5% of the samples.
* **filter / flat / flatMap results** (A5): elements are collected in the Vm's scratch buffer (shared with sort) and
  the result gets an exactly sized element store at the end, instead of growing from empty by doubling (about twice
  the final size allocated, every piece of a large result on the LOH). Only for a fresh intrinsic Array result; a
  species-created result may be observed by user code and is appended to as before. A/B in `--run-loop`, alternating
  builds three times each: 9.8–10.0 ms with the buffer vs 10.6–11.0 ms without (−7%). The cross-engine table above
  shows higher-order +8%: that workload is GC-bound and two consecutive `--run-loop` runs of the same build differed by
  30%, so its cross-engine figure is inside the noise; the controlled A/B is the number to trust.
* **String + integer** (`'k' + i`, `i + 'px'`): the digits are written straight into the result string in one
  allocation instead of formatting a digit string first (same text, `-0` prints "0").

**GC configuration (host-level, design §4.7).** These knobs belong to the host process (`runtimeconfig.json` or
`DOTNET_*` environment variables), not to the engine, and `bench/cross` keeps the defaults. Measured here with
`--run-loop` on the same build:

| Configuration | map-set | json-roundtrip | higher-order |
| --- | ---: | ---: | ---: |
| default (workstation, concurrent) | 3.26 ms, pause 1.94 s | 8.28 ms, pause 2.00 s | 9.45 ms, pause 1.53 s |
| `GCgen0size=0x4000000` (64 MB) | 2.44 ms (−25%) | 7.15 ms (−14%) | 11.0 ms (+17%) |
| `gcConcurrent=0` | 2.53 ms (−22%) | 7.28 ms (−12%) | 8.90 ms (−6%) |
| `GCLOHThreshold=0x100000` (1 MB) | 2.50 ms (−23%), gen2 491 → 1 | 7.88 ms (−5%) | 9.17 ms (−3%), gen2 507 → 0 |

Raising the LOH threshold to 1 MB removes almost every gen2 collection in allocation-heavy scripts without a
downside in these workloads; a larger gen0 helps short-lived object churn but hurts higher-order, so it is not a
general recommendation.

Verified: unit tests 578/578 plain and poisoned, 17 new `FastPathSemanticsTests` cases (Map/Set deletion and insertion
during iteration, compaction under a live iterator, −0/NaN keys, rope and mixed-type keys, clear during iteration, a
20000-entry table; reviver with duplicate keys, `context.source`, holder mutation, index-like key order, nested arrays;
string + integer edge cases; result collection with a user species, re-entrant filter/flatMap, a throwing callback,
70000-element results), test262 100,167/100,169 in both modes.
