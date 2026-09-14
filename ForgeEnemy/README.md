# ForgeEnemy

敌人实例、健康与战斗接收器、AI 与感知与移动、弱点与技能，以及生成的空间要求。遭遇的数量、时机和分布归 Map；Enemy 不复制地图的遭遇调度。

计划与状态见两仓统一框架第 6 节 U-ENEMY（链接见[仓库 README](../README.md)），带日期的验证记录见 [VALIDATION.md](VALIDATION.md)。

## 工程结构

`ForgeEnemy/Native/ForgeEnemy.Native.csproj` 是真实插件，标记 `[BepInPlugin("NAinfini.ForgeEnemy", "Infini Forge Enemy", "1.0.0")]` 与 `[BepInDependency("NAinfini.ForgeRuntime", "1.2.0")]`。它只引用宿主与唯一 SDK，不附带第二份 SDK，通过公开的 `Plugin.Runtime` 注册同一个内核。唯一生产接收器是 `Native/EnemyModule.cs`；Runtime 不再创建 Enemy provider，也不再编译 Enemy Hook。

`ForgeEnemy/ForgeEnemy.csproj` 是托管辅助工程：`Receivers/` 只剩提交路径审计使用的治疗提交与线程边界；`Spawn/` 是交给 Map 的出生空间要求合同与内容依赖计算。它**不是另一个游戏插件，不应当作玩家发行包**。原来 `Receivers/` 下未被任何工程引用的身份表与伤害观察窗口是 `EnemyModule` 的重复实现，已删除。`ModuleDefinition.cs` 现在只保留包身份常量 `ProviderId` 与 `Version`，`Create()` 方法已删除——因此不可能与 Native 的真实 provider 重复注册。

Off 模式下插件不注册也不打 Hook。Load 是单次尝试，失败后必须重启进程；原生 IL2CPP Hook 是进程级的，热重载没有已验证的恢复合同，`Unload()` 返回 false。

本包有自己的 cfg `BepInEx/config/NAinfini.ForgeEnemy.cfg`，D-007 的 `[Logging] Level` 取 `off`、`error`、`info`，默认 `error`，改动需重启；Load 读到非法值时抛错，不做静默回退。该级别随注册交给内核，成为 `forge.module.gtfo.enemy` 这个 provider 自己的级别（`RegisterModule(module, level)`），不放进 `RuntimeModule`。宿主 `Runtime.Mode = Off` 时插件提前返回，既不注册也不读这一项。

## 当前 binding（全部 implementation-only）

Native 插件持有 **5 个** binding 与 **5 个** Hook；Runtime 保留 4 个世界与会话 Hook。共享的 canonical 定义在 SDK 的 `CombatContracts` 里，共 5 个。

| binding | 已实现范围 | canonical / 权限 |
| --- | --- | --- |
| `damage_applied` | 观察指定原生承伤调用窗口的实际 HP 损失；未知攻击来源不反推 | `forge.trigger.combat.damage_applied` |
| `heal` | 存活敌人的多目标治疗：每个 target 独立量化、提交前复核、实际读回；`overheal_policy` 为 clamp/discard/overheal，overheal 在 SFloat16 量化下结构性不支持，整条命令上游拒绝 | `forge.action.combat.heal` |
| `health_changed` | 两个来源：本 Forge 治疗读回的正生命变化；同一 `ProcessReceivedDamage` 调用窗口里观察到的实际 HP 损失（`value` 为调用后夹到 ≥0 的生命，`delta` 为负损失）。窗口里生命不变或上升不发布；其他模组、原生回复、`SetHealth` 等窗口外的变化不覆盖 | `forge.trigger.combat.health_changed` / `gtfo.enemy.health.read` |
| `death_started` | 同生命的 `OnDead` 正常返回且原生状态为 dead | `forge.trigger.enemy.death_started` / `gtfo.enemy.lifecycle.read` |
| `limb_broken` | 同生命、同 receiver 的索引部位在 `DestroyLimb` 窗口中由未破坏变为已破坏 | `forge.trigger.combat.limb_broken` / `gtfo.enemy.limbs.read`；输出 target 与 limb（可空） |

治疗使用已核验的 GTFO Steam build `20403457` 的 `SendSetHealth`，预检原生 `SFloat16` 精度并同步读回报告实际变化。小于精度的正治疗不能倒扣血；满血或零有效量不发送变化事实。未知攻击者不推断；玩家治疗与普通伤害命令尚未实现。

**死亡事实不是击杀。** `death_started` 不是 `forge.trigger.combat.killed`：从 `OnDead` 推不出攻击者、致命一击、奖励、伤害原因或尸体清理成功。同一生命只认领一次死亡窗口；重复与嵌套回调、未知完成、缺少消费者或队列拒绝都不会重开旧事件。

**肢体事实要求观察到真实转换。** 由 `Dam_EnemyDamageLimb.DestroyLimb` 的 prefix/postfix 捕获 false→true，同时核对 world/life、敌人指针与 ID、receiver、owner、`m_limbID` 与 `DamageLimbs[id]`。最多接受 256 个索引部位；超限、缺部位、换组件、读回不稳定或权限与阶段丢失时不伪造事实。同一生命内被其他模组恢复的部位不在当前的再破坏支持范围，新生命单独开始。原生调用抛错而没有正常 postfix 时不发布完成事实，也不自动补发。

root 与 cause 由 Runtime 原有的 Publish 调用链维护，native 外部入口不推断 source、owner 或 instigator。排队后如果原 target 失效，Runtime 在动作执行前拒绝旧引用，不改投新生命。

## 原生实体观察

Native 观察返回精确引用、位置、生命状态，以及有效的 `health.heal` 接收能力。**阵营保持 unknown，标签为空**——未知字段不从种类、名称或当前 AI 目标填补。坐标是当前 `EnemyAgent.Position` 值：这不是碰撞净空、出生适配性、历史成员资格、LOS 证据或作者到世界的变换。查询完整性只覆盖显式引用，从不代表枚举了全部敌人或某个空间区域内的全部对象。观察受 provider 现有的玩法门槛限制。

## 出生空间要求合同（E2，离线数据等级）

Enemy 只声明需要什么空间，**合法空间求解归 Map**。合同文件是 [`evidence/e2-spawn-space-20403457/spawn-requirements.json`](evidence/e2-spawn-space-20403457/spawn-requirements.json)（`format = gtfo-forge-enemy-spawn-requirements`，`version = 1`），由 `Spawn/EnemySpawnRequirements.cs` 从同目录的离线证据确定性生成，并校验与证据一致。JSON 里没有原生 `EnemyAgent`、指针或运行时对象；Map 不引用 Enemy 私有程序集，按 JSON 合同读取。

证据由 `tests/SpawnSpaceEvidence/extract_spawn_space.py` 只读提取：Steam build、UnityPy 版本、四个游戏数据文件与三个 DataBlock TextAsset 都有哈希锁，任一不符即拒绝。**Position 快照、模型包围盒、骨骼预览都没有被用作碰撞或导航证据。**

| 字段 | 来源（全部 data 等级，除非另注） |
| --- | --- |
| `navMeshAgentTypes` / `navMeshAreas` | `globalgamemanagers` 的 NavMeshProjectSettings |
| `enemyDataBlockId` / `name` | `EnemyDataBlock` |
| `movement` | `EnemyMovementDataBlock.LocomotionPathMove`（`ES_StateEnum` 的 PathMove=2 / PathMoveFlyer=28 由 metadata 核对）与基础 prefab 上 NavMeshAgent / 空中图代理组件是否存在，两者一致才给 `ground` 或 `flying`，否则 `unresolved` 并写原因 |
| `groundNavigation` | 基础 prefab（`sharedassets43.assets`，Enemies_S1 分片场景）上 NavMeshAgent 的序列化字段：agentTypeID、radius、height、walkableMask、autoTraverseOffMeshLink |
| `ladderDescent` | `EnemyMovementDataBlock.AllowClimbDownLadders` |
| `collisionRadius` / `canBePushed` | `EnemyBalancingDataBlock`；数值为数据，**语义未知** |
| `modelSizeRanges` | `EnemyDataBlock.ModelDatas[].SizeRange`；**对导航或碰撞的影响未知** |
| `arenaDimensions` | `EnemyDataBlock.ArenaDimensions` |
| `unverified` | 每条都至少含 `spawn-clearance`、`base-prefab-resolution`、`datablock-overrides`、`size-multiplier-effect`，按字段再加对应代码 |

39 个启用的敌人块：地面 32、飞行 4（42、43、45、58）、未决 3——22 没有移动块，44 与 61 用 PathMove 移动块却挂在空中图基础 prefab 上，保持未决不猜。所有地面敌人的 prefab NavMeshAgent 半径都是 0.2（包括尺寸 6.0 的 MegaMother），因此合同**不输出单一净空尺寸**，`spawn-clearance` 永远是未核验；Map 在净空受限的候选空间里不能把它当作已适配。自定义 rundown 的 DataBlock 覆盖会使这些离线值失效。

`Spawn/EnemyContentDependencies.cs` 按计划里实际的 binding 计算内容依赖：只有 provider 为 `forge.module.gtfo.enemy` 的 binding 才要求 Enemy 包，只引用模型或资源的外观内容不带入 Enemy 能力；计划的 `domain` 标签不算依赖。

## 接收器与插件的安全边界

这些是已经复现并修好的具体缺陷，改动时不要退回去：

重复出生观察是幂等的——相同 native 实例保留原生命期，真实销毁后重生才分配新 life，**不按 managed wrapper 身份猜新生命**。销毁令牌捕获生命期：`CaptureDespawn` / `CompleteDespawn` 校验模块所有者、world/life 和 native 指针，旧令牌不能删同指针的新生命。伤害观察只消费一次：同一 token 的重复回调、零损失和被拒绝的回调都不重放，同 tick 两次真实独立伤害不会被合并。提交结果保持准确：发包后异常、receiver 或 owner 或上限或权限变化返回 failed 或 unknown，不伪造 actualAmount 也不重试；量化异常返回 failed/none 且未发包，不匹配的 receiver owner 在提交前拒绝。

插件会话的回调与释放先核对模拟线程再执行。正常卸载先请求 Runtime 注销；调度重入或被依赖时不能提前卸 Hook，也不破坏后续合法清理。失败启动先锁上玩法门槛再回滚，即使注销受阻，残留的 receiver 也是惰性的。Load 之后的失败在 finally 里清空静态 Session；只读或抛异常的 `Exception.Data` 不能让 Session 保持发布状态，也不能掩盖主要失败。异常 Message 抛错、为 null 或过长时仍有有界诊断，不形成反复执行或记录的循环。`LastCleanupDiagnostic` 只保存最近一条有界诊断（最多 4096 字符），**不是游戏状态注册表、重试队列或计时器**。

## 复跑

从仓库根运行，先把 `GTFO_BEPINEX_PATH` 设为只读的 `Forge-MapEditor-QA` profile 的 BepInEx 目录（`%APPDATA%\r2modmanPlus-local\GTFO\profiles\Forge-MapEditor-QA\BepInEx`）：它与构建引用、`evidence/native-api-20403457.json` 冻结的三个 interop 程序集是同一份副本。输出必须用隔离路径，不写入任何 profile：

```powershell
$out = Join-Path $env:TEMP ('forge-enemy-qa-' + [guid]::NewGuid().ToString('N'))
dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release --artifacts-path $out
$hostDll = Join-Path $out 'bin/ForgeRuntime/release/ForgeRuntime.dll'
$sdkDll = Join-Path $out 'bin/ForgeRuntime.Framework/release/ForgeRuntime.Framework.dll'
dotnet build ForgeEnemy/Native/ForgeEnemy.Native.csproj -c Release --artifacts-path $out `
  "-p:ForgeRuntimeAssembly=$hostDll" "-p:ForgeFrameworkAssembly=$sdkDll"
```

各测试套件的命令与边界见 [VALIDATION.md](VALIDATION.md) 和各测试目录的 README：[提交路径审计](tests/CommitAudit/README.md)、[实体观察](tests/EntityObservation/README.md)、[生命周期事实](tests/LifecycleFacts/README.md)。

## 边界

以上全部是 **implementation-only**：源码存在、托管测试通过、原生签名与元数据已静态核对，**没有一条完成 GTFO 实机验收**。原生读取、替身、编译和元数据是不同的证据层，任何一层通过都不代表游戏或多人已通过。E1 的下一个门槛是真实游戏加载与原生 Hook 与主客机验证，不是再次创建或迁移同名 provider。

E2 的出生空间要求只到离线数据与替身测试等级：运行时 NavMeshAgent、尺寸倍率效果、碰撞半径语义、空中图净空和出生阶段顺序都待游戏内核验（步骤见 [VALIDATION.md](VALIDATION.md#待游戏内核验)），Map 侧的真实求解器也未接入。E3 剩余的 `limb_damaged`、`staggered`、`killed`、`hit_candidate`、`damage_preparing`、`damage_rejected`、`assist_confirmed`、`status.*` 与 Boss 阶段，以及 E4–E7 都未完成：它们要么需要 SDK 新增 canonical 合同或事件端口类型，要么没有冻结的原生 Hook 与语义证据（逐条原因见 [VALIDATION.md](VALIDATION.md#heal-联调解除与-e3-伤害与状态事件2026-09-14)）。开发与发布包尚未生成；Native DLL 不能据当前构建宣称玩家发行资格。
