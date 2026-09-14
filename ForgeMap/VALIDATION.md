# ForgeMap 验证记录

**上次更新：2026-09-14**（2026-09-13 内容合并自原 MAP1-DELIVERY 与 MAP1-IDENTITY-CONTINUATION 两份交接记录）。

## NativeEvidence 冻结输入改用 QA profile interop（2026-09-14）

按框架 §6 U-MAP-MOD 与合同"编译引用与冻结输入要指定同一个明确来源"，把 `tests/MapNativeEvidence` 的冻结输入统一到唯一来源：`%APPDATA%\r2modmanPlus-local\GTFO\profiles\Forge-MapEditor-QA\BepInEx\interop`（构建时的 `GTFOBepInExPath` 也指向同一 profile）。此前 `evidence/map5a-player-hooks.json` 锁的是同机 `Temp` profile 的旧副本，而 `Forge-MapEditor-QA` 的 interop 于 2026-09-09 重新生成，两个程序集的 `hash.*` 与 `mvid.*` 共 4 项因此一直失败。证据等级不变：**metadata-and-static-native-call-graph-only**，没有启动游戏、没有安装或复制任何 profile 文件。

改动只有该文件 2 条 `assemblies` 记录的 `sha256` 与 `mvid`；**签名、`isVirtual`、`rva`、`readbacks` 与 7 条 `callEdges` 一字未改**，`buildId`、`gameAssemblySha256`、`dumpSha256`、`codeSection` 也未变（dump 仍是本机 build 20403457 的那份，`BF657C0E…`；`GameAssembly.dll` 仍是 `C6A5C3CD…`）。[README](README.md#复跑) 的 MAP5a 复跑段把 `$bep` 显式写成 QA profile。

换锁前的两项证明（临时 Cecil 工具，只在 `$env:TEMP` 下运行，仓库零改动）：

| 证明 | 结果 |
| --- | --- |
| Modules-ASM 逐类型/字段/属性/方法指纹 | Temp 与 QA 副本各 4283 类型、57776 字段、27357 属性、80710 方法（170127 行），除 MVID 外逐行相同 |
| SNet_ASM 逐类型/字段/属性/方法指纹 | 各 301 类型、2620 字段、805 属性、3143 方法（6870 行），除 MVID 外逐行相同 |
| 被锁成员在同 build dump 中 | 2 个 Hook RVA 各声明一次、dump 内 entries=1；7 条调用边的 8 个端点 RVA entries 全为 1 且跨度 ≤0x4000 |

| 程序集 | sha256 旧 → 新 | mvid 旧 → 新 |
| --- | --- | --- |
| Modules-ASM.dll | `A31AF38F…7943` → `E499B9C0…6D63` | `2875668a-…c926` → `6d066008-28db-4edf-9c0e-df9db732560d` |
| SNet_ASM.dll | `99175A1E…60B2` → `6DAD1168…9B2C` | `63a2b9c8-…48c5` → `143acd09-f561-44d0-9455-a78706262fb5` |

| 检查 | 命令 | 结果 |
| --- | --- | --- |
| `tests/MapNativeEvidence` 构建 | `dotnet build ForgeMap/tests/MapNativeEvidence/MapNativeEvidence.csproj -c Release --artifacts-path <临时目录> -p:GTFOBepInExPath=<QA BepInEx>` | 退出码 0，0 警告 0 错误 |
| `tests/MapNativeEvidence` 运行 | 见 [README](README.md#复跑) | QA interop 输入：退出码 0，`PASS 29/29` |
| `tests/MapNativeLayout` 运行 | 同一证据文件与 QA interop | 退出码 0，`PASS 39/39` |

本批续做（2026-09-14），把最后三份仍锁 `Temp` 的冻结输入也统一到同一来源：`evidence/map1-2026-09-12/native-api.json`、`evidence/map1-2026-09-12/native-api-recheck.json`、`evidence/map2-scope-2026-09-13/generation-api.json` 的 5 个 `interop/*.dll` 身份换成 `Forge-MapEditor-QA` 副本的 sha256 与 mvid。改动只有这 30 行（3 个文件 × 5 个程序集 × 2 个字段）：4 条 `core/*.dll` 两个 profile 字节相同、未动；`types`、`targetsSha256`、`game.*` 未动；`evidence/map1-2026-09-12/native-regions-recheck.json` 锁的是 `GameAssembly.dll` 的区段、与 profile 无关，未动；`*-result.json`、`*.log`、`solution-build-result.json` 等历史运行记录未动。`tools/verify_native_api.py` 从命令行取 capture 路径、不硬编码 profile，因此没有可改之处；复采命令的 profile 路径同步改成 QA：本文档"复跑"一节的 `$bep` 与 [GENERATION-SPEC.md](GENERATION-SPEC.md) 第 5 节。换锁后按"复跑"一节从 QA profile 复采比对：MAP1 targets 采集 32 类型 / 110 方法、`missing: 0`、build 20403457，`native-api.json` 与 `native-api-recheck.json` 各自 `verify_native_api.py --self-test` 退出码 0、`matched: true`、0 差异、自检 17 项；MAP2 targets 采集 27 类型 / 59 方法、`missing: 0`，`generation-api.json` 同样退出码 0、`matched: true`、0 差异；`verify_native_regions.py` 退出码 0、`matched: true`。

| 程序集 | sha256 旧 → 新 | mvid 旧 → 新 |
| --- | --- | --- |
| Modules-ASM.dll | `A31AF38F…7943` → `E499B9C0…6D63` | `2875668a-…c926` → `6d066008-28db-4edf-9c0e-df9db732560d` |
| GameData-ASM.dll | `3A74E665…CC7B` → `DEE52362…7106` | `7a8e1e7b-…5f3d` → `10c226b4-0ffe-41dc-8e66-7c049afdb4d9` |
| SNet_ASM.dll | `99175A1E…60B2` → `6DAD1168…9B2C` | `63a2b9c8-…48c5` → `143acd09-f561-44d0-9455-a78706262fb5` |
| UnityEngine.CoreModule.dll | `13DDFA5A…95A5` → `CB14FF81…C06C` | `e399b830-…6fe3` → `4f2d5da5-2462-491d-8c3b-a0b262e0f576` |
| UnityEngine.AIModule.dll | `46EC77EA…F693` → `B6952B1D…BF59` | `dc1f21c6-…c562` → `a9a309aa-fe5d-49fa-8dc1-655bdf053814` |

换锁前的签名等价证明（`$env:TEMP` 下的临时 Cecil 工具，只在临时目录运行，仓库零改动）：

| 证明 | 结果 |
| --- | --- |
| 5 个 interop 程序集逐类型/字段/属性/方法/参数/事件投影 | Temp 与 QA 副本的投影文件逐字节相同；投影覆盖参数名、`isOut`、`isOptional`、常量、属性读写访问器与枚举字面量，MVID 不在投影内。规模：Modules-ASM 4283 类型 / 57776 字段 / 27357 属性 / 80710 方法 / 46256 参数；GameData-ASM 102 / 8179 / 8078 / 16459 / 8179；SNet_ASM 301 / 2620 / 805 / 3143 / 2046；UnityEngine.CoreModule 2815 / 8356 / 1854 / 13191 / 18461；UnityEngine.AIModule 142 / 419 / 117 / 678 / 980 |
| 两个 profile 的 interop 同源 | 两侧 `BepInEx/interop/assembly-hash.txt` 同为 `565871abd714937729d0e74520563bec`；`GameAssembly.dll` 仍是锁定的 `C6A5C3CD…BF55`（app 493520 / build 20403457） |
| Modules-ASM、SNet_ASM 新锁值与已统一的 MAP5a 锁 | 与 `evidence/map5a-player-hooks.json` 的 sha256/MVID 一致 |

## MAP2 原生发现（2026-09-14）

按框架 §6 U-MAP-MOD 未完成项"MAP2 原生发现"与 §3.2 I-MAP-PLAN（D-013 过渡期）新增：游戏无关的 `AssemblyPlanDiscovery.cs` 发现 `BepInEx/plugins/*/forge/maps/` 并只跑 G0–G6 静态检查；原生 `Native/MapPlanDiagnostics.cs` 在插件 `Load` 注册身份后调用一次，每个计划一条有界（512 字符）诊断行。**只读、只诊断**：不生成、不改游戏状态、不注册 provider、不读网络。证据等级：**本地验证（托管发现 + 合成夹具 + 替身接线）**；没有启动游戏、没有加载 bundle、没有安装到任何 profile，通过静态检查不等于生成成功。

下表命令在**工作区**运行（当时工作区另有 ForgeRuntime 在制改动），构建一律带会话临时目录的隔离 `--artifacts-path`。提交前在 HEAD `30a09d7` 的独立 worktree 只放入本节文件复验：宿主、`ForgeMap.csproj`、`ForgeMap.Native.csproj` 与三个测试工程 0 警告 0 错误；`MapPlanDiscovery` 退出码 0、`failures: []`；`MapAssemblyPlan --self-check` 退出码 0、`failures: []`；`MapNativeAdapter` `PASS 44/44`（MapNativeLayout、Identity、NativeEvidence 未在 worktree 复跑）。

| 检查 | 命令 | 结果 |
| --- | --- | --- |
| `ForgeMap.csproj` 构建 | `dotnet build ForgeMap/ForgeMap.csproj -c Release` | 退出码 0，0 警告 0 错误 |
| `Native/ForgeMap.Native.csproj` 构建 | 先构建宿主，再 `dotnet build ForgeMap/Native/ForgeMap.Native.csproj -c Release -p:ForgeRuntimeAssembly=…` | 退出码 0，0 警告 0 错误；新增类型未触发依赖方向或只读读取检查 |
| 新测试 `tests/MapPlanDiscovery` | 构建后运行 `MapPlanDiscovery.dll` | 退出码 0，18/18（无目录、空目录、无 `forge/maps/` 的目录、缺描述符、多包、只有描述符、合法计划与 blockers、非计划文件忽略、缺/错 planId、不可解析文档、seed 越界、非规范 rotation、descriptor-lock 不符、描述符文档不可解析、重复 level layout 的 ordinal 顺序、可重复性） |
| G0–G6 变异自检（包级改走生产发现） | `MapAssemblyPlan.dll --self-check` | 退出码 0，48/48（45 条计划变异 + 3 条包级），`failures: []` |
| 变异语料两侧交叉 | `MapAssemblyPlan.dll --self-check --emit <临时目录>` 后分别运行 `MapAssemblyPlan.dll --fixtures <同上>` 与 `python ForgeMap/tools/verify_assembly_plan_fixtures.py --fixtures <同上>` | 两侧退出码均 0，`manifestVerified: true`；合法 1/1、非法 44/44、包级 3/3；包级用例现在走 `AssemblyPlanDiscovery`，与 Python `check_package` 同码 |
| `tests/MapNativeAdapter` | 构建后运行 | 退出码 0，`PASS 44/44`（41 → 44：无包时不写发现日志、每个计划一行且不改注册面、包级拒绝与 512 字符上界） |
| `tests/MapNativeLayout` | 构建后运行（输入为重建后的 `ForgeMap.dll` 与 `ForgeMap.Native.dll`） | 退出码 0，`PASS 39/39`；插件 Off 门与 `MapPluginSession::Start` 的次序断言、Hook 集合、只读调用检查仍通过 |
| MapIdentity 回归 | Release 构建后运行 `ForgeMap.Identity.Tests.dll` | 退出码 0，134 项断言，`nativeScenariosExecuted: false`（未改） |
| `tests/MapNativeEvidence` | 构建后运行（游戏 build 20403457） | 退出码 1，25/29：`hash/mvid.Modules-ASM.dll`、`hash/mvid.SNet_ASM.dll` 与本机 profile 的 interop 不一致；与 2026-09-14 上一批相同的环境差异，本次未改 Hook 或读回 |

发现规则与合同未写明的点（本次采用的解释，需裁决方复核）：

- 发现根是 `BepInEx/plugins/` 的一级子目录，只检查 `<dir>/forge/maps/` 是否存在；没有这样的目录时静默跳过（沿用 I-PACK D-009 的计划发现口径：不打开文件、不报错、不写日志）。合同只写了"恰好一个"与"多于一个全部拒绝"，**零个的语义是本次补齐的**。
- 包内有 `forge/maps/` 但缺 `rooms.descriptors.json` 时记 `assembly.package-layout`（路径为 `plugins/<dir>/forge/maps`）。合同把 `assembly.package-layout` 只写在"多于一个目录"上；这个码与路径取自 `tools/verify_assembly_plan_fixtures.py` 与 `tests/MapAssemblyPlan` 原有的同一处理。描述符文档存在但不可读或不可解析时走委派码 `descriptor.schema`（`$descriptors`）。
- 计划文件不是合法 JSON 或读不到时记 `assembly.schema`（`$`）：合同的 34 码里没有"文件读不到"这一条，取形状阶段的码。文档能解析但没有 `planId`、或文件名不等于 `planId + ".assembly.json"` 时记 `assembly.plan-file`（与两个检查器的宽松 planId 提取一致）。
- 每个计划文件单独出结果，一份被拒不影响同包其他计划（沿用 I-PACK"每个文件单独出结果"的口径）；文件身份两条码（`assembly.plan-file`、`assembly.duplicate-level-layout`）在描述符与计划文档校验之前判定，`assembly.duplicate-level-layout` 记在 ordinal 靠后的那个文件上，`assembly.package-layout` 是包级拒绝且不再出计划行。同一份计划被拒时它的 `levelLayoutId` 仍占用登记，后续同 id 文件照样报重复。
- 合同没有为 `forge/maps/` 写 I-PACK 里的链接/越界检查（`plan-path`）与单文件/合计上限（`json-size`、`plan-budget`），本次**没有实现**，等裁决方决定是否按 I-PACK 逐条补齐。
- 诊断日志暂用 BepInEx 日志的 `map.plan-accepted` / `map.plan-rejected` / `map.package-rejected` 三行（行内 `path` 为 BepInEx 相对路径），**不是** `forge.log.v1` 记录：`plan.loaded` / `plan.rejected` 归 Runtime sink。Map 的 cfg `Logging.Level`（D-007 阶段 A）已在 `Native/Plugin.cs` 绑定并随 `MapPluginSession.Start` 注册，但这三行诊断仍未改走 Runtime sink。这三个码需要按 §2.5 补进 §3.2 I-DIAG 的包内诊断码表。

**边界**：发现流程只读夹具目录并跑已有静态检查；18 项与 44 项都是合成数据与替身，不能替代实机、主客机、导航或生成验证。没有真实 Geo 包、没有网站夹具（`Tests/Forge/fixtures/map-assembly/` 仍缺 `MANIFEST.json` 与 `cases.json`），也没有 G7 生成。

## G0–G6 拼装计划静态检查（2026-09-14）

按框架 §3.2 I-MAP-PLAN（r24 关闭 Q-005）新增静态检查：`tools/verify_assembly_plan_fixtures.py`（夹具仲裁检查器，描述符部分 import `verify_resource_adapter_fixtures.document(..., '$descriptors')`）、`ResourceDescriptorReader.cs`（GENERATION-SPEC §3.1 的 C# 严格解析）、`AssemblyPlanContracts.cs` / `AssemblyPlanReader.cs` / `AssemblyPlanChecks.cs`（G0–G6 与 blockers）、`tests/MapAssemblyPlan`（夹具比对与变异自检）。证据等级：**fixture-schema-only / 静态检查**；没有加载 bundle、没有调用原生、没有运行游戏，通过静态检查不等于生成成功（v1 每份合法计划恒带 `dimension-bounds-unknown`）。

全部构建使用会话临时目录的隔离 `--artifacts-path`。

| 检查 | 命令 | 结果 |
| --- | --- | --- |
| 描述符检查器（重构后） | `python ForgeMap/tools/verify_resource_adapter_fixtures.py` | 退出码 0；合法 3/3、非法 21/21；与重构前对比脚本比对 24 条既有夹具：23 条错误码与文本逐字相同，1 条把 `sharedBytes[0]…` 补成 `$.sharedBytes[0]…`（本次把根路径前缀参数化，码未变） |
| G0–G6 变异自检 | `dotnet ForgeMap/tests/MapAssemblyPlan/MapAssemblyPlan.csproj` 后运行 `MapAssemblyPlan.dll --self-check` | 构建 0 警告 0 错误；退出码 0；48/48（45 条计划单点变异 + 3 条包级） |
| Python/C# 交叉一致 | `MapAssemblyPlan.dll --self-check --emit <临时目录>` 后分别运行 `MapAssemblyPlan.dll --fixtures <临时目录>` 与 `python ForgeMap/tools/verify_assembly_plan_fixtures.py --fixtures <临时目录>` | 两侧退出码均 0，`manifestVerified: true`；同一 48 条语料（98 个文件）：合法 1/1、非法 44/44、包级 3/3，错误码与字段路径逐字相同 |
| 网站夹具交叉运行 | `python ForgeMap/tools/verify_assembly_plan_fixtures.py --fixtures ../Infini-GTFO-Model-Site/Tests/Forge/fixtures/map-assembly` | **未运行**：网站夹具目录当前只有 `generate.ts`，没有 `MANIFEST.json` 与 `cases.json`；退出码 1，输出 `must contain MANIFEST.json and cases.json`（夹具不完整按失败报告，不抛栈）。同一检查器已用 `--emit` 生成的合成语料跑通（上一行） |
| MapIdentity 回归 | Release 构建后运行 `ForgeMap.Identity.Tests.dll` | 退出码 0，134 项断言，`nativeScenariosExecuted: false`（未改） |
| MapContracts 回归 | Release 构建后运行 `MapContracts.dll` | 退出码 -532462766：未处理 `RuntimeContractException: Unsupported plan version`（并行任务正在改 `ForgeRuntime/Framework/RuntimePlan.cs`；本次改动前同样失败） |
| 架构回归 | Release 构建后运行 `Architecture.dll` | 退出码 -532462766：`FAIL: Weapon still owns its two observed wield triggers`（并行任务正在改 `ForgeTrigger`/`ForgeWeapon` 与该测试文件；本次改动前同样失败） |
| `Native/ForgeMap.Native.csproj` 构建 | 宿主与原生构建 | 退出码 0，0 警告 0 错误 |
| `tests/MapNativeAdapter` | 构建后运行 | 退出码 0，`PASS 41/41` |
| `tests/MapNativeLayout` | 构建后运行（输入包含重建后的 `ForgeMap.dll`） | 退出码 0，`PASS 39/39`；新增的静态类型没有触发依赖方向或只读读取检查 |
| `tests/MapNativeEvidence` | 构建后运行 | 退出码 1，25/29：`hash/mvid.Modules-ASM.dll` 与 `hash/mvid.SNet_ASM.dll` 与本机 `Forge-MapEditor-QA` profile 的 interop 不一致（游戏 build 20403457、`native.hash`、`dump.hash` 均通过）；环境差异，本次未改 Native 或证据文件 |

变异自检覆盖框架 §3.2 拒绝顺序的每一条：#1 用三个 `descriptor.*` 变异（`revision-mismatch`、`evidence-escalation`，以及描述符文档本身不是 JSON 时归 `descriptor.schema`，委派路径 `$descriptors`）、#2–#34 各一条、包级 3 码各一条，另有基线合法用例（blockers `colliders`、`dimension-bounds-unknown`、`navigation`、`occlusion`）。**#16 `assembly.locator-unsupported` 在当前顺序下不可达**：检查 10 已要求每个声明的 zone 是 dimension 0 / layer 0，检查 15 已要求 locator 被某个 zone 声明，因此不可能有"已声明但 dimension/layer 非 0"的 locator。自检用一条变异钉住该顺序（改 zone 与 locator 的 layer 后报的是 `assembly.zone`），并在最终报告里请裁决方确认 #16 是否保留。

## MAP5a — gtfo.player 原生实例解析（2026-09-13）

`PlayerIdentityModule` 同时登记 `EntityInstanceResolvers["gtfo.player"]`，Weapon 由此经 SDK 的 `ResolveEntityInstance` 取得装备 owner，接口见 [Runtime 验证记录](../ForgeRuntime/VALIDATION.md#原生实例解析2026-09-13)。证据等级不变：**implementation-only + 本机静态原生证据**；没有新的原生读取签名，证据文件未改。

| 套件 | 退出码 | 输出结尾 |
| --- | --- | --- |
| `Native/ForgeMap.Native.csproj` 构建 | 0 | 0 警告 0 错误 |
| `tests/MapNativeAdapter` | 0 | `PASS 41/41 Map native player identity cases; no GTFO execution.` |
| `tests/MapNativeLayout` | 0 | `PASS 39/39 Map native layout checks; no GTFO execution.` |
| `tests/MapNativeEvidence` | 0 | `PASS 29/29 Map static native evidence checks; game execution NOT tested.` |

MapNativeAdapter 从 35 增加到 41：
- `instance.sdk-lookup-returns-recorded-life`：经 SDK 查到的就是已登记的引用。
- `instance.lookup-never-allocates-a-life`：未登记的玩家返回 null，不分配编号或 life。
- `instance.observe-gate-and-native-type`：注册期、非主机、已销毁的玩家，以及 agent、`SNet_IPlayerAgent`、字符串、数字和同 Lookup 的另一个 `SNet_Player` 都返回 null。
- `instance.lookup-rereads-native-links`：agent 互链、Lookup 或 owner 变化后返回 null，且不改表。
- `instance.stop-and-wrong-thread`：模块与内核两条路径在错线程都抛 `wrong-thread` 且不改状态；停止后模块返回 null，内核抛 `runtime-not-ready`。
- `privacy.instance-lookup-results-and-errors-exclude-account-lookup`：结果与错误文本并入输出后做 Lookup 泄漏检查，结果为没搜到。

另外两个已有用例各追加一项：外部模块给 `gtfo.player` 挂实例解析器被拒（`entity-instance-resolver-owner`）；插件成功路径能经内核解析出玩家。MapNativeLayout 从 38 增加到 39：原检查改名为 `native.player-resolver-and-instance-lookup-without-observer` 并要求登记实例解析器，新增 `native.instance-lookup-never-allocates`（单个 `object` 参数、不写字段、不调用对账或增删、不读 `Lookup` 与 `PlayerAgentsInLevel`）。

本批没有重跑 MapIdentity 与 MapContracts（身份层与 SDK 消费方源码未改）。下节表中的 35/38 是加入实例解析之前的计数。

## MAP5a — 玩家实体身份（2026-09-13）

证据等级：**implementation-only + 本机静态原生证据**。没有启动 GTFO、没有安装到任何 profile、没有主客机。全部构建使用会话临时目录的隔离 `--artifacts-path`，`GTFOBepInExPath` 指向只读的 Temp profile BepInEx；复跑命令见 [README](README.md#复跑)。

| 套件 | 退出码 | 输出结尾 | 说明 |
| --- | --- | --- | --- |
| `Native/ForgeMap.Native.csproj` 构建 | 0 | 0 警告 0 错误 | 真实 interop 编译，`ForgeRuntimeAssembly` 取同一 artifacts 的宿主构建 |
| `tests/MapNativeAdapter` | 0 | `PASS 35/35 Map native player identity cases; no GTFO execution.` | 生产源码加 loader 与游戏替身：注册顺序与回滚、身份规则、世界与停止清表、bot、迟加入、冲突、伪造引用、线程、隐私 |
| `tests/MapNativeLayout` | 0 | `PASS 38/38 Map native layout checks; no GTFO execution.` | Cecil：依赖方向、单一 provider 来源、只读且精确的玩家读取、Lookup 从不格式化或装箱、Hook 形状、插件身份与依赖、Off 门、不热卸载 |
| `tests/MapNativeEvidence` | 0 | `PASS 29/29 Map static native evidence checks; game execution NOT tested.` | build、hash、MVID、2 个 Hook 签名与 dump RVA 唯一不共享可执行、5 个读回签名、7 条直接调用边 |
| MapIdentity 回归 | 0 | 134 项断言通过，`nativeScenariosExecuted: false` | 未改 |
| MapContracts 回归 | 0 | 33 项通过 | 未改 |
| 架构回归 | 0 | `PASS 36 architecture boundary assertions. No GTFO hooks, gameplay, networking or installation exercised.` | `Program.cs` 未改 |

同批复跑 ForgeWeapon（源码未改，只改文档）：NativeAdapter `PASS 26/26`，NativeLayout `PASS 51/51`，NativeEvidence `PASS 52/52`，Identity `"cases":42,"assertions":99`，IdentityAcceptance `INDEPENDENT IDENTITY: 37/37 passed`，IdentityDispatchReview `DISPATCH REVIEW: 20/20 passed`，退出码均为 0。

中间失败与修正：
- MapNativeLayout 首跑 36/37：`GameAssembly` 判定只识别 `-ASM` 后缀，漏掉 `SNet_ASM`，精确读取集合因此缺少 SNet_Player 成员。补上 `_ASM` 后通过。
- 协调方追加隐私规则后，实体 ID 从 `gtfo.player:<Lookup>` 改为本世界编号，测试随之重写并新增 2 个隐私用例与 1 项静态检查。
- IdentityAcceptance / DispatchReview 第二次运行时报告文件已存在而拒绝覆盖（测试行已全部通过），换新报告路径重跑后退出码 0。

会话临时目录里的变异检查（只改副本，不改仓库）：去掉 agent 指针替换、去掉 `SNet.IsMaster`、去掉互链复核、去掉世界清表、冲突保留首个、实体 ID 用 Lookup、每个 life 换编号，7 个变体各自被对应的具名用例检出。报告 JSON 中搜不到任何 fixture Lookup 值。

未核验（需要游戏内确认）：bot `Lookup` 跨重生稳定性；postfix 时刻 `PlayerAgentsInLevel` 的成员是否已更新；`UnregisterPlayerAgent` 与 `OnPlayerDespawned` 的运行时顺序；倒地与救起不重建 PlayerAgent；`Object.Destroy` 延迟期间旧 agent 的状态；迟加入与主机迁移。

## MAP2 定范围（2026-09-13）

输出保存在 `evidence/map2-scope-2026-09-13/`。构建使用会话临时目录的隔离 artifacts，没有写 `ForgeMap/bin` 以外的模块输出。

| 检查 | 命令 | 结果 |
| --- | --- | --- |
| 生成相关原生签名锁 | `Capture-NativeApi.ps1 -TargetsFile tools/generation-api-targets.json` | 27 个类型、59 个方法，缺失 0，build 20403457，`metadata-only` |
| 独立复采比对 + 检查器自测 | `verify_native_api.py generation-api.json <复采> --self-test` | 退出码 0，`matched: true`，差异 0，自测 17 项 |
| 资源侧描述符 fixture | `python ForgeMap/tools/verify_resource_adapter_fixtures.py` | 退出码 0，合法 3/3（阻塞项与期望一致），非法 21/21 以期望错误码拒绝，`fixture-schema-only` |
| MapIdentity（含十个规格的托管替身标记） | Release 构建后运行 `ForgeMap.Identity.Tests.dll` | 构建 0 警告 0 错误；退出码 0，134 项断言通过；每个规格 3–17 项标记断言，`nativeScenariosExecuted: false` |
| MapContracts 回归 | Release 构建后运行 `MapContracts.dll` | 构建 0 警告 0 错误；退出码 0，33 项通过 |
| 架构回归 | `Forge.Architecture.sln` Release 构建后运行 `Architecture.dll` | 构建 0 警告 0 错误；退出码 0，36 项通过 |

MapIdentity 从 126 项增加到 134 项：新增同资源两个 placement 的兄弟回调拒绝（3 项）、同一 geomorph 两个 area 的错 area 观察拒绝（3 项），以及规格文件与标记集合一一对应的两项核对。原有断言未修改，只加了规格 id 标记。

**边界**：签名锁只证明方法存在；fixture 检查只证明描述符形状与拒绝规则，数据是合成的，不代表任何真实 Geo 包；134 项仍是托管实现加原生探针替身。没有运行游戏、加载 bundle、安装或发布。MapContracts 的 `TestWorld.cs` 在本批期间由主会话为 Framework 的 `promoted` 字段修改过，本批未改它，回归结果基于修改后的文件。

## MAP1b — 内部身份实现

| 检查 | 结果 |
| --- | --- |
| 实际 Map 身份实现 + 原生探针替身 | 126 项断言通过，退出码 0 |
| 原有 Map SDK 消费方回归 | 33 项断言通过，退出码 0 |
| 跨模块架构回归 | 通过（当时记录 35 项；**现行 Program.cs 是 36 项**，见 `ForgeRuntime/tests/Architecture/Program.cs`） |
| 三组 Release 构建 | MapIdentity、MapContracts、`Forge.Architecture.sln` 均 0 警告 0 错误 |
| 构建与测试期间的源码一致性 | 相关源码集合及 SHA-256 前后一致 |

初版 118 项断言通过后，审查把 native 探针从数字 key 提升为"创建票据 + key"的精确生命见证。随后新测试失败：测试替身把迟到的 Callback 当作新的 Bind，改写了模拟的物理生命。修复只分离了测试替身的 Bind 与 Callback 并改用正确的迟到回调路径，**没有放宽生产的身份校验**。失败记录保留在 `verification-final-1` 里，最终结论以 `verification-final-2` 的 126 项为准。

126 项覆盖地址与来源锁、int64 pathId、重复观察、冲突隔离、取消与销毁、world/generation/life、指针与 Unity ID 同时复用、probe 异常与重入、跨线程、停机与注销、历史上限、不完整采集。**这些是托管实现测试，不能替代原生创建 API、权限与阶段、主客机、导航或恢复测试。**

## MAP1 — 首批原生取证

| 检查 | 结果 |
| --- | --- |
| 本机 API 元数据与独立复采 | 32 个类型、110 个方法、9 个程序集；所选目标缺失 0；两次匹配 |
| API 检查器正反自测 | 17 项通过；故意修改方法、参数、枚举、程序集、版本或验证等级均被拒绝 |
| 命令行拒绝路径 | 6 项通过；基线未被覆盖 |
| 已有原生区域字节 | 11/11 与当前 GameAssembly 匹配 |
| 实际 SDK + 合成对象表 | 33 项断言通过 |
| Map 与测试工程 Release 构建 | 0 警告 0 错误 |

GameAssembly SHA-256：`c6a5c3cd8ca5fe2a8c1a71a3d107663e8cbf01404820dbbda2ea10c4bfd7bf55`（本机 Steam app 493520 / build 20403457）。原生区域复核是重新核对旧审计记录的字节摘要，**不是重新反汇编，也没有运行游戏**。没有复制、安装或分发游戏 DLL 与资源；程序集版本是编译元数据，不冒充 Thunderstore 包版本或许可证明。

已核验的入口示例包括 zone 与 layer 的 `LG_Zone.Create` / `LocalIndex` / `DimensionIndex` / `LG_Layer.CreateZone` / `AddZoneToLayer`，geomorph 与 area 的 `AddCustomGeomorphAreas` / `SetupAreas` / `SetPlaced` / `LG_Area.Setup`，plug 与 door 的 `LG_Plug.TryPair` / `Pair` / `LG_Gate.SpawnedDoor` / `LG_SecurityDoor.Setup` 及各种锁。**这些是签名存在的证据，不是"作者布局到本次维度层区域的唯一生成映射""创建回调的顺序与次数""运行中重建拓扑与导航与复制"的保证**——枚举可写入不等于门行为正确。

33 项 SDK 测试没有运行原生对象匹配、游戏网络或游戏权限绑定；其中的客户端与主机测试是 SDK 的权威控制测试，**不是两台真实 GTFO 客户端的验证**。

## 中间失败与修正

前三次 Map 构建遇到 Runtime 并发编辑的中间态：生命周期方法尚未配齐、重复 Advance、文件尚未写完整。原始失败输出保留在 `map-build.log` 与 attempt-2、attempt-3 里；本任务没有修改 Runtime 源码。随后构建成功。测试夹具的 provider 命名空间和未知实体的拒绝阶段按实际 SDK 修正——未知对象在入队前就被拒绝，而不是等生成命令的结果；**修正只在 Map 测试代码里，没有放宽 SDK 校验**。

## 复跑

从仓库根执行，输出目录必须尚不存在且位于 `ForgeMap` 内：

```powershell
python ForgeMap/tools/run_identity_checks.py --out ForgeMap/evidence/identity-recheck-01
```

只跑 SDK 消费方：

```powershell
$artifacts = Join-Path (Resolve-Path ForgeMap) 'bin/map1-artifacts'
dotnet build ForgeMap/tests/MapContracts/MapContracts.csproj -c Release --artifacts-path $artifacts
dotnet "$artifacts/bin/MapContracts/release/MapContracts.dll"
```

原生证据复采。路径是当前机器的已核验安装，在其他机器上必须显式替换；采集工具拒绝覆盖已有文件，每次用新的输出名，只读取游戏字节和元数据，不执行游戏代码：

```powershell
$m = (Resolve-Path ForgeMap).Path
$site = (Resolve-Path ../Infini-GTFO-Model-Site).Path
$bep = "$env:APPDATA/r2modmanPlus-local/GTFO/profiles/Forge-MapEditor-QA/BepInEx"
$game = 'E:/SteamLibrary/steamapps/common/GTFO'
$out = "$m/bin/native-api-$([Guid]::NewGuid().ToString('N')).json"
& "$m/tools/Capture-NativeApi.ps1" -BepInExRoot $bep -GameRoot $game -OutFile $out
python "$m/tools/verify_native_api.py" "$m/evidence/map1-2026-09-12/native-api.json" $out --self-test
python "$m/tools/verify_native_regions.py" "$site/artifacts/framework-implementation-2026-09-12/map-api-audit/native-regions.json" "$game/GameAssembly.dll"
```

测试的逐项进度写到 stderr，最终结构化结果写到 stdout。构建、测试与来源摘要必须一起保存；共享 SDK 后续继续变化时需要重新运行，本记录不保证未来的工作树仍然相同。

各测试目录的边界说明见 [MapContracts](tests/MapContracts/README.md) 与 [MapIdentity](tests/MapIdentity/README.md)。

## 边界

这些数量不相加为"已实现能力数"。架构解决方案只覆盖共享 SDK、五个模块骨架和架构测试，不含 Runtime 游戏宿主的实机加载验证。没有启动或安装游戏、迁移配置、打包发布，也没有执行 Git commit 或 push。构建其他模块时也使用 Map 自己的忽略输出目录，不写别的任务的 `bin/obj`。
