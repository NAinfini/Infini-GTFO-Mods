# ForgeTrigger 实施计划

未完成批次按总案 v2.0 第 13 节的 F 阶段重排。**当前已交付内容见 [README.md](README.md)，实际执行结果与失败见 [VALIDATION.md](VALIDATION.md)，仓库整体状态见 [ARCHITECTURE.md](../ARCHITECTURE.md)。**

上位依据：[唯一总案](../../Infini-GTFO-Model-Site/Docs/rework/FORGE-COMPLETE-PLAN.md) 第 2、4.1、5、6、7、13 节。本包对应工作单元 **U-TRIGGER**，阶段 F3（94 个通用地基原子）与 F7（20 个功能基础包去重后的 244 个原子），并参与 F8（补齐到 614）。发生语义差异先核对总案与实际代码，不在本文件私自改写 canonical 合同。

## 本包的范围来自哪三个数字

总案第 6.1 节固定了口径：**614** 是词表全量（trigger 215、action 190、condition 63、control 46、selector 44、modifier 37、variable 7、event 7、state 5）；**346** 是 125 组机制蓝图引用到的不同原子；**244** 是 20 个功能基础包对应的 64 组机制引用到的不同原子；**268** 是没有任何机制引用的原子。

244 里有 **94 个**是 control / condition / selector / modifier / event 的通用地基，占 57% 的使用量，做一次全局复用——**这 94 个是本包的第一优先级**。剩下 150 个领域原子（trigger 65 + action 85）里有 142 个只被一组机制引用，是单点特性。

268 条不是废料，缺的是外部需求信号而不是必要性：variable 全部 7 条、state 全部 5 条、event 7 条中的 5 条一条都没被机制引用，而它们显然是框架必需的基础件。总案裁决是**全部做进去**，理由是自由度；其中 variable / state / event / session / authoring 因为是基础件，优先级与地基同级而非最低。

第 31 节的编辑器本地事件不进入玩家 Runtime 包。

## 每类节点的分工

| 节点类别 | 本包工作 | Runtime / 领域责任 |
| --- | --- | --- |
| Trigger / Event | 订阅 canonical 事实和作者事件，区分观察与请求 | 原生事实由领域观察；Runtime 保留真实因果 |
| Selector | 明确输入、类型、集合、关系锚点与稳定排序 | 领域提供真实对象查询与 receiver；公共预算与 epoch 由 Runtime 验证 |
| Condition | 纯判断，不产生副作用；unknown 不静默变 false | 需要读真实状态时通过公共快照，不直接反射世界 |
| Modifier | 单位明确的数值、向量、权重变换 | 属性应用与持久贡献由 Runtime lease 和领域 setter 处理 |
| Control | 编排 Delay/Interval/Branch/Sequence/Join 等语义 | 唯一队列、状态、取消由 Runtime 内核执行 |
| Variable / State | 使用定义、作用域、版本描述状态 | 存储、叠层、lease 与恢复公共化，不在节点里开静态字典 |
| Action / Result | 通用消息与控制请求及领域动作组合 | 真实游戏副作用由所属领域唯一提交；结果不可伪成功 |

必须确认的四类合同：**图输入**要有精确的 resource/graph revision、nodeId、参数、typed ports、执行 domain、binding 与版本、权限，保存重开编译一致，不重新解释 UI 标签；**查询输出**要有完整实体引用与结果完成性、稳定顺序、预算与无结果原因，entity kind、relation、effect polarity 三者独立，没有 context actor 不能默认为 owner；**控制输出**要有执行分支、计划 tick 与实际 tick、scope 与 source 生命周期、typed result 与取消到期原因，queued 不等于 committed；**状态引用**要有 definition/revision、world/life、source、scope 与 stack group，临时分支结束只释放自己拥有的 lease。

## T1 — 目录与执行合同（门槛未过）

已交付的是完整目录审计、Runtime API 2.0.0（计划 schemaVersion 2：位置 binding 索引、预解析 layout、槽位输入）正反例、逐 owner 的元数据审计与精确版本检查。审计报告从当前源码生成，逐行记录 canonical ID、目录 JSON 指针、typed 合同来源与版本与端口与单位与 domain、差异和前置条件；它的显式 kind 是 `generated-non-executable-source-audit`，**永远不作为 Registry 加载，也不得用来制造 handler**。

未过的门槛有三处，按优先级：

**共享 heal 的作者元数据分歧（已不复现）。** 2026-09-13 完整入口通过（`artifacts/trigger-20260913-101425`），t1 子项 `metadataReady=true`，见 [验证记录](VALIDATION.md)。

**目录与 typed 定义的 domain 差异。** 2026-09-13 审计识别出 60 行差异：56 个作者定义相对目录多了 session；2 个作者定义多了 logic；`forge.trigger.combat.damage_applied` 与 `health_changed` 这 2 个记录在案的原生 fixture 定义缺目录的 player、多出 map/room/logic。两侧文件都归网站所有（目录与 `logic-primitives.ts`、原生 fixture），明细已交网站会话裁定。记录在案的原生 fixture 不是当前游戏支持的证据。所有者必须逐条定论，不能静默放宽或收窄某个 canonical 定义。

**资源 revision 闭包语义。** 一个文本合法但不存在于资源目录里的 revision 当前被接受为 provenance。套件如实记录这个事实，不宣称有拒绝保证。

退出验收：未知节点、修订、数据引用被拒绝；标签别名和预设不创建重复语义；完整目标保留而支持状态诚实；默认完整入口退出 0。

## T2 — 纯计算、集合与显式目标选择

底层方法与筛选已实现（见 [README](README.md)），剩余部分：

**权重抽样的正式作者合同。** `forge.selector.target.weighted` 仍是规划目录项。需要网站给出正式的参数与端口合同，本包再接图绑定。`WithReplacement` 的结果是明确的 occurrence 列表，**不能直接伪装成禁止重复的 Runtime `entity`（`cardinality: many`）端口**。

**可变端口的图接入。** 网站已把 all/any、add/multiply/minimum/maximum、union/intersection、sequence 升级为 1.1.0 并加上 variadic 元数据（重复输入用 `input_count`，sequence 用 `step_count`，作者参数范围 2…32），Runtime 的 Plan v2 加载器已能按参数展开这些元数据，但只执行 trigger→action 线性步骤，纯节点与控制节点没有 lowering。本包等 R4 的完整 lowering 落地后再实现对应版本的计算、集合与控制接入。**不能删掉 variadic 字段、忽略版本断言、把新定义降级成旧定义，或把 Selector/Condition 包装成 Action 来制造通过结果。** 历史回归确需 1.0.0 时必须显式锁定并消费该版本的真实定义，与 1.1.0 的支持状态分开记录；生产导出不能自动降级。

**原生查询与提交前重验证。** LOS、碰撞、导航可达性都还没有；领域提交前的重新核验链路未接通。

退出验收：四种友敌伤疗组合、actor 分离、乱序输入稳定、缺少 receiver、候选不足和预算超限各有确定结果；纯节点不改变游戏状态。

## T3 — 完整控制组合

开始条件是 R4 的图执行与调度接口通过合同测试。

按总案定义 Branch/Switch/Sequence/Parallel/Join、ForEach 与有限循环、Gate/Once/Counter、Cooldown/Throttle/Debounce、Delay/Pulse/Interval 各批；每类先写可检出错误的用例再接唯一内核。固定控制状态的 scope、拥有者、执行顺序与 join 的完成/失败/取消规则；不允许无界循环、同步递归、自发重复调度或域线程池执行。周期控制输出普通动作输入；立即或延后首跳、有限 pulseCount 与终点、到期边界明确，历史成员不可补算，跳过数与迟到策略进入结果。

**不能把 T3 的控制折叠成专用 Action 绕过现有 ABI。**

退出验收：相同 tick 幂等、join 不重复完成、零成员 foreach、循环预算、重入、取消父与子 scope、过期点无额外一跳。

## T4 — 范围场与持续交互

开始条件是 T2/T3、Runtime R6 与领域空间及成本原语可用。

以共享的 field 与 target 原语组合 center、shape、relation anchor、enter/stay/exit、每分支的 recipients 与 action；**不创建 HealingBeacon 专用子类或第二个扫描计时器**。每 pulse 重新核验几何成员、relation 与 receiver、world/life 与成本；center 可以随部署物移动，而 owner 与 instigator 的关系锚点仍然独立。正常离开、来源销毁、回收、死亡、卸载和世界切换分别处理；source-owned lease 只清理本源，不能用 exit 回调撤销其他来源的状态。timed-interactions 的距离、持续输入、打断与松手、结束与成本规则要实现完整，资源不足立即按规则取消而不是到最后一次性假成功。

退出验收：混合的友军治疗与敌军伤害、反向关系、自疗、移动中心、换 owner、旧 life、all-target 超限、过载跳过与单源清理。

## T5 — 跨域组合与事务结果

开始条件是 Runtime R5/R6 和 Weapon/Enemy/Map 所需 Action 已实现。

`shared-combat-effects` 使用已提交的 damage 与 heal 事实组合吸血、分摊、反射，继承 root/cause，禁止反射→反射无限递归，也禁止依据请求量发奖励。`random-gear` 只编排受限候选池和选择意图，Weapon 实施实际的库存与槽位交换，Map 处理奖励发放，Runtime 管同一事务。奖励、消耗和结果事件按实际提交分支流动；部分成功不能触发全量奖励，失败不能释放已提交成本后再执行效果。自定义 typed messages 与状态变更不能借跨图消息绕过权限、因果深度、预算或执行 domain。

退出验收：零实际伤害、部分目标成功、递归反射、重复奖励选择、共享资源不足与源失效均有正确结果，不产生免费副作用。

## T6 — 首批端到端场景

开始条件是 Map 的门与终端与扫描与警报与生成可用，以及 Runtime 的网络一致性基础（F3N 的 N1/N2）可用。

完成钥匙与发电机门：交互事实 → 合法对象与权限 → 库存或发电机条件 → 门动作 → 实际状态事实；重复交互不能扣两次或开错门。完成扫描与警报：参与条件 → 进度与完成 → 警报状态与后续动作；中断、掉线、重复完成、客户端请求均由明确的权威处理。完成刷怪：Trigger 决定何时请求，Map 解合法空间与数量与分布，Enemy 提供要求与实例行为；生成不足返回部分结果，不补到任意地方。与网站任务用真实离线导出包联调四个编辑器视图。

退出验收：主客机、重复输入、断线与恢复、取消和错误对象的完整矩阵通过；浏览器通过与游戏通过分别记录。

## T7 — 完整词汇覆盖与交付

开始条件是已使用节点逐个完成 M1–M6，离线包结构固定。

逐条清点第 31 节的通用节点与本计划的机制队列，未实现项保留精确缺口，不把三类演示当作全部控制能力完成。导出真实的 bindings、verification 与依赖，对纯节点与 host/owner/presentation 节点明确区分；编辑器本地事件不注册到玩家包。清理被公共控制替换的专用计时器、重复状态和旧图路径。

**被替代的第三方执行路径在验收后删除。** 按总案第 4.1 节，终局是对方模组不再是运行依赖，验收决定的是何时切换而不是是否切换；仍在使用且尚无原生替代的真实依赖按实际使用如实声明。

退出验收：无第二调度器、无未使用 handler、无 planned→implemented 虚标；实际使用的全部节点和生命周期路径有证据。

## 聚焦验收用例

| 用例 | 必须验证 |
| --- | --- |
| 非敌人 receiver 或敌方治疗 | 按显式输入与接收能力执行，不默认转为伤敌或疗友 |
| Delay 后目标已重生 | 拒绝旧 life，不解析同 ID 的新对象 |
| 同帧同事件进入两个图 | 因果和成本按实际计划与事务规则去重，不无限触发 |
| 两来源信标先后销毁 | 移除一源不清除另一源的贡献 |
| Interval 丢历史 tick | 标明跳过，不按当前位置补发过去的区域效果 |
| Join 中一支 cancelled 或 unknown | 保留真实分支状态，不全部显示 succeeded |
| 纯条件与纯计算 | 可在无游戏进程下确定执行，不写世界状态 |

常规只修改 `ForgeTrigger/**`。公共调度、图执行、状态归 Runtime；原生查询与提交归领域。需要公共接口时提交 fixture 与需求给负责人，等接口落地后消费；**不得复制 `RuntimeKernel`、注册第二个世界时钟，或为测试公开第一方私有旁路。**

## 总案机制工作队列（4 类）

只分配模组侧牵头责任；语义、参数和来源继续读取网站 `catalog/mechanism-blueprints.json` 及总案第 33 节。每行均为待实施，必须逐行完成 M1–M7。

| 机制 ID | 机制名称 | 需要落实的普通 Action / Effect |
| --- | --- | --- |
| `luck-weighting` | 幸运值、权重与不放回抽样 | `forge.action.diagnostic.trace` |
| `random-gear` | 局内随机装备与受限候选池 | `forge.action.inventory.equip` |
| `timed-interactions` | 距离交互、持续输入与可中断操作 | `forge.action.map.terminal_command` |
| `shared-combat-effects` | 吸血、伤害分摊与反射因果 | `forge.action.combat.life_steal`、`forge.action.combat.damage_redirect`、`forge.action.combat.reflect` |

**这 4 行不是本包的交付规模。** 本包的规模是 94 个地基原子 → 244 个原子 → 614 条词表；机制牵头数只表示"谁负责把这组机制拼出来"。

逐机制证据单至少包含：源包精确版本与 hash 与许可、已核验与未知的语义、canonical ID 与修订、真实 binding 与权限、宿主与客户端执行侧、正反 fixture、游戏日志与 runId、实际依赖及 M7 清理结论。
