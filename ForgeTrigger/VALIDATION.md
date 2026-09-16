# ForgeTrigger 验证记录

**上次更新：2026-09-15**（由原 `VALIDATION.md` 与 `VALIDATION-CURRENT.md` 合并而成，另并入 T1-CONTRACT-MAP、T1-T7-STATUS 与四份 T2 交接记录的实际结果）。

计划与状态见两仓统一框架第 6 节 U-TRIGGER（链接见[仓库 README](../README.md)）；本文只记带日期的运行记录。

## 观察条件、集合选择器与事件角色选择器注册 16 行；exists 三态读取（2026-09-15）

`Targeting/ObservedQueryModule.cs` 成为第二张声明表：16 行 `execution: query` 的能力，每行给出目录形状、`observe` binding 与 handler，`ModuleDefinition.Create()` 组合 `PureModule` 与它；纯计算 23 行未改。框架侧在 `Contracts.cs` 的 `RuntimeQuerySession` 段新增 `EntityPresence` 与 `TryPresence`：活着的引用 `Current`、kernel 已证明失效或缺失（`stale-world`/`stale-entity`）`Absent` 且**不记 refusal**、观察无法完成 `Unknown` 且照常记 refusal；该读取与 `TrySnapshot` 共用同一每 tick 查询预算，只有 `forge.condition.predicate.exists` 的 handler 使用它，没有放宽其它语义。

角色选择器（`self`/`owner`/`source`/`instigator`/`event_target`）只从求值上下文 `EvaluationContext.Actors` 读角色，本包不重建角色→端口映射。缺席语义按网站断言：`owner`/`source`/`instigator`/`event_target` 输出 null，`self` 由 handler 以 `actor-missing` 拒绝该步。

实际运行的命令与结果：

| 命令 | 结果 |
| --- | --- |
| `dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release --artifacts-path $env:TEMP\trigobs\build` | 成功，0 警告 0 错误，exit 0 |
| `dotnet build ForgeTrigger/ForgeTrigger.csproj -c Release …` | 成功，0 警告 0 错误，exit 0 |
| `dotnet build ForgeTrigger/Native/ForgeTrigger.Native.csproj -c Release … -p:GTFOBepInExPath=$env:GTFO_BEPINEX_PATH -p:ForgeRuntimeAssembly=$env:TEMP\trigobs\build\bin\ForgeRuntime\release\ForgeRuntime.dll` | 成功，0 警告 0 错误，exit 0 |
| `dotnet build Release/export-runtime-manifest -c Release …` | 成功，exit 0（上一轮记录的 `EnemyModule*.cs` 与 Unity 引用冲突本轮不再出现） |
| `dotnet run --project ForgeRuntime/tests/Framework -c Release` | `Framework checks: 532 passed`，exit 0；其中 5 条是注册的 `forge.condition.predicate.exists` 行经 kernel 派发的端到端用例（Current 为 true、Absent 为 false 且步骤不拒绝、Unknown 以原错误码拒绝、每 tick 预算 64 次读取后第 65 次 `query-budget`），另 45 条是同一张声明表其余 15 行的 handler 用例（`count` 的六个比较成员与候选预算边界、七个集合选择器的有序/去重/种子/`empty` 策略、`entity_type` 与 `has_tag` 的观察读取、五个角色各有与缺席） |
| `dotnet run --project ForgeRuntime/tests/Architecture -c Release` | 无输出（全绿），exit 0 |
| `dotnet run --project ForgeRuntime/tests/RuntimeLog -c Release -- --root $env:TEMP\trigobs\runlog` | `Runtime log: 239 assertions passed; 0 scenarios failed`，exit 0 |
| `dotnet run --project ForgeRuntime/tests/Network -c Release` | `network suite: 393 passed, 0 failed`，exit 0 |
| `dotnet run --project ForgeRuntime/tests/GameBindings -c Release`（无参） | `37 native-module boundary assertions; BLOCKED 2`（宿主拒绝子进程创建 NTFS junction），exit 0 |
| `python ForgeTrigger/tools/validate-pure.py --site <网站仓> --out $env:TEMP\trigobs\pure3` | exit 0：`pure-vectors` 1355 断言 / 293 组、`collection-vectors` 814 断言 / 272 组、`PureTests` **1697 断言 0 失败** |
| `node ForgeTrigger/tools/spatial-vectors.mjs` / `recipient-filter-vectors.mjs` | exit 0：360 断言 / 117 组、628 断言 / 164 组 |
| `dotnet … ForgeTrigger.ContractTests.dll export <ForgeTrigger> <输出> <网站仓>` | exit 0，**124 断言全部 PASS**：39 行逐行有 capability/binding/evaluator/shape/support，且 kind、label、description、graph 与网站目录逐字段一致（`collection-vectors.mjs` 的档位断言与既有向量期望未改） |
| `dotnet … ForgeTrigger.R3ConsumerTests.dll <结果> <spatial-reference> <recipient-filter-reference>` | exit 0，**1992 断言 0 失败**（含 exists 三态、count 边界、集合有序/去重、random/shuffle 种子确定性、五个角色各有与缺席） |
| `dotnet … export-runtime-manifest.dll --release Release/release.json --output $env:TEMP\trigobs\runtime-manifest.json` | exit 0：`5 player packages, runtime 1.2.0 on game build 20403457` |
| `node --import ./Tools/register-typescript.ts %TEMP%\trigobs\compare-catalog.mjs <网站仓> $env:TEMP\trigobs\runtime-manifest.json` | exit 0，`status passed`：可授权行（按 id）33 → 73，可运行行（按目录档位对应的 binding role）2 → 40，形状不符 0 处 |
| `python ForgeTrigger/tools/validate-t1.py --site <网站仓> --out $env:TEMP\trigobs\t1` | **exit 1**：C# `export` 段通过；TypeScript 段仍在 `SDK canonical must equal the locked website definition: forge.action.combat.damage` 失败（C# `1.0.0` / 网站 `2.0.0`，该契约在 `ForgeRuntime/Framework/CombatContracts.cs`，属伤害任务，本批未改），因此 `check` 段与 29 条 wire 断言本轮仍未执行 |

覆盖对比的余量：网站目录本轮已扩到 577 行（上一任务记录时是 62 行），可运行 40 行；仍缺 537 行，按档位是 host 426、presentation 7、pure 27、query 77，按类别是 trigger 192、action 178、condition 53、control 45、selector 30、modifier 20、event 7、state 5、variable 7。除本表注册的 16 行外，selector/condition 的余量属空间、筛选、控制等其它任务的行。

## 验证入口修复：目录档位、t1 结果行与挂载目标、证据目录（2026-09-15）

三处入口与网站当前合同不一致，逐个对齐后重跑；产物全部写在 `%TEMP%\trigfix`（本任务禁写 `ForgeTrigger/artifacts/**`）。

| 命令 | 结果 |
| --- | --- |
| `dotnet build ForgeTrigger/ForgeTrigger.csproj -c Release --artifacts-path $env:TEMP\trigfix\build` | 成功，0 警告 0 错误 |
| `node ForgeTrigger/tools/collection-vectors.mjs <网站仓> <输出目录>` | 修复前 exit 1（断言目录行 `execution === 'pure'`，`forge.condition.predicate.count` 已是 `query`）；改断言后 `{"status":"passed","assertions":814,"cases":272,"primitiveCount":8}`，exit 0，写出 `collections-reference.json` 与 `website-numeric-boundary.json` |
| `python ForgeTrigger/tools/validate-pure.py --out $env:TEMP\trigfix\pure` | exit 0：`pure-vectors` 1355 断言 / 293 组、`collection-vectors` 814 断言 / 272 组、`PureTests` 1697 断言 0 失败 |
| `dotnet … ForgeTrigger.ContractTests.dll export <ForgeTrigger> <输出> <网站仓>` | exit 0，76 断言全部 PASS；`test.trigger.record` 的结果端口补齐 `fields` 后 t1 seed 能注册，导出不再被 `ValidateResultFields` 拒绝 |
| `node ForgeTrigger/tools/spatial-vectors.mjs` / `recipient-filter-vectors.mjs` / `collection-vectors.mjs` | exit 0：360 断言 / 117 组（2 组 recorded 形状）、628 断言 / 164 组、814 断言 / 272 组 |
| `dotnet … ForgeTrigger.R3ConsumerTests.dll …` | exit 0：1858 断言 0 失败 |
| `python ForgeTrigger/tools/validate-t1.py --site <网站仓> --out $env:TEMP\trigfix\t1` | exit 1：C# `export` 段 76 断言通过；TypeScript 段在“SDK canonical must equal the locked website definition: forge.action.combat.damage”失败（见下） |

本批改动与原因：

- `collection-vectors.mjs`：八行集合定义的档位断言由 `pure` 改为 `query`，与网站目录一致。
- `tests/fixtures/t1/seed.json`：`test.trigger.record` 的结果端口按结果行规则补 `fields`——前四列固定为 `target`/`status`/`committed`/`code`，其后是该动作自己写出的领域列 `value`（number，与它同名输出端口一致）。没有放宽校验。
- `tools/validate-t1.py`：`Contracts` 的 `export`/`check` 调用改为 4 个参数，第 4 个是网站仓目录（命令行 `--site` 传入，默认与其余入口相同）；缺目录时 `tests/Contracts` 以 `FileNotFoundException` 直接失败，不跳过。
- `tools/validate-pure.py`、`tools/validate-t1.py`、`tools/validate-independent.py`：证据目录可以是 `ForgeTrigger/artifacts` 内的新目录或系统临时目录下的新目录，其他位置（可能覆盖仓库跟踪文件）与已存在的目录一律拒绝；默认仍是各自的时间戳目录。
- `tools/t1-contracts.mjs`：编译选项补必填的挂载目标 `attachments: [{kind:'level', reference:'test.resource'}]`（计划 ABI 要求 `attachments[]` 必填非空）；`invalidGraphs` 的 compile 段支持 `errorKind: "prose"`，用于消息不带码段的拒绝。
- `tests/fixtures/t1/cases.json`：按当前编译器实际行为更新失效期望——`execution-fanout` 与 `optional-required-recipient` 改为 prose 断言，`dynamic-node-output` 与 `empty-entrypoint` 改用实现真正抛出的码/文本，`pure-evaluator-needs-r4` 改名 `pure-evaluator` 并移入 `validGraphs`（纯计算步骤现在由数据边可达即可进入计划，不再被 `pure-shape` 拒绝）。

`validate-t1.py` 仍未通过，原因在本包之外：`auditAuthoringContracts` 要求 C# canonical 清单与网站 `logic-primitives.ts` 的定义逐字段相等，而 `forge.action.combat.damage` 在 C# 仍是 `1.0.0`、网站已是 `2.0.0`（两者除版本号外逐字段相同，已用一次离线比较确认）。该契约在 `ForgeRuntime/Framework/CombatContracts.cs`，由伤害任务负责，本批未改。因此 `validate-t1.py` 的 TypeScript 段无法完成，C# `check` 段与 `wire-cases.json` 的 29 条计划断言本次没有执行。

## 声明表注册 23 行纯节点，能力/端口逐字段取自网站目录（2026-09-15）

`Pure/PureModule.cs` 成为唯一声明表：一行同时给出 catalog 能力 id、kind、label、description、graph、evaluate binding、handler 与 `HandlerShape`，`ModuleDefinition.Create()` 由这张表生成 providers/capabilities/bindings/Evaluators/Shapes/BindingSupport，不再手写单个 compare 行（`BindingId` 仍是 `forge.module.trigger.binding.<名字>`，handler 仍是 `trigger.<kind>.<名字>`，compare 的两个 id 与旧注册逐字相同）。23 行全部 `execution: pure`，graph 的 domains、inputs、outputs、parameters（含 `set`/`values`/`minimum`/`maximum`）与 `variadic` 全部按目录行书写，没有任何一行自行发明端口。`add`、`multiply`、`minimum`、`maximum`、`all`、`any` 是目录声明的可变端口行：端口按 `input_count`（2…32）在计划里展开，注册时没有固定布局，所以它们的 shape 为空、由框架按计划解析。

求值器只调用既有 `Pure/*` 助手，算法一行未改：`constant`→`ScalarNodes.Constant`，`add`/`multiply`/`minimum`/`maximum`→`VariadicNodes.Reduce`，`subtract`/`power`→`ScalarNodes.Binary`，`clamp`/`absolute`/`round`/`lerp`/`select_value`→`ScalarNodes.Clamp`/`Absolute`/`Round`/`Lerp`/`SelectValue`，`divide`→`ScalarNodes.Divide`，`vector_compose`/`vector_add`/`vector_scale`→`VectorNodes.ComposeMetres`/`AddMetres`/`ScaleMetres`，`random_range`→`SeededNodes.Uniform`，`chance`→`SeededNodes.Chance`，`compare`/`range`/`not`→`PureConditions.Compare`/`InRange`/`Not`，`all`/`any`→`VariadicNodes.All`/`Any`。枚举入参与枚举参数按 Q3 边界以成员名到达 handler，再由声明表里的成员顺序映射回枚举值。

`forge.condition.predicate.exists` **没有注册**。它要求「已证明失效的世界/生命返回 false、观察未知则报错」，而 `query` 步骤拿到的 `RuntimeQuerySession` 会把失败的读取记为 refusal，步骤随后以该错误码拒绝：求值器边界内没有一条路径能读到「已证明失效」而不被拒绝。`Targeting/ObservedEntityNodes.Exists` 的语义正确，但它需要 kernel，不在求值器边界内。伪造一个 binding 会谎称该行可用，因此本表不含该行。

实际运行的命令与结果：

| 命令 | 结果 |
| --- | --- |
| `dotnet build ForgeTrigger/ForgeTrigger.csproj -c Release --artifacts-path $env:TEMP\trigpure\build` | 成功，0 警告 0 错误 |
| `dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release --artifacts-path $env:TEMP\trigpure\build`（原生工程需要宿主程序集） | 成功，0 警告 0 错误 |
| `dotnet build ForgeTrigger/Native/ForgeTrigger.Native.csproj -c Release --artifacts-path $env:TEMP\trigpure\build -p:GTFOBepInExPath=$env:GTFO_BEPINEX_PATH -p:ForgeRuntimeAssembly=$env:TEMP\trigpure\build\bin\ForgeRuntime\release\ForgeRuntime.dll` | 成功，0 警告 0 错误 |
| `dotnet build ForgeTrigger/tests/Contracts/Contracts.csproj -c Release …` / `…/tests/Pure/Pure.csproj` / `…/tests/R3Consumers/R3Consumers.csproj` | 三次都 0 错误 |
| `node ForgeTrigger/tools/pure-vectors.mjs <ForgeTrigger> <网站仓> $env:TEMP\trigpure\pure` | `{"status":"passed","assertions":1355,"vectorCases":293,"canonicalIds":23}`，exit 0 |
| `dotnet $env:TEMP\trigpure\build\bin\Pure\release\ForgeTrigger.PureTests.dll <pure-reference.json> <result.json>` | `{"status":"passed","primitiveCount":23,"vectorCases":293,"assertions":1697,"failureCount":0}`，exit 0 |
| `node ForgeTrigger/tools/collection-vectors.mjs <网站仓> $env:TEMP\trigpure\pure` | exit 1：`collection-vectors.mjs:92` 仍断言目录行 `execution === 'pure'`，而 `forge.condition.predicate.count` 已是 `query`。该断言与 `tools/validate-t1.py` 的参数、t1 seed 的结果行字段已在下一节修好，本条只作当时的记录 |
| `dotnet $env:TEMP\trigpure\build\bin\Contracts\release\ForgeTrigger.ContractTests.dll export <ForgeTrigger> $env:TEMP\trigpure\contracts <网站仓>` | 23 行逐行 `PASS`：能力+绑定+evaluator+shape+support 齐备、kind/label/description 与目录行相等、graph 逐字段相等（domains 按集合比较）；`production Trigger advertises exactly its declaration table`、`the registered manifest carries exactly the declared rows`、`duplicate provider rejected [provider-conflict]` 全部通过。之后在 `Harness` 注册 t1 seed 时被 SDK 以 `ValidateResultFields`（`test.trigger.record`）拒绝——既有夹具问题，见下 |
| `node ForgeTrigger/tools/spatial-vectors.mjs <网站仓> $env:TEMP\trigpure\probe` | `{"status":"passed","assertions":360,"cases":117,"unimplemented":2}`，exit 0 |
| `node ForgeTrigger/tools/recipient-filter-vectors.mjs <网站仓> $env:TEMP\trigpure\probe` | `{"status":"passed","assertions":628,"cases":164}`，exit 0 |
| `dotnet $env:TEMP\trigpure\build\bin\R3Consumers\release\ForgeTrigger.R3ConsumerTests.dll …` | `{"status":"passed","assertions":1858,"failures":[]}`，exit 0 |
| `node --import ./Tools/register-typescript.ts %TEMP%\trigpure\compare-catalog.mjs <网站仓> %TEMP%\trigpure\trigger-registry.json`（在网站仓 cwd） | 授权行 62，可授权行 **10 → 32**（按 evaluate 绑定计 1 → 23），形状不符 0 处；仍缺 39 行 |
| `python ForgeTrigger/tools/validate-pure.py` | 未运行：脚本守卫要求输出位于 `ForgeTrigger/artifacts`（本任务禁写 `artifacts/**`）。按其步骤手工执行了向量生成与 `PureTests`，结果同上 |

本轮跑到但**没能跑完**的入口，原因都在本包之外（这三条已在下一节处理，保留作当时的失败记录）：

- `Release/export-runtime-manifest` 编译失败：`ForgeEnemy/Native/EnemyModule.Damage.cs`（并发任务 13:59 新建的未跟踪文件）使用 `UnityEngine.Vector3` 与 `Dam_EnemyDamageBase.BulletDamage`，而该工具以 `EnemyModule*.cs` glob 编译这个文件且不引用 Unity/interop 程序集，4 个错误、0 警告。`Release/**` 与 `ForgeEnemy/**` 都不在本任务范围，未改。因此本轮的清单证据是 `ModuleDefinition.Create()` 的 registry JSON（`%TEMP%\trigpure\trigger-registry.json`，23 能力 23 绑定），与导出工具注册的是同一个模块。
- t1 seed 的 `test.trigger.record` 结果端口仍被 `ValidateResultFields` 拒绝，`tests/fixtures/t1/seed.json` 属结果行字段形状（rtabi 合同）的同步工作。
- `collection-vectors.mjs:92` 的 `execution === 'pure'` 期望需要与本次 `spatial-vectors.mjs`/`recipient-filter-vectors.mjs` 同样的 one-line 修改；`tools/validate-t1.py` 调用 `Contracts` 可执行文件时仍是 3 个参数，而契约测试现在要求第 4 个参数（网站目录），否则以用法错误退出。

覆盖对比的剩余缺口（39 行，按 kind）：selector 20、control 10、condition 4（`count`、`entity_type`、`exists`、`has_tag`）、action 2、trigger 2、modifier 1（`transform_point`）。


## 独立成包：插件入口与宿主摘除（2026-09-15）

本包不再被宿主链接编译，改由自己的 BepInEx 插件注册，与 ForgeMap/ForgeWeapon/ForgeEnemy 同构。

- 新增 `Native/ForgeTrigger.Native.csproj`（`net6.0`、`EnableDefaultCompileItems=false`、`RequireHostInputs` 校验 `ForgeRuntimeAssembly` 与 `GTFOBepInExPath`）与 `Native/Plugin.cs`：`BepInPlugin("NAinfini.ForgeTrigger")`、`[BepInDependency("NAinfini.ForgeRuntime", "1.2.0")]`，Load 依次处理 `Runtime.Mode == Off`、`Plugin.IsSuspended`、`Plugin.Runtime == null`，然后 `RegisterModule(ModuleDefinition.Create(), cfg 的 Logging.Level)`；单次 Load 闩锁，注册失败清空句柄并重抛；`Unload()` 返回 false。注册走公开入口，同 provider 的第二次注册由内核以 `provider-conflict` 拒绝（`RuntimeRegistry.WithModule`），插件不另写预检。
- `ForgeTrigger/ForgeTrigger.csproj` 的 `Compile Remove` 增加 `Native/**/*.cs`：默认 glob 会把插件入口编进托管包，产生第二份 `Plugin` 类型。
- 宿主摘除：`ForgeRuntime/ForgeRuntime.csproj` 删掉 `Compile Include="..\ForgeTrigger\**\*.cs"`；`GameRuntimeBridge.Initialize` 删掉 `RegisterBuiltinModule(ForgeTrigger.ModuleDefinition.Create())`，只留 CombatContracts 与 ControlContracts 两个内置 provider（`RegisterBuiltinModule` 因此仍有调用者）。`RuntimeKernel`/`Framework` 一行未改（属 rtabi）。
- `Release/release.json` 在本批之前就已把本包登记为独立包；`pwsh Release/check-identity.ps1` 中本包 10 项全部通过（providerId、模块 Version、两个 csproj 的 `<Version>`、插件 GUID/版本、manifest 名称/版本/依赖串）。

实际运行的命令与结果：

| 命令 | 结果 |
| --- | --- |
| `dotnet build ForgeRuntime/Framework/ForgeRuntime.Framework.csproj -c Release -p:GTFOBepInExPath=$env:GTFO_BEPINEX_PATH --artifacts-path $env:TEMP\trigpkg` | 成功，0 警告 0 错误 |
| `dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release …` | 成功，1 警告（`Logging/RuntimeLogWriter.cs:183` CS8602，他人文件） |
| `dotnet build ForgeTrigger/Native/ForgeTrigger.Native.csproj -c Release -p:ForgeFrameworkAssembly=… -p:ForgeRuntimeAssembly=…` | 成功，0 警告 0 错误 |
| `dotnet run --project ForgeRuntime/tests/PluginStartup … --no-build` | 46 断言通过，0 场景失败，exit 0 |
| `dotnet run --project ForgeRuntime/tests/HostIntegration … -- --host <trigpkg 编译的 ForgeRuntime.dll>` | 66 断言通过，0 组失败，exit 0（新增 2 条：宿主程序集不含 `ForgeTrigger*` 类型、也不含指向该命名空间的成员调用） |
| `dotnet run --project ForgeRuntime/tests/HostConfiguration …` | 68 断言通过，0 场景失败，exit 0 |
| `pwsh Release/check-identity.ps1` | 51 项：50 通过、1 失败；唯一失败是他人文件 `ForgeWeapon/ModuleDefinition.cs:12` 的 `Version` 为 `0.2.0`（release.json 为 `0.1.0`），与本包无关 |
| `python ForgeTrigger/tools/validate-trigger.py`（无 `--mutations`，产物 `artifacts/trigger-20260915-103646`） | exit 1：`pure` 阶段 TS 向量生成通过、`collection-authoring-fixtures` 失败；`t1` 阶段 build 成功（exit 0）、export 失败；`spatial-fixtures` 失败。三处都不是本包改动：①`collection-vectors.mjs`/`spatial-vectors.mjs` 断言目录行 `execution === 'pure'`，网站目录该行已改为 `query`；②`t1` 的 export 在测试 seed 的 `test.trigger.record` 结果端口上被 SDK 以 `ValidateResultFields` 拒绝（结果行字段形状属 rtabi 的合同变更，`tests/fixtures/t1/seed.json` 未同步）。export 之前 7 条生产断言全部通过，包含 `duplicate provider rejected [provider-conflict]` |

未运行：`ForgeTrigger/tests/Pure`、`R3Consumers`、`Acceptance`（它们需要网站侧生成的向量文件，本轮生成步骤已在上面失败）；`GameBindings --fixtures` 与 `--bridge`（前者被网站夹具的 `schemaVersion: 3` 与 rtabi 的 `attachments` 要求拦住，后者被测试替身 `test.bridge.action.record` 的结果行形状拦住——两处都在夹具/测试替身侧，不是本包改动）；`GameBindings --native`；任何原生/游戏/多人/安装/发布检查。

## t1 的 TypeScript 段跟上网站编译器 v3 拒绝码（2026-09-14）

t1 的 TypeScript 段自 `a565d17`（D-017 R4-a）起从未跑完。`validate-trigger.py --mutations` 在批次 `trigger-20260914-193611` 上 pure 与 independent 已 `passed`，t1 的 C# 段首次跑到 TypeScript 段后失败于 `action-output-dependency`：`t1-contracts.mjs:43` 期望 `/direct event output/i`，网站编译器实际抛 `from-step-kind: Runtime input must read a pure step: Second.value`（`site/forge/runtime-compiler.ts:246`）。本批按网站编译器源码静态逐条核对 `cases.json` 的拒绝期望，不运行脚本。

拒绝断言改为断言码值。`t1-contracts.mjs` 新增 `rejectsCode`，解析方式与网站 `Tests/Forge/runtime-compiler.test.ts:38` 的 `rejectionCode()` 相同（`error.message.split(':')[0].trim()`），用于 `invalidGraphs` 的 `compile` 段与伪装 wire 断言（原先只匹配 `/Unsupported runtime node kind/i` 这类散文）。`graph` 段仍走 `rejects`：`site/forge/graph.ts` 的 `requireValue` 只有散文、没有码，且 pure 与 independent 段已在 `trigger-20260914-193611` 通过。5 条 compile 期望按当前码更新：

| 用例 | 旧期望（散文） | 当前码 | 依据 |
| --- | --- | --- | --- |
| `action-output-dependency` | `direct event output` | `from-step-kind` | `runtime-compiler.ts:246`；与交付记录 plan v2→v3 漂移清单一致 |
| `execution-fanout` | `execution branch requires control lowering` | `step-order` | `runtime-compiler.ts:208`、`:218` |
| `control-needs-r4` | `Unsupported runtime node kind` | `control-unsupported` | `runtime-compiler.ts:63`（`test.trigger.branch` 的 `kind` 是 `control`） |
| `pure-evaluator-needs-r4` | `Unsupported runtime node kind` | `pure-shape` | `runtime-compiler.ts:68`（`test.trigger.add` 的 `kind` 是 `modifier`） |
| `optional-required-recipient` | `Optional event output` | `optional-event-port` | `runtime-compiler.ts:277` |

`tests/fixtures/t1/seed.json` 的 `test.trigger.add` 绑定角色由 `execute` 改为 `evaluate`。契约 §3.2 第 348 行要求纯能力（`selector`/`condition`/`modifier`）只能绑 `execute`/`observe` 之外的 `evaluate`；`test.trigger.add` 是 `kind: modifier`、`graph.execution: pure`，却绑 `execute`，`site/forge/graph.ts:106` 在验证阶段就以“Pure graph node requires an evaluate binding”拒绝，编译期永远到不了，所以 `pure-evaluator-needs-r4` 的 `check`（`t1-contracts.mjs:42`，先断言作者层结构合法）也过不去。改角色后该节点能到 `nodeShape`，按 `runtime-compiler.ts:68` 以 `pure-shape` 拒绝。网站 `RuntimeRegistry.cs:169` 只对 `role: evaluate` 校验能力 `kind`，反向不校验，所以原来的 `execute` 能导出 manifest；C# 侧不因此变更。

没有改 `cases.json` 里 `invalidPlans` 的任何 `code`/`error` 字段：这些是 wire 案例，会被 `t1-contracts.mjs:60` 原样写进 `wire-cases.json` 交给 C# 的 `RuntimePlan.cs` 断言，改错一侧会让两侧不一致。两条用法与当前契约对不上、但本批不擅自改的给 Claude 裁定：`invalid-integer`（`limits.maxEventsPerTick=0` 实际抛 `Runtime budget outside limits`，v3 计划码表里应记 `plan-budget`）与 `unknown-field`（额外步骤字段实际抛 `Unknown runtime field`，v3 计划码表里没有这个码）。另有两条 `execution-slot` 负例（compile 段与计划段）的期望是已不存在的码：v3 表里仍有 `execution-slot`，但计划派发改用 `successors`，fan-out 的新码是 `step-order`，compile 段那一条已没有可编译出的触发路径；`unknown-event-port` 声明的 `event-port-missing` 与实现抛的 `Unknown runtime port slot` 不一致。这四条都不会被 `rejects` 检出，因此不影响退出码。

本批只跑了 `dotnet build ForgeTrigger/tests/Contracts/Contracts.csproj -c Release --artifacts-path %TEMP%\trigt1-build`（成功，0 错误、3 条 net6.0 EOL 警告）、`node --check ForgeTrigger/tools/t1-contracts.mjs`（退出 0）与两份 fixture 的 JSON 解析（退出 0）。**没有**跑 `python ForgeTrigger/tools/validate-trigger.py --mutations`，也没有跑 `node t1-contracts.mjs`：按共同约束第 8 条，运行由 Claude 统一执行。`runtimeReady=false`、`publicationReady=false`、`gameVerified=false` 不变。

## 三条 R4-a 之前的旧断言按当前契约更新（2026-09-14）

`python ForgeTrigger/tools/validate-trigger.py --mutations` 在 `a565d17` 之后的每个 HEAD（含 `d1a65b5`）上退出 1，`scopes` 里 pure / t1 / independent 三项 `failed`、r3 `passed`，产物 `artifacts/trigger-20260914-191912`。四条失败断言——pure 的“helpers are not advertised as runtime handlers”“production provider remains unbound”、t1 的 `ContractTests` 第 25 行“production Trigger does not advertise test handlers or support”、independent 的“weighted helper grants no runtime binding or authority”——都写于检查点 `66eb588`（2026-09-13），并且都要求生产 provider 的 capabilities/bindings 为 0。

`a565d17`（D-017 R4-a）把 `ModuleDefinition.Create()` 从空 provider 改成注册目录行 `forge.condition.predicate.compare` 与唯一的 evaluate binding `forge.module.trigger.binding.compare`。契约第 6 节 U-RUNTIME/R4-a 写“宿主链接并注册 `ForgeTrigger.ModuleDefinition`”，U-TRIGGER 记“可执行节点 1 个（`compare`，evaluate）”“除 `compare` 外的纯计算、集合、筛选与空间方法都没有注册为节点”，ForgeRuntime `tests/Architecture` 也断言 Trigger 只发布这一条能力与一条 evaluate 绑定；同一节 T1 的完成定义要求默认完整入口退出 0。因此判定为测试过时，只改测试与本文，不改生产。

更新后的断言仍然收窄到当前契约，不是删断言或放宽：

- `tests/Pure/Program.cs`：`Handlers.Count == 0` 保留，另要求 `Evaluators` 恰好是 `trigger.condition.compare`、`BindingSupport` 恰好是 `forge.module.trigger.binding.compare`；导出的 registry 恰好一条 `forge.condition.predicate.compare` 能力与一条 `role: evaluate` 绑定。
- `tests/Contracts/Program.cs`：生产模块仍然不携带测试 seed 的任何 handler/support（`RegistryJson` 不含 `test.trigger`，`Evaluators` 与 `BindingSupport` 各恰好一条 compare），导出 manifest 里恰好一条目录能力与一条 compare 绑定。
- `tests/Acceptance/WeightedTests.cs`：权重抽样仍然没有运行绑定或权限——模块的 seed、bindings、evaluators 里都不出现 `weighted`，与契约“只是方法，未注册为节点”一致。

三个套件的 `Check`/`check` 调用数都没有增减，所以下面记录的 1697 / 911 / 2557 项计数口径不变。本批只跑了 `dotnet build`（生产工程与三个测试工程，Release，`--artifacts-path %TEMP%\trigval-build`），**没有**复跑 `python ForgeTrigger/tools/validate-trigger.py --mutations`，完整入口由 Claude 统一执行。

## 空间 capsule/box 实现（2026-09-14）

`ObservedVolumeShape` 增加 `Capsule`、`Box`。`Overlap` 仍是一次形状分派：sphere `Distance ≤ radius`、cylinder `Horizontal ≤ radius && |Δy| ≤ height/2`、capsule 照网站 `logic-evaluator.ts:89` 的 `half = max(height/2 − radius, 0)`、`dy = max(|Δy| − half, 0)`、`hypot(Δx, dy, Δz) ≤ radius`，box 照同一文件 `:90-91` 的“extents 是世界轴半尺寸”逐轴 `|Δ| ≤ 半尺寸`。四种形状都是闭判定（边界点算命中，无额外容差），半径与高度仍在任何原生观察之前由共用的 `Bounds` 拒绝（`spatial-parameter`）。`SpatialTests.cs` 把生成器的 `cases`（117 行）与 `unimplemented` recorded 行（capsule/box 2 行）合并成 119 行全部消费，删除了“未实现形状只列不算”的跳过分支，并新增 `VolumeShapes`：覆盖网站向量没有的样本（`[0,3.5,0]` 在球外但在 capsule 内、`height ≤ 2·radius` 的退化、box 角点与其外 0.001）与 capsule/box 的非正半径、负高度、NaN 高度拒绝。注意 capsule 那一组向量与同参数 sphere 组结果完全相同，生成器本身区分不出“capsule 当成球”，这条由 `[0,3.5,0]` 断言补上。

`R3_MUTATIONS` 新增两种错误实现：`capsule-exclusive`（capsule 半径判定改成 `<`）与 `box-shallow`（box 纵向半尺寸改成 `radius`）。

复跑（网站工作树含并行任务的未提交改动）：

| 命令 | 结果 |
| --- | --- |
| `python ForgeTrigger/tools/validate-trigger.py --r3-only` | 退出 0；spatial 360 项、117 cases + 2 recorded；recipient-filter 628 项；R3 `passed`，1858 项（原 1836） |
| `python ForgeTrigger/tools/validate-trigger.py --r3-only --mutations` | 16 种错误实现全部检出：`capsule-exclusive` 失败于 case 117 与“短轴 capsule 退化为球”，`box-shallow` 失败于 case 118 与“box 半尺寸逐轴闭判定”；整体退出 1，唯一原因是结尾的源哈希复核报 `Consumed source changed during full validation`——运行期间并行任务在改网站 `site/forge/*.ts`（该入口的被消费源之一），不是检出失败 |
| `python ForgeTrigger/tools/validate-trigger.py --r3-only --mutations`（网站 `site/forge` 静止后复跑，产物 `artifacts/trigger-20260914-181631`） | 退出 0，`status: passed`；R3 1858 项；`capsule-exclusive` 仍失败于 case 117 与“短轴 capsule 退化为球”，`box-shallow` 仍失败于 case 118 与“box 半尺寸逐轴闭判定”，源哈希复核通过 |

## D-017 R4-a：`compare` 注册为 evaluate 绑定（2026-09-14）

`ModuleDefinition.Create()` 不再是空 provider。它注册能力 `forge.condition.predicate.compare`（逐字取目录行），以及 binding `forge.module.trigger.binding.compare`（`role: evaluate`，handler `trigger.condition.compare`）。evaluator 调用既有的 `PureConditions.Compare`，`compare_operator` 按成员下标对应 `ScalarComparison`。宿主 `ForgeRuntime.csproj` 链接本模块源码并注册它，`--export-manifest` 因此带出该 provider、能力与 binding。

证据都在 ForgeRuntime 套件里，运行记录见 [ForgeRuntime 验证记录](../ForgeRuntime/VALIDATION.md) 的“D-017 R4-a”一节：
- Framework 用真实 `ModuleDefinition.Create()` 跑 7 组 `(left, right, operator, tolerance, expected)`；
- `--fixtures` 下网站 `native-branch` 计划经 compare → branch → heal 派发。

本批**没有**复跑 `python ForgeTrigger/tools/validate-trigger.py --mutations`：同一时间 `Targeting/` 下有另一批空间过滤的未提交改动，完整入口的结果不能归到这次提交。可执行节点从 0 变为 1（仅 evaluate）；`publicationReady=false`、`gameVerified=false` 不变。

## 完整入口复跑：向量工具改从 logic-evaluator 导入（2026-09-14，`trigger-20260914-094748`）

D-009、J-003 之后的两次复跑失败：`artifacts/trigger-20260914-093810` 的 pure、independent、spatial-fixtures 退出 1，TS 向量生成报 `previewLogicPrimitive is not a function`；`trigger-20260914-094340` 只剩 independent 的 authoring-vectors 报 `preview is not a function`。根因是网站把 `previewLogicPrimitive` 从 `site/forge/logic-preview.ts` 移到 `site/forge/logic-evaluator.ts`，而 `tools/` 下 `spatial-vectors.mjs`、`recipient-filter-vectors.mjs`、`pure-vectors.mjs`、`collection-vectors.mjs`、`acceptance-vectors.mjs` 仍从旧模块导入。五处导入改为 `logic-evaluator` 后（`24e3051`），在模组 `17b3078` 加该改动、网站 `b055a577`（工作树的未提交改动不涉及 `site/forge/`）上执行 `python ForgeTrigger/tools/validate-trigger.py --mutations`：进程退出 0，summary `status=passed`、`checksStatus=passed`；pure（C# 1697 项，23 项纯计算、293 组向量）、t1（C# 911、TypeScript 361，34 组 wire，424 基础节点、62 个 typed 作者定义、目录缺失 0）、independent（Acceptance 2557 项）、r3（1836 项）。日志里各错误实现副本的 `status: failed` 行是预期的检出，summary 的 mutation 检查整体通过。

`runtimeReady=false`、`publicationReady=false`、`gameVerified=false` 不变。

## 完整入口对齐 2.0.0 目录行（2026-09-13，`align-trigger-m`）

```powershell
python ForgeTrigger/tools/validate-trigger.py --mutations
```

**2026-09-13 对齐网站 d0091834 的 2.0.0 目录行后执行**（`artifacts/align-trigger-m`，主干 d09d6bb 加未提交改动），进程退出 0，summary `status=passed`：pure（C# 1697 项，11 种错误实现全部检出）、t1（C# 911、TypeScript 361）、independent（Acceptance 2557 项，61 组可变端口与 300 组权重样例，6 种错误实现全部检出）、r3（1836 项，14 种错误实现全部检出）。版本、标签、说明与图合同全部取自网站目录行，不再保留 1.1.0 期望。

更早的 `artifacts/trigger-20260913-101425`（1.1.0 期望）与 `trigger-20260913-100541`（t1 因 `promoted` 字段失败）记录保留，但已不对应当前合同。

**通过不等于可执行或可发布**：`runtimeReady=false`、`publicationReady=false`，T1 的 domain 差异门槛仍未过，全部证据都没有加载 GTFO。

## 各套件的最后记录

计数存在大量重叠，不同层的断言不相加成独立功能数或游戏测试数。

| 套件 | 最后记录 | 口径 |
| --- | --- | --- |
| 纯计算与集合（C#） | 1697 项断言 | 23 项纯计算 + 8 项集合；含正例重复验证与边界断言 |
| 纯计算跨端样例 | TypeScript 1355、293 组共享输入 | 含 compare 容差、divide `zero_policy` 新样例 |
| 集合跨端样例 | 272 组 814 项 | 覆盖八项 2.0.0 定义与 `empty` 策略 |
| R3 角色 / 空间 / 筛选 | 1836 项 | 含空间与筛选跨端消费 |
| 筛选跨端样例 | 164 组 628 项 | 151 值正例 + 13 预期拒绝 |
| 空间跨端样例 | 117 组 360 项 + capsule/box 2 组 recorded 行（R3 1858 项，2026-09-14 复跑） | C# 消费 sphere/cylinder/capsule/box、nearest/farthest（anchor）、chain；capsule/box 两组由网站 recorded 行改为真实断言，不再列为 C# 未实现 |
| T1 跨语言 | TypeScript 361、C# 911 | 34 组 wire 样例 |
| T1 目录审计 | 424 基础节点；62 个 typed 作者定义 | D-004 共享：heal@2.0.0 与 4 个战斗/死亡 trigger 逐字段对照目录行 |
| Acceptance（可变端口与权重） | 2557 项断言 | 61 组可变端口样例、300 组权重样例 |
| 作者元数据审计 | 62 个可登记元数据、15 个非法元数据精确拒绝、1 个共享 canonical（heal）逐字段对照 | `runtimeReady=false`；可登记元数据不等于可执行节点 |

只有 `Pow` 使用 1e-14 相对误差，其余共享数值、向量与布尔输出精确匹配。**这不是任意平台任意输入的位级一致保证。**

## 错误实现检出

每一批都先验证原样隔离副本通过，再验证故意写错的副本能被具体断言检出。**编译失败不计作检错成功**，全部错误版本都先构建成功。

纯计算与集合的 11 种错误实现全部被检出，其中：ties-to-even 舍入 4 项失败、减法变加法 6 项失败、忽略显式种子 163 项失败、reject 策略除零返回零 2 项失败、compare 忽略容差 2 项失败。R3、空间与筛选的 14 种错误实现全部被检出：7 种覆盖原有的角色与空间边界（owner 回退、忽略 receiver、反向关系、unknown 变 false、球边界排除、连锁重复访问、平局排序不稳定），7 种覆盖新的筛选边界。

可变端口与权重抽样的 6 种错误实现由 `tools/validate-independent.py --mutations` 执行：先把 hash 校验过的源码复制成原样副本（2557 项全部通过），再逐个注入错误、构建并用原样运行导出的参考向量检查，要求退出码 1、失败清单非空且两组检查都实际执行。结果：variadic 只取尾部 26 项失败、all 取尾部 14 项、any 取尾部 15 项（2.0.0 的并集/交集是二元合同，原并集配对错误已不适用）；weighted 忽略质量 162 项、总是替换 176 项、忽略熵预算 2 项。运行前后源码 hash 一致。

## 已经解决的历史阻塞

**R3 观察器登记缺口已解除。** 曾经的阻塞是 `RuntimeRegistry.WithModule` 只登记 EntityResolvers 而不读取 `module.EntityObservers`，`RuntimeKernel.InspectEntity` 因此返回 `entity-observer-unavailable`，整个查询变成 `entity-query-incomplete`；`Unregister` 也只清理 resolver。这些已由 Runtime 的 R3a 在共享 SDK 中实际接通，见 [Runtime 验证记录](../ForgeRuntime/VALIDATION.md)。

**生命周期订阅注销保护也已合入。** 曾经 417 项中剩余 2 项失败（`entity observer disposed lifecycle subscription`、`subscription remains intact`），根因是 `RemoveLifecycleObserver` 允许实体观察回调注销生命周期订阅。现在只在实体观察期间禁止该入口，普通清理、停止后清理与生命周期回调自注销都保留。当时的隔离补丁提案已被合入的实现取代并删除。

**independent 缺失的 mutation 覆盖已补上。** 早先只有一个未完成的 runner 半成品，完整入口因此记为 blocked；现在由 `validate-independent.py` 实际执行，半成品已删除。

**SDK 不支持 variadic 也已解除。** `RuntimeRegistry` 曾把 `graph.variadic` 当未知字段拒绝，导致 T1 的 C# 注册在 `RuntimeJson.Shape → RuntimeRegistry.Validate` 处失败。Runtime 的 R4a 能校验该元数据并按精确 revision 解析端口；U-RUNTIME 的 plan v2 加载器会按注册合同展开 variadic/portGroups，但 T1 的作者定义没有运行绑定，**元数据可登记不等于图已可执行**。

`artifacts/` 只保留本文仍引用的批次目录（`trigger-20260914-*`、`align-*-m`、`align-t1-final`、`trigger-20260913-100541`、`trigger-20260913-101425`）；更早的批次已于 2026-09-14 删除，它们的结论以本文记录为准，不用后来的绿色结果改写这些记录。

## 复跑

完整入口：

```powershell
python ForgeTrigger/tools/validate-trigger.py --mutations
```

单项入口（`<输出目录>` 用 `ForgeTrigger/artifacts/<本次名称>` 或系统临时目录下的新目录，不需要预先存在；`validate-t1.py` 与 `tests/Contracts` 都必须得到网站仓目录）：

```powershell
python ForgeTrigger/tools/validate-pure.py --out <输出目录>
python ForgeTrigger/tools/validate-t1.py --site <网站仓> --out <输出目录>
node ForgeTrigger/tools/spatial-vectors.mjs <网站仓> <输出目录>
node ForgeTrigger/tools/recipient-filter-vectors.mjs <网站仓> <输出目录>
node ForgeTrigger/tools/collection-vectors.mjs <网站仓> <输出目录>
```

`validate-pure.py`、`validate-t1.py` 与 `validate-independent.py` 只接受 `ForgeTrigger/artifacts` 内或系统临时目录下的新目录，默认仍是各自的时间戳目录；向量脚本把 `collections-reference.json` 之类的结果写在传入的输出目录里，不要指向仓库内已有文件的目录。

R3 专项：

```powershell
python ForgeTrigger/tools/validate-trigger.py --r3-only --mutations
```

独立的可变端口与权重审计，在仓库根建立一个尚不存在的 `ForgeTrigger/artifacts/<本次名称>` 作为 `<out>`：

```powershell
dotnet build ForgeTrigger/tests/Acceptance/Acceptance.csproj -c Release --artifacts-path <out>/build --disable-build-servers
dotnet <out>/build/bin/Acceptance/release/ForgeTrigger.AcceptanceTests.dll export <out>
node ForgeTrigger/tools/acceptance-vectors.mjs ../Infini-GTFO-Model-Site <out>
node ForgeTrigger/tools/weighted-vectors.mjs <out>
dotnet <out>/build/bin/Acceptance/release/ForgeTrigger.AcceptanceTests.dll check <out>
```

任何命令非零都不继续宣称通过；不覆盖已有日志，也不复用旧的绿色日志。每次运行都记录消费源码的前后哈希——验证期间源码发生变化时，结果只对那个快照有效。

SDK 与测试工程构建保留 3 条 NETSDK1138 目标框架生命周期提示，未更改 net6.0 目标，也没有抑制这些提示。

## 边界

以上全部是实现级与合成数据的证据。**没有执行 GTFO、没有原生 API、没有多人、没有安装、没有发布。** 生产 `ModuleDefinition` 只注册 `compare` 一条 evaluate 绑定（D-017 R4-a），没有 action/observe 绑定，独立测试输出明确 `gameVerified=false`、`publicationReady=false`。测试代码与 artifacts 已从生产编译项排除；没有另起 Registry、图执行器、世界时钟或查询服务。
