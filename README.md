# Infini GTFO Mods

> **Forge 的计划、规范、接口与状态只写在两仓统一框架里**，本仓库不另写一份：
> - 本地（两仓并排检出，反映工作树最新内容）：[`../Infini-GTFO-Model-Site/Docs/forge-contract/FORGE-FRAMEWORK.md`](../Infini-GTFO-Model-Site/Docs/forge-contract/FORGE-FRAMEWORK.md)
> - 远端（只反映已提交内容）：<https://github.com/NAinfini/Infini-GTFO-Model-Site/blob/main/Docs/forge-contract/FORGE-FRAMEWORK.md>

GTFO 模组源码仓库。每个模组占一个目录，源码、测试、文档、资源与发行产物随所属模组保存；根目录只保留仓库导航和公共验证解决方案。

## 这个仓库在做什么

Forge 是给普通 GTFO 玩家的创作工具。玩家在网站上自由组装敌人、武器、工具、消耗品和地图，保存分享，进游戏能玩，整个过程不需要理解 DataBlock、BepInEx 或依赖关系。网站负责创作与导出，这个仓库负责**让导出的定义在游戏里正确执行**：核验真实 GTFO 对象与 API，按统一的图执行，在权威端提交结果，处理多人、生命周期、成本与恢复，并报告可核实的问题。

## 目录

| 目录 | 职责 |
| --- | --- |
| [ForgeRuntime](ForgeRuntime/README.md) | 公共 SDK 与 GTFO 宿主：唯一调度、状态、结果、预算、计划发现、执行日志，**网络层与社区修复**；跨包依赖规则与所有权也写在这里 |
| [ForgeTrigger](ForgeTrigger/README.md) | 跨域逻辑节点，覆盖全部领域的横切基座 |
| [ForgeMap](ForgeMap/README.md) | 空间、任务、设备、遭遇、玩家流程，**并持有地图生成的底层逻辑** |
| [ForgeWeapon](ForgeWeapon/README.md) | 武器、工具、消耗品 |
| [ForgeEnemy](ForgeEnemy/README.md) | 敌人身份、接收器、AI、技能、生成要求 |
| [ForgeDevelopment](ForgeDevelopment/README.md) | 可选作者诊断、性能采样、报告；只给制作者，不是玩家依赖 |
| [InfiniTweaks](InfiniTweaks/README.md) | 独立 Quality of Life 模组，不属于 Forge 实施范围 |

每个 Forge 模组有两份常驻文档：`README.md` 说明能力、边界与内部结构，`VALIDATION.md` 只记带日期的运行记录。计划与各单元状态写在上面的两仓统一框架第 6 节；一次性交接记录不保留，历史从 Git 查阅。

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

各模块的聚焦复跑入口见其 VALIDATION.md。构建输出使用隔离目录，不写入已安装的 profile。各模组的 `bin/`、`obj/`、`dist/` 与 Python 缓存是本地产物，不自动入库。现有 ZIP 按模组归入各自 `dist/`，保留原文件名、字节和版本。

Infini Tweaks 与 Forge 分别构建，Forge 开发不在 Infini Tweaks 中增加玩法实现。

## 接手前读什么

先读两仓统一框架了解计划、协作规范、接口与各单元状态，再读要改的那个模块的 README、VALIDATION 和真实源码。计划描述是实施要求，不能代替读取正在变化的代码。
