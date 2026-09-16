# ForgeDevelopment

> 实施顺序与原版内容完整覆盖见[唯一开发计划](../../Infini-GTFO-Model-Site/Docs/forge-contract/FORGE-FRAMEWORK.md) §4，分步验收见 §7；本文件仅说明实现与用法。

可选的作者诊断包：观察生成、空间、性能、异常与运行证据，关联到作者对象并输出报告。

**普通玩家默认不依赖这个包。** 它不调度正常玩法，也不把"没有报错"解释为地图正确。需要玩家自己启用诊断回传才能用起来的能力不算完成——诊断是作者型用户的第二层入口，不是玩家必经路径。

交付要求见唯一开发计划 §6 U-DEV-MOD（链接见[仓库 README](../README.md)），带日期的验证记录见 [VALIDATION.md](VALIDATION.md)，版本历史见 [CHANGELOG.md](CHANGELOG.md)。**所有性能相关内容都归本包**，一般游玩用不到；InfiniTweaks 只放 Quality of Life 功能。作者工具范围与验收见唯一开发计划 §6 U-DEV-MOD 和 §7。

## 工程与启动门槛

只有一个工程：

| 工程 | 内容 |
| --- | --- |
| `Native/ForgeDevelopment.Native.csproj` | 真实 BepInEx 插件 `ForgeDevelopment.Native.dll`，包含全部诊断源码（原 `ForgeRuntime/` 根目录的 16 个文件）、18 个 Hook（10 个诊断 + 8 个快照边界）与全量记录器 |

托管 SDK 程序集已删除：原来只登记空 provider 的 `ForgeDevelopment.csproj` 与 `ModuleDefinition.cs`（`forge.module.development` 0.1.0）都不再存在，也没有测试种子保留这个身份；包版本只取 `Native/Plugin.cs` 的 `PluginVersion`。

插件身份是 `[BepInPlugin("NAinfini.ForgeDevelopment", "Infini Forge Development", "1.0.0")]`，硬依赖 `NAinfini.ForgeRuntime` 1.2.0，软依赖 InfiniTweaks（同装时要求 2.5.0 或更新，旧版包含重复采集器）。原生工程只引用宿主 `ForgeRuntime.dll` 与 SDK，不内嵌第二个内核；构建时必须显式传入 `ForgeRuntimeAssembly`、`ForgeFrameworkAssembly` 与 `GTFOBepInExPath`，缺一即失败。

**启动门槛：** 只有 `ForgeRuntime.Plugin.ConfiguredMode == Authoring` 时才启动。`Off` 与 `Play` 记录一行 inactive 日志后返回，不绑定配置、不打 Hook、不加组件；`Authoring` 但 `Plugin.Runtime` 为 null（宿主启动失败）时 Load 抛出。启动顺序是绑定配置 → `RuntimeDiagnostics.Initialize` → `RecRuntime.Start`（会话 → 日志捕获 → 跟踪器 → 内核通道）→ 本程序集的 `harmony.PatchAll` → `AuthoringMonitor` → `CaptureRegistry.EnsureStarted`（把 `CaptureMonitor` 挂到同一个 GameObject）→ `ExperimentRunner` 与 `ExperimentPanel` 组件 + `ExperimentPanel.Load()` → 可选的 `PerformanceMonitor`；任一步失败按已获取阶段逆序清理（实验面板 → 实验 runner → capture → 记录器 → Hook → 性能组件 → 作者组件 → 诊断停止），失败收据写入 `Data["ForgeDevelopment.StartupCleanupFailures"]`，原始异常不被替换，同一实例不重试。记录器内部各阶段自己降级：会话开不出来才整体跳过，日志/跟踪/内核任一段失败都只记一条 `stage_failed` 并继续跑其余通道。

capture、experiments 与记录器在同一个程序集里，集成批之后全部是编译期直接引用：`RecRuntime` 在层级边界与关卡清理时直接调 `CaptureRegistry.SnapshotAll`，热键从 `RecHotkeys` 直接调 `CaptureRegistry.SnapshotAll("hotkey")`（F7）与 `ExperimentPanel.Toggle()`（F5），`Plugin.Load` 直接调 `CaptureRegistry.EnsureStarted`、`ExperimentPanel.Load()`。原来的按签名反射查找层（`RecIntegration`）已经删除，不再留第二条路径。`forge` 通道通过 HarmonyX 后置补丁挂在宿主内核唯一的记录点 `RuntimeKernel.WriteLog` 上，宿主构建里没有这个方法时只报告、不静默。

本插件不登记 provider，不向 Runtime 提交玩法请求。报告 metadata 的 `forgeVersion` 是宿主版本，另写 `developmentVersion`；问题来源名为 `ForgeDevelopment`。

## 安装与配置

未来的开发包在 Runtime 之外另放 `ForgeDevelopment.Native.dll`；普通玩家包不包含它。**本轮未安装。**

配置文件是独立的 `BepInEx/config/NAinfini.ForgeDevelopment.cfg`（节选；`[Performance Diagnostics]` 另有摘要间隔、卡顿阈值、profiler 与资源快照等键）：

```ini
[Authoring]
ExportReportKey = F10
InspectionBudgetMilliseconds = 2
ProjectManifest =

[Recorder]
Enabled = true
BudgetGiB = 2
SegmentMiB = 8
GzipSegments = false
TraceEnabled = true

[Performance Diagnostics]
EnablePerformanceLogging = true
ManualSnapshotKey = F9
```

**不迁移旧配置**：原来写在 `NAinfini.ForgeRuntime.cfg` 的 `[Authoring]` 与 `[Performance Diagnostics]` 键被忽略，Runtime 的旧 `Off` 也不会让本包启用。

F9 请求性能快照，F10 导出当前生成报告，F11 开关战斗采样。报告写入 `BepInEx/ForgeReports`，性能日志写入 `BepInEx/PerformanceLogs`（`ForgeDevelopment_*.log` 与短时 `ForgeDevelopment_UnityProfile_*.raw`）。关卡结束和进程退出也请求导出。写入失败会报告错误；看 JSON 里的 `dropped` 和检查状态，不能只看 `outcome`。只关闭 `EnablePerformanceLogging` 只关闭性能采集。

## 全量记录器

Authoring 模式下另有一个逐条记录整个会话的记录器，写入 `BepInEx/ForgeReports/rec-<时间>-<host|client>-<slot>/`。它的用途是"以后出现新问题时还能回头查"，所以默认开着、按通道落盘、不依赖任何一份具体的验证点清单。

配置键在 `[Recorder]` 节：

```ini
[Recorder]
Enabled = true
GzipSegments = false
BudgetGiB = 2
SegmentMiB = 8
RecordKiB = 64
QueueRecords = 16384
TraceEnabled = true
BookmarkKey = F6
BookmarkLabel =
SnapshotKey = F7
ScreenshotKey = F8
ExperimentPanelKey = F5
```

每个会话目录里是若干 `rec-<session>-<序号>.jsonl` 分段（开 gzip 时是 `.jsonl.gz`）、截图目录 `shots/` 和一份 `index.json`。`index.json` 记录通道计数、分段列表、丢弃数、是否触到总量上限以及装了哪些补丁；`patches` 字段就是跟踪器的启动报告，所以一个会话能自己说明它当时在跟踪什么。

每条记录是一行 JSON，固定字段是 `v`（`forge.rec.v1`）、`seq`、`session`、`channel`、`kind`、`t`（进程毫秒）、`frame`、`snetTime`（玩家同步时间，取到才有）、`role`、`slot`、`level`（远征 key）、`worldEpoch`、`tick`，业务内容在 `body` 里。通道含义见下表。

| 通道 | 内容 |
| --- | --- |
| `session` | 开始、启动事实、跟踪配置解析结果、补丁装载与卸载、实验命令装载、关卡边界、收尾阶段与摘要 |
| `tracer` | 原生方法调用、`changes` 变化、速率溢出计数、首次命中调用栈、补丁自身错误 |
| `net` | SNet 包与状态复制（capture 批） |
| `state` | 快照（capture 批；关卡对象按 `LG_Floor` 层级遍历，被禁用的门/终端/物件也在里面，每个对象行带 `active`） |
| `forge` | 内核事件、计划装载、命令与 presentation 投递、诊断行、内核状态轮询 |
| `log` | Unity 线程日志、BepInEx 监听、进程未处理异常 |
| `mark` | 书签 |
| `shot` | 截图路径 |
| `exp` | 实验（exp 批） |
| `probe` | 验证点命中（capture 批） |

限制与失败都是显式的：单条记录超过 `RecordKiB` 时那条被丢弃并计数，总量到 `BudgetGiB` 后停写并把每个后续记录计入 `dropped`，屏幕左下角和 `session` 通道会说明发生了什么。写入线程只拿到已经序列化好的字节，游戏线程只做入队；记录器自己不会读 IL2CPP 对象。

## 怎么抓一个新的点

一个还没验证过的行为点，从"我在游戏里想知道这里发生了什么"到"我手里有一条记录"，是三步，不需要改代码、不需要重新发版：

**1. 加一份跟踪配置。** 配置放在 `BepInEx/config/ForgeDevelopment/trace/`，第一次运行时自动从包的 `probes/trace/` 复制过去；之后只读 config 目录，你的改动不会被插件更新覆盖。

```json
{ "profile": "my-door-check", "enabledByDefault": true, "entries": [
  { "type": "LevelGeneration.LG_SecurityDoor", "methods": ["OnOpen", "OnClose"],
    "record": "args+result", "instance": ["m_sync.m_stateReplicator.State", "Gate.m_linksTo"],
    "firstStack": true, "maxPerSecond": 50, "onOverflow": "count" }
]}
```

- `type` 支持通配符，写 `*LG_SecurityDoor*` 也行；`methods` 与 `exclude` 同样支持 `*`。
- `record` 是 `count`、`args`、`args+result` 或 `changes`。`changes` 只在参数或 `instance` 字段与上次不同时记一条，适合每帧都会跑的方法。
- `instance` 是在实例上读的字段路径，由 `RecReflect` 读，私有字段也能读。
- `firstStack` 在第一次命中时记一次托管调用栈。
- `maxPerSecond` 之外按 `onOverflow` 处理，并每 5 秒写一条溢出计数；`Update`、`LateUpdate`、`FixedUpdate`、`OnGUI` 和属性 getter 默认只计数或被跳过，想覆盖就把它写进 `methods`。

启动时每个条目的解析结果都会写进 `session` 通道：匹配到几个方法、跳过几个、原因是什么。方法签名是泛型、带指针或带 `ref` 参数时跳过，原因是 `generic` 或 `unsupported-parameter`。

**2. 进游戏，装上这个包，触发那个行为。** 需要的话按 F6 打书签（带上你当时看的方向）、F7 要一份全量快照、F8 截一张图。整个过程不需要重启插件，改完配置重启游戏即可。

**3. 离线查。** 会话目录就是输入：

```powershell
# 这个会话有哪些通道
python ForgeDevelopment/scripts/rec-query.py "$env:GTFO_BEPINEX_PATH/ForgeReports/rec-20260916T101500Z-host-0" --list-channels

# 只看门的方法调用
python ForgeDevelopment/scripts/rec-query.py <session> --channel tracer --type LG_SecurityDoor --limit 20

# 只看 snetTime 10 到 20 秒之间发生了什么
python ForgeDevelopment/scripts/rec-query.py <session> --since 10 --until 20

# 书签前后 5 秒
python ForgeDevelopment/scripts/rec-query.py <session> --around 14.2 --window 5

# 给脚本用的 JSON
python ForgeDevelopment/scripts/rec-query.py <session> --channel forge --json
```

两台机器都开这个包时，`rec-merge.py` 合并两份会话，按 `snetTime` 对齐，并指出"客户端发生了、主机没收到"的事实。配对规则由 capture 批写在 `probes/points.tsv` 的最后一列，工具读那个文件：

```powershell
python ForgeDevelopment/scripts/rec-merge.py host=<主机会话目录> client=<客户端会话目录> --points ForgeDevelopment/probes/points.tsv
```

规则写法是 `trace:<通道>/<kind>[/<类型>.<方法>]#<名字>=<body 内路径>[,<名字>=<路径>][;window=<秒>]`，例如 `trace:net/replicator#m_state=fields.m_state`。没有规则时工具仍然合并，退化成按通道/kind/类型/方法配对，并在输出里写明那一批配对不是规则给的。库内没有的 `points.tsv` 会明确报出来，不会静默按规则名配对。


网站离线包导出（`site/map-package.ts`，网站 `62041354` 起）只给 `NAinfini.ForgeRuntime.cfg` 写 `[Runtime] Mode`，不再写 `[Authoring] ProjectManifest`。玩家包不包含本插件（开发层不进玩家包）。

## 现行输入与报告合同

项目清单的根对象必须包含 `format`、`projectId`、`experiment`、`requiredPlugins`、`sources`、`objectReferences`，不接受额外或重复字段。`format` 固定为 `gtfo-forge-project`；**不接受 `schemaVersion` 或 `expectedObjects`，不做旧格式 fallback**。

`experiment` 必须包含 `packageVersion`、`authoringSha256`、`dependencies`，dependencies 是字符串可以为空。`requiredPlugins` 条目是 guid 与 version（精确版本，按相等判定，I-RELEASE）；`sources` 是 path、sha256、kind，kind 只接受 DataBlock / LGTuner / ModConfig；hash 是小写 SHA-256。

对象声明只支持 zone 与 room。Zone locator 明确 layoutId、dimension、layer、localIndex；room locator 是 unique-geomorph-in-zone，明确 zoneAuthorId、该区域的原生 dimension / layer / localIndex（必须与那条 zone 引用逐值相等）、room 的 id 与 revision 和完整的 `Assets/` 来源身份。房间解析只在那一个区域内进行：区域没有就是 `zone_unresolved`，区域内没有这个房间就是 `no_candidate`，区域内有两个同源房间就是 `multiple_candidates`（候选照常列出），解析器没装或 prefab 取不回来仍是 `creation_context_unverified`。**显示名称、Hierarchy 路径、预览模型都不代替创建证据。**

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
python ForgeDevelopment/scripts/rec-query.py "$env:GTFO_BEPINEX_PATH/ForgeReports/rec-<会话>" --list-channels
python ForgeDevelopment/scripts/rec-query.py <会话> --channel tracer --method OnOpen --json
python ForgeDevelopment/scripts/rec-merge.py host=<会话> client=<会话> --points ForgeDevelopment/probes/points.tsv
```

离线性能分析按远征分段、计算帧加权 FPS、排出慢分钟窗口与长帧，报告扫描开销、被检测的模组路径、内存趋势和缺失的通道。它不会把窗口分位数平均成整场分位数、不会仅凭增长断定内存泄漏、不会仅凭一个异常归咎某个模组。重复生成需要固定游戏版本、插件、配置、资源和种子；报告对照会明确缺失的输入，不根据差异自动推断 bug。

## 复跑

```powershell
python ForgeDevelopment/scripts/verify-diagnostics.py --bepinex "$env:GTFO_BEPINEX_PATH"
```

脚本依次构建宿主、原生插件与各测试工程，运行插件启动、报告、项目规则、检查、快照、关机、场景清单、遥测、采样、原生布局与 Python 套件。默认新建系统临时目录，也可以指定一个尚不存在的 `--output`；源码变化或任何失败都返回非零。完整的套件清单与结果见 [VALIDATION.md](VALIDATION.md)。

最后是负例门槛：把包复制到证据目录，分别注入五个已知破坏（快照改为导出时捕获、旧 session 接受新世界的迟到观察、去掉错误条数上限、放开 trace 聚合上限、首个清理失败后停止后续清理），每个都必须编译通过并由对应套件判失败；锚点缺失或多于一处、变异后仍全绿都算验证失败。工作树与游戏 profile 不被修改。

## 边界

当前 DLL 不应作为玩家发行物。没有 GTFO 加载、多人、原生 Hook 安全或采集开销的验收；"不安装 / 安装但未启用 / 启用"三种实际加载模式也未在游戏里核验。InfiniTweaks 的 QoL 行为不属于本模块。禁止无限重试、重置指定 prefab 种子、静默替换房间、删除任务对象、任意挂接 CourseNode、吞掉异常或调低游戏日志级别来伪装成功。
