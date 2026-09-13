当前图标更新：2.5.3 全部自有标记使用 58 张自定义 PNG；固定 HSU、插入装置和 C-Foam 已更新，未知物品使用通用问号图。以下旧版本描述为历史记录。

# 同类功能对照与取舍

**当前 2.4.3 修复与验收状态以 [UPSTREAM-ACCEPTANCE.md](UPSTREAM-ACCEPTANCE.md) 为准。** 最新决定为统计用自家源码重写、不内嵌原版 DLL；聊天／武器说明只用 Archive；EWC 特殊武器适配已排除。反应堆／动态设备登记已补。下面 2.2.9 对照为历史证据，不代表当前实现仍保留旧类、旧协议或已取消需求，也不证明完整上游功能等价。

审查日期：2026-09-08。以下“已有”指 2.2.9 源码覆盖，不代表已经通过游戏内验收。上游功能来自作者文档，并对照已安装或缓存的对应版本代码；缓存存在不等于曾在某个配置档安装。

2.2.9 消耗品核对：按真实 `InventorySlot.Consumable` 分类，不靠名称白名单；原生 Consumables 图标，名称/数量/距离、独立颜色和 12 米默认范围。自己或队友发现后显示，拿起移除，放回地面/开着的容器后仍可重新发现。新接 `ConsumablePickup_Core.OnCustomDataUpdated` 更新剩余数量；共享耗尽过滤覆盖有限次数消耗品，不因目标或无限次数物品 ammo=0 而误删。箱/柜体仍排除，未开启容器内容仍不揭示。

## 本轮代码对照：事实、修复和未覆盖部分

缓存 DLL 的具体版本及类型/方法如下，可在本机 r2modman GTFO/cache 对应包中复查；不打包第三方 DLL 或反编译源码。不是把功能名称相似当成已经等价。

| 原模组与代码证据 | 实际行为 | Infini Tweaks 的处理 |
| --- | --- | --- |
| ItemMarker 1.1.0，`ItemInLevel_Marker.IsPlacedInLevel`、`OnManualUpdate` | 按原生拾取同步状态移除/隐藏；搬运物自带的 `m_navMarkerPlacer.m_marker` 需隐藏，避免叠图标 | 2.2.8 补上同步对象身份清理和原生标记的单一显示归属。只在替代标记显示时抑制同物品原生标记；撤掉替代标记时恢复原生请求，避免全局关闭游戏目标提示。 |
| ItemMarker 1.1.0，各 `LG_ComputerTerminal/DisinfectionStation/HSU/HSUActivator/PowerGenerator/BulkheadDoorController/SecurityDoor_Locks_Marker` | 不是仅识别名字：计算机用 `m_terminalItem`，其他设备用实际交互、插入槽和门锁状态 | 2.2.8 修正计算机接口/屏幕交互锚点、自己外壳遮挡、队友原生终端同步发现。保留设备可用性处理。运行中动态新建的所有自定义设备尚未全面验证。 |
| ResourceHelper 3.0.1，`Res_Manager`、`Res_Moniter`、`MarkUI_Manager` | 附近发现、40m资源／10m消耗品、60m上限、临时PING、近处／注视名称、0.1秒淡入、50% ADS | 2.5.2独立实现这些显示规则，使用自家55张图案；10Hz NonAlloc发现与有效性检查，透明度逐帧，名称不变时不重写。保留严格遮挡检查、事件驱动开箱／落地和按同步ID的检查点，不照搬累加缩放。实机对照待验证。 |
| ColorGradingHUDInfoPlus 1.0.1，`CGHUDInfoSettings`、`PlaceNavMarkerOnGO__UpdateExtraInfo__Patch`、`PlayerHudDistanceModifier` | 按槽位选择、空槽过滤、无限弹量判断、已部署炮塔实际弹量、次数显示、颜色/字号、持包自动筛选、瞄准与角度透明度、距离缩放 | 已有持包对应数值/字号/颜色、次数和炮塔弹量；2.2.8 补齐无限弹量与缺失槽位。用户要求无包时保持正常 HUD，不照搬其常驻全部数值选项。没有复制完整槽位编辑器、颜色渐变或距离字号算法。 |
| StatDisplay 1.1.8，`StatParser`、`IConfiguration`（作者源码）、`StatHandler`；`Data`/`EWCWrapper` | 玩家色简称；默认紧凑的命中率/弱点率（伤害）；原生背包/结算接入；还提供格式 token、分武器/伤害分类及 EWC 接口 | 2.2.8 改为灰色命中率、黄色弱点率、括号伤害，弱点分母改为命中数。用户确认要此口径，不是纯头部命中。自己的协议和收集器仍不等同于原版网络协议/完整格式引擎、EWC 和检查点统计。 |
| Booster Tweaker 1.2.6，`PerfectBooster`、`CustomBooster`、两个自定义 implant manager | 完美数值、模板/效果组/条件组选择、自定义效果与条件、会话消耗拦截；Farmer 路径可将奖励直接设为 100000 | 本模组只覆盖所选的现有词条完美数值、消耗保护、正常收益乘倍率；不会自动创建自定义 Booster、删除条件/负面或改成固定 100000。上述编辑器/玩法差异仍是缺口，不假称完整搬入。 |

用户明确要求的差异：柜子/箱体不建立持续标记，只标已发现的内容；cryo/电池按 `CarryItemPickup_Core.OnSyncStateChange` 的 `PickedUpByPlayer` 跟随队友、放下回物品、完成后消失；自己携带不额外遮挡。这些是明确需求，不宣称是上游默认行为。搬运物一次性重建可读取非激活的携带对象，帧循环不重复搜索全场。

本轮还从真实日志修复了放置预览的 `Dictionary.TryGetValue → System.__Canon..ctor(IntPtr)` 异常：使用游戏 `ItemSpawnManager.GetItemPrefabs` 原生访问接口，并将资源放置修复限定到补给包/消耗品，不能干预任务搬运物的父级和渲染状态。

离线检查与实机验收分开；逐项现场用例见 REVIEW.md。没有因为这张表继续卸载其他模组。

## 功能归属

| 功能 | 负责实现 | 当前状态 |
| --- | --- | --- |
| 命中/弱点/伤害统计 HUD 与结算 | Infini Tweaks，参考 [Dinorush StatDisplay](https://github.com/Dinorush/StatDisplay) 原生界面接入方式 | 用户最新要求内置，覆盖此前 2.2.6 外置方案。2.2.7 自家收集器与 v3 同步协议接入原生紧凑 HUD 和结算文本；无独立大面板、F7 或外部 StatDisplay DLL。队友准确率需匹配 Infini Tweaks，伤害需主机安装；缺失显示破折号。检查点重开归零，不冒充兼容 StatDisplay 协议、EWC 扩展或全套格式/分槽统计。 |
| 同类资源、消耗品堆叠 | Hikaria Core / ResourceStack | 实际 DLL 包含此功能，启动日志确认挂接拾取。主机执行，资源上限 5 次，消耗品使用原生容量，溢出保留。Infini Tweaks 已删除重复算法、设置和合并消息。 |
| 放下资源、放回打开的容器 | Infini Tweaks | 原生放置请求、数量保留、空槽判断、容器关联与剔除缓存刷新；新加空槽吸附、模型预览和原生提示。模型显示与主客机放置仍需实测。 |
| 发现后的物品及终端标记 | Infini Tweaks | 接近且视线通畅时发现拾取物和命名终端实体，终端接近事件及 QUERY/PING；距离/维度/瞄准/拿起/清除过滤。2.2.4 加入分类及设备状态处理，具体见下表。 |
| 手持补给包时的队友百分比 | Infini Tweaks | 仅对应类别放大、加粗、着色；武器状态不显示额外百分比，保留原生姓名/图标。医疗对应血量、弹药对应主副武器、工具包对应工具、消毒对应感染。 |
| 左键补自己、右键补队友 | Archive / L4DStylePacks | 保留现成实现，不在 Infini Tweaks 重复实现。PacksHelper 的 HUD 部分由上面一行覆盖，不能认为只开 L4DStylePacks 就涵盖全部 PacksHelper。 |
| 完美增幅剂、消耗保护、收益倍增 | Infini Tweaks | 现有/历史模板精确匹配，保留原有效果和条件；事件日志记录修改、阻止消耗和收益。倍率收益并不等同于上游强制高额奖励。 |

## 已选择并加入 2.2.4 的能力

| 类别 | 同类模组的能力 | 本次实现及边界 |
| --- | --- | --- |
| 分类标记 | [ResourceHelper](https://github.com/GTFO-Modding/ResourceHelper)：图标、名称、数量、距离与分类配置 | 现有 14 类原生图标/分类设置；2.2.8 按用户要求移除容器类别，修正通用图标覆盖问题。每类独立距离、RGB 颜色，距离 0 关闭该类。使用原生名称/距离，资源包与消耗品显示 ×数量。 |
| 关键设备 | [ItemMarker](https://thunderstore.io/c/gtfo/p/Hikaria/ItemMarker/) 1.1.0：终端、发电机、消毒站、HSU、舱门控制器、门锁 | 按真实交互/同步状态隐藏已完成的发电机、消毒站、HSU 取样/插入装置和控制器。门锁使用交互面板作为标记位置，并显示缺失的钥匙、发电机或控制器。不是全图揭示，仍须发现。 |
| 信息透明度 | [HUDInfoPlus](https://github.com/Mamizu1028/GTFO_CGHUDInfo)：瞄准透明、动态透明度、自动隐藏 | 瞄准时淡化存活队友标记；看向目标时清楚，视线外围淡化。倒地救援标记不淡化。保持手持包上下文筛选，无包时没有额外数值。 |
| 距离 | ResourceHelper：资源与杂物不同距离，PING临时扩大范围 | 2.5.2默认资源40米、消耗品10米；所有类别上限60米。PING后15秒临时范围，附近已知物品每0.5秒刷新7次。没有同zone绕过距离的路径。 |
| 数量 | HUDInfoPlus：包/消耗品显示次数而非百分比 | 手持包时显示队友背包中的非空资源包与消耗品名称及真实次数；没有包时隐藏。血量和枪械/工具储备仍用百分比，方便判断补给对象。 |

以上已实现并编译，但实际设备状态、图标布局、多人发现及透明度仍需实机验收。工具具体名称等未选内容未顺带扩展。没有加入全图未发现物品透视、无限距离、全套常驻资源文字或高频全场扫描。

## 不应当算作等价替代

- [Booster Tweaker](https://thunderstore.io/c/gtfo/p/Hikaria/Booster_Tweaker/) 还提供基于模板的自定义增幅剂，以及无条件、无负面效果、正面效果增幅等独立功能。本模组没有实现这些；完美词条不代表取消条件。强制奖励模式也不等同于正常收益乘以 5。
- [PacksHelper](https://thunderstore.io/c/gtfo/p/Localia/PacksHelper/) 同时包含补给按键和手持包的信息显示，替代时要分开核对。
- [DropItemPlus](https://github.com/Mamizu1028/GTFO_DropItem) 包含原生空槽交互、物品模型预览和放置后的渲染缓存维护。旧版 Infini Tweaks 的位置小加号、状态变更请求不是这些能力的完整替代；2.2.3 修正了对应路径，仍需实际关卡验证。
- PingEverything 的可标记目标扩展不等同于 ResourceHelper/ItemMarker 的“发现后持续记忆”。Archive/Hikaria Core 的 ping 功能不能单独证明持续标记完整可用。

## 部署与验收原则

每个功能只保留一个负责实现。不要因名称相似继续删除外部修复模组；不同问题的修复仍需保留。Archive 的 Hikaria 分支使用完整类型名 GUID 读取 EnabledFeatures，短名称旧键可能不会生效。修改文件也不等同于当前运行进程已经切换状态。

先确认放置后的真实模型和数量、增幅剂实际效果/消耗、HUD 切换与标记状态，再认定替代成功。离线构建和接口匹配不能证明 Unity 画面或多人同步正常。
