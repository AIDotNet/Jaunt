# 实施状态：01-research 结论的落地与证据

> 状态：2026-11-06，`0.1.0-alpha.1`。本文逐条对应 [01-research.md](01-research.md) 的结论，给出实现位置与可复现证据。
> 数字均来自本仓库脚本在本机（macOS ARM64，开发机非空闲）上的实测；不外推到其他平台或作为对外承诺。

## 1. 结论 → 实现 → 证据

| 01-research 结论 | 实现 | 证据 |
| --- | --- | --- |
| 首期路线：**Acornima parser → Jaunt 语义/字节码 → 托管 VM**，且以 NativeAOT 发布后执行新源码为硬门槛 | `src/Jaunt/Compiler`（`ScopeAnalyzer` 作用域/捕获/TDZ 分析 → `CodeGen` 寄存器字节码 → `BytecodeVerifier`），`src/Jaunt/Runtime/Interpreter.cs` 寄存器 VM；Acornima AST 只在编译期存在，公共 API 不暴露 | `eng/verify-aot.sh`：对 net8.0 / net10.0 / net11.0 以 `PublishAot` 发布真实宿主，**发布之后**才写出 JS 文件，直接运行原生可执行文件；三条通道全部 PASS（`.NET 8.0.33`、`10.0.14`、`11.0.0-rc.3`，`IsDynamicCodeSupported = False`），无 AOT/trim 告警 |
| 运行时发 IL（Jurassic/Yantra 路线）只作对照，不作主路线 | 全库不使用 `Reflection.Emit`、`DynamicMethod`、`Expression.Compile`、`DynamicInvoke`；`eval`/`Function` 编译为 Jaunt 字节码 | `IsAotCompatible`+trim/AOT analyzers 打开且相关告警视为错误（`Directory.Build.props`）；AOT 宿主中 `eval`/`new Function` 检查 PASS |
| 以宿主插件工作负载为中心（MoeJS 的可借鉴点） | `JsPlugin<TIn,TOut>`（FreshRealm、有界并发、DTO 输入输出）、`JsExecution` 根执行作用域（输入/调用/输出共享一个预算）、JSON 与 DTO 两条通道 | `ApiTests.PluginUsesFreshRealmPerInvocation`、AOT 检查 “JsPlugin FreshRealm isolation”；`plugin-e2e` 基准见 §3 |
| 不能把清 scratch 当作“新世界”（MoeJS ReleaseCallData 教训） | 无 `Reset()`；插件每次调用新建 Realm（全局、可变内建、模块实例、闭包均为新），只共享不可变编译结果与跨 Realm 不含引用的布局元数据 | `PluginUsesFreshRealmPerInvocation` 断言模块状态与 `globalThis` 修改不泄漏 |
| `Interrupt` 与执行同步、迟到取消不得误伤下一次调用 | `Interrupt()` 仅在执行中生效，空闲时不留 sticky 标志；取消/超时绑定到具体 root | `ApiTests.InterruptStopsOnlyTheActiveExecution`、`CancellationTokenStopsExecution` |
| 跨 runtime foreign 检查要覆盖所有对象图 | 所有进入引擎的 `JsValue` 对象（参数、`SetValue`、宿主函数返回值、`ToObject` 输入）检查 Realm 归属 | `ApiTests.ValuesFromAnotherEngineAreRejected` |
| 宿主异常/中断后机械栈可恢复 ≠ 应用状态一致；内部错误应淘汰 | 普通 JS throw 不 fault；取消、超限、`UnjoinedAsyncWork`、内部错误一律 Faulted，禁止继续使用 | `JsExceptionDoesNotFaultEngineAndStateIsNotRolledBack`、`InfiniteLoopIsStoppedByInstructionBudget`、`UnjoinedHostWorkFaultsTheEngine` |
| ToGo / JSON stringify / typed decode 是不同契约；getter/proxy/toJSON 可能执行 JS | `ToJsonUtf8`/`ToJsonString` 遵循 `JSON.stringify`（toJSON、getter 在同一预算内）；`ToObject<T>` 为 data-only（拒绝 accessor、Proxy、函数、Promise，不调用 toJSON） | `JsonChannelFollowsStringifySemantics`、`DtoMappingIsDataOnly` |
| 内存计费不是 RSS/堆硬限 | `MaxTrackedBytes` 为 Realm 生命周期保守计费（对象、属性、数组/字符串存储），文档化为非 RSS 限额 | `TrackedBytesBudgetStopsAllocationBombs`；`docs/FEATURES.md` |
| 输入所有权：`ParseJson(span)` 复制输入 | `ParseJson(ReadOnlySpan<byte>)` 先取得自有副本，调用返回后宿主可复用缓冲 | `JsExecution.ParseJson` |
| 跨线程 settlement 只能投递 | 异步宿主函数在首次让出前快照参数；完成只入队，由执行者泵入；同步入口在调用异步宿主函数前拒绝 | `SyncEntryRejectsAsyncHostFunctionBeforeInvokingIt`、`AsyncFunctionsAndHostAsyncBindings` |
| 网络/文件能力不随“安装扩展”默认开放 | 裸引擎无 console、timer、网络、文件、CLR 访问；模块解析默认拒绝 | `ModuleLoadFailures`（无 resolver 时 import 失败） |
| test262 数字不能脱离范围；必须发布 feature/skip 清单 | `tools/Jaunt.Test262` 固定 revision，跳过依据 `docs/FEATURES.md` 的不支持清单，逐变体输出 | [`docs/test262.md`](../docs/test262.md)：48,749 files 共 87,836 个变体，执行 82,028 个，**100 %** 通过（0 失败）；5,808 个按原因列出的跳过（其中 4,613 为 Temporal）不计入通过 |
| Jint 是成熟托管对照，比较要锁定**稳定发布包**并使用其优化用法 | 基准对 Jint **4.17.0**（NuGet 稳定版）使用 `PrepareScript`/`PrepareModule`、缓存函数句柄、预热后计时 | `bench/Jaunt.Benchmarks`，结果见 §3 与 [`docs/benchmarks.md`](../docs/benchmarks.md) |
| Jint 5 源生成会对部分签名回退反射：Jaunt 必须提供完整 AOT 路径并明确诊断 | `src/Jaunt.Generators`：`[JsSerializable]` 生成只做静态成员访问的 `JsTypeInfo<T>`；不支持的成员类型是**编译错误** `JAUNT003`，不存在反射/JSON 回退；手写 `JsTypeInfo.Object<T>()` 是同等的 AOT 路径；基础类型宿主函数使用按闭合泛型解析的静态适配器 | `GeneratorTests`（往返、递归类型、改名/忽略、`JAUNT001–004`）；AOT 检查 “source-generated DTO bindings” PASS |
| Jint 的线程契约：单活动 operation、冲突 fail-fast | 同上：`JsEngine` 并发进入立即 `InvalidOperationException`，宿主回调重入同一引擎被拒 | `ConcurrentEntryFailsFast` |
| Okojo：不可变 code + 私有 linked constants/IC；同名类型不代表同一生命周期 | `FunctionTemplate`/`ScriptTemplate`（`JsScript`/`JsModule`）只含字节码、原始常量、源位置；IC/模板对象/闭包状态存于每 Realm 的 `Feedback`；`JsModuleInstance` 属于具体引擎 | `CompiledScriptIsSharedAcrossEnginesWithPrivateState`（16 线程并发共享同一 `JsScript`） |
| Okojo 默认文件 loader 会读文件：AOT/Realm 设计不能代替能力审计 | 没有默认文件/网络 loader；`JsDictionaryModuleResolver` 只解析宿主显式加入的源码 | `JsEngine.Modules.cs` |
| ClearScript：Runtime/Engine 分层，便捷与快速绑定都要比较 | `JsCompiler`/`JsScript`（无状态、可共享）与 `JsEngine`（有状态）分离；便捷入口与快速入口（固定 arity `Invoke`、raw `JsNativeFunction`）走同一语义 | `ScalarCalls`、`host-callback` 基准 |
| QuickJS：栈式字节码、显式限额；V8：寄存器解释器 + 反馈/IC | 寄存器字节码、显式 VM 帧栈（JS 调用不消耗 CLR 栈）、形状 + 单态 IC（含原型、加属性转移、全局、字符串接收者）、所有快路径都有正确慢路径与失效 guard | `DeepButLegalRecursionWorks`（5000 层 JS 递归）、`DeepJsRecursionIsBoundedByStackLimit`；基准 §3 |
| Acornima 必须验证：中断、深度/节点预算、畸形源码与栈安全 | 解析期间每个 token 检查原生栈余量与取消/截止时间；节点预算；源码长度预算；分析与生成阶段深度检查 | `ParserNestingAttackIsRejectedSafely`（20 万层括号、50 万个一元运算符被安全拒绝而非崩溃）、`CompileBudgets` |
| Acornima 语法集合 ≠ Jaunt 已实现语义 | 语法与语义一一落地：`with`、直接 `eval`（含 Annex B 声明实例化）、async generator、`for await`、BigInt、`using`/`await using` 均已实现；仍不支持的提案语法（decorators、`import defer` 等）在编译期报 `Unsupported*` 诊断 | `RejectsAtCompileTime`、`FeatureTests` |
| JS RegExp → .NET Regex 不是自动兼容 | 双后端：Acornima 能精确适配的模式走 .NET `Regex`；属性转义、`v` 集合运算、modifiers、重复/非 ASCII 组名、量词内捕获组、`u`+`i` 大小写折叠走 Jaunt 原生回溯引擎（`Runtime/Regex`，显式回溯栈，步数计入执行预算），Unicode 数据由 `tools/unicode/generate.py` 从 UCD 17.0 生成 | test262 RegExp 3,854 个变体余 4 个失败；441 个属性转义生成用例全部通过；`FeatureTests.RegexBudgetStopsCatastrophicBacktracking` |
| 许可证：Acornima 为 BSD-3-Clause 并有 NOTICE；Jaunt 自身许可未定，不擅自创建 LICENSE，不复制竞品实现 | `THIRD_PARTY_NOTICES.md` 记录义务；仓库无 `LICENSE` | 仓库根目录 |
| 不能把设计目标写成成绩 | 所有性能结论来自本仓库可复现脚本，并注明环境与噪声 | §3、`docs/benchmarks.md` |

## 2. 与设计文档的对应（摘要）

* **值表示**（02 §4.2）：`JsValue` 16 字节（独立托管引用字段 + 64 位载荷；数字以 XOR 编码，`default` 即 `undefined`，tag 位于被规范化掉的 NaN 载荷区间；从不把引用藏进数值）。
* **对象模型**（02 §4.3）：shape + slot，超 64 个属性/删除/改属性特性/改原型时转字典；以 `Object/Function/Array.prototype` 角色为根的 shape 与 Realm 无关，进程内共享（无锁读、写时复制发布、全局数量上限），其余 shape 为 Realm 私有。
* **内建**：按组惰性初始化（首次内部引用或首次查找对应全局名时）；第一个 Realm 记录内建对象布局，之后的 Realm 直接按共享的不可变 key/flag 数组填槽；内建方法在首次读取时才物化为函数对象。
* **Profile**（02 §5）：Hardened 冻结全部内建，并在 `eval`、`Function`、`GeneratorFunction`、`AsyncFunction` 构造器层面禁止字符串生成代码（覆盖 `.constructor` 别名）。
* **预算**（02 §7）：`Timeout`、`MaxInstructions`（工作单元：回边、调用、内建逐元素步）、`MaxStackDepth`、`MaxTrackedBytes`、`MaxPendingJobs`、`MaxOutputBytes`、`MaxModules`、`MaxStringLength`；JS 无法捕获终止；终止后引擎 Faulted。
* **异步**（02 §8）：请求式执行，自动 pump，`UnjoinedAsyncWork`，迟到 completion 隔离，never-settled 受超时约束。
* **模块**（02 §8.3）：canonical id、live binding、循环、命名空间、`export * as`、动态 `import()`、TLA（异步加载顺序化简，见清单）。
* **框架**（05）：`net8.0;net10.0`（.NET 11 SDK 下加 `net11.0`）；稳定通道 SDK 固定在 `global.json`，.NET 11 RC 走 `eng/sdk11`；NativeAOT/运行时包固定到已修复 CVE-2026-111497 的服务版本（`Directory.Build.targets`）。

## 3. 性能（本机实测，非承诺）

BenchmarkDotNet（Apple M5，.NET 10.0.12，ShortRun；对 Jint 4.17.0，耗时比 < 1 表示 Jaunt 更快），完整表格与环境见 [`docs/benchmarks.md`](../docs/benchmarks.md)：

| 工作负载 | Jaunt / Jint 耗时比 |
| --- | ---: |
| 数值循环 | 0.35 |
| 递归调用 fib | 0.37 |
| class + 私有字段 + 继承 | 0.38 |
| 对象分配与属性访问 | 0.44 |
| 固定 arity 标量 host→JS 调用（39.9 ns vs 71.3 ns） | 0.56 |
| 数组高阶函数 | 0.72 |
| 字符串处理 | 0.76 |
| JSON stringify + parse | 0.80 |
| 插件端到端请求（新引擎 + 模块实例化 + JSON 入 + 调用 + JSON 出） | 0.79（4.5 µs vs 5.7 µs） |
| 新建裸引擎 | 1.9（1.28 µs vs 0.67 µs，仍慢于 Jint） |

早期实现中新建引擎为 224 µs、插件端到端 840 µs；经过惰性内建、共享 shape、预测布局填充、去除每 Realm 字典与身份哈希等改动后降至上表数值。

功能补全（BigInt、direct eval/with、异步生成器、原生 RegExp 引擎等）及其后的性能轮之后，`--quick` 同进程交替测量（Jaunt 相对 Jint 的**加速比**，> 1 表示 Jaunt 更快）：数值循环 2.73×、fib 2.68×、对象 2.42×、class 3.85×、数组 1.43×、字符串 1.51×、JSON 1.31×、标量调用 2.18×、JS→宿主回调 5.32×、插件端到端 2.18×；编译 18 KB 源码 0.96×（分配由 1.93 MB 降至 0.92 MB）、新建裸引擎 0.92×（分配 8.8 KB，少于 Jint 的 9.5 KB）仍略慢于 Jint。改动与剖析结论见 `docs/benchmarks.md` 末节。

## 4. 尚未完成 / 已知偏差

见 [`docs/FEATURES.md`](../docs/FEATURES.md)。产品范围（00-product）首版不承诺的 ECMA-402/Intl 与 Temporal 现已实现：Intl 全部服务基于平台 ICU 运行时绑定（macOS libicucore / Windows icu.dll / Linux libicuuc.so.N，NativeAOT 可用；无 ICU 时不安装 `Intl`），语言标签按 UTS 35 + CLDR 48 规范化；Temporal 支持 ISO 8601 及 15 种 ECMA-402 历法、UTC/偏移/IANA 时区（ICU 时区数据）。test262 在固定 revision 下测试的全部 stage-3 提案也已实现（decorators、ShadowRealm、import defer、source phase imports、text/bytes 模块、Promise.allKeyed、Iterator.prototype.join、immutable ArrayBuffer、RegExp 遗留静态属性、Error.prototype.stack 访问器、non-extensible-applies-to-private），以及 IsHTMLDDA 与 sloppy 函数的 `caller`；test262 不再跳过任何特性。已实现原先列为缺口的：proper tail calls（严格模式尾调用复用帧，常数栈深）、规范的 TLA 模块异步求值（cycle root / async parent / 错误传播顺序）、JSON.parse source text access、`Iterator.zip`/`zipKeyed`/`chunks`/`windows`/`includes`、`Atomics.waitAsync` + `notify`。已实现多 Realm（`JsCallContext.CreateRealm`，函数按 GetFunctionRealm 规则跨 Realm 工作，用于 `$262.createRealm`）与多 agent `Atomics`（`JsSharedMemory` 跨引擎共享 SharedArrayBuffer 内存，`JsEngineOptions.CanBlock` 允许 `Atomics.wait` 阻塞，`Atomics.notify` 唤醒所有 agent 的等待者，用于 `$262.agent`）。

验证矩阵：
- macOS arm64 / CoreCLR .NET 10：全量单测 489 个（.NET 10 与 .NET 8）、test262 全量（含 `intl402/`，无跳过）100,163/100,169 通过；6 个失败变体均为 ICU/CLDR 数据差异（见 docs/test262.md）。
- macOS arm64 / CoreCLR **.NET 8.0.33**（自包含发布，使用 NuGet 缓存中的运行时包，未安装任何东西）：单测 350/350 通过；test262 以 4 线程运行 82,028/82,028 全部通过（10 线程且机器有负载时，抛错密集的 `decodeURI/S15.1.3.1_A1.11_T1` 可能超过 10 s 单测上限）。
- NativeAOT net8/net10/net11（osx-arm64）门禁通过。
- linux-x64：已在 Docker（Ubuntu/Debian x64，Rosetta 仿真，ICU 74）中实际运行：net8/net10 单测、net8/net10 test262（按 ICU 版本的已知差异清单判定）与 NativeAOT 门禁（net8/net10/net11 原生二进制，含 Intl/Temporal 历法/Realm 检查）。win-x64：GitHub Actions（`AIDotNet/Jaunt`，`.github/workflows/verify.yml`，windows-2022）上通过 net8/net10 单测、net8/net10 test262（系统 ICU 77，按 ICU 版本的已知差异清单判定）与 NativeAOT 门禁（net8/net10/net11）；同一工作流也在 ubuntu-24.04（ICU 74）与 macos-15（ICU 76）上通过。win-arm64、linux-arm64：自包含 + 裁剪发布成功，0 个警告，未实际运行（不在首轮承诺的发布矩阵内）。
- 按 05-dotnet-support 首轮要求（Linux x64、Windows x64、macOS ARM64）准备了 `.github/workflows/verify.yml`：每个 RID 跑 net8/net10 单测、固定 revision 的 test262（net10 与 net8 两个运行时）以及 NativeAOT 门禁（net8/net10/net11，发布后再写入新 JS 并直接运行原生可执行文件）。`eng/verify-aot.sh` 已修正：识别 Windows 的 `.exe`，没有任何原生二进制运行时判定失败，并可用 `REQUIRE_TFMS` 要求全部框架都运行。Linux/Windows 在该工作流产出绿色证据之前不列入已验证清单（06-roadmap："缺失证据阻断对应支持声明"）。
- .NET 8 上抛错密集的脚本较慢（CLR 异常在 .NET 8 比 .NET 9+ 慢数倍；内置函数的 JS 异常经 CLR 异常传递），上述 decodeURI 测试因此在高并发下接近 10 s 上限。
