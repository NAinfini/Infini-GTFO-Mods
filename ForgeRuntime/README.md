# Infini Forge Runtime 1.2.0（开发中，未发布）

Runtime 是唯一的公共服务与 GTFO 宿主：类型、注册、权限、生命周期、模拟调度、状态、事务结果，**以及多人环境下的网络可靠性与社区修复**。它不按领域包名分支，也不强制加载全部领域包或 Development。

仓库整体状态与全部测试数字见 [ARCHITECTURE.md](../ARCHITECTURE.md)。未完成批次见 [IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md)，当前验证结果见 [VALIDATION.md](VALIDATION.md)。公共 SDK 的 API 细节见 [Framework/README.md](Framework/README.md) 与 [Framework/HOST-LIFECYCLE.md](Framework/HOST-LIFECYCLE.md)。

**1.2.0 尚未发布、安装或完成任何游戏验收。** 下面描述的全部能力都是 implementation-only。

## 当前能力

### 公共 SDK

`Framework/` 是不依赖 Unity 或 GTFO 类型的注册、严格计划验证、世界与实体生命周期、单一有界队列和执行结果合同，编译为独立的 `ForgeRuntime.Framework.dll`。第一方游戏模块与未来扩展都走同一个 `Plugin.Runtime.RegisterModule`。只允许模块用自己的返回句柄发布或取消——这是受信模块之间的所有权约束，不是任意第三方 DLL 的安全沙箱。

`Framework/CombatContracts.cs` 注册 5 个通用 combat canonical 定义（承伤事实、治疗动作、生命变化事实、死亡流程、肢体破坏），0 个 binding。Enemy 模块只注册自己的实际 binding 和 `gtfo.enemy:<GlobalID>` 接收器；来源、目标与阵营独立。

计划执行目前只支持 host 的单 Trigger → 线性 Action，固定参数、直接事件输入、失败停止当前入口。未知控制流、动态结果依赖、recipient-policy 与未实现的数据引用都明确拒绝。完整 Graph IR、查询服务、交易预留与多人大状态恢复属于后续工作。

### 宿主启动与配置

宿主配置由 `RuntimeSettings.cs` 拥有：`Runtime.Mode`、`Framework.PlanPath`、`Framework.AllowedPermissions`。原键名、命名与数字模式值和现有默认值都保留。模式按原始文本显式解析——真实的 `ConfigFile` 枚举绑定器曾被观察到对非法文本静默选中 `Authoring` 或组合枚举值，因此非法或空模式现在在原生初始化之前就失败，不会拿到一个"启用"的默认值。

`Off` 只绑定宿主键，不启动任何组件或 Hook。`Play` 与 `Authoring` 启动同一个宿主：4 个世界/会话/检查点 Hook（`harmony.PatchAll` 只扫描宿主程序集）与 `FrameworkMonitor`。**宿主不含任何诊断、报告或性能采集**；两种模式唯一的区别是可选的 [ForgeDevelopment](../ForgeDevelopment/README.md) 插件只在 `Authoring` 下启动。模式、计划路径与权限的变更需要重启进程，不支持热卸载。

`Plugin.ConfiguredMode` 是冻结的启动选择，公开在宿主程序集里，**不是权限、不是就绪状态、不是主机权威**。`Plugin.Runtime` 只在 Load 成功后提供；失败或 Off 状态下为 null，同一实例不重试 Load。依赖 Runtime 的插件在自己的 Load 中、Runtime 首个 FixedUpdate 之前注册。

启动失败时先关闭玩法入口再清理：宿主停止 → unpatch → 销毁 FrameworkMonitor。每个已获取的阶段都会被尝试清理，即使其中一步或错误上报失败也继续执行后面的步骤，最后重新抛出原始启动异常。清理与上报的失败保留在 `Data["ForgeRuntime.StartupCleanupFailures"]` 的 AggregateException 里（字典不可写时不能替换原始异常）。这是对已获取阶段的尽力清理，不是任意原生副作用的回滚保证。

`[Framework] PlanPath` 和 `AllowedPermissions` 默认均为空，因此不会自动启用任何行为。开发者显式选择 BepInEx 内的相对路径离线计划（最大 4 MiB）和权限；首个固定更新在其他插件注册完成后验证加载，版本、模块、路径或权限不符即失败，本次进程不反复读取重试。实际注册清单输出到 `BepInEx/ForgeRuntime/capabilities.json`；游戏不联网获取最新图。

### 诊断不在宿主内

生成、空间、性能、异常聚合与报告已在 D2 全部迁到 [ForgeDevelopment](../ForgeDevelopment/README.md)，宿主程序集不引用 Development。制作端范围与对照实验方法的历史说明见 [ForgeDevelopment/AUTHORING-SCOPE.md](../ForgeDevelopment/AUTHORING-SCOPE.md)。

### 尚未有的能力

没有网络层——去重、权威端判定、迟加入、恢复、主机迁移、带宽预算和能力握手全部未实现，这是 U-NET 的范围。没有持续效果的属性回写；numeric lease 只是 SDK 内部的重算贡献记录，不代表游戏属性已改变。`BeginWorld` 不是检查点或网络恢复，恢复或迁移会暂停绑定，不能通过再次进入 Generating 绕过。没有 Control/Selector 嵌套图的完整 VM。

## 安装与配置

需要 BepInExPack GTFO 3.2.2。历史 1.1.x 发行物是单个 `ForgeRuntime.dll`；当前开发版拆出了 SDK，未来安装产物必须把 `ForgeRuntime.dll` 与 `ForgeRuntime.Framework.dll` 一起放入 Runtime 插件目录。**当前宿主编译通过不代表已完成原生或联机验收，本轮未执行安装。**

配置文件是 `BepInEx/config/NAinfini.ForgeRuntime.cfg`，只含宿主键：

```ini
[Runtime]
Mode = Authoring

[Framework]
PlanPath =
AllowedPermissions =
```

1.1.x 与 D2 之前写在本文件里的 `[Authoring]`、`[Performance Diagnostics]` 键不再被读取，也不自动迁移；诊断配置在 `NAinfini.ForgeDevelopment.cfg`，见 Development 的说明。

## 边界

已提供的是编译后宿主与 SDK 的元数据检查、本地游戏程序集的 Hook 签名检查，以及使用替身的启动与绑定测试。它们不代替实际注入、开销测量或主客机测试。

禁止无限重试、吞掉异常或调低游戏日志级别来伪装成功。
