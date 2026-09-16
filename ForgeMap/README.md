# ForgeMap

> 实施顺序与原版内容完整覆盖见[唯一开发计划](../../Infini-GTFO-Model-Site/Docs/forge-contract/FORGE-FRAMEWORK.md) §4，分步验收见 §7；本文件仅说明实现与用法。

Map 与 Room 的空间、设备、任务、遭遇、玩家生命流程与进程、世界表现，**以及地图生成的底层逻辑**。

交付要求见唯一开发计划 §6 U-MAP-MOD（链接见[仓库 README](../README.md)），带日期的验证记录见 [VALIDATION.md](VALIDATION.md)。

## 原生地图与资源入口

官方已有房间通过原版生成流程和 LGTuner 使用；LGTuner 保留房间顺序与额外环境资源配置的职责。
ForgeMap 负责地图对象、设备、任务、遭遇及与统一 Trigger 的连接。产品顺序与 MAP-BUILD
要求见上方计划 §4 / §6；第三方资源方案见 §8，不以资源提取或新房间原型阻塞原版关卡。

[GENERATION-SPEC.md](GENERATION-SPEC.md) 只记录原生生成入口、已有描述符夹具及核验方法；
资源描述要求见唯一计划 §3.2；第三方接入选型见 §8。登记资源、预览可见与实机可玩分别取证。

## 运行清单与 MAP1 内部身份层

**运行清单没有 capability 或 binding。** `ModuleDefinition.Create()` 的 capabilities 与 bindings 都是空数组，没有地图 Action。唯一对外的运行期能力是 MAP5a 的 `gtfo.player` 实体 resolver 与原生实例解析器，它们在独立的 `Native/ForgeMap.Native.csproj` 插件里，见下一节。

已经在生产程序集内的是内部身份层，只有显式构造 Session 才登记这一空 provider 并订阅生命周期，不会自动加载：

`MapIdentityContracts.cs` 定义域内地址、精确来源锁、内部 native key、不含指针的快照，以及按创建尝试颁发的票据。`MapObjectIdentityIndex.cs` 管有界关联与历史、world/generation/life 隔离、重复回调、取消、销毁、冲突隔离和观测缺口。`MapIdentitySession.cs` 消费真实的 R2 生命周期，做所属线程检查、精确生命探针、重入与失效检查以及注销。这些类型都是 `internal`，通过限定的 friend assembly 由测试访问实际程序集，不跨目录链接源码。

地址逐项区分 layout 与 revision、dimension、layer、local zone、placement、对象 ID 和对象类别。同一 geomorph 的多个 area 使用各自明确的对象 ID；同资源的两个 placement 不合并。**地址不能通过名字、位置、遍历顺序或预览 GLB 猜测**；提供地址的原生适配器仍待核验。来源锁保留 resource ID 与 revision、来源证据 SHA-256，以及已有 `SourceObjectIdentity` 的 file 与 pathId。

MAP1 也交付了可重复运行的原生 API 与字节证据检查工具：本机 build 的 32 个类型、110 个方法的元数据锁，以及旧审计的 11 个原生区域字节复核。MAP2 定范围时另锁定了生成相关的 27 个类型、59 个方法（`tools/generation-api-targets.json`）。**这些是 metadata-only 证据，不证明原生调用语义、合法阶段或复制完成。**

## MAP5a 玩家实体身份（implementation-only）

从 MAP5 切出的先行切片，目的是给 Weapon 等领域提供经核验的玩家引用。MAP5 其余内容（生命状态映射、救起与重生、落点、检查点）未开始。

`Native/ForgeMap.Native.csproj` 是独立项目，引用真实 interop 编译，不进入 `ForgeMap.dll`：
- `Plugin`：BepInEx 插件 `NAinfini.ForgeMap` / `Infini Forge Map` / `0.1.0`，依赖 `NAinfini.ForgeRuntime` 1.2.0。宿主 Off 时不注册、不装 Hook；Load 只允许一次，`Unload()` 返回 false。本包自己的 cfg 是 `BepInEx/config/NAinfini.ForgeMap.cfg`，`[Logging] Level` 取 `off`、`error`、`info`，默认 `error`，改动需重启，非法值在注册前抛错；该级别随注册交给内核，成为 `forge.module.gtfo.map` 这个 provider 自己的级别。发布身份沿用现有命名模式，**未经确认，没有清单或打包**。
- `MapPluginSession`：与 Enemy 相同的顺序。注册窗口内先注册模块（重复 provider 或 `gtfo.player` 命名空间冲突在装 Hook 前原子失败），再装 Hook；失败回滚先卸 Hook 再注销并保留原始异常；回调异常锁存故障并清表；释放时先注销再卸 Hook；所属线程检查。
- `PlayerIdentityModule`：`MapPluginSession` 在 `ModuleDefinition.Create()` 上追加本包的全部运行期接口——`gtfo.player` 的 `EntityResolvers`、`EntityInstanceResolvers`、`EntityObservers` 与 `EntityCandidates`，以及 `forge.selector.target.players` 的求值器——不另造 provider。
- `MapNativeHooks`：插件安装的 Hook 列表。玩家的 2 个 `Priority.Last` postfix，只决定何时读回、不读参数：`PlayerManager.OnPlayerSpawned` 与 `PlayerManager.OnPlayerDespawned`。两者都是非虚方法，由 `PlayerReplicationManager.OnSpawn` / `OnDeSpawn` 调用，本地、远端与 bot 玩家都经过这里（静态调用边见证据文件）。选这两个是因为它们是所有玩家生成与销毁的共同汇合点：`RegisterPlayerAgent` 与 `PlayerSync.OnSpawn` 没有直接调用者（接口派发），`PlayerAgent.Setup` / `OnDespawn` 是被 `LocalPlayerAgent` 覆盖的虚方法。同一列表里的另外 3 个是门/终端的 map-object 读回（`Native/MapObjectHooks.cs`）。

身份规则：
- 每次读回遍历 `PlayerManager.PlayerAgentsInLevel`；只登记 `agent.Owner` 非空且 `owner.PlayerAgent` 回指同一 agent 的条目。内部字典键是 `SNet_Player.Lookup`。
- **隐私硬规则**：真实玩家的 `Lookup` 是 Steam64 账号 ID，只作 Map 私有字典键，不格式化、不序列化，不进入实体 ID、日志、错误、observer 或测试证据。`MapNativeAdapter` 断言全部日志、清单与报告不含任何 fixture Lookup，`MapNativeLayout` 静态检查原生程序集从不格式化或装箱 `UInt64`。
- 引用 `gtfo.player:<n>`：`n` 是本世界内按首次登记顺序递增的编号，与 Enemy / Weapon 一样由接线计数，不来自槽位；同一玩家在本世界内保持同一编号，WorldEpoch 为内核当前世界，换世界后重新编号。agent 指针变化或 SNet_Player 对象变化即分配新的单调 lifeEpoch，并记 `map.player-life-started id=gtfo.player:<n> world=… life=… bot=true|false`；消失、替换或同键冲突记 `map.player-life-ended … reason=despawned|replaced|key-conflict`。
- 倒地、救起、Heal、传送不替换 agent 就不改 life。检查点重载由宿主暂停并换世界，模块只在 WorldChanged / Failed / Stopped 清表。
- 只在 Runtime Ready、会话未故障且 `SNet.IsMaster` 时分配。**不用 InLevel 玩法门**：电梯阶段的生成与进关同属一个世界。
- owner 解析不到或互链不成立时不登记，并对每个 agent 警告一次 `map.player-owner-unresolved`；同一玩家键出现两个 agent 时两者都不登记（`map.player-key-conflict`，警告不带键）。不按名字、槽位顺序或指针推断身份。
- `IsCurrent` 每次重读：world、前缀、无符号十进制编号、精确引用、agent 未销毁且指针相同、SNet_Player 指针与内部键不变、owner 与互链仍成立。
- 原生实例解析（其他模块经 SDK 的 `ResolveEntityInstance("gtfo.player", player)` 调用）只接受 `SNet_Player`，门槛与读回相同（已注册、Runtime Ready、会话未故障、`SNet.IsMaster`）。它按 SNet_Player 指针在已登记表里查找，再经上面的 `IsCurrent` 复核后返回该条目的引用；**只查不写**：不遍历 `PlayerAgentsInLevel`、不读 `Lookup`、不分配编号或 life。未登记、互链失效或门槛关闭都返回 null，由调用方决定不记录。
- 玩家候选来源（`EntityCandidates["gtfo.player"]`）是 `gtfo.player` 唯一的列举路径：`CurrentPlayers()` 里仍然成立的引用按 `id` 序数（`StringComparer.Ordinal`）排序交出，与模块内部字典的枚举顺序无关。模块没挂上或已经释放时按名抛 `player-module-unavailable`，内核据此报 `entity-candidates-failed`，**不返回空表**；真的没有玩家时才是空集。单次枚举上限 256 个候选，超限 `entity-query-budget` 且不截断。
- `forge.selector.target.players` 只走这一条列举路径：求值器用该步骤的 `RuntimeQuerySession.TryCandidates("gtfo.player")` 取集合，这次枚举与返回的引用计入同一份 query 预算（每 tick 64 次查询 / 1024 个引用），失败码原样交给步骤；它不再直接读 `PlayerIdentityModule.Current`，也没有第二个列举入口。`relation` 仍只回答 `ally`（`self`/`hostile`/`neutral`/`unknown` 以 `relation-unsupported` 拒绝），`empty` 仍只读取、由消费步骤施加策略。

证据文件 `evidence/map-hooks.json` 是本模块的 Hook 规格，布局审计逐条按它检查：5 个 Hook（2 个玩家读回 + 门、终端状态与终端命令 3 个 map-object 读回）的补丁目标签名与 virtual/static、每个 Hook 自己声明的 postfix 形参表、它读哪些形参、经哪个会话守卫发布、dump RVA（唯一且不共享、位于可执行段），读回成员的签名，7 条直接调用边。门与终端自己的成员登记、地址规则与游戏内确认清单在 `evidence/door-terminal-hooks.json`。

已知未核验：
- bot 的 `Lookup` 是否跨重生稳定。
- postfix 时刻 `PlayerAgentsInLevel` 是否已包含新 agent、是否已移除旧 agent（`OnDeSpawn` 先调用 `UnregisterPlayerAgent` 再调用 `OnPlayerDespawned` 只是静态调用边，运行时顺序未证）。
- 倒地与救起确实不重建 PlayerAgent。
- `Object.Destroy` 延迟销毁期间旧 agent 的可见状态。
- 迟加入玩家与主机迁移路径。

与 MAP1 的关系：`MapIdentitySession` 也登记 `forge.module.gtfo.map`，两者不能同进程并存；MAP1 原生适配器接线时必须合并为同一个 provider 生命周期。Weapon 的装备 owner 经上面的原生实例解析取得，Weapon 插件因此依赖 `NAinfini.ForgeMap`，但不引用 ForgeMap 程序集，见 [ForgeWeapon README](../ForgeWeapon/README.md#原生观察接线implementation-only)。

## 玩家生命治疗（implementation-only）

`forge.action.combat.heal` 是 canonical 能力，由 `forge.contract.combat` 声明；本包把自己的 binding 挂上去，和 ForgeEnemy 把同一能力挂到敌人接收器是同一件事，两边都不另造能力：

- 声明在 `PlayerHealthContract`：binding `forge.module.gtfo.map.binding.heal`、handler `gtfo.player.heal`、role `execute`、端口 shape `targets/source/amount/cap` + 结构参数 `overheal_policy`。只有 handler 在原生半边（`PlayerHealthAction`），所以声明和实现不会各说一套。`ModuleDefinition.Create()` 是"本程序集自答的那部分"声明，需要 handler 的行由实现它的半边经 `Create(bindings, support)` 追加——没有原生半边的 registration（`MapIdentitySession`、测试 fixture）不能声明它答不出的 binding，否则 runtime 按 `missing-handler` 拒绝整个 registration。
- 原生写入点只有一个：`Dam_PlayerDamageBase` 自己没有写健康的成员，绝对血量经基类 `Dam_SyncedDamageBase.SendSetHealth(System.Single)`（build 20403457，RVA 0x161F790）提交；游戏自己的治疗 `ReceiveAddHealth` 也是算完增量、clamp 到 `HealthMax` 后调同一个入口。该入口只认主机（非主机分支只上报不落地），并且玩家覆写的 `ReceiveSetHealth` 在 `Owner` 为空或 `Owner.Alive == false` 时**不写入**。证据、字段偏移与未核验项在 `evidence/player-health.json`，写行登记在 `evidence/map-hooks.json` 的 `writes`。
- `PlayerHealthReceiver` 按 `PlayerIdentityModule` 当前 life 解析实例（绝不由 ForgeEnemy 解析玩家），双读比对 owner/setup/alive/health/max 后给出一行结论：无当前 life → `stale-or-unsupported-recipient`；无接收器或未 setup → `missing-health-receiver`；owner 不符 → `health-receiver-owner-mismatch`；已死 → `not-alive`；血量非法 → `invalid-health-state`。提交前再次复核实例指针与状态，读回只接受"同一实例、上限不变、血量只升不超上限"，否则该行是 `unknown`（`receiver-changed-during-commit` / `unexpected-health-readback`），其后目标记 `not-attempted-after-unknown-commit`。
- 策略与 canonical 一致：`overheal` 整条拒绝（接收器的血量按 `HealthMax` 量化，没有超过上限的可表示值）；`discard` 逐目标跳过会溢出的目标（`would-overheal`）；`clamp` 收到 `min(cap, HealthMax)`；满血目标是**已提交的零变化**，不发虚假变化事实。结果行按 canonical 六列写：`target`、`status`、`committed`、`code`、`amount`、`target_count`，其中 `amount` 是读回的实际变化量，不是请求量。
- 快照的 `receives` 只在接收器可读且 agent 存活时发 `health.heal`：死玩家、无接收器的玩家不发，倒地玩家仍发（原生模型里倒地仍是 alive），但本实现只写血，不调用 `OnRevive`，因此不声称救起。玩家生命变化的触发（`forge.trigger.combat.health_changed`）本包**未**声明：它在敌人侧同时由原生伤害观察发布，只由本包治疗发布会把这个 canonical 触发说成只报自己造成的治疗。
- 未核验：以上全部来自元数据、静态调用边与方法体解码，没有启动游戏；主客机实际落地、迟加入重放、倒地状态机是否对血量写入有反应，以及哪些原生治疗工具走 `ReceiveAddHealth` 都未验证（见 `evidence/player-health.json` 的 `unverified` 与 `gameVerificationPlan`）。

## 门与终端的 map-object 地址（implementation-only）

门与终端在同一个 `gtfo.map_object` 实体命名空间里，`category` 是地址的一段，不是第二个命名空间。地址格式：

```
<category>/<dimension>/<layer>/<zone>/<key>
```

`category` 是 `MapObjectKind` 的小写名（`door`、`terminal`）。三个坐标是**对象所属区域**的原生值：`dimension` 取 `LG_Zone.m_dimensionIndex`，`layer` 取 `LG_Zone.m_layer.m_type`（主层/附加层/超载层 = 0/1/2），`zone` 取 `LG_Zone.LocalIndex`。每段都是十进制、无前导零；任何一段读不到就不产生地址，**不用 0 或 `?` 冒充**。

`key` 在区域内部命名对象。门的 key 是闭集词元 `security`：通往该区域的入口安全门——坐标因此是**被守卫区域**，一个区域至多一扇入口闸，所以不需要编号。终端的 key 是它在**所在区域** `TerminalsSpawnedInZone` 中的 0 起下标，单终端区域恒为 `0`。

实例身份是 `gtfo.map_object:<地址>`，由地址直接推导、可无表反解，换世界由内核 world epoch 整体失效。地址与解析都只走 `Native/ZoneIndex.cs` 这一张区域查找表：它按 world epoch 从 `LG_LevelBuilder.Current.m_currentFloor.allZones` 重建 `SpawnedDoor → LG_Zone` 与 `LG_Zone → 终端列表`，门和终端两个读取器都向它要坐标，不各走一套原生图。

不可寻址的对象每类只报一次原因并**不发布任何事实**：不是任何区域入口闸的门（弱门、节点门、装饰门）、bulkhead 层转换门、没有入口闸的出生区域、坐标读不到的区域里的门与终端、区域数据块声明了 `SpecificTerminalSpawnDatas` 的终端（该区域的列表不是它的全部放置顺序）、所属区域列表里没有它的终端（反应堆与任务终端）。旧的 `MapperDataID` / `SyncID` 地址写法已删除，不做兼容解析；`SyncID` 只保留为观测事实，用于把原生回调带的 id 换回终端实例。

网站导出与模组读回共用同一份向量 `Tests/Viewer/fixtures/map-object-address.json`（网站仓），模组不复制它：`MapObjectObservation` 的向量用例经 `FORGE_MAP_ADDRESS_VECTORS` 读该文件，逐行用 `native` 段调本模块的地址工厂并比对 `expected`，拒绝行必须在解析或等级匹配上失败；未提供路径时该用例失败并说明需要什么路径，不跳过。

**命中对象的解析**：子弹打到的碰撞体不是地图对象本身，而是门的门刀、门框、按钮或终端屏幕。`gtfo.map_object` 的实例解析器因此接受**命中路径唯一能观察到的对象**：`Native/MapObjectHit.cs` 用 `UnityEngine.Component.GetComponentInParent<LG_SecurityDoor>()` / `<LG_ComputerTerminal>()` 从它爬到该 kind 自己的原生实例，再由 `MapObjectModule.ResolveInstance` 走与直接交实例时**完全相同**的分类、寻址与拒绝路径——地址语法、`MapObjectRefusal` 的逐类原因、观测器与 `map-object` 挂载匹配全部不变。爬不到地图对象（世界几何、部署物、敌人/玩家伤害肢、没有门祖先的碰撞体）就返回 null，绝不合成地址；门在但不可寻址时，报的仍是门自己的那条拒绝原因。爬父级这一步是**唯一**新增的原生读取（`tests/MapNativeLayout` 的 `readSpellings` 已按"名字不以 `get_` 开头的纯查询"登记），且只在 `ForgeMap.Native` 里发生：`ForgeMap` 程序集不引用 Unity，只多了一个可选的对象解包委托。Weapon 侧因此只按 kind 名问一次，不认识门或终端的类型。

**未核验**：`zone.m_sourceGate.SpawnedDoor` 是否就是玩家进入该区域的入口闸、每个非出生区域是否恰好一扇；`TerminalsSpawnedInZone` 的顺序是否等于 `TerminalPlacements` 的顺序，以及反应堆/任务终端如何插入；`LG_LevelBuilder.Current.m_currentFloor.allZones` 在首次回调时是否已填满；以及子弹碰撞体是否真的挂在门/终端之下（门刀换父、终端屏幕碰撞体的层级）。四项都只有静态 interop 证据或根本没有运行期证据，确认清单在 `evidence/door-terminal-hooks.json` 的 `inGameVerificationPlan`。

## level 挂载与关卡身份（implementation-only）

`level` 挂载的 reference 是一个关卡的自身身份：

```
<rundown 块 persistentID>:<tier A-E>:<tier 内 0 基下标>
```

例 `31:A:0`。三段都是十进制、无前导零，tier 是一个大写字母；这是网站导出原版关卡 preset 已经在用的同一套拼写。带 rundown 块 id 是必须的：包里除被替换的那块之外保留整张原版表，只有 `A:0` 会在别的 rundown 的 A1 上误触发。

游戏侧的两个量都在 `Native/LevelIdentity.cs` 里读一次：`Globals.Global.RundownIdToLoad` 是本进程加载的 rundown 块，`RundownManager.GetActiveExpeditionData()` 的 `tier`（`eRundownTier`，A=1…E=5）与 `expeditionIndex`（0 基）是关卡在该块里的位置。游戏自己拼的 `ActiveExpeditionUniqueKey`（`Local_31_TierA_0`）只写进那一行日志，从不参与比较：它的 `Local_` 前缀是加载方式的细节，创作侧没有理由去预测它。tier 超出 A–E、下标为负、或读不到 expedition（未进关、成员读不出来）都答 `null`，匹配器按"这个世界的关卡身份读不到"处理。

比较规则：

- 解析在 `MapLevelReference.cs`（纯托管，无游戏引用）：三段严格解析，拼不出这个形状的 reference 不匹配任何关卡，并写一条有界诊断；**不会**退化成"匹配所有关卡"。
- `MapObjectModule` 注册 `level` 匹配器时用的是无主体（scope）形式：它只拿挂载目标与当前世界的关卡身份比对，因此在世界、计时、脉冲这些不带主体的事件上同样会被判到——这类事件正是整关触发的常见来源。
- 当前世界的身份在每个 world 里只读一次并缓存，下一次 `BeginWorld` 丢掉：一关只读一次原生成员，且一关的身份不会跨世界复用。
- 读不到身份与非法 reference 各自只报一次，都答 false：一关里没触发过的行为，可以从日志里区分是"没匹配上"还是"根本没比较"。

发行身份上，`Release/release.json` 给 ForgeMap 声明 `attachmentKinds: ["level", "map-object"]`，网站据此把带 `level` 挂载的计划解析到 ForgeMap 并写进依赖闭包。静态原生证据（成员签名、`ActiveExpeditionUniqueKey` 取值形态与既有游戏日志旁证）在 `evidence/level-identity.json`，读回签名登记在 `evidence/map-hooks.json` 并由布局审计按名核对。

**未核验**：本任务没有启动游戏。两关包里 A1 的行为是否真的不在 A2 派发、`Global.RundownIdToLoad` 在整场远征里是否恒定、以及同一 tier 内存在被禁用关卡时 `expeditionIndex` 的语义，都只有静态证据与既有日志旁证，确认清单在 `evidence/level-identity.json` 的 `unproven`。

## 远征结束触发（implementation-only）

`forge.trigger.session.expedition_ended` 是本 provider 唯一的会话级触发行，形状与网站授权目录该行逐字相同：`execution host`、domains `map/session/logic`、无输入、无参数，输出 `next`（execution）与 `outcome`（enum，schema `execution_outcome`）。它没有主体，所以挂在 `level` 上的计划由挂载目标判定，与门/终端事实走的是同一条派发路径的两条分支。

原生侧只有一个读回点：`RundownManager.OnExpeditionEnded(ExpeditionEndState)`（实例方法、非虚）。游戏在这次调用里就给出了结局本身（`Success`/`Fail`/`Abort`），所以事实取参数，不从游戏状态名、人数或卸载顺序反推。映射是通关 `succeeded`、团灭 `failed`、退出 `cancelled`；三种之外的值不发布，只报一次原因。签名与 `0x13E2100` 登记在 `evidence/map-hooks.json`，由布局审计按名与证据核对。

发布规则（`ExpeditionModule`，`MapObjectModule` 在同一条注册上组合它）：主机才发布（原生钩子与模块各自把关一次）；事件 id 是 `forge.trigger.session.expedition_ended:<world epoch>:<结局>`，所以同一世界同一结局的重复上报是内核的 `duplicate`（只派发一次），同一世界的另一个结局是它自己的事件，不同世界的同一结局是新事件。载荷只有 `outcome`，值是枚举成员下标（框架 enum 端口的线上形式），不是名字。

`forge.trigger.session.expedition_started` **未注册**：它的 `map` 输出是 resource 端口，而本运行时没有 resource 值——resource 只作为步骤输入上的编译期常量 `{id, revision}` 存在，触发器声明该端口会让整份计划以 `unsupported-event-port` 被拒（`evidence/expedition-trigger.json` 的 `runtimeResourcePorts` 逐条列出框架位置与实测）。目录行与 manifest 必须同形状，因此既不注册窄化版本，也不为 `map` 填占位值。

**未核验**：没有启动游戏。游戏对一次远征实际报几次结局、检查点重载是否再报、事件是否在关卡清理前进入派发，都只有静态证据与 interop/dump 元数据；确认清单在 `evidence/expedition-trigger.json` 的 `inGameVerificationPlan`。

## 边界

没有真实创建适配器、地图 Action、生成器、资源 Adapter 或玩家可用发行物；implementation-only 的原生钩子、对外 resolver 与原生实例解析器只有 MAP5a 的玩家身份，以及门/终端观察、level 挂载与远征结束触发（`gtfo.map_object` 的解析器、观察者、`map-object` 与 `level` 挂载匹配器，以及会话说的一半，均为只读）。`tests/fixtures/native-identity-scenarios.json` 的十个原生身份规格只执行了托管替身部分（MapIdentity 按用例 id 标记），原生部分仍然 `nativeExecuted: false`；报告里的 `nativeGameExecuted`、`nativeHooksInstalled` 和 `gameplayBindingsRegistered` 都是 false。没有原生创建、主客机、恢复或导航执行。

所有写世界的动作必须由明确的权威提交。查询与生成批次使用稳定排序、实际 seed、显式预算和完整性结果；缺少合法落点、导航证据或目标 receiver 时返回原因，**不静默换目标**。

Room 不另建独立运行包，资产引用使用统一资源协议。已有的世界与生成观察在 Runtime 与 Development 的诊断代码里，**诊断观察不是可调用的地图执行器**。

## 复跑

从仓库根运行，输出目录必须尚不存在且位于 `ForgeMap` 内：

```powershell
python ForgeMap/tools/run_identity_checks.py --out ForgeMap/evidence/identity-recheck-01
```

这个 runner 同时运行 MapIdentity、原有的 MapContracts 与 Architecture，构建产物隔离在 `ForgeMap/bin`，并保存命令、退出码、stdout/stderr、测试 JSON、DLL 哈希和源码摘要。构建失败不会退回去运行旧 DLL；检查期间相关源码发生变化时，最终结果不会标为通过。

只跑 SDK 消费方测试（xUnit，默认排除需要游戏程序集的 Native 用例）：

```powershell
$artifacts = Join-Path (Resolve-Path ForgeMap) 'bin/map1-artifacts'
dotnet test ForgeMap/tests/MapContracts/MapContracts.csproj -c Release --artifacts-path $artifacts --filter "Category!=Native"
```

MAP5a 原生玩家身份。`$bep` 是只读 BepInEx 目录，interop 身份只有一个冻结来源：`Forge-MapEditor-QA` profile 的 `BepInEx`（`evidence/map-hooks.json` 的 sha256 与 MVID 就是该副本的实际值）；`$game` 为 GTFO 根目录，`$dump` 为同 build 的 dump.cs；先构建宿主取得 `ForgeRuntime.dll`；MapNativeEvidence 与 MapNativeLayout 需要游戏程序集，用 `Category=Native` 过滤单独运行：

```powershell
$bep = "$env:APPDATA/r2modmanPlus-local/GTFO/profiles/Forge-MapEditor-QA/BepInEx"
$a = '<new-empty-artifacts-dir>'
$game = '<GTFO install>'
$dump = '<dump.cs>'
dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release --artifacts-path $a "-p:GTFOBepInExPath=$bep"
dotnet build ForgeMap/Native/ForgeMap.Native.csproj -c Release --artifacts-path $a "-p:GTFOBepInExPath=$bep" "-p:ForgeRuntimeAssembly=$a/bin/ForgeRuntime/release/ForgeRuntime.dll"
$env:FORGE_MAP_ADDRESS_VECTORS = '<site repo>/Tests/Viewer/fixtures/map-object-address.json'
dotnet test ForgeMap/tests/MapObjectObservation/MapObjectObservation.csproj -c Release --artifacts-path $a --filter "Category!=Native"
dotnet test ForgeMap/tests/MapNativeAdapter/MapNativeAdapter.csproj -c Release --artifacts-path $a --filter "Category!=Native"
$env:FORGE_ARTIFACTS = $a; $env:FORGE_MAP_BEPINEX = $bep; $env:FORGE_GTFO_GAME = $game; $env:FORGE_GTFO_DUMP = $dump
dotnet test ForgeMap/tests/MapNativeLayout/MapNativeLayout.csproj -c Release --artifacts-path $a --filter "Category=Native"
dotnet test ForgeMap/tests/MapNativeEvidence/MapNativeEvidence.csproj -c Release --artifacts-path $a --filter "Category=Native"
```

资源侧 Adapter 描述符 fixture（只读 JSON，不加载资源或游戏程序集）：

```powershell
python ForgeMap/tools/verify_resource_adapter_fixtures.py
```

原生证据的复采命令见 [VALIDATION.md](VALIDATION.md) 与 [GENERATION-SPEC.md](GENERATION-SPEC.md#5-fixture-与本地检查)。测试目录的边界说明见 [MapContracts](tests/MapContracts/README.md) 与 [MapIdentity](tests/MapIdentity/README.md)。

当前 DLL 不是玩家发行物；发布身份、真正加载、主客机和恢复都待实施验证。InfiniTweaks 的 QoL 行为不属于本模块。
