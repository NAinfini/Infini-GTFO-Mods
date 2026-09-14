# ForgeTrigger 验证记录

**上次更新：2026-09-13**（由原 `VALIDATION.md` 与 `VALIDATION-CURRENT.md` 合并而成，另并入 T1-CONTRACT-MAP、T1-T7-STATUS 与四份 T2 交接记录的实际结果）。

## 当前结论：完整入口通过（托管与跨语言证据）

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
| 空间跨端样例 | 117 组 360 项 | C# 消费 sphere/cylinder、nearest/farthest（anchor）、chain；capsule/box 2 组列为 C# 未实现，不计通过 |
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

**生命周期订阅注销保护也已合入。** 曾经 417 项中剩余 2 项失败（`entity observer disposed lifecycle subscription`、`subscription remains intact`），根因是 `RemoveLifecycleObserver` 允许实体观察回调注销生命周期订阅。现在只在实体观察期间禁止该入口，普通清理、停止后清理与生命周期回调自注销都保留。当时的隔离补丁提案已被合入的实现取代，已从 `handoff/` 删除。

**independent 缺失的 mutation 覆盖已补上。** 早先 runner 半成品曾放在 `handoff/validate-independent.incomplete.txt`，完整入口因此记为 blocked；现在由 `validate-independent.py` 实际执行，半成品已删除。

**SDK 不支持 variadic 也已解除。** `RuntimeRegistry` 曾把 `graph.variadic` 当未知字段拒绝，导致 T1 的 C# 注册在 `RuntimeJson.Shape → RuntimeRegistry.Validate` 处失败。Runtime 的 R4a 能校验该元数据并按精确 revision 解析端口；U-RUNTIME 的 plan v2 加载器会按注册合同展开 variadic/portGroups，但 T1 的作者定义没有运行绑定，**元数据可登记不等于图已可执行**。

历史失败日志全部保留在各自的 `artifacts/` 目录里，不用后来的绿色结果覆盖它们。

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

以上全部是实现级与合成数据的证据。**没有执行 GTFO、没有原生 API、没有多人、没有安装、没有发布。** 生产 `ModuleDefinition` 保持空 provider，独立测试输出明确 `gameVerified=false`、`publicationReady=false`。测试代码与 artifacts 已从生产编译项排除；没有另起 Registry、图执行器、世界时钟或查询服务。
