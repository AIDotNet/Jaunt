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
"necessarily faster"). NativeAOT throughput is not measured here (the AOT gate checks behaviour, not speed); NativeAOT start-up is, in
"Cold start and shared costs" below.

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
