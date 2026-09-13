# ForgeWeapon

武器、工具、消耗品共用一个装备领域与 Workshop：装备实例、输入与攻击、弹药与能源与库存、部署与回收、装备表现。时间、状态、目标与事务的基础由 Runtime 提供，**不为每种工具或药剂复制一套框架**。

仓库整体状态见 [ARCHITECTURE.md](../ARCHITECTURE.md)，未完成批次见 [IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md)，验证结果见 [VALIDATION.md](VALIDATION.md)。

## 工坊里的"标准武器"本身就是一张图

按总案第 2 节，内置预设与玩家自制的行为共享同一条编译、校验、执行路径。治疗炮台、命中标记、ping 不是三个系统，是同一批原子的三种拼法——治疗炮台 = 部署动作 + 范围查询 + 关系过滤 + 周期 Control + 治疗 Action，外观在工坊里配。**任何为内置预设开的特权通道都是设计缺陷。**

这条对本包的含义是：Weapon 提供的是真实的装备实例、成本、攻击与部署这些**领域能力**，不是成品功能开关。玩家怎么把它们拼起来由 Trigger 的图决定。

## 当前代码状态

**`ModuleDefinition.Create()` 仍是空 provider**，0 新 capability、0 binding、0 handler。没有自动加载的 BepInEx 插件，没有射击、伤害、换弹或部署能力。

已经在生产程序集内的是 W1 的装备身份托管实现，只有显式创建 Session 才生效：

`EquipmentObservation.cs` 把 `EntityReference`、资源 ID 与 revision、显式 owner、slot、Inventory/World/Deployed 位置、加载与持有状态分开，没有另造跨域的 EntityReference。`EquipmentIdentityIndex.cs` 维护有界的活动实例与槽位索引及已观察的生命历史；同槽占用冲突原子拒绝，资源版本不能在同一生命内偷换，移除必须匹配完整引用，旧的 despawn 不会删除复用键上的新生命。`EquipmentIdentitySession.cs` 显式注册同一个 `forge.module.gtfo.weapon` provider 的 `gtfo.equipment` resolver，并通过 `ObserveLifecycle` 清理世界切换、失败、停止和失去主机权限之后的记录。

`EquipmentUseTicket` 是**仅进程内**的前置条件快照：任何已观察到的 owner、slot、location、readiness 或 wield 变化都让旧票据失效，A→B→A 和卸下再装备都不复活旧票据。**它不是权限、不是预留、不是成本收据、不是网络身份、不是检查点数据**，不能序列化成共享的所有权合同。

原生与 owner 的探测由后续经核验的绑定提供。当前必须在 Runtime 的注册窗口内显式创建 Session 并传入两个真正核验当前原生实例和玩家生命的探测函数；写入与解析要求 Runtime Ready 且已完成首个 host tick，未初始化、客户端、未知权限与注销状态都不接收记录。示例测试里的永真探测**只能用于合成输入，绝不能作为游戏默认实现**。

索引不生成世界、生命或资源身份，也不从 slot、模型、资源名、owner 或裸指针推断另一个身份。新实例必须由原生接线提供经核验的新引用；同一 ID 必须在精确退役之后以更大的 lifeEpoch 再出现。历史到预算上限时明确拒绝，**不淘汰旧记录后放行重放**。探测期间重入 Record / Remove / Dispose 会被拒绝；探测中发生世界切换、停止或注销会重新检查，迟到的观察不能写进新世界。

## 原生 API 证据

W1 已交付只读的元数据核验工具与精确的输入锁：Steam app `493520` / build `20403457` / revision `34873`，GameAssembly SHA-256 `C6A5C3CD8CA5FE2A8C1A71A3D107663E8CBF01404820DBBDA2EA10C4BFD7BF55`，global-metadata SHA-256 `F57AE2790F7AE0A7ACCAD42BD5F5A541EE74C85E96B4D4B64A346B465C23C882`，锁定 8 个文件、34 个类型、164 个方法、47 个属性、22 个字段。

初次名称检索得到 345 个候选类型，之后补读 8 个身份与库存与网络类型；**这不是可运行的能力计数**，最终只按人工选择的精确全名、程序集、基类和签名核验。

证据等级是 **metadata-only**：读取 BepInEx interop 的元数据并锁定同机游戏与生成的元数据文件，**没有执行原生方法、没有核验完整的原生调用链、没有安装 Hook、没有启动 GTFO**。同机文件存在，不单独证明 interop 的生成来源与原生地址映射正确。工具本身不进入 ForgeWeapon 的生产 DLL。

## 边界

网站可以挂载多个部件或预览模型，不证明 GTFO 支持相同数量的活动组件。Enemy 与 Map 分别拥有敌人和玩家与世界的 receiver；Weapon 不能为了省接口直接写它们的私有字段。一个原生攻击或附加效果必须指定唯一的执行 binding——**观察到原生承伤不代表还要再施加一次同额伤害**。

`tests/fixtures/w1-runtime-acceptance.json` 里的 20 个完整接线规格已经写好但 **0 个执行**，`verification=not-executed`；它们不是新的 Runtime IR 也不是已实现的库存服务，不计为通过的玩法测试。

W1 的原生 Hook、生成与 owner 生命周期映射、共享 R3/R5 合同的消费、资源与动画和游戏验证都待完成；W2–W8 未开始。当前 DLL 不是玩家发行物。

## 复跑

从仓库根目录：

```powershell
python ForgeWeapon/tools/verify-w1.py --bepinex <existing-BepInEx> --game <existing-GTFO> --architecture
python ForgeWeapon/tools/verify-identity-acceptance.py
python ForgeWeapon/tools/verify-identity-dispatch.py
python ForgeWeapon/tools/test-identity-mutations.py
```

第一条是统一入口（`--architecture` 附带跨模块架构断言），后三条是并发任务编写的独立套件与变异检查。每次运行都用新的输出目录，`receipt.json` 保留源码哈希与命令退出码。实际结果见 [VALIDATION.md](VALIDATION.md)，独立验收套件的覆盖与限制见 [tests/IdentityAcceptance/README.md](tests/IdentityAcceptance/README.md)。
