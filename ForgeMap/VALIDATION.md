# ForgeMap 验证记录

**上次更新：2026-09-13**（内容合并自原 MAP1-DELIVERY 与 MAP1-IDENTITY-CONTINUATION 两份交接记录）。

## 当前结论

MAP1 的原生 API 取证、字节复核、SDK 消费方测试，以及生产程序集内的身份表、创建生命票据与 R2 生命周期接入都已交付并通过。

**没有真实创建适配器、对外原生 resolver、地图 Action、游戏 Hook 或玩家可用插件；MAP1 整体未关闭，MAP2–MAP9 与新增的 MAP-GEN、MAP-ADAPTER 全部未开始。** 正式 `ModuleDefinition` 保持空运行清单，`nativeGameExecuted`、`nativeHooksInstalled`、`gameplayBindingsRegistered` 都是 false，`tests/fixtures/native-identity-scenarios.json` 的十个原生身份规格仍然 `executed: false`。

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
