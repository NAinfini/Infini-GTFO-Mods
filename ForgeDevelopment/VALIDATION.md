# ForgeDevelopment 验证记录

**上次更新：2026-09-16**（新增集成批 integ-dev：Authoring 分支直接接线 + 删掉反射层、快照按关卡层级遍历并给每个对象行加 `active`、points.tsv 删 cut 行并把两个 pending 点改成 recorder 自己采集、追加 9 份 nl-* 报告的必须进游戏验证点）。此前：2026-09-16 记录器核心（dev-rec-core：`Native/Rec*.cs`、`tests/Recorder`、离线查询与合并工具）；2026-09-15 作者工具收口运行（loader/host double、节点回指、trace 入队冻结、聚合上限与 `verify-diagnostics.py` 负例门槛，并把变异检出收紧为断言级检出）；补记托管 ModuleDefinition 清理与 CHANGELOG 迁移；补记 D2 全量运行记录；D2 切换后重写于 2026-09-13；D1 部分合并自原 CONTINUATION-STATUS、D1-CONTINUATION、D1-INTEGRATION-REVIEW、D1-SNAPSHOT-DELIVERY、D1-VALIDATION 五份交接记录。

## 运行记录 2026-09-16 — 集成接线与补验证点（integ-dev）

- 范围：`Native/Plugin.cs`、`RecRuntime.cs`、`RecUnity.cs`、`CaptureRegistry.cs`、`CaptureWorld.cs`、`ExperimentPanel.cs`、`RuntimeDiagnostics.cs`；删除 `Native/RecIntegration.cs`（反射层）；`tests/PluginStartup/{Doubles,Program,LoaderModes}.cs`、`tests/NativeLayout/Program.cs`、`tests/Capture/Program.cs`、`tests/Experiments/ShippedCommandTests.cs`、`tests/Recorder/Recorder.csproj`；`probes/points.tsv`、`probes/PLAYTEST.md`、`probes/trace/damage.json`、`probes/trace/level.json`；`README.md`。
- 接线：`Plugin.Load` 的 Authoring 分支直接调 `CaptureRegistry.EnsureStarted(monitor, …)` 并挂上 `ExperimentRunner`/`ExperimentPanel` 组件、调 `ExperimentPanel.Load()`；`RecHotkeys` 里 F7 直调 `CaptureRegistry.SnapshotAll("hotkey")`、F5 直调 `ExperimentPanel.Toggle()`；`RecRuntime` 的层级边界与关卡清理直调 `CaptureRegistry.SnapshotAll`。跨批按签名反射的层（`RecIntegration`）整个删除，不留第二条路径。
- 快照：关卡对象（门/弱门/终端/发电机/集群/HSU/容器/弱容器/拾取物/出口/链式谜题/灯）改成对 `LG_Floor` 层级的一次遍历，被禁用的对象也在快照里，每个对象行带 `active`；敌人、导航标记与 HUD 文本没有 `includeInactive` 的 `FindObjectsOfType` 重载可用，仍是场景检索（在报告里列为游戏内验证点）。
- points.tsv：删掉 10 行 `trigger.cut-*`；`development.stop-phase-list` 改成 `trace:session/shutdown_stage`（`RuntimeDiagnostics.Stop` 的收尾序列每阶段开始时写一条会话记录），`development.module-reachability` 改成 `trace:forge/kernel;state:forge.*`；ni-hud C（`hud.teammate-extra-info-writer`）改成 trace + 截图并同步 `PLAYTEST.md`；把 nl-weapon/weapon2/deploy/env/env2/query/player/doorterm2/glue 九份报告的"必须进游戏验证"点追加为 31 行。结果：177 行数据（`trace:` 132、`state:` 129、`net:` 24、`exp:` 1、`manual:` 8）。`tests/Capture` 的 points 检查同步收紧：capture 列只允许这五种形式，`trace:` 命名的类型必须在 profile 里、方法必须是该条目真的会记录的。
- 命令与结果（仓库根目录，`Forge-MapEditor-QA` profile 只作编译引用；构建与运行输出写进 `%TEMP%\integ-dev` 下的隔离目录）：

  ```powershell
  $env:GTFO_BEPINEX_PATH="$env:APPDATA\r2modmanPlus-local\GTFO\profiles\Forge-MapEditor-QA\BepInEx"
  $art="$env:TEMP\integ-dev\art"; $tart="$env:TEMP\integ-dev\art-tests"
  dotnet build ForgeRuntime/Framework/ForgeRuntime.Framework.csproj -c Release --artifacts-path $art -p:GTFOBepInExPath=$env:GTFO_BEPINEX_PATH
  dotnet build ForgeRuntime/ForgeRuntime.csproj            -c Release --artifacts-path $art -p:GTFOBepInExPath=$env:GTFO_BEPINEX_PATH
  dotnet build ForgeDevelopment/Native/ForgeDevelopment.Native.csproj -c Release --artifacts-path $art `
      -p:GTFOBepInExPath=$env:GTFO_BEPINEX_PATH -p:ForgeRuntimeAssembly=$art\bin\ForgeRuntime\release\ForgeRuntime.dll `
      -p:ForgeFrameworkAssembly=$art\bin\ForgeRuntime.Framework\release\ForgeRuntime.Framework.dll
  dotnet run --project ForgeDevelopment/tests/Recorder/Recorder.csproj -c Release --artifacts-path $tart
  dotnet run --project ForgeDevelopment/tests/PluginStartup/PluginStartup.csproj -c Release --artifacts-path $tart
  dotnet run --project ForgeDevelopment/tests/Experiments/Experiments.csproj -c Release --artifacts-path $tart -p:GTFOBepInExPath=$env:GTFO_BEPINEX_PATH
  dotnet build ForgeDevelopment/tests/Capture/Capture.csproj -c Release --artifacts-path $tart -p:GTFOBepInExPath=$env:GTFO_BEPINEX_PATH
  dotnet $tart\bin\Capture\release\DevelopmentCapture.dll $env:GTFO_BEPINEX_PATH ForgeDevelopment $env:TEMP\integ-dev\evidence\capture.json $env:TEMP\integ-dev\evidence\capture-detail.json
  dotnet build ForgeDevelopment/tests/NativeLayout/NativeLayout.csproj -c Release --artifacts-path $tart -p:GTFOBepInExPath=$env:GTFO_BEPINEX_PATH
  dotnet $tart\bin\NativeLayout\release\DevelopmentNativeLayout.dll $env:GTFO_BEPINEX_PATH `
      $art\bin\ForgeRuntime\release\ForgeRuntime.dll $art\bin\ForgeRuntime.Framework\release\ForgeRuntime.Framework.dll `
      $art\bin\ForgeDevelopment.Native\release\ForgeDevelopment.Native.dll $env:TEMP\integ-dev\evidence\native-layout.json
  ```

| 命令 | 结果 |
| --- | --- |
| `ForgeDevelopment.Native` 构建 | 退出 0，0 警告 0 错误（上一批遗留的 3 条可空警告同时修掉） |
| `tests/Recorder` | 115 断言、0 场景失败、退出 0（补编 `ExperimentTrace.cs`：`RecTracerPatches` 调它，之前该工程编不过） |
| `tests/PluginStartup` | 103 断言、0 场景失败、退出 0（启动顺序与回滚矩阵按新接线更新） |
| `tests/Experiments` | 305 检查、0 失败、退出 0 |
| `tests/Capture` | 28/28、退出 0（含收紧后的 points 检查） |
| `tests/NativeLayout` | 38/38、退出 0（hook 集合改为 18 个字面量、帧循环期望加 `CaptureMonitor`/`ExperimentRunner`） |

- 没有运行：`verify-diagnostics.py` 全量入口、其余测试工程与 Python 套件；也没有启动游戏或装进任何 profile。`plugins/trace` 的实际装载、`CaptureMonitor` 与 `ExperimentRunner` 作为托管 MonoBehaviour 被 `AddComponent` 后的真实行为、快照的真实行数与开销，都只能在游戏里确认。

## 运行记录 2026-09-16 — 记录器核心（dev-rec-core）

- 范围：`Native/RecSession.cs`、`RecReflect.cs`、`RecTracer.cs`、`RecTracerRuntime.cs`、`RecTracerPatches.cs`、`RecLog.cs`、`RecTime.cs`、`RecUnity.cs`、`RecForge.cs`、`RecIntegration.cs`、`RecRuntime.cs`、`RecSettings.cs`；`Plugin.cs`、`AuthoringSettings.cs`、`RuntimeDiagnostics.cs`、`ForgeDevelopment.Native.csproj` 的接入；`tests/Recorder/`（新建）、`tests/PluginStartup/`、`tests/NativeLayout/Program.cs`；`scripts/rec-query.py`、`scripts/rec-merge.py`、`tests/test_recorder_tools.py`；README 新增记录器与"怎么抓一个新的点"两节。
- 命令（仓库根目录，`Forge-MapEditor-QA` profile 只作编译引用；所有构建与运行输出都写进 `%TEMP%\dsh\dev-rec-core` 下的隔离目录）：

  ```powershell
  $env:GTFO_BEPINEX_PATH="$env:APPDATA\r2modmanPlus-local\GTFO\profiles\Forge-MapEditor-QA\BepInEx"
  $out="$env:TEMP\dsh\dev-rec-core"
  dotnet build ForgeRuntime/Framework/ForgeRuntime.Framework.csproj -c Release --artifacts-path $out\artifacts --disable-build-servers -p:GTFOBepInExPath=$env:GTFO_BEPINEX_PATH
  dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release --artifacts-path $out\artifacts --disable-build-servers -p:GTFOBepInExPath=$env:GTFO_BEPINEX_PATH
  dotnet run --project ForgeDevelopment/tests/Recorder/Recorder.csproj -c Release --artifacts-path $out\artifacts-tests
  dotnet run --project ForgeDevelopment/tests/PluginStartup/PluginStartup.csproj -c Release --artifacts-path $out\artifacts-tests
  python -m unittest ForgeDevelopment.tests.test_recorder_tools
  dotnet <NativeLayout.dll> $env:GTFO_BEPINEX_PATH <ForgeRuntime.dll> <ForgeRuntime.Framework.dll> <ForgeDevelopment.Native.dll> <report.json>
  ```

- 结果：宿主与 Framework 各 0 警告 0 错误；`tests/Recorder` 115 断言、0 场景失败、退出 0；`tests/PluginStartup` 82 断言、0 场景失败、退出 0；`tests/test_recorder_tools.py` 17 用例通过；NativeLayout 29/29、退出 0。**`ForgeDevelopment.Native.csproj` 的整目录构建本轮不通过**，原因不在本批文件：整目录剩 19 个错误，全部在 exp 批的 `ExperimentRuntime.cs`（`WardenObjectiveEventData` 未解析）与 `ExperimentTargeting.cs`（`EnemyAgent`/`FPCamera`/`RaycastHit?.collider`/`Physics.DefaultRaycastLayers` 等）。本批因此用一份剔除 exp 批文件的隔离副本编译，0 警告 0 错误；删掉 exp 批后整目录的报错里没有任何一条指向 `Rec*.cs`。详见下方"未运行的检查"。

| 套件 | 本次 | 结果 |
| --- | --- | --- |
| tests/Recorder（新建） | 115 断言，0 失败 | 会话分段/总量上限/丢弃计数/gzip/index、RecReflect 循环与深度与元素上限与异常、glob 与拒绝表与速率门与 `changes`、补丁记录与溢出摘要 |
| tests/PluginStartup | 82 断言，0 失败 | 启动顺序含 `recorder:start`，逐阶段失败矩阵含 `recorder:start` 的回滚 |
| tests/test_recorder_tools.py（新建） | 17 用例通过 | rec-query 的通道/方法/snetTime/书签窗口/gzip/截断行/表格与 JSON；rec-merge 的规则配对、单机事实、无规则退化、points.tsv 读取 |
| NativeLayout | 29/29，退出 0 | 静态补丁集合按 `[HarmonyPatch]` 断言，跟踪器断言为运行时装载 |

- 记录器的具体边界：
  - 会话是每进程一个目录，主线程只把记录组成字节入队，分段、gzip、总量上限、`index.json` 全在写线程；总量到上限后停写，把每个后续记录计入 `dropped`，同时写 `session` 通道并在屏幕左下角提示，不静默截断。
  - `RecReflect` 对循环写 `{"$ref":id}`，对超深写 `$depthCapped`，对超元素写 `$truncatedAt`，对读不出的成员写 `$error:...` 而不抛异常；单条记录超过 `RecordKiB` 时那条被丢弃并计数。
  - 跟踪器的拒绝表按理由分类：`frame-loop`（Update/LateUpdate/FixedUpdate）、`immediate-mode-ui`（OnGUI）、`render-loop`、`editor-gizmo`、`property-getter`、`generic`、`unsupported-parameter`、`patch-failed`；条目在 `methods` 里显式点名时可以覆盖前五类。启动时全部解析结果写进 `session` 通道。
  - 补丁抛异常时只写一条记录并自动卸下该补丁，不影响游戏；速率溢出按 `onOverflow` 处理并每 5 秒写一次摘要。
  - `forge` 通道把宿主的每条 `RuntimeLogRecord` 复制进来，不替换宿主自己的日志写入器。
- **实现，未测**：记录器在游戏里的实际行为没有跑过——不启动游戏是本轮的硬约束。具体没有验证的是：Unity 线程日志回调与 IL2CPP gchandle 的实际附加/释放、`Application.logMessageReceivedThreaded` 在退出路径上的行为、跟踪器在真实 interop 方法上的 HarmonyX 绑定（含 `__args` 对 ref/out 参数的绑定）、截图路径、内核后置补丁在真实宿主上的装载、以及两台机器上的 `snetTime` 对齐。
- **未运行的检查**：整套 `python ForgeDevelopment/scripts/verify-diagnostics.py --bepinex "$env:GTFO_BEPINEX_PATH"`（它会构建并运行全部套件与负例门槛）本轮没有跑，因为它要求整目录 `ForgeDevelopment.Native.csproj` 可编译，而 exp 批的在途错误让整目录构建失败；`tests/Reports`、`tests/ProjectChecks`、`tests/DevelopmentInspection`、`tests/ReportSnapshots`、`tests/Shutdown`、`tests/SceneInventory`、`tests/Telemetry`、`tests/Samples` 与 `ForgeDevelopment/tests/` 下的其余 Python 套件本轮也没有重跑——它们的输入没有变（本批只改了 `Plugin.cs`、`AuthoringSettings.cs`、`RuntimeDiagnostics.cs` 与 `NativeLayout`/`PluginStartup`）。由 Claude 在 exp 批修好后统一跑一次完整入口。

## 运行记录 2026-09-15 — 作者工具收口：loader/节点回指/负例门槛（U-DEV-MOD）

- 范围：把 VALIDATION 里"没有自动测试"的部分补成可复跑测试。新增 `tests/PluginStartup/LoaderDouble.cs` 与 `LoaderModes.cs`（按真实 `[BepInPlugin]`/`[BepInDependency]` 元数据模型加载器门槛）、`tests/DevelopmentInspection/NodeBackReferenceTests.cs`（生产 `WorldInspection` 的 CourseNode/item/terminal 回指检查）、`tests/ReportSnapshots/TraceBackReferenceTests.cs`（生成 trace 与节点回指负载的入队冻结）；`tests/Reports/Program.cs` 增加 trace 聚合上限回归；`scripts/verify-diagnostics.py` 增加负例门槛。未改 `Native/` 生产代码。
- 命令（仓库根目录，`Forge-MapEditor-QA` profile 只作编译引用）：

  ```powershell
  $env:GTFO_BEPINEX_PATH="$env:APPDATA\r2modmanPlus-local\GTFO\profiles\Forge-MapEditor-QA\BepInEx"
  python ForgeDevelopment/scripts/verify-diagnostics.py --bepinex "$env:GTFO_BEPINEX_PATH" --output "$env:TEMP\forge-devqa\verify-3"
  ```

- 结果：进程退出码 0；24 条子命令全部退出 0（含 12 次构建，均 0 警告 0 错误）；`summary.json` 为 `passed:true`、`sourceChangesDuringRun:[]`、`mutationGate {cases:5, detected:5, abnormalDetections:[]}`、`gameExecuted:false`、`installed:false`。

| 套件 | 本次 | 2026-09-14 全量 |
| --- | --- | --- |
| PluginStartup（含 loader/host 门槛） | 73 通过，0 场景失败 | 54 |
| Reports（含聚合上限回归） | 102/102 | 99/99 |
| ProjectChecks | 204/204 | 202/202 |
| DevelopmentInspection（含节点回指） | 71/71 | 47/47 |
| ReportSnapshots（含 trace 冻结） | 117/117 | 100/100 |
| Shutdown / SceneInventory / Telemetry / Samples | 31 / 8 / 56 / 7 | 31 / 8 / 56 / 7 |
| NativeLayout | 23/23 | 23/23 |
| Python 离线工具 | 41/41 | 41/41 |

- 新增断言覆盖的具体边界：
  - 加载状态：开发包未安装（程序集缺席即无插件、零副作用）；仅类型被加载而未调用 `Load`；宿主缺失或低于声明要求时加载器不构造插件；宿主已装但 Runtime 为 Off/Play 时只记一行 inactive；Authoring 走完整启动顺序。软依赖 InfiniTweaks 缺失可接受；进程内注入一个没有 `InfiniTweaks.Telemetry` 的旧版程序集时启动被拒绝，且只完成配置绑定、不获取任何阶段，失败后不能重试成 active。
  - 节点回指：`zone_course_nodes`、`area_course_node`、`area_navigation_data`、`area_navmesh_sample`、`item_course_node`、`terminal_item_node`、`terminal_setup` 的 observed / missing_data / mismatch 分支，以及事件里的 `courseNode`/`spawnNode`/`nodeContainsItem`/`zoneRegistersTerminal` 字段。NavMesh 采样失败只记 `sample_failed`，不当作不可达结论；节点没列出的 item 记为 `nodeContainsItem=False` 而不是 mismatch。
  - trace 冻结：入队后继续运行同一 job、完成扫描、更换 world 或替换 receipt，都不能改写已排队文件里的 trace 聚合 count/耗时/首末 random 状态、阶段开销 metadata、绑定原生对象的 check 与 issue、以及 world/epoch/tick/候选。
  - 有界聚合：不同 trace 上下文超过 1024 后不再新建聚合项，被拒绝的上下文计入 `droppedAggregateEvents`，已存在的聚合仍继续累加。

- **负例门槛（新增）**：`verify-diagnostics.py` 把包复制到证据目录后注入五个已知破坏，每个必须编译通过、且由对应套件的断言失败（退出码 1、正常结束、打印带失败项/总数的摘要与逐条 `FAIL:`）检出，否则整体返回非零。锚点缺失或多于一处按失败处理，不做模糊文本替换，工作树不被修改。崩溃、信号、超时或没有可归属的断言失败一律记 `cleanDetection:false`，只算“检出但异常”，不能当普通检出通过。

| 变异 | 注入的破坏 | 检出套件与结果 |
| --- | --- | --- |
| `snapshot-freeze` | `FrozenReport` 改为导出时才捕获报告 | ReportSnapshots 73/117 passed，44 条 FAIL，退出 1 |
| `world-isolation` | `ProjectInspectionSession.AcceptWorld` 去掉 epoch 相等判断 | DevelopmentInspection 68/71 passed，3 条 FAIL，退出 1 |
| `bounded-issues` | 删除 `MaxIssues` 上限分支 | Reports 100/102 passed，2 条 FAIL，退出 1 |
| `bounded-aggregates` | `MaxAggregates` 1024 → 1000000 | Reports 100/102 passed，2 条 FAIL，退出 1 |
| `shutdown-cleanup` | 首个清理失败后直接返回收据 | Shutdown 11/31 passed，20 条 FAIL（含收据索引断言），退出 1、无未处理异常 |

- 收口时修掉的一个测试缺陷：`tests/Shutdown/ReceiptTests.cs` 原来直接索引 `receipt[0..3]`。被变异的序列提前返回只有一项的收据时，越界异常逃逸，`shutdown-cleanup` 变异以 CLR 未处理异常码 3762504530 结束，后面五条断言根本没跑——那是测试自身的脆弱，不是产品缺陷。改用 `ElementAtOrDefault` 后，短收据按断言失败上报，20 条 `FAIL:` 全部执行。上一轮（`verify-1`）因此把这条变异记成 `detected:true` 普通检出；现在判定要求断言级检出，`abnormalDetections` 为空。

- 证据：`C:\Users\nainf\AppData\Local\Temp\forge-devqa\verify-3\`（`summary.json`、`mutations.json`（含 `cleanDetection`、`reportedFailures`、`reportedAssertions`）、各步 `*.log`、`mutation-*-run.log`、运行前后源码 hash；未入库，临时目录）。
- 未验证（仍需实机）：真实 BepInEx/IL2CPP chainloader 是否按声明加载本插件、三种安装状态的游戏内结果；原生 Hook 是否真正执行与 detour 是否安全；GC 之后的原生 callback；采集在主线程上的开销；两次进出图与切关后的残留；主客机、迟加入与检查点恢复；`RuntimeDiagnostics.Stop()` 的真实阶段列表（监听器反注册、gchandle 释放、最终导出与 writer 释放）没有可托管替身，本轮只覆盖了它依赖的 `ShutdownSequence` 顺序契约与变异检出。本轮未启动游戏、未安装到任何 profile。

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
