# 照着玩：原生未证点的实验清单

这份清单给作者照着玩。每一步都写明：去哪、做什么、按哪个热键、跑哪个命令、单人还是双人。
目标是用最少的局数覆盖最多的点。

命令来源是 `probes/commands/*.json`，首次启动时由插件复制到
`BepInEx/config/ForgeDevelopment/commands/`；之后改那个目录里的文件即可，**不需要重新发版**。

## 0. 开跑前的三件事

1. **必须是 Authoring 模式**：`BepInEx/config/NAinfini.ForgeRuntime.cfg` 里 `[Runtime] Mode = Authoring`。
   其它模式下插件不加载，热键没有任何反应。
2. **确认命令已就位**：进游戏后按 **F5** 打开实验面板。第一行会写命令数量与目录。
   如果是 0 条，按面板上的 **reload files** 再看；还是没有就去
   `BepInEx/config/ForgeDevelopment/commands/` 里放 JSON。
3. **确认身份**：面板每行标着 `[host]` / `[client]` / `[any]`。标 `[host]` 的命令在客户端执行会被拒，
   并在 exp 通道里写明原因。

## 1. 热键

| 键 | 作用 |
| --- | --- |
| **F5** | 开关实验面板（列出所有命令、上次结果、准星/最近目标解析、存活实验对象数） |
| **F6** | 书签（标记"刚才发生了"，供 rec-query 定位） |
| **F7** | 全量快照 |
| **F8** | 截图（原生功能，和命令里的截图都进会话目录 `shots/`） |
| **F9** | 性能快照（已有） |
| **F10** | 导出报告（已有） |
| **F11** | 战斗采样（已有） |
| 面板内 **↑/↓** | 选择命令 |
| 面板内 **Enter** | 执行选中的命令 |
| 面板内 **Esc** | 取消正在跑的命令（cleanup 仍然会跑） |
| 面板内 **R** | 销毁实验创建的标记与克隆体 |

面板里的"crosshair / nearest"两行是**实时目标解析**：进关卡后先看这两行，能立刻知道准星指着哪个敌人、
哪扇门、哪个终端，以及最近的发电机/终端有多远。命令跑不动时先看这里。

## 2. 单人就能覆盖的关卡与命令

### 2.1 R4B1「Malachite」（`Rundown004_B1_L1_Power_Cells`）——发电机与电池的主场

这关自带 `PowerGeneratorPlacements` 与 Type 7 搬电池目标，是 `ni-generator` 的主战场。

| 步骤 | 做什么 | 命令 |
| --- | --- | --- |
| 1 | 进关，等落地稳定 | 按 **F5**，看 crosshair/nearest 两行有没有解析出东西 |
| 2 | 走到**门口那台发电机**旁边，准星对着它 | `gen-generator-status-wait`（等状态变化，需要插电池才会变） |
| 3 | 搬一块电池插进去 | 不用命令；插完再看面板上那条命令是否已 `[done]`，exp 通道里记录了 `playerThatChangedTheState` |
| 4 | 走到**锁着的门**前，准星对着门 | `gen-door-locked-read`（读锁状态 + `CustomText`） |
| 5 | 站着不动 | `gen-cluster-state`、`gen-cluster-objective-data`（集群台数、Type 9 声明的发电机/电池数） |
| 6 | 开门前先跑 | `gen-unlock-type2`（对**准星所指的门**发 Type 2 解锁） |

**要验证的对照**（需要装数据块，见第 5 节）：
`generator-cluster-without-type9.json` 把 R4B1 的 z2 设成只带集群、不带 Type 9；重进关卡后跑
`gen-cluster-state`，看 `m_generators.Count` 是 0 还是有台数。

### 2.2 R2B2「Power corrupts」/ R4E1 / R6C2 / R5B2——电池分布与区域别名

| 关卡 | 覆盖点 | 命令 |
| --- | --- | --- |
| R2B2 `Rundown002_B2` | 13 条 `PowerGeneratorPlacements` vs 目标 4 块电池（候选位 vs 台数） | `gen-cluster-state` + 数场上实际发电机台数 |
| R4E1 `Rundown004_E1_L1_Distribute` | 只有 2 条 placement、目标 2 块电池 | 同上，用作"条数==电池数"那一侧的对照 |
| R6C2 `Rundown006_C2_L1` | 3 条 placement、Type 7 目标 3 块电池 | `gen-cluster-state` |
| R5B2 `Rundown005_B1_L2` | 没有远征引用的布局（旧数据残留） | 只能通过 LGTuner / 自定义远征进入；记下能不能进 |

**共同步骤**：进关 → **F5** → 跑 `gen-cluster-objective-data`（读目标块声明的数字）→
跑 `gen-cluster-state`（读场上实际台数）→ 把两个数字和 `ni-generator.md` §1 的表格对照。
这一步不需要打完关卡，进关、看完、退出即可。

### 2.3 R7D1 / R7C1——门锁与播报

| 步骤 | 做什么 | 命令 |
| --- | --- | --- |
| 1 | 找一扇 **Locked_No_Key**（`PuzzleType=3`）的门 | `gen-door-locked-read` |
| 2 | 对它发 Type 2 | `gen-unlock-type2` |
| 3 | 看播报文字 | `gen-intel-zone-fragment`（`[ZONE_4]` 片段） |
| 4 | 看片段能不能替换 | `gen-intel-fragment-replace` 的返回值和截图 |
| 5 | 看 ProgressionTextFragment | `gen-intel-target-zone-fragment` |
| 6 | 看本地化 id | `gen-intel-localized-id` |

**判断口径**：`ni-generator.md` §5 的未证点是"`[ZONE_n]` 渲染成什么、`[TARGET_ZONE]` 能不能进 WardenIntel"。
截图里如果 `[ZONE_4]` 原样出现，就是没被替换；如果渲染成区域名，记下渲染出来的名字和该区域的
`ZoneAliasStart`（`gen-door-locked-read` 那条会读 `ZoneAliasStart` 附近的信息）。
**第 5 步是关键**：`[GENERATOR_NAME]`/`[TARGET_ZONE]` 属于另一套片段系统，原样出现即证伪合流。

### 2.4 HUD：护盾条、克隆 TMP、队友头顶

**护盾条（`ni-hud` A）**——单人，任何关卡：

1. 进关，按 **F5**，跑 `hud-shield-write`。
2. 命令会依次写 50 / 100 / 1000，每次都在下一帧和 1 秒后读回 `m_lastShieldVal` 与
   `m_shield1.rectTransform.sizeDelta`，并各截一张图。
3. 看截图回答 `ni-hud` §6 A 的三个问题：条**有没有出现**、数值→长度怎么映射、超上限会不会爆格。
4. 跑 `hud-shield-overlap`：感染条与护盾条同时非零，看几何是否打架。
5. 跑 `hud-shield-hide-states`：读 `m_shieldUIParent.activeInHierarchy`，**然后自己按 Tab / 开终端 /
   倒地**，再跑一次，比较两次的读数。

**克隆 TMP（B）**——单人：跑 `hud-tmp-clone`。它把 `PUI_Inventory.m_headerTxt` 克隆到同一个
RectTransform 下并写一行金字。截图看位置对不对；cleanup 里已经用 `Destroy` 销毁克隆体。

**队友头顶附加信息（C）**——**需要两人**：附加信息这一行已经由模组的 `TeammateOverhead` 实现，
不再需要实验命令（裁定 109 第 5 条）。验证方式是**看 trace 加截图**：`hud.teammate-extra-info-writer`
这一点的 capture 是 `trace:gui/PlaceNavMarkerOnGO.UpdateExtraInfo`，两人同局时让队友掉血、改名、
倒地，并在 4 人同屏与屏幕边缘收缩时各截一张图，读回 `state:navMarkers.*` 与截图里的行位置；
同时开 InfiniTweaks 的资源行，确认两行不会抢同一个位置。`mark-clean-slate` 只能清标记，不能替代。

### 2.5 ni-markcolor 的 10 条——一条命令一条

**单人**，任何有敌人的关卡。每条命令都要求**准星指着目标**；面板的 crosshair 行会先告诉你指到了什么。

| 命令 | 覆盖 `ni-markcolor.md` 的哪一条 |
| --- | --- |
| `mark-place-custom` | 10-1、10-2：`PlaceCustomMarker` 挂敌人身上，图标是否和追踪器一致 |
| `mark-place-on-looked-at-enemy` | 10-1、10-9：跟随移动、2 秒后是否还在、两个标记是否叠加 |
| `mark-prepare` | 10-1：`PrepareMarker` 的行为差异 |
| `mark-selection-color-null` | 10-10：Color 传 null |
| `mark-selection-color-gold` | 10-10、10-3、10-4：传金色 → `SetColor` 红色 → `SetStyle`，每一步都读回四个子部件的颜色 |
| `mark-tag-marker-read` | 10-5：读私有 `m_tagMarker`，等到它非空（超时 600 帧） |
| `mark-tag-marker-setcolor` | 10-3、10-6：对原版 tag 设色，3 秒后再读一次，看有没有被重扫冲掉 |
| `mark-destroy-delay-zero` | 10-8：`destroyDelay=0`，3 秒后再读一次，看标记还在不在 |
| `mark-clean-slate` | 10-9：清掉本轮实验的所有标记 |

**第 6 条（`mark-tag-marker-read`）的额外步骤**：先用**生物追踪器**扫一下准星所指的敌人，让原版
红 tag 出现，再跑命令。命令里的 `wait.path = m_tagMarker` 就是等这个时机。

### 2.6 护盾模板的"伤害生效前改写"——单人

`probes/commands/ni-shield-template.json`：

1. `damage-rewrite-arm-50`：`ArmDamage(0.5)` 装上改写，然后**去找一个敌人挨一下**。
   命令里的 `wait.path = Damage.Health changed` 会在你受伤时通过。之后读回血量，和 `before` 快照比。
2. `damage-rewrite-arm-zero`：`ArmDamage(0.0)`，20 秒内随便挨打，读回血量应该完全不变。
3. `damage-rewrite-disarm`：卸载补丁，确认之后一切照常。

**这个前缀怎么做到"只在实验期间生效"**：`ExperimentDamage.Arm` 在命令开始时用 HarmonyX 现场给
本机玩家的 6 个伤害入口（`BulletDamage` / `MeleeDamage` / `ExplosionDamage` / `FireDamage` /
`PushDamage` / `FallDamage`）装同一个前缀，`Disarm` 用 `Unpatch` 全部卸掉。前缀里只做三件事：
读因子、确认受伤的是本机玩家、把 `dam` 乘上因子。没有 `Arm` 时补丁根本不存在（不是"因子为 1"），
所以命令结束后伤害处理与没装插件时完全一致。命令失败时 cleanup 仍然会执行 `DisarmDamage`。

## 3. 双人才能覆盖的

| 覆盖点 | 怎么玩 | 为什么必须双人 |
| --- | --- | --- |
| `ni-markcolor` 10-7：一台机器改色是否只在本机可见 | 两台机都开开发模组；A 机跑 `mark-tag-marker-setcolor`，B 机盯着同一个敌人 | 判断"颜色是纯本地表现" |
| 原版 tag 的同步（`pTagEnemy` 只有一个字段） | A 机扫敌人，B 机看 B 机自己有没有红 tag | 证明标记是各机自建 |
| HUD C：队友头顶附加信息 | 两人同屏，4 人时排版最好 | 只对队友有效 |
| 客户端事实有没有到主机 | 两台机都开记录；客户端跑 `gen-unlock-type2`（会被拒，记下原因），主机跑同样的命令 | 验证 host 门与 rec-merge 的配对 |

## 4. 跑完之后：把会话交给 Claude

1. 退出关卡、退出游戏，等会话文件写完（退出时会 flush）。
2. 会话目录在：`BepInEx/ForgeReports/rec-<时间>-<host|client>-<slot>/`
   （绝对路径：`%APPDATA%\r2modmanPlus-local\GTFO\profiles\<profile>\BepInEx\ForgeReports\`）。
3. 把**整个目录**（或打包成 zip）交给 Claude，路径原样给出即可。
4. Claude 侧的查询方式：

```powershell
# 看实验命令的执行结果（每个 step 一条）
python ForgeDevelopment/scripts/rec-query.py <会话目录> --channel exp

# 只看失败与拒绝
python ForgeDevelopment/scripts/rec-query.py <会话目录> --channel exp --kind result

# 看某次命令附近的原生调用（书签前后 10 秒）
python ForgeDevelopment/scripts/rec-query.py <会话目录> --near <书签标签> --seconds 10

# 截图清单
python ForgeDevelopment/scripts/rec-query.py <会话目录> --channel shot

# 两台机器的会话对齐，标出"客户端发生了但主机没收到"
python ForgeDevelopment/scripts/rec-merge.py <主机会话目录> <客户机会话目录> --points ForgeDevelopment/probes/points.tsv
```

脚本由 dev-rec-core 提供；如果 `scripts/` 里还没有这两个文件，说明那批还没落地，
此时直接看会话目录里的 `index.json` 和 `exp-*.jsonl` 也能读到全部结果。

## 5. 需要改数据块才能验证的点（**本批不安装**）

见 `probes/datablocks/README.md`。四个文件分别是：

- `generator-cluster-without-type9.json` → 6.3.3
- `specific-pickup-battery.json` → 6.3.7
- `door-locked-no-key-literal.json` / `door-locked-no-key-localized.json` → 6.3.8

安装方式：MTFO 读 `BepInEx/plugins/<RundownName>/GameData_*_bin.json`（本 profile 里就是
`plugins/InfiniMap/`）。把文件复制进去，**同名文件要先合并再覆盖**，然后重进关卡。
验证完记得把文件删掉——它会整块替换对应数据块。

## 6. 最少局数的最省力顺序

1. **R4B1 一局**：2.1 全部 + 2.2 的 `gen-cluster-*`。
2. **任意有敌人的关卡一局**：2.5 的九条 markcolor 命令（准星对着敌人/门/终端逐个跑）。
3. **R7D1 或 R7C1 一局**：2.3 的六条播报命令。
4. **任意关卡一局（可以就在第 3 局里做）**：2.4 的护盾三条 + 2.6 的三条伤害改写。
5. 数据块实验单独开一局：装文件 → 进关 → 跑两条命令 → 退出 → 删文件。
6. 双人项目最后合并成 1–2 局。

合计约 **5 局单人 + 1–2 局双人**。
