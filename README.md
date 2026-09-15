<a id="top"></a>
# Infini GTFO Mods

**🌐 Live site: [gtfo-forge.ca](https://gtfo-forge.ca)**

**[English](#en)** · **[中文](#zh)**

---

<a id="en"></a>
## English

> The plan, spec, interfaces, decisions and status for Forge live **only** in the
> two-repo unified framework — nothing is duplicated here:
> - Local (both repos checked out side by side, always up to date): [`../Infini-GTFO-Model-Site/Docs/forge-contract/FORGE-FRAMEWORK.md`](../Infini-GTFO-Model-Site/Docs/forge-contract/FORGE-FRAMEWORK.md)
> - Remote (committed content only): <https://github.com/NAinfini/Infini-GTFO-Model-Site/blob/main/Docs/forge-contract/FORGE-FRAMEWORK.md>

The GTFO mod source repo. Each mod gets its own directory — source, tests, docs,
assets and release artifacts stay together; the repo root is just navigation plus a
shared verification solution.

### What lives here

Forge lets ordinary GTFO players build enemies, weapons, tools, consumables and maps
on the website, no modding knowledge required. The website handles authoring and
exports data-only Rundown packages; this repo holds all the game code and makes sure
the exported stuff **actually runs correctly in the game** — validating real GTFO
objects/APIs, executing the unified graph, committing results on the authoritative
side, handling multiplayer/lifecycle/cost/recovery, and reporting issues that can
actually be verified.

### Layout

Six Forge base packages, each shipped as its own Thunderstore package (D-011), plus
one standalone mod:

| Directory | What it does |
| --- | --- |
| [ForgeRuntime](ForgeRuntime/README.md) | Shared SDK + GTFO host: scheduler, state, results, budgets, plan discovery, execution log, **networking and community fixes** — also where cross-package dependency rules live |
| [ForgeTrigger](ForgeTrigger/README.md) | Cross-domain logic nodes, the base layer everything else sits on |
| [ForgeMap](ForgeMap/README.md) | Room instances and topology, missions, devices, encounters, extraction, player flow — **and building website-assembled rooms in game** |
| [ForgeWeapon](ForgeWeapon/README.md) | Weapons, tools, consumables |
| [ForgeEnemy](ForgeEnemy/README.md) | Enemy identity, receivers, AI, skills, spawn requirements |
| [ForgeDevelopment](ForgeDevelopment/README.md) | Author diagnostics/perf sampling/reports — author-only, never a player dependency |
| [InfiniTweaks](InfiniTweaks/README.md) | Standalone QoL mod, not part of Forge |

Every Forge mod keeps two docs: `README.md` (capability, boundaries, structure) and
`VALIDATION.md` (dated run logs only). Plan and status per unit are in §6 of the
framework linked above, decisions in §8.2 — no one-off handoff notes, history lives
in Git.

### How Forge builds on the game

- **Native mechanics, no scope cuts.** Weapons, enemies, objectives, checkpoints,
  self-revive and spawn-apart are implemented on Forge's own Trigger/Event system;
  third-party mods are behaviour references only. Host migration, checkpoint restore,
  the full 605-atom vocabulary and custom enemy/weapon models are all required (D-021).
- **Networking.** Transport is GTFO-API's NetworkAPI (bundled with BepInExPack_GTFO)
  and BepInEx. Forge only adds dedupe, epochs, batching, the handshake and sync rules
  on top.
- **Level build.** Build phases are observed through the game's own `LG_Factory`
  callbacks.
- **Community fixes.** Not rewritten. Pure fix packages become dependencies; only
  fixes that overlap side effects or authoritative state Forge owns are implemented by
  Forge, and the dependency must not run the same side effect again.
- **Rooms (D-019).** The website assembles rooms from parts the game already has and
  exports them as data. ForgeMap builds each room in game and registers it as a
  geomorph that ComplexResourceSet and `CustomGeomorph`/LGTuner use; the vanilla
  generator still decides placement. LGTuner (`hirnukuono-LGTuner`) stays long-term
  for zone room order and extra complex resources. Exact placement/assembly generation
  and its G0–G6 checks are cancelled and deleted. Author-uploaded mod packs extracted
  into the community are a supplementary source.

### Build & validate

Until the six base packages and the website are all built, only `dotnet build` is run
routinely; test runs happen together in one final phase (D-020).

Stuff that doesn't need the game:

```powershell
dotnet build Forge.Architecture.sln -c Release
dotnet run --project ForgeRuntime/tests/Architecture/Architecture.csproj -c Release --no-build
```

Stuff that needs a game reference — point `GTFO_BEPINEX_PATH` at a local BepInEx
folder first; none of these install the mod:

```powershell
dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release
dotnet build InfiniTweaks/InfiniTweaks.csproj -c Release
dotnet run --project ForgeRuntime/tests/Framework/Framework.csproj -c Release -- --fixtures ../Infini-GTFO-Model-Site/Tests/Forge/fixtures/runtime
```

Each module has its own focused rerun command in its `VALIDATION.md`. Build output
goes to isolated dirs, never into an installed profile. `bin/`, `obj/`, `dist/` and
Python caches per mod are local, not auto-committed. Existing ZIPs sit under each
mod's own `dist/` with original names/bytes/versions intact.

Infini Tweaks and Forge build separately — Forge work doesn't leak gameplay
implementation into Infini Tweaks.

### Before you touch something

Read the two-repo framework first for plan/rules/interfaces/status, then read the
README, VALIDATION and actual source of the module you're changing. The plan tells
you the requirements, not the current state of code that's still moving.

<p align="right"><a href="#top">↑ top</a> · <a href="#zh">中文 →</a></p>

---

<a id="zh"></a>
## 中文

> Forge 的计划、规范、接口、裁决与状态**只**写在两仓统一框架里，这里不重复一份：
> - 本地（两仓并排检出，永远最新）：[`../Infini-GTFO-Model-Site/Docs/forge-contract/FORGE-FRAMEWORK.md`](../Infini-GTFO-Model-Site/Docs/forge-contract/FORGE-FRAMEWORK.md)
> - 远端（只有已提交的内容）：<https://github.com/NAinfini/Infini-GTFO-Model-Site/blob/main/Docs/forge-contract/FORGE-FRAMEWORK.md>

GTFO 模组源码仓库。每个模组一个目录，源码、测试、文档、资源和发行产物都放一起；根目录
只放导航和公共验证方案。

### 这仓库是干嘛的

Forge 让普通 GTFO 玩家不用学模组制作，就能在网站上拼出敌人、武器、工具、消耗品和地图。
网站管创作，导出只含数据的 Rundown 包；游戏端代码全在这个仓库，负责**让导出的东西在
游戏里真跑得对**：核验真实的 GTFO 对象和 API，按统一的图执行，在权威端提交结果，处理
多人、生命周期、成本和恢复，报告能核实的问题。

### 目录

六个 Forge 基础包，各自作为独立的 Thunderstore 包发行（D-011），外加一个独立模组：

| 目录 | 干什么 |
| --- | --- |
| [ForgeRuntime](ForgeRuntime/README.md) | 公共 SDK + GTFO 宿主：调度、状态、结果、预算、计划发现、执行日志，**网络层和社区修复**——跨包依赖规则也在这 |
| [ForgeTrigger](ForgeTrigger/README.md) | 跨域逻辑节点，其他一切的地基 |
| [ForgeMap](ForgeMap/README.md) | 房间实例与拓扑、任务、设备、遭遇、撤离、玩家流程——**以及在游戏里搭出网站拼好的房间** |
| [ForgeWeapon](ForgeWeapon/README.md) | 武器、工具、消耗品 |
| [ForgeEnemy](ForgeEnemy/README.md) | 敌人身份、接收器、AI、技能、生成要求 |
| [ForgeDevelopment](ForgeDevelopment/README.md) | 作者诊断/性能采样/报告——只给制作者用，永远不会成为玩家依赖 |
| [InfiniTweaks](InfiniTweaks/README.md) | 独立的 QoL 模组，不属于 Forge |

每个 Forge 模组保留两份文档：`README.md`（能力、边界、结构）和 `VALIDATION.md`（只记
带日期的运行记录）。各单元的计划和状态在上面框架的第 6 节，裁决在 §8.2——不留一次性
交接记录，历史翻 Git。

### Forge 怎么搭在游戏上

- **原生实现，范围不砍。** 武器、敌人、任务目标、检查点、自救和分开出生都在 Forge 自己的
  Trigger/Event 体系上实现，第三方模组只当行为参考。主机迁移、检查点恢复、完整的 605 条
  原子词表、自定义敌人和武器模型都要做（D-021）。
- **网络。** 传输用 GTFO-API 的 NetworkAPI（BepInExPack_GTFO 自带）和 BepInEx，Forge
  只在上面加去重、epoch、合批、握手和同步规则。
- **关卡搭建。** 通过游戏自己的 `LG_Factory` 回调观察各个搭建阶段。
- **社区修复。** 不重写。纯修复包直接作为依赖；和 Forge 自己负责的副作用或权威状态重叠的
  条目才由 Forge 实现，依赖包不能对同一副作用再执行一次。
- **房间（D-019）。** 网站用游戏已有的零件拼房间，导出成数据；ForgeMap 在游戏里把房间搭
  出来，登记成 geomorph，供 ComplexResourceSet 和 `CustomGeomorph`/LGTuner 使用，房间摆
  在哪仍由原版生成器决定。LGTuner（`hirnukuono-LGTuner`）长期保留，负责区域内的房间选择
  顺序和额外环境资源加载。精确摆放/拼装生成及其 G0–G6 检查已取消并删除。作者上传、提取进社区的
  模组包是补充来源。

### 构建和验证

六个基础包和网站全部搭完之前，日常只跑 `dotnet build`；测试留到最后一个阶段统一跑
（D-020）。

不需要游戏的部分：

```powershell
dotnet build Forge.Architecture.sln -c Release
dotnet run --project ForgeRuntime/tests/Architecture/Architecture.csproj -c Release --no-build
```

需要游戏引用的部分——先把 `GTFO_BEPINEX_PATH` 指向本地 BepInEx 目录，这些命令都不装
模组：

```powershell
dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release
dotnet build InfiniTweaks/InfiniTweaks.csproj -c Release
dotnet run --project ForgeRuntime/tests/Framework/Framework.csproj -c Release -- --fixtures ../Infini-GTFO-Model-Site/Tests/Forge/fixtures/runtime
```

每个模块自己的聚焦复跑命令在各自 `VALIDATION.md` 里。构建输出用隔离目录，不写进已装的
profile。各模组的 `bin/`、`obj/`、`dist/` 和 Python 缓存是本地的，不自动入库。现有 ZIP
按模组放各自 `dist/` 下，文件名/字节/版本原样保留。

Infini Tweaks 和 Forge 分开构建——Forge 的东西不会漏到 Infini Tweaks 的玩法实现里。

### 动手之前先看什么

先看两仓框架搞清楚计划、规则、接口和状态，再看要改的那个模块的 README、VALIDATION 和
真实源码。计划写的是要求，不是还在变的代码的当前状态。

<p align="right"><a href="#top">↑ 顶部</a> · <a href="#en">English →</a></p>
