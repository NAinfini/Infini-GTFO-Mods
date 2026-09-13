# ForgeRuntime 公共 SDK 1.0.0

本目录由 `ForgeRuntime.Framework.csproj` 单独编译成 `ForgeRuntime.Framework.dll`，生产宿主和全部模块共享这一个程序集。它提供与 GTFO/Unity 类型无关的注册、计划验证和唯一模拟队列；实际游戏绑定在相邻的 `GameBindings/` 与各领域包里。网站侧的 TS 合同与共同正反例在模型网站仓库的 `site/forge/runtime-contracts.ts` 与 `Tests/Forge/fixtures/runtime/`。

当前开发 Runtime 身份是 `forge.runtime / 1.2.0`，SDK API 1.0.0，游戏 build `20403457`。未经游戏验收的绑定一律保持 `implementation-only`。仓库整体状态见 [ARCHITECTURE.md](../../ARCHITECTURE.md)，未完成批次见 [Runtime 实施计划](../IMPLEMENTATION-PLAN.md)，公开生命周期的完整时序与限制见 [HOST-LIFECYCLE.md](HOST-LIFECYCLE.md)。

共享 `CombatContracts.cs` 当前注册 **5 个** canonical 定义（承伤事实、治疗动作、生命变化事实、死亡流程、肢体破坏），**0 个** binding。Enemy 因此持有 5 个 binding 与 5 个原生 Hook，Runtime 保留 4 个世界与会话 Hook。

## 宿主生命周期

`Plugin.Runtime` 是唯一内核入口；`Plugin.CanExecuteGameplay` 提供模拟线程上的当前阶段与主机权威门槛，**不代替目标、权限和成本校验**。宿主首个 FixedUpdate 调用 `StartRuntime`，在导出实际 manifest 与加载离线计划之前冻结注册；失败锁存，不逐帧重读。`StopRuntime` 清除工作并使保留的内核引用不能继续执行。

模块在依赖 Runtime 的插件 Load 中登记，用返回句柄的 `ObserveLifecycle` 观察 Snapshot / StartupChanged / WorldChanged / TickAdvanced。通知有界、只读、按模块清理；回调错误被隔离而不是关闭正常玩法。

## 模块接入

在首个 Runtime FixedUpdate 之前，从公开的 `Plugin.Runtime` 取得宿主，调用 `RegisterModule(RuntimeModule)`；第一方模块也走同一个接口。模块声明一个 provider、自己拥有的 capability，以及自己提供的 bindings，结构与网站 ForgeRegistry 完全一致。扩展 bindings 可以引用已注册的共同 canonical capability，不重新声明也不覆盖它。**计划词汇中的 planned 不能注册成可执行能力。**

`RuntimeModule` 的 `Handlers` 以 `binding.handler` 字符串映射同步 `CommandHandler`。`EntityResolvers` 按精确对象 ID 前缀注册，例如 `gtfo.enemy` 对应 `gtfo.enemy:7`；ID 始终是字符串，完整引用还包含 worldEpoch 与 lifeEpoch。接收者有效性和具体游戏接收能力由所属模块再次检查，核心不按阵营或实体种类推断治疗与伤害目标。

注册返回 `RuntimeModuleHandle`：

- `Publish(RuntimeEvent)` 只能发布自己的绑定；输入被复制，重复身份不会再次执行。返回 queued 只表示已入队，不表示游戏动作已提交。
- `CancelScope(scopeId)` 只取消该模块拥有的来源作用域，同名的其他模块作用域不受影响。
- `Schedule(...)` 与 `AcquireNumericLease(...)` 是同一句柄上的定时与状态入口，见下文。
- `Dispose()` 只注销自身。如果其他模块仍引用它的公共定义或绑定，整个注销被拒绝。只在启动前尚未冻结注册时允许注销后再登记；同 ID 新登记不会复活旧计划或旧句柄，Ready 之后不开放模块热加载。

这是受信模块之间的所有权约束，**不是任意第三方 DLL 的安全沙箱**。所有修改与分发都在宿主模拟线程调用；处理器执行过程中不能注册、注销或替换世界与计划，但可以取消或释放自己拥有的句柄以及申请新的 schedule 与 lease（仍走相同的因果深度和共享配额）。

## 实体观察

模块可以在 `RuntimeModule.EntityObservers` 中登记观察函数，由 `RuntimeKernel.InspectEntity` / `InspectEntities` 消费。注册时校验回调、命名空间归属、已拥有的 resolver、重复冲突和容量，失败不部分改变 Registry；注销时清除该模块拥有的观察器并释放委托引用。

返回的快照不可变、引用精确，并显式标注 complete / partial / rejected。**空的完整请求不等于观察不可用。** actor 永远不在 self / source / owner / instigator / event-target 之间回退；关系是有向的，实体种类与伤害治疗极性都不隐含阵营。观察回调是只读的：它不能变更内核，也不能在实体观察期间注销生命周期订阅（普通清理、停止后清理与生命周期回调自注销仍然允许）。

查询完成性只覆盖显式请求的引用，**从不代表扫描了整个世界或某个空间区域**。世界枚举与分页查询属于后续 R3 工作。

## 定时脉冲

`Schedule` 返回 `ScheduleResult`：`Status`（`scheduled` / `duplicate` / `rejected`）、`Code` 和**可空**的 `Handle`。只有成功时才是 `RuntimeScheduleHandle`（`NextTick`、`DispatchedPulses`、`SkippedPulses`、`Status`/`Code` 和幂等的 `Cancel()`）；重复（`duplicate-schedule`）、同 ID 改定义（`schedule-id-conflict`）和各种拒绝的 `Handle` 都是 null，此时不产生任何定时工作。

`PulseSchedule` 必须显式给出首次时机（`Immediate` / `AfterInterval`）、错过策略（`SkipMissed` / `CatchUp`），并且至少有一个有限上界：`MaxPulses`（≤65536）或 `LifetimeTicks`（结束点不包含，两者同时存在取最早边界）。没有上界的 interval、零 interval、越过 JavaScript 安全整数的到期 tick、lifetime 内脉冲数超过 65536，全部明确拒绝。首脉冲落在结束点之后时仍返回 `Status = scheduled`，但 `Handle` 已经是 `Status = completed / Code = no-pulses-before-end`，没有排队 job，也不补脉冲。

- `SkipMissed` 只执行最新到期的一次 occurrence，用 `SkippedPulses` 和 `missed-pulses` 收据累计跳过数，结束点之后不补历史动作。
- `CatchUp` 只在同一实际 Registry 中 Trigger 及其全部 Action 都声明 `capability.parameters.scheduleReplay = "fixed-inputs"` 时接受，并按 due tick 顺序补发原定结束前的有限 fixed-input backlog。缺声明、需要世界查询或空间选择的效果只能使用 `SkipMissed`。声明的真实性由受信模块负责，核心不推断某效果是否可重放，也不为通过测试添加声明。
- 创建时捕获已加载的 plan 集合：之后载入的 plan 不会追溯加入，卸载任一被捕获的 plan 会在下一次 `Advance` 取消该 schedule。
- 配额：活跃 schedule 上限 256，单 schedule 计划 pulse 上限 65536，每次 `Advance` 最多 64 个 scheduled pulse，捕获 entity 引用最多 16 且不截断。同时受 queue、plan、events、commands、causal、history 的原有配额约束。更低一级的预算优先拒绝或延后，同一 tick 重复 `Advance` 不会重置任何预算。
- 事件身份包含 provider、模块 generation、world、source、scope、schedule id 的摘要与 occurrence index；queued/dispatched 不是 committed。其它工作占用同一 occurrence 身份时报 `event-id-conflict`，ledger 真正写满时报 `event-history-budget`；两者都停止该 schedule，不重试、不二次执行，也不驱逐已记录身份。
- 第一个 pulse 的 handler 使捕获的 source 或 target 进入新 life 之后，剩余 catch-up 不再调用旧对象的 handler；该 schedule 以 `stale-entity` 收据结束，不会逐个 pulse 反复失败。

## 数值来源租约

`AcquireNumericLease(NumericLeaseRequest)` 返回 `NumericLeaseResult`：`Status`（`acquired` / `duplicate` / `rejected`）、`Code` 和**可空**的 `Handle`。只有成功时才是 `RuntimeStateLeaseHandle`（`ExpiresAtTick`、幂等的 `Release()`）。定义必须已通过同一个 Registry 注册为 `kind = "state"` 且 `parameters.valueType = "numeric-contribution"`，版本严格锁定；未知定义报 `state-definition`，缺类型的定义报 `state-value-type`。

唯一 key 是 provider + 完整 target（含 world/life）+ 完整 source 或 null + scope + definition + stackGroup。同一 key 的第二份贡献以 `state-source-conflict` 拒绝，不隐式 refresh、不叠加、不替换；不同 stackGroup、不同 source、不同 life 之间互相隔离。

读取由 `EvaluateNumericState(target, definitionId, stackGroup, baseValue)` 完成：每次从传入的 base 重新计算 `(base + Σ additive) × Π multiplier`，按稳定的 source 顺序排序，因此结果与获取顺序无关；溢出拒绝，零乘数精确表示，**移除贡献不做除法回滚**。

`DurationTicks` 为正，到期点不包含。到期、release、来源或目标的 life 变化、world 变更、模块注销、作用域取消都会移除贡献。活跃上限 512，超过以 `state-lease-budget` 拒绝。申请与读取都要求宿主已以 `isHost = true` 推进过该世界。核心不写游戏属性、不按阵营或实体种类推断目标，也不承诺已提交副作用的回滚。

## 宿主边界

`ExportManifest()` 导出实际注册快照。`LoadPlan(json, grantedPermissions)` 从网站的离线计划重新验证精确版本、领域、绑定闭包、显式权限和端口；权限由宿主提供，**不能从文件自身反读自授**。当前只支持 host 的单 Trigger → 线性 Action，固定参数、直接事件输入、失败停止当前入口。未知控制流、动态结果依赖、recipient-policy、未实现的数据引用与可变端口执行都明确拒绝。

`BeginWorld(epoch)` 需要递增的安全整数 epoch，取消所有旧世界的排队事件并清理去重与取消记录；**这不是检查点或网络恢复**。`Advance(simulationTick, isHost)` 使用游戏提供的模拟时间，按 due tick 与入队序号调度。重复 tick 不会重置预算，积压以有界延后保留；非主机不调用 handler，世界内主机迁移明确不支持。无订阅的绑定可以用 `HasSubscribers(bindingId)` 在游戏 hook 中提前返回。

`CommandContext` 携带 plan 与 resource revision 与 nodeId、原始事件与根因果、命令 ID、world epoch、计划与执行 tick、来源和显式 Inputs。处理器通过 `Succeeded(outputs, facts)`、`Rejected(code, detail)` 或 `Failed(code, detail)` 返回。请求量与实际量由领域结果区分，实际变化为 0 仍可成功。提交后的事实只能由该处理器所属模块发布并继承真实因果深度；异常不会自动重试，也不承诺已提交副作用的回滚。

`TickResult` 另带 `Schedules` 和 `StateLeases` 收据，但**只包含该次 `Advance` 期间**产生的条目。主动调用 `Cancel()`、`Release()` 或 `CancelScope` 不保证出现在之后的 `TickResult` 里；这些调用应读方法自身的返回值和句柄的 `Status`/`Code`。

## 预算

运行预算由已锁定的 manifest 约束：入口 32、每入口 128 步、总 512 步；每 tick 128 事件 / 512 命令、队列 1024、因果深度 16，作者计划可以要求更低的上界。单事件 64 KiB，命令最多 128 事实、总结果 256 KiB；每世界去重账本 65536 项且不驱逐，耗尽时明确拒绝新事件（包括新的 schedule 与 lease）。定时与状态另有 256 活跃 schedule、65536 计划 pulse、每 tick 64 scheduled pulse、16 捕获引用、512 活跃 lease 的上限。所有 epoch 与 tick 限制在 JavaScript 安全整数范围内。

## 当前不支持的边界

只支持 host 的单 Trigger → 线性 Action。Control 与 Selector 的嵌套图、可变历史世界快照、完整 VM 都不在当前执行范围，也没有第二套内核、时钟、Registry 或兼容入口。没有持续效果（Buff 层级的作者 UI、refresh 策略、属性回写或原生 modifier lowering）；numeric lease 只是 SDK 内部的重算贡献记录，不代表游戏属性已改变。`BeginWorld` 不处理断线重连、状态同步或多人复制——整个网络层是 [Runtime 实施计划](../IMPLEMENTATION-PLAN.md) 的 F3N 范围，尚未开工。

上述任何测试通过都不证明游戏内注入、多人复制、全部状态与周期效果或性能已验收。复跑命令见 [VALIDATION.md](../VALIDATION.md)。
