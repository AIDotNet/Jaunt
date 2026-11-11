# Cross-engine workloads (Jaunt · Jint · MoeJS)

`run.sh` runs the self-written workloads in `workloads/` (no third-party or AGPL workloads; research doc §7) on Jaunt
and Jint (`bench/Jaunt.Benchmarks --cross`) and on MoeJS (`moejs-runner`, Go), one engine after another, and checks
every result against `expected.json` (produced with Node). It is a development harness; published comparisons are in
[`docs/benchmarks.md`](../../docs/benchmarks.md).

## Engine configurations (research doc §3.5: mutable and frozen intrinsics must not be compared silently)

| Engine | Version | Configuration used |
| --- | --- | --- |
| Jaunt | this repository | `new JsEngine()` — `JsProfile.Standard`: **mutable** intrinsics, created per engine (lazily, per group) |
| Jint | 4.17.0 | `new Engine()` — mutable intrinsics per engine, prepared scripts |
| MoeJS | source `9341716` (2026-11-09; Go pseudo-version `v0.1.1-0.20261109125924-934171643e14`) | `moejs.NewRuntime(moejs.Options{})` — MoeJS's default: intrinsics built once per process, **deeply frozen and shared** by every runtime (`MutableIntrinsics: false`) |

So the per-runtime creation and first-call figures (`coldUs` in the raw output: new engine + load + first call; not a
process cold start) compare engines that build per-engine mutable intrinsics (Jaunt, Jint) with one that shares a
frozen set (MoeJS) — MoeJS does not pay intrinsic creation per runtime, and scripts that modify builtins behave
differently there. Warm per-call figures are not affected by this choice. A like-for-like creation comparison would
use `Options{MutableIntrinsics: true}` for MoeJS, or Jaunt's `JsProfile.Hardened` (frozen intrinsics, no string code
generation) against MoeJS's default.

Process cold start and the process-wide shared state of Jaunt are measured separately (`--cold`, see
`docs/benchmarks.md`, "Cold start and shared costs").

## NativeAOT vs CoreCLR JIT (`aot-vs-jit.sh`)

The measurement protocol is one source file, `dotnet/CrossProtocol.cs`, linked into both `bench/Jaunt.Benchmarks`
(`--cross`) and `samples/Jaunt.AotHost` (`bench` subcommand). `aot-vs-jit.sh` publishes the AOT host (`PublishAot=true`,
net10.0, this machine's RID), builds the same project framework-dependent, and runs the corpus on each — the native binary
reports itself as engine `jaunt-aot`, `dotnet Jaunt.AotHost.dll` as `jaunt-jit` (told apart by the JIT's compiled-method
counter; `RuntimeFeature.IsDynamicCodeSupported` is false in both because `PublishAot` writes that switch into the JIT
build's runtimeconfig too). Every result is checked against `expected.json`, and the report flags any workload whose AOT and
JIT results differ. `report_aot.py` prints warm, CPU and cold figures side by side with the AOT/JIT ratio and geomeans
(overall and per category); `METRIC=cpu` compares CPU time. Results go to `artifacts/cross/jaunt-{aot,jit}.jsonl` and
`artifacts/cross/aot-vs-jit.json`; a published comparison is in `docs/benchmarks.md`, "NativeAOT vs CoreCLR JIT".

```bash
bench/cross/aot-vs-jit.sh                         # whole corpus
bench/cross/aot-vs-jit.sh --filter apps/ --samples 3
TFM=net8.0 bench/cross/aot-vs-jit.sh              # another framework channel
artifacts/cross/aot-host/Jaunt.AotHost bench bench/cross/workloads bench/cross/expected.json --filter compute/   # one side by hand
```
