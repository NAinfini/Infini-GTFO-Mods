# ForgeEnemy 实施计划

未完成批次按总案 v2.0 第 13 节的 F 阶段重排。**已交付内容见 [README.md](README.md)，实际执行结果见 [VALIDATION.md](VALIDATION.md)，仓库整体状态见 [ARCHITECTURE.md](../ARCHITECTURE.md)。**

上位依据：[唯一总案](../../Infini-GTFO-Model-Site/Docs/rework/FORGE-COMPLETE-PLAN.md) 第 2、8、13 节。本包对应工作单元 **U-ENEMY**，阶段 F5（领域接入），已在推进中。发生语义差异先核对总案与实际代码，不在本文件私自改写 canonical 合同。

Enemy 包拥有敌人实例、健康与战斗接收器、AI 与感知与移动、弱点与技能，以及生成的空间要求。敌人资产属于统一资源库；Map 决定遭遇的数量、时机和分布。

**三端出生协作不变**：Enemy 声明需要的空间，Room 声明允许使用的空间，Map 与 Encounter 决定是否生成、类型、数量、时机与分布。房间 marker 不硬编码必刷敌人。

## E1 — 迁出宿主（代码切换已完成）

源码与程序集迁移、唯一注册路径、cutover 元数据都已完成，见 [README](README.md#工程结构)。保留的验收要求：无重复 provider、无重复 Hook、无重复承伤事实；卸载清理正确；Runtime 不反向引用 Enemy。

**下一个门槛是真实游戏加载与原生 Hook 行为与主客机验证**，不是再次创建或迁移同名 provider。

## E2 — 身份、生成要求与原生能力盘点

开始条件：API 取证可先行；resolver 与 requirement 的实施等待 R3 与 Map 的空间合同。

逐项冻结敌人健康、AI 状态、感知、移动、攻击与弹丸、部位与死亡、材质与动画 API 的精确 build 与 hash、签名、调用阶段和权威证据，未知保持未知。完善 GlobalID、指针与完整 world/life 的关联与失效——对象池复用、同 ID 重生、销毁后的迟到回调和 world 变化都不能复用旧引用或多次生成同一生命。从已核验的资源与原生配置声明尺寸与 clearance、移动类型、导航与碰撞、环境需求，用公开 DTO 与资源合同交给 Map，**不把原生 `EnemyAgent` 塞进 JSON**。区分外观-only 的内容与需要 Enemy 行为能力的内容依赖；模型属于资源库，不因换皮就自动强制加载全部 Enemy 能力。

已有的元数据证据是 10 个审计领域、92 个精确方法签名（含重载、声明程序集和 static/virtual/public 标记）；这是 metadata-only 等级，方法数不是已支持的能力数。

**当前 Position 快照、模型包围盒或骨骼预览都不是碰撞或导航证据。** 合法空间求解归 Map。

退出验收：同资源多实例、对象池复用、超大或特殊移动单位被不合法空间拒绝、外观包依赖按实际引用计算。

## E3 — 健康、伤害、治疗与部位结果

已交付的切片是 Heal 安全链、`death_started` 与 `limb_broken` 两个观察入口，见 [README](README.md#当前-binding全部-implementation-only)。剩余部分：

实现明确范围的 damage / health / status receiver，与玩家和世界的接收器并列；**友敌关系只由显式目标策略决定，Enemy 不硬编码"只能受到伤害"**。核验护甲、抗性、肢体、弱点、死亡与 Boss 阶段的原生时序，区分命中请求、有效 damage、limb 破坏和真正的死亡事实，防止多个 Hook 同时奖励一次击杀。所有事实携带可靠的 target 与因果；未知 source 不反推，缺 health receiver、不存活和权限不足分别拒绝。

退出验收：敌方治疗与友方伤害、满血与微量量化与上限、死亡对象、原生提交后异常、同一伤害的重复回调、部分目标成功。

## E4 — AI、感知、移动与技能门槛

开始条件：E2/E3 与 Trigger 的 T2/T3 公共条件与控制可用。

实现距离、视线、状态、冷却触发的能力控制和目标选择；AI 当前目标、owner 与 instigator、关系锚点各自独立，强制控制不能绕过权限与 receiver。实现状态与移动与警觉、scout feeler 与尖叫事实、按距离选择弹丸、破门和可破坏危险源等能力；跨域的门与设备动作由 Map 提供的 binding 提交。实现阵营变化、目标重定向与操控敌人请求的合法阶段和恢复策略；**变化后每 pulse 重验关系，不让施加时的 friendly/hostile 结果永久缓存**。用公开 Control 组合 Boss 阶段、limb 条件和限次与冷却，不另开每种敌人自带的通用图执行器。

退出验收：目标消失、换阵营、遮挡、冷却临界、重复状态事件、门销毁、能力中断与主机权威；预览行为不当作实机动作。

## E5 — 周期状态、产怪与跨域能力

开始条件：Runtime R6、Trigger T4、Map MAP4、Weapon W5 的对应原语可用。

回血、治疗攻击、感染与流血与击退与体力打击复用普通 Action、统一状态和周期调度；基于目标种类声明 receiver，而不是把感染硬套成健康数值。产怪、分裂与死亡生成向 Map 发送带 parent、cause、scope、资源与要求的显式请求；Map 管合法空间、数量与全局预算，记录实际子实例和不足原因。雾球、隐形、EMP 等能力分解为感知、表现、设备或装备的 receiver；center 与 relation anchor 独立，持续效果销毁只清理本来源。明确父死亡后的 bound/detached、子实例所有权、驱散与免疫、旧 life 与重复死亡事件策略，避免级联生成和反射的无限递归。

退出验收：父死亡与子源失效、空间不足、连锁产怪预算、两来源状态、周期数量与实际量、未知 receiver、检查点不重生同一批子怪。

## E6 — 骨骼、材质、动画与性能

开始条件：E2 的原生部位证据与统一资源来源已核验。

对齐骨骼与模型参考、部位配置、皮肤与发光与轮廓、动画时段与音效和真实 runtime binding；旧动作导入要核验格式和权利，**不能因为预览能播就宣称 game-ready**。把攻击有效时段与命中部位和视觉动画明确区分；材质、阴影、轮廓的失败不伪造玩法状态成功，也不改变伤害判定。先记录真实 profiler 和固定场景的性能，再调整 AI 更新频率与尸体清理；超预算不得悄悄丢失目标。尸体与模型清理必须结束相应的引用和 source 生命周期，不能清理仍被有效任务持有的实体却让旧 resolver 返回 true。

性能验收与功能验收分开记账：给出同设备同场景的前后实测对比，没有实测数据不得声称性能达标。

退出验收：同模型不同预设、骨骼缺失、比例与位置、部位命中、材质缺失、尸体清理后的迟到动作、主客机表现，分别验收。

## E7 — 完整机制与多人交付

开始条件：E1–E6 完成，Runtime 的网络接口（F3N 的 N1–N5）与包合同可用；支持模式的实机验收与网络侧联合完成。

逐条验收 15 类机制队列及相关的第 31/32 节原语，记录完整、部分与未实现；**部分健康绑定不能使整个 Enemy 包被标为完整**。测试实际主客机、迟加入、死亡与重生、断线与恢复、场景切换和多人一次提交；未支持的主机迁移保持明确拒绝。从 Enemy Editor 定义 → Map 的 picker 与 encounter → 离线包 → 游戏实例 → Development 回指，验证同一 resource / revision / source；Enemy 包不持有网站的模型源文件。按真实 binding 和资产闭包输出包依赖。

**被替代的外部执行路径在验收后删除。** 按总案第 4.1 节，终局是对方模组不再是运行依赖，验收决定的是何时切换；验收前仍在使用的真实依赖如实声明，不提前删除也不虚报。

退出验收：身份、攻防、AI、生成要求、表现、性能各有证据；恢复不重复治疗、产怪或奖励；不以替身或预览代替游戏验证。

## 回归用例

| 用例 | 期望 |
| --- | --- |
| `gtfo.enemy:7` 的旧生命和新生命 | 旧 schedule、lease、命令拒绝；新引用单独登记 |
| 相同 GameObject 名称或 prefab | 不参与身份决策，不改投第一个对象 |
| 正治疗量低于 SFloat16 精度 | 不倒扣血；实际量为 0 时不发虚假 `health_changed` |
| Heal 原生提交后 receiver 变更 | unknown 或部分提交可诊断，不当作未执行再重试 |
| 原生 damage 多处观察 | 同一次实际承伤与死亡奖励不重复发出 |
| 敌人换阵营后周期继续 | 每跳重新判断显式 relation policy |
| 父怪死亡触发产怪与恢复 | 同一批生成不重放；数量、空间、递归预算有效 |
| 仅修改敌人外观 | 依赖按资源实际需求计算，不自动添加全套行为 |

常规拥有 `ForgeEnemy/**`。公共 `CombatContracts`、`RuntimeKernel` 和宿主的四个世界 Hook 由 Runtime 维护。**不得在 Enemy 内复制 combat 定义或直接引用 Map / Weapon 的私有程序集。**

## 总案机制工作队列（15 类）

只分配模组侧牵头责任；语义、参数和来源继续读取网站 `catalog/mechanism-blueprints.json` 及总案第 33 节。每行的完整机制均未关闭，必须逐行完成 M1–M7；`enemy-limb-death` 的两个观察入口已有实现，**不代表 Boss、产怪或整个机制通过**。

| 机制 ID | 机制名称 | 需要落实的普通 Action / Effect |
| --- | --- | --- |
| `enemy-ability-gates` | 按距离、视线、状态和冷却发动敌人能力 | `forge.action.enemy.ability` |
| `enemy-limb-death` | 肢体破坏、死亡和 Boss 阶段联动 | `forge.action.enemy.phase_set`、`forge.action.enemy.spawn_children`、`forge.action.map.wave_start` |
| `enemy-regeneration` | 敌人回血、衰减与治疗攻击 | `forge.action.combat.heal`、`forge.action.combat.damage` |
| `enemy-infection-bleed` | 感染、流血、击退与体力打击 | `forge.action.combat.status_apply`、`forge.action.player.infection_change`、`forge.action.player.stamina_change`、`forge.action.combat.impulse` |
| `enemy-birthing` | 产怪、分裂与死亡生成 | `forge.action.enemy.spawn_children`、`forge.action.map.spawn_solve`、`forge.action.map.spawn_commit` |
| `enemy-fog-cloak-emp` | 雾球、隐形和设备 EMP | `forge.action.presentation.fog`、`forge.action.enemy.cloak`、`forge.action.weapon.action_lock` |
| `enemy-bones` | 敌人骨骼、模型参考和部位配置 | `forge.action.enemy.limb_profile` |
| `enemy-materials` | 皮肤、发光、阴影和轮廓 | `forge.action.presentation.material_override`、`forge.action.enemy.cloak`、`forge.action.combat.reveal` |
| `enemy-animation` | 动画时段、攻击音效和旧动作导入 | `forge.action.presentation.animation_play`、`forge.action.presentation.audio_play` |
| `scout-feelers` | 侦察触须和尖叫波次 | `forge.action.map.wave_start`、`forge.action.presentation.fog`、`forge.action.presentation.lighting` |
| `enemy-projectile-distance` | 按距离切换敌人弹丸 | `forge.action.combat.projectile_fire` |
| `door-breaker` | 敌人破门与破坏速度 | `forge.action.map.door_damage` |
| `killable-hazard` | 可破坏喷射器、灯光与危险源 | `forge.action.map.object_disable`、`forge.action.presentation.lighting` |
| `enemy-control` | 操控敌人、换阵营与目标重定向 | `forge.action.enemy.target_set`、`forge.action.enemy.state_request`、`forge.action.player.input_restrict` |
| `enemy-update-budget` | AI 更新频率与尸体清理 | `forge.action.map.object_despawn`、`forge.action.diagnostic.metric` |

逐机制证据单至少包含：源包精确版本与 hash 与许可、已核验与未知的语义、canonical ID 与修订、真实 binding 与权限、宿主与客户端执行侧、正反 fixture、游戏日志与 runId、实际依赖及 M7 清理结论。
