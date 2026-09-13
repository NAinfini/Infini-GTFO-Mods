# ForgeDevelopment 验证记录

**上次更新：2026-09-13**（D2 切换后重写；D1 部分合并自原 CONTINUATION-STATUS、D1-CONTINUATION、D1-INTEGRATION-REVIEW、D1-SNAPSHOT-DELIVERY、D1-VALIDATION 五份交接记录）。

## 当前结论

D1 与 D2 都已交付到**实现 + 本地托管/元数据验证**等级。D2 把 16 个诊断源码、全部诊断测试与 Python 工具从 `ForgeRuntime/` 迁到本包，建立独立的 `ForgeDevelopment.Native` BepInEx 插件，宿主不再含任何诊断、报告或性能采集。宿主与原生插件都构建通过，0 警告 0 错误。

**没有 GTFO 加载、安装、多人、原生 Hook 安全或采集开销的验收**；"不安装 / 安装但未启用 / 启用"三种实际加载模式也没有在游戏里执行。这不关闭 D3–D7。

## 最后一次记录的通过数

`verify-diagnostics.py` 全量一次运行，每一步退出 0：

| 套件 | 结果 |
| --- | --- |
| 宿主 / SDK 模块 / 原生插件构建 | 0 警告 0 错误 |
| PluginStartup（本包插件入口） | 54 项断言 |
| Reports | 99/99 |
| ProjectChecks | 202/202 |
| DevelopmentInspection | 47/47 |
| ReportSnapshots | 100/100（含保留的原 37 项） |
| Shutdown | 31/31 |
| SceneInventory / Telemetry / Samples | 8 / 56 / 7 |
| NativeLayout（编译后宿主 + 原生插件元数据） | 23/23 |
| Python 离线工具 | 41/41 |

Python 的跨端 reader 用例此前硬编码网站旧文件 `site/map-balance-report.js`；源码现在已指向 `site/map-balance-report.ts`，本次随迁移后的路径全量通过。

## D2 — 独立插件切换

**切换内容。** 诊断源用 `git mv` 保留历史移到 `Native/`，namespace 改为 `ForgeDevelopment.Native`，测试链接改为 `../../Native/`。宿主 `Plugin.cs` 删除配置绑定、诊断初始化、两个作者 monitor 与诊断清理；`GameBindings/PluginPatchSelection.cs` 删除，宿主与本插件各自 `harmony.PatchAll(本程序集)`。宿主侧测试（PluginStartup、HostConfiguration、HostIntegration、GameBindings 原生证据、GraphContracts 集成清单）同批改为"宿主不含诊断"的断言。

**本包 PluginStartup（54）** 编译生产 `Native/Plugin.cs`，宿主、BepInEx、Unity 与 Harmony 用托管替身。覆盖：Off 与 Play 只记 inactive 日志；Authoring 但宿主 Runtime 为 null 时抛出且无任何副作用；精确启动顺序；关闭性能日志时不加 PerformanceMonitor；单次 Load；inactive 的 Load 不能被重试成 active；每个阶段失败时清理与已获取阶段一致、诊断停止最后执行；一个清理失败不阻止其余清理（4 个失败聚合）；异常 Data 不可写时保留原始异常。

**NativeLayout（23）** 用 Mono.Cecil 读取编译后的宿主、SDK 与原生插件：宿主无诊断类型且不引用 Development；每个诊断类型只有一份实现；插件只引用 `ForgeRuntime` 与 `ForgeRuntime.Framework` 且 SDK 身份一致、不内嵌内核；插件身份与 Runtime 依赖；Load 读取 `ConfiguredMode`、`Runtime` 并调用 `PatchAll`；只有两个 monitor 有 Update 循环；Hook 集合精确为 10 个，且每个 Hook 的目标类型与方法在本地游戏 interop 程序集中唯一解析。**这是 metadata-only 证据，未执行 GTFO。**

**InfiniTweaks NativeContracts** 改为检查 Development 插件：InfiniTweaks 不引用任何 Forge 程序集、Development 不引用 InfiniTweaks，4 个性能与场景诊断类型只在 Development 中；106 项 QOL Hook 契约与 249 项诊断 Hook 契约通过，退出 0。所用 `dump.cs` 取自本机 ForgeWeapon W1 证据目录（2026-09-12），未另行核对它与当前 GameAssembly 为同一构建。

宿主侧的对应数字见 [Runtime 验证记录](../ForgeRuntime/VALIDATION.md#d2--宿主移除诊断)。

**设计决定。** 启动门槛取 Runtime 的 `Authoring` 模式，不另设开关；配置文件独立为 `NAinfini.ForgeDevelopment.cfg`，**不迁移旧键**，旧 `Off` 不会变成启用；本插件不登记 provider；报告写 `forgeVersion`（宿主）与 `developmentVersion`。

**未关闭。** 网站离线包仍把 `ProjectManifest` 写进 Runtime 配置且不含本插件（网站仓库负责）。

## D1 — 入队快照缺陷的复现与修复

`AsyncReportWriter.Request` 原来保存的是可变的 `DiagnosticsReport` 和 outcome，后台线程才执行 `Report.Export`。因此请求排队之后继续观察、完成或取消扫描、重新附着报告，都会改变尚未写盘的请求内容。

回归用一次故意失败的写盘和受控回调阻塞后台线程复现（**不靠 Sleep 或原生游戏时序**），随后入队 pending 与 complete 两份报告，改变 world、tick、来源验证、计数和元数据，再释放线程。**首次结果是 19/37 通过、18 项失败、退出码 1——红测是缺陷证据，不是验收通过。**

修复后 `DiagnosticsReport.cs` 在 Enqueue 时捕获不可变的托管证据，`AsyncReportWriter.cs` 排队的是 FrozenReport。受控的 writer 闸门测试从 9/21 → 21/21 → 扩展后 63/63。先前会话的 `Program.cs` 曾被新入口覆盖，已从工具历史完整恢复为 `PreservedSnapshotTests.cs`，原 37 项断言逐项保留并与新增 63 项一起执行，最终 100/100。

ProjectChecks 的 D1 用例覆盖：同一次 scan 的报告快照、导出文件不回写、旧 world 回调拒绝、取消前后与新 run 的隔离、开始前 partial、拒绝清单、未配置清单、未知来源、来源 pending 与 mismatch、错误 scan 所有者、重复 Start。**合成世界只验证 C# 合同，不是 GTFO 实测。**

DevelopmentInspection 的基线曾报四个缺失 `Dimension.Pointer` 的错误；**生产的 pointer 检查保留**，修的是原生身份替身，新增六条断言后共 47 项。Shutdown 原始代码 21 项检查只过 10 项；`ShutdownSequence` 改为继续执行所有清理阶段并返回只读、有序的失败收据后 31/31。

## 复跑

```powershell
python ForgeDevelopment/scripts/verify-diagnostics.py --bepinex "$env:GTFO_BEPINEX_PATH"
```

默认新建系统临时目录，也可以指定一个尚不存在的 `--output`；源码变化或任何失败都返回非零。单跑纯托管套件：

```powershell
dotnet run --project ForgeDevelopment/tests/Reports -c Release
dotnet run --project ForgeDevelopment/tests/ProjectChecks -c Release
dotnet run --project ForgeDevelopment/tests/DevelopmentInspection -c Release
dotnet run --project ForgeDevelopment/tests/ReportSnapshots -c Release
dotnet run --project ForgeDevelopment/tests/Shutdown -c Release
python -m unittest discover -s ForgeDevelopment/tests -p 'test_*.py'
```

原生插件与 NativeLayout 需要先构建宿主，再传入宿主与 SDK 路径，参数形式以 `verify-diagnostics.py` 为准。构建输出用 `--artifacts-path` 指向隔离目录。

## 边界

以上是托管逻辑、合成输入与编译后元数据的证据。采集本身在游戏主线程的成本、两次进出图、GC 之后的原生 callback、GTFO 主客机与恢复、独立插件的真实加载组合、安装与发布，全部未执行。

没有安装到游戏 profile、运行 GTFO、发布或修改网站；D2 已按用户要求提交并推送到 `origin/main`。Temp profile 只作为本地构建的程序集引用，构建输出在独立的临时目录。
