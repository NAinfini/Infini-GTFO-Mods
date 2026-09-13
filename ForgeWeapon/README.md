# ForgeWeapon

武器、工具、消耗品共用一个装备领域与 Workshop：装备实例、输入与攻击、弹药与能源与库存、部署与回收、装备表现。时间、状态、目标与事务的基础由 Runtime 提供，**不为每种工具或药剂复制一套框架**。

仓库整体状态见 [ARCHITECTURE.md](../ARCHITECTURE.md)，未完成批次见 [IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md)，验证结果见 [VALIDATION.md](VALIDATION.md)。

## 工坊里的"标准武器"本身就是一张图

按总案第 2 节，内置预设与玩家自制的行为共享同一条编译、校验、执行路径。治疗炮台、命中标记、ping 不是三个系统，是同一批原子的三种拼法——治疗炮台 = 部署动作 + 范围查询 + 关系过滤 + 周期 Control + 治疗 Action，外观在工坊里配。**任何为内置预设开的特权通道都是设计缺陷。**

这条对本包的含义是：Weapon 提供的是真实的装备实例、成本、攻击与部署这些**领域能力**，不是成品功能开关。玩家怎么把它们拼起来由 Trigger 的图决定。

## 当前代码状态

`ModuleDefinition.Create()` 注册 provider `forge.module.gtfo.weapon` 与两个观察型 trigger capability：`forge.trigger.input.equipped`（装备切入）与 `forge.trigger.input.unequipped`（装备切出）。每个 capability 有一个 `role=observe`、`status=implemented` 的 binding，都没有 handler。bindingSupport 均为 **`implementation-only`**，所需权限 `gtfo.equipment.wield.read`。

目前只注册 runtimeBinding，没有 previewBinding，也没有动作 binding。没有自动加载的 BepInEx 插件，没有射击、伤害、换弹或部署能力。

SDK 目前没有输入类 trigger 的合同模块，这两个 capability 暂由 Weapon 自己声明为 owner。若以后 SDK 提供 canonical 合同，要改为绑定到该合同，不能两处同时声明。

已经在生产程序集内的是 W1 的装备身份托管实现，只有显式创建 Session 才生效：

`EquipmentObservation.cs` 把 `EntityReference`、资源 ID 与 revision、显式 owner、slot、Inventory/World/Deployed 位置、加载与持有状态分开，没有另造跨域的 EntityReference。`EquipmentIdentityIndex.cs` 维护有界的活动实例与槽位索引及已观察的生命历史；同槽占用冲突原子拒绝，资源版本不能在同一生命内偷换，移除必须匹配完整引用，旧的 despawn 不会删除复用键上的新生命。`EquipmentIdentitySession.cs` 显式注册同一个 `forge.module.gtfo.weapon` provider 的 `gtfo.equipment` resolver，并通过 `ObserveLifecycle` 清理世界切换、失败、停止和失去主机权限之后的记录。

`EquipmentUseTicket` 是**仅进程内**的前置条件快照：任何已观察到的 owner、slot、location、readiness 或 wield 变化都让旧票据失效，A→B→A 和卸下再装备都不复活旧票据。**它不是权限、不是预留、不是成本收据、不是网络身份、不是检查点数据**，不能序列化成共享的所有权合同。

`Record` 只在同一生命、同一 owner、同一槽位且都在 Inventory 时，直接观察到 `IsWielded` 翻转，才发布 equipped 或 unequipped 事实。初始快照、移动、转交和新生命都不发布。

当前必须在 Runtime 的注册窗口内显式创建 Session 并传入两个真正核验当前原生实例和玩家生命的探测函数。原生接线里这两个函数分别是 `EquipmentNativeAdapter.IsNativeCurrent`（严格读回）与注入的 `WeaponPlayerReferences.IsCurrent`。写入与解析要求 Runtime Ready 且已完成首个 host tick，未初始化、客户端、未知权限与注销状态都不接收记录。示例测试里的永真探测**只能用于合成输入，绝不能作为游戏默认实现**。

索引不生成世界、生命或资源身份，也不从 slot、模型、资源名、owner 或裸指针推断另一个身份。新实例必须由原生接线提供经核验的新引用；同一 ID 必须在精确退役之后以更大的 lifeEpoch 再出现。历史到预算上限时明确拒绝，**不淘汰旧记录后放行重放**。探测期间重入 Record / Remove / Dispose 会被拒绝；探测中发生世界切换、停止或注销会重新检查，迟到的观察不能写进新世界。

## 原生观察接线（implementation-only）

`Native/ForgeWeapon.Native.csproj` 是独立项目，引用真实 interop 程序集编译，不进入 `ForgeWeapon.dll`，也**没有 BepInEx 入口**。

组成：
- `WeaponNativeSession` 按固定顺序启动：注册窗口内先注册身份 Session（重复 provider 在装 Hook 前失败），再装 Hook；失败时回滚，释放时先注销再卸 Hook。任何意外异常都锁存故障，并清空句柄表。
- `WeaponNativeHooks` 有 7 个 `Priority.Last` 的 postfix-only Hook，只读回、不改参数或返回值：
  - `PlayerBackpack.CreateAndStoreBackpackItem` / `TryClearSlot` / `DestroyAllInstance`
  - `PlayerInventoryLocal.DoWieldItem` / `UnWield`
  - `PlayerInventorySynced.DoEquipItem` / `UnWield`
- `EquipmentNativeAdapter` 只在 `SNet.IsMaster`、Runtime Ready 且 host 时对账。
  - 背包每个有实例的槽位都读回成 `EquipmentObservation`。
  - 实例 ID `gtfo.equipment:<world>.<n>` 由接线按世界递增生成，槽位、指针或资源变化即退役旧生命。
  - owner 解析不到就不记录，不从指针或名字推断玩家。

证据文件 `evidence/w1-native-hooks.json` 锁定的内容：
- 每个 Hook 的签名、是否 virtual、dump RVA（各自唯一且不共享）。
- 14 条到达或清理路径的直接调用边。
- 3 条离线表现调用边：DoWieldItem 调 `FirstPersonItemHolder.SetWieldedItem` 与 `PlayAnimationsForWieldedItem`，本地 UnWield 调 `FirstPersonItemHolder.UnWield`。**这 3 条不证明模型挂载、rig 或动画的实际结果。**

阻塞游戏加载的原因：没有领域注册 `gtfo.player` resolver，也没有公开的 SNet_Player→EntityReference 查询。Weapon 不能自造玩家身份，所以不提供插件入口。

已知未核验的点：
- `Slots` 下标是否等于 `InventorySlot` 值。
- `IsLoaded` 的实际语义。
- UnWield 后 `WieldedItem` 的状态。
- Bot 等其他 inventory 子类。
- 两次 Hook 之间指针被复用且没有任何清理 Hook。
- 转交时新背包先于旧背包被对账。
- 事件时间取 Hook 时刻。

## 原生 API 证据

W1 已交付只读的元数据核验工具与精确的输入锁：Steam app `493520` / build `20403457` / revision `34873`，GameAssembly SHA-256 `C6A5C3CD8CA5FE2A8C1A71A3D107663E8CBF01404820DBBDA2EA10C4BFD7BF55`，global-metadata SHA-256 `F57AE2790F7AE0A7ACCAD42BD5F5A541EE74C85E96B4D4B64A346B465C23C882`，锁定 8 个文件、34 个类型、164 个方法、47 个属性、22 个字段。

初次名称检索得到 345 个候选类型，之后补读 8 个身份与库存与网络类型；**这不是可运行的能力计数**，最终只按人工选择的精确全名、程序集、基类和签名核验。

这批元数据锁的证据等级是 **metadata-only**，原生 Hook 的静态证据见上一节：读取 BepInEx interop 的元数据并锁定同机游戏与生成的元数据文件，**没有执行原生方法、没有核验完整的原生调用链、没有安装 Hook、没有启动 GTFO**。同机文件存在，不单独证明 interop 的生成来源与原生地址映射正确。工具本身不进入 ForgeWeapon 的生产 DLL。

## 边界

网站可以挂载多个部件或预览模型，不证明 GTFO 支持相同数量的活动组件。Enemy 与 Map 分别拥有敌人和玩家与世界的 receiver；Weapon 不能为了省接口直接写它们的私有字段。一个原生攻击或附加效果必须指定唯一的执行 binding——**观察到原生承伤不代表还要再施加一次同额伤害**。

`tests/fixtures/w1-runtime-acceptance.json` 里的 20 个完整接线规格已经写好但 **0 个执行**，`verification=not-executed`；它们不是新的 Runtime IR 也不是已实现的库存服务，不计为通过的玩法测试。

W1 仍待完成：
- 游戏加载入口，受 `gtfo.player` 引用阻塞。
- 非背包生成路径的采集。
- 共享 R3/R5 合同的消费。
- 模型、rig 与动画的运行时核验。
- 全部游戏验证。

W2–W8 未开始。当前 DLL 不是玩家发行物。

## 复跑

从仓库根目录：

```powershell
python ForgeWeapon/tools/verify-w1.py --bepinex <existing-BepInEx> --game <existing-GTFO> --architecture
python ForgeWeapon/tools/verify-identity-acceptance.py
python ForgeWeapon/tools/verify-identity-dispatch.py
python ForgeWeapon/tools/test-identity-mutations.py
```

第一条是统一入口（`--architecture` 附带跨模块架构断言），后三条是并发任务编写的独立套件与变异检查。**`verify-w1.py` 不包含原生接线的套件。** 原生接线的套件直接用 dotnet 构建与运行，输出放到仓库外的隔离目录：

```powershell
dotnet build ForgeWeapon/Native/ForgeWeapon.Native.csproj -c Release --artifacts-path <out>/native -p:GTFOBepInExPath=<existing-BepInEx>
dotnet build ForgeWeapon/tests/NativeAdapter/NativeAdapter.csproj -c Release --artifacts-path <out>/managed
dotnet <out>/managed/bin/NativeAdapter/release/NativeAdapter.dll <report.json>
dotnet <NativeLayout.dll> <BepInEx> <out>/native/bin/ForgeRuntime.Framework/release/ForgeRuntime.Framework.dll <out>/native/bin/ForgeWeapon/release/ForgeWeapon.dll <out>/native/bin/ForgeWeapon.Native/release/ForgeWeapon.Native.dll ForgeWeapon/evidence/w1-native-hooks.json <report.json>
dotnet <NativeEvidence.dll> <BepInEx> <GTFO root> <dump.cs> ForgeWeapon/evidence/w1-native-hooks.json <report.json>
```

`dump.cs` 必须与证据文件里的 `dumpSha256` 一致。每次运行都用新的输出目录，`receipt.json` 保留源码哈希与命令退出码。实际结果见 [VALIDATION.md](VALIDATION.md)，独立验收套件的覆盖与限制见 [tests/IdentityAcceptance/README.md](tests/IdentityAcceptance/README.md)。
