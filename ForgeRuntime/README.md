# Infini Forge Runtime 1.0.0（开发中，未发布）

> 实施顺序与原版内容完整覆盖见[唯一开发计划](../../Infini-GTFO-Model-Site/Docs/forge-contract/FORGE-FRAMEWORK.md) §4，分步验收见 §7；本文件仅说明实现与用法。

Runtime 是唯一的公共服务与 GTFO 宿主：类型、注册、权限、生命周期、模拟调度、状态、事务结果，**以及多人环境下的网络可靠性与社区修复**。它不按领域包名分支，也不强制加载全部领域包或 Development。

交付要求见唯一开发计划 §6 U-RUNTIME、U-NET（链接见[仓库 README](../README.md)），带日期的验证记录见 [VALIDATION.md](VALIDATION.md)。跨包依赖规则、provider 身份与所有权见下文“跨包结构与所有权”。公共 SDK 的 API 细节见 [Framework/README.md](Framework/README.md) 与 [Framework/HOST-LIFECYCLE.md](Framework/HOST-LIFECYCLE.md)。

**1.0.0 尚未发布、安装或完成任何游戏验收。** 下面描述的全部能力都是 implementation-only。

## 当前能力

### 公共 SDK

`Framework/` 是不依赖 Unity 或 GTFO 类型的注册、严格计划验证、世界与实体生命周期、单一有界队列和执行结果合同，编译为独立的 `ForgeRuntime.Framework.dll`。第一方游戏模块与未来扩展都走同一个 `Plugin.Runtime.RegisterModule(module, level)`，级别是该包自己 cfg 的 `Logging.Level`。只允许模块用自己的返回句柄发布或取消——这是受信模块之间的所有权约束，不是任意第三方 DLL 的安全沙箱。

`Framework/CombatContracts.cs` 注册 5 个通用 combat canonical 定义（承伤事实、治疗动作、生命变化事实、死亡流程、肢体破坏），0 个 binding。Enemy 模块只注册自己的实际 binding 和 `gtfo.enemy:<GlobalID>` 接收器；来源、目标与阵营独立。

计划执行目前只支持 host 的单 Trigger → 线性 Action，步骤输入是直接事件输入或字面量二选一，入口只在 rejected/failed/cancelled/expired 或 partial+unknown 时停止。未知控制流、动态结果依赖、recipient-policy 与未实现的数据引用都明确拒绝。完整 Graph IR、查询服务、交易预留与多人大状态恢复属于后续工作。

**挂载匹配器不再有内核自有的种类。** `attachments[]` 的每个种类都由注册它的 provider 匹配：`RuntimeModule.AttachmentMatchers` 的值是 `AttachmentMatcherRegistration`，由 `BySubject`（针对事件主体匹配）或 `ByScope`（只看挂载目标，不看主体）二者之一构造，注册期就确定了这个种类要不要事件主体。派发时，无主体的注册项直接从挂载目标判定，因此世界、计时与脉冲这些不带主体的事件同样会被判到；有主体的注册项照旧在事件的 source 与触发载荷的实体端口上问。没有任何 provider 注册的 kind 在装载期按 `attachment-kind` 拒收（`level` 也不例外），所以计划不会出现"被接受但永不派发"的状态。provider 注销时它自己的 kind 一并移除，同一 kind 可以被下一个 provider 重新认领。

### 宿主启动与配置

宿主配置由 `RuntimeSettings.cs` 拥有：`Runtime.Mode`（默认 `Play`；Rundown 包不带基础包 cfg）、`Logging.Level`（默认 `error`）。这一项只管 Runtime 自己的 provider；每个领域包都有自己的 `[Logging] Level`，不共用这一项。原键名与命名、数字模式值都保留。模式按原始文本显式解析——真实的 `ConfigFile` 枚举绑定器曾被观察到对非法文本静默选中 `Authoring` 或组合枚举值，因此非法或空模式现在在原生初始化之前就失败，不会拿到一个"启用"的默认值。

`Off` 只绑定宿主键，不启动任何组件或 Hook，也不扫描计划。`Play` 与 `Authoring` 启动同一个宿主：1 个检查点 Hook（`harmony.PatchAll` 只扫描宿主程序集）、3 个 GTFO-API 关卡生命周期订阅、网络绑定（SNet 成员事件订阅；GTFO-API 的事件名在第一次 attach 时注册一次，见下）与 `FrameworkMonitor`。**宿主不含任何诊断、报告或性能采集**；两种模式唯一的区别是可选的 [ForgeDevelopment](../ForgeDevelopment/README.md) 插件只在 `Authoring` 下启动。模式变更需要重启进程，不支持热卸载。

关卡生命周期走 GTFO-API（`BepInDependency("dev.gtfomodding.gtfo-api", ">=0.5.0")`，硬依赖写最低版本），宿主不再自己补 `GameStateManager`：`LevelAPI.OnBuildStart` 对应原来的 `Generating`（丢旧世界、释放等待新远征的挂起）、`LevelAPI.OnEnterLevel` 对应 `InLevel`（玩家能移动时取主机基线并打开玩法阶段门槛）、`LevelAPI.OnLevelCleanup` 对应原来的 `OnLevelCleanup`/`OnResetSession`（关卡消失）。装上的是 GTFO-API 0.5.0：它没有 `EventAPI.OnGameStateChanged`，也没有检查点重载事件，所以检查点恢复的 detour 保留，主机迁移继续靠 `SNet.IsMaster` 变化在 tick 上判定。差异逐条见 [VALIDATION.md](VALIDATION.md) 的 2026-09-15 一节。

`GameAssembly` 哈希不符不再抛异常：宿主把 kernel 停在 `Registering`、置 `Plugin.IsSuspended`/`Plugin.SuspensionCode` 为 `startup-failed`，写一条 `runtime.suspended`（message 含期望与实际哈希），既不导出 manifest 也不加载计划。依赖包在自己的 Load 里查 `Plugin.IsSuspended`，停止注册并各自写一条 BepInEx error，不再走"Runtime unavailable"异常链。

`Plugin.ConfiguredMode` 是冻结的启动选择，公开在宿主程序集里，**不是权限、不是就绪状态、不是主机权威**。`Plugin.Runtime` 只在 Load 成功后提供；失败或 Off 状态下为 null，同一实例不重试 Load。依赖 Runtime 的插件在自己的 Load 中、Runtime 首个 FixedUpdate 之前注册。

启动失败时先关闭玩法入口再清理：宿主停止 → unpatch → 销毁 FrameworkMonitor。每个已获取的阶段都会被尝试清理，即使其中一步或错误上报失败也继续执行后面的步骤，最后重新抛出原始启动异常。清理与上报的失败保留在 `Data["ForgeRuntime.StartupCleanupFailures"]` 的 AggregateException 里（字典不可写时不能替换原始异常）。这是对已获取阶段的尽力清理，不是任意原生副作用的回滚保证。

**计划发现不再走单一配置路径。** `Play`/`Authoring` 下，首个固定更新在其他插件注册完成后，按包目录扫描离线计划：只看 `BepInEx/plugins` 的一级子目录，每个子目录下若存在 `forge/plans`（不存在则静默跳过，不算错误），取其中直接子文件、按 ordinal 精确匹配 `.plan.json` 后缀的文件（不递归、不识别其他扩展名）；发现顺序按 `/` 分隔的 BepInEx 相对路径以 `StringComparer.Ordinal` 排序。单文件超过 4 MiB 按 `json-size` 单独拒绝，且不计入下面的合并预算；其余文件按合并上限 256 个/64 MiB 做尾部优先淘汰——只要剩余集合仍超个数或字节上限，就反复剔除排序最靠后的一个文件（`plan-budget`），不是遇到第一个超限文件就停止接受后面的文件。链接/联接与转义检查只在确认 `forge/plans` 存在后，沿 `<目录>→forge→plans` 链与文件本身进行，命中按 `plan-path` 拒绝；IO 读取失败或非法 UTF-8 按 `invalid-json` 拒绝。每个文件独立产生一条 `plan.loaded`/`plan.rejected`（带 `path`，前者还带 `permissions`）；同一 planId 出现在多个文件中全部按 `plan-conflict` 拒绝，消息带上冲突组内全部相对路径；解析后的计划总数上限仍是 128（`plan-budget`）。本次进程只扫描一次，不支持热重载。实际注册清单输出到 `BepInEx/ForgeRuntime/runtime-manifest.json`；游戏不联网获取最新图。

### 执行日志

**内核记录点已接入。** 按 `forge.log` 事件码表，Runtime 归属与内核可见的记录点全部在内核里写出：`registration.rejected`、`binding.registered`、`plan.loaded`、`plan.rejected`、`world.began`、`trigger.fired`、`event.rejected`、`budget.exceeded`、`event.deferred`、`event.cancelled`、`step.started`、`step.finished`、`entry.stopped`、`observer.failed`、`runtime.suspended`，加上 writer 自己的 `log.level`/`log.dropped`。`adapter.*` 等适配器事件码定稿后再做，领域包内部的原生诊断码也不在本节范围。SDK 合同见 [Framework README](Framework/README.md#执行日志-sink-与级别)。

归属按合同的归属规则：`step.started`/`step.finished` 与 `trigger.fired` 归该 binding 所属的 provider，`binding.registered` 归被注册 binding 的 provider，`observer.failed` 归该观察者的 provider，`event.cancelled` 与其余 `event.*`、`plan.*`、`entry.stopped`、`budget.exceeded`、`world.began`、`runtime.suspended`、`registration.*` 归 Runtime。级别由码本身决定，只有 `step.finished` 按结果分级：`failed` 或 commit 为 `unknown` 记 error，其余记 info。`registration.rejected` 的 `subjectProvider` 是被拒绝的 provider，`runtime.suspended` 一条对应一次暂停（启动失败、主机迁移、检查点恢复、宿主回调异常、停止各一条）。

**内核是唯一来源。** 事件与步骤结果直接交给 sink，宿主不再订阅 `TickResult` 把命令/事件回执转成 BepInEx 警告，也不再逐 tick 镜像观察者故障——这两条旧路径已删除。error/info 不带 `inputs`，调用点不拼接字符串、记录是只读结构按 `in` 传递；`trace` 的 inputs 复制与转文本仍留给 ForgeDevelopment 的 trace 记录器，内核侧只保留 trace 级记录点。

阶段 A 的注册级别已落地：`RegisterModule(RuntimeModule, RuntimeLogLevel)` 的级别是必填参数，各包原生插件在 Load 里读自己的 `[Logging] Level` 后交给内核，级别不放进 `RuntimeModule`。级别表因此覆盖 Runtime 自己（`forge.runtime`，用宿主 cfg）以及每个已注册的领域 provider；注销即移除；注册时先发布新表、再写该 provider 的 `binding.registered` 记录，所以每条记录携带的表都已经列出它自己的 provider。未注册的 provider 仍以 `log-provider-unregistered` 拒绝，不给默认值。Runtime 自己随宿主注册的只有 CombatContracts 与 ControlContracts 两个内置 provider，它们没有包 cfg，走只对宿主程序集可见的内部路径取 Runtime 的级别；ForgeTrigger 有自己的插件与 cfg，走公开入口。

各包 cfg 与宿主同构：`[Logging] Level` 接受 `off`、`error`、`info`（大小写与首尾空白不敏感），默认 `error`，按原始文本解析，其他值（包括 `trace`）在注册之前失败。改动需要重启。`Runtime.Mode = Off` 时依赖 Runtime 的插件在自己的 Load 里直接返回，既不注册也不绑定 cfg。

`[Logging] Level` 接受 `off`、`error`、`info`（大小写与首尾空白不敏感），默认 `error`，按原始文本解析，其他值（包括 `trace`）在原生初始化之前失败。改动需要重启。`Runtime.Mode = Off` 时宿主不初始化，也就没有 writer。

宿主的 `Logging/RuntimeLogWriter.cs` 实现 sink，写 `forge.log` JSONL：

- 惰性启动：第一条被接受的记录才创建后台线程、目录和文件。没有记录就没有线程、目录、文件和控制台输出。
- 文件是 `BepInEx/forge-logs/<yyyyMMddTHHmmssZ>-<4 位十六进制随机>.jsonl`，UTF-8 无 BOM，`\n` 换行，`CreateNew` 不覆盖。新文件建好后按修改时间只保留最新 10 个，只删该目录顶层的 `*.jsonl`。
- 第一行是 `log.level`（level 为 info，带 `levels[{provider, level}]` 与 `elevated`），与第一条真实记录一起写出。级别表每变一次都会发布新快照（后面的记录因此带上最新的 provider 列表），但文件只在 tier 变化时补一行 `log.level`：首行之外最多再有一行提级行，与合同「一个文件最多两行」一致。
- 限流：普通档每 tick 256 行、队列 8192；提级档每 tick 4096 行、队列 65536。超限的记录丢弃并计数，下一条被接受的记录前或停止时写 `log.dropped`（带 `count`，level 为 error）。`log.level` 与 `log.dropped` 不受限流，因此队列最多可超出上限 2 项。
- 单文件 64 MiB（预留一行给最后的 `log.dropped`）。到达上限后停止写文件，下一条被内核线程接受的记录前报一次 `log.dropped`，停止时在文件末尾写带未写条数的 `log.dropped`。
- error 与 info 通过 `ManualLogSource` 镜像到控制台，trace 不镜像。镜像发生在调用 `Write()` 的那个内核线程上（BepInEx 会把消息同步分发给所有 `ILogListener`，监听器可能在未注册线程上碰 IL2CPP 对象）；后台线程只做序列化、写文件和保留清理，一次 BepInEx 调用都没有。文件建不出来、写失败或保留清理失败时，后台线程只记计数与原因，由下一次内核线程上的 `Write()`（或 `Dispose()`）按合同写成 `log.dropped`，message 里带具体原因，不再另造通道。
- `GameRuntimeBridge.Stop` 在 `StopRuntime` 之后结束写入，最多等 5 秒；超时时报告未写出的条数。进程被强杀时，队列中未写出的记录和文件上限的最后一行 `log.dropped` 会丢失。
- 限值只是内部构造参数，供测试注入，不对玩家开放配置。

以下尚未验证：真实磁盘和退出时序、游戏内文件 IO 的耗时，以及任何记录点的开销。内核记录点只在托管测试替身上检查过，没有实机运行，也没有在游戏内导出 `forge-logs` jsonl 作为诊断证据。

### 诊断不在宿主内

生成、空间、性能、异常聚合与报告已全部迁到 [ForgeDevelopment](../ForgeDevelopment/README.md)，宿主程序集不引用 Development。作者工具交付与验收见唯一开发计划 §6 U-DEV-MOD 和 §7。

### 网络层（已接入宿主，未联机验证）

纯逻辑网络层（`Network/**`）现在由宿主接线，规则按裁定 19/20/21：计划执行只有主机权威、去重键固定为 `(sender session, worldEpoch, planId, eventId)`、握手不匹配只挂起客机。`GameBindings/NetworkBinding.cs` 拥有这一层唯一的 `NetworkHost`：`GameRuntimeBridge.Initialize` 在哈希校验通过并 `BeginWorld` 之后 `Start()`，`GameRuntimeBridge.Stop` 与 `Plugin.Load` 的回滚链都会 `Stop()`（幂等，`network` 阶段排在 `runtime` 之后、`level_events` 之前）。会话就绪用 SNet 的成员事件判定：`SNetwork.SNet_Events.OnPlayerJoin`/`OnPlayerLeave`（`SNet_ASM.dll` 中两个静态委托字段，ilspycmd 证据）触发重读 `SNet.HasLocalPlayer`/`LocalPlayer.Lookup`/`IsMaster`/`Master.Lookup`，`LevelAPI.OnEnterLevel` 时也重读一次（玩家能移动即会话存在，重读幂等）；`Attach(本地会话)`/`Detach()` 只发生在这一处。Off 模式与启动被挂起（`startup-failed`）时 `NetworkBinding.Start` 根本不会被调用，所以一个游戏事件也不会注册。

`Role` 按会话重建而不是在插件加载时冻结：`SNet.IsMaster` 为真即 `Host`，否则 `Client`。`Identity` 取 kernel 的 `RuntimeIdentity`；`Plans` 取 [Framework/RuntimeKernel.PlanIdentities.cs](Framework/RuntimeKernel.PlanIdentities.cs) 暴露的已加载快照（`planId`、`resourceId`、`resourceRevision`、ordinal 排序后用 `,` 连接的 binding pins），在首个 FixedTick 里 `LoadPlans` 之后由 `NetworkBinding.AdoptPlans` 交给握手。hello 不再携带计划摘要：报文里只有计划条数和 32 字节计划集摘要（对 ordinal 排序后的 `planId|resourceId|resourceRevision|bindingPins` 行取 SHA-256），摘要在采纳计划集时算一次，不在 tick 里算，也不再有 16 条计划的上限（条数字段 1 字节，内核自身上限 128，超出按 `payload-too-large` 拒绝）。比较只用条数加摘要：本地还没采纳计划集时回 `plans-unavailable`（属重试，不是不匹配），条数或摘要不同回 `plan-set-mismatch`，说明写「Plan sets differ: peer advertises N plans, this runtime has M.」并进入客机的可见警告。发送时机：有会话且已采纳计划集的一方在 attach 时发一次 hello，之后只在自己采纳的计划集变化时再发；主机采纳计划集时发出的那条 hello 同时就是就绪信号，此前被 `plans-unavailable` 拒绝的客机收到它以后重发一次自己的 hello（不需要定时器，也不依赖 epoch 广播，重复的拒绝不会再触发第二次）。`SenderIsMaster` 每次比较 `SNet.Master` 的会话号，不再是 `_ => false`。世界 epoch 只在 `GameRuntimeBridge.InvalidateWorld` 真正推进 `_epoch` 之后广播，reason 按调用来源区分新世代、关卡清理、检查点挂起（`NativeHooks` 的 `checkpoint-restore`）与宿主挂起（新增 `WorldChangeReason.HostSuspend = 4`，只加枚举值，报文字节布局未动）；命令派发途中到达的转换进 `PendingWorldChange`，在派发结束的那次真实推进处只广播一次。客机不执行计划、也不自行推进 epoch，只接受主机的广播，且握手完成前一律按 `epoch-unavailable` 拒绝（主机会话尚未绑定、`Synchronized` 为假），握手完成后才接受。握手不匹配（裁定 21）只在客机侧挂起：`GameRuntimeBridge.Suspend(<握手拒绝码>, <可见详情>)` 加一条 BepInEx 警告；主机只拒绝那个 peer，不暂停自己。

**GTFO-API 0.5.0 的事件名一个进程只能注册一次。** ilspycmd 反编译证据：已就绪时 `NetworkAPI.RegisterEvent<T>` 走 `NetworkAPI_Impl.Instance.RegisterEvent`，内部是 `m_Events.Add(...)`（`Dictionary`），未就绪时进 `s_EventCache`，两条路径对同名第二次注册都抛 `ArgumentException("An event with the name X has already been registered.")`，既不覆盖也不追加；`NetworkAPI.IsEventRegistered` 在 `Awake` 之前会因 `Instance` 为 null 抛 `NullReferenceException`，所以没有拿它当守卫。因此 `ForgeNetworkTransport` 把游戏侧注册做成进程内一次：第一次 attach 时注册 6 个事件名（hello/ack/world epoch/command request/command result/fact），`Detach` 只摘掉本层 listener，`Attach → Detach → Attach` 既不重复注册也不丢投递。

`Network/NetworkMessages.cs` 是生成文件，布局表在 `tools/NetworkMessagesGen`（独立工程，`ForgeRuntime.csproj` 用 `Compile Remove="tools\**\*.cs"` 把它排除在插件之外）：改报文布局要改工具里的字段表，然后在仓库根重跑 `dotnet run --project ForgeRuntime/tools/NetworkMessagesGen` 重写该文件；提交前用 `dotnet run --project ForgeRuntime/tools/NetworkMessagesGen -- --check` 校验（生成到内存逐字节比较，不一致 exit 1）。文件头两行也写着这条规则，不要手改生成文件。

尚未有：hello 有了发送点（attach、采纳计划集、被 `plans-unavailable` 拒绝后随主机就绪 hello 重发一次），但**两台真机之间的握手与 epoch 接受从未实测**（纯逻辑测试见 `tests/Network`），所以真机上能否握手成功仍未验证。没有任何领域执行器，`NetworkHost.CommandExecutor` 保持为空，`CommandRequest` 一律以 `NetworkCodes.NoConsumer`（`no-consumer`）在去重账本之前拒绝，装了执行器之后同一 key 仍可执行；被请求的两档已有接收端：`presentation` 与 `owner` 用同一个 `CommandRequest` 的 node index 区分（1 / 2），客机按 `RuntimeKernel.ExecutePresentationCommand` / `ExecuteOwnerCommand` 执行并回 `CommandResult`，owner 档的提交 scope 走 ResourceId 槽、结果连同 fact 计数一起回主机，两档都按“发件人是本会话主机 + endpoint 指向本会话”过滤；`ResultObserver`/`FactObserver` 未接线，`CommandResult`/`Fact` 仍经过版本、角色、主机、世界与账本校验，然后没有回调地丢弃。整个网络层没有任何实机或联机验证。

### 尚未有的能力

网络层已接线但还不能联机：hello 有发送点，但两台真机之间的握手与 epoch 接受未实测；没有领域执行器、没有 fact 发送路径，迟加入、恢复与主机迁移仍属 U-NET 范围（迁移继续按不支持处理：`SNet.IsMaster` 变化即挂起）。没有持续效果的属性回写；numeric lease 只是 SDK 内部的重算贡献记录，不代表游戏属性已改变。`BeginWorld` 不是检查点或网络恢复，恢复或迁移会暂停绑定，不能通过再次进入 Generating 绕过。没有 Control/Selector 嵌套图的完整 VM。

## 安装与配置

需要 BepInExPack GTFO 3.2.2。历史 1.1.x 发行物是单个 `ForgeRuntime.dll`；当前开发版拆出了 SDK，未来安装产物必须把 `ForgeRuntime.dll` 与 `ForgeRuntime.Framework.dll` 一起放入 Runtime 插件目录。**当前宿主编译通过不代表已完成原生或联机验收，本轮未执行安装。**

配置文件是 `BepInEx/config/NAinfini.ForgeRuntime.cfg`，只含宿主键：

```ini
[Runtime]
Mode = Play

[Logging]
Level = error
```

1.1.x 的旧版本与诊断迁移之前写在本文件里的 `[Authoring]`、`[Performance Diagnostics]` 键不再被读取，也不自动迁移；诊断配置在 `NAinfini.ForgeDevelopment.cfg`，见 Development 的说明。

## 跨包结构与所有权

公共 SDK 与宿主归本包，所以整个模组仓库的跨包结构写在这里。

**依赖方向。** 领域原生插件（`ForgeTrigger.Native`、`ForgeEnemy.Native`、`ForgeMap.Native`、`ForgeWeapon.Native`、`ForgeDevelopment.Native`）单向引用宿主程序集取得 `Plugin.Runtime`，之后只用 SDK 类型；SDK 不引用宿主或领域，也不含 Unity、BepInEx 依赖。领域之间不引用对方程序集：Weapon 插件以 `BepInDependency` 依赖 `NAinfini.ForgeMap`，经 SDK 的 `ResolveEntityInstance("gtfo.player", …)` 取玩家引用。源码依赖、运行时能力闭包和发布包依赖是三件不同的事。

**工程。** `ForgeRuntime.csproj` 是 GTFO 宿主（模拟时钟、世界与会话桥、启动配置、计划发现 `GameBindings/PlanDiscovery.cs`、日志 writer `Logging/RuntimeLogWriter.cs`，不含诊断）；`Framework/ForgeRuntime.Framework.csproj` 是唯一公共 SDK。各领域的托管工程（`ForgeTrigger`、`ForgeMap`、`ForgeWeapon`、`ForgeEnemy`）只引用 SDK；`ForgeEnemy/ForgeEnemy.csproj` 只是托管辅助，不是玩家发行包；ForgeDevelopment 没有托管工程。根 `Forge.Architecture.sln` 覆盖 SDK、四个领域托管工程与 `tests/Architecture`，不代替宿主完整构建。架构验收检查：领域程序集只引用同一份 SDK、不含 Unity/BepInEx/Harmony 引用、不内嵌第二个内核、provider 可共存、重复注册以 `provider-conflict` 原子拒绝、注册不启动工作、注销幂等。全部工程 `net6.0`，没有 DI 容器、第二套事件总线或状态机框架。

**注册身份。** 只有调用方显式 `RegisterModule(module, level)` 才登记 provider，级别同样是必填；加载程序集不启动任何工作。

| 模块 | provider ID | 版本 | 注册内容 |
| --- | --- | --- | --- |
| Development | — | — | 不登记 provider；原生插件 `NAinfini.ForgeDevelopment` 1.0.0 只在 `Authoring` 下启动 |
| Trigger | `forge.module.trigger` | `1.0.0` | 1 条 `evaluate` 绑定（`forge.condition.predicate.compare`）；由本包原生插件 `NAinfini.ForgeTrigger` 1.0.0 注册，宿主不再内联编译或代注册 |
| Map | `forge.module.gtfo.map` | `1.0.0` | 清单为空；原生插件 `NAinfini.ForgeMap` 1.0.0 追加 `gtfo.player` resolver 与实例解析器 |
| Weapon | `forge.module.gtfo.weapon` | `1.0.0` | 2 个观察型 trigger 与 binding；原生插件 `NAinfini.ForgeWeapon` 1.0.0 登记 `gtfo.equipment` resolver |
| Enemy | `forge.module.gtfo.enemy` | `1.0.0` | binding 与 Hook 由原生插件 `NAinfini.ForgeEnemy` 1.0.0 注册；`ForgeEnemy/ModuleDefinition.cs` 只剩身份常量 |
| 共享 combat 合同 | `forge.contract.combat` | — | 5 个 canonical 定义，逐字采用网站目录行，0 个 binding |

宿主插件 `NAinfini.ForgeRuntime` 1.0.0 自己只有 1 个检查点 Hook 与 3 个 GTFO-API 关卡事件订阅。

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
