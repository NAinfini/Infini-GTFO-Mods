# ForgeDevelopment 验证记录

**上次更新：2026-09-13**（本文件为新建，内容合并自原 CONTINUATION-STATUS、D1-CONTINUATION、D1-INTEGRATION-REVIEW、D1-SNAPSHOT-DELIVERY、D1-VALIDATION 五份交接记录）。

## 当前结论

D1 交付完成：诊断接线修好，报告的入队快照缺陷已修复，完整宿主与 Development 构建都通过，0 警告 0 错误。**D2 的文件、配置与插件入口切换尚未执行**，全部诊断源码仍在 `ForgeRuntime/` 内。这不是 GTFO 加载、多人、原生 Hook 安全或采集开销的验收，也不关闭 D3–D7。

## 最后一次记录的通过数

| 套件 | 结果 |
| --- | --- |
| ReportSnapshots | 100/100（含保留的原 37 项 + 新增 63 项） |
| Reports | 99/99 |
| ProjectChecks（含新增 D1 用例） | 202/202 |
| DevelopmentInspection | 47/47 |
| Shutdown | 31/31 |
| SceneInventory / Telemetry / Samples | 8 / 56 / 7 |
| 编译后宿主 + 公共 SDK 集成 | 50 项断言，验证期间无源码漂移 |
| Python 离线工具 | 41 项中 40 通过，见下文 |

Python 的 `test_import_log.py::test_cli_output_is_accepted_by_the_website_report_reader` 仍硬编码 `site/map-balance-report.js`，而网站实际文件已改名为 `site/map-balance-report.ts`。曾有一次诊断运行只在内存里把常量改成 `.ts` 路径并通过了同一条跨端断言，**源码测试没有被修改，那次诊断不计作未改动套件的通过**。修复归本包与网站责任任务共同完成；不引入 fallback reader、跳过断言或弱化报告 schema。

ProjectChecks 的新增用例覆盖：同一次 scan 的报告快照、导出文件不回写、旧 world 回调拒绝、取消前后与新 run 的隔离、开始前 partial、拒绝清单、未配置清单、未知来源、来源 pending 与 mismatch、错误 scan 所有者、重复 Start。**合成世界只验证 C# 合同，不是 GTFO 实测。**

## 入队快照缺陷的复现与修复

`AsyncReportWriter.Request` 原来保存的是可变的 `DiagnosticsReport` 和 outcome，后台线程才执行 `Report.Export`。因此请求排队之后继续观察、完成或取消扫描、重新附着报告，都会改变尚未写盘的请求内容。

回归用一次故意失败的写盘和受控回调阻塞后台线程复现（**不靠 Sleep 或原生游戏时序**），随后入队 pending 与 complete 两份报告，改变 world、tick、来源验证、计数和元数据，再释放线程。**首次结果是 19/37 通过、18 项失败、退出码 1——红测是缺陷证据，不是验收通过。**

修复后 `DiagnosticsReport.cs` 在 Enqueue 时捕获不可变的托管证据，`AsyncReportWriter.cs` 排队的是 FrozenReport。受控的 writer 闸门测试从 9/21 → 21/21 → 扩展后 63/63。复核时发现先前会话的 `Program.cs` 曾被本轮的新入口覆盖，已从工具历史完整恢复为 `PreservedSnapshotTests.cs`，原 37 项断言逐项保留并与新增 63 项一起执行，最终 100/100。**恢复的原文保存在证据目录里，不是删除失败断言换取通过。** 恢复的用例保留了来源 pending→matched、完成后取消、随机状态与捕获时间边界。

第一次尝试写入这两个生产文件时被工具安全检查拦截；随后 SHA-256 核对确认两个文件仍与修复前的备份一致，**没有换工具或命令绕过拦截**，也没有把内存中准备好的修改、别人的修复或成功构建记作本轮的生产修复。

## DevelopmentInspection 与 Shutdown

DevelopmentInspection 的基线复现：完整 Runtime 宿主构建退出 0、0 警告 0 错误；DevelopmentInspection 构建退出 1，报四个缺失 `Dimension.Pointer` 的错误和 14 条 nullable 警告。**生产的 pointer 检查保留**，修的是原生身份替身。修好后新增六条断言（不同指针、wrapper 别名、外来 layer、重复 layer），共 47 项。

Shutdown 的失败复现：原始代码 21 项检查只过 10 项、11 项失败。`ShutdownSequence` 改为继续执行所有清理阶段并返回一份只读、有序的失败收据后 31/31。

## 复跑

```powershell
python ForgeDevelopment/scripts/verify-diagnostics.py --bepinex "$env:GTFO_BEPINEX_PATH"
```

默认新建系统临时目录，也可以指定一个尚不存在的 `--output`；源码变化或任何失败都返回非零。

单跑各套件（迁移之后这些路径会变）：

```powershell
dotnet run --project ForgeDevelopment/tests/ReportSnapshots -c Release
dotnet run --project ForgeDevelopment/tests/Shutdown -c Release
dotnet run --project ForgeRuntime/tests/Reports -c Release
dotnet run --project ForgeRuntime/tests/ProjectChecks -c Release
dotnet run --project ForgeRuntime/tests/DevelopmentInspection -c Release
python -m unittest discover -s ForgeRuntime/tests -p 'test_*.py'
```

ReportSnapshots 的说明见 [tests/ReportSnapshots/README.md](tests/ReportSnapshots/README.md)。`ForgeDevelopment.csproj` 排除 `tests/**`，防止测试入口混进模块程序集。

## D2 的接续输入

`d2-migration-inventory.json` 记录 15 个诊断源码、21 个测试与脚本的 source→target 与 hash，以及 5 个共享切换文件。消费者行号是文本引用清单，**不冒充语义调用图；迁移前重核 hash**。切换的五项要求见 [IMPLEMENTATION-PLAN 的 D2 节](IMPLEMENTATION-PLAN.md#d2--拆出真正可选的-development-插件未做b-批唯一缺口)。

本轮没有改写那 5 个共享入口文件，也没有移动生产源码——**避免与 Runtime 负责人并发进行半套切换**。

## 边界

以上是托管逻辑与合成输入的证据。采集本身在游戏主线程的成本、两次进出图、GC 之后的原生 callback、GTFO 主客机与恢复、独立插件的真正加载、安装与发布，全部未执行。并发的扫描、shutdown、Python 与 Runtime 修复由各自的所有者独立重测，不计入本包的成果。

没有安装到游戏 profile、运行 GTFO、发布、commit、push 或修改网站。Temp profile 只作为本地构建的程序集引用，构建输出在独立的临时目录。
