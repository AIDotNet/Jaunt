# Jaunt feature manifest

Version: `0.1.0-alpha.1`. This manifest is the contract the test262 runner uses for skips
(`tools/Jaunt.Test262/Program.cs`, `Manifest`). A feature is listed as supported only if it is implemented in the
compiler/VM/builtins **and** available identically under CoreCLR and NativeAOT. Syntax the parser accepts but Jaunt
does not implement is rejected at compile time with a `JsCompileException` — never silently compiled as different
semantics.

Scope (see [`design/00-product.md`](../design/00-product.md)): ECMA-262 as standardized through ES2025 plus the
library additions that reached stage 4 since, Temporal, and ECMA-402 (`Intl`, when the platform ICU is present).
Stage < 4 proposals are not part of the core commitment.

## Supported

### Language

| Area | Notes |
| --- | --- |
| Scripts and ES modules | strict and sloppy scripts; modules always strict |
| `var` / `let` / `const`, TDZ | per-iteration `let` bindings in `for`, `for-in`, `for-of` |
| Functions | declarations, expressions, arrows, default/rest parameters, separate parameter/body environments, mapped (sloppy) and unmapped `arguments`, `new.target`, closures |
| Control flow | all statements, labels, `try`/`catch`/`finally`, statement completion values (`eval`) |
| `with`, direct `eval` | full semantics: functions containing direct `eval` or `with` switch to named environments and dynamic name resolution; everything else is compiled with static slots |
| Destructuring, spread | patterns everywhere, evaluation order per spec (assignment targets before `IteratorStep`) |
| Objects and classes | computed keys, accessors, `super`, public/private fields and methods, `#x in o`, static blocks, private names visible to direct `eval`, private elements cannot be added to non-extensible objects |
| Generators, async functions, async generators | `yield*` (sync and async), `for await`, top-level `await` |
| Proper tail calls (ES2015 PTC) | calls in tail position of strict, non-generator, non-async functions (`return`, concise arrow bodies, conditional / `&&` / `\|\|` / `??` / comma operands, tagged templates) replace the caller's frame, so tail recursion runs in constant stack depth |
| Explicit resource management | `using`, `await using` (blocks, functions, modules, `for`/`for-of` heads), `DisposableStack`, `AsyncDisposableStack`, `SuppressedError`, `Symbol.dispose`/`asyncDispose` |
| BigInt | literals, all operators, mixed comparisons, `BigInt64Array`/`BigUint64Array`, `DataView` 64-bit accessors |
| Modules | named/default/namespace imports and exports, `export * as`, live bindings, cycles, `import.meta`, dynamic `import()`, import attributes, JSON and text modules (`with { type: "json" }`, `with { type: "text" }`), `import defer` / `import.defer()` (deferred namespaces evaluated on first access, asynchronous dependencies evaluated eagerly), source-phase imports (`import source` / `import.source()`, `%AbstractModuleSource%`; host modules with a source representation via `JsCompiler.CreateSourceModule`, source text modules throw a SyntaxError), top-level await with the specification's asynchronous cycle-root evaluation (async parents, pending dependencies, error propagation order) |
| Annex B | HTML-like comments, legacy octal, block-level function hoisting (B.3.2–B.3.5), `__proto__`, `escape`/`unescape`, legacy RegExp syntax, `__defineGetter__` family, `Date` legacy methods, String HTML methods |
| `eval` / `Function` | compiled to Jaunt bytecode at run time (works under NativeAOT); disabled in the Hardened profile |

### Builtins

`Object`, `Function`, `Array` (ES2023 change-array methods, `Array.fromAsync`), `String` (`isWellFormed`,
`toWellFormed`, …), `Number`, `Boolean`, `Symbol`, `Math` (`f16round`, `sumPrecise`), `JSON` (incl. source text access: reviver `context.source`, `JSON.rawJSON`, `JSON.isRawJSON`), Error hierarchy
(`cause`, `AggregateError`, `SuppressedError`, `Error.isError`, `Error.prototype.stack` accessor), `Map`/`Set` (set methods, `groupBy`,
`getOrInsert`/`getOrInsertComputed`), `WeakMap`/`WeakSet`, `WeakRef`, `FinalizationRegistry`, `Promise`
(`withResolvers`, `try`, `allKeyed`/`allSettledKeyed`), `RegExp` (flags `dgimsuvy`, named and duplicate named groups, lookbehind, modifiers
`(?ims-ims:…)`, Unicode property escapes and properties of strings (Unicode 17.0), `v`-flag set operations,
`RegExp.escape`, Annex B legacy static properties `RegExp.$1`–`$9`/`input`/`lastMatch`/…), `Date`, `Reflect`, `Proxy`, `ArrayBuffer` (resizable, `transfer`, `transferToFixedLength`,
`detached`, immutable buffers: `transferToImmutable`/`sliceToImmutable`/`immutable`), `SharedArrayBuffer` (growable), `Atomics`, `DataView` (incl. `Float16`), typed arrays incl.
`Float16Array`, `Uint8Array` base64/hex, `Iterator` helpers (`Iterator.from`, `map`/`filter`/`take`/`drop`/
`flatMap`/`reduce`/`toArray`/`forEach`/`some`/`every`/`find`, `Iterator.concat`, `Iterator.zip`/`zipKeyed`, `chunks`/`windows`,
`includes`, `join`), URI functions, `globalThis`.

### Temporal

`Temporal.Instant`, `ZonedDateTime`, `PlainDate`, `PlainTime`, `PlainDateTime`, `PlainYearMonth`, `PlainMonthDay`,
`Duration` (incl. `round`/`total` with `relativeTo`, calendar-aware balancing), `Temporal.Now`, and
`Date.prototype.toTemporalInstant`. Calendars: ISO 8601, plus the ECMA-402 calendars listed below when ICU is available. Time zones: `UTC`, fixed UTC offsets, and IANA names resolved through the OS time zone
database (`TimeZoneInfo`; transitions, `getTimeZoneTransition`, ambiguous/skipped wall times with all
disambiguation modes). `toLocaleString` uses `Intl.DateTimeFormat` / `Intl.DurationFormat` (ISO form without ICU). Exact arithmetic: epoch nanoseconds and
time durations are 128-bit integers, `total()` results are correctly rounded rationals.

### Proposals beyond ES2025 (stage 3 at the pinned test262 revision)

Decorators (2023-05 semantics: class, method, getter/setter, field and `accessor` decorators, `context.addInitializer`,
`Symbol.metadata`), `ShadowRealm` (`evaluate`, `importValue`, wrapped functions), `import defer` / `import.defer()`,
source-phase imports (`import source`, `import.source()`, `%AbstractModuleSource%`), text and bytes modules
(`with { type: "text" }` / `{ type: "bytes" }`), `Promise.allKeyed` / `allSettledKeyed`, `Iterator.prototype.join`,
immutable `ArrayBuffer` (`transferToImmutable`, `sliceToImmutable`), legacy RegExp statics (`RegExp.$1`…, `lastMatch`,
…), the `Error.prototype.stack` accessor and non-extensible-applies-to-private. These follow the proposal texts as tested
by test262 and may change with the proposals. Decorator details beyond test262 were checked against the proposal README
(`addInitializer` timing per element kind, nearest decorator first, decorator expressions evaluated interleaved with
computed keys in source order, decorators called without a receiver, context object shape); `Symbol.metadata` matches the
decorator-metadata proposal README (one metadata object shared by all decorators of a class, whose prototype is the
superclass's metadata, assigned to `Symbol.metadata` once the class is defined); property attributes are not specified
there.

### Legacy and host extensions

Sloppy ordinary functions have the web-compatible own `caller` (the calling sloppy function, never a strict function)
and `arguments` (null) properties; `JsCallContext.CreateHtmlDdaObject()` creates an `[[IsHTMLDDA]]` object (Annex B
`document.all` semantics) for hosts and test262's `$262.IsHTMLDDA`.

### Realms and agents

| Area | Notes |
| --- | --- |
| Multiple realms | `JsCallContext.CreateRealm()` creates a realm (own global object and intrinsics) in the running engine; `EvaluateScript(realmGlobal, source)` runs a script in it. Objects move freely between realms; every function keeps its realm (GetFunctionRealm), so `new.target`-derived prototypes, `ArraySpeciesCreate`, errors thrown by `[[Call]]`/`[[Construct]]` and Proxy internal methods follow the specification's realm rules. `Symbol.for` is shared by all realms of an engine. Used for test262's `$262.createRealm` |
| Multiple agents | each `JsEngine` is one agent. A `SharedArrayBuffer`'s memory can be handed to engines on other threads (`JsCallContext.GetSharedMemory` → `JsSharedMemory` → `CreateSharedArrayBuffer`); `Atomics` operations on it are sequentially consistent across engines and `Atomics.notify` wakes waiters of every agent (FIFO). `JsEngineOptions.CanBlock = true` makes `Atomics.wait` park the thread (still bounded by the execution's timeout / cancellation) and lets a drained job queue wait for pending `Atomics.waitAsync` promises. Used for test262's `$262.agent` |

### ECMA-402 (Intl)

`Intl.Collator`, `NumberFormat` (v3: rounding options, `formatRange`, exact decimal strings), `PluralRules`
(`selectRange`), `DateTimeFormat` (styles, `dayPeriod`, `fractionalSecondDigits`, all `timeZoneName` styles,
`formatRange`/`ToParts`, Temporal objects), `RelativeTimeFormat`, `ListFormat`, `DisplayNames`, `Segmenter`,
`Locale` (incl. Locale Info: `getCalendars`, `getWeekInfo`, …), `DurationFormat`, `Intl.getCanonicalLocales`,
`Intl.supportedValuesOf`, and the locale-sensitive `toLocaleString`/`localeCompare`/`toLocale{Upper,Lower}Case`
methods. Locale data and algorithms come from the platform ICU, bound at run time (no managed dependency, NativeAOT
compatible): `libicucore` on macOS, the newest `libicuuc.so.N` on Linux, and on Windows an app-local ICU (the
`Microsoft.ICU.ICU4C.Runtime` package, `icuucNN.dll`) or else the system `icu.dll`. A library is used only if it exports
every ICU function Jaunt needs — the system `icu.dll` of Windows Server 2022 / Windows 10 does not, so Windows hosts
that want `Intl` should reference `Microsoft.ICU.ICU4C.Runtime`. Language tags are
canonicalized with UTS 35 using CLDR alias tables generated into the assembly (`tools/unicode/cldr_locale.py`, CLDR
48). Without ICU (or with `JAUNT_DISABLE_ICU=1`) the `Intl` global is absent and `toLocale*` fall back to the
invariant culture. Temporal additionally supports the calendars `buddhist`, `chinese`, `coptic`, `dangi`, `ethioaa`,
`ethiopic`, `gregory`, `hebrew`, `indian`, `islamic-civil`, `islamic-tbla`, `islamic-umalqura`, `japanese`, `persian`,
`roc` (eras, leap-month codes, non-ISO arithmetic) and takes named-zone offsets and transitions from ICU's time zone
data (second-precision historical offsets).

### RegExp backends

Patterns are executed by .NET `Regex` (after Acornima's adaptation) when that reproduces ECMAScript semantics
exactly; patterns using property escapes, the `v` flag, modifiers, duplicate or non-ASCII group names,
capturing groups inside quantifiers, or `u`+`i` case folding run on Jaunt's native backtracking engine
(`src/Jaunt/Runtime/Regex`), whose steps are charged to the execution budget.

## Not supported

| Feature | Reason |
| --- | --- |
| `Intl` without a platform ICU | the `Intl` global is not installed (e.g. Linux containers without `libicu`) |
| `$262.gc` (forced garbage collection from script) | host hook not provided; no test262 test outside `staging/` requires it |

## Known deviations (implemented, but not exactly per spec)

| Area | Deviation |
| --- | --- |
| `Atomics.wait` | by default throws `TypeError` (`[[CanBlock]]` is false so an embedded script cannot park the host thread); set `JsEngineOptions.CanBlock` for worker engines. A finite `Atomics.waitAsync` timeout uses a host timer in async executions and otherwise resolves `"timed-out"` when the job queue is next processed |
| `Date` | local time = `JsEngineOptions.TimeZone` (default UTC); without ICU, locale formatting uses the invariant culture |
| Intl output | exact strings follow the platform ICU/CLDR version (ICU 78 / CLDR 48 on the reference machine); three intl402 tests encode data or ICU behaviour that differs (German `MMMMEd` punctuation, a separate pre-Hijra era, Hebrew year-0 month lengths) |
| Default locale | `en-US` (`Realm.DefaultLocale`) |
| error messages / `stack` | V8-like text, not guaranteed identical to any engine |

## Host-facing behaviour (not ECMAScript)

* A bare engine has no `console`, timers, network, file or CLR access; hosts add explicit capabilities.
* `JsProfile.Hardened`: intrinsics frozen; `eval`, `Function`, generator/async (generator) function constructors and
  all their aliases throw `EvalError`.
* Limits (`JsLimits`): timeout, VM work units (also charged by the native RegExp engine), call depth, tracked
  allocation bytes, pending jobs, output bytes, module count, string length. Exceeding a limit terminates the root
  execution (JS cannot catch it) and faults the engine.
* Module resolvers receive import attributes (`IJsModuleResolver.Resolve(specifier, referrer, attributes)`);
  `JsCompiler.CompileJsonModule` / `JsDictionaryModuleResolver.AddJson` create JSON modules,
  `JsCompiler.CreateTextModule` / `JsDictionaryModuleResolver.AddText` text modules, and `JsCompiler.CreateSourceModule`
  host modules that have a Module Source object (`JsModule.ModuleSourceKind`); `JsCallContext.GetAbstractModuleSource`
  returns `%AbstractModuleSource%`.
* `JsCallContext.EvaluateScript` (ScriptEvaluation from a host callback) and `JsCallContext.DetachArrayBuffer`.
