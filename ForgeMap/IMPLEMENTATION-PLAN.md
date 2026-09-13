# ForgeMap 实施计划

未完成批次按总案 v2.0 第 13 节的 F 阶段重排。**已交付内容见 [README.md](README.md)，实际执行结果见 [VALIDATION.md](VALIDATION.md)，仓库整体状态见 [ARCHITECTURE.md](../ARCHITECTURE.md)。**

上位依据：[唯一总案](../../Infini-GTFO-Model-Site/Docs/rework/FORGE-COMPLETE-PLAN.md) 第 2、4.2、8、9、13 节。本包对应工作单元 **U-MAP-MOD**，跨两个阶段：**F5**（领域接入）与 **F9**（模型 Adapter 的运行期调用方）。发生语义差异先核对总案与实际代码，不在本文件私自改写 canonical 合同。

## 本包范围按 v2.0 扩大了

除原有的空间、设备、任务、遭遇、玩家流程之外，本包新增两项职责，见 [README](README.md#本包按-v20-新增的两项职责)：**地图生成的底层逻辑**归本包，**运行期由本包调用模型 Adapter** 读取对方模组已加载的资源。范围已于 2026-09-13 按 [GENERATION-SPEC.md](GENERATION-SPEC.md) 重新划定：生成步骤 G0–G10 的输入、输出、归属、原生依赖与证据等级，资源侧 Adapter 描述符形状，以及游戏内验证步骤都以该文件为准。

批次顺序：**MAP2（静态合同与检查）→ MAP-ADAPTER 第一段（首个 profile 游戏核验）→ MAP-GEN（生成阶段）→ MAP-ADAPTER 第二段（三家作者）**，之后 MAP4、MAP5、MAP9 才消费合法空间与生成结果。相关机制的落点：`deterministic-layout` 在 MAP2 的 G2 与 MAP-GEN；`custom-room-composition` 在 MAP-ADAPTER 与 MAP-GEN；`extra-door-topology` 的连通与朝向在 MAP2 的 G4 / G6，门行为在 MAP3。

## 输入输出与所有权

| 输入 / 领域 | Map 应提供的结果 | 禁止混淆 |
| --- | --- | --- |
| room definition 与 revision、placement transform、原生来源与子房间与 plug | 可核验的实例、拓扑、合法空间、生成证据 | 预览 GLB 不替代权威布局或原生子对象身份 |
| Enemy requirement + encounter 配置 + seed 与 budget | 请求数、实际生成数、每实例引用、明确的不足原因 | Enemy 不自己决定整场数量与波次；不足不任意换房补足 |
| 门、终端、扫描、目标引用与显式参数 | 已提交的状态变化与 canonical fact | UI 对象 ID、原生指针和持久资源 ID 不互换 |
| 玩家引用、动作类型、策略、落点与资源与检查点 | 明确的 life 与 world 变化和实际动作结果 | Heal ≠ Revive；Revive ≠ Respawn；Teleport ≠ Checkpoint |
| 奖励选择、共享资源、局内进程 | 事务结果、实际授予与已锁定版本 | 选项显示不代表授予；断线不能重复领取 |
| 灯光、雾、音乐、HUD、标记 | 指定受众的表现状态 | 表现反馈不能反向充当伤害、AI 或任务事实 |
| 对方 Geo 包已加载的模型资源 | 按玩家拼装结果生成的真实世界对象 | 资源可读不等于生成合法；不持有拆解后的副本 |

## MAP1 — 原生 API 与世界对象身份

已交付的是 metadata-only 的 API 与字节证据检查工具，以及生产程序集内的内部身份表、创建生命票据与 R2 生命周期接入，见 [README](README.md#当前代码状态)。**MAP1 整体未关闭。**

三项遗留在 2026-09-13 复核后的状态：

1. **与 D3 对接真实创建上下文和 source evidence：阻塞。** 实例化点取决于 GENERATION-SPEC 第 6 节 A 组的游戏证据，Hook 由 Development D3 提供；无法证实的映射明确标未知，不借诊断的反射结果直接登记 game-verified。
2. **通过公开注册路径暴露首批已核验的读取能力：阻塞。** SDK 的 `RuntimeModule.EntityResolvers` 路径已存在，但没有经游戏核验的原生探针；注册合成探针等于伪成功，所以 `ModuleDefinition` 继续不登记 resolver。
3. **`tests/fixtures/native-identity-scenarios.json` 的十个原生身份规格：托管部分已执行，原生部分未执行。** MapIdentity 按用例 id 标记断言，并核对标记集合与规格文件一一对应；规格文件保持 `nativeExecuted: false`，每个用例的 `nativeOnly` 字段对应 GENERATION-SPEC 第 6 节 C 组的游戏步骤。

退出验收：多维度同编号、同资源多实例、生成重入、销毁后的迟到回调、同 ID 新生命都不混淆；无私有的跨模块引用。

## MAP2 — Room 空间、布局与拓扑的静态合同

范围是 GENERATION-SPEC 第 2 节的 G0–G6：拼装计划接收、资源描述符校验、固定 seed 的候选选择、布局约束、端口类型与朝向（含双面门与额外连接）、碰撞与重叠、跨区连通。全部是**生成前的静态检查**，结果带具体对象引用；**不从整房或整组的预览模型推导可执行布局**，renderer 包围盒不充当碰撞。游戏生成阶段、NavMesh 就绪与合法空间发布属于 MAP-GEN。

房间的权威数据是游戏原生的部件层级、局部变换、实例、Area 与连接器与碰撞与导航与遮挡及引用；多个房间以实例与拓扑组成房间组。共享网格与纹理字节可以去重，**资源身份、授权与实例仍然独立**。

已完成（2026-09-13，定范围）：生成步骤归属表、资源描述符形状与 24 个正反 fixture 及检查器、生成相关原生签名锁（metadata）。**尚无 C# 实现。**

开始实现的条件：MAP1 的托管身份层（已具备）；网站能导出 G0 所需的拼装计划——room 实例、Unity 空间局部变换、连接器配对、zone / dimension / layer、seed 与预算——或至少冻结其 fixture 形状。网站当前导出不含摆放坐标。

退出验收：端口类型不匹配、朝向错误、重叠、跨区不连通、空间数据未知各有确定的拒绝结果与对象引用；静态检查通过不记作生成成功。

## MAP-ADAPTER — 资源侧 Adapter（新增，F9）

Adapter 只负责"资源怎么读出来"这一层：从对方模组**已在游戏内加载的**资源里，按已核验的资源协议或 profile 取得部件、层级、局部变换、Area、连接器、碰撞、导航与遮挡数据。按已核验的协议或 profile 复用，**不逐内容包或逐编辑器增加 Provider**；走已核验 profile 的新包只需要描述符，新的加载协议才新增 Adapter。原版 GearPart、GPC 部件、enemy prefab、geomorph 与自有资源加载器各自保留真实身份，**不按包名猜兼容**。接口形状见 GENERATION-SPEC 第 3 节。

Unity 的 64 位 path ID 以十进制字符串穿过 JSON 边界。三分量与四分量的位置和法线显式处理，第四分量作为自定义标量通道保留，不做齐次除法也不丢弃。

转换器不执行上传的 DLL；**资源转换通过不证明玩法兼容**。玩家的依赖列表与现在一致——他仍然装那个 Geo 包，我们调用它；我们不持有拆解后的资源副本去分发。

**第一段：首个 profile 的游戏核验。** 开始条件：MAP2 的描述符形状稳定；网站能按 GENERATION-SPEC 3.1 输出真实包的描述符。按第 6 节 B 组核对 `gtfo.complex-resource-geomorph` 的 acquire：对象来自已加载资源，file / pathId、area 与 plug 数、plug 方向与描述符一致。此时才确定 C# 形状，之前不写空接口或占位实现。

**第二段：三家作者。** 开始条件：MAP-GEN 可用。网站现有 geometry pin 只有 CheeseGeos 与 ZaeroGeos 两家，第三家需要网站先导入。

退出验收：至少三家不同作者的 Geo 包走同一条流程；新接一个包不改 MAP-GEN。

## MAP-GEN — 地图生成的底层逻辑（新增，F9）

开始条件：MAP2 的静态检查已实现；MAP-ADAPTER 第一段通过；GENERATION-SPEC 第 6 节 A 组给出"注入原版批次"还是"自建阶段"的结论；Runtime 提供关卡构建开始、完成与 NavMesh 就绪的公开观察；Development D3 在实例化点提供创建上下文。

范围是 GENERATION-SPEC 第 2 节的 G7–G10。生成逻辑要**一次实现、对所有资源来源通用**：从 Adapter 取得已加载资源、为每个 placement / area / connector 颁发创建票据后实例化、编排原生 AIGraph 与 NavMesh 构建、就绪后做局部导航检查、发布合法空间、登记实例与拓扑。原生构建原语仍由引擎执行，本层负责决策与编排。逐家 Geo 包的差异全部收敛到资源侧 Adapter。

生成过程必须可复现：固定 seed、显式预算、稳定排序、每一步的失败原因可追踪。生成不足时返回明确的部分结果和原因，**不自动把剩余对象堆到任意位置**；NavMesh 就绪前的采样成功不记作房间可用。保留合法空间的查询入口给 encounter、deployable 和玩家落点；Enemy 提供所需的 clearance、movement 与 collision，不由 Room 自动塞入敌人数量。

退出验收：同一份拼装结果在同一 seed 下，主机与客户端生成相同的选择、实例地址与拓扑；NavMesh 未就绪有确定结果；换一家资源来源不改本层代码。

## MAP3 — 交互设备、终端与门

开始条件：MAP1/MAP2 的对象有效，Runtime R3/R5 与 Trigger T2/T3 可供动作与条件使用。

按功能分批实现 custom-world-object、门开闭与锁与可破坏门、发电机与门条件、门附属终端；请求和已提交的状态事实分开，防止两个 Hook 重复报告。实现终端交互、命令与密码与日志、查询与 PING、自定义可查询对象和 challenge code；命令参数严格类型化，作者程序只执行允许的图与命令，**不解释任意系统代码**。落实 shuttle 的接收、消耗、转运、召回与箱体状态和任务携带物的所有权交接；库存的实际变更由 Weapon 事务执行，Map 只处理世界对象和任务语义。用公开 control 处理持续交互、距离检查、破解时序和打断；提交前重新检查权限与目标状态，设备被摧毁不能继续完成旧请求。

退出验收：重复交互与查询、门销毁、终端取消、越权命令、同物品转运竞争、错误落点、客户端伪请求，各有确定结果。

## MAP4 — 任务、扫描、警报与遭遇

开始条件：MAP2 的合法空间、MAP3 的设备事实、Enemy E2 的生成要求和 Runtime 预算就绪。

实施目标计数与阈值与阶段、warden 事件、reactor 验证轮次、scan 的参与条件与路线；每个状态有唯一拥有者，完成事实只在真实提交后发布。组合生存波次、命名波次池、警报和生成预算：Trigger 管触发时机，Map 求数量与分布与间隔，Enemy 提供每种单位的空间需求和实际实例生命周期。求解合法生成点，记录尝试上限、候选不足、导航与尺寸拒绝及实际生成列表——**"fallback"只执行作者明确允许的策略**。落实保证补给与混合大型物品生成；重复波次、断线、场景切换时取消旧 scope。

退出验收：实际生成数与要求数可区分；不静默少生也不超生；scan 完成不双发；警报循环受预算约束；单人与多人的参与条件都真实。

## MAP5 — 玩家生命流程、落点与恢复

开始条件：MAP2 的空间、Runtime R3 的引用，以及 F3N 的 N3 迟加入与恢复接口可用；本批的游戏验证与 N3 联合完成。

**玩家生命周期的六类操作互不替代**：首次降落、迟加入落点、倒地救起、真正死亡后重生、检查点恢复、传送。Heal 不隐式复活，Teleport 不新建 life，检查点恢复是世界一致性恢复而不只是移动坐标。次数、冷却与代价归远征规则账本，不因换生命期或检查点恢复意外返还。

明确 player 的 alive / downed / dead / respawning 等实际原生状态映射及 life epoch 的更新点；不把诊断的猜测状态用于动作提交。合法落点验证维度、碰撞、地面与导航、占用策略；队伍部分成功要保留逐玩家的结果，**不能把传送失败伪装成已到达**。Checkpoint 独立记录世界、任务、玩家、部署物、剩余计划和提交账本；恢复时重新解析实体与资源修订，已提交的奖励、费用与首跳不能重放。

退出验收：倒地自救扣费一次、死亡不能被 Heal 意外复活、传送不新建 life、重生不继承旧 lease、恢复不重复授予或生成。

### MAP5a — 玩家实体身份（先行切片，implementation-only）

为解除 Weapon W1 的加载阻塞，从 MAP5 切出身份这一项先做，其余 MAP5 内容（alive / downed / dead 映射、救起与重生语义、落点、检查点）**仍未开始**。已交付内容见 [README](README.md#map5a-玩家实体身份implementation-only)，结果见 [VALIDATION](VALIDATION.md#map5a--玩家实体身份2026-09-13)。

规则：
- 引用为 `gtfo.player:<n>`，`n` 是本世界内按首次登记顺序递增的编号，WorldEpoch 取内核当前世界；同一 SNet_Player 上观察到新的 PlayerAgent 实例（指针变化）或 SNet_Player 对象变化即分配新 lifeEpoch，lifeEpoch 单调、不复用。
- 隐私硬规则：`SNet_Player.Lookup` 是 Steam64 账号 ID，只作 Map 私有字典键，不进入实体 ID、observer 输出、错误码或 detail、日志与测试证据，也不序列化。
- 倒地、救起、Heal、传送不改 life：只有 agent 实例替换或 despawn 才结束 life。检查点重载由宿主暂停并换世界，Map 只在 WorldChanged / Failed / Stopped 清表，不另做一套。
- 只在 host（`SNet.IsMaster`）且 Runtime Ready 时分配；**不使用 InLevel 玩法门**，因为电梯阶段的生成与进关同属一个世界。
- owner 解析不到、agent 与 SNet_Player 互链不成立、同键两个 agent，一律不登记；不按名字、槽位顺序或指针推断身份。Bot 同样规则，Lookup 稳定性未核验。
- 只注册 resolver，不注册 observer、capability 或 binding。

剩余：
- 游戏内核验（清单见 VALIDATION）。
- 与 MAP1 身份 Session 合并为同一 provider 生命周期：两者都登记 `forge.module.gtfo.map`，不能同进程并存；MAP1 原生适配器接线时必须合并，不做双注册。
- SDK 的原生实例查询（Framework 所有者，形状见 [ForgeWeapon README](../ForgeWeapon/README.md#原生观察接线implementation-only)）落地后，Weapon 才能消费；不以 Lookup 作公开键凑合。

## MAP6 — 共享资源、奖励与局内进程

开始条件：Runtime R5 事务、R6 状态与 Weapon 的库存与装备动作可用。

实现共享资源池的成员、容量、初值、消费、回收和离队策略；shared-stamina 通过公共 pool 与玩家接收器组合，**每项资源只有一个权威账本**。实现击杀经验、等级、受限奖励候选、offer 与选择与重抽与提交、进程解锁；以实际 committed fact 作为发放依据，选项和价格在事务修订下锁定。处理 custom boosters、腐化与局内规则、主动技能与生命关联提案，复用状态、成本、因果——来源未核验的 life-link 明示为提案，**不伪称已复刻第三方机制**。装备结果由 Weapon 执行，Enemy 来源的事实由其 provider 发布。

退出验收：最后一份共享资源的竞争、付费重抽失败、候选不足、重复奖励、玩家离队与重连、checkpoint 的一致性，全部可追踪。

## MAP7 — 环境、区域效果与世界表现

开始条件：MAP2 的空间、Trigger T4 的区域组合、Runtime R6 的周期与状态可用。

实现温度与热交换与暴露累积：环境值和个体积累分开，阈值、滞回、自然衰减用公共状态与 Control，**不能把每次帧更新当作一秒**。实施安全传感器与移动检测、EMP 设备分支、动态雾与灯光与战斗音乐；EMP 对 Weapon 与 Enemy 的效果通过相应 receiver，不由 Map 直接改它们的内部字段。实现世界与玩家 HUD、标记、小地图与拓扑与指引样条和音效路由，明确可见受众、维度、生命周期、预算与作者开关范围。表现失败不改变已经提交的玩法结果；视觉与音频事件不能绕过权威生成伤害、任务完成或 AI 警觉。

退出验收：区域移动与跨维度、来源销毁、温度阈值抖动、未知 receiver、标记过期、表现关闭，都不污染玩法状态。

## MAP8 — 玩家动作、机器人与剩余机制

开始条件：MAP5/MAP6 的生命与进程和 Weapon 的动作互斥合同已稳定。

逐项核验并实现滑铲与二段跳与冲刺、跑动或空中的射击与装填、玩家尺寸与通行性；动作请求、运动状态和装备动作的权威边界先明确。针对 bot 的 pickup、resupply、combat policy 使用实际的机器人 API，**不把玩家输入强制套用机器人**；同一补给或攻击仍走公共预算与成本。语音活动只消费用户明确启用、可撤回的活动信号及其合同，不把诊断录音当作默认输入；警觉效果由 Enemy 的感知接收器落实。核对所有 map / player / presentation 队列与第 31/32 节的基础能力：**遗漏项保持待实施，不能以替代标签或相似的现有节点关闭**。

退出验收：动作冲突与冷却与资源不足、尺寸改变后落点非法、机器人竞争资源、语音停用、主客机一致性；未知 API 单独列阻塞证据。

## MAP9 — 资源闭环、游戏验证与清理

开始条件：各被使用能力完成总案 M5；网站的 Map 与 Room 导出和 Development 回指可用；MAP-GEN 与 MAP-ADAPTER 已交付。

使用真实的首批已导入资源，校验来源、精确版本与 hash、权利、原生实例和实际依赖；**资源转换成功不证明外部玩法已被替代**。从编辑器保存、重开、导出到游戏生成、任务、退出、恢复联测，并让 Development 报告回指实际对象。逐项完成主客机、迟加入、断线与恢复、世界切换、数量与性能预算测试。

依赖闭包只按工程实际使用的内容与 binding 计算——**库里有某个包不代表每张地图都要它**。上游的依赖声明与工程锁定版本分开保存，不靠名字去重、不盲选最大版本、不"最后注册者获胜"；冲突时要么有明确的兼容证据，要么阻止导出。

按实际 binding 输出依赖与验证级别。**只删除已被验证替代的旧路径；不删用户布局、源资源，也不删仍在被调用的资源侧 Adapter**——按总案第 4.2 节，模型类模组保留为运行依赖，被替代的只是生成逻辑。

退出验收：实际任务可按设计完成，错误路径有结果；房间生成与导航、玩家恢复和表现分别记录，不用一条绿色测试概括全部。

## 必须保存的场景 fixture

| fixture 场景 | 区分的结果 |
| --- | --- |
| 同房间资源的两个 placement、同编号多维度 zone | 真实的 instance 与 source 身份，而非名称首项匹配 |
| 合法空间只容纳请求数量的一部分 | requested / spawned / rejected 原因；不默认超预算重试 |
| 不满足敌人尺寸或移动要求的房间 | Map 拒绝该候选，Enemy 不偷偷改为另一体型 |
| scan 结束同 tick 收到重复完成与断线 | 一次目标提交、明确的参与策略 |
| 两人同时取最后一份补给或奖励 | 一笔成功，另一笔可解释拒绝，无复制物品 |
| 传送、复活、重生、checkpoint 各一例 | world/life、装备、状态、费用和提交序号的规则各不相同 |
| 温度区域与移动信标重叠 | center、relation anchor、个人积累和 source lease 各自独立 |
| 同一拼装结果换一家 Geo 包资源 | 生成逻辑不变，只有资源侧 Adapter 不同 |
| 表现模块关闭或资源丢失 | 已提交的任务与伤害不回滚不重复，也不假装表现成功 |

## Agent 范围

常规拥有 `ForgeMap/**`；不修改 Runtime、Trigger、Weapon、Enemy 的私有实现，也不改网站页面。D3 的诊断映射由 Development 提供；Map 提供真实的创建来源接口和带失败原因的 fixture。共享接口缺失先交给 Runtime 负责人，**不临时复制 scheduler 或跨目录链接对方源码**。

功能阶段新增本域测试工程，使用模块的实际编译产物与明确的游戏替身；原生方法签名另做实际程序集检查。最终运行真实的 GTFO 主客机场景并记录 seed、resource revision、依赖和日志，不能把未来的测试路径或待执行步骤标为通过。

## 总案机制工作队列（51 类）

只分配模组侧牵头责任；语义、参数和来源继续读取网站 `catalog/mechanism-blueprints.json` 及总案第 33 节。每行当前均为待实施，必须逐行完成 M1–M7。

| 机制 ID | 机制名称 | 需要落实的普通 Action / Effect |
| --- | --- | --- |
| `shuttle-routing` | 运输箱物品接收、消耗与转运 | `forge.action.inventory.consume`、`forge.action.inventory.transfer`、`forge.action.map.shuttle_deliver` |
| `shuttle-summon` | 延后补给召回与箱体开闭 | `forge.action.map.transport_move`、`forge.action.map.object_enable` |
| `custom-world-object` | 可交互世界对象与局部事件 | `forge.action.map.object_enable`、`forge.action.map.object_spawn` |
| `coordinate-trigger` | 空间进入、离开、持续与占用条件 | `forge.action.diagnostic.trace` |
| `warden-control` | 延迟目标事件、条件和取消 | `forge.action.map.objective_state` |
| `objective-counter` | 计数器、阈值、跳转与一次触发 | `forge.action.map.objective_progress`、`forge.action.map.objective_phase` |
| `terminal-interaction` | 终端接近、命令、密码和日志事件 | `forge.action.map.terminal_output` |
| `terminal-programs` | 终端命令流水线与沙箱程序 | `forge.action.map.terminal_output` |
| `terminal-queryable` | 自定义对象可查询与 PING | `forge.action.map.terminal_output`、`forge.action.presentation.hud_marker` |
| `challenge-codes` | 反应堆与终端密码格式 | `forge.action.map.objective_progress`、`forge.action.map.terminal_output` |
| `reactor-phases` | 反应堆轮次与验证状态 | `forge.action.map.objective_phase`、`forge.action.map.alarm_start`、`forge.action.map.alarm_stop` |
| `scan-quorum` | 扫描参与人数与进度规则 | `forge.action.map.scan_progress`、`forge.action.map.scan_pause` |
| `scan-path` | 扫描路线、固定位置与全区扫描 | `forge.action.map.scan_path`、`forge.action.map.scan_start` |
| `security-sensor` | 安全传感器与移动侦测 | `forge.action.map.alarm_start`、`forge.action.presentation.hud_message` |
| `emp-system` | EMP 对设备、武器和区域的控制 | `forge.action.weapon.action_lock`、`forge.action.map.power_set`、`forge.action.combat.status_apply` |
| `wave-survival` | 有间歇的自定义生存波次 | `forge.action.map.wave_start`、`forge.action.map.spawn_commit` |
| `wave-budgeting` | 命名波次池、全局警报和生成预算 | `forge.action.map.spawn_pool`、`forge.action.map.wave_stop` |
| `spawn-fallback` | 出生位置合法性和明确的数量不足 | `forge.action.map.spawn_solve`、`forge.action.map.spawn_commit` |
| `guaranteed-supplies` | 保证补给与混合大型物品 | `forge.action.map.resource_distribution`、`forge.action.inventory.give` |
| `deterministic-layout` | 固定种子、房间选择和布局随机化 | `forge.action.map.object_transform`、`forge.action.map.spawn_pool` |
| `custom-room-composition` | 自定义房间、标记与导航接入 | `forge.action.map.object_spawn`、`forge.action.map.spawn_marker_state` |
| `extra-door-topology` | 额外门、双面门和区域连通 | `forge.action.map.door_open`、`forge.action.map.door_close`、`forge.action.map.door_lock`、`forge.action.map.door_unlock` |
| `shootable-door` | 弱门可射击与破坏反馈 | `forge.action.map.door_damage` |
| `door-terminal` | 门附属终端与定向控制 | `forge.action.map.door_unlock`、`forge.action.map.door_open` |
| `checkpoint-system` | 自定义检查点和恢复策略 | `forge.action.map.checkpoint_save`、`forge.action.map.checkpoint_restore` |
| `split-spawn` | 玩家分散降落与独立出生点 | `forge.action.player.spawn_policy`、`forge.action.player.safe_position` |
| `dimension-warp` | 跨维度传送与队伍分流 | `forge.action.player.teleport` |
| `environment-temperature` | 区域温度、热交换与过冷状态 | `forge.action.combat.status_apply`、`forge.action.combat.attribute_apply` |
| `environment-cues` | 动态雾、灯光与战斗音轨 | `forge.action.presentation.fog`、`forge.action.presentation.lighting`、`forge.action.presentation.audio_play` |
| `hacking-timing` | 可配置破解关卡和时序窗口 | `forge.action.map.objective_progress`、`forge.action.presentation.input_hint` |
| `guidance-spline` | 样条指引、路径提示与导航标记 | `forge.action.presentation.hud_marker`、`forge.action.presentation.vfx_spawn` |
| `progression-unlocks` | 完成记录、装备与关卡解锁 | `forge.action.map.progression_update`、`forge.action.progression.reward_grant` |
| `shared-stamina` | 队伍共享体力池 | `forge.action.resource.pool_define`、`forge.action.resource.pool_bind`、`forge.action.resource.pool_transfer`、`forge.action.resource.pool_unbind` |
| `life-link-proposal` | 生命关联与伤害转移（待核验来源） | `forge.action.combat.damage_redirect` |
| `experience-levels` | 击杀经验与局内等级成长 | `forge.action.progression.experience_change`、`forge.action.combat.attribute_apply` |
| `reward-offers` | 有条件奖励选项与抽卡 | `forge.action.progression.offer_create`、`forge.action.progression.offer_resolve` |
| `reward-reroll` | 可计费的重抽与奖励提交 | `forge.action.progression.offer_reroll`、`forge.action.progression.reward_grant` |
| `corruption-rules` | 局内腐化、增益与团队规则 | `forge.action.combat.attribute_apply`、`forge.action.combat.status_apply` |
| `active-skills` | 职业与卡牌主动技能 | `forge.action.combat.status_apply` |
| `custom-boosters` | 可组合强化剂和职业属性 | `forge.action.combat.attribute_apply`、`forge.action.inventory.consume` |
| `self-revive` | 消耗医疗包的倒地自救 | `forge.action.combat.revive`、`forge.action.inventory.consume` |
| `downed-penalty` | 倒地惩罚与恢复限制 | `forge.action.player.infection_change`、`forge.action.combat.status_apply` |
| `movement-abilities` | 滑铲、二段跳和冲刺动作 | `forge.action.player.movement_request`、`forge.action.player.movement_profile` |
| `movement-combat` | 跑动、空中射击与装填动作组合 | `forge.action.weapon.action_lock`、`forge.action.player.movement_profile` |
| `player-size` | 玩家尺寸与可通行性 | `forge.action.map.object_transform`、`forge.action.player.movement_profile` |
| `voice-alerts` | 自愿启用的语音活动与敌人警觉 | `forge.action.enemy.noise_emit` |
| `bot-policies` | 机器人拾取、补给与战斗许可 | `forge.action.inventory.pickup`、`forge.action.inventory.refill`、`forge.action.enemy.target_set` |
| `hud-effects` | 生命条、数值、命中与状态 HUD | `forge.action.presentation.hud_message`、`forge.action.presentation.countdown_display` |
| `map-markers` | 物资、门、敌人与多维度地图标记 | `forge.action.presentation.hud_marker` |
| `minimap-views` | 小地图、路径图与拓扑视图 | `forge.action.presentation.hud_marker` |
| `audio-routing` | 状态音效、空间语音与战斗音乐 | `forge.action.presentation.audio_play`、`forge.action.presentation.audio_stop` |

**51 这个数字不表示本包的交付优先级。** MAP-GEN 与 MAP-ADAPTER 不在这 51 类里，它们是总案第 4.2 节新增的架构职责。

逐机制证据单至少包含：源包精确版本与 hash 与许可、已核验与未知的语义、canonical ID 与修订、真实 binding 与权限、宿主与客户端执行侧、正反 fixture、游戏日志与 runId、实际依赖及 M7 清理结论。
