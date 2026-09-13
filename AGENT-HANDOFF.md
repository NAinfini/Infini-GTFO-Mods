# 后续 Agent 进场交接

本文件是模组仓库的进场流程与委派合同。**状态数字不在这里**：构建结果、测试计数、架构断言数、批次位置一律见 [ARCHITECTURE.md](ARCHITECTURE.md)。

## 1. 我们在做什么，为什么要做

Forge 是给**普通 GTFO 玩家**的创作工具。目标用户不会做地图，没装过模组，不知道 DataBlock 是什么，也不该被要求知道。

他打开网站，选一个房间，摆几扇门，放几只敌人，给枪加一个"打中就标记"的效果，或者放一个只治疗队友的炮台，保存，分享，进游戏能玩。整个过程里他不需要判断该装哪个模组、不需要解析依赖闭包、不需要读诊断报告。这条定位来自总案第 1 节，是判断任何功能"做没做完"的标准：只有懂模组的人才用得起来的能力不算完成；需要玩家自己去别处找一个 JSON 导入才能解锁的东西不算完成。

制作者向的能力（公开 SDK、模块创作、资源提交、诊断采集）继续保留，但退到二级入口，不是新玩家的第一屏。

整个 Forge 只有一套执行语义：**Trigger / Event / Target**。Event 是已发生的事实或类型化的请求与结果，Trigger 订阅事实并启动逻辑，Selector 选出显式目标集合，Condition 是纯判定，Modifier 是纯变换，Control 管流程与时序与预算，Action 产生副作用，Result 是动作的返回端口。武器、工具、消耗品与敌人全部接入这一套——治疗炮台、命中标记、ping 是同一批原子的不同拼法，不是各写一个系统。工坊里的"标准武器"本身就是一张图，内置预设与玩家自制走同一条编译、校验、执行路径。

**目标选择与效果极性彻底独立。** 伤害不隐含"打敌人"，治疗不隐含"治队友"。接收者永远是显式的图输入，玩家可以做出治疗敌人、伤害队友、混合范围效果——这不是漏洞，是自由度。

网站负责作者侧，本仓库负责游戏侧。六个包的职责：Runtime 提供公共 SDK、唯一调度、状态、结果、预算，**并自己扛一层网络**；Trigger 提供跨域逻辑节点；Map 管空间、任务、玩家流程**并持有地图生成的底层逻辑**；Weapon 覆盖武器/工具/消耗品；Enemy 管敌人；Development 是可选诊断，普通玩家不装也能正常游玩。

## 2. 玩家最终要能完成的事

旧版本这一节写的是六步作者流程，其中"自行校验依赖闭包""自行判断该装哪些模组""自行启用诊断回传"对目标用户根本不可执行。按新定位，玩家侧的完整链路是：

在网站上选房间、摆门、放敌人、配武器工具消耗品，用同一套原子拼出自己想要的效果；保存时看得见东西存在哪、谁能看到；分享或收进社区；**系统自动算出这份作品实际需要哪些模组和哪些版本，玩家按提示装上就能玩**——依赖闭包、绑定选择、版本锁定都是系统的责任，不是玩家的作业。进游戏后按同一套调度执行，真正提交的结果被记录下来。

诊断是可选的第二层：作者型用户可以显式启用 Development 把 runId、对象与节点回指、原生错误、预算与生成证据带回网站；**普通玩家不装 Development 也能正常游玩**，这是硬约束不是优化项。

模组侧因此有三个直接义务。第一，任何能力都必须能从系统自动计算的依赖闭包里被正确表达，不能要求玩家手工配置。第二，任何失败都要给出玩家看得懂的明确结果，不能静默降级或改投目标。第三，未实现的能力必须诚实地报为缺口，不生成看似可玩的伪实现。

三个典型验收场景说明这项工作的用途：钥匙或发电机条件满足才开指定门；扫描完成后按合法空间和预算触发一次波次；消耗电量的区域信标每秒按显式关系策略对指定接收者作用。最后一例的治疗、伤害、感染或其他效果都是普通 Action，不能再为每种用途写一个计时器。

## 3. 接手前需要懂的术语

这些是**内部术语**。它们在代码和本仓库文档里用，但按总案第 10 节，Binding、Domain、Authority、capability snapshot、Graph 这些词不得出现在玩家必经路径的界面上。

| 名词 | 在这里的含义 |
| --- | --- |
| Resource / definition / revision | 可保存复用的资源定义及精确版本；不等于游戏里的一次实例 |
| Part / Slot / instance | 可组合部件、安装位置和真实实例；位置相同不代表同一实体生命 |
| Capability / canonical ID | 一项语义的统一定义及唯一 ID，由网站 Registry 维护 |
| Binding / provider | 某模块实现或观察该语义的明确连接及所有者；planned 词汇不是 executable binding |
| Trigger / Selector / Condition / Modifier | 发生的事实、选择显式对象、纯条件、纯数值与向量与权重变换 |
| Control / Action / Result | 编排时间与分支、请求副作用、记录实际提交/拒绝/部分/未知结果 |
| worldEpoch / lifeEpoch | 世界和实体生命代次，用于拒绝迟到动作，不把旧引用投给同 ID 新生命 |
| source / owner / instigator / recipient | 效果来源、拥有者、施加者、接收者；关系锚点明确，不能互相猜测替代 |
| lease / scope / cost transaction | 本来源的临时贡献、生命周期范围、预留与提交与释放费用 |
| implementation-only / game-verified | 代码级实现与指定游戏场景已验证是两个证据级别，不能混用 |

证据分五级，任何交付说明必须自报所处级别，不得跨级宣称：**计划 → 实现 → 本地验证 → 浏览器验证 → 游戏验证**。构建通过不是行为通过，源码合并不是部署，部署不是资产批准。

## 4. 资料位置

| 资料 | 用途 |
| --- | --- |
| [唯一总案 v2.0](../Infini-GTFO-Model-Site/Docs/rework/FORGE-COMPLETE-PLAN.md) | 第 0–14 节是现行产品与架构裁决；第 31–33 节是生成视图 |
| [ARCHITECTURE.md](ARCHITECTURE.md) | 本仓库真实结构、构建与测试状态、批次位置、按 F 阶段重排的工作单元 |
| [包架构](../Infini-GTFO-Model-Site/Docs/rework/FORGE-PACKAGE-ARCHITECTURE.md) / [资源合同](../Infini-GTFO-Model-Site/Docs/rework/UNIFIED-RESOURCE-CONTRACT.md) | 六包责任、离线内容、资源与运行身份、真实依赖 |
| [现行实现状态](../Infini-GTFO-Model-Site/Docs/rework/FORGE-IMPLEMENTATION-STATUS.md) | 区分设计、已落地和未验证；仍需对照当前工作树 |
| [能力词汇](../Infini-GTFO-Model-Site/catalog/capability-catalog.json) / [机制蓝图](../Infini-GTFO-Model-Site/catalog/mechanism-blueprints.json) | 规划词汇、来源和组合；不是运行注册清单 |
| [C# 公共 SDK](ForgeRuntime/Framework/README.md) / [公开生命周期合同](ForgeRuntime/Framework/HOST-LIFECYCLE.md) | 当前真实类型、支持上限、生命周期时序与限制 |
| [共享 Runtime fixtures](../Infini-GTFO-Model-Site/Tests/Forge/fixtures/runtime) | 网站与 C# 使用的跨语言正反例；只覆盖实际存在的场景 |

四份历史 API 文档的路径已经缺失，不要按旧引用去找：`Docs/runtime/forge-runtime-api-contract.md`、`Docs/runtime/forge-native-binding-delivery.md`、`Docs/runtime/forge-map-gameplay-api-audit.md`、`Docs/runtime/infini-forge-runtime.md`。它们的处置属于总案第 34.3 节，待用户决定；在此之前，需要这些内容时读取实际源码与各模块 VALIDATION.md，不要凭旧文件名推断结论。

## 5. 先读哪些文件

先看用户当前任务范围与适用的 AGENTS.md，再读 [ARCHITECTURE.md](ARCHITECTURE.md)，再读总案第 0–14 节，再读要改的那个模块的 `README.md` / `IMPLEMENTATION-PLAN.md` / `VALIDATION.md`，最后读将要修改的真实源码与测试。

本地模组 Git 根是 `C:/Users/nainf/Github/Infini-GTFO-Mods`；另一个 `GTFO-Mods` 目录不是目标 Git 根。网站在 `C:/Users/nainf/Github/Infini-GTFO-Model-Site`，由另一个任务维护。两仓唯一实施分支都是 `main`，不新建工作分支。已有历史与未提交工作不得重置、覆盖或清理；HEAD 不是工作树快照。历史"停止"记录不能覆盖用户之后的新授权，但也不能据计划自行安装或发布。

## 6. 可委派的工作单元

按总案第 13.2 节，模组仓库承担以下单元。**每个单元独占其持有文件，跨单元的改动走接口，同一文件只能有一个持有者。**

| 单元 | 阶段 | 独占文件 | 前置 | 停止条件 |
| --- | --- | --- | --- | --- |
| **U-RUNTIME** | F3 | `ForgeRuntime/Framework/**`、宿主与公共 tests、根解决方案 | 网站 U-CONTRACT 合同定稿、U-IR 计划格式 | 全宿主构建 + 性能基线实测 |
| **U-NET** | F3N | `ForgeRuntime/` 网络与同步层、独立维护的社区修复清单 | U-RUNTIME | 四类多人场景通过 + 网络实测对比 + 修复清单逐条有来源与验证 |
| **U-TRIGGER** | F3/F7 | `ForgeTrigger/**` | U-RUNTIME 的公共 API | T1 门槛通过 |
| **U-ENEMY** | F5 | `ForgeEnemy/**` | U-RUNTIME | 已在推进中，继续 |
| **U-MAP-MOD** | F5/F9 | `ForgeMap/**` | U-RUNTIME | 生成逻辑归位 + 模型 Adapter 调用链 |
| **U-WEAPON-MOD** | F5 | `ForgeWeapon/**` | U-RUNTIME | 三模式接入同一套 |
| **U-DEV-MOD** | F5 | `ForgeDevelopment/**` | U-RUNTIME | D2 已切换；剩游戏内三种加载模式核验，之后 D3 |
| **U-DOCS-MOD** | 全程 | 本仓库全部 markdown | 无 | 过时陈述清零、矛盾消除、失效引用修复 |

`ForgeRuntime/Plugin.cs`、`GameBindings/GameRuntimeBridge.cs`、`GameBindings/NativeHooks.cs`、公共 csproj 与根文档是共享文件，改动前必须先交接。InfiniTweaks、旧发行 ZIP、用户模型与地图内容不属于这些单元。两个构建同时写同一个 SDK 输出目录会冲突，集成构建由负责人串行执行或使用明确隔离的输出目录。

拆分旧文件时先列 source→target、调用者、测试引用、命名空间与程序集和依赖影响；原字节可保留的移动做 hash 检查。迁移完成后删除被替代的旧执行路径，不保留"临时兼容"的双注册或双 Hook。

## 7. 六份可直接委派的首个批次

| 接手单元 | 首个可执行批次 | 首批必须交付 | 本批停止点 |
| --- | --- | --- | --- |
| U-RUNTIME | R3 收口 → R4 图执行 | 实体/事件/目标合同的跨语言正反例；一条实际图执行链与控制内核 | 合同未定稿前不重做 IR；未测功能保持未验证 |
| U-NET | N0 来源盘点 | 修复类与网络类模组的完整盘点表：哪些修复值得收、哪些已被官方补丁覆盖、哪些与架构冲突 | 盘点结果单独成表，不混进 125 组机制；不复制任何许可不明的代码 |
| U-TRIGGER | T1 门槛收口 | 默认完整入口退出 0；共享 heal 的作者元数据分歧与 owner 归属定论 | 公共 Control API 未交付不复制 scheduler；未实现词汇仍 planned |
| U-ENEMY | E2 空间要求 | 实际 collider、尺寸、移动/导航 profile 与出生空间要求的证据 | 不把 Position 快照或模型包围盒当作碰撞证据 |
| U-MAP-MOD | MAP2 重新定范围 | 按总案 4.2 把地图生成的底层逻辑纳入本包范围，给出资源侧 Adapter 的接口形状 | 未证实的生成/导航不得实现为假成功或自动 fallback |
| U-WEAPON-MOD | W1 原生接线 | 原生装备观察接入、方法体阶段核验、首批真实 binding | 事务未交付先完成证据与纯合同，不在本域做第二资源账本 |
| U-DEV-MOD | D2 游戏内核验 → D3 | 在独立测试 profile 核验不安装、安装但 Runtime 非 Authoring、Authoring 启用三种加载，确认前两种无诊断 Hook、线程与热键；随后按 D3 接作者对象回指 | 安装需当次明确授权；不恢复宿主内诊断，不另建第二套采集器 |

每次委派指定本表中的一个有边界批次，连同以下模板：

> 负责 `<模组>/<批次ID>`，按对应 IMPLEMENTATION-PLAN 实施。先检查当前工作树和真实源码；只写已分配路径，保留并发改动。输入依赖为 `<已落地 API/fixture/版本>`；验收为该批次的退出条件与相应命令。遇到共享接口缺失、未核验原生 API 或合同冲突，先提交可复现证据和最小接口需求，继续本批独立工作，不创建私有 fallback。交付实际差异、命令与退出码、未测项和下一批所需输入；本批不安装不发布，除非用户另行明确要求。

不需要把每个动作上报求确认；在分配范围内完成实现和足够验证。只有授权范围外的不可逆操作，或真实合同歧义阻止安全推进时，才提出聚焦问题。

## 8. 公共接口先行交付清单

Runtime 负责人必须先交出可消费的接口，消费者不等待最终收口。

| 接口批次 | 必须给出的材料 | 消费方 |
| --- | --- | --- |
| R3 对象/事件/目标 | 引用、actor、receiver、关系、集合完成性的实际类型；单位与 domain；事件阶段与 root/cause；合法与非法 fixture | Trigger、全部 resolver 与诊断 |
| R4 图/控制 | 实际 Graph IR lowering 版本；typed data 与执行端口；分支、取消、预算语义；支持清单 | Trigger 与跨域作者图 |
| R5 事务/结果 | resource owner、reserve/commit/release/expiry、幂等键、partial 与 unknown 收据及竞争 fixture | Weapon 库存与部署、Map 奖励、Enemy receiver |
| R6 状态/时间 | firstPulse、有限终点、跳过策略、绑定与重选、source/life、叠层与刷新、成本与精度余数 fixture | 全部周期与状态机制 |
| N1–N5 网络 | 去重键与提交账本、四角色的提交权与可见性、迟加入快照与恢复、消息预算与降级、能力握手协议 | 全部真实游戏场景 |
| 离线包合同 | 真实 manifest、计划与资源与权限与依赖锁及路径与 hash | 网站导出与各域游戏验收 |

现有 `Plugin.Runtime` 在宿主程序集，不在纯 SDK 内。真实 BepInEx 插件单向引用宿主取得这一对象再使用 SDK；不得因此让 Runtime 反向引用领域。注册和世界变更在唯一模拟线程的安全阶段执行；计划加载晚于实际模块登记，不能靠无限重试等待。日志或其他线程不能直接写内核。

架构测试目前强制"所有模块程序集不含 Unity/BepInEx 依赖"。Enemy 的 Native 插件已经有原生依赖，因此它由 NativeLayout 测试单独检查而不是被排除；后续其他模块加入原生依赖时，同样要把骨架限定检查替换为相应的原生边界检查，不能简单删除依赖验证。

## 9. 每批结束需要交什么

实际修改的文件、明确完成的批次与机制、public API 与 canonical 与 binding 与版本的差异；没做的项保留原状态。输入的源代码、资源、包版本与 hash，当前游戏 build 与 hash 及许可核验；研究或 fixture 不标为实机证据。实际运行的命令、退出码和结果，未测与失败与阻塞独立列出——已经提交但结果未知的游戏动作不能重试冒充成功。跨模组消费者、网站编辑器与离线导出、Development 报告回指的必要同步项。下一个 Agent 可用的真实 API 与 fixture 与 manifest 路径及完成门槛；被替代旧路径的清理结果，或尚不能删除的实际依赖原因。

根 `git diff --check` 会看到其他已存在的未提交变化；报告要按本次拥有范围区分，不能清理他人变更来得到绿色结果。未跟踪新文件另外检查 UTF-8、链接和实际项目文件。

网站共享合同改变后，由网站责任任务运行 `node Tools/Forge/test-contracts.mjs` 和相关 typecheck/build/browser；模组方运行 C# 与实际原生证据检查。双方均提供执行结果；CI、浏览器与 mock 通过不能替代 GTFO 主客机与恢复验证。

不接受：第二事实表、默认 fallback、单 Mod 核心分支、假数据运行、吞错误、放宽预算、删除检错断言、把 mock 当实机。测试本身也要审查是否能检出真实错误。
