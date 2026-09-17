# ForgeWeapon

> 实施顺序与原版内容完整覆盖见[唯一开发计划](../../Infini-GTFO-Model-Site/Docs/forge-contract/FORGE-FRAMEWORK.md) §4，分步验收见 §7；本文件仅说明实现与用法。

武器、工具、消耗品共用一个装备领域与 Workshop：装备实例、输入与攻击、弹药与能源与库存、部署与回收、装备表现。时间、状态、目标与事务的基础由 Runtime 提供，**不为每种工具或药剂复制一套框架**。

交付要求见唯一开发计划 §6 U-WEAPON-MOD（链接见[仓库 README](../README.md)），带日期的验证记录见 [VALIDATION.md](VALIDATION.md)。

## 工坊里的"标准武器"本身就是一张图

内置预设与玩家自制的行为共享同一条编译、校验、执行路径。治疗炮台、命中标记、ping 不是三个系统，是同一批原子的三种拼法——治疗炮台 = 部署动作 + 范围查询 + 关系过滤 + 周期 Control + 治疗 Action，外观在工坊里配。**任何为内置预设开的特权通道都是设计缺陷。**

这条对本包的含义是：Weapon 提供的是真实的装备实例、成本、攻击与部署这些**领域能力**，不是成品功能开关。玩家怎么把它们拼起来由 Trigger 的图决定。

## 运行清单与 W1 装备身份

`ModuleDefinition.Create()` 注册 provider `forge.module.gtfo.weapon` 与七个观察型 trigger capability：装备切入/切出（`forge.trigger.input.equipped`、`forge.trigger.input.unequipped`）、开火与命中候选（`forge.trigger.combat.shot_committed`、`forge.trigger.combat.hit_candidate`）、实体消失（`forge.trigger.entity.despawned`）、部署与回收完成（`forge.trigger.equipment.deploy_completed`、`forge.trigger.equipment.recall_completed`）。每个 capability 有一个 `role=observe`、`status=implemented` 的 binding，都没有 handler。bindingSupport 均为 **`implementation-only`**，所需权限 `gtfo.equipment.wield.read`、`gtfo.weapon.combat.read`、`gtfo.equipment.deployable.read`。

只注册 runtimeBinding，没有 previewBinding，也没有动作 binding。**观察即事实**：开火、命中候选、部署、回收与消失都只发布事实，不施加伤害、不消耗弹药、不写背包、不改部署状态；原生观察由独立的 BepInEx 插件 `NAinfini.ForgeWeapon` 加载（implementation-only，见下文）。

`deploy_completed` 声明 `actor`、`deployed` 与 **`optional` 的 `position`（`vector3`，单位 `m`）**：**`deployment:handle` 端口没有注册**，因为本仓库的 Runtime 还没有 handle 端口契约（rtabi 的 FrameHandle 尚未落地）。位置取自**这次 spawn 出来的那个世界实例自己的 transform**，不是放置请求里的坐标：全镜像只有 `SNet_ReplicationManager_Gear::GetInstanceReplicator` 会给部署实例写位置，写的就是 `pGearSpawnData` 带的那一份，部署物自己的代码从不移动自己（见 `evidence/w4-deployable-hooks.json` 的 `positionAtSpawn`）。它是 `optional` 而不是必有或者 `nullable`：复制器工厂与同一个复制操作的 spawn 回调之间的顺序是结构性的、没有可执行证据，读不到有限世界坐标时这个端口**整条不出现在事实里**，而不是发一个 null 或用请求坐标冒充完成位置。`recall_completed` 只声明 `actor` 与 `deployed`：**返还数量端口没有注册**，它属于本 provider 刻意不复制的那份背包与弹药状态，声明一个永远发不出的端口等于发不出的承诺。`hit_candidate.target` 在注册图里是 **`optional`（不是 `nullable`）**：命中候选在任何伤害结算之前产生，命中对象的原生类型决定它属于哪个域，解析不出来时这个端口**整条不出现在事实里**，而不是发一个 null 引用冒充真实引用。`hit_candidate.equipment` 则是**必有**端口：命中候选只在开着的开火窗口内产生，所以它一定说得出开这一发的那条装备生命，取值就是同一发 `shot_committed.equipment` 的同一个引用——取自窗口本身，不从命中对象反推，挂在那个装备块上的行为正是靠它认领自己武器的命中。

SDK 目前没有输入类 trigger 的合同模块，这两个 capability 暂由 Weapon 自己声明为 owner。若以后 SDK 提供 canonical 合同，要改为绑定到该合同，不能两处同时声明。

已经在生产程序集内的是 W1 的装备身份托管实现，只有显式创建 Session 才生效：

`EquipmentObservation.cs` 把 `EntityReference`、资源 ID 与 revision、显式 owner、slot、Inventory/World/Deployed 位置、加载与持有状态分开，没有另造跨域的 EntityReference。`EquipmentIdentityId.cs` 是本 provider 唯一的 id 形状定义：命名空间固定 `gtfo.equipment`，**背包装备生命**是 `gtfo.equipment:<world>.<life>`（两段数字），**部署出去的世界实例**是 `gtfo.equipment:<world>.<life>.<instance>`（三段数字，接在放置它的那条生命之后）。两种形状互不重叠，`EquipmentIdentityIndex.cs` 只接受生命形状，世界实例由原生接线自己的放置表回答。`EquipmentIdentityIndex.cs` 维护有界的活动实例与槽位索引及已观察的生命历史；同槽占用冲突原子拒绝，资源版本不能在同一生命内偷换，移除必须匹配完整引用，旧的 despawn 不会删除复用键上的新生命。`EquipmentIdentitySession.cs` 显式注册同一个 `forge.module.gtfo.weapon` provider 的 `gtfo.equipment` resolver：**一个命名空间、一个 resolver、一个 observer**，收到引用后按 id 形状分派到对应的表（生命查索引与世界背包，世界实例查放置表），因此一次查询不可能把装备生命读成世界实例或反过来；世界切换、失败、停止和失去主机权限之后的记录由 `ObserveLifecycle` 清理。

`EquipmentUseTicket` 是**仅进程内**的前置条件快照：任何已观察到的 owner、slot、location、readiness 或 wield 变化都让旧票据失效，A→B→A 和卸下再装备都不复活旧票据。**它不是权限、不是预留、不是成本收据、不是网络身份、不是检查点数据**，不能序列化成共享的所有权合同。

`Record` 只在同一生命、同一 owner、同一槽位且都在 Inventory 时，直接观察到 `IsWielded` 翻转，才发布 equipped 或 unequipped 事实。初始快照、移动、转交和新生命都不发布。

当前必须在 Runtime 的注册窗口内显式创建 Session 并传入本包 cfg 的 `Logging.Level`（随 `RegisterModule` 交给内核）与两个真正核验当前原生实例和玩家生命的探测函数。原生接线里这两个函数分别是 `EquipmentNativeAdapter.IsNativeCurrent`（严格读回）与 SDK 的 `RuntimeKernel.IsEntityCurrent`（owner 是 ForgeMap 登记的 `gtfo.player` 引用，由其拥有者核验）。写入与解析要求 Runtime Ready 且已完成首个 host tick，未初始化、客户端、未知权限与注销状态都不接收记录。示例测试里的永真探测**只能用于合成输入，绝不能作为游戏默认实现**。

索引不生成世界、生命或资源身份，也不从 slot、模型、资源名、owner 或裸指针推断另一个身份。新实例必须由原生接线提供经核验的新引用；同一 ID 必须在精确退役之后以更大的 lifeEpoch 再出现。历史到预算上限时明确拒绝，**不淘汰旧记录后放行重放**。探测期间重入 Record / Remove / Dispose 会被拒绝；探测中发生世界切换、停止或注销会重新检查，迟到的观察不能写进新世界。

## 原生观察接线（implementation-only）

`Native/ForgeWeapon.Native.csproj` 是独立项目，引用真实 interop 程序集与宿主 `ForgeRuntime.dll` 编译，不进入 `ForgeWeapon.dll`，也不引用 ForgeMap 程序集。

组成：
- `Plugin`：BepInEx 插件 `NAinfini.ForgeWeapon` / `Infini Forge Weapon` / `1.0.0`（与 `ForgeWeapon.dll` 版本一致），依赖 `NAinfini.ForgeRuntime` 1.0.0 与 `NAinfini.ForgeMap` 1.0.0。宿主 Off 时不注册、不装 Hook；宿主不可用时抛出；Load 只允许一次，失败回滚并保留原始异常，`Unload()` 返回 false（不热卸载）。本包自己的 cfg 是 `BepInEx/config/NAinfini.ForgeWeapon.cfg`，`[Logging] Level` 取 `off`、`error`、`info`，默认 `error`，改动需重启，非法值在注册前抛错；该级别随注册交给内核，成为 `forge.module.gtfo.weapon` 这个 provider 自己的级别。发布身份沿用现有命名模式，**未经确认，没有清单或打包**。
- `WeaponNativeSession` 按固定顺序启动：注册窗口内先注册身份 Session（重复 provider 在装 Hook 前失败），再装 Hook；失败时回滚，释放时先注销再卸 Hook。任何意外异常都锁存故障，并清空句柄表。
- `WeaponNativeHooks` 有 23 个 Hook：21 个 `Priority.Last` 的 postfix-only Hook 只读回、不改参数或返回值，另加装备池的一对（一个 `Priority.Last` postfix、一个 `Priority.First` prefix，见下节）：
  - `PlayerBackpack.CreateAndStoreBackpackItem` / `TryClearSlot` / `DestroyAllInstance` / `SetDeployed`
  - `PlayerInventoryLocal.DoWieldItem` / `UnWield`
  - `PlayerInventorySynced.DoEquipItem` / `UnWield`
  - `BulletWeapon.Fire` / `Shotgun.Fire` / `BulletWeaponSynced.Fire` / `ShotgunSynced.Fire` / `BulletWeapon.BulletHit`：Slot 151 的四个 `Fire` 体各挂一个 postfix，每个 `Fire` 体记一发并开火，命中候选认领最近一次开火。四个体互不调用（见 `evidence/w4-player-fire.json`），所以"一个 `Fire` 体一次调用＝恰好一条 `shot_committed`"，既不需要去重也不会漏记：玩家自己的武器走未同步的两个体（`BulletWeapon.Fire` / `Shotgun.Fire`，它们把这一发登记进 `PlayerSync`），本机持有的"别人的武器副本"走同步的两个体（由 `PlayerInventorySynced.GetSync` 把复制过来的射击数喂给 `OnSyncFire`，再逐发调用 `Fire`），`RifleWeapon` 与 `RifleWeaponSynced` 不重写 `Fire` 因而用各自家族的那个体。没有开火窗口的命中（哨戒自身的 `SentryGunInstance_Firing_Bullets.FireBullet` / `UpdateFireShotgunSemi` 直接调同一个静态命中函数）丢弃并写 `weapon.hit-outside-shot`。
  - `SentryGunInstance` / `MineDeployerInstance` 的 `OnSpawn`、`SyncedPickup`、`OnDespawn`（矿没有）、`OnDestroy`：部署物世界实例的生命。
  - `Gear.GearPartHolder.OnAllPartsSpawned`：唯一一个**写**原生状态的 Hook，且只写表现层（见下节）。它不读回、不发事实。
- `GearPartTransformData` / `GearPartTransformApplier`：按裁决的部件姿态定制。包把每个装备块一个文件写在 `BepInEx/plugins/<包的插件目录>/forge/gear-parts/<blockId>.json`——与 `forge/plans/` 同一个包目录规则（`plugins/` 下**一层**目录，不递归），文件名的 blockId 与文件内容里的 `blockId` 必须都是规范十进制且逐字相等。插件启动时一次性读成不可变快照（不监听、不热重载、没有第二个来源）；坏文件按**文件**粒度拒绝（其它文件照常加载），每份文件写一次 error 行 `weapon.gear-part-<code> file=<相对路径>: <原因>`，`<code>` 取 `json`、`id`、`parts`、`component`、`number`、`path`、`limit`、`size`、`unreadable`、`duplicate-block`。上限：单文件 256 KiB；每块部件项 ≤ 64；`localPosition` 每轴绝对值 ≤ 1 m；`localScale` 每轴 ∈ [0.05, 4]；欧拉角规范到 [0, 360)；子物体路径按**从部件起算的完整路径**计段（嵌套的父段计入）≤ 8 段，且每段非空、不是 `.` 或 `..`、没有前导或尾随 `/`。**一个块只有一份文件**：两份或更多文件声称同一个块时该块**整体不加载**（`duplicate-block`），不保留第一份、不合并、不按扫描顺序决胜，每个声称的文件各写一次。
  - **键**：`component` 是 `eGearComponent` 成员名（只有 `GearPartHolder` 真的持有 `GameObject` 的那 21 个部件槽位，枚举数值与 `Modules-ASM` 逐成员核对）；可选 `partId` 是作者期望该槽位装的部件块 id，它**不是键而是校验**——`GearIDRange.GetCompID` 读回不同（或读不到）就不应用该条，每槽位写一次 `weapon.gear-part-mismatch: block=… component=… expected=… actual=…`。
  - **字段**：`enabled`、`localPosition`、`localEulerAngles`、`localScale`、`children[{path, 同样字段}]`；子节点自己还可以再带 `children`，**整棵树都会应用**，每个节点用它从部件起算的完整路径解析。所有值是**绝对值**（本地空间），所以同一个 holder 再出生一次、图标再渲染一次、检查点重载都写下同一组数，不会累积；**文件没写的字段一个都不写**，游戏自己的姿态原样保留。
  - **写集恰好四个成员**：`Transform.localPosition`、`Transform.localEulerAngles`、`Transform.localScale` 与 `GameObject.SetActive`（本 build 的 interop 没有 `active` 属性，`SetActive` 是唯一拼法）。不发事实、不走网络、不碰游戏状态，因此多人一致靠"同一份包＝同一份数据"。
  - **挂点**：块 id 从 holder 的 `GearIDRange.PlayfabItemInstanceId` 取，走的是与 `gear-block` 挂载**同一个** `EquipmentNativeAdapter.GearBlockId`，所以块 id 在全包只有一种拼法。子物体按作者写的相对路径用游戏自己的 `Transform.Find` 查找，**嵌套的 `children` 整棵树都会应用**；找不到写一次 `weapon.gear-part-child-missing: block=… component=… path=…`（缺失节点自己的子树随之跳过：它下面不可能解析得到），并继续处理其它条目。应用成功每个 holder 写一条 info：`weapon.gear-part-applied block=<id> parts=<n>`；启动时 `weapon.gear-part-blocks-loaded count=<n>` 写一次。诊断预算：不匹配与子物体缺失每条目/每路径一次，应用行每个 holder 一次。
  - **作用范围**：所有本机可见实例各组装自己的 holder（第一人称、他人第三人称副本、菜单预览、图标渲染、部署哨戒），各自都会命中这个 Hook，因此都会被同一个文件摆成同一个姿态。
  - **宿主 Off**：`Plugin.Load` 在任何注册与原生动作之前就返回，所以宿主 Off 时本包**既不读这些文件也不装这个 Hook**，任何一个部件都不会被摆位——与其它原生 Hook 完全一样的规则，没有本包自己的开关。证据链见 `evidence/w6-gear-parts.json`。
- `GearLoadoutFilter` / `GearLoadoutSession` / `LoadoutPolicyData` / `LoadoutPolicyWiring`：**装备池策略**（implementation-only），让作者用一份策略文件决定某个 rundown 里可选与可装备的装备。它不是观察：这是本包唯一改游戏状态的地方，改的也只是游戏自己那份装备池的内容。
  - **发现与激活**：`BepInEx/plugins/<恰好一层目录>/forge/loadout.json`，与 `forge/plans/`、`forge/gear-parts/` 同一目录规则（不递归、按相对路径序数排序、单文件 ≤ 1 MiB），插件 Load 期**读一次**，改文件需重启。用哪一份由 `Globals.Global.RundownIdToLoad` 决定：一个 rundown 一份策略，同一个 rundown 被两份文件声称时**两份都不生效**（`loadout-policy-conflict`），不按扫描顺序决胜；不匹配当前 rundown 的策略文件不是错误，只是不生效。
  - **内容 pin**：`sources[]` 逐条按 `FrameworkFiles` 同规则解析到 BepInEx 相对路径并比 sha256，`gameAssemblySha256` 比 `Paths.GameRootPath` 下的 `GameAssembly.dll`，`plugins[]` 比 `IL2CPPChainloader.Instance.Plugins[guid].Location` 那个 DLL。任何一条不符，**该文件整份拒收**（`weapon.loadout-policy-<code> file=<相对路径>: <原因>`），其它文件不受影响；pin 只能报告安装不一致，不能拒绝别的客户端。
  - **身份**：一件被提供的装备属于策略里的哪一条，只看两个正向事实——`GearIDRange.PlayfabItemInstanceId` 的 `OfflineGear_ID_<规范十进制>` 文本（与 `gear-block` 挂载同一个拼法，`010001`/`+10001`/`10001.0` 一律不匹配），或 `GearIDRange.GetCompID(eGearComponent.Category)` 等于该 id（网站给自制装备 `setComponent(2, state.blockId)`，分量随副本走）。不解析、不规范化、不猜。
  - **两个 Hook**：`GearManager.GetAllGearForSlot` **postfix（`Priority.Last`）** 替换 UI 拿去建列表的那个数组，覆盖大厅选择器与局内背包流的唯一数据源；`GearManager.OnGearLoadingDone` **prefix（`Priority.First`）** 在 `RescanFavorites` 之前把三个槽的 `m_gearPerSlot` 列表**原地**收窄——存档里的 `LastEquipped_*` 原名随之失配，游戏自己回落到同一池的第一件（＝策略的第一件），`TryGetGearFromCategory` 读同一个池，所以大厅预设装备那条路一并失效。
  - **整槽回退**：只有"策略里每一件都真的在游戏自己那份列表里被认出来"才发布投影；少一件、游戏还没建这个槽、或读身份时抛异常，都**整池回退**为游戏自己的内容并写一条 `weapon.loadout-policy-unmatched slot=<slot> missing=<id,id>`；任何异常再额外把整个策略锁存失效到重启（`weapon.loadout-policy-failed: <Type>: <message>`），绝不发布半份列表。
  - **恢复**：收窄前三个槽的原始内容各留一份快照，当前 rundown 不再有策略（例如进程换了 rundown）或会话释放时，按原顺序写回**同一个列表实例**；列表实例与 `m_gearPerSlot` 数组本身都不替换，所以其它持有者看到的是同一份内容。
  - **不写盘**：一次都不调用 `SaveFavoritesData` / `RegisterGearInSlotAsEquipped`，不碰数据块；`GTFO_Favorites.txt` 里原版键留着但永远失配，游戏自己的正常操作照旧写它自己的值。
  - **门**：只在 `Player.InventorySlot` 1/2/3 上生效（近战、黑客工具、消耗品一律原样返回）；投影只在**非局内**（`GameStateManager.IsInExpedition` 为假）时做——局内不能换装，过滤只会让正带着的装备在 HUD 里消失；池是在进关之前建好的，所以加载 Hook 不受这个门限制。
  - **诊断（info）**：`weapon.loadout-policies-loaded count=<n>`、`weapon.loadout-policy-active rundown=<id> policy=<相对路径> slots=<slots>`、`weapon.loadout-policy-inactive reason=rundown-mismatch rundown=<id>`、每槽一次 `weapon.loadout-slot slot=<slot> offered=<n> kept=<n> duplicates=<n> dropped=<n> ids=<…>`、`dropped>0` 且该槽是标准/特种时再写一条 `weapon.loadout-vanilla-dropped count=<n>`。重复项是游戏本来就有的行为，只计数不去重。
  - **多人**：装备池是每客户端本地状态，选择在本地背包做出后随同步副本复制，**没有任何主机校验入口**，所以每个客户端各自过滤，本包不伪造主机校验。
  - 证据链见 `evidence/w7-loadout-policy.json`；本节的规则由 `tests/NativeAdapter/LoadoutRuntimeTests.cs` 逐条覆盖（策略文件用 `%TEMP%` 下的 fixture 安装读取，不碰 Release 发布物）。
- `WeaponCombatObserver` 把开火与命中分成两个 binding：`index` 是该装备生命内的第几发，命中候选的 scope 是 `gtfo.weapon.shot:<equipment id>:<index>`。**同一发只发布一次开火事实**，霰弹枪一个 `Fire` 体打出的多颗弹丸由同一发认领。命中候选的 `equipment` 直接取那条仍开着的窗口，因此与同一发开火事实的 `equipment` 逐字相同；换枪后再开火开的是新生命的窗口，命中随之带上新生命。
- `EquipmentNativeAdapter` 只在 `SNet.IsMaster`、Runtime Ready 且 host 时对账。
  - 背包每个有实例的槽位都读回成 `EquipmentObservation`。
  - 装备生命 ID `gtfo.equipment:<world>.<life>` 由接线按世界递增生成，槽位、指针或资源变化即退役旧生命。
  - 炮台与屏障放置后槽位仍然持有同一件装备，接线用 `PlayerBackpack.IsDeployed(slot)` 读回该槽位的部署标记：标记为真时同一个生命以 `Location=Deployed`（无槽位、未持有）记录，收回后回到 `Inventory`。**部署状态变化不发布 equipped/unequipped 事实**，世界里的部署实例有自己的身份（见下）。
  - 部署物的世界实例用同一个命名空间的第三种形状 `gtfo.equipment:<world>.<life>.<instance>`（接在放置它的那条生命之后，出生序从 1 递增，绝不复用已回收实例的 id）：一个装备生命可以放置并回收多个实例，每个实例一条身份，`SyncedPickup`、`OnDespawn` 与 `OnDestroy` 都走同一条结束路径，第一条到达的路径结束它，其余路径不会重复发布；世界或权威切换时在飞的部署物**不发事实**直接丢弃并写 `weapon.deployments-closed`（把旧世界的结束盖到新世界上会是假事实）。
  - 命中对象 `hit_candidate.target` 由命中物体自己的原生类型决定：从 `rayHit.collider` 起 `GetComponentInParent<Dam_EnemyDamageLimb>()` / `<Dam_PlayerDamageLimb>()` 取 `GetBaseAgent()`，再按 agent 的真实类型选 kind——敌人交给 `gtfo.enemy`（ForgeEnemy 的实例表键就是 agent 本身），玩家交给 `gtfo.player`（ForgeMap 的键是 agent 所属的 `SNet_Player`）。两条腿都不是命中对象的 kind 时，把命中对象本身交给 `gtfo.map_object` 的实例解析器（`ResolveEntityInstance("gtfo.map_object", collider)`，见下）。世界几何、部署物、没有登记实例表的 kind、或没有伤害肢也没有地图对象祖先的碰撞体都解析不出引用，此时 **target 端口整条不出现**，事实本身照常发布（命中候选本来就可能打在墙上）。
  - 地图对象腿只问 kind 的名字，不认门或终端的原生类型：子弹打到的碰撞体属于门的门刀、门框、按钮或终端屏幕，**能从碰撞体爬到自己所属地图对象的只有拥有该域的 ForgeMap**，所以 Weapon 把 `rayHit.collider` 原样交出去，由该 kind 的实例解析器自己爬父级、自己判可寻址性。Weapon 因此不引用 ForgeMap 程序集、不新增任何游戏成员读取（`native.exact-game-reads` 逐字不变），门/终端的知识与地址语法全部留在拥有者一侧。该 kind 没登记（ForgeMap 未加载）或爬不到地图对象时同样返回 null，端口不出现。
  - owner 只经 SDK 的 `ResolveEntityInstance("gtfo.player", backpack.Owner)` 取得；返回 null 就不记录并警告一次，不从指针、名字、槽位或 `Lookup` 推断玩家。
  - 装备生命还是本 provider 唯一的挂载目标：它注册 `gear-block`（官方装备块 id，该块的全部实例）matcher，判断时按当前背包装备重新读 `BackpackItem.GearIDRange.PlayfabItemInstanceId`——游戏自己的 `GearManager.LoadOfflineGearDatas` 把 `PlayerOfflineGearDataBlock.persistentID` 写成 `OfflineGear_ID_<十进制>` 存在这里，所以这就是网站 `state.blockId`（网站用 `String(blockId)` 写出）的同一个数，不是 checksum。匹配是**序数文本比对**：去掉 `OfflineGear_ID_` 前缀后的文本与 reference 逐字相等才算匹配，脚本不做数值解析、不做规范化，所以 `010001`、`+10001`、` 10001`、`10001.0` 这些第二种拼法都不匹配块 `10001`。reference 不是规范十进制文本，或读不到该记录（非离线块装备、包体重建的 gear、记录后缀不是规范十进制）、生命已退役、跨世界、非 `gtfo.equipment` 生命形状的 subject，一律 false；前一种每份不同文本写一次 `weapon.gear-block-reference-invalid`。挂载按**生命**生效，部署出去的世界实例不匹配。证据链见 `evidence/w5-gear-block.json`。

证据文件 `evidence/w1-native-hooks.json` 锁定的内容：
- 每个 Hook 的签名、是否 virtual、dump RVA（各自唯一且不共享）。
- 32 条到达或清理路径的直接调用边，其中 15 条是 2026-09-14 为部署与回收路径补的（`SentryGunInstance.OnSpawn` 先取 owner 背包再调用部署标记、`SentryGunInstance.SyncedPickup`、`BarrierFirstPerson.PlaceOnGround`、bot 放置、以及矿与投掷物的清槽路径）。
- 3 条离线表现调用边：DoWieldItem 调 `FirstPersonItemHolder.SetWieldedItem` 与 `PlayAnimationsForWieldedItem`，本地 UnWield 调 `FirstPersonItemHolder.UnWield`。**这 3 条不证明模型挂载、rig 或动画的实际结果。**

背包之外的装备路径调研（关卡拾取、世界掉落、部署物、转移）见 `evidence/w1-native-world-paths.json`：记录所用 interop 目录与各程序集 sha256、每个类型的签名与 RVA 唯一性、直接调用方、同步/本地归属，以及为什么只有部署标记被接线。该文件同时记录 `LG_PickupItem`、`ItemInLevel`、`SentryGunInstance`、`MineDeployerInstance` 等真实类型与它们缺少资源定义或 owner 的原因；`PickupItem`、`DeployerInstance` 这类不存在的名字没有被使用。

`gear-block` 挂载键的来源见 `evidence/w5-gear-block.json`：记录 `GearManager.LoadOfflineGearDatas`（0x1330250）如何把 `PlayerOfflineGearDataBlock.persistentID` 拼成 `OfflineGear_ID_<十进制>`、`ParseAndStashGear`（0x1330A70）把它写到 `GearIDRange` 的哪个偏移、以及这条记录如何随 `EquipLocalGear`/`EquipSyncGear` 进入 `Player.BackpackItem`；同时记录被拒绝的三个候选（同步包体 `pGearIDRange` 只有 Comps/Mod/MatTrans/publicName、`GearCategory` 组件是另一个块的 id、checksum 不是块 id）。

玩家 owner 的来源：玩家引用 `gtfo.player:<n>` 的编号与 lifeEpoch 由 ForgeMap 的 MAP5a 私有分配（见 [ForgeMap README](../ForgeMap/README.md#map5a-玩家实体身份implementation-only)）；唯一稳定的原生玩家键 `SNet_Player.Lookup` 是 Steam64 账号 ID，不能成为公开键。所以 Weapon 不猜、不自造、不读 `Lookup`，只调用 SDK 的 `ResolveEntityInstance("gtfo.player", player)`，由 Map 按 SNet_Player 指针在已登记表里查找并复核；接口规则见 [Framework README](../ForgeRuntime/Framework/README.md#从原生实例取得引用)。敌人引用同理只问 `ResolveEntityInstance("gtfo.enemy", agent)`，由 ForgeEnemy 按 agent 自己的登记表回答。**kind 没有登记实例解析器时（例如 Map 或 Enemy 未加载）该调用返回 null**，接线把它当成"这个 kind 答不出来"：不记录、不发布、每个背包写一次 `weapon.owner-unresolved`，会话继续观察（非法的 kind 名仍然抛 `entity-resolver`）。

Slot 151 的四个 `Fire` 体、它们各自的持有者、`OnSyncFire` 到 `Fire` 的重放链、以及哨戒自己的命中调用方，见 `evidence/w4-player-fire.json`：该文件按同一份 dump 与同一个原生镜像（sha256 与其余证据文件逐字相同）记录每个体的签名、Slot、RVA 唯一性、整个体范围内的直接调用（含"没有任何一个体调用另一个"这条否定证据）、il2cpp 虚调用点（`klass + 0x130 + slot*16`，用 Slot 150/151/128 三处已知目标校准），以及仍属运行期未证的五点（重放链是否真的跑起来、`FireCount` 是增量还是累计、bot 开火、本地武器是否可能也是同步类、`GetSync` 由谁调用）。**这仍是静态调用图证据，不是游戏验证。**

有两处行为是设计后果，不是缺陷：
- `InspectEntities` 期间内核禁止嵌套查询，装备的 owner 复核因此失败，装备在该次查询里显示为 `stale-entity`；Weapon 没有注册实体 observer，这不影响记录，也不锁存故障。
- 清表行在世界切换后的下一次 Hook 才写出，不在切换当刻。

信息日志（BepInEx 来源 `Infini Forge Weapon`，只含 Forge 引用、槽位与资源键）：
- `weapon.equipment-life-started id=gtfo.equipment:<world>.<life> world=<world> owner=gtfo.player:<m> ownerLife=<life> slot=<InventorySlot> resource=gtfo.gear:<checksum>|gtfo.item:<id> location=Inventory|Deployed`
- `weapon.equipment-location id=… location=Inventory|Deployed`：同一生命的部署状态变化时写一次（起始位置已在 life-started 行里）。
- `weapon.equipment-life-ended id=… reason=owner-unresolved|owner-changed|slot-changed|moved-or-replaced|observation-rejected`
- `weapon.wield-fact kind=equipped|unequipped id=… owner=… status=<dispatch status> code=<code>`：没有计划消费时是 `status=ignored code=no-consumer`。
- `weapon.shot-fact equipment=… index=<n> status=… code=…`：每个 `Fire` 体一行。
- `weapon.hit-fact equipment=… index=<n> target=<归一化引用>|unresolved status=… code=…`：每个命中候选一行；`target=unresolved` 表示命中对象的原生类型答不出引用，此时事实里没有 `target` 端口。
- `weapon.hit-outside-shot: …` / `weapon.hit-owner-unresolved: …`：命中候选没有开火窗口、或命中数据没有本会话能核验的射手时写，不发布事实。命中对象解析不出引用不是警告：端口省略，事实照发。
- `weapon.deployable-life-started id=gtfo.equipment:<world>.<life>.<instance> equipment=gtfo.equipment:<world>.<life> resource=…`、`weapon.deployable-life-ended id=… reason=recalled|despawned|destroyed|equipment-ended`。
- `weapon.despawn-fact status=… code=… id=…`（`OnDespawn` 与 `OnDestroy` 走同一条结束路径）；收回先发 `weapon.recall-fact …` 再发这一行；只有 `OnDestroy` 时是 `weapon.despawn(destroyed)-fact …`。
- `weapon.equipment-lives-cleared world=<旧世界> count=<n> reason=world-changed|identity-cleared` / `weapon.deployments-closed world=<旧世界> count=<n>`。
- `weapon.gear-part-blocks-loaded count=<n>`：启动快照里被接受的块数，只写一次。
- `weapon.gear-part-applied block=<blockId> parts=<n>`：每个 holder 出生后写一次；同一个 holder 再出生就再写一行。

警告：`weapon.owner-unresolved: …`（每个背包一次）、`weapon.observation-rejected: <code>`、`weapon.wield-fact-rejected: <code>`、`weapon.shot-owner-unresolved: …`、`weapon.shot-fact-rejected: <code>`、`weapon.hit-fact-rejected: <code>`、`weapon.deploy-owner-unresolved: …`（部署物在世界实例出现时还没有可核验的 owner：仍然跟踪、仍然结束，但不发部署事实）、`weapon.recall-fact-rejected: <code>` / `weapon.despawn(x)-fact-rejected: <code>`、`weapon.gear-block-reference-invalid: <reference>`（每个不是规范十进制文本的 gear-block reference 一次：该挂载永远不会匹配）、`weapon.gear-part-<code> file=<相对路径>: <原因>`（每份被拒绝的部件文件一次）、`weapon.gear-part-mismatch: …`、`weapon.gear-part-child-missing: …`、`Weapon equipment observation disabled until restart: …`。

已知未核验的点：
- `Slots` 下标是否等于 `InventorySlot` 值。
- `IsLoaded` 的实际语义。
- UnWield 后 `WieldedItem` 的状态。
- 部署后槽位是否仍持有原来的 `BackpackItem` 实例；若实例被销毁，现有 `Matches` 逻辑退役该生命，而不是报出错误的部署位置。
- `SentryGunInstance.OnDespawn` 与 `SyncedPickup` 不都回写部署标记，原生标记可能在部署物消失后仍为真；读回只镜像原生标记，不替游戏修正。
- `itemID_gearCRC` 在游戏里既可能是 itemID 也可能是 gearCRC；背包路径靠槽位的 `BackpackItem` 解析，世界路径没有对应解析，因此没有接线。
- Bot 等其他 inventory 子类。
- 两次 Hook 之间指针被复用且没有任何清理 Hook。
- 转交时新背包先于旧背包被对账。
- 事件时间取 Hook 时刻。
- 出生时背包存入 Hook 是否早于 Map 登记玩家（若早于，会先出现一次 `weapon.owner-unresolved`，下一次 Hook 才记录）。
- 实际加载顺序与 Off / 缺 Map 时的日志。
- 客户端开火在 host 上的重放：静态链已完整（客户端自己的武器把这一发登记进 `PlayerSync`，复制过去的射击数由 `PlayerInventorySynced.GetSync` 交给该玩家武器在本机的同步副本 `OnSyncFire`，副本的每帧 `Update`/`UpdateRegular` 再逐发调用 `Fire`，见 `evidence/w4-player-fire.json`），但**没有任何运行期证据**：这条链是否真的每发跑一次、`FireCount` 是增量还是累计、`GetSync` 由谁按什么频率调用，都要靠游戏内一枪一计数确认（见验证清单第 10、11 步）。接线按 host 权威实现（`Authoritative()` 要求 `SNet.IsMaster`），同一发仍只发布一次。
- `WeaponHitData.owner` 是否由 `Fire` 体填写：命中数据没有射手时写 `weapon.hit-owner-unresolved` 而不发事实。
- `SentryGunInstance.Owner` / `MineDeployerInstance.Owner` 在 `OnSpawn` 时刻是否已可用：不可用时该放置仍被跟踪、仍可结束，但不发 `deploy_completed`，并写 `weapon.deploy-owner-unresolved`。
- 关卡切换走 `OnDespawn` 还是 `OnDestroy`：两条都挂了 Hook，且结束路径只会成功一次；无论哪条先到，结果都是恰好一条结束事实。
- 部署物的 `transform.position` 是否是玩家关心的世界坐标；`GetGroundOffset` 与 `GroundOffset` 在部署后是否参与修正。静态侧已确证**没有任何部署物代码在 spawn 之后移动自己**，位置只在复制器实例化那一次写入（见 `evidence/w4-deployable-hooks.json` 的 `positionAtSpawn`），但复制器工厂与同一个复制操作的 spawn 回调之间的先后仍然只能靠游戏内一次部署确认：这就是 `deploy_completed.position` 按 `optional` 注册、读不到就不出这个端口的原因。
- 命中候选的 `limb`：需要把命中碰撞体解析成伤害肢的序号，而候选在任何伤害结算之前产生，本 provider 不做。`target` 已有三条腿：敌人、玩家与地图对象（门、终端），见上文与 [ForgeMap README](../ForgeMap/README.md)。
- 部件姿态在游戏内是否被原动画覆盖：写发生在 `OnAllPartsSpawned` 之后，但同一类型还有 `Update`（dump.cs 第 614843 行），本轮没有解它的方法体。检查点重载、图标渲染频率、与同类部件模组同装时的先后顺序同理，见验证清单第 13 步。

## 原生 API 证据

W1 已交付只读的元数据核验工具与精确的输入锁：Steam app `493520` / build `20403457` / revision `34873`，GameAssembly SHA-256 `C6A5C3CD8CA5FE2A8C1A71A3D107663E8CBF01404820DBBDA2EA10C4BFD7BF55`，global-metadata SHA-256 `F57AE2790F7AE0A7ACCAD42BD5F5A541EE74C85E96B4D4B64A346B465C23C882`，锁定 8 个文件、34 个类型、164 个方法、47 个属性、22 个字段。

初次名称检索得到 345 个候选类型，之后补读 8 个身份与库存与网络类型；**这不是可运行的能力计数**，最终只按人工选择的精确全名、程序集、基类和签名核验。

这批元数据锁的证据等级是 **metadata-only**，原生 Hook 的静态证据见上一节：读取 BepInEx interop 的元数据并锁定同机游戏与生成的元数据文件，**没有执行原生方法、没有核验完整的原生调用链、没有安装 Hook、没有启动 GTFO**。同机文件存在，不单独证明 interop 的生成来源与原生地址映射正确。工具本身不进入 ForgeWeapon 的生产 DLL。

## 边界

网站可以挂载多个部件或预览模型，不证明 GTFO 支持相同数量的活动组件。Enemy 与 Map 分别拥有敌人和玩家与世界的 receiver；Weapon 不能为了省接口直接写它们的私有字段。一个原生攻击或附加效果必须指定唯一的执行 binding——**观察到原生承伤不代表还要再施加一次同额伤害**。

`tests/fixtures/w1-runtime-acceptance.json` 里的 20 个完整接线规格已经写好但 **0 个执行**，`verification=not-executed`；它们不是新的 Runtime IR 也不是已实现的库存服务，不计为通过的玩法测试。

游戏内核验按 [VALIDATION.md](VALIDATION.md#游戏内核验清单未执行) 的清单在隔离 profile 执行，记录结果前不算验证。当前 DLL 不是玩家发行物。

## 复跑

从仓库根目录：

```powershell
python ForgeWeapon/tools/verify-w1.py --bepinex <existing-BepInEx> --game <existing-GTFO> --architecture
python ForgeWeapon/tools/verify-identity-acceptance.py
python ForgeWeapon/tools/verify-identity-dispatch.py
python ForgeWeapon/tools/test-identity-mutations.py
```

第一条是统一入口（`--architecture` 附带跨模块架构断言），后三条是并发任务编写的独立套件与变异检查。**`verify-w1.py` 不包含原生接线的套件。** 原生接线的套件是 xUnit 工程，用 `dotnet test` 运行，输出放到仓库外的隔离目录：

`$bep` 为只读的 `Forge-MapEditor-QA` profile 的 BepInEx 目录（`%APPDATA%\r2modmanPlus-local\GTFO\profiles\Forge-MapEditor-QA\BepInEx`），与证据文件冻结的 interop 是同一份副本；`$game` 为 GTFO 根目录，`$dump` 为同 build 的 dump.cs。原生插件需要同一 artifacts 里的宿主 `ForgeRuntime.dll`；NativeLayout 还读取 ForgeMap 原生插件，核对依赖的 id 与版本。带游戏程序集的 NativeEvidence 与 NativeLayout 用 `Category=Native` 过滤单独运行，其余工程默认排除它们。

**`NativeEvidence` 的运行前提**：它在 `$env:FORGE_WEAPON_BEPINEX` 根下直接读 `GameAssembly.dll`（核对 sha256 与证据锁逐字相同），而 r2modman profile 目录里只有 `core/`、`interop/` 等 BepInEx 自己的文件，没有游戏本体。所以要么把 `GameAssembly.dll` 复制/软链到 BepInEx 根下，要么像实测那样用一个 staging 目录当 `$env:FORGE_WEAPON_BEPINEX`：里面放 `GameAssembly.dll` 的副本，`interop` 指向 QA profile 的 `interop`（junction）。缺这个文件时该套件在 hash 检查处失败，与本包源码无关。

```powershell
$a = '<new-empty-artifacts-dir>'
$bep = "$env:APPDATA\r2modmanPlus-local\GTFO\profiles\Forge-MapEditor-QA\BepInEx"
$game = '<GTFO install>'
$dump = '<dump.cs>'
$hostDll = "$a/bin/ForgeRuntime/release/ForgeRuntime.dll"
dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release --artifacts-path $a "-p:GTFOBepInExPath=$bep"
dotnet build ForgeMap/Native/ForgeMap.Native.csproj -c Release --artifacts-path $a "-p:GTFOBepInExPath=$bep" "-p:ForgeRuntimeAssembly=$hostDll"
dotnet build ForgeWeapon/Native/ForgeWeapon.Native.csproj -c Release --artifacts-path $a "-p:GTFOBepInExPath=$bep" "-p:ForgeRuntimeAssembly=$hostDll"
dotnet test ForgeWeapon/tests/NativeAdapter/NativeAdapter.csproj -c Release --artifacts-path $a --filter "Category!=Native" "-p:ForgeRuntimeAssembly=$hostDll"
$env:FORGE_ARTIFACTS = $a; $env:FORGE_WEAPON_BEPINEX = $bep; $env:FORGE_GTFO_GAME = $game; $env:FORGE_GTFO_DUMP = $dump
# NativeLayout 还要 $env:FORGE_WEAPON_HOOK_SPEC 指到绝对路径的 evidence/w1-native-hooks.json，$env:FORGE_WEAPON_LOADOUT_SPEC 指到绝对路径的 evidence/w7-loadout-policy.json（默认都是相对路径，测试宿主的当前目录在构建输出下）。
dotnet test ForgeWeapon/tests/NativeLayout/NativeLayout.csproj -c Release --artifacts-path $a --filter "Category=Native"
dotnet test ForgeWeapon/tests/NativeEvidence/NativeEvidence.csproj -c Release --artifacts-path $a --filter "Category=Native"
```

`dump.cs` 必须与证据文件里的 `dumpSha256` 一致。每次运行都用新的输出目录，`receipt.json` 保留源码哈希与命令退出码。实际结果见 [VALIDATION.md](VALIDATION.md)，独立验收套件的覆盖与限制见 [tests/IdentityAcceptance/README.md](tests/IdentityAcceptance/README.md)。
