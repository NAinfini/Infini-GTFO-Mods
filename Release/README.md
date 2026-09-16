# Release 身份与打包输入

本目录是六个基础包发行身份的唯一来源。`release.json` 逐包声明 Thunderstore 包名、插件 GUID、provider、精确版本、受众、依赖串和 zip 里要放的文件；`check-identity.ps1` 核对源码与清单里重复写出的同一批数字。这里不做打包，也不改任何 `.cs` / `.csproj`；版本不一致只报告、由对应任务的负责人修改。

## 检查

```powershell
pwsh Release/check-identity.ps1
```

零退出表示 `release.json` 与下列文件逐条一致；非零退出会打印每个不一致的文件与行号，以及缺失的插件入口：

- `identity.csprojs[]` 的 `<Version>`，并要求该列表恰好覆盖包目录下每个 Forge 程序集工程（`tests/`、`tools/`、`obj/`、`bin/`、`artifacts/` 里的工程不算发行程序集）：漏写一个工程、或写了包外的工程都会失败；
- `identity.plugin` 的 `PluginGuid` / `PluginVersion` 常量；
- `identity.module` 的 `ProviderId` / `Version` 常量（`null` 表示该包没有模块定义）；
- `identity.plugin` 里每个 `[BepInDependency(...)]`：硬依赖必须带最低版本，字面量要恰为 `">="` 加 `release.json` 依赖图里的版本（本发行集内的包取该包的 `pluginGuid` 与版本，基础依赖取它 `provides` 的 `guid@version`）；BepInEx 6 把该参数按 SemVer Range 解析，裸版本是精确匹配，所以核对的是 `>=` 这一种拼写；本发行集内的每条硬依赖都必须在插件入口里以带最低版本的硬依赖出现；软依赖保持不带版本的形态，不参与这张图；
- `<包目录>/manifest.json` 的 `name` / `version_number` / `dependencies`，并顺带核对本发行集内部依赖的版本与 `release.json` 相同；
- `attachmentKinds`：每个包声明的挂载种类，必须恰好等于该包自己的非测试源码里注册的挂载匹配器种类（`AttachmentMatchers` 里由 `AttachmentMatcherRegistration` 工厂构造的条目，字典键可以是字面量或本包声明过的字符串常量）；声明了却没有匹配器、注册了却没有声明都会失败——网站按这份声明写计划的依赖闭包，缺一个就会让玩家的计划在装载时被拒。

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
- `attachmentKinds`：该包注册挂载匹配器的挂载种类（ordinal 升序），只写在有匹配器的三个包上。网站据此把计划里出现的挂载种类解析成"需要哪个包"，再写进 `manifest.dependencies` 与发行集依赖闭包；与包内源码不一致由 `check-identity.ps1` 拦下。缺这个字段表示该包不注册任何挂载匹配器。
- `version`：Thunderstore 包版本，同时是插件版本，也是包内每个 Forge 程序集工程的 `<Version>`；同一数字只应出现在 `release.json`、`identity` 指出的文件、各包 Forge 程序集工程与 `manifest.json` 里，其他包依赖它时写成 `[BepInDependency(guid, ">=<版本>")]`：BepInEx 6 把该参数按 SemVer Range 解析，写裸版本会让整条 Forge 栈在小版本升级后拒绝加载。
- `audience`：`player` 或 `author`；`ForgeDevelopment` 为 `author`，不得进入任何玩家依赖闭包。
- `dependencies`：Thunderstore 精确串，只写实际硬依赖。GTFO-API 0.5.0 由 `BepInEx-BepInExPack_GTFO-3.2.2` 随包提供，因此没有单独的 GTFO-API 串。`ForgeDevelopment` 对 InfiniTweaks 只有软依赖，故意不写。
- `identity`：重复写出该包身份的文件位置。`csprojs` 是产出该包发行程序集的项目，包目录下每个这样的工程都要列上（包根、`Native/`，以及 `Framework/` 这类嵌套目录；测试、工具与构建输出工程不列）；`plugin` 是插件入口，它声明的 `[BepInDependency(...)]` 也按 `release.json` 的依赖图核对最低版本；`module` 是 provider 定义，`null` 表示该包没有。
- `files`：zip 根目录应恰好包含的文件，先是本包 DLL，再是合同 §1.4 要求的 `manifest.json`、`icon.png`、`README.md`、`CHANGELOG.md`。`ForgeEnemy` 只发 Native 插件（它的 provider 定义在该程序集内），其余包的 provider 定义程序集与插件程序集一起发：`ForgeTrigger` 发 `ForgeTrigger.Native.dll` + `ForgeTrigger.dll`，与 ForgeMap/ForgeWeapon 的结构相同。

## 基础依赖来源

| 精确串 | 来源 |
| --- | --- |
| `BepInEx-BepInExPack_GTFO-3.2.2` | QA profile `mods.yml`（`BepInEx-BepInExPack_GTFO`，3.2.2）；包内 `BepInEx/plugins/GTFO-API.dll` 版本 0.5.0 |
| `dakkhuza-MTFO-4.6.3` | QA profile `mods.yml` 与其 `BepInEx/plugins/dakkhuza-MTFO/manifest.json` |
| `hirnukuono-LGTuner-1.2.7` | QA profile `mods.yml` 与其 `BepInEx/plugins/hirnukuono-LGTuner/manifest.json` |
| `Inas07-LocalProgression-1.3.7` | 本机四个 profile 都未安装，串取网站 `site/map-package.ts` 的 `localProgressionDependency`，并在 `catalog/thunderstore-package-inventory.json` 的依赖串中交叉核对 |

GTFO-API 的插件 GUID 是 `dev.gtfomodding.gtfo-api`（0.5.0）。它不是独立发行包：r2modman 缓存里的 `BepInEx-BepInExPack_GTFO/3.2.2/BepInExPack_GTFO/BepInEx/plugins/GTFO-API.dll` 就是它的载体，网站 `site/workshop-data.ts` 也把该 GUID 解析为 `BepInEx-BepInExPack_GTFO` 3.2.2。

## 运行清单导出（`export-runtime-manifest/`）

`export-runtime-manifest` 是控制台工程，在进程内按真实宿主顺序重放一次注册，并输出与游戏内写出的 `BepInEx/ForgeRuntime/runtime-manifest.json` 同形的文件：先是 Runtime 自己的合同模块（`CombatContracts`、`ControlContracts`），再是 `release.json` 里每个 `audience=player` 的领域包。宿主内建模块走的是 SDK 内部入口 `RegisterBuiltinModule`，这里用公开的 `RegisterModule(..., RuntimeLogLevel.Off)`；两者只差日志级别，清单里没有任何一行来自级别。

```powershell
dotnet build Release/export-runtime-manifest/export-runtime-manifest.csproj -c Release --artifacts-path $env:TEMP\relset
& "$env:TEMP\relset\bin\export-runtime-manifest\release\export-runtime-manifest.exe" `
  --release Release/release.json --output <runtime-manifest.json 的路径>
```

- 模块来源是各包自己的声明，不是复制品：`ForgeMap/ModuleDefinition.cs`（连同 `PlayerSelectorContract.cs`、`MapObjectContract.cs`）、`ForgeWeapon/ModuleDefinition.cs`、`ForgeTrigger/ModuleDefinition.cs` 与 `ForgeTrigger/Pure/**`、`ForgeTrigger/Targeting/**` 以源文件编译进来，只引用 `ForgeRuntime/Framework` 一个 SDK 程序集，避免同一 `RuntimeModule` 类型出现两份身份。ForgeMap 的玩家选择器求值器属于原生程序集，这里用 `ForgeMap/tests/MapContracts/TestWorld.cs` 同款的替身注册（清单里没有求值器行）。
- `ForgeEnemy` 的 provider 定义在游戏绑定的原生程序集里（`Native/EnemyModule.cs` 持有唯一可执行 provider），因此该模块按 `ForgeEnemy/tests/BehaviorObservation` 的做法，用 `ForgeEnemy/tests/NativePlugin/GameDoubles.cs` 的替身编译后在本进程内注册。它注册的是同一份 `RegistryJson`，没有第二份声明。
- 运行身份的 `gameBuild` 从宿主源码 `ForgeRuntime/GameBindings/GameRuntimeBridge.cs` 的 `GameBuild` 常量读取：这是该数字的唯一来源，工具不复制一份。
- 任何 `audience=player` 的包没有可用模块、或导出后的 provider 集合与 `release.json` 不一致时，工具失败退出，不写出清单——发行集要么完整，要么不成立。
- 输出使用和 `FrameworkFiles.WriteManifest` 相同的写法（UTF-8 无 BOM、不追加换行），连跑两次逐字节一致；网站的 `catalog/forge-release/runtime-manifest.json` 直接复制这份字节。

## 内容钉导出（`export-content-pins/`）

`export-content-pins` 是控制台工具，把"这次发行到底装了什么"写成一个 JSON：宿主接受的那个 `GameAssembly.dll`，以及每个 `audience=player` 包实际发出的每个程序集的 SHA-256。网站把它原样复制成 `catalog/forge-release/content-pins.json`（再由 `MANIFEST.json` 覆盖这些字节），strict `gtfo-forge-loadout` 策略靠它把玩家的安装锁死在这次发行上：游戏程序集、每个包的插件程序集 GUID 与摘要。任何一处对不上，策略就不该被写出来。

```powershell
dotnet build Release/export-content-pins/export-content-pins.csproj -c Release --artifacts-path $env:TEMP\pins
& "$env:TEMP\pins\bin\export-content-pins\release\export-content-pins.exe" `
  --release Release/release.json `
  --packages-root <每个包一个目录的打包暂存根> `
  --game-root "<GTFO 安装目录>" `
  --runtime-manifest <runtime-manifest.json 的路径> `
  --output <content-pins.json 的路径>
```

- 包名、插件 GUID、版本和文件表全部读 `Release/release.json`，工具里没有第二份名单；`audience=author` 的包（`ForgeDevelopment`）不进这份文件，它的程序集也不会被读取。
- 每个玩家包的 `files` 里 `.dll` 条目按声明顺序逐个哈希，`files[0]` 就是携带插件 GUID 的那个程序集——`loadout` 的 plugins pin 取它。包内其它非 DLL 文件（`manifest.json`、`icon.png`、`README.md`、`CHANGELOG.md`）不在这枚钉的范围内：它们由 `check-identity.ps1` 与网站发行集负责，不是可加载内容。
- 游戏程序集从 `--game-root` 下的 `GameAssembly.dll` 读取，或从 `--bepinex-root`（其父目录即游戏根）反推；宿主源码 `ForgeRuntime/GameBindings/GameRuntimeBridge.cs` 的 `GameBuild` 与 `GameAssemblySha256` 是这两个数字的唯一来源，实际文件的摘要必须与它相等，运行清单的 `runtime.gameBuild` 也必须与它相等。本机 GTFO（build 20403457）实测哈希与该常量逐字符相同。
- 输出是 `{gameAssembly:{gameBuild,sha256},plugins:[{packageName,pluginGuid,version,files:[{path,sha256}]}]}`，hash 一律小写十六进制，`plugins` 按包名 ordinal 升序，UTF-8 无 BOM、LF 结尾。
- 暂存根布局是 `<packages-root>/<packageName>/<release.json 里声明的文件名>`，`packageName` 是完整的 Thunderstore 名（如 `NAinfini-ForgeRuntime`）。工具只在显式给出的暂存根里找文件，不去 `bin/` 里猜"最近构建的那一个"；缺目录、缺 DLL、没有玩家包、哈希与宿主绑定不符都会退出 1，不写半份 pin，也不产出空 pin 文件。

```powershell
pwsh Release/test-content-pins.ps1
```

该脚本只用 `%TEMP%` fixture（自带 fixture 宿主源码与 fixture 游戏程序集）驱动真实命令行，覆盖成功路径、author 包排除、稳定排序、缺 GameAssembly、缺 DLL、改一字节、`gameBuild` 不符、`--bepinex-root` 反推与参数互斥；它不写仓库，也不需要本机游戏。

## 打包（尚未接入）

Thunderstore CLI（`tcli`）在本机未安装，本任务不下载、不安装，`Release/` 下没有 `thunderstore.toml`、也没有打包脚本。后续打包任务以本目录为输入：按 `packages[].files` 收集构建产物与四件元数据、按 `audience` 决定是否进入玩家集、按合同 §1.4 的路径排序/时间戳/压缩设置产出可重复的 `NAinfini-<包名>-<版本>.zip` 并记录 SHA-256。六个包的 `manifest.json`、`icon.png`（256×256）、`CHANGELOG.md` 已在本任务补齐；`README.md` 由各包既有文档提供。打包过程顺手把每个包的 `packages[].files` 放进 `<暂存根>/<packageName>/`，`export-content-pins --packages-root` 与网站发行集都直接读这份暂存；在拿到这份暂存之前，发行集里没有 `contentPins`，strict loadout 策略也不会被写出来。
