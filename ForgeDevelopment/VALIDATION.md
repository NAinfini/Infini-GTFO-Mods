# ForgeDevelopment 验证记录

**上次更新：2026-09-14**（补记托管 ModuleDefinition 清理与 CHANGELOG 迁移的运行记录；补记 D2 全量运行记录；D2 切换后重写于 2026-09-13；D1 部分合并自原 CONTINUATION-STATUS、D1-CONTINUATION、D1-INTEGRATION-REVIEW、D1-SNAPSHOT-DELIVERY、D1-VALIDATION 五份交接记录）。

## 运行记录 2026-09-14 — 托管 ModuleDefinition 清理与 CHANGELOG 迁移（U-DEV-MOD / U-RELEASE）

- 范围：删除托管 `ForgeDevelopment.csproj` 与 `ModuleDefinition.cs`（空 provider `forge.module.development` 0.1.0）。引用它的只有 `ForgeRuntime/tests/Architecture`、根 `Forge.Architecture.sln` 与 `scripts/verify-diagnostics.py`，三处同步删除该引用，没有留测试种子或空壳；`ForgeRuntime/CHANGELOG.md` 的 1.0.0–1.1.2 诊断版条目原文迁入本包 [CHANGELOG.md](CHANGELOG.md)，宿主文件改为从 1.2.0 起只记宿主内容。
- 命令（仓库根目录，`Forge-MapEditor-QA` profile 只作编译引用）：

  ```powershell
  $env:GTFO_BEPINEX_PATH="$env:APPDATA\r2modmanPlus-local\GTFO\profiles\Forge-MapEditor-QA\BepInEx"
  python ForgeDevelopment/scripts/verify-diagnostics.py --bepinex "$env:GTFO_BEPINEX_PATH" --output "$env:TEMP\forge-taskL-verify-20260914T1425"
  ```

- 结果：24 条子命令全部退出 0，其中 12 次构建均 0 警告 0 错误；PluginStartup 54、Reports 99/99、ProjectChecks 204/204、DevelopmentInspection 47/47、ReportSnapshots 100/100、Shutdown 31/31、SceneInventory 8、Telemetry 56、Samples 7、NativeLayout 23/23、Python 41/41。除托管 SDK 工程删除后构建由 13 次降为 12 次外，数字与本文件 2026-09-14 全量运行一致。
- 脚本仍按设计报"源码在运行中变化"，所以 `summary.json` 的 `passed` 为 false、进程退出码为 1：并行的另一任务当时正在改 `ForgeRuntime/tests/Framework/Program.cs`（第一次复跑还有 `ForgeRuntime/Framework/RuntimeJson.cs`、`RuntimePlan.cs`）。三次复跑的 132 个被哈希文件中，只有这些非本次改动的文件变化；本次改动的 `scripts/verify-diagnostics.py`、`ForgeRuntime/tests/Architecture/Program.cs` 与 `Architecture.csproj` 前后哈希相同。
- `Forge.Architecture.sln` 构建 0 警告 0 错误。Architecture 套件在并行任务在途的断言 `Weapon still owns its two observed wield triggers` 上失败；改动前后是同一条失败，且该套件不属于本包套件，未计入上表。
- 证据：`C:\Users\nainf\AppData\Local\Temp\forge-taskL-verify-20260914T1425\`（`summary.json`、各步 `*.log`、运行前后源码 hash；另两次复跑在 `...T1400`、`...T1410`；均未入库、临时目录）。
- 未验证：宿主侧 Architecture 之外的套件本轮未复跑，见 [Runtime 验证记录](../ForgeRuntime/VALIDATION.md)；没有加载游戏，没有安装到任何 profile。

## 运行记录 2026-09-14 — I-RELEASE 精确版本（D-018）ProjectChecks

- 范围：`Native/ProjectChecks.cs` 的 `requiredPlugins` 读取字段由 `minimumVersion` 改为 `version`，判定由 `>=` 改为精确相等（`SemanticVersioning.Version.CompareTo(...)==0`）；诊断消息由 `Required >= X` 改为 `Required exactly X`。与网站 `site/map-package.ts`／`site/map-balance-report.ts` 同批（U-MAP-WEB、U-DEV-MOD，随 D-018）。
- 测试：`tests/ProjectChecks/Program.cs` 全部 `minimumVersion` 字段改名为 `version`；原“较新安装版本视为满足最低版本”用例改为验证“安装版本与要求精确相等才算满足”，并新增一条用例验证安装版本比要求更新时现在判 `failed`（此前会误判满足，这正是 D-018 要修的矛盾）。
- 命令（仓库根目录，`Forge-MapEditor-QA` profile 只作编译引用）：

  ```powershell
  $env:GTFO_BEPINEX_PATH="$env:APPDATA\r2modmanPlus-local\GTFO\profiles\Forge-MapEditor-QA\BepInEx"
  python ForgeDevelopment/scripts/verify-diagnostics.py --bepinex "$env:GTFO_BEPINEX_PATH" --output "$env:TEMP\forge-dev-i-release-verify-20260914T105954"
  ```

- 结果：脚本整体退出码 1（`summary.json` 的 `passed:false`）——但失败只来自 `host-build`／`development-build`／`development-native-build`／`native-layout`，全部因为 `ForgeRuntime/Framework/RuntimePlan.cs` 当时正由并行的 I-PLAN schemaVersion 3 改动编辑、尚未提交，存在编译错误（`CS0165 Use of unassigned local variable 'portIndex'/'start'`），不是本次改动引入。
- `ProjectChecks` 本身：`project-checks-build`、`project-checks` 均退出 0，`project-checks.log`：**`Forge Runtime project checks: 204/204 passed`**（VALIDATION 基线为 202/202；本次新增 1 条“更新版本不再满足精确锁”回归用例，另 1 条差异来自 D2 基线记录之后、本任务改动之前已存在的仓库变化，未逐一溯源，因与本任务无关）。
- 其余不依赖 `ForgeRuntime` 编译的套件全部退出 0：`plugin-startup`、`reports`、`inspection`、`snapshots`、`shutdown`、`scene-inventory`、`telemetry`、`samples`、`python-tools`。
- 证据：`C:\Users\nainf\AppData\Local\Temp\forge-dev-i-release-verify-20260914T105954\`（`summary.json`、各步 `*.log`，未入库，临时目录）。
- 未验证：`ForgeRuntime` 恢复可编译后的端到端 `native-layout`／完整 `verify-diagnostics.py` 全绿复跑；未启动游戏，未安装到任何 profile。

## 运行记录 2026-09-14 — `verify-diagnostics.py` 全量

- 提交：`dac0bdb501fc93eaeda5c68c08e24ada23e8fabc`（运行前后相同）；工作区只有与本包无关的未跟踪文件 `ForgeMap/tests/fixtures/resource-adapter/SHA256SUMS`
- 环境：Windows 11，.NET SDK 10.0.400，Python 3.10.10；`GTFO_BEPINEX_PATH` 指向 r2modman `Forge-MapEditor-QA` profile 的 `BepInEx`，**只作编译引用**
- 命令（仓库根目录）：

  ```powershell
  $env:GTFO_BEPINEX_PATH="$env:APPDATA\r2modmanPlus-local\GTFO\profiles\Forge-MapEditor-QA\BepInEx"
  python ForgeDevelopment/scripts/verify-diagnostics.py --bepinex "$env:GTFO_BEPINEX_PATH" --output "$env:TEMP\forge-dev-d2-verify-20260914"
  ```

- 结果：进程退出码 0；25 条子命令全部退出 0（合计约 32 秒）；`summary.json` 为 `passed: true`、`sourceChangesDuringRun: []`、`gameExecuted: false`、`installed: false`
- 证据：[`evidence/d2-verify-20260914/`](evidence/d2-verify-20260914/)（每步日志、`commands.json`、`summary.json`、`native-layout.json`、运行前后源码 hash、控制台输出）；构建产物留在临时目录，未入库

| 套件 | 结果（取自本次日志） |
| --- | --- |
| 宿主 / SDK 模块 / 原生插件，以及 10 个测试工程构建 | 13 次构建均 0 警告 0 错误 |
| PluginStartup（本包插件入口） | 54 项断言通过，0 个场景失败 |
| Reports | 99/99 |
| ProjectChecks | 202/202 |
| DevelopmentInspection | 47/47 |
| ReportSnapshots | 100/100（含保留的原 37 项） |
| Shutdown | 31/31 |
| SceneInventory / Telemetry / Samples | 8 / 56 / 7 |
| NativeLayout（编译后宿主 + 原生插件元数据） | 23/23 |
| Python 离线工具 | 41/41 |

数字与 D2 切换时那次没有写日期的全量运行一致。

**变异检测：没有。** `verify-diagnostics.py` 没有 full 与 mutation 之分，只有这一种全量模式；本包测试也没有注入变异再确认检出的用例。缺陷检出的证据只有 D1 的红测（见下文 D1 节），本次没有复跑。

**这次运行不能证明：** 游戏能加载本插件（没有启动 GTFO，也没有安装到任何 profile）；三种加载模式；原生 Hook 真正执行，以及 IL2CPP detour 是否安全；GC 之后的原生 callback；采集在主线程上的开销；进出图、切关后的残留；主客机、迟加入与检查点恢复。NativeLayout 只从元数据确认 Hook 目标能在本机 interop 程序集中解析；本次没有复跑 InfiniTweaks NativeContracts。

Python 的跨端 reader 用例此前硬编码网站旧文件 `site/map-balance-report.js`；源码现在已指向 `site/map-balance-report.ts`，按迁移后的路径通过。

## D2 — 独立插件切换

**切换内容。** 诊断源用 `git mv` 保留历史移到 `Native/`，namespace 改为 `ForgeDevelopment.Native`，测试链接改为 `../../Native/`。宿主 `Plugin.cs` 删除配置绑定、诊断初始化、两个作者 monitor 与诊断清理；`GameBindings/PluginPatchSelection.cs` 删除，宿主与本插件各自 `harmony.PatchAll(本程序集)`。宿主侧测试（PluginStartup、HostConfiguration、HostIntegration、GameBindings 原生证据、GraphContracts 集成清单）同批改为"宿主不含诊断"的断言。

**本包 PluginStartup（54）** 编译生产 `Native/Plugin.cs`，宿主、BepInEx、Unity 与 Harmony 用托管替身。覆盖：Off 与 Play 只记 inactive 日志；Authoring 但宿主 Runtime 为 null 时抛出且无任何副作用；精确启动顺序；关闭性能日志时不加 PerformanceMonitor；单次 Load；inactive 的 Load 不能被重试成 active；每个阶段失败时清理与已获取阶段一致、诊断停止最后执行；一个清理失败不阻止其余清理（4 个失败聚合）；异常 Data 不可写时保留原始异常。

**NativeLayout（23）** 用 Mono.Cecil 读取编译后的宿主、SDK 与原生插件：宿主无诊断类型且不引用 Development；每个诊断类型只有一份实现；插件只引用 `ForgeRuntime` 与 `ForgeRuntime.Framework` 且 SDK 身份一致、不内嵌内核；插件身份与 Runtime 依赖；Load 读取 `ConfiguredMode`、`Runtime` 并调用 `PatchAll`；只有两个 monitor 有 Update 循环；Hook 集合精确为 10 个，且每个 Hook 的目标类型与方法在本地游戏 interop 程序集中唯一解析。**这是 metadata-only 证据，未执行 GTFO。**

**InfiniTweaks NativeContracts** 改为检查 Development 插件：InfiniTweaks 不引用任何 Forge 程序集、Development 不引用 InfiniTweaks，4 个性能与场景诊断类型只在 Development 中；106 项 QOL Hook 契约与 249 项诊断 Hook 契约通过，退出 0。所用 `dump.cs` 取自本机 ForgeWeapon W1 证据目录（2026-09-12），未另行核对它与当前 GameAssembly 为同一构建。

宿主侧的对应数字见 [Runtime 验证记录](../ForgeRuntime/VALIDATION.md#d2--宿主移除诊断)。

**设计决定。** 启动门槛取 Runtime 的 `Authoring` 模式，不另设开关；配置文件独立为 `NAinfini.ForgeDevelopment.cfg`，**不迁移旧键**，旧 `Off` 不会变成启用；本插件不登记 provider；报告写 `forgeVersion`（宿主）与 `developmentVersion`。

**已关闭（2026-09-13）。** 网站 `62041354` 起离线包不再把 `ProjectManifest` 写进 Runtime 配置，`NAinfini.ForgeRuntime.cfg` 只剩 `[Runtime] Mode`；玩家包不含本插件是 D-007 的设计。模组侧无代码改动（本插件从未读 Runtime 配置里的这个键）；证据为网站提交，模组未复跑离线包导出。

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
