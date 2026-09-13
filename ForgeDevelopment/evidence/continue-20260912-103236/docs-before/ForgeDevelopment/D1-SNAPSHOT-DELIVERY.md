# D1 入队快照修复与 D2 迁移准备

2026-09-12。本次继续通过 Remote Desktop Commander 操作本地工作树。
最新结论：D1 完整宿主构建与托管诊断回归通过；D2 的文件/配置/插件入口切换尚未执行。
这不是 GTFO 加载、多人、原生 Hook 安全或采集开销验收，也不关闭 D3–D7。

## 本次生产修复

`ForgeRuntime/DiagnosticsReport.cs` 在 Enqueue 时捕获不可变托管证据；
`ForgeRuntime/AsyncReportWriter.cs` 排队的是 FrozenReport，不再保存可变报告供以后读取。
metadata、事件/检查、异常/聚合计数、overflow、objectReferences 与导出时间来自同一次捕获。
新世界、扫描完成、来源验证完成或之后的取消不能改写已经排队的旧快照。
序列化、原有字节预算裁剪和原子写盘仍在 writer 线程；不在后台读取 Unity/IL2CPP。
队列保留 8 个 pending 路径上限；同路径替换整个快照，满队列的新路径明确拒绝。
未更改报告 format、权限、原生玩法、目标选择或来源匹配规则。

## 确定性失败与修复证据

本轮受控 writer 闸门测试首先为 9/21，通过修复后为 21/21，扩展后为 63/63。
复核发现先前会话的 Program.cs 曾被本轮新入口覆盖；已从工具历史完整恢复为
`PreservedSnapshotTests.cs`，原 37 项断言逐项保留并与新增 63 项一起执行，最终 100/100。
恢复原文保存在 evidence 的 `prior-snapshot-regression-recovered.txt`，不是删除失败断言。
该原文新增覆盖来源 pending→matched、完成后取消、随机状态与捕获时间边界。
历史 19/37 和工具拒绝记录保留在 D1-CONTINUATION.md，不当成最新源码状态。

证据目录：[continue-20260912-103236](evidence/continue-20260912-103236/)。

## 最终独立验证

全部使用新建隔离输出目录；构建失败不运行旧 DLL。
`final-verification/summary.json` 的 passed=true，源码前后 hash 一致。

| 检查 | 结果 |
| --- | --- |
| 完整 Runtime / Development 构建 | 均 0 警告、0 错误 |
| Reports | 99/99 |
| ProjectChecks | 202/202 |
| DevelopmentInspection | 47/47 |
| ReportSnapshots | 100/100 |
| Shutdown | 31/31 |
| SceneInventory / Telemetry / Samples | 8/8、56/56、7/7 |
| Python 报告与实际网站导入边界 | 41/41 |
| 编译后宿主与公共 SDK 生命周期 | 50 项通过，0 组失败，源码未变 |

扫描、Shutdown 和 Python 测试修复由并发诊断任务交付，本次独立复验，不冒充本次独有改动。
日志及精确命令/退出码保存在 `final-verification/`；实际 DLL/API 证据在 `host-integration/`。
只保存日志和 JSON 收据，不在证据目录复制游戏/第三方 DLL。

复跑入口：
```powershell
python ForgeDevelopment/scripts/verify-diagnostics.py --bepinex "$env:GTFO_BEPINEX_PATH"
```
默认新建系统临时目录；可指定尚不存在的 `--output`。源码变化或任何失败均返回非零。

## D2 可直接接续的输入

`d2-migration-inventory.json` 记录 15 个诊断源码、21 个测试/脚本的 source→target、hash，
以及 5 个共享切换文件。消费者行号是文本引用清单，不冒充语义调用图；迁移前重核 hash。
增加迁移表原先未列出的 ProjectInspectionSession 和 DevelopmentInspection；
本目录新增 ReportSnapshots、Shutdown 的编译链接及 verify-diagnostics 路径也必须同步更新。

公共接入已存在：`ForgeRuntime.Plugin.Runtime` 与模块句柄 `ObserveLifecycle`。
Off/停止时入口为 null，未 Advance 的 tick=-1/authority=null 不作为已观测世界数据；
注册发生在依赖插件 Load，早于首个 FixedUpdate 的 StartRuntime 冻结点。
详见 [Runtime R2b 交接](../ForgeRuntime/R2B-MIGRATION-HANDOFF.md)。

一次切换必须共同完成以下实际改动，不启动第二套采集器：
1. 迁移诊断源及其 namespace/测试链接，保留唯一 Report/Scan 实现和当前未提交内容。
2. Runtime 的 Plugin 移除作者采集初始化、两个作者 monitor、诊断失败清理和可选 Telemetry 装配；
   原 FrameworkMonitor、世界时钟、游戏阶段和权限仍留宿主。
3. AuthoringSettings 拆分：Mode/PlanPath/AllowedPermissions 归 Runtime；报告/扫描/性能选项归 Development。
   新 GUID、配置名、producer 身份与一次性迁移由 R2b 同批核定；旧 Off 不得自动变成启用。
4. 独立 Development 入口只有显式启用且真实 Runtime 可用时才订阅、采集和登记自己的 provider；
   失败与退出逐项清理，正常玩法不得依赖 Development。仅 Runtime 分发 SDK。
5. 修改宿主 Hook 选择、双方 csproj 与原生签名测试，然后核验不安装/关闭/启用三种实际加载模式。

本次未改写这 5 个共享入口文件、未移动生产源码；避免与 Runtime 负责人并发进行半套切换。
D2 迁移仍 OPEN。采集冻结自身的游戏主线程成本、两次进出图、GC 后原生 callback、
GTFO 主客机/恢复、独立插件真正加载、安装与发布全部未执行。
