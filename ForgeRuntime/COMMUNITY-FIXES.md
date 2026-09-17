# N0 修复类与网络类模组来源盘点（草稿）

> **2026-09-14 用户裁决（框架 D-021），取代下文“收的都是问题与行为规格、不复用任何实现”的口径：** 社区修复不在 Forge 里重写。纯修复包写成依赖；与 Forge 自己持有的副作用或权威状态重叠的条目（例如门请求的主机校验、生命状态切换、敌人血量与弹药耐力同步）由 Forge 实现，依赖包不得对同一副作用重复执行。网络传输走 GTFO-API NetworkAPI（随 BepInExPack_GTFO 自带），Forge 不另起通道。表中其余调研内容保持原样。

## N0 标注汇总（2026-09-15）

主表 28 行「收」全部标注完毕，按子条目计 40 条：`〔Forge 实现〕` 39，`〔依赖〕` 1，`〔待裁定〕` 0。A9 拆成 6 个子条目（原 1 行），C1、E1 沿用原文的“拆成子条目”登记、不另计。

| 标签 | 子条目 |
|---|---|
| 〔Forge 实现〕39 | A1、A2、A4、A5、A6、A7、A8、A9 的 5 个子条目（No Dead Pings、Kill Indicator Fix、Anti Spawn / Anti Gear Hack / Anti Booster Hack、99% Reload Fix、Decay IRF NRE Fix）、B1–B3、C1–C7、D1–D8、E1 |
| 〔依赖〕1 | A3 |
| 〔待裁定〕0 | 无；5 个依赖候选包（A3–A6 与 A9）都是活跃包，没有弃用或仅 Discord 发布的情形，不触发“依赖不可声明” |

**已裁定结论。** 依赖清单只有 `randomuserhi-DamageSync-0.0.7`，挂 `NAinfini-ForgeRuntime-1.0.0`（ForgeRuntime 是其余基础包共同依赖的发行基础包，依赖只在这里声明一次）。A4、A5、A6 和 A9 的 99% Reload 子条目改写或二次校验 Forge 持有的权威状态（客机 HP、敌人位置、主机开火事实、弹匣转移），A9 又无法从整包里单独声明纯修复，因此一律由 Forge 实现。A3 只把主机上已提交的敌人血量复制给客机，不执行伤害本身，保留为依赖；若将来 N4 增量同步自己复制敌人血量，A3 改为 Forge 实现并移除该依赖。28 行全部写明“不得同时装原包”；A3 的 DamageSync 与 A8 的 PlayerSync 会对同一对状态各同步一份，两行互相点名。

## 口径

调研日期 2026-09-13，只做来源盘点，没有实机复现任何一条，没有下载或执行任何 DLL/压缩包，也没有读取模组实现代码（只读 README、包元数据和仓库许可文件）。

**数据来源。** 包列表来自 Thunderstore GTFO 全量 API（[api/v1/package][tsapi]），当日共 1,178 个包，其中 336 个标记为弃用。README 取自 `https://thunderstore.io/api/experimental/package/{作者}/{包名}/{版本}/readme/` 的最新版本。源码许可通过 GitHub API（`/license` 和仓库根目录文件列表）核对；git.takina.io 上的两个仓库用 GitLab API 核对。官方修复情况来自 Steam News API 的 GTFO（appid 493520）官方公告（[接口][steamapi]），共 133 条，时间覆盖 2018-05-28 至 2026-07-25。

**筛选。** 先排除只属于 Rundowns / Modpacks 类别的包，再按包名或最新版描述匹配关键词：fix、bug、patch、sync、desync、network、netcode、lag、late join、host、crash、stuck、softlock、exploit、packet、reconnect、disconnect、replicat*、jitter、rubber*、latency、glitch、nullref、freeze、leak、stutter、broken，得到 125 个。又用 vanilla、issue、hitbox、correct、properly、intended、audio bug、deafen、consistent、archive 等补充词扫出 164 个，人工浏览后补入 13 个。共审阅 138 个包：主表 72 个，剔除附表 66 个。

**字段口径。** 版本为最新版本；更新为包的 `date_updated`；下载为所有版本下载数之和。许可只写能查到的：「未知（仓库无 LICENSE）」表示仓库存在但根目录没有许可文件、GitHub 也未识别出许可，按默认版权保留处理，只能吸收行为规格；「未知（无源码）」表示包没有提供源码链接。「蓝图已引用」表示该包已出现在网站仓库 `catalog/mechanism-blueprints.json` 的 sources 中。

**官方覆盖口径。** 官方最后一次涉及玩法的补丁是 [2024-06-04][p20240604]；[2025-10-17][p20251017] 只是 Unity 安全补丁；此后到 2026-07-25 的公告中没有补丁。表中「未核实（无官方条目）」指全部公告中都没有对应描述，只能说明没被官方记录修过，**不等于已确认仍复现**。

**结论口径。**
- **收**：缺陷描述具体、落在 Forge 责任域内、没有官方覆盖证据，且模组在 R8 之后仍有维护或发布。进入修复清单后仍要按 U-NET 的 N0、N6 复现和验证。收的都是问题与行为规格；除 MIT 许可的 TheArchive 外，不复用任何实现。
- **待核实**：缺陷描述不足、无法独立复现、可能已被官方覆盖，或是缺陷还是设计意图无法判断。
- **不收**：已被官方覆盖、已弃用且被其他条目取代、与 Forge 架构冲突（另起网络通道、人为模拟延迟），或是纯客户端表现改动。

**局限。** README 是作者自述，不能证明当前游戏行为；若干缺陷细节只在 Discord 链接里，无法访问；关键词筛选可能漏掉只在 README 正文里写修复的包；只在 GitHub 或 Discord 发布、未上 Thunderstore 的修复没有覆盖；「纯客户端表现不收」这条是按任务给出的冲突口径执行的，用户已决定暂不收纯客户端表现类修复。

**顺带发现。** 主表中有 11 个修复/网络包已被 `catalog/mechanism-blueprints.json` 当作机制来源引用：AggroFix、hostType6SpawnEnemyFix、PierceBugFix、FireRateFPSFix、RealBackBonus、BetterDoorBulletCollision、NoInterruptions、PlayerSync、Netstat、NetworkQualityTracker、OptionalNetworkAPI。这与 N0「修复清单与机制目录分开维护、单独成表」冲突，应交给 U-BLUEPRINT 处理。

## 主表：候选修复与网络包

### A. 网络同步、权威端与反作弊（U-NET / Runtime）

| # | 包 · 版本 · 更新 · 下载 | 许可 · 源码 | 修的缺陷（症状与条件） | 官方覆盖 / 现状 | 结论与理由 |
|---|---|---|---|---|---|
| A1 | [Dinorush-DoorSyncFix](https://thunderstore.io/c/gtfo/p/Dinorush/DoorSyncFix/) · 1.0.0 · 2026-06-08 · 2,002 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/Dinorush/DoorSyncFix) | 高延迟客机操作安全门时出现门打开后又关上、扫描完成事件执行两次等问题；修法是主机收到客机门包时增加一次主机校验（[README](https://thunderstore.io/api/experimental/package/Dinorush/DoorSyncFix/1.0.0/readme/)） | 未核实（无官方条目） | 〔Forge 实现〕**收**。正是 4.3「同一副作用只由权威端提交一次、重复包显式拒绝」的场景。规格：客机门请求必须经主机状态校验，扫描完成事件按世界代次去重。归属 U-NET（门请求的主机校验）；已核对：本模组的门包校验与 Forge 的门请求校验是同一副作用，不得同时装原包。 |
| A2 | [Dinorush-ReDownFix](https://thunderstore.io/c/gtfo/p/Dinorush/ReDownFix/) · 1.0.1 · 2025-07-18 · 24,306 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/Dinorush/ReDownFix) | 主机侧：客机被救起后、主机确认其存活之前的窗口内，受到任何伤害都会再次倒地（[README](https://thunderstore.io/api/experimental/package/Dinorush/ReDownFix/1.0.1/readme/)） | 未核实（无官方条目） | 〔Forge 实现〕**收**。生命状态切换的权威提交与 lifeEpoch 问题：复活提交后，伤害应作用于新生命期，不能被旧的倒地状态吞掉。归属 U-NET（生命状态提交）；已核对：本模组改写的正是 Forge 提交的生命状态窗口，不得同时装原包。 |
| A3 | [randomuserhi-DamageSync](https://thunderstore.io/c/gtfo/p/randomuserhi/DamageSync/) · 0.0.7 · 2025-07-10 · 64,842 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/randomuserhi/DamageSync) | 原版不向客机同步敌人血量（0.0.5 起也同步部位血量），客机无从知道剩余 HP；模组由主机经 SNet `GameReceiveCritical` 通道下发（[README](https://thunderstore.io/api/experimental/package/randomuserhi/DamageSync/0.0.7/readme/)） | 未核实（无官方条目） | 〔依赖〕**收**（规格）。敌人 HP 和部位 HP 是权威状态，应增量同步给客机。它只把主机上已提交的敌人血量复制给客机，不执行伤害本身；Forge 的伤害由主机提交一次，与复制不是同一副作用。模组自带的通道实现不收，与 Forge 自有网络层冲突。依赖声明：`randomuserhi-DamageSync-0.0.7`，挂 `NAinfini-ForgeRuntime-1.0.0`。若将来 N4 增量同步自己复制敌人血量，此条改为 Forge 实现并移除依赖。不得同时装原包（与 A8 的 PlayerSync 互相点名：两者也会对同一对状态各同步一份）。 |
| A4 | [randomuserhi-KillIndicatorFix](https://thunderstore.io/c/gtfo/p/randomuserhi/KillIndicatorFix/) · 0.2.1 · 2025-09-28 · 66,053 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/randomuserhi/KillIndicatorFix) | 非主机玩家的击杀提示不一致：依赖主机 onkill 回包，有延迟时缺失或迟到。模组做法是客机预测击杀即显示，收到主机死亡事件后去重（3 秒标记寿命、1 秒射击缓冲）；README 自述会直接改写客机内部 HP（[README](https://thunderstore.io/api/experimental/package/randomuserhi/KillIndicatorFix/0.2.1/readme/)） | 未核实（无官方条目）；TheArchive 有同名修复（A9） | 〔Forge 实现〕**收**（问题规格）。它改写客机 HP，而客机 HP 是 Forge 持有的权威状态，等于把本地预测当成已提交结果，违反 4.3 与 N6「依赖包不得对同一副作用重复执行」。规格：预测提示标记为表现层，主机确认后去重。归属 U-NET（客机生命状态）；不得同时装原包。 |
| A5 | [randomuserhi-StaggerSync](https://thunderstore.io/c/gtfo/p/randomuserhi/StaggerSync/) · 0.0.1 · 2024-03-04 · 7,953 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/randomuserhi/GTFOBackFix) | 敌人硬直或攻击动画期间，客机显示位置与主机模拟位置不一致，玩家没碰到敌人也会被碰撞减速，背伤判定也受影响（[README](https://thunderstore.io/api/experimental/package/randomuserhi/StaggerSync/0.0.1/readme/)） | 官方 [2022-08-19][p20220819] "Implemented partial fix for clients experiencing enemy position desync"，只是部分修复；模组发布在其后，是否仍复现未核实 | 〔Forge 实现〕**收**。它改写的敌人位置是 Forge 持有的权威状态（主机模拟位置的客机呈现），与 N6 的重叠不可声明为纯修复。规格：敌人根运动动画期间的位置对账。归属 U-ENEMY（敌人位置对账）；只发过一个版本，必须实机复现；不得同时装原包。 |
| A6 | [randomuserhi-GTFOSilentShotFix](https://thunderstore.io/c/gtfo/p/randomuserhi/GTFOSilentShotFix/) · 0.0.2 · 2024-10-15 · 5,850 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/randomuserhi/GTFOSilentShotFix) | 仅主机：阻止客机"无声射击"。README 没写手法；已弃用的 A18 说明了两种：改数据让枪不可见、利用延迟在切枪瞬间开枪，结果都是枪声不惊醒敌人（[README](https://thunderstore.io/api/experimental/package/randomuserhi/GTFOSilentShotFix/0.0.2/readme/)） | 未核实（无官方条目） | 〔Forge 实现〕**收**。开火噪声和惊醒是游戏状态，应由权威端根据开火事实提交，不依赖客机的本地武器状态；它二次校验的正是主机持有的开火事实，与 N6 的重叠不可声明为纯修复。归属 U-NET（主机开火事实）；不得同时装原包。 |
| A7 | [hirnukuono-NoInterruptions](https://thunderstore.io/c/gtfo/p/hirnukuono/NoInterruptions/) · 0.1.12 · 2026-08-18 · 26,642 · 蓝图已引用 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/Dinorush/NoInterruptions) | 交互会被其他交互或路过的玩家打断。终端问题：离开时仍在输入字符；离开过快时已键入的字符不同步；客机要等一段与 ping 相关的延迟才能输入；上一条命令执行中还能执行新命令；终端变得不可用；队友走近时卡在终端；被移动过的资源位置不更新（[README](https://thunderstore.io/api/experimental/package/hirnukuono/NoInterruptions/0.1.12/readme/)） | 官方 [2021-04-29][p20210429] 修过"终端可瞬间 PING"，[2020-10-22][p20201022] 更新过交互系统；均早于本模组，没有上述条目 | 〔Forge 实现〕**收**（只收终端同步与命令并发部分）。终端输入同步与命令串行化属权威提交。归属 U-NET（终端输入与命令串行化）；"交互不被打断"改变交互规则，按玩法对待，不收；不得同时装原包。 |
| A8 | [food-PlayerSync](https://thunderstore.io/c/gtfo/p/food/PlayerSync/) · 1.1.2 · 2026-05-29 · 2,973 · 蓝图已引用 | 未知（GitLab 仓库根目录无 LICENSE，API license 为空）· [git.takina.io](https://git.takina.io/gtfo/playersync) | 原版不同步其他玩家的弹匣弹药和耐力，只同步总弹药；模组在开火和装填时同步，频率上限 60Hz（[README](https://thunderstore.io/api/experimental/package/food/PlayerSync/1.1.2/readme/)） | 属于原版没实现的同步，不是回归缺陷；无官方条目 | 〔Forge 实现〕**收**（网络能力需求，不是缺陷修复）。Forge 条件节点只要读取他人耐力或弹匣，这两项就必须是权威同步状态，同步频率上限与合批计入带宽预算。归属 U-NET（玩家弹匣与耐力同步）；不得同时装原包（与 A3 的 DamageSync 互相点名：两者也会对同一对状态各同步一份）。 |
| A9 | [AuriRex-TheArchive_Essentials](https://thunderstore.io/c/gtfo/p/AuriRex/TheArchive_Essentials/) · 2025.2.0 · 2025-08-23 · 3,459 | **MIT**（[LICENSE](https://github.com/AuriRex/GTFO_TheArchive/blob/main/LICENSE)，另含 LICENSE_BepInEx）· [GitHub](https://github.com/AuriRex/GTFO_TheArchive) | [Features.md][archivefeat] 的 Fixes 分类：99% Reload Fix（装填后弹匣少一发）、Bio Tracker Small Red Dots、Decay IRF NRE Fix（tank_boss 尸体消散刷 NRE）、Interaction Fix（资源包被路过储物柜等交互打断）、Kill Indicator Fix（客机击杀提示不一致）、No Dead Pings（高延迟下生物标记留在已死敌人上）、Pouncer ScreenFX Stuck Fix（WIP）。Security 分类：Anti Spawn（阻止客机生成敌人）、Anti Gear Hack / Anti Booster Hack（阻止客机使用篡改的装备和增益） | 未核实（无官方条目） | 〔Forge 实现〕**收**（拆条登记，逐子条目标注）：<br>1. No Dead Pings〔Forge 实现〕：高延迟下生物标记留在已死敌人上，标记必须在主机确认死亡后撤销，归属 U-NET（权威端校验）；不得同时装原包。<br>2. Kill Indicator Fix〔Forge 实现〕：与 A4 是同一副作用（客机击杀提示依赖主机死亡事件），已并给 A4 的 U-NET（客机生命状态），不另立实现；不得同时装原包。<br>3. Anti Spawn / Anti Gear Hack / Anti Booster Hack〔Forge 实现〕：客机生成敌人与篡改装备、增益都属反作弊，由权威端校验并拒绝，归属 U-NET（反作弊）；不得同时装原包。<br>4. 99% Reload Fix〔Forge 实现〕：整包混装权威端校验，无法从整包里单独声明为纯修复；规格并入 D4 的弹匣与备弹受限转移规则（与机制 `reserve-magazine` 共用原子），归属 ForgeWeapon（弹匣转移规则）；不得同时装原包。<br>5. Decay IRF NRE Fix〔Forge 实现〕：tank_boss 尸体消散刷 NRE，属资源校验与生命周期释放，归属 ForgeEnemy（资源校验）；E3 以本条为登记来源；不得同时装原包。<br>6. Interaction Fix〔Forge 实现〕：资源包被路过储物柜等交互打断，属交互引用与所有权，归属 U-NET（交互中断判定）；不得同时装原包。<br>MIT 允许复用源码，按 4.3 登记版本与 notice。其余 QoL 和外观功能剔除。 |
| A10 | [Localia-DeadBodyFix](https://thunderstore.io/c/gtfo/p/Localia/DeadBodyFix/) · 3.0.0 · 2022-08-19 · 26,131 | 未知（无源码） | 敌人死后随机一段时间内尸体仍挡子弹；客机因延迟继续向已死敌人开火浪费子弹。README 说客机侧无法完美解决，因为原版不同步血量（[README](https://thunderstore.io/api/experimental/package/Localia/DeadBodyFix/3.0.0/readme/)） | 未核实；2022-08（R7 时期）后未更新，ALT 和 R8 是否改过尸体碰撞未知 | **待核实**。无源码、长期未维护，且与 A3/A4 及 C1 的"近战不能命中尸体"重叠；需实机确认尸体挡弹是否仍存在。 |
| A11 | [AuriRex-FlashlightSyncFixMaybeHopefully](https://thunderstore.io/c/gtfo/p/AuriRex/FlashlightSyncFixMaybeHopefully/) · 2025.1.2 · 2025-07-22 · 1,730 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/AuriRex/GTFO_FlashlightSyncFixMaybeHopefully) | README 只说"可能修复部分手电同步问题"，而且只保证本机手电正确同步给别人；没有复现步骤（[README](https://thunderstore.io/api/experimental/package/AuriRex/FlashlightSyncFixMaybeHopefully/2025.1.2/readme/)） | 未核实 | **待核实**。缺陷描述太模糊，无法独立复现。手电会惊醒敌人（官方 [2022-01-21][p20220121] 修过手电中近距离不惊醒睡眠者），若确实不同步就属于权威状态，需要先复现。 |
| A12 | [Hikaria-HostEnemyLimbDestroyFix](https://thunderstore.io/c/gtfo/p/Hikaria/HostEnemyLimbDestroyFix/) · 1.0.1 · 2024-02-25 · 4,693 · 已弃用 | 未知（源码仓库 GitHub 返回 404） | 客机因延迟，部位销毁不及时，霰弹枪的弹丸能全部打在同一部位上（杀母体尤其明显）；主机却立即销毁部位，主客机伤害结果不一致。做法是把主机的部位销毁推迟到整次霰弹开火之后（[README](https://thunderstore.io/api/experimental/package/Hikaria/HostEnemyLimbDestroyFix/1.0.1/readme/)） | 官方 [2024-03-07][p20240307] "Fixed bug where extra damage was dealt to enemies when destroying their weak spots" 与此相关但不相同；是否覆盖未核实 | **待核实**。已弃用、源码拿不到；需实机确认当前主客机霰弹部位伤害是否仍有差异。 |
| A13 | [randomuserhi-GiveHostClientDamage](https://thunderstore.io/c/gtfo/p/randomuserhi/GiveHostClientDamage/) · 0.0.8 · 2026-09-04 · 3,377 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/randomuserhi/GTFOGiveHostClientDamage) | 与 A12 是同一个主客差异，但方向相反：在主机上人为推迟部位销毁（可选推迟子弹伤害）来模拟客机延迟（[README](https://thunderstore.io/api/experimental/package/randomuserhi/GiveHostClientDamage/0.0.8/readme/)） | 同 A12 | **不收**。人为引入延迟是手感仿真，与 Forge「同一开火事务内的结算时序与主客身份无关」冲突。问题本身并入 A12 复现。 |
| A14 | [Kasuromi-SniperMeleeFix](https://thunderstore.io/c/gtfo/p/Kasuromi/SniperMeleeFix/) · 1.0.0 · 2022-07-12 · 6,847 · 已弃用 | 未知（无源码） | "sniper melee"：客机敌人位置插值在 `PositionSnapshotBuffer.NextPosition` 中用常量 0.5，客机看到的敌人位置滞后，会被远处的敌人近战打中；修法是改内存里的常量（[README](https://thunderstore.io/api/experimental/package/Kasuromi/SniperMeleeFix/1.0.0/readme/)） | 官方 [2022-08-19][p20220819] 部分修复了客机敌人位置不同步；[2022-01-27][p20220127] 修过高延迟客机敌人移动过度抖动 | **不收**。已弃用，官方已部分修复，实现是改原生内存字节。残留的敌人位置不同步并入 A5 复现。 |
| A15 | [Frog-SniperMeleeFix](https://thunderstore.io/c/gtfo/p/Frog/SniperMeleeFix/) · 1.0.6 · 2022-07-12 · 3,609 · 已弃用 | 未知（无源码） | 同 A14；README 自述已被 Kasuromi 版取代（[README](https://thunderstore.io/api/experimental/package/Frog/SniperMeleeFix/1.0.6/readme/)） | 同 A14 | **不收**。理由同 A14。 |
| A16 | [Untilted-SniperMeleeBandaid](https://thunderstore.io/c/gtfo/p/Untilted/SniperMeleeBandaid/) · 0.1.0 · 2022-04-02 · 300 · 已弃用 | 未知（无源码） | 敌人位置偏差超过阈值时强制同步，用抖动换掉 sniper melee（[README](https://thunderstore.io/api/experimental/package/Untilted/SniperMeleeBandaid/0.1.0/readme/)） | 同 A14 | **不收**。已弃用，而且是症状补丁。 |
| A17 | [Kasuromi-NetworkingHotfix](https://thunderstore.io/c/gtfo/p/Kasuromi/NetworkingHotfix/) · 6.6.6 · 2022-04-17 · 606 · 已弃用 | 未知（website 指向无关的 ISP 站点） | README 是玩笑文本，没有缺陷描述。A14 README 说它的做法是关闭插值和整个位置缓冲，导致敌人抖动 | 同 A14 | **不收**。没有可用规格，且已弃用。 |
| A18 | [Hikaria-SilencedWeaponFix](https://thunderstore.io/c/gtfo/p/Hikaria/SilencedWeaponFix/) · 1.0.0 · 2023-06-29 · 2,266 · 已弃用 | 未知（无源码） | 客机"无声枪"两种手法：改数据让枪不可见；利用延迟在切枪瞬间开枪。只需主机安装（[README](https://thunderstore.io/api/experimental/package/Hikaria/SilencedWeaponFix/1.0.0/readme/)） | 未核实 | **不收**。已弃用，由 A6 取代；"改数据"属客户端作弊，归反作弊而不是修复。 |
| A19 | [IsaiahWoods-DamageSync](https://thunderstore.io/c/gtfo/p/IsaiahWoods/DamageSync/) · 0.0.4 · 2023-12-29 · 20 · 已弃用 | 源码链接指向 randomuserhi/DamageSync | A3 旧版本的重新发布（[README](https://thunderstore.io/api/experimental/package/IsaiahWoods/DamageSync/0.0.4/readme/)） | — | **不收**。与 A3 重复。 |
| A20 | [randomuserhi-HeroicHeart](https://thunderstore.io/c/gtfo/p/randomuserhi/HeroicHeart/) · 0.0.1 · 2022-12-17 · 37 · 已弃用 | 未知（无源码） | A4 的前身，描述相同（[README](https://thunderstore.io/api/experimental/package/randomuserhi/HeroicHeart/0.0.1/readme/)） | — | **不收**。已被 A4 取代。 |
| A21 | [Hikaria-The_Archive_UNSTABLE_TEST_ONLY](https://thunderstore.io/c/gtfo/p/Hikaria/The_Archive_UNSTABLE_TEST_ONLY/) · 0.0.7 · 2026-05-07 · 49,134 | MIT（[LICENSE](https://github.com/Mamizu1028/GTFO_TheArchive/blob/doing-things/LICENSE)）· [GitHub](https://github.com/Mamizu1028/GTFO_TheArchive) | A9 的分叉，包描述自述"未经 AuriRex 审核、稳定性不保证、仅供测试" | — | **不收**。修复内容以上游 A9 为登记来源；分叉新增的改动未经审核，不作规格依据。 |
| A22 | [Dinorush-OptionalNetworkAPI](https://thunderstore.io/c/gtfo/p/Dinorush/OptionalNetworkAPI/) · 1.3.0 · 2026-09-12 · 274 · 蓝图已引用 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/Dinorush/OptionalNetworkAPI) | 网络 API 库，不是修复：检测哪些玩家装了某个模组，只向已安装的玩家发送或同步自定义数据，提供玩家加入和模组状态回调（[README](https://thunderstore.io/api/experimental/package/Dinorush/OptionalNetworkAPI/1.3.0/readme/)） | 不适用 | **不收**。这是另一套模组间网络通道与在场检测，与 Forge 自带网络层和能力握手（4.3）职责重叠。可作握手设计参考，尤其是"对端未安装"时的降级回调。 |
| A23 | [Kasuromi-Nidhogg](https://thunderstore.io/c/gtfo/p/Kasuromi/Nidhogg/) · 1.1.1 · 2021-10-02 · 3,575 · 已弃用 | 未知（无源码） | 网络事件 API，要求所有玩家使用同一版本（[README](https://thunderstore.io/api/experimental/package/Kasuromi/Nidhogg/1.1.1/readme/)） | 不适用 | **不收**。已弃用（AoiYuki-DamageIndicator 的更新日志记载改用 GTFO-API 替代），而且是另起的网络通道。 |
| A24 | [randomuserfood-Netstat](https://thunderstore.io/c/gtfo/p/randomuserfood/Netstat/) · 1.0.8 · 2026-08-26 · 2,753 · 蓝图已引用 | 未知（GitLab 仓库根目录无 LICENSE）· [git.takina.io](https://git.takina.io/gtfo/netstat) | 与主机是否安装无关的网络统计：到主机的往返时间、Rx/Tx 字节率、按 6 类 SNet 通道（SessionOrderCritical、SessionMigration、GameOrderCritical、GameReceiveCritical、GameNonCritical、BotCommands）拆分的流量、客户端/服务器 tick 时间（[README](https://thunderstore.io/api/experimental/package/randomuserfood/Netstat/1.0.8/readme/)） | 不适用 | **不收**（不进 Runtime）。诊断工具不是修复，应归 Development。它的通道拆分口径可直接用作 F3N"网络前后对比"的测量维度。 |
| A25 | [Hikaria-NetworkQualityTracker](https://thunderstore.io/c/gtfo/p/Hikaria/NetworkQualityTracker/) · 1.2.3 · 2025-02-13 · 9,820 · 蓝图已引用 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/Tuesday1028/GTFO_NetworkQualityTracker) | 显示延迟、抖动、丢包率（[README](https://thunderstore.io/api/experimental/package/Hikaria/NetworkQualityTracker/1.2.3/readme/)） | 不适用 | **不收**。理由同 A24，只是诊断显示。 |

### B. 迟加入、断线与会话

| # | 包 · 版本 · 更新 · 下载 | 许可 · 源码 | 修的缺陷（症状与条件） | 官方覆盖 / 现状 | 结论与理由 |
|---|---|---|---|---|---|
| B1 | [hirnukuono-InGameJoinFix](https://thunderstore.io/c/gtfo/p/hirnukuono/InGameJoinFix/) · 0.0.1 · 2025-04-28 · 3,147 | 未知（无源码） | 迟加入玩家会听到错误警报声，而触发它的安全门其实还没被拉开；迟加入玩家的地图探索从零开始。只在非主机、远征进行超过 120 秒时生效，靠安全门和弱门状态推断（[README](https://thunderstore.io/api/experimental/package/hirnukuono/InGameJoinFix/0.0.1/readme/)） | 官方 [2022-06-16][p20220616] 修过迟加入时雾和远征时钟不同步，[2022-01-27][p20220127] 修过迟加入者战斗中不进入战斗状态；没有本条两项 | 〔Forge 实现〕**收**。迟加入是 4.3 的一等场景。规格：迟加入时同步环境警报状态和已探索地图状态。归属 U-NET（迟加入快照）；原模组的推断式实现不收，Forge 应下发权威快照；不得同时装原包。 |
| B2 | [JarheadHME-E_Bug_Fix](https://thunderstore.io/c/gtfo/p/JarheadHME/E_Bug_Fix/) · 0.1.1 · 2025-08-07 · 5,797 | 未知（无源码） | "E 键失灵"：正在救的玩家中途断线后，`PlayerInteraction` 保存的交互对象已销毁但接口引用没清空，取消选中时抛 NRE，此后无法与任何物体交互。作者声明只确认了这一条复现路径（[README](https://thunderstore.io/api/experimental/package/JarheadHME/E_Bug_Fix/0.1.1/readme/)） | 未核实（无官方条目） | 〔Forge 实现〕**收**。断线导致的引用未释放。规格：实体离场时释放所有指向它的交互引用。归属 U-NET（实体离场清理）；原模组每 3 秒轮询一次，属症状补丁，实现不收；不得同时装原包。 |
| B3 | [JarheadHME-NoBrokenBotGear](https://thunderstore.io/c/gtfo/p/JarheadHME/NoBrokenBotGear/) · 1.0.0 · 2025-07-22 · 3,302 | 未知（无源码） | 机器人装备存档引用了当前环境里不存在的枪（比如从带自定义枪的模组关卡切回原版），装备卡成空的"MACHINE GUN"且改不掉（[README](https://thunderstore.io/api/experimental/package/JarheadHME/NoBrokenBotGear/1.0.0/readme/)） | 官方 [2024-02-14][p20240214] 修过"切换大厅主机后机器人不生成或没有物品"，是另一个问题 | 〔Forge 实现〕**收**。Forge 会引入自定义装备，一定会触发。规格：加载机器人装备时校验 ID，不存在就清空并报告。归属 U-WEAPON-MOD（机器人装备校验）；不得同时装原包。 |
| B4 | [Hikaria-InheritResourceFix](https://thunderstore.io/c/gtfo/p/Hikaria/InheritResourceFix/) · 1.0.0 · 2023-07-05 · 1,851 · 已弃用 | 未知（无源码） | 玩家或机器人拿着资源离开游戏时资源丢失；模组让主机保存这些资源，由新加入的玩家继承（[README](https://thunderstore.io/api/experimental/package/Hikaria/InheritResourceFix/1.0.0/readme/)） | 官方 [2020-10-23][p20201023] 修过"离开后重进会丢失消耗品"，[2022-06-16][p20220616] 让离开玩家持有的任务物品移到主机脚下；资源包没有条目 | **待核实**。弃用原因不明；"离开玩家的资源消失"可能是设计规则而非缺陷，需先实机确认资源去向，再决定按修复还是规则处理。 |
| B5 | [Inas07-FixEndScreen](https://thunderstore.io/c/gtfo/p/Inas07/FixEndScreen/) · 1.0.0 · 2023-12-13 · 18,107 | 未知（无源码） | README 只有标题"Get me back to lobby pls"和一条 Discord 消息链接，缺陷没有描述；发布于 R8 上线一周内（[README](https://thunderstore.io/api/experimental/package/Inas07/FixEndScreen/1.0.0/readme/)） | 官方 [2024-02-14][p20240214] 有"从检查点重开后回大厅声音不停"，是否相关未知 | **待核实**。缺陷描述拿不到，无法独立复现。 |

### C. 敌人 AI、寻路与生成

| # | 包 · 版本 · 更新 · 下载 | 许可 · 源码 | 修的缺陷（症状与条件） | 官方覆盖 / 现状 | 结论与理由 |
|---|---|---|---|---|---|
| C1 | [Dinorush-EnemyAnimationFix](https://thunderstore.io/c/gtfo/p/Dinorush/EnemyAnimationFix/) · 1.4.8 · 2026-07-24 · 63,226 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/Dinorush/EnemyAnimationFix) | 主客机都有：敌人调用不存在的动画（巨人尖叫、射手攻击、混合体硬直），攻击或尖叫时还在移动。客机：舌头和射击动画提前中断；停下来攻击时位置修正。主机侧（影响所有人）：屏幕外敌人不执行攻击和硬直位移；换动作时不停止射击或舌头；游戏分配不出更多舌头时敌人卡死；玩家站在敌人体内时近战被取消；两个房间以外的敌人接近目标时寻路出错；飞行者跨房间反应；客机 C-foam 状态时长不对；波次敌人可能无限期挂机；第二次及之后的尖叫不惊醒房间；尖叫冷却在两次下潜之间不重置；玩家躲到连通房间的另一扇门后，敌人仍能远程破门；被出生即杀的敌人变得不可杀并永久失效（[README](https://thunderstore.io/api/experimental/package/Dinorush/EnemyAnimationFix/1.4.8/readme/)） | 官方 [2023-09-14][p20230914] 修过"出生时被杀产生幽灵敌人"，但本模组 2026 年版本仍列"出生即杀后不可杀"，说明至少有残留；其他各项没有官方记录 | 〔Forge 实现〕**收**（拆成子条目逐条复现）。大多是主机模拟与客机表现不一致，或状态机没复位，直接属于 Runtime/ForgeEnemy 的权威状态与生命周期。归属 U-ENEMY（敌人状态机与模拟一致性）；不得同时装原包。 |
| C2 | [randomuserhi-ScoutBombingFix](https://thunderstore.io/c/gtfo/p/randomuserhi/ScoutBombingFix/) · 0.0.1 · 2024-05-28 · 5,928 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/randomuserhi/GTFOScoutBombingFix) | 房间被门封闭、敌人 AI 休眠时不重置目标信息，保留着旧的玩家位置，导致 scout bombing（[示例视频](https://www.youtube.com/watch?v=LHGtqTBGG2U)）；AI 在主机模拟，只能主机修（[README](https://thunderstore.io/api/experimental/package/randomuserhi/ScoutBombingFix/0.0.1/readme/)） | 官方 [2022-06-16][p20220616] 修过"主机与敌人之间关门会关闭战斗状态"，是另一个问题 | 〔Forge 实现〕**收**。规格：AI 休眠和唤醒时复位感知状态。归属 U-ENEMY（AI 感知状态）；不得同时装原包。 |
| C3 | [randomuserhi-AggroFix](https://thunderstore.io/c/gtfo/p/randomuserhi/AggroFix/) · 0.0.2 · 2024-07-29 · 13,481 · 蓝图已引用 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/randomuserhi/GTFOAggroFix) | 仅主机：玩家分散在不同区域时，敌人隔着安全门锁定玩家；找不到其他有效目标时锁定已死亡的玩家（[README](https://thunderstore.io/api/experimental/package/randomuserhi/AggroFix/0.0.2/readme/)） | 官方 [2020-11-06][p20201106] 修过早期的"敌人隔门攻击"，没有本条 | 〔Forge 实现〕**收**。Forge 支持玩家分开出生（蓝图已收 Player_Spawn_Apart），一定会遇到。规格：选目标时排除被安全门隔断的区域和已死亡玩家。归属 U-ENEMY（目标选择）；按 4.3 应从蓝图迁到修复清单，须记入「顺带发现」交给 U-BLUEPRINT；不得同时装原包。 |
| C4 | [Zose-hostType6SpawnEnemyFix](https://thunderstore.io/c/gtfo/p/Zose/hostType6SpawnEnemyFix/) · 1.4.0 · 2026-06-30 · 2,535 · 蓝图已引用 | 未知（无源码） | 类型 6（Towards Position / 朝电梯方向）生存波次，有时按主机位置而不是最前方的玩家计算出生点（[README](https://thunderstore.io/api/experimental/package/Zose/hostType6SpawnEnemyFix/1.4.0/readme/)） | 未核实（无官方条目） | 〔Forge 实现〕**收**。出生点计算依赖主机身份，是典型的主机偏置缺陷。规格：以离维度入口最近的玩家为基准。归属 U-ENEMY（出生点计算）；本包已在蓝图 sources 中，须按 4.3 迁到修复清单并交给 U-BLUEPRINT；不得同时装原包。 |
| C5 | [Amorously-SnatcherBugFix](https://thunderstore.io/c/gtfo/p/Amorously/SnatcherBugFix/) · 0.5.0 · 2026-03-26 · 23,199 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/Amorously/SnatcherBugFix) | 打开地图、大厅等菜单页时 pouncer 屏幕效果没应用上，吐出后也不移除；玩家被困在竞技场维度；（主机）生存波次和敌人刷在竞技场维度里；被吐出后移动和环境音频报错（[README](https://thunderstore.io/api/experimental/package/Amorously/SnatcherBugFix/0.5.0/readme/)） | 官方 [2023-12-07][p20231207] 修过"每次从 Snatcher 返回都更新 Warden Intel"，[2022-08-19][p20220819] 修过"客机被抓时不丢下携带物品"；没有本条 | 〔Forge 实现〕**收**，被困与维度内刷怪两项优先。规格：跨维度传送后的状态恢复，以及刷怪空间限制。归属 U-ENEMY（维度传送与刷怪空间）；不得同时装原包。 |
| C6 | [hirnukuono-StuckEnemyFix](https://thunderstore.io/c/gtfo/p/hirnukuono/StuckEnemyFix/) · 0.0.8 · 2026-05-08 · 20,166 | 未知（无源码） | 警报波次敌人刷在家具上卡住不动，战斗音乐和耐力惩罚一直持续。做法：30 秒内没离开出生点 2 米就重新定位并指定最近玩家为目标，重定位 4 次仍不动就杀掉（[README](https://thunderstore.io/api/experimental/package/hirnukuono/StuckEnemyFix/0.0.8/readme/)） | 未核实（无官方条目） | 〔Forge 实现〕**收**问题，实现不收。计时传送和击杀是症状补丁；作者自己建议配合 C7 去掉家具上的导航网格，根因应由 Room 的合法出生空间校验解决。归属 U-ENEMY（刷怪空间校验）；不得同时装原包。 |
| C7 | [hirnukuono-BorkenCellGeoFix](https://thunderstore.io/c/gtfo/p/hirnukuono/BorkenCellGeoFix/) · 0.5.6 · 2026-09-07 · 11,883 | 未知（无源码） | tile 分组漏掉单元格，部分 geo 的地图和导航网格生成不正确；花盆、桌子、箱子等道具上生成导航网格，敌人卡住或卡进墙里；地面缺口导致导航网格断开；另外调整了六边形扫描的填充和敌人重新寻路频率（默认每秒 5 次）（[README](https://thunderstore.io/api/experimental/package/hirnukuono/BorkenCellGeoFix/0.5.6/readme/)） | 未核实（无官方条目） | 〔Forge 实现〕**收**（归 ForgeMap/Room 生成域，不归 U-NET）。按 4.2，地图生成底层逻辑由 Map 持有；"道具上不生成导航网格、单元格分组完整"作为生成规格。归属 ForgeMap（Room 生成规格）；扫描形状和寻路频率是调参，不收；须确认这条生成规格登记进 ForgeMap 还是 U-NET N0 修复清单（待 Claude 复核）；不得同时装原包。 |
| C8 | [hirnukuono-ScoutScreamFix](https://thunderstore.io/c/gtfo/p/hirnukuono/ScoutScreamFix/) · 0.0.1 · 2024-07-04 · 7,000 | 未知（无源码） | "screambug"：侦察者尖叫后一直不死，并每秒重复尖叫几十次。做法：尖叫超过 5 秒就强制切到 receive 状态并取消不死（[README](https://thunderstore.io/api/experimental/package/hirnukuono/ScoutScreamFix/0.0.1/readme/)） | 官方 [2022-11-02][p20221102] 修过"远征失败时侦察者尖叫的致聋效果残留"，是另一个问题 | **待核实**。没给触发条件，修法是超时强切状态；需先找到复现条件。 |
| C9 | [Dinorush-LadderFoamFix](https://thunderstore.io/c/gtfo/p/Dinorush/LadderFoamFix/) · 1.0.1 · 2025-06-29 · 3,841 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/Dinorush/LadderFoamFix) | 仅主机：敌人被 C-foam 冻在梯子上或狭窄通道中间时，其他敌人过不去；模组让敌人可以穿过被冻住的敌人（[README](https://thunderstore.io/api/experimental/package/Dinorush/LadderFoamFix/1.0.1/readme/)） | 未核实（无官方条目） | **待核实**。无法从官方资料确认原生行为是不是设计意图；这个改动会改变敌人之间的碰撞规则，若判定为设计就属玩法，需用户决定。 |
| C10 | [hirnukuono-DesertMarkerFix](https://thunderstore.io/c/gtfo/p/hirnukuono/DesertMarkerFix/) · 0.0.2 · 2025-04-10 · 889 | 未知（无源码） | 面向关卡作者：Desert（crs 47）维度搭配非配套的基础 complex 时，终端不生成或生成异常；做法是把终端 marker 换成对应 complex 的 producer（[README](https://thunderstore.io/api/experimental/package/hirnukuono/DesertMarkerFix/0.0.2/readme/)） | 不适用（原版关卡不会出现这种组合） | **待核实**。只在自定义 complex 与维度组合时触发，需确认 Forge Map 会不会产生这种组合；若会，归 Map 生成域。 |
| C11 | [hirnukuono-StairsFix](https://thunderstore.io/c/gtfo/p/hirnukuono/StairsFix/) · 0.0.2 · 2024-12-22 · 2,548 | 未知（无源码） | Dimension_Desert_Mining_Shaft 因为"一个梯子"不可用；修正其位置（[README](https://thunderstore.io/api/experimental/package/hirnukuono/StairsFix/0.0.2/readme/)） | 未核实 | **待核实**。缺陷位置和症状描述不够；属地图资源域，需实机确认。 |
| C12 | [Flowaria-NoGhostEnemy](https://thunderstore.io/c/gtfo/p/Flowaria/NoGhostEnemy/) · 1.0.8 · 2023-04-01 · 6,577 · 已弃用 | 未知（无源码） | 敌人刚出生就被杀，产生"幽灵敌人"（[README](https://thunderstore.io/api/experimental/package/Flowaria/NoGhostEnemy/1.0.8/readme/)） | 官方 [2023-09-14][p20230914] "Fixed bug where there could be “ghost” enemies if they are killed while spawning" | **不收**。已被官方补丁覆盖；残留情况见 C1。 |

### D. 战斗判定与派生实体

| # | 包 · 版本 · 更新 · 下载 | 许可 · 源码 | 修的缺陷（症状与条件） | 官方覆盖 / 现状 | 结论与理由 |
|---|---|---|---|---|---|
| D1 | [tru0067-PierceBugFix](https://thunderstore.io/c/gtfo/p/tru0067/PierceBugFix/) · 1.2.2 · 2025-10-18 · 29,249 · 蓝图已引用 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/tru0067/GTFO-PierceBugFix) | 穿透射击硬编码最多检测 5 次命中，同一个敌人会占用多次，打宽体敌人时实际穿透数远少于标称；霰弹每次穿透都重新计算散布，越穿越散。修法是改 `BulletWeapon.Fire` / `Shotgun.Fire` 的指令字节（[README](https://thunderstore.io/api/experimental/package/tru0067/PierceBugFix/1.2.2/readme/)） | 官方只在 [2023-04-27][p20230427] 记载 HEL 改为穿透一名敌人，没有修复条目 | 〔Forge 实现〕**收**。命中事务规格：穿透计数按不同敌人算，弹丸沿原轨迹继续飞。归属 ForgeWeapon（命中事务）；字节补丁实现不收；不得同时装原包。 |
| D2 | [Dinorush-FireRateFPSFix](https://thunderstore.io/c/gtfo/p/Dinorush/FireRateFPSFix/) · 1.0.10 · 2026-03-22 · 10,535 · 蓝图已引用 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/Dinorush/FireRateFPSFix) | 武器只在冷却结束后的下一帧才开火，每次冷却都会"溢出"，低帧率时实际射速明显低于标称；模组累计溢出量做补偿，仍不能一帧打多发（[README](https://thunderstore.io/api/experimental/package/Dinorush/FireRateFPSFix/1.0.10/readme/)） | 未核实（无官方条目） | 〔Forge 实现〕**收**。规格：射击节奏与帧率无关（冷却累加器）；归 ForgeWeapon。归属 ForgeWeapon（射击时序）；不得同时装原包。 |
| D3 | [Dinorush-FlickShotFix](https://thunderstore.io/c/gtfo/p/Dinorush/FlickShotFix/) · 1.0.1 · 2024-11-06 · 8,527 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/Dinorush/FlickShotFix) | 原版以 30 FPS 更新枪口指向，快速甩枪打不准，极高射速武器会"成批"打向同一方向；模组每次开火时强制更新（[README](https://thunderstore.io/api/experimental/package/Dinorush/FlickShotFix/1.0.1/readme/)） | 未核实（无官方条目） | 〔Forge 实现〕**收**。规格：开火方向在开火时刻采样；与 D2 合为同一个射击时序条目。归属 ForgeWeapon（射击时序）；不得同时装原包。 |
| D4 | [randomuserhi-DMRReloadFix](https://thunderstore.io/c/gtfo/p/randomuserhi/DMRReloadFix/) · 0.0.6 · 2025-06-14 · 41,976 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/randomuserhi/DMRReloadFix) | DMR 弹匣剩 1 发时装填只到 11 发而不是 12 发；A9 的 99% Reload Fix 把它描述为通用的"弹匣少一发"。模组做法是内部装填两次（[README](https://thunderstore.io/api/experimental/package/randomuserhi/DMRReloadFix/0.0.6/readme/)） | 官方修过个别武器装填不满：[2022-06-27][p20220627] 卡宾枪、[2023-05-09][p20230509] 高口径手枪、[2024-02-14][p20240214] TR22 Hanaway；没有 DMR，也没有通用修复 | 〔Forge 实现〕**收**。规格：弹匣与备弹之间受限转移的整数规则（与机制 `reserve-magazine` 共用原子，但修复登记在修复清单）。归属 ForgeWeapon（弹匣转移规则）；A9 的 99% Reload Fix 并入本条；"装填两次"的实现不收；不得同时装原包。 |
| D5 | [randomuserhi-ShooterBugFix](https://thunderstore.io/c/gtfo/p/randomuserhi/ShooterBugFix/) · 0.0.5 · 2024-02-17 · 10,843 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/randomuserhi/GTFOShooterBug) | Unity 缺陷：场景中有碰撞体飞到极远处时射线检测失效（[Unity issue][unity]），表现为 shooter bug、C-foam 穿门、地雷炸不死玩家。最常见的来源是跨关卡保留、持续下落的弹匣。模组在关卡开始前和每小时剔除离原点过远的碰撞体对象，只对本机生效（[README](https://thunderstore.io/api/experimental/package/randomuserhi/ShooterBugFix/0.0.5/readme/)） | 未核实；E1 的 1.3.9 版（2026-08）仍把 shooter bug 列为修复项 | 〔Forge 实现〕**收**。规格：越界的带碰撞体对象立即回收，关卡结束时清理跨关卡残留物；与 E1 的同名项合并复现。归属 U-NET（越界对象回收）；不得同时装原包。 |
| D6 | [Dinorush-DoorEnemyFixUpdated](https://thunderstore.io/c/gtfo/p/Dinorush/DoorEnemyFixUpdated/) · 1.1.3 · 2026-03-01 · 10,441 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/Dinorush/DoorEnemyFixUpdated) | 门另一侧的敌人会触发绊雷、被 C-foam 粘到；敌人能碰到已经粘在门上的 foam；foam 贴上又被移除后门打不开（[README](https://thunderstore.io/api/experimental/package/Dinorush/DoorEnemyFixUpdated/1.1.3/readme/)；前身 D12 说明了主机需装与使用者需装的区别） | 官方 [2021-04-29][p20210429] "C-foam 不再穿过钢门"与 [2022-12-07][p20221207] 若干地雷与门的修复都早于本模组，没覆盖弱门和血门场景 | 〔Forge 实现〕**收**。规格：门两侧空间判定，以及 foam 状态清理。归属 ForgeWeapon（派生实体空间判定）；主机需装（绊雷、偷胶）和使用者需装（foam 穿门）两类分开验证；不得同时装原包。 |
| D7 | [hirnukuono-GlueFix](https://thunderstore.io/c/gtfo/p/hirnukuono/GlueFix/) · 0.0.8 · 2025-03-03 · 18,548 | 未知（无源码） | 敌人被杀或解冻走开后，foam 块悬在空中不消失；门被摧毁后，粘在门上的 foam 不移除；不在弱门上、存活超过 256 秒的 foam（可能掉出地面）不清理；没释放的 CellSoundPlayer 增加"音频 bug"的概率（[README](https://thunderstore.io/api/experimental/package/hirnukuono/GlueFix/0.0.8/readme/)） | 官方 [2022-06-16][p20220616] 修过"主客机 C-foam 使用不一致"，是另一个问题 | 〔Forge 实现〕**收**。规格：派生实体寿命与宿主解绑（宿主死亡或门被销毁时释放）；音频回收并入 E1。归属 ForgeWeapon（派生实体生命周期）；不得同时装原包。 |
| D8 | [AoiYuki-GlueEfficiency](https://thunderstore.io/c/gtfo/p/AoiYuki/GlueEfficiency/) · 1.1.1 · 2024-03-24 · 5,216 | 未知（无源码） | 胶枪带 Glue Efficiency 增益时，只发射一团不享受消耗减免（效率超过 100% 时，GUI 上的一格本应能打两发）（[README](https://thunderstore.io/api/experimental/package/AoiYuki/GlueEfficiency/1.1.1/readme/)） | 未核实（无官方条目） | 〔Forge 实现〕**收**（低优先）。增益修正应对每一次消耗都生效，属"成本"规则。归属 ForgeWeapon（增益成本规则）；不得同时装原包。 |
| D9 | [Localia-RealBackBonus](https://thunderstore.io/c/gtfo/p/Localia/RealBackBonus/) · 3.0.0 · 2022-08-21 · 4,110 · 蓝图已引用 | 未知（无源码） | 背伤方向用的是对象朝向，而不是脊柱骨骼的水平方向，敌人扭身时判定与姿态不符；近战距离太近时拿不到背伤加成（[README](https://thunderstore.io/api/experimental/package/Localia/RealBackBonus/3.0.0/readme/)） | 未核实；A5 README 说 StaggerSync 也"顺带"修了背伤 | **待核实**。2022 年后没更新；"用对象朝向"是不是设计口径不清楚，只有"近战过近拿不到加成"像是明确缺陷，复现后再拆分。 |
| D10 | [Flowaria-BetterDoorBulletCollision](https://thunderstore.io/c/gtfo/p/Flowaria/BetterDoorBulletCollision/) · 1.0.1 · 2023-04-01 · 18,760 · 蓝图已引用 | 未知（无源码） | 包描述是"让破损门的子弹碰撞变准确"，README 正文只有一个词"Please"（[README](https://thunderstore.io/api/experimental/package/Flowaria/BetterDoorBulletCollision/1.0.1/readme/)） | 未核实 | **待核实**。README 没有内容、没有源码，得不出缺陷规格。 |
| D11 | [randomuserhi-BioScannerFix](https://thunderstore.io/c/gtfo/p/randomuserhi/BioScannerFix/) · 0.0.2 · 2025-08-03 · 15,472 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/randomuserhi/GTFOBioScannerFix) | 生物追踪器上的敌人显示成很小的红点；0.0.2 修"受这个 bug 影响的侦察者无法标记"。README 致谢 JarheadHME 找到了稳定复现方式，但没写步骤（[README](https://thunderstore.io/api/experimental/package/randomuserhi/BioScannerFix/0.0.2/readme/)） | 未核实；A9 有同名修复 | **待核实**。复现步骤没公开；红点本身是表现问题，但"无法标记"会影响玩法，复现后再判断。 |
| D12 | [Localia-DoorEnemyFix](https://thunderstore.io/c/gtfo/p/Localia/DoorEnemyFix/) · 1.0.0 · 2023-11-16 · 18,273 · 已弃用 | 未知（无源码） | 与 D6 前三项相同（[README](https://thunderstore.io/api/experimental/package/Localia/DoorEnemyFix/1.0.0/readme/)） | — | **不收**。已被 D6 用更高版本号取代（D6 README 有说明）。 |
| D13 | [randomuserhi-HelAutoSentryFix](https://thunderstore.io/c/gtfo/p/randomuserhi/HelAutoSentryFix/) · 0.0.4 · 2023-11-08 · 992 · 已弃用 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/randomuserhi/GTFO-Hel-Auto-Sentry-Fix) | 哨戒炮开火代码缺少 HEL 穿透逻辑，HEL 自动哨戒炮不能穿透（[README](https://thunderstore.io/api/experimental/package/randomuserhi/HelAutoSentryFix/0.0.4/readme/)） | 官方 [2023-12-07][p20231207] "Fixed bug where Sentry Guns were not able to use bullet penetration" | **不收**。已被官方补丁覆盖。 |
| D14 | [Hikaria-ReceiveAmmoGiveFix](https://thunderstore.io/c/gtfo/p/Hikaria/ReceiveAmmoGiveFix/) · 1.1.0 · 2023-12-15 · 23,489 · 已弃用 | 未知（无源码） | 弹匣不满时补给加不满；弹药不平衡时溢出浪费，模组把溢出部分按效率转给另一把枪（[README](https://thunderstore.io/api/experimental/package/Hikaria/ReceiveAmmoGiveFix/1.1.0/readme/)） | 未核实 | **不收**。已弃用；"溢出转给另一把枪"是玩法改动；"加不满"与 D4 同类，并入 D4 复现。 |
| D15 | [Frog-BackDamageFix](https://thunderstore.io/c/gtfo/p/Frog/BackDamageFix/) · 0.0.1 · 2021-12-31 · 1,206 · 已弃用 | 未知（website 为无关站点） | 包描述是"修复原版背伤没有按设计工作"，README 只有致谢（[README](https://thunderstore.io/api/experimental/package/Frog/BackDamageFix/0.0.1/readme/)） | — | **不收**。已弃用、早于 R6、没有缺陷描述；背伤问题见 D9。 |
| D16 | [Untilted-EvadeFix](https://thunderstore.io/c/gtfo/p/Untilted/EvadeFix/) · 1.0.2 · 2022-08-19 · 19,488 | 未知（无源码） | R6 加入速度上限后，闪避被限制在冲刺速度（[README](https://thunderstore.io/api/experimental/package/Untilted/EvadeFix/1.0.2/readme/)） | 官方 [2023-12-14][p20231214] "Added dodge mechanic back, consuming stamina on use"，重新实现了闪避 | **不收**。前提已被官方改写，包从 2022-08 起没更新；当前闪避速度是否仍受限未核实。 |
| D17 | [Flowaria-BetterEnemyHitbox](https://thunderstore.io/c/gtfo/p/Flowaria/BetterEnemyHitbox/) · 1.0.1 · 2023-05-13 · 3,239 · 已弃用 | 未知（无源码） | 缩小 Birther 身体、Tank 头部与身体、Shooter 颈部的命中盒；另提到"tank 预制体导致部位不同步"（[README](https://thunderstore.io/api/experimental/package/Flowaria/BetterEnemyHitbox/1.0.1/readme/)） | — | **不收**。已弃用；主体是命中盒尺寸调整，属平衡；"部位不同步"没有细节，无法复现。 |

### E. 生命周期、资源泄漏与性能

| # | 包 · 版本 · 更新 · 下载 | 许可 · 源码 | 修的缺陷（症状与条件） | 官方覆盖 / 现状 | 结论与理由 |
|---|---|---|---|---|---|
| E1 | [Dinorush-MemoryLeakFix](https://thunderstore.io/c/gtfo/p/Dinorush/MemoryLeakFix/) · 1.3.9 · 2026-08-28 · 34,722 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/Dinorush/MemoryLeakFix) | 敌人尸体消散效果既不复用也不销毁（可能是长时间游玩后性能下降的主要原因）；shooter bug；若干声音播放器不清理；敌人死亡或关卡结束时多个对象不清理；IRF 渲染低效；附近敌人和物体太多时 WindVolumeCamera 刷错误；消毒站导致掉帧；关卡清理时不移除终端的关卡构建回调；切换近战武器后发出错误的网络包（[README](https://thunderstore.io/api/experimental/package/Dinorush/MemoryLeakFix/1.3.9/readme/)） | 官方 [2024-02-14][p20240214] 修过"从检查点重开后回大厅声音不停"，[2022-01-27][p20220127] 修过"声音可能停止播放"；都不涵盖本条各项 | 〔Forge 实现〕**收**。生命周期释放是 Runtime 职责；"切换近战武器后发出错误网络包"直接属于 U-NET。归属 U-NET（生命周期释放；错误网络包）；各子项分开复现；不得同时装原包。 |
| E2 | [Flowaria-SoundMemoryLeakFix](https://thunderstore.io/c/gtfo/p/Flowaria/SoundMemoryLeakFix/) · 1.2.71 · 2022-06-19 · 9,809 · 已弃用 | 未知（无源码） | 大量声音同时播放、或多次生成关卡后，降低"致聋 bug"的出现概率（[README](https://thunderstore.io/api/experimental/package/Flowaria/SoundMemoryLeakFix/1.2.71/readme/)） | — | **不收**。已弃用；同方向由 E1 和 D7 在更新的版本中覆盖。 |
| E3 | [AuriRex-Decay_IRF_NRE_Fix](https://thunderstore.io/c/gtfo/p/AuriRex/Decay_IRF_NRE_Fix/) · 1.0.0 · 2025-05-13 · 1,325 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/AuriRex/GTFO_DecayIRFFix) | tank_boss 预制体上的无效 IRF 组件在尸体消散时刷 NullReferenceException（[README](https://thunderstore.io/api/experimental/package/AuriRex/Decay_IRF_NRE_Fix/1.0.0/readme/)） | 未核实（无官方条目） | **不收**（作为独立来源）。与 A9 TheArchive 的同名修复重复，以 MIT 许可的 A9 为登记来源，归 ForgeEnemy 资源校验。 |

### F. 纯客户端表现与 UI 修复

| # | 包 · 版本 · 更新 · 下载 | 许可 · 源码 | 修的缺陷（症状与条件） | 官方覆盖 / 现状 | 结论与理由 |
|---|---|---|---|---|---|
| F1 | [Dinorush-GalaxyBugFix](https://thunderstore.io/c/gtfo/p/Dinorush/GalaxyBugFix/) · 1.0.1 · 2025-11-03 · 5,172 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/Dinorush/GalaxyBugFix) | 遮挡剔除逻辑误判玩家所在区域，整个世界在画面上消失（Galaxy Bug）（[README](https://thunderstore.io/api/experimental/package/Dinorush/GalaxyBugFix/1.0.1/readme/)） | 未核实（无官方条目） | **不收**。纯客户端渲染，不涉及提交状态，不属 Runtime/U-NET。影响很严重，若决定另设"表现层修复"类别，应排第一。 |
| F2 | [Dinorush-ThermalAlignFix](https://thunderstore.io/c/gtfo/p/Dinorush/ThermalAlignFix/) · 1.1.0 · 2025-11-24 · 1,474 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/Dinorush/ThermalAlignFix) | 原版 PDW 和精确步枪的热成像瞄具偏离屏幕中心（[README](https://thunderstore.io/api/experimental/package/Dinorush/ThermalAlignFix/1.1.0/readme/)） | 未核实 | **不收**。纯表现（瞄具模型位置）。 |
| F3 | [Amorously-RespawnSackBarcodeFix](https://thunderstore.io/c/gtfo/p/Amorously/RespawnSackBarcodeFix/) · 1.0.1 · 2025-05-09 · 9,628 | 未知（无源码） | 重生囊和杂物的血迹贴花旋转了 90 度，看起来像条形码（[README](https://thunderstore.io/api/experimental/package/Amorously/RespawnSackBarcodeFix/1.0.1/readme/)） | 未核实 | **不收**。纯表现。 |
| F4 | [Ribbit-SustainScanPentagonFix](https://thunderstore.io/c/gtfo/p/Ribbit/SustainScanPentagonFix/) · 0.0.1 · 2026-06-23 · 209 | 未知（无源码） | 持续扫描的内部几何与外形不对齐，README 自述"纯视觉"，是从 C7 拆出的独立版本（[README](https://thunderstore.io/api/experimental/package/Ribbit/SustainScanPentagonFix/0.0.1/readme/)） | 未核实 | **不收**。纯表现。 |
| F5 | [Andocas-TerminalPingFix](https://thunderstore.io/c/gtfo/p/Andocas/TerminalPingFix/) · 0.1.0 · 2026-02-13 · 2,244 | 未知（无源码） | 上一个 PING 指示还在显示时再次使用 PING（包括 PING -T），指示会提前淡出；另外把淡出时长做成可配置，并允许多个指示同时存在（[README](https://thunderstore.io/api/experimental/package/Andocas/TerminalPingFix/0.1.0/readme/)） | 未核实 | **不收**。客户端表现修复，外加 QoL 配置。 |
| F6 | [hirnukuono-WardenIntelOhBehave](https://thunderstore.io/c/gtfo/p/hirnukuono/WardenIntelOhBehave/) · 0.0.1 · 2024-01-16 · 17,100 | 未知（无源码） | 同一会话内开始新远征时，Warden Intel 不再显示（[README](https://thunderstore.io/api/experimental/package/hirnukuono/WardenIntelOhBehave/0.0.1/readme/)） | 官方 [2023-12-07][p20231207] 修过相关但不同的 Warden Intel 问题；当前是否复现未核实 | **不收**。纯 UI 表现。 |
| F7 | [JarheadHME-RundownExtensionFix](https://thunderstore.io/c/gtfo/p/JarheadHME/RundownExtensionFix/) · 1.0.0 · 2024-10-04 · 4,272 | 未知（无源码） | 切换 rundown 或热重载时，Extended Protocol 的图标列表不清理，出现重复（[README](https://thunderstore.io/api/experimental/package/JarheadHME/RundownExtensionFix/1.0.0/readme/)） | 不适用 | **不收**。菜单 UI，只影响开发和多 rundown 切换，与 Runtime 无关。 |
| F8 | [Flowaria-GavelIconFixer](https://thunderstore.io/c/gtfo/p/Flowaria/GavelIconFixer/) · 1.0.0 · 2021-10-12 · 1,572 · 已弃用 | 未知（无源码） | 包描述"Give my Gavel Icon back"，README 只写了作者（[README](https://thunderstore.io/api/experimental/package/Flowaria/GavelIconFixer/1.0.0/readme/)） | 官方 [2023-12-07][p20231207] "Fixed bug where the melee weapons Maul and Gavel had the same icons"，描述吻合，推定已覆盖 | **不收**。已弃用，推定已被官方覆盖。 |
| F9 | [randomuserhi-AMDScanLineFix](https://thunderstore.io/c/gtfo/p/randomuserhi/AMDScanLineFix/) · 0.0.4 · 2025-09-20 · 674 · 已弃用 | 未知（仓库无 LICENSE）· [GitHub](https://github.com/randomuserhi/GTFODebugScanLines) | 改用另一条代码路径绘制全息路径，避开 AMD 显卡驱动的问题（[README](https://thunderstore.io/api/experimental/package/randomuserhi/AMDScanLineFix/0.0.4/readme/)） | 不适用 | **不收**。已弃用；是驱动兼容的表现问题。 |
| F10 | [hirnukuono-FreeThySelf](https://thunderstore.io/c/gtfo/p/hirnukuono/FreeThySelf/) · 0.0.3 · 2026-01-18 · 3,081 | 未知（无源码） | 玩家卡进洞里或掉进出不来的位置时，按键尝试脱困（10 秒冷却，倒地时不可用）（[README](https://thunderstore.io/api/experimental/package/hirnukuono/FreeThySelf/0.0.3/readme/)） | 不适用 | **不收**。玩家自救传送是绕过手段，不修根因；根因归地图碰撞与导航（C7、C11）。 |

### 结论统计

主表 72 个：收 28，待核实 12，不收 32。

- 收（28）：A1–A9，B1–B3，C1–C7，D1–D8，E1。
- 待核实（12）：A10–A12，B4–B5，C8–C11，D9–D11。
- 不收（32）：A13–A25，C12，D12–D17，E2–E3，F1–F10。

## 剔除附表：玩法 / QoL / 非修复 / 范围外

| 包 · 版本 · 更新 · 下载 | 剔除理由 |
|---|---|
| [hirnukuono-EEC_H](https://thunderstore.io/c/gtfo/p/hirnukuono/EEC_H/) · 1.8.29 · 2026-07-22 · 57,803 | 属 20 个玩法基础包（EEC），本次范围外。README 自述含"R8 以来若干热修"，其中的修复项应由 U-ENEMY 在基础包移植时登记到修复清单。 |
| [Dinorush-ExtraToolCustomization](https://thunderstore.io/c/gtfo/p/Dinorush/ExtraToolCustomization/) · 1.7.1 · 2026-04-12 · 26,694 | 属 20 个基础包（ETC），本次范围外。README 列出的修复不要遗漏：工具弹药超过 100% 时收回后重置为 100%；哨戒炮打空后补弹会立即开一枪；Burst 哨戒炮跨激活保留剩余连发、初次锁定时间过长（[README](https://thunderstore.io/api/experimental/package/Dinorush/ExtraToolCustomization/1.7.1/readme/)）。建议 U-WEAPON-MOD 移植时登记。 |
| [Dinorush-ItemSpawnFix](https://thunderstore.io/c/gtfo/p/Dinorush/ItemSpawnFix/) · 1.3.1 · 2026-06-12 · 15,605 | 属 20 个基础包，本次范围外。它修的是"资源、消耗品、大件物品因空间不足不生成"和"Surprise! 箱不工作"（[README](https://thunderstore.io/api/experimental/package/Dinorush/ItemSpawnFix/1.3.1/readme/)），建议 U-MAP-MOD 登记。 |
| [Localia-ModList](https://thunderstore.io/c/gtfo/p/Localia/ModList/) · 3.0.0 · 2022-08-19 · 67,709 | 在大厅显示每个人的模组列表和延迟，属信息展示；可作能力握手 UI 参考。 |
| [AoiYuki-DamageIndicator](https://thunderstore.io/c/gtfo/p/AoiYuki/DamageIndicator/) · 0.4.10 · 2022-10-01 · 22,737 | 主机侧伤害统计显示；更新日志里的 fix 修的是模组自身问题。 |
| [Frog-SilencedWeapons](https://thunderstore.io/c/gtfo/p/Frog/SilencedWeapons/) · 1.1.5 · 2022-08-19 · 28,822 | 消音器配置属玩法；描述里的"修复隔着区域门惊醒"没有细节（蓝图已引用）。 |
| [Dinorush-DropItemFixed](https://thunderstore.io/c/gtfo/p/Dinorush/DropItemFixed/) · 0.1.8 · 2026-07-01 · 22,320 | 把物品放回储物柜属玩法功能；"fixes"指修模组自身（蓝图已引用）。 |
| [Dex-DropResource](https://thunderstore.io/c/gtfo/p/Dex/DropResource/) · 1.0.2 · 2022-07-07 · 12,577 | 同上，玩法功能，关键词误匹配（"has some bugs"）。 |
| [Fody55-GTFO_DropResources](https://thunderstore.io/c/gtfo/p/Fody55/GTFO_DropResources/) · 1.0.0 · 2022-05-29 · 537 · 已弃用 | 同上。 |
| [Fody55-GTFO_BringBackBunnyHop](https://thunderstore.io/c/gtfo/p/Fody55/GTFO_BringBackBunnyHop/) · 2.0.1 · 2022-08-22 · 12,687 | 恢复已被移除的连跳机制，属玩法。 |
| [GTFOModding-ScanPositionOverride](https://thunderstore.io/c/gtfo/p/GTFOModding/ScanPositionOverride/) · 1.0.0 · 2023-01-11 · 6,736 | 关卡作者的扫描位置配置；"desync"指作者没统一安装时的后果（蓝图已引用）。 |
| [Inas07-ScanPositionOverride](https://thunderstore.io/c/gtfo/p/Inas07/ScanPositionOverride/) · 1.1.1 · 2023-01-31 · 335 · 已弃用 | 同上。 |
| [GraybNCarb-CarboFix](https://thunderstore.io/c/gtfo/p/GraybNCarb/CarboFix/) · 2.2.0 · 2026-02-06 · 6,471 | 武器平衡改动，名字里的 Fix 是误匹配。 |
| [GraybNCarb-CarboFix_fin](https://thunderstore.io/c/gtfo/p/GraybNCarb/CarboFix_fin/) · 2.1.1 · 2026-02-06 · 8 · 已弃用 | 同上。 |
| [GrongusMods-Grongus_Vanilla_Overhaul](https://thunderstore.io/c/gtfo/p/GrongusMods/Grongus_Vanilla_Overhaul/) · 1.6.1 · 2026-07-17 · 3,976 | 平衡与沉浸感大改；装填时序等"修正"混在平衡改动里。 |
| [ProjectZaero-Vanilla_QoL_Modpack](https://thunderstore.io/c/gtfo/p/ProjectZaero/Vanilla_QoL_Modpack/) · 1.2.1 · 2024-01-15 · 3,947 | 模组合集，不含自身修复。 |
| [notpeelz-QoLFix](https://thunderstore.io/c/gtfo/p/notpeelz/QoLFix/) · 0.3.2 · 2021-03-20 · 4,817 · 已弃用 | LGPL-3.0（[LICENSE](https://github.com/notpeelz/GTFO-QoLFix/blob/master/LICENSE)），仓库已归档，R5 之前。以 QoL 为主，2021-03 后停更，早于 R6 至 R8 的大量改动，不作修复规格来源。 |
| [Dinorush-InheritanceDataBlocks](https://thunderstore.io/c/gtfo/p/Dinorush/InheritanceDataBlocks/) · 1.1.6 · 2025-07-05 · 5,791 | 开发工具（数据块继承）。 |
| [hirnukuono-AWOPartialDataFixer](https://thunderstore.io/c/gtfo/p/hirnukuono/AWOPartialDataFixer/) · 1.5.1 · 2024-07-29 · 4,028 · 已弃用 | 开发数据加载库。 |
| [Red_Leicester_Cheese-AdditiveDatablockLoader](https://thunderstore.io/c/gtfo/p/Red_Leicester_Cheese/AdditiveDatablockLoader/) · 0.0.2 · 2026-01-29 · 182 | 开发工具。 |
| [hirnukuono-DoorPanelFix](https://thunderstore.io/c/gtfo/p/hirnukuono/DoorPanelFix/) · 0.0.3 · 2024-07-06 · 5,348 | 门按钮交互距离改为 1 米，属交互手感调整。 |
| [hirnukuono-TumorShadowFix](https://thunderstore.io/c/gtfo/p/hirnukuono/TumorShadowFix/) · 0.1.0 · 2026-06-24 · 4,397 | 给肿瘤开启阴影、把敌人配置成暗影变体，属表现与新功能。 |
| [HazardousMonkey-Informative_Door_Icons](https://thunderstore.io/c/gtfo/p/HazardousMonkey/Informative_Door_Icons/) · 1.3.2 · 2026-04-22 · 4,382 | 地图门图标 QoL（蓝图已引用）。 |
| [Inas07-EOSExt_ExtraDoor](https://thunderstore.io/c/gtfo/p/Inas07/EOSExt_ExtraDoor/) · 1.0.1 · 2025-05-28 · 4,285 | 关卡功能扩展，关键词误匹配（蓝图已引用）。 |
| [Inas07-EOSExt_EnvTemperature](https://thunderstore.io/c/gtfo/p/Inas07/EOSExt_EnvTemperature/) · 1.0.4 · 2025-05-23 · 3,611 | 关卡功能扩展（"freeze"误匹配）（蓝图已引用）。 |
| [hirnukuono-RundownTitleFix](https://thunderstore.io/c/gtfo/p/hirnukuono/RundownTitleFix/) · 0.0.1 · 2024-10-15 · 3,185 | 启用原版关掉的 rundown 标题，不是缺陷。 |
| [JarheadHME-ChangeHostSlot](https://thunderstore.io/c/gtfo/p/JarheadHME/ChangeHostSlot/) · 1.1.1 · 2025-07-05 · 2,646 | 改主机槽位，属 QoL；MIT。 |
| [zooooox-ReloadTimer](https://thunderstore.io/c/gtfo/p/zooooox/ReloadTimer/) · 1.5.0 · 2026-04-09 · 2,537 | HUD 功能（"synced"误匹配）。 |
| [Zose-ReloadTimerPlus](https://thunderstore.io/c/gtfo/p/Zose/ReloadTimerPlus/) · 1.1.0 · 2026-06-26 · 629 | 同上。 |
| [Andocas-AchievementHelper](https://thunderstore.io/c/gtfo/p/Andocas/AchievementHelper/) · 0.1.1 · 2024-08-22 · 1,920 | 成就辅助；它修的 PlayFab 与 Steam 调用部分失败属平台成就，Forge 不承担。 |
| [hirnukuono-NoCrankNoStart](https://thunderstore.io/c/gtfo/p/hirnukuono/NoCrankNoStart/) · 1.0.0 · 2026-08-29 · 1,624 | 完全禁用 START 命令，是规则改动；客机执行 START 的漏洞官方 [2024-06-04][p20240604] 已修。 |
| [AoiYuki-ConnectIPV6](https://thunderstore.io/c/gtfo/p/AoiYuki/ConnectIPV6/) · 0.4.0 · 2023-08-20 · 1,332 | 终端上行链路 IPv6 格式，是谜题玩法，与联网无关。 |
| [DarkEmperor-Old_Combat_Music_Fix](https://thunderstore.io/c/gtfo/p/DarkEmperor/Old_Combat_Music_Fix/) · 1.0.0 · 2024-10-31 · 1,269 | 修的是另一个模组。 |
| [DarkEmperor-MapperFix](https://thunderstore.io/c/gtfo/p/DarkEmperor/MapperFix/) · 0.0.2 · 2026-07-21 · 285 | 恢复未发布的 Mapper 工具，属新功能。 |
| [Aak-MapperTracker](https://thunderstore.io/c/gtfo/p/Aak/MapperTracker/) · 2.4.20 · 2026-09-12 · 23 | 恢复追踪器 mapper 模式，属新功能。 |
| [the_tavern-KillableSpitters](https://thunderstore.io/c/gtfo/p/the_tavern/KillableSpitters/) · 1.1.0 · 2026-07-29 · 1,223 | 让 spitter 可被杀，属玩法（蓝图已引用）。附带的"spitter 不再锁定 AI 队友、超过 4 人大厅正确处理"没有复现细节。 |
| [Panthr75-MushroomSeedFixed](https://thunderstore.io/c/gtfo/p/Panthr75/MushroomSeedFixed/) · 1.2.1 · 2025-12-08 · 1,203 | 种子锁定工具（蓝图已引用）。 |
| [Memorial_Standing-SCOLD_Solo_Conversion_Overhaul_Lowered_Datablocks](https://thunderstore.io/c/gtfo/p/Memorial_Standing/SCOLD_Solo_Conversion_Overhaul_Lowered_Datablocks/) · 0.0.8 · 2025-03-24 · 1,194 | 单人难度数据块改动。 |
| [Memorial_Standing-DCOLD_Duo_Conversion_Overhaul_Lowered_Datablocks](https://thunderstore.io/c/gtfo/p/Memorial_Standing/DCOLD_Duo_Conversion_Overhaul_Lowered_Datablocks/) · 0.0.10 · 2025-03-24 · 930 | 双人难度数据块改动。 |
| [Memorial_Standing-TCOLD_Trio_Conversion_Overhaul_Lowered_Datablocks](https://thunderstore.io/c/gtfo/p/Memorial_Standing/TCOLD_Trio_Conversion_Overhaul_Lowered_Datablocks/) · 0.0.1 · 2025-03-25 · 334 | 三人难度数据块改动。 |
| [BrokenArms-BrokenArms_GTFO](https://thunderstore.io/c/gtfo/p/BrokenArms/BrokenArms_GTFO/) · 1.0.3 · 2024-11-02 · 313 | 降难度数据块。 |
| [mythicaltea-TrickyBalls_Paradise](https://thunderstore.io/c/gtfo/p/mythicaltea/TrickyBalls_Paradise/) · 1.0.22 · 2026-01-16 · 298 | 关卡内容（"Fixed F2B"指关卡）。 |
| [hirnukuono-OldSchoolGraphics](https://thunderstore.io/c/gtfo/p/hirnukuono/OldSchoolGraphics/) · 0.1.1 · 2024-01-21 · 1,149 | 画面风格模组的重编译（为兼容检查点）。 |
| [AuriRex-All_Levels_Unlocked](https://thunderstore.io/c/gtfo/p/AuriRex/All_Levels_Unlocked/) · 1.0.0 · 2025-11-23 · 923 | 解锁关卡。 |
| [I_N_F-ExpeditionUnlock](https://thunderstore.io/c/gtfo/p/I_N_F/ExpeditionUnlock/) · 1.0.0 · 2026-04-06 · 153 | 解锁关卡。 |
| [Kasuromi-Lockout_3](https://thunderstore.io/c/gtfo/p/Kasuromi/Lockout_3/) · 0.0.2 · 2023-12-15 · 739 | 关卡资源包。 |
| [mccad00-LockoutWeaponPack](https://thunderstore.io/c/gtfo/p/mccad00/LockoutWeaponPack/) · 91.0.1 · 2021-10-15 · 4,776 · 已弃用 | 武器测试包。 |
| [goobers-Goobers_Custom_FPS_Anims](https://thunderstore.io/c/gtfo/p/goobers/Goobers_Custom_FPS_Anims/) · 0.0.1 · 2023-02-25 · 335 | 动画资源。 |
| [JarheadHME-FastLockMelter](https://thunderstore.io/c/gtfo/p/JarheadHME/FastLockMelter/) · 1.0.0 · 2024-12-08 · 639 | 跳过熔锁动画，属 QoL。 |
| [RoboRyGuy-BetaArchipelago](https://thunderstore.io/c/gtfo/p/RoboRyGuy/BetaArchipelago/) · 0.0.6 · 2026-09-03 · 547 | Archipelago 随机器集成。 |
| [Aster0472-LobbyListRemake](https://thunderstore.io/c/gtfo/p/Aster0472/LobbyListRemake/) · 1.0.2 · 2026-08-06 · 519 | 把房间发布到自建网站的大厅列表，是外部服务，不是修复。 |
| [randomuserhi-StaminaDebug](https://thunderstore.io/c/gtfo/p/randomuserhi/StaminaDebug/) · 0.0.1 · 2024-07-29 · 339 | 耐力曲线调试显示。 |
| [randomuserhi-MindControl](https://thunderstore.io/c/gtfo/p/randomuserhi/MindControl/) · 0.0.6 · 2026-08-02 · 253 | 通过回放查看器控制敌人（蓝图已引用）。 |
| [Frog-MultipleReactorsFix](https://thunderstore.io/c/gtfo/p/Frog/MultipleReactorsFix/) · 1.0.0 · 2021-11-01 · 294 · 已弃用 | 支持一关多个反应堆，属功能扩展。 |
| [KainKondraki-Hostile_Mine](https://thunderstore.io/c/gtfo/p/KainKondraki/Hostile_Mine/) · 1.1.0 · 2026-09-02 · 177 | 敌对地雷玩法（蓝图已引用）。 |
| [bingak-SharedStamina](https://thunderstore.io/c/gtfo/p/bingak/SharedStamina/) · 0.1.2 · 2026-07-17 · 85 | 共享耐力玩法（蓝图已引用）；它的主机权威加客机预测对账可作网络实现参考。 |
| [hirnukuono-BorderlessFix](https://thunderstore.io/c/gtfo/p/hirnukuono/BorderlessFix/) · 0.0.1 · 2025-11-23 · 53 · 已弃用 | 窗口模式与 GPU 占用技巧，属客户端图形设置。 |
| [Amorously-WaveRoarFix](https://thunderstore.io/c/gtfo/p/Amorously/WaveRoarFix/) · 1.0.3 · 2024-12-27 · 9,923 | 启用未使用的波次吼叫音效，属内容恢复；已并入 EEC。 |
| [Dinorush-KnifeFix](https://thunderstore.io/c/gtfo/p/Dinorush/KnifeFix/) · 1.3.1 · 2025-04-22 · 9,851 · 已弃用 | 刀的命中盒和时序调参，已并入 BetterMeleeHitbox。 |
| [Dinorush-BetterMeleeHitbox](https://thunderstore.io/c/gtfo/p/Dinorush/BetterMeleeHitbox/) · 1.1.0 · 2026-04-22 · 5,816 | 近战命中盒与出手速度调整，属手感/平衡（蓝图已引用）。 |
| [AuriRex-TheArchive_Core](https://thunderstore.io/c/gtfo/p/AuriRex/TheArchive_Core/) · 2025.2.2 · 2025-09-03 · 5,824 | 模组设置菜单库，不含修复（修复在 A9）。 |
| [KainKondraki-No_More_Cocoons](https://thunderstore.io/c/gtfo/p/KainKondraki/No_More_Cocoons/) · 1.0.3 · 2022-08-30 · 8,673 | 去掉茧的视觉效果。GlueFix 推荐搭配它减少音频 bug，但本身不描述缺陷。 |
| [Cactus-LobbyExpansion](https://thunderstore.io/c/gtfo/p/Cactus/LobbyExpansion/) · 1.3.0 · 2026-02-08 · 33,160 | 最多 8 人游玩，属玩法扩展；GPL-3.0（[LICENSE](https://github.com/ADarkCactus/GTFO.LobbyExpansion/blob/master/LICENSE.md)）。README 列出的"储物柜开关状态不总是同步"只在扩展人数下出现。 |
| [hirnukuono-PointBlank](https://thunderstore.io/c/gtfo/p/hirnukuono/PointBlank/) · 0.0.3 · 2025-07-22 · 481 | 禁用检查点保存，属规则改动。它说明速通玩家"检查点保存时会不同步"，已列入复现清单。 |
| [hirnukuono-NoDownedBuzz](https://thunderstore.io/c/gtfo/p/hirnukuono/NoDownedBuzz/) · 0.0.1 · 2024-03-17 · 4,986 | 去掉倒地嗡鸣音效，属 QoL。 |
| [Localia-ResourceHelper](https://thunderstore.io/c/gtfo/p/Localia/ResourceHelper/) · 3.0.1 · 2022-10-21 · 102,608 | 资源标记 HUD，属 QoL；补充扫描时误纳入。 |

## 需要实机复现才能下结论的清单

以下各项建议至少准备：一台主机加一台客机、客机侧可控延迟（便于放大时序问题）、原版 R8 最新构建、不装任何其他模组。

| 条目 | 复现条件 | 观察点 |
|---|---|---|
| A10 DeadBodyFix | 霰弹或全自动武器连续射击，敌人死亡瞬间继续向尸体位置开火；主机和客机各测一次 | 子弹是否仍被尸体挡住、持续多久 |
| A11 FlashlightSync | 客机开关手电、切换装备、放置哨戒炮后开关手电，主机观察 | 主机上显示的手电状态是否与客机一致；敌人惊醒判定按哪一侧的状态 |
| A12 / A13 部位销毁时序 | 同一只母体，主机和客机分别用霰弹枪瞄同一部位射击 | 部位销毁前命中的弹丸数、击杀所需枪数是否因主客身份不同 |
| A5 StaggerSync / A14 系列 | 客机近距离对敌人造成硬直，再观察攻击动画 | 客机碰撞减速是否出现在远离敌人模型的位置；远距离近战命中是否仍存在 |
| A6 SilentShotFix | 客机在高延迟下切枪瞬间开火，旁边有睡眠敌人 | 主机上敌人是否被惊醒 |
| A1 DoorSyncFix | 客机高延迟下反复开关安全门、在门扫描完成的瞬间操作 | 门是否开了又关；扫描完成事件是否触发两次 |
| A2 ReDownFix | 客机被救起的瞬间让敌人或友伤命中 | 客机是否立即再次倒地 |
| B1 InGameJoinFix | 关卡中有带错误警报的未开安全门，进行超过 120 秒后客机中途加入 | 迟加入者是否听到警报；地图是否全黑 |
| B2 E_Bug_Fix | 客机 A 救客机 B，B 在读条中断开连接 | A 之后能否与箱子、门、队友交互 |
| B4 InheritResourceFix | 客机拿着资源包和消耗品离开，另一名玩家再加入 | 资源是否消失、掉落，还是有明确去向（判断是缺陷还是规则） |
| B5 FixEndScreen | 远征成功或失败后返回大厅（含检查点重开后） | 是否卡在结算画面无法回大厅；缺陷细节需先拿到 Discord 原文 |
| C1 EnemyAnimationFix 子项 | 逐条：巨人尖叫、射手攻击、混合体硬直；波次敌人挂机；二次尖叫；出生即杀；隔门远程破门 | 各子项是否复现；出生即杀与官方 2023-09-14 修复是否仍有残留 |
| C8 ScoutScreamFix | 多次触发侦察者尖叫，包括尖叫中被伤害、被关门、换维度 | 是否出现持续不死和重复尖叫 |
| C9 LadderFoamFix | 在梯子上用 C-foam 冻住一只敌人，后续敌人需经过该梯子 | 后续敌人是否永久被阻塞；判断是缺陷还是设计 |
| C10 / C11 沙漠维度 | 加载 Dimension_Desert_Mining_Shaft；Desert 维度搭配非配套 complex | 梯子是否不可用；终端是否生成 |
| D5 / E1 shooter bug | 长时间游玩并多次重开，频繁换枪让弹匣掉落；向电梯井扔荧光棒 | 射手弹体、C-foam 撞门、地雷伤害是否失效 |
| D9 RealBackBonus | 贴身近战背击；敌人扭身时从视觉背后射击 | 背伤加成是否生效 |
| D10 BetterDoorBulletCollision | 向破损的门缺口和门框射击 | 子弹是否被不可见碰撞体挡住 |
| D11 BioScannerFix | 需先从 JarheadHME 获取复现步骤 | 追踪器红点尺寸；能否标记侦察者 |
| D16 EvadeFix | R8 最新构建中执行闪避 | 闪避速度是否仍被冲刺速度上限限制 |
| E1 MemoryLeakFix "错误网络包" | 客机在关卡中反复切换近战武器 | 主机日志或网络统计中是否出现错误包 |
| PointBlank 提到的检查点不同步 | 多人快速连续触发检查点保存与恢复 | 客机状态是否与主机不一致（缺陷细节未公开） |

## 引用

[tsapi]: https://thunderstore.io/c/gtfo/api/v1/package/
[steamapi]: https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid=493520&count=300&maxlength=0&feeds=steam_community_announcements
[archivefeat]: https://github.com/AuriRex/GTFO_TheArchive/blob/main/TheArchive.Essentials/Features.md
[unity]: https://issuetracker.unity3d.com/issues/raycasts-fail-to-hit-collider-when-there-is-a-different-gameobject-with-a-collider-very-far-away-in-the-scene
[p20251017]: https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/1813674388962097
[p20240604]: https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/5742731639347517011
[p20240307]: https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/5679673637651974058
[p20240214]: https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/5750603429272485774
[p20231214]: https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/7585814657460325762
[p20231207]: https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/5395938616796575808
[p20230914]: https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/5480373588782255544
[p20230509]: https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/5138087875097356256
[p20230427]: https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/5133583642650777543
[p20221207]: https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/4980448574104785471
[p20221102]: https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/4732747437198123139
[p20220819]: https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/4584121659783575324
[p20220627]: https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/4474904295744070962
[p20220616]: https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/6214419130138774929
[p20220127]: https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/4237326104320220626
[p20220121]: https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/4237325463674296082
[p20210429]: https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/4074045878072887732
[p20201106]: https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/3887130640399675636
[p20201022]: https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/3895010671337408670
[p20201023]: https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/3895010671343328588

- Thunderstore 全量包列表：[api/v1/package][tsapi]
- GTFO 官方公告（Steam News API）：[GetNewsForApp][steamapi]
- TheArchive 功能与修复列表：[Features.md][archivefeat]
- Unity 射线检测缺陷：[Issue Tracker][unity]
