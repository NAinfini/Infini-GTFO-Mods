# ForgeWeapon 实施计划

未完成批次按总案 v2.0 第 13 节的 F 阶段重排。**已交付内容见 [README.md](README.md)，实际执行结果见 [VALIDATION.md](VALIDATION.md)，仓库整体状态见 [ARCHITECTURE.md](../ARCHITECTURE.md)。**

上位依据：[唯一总案](../../Infini-GTFO-Model-Site/Docs/rework/FORGE-COMPLETE-PLAN.md) 第 2、4.1、8、13 节。本包对应工作单元 **U-WEAPON-MOD**，阶段 F5（领域接入）。发生语义差异先核对总案与实际代码，不在本文件私自改写 canonical 合同。

## 第三方玩法依赖是过渡态，不是常态

旧版本这份计划在 W8 里写"只有原生同等能力与实机验收通过才移除被替代的 EWC/EEC/其他执行路径"，读起来像是把保留第三方依赖当成默认。**按总案第 4.1 节，这是设计前提被写反了。**

正确口径是：第 5.1 节那 20 个功能基础包所支持的玩法能力，全部由 Forge 领域模块原生实现。**终局是对方模组不再是运行依赖，验收决定的是何时切换，不是是否切换。** 切换纪律不变——一个副作用只有一个明确执行者，不能双重提交；被替代的执行路径在验收后删除，不留永久兼容层；仍在使用且尚无原生替代的真实依赖，按实际使用如实声明，不提前删除也不虚报。

模型类模组是另一条线：它们保留为运行依赖，但底层生成逻辑归 ForgeMap，本包不涉及。

## 合同与边界

| 对象 / 请求 | 必须携带或核实 | 结果要求 |
| --- | --- | --- |
| 装备资源与实例 | definition 与 revision、slot 与 part、owner、instance、world/life、实际持有状态 | 资源与新运行实例不同；陈旧装备引用拒绝 |
| 攻击事务 | source/owner/instigator、武器实例、attack/root/cause、命中阶段、显式 recipient、成本 | 请求命中与实际 damage/heal fact 分开，不重复提交原生伤害 |
| 弹药、能源、次数 | 弹匣与备弹与电量与使用次数、单位、容量、预留与提交身份 | 报实际扣除、转移、溢出；重复事件不双扣 |
| 状态与时间 | 普通 Action、firstPulse、有限终点、目标集合策略、lease 与 source 生命周期 | 每跳重验 receiver 与成本；数值状态与引擎属性回写分别验收 |
| 部署与回收 | 库存项、实例、落点与表面与朝向、owner 与召回规则、预算 | 预留 → 合法放置 → 实体提交 → 扣费；部分或 unknown 不自动退款再复制物品 |
| 外观与反馈 | 真实资产来源、部件插槽、第一与第三人称、动画与受众 | 表现不授予游戏能力；失败不伪装成游戏提交失败或重发攻击 |

后续按实际实现建立 `GameBindings/`、装备与攻击、库存与部署、资源与表现的分区。**一个模组一个领域项目**，不把 Tool 和 Consumable 再拆成两套公共生命周期。C# 游戏引用由真实 profile 核验后加入；当前骨架不锁定尚未核定的 BepInEx GUID 或 Thunderstore ID。

## W1 — 装备身份与原生 API 核验（部分交付）

已交付元数据核验工具、精确输入锁、装备身份的托管实现，以及 **implementation-only** 等级的首批原生观察接线，见 [README](README.md#当前代码状态)。**W1 未关闭，没有任何一项游戏验证。**

已实现（均未在游戏中执行）：
- `Native/ForgeWeapon.Native.csproj` 里 7 个 postfix-only 读回 Hook（背包存入、清槽、销毁全部实例、本地与同步 inventory 的持有与收起），每个目标的签名、程序集、dump RVA 唯一且不共享、位于可执行段，均由 `tests/NativeEvidence` 对本机 build 20403457 静态核验；14 条直接调用边说明哪些上层路径能到达这些 Hook。
- 背包存入后的读回就是初始采集，不补造历史。
- 实例身份由原生接线按世界递增生成，读回探测严格比对指针、槽位、资源与 owner。
- 通过公开 SDK 注册两个观察型 trigger：`forge.trigger.input.equipped` 与 `forge.trigger.input.unequipped`，所需权限 `gtfo.equipment.wield.read`。只注册 runtimeBinding。

仍未做的：
- 游戏内加载入口。阻塞原因是没有领域提供 `gtfo.player` 引用与 SNet_Player→EntityReference 查询，Weapon 不自造玩家身份。
- 关卡拾取物、世界掉落与部署物等非背包生成路径的采集。
- 方法体内的虚调用与字段写入语义核验。
- 模型挂载、rig 与动画的运行时核验：目前只有 3 条离线静态调用边（DoWieldItem→FirstPersonItemHolder.SetWieldedItem 与 PlayAnimationsForWieldedItem，UnWield→FirstPersonItemHolder.UnWield）。
- 部件来源、实际原生组件限制、previewBinding（与 runtimeBinding 分开，源包与资产 hash 和权利证据不丢）。
- 任何动作 binding：没有执行证据，不注册。

**未观察到的转交历史不能由最终快照补造。**

退出验收：同槽位新武器、同资源多实例、转交后的旧 owner、世界切换、陈旧指针，都不混淆；无隐式的包级兼容宣称。托管替身里这五类都已覆盖（`tests/NativeAdapter` 26/26），**都没有游戏验证**，退出验收因此仍未满足。

## W2 — 攻击、命中和接收者链

开始条件：Runtime R3/R4 与 Enemy/Map 的所需 receiver 合同可用。

区分攻击意图、允许与准备、成本预留、原生开火与命中、实际承伤与治疗、提交后事实；每个阶段有明确的可修改数据和合法线程，**不能在已提交的 Hook 里假装取消**。实现 hitscan、projectile、近战的基础攻击输出、显式 target 和实际结果；来源未知时保留未知，不依据敌人对象反推攻击者或队伍。为命中附加效果、爆炸与衰减、弹片与递归弹丸、弱点与背击与部位倍率、护甲与破甲、生命缩放攻击，逐项分解为普通的条件、Modifier 和 Action。确保 Enemy 与 Map 负责 receiver 的实际游戏写入；Weapon 和原生游戏事件之间指定唯一的副作用提交点。

退出验收：友伤、敌伤、友疗、敌疗四组合，未命中、无 receiver、无实际变化、多目标部分成功、同一攻击多 Hook、递归上限。

## W3 — 库存、弹药、能源与费用

开始条件：Runtime R5 事务可用，W1 的身份与实际库存 API 已核验。

统一预留、提交、释放与有效期，区分每次开火、每命中、每跳、每使用、每部署的计费时机；资源不足明确拒绝，**不先执行效果再猜要扣多少**。实现弹匣与备弹转移、备弹即弹匣、真实丢弹装填、起始与补给缩放、工具电量和使用次数；整数与归一化单位只在边界转换一次。实现补给合并与携带上限与转交、放回容器与取回、背负任务物品与终端领取的库存一侧；Map 负责世界对象和命令语义，不重复扣或生成。记录 actualCost、余额与容量、溢出与部分提交；原生操作后无法读回时返回 unknown 并保存提交证据，**不自动退款后再发物品**。

退出验收：最后一发与一格与一次使用的竞争、重复交易、填满库存、取消与过期、近容量溢出、转交中断、每跳欠费。

## W4 — 周期、状态与临时属性

开始条件：Runtime R6 与 Trigger T3/T4 可用，W2/W3 的普通效果与成本完成。

统一持续伤害、弹药恢复与消耗、治疗与感染、温度与热量、蓄能与延迟药剂：用公共的 Interval 与 Pulse 和普通 Action，**不建立 DoT / HoT / AmmoRegen 私有 timer**。区分固定每跳、连续速率、有限总量和原生被动回复，验证 interval、首跳、有限终点与整数余数；不能把跳过的 pulse 仍显示为已累计回复。实现临时 profile、攻速与换弹与后坐与散布与保护与禁用等属性贡献和 action-lock；每个来源独立 lease，移除后重算而不恢复过时快照。处理换装、松键、中断、回收、来源销毁、玩家倒地与死亡与重生，按 source-bound / detached 和重施策略明确续期、重置、叠层。

退出验收：精确 10 跳的请求量、满容量零有效量、两来源叠加与单源驱散、到期终点、跳帧、持续引导欠费、旧 life 清理。

## W5 — 部署、回收与空间工具

开始条件：Map 的 MAP2 合法空间、W3 库存事务、Trigger T4 的通用区域能力可用。

实现部署哨戒、地雷与绊线、表面安装与随机分布、预警与延迟起爆、回收：明确 place / activate / trigger / destroy / recall 的生命周期和唯一实例所有者。把泡沫载荷与散射胶泡与泡沫雷、驱雾器、荧光棒、可充电手电、热成像与传感器映射到普通效果与明确的真实游戏接收能力。`programmable-field` 使用共享的 field 与 target 原语：中心与关系锚点独立，enter/stay/exit、友敌混合分支与每跳成本显式，**不另造治疗信标框架**。实现多次药剂、延迟自疗与自爆、携带物周期效果；延迟期间的销毁、转交、死亡按合同处理，不因定时器仍存活就作用到新生命。

退出验收：非法表面与空间、生成失败后的资源处理、回收双请求、source 清理、all-target 超预算、间断扫描、完整成本账目。

## W6 — 射击节奏、碰撞与动作组合

开始条件：W2/W4 的基础攻击与属性已完成，Runtime R3 的公共动作互斥合同明确。

实现多重射击与偏移、连发与二段扳机与自动开火及取消、动作缓冲、连续射击加速；**节奏使用实际的模拟时间而不是渲染帧**。实现穿透与逐次衰减、有厚度弹丸与碰撞过滤、辅助瞄准与目标锁定、消声与听觉半径；听觉事实由 Enemy 的感知接收，不在 Weapon 内直接重写 AI。实现近战的有效挥击窗口、多目标去重、姿态与友伤规则、限时格挡与反射与奖励、弱点倍率；每次攻击的 root/cause 与唯一命中身份可追踪。与 Map 的 MAP8 同步跑动与空中动作和装填互斥、冷却与资源限制。

退出验收：不同帧率、连发取消同 tick、动作缓冲溢出、重复碰撞与多次穿透、目标锁失效、格挡过期点、递归反射预算。

## W7 — 局内装备选择、进程与表现

开始条件：W1/W3 与 Runtime R3 的槽位与选择合同可用，资产来源已核验。本批分 a、b 两个交接点。

W7.a 先独立实现同槽选择、局内换枪与换近战与第三槽的实际装备动作与事务 fixture，交付给 Map 的 MAP6；**W7.a 不等待奖励系统**。W7.b 在 MAP6 与 Trigger T5 就绪后接入 gear progression 和奖励与随机装备场景；候选池与奖励授予由 Map 和 Trigger 编排，Weapon 不复制抽卡系统。

完成模块化模型、皮肤、材质、配色与第一/第三人称、握持、瞄准与换弹与近战与工具动画的验证；真实骨架和挂载限制必须实机确认。实现 FOV、瞄准晃动、后坐反馈、滤镜和装备音效，明确受众与本地表现权限；**镜头反馈不是实际弹道或伤害修改**。核对全部 49 类牵头机制及总案 32.2–32.4 与 32.13 的相关 Action 与状态；没有独立原语缺口的机制用组合完成，未知语义保留在 M1。

退出验收：更换装备取消旧 lease 与输入、背包满时奖励失败、第一与第三人称一致、换弹握持与 scale 正确、表现关闭不影响已提交的伤害。

## W8 — 多人、恢复、性能与替代收口

开始条件：所有实际使用的能力完成 M5；Runtime 的 F3N（N1–N5）网络接口与离线包合同可用；本批的游戏验收与网络侧联合收口。

逐能力验证 owner 请求、host 提交、client 表现；客户端不能伪造成本、命中或目标；迟加入、断线、换主机在未支持时明确拒绝。检查 checkpoint 的弹药、库存、部署物、定时任务与状态、最后提交序号；恢复不复制物品、不补发首跳、不重复原生射击。在固定的复杂场景里测弹丸、命中、字段查询、场数量与分配开销，过预算时提供明确结果；**效果的 "all targets" 不得因性能策略悄悄漏目标**。

**替代收口按第一节的口径执行**：原生同等能力通过实机验收后，被替代的外部执行路径删除，不留永久兼容层；来源和权利证据保留。发布时按实际的资产与行为依赖声明——仍在使用且尚无原生替代的依赖如实列出，已经被替代的不再列。

退出验收：单人、主客机、迟加入、断线恢复、压力预算，以及资源与动画与行为各自有证据；不能用预览截图或 mock 判定完整兼容。

## 关键回归场景

| 场景 | 必须检出的错误 |
| --- | --- |
| 满血治疗 / 满电充能 | 请求量与有效量分离，零有效量不发虚假变化事实 |
| 射击与命中的重复回调 | 原生伤害与附加效果不被重复提交 |
| 使用后延迟效果期间换装、转交、死亡 | 按明确的 source/life 策略，不命中新实例 |
| 最后一发子弹 / 最后一次使用 | 一次成功与明确成本，不免费维持持续效果 |
| 两来源相反的属性贡献 | 释放其中一个仍正确重算，零乘数不除零 |
| 部署实体成功但读回失败 | unknown 或部分结果保留证据，不简单退款并重新放置 |
| 预算只够部分目标 | 显式的部分结果或拒绝，不把 all-target 宣称完整 |
| 只有模型资源变更 | 不自动声明新的逻辑 binding 或整包玩法兼容 |

常规只拥有 `ForgeWeapon/**`。Runtime 维护事务、状态、时序；Trigger 维护通用组合；Map 与 Enemy 提供各自真实的 receiver。共享接口的变化先交给接口负责人，**不能跨目录源码链接或反射另一个包的内部字段**。

每批交接给网站任务的内容：装备资源与运行实例的差异、slots 与 parts 限制、节点和 receiver 与成本参数、绑定与版本与权限清单、请求量与有效量的例子、真实依赖和第一/第三人称的验收证据。**Workshop 的三个模式必须共用这些合同**，不以三个编辑视图创建三套游戏 API。

## 总案机制工作队列（49 类）

只分配模组侧牵头责任；语义、参数和来源继续读取网站 `catalog/mechanism-blueprints.json` 及总案第 33 节。每行当前均为待实施，必须逐行完成 M1–M7。

| 机制 ID | 机制名称 | 需要落实的普通 Action / Effect |
| --- | --- | --- |
| `hit-effect` | 命中效果与任意接收者 | `forge.action.combat.damage`、`forge.action.combat.heal` |
| `accelerating-fire` | 连续射击加速或增伤 | `forge.action.weapon.fire_rate`、`forge.action.combat.attribute_apply` |
| `ammo-transfer` | 弹匣与备弹转移 | `forge.action.weapon.ammo_transfer` |
| `ammo-regen` | 按时间回复或消耗弹药 | `forge.action.weapon.ammo_add`、`forge.action.weapon.ammo_consume` |
| `armor-protection` | 减伤与破甲 | `forge.action.combat.attribute_apply`、`forge.action.combat.armor_shred` |
| `bio-mark` | 命中、工具或投掷物标记 | `forge.action.combat.mark` |
| `damage-over-time` | 流血、腐蚀等持续伤害 | `forge.action.combat.status_apply`、`forge.action.combat.damage` |
| `explosive-hit` | 命中爆炸与范围衰减 | `forge.action.combat.explosion` |
| `shrapnel` | 命中弹片与递归弹丸预算 | `forge.action.combat.shrapnel` |
| `foam-payload` | 胶泡命中、直接胶量与散射胶泡 | `forge.action.combat.foaming` |
| `health-scaled-shot` | 按生命值变化的攻击 | `forge.action.combat.attribute_apply` |
| `recoil-spread` | 后坐力、散布、射速及装填修正 | `forge.action.weapon.recoil`、`forge.action.weapon.spread`、`forge.action.weapon.fire_rate`、`forge.action.weapon.property_profile` |
| `temporary-profile` | 临时属性组与数据切换 | `forge.action.weapon.property_profile` |
| `reference-event` | 效果间引用触发与重置 | `forge.action.diagnostic.trace` |
| `multi-shot` | 多重射击与可定义发射偏移 | `forge.action.combat.projectile_fire`、`forge.action.weapon.firing_offset` |
| `wall-pierce` | 穿透物体与逐次衰减 | `forge.action.combat.projectile_penetration` |
| `thick-projectile` | 有厚度弹丸与碰撞规则 | `forge.action.combat.projectile_fire` |
| `auto-aim` | 辅助瞄准与目标锁定 | `forge.action.weapon.auto_aim` |
| `input-fire-modes` | 全自动、二段扳机和连发取消 | `forge.action.weapon.fire_mode`、`forge.action.weapon.auto_trigger` |
| `action-buffer` | 连发期间动作缓冲 | `forge.action.weapon.reload`、`forge.action.weapon.reload_cancel` |
| `silenced-shot` | 消声、听觉半径与睡眠警觉 | `forge.action.enemy.noise_emit`、`forge.action.enemy.threat_change` |
| `weakspot-multipliers` | 背击、肿瘤、部位倍率 | `forge.action.combat.attribute_apply` |
| `fps-independent-fire` | 独立于显示帧率的射击节奏 | `forge.action.combat.hitscan_fire` |
| `action-lock` | 动作禁用与 EMP 免疫 | `forge.action.weapon.action_lock`、`forge.action.combat.status_immunity` |
| `parry` | 限时格挡、反射与成功奖励 | `forge.action.combat.hit_cancel`、`forge.action.combat.reflect`、`forge.action.combat.explosion`、`forge.action.combat.heal`、`forge.action.weapon.ammo_add` |
| `melee-swing` | 近战挥击时段和多目标碰撞 | `forge.action.combat.melee_swing` |
| `melee-friendly-fire` | 近战友伤与姿态安全规则 | `forge.action.combat.damage`、`forge.action.combat.hit_cancel` |
| `reserve-magazine` | 备弹即弹匣与真实丢弹装填 | `forge.action.weapon.ammo_transfer`、`forge.action.weapon.ammo_consume` |
| `resupply-scaling` | 补给、起始弹药按参与人数缩放 | `forge.action.weapon.resupply_policy`、`forge.action.inventory.refill` |
| `deployed-sentry` | 部署哨戒、锁定、开火与回收 | `forge.action.weapon.aim`、`forge.action.combat.hitscan_fire`、`forge.action.inventory.recall` |
| `mine-tripwire` | 绊线、接近、预警和延时起爆 | `forge.action.inventory.arm`、`forge.action.inventory.detonate`、`forge.action.combat.explosion` |
| `mine-placement` | 地雷随机分布与表面安装 | `forge.action.map.spawn_solve`、`forge.action.inventory.deploy` |
| `foam-mine` | 泡沫地雷与分批胶泡 | `forge.action.combat.foaming`、`forge.action.combat.projectile_fire` |
| `programmable-field` | 持续范围信标与双分支效果 | `forge.action.combat.heal`、`forge.action.combat.damage`、`forge.action.combat.status_apply` |
| `syringe-composition` | 延迟药剂与多次使用 | `forge.action.combat.heal`、`forge.action.combat.attribute_apply`、`forge.action.player.movement_profile` |
| `fused-self-effect` | 定时自爆或延迟自疗 | `forge.action.combat.explosion`、`forge.action.combat.heal` |
| `fog-repeller` | 雾排斥场和可调范围 | `forge.action.presentation.fog`、`forge.action.inventory.field_create` |
| `glowstick-field` | 荧光棒照明、侦测与标记 | `forge.action.presentation.lighting`、`forge.action.combat.mark` |
| `flashlight-battery` | 可充电手电与发光配置 | `forge.action.weapon.flashlight`、`forge.action.weapon.energy_change` |
| `thermal-sensors` | 热成像、探测器与独立 HUD | `forge.action.combat.reveal`、`forge.action.presentation.hud_marker` |
| `supply-stack` | 补给合并、携带上限和转交 | `forge.action.inventory.transfer`、`forge.action.inventory.capacity_apply`、`forge.action.inventory.stack_set` |
| `drop-return-resource` | 物品放回容器与取回 | `forge.action.inventory.drop`、`forge.action.inventory.pickup` |
| `back-carry` | 背负大型任务物品 | `forge.action.inventory.equip`、`forge.action.player.movement_profile` |
| `carried-hazard` | 携带物造成周期伤害或恢复 | `forge.action.combat.damage`、`forge.action.combat.heal`、`forge.action.player.infection_change` |
| `terminal-consumable` | 终端领取消耗品 | `forge.action.inventory.give`、`forge.action.map.terminal_output` |
| `gear-progression` | 进程解锁与同槽装备选择 | `forge.action.inventory.equip`、`forge.action.map.progression_update` |
| `midrun-gear-swap` | 局内换枪、换近战和第三武器槽 | `forge.action.inventory.equip` |
| `modular-visuals` | 模块化皮肤、材料和配色 | `forge.action.presentation.material_override`、`forge.action.weapon.part_transform`、`forge.action.weapon.part_visibility` |
| `camera-feedback` | FOV、后坐反馈、瞄准晃动与滤镜 | `forge.action.presentation.camera_effect`、`forge.action.weapon.zoom` |

逐机制证据单至少包含：源包精确版本与 hash 与许可、已核验与未知的语义、canonical ID 与修订、真实 binding 与权限、宿主与客户端执行侧、正反 fixture、游戏日志与 runId、实际依赖及 M7 清理结论。
