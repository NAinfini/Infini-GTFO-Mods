# ForgeMap

Map 与 Room 的空间、设备、任务、遭遇、玩家生命流程与进程、世界表现，**以及地图生成的底层逻辑**。

仓库整体状态见 [ARCHITECTURE.md](../ARCHITECTURE.md)，未完成批次见 [IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md)，验证结果见 [VALIDATION.md](VALIDATION.md)。

## 本包按 v2.0 新增的两项职责

总案第 4.2 节把**地图生成的底层逻辑**划给 ForgeMap。驱动这个决定的是维护成本：每家 Geo 包的写法和底层代码都不一样，逐包跟一套实现意味着永远单独维护十家。把生成逻辑收到本包，Adapter 只负责"资源怎么读出来"这一层，新接一个包就只写一个资源侧 Adapter，不动生成逻辑。

因此本包同时是**模型 Adapter 的运行期调用方**。链路分两段，不要混为一谈：编辑期由网站从模组包里提取模型、拆成可组合小件、在地图编辑器里渲染；**运行期由 Forge Map 直接读取对方模组已在游戏内加载的模型资源，按玩家的拼装结果生成**。玩家的依赖列表和现在一致——他仍然装那个 Geo 包，我们调用它；我们不持有拆解后的资源副本去分发。

这两条在 v1.5 时期的模组侧文档里完全没有落点，MAP2 与 MAP9 需要按它重新定范围。

## 当前代码状态

**运行清单仍是空 provider。** `ModuleDefinition.Create()` 的 capabilities 与 bindings 都是空数组，没有独立的游戏插件入口、对外原生 resolver、Action、Hook 或自动安装。

已经在生产程序集内的是内部身份层，只有显式构造 Session 才登记这一空 provider 并订阅生命周期，不会自动加载：

`MapIdentityContracts.cs` 定义域内地址、精确来源锁、内部 native key、不含指针的快照，以及按创建尝试颁发的票据。`MapObjectIdentityIndex.cs` 管有界关联与历史、world/generation/life 隔离、重复回调、取消、销毁、冲突隔离和观测缺口。`MapIdentitySession.cs` 消费真实的 R2 生命周期，做所属线程检查、精确生命探针、重入与失效检查以及注销。这些类型都是 `internal`，通过限定的 friend assembly 由测试访问实际程序集，不跨目录链接源码。

地址逐项区分 layout 与 revision、dimension、layer、local zone、placement、对象 ID 和对象类别。同一 geomorph 的多个 area 使用各自明确的对象 ID；同资源的两个 placement 不合并。**地址不能通过名字、位置、遍历顺序或预览 GLB 猜测**；提供地址的原生适配器仍待核验。来源锁保留 resource ID 与 revision、来源证据 SHA-256，以及已有 `SourceObjectIdentity` 的 file 与 pathId。

MAP1 也交付了可重复运行的原生 API 与字节证据检查工具：本机 build 的 32 个类型、110 个方法的元数据锁，以及旧审计的 11 个原生区域字节复核。**这些是 metadata-only 证据，不证明原生调用语义、合法阶段或复制完成。**

## 边界

没有真实创建适配器、对外 EntityResolver、地图 Action、游戏 Hook 或玩家可用插件。`tests/fixtures/native-identity-scenarios.json` 的十个原生身份规格仍然 `executed: false`，报告里的 `nativeGameExecuted`、`nativeHooksInstalled` 和 `gameplayBindingsRegistered` 都是 false。没有原生创建、主客机、恢复或导航执行。

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

原生证据的复采命令见 [VALIDATION.md](VALIDATION.md)。测试目录的边界说明见 [MapContracts](tests/MapContracts/README.md) 与 [MapIdentity](tests/MapIdentity/README.md)。

当前 DLL 不是玩家发行物；发布身份、真正加载、主客机和恢复都待实施验证。InfiniTweaks 的 QoL 行为不属于本模块。
