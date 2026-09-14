# ForgeWeapon

武器、工具、消耗品共用一个装备领域与 Workshop：装备实例、输入与攻击、弹药与能源与库存、部署与回收、装备表现。时间、状态、目标与事务的基础由 Runtime 提供，**不为每种工具或药剂复制一套框架**。

计划与状态见两仓统一框架第 6 节 U-WEAPON-MOD（链接见[仓库 README](../README.md)），带日期的验证记录见 [VALIDATION.md](VALIDATION.md)。

## 工坊里的"标准武器"本身就是一张图

内置预设与玩家自制的行为共享同一条编译、校验、执行路径。治疗炮台、命中标记、ping 不是三个系统，是同一批原子的三种拼法——治疗炮台 = 部署动作 + 范围查询 + 关系过滤 + 周期 Control + 治疗 Action，外观在工坊里配。**任何为内置预设开的特权通道都是设计缺陷。**

这条对本包的含义是：Weapon 提供的是真实的装备实例、成本、攻击与部署这些**领域能力**，不是成品功能开关。玩家怎么把它们拼起来由 Trigger 的图决定。

## 运行清单与 W1 装备身份

`ModuleDefinition.Create()` 注册 provider `forge.module.gtfo.weapon` 与两个观察型 trigger capability：`forge.trigger.input.equipped`（装备切入）与 `forge.trigger.input.unequipped`（装备切出）。每个 capability 有一个 `role=observe`、`status=implemented` 的 binding，都没有 handler。bindingSupport 均为 **`implementation-only`**，所需权限 `gtfo.equipment.wield.read`。

目前只注册 runtimeBinding，没有 previewBinding，也没有动作 binding。原生观察由独立的 BepInEx 插件 `NAinfini.ForgeWeapon` 加载（implementation-only，见下文）；没有射击、伤害、换弹或部署能力。

SDK 目前没有输入类 trigger 的合同模块，这两个 capability 暂由 Weapon 自己声明为 owner。若以后 SDK 提供 canonical 合同，要改为绑定到该合同，不能两处同时声明。

已经在生产程序集内的是 W1 的装备身份托管实现，只有显式创建 Session 才生效：

`EquipmentObservation.cs` 把 `EntityReference`、资源 ID 与 revision、显式 owner、slot、Inventory/World/Deployed 位置、加载与持有状态分开，没有另造跨域的 EntityReference。`EquipmentIdentityIndex.cs` 维护有界的活动实例与槽位索引及已观察的生命历史；同槽占用冲突原子拒绝，资源版本不能在同一生命内偷换，移除必须匹配完整引用，旧的 despawn 不会删除复用键上的新生命。`EquipmentIdentitySession.cs` 显式注册同一个 `forge.module.gtfo.weapon` provider 的 `gtfo.equipment` resolver，并通过 `ObserveLifecycle` 清理世界切换、失败、停止和失去主机权限之后的记录。

`EquipmentUseTicket` 是**仅进程内**的前置条件快照：任何已观察到的 owner、slot、location、readiness 或 wield 变化都让旧票据失效，A→B→A 和卸下再装备都不复活旧票据。**它不是权限、不是预留、不是成本收据、不是网络身份、不是检查点数据**，不能序列化成共享的所有权合同。

`Record` 只在同一生命、同一 owner、同一槽位且都在 Inventory 时，直接观察到 `IsWielded` 翻转，才发布 equipped 或 unequipped 事实。初始快照、移动、转交和新生命都不发布。

当前必须在 Runtime 的注册窗口内显式创建 Session 并传入两个真正核验当前原生实例和玩家生命的探测函数。原生接线里这两个函数分别是 `EquipmentNativeAdapter.IsNativeCurrent`（严格读回）与 SDK 的 `RuntimeKernel.IsEntityCurrent`（owner 是 ForgeMap 登记的 `gtfo.player` 引用，由其拥有者核验）。写入与解析要求 Runtime Ready 且已完成首个 host tick，未初始化、客户端、未知权限与注销状态都不接收记录。示例测试里的永真探测**只能用于合成输入，绝不能作为游戏默认实现**。

索引不生成世界、生命或资源身份，也不从 slot、模型、资源名、owner 或裸指针推断另一个身份。新实例必须由原生接线提供经核验的新引用；同一 ID 必须在精确退役之后以更大的 lifeEpoch 再出现。历史到预算上限时明确拒绝，**不淘汰旧记录后放行重放**。探测期间重入 Record / Remove / Dispose 会被拒绝；探测中发生世界切换、停止或注销会重新检查，迟到的观察不能写进新世界。

## 原生观察接线（implementation-only）

`Native/ForgeWeapon.Native.csproj` 是独立项目，引用真实 interop 程序集与宿主 `ForgeRuntime.dll` 编译，不进入 `ForgeWeapon.dll`，也不引用 ForgeMap 程序集。

组成：
- `Plugin`：BepInEx 插件 `NAinfini.ForgeWeapon` / `Infini Forge Weapon` / `0.1.0`（与 `ForgeWeapon.dll` 版本一致），依赖 `NAinfini.ForgeRuntime` 1.2.0 与 `NAinfini.ForgeMap` 0.1.0。宿主 Off 时不注册、不装 Hook；宿主不可用时抛出；Load 只允许一次，失败回滚并保留原始异常，`Unload()` 返回 false（不热卸载）。发布身份沿用现有命名模式，**未经确认，没有清单或打包**。
- `WeaponNativeSession` 按固定顺序启动：注册窗口内先注册身份 Session（重复 provider 在装 Hook 前失败），再装 Hook；失败时回滚，释放时先注销再卸 Hook。任何意外异常都锁存故障，并清空句柄表。
- `WeaponNativeHooks` 有 8 个 `Priority.Last` 的 postfix-only Hook，只读回、不改参数或返回值：
  - `PlayerBackpack.CreateAndStoreBackpackItem` / `TryClearSlot` / `DestroyAllInstance` / `SetDeployed`
  - `PlayerInventoryLocal.DoWieldItem` / `UnWield`
  - `PlayerInventorySynced.DoEquipItem` / `UnWield`
- `EquipmentNativeAdapter` 只在 `SNet.IsMaster`、Runtime Ready 且 host 时对账。
  - 背包每个有实例的槽位都读回成 `EquipmentObservation`。
  - 实例 ID `gtfo.equipment:<world>.<n>` 由接线按世界递增生成，槽位、指针或资源变化即退役旧生命。
  - 炮台与屏障放置后槽位仍然持有同一件装备，接线用 `PlayerBackpack.IsDeployed(slot)` 读回该槽位的部署标记：标记为真时同一个生命以 `Location=Deployed`（无槽位、未持有）记录，收回后回到 `Inventory`。**部署状态变化不发布 equipped/unequipped 事实**，也不为世界里的部署实例另建身份。
  - owner 只经 SDK 的 `ResolveEntityInstance("gtfo.player", backpack.Owner)` 取得；返回 null 就不记录并警告一次，不从指针、名字、槽位或 `Lookup` 推断玩家。

证据文件 `evidence/w1-native-hooks.json` 锁定的内容：
- 每个 Hook 的签名、是否 virtual、dump RVA（各自唯一且不共享）。
- 32 条到达或清理路径的直接调用边，其中 15 条是 2026-09-14 为部署与回收路径补的（`SentryGunInstance.OnSpawn` 先取 owner 背包再调用部署标记、`SentryGunInstance.SyncedPickup`、`BarrierFirstPerson.PlaceOnGround`、bot 放置、以及矿与投掷物的清槽路径）。
- 3 条离线表现调用边：DoWieldItem 调 `FirstPersonItemHolder.SetWieldedItem` 与 `PlayAnimationsForWieldedItem`，本地 UnWield 调 `FirstPersonItemHolder.UnWield`。**这 3 条不证明模型挂载、rig 或动画的实际结果。**

背包之外的装备路径调研（关卡拾取、世界掉落、部署物、转移）见 `evidence/w1-native-world-paths.json`：记录所用 interop 目录与各程序集 sha256、每个类型的签名与 RVA 唯一性、直接调用方、同步/本地归属，以及为什么只有部署标记被接线。该文件同时记录 `LG_PickupItem`、`ItemInLevel`、`SentryGunInstance`、`MineDeployerInstance` 等真实类型与它们缺少资源定义或 owner 的原因；`PickupItem`、`DeployerInstance` 这类不存在的名字没有被使用。

玩家 owner 的来源：玩家引用 `gtfo.player:<n>` 的编号与 lifeEpoch 由 ForgeMap 的 MAP5a 私有分配（见 [ForgeMap README](../ForgeMap/README.md#map5a-玩家实体身份implementation-only)）；唯一稳定的原生玩家键 `SNet_Player.Lookup` 是 Steam64 账号 ID，不能成为公开键。所以 Weapon 不猜、不自造、不读 `Lookup`，只调用 SDK 的 `ResolveEntityInstance("gtfo.player", player)`，由 Map 按 SNet_Player 指针在已登记表里查找并复核；接口规则见 [Framework README](../ForgeRuntime/Framework/README.md#从原生实例取得引用)。`gtfo.player` 没有实例解析器（例如 Map 未加载）时该调用抛 `entity-resolver`，会话锁存故障、停止观察，**不回退、不记录无 owner 的装备**。

有两处行为是设计后果，不是缺陷：
- `InspectEntities` 期间内核禁止嵌套查询，装备的 owner 复核因此失败，装备在该次查询里显示为 `stale-entity`；Weapon 没有注册实体 observer，这不影响记录，也不锁存故障。
- 清表行在世界切换后的下一次 Hook 才写出，不在切换当刻。

信息日志（BepInEx 来源 `Infini Forge Weapon`，只含 Forge 引用、槽位与资源键）：
- `weapon.equipment-life-started id=gtfo.equipment:<world>.<n> world=<world> owner=gtfo.player:<m> ownerLife=<life> slot=<InventorySlot> resource=gtfo.gear:<checksum>|gtfo.item:<id> location=Inventory|Deployed`
- `weapon.equipment-location id=… location=Inventory|Deployed`：同一生命的部署状态变化时写一次（起始位置已在 life-started 行里）。
- `weapon.equipment-life-ended id=… reason=owner-unresolved|owner-changed|slot-changed|moved-or-replaced|observation-rejected`
- `weapon.wield-fact kind=equipped|unequipped id=… owner=… status=<dispatch status> code=<code>`：没有计划消费时是 `status=ignored code=no-consumer`。
- `weapon.equipment-lives-cleared world=<旧世界> count=<n> reason=world-changed|identity-cleared`

警告：`weapon.owner-unresolved: …`（每个背包一次）、`weapon.observation-rejected: <code>`、`weapon.wield-fact-rejected: <code>`、`Weapon equipment observation disabled until restart: …`。

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

第一条是统一入口（`--architecture` 附带跨模块架构断言），后三条是并发任务编写的独立套件与变异检查。**`verify-w1.py` 不包含原生接线的套件。** 原生接线的套件直接用 dotnet 构建与运行，输出放到仓库外的隔离目录：

`$bep` 为只读 BepInEx 目录，`$game` 为 GTFO 根目录，`$dump` 为同 build 的 dump.cs。原生插件需要同一 artifacts 里的宿主 `ForgeRuntime.dll`；NativeLayout 还读取 ForgeMap 原生插件，核对依赖的 id 与版本：

```powershell
$a = '<new-empty-artifacts-dir>'
$hostDll = "$a/bin/ForgeRuntime/release/ForgeRuntime.dll"
dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release --artifacts-path $a "-p:GTFOBepInExPath=$bep"
dotnet build ForgeMap/Native/ForgeMap.Native.csproj -c Release --artifacts-path $a "-p:GTFOBepInExPath=$bep" "-p:ForgeRuntimeAssembly=$hostDll"
dotnet build ForgeWeapon/Native/ForgeWeapon.Native.csproj -c Release --artifacts-path $a "-p:GTFOBepInExPath=$bep" "-p:ForgeRuntimeAssembly=$hostDll"
dotnet build ForgeWeapon/tests/NativeAdapter/NativeAdapter.csproj -c Release --artifacts-path $a
dotnet "$a/bin/NativeAdapter/release/NativeAdapter.dll" "$a/reports/weapon-native-adapter.json"
dotnet build ForgeWeapon/tests/NativeLayout/NativeLayout.csproj -c Release --artifacts-path $a "-p:GTFOBepInExPath=$bep"
dotnet "$a/bin/NativeLayout/release/NativeLayout.dll" $bep "$a/bin/ForgeRuntime.Framework/release/ForgeRuntime.Framework.dll" $hostDll "$a/bin/ForgeMap.Native/release/ForgeMap.Native.dll" "$a/bin/ForgeWeapon/release/ForgeWeapon.dll" "$a/bin/ForgeWeapon.Native/release/ForgeWeapon.Native.dll" ForgeWeapon/evidence/w1-native-hooks.json "$a/reports/weapon-native-layout.json"
dotnet build ForgeWeapon/tests/NativeEvidence/NativeEvidence.csproj -c Release --artifacts-path $a "-p:GTFOBepInExPath=$bep"
dotnet "$a/bin/NativeEvidence/release/NativeEvidence.dll" $bep $game $dump ForgeWeapon/evidence/w1-native-hooks.json "$a/reports/weapon-native-evidence.json"
```

`dump.cs` 必须与证据文件里的 `dumpSha256` 一致。每次运行都用新的输出目录，`receipt.json` 保留源码哈希与命令退出码。实际结果见 [VALIDATION.md](VALIDATION.md)，独立验收套件的覆盖与限制见 [tests/IdentityAcceptance/README.md](tests/IdentityAcceptance/README.md)。
