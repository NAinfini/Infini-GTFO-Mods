# ForgeRuntime 公共 SDK 1.0.0

本目录由 `ForgeRuntime.Framework.csproj` 单独编译成 `ForgeRuntime.Framework.dll`，生产宿主和全部模块共享这一个程序集。它提供与 GTFO/Unity 类型无关的注册、计划验证和唯一模拟队列；实际游戏绑定在相邻的 `GameBindings/` 与各领域包里。网站侧的 TS 合同与共同正反例在模型网站仓库的 `site/forge/runtime-contracts.ts` 与 `Tests/Forge/fixtures/runtime/`。

当前开发 Runtime 身份是 `forge.runtime / 1.0.0`，SDK API 1.0.0（Forge Standard，与网站同批升级），游戏 build `20403457`。未经游戏验收的绑定一律保持 `implementation-only`。交付要求见唯一开发计划 §6 U-RUNTIME（链接见[仓库 README](../../README.md)），跨包依赖与所有权见 [Runtime README](../README.md)，公开生命周期的完整时序与限制见 [HOST-LIFECYCLE.md](HOST-LIFECYCLE.md)。

共享 `CombatContracts.cs` 当前注册 **5 个** canonical 定义（承伤事实、治疗动作、生命变化事实、死亡流程、肢体破坏），**0 个** binding。Enemy 因此持有 5 个 binding 与 5 个原生 Hook，Runtime 保留 4 个世界与会话 Hook。

## 宿主生命周期

`Plugin.Runtime` 是唯一内核入口；`Plugin.CanExecuteGameplay` 提供模拟线程上的当前阶段与主机权威门槛，**不代替目标、权限和成本校验**。宿主首个 FixedUpdate 调用 `StartRuntime`，在导出实际 manifest 与加载离线计划之前冻结注册；失败锁存，不逐帧重读。`StopRuntime` 清除工作并使保留的内核引用不能继续执行。

模块在依赖 Runtime 的插件 Load 中登记，用返回句柄的 `ObserveLifecycle` 观察 Snapshot / StartupChanged / WorldChanged / TickAdvanced。通知有界、只读、按模块清理；回调错误被隔离而不是关闭正常玩法。

## 模块接入

在首个 Runtime FixedUpdate 之前，从公开的 `Plugin.Runtime` 取得宿主，调用 `RegisterModule(RuntimeModule, RuntimeLogLevel)`；第一方模块也走同一个接口。级别是必填参数，来自该包自己 cfg 的 `Logging.Level`（见下文），**不放进 `RuntimeModule`**；一个包注册多个 provider 时逐个传同一个级别。Runtime 自己随宿主注册的内置 provider（CombatContracts、ControlContracts）没有包 cfg，走只对宿主程序集可见的内核内部路径取 Runtime 自己的级别，不经公开参数伪造。模块声明一个 provider、自己拥有的 capability，以及自己提供的 bindings，结构与网站 ForgeRegistry 完全一致。扩展 bindings 可以引用已注册的共同 canonical capability，不重新声明也不覆盖它。**计划词汇中的 planned 不能注册成可执行能力。**

`RuntimeModule` 的 `Handlers` 以 `binding.handler` 字符串映射同步 `CommandHandler`。`EntityResolvers` 按精确对象 ID 前缀注册，例如 `gtfo.enemy` 对应 `gtfo.enemy:7`；ID 始终是字符串，完整引用还包含 worldEpoch 与 lifeEpoch。接收者有效性和具体游戏接收能力由所属模块再次检查，核心不按阵营或实体种类推断治疗与伤害目标。`EntityObservers`（读一个已知引用的当前状态）、`EntityInstanceResolvers`（把进程内原生对象映射成当前引用）与 `EntityCandidates`（说出自己的种类现在有哪些实例）是同一份所有权下的另三个可选表，各自要求该模块已经拥有这个种类的 `EntityResolvers`。

注册返回 `RuntimeModuleHandle`：

- `Publish(RuntimeEvent)` 只能发布自己的绑定；输入被复制，重复身份不会再次执行。返回 queued 只表示已入队，不表示游戏动作已提交。
- `CancelScope(scopeId)` 只取消该模块拥有的来源作用域，同名的其他模块作用域不受影响。
- `Schedule(...)` 与 `AcquireNumericLease(...)` 是同一句柄上的定时与状态入口，见下文。
- `Dispose()` 只注销自身。如果其他模块仍引用它的公共定义或绑定，整个注销被拒绝。只在启动前尚未冻结注册时允许注销后再登记；同 ID 新登记不会复活旧计划或旧句柄，Ready 之后不开放模块热加载。

这是受信模块之间的所有权约束，**不是任意第三方 DLL 的安全沙箱**。所有修改与分发都在宿主模拟线程调用；处理器执行过程中不能注册、注销或替换世界与计划，但可以取消或释放自己拥有的句柄以及申请新的 schedule 与 lease（仍走相同的因果深度和共享配额）。

## 实体观察

模块可以在 `RuntimeModule.EntityObservers` 中登记观察函数，由 `RuntimeKernel.InspectEntity` / `InspectEntities` 消费。注册时校验回调、命名空间归属、已拥有的 resolver、重复冲突和容量，失败不部分改变 Registry；注销时清除该模块拥有的观察器并释放委托引用。

返回的快照不可变、引用精确，并显式标注 complete / partial / rejected。**空的完整请求不等于观察不可用。** actor 永远不在 self / source / owner / instigator / event-target 之间回退；关系是有向的，实体种类与伤害治疗极性都不隐含阵营。观察回调是只读的：它不能变更内核，也不能在实体观察期间注销生命周期订阅（普通清理、停止后清理与生命周期回调自注销仍然允许）。

正在派发的触发事件的角色上下文与当前世界的阵营关系随 `EvaluationContext` 一起交给 `evaluate` 处理器：`Actors` 是按角色命名的显式引用集合，`Relations` 是有界有向的关系表，处理器不必也不应自己再查一次。角色与事件槽的对应只有 `RuntimeActorRoles` 这一张表，每个角色只有一个槽位：`source`、`owner`、`instigator` 各自读载荷里同名的端口，`event-target` 读载荷的 `target`（角色名与 `event_target` 这个选择器端口拼写只在这张表里关联），`self` 不读载荷，取派发时算出的挂载主体。端口存在但为 null 与端口缺席都是该角色缺席，某个槽位的值永远不会被拿去回答相邻的角色，事件上也没有第二条事件主体通道。挂载主体来自本计划自己的挂载目标：派发时把「有主体匹配器接受的实体」随排队的工作项带走，按 id 与两个 epoch 去重后恰好一个才是 `self`（无主体挂载如 level 不提供，0 个即缺席），多于一个不同实体时读取 `self` 的步骤以 `actor-ambiguous` 拒绝，既不任选也不报成缺席。缺席语义随后续合同声明的可空性：可空输出写 null，不可空输出（`self`）在处理器运行之前以 `actor-missing` 拒绝该步，不会拿到占位值。角色名也用于 `recipient_anchor` 枚举，两个拼写的映射同样只在这一张表里。

一个种类的候选集合只能由拥有该种类的 provider 交出：`RuntimeModule.EntityCandidates` 登记该种类自己的 `Func<IReadOnlyList<EntityReference>>`，注册校验与 observer 同组——key 必须是合法种类且值非空（`entity-candidate-source`），同一模块必须拥有该种类的 `EntityResolvers`（`entity-candidate-source-owner`），一个种类只能有一个来源，重复登记原子拒绝（`entity-candidate-source-conflict`），注销时连同该 provider 的全部来源一起移除，manifest 不导出。

查询完成性只覆盖显式请求的引用，**从不代表扫描了整个世界或某个空间区域**。候选集合也一样：内核不枚举、不发现、不按空间筛选，`RuntimeQuerySession.TryCandidates(kind)` 只是把 provider 自己的清单按预算读回；分页与空间查询仍属后续工作。

### 从原生实例取得引用

编号与 lifeEpoch 由拥有该实体种类的模块私有分配，其他模块不能猜，也不能拿原生键（例如账号 ID）自造。因此拥有者可以在 `RuntimeModule.EntityInstanceResolvers` 里按种类登记 `Func<object, EntityReference?>`，把进程内活的原生对象映射为当前引用。注册规则与 observer 相同：key 必须是合法种类且值非空（`entity-instance-resolver`），同一模块必须拥有该种类的 `EntityResolvers`（`entity-instance-resolver-owner`），重复登记原子拒绝（`entity-instance-resolver-conflict`）；注销时移除，manifest 不导出。

`RuntimeKernel.ResolveEntityInstance(kind, instance)` 只问该种类的唯一拥有者，不枚举、不回退到其他模块：
- 所属线程；Failed / Stopped 抛 `runtime-not-ready`；注册期或世界未开始时返回 null。
- 种类没有实例解析器抛 `entity-resolver`；解析器抛出的任何异常换成 `entity-resolver-failed`，消息只有种类名、不带内部异常，**实例和由它导出的键不进入错误文本**。
- 答案必须以 `kind:` 开头，并通过与已发布引用相同的路由核验（当前世界、拥有者 resolver），否则返回 null。
- 解析器在实体观察保护内执行：不能变更内核，也不能再查询内核（`entity-observer-mutation`）。

`RuntimeKernel.IsEntityCurrent(reference)` 公开同一条路由核验，线程与状态限制相同，返回 bool，不抛出核验失败。

## 定时脉冲

`Schedule` 返回 `ScheduleResult`：`Status`（`scheduled` / `duplicate` / `rejected`）、`Code` 和**可空**的 `Handle`。只有成功时才是 `RuntimeScheduleHandle`（`NextTick`、`DispatchedPulses`、`SkippedPulses`、`Status`/`Code` 和幂等的 `Cancel()`）；重复（`duplicate-schedule`）、同 ID 改定义（`schedule-id-conflict`）和各种拒绝的 `Handle` 都是 null，此时不产生任何定时工作。

`PulseSchedule` 必须显式给出首次时机（`Immediate` / `AfterInterval`）、错过策略（`SkipMissed` / `CatchUp`），并且至少有一个有限上界：`MaxPulses`（≤65536）或 `LifetimeTicks`（结束点不包含，两者同时存在取最早边界）。没有上界的 interval、零 interval、越过 JavaScript 安全整数的到期 tick、lifetime 内脉冲数超过 65536，全部明确拒绝。首脉冲落在结束点之后时仍返回 `Status = scheduled`，但 `Handle` 已经是 `Status = completed / Code = no-pulses-before-end`，没有排队 job，也不补脉冲。

- `SkipMissed` 只执行最新到期的一次 occurrence，用 `SkippedPulses` 和 `missed-pulses` 收据累计跳过数，结束点之后不补历史动作。
- `CatchUp` 只在同一实际 Registry 中 Trigger 及其全部 Action 都声明 `capability.parameters.scheduleReplay = "fixed-inputs"` 时接受，并按 due tick 顺序补发原定结束前的有限 fixed-input backlog。缺声明、需要世界查询或空间选择的效果只能使用 `SkipMissed`。声明的真实性由受信模块负责，核心不推断某效果是否可重放，也不为通过测试添加声明。
- 创建时捕获已加载的 plan 集合：之后载入的 plan 不会追溯加入，卸载任一被捕获的 plan 会在下一次 `Advance` 取消该 schedule。
- 配额：活跃 schedule 上限 256，单 schedule 计划 pulse 上限 65536，每次 `Advance` 最多 64 个 scheduled pulse，捕获 entity 引用最多 16 且不截断。同时受 queue、plan、events、commands、causal、history 的原有配额约束。更低一级的预算优先拒绝或延后，同一 tick 重复 `Advance` 不会重置任何预算。
- 事件身份包含 provider、模块 generation、world、scope、schedule id 的摘要与 occurrence index；queued/dispatched 不是 committed。其它工作占用同一 occurrence 身份时报 `event-id-conflict`，ledger 真正写满时报 `event-history-budget`；两者都停止该 schedule，不重试、不二次执行，也不驱逐已记录身份。
- 第一个 pulse 的 handler 使捕获载荷里的实体进入新 life 之后，剩余 catch-up 不再调用旧对象的 handler；该 schedule 以 `stale-entity` 收据结束，不会逐个 pulse 反复失败。

## 控制步骤与 query 档位

第一步的控制词汇表由内核 `ControlContracts` 拥有（`forge.control.flow.*`：7 个 canonical 定义、0 个 binding handler）。内核在走后继表的同一处按控制 id 分派它们，没有第二个路由器、也没有控制专用的执行路径；每种控制的执行输出个数与端口形状以注册合同为准（`next` 是出口，`pulse`/`body` 是下一次激活的入口），计划里 `successors` 的项数与顺序必须与之逐项相符（`successor-shape`）。表外的控制按 id 拒绝（`control-unsupported`）。

| 控制 | 进入时 | 之后 |
| --- | --- | --- |
| `branch` | 读 `condition` | 真走第一个后继、假走第二个 |
| `sequence` | 按执行输出顺序深度优先逐个走完：每个后继及其整条链走完，才走下一个 | — |
| `delay` | 写出 `timer` 句柄并登记 `MaxPulses = 1` 的续延 | 到点才从 `next` 继续，进入时不走 |
| `interval` | 写出 `timer` 句柄并登记 `PulseSchedule{IntervalTicks = interval, FirstPulse = first_pulse, MissedPulsePolicy = SkipMissed, MaxPulses = count}`（`count ≥ 1`） | 立即走 `next`；每跳把该跳的 `timer` 重新写进控制帧后从 `pulse` 走一遍 |
| `repeat` / `for_each` | 同一派发内同步循环：每轮写 `index`（`for_each` 另写 `item`）后走 `body` | 轮数走完走 `next`；零轮直接走 `next` |
| `cancel` | 取消 `task` 句柄持有的调度 | 实际取消数写进 `cancelled`；句柄已失效报 `stale-handle`、计数为 0 |
| `present` | 读 `value`：不是 null 走 `present`，是 null（或没接线）走 `missing` | 测过的值按计划自己声明的端口 id 写进控制帧；只有 `present` 出口可达的步骤能读它，分支外读取按 `present-branch` 拒绝（`carried` 端口在分支内按非空用） |

- 续延由内核自己保存（planId、控制步下标、输出序号、帧快照、句柄），重入时帧里的实体与句柄照常做 world/life epoch 校验，槽位或世代不符报 `stale-handle`/`stale-world`。`BeginWorld` 清空全部续延、调度与句柄池。
- 每次派发、每个循环轮、每次调度重入各算一次“激活”：`query`/`pure` 的 memo 以激活为作用域，重入即清空。每次激活至多执行 `MaxStepExecutionsPerDispatch = 2048` 步，超出报 `dispatch-step-budget`。循环控制的轮数另有 `MaxControlIterations = 1024` 上限：`repeat` 的 `count`、`for_each` 的候选数或它自己的 `budget` 超过上限时整个控制步骤被拒（`iteration-budget`），不跑一部分、也不静默截断。
- 句柄是“池地址 + 世代”：分配时写 `worldEpoch`/`lifeEpoch`/`local` 三元组加创建 provider 的注册下标，由消费它的那一步校验一次（种类与生命期来自端口声明，不符报 `handle-kind`/`handle-lifetime`）；槽位回收后再读同一值报 `stale-handle`，不会命中新占用者。活跃句柄超过 `MaxLiveHandles` 报 `handle-budget`。
- `query` 步骤经只在本次求值内有效的查询会话读世界：每 tick 64 次查询 / 1024 个实体引用、单次最多 256 个引用，额度耗尽或观察者不可用时该步显式拒绝（`query-budget`/`query-observer-unavailable`），**不返回截断后的半份结果**。`pure` 步骤拿到的是拒绝一切读取的会话，注册期也要求它零世界端口（`pure-world-port`）；带 entity/resource/handle 端口的 selector/condition/modifier 必须是 `query`，只读值的观察不得自称 `query`（`query-authority`）。
- 按种类枚举候选走同一份预算：`RuntimeQuerySession.TryCandidates(kind)` 每次计入一次查询和它返回的引用数，单次最多 256 个候选（超限报 `entity-query-budget`，**不截断**）；没有 provider 暴露该种类时以 `entity-candidates-unavailable` 拒绝，provider 自己的清单给出别种类的引用、抛异常或返回空时报 `entity-candidate-kind`/`entity-candidates-failed`。枚举只拿回引用，不附带快照。
- 挂载过滤发生在入队之前：每个挂载目标问拥有该 kind 的 provider 注册的匹配器——有主体的 kind 拿事件载荷里每一个实体端口的值去问（同一实体出现在两个端口上仍算一个主体），无主体的 kind（`level`）只按挂载目标判定；`category` 与 `reference` 原样交给匹配器。事件没有被任何挂载命中时按 `ignored` + `attachment-mismatch` 返回，不进队列；没有匹配器的 kind 在加载期就被拒，所以计划不会“加载成功但永不派发”。

## 数值来源租约

`AcquireNumericLease(NumericLeaseRequest)` 返回 `NumericLeaseResult`：`Status`（`acquired` / `duplicate` / `rejected`）、`Code` 和**可空**的 `Handle`。只有成功时才是 `RuntimeStateLeaseHandle`（`ExpiresAtTick`、幂等的 `Release()`）。定义必须已通过同一个 Registry 注册为 `kind = "state"` 且 `parameters.valueType = "numeric-contribution"`，版本严格锁定；未知定义报 `state-definition`，缺类型的定义报 `state-value-type`。

唯一 key 是 provider + 完整 target（含 world/life）+ 完整 source 或 null + scope + definition + stackGroup。同一 key 的第二份贡献以 `state-source-conflict` 拒绝，不隐式 refresh、不叠加、不替换；不同 stackGroup、不同 source、不同 life 之间互相隔离。

读取由 `EvaluateNumericState(target, definitionId, stackGroup, baseValue)` 完成：每次从传入的 base 重新计算 `(base + Σ additive) × Π multiplier`，按稳定的 source 顺序排序，因此结果与获取顺序无关；溢出拒绝，零乘数精确表示，**移除贡献不做除法回滚**。

`DurationTicks` 为正，到期点不包含。到期、release、来源或目标的 life 变化、world 变更、模块注销、作用域取消都会移除贡献。活跃上限 512，超过以 `state-lease-budget` 拒绝。申请与读取都要求宿主已以 `isHost = true` 推进过该世界。核心不写游戏属性、不按阵营或实体种类推断目标，也不承诺已提交副作用的回滚。

## 执行帧形状

每个计划装载时都按端口合同生成一份位置化的帧描述符：每个步骤的输入区、`pure`/`query` 的 memo 区或结果行区，每个入口的事件载荷帧与结果行区，以及一个写一次的计划级常量池。`ValueKind` 的编号是 wire 的一部分，新类型只在尾部追加：`Resource = 10`、`Event = 11`、`Result = 12`，前十个值不变；`RuntimeValueTypes` 与之一一对应（`ValueKind - 2` 就是端口类型的密集下标）。

一个值的槽宽：`execution` 不占槽；`boolean`/`integer`/`number`/`string`/`enum`/`entity`/`handle` 各一槽（`handle` 把创建它的 provider 注册下标放在同一个槽里）；`vector3` 三槽，头槽带 x、后面两槽带 y 和 z；`resource` 两槽，头槽是资源种类在共享词表里的下标、第二槽是 id 的字符串槽；`event` 一槽，是本次派发事件行的下标；`result` 没有自己的头槽，行宽等于它声明字段的宽度之和（前四列固定 `entity`/`enum`/`enum`/`string`，各一槽）。

集合（`many`）是「一个头槽带元素个数 + 256 个元素槽」，元素按自己的宽度走，所以 `vector3` 集合按 3 槽一个元素预留。只有元素类型的七种（`boolean`/`integer`/`number`/`string`/`vector3`/`entity`/`handle`）有集合形式：`resource`、`event`、`result` 是引用或行，`enum` 元素不开放，声明成集合即在帧层按 `unsupported-port` 拒绝。`FrameWriter.SetSegment` 开头槽、按类型的 `SetBoolean`…`SetHandle` 逐元素写，`SetResource`/`SetEvent` 写单值；`Frame` 侧对称地给出 `Count`/`Element` 与各类型的元素读取。

每步的 64 值槽预算只算它解码的值：集合预留的整段、`handle`、`resource` 与结果行都不计入（超限报 `frame-slot-budget`），它们连同常量池一起只受计划帧字节预算约束（每槽 24 字节加常量池字符串字节，超限报 `frame-bytes-budget`）。常量池只接受编译期 `resource` 引用（种类取端口的 `resourceKind`，id 取字面量的 `id`）；`handle`、`event`、`result` 在帧层没有字面量形式，分别按 `handle-literal` 和 `unsupported-port` 明确拒绝，不会静默写成别的值。

枚举集合的声明顺序就是编译进 `layout` 的 `valueSet` 下标，因此只允许尾部追加：既有 22 个集合的下标不变，`commit_state` 接在其后（下标 22），`agent_modifier`/`rundown_tier`/`door_state` 依次是 23/24/25。集合内的成员同样只尾加：`ai_state` 追加 `patrolling`/`hibernating`，`commit_state` 是 `none`/`confirmed`/`unknown`/`partial`。`ResourceKinds` 追加 `chained-puzzle`/`zone`、`HandleKinds` 追加 `pool`/`request`，旧成员（含 `room`、`pool_membership`、`transaction`）在引用它们的目录行改形之前一律保留。

## 宿主边界

`ExportManifest()` 导出实际注册快照。`LoadPlans(candidates)`（I-PACK）批量从宿主发现的离线计划文件重新验证精确版本、领域、绑定闭包与端口；运行时未就绪时和其余调用方错误一样直接抛出，不吞掉、也不逐文件报告。每个文件独立产生一条 `PlanLoadOutcome`，互不影响——同一 planId 出现在多个文件中，全部按 `plan-conflict` 拒绝，写出的记录消息带上该冲突组全部相对路径；无法解析 planId 的文件按自己的错误单独拒绝，不计入冲突分组；解析后的计划总数上限 128（`plan-budget`），超出部分按传入顺序依次拒绝。**权限不再由宿主授予或过滤**，计划声明的 `permissions` 只用于 `permission-lock`：必须与其绑定闭包全部 `RequiredPermissions` 的并集精确相等（`ExactSet`），多一个少一个都拒绝。计划格式是 schemaVersion 1（上一版是 3，形状变了就升版，不做兼容）：binding 以 pin 表下标引用，每个节点的 `layout` 由 Runtime 按已注册合同（含可变端口与端口组展开）重新推导并逐项比对，`layout.constants` 按参数声明顺序排列（null 表示未填写），数据输入是 `{slot, fromEventSlot}`、`{slot, fromStepSlot: {step, port}}` 或 `{slot, value}` 字面量三选一。计划声明 `limits` 仍是 `maxEventsPerTick`、`maxCommandsPerTick`、`maxQueuedEvents`、`maxCausalDepth` 四个键；查询、控制与句柄的额度是内核常量，没有计划级字段。顶层 `attachments[]` 必填且非空，每项是 `{kind, category?, reference}`，`kind` 属于 `level`/`map-object`/`gear-block`/`enemy-type`，按 `(kind, category, reference)` 序数升序且不重复（`attachment-empty`/`attachment-order`/`attachment-duplicate`/`attachment-budget`）；`level` 由内核匹配当前世界，其余 kind 在加载期要求已有 provider 注册的匹配器，否则报 `attachment-kind`。加载时把下标解析回端口名，处理器仍拿到按名称组织的 `Parameters` 与 `Inputs`；位置化的执行帧 SDK 已提供（`PlanFrames` 描述符加 `Frame`/`FrameWriter` 读写），端口名在装载期就已经用完。注册按 v0.2 校验端口的 cardinality、resourceKind、handleKind 与 lifetime，参数必须声明 role，每个 action 必须声明完整的 recipients（target、cardinality、result），每个 result 端口必须声明行字段——前四个固定为 `target`/`status`/`committed`/`code`，其后字段各自给出 id、值类型、单位、可空与值集，形状不符报 `result-schema`（本批只声明与校验，不写行）。入口带 `start`，步骤带 `nodeKind`（`action`/`control`/`query`/`pure`）与 `successors`：`successors` 只列该步骤的执行输出，按声明顺序一一对应，`query`/`pure` 恒为空；派发从 `start` 起沿后继前进，`control` 由内核在同一处按控制 id 分派（不查 handler），`query`/`pure` 步骤只在消费者用 `fromStepSlot` 读取时按需求值，可读同一次激活内任意更早步骤的帧（含控制步骤写出的 `timer`/`index`/`item`/`cancelled`），同一次激活内每个步骤最多求值一次，求值失败把消费者变成 rejected 回执，求值器自己抛的合同错误保留它的码，只有不带码的失败才统一编码为 `pure-evaluation-failed`。`handle` 只能来自更早的步骤或事件端口，计划里的字面量报 `handle-literal`，消费端口声明的种类与生命期不符报 `handle-kind`/`handle-lifetime`；`resource` 只接受编译期常量引用（帧里占两个槽：种类下标加 id 的字符串槽），运行期构造报 `resource-runtime-value`。`steps` 数组顺序必须是 Kahn 拓扑序（`nodeId` ordinal 决胜），加载器按同一算法重算后逐项比对；入口只在 rejected/failed/cancelled/expired 或 partial+unknown 时停止。`layout.promoted` 提升的 value 参数按声明顺序追加为输入，dispatch 时并回 `Parameters` 并按注册合同重新校验，越界拒绝不钳制。`evaluate` 角色把 selector/condition/modifier 绑定到 `RuntimeModule.Evaluators`，只读 `EvaluationContext` 有 NodeId、Parameters、Inputs、预算内的 `Query` 会话，以及本次派发事件的 `Actors` 与当前世界的 `Relations`。未知控制流、动态结果依赖、recipient-policy 与未实现的数据引用都明确拒绝；运行期数据端口只接受 boolean、integer、number、string、vector3、entity、enum（enum 的 wire 值是集合成员下标），`many` 只允许 entity；帧层本身已经有 handle、resource、event 三种形状与七种元素类型的集合形式，端口白名单与 `layout` 权威的放开是后面一批的事。

`BeginWorld(epoch)` 需要递增的安全整数 epoch，取消所有旧世界的排队事件并清理去重与取消记录；**这不是检查点或网络恢复**。`Advance(simulationTick, isHost)` 使用游戏提供的模拟时间，按 due tick 与入队序号调度。重复 tick 不会重置预算，积压以有界延后保留；非主机不调用 handler，世界内主机迁移明确不支持。无订阅的绑定可以用 `HasSubscribers(bindingId)` 在游戏 hook 中提前返回。

`CommandContext` 携带 plan 与 resource revision 与 nodeId、原始事件与根因果、命令 ID、world epoch、计划与执行 tick、因果深度和显式 Inputs。处理器通过 `Succeeded(outputs, facts)`、`Rejected(code, detail)` 或 `Failed(code, detail)` 返回。请求量与实际量由领域结果区分，实际变化为 0 仍可成功。提交后的事实只能由该处理器所属模块发布并继承真实因果深度；异常不会自动重试，也不承诺已提交副作用的回滚。

`TickResult` 另带 `Schedules` 和 `StateLeases` 收据，但**只包含该次 `Advance` 期间**产生的条目。主动调用 `Cancel()`、`Release()` 或 `CancelScope` 不保证出现在之后的 `TickResult` 里；这些调用应读方法自身的返回值和句柄的 `Status`/`Code`。

## 执行日志 sink 与级别

合同在 `RuntimeLogContracts.cs`，内核侧在 `RuntimeKernel.Logging.cs`。宿主用 `RuntimeKernel(identity, limits, IRuntimeLogSink sink, RuntimeLogLevel runtimeLogLevel)` 构造内核，sink 在内核生命周期内固定，没有注册或替换接口。`RuntimeLogLevel` 为 `Off < Error < Info < Trace`，构造参数只接受 off、error、info（否则 `log-level`），trace 只能经提级得到。`RuntimeLogConfiguration.ParseLevel(string)` 是宿主 cfg 与各包 cfg 共用的文本词表：off、error、info，大小写与首尾空白不敏感，非法值抛同一条消息；SDK 自己不读任何 cfg。

- `RuntimeLogRecord` 是 `readonly struct`，以 `in` 传给 `IRuntimeLogSink.Write(in record, RuntimeLogLevels levels)`。字段：Level、Code、Provider、SubjectProvider?、Tick、WorldEpoch、Frame?、CommandId?、EventId?、CauseId?、RootEventId?、Plan?（planId 与 resource id/revision）、Path?、Permissions?、Entry?、Step?、Binding?、Result?（status、commit?、reason）、Detail?（自由文本，只并入 sink 拼出的消息）。**没有 message 与 inputs**：消息由 sink 在后台线程拼，调用点不构造字符串；inputs 属于 Development 的 trace recorder，未实现。status 与 commit 是不做词表校验的字符串。
- 级别表按 provider 保存。每个条目在注册时建立：`RegisterModule(module, level)` 的级别只接受 off、error、info（否则 `log-level`），注册成功后该 provider 的 `LogGate` 返回这个级别；未注册的 provider 仍然抛 `log-provider-unregistered`，不静默当作 off 或 error。注销时移除条目，之后再查同样报错，不允许句柄失效后继续写。Runtime 自己的条目（`Identity.Id`）由宿主构造建立、也不由模块注销删除：模块即便声明同一个 provider id 也替换或删除不了它。表变化（注册、注销、提级）都立即发布新快照；注册时**先发布新表再写 `binding.registered`**，所以每条记录携带的表都已经列出它自己的 provider。
- `ElevateLogging()` 把所有条目升为 Trace 并切换到提级限流档。只在注册窗口内接受一次；窗口关闭（Ready、Failed、Stopped）或第二次调用都抛 `log-elevation-rejected`，不可撤销。级别门是同一个可变对象，提级前取得的门也会看到 Trace；提级之后注册的 provider 也是 Trace，不是它的 cfg 值。
- `WriteLog(in record)` 是记录点到 sink 的唯一通道：所属线程（`wrong-thread`）；无 sink 的内核抛 `log-unconfigured`；缺 code、provider 或不完整的 plan/result 抛 `log-record`；Off 或门未开抛 `log-level-disabled`。调用点应先比对门，再构造记录。
- 每次级别表变化都会发布新的 `RuntimeLogLevels` 快照（按 provider 排序、带 tier）。sink 只在 **tier 变化**时写新的 `log.level` 行（首行 + 提级行），所以注册多少 provider 都不会多出行；提级之前的快照只是让后续记录带上最新的 provider 列表。宿主 writer 的构造参数里带 Runtime 的 provider id：首条记录就被限流丢掉时，`log.dropped` 仍要能写出归属。
- `RuntimeLogCodes` 是码表单一来源，含 `log.level`、`log.dropped` 与内核写出的 15 个事件码；`RuntimeLogReasonCodes` 只有 `invalid-handler-result`（结果组合非法时内核改记 failed/unknown）与 `lifecycle-observer-failed`（观察者故障结果码），其余 reason 都是产生它的异常码或结果码本身。`adapter.*` 待 I-ADAPTER-SCHEMA，未声明。
- 内核记录点（归属规则）：`registration.rejected`（被拒注册与被拒容量都记，被拒注册的 provider 写进 `subjectProvider`；解析 seed 才知道 provider id 的失败没有 subject）、注册时每个 binding 一条 `binding.registered`、`BeginWorld` 一条 `world.began`、每次入队（发布、事实转发、计划 pulse 放行）一条 `trigger.fired`、事件被拒按 reason 是否以 `-budget` 结尾分流成 `event.rejected`（error）与 `budget.exceeded`（error，发布期的 `queue-budget`、`event-history-budget` 也走这一条）、每 tick 最多一条 `event.deferred`（trace，reason 是第一个拦下它的预算码，含 `scheduled-tick-budget`）、排队事件在派发前被丢弃就写一条 `event.cancelled`（trace，覆盖发布方注销、scope 取消、计划卸载、计划脉冲被跳过期丢弃）、每次调用的 `step.started`（trace）与 `step.finished`（`failed` 或 commit 为 `unknown` 记 error，其余 info，唯一带 `commit` 的记录）、结果停下且该步仍有后继时一条 `entry.stopped`（info，归 Runtime）、观察者抛异常时一条 `observer.failed`（error，归该观察者 provider）、以及 `LogSuspended(code, detail?)`（error，每次暂停一条；`StartRuntime` 失败写 `startup-failed`，正常停止不写——停止不是暂停）。
- `step.*` 与 `trigger.fired` 的 provider 取自**执行该步的 binding**（`step.BindingId`），不是入口的触发器 binding：跨包触发时归属和级别门都属于真正执行的那一方。
- 内核直接写事件与步骤记录，不再把 `TickResult` 回执订阅转成日志；error/info 不带 inputs，trace 的 inputs 复制仍由 Development 的 trace 记录器负责，本层只留记录点。
- 生成布局记录点：`ReportGeneratedLayout(in RuntimeLogLayout)`（info，归 Runtime）写 `map.layout-generated`，字段为 `Layout?`（`complete`、`elevatorLandedTick`、`zones[]`、`connections[]`）。调用方是观察关卡生成的那一层，它的事实由 `RuntimeLogLayoutCheck` 在写之前校验（区域 128、连线 512、每区域瓦片 64、方向词表 north/south/east/west/up/down/same/unknown），不合格以 `log-record` 拒绝而不写出一行；级别关闭时不写也不抛。记录格式见 `Docs/forge-contract/FORGE-FRAMEWORK.md` §3.2。

原有构造函数 `RuntimeKernel(identity, limits)` 不带 sink，测试与领域消费方仍用它；它的日志接口一律拒绝，不写任何东西。领域包自己的记录点（经句柄写出、providerId 由句柄盖）仍不在本层：目前只有内核可见的记录点已接入。

## 预算

运行预算由已锁定的 manifest 约束：入口 32、每入口 128 步、总 512 步；每 tick 128 事件 / 512 命令、队列 1024、因果深度 16，作者计划可以要求更低的上界。单事件 64 KiB，命令最多 128 事实、总结果 256 KiB；每世界去重账本 65536 项且不驱逐，耗尽时明确拒绝新事件（包括新的 schedule 与 lease）。定时与状态另有 256 活跃 schedule、65536 计划 pulse、每 tick 64 scheduled pulse、16 捕获引用、512 活跃 lease 的上限；控制另有每控制 1024 轮（`iteration-budget`）与每次激活 2048 步（`dispatch-step-budget`），查询另有每 tick 64 次查询 / 1024 个引用、单次 256 个引用、单种类候选枚举 256 项（`query-budget`）与活跃句柄上限（`handle-budget`）。所有 epoch 与 tick 限制在 JavaScript 安全整数范围内。

## 当前不支持的边界

执行范围是 host 的 Trigger → Action/控制步骤图：第一步的七种控制（含 `pulse`/`body` 区段的续延）已可执行，`query` 步骤按需求值。仍不在范围内的是结果行写入（当前只做行字段声明与注册期校验）、第二步的 `cancel_scope` 与 join/parallel/retry/timeout/transaction、可变历史世界快照、完整 VM，以及持续效果（Buff 层级的作者 UI、refresh 策略、属性回写或原生 modifier lowering）；也没有第二套内核、时钟、Registry 或兼容入口。numeric lease 只是 SDK 内部的重算贡献记录，不代表游戏属性已改变。`BeginWorld` 不处理断线重连、状态同步或多人复制——整个网络层属于框架 §6 U-NET。

上述任何测试通过都不证明游戏内注入、多人复制、全部状态与周期效果或性能已验收。复跑命令见 [VALIDATION.md](../VALIDATION.md)。
