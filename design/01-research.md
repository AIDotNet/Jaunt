# 相关产品调研与取舍依据

> 调研日期：2026-11-03。阅读官方仓库、文档、部分关键源码与包发布页面。**没有运行这些引擎的基准或兼容性测试。** 分支页面会变化，下列提交用于标识本轮快照。

## 1. 结论先行

- MoeJS 的可借鉴之处首先是**以宿主插件工作负载为中心**，而不是某一种 Go 数据布局。
- Jint 是成熟托管对照，不能继续用“只支持 ES5、没有 async”的旧印象评价。
- Okojo 已采用非常接近拟议 Jaunt 的寄存器 VM、共享代码/私有 Realm、AOT 与源生成路线，应加入核心对照。
- ClearScript/V8 是集成原生引擎的产品，不是纯 C# 解释器；其便捷 API 与 FastProxy 都值得比较。
- QuickJS 与 V8 展示两种成本取舍，不能根据启动宣传数字决定通用性能排名。
- 运行时发 IL 的 Jurassic/Yantra 路线仅作为机制/成本对照；用户已明确 NativeAOT 必须支持，不能把“纯托管”误当 AOT 保证，也不能让运行时发码成为 Jaunt 主路线依赖。
- 首期候选应是 **Acornima parser → Jaunt 语义/字节码 → 托管 VM**，以 NativeAOT 发布后执行新源码作为选型硬门槛；优化优先落在生成绑定、数据布局与解释器专门化，而非增加 IL 后端。

## 2. 对照矩阵

| 产品 | 已核实的执行路线 | 最值得比较的能力 | 对 Jaunt 的提醒 |
| --- | --- | --- | --- |
| MoeJS | Go 寄存器字节码解释器 | 低插件调用成本、共享编译、数据转换、限额、可选宿主环境 | Go 布局/PGO/GC 优化不能直接搬到 CLR |
| Jint | Acornima AST 的准备与树式解释，无 JS 字节码 | 简洁 embedding API、prepared 复用、现代 JS、interop、约束、AOT | 不是“功能很弱但方便”的基线；必须比较其优化使用方式 |
| Okojo | 托管寄存器 VM，当前 .NET 10+ | 不可变 code、私有 linked constants/IC、AOT、生成绑定 | 拟议核心机制不是 Jaunt 独有；当前仍为 prerelease |
| ClearScript/V8 | .NET 宿主层 + 原生 V8 | Runtime/Engine 分层、广泛 CLR interop、FastProxy、模块/Promise | 原生部署、桥接成本、原生堆与托管堆分别测量 |
| QuickJS | C 栈式字节码解释器 | 小型嵌入、显式 C API、对象 shape、限额 | 原生 allocator/引用计数不等于 CLR 可提供的保证 |
| V8 | Ignition 与分层 JIT | 类型反馈、IC、分层编译、长热代码优化 | 可借鉴反馈/缓存机制；运行时发码不是 Jaunt 的 NativeAOT 主路线 |
| Jurassic | JS → DynamicMethod/CIL → CLR | 预编译与类型化调用 | 动态 IL 与 NativeAOT 冲突，语言与资源契约需核验 |
| YantraJS | 表达式表示 → IL/动态程序集 | ESM/async、编译缓存、生成初始化 | “AOT ready”不能等同运行时任意 JS 在 NativeAOT 可用 |
| goja / Sobek | Go 托管引擎，宿主 API 导向 | Program 共享、值导出、Sobek 模块能力 | live 宿主包装与快照转换语义不同；取消不是任意宿主抢占 |

本表描述机制和适用点，不是性能排行榜。

## 3. MoeJS：产品范式与实现证据

### 3.1 所读版本及范围

MoeJS `main` 快照：`df42231a9559cfdd6d2594317a8675a48a93c2b8`，处于 alpha。

[README](https://github.com/MoeForge/moejs) 与 [guide](https://github.com/MoeForge/moejs/blob/main/docs/guide.md) 描述：

- 纯 Go、面向嵌入式 JS 插件，现代语法、ESM、Promise/async、Proxy、BigInt、typed arrays 等。
- 独立的 `eventloop`、`webapi`、`node` 扩展；裸 Runtime 无 timer、网络或文件访问。
- `CompileTS` 擦除可擦除类型语法，不是完整 TypeScript 编译与检查。
- 共享冻结内建为默认取舍；`MutableIntrinsics` 提供另一种语义/成本。

[TODO](https://github.com/MoeForge/moejs/blob/main/TODO.md) 仍记录未支持功能、已知不正确行为和实现上限。不能只根据 README 的大功能列表判断完整性。

### 3.2 实际架构与 API

核心使用流程：

```text
Compile / CompileScript
  → Link / Importer（需要模块图时）
  → NewRuntime / SetGlobal / Load
  → Module.Hook / Call
  → Unmarshal / AppendJSON / ToGo
  → ReleaseCallData / Interrupt 等生命周期管理
```

关键源码交叉核验：

- [字节码说明](https://github.com/MoeForge/moejs/blob/main/bytecode/doc.go)：寄存器 VM，32-bit 主编码字，部分指令带扩展字；不是所有完整指令都固定四字节。
- [Value](https://github.com/MoeForge/moejs/blob/main/engine/value_unsafe.go)：16B 的 GC 可见指针字段加数值/tag 字段；**不是把 GC 指针藏进 double**。零初始化代表 `+0`，不是 undefined。
- [metadata](https://github.com/MoeForge/moejs/blob/main/engine/interp.go)：当前共享 `funcMeta` 包括物化常量/属性键，使用弱生命周期管理；动态字符串编译另有私有路径。
- [Realm](https://github.com/MoeForge/moejs/blob/main/engine/realm.go)：IC、栈、执行环境等私有。
- [shape](https://github.com/MoeForge/moejs/blob/main/engine/shape.go)：slot/shape、原型 epoch guard；共享 shape 发布有锁，热读使用快照，不能称为“整个引擎无锁”。

部分文档仍将物化常量说成全都 per-realm，与所读代码不一致。Jaunt 设计应以明确不变量为准，不机械复制上游注释。

### 3.3 最重要的生命周期经验

[ReleaseCallData](https://github.com/MoeForge/moejs/blob/main/engine/realm_calldata.go) 清调用 scratch、引用与部分计费，**不清 globals、模块单例或闭包状态**。因此 README 的 runtime 池不是“每次等价新世界”。

- `Hook` 跟踪导出 slot/live binding，不等于永远缓存第一次取得的函数对象。
- `Interrupt` 需要与池归还同步；迟到取消可能误伤下一次调用。
- 跨 runtime 的 foreign 检查并非覆盖所有对象图，宿主不能依赖部分检查任意传值。
- 宿主 panic/中断后能恢复机械栈状态，不等于应用状态一致；内部错误应淘汰。

### 3.4 数据与安全边界

- `FromGo` 对某些容器按层懒物化；普通 struct 等可能先经过 JSON 快照。不能声称所有输入都零复制。
- `ParseJSON([]byte)` 复制输入；`ParseJSONString(string)` 对适用输入避免该复制。小片段可能保留整个大输入。
- `ToGo`、JSON stringify 等价输出、JSON 等价 typed decode 是不同契约；getter/proxy/toJSON 可能执行 JS。
- [内存计费](https://github.com/MoeForge/moejs/blob/main/engine/memlimit.go) 使用近期可达存量与其后分配，不是独立堆/RSS 硬限；宿主输出与外部缓存仍需单独处理。
- 复杂输出路径的 DecodeOptions 检查可能在中间导出之后发生，不能据此承诺所有中间分配都被预防。
- eventloop 的跨线程 settlement 是投递机制；裸 runtime resolver 不能任意跨线程调用。
- 可选 fetch 有 client 但未提供 allow hook 时，可访问 client 能到达的地址。Jaunt 不应把“安装了网络扩展”直接等同网络隔离。

依据：[Runtime API](https://github.com/MoeForge/moejs/blob/main/runtime.go)、[输出转换](https://github.com/MoeForge/moejs/blob/main/unmarshal.go)、[eventloop](https://github.com/MoeForge/moejs/blob/main/eventloop/eventloop.go)、[SECURITY](https://github.com/MoeForge/moejs/blob/main/SECURITY.md)。

### 3.5 性能数字的正确阅读方式

以下仅为 [performance.md](https://github.com/MoeForge/moejs/blob/main/docs/performance.md) 的**上游自报**：

| 项目 | MoeJS | Sobek |
| --- | ---: | ---: |
| 单次 hook，含选定的宿主参数/结果转换 | 6.86 µs | 14.32 µs |
| 新 runtime | 1.39 µs | 2.20 µs |
| 最大插件加载后的每实例留存口径 | 80.5 KiB | 263.6 KiB |

这张默认表对应 2026-10-01、i5-13500H/Linux/Go 1.26.6、五次中位数；工作负载为 new-api 的 10 个插件、269 次录制调用，其中包含 47 次抛错。上游说明机器非空闲，约 10% 内差异视为噪声。

必须同时保留的限定：

- 新 runtime 不是新进程冷启动；共享内建成本不能消失在分母外。
- `MutableIntrinsics` 的创建/内存成本明显不同，不能与冻结配置混比。
- README 的 2026-10-21 Xeon 场景图、2026-10-25 Web/Node 图是**其他实验**，不能混用条件。
- 部分对照 memory 列使用 live heap、引擎 heap 或 RSS 增量，口径不同。
- 宿主 live wrapper 与数据快照路径可能带来额外 deep-copy，比较的是产品使用流程，不是纯 VM 指令效率。
- 本轮未复现；无法将这些数字转换成 Jaunt 的延迟、内存或倍率目标。

### 3.6 test262 数量不能脱离范围

README 前文为 79,385，后文为 80,971，存在不同步。[RESULTS](https://github.com/MoeForge/moejs/blob/main/bench/test262/RESULTS.md) 在本轮快照记录：

- 固定 test262 revision `045bf6f9966ce3291b8fbc1e0403cd97b9201b00`。
- 总量 95,239；pass 80,971；fail 0；skip 14,268。

这说明“运行范围内通过”，不等于完整 ECMAScript 或没有已知错误。Jaunt 必须发布自己的 feature/skip 清单，而非只发布通过率。

## 4. .NET 竞品的当前状态

### 4.1 Jint：稳定版和未发布主线分开

- [NuGet](https://www.nuget.org/packages/Jint) 当前为 **4.17.0**；其包内资料已经包含 ESM、Promise/async、`EvaluateAsync` / `ExecuteAsync` / `InvokeAsync` 和 prepared 复用。
- 所读 `main` 为 `fecd85d1`，明确是**尚未发布的 Jint 5**；`4.x` 快照 `dfccb4d3`。部分 main README 仍写 4.16 维护线，不能代替包发布事实。
- [执行模型](https://github.com/sebastienros/jint/blob/main/docs/architecture/execution-model.md) 为 AST preparation + tree-walking，不生成 JS 字节码；prepared 可共享，引擎状态不共享。
- [线程契约](https://github.com/sebastienros/jint/blob/main/docs/guide/thread-safety.md) 明确单活动 operation，冲突 fail-fast，而不是内部排队锁；主线允许特定 owner 回调重入。
- [异步](https://github.com/sebastienros/jint/blob/main/docs/guide/async.md) 与 [模块](https://github.com/sebastienros/jint/blob/main/docs/guide/modules.md) 包含宿主 pump、同步/异步加载等；TaskInterop 的具体状态要按版本核对。
- [不可信代码配置](https://github.com/sebastienros/jint/blob/main/docs/guide/untrusted-code.md) 与 [约束](https://github.com/sebastienros/jint/blob/main/docs/guide/constraints.md) 分离执行、等待、结果转换等风险；分配量限制不等于 retained heap。
- [AOT 文档](https://github.com/sebastienros/jint/blob/main/docs/reference/native-aot.md) 有核心部署保证及反射/泛型边界。不能把 Browser 等附加库的能力与核心混为一谈。
- Jint 5 的[源生成绑定](https://github.com/sebastienros/jint/blob/main/docs/guide/migrating-to-v5.md#51-source-generated-clr-interop) 已存在，但部分方法形状会回退反射。Jaunt 必须为已支持的签名提供完整 AOT 路径与明确诊断，不能静默回退到不兼容 AOT 的绑定；性能收益仍需验证，也不应宣称首创源生成。

主线项目列 net472、netstandard2.0/2.1、net8.0、net10.0；4.x 中另有 net462。许可 BSD-2-Clause。上述主线增强不自动属于 4.17.0；实际基准必须锁定发布包，预发布/主线另外列出。

### 4.2 Okojo：直接架构对照

[官方仓库](https://github.com/akeit0/okojo)，快照 `478ec2d3`，提交标注 `0.2.0-preview.2`，核心面向 net10.0，MIT。

- [JsFunctionCode](https://github.com/akeit0/okojo/blob/main/src/Okojo.JavaScript/Bytecode/JsFunctionCode.cs) 明确为 realm-independent、不可变字节码；JS object、shape、feedback、debug patch 不进入共享代码。
- [JsScript](https://github.com/akeit0/okojo/blob/main/src/Okojo.JavaScript/Bytecode/JsScript.cs) 反而是 Realm 私有执行实例，拥有 linked constants、IC 与调试视图。**不同库的同名类型不代表同一生命周期。**
- [VM loop](https://github.com/akeit0/okojo/blob/main/src/Okojo.JavaScript/Execution/JsRealm.VmLoop.cs) 可见 register/opcode dispatch 与 checkpoint。
- 已有 Runtime/Agent/Realm、async/pump、强类型源生成绑定、`.d.ts` 生成、独立 Reflection 包。
- 核心标注 AOT-compatible；[Reflection 包](https://github.com/akeit0/okojo/blob/main/src/Okojo.Reflection/Okojo.Reflection.csproj) 明确不作同等承诺。本轮未做发布运行验证。
- 默认 [FileModuleSourceLoader](https://github.com/akeit0/okojo/blob/main/src/Okojo.JavaScript.Embedding/FileModuleSourceLoader.cs) 会读取文件；AOT/Realm 设计不能代替宿主能力审计。
- 上游 test262 “100%”附有排除范围；线程并发与硬堆限额的完整公共保证本轮未核实，不自行补全。

Jaunt 的 .NET 8 基线、输入输出契约、绑定限制与实测端到端成本可以形成产品取舍，但仍需证据证明价值。

### 4.3 ClearScript/V8：不只看普通反射调用

[官方仓库](https://github.com/ClearFoundry/ClearScript)，快照 `df0ce742`；[包](https://www.nuget.org/packages/Microsoft.ClearScript.V8) 为 **7.5.1.1**。ClearScript 本体 MIT，V8/其他依赖许可另计。

- `V8Runtime` / `V8ScriptEngine` / `V8Script` 分离。一个 Runtime 下的 Engine 可共享某些对象；这种共享不等于安全隔离。
- [Engine 源码](https://github.com/ClearFoundry/ClearScript/blob/master/ClearScript/V8/V8ScriptEngine.cs) 说明可由不同线程调用，但内部锁串行执行；与 Jint 的 fail-fast 策略不同。
- 广泛的 CLR interop 之外，已有 [FastProxy](https://github.com/ClearFoundry/ClearScript/blob/master/ClearScript/V8/FastProxy/V8FastHostObject.cs)。基准需包含便捷路径和快速绑定路径。
- ESM/CommonJS、TLA、Task/Promise 转换不意味着完整 Node 环境；某些转换有损，选项必须披露。
- 内存 constraints 在严重场景可能终止进程；ArrayBuffer、宿主成本并不都包含在同一堆监测中。
- 本轮没有找到足以支持“整个 ClearScript 已保证 NativeAOT/裁剪兼容”的官方契约，列为待验证，不能直接断言一定可用或一定不可用。

此路线不符合本文推荐的“纯托管内核”，但仍是必须比较的实际用户选择。

### 4.4 Jurassic / YantraJS：IL 路线参照

Jurassic [包](https://www.nuget.org/packages/Jurassic/) 为 **3.2.9**，快照 `17f72827`，netstandard2.0，MIT。

- [MethodGenerator](https://github.com/paulbartrum/jurassic/blob/master/Jurassic/Compiler/MethodGenerator/MethodGenerator.cs#L140-L202) 使用 `DynamicMethod`/IL；`CompiledScript`、`CompiledEval` 可先编译再绑定 engine 执行。
- 有 Promise/队列与宿主对象 API，但本轮未核实完整现代 ESM/async/await 支持；README 的历史 ES6 表不是当前完整一致性报告。
- [安全说明](https://github.com/paulbartrum/jurassic/wiki/Safely-executing-user-provided-scripts) 明确旧 `Thread.Abort` 示例不适用于 .NET Core，不能作为 Jaunt 的取消方案。
- 动态 IL 主执行路径不能直接满足 NativeAOT；没有发现独立 AOT 执行后端证据。

[YantraJS](https://github.com/yantrajs/yantra) 快照 `9cf0ba07`，Apache-2.0。Core 含 netstandard2.0/2.1、net8.0。当前 [ExpressionCompiler](https://github.com/yantrajs/yantra/blob/main/YantraJS.ExpressionCompiler/ExpressionCompiler.cs) 有动态程序集/方法路径；README 的 “AOT ready”还涉及生成绑定初始化，不能据此推导任意动态脚本都能在 NativeAOT 中执行。本轮未全面核实其沙箱/并发契约。

## 5. QuickJS / V8 的机制借鉴

### QuickJS

[官方文档](https://bellard.org/quickjs/quickjs.html) 本轮标示版本 2026-06-04：

- `JSRuntime` 是堆/运行单元，`JSContext` 是 Realm；单 Runtime 不支持同时多线程执行。
- 栈式字节码，直接编译、静态计算每函数操作数栈；无需先照搬一个大型 AST 系统。
- 引用计数加循环清理；32-bit 使用 NaN boxing，64-bit `JSValue` 为 128 bit，不应笼统说成所有架构都 NaN-boxing。
- `JS_SetMemoryLimit`、`JS_SetInterruptHandler` 等依赖原生执行/分配模型。
- `JS_DupValue` / `JS_FreeValue` 等原生寿命控制不宜机械搬到普通 C# 使用体验。
- 明确警告字节码与引擎版本相关、不可直接执行不可信字节码；`qjsc` 是嵌入字节码与引擎的产物，不等于完整优化 JS→机器码编译器。

### V8

- [Ignition](https://v8.dev/docs/ignition) 是寄存器解释器。
- [Sparkplug](https://v8.dev/blog/sparkplug)、[Maglev](https://v8.dev/blog/maglev)、TurboFan 体现不同编译成本和优化程度，不要求每个函数走遍全部层。
- [2025 年架构更新](https://v8.dev/blog/leaving-the-sea-of-nodes) 介绍 TurboFan 的 Turboshaft 后端；Turboshaft 不是额外执行 tier。
- [嵌入文档](https://v8.dev/docs/embed) 的 Isolate/Context/handle 分层、移动 GC 与 code cache 很有参考价值，但原生 handles 不应成为 Jaunt 每次调用的繁琐必需品。
- [Isolate 线程契约](https://v8.github.io/api/head/classv8_1_1Isolate.html) 与 [堆约束](https://v8.github.io/api/head/classv8_1_1ResourceConstraints.html) 提醒：一个实例不等于并行 VM，堆超限也不保证总是可安全捕获的脚本异常。
- [JIT-less](https://v8.dev/blog/jitless) 说明解释执行与部署限制可以共存；对 Jaunt 而言，AOT 发布后解析/编译/执行新 JS 是必须验收的能力，不只是理论可行性。

## 6. 初期 parser 候选：Acornima

[官方仓库](https://github.com/adams85/acornima)，快照 `be0e7c38`，2026-10-25。

已核验：[项目文件](https://github.com/adams85/acornima/blob/master/src/Acornima/Acornima.csproj) 含 net8.0 等资产及条件 AOT/trim 标记；[Parser](https://github.com/adams85/acornima/blob/master/src/Acornima/Parser.cs) 提供 Script/Module/Expression 入口；[README](https://github.com/adams85/acornima/blob/master/README.md) 自报现代语法测试与解析基准。

推荐：内部直接遍历其 AST 完成语义分析/降级，不必为了“自有 AST”再复制一套完整树。只暴露 Jaunt 自己的源码、编译配置、diagnostic 与编译结果。

必须验证而不能预设：

- Jaunt 集成后的 AOT publish/run：向实际发布产物提交构建时未知的新源码，并逐项解决 AOT/trim 诊断；不能只验证库声明或预嵌入脚本。
- 解析中断、深度/节点预算、畸形源码与栈安全。
- AST/源码保留、编译分配及异常映射。
- parser 语法版本与 Jaunt 已实现语义的交集。
- JS RegExp → .NET Regex 的兼容缺口：选中 parser 并没有顺便完成 RegExp 引擎。

[LICENSE](https://github.com/adams85/acornima/blob/master/LICENSE) 是 **BSD-3-Clause**，另有 [NOTICE](https://github.com/adams85/acornima/blob/master/NOTICE)，不是 MIT。

## 7. 合规与证据纪律

- MoeJS 主体 Apache-2.0，但 [THIRD_PARTY_NOTICES](https://github.com/MoeForge/moejs/blob/main/THIRD_PARTY_NOTICES) 包含 ICU、V8、Unicode/CLDR 等不同义务；直接移植代码或数据不能只保留一份 Apache LICENSE。
- new-api 插件基准另行下载、为 AGPL-3.0；Jaunt 首选自建/可再分发测试工作负载，使用上游测试输入前单独评估。
- [goja](https://github.com/dop251/goja)、[Sobek](https://github.com/grafana/sobek) 本体 MIT；读取快照分别为 `64826782`、`c1e3e36b`。源码复用仍要保留相应通知。
- Jaunt 自身许可证尚未决定，本轮不擅自创建 LICENSE，也不复制竞品实现。
- [test262 官方说明](https://github.com/tc39/test262) 明确测试覆盖持续演进且不完整；通过数不能替代特性清单与已知偏差。
- 本轮没有证据支持“Jaunt 必然快于 Jint/Okojo/V8”、固定倍数收益或固定每实例内存占用。后续所有性能结论依 [验证方案](04-performance.md) 产生。
