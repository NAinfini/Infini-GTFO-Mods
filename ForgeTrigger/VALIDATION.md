# ForgeTrigger 验证记录

**上次更新：2026-09-13**（由原 `VALIDATION.md` 与 `VALIDATION-CURRENT.md` 合并而成，另并入 T1-CONTRACT-MAP、T1-T7-STATUS 与四份 T2 交接记录的实际结果）。

## 当前结论：完整入口受阻于缺失的新 mutation 覆盖

```powershell
python ForgeTrigger/tools/validate-trigger.py --mutations
```

**2026-09-13 U-RUNTIME 迁移后执行**（`artifacts/trigger-20260913-094711`），进程退出 0，summary `status=blocked`、`checksStatus=passed`：pure、r3（1561 项；14 种错误实现，检出 14 种）、t1 均通过；independent 的基线 2625 项通过，但可变端口与权重抽样的新错误实现覆盖不可用，所以记为 blocked。此前"t1 因共享 heal 作者元数据标记失败"在本次不再复现（作者元数据 `metadataReady=true`）。

**blocked 不是通过**，不能作为发布或整体完成的判定；缺口见下面"错误实现检出"。

## 各套件的最后记录

计数存在大量重叠，不同层的断言不相加成独立功能数或游戏测试数。

| 套件 | 最后记录 | 口径 |
| --- | --- | --- |
| 纯计算与集合（C#） | 1613 项断言 | 23 项纯计算 + 8 项集合；含正例重复验证与边界断言 |
| 纯计算跨端样例 | TypeScript 1264、C# 878、270 组共享输入 | 245 正例 + 25 预期拒绝 |
| 集合跨端样例 | 255 组 | 覆盖八项定义 |
| R3 角色 / 空间 / 筛选 | 1529 项 | 含既有 417 项与新增筛选断言 |
| 筛选跨端样例 | 164 组 628 项 | 151 值正例 + 13 预期拒绝 |
| 空间跨端样例 | 78 组 237 项 | 已由 C# SpatialTests 实际消费，不再只是 TypeScript 预览 |
| T1 跨语言 | TypeScript 125、C# 75 | 27 个共享计划：3 接受、24 按预期理由在两种语言里拒绝 |
| T1 目录审计 | 424 基础节点跨 8 类；56 个 typed 作者定义 | 190 个 Action 留在领域侧；56 个 ID 都在目录中且 0 个运行绑定 |
| Acceptance（可变端口与权重） | 2082 项断言 | 77 组可变端口样例、300 组权重样例、780 项 BigInt 参考断言 |
| 作者元数据审计 | 52 个可登记元数据、9 个 variadic 精确拒绝、1 个共享 canonical 逐字段对照 | `runtimeReady=false`；52 个可登记元数据不等于 52 个可执行节点 |

只有 `Pow` 使用 1e-14 相对误差，其余共享数值、向量与布尔输出精确匹配。**这不是任意平台任意输入的位级一致保证。**

## 错误实现检出

每一批都先验证原样隔离副本通过，再验证故意写错的副本能被具体断言检出。**编译失败不计作检错成功**，全部错误版本都先构建成功。

纯计算与集合的 10 种错误实现全部被检出，其中最初四种分别产生：ties-to-even 舍入 4 项失败、减法变加法 6 项失败、忽略显式种子 23 项失败、除零返回零 2 项失败。R3、空间与筛选的 14 种错误实现全部被检出：7 种覆盖原有的角色与空间边界（owner 回退、忽略 receiver、反向关系、unknown 变 false、球边界排除、连锁重复访问、平局排序不稳定），7 种覆盖新的筛选边界。

新增的可变端口与权重抽样的六项错误实现**没有验证过**。当时 runner 的写入被工具安全检查拦截，半成品移到了 `handoff/validate-independent.incomplete.txt`；现在 `tools/validate-independent.py` 已存在，需要重跑确认。原十种 mutation 通过不能算作新的 weighted 与 variadic 错误实现已验证。

## 已经解决的历史阻塞

**R3 观察器登记缺口已解除。** 曾经的阻塞是 `RuntimeRegistry.WithModule` 只登记 EntityResolvers 而不读取 `module.EntityObservers`，`RuntimeKernel.InspectEntity` 因此返回 `entity-observer-unavailable`，整个查询变成 `entity-query-incomplete`；`Unregister` 也只清理 resolver。这些已由 Runtime 的 R3a 在共享 SDK 中实际接通，见 [Runtime 验证记录](../ForgeRuntime/VALIDATION.md)。

**生命周期订阅注销保护也已合入。** 曾经 417 项中剩余 2 项失败（`entity observer disposed lifecycle subscription`、`subscription remains intact`），根因是 `RemoveLifecycleObserver` 允许实体观察回调注销生命周期订阅。现在只在实体观察期间禁止该入口，普通清理、停止后清理与生命周期回调自注销都保留。`handoff/` 下的隔离补丁提案（`r3-observer-candidate.*`、`r3-observer-followup.*`）已经无效，不要再应用。

**SDK 不支持 variadic 也已解除。** `RuntimeRegistry` 曾把 `graph.variadic` 当未知字段拒绝，导致 T1 的 C# 注册在 `RuntimeJson.Shape → RuntimeRegistry.Validate` 处失败。Runtime 的 R4a 能校验该元数据并按精确 revision 解析端口；U-RUNTIME 的 plan v2 加载器会按注册合同展开 variadic/portGroups，但 T1 的作者定义没有运行绑定，**元数据可登记不等于图已可执行**。

历史失败日志全部保留在各自的 `artifacts/` 目录里，不用后来的绿色结果覆盖它们。

## 复跑

完整入口（会失败，这是当前真实状态）：

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

以上全部是实现级与合成数据的证据。**没有执行 GTFO、没有原生 API、没有多人、没有安装、没有发布。** 生产 `ModuleDefinition` 保持空 provider，独立测试输出明确 `gameVerified=false`、`publicationReady=false`。测试代码与 artifacts 已从生产编译项排除；没有另起 Registry、图执行器、世界时钟或查询服务。
