# 性能优化设计：基于 2026-11-07 Jaunt / MoeJS / Jint 对比

> 状态：阶段 A 与阶段 B 中的 K1/K3/T1/O1/C2 已于 2026-11-07 实施并验证（见 §8）；第二轮的访问器/不存在/megamorphic IC、字符串 for-of、JsRegExp 精简见 §9；第三轮的调用路径（C3 评估为无独立价值、C5 评估为不做、新增重叠寄存器窗口）见 §10；第四轮的 Map/Set 表、A4（reviver 路径）、A5、字符串拼整数与 4.7 GC 指引见 §11。未做：K2、D2/D3。下文的方案与估计保留为设计记录。所有数字来自本机实测（Apple M4，10 核，macOS 26；.NET SDK 10.0.401，Workstation 并发 GC；Go 1.27.3；MoeJS v0.1.0-alpha.11；Jint 4.17.0），只用于排序优先级与设定验收口径，不是对外承诺。标注“估计”的数字未经测量。
> 方法与规则沿用 [04-performance.md](04-performance.md)（公平比较、回归门槛）与 [02-architecture.md](02-architecture.md)（所有快路径必须有 guard 与等价慢路径；不运行时发 IL；不把引用藏进数值；无界缓存禁止）。

## 1. 现状与目标

两轮 `bench/cross/run.sh`，每项取较好一轮（结果一致：MoeJS/Jaunt 几何均值 0.972 与 0.974）：

| 指标（几何均值，Jaunt 用时 = 1） | MoeJS | Jint |
| --- | ---: | ---: |
| 预热后单次调用（56 项三引擎共有） | **0.97×** | 2.55× |
| 首次调用（新建引擎 + 加载 + 运行；MoeJS 共享冻结内建，不对等，见 `bench/cross/README.md`） | 0.68× | 2.51× |
| 编译 | 0.86× | 1.16× |
| 最快项数（59 项） | 32 | 3（Jaunt 24） |

按类型：Jaunt 领先函数式（+14%）、对象与类（+12%）、字符串与正则（+12%）；MoeJS 领先异步（16%）、数值计算（13%）、应用类（8%）。

Jaunt 落后 ≥ 25% 的项（MoeJS 用时 / Jaunt 用时）：

| 工作负载 | Jaunt | MoeJS | 比值 | Jaunt 分配/次 | 主因（§3） |
| --- | ---: | ---: | ---: | ---: | --- |
| functional/higher-order | 12.85 ms | 6.99 ms | 0.54 | 12.8 MB | R1 回调调用、R2 索引键、R5 分配 |
| compute/matmul | 9.03 ms | 5.26 ms | 0.58 | 2.1 MB | R2 复合赋值键字符串化、R3 typed array |
| compute/fib | 3.99 ms | 2.37 ms | 0.60 | 0 | R1 调用 |
| compute/sort-compare | 14.65 ms | 9.68 ms | 0.66 | 6.1 MB | R1 比较器调用、R5 排序拷贝 |
| collections/array-ops | 3.98 ms | 2.68 ms | 0.67 | 1.9 MB | R2 内建通用路径 |
| compute/crc-base64 | 14.96 ms | 10.36 ms | 0.69 | 10.1 MB | R2/R5（待剖析） |
| compute/spectral-norm | 8.32 ms | 5.83 ms | 0.70 | 3 KB | R1 调用 |
| objects/classes-inheritance | 7.63 ms | 5.36 ms | 0.70 | 1.5 MB | R1 调用/构造（待剖析） |
| apps/graph-algos | 23.33 ms | 16.58 ms | 0.71 | 5.4 MB | R4 对象数值键、R1 |
| advanced/json-roundtrip | 10.25 ms | 7.45 ms | 0.73 | 20.4 MB | R5 LOH/GC、正则字面量 |
| apps/huffman-lz | 24.14 ms | 17.62 ms | 0.73 | 38.6 MB | R5、R4（待剖析） |
| compute/levenshtein | 6.51 ms | 4.76 ms | 0.73 | 0.9 MB | R6 dispatch 密度 |
| collections/map-set | 3.96 ms | 3.02 ms | 0.76 | 5.9 MB | 待剖析 |

**目标**（验收口径，按 04 §6 的 5% 回归门槛执行）：

1. 预热后几何均值 MoeJS/Jaunt ≥ 1.0；上表没有任何一项低于 0.80。
2. 调用密集型（fib、spectral-norm、sort-compare、higher-order 的回调部分）每次 JS 调用 ≤ 16 ns（现 22–24 ns，MoeJS 16–19 ns）。
3. 不以降低语义、预算检查、隔离或 NativeAOT 覆盖为代价；每个快路径仍有 guard 与慢路径，并进 `FastPathSemanticsTests` 对照。
4. 不改变 `JsValue` 16 字节布局，不引入运行时 IL 生成（AOT 约束）。

## 2. 分析方法与证据

四类证据互相印证，单独一类都不够：

1. **采样剖析**（dotnet-trace，`--run-loop` 8 s）：找出哪些路径没走快路径。注意：本机上 `Thread.PollGCWorker` 的样本占比严重失真（state-reducer 显示 97%，但 `GC.GetTotalPauseDuration` 只占运行时间 2.9%），GC 开销以第 2 类证据为准。
2. **GC 计数与暂停时间**（`--run-loop` 输出 gc0/gc1/gc2/pause）：

   | 工作负载 | 默认 | `GCgen0size=64MB` | `gcConcurrent=0` | 两者 | GC 暂停占比（默认） |
   | --- | ---: | ---: | ---: | ---: | ---: |
   | apps/state-reducer | 61.1 ms | 66.1 ms | 60.0 ms | 65.7 ms | 2.9%（gc2 每 26 次） |
   | compute/sort-compare | 14.7 ms | 14.6 ms | 15.0 ms | 14.6 ms | 1.8%（**每次都触发 gen2**） |
   | advanced/json-roundtrip | 10.5 ms | 9.2 ms | 9.5 ms | 9.1 ms | **27%**（gen2 每 2.5 次） |
   | functional/higher-order | 13.2 ms | 11.5 ms | 11.6 ms | 11.2 ms | **21%**（gen2 每 1.7 次） |

   结论：GC 只在 LOH 频繁分配（≥ 85 KB 的数组/字符串）的工作负载上是主因；state-reducer、sort-compare 的瓶颈是回调调用次数（state-reducer 每次约 1.7M 次 `filter`/`map` 回调），不是 GC。GC 参数是宿主级决定（§4.7），不能作为引擎默认。
3. **隔离微基准**（附录 A 的 20 个片段，走同一套 `--cross` harness 与 MoeJS runner，三引擎同口径）：

   | 片段 | 每次操作 Jaunt | MoeJS | Jint | MoeJS/Jaunt | 分配 Jaunt / MoeJS |
   | --- | ---: | ---: | ---: | ---: | ---: |
   | call-js（JS→JS 调用） | 23.5 ns | 19.2 ns | 110 ns | 0.82 | 0 / 0 |
   | fib(24)（150k 次调用） | 26.6 ns/调用 | 15.8 ns | 134 ns | 0.60 | 0 / 0 |
   | cb-foreach（内建→JS 回调） | 26.6 ns | 16.8 ns | 46 ns | 0.63 | 516 KB / 653 KB |
   | sort-num（20k 元素带比较器） | 10.06 ms | 7.07 ms | 19.8 ms | 0.70 | 3.9 MB / 2.5 MB |
   | ta-read（Float64Array 读） | 13.2 ns | 7.9 ns | 52 ns | 0.60 | 19 KB / 18 KB |
   | ta-rmw（`c[i] += 0.5*b[i]`） | 59 ns | 32 ns | 120 ns | 0.54 | **2.0 MB** / 37 KB |
   | flatmap（10k 对象 ×3） | 6.26 ms | 3.30 ms | 6.64 ms | 0.53 | **10.9 MB** / 6.8 MB |
   | obj-numkey（普通对象数值键） | 69 ns | 32.5 ns | 158 ns | 0.47 | 4 KB / 3 KB |
   | obj-strkey-same（同对象字符串键） | 74 ns | 82 ns | 146 ns | 1.11 | 6 KB / 6 KB |
   | regex-literal（函数内 `/re/.test`） | 171 ns | 124 ns | 209 ns | 0.73 | **420 B/次** / 144 B |
   | push-grow（20×10k push） | 23 ns | 23 ns | 40 ns | 0.97 | 10.2 MB / 13.0 MB |
   | spread-arr（`[...items, x]` ×1500） | 2.79 ms | 10.9 ms | 5.77 ms | 3.92 | **53 MB** / 50 MB |
   | spread-obj（`{...s, count}` ×100k） | 48.7 ns | 460 ns | 70 ns | 9.44 | 24 MB / 72 MB |
   | math-min / charcode / new-date / arr 读写 | — | — | — | 0.88–1.52 | — |

   Jaunt 在对象/数组 spread、Date、Math 调用、数组字面量读写上领先；落后集中在**调用、索引键、typed array、对象数值键、正则字面量**。
4. **两个临时补丁实验**（只改一行，测后 `git checkout` 还原，用于校准预期收益）：

   | 补丁 | 结果 |
   | --- | --- |
   | `PopFrame` 不清零寄存器 | call-js 2182 → 2040 µs（−6.5%），fib 3660 → 3264 µs（−11%）：清零占每次调用约 1.5–2 ns，其余在帧建立与 dispatch |
   | `ToPropertyKeyValue` 对合法数组索引的数字原样返回 | ta-rmw 6373 → 5071 µs（−20%），分配归零（gc0 313 → 7/8 s）；matmul 8269 → 7077 µs（−14%）。ta-rmw 仍是纯读的 3 倍，说明复合赋值路径还有其他开销（§3 R3/R6） |

另外：MoeJS 在分配密集项上 CPU/墙钟 = 1.4–2.0（Go 并行 GC 用了其他核），Jaunt 恒为 1.00。按 CPU 时间算，state-reducer（66 vs 92 ms）、closures（8.7 vs 21.9 ms）、json-roundtrip（10.6 vs 11.9 ms）Jaunt 反而领先。墙钟差距的一部分是 GC 并行度，引擎侧能做的是**少分配**（`report.py` 的 `METRIC=cpu` 模式用于交叉核对）。

## 3. 根因（按影响排序）

### R1 JS 调用/返回路径：22–24 ns，目标 ≤ 16 ns

影响：fib、spectral-norm、sort-compare（约 27 万次比较器调用）、higher-order、state-reducer（每次约 170 万次回调）、closures、mini-interpreter、graph-algos、astar、event-emitter、raytracer。这是单项收益最大的杠杆。

现路径（`Runtime/Vm.cs` `PushFrame`/`SetupFrame`/`PopFrame`，`Runtime/Interpreter.cs` `Op.Call`/`Return`）：

- `PushFrame`：深度检查 → `RentFrame`（按深度查池）→ `AllocRegs`（chunk 检查、两个 out 参数）→ 参数逐元素拷贝 → `NeedsArgsArray` 检查 → `SetupFrame`：Callee/Env/ThisBox/This 四处“变了才写”的比较、`Caller = Current`、`Current = f`（两次写屏障）、Realm 比较、Arrow/Strict/this 强制分支、`Depth++`。约 15 个分支、10 次存储。
- `PopFrame`：逐寄存器清零（≤ 24 个时循环，实测约 1.5–2 ns）、chunk 回退、三个字段置空。
- 内建 → JS 回调（`Vm.Call` → `JsClosure.Call` → `CallClosure` → `EnsureStack` → `Run` → 嵌套 `RunLoop`）只比 JS→JS 多约 3 ns（cb-foreach 26.6 vs call-js 23.5），嵌套 `RunLoop` 本身不是瓶颈；但每次回调都做 `TryEnsureSufficientExecutionStack`，并且排序走 `Comparison<JsValue>` 委托 + `Operations.ToNumber`。

### R2 索引键被字符串化

- `Operations.ToPropertyKeyValue(number)` 返回字符串（`Runtime/Operations.cs:89`）。CodeGen 对复合赋值/逻辑赋值的计算成员（`c[k] += v`、`acc[g] ||= x`、`dp[i][j] = …` 中需要“键只求值一次”的形式）先发 `Op.ToPropertyKey`（`Compiler/CodeGen.Expressions.cs:744,772`），之后的 `GetElem`/`SetElem` 看到的是字符串键，数字快路径全部落空，走 `GetElemSlow`/`SetElemSlow` → `TryParseArrayIndex`，每次分配一个字符串（matmul 剖析里的 `Number.UInt32ToDecStr`、`PropertyKey.IndexToString`）。
- `PropertyKey.FromIndex(uint)` 直接构造字符串（`Runtime/PropertyKey.cs:112`），`ArrayBuiltins.Key(i)` 据此生成。凡走通用路径的内建（`Flatten`/`flat`/`flatMap`、`concat`、`splice`、`slice`、`indexOf`/`includes`、`copyWithin`、`reverse`、`fill`、`Array.from`、`toSorted`、`Object.groupBy`…）每个元素至少一次 `HasProperty(Key(i))` + `Get(Key(i))` 或 `CreateDataPropertyOrThrow(Key(i))`，各分配一个字符串并做一次哈希查找。flatmap 的 10.9 MB 与 `Flatten` 13.5% 自身耗时即来自这里；`Iterate`（map/filter/forEach）已有 `TryGetDense` 快路径，所以差距集中在没有快路径的内建上。
- `Op.ToPropertyKey` 在 `RareOp` 中（额外一次非内联调用 + switch）。

### R3 typed array 元素访问

`JsTypedArray.GetElement`/`SetElement`（`Builtins/TypedArrayBuiltins.cs:196`）每次：`ElementSize(Kind)`、`Length` 属性（可能按 length-tracking 视图计算）、`Buffer.Data.Length` 检查、`AsSpan` + 按 Kind 的 switch；`GetElem` 先做 `is JsArray` 失败再 `is JsTypedArray`。纯读 13.2 ns vs MoeJS 7.9 ns；读-改-写在 R2 修复后仍 44 ns。

### R4 普通对象的计算键访问没有 IC

`obj[k]`（k 为数字或运行时字符串）在 `JsObject` 上总是 `GetElemSlow` → `ToPropertyKey` → `Shape.IndexOf` → 字典查找（`Shape.IndexOfSlow` 的 `Dictionary<PropertyKey,int>`，含字符串哈希/比较）。obj-numkey 69 ns vs 32.5 ns；higher-order 的 `acc[d.g]`、graph-algos/huffman-lz 的“对象当 map 用”都走这条路。

### R5 分配量与 LOH

- **数组字面量 spread**：`[...items, x]` 先按 `items.Length` 精确分配，再 `ArrayPush` 触发 `EnsureCapacity` 倍增 → 每次 3× 所需容量（spread-arr 53 MB，理论 18 MB）。state-reducer 的 `add` 分支、destructure-spread 受影响。
- **排序**（`ArrayBuiltins.SortObject`）：`List<JsValue>` 收集 → `ToArray` → `tmp` 数组 → 写回，20k 元素时四块 320 KB 的 LOH 分配，**每次调用触发一次 gen2 GC**。比较器经 `Comparison<JsValue>` 委托、`realm.Vm.Call`（虚调用 + `EnsureStack`）与 `ToNumber`。
- **JSON**：`StringBuilder` 线程缓存已存在，但输出字符串（本工作负载约 55k 字符 = 110 KB）与 builder 扩容块进 LOH，json-roundtrip 的 GC 暂停占 27%。
- **正则字面量**：每次求值 `RegExpBuiltins.Create` 经进程级 `s_cache` 的 `lock` + 元组字典查找，再 `new JsRegExp`（420 B/次，MoeJS 144 B）。剖析中 `Monitor.Enter_Slowpath` 5.4% 来自 json-roundtrip reviver 中的 `/^\d{4}-/`。
- **结果数组增长**：filter/flatMap 结果按倍增增长到 15k 元素（240 KB）时产生 LOH 垃圾链，这是 higher-order 21% GC 暂停的来源。

### R6 dispatch 密度

levenshtein 去掉 `Math.min`、`charCodeAt`、`length`、rope 的变体分别只快 3–13%，没有单一热点：内层每轮约 25 条指令、125 ns，即约 5 ns/指令，MoeJS 约 4 ns。`RunLoop` 对 `code[pc + n]` 与 `regs[bp + …]` 的每次访问都带边界检查；`ToPropertyKey`/`ToStringOp` 等常见指令在 `RareOp` 里。sudoku、game-of-life、fannkuch 同样受 dispatch 成本约束（这些项 Jaunt 已持平或领先，说明 dispatch 不是最差的部分，但是普遍的 10–20% 空间）。

### R7 GC 配置（宿主级）

§2 第 2 类证据：`GCgen0size`/`gcConcurrent` 对 LOH 密集项有 13–15% 收益，对 state-reducer 反而 −8%。不是引擎默认值，作为宿主调优文档与 harness 披露项（§4.7）。

## 4. 方案

每项都给出：改动点、guard/慢路径、验证、预期（测量值或“估计”）。

### 4.1 调用路径（R1）

| 编号 | 改动 | 语义与 guard | 预期 |
| --- | --- | --- | --- |
| C1 | **不在 `PopFrame` 清零寄存器**。`FunctionTemplate` 新增 `UndefInitCount`/位图（CodeGen 从绑定分析得到：未显式初始化就可能被读的 `var`/临时寄存器），`PushFrame` 只初始化这些；root 执行结束（`ReleasePooledFrames` 处）对寄存器栈高水位以下整体 `Array.Clear`，保持 02 §9 “空闲引擎不留存对象图”的承诺 | 执行**期间**死帧的寄存器可能延长对象存活，上限是本次执行；这是可接受的、需在 FEATURES 披露的变化。验证：Debug 配置下 `PushFrame` 用毒值（`Empty` 之外的专用 tag 或 `Symbol`）填充新帧全部寄存器，跑全部单测 + test262，任何读到毒值的指令即为未初始化读 | 测量：call-js −6.5%，fib −11% |
| C2 | **`SetupFrame` 分支折叠**：`FunctionTemplate` 预计算 `CallKind`（strict/非 arrow/不需要 this 强制/同 Realm 的“简单调用”），`Op.Call`/`CallMethod` 对简单调用走一条无比较的直写序列；`Caller` 改为帧池索引（int）而不是引用，去掉一次写屏障；`This` 比较改为 `Bits`+`Ref` 一次性比较 | 无语义变化；非简单调用走现路径 | 估计 2–4 ns |
| C3 | **调用点单态 IC**：`Feedback` 为每个 `Call`/`CallMethod` 站点缓存上次的 `FunctionTemplate`，命中时跳过 `is JsClosure`、`InlineCallMask`、`IsClassConstructor` 检查 | guard：`ReferenceEquals(c.Template, ic.Template)`；IC 在 Feedback（Realm 私有）中，模板不可变（`SharedCodeInvariantTests`） | 估计 1–2 ns |
| C4 | **内建回调**：`CallClosure` 的 `TryEnsureSufficientExecutionStack` 改为按 `Depth` 增量（每进入 16 层原生递归一次）或仅在 `EnterNative` 做；排序比较器不经 `Comparison<JsValue>` 委托，直接 `PushFrame` + `Run` 并用数字快路径做 `ToNumber` | `MaxStackDepth`/原生栈保护语义不变（`EnsureNativeStack` 仍在递归内建处） | 估计 cb-foreach 26.6 → ≈ 22 ns |
| C5 | （评估项）**帧结构化**：`Frame` 从池化 class 改为 struct 数组 + 索引，协程帧仍用堆对象 | 改动面大；先在 C1–C4 之后用 fib/call-js 决定是否值得 | 不承诺 |

验收：call-js ≤ 16 ns、fib ≤ 18 ns/调用、sort-num ≤ 7.5 ms、cb-foreach ≤ 20 ns；`DeepButLegalRecursionWorks`、`DeepJsRecursionIsBoundedByStackLimit`、尾调用、生成器/async 相关测试全部不变。

### 4.2 索引键保持数字（R2）—— 成本最低、已测量

| 编号 | 改动 | guard/慢路径 | 预期 |
| --- | --- | --- | --- |
| K1 | `ToPropertyKeyValue`：数字若是合法数组索引（整数、`< 2^32-1`）原样返回；`Op.ToPropertyKey` 进 `RunLoop` 主 switch，字符串/符号/索引数字为 no-op | 规范上 ToPropertyKey 只在对象键上有副作用，数字 → 规范字符串没有可观察差异；`GetElem`/`SetElem` 慢路径仍按原规则转换 | 测量：ta-rmw −20%，matmul −14%，分配归零 |
| K2 | `PropertyKey` 支持**索引形态**（`uint` + 无字符串），`FromIndex` 不再分配；`Shape`/字典按索引数值比较；`IndexToString` 只在真正需要字符串时（枚举、`Object.keys`、错误消息）调用 | `PropertyKeyComparer` 需让索引形态与等值字符串形态相等且同哈希（测试：`{ "1": a }` 与 `o[1]` 互通、`Object.keys` 顺序不变） | 估计：通用路径每元素少一次字符串分配与哈希 |
| K3 | 无快路径的数组内建加 dense 快路径（已有模式：`Iterate` 的 `TryGetDense` + pristine 检查）：`Flatten`、`concat`、`slice`、`splice`、`indexOf`/`lastIndexOf`/`includes`、`fill`、`copyWithin`、`reverse`、`Array.from`（数组源）、`toSorted`/`toReversed`/`with`、`Object.groupBy`/`values`；结果数组尽量精确预分配 | guard：`Dense && LengthWritable && Extensible && !ProtoChainMayHaveIndices`、元素非 `Empty`；任何一步失败回退逐元素通用路径（与规范一致的 HasProperty/Get 顺序）；`FastPathSemanticsTests` 加带 getter/原型索引/稀疏/Proxy 的对照 | 估计：flatmap 6.3 → ≈ 3.5 ms，array-ops −25%，higher-order −15% |

### 4.3 typed array 元素 IC（R3）

T1：`GetElem`/`SetElem` 站点 IC 新增 `IcKind.TypedElem`，缓存 `TypedKind`；命中时直接对 `Buffer.Data` 做 `Unsafe.ReadUnaligned`/`WriteUnaligned`（Float64/Float32/Int32/Uint32/Int16/Uint16/Int8/Uint8 八种），保留 detached/长度（含 length-tracking 视图）检查；`JsTypedArray.Length` 做成字段或内联属性。guard：`obj.Kind == ic.Kind`，`Buffer.Detached` 为假。预期（估计）：ta-read 13 → ≈ 8 ns，ta-rmw ≈ 20 ns，matmul 与 MoeJS 持平。

### 4.4 普通对象的键控 IC（R4）

O1：`GetElem`/`SetElem` 对 `JsObject` 接收者增加 `(Shape, key)` 单态缓存（`IcKind.KeyedOwnData`）：键为字符串时按引用相等（字符串键多数来自常量/同一来源，引用不等再回退一次 `string.Equals` 后更新 IC），键为索引数字时与 K2 的索引形态配合；对象索引键进入 shape（`HasIndexKeys` 已存在，核对其语义）。guard 同 `GetProp` 的 shape 比较。预期（估计）：obj-numkey 69 → ≈ 30 ns；higher-order、graph-algos、huffman-lz、sudoku 受益。

### 4.5 分配与 LOH（R5）

| 编号 | 改动 | 说明 | 预期 |
| --- | --- | --- | --- |
| A1 | 数组字面量 spread 预留尾部元素：CodeGen 给 `ArraySpread` 传“其后还有 n 个元素”的立即数，`TryAppendDense` 按 `start + n + extra` 一次扩容 | 无语义变化 | 测量口径：spread-arr 53 MB → ≈ 18 MB；state-reducer `add` 分支分配 −60%（估计） |
| A2 | 排序：规范要求先收集再排序再写回（SortIndexedProperties），所以不能原地；但收集可直接进一块按 `len` 精确分配的数组（去掉 `List` + `ToArray`），`tmp` 用 `Vm` 级可复用 scratch（按 `MaxTrackedBytes` 计费，root 结束时若超过阈值释放，避免 02 §3.4 的无界缓存） | 比较器异常/中断路径不变；scratch 不跨 root 保留引用（写回后清零） | 20k 元素时 LOH 分配从 4 块降为 ≤ 2 块；gen2 不再每次触发（估计 sort-compare −10%，再叠加 C4） |
| A3 | 正则字面量：`Op.NewRegExp` 站点在 `Feedback` 缓存 `CacheEntry`（编译产物，不含 Realm 引用），跳过进程级 `lock` + 元组哈希；`JsRegExp` 对象瘦身（`lastIndex` 仍是自有数据属性，但其余字段延迟） | 进程级缓存与大小上限保持（Feedback 缓存同样不保存超过上限的程序）；每次求值仍产生新对象（规范） | 估计 regex-literal 171 → ≈ 110 ns，分配减半；json-roundtrip 去掉 5% 的锁 |
| A4 | JSON.stringify 输出用 `ArrayPool<char>` 分块写入、最后一次 `new string`；JSON.parse 避免中间子串（已有 `_keyCache`，核对数值/字符串值路径） | 输出预算 `MaxOutputBytes`、tracked bytes 计费不变 | 估计 json-roundtrip GC 暂停 27% → 15% |
| A5 | 结果数组增长：filter/flatMap 在源为 dense 数组时先按源长度上界 reserve 再精确收缩，避免倍增链进 LOH | 内存峰值短暂增加（≤ 源数组大小），计入 tracked bytes | 估计 higher-order GC 暂停 −10 个百分点 |

### 4.6 dispatch（R6）

D1：`RunLoop` 的操作数与寄存器访问改为经验证后的无检查访问（`ref int code0 = ref MemoryMarshal.GetArrayDataReference(code)` + `Unsafe.Add`；寄存器同理），依据是 `BytecodeVerifier` 已在加载时校验所有寄存器索引、跳转目标与处理器边界（02 §3.3）。配套：Debug 配置保留检查版本；对 verifier 增加 fuzz（随机破坏字节码必须被 verifier 拒绝而不是在无检查循环中越界）。D2：`ToPropertyKey`、`ToStringOp`、`ArraySpread`、`NewRegExp` 等高频指令移入主 switch。D3：超指令候选（按 sudoku/levenshtein/game-of-life 的指令直方图决定）：`GetElem2`（`a[i][j]`）、`AddImm`/`SubImm`、`GetPropCall`（取方法后立即调用已有 `CallMethod`，核对覆盖率）。预期（估计）：dispatch 密集项 10–20%。

### 4.7 GC 配置指引（R7，宿主文档，不是默认值）

在 `docs/benchmarks.md` 与宿主文档中记录 §2 的测量：`System.GC.Gen0Size`（runtimeconfig `GCgen0size`）、`System.GC.Concurrent=false`、`GCLOHThreshold` 对分配密集插件的影响，以及 Server GC 在多引擎并发时的取舍。`bench/cross` 的 .NET 两个引擎共用同一配置，报告中披露；不为了数字好看单独给 Jaunt 调 GC。

## 5. 验证与验收

- 每个快路径：`FastPathSemanticsTests` 增加与通用路径的对照用例（含 getter/Proxy/原型索引/稀疏数组/detached buffer/被替换的 `RegExp.prototype`），全部单测、test262 全量（`docs/test262.md` 口径，0 跳过）、`eng/verify-aot.sh` 三条通道、`SharedCodeInvariantTests`（所有新缓存都在 `Feedback`/Realm 私有状态，模板字节不变）。
- C1 的毒值填充模式作为 Debug 配置的常规检查，不是一次性实验。
- 预算：新增的 scratch/预留容量全部计入 tracked bytes；没有进程级无界缓存；`MaxInstructions` 的 tick 计数在快路径中按元素数补记（现有 `Tick(n)` 模式）。
- 性能门：附录 A 的微基准三引擎同跑；`bench/cross/run.sh` 两轮取优 + `METRIC=cpu` 交叉核对；回归按 04 §6（>5% 复核）。每一阶段提交都附 `docs/benchmarks.md` 的更新与剖析结论，沿用 `cce8a08` 的做法。
- 不允许的做法：识别特定基准脚本、跳过预算检查点、改变 `JsValue` 布局换速度、以 CoreCLR 数字替代 AOT 验收（04 §1）。

## 6. 实施顺序与预期

| 阶段 | 内容 | 依据 | 预期（估计，待实测） |
| --- | --- | --- | --- |
| A（1–2 周量级的改动面，先做） | K1、A1、A2、A3、C1、C4 | 四项已有测量或改动极小 | matmul −15%，sort-compare −15%，fib/spectral-norm −10%，state-reducer −10%，json-roundtrip −8% |
| B | K2、K3、T1、O1、C2、C3 | 需要新 IC 种类与 PropertyKey 形态 | higher-order、array-ops、flatmap 接近 MoeJS；matmul 持平；obj-numkey 减半 |
| C | D1–D3、A4、A5、C5 评估 | 普遍性收益，风险在 verifier 完备性与改动面 | dispatch 密集项 10–20%；json/higher-order GC 暂停减半 |

阶段 A 完成后重跑三引擎对比并更新本文 §1 的表；若几何均值未达 1.0，再按新剖析调整 B/C 的顺序，不按本文估计硬推。

## 7. 风险与回退

| 风险 | 处理 |
| --- | --- |
| C1 未初始化读（TDZ、`var`、异常处理寄存器） | 毒值模式 + test262；任何失败即恢复清零并缩小为“只跳过参数寄存器清零” |
| D1 越界依赖 verifier | verifier fuzz；Debug 保留检查；`eval`/`Function` 生成的字节码同样经过 verifier（现状已如此，加测试锁定） |
| K2 影响面广（Shape、字典、枚举顺序、`Object.keys`/`for-in` 的整数键排序） | 先以 helper API 封装索引形态，分内建逐步迁移；每步跑 test262 的 `built-ins/Array`、`Object`、`Proxy`、`Reflect` |
| A3 的站点缓存持有编译产物 | 与进程级缓存相同的大小上限；`Feedback` 随 Realm 释放；超限程序不缓存 |
| A2/A5 的 scratch 与预留容量 | 计入 tracked bytes；root 结束释放大块；并发引擎各自持有（`Vm` 级，不是静态） |
| 微基准过拟合 | 验收以 59 项工作负载的几何均值与分组结果为准，附录 A 只用于定位 |

## 附录 A：隔离微基准（可直接放入 `bench/cross/workloads/` 的子目录运行）

每个文件定义 `main()` 并返回校验值；用 `node bench/cross/tools/node-expected.mjs <dir> expected.json` 生成期望值，再分别以 `--cross <dir> expected.json --json out.jsonl` 与 `moejs-runner -dir <dir> -expected expected.json -json out.jsonl` 运行。

```js
// call-js.js
function add(t, v) { return t + v; } function main() { let t = 0; for (let r = 0; r < 10; r++) for (let i = 0; i < 10000; i++) t = add(t, i); return String(t); }
// cb-foreach.js
function main() { const a = []; for (let i = 0; i < 10000; i++) a.push(i); let t = 0; for (let r = 0; r < 10; r++) a.forEach(v => { t += v; }); return String(t); }
// ta-read.js
function main() { const n = 2304, a = new Float64Array(n); for (let i = 0; i < n; i++) a[i] = (i % 13) / 13; let s = 0; for (let r = 0; r < 50; r++) for (let i = 0; i < n; i++) s += a[i]; return s.toFixed(3); }
// ta-rmw.js
function main() { const n = 2304, c = new Float64Array(n), b = new Float64Array(n); for (let i = 0; i < n; i++) b[i] = (i % 7) / 7; for (let r = 0; r < 50; r++) for (let i = 0; i < n; i++) c[i] += 0.5 * b[i]; let s = 0; for (let i = 0; i < n; i++) s += c[i]; return s.toFixed(3); }
// arr-newfill-write.js
function main() { const C = []; for (let i = 0; i < 32; i++) C.push(new Array(32).fill(0)); for (let r = 0; r < 100; r++) for (let i = 0; i < 32; i++) for (let j = 0; j < 32; j++) C[i][j] = i + j + r; let s = 0; for (const row of C) for (const v of row) s += v; return String(s); }
// arr-literal-write.js
function main() { const C = []; for (let i = 0; i < 32; i++) { const row = []; for (let j = 0; j < 32; j++) row.push(0); C.push(row); } for (let r = 0; r < 100; r++) for (let i = 0; i < 32; i++) for (let j = 0; j < 32; j++) C[i][j] = i + j + r; let s = 0; for (const row of C) for (const v of row) s += v; return String(s); }
// arr-from-read.js
function main() { const A = Array.from({ length: 32 }, (_, i) => Array.from({ length: 32 }, (_, j) => (i * 3 + j) % 11)); let s = 0; for (let r = 0; r < 100; r++) for (let i = 0; i < 32; i++) for (let k = 0; k < 32; k++) s += A[i][k]; return String(s); }
// obj-numkey.js
function main() { const acc = {}; let t = 0; for (let i = 0; i < 100000; i++) { const g = i % 17; (acc[g] ||= { n: 0 }).n += 1; t += acc[g].n; } return String(t); }
// obj-strkey-same.js
function main() { const acc = {}; const keys = []; for (let g = 0; g < 17; g++) keys.push('k' + g); let t = 0; for (let i = 0; i < 100000; i++) { const g = keys[i % 17]; (acc[g] ||= { n: 0 }).n += 1; t += acc[g].n; } return String(t); }
// flatmap.js
function main() { const data = []; for (let i = 0; i < 10000; i++) data.push({ tags: ['a', 'b', 'c'].slice(0, i % 4) }); let t = 0; for (let r = 0; r < 3; r++) t += data.flatMap(d => d.tags).length; return String(t); }
// push-grow.js
function main() { let t = 0; for (let r = 0; r < 20; r++) { const a = []; for (let i = 0; i < 10000; i++) a.push(i); t += a.length; } return String(t); }
// spread-arr.js
function main() { let items = []; let t = 0; for (let i = 0; i < 1500; i++) { items = [...items, { id: i }]; t += items.length; } return String(t); }
// spread-obj.js
function main() { let s = { a: 1, b: 2, c: 3, d: 4, e: 5, f: 6, g: 7, h: 8, count: 0 }; for (let i = 0; i < 100000; i++) s = { ...s, count: s.count + 1 }; return String(s.count + s.h); }
// sort-num.js
function main() { const n = 20000, a = new Array(n); let x = 2463534242; for (let i = 0; i < n; i++) { x ^= x << 13; x ^= x >>> 17; x ^= x << 5; x >>>= 0; a[i] = x % 1000000; } const b = a.slice().sort((p, q) => p - q); return b[0] + ':' + b[n - 1]; }
// math-min.js
function main() { let t = 0; for (let i = 0; i < 100000; i++) t += Math.min(i, 50000, t % 7); return String(t); }
// math-min-hoisted.js
function main() { const min = Math.min; let t = 0; for (let i = 0; i < 100000; i++) t += min(i, 50000, t % 7); return String(t); }
// charcode.js
function main() { let s = ''; for (let i = 0; i < 220; i++) s += String.fromCharCode(97 + i % 7); let t = 0; for (let r = 0; r < 500; r++) for (let i = 0; i < 220; i++) t += s.charCodeAt(i); return String(t); }
// regex-literal.js
function isDate(v) { return /^\d{4}-\d\d-\d\dT/.test(v); } function main() { let t = 0; for (let i = 0; i < 10000; i++) t += isDate(i % 2 ? '2024-01-02T' : 'nope') ? 1 : 0; return String(t); }
// json-big.js
function main() { const users = []; for (let i = 0; i < 400; i++) users.push({ id: i, name: 'User ' + i, email: `u${i}@ex.com`, score: i * 1.37, active: i % 2 === 0, addr: { zip: String(10000 + i), geo: [i / 10, -i / 10] } }); let t = 0; for (let r = 0; r < 5; r++) { const s = JSON.stringify(users); t += s.length + JSON.parse(s).length; } return String(t); }
// new-date.js
function main() { let t = 0; for (let i = 0; i < 2000; i++) { const d = new Date(1700000000000 + i * 1000); t += d.toISOString().length + new Date(d.toISOString()).getTime() % 7; } return String(t); }
```

## 附录 B：本轮使用的命令

```bash
bench/cross/run.sh                                   # 三引擎对比（两轮，report.py 合并取优）
python3 bench/cross/make_report.py artifacts/cross/summary.json d3.min.js artifacts/cross/report.html
dotnet Jaunt.Benchmarks.dll --run-loop <file.js> main   # 8 s 热循环：µs/run、gc0/gc1/gc2、暂停总时长
dotnet-trace collect -- dotnet Jaunt.Benchmarks.dll --run-loop <file.js> main
dotnet-trace report <trace>.nettrace topN -n 30 [--inclusive]
DOTNET_GCgen0size=0x4000000 DOTNET_gcConcurrent=0 dotnet Jaunt.Benchmarks.dll --run-loop ...
METRIC=cpu python3 bench/cross/report.py ...            # 以 CPU 时间交叉核对
```

## 8. 实施记录（2026-11-07）

已落地：K1、K3（flat/flatMap、lastIndexOf、copyWithin、toReversed/with/toSpliced 的 dense 路径；回调内建直接调用闭包）、T1、O1（站点 IC + Vm 级 (shape, key) 直接映射缓存，后者覆盖同一站点轮换多个键的情形）、A1、A2、A3、C1（含 `JAUNT_POISON_REGISTERS=1` 毒值模式）、C2（`PushCallFrame`：从调用者寄存器窗口直接取 this 与参数）、C4。未做：K2（PropertyKey 索引形态）、C3、C5、A4、A5、D1–D3、4.7 文档。

验证：单测 578/578（普通与毒值模式）；test262 固定 revision 100,167/100,169（仅 2 个已知 Apple ICU 数据差异，0 跳过）；`eng/verify-aot.sh` net8/net10 全部 PASS；`FastPathSemanticsTests` 新增 20 个对照用例（数字键、spread 预留、排序的稳定性/undefined/洞/不一致与自改比较器/非闭包比较器/重入/抛错、正则字面量的新对象与原型替换、越界与 length-tracking 的 typed array 视图），期望值由 Node 生成。过程中发现并修复：T1 首版对 length-tracking 视图越界写 backing array（test262 进程 AccessViolation）——现在做整视图范围检查，length-tracking 视图按当前 buffer 长度计算。

实测（`bench/cross`，两轮取优；完整表在 `docs/benchmarks.md`）：matmul −40%（MoeJS/Jaunt 0.58→0.91）、higher-order −19%（0.54→0.66）、graph-algos −12%、fib −11%、sha256 −10%、game-of-life −7%、sieve −6.5%、sort-compare −6%；分配：spread-arr 53→18 MB、ta-rmw 2 MB→37 KB、flatmap −33%、sort −22%、state-reducer −37%。Jaunt 自身几何均值 −2.3%；**相对 MoeJS 的几何均值不变（0.974→0.976）**，因为同一时段 MoeJS 的测量也快了 2.3%（机器状态），未改动项在 ±4% 内波动。§1 的目标 1（几何均值 ≥ 1.0）未达成；目标 2（每次调用 ≤ 16 ns）未达成（现 21.7 ns）。

与 §3 的估计相比：K1/T1 对 matmul 的收益高于估计；C1 的收益（fib −11%）与实验一致；C2 约 −3%；O1 对 obj-numkey 为 −38%（估计约 −55%），因为 (shape, key) 缓存命中仍需一次哈希与三次比较；A2/C4 对排序只有 −6%，确认排序成本就是比较器调用本身。下一步按 §6 的阶段 B/C：C3、D1/D2（dispatch）、A4/A5（JSON/LOH）、K2。

遗留的既有差异（本轮发现、未改）：`o[k] op= v` 的键转换只做一次，而现行规范在读和写各转换一次（V8 调用两次 `toString`）；已记入 `docs/FEATURES.md`。

## 9. 第二轮实施记录（2026-11-07，c0f2631 之后）

用临时计数器聚合每次 `GetPropSlow` 的接收者与 IC 状态，找到三类系统性漏洞并修复：**访问器属性从不进 IC**（typed array `length`、`Map.size`、类 getter：每次完整查找 + 字符串哈希）→ `OwnGetter`/`ProtoGetter` 缓存槽位；**字符串接收者的方法在 `GetProp` 路径永远 miss**（`StringProtoData` 只有 `CallMethod` 认）；**属性不存在不缓存** → `NotFound` 种类（受 ProtoEpoch 保护）；另加 **megamorphic 站点的 Vm 级 (shape, name) 二级缓存**、**字符串 for-of 直接按码点迭代**（pristine 守卫）、**JsRegExp 只持有共享编译产物的一个引用**。尝试并回退：D1（无边界检查 dispatch）无可测收益。

实测（两轮取优，MoeJS 本轮测量与上轮一致 ×0.999）：huffman-lz −40%、crc-base64 −27%、spectral-norm −27%、unicode-text −26%、levenshtein −22%、classes −6%、json-roundtrip −4%；**MoeJS/Jaunt 几何均值 0.976 → 1.013**，§1 目标 1 达成；目标 2（每次调用 ≤ 16 ns）未达成（仍约 21 ns），fib/sort-compare/higher-order/async 仍落后 1.3–1.6 倍。

验证：单测 578/578（两种模式）、13 个新对照用例、test262 100,167/100,169、AOT 门禁未变。

## 10. 第三轮实施记录：调用路径（2026-11-07，581ad31 之后）

目标是 §4.1 的 C3 与 C5。方法：先用 `--run-loop` 建立四个调用密集微基准的基线（fib(24)、纯函数调用 call-js、方法调用 method、双参数多态调用 poly），再用 `DOTNET_JitDisasm` 看 `PushCallFrame` 的 Tier1 汇编逐项削减，每项改动单独测量。

**C3（调用点 IC）**：`JsClosure.IsClassConstructor` 只是模板标志的副本，把它并入 `InlineCallMask`（`Generator | Async | ClassConstructor`）后，调用点检查只剩类型判断和一次模板标志读取；再加一个缓存模板的 IC 不会少任何一次加载。实测折叠本身在噪声内（±1%），与 §4.1 的估计（1–2 ns）一致：C3 在当前 dispatch 结构下没有独立价值，不再单列。

**C5（帧结构化）评估**：先做了改动面最小、能单独量化其主要收益的变体，而不是把 `Frame` 改成 struct。`Frame.Caller` 链和 `Vm.Current` 字段去掉，帧栈改为按深度索引的数组 `_frames[Sp]`（池化帧就放在它的深度槽里；协程堆帧挂入时顶替该槽、挂出时归还，见 `Frame.Displaced`），`Current` 变成 `_frames[Sp-1]`。这样每次调用/返回少三次引用写入（即三次 GC 写屏障）。实测 fib −3%、call-js −3%：写屏障只解释了每次调用约 1 ns，不是主因；全量 struct 化只能再省一次池数组间接寻址，不值得其改动面，**C5 结论为不做**。期间发现并修正：`EnterNative` 也递增 `Depth`，所以 `Depth` 不能当帧栈下标——现在 `Sp` 是帧数，`_nativeDepth` 是原生嵌套层数，`Depth = Sp + _nativeDepth` 仍是预算口径。

**真正的大头是参数拷贝**（汇编里每个参数约 20 条指令加一次写屏障调用）。新增**重叠寄存器窗口**：`Call`/`CallMethod`/`New` 的被调用帧的寄存器窗口直接从调用者的 argStart 开始（`Vm.BindCallWindow`），参数原地就是被调用者的参数寄存器，不拷贝。前提是调用者 argStart 以上的寄存器在调用点都是死的：CodeGen 的寄存器分配是严格栈式的（`Temp`/`ResetTo`），`Arguments()` 把参数放在当前分配顶端，所以普通函数的每个发射点都满足；`yield*`/`for await` 的手写发射点在 received 之上有存活寄存器，但它们只出现在协程函数里，而协程帧拥有私有寄存器数组（`callerRegs != _chunk`），和"被调用者放不进当前 chunk"的情形一样走原来的拷贝路径。帧记录 `SavedTop`/`SavedChunkIndex`，弹出时精确恢复调用者的栈顶（之前是 `_top = f.Base`，重叠后不再成立）。**验证**：毒值模式扩展为在重叠帧弹出时把调用者 argStart 到原栈顶之间全部填毒值，之后调用者任何读取都会暴露；单测与 test262（100,167/100,169）在毒值模式下全部通过。

其余削减：`PushCallFrame` 专用的 `SetupCallFrame`，`this` 不再经过 16 字节栈上临时量（无接收者的 sloppy 调用直接比较全局对象引用，原始值接收者的转换放到不内联的冷路径）；`Frame.K` 缓存常量池引用，调用/返回少一次依赖加载；`PopFrame` 允许内联进 `RunLoop`。

微基准（µs/run，8 s 热循环，每步相对前一步；括号为相对基线累计）：

| 步骤 | fib(24) | call-js | method | poly | newobj |
|---|---:|---:|---:|---:|---:|
| 基线 581ad31 | 3498 | 2160 | 2976 | 14247 | — |
| C3 折叠 | 3525 | 2133 | 3025 | 14278 | — |
| 深度索引帧栈（C5 评估） | 3387 (−3%) | 2099 (−3%) | 2936 (−1%) | 14025 (−2%) | — |
| 重叠窗口 | 3300 (−6%) | 1937 (−10%) | 2858 (−4%) | 10510 (−26%) | 5335 |
| this/K/PopFrame 内联 | 3014 (−14%) | 1779 (−18%) | 2828 (−5%) | 10273 (−28%) | — |
| `new` 也用重叠窗口 | 2862 (−18%) | 1704 (−21%) | ~2850 (−4%) | 9977 (−30%) | 4990 (−6.5%) |

`PushCallFrame` 的 Tier1 快路径从约 140 条指令降到 116 条且不再含参数拷贝；每次 JS→JS 调用约 21.7 → 17 ns（fib 口径），进入 §1 目标 2 的区间（MoeJS 16–19 ns）。剩余成本：方法 prologue/epilogue（10 个被调用者保存寄存器，约 24 条）、返回值写入的写屏障、`_ticks` 预算计数。

## 11. 第四轮实施记录：A4/A5、Map 字符串键、GC 指引（2026-11-08，9782dce 之后）

先用 `dotnet-trace` 对 map-set、json-roundtrip、higher-order 和一个字符串键 Map 微基准采样，再按证据改。采样的第一个结论是这四个负载的瓶颈不在设计稿假设的位置：**GC 暂停占墙钟 20–26%**（`--run-loop` 的 pause 计数：map-set 2.06 s / 8 s，json-roundtrip 2.10 s，higher-order 1.57 s），每次运行都触发 gen2——8000 项的 Map/Set 和 10000 项数组的倍增链都是 ≥85 KB 的大对象堆分配，LOH 预算一满就是一次 gen2。

**Map/Set（`OrderedTable`）**：`Dictionary<JsValue,int>` 索引换成内联的链式哈希索引（`_hashes`/`_next`/`_buckets` 三个 int 数组），哈希只算一次，数字/字符串/对象各有快路径，Set 不再存值数组；每项约 70 → 40 字节，大集合的 LOH 数组更少更小。第一版数字键退化 6.6 倍：`double.GetHashCode()` 对整数值只在高位不同，按低位取桶全部撞链，而 `Dictionary` 的素数取模掩盖了这一点——现在数字和对象标识哈希过一遍 MurmurHash3 finalizer，字符串仍用运行时随机化的 Marvin 哈希（攻击者选的键无法制造碰撞）。map-str 微基准 4382 → 2777 µs（−37%，含下面的拼接优化），map-num 持平（3350 µs：每次 `has`/`get` 约 20 ns，瓶颈是 CallMethod + 内建调用而不是查表），map-set −6%。

**A4（JSON）**：采样显示 json-roundtrip 的大头不是 stringify（14%）而是带 reviver 的 `JSON.parse`（`Internalize` 56%）：每个属性新建一个 context 对象并走 shape 转换、每个对象 `OwnPropertyKeys` 列表 + 逐键 `GetOwnProperty`、每次调用新建参数数组，以及 source-text-access 记录对每个对象建一个 `Dictionary`。现在 context 用共享的单槽 shape（`Shape.SharedJsonContextShape`），shape 模式对象直接遍历布局取键，参数数组复用，记录的条目改为按解析顺序的列表并用游标匹配（键重复时才退回扫描；是否重复由解析完的属性数与条目数之差判定）。json-roundtrip 9619 → 8063 µs（−16%）。设计稿里的输出分块写入没有做：序列化器已有线程缓存的 `StringBuilder`，采样里 `Quote`/`Append` 合计不到 5%。

**A5（结果数组）**：filter/flat/flatMap 的结果不再从空数组倍增，而是收集进 Vm 的 scratch 缓冲（与排序共用 `TakeScratch`），结束时一次分配精确大小的元素数组：倍增链约分配最终大小的两倍，大结果的每一段都是 LOH；只在 `ArraySpeciesCreate` 走内建 Array 路径时启用（用户 species 构造的结果可能被用户持有，必须边产生边写入）。higher-order 10388 → 9622 µs（−7%）。

**字符串 + 整数**：`'k' + i` 之前先把整数格式化成一个字符串再拼接；现在 `Operations.Add` 对平坦字符串与 32 位整数直接按最终长度一次分配、原地写入数字（`-0` 仍打印 "0"，超出 int 或非整数走原路径）。

**GC 指引（4.7，宿主级，不是引擎默认）**：同一台机器、同一构建，`--run-loop` 8 s：

| 配置 | map-set | json-roundtrip | higher-order |
|---|---:|---:|---:|
| 默认（Workstation 并发 GC） | 3261 µs，pause 1943 ms | 8282 µs，pause 1997 ms | 9449 µs，pause 1526 ms |
| `DOTNET_GCgen0size=0x4000000`（64 MB） | 2441（−25%） | 7153（−14%） | 11012（+17%） |
| `DOTNET_gcConcurrent=0` | 2530（−22%） | 7275（−12%） | 8904（−6%） |
| 两者 | 2510（−23%） | 7275（−12%） | 9112（−4%） |
| `DOTNET_GCLOHThreshold=0x100000`（1 MB） | 2496（−23%，gen2 491 → 1） | 7880（−5%） | 9175（−3%，gen2 507 → 0） |

结论：分配密集的宿主可以考虑提高 LOH 阈值到 1 MB（几乎消灭 gen2，对其它负载没有副作用）或关闭并发 GC；增大 gen0 对对象多但存活短的负载有利、对 higher-order 这类反而更慢，不能一概而论。`bench/cross` 继续用默认配置，报告里披露。

三引擎对比（两轮取优，MoeJS ×1.001）：state-reducer −19%、map-set −19%、json-roundtrip −18%、concat-build −7%、dynamic-props −7%；**MoeJS/Jaunt 几何均值 1.028 → 1.049**，最快项数 Jaunt 31 / MoeJS 26 / Jint 2。higher-order 在对比里显示 +8%，但它是 GC 主导的负载，同一构建连续两次 `--run-loop` 相差 30%；交替运行各三次的受控 A/B 为带 scratch 收集 9.8–10.0 ms、不带 10.6–11.0 ms（−7%），以 A/B 为准。

验证：单测 578/578（两种模式）与 17 个新对照用例（Map 表的迭代中删除/插入、压缩、-0/NaN、绳子键、clear、两万项表；reviver 的重复键、`context.source`、holder 变更、索引键顺序、嵌套数组；字符串拼接边界；结果收集的 species、重入、抛错、大结果）、test262 100,167/100,169（两种模式）。

## 12. 第五轮实施记录：数组解构、私有字段、`**`，以及两个否定结论（2026-11-08，7dcac93 之后）

本轮先重跑三引擎对比确认现状（本机 Apple M4，SDK 10.0.300 / 运行时 10.0.8，MoeJS 同一台机器同一时段）：**MoeJS/Jaunt 几何均值 1.42**，59 项里只有 float-loop（0.98）落后，higher-order 持平（1.03）。§1 的目标全部达成，所以本轮不再按“落后项”找问题，而是用隔离微基准找绝对开销大的语言构造。`dotnet-trace` 采样在本机仍被 `PollGCWorker` 严重失真（state-reducer 97%），只用它定位到 higher-order 的回调机制占比高，其余结论都来自微基准 A/B（`--run-loop`，同一构建、交替运行）。

**做了的（三项，都有 Node 生成期望值的对照测试 `ArrayPatternsPrivateFieldsAndExponentMatchReferenceBehavior`）：**

| 改动 | 微基准（µs/run） | 说明 |
|---|---:|---|
| **数组模式一条指令**：`const [v, w] = x` / `for (const [k, v] of m)` 的目标全是局部寄存器或环境槽时，CodeGen 发一条 `Op.DestructArray src, dst, n`，替代 GetIterator + n×IterNextOrUndef + IterClose（四次 RareOp 与一个 IteratorRecord 分配）。快路径：原型链未改动的稠密数组直接从元素存储读 n 个值；任何洞、分页/队列存储、非数组都回退到完整的迭代器协议（next 抛出不 close，读完 close）。只有当两次 next() 之间不可能运行用户代码时才融合：元素必须是省略或标识符，绑定是寄存器/环境槽（排除脚本级、with/eval 动态、import；赋值模式下再排除 const/函数名/需要 TDZ 检查的目标，它们会在两步之间抛错） | destructure 3263 → 650（按下标读同一数据 632）；destructure-decl 3115 → 712 | graph-algos −6%（AOT −9%，分配 5.5 → 3.4 MB）、map-set −8%、astar −8.5%、dynamic-props −13% |
| **私有字段进主循环**：`PrivateGet`/`PrivateSet` 在 RunLoop 主 switch 中，命中“对象有该字段”的常见情形走不内联的小帮助函数（线性扫描 `PrivateElements`），方法/访问器/错误仍走原慢路径；`PrivateDefine` 也直接从主循环调用 | class-base-fields 4152 → 3631（−12.5%）；class-new 4481 → 3965 | classes-inheritance −9.6% |
| **`**` 数字快路径**：`Op.Exp` 进主 switch，两个数字直接 `MathBuiltins.Pow`（已有 `b == 2 → a*a`），其余走 `ArithSlow`（BigInt / ToNumeric） | pow 10943 → 4769（纯乘法等价写法 3876） | classes-inheritance 含 `r ** 2`，其 −9.6% 可能部分来自此项。bigint 同一轮 −19%，但其 `**` 全是 BigInt，走不到这条快路径，该变化原因未查明 |

三引擎对比（同一 MoeJS 数据）：Jaunt 自身几何均值 ×0.986；**MoeJS/Jaunt 1.418 → 1.438**；未改动项在 ±3% 内（intl-format +3.8% 为噪声，与本轮改动无关）。

**评估后不做的（有测量）：**

1. **解释器驱动的回调内建（替代 CallCallback → Run → 嵌套 RunLoop）**。完整实现过：forEach/map/filter/some/every/find/findIndex/reduce 的循环由 `FrameFlags.Driver` 帧在 RunLoop 内推进，回调帧返回时走驱动而不是嵌套循环；语义对照（可变数组、提前退出、species、bound/native/生成器回调、异常、`error.stack`）与 Node 全部一致。结果：JIT 下 cb-foreach 1345 vs 走原内建路径（`Array.prototype.forEach.call`）1320，cb-map 1533 vs 1511；NativeAOT 下 1650 vs 1597，1786 vs 1788——**嵌套 RunLoop 入口在当前代码上已经不是成本**（第三轮的 `_onThread`/栈探测缓存之后），§“NativeAOT vs JIT”里的 +12 ns/次重入在本轮构建上测不到。已回退，不留代码。副产品：发现池化帧的不变式“Callee 相同则 Feedback 相同”必须由任何自定义帧种类维护（驱动帧曾因借用调用者的 Callee 而让后续同闭包的调用拿到空代码）。
2. **多态内联缓存（2–4 个 shape）**：mono-method 2965 vs poly-method（3 个类交替）3111，每次多态调用只多 1.6 ns——megamorphic 的 Vm 级 (shape, name) 缓存已经足够便宜。
3. **`instanceof` 缓存**：每次约 20 ns（含 `Symbol.hasInstance` 查找与原型链遍历），classes-inheritance 里 3000 次 ≈ 0.7%。
4. **Math 一元内建的委托链**：float-onecall − float-nocall ≈ 5 ns/次调用，float-loop 的时间在 `Math.sin` 本身；把 `Math` 提升为局部变量只省 GetGlobal 的 4 ns。
5. 类构造路径剩余开销（`extends` + `super()` 约 40 ns/对象：`GetPrototypeFromConstructor` 的 `Get("prototype")`、SuperBind RareOp、ConstructReturnValue）与 `FieldInit.Call` 的嵌套 Run：按 1 的结论后者无收益；前者每项 3–5 ns 是估计值，未单独测量，未做。

验证：单测 622/622；test262 100,167/100,169（同两处 Apple ICU 数据差异，0 跳过）；NativeAOT（ILCompiler 10.0.14）`verify` 全部 PASS，AOT 下 58/59 工作负载结果与 Node 一致（unicode-text 为既有的 InvariantGlobalization 差异）。
