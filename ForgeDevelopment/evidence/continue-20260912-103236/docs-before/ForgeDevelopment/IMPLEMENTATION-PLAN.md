# ForgeDevelopment 详细实施计划

2026-09-12；状态：**基本工程和架构骨架已建立，D1 诊断接线与专项回归已实施，完整宿主集成门槛仍 OPEN；D2–D7 尚未完成**。本轮没有安装、发布或游戏验收。

Development 是可选作者诊断包：观察生成、空间、性能、异常与运行证据，关联作者对象并输出报告。它不调度正常玩法，不将“没有报错”解释为地图正确。

本模组在产品中的作用：作者在网页完成配置后，需要知道游戏生成或运行哪里失败，以及对应哪个工程对象。Development 把可核实的游戏证据带回报告，帮助定位与复现，同时让普通玩家无需承担作者采集成本。 完整产品目标、典型流程、术语与资料索引见 [Agent 交接入口](../AGENT-HANDOFF.md#我们在做什么为什么要做)。

上位依据：[唯一总案](../../Infini-GTFO-Model-Site/Docs/rework/FORGE-COMPLETE-PLAN.md) 第 15、26、29、31.27、32.9、33.9 节、[包边界](../../Infini-GTFO-Model-Site/Docs/rework/FORGE-PACKAGE-ARCHITECTURE.md)、[模组架构](../ARCHITECTURE.md)。这些实施任务是总案在本模组的分解；发生语义差异先核对总案与实际代码，不能在本文件私自改写 canonical 合同。

每个批次的“验收”是未来退出条件，除明确标为本轮已执行的骨架检查外，都不是测试通过记录。所有步骤遵循架构文档中的 M0–M7 和跨仓库交接表。

## 当前骨架和源码迁移表

当前 `ForgeDevelopment.csproj` 仅引用公共 SDK，`ModuleDefinition.Create()` 只登记空 provider；没有 BepInEx 插件、采样器或报告实例被自动启动。下面这些实现仍位于 ForgeRuntime；D1 已修改诊断接线，但尚未开始 D2 文件迁移。

| 当前源码 / 测试 | 后续归属 | 必须一起处理的连接 |
| --- | --- | --- |
| `RuntimeDiagnostics.cs`、`DiagnosticsReport.cs`、`DiagnosticLogClassifier.cs`、`AsyncReportWriter.cs` | Development 的采集与报告 | 日志订阅/解除、原生委托根引用、runId、写入队列关闭 |
| `GenerationHooks.cs`、`WorldInspection.cs`、`ProjectChecks.cs`、`ProjectObjectReferences.cs` | Development 的生成与对象检查 | 实际生成阶段、世界 epoch、扫描创建/完成/取消和对象来源 |
| `PerformanceDiagnostics.cs`、`PerformanceSampleWindow.cs`、`SceneInventory.cs`、`CombatSampling.cs` | Development 的可选采样 | 配置、UI 热键、样本预算、主线程访问、采样开销 |
| `TelemetryBridge.cs`、`ShutdownSequence.cs` | Development 的可选互操作和清理 | InfiniTweaks 只读可选互操作；每个清理步骤独立执行 |
| `AuthoringSettings.cs`、`Plugin.cs`、hook 选择 | 拆分时由 R2/D2 协同分配 | 不把公共宿主配置和模拟时钟复制进诊断包 |
| Reports / ProjectChecks / Samples / SceneInventory / Telemetry 测试与 Python 脚本测试 | 跟随对应诊断源进入 Development | 相对编译链接、fixture、脚本路径与 README 一并更新 |
| `scripts/`、AUTHORING-SCOPE / PERFORMANCE-REVIEW / VALIDATION 的诊断部分 | Development | 公共 SDK 和原生领域验证说明仍归 Runtime/领域 |

迁移应保留每份文件现有未提交内容，按清单移动并校验；不能用旧 Git 版本覆盖工作树。Telemetry 的测试源码引用需按当前 `InfiniTweaks/Telemetry.cs` 位置重新核对；不得为了消除测试依赖去修改 QoL 行为。

## 输入、输出和观测边界

| 输入 | 处理 | 输出 / 不确定性 |
| --- | --- | --- |
| Runtime 生命周期、tick 和结果证据 | 只读订阅、来源与 epoch 关联 | 计划/resource revision/node/command/cause/runId；无公共观察 API 时先提 R3 合同需求 |
| 离线 `gtfo-forge-project` 声明 | 严格格式、路径、hash、依赖和对象检查 | 实际匹配/缺失/不确定，不自授能力或执行作者代码 |
| 原生生成与实际对象 | 经已核实创建上下文建立映射 | 验证对象引用；仅名字相同不得匹配 |
| 游戏/插件日志 | 聚合分类、次数、首末时间和代表栈 | 保留原始错误，归因未知时明确未知 |
| 性能与空间采样 | 有预算的游戏线程读取、纯快照后台写盘 | 样本数量/丢弃/耗时/检查覆盖率；不承诺全图完备 |

目前 `ProjectChecks.Load(report, worldEpoch, simulationTick)` 与 `ProjectObjectReferenceScan` 已变化；D1 已接入真实 scan 所有权及 epoch；原 10 个退役接口调用已替换。完整构建仍被并行修改的 Runtime/SDK 阻塞，不能将本批纯测试标为完整宿主验收。现行报告格式以源码的 `gtfo-forge-diagnostics-report` 为准，旧 README 的 schemaVersion 示例必须在 D1 同步核对，不新增旧格式 fallback。

## 实施批次

### D1 — 修复诊断合同真实接线

2026-09-12：代码与诊断专项回归已交付，D1/R1 集成门槛 OPEN。证据、现行 schema 和 R2 交接见 [D1-VALIDATION.md](D1-VALIDATION.md)。

开始条件：本批诊断调用方由 D1 单独修改，R1 负责人复核；先读现行源码和相关 fixture。

1. 逐个替换 RuntimeDiagnostics 与 WorldInspection 的失配调用，明确由谁持有 scan、如何取得世界 epoch、何时 Start/ObserveZone/ObserveGeomorph/MarkPartial/Complete/Cancel。不能把缺失方法补成空实现。
2. 确保报告附着同一次扫描的不可变快照，加载、生成、切关和取消不会把新旧世界结果混合；没有完整扫描证据时不输出 completed/full。
3. 区分 zone 的 dimension/layer/local index 与作者 layout、room placement、geomorph source。缺少已核验创建上下文时记录 unverified/partial，不猜哪个对象出错。
4. 同步诊断文档中的真实格式字段和路径校验边界；保留当前严格项目输入及扫描预算，不恢复退役字段。

交付物：可构建的诊断调用链、准确报告快照与针对当前错误的回归。

退出验收：新旧 world、非法项目、部分扫描、未知 geomorph 来源和取消均可区分；完整宿主编译无这 10 个错误。

### D2 — 拆出真正可选的 Development 插件

开始条件：D1 通过，R2 已确定宿主生命周期入口与配置迁移。

1. 按迁移表把诊断源/测试/脚本/说明移入本目录，精确更新相对引用；配置与调用方一次迁移，删除 Runtime 内被替代副本。
2. 新增独立 BepInEx 入口，仅在开发配置允许时订阅日志/启用诊断 Hook；公共 Runtime 注册仍走同一 SDK。新增采集身份、报告 producer 与实际版本在双方合同中明确。
3. 保留原生日志 callback 的托管根引用；启动失败和退出时分别撤销日志、Harmony、Unity 回调、Telemetry 与后台 writer，不能一个异常阻止其余清理。
4. 验证不装 Development、安装但关闭、启用三个模式；不得以 Development 关闭为理由关掉必要玩法，也不得把旧 Runtime Off 配置绕过。

交付物：独立 Development 插件、成套测试/脚本与实际依赖声明。

退出验收：普通玩家不启动诊断线程/采样/热键/Hook；退出后无重复订阅，反复进出图无对象或回调泄漏。

### D3 — 作者对象回指和生成证据

开始条件：MAP1/MAP2 提供已核验对象创建身份和实际拓扑；R3 提供公共上下文。

1. 对齐导出 project/resource/revision/instance、native layout/zone/geomorph 与运行对象的映射，不用预览 GLB 节点或 prefab 名称代替实际 placement。
2. 分别记录 generation job 开始/持续/完成/失败、随机 seed/state 与对象阶段；Build 返回 false 可能只是分帧继续，不能自动算重试。
3. 对每项声明保留观察来源、已扫范围、epoch 和 evidence level；同名对象、重复 placement、多维度同 localIndex 必须能区分。
4. 把导航/碰撞/plug 检查关联到可核实对象，同时保留原始异常与无法归因的事实；报告不能推断本模组已修复游戏原生异常。

交付物：真实创建上下文驱动的对象映射与可追溯报告。

退出验收：同名不同实例、多维度、切关迟到回调、缺失创建证据、局部 NavMesh 可达但整图断路的结果不混淆。

### D4 — 日志、执行轨迹和错误聚合

开始条件：D2 的生命周期与 R3/R5 的证据接口可用。

1. 统一 runId、root/cause、plan/resource/node、command 和 committed fact 关联；Replay 只作为可关联证据，不在这里实现 Recorder/Viewer。
2. 重复异常按来源/类型/代表栈聚合，保留计数、首次/末次时间、丢弃量与原始日志位置；不能用事件溢出来淹没真正错误。
3. 把未实现 binding、权限不足、对象陈旧、预算不足、部分提交和 unknown 提交区分记录；报告不把拒绝和游戏崩溃归为一种“失败”。
4. 后台只写冻结的托管数据，不在线程池访问 Unity/IL2CPP 对象；写盘失败、队列满、退出超时明确记录可用范围。

交付物：稳定的结构化报告和离线分析输入。

退出验收：大量重复异常仍有代表证据且内存有界；写盘异常/退出异常不影响游戏对象清理或伪造成功报告。

### D5 — 空间和性能诊断

开始条件：MAP2/MAP4 有真实导航/生成接口，D3 能关联证据。

1. 拆分采样、判定和呈现开销；建立固定 seed/路线/时段对照，记录采样自身消耗，避免每帧全场扫描或反射类型发现。
2. 验证 plug 配对、局部/跨区路径、敌人空间要求、生成可用点和数量不足；区别静态空间合法、实际生成成功和任务可通关。
3. 使用有限样本窗口、缓存已发现类型与有界记录；超过预算报告未检查/丢弃，不默认为检查通过。
4. 性能优化先用实际 profiler/日志确认 CPU/GPU/分配瓶颈；桌面合成测试不能替代 GTFO 帧时间和多人压力场景。

交付物：可复现诊断场景、性能/导航报告与限制说明。

退出验收：同 seed 对照可解释；采样开关开销已测，超预算保持可诊断；不因部分路径成功报告整图成功。

### D6 — 离线工具和联网诊断

开始条件：D4 报告格式稳定，R7.a 的实际同步证据可消费。

1. 迁移并验证 import_log/compare_runs/performance_report 等已有工具，对输入格式/文件大小/路径与缺字段严格处理；不另造近似报告标准。
2. 记录主客机版本、参与者、消息/提交序号与状态差异，只用已授权采集的数据；不能把外部 packetIndex 等未知异常直接归因给 Forge。
3. 给网站提供 exact schema、诊断代码、资源回指和可上传报告的边界；离线工具不直接写网站生产数据。

交付物：已有 Python 工具适配、主客机差异报告与网站导入样本。

退出验收：旧错误输入明确拒绝，合法报告可读；时间偏差/丢包/日志缺失被标注，不伪造一致或可恢复结论。

### D7 — 版本化调试与交付收口

开始条件：R7.a/R8.a 提供安全生命周期和版本边界；其余诊断批次完成。

1. 热重载只针对明确允许的离线数据修订：在安全点验证全部合同，原子替换整版并释放旧 scope；失败保持原有效版本且报告失败，禁止半应用。
2. 不把热调试做成任意 C# 或 DLL 执行入口，不在游戏进行中自动下载作者新定义；原生程序集变化按重启/显式安装处理。
3. 普通玩家导出默认排除 Development；开发包显式包含版本与诊断配置，记录采样成本、局限与存储位置。
4. 在两次以上进图/切图/恢复/退出场景检查残留回调、写入队列和来源 lease；跨仓库更新报告生产方版本与运行支持状态。

交付物：明确边界的开发调试路径、可选发行包和联合验收记录。

退出验收：有效/无效修订、重复提交、忙碌安全点、世界变化中重载、关闭后玩法继续、多人报告对齐；未实现的调试模式保持拒绝。

## 必须新增或保留的验收用例

| 场景 | 期望证据 |
| --- | --- |
| 诊断关闭 / 不安装 | 正常玩法不依赖报告，诊断 Hook/采集不启动 |
| 原生日志接入后发生 GC | 回调仍有效；退出后不再调用已释放对象 |
| 一项 Shutdown 清理抛异常 | 其余订阅/Hook/writer 仍清理且错误有记录 |
| world A 扫描期间进入 world B | A 的迟到观察不能污染 B，不报告虚假完成 |
| 相同 prefab、多份 placement | 依真实创建身份回指，不按名称第一项匹配 |
| 重复错误超过容量 | 聚合计数与丢弃量可见，原始类别不被无关警告掩盖 |
| 网络日志不完整 | 明确证据不足，不判定同步正确或归因第三方 |
| 部分 NavMesh/空间采样 | 只报告已检查区域，不声明整图可达/可通关 |

## Agent 修改范围与验证

常规实施拥有 `ForgeDevelopment/**`。D1/D2 需要迁移表中的 `ForgeRuntime` 诊断文件，由 [AGENT-HANDOFF](../AGENT-HANDOFF.md) 的 Runtime 接口负责人安排一次文件所有权切换；不要同时让两个 Agent 修改 `Plugin.cs` 或 `WorldInspection.cs`。

本批已执行诊断专项回归；完整宿主及游戏验收未通过，见 D1-VALIDATION。功能阶段从本目录迁入后的实际 csproj/脚本路径运行 Reports、ProjectChecks、Samples、SceneInventory、Telemetry 和 Python 报告测试，并完整构建 Runtime+Development；迁移前参考 [旧验证说明](../ForgeRuntime/VALIDATION.md)。测试路径必须随迁移更新，不能把本计划中的未来路径当成已存在命令。最后在真实 GTFO profile 分别测采集关闭/开启、切关、异常与退出；安装需当时任务明确授权。

## 总案机制工作队列（4 类）

以下只分配模组侧牵头责任；语义、参数和来源继续读取网站 `catalog/mechanism-blueprints.json` 及总案第 33 节。牵头不代表独占公共语义，也不决定导出包必须依赖本模组。每行当前均为待实施，必须逐行完成 M1–M7；既有局部代码不能将整类标为通过。

| 机制 ID | 机制名称 | 需要落实的普通 Action / Effect（源目录引用） |
| --- | --- | --- |
| `replay-observability` | 运行轨迹、报告与回放关联 | `forge.action.diagnostic.trace`、`forge.action.diagnostic.metric` |
| `spatial-debugging` | 碰撞、导航和生成概率检查 | `forge.action.diagnostic.spawn_preview`、`forge.action.diagnostic.inspect` |
| `network-diagnostics` | 联网延迟与状态差异诊断 | `forge.action.diagnostic.metric` |
| `revisioned-live-edit` | 配置热重载与版本化调试 | `forge.action.diagnostic.inspect` |

逐机制证据单至少包含：源包精确版本/hash/许可、已核验与未知的语义、canonical ID 与修订、真实 binding/权限、宿主与客户端执行侧、正反 fixture、游戏日志/runId、实际依赖及 M7 清理结论。此表不复制来源代码、不创建第二份能力注册表。
