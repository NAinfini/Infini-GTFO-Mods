# Release 身份与打包输入

本目录是六个基础包发行身份的唯一来源。`release.json` 逐包声明 Thunderstore 包名、插件 GUID、provider、精确版本、受众、依赖串和 zip 里要放的文件；`check-identity.ps1` 核对源码与清单里重复写出的同一批数字。这里不做打包，也不改任何 `.cs` / `.csproj`；版本不一致只报告、由对应任务的负责人修改。

## 检查

```powershell
pwsh Release/check-identity.ps1
```

零退出表示 `release.json` 与下列文件逐条一致；非零退出会打印每个不一致的文件与行号，以及缺失的插件入口：

- `identity.csprojs[]` 的 `<Version>`；
- `identity.plugin` 的 `PluginGuid` / `PluginVersion` 常量；
- `identity.module` 的 `ProviderId` / `Version` 常量（`null` 表示该包没有模块定义）；
- `<包目录>/manifest.json` 的 `name` / `version_number` / `dependencies`，并顺带核对本发行集内部依赖的版本与 `release.json` 相同。

脚本只读；它不生成也不修复任何文件。

## 字段

顶层：

- `kind`：固定 `forge-base-release-identity`。
- `schemaVersion`：本文件形状的版本；字段变化时递增。
- `author`：Thunderstore 作者名，包名一律 `<author>-<包名>`。
- `websiteUrl`：写进各包 `manifest.json` 的 `website_url`。
- `baseDependencies`：Rundown 导出使用的基础依赖精确串及来源。它不写进六个基础包的 `manifest.json`（基础包只声明自己的硬依赖），供后续打包与网站依赖闭包任务读取。
- `packages`：六个基础包，顺序固定。

每个 package：

- `packageName`：`NAinfini-<包名>`；包目录名就是去掉 `NAinfini-` 前缀的部分。
- `pluginGuid`：`BepInPlugin` 的 GUID。
- `providerId`：该包注册的主要 provider id；宿主是运行身份 `forge.runtime`，`ForgeDevelopment` 不注册 provider，为 `null`。
- `providerIds`：该包实际注册的全部 provider id（ordinal 升序），供网站按计划 binding 的 provider id 反查包。宿主另有内建合同 provider `forge.contract.combat` / `forge.contract.control`，所以它有三个。
- `version`：Thunderstore 包版本，同时是插件版本；同一数字只应出现在 `release.json`、`identity` 指出的文件与 `manifest.json` 里。
- `audience`：`player` 或 `author`；`ForgeDevelopment` 为 `author`，不得进入任何玩家依赖闭包。
- `dependencies`：Thunderstore 精确串，只写实际硬依赖。GTFO-API 0.5.0 由 `BepInEx-BepInExPack_GTFO-3.2.2` 随包提供，因此没有单独的 GTFO-API 串。`ForgeDevelopment` 对 InfiniTweaks 只有软依赖，故意不写。
- `identity`：重复写出该包身份的文件位置。`csprojs` 是产出该包插件 DLL 的项目（ForgeTrigger 的 `Native/ForgeTrigger.Native.csproj` 按裁定 12 独立成包后才存在）；`plugin` 是插件入口；`module` 是 provider 定义，`null` 表示该包没有。
- `files`：zip 根目录应恰好包含的文件，先是本包 DLL，再是合同 §1.4 要求的 `manifest.json`、`icon.png`、`README.md`、`CHANGELOG.md`。`ForgeEnemy` 只发 Native 插件（它的 provider 定义在该程序集内），其余包的 provider 定义程序集与插件程序集一起发。`ForgeTrigger` 的 DLL 名按 ForgeMap/ForgeWeapon 的既有结构假定为 `ForgeTrigger.Native.dll` + `ForgeTrigger.dll`，T3 落地时若结构不同需同步本字段。

## 基础依赖来源

| 精确串 | 来源 |
| --- | --- |
| `BepInEx-BepInExPack_GTFO-3.2.2` | QA profile `mods.yml`（`BepInEx-BepInExPack_GTFO`，3.2.2）；包内 `BepInEx/plugins/GTFO-API.dll` 版本 0.5.0 |
| `dakkhuza-MTFO-4.6.3` | QA profile `mods.yml` 与其 `BepInEx/plugins/dakkhuza-MTFO/manifest.json` |
| `hirnukuono-LGTuner-1.2.7` | QA profile `mods.yml` 与其 `BepInEx/plugins/hirnukuono-LGTuner/manifest.json` |
| `Inas07-LocalProgression-1.3.7` | 本机四个 profile 都未安装，串取网站 `site/map-package.ts` 的 `localProgressionDependency`，并在 `catalog/thunderstore-package-inventory.json` 的依赖串中交叉核对 |

GTFO-API 的插件 GUID 是 `dev.gtfomodding.gtfo-api`（0.5.0）。它不是独立发行包：r2modman 缓存里的 `BepInEx-BepInExPack_GTFO/3.2.2/BepInExPack_GTFO/BepInEx/plugins/GTFO-API.dll` 就是它的载体，网站 `site/workshop-data.ts` 也把该 GUID 解析为 `BepInEx-BepInExPack_GTFO` 3.2.2。

## 打包（尚未接入）

Thunderstore CLI（`tcli`）在本机未安装，本任务不下载、不安装，`Release/` 下没有 `thunderstore.toml`、也没有打包脚本。后续打包任务以本目录为输入：按 `packages[].files` 收集构建产物与四件元数据、按 `audience` 决定是否进入玩家集、按合同 §1.4 的路径排序/时间戳/压缩设置产出可重复的 `NAinfini-<包名>-<版本>.zip` 并记录 SHA-256。六个包的 `manifest.json`、`icon.png`（256×256）、`CHANGELOG.md` 已在本任务补齐；`README.md` 由各包既有文档提供。
