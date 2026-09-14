# ForgeEnemy 验证记录

**上次更新：2026-09-14**（E2 批次；本文件合并自原 E1-BOUNDARY-HANDOFF、E1-NATIVE-CUTOVER、E1-PLUGIN-LIFETIME-SAFETY、E1-R3-INTEGRATION、E2-DELIVERY、E2-OBSERVATION-HANDOFF、E23-CONTINUATION、E3-LIFECYCLE-FACTS、E3-SAFETY-HANDOFF 九份交接记录）。

计划与状态见两仓统一框架第 6 节 U-ENEMY（链接见[仓库 README](../README.md)）；本文只记带日期的运行记录。全部结果都是 implementation-only 或离线数据等级，没有启动 GTFO。

## 最后一次记录的通过数

不同套件覆盖不同边界并共享用例，**任何两行都不得相加成独立机制数**。下表除"架构 / Framework"外都在 2026-09-13 E2 批次用同一份隔离构建重跑，命令与哈希见 [`evidence/e2-spawn-space-20403457/commands.json`](evidence/e2-spawn-space-20403457/commands.json)。

D-004（Heal 多目标、伤害与生命周期事实形状）之后，前四行在 2026-09-13 于 HEAD `d09d6bb` 加未提交的 D-004 改动上重跑（隔离构建，Native 与宿主 0 警告 0 错误）；测试计划已改为本地构造，阻塞项见[阻塞项与解除条件](#阻塞项与解除条件)。其余行未受 D-004 测试迁移影响，同一次重跑结果与表中一致。

R4（运行时枚举值端口，见下方阻塞项表）落地后，ReceiverProbe 与 CommitAudit 在 `feat/enum-value-ports` 于 HEAD `96d8756` 加未提交改动上重跑（隔离构建，0 警告 0 错误），下表两行已更新为新结果；其余行不受影响，未重跑。

| 套件 | 结果 | 口径 |
| --- | --- | --- |
| 生命周期事实（LifecycleFacts） | 50/52，BLOCKED 2；变体 7/7 | 死亡流程与肢体破坏；含原生 Hook 适配器；事实→Heal 联调阻塞（J-003） |
| 接收器（ReceiverProbe） | 43/44，BLOCKED 1；变体 8/8 | 唯一现行接收器；伤害用例已随 R4 恢复执行，仅 Heal 内核用例仍阻塞（J-003） |
| 实体观察（EntityObservation） | 66/66 | 通过实际共享 SDK |
| 行为观察（BehaviorObservation） | 22/22 | AI、移动状态、技能的只读观察 |
| 插件生命周期（NativePlugin） | 24/24 | 跨线程、卸载、失败清理；计划为 death_started → 记录 |
| 提交路径审计（CommitAudit） | 68/68，BLOCKED 0 | 现行路径 32（伤害 6 项已随 R4 恢复执行）+ 迁移边界 20；20 个是同一批治疗用例在两条路径上各跑一次，不算独立机制 |
| cutover 布局（NativeLayout） | 38/38 | Enemy 五 Hook、Runtime 四 Hook |
| 原生静态审计（NativeEvidence） | 544/544；工具检错 22/22 | 127 个签名 + 7 个枚举常量 + Forge IL 用法 + 数据指针；读元数据与 PE 指令，不加载也不调用游戏方法 |
| 出生空间要求（SpawnRequirements） | 48/48 | 离线数据合同 + Map 替身求解器 + 内容依赖 |
| 出生空间证据提取器 | 复现字节一致；锁检错 8/8 | 39 个敌人块、10 个基础 prefab |
| cutover CLI 防错（CutoverGuard） | 16 个 unittest OK | 真实 CLI 与退出码 |
| 架构 / Framework | 36 / 253 | **本批未重跑**，保留此前记录；属于 Runtime |

`ForgeEnemy.Native` 与完整宿主的零警告结果单独记录。Trigger 的测试工程构建有 3 条 NETSDK1138 目标框架提示，0 错误；本仓库没有切换目标框架。

## r11 heal 结果聚合与 21 码改名（2026-09-14）

`Native/EnemyModule.cs` 的 `Heal` 与 `Receivers/EnemyHealthCommit.cs` 的 `Execute` 全部 21 个拒绝码改成 `CombatContracts.cs` 已提交的 kebab-case 形式（如 `gtfo.enemy.overheal_unsupported` → `overheal-unsupported`），删除 `heal-no-state-change`；最终聚合按"有无 committed 行"重写而不是按 `unknown==0`/`facts.Count==0`：≥1 行 committed 且存在 rejected/unknown 时报 Partial（commitState 视是否有 unknown 行取 unknown/confirmed，facts 可为空）；0 committed 只剩 rejected 时报 Rejected/None；0 committed 有 unknown 时报 Failed/Unknown。随动改了 `CommitCases.cs`（两个用例改名并重写为 `-is-partial-confirmed`/`-is-partial-unknown`）、`AuditScene.cs`、`verify_mutations.py`（三处 mutation 字符串同步改名，`exception-none` 改指向 `EnemyModule.cs` 新的第一个 catch 块）、`GameBindings/Program.cs`、`ReceiverProbe/Program.cs` 的对应码字符串断言。

隔离构建，SDK 与三个测试工程 0 警告 0 错误：

| 套件 | 结果 |
| --- | --- |
| CommitAudit | 68/68，BLOCKED 0 |
| ReceiverProbe | 43/44，BLOCKED 1（`commit.kernel-unknown-no-retry`，见下方阻塞项表） |
| ReceiverProbe `verify_mutations.py` | baseline + 8 个 mutant 全部按预期检出，0 failed |
| LifecycleFacts | 50/52，BLOCKED 2（`integration.real-heal-death_started`、`integration.real-heal-limb_broken`，见下方阻塞项表） |
| LifecycleFacts `verify_mutations.py` | baseline + 7 个 mutant 全部按预期检出 |

**上表三个 BLOCKED 用例本批未解除**，尽管 Runtime 侧的 J-003（字面量/单转多输入加载）本批已经落地并经 `ForgeRuntime/tests/Framework --fixtures`（27 个站内负例逐条核对）与本表 CommitAudit 验证。根因是 `ForgeEnemy/tests/Shared/Blockers.cs` 的 `Heal` 常量在 `ReceiverProbe/Program.cs`、`LifecycleFacts` 里被无条件引用为阻塞，不读取任何运行时状态，文本仍写"J-003 未实现"。真正解除需要新写用 `LocalPlan` 从内核注册表构造 事实→Heal 计划并恢复 +5HP 断言（即下表"解除条件"一栏所写的工作），属于新增测试基础设施而不是简单改名，不在本次 J-003 loader 实现范围内，留作后续修复项。

## 阻塞项与解除条件

Enemy 套件的计划全部由 `tests/Shared/LocalPlan.cs` 从内核注册表本地构造（版本、权限、槽位都读注册表），不再读网站夹具。无法在当前合同下构造或加载的用例单列为 `BLOCKED n (原因)` 并逐条列出 id，**不计入通过**。有失败时退出码为 1；没有失败、只有阻塞时退出码为 0，但结论词是 `INCOMPLETE`，尾行写出阻塞数。

| 阻塞原因 | 用例 | 解除条件 |
| --- | --- | --- |
| J-003：没有合法的 Heal 计划 | LifecycleFacts `integration.real-heal-death_started`、`integration.real-heal-limb_broken`；ReceiverProbe `commit.kernel-unknown-no-retry`；GameBindings `bridge.configured-heal-plan-commits-5hp` | D-004 后 Heal 的 `targets` 是多值实体输入，事件实体是单值输出，另有必填 `amount` 与 `overheal_policy`。要等 J-003（字面量输入、单值接多值，FORGE-FRAMEWORK §8.2 D-006 ①②）在 SDK 落地。之后 Enemy 用例用 `LocalPlan` 构造事实→Heal 计划并恢复 +5 HP 断言；GameBindings bridge 改回读取网站按 J-002 导出重新生成的 `native-heal.plan.json`。 |
| J-002/J-003：网站夹具早于注册表 | GameBindings `fixtures.valid-plan-heal-dispatch`、`fixtures.invalid-plan-rejections` | `--fixtures` 先把网站有效计划的 capability/provider 版本与注册表逐项比对，不一致就整组阻塞（无效计划都是有效计划的单字段变体，版本不符会让它们因错误的原因被拒）。模组导出 J-002 清单、网站据此重生成夹具且 J-003 让 Heal 计划合法后，比对一致即自动恢复执行。 |

R4（运行时枚举值端口）已随本次改动解除：`damage_applied` 的 `damage_kind` 输出不再以 `unsupported-event-port` 拒绝订阅，`LocalPlan.Load` 也已移除把这一种拒绝转成阻塞的特判。原先列在此处的 ReceiverProbe 7 个伤害用例、CommitAudit 6 个 `existing-path.damage.*`、以及 ReceiverProbe 的 `observation-replay`/`late-damage-retargeted` 两个变体均已恢复执行并通过，见上表。

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

## 接收器安全修复的复现记录

原始 13 项探针先复现 8/13。**E2-002 一项没有被修成通过**：它只给出同指针的新 wrapper，没有可证明的新生命边界，失败证据保留。v2 显式加入"捕获旧生命 → 销毁 → 同指针重生 → 重放旧令牌"，同时验证 wrapper 本身不代表新生命；扩展后 34/34 通过。裸指针的迟到网络回调如何取得原生代次，仍待上面第 2 项核验。

插件生命周期的 18 项用例最初 12/18 通过、六个新用例全部失败；修复并扩展后 24/24。移除一层冗余外层检查后仍被下一层保护的一个探针单独记录，**不冒充检错成功**。

## E1 / R3 切换

唯一 `EnemyModule` 从 `ForgeRuntime/GameBindings/` 移入 `ForgeEnemy/Native/EnemyModule.cs` 并切换命名空间。Runtime 不再创建或清理 Enemy provider，只保留四个世界、会话与检查点 Hook；三个敌人 Hook（后来扩为五个）由独立的 Native 插件拥有，空的 `ModuleDefinition.Create` 已删除。所有实际的 receiver、bridge、插件、实体观察测试的源码引用同步更新，不保留兼容执行副本。Native 仍单向引用宿主与唯一 SDK；SDK 无 Unity/BepInEx 依赖，Runtime 无 Enemy 生产依赖。

R3 在既有 Registry 中登记了带所有权和容量校验的观察函数，注销时清理，并接上只读回调约束。跨模块测试进一步检出生命周期订阅注销的缺口：`RemoveLifecycleObserver` 现在只在实体观察期间拒绝修改，仍允许普通清理、停止后清理与生命周期回调自注销。

集成修改经过 25 个目标路径的哈希前置检查，没有恢复或覆盖并发的 Trigger 工作。首次全快照检查发现四个未拥有的 Trigger 文件发生变化，保留了它们的工作树并刷新测试副本。

## E3 事件的验证细节

D-004 之前记录的事件套件 52/52 包含原生 Hook 适配器（作为托管测试代码调用 prefix/postfix，不注入）、真实计划 → Heal 的 +5 HP 正例，以及"死亡目标不隐式复活"的负例。D-004 把 Heal 改成多值 `targets` 后，那个 +5 HP 正例已经无法构造合法计划，现为 BLOCKED（见[阻塞项](#阻塞项与解除条件)），**不再算作通过**。事件错误变体全部被指定断言检出；**编译失败不算通过**。

两处初始失败保留：首次 50 项用例检出"owner ID 变化而指针相同"的缺口，补上 owner ID 校验后通过；当时的 Heal 联调最初因夹具的权限与绑定列表未按 canonical ordinal 排序被严格加载器拒绝，修正夹具排序后通过——**没有降低加载器校验**。

## 输入锁

本机 Steam app 493520 / build 20403457；游戏与 BepInEx 只作只读输入。没有复制游戏 DLL、模型或第三方代码到可分发目录。

| 输入 | SHA-256 |
| --- | --- |
| GameAssembly.dll | `C6A5C3CD8CA5FE2A8C1A71A3D107663E8CBF01404820DBBDA2EA10C4BFD7BF55` |
| interop/Modules-ASM.dll | `A31AF38FAC3F96F1130CF7CDFCD10AE436E0D553EDA7163B9A9FF82392907943` |
| interop/GameData-ASM.dll | `3A74E6656CDA3A563290BBE71B70578E1E8D0745A5ECDDD05C894934A7A8CC7B` |
| interop/SNet_ASM.dll | `99175A1EF40454C1C8DD45D86D9EB9D3DF4D24891FB26ED649835BA25E7360B2` |
| GTFO_Data/resources.assets | `5833891d4f9d04c8ddb103e2f7feca5c67ed7ec70a7618374fa3cc51640aae5f` |
| GTFO_Data/sharedassets43.assets | `d2b5db1128577bdd48f68c61002106fdc60d100ad8b5e542f7748cd7d5db866e` |
| GTFO_Data/globalgamemanagers | `7825069c90c34fbb49e9e742197d4a6ef2abee09cd60b8193e2a3304c1ff6337` |
| GTFO_Data/globalgamemanagers.assets | `3bbbdc31f2d9c0a30bd2098a6710606d72708f6f73986d4d10f5a21ee7490ce6` |

冻结规格保存三个 interop 的 MVID；提取器另锁三个 DataBlock TextAsset 的内容哈希。**哈希和元数据证明这份输入的签名与资产数值，不证明原生参数语义、合法提交阶段、运行时组件状态、复制完成或性能。**

## 复跑

先按 [README 的构建入口](README.md#复跑) 得到 `$hostDll`、`$sdkDll` 与 `$enemyDll`（`bin/ForgeEnemy.Native/release/ForgeEnemy.Native.dll`），然后从仓库根运行，输出目录必须隔离。Enemy 套件不再读网站夹具，计划都从内核注册表本地构造。每个 `dotnet build` 都带 `--artifacts-path $out "-p:ForgeFrameworkAssembly=$sdkDll"`；NativeEvidence 与 NativeLayout 还要 `"-p:GTFOBepInExPath=$bepinex"`。

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

原生读取、离线数据、替身、编译和元数据是不同的证据层，任何一层通过都不代表游戏或多人已通过。GameBindings 各模式共享基础断言；D-004 后的重跑为无参 31、`--fixtures` 32 且 BLOCKED 2、`--bridge` 63 且 BLOCKED 1、`--native` 51。

没有安装、发布、启动游戏、修改用户 profile、Git 提交或推送。
