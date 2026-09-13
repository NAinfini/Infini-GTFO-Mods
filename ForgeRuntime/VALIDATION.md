# ForgeRuntime 验证记录

**上次更新：2026-09-13**（内容合并自 R1、R2a、R2a-cleanup、R2b-1、R3a、R4a 六份交接记录与旧采集版验证记录）。

全仓库的测试计数汇总在 [ARCHITECTURE.md 第 3 节](../ARCHITECTURE.md#3-测试计数各自独立不相加)。本文记录 Runtime 侧各次交付的实际内容、复跑命令与仍然存在的失败。

## 当前结论

完整 GTFO 宿主与 `Forge.Architecture.sln` 都构建通过，0 警告 0 错误。R1 编译基线、R2a 公开生命周期、R2b-1 宿主配置与启动隔离、R3a 实体观察接线、R4a 可变端口元数据解析已交付并有实现级证据。**R2 整体、R3、R4、R5–R8 未关闭；没有任何游戏、安装、多人或恢复验收。**

R1 交接记录末段那三条 REOPENED 阻塞已全部解决：`ForgeEnemy/Receivers/EnemyHealthCommit.cs` 的 `RuntimeJson.Text` 缺失、`GameBindings/EnemyModule.cs` 的 `DamageObservation` 缺少 `damagePointer` 参数、以及六模块架构工程因此无法构建。旧 `GameBindings/EnemyModule.cs` 已在 E1 切换中移除。

Python 套件仍有一项失败：`test_import_log.py::test_cli_output_is_accepted_by_the_website_report_reader` 硬编码 `site/map-balance-report.js`，而网站实际文件是 `site/map-balance-report.ts`。曾有一次诊断运行只在内存里把常量改成 `.ts` 路径并通过了同一条跨端断言，但源码测试没有被修改，那次诊断**不计**为未改动 Python 套件的通过。这项修复归 Development 与网站责任任务；不引入 fallback reader、跳过断言或弱化报告 schema。

## R1 — 宿主编译基线

合并进来的 `Infini.ForgeRuntime` 原型被默认 compile glob 误编进生产宿主，同时生产宿主与 SDK 与测试也被误编进原型工程，复现出 59 个错误。两个工程现在显式分离源码，生产只引用 `ForgeRuntime.Framework`。**原型不是生产 API 也不是 fallback**，它的退休是 R4 的显式收尾项；新合并的源码与其独立测试保留。原型单独构建通过，其 14 项测试需要显式 `dotnet --roll-forward Major`（本机没装 .NET 8），这不改变 net6.0 生产目标，原型的检查也不计作生产 SDK 或游戏支持证据。

## R2a — 公开生命周期与宿主接线

`GameRuntimeBridge` 改为调用内核的 `StartRuntime`/`StopRuntime`，注册在导出 manifest 与离线 LoadPlan 之前冻结。删除了 `GameBindings/FrameworkStartup.cs` 与其测试源码链接，两条旧 latch 断言迁到公开内核。新增 `Plugin.CanExecuteGameplay`，包含所属线程检查、Ready/InLevel/主机门槛、待处理的失效与停止、迁移与挂起检查。失败处理不再对 Failed/Stopped 内核调用 `BeginWorld`；文件初始化失败不会在 100 tick 后或 Generating/InLevel 转换时重试。派发过程中的原生 teardown 立即关闭门槛，然后在安全点做世界与停止清理；保留的内核引用在停止后是终态。观察器异常被隔离，按变化的错误计数记录一次，健康的观察器与玩法继续。

一条新回归先在"客户端在下一 tick 之前被提升"上失败，保留原始权威基线并忽略重复的 InLevel 重置后通过。

独立复验在全新目录中完成，`tests/HostIntegration/verify.ps1` 退出 0；前后源码快照零变化，宿主与测试各自的 SDK 副本 SHA-256 相同。

## R2a — 已加载工作的清理修复

检查发现 `CancelSchedule` 与 `ReleaseLease` 调用 `Thread()`，而 `Thread()` 现在也拒绝失败或停止的启动状态，因此已取消的 IDisposable 句柄可能在正常的停止后清理中抛出。用真实已加载的 schedule 与 lease 复现：LifecycleWork 先是 50 项通过、2 组失败，失败栈指向停止后的 `CancelSchedule` 与失败启动后的 `ReleaseLease`。

两个清理方法改用 `ReadThread(); NoLifecycleMutation();` 而不是 `Thread()`——终态句柄的清理不再请求"开始新工作"的权限。精确句柄身份检查、错误线程拒绝和只读观察器限制都保留，注册、发布、调度、lease 获取与 Advance 的门槛都没有放宽。修复后 57 项通过、0 组失败。

## R2b-1 — 宿主配置与启动隔离

配置归属从 `AuthoringSettings` 移到 `RuntimeSettings`，行为细节见 [README](README.md#宿主启动与配置)。第一次真实 BepInEx 配置测试暴露了 5 种畸形模式场景会选中默认值或组合枚举值；改为显式解析并加上保存文件往返检查、以及"宿主只保存自己的键时不破坏未绑定的诊断设置"之后通过。

用生产 `Plugin.cs` 加托管替身复现插件加载失败路径：原始生产源码 5 项断言通过、8 个场景失败；补全后的 bootstrap 套件 42 项通过、退出 0。失败的 unpatch、日志或组件清理不会跳过剩余清理、不会替换原始启动异常、不会留下可用的宿主、也不会重试 Load。

Windows 的共享锁曾阻止两个已有文件的原子替换；先核对其未变字节再做常规原地编辑并读回，没有为此改动 ACL、其他进程或用户文件。

**R2b-1 是宿主配置与启动交付，不是 D2/E1 的插件切换。** 独立 GUID 与包身份、最终配置文件迁移、发行布局、真实加载组合、原生 detour 行为与多人都未验证。

## R3a — 实体观察与 actor 合同

原来未完成的 Registry 观察登记、模块注销清理和公共只读保护已在真实共享 SDK 接通。跨模块测试另外检出"实体观察回调可以注销生命周期订阅"的问题；现已只在实体观察回调内禁止该入口，正常清理、停止后清理与生命周期回调自注销都保留。

SDK 合同与注册探针 76 项、Enemy 消费方 66 项、Trigger 的 R3 消费方 417 项、Framework 253 项与生命周期回归全部通过。**没有第二个 SDK 或 Registry；旧的隔离补丁提案不再需要应用。** 完整 R3、世界枚举、网络与恢复、游戏验证仍未关闭。

## R4a — 可变端口元数据与精确修订解析

网站 `graph-schema.ts` 会校验 `graph.variadic` 并按可选整数计数展开选定一侧，下界等于原有的 2 个端口，上界不超过 32；已有基础端口名与顺序不变，新增端口是 `templateId_3`、`templateId_4` 依此类推。此前 `RuntimeRegistry` 把 `variadic` 当未知字段拒绝，Trigger 的 R4 交接记录了这个阻塞。

现在能在不剥字段、不改所有者与版本的前提下校验该元数据，并从唯一注册定义中按精确能力 revision 和有界计数解析端口。**解析出的端口布局只是元数据**：没有 handler、权限、调度器或可执行 binding。v1 计划加载器像当前网站编译器一样明确拒绝可变端口的执行；完整 R4 lowering 另算。

## 复跑

从仓库根目录执行。宿主与 GameBindings 需要 `GTFO_BEPINEX_PATH` 或 `-p:GTFOBepInExPath=<BepInEx 目录>`。构建输出用 `--artifacts-path` 指向隔离目录，绝不写入已安装的插件目录。

```powershell
dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release
dotnet build Forge.Architecture.sln -c Release
dotnet run --project ForgeRuntime/tests/Architecture/Architecture.csproj -c Release --no-build
dotnet run --project ForgeRuntime/tests/Framework/Framework.csproj -c Release -- --fixtures ../Infini-GTFO-Model-Site/Tests/Forge/fixtures/runtime
dotnet run --project ForgeRuntime/tests/Framework/Framework.csproj -c Release -- --benchmark
dotnet run --project ForgeRuntime/tests/GameBindings/GameBindings.csproj -c Release -- --fixtures ../Infini-GTFO-Model-Site/Tests/Forge/fixtures/runtime
dotnet run --project ForgeRuntime/tests/HostIntegration -c Release
dotnet run --project ForgeRuntime/tests/LifecycleWork/LifecycleWork.csproj -c Release -- --fixtures ../Infini-GTFO-Model-Site/Tests/Forge/fixtures/runtime
dotnet run --project ForgeRuntime/tests/Reports -c Release
dotnet run --project ForgeRuntime/tests/ProjectChecks -c Release
dotnet run --project ForgeRuntime/tests/SceneInventory -c Release
dotnet run --project ForgeRuntime/tests/Telemetry -c Release
dotnet run --project ForgeRuntime/tests/Samples -c Release
python -m unittest discover -s ForgeRuntime/tests -p 'test_*.py'
```

聚焦回归各有自己的 README：[宿主启动](tests/PluginStartup/README.md)、[真实配置](tests/HostConfiguration/README.md)、[已加载工作清理](tests/LifecycleWork/README.md)。

`--benchmark` 只做小型可重复的合成负载收据：注册与 LoadPlan 各一次，固定数量的 dispatch、scheduled pulse 与 lease 请求，各自的耗时、分配与预算拒绝原因。**这些是桌面替身数字，不是 GTFO 帧率或联机性能证明。**

## 历史：1.1.x 采集版

1.1.0 的实机报错位于 `DMD<LG_ZoneJob_CreateZoneForStaticLevel::Build>` 的 native→managed trampoline。原生映射显示该 Build 的 RVA 0x34E9A0 与 247 个方法条目共享，`GetShadowRenderGroups` 的 RVA 0x46E980 与 95 个条目共享——**签名正确无法保证 detour 安全**。1.1.1 改用明确的非共享入口列表，`NativeContracts` 增加同一游戏版本 dump.cs 的共享地址检查。这条结论对后续所有原生 Hook 仍然有效。

1.1.0 的离线检查曾记录：Release 构建 0 警告 0 错误；报告核心与后台写入 32 项；项目规则与文件采样 34 项；场景清单 8 项；可选遥测订阅生命周期 56 项；帧采样 7 项；Python 报告分析、日志导入与运行对照 18 项；原生程序集契约 109 个 QOL hook、254 个制作端 hook 与 9 个内嵌图标检查通过。这些是当时版本的历史快照，不代表当前源码，也从未执行原生 detour。原版生成一致性、实机采集开销、连续进退关、主客机以及 R7D2 与 CullingCluster 的根因仍未验证。

## 边界

以上全部是托管逻辑、编译后 DLL、静态元数据与合成 fixture 的证据。没有执行原生 GTFO 方法、没有 detour、没有多人、没有安装。测试未通过时不能把已有发行包当成新构建成功；构建其他模块时也用自己的隔离输出目录，不写别人的 `bin/obj`。
