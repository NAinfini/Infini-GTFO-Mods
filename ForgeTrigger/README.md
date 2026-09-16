# ForgeTrigger

> 实施顺序与原版内容完整覆盖见[唯一开发计划](../../Infini-GTFO-Model-Site/Docs/forge-contract/FORGE-FRAMEWORK.md) §4，分步验收见 §7；本文件仅说明实现与用法。

跨域逻辑节点，使用唯一的 Runtime SDK。

**Trigger 的交付优先级与 Runtime 同级。** Trigger 的节点词汇是覆盖全部领域的横切基座——Map、Weapon、Enemy 各自的机制都落在它提供的原子上，玩家拼出的每一张图都从这里取积木。本包牵头的机制只有 4 组，但牵头数不决定交付顺序。

交付要求见唯一开发计划 §6 U-TRIGGER（链接见[仓库 README](../README.md)），带日期的验证记录与失败见 [VALIDATION.md](VALIDATION.md)。

## 当前能力

生产 `ModuleDefinition` 由两张声明表组合而成：`Pure/PureModule.cs`（23 行纯计算）与 `Targeting/ObservedQueryModule.cs`（16 行观察）。每行同时给出能力（id、kind、label、description、graph）、binding 与求值器/handler，注册的能力、绑定与 handler 因此不会互相漂移。注册走公开的 `RegisterModule`：本包原生插件 `NAinfini.ForgeTrigger`（`Native/Plugin.cs`）在 Load 里注册 `ModuleDefinition.Create()`，`Release/export-runtime-manifest` 用同一入口离线导出。下面其余方法编译进 `ForgeTrigger.dll`，只被自己的测试工程消费；方法存在不等于节点已注册。

## 已注册节点

39 行，全部 `execution` 与 binding role 一致：23 行 `pure` 绑定为 `evaluate`，16 行 `query`（需要世界端口或事件角色）绑定为 `observe`。类别、端口、参数与枚举集合逐字取自网站 `catalog/capability-catalog.json` 的对应行——`tests/Contracts` 带网站目录运行时逐字段比对，目录缺失直接失败，不做跳过。binding 名为 `forge.module.trigger.binding.<名字>`。

| 能力（pure） | handler |
| --- | --- |
| `forge.modifier.value.constant` | `trigger.modifier.constant` |
| `forge.modifier.value.add` | `trigger.modifier.add` |
| `forge.modifier.value.subtract` | `trigger.modifier.subtract` |
| `forge.modifier.value.multiply` | `trigger.modifier.multiply` |
| `forge.modifier.value.divide` | `trigger.modifier.divide` |
| `forge.modifier.value.minimum` | `trigger.modifier.minimum` |
| `forge.modifier.value.maximum` | `trigger.modifier.maximum` |
| `forge.modifier.value.power` | `trigger.modifier.power` |
| `forge.modifier.value.clamp` | `trigger.modifier.clamp` |
| `forge.modifier.value.absolute` | `trigger.modifier.absolute` |
| `forge.modifier.value.round` | `trigger.modifier.round` |
| `forge.modifier.value.lerp` | `trigger.modifier.lerp` |
| `forge.modifier.value.select_value` | `trigger.modifier.select_value` |
| `forge.modifier.value.random_range` | `trigger.modifier.random_range` |
| `forge.modifier.value.vector_compose` | `trigger.modifier.vector_compose` |
| `forge.modifier.value.vector_add` | `trigger.modifier.vector_add` |
| `forge.modifier.value.vector_scale` | `trigger.modifier.vector_scale` |
| `forge.condition.predicate.compare` | `trigger.condition.compare` |
| `forge.condition.predicate.range` | `trigger.condition.range` |
| `forge.condition.predicate.all` | `trigger.condition.all` |
| `forge.condition.predicate.any` | `trigger.condition.any` |
| `forge.condition.predicate.not` | `trigger.condition.not` |
| `forge.condition.predicate.chance` | `trigger.condition.chance` |

| 能力（query） | handler |
| --- | --- |
| `forge.condition.predicate.count` | `trigger.condition.count` |
| `forge.condition.predicate.entity_type` | `trigger.condition.entity_type` |
| `forge.condition.predicate.has_tag` | `trigger.condition.has_tag` |
| `forge.condition.predicate.exists` | `trigger.condition.exists` |
| `forge.selector.target.distinct` | `trigger.selector.distinct` |
| `forge.selector.target.limit` | `trigger.selector.limit` |
| `forge.selector.target.shuffle` | `trigger.selector.shuffle` |
| `forge.selector.target.random` | `trigger.selector.random` |
| `forge.selector.target.union` | `trigger.selector.union` |
| `forge.selector.target.intersection` | `trigger.selector.intersection` |
| `forge.selector.target.difference` | `trigger.selector.difference` |
| `forge.selector.target.self` | `trigger.selector.self` |
| `forge.selector.target.owner` | `trigger.selector.owner` |
| `forge.selector.target.source` | `trigger.selector.source` |
| `forge.selector.target.instigator` | `trigger.selector.instigator` |
| `forge.selector.target.event_target` | `trigger.selector.event_target` |

`add`、`multiply`、`minimum`、`maximum`、`all`、`any` 是目录声明的可变端口行：端口按 `input_count`（2…32）在计划里展开，注册时没有固定端口布局，所以它们不声明 shape，由框架按计划解析。观察行的 handler 不复制任何算法：七个集合选择器与 `count` 直接调用既有 `ReferenceCollections`，`entity_type`/`has_tag`/`exists` 通过求值器唯一的取数口 `context.Query` 读取（`TrySnapshot`/`TryPresence`）——`Targeting/ObservedEntityNodes` 是 kernel 侧助手，签名需要 `RuntimeKernel`，handler 拿不到它，其算法未被改动，仍由 R3 消费者测试直接验证。

**`exists` 与三态读取**：`forge.condition.predicate.exists` 读 `RuntimeQuerySession.TryPresence`（框架为这一语义新增的窄读取）：活着的引用为 `Current`（true），kernel 已证明失效或缺失（`stale-world`/`stale-entity`）为 `Absent`（false，且不记 refusal），观察无法完成是 `Unknown`——照常记 refusal，步骤以该错误码拒绝，不会拿到一个看起来像"已观察"的 false。该读取与 `TrySnapshot` 共用同一个每 tick 查询预算，只有这一个 handler 使用它。

**五个角色选择器**从求值上下文交付的事件角色取值（`EvaluationContext.Actors`，由框架的角色表从被派发的事件读出），本包不自己拼角色到端口的映射：`self` 是唯一不可缺席的角色，缺席按 `actor-missing` 拒绝该步；`owner`、`source`、`instigator`、`event_target` 缺席时输出 null。

目录里其余未注册行（`transform_point`、空间/筛选/控制等）不在本表范围。

**纯计算**（`Pure/ScalarNodes.cs`、`VectorNodes.cs`、`SeededNodes.cs`）覆盖现有 23 项语义：常量、四则与最值与幂、clamp/absolute/round/lerp/select_value、compare/range/all/any/not、chance/random_range、三个向量运算。所有数值输入必须有限；NaN、正负无穷、除零、溢出、反向区间、非法权重、非法种子和未知枚举分别拒绝，不以 0 或默认值兜底。

舍入与网站 `Math.round` 对齐：中点朝正无穷，所以 -1.5 → -1、2.5 → 3，**不用 .NET 默认的 ties-to-even**；实现不使用 `Floor(value + 0.5)`，避免大整数加 0.5 时被错误递增。输出的负零按 JSON 边界规范为正零。种子随机使用网站同一次 Mulberry32 采样并显式保留 modulo-2^32 位运算，相同种子与参数重复调用返回相同结果，其他调用不推进隐式随机状态。**种子派生（工程、epoch、实例、事件、用途）由未来的公共上下文提供，本包不自造 seed context 或全局种子。**

**集合**（`Pure/ReferenceCollections.cs`）覆盖 distinct、union、intersection、difference、limit、shuffle、random、count 八项；这八项已注册为 7 个 `forge.selector.target.*` 选择器行与 `forge.condition.predicate.count` 条件行，handler 直接调用这里的助手，集合运算保持有序与去重语义。按 id + worldEpoch + lifeEpoch 的完整身份去重，不按裸 ID 合并；输入在过滤和截取前完整校验。distinct 与集合运算按 JSON 身份字符串序稳定排序，limit 保留首次输入顺序。`ReferenceSelection` 分别记录 InputCount、AvailableCount、RequestedCount、SelectedCount 和 UnfilledCount——请求 5 个而只有 1 个候选时明确显示缺额 4，不假称足额完成。输入与完整输出上限 4096，显式 limit/random 的选择量 1…256；输入超限在去重前拒绝，合并输出超限不静默截断。2.0.0 的 count 用比较运算符与整数 value，选择器的 `empty` 策略由 `ApplyEmptyPolicy` 执行：emit-empty 原样、fail 拒绝；skip 只是调度提示，本包没有图执行器来跳过下游。

**目标与关系筛选**（`Targeting/ObservedRecipientFilter.cs`、`RecipientFilterPolicy.cs`）实现既有 `forge.selector.target.filter` 的 RecipientPolicy v1。筛选次序与网站 `targeting.ts` 一致：种类 → 关系 → 生命状态 → 必需标签 → 排除标签 → 接收能力。关系参考对象必须显式选择 self/source/owner/instigator/event-target，缺失即拒绝，不回退其他角色；未定义的关系保持 unknown，不当作"不匹配"。生命状态由作者显式选择，筛选本身不产生治疗或复活。接收能力由调用方传入实际动作所需的能力，缺失记 `receiver-unsupported`，不用效果名称猜测。排序支持 stable-id / nearest / farthest，平局按实体 ID 的 ordinal 顺序。匹配数超过 `maxTargets` 时**拒绝**，绝不静默返回前 N 个。

**空间**（`Targeting/ObservedSpatialNodes.cs`、`ObservedEntityNodes.cs`）实现 sphere/cylinder/capsule/box 点位置过滤、nearest/farthest 与有限 chain，消费 `RuntimeKernel.InspectEntities` / `RequireComplete`。中心显式，候选完整性显式。四种形状都是世界轴对齐的闭判定——位置正好落在边界上算命中，没有额外容差；capsule 是沿 Y 轴总长 `height` 的线段加两端 `radius` 半球，`height/2 ≤ radius` 时退化为球；box 的半尺寸是 radius / height/2 / radius，即网站 `extents` 读到的同一组值；`angle` 在网站的纯预览里不参与判定。与网站 2.0.0 的差距：shape_overlap 在网站上查询整个世界样本，C# 只能过滤调用方给出的完整候选；nearest/farthest 的 anchor 实体需先由调用方观察出位置。`Exists` 在完整观察时返回 true，SDK 明确报告旧 world/life 或对象缺失时返回 false，观察器缺失、未知命名空间、回调失败或查询预算问题抛明确错误——**不把 unknown 吞成 false**。不提供世界枚举、LOS、碰撞或导航证据。

**可变端口与权重抽样**（`Pure/VariadicNodes.cs`、`WeightedSampling.cs`）实现网站目录行当前版本（2.0.0）的 2–32 输入 AND/OR、加法与乘法、最小与最大值；集合并交集在 2.0.0 是二元合同，由 `ReferenceCollections` 实现。顺序遵循作者端口顺序，不按端口名排序也不重排浮点求和。权重抽样显式区分有放回与无放回，处理零权重、候选不足、重复身份拒绝、确定种子和有限预算；算法版本 `binary64-integer-mass-mulberry32-v1` 把有限非负 binary64 权重化为精确整数质量、用最大公约数约简，再用整数拒绝抽样，避免归一化浮点求和的溢出与微小正权重丢失。输入上限 4096、抽取上限 256、随机字上限 65536，每次抽取最多 64 轮拒绝尝试；预算耗尽不返回半份成功。

`forge.selector.target.weighted` 仍是规划目录项。新算法尚未成为网站的正式参数与端口合同，也没有注册游戏 binding。

## 边界

这些方法只处理传入的引用，不能证明目标当前有效、查询完整、存在接收能力或获得权限。本包内没有第二个世界查询表、时钟或 Registry；所有实体状态通过公共 `InspectEntities` 获取，完整性只覆盖所请求的引用。任何筛选结果都是观察证据，不是权限或成本凭证——实际动作提交前必须由对应领域重新核对身份、关系、接收能力、权限与费用。

本包已有自己的插件入口与发行身份（`manifest.json`、`Release/release.json`、`icon.png`、`CHANGELOG.md`），但**没有打包、没有安装、没有游戏内验证**。R4 图执行、T3–T7 与原生动作、GTFO 主客机与恢复仍未完成。

## 复跑

从仓库根目录执行完整验收：

```powershell
python ForgeTrigger/tools/validate-trigger.py --mutations
```

最近一次（对齐网站 2.0.0 目录行后）退出 0，详情见 [VALIDATION.md](VALIDATION.md)。明确只验证 R3 消费者时用 `--r3-only --mutations`，结果会标记 `requestedScope=r3-only`；**专项通过不代表完整入口通过**，也不能作为发布或整体完成的判定。构建与测试输出都位于 `ForgeTrigger/artifacts`，不写入游戏安装目录。

单步入口（`<输出目录>` 用 `ForgeTrigger/artifacts/<本次名称>` 或系统临时目录下的新目录，不需要预先存在）：

```powershell
python ForgeTrigger/tools/validate-pure.py --out <输出目录>
python ForgeTrigger/tools/validate-t1.py --site <网站仓> --out <输出目录>
node ForgeTrigger/tools/spatial-vectors.mjs <网站仓> <输出目录>
node ForgeTrigger/tools/recipient-filter-vectors.mjs <网站仓> <输出目录>
node ForgeTrigger/tools/collection-vectors.mjs <网站仓> <输出目录>
```

`validate-t1.py` 与 `tests/Contracts` 都需要网站仓目录：目录里的 `catalog/capability-catalog.json` 就是本包逐字段比对的合同，缺参数或目录缺失都直接失败，不跳过。`validate-pure.py --out` 只接受 `ForgeTrigger/artifacts` 内或系统临时目录下的新目录，默认仍是 `ForgeTrigger/artifacts/pure-<时间戳>`；向量脚本把结果 JSON 写在传入的输出目录里，不要指向仓库内已有文件的目录。
