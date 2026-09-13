# ForgeDevelopment 实施计划

未完成批次按总案 v2.0 第 13 节的 F 阶段重排。**已交付内容见 [README.md](README.md)，实际执行结果见 [VALIDATION.md](VALIDATION.md)，仓库整体状态见 [ARCHITECTURE.md](../ARCHITECTURE.md)。**

上位依据：[唯一总案](../../Infini-GTFO-Model-Site/Docs/rework/FORGE-COMPLETE-PLAN.md) 第 1、8、13 节。本包对应工作单元 **U-DEV-MOD**，阶段 F5，D2 已把诊断源码从宿主迁出（实现与本地验证等级），剩余停止条件是游戏内三种加载模式的实际核验。发生语义差异先核对总案与实际代码，不在本文件私自改写 canonical 合同。

## 输入、输出和观测边界

| 输入 | 处理 | 输出 / 不确定性 |
| --- | --- | --- |
| Runtime 生命周期、tick 与结果证据 | 只读订阅、来源与 epoch 关联 | plan 与 resource revision、node、command、cause、runId；无公共观察 API 时先提合同需求 |
| 离线 `gtfo-forge-project` 声明 | 严格的格式、路径、hash、依赖与对象检查 | 实际匹配、缺失或不确定；不自授能力，不执行作者代码 |
| 原生生成与实际对象 | 经已核实的创建上下文建立映射 | 验证过的对象引用；**仅名字相同不得匹配** |
| 游戏与插件日志 | 聚合分类、次数、首末时间、代表栈 | 保留原始错误；归因未知时明确未知 |
| 性能与空间采样 | 有预算的游戏线程读取、纯快照后台写盘 | 样本数量、丢弃、耗时、检查覆盖率；不承诺全图完备 |

## D1 — 诊断合同真实接线（已交付）

修复内容见 [README](README.md#d1-修好的具体行为)，执行结果见 [VALIDATION.md](VALIDATION.md)。原来的 10 个宿主编译错误已经消除，完整宿主构建通过。

保留的验收要求：新旧 world、非法项目、部分扫描、未知 geomorph 来源和取消都可区分。

## D2 — 拆出真正可选的 Development 插件（已实现并本地验证，游戏加载待核验）

切换已完成，结构见 [README](README.md#当前状态d2-已切换未做游戏加载验收)，证据见 [VALIDATION.md](VALIDATION.md#d2--独立插件切换)。逐项落点：诊断源与测试已迁移，保留唯一的 Report 与 Scan 实现；宿主 `Plugin` 已移除作者采集、monitor 与诊断清理；设置已拆分，Development 用独立的 `NAinfini.ForgeDevelopment.cfg` 且不迁移旧键；启动门槛是 Runtime `Authoring` 且 `Plugin.Runtime` 非空，本插件不登记 provider；宿主 Hook 选择已删除，双方 csproj 与原生元数据测试已更新。**唯一未完成的是核验不安装、安装但关闭、启用三种实际加载模式**，需要安装到独立测试 profile，须当次明确授权。下面保留原切换要求作为验收依据。

公共接入已经存在：`ForgeRuntime.Plugin.Runtime` 与模块句柄的 `ObserveLifecycle`。Off 或停止时入口为 null；未 Advance 的 tick = -1 与 authority = null 不作为已观测的世界数据；注册发生在依赖插件的 Load 中，早于首个 FixedUpdate 的 `StartRuntime` 冻结点。详见 [公开生命周期合同](../ForgeRuntime/Framework/HOST-LIFECYCLE.md)。

**原切换要求（一次共同完成，不启动第二套采集器）：**

迁移诊断源及其 namespace 与测试链接，保留唯一的 Report 与 Scan 实现和当前的未提交内容。Runtime 的 `Plugin` 移除作者采集初始化、两个作者 monitor、诊断失败清理和可选的 Telemetry 装配；FrameworkMonitor、世界时钟、游戏阶段和权限仍留在宿主。`AuthoringSettings` 拆分——Mode、PlanPath、AllowedPermissions 归 Runtime（这部分 R2b-1 已完成），报告、扫描、性能选项归 Development；新 GUID、配置名、producer 身份与一次性迁移由 Runtime 同批核定，**旧的 Off 不得自动变成启用**。独立的 Development 入口只有在显式启用且真实 Runtime 可用时才订阅、采集和登记自己的 provider；失败与退出逐项清理，**正常玩法不得依赖 Development**；只有 Runtime 分发 SDK。修改宿主的 Hook 选择、双方的 csproj 与原生签名测试，然后核验不安装、安装但关闭、启用三种实际加载模式。

保留原生日志 callback 的托管根引用；启动失败和退出时分别撤销日志、Harmony、Unity 回调、Telemetry 与后台 writer，**一个异常不能阻止其余清理**。

退出验收：普通玩家不启动诊断线程、采样、热键或 Hook；退出后无重复订阅；反复进出图无对象或回调泄漏。

## D3 — 作者对象回指和生成证据

开始条件：Map 的 MAP1/MAP2 提供已核验的对象创建身份和实际拓扑，Runtime R3 提供公共上下文。

对齐导出的 project / resource / revision / instance、native layout 与 zone 与 geomorph 和运行对象的映射，**不用预览 GLB 节点或 prefab 名称代替实际 placement**。分别记录 generation job 的开始、持续、完成、失败与随机 seed 和 state 与对象阶段；`Build` 返回 false 可能只是分帧继续，不能自动算作重试。对每项声明保留观察来源、已扫范围、epoch 和 evidence level；同名对象、重复 placement、多维度同 localIndex 必须能区分。把导航、碰撞、plug 检查关联到可核实的对象，同时保留原始异常与无法归因的事实；**报告不能推断本模组已修复游戏原生异常**。

退出验收：同名不同实例、多维度、切关迟到回调、缺失创建证据、局部 NavMesh 可达但整图断路，结果各不混淆。

## D4 — 日志、执行轨迹和错误聚合

开始条件：D2 的生命周期与 Runtime R3/R5 的证据接口可用。

统一 runId、root/cause、plan 与 resource 与 node、command、committed fact 的关联；Replay 只作为可关联的证据，不在这里实现 Recorder 或 Viewer。重复异常按来源、类型、代表栈聚合，保留计数、首末时间、丢弃量与原始日志位置；**不能用事件溢出淹没真正的错误**。把未实现 binding、权限不足、对象陈旧、预算不足、部分提交和 unknown 提交区分记录；报告不把拒绝和游戏崩溃归为同一种"失败"。后台只写冻结的托管数据，不在线程池访问 Unity 或 IL2CPP 对象；写盘失败、队列满、退出超时明确记录可用范围。

退出验收：大量重复异常仍有代表证据且内存有界；写盘异常或退出异常不影响游戏对象清理，也不伪造成功报告。

## D5 — 空间和性能诊断

开始条件：Map 的 MAP2/MAP4 有真实的导航与生成接口，D3 能关联证据。

拆分采样、判定和呈现的开销；建立固定 seed、路线、时段的对照，记录采样自身的消耗，避免每帧全场扫描或反射式类型发现。验证 plug 配对、局部与跨区路径、敌人空间要求、生成可用点和数量不足；**区别静态空间合法、实际生成成功和任务可通关**。使用有限的样本窗口、缓存已发现的类型与有界记录；超过预算时报告未检查或丢弃，不默认为检查通过。性能优化先用实际的 profiler 与日志确认 CPU、GPU、分配瓶颈；桌面合成测试不能替代 GTFO 帧时间和多人压力场景。

退出验收：同 seed 的对照可解释；采样开关的开销已测，超预算时保持可诊断；不因部分路径成功就报告整图成功。

## D6 — 离线工具和联网诊断

开始条件：D4 的报告格式稳定，Runtime F3N 的实际同步证据可消费。

迁移并验证 `import_log` / `compare_runs` / `performance_report` 等已有工具，对输入格式、文件大小、路径与缺字段严格处理，**不另造近似的报告标准**。记录主客机版本、参与者、消息与提交序号和状态差异，只用已授权采集的数据；不能把外部的 packetIndex 之类未知异常直接归因给 Forge。给网站提供精确 schema、诊断代码、资源回指和可上传报告的边界；离线工具不直接写网站的生产数据。

**F3N 的社区修复清单需要诊断侧配合**：每条修复的复现条件与验证方式要能被工具复跑，但清单本身由 Runtime 维护，不进入本包的机制目录。

退出验收：旧的错误输入明确拒绝，合法报告可读；时间偏差、丢包、日志缺失被标注，不伪造一致或可恢复的结论。

## D7 — 版本化调试与交付收口

开始条件：Runtime 提供安全生命周期和版本边界；其余诊断批次完成。

热重载只针对明确允许的离线数据修订：在安全点验证全部合同，原子替换整版并释放旧 scope；失败时保持原有效版本并报告失败，**禁止半应用**。不把热调试做成任意 C# 或 DLL 的执行入口，不在游戏进行中自动下载作者的新定义；原生程序集变化按重启或显式安装处理。普通玩家的导出默认排除 Development；开发包显式包含版本与诊断配置，记录采样成本、局限与存储位置。在两次以上的进图、切图、恢复、退出场景里检查残留回调、写入队列和来源 lease。

退出验收：有效与无效修订、重复提交、忙碌安全点、世界变化中重载、关闭后玩法继续、多人报告对齐；未实现的调试模式保持拒绝。

## 必须保留的验收用例

| 场景 | 期望证据 |
| --- | --- |
| 诊断关闭或不安装 | 正常玩法不依赖报告，诊断 Hook 与采集不启动 |
| 原生日志接入后发生 GC | 回调仍有效；退出后不再调用已释放对象 |
| 一项 Shutdown 清理抛异常 | 其余订阅、Hook、writer 仍清理且错误有记录 |
| world A 扫描期间进入 world B | A 的迟到观察不污染 B，不报告虚假完成 |
| 相同 prefab、多份 placement | 依真实创建身份回指，不按名称第一项匹配 |
| 重复错误超过容量 | 聚合计数与丢弃量可见，原始类别不被无关警告掩盖 |
| 网络日志不完整 | 明确证据不足，不判定同步正确也不归因第三方 |
| 部分 NavMesh 或空间采样 | 只报告已检查区域，不声明整图可达或可通关 |

## Agent 范围

常规拥有 `ForgeDevelopment/**`，D2 后包括全部诊断源码；`ForgeRuntime/Plugin.cs` 等共享文件的改动仍按 [AGENT-HANDOFF](../AGENT-HANDOFF.md) 先交接。

功能阶段用 `scripts/verify-diagnostics.py` 从本目录的实际 csproj 与脚本路径运行全部套件，并完整构建 Runtime + Development 原生插件。最后在真实的 GTFO profile 分别测采集关闭、开启、切关、异常与退出；安装需要当时任务的明确授权。

## 总案机制工作队列（4 类）

只分配模组侧牵头责任；语义、参数和来源继续读取网站 `catalog/mechanism-blueprints.json` 及总案第 33 节。每行当前均为待实施，必须逐行完成 M1–M7。

| 机制 ID | 机制名称 | 需要落实的普通 Action / Effect |
| --- | --- | --- |
| `replay-observability` | 运行轨迹、报告与回放关联 | `forge.action.diagnostic.trace`、`forge.action.diagnostic.metric` |
| `spatial-debugging` | 碰撞、导航和生成概率检查 | `forge.action.diagnostic.spawn_preview`、`forge.action.diagnostic.inspect` |
| `network-diagnostics` | 联网延迟与状态差异诊断 | `forge.action.diagnostic.metric` |
| `revisioned-live-edit` | 配置热重载与版本化调试 | `forge.action.diagnostic.inspect` |

逐机制证据单至少包含：源包精确版本与 hash 与许可、已核验与未知的语义、canonical ID 与修订、真实 binding 与权限、宿主与客户端执行侧、正反 fixture、游戏日志与 runId、实际依赖及 M7 清理结论。
