# Infini Forge Runtime 1.2.0（开发中，未发布）

Runtime 是唯一的公共服务与 GTFO 宿主：类型、注册、权限、生命周期、模拟调度、状态、事务结果，**以及多人环境下的网络可靠性与社区修复**。它不按领域包名分支，也不强制加载全部领域包或 Development。

计划与状态见两仓统一框架第 6 节 U-RUNTIME、U-NET（链接见[仓库 README](../README.md)），带日期的验证记录见 [VALIDATION.md](VALIDATION.md)。跨包依赖规则、provider 身份与所有权见下文“跨包结构与所有权”。公共 SDK 的 API 细节见 [Framework/README.md](Framework/README.md) 与 [Framework/HOST-LIFECYCLE.md](Framework/HOST-LIFECYCLE.md)。

**1.2.0 尚未发布、安装或完成任何游戏验收。** 下面描述的全部能力都是 implementation-only。

## 当前能力

### 公共 SDK

`Framework/` 是不依赖 Unity 或 GTFO 类型的注册、严格计划验证、世界与实体生命周期、单一有界队列和执行结果合同，编译为独立的 `ForgeRuntime.Framework.dll`。第一方游戏模块与未来扩展都走同一个 `Plugin.Runtime.RegisterModule`。只允许模块用自己的返回句柄发布或取消——这是受信模块之间的所有权约束，不是任意第三方 DLL 的安全沙箱。

`Framework/CombatContracts.cs` 注册 5 个通用 combat canonical 定义（承伤事实、治疗动作、生命变化事实、死亡流程、肢体破坏），0 个 binding。Enemy 模块只注册自己的实际 binding 和 `gtfo.enemy:<GlobalID>` 接收器；来源、目标与阵营独立。

计划执行目前只支持 host 的单 Trigger → 线性 Action，步骤输入是直接事件输入或字面量二选一（J-003），入口只在 rejected/failed/cancelled/expired 或 partial+unknown 时停止。未知控制流、动态结果依赖、recipient-policy 与未实现的数据引用都明确拒绝。完整 Graph IR、查询服务、交易预留与多人大状态恢复属于后续工作。

### 宿主启动与配置

宿主配置由 `RuntimeSettings.cs` 拥有：`Runtime.Mode`（默认 `Play`，D-011：Rundown 包不带基础包 cfg）、`Logging.Level`（默认 `error`）。原键名与命名、数字模式值都保留。模式按原始文本显式解析——真实的 `ConfigFile` 枚举绑定器曾被观察到对非法文本静默选中 `Authoring` 或组合枚举值，因此非法或空模式现在在原生初始化之前就失败，不会拿到一个"启用"的默认值。

`Off` 只绑定宿主键，不启动任何组件或 Hook，也不扫描计划。`Play` 与 `Authoring` 启动同一个宿主：4 个世界/会话/检查点 Hook（`harmony.PatchAll` 只扫描宿主程序集）与 `FrameworkMonitor`。**宿主不含任何诊断、报告或性能采集**；两种模式唯一的区别是可选的 [ForgeDevelopment](../ForgeDevelopment/README.md) 插件只在 `Authoring` 下启动。模式变更需要重启进程，不支持热卸载。

`Plugin.ConfiguredMode` 是冻结的启动选择，公开在宿主程序集里，**不是权限、不是就绪状态、不是主机权威**。`Plugin.Runtime` 只在 Load 成功后提供；失败或 Off 状态下为 null，同一实例不重试 Load。依赖 Runtime 的插件在自己的 Load 中、Runtime 首个 FixedUpdate 之前注册。

启动失败时先关闭玩法入口再清理：宿主停止 → unpatch → 销毁 FrameworkMonitor。每个已获取的阶段都会被尝试清理，即使其中一步或错误上报失败也继续执行后面的步骤，最后重新抛出原始启动异常。清理与上报的失败保留在 `Data["ForgeRuntime.StartupCleanupFailures"]` 的 AggregateException 里（字典不可写时不能替换原始异常）。这是对已获取阶段的尽力清理，不是任意原生副作用的回滚保证。

**计划发现（I-PACK D-009）不再走单一配置路径。** `Play`/`Authoring` 下，首个固定更新在其他插件注册完成后，按包目录扫描离线计划：只看 `BepInEx/plugins` 的一级子目录，每个子目录下若存在 `forge/plans`（不存在则静默跳过，不算错误），取其中直接子文件、按 ordinal 精确匹配 `.plan.json` 后缀的文件（不递归、不识别其他扩展名）；发现顺序按 `/` 分隔的 BepInEx 相对路径以 `StringComparer.Ordinal` 排序。单文件超过 4 MiB 按 `json-size` 单独拒绝，且不计入下面的合并预算；其余文件按合并上限 256 个/64 MiB 做尾部优先淘汰——只要剩余集合仍超个数或字节上限，就反复剔除排序最靠后的一个文件（`plan-budget`），不是遇到第一个超限文件就停止接受后面的文件。链接/联接与转义检查只在确认 `forge/plans` 存在后，沿 `<目录>→forge→plans` 链与文件本身进行，命中按 `plan-path` 拒绝；IO 读取失败或非法 UTF-8 按 `invalid-json` 拒绝。每个文件独立产生一条 `plan.loaded`/`plan.rejected`（带 `path`，前者还带 `permissions`）；同一 planId 出现在多个文件中全部按 `plan-conflict` 拒绝，消息带上冲突组内全部相对路径；解析后的计划总数上限仍是 128（`plan-budget`）。本次进程只扫描一次，不支持热重载。实际注册清单输出到 `BepInEx/ForgeRuntime/capabilities.json`；游戏不联网获取最新图。

### 执行日志（D-007 阶段 B）

**记录点尚未接入。** 内核与 Enemy、Map、Weapon、Trigger 都还没有调用日志接口，所以玩家层现在不会写出任何业务记录；目前可用的只有 Runtime 自身的级别、sink 和提级接口，SDK 合同见 [Framework README](Framework/README.md#执行日志-sink-与级别d-007-阶段-b)。领域包的级别条目在阶段 A 随必填参数 `RegisterModule(RuntimeModule, RuntimeLogLevel)` 加入；在那之前查领域 provider 的级别会以 `log-provider-unregistered` 拒绝，不给默认值。

`[Logging] Level` 接受 `off`、`error`、`info`（大小写与首尾空白不敏感），默认 `error`，按原始文本解析，其他值（包括 `trace`）在原生初始化之前失败。改动需要重启。`Runtime.Mode = Off` 时宿主不初始化，也就没有 writer。

宿主的 `Logging/RuntimeLogWriter.cs` 实现 sink，写 `forge.log.v1` JSONL：

- 惰性启动：第一条被接受的记录才创建后台线程、目录和文件。没有记录就没有线程、目录、文件和控制台输出。
- 文件是 `BepInEx/forge-logs/<yyyyMMddTHHmmssZ>-<4 位十六进制随机>.jsonl`，UTF-8 无 BOM，`\n` 换行，`CreateNew` 不覆盖。新文件建好后按修改时间只保留最新 10 个，只删该目录顶层的 `*.jsonl`。
- 第一行是 `log.level`（level 为 info，带 `levels[{provider, level}]` 与 `elevated`），与第一条真实记录一起写出。提级后级别表变化，下一条记录前再写一行 `log.level`。
- 限流：普通档每 tick 256 行、队列 8192；提级档每 tick 4096 行、队列 65536。超限的记录丢弃并计数，下一条被接受的记录前或停止时写 `log.dropped`（带 `count`，level 为 error）。`log.level` 与 `log.dropped` 不受限流，因此队列最多可超出上限 2 项。
- 单文件 64 MiB（预留一行给最后的 `log.dropped`）。到达上限后停止写文件，控制台报告一次，停止时在文件末尾写带未写条数的 `log.dropped`。
- error 与 info 通过 `ManualLogSource` 镜像到控制台，trace 不镜像。序列化和消息文本都在后台线程完成，内核线程只做计数、复制和入队。
- `GameRuntimeBridge.Stop` 在 `StopRuntime` 之后结束写入，最多等 5 秒；超时时报告未写出的条数。进程被强杀时，队列中未写出的记录和文件上限的最后一行 `log.dropped` 会丢失。
- 限值只是内部构造参数，供测试注入，不对玩家开放配置。

以下尚未验证：游戏内 `ManualLogSource` 从后台线程调用是否安全、真实磁盘和退出时序，以及任何记录点的开销。

### 诊断不在宿主内

生成、空间、性能、异常聚合与报告已在 D2 全部迁到 [ForgeDevelopment](../ForgeDevelopment/README.md)，宿主程序集不引用 Development。制作端范围与对照实验方法的历史说明见 [ForgeDevelopment/AUTHORING-SCOPE.md](../ForgeDevelopment/AUTHORING-SCOPE.md)。

### 尚未有的能力

没有网络层——去重、权威端判定、迟加入、恢复、主机迁移、带宽预算和能力握手全部未实现，这是 U-NET 的范围。没有持续效果的属性回写；numeric lease 只是 SDK 内部的重算贡献记录，不代表游戏属性已改变。`BeginWorld` 不是检查点或网络恢复，恢复或迁移会暂停绑定，不能通过再次进入 Generating 绕过。没有 Control/Selector 嵌套图的完整 VM。

## 安装与配置

需要 BepInExPack GTFO 3.2.2。历史 1.1.x 发行物是单个 `ForgeRuntime.dll`；当前开发版拆出了 SDK，未来安装产物必须把 `ForgeRuntime.dll` 与 `ForgeRuntime.Framework.dll` 一起放入 Runtime 插件目录。**当前宿主编译通过不代表已完成原生或联机验收，本轮未执行安装。**

配置文件是 `BepInEx/config/NAinfini.ForgeRuntime.cfg`，只含宿主键：

```ini
[Runtime]
Mode = Play

[Logging]
Level = error
```

1.1.x 与 D2 之前写在本文件里的 `[Authoring]`、`[Performance Diagnostics]` 键不再被读取，也不自动迁移；诊断配置在 `NAinfini.ForgeDevelopment.cfg`，见 Development 的说明。

## 跨包结构与所有权

公共 SDK 与宿主归本包，所以整个模组仓库的跨包结构写在这里。

**依赖方向。** 领域原生插件（`ForgeEnemy.Native`、`ForgeMap.Native`、`ForgeWeapon.Native`、`ForgeDevelopment.Native`）单向引用宿主程序集取得 `Plugin.Runtime`，之后只用 SDK 类型；SDK 不引用宿主或领域，也不含 Unity、BepInEx 依赖。领域之间不引用对方程序集：Weapon 插件以 `BepInDependency` 依赖 `NAinfini.ForgeMap`，经 SDK 的 `ResolveEntityInstance("gtfo.player", …)` 取玩家引用。源码依赖、运行时能力闭包和发布包依赖是三件不同的事。

**工程。** `ForgeRuntime.csproj` 是 GTFO 宿主（模拟时钟、世界与会话桥、启动配置、计划发现 `GameBindings/PlanDiscovery.cs`、日志 writer `Logging/RuntimeLogWriter.cs`，不含诊断）；`Framework/ForgeRuntime.Framework.csproj` 是唯一公共 SDK。各领域的托管工程（`ForgeTrigger`、`ForgeMap`、`ForgeWeapon`、`ForgeEnemy`）只引用 SDK；`ForgeEnemy/ForgeEnemy.csproj` 只是托管辅助，不是玩家发行包；ForgeDevelopment 没有托管工程。根 `Forge.Architecture.sln` 覆盖 SDK、四个领域托管工程与 `tests/Architecture`，不代替宿主完整构建。架构验收检查：领域程序集只引用同一份 SDK、不含 Unity/BepInEx/Harmony 引用、不内嵌第二个内核、provider 可共存、重复注册以 `provider-conflict` 原子拒绝、注册不启动工作、注销幂等。全部工程 `net6.0`，没有 DI 容器、第二套事件总线或状态机框架。

**注册身份。** 只有调用方显式 `RegisterModule` 才登记 provider，加载程序集不启动任何工作。

| 模块 | provider ID | 版本 | 注册内容 |
| --- | --- | --- | --- |
| Development | — | — | 不登记 provider；原生插件 `NAinfini.ForgeDevelopment` 1.0.0 只在 `Authoring` 下启动 |
| Trigger | `forge.module.trigger` | `0.1.0` | 空；没有 BepInEx 插件 |
| Map | `forge.module.gtfo.map` | `0.1.0` | 清单为空；原生插件 `NAinfini.ForgeMap` 0.1.0 追加 `gtfo.player` resolver 与实例解析器 |
| Weapon | `forge.module.gtfo.weapon` | `0.1.0` | 2 个观察型 trigger 与 binding；原生插件 `NAinfini.ForgeWeapon` 0.1.0 登记 `gtfo.equipment` resolver |
| Enemy | `forge.module.gtfo.enemy` | `1.0.0` | binding 与 Hook 由原生插件 `NAinfini.ForgeEnemy` 1.0.0 注册；`ForgeEnemy/ModuleDefinition.cs` 只剩身份常量 |
| 共享 combat 合同 | `forge.contract.combat` | — | 5 个 canonical 定义，逐字采用网站目录行，0 个 binding |

宿主插件 `NAinfini.ForgeRuntime` 1.2.0 自己只有 4 个世界、会话、检查点 Hook。

**跨领域所有权。**

| 边界 | 唯一所有者 | 协作者如何使用 |
| --- | --- | --- |
| canonical 语义、端口与修订 | 网站目录 `catalog/capability-catalog.json`；C# 共享合同逐字采用目录行 | 领域只提供自己的 binding，不复制或覆盖 canonical ID |
| 调度、因果、预算、通用状态和结果 | Runtime | 领域提交公开请求，不复制队列或时钟 |
| 执行日志 writer | Runtime | 各包只调用记录接口；Development 只做提级与 trace |
| 网络可靠性、去重、权威判定、迟加入恢复 | Runtime（U-NET） | 领域不各自实现重传或去重 |
| 合法出生空间与拓扑 | Map | Enemy 提交尺寸、移动、碰撞要求，Map 组合遭遇数量、时机、分布 |
| 地图生成的底层逻辑 | Map | 近期只用原版 geomorph |
| 实体身份与 receiver | 实际拥有该游戏实体的领域模块 | 公共引用含 world/life epoch，不拿原生指针作内容 ID；其他领域经 SDK 实例解析取引用 |
| 装备槽、库存和部署实例 | Weapon | Map 管世界落点与任务物品关联；同一笔成本不能两方各扣一次 |
| 玩家倒地、复活、重生、传送、检查点 | Map 与 Runtime 生命周期 | Heal 不隐式复活，Teleport 不新建 life |
| 诊断与报告 | Development | 普通玩家无采集也能执行玩法 |

source、owner、instigator、recipient 四者不互换。实体种类不决定敌我关系，伤害或治疗极性不决定目标；未知关系、缺失 actor、过期 world/life epoch、不支持的 receiver 都必须给出明确结果。all-target 查询预算不足不得静默截断。定时效果分解为事实 → 选择/条件 → 公共 Pulse/Interval → 普通 Action → 结果，不为每种用途写一个计时器。

## 边界

已提供的是编译后宿主与 SDK 的元数据检查、本地游戏程序集的 Hook 签名检查，以及使用替身的启动与绑定测试。它们不代替实际注入、开销测量或主客机测试。

禁止无限重试、吞掉异常或调低游戏日志级别来伪装成功。
