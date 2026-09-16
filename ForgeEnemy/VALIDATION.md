# ForgeEnemy 验证记录

**上次更新：2026-09-16**（模组收尾批；本文件合并自原 E1-BOUNDARY-HANDOFF、E1-NATIVE-CUTOVER、E1-PLUGIN-LIFETIME-SAFETY、E1-R3-INTEGRATION、E2-DELIVERY、E2-OBSERVATION-HANDOFF、E23-CONTINUATION、E3-LIFECYCLE-FACTS、E3-SAFETY-HANDOFF 九份交接记录）。

计划与状态见两仓统一框架的 U-ENEMY 一节（链接见[仓库 README](../README.md)）；本文只记带日期的运行记录。全部结果都是 implementation-only 或离线数据等级，没有启动 GTFO。

## 最后一次记录的通过数

不同套件覆盖不同边界并共享用例，**任何两行都不得相加成独立机制数**。下表除"架构 / Framework"外都在 2026-09-13 E2 批次用同一份隔离构建重跑，命令与哈希见 [`evidence/e2-spawn-space-20403457/commands.json`](evidence/e2-spawn-space-20403457/commands.json)。

Heal 改为多目标（伤害与生命周期事实形状同批收敛）之后，前四行在 2026-09-13 于 HEAD `d09d6bb` 加未提交的 Heal 多目标改动上重跑（隔离构建，Native 与宿主 0 警告 0 错误）；测试计划已改为本地构造，阻塞项见[阻塞项与解除条件](#阻塞项与解除条件)。其余行未受 Heal 多目标与测试计划迁移影响，同一次重跑结果与表中一致。

运行时枚举值端口落地后（ReceiverProbe 与 CommitAudit 恢复执行的伤害用例见[阻塞项与解除条件](#阻塞项与解除条件)），这两个套件在 `feat/enum-value-ports` 于 HEAD `96d8756` 加未提交改动上重跑（隔离构建，0 警告 0 错误），下表两行已更新为新结果；其余行不受影响，未重跑。

| 套件 | 结果 | 口径 |
| --- | --- | --- |
| 生命周期事实（LifecycleFacts） | 53/53（2026-09-16）；变体 7/7（2026-09-14） | 死亡流程与肢体破坏；含原生 Hook 适配器。原先两条 `integration.real-heal-*` 联调用例已按 155.3 删除，真实接收器覆盖改由 ReceiverProbe 承担（见[模组收尾批](#模组收尾批mods-tail2026-09-16)） |
| 接收器（ReceiverProbe） | 75/75；变体 19/19（2026-09-15） | 唯一现行接收器；含内核派发的 damage→Heal unknown 不重试、伤害窗口 `health_changed` 4 例、`enemy-type` 挂载 13 例，以及伤害动作 13 例与网站目录逐字比对 1 例（见[伤害动作](#伤害动作2026-09-15)） |
| 实体观察（EntityObservation） | 78/78（2026-09-16） | 通过实际共享 SDK；本轮改为编译生产清单与 `NativePlugin` 替身，自带的第二份替身已删除 |
| 行为观察（BehaviorObservation） | 71/71（2026-09-15） | AI、移动状态、技能的只读观察 50 例 + 攻击三阶段里的 windup/active 21 例（见[攻击阶段事实](#攻击阶段事实2026-09-15)） |
| 插件生命周期（NativePlugin） | 26/26（2026-09-15） | 跨线程、卸载、失败清理；计划为 death_started → 记录；Hook 计数改由 `EnemyNativeHooks.Types` 推导，含两条攻击 Hook |
| 提交路径审计（CommitAudit） | 68/68（2026-09-14） | 现行路径 32（伤害 6 项已随运行时枚举值端口恢复执行）+ 迁移边界 20；20 个是同一批治疗用例在两条路径上各跑一次，不算独立机制 |
| cutover 布局（NativeLayout） | 50/50（2026-09-15） | Enemy 九条 Harmony Patch（本轮加 `EnemyAttackWindup`、`EnemyAttackPerform`，逐条核对目标类型/成员/静态性与 `__instance`）、Runtime 四 Hook |
| 原生静态审计（NativeEvidence） | **674/681（2026-09-15，红）**：两条攻击 Hook 与新读的三个成员尚未进冻结规格，见[攻击阶段事实](#攻击阶段事实2026-09-15)的解除条件 | 154 个签名 + 7 个枚举常量 + Forge IL 用法 + 数据指针 + 伤害窗口 182 项；读元数据与 PE 指令，不加载也不调用游戏方法 |
| 出生空间要求（SpawnRequirements） | 48/48（2026-09-14）；**2026-09-15 改用运行期推导，本批未运行**，用例集合不变 | 离线数据合同 + Map 替身求解器 + 内容依赖 |
| 出生空间证据提取器 | 复现字节一致；锁检错 8/8 | 39 个敌人块、10 个基础 prefab |
| cutover CLI 防错（CutoverGuard） | 16 个 unittest OK | 真实 CLI 与退出码 |
| 架构 / Framework | 36 / 253 | **本批未重跑**，保留此前记录；属于 Runtime |

`ForgeEnemy.Native` 与完整宿主的零警告结果单独记录。Trigger 的测试工程构建有 3 条 NETSDK1138 目标框架提示，0 错误；本仓库没有切换目标框架。

## NativeEvidence 冻结输入统一到 Forge-MapEditor-QA（2026-09-14）

`evidence/native-api-20403457.json` 原来冻结 `Temp` profile 的三个 interop 程序集哈希与 MVID，而构建引用、调研与其它套件都用只读的 `Forge-MapEditor-QA` profile，于是用 QA 输入跑 NativeEvidence 有 6 项不符（见下）。两份副本的 MVID 与字节不同，但各自 `interop/assembly-hash.txt` 相同（`565871abd714937729d0e74520563bec`），说明由同一份 `GameAssembly.dll` 生成；按"唯一冻结来源与构建参考同一目录"的决定，冻结输入改为 QA 副本。

换锁前的只读复检（Cecil 与 dump 解析，不加载程序集、不启动游戏）：

| 检查 | 结果 |
| --- | --- |
| 逐类型/字段/属性/方法签名指纹（`$env:TEMP\dsh-v2\fingerprint.json`） | 3/3 identical：`Modules-ASM.dll` rows=170126、`GameData-ASM.dll` rows=32818、`SNet_ASM.dll` rows=6869，两份副本逐行相同 |
| 锁定的 `nativeRva`（`SendSetHealth` 0x161F790、`ReceiveSetHealth` 0x1380D50）在 dump.cs（sha256 `BF657C0E…DE1CC`）中的声明数与共享计数 | 各恰一条声明、共享计数 1（与 Weapon 的锁定 RVA 一起跑，98/98） |
| 游戏来源 | `GameAssembly.dll` sha256 `C6A5C3CD…7BF55`、Steam buildid 20403457 与锁一致 |

改动只替换 `assemblies` 三个条目的 sha256 与 MVID（`Modules-ASM.dll` → `E499B9C0…36D63` / `6d066008-…`；`GameData-ASM.dll` → `DEE52362…E7106` / `10c226b4-…`；`SNet_ASM.dll` → `6DAD1168…CF9B2C` / `143acd09-…`），127 个签名、7 个枚举常量、Forge IL 用法、`dataEvidenceFiles` 与游戏标识未改。

改动前的运行计数（上一次运行留下的报告，不是本次运行）：`Temp` 输入 `PASS 544/544`（`$env:TEMP\dsh-v\logs\enemy-before-temp.log`）；`Forge-MapEditor-QA` 输入 `FAIL 538/544`，失败的正是三个哈希与三个 MVID（`$env:TEMP\dsh-v\logs\enemy-before-qa.log`）。换锁后用 QA 输入、独立 artifacts 构建复跑：NativeEvidence 退出码 0，`PASS 544/544`；`verify_negative_cases.py` 退出码 0，`PASS 22/22`。命令见[复跑](#复跑)。

## r11 heal 结果聚合与 21 码改名（2026-09-14）

`Native/EnemyModule.cs` 的 `Heal` 与 `Receivers/EnemyHealthCommit.cs` 的 `Execute` 全部 21 个拒绝码改成 `CombatContracts.cs` 已提交的 kebab-case 形式（如 `gtfo.enemy.overheal_unsupported` → `overheal-unsupported`），删除 `heal-no-state-change`；最终聚合按"有无 committed 行"重写而不是按 `unknown==0`/`facts.Count==0`：≥1 行 committed 且存在 rejected/unknown 时报 Partial（commitState 视是否有 unknown 行取 unknown/confirmed，facts 可为空）；0 committed 只剩 rejected 时报 Rejected/None；0 committed 有 unknown 时报 Failed/Unknown。随动改了 `CommitCases.cs`（两个用例改名并重写为 `-is-partial-confirmed`/`-is-partial-unknown`）、`AuditScene.cs`、`verify_mutations.py`（三处 mutation 字符串同步改名，`exception-none` 改指向 `EnemyModule.cs` 新的第一个 catch 块）、`GameBindings/Program.cs`、`ReceiverProbe/Program.cs` 的对应码字符串断言。

隔离构建，SDK 与三个测试工程 0 警告 0 错误：

| 套件 | 结果 |
| --- | --- |
| CommitAudit | 68/68，BLOCKED 0 |
| ReceiverProbe | 43/44，BLOCKED 1（`commit.kernel-unknown-no-retry`） |
| ReceiverProbe `verify_mutations.py` | baseline + 8 个 mutant 全部按预期检出，0 failed |
| LifecycleFacts | 50/52，BLOCKED 2（`integration.real-heal-death_started`、`integration.real-heal-limb_broken`） |
| LifecycleFacts `verify_mutations.py` | baseline + 7 个 mutant 全部按预期检出 |

**上表三个 BLOCKED 用例当时未解除**，尽管 Runtime 侧的字面量与单值接多输入加载那批已经落地并经 `ForgeRuntime/tests/Framework --fixtures`（27 个站内负例逐条核对）与本表 CommitAudit 验证。根因是 `ForgeEnemy/tests/Shared/Blockers.cs` 的 `Heal` 常量在 `ReceiverProbe/Program.cs`、`LifecycleFacts` 里被无条件引用为阻塞，不读取任何运行时状态。**2026-09-14 已解除，三个用例都恢复真实断言并通过**（做法与结果见下一节）；当时的解除计划（用 `LocalPlan` 从内核注册表构造 事实→Heal 计划并恢复 +5HP 断言）就是按此执行的。

## Heal 联调解除与 E3 伤害与状态事件（2026-09-14）

基于 HEAD `aa2ff18` 加未提交改动，隔离构建目录 `$env:TEMP\forge-enemy-heal-e3-20260914`，`GTFO_BEPINEX_PATH` 指向 `Forge-MapEditor-QA` 配置（只作编译引用）。宿主、Native、8 个 Enemy 测试工程与 GameBindings 均 0 警告 0 错误。没有启动 GTFO，没有安装到任何 profile，没有 Git 提交。

**Heal 联调解除。** `tests/Shared/LocalPlan.cs` 新增 `{slot, value}` 字面量输入，以及按 capability 参数定义排位的 `constants`（枚举取成员下标，没给的参数填 null）；参数同时传给 `ResolveGraphContract`。`LocalPlan.Heal` 构造 事实→`forge.action.combat.heal`：事实主体经单值接多值进入 `targets`，同时作为 `source`，`amount` 为字面量 5，`overheal_policy` 为下标 0（clamp）。`permissions` 取两个 binding 的并集。三个 Enemy 用例恢复真实断言：
- `integration.real-heal-limb_broken`：一条命令，succeeded/confirmed，`actualAmount` 5，Health 50→55，Sends 1，下一 tick 不再派发。
- `integration.real-heal-death_started`：一条命令，rejected/none，`not-alive`，Sends 0，Health 不变。（2026-09-16 更正：这两条 `integration.real-heal-*` 用例与 `Scene.HealPlan` 已按 155.3 删除，真实接收器覆盖由 ReceiverProbe 承担。）
- `commit.kernel-unknown-no-retry`：damage_applied→heal 经内核派发，提交写入后抛错，结果 failed/unknown/`native-commit-exception`，Sends 1，Health 40→45，下一 tick 0 条命令，队列清空。

`Blockers.cs`、`CaseBlocked`、`T.BlockedRows`、`Audit.Blocked`、三个报告的 `blocked` 字段与两个 `verify_mutations.py` 的 blocked 分支都已删除（报告 schemaVersion 升一版）。GameBindings `--bridge` 删除 `HealBlocker`：同一测试构造器从内核注册表生成 `death-record.plan.json` 与 `damage-heal.plan.json`，写入 bridge 插件计划目录。现在断言 `LoadedPlans == 2`，并断言发现的 damage→heal 计划经真实 handler 提交 +5 HP 一次（45、Sends 1）且下一帧不重试。

**E3 伤害与状态事件。** 对照站内 `catalog/capability-catalog.json` 的 `canonicalVocabulary`（enemy 域触发器）与 SDK `CombatContracts.cs`：`damage_applied`、`health_changed`、`heal` 的 graph 与目录行逐字段相等（排序键后比较）。本批唯一实现的是 **`health_changed` 覆盖扩展**：binding 与 canonical 早已存在，原来只由 Forge 治疗产生。现在同一 `Dam_EnemyDamageBase.ProcessReceivedDamage` prefix/postfix 窗口里观察到实际 HP 损失时，发布 `{target, value = max(0, 调用后 Health), delta = -损失}`。观察门槛改为 damage_applied 或 health_changed 任一有订阅。复用已冻结的 Hook 与 `Health` getter，调用仍在 `BeforeDamage`/`AfterDamage` 里，NativeEvidence 调用者集合不变。窗口内生命不变或上升不发布，也不推断为治疗。

未实现，原因如下：

| 事件 | 原因 |
| --- | --- |
| `limb_damaged` | SDK 未注册 canonical（要改 `CombatContracts.cs`，本批边界禁止）；`Dam_EnemyDamageLimb` 的承伤入口与部位 HP 不在 E2 冻结里 |
| `staggered` | 未注册；没有冻结的硬直状态或时长 API |
| `killed` | 未注册；`damage_kind` 非空必填，而原生窗口推不出击杀归属与伤害类型（死亡≠击杀） |
| `hit_candidate`、`damage_preparing` | 未注册；`source` 非空必填，且需要伤害结算前可修改的窗口，没有冻结证据 |
| `damage_rejected` | 未注册；需要非空 `source`、`outcome`、`reason`，`ProcessReceivedDamage` 返回值语义未核验 |
| `assist_confirmed` | 未注册；游戏里没有已知的助攻概念 |
| `status.applied`、`stack_changed`、`refreshed`、`ticked`、`variable_changed`、`timer_elapsed`、`cooldown_ready`、`event_received`、`result_received`、`state_entered`、`state_exited` | 含 `handle`/`resource`/`event`/`result` 端口，运行时拒绝为 `unsupported-event-port`；也都未注册 |
| `status.expired`、`removed`、`resisted`、`threshold_crossed` | 端口类型可支持，但未注册；敌人状态（胶、燃烧等）没有进入 E2 冻结的原生 API |

`damage_applied` 的 `source` 与 `limb` 仍发布 null：`ProcessReceivedDamage` 带有攻击者与部位参数，但语义未核验。

| 套件 | 命令（`$out` 为上面的构建目录） | 结果 |
| --- | --- | --- |
| LifecycleFacts | `dotnet $out/bin/LifecycleFacts/release/LifecycleFacts.dll "$out/lifecycle.json"` | PASS 52/52 |
| LifecycleFacts 变体 | `python -X utf8 ForgeEnemy/tests/LifecycleFacts/verify_mutations.py --sdk $sdkDll --output "$out/lifecycle-mutations-2"` | baseline + 6 变体 7/7 |
| ReceiverProbe | `dotnet $out/bin/ReceiverProbe/release/ReceiverProbe.dll "$out/receiver.json"` | PASS 48/48 |
| ReceiverProbe 变体 | `python -X utf8 ForgeEnemy/tests/ReceiverProbe/verify_mutations.py --sdk $sdkDll --output "$out/receiver-mutations-2"` | baseline + 11 变体 12/12；新增 `health-gate-lost`、`health-delta-unsigned`、`health-value-before`、`health-rise-inferred` 均被指定用例检出 |
| CommitAudit | `dotnet $out/bin/CommitAudit/release/CommitAudit.dll "$out/commit.json"` | PASS 68/68 |
| NativePlugin | `dotnet $out/bin/NativePlugin/release/NativePlugin.dll "$out/plugin.json"` | PASS 24/24 |
| EntityObservation | `dotnet $out/bin/EntityObservation/release/EntityObservation.dll "$out/entity.json" $sdkDll` | PASS 66/66 |
| BehaviorObservation | `dotnet $out/bin/BehaviorObservation/release/BehaviorObservation.dll "$out/behavior.json"` | PASS 22/22 |
| NativeLayout | `dotnet $out/bin/NativeLayout/release/NativeLayout.dll $bepinex $hostDll $sdkDll $enemyDll cutover "$out/layout.json"` | PASS 38/38 |
| NativeEvidence | `dotnet $out/bin/NativeEvidence/release/NativeEvidence.dll $frozen $game $enemyDll ForgeEnemy/evidence/native-api-20403457.json "$out/native.json"` | PASS 544/544；检错 22/22 |
| GameBindings `--bridge` | `dotnet $out/bin/GameBindings/release/GameBindings.dll --bridge E:\SteamLibrary\steamapps\common\GTFO` | PASS 74，BLOCKED 0 |

NativeEvidence 先用 `Forge-MapEditor-QA` 的 interop 跑，结果 FAIL 538/544：只有 Modules-ASM、GameData-ASM、SNet_ASM 三个文件的哈希与 MVID 共 6 项不符（该 profile 的 interop 于 2026-09-09 重新生成，与冻结输入不同）。随后改用只读的 `Temp` profile interop（`$frozen`，三个哈希与输入锁表一致）重跑，得到上表结果。**同日晚些时候已把冻结输入本身统一到 `Forge-MapEditor-QA` 副本，这 6 项不符随之消失**，见 [NativeEvidence 冻结输入统一到 Forge-MapEditor-QA](#nativeevidence-冻结输入统一到-forge-mapeditor-qa2026-09-14)。GameAssembly.dll 哈希与输入锁一致。`--bridge` 由此前的 63 升到 74，本批只新增 4 个断言，其余差值来自两次记录之间的其他改动，未逐条归因。LifecycleFacts、ReceiverProbe、CommitAudit、NativePlugin 在 recorder 端口补 `unit: hp` 之后重建重跑；EntityObservation、BehaviorObservation、NativeLayout 不编译 `tests/Shared`，结果对应当前源码。

源码哈希：`Native/EnemyModule.cs` `F09F0743…93AFC1`，`tests/Shared/LocalPlan.cs` `7B9537A6…932273`，GameBindings `Program.cs` `2AFF9CE4…7442CB`；SDK `FE629515…555F5B`。

## 阻塞项与解除条件

Enemy 套件的计划全部由 `tests/Shared/LocalPlan.cs` 从内核注册表本地构造（版本、权限、槽位、按参数定义排位的 constants 与 `{slot, value}` 字面量都读注册表），不再读网站夹具。2026-09-14 起 Enemy 套件**没有阻塞结果**：`Blockers.cs`、`CaseBlocked` 与报告里的 `blocked` 字段都已删除，用例只有通过或失败，有失败时退出码为 1。

原先由加载器不支持字面量与单值接多输入而阻塞的三条用例（LifecycleFacts `integration.real-heal-*`、ReceiverProbe `commit.kernel-unknown-no-retry`、GameBindings `bridge.configured-heal-plan-commits-5hp`）已解除，见 [2026-09-14 记录](#heal-联调解除与-e3-伤害与状态事件2026-09-14)。其中 LifecycleFacts 的两条 `integration.real-heal-*` 后来按 155.3 删除，真实接收器覆盖只剩 ReceiverProbe 一条路径（见[模组收尾批](#模组收尾批mods-tail2026-09-16)）。下表只剩 Runtime 侧 GameBindings `--fixtures` 的阻塞，Enemy 套件不使用。

| 阻塞原因 | 用例 | 解除条件 |
| --- | --- | --- |
| 网站夹具早于注册表 | GameBindings `fixtures.valid-plan-heal-dispatch`、`fixtures.invalid-plan-rejections` | `--fixtures` 先把网站有效计划的 capability/provider 版本与注册表逐项比对，不一致就整组阻塞（无效计划都是有效计划的单字段变体，版本不符会让它们因错误的原因被拒）。模组导出真实 capability/provider 清单、网站据此重生成夹具、且夹具里的 Heal 计划与注册表一致后，比对通过即自动恢复执行。 |

运行时枚举值端口的拒绝已随本次改动解除：`damage_applied` 的 `damage_kind` 输出不再以 `unsupported-event-port` 拒绝订阅，`LocalPlan.Load` 也已移除把这一种拒绝转成阻塞的特判。原先列在此处的 ReceiverProbe 7 个伤害用例、CommitAudit 6 个 `existing-path.damage.*`、以及 ReceiverProbe 的 `observation-replay`/`late-damage-retargeted` 两个变体均已恢复执行并通过，见上表。

## E2 批次

### API 冻结 v2

`evidence/native-api-20403457.json` 是唯一现行的冻结规格（schemaVersion 2）。它取代了之前 92 签名的 v1 与 E3 批次 99 签名的输入；那两份作为批次历史保留在各自的 evidence 目录，现行审计器不再接受 v1。

v2 共 127 个签名，分属 11 个领域：identity 16、spawn-requirements 21、health 9、damage-limbs-death 19、ai-perception 10、movement-space 9、abilities 10、birthing-scout 11、attacks-projectiles 5、appearance-animation 11、cleanup-replication 6。

每个签名冻结：
- 声明程序集、类型、完整签名与 static/virtual/public。
- `evidenceLevel`：只允许 `metadata`，或 `static-native`（限于 `NativeHealth` 实际解码的两个方法体，并写明 RVA）。结果是 metadata 125、static-native 2。RVA 与方法的对应来自这一 build 的 Il2CppDumper 映射，审计器证明的是该地址的指令形状，不是对应关系本身。
- `nativeCallPhase`：全部为 `unknown`。审计器拒绝任何其他取值，因为没有运行时追踪。
- `forgeHooks` / `forgeCallers`：审计器从 `ForgeEnemy.Native.dll` 的 IL 读出 Harmony patch 与调用点，与规格逐项比对；任何 Forge 调用到的三份游戏程序集 API 未冻结都会失败。结果是 hook 5、call 29、inventory 93。
- `dataEvidence`：12 个 DataBlock getter 指向出生空间证据里的字段。审计器校验证据文件哈希，并确认字段在每个数组元素上存在。

另冻结枚举常量 7 个：`ES_StateEnum.PathMove=2`、`PathMoveFlyer=28`，以及 `eEnemyType` 的 0–4。

**审计中发现的缺口**：BehaviorObservation 与 EntityObservation 调用的 12 个游戏 API（如 `Agents.Agent::get_Alive`、`Dam_EnemyDamageBase::get_Owner`、`EnemyAgent::get_AI/get_Locomotion/get_Abilities`）此前没有冻结。旧审计只核对规格里写了什么，不核对 Forge 实际用了什么。

工具检错 22/22：
- 基线通过。
- 原有 10 类错误全部精确检出。
- 新增 11 类同样精确检出：未知字段、未知证据等级、static-native 过度声明或遗漏、声称已知的调用阶段、删掉 Hook、伪造调用者、遗漏 Forge 调用、枚举值错误、数据字段不存在、数据文件哈希错误。

### 出生空间证据与合同

提取器 `tests/SpawnSpaceEvidence/extract_spawn_space.py` 只读游戏文件，锁定以下输入，任一不符即以对应错误码拒绝：
- Steam build 20403457、UnityPy 1.25.3。
- 四个数据文件的 SHA-256。
- 三个 DataBlock TextAsset 的名称、pathId 与内容哈希。
- Enemies_S1 分片场景（BuildSettings 第 43 项）与基础 prefab 目录前缀。

本批复跑的输出与仓库内证据字节一致。

合同由 `Spawn/EnemySpawnRequirements.cs` 从证据生成，字段来源见 [README](README.md#出生空间要求合同e2离线数据等级)。SpawnRequirements 48 项覆盖：
- 证据目录、仓库内合同与证据一致、合同往返。
- 移动分类：地面 32 / 飞行 4 / 未决 3。
- Striker、MegaMother、Flyer、Cocoon、SquidBoss、Pouncer 的逐字段断言，以及查找不到时不替代。
- 6 种证据变异：游戏已执行、缺基础 prefab、缺移动块、禁用块、未映射的移动状态、地面 prefab 带空中图。
- 10 种合同篡改。
- 16 个 Map 替身求解器用例：多实例同资源、未决拒绝、飞行需要空中图、agent 类型与区域掩码、竞技场维度、净空受限时拒绝未核验净空。
- 5 个内容依赖用例：只引用资源不要求 Enemy 包；Enemy binding 从真实 `EnemyModule` 注册表判定；domain 标签不算依赖；非法输入拒绝。

检错也实测过：篡改仓库内合同的一个字段，或把夹具期望改成"接受"，check 都以退出码 1 失败。

**Map 替身不是 MAP2 求解器**；拒绝超大或特殊移动单位的责任归 Map，本批只交付 Enemy 侧要求与夹具。

### 出生空间要求的运行期来源（2026-09-15，实现，未测）

此前 `EnemySpawnRequirementCatalog` 只能由仓库内的证据 json 构建，`Get(id)` 对证据里没有的 id 抛 `KeyNotFoundException`，因此网站经 MTFO 注入的自制敌人在 MAP4 里拿不到出生要求。本批把「来源」与「推导」拆开：推导只剩 `EnemySpawnRequirementCatalog.Build(EnemySpawnInputs)` 一个入口，输入是强类型记录（敌人行、movement 行、balancing 行、base prefab 的 NavMeshAgent 与空中图标记、agent types、areas），不认识 JSON 也不读文件；证据 json 降级为测试夹具，由新增的 `tests/SpawnRequirements/EvidenceInputs.cs` 翻成同一组记录，生产代码里的 `FromEvidence` 已删除，没有第二条推导路径。

运行期来源是新增的 `Native/EnemySpawnRequirementSource.cs`，由 `Plugin.cs` 在 Load 时用 `Log.LogWarning` 作上报口接上（能力降级用 warning 更贴切，也不依赖 `TestLog` 替身当时有没有 LogError）。构建时点、依据与边界：

- 三张 DataBlock 表在 GTFO-API `GameDataAPI.OnGameDataInitialized` 之后读；该事件由 `GTFO.API.Patches.GameDataInit_Patches.Initialize_Postfix` 在 `GameData.Initialize` 之后触发，MTFO 注入的块此时已在 `GetAllBlocks()` 里。GUID `dev.gtfomodding.gtfo-api` 与版本 0.5.0 读自随包 `BepInEx/plugins/GTFO-API.dll` 的 `BepInPlugin`。
- base prefab 是资源分片对象而不是 DataBlock，只能在 `AssetShardManager.EnemyAssetsIsLoaded` 为真之后用 `GetLoadedAsset<GameObject>` 取；两个前提谁后满足就以谁为构建点（`AssetShardManager.OnEnemyAssetsLoaded` 在 interop 里是属性加 `add_`/`remove_` 访问器，订阅用同一个 `Il2CppSystem.Action` 实例）。**这个先后关系没有在真实游戏里验证过**，见待核验第 12 项。
- 引用不到的 movement/balancing 块或 base prefab，使 `Build` 抛 `InvalidDataException`，被来源捕获成一条不超过 2048 字符的诊断；`Catalog` 保持 null，没有默认体型或默认导航兜底。`Get(id)` 的错误信息现在写清 DataBlock id 并说明“运行期未加载该行、该行被禁用或该 id 不存在”。
- 运行期 agent 类型表来自 `NavMesh.GetSettingsCount()`/`GetSettingsByIndex()`（与离线 `m_Settings` 同源）。区域表没有等价的运行期枚举 API（`NavMeshProjectSettings` 是编辑器对象），只按引擎保留名 Walkable / Not Walkable / Jump 反查 `NavMesh.GetAreaFromName`；锁定构建只有这三个区域，将来的自定义区域会缺行，见待核验第 13 项。
- `GameBuild`/`EvidenceSha256` 从目录对象移到 `EnemySpawnRequirementProvenance`：运行期表不会被序列化，也就没有诚实的证据哈希可写，来源信息因此只在写读合同时出现。合同 JSON 的字段名与 `version = 1` 未变，`evidence/e2-spawn-space-20403457/spawn-requirements.json` 不需要重新生成（本批未运行生成器，也没有改动该文件）。
- 架构边界不变：`Spawn/EnemySpawnRequirements.cs` 仍无 Unity/BepInEx 依赖，`ForgeEnemy.dll` 的引用只有 `System.*` 与 `ForgeRuntime.Framework`；运行期来源放在 `Native/` 而不是 `Spawn/`，因为 `ForgeRuntime/tests/Architecture` 要求托管程序集与游戏无关。该推导源码由 Native 工程与 `tests/NativePlugin` 直接编译进来（与本仓库既有的源码共享方式一致），不新增第二份实现。

本批实际执行的只有编译检查（`dotnet build`，隔离 `--artifacts-path`）：`ForgeEnemy`（托管）、`ForgeEnemy.Native`、`tests/SpawnRequirements` 与 `ForgeRuntime/tests/Architecture` 全部 0 警告 0 错误。`tests/NativePlugin` 在本批改动完成后曾以 0 警告 0 错误构建，随后被同一工作区里并发的宿主改动打破：`ForgeRuntime/Plugin.cs` 与 `Plugin.cs` 新增的 `HostPlugin.IsSuspended` 分支让 3 个错误出现（`ForgeRuntime.Plugin` 替身缺 `IsSuspended`/`SuspensionCode`、`TestLog` 缺 `LogError`），三处都在该分支内，与本批新增的源码无关；替身留给该分支的拥有者补。**SpawnRequirements 的 48 项没有运行**（本批只做编译级验证），运行期来源也没有在游戏里跑过。

为了让上述工程能编译，改了范围外的文件并说明理由：`Native/ForgeEnemy.Native.csproj` 增加 `EnemySpawnRequirementSource.cs`、`../Spawn/EnemySpawnRequirements.cs`、GTFO-API / Shards-ASM / UnityEngine.AIModule / GlobalFramework-ASM 引用（否则来源读不到 DataBlock 与分片）；`tests/NativePlugin/NativePlugin.csproj` 增加同两个源码；`tests/NativePlugin/SpawnRequirementDoubles.cs` 为运行期来源新增替身；`tests/NativePlugin/LoaderDoubles.cs` 的 `BepInDependency` 替身补上 `AllowMultiple = true`（真实 BepInEx 属性就是 `AllowMultiple = True`，本插件现在有两个依赖）。

运行时构建点与区域表的复跑方式是 `dotnet build ForgeEnemy/tests/NativePlugin/NativePlugin.csproj`（只编译，替身不注入游戏；替身补齐前该工程会因上述并发改动报 3 个错，见上）；真实构建点只能靠游戏内记录，见下。

### 身份替身用例

ReceiverProbe 新增 4 项：
- `identity.same-resource-instances`：同资源两个实例，销毁一个不影响另一个。
- `identity.pooled-pointer-new-id`：同一原生对象换 GlobalID，旧 ID 再被另一对象占用，得到三个不同生命，旧引用被拒。
- `identity.world-change-single-life`：world 变化后重放出生只得一个新生命，旧 world 引用被拒，只发一次包。
- `identity.late-damage-after-respawn`：伤害窗口跨越销毁并同 ID 重生，迟到回调不发布事实也不治疗新生命。

新增变体 `late-damage-retargeted` 被最后一项精确检出。

第一版变体只把 `Resolve(before.Target)` 换成按 GlobalID 查找，**没有被任何用例检出**：Runtime 的 Publish 本身会拒绝已失效的 target。这说明迟到回调有两层保护，其中 Enemy 这一层不能在现有替身里单独观察到。变体因此改成同时改投新生命，这正是这类缺陷的真实形态。

`Receivers/EnemyIdentityTable.cs` 与 `Receivers/EnemyDamageWindow.cs` 未被任何工程引用，是 `EnemyModule` 身份与伤害窗口逻辑的重复实现，已删除。

## 待游戏内核验

以下都**没有执行**。每项需要一次真实加载（主机，必要时加客机），只记录、不改变原生行为，并与合同或规格的对应字段逐条比对。

1. **出生与销毁的原生阶段顺序**：对 `EnemyAllocator.SpawnEnemy`、`EnemyAgent.Setup`、`EnemySync.OnSpawn`、`EnemyAllocator.OnEnemySpawned` 与 `EnemySync.OnDespawn`、`EnemyAgent.OnDeSpawn` 挂只记录的 Hook，记录帧号、线程、主客机身份、GlobalID 与原生指针，确定顺序并据此把 `nativeCallPhase` 从 unknown 改为有证据的值。
2. **对象池与迟到回调**：同一关卡反复刷怪与清怪，记录 GlobalID 与指针是否复用、复用前是否总有 `OnDespawn`，以及销毁后是否仍有 `ProcessReceivedDamage`、`DestroyLimb`、`OnDead` 进入。
3. **运行时 NavMeshAgent**：对 Striker(13)、MegaMother(55) 等地面敌人读取实例上的 NavMeshAgent 的 agentTypeID、radius、height、areaMask、enabled 与 transform 缩放，比对 `groundNavigation` 与 `navMeshAgentTypes`。
4. **尺寸倍率**：同一敌人块多次出生，记录 `EnemyAgent.SizeMultiplier` 与模型缩放是否落在 `modelSizeRanges` 内，以及 NavMeshAgent 半径和碰撞体是否随之缩放。
5. **碰撞半径语义**：记录谁在何时读取 `EnemyBalancingDataBlock.EnemyCollisionRadius` 与 `CanBePushed`（调用栈与阶段），判断它是敌人间分离、推挤还是出生净空。
6. **空中图**：飞行敌人（42、43、45、58）出生时记录 `GetCurrentAirGraph()` 与 `FlyingAirGraphAgent.IsPositionOnGraph(出生点)`，并在有与没有空中图的房间分别测试。
7. **基础 prefab 解析**：在 `EnemyPrefabManager.BuildEnemyPrefab` 返回后记录层级与组件，比对证据中 Enemies_S1 分片里按名称解析到的对象。
8. **SquidBoss 冲突**：在 44、61 实际出现的关卡记录移动中的 `Locomotion.CurrentStateEnum`、空中图与 NavMeshAgent 状态，解决 PathMove 与空中图的矛盾。
9. **竞技场维度**：Pouncer(46) 的 `ArenaDimensions=[14]` 实际触发什么，记录其读取点。
10. **净空**：在窄通道和通风口类空间生成地面与大型敌人，记录卡住、瞬移或出生失败，给 `spawn-clearance` 提供真实证据。
11. **DataBlock 覆盖**：在自定义 rundown 下比对实例的 `EnemyData`、`EnemyMovementData`、`EnemyBalancingData` 与离线证据，不一致则该 rundown 不能使用本合同。
12. **运行期要求表的构建时点**：装 GTFO-API 0.5.0 与一个自制敌人（经 MTFO 注入新 EnemyDataBlock）启动，记录 `GameDataAPI.OnGameDataInitialized` 与 `AssetShardManager.OnEnemyAssetsLoaded` 的先后、两者与 `EnemyAssetsIsLoaded` 的关系，以及 `EnemySpawnRequirementSource` 上报的日志或失败诊断；确认 `GetAllBlocks()` 里已含注入行、`GetLoadedAsset<GameObject>` 能按 `BasePrefabs` 路径取到自制 prefab。**只读记录，不改变处理顺序。**
13. **运行期 NavMesh 区域表**：在真实运行的游戏里断言 `NavMesh.GetAreaFromName("Walkable"/"Not Walkable"/"Jump")` 的返回值与 `walkableAreaMask` 的位一致；若换用带自定义区域的构建，记录运行期 `navMeshAreas` 是否真的缺行、以及缺行是否会影响 Map 的掩码解释。

## 接收器安全修复的复现记录

原始 13 项探针先复现 8/13。**E2-002 一项没有被修成通过**：它只给出同指针的新 wrapper，没有可证明的新生命边界，失败证据保留。v2 显式加入"捕获旧生命 → 销毁 → 同指针重生 → 重放旧令牌"，同时验证 wrapper 本身不代表新生命；扩展后 34/34 通过。裸指针的迟到网络回调如何取得原生代次，仍待上面第 2 项核验。

插件生命周期的 18 项用例最初 12/18 通过、六个新用例全部失败；修复并扩展后 24/24。移除一层冗余外层检查后仍被下一层保护的一个探针单独记录，**不冒充检错成功**。

## E1 切换与实体观察登记

唯一 `EnemyModule` 从 `ForgeRuntime/GameBindings/` 移入 `ForgeEnemy/Native/EnemyModule.cs` 并切换命名空间。Runtime 不再创建或清理 Enemy provider，只保留四个世界、会话与检查点 Hook；三个敌人 Hook（后来扩为五个）由独立的 Native 插件拥有，空的 `ModuleDefinition.Create` 已删除。所有实际的 receiver、bridge、插件、实体观察测试的源码引用同步更新，不保留兼容执行副本。Native 仍单向引用宿主与唯一 SDK；SDK 无 Unity/BepInEx 依赖，Runtime 无 Enemy 生产依赖。

实体观察登记在既有 Registry 中落地，带所有权和容量校验，注销时清理，并接上只读回调约束。跨模块测试进一步检出生命周期订阅注销的缺口：`RemoveLifecycleObserver` 现在只在实体观察期间拒绝修改，仍允许普通清理、停止后清理与生命周期回调自注销。

集成修改经过 25 个目标路径的哈希前置检查，没有恢复或覆盖并发的 Trigger 工作。首次全快照检查发现四个未拥有的 Trigger 文件发生变化，保留了它们的工作树并刷新测试副本。

## E3 事件的验证细节

Heal 改用多值 `targets` 之前记录的事件套件 52/52 包含原生 Hook 适配器（作为托管测试代码调用 prefix/postfix，不注入）、真实计划 → Heal 的 +5 HP 正例，以及"死亡目标不隐式复活"的负例。Heal 改成多值 `targets` 后，那个 +5 HP 正例一度无法构造合法计划而列为 BLOCKED；2026-09-14 用单值接多值与字面量重新构造计划，正例与负例都恢复执行并通过。事件错误变体全部被指定断言检出；**编译失败不算通过**。

两处初始失败保留：首次 50 项用例检出"owner ID 变化而指针相同"的缺口，补上 owner ID 校验后通过；当时的 Heal 联调最初因夹具的权限与绑定列表未按 canonical ordinal 排序被严格加载器拒绝，修正夹具排序后通过——**没有降低加载器校验**。

## 输入锁

本机 Steam app 493520 / build 20403457；游戏与 BepInEx 只作只读输入。没有复制游戏 DLL、模型或第三方代码到可分发目录。

| 输入 | SHA-256 |
| --- | --- |
| GameAssembly.dll | `C6A5C3CD8CA5FE2A8C1A71A3D107663E8CBF01404820DBBDA2EA10C4BFD7BF55` |
| interop/Modules-ASM.dll | `E499B9C0EB1FA4F1C4194B13163CE1932355AD20C04C424FE4661F8EEF736D63` |
| interop/GameData-ASM.dll | `DEE523620186887A95A8E940FF1DDB0CDB94E395840F12C823CC26AE4F5E7106` |
| interop/SNet_ASM.dll | `6DAD116887A975508B6DB3843C0BECA31874E4F7AFB7C3D6E65D1BE3EDCF9B2C` |
| GTFO_Data/resources.assets | `5833891d4f9d04c8ddb103e2f7feca5c67ed7ec70a7618374fa3cc51640aae5f` |
| GTFO_Data/sharedassets43.assets | `d2b5db1128577bdd48f68c61002106fdc60d100ad8b5e542f7748cd7d5db866e` |
| GTFO_Data/globalgamemanagers | `7825069c90c34fbb49e9e742197d4a6ef2abee09cd60b8193e2a3304c1ff6337` |
| GTFO_Data/globalgamemanagers.assets | `3bbbdc31f2d9c0a30bd2098a6710606d72708f6f73986d4d10f5a21ee7490ce6` |

冻结规格保存三个 interop 的 MVID；提取器另锁三个 DataBlock TextAsset 的内容哈希。**哈希和元数据证明这份输入的签名与资产数值，不证明原生参数语义、合法提交阶段、运行时组件状态、复制完成或性能。**

## 复跑

先按 [README 的构建入口](README.md#复跑) 得到 `$hostDll`、`$sdkDll` 与 `$enemyDll`（`bin/ForgeEnemy.Native/release/ForgeEnemy.Native.dll`），然后从仓库根运行，输出目录必须隔离。Enemy 套件不再读网站夹具，计划都从内核注册表本地构造。`$bepinex` 是只读的 `Forge-MapEditor-QA` profile 的 `BepInEx` 目录（与冻结输入同一份 interop）。每个 `dotnet build` 都带 `--artifacts-path $out "-p:ForgeFrameworkAssembly=$sdkDll"`；NativeEvidence 与 NativeLayout 还要 `"-p:GTFOBepInExPath=$bepinex"`。

```powershell
# 生成物位于 $out/bin/<工程名>/release/<工程名>.dll
dotnet $out/bin/LifecycleFacts/release/LifecycleFacts.dll "$out/lifecycle.json"
python ForgeEnemy/tests/LifecycleFacts/verify_mutations.py --sdk $sdkDll --output "$out/lifecycle-mutations"
dotnet $out/bin/ReceiverProbe/release/ReceiverProbe.dll "$out/receiver.json"
python ForgeEnemy/tests/ReceiverProbe/verify_mutations.py --sdk $sdkDll --output "$out/receiver-mutations"
dotnet $out/bin/CommitAudit/release/CommitAudit.dll "$out/commit.json"
dotnet $out/bin/EntityObservation/release/EntityObservation.dll "$out/entity.json" $sdkDll
dotnet $out/bin/BehaviorObservation/release/BehaviorObservation.dll "$out/behavior.json"
dotnet $out/bin/NativePlugin/release/NativePlugin.dll "$out/plugin.json"
dotnet $out/bin/NativeLayout/release/NativeLayout.dll $bepinex $hostDll $sdkDll $enemyDll cutover "$out/layout.json"
dotnet $out/bin/NativeEvidence/release/NativeEvidence.dll $bepinex $game $enemyDll `
  ForgeEnemy/evidence/native-api-20403457.json "$out/native.json"
python ForgeEnemy/tests/NativeEvidence/verify_negative_cases.py $out/bin/NativeEvidence/release/NativeEvidence.dll `
  $bepinex $game $enemyDll ForgeEnemy/evidence/native-api-20403457.json "$out/native-negative"
python ForgeEnemy/tests/SpawnSpaceEvidence/extract_spawn_space.py --game $game --unitypy $unitypy --output "$out/spawn-space-evidence.json"
python ForgeEnemy/tests/SpawnSpaceEvidence/verify_negative_cases.py --game $game --unitypy $unitypy --output "$out/spawn-space-negative.json"
dotnet $out/bin/SpawnRequirements/release/SpawnRequirements.dll check ForgeEnemy "$out/spawn-requirements.json"
python ForgeEnemy/tests/CutoverGuard/test_guard.py
```

`$unitypy` 是装有 UnityPy 1.25.3 的目录；版本不符时提取器拒绝运行。提取器输出应与 `evidence/e2-spawn-space-20403457/spawn-space-evidence.json` 字节一致。只有证据变化时才运行 `SpawnRequirements.dll generate ForgeEnemy <合同路径>` 重写合同。

各测试目录的边界说明见 [提交路径审计](tests/CommitAudit/README.md)、[实体观察](tests/EntityObservation/README.md)、[生命周期事实](tests/LifecycleFacts/README.md)。每批的实际命令与退出码记录在对应的 `evidence/` 目录的 `commands.json` 里。

构建失败时不要运行旧的可执行文件当作新构建的结果。在活动工作区里构建时要比较前后的源码哈希——源码变化意味着报告只对那个版本有效，不是最新文件的证明。

## 边界

原生读取、离线数据、替身、编译和元数据是不同的证据层，任何一层通过都不代表游戏或多人已通过。GameBindings 各模式共享基础断言；Heal 改为多目标后的重跑为无参 31、`--fixtures` 32 且 BLOCKED 2、`--bridge` 63 且 BLOCKED 1、`--native` 51；2026-09-14 只重跑了 `--bridge`，为 74 且 BLOCKED 0，其余模式未重跑。

没有安装、发布、启动游戏、修改用户 profile、Git 提交或推送。

## E10 敌人伤害窗口（2026-09-15）

`evidence/e10-damage-window.json`（schemaVersion 1）冻结敌人伤害路径的元数据、原生调用边与调用点指令窗口，回答 E10 的四个问题。它的 producer 是 `tests/DamageWindow`（离线：读 `GameAssembly.dll` 的 PE 字节与 Il2CppDumper `dump.cs`，Iced 有界反汇编，**不加载游戏、不执行游戏方法**）；消费者是 `tests/NativeEvidence` 的第 6 个可选参数。

**产出。** 35 个方法条目（角色、RVA、virtualSlot、Il2Cpp 参数名与逐参数说明、反汇编观测）、8 个 packet 结构字段表、32 条直接调用边（扫描 `.text`/`il2cpp` 得到 14362 条 rel32 call，只保留伤害链）、32 个调用点指令窗口、15 个枚举值、4 条结论。每条结论把未证部分单列在 `unverified` 里，不混进答案。

**四问结论。** Q1：伤害入口是 `Dam_EnemyDamageBase.ProcessReceivedDamage(float damage, Agent damageSource, Vector3 position, Vector3 direction, ES_HitreactType hitreact, bool tryForceHitreact, int limbID, float staggerDamageMulti, DamageNoiseLevel damageNoiseLevel, uint gearCategoryId)`，返回 `Boolean`；原生体 0x137E570..0x137E9FF 共 243 条指令、以 `ret` 结束，调用者是 `ReceiveBulletDamage`/`ReceiveMeleeDamage`/`ReceiveExplosionDamage`/`ReceiveFireDamage`/`ReceiveFreezeDamage` 等 packet 接收器，返回值在 `ReceiveMeleeDamage` 0x138076D 处被 `test al,al; je` 判定。`BulletDamage`/`MeleeDamage`/`ExplosionDamage` 三个入口**不直接调用**它，而是走 `SendPacket` + `SendLocally`。Q2：bullet/full/small/medium packet 都带 `pAgent source`，limb 以 byte 同包传输（`pBulletDamageData.limbID` @0x10、`pFullDamageData.limbID` @0x15、`pExplosionDamageData.limbID` @0xA），所以主机侧能拿到来源 agent、命中 limb，伤害类别由被调用的接收器决定；属客机发起还是主机代发**未证**。Q3：`pExplosionDamageData` **没有 source 字段**，爆炸伤害无法指定攻击者；炮塔是否把部署者写成 sourceAgent **未证**（需要读炮塔的伤害发射点，超出本窗口）。Q4：外部伤害走 `Dam_EnemyDamageBase.BulletDamage`（第 7 个原生参数是 limbID，`Dam_SyncedDamageBase.BulletDamage` 没有 limb 参数），它经 SendLocally+SendPacket 让接收器在权限侧应用；实际伤害与剩余生命从 `get_Health`（字段 +0x20）与 `get_HealthMax`/`get_DamageMax` 读回。**未证**：非主机调用是否本地生效、发包是否有主机门槛、被拒绝与伤害被修正为零如何区分。

**审计接线。** `NativeEvidence` 第 6 个参数指向本文件，新增伤害窗口检查 178 项：schema/buildId/GameAssembly 哈希与 v2 规格一致、35 个方法的 Il2Cpp 参数名逐项与 interop 程序集相同、`ReviewedRvas` 中的三个方法 RVA 与本文件一致、8 个 packet 的字段序列、32 条调用边各自有一个冻结窗口且窗口内**确实反汇编出指向该目标的 call**、4 条结论齐备。另有 `NativeHealth.VerifyDamageWindow` 在同一构建上重解 0x137E570（体）、0x137EF63/0x1380768/0x137F6D7/0x137FC8C（接收器）与 0x137D4BD（SendLocally）六处窗口的指令。生成物重跑应与仓库内文件字节一致。

**本批实际执行的命令与结果**（`$out` = 本任务隔离 artifacts 目录，`$bepinex` = 只读 `Forge-MapEditor-QA` profile 的 BepInEx，`$game` = GTFO 根目录）：

| 命令 | 结果 |
| --- | --- |
| `dotnet build ForgeEnemy/tests/DamageWindow/DamageWindow.csproj -c Release --artifacts-path $out/build` | 0 警告 0 错误，退出码 0 |
| `dotnet $out/build/bin/DamageWindow/release/DamageWindow.dll $game <dump.cs> ForgeEnemy/evidence/e10-damage-window.json` | 退出码 0，`methods=35 packetTypes=8 callEdges=32 callSiteWindows=32 scannedCallEdges=14362` |
| `dotnet build ForgeEnemy/tests/NativeEvidence/NativeEvidence.csproj -c Release --artifacts-path $out "-p:GTFOBepInExPath=$bepinex"` | 0 警告 0 错误，退出码 0 |
| `dotnet $out/bin/NativeEvidence/release/NativeEvidence.dll $bepinex $game $enemy ForgeEnemy/evidence/native-api-20403457.json "$out/native.json" ForgeEnemy/evidence/e10-damage-window.json` | 退出码 1，`FAIL 715/738`；其中伤害窗口 194 项全过，`native.health.*` 全过 |
| `python ForgeEnemy/tests/NativeEvidence/verify_negative_cases.py …` | **未完成**：baseline 因下述并发漂移不通过，脚本按设计拒绝给变异计分（退出码 1，未产出 summary） |

`$enemy` 用本批现编的 `ForgeEnemy.Native.dll`（0 警告 0 错误，用当前 `ForgeEnemy/Native` 源码与当前 `ForgeRuntime.Framework` 编译，宿主引用取 2026-09-15 08:40 的宿主构建产物）。**未能重编宿主**：`ForgeRuntime/Logging/RuntimeLogWriter.cs` 与 `ForgeRuntime/Network/NetworkMessages.cs` 在本批执行期间被其它任务改写，`dotnet build ForgeRuntime/ForgeRuntime.csproj` 报 `CS0103 BufferBytes`、`CS1501 Append` 等错误，因此宿主与整链构建不是本批结论的一部分。738 项里 24 项失败的分布是：本批新增的伤害窗口断言 0 项失败；其余为 `spawn-requirements` 域的 IL 调用者集合差异与 `forge-use.call-coverage`（当前源码调用 `GameDataBlockBase<T>.GetAllBlocks()/get_internalEnabled()/get_persistentID()/get_name()` 等 10 个未冻结 API）——这批改动来自并发的出生要求任务，与伤害窗口证据无关，留给该任务把规格与源码对齐后再跑负例。**因此"194 项全过"是本批能给出的最强结论，不是整套 NativeEvidence 已经全绿。**

**证据层边界。** 全部结论来自 PE 指令、Il2CppDumper 元数据与 interop 程序集签名：未启动游戏、未安装、未进多人、未改任何 profile，也没有运行期伤害数值或复制时序证据。RVA→方法的绑定来自该构建的 dump，审计只证明"该地址上的指令形状"，不证明绑定本身；0x137E623 的虚表槽未解析；炮塔来源、客机发包方、非主机本地生效与拒绝原因都仍是 `unverified`，不要当成已确认的行为。

## 出生要求任务后的原生证据基线恢复（2026-09-15）

并发的出生要求任务把 `Native/EnemySpawnRequirementSource.cs` 接进 Native 插件后，`tests/NativeEvidence` 的基线不再通过：本批起点是 `FAIL 727/738`，11 项失败全部是**冻结规格落后于生产 IL**，不是生产代码用了不该用的 API。10 项 `forge-use.<条目>` 是条目的 `forgeCallers` 仍为空、而 IL 已在调用；1 项 `forge-use.call-coverage` 是 `GameDataBlockBase<T>::GetAllBlocks()/get_internalEnabled()/get_persistentID()/get_name()` 经 `EnemyDataBlock`/`EnemyMovementDataBlock`/`EnemyBalancingDataBlock` 三个实例化的 10 个调用点未冻结。这 10 个成员都在 build 20403457 里存在，`EnemySpawnRequirementSource` 也只在 `GameDataAPI.OnGameDataInitialized` 与 `AssetShardManager.EnemyAssetsIsLoaded` 两者都成立后的构建点各读一次全部启用行（`ForgeEnemy/Native/EnemySpawnRequirementSource.cs` 第 75–188 行）：没有逐帧枚举、没有反射、没有臆造成员。**因此本批没有需要生产代码修改的项，`Native/**`、`Spawn/**` 未改动。**

**新增冻结的 10 个 API 与证据。** 签名取自 `ForgeEnemy.Native.dll` 的 IL 引用（Cecil，`forge-use` 比较用的就是这个字符串）与 interop 声明（`ilspycmd -t` 指向 `Modules-ASM.dll` 里的泛型类型 `GameDataBlockBase<T>`，输出留档）；原生存在性取自该构建的 Il2CppDumper map（`dump.cs` 的 `GenericInstMethod` 区：开放声明 RVA 均为 -1，实例化体共享同一段代码）：

| 类型.方法（开放声明） | 实例化体 RVA | 冻结的实例化与调用者 |
| --- | --- | --- |
| `GameData.GameDataBlockBase<T>::GetAllBlocks()` | 0x1C13910 | `EnemyDataBlock`（`BasePrefabs`、`Enemies`）、`EnemyMovementDataBlock`（`MovementBlocks`）、`EnemyBalancingDataBlock`（`BalancingBlocks`） |
| `GameData.GameDataBlockBase<T>::get_internalEnabled()` | 0x562060 | 同上三种实例化，调用者相同 |
| `GameData.GameDataBlockBase<T>::get_persistentID()` | 0x3A4150 | `EnemyDataBlock`（`Enemies`）、`EnemyMovementDataBlock`（`MovementBlocks`）、`EnemyBalancingDataBlock`（`BalancingBlocks`） |
| `GameData.GameDataBlockBase<T>::get_name()` | 0x320020 | `EnemyDataBlock`（`Enemies`） |

全部记为 `evidenceLevel: metadata`（不写 `nativeRva`，RVA 只在上表作为取证记录）：这些是开放泛型声明，三种实例化共享同一个原生体，逐实例化各记一条会把同一个地址重复冻结。真正冻结的是"每个实例化调用点"——`signature` 写实例化形式（如 `GameDataBlockBase<T>` 的 `EnemyDataBlock` 实例化），`dataEvidence` 指向离线证据里对应数组的 `id`/`name`/`internalEnabled`，于是三种实例化都留在证据链上。

**审计的泛型放宽。** 声明在开放类型上、签名写实例化，`tests/NativeEvidence/Program.cs` 新增的 `Open()` 只在"声明匹配"这一步去掉类型实参（`type` 与 `signature` 两侧同样处理，注释写明原因）；`frozen` 集合与 IL 调用键仍是原始字符串，所以将来新增一个实例化调用点仍会被 `forge-use.call-coverage` 判为未冻结。另同步把 10 个既有条目的 `forgeCallers` 从空改成真实调用者（`EnemySpawnRequirementSource::Enemies`、`::MovementBlocks`、`::BalancingBlocks`、`::BasePrefabs`，其中 `get_BasePrefabs` 与 `GetAllBlocks<EnemyDataBlock>` 各有两个调用者）。规格内嵌的三个 interop `sha256`/`mvid` 锁本批未改，运行中 `hash.*` 与 `mvid.*` 全部相符。

**同批修掉的两处旧漏洞**（都在本任务负责的 `tests/NativeEvidence/**`，与出生要求漂移无关，但由同一批负例暴露）：

1. `damage-window.rva.<方法>` 原来只锁 `NativeHealth.ReviewedRvas` 里的两个方法，而 `e10-damage-window.json` 的 `nativeRvaLock` 声称 `Dam_EnemyDamageBase.ProcessReceivedDamage`（0x137E570）同由 `NativeHealth.cs` 锁定——实际没有任何检查比对它，`window-moved-rva` 变异体因此存活。把该签名加进 `ReviewedRvas`（伤害窗口本来就在这个地址解码它的体）后，规格里该条目从 `metadata` 升为 `static-native` + `nativeRva: "0x137E570"`，e10 的这一条 RVA 才真正被锁，变异体改由 `damage-window.rva.Dam_EnemyDamageBase.ProcessReceivedDamage` 拒绝。**这取代了 E10 批"标 metadata、RVA 只留在 e10 里"的判断。**
2. `window-missing-window` 变异体原来期望 `audit.error`；实际拒绝者是缺失窗口所对应调用边的 `damage-window.edge.<site>`（以及 `edge-call`）检查，期望值改为从文件里取出该 site，不再硬编码。

**本批实际执行的命令与结果**（`$out` = `%TEMP%\spawnev`，其它变量同[复跑](#复跑)）：

| 命令 | 结果 |
| --- | --- |
| `dotnet build ForgeEnemy/tests/NativeEvidence/NativeEvidence.csproj -c Release --artifacts-path $out "-p:GTFOBepInExPath=$bepinex"` | 退出码 0，0 警告 0 错误 |
| `dotnet build ForgeEnemy/Native/ForgeEnemy.Native.csproj -c Release --artifacts-path $out/enemy-fresh "-p:GTFOBepInExPath=$bepinex" "-p:ForgeRuntimeAssembly=<并发任务的既有 ForgeRuntime.dll>" "-p:ForgeFrameworkAssembly=<并发任务的既有 ForgeRuntime.Framework.dll>"` | 退出码 0，0 警告 0 错误 |
| `dotnet $out/bin/NativeEvidence/release/NativeEvidence.dll $bepinex $game $enemy ForgeEnemy/evidence/native-api-20403457.json "$out/native-after2.json" ForgeEnemy/evidence/e10-damage-window.json` | 退出码 0，`PASS 788/788`；`enum-constants=7, evidence:metadata=134, evidence:static-native=3, use:damage-window=1, use:forge-call=49, use:forge-hook=5, use:inventory=83` |
| `python ForgeEnemy/tests/NativeEvidence/verify_negative_cases.py $out/bin/NativeEvidence/release/NativeEvidence.dll $bepinex $game $enemy ForgeEnemy/evidence/native-api-20403457.json "$out/native-negative2" --damage-window ForgeEnemy/evidence/e10-damage-window.json` | 退出码 0，`PASS 31/31`（22 项规格变异 + 9 项伤害窗口变异），baseline 在脚本内先跑一次同样通过 |

这两条运行记录对应的是重复调用尚在时的字节（788 = 780 + 8 个重复窗口检查）。合并重复调用后的复跑记在[重复的伤害窗口检查合并](#重复的伤害窗口检查合并2026-09-15)，报告里 780 个检查 id 已互不重复。

`$enemy` 是本批现编的 `ForgeEnemy.Native.dll`（sha256 `D79E6A42…565D`，源码与 E10 批一致：`ForgeEnemy/Native` 下最后改动是 08:48）。**宿主仍不构建**：`ForgeRuntime` 正被并发任务改写，构建引用取它们已成功的既有产物（`%TEMP%\dsh-rtframe2\bin\ForgeRuntime\release\ForgeRuntime.dll` 09:12:51、`…\ForgeRuntime.Framework\release\ForgeRuntime.Framework.dll` 09:21:01），因此宿主与整链构建不在本批结论内。规格 `evidence/native-api-20403457.json` 由 127 个签名增至 137 个，本批后的 sha256 为 `9E84E0C0…463E`（这是记录，不是锁）。

**计数口径。** 780 = E10 批的 738 + 10 个新条目的 4 项检查（40）+ 10 个新 `dataEvidence` 指针 - 8 个重复的窗口检查。E10 批曾在 `Program.cs` 里调用两次 `NativeHealth.VerifyDamageWindow`（伤害窗口块内一次、末尾一次），8 个窗口检查 id 因此在报告里各出现两次；2026-09-15 已合并为伤害窗口块内的唯一一次调用，检查集合与语义不变，负例判定也不受影响（`verify_negative_cases.py` 只看失败 id，不看总数）。合并后的实际运行见下表。

**规格自身的哈希记录。** 本仓库里记录该规格文件哈希的只有 `evidence/e2-spawn-space-20403457/commands.json` 的 `outputs`（E2 批次记录，写的是当时那份 `8df8614e…`，在本批之前就已不等于工作区文件），它是带日期的批次记录而不是活锁，本批没有改写它；现行哈希记在本节。规格内嵌的 interop 哈希与 MVID 是活锁，由每次运行重新比对（本批全部通过）。`e10-damage-window.json` 未改动。

**边界。** 本批只到元数据、IL 与 PE 证据层：未启动游戏、未安装、未进多人、未改任何 profile、未做任何 Git 写操作。新增的 10 个条目是 `metadata` 等级（无 RVA 锁），原生存在性来自该构建的 dump，不是运行期读数；`EnemySpawnRequirementSource` 的运行期时点与 MTFO 注入行是否已在表里仍未在游戏内核验（[待游戏内核验](#待游戏内核验) 第 12、13 项）。

## 重复的伤害窗口检查合并（2026-09-15）

E10 批在 `tests/NativeEvidence/Program.cs` 里留下两处 `NativeHealth.VerifyDamageWindow` 调用（伤害窗口块内与文件末尾），8 个窗口检查 id 因此在报告里各出现两次，788 这个总数里含 8 项重复。`Program.cs` 现在只在伤害窗口块内调用一次（该文件同批还有并发的敌人触发任务改动，两边不重叠）：检查集合与每个检查的语义不变，只是不再重复记账，负例判定照旧（`verify_negative_cases.py` 按失败 id 匹配，与总数无关）。

**本批实际执行的命令与结果**（`$out` = `%TEMP%\enemyclean`，其它变量同[复跑](#复跑)）：

| 命令 | 结果 |
| --- | --- |
| `dotnet build ForgeEnemy/tests/NativeEvidence/NativeEvidence.csproj -c Release --artifacts-path $out "-p:GTFOBepInExPath=$bepinex"` | 退出码 0，0 警告 0 错误 |
| `dotnet $out/bin/NativeEvidence/release/NativeEvidence.dll $bepinex $game $enemy ForgeEnemy/evidence/native-api-20403457.json "$out/native.json" ForgeEnemy/evidence/e10-damage-window.json` | 退出码 0，`PASS 780/780`；`enum-constants=7, evidence:metadata=134, evidence:static-native=3, use:damage-window=1, use:forge-call=49, use:forge-hook=5, use:inventory=83`；报告里 780 个检查 id 互不重复 |
| `python ForgeEnemy/tests/NativeEvidence/verify_negative_cases.py $out/bin/NativeEvidence/release/NativeEvidence.dll $bepinex $game $enemy ForgeEnemy/evidence/native-api-20403457.json "$out/native-negative" --damage-window ForgeEnemy/evidence/e10-damage-window.json` | 退出码 0，`PASS 31/31`（22 项规格变异 + 9 项伤害窗口变异），baseline 在脚本内先跑一次同样通过 |

`$enemy` 沿用上一批现编的 `ForgeEnemy.Native.dll`（`%TEMP%\spawnev\enemy\bin\ForgeEnemy.Native\release`），因为本批没有改动 `Native/**`；`ForgeEnemy.Native` 与宿主因此未重编，本批没有新增或收紧任何原生证据。

## 行为观察证据刷新（2026-09-15）

行为观察批次给 `Native/**` 加了两个 Hook（`Enemies.EnemyDetection::UpdateTargets`、`ES_ScoutDetection::OnTargetRegistered`）并让观察者读了 15 个新的游戏 API，冻结规格因此落后于生产 IL：本批起点是 `FAIL 772/780`，8 项失败全部是证据过期（2 项 `forge-use.hook-coverage`、1 项 `forge-use.call-coverage`、6 项既有条目的 `forge-use.*` 调用者集合少一个/多个调用者），不是生产代码用了不该用的 API。

**生成工具。** `tests/NativeEvidence` 新增 `--generate` 模式（`Program.cs` 分派到 `GenerateSpec.cs`），与审计器共用 `ForgeIl.cs` 读同一份 IL，所以生成物里冻结的 Hook 与调用者字符串和审计器检查的字符串来自同一段代码：

```
dotnet $out/bin/NativeEvidence/release/NativeEvidence.dll --generate $bepinex $enemy `
  ForgeEnemy/evidence/native-api-20403457.json ForgeEnemy/evidence/native-api-20403457.json
```

工具只重算 IL 派生的字段（`forgeHooks`、`forgeCallers`、新条目的 `assembly`/`type`/`signature`/三个可见性标志），**人工复核过的字段原样保留**（`id`、`area`、`evidenceLevel`、`nativeCallPhase`、既有条目的 `nativeRva` 与 `dataEvidence`），并会拒绝三类输入：内嵌 interop 哈希漂移、既有条目在锁定 interop 里解析不到或可见性不符、以及插件读到的成员没有复核记录。新条目的 `area` 与 `dataEvidence` 指针来自 `GenerateSpec.cs` 的 `ObservedMembers` 表；这张表要求每个成员在 `evidence/enemy-behavior-hooks.json` 里**恰好有一条**同名记录，指针写成该记录的索引（`hookMembers[N].nativeRva` 或 `readFields[N].declaration`），索引由生成器从记录名反查，写错或查不到都会让生成失败。落盘后再次 `--generate` 到临时文件得到 `methods=153 (+0), refreshed=0`，sha256 与工作区文件相同（`8D4008DB…FE07F`），即刷新结果可复现。

**新增 16 个条目，每条都有可追溯来源**（`*` = 该成员由本次生成加入 `ObservedMembers`）。前 2 条是 Hook（`evidenceLevel: metadata`，无 RVA 锁），其余 14 条是行为观察新读的游戏 API：

| 条目 | 来源记录 |
| --- | --- |
| `ai-perception.Enemies.EnemyDetection.UpdateTargets.0` * | `hookMembers[0]`（RVA 0x156CF60） |
| `birthing-scout.ES_ScoutDetection.OnTargetRegistered.0` * | `hookMembers[1]`（RVA 0x16EA090） |
| `ai-perception.Enemies.EnemyDetection.get_m_ai.0` * | `readFields[2]`（`EnemyAI m_ai` @0x18） |
| `ai-perception.Enemies.EnemyDetection.get_m_biggestDetectionBuildup.0` * | `readFields[3]`（`float m_biggestDetectionBuildup` @0x50） |
| `ai-perception.Enemies.EnemyAI.get_m_behaviour.0` * | `readFields[7]`（`EnemyBehaviour m_behaviour` @0x48） |
| `ai-perception.Enemies.EnemyAI.get_m_detection.0` * | `readFields[8]`（`EnemyDetection m_detection` @0x58） |
| `movement-space.Enemies.EnemyAI.get_m_locomotion.0` * | `readFields[6]`（`EnemyLocomotion m_locomotion` @0x40） |
| `ai-perception.Enemies.EnemyBehaviour.get_m_ai.0` * | `readFields[0]`（`EnemyAI m_ai` @0x90） |
| `ai-perception.Enemies.EnemyBehaviour.get_m_currentStateName.0` * | `readFields[1]`（`EB_States m_currentStateName` @0x99） |
| `movement-space.Enemies.EnemyLocomotion.get_ScoutScream.0` * | `readFields[14]`（`ES_ScoutScream ScoutScream` @0xF0） |
| `birthing-scout.Enemies.ES_ScoutScream.get_m_state.0` * | `readFields[15]`（`ScoutScreamState m_state` @0x68） |
| `birthing-scout.ES_ScoutDetection.get_m_owner.0` * | `readFields[16]`（`EnemyAgent m_owner` @0x68） |
| `ai-perception.Agents.AgentAI.get_IsTargetValid.0` * | `hookMembers[7]` |
| `ai-perception.Agents.AgentAI.get_Target.0` * | `hookMembers[8]` |
| `ai-perception.Agents.AgentTarget.get_m_position.0` * | `readFields[10]`（`Vector3 m_position` @0x30） |
| `ai-perception.Agents.AgentTarget.get_m_agent.0` * | `readFields[9]`（`Agent m_agent` @0x18） |

另有 6 个既有条目的 `forgeCallers` 被刷新（`identity.Agents.Agent.get_GlobalID`、`identity.Enemies.EnemyAgent.get_IsSetup`、`identity.Enemies.EnemyAI.get_m_enemyAgent`、`identity.Agents.Agent.get_Alive`、`ai-perception.Enemies.EnemyAgent.get_AI`、`movement-space.Enemies.EnemyLocomotion.get_CurrentStateEnum`），调用者来自 `EnemyBehaviorObserver`、`EnemyBehaviorFactsObserver`、`EnemyEntityObserver` 与新增的 `EnemyModule::ObserveEnemyBehavior`/`::ObserveScoutDetection`/`::ResolveInstance`、`EnemyBehaviorPump::Postfix`。`dataEvidenceFiles` 新增 `enemy-behavior-hooks.json` 的内容哈希锁（`AC02FC58…342ED`），它是本批新条目的数据来源。

**审计器同批改了一处数据指针语法。** `dataEvidence` 指针原来只支持 `name` 与 `name[*]`，本批的索引形式 `readFields[N]` 需要 `Resolves` 支持 `name[N]`（越界或非数字判为不可解析）。改动中一度把 `[*]` 段的后缀剥离写漏（`enemies[*]` 会拿整段去查属性名），基线里 19 条 `[*]` 指针因此全部失败；修正后基线与生成物分别回到各自的 772/780 与 861/861，全部 38 条 `data-evidence` 检查（既有 22 条 + 新增 16 条索引指针）通过。

**本批实际执行的命令与结果**（`$out` = `%TEMP%\enemyevid`，`$bepinex`/`$game`/`$enemy` 同[复跑](#复跑)，`$game` 是从只读 stage 目录读的 QA profile interop 与 `GameAssembly.dll` 副本，未写入任何 profile）：

| 命令 | 结果 |
| --- | --- |
| `dotnet build ForgeEnemy/tests/NativeEvidence/NativeEvidence.csproj -c Release --artifacts-path $out "-p:GTFOBepInExPath=$bepinex"` | 退出码 0，0 警告 0 错误 |
| `dotnet $out/bin/NativeEvidence/release/NativeEvidence.dll --generate $bepinex $enemy ForgeEnemy/evidence/native-api-20403457.json ForgeEnemy/evidence/native-api-20403457.json` | 退出码 0，`methods=153 (+16), refreshed=6`，`relocked` 无 |
| `dotnet build ForgeEnemy/Native/ForgeEnemy.Native.csproj -c Release --artifacts-path $out "-p:ForgeRuntimeAssembly=<并发任务已构建的 ForgeRuntime.dll>" "-p:ForgeFrameworkAssembly=<同一批 ForgeRuntime.Framework.dll>"` | 退出码 0，0 警告 0 错误（clean 重编） |
| `dotnet $out/bin/NativeEvidence/release/NativeEvidence.dll $bepinex $game $enemy ForgeEnemy/evidence/native-api-20403457.json "$out/native.json" ForgeEnemy/evidence/e10-damage-window.json` | 退出码 0，`PASS 861/861`；`enum-constants=7, evidence:metadata=150, evidence:static-native=3, use:damage-window=1, use:forge-call=63, use:forge-hook=7, use:inventory=83` |
| `python ForgeEnemy/tests/NativeEvidence/verify_negative_cases.py $out/bin/NativeEvidence/release/NativeEvidence.dll $bepinex $game $enemy ForgeEnemy/evidence/native-api-20403457.json "$out/native-negative"` | 退出码 0，`PASS 31/31`（22 项规格变异 + 9 项伤害窗口变异），baseline 在脚本内先跑一次同样通过 |
| `dotnet $out/bin/NativePlugin/release/NativePlugin.dll "$out/NativePlugin.report.json"` | 退出码 0，`PASS 25/25`，0 警告 0 错误 |
| `dotnet $out/bin/BehaviorObservation/release/BehaviorObservation.dll "$out/BehaviorObservation.report.json"` | 退出码 0，`PASS 50/50`，0 警告 0 错误 |
| `dotnet $out/bin/LifecycleFacts/release/LifecycleFacts.dll "$out/LifecycleFacts.report.json"` | 退出码 0，`PASS 52/52`，0 警告 0 错误 |

**计数口径。** 780 与 861 的差是 81，全部来自本次新增的 16 个条目：每个条目加 `forge-use`、`evidence-level`、`call-phase`、`data-evidence` 各 1 项（64），每个新签名加声明解析 1 项（16），`dataEvidenceFiles` 加 `enemy-behavior-hooks.json` 的 `data-file` 1 项。伤害窗口 178 项、`steam`/`hash`/`mvid`/`native`/`damage`/`enum`/`data-file`/`data-evidence` 之外的既有检查数都没变；刷新后的分布对应 `use:forge-call=63`（49→63）、`use:forge-hook=7`（5→7）、`evidence:metadata=150`（134→150），`use:inventory` 仍 83。伤害窗口一项在本批报告里是 178 个检查 id，早先几节写的 194 是合并前把 8 个窗口检查重复记账两次时的总数，两处不矛盾。

**基线对照。** 用新审计器跑刷新前的规格仍是 `FAIL 772/780`，失败集合就是上列的 8 项；生成物跑审计是同一次运行里的 `PASS 861/861`，两者只差规格文件本身。

**本批未改生产源码。** `ForgeEnemy/Native/EnemyNativeHooks.cs` 无需修改：`EnemyBehaviorPump.Postfix` 已经写成 `if (__instance.m_ai is { } ai) module.ObserveEnemyBehavior(ai);`，`EnemyDetection.m_ai` 的 interop 属性本身可空（指针为零时返回 null），跳过分支是显式且真实的；`ForgeEnemy.Native` 与 `tests/NativePlugin` 都用 clean 重编确认 **0 警告**，`CS8604` 已不复现（此前记录里那条可见于测试工程编译的警告来自更早的源文件状态，未用 `!` 压制）。

**边界。** 只到元数据、IL 与 PE 指令层：未启动游戏、未安装、未写任何 profile、未 commit/push。新增条目的原生存在性来自该构建的 interop 与 dump 证据，`nativeRva` 只作取证记录（`evidenceLevel: metadata`），不是运行期读数。

**规格自身的哈希记录。** 刷新后 `evidence/native-api-20403457.json` 的 sha256 是 `8D4008DB…FE07F`（记录，不是活锁）。内嵌的 interop 哈希与 MVID 未改，运行中 `hash.*` 与 `mvid.*` 全部相符；`e10-damage-window.json` 未改动（`79944666…5BD3`），`enemy-behavior-hooks.json` 的锁值随本批写入规格。

## `enemy-type` 挂载（2026-09-15）

网站导出的敌人挂载 `{kind:"enemy-type", reference:"<EnemyDataBlock.persistentID 的十进制文本>"}` 现在由 ForgeEnemy 自己认领。`EnemyModule` 构造时按需注册 `AttachmentMatchers["enemy-type"]`（`EnemyModule.cs:108-109`），匹配规则是 `category == null` + 严格解析引用 + **本 provider 自己的实例** + 该实例的官方块 id 相等（`EnemyModule.cs:200-217`）。

**引用语法按 ruling 61 取窄。** `TryEnemyTypeId` 用 `uint.TryParse(reference, NumberStyles.None, InvariantCulture)` 加往返字符串相等（`EnemyModule.cs:214-217`）：`NumberStyles.None` 已排除符号与空白，往返相等排除前导零，`uint` 排除超范围。因此 `007`、`+7`、`-7`、`4294967296`、`7.0` 都是**能装进计划但永不匹配**的拼写；空串与带空白的引用在 `RuntimeJson.Text` 处就被 `invalid-string` 拒绝，匹配器不会看到它们。分类（`map-object` 才有的那个字段）不是本挂载的语义，非 null 一律 false。

**主体必须是本域实例。** 匹配先 `Resolve(subject)` 拿本模块登记的生命；主体属于别的域、或该引用不是本模块的存活实例，直接 false。读取块 id 之后**再解一次**（`EnemyModule.cs:212`）：块 getter 是原生调用，回调可以在读的中途退役生命或换实例，所以“曾经可读”不算数。

**原生读取走注入的缝。** 新增 `Native/Observation/EnemyTypeReader.cs`（`Read`：判 null 指针与 `IsSetup` → 读 `EnemyAgent.EnemyData` → 读块 `persistentID` → 回读块身份 → 异常与不可读一律 null），由 `EnemyPluginSession.cs:37-38` 作为第二个可选委托交给模块。这样做的原因是 `EnemyModule` 编译进 ReceiverProbe/CommitAudit 时用的是 `ForgeRuntime/tests/GameBindings/GameDoubles.cs`（ForgeRuntime 不可改，那份替身没有 `EnemyData`），原生读取必须留在 Observation 层。**插件未传读取器时不注册这个 kind**：三参数构造的模块（含 `Release/export-runtime-manifest`）会让带 `enemy-type` 挂载的计划在加载期以 `attachment-kind` 被拒，而不是被接受却永不派发。

**证据刷新。** 两行早已冻结的成员本次多了一个 Forge 调用者，走 `--generate` 而不是手写哈希：

| 条目 | 签名 | `forgeCallers` |
| --- | --- | --- |
| `spawn-requirements.Enemies.EnemyAgent.get_EnemyData.0`（`native-api-20403457.json:622-637`） | `GameData.EnemyDataBlock Enemies.EnemyAgent::get_EnemyData()` | `EnemyTypeReader::Read` |
| `spawn-requirements.GameData.GameDataBlockBase\`1<GameData.EnemyDataBlock>.get_persistentID.0`（同文件 `863-877`） | `System.UInt32 GameData.GameDataBlockBase\`1<GameData.EnemyDataBlock>::get_persistentID()` | `EnemySpawnRequirementSource::Enemies`、`EnemyTypeReader::Read` |

`Enemies.EnemyAgent.get_IsSetup.0` 的 `forgeCallers` 同样多了一条（`EnemyTypeReader::Read`）。生成物只改这 3 个 `forgeHooks`/`forgeCallers` 派生字段（与工作区文件差 7 增 3 删），`methods=153 (+0), refreshed=3`，没有新增条目、没有新的 `ObservedMembers` 记录、没有人工改哈希；刷新后再跑一次 `--generate` 得到 `refreshed=0` 且字节相同（sha256 `04613D55…EED8C`）。刷新前审计是 `FAIL 858/861`，失败正是这 3 条 `forge-use.*`；刷新后 `PASS 861/861`。

**本批实际执行的命令与结果**（`$out` = `%TEMP%\enemyattach\final`，`$bepinex` = QA profile 的 BepInEx，`$game` = `E:\SteamLibrary\steamapps\common\GTFO` 只读，`$dump` = 该构建的 Il2CppDumper 输出，全程未启动游戏、未写 profile）：

| 命令 | 结果 |
| --- | --- |
| `dotnet build ForgeEnemy/Native/ForgeEnemy.Native.csproj -c Release --artifacts-path $out -p:ForgeRuntimeAssembly=… -p:ForgeFrameworkAssembly=…` | 退出码 0，0 警告 0 错误 |
| `dotnet $out/bin/NativeEvidence/release/NativeEvidence.dll --generate $bepinex $enemy ForgeEnemy/evidence/native-api-20403457.json $out/native-api-20403457.generated.json` | 退出码 0，`methods=153 (+0), refreshed=3`，只刷新 3 条调用者集合 |
| `dotnet $out/bin/NativeEvidence/release/NativeEvidence.dll $bepinex $game $enemy ForgeEnemy/evidence/native-api-20403457.json $out/reports/NativeEvidence.json $out/reports/e10-damage-window.json` | 退出码 0，`PASS 861/861`；`use:forge-call=64`（63→64）、`use:inventory=82`（83→82），其余分布不变 |
| `python ForgeEnemy/tests/NativeEvidence/verify_negative_cases.py $out/bin/NativeEvidence/release/NativeEvidence.dll $bepinex $game $enemy ForgeEnemy/evidence/native-api-20403457.json $out/native-negative --damage-window $out/reports/e10-damage-window.json` | 退出码 0，`PASS 31/31`，含 `forge-caller-fabricated` 仍被抓住 |
| `dotnet $out/bin/ReceiverProbe/release/ReceiverProbe.dll $out/reports/ReceiverProbe.json` | 退出码 0，`PASS 61/61`（48→61，新增 13 个挂载用例） |
| `python ForgeEnemy/tests/ReceiverProbe/verify_mutations.py --sdk $sdk --output $out/receiver-mutants` | 退出码 0，`PASS baseline+mutants 15/15`（12→15，新增 3 个挂载变异各自只被对应用例抓住） |
| `dotnet $out/bin/NativePlugin/release/NativePlugin.dll $out/reports/NativePlugin.json` | 退出码 0，`PASS 26/26`（25→26，新增“插件加载后 `enemy-type` 计划可加载并对该实例派发”） |
| `dotnet $out/bin/EntityObservation/release/EntityObservation.dll $out/reports/EntityObservation.json` | 退出码 0，`PASS 78/78`（72→78，新增 6 个读取器用例） |
| `dotnet $out/bin/CommitAudit/release/CommitAudit.dll …` / `NativePlugin` / `BehaviorObservation` / `LifecycleFacts` / `SpawnRequirements check` | 分别 `PASS 68/68`、`26/26`、`50/50`、`52/52`、`49/49`，与上批基线同值 |
| `python ForgeEnemy/tests/LifecycleFacts/verify_mutations.py --sdk $sdk --output $out/lifecycle-mutants` | 退出码 0，`PASS baseline+mutants 7/7` |
| `dotnet $out/bin/NativeLayout/release/NativeLayout.dll $bepinex $host $sdk $enemy cutover $out/reports/NativeLayout-cutover.json` | 退出码 0，`PASS 44/44` |
| `dotnet $out/bin/DamageWindow/release/DamageWindow.dll $game $dump $out/reports/e10-damage-window.json` | 退出码 0，与冻结件字节相同（`79944666…5BD3`） |
| `python -m unittest -v test_guard`（在 `ForgeEnemy/tests/CutoverGuard` 下） | 退出码 0，`Ran 16 tests … OK` |

**Release 侧不声明挂载种类。** `Release/export-runtime-manifest` 用四参数构造 `EnemyModule`（因此新参数必须是可选的），导出的是 `kernel.ExportManifest()`；清单只含 `schemaVersion`、`runtime`、`registry`（`RuntimeRegistry.Snapshot()` = providers/capabilities/bindings，`RuntimeRegistry.cs:271-275`）、`bindingSupport` 与 `limits`，结构上没有挂载种类这一栏。本批实跑该工具（`5 player packages, runtime 1.2.0 on game build 20403457`，退出码 0，清单 33052 字节），全文匹配 `enemy-type|gear-block|map-object|attachment` 为 **0 处**，即预期差异为空。`Release/` 未做任何修改。

**计数口径。** ReceiverProbe +13 = 加载并派发 1、异类型 1、同场景仅本类型派发 1、非法拼写 5、加载期拒绝 2、异域主体 1、读中被退役 1、未注册 kind 1。EntityObservation +6 = 正常读到块 id 1、不可读 5（缺块、零指针、未 setup、读中被换块、getter 抛错）。NativePlugin +1。三个挂载变异分别锚在 `TryEnemyTypeId` 的严格解析、读后重解、以及“无读取器不注册”。

**未跑与旁证。** `NativeLayout pending` 在本工作区是 `FAIL 42/44`（`host.receiver-placement`、`host.exact-hook-set`），原因是并发的宿主任务已把 `ForgeRuntime/GameBindings` 切到 cutover（同一程序集跑 `cutover` 模式 `PASS 44/44`，宿主里已无 `EnemyModule` 类型与旧世界 Hook），与 ForgeEnemy 侧无关；上批基线用的是切换前的宿主 dll。另：`LifecycleFacts` 的 2 条 `CS8631` 警告来自 `IntegrationCases.cs:15`，是本批之前就存在的。

**边界。** 匹配规则的正确性只在托管替身上验证：真实 `EnemyDataBlock.persistentID`、真实 `EnemyAgent.EnemyData` 的读取只到 interop 签名、IL 与元数据层（`evidenceLevel: metadata`），未启动游戏、未进关卡、未验证多人同步，也未验证网站导出的计划文本在真实 MTFO 数据下的取值；未 commit/push。

## 伤害动作（2026-09-15）

`forge.action.combat.damage` 与它引用的 `forge.result.combat.damage` 结果形状写进 SDK 的 `CombatContracts.cs`（`CombatContracts.cs:210-327`，第 6 个 canonical 定义）；ForgeEnemy 注册同名能力、`gtfo.enemy.damage` 绑定（role `execute`、status `implemented`）与 HandlerShape。处理函数只对本 provider 自己解析出的存活敌人实例生效：每个接收者写一行结果，顺序与计划一致；失效引用、缺接收器、接收器 owner 与实例不符、目标已死或生命非正、生命/上限非法、肢体 id 解析不了各自带 code 拒绝，不静默跳过。原先把主机闸门放在反射式“本地闸门”里的那层实现（含可选写入器注入与条件 registry 行）已被取代并删除，写路径只剩 `EnemyNativeWrite.ApplyDamage` 一处。

**契约与授权目录逐字一致。** 端口 id、类型、多值、结构参数、recipients 与结果列都取自网站目录 `catalog/capability-catalog.json` 的同一行：inputs = `in`(execution)、`targets`(entity, many)、`source`(entity)、`instigator`(entity)、`amount`(number, hp)、`damage_kind`(enum, `damage_kind`)、`limb`(integer, optional)；outputs = `next` 与 `result`（schema `forge.result.combat.damage`，列 `target`/`status`/`committed`/`code`/`amount`/`target_count`）；`mitigation_policy` 是 `required` 的结构参数，值 `receiver_rules`/`ignore_armor`/`explicit_profile`；recipients = `targets`→`entity`/`many`/`health.damage`/`result`。**一处形状差异（目录事实，不是模组侧自创）：目录的伤害行没有 `codes` 数组**，heal 行有；SDK 与目录一致地没有它，所以伤害的拒绝码只出现在处理函数与测试里。ReceiverProbe 的 `contract.damage-row-verbatim` 从网站仓路径参数读该目录，逐字段比较 kind/label/description/graph/前四列与绑定广告；未给路径时进程以退出码 2 打印用法退出，路径下没有目录文件时抛错退出 1，都不跳过。

**复核修正：canonical 版本与授权副本（2026-09-15）。** 独立复核发现该行在 SDK 里仍是 `version: "1.0.0"`，而网站 `site/forge/logic-primitives.ts` 的内置定义已整体收敛到 `logicContractVersion = "2.0.0"`，且每行 `parameters` 都带 `summary`（=目录 `descriptionZh`）与 `summaryEn`（=目录 `descriptionEn`）。逐字段离线比对（仓库外只读脚本，导入网站 `logic-primitives.ts` 并读目录行）显示差异有三处：version、`parameters.summary`、`parameters.summaryEn`——不只是版本。已把该行改成 `version: "2.0.0"` 并补齐这两个授权副本字段（`description` 与目录 `descriptionZh` 本就逐字相同），同时把 `tests/ReceiverProbe/Program.cs` 的 `action.ports-match-contract` 里 `ResolveGraphContract("forge.action.combat.damage", "1.0.0")` 改成 `"2.0.0"`：内核按合同锁定版本解析，版本不符会以 `capability-version` 拒绝。改后该行与网站当前定义逐字段相同，`validate-t1.py` 的 TypeScript 段实际打印 `PASS: SDK canonical equals the locked website definition: forge.action.combat.damage`。

**T1 尚未整体转绿，阻塞不在本行。** 同一次 `validate-t1.py`（`--out %TEMP%\dmgaction-t1-fix`）在排序中的下一行 `forge.action.combat.heal` 停下：网站定义要求 `parameters.summary`/`summaryEn`，SDK 行没有。其后的 4 条 `forge.trigger.*`（`damage_applied`/`health_changed`/`limb_broken`/`death_started`）差异更大：网站 16:57 的在途改动把除 `forge.action.combat.*` 以外所有行的 owner 改成 `forge.contract.logic`、version 改成 `2.0.0`、`parameters` 补 `summary`/`summaryEn`/`support`，`death_started` 的 `description` 也改了。owner 必须等于注册它的 provider id（内核 `capability-owner`），所以这 4 行要跟上就得迁到 `forge.contract.logic` 的合同模块，属触发器形状归属迁移批次，不在本任务范围；TypeScript 段失败后 C# 的 `check`/`wire` 两段没有执行。同一软件快照下 heal 与这 4 行的差异清单见 `%TEMP%\dmgaction\canonical-vs-website-postfix.txt`。

**三条 unverified 的定案（只读解码 `GameAssembly.dll`，Steam build 20403457；未启动游戏、未进多人）。** 结论同时写进 `evidence/e10-damage-window.json` 的 q4 与 `tests/NativeHealth.cs` 的窗口校验：

| 问题 | 定案 | 证据 |
| --- | --- | --- |
| 客机调用是否本地生效 | **会**。原生入口不是主客机闸门 | `Dam_SyncedDamageBase.Setup`（`0x16201B0`）把 `m_onlyToMaster`（+0x25）写成“所有者类型是否为 PlayerBot”；`Dam_EnemyDamageBase.get_DamageBaseOwner`（`0x503EF0`）是 `mov eax,2; ret`（Enemy），所以本 provider 的接收器该字段恒为 0；`SendLocally`（`0x161F4E0`）在字段为 0 时 `mov al,1; ret`，不读网络主机位 |
| 非主机是否跳过发包 | **不跳过**。`SendPacket`（`0x161F5A0`）走同一闸门，同样在读网络标志之前返回真；包的实际路由由包层决定，本次不解码、不声称 | 同上两条窗口解码。因此“只有主机应用”只能由本 provider 自己的 `SNet.IsMaster` 门槛保证，非主机按权限失败语义上报（`authority-or-phase`，无 commitState） |
| 被拒绝与零伤害能否区分 | **不能**。`BulletDamage` 返回 void；`ProcessReceivedDamage`（`0x137E570`）的 Boolean 只在 `ReceiveBulletDamage`（`0x137EF68`）/`ReceiveMeleeDamage`（`0x138076D`）内部被消费，提交侧之后只能读 `get_Health` | 生命没有变化一律报 `unknown`（`damage-unseen`），绝不默认成功；提交抛错、提交后实例或接收器变化、读回非法或上升同样 `unknown` |

附带定案：**空攻击者安全**。`ProcessReceivedDamage` 只把攻击者交给 `EnemyAgent.RegisterDamageInflictor`（`0x1562BD0`），该入口对 null 直接返回，所以本 provider 提交时不伪造攻击者（传 null）；`source`/`instigator` 仍作为合同要求的实体引用由内核校验。

**证据刷新。** `evidence/e10-damage-window.json` 新增 4 个 reviewed 解码窗口（`SendLocally` `0x161F4E0`、`SendPacket` `0x161F5A0`、`Setup` `0x16201B0`、`get_DamageBaseOwner` `0x503EF0`，并带 `stopAtReturn` 字段），`nativeRvaLock` 多 2 行，q2/q4 文本按上表改写；`damageMethods` 35→37、`reviewedEntries` 3→7，`callEdges`/`callSiteWindows` 仍是 32/32（同一批调用边，未重扫）。新 sha256 `FA849FDC…013B`（旧 `79944666…5BD3`），重跑 `tests/DamageWindow` 与仓库内文件字节一致。`tests/NativeHealth.cs` 新增 6 条窗口校验（`damage.window.local-gate`/`send-gate`/`setup-gate`/`enemy-owner`/`receive-slot`/`inflictor-null-guard`），全部只读字节，不加载也不调用游戏方法。`evidence/native-api-20403457.json` 走 `--generate` 刷新：`methods=154`（+1，`Dam_EnemyDamageBase.BulletDamage`）、首次 `refreshed=8`，只有 `forgeCallers`/`dataEvidenceFiles`/新行变化，无人工改哈希；本轮再跑一次 `--generate` 为 `refreshed=0` 且与仓库文件字节一致（`CD1E2B67…7A1E`）。

**计数口径（ReceiverProbe 75 = 61 + 14）。** 新增 13 个动作用例：主机提交 1、非主机权限 1、失效生命 1、缺接收器 1、目标已死 1、生命未变报 unknown 1、提交抛错 1、多接收者顺序 1、肢体 id 解析 1、种类下标越界 1、不支持的 mitigation 1、数量边界 1、端口与合同一致 1；加 1 个目录逐字比对用例。4 个伤害变异（非主机仍提交、unknown 行改报 committed、生命未变即报提交、肢体 id 当数组下标）各自只被对应用例抓住。

**本批实际执行的命令与结果**（`$out` = `%TEMP%\dmgaction\final4`，`$bepinex` = QA profile 的 BepInEx，`$game` = `E:\SteamLibrary\steamapps\common\GTFO` 只读，`$website` = 网站仓根，全程未启动游戏、未写 profile、未安装任何包）：

| 命令 | 结果 |
| --- | --- |
| `dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release --artifacts-path $out/build` | 退出码 0，0 警告 0 错误 |
| `dotnet build ForgeEnemy/Native/ForgeEnemy.Native.csproj -c Release --artifacts-path $out/build -p:ForgeRuntimeAssembly=… -p:ForgeFrameworkAssembly=…` | 退出码 0，0 警告 0 错误 |
| 10 个测试工程各自 `dotnet build … -c Release --artifacts-path $out/build` | 10/10 退出码 0、0 错误；`LifecycleFacts` 2 条 `CS8631` 警告是本批之前就存在的 |
| `dotnet $out/build/bin/ReceiverProbe/release/ReceiverProbe.dll $out/receiver-probe.json $website` | 退出码 0，`PASS 75/75 receiver probes; failed 0` |
| `dotnet …ReceiverProbe.dll $out/probe-missing.json`（不给网站目录） | 退出码 2，打印用法后退出；再给一个不存在的目录则退出码 1 抛错——目录比对不会静默跳过 |
| `python -X utf8 ForgeEnemy/tests/ReceiverProbe/verify_mutations.py --sdk $sdk --output $out/receiver-mutants --website $website` | 退出码 0，`PASS baseline+mutants 19/19; failed 0`，4 个伤害变异各自命中预期用例 |
| `dotnet …CommitAudit.dll` / `EntityObservation.dll` / `BehaviorObservation.dll` / `LifecycleFacts.dll` / `NativePlugin.dll` / `SpawnRequirements.dll check ForgeEnemy` / `NativeLayout.dll $bepinex $host $sdk $enemy cutover` | 退出码 0，分别 `PASS 68/68`、`78/78`、`50/50`、`52/52`、`26/26`、`49/49`、`44/44` |
| `python -X utf8 ForgeEnemy/tests/LifecycleFacts/verify_mutations.py --sdk $sdk --output $out/lifecycle-mutants` | 退出码 0，`PASS baseline+mutants 7/7` |
| `dotnet …NativeEvidence.dll --generate $bepinex $enemy ForgeEnemy/evidence/native-api-20403457.json $out/native-api.regenerated.json` | 退出码 0，`methods=154 (+0), refreshed=0`，与仓库文件字节一致 |
| `dotnet …NativeEvidence.dll $bepinex $game $enemy ForgeEnemy/evidence/native-api-20403457.json $out/native-evidence.json ForgeEnemy/evidence/e10-damage-window.json` | 退出码 0，`PASS 877/877`；`use:damage-window=1, use:forge-call=65, use:forge-hook=7, evidence:metadata=151, evidence:static-native=3` |
| `python -X utf8 ForgeEnemy/tests/NativeEvidence/verify_negative_cases.py … --damage-window ForgeEnemy/evidence/e10-damage-window.json` | 退出码 0，`PASS 31/31`（含窗口缺失/结论缺失变异被 `damage-window.*` 拒绝） |
| `dotnet …DamageWindow.dll $game <dump.cs> $out/e10.regenerated.json` | 退出码 0，`methods=37 packetTypes=8 callEdges=32 callSiteWindows=32`，与冻结件字节一致 |
| `dotnet build Release/export-runtime-manifest/… -c Release --artifacts-path $out/release` 与 `dotnet <dll> --release Release/release.json --output %TEMP%\dmgaction\runtime-manifest.json` | 退出码 0 / 0，清单含 `forge.action.combat.damage`（结果模式 `forge.result.combat.damage`，6 列）与 `gtfo.enemy.damage` 绑定（`execute`/`implemented`） |
| `dotnet $out_rt/bin/Framework/release/Framework.dll` | 退出码 0，`Timing/state checks: 116 passed`、`Execution result checks: 49 passed`、`Framework checks: 532 passed` |
| `dotnet $out_rt/bin/Architecture/release/Architecture.dll` | 退出码 0（该工程成功时不打印，失败即抛错；`$out_rt` = `%TEMP%\dmgaction\rt-tests`） |

**复核轮重跑（同一批命令，`$out` = `%TEMP%\dmgaction\final5`）。** 构建 `ForgeRuntime.csproj`、`ForgeEnemy.Native.csproj` 与 10 个测试工程全部退出码 0、0 错误（`LifecycleFacts` 仍有 2 条本批之前就存在的 `CS8631` 警告）；ReceiverProbe 75/75、变异 19/19、CommitAudit 68/68、EntityObservation 78/78、BehaviorObservation 50/50、LifecycleFacts 52/52 加变异 7/7、NativePlugin 26/26、SpawnRequirements 49/49、NativeLayout cutover 44/44、NativeEvidence 877/877 加负例 31/31、`--generate` 字节一致、DamageWindow 与冻结件字节一致、`export-runtime-manifest` 退出码 0——全部与上一轮计数相同。`ForgeRuntime/tests/Framework` 退出码 0（116 + 49 + 532 通过）、`tests/Architecture` 退出码 0。`validate-t1.py --site $website --out %TEMP%\dmgaction-t1-fix`：build 退出码 0，export 段 `PASS 184 export/architecture assertions`，typescript 段退出码 1——`forge.action.combat.damage` 的比对已通过，失败点是紧随其后的 `forge.action.combat.heal`，C# 的 `check`/`wire` 段因此没有执行。

**边界。** 只到静态证据、编译与托管替身等级：真实 `BulletDamage` 调用、护甲/弱点/友伤规则对数值的影响、主机与客机的复制定序、“无攻击者”在真实接收器与真实对局里的表现都没有在游戏里跑过；多人同步与包层路由未解码也未验证。三条定案来自离线反汇编，属于冻结证据而不是运行期读数。本批未改 `ForgeRuntime/Framework` 的其他文件、`ForgeTrigger`、`ForgeMap`、`ForgeWeapon`、`Release`、网站仓与两仓合同；未 commit/push。

## 攻击阶段事实（2026-09-15）

目录里的 `attack_windup` / `attack_active` / `attack_recovery` 三行在 `evidence/enemy-behavior-hooks.json` 的 `unverified` 里此前都是 `no-evidence`。本批只读反汇编（`GameAssembly.dll` sha256 `C6A5C3CD…7BF55`、Steam buildid 20403457、Il2CppDumper `dump.cs` sha256 `BF657C0E…DE1CC`，仓库外 Iced 1.21 工具）把它们逐行核实：**windup 与 active 有真实原生成员，recovery 没有**，所以只实现了前两行，第三行没有任何事件被造出来。取证明细写在仓库外的 `%TEMP%\enemy-e2-native\evidence-draft.json`（含每个调用点与字段偏移），仓库侧证据文件按本批边界未改。

**windup。** `ES_EnemyAttackBase.OnAttackWindUp(int animIndex, AgentAbility, int)` 是 vtable slot 33（+0x340），由 `DoStartAttack`（slot 32，RVA 0x17FC280）末尾在 0x17FC67A 虚分派；紧邻之前的 0x17FC668..0x17FC675 把 `ability+0x1C` 加上 `[rbx+50h]` 存进 `[rbx+5Ch]`（`m_performAttackTimer`），所以这个分派点是“前摇截止时间已写好”。5 个具体重写：`ES_ShooterAttack` 0x1804680、`ES_ShooterAttackFlyer` 0x1804050、`ES_StrikerAttack` 0x1804DF0、`ES_TankAttack` 0x1808320、`ES_TankMultiTargetAttack` 0x1809970。

**active。** `OnAttackPerform(Vector3)` 是 slot 35（+0x360），两处调用：`UpdateAttackTimingAndLineOfSight`（slot 34，RVA 0x17FCC80）在 0x17FCE83，前置 guard 读 `m_attackWasPerformed`（+0x8D）并在 0x17FCE64 写 1；以及 `DoStartAttack` 自己在 0x18042ED 写该标志后的尾分派，即零长度前摇可在同一次调用里连发两个成员。基类体 0x353190 是 `ret 0` 空桩，实现在 4 个重写里（`ES_ShooterAttack` 0x18044D0、`ES_ShooterAttackFlyer` 0x1803A70、`ES_StrikerAttack` 0x1804B20、`ES_TankAttack` 0x1808100）。

**recovery 无独立阶段。** `m_attackDoneTimer`（+0x58）与 `m_attackWindupDuration`（+0x50）在整份 dump 里只有声明，没有任何读写点；`CommonExit`（0x17FC1C0）只清 `m_overrideAttackTargetPos`；`ES_ShooterAttack.m_burstCoolDownBeforeExit` 由 `AbilityIsDone` 读回，属于 active 阶段内部，不是攻击后的恢复窗口。**该行应改形或删除，不得为了凑一行而伪造。**

**实现。** 新增 `Native/Observation/EnemyAttackFactsObserver.cs`（只读原生状态：按 `EnemyAI.m_enemyAgent` 找回当前生命、两次读数一致才返回、越权或失稳即丢弃）、`Native/EnemyAttackHooks.cs`（两条 Harmony patch：windup 用 Postfix 取 `animIndex`，perform 用 Prefix 读 `m_lastAttackIndex`）、`Native/EnemyModule.AttackFacts.cs`（主机闸门 + 发布）。`EnemyModule` 注册表新增两行 capability 与两行 binding，binding id 用本 provider 自己的 `forge.module.gtfo.enemy.binding.*`（与七条 behaviour 行同一约定），capability 指向目录 id。**每个生命一份攻击水印**（`Entry.AttackIndices`，随生命/世界一起丢弃）：同一 binding 同一 attack index 的重复读数不再发布，新 index、新生命、新世界照常发布；水印只在该次发布被内核接受后写入，被闸门或失稳丢掉的读数不会被记成“已发布”。perform 行不声明 `target` 端口（原生该成员没有目标参数，行里也没有该端口）。

**形状差异（目录事实）。** 目录的 `attack_windup` 行声明 `ability`（resource/`forge.resource.ability`，nullable）输出，但前摇背后没有任何原生对象是这种资源，注册的形状**不含该端口**，也不发布它；`attack_active` 目录里没有行，本批只作为实现内 capability+binding 注册（与 `scout_detection`/`scout_scream` 同样是模块自有）。两行的 attack index 都只用于判重，不作为端口发布（未声明的端口会被内核事件形状检查拒绝），因此消费者看不到事实属于哪一次攻击——这需要先改目录。

**本批实际执行的命令与结果**（`$out` = `%TEMP%\enemy-e2-native\iso`，`$bepinex` = 只读 QA profile 的 BepInEx，`$stage` = `%TEMP%\iso-game\1\2`，后者只放 `GameAssembly.dll` 副本与 `interop/Modules-ASM.dll` 副本，未写任何 profile，未启动游戏）：

| 命令 | 结果 |
| --- | --- |
| `dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release --artifacts-path $out` | 退出码 0，0 警告 0 错误 |
| `dotnet build ForgeEnemy/Native/ForgeEnemy.Native.csproj -c Release --artifacts-path $out -p:ForgeRuntimeAssembly=… -p:ForgeFrameworkAssembly=…` | 退出码 0，0 警告 0 错误 |
| `dotnet build ForgeEnemy/tests/BehaviorObservation/… -c Release --artifacts-path $out` | 退出码 0，0 警告 0 错误 |
| `dotnet $out/bin/BehaviorObservation/release/BehaviorObservation.dll $out/behavior.json` | 退出码 0，`PASS 71/71`（behaviour 50 + attack 21），报告 `factBindings` 含两条攻击 binding |
| `dotnet $out/bin/NativePlugin/release/NativePlugin.dll $out/plugin.json` | 退出码 0，`PASS 26/26` |
| `dotnet $out/bin/NativeLayout/release/NativeLayout.dll $bepinex $host $sdk $enemy cutover $out/layout.json` | 退出码 0，`PASS 50/50`（九条原生 Hook 逐条核对目标成员与 `__instance`） |
| `dotnet $out/bin/LifecycleFacts/release/LifecycleFacts.dll $out/lifecycle.json` | 退出码 0，`PASS 52/52` |
| `dotnet $out/bin/SpawnRequirements/release/SpawnRequirements.dll check ForgeEnemy $out/spawn.json` | 退出码 0，`PASS 49/49` |
| `dotnet build Release/export-runtime-manifest/… -c Release --artifacts-path $out` 与 `dotnet <dll> --release Release/release.json --output …` | 退出码 0 / 0，清单含 `binding.attack_windup`、`binding.attack_active`（`observe`/`implemented`） |
| `dotnet $out/bin/NativeEvidence/release/NativeEvidence.dll $bepinex $stage $enemy ForgeEnemy/evidence/native-api-20403457.json $out/native.json` | **退出码 1，`FAIL 674/681`（红，见下）**；`use:forge-hook=7`、`use:forge-call=65` |
| `dotnet …NativeEvidence.dll --generate $bepinex $enemy ForgeEnemy/evidence/native-api-20403457.json $out/regenerated.json` | **退出码 2**：`No reviewed area/data evidence for Enemies.ES_EnemyAttackBase::OnAttackWindUp; add it to ObservedMembers.` |

**攻击阶段事实的解除条件（红项，下一批做）。** `NativeEvidence` 的两处失败**不是本批引入的代码缺陷，而是冻结规格还没跟上实现**：两条 Hook 没有冻结归属、三个新读成员（`ES_EnemyAttackBase::get_m_lastAttackIndex`、`ES_Base::get_m_ai`、`ES_EnemyAttackBase::get_m_attackTarget`）不在规格里，另有个别既有条目的 `forgeCallers` 因新增调用者而漂移。修它需要同批两件事，且必须一起做（只做一件仍不绿）：

1. `tests/NativeEvidence/GenerateSpec.cs` 的 `ObservedMembers` 加 6 条：`Enemies.ES_EnemyAttackBase` 的 `OnAttackWindUp`、`OnAttackPerform`、`get_m_ai`、`get_m_lastAttackIndex`、`get_m_attackTarget`，以及 `Enemies.ES_Base` 的 `get_m_ai`（`m_ai` 的声明在 `ES_Base` 上），area 都取 `ai-perception`；
2. `evidence/enemy-behavior-hooks.json` 为这两条 Hook 加 `hookMembers` 记录、为三个字段加 `readFields` 记录（生成器按类型+成员名反查 `dataEvidence` 指针，缺记录会以 `No reviewed area/data evidence` 直接失败），随后 `--generate` 刷新规格，`dataEvidenceFiles` 的 `enemy-behavior-hooks.json` 内容哈希会随之 relock。

这两处超出本批“不改共享 evidence / 不改档案仓文件”的边界，因此本批只报告、不代改；取证结论已经落在 `%TEMP%\enemy-e2-native\evidence-draft.json`，上述 5 个成员的签名与 area 都可直接从那里抄。

**边界。** 只到静态反汇编、编译与托管替身等级：Harmony 打在 `ES_EnemyAttackBase` 的虚成员上能否在实机命中具体重写、同一次 `FixedUpdate` 内尾分派与计时分派的先后、以及主机/客机上的实际复制定序都没有在游戏里看过。本批未启动 GTFO、未安装到任何 profile、未改 `ForgeRuntime/**`、`EnemyModule.cs` 的既有行、`Plugin.cs`、`Release/**`、网站、目录与共享 evidence JSON；未 commit/push。

## 节点清单敌人批的集成（integ-enemy，2026-09-16）

把 `nl-enemy` 集成片段的 `ForgeEnemy/**` 条目落地，并做裁定 122 的第 2、3、6 条（第 1、4 条要改 `ForgeRuntime/**`，交 integ-kernel / integ-state）。

**落地内容。** 四个行动家族（control / combat / foam / behavior）与 wave、node 家族一起接进一个注册：`Native/EnemyActionFamilies.cs`（handlers / shapes / support / 三类行 / 一个 presentation 观众）、`Native/EnemyModule.cs`（合并两组家族、挂 `PresentationSessions`、新增 `CanPresent` 门）、`Native/EnemyHookInstall.cs`（一张安装表：native + node + wave 三族，`Plugin.Load` 只走这张表）、`Native/ForgeEnemy.Native.csproj`（编译清单）。`forge.action.enemy.mark` 改成 `execution: presentation`（122.3）：主机决定步进与观众，观众是 realm 的玩家列表（`SNet.SessionHub.PlayersInSession`），handler 走 `CanPresent`（不要 `SNet.IsMaster`），结果不提交（`Partial` + `commitState=none`），每行 `committed` 一律 `none`。六个 `forge.query.enemy.*` 行的 `kind` 从 `modifier` 改成 `state`：内核现在的规则是只有 `selector/condition/state` 的 observe 行才注册 evaluator（`RuntimeRegistry.WithModule`），`modifier` 行会被跳过并以 `unused-evaluator` 拒绝注册。删除 `EnemyWaveModule.AfterWaveCost` 一行死代码（它调用的 `EnemyWaveFacts.AfterWaveCost` 在 wave 批的最终状态里已不存在，短缺口行也已从 `EnemyWaveContract` 撤掉，没有任何 hook 或测试调用它）。

**实际命令与结果**（`GTFO_BEPINEX_PATH` 指向只读的 `Forge-MapEditor-QA` profile；artifacts 一律用隔离目录 `%TEMP%\integen\*`）：

| 命令 | 结果 |
| --- | --- |
| `dotnet build ForgeRuntime/Framework/ForgeRuntime.Framework.csproj`、`ForgeRuntime/ForgeRuntime.csproj`（Release） | 各 `Build succeeded`，0 错误 |
| `dotnet build ForgeEnemy/Native/ForgeEnemy.Native.csproj -c Release … -p:ForgeRuntimeAssembly=… -p:ForgeFrameworkAssembly=…` | 退出码 0，0 警告 0 错误 |
| `dotnet build ForgeEnemy/ForgeEnemy.csproj -c Release --artifacts-path $out` | 退出码 0，0 警告 0 错误 |
| `dotnet build/run ForgeEnemy/tests/EnemyControlActions` | `PASS 24/24 enemy control action cases; failed 0` |
| `dotnet test ForgeEnemy/tests/EnemyCombat` | **退出码 1，套件在本树里编不过（红项，见下）** |
| `dotnet build/run ForgeEnemy/tests/GlueFacts` | `25/25 passed` |
| `dotnet build/run ForgeEnemy/tests/EnemyBehavior` | `PASS 35/35 enemy behaviour cases; failed 0` |
| `dotnet test ForgeEnemy/tests/WaveFacts` | `Passed! - Failed: 0, Passed: 12` |
| `dotnet build/run ForgeEnemy/tests/EnemyNodeFacts` | `PASS 45/45 enemy node cases; failed 0`（含新增的 presentation 用例） |
| `dotnet build/run ForgeEnemy/tests/HookInstall <BepInEx> <ForgeEnemy.Native.dll> <report.json>` | `PASS 8/8 hook install checks; failed 0`（17 个 patch 类全部在安装表里，`Plugin` 只有一处 `Install` 调用） |

断线重启后的补跑（同一棵工作树，`artifacts` 用 `%TEMP%\integen-art`）：宿主 Release、托管 `ForgeEnemy` Release 与 Native Release 各退出 0；四个家族与 wave、node、hook 套件复跑为 `EnemyControlActions` 24/24、`GlueFacts` 25/25、`EnemyBehavior` 35/35、`EnemyNodeFacts` 45/45、`WaveFacts` 12/12、`HookInstall` 8/8，全部退出 0；`EnemyCombat` 复跑确认仍停在下面的红项。

**红项。** `EnemyCombat` 套件在合并后的树里编不过：它按老的部分清单编译生产 `EnemyModule.cs`，其中 `EnemyModule.AttackFacts.cs` 已不存在（本批已从 csproj 删掉该行并补了 `UnityEngine.CoreModule` / `UnityEngine.AIModule` 两个引用），之后停在合并态本身——`EnemyModule.cs` 引用 `AllNodeHandlers`、`ActionFamilyHandlers`、`NodeSupport`、`EnemyWaveContract`、`PresentationAudience`，而套件的 `GameDoubles.cs` 也没有 `EnemyAgent.IsTagged`/`EnemyTaggedTimer`/`CourseNode`、`Dam_EnemyDamageBase.AttachedGlueVolume`。修好它需要按 `EnemyControlActions` 的 shim 模式补一个 `EnemyModuleShim`，或把编译清单补成原生工程全集并扩替身；两条都不属于本批边界，本批只报告。`BehaviorObservation` 同样编不过（缺 `UnityEngine.AI` 引用，其后会撞同一组合并态符号），它测的是 behavior 观察泵而不是本批的行动家族，behavior 家族由 `EnemyBehavior` 覆盖。`tests/NativeLayout` 的 `enemy.hooks` 期望集仍是旧的 7 个名字，合并后实际是 17 个（native 7 + node 4 + wave 6），这一条按 `s-wave` 集成片段处理。

**边界。** 全部是托管替身、编译与静态程序集读取：没有启动 GTFO、没有安装到任何 profile、没有改 `ForgeRuntime/**`、网站目录或共享 evidence、没有 commit。必须进游戏验证：`forge.action.enemy.mark` 在 presentation 档下的真实可见性（两个客户端 + 主机自己的屏，含主机自发请求是否回环）、`PlaceCustomMarker` 的 `destroyDelay = 0` 语义、标记是否跟随敌人与跨 dimension 存活；四个家族的实机写入时序与主客机复制；wave 快照字段在实机上的含义。网站目录批需按本批合同改动收敛：`forge.action.enemy.mark` 的 `graph.execution` 现在是 `presentation`，六个 `forge.query.enemy.*` 行的 `kind` 是 `state`。




## 敌人包收尾批（integ-state2，2026-09-16）

裁定 122.1 的内核一半（`TriggerContracts` 已声明 `forge.trigger.entity.spawned`）落地后的包侧收尾，范围只在 `ForgeEnemy/**`。

**已落地。** `EnemyNodeTriggerContract` 删掉 `SpawnedRow`/`SpawnedRowJson`，`CapabilityRows`/`CapabilityIds` 收敛为两条自有的 tagged/glued 行，注释改为指向内核的触发器合同；`EnemyNodeFacts/Scene` 不再复述该行文本，改注册内核的 `TriggerContracts.Module()`。敌人死亡与回收都调内核 `ReleaseVariableScope("enemy", reference)`（scope 名取自 `VariableScopeKinds.Enemy`，即内核 `Variables` 测试用的同一拼写），落在 `AfterDeath` 与 `CompleteDespawn` 两处。行为泵的目标事实补上实现：`TargetAcquiredBinding`/`TargetLostBinding` 进 `BehaviorBindings`，`Behavior` 加目标水位，`target_acquired` 带可解析的 `target` 端口、不可解析时省略该端口，`target_lost` 只报敌人——这两个常量此前被 `BehaviorObservation` 引用而生产侧没有，是该套件编不过的原因之一。`tests/EnemyCombat`、`tests/BehaviorObservation` 改 shim 模式（各自不再编译 `EnemyModule` 核心的兄弟半成品，只编译本片生产源码 + 一个 `EnemyModuleShim`）。`tests/NativeLayout` 的 `enemy.hooks` 期望集改从 `EnemyHookInstall.cs` 的安装表读出（17 个类，`XxxHooks.Types` 每族再由该族的 `Types` 数组展开），不再手写名单。`tests/NativePlugin/GameDoubles.cs` 补上 `Dam_EnemyDamageBase.AddToTotalGlueVolume`/`AttachedGlueVolume` 与 `GlueGunProjectile`/`GlueVolumeDesc`（`EnemyNodeHooks` 编译所需，此前缺失）。

**命令与结果**（`GTFO_BEPINEX_PATH` = 只读 `Forge-MapEditor-QA` profile；artifacts 用隔离目录 `%TEMP%\s2art`）：

| 命令 | 结果 |
| --- | --- |
| `dotnet build ForgeEnemy/ForgeEnemy.csproj` | 退出 0，0 警告 0 错误 |
| `dotnet build ForgeEnemy/Native/ForgeEnemy.Native.csproj` | 退出 0，0 警告 0 错误 |
| `dotnet test ForgeEnemy/tests/EnemyCombat` | `Passed! Failed: 0, Passed: 35`（改 shim 后首次通过） |
| `dotnet run ForgeEnemy/tests/BehaviorObservation` | `PASS 50/50`（改 shim 后首次通过） |
| `dotnet run ForgeEnemy/tests/EnemyNodeFacts` | `PASS 45/45` |
| `dotnet run ForgeEnemy/tests/EnemyBehavior` | `PASS 35/35` |
| `dotnet run ForgeEnemy/tests/HookInstall` | `PASS 8/8` |
| `dotnet run ForgeEnemy/tests/NativeLayout … cutover` | `PASS 80/81`，唯一红项 `host.exact-hook-set`（宿主 DLL 状态） |
| `dotnet run ForgeEnemy/tests/NativeLayout … pending` | 78→79/81 区间，另有 `host.receiver-placement` 同因 |

**红项与阻塞。** `tests/LifecycleFacts` 编不过：它的 csproj 用 `../../Native/*.cs` 全量编译原生源码，而合入 control/combat/foam/behavior 四个家族后，这些文件引用的游戏类型（`AIGraph`、`UnityEngine.AI`、`NM_NoiseType`、`eDimensionIndex`、`ES_HitreactType`、`LimbForceApplicator`、`SurvivalWave`、`EnemyGroup`、`NavMarker`、`ToolSyncManager` 等）都不在 `NativePlugin/GameDoubles.cs` 的替身集合里；该套件本身声明的“只用替身、不引 interop”边界与全量编译清单已经互相矛盾，是合并批留下的既有缺口，不是本批改动引入。按裁定 139.3 的同一逻辑，正确解法是把 `LifecycleFacts` 也改成 shim 模式（只编译生命周期相关的生产源码 + `EnemyModuleShim`），本轮未做。本轮新增的三条 `enemy` scope 用例（死亡清理、回收清理、只清被回收的那条命）以及 `EnemyScopePlan` 随该套件一起编译，因此状态是“已写、未跑”；清理逻辑本身已随 `ForgeEnemy.Native` 零警告编译，并有两处调用点。

**边界。** 没有启动 GTFO、没有安装到 profile、没有改 `ForgeRuntime/**`、`ForgeWeapon/**`、`ForgeMap/**`、`Release/**` 或网站目录、没有 commit。必须进游戏验证：目标获得/失去两个事实在实机 AI 上的触发时机与端口内容；`target_acquired` 的 `target` 端口在被另一个 provider 拥有的目标上是否真的省略；死亡与回收两条路径上 `enemy` 作用域变量被清掉的实机时序（尤其死亡后同一 native 指针被对象池复用的情况）。

## 模组收尾批（mods-tail，2026-09-16）

裁定 164.2–6 的收尾：网站目录对齐与 `wave_exhausted` 补行、内核求值错误码、以及三个 Enemy 测试工程的清单/替身对齐。范围含网站仓（目录与 `FORGE-FRAMEWORK.md`）与 `ForgeRuntime/Framework`、`ForgeEnemy/tests`。

**网站。** `Tools/Forge/capability-research-source.json` 里 `forge.trigger.enemy.tagged`/`glued` 与 `forge.trigger.objective.wave_*` 的 domains 改为内核 `TriggerContracts.cs` 的同一集合，并新增 `forge.trigger.objective.wave_exhausted`（形状照内核：host、`wave` 句柄端口、`count` 输出、必填结构参数 `resource`；`capability-rules-trigger.ts` 与 `capability-copy.json` 同步）。`build-capability-catalog.ts --write` 重新生成 `catalog/capability-catalog.json`，随后 `--check` 通过（trigger 82 行）。`Docs/forge-contract/FORGE-FRAMEWORK.md` 的 I-CATALOG 端口段写明：事件输出上模组声明的 `optional` 等于网站目录的可空输出。

**内核。** `RuntimeKernel.Control.cs` 不再把 evaluator 抛出的 `RuntimeContractException` 统一改写成 `pure-evaluation-failed`：求值器自己的码原样上抛，只有不带码的失败才编码为 `pure-evaluation-failed`（`ForgeRuntime/Framework/README.md` 同句更新）。`EnemySelectorQuery/selector.relation-refused` 由此恢复为 evaluator 自己的 `relation-unsupported`。

**套件清单与替身。** `CommitAudit`、`ReceiverProbe`、`EntityObservation`、`EnemyProfileActions` 四个工程改为编译 `ForgeEnemy.Native.csproj` 的同一份生产清单加 `NativePlugin/*Doubles.cs`；各自那份会漂移的第二份替身（`EntityObservation/GameDoubles.cs`、`EnemyProfileActions/GameDoubles.cs`）已删除。生产镜像替身新增：`SFloat16.Preview`、`EnemyAgent.Position`/`EnemyData` 的读取钩子（`OnPositionRead`/`OnEnemyDataRead`，默认身份、不改其他套件行为）、`Enemies.SquidBossBehaviour`（阶段动作写入的 boss 行为机，`EnemyBehaviour` 相应去掉 `sealed`）。`CommitAudit`/`ReceiverProbe` 反射构造 `CommandContext` 处补上新增的 `isHost` 实参（true，两条路径都是宿主侧提交）；`ReceiverProbe` 的场景不再对同一世界调用两次 `BeginWorld(1)`。`EnemySelectorQuery/selector.source-refused` 按 164.5 改为断言「事件被忽略 + kind 不可用」。

**命令与结果**（SDK 由 `dotnet build ForgeRuntime/Framework/ForgeRuntime.Framework.csproj -c Release --artifacts-path .artifacts/dotnet` 现编，各套件以同样的 `-p:ForgeFrameworkAssembly=<该 dll>` 编译）：

| 套件 | 结果 |
| --- | --- |
| EnemySelectorQuery | PASS 14/14（原 12/14） |
| NativePlugin | PASS 26/26 |
| CommitAudit | PASS 68/68 |
| ReceiverProbe | PASS 75/75（原 14/75） |
| EntityObservation | PASS 78/78（原编不过） |
| EnemyProfileActions | PASS 22/22（原编不过） |
| LifecycleFacts | PASS 53/53（替身改动后的回归） |
| BehaviorObservation | PASS 50/50（同上） |
| SpawnRequirements | PASS 49/49（同上） |
| 网站 `build-capability-catalog.ts --check` | 退出 0，目录与源同步 |

**边界与遗留。** 没有启动 GTFO、没有安装到 profile、没有 commit。`Native/EnemyProfile*.cs`（阶段动作切片）已进 `ForgeEnemy.Native.csproj` 的编译清单并随本批编译通过（补上它缺的 `using System.Linq;`，见下）；本轮此前只让它的测试工程编过并通过。必须进游戏验证：`wave_exhausted`/`tagged`/`glued` 行由内核注册后宿主真实加载顺序下的注册结果与导出快照；`optional` 三端口在真实发布路径上是否足够；`SquidBossBehaviour.Phase` 与 `ActivateWeakspotIfNeeded` 在实机上是否就是阶段动作假设的那两个成员。
