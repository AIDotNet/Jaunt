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

* Interpreter-bound workloads (calls, numeric loops, classes, objects) are 2.3–2.9× faster than Jint; the
  register VM keeps numbers unboxed in 16-byte values and JS→JS calls don't recurse on the CLR stack.
* Builtin-heavy workloads (arrays, strings, JSON) are 1.25–1.4× faster; time is dominated by builtin code
  rather than dispatch. (JSON allocation has since been brought to within 7 % of Jint — see the last section.)
* A bare `JsEngine` is still ~2× slower to create than a Jint `Engine`, even with lazily created intrinsic
  groups and shared builtin layouts. In the end-to-end plugin request — the workload FreshRealm exists for —
  Jaunt is ~1.27× faster overall.
* History: the first implementation took 224 µs to create an engine and 840 µs per plugin request; lazy
  intrinsics, realm-independent shapes, predicted builtin layouts and removing per-realm dictionaries brought
  that to the numbers above.

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
