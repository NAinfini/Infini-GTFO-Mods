# ForgeWeapon 验证记录

**上次更新：2026-09-14**（内容合并自原 W1-IDENTITY-HANDOFF、W1-IDENTITY-ACCEPTANCE-HANDOFF、W1-IDENTITY-REVIEW-HANDOFF、W1-NATIVE-API-AUDIT 四份交接记录与旧 VALIDATION）。

## W1 背包之外装备路径调研与部署读回（2026-09-14）

新增 `evidence/w1-native-world-paths.json`：只读调研背包之外的装备出现/消失路径（关卡拾取、世界掉落、部署物、转移），记录所用 interop 目录与程序集 sha256、每个成员的签名、RVA 唯一性（dump 中声明该 RVA 的条目数）、直接调用方与同步/本地归属，以及"能否提供身份与 owner"的判定。调研结论只接线了一条路径：`PlayerBackpack.SetDeployed` 的 postfix（第 8 个 Hook）加 `PlayerBackpack.IsDeployed(slot)` 读回，同一个装备生命在部署时以 `Location=Deployed`（无槽位、未持有）记录，收回后回到 `Inventory`；其余路径记录在案但不接线（世界物品的 `itemID_gearCRC` 没有 gear/item 判别，也没有世界物品表，资源定义只能靠猜；世界实例与虚拟分发的方法没有直接调用边）。

- `WeaponNativeHooks` 7 → 8 个 Hook；`NativeLayout` 的纯查询白名单增加 `IsDeployed`，精确读取集合增加 `Player.PlayerBackpack::IsDeployed`。
- `weapon.equipment-life-started` 行末增加 `location=Inventory|Deployed`；同一生命的部署状态变化另写 `weapon.equipment-location`；`Record` 对非 Inventory 位置不发布 equipped/unequipped，因此部署与回收只产生观察变化，不补造持有事实。
- 证据文件 `w1-native-hooks.json`：8 个 Hook、32 条直接调用边（新增 15 条）。

interop 目录：调研用 r2modman `Forge-MapEditor-QA` profile 的 `BepInEx/interop`（`Modules-ASM.dll` sha256 `E499B9C0…36D63`，mvid `6d066008-28db-4edf-9c0e-df9db732560d`；`GameData-ASM.dll` `DEE52362…E7106`；`SNet_ASM.dll` `6DAD1168…CF9B2C`）。`w1-native-hooks.json` 与 `w1-native-contract.json` 里冻结的文件锁指向 `Temp` profile 的旧副本（`A31AF38F…07943`，mvid `2875668a-…`），两份副本的 MVID 与文件字节不同，但逐类型/字段/属性/方法签名的 Cecil 指纹完全一致（`Modules-ASM.dll` rows=170126 `7E7665E8…C8B`，`GameData-ASM.dll` rows=32818 `1870F20A…CB6`，`SNet_ASM.dll` rows=6869 `83B16D5D…F83`），所以没有改写冻结的锁，`NativeEvidence` 仍指向 `Temp` 副本运行。

全部用会话临时目录的隔离 `--artifacts-path` 与 `--disable-build-servers` 构建，报告写到新目录，没有安装、没有启动 GTFO、没有改动 ForgeRuntime 的源码或 Git 状态。

| 套件 | 退出码 | 输出结尾 |
| --- | --- | --- |
| 宿主、`ForgeWeapon`、`ForgeMap.Native`、`ForgeWeapon.Native` 构建 | 0 | 各 0 警告 0 错误 |
| `tests/NativeLayout` | 0 | `PASS 68/68 Weapon native layout checks; no GTFO execution.` |
| `tests/NativeEvidence`（`Temp` profile） | 0 | `PASS 71/71 Weapon static native evidence checks; game execution NOT tested.` |
| `tests/Identity` | 0 | `RESULT {…"cases":43,"assertions":102…,"gameExecuted":false,"installed":false,"nativeCalls":0}` |
| `tests/IdentityAcceptance --report <新路径>` | 0 | `INDEPENDENT IDENTITY: 37/37 passed; gameExecuted=false; synthetic inputs` |
| `tests/NativeAdapter`（工作区 SDK） | 1 | `FAIL 11/43 Weapon native adapter cases`，32 个 `Unsupported plan version.` |
| `tests/NativeAdapter`（提交版 HEAD SDK，隔离构建） | 0 | `PASS 43/43 Weapon native adapter cases; managed doubles, no GTFO execution.` |
| `tests/IdentityDispatchReview --report <新路径>`（工作区 SDK） | 1 | `DISPATCH REVIEW: 2/20 passed`，18 个 `Unsupported plan version.` |
| `tests/IdentityDispatchReview`（提交版 HEAD SDK，隔离构建） | 0 | `DISPATCH REVIEW: 20/20 passed; fixture-only; gameExecuted=false` |
| `ForgeRuntime/tests/Architecture` | 非 0（`0xE0434352`） | `FAIL: Weapon still owns its two observed wield triggers` |

改动前后的计数（同一台机器、同一次会话）：

| 套件 | 改动前 | 改动后 |
| --- | --- | --- |
| NativeAdapter（提交版 HEAD SDK） | 38/38 | 43/43 |
| NativeAdapter（工作区 SDK） | 11/38 | 11/43 |
| NativeLayout | 62/62 | 68/68 |
| NativeEvidence | 52/52 | 71/71 |
| Identity | 42 场景 / 99 断言 | 43 场景 / 102 断言 |
| IdentityAcceptance | 37/37 | 37/37 |

NativeAdapter 新增 5 例：`deploy.sentry-slot-reads-back-deployed`、`deploy.pickup-returns-to-inventory`、`deploy.wielded-deployed-slot-publishes-no-fact`、`deploy.stale-deployed-observation-is-not-current`、`log.deploy-location-lines-are-exact`（日志逐字比对）。Identity 新增 `deploy-and-recall-keeps-one-life`。NativeLayout 的 62 → 68 是第 8 个 Hook 的 6 项形状检查（声明目标、interop 元数据里的唯一目标方法、postfix-only、单个 `__instance` 参数、`Priority.Last`、经会话守卫）；`IsDeployed` 加入纯查询白名单与精确读取集合属于既有检查的内容变化，不新增检查项。

**两个套件在当前工作区无法运行，原因不是本次改动。** `ForgeRuntime/Framework/RuntimePlan.cs` 在本会话期间被另一任务改成要求 `schemaVersion == 3`（D-017 R4-a 的步骤/后继/纯步骤计划格式），而 Weapon 的测试夹具仍写 v2，于是所有需要加载计划的用例报 `Unsupported plan version.`。这可复现地定位为环境问题：把提交版 HEAD 的 `ForgeRuntime/Framework` 源码与本次工作区的 Weapon 源码一起隔离构建后，同一批用例 43/43 通过，改动前的 38 个用例也是 38/38；工作区 SDK 下改动前就是 11/38。没有改 ForgeRuntime，也没有把 Weapon 的夹具迁到尚未定稿的 v3 计划格式。`Architecture` 的失败来自另一任务在 `ForgeTrigger/ModuleDefinition.cs` 里新增的 `forge.condition.predicate.compare` capability：断言仍要求能力列表恰为 Weapon 的两项，本次改动没有触碰任何托管 capability，也没有触碰该测试文件。

`NativeEvidence` 用 `Forge-MapEditor-QA` profile 运行时会停在 69/71，失败的只有 `hash.Modules-ASM.dll` 与 `mvid.Modules-ASM.dll`（该 profile 的副本 2026-09-09 重新生成过），签名、RVA 唯一性、可执行段与 32 条调用边全部通过，因此按上面的指纹等价结论改用冻结锁对应的 `Temp` 副本运行。

**这些仍是托管替身、编译后元数据与静态调用图证据，不是游戏验证。** 部署读回的真实行为（游戏是否保留部署槽位的 `BackpackItem` 实例、原生标记的生命周期）未在游戏内核验，已列入 README 的未核验清单。

## W1 游戏加载入口（2026-09-13，implementation-only）

新增 `Native/Plugin.cs`，删除 `WeaponPlayerReferences`。适配器与身份会话都只经 SDK 的 `ResolveEntityInstance("gtfo.player", …)` 与 `IsEntityCurrent` 使用 ForgeMap 的玩家引用，接口见 [Runtime 验证记录](../ForgeRuntime/VALIDATION.md#原生实例解析2026-09-13)。适配器新增有界的信息日志，格式见 [README](README.md#原生观察接线implementation-only)。

全部用会话临时目录的隔离 `--artifacts-path` 与 `--disable-build-servers` 构建，`GTFOBepInExPath` 指向只读 Temp profile 的 BepInEx；报告写到新目录，没有覆盖旧证据，没有安装、没有启动 GTFO。

| 套件 | 退出码 | 输出结尾 |
| --- | --- | --- |
| 宿主、`ForgeMap.Native`、`ForgeWeapon.Native` 构建 | 0 | 各 0 警告 0 错误 |
| `tests/NativeAdapter` | 0 | `PASS 38/38 Weapon native adapter cases; managed doubles, no GTFO execution.` |
| `tests/NativeLayout` | 0 | `PASS 62/62 Weapon native layout checks; no GTFO execution.` |
| `tests/NativeEvidence` | 0 | `PASS 52/52 Weapon static native evidence checks; game execution NOT tested.` |
| `tests/Identity` | 0 | `RESULT {"verification":"managed-implementation-only","cases":42,"assertions":99,…,"gameExecuted":false,"installed":false,"nativeCalls":0}` |
| `tests/IdentityAcceptance --report <新路径>` | 0 | `INDEPENDENT IDENTITY: 37/37 passed; gameExecuted=false; synthetic inputs` |
| `tests/IdentityDispatchReview --report <新路径>` | 0 | `DISPATCH REVIEW: 20/20 passed; fixture-only; gameExecuted=false` |
| `ForgeRuntime/tests/Architecture`（Weapon 改动之后） | 0 | `PASS 36 architecture boundary assertions. No GTFO hooks, gameplay, networking or installation exercised.` |

NativeAdapter 从 26 变为 38。测试中的玩家 provider 换成只暴露 SDK 表面（resolver + 实例解析器）的 `gtfo.player` 替身，引用改为 `gtfo.player:<id>`。原 `contract.unloaded-wielded-item-rejected` 并入 `contract.rejected-observation-does-not-fault`（仍用未加载却持有触发拒绝）。新增：
- `owner.non-current-player-reference-is-unresolved`：拥有者 resolver 不再接受的引用经 SDK 复核后为 null，只警告一次、不锁存。
- `owner.resolved-through-sdk-player-lookup`：记录的 owner 就是 SDK 返回的引用，查询输入只有背包的 `SNet_Player`。
- `owner.new-player-life-retires-equipment-without-history`：玩家换 life 后旧装备失效、以新 life 重新记录、不补造持有事件。
- `owner.missing-player-lookup-fails-closed`：`gtfo.player` 没有实例解析器时锁存故障（`RuntimeContractException: gtfo.player`），不记录。
- `owner.lookup-inside-entity-inspection-is-stale-not-fault`：`InspectEntities` 期间装备报 `stale-entity`，查询后仍然当前、不锁存。
- `log.life-wield-and-clear-lines-are-exact`：生命开始、持有事件、生命结束、世界切换清表的日志逐字比对，且无警告。
- 插件 7 例：`plugin.off`、`plugin.missing-runtime`、`plugin.existing-weapon-provider-conflict`、`plugin.partial-hook-failure`（第 3 个 Hook 失败时装 3 卸 1）、`plugin.log-failure-rollback`（装 7 卸 1、注销）、`plugin.cleanup-failure-preserves-cause`、`plugin.success-owner-from-player-namespace-no-hot-reload`（重复 Load 被拒、不热卸载、宿主 gameplay 门生效、事件 actor 为 `gtfo.player` 引用）。

NativeLayout 从 51 变为 62，参数改为 8 个（增加宿主 `ForgeRuntime.dll` 与 `ForgeMap.Native.dll`）：
- 修正 `GameAssembly` 判定：补上 `_ASM` 后缀，`SNet_ASM` 现在计入游戏访问；同时加载 `SNet_ASM` 解析 Hook 目标。
- `native.forge-references` 精确为 SDK、宿主、ForgeWeapon；`native.no-map-reference`。
- `native.owner-through-sdk-player-lookup`：调用 `ResolveEntityInstance` 与 `IsEntityCurrent`、使用字面量 `gtfo.player`、没有 `WeaponPlayerReferences`、不自己登记 resolver 或 observer；`native.no-account-lookup`：没有 `get_Lookup`。
- `native.exact-game-reads` 固定 21 个成员：`GearIDRange::GetChecksum`、`Il2CppArrayBase`1::get_Item/get_Length`、`Il2CppObjectBase::TryCast/get_Pointer`、`BackpackItem::get_GearIDRange/get_Instance/get_IsLoaded/get_ItemID`、`PlayerAgent::get_Inventory/get_Owner`、`PlayerBackpack::get_Owner/get_Slots`、`PlayerBackpackManager::TryGetBackpack`、`PlayerInventoryBase::get_Owner/get_WieldedItem`、`SNet::get_IsMaster`、`SNet_Player::get_HasPlayerAgent/get_PlayerAgent`、`UnityEngine.Object::op_Equality/op_Inequality`。`native.snet-read-only`：SNet 成员全是 getter。
- `native.harmony-install-and-unpatch-only`；插件检查 `plugin.single-entry`、`plugin.identity`、`plugin.depends-on-host-and-map`（依赖 id 与版本从构建出的宿主与 Map 插件属性读取）、`plugin.off-gate-before-runtime-and-hooks`、`plugin.gameplay-gate-from-host`、`plugin.no-hot-unload`。

中间运行：精确读取集合先留空跑一次取实际集合（61/62，只有这一项失败），核对全部是 getter、`TryCast` 或相等运算且没有 `get_Lookup` 后固定为字面量，重跑 62/62。加入信息日志后全部六个套件与 Architecture 重跑，结果即上表。

**这些仍是托管替身与编译后元数据证据，不是游戏验证。**

## 游戏内核验清单（未执行）

目的：确认插件在真实游戏中加载，owner 来自 Map 的玩家引用，各退出场景的日志与托管替身一致。**以下期望都未经游戏验证**；某步无法操作时记"未执行"，不要推断。

准备：
- 在 r2modman 新建一个隔离的测试 profile，不用日常 profile，也不用编译参考的 Temp profile。只放 BepInEx、宿主 `ForgeRuntime.dll` 及 `ForgeRuntime.Framework.dll`，`ForgeMap.dll` + `ForgeMap.Native.dll`，`ForgeWeapon.dll` + `ForgeWeapon.Native.dll`，全部取同一次构建。宿主模式设为 Play（非 Off）。
- 用户开私人大厅当主机，可带 bot。开始前清空或记下 `BepInEx/LogOutput.log` 的位置，以下日志都在该文件里搜索。
- 下面的 `<W>` 是当前世界编号，`<n>` 是装备序号，`<m>` 是玩家编号，均以实际值为准。

| 步骤 | 操作 | 期望日志（按出现顺序） |
| --- | --- | --- |
| 0 加载 | 启动游戏到主菜单 | `[Info   :Infini Forge Map] Forge Map registered gtfo.player identity; native bindings remain implementation-only.`，其后 `[Info   :Infini Forge Weapon] Forge Weapon registered equipment identity with gtfo.player owners; native bindings remain implementation-only.`；不应出现 `Weapon equipment observation disabled until restart` |
| 1 拾取 | 进关出生；再在关卡里拾取一个资源包或消耗品 | `map.player-life-started id=gtfo.player:<m> world=<W> life=1 bot=false`；每个有物品的槽位一行 `weapon.equipment-life-started id=gtfo.equipment:<W>.<n> world=<W> owner=gtfo.player:<m> ownerLife=1 slot=<槽位> resource=gtfo.gear:<checksum> location=Inventory`（非 gear 物品为 `gtfo.item:<id>`）；拾取后多一行同格式、新 `<n>` 的 life-started。若先出现一次 `weapon.owner-unresolved`，记下，它对应"出生 Hook 早于 Map 登记"的未核验项 |
| 2 切换 | 从主武器切到副武器 | `weapon.wield-fact kind=unequipped id=<主武器 id> owner=gtfo.player:<m> status=ignored code=no-consumer` 与 `weapon.wield-fact kind=equipped id=<副武器 id> … status=ignored code=no-consumer`（没有加载计划时 status 为 ignored）；不应出现 life-ended |
| 3 收起 | 让当前武器被收起（例如拿起大型任务物品或爬梯子） | `weapon.wield-fact kind=unequipped id=<该武器 id> …`；恢复持有后同 id 的 `kind=equipped`。没有这两行时记下，它对应"UnWield 后 WieldedItem 状态"的未核验项 |
| 4 换槽 | 用新的同类物品替换某槽位的物品，或丢下资源包 | `weapon.equipment-life-ended id=<旧 id> reason=slot-changed`（被同一实例移动时为 `moved-or-replaced`），替换时随后是新 `<n>` 的 life-started；新旧 id 不同 |
| 5 转交 | 丢下资源包或消耗品，由 bot 或另一名玩家拾起 | 原持有者：`weapon.equipment-life-ended id=<X> reason=slot-changed`（若拾起者的背包先对账，则为 `moved-or-replaced`，两者都符合，记下是哪一个）；拾起者：`weapon.equipment-life-started id=<Y> … owner=gtfo.player:<拾起者编号>`，`Y≠X`；没有 wield-fact 补造转交历史 |
| 6 切世界 | 结束本次行动并开始另一次（或回大厅再下潜） | `map.player-life-started … world=<W2>`；下一次背包 Hook 时 `weapon.equipment-lives-cleared world=<W> count=<旧数量> reason=world-changed`，随后 `weapon.equipment-life-started id=gtfo.equipment:<W2>.1 world=<W2> …`（序号从 1 重新开始） |
| 7 倒地救起 | 倒地后被 bot 或队友救起，再切一次武器 | 不应出现 `map.player-life-ended` 或 `weapon.equipment-life-ended`；救起后的 wield-fact 用倒地前的同一装备 id。出现 life-ended 时原样记下 reason，它对应"倒地与救起不重建 PlayerAgent"的未核验项 |
| 8 检查点 | 在有检查点的关卡团灭或重开检查点 | `map.player-life-started … world=<新世界>`；Weapon 同第 6 步（`reason=world-changed`，新世界序号从 1 开始）。若出现 `reason=identity-cleared` 或 `weapon.observation-rejected`，原样记下 |
| 9 隐私 | 退出游戏后，用自己的 Steam64 ID 搜索 `LogOutput.log` 与本次产生的所有 Forge 报告 | 只回答"搜到"或"没搜到"，不要粘贴 ID 或上下文。期望：没搜到 |

全程任何一步出现 `Weapon equipment observation disabled until restart`、`weapon.wield-fact-rejected` 或持续的 `weapon.owner-unresolved`（玩家已有 `map.player-life-started` 之后仍出现），都视为该步失败并保留日志。结果回填时逐步写"符合 / 不符合（附日志行）/ 未执行"。

## W1 原生观察接线复验（2026-09-13，加载入口之前）

下表是加入加载入口之前的计数，现行结果见上文。

全部用隔离的 `--artifacts-path` 构建，`-p:GTFOBepInExPath` 指向只读的 BepInEx interop，没有写入任何 profile 的 plugins 目录。

| 套件 | 退出码 | 输出结尾 | 说明 |
| --- | --- | --- | --- |
| `Native/ForgeWeapon.Native.csproj` 构建 | 0 | 0 警告 0 错误 | 引用真实 interop 程序集编译；不含 BepInEx 入口 |
| `tests/NativeAdapter` | 0 | `PASS 26/26 Weapon native adapter cases; managed doubles, no GTFO execution.` | 编译同一批原生源码，替换为托管游戏替身 |
| `tests/NativeLayout` | 0 | `PASS 51/51 Weapon native layout checks; no GTFO execution.` | Cecil 检查构建产物：依赖方向、只读访问、Hook 集合与 postfix 形状 |
| `tests/NativeEvidence` | 0 | `PASS 52/52 Weapon static native evidence checks; game execution NOT tested.` | 文件 hash、MVID、签名、dump RVA 唯一、不共享、可执行段，17 条直接调用边 |
| `tests/Identity` | 0 | `"cases":42,"assertions":99` | 改为 2 个观察 binding 后重跑 |
| `tests/IdentityAcceptance` | 0 | `INDEPENDENT IDENTITY: 37/37 passed; gameExecuted=false; synthetic inputs` | |
| `tests/IdentityDispatchReview` | 0 | `DISPATCH REVIEW: 20/20 passed; fixture-only; gameExecuted=false` | |
| `ForgeRuntime/tests/Architecture` | 0 | `PASS 36 architecture boundary assertions. No GTFO hooks, gameplay, networking or installation exercised.` | 3 条"不声明能力"断言改为精确匹配 Weapon 的两个观察 binding |

本批最初两次运行失败，都已修复，没有放宽检查。第一次是 NativeAdapter 夹具的 plan 顺序错误（binding lock 与 permissions 未按 ordinal 排序），修正的是夹具。第二次是 `exit.world-switch-clears-and-reobserves` 失败（25/26）：实例序号跨世界不归零，修正的是适配器，世界切换时序号归零。同一世界内序号不回退，因为索引保留该世界的退役生命。

NativeAdapter 覆盖的场景：
- 会话：注册先于 Hook、重复 provider、安装失败回滚、清理失败保留原因、迟到注册、注销后卸 Hook。
- 初始快照不产生事件；无 GearIDRange 时用 ItemID 作资源。
- 直接观察到的持有翻转依次发布 equipped、unequipped；同步 inventory 路径同样生效。
- 未观察到的变化只让探测失效，不补造历史；已销毁 agent 的 inventory 被忽略。
- 退出场景：同槽新武器（有无清槽 Hook 两种）、同资源多实例、转交后旧 owner 退役且不补造转交历史、世界切换、指针复用成为新生命（有无清槽 Hook 两种）、销毁全部实例。
- owner 未解析时不记录；客户端或 gate 关闭时不记录。
- 合同拒绝不锁死会话；未加载却持有被拒绝；意外原生异常锁存故障。

**这些都是托管替身结果，不是游戏验证。**

NativeEvidence 的证据范围：直接 near call 边与方法边界来自 dump 的下一个 RVA。它**不**解析虚调用、字段写入、运行时实际调用顺序或多人流量。3 条表现调用边只说明离线调用关系，不证明模型挂载、rig 或动画结果。

## 此前的统一入口复验（verify-w1.py，原生接线之前）

| 套件 | 结果 | 说明 |
| --- | --- | --- |
| Weapon / SDK / Identity 构建 | 0 警告 0 错误 | |
| 原身份套件 | 42 场景、99 断言 | 由并发的实现任务编写 |
| 独立验收套件 | 37/37 | 由另一并发任务编写，本任务只复读源码并独立运行 |
| 公开队列与命令联调 | 20/20 | 消费实际编译的 `EquipmentIdentitySession` 与唯一 SDK |
| 元数据核验 | 312/312 | metadata-only |
| 核验工具正反测试 | 24/24 | 变异的是合同副本，不是游戏文件 |
| 跨模块架构断言 | 通过 | 当时记录 35 项；**现行 Program.cs 是 36 项**，见 `ForgeRuntime/tests/Architecture/Program.cs` |
| 故意错误实现 | 6/6 被检出 | 合并前是四类与五类两套，现已按检错范围合并；所有错误版本先编译成功 |

**这些数量描述不同层的检查，不能相加作为已实现的玩法数。** 24 个工具测试里的错误输入拒绝是测试预期，不是 24 个游戏场景；312 是所选文件、类型与成员的核验断言，不是完整的原生语义证明；6 个变异防护不是六项原生玩法。

变异 runner 检查四处关键防护：票据版本、退役生命、owner 探测、世界通知；合并后覆盖六类，分别由原身份套件和独立套件检出。

独立验收套件覆盖共享 provider 注册、重复观察、实例与资源身份、A→B→A 所有权、owner 生命变化、wield 与 loading 状态、原子的 slot 与 definition 拒绝、live-predicate 失败、epoch 与权威与停止与释放、外来票据、回调重入、退役生命的重放保护、有界的活动与历史容量。它**不**实现或测试实际的弹药事务、生成与部署副作用、攻击执行、多人流量或检查点。

公开队列联调的 20 个用例覆盖：排队后装备销毁、原生与 owner 失效、同 key 新 life、转交 A→B→A、卸下、owner 新生命、世界切换、resolver 注销、客户端拒绝、取消、权限、提交后清理。

## 尚未执行的规格

`tests/fixtures/w1-runtime-acceptance.json` 里写好了 20 个合成规格，覆盖实例与槽位与转交与 epoch、最后一发竞争、miss 仍扣费、重复回调、预留过期、换弹容量与来源限制、放置失败、提交不明、回收、周期能源。**`verification=not-executed`，0 个执行。** 它们不是新的 Runtime IR 也不是已实现的库存服务，不计为通过的玩法测试；托管子问题的测试也不能计作 native、成本或部署断言已通过。

工具收据里 `gameExecuted=false`、`installed=false`。

## 原生元数据锁

Steam app `493520` / build `20403457` / revision `34873`。GameAssembly SHA-256 `C6A5C3CD8CA5FE2A8C1A71A3D107663E8CBF01404820DBBDA2EA10C4BFD7BF55`，global-metadata SHA-256 `F57AE2790F7AE0A7ACCAD42BD5F5A541EE74C85E96B4D4B64A346B465C23C882`。精确锁覆盖 8 个文件、34 个类型、164 个方法、47 个属性、22 个字段。

证据等级 **metadata-only**：只读 BepInEx interop 元数据并锁定同机游戏与生成的元数据文件，没有执行原生方法、核验完整调用链、安装 Hook 或启动 GTFO。同机文件存在不单独证明 interop 的生成来源与原生地址映射正确。

## 保留的失败记录

两次完整依赖复验曾失败于并发的 Runtime 输入尚未写完：`Framework/RuntimeKernel.Lifecycle.cs(27,87)` 与 `Framework/RuntimeKernel.cs(198,6)` 各报一次 `CS1513: } expected`。没有替 Runtime 补括号、回滚、覆盖或放宽构建门槛；后续复读发现 Lifecycle 文件已从 27 行增长为完整的 135 行，但第二次构建又读到另一个共享文件的中间状态。**不能据此认定 Runtime 最终实现有固定缺陷，也不能用较早的架构通过结果覆盖这两次失败。**

一次较早的跨模块复验曾在 `ForgeEnemy/EnemyLifeTable.cs(97,85)` 报 `CS1503`（CultureInfo 与 NumberStyles 参数不匹配）。没有修改 Enemy；最终实际重跑通过，不用旧结果覆盖失败，也不把其修复归到 Weapon。

一份并行的"仅物理会话"草稿已从生产移除，保留在被忽略的 `.artifacts` 下；生产方向只有 `EquipmentIdentitySession` 与索引这一条。一次原生静态代码检查在检查前被工具安全检查拦截，其未验证的草稿被排除在 NativeAudit 工程之外，先前的元数据审计器已恢复。该记录时本包没有原生代码层面的断言。现行的静态原生断言只来自 `tests/NativeEvidence`，见上文。

## 复跑

从仓库根目录：

```powershell
python ForgeWeapon/tools/verify-w1.py --bepinex <existing-BepInEx> --game <existing-GTFO> --architecture
python ForgeWeapon/tools/verify-identity-acceptance.py
python ForgeWeapon/tools/verify-identity-dispatch.py
python ForgeWeapon/tools/test-identity-mutations.py
python -m unittest ForgeWeapon/tests/test_native_audit.py
```

默认的 `verify-w1` 已包含 Identity 的构建与测试；`metadata-only` 模式不含它们。变异脚本在新的隔离目录里先构建未修改的副本，再在各自的 `.artifacts` 副本里故意破坏检查——**编译错误不计作变异检出成功**，每个变体必须让它对应的具名回归测试失败。

每次运行都用新的输出目录。`receipt.json` 保留源码哈希与命令退出码，`tests.json` 保留逐项结果。运行期间输入发生变化会使该次的稳定性声明失效；已有证据不被覆盖。这些命令不改动生产 C#、用户游戏文件、profile 设置或 Git 状态。

独立验收套件的详细覆盖与限制见 [tests/IdentityAcceptance/README.md](tests/IdentityAcceptance/README.md)。
