# ForgeDevelopment

可选的作者诊断包：观察生成、空间、性能、异常与运行证据，关联到作者对象并输出报告。

**普通玩家默认不依赖这个包。** 它不调度正常玩法，也不把"没有报错"解释为地图正确。需要玩家自己启用诊断回传才能用起来的能力不算完成——诊断是作者型用户的第二层入口，不是玩家必经路径。

计划与状态见两仓统一框架第 6 节 U-DEV-MOD（链接见[仓库 README](../README.md)），带日期的验证记录见 [VALIDATION.md](VALIDATION.md)。**所有性能相关内容都归本包**，一般游玩用不到；InfiniTweaks 只放 Quality of Life 功能。历史记录：[1.1.0 制作端范围](AUTHORING-SCOPE.md)、[InfiniTweaks 标记与 HUD 性能对照](PERFORMANCE-REVIEW.md)。

## 工程与启动门槛

两个工程：

| 工程 | 内容 |
| --- | --- |
| `ForgeDevelopment.csproj` | 只引用 `ForgeRuntime.Framework` 的 SDK 程序集；`ModuleDefinition.Create()` 仍是空 provider，排除 `Native/**` 与 `tests/**` |
| `Native/ForgeDevelopment.Native.csproj` | 真实 BepInEx 插件 `ForgeDevelopment.Native.dll`，包含全部诊断源码（原 `ForgeRuntime/` 根目录的 16 个文件）与 10 个诊断 Hook |

插件身份是 `[BepInPlugin("NAinfini.ForgeDevelopment", "Infini Forge Development", "1.0.0")]`，硬依赖 `NAinfini.ForgeRuntime` 1.2.0，软依赖 InfiniTweaks（同装时要求 2.5.0 或更新，旧版包含重复采集器）。原生工程只引用宿主 `ForgeRuntime.dll` 与 SDK，不内嵌第二个内核；构建时必须显式传入 `ForgeRuntimeAssembly`、`ForgeFrameworkAssembly` 与 `GTFOBepInExPath`，缺一即失败。

**启动门槛：** 只有 `ForgeRuntime.Plugin.ConfiguredMode == Authoring` 时才启动。`Off` 与 `Play` 记录一行 inactive 日志后返回，不绑定配置、不打 Hook、不加组件；`Authoring` 但 `Plugin.Runtime` 为 null（宿主启动失败）时 Load 抛出。启动顺序是绑定配置 → `RuntimeDiagnostics.Initialize` → 本程序集的 `harmony.PatchAll` → `AuthoringMonitor` → 可选的 `PerformanceMonitor`；任一步失败按已获取阶段逆序清理（Hook → 性能组件 → 作者组件 → 诊断停止），失败收据写入 `Data["ForgeDevelopment.StartupCleanupFailures"]`，原始异常不被替换，同一实例不重试。

本插件不登记 provider，不向 Runtime 提交玩法请求。报告 metadata 的 `forgeVersion` 是宿主版本，另写 `developmentVersion`；问题来源名为 `ForgeDevelopment`。

## 安装与配置

未来的开发包在 Runtime 之外另放 `ForgeDevelopment.Native.dll`；普通玩家包不包含它。**本轮未安装。**

配置文件是独立的 `BepInEx/config/NAinfini.ForgeDevelopment.cfg`（节选；`[Performance Diagnostics]` 另有摘要间隔、卡顿阈值、profiler 与资源快照等键）：

```ini
[Authoring]
ExportReportKey = F10
InspectionBudgetMilliseconds = 2
ProjectManifest =

[Performance Diagnostics]
EnablePerformanceLogging = true
ManualSnapshotKey = F9
```

**不迁移旧配置**：原来写在 `NAinfini.ForgeRuntime.cfg` 的 `[Authoring]` 与 `[Performance Diagnostics]` 键被忽略，Runtime 的旧 `Off` 也不会让本包启用。

F9 请求性能快照，F10 导出当前生成报告，F11 开关战斗采样。报告写入 `BepInEx/ForgeReports`，性能日志写入 `BepInEx/PerformanceLogs`（`ForgeDevelopment_*.log` 与短时 `ForgeDevelopment_UnityProfile_*.raw`）。关卡结束和进程退出也请求导出。写入失败会报告错误；看 JSON 里的 `dropped` 和检查状态，不能只看 `outcome`。只关闭 `EnablePerformanceLogging` 只关闭性能采集。

网站离线包导出（`site/map-package.ts`，网站 `62041354` 起）只给 `NAinfini.ForgeRuntime.cfg` 写 `[Runtime] Mode`，不再写 `[Authoring] ProjectManifest`。玩家包不包含本插件（D-007：开发层不进玩家包）。

## 现行输入与报告合同

项目清单的根对象必须包含 `format`、`projectId`、`experiment`、`requiredPlugins`、`sources`、`objectReferences`，不接受额外或重复字段。`format` 固定为 `gtfo-forge-project`；**不接受 `schemaVersion` 或 `expectedObjects`，不做旧格式 fallback**。

`experiment` 必须包含 `packageVersion`、`authoringSha256`、`dependencies`，dependencies 是字符串可以为空。`requiredPlugins` 条目是 guid 与 version（精确版本，按相等判定，I-RELEASE D-018）；`sources` 是 path、sha256、kind，kind 只接受 DataBlock / LGTuner / ModConfig；hash 是小写 SHA-256。

对象声明只支持 zone 与 room。Zone locator 明确 layoutId、dimension、layer、localIndex；room locator 是 unique-geomorph-in-zone，明确 zoneAuthorId、room 的 id 与 revision 和完整的 `Assets/` 来源身份。**显示名称、Hierarchy 路径、预览模型都不代替创建证据。**

`ProjectManifest` 配置是本地文件选择器，相对路径以 BepInEx 为基准；它与清单内 `sources` 的安全规则不同——sources 必须是 BepInEx 内的规范相对路径，用正斜杠，禁止绝对路径、盘符、空段、点段、上级目录和重解析点逃逸。

清单上限 4 MiB，严格 UTF-8 与 JSON、深度 32。来源扫描上限：256 目录、4096 条目、256 文件、单文件 8 MiB、总计 64 MiB；报告上限 16 MiB。`sourceCoverageStatus=complete` **只表示采样覆盖结束**，不表示 `sourceVerification=matched`，更不证明内存或游戏行为一致。

报告的 format 是 `gtfo-forge-diagnostics-report`，`objectReferences` 独立记录 worldEpoch、simulationTick、scanStatus、sourceVerification、groups 与 overflow。遍历是分帧观察，**不是原子世界快照**；完整的局部遍历也不证明目标、导航或远征可以完成。对象存在不等于任务脚本正确，`FactoryDone` 只表示观察到结构生成完成；现有诊断没有宣称修复 R7D2 随机异常或连续切关的 CullingCluster 异常。

## D1 修好的具体行为

`ProjectChecks.Load` 返回该报告实际附着的 scan，严格清单校验和来源采样保留。`ProjectInspectionSession` 绑定固定的 report、scan 与 Runtime worldEpoch，分别处理重复开始、扫描前取消、旧世界的迟到完成、无世界上下文和部分扫描。`RuntimeDiagnostics` 使用公共 Runtime 的 worldEpoch 与 currentTick，在生成、切关、退出时清理扫描与来源请求；跨报告或跨世界的旧 JobTrace 不写入新报告。`WorldInspection` 的原生实例 ID 与显示路径分开；活动布局只取实际主维度的已生成层。

**Geomorph 的 prefab 名称不是作者来源的证明。** 缺少创建上下文时保留 `creation_context_unverified`，不伪造房间匹配。

报告在 Enqueue 时就捕获不可变的托管证据；之后的新世界、扫描完成、来源验证或取消都不能改写已经排队的旧快照。序列化、字节预算裁剪和原子写盘在 writer 线程，后台不读取 Unity 或 IL2CPP 对象。队列保留 8 个 pending 路径上限。`ShutdownSequence` 继续执行所有清理阶段并返回只读、有序的失败收据。

## 制作工具

使用 Python 3.10+ 标准库，不下载依赖也不执行外部代码：

```powershell
python ForgeDevelopment/scripts/compare_runs.py before.json after.json --output comparison.json
python ForgeDevelopment/scripts/import_log.py BepInEx.log imported.json
python ForgeDevelopment/scripts/analyze_performance.py session.log --output performance.md
```

离线性能分析按远征分段、计算帧加权 FPS、排出慢分钟窗口与长帧，报告扫描开销、被检测的模组路径、内存趋势和缺失的通道。它不会把窗口分位数平均成整场分位数、不会仅凭增长断定内存泄漏、不会仅凭一个异常归咎某个模组。重复生成需要固定游戏版本、插件、配置、资源和种子；报告对照会明确缺失的输入，不根据差异自动推断 bug。

## 复跑

```powershell
python ForgeDevelopment/scripts/verify-diagnostics.py --bepinex "$env:GTFO_BEPINEX_PATH"
```

脚本依次构建宿主、SDK 模块、原生插件，运行插件启动、报告、项目规则、检查、快照、关机、场景清单、遥测、采样、原生布局与 Python 套件。默认新建系统临时目录，也可以指定一个尚不存在的 `--output`；源码变化或任何失败都返回非零。完整的套件清单与结果见 [VALIDATION.md](VALIDATION.md)。

## 边界

当前 DLL 不应作为玩家发行物。没有 GTFO 加载、多人、原生 Hook 安全或采集开销的验收；"不安装 / 安装但未启用 / 启用"三种实际加载模式也未在游戏里核验。InfiniTweaks 的 QoL 行为不属于本模块。禁止无限重试、重置指定 prefab 种子、静默替换房间、删除任务对象、任意挂接 CourseNode、吞掉异常或调低游戏日志级别来伪装成功。
