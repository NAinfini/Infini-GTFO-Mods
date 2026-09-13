## 2.5.2 当前标记状态 — 2026-09-09

本轮按 Localia ResourceHelper 3.0.1 发布包核对并实现显示行为，取代下方历史审计中的永久PING扩展、20米消耗品、同zone越距和瞄准全隐藏。当前详情见 HUD-BEHAVIOR.md；55张内嵌图案及11色钥匙卡见 MARKER-COVERAGE.md。原生inactive组件不会接收SetAlpha已用游戏二进制确认，离线回归先复现旧代码的恢复失败、再通过修复。游戏渲染和多人验收仍待重启后进行。以下按版本记录的历史发现不代表当前实现状态。

# 上游流程对照与发布验收

## 2.4.5 — 2026-09-09

本版发布内容以 CHANGELOG.md 与 HUD-BEHAVIOR.md 的 2.4.5 段为准。以下旧版本审查属于历史记录。离线检查不代表游戏实测；安装状态通过 Profile、mods.yml 和 DLL 哈希独立核对。

更新日期：2026-09-08。**当前修复版本 2.4.3：已处理下列源码缺陷并通过离线检查，游戏内验收仍未完成。** 本文件顶部状态优先于下方 2.4.2 历史审查及其它文档的旧版本结论。安装状态须另核对 Profile manifest、mods.yml 和 DLL 哈希；已安装不等于已在游戏加载或实测。

## 2.4.3 修复结果与仍需验收的范围

| 审查项 | 本次源码处理 | 实际验证／边界 |
| --- | --- | --- |
| A1：资源 HUD 重建 | 同 owner 更换 NavMarker 时释放旧克隆、恢复旧原生透明度并绑定新对象；PlaceMarker 后刷新 | 13 项生产视图检查通过，包含 Show／Alpha 两种重建顺序与真实非空模拟父级；未验证游戏复活画面 |
| A2：设备发现路由 | 拾取 sync／设备登记表为同一身份入口，覆盖附近发现、主动及收到的 impact ping；终端碰撞体补原生 ping，监听 PlayPing | 19 项生产登记检查通过，含终端与屏幕碰撞体为兄弟节点；多人实际 ping 尚未验收 |
| A3／A4：恢复和晚生成 | 槽位独立订阅 Recall，不依赖标记快照；SpawnItem 完成后登记、用真实节点补齐和关联槽位容器；已打开箱内晚生成内容可发现；保存手动隐藏记忆 | 原生契约解析通过；检查点及模型生成顺序仍需实机验证 |
| A5／A11：箱柜槽位 | 同一槽位解析供占用和放置关联使用，反向索引释放占用；容器回调只更新本箱，重建清理索引 | 32 项生产槽位检查通过，含独立期望锚点、旋转／缩放、占用冲突；非完整 Unity Transform／物理模拟。三格箱、六格柜的提示、预览、落地、双人抢格仍待实测 |
| A6：统计 HUD | 改用原生 Background Fade 行→内容→文字层级，继承原生文字对齐，保留用户 OffsetX／Y；取消错父级的右 pivot 路径 | 可编译；当前 4K、1080p、宽屏及四人画面未验收，不宣称截图位置已通过 |
| A8：NoInterruptions | 补原生地雷回收点偏移、无声音原点时使用本地玩家位置；保留现有终端输入同步、原生验证和有界队列 | 109 项原生契约包含新增 hook；12 项终端生产检查通过，主客机实测未完成 |
| A9：统计来源 | 明确枪／近战／炮塔／地雷的当前来源上下文；失败后检查点继续恢复采集且保留累计 | 模型回归和参数契约通过，真实伤害回调时序仍需实测；不添加 EWC 或随机未知武器分类 |
| A10／A12：重复工作 | 格式模板预编译；每个接收者只发送变化统计，完整首次握手保留，发送返回后才记已发布；HUD 复用文本构造器，不从渲染再次置脏 | 734 项回归断言通过；无变化 marker 工作量测试仅测调用数，不是本轮 FPS／GPU 基准，不能宣称整体快于上游 |
| A7：原生 Actions 越界 | **未解决**；不能从接收堆栈确认发送插件 | 需要主客机同时间日志与加载版本；没有吞异常或声称已修复 |
| A13：高级配置 | 未新增外部 PNG 导入、完整可见模式／节点距离／逐物品 ADS 配置编辑器 | 不宣称完整复刻所有上游配置。用户取消的 Booster 编辑、无条件、无负面、EWC、终端使用者和普通背包信息继续排除 |

最终 2.4.3 Release 构建为零警告／零错误；734 项回归、13 项 HUD、19 项标记登记、32 项箱柜槽位、12 项终端检查及 109 项原生契约实际通过。契约检查现已校验 __instance 的实例类型继承关系；不执行 IL2CPP detour。没有进行游戏内验收或新的 CPU/GPU 测量。第三方参考均在独立临时目录；没有复制未授权源码或打包第三方 DLL。

游戏验收顺序：开有资源／消耗品的箱→出现分类图标→拿起消失→补给一次→指定空格放回→确认提示、模型、数量、图标一致→检查点恢复后再拿放；另验终端及动态设备 ping、cell/cryo 跟随／放下、四人统计和持包切换。主／客机分别验收；没有完成的组合继续标未验证。

## 2.4.2 历史审查（以下保留发现时的证据）

以下“当前／本轮／未修改／仍未关闭”均指 2.4.2 审查时点，当时结论为暂缓部署、Temp 已装 2.4.1。后续修复和验证以顶部 2.4.3 表为准。

## 为什么反复返工

1. 曾按功能名称和局部方法迁移，没有先核对完整事件链。开箱发现、生成完成后的节点修复、原生标记重建、检查点恢复等不是附属细节。
2. 以简化对象模拟 Unity，却没有明确模拟覆盖边界。原有 SpawnNode 模拟允许 null，真实 setter 会抛异常；槽位模拟不处理父级／旋转／缩放；Place 模拟只计数并返回成功。
3. 编译和补丁参数检查只能说明符号存在，不能证明原生调用时序、模型、图标、投影或多人消息正确。
4. 用一次截图位置推测坐标修正，没有记录完整原生父级、pivot、缩放和实际分辨率下的验收结果。
5. 多份历史文档仍含“最新候选”等已过期文字，容易混淆源码、包、已安装和实际运行版本。

## 此次发现

| ID | 结论／证据等级 | 现状与后果 | 必须补的检查 |
| --- | --- | --- | --- |
| A1 | 确认的控制流缺陷；生产代码＋模拟对象已复现 | `ResourceHudView.Show` 只按 owner.Pointer 查缓存，缓存 `_marker` 为 readonly。同一个 owner 的 m_marker 被替换后，文字和图标仍属于旧标记；`Alpha` 也继续操作旧对象。HUDInfoPlus 在原生 PlaceMarker 后处理当前标记 | 同 owner／新 NavMarker、旧标记销毁、重新入队／复活；必须检查新父级及旧克隆释放 |
| A2 | 确认的路由覆盖缺口；场景触发仍需实测 | `ItemMarkers.PlayerPing` 只沿父级找 ItemInLevel／LG_GenericTerminalItem；无 terminalItemId 的 ReceivedPing 只解析拾取物。两者没有统一使用已登记的 DiscoveryOwners。碰撞体与 terminalItem 为兄弟节点时不能这样找到设备。上游给碰撞体附加 ItemMarkerTag，终端还补 PlayerPingTarget | 相同设备经附近发现、直接 ping、队友 ping、终端 ping 都应解析为同一实体；兄弟节点层级不能遗漏 |
| A3 | 确认的结构耦合；故障触发条件待实测 | `ItemMarkers.RestoreMarkers` 在没有本地标记快照时提前返回，导致它内部的 `ResourceHandling.RebuildWorld` 也不执行。槽位恢复不应依赖是否有标记快照。DropItemPlus 独立订阅 OnRecallComplete 重建槽位 | 有／无标记快照均重建原生槽位占用；晚加入、已放回、已拿起、空箱都测 |
| A4 | 上游流程缺口，不冒充已复现的本局根因 | ItemMarker 和 DropItemPlus 在 ItemSpawnManager.SpawnItem 完成后修复节点；包括真实 LG_PickupItem.SpawnNode 来源。我们主要依靠 sync Setup／factory 和 container SetSpawnNode，ItemInLevel.Setup 只登记对象 | 生成顺序为“sync 已建立但模型后来才出现”、非容器拾取物、动态生成和节点缺失；确认所需路径后统一登记，不堆叠猜测节点 |
| A5 | 验证缺口 | 2.4.2 槽位只核对了原版六格柜／三格箱；测试没有真实父级、旋转、缩放或多人放置，模型幽灵测试也未覆盖渲染 | 每一格、旋转柜、两种物品锚点、邻格已占用、主／客机、两个玩家同时放入、检查点；预览与落地模型逐格对齐 |
| A6 | 验证缺口 | Dinorush 克隆 Background Fade 的容器层级再放文字；我们直接克隆文字到其父级并设置右 pivot／localPosition。代码不同不一定错误，但没有实机证据支持“位置已正确” | 当前 3840×2160、1080p、至少一个宽屏比例；满四人、长名字、不同字号、手持各装备／包时不遮挡 |
| A7 | 未解决的现场错误 | Actions packetIndex=47、packet count=47 是原生接收表不包含该索引。堆栈不提供发送插件；自己的 GTFO-API bot 过滤不能证明该错误已修复 | 核对主客机加载版本、网络注册与双方同时间日志；不得吞异常或虚构根因 |

定位：A1 `ResourceHudView.cs:34`；A2 `ItemMarkers.cs:191`、`:391`；A3 `ItemMarkers.cs:240`；A4 `ResourceHandling.cs:33`、`:100`；A5 `ResourceHandling.Slots.cs`、`tests/ResourceSlots/Program.cs`；A6 `CombatStatsView.cs:50`。

### A1 复现结果

临时检查直接链接现行 `ResourceHudView.cs`，复用现有 HudView 模拟测试，不修改生产源码：先运行原有 9 项检查；随后 Show 一个资源 HUD，保留旧文字引用，把 owner.m_marker 换成新 NavMarker，再调用 Show。应销毁／重绑定旧视图，实际没有。

运行结果：原有 9 项通过；新增检查抛出 `REPRO: native NavMarker replaced on the same owner, but the old resource HUD remains cached instead of being rebound.`，退出码 1。这证明缓存控制流缺陷，不等同于已经在游戏中复现队友复活场景。

临时检查位置：`C:/Users/nainf/AppData/Local/Temp/infini-upstream-audit-31603b17af744169b04dfc53343e2c52`。可用 `dotnet run --project Repro.csproj -c Release` 重跑；后续修复时应把这项迁入正式生产代码回归，临时目录不是长期测试来源。

## 从上游保留什么，而不是照抄什么

| 模块／参考 | 必须保留的流程 | 用户定制或需要证据支持的差异 |
| --- | --- | --- |
| ItemMarker：Features/ItemMarker、ItemScanner、ItemMarkerBase、各设备类 | 原生生成／开箱／拾取事件、碰撞体到实体的准确绑定、检查点、设备生命周期 | 不标箱柜外壳和普通门；已发现资源记忆；远处非注视只显示图标；不显示终端使用者 |
| ResourceHelper：Res_Manager／Res_Moniter | 拾取身份与数量核对、近处／注视信息展开 | 不照搬高频扫描和重复 UI 提交；必须用同场景耗时验证性能，不从代码长短推断 FPS |
| DropItemPlus：Slot／Manager／Feature | 原生 Interact_Timed、槽位占用、放置前容器关联、原生落地后渲染缓存更新、独立 Recall | 保留无脚本网格预览的实现选择，但必须证明它和落地模型一致；不能把参数正确当作模型正确 |
| HUDInfoPlus／PacksHelper | PlaceMarker、持物切换、Alive、资源刷新、炮塔放下／回收、原生对象重建 | 持对应包才显示对应资源，红黄绿渐变；不恢复普通队友背包清单；名字／距离／资源行分别控制 |
| Dinorush StatDisplay：StatHandler／AccuracyPatches／同步 | 正确 UI 层级、开枪／弹丸／弱点计数口径、信息来源和主客机权限 | 自家源码、不内嵌 DLL；弱点命中率=弱点命中÷命中；EWC 排除，未知值不是零，不伪造远端统计 |
| NoInterruptions：Interaction／Command／Terminal patches | 原生交互选择、退出前输入同步、队列／状态机和清理顺序 | 自家保留原生命令验证、有界队列及异常后恢复控制标志；不为“照抄”撤回这些明确约束 |
| BoosterTweaker：PerfectBooster | 模板／库存刷新、消耗路径、原生收益换算 | 只保留约定的完美数值、不消耗、实际收益×100000；不加入编辑器、无条件、无负面、固定奖励；到账需库存前后核对 |
| Archive／Core | 由既有提供者负责 | ChatTweaks、WeaponStats 交给 Archive；ResourceStack／ping helper 交给 Core，不再重复实现 |

上游也有实现局限。例如所查 NoInterruptions CommandPatch 把排队命令直接提交发送，自家重新进入原生验证；所查 ResourceHelper 高频扫描不应成为未测量就照搬的性能方案。参考源码是行为证据，不是“原版必然无错”的证明。

## 以后每项替代功能的完成门槛

1. **先锁定真实参考。** 记录上游版本／commit、入口、状态所有者、依赖和清理事件；列出用户明确要改掉的行为。不能先凭名称写实现，再找参考证明自己。
2. **按完整生命周期迁移。** 最少核对生成→发现→显示→拿起→放下→耗尽／完成→重建／检查点→销毁；跨模块用同一实体身份，不各做一套近似查找。
3. **先加入能拦住故障的用例。** 每个现场 bug 都有明确反例；测试数据不能直接用被测实现的同一组常量生成唯一预期。原生约束未知就标未验证，不写一个宽松模拟让它通过。
4. **分开汇报验证层级。** 构建＝可编译；原生契约＝能解析到方法和参数（当前检查还跳过 __instance 类型）；生产模拟＝受模拟边界约束的控制流；游戏验收＝实际表现。四者分别列，不相互替代。
5. **验收用户关心的组合。** 主／客机＋真人／机器人；医疗／弹药／工具／消毒；资源／消耗品／任务携带物；柜／箱所有格；正常流程／检查点／重新入队；仅在涉及的组合中测，不用随意增加大量无关断言充数。
6. **候选不冒充完成。** 包、安装、实际加载各自核对版本／哈希。先做受控游戏验收，再进入长期使用；确认的缺陷或遗漏流程未处理，不因为构建通过就继续正常部署。

本轮未执行新构建，也未重跑与审查无关的全套检查。唯一新增运行是上面的失败反例。不能保证以后零 bug；可以明确防止再次用“编译成功＋简化模拟通过”宣布整套替代已经完成。

## 第二轮：下载固定版本后的逐流程对照

本节是同日后续审查，不是修复交付。生产源码、配置、DLL 和安装状态未变；此前 A1–A7 仍未关闭。此次实际补下载三个源码库、两个原始发布包，继续读取实现，不把旧文档的功能声明当作验收证据。

### 参考锁定与复用边界

本地独立参考根：`C:/Users/nainf/AppData/Local/Temp/infini-upstream-6769902ffc1940719c8b9f2126ce96e5`。不在项目编译目录内，没有安装这些参考包。Temp 目录可被系统清理，下面的 URL、commit 和哈希才是可重新取得参考的依据。

| 参考模组 | 已取得的参考 | 固定身份 |
| --- | --- | --- |
| [ItemMarker](https://github.com/Mamizu1028/GTFO_ItemMarker) | 已有源码；PluginInfo 标注 1.1.0 | `5b147221326d48402a874e2f625c63f6ef15b871` |
| [DropItemPlus](https://github.com/Mamizu1028/GTFO_DropItem) | 本轮下载源码；PluginInfo 标注 1.0.1 | `f33c9eae721f7d34a790c0462bf10080bf9395ee` |
| [ColorGradingHUDInfoPlus](https://github.com/Mamizu1028/GTFO_CGHUDInfo) | 本轮下载源码；PluginInfo 标注 1.0.1 | `2581299fa3cc95676b90464df97c289b89a61190` |
| [StatDisplay](https://github.com/Dinorush/StatDisplay) | 已有源码；插件声明 1.1.8 | `92f738a557a256905e4e198051fe44838fbe9bf1` |
| [NoInterruptions](https://github.com/Dinorush/NoInterruptions) | 已有源码；插件声明 0.1.12 | `596ec84b7de43a1f6f61e663ec078518a6ab179a` |
| [BoosterTweaker](https://github.com/Mamizu1028/GTFO_BoosterTweaker) | 已有源码；PluginInfo 标注 1.2.6 | `0583c8889db1b81588ed2fd9241d20538d39288d` |
| [ResourceHelper](https://github.com/GTFO-Modding/ResourceHelper) | 本轮下载源码，但插件声明 **3.0.0**，不是 3.0.1；另下载并核对 3.0.1 包 | 源码 `7533b08368eb2456316d0dc0f8b643a002b63b14`；运行版本对照使用下方发布 DLL |
| [PacksHelper](https://thunderstore.io/c/gtfo/p/Localia/PacksHelper/) | 本轮下载 3.1.4 包；发布 manifest 仅链接 Thunderstore 首页，没有找到可核实的作者源码链接 | 使用实际 3.1.4 DLL 反编译审查，不称为作者原始源码 |

源码中的版本声明不能证明它与同版本发布 DLL 逐字节对应。两个 Localia 包通过原始发布下载地址取得：`https://thunderstore.io/package/download/Localia/PacksHelper/3.1.4/`、`https://thunderstore.io/package/download/Localia/ResourceHelper/3.0.1/`。直接读取 ZIP 内 DLL 计算 SHA256，均与本地 r2modman 缓存一致：

| 文件 | SHA256 |
| --- | --- |
| Localia-PacksHelper-3.1.4.zip | `73CDB262C4050EFCCCEA8E05C5858C8FA870990AC9652BD13CDD552F10A591E2` |
| 包内 Localia.PacksHelper.dll | `942F1008AABB6545D862846C5A14217A7E6A54EFA0EDBC00DA65BC31E5DB8285` |
| Localia-ResourceHelper-3.0.1.zip | `1164FE030B5BA5800073E1F013B7A5F42D7C5046B0571DE01640AF3A7C812086` |
| 包内 plugins/Localia.ResourceHelper.dll | `9D26260A94C57B1FC6A973437FE0C76BBF5E0675D0442BB7DB9B2F9D39CE577F` |

本轮检查上述七个 Git 检出的已跟踪 LICENSE／COPYING 文件，均未找到；不能据此把整段第三方代码按 MIT 搬入发布包。可参考原生 API、事件和实际行为独立实现；取得明确许可后才做受许可约束的源码复用。历史 WeaponStatShower 检出有 MIT，但用户已取消自家武器说明，不因此重新引入。Hikaria 三项还依赖 Archive Feature API／Core 事件，不能只复制某个方法而漏掉注册、启停和事件订阅。

### 本轮新增／细化的缺口

| ID | 证据与影响 | 应采取的修改；本轮未实施 |
| --- | --- | --- |
| A8 | NoInterruptions `Patches/MinePatch.cs` 在 `OnStickyMineSpawned` 后将地雷 PickupInteraction 沿本地 Z 移动 -0.01；自家没有对应入口。它属于地雷回收交互，不是终端输入 | 列入替代缺口，核查 Archive/Core 是否已有同一修正，再测试贴墙地雷回收。不能因终端表完成就称 NoInterruptions 全覆盖，也不能未核对重复补丁就再加一次位移 |
| A9 | StatDisplay `DamagePatches` 明确缓存枪械、近战、炮塔及地雷的来源；自家 `CombatStatistics.Damage` 只用 ProcessReceivedDamage 的 gearCategoryId 映射槽位。无有效类别时进入 Other，没有上述来源上下文 | 核对原生参数和炮塔／地雷调用链后补准确来源；必须测试一发已知伤害分别进入对应槽。现在不能承诺 Tool／Melee 等槽位与原版等价；这不是已复现的本局伤害错误 |
| A10 | StatDisplay `StatParser` 在模板改变时解析、数据变动时更新文字；`StatSyncManager` 只发送待同步增量，间隔 2 秒。自家每秒重复广播 Hello、遍历并发送已有行，Refresh 重新格式化，只有最终 SetText 做相同文本过滤 | 保留自家会话身份／权限校验和可靠快照，只对变化数据发送、在新连接时握手和补全快照；模板解析缓存。不能直接换成原版协议，也不能宣称这些差异解释了 120→40 FPS |
| A11 | `ResourceHandling.AttachContainer` 已调用 TrackSlot，却又遍历所有容器和锚点找同一落点；`DepositContainerChanged` 每个容器回调都更新所有容器的全部格子。上游槽位对象订阅自己的 container sync | 让槽位查找结果同时负责占用、container 和 parent；容器事件只刷新该容器。减少重复查找，也避免两套判定认定不同槽位 |
| A12 | ResourceHud Render 每次 UpdateExtraInfo 新建 StringBuilder、重写 UpdateName，并无条件设 m_isInfoDirty=true；HUDInfoPlus 复用 builder，StatDisplay 使用脏标记。自家尚未把内容变化与原生刷新安排明确分开 | 缓存对应包／数值／格式的显示结果，仅值变时重建文本，不让自家刷新持续重新置脏。先测实际回调频率；不能只删脏标志导致资源不再更新 |
| A13 | ItemMarker 逐物品配置还包括标题来源、节点距离、可见性模式、单物品 ADS 透明度、独立玩家／终端 ping 时限、外部图标文件。自家仅包含其中一部分，见下表 | 明确列为未提供的配置，不把“有逐物品配置”写成全等价。稳定原生图标与生命周期优先；没有把这些选项擅自全部加入 |

### 功能对照：保留项、用户差异与未完成项

下表以现行 2.4.2 源码为准。“有路径”只说明读到了实现，不代表游戏内通过。具体失败和验收门槛见 A1–A13；用户取消的功能不是待补缺口。

| 模块／子功能 | 上游实现依据 | 我们的现状与取舍 |
| --- | --- | --- |
| 资源／消耗品首次登记 | ItemMarker：Setup、SpawnItem 完成回调、容器节点赋值 | 有 Setup／factory／容器回调；生成完成补节点仍有 A4 缺口 |
| 开箱发现内容 | ItemMarker：容器 Open 后直接通知子物品 tag；容器内物品不依赖普通发现扫描 | 2.4.2 有 ContainerOpened；要验收隐藏内容不提前显示、开箱立即出现 |
| 自己／队友附近发现 | ItemMarker 碰撞体 tag；ResourceHelper 交互球形查询 | 自家 4 Hz NonAlloc 查询，有已登记 owner 表；直接／远端 ping 仍没统一用它（A2） |
| 拿起、耗尽、换对象 | ItemMarker 同步状态；ResourceHelper 比较同步物品 ID 和数量 | 自家有同步状态、数量回调、身份去重；必须测试资源拿起后世界图标立即消失且不跟人走 |
| 放回后重新出现 | DropItemPlus 原生 Place 完成、容器关联、culler buffer 更新 | 有这些路径及 PlacementCompleted；逐格落地模型未验收，A3／A4／A5／A11 尚在 |
| 任务携带物 | ItemMarker 对特殊携带物控制原生／替代图标 | 自家按 CarryItemPickup_Core 状态跟随携带者，放下回物品；保持用户要求，普通资源不这样跟随 |
| 终端 | ItemMarker m_terminalItem、碰撞体 tag 和补 PlayerPingTarget | 自家存在型可用性、屏幕交互锚点、proximity／interaction；ping 的兄弟节点识别仍缺（A2） |
| 发电机 | ItemMarker 插槽 graphics 与 powerCellInteraction 状态 | 自家使用对应状态条件；完成隐藏有路径，事件时序待游戏验收 |
| 消毒站 | ItemMarker m_interact.IsActive | 自家同一可用性依据；生成／使用完毕／检查点组合待验收 |
| HSU 取样／插入 | ItemMarker pickupSampleInteraction／insertHSUInteraction | 自家两类都有；插入装置含 custom geomorph Setup；非普通装饰 HSU 全图揭示 |
| 舱门控制器 | ItemMarker InactiveNoMoreInteraction | 自家同一完成条件；不需要终端使用者姓名 |
| 安全门锁需求 | ItemMarker 根据三类锁显示钥匙／发电机／控制器需求 | 自家三类 Setup 和状态条件都有；不显示普通门、箱柜壳体 |
| 查询／主动 ping | ItemMarker GenericTerminal.PlayPing、tag identity；ResourceHelper ping 位置附近临时放大范围 | 自家有 QUERY、原生 ping 和自家拾取物消息，但身份入口不一致；不能以“事件挂上了”判完成 |
| 去重 | ItemMarker 接管同物品原生图标；ResourceHelper 一物品一个 marker | 自家 MarkerOwnership 与原生距离已有单一归属；携带／放回／瞬时 ping 要实测没有双图标双距离 |
| 检查点 | ItemMarker 按 buffer 保存状态；DropItemPlus 独立重建槽位；ResourceHelper 保存位置再扫描 | 自家有标记快照但槽位重建错误依赖快照（A3）；手动隐藏记忆也未全等价 |
| 原生分类图标 | ItemMarker 各设备 native style，拾取物类型匹配；ResourceHelper 自带 PNG | 自家使用原生类别图标，不复制外部美术；医／弹／工具／消毒与消耗品实际图形仍需逐项截图验收 |
| 标题／数量／距离 | ResourceHelper 近 4m 或注视显示名称、资源除以 20／消耗品除以 1 | 自家近处／注视展开，远处仅图标；原生数量规则＋可配置 divisor；不把百分比当次数 |
| 逐物品基础配置 | ItemMarker／ResourceHelper 用 ItemDataBlock ID | 自家支持启用、名称、类别图标、距离、透明度、颜色、数量 divisor、标题和距离开关 |
| 逐物品高级配置 | ItemMarker 独立可见模式、节点距离、标题来源、ADS alpha、两种 ping 时限、自定义图像 | 未全覆盖（A13）；不冒称已有完整编辑器或外部图标导入 |
| 持包筛选 | HUDInfoPlus 读取 pack type；PacksHelper 用 prefab 名和原生字符串行解析 | 自家读真实类型与资源数据：医疗 HP、弹药 Main/Special、工具 Tool、消毒 Infection；比字符串解析更少格式依赖 |
| 颜色／字号 | HUDInfoPlus 分段渐变且满值可青色；PacksHelper 140% | 按用户要求 0红→50黄→100绿、感染反向，字号 1.2 倍加粗；这属于明确定制，不照搬青色／140% |
| 炮塔弹量／无限工具 | HUDInfoPlus Spawn/Despawn 登记炮塔，读取部署弹量和 infinite 标志 | 自家有对应数据路径；晚加入／检查点生成时序及 marker 重建需验收 |
| UI 重建／清理 | HUDInfoPlus PlaceMarker 后处理当前 NavMarker；启停安装／移除 modifier | 自家同 owner 换 marker 缺陷已复现（A1）；必须修复，不能只调字号 |
| 队友距离／透明度 | HUDInfoPlus 距离缩放、视角和 ADS alpha | 自家姓名、距离、资源行分开控制；用户要求不显示队友距离。没有照搬上游夸大的距离缩放 |
| 无包时的信息／背包清单 | HUDInfoPlus 可以常驻显示全部槽位、BotLeader；PacksHelper 恢复原文本 | 用户明确不要额外资源百分比、普通背包清单、终端使用者信息；自家只保留正常原生 HUD，不能为全等价恢复这些 |
| 左右键／E 补给和提示 | PacksHelper UpdateInteractionActionName、PlayerCheckInput、ManualUpdateWithCondition；还把目标资源信息加入提示 | 按键交给 Archive L4DStylePacks，HUD 交给自家。不能据名称断言 3.1.4 的所有边缘修复等价；靠近箱柜、已按住按键、切换自己／队友要单独验收；提示附加资源文字未证明等价 |
| 放回选择与提示 | DropItemPlus 每格 Interact_Timed、0.4秒、原生文本 864/827、Use 绑定 | 自家有同类格交互及原生提示；不再是小十字选点；真实箱三格／柜六格必须逐格验收 |
| 预览／物品动画 | DropItemPlus 实例化 prefab，透明材质，选择时发送 GiveResource 动画 | 自家只画无脚本网格，未发送该选择动画；差异公开保留，不复制可能带逻辑的完整预制体。模型／旋转／槽位对齐仍未验收 |
| 放回占用／多人 | DropItemPlus 按 sync 身份跟踪、原生请求；Recall 后重建 | 自家有主机占用验证，但两人同时放同格尚未实测；请求返回不代表主机接受和落地成功 |
| 连续交互不被抢 | NoInterruptions InteractionPatch | 自家 FixedUpdate 内锁定有效选择，finalizer 恢复原生标志；距离／视线／梯子与补给组合待实测 |
| 终端退出同步／快速输入 | NoInterruptions TerminalPatch | 自家退出前同步、进入计时，状态表按 terminal ID；有离线覆盖，网络延迟实测未完成 |
| 命令排队／卡住恢复 | NoInterruptions CommandPatch、TerminalPatch | 自家 32条有界队列、重新走原生验证、Ping 结束无使用者回 Awake；不照搬原版直接发命令 |
| 放下资源后的终端位置 | NoInterruptions ResourcePatch | 自家更新节点和 FloorItemLocation；生成阶段的 A4 仍需解决 |
| 地雷回收点 | NoInterruptions MinePatch | 新确认遗漏（A8），不能被“终端已修复”覆盖 |
| 命中／弱点／穿透 | StatDisplay AccuracyPatches、IConfiguration | 自家 Fired/Hit、Crit/Hit、FullHit/Hit 和 Shot/Group/Full 路径已有；弱点不是纯头部；Core 替换 Fire 的组合必须验收 |
| 紧凑 HUD 位置 | StatDisplay 克隆 Background Fade 层级、文字放其子节点 | 自家层级不同（A6），右移配置存在不能证明画面正确；按上游结构对齐后再看用户分辨率截图 |
| 按槽结算／伤害 | StatDisplay DamagePatches、PagePatches | 自家有原生成功／失败页文本和槽位结果，但伤害来源有 A9；不是按换枪建立每件武器历史 |
| 统计格式／多人同步 | StatParser 编译模板、数据脏更新；StatSyncManager 增量 | 自家仅支持约定 token 子集、自己的 v5 协议，不与原版互通；A10 可减少空闲格式化和流量 |
| 无 mod 队友／主机 | StatDisplay 无 mod 主机时可显示本机估算伤害；部分武器分类含随机选择 | 自家只显示已知权威伤害，未知为破折号／Other，不假造精确结果；不复制随机归类 |
| Booster 完美值 | BoosterTweaker 按模板及效果组匹配库存 | 自家精确匹配现有词条与当前／历史模板，不改变条件或删除负面；库存更新和实际应用需要验收 |
| Booster 不消耗 | BoosterTweaker 会话 ID 和 ConsumeBoosters 拦截 | 自家还拦本地活动／分类消耗；不是“日志写了阻止”就证明服务器库存没扣 |
| Booster 收益 | BoosterTweaker Farmer 把结果固定设 100000 | 用户选择实际收益×100000且 0仍为0；不固定发奖。每种品质到账数量必须核对实际库存，不按换算日志宣称到账 |
| Booster 编辑／无条件／无负面 | BoosterTweaker 有这些能力 | 用户明确取消，不实现、不再列待补 |
| 聊天／武器说明 | BetterTextChat／WeaponStatShower 不是本次自家迁移目标 | 用户已接受只用 Archive ChatTweaks／WeaponStats；不恢复多行编辑器、分页、断点、EWC |
| PingEverything／ResourceStack／穿透与甩枪修复 | Core／Archive 负责既定功能 | 本轮没有修改这些提供者。此前穿透计数语义差异、与统计的补丁组合仍见原始审计；本轮不声称重新完整核验了其全部源码 |

### 先改哪里，以及怎样证明不是又差一点

1. **统一物品／设备身份与状态链**：先处理 A2/A4；开箱、附近扫描、玩家 ping、终端 ping、放回都进入同一登记和发现流程。资源状态负责显隐，渲染是否被剔除不能被当成拿起状态。
2. **独立槽位生命周期**：处理 A3/A11；原生格子记录一次解析，供占用／容器／模型使用。无标记快照的 Recall 也必须工作。
3. **原生 UI 重建和层级**：处理 A1/A6；同 owner 新 marker、四人统计、不同分辨率必须测。不是再凭截图添加一个偏移数。
4. **补齐来源和小修复**：A8/A9 单独验收；不复制未经核实的上游常量位移或随机伤害分类。
5. **减少无变化工作**：A10/A12，保留正确更新和完整首次同步。同场景比较 CPU 时间、分配、原生 UI 提交次数、网络包数；不只比较扫描 Hz。ResourceHelper 的 0.012秒 SphereCastAll 和每帧 SetTitle/SetAlpha 不宜照搬。

关键验收序列：打开有资源的箱→图标出现→拿起消失→给人补一次→放入指定空格→模型／数量／图标一致→换另一种包→重复→检查点恢复→再次拿放。另测普通消耗品、cell/cryo 携带、所有上述设备完成前后。主机和客机都跑，确认没有重复图标、旧数量、旧槽位占用。没有跑过的组合继续标“未验证”。

本轮新增验证仅是源码／发布身份核对与 DLL 哈希匹配，没有重建候选或执行游戏验收。当前仍不能宣布所有替代功能完成，也不能证明我们整体比上游快。之前 A1 的失败反例依然有效；这个报告是下一次实施的明确边界，不是已修复清单。

### 第一轮引用（保留）

- [ItemMarker](https://github.com/Mamizu1028/GTFO_ItemMarker)，本地检出 commit `5b147221326d48402a874e2f625c63f6ef15b871`；Features/ItemMarker.cs、ItemMarkerBase.cs、ItemInLevel_Marker.cs、LG_ComputerTerminal_Marker.cs。
- [DropItemPlus](https://github.com/Mamizu1028/GTFO_DropItem)，r2modman 缓存 1.0.1 的实际 DLL；LG_WeakResourceContainer_Slot、DropItemManager、DropItem 的原生注册和 Recall 实现。
- [HUDInfoPlus](https://github.com/Mamizu1028/GTFO_CGHUDInfo)，缓存 ColorGradingHUDInfoPlus 1.0.1 的 CGHUDInfo 实际实现；PacksHelper 3.1.4 和 ResourceHelper 3.0.1 的已安装缓存实现。
- [Dinorush StatDisplay](https://github.com/Dinorush/StatDisplay)，本地 upstream 检出中的 Handler/StatHandler.cs、Patches/AccuracyPatches.cs；NoInterruptions 本地 upstream 检出的三个相关补丁；BoosterTweaker 的 PerfectBooster.cs。仅描述已读部分，不据此宣称全部原版能力等价。
