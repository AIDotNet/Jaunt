# Jaunt 架构设计

> 状态：推荐方案，2026-11-03；尚未实施。来源与竞品事实见 [调研](01-research.md)，技术选型必须通过 [P0 验证](06-roadmap.md)。（2026-11-06 更新：已实施为 `0.1.0-alpha.1`，实测与偏差见 [07-implementation-status.md](07-implementation-status.md)；本文保留为设计记录。）

## 1. 不应被优化破坏的约束

1. 编译结果可以共享；全局对象、闭包、模块实例、可变内建和执行反馈不能随之共享。
2. 单个 `JsEngine` 同一时刻只有一个逻辑执行者，多引擎才是并行单位。
3. 所有托管引用必须对 CLR GC 可见；不把移动对象地址藏进整数或 NaN payload。
4. 方便入口与高性能入口使用相同语义、限额和异常机制。
5. 普通 JavaScript 异常不等于引擎损坏；取消/资源终止不等于事务回滚。
6. 所有“可能执行用户代码”的路径都受预算约束，包括 getter、Proxy、类型强制转换、`toJSON` 和结果转换。
7. 输入、输出、缓存、jobs、I/O 都有所有权和容量边界。
8. **NativeAOT 是用户确认的强制要求**：每阶段正式承诺的运行时能力都必须有完整 AOT 路径，且发布后仍可编译/执行新 JS；不依赖运行时发码，不在 AOT 下删减功能。

## 2. 分层与发布单元

```text
Jaunt.Hosting（后续可选）
  插件契约、隔离租约、池、DI、端到端输入输出
         │
Jaunt 公共 API
  JsCompiler / JsEngine / JsExecution / JsValue
  JsScript / JsModule / JsModuleInstance / JsFunction
         │
  ┌───────────────┬──────────────────┬─────────────────┐
  │ 前端与编译     │ 运行时            │ 宿主边界         │
  │ Parser adapter│ Realm / VM frames│ Typed bindings  │
  │ Binder / IR   │ Objects / shapes │ DTO / JSON      │
  │ Bytecode      │ Inline caches    │ Module resolver │
  │ Verifier      │ Jobs / budgets   │ Diagnostics     │
  └───────────────┴──────────────────┴─────────────────┘
         │
   .NET 8 基线 BCL + 内部版本优化适配

Jaunt.Generators：构建期 DTO/宿主绑定生成
Jaunt.Reflection：仅评估可通过 AOT/trim 验证的受控适配
Jaunt.Web：后续网络、timer、流等可选能力，同样必须 AOT 兼容
构建期 JS 预编译：后续可选优化，不代替运行时 JS 编译
```

这是逻辑边界，不要求首日建立大量程序集。建议起步仅有核心、测试和 benchmark 工程，按真实依赖再拆包。公共 API 不暴露第三方 parser AST 或内部 opcode/shape 类型。

AOT 要求覆盖 parser/compiler/VM、标准库、ESM、Promise、绑定、DTO/JSON、Hosting，以及将来正式发布的运行时扩展和依赖。拆成可选包不能豁免；构建期工具本身不必 NativeAOT，但生成到应用中的代码必须兼容。`Jaunt.Jit` / 运行时 JS→IL 不纳入当前交付主路线。

## 3. 编译管线

```text
源码 + 来源名 + 编译配置 + 编译预算
    → parser
    → 内部 AST 适配 / 绑定与作用域分析
    → 带语义信息的 IR
    → 字节码 + 常量描述 + 异常表 + 源位置映射
    → 结构验证
    → 不可变 JsScript / JsModule
```

### 3.1 解析器决策

优先验证 **Acornima**，而不是同时重写全套 JS 语法和 VM。

- 使用成熟语法覆盖，但编译后释放 AST 引用；不得让每个执行实例各持一份 AST。
- 建立内部适配层，公共 `JsCompileOptions`、diagnostics 与 parser 类型解耦。
- 不承诺零分配解析，也不承诺最初的冷编译一定超过成熟引擎。
- 必须验证恶意嵌套、长 token、AST 节点数、取消和源码保留行为，并在真实 NativeAOT 发布产物中完成新源码的完整编译/执行；parser 的 AOT 标记不能代替集成验收。
- **超时返回一个 `Task` 不等于停止底层解析。** 如果 parser 无法满足预算，需增加可维护的中断能力、改变实现或将不可信编译放入进程边界，不能带着已失效的线程继续无限解析。
- 许可为 BSD-3-Clause，并涉及 NOTICE；在实施依赖选型时复核。

Parser 接受某种语法不代表 Jaunt 已实现该语义。未支持特性必须在编译阶段返回明确诊断，不能静默降级。

### 3.2 必须在 IR 中体现的语义

- 词法作用域、`var`/`let`/`const`、TDZ、捕获变量与每次循环的词法环境。
- `this`、严格/非严格调用、参数与 `arguments` 的差别。
- 引用求值和赋值顺序、短路、可选链、属性 getter/setter。
- `return` / `break` / `continue` / `throw` 经过 `finally` 时的 completion 语义。
- generator/async 的恢复位置、环境、异常处理状态；后续不能临时依赖 CLR 栈保存它们。
- 支持 direct `eval` / `with` 的函数进入保守环境路径，不错误地将所有绑定固定成普通局部槽位。

语义 helpers 由解释器、其专门化快路径及后续构建期生成代码共用，但不为每个简单数字加法引入虚调用；不以运行时 IL 后端作为正确性或性能前提。

### 3.3 字节码建议

- 首选寄存器 VM：局部与临时值用寄存器窗口；被闭包捕获的绑定用独立 cell/environment。
- 原型比较固定主编码字加扩展操作数的形式；不锁死“每条指令必为四字节”或每函数只能 256 个寄存器。
- 编译时计算寄存器、临时空间与异常表范围；加载时验证索引、跳转目标、处理器边界和资源上限。
- 使用显式 VM 帧栈，避免 JS 深递归直接耗尽 CLR 调用栈。
- JS 正常 `throw` 用显式 completion/异常表处理；CLR 异常保留给宿主边界与运行时故障，是否将其用于非热异常路径需实测。
- 热路径允许专门化 opcode、常量折叠、稳定属性访问和小型多态缓存；所有快速路径均有正确慢路径。

### 3.4 编译结果共享与缓存

| 可以共享 | 不得放入共享编译结果 |
| --- | --- |
| 字节码、原始常量描述、字符串、来源映射 | 引擎的 JS object/function/RegExp 实例 |
| 函数模板、作用域描述、导入导出描述 | 闭包 cell、模块 live binding 当前值 |
| 不捕获 Realm 的内建实现与只读元数据 | 全局对象、宿主服务实例、delegate 捕获的请求对象 |
| 经过证明的只读布局描述 | IC 反馈、prototype 对象指针、断点补丁 |

- `JsScript` / `JsModule` 逻辑不可变并可被多引擎并发使用；公开接口不提供可写底层数组。
- 模块的编译产物不是已经加载、求值完成的模块图。
- 缓存由宿主显式持有，限制字节数、条目数、并发编译数与逐租户占用，支持淘汰。
- cache key 至少包含源内容摘要、编译器/字节码版本、语法/严格模式配置；来源映射、模块身份及权限相关缓存另含对应域，不能只按文件名缓存。
- 禁止无界进程全局 `string.Intern`、动态 `eval` 编译缓存或无限 shape 缓存。
- 首版仅承诺本进程可信编译器产生的内存产物，不开放任意字节构造/反序列化；加载时校验语义配置、版本与执行 policy。源码 hash 是身份，不是来源认证。
- 在 Standard 下编译不能绕过 Hardened 的运行时限制。将来开放持久化字节码时，需要独立格式与威胁模型，签名不能替代结构验证。

## 4. 执行实例与对象模型

### 4.1 对外一个引擎，对内明确 Realm

`JsEngine` 在第一阶段拥有一个默认 Realm、VM 工作区、module instance map、job queue、限额与宿主注册。暂不暴露多 Realm 共享活对象的复杂 API。

- 引擎可以跨线程**顺序**使用，不要求固定 OS 线程。
- 并发进入立即失败，不隐式锁住整个脚本来制造“线程安全”的假象。
- `InvokeAsync` / `EvaluateAsync` 等待期间仍保留逻辑独占；其他调用不得进入同一引擎。
- 宿主回调中重入同一个引擎默认禁止；以后如开放，必须共享根预算、明确栈/异常规则。
- 外部线程只可完成受控投递、取消或发出中断请求，不能直接修改 JS 对象。

### 4.2 Value 表示

推荐先实现普通 GC 安全 tagged struct，再测试压缩方案：

- number/boolean/null/undefined 等不为每个值创建对象。
- 引用保存在独立的托管引用字段，number/tag 只在非引用存储中编码。
- **`default(JsValue)` 定义为 `undefined`**，不是 `+0`；避免未初始化槽位误变成数字。
- TDZ、array hole 等内部 sentinel 不暴露为普通 `undefined`。
- 常规 tag + 数值 payload + object 引用可能为 24B；16B 表示是实验目标，不是首版 ABI 承诺。
- 禁止重叠托管引用与整数字段、用裸地址逃避 GC 跟踪、或假定包含引用的 `JsValue` 可以 `stackalloc`。
- 字符串遵守 JS 的 UTF-16 code unit 语义；UTF-8 输入优化不能改变索引、长度、代理项和正则语义。

含状态的 value、function、module instance 携带可验证的引擎/代际归属；不仅检查函数而遗漏普通对象或 Proxy。跨引擎优先通过 DTO/JSON，未来 structured clone 另行定义。

### 4.3 Object / shape / IC

- 常见对象使用 `shape + slot storage`；属性名 atom 化，动态属性过多时转 dictionary。
- 共享的是不含 Realm 引用的布局元数据；prototype 活对象及可变 transition/反馈默认私有。
- IC 初期从单态开始，必要时有限多态，最后退化到通用查找；控制每调用点缓存大小。
- 缓存 guard 至少覆盖对象布局、原型链变化、属性 descriptor/accessor 类型、数组表示变化和宿主绑定版本。
- `Object.defineProperty`、删除属性、变更原型、可变 builtin、Proxy 等必须触发失效或正确慢路径。
- IC 缓存位置而非无条件缓存当前值；属性写入不能返回过期结果。
- dense/sparse array 分开处理，保留 hole 与 `undefined`、length、副作用和枚举顺序。
- pooled slot/register 数组离开使用范围时清理真实脏区；大容量数组按保留阈值丢弃，避免一次大请求长期污染池。

### 4.4 标准库的高风险边界

`RegExp` 不是简单包装 .NET Regex：字符集、Unicode、标志、`lastIndex`、匹配/替换语义与可中断性要单独验证。`Date`/时区、数字格式化、Intl 也不能直接把 BCL 行为当 ECMAScript 保证。

对未完成语义列出支持范围，不以“parser 能解析”或“调用 BCL 成功”作为合规依据。

## 5. Profile 与隔离

| Profile | JS 语义与能力 | 可共享的内容 |
| --- | --- | --- |
| `Standard`，默认 | 独立 globals 与可变 intrinsics；实现范围内保留标准语言行为；无隐式 CLR/文件/网络权限 | 代码、纯元数据、内建实现 |
| `Hardened`，显式选择 | 冻结内建、禁用动态代码生成等额外约束；这是可见语义差异 | 先共享元数据；共享真实 JS builtin 对象需另行证明安全 |

冻结对象不自动等于可跨 Realm 共享：函数捕获的 realm、prototype、constructor、异常对象、符号身份和返回对象归属均需处理。首版不以共享可观察 JS 对象换取更好看的内存数字。

禁用动态代码必须覆盖 `eval`、各种 Function/async/generator 构造器、`.constructor` 和已有别名等实际可达路径，不能只删除几个 global 属性。

这里指 Hardened 的 **JS 字符串代码生成政策**，不是 NativeAOT 对宿主 IL 的限制。Standard 中已经实现的 eval/Function 仍可在 AOT 中生成 Jaunt 字节码；AOT 也不能成为禁止宿主提交新脚本的理由。

两种 profile 都不是进程沙箱。高威胁脚本需要外部进程/OS 资源控制，且宿主提供的能力本身必须可信。

## 6. 互操作与数据通道

### 6.1 四类边界分别命名

1. **值构造**：`JsValue.From(...)`，严格的基础类型映射。
2. **数据快照/DTO 映射**：`FromObject` / `ToObject` 配合 `JsTypeInfo<T>`，不默认暴露 CLR 对象行为。
3. **JSON 通道**：`ParseJson` / `ToJsonUtf8` / `WriteJson`，输出遵守约定的 JSON 序列化语义。
4. **宿主能力暴露**：`SetFunction` / `Expose`，明确授权的方法与成员，不自动开放反射、类型对象、构造器或 `GetType` 链。

DTO 映射不声称等价 `JSON.stringify`：首版默认 data-only，只读取约定的自有数据属性，拒绝 accessor/Proxy/Promise/函数，不调用 `toJSON`；循环、missing/undefined、数字范围等有独立契约。将来开放可观察访问模式时，必须在同一预算内执行 getter 等副作用。不能把不同导出语义塞进一个没有说明的 `ToObject<T>`。

### 6.2 常用路径

- 基础类型/固定参数委托：使用静态强类型适配器，注册时也不依赖动态发码；调用时不 `DynamicInvoke`，普通重载即可在 AOT 中使用。
- 自定义 DTO、服务与异步绑定：源生成或显式 descriptor，覆盖实际闭合泛型；未知类型/签名明确诊断，不在生成或手写路径中静默反射/发码回退。
- 可选反射适配：仅针对静态保留元数据的已知类型核验，缓存有界；无法通过 AOT/trim 全链验证则不纳入当前正式运行时交付。AOT 不等于所有反射均不可用。
- raw callback 使用临时 `ReadOnlySpan<JsValue>` 与 `ref struct` 上下文；不得保存到异步任务。
- 异步宿主绑定只接收脱离的原始值/DTO 或自有 buffer，不能带可操作的 JS 活对象；首次让出执行前完成参数快照。`ReadOnlyMemory` 只是视图，不是不可变所有权。
- 回调完成仅投递拥有清楚所有权的 CLR 数据，JS 值构造/转换回到执行器；不在任务 continuation 上直接执行 JS。

### 6.3 JSON 与大输入

默认 `ParseJson(ReadOnlySpan<byte>)` 取得自己的数据所有权，调用返回后宿主可以归还输入池。不可因为参数是 `ReadOnlyMemory<byte>` 就宣称底层不可变。

优化顺序：

1. 正确的 eager JSON 基线与直接 DTO 映射。
2. 验证完整文本的合法性，但只在需要时物化 JS 容器/字符串；“懒”不允许忽略未访问区域的非法 JSON。
3. 以 copy-on-write 或等价机制维护写入、删除、枚举、descriptor、重复键与 `__proto__` 数据属性语义。
4. 只有收益明确时才增加借用/转移所有权 API；名称、失效时间、异步寿命和 buffer 归还规则必须显式。

保留一个小字段可能令整个 8MiB 输入存活。需要测量 retention，并为跨请求保存的片段提供物化/复制策略；“零输入复制”不是“零分配”或“更少保留内存”。

## 7. 根执行预算与终止

### 7.1 一个完整请求的边界

```text
可选编译/模块初始化
  → 输入转换
  → JS 调用与宿主能力调用
  → Promise/jobs
  → DTO/JSON 输出转换
  → 最终 ready-job checkpoint
  → 输出脱离引擎、结束执行
```

`JsExecution` 为低层调用提供显式共同预算；`JsPlugin` 自动使用这一边界。单独调用 `engine.Invoke` 的方便入口只能覆盖那次操作，不能声称已限制随后任意宿主转换。全局/导出查找若能触发用户代码，也属于预算内操作。

scope 持有引擎独占权，内部使用 `execution.*`，禁止 `engine.*` 执行入口悄悄开新 root。`Complete()` 做最终 checkpoint 和未决工作检查；输出在成功后才交付。Dispose/DisposeAsync **不执行 JS**，仅清理、撤销权限和释放所有权；放弃未完成 scope 不能直接交给下一调用。

所有限额可按调用收紧，不得越过引擎/宿主配置上限。编译、模块图扩张与输出操作同样可成为攻击面。

### 7.2 分开计量，不使用含糊的 MemoryLimit 承诺

| 限额 | 含义 |
| --- | --- |
| 编译预算 | 源码长度、嵌套/AST/字节码大小、模块数及解析可取消性 |
| `Timeout` | 完整根执行的截止时间；协作检查而非硬实时抢占 |
| `MaxInstructions` | VM 工作量预算；内建长操作另有分段工作量检查 |
| `MaxStackDepth` | VM 调用/恢复深度，不依赖 CLR 栈溢出来终止 |
| `MaxTrackedBytes` | 引擎按明确规则计入的分配额度，不是 CLR live heap/RSS |
| 转换/输出预算 | 节点数、深度、字符串长度、输出字节数，写入/扩容前检查 |
| queue/I/O 预算 | pending jobs、并发宿主操作、读取的解压后数据量等 |

首版 `MaxTrackedBytes` 可以采用保守的**Realm 生命周期累计计费**，只在已证明安全释放的容器上退费；不靠 GC 时间或一次调用结束把仍存活数据的费用清零。这个模型可能提前拒绝长期分配型会话，必须如实报告，后续再验证可达存量统计。

共享代码/cache 成本由对应所有者另行计费。计费包含哪些对象、容量、外部 buffer 和转换暂存，应有可测试的表；宿主任意分配和 CLR 开销无法全面归属到一个引擎。

### 7.3 检查点与错误状态

- 在循环、函数入口、分配、长字符串/JSON/正则操作、模块加载、job dispatch 检查预算。
- 取消唤醒等待中的调度器；不能只有 VM 正在跑时才看得到取消。
- 不使用 `Thread.Abort`；阻塞宿主方法必须自行响应取消，无法合作时使用进程边界。
- JS 普通 throw 可被 JS catch；取消/预算终止是宿主终止，不可被 JS 捕获后继续无限运行。
- 普通 JS 异常经过安全收尾可继续使用引擎，但已发生的状态变更不回滚。异常传播前完成规定的 checkpoint/未决工作检查，不把运行脚本的收尾藏进 Dispose。
- 初期取消/超限/未结外部工作/内部不变量错误后将引擎标为 Faulted，禁止继续执行并从池中淘汰；不承诺执行脚本 `finally` 完成清理。
- 库可提供 `Try...` 便利方法，但不能在热路径上故意用抛 CLR 异常实现正常分支。

## 8. Promise、异步与模块

### 8.1 不混淆三个身份

| 身份 | 生命周期用途 |
| --- | --- |
| Realm generation | 引擎销毁/新 Realm 后让旧句柄和迟到 completion 失效 |
| 根执行/预算 ID | 一次宿主执行的截止时间、计费与取消注册 |
| 异步 operation ID | 某个 pending 宿主操作/Promise bridge 的完成与取消状态 |

迟到的取消回调不能中断下一次执行；但旧调用创建的普通 JS pending Promise 可以被后续合法 JS 调用解决，不能只因创建 root 已结束就作废。外部 completion 则必须检查自身 operation 与 Realm 的状态后入队。

- 已产生的外部异步工作不能借新 root 重置自己的预算；单纯未结算的 JS Promise 不永久绑定一个已关闭的 root，之后 JS 主动解决它产生的 job 使用当次合法执行预算。
- 根执行退出时注销/同步取消注册，未决外部任务保持可撤销的上下文到完成或取消，不复用当前调用字段冒充它的身份。
- MVP 请求式执行不允许外部 host operation 越过成功关闭边界；未来长期 session 的跨 root 工作需独立契约。FreshRealm 结束会撤销该 Realm 的能力与 completion。

### 8.2 最小调度契约

- 同步 `Evaluate` / `Invoke` 不阻塞等待外部 I/O；**调用异步 host delegate 前拒绝**，不根据 ValueTask 恰巧是否已完成改变行为。返回前排空当前 ready microtasks，计入预算。
- 同步返回值若仍是 pending Promise，保留为 `JsValue`，不 `.Result` 等待、不重新执行源码。`AwaitAsync` 可等待已取得的值，通常优先直接使用 Async 入口。
- `AwaitAsync` 期间仍独占引擎，不能再并行进入同一引擎调用 JS resolver。纯 JS resolver 必须先在合法执行中调用，或在同一 scope 开始等待前调用；没有合法完成来源的 Promise 只能等待至取消/超时。
- 异步入口按 JS await 的 Promise/thenable assimilation 规则处理返回值，自动推进队列；输入、调用、输出与最终 checkpoint 保持在明确的 root 中。
- 只等待当前返回链所需工作，不等待 Realm 中所有普通 Promise。在 **root 最终 checkpoint 后关闭时**，若仍有尚未完成的外部 host operation，MVP 报 `UnjoinedAsyncWork`、取消剩余工作并 fault 引擎，而非静默后台化。显式 scope 的一个子调用完成不等于 root 完成；曾参与 Promise.race 也不豁免其 pending loser。
- 上述请求式约束限制 fire-and-forget 以及 `Promise.race` 尚未结束的宿主 I/O loser；必须公开说明，不冒充完整驻留 event loop。
- 首个 MVP 不公开另一套手工 `RunJobs` 模式，也不开放任意跨线程 JS resolver；长期 event loop 后置。
- never-settled 返回值受取消/截止时间控制；未处理 rejection 在规定的 root checkpoint 报告，不留到下一请求补抛。
- 同一引擎不会因两个 Task 完成而并行执行两段 JS；Dispose 不泵 jobs。

```text
Ready → 持有 scope（Executing ↔ Awaiting）
          ├─ Complete / 普通异常安全收尾 → CompletedOwned → scope.Dispose → Ready
          └─ 取消 / 超限 / 未结外部工作 / 损坏 → Faulted → engine.Dispose → Disposed
```

异步状态机必须验证不合作的宿主、参数被调用方修改、旧 token、输出副作用及未结工作；此处约束不能以“加一把锁”替代。

### 8.3 模块图

- `CompileModule` 产生模板；`LoadModule` / `LoadModuleAsync` 在引擎内解析依赖、链接、实例化、求值。
- 使用宿主返回的 canonical module ID；模块身份同时处于 resolver namespace 和权限域内，缓存另受源码版本/解析器配置影响，不只按显示名称或源码 hash 定义身份。
- resolver 由宿主提供；默认没有文件系统或网络回退，dynamic import 经过相同能力检查。
- cycles、live bindings、一次求值、失败缓存行为必须作为模块语义实现，不能把 ESM 简化为字典拼接。
- 缓存函数对象的 `GetFunction` 与跟踪导出 live binding 的 `GetExport` 是不同 API，见 [公共 API](03-public-api.md)。
- 同步加载只处理预先可用、无需 TLA/异步 resolver 的图，在模块 JS 求值前拒绝必须异步的图。区分同步/异步 resolver 能力，不先启动异步 I/O 再探测是否已完成；异步图通过 `LoadModuleAsync` 给出明确结果。
- 模块数量、总源码、加载深度、循环与重复解析都有限制。链接失败后不对外暴露半初始化模块实例。

## 9. 池化、持久状态与释放

默认池化策略为 **FreshRealm**：每份租约有新的 globals、intrinsics 实例、模块实例和闭包状态；只复用已经清理的 VM 工作缓冲，以及共享不可变代码。

这意味着模块顶层初始化仍有成本，不能把 live engine reuse 的快路径数字写成 FreshRealm 性能。

租约结束顺序：

1. 完成输出转换与最终 checkpoint，检查执行结果。
2. 停止新宿主工作，取消/隔离未决 I/O 与 posted completion，注销取消注册。
3. 使旧 Realm generation 失效；旧任务不得修改下一份租约。
4. 清理栈、slot、IC、宿主捕获等引用；异常/不确定状态不复用。
5. 按阈值回收清洁 buffers；释放租约。

不依赖宿主一定及时停止：不合作的外部任务不得持有可写回新引擎的能力，且要有有界排队/熔断策略。真正强制终止依赖外部进程。

`SessionReuse` 只作为后续显式会话方案：按租户、脚本版本、权限、绑定配置分区，保留状态本身就是契约。首版不提供名为 `Reset()` 却只清 scratch 的 API，也不承诺一个堆快照可以便宜、完整地恢复所有可观察状态。

## 10. 性能实现顺序

1. 固定 JS 语义与测试；建立 eager 数据通道作为正确参考。
2. 测量 value 布局、dispatch、帧和固定参数调用。
3. 加入 shape/IC，并验证所有失效条件。
4. 优化宿主绑定、直接 DTO、JSON 与大输入保留。
5. 测量 Realm 初始化、buffer 池化和并发 GC。
6. 对已证明的瓶颈增加 SIMD/新 TFM 专用实现。
7. 在 AOT 内研究反馈驱动的字节码专门化、合并指令和可选构建期预编译；利用已编译的操作实现，不运行时生成 CLR IL/机器码。保持 guard、失效、慢路径与预算等价。

所有阶段分别测量 NativeAOT 与 CoreCLR。普通 CoreCLR 对解释器方法做 JIT，不等于给用户 JS 做了特化 JIT；AOT 的功能和性能验收也不能靠未来 CoreCLR 专用 JS→IL 实验来完成。
