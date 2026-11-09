# Changelog

Jaunt follows [Semantic Versioning](https://semver.org/) from 1.0.0. See "Versioning" in the [README](README.md) for what
counts as public API.

## Unreleased

## 1.0.2 (2026-11-09)

A performance release. No public API changed. Over the 59 workloads of the benchmark corpus under NativeAOT, wall time is about 5%
lower than 1.0.1 (geometric mean, Apple M4, two passes measured separately: −2.5% and −2.7%). Details and the method are in
[docs/benchmarks.md](docs/benchmarks.md).

### Performance

- Script compilation is about 16% faster and allocates about 11% less, measured under NativeAOT on the benchmark corpus.
- `Object.assign` into an empty object, `instanceof`, `new` and class fields with constant names are faster.
- Repeated `eval` of the same source reuses its compiled code, up to a bounded total source length.
- Closures and environments are smaller.
- `regex.test` and capture-free `exec`, `replace` and `split` no longer build match objects, and legacy `RegExp` statics are captured lazily.
- `promise.then` allocates less, and the job queue no longer copies when it grows.
- `arguments.length` and `arguments[i]` read from the call frame when the object cannot be observed. Rest and spread allocate their exact size.
- Class field initializers run in the interpreter loop rather than a nested one. Closures allocate less.
- `typeof x === 'name'` ends at a reference comparison, and bitwise operators keep their fast path for values outside the int32 range.

### Hosting

- The README now recommends `OptimizationPreference=Speed` for NativeAOT hosts: about 2% faster overall and 6% faster to compile
  scripts on the benchmark corpus, for about 0.3 MB of binary size. The sample AOT host uses it.

## 1.0.1 (2026-11-08)

First release on NuGet. Everything listed under 1.0.0 applies; that tag failed its release checks and was never published.

### Fixed

- `Function.prototype.apply`, `Reflect.apply`, `Reflect.construct` and the other users of `CreateListFromArrayLike` allocated the
  whole list for the claimed length before any budget check: 1.4 GB for `{ length: 90000000 }`, not charged to
  `MaxTrackedBytes`, and able to stall the process. A long list now grows as it fills and each growth is charged.

## 1.0.0 (2026-11-08)

Tagged but not published.

First stable release: a fully managed, NativeAOT-compatible JavaScript engine for .NET 8, .NET 10 and .NET 11.

### Language and standard library

- ECMA-262 through ES2025, plus the library additions that reached stage 4 since and the stage 3 proposals tested at
  the pinned test262 revision. Includes modules, top-level `await`, generators, async generators, classes with private
  elements, BigInt, proper tail calls, explicit resource management, `with`, direct `eval` and Annex B.
- Temporal, and ECMA-402 `Intl` on the platform ICU. Without an ICU the `Intl` global is not installed.
- Native RegExp engine with Unicode 17 properties, the `v` flag, modifiers and lookbehind.
- Multiple realms and multi-agent `Atomics`.
- `eval` and `Function` compile to Jaunt bytecode at run time, so they work in NativeAOT binaries. The Hardened profile
  disables them.

The exact surface is in [docs/FEATURES.md](docs/FEATURES.md), including what is not supported and the known deviations.

### Hosting

- Compiled scripts and modules are immutable and can be shared by any number of engines and threads.
- `JsEngine` with typed host functions, raw native functions, async host functions and ES module loading.
- Isolated realms, and `JsPlugin` for a fresh realm per call with bounded concurrency.
- Budgets through `JsLimits`: timeout, instructions, stack depth, tracked bytes and output bytes, plus cancellation.
- Reflection-free DTO mapping, by hand with `JsTypeInfo` or through the source generator bundled in the package.

### Performance

- Register bytecode VM with inline caches, shared hidden-class shapes and lazily built intrinsics.
- This release's last optimization pass: array destructuring of local targets as one instruction, private fields and
  number `**` in the dispatch loop.
- Measured against MoeJS v0.1.0-alpha.11 under NativeAOT, on one Apple M4 and 55 comparable workloads: warm wall time 28%
  lower (geometric mean), faster on 53 and within ±3% on 2. Script compilation is 29% slower than MoeJS, and first-call
  conditions differ between the two engines. Details and method are in [docs/benchmarks.md](docs/benchmarks.md).

### Quality

- test262 at revision `045bf6f`: 100,167 of 100,169 variants pass on macOS with ICU 78 and nothing is skipped. The two
  failures are one `intl402` file affected by Apple's locale data. Results on other platforms follow their ICU version
  (see [docs/test262.md](docs/test262.md)).
- Every release passes unit tests, test262 and a NativeAOT gate that runs newly written JavaScript in published binaries,
  on Linux x64, Windows x64 and macOS ARM64.

### Fixed before release

- `JsEngine.Interrupt()` could be lost when it arrived just as an execution began, leaving the script running until another
  limit stopped it. CI exposed this as an intermittent test failure.

### Compatibility notes

- The `net11.0` assets are built with the .NET 11 RC SDK (`11.0.100-rc.2`), and .NET 11 was verified on that RC. They will
  be rebuilt and verified again once .NET 11 reaches general availability.
