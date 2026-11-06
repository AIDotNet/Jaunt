# .NET 支持、构建与部署策略

> 状态：设计草案，2026-11-03。目标框架及 **NativeAOT 必须支持** 已由用户确认；下述实现与发布安排为设计，尚未建立工程或运行兼容性测试。（2026-11-06 更新：已实施为 `0.1.0-alpha.1`，实测与偏差见 [07-implementation-status.md](07-implementation-status.md)；本文保留为设计记录。）

## 1. 最终支持范围

| 运行时 | Jaunt 定位 | 2026-11-03 核验的上游状态 |
| --- | --- | --- |
| .NET 8 | 最低兼容基线，保留用户要求的支持 | LTS 维护阶段，上游支持于 **2026-11-10** 结束 |
| .NET 10 | 推荐的稳定生产与性能验证基线 | LTS，上游支持至 **2028-11-14** |
| .NET 11 | 明确规划支持，先走 RC 验证通道 | 官方页面列出 **RC3 / Go-live**，尚不能当作 GA 版本 |

来源：[Microsoft 支持策略](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core#cadence)、[官方下载页面](https://dotnet.microsoft.com/en-us/download/dotnet?cid=getdotnetcorecli)。日期与版本状态可能变化，发布时必须复核。

.NET 8 即将结束上游支持**不改变用户的目标要求**。Jaunt 可以保留兼容性测试与自身修复，但不能替 Microsoft 承诺运行时安全补丁；新生产部署优先建议 .NET 10。移除 .NET 8 应是未来明确的版本决策，不能静默移除。

不承诺 .NET Framework、.NET Standard、.NET 9、Unity/Mono、浏览器 WASM、移动平台等额外矩阵；某个平台能够引用或运行库，不等于已经得到官方验证。

**NativeAOT 是强制交付和发布门槛，不是可选部署或“尽力兼容”。** 普通 CoreCLR 部署仍然支持，但不得靠 AOT 下删减已承诺的 JS 功能、核心 API 或绑定能力来通过验证。要求落实到各目标框架的已承诺 RID，不能只在 .NET10 的一台机器上验证后外推。

## 2. 运行时支持不等于必须发布三份不同实现

- `net8.0` 库通常可以被更新版本的 .NET 应用引用，从宿主的新 JIT/GC 中受益。
- `net8.0` 应用是否自动在 .NET 10/11 上启动，是应用的运行时选择与 roll-forward 策略，不能与类库兼容性混为一谈。
- 不发布 `net9.0` 专用资产，不表示能或需要阻止 .NET 9 应用引用 `net8.0` 资产；只是它不在承诺验证范围。
- 新的 TFM 只在确有新版 BCL/API、分析工具或实测收益时带来差异；不是修改 TFM 就自然拥有 JS 特化 JIT。

参考：[目标框架说明](https://learn.microsoft.com/en-us/dotnet/standard/frameworks)、[部署说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/)。

### 推荐发布形态

目标是同一 NuGet 包提供一致 API 的资产：

```xml
<TargetFrameworks>net8.0;net10.0;net11.0</TargetFrameworks>
```

这只是未来工程配置示意，本轮没有创建 `.csproj`。

- P0 可以先用 `net8.0` 公共实现验证核心设计，但必须从 P0 建立 CoreCLR 与 NativeAOT 的实际消费/运行验证，并扩展到三个目标框架；不能先完成 JIT 路径，再把 AOT 作为附加移植。
- 稳定通道先使用稳定 SDK 构建 `net8.0` / `net10.0`；.NET 11 RC 使用单独、固定版本的验证/预发布通道。
- .NET 11 GA 后重新测试，才把对应资产纳入正式发布。Go-live 支持不等于 Jaunt 已完成发布验证。
- 无明确收益时三个 TFM 共用同一算法，不为展示“多目标优化”复制三套 VM。

## 3. 共用能力和版本优化边界

.NET 8 基线已经可以使用：

- `Span<T>`、`ReadOnlySpan<T>`、`Memory<T>`、`ArrayPool<T>`、`IBufferWriter<T>`。
- `ValueTask`、`CancellationToken`、现代泛型与 BCL 集合。
- `System.Text.Json`、`System.Numerics`、向量指令能力检测。
- `SearchValues<T>` 的基线适用形式、`FrozenDictionary` 等适合只读元数据的设施。
- NativeAOT/裁剪分析与源生成绑定路线。

以上是可用工具，不是全部都应使用：小型表可能比冻结字典更快；SIMD 可能增加短输入开销；`ArrayPool` 可能保留大数组和旧对象引用。

版本专用实现集中在内部适配边界，例如 `TextSearch`、`Hashing`、`BufferOperations`。只有在实测和正确性验证通过后才增加条件实现，公共 API 和 JS 语义不得随 TFM 改变。

- 固定稳定的 `LangVersion`，不把 `latest` / `preview` 当公共构建默认值。
- 不要求用户为了基础功能开启 `unsafe`。
- 指令集优化必须有标量回退；不能只验证开发者机器上的 ARM64 或 AVX。
- 不预先假定 .NET 11 存在某个未核验 API，更不把 RC 上的收益写成 GA 承诺。

## 4. NativeAOT 与裁剪：强制架构约束

### 交付边界

AOT 要求覆盖每个阶段已经承诺的解析器、字节码编译器、VM、标准库、模块、Promise/异步、取消/预算、宿主绑定、DTO/JSON，以及 Hosting 和未来正式发布的运行时扩展。尚未实现的 JS 特性仍可按 roadmap 推进，但不能出现同一版本“CoreCLR 完整、AOT 只能运行一个缩水子集”的情况。

构建期生成器、benchmark 控制器和开发工具不是嵌入宿主的运行时组件，不要求它们自身也以 NativeAOT 发布；它们生成或引用到最终应用中的代码和依赖必须满足上述要求。

### 动态 JS 与 NativeAOT 并不冲突

**宿主以 NativeAOT 发布后，仍必须能够解析、编译并执行构建时未知的新 JS。** NativeAOT 限制的是运行时生成宿主机器码/IL 等能力，不是禁止解释动态语言。

```text
NativeAOT 宿主
  ├─ 预先编译的 parser / 字节码编译器 / VM
  ├─ 发布后取得的新 JS → Jaunt 字节码 → VM 执行
  └─ 源生成或手写的静态 DTO / 宿主函数适配器
```

固定脚本的构建期预编译只是一条可选性能路径，不能成为使用 NativeAOT 的前提。Standard 中已经实现的 JS `eval` / `new Function` 也应编译为 Jaunt 字节码；Hardened 禁止它们是安全政策，不是 AOT 强加的语义降级。

### 实现规则

- 核心不得依赖 `Reflection.Emit` / `DynamicMethod`、运行时加载新的 CLR 程序集、必须动态发码的表达式树或 JS→IL 后端。JS 模块加载不等于 CLR 程序集加载。
- 核心调用使用强类型静态适配器，不依赖 `Delegate.DynamicInvoke`。基础类型重载和手写 descriptor 也必须可用，不强迫所有调用都配置生成器。
- 复杂 DTO 与宿主能力优先源生成 `JsTypeInfo<T>` / typed binding；未知类型和不支持的签名在可诊断的构建期或注册阶段明确拒绝，不在 AOT 下静默反射/发码回退。
- 生成代码必须有真实的静态成员引用或经过验证的元数据保留，覆盖实际闭合泛型；不能运行后才发现所需泛型代码或成员已被裁剪。
- **AOT 不等于禁止所有反射。** 对已知、静态保留元数据的受控反射可单独核验，但不是核心快路；若某便利适配无法满足 AOT，它不进入当前正式运行时交付范围。
- Parser、序列化依赖、绑定、宿主扩展和完整调用链都必须通过 publish + 实际运行；`IsAotCompatible` 标记和一次普通 build 都不是验证结论。
- 后续库工程应声明 `IsAotCompatible` 并启用 AOT/trim 分析。CI 将相关告警视作失败；仅对有静态保留/调用证据及发布运行测试的分析误报作局部、有记录的处置，禁止全局 suppress 掩盖缺失能力。
- `Jaunt.Jit` / 运行时 JS→IL 移出当前交付主路线。未来如另行研究 CoreCLR 专用加速，不能成为功能或性能验收的依赖，更不能替代完整 AOT 路径。

参考：[Native AOT 官方限制](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)，其中明确不支持运行时代码生成，表达式树可能以解释形式执行。不能把后一种隐式退化当作已经满足 Jaunt 的性能设计。

源生成器是构建期工具，其 TFM 由 Roslyn 宿主兼容要求决定；它不是给 Jaunt 增加 .NET Standard 运行时支持。托管依赖自身包含较低 TFM 资产也不改变引擎的支持承诺。

## 5. 验证矩阵

### 运行模式

| 模式 | .NET 8 | .NET 10 | .NET 11 |
| --- | --- | --- | --- |
| CoreCLR / 普通 JIT 宿主 | 必测 | 必测、生产基线 | RC 必测；GA 后复测 |
| Trimmed self-contained | 必测完整承诺调用链 | 必测完整承诺调用链 | RC 必测；GA 后复测 |
| NativeAOT 宿主 + 动态 JS 编译/执行 | **必测、发布门槛** | **必测、发布门槛** | **RC 必测；GA 后必测** |
| 生成/手写绑定、DTO/JSON、Hosting | 包含在 AOT 全链测试 | 包含在 AOT 全链测试 | 对应通道必须覆盖 |
| 受控反射适配（若纳入交付） | 仍须 AOT/trim 验证 | 仍须 AOT/trim 验证 | 对应通道必须覆盖 |

操作系统目标为 Windows、Linux、macOS；架构目标为 x64、ARM64，具体组合取三版运行时官方支持交集。首轮至少覆盖 Linux x64、Windows x64、macOS ARM64；其余组合补齐测试后才进入已验证清单。Linux 的 glibc/musl、不同 RID 和 NativeAOT 工具链单独记录，不从一次成功构建推断全平台可用。

### 必须记录

- SDK 与运行时版本、RID、OS、CPU、GC 模式、Tiered PGO、构建配置。
- 公共 API 对照与编译消费测试，防止三个 TFM 暴露不同签名。
- 同一 feature manifest、API 与语义测试在各 TFM、CoreCLR/NativeAOT 下的一致性。
- AOT 发布告警、产物大小、启动时间、端到端性能和真实运行结果，不能用 CoreCLR 的成绩替代。
- SIMD 开关、文化/时区/Unicode 差异、弱内存序与 GC 压力测试。

### NativeAOT 发布验收

1. 使用真实消费宿主，在承诺的 framework/RID 上以 `PublishAot=true` 发布，并直接运行生成的原生可执行文件；只 `build` 或 `dotnet run` 不算通过。
2. 发布后再提供源码，验证动态 JS 的解析、编译、执行和错误路径，不能只测构建时已嵌入的脚本或 `1 + 2`。
3. 随阶段覆盖模块图、异步宿主函数、生成/手写 DTO、JSON、取消/限额、异常与 FreshRealm 清理；所有正式运行时扩展遵守同一门槛。
4. 与 CoreCLR 对照相同功能和政策；缺失元数据、未生成泛型、隐式反射/发码回退都阻断发布，不以 AOT 专用 skip 掩盖。
5. 单独建立 AOT 的启动、吞吐、分配、留存和产物大小基线，不预设 NativeAOT 的热吞吐一定优于 CoreCLR。

## 6. 本轮实际检查的边界

当前工作机为 macOS ARM64；命令输出显示已安装 .NET SDK `10.0.401` 与 .NET 11 的若干预发布 SDK，当前默认为 `11.0.100-rc.2.26504.105`。没有发现 .NET 8 SDK/运行时。

这只是环境盘点，**没有安装或更换 SDK，没有编译 Jaunt，没有完成上述任何运行矩阵验证**。后续实施时固定构建 SDK，补齐测试环境，并重新检查已经更新的 RC/GA 版本。
