# ForgeRuntime 公共 SDK 1.0.0

本目录提供与 GTFO/Unity 类型无关的注册、计划验证和唯一模拟队列。实际游戏模块位于相邻 `GameBindings/`；网站的 TS 合同与共同正反例位于模型网站仓库 `site/forge/runtime-contracts.ts` 和 `Tests/Forge/fixtures/runtime/`。当前开发 Runtime 身份为 `forge.runtime / 1.2.0`，游戏 build `20403457`；未经游戏验收的绑定保持 `implementation-only`。

## 模块接入

从公开 `Plugin.Runtime` 取得宿主，调用 `RegisterModule(RuntimeModule)`；第一方模块也调用同一个接口。模块声明一个 provider、自己拥有的 capability，以及自己提供的 bindings，结构与网站 ForgeRegistry 完全一致。扩展 bindings 可以引用已经注册的共同 canonical capability，不重新声明或覆盖它。计划词汇中的 planned 不能注册成可执行能力。

`RuntimeModule` 的 `Handlers` 以 binding.handler 字符串映射同步 `CommandHandler`。`EntityResolvers` 按精确对象ID前缀注册，例如 `gtfo.enemy` 对应 `gtfo.enemy:7`；ID始终是字符串，完整引用还包含 worldEpoch/lifeEpoch。接收者有效性和具体游戏接收能力由所属模块再次检查，核心不按阵营或实体种类推断治疗/伤害目标。

注册返回 `RuntimeModuleHandle`：

- `Publish(RuntimeEvent)` 只能发布自己的绑定；输入被复制，重复身份不会再次执行。返回 queued 仅表示已入队，不表示游戏动作已提交。
- `CancelScope(scopeId)` 只取消该模块拥有的来源作用域；同名的其他模块作用域不受影响。
- `Schedule(RuntimeEvent template, PulseSchedule spec)` 与 `AcquireNumericLease(NumericLeaseRequest)` 是同一句柄上的定时/状态入口，见下文。
- `Dispose()` 仅注销自身。如果其他模块仍引用其公共定义/绑定，则完整拒绝注销。注销后再注册同ID不会复活旧计划或旧句柄。

这是受信模块之间的所有权约束，不是任意第三方 DLL 的安全沙箱。所有修改与分发在宿主模拟线程调用；处理器执行过程中不能注册/注销或替换世界/计划，但可以取消/释放自己拥有的句柄以及申请新 schedule/lease（仍走相同因果深度和共享配额）。

## 定时脉冲

`Schedule` 返回 `ScheduleResult`：`Status`（`scheduled` / `duplicate` / `rejected`）、`Code` 和**可空** `Handle`。只有成功时为 `RuntimeScheduleHandle`（`NextTick`、`DispatchedPulses`、`SkippedPulses`、`Status/Code` 和幂等 `Cancel()`），重复（`duplicate-schedule`）、同ID改定义（`schedule-id-conflict`）和各种拒绝的 `Handle` 都是 `null`，此时不产生任何定时工作。`PulseSchedule` 必须显式给出首次时机（`Immediate`/`AfterInterval`）、错过策略（`SkipMissed`/`CatchUp`），并且至少有一个有限上界：`MaxPulses`（≤65536）或 `LifetimeTicks`（结束点不包含，两者同时存在取最早边界）。没有上界的 interval、零 interval、越过 JavaScript 安全整数的到期 tick、lifetime 内脉冲数超过 65536 都明确拒绝；首脉冲落在结束点之后时仍返回 `Status = scheduled`，但 `Handle` 已经是 `Status = completed / Code = no-pulses-before-end`，且没有排队 job，也不补脉冲。

- `SkipMissed` 只执行最新到期的一次 occurrence，用 `SkippedPulses` 和 `missed-pulses` 收据累计跳过数，结束点之后不补历史动作。
- `CatchUp` 只在同一实际 Registry 中 Trigger 及其全部 Action 都声明 `capability.parameters.scheduleReplay = "fixed-inputs"` 时接受，并按 due tick 顺序补发原定结束前的有限 fixed-input backlog；缺声明、世界查询或空间选择只能使用 `SkipMissed`。声明由受信模块负责真实性，核心不推断某效果是否可重放，也不为通过测试添加声明。
- 捕获创建时已加载的 plan 集合：之后载入的 plan 不会追溯加入，卸载任一捕获 plan 会在下一 `Advance` 取消该 schedule。每个下一 pulse 复用同一个优先队列位置，cancel/完成/过期立即释放。
- 配额：活跃 schedule 上限 256，单 schedule 计划 pulse 上限 65536，每个 tick（每次 `Advance`）最多 64 个 scheduled pulse，捕获 entity 引用最多 16 且不截断；同时受 queue/plan/events/commands/causal/history 原有配额约束。更低的一级预算（per-plan 队列/事件/命令、每 tick pulse）优先拒绝或延后，同一 tick 重复 `Advance` 不会重置任何预算。
- 事件身份包含 provider、模块 generation、world、source、scope、schedule id 的摘要与 occurrence index；queued/dispatched 不是 committed。其它工作占用同一 occurrence 身份时报告 `event-id-conflict`，ledger 真正写满则报告 `event-history-budget`；两者都停止该 schedule，不重试、不二次执行，也不驱逐已记录身份。
- 第一个 pulse 的 handler 使捕获的 source/target 进入新 life 后，剩余 catch-up 不再调用旧对象 handler；该 schedule 在同一次 `Advance`（或下一次 `Advance` 的开头清理）内以 `stale-entity` 收据结束，不会逐个 pulse 反复失败。

## 数值来源租约

`AcquireNumericLease(NumericLeaseRequest)` 返回 `NumericLeaseResult`：`Status`（`acquired` / `duplicate` / `rejected`）、`Code` 和**可空** `Handle`。只有成功时为 `RuntimeStateLeaseHandle`（`ExpiresAtTick`、幂等 `Release()`）；重复（`duplicate-lease`）、同ID改贡献（`lease-id-conflict`）和各种拒绝的 `Handle` 都是 `null`，此时没有登记任何贡献。定义必须已经通过同一个 Registry 注册为 `kind = "state"` 且 `parameters.valueType = "numeric-contribution"`，版本严格锁定；未知定义报 `state-definition`，缺类型的定义报 `state-value-type`。

- 唯一 key 是 provider + 完整 target（world/life）+ 完整 source 或 null + scope + definition + stackGroup。同一 key 的第二份贡献以 `state-source-conflict` 拒绝，不隐式 refresh、不叠加、不替换；不同 stackGroup、不同 source、不同 life 之间互相隔离。
- 读取由 `EvaluateNumericState(target, definitionId, stackGroup, baseValue)` 完成：每次从传入 base 重新计算 `(base + Σ additive) × Π multiplier`，按稳定 source 顺序排序，因此结果与获取顺序无关；溢出拒绝，零乘数精确表示，移除贡献不做除法回滚。
- `DurationTicks` 为正，到期点不包含（读取当前 tick 时先结算到期）；到期、release、来源/目标 life 变化、world 变更、模块注销、作用域取消都会移除贡献。活跃上限 512，超过以 `state-lease-budget` 拒绝；无效输入按自己的原因拒绝，不消耗 ledger 或容量。
- 申请与读取都要求宿主已以 `isHost = true` 推进过该世界。核心不写游戏属性、不按阵营或实体种类推断目标，也不承诺已提交副作用回滚。

## 宿主边界

`ExportManifest()` 导出实际注册快照。`LoadPlan(json, grantedPermissions)` 从网站的离线Plan重新验证精确版本、领域、绑定闭包、显式权限和端口；权限由宿主提供，不能从文件自身反读自授。首批只支持 host 的单 Trigger→线性 Action，固定参数、直接事件输入、失败停止当前入口。未知控制流、动态结果依赖、recipient-policy及未实现数据引用明确拒绝。

`BeginWorld(epoch)` 需要递增安全整数epoch，取消所有旧世界排队事件并清理去重/取消记录；这不是检查点或网络恢复。`Advance(simulationTick, isHost)` 使用游戏提供的模拟时间，按due tick/入队序号调度。重复tick不会重置预算，积压以有界延后保留；非主机不调用handler，世界内主机迁移明确不支持。无订阅绑定用 `HasSubscribers(bindingId)` 在游戏hook中提前返回，避免构造无用payload。

`CommandContext` 携带plan/resource revision/nodeId、原始事件/根因果、命令ID、world epoch、计划/执行tick、来源和显式Inputs。处理器通过 `Succeeded(outputs, facts)`、`Rejected(code, detail)` 或 `Failed(code, detail)` 返回。请求量与实际量由领域结果区分，实际变化为0仍可成功。提交后事实只能由该处理器所属模块发布，继承真实因果深度；异常不会自动重试，也不承诺已提交副作用回滚。

`TickResult` 另外带 `Schedules` 和 `StateLeases` 收据，但只包含**该次 `Advance` 期间**产生的条目（到期、预算延后/拒绝、生命周期清理、pulse dispatch、非主机取消等）。主动调用 `RuntimeScheduleHandle.Cancel()`、`RuntimeStateLeaseHandle.Release()` 或 `CancelScope` 不会保证出现在之后的 `TickResult` 里；这些调用应读取方法自身返回值（`Cancel`/`Release` 的 bool、`CancelScope` 的受影响数量）以及句柄的 `Status`/`Code`。

## 预算与验证

运行预算由已锁定manifest约束：入口32、每入口128步、总512步；每tick128事件/512命令、队列1024、因果深度16，作者计划可要求更低上界。单事件64 KiB，命令最多128事实、总结果256 KiB；每世界去重账本65536项且不驱逐，耗尽明确拒绝新事件（包括新的schedule/lease）。定时/状态另有256活跃schedule、65536计划pulse、每tick64 scheduled pulse、16捕获引用、512活跃lease上限。所有epoch/tick限制在JavaScript安全整数范围。

从游戏仓库根目录执行：

```text
dotnet run --project ForgeRuntime/tests/Framework/Framework.csproj -- --fixtures C:/Users/nainf/Github/Infini-GTFO-Model-Site/Tests/Forge/fixtures/runtime
dotnet run --project ForgeRuntime/tests/Framework/Framework.csproj -- --benchmark
```

`--benchmark` 只做小型可重复的合成负载收据：注册/LoadPlan各一次，固定数量的dispatch、scheduled pulse和lease请求，各自的耗时、分配与预算拒绝原因。这些是桌面替身数字，不是GTFO帧率或联机性能证明。

公共内核测试使用独立合成模块；共享fixture来自实际注册导出并由网站真实编译。真实模块的接收边界及本机游戏程序集签名另由 `tests/GameBindings/` 验证。

## 未支持的边界

- 只支持 host 的单 Trigger→线性 Action；Control/Selector 嵌套图、可变历史世界快照、完整 VM 都不在当前执行Plan范围，也没有第二套内核、时钟、Registry或兼容入口。
- 没有持续效果（Buff/modifier层级的作者UI、refresh策略、属性回写或原生modifier lowering）；numeric lease只是SDK内部重算的贡献记录，不代表游戏属性已改变。
- `BeginWorld` 不是检查点或网络恢复：不处理断线重连、状态同步或多人复制。
- 上述测试不证明游戏内注入、多玩家复制、全部状态/周期效果或性能已验收；未执行GTFO、原生游戏API或联机。
