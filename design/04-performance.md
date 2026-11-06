# 性能、正确性与隔离验证方案

> 状态：验证计划，2026-11-03。**本文件没有 Jaunt 实测结果。** 数字门槛是建议验收目标，必须由 P0 基线校准，不是对外性能承诺。（2026-11-06 更新：已实施为 `0.1.0-alpha.1`，实测与偏差见 [07-implementation-status.md](07-implementation-status.md)；本文保留为设计记录。）

## 1. “高性能”需要多个维度

首要优化对象是嵌入式插件的完整成本：

```text
排队/取得实例 + 初始化 + 输入转换 + JS 执行
+ 宿主回调 + Promise 调度 + 输出转换 + 回收
```

同时单独测量编译、实例化与纯执行，才能判断优化来自哪里。不能仅让 Jaunt 使用预编译和预热路径，却让对照在每轮重新解析。

主要指标：

- 冷进程首个结果、冷编译、创建引擎、模块加载延迟。
- warm invocation 的 p50/p95/p99、吞吐、每操作分配量。
- 实例增量留存、共享成本、进程峰值 RSS、稳态内存。
- 多实例并发下的 GC 暂停、排队、尾延迟和退化。
- 取消响应、资源限额检查开销、长期执行的内存保留。
- NativeAOT 发布大小、启动、运行与功能等价性。

**NativeAOT 是强制验收主线，不是附加测试项。** CoreCLR 与 NativeAOT 分别建立正确性、启动、热执行、转换和内存基线；不能用 CoreCLR 的 JIT/PGO 成绩，或未来非 AOT 实验的收益，替代 AOT 的功能/性能验收。

“平均更快”“没有 P/Invoke”“通过了微基准”都不能替代这些指标。

## 2. 对照对象与版本规则

| 对照 | 用途 | 注意事项 |
| --- | --- | --- |
| Jint 稳定发布包，本轮所见 4.17.0 | 主要成熟托管基线 | 使用 prepared script/module、缓存函数与合理绑定方式 |
| Jint 5/main | 观察未来能力 | 与稳定包单列，锁 commit，不混成一个结果 |
| Okojo prerelease | 同类寄存器 VM/AOT 路线 | 当前 .NET10+；net8 的缺失标 N/A，不计成败绩 |
| ClearScript/V8 | 原生集成路线 | 普通 interop 与 FastProxy 分列；记 native heap/RSS |
| Jurassic | JS→IL 路线的特定比较 | 仅运行共同支持语法，记录编译预热成本 |
| QuickJS 原生与 MoeJS | 产品思路、必要的外部参照 | 不同语言宿主/桥接路线单列，不伪装成同一 .NET 基准 |

所有包、依赖、源码 commit、运行时、输入集都固定并公开。基准发布时重新核对版本，不能永久以本次检索日期替代版本维护。

AOT 对照必须先验证所选版本及完整绑定/转换路径确实可 NativeAOT 发布运行。不能从竞品 main 的说明推导稳定包保证；不支持的组合标 N/A，不改用 CoreCLR 对照后仍标成 AOT 比较。

## 3. 工作负载矩阵

| 编号 | 工作负载 | 主要观察点 |
| --- | --- | --- |
| C01 | 新进程求值 `1 + 2`，逐次独立进程 | CLR/引擎真实冷启动，不能只 new Engine |
| C02 | 1KiB/10KiB/100KiB 模块编译 | parser、绑定、字节码、AST/source 分配 |
| C03 | 共享同一编译产物创建 1/100/1000 个实例 | 共享成本与单实例成本、lazy builtin 初始化 |
| E01 | 数字/字符串/布尔表达式、循环、分支 | dispatch、value 布局、数值转换 |
| E02 | 单态/多态对象访问、prototype 变化、getter、dictionary | shape/IC 命中与失效；不能只测永不变化的对象 |
| E03 | 普通函数、闭包、递归、rest/arguments、异常/finally | 帧、逃逸、异常表及语义对象成本 |
| H01 | 每轮 1/10/100 次 C#↔JS 标量调用 | 固定 arity、typed delegate、raw callback、反射路径差异 |
| H02 | DTO→JS→DTO，同字段集合及动态字段集合 | 生成绑定、数字/空值转换、重复映射与保留 |
| J01 | 1KiB/64KiB/1MiB/8MiB JSON，只读取少数字段 | 输入复制、校验、索引、懒物化、整块 retention |
| J02 | JSON 全遍历、改写、增删键、数组变化、输出 | 懒策略的最坏情况、copy-on-write 与输出成本 |
| P01 | 请求路由/请求体改写/响应提取/计价规则等完整插件 | 真正业务端到端成本，含异常路径 |
| A01 | 已完成/延迟 Task、Promise 链、async host calls | 同步完成快路、pump、唤醒与操作生命周期 |
| A02 | 超时、拒绝、never-settled Promise、迟到 completion | 取消延迟、队列容量、无污染回收 |
| I01 | 每次 FreshRealm vs 同一会话重复调用 | 隔离成本，不混淆 state reuse 与 request isolation |
| I02 | 并发独立引擎，worker 为 1/N/2N | CPU 饱和、GC、排队、工作集与尾延迟 |
| L01 | 长期重复小调用，中间穿插超大输入 | 池中大 buffer、闭包、IC/cache 和旧输入的泄漏 |
| N01 | NativeAOT 发布产物接收发布后才提供的新 JS | 动态解析/编译/执行、完整绑定/数据/异步链，不只执行预嵌入脚本 |

N 为机器可用核心数，并披露容器/CPU quota。随机种子、输入字节与预期输出固定，必须先验证结果正确再计时。

P01 优先用 Jaunt 自建、可再分发的 workload，覆盖正常与抛错分布。参考 MoeJS 的产品场景，但不直接把 AGPL 插件拷入产品或测试包。

## 4. 公平比较的强制规则

1. 相同代码、输入、可观察结果和错误路径；不支持的特性标 N/A。
2. 相同 warm/cold 层级：源码求值、预编译执行、预加载模块分别比较。
3. 相同隔离语义：可变独立 builtins、冻结 profile、会话复用、新 Realm 分开。
4. 相同输入所有权：借用不可变输入与复制快照不可在同一列隐藏差别。
5. 相同输出契约：JsValue、宿主对象、DTO、UTF-8 JSON 分开；序列化费用计入端到端列。
6. 相同安全政策：限额开启状态、检查频率、输出预算均披露；无约束数字不得作为默认安全配置成绩。
7. 比较各产品合理的快路径，不只拿竞争对手的最便利/最慢 API。
8. 本体 heap、托管 heap、native heap、共享编译数据和 RSS 分列；不将不同口径直接相除。
9. 禁止因知道测试结果而跳过对象语义、取消、归属检查，或硬编码特定脚本。
10. 既报告每项，也报告分组几何均值/分位数；不能只展示赢的项目。

## 5. 测量方法

### 微基准与端到端分开

- 微基准计划采用 BenchmarkDotNet，固定可支持相应 runtime 的 runner 版本、warmup/iteration 参数；不能先假设现有 runner 已支持 .NET 11 RC。
- 首请求、RSS、并发负载与长时间 soak 用独立进程 harness，固定输入与并发模式。
- 空 harness、纯 .NET 等价流程作为背景开销参照，不从最终数字中任意减去费用。
- 普通 JS 结果必须被消费与校验，避免死代码消除或 benchmark 测错路径。

### CLR 预热与机器信息

记录 OS/RID、CPU 型号/核心数、电源模式、SDK/runtime、Server/Workstation GC、Tiered PGO、ReadyToRun/AOT、指令集、进程亲和性及机器负载。

冷进程不能复用已经 JIT 过的解释器；warm 结果必须给予各引擎合理预热。主线程多次 `new JsEngine()` 只能表示实例创建，不表示冷进程。

### 内存

- 同步单线程内核可用线程分配指标；跨线程/async 不可只用 `GC.GetAllocatedBytesForCurrentThread()` 代表总分配。
- 并发测试使用隔离进程、进程级分配/GC 事件和各引擎统计，说明采样边界与背景线程成本。
- 峰值 RSS 包含 native、JIT、栈、共享部分，与强制 GC 后的托管留存不是同一指标。
- 可在专门留存测试中做稳定化与 full GC；不要在真实请求计时热环中强制 GC 来美化结果。
- 输入归还、函数闭包、global/module 留存、已结束的取消注册和 queued jobs 都作为 root retention 排查对象。

### 统计

建议至少 3 轮独立进程实验；稳定微基准增加足够样本，输出分布/误差区间而不是单次最小值。p99 需要足够请求数；独立闭环吞吐不能冒充包含排队的用户尾延迟，负载测试说明到达模型，避免 coordinated omission。

## 6. 初步目标与回归门槛

以下是**待 P0 校准的内部目标**，不是已达成成绩，也不是对每个 workload 的保证：

| 类别 | 建议目标 |
| --- | --- |
| 核心短插件 P01/H02/J01 分组 | 在 .NET10 的同模式、同隔离/转换/限额条件下，争取相对可比 Jint 稳定版约 30% 的端到端延迟改善；CoreCLR/NativeAOT 分列，每项公开 |
| warm 固定 arity 标量 VM 调用 | 将不产生 JS 语义对象的内核机械路径做到接近或达到 0 B/op；公开 scope、取消注册、结果映射的额外费用 |
| 实例创建与留存 | 不劣于主要托管基线；重点争取独立实例密度，不能靠冻结/共享状态偷换比较 |
| 安全检查 | 量化启用限额后的成本，争取常见 workload 额外耗时不超过约 10%；不能为达标删除检查点 |
| 已建立基线后的回归 | 稳定环境同场景耗时劣化 >5% 或分配增加 >5% 触发复核；确认显著再阻断，不用噪声自动判死 |
| 语义与隔离 | 声明支持范围内无未解释失败；任何跨租户污染、Use-after-return 或预算绕过阻断发布 |

.NET8 与 .NET11 分别报告，不由 .NET10 数字外推；.NET11 RC 结果不作为 GA 保证。相对性能目标只在同模式存在有效对照时评估；没有可比 AOT 基线时披露 N/A，并在 P0 冻结独立的 AOT 成本预算/回归门槛，不能据此豁免 AOT 验收或用 CoreCLR 数字代替。若 P0 找不到真实场景优势，应先修改执行/数据路线，而不是继续扩大标准库后才发现产品没有优势。

## 7. 正确性验证体系

### test262

使用固定 revision 的 [test262](https://github.com/tc39/test262)，按其 [运行规范](https://github.com/tc39/test262/blob/main/INTERPRETING.md) 实现 harness。

必须正确处理 strict/sloppy、module、negative 的 parse/early/runtime 阶段、async `$DONE`、includes、feature flags 和必要的 harness 辅助能力。

每次报告至少包含：

```text
引擎版本 / runtime / backend / profile / test262 revision
总用例与执行变体数
pass / fail / expected-fail / skip / timeout / harness-error
feature manifest 与每个 skip 的原因
已知偏差、失败列表及对应问题
```

- 文件数和 strict/sloppy 执行变体数不能混用。
- 未实现特性按公开清单跳过，不按失败结果临时加过滤。
- 已知错误仍属于错误，不通过缩小统计口径消失。
- Hardened 的有意限制与 Standard 的一致性结果分开。
- test262 不是完整证明，还需宿主、并发、生命周期和性能回归测试。

### 差分与 fuzz

- 与 Jint、V8、QuickJS 在共同支持且确定性的语义上差分；不把任一实现当绝对规范 oracle。
- 对 parser、bytecode verifier、值转换、JSON、对象/IC、module graph 和 Promise 状态机 fuzz。
- 对所有优化保留能关闭的参考路径，用 eager vs lazy、IC on/off、通用解释器 vs AOT 可用的专门化/构建期快路径做差分，并在 CoreCLR/NativeAOT 间对照同一特性清单。
- 单独覆盖 NaN、负零、整数精度、代理项、property order、hole/undefined、TDZ、getter/Proxy 副作用、finally 与 abrupt completion。
- Unicode、时间、随机数、时区、网络不可控结果用固定环境或 mock，不混成稳定差分输入。

## 8. 必须阻断发布的隔离/资源测试

1. 上一租约的 global/module/闭包/prototype 修改不得出现在下一 FreshRealm。
2. 上次取消、I/O completion、timer、Promise rejection 不能修改下一代 Realm；也不能错误丢弃仍合法的会话工作。
3. callback 参数 Span、借用 buffer、池化 register/slot 在归还后不可继续访问。
4. 对象/函数/模块句柄跨引擎或过期后明确失败，不只是检查 callable。
5. 长循环、深递归、正则、字符串操作、JSON 转换、getter/toJSON 微任务攻击都触发预算。
6. parse/compile/module graph 也受限；超时 API 返回后后台不能仍无限消耗 CPU。
7. 输出在超限前检查扩容/写入；返回错误时不得把部分 DTO/JSON 当成功结果提交。
8. 生成与手写绑定、DTO/JSON、异步桥接及正式扩展在 AOT 下均不偷偷反射或运行时发码回退；所有声明类型、闭合泛型、相关生成/trim/AOT 诊断和消费测试齐全。
9. 不受信代码不能经 CLR 类型、反射、文件 resolver、URL redirect/DNS/IP 或宿主异常意外取得能力。
10. dispose、普通 JS throw、终止、内部错误的状态机与淘汰行为一致；Dispose 不运行 JS，不依赖 JS finally 承担宿主资源回收。
11. 修改异步参数底层数组不会改变已提交参数；同步入口在调用 async host delegate 前拒绝，不能按 Task 完成快慢改变规则。
12. 请求式 root 对未结外部工作给 `UnjoinedAsyncWork`；测试 Promise.race loser、fire-and-forget、无关 pending JS Promise，以及之后由 JS 解决的合法 Promise。
13. Standard 编译产物不能绕过 Hardened；动态代码限制覆盖各类构造器、别名和 `.constructor`，而非只删除 global 名称。
14. 同步模块加载在有 JS 副作用前识别必须异步的图；TLA/cycles/live binding/失败缓存均有验收案例。
15. 每个已承诺 framework/RID 必须实际发布并运行 NativeAOT 宿主，再提供构建时未知的新 JS；普通 build、`dotnet run` 或只执行预嵌入脚本不算通过。
16. 每阶段已声明的运行时能力在 AOT 下完整可用，不增加 AOT 专用 skip 掩盖功能缺失；未解决的 AOT/trim 诊断、缺失成员/泛型或仅 CoreCLR 成立的必需路径阻断发布。

## 9. 产物与发布纪律

后续实施时再建立 benchmark 工程、机器可读结果与图表；本轮不创建伪结果文件。

每份公开性能结论应能追溯到代码版本、脚本/输入、命令、机器、原始结果和基线版本。测试方法或语义改变时建立新基线，不悄悄把不可比数字画在同一趋势线上。
