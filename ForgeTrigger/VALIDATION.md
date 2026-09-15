# ForgeTrigger 验证记录

**上次更新：2026-09-14**（由原 `VALIDATION.md` 与 `VALIDATION-CURRENT.md` 合并而成，另并入 T1-CONTRACT-MAP、T1-T7-STATUS 与四份 T2 交接记录的实际结果）。

计划与状态见两仓统一框架第 6 节 U-TRIGGER（链接见[仓库 README](../README.md)）；本文只记带日期的运行记录。

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
