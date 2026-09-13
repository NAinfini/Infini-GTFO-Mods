# Infini GTFO Mods

GTFO 模组源码仓库。每个模组占一个目录，源码、测试、文档、资源与发行产物随所属模组保存；根目录只保留仓库导航、架构说明、Agent 交接和公共验证解决方案。

本文件只做导航。**构建状态、测试计数、架构断言数和批次位置一律见 [ARCHITECTURE.md](ARCHITECTURE.md)**，不在这里重复——以前三份根文档各写一份状态数字，结果是十来处互相矛盾。

## 这个仓库在做什么

Forge 是给普通 GTFO 玩家的创作工具。玩家在网站上自由组装敌人、武器、工具、消耗品和地图，保存分享，进游戏能玩，整个过程不需要理解 DataBlock、BepInEx 或依赖关系。网站负责创作与导出，这个仓库负责**让导出的定义在游戏里正确执行**：核验真实 GTFO 对象与 API，按统一的图执行，在权威端提交结果，处理多人、生命周期、成本与恢复，并报告可核实的问题。

产品定位、受众裁决与完整架构见网站仓库的 [唯一总案 v2.0](../Infini-GTFO-Model-Site/Docs/rework/FORGE-COMPLETE-PLAN.md)。本仓库不另立总计划。

## 目录

| 目录 | 职责 | 当前状态 |
| --- | --- | --- |
| [ForgeRuntime](ForgeRuntime/README.md) | 公共 SDK、唯一调度、状态、结果、预算、**网络层与社区修复** | 宿主与 SDK 已构建通过；诊断源码待 D2 迁出；网络层未开工 |
| [ForgeTrigger](ForgeTrigger/README.md) | 跨域逻辑节点，覆盖全部领域的横切基座 | 纯计算、集合、目标筛选、空间与可变端口/权重底层已实现；空 provider，无可执行 binding |
| [ForgeMap](ForgeMap/README.md) | 空间、任务、设备、遭遇、玩家流程，**并持有地图生成的底层逻辑** | 内部身份表与生命周期已在生产程序集；空 provider，无对外 resolver |
| [ForgeWeapon](ForgeWeapon/README.md) | 武器、工具、消耗品 | 装备身份托管实现已交付；空 provider，无游戏 binding |
| [ForgeEnemy](ForgeEnemy/README.md) | 敌人身份、接收器、AI、技能、生成要求 | **唯一有真实 BepInEx 插件与 5 个 binding 的模块**，全部 implementation-only |
| [ForgeDevelopment](ForgeDevelopment/README.md) | 可选作者诊断、性能采样、报告 | 空骨架；诊断源码仍在 ForgeRuntime 内 |
| [InfiniTweaks](InfiniTweaks/README.md) | 独立 Quality of Life 模组 | 已有完整工程，不属于 Forge 实施范围 |

每个模组固定三份常驻文档：`README.md` 说明当前能力与边界，`IMPLEMENTATION-PLAN.md` 说明未完成的批次，`VALIDATION.md` 记录当前验证结果并带「上次更新」戳。一次性交接记录不再保留，历史从 Git 查阅。

## 玩法实施已经开始

旧 README 写「玩法实施仍未开始」已经过时。Enemy 已有 5 个 binding 与 5 个原生 Hook 的实际接线（承伤观察、显式治疗、生命变化事实、死亡流程、肢体破坏），全部处于 **implementation-only** 证据级——代码存在、托管测试通过，**没有任何一条完成 GTFO 实机验收**。Map、Weapon 的身份层已在生产程序集里，但它们的 `ModuleDefinition` 仍是空 provider。

## 构建与验证

先验证不需要游戏的部分：

```powershell
dotnet build Forge.Architecture.sln -c Release
dotnet run --project ForgeRuntime/tests/Architecture/Architecture.csproj -c Release --no-build
```

需要游戏引用的命令先把 `GTFO_BEPINEX_PATH` 指向本地 BepInEx 目录；这些命令不安装模组：

```powershell
dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release
dotnet build InfiniTweaks/InfiniTweaks.csproj -c Release
dotnet run --project ForgeRuntime/tests/Framework/Framework.csproj -c Release -- --fixtures ../Infini-GTFO-Model-Site/Tests/Forge/fixtures/runtime
```

完整命令清单与实际结果见 [ARCHITECTURE.md 第 11 节](ARCHITECTURE.md#11-复跑本文的数字)，各模块的聚焦入口见其 VALIDATION.md。各模组的 `bin/`、`obj/`、`dist/` 与 Python 缓存是本地产物，不自动入库。现有 ZIP 按模组归入各自 `dist/`，保留原文件名、字节和版本。

Infini Tweaks 与 Forge 分别构建，Forge 开发不在 Infini Tweaks 中增加玩法实现。

## 接手前读什么

先读 [ARCHITECTURE.md](ARCHITECTURE.md) 了解真实结构与状态，再读 [AGENT-HANDOFF.md](AGENT-HANDOFF.md) 拿到进场流程与委派合同，然后读要改的那个模块的三份文档和真实源码。计划描述是实施要求，不能代替读取正在变化的代码。
