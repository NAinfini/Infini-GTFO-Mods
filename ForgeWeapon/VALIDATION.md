# ForgeWeapon 验证记录

**上次更新：2026-09-16**

## 装备池策略（`forge/loadout.json`）：复核、修一个真实缺陷、接线测试（2026-09-16）

**已实现并已测（托管替身，未启动游戏）**：装备池的两个 Hook 与策略读取此前由另一个 Agent 写好但没有任何报告，本轮逐条对照规格复核、修掉一处不符合、补上 `tests/NativeAdapter/LoadoutRuntimeTests.cs`。**没有启动游戏、没有安装 profile、没有改 Release 发布物**；策略文件在 `%TEMP%` 下的 fixture 安装里由生产读取器读入。

- **复核发现并修掉的缺陷：缓存的策略不随 rundown 变化失效**。原实现只在 `_policy == null` 时查表，一旦解析成功就永不重查，所以"`RundownIdToLoad` 变了就用快照恢复原版池"这条规格**从未发生**：进程换 rundown 后仍按旧策略收窄新池。现在会话记住它解析的是哪个 rundown（`_resolved`/`_rundown`），rundown 一变就重解，无策略即走 `Restore()`。用例 `a_rundown_with_no_policy_gets_the_games_own_pool_back` 先收窄、再换 rundown、断言三个池逐项回到游戏原内容，然后换回原 rundown 再收窄一次（策略与池一起恢复，说明状态确实被忘记）。
- **为可测性把"池"抽成一个最小接缝**：`GearLoadoutSession` 不再直接持有 IL2CPP 类型，而是 `IGearPoolSlot`（`Count`/索引器/`Clear`/`Add`）与 `GearPoolSlotSource(int slot)` 委托；interop 包装 `Native/Il2CppGearPoolSlot.cs` 与 `WeaponNativeSession.GuardNarrow` 是唯一知道 `GearManager.m_gearPerSlot` 形态的地方。投影侧同理：`WeaponNativeHooks.Items` 改读 `IList<GearIDRange>`（interop 数组本来就实现该接口），`GearLoadoutSession.Offer` 收 `IReadOnlyList<GearIDRange>`。**这不是兼容层**：池与数组仍然只有一份实现，收窄仍然写回游戏自己的列表实例，测试之所以能跑是因为接缝两侧都是同一个实现。
- **覆盖的场景（22 个用例）**：投影顺序与 `weapon.loadout-slot` 行逐字；非 1/2/3 槽原样返回；少一条策略项整槽回退（`weapon.loadout-policy-unmatched`）；一条都没命中也不产出空槽；再投影幂等；自制装备按 `GetCompID(Category)` 命中且保留原实例；块 id 只有一种拼法（`010001`/`+10001`/`10001.0`/尾随空格/大写前缀/`null` 全部不匹配）；身份读取抛异常时整槽回退并锁存会话（后续好列表也不再投影）；池里少一件策略项则三个槽全部留在原样；收窄原地重写游戏自己那三个列表实例；游戏还没建的槽使整池回退；工具栏 allow-list 同时容纳 7 件原版工具与自制 id；存档里的原版键（本机实际存 `OfflineGear_ID_34`/`_31`）在收窄后失配并回落到策略第一件，而策略自己带的键仍命中；`Drop`/`Dispose`/`GameStateManager.IsInExpedition` 三种门；诊断行不重复；**内容 pin** 用 fixture 的真实字节逐条比对（改一个被 pin 的源文件后该文件整份拒收、诊断码 `weapon.loadout-policy-source-mismatch`）；策略文件的 rundown 与当前加载不一致时不生效。
- **NativeLayout 审计同步扩展**：证据 `evidence/w7-loadout-policy.json` 现在带 `hooks[]`（两个 Hook 的类型、签名、RVA、`rvaSharing`、dispatch 与优先级），`tests/NativeLayout` 多读一份 `$env:FORGE_WEAPON_LOADOUT_SPEC`，并新增 `native.spec-hook-set-partitioned`、每 Hook 的 `dispatch`/`priority`/`unique-rva` 检查；`hook.<name>.instance-type` 改为"绑了 `__instance` 就必须是目标类型"，因为 `GearManager.GetAllGearForSlot` 是**静态**方法（绑不到实例）。读集合新增 8 条并新增写白名单 `native.pool-writes-exact`：本包唯一改的 native 状态就是池那三个列表的 `Clear`/`Add`，与表现层四个写、命令写一个分开列出。
- **明确未证**：策略能否真的在实机里生效、`m_gearPerSlot` 究竟由离线行还是账号实例填满、两条路径是否叠加，都是静态不可证项，已写进 `evidence/w7-loadout-policy.json` 的 `limits`；实机清单见该文件与 `%TEMP%\loadout-runtime-audit\plan.md` 第 8 节。

本轮实际运行的命令与结果（artifacts 目录 `%TEMP%\loadhook\art`；`GTFO_BEPINEX_PATH` 指向只读的 `Forge-MapEditor-QA` profile）：

| 命令 | 退出码 | 结果 |
| --- | --- | --- |
| `dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release --artifacts-path $a` | 0 | `Build succeeded`，0 警告 0 错误（只为给测试提供宿主程序集，未改宿主源码） |
| `dotnet build ForgeWeapon/Native/ForgeWeapon.Native.csproj -c Release --artifacts-path $a -p:ForgeRuntimeAssembly=…` | 0 | `Build succeeded`，0 警告 0 错误 |
| `dotnet test ForgeWeapon/tests/NativeAdapter -c Release --filter "FullyQualifiedName~LoadoutRuntimeTests" --artifacts-path $a -p:ForgeRuntimeAssembly=… -p:ForgeWeaponAssembly=… -p:ForgeRuntimeFrameworkAssembly=…` | 0 | `Passed! - Failed: 0, Passed: 22, Skipped: 0, Total: 22` |
| `dotnet test ForgeWeapon/tests/NativeLayout -c Release --artifacts-path $a`（带 `FORGE_ARTIFACTS`、`FORGE_WEAPON_HOOK_SPEC`、`FORGE_WEAPON_LOADOUT_SPEC`） | 0 | `Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1`（整份 layout 审计，含新增的两项） |
| 同工程 `NativeAdapter` 全量 | 1 | `Failed: 95, Passed: 20, Total: 115`：**装本包之前的既有用例**（`NativeAdapterTests`）全部失败在 `RuntimeContractException: forge.module.gtfo.weapon.binding.equipped`（`binding-lock`，fixture 计划把 provider 版本锁成 `1.0.0`，而并发的 combat-mark/identity 集成已把 `ForgeWeapon.csproj` 版本提到 `0.2.0`）；首个用例失败后 `WeaponNativeSession.Current` 未释放，其余 60 例连带报 `single-instance per process`。HEAD 基线对照：`git worktree add --detach %TEMP%\loadhook\baseline HEAD` 跑 HEAD 的 NativeAdapter（当时是 console 版）得 `PASS 44/44`，所以这是**集成未完成**而不是装备池改动造成的。 |
| `dotnet test ForgeWeapon/tests/LoadoutPolicy` | 未运行 | 本轮未改 `LoadoutPolicyData`/`GearLoadoutFilter` 的解析与过滤逻辑，只改了 `GearLoadoutSlotSnapshot<T>.Items` 的返回类型（`IReadOnlyList<T>` → `T[]`）；该套件应由统一验收重跑 |

## `deploy_completed.position`：部署完成位置（2026-09-16）

**已实现并已测（合成输入）**：`forge.trigger.equipment.deploy_completed` 多了一个 **`optional`** 的 `position`（`vector3`，单位 `m`）端口，取值是**这次 spawn 出来的世界实例自己的 transform 位置**。**没有启动游戏**；位置语义的结论来自只读的原生静态解码（`evidence/w4-deployable-hooks.json` 新增 `positionAtSpawn`），发布行为来自合成替身与 xUnit 断言。

- **位置只有一个写入者，且不是部署物自己**：全镜像 `UnityEngine.Transform::SetPositionAndRotation`（RVA `0x113CD40`）的全部直接调用点里，唯一给 gear 世界实例定位的是 `SNet_ReplicationManager_Gear::GetInstanceReplicator`（调用点 `0x140BCA3`，方法 RVA `0x140BA80`），顺序是 `GearIDRange..ctor` → `GearManager::AssembleGearAsync` → `Component::get_transform` → `Transform::SetPositionAndRotation` → `Component::get_gameObject`，写进去的就是 `pGearSpawnData`（结构体自带 `Vector3 position` @0x8、`Quaternion rotation` @0x14）。`SentryGunInstance` 与 `MineDeployerInstance` 的任何方法体都不含 transform 位置写入；`SentryGunFirstPerson::LateUpdate`（`0x1410F65`）与 `MineDeployerFirstPerson::LateUpdate`（`0x14FCAB7`）写的是手里的预览工具。**取证方法本身也记录在案**：dump 的 RVA 是 section 相对地址，必须按 `PointerToRawData + (rva - VirtualAddress)` 读字节并从 dump 自己的方法入口逐方法解码；从 section 头线性扫描会失步并给出零结果。
- **端口是 `optional`，不是必有也不是 `nullable`**：复制器工厂与同一个复制操作的 spawn 回调之间的顺序是结构性的（泛型复制器经接口槽派发，`SNet_ReplicationManager<TSpawnData,TReplicator>.Spawn` 在 `0x1D8CCE0` 两次经 `[r9]` 间接调用，从未字面点名 `GetInstanceReplicator`），没有可执行证据。因此读不到有限世界坐标时端口**整条不出现**，事实照发；绝不把 `deploy_requested` 的请求坐标当成完成位置。
- **发布路径收敛到一个私有读取器**：`EquipmentNativeAdapter.Position(Item)` 返回 `double[]?`（destroyed 对象经 Unity 相等读成 null、transform 缺失、任一坐标非有限都返回 null），`ObserveEquipment`、`ObserveDeployable` 与 `Placed` 三处共用它，所以一份位置在这个包里不可能有第二种拼法。`Placed` 只在该读取器给出有限值时加上 `position`。
- **新增 3 个用例**：`deploy_completed_reports_the_world_instances_own_position`（背包装配路径报出实例自己的坐标 `12.5/-3/0.25`）、`deploy_completed_from_the_spawn_body_reads_that_instance`（`OnSpawn` 路径报出同一实例的 `0/-120/4`）、`deploy_completed_without_a_readable_world_position_omits_the_port`（transform 读不到时事实照发、端口整条不出现、无报告）。两个既有的 `Fact(...)` 断言同步加上 `position` 端口。
- **网站侧同批对齐**：`Tools/Forge/capability-rules-trigger.ts` 的 `equipment.deploy_completed` 改为 `position:vec3?@m`（可选标记写在单位前，DSL 先解析 `@unit`）。

本轮实际运行的命令与结果（artifacts 目录 `%TEMP%\deploy-position-mod`；`GTFO_BEPINEX_PATH` 指向只读的 `Forge-MapEditor-QA` profile）：

| 命令 | 退出码 | 结果 |
| --- | --- | --- |
| `dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release --artifacts-path $a -p:GTFOBepInExPath=$bep` | 0 | `Build succeeded`，0 错误（只为给测试提供宿主程序集，未改 ForgeRuntime 源码） |
| `dotnet build ForgeWeapon/ForgeWeapon.csproj -c Release --artifacts-path $a` | 0 | `Build succeeded`，0 错误 |
| `dotnet test ForgeWeapon/tests/NativeAdapter -c Release --filter "Category!=Native" --artifacts-path $a -p:GTFOBepInExPath=$bep -p:ForgeRuntimeAssembly=$a\bin\ForgeRuntime\release\ForgeRuntime.dll` | 0 | `Passed! - Failed: 0, Passed: 95, Skipped: 0, Total: 95`（本轮新增 3 个用例；运行时刻 Framework 源码仍可编译，此后被并行任务改到编译不过） |
| 复跑（改用已构建的宿主/Framework 程序集做引用：`-p:ForgeWeaponAssembly=… -p:ForgeRuntimeFrameworkAssembly=… -p:ForgeRuntimeAssembly=…`） | 0 | `Passed! - Failed: 0, Passed: 95, Skipped: 0, Total: 95`，与本轮最终工作树一致 |
| `dotnet test ForgeWeapon/tests/Identity` / `IdentityAcceptance` / `IdentityDispatchReview` | 未运行 | 同一工作树里 `ForgeRuntime/Framework` 正被并行任务改写，`RuntimeGraphContracts.cs` 报 `CS0117 ValueKind.Result/Resource/Event`、`RuntimeFrames.ResourceWidth`，`Frame/Frames.cs` 报 `CS1519`，Framework 当前编译不过；这三套件依赖它，稍后由统一验收重跑 |
| `dotnet build ForgeWeapon/Native/ForgeWeapon.Native.csproj` | 未运行 | 同上：Native 项目引用宿主 `ForgeRuntime.dll` 与 Framework，Framework 修好后应重跑 |

未在游戏内核验的点：复制器工厂是否真的早于同一个复制操作的 spawn 回调（这条只能靠游戏内一次部署确认，读了位置不对就说明顺序相反，此时端口仍然按 optional 语义工作、不会报出请求坐标）；部署物是否会被 `GroundOffset` 或别的机制在 spawn 之后挪动；`pGearSpawnData.position` 与实际落地点的偏差（`GetGroundOffset` 在部署后是否参与修正）。

## 命中候选的第三条腿：`gtfo.map_object`（2026-09-16）

**已实现并已测（合成输入）**：`hit_candidate.target` 现在也能把门与终端的命中解析成 Forge 实体。**没有启动游戏**，结论来自合成替身、xUnit 断言与编译后程序集的元数据审计；`GetComponentInParent` 能否真的从子弹碰撞体爬到门，属运行期未证项，已写进 `ForgeMap/evidence/door-terminal-hooks.json` 的 `unproven` 与游戏内验证计划。

- **Weapon 只加一条腿，不认门的类型**：`EquipmentNativeAdapter.HitTarget` 在敌人腿与玩家腿之后调用 `_kernel.ResolveEntityInstance(MapObjectKind = "gtfo.map_object", collider)`，传入的就是 `data.rayHit.collider`。Weapon 不引用 ForgeMap 程序集、不新增任何游戏成员读取——`tests/NativeLayout` 的 `native.exact-game-reads` 逐字表**未改一行**且该项通过，这就是"门的知识留在拥有者一侧"的可断言证据。kind 未登记（ForgeMap 未加载）与解析不出引用都是 null，端口整条不出现，`optional` 语义不变。
- **爬父级只发生在 ForgeMap**：新增 `ForgeMap/Native/MapObjectHit.cs`（唯一新类型），用 `UnityEngine.Component.GetComponentInParent<LG_SecurityDoor>()` / `<LG_ComputerTerminal>()` 从命中对象爬到该 kind 自己的原生实例；`MapObjectModule.ResolveInstance` 先做这一步解包，再走**原有**的 `Source(instance)`/`TryAddress`/`EntityId` 路径，所以地址语法、拒绝原因与"每类原因只报一次"全部沿用。爬不到就返回 null，绝不合成引用。`ForgeMap`（无 Unity 引用的事实源程序集）只多了一个可选的 `Func<object, object?>` 构造参数，Unity 类型不出现在该程序集里。
- **ForgeMap 侧新增 3 个用例**（`tests/MapNativeAdapter/MapObjectHitTests.cs`）：门/终端命中对象解析到既有地址（含跨两层祖先）、没有地图对象祖先的命中对象（世界几何、伤害肢）返回 null、非入口门与 bulkhead 门的命中对象返回 null 且每类原因各一条诊断。`SyntheticLevel` 从 `MapNativeAdapterTests` 的私有嵌套类提为独立文件并改为显式接收 kernel，两个测试类共用同一份层级夹具。
- **顺带修掉一个被本轮测试暴露的既有隐患**：`ZoneIndex.Current` 原先把"无 world（`epoch == null`）"读到的那张表也缓存进 `Cached`，下一个 world 用同一个 `null` 键就能命中它——测试里表现为"上一个关卡的门地址回答了这一个关卡"。现在 `epoch == null` 时不缓存（"没有 world"不是 world，每次重建是空表的廉价读），并在方法注释里写明原因。

本轮实际运行的命令与结果（artifacts 目录 `%TEMP%\hitobj\artifacts`；`GTFO_BEPINEX_PATH` 指向只读的 `Forge-MapEditor-QA` profile）：

| 命令 | 退出码 | 结果 |
| --- | --- | --- |
| `dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release --artifacts-path $a` | 0 | `Build succeeded`，0 警告 0 错误（只为给 Native 项目提供宿主程序集，未改 ForgeRuntime 源码） |
| `dotnet build ForgeWeapon/ForgeWeapon.csproj -c Release --artifacts-path $a` | 0 | `Build succeeded`，0 警告 0 错误 |
| `dotnet build ForgeWeapon/Native/ForgeWeapon.Native.csproj -c Release --artifacts-path $a -p:ForgeRuntimeAssembly=…` | 0 | `Build succeeded`，0 警告 0 错误 |
| `dotnet build ForgeMap/ForgeMap.csproj -c Release --artifacts-path $a` | 0 | `Build succeeded`，0 警告 0 错误 |
| `dotnet build ForgeMap/Native/ForgeMap.Native.csproj -c Release --artifacts-path $a -p:ForgeRuntimeAssembly=…` | 0 | `Build succeeded`，0 警告 0 错误 |
| `dotnet test ForgeWeapon/tests/NativeAdapter -c Release --artifacts-path $a -p:ForgeRuntimeAssembly=…` | 0 | `Failed: 0, Passed: 92`（本轮新增 2 个用例：地图对象命中解析、四类目标的 equipment 端口不变） |
| `dotnet test ForgeMap/tests/MapNativeAdapter -c Release --artifacts-path $a` | 0 | `Failed: 0, Passed: 84`（本轮新增 3 个用例；同工作区并行任务 map-a1-native 的用例也在其中） |
| `dotnet test ForgeMap/tests/MapObjectObservation -c Release --artifacts-path $a`（另设 `FORGE_MAP_ADDRESS_VECTORS`） | 0 | `Failed: 0, Passed: 98` |
| `dotnet test ForgeWeapon/tests/NativeLayout -c Release --artifacts-path $a`（`FORGE_ARTIFACTS`/`FORGE_WEAPON_BEPINEX`/`FORGE_WEAPON_HOOK_SPEC` 绝对路径） | 0 | `Failed: 0, Passed: 1`（`native.exact-game-reads` 逐字表未改，内部检查全过） |
| `dotnet test ForgeMap/tests/MapNativeLayout -c Release --artifacts-path $a` | 1 | 本轮新增的 `UnityEngine.Component::GetComponentInParent` 已按"非 getter 的纯查询"登记白名单；**剩余 5 项红属于并行任务 map-a1-native 刚加入的门/终端动作成员（尚未补证据行）**，与本轮改动无关 |

未在游戏内核验的点：子弹碰撞体是否真的挂在门/终端之下（`GetComponentInParent` 的实际层级与返回顺序）；`LG_DoorBladeCuller` 之类是否会在运行期换父；终端屏幕碰撞体是否在 `LG_ComputerTerminal` 之下；弱门与区域入口闸是弱门时的实际比例。接线对每一种情况都取"爬不到就不发 target 端口"的实现，不会因为读不到而伪造引用。

## 装备部件姿态（`forge/gear-parts/<blockId>.json`）与 xUnit 套件的按 case 所有权（2026-09-16）

**已实现并已测（合成输入）**：包可以按装备块写一份部件姿态文件，插件启动时读成不可变快照，`Gear.GearPartHolder.OnAllPartsSpawned` 的 `Priority.Last` postfix 把绝对本地位置/欧拉角/缩放与显示与否写到该块的部件与子物体上。**没有启动游戏**：原生挂点与读写集来自 `dump.cs` + `GameAssembly.dll` + interop 元数据的只读分析（新证据文件 `evidence/w6-gear-parts.json`），行为结论来自合成替身与 xUnit。

- **数据格式与拒绝口径**：`BepInEx/plugins/<包的插件目录>/forge/gear-parts/<blockId>.json`，包目录规则与计划发现相同（`plugins/` 下**一层**目录，不递归；`PlanDiscovery` 同一写法）；文件名的 blockId 与内容里的 `blockId` 必须都是规范十进制（`uint` 且文本等于自身 `ToString`）且逐字相等。字段 `component`（`eGearComponent` 成员名）、可选 `partId`、`enabled`、`localPosition`、`localEulerAngles`、`localScale`、`children[{path, 同样字段}]`。上限逐条实现：单文件 256 KiB（在读之前按长度拒绝）、每块 ≤ 64 项、位置每轴 |v| ≤ 1 m、缩放每轴 ∈ [0.05, 4]、欧拉角规范到 [0, 360)、子物体路径按**从部件起算的完整路径**计段（嵌套的父段计入，所以 4+4=8 通过、5+4=9 拒绝）≤ 8 段且每段必须是干净相对名。**一个块只有一份文件**：两份或更多文件声称同一个块时该块**整体不加载**（`gear-part-duplicate-block`），不保留第一份、不合并、不按扫描顺序决胜，每个声称的文件各写一次。坏文件按文件粒度拒绝，每份写一次 error 行 `weapon.gear-part-<code> file=<相对路径>: <原因>`，其余文件照常加载。
- **应用**：块 id 经 `EquipmentNativeAdapter.GearBlockId(holder.GearIDRange)` 取——**与 `gear-block` 挂载匹配器是同一个函数**，没有第二份解析；槽位到 holder 字段是显式 `switch` 表（21 个槽位，无反射字符串）；写的是**绝对值**，因此同一 holder 再出生、图标再渲染、检查点重载都写同一组数，天然幂等；`partId` 不符时整条不应用并写一次 `weapon.gear-part-mismatch`；子物体按相对路径用游戏自己的 `Transform.Find` 查找，**嵌套的 `children` 整棵树都会应用**（每个节点用它从部件起算的完整路径解析，与加载器记下的是同一条路径），缺失写一次 `weapon.gear-part-child-missing` 并继续其它条目（缺失节点自己的子树随之跳过，它下面不可能解析得到）；应用成功每个 holder 写一条 `weapon.gear-part-applied block=<id> parts=<n>`；文件里没写的字段一个都不写。
- **写集白名单**：`tests/NativeLayout` 新增 `presentationWrites`（恰好四个成员：`UnityEngine.Transform::set_localPosition`、`set_localEulerAngles`、`set_localScale`、`UnityEngine.GameObject::SetActive`）与检查 `native.presentation-writes-exact`；`native.read-only-game-access` 仍然对**任何其它**非 getter 原生成员报错，`native.exact-game-reads` 的钉子表同步补上本次新增的读取成员。`Find`、`Vector3::.ctor` 记入纯查询（前者是 Unity 自己的层级查找，后者是值类型构造，都不改任何活过调用的状态）。
- **修正了一处会静默摆错槽位的缺陷**：`GearPartSlot` 里 `ToolMainPartAttachment` 与 `ToolGripPart` 的数值原来是 30/29，与 `Modules-ASM` 的 `eGearComponent` 相反（原生是 `ToolMainPartAttachment = 29`、`ToolGripPart = 30`），而 `GearIDRange.GetCompID` 收的正是这个数。已按原生元数据改正生产枚举与测试替身，并新增 `native.gear-part-slots-exact`：21 个槽位逐个与 `Modules-ASM` 的 `eGearComponent` 常量比对（做负向验证：把 `MeleePommelPart` 临时改成 51，该检查逐字报 `MeleePommelPart=51 vs 50`，随后还原）。
- **xUnit 套件的真正根因与修法**：整类合跑时 `Failed: 76` 的首条是 `World.Cleanup` → `RuntimeModuleHandle.Dispose` 抛 `Runtime APIs must run on the owning simulation thread`。根因不是"跨线程清理"本身，而是**测试进程内的两个测试类被并行执行**：`NativeAdapterTests` 与 `GearPartTransformTests` 各自在同一个进程里抢 `WeaponNativeSession.Current`、`Host.Runtime`、`Harmony` 计数与替身的背包表，于是失败像滚雪球一样扩散（单跑任一类都全过）。`NativeAdapter/AssemblyAttributes.cs` 里的 `[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]` 在本 runner 组合下**没有生效**；改为在 `tests/NativeAdapter/` 放 `xunit.runner.json`（`parallelizeTestCollections: false`，随构建复制到输出目录）后，同一批用例整类合跑全过（当轮 87 个；本轮新增部件用例后为 90）。
- **测试夹具改为按 case 所有权**：`World` 不再把自己登记进静态 `Live` 列表由 `Cleanup()` 统一拆，而是每个 case 自己 `using var` 持有并在本线程释放（与 `tests/Identity`、`tests/IdentityDispatchReview` 的既有习惯一致）；`World.Cleanup()` 删除，替身里的进程级状态另设 `World.Reset()`（`Harmony` 计数、宿主模式/运行时/游戏门槛、`SNet.IsMaster`），语义与原来一致但不碰别的 case 的对象。`tests/NativeAdapter` 的全部用例都按这条规则改写所有权（当轮 87 个，本轮新增部件用例后 90 个）。
- **`tests/NativeAdapter` 的部件用例**：24 个，覆盖规格要求的七类（合法文件被应用、超限被拒绝、文件名与内容 id 不符、`partId` 不符、子物体缺失、未配置的块保持不动、重复触发结果幂等），另加边界与负向：值恰好落在上限上被接受、单文件过大按长度拒绝、文件名不是块 id、`partId` 读回为 0、每个缺失子路径各自诊断一次、空槽位跳过、五种视图各摆一次、包目录之外的文件不读、路径越界/超深、未定义组件、坏 JSON 单独拒绝、无可读块记录的 holder 不动；本轮补上三项：**多层嵌套子树整棵应用**（三层各有自己的字段）、**完整路径计段的 8 通过 / 9 拒绝**（4+4 与 5+4、1+8）、**同块两份与三份都整体不加载**（每个声称文件各一条诊断，第一份也不再保留）。夹具的 `Transform.Find` 同时改成按 `/` 逐段下降（与 Unity 的层级查找同形），这样嵌套用例走的是真实解析形状。
- **宿主 Off 的实际效果**：`Plugin.Load` 在 `HostPlugin.ConfiguredMode == Off` 时于任何注册与原生动作之前返回，所以宿主 Off 时本包**既不读部件文件也不装这个 Hook**，没有任何部件会被摆位；本包没有自己的开关，这与其它原生 Hook 的规则一致（`weapon.gear-part-blocks-loaded` 也不会出现）。`plugin_off` 用例断言 `Harmony.Patches == 0`、会话为 null、清单未变。

本轮实际运行的命令与结果（artifacts 目录 `%TEMP%\gpca\artifacts`；`GTFO_BEPINEX_PATH` 指向只读的 `Forge-MapEditor-QA` profile，Native 类测试另用 `%TEMP%\gpca\bep`：`GameAssembly.dll` 副本 sha256 `C6A5C3CD…BF55` + 指向 QA profile 的 `core`/`interop`/`unity-libs` junction）：

| 命令 | 退出码 | 结果 |
| --- | --- | --- |
| `dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release --artifacts-path $a` | 0 | `Build succeeded`，0 警告 0 错误（只为给 Native 项目提供宿主程序集，未改 ForgeRuntime 源码） |
| `dotnet build ForgeWeapon/ForgeWeapon.csproj -c Release --artifacts-path $a` | 0 | `Build succeeded`，0 警告 0 错误 |
| `dotnet build ForgeWeapon/Native/ForgeWeapon.Native.csproj -c Release --artifacts-path $a -p:ForgeRuntimeAssembly=…` | 0 | `Build succeeded`，0 警告 0 错误（Hook 20 → 21） |
| `dotnet build ForgeMap/Native/ForgeMap.Native.csproj -c Release --artifacts-path $a -p:ForgeRuntimeAssembly=…` | 0 | `Build succeeded`，0 警告 0 错误（NativeLayout 的输入之一，未改 ForgeMap 源码） |
| `dotnet test ForgeWeapon/tests/NativeAdapter --filter "Category!=Native" -p:ForgeRuntimeAssembly=…` | 0 | `Failed: 0, Passed: 90`（基线 66 + 24 个部件用例；整类合跑，不再需要拆开跑） |
| `dotnet test ForgeWeapon/tests/Identity --filter "Category!=Native"` | 0 | `Failed: 0, Passed: 46` |
| `dotnet test ForgeWeapon/tests/IdentityAcceptance --filter "Category!=Native"` | 0 | `Failed: 0, Passed: 37` |
| `dotnet test ForgeWeapon/tests/IdentityDispatchReview --filter "Category!=Native"` | 0 | `Failed: 0, Passed: 20` |
| `dotnet test ForgeWeapon/tests/NativeEvidence --filter "Category=Native"`（staging BepInEx） | 0 | `Failed: 0, Passed: 1`（内部静态证据检查全过） |
| `dotnet test ForgeWeapon/tests/NativeLayout --filter "Category=Native"`（`FORGE_ARTIFACTS`/`FORGE_WEAPON_BEPINEX`/`FORGE_WEAPON_HOOK_SPEC` 绝对路径） | 0 | `Failed: 0, Passed: 1`（含新增的 `native.presentation-writes-exact`、`native.gear-part-slots-exact`） |
| `pwsh -NoProfile -File Release/check-identity.ps1` | 0 | `核对 83 项：83 通过，0 失败`（只读运行，未改 `Release/`） |

未在游戏内核验的点（已写入 `evidence/w6-gear-parts.json` 的 `limits` 与 README）：`OnAllPartsSpawned` 的派发点没有解出来（它的地址只在 `.data` 的 method-pointer 表里，没有字面 `E8` 调用方），所以"这个方法体可达"是证据，"它在实机跑过"不是；`GearPartHolder` 自己还有 `Update`（dump.cs 第 614843 行）没有被反编译，原动画是否会在写之后把部件拉回原位属未知；1 m 与 [0.05, 4] 是裁定给作者的界限，不是原生限制。游戏内核验清单补了第 13–16 步（五种视图各看一次、检查点重载、原动画是否覆盖、与同类部件模组同装）。

## 命中候选携带装备端口（2026-09-16）

**已实现并已测（合成输入）**：`forge.trigger.combat.hit_candidate` 的注册图新增 `equipment:entity` 输出端口，发布值取自已开着的开火窗口。**没有启动游戏**：结论来自合成替身与 xUnit，以及导出运行清单与网站目录的逐字段比对。

- **端口是必有**：命中候选只在开火窗口内产生（`WeaponCombatObserver.Hit` 在没有开着的 `Shot` 时写 `weapon.hit-outside-shot` 后直接返回，不发布事实），所以"开这一发的那条装备生命"一定可观察，端口按**必有**注册，不带 `?`。它与同一发 `shot_committed` 的 `equipment` 是**同一个引用**（同一个 `Shot.Equipment`，同一条 `gtfo.weapon.shot:<装备生命>:<序号>` scope），类型同为 `entity`，因此挂在那个装备块上的行为能认领自己武器的命中。
- **取值来源**：开火时 `WeaponCombatObserver.Open` 把 `EquipmentNativeAdapter.RecordShot` 返回的装备生命存进窗口（`Shot(EntityReference Equipment, long Index)`），命中候选直接读这个字段。不另起一次装备查找，也不从命中对象反推装备——命中对象的原生类型只决定 `target`。
- **日志**：`weapon.hit-fact equipment=… index=… target=… status=… code=…` 仍按每个候选一行写，装备生命就在这一行里；命中候选的日志行与开火事实的日志行因此可以逐发对上。
- **测试**：`tests/NativeAdapter` 新增 `combat_hit_candidate_carries_the_equipment_of_its_own_shot`（候选的 `equipment` 与同一发 `shot_committed` 的 `equipment` 逐字相同，两条事实的 scope 指向同一条生命）与 `combat_hit_after_a_weapon_swap_carries_the_new_equipment_life`（同一玩家换枪后再开火，新窗口的命中带新武器的生命，不再带换枪前的那条）。

本轮实际运行的命令与结果（artifacts 目录 `%TEMP%\hitequip\artifacts`，`GTFO_BEPINEX_PATH` 指向只读的 `Forge-MapEditor-QA` profile）：

| 命令 | 退出码 | 结果 |
| --- | --- | --- |
| `dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release --artifacts-path $a` | 0 | `Build succeeded`，0 警告 0 错误 |
| `dotnet build ForgeWeapon/ForgeWeapon.csproj -c Release --artifacts-path $a` | 0 | `Build succeeded`，0 警告 0 错误 |
| `dotnet build ForgeWeapon/Native/ForgeWeapon.Native.csproj -c Release --artifacts-path $a -p:ForgeRuntimeAssembly=…` | 0 | `Build succeeded`，0 警告 0 错误 |
| `dotnet test ForgeWeapon/tests/NativeAdapter --filter "Category!=Native" -p:ForgeRuntimeAssembly=…` | 1 | 整类合跑 `Failed: 76, Passed: 3, Total: 79`：失败首条是 `GearPartTransformTests.repeated_spawns_are_idempotent` 的 `World.Cleanup` → `RuntimeModuleHandle.Dispose` 抛 `Runtime APIs must run on the owning simulation thread`，两个测试类同进程串跑时的静态状态冲突（同工作区部件变换任务的在途状态，本任务未改那两个文件）；单独跑任一测试类都通过，见下两行 |
| `dotnet test ForgeWeapon/tests/NativeAdapter --filter "FullyQualifiedName~NativeAdapterTests"` | 0 | `Failed: 0, Passed: 66`（含本任务新增的 2 个用例） |
| `dotnet test ForgeWeapon/tests/NativeAdapter --filter "FullyQualifiedName~combat_"` | 0 | `Failed: 0, Passed: 10` |
| `dotnet test ForgeWeapon/tests/NativeAdapter --filter "FullyQualifiedName~GearPartTransformTests"` | 0 | `Failed: 0, Passed: 13`（单独跑通过） |
| `dotnet build Release/export-runtime-manifest/export-runtime-manifest.csproj -c Release --artifacts-path $a`，再运行导出的可执行文件写 `%TEMP%\hitequip\runtime-manifest.json` | 0 | `Wrote …runtime-manifest.json: 5 player packages, runtime 1.2.0 on game build 20403457` |

导出的运行清单里该行 outputs 为 `next / source / equipment / target(optional) / limb(nullable) / position(m)`，与网站目录 `catalog/capability-catalog.json` 的同名行**逐字段一致**：端口数、顺序、`type`、`unit`、`optional`、`nullable` 全部相等，`inputs` 两侧都是空、`execution` 两侧都是 `host`、`domains` 两侧相同。

## Slot 151 四个 `Fire` 体全部接线、玩家路径与客机重放的静态结论（2026-09-16）

**已实现并已测（合成输入）**：`BulletWeaponSynced.Fire` 与 `ShotgunSynced.Fire` 各补一个 `Priority.Last` 的 postfix，`Fire` 钩子从 2 个变成 4 个（`WeaponNativeHooks` 20 个 Hook）。**没有启动游戏**：调用结构与"谁持有哪种武器"来自 `dump.cs` + `GameAssembly.dll` 的只读静态分析（`evidence/w4-player-fire.json`），行为结论来自合成替身与 xUnit，运行期是否真的按这条链跑仍待游戏内确认。

- **四个 `Fire` 体互不调用（否定证据）**：对每个体的**整个 slot 范围**做线性扫描（不是"遇到第一个 terminator 就停"），没有任何一个体分支到另外三个 RVA；`RifleWeapon` 与 `RifleWeaponSynced` 各自只声明构造函数（后者另有 `PlayRecoilAnim`，Slot 153），因此它们用各自家族的 `Fire` 体。于是"一个 `Fire` 体一次调用＝恰好一条 `shot_committed`"天然成立：不加调用结构去重，也不会一发记两次。
- **玩家路径的划分**：未同步的两个体（`BulletWeapon.Fire` 0x145FB30、`Shotgun.Fire` 0x13C3020）都调用 `PlayerSync.RegisterFiredBullets`，即**射击者自己**的那把武器；同步的两个体（`BulletWeaponSynced.Fire` 0x145D430、`ShotgunSynced.Fire` 0x13C2580）读 `PlayerAgent.get_Owner` 与 `SNet_Player.get_IsBot`、跑 `WhizByTest` 与单发音效、**从不登记射击**，即"本机持有的、别人的武器副本"。两个同步类还把 `IsFirstPerson`（Slot 101）重写成恒 `false` 的共享体（0x380300），而 `ItemEquippable.get_IsFirstPerson` 读实例自己的 0x170 字节——本地玩家手持的武器因此不是同步类。
- **A4：客机开火在 host 上会进 `Fire` 体**（静态）。数据侧：客户端自己的未同步体把这一发交给 `PlayerSync.RegisterFiredBullets`，`pPlayerLocomotion.FireCount`（结构体 0x15）随移动同步复制，`PlayerSync.IncomingLocomotion` 把它写进 `FireCountSync`（0x7C）并调用 `FireCountCallBack`。应用侧：整张镜像里 `Slot 128` 只有一个虚调用点——`PlayerInventorySynced.GetSync`，它读 `PlayerAgent.Sync.FireCountSync`、把它交给手持武器的 `OnSyncFire` 并把该字段清零；`OnSyncFire` 转发给 `Setup` 安装的 `OnSyncCall` 委托，`OnSyncFireRegular`/`OnSyncFireBurst` 把它累加到 `m_shotsToFire`（0x354），每帧 `Update` → `UpdateRegular` 在射速允许时按 Slot 151 虚调用一次 `Fire` 并 `dec` 一次。host 上"客户端"就是非本地玩家、其武器就是同步副本，所以 **host 会为客机的每一发执行一次同步 `Fire` 体**；客机上对 host 与其他客机的武器同理，只是非 master 不发布。
- **哨戒不需要新体、也不会被误记**：`BulletWeapon.BulletHit` 是静态方法，它的直接调用方只有四个 `Fire` 体加 `SentryGunInstance_Firing_Bullets.FireBullet`（0x1414370）与 `UpdateFireShotgunSemi`（0x1416B70）。哨戒自己的开火组件走的是自己那条路，射线到达观察者时没有开火窗口，写 `weapon.hit-outside-shot` 且不发事实（新增用例显式覆盖，且按 `BulletHit` 的真实形状传 `__instance = null`）。已知口径保留：命中只认领**最近一次**开火，所以玩家刚开过火时哨戒的射线会记到那名玩家仍开着的窗口上。
- **对账能拿到远端玩家的装备生命与 owner**：`Reconcile(PlayerBackpack)` 对**任何**玩家的背包按 `backpack.Owner` 求 `gtfo.player` 引用（不区分本地/远端），`OwnerOf(Item)` 走 `instance.Owner.Owner` 再问同一个解析器。新用例用一个 `synced: true` 的玩家（`PlayerInventorySynced`）持 `RifleWeaponSynced` / `ShotgunSynced` 验证：一发一条事实、`equipment` 是那条生命、`source` 是该玩家的引用、`index` 逐发 +1。
- **证据与测试登记**：新增 `evidence/w4-player-fire.json`（四个体的签名/Slot/RVA 唯一性/整段范围的直接调用/否定证据、`OnSyncFire` 到 `Fire` 的重放链与逐条汇编、Slot 151 与 Slot 128 的全部虚调用点、静态调用者集合、5 条运行期未证项）；`evidence/w4-combat-hooks.json` 的 `client-shot-reaches-host` 与 `bot-and-ai-holders` 两条 `unverified` 补上该文件已经落实的部分与仍然未证的部分；`tests/NativeLayout` 新增 `native.one-patch-per-fire-body`（四个 `Fire` 钩子必须声明**四个互不相同**的游戏方法），`tests/NativeAdapter` 新增 2 个用例并修正一个用例的注释（它实际验证的是"武器没有 owner 时这一发没有开火窗口"）。

本轮实际运行的命令与结果（artifacts 目录 `%TEMP%\wpnfire\artifacts`；`GTFO_BEPINEX_PATH` 指向只读的 `Forge-MapEditor-QA` profile，Native 类测试另用 `%TEMP%\wpnfire\bep`：`GameAssembly.dll` 副本 sha256 `C6A5C3CD…BF55` + 指向 QA profile 的 `core`/`interop` junction）：

| 命令 | 退出码 | 结果 |
| --- | --- | --- |
| `dotnet build ForgeRuntime/Framework/ForgeRuntime.Framework.csproj -c Release --artifacts-path $a` | 0 | `Build succeeded`，0 警告 0 错误（Native 测试的输入之一，未改源码） |
| `dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release --artifacts-path $a` | 0 | `Build succeeded`，0 警告 0 错误 |
| `dotnet build ForgeWeapon/ForgeWeapon.csproj -c Release --artifacts-path $a` | 0 | `Build succeeded`，0 警告 0 错误 |
| `dotnet build ForgeWeapon/Native/ForgeWeapon.Native.csproj -c Release --artifacts-path $a -p:ForgeRuntimeAssembly=…` | 0 | `Build succeeded`，0 警告 0 错误（新增两个 `Fire` 钩子） |
| `dotnet build ForgeMap/Native/ForgeMap.Native.csproj -c Release --artifacts-path $a -p:ForgeRuntimeAssembly=…` | 0 | `Build succeeded`，0 警告 0 错误（NativeLayout 的输入之一，未改 ForgeMap 源码） |
| `dotnet test ForgeWeapon/tests/NativeAdapter --filter "Category!=Native" -p:ForgeRuntimeAssembly=… -p:ForgeRuntimeFrameworkAssembly=…` | 0 | `Failed: 0, Passed: 64`（基线 62 + 2：`combat_synced_fire_body_publishes_the_other_players_one_shot`、`combat_sentry_fire_is_not_billed_to_a_player`） |
| `dotnet test ForgeWeapon/tests/Identity --filter "Category!=Native"` | 0 | `Failed: 0, Passed: 46` |
| `dotnet test ForgeWeapon/tests/IdentityAcceptance --filter "Category!=Native"` | 0 | `Failed: 0, Passed: 37` |
| `dotnet test ForgeWeapon/tests/IdentityDispatchReview --filter "Category!=Native"` | 0 | `Failed: 0, Passed: 20` |
| `dotnet test ForgeWeapon/tests/NativeLayout --filter "Category=Native"`（`FORGE_ARTIFACTS`/`FORGE_WEAPON_BEPINEX`/`FORGE_WEAPON_HOOK_SPEC` 绝对路径） | 0 | `Failed: 0, Passed: 1`（内部检查全过，含新增的 `native.one-patch-per-fire-body`；上一节记录的 `plugin.identity` 版本不一致在本轮观测时已不再复现） |
| `dotnet test ForgeWeapon/tests/NativeEvidence --filter "Category=Native"` | 0 | `Failed: 0, Passed: 1`（内部 ≥ 60 项静态检查全过） |
| `pwsh -NoProfile -File Release/check-identity.ps1` | 0 | `核对 83 项：83 通过，0 失败。` |

未在游戏内核验的点（已写入 `evidence/w4-player-fire.json` 的 `unverified` 与 README）：重放链是否真的在实机跑（第 10、11 步一枪一计数）、`FireCount`/`FireCountSync` 是增量还是累计、`GetSync` 由谁按什么频率调用、bot 开火是否也走同步体、本地玩家的武器是否可能在某些路径上也是同步类。静态证据只能证明这条链存在，不证明它每次触发。

## `gear-block` 挂载改为序数文本比对（2026-09-16）

**已实现并已测（合成输入）**：所有挂载匹配按同一条规则——reference 文本必须与游戏读回的块 id 文本逐字相等。上一轮 `gear-block` 走的是"两边都解析成 `uint` 再比数值"，于是 `"010001"` 也能匹配块 `10001`；本轮删掉数值解析路径，改为一条 `string.Equals(..., StringComparison.Ordinal)`。**没有启动游戏**，原生结论沿用 `evidence/w5-gear-block.json`，行为结论来自合成替身与 xUnit。

- **比对逻辑**：`EquipmentNativeAdapter.MatchesGearBlock` 先用 `IsPlainDecimalId` 检查 reference 是不是规范十进制文本（数字、落在 `UInt32` 内、且是它自己 `ToString` 的结果，即网站 `String(state.blockId)` 的拼法），不是就每份不同文本写一次 `weapon.gear-block-reference-invalid` 并返回 false；是则做原有的三道守卫（世界纪元、`_byEntity` 生命表命中、`Matches(handle, backpack)` 重读槽位与实例指针），最后把 `GearBlock(...)` 读回的文本与 reference 做序数比较。
- **读回文本**：`GearBlock` 从 `GearIDRange.PlayfabItemInstanceId` 去掉 `OfflineGear_ID_` 前缀后返回**后缀文本本身**（不再解析成数字）；缺前缀、或后缀不是规范十进制文本（例如零填充）都返回 null，不做猜测。因此 `GearBlock` 返回 `string?`，数值解析函数 `TryNumber`/`TryReference` 已删除。
- **行为对照**：`10001` 匹配块 `10001`；`010001`、`+10001`、` 10001`、`10001.0`、`""`、`0x2711`、`10001a`、`4294967296`、`99999999999` 全部不匹配；`10002`、`4294967295` 是"另一个块"，不匹配但**不算非法引用**（不写诊断）；reference 为 null 时既不是文本也不写诊断。读回文本缺前缀或后缀非规范时同样不匹配。
- **测试**：`tests/NativeAdapter` 的 3 个 `gear-block` 用例改写并新增 2 个，共 5 个（`gear_block_mount_matches_only_the_block_the_gear_came_from`、`gear_block_mount_reports_each_unmatchable_reference_once`、`gear_block_mount_needs_the_games_own_record_spelling`、`gear_block_mount_refuses_other_domains_and_gone_lives`、`gear_block_plan_loads_and_dispatches_only_for_its_own_block`）。诊断只出一次的断言按"每份不同文本一次"实现（同一文本连问两次只写一行，另起一份文本再写一行）。

本轮实际运行的命令与结果（artifacts 目录 `%TEMP%\wpnfix`，`GTFO_BEPINEX_PATH` 指向只读的 `Forge-MapEditor-QA` profile）：

| 命令 | 退出码 | 结果 |
| --- | --- | --- |
| `dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release --artifacts-path $a` | 0 | `Build succeeded`，0 警告 0 错误（只为给 Native 项目提供宿主程序集，未改 ForgeRuntime 源码） |
| `dotnet build ForgeWeapon/ForgeWeapon.csproj -c Release --artifacts-path $a` | 0 | `Build succeeded`，0 警告 0 错误 |
| `dotnet build ForgeWeapon/Native/ForgeWeapon.Native.csproj -c Release --artifacts-path $a -p:ForgeRuntimeAssembly=…` | 0 | `Build succeeded`，0 警告 0 错误 |
| `dotnet test ForgeWeapon/tests/NativeAdapter --filter "Category!=Native" -p:ForgeRuntimeAssembly=…` | 0 | `Failed: 0, Passed: 62`（基线 60 + 2） |
| `dotnet test ForgeWeapon/tests/Identity --filter "Category!=Native"` | 0 | `Failed: 0, Passed: 46` |
| `dotnet test ForgeWeapon/tests/IdentityAcceptance --filter "Category!=Native"` | 0 | `Failed: 0, Passed: 37` |
| `dotnet test ForgeWeapon/tests/IdentityDispatchReview --filter "Category!=Native"` | 0 | `Failed: 0, Passed: 20` |
| `dotnet test ForgeWeapon/tests/NativeEvidence --filter "Category=Native"`（staging BepInEx：`GameAssembly.dll` 副本 sha256 `C6A5C3CD…BF55` + 指向 QA profile 的 `core`/`interop` junction） | 0 | `Failed: 0, Passed: 1` |
| `dotnet test ForgeWeapon/tests/NativeLayout --filter "Category=Native"`（`FORGE_ARTIFACTS`/`FORGE_WEAPON_BEPINEX`/`FORGE_WEAPON_HOOK_SPEC` 绝对路径） | 1 | **唯一失败项仍是既有的 `plugin.identity`**，逐字输出：`plugin.identity: NAinfini.ForgeWeapon, Infini Forge Weapon, 0.2.0; ForgeWeapon 0.1.0.0; native 0.2.0.0`（`Plugin.cs` 0.2.0 对 `ForgeWeapon.csproj` 的 0.1.0）；本轮改动涉及的检查（`native.exact-game-reads`、`native.gear-block-reads-the-offline-record` 等）全部通过 |
| `pwsh -NoProfile -File Release/check-identity.ps1` | 0 | `核对 51 项：51 通过，0 失败`（只读运行，未改 `Release/`） |

测试过程中同工作区另一任务一度把 `ForgeRuntime/Framework/RuntimeActorRoles.cs` 改成 `internal static Slot? TryGet(...)` 配 `private sealed record Slot`，`ForgeRuntime.Framework` 因此短暂编译失败（`error CS0050`），项目引用会把该失败传染给每个测试工程；中途用 `-p:BuildProjectReferences=false` 加已构建程序集绕开，等对方改回后上表全部命令都按原样重跑，结果与绕开时一致。

已报告但未改的点：`MatchesGearBlock` 不检查 host 权限，只做上述三道守卫；挂载只对**生命**生效，部署出去的世界实例不匹配；`OfflineGear_ID_` 前缀在别的构建里是否改变未在游戏内核验。

## `gear-block` 挂载 matcher 与装备块 id 读回（2026-09-16，只读静态分析）

**已实现并已测（合成输入）**：本轮按裁决 14/61 实现网站 `equipmentAttachments` 用的 `gear-block`（官方装备块 id，该块全部实例）挂载。**没有启动游戏**，原生结论来自 interop 元数据、`GameAssembly.dll` 反汇编与 `global-metadata.dat` 字面量表的只读分析（证据文件 `evidence/w5-gear-block.json`），行为结论来自合成替身与 xUnit。

- **块 id 的语义已确认**：网站把 `state.blockId`（`10000 + template.id`）写成 `PlayerOfflineGearDataBlock.persistentID`、`GearCategoryDataBlock.persistentID` 与非哨戒机的 `ArchetypeDataBlock.persistentID`。原生侧 `GearManager.LoadOfflineGearDatas`（RVA 0x1330250）要求 `Type`∈{1,2} 且 `GearJSON` 非空，再把 `String.Concat("OfflineGear_ID_", (uint)block.persistentID)` 交给 `ParseAndStashGear`（0x1330A70），后者写入 `GearIDRange.PlayfabItemInstanceId`（+0x28，与 dump.cs 的 backing field 一致）。前缀字面量在 metadata 字面量表里唯一（litOffset 67552），并由游戏自己的存档交叉验证：`GTFO_Favorites.txt` 存 `"LastEquipped_Standard": "OfflineGear_ID_34"`。
- **读回路径**：`EquipLocalGear`/`EquipSyncGear`（0x1750BB0/0x1750DB0）把同一个 `GearIDRange` 对象交给 `PlayerBackpack.SpawnAndEquipGearAsync` → `CreateAndStoreBackpackItem`，因此 `Player.BackpackItem.GearIDRange.PlayfabItemInstanceId` 就是该块 id，**不是 checksum**（该轮以此确认了块 id 可从原生读回，就是上面那节序数比对的前提）。被拒绝的候选：同步包体 `pGearIDRange`（只有 Comps/Mod/MatTrans/publicName）、`GearCategory` 组件（另一个块的 id）、`GetChecksum()`。
- **实现**：`ModuleDefinition.GearBlockAttachmentKind = "gear-block"`；`EquipmentIdentitySession` 新增可选 `gearBlockMatcher` 参数并在注册时声明 `AttachmentMatchers`（kind 由本 provider 拥有，`category` 由框架强制为 null）；读取逻辑在 `EquipmentNativeAdapter.MatchesGearBlock`/`GearBlock`——引用必须是规范十进制 `UInt32` 文本（不带符号、不带前缀、无前导零），subject 必须是本 provider 当前记录的装备**生命**（世界纪元一致、`_byEntity` 命中、`Matches(handle, backpack)` 重新读回槽位与实例指针），再比较 `PlayfabItemInstanceId` 里去掉前缀后的块 id 文本（**该轮的比较是数值相等，已由本轮上面的序数比对取代**），任何一步不成立都返回 false。部署出去的世界实例不在生命表里，因此永不匹配。native 读取面只在 `EquipmentNativeAdapter` 增加，`WeaponNativeSession.Start` 负责接线。
- **测试**：`tests/NativeAdapter` 新增 3 个用例（块 id 匹配/不匹配/10 种非法引用、非本域 subject 与已退役生命、以及挂载计划只在匹配块上派发），替身新增 `Gear.GearIDRange.PlayfabItemInstanceId`，fixture 的 `Put` 显式拼 `"OfflineGear_ID_"` 字面量（不引用生产常量，改写前缀会失败）。`tests/NativeLayout` 的 `expectedReads` 增加 `Gear.GearIDRange::get_PlayfabItemInstanceId`，并新增检查 `native.gear-block-reads-the-offline-record`（同时要求该调用边与 `ldstr "OfflineGear_ID_"` 存在）。
- **运行时清单导出不声明挂载 kind（只报告，未改任何代码）**：`RuntimeKernel.ExportManifest()` 只导出 `schemaVersion`/`runtime`/`registry{providers,capabilities,bindings}`/`bindingSupport`/`limits`，`RuntimeRegistry.Snapshot()` 里没有 matcher 表。也就是说 kind 词表（`RuntimeGraphContracts.AttachmentKinds`：`level`、`map-object`、`gear-block`、`enemy-type`）与"哪个 provider 拥有哪个 kind"无法从导出的清单里看出来，只有计划加载时的 `attachment-kind` 拒绝码能反证。这是 Runtime 的现状，不在本任务责任范围内，未做改动。

本轮实际运行的命令与结果（artifacts 目录 `%TEMP%\wpnattach\dotnet`，`GTFO_BEPINEX_PATH` 指向只读的 `Forge-MapEditor-QA` profile）：

| 命令 | 退出码 | 结果 |
| --- | --- | --- |
| `dotnet build ForgeWeapon/ForgeWeapon.csproj -c Release --artifacts-path $a` | 0 | `Build succeeded`，0 警告 0 错误 |
| `dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release --artifacts-path $a -p:GTFOBepInExPath=$bep` | 0 | `Build succeeded`（只为给 Native 项目提供宿主程序集，未改 ForgeRuntime 源码） |
| `dotnet build ForgeWeapon/Native/ForgeWeapon.Native.csproj -c Release --artifacts-path $a -p:GTFOBepInExPath=$bep -p:ForgeRuntimeAssembly=…` | 0 | `Build succeeded`，0 警告 0 错误 |
| `dotnet test ForgeWeapon/tests/Identity -c Release --filter "Category!=Native"` | 0 | `Failed: 0, Passed: 46` |
| `dotnet test ForgeWeapon/tests/IdentityAcceptance --filter "Category!=Native"` | 0 | `Failed: 0, Passed: 37` |
| `dotnet test ForgeWeapon/tests/IdentityDispatchReview --filter "Category!=Native"` | 0 | `Failed: 0, Passed: 20` |
| `dotnet test ForgeWeapon/tests/NativeAdapter --filter "Category!=Native" -p:ForgeRuntimeAssembly=…` | 0 | `Failed: 0, Passed: 60`（57 + 3 个 `gear-block` 用例） |
| `dotnet test ForgeWeapon/tests/NativeEvidence --filter "Category=Native"`（staging BepInEx：`GameAssembly.dll` 副本 + 指向 QA profile 的 `interop` junction） | 0 | `Failed: 0, Passed: 1`（内部静态证据检查全过） |
| `dotnet test ForgeWeapon/tests/NativeLayout --filter "Category=Native"`（`FORGE_ARTIFACTS`/`FORGE_WEAPON_BEPINEX`/`FORGE_WEAPON_HOOK_SPEC` 绝对路径） | 1 | **唯一失败项是既有的 `plugin.identity`**：审计把插件版本写死成 `0.1.0`，而工作区里 `Native/Plugin.cs` 已是 `0.2.0`（另一个任务的未提交改动），且 `ForgeWeapon.csproj` 的 `<Version>` 仍是 `0.1.0`，所以 `weapon.Name.Version == identity[2]` 也不成立。本轮新增/改动的检查（`native.exact-game-reads`、`native.gear-block-reads-the-offline-record`、hook/插件结构等）全部通过，未被列入失败 |
| `pwsh -NoProfile -File Release/check-identity.ps1` | 0 | `核对 51 项：51 通过，0 失败`（只读运行，未改 `Release/`） |

未在游戏内核验的点：游戏是否对每条装备路径都保留 `BackpackItem.GearIDRange` 这个对象（包体重建的 gear 读不到记录，matcher 会返回 false 而不是错误匹配）；`OfflineGear_ID_` 前缀在别的构建里是否改变；挂载只在**生命**上生效，部署出去的世界实例（`gtfo.equipment:<world>.<life>.<instance>`）不匹配是否是策展方想要的口径。已知遗留：`tests/NativeLayout` 的版本钉子（属于版本发布轮次的收尾，本轮未改）。

## 部署身份并入 gtfo.equipment、命中目标解析、端口收敛（2026-09-16）

**已实现并已测（合成输入）**：本轮按裁决 50/53 收敛注册面与身份面；**没有启动游戏**，全部结论来自合成替身、xUnit 断言与元数据审计。

- **部署身份并入 `gtfo.equipment`**（裁决 31/50）：旧的独立部署命名空间、它的 `EntityResolvers`/`EntityObservers` 条目、权限字面与相关断言全部删除（本文件与 README 只在说明"已删除"时用描述性说法，不再写出该命名空间，故静态搜索 0 命中）。`EquipmentIdentityId` 是本 provider 唯一的 id 形状定义——生命 `gtfo.equipment:<world>.<life>`、世界实例 `gtfo.equipment:<world>.<life>.<instance>`，实例号接在放置它的生命之后并从 1 递增，回收后的号不复用；`EquipmentIdentityIndex` 只接受生命形状。`EquipmentIdentitySession` 注册**一个** resolver 与**一个** observer，收到引用按形状分派（生命查索引、世界实例查放置表），因此不存在"同一引用被两张表同时回答"的可能。权限名未改：`gtfo.equipment.wield.read`、`gtfo.weapon.combat.read`、`gtfo.equipment.deployable.read`（第三个本来就在 `gtfo.equipment.*` 下，只是词面含 deployable，是否改名留给裁决方）。
- **`hit_candidate.target` 由命中对象自己的原生类型解析**（裁决 50）：`EquipmentNativeAdapter.HitTarget` 从 `data.rayHit.collider` 起 `GetComponentInParent<Dam_EnemyDamageLimb>()` / `<Dam_PlayerDamageLimb>()` 取 `GetBaseAgent()`，再按 agent 真实类型选 kind：`gtfo.enemy` 用 agent 本身（ForgeEnemy 的 `EntityInstanceResolvers` 键就是 `EnemyAgent`），`gtfo.player` 用 `agent.Owner`（ForgeMap 的实例表键是 `SNet_Player`）。端口在注册图里改为 **`optional`（不是 `nullable`）**；解析不出引用时该端口**整条不出现**，事实照发。世界几何、部署物、没有伤害肢、kind 未登记、agent 未被登记五种情况各有断言。
- **`recall_completed` 不再有返还数量端口**：`PublishRecall` 的负载只剩 `actor` 与 `deployed`，注册图同步删除该端口。`deploy_completed` 的 `deployment:handle` 仍未注册（沿用上一轮的选择 1：Runtime 没有 handle 端口契约）。
- **Framework 一行语义变更**（裁决 53，条件满足：同目录 `rtabi.out.md` 存在且 >200B）：`RuntimeKernel.Entities.cs::ResolveEntityInstance` 对**未登记的 kind 返回 null**，非法 kind 名仍抛 `entity-resolver`。受影响的本包断言只有一条（原 `owner_missing_player_lookup_fails_closed` 期望 `gtfo.player` 未登记时锁存故障），已按新语义改名为 `owner_kind_without_an_instance_lookup_is_unresolved_not_a_fault`：不记录、不发布、每个背包写一次 `weapon.owner-unresolved`，会话继续观察。
- **`wrong_thread_does_not_mutate`（上一轮 Identity 的唯一失败）**：生产代码行为正确，问题在测试的"外来线程"机制——xUnit 在池线程上装 `AsyncTestSyncContext` 跑测试体，`Task.Run` 的委托随后被**内联回同一条池线程**，于是 `Dispose` 看到的正是属主线程。断言未动，改为专用 `Thread`+`Join` 的 `OnForeignThread`，两次调用都稳定抛 `wrong-thread`。
- 清理：`NativeLayoutTests` 未使用的 `Hash` 本地函数与 `System.Security.Cryptography` using 删除（CS8321 消失）；临时探针 `tests/Identity/ZzProbe.cs` 删除。

原生读取面的新增项已在 `tests/NativeLayout` 逐条复核并锁进 `expectedReads`，同时进入只读白名单（`NativeLayoutTests.cs` 的 `queries`）：`Dam_EnemyDamageLimb::GetBaseAgent`、`Dam_PlayerDamageLimb::GetBaseAgent`（游戏自己的伤害肢 owner 取值器）、`UnityEngine.Component::GetComponentInParent`（Unity 层级查询）、`UnityEngine.RaycastHit::get_collider`。四者都是纯读，没有写路径。

本轮实际运行的命令与结果（artifacts 目录 `%TEMP%\wpnfix`，`GTFO_BEPINEX_PATH` 指向只读的 `Forge-MapEditor-QA` profile）：

| 命令 | 退出码 | 结果 |
| --- | --- | --- |
| `dotnet build ForgeWeapon/ForgeWeapon.csproj -c Release --artifacts-path $a` | 0 | `Build succeeded`，0 警告 0 错误 |
| `dotnet build ForgeWeapon/Native/ForgeWeapon.Native.csproj -c Release --artifacts-path $a -p:GTFOBepInExPath=$bep -p:ForgeFrameworkAssembly=… -p:ForgeRuntimeAssembly=…` | 0 | `Build succeeded`，0 警告 0 错误 |
| `dotnet build ForgeMap/Native/ForgeMap.Native.csproj … --artifacts-path $a` | 0 | `Build succeeded`（只为给 NativeLayout 提供输入程序集，未改 ForgeMap 源码） |
| `dotnet test ForgeWeapon/tests/Identity -c Release --filter "Category!=Native"` | 0 | `Passed! - Failed: 0, Passed: 46`（43 + 1 个 id 形状用例 + 2 行非法形状 Theory） |
| `dotnet test ForgeWeapon/tests/IdentityAcceptance --filter "Category!=Native"` | 0 | `Failed: 0, Passed: 37` |
| `dotnet test ForgeWeapon/tests/IdentityDispatchReview --filter "Category!=Native"` | 0 | `Failed: 0, Passed: 20` |
| `dotnet test ForgeWeapon/tests/NativeAdapter --filter "Category!=Native" -p:ForgeRuntimeAssembly=… -p:ForgeRuntimeFrameworkAssembly=…` | 0 | `Failed: 0, Passed: 57`（新增命中目标与省略端口两个用例） |
| `dotnet test ForgeWeapon/tests/NativeLayout --filter "Category=Native"`（`FORGE_ARTIFACTS`/`FORGE_WEAPON_BEPINEX`/`FORGE_WEAPON_HOOK_SPEC` 绝对路径） | 0 | `Failed: 0, Passed: 1`（内部 56+ 项元数据检查全过） |
| `dotnet test ForgeWeapon/tests/NativeEvidence --filter "Category=Native"`（staging BepInEx：`GameAssembly.dll` 副本 sha256 `C6A5C3CD…BF55` + 指向 QA profile 的 `interop` junction） | 0 | `Failed: 0, Passed: 1`（内部 71 项静态证据检查全过） |
| `dotnet build ForgeEnemy/tests/EntityObservation -p:ForgeFrameworkAssembly=…` + `dotnet <dll> <report>` | 0 | `PASS 72/72 Enemy entity observation cases`（新增 6 个 `gtfo.enemy` 实例解析器用例） |
| `dotnet <ForgeRuntime/tests/EntityObservation>` | 0 | `Entity contracts: 122 passed; 0 failed` |
| 在 `ForgeWeapon/`（排除 `bin`/`obj`/`evidence`）静态搜索已删除的部署命名空间与返还数量端口名 | 1 | 0 命中（rg 无匹配时退出码 1） |

未在游戏内核验的点：`Dam_EnemyDamageLimb`/`Dam_PlayerDamageLimb` 是否覆盖全部可被子弹命中的敌人与玩家碰撞体（IAgent 型敌人、护盾、开发用无敌体等）；`GetComponentInParent` 在这些碰撞体层级上的实际代价与返回顺序；命中候选与伤害结算的时序是否**总是**先于伤害（本轮只按"候选先于结算"的既有证据处理）。接线对每一种情况都取"解析不出就不发该端口"的实现，不会因为读不到而伪造引用。

## W4 射击、命中与部署物身份接线（2026-09-15）

**已实现并已测（合成输入）**：本轮把 W4 证据里已经确认的 Slot 151 开火入口、`BulletHit` 与两个部署物家族的 `OnSpawn`/`SyncedPickup`/`OnDespawn`/`OnDestroy` 接成观察事实；**没有启动游戏**，全部结论来自合成替身与元数据审计。

注册面从 2 个观察 binding 变成 7 个：`forge.trigger.combat.shot_committed`（端口 `next/source/equipment/index`）、`forge.trigger.combat.hit_candidate`（`next/source/equipment/target?/limb?/position`）、`forge.trigger.entity.despawned`（`next/entity/reason`）、`forge.trigger.equipment.deploy_completed`（`next/actor/deployed`）、`forge.trigger.equipment.recall_completed`（`next/actor/deployed`），加上原有的 equipped/unequipped。7 个 binding 全部 `role=observe`、`implementation-only`、无 handler，因此不写任何结果行；新增权限 `gtfo.weapon.combat.read`、`gtfo.equipment.deployable.read`。（`recall_completed` 的端口、`target` 的 `optional` 语义与 `equipment` 必有端口的加入都是 2026-09-16 的裁决结果，见前两节；本节其余记录未改。）

部署物在世界里的实例与背包里的装备生命同属 `gtfo.equipment`，由 id 形状区分：`gtfo.equipment:<world>.<life>` 是生命，`gtfo.equipment:<world>.<life>.<instance>` 是那次放置的世界实例。`EquipmentIdentitySession` 只注册一个命名空间的一个 resolver 与一个 observer，按形状分派到索引或放置表，一个引用永远只由一张表回答。（2026-09-16 前的记录是独立命名空间加前缀分派，已被裁决 50 取代。）

`deploy_completed` **没有注册 `deployment:handle` 端口**：本仓库的 Runtime 至今没有 handle 端口契约（rtabi 的 FrameHandle 尚未落地），注册一个没有契约背书的端口只会造出一个永远为空的端口。命中目标与回收数量的端口处理见下一节，它们不再按 `nullable` 注册。

去重点与结束点：

- 开火：每个 `Fire` 体（Slot 151 的四个实现）发布**恰好一条** `shot_committed`，`index` 是该装备生命内的第几发。`BulletHit` 是静态方法，一次射击可以有多个命中点，所以命中只认领"最近一次开火"（`WeaponCombatObserver` 只保留一个开着的 `Shot`，被下一次 `Fire` 顶掉），命中没有开火窗口时丢弃并写 `weapon.hit-outside-shot`——哨戒炮的 `FireBullet` 走的正是同一条 `BulletHit`。
- 部署物：`OnSpawn` 建身份并发布 `deploy_completed`；槽位标记从"已部署"翻回"在背包里"时发布 `recall_completed`；`SyncedPickup`、`OnDespawn`、`OnDestroy` 三条路径都进同一个 `End`，第一条到达的路径结束它并发布 `entity.despawned`，其余路径查不到放置记录所以不再发布。换关无论走 `OnDespawn` 还是 `OnDestroy`，结果都是恰好一条结束事实。
- 世界或权威切换时在飞的部署物**不发事实**直接丢弃并写 `weapon.deployments-closed`：把旧世界的结束事件盖到新世界上是假事实，重新打戳同样是假事实。

未在游戏内核验的点（见 README"已知未核验的点"）：客户端开火是否在 host 重放；`WeaponHitData.owner` 是否由 `Fire` 体填写；`SentryGunInstance.Owner`/`MineDeployerInstance.Owner` 在 `OnSpawn` 时刻是否可用；关卡切换实际走哪条结束路径；部署物 `transform.position` 的坐标语义。接线对每一种情况都取"两条路都成立、且只结束一次"的实现。

本轮实际运行的命令与结果：

| 命令 | 退出码 | 结果 |
| --- | --- | --- |
| `dotnet build ForgeRuntime/Framework/ForgeRuntime.Framework.csproj` | 0 | `Build succeeded`（本会话开始时报 `RuntimeKernel.cs(661,40) CS7036`，已被同工作区另一任务修好） |
| `dotnet build ForgeRuntime/ForgeRuntime.csproj` | 0 | `Build succeeded` |
| `dotnet build ForgeWeapon/Native/ForgeWeapon.Native.csproj --artifacts-path %TEMP%\wpnobs` | 0 | `Build succeeded` |
| `dotnet build ForgeMap/Native/ForgeMap.Native.csproj --artifacts-path %TEMP%\wpnobs` | 0 | `Build succeeded`（NativeLayout 的输入之一） |
| `dotnet test ForgeWeapon/tests/NativeAdapter -c Release --no-build --filter "Category!=Native"` | 0 | `Passed! - Failed: 0, Passed: 55, Skipped: 0, Total: 55` |
| `dotnet test ForgeWeapon/tests/IdentityDispatchReview -c Release --filter "Category!=Native"` | 0 | `Passed! - Failed: 0, Passed: 20, Skipped: 0, Total: 20` |
| `dotnet test ForgeWeapon/tests/IdentityAcceptance -c Release --filter "Category!=Native"` | 0 | `Passed! - Failed: 0, Passed: 37, Skipped: 0, Total: 37` |
| `dotnet test ForgeWeapon/tests/NativeLayout -c Release --filter "Category=Native"` | 0 | `Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1`（内部布局检查项全部通过） |
| `dotnet test ForgeWeapon/tests/NativeEvidence -c Release --filter "Category=Native"` | 0 | `Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1`（内部 ≥ 60 项静态检查全部通过；`GameAssembly.dll` 由 `%TEMP%\wpnobs-evidence\BepInEx` 的副本提供，其 sha256 与证据锁逐字相同，interop 是指向 QA profile 的 junction——`NativeEvidence` 需要 BepInEx 根下有 `GameAssembly.dll`，而 r2modman 的 profile 目录里没有） |
| `dotnet test ForgeWeapon/tests/Identity -c Release --filter "Category!=Native"` | 1 | `Failed! - Failed: 1, Passed: 42, Skipped: 0, Total: 43`；唯一失败是 `wrong_thread_does_not_mutate`，见下 |

`Identity` 的 `wrong_thread_does_not_mutate` 失败与本轮改动无关：它断言 `EquipmentIdentitySession.Dispose()` 从别的线程调用必须被拒为 `wrong-thread`，而当前工作区 Runtime 的生命周期读（`RuntimeKernel.Lifecycle` 的 `ReadThread`）在这一条路径上不再拒绝，`IdentityAcceptance` 同样形状的检查则通过。没有改这条断言——放宽它等于删掉一条别人写的并发不变量，应由 Runtime 侧或 Claude 复核后决定是恢复拒绝还是改写断言。

`NativeAdapter` 的 `55` 比 D-026 表里的 `45` 多 10 项，是本轮新增的用例：`combat_one_shot_fact_per_fire_body_with_several_hits`（一个 `Fire` 体一条事实，多命中仍只一条）、`combat_hit_without_an_open_shot_is_not_a_candidate`、`combat_client_publishes_nothing`、`combat_shot_without_the_equipment_life_publishes_nothing`、`deploy_recall_redeploy_ends_lives_once`（回收后重放得到新身份，旧身份被拒）、`deploy_despawn_then_destroy_publishes_once`（销毁只结束一次）、`deploy_mine_family_uses_its_own_ending_paths`（矿没有 `OnDespawn`）、`deploy_world_change_closes_placements_without_a_stale_fact`、`deploy_level_change_paths_both_end_exactly_once`、`deploy_equipment_observer_names_the_open_placement`（装备 observer 能指出打开的世界实例）。上述全部为 `[Fact]`，实测 `Total: 55` 与之相符。`IdentityDispatchReview`（20）与 `NativeLayout`（1 个 `[Fact]`，内部布局检查项全部通过）的用例数与 D-026 表一致。

两处夹具同步（**不是本轮功能改动，是被同工作区的 ABI 变更推着走的**）：Runtime 的 plan 契约先是在本会话期间变成 `schemaVersion == 3`（D-017 R4-a），随后又变成 `schemaVersion == 4` 并要求非空 `attachments[]`、result 端口声明固定前四列 `fields`。`tests/NativeAdapter` 与 `tests/IdentityDispatchReview` 的计划夹具因此迁到 v4（挂载目标是 `level`，由内核自己解析，所以夹具不需要 matcher provider），result 端口补上 `target,status,committed,code` 四列。`NativeLayout` 的 Hook 集合断言原来是"等于证据文件里的 8 个 Hook"，本轮扩成"证据文件里的 8 个按记录签名核对 + 之后新增的 10 个按各自补丁声明在游戏程序集里核到唯一方法"，精确读取集合补上 8 条新读取（`Item.get_Owner`、`Component.get_transform`、`Transform.get_position`、`RaycastHit.get_point`、`WeaponHitData.get_owner`、`WeaponHitData.get_rayHit`）。**没有改 `evidence/**`**：那份文件是 W1 的冻结见证，本轮新增的 Hook 不在其中，因此用游戏程序集本身作为见证。

另外两处断言随本轮**功能面**变化同步：`tests/Identity` 的 `no_gameplay_claims_or_embedded_kernel` 原来要求"恰好两个 capability、每个 binding 只读 `gtfo.equipment.wield.read`"，`tests/IdentityAcceptance` 的 `public_module_no_executable_claims` 原来要求 `capabilities` 恰为 2。两处都改成"capability 集合精确等于本包声明的 7 个 / 非空，且所有 binding 都是 `observe`、`implementation-only`、只申请三个读权限之一"，判别力没有降低（仍然精确匹配 id 集合与角色）。

## 测试工程迁移到 xUnit（D-026）（2026-09-15）
**实现，未测（D-020）**：本节只描述迁移后的结构与待跑命令，未运行任何测试；`dotnet build` 已逐个执行，结果见表。

六个测试工程（`tests/Identity`、`IdentityAcceptance`、`IdentityDispatchReview`、`NativeAdapter`、`NativeEvidence`、`NativeLayout`）从自写 `Program.cs` 计数器改为 xUnit 测试工程：`Microsoft.NET.Test.Sdk` 17.14.1、`xunit` 2.9.3、`xunit.runner.visualstudio` 3.1.5，目标框架 `net6.0` → `net8.0`（Test.Sdk 17.14.1 明确不支持 net6.0）。旧 `Program.cs`、自写 ExitCode、`RESULT`/`PASS` 汇总输出与 JSON 报告全部删除，断言逐条保留为 `[Fact]`（Identity 的 15 行非法观察表保留为 `[Theory]`/`MemberData`），未放宽或删除任何检查。测试类之间共享进程级静态状态（Harmony patch、`Host.Runtime`、`WeaponNativeSession.Current`、`SNet.IsMaster`），因此每个程序集都加了 `AssemblyAttributes.cs` 的 `[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]`。

命令行参数改为环境变量或 xUnit 配置：

| 旧参数 | 现在 |
| --- | --- |
| `NativeEvidence <BepInEx> <game> <dump> <spec> <report>` | `FORGE_WEAPON_BEPINEX`（回退 `GTFO_BEPINEX_PATH`）、`FORGE_GTFO_GAME`、`FORGE_GTFO_DUMP`、`FORGE_WEAPON_HOOK_SPEC`（默认 `ForgeWeapon/evidence/w1-native-hooks.json`）；不再写 JSON 报告 |
| `NativeLayout <BepInEx> <sdk> <host> <map> <map.native> <spec> <report>` | `FORGE_ARTIFACTS` + 制品相对路径 `bin/ForgeRuntime.Framework/release/ForgeRuntime.Framework.dll`、`bin/ForgeRuntime/release/ForgeRuntime.dll`、`bin/ForgeMap/release/ForgeMap.dll`、`bin/ForgeMap.Native/release/ForgeMap.Native.dll`、`bin/ForgeWeapon/release/ForgeWeapon.dll`、`bin/ForgeWeapon.Native/release/ForgeWeapon.Native.dll`；`FORGE_WEAPON_HOOK_SPEC`、`FORGE_WEAPON_BEPINEX` 同上 |
| `IdentityAcceptance --report <path>`、`IdentityDispatchReview --report <path>` | 报告改由 `dotnet test --logger trx` 产出，`tools/*.py` 解析 TRX |

`NativeEvidence` 与 `NativeLayout` 需要游戏程序集，标记为 `[Trait("Category", "Native")]`，默认用 `--filter "Category!=Native"` 排除；`Identity`、`IdentityAcceptance`、`IdentityDispatchReview`、`NativeAdapter` 不加载游戏程序集，默认运行。

迁移后用例数（原用例数 → 新 `[Fact]`/`[Theory]` 用例数，`dotnet build` 均退出码 0）：

| 工程 | 原 | 新 |
| --- | --- | --- |
| `tests/Identity` | 43（28 具名 + 15 表驱动） | 43（28 `[Fact]` + 1 `[Theory]`×15） |
| `tests/IdentityAcceptance` | 37 | 37 `[Fact]` |
| `tests/IdentityDispatchReview` | 20 | 20 `[Fact]` |
| `tests/NativeAdapter` | 44 | 45 `[Fact]`（新增 `plugin_suspended_host`，覆盖宿主挂起分支） |
| `tests/NativeEvidence` | 71 项静态检查 | 1 `[Fact]`，内部断言全部检查项通过且总数 ≥ 60 |
| `tests/NativeLayout` | 68 项布局检查 | 1 `[Fact]`，内部断言全部检查项通过且总数 ≥ 56 |

`NativeAdapter` 的 `ForgeRuntime.Plugin` 替身与 `TestLog` 补上生产代码新增的 `IsSuspended`/`SuspensionCode`/`LogError`，否则编译不过；生产 `Native/Plugin.cs` 未改。

待跑命令（**尚未执行**，需在设置好 `GTFO_BEPINEX_PATH`、`FORGE_ARTIFACTS` 后由人工运行）：

```powershell
$a = "$env:TEMP\dsh-xunitwm"
$env:GTFO_BEPINEX_PATH = "$env:APPDATA\r2modmanPlus-local\GTFO\profiles\Forge-MapEditor-QA\BepInEx"
dotnet test ForgeWeapon/tests/Identity/Identity.csproj -c Release --artifacts-path $a --filter "Category!=Native"
dotnet test ForgeWeapon/tests/IdentityAcceptance/IdentityAcceptance.csproj -c Release --artifacts-path $a --filter "Category!=Native"
dotnet test ForgeWeapon/tests/IdentityDispatchReview/IdentityDispatchReview.csproj -c Release --artifacts-path $a --filter "Category!=Native"
dotnet test ForgeWeapon/tests/NativeAdapter/NativeAdapter.csproj -c Release --artifacts-path $a --filter "Category!=Native" `
  -p:ForgeRuntimeAssembly=$a\bin\ForgeRuntime\release\ForgeRuntime.dll
$env:FORGE_ARTIFACTS = $a; $env:FORGE_GTFO_GAME = "<GTFO install>"; $env:FORGE_GTFO_DUMP = "<dump.cs>"
dotnet test ForgeWeapon/tests/NativeEvidence/NativeEvidence.csproj -c Release --artifacts-path $a --filter "Category=Native"
dotnet test ForgeWeapon/tests/NativeLayout/NativeLayout.csproj -c Release --artifacts-path $a --filter "Category=Native"
python ForgeWeapon/tools/verify-w1.py --bepinex $env:GTFO_BEPINEX_PATH --game $env:FORGE_GTFO_GAME
python ForgeWeapon/tools/verify-identity-acceptance.py
python ForgeWeapon/tools/verify-identity-dispatch.py
python ForgeWeapon/tools/test-identity-mutations.py
```

`verify-w1.py`、`verify-identity-acceptance.py`、`verify-identity-dispatch.py`、`test-identity-mutations.py` 已改为调用 `dotnet test` 并解析 `--logger trx` 结果；变异脚本用 TRX 里失败的具体测试名判定检出，编译失败不再计作检出。已做 `python -m py_compile` 语法检查，未实际运行。

## W4 射击与部署物原生证据（2026-09-15，只读静态分析）

**实现（证据），未测（玩法）**：本节只记录对已装 build 的只读静态分析结果，没有启动游戏、没有安装 Hook、没有执行任何原生方法。证据文件是 [evidence/w4-combat-hooks.json](evidence/w4-combat-hooks.json) 与 [evidence/w4-deployable-hooks.json](evidence/w4-deployable-hooks.json)。

方法与口径：本地 dump.cs（SHA-256 `BF657C0E…DE1CC`，与 W1 同一份）提供 RVA、Slot 与声明行，`GameAssembly.dll`（SHA-256 `C6A5C3CD…BF55`，build 20403457 / revision 34873）提供字节，用 Iced x64 解码每条方法体。方法体从 dump RVA 开始，到同类型下一个不同 RVA 或 padding 为止，列出的边是同一条 `E8/E9 rel32` 调用指令本身。`rvaSharing=1` 表示整个 dump 里只有一条方法声明用这个 RVA。**不解引用虚调用、接口调用与委托**，所以"没有直接调用者"只说明它是间接调用的，不代表死代码。interop 程序集（`Modules-ASM.dll`、`SNet_ASM.dll`）只用于核对命名空间与类型名，它们的 body 是 `il2cpp_runtime_invoke` thunk，读不出调用关系。

射击：

- 开火入口是每族武器的同一个虚槽 `Fire(bool resetRecoilSimilarity)` = **Slot 151**：`BulletWeapon.Fire` `0x145FB30`、`BulletWeaponSynced.Fire` `0x145D430`、`Shotgun.Fire` `0x13C3020`、`ShotgunSynced.Fire` `0x13C2580`，四个 RVA 各自唯一。没有单独的 OnShot/OnFire 入口。
- 命中判定只有一个静态方法：`BulletWeapon.BulletHit` `0x145EE10`（`share=1`，非虚），直接调用者正好是上面四个 `Fire`。它按值接收 `Weapon.WeaponHitData`，所以补丁拿不到开火者，只能拿到结构体里游戏自己写的 `owner` 字段（偏移 `0x68`，类型 `PlayerAgent`）。
- **一次射击会产生多次命中，不是一次**：`BulletWeapon.Fire` 与 `BulletWeaponSynced.Fire` 各有两个 `BulletHit` 调用点（一个直落、一个在循环体内），`Shotgun.Fire` 有两个、`ShotgunSynced.Fire` 有一个，且都在循环体内。一次射击只发布一次事实的去重点因此只能是 `Fire` 本身（Slot 151），或由 `Fire` 前缀开一个作用域、命中时只上报明细；放在 `BulletHit` 上必然多发。
- 客机路径只看到接收端：`BulletWeaponSynced.OnSyncFire(int shots)` `0x145E330`（Slot 128，28 字节）只转发给两个空实现 `OnSyncFireRegular`/`OnSyncFireBurst`，自己不调用 `Fire` 也不调用 `BulletHit`。**主机是否因客机开火而执行 `Fire` 未证实**，原因是 `Fire` 没有直接调用者、复制调用方走虚表，解码器不跟进；要证实需要运行时在主机上记录 `Fire`。这一条留给 Claude 复核。

部署物（哨戒炮与雷）：

- 部署分两步两个对象：手上的 `SentryGunFirstPerson.PlaceOnGround` `0x14115E0` 调 `PlayerBackpack.IsDeployed` 与 `SetDeployed`（写"槽位已部署"标记）；世界里的 `SentryGunInstance` 是另一个对象，经物品复制路径 `OnSpawn(pGearSpawnData)` `0x14184B0`（Slot 150，无直接调用者）生成，并在自己体内再写一次同一个 `SetDeployed`。
- 回收 `SentryGunInstance.SyncedPickup(PlayerAgent)` `0x141B2B0`（Slot 45，`ISyncedItem`）：读 `PlayerAgent.get_Owner`，写 `SetDeployed`，并读自己的 `get_Replicator`。
- 换关/销毁：`OnDespawn` `0x1417A20`（Slot 35，覆写 `ItemEquippable.OnDespawn`，90 字节转发）与 Unity 的 `OnDestroy` `0x1417A80` 是**两条**路径，`OnDestroy` 与私有 `CleanUp` `0x14122C0` 都调 `StopFiring` 与 `StopScanning`；`CleanUp` 没有直接调用者。`SetDeployed` 的直接调用者只有三处（放置、世界生成、回收），**`OnDespawn` 不在其中**，所以 W1 关于"世界实例消失后原生标记可能仍为真"的限制仍然成立。
- 部署者身份用装备自己的 owner：`SentryGunInstance` 继承 `ItemEquippable` 的 `PlayerAgent Owner`（槽位 33/34 的 get/set），`CheckIsSetup` `0x1411EC0` 读 `PlayerAgent.get_Owner`；`SyncedTurnOn(PlayerAgent, AIG_CourseNode)`（Slot 40）与 `SyncedTurnOff(PlayerAgent)`（Slot 41）也把施动玩家作为参数。位置来自世界对象自身（`GroundOffset` Slot 167 与注册的 course node），放置位置由手上的 `CheckCanPlace`/`PlaceOnGround` 决定。
- 目标选择与开火是分开的组件：`SentryGunInstance_Detection.UpdateDetection`（Slot 6）同时调 `CheckForPlayerTarget`、`CheckForTarget`、`CheckForTargetLegacy`（都是私有静态），玩家目标选择器与敌人目标选择器同体，医疗炮台可以直接复用；`SentryGunInstance_Firing_Bullets` 把主机与客机拆成 Slot 8 `UpdateFireMaster` / Slot 9 `UpdateFireClient`，私有 `FireBullet(bool doDamage, bool targetIsTagged)` `0x1414370` 只有 `UpdateFireAuto`/`UpdateFireSemi`（都带 `isMaster` 参数）能到达，并且它调用的就是 `Gear.BulletWeapon::BulletHit` `0x145EE10`——哨戒炮与玩家武器共用同一条命中路径（承伤一侧见 enemydmgev）。
- 权威端：`SentryGunInstance.Update`（Slot 139）分派 `UpdateMaster` `0x141D000` 与 `UpdateClient` `0x141C8F0`；`WantToFire` `0x141E150` 与 `RefreshDangerZone` `0x1418BC0` 的直接调用者只有 `UpdateMaster`。类型上 `SentryGunInstance` 是 `IDynamicReplicatorSupplier<pGearSpawnData>`，并带一个 `SentryGunInstance_Sync`（`SNet_Packet<pSentryGunSync_New>` 与 `OnTargetingData` 接收端）。**两端都有对象，主机权威**；因此 Forge 的部署物脉冲应在主机侧发布，让原生复制带可见效果。
- `MineDeployerInstance` 形状相同但更小：`OnSpawn(pItemSpawnData)` Slot 150 `0x14FFEF0`、`Setup` Slot 36 `0x1500200`、`SyncedPickup` Slot 45 `0x1500600`（读 `PlayerAgent.get_Owner`）、私有 `OnDestroy` `0x14FF9C0`；它**不**调 `SetDeployed`。

未证实项（详见两份 JSON 的 `unverified`）：客机的开火是否在主机侧重放；世界实例由谁创建、换关时走 `OnDespawn` 还是 `OnDestroy`；`BulletHit` 是否把开火者写进 `owner`；`OnDespawn` 是否清标记；目标选择器的每原型分支；`SentryGunInstance_Sync` 的 body 与出边（该类型不在本轮解码焦点内，只声明了槽位与包字段，没有断言任何调用边）。

两份 JSON 用 `ConvertFrom-Json` 解析通过（`w4-combat-hooks.json`：12 个方法条目、10 条边；`w4-deployable-hooks.json`：22 个方法条目、20 条边）。本轮没有新增或修改任何 `.cs`/`.csproj`，没有改动 `tests/**`。

**上次更新：2026-09-14**（内容合并自原 W1-IDENTITY-HANDOFF、W1-IDENTITY-ACCEPTANCE-HANDOFF、W1-IDENTITY-REVIEW-HANDOFF、W1-NATIVE-API-AUDIT 四份交接记录与旧 VALIDATION）。

## W1 背包之外装备路径调研与部署读回（2026-09-14）

新增 `evidence/w1-native-world-paths.json`：只读调研背包之外的装备出现/消失路径（关卡拾取、世界掉落、部署物、转移），记录所用 interop 目录与程序集 sha256、每个成员的签名、RVA 唯一性（dump 中声明该 RVA 的条目数）、直接调用方与同步/本地归属，以及"能否提供身份与 owner"的判定。调研结论只接线了一条路径：`PlayerBackpack.SetDeployed` 的 postfix（第 8 个 Hook）加 `PlayerBackpack.IsDeployed(slot)` 读回，同一个装备生命在部署时以 `Location=Deployed`（无槽位、未持有）记录，收回后回到 `Inventory`；其余路径记录在案但不接线（世界物品的 `itemID_gearCRC` 没有 gear/item 判别，也没有世界物品表，资源定义只能靠猜；世界实例与虚拟分发的方法没有直接调用边）。

- `WeaponNativeHooks` 7 → 8 个 Hook；`NativeLayout` 的纯查询白名单增加 `IsDeployed`，精确读取集合增加 `Player.PlayerBackpack::IsDeployed`。
- `weapon.equipment-life-started` 行末增加 `location=Inventory|Deployed`；同一生命的部署状态变化另写 `weapon.equipment-location`；`Record` 对非 Inventory 位置不发布 equipped/unequipped，因此部署与回收只产生观察变化，不补造持有事实。
- 证据文件 `w1-native-hooks.json`：8 个 Hook、32 条直接调用边（新增 15 条）。

interop 目录：调研与冻结锁统一用 r2modman `Forge-MapEditor-QA` profile 的 `BepInEx/interop`（`Modules-ASM.dll` sha256 `E499B9C0…36D63`，mvid `6d066008-28db-4edf-9c0e-df9db732560d`；`GameData-ASM.dll` `DEE52362…E7106`；`SNet_ASM.dll` `6DAD1168…CF9B2C`）。本节记录时 `w1-native-hooks.json` 与 `w1-native-contract.json` 的冻结文件锁还指向 `Temp` profile 的旧副本（`A31AF38F…07943`，mvid `2875668a-…`），当天稍后已按指纹等价与 RVA 复检结果统一到 `Forge-MapEditor-QA`，见 [下文](#nativeevidence-冻结输入统一到-forge-mapeditor-qa2026-09-14)。

全部用会话临时目录的隔离 `--artifacts-path` 与 `--disable-build-servers` 构建，报告写到新目录，没有安装、没有启动 GTFO、没有改动 ForgeRuntime 的源码或 Git 状态。

| 套件 | 退出码 | 输出结尾 |
| --- | --- | --- |
| 宿主、`ForgeWeapon`、`ForgeMap.Native`、`ForgeWeapon.Native` 构建 | 0 | 各 0 警告 0 错误 |
| `tests/NativeLayout` | 0 | `PASS 68/68 Weapon native layout checks; no GTFO execution.` |
| `tests/NativeEvidence`（当时的 `Temp` profile 输入） | 0 | `PASS 71/71 Weapon static native evidence checks; game execution NOT tested.` |
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

`NativeEvidence` 当时用 `Forge-MapEditor-QA` profile 运行会停在 69/71，失败的只有 `hash.Modules-ASM.dll` 与 `mvid.Modules-ASM.dll`（该 profile 的副本 2026-09-09 重新生成过），签名、RVA 唯一性、可执行段与 32 条调用边全部通过，因此当时按指纹等价结论改用冻结锁对应的 `Temp` 副本运行。**当天稍后已把冻结锁统一到 `Forge-MapEditor-QA` 副本，这两项不符随之消失**，见下一节。

**这些仍是托管替身、编译后元数据与静态调用图证据，不是游戏验证。** 部署读回的真实行为（游戏是否保留部署槽位的 `BackpackItem` 实例、原生标记的生命周期）未在游戏内核验，已列入 README 的未核验清单。

## NativeEvidence 冻结输入统一到 Forge-MapEditor-QA（2026-09-14）

`w1-native-hooks.json` 与 `w1-native-contract.json` 冻结的 interop 副本原指只读的 `Temp` profile，而构建引用、本节调研与 `w1-native-world-paths.json` 用的是 `Forge-MapEditor-QA`（2026-09-09 重新生成）。两份副本的 MVID 与文件字节不同，但各自 `interop/assembly-hash.txt` 相同（`565871abd714937729d0e74520563bec`），即由同一份 `GameAssembly.dll` 生成。按"唯一冻结来源与构建参考同一目录"的决定，该来源定为 `Forge-MapEditor-QA` 的 `BepInEx/interop`。

换锁前只读复检（Cecil 与 dump 解析，不加载程序集、不启动游戏）：

| 检查 | 结果 |
| --- | --- |
| 逐类型/字段/属性/方法签名指纹（Cecil，`$env:TEMP\dsh-v2\fingerprint.json`） | 3/3 identical：`Modules-ASM.dll` rows=170126 摘要 `E22E23BF…27B44`、`GameData-ASM.dll` rows=32818 摘要 `3F24DCF5…AF1A1F`、`SNet_ASM.dll` rows=6869 摘要 `888B8ED4…49B4337`，两份副本逐行相同 |
| 锁定 RVA 在 dump.cs（sha256 `BF657C0E…DE1CC`，build 20403457）里的声明数与共享计数（`$env:TEMP\dsh-v2\rva-results.json`） | 98/98 恰好一条声明且不共享：8 个 Hook 目标、37 个直接调用边端点、51 个 world-paths 成员与调用方、2 个 Enemy `nativeRva` |
| 构建来源 | `GameAssembly.dll` sha256 `C6A5C3CD…7BF55`、Steam buildid 20403457 与两个锁的 `gameAssemblySha256`/`buildId` 一致，故同一份 dump.cs 对两份 interop 都成立 |

改动只替换锁里的文件 sha256 与 MVID（`Modules-ASM.dll` → `E499B9C0…36D63` / `6d066008-…`；`GameData-ASM.dll` → `DEE52362…E7106`；`SNet_ASM.dll` → `6DAD1168…CF9B2C`），签名、RVA、调用边、`dumpSha256` 与游戏标识未改。`w1-native-world-paths.json` 的 `interop.used` 已是该目录；原先只用于记录 `Temp` 差异的 `frozenEvidenceLock` 随差异消失一并删除。README 的原生套件说明改为把 `$bep` 指向该 profile。

改动前的运行计数（上一次运行留下的报告，不是本次运行）：`Temp` 输入 `PASS 71/71`（`$env:TEMP\dsh-v\logs\weapon-before-temp.log`）；`Forge-MapEditor-QA` 输入 `FAIL 69/71`，仅 `hash.Modules-ASM.dll` 与 `mvid.Modules-ASM.dll` 不符（`$env:TEMP\dsh-v\logs\weapon-before-qa.log`）。换锁后用 `Forge-MapEditor-QA` 输入、独立 artifacts 构建复跑 `tests/NativeEvidence`：退出码 0，`PASS 71/71`；没有跑任何游戏或安装动作。命令见 [README](README.md#复跑)。

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
- 在 r2modman 新建一个隔离的测试 profile，不用日常 profile，也不用构建参考的 `Forge-MapEditor-QA` profile。只放 BepInEx、宿主 `ForgeRuntime.dll` 及 `ForgeRuntime.Framework.dll`，`ForgeMap.dll` + `ForgeMap.Native.dll`，`ForgeWeapon.dll` + `ForgeWeapon.Native.dll`，全部取同一次构建。宿主模式设为 Play（非 Off）。
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
| 10 开火：主机 | 主机玩家手持武器**单发**打一枪（不要连发、不要霰弹枪，便于逐发计数） | 恰好一行 `weapon.shot-fact equipment=gtfo.equipment:<W>.<n> index=<k> status=… code=…`：`<n>` 是主机自己那把武器的装备生命，`index` 从 1 开始、每打一枪 +1。行数记成"1 / 0 / 多"：0 行说明玩家自己的 `Fire` 体没有开火窗口（未核验项），多行说明同一发被记了两次（缺陷） |
| 11 开火：客机 | 一名客机玩家手持武器单发打一枪；主机与客机各看自己的 `LogOutput.log` | 主机：恰好一行 `weapon.shot-fact equipment=<客机那把武器的生命> index=<k>`，事实里的 `source` 是客机的 `gtfo.player:<m>`；客机：不应出现 `weapon.shot-fact`（`Authoritative()` 要求 `SNet.IsMaster`，客机不发布）。主机 0 行即说明同步副本的 `Fire` 体重放链没有跑起来，原样记下（这是本轮列出的运行期未核验项） |
| 12 哨戒 | 部署一台哨戒，让它自己开火（玩家不开枪） | 不应出现 `weapon.shot-fact`；哨戒的射线写 `weapon.hit-outside-shot` 且不产生 `hit_candidate`。若在玩家刚开过火的窗口里出现哨戒的 `weapon.hit-fact`，说明它被记到那名玩家仍开着的开火窗口上（本实现的已知口径：命中只认领最近一次开火），原样记下而不是判为失败 |
| 13 部件姿态 | 给"当前正拿着的那个装备块"写一份 `plugins/<包目录>/forge/gear-parts/<blockId>.json`：只配一个槽位（例如 `StockPart`）的 `localPosition` 与 `enabled: false`，`parts` 里只放这一条 | 启动时 `weapon.gear-part-blocks-loaded count=1`。五种场合各看一次，每次该槽位都应移位或消失：①本地第一人称手持；②另一名玩家的第三人称模型（需第二名玩家或 bot 持同一把枪）；③菜单里的装备预览；④背包/装备栏的图标渲染；⑤部署出去的哨戒（部署物的部件槽位同一套）。每种场合出生时都写一行 `weapon.gear-part-applied block=<blockId> parts=1`，一场里出现多行是正常的（图标与预览会反复重建 holder）。若某个场合没有变化，先查那行 applied 是否出现，再记下场合与有无该行 |
| 14 检查点重载 | 在有检查点的关卡团灭或重开检查点，再举手看一次同一把枪 | 部件仍停在文件写的绝对位置（`weapon.gear-part-applied` 再出现一次，`weapon.gear-part-limit` 一次都不出现）。若第二次变成偏移翻倍，说明写被当成增量，按失败保留日志 |
| 15 原动画是否覆盖 | 在同一把枪上让部件动起来（换弹、开火、检视），观察第 13 步的位移是否被拉回原位 | 这是尚未核验项：`GearPartHolder` 自己还有 `Update`（dump.cs 第 614843 行），本轮没有解它的方法体。**不论结果如何都原样记下**（保持 = 文件生效且不被覆盖；被拉回 = 需要在写之后再加一次，或挂到别的点）。图标与菜单预览同理各记一次 |
| 16 与同类部件模组同装 | 在隔离 profile 里同时装一个同样改部件姿态的模组（例如 `GearPartCustomization`），两者的文件指向**同一个块** | 本轮不保证先后顺序（两边都是 `OnAllPartsSpawned` 的 postfix，实际顺序由 Harmony 决定）。记下"谁的姿态最终可见"，以及是否出现互相覆盖的抖动。这一条是给策展方的知会，不是失败判据 |

全程任何一步出现 `Weapon equipment observation disabled until restart`、`weapon.wield-fact-rejected` 或持续的 `weapon.owner-unresolved`（玩家已有 `map.player-life-started` 之后仍出现），都视为该步失败并保留日志。第 10、11 步的 `weapon.shot-fact` 行数按"恰好一行"判定：0 行或 2 行以上都要保留日志并逐步写明。第 13–15 步的部件文件由测试者手写，坏文件的码分别是 `weapon.gear-part-json|id|parts|component|number|path|limit|size|unreadable|duplicate-block`，文件路径写全相对路径；第 13 步里一次 `weapon.gear-part-mismatch` 或 `weapon.gear-part-child-missing` 都说明这份手写文件与真实部件对不上，先改文件再判结果。结果回填时逐步写"符合 / 不符合（附日志行）/ 未执行"。

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
