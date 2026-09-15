# ForgeMap 地图生成与资源侧 Adapter 规格

版本 2026-09-14，对应批次 MAP2 重新定范围（U-MAP-MOD）。上位依据是两仓统一框架（网站仓库 `Docs/forge-contract/FORGE-FRAMEWORK.md`）的 U-MAP-MOD 与 D-010、D-012、D-013。本文件只定义归属、接口形状和验收方法；第 4 节的新方向与第 6 节的验证步骤都停在"计划"等级，没有实现，也没有任何游戏执行。

证据等级沿用框架 §2.1：计划 → 实现 → 本地验证 → 联调验证 → 浏览器验证 → 游戏验证。原生 API 的"签名存在"写作 metadata，不等于调用语义、合法阶段或复制行为已核验。

## 0. 现行方向（2026-09-14 决定）

1. **不做 G7 精确拼接生成。** 原 G0–G10 生成步骤、I-MAP-PLAN 拼装计划、原版房间描述符，以及模组侧 G0–G6 静态检查（`AssemblyPlanDiscovery` / `AssemblyPlanReader` / `AssemblyPlanContracts` / `AssemblyPlanChecks`、原生 `MapPlanDiagnostics` 与 `tests/MapAssemblyPlan`、`tests/MapPlanDiscovery`）全部取消并删除，本文件对应章节已移除；历史运行记录见 [VALIDATION.md](VALIDATION.md)。
2. **LGTuner 长期保留。** 区域内房间选择顺序与额外环境资源加载继续归 LGTuner，不再计划由 ForgeMap 替代（D-013 的"生成完全替代 LGTuner"目标作废）。
3. **新方向（待游戏内可行性原型）**：房间结构全部在网站编辑器里拼（只用游戏已有零件），导出为数据；ForgeMap 在游戏里用游戏自带素材搭建该房间，并登记为可用 geomorph（候选接口 GTFO-API `AssetAPI.RegisterAsset` / `PrefabAPI`，**未核实**），再由 ComplexResourceSet + `CustomGeomorph` / LGTuner 使用。兜底路线：作者上传模组包，网站提取房间进社区（D-012）。
4. 所有测试放到最终阶段统一跑。

## 1. 现在谁在生成地图

| 执行者 | 做了什么 | 依据 | 证据等级 |
| --- | --- | --- | --- |
| 原版 `LG_Factory` | 按 LevelLayout / ComplexResourceSet DataBlock 分批（`LG_Factory.BatchName` 0–76）完成区域扩张、geomorph 选择与摆放、area / plug / gate、AIGraph、NavMesh、剔除和最后的逻辑链接 | `evidence/map2-scope-2026-09-13/generation-api.json`：27 个类型、59 个方法签名 | metadata |
| MTFO 4.6.3 | 加载自定义 DataBlock 与内容；网站导出把它列为依赖。它本身不做摆放决策 | 网站依赖清单 | 计划（未反编译核对） |
| LGTuner 1.2.7 | 按 zone 覆盖 geomorph 选择与方向；网站 `map-native-rules.ts` 输出 `ZoneOverrides[].Geomorphs[]`，摆放仍由原版生成器完成。**长期保留**：区域内房间选择顺序与额外环境资源加载继续由它负责 | 网站导出代码 | 浏览器 / 本地（仅导出格式） |
| Zone_Randomizer 1.4.1、MushroomSeedFixed 1.2.1 | `deterministic-layout` 机制的来源：种子与房间随机化 | `catalog/mechanism-blueprints.json`，`upstreamApiVerified: false` | metadata-triaged |
| EOSExt_ExtraDoor 1.0.1、DoubleSidedDoors 0.8.1 | `extra-door-topology` 机制的来源：额外门与双面门 | 同上 | metadata-triaged |
| CheeseGeos 0.5.8、FlowGeos 0.9.4、ZaeroGeos 0.6.0 等 Geo 包 | 提供 geomorph prefab 资源；如何被原版流程加载尚未逐包核验 | 网站 `forge/resource-adapters.ts` 首批 pin（运行期阻塞 `native-resource-binding-unverified`） | metadata |
| 网站编辑器 | 编辑期选模块、`snapRoomToPortal` / `orientTemplateToPortal` 预览吸附、renderer AABB 布局元数据、出生候选打分预览。**导出不含摆放坐标**，renderer AABB 明确不是地板、碰撞或导航 | 网站源码与导出 README | 浏览器验证（仅编辑期行为） |
| ForgeDevelopment | `GenerationHooks` 观察 `LG_Factory.Setup` / `FactoryDone` 等；`WorldInspection` 用 `NavMesh.SamplePosition` / `CalculatePath` 做诊断 | Development 源码 | 诊断观察，不是执行器 |
| ForgeMap | 只有内部身份层（地址、来源锁、创建票据、生命隔离） | `tests/MapIdentity` | 本地验证（托管） |

结论：今天没有任何一方持有"按玩家拼装结果决定摆放并保证合法"的逻辑，这条路已经取消。LGTuner 一类只改原版生成器的输入，摆放、碰撞与导航都在原版流程里隐式发生，没有可追踪的失败原因；新方向因此不再自建拼接流程，只把房间做成原版流程能直接使用的 geomorph。

## 2. 归属

**房间怎么搭、搭成什么形状归 ForgeMap**；Unity / GTFO 的原生构建原语（实例化、AIGraph、NavMesh 烘焙、剔除）仍是引擎调用，由 Map 编排而不是重写。新方向下 Map 不注入 `LG_Factory` 批次，也不做精确拼接摆放：房间以"已登记的可用 geomorph"身份交给 ComplexResourceSet 与原版流程。

采样成功不能记作房间可用，登记成功也不能记作生成成功；只有房间被原版流程实际选取并使用，才算接入。

## 3. 资源侧 Adapter 接口形状

Adapter 只回答"这个资源在已加载的游戏里是什么"。一个 Adapter 对应一个**已核验的资源协议（profile）**，不对应包名。首个候选 profile 是 `gtfo.complex-resource-geomorph`（revision 1）：通过 ComplexResourceSet 加载的 geomorph prefab。它仍是候选，要到第 6 节 B 组游戏验证后才能用于生成。

新接一个 Geo 包时，如果它走已核验的 profile，只需提供该包的描述符（由网站编辑期提取），不写代码；只有出现新的加载协议才新增 Adapter。

### 3.1 描述符文档

顶层 `{schemaVersion: 1, kind: "forge-map-resource-descriptors", evidence, sharedBytes, descriptors}`。`evidence` 只能是 `fixture-synthetic` 或 `extracted-metadata`——静态描述符**不能**自称 game-verified，那属于运行期回执。所有对象字段都必需，未知字段拒绝。

每个描述符包含：

| 字段 | 内容 | 规则 |
| --- | --- | --- |
| `reference` | `{id, revision}` | package 来源：`id` 按网站 `importedResourceReference` 从 pin 名、bundle、file、pathId 推导，`revision` = 包 archive SHA-256；native 来源：`revision` = 内容 SHA-256，`id` 由网站分配（现有写法 `forge.native.room:<templateId>`） |
| `adapter` | `{profile, profileRevision}` | 必须是已登记的 profile；包名不是 profile |
| `source` | package：`{packagePin, archiveSha256, bundle, assetPath, object{file, pathId}}`；native：`{sourceId, contentSha256, evidence{path, sha256}, assetPath, object}` | `pathId` 是 int64 十进制字符串，不能是 JSON 数字 |
| `authorization` | `{reviewRef, runtimeUse, redistributeBytes, dependency}` | package：`installed-package`，`dependency` 必须是来源 pin；native：`game-content`，`dependency: null`；`redistributeBytes` 恒为 false |
| `runtimeLocator` | `{loadedBy: "complex-resource-set", assetPath, requiresLoaded: true}` | Adapter 只取已加载资源，不自行加载 bundle |
| `hierarchy` | `{space: "unity-left-handed-meters", nodes[]}` | 节点 `{id = file:pathId, parent, name, source, gameObject, local{position, rotation, scale}, active, meshes[]}`；唯一根且等于 `source.object`；无环；四元数归一；scale 非零；上限 50000 |
| `areas` | `[{id, node}]` | node 必须在层级内 |
| `connectors` | `[{id, node, area, expanderType: plug/gate, position, outward, doubleSided}]` | `area` 必须存在；`outward` 为单位向量 |
| `colliders` | `{status: source/unknown, reason, items[{node, shape, evidence: "collider-component"}]}` | renderer / 预览包围盒不是碰撞证据 |
| `navigation` | `{status: build-time/unknown, reason, items[{node, kind: node-volume/navmesh-source}]}` | 描述符不能声明 `ready`；就绪是运行期事实 |
| `occlusion` | `{status: source/unknown, reason, items[{node, kind: culling-portal/occluder, area}]}` | |

任一空间块为 `unknown` 时必须给 `reason` 且 `items` 为空；该描述符仍合法，但产生对应的 `generationBlockers`（`colliders` / `navigation` / `occlusion`），消费方必须据此拒绝，而不是当作"没有碰撞"。

`sharedBytes` 是 `[{key = kind:sha256, kind: mesh/texture/material, sha256, attributes?}]`。字节可以按 key 在多个资源间去重，**资源身份、授权与实例不去重**：同一 `id@revision` 在一个文档里出现两次被拒绝。mesh 的 POSITION / NORMAL 声明分量数；四分量时 `fourthComponent` 必须是 `custom-scalar`（保留自定义标量通道，不做齐次除法也不丢弃）。

### 3.2 错误码

`descriptor.schema`、`unknown-field`、`reference`、`unsupported-profile`、`source`、`path-id`、`revision-mismatch`、`reference-derivation`、`authorization`、`runtime-locator`、`hierarchy`、`node-identity`、`duplicate-node`、`root-mismatch`、`transform`、`shared-bytes`、`shared-bytes-missing`、`vector-channel`、`area`、`connector`、`connector-area`、`connector-orientation`、`navigation-state`、`unknown-reason`、`collider-evidence`、`evidence-escalation`、`duplicate-resource`。

### 3.3 运行期 Adapter 的两个操作

1. **describe**：给出 3.1 的描述符。可以是网站提取后下发，也可以是运行期读取元数据；两种来源结果必须一致，冲突即拒绝。
2. **acquire**：在生成窗口内，按 `runtimeLocator` 从已加载资源里取得 prefab 对象，并核对它的 file / pathId 与描述符根节点一致；不一致或未加载时返回明确失败，不去别的 bundle 里按名字找。

Adapter 不做的事：选择摆放、计算碰撞或连通、声明 NavMesh 就绪、加载或复制资源字节、执行上传的 DLL。

**运行期 Adapter 仍不创建 C# 类型。** 目前没有消费方，也没有可在游戏里核验的 acquire 实现；按规则不写空接口或占位 Adapter。描述符形状与第 5 节的 fixture **保留**，D-012 兜底路线（作者上传模组包、网站提取房间）可能复用它；随 G0–G6 取消删除的是把描述符接到拼装计划上的静态检查实现。C# 形状在 MAP-ADAPTER 开始、第 6 节 B 组有结果时再定。

## 4. 新方向的运行期调用链（计划等级，未执行）

1. 网站编辑器里用游戏已有零件拼房间，导出为数据；格式待定（I-MAP-PLAN 已取消，不由本文件定义）。
2. ForgeMap 读取该数据，在游戏里用游戏自带素材搭建该房间。
3. 把搭好的房间登记为可用 geomorph：候选接口是 GTFO-API 的 `AssetAPI.RegisterAsset` / `PrefabAPI`，**未核实**；登记失败即停止，不按名字或包名兜底猜测。
4. 由 ComplexResourceSet + `CustomGeomorph` / LGTuner 按现有方式选取与加载。区域内房间选择顺序与额外环境资源加载仍归 LGTuner。
5. 主路线不可行时走兜底路线：作者上传模组包，网站提取房间进社区（D-012）。
6. 世界结束或重建时按身份层现有规则注销引用，旧回调按 stale-world / stale-generation 拒绝。

这条链路只到"房间成为原版流程可用的 geomorph"为止；摆放、AIGraph、NavMesh 与剔除照旧由原版流程完成，ForgeMap 不重写也不预估它们的结果。

## 5. Fixture 与本地检查

资源侧描述符：`tests/fixtures/resource-adapter/`，由 `cases.json` 列出。合法 3 个：`valid/package-geomorph.json`（无阻塞）、`valid/shared-mesh-distinct-identities.json`（两个资源共享 mesh 与 material 字节但身份独立）、`valid/native-geomorph-unknown-spatial.json`（碰撞 / 导航 / 遮挡未知 → 三个阻塞）。非法 21 个，每个只含一个错误并指定期望错误码。数据全部是合成的（`Fixture_AuthorA-FixtureGeos-1.0.0`、重复字符哈希），不代表任何真实包。

```powershell
python ForgeMap/tools/verify_resource_adapter_fixtures.py
```

生成相关原生签名锁（metadata 证据，保留）：

```powershell
$m = (Resolve-Path ForgeMap).Path
$out = "$m/bin/generation-api-$([Guid]::NewGuid().ToString('N')).json"
& "$m/tools/Capture-NativeApi.ps1" -BepInExRoot "$env:APPDATA/r2modmanPlus-local/GTFO/profiles/Forge-MapEditor-QA/BepInEx" -GameRoot 'E:/SteamLibrary/steamapps/common/GTFO' -OutFile $out -TargetsFile "$m/tools/generation-api-targets.json"
python "$m/tools/verify_native_api.py" "$m/evidence/map2-scope-2026-09-13/generation-api.json" $out --self-test
```

实际执行结果记录在 [VALIDATION.md](VALIDATION.md)。

## 6. 游戏内验证步骤

以下都未执行。每一步保存 build、seed、依赖锁与日志；诊断 Hook 来自 Development D3，Map 不私自跨目录引用。

**A. 原版构建时间线与新方向原型（第 4 节的前提，未执行）**
1. 在 Temp profile 装原版关卡 + Development 诊断，订阅 `LG_Factory.add_OnFactoryBatchDone`，记录一次完整构建的批次序列与每批耗时（主机与客户端各一次）。
2. 记录 geomorph prefab 首次实例化、`LG_Plug.Pair`、`LG_BuildUnityGraphJob.NavmeshDone`、`LG_BuildAIGraphJob_End.Build` 分别发生在哪个批次。
3. 按第 4 节把编辑器导出的一个房间在游戏里用自带素材搭起来，尝试登记为可用 geomorph（候选接口 `AssetAPI.RegisterAsset` / `PrefabAPI`，未核实），确认 ComplexResourceSet + `CustomGeomorph` / LGTuner 是否真的把它当普通 geomorph 选取与加载。
4. 结论决定主路线是否可行；不可行则改走兜底路线（作者上传模组包、网站提取房间进社区，D-012），把结果写回本文件。

**B. `gtfo.complex-resource-geomorph` profile 核验（MAP-ADAPTER 的开始条件；第三方 Geo 包按 D-010、D-012 暂缓，B 组保持未执行）**
1. 装 CheeseGeos 0.5.8 + MTFO，使用引用其 geomorph 的 ComplexResourceSet。
2. 在 `LG_LoadComplexDataSetResourcesJob.ComplexAssetBundleLoaded` 之后，按描述符 `assetPath` 取对象，记录其 file / pathId 与网站提取值是否一致。
3. 实例化后比对 `LG_Geomorph.m_areas` / `m_plugs` 数量与描述符 `areas` / `connectors`，比对 plug 的 `m_dir` 与 `outward`。
4. 对 ZaeroGeos 0.6.0 和第三位作者的包重复 1–3；三家都一致才把 profile 标为游戏验证。

**C. 十个原生身份规格（MAP1 遗留）**：每个用例按 `tests/fixtures/native-identity-scenarios.json` 的 `nativeOnly` 字段执行：
1. same-zone-index：选一个两维度都有 local index 相同 zone 的关卡，记录每个 zone 的 DimensionIndex / Layer / LocalIndex 与创建 Hook 给出的地址。
2. same-resource-placements：同一 geomorph 放两次，确认两次实例化各自带票据、`MapCreationObservation` 地址不同。
3. generation-reentry：进关后重开（或迟加入触发重建），确认旧批次回调在新 world epoch 下被拒绝。
4. same-native-id-new-life：多次重开同一关，记录指针与 Unity instance ID 是否复用，确认旧引用不解析。
5. duplicate-observation：统计同一对象被多少个 Hook 报告，确认只产生一次分配。
6. ambiguous-token：找一个一次创建产生多个候选对象的步骤（如 custom geomorph 子 area），确认进入隔离而不是取第一个。
7. unknown-source：原版或第三方直接生成、未经 Forge 票据的对象，确认记录 UnknownSource 缺口。
8. revision-mismatch：装与锁不同版本的 Geo 包，确认拒绝而不是静默升级。
9. incomplete-observations：用低观测上限跑整关，确认报告缺口而不是"不存在"。
10. area-course-node：对多 area 的 geomorph，读取每个 `LG_Area.m_courseNode` 并做 pCourseNode Set / TryGet 往返，确认归属一致。

**D. NavMesh 就绪**：在 `NavmeshDone` 之前与之后各做一次 `NavMesh.SamplePosition`，确认之前的结果不被发布为合法空间。

**E. 主客机**：同一房间数据与 seed 在主机和客户端分别记录房间搭建结果的 geomorph、实例地址与拓扑，逐项相等。

## 7. 需要其他单元提供的最小接口

| 提供方 | 最小需求 | 证据 |
| --- | --- | --- |
| 网站 | 导出编辑期拼好的房间结构数据（只用游戏已有零件，格式待定；I-MAP-PLAN 已取消，需重新定范围） | 框架 §6 U-MAP-WEB 尚未完成 |
| 网站 | 按第 3.1 节输出资源描述符（提取器归网站），D-012 兜底路线用它提取作者上传的模组包；native room 的 revision 规则 | `forge/resource-adapters.ts` 目前只有 pin 与预览，运行期阻塞 `native-resource-binding-unverified` |
| Runtime | 公开的"关卡构建开始 / 构建完成 / NavMesh 就绪"生命周期观察；现在只有 `BeginWorld(worldEpoch)` | `ForgeRuntime/Framework` 中无生成阶段相关 API |
| GTFO-API（外部，未核实） | 运行期注册自定义 asset / prefab 并让它成为可用 geomorph 的能力 | `AssetAPI.RegisterAsset` / `PrefabAPI` 只是候选接口，语义与合法阶段未核验 |
