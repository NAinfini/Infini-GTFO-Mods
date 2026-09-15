# ForgeRuntime 验证记录

**上次更新：2026-09-14**（内容合并自 R1、R2a、R2a-cleanup、R2b-1、R3a、R4a 六份交接记录与旧采集版验证记录）。

计划与状态见两仓统一框架第 6 节 U-RUNTIME（链接见[仓库 README](../README.md)）。本文只记录 Runtime 侧各次交付的实际内容、复跑命令与仍然存在的失败；没有任何游戏、安装、多人或恢复验收。

## D-007 阶段 C — 内核记录点（2026-09-14）

**现状盘点（改前，逐码）。** 已有记录点只有三处：`plan.rejected`（`Framework/RuntimeKernel.cs` 的 `LogPlanRejected`，被 8 个计划拒绝分支调用）、`plan.loaded`（同文件的 `LogPlanLoaded`，1 处）、`log.level` 与 `log.dropped`（`Logging/RuntimeLogWriter.cs` 的 71/100/136 行附近）。其余 13 个内核可见的事件码没有任何记录点。从 `TickResult` 转日志的旧路径有两处，都在 `GameBindings/GameRuntimeBridge.cs`：`FixedTick` 里按 `result.Commands`/`result.Events` 逐条 `LogWarning`（改动前的 84-88 行），以及 `ReportLifecycleFaults` 把内核的观察者故障计数镜像成 `LogWarning`（改动前的 135-141 行）。`Suspend` 直接写 `PluginLog.LogError`。

**事件码表落点。** `Framework/RuntimeLogContracts.cs` 的 `RuntimeLogCodes` 补齐 `registration.rejected`、`binding.registered`、`world.began`、`trigger.fired`、`event.rejected`、`budget.exceeded`、`event.deferred`、`event.cancelled`、`step.started`、`step.finished`、`entry.stopped`、`observer.failed`、`runtime.suspended`，加上原有的 4 个共 17 个；`adapter.attached`/`adapter.failed` 与各包原生诊断码按任务说明不做，也没有提前声明。新增 `RuntimeLogReasonCodes`，只放内核自己发明、契约码表里以「内核码」身份出现的两个值：`invalid-handler-result`（结果组合非法时改记 failed/unknown）与 `lifecycle-observer-failed`（观察者故障结果码，同时仍是 `LastLifecycleFault.Code`）。其余 reason 都是产生它的异常码或结果码本身，例如计划组的 `plan-conflict`/`plan-budget`/`plan-path`/`invalid-json`、结果组的 `rejected`/`failed`/`cancelled`/`expired`/`deferred`、内核码 `source-lifecycle`/`plan-unloaded`/`not-host`/`queue-budget`/`tick-event-budget`/`plan-tick-command-budget` 等 —— 没有新增或删除任何码值。

**记录点（`Framework/RuntimeKernel.Logging.cs` + 调用点）。** 新增一组只在内核里使用的写入方法，全部接收 `in RuntimeLogRecord`（记录是 readonly struct，不装箱），调用点只做一次门比较，不构造字符串：

- `LogRegistrationRejected`：`Register` 的**全部**拒绝路径（注册窗口已关、模块容量、seed 解析与注册表容量）都包在同一处 try/catch 里，按 `rejected` 记 error；`subjectProvider` 从 seed 文本里单独取一次 provider id（`DeclaredProvider`），seed 不可读时留空。
- `LogBindingRegistered`：注册成功后按 ordinal 顺序为每个属于该 provider 的 binding 写一条 info。为此 `RegisterLogGate` 不再自己发布快照，`Register` 先发布新表、再写 binding 记录，因此每条记录携带的级别表已经列出它自己的 provider。
- `LogWorldBegan`：`BeginWorld` 状态复位后、通知观察者前写一条 info（tick 为 -1，`worldEpoch` 已是新值）。
- `LogTriggerFired`：`Publish` 入队成功后与 `AdmitPulse` 计数完成后各写一条 info，归属取 binding 的 provider，`rootEventId` 为事件自身的 root 或它自己的 id。
- `LogEventRejected` / `LogBudgetExceeded`：`Publish`、`Schedule` 的 catch、`Publish` 与 `Schedule` 的在 try 内直接返回的拒绝（`event-id-conflict`、`schedule-id-conflict`）、`AdmitPulse` 的脉冲拒绝、`PublishConfirmedFacts` 的事实拒绝与非主机队列清理，全部按 reason 是否以 `-budget` 结尾分流；同一拒绝只记一条。`event.rejected` 与 `event.cancelled` 在发布方仍可识别时把 providerId 写进 `subjectProvider`（契约说该字段「可识别时带」）。
- `LogEventDeferred`：`AdvanceCore` 的分派循环用新的 `BudgetRefusal(Pending)` 统一判断预算顺序（全局事件 → 全局命令 → 每个计划的事件/命令，与循环原来的判断顺序一致），返回第一个拦下它的码；新的 `deferredLogged` 标志保证每 tick 最多一条 trace 记录（重置点与 `eventsThisTick` 相同）。计划 pulse 因每 tick 脉冲上限（`scheduled-tick-budget`）延后时也写这一条。
- `LogEventCancelled`：排队事件在派发前被丢弃就写一条 trace —— provider 注销、scope 取消、计划卸载，以及计划 pulse 被跳过期策略丢弃、生命周期结束、句柄不再 active 这几种队列内脉冲的丢弃。
- `LogStepStarted` / `LogStepFinished` / `LogEntryStopped`：`StepOrigin` 是新增的 readonly struct（事件、计划身份、entry nodeId），按 `in` 传递；归属取**执行该步的 binding**（`step.BindingId`，不是入口的 trigger binding），`step.started` 在 handler 调用前（trace），`step.finished` 在命令回执生成后（`failed` 或 commit 为 `unknown` 记 error，其余 info；它是唯一带 `commit` 的记录），`entry.stopped` 只在结果让 entry 停下且该步还有未执行后继时写一条 info，归 Runtime。控制步不发回执，因此仍不写步骤记录；`pure` 步的求值失败继续只走所在的 action/control 步结果。
- `LogObserverFailed`：`InvokeLifecycle` 的 catch 里写 error，归属观察者的 provider，`Detail` 是单值 `error.Message`；`LastLifecycleFault` 与计数保留。
- `LogSuspended(code, detail?)`：公开入口，每次暂停一条 error，reason 取契约 `runtime.suspended` 一行的四个值。`StartRuntime` 初始化失败记 `startup-failed`；正常 `StopRuntime` **不写**——停止不是暂停，契约的 reason 表也没有对应码（`runtime-stopped` 在不进日志的内核码表里）。宿主 `Suspend(code, detail)` 只在启动状态不是 Failed/Stopped 时补记，避免与启动失败重复。

**删掉的双路径。** `GameRuntimeBridge` 不再读 `TickResult` 的命令/事件回执写 BepInEx 警告，`ReportLifecycleFaults` 与 `_reportedLifecycleFaults` 整段删除；`Suspend` 改成 `Suspend(string code, string detail, bool untilLobby = false)`，只调 `kernel.LogSuspended`；`Guard` 用 `bridge-exception` + `error.Message`；`NativeHooks` 的检查点恢复传 `checkpoint-restore`。观察者故障不再每帧镜像，因此 `tests/GameBindings` 相应断言改为「宿主不再镜像内核记录」+「故障计数不重复」。

**测试（已写好，未运行）。** `tests/RuntimeLog` 新增 `RecordFixture.cs`：三个托管测试 provider（两个扩展包：一个 trigger，一个同时提供 trigger 与 action，含真实声明图、接收者合同、实体解析器）与一份编译计划（入口 A 走第一个包的 trigger、步骤在第二个包；入口 C 走第二个包自己的 trigger，首个步骤由失败 handler 停住且后继步骤在第二个包，因此「步骤归执行方 provider」和「停止只在还有后继时写」都可观察）。`Program.cs` 新增 11 个用例，覆盖每个码的级别、归属、必带字段与是否带结果（含「除 `step.finished` 外不带 commit」）、`event.rejected`/`budget.exceeded` 的分流、每 tick 一条 `event.deferred`、关闭级别时 sink 零记录且提级后旧门可见、以及 writer 端「一个注册不额外写 `log.level`」与 `runtime.suspended` 的 JSONL 往返。`tests/Architecture` 新增 `RecordPointProbe.cs`：事件码常量与码表双向一致、`RuntimeLogRecord` 不含 message/inputs/玩家标识字段、级别词表顺序不变、只有 `LogStepFinished` 写 `commit`，并用自带的 CIL 走查确认 12 个以上记录点方法都不调用 `System.String`/`StringBuilder`。`tests/HostIntegration` 的 `LogBoundaryProbe` 扩到宿主同目录下的全部 `Forge*.dll`。`tests/GameBindings` 的 `Suspend` 调用点补 code 参数。

**编译（本次实际执行，未运行任何测试）。** `dotnet build <工程> -c Release -v q --artifacts-path $env:TEMP\dsh-d7b`，`GTFO_BEPINEX_PATH` 指向只读的 `Forge-MapEditor-QA` profile。`ForgeRuntime.Framework`、`ForgeRuntime` 宿主与 `tests/Architecture`、`EntityObservation`、`Framework`、`GameBindings`、`GraphContracts`、`HostConfiguration`、`HostIntegration`、`LifecycleWork`、`PluginStartup`、`RuntimeLog` 共 12 个工程，退出码全为 0，0 警告 0 错误。

**待 Claude 统一运行的测试（尚未运行，不能记为通过）。**

| 套件与参数 | 本批关注点 |
| --- | --- |
| `tests/RuntimeLog --root <新目录>` | 新增 11 个记录点用例；原有 sink、级别、提级与 writer 场景不变 |
| `tests/Architecture` | 新增码表双向一致、记录形状与 CIL 走查断言 |
| `tests/HostIntegration --host <ForgeRuntime.dll>` | `LogBoundaryProbe` 现在也扫宿主同目录的 `Forge*.dll` |
| `tests/GameBindings`（默认与 `--native`） | 宿主不再镜像内核记录；`Suspend` 补 code 参数后恢复/迁移用例不变 |
| `tests/Framework`、`GraphContracts`、`LifecycleWork`、`EntityObservation` | 内核记录点接入后原有断言不变（`LifecycleWork` 需 `--fixtures ../Infini-GTFO-Model-Site/Tests/Forge/fixtures/runtime`） |
| `tests/HostConfiguration`、`PluginStartup` | 宿主 cfg 与启动顺序不变 |

**规格歧义与裁定（2026-09-15）。** 1、3、4、5 按本实现接受。2：合同取消组扩为 4 个码，`lifetime-ended`、`missed-pulses` 移出"不写入日志"表。6：合同 `budget.exceeded` 改为"事件发布、入队或派发时因预算被拒"，与后缀分流一致。下面保留原问题描述。

1. 契约「结果组合合法性检查」在 622-627 行给出规则，但内核现有的 `NormalizeInvokedResult` 一直实现着它（`CommandResultRules.TryValidate` 不通过就改记 `failed`/`unknown` + `invalid-handler-result`）。本批没有改动这个行为，只把它的结果码收到 `RuntimeLogReasonCodes.InvalidHandlerResult`；如果契约要求把非法结果的 reason 记成别的码，需要改这一处。
2. `event.cancelled` 的 reason：契约触发时机说「发布模块已注销、scope 已取消或计划已卸载」，而 reason 表的取消组是 `source-lifecycle`/`plan-unloaded`。本实现把「provider 注销」与「scope 取消」都记 `source-lifecycle`（与该事件原有的 EventReceipt 码一致），只有计划卸载记 `plan-unloaded`。另外契约的取消组没有列「计划脉冲被跳过期策略丢弃」「生命周期结束」「句柄不再 active」这三种队列内脉冲丢弃，本实现分别记 `missed-pulses`、`lifetime-ended`、`source-lifecycle`。
3. `event.deferred` 的 reason 表列了 `tick-event-budget`、`tick-command-budget`、`plan-tick-event-budget`、`plan-tick-command-budget`、`scheduled-tick-budget` 五个码，本实现按「第一个拦下它的预算」选码（顺序与改动前循环的判断顺序一致）。每 tick 只写一条 `event.deferred`（用第一个被拦下的事件作为 eventId/binding），这也是契约「每 tick 最多一条」的读法。
4. trace 的 inputs：契约说 trace 的 `inputs` 由 sink 同步复制、由 ForgeDevelopment 的 trace 记录器格式化，但没写清内核侧的传递形状。本批按任务说明只在内核里留了 trace 级记录点（`step.started`/`event.deferred`/`event.cancelled`），`RuntimeLogRecord` 仍没有 inputs 字段，也没有把 slot 数据传出去；`inputs` 的复制与转文本留给 Development 的记录器，需要 Claude 确认这个边界。
5. `runtime.suspended` 的 level：契约把它列在 error 组，本实现一律记 error。正常 `StopRuntime` 不写这条记录（停止不是暂停，reason 表也没有对应码；`runtime-stopped` 在契约「不写入日志的内核码」表里）。如果契约要求「停止也记一条」，需要先给它一个进日志的 reason 码。
6. `budget.exceeded` 的适用范围：契约 651 行用「reason 不以 `-budget` 结尾」定义 `event.rejected`，652 行却把 `budget.exceeded` 写成「派发阶段因预算拒收事件」。本实现按后缀规则分流全部拒绝（发布期的 `queue-budget`、`event-history-budget`、`plan-queue-budget` 也走 `budget.exceeded`），否则发布期的预算拒绝会落进 `event.rejected` 与 651 行的定义冲突。需要网站确认 652 行的「派发阶段」是否要按字面收窄。

**未做/未验证。** 没有实机运行、没有加载 GTFO、没有导出 `forge-logs` jsonl 作为 I-DIAG 仲裁物；领域包自己的记录点（经 `RuntimeModuleHandle` 写出）与本包内部原生诊断码仍不在本批；`adapter.*` 待 I-ADAPTER-SCHEMA；没有性能测量，内核记录点的开销只有「未启用时一次门比较」这条静态保证。`TickResult` 的字段与语义没有改动，仍按原样返回给宿主。

## D-007 阶段 A — 注册时必填日志级别（2026-09-14）

> 本节按任务要求放在顶部；本文件其余章节仍是按交付时间顺序排列的历史记录，下面 D-007 阶段 B 一节里的"只有 Runtime 自身有条目"是当时的记录，已被本节的注册级别取代。

**改动。** SDK 的注册入口改为 `RegisterModule(RuntimeModule module, RuntimeLogLevel level)`，单参数重载删除，不留兼容路径；`level` 只接受 off、error、info，传 trace 报 `log-level`（提级是唯一的 trace 入口）。级别不放进 `RuntimeModule`：内核在注册成功时为该 provider 建 `RuntimeLogGate(level)`，注销时移除。`LogGate` 对已注册 provider 返回其级别，对未注册 provider 仍报 `log-provider-unregistered`；级别表在注册、注销与提级时发布新快照，snapshot 因此总是包含已注册 provider。提级之后再注册的 provider 也是 Trace，不是它的 cfg 值。Runtime 自己的条目（`Identity.Id`）由宿主构造建立，注册与注销两侧都跳过它，模块即使声明同一个 provider id 也不能替换或删除。Runtime 自己随宿主注册的 CombatContracts、ControlContracts 与 Trigger 框架模块没有包 cfg：它们走 `internal RegisterBuiltinModule`（Framework 程序集加 `InternalsVisibleTo("ForgeRuntime")`，只有宿主程序集能调用），取 Runtime 自己的级别，不经公开参数伪造。

新增 `RuntimeLogConfiguration.ParseLevel(string)` 作为宿主 cfg 与各包 cfg 共用的文本词表（off/error/info，大小写与首尾空白不敏感，非法值抛同一条消息）；`RuntimeSettings` 改用它，行为不变。SDK 仍然不读任何 cfg。

**各包接线。** Enemy、Weapon、Map 三个原生插件在自己的 `Load` 里绑定 `[Logging] Level`（默认 `error`，改动需重启，非法值在注册前抛错），把级别经 `EnemyPluginSession.Start` / `WeaponNativeSession.Start` / `MapPluginSession.Start` 传到 `EnemyModule` / `EquipmentIdentitySession` / `PlayerIdentityModule` 的 `RegisterModule` 调用；`MapIdentitySession` 与 `EquipmentIdentitySession` 的公开构造函数同样新增该参数。宿主 `Runtime.Mode = Off` 时三个插件都在读 cfg 之前返回，`Off` 下不会写出新的包 cfg 文件。Enemy、Map、Weapon 三个插件的测试替身补了 `BepInEx.Configuration.ConfigFile` / `ConfigEntry<T>` 与 `BasePlugin.Config`，并各加一条 `plugin.invalid-log-level` 用例（非法值在装 Hook 前失败）。

**测试迁移。** 138 处测试 `RegisterModule` 调用点与 40 处模块/会话构造点改为显式传级别（测试统一传 `RuntimeLogLevel.Off`）。`tests/RuntimeLog` 的 provider 级别用例改为：未注册报 `log-provider-unregistered`；注册时传 info 后 `LogGate` 返回 info 且该 provider 的记录能进 sink；trace 作为注册级别被拒且不留条目；一个包注册两个 provider 时共用同一级别；注销后再次报错且记录被拒。`tests/GameBindings` 新增"Runtime 内置 provider 取 Runtime 自己的级别"断言。

**编译（本次实际执行，未运行任何测试）。** `dotnet build <工程> -v q --artifacts-path $env:TEMP\dsh-d7a`，`GTFO_BEPINEX_PATH` 指向只读的 `Forge-MapEditor-QA` profile；三个原生工程另传 `-p:ForgeRuntimeAssembly=<artifacts>/bin/ForgeRuntime/debug/ForgeRuntime.dll` 与 `-p:ForgeFrameworkAssembly=<artifacts>/bin/ForgeRuntime.Framework/debug/ForgeRuntime.Framework.dll`（ForgeEnemy 各测试工程按自身 csproj 要求也传了 SDK 程序集）。`ForgeRuntime.Framework`、`ForgeRuntime` 宿主、三个 `Native` 工程与 33 个测试/领域工程共 38 次构建，退出码全为 0，0 警告 0 错误。

**待 Claude 统一运行的测试（尚未运行，不能记为通过）。** 期望值按现有断言推断，只列本批直接相关者：

| 套件与参数 | 本批关注点 |
| --- | --- |
| `tests/RuntimeLog --root <新目录>` | 注册/注销级别、级别表快照、Runtime 自己的条目不被模块替换或删除、提级与既有 writer 场景全部通过（原 127 项基础上调整 provider 级别用例） |
| `tests/HostConfiguration` | `[Logging] Level` 的合法/非法值与保存往返不变；解析改走 `RuntimeLogConfiguration.ParseLevel` 后消息与拒绝行为一致 |
| `tests/PluginStartup` | 宿主插件仍在 Harmony 与宿主初始化之前拒绝非法级别 |
| `tests/GameBindings`（默认与 `--native`） | 新增内置 provider 级别断言；注册调用点补参后原有边界断言不变 |
| `tests/HostIntegration --host <ForgeRuntime.dll>` | 日志调用点 IL 检查仍通过；宿主注册内置 provider 的调用点不构造字符串 |
| `tests/ForgeTrigger`（Contracts / Pure / R3Consumers / Acceptance） | 补级别参数后注册与观察者语义不变 |
| `ForgeEnemy/tests/NativePlugin` | 新增 `plugin.invalid-log-level`；其余 plugin/session 用例不变 |
| `ForgeEnemy/tests/LifecycleFacts`、`EntityObservation`、`BehaviorObservation`、`CommitAudit`、`ReceiverProbe`、`SpawnRequirements` | 构造点补级别参数后原有断言不变 |
| `ForgeMap/tests/MapNativeAdapter` | 新增 `plugin.invalid-log-level`；Map 插件接线与身份用例不变 |
| `ForgeMap/tests/MapIdentity`、`MapContracts` | `MapIdentitySession` 新参数后的生命周期与注册用例不变 |
| `ForgeWeapon/tests/NativeAdapter` | 新增 `plugin.invalid-log-level`；会话启动顺序与 owner 用例不变 |
| `ForgeWeapon/tests/Identity`、`IdentityAcceptance`、`IdentityDispatchReview` | `EquipmentIdentitySession` 新参数后的注册、冲突与派发用例不变 |
| `tests/Framework`、`GraphContracts`、`LifecycleWork`、`EntityObservation`、`Architecture` | 注册调用点补参后全部原有断言不变 |

**未做/未验证。** 内核与领域都还没有记录点，玩家层仍不会写出任何业务记录；`log.level` 两行上限只在"注册期先于首条记录"的前提下成立（注册窗口在 `StartRuntime` 前关闭）；没有加载 GTFO，没有实机验证 `forge-logs` 输出或各包 cfg 文件的实际生成。网站合同 §I-DIAG 不需要改（接口文字与实现一致），如需补充可在 577 行后加一句"领域包 provider 的级别表条目在注销时移除"。

## R1 — 宿主编译基线

R1 交接记录末段那三条 REOPENED 阻塞已全部解决：`ForgeEnemy/Receivers/EnemyHealthCommit.cs` 的 `RuntimeJson.Text` 缺失、`GameBindings/EnemyModule.cs` 的 `DamageObservation` 缺少 `damagePointer` 参数、以及六模块架构工程因此无法构建。旧 `GameBindings/EnemyModule.cs` 已在 E1 切换中移除。

合并进来的 `Infini.ForgeRuntime` 原型被默认 compile glob 误编进生产宿主，同时生产宿主与 SDK 与测试也被误编进原型工程，复现出 59 个错误。两个工程现在显式分离源码，生产只引用 `ForgeRuntime.Framework`。**原型不是生产 API 也不是 fallback**，它的退休是 R4 的显式收尾项；新合并的源码与其独立测试保留。原型单独构建通过，其 14 项测试需要显式 `dotnet --roll-forward Major`（本机没装 .NET 8），这不改变 net6.0 生产目标，原型的检查也不计作生产 SDK 或游戏支持证据。2026-09-13 原型源码、测试工程与其 CI 工作流已整体删除。

## R2a — 公开生命周期与宿主接线

`GameRuntimeBridge` 改为调用内核的 `StartRuntime`/`StopRuntime`，注册在导出 manifest 与离线 LoadPlan 之前冻结。删除了 `GameBindings/FrameworkStartup.cs` 与其测试源码链接，两条旧 latch 断言迁到公开内核。新增 `Plugin.CanExecuteGameplay`，包含所属线程检查、Ready/InLevel/主机门槛、待处理的失效与停止、迁移与挂起检查。失败处理不再对 Failed/Stopped 内核调用 `BeginWorld`；文件初始化失败不会在 100 tick 后或 Generating/InLevel 转换时重试。派发过程中的原生 teardown 立即关闭门槛，然后在安全点做世界与停止清理；保留的内核引用在停止后是终态。观察器异常被隔离，按变化的错误计数记录一次，健康的观察器与玩法继续。

一条新回归先在"客户端在下一 tick 之前被提升"上失败，保留原始权威基线并忽略重复的 InLevel 重置后通过。

独立复验在全新目录中完成，`tests/HostIntegration/verify.ps1` 退出 0；前后源码快照零变化，宿主与测试各自的 SDK 副本 SHA-256 相同。

## R2a — 已加载工作的清理修复

检查发现 `CancelSchedule` 与 `ReleaseLease` 调用 `Thread()`，而 `Thread()` 现在也拒绝失败或停止的启动状态，因此已取消的 IDisposable 句柄可能在正常的停止后清理中抛出。用真实已加载的 schedule 与 lease 复现：LifecycleWork 先是 50 项通过、2 组失败，失败栈指向停止后的 `CancelSchedule` 与失败启动后的 `ReleaseLease`。

两个清理方法改用 `ReadThread(); NoLifecycleMutation();` 而不是 `Thread()`——终态句柄的清理不再请求"开始新工作"的权限。精确句柄身份检查、错误线程拒绝和只读观察器限制都保留，注册、发布、调度、lease 获取与 Advance 的门槛都没有放宽。修复后 57 项通过、0 组失败。

## R2b-1 — 宿主配置与启动隔离

配置归属从 `AuthoringSettings` 移到 `RuntimeSettings`，行为细节见 [README](README.md#宿主启动与配置)。第一次真实 BepInEx 配置测试暴露了 5 种畸形模式场景会选中默认值或组合枚举值；改为显式解析并加上保存文件往返检查、以及"宿主只保存自己的键时不破坏未绑定的诊断设置"之后通过。

用生产 `Plugin.cs` 加托管替身复现插件加载失败路径：原始生产源码 5 项断言通过、8 个场景失败；补全后的 bootstrap 套件 42 项通过、退出 0。失败的 unpatch、日志或组件清理不会跳过剩余清理、不会替换原始启动异常、不会留下可用的宿主、也不会重试 Load。D2 移除宿主内诊断后，该套件现为 35 项，HostConfiguration 为 66 项（“不破坏未绑定诊断设置”用例随诊断键移出宿主而删除）。

Windows 的共享锁曾阻止两个已有文件的原子替换；先核对其未变字节再做常规原地编辑并读回，没有为此改动 ACL、其他进程或用户文件。

**R2b-1 是宿主配置与启动交付，不是 D2/E1 的插件切换。** 独立 GUID 与包身份、最终配置文件迁移、发行布局、真实加载组合、原生 detour 行为与多人都未验证。

## R3a — 实体观察与 actor 合同

原来未完成的 Registry 观察登记、模块注销清理和公共只读保护已在真实共享 SDK 接通。跨模块测试另外检出"实体观察回调可以注销生命周期订阅"的问题；现已只在实体观察回调内禁止该入口，正常清理、停止后清理与生命周期回调自注销都保留。

SDK 合同与注册探针 76 项、Enemy 消费方 66 项、Trigger 的 R3 消费方 417 项、Framework 253 项与生命周期回归全部通过。**没有第二个 SDK 或 Registry；旧的隔离补丁提案不再需要应用。** 完整 R3、世界枚举、网络与恢复、游戏验证仍未关闭。

## R4a — 可变端口元数据与精确修订解析

网站 `graph-schema.ts` 会校验 `graph.variadic` 并按可选整数计数展开选定一侧，下界等于原有的 2 个端口，上界不超过 32；已有基础端口名与顺序不变，新增端口是 `templateId_3`、`templateId_4` 依此类推。此前 `RuntimeRegistry` 把 `variadic` 当未知字段拒绝，Trigger 的 R4 交接记录了这个阻塞。

现在能在不剥字段、不改所有者与版本的前提下校验该元数据，并从唯一注册定义中按精确能力 revision 和有界计数解析端口。**解析出的端口布局只是元数据**，没有 handler、权限或调度器。Plan v2 加载器按常量展开 variadic 与 portGroups，再与文件 layout 逐项比对（`PlanBoundaryTests` 覆盖固定与展开两种计划，以及未展开、虚假展开、计数不符三类拒绝）；步骤输入仍只接触发事件槽位。完整 R4 lowering 另算。

## D2 — 宿主移除诊断

`Plugin.cs` 只保留宿主配置、`GameRuntimeBridge`、本程序集的 `harmony.PatchAll` 与 `FrameworkMonitor`；启动失败按宿主停止 → unpatch → 组件销毁清理。`GameBindings/PluginPatchSelection.cs` 删除，Play 与 Authoring 启动同一个宿主。诊断源、测试与 Python 工具迁到 ForgeDevelopment，结果见 [Development 验证记录](../ForgeDevelopment/VALIDATION.md#d2--独立插件切换)。

宿主侧重跑：宿主与 `Forge.Architecture.sln` 构建 0 警告 0 错误；Architecture 36；PluginStartup 35；HostConfiguration 66；HostIntegration `--host` 53，新增宿主无诊断类型、全部 HarmonyPatch 位于 `ForgeRuntime.GameBindings`、宿主插件无 BepInDependency 且不引用 ForgeDevelopment；GameBindings `--native` 51，宿主 Hook 精确为 4 个 Framework Hook 且 Load 调用 `PatchAll`，对照的本地 GameAssembly SHA-256 以 `C6A5C3CD` 开头。Enemy 原生插件随宿主重新构建，NativeLayout 38/38；重建时修了 `ForgeEnemy/Native/EnemyModule.BehaviorObservation.cs` 缺少 `using System;` 的既有编译错误。

使用网站 fixture 的 GameBindings `--fixtures` 入口当时失败于 `Unsupported plan version`：网站 F0 fixture 已把 schemaVersion 改为 2，SDK 仍只接受 1。D2 没有改 SDK，这不是 D2 回归；已由下面的 U-RUNTIME 处理。

以上都是托管替身与编译后元数据证据，没有加载 GTFO。

## U-RUNTIME — API 2.0.0 与 plan schemaVersion 2

与网站 `ebc37a11`（Forge Standard v0.2）同批。`RuntimeKernel.ApiVersion` 为 2.0.0，是模块、宿主身份与测试唯一的版本来源；`GameRuntimeBridge` 原先写死的 1.0.0 身份一并改掉。manifest 自身 schemaVersion 仍为 1。

注册校验按网站 `validateCapabilityGraph` 重写：cardinality、resourceKind/handleKind/lifetime、参数 role 与 set/values、参数与输入重名、上下文角色端口、完整 recipients 与 result 输出、variadic 与 portGroups 约束；`entity-list` 退役为 entity + many。计划加载器只读 v2：位置 pin 表、按注册合同展开 variadic/portGroups 后重推 layout 并逐项比对、位置常量、`{slot, fromEventSlot}` 输入。加载时把槽位解析回名字，handler 仍拿键值形式的 Parameters/Inputs；**没有提供位置帧 handler SDK**。v1 计划与 v1 注册形状直接拒绝，没有兼容路径。

构建：SDK、宿主 `ForgeRuntime.csproj` 与 `Forge.Architecture.sln` 0 警告 0 错误。结果：

| 套件 | 结果 |
| --- | --- |
| Framework | 242；`--fixtures` 270，网站 27 个非法计划逐条核对拒绝码均为预期原因 |
| GraphContracts | 1911，0 失败；verify.py 通过；5 个错误实现全部检出（新增 `plan-skips-expansion`） |
| GameBindings | 默认 31；`--fixtures` 62；`--bridge` 57；`--native` 51（GameAssembly SHA-256 `C6A5C3CD…`）；`--export-manifest` 实际导出 apiVersion 2.0.0 |
| 宿主 | Architecture 36；HostIntegration 默认 42、`--host` 53；PluginStartup 35；HostConfiguration 66；LifecycleWork `--fixtures` 57；EntityObservation 75、`--probe-registration` 76 |
| Enemy | LifecycleFacts 52/52；CommitAudit 52/52；NativePlugin 24/24；ReceiverProbe 40/40；EntityObservation 66/66；BehaviorObservation 22/22 |
| Map / Weapon | MapContracts 报告 33 通过、0 失败；IdentityDispatchReview 20/20 |
| Trigger T1 | C# 928、TypeScript 392；Acceptance 2625 |

网站 `generate.ts` 的 `permission-escalation` 曾在追加权限后未排序，C# 报的是 `plan-order`；网站已改为排序后生成，C# 现报 `permission-lock`。网站工作树里未提交的枚举集改动（`compare_operator` 收窄、新增三个集合、structural enum 的 set 子集）尚未同步，提交后需要跟进。

以上都是托管替身、编译后元数据与本地原生签名证据，没有加载 GTFO，也没有安装。

## 提升参数（layout.promoted）

网站工作树的编译器开始为每个 layout 写出 `promoted`：严格递增的参数声明下标，被提升的参数在 `constants` 中必须为 null。加载器据此把这些 value 参数移出参数表，按声明顺序作为输入追加在展开后的输入之后（类型沿用参数；recipient-policy 变为 schema `forge.policy.recipient` 的 policy；枚举带 set；单位照抄；非必填即 optional），再与文件 layout 逐项比对。只有 role 为 value 的参数可以提升。

R4/Q3：枚举值端口已放开。运行期把 enum 端口/字面量/提升输入的 wire 值一律当作集合内的成员下标——事件槽和提升输入按端口 schema 对应的完整具名集合算下标，结构参数的字面量常量若带 inline `values` 则按该列表算，否则按所属 set 算；计划里任何位置都不能出现成员名字符串，出现即以 `invalid-enum` 拒绝（越界、非整数、字符串同一处理）。dispatch 时按索引重新校验提升值的边界与成员，通过后才在 handler 边界把索引换回成员名字符串，交给 handler 的 `CommandContext.Parameters`/`Inputs` 与改动前一样是名字，无需改动现有 handler。含 recipient-policy 参数的能力仍以 `unsupported-parameter` 拒绝，这一处与 enum 无关，未随本次改动变化。

handler 不感知提升：dispatch 把事件送来的值并回 Parameters，再按注册合同重新校验边界与成员，**越界直接拒绝，不钳制**，handler 不被调用。输入仍只能来自触发事件槽位。

新增拒绝码 `promotion-frame`（下标重复、逆序或越界）、`promoted-constant`（提升位置写了常量）、`promotion-role`、`promotion-collision`；删除提升槽位报 `layout-mismatch`，未驱动的必填提升输入报 `missing-input`，缺 `promoted` 字段报 `missing-field`。

重跑结果：

| 套件 | 结果 |
| --- | --- |
| Framework | 255；`--fixtures` 285（含 enum 提升被拒），网站新增的 `promoted-constant` 与 `promoted-slot-missing` 非法计划分别报 `promoted-constant` 与 `layout-mismatch`；正例中值 7 进入 Parameters，值 500 以 `parameter-maximum` 拒绝 |
| GraphContracts | 1911，0 失败；verify.py 通过 |
| GameBindings | 默认 31；`--fixtures` 64 |
| 宿主 | Architecture 36；HostIntegration 默认 42、`--host` 51（`2a20d18` 删除原型探针时去掉 2 项）；PluginStartup 35；HostConfiguration 66；LifecycleWork `--fixtures` 57；EntityObservation 75、`--probe-registration` 76 |
| Enemy | LifecycleFacts 52/52（生成的计划与当时的 `examples/limb-broken-heal.plan.json` 一致；该示例已在 D-004 批次随 heal 2.0.0 删除）；CommitAudit 52/52；NativePlugin 24/24；ReceiverProbe 40/40；EntityObservation 66/66；BehaviorObservation 22/22 |
| Map / Weapon | MapContracts 通过；IdentityDispatchReview 20/20 |
| Trigger | 完整入口通过，见 [Trigger 验证记录](../ForgeTrigger/VALIDATION.md) |

GameBindings 的 `--bridge` 与 `--native` 本次没有重跑。全部是托管替身证据，没有加载 GTFO。

## 原生实例解析（2026-09-13）

Weapon 的 W1 游戏入口需要从 `SNet_Player` 取得 ForgeMap 登记的当前 `gtfo.player` 引用，SDK 此前只有拥有者内部的核验。本批在同一个 SDK 上增加三处公开接口，规则见 [Framework README](Framework/README.md#从原生实例取得引用)：

```csharp
public IReadOnlyDictionary<string, Func<object, EntityReference?>>? EntityInstanceResolvers { get; init; } // RuntimeModule
public EntityReference? ResolveEntityInstance(string kind, object instance);                              // RuntimeKernel
public bool IsEntityCurrent(EntityReference reference);                                                     // RuntimeKernel
```

版本：`RuntimeKernel.ApiVersion` 保持 2.0.0，它是与网站共享的 I-MANIFEST 版本，manifest 形状没有变化。宿主插件保持 1.2.0：这是纯增量接口，此前加入 `EntityObservers` 时同样没有递增，Map、Enemy 与 Weapon 的 `BepInDependency` 和测试都锁定 1.2.0。**代价是：若已有不含这些接口的 1.2.0 构建在外流通，Weapon 在其上会以缺失方法加载失败**；首次发布这组插件前需要维护者确认是否改为 1.3.0。

新增 `tests/EntityObservation/InstanceResolutionTests.cs`，分为注册（归属、非法 key、原子性、跨模块拒绝、只问拥有者、注销）、就绪状态、失败文本（异常消息与内部异常不外泄）、答案核验（他人命名空间、前缀相近、旧世界、拥有者拒绝）、观察保护、`IsEntityCurrent` 真假、manifest 不变七组。

隔离 `--artifacts-path` 构建，0 警告 0 错误：

| 套件 | 退出码 | 输出结尾 |

## R4：运行时枚举值端口（本批）

Q3 落地：enum 的运行期/wire 值是集合内的成员下标，而不是成员名。事件槽、字面量常量、提升输入三处一起放开；下标基准——结构参数带 inline `values` 时按该列表算，否则（含提升输入、事件/输入端口）按端口或参数指向的完整具名集合算。计划里任何位置出现成员名字符串一律 `invalid-enum` 拒绝，越界与非整数同一处理。handler 边界不变：dispatch 校验通过后，`CommandContext.Parameters`/`Inputs` 里的 enum 字段在调用 handler 前从下标换回成员名字符串，现有 handler（如 `EnemyModule.Heal` 读 `overheal_policy`）不用改。`ForgeEnemy/Native/EnemyModule.cs` 的 `damage_applied` 事实同步把 `damage_kind` 声明为可空下标（`int?`），仍然只在能判定时才发布非空值。

隔离 `--artifacts-path` 构建，0 警告 0 错误：

| 套件 | 结果 |
| --- | --- |
| Framework | 275（新增 enum 事件槽/字面量/提升输入的合法用例，越界、非整数、字符串三类拒绝用例，以及 handler 边界换名断言） |
| GraphContracts | verify.py 1770 项通过，0 失败；`EnumSetTable` 与网站 `contracts.ts` 的 22 个集合逐项一致，未发现漂移 |
| GameBindings | 默认 31；`--native` 51 |
| 宿主 | Architecture 36；HostIntegration 默认 61（含 `--host`）；PluginStartup 39；HostConfiguration 90 |
| Enemy | NativePlugin 24/24；EntityObservation 66/66；LifecycleFacts 50/52，BLOCKED 2（J-003，与 enum 无关）；**ReceiverProbe 43/44，BLOCKED 1（此前 36/44 BLOCKED 8，7 个伤害用例随 R4 恢复执行，仅剩 J-003 一项）**；**CommitAudit 68/68，BLOCKED 0（此前 62/68 BLOCKED 6，6 个伤害用例随 R4 恢复执行）**；ReceiverProbe 变体 8/8（`observation-replay`、`late-damage-retargeted` 随 R4 恢复检出）；LifecycleFacts 变体 7/7 |

`Framework --fixtures` 与 `LifecycleWork --fixtures` 仍然崩溃，但栈顶已经从 `DamageObserved.damage_kind`（R4 未开放时）变成 `HealExplicitRecipient.targets`——即本次改动已经让这两个夹具测试越过枚举端口这一关，卡在的是另一个已知且无关的缺口：网站 `Tests/Forge/fixtures/runtime` 里的合法计划仍按 D-004 之前的 Heal 单值 `targets` 形状编写，与当前需要多值输入的 Heal 合同不符（J-003，由另一工作树的 heal 合并处理，不在本批范围）。两处均用 `git stash` 在改动前的 HEAD 上重新构建验证过：改动前后这两个夹具用例都是同样的 "0 assertions passed"/未捕获异常收场，只是失败原因从 damage_kind 换成了 targets，本次改动没有引入新的回归，也没有为字符串枚举常量添加任何兼容层。
| --- | --- | --- |
| Framework | 0 | `Framework checks: 255 passed.` |
| EntityObservation | 0 | `Entity contracts: 118 passed; 0 failed. No native APIs exercised.` |
| EntityObservation `--probe-registration` | 0 | `Entity contracts: 119 passed; 0 failed.` |
| LifecycleWork `--fixtures` | 0 | `Lifecycle work: 57 assertions passed; 0 groups failed.` |
| GameBindings（默认，`Program.cs` 未改） | 0 | `PASS 31 native-module boundary assertions. Native API execution and multiplayer are not exercised by these doubles.` |
| Architecture | 0 | `PASS 36 architecture boundary assertions. No GTFO hooks, gameplay, networking or installation exercised.` |

消费方结果见 [Map 验证记录](../ForgeMap/VALIDATION.md#map5a--gtfoplayer-原生实例解析2026-09-13) 与 [Weapon 验证记录](../ForgeWeapon/VALIDATION.md)。LifecycleWork 首次运行漏传 `--fixtures`，只打印用法后退出，补参数重跑。测试自身修过两处：非法 key 用例原先先命中 `entity-namespace`，前缀用例原先依赖未注册的命名空间；两处都改的是测试输入，没有放宽 SDK。

本批没有重跑 GameBindings 的 `--fixtures` / `--bridge` / `--native`、HostIntegration、PluginStartup、HostConfiguration、GraphContracts、Enemy 与 Trigger 套件。全部是托管替身证据，没有加载 GTFO。

## D-007 阶段 B — 执行日志 sink、宿主 JSONL writer 与提级（2026-09-13）

分支 `feat/runtime-log-writer`，基于 `d09d6bb`（已含 Weapon/Map 的实例解析）。行为说明见 [README](README.md#执行日志d-007-阶段-b) 与 [Framework README](Framework/README.md#执行日志-sink-与级别d-007-阶段-b)。`Contracts.cs` 与 `RuntimeKernel.cs` 未改：内核构造重载、级别表和提级都放在新的 partial 文件 `RuntimeKernel.Logging.cs` 里。

**记录点尚未接入，玩家层现在不会写出任何业务记录。** 当前只有 Runtime 自身 provider 的级别（来自 `[Logging] Level`）。领域包的级别条目在阶段 A 随必填参数 `RegisterModule(RuntimeModule, RuntimeLogLevel)` 加入，在那之前查领域 provider 抛 `log-provider-unregistered`。

新增 `tests/RuntimeLog`，编译 SDK 源码和生产 writer，使用真实的 BepInEx `ManualLogSource`，日志只写到 `--root` 指定的新目录。14 个场景：惰性启动；首行 `log.level`；seq 连续；每 tick 限流与 `log.dropped` 计数；队列满时计数确定（让消费线程停在第一次控制台镜像）；注入 4096 字节文件上限；保留 10 个文件且不动非 jsonl 和子目录；控制台只镜像 error 与 info；按 provider 的级别矩阵与固定拒绝码；提级只接受一次、只在注册窗口内接受、不可撤销且切换限流档；停止时写完 60000 条提级记录；JSON 合法、字段顺序、无 BOM 与 CR、非 ASCII 原样写出；无 17 位 Steam64 形数字，记录也没有玩家身份字段；错误线程拒绝。

HostIntegration `--host` 新增日志调用点检查，在 IL 层检查 SDK 与宿主（Architecture 工程没有宿主 DLL 和 Cecil）。检查两件事：记录的字符串字段不接受当场拼接、格式化或 `ToString` 产生的值（直接或经一个局部变量）；SDK 与 `ForgeRuntime.Logging` 不调用 `SNet_Player.Lookup`。四个拼接 fixture 与一个 Lookup fixture 必须被检出，干净 fixture 的 8 个 setter 必须全部放行。宿主当前至少有 writer 自己的站点，因此检查不是空转。

PluginStartup 新增默认与配置级别传入宿主、trace 与未知级别在 Harmony 和宿主初始化前失败、运行中改级别不生效。HostConfiguration 新增 4 个合法值、6 个非法值、带级别的保存往返，以及默认值写出为 `Level = error`。GameBindings 的替身日志改为 `BepInEx.Logging.ManualLogSource` 形状，并链接生产 writer。

rebase 到 `d09d6bb` 之后的验证记录如下。构建使用 `dotnet build <工程> -c Release --artifacts-path <隔离目录> --disable-build-servers -p:GTFOBepInExPath=<BepInEx 目录>`，套件使用 `dotnet <隔离目录>/bin/<套件>/release/<套件>.dll <参数>`。宿主 `ForgeRuntime.csproj`、`Forge.Architecture.sln`，以及 Framework、GameBindings、LifecycleWork、EntityObservation、HostIntegration、PluginStartup、HostConfiguration、RuntimeLog 八个测试工程，退出码均为 0，输出结尾均为 `0 Warning(s)` / `0 Error(s)`。

| 套件与参数 | 退出码 | 输出结尾 |
| --- | --- | --- |
| RuntimeLog `--root <新目录>` | 0 | `Runtime log: 127 assertions passed; 0 scenarios failed.`（停止写完：`60000 elevated records: enqueue 12 ms, stop and flush 68 ms`） |
| Framework | 0 | `Framework checks: 255 passed.` |
| GameBindings（默认） | 0 | `PASS 31 native-module boundary assertions. Native API execution and multiplayer are not exercised by these doubles.` |
| GameBindings `--native <BepInEx> <ForgeRuntime.dll> <GTFO>` | 0 | `Host patches: FrameworkStateChanged, FrameworkWorldCleanup, FrameworkSessionReset, FrameworkCheckpointRestore` / `PASS 51 native-module boundary assertions. …`，宿主 Hook 精确为 4 个，GameAssembly SHA-256 `C6A5C3CD…` |
| LifecycleWork `--fixtures <网站 fixtures/runtime>` | 0 | `Lifecycle work: 57 assertions passed; 0 groups failed.` |
| EntityObservation | 0 | `Entity contracts: 118 passed; 0 failed. No native APIs exercised.` |
| Architecture | 0 | `PASS 36 architecture boundary assertions. No GTFO hooks, gameplay, networking or installation exercised.` |
| HostIntegration `--host <ForgeRuntime.dll>` | 0 | `Host integration: 61 assertions passed; 0 groups failed.` |
| PluginStartup | 0 | `Plugin bootstrap: 39 assertions passed; 0 scenarios failed.` |
| HostConfiguration | 0 | `Real BepInEx configuration: 90 assertions passed; 0 scenarios failed.` |

本批没有跑 GameBindings `--fixtures` / `--bridge`、EntityObservation `--probe-registration`、GraphContracts、Enemy、Map、Weapon 与 Trigger 套件，也没有检查内核或领域记录点的开销（记录点不存在）。以下仍未验证：游戏内 `ManualLogSource` 从后台线程调用、真实退出时序、进程强杀时丢失的队列与最后一行 `log.dropped`。以上都是托管替身、编译后元数据与本地原生签名证据，没有加载 GTFO，也没有安装。

## J-003 与 r11：字面量/单转多输入、heal 结果聚合修正（2026-09-14）

D-006①②（J-003）：schemaVersion 2 的 step 输入行只能是 `{slot, fromEventSlot}`（wired）或 `{slot, value}`（literal）二选一，两者同时出现报 `literal-with-event-source`；literal 值类型或类别（entity 一律禁止）与目标端口不符报 `literal-wrong-type`；非空的 one 输出接多输入端口时由加载器按 `RuntimeGraphContracts.ValueTypeMatches`（新拆出，忽略 cardinality）与显式 nullable 检查后原地包成一元素集合，其余端口维度不符仍按原 `port-mismatch`/`nullable-port` 拒绝。落地在 `RuntimeGraphContracts.cs`（`SameValue` 拆成精确匹配 + `ValueTypeMatches` 两个函数）、`RuntimePlan.cs`（step 输入解析改写）、`RuntimeKernel.cs`（dispatch 时按 `StepInput.Literal`/`Wrap` 取值）。

r11（heal 结果聚合，кernel 级、与 provider 无关）：`Contracts.cs` 的 `CommandResultRules.TryValidate` 对 Partial 只再要求 commit 为 confirmed 或 unknown，不再要求非空 `Facts`——一个满血目标 commit 时 `actualAmount=0` 不产生 fact，与另一个被拒绝目标合并后仍是 Partial，facts 可以为空。`RuntimeKernel.cs` 的 entrypoint 派发循环相应改为只在 rejected/failed/cancelled/expired，或 partial+unknown 时停止；partial+confirmed 不再停止，后续 step 继续执行。`EnemyModule.Heal` 与 `EnemyHealthCommit.Execute` 的 21 个拒绝码改成 `CombatContracts.cs` 里已提交的 kebab-case 形式，聚合逻辑按有无 committed 行重写（不再按 `unknown==0`/`facts.Count==0` 判断），删除 `heal-no-state-change`。

同批修正了两处被 r11 改变的既有断言：`ForgeRuntime/tests/Framework/ExecutionResultTests.cs` 里"partial without/with known commit"的两条旧断言（原假设 partial+confirmed/unknown 且空 facts 一定非法，现改为验证合法）与"partial stops the entrypoint after its invoked step"整段场景（confirmed-commit partial 不再停止，2 步都执行，2 条 fact 都发布排队）；`ForgeEnemy/tests/CommitAudit/CommitCases.cs` 的 `heal.multi-full-and-rejected-is-no-state-change`、`heal.multi-full-and-unknown-is-failed` 两个用例按 r11 改写为 `-is-partial-confirmed`/`-is-partial-unknown`。

website `Tests/Forge/fixtures/runtime` 的 27 个非法计划（不是任务文本原先假设的 28 个）逐一核对了 sha256（未直接采信既有记录）：`cases.json` d5e355d8…、`native-heal.plan.json` 475363c2…、`native-manifest.json` 731a166f…、`MANIFEST.json` e5020919… 均与站内 `MANIFEST.json` 登记值一致；27 个 `invalid/*.plan.json` 中含 `literal-with-event-source.plan.json`、`literal-wrong-type.plan.json` 两个 J-003 专属负例。

隔离构建，`Forge.Architecture.sln` 与各测试工程 0 警告 0 错误：

| 套件与参数 | 结果 |
| --- | --- |
| Framework（默认） | 274 passed |
| Framework `--fixtures <站内 fixtures/runtime>` | 302 passed（含 27 个非法计划逐条按预期码拒绝，native-heal.plan.json 正常加载） |
| Framework `--benchmark` | 通过，输出合成负载数字（非 GTFO 帧率） |
| GraphContracts `verify.py --website ..\Infini-GTFO-Model-Site`（非 `--integration`） | typescript 生成 exit 0；graph-contracts 1770 passed，0 failed；`sourceStable=true` |
| GraphContracts `mutations.py <本次生成的 vectors.json>` | control 通过，5/5 错误实现（含 `plan-skips-expansion`）全部检出 |
| GameBindings（默认） | PASS 39，BLOCKED 0 |
| GameBindings `--fixtures <站内 fixtures/runtime>` | PASS 70，BLOCKED 0（此前记录的 `fixtures.valid-plan-heal-dispatch`/`fixtures.invalid-plan-rejections` 版本比对阻塞已随 J-003 解除） |
| CommitAudit | 68/68，BLOCKED 0 |
| ReceiverProbe | 43/44，BLOCKED 1；`verify_mutations.py` 8/8 mutants 检出 |
| LifecycleFacts | 50/52，BLOCKED 2；`verify_mutations.py` 7/7 mutants 检出 |
| ForgeTrigger T1（`validate-trigger.py --mutations` 的 t1 阶段） | PASS 911 C# assertions |

**LifecycleWork `--fixtures` 本批未通过，退出码 1，0 assertions passed，6 组失败。** 根因不在 J-003/r11：`tests/LifecycleWork/WorkFixture.cs` 的 `Event()` 用固定 payload `{ target, actual_damage }` 构造 `damage_applied` 触发事件，但站内当前 `native-manifest.json` 里该 trigger 的输出端口是 `next/source/target/amount/damage_kind/limb` 且均非 `optional`——字段名早已不是 `actual_damage`（应为 `amount`），且缺 `source`/`damage_kind`/`limb`，命中 `RuntimeKernel.ValidateEvent` 的 `RuntimeJson.Shape` 检查报 `unknown-field`。这是独立于本批改动的既有失效夹具。

同日修复：`WorkFixture.Event()` 改为按目录行构造 `{ source: null, target, amount: 10, damage_kind: null, limb: null }`（与 `EnemyModule` 发布 `damage_applied` 的形状一致）。复跑 `LifecycleWork.dll --fixtures ../Infini-GTFO-Model-Site/Tests/Forge/fixtures/runtime`：构建 0 警告 0 错误，57 assertions passed，0 groups failed，退出码 0。至此 J-003 要求的 Framework、GameBindings、LifecycleWork 三组 `--fixtures` 全部通过。

**ReceiverProbe 的 `commit.kernel-unknown-no-retry` 与 LifecycleFacts 的 `integration.real-heal-death_started`/`integration.real-heal-limb_broken` 仍列 BLOCKED。** 根因是 `ForgeEnemy/tests/Shared/Blockers.cs` 的 `Heal` 常量与其在 `ReceiverProbe/Program.cs`、`LifecycleFacts` 里的引用是硬编码的无条件阻塞，文本仍写"J-003 未实现"——J-003 本批已经落地并经 Framework `--fixtures`/CommitAudit 验证，但这三个用例没有随之自动解除，因为阻塞判断本身没有读取任何运行时状态。解除需要新写用 `LocalPlan` 从内核注册表构造 事实→Heal 计划并恢复 +5HP 断言（README 里已写明的解除条件），属于新增测试基础设施，不在本次 J-003 loader 实现 + heal 合并的范围内，留作后续修复项。`GameBindings/Program.cs` 里同类的 `HealBlocker`/`bridge.configured-heal-plan-commits-5hp` 未见于本批 `--fixtures`/默认运行的失败或阻塞列表中（该 bridge 场景未在本次跑的两个模式里触发），未重新核实，一并留作后续检查项。

`ForgeTrigger/tools/validate-trigger.py --mutations` 的 `pure` 与 `independent` 两个阶段在 TypeScript 向量生成步骤失败（`previewLogicPrimitive is not a function`、`preview is not a function`），栈顶都在站内 `site/forge` 的 TS 导出函数缺失，与本批改动的 C# 文件无关，也不是 J-003/r11 涉及的路径；只有 t1 阶段（消费共享 SDK 的 C# 断言）被跑到并通过。这个 TS 侧失败未进一步排查。

## U-RUNTIME/R4 调研：完整 lowering 卡在计划格式（2026-09-14）

按工作顺序（heal 合并 → J-003 → R4 → player bindings）开始 R4 完整 lowering。范围是契约里点名的四项：步骤间数据边、pure 节点运行期求值（selector/condition/modifier）、条件分支、control 节点。逐项核对源码后结论是：**这四项在当前 I-PLAN schemaVersion 2 wire 格式里都不存在，C# 与网站两侧完全对称地卡在同一处，不是本仓单独能补的缺口。**

核对依据（只读，未改动网站仓库）：

- `RuntimePlan.cs`（本仓）：`Load()` 的 `node-kind` 检查只接受 `capability.kind == "trigger"` 或 `"action"`；步骤输入 `{slot, fromEventSlot}` 的 `fromEventSlot` 只按 `entryId` 对应触发器的事件输出解析，不存在"从前一步输出取值"的形状；执行链每个节点最多一条后继（`entrypoint.steps` 是数组，不是带分支的图）。
- 网站 `site/forge/runtime-compiler.ts`（只读核对，未改动）：`linearContract` 与本仓逐条对称（`kind === 'trigger' || 'action'`、`executionOutputs.length <= 1`）；`foldPureNodes` 只把 locked 内建 pure 节点在**编译期**常量折叠，端口含 `entity`/`resource`/`handle` 时直接不折叠且无法编译（`worldPortTypes` 排除折叠）；主循环里 `requireRuntime(data.from.node === entryId, ...)`——数据输入必须直接来自入口触发器，逐字禁止步骤间数据边；`execution.get(current).length <= 1` 的注释原文是 "Runtime execution branch requires control lowering"，即分支本就是网站自己标注的未来扩展点，当前直接拒绝编译。
- 网站 `site/forge/runtime-contracts.ts`：`ForgeRuntimeInputBinding` 类型只有 `{slot, fromEventSlot}` 与 `{slot, value}` 两种变体，没有"引用另一步输出"的第三种形状；`ForgeRuntimeStep`/`ForgeRuntimeEntrypoint` 是纯线性数组，没有 kind 判别字段，无法表达 control 节点。
- I-CATALOG（`catalog/capability-catalog.json`）里 `selector`/`condition`/`modifier`/`control` 四类能力已经有 44/63/37/46 条定义（authoring 层的端口与参数合同已存在），但绑定角色（`role`）目前只有 `execute`（action）与 `observe`（trigger）两种（`RuntimeRegistry.cs:153`），没有给 pure 求值 handler 用的第三种角色；这是注册合同层面的缺口，不只是计划文件格式的缺口。

即：网站编译器与本仓加载器目前是同一套线性子集的两份独立实现，彼此对称地拒绝这四项，而不是网站已经走在前面、本仓没跟上。按任务约束（不发明格式），本批没有为这四项写任何 C# 代码；已确认的 recipient-policy 参数、enum 值端口两个相邻缺口不在本次任务范围内，分别按契约原文维持现状（前者显式等待与网站同批放开；后者复核后发现 `RuntimeGraphContracts.RuntimeValueTypes` 与网站 `forgeRuntimeValuePortTypes` 均已把 `enum` 列入端口允许类型，属既有实现，非本批改动）。

本批只做了验证性复跑，确认上述调研没有引入回归，隔离产物目录 `%TEMP%\forge-r4-artifacts-20260914`，`GTFO_BEPINEX_PATH` 指向 `Forge-MapEditor-QA` profile（仅供编译引用）：

```powershell
dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release --artifacts-path <隔离目录> --disable-build-servers -p:GTFOBepInExPath=<BepInEx目录>
dotnet build Forge.Architecture.sln -c Release --artifacts-path <隔离目录> --disable-build-servers -p:GTFOBepInExPath=<BepInEx目录>
dotnet build ForgeRuntime/tests/Framework/Framework.csproj -c Release --artifacts-path <隔离目录> --disable-build-servers -p:GTFOBepInExPath=<BepInEx目录>
dotnet build ForgeRuntime/tests/GameBindings/GameBindings.csproj -c Release --artifacts-path <隔离目录> --disable-build-servers -p:GTFOBepInExPath=<BepInEx目录>
dotnet build ForgeRuntime/tests/LifecycleWork/LifecycleWork.csproj -c Release --artifacts-path <隔离目录> --disable-build-servers -p:GTFOBepInExPath=<BepInEx目录>
dotnet build ForgeEnemy/tests/CommitAudit/CommitAudit.csproj -c Release --artifacts-path <隔离目录> --disable-build-servers -p:GTFOBepInExPath=<BepInEx目录> -p:ForgeFrameworkAssembly=<隔离目录>\bin\ForgeRuntime.Framework\release\ForgeRuntime.Framework.dll
python ForgeRuntime/tests/GraphContracts/verify.py --website ..\Infini-GTFO-Model-Site
```

| 套件与参数 | 结果 |
| --- | --- |
| `ForgeRuntime/ForgeRuntime.csproj` 构建 | 0 警告 0 错误 |
| `Forge.Architecture.sln` 构建 | 0 警告 0 错误 |
| Framework（默认） | 274 passed |
| Framework `--fixtures ../Infini-GTFO-Model-Site/Tests/Forge/fixtures/runtime` | 302 passed |
| GameBindings（默认） | PASS 39，BLOCKED 0 |
| GameBindings `--fixtures ../Infini-GTFO-Model-Site/Tests/Forge/fixtures/runtime` | PASS 70，BLOCKED 0 |
| LifecycleWork `--fixtures ../Infini-GTFO-Model-Site/Tests/Forge/fixtures/runtime` | 57 assertions passed，0 groups failed |
| CommitAudit | 68/68，BLOCKED 0 |
| GraphContracts `verify.py --website ..\Infini-GTFO-Model-Site`（非 `--integration`） | typescript 生成 exit 0；graph-contracts 1770 passed，0 failed；`sourceStable: true`，`changedSources: []` |

以上数字与 09cdb70/17b3078（J-003、r11）落地时记录的一致，确认本批调研没有改动任何生产源码，也没有引入回归。本批没有跑 HostIntegration、PluginStartup、HostConfiguration、EntityObservation、ReceiverProbe、LifecycleFacts、RuntimeLog、Trigger、Map、Weapon 套件。以上都是托管替身与编译后元数据证据，没有加载 GTFO。

R4 完整 lowering 的四项（步骤间数据边、pure 节点运行期求值、条件分支、control 节点）需要的计划格式扩展提案见本次交接消息，不写入本文件（本文件按 §2.8 只放带日期的运行记录，不放"当前结论"之外的规格文字；规格提案是待网站与用户裁决的内容，归属 FORGE-FRAMEWORK.md §8.1/§3.2，由网站会话落笔）。

## D-017 R4-a — schemaVersion 3 运行时内核（2026-09-14）

按 FORGE-FRAMEWORK.md §3.2「I-PLAN schemaVersion 3（D-017 R4-a）」与 §6 U-RUNTIME 落地 v3 wire 格式：入口 `start`、步骤 `nodeKind`（`action`/`control`/`pure`）与 `successors`、`{slot, fromStepSlot}` 数据边、`evaluate` 角色与 `RuntimeModule.Evaluators`、Kahn 拓扑序校验、SDK 自有的 `forge.contract.control` 分支合同，以及 ForgeTrigger 的 `forge.condition.predicate.compare` 求值绑定。**v2 不再读取**，没有任何兼容分支。

验证入口与隔离产物目录 `%TEMP%\forge-r4a-v3-20260914`，`GTFO_BEPINEX_PATH` 指向 `Forge-MapEditor-QA` profile（仅供编译引用）：

```powershell
$out = "$env:TEMP\forge-r4a-v3-20260914"
$fx  = "..\Infini-GTFO-Model-Site\Tests\Forge\fixtures\runtime"
dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release --artifacts-path $out --disable-build-servers -p:GTFOBepInExPath=$env:GTFO_BEPINEX_PATH
dotnet build ForgeRuntime/tests/Framework/Framework.csproj -c Release --artifacts-path $out --disable-build-servers -p:GTFOBepInExPath=$env:GTFO_BEPINEX_PATH
dotnet build ForgeRuntime/tests/GameBindings/GameBindings.csproj -c Release --artifacts-path $out --disable-build-servers -p:GTFOBepInExPath=$env:GTFO_BEPINEX_PATH
dotnet build ForgeRuntime/tests/LifecycleWork/LifecycleWork.csproj -c Release --artifacts-path $out --disable-build-servers
dotnet build ForgeRuntime/tests/Architecture/Architecture.csproj -c Release --artifacts-path $out --disable-build-servers
dotnet $out/bin/Framework/release/Framework.dll
dotnet $out/bin/Framework/release/Framework.dll --fixtures $fx
dotnet $out/bin/GameBindings/release/GameBindings.dll
dotnet $out/bin/GameBindings/release/GameBindings.dll --fixtures $fx
dotnet $out/bin/GameBindings/release/GameBindings.dll --bridge E:\SteamLibrary\steamapps\common\GTFO
dotnet $out/bin/GameBindings/release/GameBindings.dll --export-manifest <scratchpad>\v3-native-manifest.json
dotnet $out/bin/LifecycleWork/release/LifecycleWork.dll --fixtures $fx
dotnet $out/bin/Architecture/release/Architecture.dll
```

| 套件与参数 | 结果 | 退出码 |
| --- | --- | --- |
| 上述五个构建（宿主 + 四个测试工程） | 均 `0 Warning(s)` / `0 Error(s)` | 0 |
| Framework（默认） | `Framework checks: 336 passed.` | 0 |
| Framework `--fixtures $fx` | `Framework checks: 379 passed.`（加入 `successor-pure-target` 负例后） | 0 |
| GameBindings（默认） | PASS 39，BLOCKED 0 | 0 |
| GameBindings `--fixtures $fx` | PASS 85，BLOCKED 0（加入 `successor-pure-target` 后；本批从「PASS 32 + 2 BLOCKED」变为全通：站内夹具已删掉 D-009 的 `grantedPermissions` 负例，本仓不再读计划内的 `validPlan` 键） | 0 |
| GameBindings `--bridge <GTFO 根目录>` | PASS 74，BLOCKED 0 | 0 |
| GameBindings `--export-manifest` | 导出 8499 字节；sha256 `4d6c74bb819efc36a3be5fe213670f71dd3ac4fb59c6026712875c8db612c4a9` | 0 |
| LifecycleWork `--fixtures $fx` | `Lifecycle work: 57 assertions passed; 0 groups failed.` | 0 |
| Architecture | `PASS 41 architecture boundary assertions.` | 0 |

导出的运行期清单与站内 `Tests/Forge/fixtures/runtime/native-manifest.json` 逐字段相等（四家 provider、7 条 capability、7 条 binding、7 行 bindingSupport、permissions 与 `runtime` 身份完全一致）。这是本批最关键的一条证据：宿主 `ForgeRuntime.csproj` 现在链接 ForgeTrigger 源码并注册 `forge.module.trigger`，SDK 自己声明 `forge.contract.control`，因此真实注册表与网站编译器写出的夹具不再有偏差。

本批新增/改写的负例与断言（只列 R4-a 相关的重点）：

- **拒绝码单元覆盖**：`node-kind`（步骤 kind 与能力 kind 不符、control 步骤自称 pure、pure 步骤绑 action 能力）、`control-unsupported`（R4-a 只路由 `forge.control.flow.branch`）、`successor-shape`（后继帧长度/越界）、`successor-index`（后继只能向后）、`entry-start`（`start` 必须落在 action/control 步骤内）、`pure-successor`（pure 步骤没有后继）、`unreachable-step`、`from-step-kind`、`from-step-port`、`from-step-slot`、`execution-slot`、`port-mismatch`、`event-port-missing`、`missing-input`、`missing-evaluator`、`unused-evaluator`。
- **Kahn 顺序**：正例由夹具按同一算法现算并逐项断言；`step-order` 的负例在本仓线性图里**无法手工构造**（每个 action/control 步骤恰好一个前驱，凡是边都向前且步骤全部可达的数组必然就是规范序）。该码由站内 `invalid/step-order.plan.json` 覆盖，`--fixtures` 运行会对同一段代码断言，`ForgeRuntime/tests/Framework/Program.cs` 里写明了这个取舍。
- **分支路由派发**：`then` 命中 action 命令、`otherwise` 收敛回执且零命令，两种结果都断言，常数真/常数假求值器都过不了。
- **真实 `compare` 求值**：Framework 用 `ForgeTrigger.ModuleDefinition.Create()` 跑 7 组 `(left, right, operator, tolerance, expected)`，覆盖容差把 `lte`/`gt` 翻转的两种方向，操作数以成员集下标上线、handler 收到成员名。断言的是命令是否真的产生，常数求值器或忽略 operator 的实现都会失败。
- **枚举帧**：枚举端口的 `valueSet` 取 SDK 表内的声明下标；测试夹具用反射读同一张表，不再手抄集合顺序。

本轮**没有**跑 HostIntegration、PluginStartup、HostConfiguration、RuntimeLog、EntityObservation、Map、Weapon 套件；`ForgeRuntime/ForgeRuntime.csproj` 新链接了 ForgeTrigger 源码，这些套件理论上不受影响，但没有实测，不在此声称。ForgeEnemy 侧同批：LifecycleFacts 52/52、ReceiverProbe 48/48、CommitAudit 68/68，退出码均 0；NativeEvidence 544/544 与其负例 22/22 只在实现批次的工作树里跑过，下面的隔离复跑没有包含。

**审查修正与隔离复跑（同日）**。对照网站 `validateForgeRuntimePlan`，加载器补了两处检查：
- 非 `null` 后继指向 `pure` 步骤记 `successor-index`。此前只查下标方向，向后指向纯步骤的计划会被加载。网站夹具新增 `invalid/successor-pure-target.plan.json` 覆盖这一条。
- 步骤绑定角色记 `binding-role`：`action`、`control` 必须是 `execute`，`pure` 必须是 `evaluate`；`pure` 的 binding 没有已注册 evaluator（planned）记 `missing-evaluator`，`action` 缺 handler 仍记 `action-handler`。

复跑方式：`git worktree add --detach` 建 HEAD 工作树，拷入本批全部未提交文件后按上面的命令构建；ForgeEnemy 三个套件另带 `-p:ForgeFrameworkAssembly=<SDK dll>`，并各自传入报告路径。结果：
- 9 个构建全部 0 警告 0 错误；
- 默认套件：Framework 336、GameBindings 39、Architecture 41，`--bridge` 74；
- 导出清单 sha256 仍是 `4d6c74bb…`；
- `--fixtures`（网站按该导出重新生成、负例 41 条的夹具）：Framework 379、GameBindings 85、LifecycleWork 57；
- LifecycleFacts 52、ReceiverProbe 48、CommitAudit 68；
- 退出码全部 0。

## 复跑

从仓库根目录执行。宿主与 GameBindings 需要 `GTFO_BEPINEX_PATH` 或 `-p:GTFOBepInExPath=<BepInEx 目录>`。构建输出用 `--artifacts-path` 指向隔离目录，绝不写入已安装的插件目录。

```powershell
dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release
dotnet build Forge.Architecture.sln -c Release
dotnet run --project ForgeRuntime/tests/Architecture/Architecture.csproj -c Release --no-build
dotnet run --project ForgeRuntime/tests/Framework/Framework.csproj -c Release -- --fixtures ../Infini-GTFO-Model-Site/Tests/Forge/fixtures/runtime
dotnet run --project ForgeRuntime/tests/Framework/Framework.csproj -c Release -- --benchmark
dotnet run --project ForgeRuntime/tests/GameBindings/GameBindings.csproj -c Release -- --fixtures ../Infini-GTFO-Model-Site/Tests/Forge/fixtures/runtime
dotnet run --project ForgeRuntime/tests/GameBindings/GameBindings.csproj -c Release -- --bridge <GTFO 游戏根目录>
dotnet run --project ForgeRuntime/tests/HostIntegration -c Release
dotnet run --project ForgeRuntime/tests/LifecycleWork/LifecycleWork.csproj -c Release -- --fixtures ../Infini-GTFO-Model-Site/Tests/Forge/fixtures/runtime
dotnet run --project ForgeRuntime/tests/PluginStartup -c Release
dotnet run --project ForgeRuntime/tests/HostConfiguration -c Release
dotnet run --project ForgeRuntime/tests/HostIntegration -c Release -- --host <隔离目录>/bin/ForgeRuntime/release/ForgeRuntime.dll
dotnet run --project ForgeRuntime/tests/RuntimeLog -c Release -- --root <仓库外的新目录>
```

`LifecycleWork` 只从夹具目录读 `cases.json` 与它指向的 manifest（provider/binding/permission 的声明来源），计划由内核自己的注册表现造，因此 v3 加载器与 SDK 模块始终是同一份。

诊断侧套件的复跑见 [Development 验证记录](../ForgeDevelopment/VALIDATION.md#复跑)。聚焦回归各有自己的 README：[宿主启动](tests/PluginStartup/README.md)、[真实配置](tests/HostConfiguration/README.md)、[已加载工作清理](tests/LifecycleWork/README.md)。

`--benchmark` 只做小型可重复的合成负载收据：注册与 LoadPlan 各一次，固定数量的 dispatch、scheduled pulse 与 lease 请求，各自的耗时、分配与预算拒绝原因。**这些是桌面替身数字，不是 GTFO 帧率或联机性能证明。**

## 历史：1.1.x 采集版

1.1.0 的实机报错位于 `DMD<LG_ZoneJob_CreateZoneForStaticLevel::Build>` 的 native→managed trampoline。原生映射显示该 Build 的 RVA 0x34E9A0 与 247 个方法条目共享，`GetShadowRenderGroups` 的 RVA 0x46E980 与 95 个条目共享——**签名正确无法保证 detour 安全**。1.1.1 改用明确的非共享入口列表，`NativeContracts` 增加同一游戏版本 dump.cs 的共享地址检查。这条结论对后续所有原生 Hook 仍然有效。

1.1.0 的离线检查曾记录：Release 构建 0 警告 0 错误；报告核心与后台写入 32 项；项目规则与文件采样 34 项；场景清单 8 项；可选遥测订阅生命周期 56 项；帧采样 7 项；Python 报告分析、日志导入与运行对照 18 项；原生程序集契约 109 个 QOL hook、254 个制作端 hook 与 9 个内嵌图标检查通过。这些是当时版本的历史快照，不代表当前源码，也从未执行原生 detour。原版生成一致性、实机采集开销、连续进退关、主客机以及 R7D2 与 CullingCluster 的根因仍未验证。

## 边界

以上全部是托管逻辑、编译后 DLL、静态元数据与合成 fixture 的证据。没有执行原生 GTFO 方法、没有 detour、没有多人、没有安装。测试未通过时不能把已有发行包当成新构建成功；构建其他模块时也用自己的隔离输出目录，不写别人的 `bin/obj`。
