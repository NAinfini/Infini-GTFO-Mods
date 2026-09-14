# ForgeMap

Map 与 Room 的空间、设备、任务、遭遇、玩家生命流程与进程、世界表现，**以及地图生成的底层逻辑**。

计划与状态见两仓统一框架第 6 节 U-MAP-MOD（链接见[仓库 README](../README.md)），带日期的验证记录见 [VALIDATION.md](VALIDATION.md)。

## 地图生成归本包

地图生成的底层逻辑归 ForgeMap，不由各个房间包各自实现。近期只用原版 geomorph（框架 D-010、D-012）：自定义地图生成照做，第三方房间包的模型、资源侧 Adapter、素材提取与 geometry pin 暂缓。ForgeMap 生成还要完全接替 LGTuner 现在承担的两件事：同一区域多个房间按顺序放置、加载额外环境资源（D-013）。

归属与接口形状见 [GENERATION-SPEC.md](GENERATION-SPEC.md)：生成步骤 G0–G10 各自的输入输出与归属、资源描述符与正反 fixture、运行期调用链、游戏内验证步骤，以及需要网站与其他单元提供的最小接口。**这些目前是计划、fixture 形状和原生签名证据，没有生成器的 C# 实现。**

## 运行清单与 MAP1 内部身份层

**运行清单没有 capability 或 binding。** `ModuleDefinition.Create()` 的 capabilities 与 bindings 都是空数组，没有地图 Action。唯一对外的运行期能力是 MAP5a 的 `gtfo.player` 实体 resolver 与原生实例解析器，它们在独立的 `Native/ForgeMap.Native.csproj` 插件里，见下一节。

已经在生产程序集内的是内部身份层，只有显式构造 Session 才登记这一空 provider 并订阅生命周期，不会自动加载：

`MapIdentityContracts.cs` 定义域内地址、精确来源锁、内部 native key、不含指针的快照，以及按创建尝试颁发的票据。`MapObjectIdentityIndex.cs` 管有界关联与历史、world/generation/life 隔离、重复回调、取消、销毁、冲突隔离和观测缺口。`MapIdentitySession.cs` 消费真实的 R2 生命周期，做所属线程检查、精确生命探针、重入与失效检查以及注销。这些类型都是 `internal`，通过限定的 friend assembly 由测试访问实际程序集，不跨目录链接源码。

地址逐项区分 layout 与 revision、dimension、layer、local zone、placement、对象 ID 和对象类别。同一 geomorph 的多个 area 使用各自明确的对象 ID；同资源的两个 placement 不合并。**地址不能通过名字、位置、遍历顺序或预览 GLB 猜测**；提供地址的原生适配器仍待核验。来源锁保留 resource ID 与 revision、来源证据 SHA-256，以及已有 `SourceObjectIdentity` 的 file 与 pathId。

MAP1 也交付了可重复运行的原生 API 与字节证据检查工具：本机 build 的 32 个类型、110 个方法的元数据锁，以及旧审计的 11 个原生区域字节复核。MAP2 定范围时另锁定了生成相关的 27 个类型、59 个方法（`tools/generation-api-targets.json`）。**这些是 metadata-only 证据，不证明原生调用语义、合法阶段或复制完成。**

## MAP5a 玩家实体身份（implementation-only）

从 MAP5 切出的先行切片，目的是给 Weapon 等领域提供经核验的玩家引用。MAP5 其余内容（生命状态映射、救起与重生、落点、检查点）未开始。

`Native/ForgeMap.Native.csproj` 是独立项目，引用真实 interop 编译，不进入 `ForgeMap.dll`：
- `Plugin`：BepInEx 插件 `NAinfini.ForgeMap` / `Infini Forge Map` / `0.1.0`，依赖 `NAinfini.ForgeRuntime` 1.2.0。宿主 Off 时不注册、不装 Hook；Load 只允许一次，`Unload()` 返回 false。本包自己的 cfg 是 `BepInEx/config/NAinfini.ForgeMap.cfg`，D-007 的 `[Logging] Level` 取 `off`、`error`、`info`，默认 `error`，改动需重启，非法值在注册前抛错；该级别随注册交给内核，成为 `forge.module.gtfo.map` 这个 provider 自己的级别。发布身份沿用现有命名模式，**未经确认，没有清单或打包**。
- `MapPluginSession`：与 Enemy 相同的顺序。注册窗口内先注册模块（重复 provider 或 `gtfo.player` 命名空间冲突在装 Hook 前原子失败），再装 Hook；失败回滚先卸 Hook 再注销并保留原始异常；回调异常锁存故障并清表；释放时先注销再卸 Hook；所属线程检查。
- `PlayerIdentityModule`：在 `ModuleDefinition.Create()` 上只追加 `EntityResolvers["gtfo.player"]` 与 `EntityInstanceResolvers["gtfo.player"]`，不注册 observer、capability 或 binding，也不另造 provider。
- `MapNativeHooks`：2 个 `Priority.Last` postfix，只决定何时读回、不读参数：`PlayerManager.OnPlayerSpawned` 与 `PlayerManager.OnPlayerDespawned`。两者都是非虚方法，由 `PlayerReplicationManager.OnSpawn` / `OnDeSpawn` 调用，本地、远端与 bot 玩家都经过这里（静态调用边见证据文件）。选这两个是因为它们是所有玩家生成与销毁的共同汇合点：`RegisterPlayerAgent` 与 `PlayerSync.OnSpawn` 没有直接调用者（接口派发），`PlayerAgent.Setup` / `OnDespawn` 是被 `LocalPlayerAgent` 覆盖的虚方法。

身份规则：
- 每次读回遍历 `PlayerManager.PlayerAgentsInLevel`；只登记 `agent.Owner` 非空且 `owner.PlayerAgent` 回指同一 agent 的条目。内部字典键是 `SNet_Player.Lookup`。
- **隐私硬规则**：真实玩家的 `Lookup` 是 Steam64 账号 ID，只作 Map 私有字典键，不格式化、不序列化，不进入实体 ID、日志、错误、observer 或测试证据。`MapNativeAdapter` 断言全部日志、清单与报告不含任何 fixture Lookup，`MapNativeLayout` 静态检查原生程序集从不格式化或装箱 `UInt64`。
- 引用 `gtfo.player:<n>`：`n` 是本世界内按首次登记顺序递增的编号，与 Enemy / Weapon 一样由接线计数，不来自槽位；同一玩家在本世界内保持同一编号，WorldEpoch 为内核当前世界，换世界后重新编号。agent 指针变化或 SNet_Player 对象变化即分配新的单调 lifeEpoch，并记 `map.player-life-started id=gtfo.player:<n> world=… life=… bot=true|false`；消失、替换或同键冲突记 `map.player-life-ended … reason=despawned|replaced|key-conflict`。
- 倒地、救起、Heal、传送不替换 agent 就不改 life。检查点重载由宿主暂停并换世界，模块只在 WorldChanged / Failed / Stopped 清表。
- 只在 Runtime Ready、会话未故障且 `SNet.IsMaster` 时分配。**不用 InLevel 玩法门**：电梯阶段的生成与进关同属一个世界。
- owner 解析不到或互链不成立时不登记，并对每个 agent 警告一次 `map.player-owner-unresolved`；同一玩家键出现两个 agent 时两者都不登记（`map.player-key-conflict`，警告不带键）。不按名字、槽位顺序或指针推断身份。
- `IsCurrent` 每次重读：world、前缀、无符号十进制编号、精确引用、agent 未销毁且指针相同、SNet_Player 指针与内部键不变、owner 与互链仍成立。
- 原生实例解析（其他模块经 SDK 的 `ResolveEntityInstance("gtfo.player", player)` 调用）只接受 `SNet_Player`，门槛与读回相同（已注册、Runtime Ready、会话未故障、`SNet.IsMaster`）。它按 SNet_Player 指针在已登记表里查找，再经上面的 `IsCurrent` 复核后返回该条目的引用；**只查不写**：不遍历 `PlayerAgentsInLevel`、不读 `Lookup`、不分配编号或 life。未登记、互链失效或门槛关闭都返回 null，由调用方决定不记录。

证据文件 `evidence/map5a-player-hooks.json` 锁定：两个 Hook 的签名、非 virtual、dump RVA（唯一且不共享、位于可执行段），5 个读回成员的签名，7 条直接调用边。

已知未核验：
- bot 的 `Lookup` 是否跨重生稳定。
- postfix 时刻 `PlayerAgentsInLevel` 是否已包含新 agent、是否已移除旧 agent（`OnDeSpawn` 先调用 `UnregisterPlayerAgent` 再调用 `OnPlayerDespawned` 只是静态调用边，运行时顺序未证）。
- 倒地与救起确实不重建 PlayerAgent。
- `Object.Destroy` 延迟销毁期间旧 agent 的可见状态。
- 迟加入玩家与主机迁移路径。

与 MAP1 的关系：`MapIdentitySession` 也登记 `forge.module.gtfo.map`，两者不能同进程并存；MAP1 原生适配器接线时必须合并为同一个 provider 生命周期。Weapon 的装备 owner 经上面的原生实例解析取得，Weapon 插件因此依赖 `NAinfini.ForgeMap`，但不引用 ForgeMap 程序集，见 [ForgeWeapon README](../ForgeWeapon/README.md#原生观察接线implementation-only)。

## MAP2 G0 计划发现（D-013 过渡期）

`AssemblyPlanDiscovery`（游戏无关程序集）做一次只读发现：枚举 `BepInEx/plugins/` 的一级目录，找出**恰好一个**含 `forge/maps/` 的包，读该包的 `forge/maps/rooms.descriptors.json` 与每份 `<planId>.assembly.json`，按框架 §3.2 I-MAP-PLAN 的 G0–G6 顺序跑已有静态检查，每个计划文件产出一条诊断。**过渡期只做静态检查与诊断**：不生成地图、不改游戏状态、不注册 provider、不读网络；一份计划被拒不影响同包其他计划，也不影响 Runtime 启动。通过不等于生成成功，合法计划照常带 blockers。

- 恰好一个包：没有目录含 `forge/maps/` 时静默跳过（与 I-PACK 计划发现同一条：不打开文件、不报错、不写日志）；多于一个全部拒绝 `assembly.package-layout`。
- 包内必须有 `forge/maps/rooms.descriptors.json`，缺失按 `assembly.package-layout` 拒绝（与 `tools/verify_assembly_plan_fixtures.py`、`tests/MapAssemblyPlan` 同码同路径）。描述符文档对每个计划都是拒绝顺序的第 1 条，先于计划文档校验。
- 只处理文件名按 ordinal 精确匹配 `.assembly.json` 的文件，其他文件静默忽略；文件名前缀必须等于文档里的 `planId`，否则 `assembly.plan-file`。
- 每个 `levelLayoutId` 只允许一份计划，第二个声明同一 id 的文件报 `assembly.duplicate-level-layout`；计划的码与 `$` 路径与两个检查器逐字相同（首错即停）。

原生接线只有 `Native/MapPlanDiagnostics`：插件 `Load` 在注册身份后调用一次（`Unload()` 仍返回 false，无热重载），每个计划一条有界（512 字符）诊断行写到 BepInEx 日志：

| 行首 | 级别 | 字段 |
| --- | --- | --- |
| `map.plan-accepted` | Info | `plan=<planId> path=<BepInEx 相对路径> blockers=<按 ordinal 排序>` |
| `map.plan-rejected` | Error | `plan=<planId 或 -> code=<assembly.*/descriptor.*> path=<BepInEx 相对路径> at=<$ 路径>` |
| `map.package-rejected` | Error | `code=assembly.package-layout path=<BepInEx 相对路径>`；包级拒绝时没有计划行 |

路径形如 `plugins/<Team-Pkg>/forge/maps/<planId>.assembly.json`。这些行**还不是** I-DIAG 的 `forge.log.v1` 记录：`plan.loaded` / `plan.rejected` 归 Runtime sink，现在只是 BepInEx 日志诊断。Map 的 cfg `Logging.Level`（D-007 阶段 A）已随注册进入内核级别表，本阶段还没有记录点用它。

`tests/MapPlanDiscovery` 用 `$env:TEMP` 下合成夹具覆盖无目录、空目录、合法与非法计划（多个错误码）、非计划文件、多包与重复 level layout；包级规则只有一处实现，`tests/MapAssemblyPlan` 的包级用例与 `tests/MapNativeAdapter` 的接线用例都走它。

## 边界

没有真实创建适配器、地图 Action、生成器、资源 Adapter 或玩家可用发行物；唯一的游戏 Hook、对外 resolver 与原生实例解析器是上面 MAP5a 的 implementation-only 玩家身份。`tests/fixtures/native-identity-scenarios.json` 的十个原生身份规格只执行了托管替身部分（MapIdentity 按用例 id 标记），原生部分仍然 `nativeExecuted: false`；报告里的 `nativeGameExecuted`、`nativeHooksInstalled` 和 `gameplayBindingsRegistered` 都是 false。没有原生创建、主客机、恢复或导航执行。

所有写世界的动作必须由明确的权威提交。查询与生成批次使用稳定排序、实际 seed、显式预算和完整性结果；缺少合法落点、导航证据或目标 receiver 时返回原因，**不静默换目标**。

Room 不另建独立运行包，资产引用使用统一资源协议。已有的世界与生成观察在 Runtime 与 Development 的诊断代码里，**诊断观察不是可调用的地图执行器**。

## 复跑

从仓库根运行，输出目录必须尚不存在且位于 `ForgeMap` 内：

```powershell
python ForgeMap/tools/run_identity_checks.py --out ForgeMap/evidence/identity-recheck-01
```

这个 runner 同时运行 MapIdentity、原有的 MapContracts 与 Architecture，构建产物隔离在 `ForgeMap/bin`，并保存命令、退出码、stdout/stderr、测试 JSON、DLL 哈希和源码摘要。构建失败不会退回去运行旧 DLL；检查期间相关源码发生变化时，最终结果不会标为通过。

只跑 SDK 消费方测试：

```powershell
$artifacts = Join-Path (Resolve-Path ForgeMap) 'bin/map1-artifacts'
dotnet build ForgeMap/tests/MapContracts/MapContracts.csproj -c Release --artifacts-path $artifacts
dotnet "$artifacts/bin/MapContracts/release/MapContracts.dll"
```

MAP5a 原生玩家身份。`$bep` 是只读 BepInEx 目录，interop 身份只有一个冻结来源：`Forge-MapEditor-QA` profile 的 `BepInEx`（`evidence/map5a-player-hooks.json` 的 sha256 与 MVID 就是该副本的实际值）；`$game` 为 GTFO 根目录，`$dump` 为同 build 的 dump.cs；先构建宿主取得 `ForgeRuntime.dll`：

```powershell
$bep = "$env:APPDATA/r2modmanPlus-local/GTFO/profiles/Forge-MapEditor-QA/BepInEx"
$a = '<new-empty-artifacts-dir>'
dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release --artifacts-path $a "-p:GTFOBepInExPath=$bep"
dotnet build ForgeMap/Native/ForgeMap.Native.csproj -c Release --artifacts-path $a "-p:GTFOBepInExPath=$bep" "-p:ForgeRuntimeAssembly=$a/bin/ForgeRuntime/release/ForgeRuntime.dll"
dotnet build ForgeMap/tests/MapNativeAdapter/MapNativeAdapter.csproj -c Release --artifacts-path $a
dotnet "$a/bin/MapNativeAdapter/release/MapNativeAdapter.dll" "$a/reports/map-native-adapter.json"
dotnet build ForgeMap/tests/MapNativeLayout/MapNativeLayout.csproj -c Release --artifacts-path $a "-p:GTFOBepInExPath=$bep"
dotnet "$a/bin/MapNativeLayout/release/MapNativeLayout.dll" $bep "$a/bin/ForgeRuntime.Framework/release/ForgeRuntime.Framework.dll" "$a/bin/ForgeRuntime/release/ForgeRuntime.dll" "$a/bin/ForgeMap/release/ForgeMap.dll" "$a/bin/ForgeMap.Native/release/ForgeMap.Native.dll" ForgeMap/evidence/map5a-player-hooks.json "$a/reports/map-native-layout.json"
dotnet build ForgeMap/tests/MapNativeEvidence/MapNativeEvidence.csproj -c Release --artifacts-path $a "-p:GTFOBepInExPath=$bep"
dotnet "$a/bin/MapNativeEvidence/release/MapNativeEvidence.dll" $bep $game $dump ForgeMap/evidence/map5a-player-hooks.json "$a/reports/map-native-evidence.json"
```

MAP2 G0 计划发现（游戏无关的托管发现流程，`$env:TEMP` 下合成夹具，不读游戏）：

```powershell
$artifacts = Join-Path (Resolve-Path ForgeMap) 'bin/map2-discovery-artifacts'
dotnet build ForgeMap/tests/MapPlanDiscovery/MapPlanDiscovery.csproj -c Release --artifacts-path $artifacts
dotnet "$artifacts/bin/MapPlanDiscovery/release/MapPlanDiscovery.dll"
```

资源侧 Adapter 描述符 fixture（只读 JSON，不加载资源或游戏程序集）：

```powershell
python ForgeMap/tools/verify_resource_adapter_fixtures.py
```

原生证据的复采命令见 [VALIDATION.md](VALIDATION.md) 与 [GENERATION-SPEC.md](GENERATION-SPEC.md#5-fixture-与本地检查)。测试目录的边界说明见 [MapContracts](tests/MapContracts/README.md) 与 [MapIdentity](tests/MapIdentity/README.md)。

当前 DLL 不是玩家发行物；发布身份、真正加载、主客机和恢复都待实施验证。InfiniTweaks 的 QoL 行为不属于本模块。
