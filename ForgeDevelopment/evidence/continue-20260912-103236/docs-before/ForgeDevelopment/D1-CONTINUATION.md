# D1 报告快照复核与接续记录

2026-09-12。通过 Remote Desktop Commander 操作本机工作树，未直接访问 GitHub。
**状态：新增回归已交付；报告入队快照缺陷已复现，生产修复尚未写入，D1 保持 OPEN。**

## 本轮实际改动

- 新增 [ReportSnapshots 回归](tests/ReportSnapshots/README.md)：直接编译现有诊断源码，不复制生产实现。
- `ForgeDevelopment.csproj` 排除 `tests/**`，防止测试入口混入模块程序集；模块构建通过。
- 新增本文与 [复核证据目录](evidence/d1-snapshot-review-20260912/)。
- 已有 ProjectInspectionSession、RuntimeDiagnostics、WorldInspection 接线及其他会话新增测试均保留。

本轮尝试写入 DiagnosticsReport/AsyncReportWriter 的生产修复，被工具安全检查拦截。
随后 SHA-256 核对确认这两个文件仍与修复前备份一致；没有换工具或命令绕过拦截。
不能把内存中准备的修改、已有他人修复或成功构建记成本轮生产修复。

## 已复现的问题

`AsyncReportWriter.Request` 保存可变 DiagnosticsReport 和 outcome，后台才执行 Report.Export。
因此请求排队后继续观察、完成/取消扫描或重新附着报告，会改变尚未写盘的请求内容。
回归用一次故意失败的写盘和受控回调阻塞后台线程，不靠 Sleep 或原生游戏时序。
随后入队 pending/complete 两份报告，变更 world/tick、来源验证、计数和元数据，再释放线程。
当前结果为 **19/37 通过，18 项失败，进程退出码 1**；红测是缺陷证据，不是验收通过。

失败覆盖：入队快照时点、metadata/events/checks、聚合次数/耗时/随机状态、issue 次数、
旧 world/tick 回执、完成后取消、来源验证，以及同路径合并后仍读取后续可变状态。
队列 8 个待处理路径、超限拒绝、同路径合并准入、关闭拒绝与原子写入清理也在测试中。

## 实际运行结果

下表是各命令运行时的工作树结果，不是并发工作区的原子版本快照。

| 检查 | 本轮复跑结果 |
| --- | --- |
| 完整 ForgeRuntime 宿主 | 构建退出码 0 |
| ForgeDevelopment 模块 | 构建退出码 0 |
| DevelopmentInspection | 47/47，退出码 0 |
| ProjectChecks | 202/202，退出码 0 |
| Reports | 99/99，退出码 0 |
| Samples | 7/7，退出码 0 |
| SceneInventory | 8/8，退出码 0；托管遍历，不是原生耗时 |
| Telemetry | 56/56，退出码 0 |
| Python 报告工具 | 41/41，退出码 0 |
| 新增 ReportSnapshots | 构建成功；19/37，18 项失败，退出码 1 |
| 全模块 Architecture | 构建退出码 1；当时 ForgeEnemy/EnemyLifeTable.cs:57 为 CS1513 |

初次宿主构建遇到 RuntimeKernel.cs:224 的 CS1513；稍后复跑宿主已成功。
本轮未修复共享 Kernel 或 Enemy 文件，不能将这些并发状态变化归为本轮成果。
DevelopmentInspection 初跑 41 项、复跑 47 项，期间其他会话增加了测试；均保留原始日志。
现有 [D1-VALIDATION](D1-VALIDATION.md) 的更早宿主失败记录不否定上述较晚成功构建，
但较晚成功构建也不能消除本轮新增的报告快照红测。

构建使用 .NET SDK 10.0.400、现行 net6.0、既有 Temp profile 的只读 BepInEx 引用。
输出隔离到系统临时目录，不清理或共用其他会话的 bin/obj；没有写入游戏安装目录。

## 修复设计与关闭门槛（尚未实施）

入队时捕获冻结的托管报告数据及同一次 scan 的不可变回执；队列不能继续持有可变报告。
Metadata、事件/检查集合、异常与聚合计数、溢出计数及来源验证必须来自该次捕获。
保持既有报告格式；不新增兼容格式，不混入 Unity 对象，不复制 Runtime 的时钟或调度器。
字节预算裁剪、JSON 序列化和原子写盘仍在后台执行，不能把大报告序列化搬到游戏主线程。
捕获前检查队列准入，捕获后重新检查关闭/容量竞争；同路径合并替换整个冻结快照。

关闭前必须：新回归 37/37 通过；既有 Reports 的预算/并发/写盘失败测试继续通过；
ProjectChecks 与 DevelopmentInspection 保持通过；完整宿主和 Development 重新构建；
补充捕获开销证据，不能将纯托管测试当成 GTFO 帧时间、主客机或原生回调验收。

D2 仍需 R2 的实际 readiness/world/tick 与配置迁移正式交接，再一次迁移源码/入口。
仅公共生命周期类型出现或宿主构建通过，不足以证明独立诊断插件已可接入。
已有当前输入/报告格式说明见 [D1-VALIDATION](D1-VALIDATION.md#现行输入与报告合同)，不另造 schema。
网站导入方需知道：当前排队报告仍有时点缺陷；未完成修复前不能声称入队快照已经冻结。

## 证据与边界

原始日志：`%TEMP%/forge-development-continuation-20260912-101801/`。
关键构建/测试日志、源码收据及退出码复制到本文所链接的 evidence 子目录。
修复前源码备份仅保留在临时目录；证据目录不包含游戏 DLL、配置或用户素材。
`git diff` 不展示全部未跟踪源码，不能只靠它证明本批文件无差异。
没有安装、启动 GTFO、配置改写、独立插件迁移、发布、commit、push 或网站改动。
本轮没有完成生产快照修复，也没有将 D1–D7 或四类机制标为游戏已验证。
