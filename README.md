# Jaunt

A fully managed, **NativeAOT-compatible** JavaScript engine for .NET 8 / .NET 10 / .NET 11, built for
embedded scripts, rules and plugins: low host-boundary cost, shareable compiled code, isolated realms, budgeted
execution and explicit, reflection-free data mapping.

> Status: `0.1.0-alpha.1`. Design documents live in [`design/`](design/); the implementation follows the route
> chosen in [`design/01-research.md`](design/01-research.md) — **Acornima parser → Jaunt scope analysis /
> register bytecode → managed register VM**, with NativeAOT execution of post-publish source as a hard gate.
> See [`design/07-implementation-status.md`](design/07-implementation-status.md) for the evidence behind each
> research conclusion, and [`docs/FEATURES.md`](docs/FEATURES.md) for the exact supported surface.

```csharp
using Jaunt;

using var engine = new JsEngine();
engine.SetValue("price", 100.0);
engine.SetFunction("tax", (double amount) => amount * 0.13);
double total = engine.Evaluate("price * 2 + tax(price * 2)").AsNumber();   // 226
```

## Architecture (short)

```text
JsCompiler ── Acornima AST (discarded) ─▶ ScopeAnalyzer ─▶ CodeGen ─▶ verified, immutable bytecode (JsScript / JsModule)
                                                                         │ shared by any number of engines/threads
JsEngine ── Realm (globals, lazily built intrinsics, module instances) ──┤
         └─ Vm: register interpreter, heap frames (no CLR recursion for JS calls), per-realm inline caches,
            job queue, budgets (time / work / depth / tracked bytes / output), cancellation
```

* **Values**: 16-byte `JsValue` (GC-visible reference + 64-bit payload; doubles XOR-encoded so `default` is
  `undefined`; references are never hidden in the bits).
* **Objects**: hidden-class shapes + slots, dictionary fallback; shapes for `Object/Function/Array.prototype`
  roles are realm-independent and shared process-wide (lock-free reads, bounded growth), so FreshRealm
  workloads don't rebuild layouts per request.
* **Inline caches** (own/prototype data, add-property transitions, globals, primitive string receivers) are
  realm-private feedback; compiled code holds no realm state.
* **Intrinsics** are created lazily per group (Array, String, Map/Set, Promise, …) on first use; builtin layouts
  are predicted from the first realm and filled directly; builtin methods materialize on first read.
* **NativeAOT**: no `Reflection.Emit`, expression compilation, `DynamicInvoke` or reflection fallback anywhere;
  `eval`/`Function` compile new Jaunt bytecode at run time and work in AOT binaries.

## API at a glance

| Need | API |
| --- | --- |
| evaluate source / reuse compiled code | `engine.Evaluate(string)`, `JsCompiler.CompileScript` → `engine.Evaluate(JsScript)` |
| ES modules | `JsCompiler.CompileModule`, `engine.LoadModule(module, resolver)`, `LoadModuleAsync` (top-level await), `instance.GetFunction` (fixed) / `GetExport` (live binding) |
| call JS from C# | `engine.Invoke(fn, a0, a1)`, `InvokeWithThis`, `InvokeAsync` |
| host functions | typed `SetFunction<T1,…,TR>` (static adapters), raw `JsNativeFunction(ref JsCallContext)`, `SetAsyncFunction` |
| one budget for a whole request | `engine.BeginExecution(options, token)` → `ParseJson` / `Invoke` / `ToJsonUtf8` / `FromObject` / `ToObject` → `Complete()` |
| DTO mapping | `JsTypeInfo.Object<T>()…Build()` by hand, or `[JsSerializable(typeof(T))] partial class X : JsBindingContext` with `Jaunt.Generators` |
| production plugins | `JsPlugin.Create(module, "export", inInfo, outInfo, options)` → FreshRealm per call, bounded concurrency |
| limits / isolation | `JsLimits` (Timeout, MaxInstructions, MaxStackDepth, MaxTrackedBytes, MaxOutputBytes, …), `JsProfile.Hardened` |

Errors: `JsCompileException` (with code/line/column), `JsException` (JS throw; engine stays usable),
`JsLimitExceededException` / `OperationCanceledException` (engine faulted), `JsAsyncRequiredException`,
`JsEngineFaultedException` (`UnjoinedAsyncWork`, internal errors), `JsConversionException` (member path).

## Repository layout

| Path | Content |
| --- | --- |
| `src/Jaunt` | the engine (`net8.0;net10.0`, plus `net11.0` when built with the .NET 11 SDK) |
| `src/Jaunt.Generators` | Roslyn source generator for DTO bindings (diagnostics `JAUNT001`–`JAUNT005`) |
| `tests/Jaunt.Tests` | xUnit: language semantics, API contracts, limits, isolation, modules, async, DTO/JSON, generator |
| `samples/Jaunt.AotHost` | NativeAOT consumer host used by the release gate; its `bench` subcommand measures hot-path throughput under NativeAOT vs CoreCLR JIT |
| `bench/Jaunt.Benchmarks` | BenchmarkDotNet suites + `--quick` harness, compared with Jint 4.17.0 |
| `bench/cross` | shared workload corpus, cross-engine protocol (`dotnet/CrossProtocol.cs`), `run.sh` (Jaunt/Jint/MoeJS) and `aot-vs-jit.sh` (NativeAOT vs JIT) |
| `tools/Jaunt.Test262` | test262 runner (pinned revision, feature-manifest skips, per-variant results) |
| `eng/verify-aot.sh` | publishes the AOT host for net8.0/net10.0/net11.0, writes **new** JS after publishing, runs the native binaries |
| `docs/` | feature manifest, benchmark and conformance reports |

## Build, test, verify

```bash
dotnet test tests/Jaunt.Tests                      # unit + contract tests
./eng/verify-aot.sh                                # NativeAOT gate (needs .NET 10 SDK; .NET 11 RC via eng/sdk11)
dotnet run -c Release --project bench/Jaunt.Benchmarks -- --quick
dotnet run -c Release --project bench/Jaunt.Benchmarks -- --filter '*'   # BenchmarkDotNet
bench/cross/aot-vs-jit.sh                          # hot-path throughput: NativeAOT binary vs the same host on CoreCLR
git -C artifacts/test262 checkout 045bf6f9966ce3291b8fbc1e0403cd97b9201b00   # see docs/test262.md
dotnet run -c Release --project tools/Jaunt.Test262 -- artifacts/test262
```

The SDK used for the stable channel is pinned in `global.json`; NativeAOT/runtime packs are pinned to patched
servicing releases in `Directory.Build.targets`.

## Licensing

Jaunt's own license is not decided yet (no `LICENSE` file). Third-party obligations (Acornima, BSD-3-Clause)
are listed in [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md).
