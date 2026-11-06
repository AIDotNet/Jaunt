# 公共 API 与使用体验草案

> 状态：设计草案，2026-11-03。以下名称/签名用于讨论与后续消费测试，**尚未实现或编译验证**。同步基础属于 P1；模块、异步、生成绑定与插件包装属于 P2 方向，不应一次性冻结全部 API。（2026-11-06 更新：已实施为 `0.1.0-alpha.1`，实测与偏差见 [07-implementation-status.md](07-implementation-status.md)；本文保留为设计记录。）

## 1. 设计原则

- 简单场景少写代码；高频场景不被迫使用反射、JSON 往返或 `params object[]`。
- 只有一个有状态执行入口 `JsEngine`，不同时让使用者理解另一套公开 Runtime/Context 管理体系。
- `JsExecution` 是引擎的执行作用域与预算能力，不是第二个引擎。
- 复制、持有活对象、暴露宿主能力与异步等待必须看得出来。
- API 名称遵循 .NET 习惯；不照搬 Go 的错误返回、原生 Dup/Free 或 handle scope。
- `.NET8/10/11` 共用公共契约；版本差异主要留在内部实现。
- **NativeAOT 必须支持**。每阶段声明的 API 在 CoreCLR/NativeAOT 下保持同一能力与语义，不能只让少数示例可 AOT 发布。`Evaluate`、`CompileScript`、`CompileModule` 必须能在发布后处理构建时未知的新源码。
- 不以运行时发 IL 或隐式反射作为绑定/转换回退；生成和显式手写 descriptor 都是完整 AOT 路径。模块、异步、JSON、插件包装也必须各自完成真实 AOT 消费验证。

## 2. 类型与所有权

| 类型 | 作用 | 共享、生命周期和线程规则 |
| --- | --- | --- |
| `JsCompiler` | 无执行状态的编译入口 | 各编译调用独立；缓存由宿主显式配置 |
| `JsScript` | 不可变 Script 编译结果 | 可被兼容配置的多个引擎并发使用 |
| `JsModule` | 不可变 ESM 模板 | 可共享；不等于已解析/授权/求值的模块图 |
| `JsEngine` | 一个 Realm、VM、模块状态和队列 | 有状态，同一时刻一个逻辑执行者 |
| `JsExecution` | 持有独占权的根执行作用域 | 可跨 await；自身也不能并发调用；不是 ref struct |
| `JsValue` | JS 值 | 原始值可复制；对象/函数/Promise 等受 engine + Realm generation 约束 |
| `JsModuleInstance` | 当前引擎已求值的模块实例 | 不跨引擎或租约共享 |
| `JsFunction` | 当前函数对象的句柄 | 固定函数身份；不自动跟随变量重新赋值 |
| `JsExport` | 某模块实例的导出绑定句柄 | 每次调用读取 live binding；绑定仍属于当前引擎 |
| `JsTypeInfo<T>` | 显式 DTO/值映射描述 | 不捕获请求/Realm 时可共享；不隐式发现未知类型 |
| `JsCallContext` | 同步 raw host callback 上下文 | ref struct/临时参数 span，仅回调内有效 |
| `JsPlugin<TIn,TOut>` | 后续生产插件包装 | 管理并发、FreshRealm 与完整输入输出，不向调用方泄漏 JS 活值 |

`JsValue` 的结构体尺寸/字段布局不是公共 ABI。包含托管引用的值不能 `stackalloc`；固定 arity 重载或可复用数组更合适。

`AsString()` 返回普通 CLR string；原始构造值可安全复制。内部懒字符串若依赖引擎 buffer，向公共字符串边界交付前应物化或取得稳定所有权，不能让 string 引用已经归还的池。

### 句柄不等于结果快照

- `JsFunction`、JS object、Promise 等不能在引擎 Dispose 或旧租约结束后继续使用。
- 即使池重用了同一个 CLR 包装对象，旧 generation 的句柄也必须失效。
- DTO/JSON 转换完成后才是脱离引擎的数据。
- 不要求普通使用者手工 Retain/Release；CLR 引用保活不代表授权句柄在 Dispose 后继续访问。

## 3. 最简单的使用方式：求值与注册

以下示例假定 `using Jaunt;`，C# 语法以 .NET8 常用语言能力为基线。

```csharp
using var engine = new JsEngine();

engine.SetValue("price", 100.0);
engine.SetValue("quantity", 2);
engine.SetFunction("tax", (double amount) => amount * 0.13);

JsValue value = engine.Evaluate(
    "price * quantity + tax(price * quantity)");

double total = value.AsNumber();
```

契约：

- `Evaluate(string)` 是便利入口，每次都会编译；内含编译额度与整体截止时间，不做无界全局缓存。
- 同步 `Evaluate` 不等待外部 I/O，不把 Promise 自动变成普通结果。
- `SetValue` 的常用重载只做明确的数据构造；给一个任意 `object` 不会自动开放整个 CLR 成员图。
- `SetFunction` 对基础类型提供 typed overload；注册时完成适配，调用时不 `DynamicInvoke`。
- `SetValue/SetFunction` 定义或更新 global object 的自有数据属性，不改写 `let/const` 词法绑定；现有 accessor/只读属性冲突默认拒绝，不暗中调用 setter 或强制覆盖。注册/更新也检查所有权与计费，并遵守引擎独占规则。
- `AsNumber` / `AsString` 是类型要求明确的读取，不暗中调用用户 `valueOf`/`toString`。需要 JS 强制转换的 API 必须在执行预算中运行。
- 默认 Standard profile 无文件、网络、反射或任意 CLR type 导入；具体默认额度在 P0/P1 验证后冻结，示例并不表示无约束执行。

## 4. 高频场景：编译一次，重复执行

### 4.1 Script

```csharp
JsScript script = JsCompiler.CompileScript(
    "price * quantity",
    new JsCompileOptions { SourceName = "pricing.js" });

using var engine = new JsEngine();
engine.SetValue("price", 100.0);
engine.SetValue("quantity", 2);

double first = engine.Evaluate(script).AsNumber();
engine.SetValue("quantity", 3);
double second = engine.Evaluate(script).AsNumber();
```

可以把同一个 `script` 交给其他引擎，但 `price`、`quantity` 和执行反馈保持私有。重复执行 Script 的 JS 可观察副作用不会自动恢复。

### 4.2 模块函数与 live export

```csharp
JsModule module = JsCompiler.CompileModule(
    """
    export function calculate(price, quantity) {
        return price * quantity;
    }
    """,
    new JsCompileOptions { SourceName = "pricing.mjs" });

using var engine = new JsEngine();
JsModuleInstance instance = engine.LoadModule(module);

// 固定当前函数对象，适合不会替换导出函数的调用约定。
JsFunction calculate = instance.GetFunction("calculate");

double total = engine.Invoke(
    calculate,
    JsValue.From(100.0),
    JsValue.From(2)).AsNumber();

// 如果模块会重新赋值导出，使用 live binding 句柄。
JsExport liveCalculate = instance.GetExport("calculate");
double current = engine.Invoke(
    liveCalculate,
    JsValue.From(100.0),
    JsValue.From(2)).AsNumber();
```

- `GetFunction` 返回当时的函数对象，不承诺跟随 `export let f` 的后续赋值。
- `GetExport` 只取得导出绑定标识；`Invoke(JsExport, ...)` 在预算内读取当时的值，若不是 callable 则报 JS TypeError。
- `GetFunction` 如需读取实际值，必须使用引擎执行入口与预算；显式 scope 内使用 `execution.GetFunction(instance, name)`，不偷偷重入引擎。
- 不支持把 `"a.b.c"` 隐式解析成属性路径；需要访问属性时使用明确、受预算约束的对象 API。
- 顶层 lexical binding 不一定是 global object 属性；初期不要添加一个名称含糊的 `GetFunction(string)` 同时混淆它们。模块导出是推荐入口。
- `Invoke` 定义 JS `this` 为 `undefined`，再由目标函数自身 strict/sloppy 规则处理；需要显式 receiver 使用 `InvokeWithThis`。

建议为 0–4 参数提供固定重载，并提供 `ReadOnlySpan<JsValue>` 同步批量入口。是否全部需要由 P0 数据决定，不将几十个未使用重载当性能成绩。

## 5. 根执行作用域：让整个数据流程共享预算

低层分开调用 `ParseJson`、`Invoke`、`ToObject` 时，每步单独的超时不足以限制整个请求。显式 scope 解决这个问题：

```csharp
static byte[] Transform(
    JsEngine engine,
    JsExport transform,
    ReadOnlySpan<byte> requestUtf8,
    CancellationToken cancellationToken)
{
    using var execution = engine.BeginExecution(
        new JsExecutionOptions
        {
            Timeout = TimeSpan.FromMilliseconds(50),
            MaxInstructions = 1_000_000,
            MaxOutputBytes = 1024 * 1024
        },
        cancellationToken);

    JsValue input = execution.ParseJson(requestUtf8);
    JsValue output = execution.Invoke(transform, input);
    byte[] response = execution.ToJsonUtf8(output);

    // 最终微任务检查点与未结工作检查，成功后才能交付 response。
    execution.Complete();
    return response;
}
```

这里的限额是业务示例，不是已经确定的库默认值。

### scope 规则

1. `BeginExecution` 取得独占权，建立 root ID、deadline、工作量和输出预算。
2. scope 内只能使用 `execution.*` 的执行/转换入口；再次 `engine.Invoke` 等明确拒绝，不隐式创建嵌套 root。
3. scope 可顺序执行多次调用，全部共享额度；可以收紧引擎政策，不能抬高上限。
4. `Complete()` 执行最终 ready-job checkpoint 和资源/未决工作检查，之后不允许继续操作。
5. **Dispose / DisposeAsync 不执行 JS、不调用 getter、不做转换、不隐式泵 jobs。** 它们只清理、撤销权限和释放所有权。
6. 未 Complete 而放弃 scope：取消它拥有的宿主工作，必要时将 engine 标为 Faulted；不能静默归还一个仍在运行的引擎。普通 JS throw 的安全异常收尾与资源终止分开。
7. 每次 `execution.*` 操作也不能彼此并发；不通过 `AsyncLocal` 把执行权限传给任意子任务。
8. DTO/JSON 在 Complete 成功前只是待发布结果；完整插件 API 会在内部保证这个顺序。

简单 `engine.Evaluate/Invoke` 自动建立并完成单操作 scope，避免强迫每个使用者写上述样板。但调用者随后另做的 DTO 转换不自动属于上一次 root；需要整体策略时使用 scope 或插件 API。

公开 scope 包装本身可能有分配。性能报告必须将它与无语义对象产生的 VM 内核成本分开，不伪称完整请求零分配。

## 6. 宿主绑定：便利路径与明确快路径

### 同步基础类型

```csharp
engine.SetFunction("sum", (double a, double b) => a + b);
```

自定义复杂类型要求对应 `JsTypeInfo<T>` 或生成绑定，不能对未知类型自动退回反射或运行时发码。基础类型重载使用预先编译的 typed adapter；“注册时适配”也不能隐藏 `DynamicMethod` / `Expression.Compile` 发码依赖。

### raw callback

建议签名：

```csharp
public delegate JsValue JsNativeFunction(ref JsCallContext context);
```

使用示例：

```csharp
engine.SetFunction("sum", static (ref JsCallContext call) =>
    JsValue.From(call.GetNumber(0) + call.GetNumber(1)));
```

`JsCallContext` 提供受约束的 `This`、参数、基础读取与值构造能力；参数可借用 VM 寄存器窗口，不可保存、装箱或跨 await 使用。参数不足/类型错误有明确绑定或 JS TypeError，而不是任意 CLR cast exception。

### 服务对象

后续 `Expose(name, service, bindingDescriptor)` 只暴露显式生成/声明的成员，生成与手写描述均须在 NativeAOT 中可用；不支持的成员形状在构建期或注册阶段明确诊断，不等到脚本执行时静默切换机制。

- 数据映射与 live service wrapper 分开；默认数据写入不反向修改原 DTO。
- CLR type、反射、非公开成员、`GetType`、任意构造器/静态成员不自动可达。
- 方法异常如何映射由策略决定；对脚本的消息默认脱敏，对 .NET 诊断可保留受控 inner exception。
- 宿主若主动返回更高权限对象，任何引擎开关都无法替它撤回能力；绑定设计本身需要审查。

## 7. DTO：直接映射，不经 JSON 中转

源生成方向示例：

```csharp
public sealed record PluginInput(string Prompt, int Count);
public sealed record PluginOutput(string Text, int Count);

[JsSerializable(typeof(PluginInput))]
[JsSerializable(typeof(PluginOutput))]
public partial class AppBindings : JsBindingContext { }
```

生成 `AppBindings.Default.PluginInput` / `PluginOutput`，类型为相应 `JsTypeInfo<T>`。建议默认 camelCase、区分大小写，允许显式名称映射；名称冲突在生成时诊断，不按系统文化做猜测。

生成结果必须静态引用所需成员并覆盖实际闭合泛型，保证裁剪后的发布产物可运行；不能只输出类型名称，再在运行时反射发现或发码构造转换器。用户也可以显式提供手写 descriptor，不必为基础 API 安装生成器。

```csharp
using var execution = engine.BeginExecution();

JsValue input = execution.FromObject(
    new PluginInput("hello", 2),
    AppBindings.Default.PluginInput);

JsValue output = execution.Invoke(transform, input);
PluginOutput response = execution.ToObject(
    output,
    AppBindings.Default.PluginOutput);

execution.Complete();
// response 现在可以脱离 engine 使用。
```

### 默认 data-only 导出

首版 DTO 路径优先定义为**数据对象映射**：

- 基础值、普通对象、数组与显式 descriptor；只读约定的自有数据属性，不沿原型链执行访问器，也不自动调用 `toJSON`。
- 默认拒绝访问器、Proxy、Promise、函数和未注册 host object，避免把“读数据”变成任意执行。
- 循环引用默认报错；不承诺维护任意对象图 identity。图导出是另一个后续契约。
- 后续如提供 `AllowObservableAccess` 等模式，getter/Proxy/强制转换必须在同一 root 内执行并计费。
- 导出成功前不修改调用方已有业务对象；返回新建 DTO，避免异常时暴露半成品。
- 源生成或手写 descriptor 不足时给明确错误；不存在隐形 JSON 往返、反射或运行时发码回退。AOT 与 CoreCLR 不能对同一未注册类型采用不同的隐藏转换机制。
- 输入 DTO 的 CLR property getter 仍是宿主代码，源生成不会让任意 getter 自动可抢占；宿主负责提供适当的数据对象。

## 8. JSON：与 DTO 语义明确区分

| API | 约定 |
| --- | --- |
| `ParseJson(string)` | 输入 string 不可变，可保留引用；仍需完整合法性验证与计费 |
| `ParseJson(ReadOnlySpan<byte>)` | 默认取得自身数据所有权；返回后宿主可复用输入 |
| `ToJsonUtf8(JsValue)` | 返回独立 byte[]，检查输出额度，适合原子交付 |
| `WriteJson(JsValue, IBufferWriter<byte>)` | 后续流式快路径；写入可能部分完成，不能承诺失败回滚 |

JSON 输出遵循明确的 `JSON.stringify` 等价规则，包括属性顺序、`toJSON`/getter、副作用、undefined、NaN/Infinity、BigInt 和循环错误。**它不是 data-only DTO 映射的别名。**

建议 `ToJsonUtf8` 要求存在顶层 JSON 表示：当标准 stringify 的结果是 `undefined` 时，给明确的“无 JSON 表示”错误，而不是返回 UTF-8 的 `"undefined"` 或悄悄变成 null。后续可用显式 Try 入口表达这种分支。

getter/Proxy/toJSON 和生成的微任务均计入 root。默认转换先进入有界自有 buffer，Complete 成功后再交付；直接外部 writer 必须允许调用者丢弃部分输出。Framework DTO 导出拒绝 Promise，标准 JSON.stringify 对 Promise 的对象行为不能被误当成业务结果；业务异步调用先经 Async 入口解析返回值。

懒 JSON 与借用 buffer 是后续优化，不改变 API 的所有权承诺；任何借用 API 必须另行命名并定义生命周期。

## 9. 类型转换的默认约定

| CLR / JS | 草案规则 |
| --- | --- |
| `null` / `undefined` | 分别表示；`default(JsValue)` 为 undefined；缺失成员与显式 null 不合并 |
| bool / string / int | 对应 JS boolean/string/number；字符串遵守 UTF-16 |
| double / float | 对应 JS Number；保留 NaN、Infinity、负零的语言语义 |
| long / ulong | 不自动静默丢精度；超过安全整数范围必须显式选择 BigInt 或有损 Number |
| decimal | 不默认隐式转 double；选择自定义映射、字符串或明确的有损策略 |
| BigInteger | 在 BigInt 特性实现后显式映射；JSON 不能默认无损表示 BigInt |
| DateTime / DateTimeOffset | 不依赖机器文化猜测；先显式 descriptor 选 ISO 字符串或 JS Date，拒绝含糊时区 |
| byte[] / Memory | 明确选择 ArrayBuffer/typed array 或数据数组；默认复制，不能顺便共享可写存储 |
| DTO / Dictionary / List | 显式数据映射；不是任意 CLR 反射投射 |
| JS Number → int | 必须为范围内整数；不把截断或溢出当便利默认 |
| required 字段缺失 / 类型不匹配 | 给转换错误和成员路径；可选字段策略显式配置 |

这些规则在三个 TFM、解释器/未来后端、生成/手写绑定下相同。JSON 通道另遵循其语言规则，不被 DTO 的严格规则替换。

## 10. 异步入口与最小 pump 契约

建议提供：

```text
EvaluateAsync(source / script, options?, cancellationToken)
InvokeAsync(function / export, owned arguments, options?, cancellationToken)
AwaitAsync(promiseOrValue, options?, cancellationToken)
LoadModuleAsync(module, options?, cancellationToken)
SetAsyncFunction(name, typed delegate + explicit binding)
```

典型使用：

```csharp
using var engine = new JsEngine();
engine.SetAsyncFunction(
    "lookup",
    async (string key, CancellationToken token) =>
    {
        await Task.Delay(1, token); // 仅演示可取消宿主工作。
        return "value:" + key;
    });

JsValue value = await engine.EvaluateAsync(
    "lookup('item')",
    cancellationToken: cancellationToken);

string text = value.AsString();
```

异步委托的主要形状为 `Func<TArg, CancellationToken, ValueTask<TResult>>`；不同返回类型的重载需避免 async lambda 绑定歧义。复杂参数/结果必须是脱离的 DTO/原始值或拥有明确所有权的 buffer，不能向工作线程传可操作的 JS 活对象。异步适配器及其参数/返回类型也必须静态可达、NativeAOT 可用，不能把 Task/Promise bridge 变成动态发码或未保留泛型的例外。

### 规则必须固定，不由 Task 碰巧是否完成决定

- **同步入口不启动异步 host delegate**：在调用 delegate 前拒绝，提示使用 Async；拒绝前的 JS 副作用不会自动回滚，不可盲目重放源码。
- 同步 `Invoke/Evaluate` 返回前排空当前 ready microtasks，但不 await/unwrap Promise。
- `AwaitAsync` 可等待已取得的 Promise，不重新调用原函数。pending 普通 JS Promise 不因创建 root 已结束就作废，但等待期间仍独占引擎，不能先 `AwaitAsync(p)` 再并行进入同一引擎调用 resolver。
- 纯 JS resolver 应先在合法执行中调用，或在同一 scope 开始等待前调用；有归属的外部 completion 只能通过既有 root 的桥接投递。没有合法完成来源的 Promise 会等到取消/超时，AwaitAsync 不能自动解决它。
- 异步入口采用 JS await 对 Promise/thenable 的 assimilation 语义；普通值也可完成。读取 `then` 等副作用同样计费。
- 只有一个自动 pump；completion 只入队并唤醒，不 inline 执行 JS。等待期间保留引擎独占权。
- 异步参数在首次让出执行前完成稳定快照；`ReadOnlyMemory<JsValue>` 不是 immutable，不能仅凭它保证调用者不会改底层数组。
- 不等待 Realm 中所有 Promise；无关的永不完成普通 Promise 不应堵住整个引擎。
- 未处理 rejection 在规定的 root checkpoint 报告给宿主诊断，不拖到下一请求突然补抛。

### MVP 的请求式异步限制

在 **root 最终 checkpoint 后关闭时**，不允许仍有**尚未完成的外部 host operation**。初期遇到此情况报告 `UnjoinedAsyncWork`，取消剩余工作、使 engine Faulted，并隔离迟到结果；不默默丢弃，也不无限等待。显式 scope 内一个 InvokeAsync 子调用完成不等于 root 关闭，调用方可在同一 root 内继续等待合法工作；是否曾进入返回链不构成豁免。

这限制了 fire-and-forget，以及 `Promise.race` 中尚未结束的宿主 I/O loser。它是明确的宿主生命周期约束，不代表完整的长驻 event loop。后续 Session/后台模式需独立 API、预算和关闭协议；不能悄悄放宽后继续承诺请求隔离。

scope 版本使用 `JsExecutionOptions.Mode = JsExecutionMode.Async`，然后 `await execution.InvokeAsync(...)`、转换、显式 `Complete()`，最后 `DisposeAsync()` 清理。同步 mode 禁止异步宿主工作，即使该 ValueTask 恰好同步完成。

## 11. 模块 resolver 的能力边界

- `LoadModule` 返回时必须已链接、实例化并完成同步求值，能正常读取导出。
- 同步加载只使用明确具备同步能力的 resolver 和无 TLA 的图；发现必须异步时在模块 JS 求值前拒绝。
- `LoadModuleAsync` 等待异步 resolver/TLA，并给出明确成功/失败，不以 pending instance 伪装完成。
- 建议区分同步 `IJsModuleResolver` 与异步 `IJsAsyncModuleResolver`，避免为了探测“是否已完成”而先启动一个异步 I/O。
- resolver 返回 canonical module ID、来源与源码/受信模板；同名不等于同一模块，身份包含 resolver namespace 与权限域。
- 默认 resolver 拒绝访问，不自动读取当前目录、网络或程序集资源。
- static import、dynamic import、循环和 re-export 都走同一授权与图限额。
- 编译产物跨 profile 使用时重新检查 policy；不能先在 Standard 编译，再绕过 Hardened 的动态代码限制。

完整接口中的 cancellation、diagnostic、source/version 字段在模块 spike 后冻结，不在首个 Script 原型里预留庞大 loader 框架。

## 12. 完整插件入口：默认隔离、默认脱离输出

这是 P2 的易用目标，不是首个 P0 必做接口：

```csharp
JsModule module = JsCompiler.CompileModule(
    """
    export function transform(input) {
        return { text: input.prompt.trim(), count: input.count };
    }
    """,
    new JsCompileOptions { SourceName = "plugin.mjs" });

await using var plugin = JsPlugin.Create(
    module,
    "transform",
    AppBindings.Default.PluginInput,
    AppBindings.Default.PluginOutput,
    new JsPluginOptions
    {
        MaxConcurrency = Environment.ProcessorCount
    });

PluginOutput output = await plugin.InvokeAsync(
    new PluginInput(" hello ", 2),
    cancellationToken);
```

`JsPlugin` 负责：

1. 有界等待执行容量，等待也能取消；等待 deadline 与执行 deadline 分开记录，整体请求可由宿主 token 约束。
2. 为该次调用创建新的 Realm，安装明确绑定，实例化模块；共享的是编译结果，不是模块状态。
3. 在同一 root 中完成输入、live export 查找、调用、await、输出与最终 checkpoint。
4. 检查/终止未决外部工作，废止旧 generation，再归还清洁 buffer。
5. 返回 DTO/JSON；调用者无需拿到 `JsEngine` 或自己配对 Rent/Return。

模块顶层初始化成本必须计入。raw `JsEnginePool` / `JsEngineLease` 可在证明确有需求后公开，不与插件 API 一起过早冻结。后续有状态会话是另一种显式契约，不是把默认隔离开关关掉就能安全跨租户复用。

## 13. 限额、取消与异常

配置方向：

```csharp
using var engine = new JsEngine(new JsEngineOptions
{
    Profile = JsProfile.Hardened,
    Limits = new JsLimits
    {
        Timeout = TimeSpan.FromMilliseconds(100),
        MaxInstructions = 2_000_000,
        MaxTrackedBytes = 16 * 1024 * 1024,
        MaxStackDepth = 128,
        MaxPendingJobs = 1000,
        MaxOutputBytes = 1024 * 1024
    }
});
```

额度仅为示例。`MaxTrackedBytes` 是保守计费，不是独立堆/RSS；跨调用仍存活的数据不因开始新 root 就免计费。配置在 engine 创建后冻结，不能在运行中无同步地改预算。

### 取消入口

- 优先在执行入口传 `CancellationToken`，内部注册绑定到具体 root/operation。
- `Interrupt()` 可作为“停止当前活动执行”的显式管理入口；空闲时不留下影响下一次调用的 sticky flag。
- 不要求用户 `ClearInterrupt()`；不得把上一个 token 的 callback 简单注册成无归属的 `engine.Interrupt()`。
- 取消不抢占任意宿主/BCL 阻塞工作，不保证纳秒级 deadline；需要强隔离时使用进程边界。

### 错误分类

| 情况 | 对 .NET 的建议结果 | 引擎是否可复用 |
| --- | --- | --- |
| 语法错误/未支持特性 | `JsCompileException`，含 diagnostic code、来源和位置 | 编译未进入 Realm 时通常不受影响 |
| 参数/归属/并发调用错误 | `ArgumentException` / `InvalidOperationException` 或专用 binding 错误 | 拒绝发生在执行前时可继续；不重放已经发生副作用的代码 |
| 同步路径触发异步宿主绑定 | 明确的 `AsyncRequired` 执行诊断，宿主 delegate 尚未被调用 | 当前 root 失败；安全收尾可复用，但此前 JS 副作用不回滚 |
| 普通 JS throw | `JsException`，含 name/message/JS stack/受控 thrown value | 安全收尾后可继续，状态不回滚 |
| DTO/JSON 转换失败 | `JsConversionException`，含成员路径/原因 | 视是否触发 JS 或终止决定；部分输出不能提交 |
| 活动执行被 token 取消 | 带对应 token 的 `OperationCanceledException` | 初期标 Faulted，丢弃 |
| 执行/分配/输出限额终止 | `JsLimitExceededException`，含 limit kind 与观测值 | Faulted，JS 不可 catch 后继续执行 |
| 未结外部宿主工作 | 执行失败，diagnostic code `UnjoinedAsyncWork` | Faulted，撤销能力、隔离迟到结果 |
| 引擎内部不变量损坏 | 独立 engine fault 异常 | 必须丢弃；不假装普通 JS Error |
| 宿主业务异常 | 按已声明绑定策略映射受控 JS Error 或宿主异常 | 不一概吞所有 CLR Exception；fatal/内部错误不可伪装成功恢复 |

执行入口在传播普通 JS throw 前也需完成规定的异常收尾/checkpoint 与未决工作检查，而不是依赖 Dispose 运行脚本。取消/资源终止不承诺执行 JS finally；宿主资源清理由 .NET 侧负责。

## 14. 状态机与诊断

```text
Ready → 持有 scope（Executing ↔ Awaiting）
          ├─ Complete / 普通异常安全收尾 → CompletedOwned → scope.Dispose → Ready
          └─ 取消 / 超限 / 未结外部工作 / 损坏 → Faulted
Ready / Faulted → engine.Dispose → Disposed
```

- Complete 关闭执行但仍持有 scope 所有权，Dispose 才释放；便利入口在返回前自动完成这两步。
- 正在异步执行时不能调用同步 Dispose 来立刻释放独占权；先取消并观察执行结束，或使用明确的异步关闭流程。关闭不是对任意宿主代码的强制抢占。
- Dispose 幂等；scope 的 Dispose 不等于销毁 engine，Faulted 也不因释放 scope 而恢复。Faulted 不支持 `Reset()` 恢复成新世界。
- `GetStatistics()` 返回明确计量范围的快照，通常在 scope 释放后读取；不默认每条指令发事件或抓 CLR stack。
- JS stack/source location 默认可用；完整源码、宿主参数和租户数据不得默认写日志。
- 未来 trace/debug/ActivitySource 按需开启；缓存诊断容量，防止错误输出本身放大内存。

## 15. 暂不添加的便利 API

- 静态 `Jaunt.Eval(...)` 隐藏共享全局 engine。
- 默默反射所有对象的 `SetValue(string, object)`。
- `dynamic` 作为唯一调用方式、以 `params object[]` 作为唯一参数通道。
- 一个 `ToObject<T>` 同时承担 data-only、JSON stringify 和完整对象图 identity 导出。
- 任意 byte[] 反序列化为可执行代码。
- `Reset()` 假装清除了所有脚本状态。
- 默认网络/文件模块 loader 或 broad `AllowClr()`。
- 同时提供三种含义不一致的同步阻塞、自动 pump 和手工 pump 模型。

**API 的易用性不仅是代码短，还包括用户能够正确推断成本、生命周期和安全边界。**
