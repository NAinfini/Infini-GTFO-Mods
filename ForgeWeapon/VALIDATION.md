# ForgeWeapon 验证记录

**上次更新：2026-09-13**（内容合并自原 W1-IDENTITY-HANDOFF、W1-IDENTITY-ACCEPTANCE-HANDOFF、W1-IDENTITY-REVIEW-HANDOFF、W1-NATIVE-API-AUDIT 四份交接记录与旧 VALIDATION）。

## 当前结论

W1 的元数据核验与装备身份托管实现已交付，统一入口 `tools/verify-w1.py --architecture` 退出码 0，所检查的源码前后哈希一致。

**W1 未关闭，W2–W8 未开始。** 原生 Hook、生成与 owner 生命周期映射、共享 R3/R5 合同的消费、资源与动画、游戏验证都待完成。生产 `ModuleDefinition` 保持原字节，0 新游戏 binding。没有启动 GTFO、没有主客机、没有安装或发布。

## 最后一次统一复验

| 套件 | 结果 | 说明 |
| --- | --- | --- |
| Weapon / SDK / Identity 构建 | 0 警告 0 错误 | |
| 原身份套件 | 42 场景、99 断言 | 由并发的实现任务编写 |
| 独立验收套件 | 37/37 | 由另一并发任务编写，本任务只复读源码并独立运行 |
| 公开队列与命令联调 | 20/20 | 消费实际编译的 `EquipmentIdentitySession` 与唯一 SDK |
| 元数据核验 | 312/312 | metadata-only |
| 核验工具正反测试 | 24/24 | 变异的是合同副本，不是游戏文件 |
| 跨模块架构断言 | 通过 | 当时记录 35 项；**现行 Program.cs 是 36 项**，见 [ARCHITECTURE.md](../ARCHITECTURE.md#2-架构断言数是-36) |
| 故意错误实现 | 6/6 被检出 | 合并前是四类与五类两套，现已按检错范围合并；所有错误版本先编译成功 |

**这些数量描述不同层的检查，不能相加作为已实现的玩法数。** 24 个工具测试里的错误输入拒绝是测试预期，不是 24 个游戏场景；312 是所选文件、类型与成员的核验断言，不是完整的原生语义证明；6 个变异防护不是六项原生玩法。

变异 runner 检查四处关键防护：票据版本、退役生命、owner 探测、世界通知；合并后覆盖六类，分别由原身份套件和独立套件检出。

独立验收套件覆盖共享 provider 注册、重复观察、实例与资源身份、A→B→A 所有权、owner 生命变化、wield 与 loading 状态、原子的 slot 与 definition 拒绝、live-predicate 失败、epoch 与权威与停止与释放、外来票据、回调重入、退役生命的重放保护、有界的活动与历史容量。它**不**实现或测试实际的弹药事务、生成与部署副作用、攻击执行、多人流量或检查点。

公开队列联调的 20 个用例覆盖：排队后装备销毁、原生与 owner 失效、同 key 新 life、转交 A→B→A、卸下、owner 新生命、世界切换、resolver 注销、客户端拒绝、取消、权限、提交后清理。

## 尚未执行的规格

`tests/fixtures/w1-runtime-acceptance.json` 里写好了 20 个合成规格，覆盖实例与槽位与转交与 epoch、最后一发竞争、miss 仍扣费、重复回调、预留过期、换弹容量与来源限制、放置失败、提交不明、回收、周期能源。**`verification=not-executed`，0 个执行。** 它们不是新的 Runtime IR 也不是已实现的库存服务，不计为通过的玩法测试；托管子问题的测试也不能计作 native、成本或部署断言已通过。

工具收据里 `gameExecuted=false`、`installed=false`。

## 原生元数据锁

Steam app `493520` / build `20403457` / revision `34873`。GameAssembly SHA-256 `C6A5C3CD8CA5FE2A8C1A71A3D107663E8CBF01404820DBBDA2EA10C4BFD7BF55`，global-metadata SHA-256 `F57AE2790F7AE0A7ACCAD42BD5F5A541EE74C85E96B4D4B64A346B465C23C882`。精确锁覆盖 8 个文件、34 个类型、164 个方法、47 个属性、22 个字段。

证据等级 **metadata-only**：只读 BepInEx interop 元数据并锁定同机游戏与生成的元数据文件，没有执行原生方法、核验完整调用链、安装 Hook 或启动 GTFO。同机文件存在不单独证明 interop 的生成来源与原生地址映射正确。

## 保留的失败记录

两次完整依赖复验曾失败于并发的 Runtime 输入尚未写完：`Framework/RuntimeKernel.Lifecycle.cs(27,87)` 与 `Framework/RuntimeKernel.cs(198,6)` 各报一次 `CS1513: } expected`。没有替 Runtime 补括号、回滚、覆盖或放宽构建门槛；后续复读发现 Lifecycle 文件已从 27 行增长为完整的 135 行，但第二次构建又读到另一个共享文件的中间状态。**不能据此认定 Runtime 最终实现有固定缺陷，也不能用较早的架构通过结果覆盖这两次失败。**

一次较早的跨模块复验曾在 `ForgeEnemy/EnemyLifeTable.cs(97,85)` 报 `CS1503`（CultureInfo 与 NumberStyles 参数不匹配）。没有修改 Enemy；最终实际重跑通过，不用旧结果覆盖失败，也不把其修复归到 Weapon。

一份并行的"仅物理会话"草稿已从生产移除，保留在被忽略的 `.artifacts` 下；生产方向只有 `EquipmentIdentitySession` 与索引这一条。一次原生静态代码检查在检查前被工具安全检查拦截，其未验证的草稿被排除在 NativeAudit 工程之外，先前的元数据审计器已恢复——**本包没有做出任何原生代码层面的断言**。

## 复跑

从仓库根目录：

```powershell
python ForgeWeapon/tools/verify-w1.py --bepinex <existing-BepInEx> --game <existing-GTFO> --architecture
python ForgeWeapon/tools/verify-identity-acceptance.py
python ForgeWeapon/tools/verify-identity-dispatch.py
python ForgeWeapon/tools/test-identity-mutations.py
python -m unittest ForgeWeapon/tests/test_native_audit.py
```

默认的 `verify-w1` 已包含 Identity 的构建与测试；`metadata-only` 模式不含它们。变异脚本在新的隔离目录里先构建未修改的副本，再在各自的 `.artifacts` 副本里故意破坏检查——**编译错误不计作变异检出成功**，每个变体必须让它对应的具名回归测试失败。

每次运行都用新的输出目录。`receipt.json` 保留源码哈希与命令退出码，`tests.json` 保留逐项结果。运行期间输入发生变化会使该次的稳定性声明失效；已有证据不被覆盖。这些命令不改动生产 C#、用户游戏文件、profile 设置或 Git 状态。

独立验收套件的详细覆盖与限制见 [tests/IdentityAcceptance/README.md](tests/IdentityAcceptance/README.md)。
