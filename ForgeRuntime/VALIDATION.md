# ForgeRuntime 验证记录

**上次更新：2026-09-13**（内容合并自 R1、R2a、R2a-cleanup、R2b-1、R3a、R4a 六份交接记录与旧采集版验证记录）。

全仓库的测试计数汇总在 [ARCHITECTURE.md 第 3 节](../ARCHITECTURE.md#3-测试计数各自独立不相加)。本文记录 Runtime 侧各次交付的实际内容、复跑命令与仍然存在的失败。

## 当前结论

完整 GTFO 宿主与 `Forge.Architecture.sln` 都构建通过，0 警告 0 错误。R1 编译基线、R2a 公开生命周期、R2b-1 宿主配置与启动隔离、R3a 实体观察接线、R4a 可变端口元数据解析已交付并有实现级证据。D2 同批从宿主移除了全部诊断，见下文。**R2 整体、R3、R4、R5–R8 未关闭；没有任何游戏、安装、多人或恢复验收。**

R1 交接记录末段那三条 REOPENED 阻塞已全部解决：`ForgeEnemy/Receivers/EnemyHealthCommit.cs` 的 `RuntimeJson.Text` 缺失、`GameBindings/EnemyModule.cs` 的 `DamageObservation` 缺少 `damagePointer` 参数、以及六模块架构工程因此无法构建。旧 `GameBindings/EnemyModule.cs` 已在 E1 切换中移除。

## R1 — 宿主编译基线

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

## 复跑

从仓库根目录执行。宿主与 GameBindings 需要 `GTFO_BEPINEX_PATH` 或 `-p:GTFOBepInExPath=<BepInEx 目录>`。构建输出用 `--artifacts-path` 指向隔离目录，绝不写入已安装的插件目录。

```powershell
dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release
dotnet build Forge.Architecture.sln -c Release
dotnet run --project ForgeRuntime/tests/Architecture/Architecture.csproj -c Release --no-build
dotnet run --project ForgeRuntime/tests/Framework/Framework.csproj -c Release -- --fixtures ../Infini-GTFO-Model-Site/Tests/Forge/fixtures/runtime
dotnet run --project ForgeRuntime/tests/Framework/Framework.csproj -c Release -- --benchmark
dotnet run --project ForgeRuntime/tests/GameBindings/GameBindings.csproj -c Release -- --fixtures ../Infini-GTFO-Model-Site/Tests/Forge/fixtures/runtime
dotnet run --project ForgeRuntime/tests/HostIntegration -c Release
dotnet run --project ForgeRuntime/tests/LifecycleWork/LifecycleWork.csproj -c Release -- --fixtures ../Infini-GTFO-Model-Site/Tests/Forge/fixtures/runtime
dotnet run --project ForgeRuntime/tests/PluginStartup -c Release
dotnet run --project ForgeRuntime/tests/HostConfiguration -c Release
dotnet run --project ForgeRuntime/tests/HostIntegration -c Release -- --host <隔离目录>/bin/ForgeRuntime/release/ForgeRuntime.dll
dotnet run --project ForgeRuntime/tests/RuntimeLog -c Release -- --root <仓库外的新目录>
```

诊断侧套件的复跑见 [Development 验证记录](../ForgeDevelopment/VALIDATION.md#复跑)。聚焦回归各有自己的 README：[宿主启动](tests/PluginStartup/README.md)、[真实配置](tests/HostConfiguration/README.md)、[已加载工作清理](tests/LifecycleWork/README.md)。

`--benchmark` 只做小型可重复的合成负载收据：注册与 LoadPlan 各一次，固定数量的 dispatch、scheduled pulse 与 lease 请求，各自的耗时、分配与预算拒绝原因。**这些是桌面替身数字，不是 GTFO 帧率或联机性能证明。**

## 历史：1.1.x 采集版

1.1.0 的实机报错位于 `DMD<LG_ZoneJob_CreateZoneForStaticLevel::Build>` 的 native→managed trampoline。原生映射显示该 Build 的 RVA 0x34E9A0 与 247 个方法条目共享，`GetShadowRenderGroups` 的 RVA 0x46E980 与 95 个条目共享——**签名正确无法保证 detour 安全**。1.1.1 改用明确的非共享入口列表，`NativeContracts` 增加同一游戏版本 dump.cs 的共享地址检查。这条结论对后续所有原生 Hook 仍然有效。

1.1.0 的离线检查曾记录：Release 构建 0 警告 0 错误；报告核心与后台写入 32 项；项目规则与文件采样 34 项；场景清单 8 项；可选遥测订阅生命周期 56 项；帧采样 7 项；Python 报告分析、日志导入与运行对照 18 项；原生程序集契约 109 个 QOL hook、254 个制作端 hook 与 9 个内嵌图标检查通过。这些是当时版本的历史快照，不代表当前源码，也从未执行原生 detour。原版生成一致性、实机采集开销、连续进退关、主客机以及 R7D2 与 CullingCluster 的根因仍未验证。

## 边界

以上全部是托管逻辑、编译后 DLL、静态元数据与合成 fixture 的证据。没有执行原生 GTFO 方法、没有 detour、没有多人、没有安装。测试未通过时不能把已有发行包当成新构建成功；构建其他模块时也用自己的隔离输出目录，不写别人的 `bin/obj`。
