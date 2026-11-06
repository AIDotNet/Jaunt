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
| MoeJS | v0.1.0-alpha.11 | `moejs.NewRuntime(moejs.Options{})` — MoeJS's default: intrinsics built once per process, **deeply frozen and shared** by every runtime (`MutableIntrinsics: false`) |

So the per-runtime creation and first-call figures (`coldUs` in the raw output: new engine + load + first call; not a
process cold start) compare engines that build per-engine mutable intrinsics (Jaunt, Jint) with one that shares a
frozen set (MoeJS) — MoeJS does not pay intrinsic creation per runtime, and scripts that modify builtins behave
differently there. Warm per-call figures are not affected by this choice. A like-for-like creation comparison would
use `Options{MutableIntrinsics: true}` for MoeJS, or Jaunt's `JsProfile.Hardened` (frozen intrinsics, no string code
generation) against MoeJS's default.

Process cold start and the process-wide shared state of Jaunt are measured separately (`--cold`, see
`docs/benchmarks.md`, "Cold start and shared costs").
