# ForgeMap 验证记录

**上次更新：2026-09-13**（内容合并自原 MAP1-DELIVERY 与 MAP1-IDENTITY-CONTINUATION 两份交接记录）。

## 当前结论

MAP1 的原生 API 取证、字节复核、SDK 消费方测试，以及生产程序集内的身份表、创建生命票据与 R2 生命周期接入都已交付并通过。

**没有真实创建适配器、地图 Action、生成器、资源 Adapter 或玩家可用发行物；MAP1 整体未关闭。MAP2 只完成了定范围（规格、fixture、签名锁），没有 C# 实现；MAP5a 玩家实体身份为 implementation-only（唯一的游戏 Hook 与对外 resolver，见下节），MAP5 其余与 MAP3–MAP9、MAP-GEN、MAP-ADAPTER 未开始。** 正式 `ModuleDefinition` 保持空运行清单，`nativeGameExecuted`、`nativeHooksInstalled`、`gameplayBindingsRegistered` 都是 false；`tests/fixtures/native-identity-scenarios.json` 的十个原生身份规格只执行了托管替身部分，原生部分 `nativeExecuted: false`。

## MAP5a — 玩家实体身份（2026-09-13）

证据等级：**implementation-only + 本机静态原生证据**。没有启动 GTFO、没有安装到任何 profile、没有主客机。全部构建使用会话临时目录的隔离 `--artifacts-path`，`GTFOBepInExPath` 指向只读的 Temp profile BepInEx；复跑命令见 [README](README.md#复跑)。

| 套件 | 退出码 | 输出结尾 | 说明 |
| --- | --- | --- | --- |
| `Native/ForgeMap.Native.csproj` 构建 | 0 | 0 警告 0 错误 | 真实 interop 编译，`ForgeRuntimeAssembly` 取同一 artifacts 的宿主构建 |
| `tests/MapNativeAdapter` | 0 | `PASS 35/35 Map native player identity cases; no GTFO execution.` | 生产源码加 loader 与游戏替身：注册顺序与回滚、身份规则、世界与停止清表、bot、迟加入、冲突、伪造引用、线程、隐私 |
| `tests/MapNativeLayout` | 0 | `PASS 38/38 Map native layout checks; no GTFO execution.` | Cecil：依赖方向、单一 provider 来源、只读且精确的玩家读取、Lookup 从不格式化或装箱、Hook 形状、插件身份与依赖、Off 门、不热卸载 |
| `tests/MapNativeEvidence` | 0 | `PASS 29/29 Map static native evidence checks; game execution NOT tested.` | build、hash、MVID、2 个 Hook 签名与 dump RVA 唯一不共享可执行、5 个读回签名、7 条直接调用边 |
| MapIdentity 回归 | 0 | 134 项断言通过，`nativeScenariosExecuted: false` | 未改 |
| MapContracts 回归 | 0 | 33 项通过 | 未改 |
| 架构回归 | 0 | `PASS 36 architecture boundary assertions. No GTFO hooks, gameplay, networking or installation exercised.` | `Program.cs` 未改 |

同批复跑 ForgeWeapon（源码未改，只改文档）：NativeAdapter `PASS 26/26`，NativeLayout `PASS 51/51`，NativeEvidence `PASS 52/52`，Identity `"cases":42,"assertions":99`，IdentityAcceptance `INDEPENDENT IDENTITY: 37/37 passed`，IdentityDispatchReview `DISPATCH REVIEW: 20/20 passed`，退出码均为 0。

中间失败与修正：
- MapNativeLayout 首跑 36/37：`GameAssembly` 判定只识别 `-ASM` 后缀，漏掉 `SNet_ASM`，精确读取集合因此缺少 SNet_Player 成员。补上 `_ASM` 后通过。
- 协调方追加隐私规则后，实体 ID 从 `gtfo.player:<Lookup>` 改为本世界编号，测试随之重写并新增 2 个隐私用例与 1 项静态检查。
- IdentityAcceptance / DispatchReview 第二次运行时报告文件已存在而拒绝覆盖（测试行已全部通过），换新报告路径重跑后退出码 0。

会话临时目录里的变异检查（只改副本，不改仓库）：去掉 agent 指针替换、去掉 `SNet.IsMaster`、去掉互链复核、去掉世界清表、冲突保留首个、实体 ID 用 Lookup、每个 life 换编号，7 个变体各自被对应的具名用例检出。报告 JSON 中搜不到任何 fixture Lookup 值。

未核验（需要游戏内确认）：bot `Lookup` 跨重生稳定性；postfix 时刻 `PlayerAgentsInLevel` 的成员是否已更新；`UnregisterPlayerAgent` 与 `OnPlayerDespawned` 的运行时顺序；倒地与救起不重建 PlayerAgent；`Object.Destroy` 延迟期间旧 agent 的状态；迟加入与主机迁移。

## MAP2 定范围（2026-09-13）

输出保存在 `evidence/map2-scope-2026-09-13/`。构建使用会话临时目录的隔离 artifacts，没有写 `ForgeMap/bin` 以外的模块输出。

| 检查 | 命令 | 结果 |
| --- | --- | --- |
| 生成相关原生签名锁 | `Capture-NativeApi.ps1 -TargetsFile tools/generation-api-targets.json` | 27 个类型、59 个方法，缺失 0，build 20403457，`metadata-only` |
| 独立复采比对 + 检查器自测 | `verify_native_api.py generation-api.json <复采> --self-test` | 退出码 0，`matched: true`，差异 0，自测 17 项 |
| 资源侧描述符 fixture | `python ForgeMap/tools/verify_resource_adapter_fixtures.py` | 退出码 0，合法 3/3（阻塞项与期望一致），非法 21/21 以期望错误码拒绝，`fixture-schema-only` |
| MapIdentity（含十个规格的托管替身标记） | Release 构建后运行 `ForgeMap.Identity.Tests.dll` | 构建 0 警告 0 错误；退出码 0，134 项断言通过；每个规格 3–17 项标记断言，`nativeScenariosExecuted: false` |
| MapContracts 回归 | Release 构建后运行 `MapContracts.dll` | 构建 0 警告 0 错误；退出码 0，33 项通过 |
| 架构回归 | `Forge.Architecture.sln` Release 构建后运行 `Architecture.dll` | 构建 0 警告 0 错误；退出码 0，36 项通过 |

MapIdentity 从 126 项增加到 134 项：新增同资源两个 placement 的兄弟回调拒绝（3 项）、同一 geomorph 两个 area 的错 area 观察拒绝（3 项），以及规格文件与标记集合一一对应的两项核对。原有断言未修改，只加了规格 id 标记。

**边界**：签名锁只证明方法存在；fixture 检查只证明描述符形状与拒绝规则，数据是合成的，不代表任何真实 Geo 包；134 项仍是托管实现加原生探针替身。没有运行游戏、加载 bundle、安装或发布。MapContracts 的 `TestWorld.cs` 在本批期间由主会话为 Framework 的 `promoted` 字段修改过，本批未改它，回归结果基于修改后的文件。

## MAP1b — 内部身份实现

| 检查 | 结果 |
| --- | --- |
| 实际 Map 身份实现 + 原生探针替身 | 126 项断言通过，退出码 0 |
| 原有 Map SDK 消费方回归 | 33 项断言通过，退出码 0 |
| 跨模块架构回归 | 通过（当时记录 35 项；**现行 Program.cs 是 36 项**，见 [ARCHITECTURE.md](../ARCHITECTURE.md#2-架构断言数是-36)） |
| 三组 Release 构建 | MapIdentity、MapContracts、`Forge.Architecture.sln` 均 0 警告 0 错误 |
| 构建与测试期间的源码一致性 | 相关源码集合及 SHA-256 前后一致 |

初版 118 项断言通过后，审查把 native 探针从数字 key 提升为"创建票据 + key"的精确生命见证。随后新测试失败：测试替身把迟到的 Callback 当作新的 Bind，改写了模拟的物理生命。修复只分离了测试替身的 Bind 与 Callback 并改用正确的迟到回调路径，**没有放宽生产的身份校验**。失败记录保留在 `verification-final-1` 里，最终结论以 `verification-final-2` 的 126 项为准。

126 项覆盖地址与来源锁、int64 pathId、重复观察、冲突隔离、取消与销毁、world/generation/life、指针与 Unity ID 同时复用、probe 异常与重入、跨线程、停机与注销、历史上限、不完整采集。**这些是托管实现测试，不能替代原生创建 API、权限与阶段、主客机、导航或恢复测试。**

## MAP1 — 首批原生取证

| 检查 | 结果 |
| --- | --- |
| 本机 API 元数据与独立复采 | 32 个类型、110 个方法、9 个程序集；所选目标缺失 0；两次匹配 |
| API 检查器正反自测 | 17 项通过；故意修改方法、参数、枚举、程序集、版本或验证等级均被拒绝 |
| 命令行拒绝路径 | 6 项通过；基线未被覆盖 |
| 已有原生区域字节 | 11/11 与当前 GameAssembly 匹配 |
| 实际 SDK + 合成对象表 | 33 项断言通过 |
| Map 与测试工程 Release 构建 | 0 警告 0 错误 |

GameAssembly SHA-256：`c6a5c3cd8ca5fe2a8c1a71a3d107663e8cbf01404820dbbda2ea10c4bfd7bf55`（本机 Steam app 493520 / build 20403457）。原生区域复核是重新核对旧审计记录的字节摘要，**不是重新反汇编，也没有运行游戏**。没有复制、安装或分发游戏 DLL 与资源；程序集版本是编译元数据，不冒充 Thunderstore 包版本或许可证明。

已核验的入口示例包括 zone 与 layer 的 `LG_Zone.Create` / `LocalIndex` / `DimensionIndex` / `LG_Layer.CreateZone` / `AddZoneToLayer`，geomorph 与 area 的 `AddCustomGeomorphAreas` / `SetupAreas` / `SetPlaced` / `LG_Area.Setup`，plug 与 door 的 `LG_Plug.TryPair` / `Pair` / `LG_Gate.SpawnedDoor` / `LG_SecurityDoor.Setup` 及各种锁。**这些是签名存在的证据，不是"作者布局到本次维度层区域的唯一生成映射""创建回调的顺序与次数""运行中重建拓扑与导航与复制"的保证**——枚举可写入不等于门行为正确。

33 项 SDK 测试没有运行原生对象匹配、游戏网络或游戏权限绑定；其中的客户端与主机测试是 SDK 的权威控制测试，**不是两台真实 GTFO 客户端的验证**。

## 中间失败与修正

前三次 Map 构建遇到 Runtime 并发编辑的中间态：生命周期方法尚未配齐、重复 Advance、文件尚未写完整。原始失败输出保留在 `map-build.log` 与 attempt-2、attempt-3 里；本任务没有修改 Runtime 源码。随后构建成功。测试夹具的 provider 命名空间和未知实体的拒绝阶段按实际 SDK 修正——未知对象在入队前就被拒绝，而不是等生成命令的结果；**修正只在 Map 测试代码里，没有放宽 SDK 校验**。

## 复跑

从仓库根执行，输出目录必须尚不存在且位于 `ForgeMap` 内：

```powershell
python ForgeMap/tools/run_identity_checks.py --out ForgeMap/evidence/identity-recheck-01
```

只跑 SDK 消费方：

```powershell
$artifacts = Join-Path (Resolve-Path ForgeMap) 'bin/map1-artifacts'
dotnet build ForgeMap/tests/MapContracts/MapContracts.csproj -c Release --artifacts-path $artifacts
dotnet "$artifacts/bin/MapContracts/release/MapContracts.dll"
```

原生证据复采。路径是当前机器的已核验安装，在其他机器上必须显式替换；采集工具拒绝覆盖已有文件，每次用新的输出名，只读取游戏字节和元数据，不执行游戏代码：

```powershell
$m = (Resolve-Path ForgeMap).Path
$site = (Resolve-Path ../Infini-GTFO-Model-Site).Path
$bep = "$env:APPDATA/r2modmanPlus-local/GTFO/profiles/Temp/BepInEx"
$game = 'E:/SteamLibrary/steamapps/common/GTFO'
$out = "$m/bin/native-api-$([Guid]::NewGuid().ToString('N')).json"
& "$m/tools/Capture-NativeApi.ps1" -BepInExRoot $bep -GameRoot $game -OutFile $out
python "$m/tools/verify_native_api.py" "$m/evidence/map1-2026-09-12/native-api.json" $out --self-test
python "$m/tools/verify_native_regions.py" "$site/artifacts/framework-implementation-2026-09-12/map-api-audit/native-regions.json" "$game/GameAssembly.dll"
```

测试的逐项进度写到 stderr，最终结构化结果写到 stdout。构建、测试与来源摘要必须一起保存；共享 SDK 后续继续变化时需要重新运行，本记录不保证未来的工作树仍然相同。

各测试目录的边界说明见 [MapContracts](tests/MapContracts/README.md) 与 [MapIdentity](tests/MapIdentity/README.md)。

## 边界

这些数量不相加为"已实现能力数"。架构解决方案只覆盖共享 SDK、五个模块骨架和架构测试，不含 Runtime 游戏宿主的实机加载验证。没有启动或安装游戏、迁移配置、打包发布，也没有执行 Git commit 或 push。构建其他模块时也使用 Map 自己的忽略输出目录，不写别的任务的 `bin/obj`。
