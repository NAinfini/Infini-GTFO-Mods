# 实验数据块（MTFO 最小修改，**本批不安装**）

这些文件是「必须改数据块才能验证」的点的最小修改。它们放在仓库里作为素材，**没有复制到任何 profile**，
玩家/作者需要自己按下面的方式安装。

## 目录

| 文件 | 验证点 | 改了什么 |
| --- | --- | --- |
| `generator-cluster-without-type9.json` | `ni-generator-6.3.3`：单独设 `GeneratorClustersInZone > 0`、主目标不是 Type 9 会发生什么 | 只给一个区域的 `GeneratorClustersInZone` 设 1，主目标保持原样 |
| `specific-pickup-battery.json` | `ni-generator-6.3.7`：`SpecificPickupSpawningDatas` 投电池能否落到指定区域 | 在指定区域加一条 `PickupToSpawn` = 电池物品 id 的投放 |
| `door-locked-no-key-literal.json` | `ni-generator-6.3.8`：`CustomText` 写字面量的显示效果 | 把一个门的 `ProgressionPuzzleToEnter` 设成 `PuzzleType=3` + 字面量文案 |
| `door-locked-no-key-localized.json` | `ni-generator-6.3.8`：`CustomText` 写本地化 id 的显示效果 | 同上，但换成 id 972（原版默认锁门文案） |

## 安装方式（MTFO）

MTFO（More Than Four Options）读取 profile 下 `BepInEx/config/` 里的 `GameData_*_bin.json`：

1. 找到目标 profile：`%APPDATA%\r2modmanPlus-local\GTFO\profiles\<profile>\BepInEx\config\`。
2. 把本目录里要验证的文件复制过去，**文件名必须是 MTFO 认的那一个**（见每个文件里的 `$file` 字段）。
3. 如果同名文件已经存在，不要覆盖：把本文件里的条目合并进已有文件的对象/数组（MTFO 是整文件替换，
   不会自动合并）。
4. 进关卡后由 `probes/commands/ni-generator.json` 里的命令读回结果：
   - `gen-cluster-state`、`gen-cluster-objective-data` 读集群；
   - `gen-door-locked-read` 读锁门状态与文案；
   - `gen-generator-status-wait` 等发电机状态变化。

## 值与 id 的来源

- 电池物品 id：从本机 profile 的 `GameData_ItemDataBlock_bin.json` 里查（`publicName` 含 "Power Cell"），
  本批没有把具体 id 写死，因为不同 build 的 id 需要现场核对。
- 本地化 id 972：来自 `ni-generator.md` §3 记录的原版默认锁门文案
  `"<color=red>://ERROR: Door in emergency lockdown, unable to operate.</color>"`。
- `R7_Tutorial` 区域 4 的字面量文案同样是 §3 的原文。
- 每个文件里的 `ZonePlacementData` / `LocalIndex` 都是**占位值**，安装前必须改成目标关卡实际存在的区域，
  否则 MTFO 会因为找不到区域而报错或静默失效。

## 为什么本批不安装

任务约定：并行批次不向任何 r2modman profile 安装或复制文件，数据块只在仓库里准备好并写清安装方式。
安装、进游戏、把结果并进证据由 Claude 或作者按 `PLAYTEST.md` 的清单执行。
