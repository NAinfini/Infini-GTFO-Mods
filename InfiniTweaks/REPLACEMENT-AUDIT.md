## 2.5.2 当前标记状态 — 2026-09-09

本轮按 Localia ResourceHelper 3.0.1 发布包核对并实现显示行为，取代下方历史审计中的永久PING扩展、20米消耗品、同zone越距和瞄准全隐藏。当前详情见 HUD-BEHAVIOR.md；58张内嵌图案及11色钥匙卡见 MARKER-COVERAGE.md。原生inactive组件不会接收SetAlpha已用游戏二进制确认，离线回归先复现旧代码的恢复失败、再通过修复。游戏渲染和多人验收仍待重启后进行。以下按版本记录的历史发现不代表当前实现状态。

# Infini Tweaks 与待替代模组：逐项功能审计

**历史功能审计。当前 2.4.3 的修复、实际离线验证和未验收项统一见 [UPSTREAM-ACCEPTANCE.md](UPSTREAM-ACCEPTANCE.md)。下文各版本的“本轮／尚未安装”仅对应其历史时间点，不代表当前安装状态。**

审查日期：2026-09-08。范围来自本次对话中的替代名单、项目源码、Temp 配置档，以及对应缓存版本的作者说明和 DLL 实现。这里只调查，不修改源码、配置或安装状态。

## 先看结论

后续需求更新（2026-09-08）：用户取消固定奖励模式，改为仅放大各品质实际获得的奖励。当前开发源码已将倍率默认值及上限调为 100000，保留零收益为零；固定奖励分支已删除。下表 ×5 描述保留为审计基准的历史状态，不代表这一后续修改。尚未更新 Temp 安装或验证实际到账数量。

最新明确排除：**不需要 Booster 无条件、无负面、自定义／编辑器，以及额外任意放大效果数值。** 本轮曾添加的相关代码、配置和专用测试已移除，没有部署到 Temp。保留精确模板完美数值、不消耗、实际收益 ×100000；原有条件和负面效果不删除。这些排除项不再是待补缺口，优先于下方旧审计表。

### 后续开发进度（不是已安装版本的能力声明）

上述开发改动现汇入 **2.4.1 本地候选**，未部署。最新用户决定：**统计重新用自家源码实现，不内嵌／加载原版 DLL；聊天与武器说明只用 Archive，自家这两项删除；不需要 EWC 特殊武器适配。** EWC、多行聊天、分页、自定义说明和击杀断点不再是待补需求。新增的标记/扫描性能优化及同类实现对照见 [PERFORMANCE-REVIEW.md](https://github.com/NAinfini/GTFO-Forge/blob/main/Mods/ForgeDevelopment/PERFORMANCE-REVIEW.md)；下方原始 2.2.9 审计表只保留为历史基准，不覆盖这些最新决定。

最新交互要求已写入源码：持包只显示对应资源，红→黄→绿连续变化；名称／距离／资源行独立控制。删除普通队友背包清单，不显示终端使用者名字；电池／CRYO 等任务携带物仍跟随携带者并显示携带者名称。近处／注视显示详情、远处不注视只留图标，发现改用 4 Hz 附近空间查询。详见 [HUD-BEHAVIOR.md](HUD-BEHAVIOR.md)。这些选择替代旧表中冲突的显示方案，不是所有上游功能已完成的声明。

| 范围 | 当前源码进度 | 未完成／待验证 |
| --- | --- | --- |
| 标记 | 原有设备 Setup 回调之外，补反应堆 OnBuildDone 区域／标记登记、缺失区域节点、终端 ID／名称变化、已知标记重绑定和替换对象安全清理；不变登记不重建图标、不自动发现新设备 | 16 项生产登记测试通过；手动隐藏状态的检查点恢复、原版所有细化显示配置仍非完整等价；多人／拾取／重开实测未完成 |
| 终端 | 输入退出前同步、输入时序、占用保护、32 条命令排队、卡住恢复；修正同步回调导致新监视记录误删。持续交互在单次 FixedUpdate 内保存／恢复原生控制标志 | 12 项生产终端补丁离线检查通过；多人延迟、持续交互实测未完成，不宣称覆盖原版所有小修复 |
| 武器说明 | 只用已开启的 Archive WeaponStats；自家参数页、分页、断点与相关源码已删除 | 用户接受差异，不再补齐 DescriptiveWeaponStatShower 全功能 |
| 战斗统计 | 自家 CombatStatistics/CombatStatsModel/Format/View 重写；单颗子弹／扳机组／穿透全命中、自定义模板、弱点有效伤害、按装备槽原生结算；v5 会话同步，无原版 DLL | EWC 已明确排除，不再待补。全部原版模板／协议不支持；原生未归类伤害留在 Other；换枪不单独建立武器实例历史；布局、检查点、IL2CPP／多人实测未完成 |
| Booster | 保留用户指定的三项能力，不加入刚明确拒绝的功能 | ×100000 尚未写入 Temp 配置，实际到账数量未验证 |
| 聊天 | 只用已开启的 Archive ChatTweaks；自家聊天输入／历史／打字状态源码及测试已删除 | 用户接受差异，不再补齐 BetterTextChat 编辑器 |

原始逐项审计表仍以候选 2.2.9 为基准。以上改动目前只是开发源码和本地构建，不代表旧 dist 包或 Temp 2.2.7 已更新，也不代表完整替代已完成。

**当前不是所有剩余需求均已实机验收。** 本轮指出的反应堆／动态设备登记缺口已补；EWC、聊天／武器说明和 Booster 编辑器等已明确取消的内容不再列为待补。原版未选取的细化配置不冒称等价，离线检查也不冒充全部游戏功能正常。

同时必须区分三个层次：

- **实际安装：Temp 的 Infini Tweaks 2.2.7。**
- **本报告的自家实现基准：源码／候选包 2.2.9，尚未部署。** 2.2.8–2.2.9 的修正不能当作当前游戏已经生效。
- **组合提供者：Infini Tweaks + Hikaria Core 1.0.1 + Hikaria 的 Archive 包 0.0.7。** 游戏菜单显示 TheArchive 0.0.838；这不是同一个版本编号体系。

“覆盖”仅代表找到相应实现，不代表游戏内和多人验收已经通过。现有离线回归结果不能证明视觉效果、所有敌人／装备／关卡及全部多人时序正常。缓存存在也不等于该模组曾在 Temp 安装；没有完整的卸载操作历史可据此复原。

## 状态口径

| 标记 | 含义 |
| --- | --- |
| 自家覆盖 | 2.2.9 中有对应实现；仍受该行边界限制 |
| 外部负责 | Archive、Core 或仍保留的独立模组负责，不在自家 DLL |
| 部分／不同 | 有相似能力，但遗漏子功能、口径或行为不同 |
| 未实现 | 自家和本次核对的替代功能没有该能力 |
| 接受差异 | 用户明确认可或要求不同，不算待补缺口 |
| 未验证 | 证据不足以判断等价或运行正确，不能列为完成 |

这里不计算“覆盖百分比”：一个普通开关和完整聊天编辑器的成本、重要性不同，直接数行会误导。

## 1. 完整替代范围总表

前 18 项是本次替代讨论涉及的具体模组／直接前身。旧版前身单独注明，避免冒称都被卸载过。

| 模组与核对版本 | 实际替代者 | 整体判断 |
| --- | --- | --- |
| Localia PingEverything 3.0.1 | Core PlayerPingHelper | 扩展标点覆盖；敌人状态／距离差异用户已接受 |
| tru0067 PierceBugFix 1.2.2 | Core BulletPierceFix | 两项核心修复有对应实现，但穿透次数语义不同 |
| Dinorush FlickShotFix 1.0.1 | Archive FlickShotFix | 有对应逐发更新修复；组合补丁仍需实机验收 |
| hirnukuono NoInterruptions 0.1.12 | Archive InteractionFix + 自家资源位置维护 | **仅部分；大多数终端专项修复未被替代** |
| Localia PacksHelper 3.1.4 | Archive L4DStylePacks + 自家 ResourceHud | 补给按键和持包 HUD 分开提供；边缘交互未证明完全等价 |
| Amorously DescriptiveWeaponStatShower 2.0.3 | Archive WeaponStats | **基础数字有覆盖，分页／击杀断点／作者自定义说明等未覆盖** |
| Andocas BetterTextChat 0.6.2 | Archive ChatTweaks | **只覆盖少量输入功能，不是完整聊天替代** |
| Hikaria ItemMarker 1.1.0 | 自家 ItemMarkers | 基础标记与设备覆盖；配置、检查点、ping 去重和部分事件缺口 |
| Localia ResourceHelper 3.0.1 | 自家 ItemMarkers | 资源／消耗品基础覆盖；逐物品外观、焦点表现和检查点缺口 |
| Hikaria ColorGradingHUDInfoPlus 1.0.1 | 自家 ResourceHud，炮塔由 Archive 补充 | 持包上下文覆盖，非完整 HUD 编辑器 |
| easternunit100 ColorGradingHUDInfo 0.0.4 | 同上 | 相关旧版前身；渐变／距离缩放未照搬，不能证明曾从 Temp 卸载 |
| Hikaria DropItemPlus 1.0.1 | 自家 ResourceHandling + PlacementPreview | 主要放回路径已有，原生交互及检查点时序仍需验收 |
| Extra DropItem 0.1.4 | 同上 | 相关前身，基础放回功能已有；未证明曾从 Temp 卸载 |
| Dinorush StatDisplay 1.1.8 | 自家 TeamStatistics + StatisticsDisplay | 核心 Hit／Crit／伤害覆盖，逐武器／格式引擎／EWC 等未覆盖 |
| Localia PerfectBoosters 3.0.1 | 自家 BoosterTweaks | 完美数值及不消耗覆盖；旧模板与关闭后还原策略不同 |
| Hikaria Booster_Tweaker 1.2.6 | 自家 BoosterTweaks | **只覆盖选定的三个功能，不含完整自定义编辑器** |
| Hikaria BoosterFarmer 1.0.0 | 自家 BoosterTweaks | 改为实际收益倍增，不等同于原版固定伪造拾取数量 |
| Hikaria ResourceStack 2.1.1 | Core ResourceStack | 按用户要求交给 Core，自家已移除重复实现 |

另见第 10 节的原有小模组对照、第 11 节统计相关参考，以及第 12 节仍保留的游戏修复。不能把整个缓存目录都当作待替代清单。

## 2. 标点、穿透、甩枪、交互与补给按键

### PingEverything → Core PlayerPingHelper

| 原版功能／规则 | 当前实现 | 状态 |
| --- | --- | --- |
| 中键扩大可标点对象 | Core 扩大目标层并为命中对象建立 PlayerPingTarget | 外部负责 |
| 敌人可标点 | Core 给敌人碰撞体添加敌人标记目标 | 外部负责 |
| 只允许 12 米内睡眠敌人，不标清醒／隐形敌人 | Core 当前代码未复刻这些限制，补充目标射线使用 40 米 | **接受差异：用户 2026-09-08 明确认可** |
| 客户端标点，不要求全队安装相同插件 | Core 使用原生标点链；自家持续发现同步另有匹配客户端协议 | 不同职责，不能混算 |
| 标点相关弱点／异常目标修正 | Core 用另一套目标附加方式；未逐个敌人验证 | 未验证 |
| 通讯菜单打开时避免错误处理中键 | 未证明 Core 与 PingEverything 的该防护等价 | 未验证 |
| 发现后长期资源记忆 | 不是 PingEverything 的基础职责；由自家 ItemMarkers 提供 | 自家覆盖，非原版功能搬运 |

证据：PingEverything 缓存 README；Core 的 PlayerPingHelper 四组补丁。用户接受敌人限制差异不等于自动接受其他未验证项。

### PierceBugFix → Core BulletPierceFix

| 原版功能／规则 | 当前实现 | 状态 |
| --- | --- | --- |
| 移除射线最多检查五个碰撞体的硬限制 | Core 重写枪械／霰弹 Fire，按射程与穿透条件继续射线 | 外部负责 |
| 同一发穿透后不重新随机霰弹散布 | Core 在初次 CastWeaponRay 后清零随机 spread／偏移 | 外部负责 |
| 遵守原游戏“最大命中数”口径 | Core 说明明确改为“可穿透数量”，计数起点也不同，存在额外命中一个目标的语义差异 | 部分／不同；不是纯等价 bugfix |
| 与统计收集器同时使用 | Core 替换 Fire，自家也挂接 Fire／射线／伤害 | 加载已确认，准确率与穿透组合需实测 |

证据：[PierceBugFix 源码](https://github.com/tru0067/GTFO-PierceBugFix)、缓存 1.2.2 README、Core BulletPierceFix 的实际 Fire／CastWeaponRay 实现。

### FlickShotFix → Archive FlickShotFix

| 原版功能／规则 | 当前实现 | 状态 |
| --- | --- | --- |
| 每次发射更新瞄准射线，避免原生约 30 Hz 更新造成甩枪错位 | Archive 有对应 BulletWeapon.Fire 和 Shotgun.Fire 补丁，日志确认挂接 | 外部负责 |
| 高频武器同一更新周期内多发也更新方向 | 对应逐发路径存在；实际与 Core 替换 Fire 的组合仍需验证 | 外部负责／未实测 |
| 改变后坐力、散布、伤害 | 不是此修复的目标；自家后坐力功能属于独立玩法修改 | 不混算 |

证据：[FlickShotFix](https://github.com/Dinorush/FlickShotFix)、缓存 README、Temp 启动补丁日志。

### NoInterruptions → **没有完整替代**

| 原版能力 | 当前替代情况 | 状态 |
| --- | --- | --- |
| 防止很多交互被其他交互／玩家遮挡打断 | Archive InteractionFix 只找到以资源包为中心的交互射线处理 | 部分 |
| 使用资源包时不被柜子等世界交互抢走 | Archive ResourcePackFirstPerson.Update／OnUnWield 等路径负责 | 外部负责 |
| 离开终端时仍能输入字符的问题 | 没有找到对应替代终端补丁 | 未实现 |
| 快速离开终端导致字符不同步 | 同上 | 未实现 |
| 客户端进入终端后受网络延迟影响，不能立即打字 | 同上 | 未实现 |
| 上一条命令运行中又执行下一条命令 | 同上 | 未实现 |
| 终端进入不可用状态 | 同上 | 未实现 |
| 队友靠近／经过使玩家卡在终端 | 自家“终端被发现”监听不是交互修复 | 未实现 |
| 移动资源后终端仍报告旧位置 | 自家维护放置节点、容器关联；所有搬运来源尚未逐项确认 | 部分 |

结论：此前仅凭 InteractionFix 名称就认为可以完整删除 NoInterruptions 的依据不足。这里指出缺口，**本轮不自动恢复安装或修改代码**。

证据：NoInterruptions 0.1.12 作者 README；Archive InteractionFix 实际补丁；自家 ResourceHandling.cs。

### PacksHelper → Archive 按键 + 自家 HUD

| 原版能力 | 当前替代情况 | 状态 |
| --- | --- | --- |
| 按住左键给自己补给 | Archive L4DStylePacks | 外部负责 |
| 按住右键给队友补给 | Archive L4DStylePacks | 外部负责 |
| 保留 E 键补给 | 仍使用原生补给交互 | 外部负责 |
| 持有哪种包就显示对应信息 | 自家医疗→血量、弹药→主副武器、工具→工具、消毒→感染 | 自家覆盖 |
| 持包时队友信息强制可见并放大处理 | 自家控制原生额外信息及强调字号；遮挡、离屏边界不保证逐像素等价 | 部分 |
| 靠近队友时补给提示修正 | 未单独复刻原版历史修正；需测原生＋Archive 组合 | 未验证 |
| 某些状态下左键无法补给的修正 | 找到按键替代不等于重现所有旧版修复场景 | 未验证 |

## 3. 武器说明页与聊天：不是战斗统计 HUD

### DescriptiveWeaponStatShower → Archive WeaponStats

自家 Hit／Crit 统计不能替代此模组。这里的“属性”指选装备时的参数和说明，而不是本局打中了多少。

| 原版能力 | Archive／自家当前情况 | 状态 |
| --- | --- | --- |
| 按当前武器数据生成基础参数 | Archive WeaponStats 读取 Archetype／Item／Player 数据 | 外部负责 |
| 基础伤害 | Archive Damage | 外部负责 |
| 霰弹单丸伤害 × 弹丸数 | Archive 分开显示 Damage、ShotgunPellets，而非同一格式 | 外部负责，表现不同 |
| 弹匣容量 | Archive Clip | 外部负责 |
| 总弹药，包含弹匣 | Archive GetTotalAmmo，炮塔另算 | 外部负责 |
| 换弹时间 | Archive Reload | 外部负责 |
| 精准／弱点倍率 | Archive Precision 可选；默认初始化列表不含该项 | 外部能力，当前展示未确认 |
| 伤害衰减起点 | Archive FalloffStart | 外部负责 |
| 腰射散布 HIP | Archive 当前 Stats 枚举没有对应项 | 未实现 |
| 开镜散布 ADS | Archive 当前 Stats 枚举没有对应项 | 未实现 |
| 霰弹散布 | Archive ShotgunSpread 可选 | 外部能力，默认不展示 |
| 硬直倍率 | Archive Stagger | 外部负责 |
| 穿透数 | Archive Pierce 展示数据值；Core 改了实际语义，需统一解释 | 部分／不同 |
| 连发模式与每组发数 | Archive 有 Burst 数量，不是原版完整模式说明格式 | 部分 |
| 射速 | Archive 用 RPM，原版用每秒子弹数；burst 计算实现不能直接等同 | 部分／不同 |
| 蓄力时间 | Archive ChargeupTime 可选 | 外部能力，默认不展示 |
| 点击描述在原文／参数／额外说明间切页 | Archive 直接拼接描述，没有此分页系统 | 未实现 |
| 自定义武器变化后更新显示 | Archive 读当前数据；不保证理解 EWC 等运行时额外行为 | 部分 |
| 一枪击杀断点：胸、背、头、后脑 | Archive 没有敌人击杀计算器 | 未实现 |
| 可选择要显示断点的敌人 | 无对应配置 | 未实现 |
| 断点计算含敌人装甲／倍率、全弹丸命中假设 | 无对应计算 | 未实现 |
| 英文／中文显示 | Archive 自己的本地化系统；不是原版语言配置 | 外部负责 |
| 配置修改后重开装备页刷新 | Archive 有自己的设置系统；没有 DWSS 全套 LiveEdit 文件协议 | 部分 |
| MTFO 自定义说明页 | 无对应系统 | 未实现 |
| Global.json：默认说明索引、作者偏好隐藏参数、用户覆盖 | 无对应系统 | 未实现 |
| ExtraDescriptions：按 ArchetypeID／GearCategoryID 匹配 | 无对应系统 | 未实现 |
| 自定义标题、多个正文、说明索引覆盖 | 无对应系统 | 未实现 |
| 近战轻击／蓄力伤害、硬直、精准、睡眠倍率、环境倍率、蓄力时奔跑、满伤蓄力时间 | Archive 额外提供近战参数集合，代码对 R6+ 分支处理 | 外部新增，不能抵消上述缺口 |
| 衰减终点、数值舍入、隐藏原始说明、穿透文字删除线 | Archive 自己的额外设置 | 外部新增 |

当前磁盘 WeaponStats 设置含空列表及 IsFirstTime=true；**不能据此断言它什么都不显示**，因为 Init 会填默认列表。是否已在当前运行进程初始化、用户选择了哪些可选项，应看实际界面；本报告没有伪造运行时值。

证据：[DWSS 作者仓库](https://github.com/Amorously/GTFO-WeaponStatShower)、缓存 2.0.3 完整 README；Archive WeaponStats、WeaponStatsSettings、GetFormatedWeaponStats。

### BetterTextChat → Archive ChatTweaks

| 原版能力 | 当前替代情况 | 状态 |
| --- | --- | --- |
| 超过原生 50 字后自动换行发送 | Archive 粘贴明确截到 50 字 | 未实现 |
| Shift+Enter 手动换行 | 没有对应多行编辑系统 | 未实现 |
| Home／End／左右方向键移动光标 | 没有对应光标编辑实现 | 未实现 |
| Ctrl+C／Ctrl+V | Archive 提供复制粘贴，但受长度限制 | 外部负责，部分 |
| 允许 <、>、= 等原版限制字符／富文本 | Archive RemoveChatRestrictions 是另一个功能，当前完整 GUID 开关为 false | 当前不等价 |
| F1–F4 快速插入对应玩家前缀 | 未找到对应替代 | 未实现 |
| LobbyExpansion 的 F5–F8 玩家前缀 | 未找到对应替代 | 未实现 |
| 鼠标滚轮浏览已接收聊天记录 | ChatTweaks 上下键只翻最近十条自己发出的消息 | 未实现 |
| 跨关卡／大厅保留接收历史 | 没有对应历史保留系统 | 未实现 |
| 对方正在输入提示，双方装 BTC 时可见 | 没有相同协议／界面 | 未实现 |
| 可选时间戳 | 没有对应设置 | 未实现 |
| 语音 PTT 声音与多个说话人相关问题修正 | 没有找到替代补丁 | 未实现 |
| 长玩家名字的字号问题修正 | 没有找到聊天专用替代 | 未实现 |
| 上下键调用最近自己发送的消息，并保留未发草稿 | Archive 的实际能力 | 外部新增，不等于接收历史 |
| 聊天时不隐藏导航标记 | Archive NoNavMarkerHideInChat 已启用 | 外部新增，不等于聊天编辑器 |

证据：BetterTextChat 0.6.2 README；Archive ChatTweaks 的输入、PostMessage 和长度处理；完整 GUID 的 EnabledFeatures 配置。

## 4. ItemMarker 与 ResourceHelper：发现、生命周期、外观

以下两表把重叠能力合并比较，但逐行注明来源。IM = Hikaria ItemMarker 1.1.0；RH = Localia ResourceHelper 3.0.1。

### 标记对象、发现和生命周期

| 能力 | 原版来源 | 自家 2.2.9 | 状态 |
| --- | --- | --- | --- |
| 医疗包、弹药包、工具包、消毒包 | IM／RH | 独立类别、原生图标、数量、名称、距离 | 自家覆盖 |
| 消耗品，包括不同道具而非仅资源包 | IM／RH | 按真实 Consumable 槽位分类，不靠名字白名单 | 自家覆盖 |
| 消耗品剩余数量变动 | IM／RH | 2.2.9 接入 OnCustomDataUpdated，有限次数耗尽过滤 | 自家覆盖；未部署 |
| 任务搬运物、cryo、电池等 | IM | 原生搬运状态；队友携带时跟随，放下重新锚定 | 自家覆盖；实机待验 |
| 钥匙／其他命名关键物品 | IM | Objective／Other 等类别；依赖原生物品／终端实体 | 自家覆盖，未穷举自定义道具 |
| 普通计算机终端 | IM | 专用 terminalItem、屏幕／交互锚点及 Setup 注册 | 自家覆盖 |
| 发电机 | IM | 插入交互状态决定是否仍需标记 | 自家覆盖 |
| 消毒站 | IM | 可用交互检查 | 自家覆盖 |
| HSU 取样装置 | IM | 真实取样交互状态 | 自家覆盖 |
| HSU 激活／插入装置 | IM | 真实插入交互状态 | 自家覆盖 |
| 舱门控制器 | IM | 真实设备状态 | 自家覆盖 |
| 安全门锁／缺失钥匙、电池、控制器要求 | IM | 锁交互面板锚点与缺失要求文本 | 自家覆盖 |
| 运行中／自定义 geomorph 新建上述各种设备 | IM 对各设备有专用 Setup 钩子 | 自家计算机有 Setup，其他多数专用设备映射在重建阶段生成 | 部分；动态设备缺口 |
| 反应堆终端补登记到区域终端列表 | IM Reactor OnBuildDone | 没有同等补登记修复 | 未实现 |
| 自己发现物品 | IM／RH | 4 米、同维度、视线判定 | 自家覆盖，判定不同 |
| 队友发现物品 | IM／RH | 对活队友附近检查；匹配自家客户端分享精确物品 ID | 自家覆盖；协议不同 |
| 开箱后直接把箱内所有物品标为已发现 | IM 的箱体打开事件 | 自家要求内容物满足发现／视线条件，不因打开就全部记忆 | 部分／不同 |
| 不揭示未开容器内部 | 发现逻辑边界 | 自家明确排除未发现内容 | 自家覆盖 |
| 玩家主动 ping 显示／延长物品可见 | IM／RH | 精确目标持久记忆，至少 60 米显式范围 | 自家覆盖，时限不同 |
| 对 ping 位置附近多个资源一起延长范围 | RH 按附近坐标查找 | 自家只认精确目标，不猜附近多个对象 | 部分／不同 |
| 终端 ping 强调物品 | IM | 自家接原生终端 ping／同步事件 | 自家覆盖，强调表现不同 |
| 终端 QUERY 发现 | 自家扩展 | 获取详细信息时记忆对应终端实体 | 自家新增 |
| 队友使用终端也记忆那个终端 | 自家需求 | 监听终端原生同步状态 | 自家覆盖 |
| 显示“哪个玩家正在终端操作”的专用状态标记 | 不能从终端被发现推导出此能力 | 现有监听只负责记忆终端，没有独立持续操作员标签 | 未实现／需单列需求 |
| 资源被自己／队友拿起后移除 | IM／RH | 拾取同步对象身份清理 + OnPickedUp | 自家覆盖；2.2.8 修正，未部署 |
| 掉落／放回后重新标记 | IM／RH | 原生物品登记及重新发现路径 | 自家覆盖；主客机待验 |
| 物品耗尽／销毁后清理 | IM／RH | 耗尽、同步状态、对象失效处理 | 自家覆盖 |
| 已完成的设备隐藏 | IM | 专用设备可用性检查 | 自家覆盖；自定义设备待验 |
| cryo／电池跟随队友携带者并显示携带者名字 | 用户明确要求的增强 | 已有；自己携带时不增加遮挡图标 | 自家覆盖，非声称上游默认 |
| 完成目标后隐藏搬运物 | IM 的状态过滤／自家需求 | 原生完成状态处理 | 自家覆盖 |
| 搬运物原生持久图标与自家图标去重 | IM | 2.2.8 MarkerOwnership 只抑制同物体的原生 placer | 自家覆盖；未部署 |
| 普通玩家临时 ping 图标与持久图标去重 | IM 拦截 SyncedNavMarkerWrapper 并保留音效等 | 自家只有接收发现，没有同等临时 ping 抑制 | **未实现；仍可能双图标／双距离** |
| 检查点保存／恢复已发现、可见状态 | IM 状态缓冲；RH 位置记忆 | 自家检查点重开清空，之后重新发现 | 未实现 |
| 当前维度之外隐藏 | IM／RH | 同维度筛选 | 自家覆盖 |
| 柜／箱体自身的持久标记 | 用户明确禁止 | 自家已排除，仍可标已发现内容 | 接受差异；2.2.8 起 |
| 开发／全图揭示模式 | IM 有调试式揭示能力 | 不加入未发现全图透视 | 接受差异，遵循现有范围 |
| 手动清除所有／准星附近一个标记 | 自家扩展 | F8／Shift+F8 | 自家新增 |

### 配置、图标、文本和可见性

| 能力 | 原版来源 | 自家 2.2.9 | 状态 |
| --- | --- | --- | --- |
| 原生分类图标 | IM／RH | 14 类原生图标；医疗／弹药／工具不再被通用 Loot 覆盖 | 自家覆盖；2.2.8 起 |
| 每种自定义物品各自 PNG 图标 | IM、RH 扩展资源配置 | 自家消耗品统一类别图标，没有逐物品 PNG 编辑器 | 未实现 |
| 每类颜色 | IM／RH 有更细粒度配置 | 自家分类 RGB 颜色 | 自家覆盖 |
| 每类距离，资源和杂物不同 | RH／IM | 资源默认 35 米，消耗品 12 米，其他分类另有默认值 | 自家覆盖 |
| 单个 ItemID 的名称／颜色／范围／忽略开关 | IM | 自家按大类设置，不是逐物品定义表 | 部分 |
| 自定义显示名／终端键／公共名之间选择 | IM；RH 可改名 | 自家使用原生名称，没有同等逐物品选择器 | 部分 |
| 自定义数量除数、设 0 不显示数量 | RH 的自定义资源参数 | 自家按实际资源／消耗品规则换算，没有作者逐条覆盖 | 部分 |
| 杂物／黄红橙等荧光棒逐种配置 | RH | 同属消耗品，名称／数量覆盖，不同图标和颜色未逐种设置 | 部分 |
| 按世界距离／CourseNode／区域／维度等模式控制可见 | IM | 自家主要是世界距离 + 同维度，没有完整模式选择 | 部分 |
| CourseNode 距离设置 | IM | 没有独立图距离参数 | 未实现 |
| 常规透明度 | IM／RH | 自家全局 MarkerOpacity，非每物品 | 部分 |
| ADS 透明度 | IM／RH 可淡化 | 自家持久物品标记默认 ADS 隐藏，队友 HUD 另行淡化 | 部分／不同 |
| 近处／准星目标清晰，远处只留图标 | IM／RH 标题／距离与焦点显示 | 自家名称和距离常显，没有完整焦点折叠规则 | 未实现 |
| AlwaysShowTitle／AlwaysShowDistance 独立开关 | IM | 无同等设置 | 未实现 |
| 焦点字号和距离缩放 | RH | 自家全局缩放，不是同等动态算法 | 部分 |
| 出现时淡入、ping 强调动画 | IM／RH | 无完整动画实现 | 未实现 |
| 玩家 ping／终端 ping 分别配置消退时间 | IM 默认约 12／18 秒；RH 约 15 秒 | 自家扩大范围持续到清除，不是定时消退 | 部分／不同 |
| 按精确对象维护标记 | 两者实现不同 | 自家事件登记 + 4 Hz 附近视线检查，避免高频全场扫描 | 自家设计改进；没有性能对比实测 |

证据：[ItemMarker 作者仓库](https://github.com/Mamizu1028/GTFO_ItemMarker)、[ResourceHelper 作者仓库](https://github.com/GTFO-Modding/ResourceHelper)；缓存类型 ItemMarker、ItemMarkerBase、ItemInLevel_Marker、ItemInLevelMarkerDefinition、各 LG_*_Marker、Res_Manager、Res_Moniter；自家 ItemMarkers.cs、MarkerRules.cs、MarkerVisuals.cs、MarkerOwnership.cs、ResourceHandling.cs。

## 5. 队友资源 HUD：HUDInfoPlus／旧 ColorGradingHUDInfo

| 原版能力 | 自家／现有组合 | 状态 |
| --- | --- | --- |
| 血量显示 | 持医疗包时显示并强调 | 自家覆盖 |
| 感染显示 | 持消毒包时显示并强调 | 自家覆盖 |
| 主武器／副武器弹量 | 持弹药包时分别显示并强调 | 自家覆盖 |
| 工具百分比 | 持工具补给包时显示并强调 | 自家覆盖 |
| 持包类别相关内容放大 | 默认 1.2 倍，可加粗／着色 | 自家覆盖 |
| 不持包时常驻所有资源数字 | 用户要求正常原生 HUD、不常驻额外百分比 | 接受差异 |
| 低血／低弹量着色 | 自家阈值式短缺颜色，不是原版完整连续渐变 | 部分 |
| 血量颜色按感染后的可恢复上限计算 | 未使用同等 1−infection 上限渐变算法 | 未实现 |
| 医疗包上下文同时展示感染 | 自家采用医疗→血量、消毒→感染的对应关系 | 不同；不能声称照搬 |
| 玩家所带资源包显示使用次数 | 自家只在持包上下文显示非空包 | 自家覆盖，显示时机不同 |
| 玩家所带消耗品显示个数／次数 | 自家同上 | 自家覆盖 |
| 隐藏空槽／空物品 | 自家空槽、空包过滤 | 自家覆盖；2.2.8 补齐 |
| 无限弹量符号 | 按原生 GUI 无限标志显示 ∞ | 自家覆盖；2.2.8 补齐 |
| 已部署哨戒炮使用真实炮塔弹量 | 自家读已部署炮塔，而非只读背包 | 自家覆盖 |
| 自由选择显示哪些槽位 | 原版 ShowSlots；自家只有对应包上下文和次数开关 | 部分 |
| 自由常显／自动隐藏／自动放大开关组合 | 自家只提供所选的上下文行为和有限设置 | 部分 |
| 瞄准透明 | 自家 AimOpacity，倒地标记不淡化 | 自家覆盖，算法不完全同等 |
| 准星角度动态透明度 | 自家外围淡化；原版角度范围、最小值不同 | 自家覆盖，参数不同 |
| 根据距离动态调整队友字号 | 自家固定强调倍率，没有原版距离算法 | 未实现 |
| 工具／装备详细名称展示编辑 | 没有原版全套槽位名称格式 | 部分 |
| 旧 HUD 的血量下划线／已部署工具灰化等视觉规则 | 未完整照搬 | 未实现 |
| 关闭资源 HUD 后恢复原生额外信息，包括切到包 | 自家显式恢复所拥有的可见性、文字与透明度 | 自家修正，仍需切换验收 |
| 炮塔世界位置标记／主人颜色 | Archive SentryMarkerTweaks，当前已启用 | 外部负责 |
| 炮塔上显示类型与实时弹量 | Archive 当前 ShowSentryArchetype=true、ShowSentryAmmoPercentage=true | 外部负责 |
| 炮塔上显示主人名字 | Archive 有能力，当前 DisplayPlayerNameAboveMarker=false | 外部能力，当前未启用 |
| 分别控制自己／队友／机器人炮塔标记 | Archive 三个开关当前全 true | 外部负责 |
| 旧 HUD 自定义炮塔定位图形／信标与 Archive 逐像素一致 | 没有此验证；Archive 用自己的原生标记表现 | 部分／不同 |

证据：[HUDInfoPlus 作者仓库](https://github.com/Mamizu1028/GTFO_CGHUDInfo)、缓存 CGHUDInfoSettings／UpdateExtraInfo 补丁／PlayerHudDistanceModifier、旧 HUD README；自家 ResourceHud.cs；Archive SentryMarkerTweaks 源码与实际 JSON 设置。

## 6. DropItemPlus／DropItem：地面、容器、模型与同步

| 原版能力／相关需求 | 自家 2.2.9 | 状态 |
| --- | --- | --- |
| 把资源放回柜／箱空槽 | 对打开容器的匹配空槽按原生 Use 键，保持 0.35 秒 | 自家覆盖 |
| 消耗品也能放回 | 支持资源包和 Consumable | 自家覆盖 |
| 不往已有物品的位置覆盖 | 空槽和占用检查，主机验证 | 自家覆盖 |
| 当前手持物品与槽位是否允许放置 | 自家资源／消耗品槽匹配；所有自定义类型未验 | 自家覆盖，边界待验 |
| 空槽原生交互组件和提示 | 原版给空槽增加交互对象；自家使用自己的选槽流程及原生本地化提示 | 部分／不同 |
| 位置容易瞄准 | 自家空槽吸附，不再要求对准小加号 | 自家增强，手感待验 |
| 放置前可见物品模型预览 | 自家缓存原生物品网格并显示半透明预览 | 自家覆盖，视觉待验 |
| 放置后保留真实模型，不只改变数据位置 | 原生放置请求 + 容器关联 + 剔除／阴影缓存刷新 | 自家覆盖，主客机视觉待验 |
| 保留物品身份和剩余使用次数 | 移动原物品，不凭空创建替代品 | 自家覆盖 |
| 保持终端节点／位置一致 | 维护 SpawnNode／CourseNode／容器关系 | 自家覆盖，跨来源待验 |
| 容器生成及检查点回忆后重新挂接槽交互 | 原版有专用 Recall 注册；自家重建及原生调用不同 | 部分／检查点待验 |
| 丢到地面 | 自家默认 G，向可达实体地面投放资源或消耗品 | 自家新增，不冒称原版全部如此 |
| 武器／工具和任务搬运物 | 自家不允许丢枪／工具，任务搬运保持原生操作 | 明确范围，不算资源放回缺陷 |
| 与 Core ResourceStack 共存 | 自家不再实现堆叠，放回与合并分工 | 外部负责堆叠；组合待验 |
| 放置后持久标记更新 | 自家标记读取原物品的新状态 | 自家覆盖，含消耗品现场待验 |

证据：[DropItemPlus 作者仓库](https://github.com/Mamizu1028/GTFO_DropItem)、[DropItem 前身](https://github.com/GTFO-Modding/DropItem)；DropItemPlus 1.0.1 Feature 的槽位／交互／渲染／Recall 补丁；自家 ResourceHandling.cs、PlacementPreview.cs。

## 7. StatDisplay：计算口径、界面、网络、结算

| StatDisplay 1.1.8 能力 | 自家 2.2.9 | 状态 |
| --- | --- | --- |
| 命中率：命中弹丸 ÷ 发射弹丸 | 同口径；霰弹按丸数 | 自家覆盖 |
| 弱点率：弱点命中 ÷ 命中 | 用户明确选择 Dinorush 口径；不是仅头部 | 自家覆盖；2.2.8 校正 |
| 一发穿透多目标不重复增加“命中弹丸” | 自家按发射／射线归属去重 | 自家覆盖；与 Core 同时使用待验 |
| 穿透率：所有命中 ÷ 命中弹丸 | 没有独立展示／格式 token | 未实现 |
| 总伤害 | 自家主机有效生命损失，排除超额伤害 | 自家覆盖，严格主机口径 |
| 弱点伤害及其独立颜色 | 没有独立伤害分类展示 | 未实现 |
| 按主武器／副武器分别统计 | 当前只给玩家汇总行 | 未实现 |
| 近战／工具分别统计伤害 | 可进入总伤害，不提供分槽输出 | 部分 |
| All／Primary(Main)／Secondary(Special)／Melee／Tool(Class) token | 没有格式解析引擎 | 未实现 |
| Shot：单个子弹／霰弹丸口径 | 自家固定采用该准确率口径 | 自家覆盖 |
| Group：一次扣扳机／一组霰弹口径 | 无可选 Group 统计 | 未实现 |
| Full：所有穿透命中／EWC 伤害效果口径 | 无可选 Full 统计 | 未实现 |
| Hit／Crit／Fired 原始计数和自由组合比例 | 内部有必要计数，用户界面不支持任意组合 | 部分 |
| 玩家色 RED／GRE／BLU／PUR 简称 | 使用原生玩家颜色和简称 | 自家覆盖 |
| 显示完整玩家名字或简称可选 | HUD 固定简称，结算用名字，无同等选择器 | 部分 |
| 紧凑数字／斜线／括号伤害样式 | 2.2.8 改用此布局方向，无独立大面板 | 自家覆盖；实际美观和位置待验 |
| HUD 横纵偏移 | 有 OffsetX／OffsetY，Temp OffsetX=100 保留 | 自家覆盖 |
| 字体整体缩放 | 有 TextScale | 自家能力 |
| 五种统计分别自定义颜色 | 自家固定命中灰、弱点黄等配色 | 未实现 |
| HUD 自定义格式 | 无模板／token 系统 | 未实现 |
| 结算独立预设和独立自定义格式 | 只有显示开关和固定汇总文本 | 部分 |
| 原生装备／逐武器结算信息 | 自家没有逐武器结算插入 | **未实现** |
| 原生成功页统计 | 在原生玩家报告下增加紧凑文字，不替换奖励面板 | 自家覆盖，布局不同 |
| 原生失败页统计 | 在原生失败信息下增加玩家汇总 | 自家覆盖，布局不同 |
| 自己／团队显示开关 | 有 | 自家覆盖 |
| 已安装玩家互相发送准确率 | 自家 v3 协议，只认匹配 Infini Tweaks 客户端 | 自家覆盖，协议不同 |
| 与原版 StatDisplay 客户端互通 | 不兼容原版协议 | 未实现 |
| 伤害由主机提供 | 自家要求主机安装；缺失为破折号 | 自家覆盖，严格策略 |
| 未安装模组主机时客户端估算伤害 | 原版存在估算及超额伤害限制；自家不伪造估计 | 不同；不是等价实现 |
| EWC 集成，额外武器伤害／射击效果适配 | 没有 EWCWrapper 对应适配层 | 未实现 |
| Archive 内设置与独立配置文件两种入口 | 自家用 BepInEx 配置，不注册原版 Archive 设置页 | 部分 |
| 配置热重载行为 | 自家现有设置读取不等于原版整套文件监听器 | 未证明等价 |
| 检查点／重开统计生命周期 | 自家重开归零；没有完整复刻原版的检查点统计流程 | 部分／不同；原版所有边界未穷举 |
| 玩家重连、统计包迟到 | 自家有 session／generation／peer 校验 | 自家实现；不同协议，不能用原版测试结果替代验收 |

证据：StatDisplay 缓存 1.1.8 的 VanillaConfiguration、StatParser、StatHandler、Data、EWCWrapper；自家 TeamStatistics.cs、StatisticsRules.cs、StatisticsSync.cs、StatisticsDisplay.cs、StatisticsSettings.cs。之前“大面板已去掉”不等于“Dinorush 全功能已移植”。

## 8. Booster 三个模组：不是同一种 Farmer

### PerfectBoosters 3.0.1

| 原版能力 | 自家 2.2.9 | 状态 |
| --- | --- | --- |
| 现有增幅剂效果调到最佳值 | 按精确模板与效果槽处理 | 自家覆盖 |
| 增幅剂不消耗 | 本地消耗和会话提交路径保护 | 自家覆盖，实机待验 |
| 旧 rundown 的部分增幅剂不优化但仍不消耗 | 自家增加已知历史模板表，无法匹配则不改且写警告 | 自家扩展，**不是所有历史模板保证覆盖** |
| 关闭后回到原值 | 自家没有完整原值缓存和热关闭回滚系统 | 未实现同等热还原；不能承诺开关一关即恢复 |
| 保留已有条件／词条 | 自家保留；“完美”不代表无条件 | 自家明确规则 |

### Booster Tweaker 1.2.6

| 原版能力 | 自家 2.2.9 | 状态 |
| --- | --- | --- |
| 完美强化剂 | 现有模板最佳数值 | 自家覆盖，模板边界不同 |
| 强化剂不消耗 | 独立于完美数值开关 | 自家覆盖 |
| Farmer 刷取强化剂 | 自家对真实赚取的 artifact currency 乘倍率 | 部分／不同 |
| Farmer 把奖励设为 100000 的路径 | 自家不采用固定高额，默认 ×5，零仍为零 | 未实现原版模式 |
| 基于模板创建自定义强化剂 | 没有创建编辑器 | 未实现 |
| 自定义条目名称、类别、模板 ID／索引 | 没有对应编辑器 | 未实现 |
| 选择效果组 | 保留现有合法效果，不让用户选组 | 未实现 |
| 选择条件组 | 保留原条件，不让用户选组 | 未实现 |
| 从背包生成自定义配置 | 没有对应操作 | 未实现 |
| 加载自定义设置并应用 | 没有原版 JSON manager／应用按钮 | 未实现 |
| 自定义完美增幅剂管理器 | 没有对应管理器 | 未实现 |
| 自定义普通增幅剂管理器 | 没有对应管理器 | 未实现 |
| 无条件 | 不删除原条件 | 未实现 |
| 无负面效果 | 不删除原负面 | 未实现 |
| 正面效果额外增幅 | 只在模板合法最佳值内处理，不额外任意增幅 | 未实现 |
| 任意自定义效果数值 | 没有对应功能 | 未实现 |
| Archive 设置页入口 | 自家不注册 Booster Tweaker 的页面 | 未实现同等入口 |

### BoosterFarmer 1.0.0

| 原版能力 | 自家 2.2.9 | 状态 |
| --- | --- | --- |
| enableBoosterFarming 启用后 ArtifactInventory.GetArtifactCount 返回 1000 | 自家没有伪造该计数 | 不同 |
| 提高实际关卡收益 | 自家乘 1–100 倍、向下取整、乘后封顶 100000，不降低原生已高于上限的值 | 自家替代方案，不等价 |

**必须区分：BoosterFarmer 固定 1000 的拾取计数、Booster Tweaker 固定 100000 的奖励路径、自家 ×5 正常收益，是三种不同机制。** 不能都用“Farmer 已支持”一笔带过。

自家的额外保护：无法精确匹配模板时明确日志；记录检查／改动／消耗拦截／奖励；有意丢弃仍允许。此类保护有价值，但不填补自定义编辑器缺口。

证据：[Booster Tweaker 作者仓库](https://github.com/Mamizu1028/GTFO_BoosterTweaker)、缓存 PerfectBoosters README、BoosterFarmerPatch、PerfectBooster／CustomBooster／配置 manager；自家 BoosterTweaks.cs、BoosterRollRules.cs、HistoricalBoosterTemplates.cs。

## 9. ResourceStack：按要求留给 Core

| ResourceStack 能力 | 当前负责者 | 状态 |
| --- | --- | --- |
| 同类资源包合并 | Core ResourceStack | 外部负责 |
| 同类消耗品合并 | Core ResourceStack | 外部负责 |
| 资源容量最多 5 次 | Core | 外部负责 |
| 消耗品使用原生容量限制 | Core | 外部负责 |
| 溢出部分保留 | Core | 外部负责 |
| 主机执行、为房间玩家处理 | Core，不能把客户端开关当成主机已提供 | 外部负责，主客机待验 |
| 自家重复堆叠算法 | 已按用户要求删除 | 接受差异，不需要重新实现 |

Core 完整 GUID 开关为 true，启动日志确认拾取交互补丁挂接。挂接不是所有物品合并场景的实际验收。

## 10. 自家原有功能与其他小模组

这些是既有功能的重叠／历史参考，和上面新增的 HUD／统计替代分别列出。缓存存在不证明卸载历史。

| 模组与版本 | 原模组可确认的功能 | 自家对应与边界 |
| --- | --- | --- |
| GTFOModding BetterMaps 1.1.0 | 翻正反向门图标 | BetterMaps.cs 有对应门方向处理 |
| 同上 | 减少地图模糊 | 有对应处理 |
| 同上 | 去掉多数不可达区域 | 有可达／导航区域过滤；自定义几何仍需实测 |
| 同上 | 柜／终端等重要图标置于梯子／标牌上方 | 有图标层序处理；这不等于给柜体加世界持久标记 |
| Localia TeammatesIgnoreBullets 3.0.0 | 自己射出的子弹不伤队友，别人仍可能伤你 | 自家出射友伤保护；不是全房间免疫，也不是机器人的所有伤害 |
| Untilted AimPunchAdjustment 1.0.2 | 受击视角偏移倍率 | 自家可调；允许范围／细节不承诺完全相同 |
| Caera R6_HELGun 0.0.3 | 恢复 R6 HEL Gun 参数 | 自家特定原版数据预设；不是所有 rundown／自定义 HEL 架构都强制回滚 |
| hirnukuono MakeSniperGreatAgain 0.0.3 | 弹匣 3、弹药成本 17.5、射击间隔 0.5、腰射散布 3 | **不是完全相同。** Temp 选 OriginalR6：3／15／0.5／3；自家 R8Current：2／17.5／0.8／13。没有一个现有预设等于原模组四个字段的组合；自家还设置了其他武器字段 |
| hirnukuono NoSway 0.0.1 | 手电方向不晃动 | 自家本地第一人称 NoSway |
| Andocas ActuallyNoSway 0.1.0 | 方向固定，同时让光源原点脱离武器，避免换弹／换枪光束变形 | **部分。** 自家只把光线旋转到摄像机前方 6 米目标，没有移动光源原点或解除武器父级；原版解决的原点晃动／光束变形不能宣称已覆盖 |
| NAinfini InfiniFlashlight 1.0.0 | 射程增量、角度增量、无晃动 | 已归入自家统一插件 |
| 同上 | 保留原生开关、状态、颜色、强度、cookie、敌人检测及网络 | 自家保持原生系统职责，不强制常亮 |

自家其他现有能力包括后坐力幅度、体力／近战／跳跃成本调整以及性能诊断。这些不自动等于缓存中的任意武器、体力或调试模组，也没有证据把那些包都列为待卸载目标。

### 性能日志能力边界

性能诊断是自家新增，不是替代某个 HUD 模组。它记录帧时间分布、可取得的 CPU／GPU 时序、内存／GC、Unity sampler、自家热点、插件与 Harmony 所有者、场景类别／网格／材质／纹理等压力信息，并可尝试有界原生 Profiler 捕获。

**它不能保证逐个物体、皮肤、第三方补丁、每个 draw call 的真实 GPU 毫秒开销。** 零售 Unity 没有暴露的计数必须标不可用，物体数／顶点数／内存代理指标不是精确 GPU 归因。日志器也不等于 MemoryLeakFix 或消毒站性能修复。

## 11. 统计相关参考模组，不冒称都是明确待替代

| 参考模组 | 作者说明中的能力 | 自家对照 |
| --- | --- | --- |
| Hikaria AccuracyTracker 1.3.1 | 命中率、弱点率、弱点次数、命中次数、总弹丸数 | 自家显示两种率，内部计数不等于 UI 暴露全部计数 |
| 同上 | 霰弹按丸数、穿透只算一次准确命中 | 自家采用对应原则 |
| 同上 | 主机可估算未安装玩家准确率，用 * 标注不可靠 | 自家对无匹配数据的真人显示破折号，不提供同等估计 |
| 同上 | 非主机显示安装相同插件的玩家数据 | 自家仅匹配自己的协议，不兼容 AccuracyTracker |
| AoiYuki AccuracyShow 0.2.3 | 本局自己射击命中率 | 自家覆盖核心用途 |
| AoiYuki DamageIndicator 0.4.10 | 主机侧全队总伤害及结算／可控伤害报告消息 | 自家覆盖主机总伤害和自己的结算文本，不复刻原版聊天报告协议／选项 |
| Hikaria DamageAnalyzer 1.2.1 | 命中时显示目标敌人 HP 等即时信息 | 自家统计不提供敌人血条／即时目标信息，不能声称替代 |

这里的证据级别为对应版本作者 README，不是对这些额外参考模组全部隐藏设置的二进制审计。

## 12. 游戏修复：仍在 Temp 的模组不能因 Archive 有 Fixes 分类就删除

下面十个包当前仍启用。自家不是它们的完整替代者；Archive 有相近名字也不能证明所有子修复等价。

| 当前保留模组 | 该版本作者列出的功能 | 对照结论 |
| --- | --- | --- |
| MemoryLeakFix 1.3.9 | 尸体消失特效复用／清理；Shooter bug；若干音源未清理；敌人死亡／关卡结束对象清理；低效 IRF 渲染；WindVolumeCamera 错误刷屏；消毒站掉帧；终端关卡构建回调未解除；换近战后的坏数据包 | 自家日志不修复这些；Archive DecayIRFNREFix 不是整套等价，保留 |
| EnemyAnimationFix 1.4.8 | 泡沫视觉渐退；不存在动画修正（巨人喊叫、射手攻击、Hybrid 硬直）；对应攻击时滑行问题；屏外脚步；近战不打尸体；客户端攻击动画不提前结束；攻击停步位置纠正及平滑；主机屏外攻击／硬直移动；动作切换停止舌头／射击；舌头分配失败不永久卡住；玩家贴身不取消近战；远房间寻路；飞行敌人跨房目标变化；泡沫时长同步；波次敌人 AFK 时限；后续喊叫正常唤醒房间；重开喊叫冷却；防隔门远程破门；出生即死导致无敌／永久失能修复；部分飞行寻路 | 覆盖面远超 GhostEnemyFix／DeadBodyFix，保留 |
| DoorEnemyFixUpdated 1.1.3 | 防雷／泡沫穿门触及另一侧敌人；已附着门的泡沫不被任何一侧敌人碰走；门上泡沫消失后门无法打开修复 | 自家没有；它是 Localia DoorEnemyFix 的新版替代，不是被 Archive 全包 |
| ReDownFix 1.0.1 | 主机还未确认复活时，客户端受任意伤害再次倒地的问题 | 与复活交互提示修复不同，保留 |
| StuckEnemyFix 0.0.8 | 主机检查非飞行进攻敌人 30 秒内是否离出生点 2 米；同节点重定位并锁最近玩家；四次失败后清除；泡沫状态暂停判断 | 与普通 ghost 清理／动画修复不同，保留 |
| SnatcherBugFix 0.5.0 | 切菜单时吞吐屏幕特效；防困在 arena 维度；主机防 arena 刷生存波／敌人；吐出后移动与环境音频错误 | Archive PouncerFXFix 只证明其中相近特效范围，不能替代全部，保留 |
| GalaxyBugFix 1.0.1 | 修正剔除系统误判玩家所在区域 | 自家模型刷新不是全局剔除逻辑修复，保留 |
| DoorSyncFix 1.0.0 | 主机验证客户端安全门包，防高延迟导致重关门／扫描完成事件重复 | 自家没有，保留 |
| NoMenuBlink 1.0.2 | 装备页快速选择导致意外关闭；限定只处理装备避免影响其他扩展 | 自家没有，保留 |
| STFU 1.3.0 | 可配置且热编辑的日志降噪；MCP3 日志、Borken Cells、负缩放 BoxCollider、ConfigurableGlobalWaveSettings 调试输出、部分 SamGeos 错误噪声 | 自家诊断不是通用降噪，保留；性能分析应知道日志可能被过滤 |

### Archive／Core 当前相关开关

| 提供者 | 已确认启用的相关功能 | 不能推导的结论 |
| --- | --- | --- |
| Archive Fixes | BioTrackerSmallRedDotFix、BioTrackerStuckSoundFix、DecayIRFNREFix、FlickShotFix、GhostEnemyFix、InteractionFix、MapPanFix、NoDeadPings、PouncerFXFix、ReloadMinusOneBugFix、ResourceInheritanceFix、WeaponAudioSyncFix，以及配置中的 KillIndicatorFix、MapToObjectivesSwitchFix、WeaponShootForward | 名称及 true 不代表上表所有独立修复被覆盖 |
| Core Fixes | BackBonusFix、BulletPierceFix、DeadBodyFix、KillIndicatorFix、LobbyGhostPlayerFix | 必须按各补丁作用点判断，不按 Fix 一词合并 |
| Core Accessibility | PlayerPingHelper、ReceiveAmmoGiveFix、ResourceStack | 不等于全部 HUD／持续标记／交互修复 |
| Archive QoL／HUD | L4DStylePacks、NoNavMarkerHideInChat、WeaponStats、SentryMarkerTweaks、DisplaySentryArchetypeName、InteractReviveFix、ResourcePrioritizationPings | 有各自职责，不是上述独立模组的一键完整替代 |
| Archive Accessibility | ChatTweaks、DisableDeathDrone、DisableInfectionBreathing、DisableInfectionCoughing | 三个禁声音开关已开；ChatTweaks 仍有第 3 节缺口 |

Archive 与 Core 的 KillIndicatorFix 都存在启用记录，属于需要后续按实际补丁核对的重叠点；本轮不擅自关其中一个。历史旧短键与完整类型名键可能同时存在，本报告只把完整 GUID 作为该分支配置证据。

BetterBots、ChatterReborn、GTFOReplay、BepInEx、Clonesoft Json 也仍在 Temp；没有把它们当作这次自家 HUD／统计替代的目标。依赖不能按“看起来没用”删除。

## 13. 所有 rundown 的边界

用户要求不只 R8，这不能被解释成“一个最新 DLL 已经支持所有历史 GTFO 可执行版本”。

| 范围 | 能确认什么 | 尚不能确认什么 |
| --- | --- | --- |
| 当前 GTFO 中可选的官方不同 rundown／ALT 内容 | 自家多数 HUD、标记、资源逻辑走当前游戏通用类型，不硬编码只标 R8 | 每个任务物品／设备／检查点的现场行为 |
| 自定义 rundown | 使用原生槽位与终端实体的对象有基础覆盖 | 任意自定义图标、动态设备、EWC 效果、作者自定义描述页 |
| 历史 Booster | 自家有精确历史模板补充 | 全部旧模板；未知模板明确不修改 |
| 旧版本 GTFO 可执行程序／不同 interop API | Archive 某些功能有 RundownConstraint 或分支 | 自家按当前 Temp interop 构建，不是跨所有历史构建兼容承诺 |
| 武器恢复预设 | 特定原版 HEL Gun／狙击数据有定义 | 对全部 rundown 的同名／改装武器都应该强制覆盖 |

## 14. 缺口优先级与已经接受的差异

### 应优先处理，但本轮仅报告

1. **终端交互与聊天真实功能损失**：NoInterruptions 的终端修复、BetterTextChat 的多行／光标／接收历史等；先明确负责者再移植或恢复，不凭名字判断。
2. **标记生命周期完整性**：临时 ping 与持久标记去重、检查点记忆、动态设备、反应堆终端登记；操作者标记与终端被发现必须分开验收。
3. **武器属性页缺失**：DWSS 断点、分页、自定义说明、HIP／ADS；不能用战斗统计 HUD 顶替。
4. **StatDisplay 完整度**：逐武器统计及结算、穿透／弱点伤害、可选格式与 EWC；先统一口径再做外观，不只是向右挪几个像素。
5. **Booster 功能选择**：原版编辑器、无条件／无负面、固定奖励模式都未迁入；不能把“完美＋不消耗＋×5”称为全功能版。
6. **剩余表现与配置**：逐物品图标／名称、焦点折叠、距离缩放、渐变颜色；现有 Archive 炮塔标记不要再重复实现一套。

### 已经明确不作为缺口补回

- Core 标点助手不复刻 PingEverything 的“睡眠敌人／12 米”限制：用户已接受。
- 不给柜子和箱体自身加持续标记；只标已发现内容。
- 不持包时保持正常原生 HUD，不常驻额外弹量／工具／血量数字。
- 统计采用弱点命中率，不是只算头部；不要恢复独立大统计面板。
- ResourceStack 留给 Core，不在自家重复实现。
- 不因“增强版”名义加入未发现全图揭示。

## 15. 可复查的证据与验收状态

### 本机一手证据

- 自家源码：C:/Users/nainf/Github/Infini-GTFO-Mods，重点文件在各节列明。
- 上游版本：C:/Users/nainf/AppData/Roaming/r2modmanPlus-local/GTFO/cache/{作者-包名}/{版本}/ 下的 README、manifest、实际 DLL；用本机 ILSpy 只读查看类型／方法。
- 当前安装：C:/Users/nainf/AppData/Roaming/r2modmanPlus-local/GTFO/profiles/Temp/mods.yml 和 BepInEx/plugins。
- 开关：C:/Users/nainf/AppData/LocalLow/GTFO_TheArchive/SaveData/OtherConfigs/EnabledFeatures.json 的 Features 对象。
- 功能细项：同 SaveData/FeatureSettings 下的 WeaponStats、SentryMarkerTweaks 等 JSON。
- 加载证据：Temp/BepInEx/LogOutput.log。日志证明挂接，不证明玩家看到的结果。

### 二进制对应

| 对象 | SHA256 |
| --- | --- |
| Temp 已装 2.2.7 DLL | 1047329AB01937B791A2E667A7EBB2A658A37A160D40CFF44D68A2A3FFDE870F |
| 候选 2.2.9 DLL | 9373A11745FFAF5EF0F09DEB822E63EA7F1B2AE5CC5A08ACA10FE7A4262E672A |

先前 2.2.9 离线记录为 749 个回归断言、65 个原生契约检查、Release 零警告错误；本次纯审计未重新运行构建。它们不覆盖第三方所有行为，更不证明上文“未实现”项已通过。

本报告覆盖上述明确版本可核对的用户功能／公开配置，以及替代相关实现差异。未把历史版本每条编译兼容更新当作独立用户功能，也没有对未指定的全部缓存插件逐 DLL 做穷举审计。无法证明的边界明确标出，不包装成“全部正常”或“可以全部安全卸载”。
