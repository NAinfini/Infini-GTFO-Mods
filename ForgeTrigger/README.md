# ForgeTrigger

跨域逻辑节点，使用唯一的 Runtime SDK。

**Trigger 的交付优先级与 Runtime 同级。** Trigger 的节点词汇是覆盖全部领域的横切基座——Map、Weapon、Enemy 各自的机制都落在它提供的原子上，玩家拼出的每一张图都从这里取积木。本包牵头的机制只有 4 组，但牵头数不决定交付顺序。

计划与状态见两仓统一框架第 6 节 U-TRIGGER（链接见[仓库 README](../README.md)），带日期的验证记录与失败见 [VALIDATION.md](VALIDATION.md)。

## 当前能力

生产 `ModuleDefinition` 仍是**空 provider**，没有注册任何可执行的游戏 binding。下面全部是编译进 `ForgeTrigger.dll` 的底层方法，被自己的测试工程消费；方法存在不等于节点已注册。

**纯计算**（`Pure/ScalarNodes.cs`、`VectorNodes.cs`、`SeededNodes.cs`）覆盖现有 23 项语义：常量、四则与最值与幂、clamp/absolute/round/lerp/select_value、compare/range/all/any/not、chance/random_range、三个向量运算。所有数值输入必须有限；NaN、正负无穷、除零、溢出、反向区间、非法权重、非法种子和未知枚举分别拒绝，不以 0 或默认值兜底。

舍入与网站 `Math.round` 对齐：中点朝正无穷，所以 -1.5 → -1、2.5 → 3，**不用 .NET 默认的 ties-to-even**；实现不使用 `Floor(value + 0.5)`，避免大整数加 0.5 时被错误递增。输出的负零按 JSON 边界规范为正零。种子随机使用网站同一次 Mulberry32 采样并显式保留 modulo-2^32 位运算，相同种子与参数重复调用返回相同结果，其他调用不推进隐式随机状态。**种子派生（工程、epoch、实例、事件、用途）由未来的公共上下文提供，本包不自造 seed context 或全局种子。**

**集合**（`Pure/ReferenceCollections.cs`）覆盖 distinct、union、intersection、difference、limit、shuffle、random、count 八项。按 id + worldEpoch + lifeEpoch 的完整身份去重，不按裸 ID 合并；输入在过滤和截取前完整校验。distinct 与集合运算按 JSON 身份字符串序稳定排序，limit 保留首次输入顺序。`ReferenceSelection` 分别记录 InputCount、AvailableCount、RequestedCount、SelectedCount 和 UnfilledCount——请求 5 个而只有 1 个候选时明确显示缺额 4，不假称足额完成。输入与完整输出上限 4096，显式 limit/random 的选择量 1…256；输入超限在去重前拒绝，合并输出超限不静默截断。2.0.0 的 count 用比较运算符与整数 value，选择器的 `empty` 策略由 `ApplyEmptyPolicy` 执行：emit-empty 原样、fail 拒绝；skip 只是调度提示，本包没有图执行器来跳过下游。

**目标与关系筛选**（`Targeting/ObservedRecipientFilter.cs`、`RecipientFilterPolicy.cs`）实现既有 `forge.selector.target.filter` 的 RecipientPolicy v1。筛选次序与网站 `targeting.ts` 一致：种类 → 关系 → 生命状态 → 必需标签 → 排除标签 → 接收能力。关系参考对象必须显式选择 self/source/owner/instigator/event-target，缺失即拒绝，不回退其他角色；未定义的关系保持 unknown，不当作"不匹配"。生命状态由作者显式选择，筛选本身不产生治疗或复活。接收能力由调用方传入实际动作所需的能力，缺失记 `receiver-unsupported`，不用效果名称猜测。排序支持 stable-id / nearest / farthest，平局按实体 ID 的 ordinal 顺序。匹配数超过 `maxTargets` 时**拒绝**，绝不静默返回前 N 个。

**空间**（`Targeting/ObservedSpatialNodes.cs`、`ObservedEntityNodes.cs`）实现 sphere/cylinder 点位置过滤、nearest/farthest 与有限 chain，消费 `RuntimeKernel.InspectEntities` / `RequireComplete`。中心显式，候选完整性显式。与网站 2.0.0 的差距：shape_overlap 在网站上查询整个世界样本并支持 capsule/box，C# 只能过滤调用方给出的完整候选且只有 sphere/cylinder；nearest/farthest 的 anchor 实体需先由调用方观察出位置。`Exists` 在完整观察时返回 true，SDK 明确报告旧 world/life 或对象缺失时返回 false，观察器缺失、未知命名空间、回调失败或查询预算问题抛明确错误——**不把 unknown 吞成 false**。不提供世界枚举、LOS、碰撞或导航证据。

**可变端口与权重抽样**（`Pure/VariadicNodes.cs`、`WeightedSampling.cs`）实现网站目录行当前版本（2.0.0）的 2–32 输入 AND/OR、加法与乘法、最小与最大值；集合并交集在 2.0.0 是二元合同，由 `ReferenceCollections` 实现。顺序遵循作者端口顺序，不按端口名排序也不重排浮点求和。权重抽样显式区分有放回与无放回，处理零权重、候选不足、重复身份拒绝、确定种子和有限预算；算法版本 `binary64-integer-mass-mulberry32-v1` 把有限非负 binary64 权重化为精确整数质量、用最大公约数约简，再用整数拒绝抽样，避免归一化浮点求和的溢出与微小正权重丢失。输入上限 4096、抽取上限 256、随机字上限 65536，每次抽取最多 64 轮拒绝尝试；预算耗尽不返回半份成功。

`forge.selector.target.weighted` 仍是规划目录项。新算法尚未成为网站的正式参数与端口合同，也没有注册游戏 binding。

## 边界

这些方法只处理传入的引用，不能证明目标当前有效、查询完整、存在接收能力或获得权限。本包内没有第二个世界查询表、时钟或 Registry；所有实体状态通过公共 `InspectEntities` 获取，完整性只覆盖所请求的引用。任何筛选结果都是观察证据，不是权限或成本凭证——实际动作提交前必须由对应领域重新核对身份、关系、接收能力、权限与费用。

R4 图执行、T3–T7、原生动作与费用、GTFO 主客机与恢复、安装和发布都未完成。

## 复跑

从仓库根目录执行完整验收：

```powershell
python ForgeTrigger/tools/validate-trigger.py --mutations
```

最近一次（对齐网站 2.0.0 目录行后）退出 0，详情见 [VALIDATION.md](VALIDATION.md)。明确只验证 R3 消费者时用 `--r3-only --mutations`，结果会标记 `requestedScope=r3-only`；**专项通过不代表完整入口通过**，也不能作为发布或整体完成的判定。构建与测试输出都位于 `ForgeTrigger/artifacts`，不写入游戏安装目录。
