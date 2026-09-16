# ForgeMap 验证记录

**上次更新：2026-09-16**

## door/terminal 动作成员的原生证据与布局审计收口（2026-09-16）

**已实现并已测（静态证据 + 合成替身）**：`map-a1-native` 新增的 door/terminal 原生读写成员逐条进入共享 evidence 与布局审计的已声明集合，`MapNativeLayout`（`Category=Native`）从 4 项红转绿。**没有启动游戏**、没改 Runtime/网站、没新增 capability/binding，只做证据与审计分类收口。

- **共享 evidence 新增 14 条成员行**（`evidence/door-terminal-hooks.json` 的 `members`）：4 条动作层读取（`LG_SecurityDoor.get_InteractionAllowed`、`LG_ComputerTerminal.get_m_command`、`LG_ComputerTerminalManager.get_Current`、`Localization.LocalizedText.get_HasValue`）、8 条声明写入，以及空桩与弱门两条对照行。每条带 `entry`（read/write/none）与 `evidenceLevel`（`metadata` 或 `static-native`）；方法成员带本 build 的 `rva`/`fileOffset`/首字节，字段成员（`m_command` 0xB0、静态 `Current` 0x0）没有 RVA，改记 `fieldOffset` 并写明是 interop 为字段生成的属性读取。新增 `evidenceVocabulary` 说明这几个字段的含义。
- **空桩不会被读成成功入口**：`LG_SecurityDoor.AttemptDamage`（`0x353190`/`0x351790` = `C2 00 00` 即 `ret 0`，该地址在本机 dump 里有 **2102** 条登记）记 `evidenceLevel: static-native` + `bodyShape: empty-stub` + `entry: none`，并在 `refusals` 里以 `door-not-damageable` 说明；对照行 `LG_WeakDoor.AttemptDamage`（`0x15C2200`）是真实函数体。写集里**没有**这一条。`door_unlock`（只有 `onlyUnlock` 参数名可作候选）与 `door_alarm`（只是 `m_hasActiveEnemyWave` 标志）同样只进 `refusals`，`crush`/`force` 关门按名字拒绝，"Print 的 AddLine 本地可见性"留在 `unproven`。
- **审计的已声明写入集**：`evidence/map-hooks.json` 新增 `writes`（8 条，含 `type`/`isStatic`/完整签名），与 `readbacks` 对称，`writeVocabulary` 写明两向比较规则。`tests/MapNativeLayout` 把 `native.read-only-game-access` 换成三项检查：`native.game-writes-are-declared`（任何非 getter 游戏调用必须被声明）、`native.declared-writes-exact`（按**完整签名**两向比较，因此能分辨 `AddLine` 的三个重载）、`native.writes-live-in-the-action-layer`（写入调用只出现在 `DoorActions`/`TerminalActions`），并为每条 write 行加 `write.<id>.game-member`（签名唯一命中 + `isStatic`）。读的一方把已声明写成员从读集里剔除；`Localization.` 归入 map-object 读取并补进 spec 的 `localized-text-has-value`。hitobj 的 `GetComponentInParent` 读拼写与既有证据一行未改。
- **反假绿实测**（改临时 spec 副本或临时改回读拼写，跑完即还原；仓库只留本批改动）：删掉 `terminal-add-line` 写行 → `native.game-writes-are-declared` 报 `AddLine`；把该行签名换成另一个重载 → `native.declared-writes-exact` 报 missing/extra；加一条真实但没人调用的写行 → 报 missing；把 `door-force-open-entry` 的 `isStatic` 改错 → `write.<id>.game-member` 报出；删 `localized-text-has-value` 或 `door-interaction-allowed` 读回行 → `native.exact-map-object-reads` 各报 missing；把 `GetComponentInParent` 移出 `readSpellings` → `native.game-writes-are-declared` 报 `UnityEngine.Component::GetComponentInParent`。
- **证据所有权核对**（临时脚本，只读）：ForgeMap/evidence 全部 JSON 共 239 行、185 个成员、121 个地址，**没有 RVA 所有者冲突**（同一地址只出现在同一成员的多行之间）；`door-terminal-hooks.json` 的 `dumpLine` 全部指向成员自己的行。发现并**只报告不改**：`objective-hooks.json`（20 行）与 `encounter-wave-hooks.json`（27 行）用类型声明行当 `dumpLine`，其中 3 处与 `door-terminal-hooks.json` 记的成员行号直接冲突（`OnDoorState` 686903/687024、`OnStateChange` 681625/682276、`SetActiveEnemyWaveEnabled` 686903/687039）。顺带修正本批文件内一处既有错误：`zone-terminals` 的 `dumpLine` 698256 是无关 `struct LinkBeforeBuild`，改为成员自己的 698169，并补 `rva` `0x3A5F30`（该地址有 32 条登记，是生成的访问器 thunk，不是独占函数体）。
- **仍需实机确认**（已写进该文件 `unproven` 与 `inGameVerificationPlan` 的 Action confirmation 1–7）：`onlyUnlock` 的语义、波次标志是否真的起波、`AddLine` 的行是否到客户端、`LocalizedText` 文本是否成为门提示、无玩家时命令入口是否受理、是否存在别的伤害路径、`TryGetCommand` 是否不动解释器状态。证据等级仍是 `metadata-and-static-native-call-graph-only`，`gameVerified` 仍是 `false`。

本轮实际运行的命令与结果（artifacts 目录 `%TEMP%\map-a1-evidence\artifacts`；构建用 `Forge-MapEditor-QA` profile 作编译引用）：

| 检查 | 命令 | 结果 |
| --- | --- | --- |
| 宿主 / Map / Map 原生构建 | `dotnet build ForgeRuntime/ForgeRuntime.csproj`、`ForgeMap/ForgeMap.csproj`、`ForgeMap/Native/ForgeMap.Native.csproj`（同一 `--artifacts-path`，后两者与前者的 `-p:GTFOBepInExPath=<QA profile>`、`-p:ForgeRuntimeAssembly=…`） | 各 `Build succeeded`，0 警告 0 错误，exit 0 |
| 原生读/写集布局（MapNativeLayout，`Category=Native`） | `dotnet test ForgeMap/tests/MapNativeLayout -c Release --artifacts-path $a --filter "Category=Native"`（`FORGE_ARTIFACTS`/`FORGE_MAP_BEPINEX`/`FORGE_MAP_HOOK_SPEC` 绝对路径；测试工程另需 `GTFOBepInExPath` 才能解析 `core/Mono.Cecil.dll`） | `Failed: 0, Passed: 1`，exit 0（审计内部 **127 条检查**全过；修复前实测 4 项红：`native.read-only-game-access`、`native.reads-are-declared`、`native.exact-map-object-reads` actual=41 expected=30、`native.every-game-read-is-classified` 61=19+41） |
| 静态原生证据（MapNativeEvidence，`Category=Native`） | `dotnet test ForgeMap/tests/MapNativeEvidence -c Release --artifacts-path $a --filter "Category=Native"`（加 `FORGE_GTFO_GAME`/`FORGE_GTFO_DUMP`） | `Failed: 0, Passed: 1`，exit 0 |
| 原生替身（MapNativeAdapter） | `dotnet test ForgeMap/tests/MapNativeAdapter -c Release --artifacts-path $a --filter "Category!=Native"` | `Failed: 0, Passed: 84`，exit 0 |
| 观察 / 选择器 / 合同 / 身份回归 | `MapObjectObservation`（带 `FORGE_MAP_ADDRESS_VECTORS`）、`MapSelectorDispatch`、`MapContracts`、`MapIdentity`（均 `Category!=Native`） | 98 / 12 / 12 / 30 全过，exit 0 |

`%TEMP%\map-a1-native\bep` 是只读重排的 interop + `GameAssembly.dll` 副本，没有 `plugins/GTFO-API.dll`，所以构建统一用 QA profile 作编译引用；测试的 `FORGE_MAP_BEPINEX` 仍指向该副本（`GameAssembly.dll` 的 SHA256 与 `map-hooks.json` 冻结值一致，`MapNativeEvidence` 的 `native.hash` 检查通过）。

## `gtfo.map_object` 的实例解析接受命中对象（2026-09-16）

**已实现并已测（合成输入）**：`ResolveEntityInstance("gtfo.map_object", <子弹碰撞体>)` 现在能解析出门与终端的既有地址。**没有启动游戏**，结论来自合成替身、xUnit 断言与编译后程序集的元数据审计；"碰撞体是否真的挂在门/终端之下"是运行期未证项，已写进 `evidence/door-terminal-hooks.json`。

- **唯一新类型**：`Native/MapObjectHit.cs`，把命中对象（`UnityEngine.Component`）用 `GetComponentInParent<LG_SecurityDoor>()` / `<LG_ComputerTerminal>()` 爬到该 kind 自己的原生实例，两个 category 固定顺序查找，爬不到返回 null。`MapObjectModule.ResolveInstance` 先调用它解包，再走**原有**的 `Source(instance)`/`TryAddress`/`EntityId`：地址语法、`MapObjectRefusal` 逐类原因、`IsCurrent`、观察器与 `map-object` 挂载匹配一行未改，也不新增第二张解析表。
- **归属分层**：`ForgeMap`（不引用 Unity 的事实源程序集）只多了一个可选构造参数 `Func<object, object?>? hit`；Unity 类型与父级爬取只在 `ForgeMap.Native`，`MapPluginSession` 传入 `MapObjectHit.Instance`。
- **不产生伪引用**：没有门/终端祖先的碰撞体（世界几何、部署物、敌人或玩家伤害肢）返回 null；门在但不可寻址（非入口门、bulkhead）仍走门自己的拒绝路径，每类原因只报一次；一律不发布事实。
- **布局审计**：`UnityEngine.Component::GetComponentInParent` 已按"名字不以 `get_` 开头的纯查询"登记进 `tests/MapNativeLayout` 的 `readSpellings`（与既有的 `TryCast`、`op_Implicit` 同类；它是 `UnityEngine.*` 引擎调用，本就不进 spec 的 `readbacks` 与 `native.exact-map-object-reads` 两侧的比较）。
- **顺带修掉的既有隐患**：`ZoneIndex.Current` 原先把 `epoch == null`（"没有 world"）读到的那张表也缓存进 `Cached`，下一个 world 用同一个 `null` 键就能命中它——本轮新测试暴露为"上一个关卡的门地址回答了这一个关卡"。现在 `epoch == null` 时不写缓存，只在方法注释里说明；生产语义不变（无 world 时本来就只有空表可读）。
- **新增用例 3 个**（`tests/MapNativeAdapter/MapObjectHitTests.cs`）：`map_object_resolver_accepts_a_hit_object_and_answers_its_own_entity`（门、终端、跨两层祖先各自解析到既有地址）、`map_object_resolver_refuses_a_hit_object_with_no_map_object_ancestor`（世界几何与伤害肢返回 null）、`map_object_resolution_never_invents_an_address`（非入口门与 bulkhead 的命中对象返回 null，且每类原因恰好一条诊断、`PublishedFacts == 0`）。`SyntheticLevel` 由 `MapNativeAdapterTests` 的私有嵌套类提为独立文件并改为显式接收 kernel，两个测试类共用同一份层级夹具；`MapObjectFixture` 补一个 `StubHit` 与一条 climb 委托，托管模块用例同样覆盖解包路径。

本轮实际运行的命令与结果（artifacts 目录 `%TEMP%\hitobj\artifacts`；`GTFO_BEPINEX_PATH` 指向只读的 `Forge-MapEditor-QA` profile）：

| 命令 | 退出码 | 结果 |
| --- | --- | --- |
| `dotnet build ForgeMap/ForgeMap.csproj -c Release --artifacts-path $a` | 0 | `Build succeeded`，0 警告 0 错误 |
| `dotnet build ForgeMap/Native/ForgeMap.Native.csproj -c Release --artifacts-path $a -p:ForgeRuntimeAssembly=…` | 0 | `Build succeeded`，0 警告 0 错误 |
| `dotnet test ForgeMap/tests/MapNativeAdapter -c Release --artifacts-path $a` | 0 | `Failed: 0, Passed: 84`（含本轮 3 个新用例；同工作区并行任务 map-a1-native 的用例也在同一程序集内，均通过） |
| `dotnet test ForgeMap/tests/MapObjectObservation -c Release --artifacts-path $a`（另设 `FORGE_MAP_ADDRESS_VECTORS` 指向网站仓的向量文件） | 0 | `Failed: 0, Passed: 98` |
| `dotnet test ForgeMap/tests/MapNativeLayout -c Release --artifacts-path $a`（`FORGE_ARTIFACTS`/`FORGE_MAP_BEPINEX`/`FORGE_MAP_HOOK_SPEC` 绝对路径） | 1 | 本轮新增的 `UnityEngine.Component::GetComponentInParent` 已登记且不再被判为写；当时剩余 4 项红是并行任务 map-a1-native 刚加入的门/终端动作成员（`AttemptOpenCloseInteraction`、`ForceOpenSecurityDoor` 等尚无证据行），已由本文顶部"door/terminal 动作成员的原生证据与布局审计收口"一节补齐并转绿 |

未在游戏内核验：子弹碰撞体是否真的挂在门/终端之下（门刀换父、终端屏幕碰撞体的层级与 `GetComponentInParent` 的返回顺序）；`LG_DoorBladeCuller` 之类是否会在运行期改变父级；弱门与区域入口闸是弱门时的比例。全部写进 `evidence/door-terminal-hooks.json` 的 `unproven`，游戏内确认步骤写进同一文件的 `inGameVerificationPlan`（`Hit confirmation 1`/`2`）。

## 远征结束触发，以及 `map` resource 端口的框架边界（2026-09-15）

`forge.trigger.session.expedition_ended` 按网站授权目录该行逐字注册：`execution host`、domains `map/session/logic`、无输入、无参数、输出 `next`（execution）与 `outcome`（enum，schema `execution_outcome`）。管理侧新增 `ExpeditionContract.cs`（目录行、binding 行、三种结局到 `execution_outcome` 的映射）与 `ExpeditionModule.cs`（主机门、world epoch 与结局共同构成事件 id、未知结局不发布且只报一次、把内核的原样 `DispatchResult` 交回调用方）；`MapObjectModule` 在既有的那一条注册上组合该半、在 `Dispose()` 里释放它。原生侧新增 `Native/ExpeditionHooks.cs`：`RundownManager.OnExpeditionEnded(ExpeditionEndState)` 的 `Priority.Last` postfix，只有主机发布，结局取游戏自己给出的那个参数，不从游戏状态名、人数或卸载顺序反推。

事件身份是 `forge.trigger.session.expedition_ended:<world epoch>:<结局>`：同一世界同一结局重复上报得到内核的 `duplicate`（一次派发），同一世界的另一个结局是它自己的事件，换世界后同一结局是新事件。载荷只有 `outcome`，值是枚举成员下标而不是名字。

`forge.trigger.session.expedition_started` **未注册**。它的 `map` 输出是 resource 端口，而本运行时没有 resource 值：`RuntimeGraphContracts.RuntimeValueTypes` 不含 resource（`RuntimeGraphContracts.cs:89`，并在 `:96` 写明 resource 从不进入帧），`RuntimeJson.ValidateValue` 没有 resource 分支（落到 `unsupported-port`），`RuntimePlan.cs:288-293` 对触发器的非 execution 输出端口要求运行期值类型或 handle，因此带该端口的触发行会让**任何**订阅它的计划在装载期以 `unsupported-event-port` 被拒；resource 唯一的合法用法是步骤输入上的编译期常量 `{id, revision}`（`RuntimePlan.cs:338-339,382-393`）。在仓库外的一次性消费者上按目录逐字声明该行做实测：注册成功，装载订阅它的计划返回 `loaded=false code=unsupported-event-port detail=Entry.map`。目录行与 manifest 必须同形状，所以不注册窄化版本，也不为 `map` 填占位值；代价是这一步的"进关即执行"入口仍然缺失，属于框架能力缺口而不是本包的取舍。

| 检查 | 命令 | 结果 |
| --- | --- | --- |
| 宿主、Map 与 Map 原生构建 | `dotnet build ForgeRuntime/ForgeRuntime.csproj`、`dotnet build ForgeMap/ForgeMap.csproj`、`dotnet build ForgeMap/Native/ForgeMap.Native.csproj`（同一 `--artifacts-path %TEMP%\levtrig\artifacts`，`-p:ForgeRuntimeAssembly=…`） | 各 0 警告 0 错误，exit 0 |
| 会话触发行用例（MapObjectObservation，新 `ExpeditionTriggerTests`） | `dotnet test ForgeMap/tests/MapObjectObservation -c Release --artifacts-path %TEMP%\levtrig\artifacts`（带 `FORGE_MAP_ADDRESS_VECTORS`） | 98 项通过（原 84 项 + 14 项），exit 0 |
| 原生替身（MapNativeAdapter） | `dotnet test ForgeMap/tests/MapNativeAdapter -c Release --artifacts-path %TEMP%\levtrig\artifacts` | 57 项通过（新增钩子路径用例；hook 集合与 capability 集合断言已同步），exit 0 |
| 合同 / 身份 / 派发回归 | `dotnet test ForgeMap/tests/MapContracts`、`MapIdentity`、`MapSelectorDispatch` | 12 / 30 / 12 项通过，exit 0 |
| 原生读集布局（MapNativeLayout，`Category=Native`） | `dotnet test ForgeMap/tests/MapNativeLayout -c Release --filter "Category=Native"`（`FORGE_ARTIFACTS`/`FORGE_MAP_BEPINEX`/`FORGE_MAP_HOOK_SPEC` 绝对路径） | 1 项通过（规格 6 条钩子），exit 0 |
| 静态原生证据（MapNativeEvidence，`Category=Native`） | `dotnet test ForgeMap/tests/MapNativeEvidence -c Release --filter "Category=Native"`（加 `FORGE_GTFO_GAME`/`FORGE_GTFO_DUMP`） | 1 项通过（新钩子的 interop 签名与 dump RVA `0x13E2100` 都在审计内），exit 0 |
| 框架与原生绑定（只运行） | `dotnet run --project ForgeRuntime/tests/Framework`、`dotnet run --project ForgeRuntime/tests/GameBindings` | 482 项通过 exit 0；`INCOMPLETE 37 native-module boundary assertions; BLOCKED 2` exit 0（两条被阻塞的是 junction 发现用例，宿主沙箱拒绝子进程建目录联接，与本次改动无关） |
| 发行身份（只运行） | `pwsh -File Release/check-identity.ps1` | 83 项 83 通过，exit 0 |
| 运行清单导出 | `dotnet run --project Release/export-runtime-manifest -- --release Release/release.json --output %TEMP%\levtrig\runtime-manifest.json` | exit 0，5 个玩家包；导出文件中该行的 domains/execution/inputs/outputs（含 `execution_outcome`）与目录逐字段一致 |

新用例覆盖：本关的计划被认领（`queued`）、另外三个关卡身份一律 `attachment-mismatch` 且不报诊断、身份读不到时不匹配任何计划、同一世界同一结局第二次发布得到 `duplicate` 且计数不变、同一世界另一个结局是第二个事件、换世界后同一结局是新事件、三种结局各自的 `execution_outcome` 下标（0/3/4）、未知结局既不发布也只报一次、客机（`Authority=false`）不发布且只报一次、以及声明形状等于目录行（端口、域、执行层、无输入无参数）。原生替身用例证明加倍的原生回调被接受、钩子只主机发布、以及钩子集合与 provider 的 capability 集合仍然逐项相等。

**边界**：全部是托管替身、静态程序集读取与只读 interop/dump 读取；没有启动游戏，没有安装或复制任何 profile 文件。`MapNativeLayout`/`MapNativeEvidence` 的输入是只读重排：`%TEMP%\levtrig\bep`（游戏根目录 `GameAssembly.dll` 副本 + 指向 `Forge-MapEditor-QA` profile 的 `interop`/`core` 目录联接），编译另用 `GTFO_BEPINEX_PATH` 指 profile。游戏对一次远征实际报几次结局、检查点重载是否再报、事件是否在关卡清理前进入派发、以及客机路径，都只有静态证据，清单在 `evidence/expedition-trigger.json` 的 `inGameVerificationPlan` 与 `unproven`。

**并行任务**：`Native/MapPluginSession.cs` 与 `tests/MapSelectorDispatch` 由另一个任务改动，本次没有改它们；会话半因此由 `MapObjectModule` 在同一条注册上组合（见 README 的"远征结束触发"），原生回调复用会话既有的 `GuardMapObjects` 路径而不是新增一个守卫方法。上面 MapNativeAdapter 的 57 项是在两批改动同时存在的工作树上跑的。

## 玩家候选来源与选择器的单列举路径（2026-09-15）

`gtfo.player` 现在有且只有一条列举路径：`MapPluginSession` 的注册在 `EntityResolvers`/`EntityInstanceResolvers`/`EntityObservers` 之外再登记 `EntityCandidates["gtfo.player"]`，来源读 `PlayerIdentityModule.Current.CurrentPlayers()`（仍然成立的引用）并按 `id` 序数排序交出；模块没挂上或已经释放时按名抛 `player-module-unavailable`，内核报 `entity-candidates-failed`，不返回空表。`PlayerSelector` 删掉了直接读 `PlayerIdentityModule.Current` 的旁路和只为旁路存在的 `Evaluate(module, parameters)` 测试入口，只经该步骤的 `RuntimeQuerySession.TryCandidates("gtfo.player")` 取集合，失败码原样抛出；`relation`（只答 `ally`）与 `empty`（只读不应用）的语义未变。

| 检查 | 命令 | 结果 |
| --- | --- | --- |
| 宿主、Map 与 Map 原生构建 | `dotnet build ForgeRuntime/ForgeRuntime.csproj`、`dotnet build ForgeMap/ForgeMap.csproj`、`dotnet build ForgeMap/Native/ForgeMap.Native.csproj`（同一 `--artifacts-path %TEMP%\provplayer\art-native`，`-p:ForgeRuntimeAssembly=…`） | 各 0 警告 0 错误，exit 0 |
| 选择器派发（MapSelectorDispatch） | `dotnet test ForgeMap/tests/MapSelectorDispatch -c Release` | 12 项通过（原 7 项 + 5 项候选来源用例），exit 0 |
| 原生替身（MapNativeAdapter，`Category!=Native`） | `dotnet test ForgeMap/tests/MapNativeAdapter -c Release --filter "Category!=Native"` | 本改动单独复跑 56 项全过（删 4 个旁路用例、加 3 个 kernel 候选用例）；并行任务把远征行加进同一份 provider 后共享工作树为 55/57，见「边界」 |
| 合同 / 身份 / 观察回归 | `dotnet test ForgeMap/tests/MapContracts`、`MapIdentity`、`MapObjectObservation`（后两个带 `FORGE_MAP_ADDRESS_VECTORS`） | 12 / 30 / 98 项通过（观察工程数量含并行任务新增用例），exit 0 |
| 原生读集布局（MapNativeLayout，`Category=Native`） | `dotnet test ForgeMap/tests/MapNativeLayout -c Release --filter "Category=Native"`（`FORGE_ARTIFACTS`/`FORGE_MAP_BEPINEX`/`FORGE_MAP_HOOK_SPEC` 绝对路径） | 1 项通过，exit 0 |
| 静态原生证据（MapNativeEvidence，`Category=Native`） | `dotnet test ForgeMap/tests/MapNativeEvidence -c Release --filter "Category=Native"`（加 `FORGE_GTFO_GAME`/`FORGE_GTFO_DUMP`） | 1 项通过，exit 0 |
| 框架与原生绑定 | `dotnet run --project ForgeRuntime/tests/Framework`（无参 482 项、`--fixtures` 网站夹具 535 项）、`dotnet run --project ForgeRuntime/tests/GameBindings`（无参 39 项、`--fixtures` 107 项） | 全部 exit 0 |
| 发行身份（只运行） | `pwsh -File Release/check-identity.ps1` | 83 项 83 通过，exit 0 |

新用例覆盖：候选集合按 `id` 序数交出（11 条生命使 `gtfo.player:10` 落在 `:1` 与 `:2` 之间，记录顺序与序数顺序不同）；世界清空且种类仍被 provider 暴露时回答空集（成功派发、动作收到空集合，不是失败）；模块没挂上时步骤以 `entity-candidates-failed` 拒绝且动作不运行；选择器的那次枚举计入每 tick 64 次查询的预算（把扫描步骤给满 64 次读，只有选择器也占了一次才会被拒）；`relation` 非 `ally` 被拒（步骤拒绝、动作不运行，原因里带 `relation-unsupported`）。`MapNativeLayout` 的白名单检查补上 `set_EntityCandidates`，玩家半边在这份注册上的四个所有权表缺一即失败。

反假绿实测两次（改完即改回）：① 去掉候选来源里的排序，`player_candidates_are_every_current_life_in_id_order` 报实际顺序 `gtfo.player:1..11`，失败；② 把选择器改回直读模块（`PlayerIdentityModule.Current.CurrentPlayers()`），候选有序、模块未挂上、预算三项分别失败，只有 `relation` 那项仍通过（它在读世界之前就拒绝）。

**边界**：全部是托管替身、静态程序集读取与只读 interop/dump 读取，没有启动游戏，没有安装或复制任何 profile 文件。`MapNativeLayout`/`MapNativeEvidence` 的输入是只读重排：`%TEMP%\provplayer\bep`（游戏根目录 `GameAssembly.dll` 副本，SHA-256 与规格锁一致；`interop`/`core` 是指向 `Forge-MapEditor-QA` profile 的目录联接），编译另用 `GTFO_BEPINEX_PATH` 指 profile。候选的排序按 `id` 字符串序数：正式游戏最多 4 名玩家时它与登记顺序一致，10 名以上才会不同（本批没有按数值段排序的需求）。原先由旁路入口直接驱动的「缺参数报 `missing-parameter`」两项断言随该入口一起删除：结构参数在执行前就由计划装载校验，测试不再从内核外部构造参数。

**并行任务**：同一工作树里另一个任务正在给同一个 provider 加「远征开始/结束」触发（`ModuleDefinition` 的能力与绑定行、`Native/ExpeditionHooks.cs`、Hook 表）。它在共享工作树里让 `MapNativeAdapter` 的 `module_provider_and_player_namespace_only`（逐项断言 capability 集合）与它自己的新用例当前为红，与本改动无关：把那些远征行和 Hook 从一份仓库副本里移出后，同一套 56 项候选/身份用例全过（`%TEMP%\provplayer\iso`，工作树本身未被改动）。

## level 挂载真正比对关卡身份（2026-09-15）

`level` 挂载此前在内核里对任何 reference 都返回 true：一个 Rundown 包里有多个关卡时，每关的行为会在所有关卡里运行。reference 现在是一个关卡的自身身份 `<rundown 块 persistentID>:<tier A-E>:<tier 内 0 基下标>`（例 `31:A:0`），由 ForgeMap 注册的匹配器与当前关卡比对。

- `MapLevelReference.cs`（新，纯托管，无游戏引用）是三段语法的唯一解析与拼写来源：十进制无前导零、tier 单个大写字母，`FromNative(uint, int, int)` 把游戏的 `eRundownTier`（1–5）与 0 基下标映射成同一拼写。解析失败即"不是一个关卡身份"，不做宽松回退。
- `Native/LevelIdentity.cs`（新，加入 `ForgeMap.Native.csproj` 的显式编译列表）读 `Globals.Global.RundownIdToLoad` 与 `RundownManager.GetActiveExpeditionData()` 的 `tier`/`expeditionIndex`，并把游戏自己的 `ActiveExpeditionUniqueKey` 写进同一行日志；读不到（未进关、成员读不出来、tier 超出 A–E）返回 null 而不是抛异常。
- `MapObjectModule` 新增可选的关卡身份读取器参数与 `level` 匹配器，用**无主体**（scope）形式注册：只拿挂载目标与当前世界的身份比对，因此世界/计时/脉冲这类不带主体的事件同样会被判到。当前世界的身份每个 world 只读一次，缓存由 `BeginWorld()` 丢掉。非法 reference 与读不到身份各自只报一次有界诊断，都返回 false——绝不退化成"匹配所有关卡"。
- `Native/MapPluginSession` 把 `() => LevelIdentity.Read(log)` 交给模块；没有这个读取器的测试替身不注册 `level`，计划会在装载期被 `attachment-kind` 拒收，不会静默不派发。
- 发行身份：`Release/release.json` 的 ForgeMap 增加 `attachmentKinds: ["level", "map-object"]`，`Release/check-identity.ps1` 逐包核对声明的 kind 与包内源码注册的匹配器（双向）。
- 证据：`evidence/level-identity.json` 记录五个原生成员的签名、reference 语法、游戏日志旁证（`Local_31_TierA_0`）与三项未取证；`evidence/map-hooks.json` 增补这五条读回登记，布局审计据此按成员全名核对实际读取集。

| 检查 | 命令 | 结果 |
| --- | --- | --- |
| 宿主与 Map 原生构建 | `dotnet build ForgeRuntime/ForgeRuntime.csproj`、`dotnet build ForgeMap/Native/ForgeMap.Native.csproj`（`-p:ForgeRuntimeAssembly=…`，隔离 `--artifacts-path`） | 各 0 警告 0 错误，exit 0 |
| level 挂载用例（MapObjectObservation，新 `MapLevelMountTests`） | `dotnet test ForgeMap/tests/MapObjectObservation -c Release --filter "Category!=Native"`（带 `FORGE_MAP_ADDRESS_VECTORS`） | 84 项通过（原 69 项 + 15 项 level 用例），exit 0 |
| 关卡身份读取替身（MapNativeAdapter） | `dotnet test ForgeMap/tests/MapNativeAdapter -c Release --filter "Category!=Native"` | 57 项通过（新增 tier 映射、Surface 与读不到三组断言），exit 0 |
| 合同 / 身份 / 派发回归 | `dotnet test ForgeMap/tests/MapContracts`、`MapIdentity`、`MapSelectorDispatch` | 12 / 30 / 7 项通过，exit 0 |
| 原生读集布局（MapNativeLayout） | `dotnet test ForgeMap/tests/MapNativeLayout -c Release --filter "Category=Native"`（`FORGE_ARTIFACTS`/`FORGE_MAP_BEPINEX`/`FORGE_MAP_HOOK_SPEC` 需绝对路径） | 1 项通过、审计内部检查全过，exit 0 |
| 静态原生证据（MapNativeEvidence） | `dotnet test ForgeMap/tests/MapNativeEvidence -c Release --filter "Category=Native"`（`FORGE_GTFO_GAME`/`FORGE_GTFO_DUMP`；`FORGE_MAP_BEPINEX` 需能看到 `GameAssembly.dll`，见复跑） | 1 项通过，exit 0 |

`MapLevelMountTests` 的 15 项覆盖：同一身份匹配、下标/层/块 id 各不相同的三种不匹配、九种非法拼写（旧拼写、小写 tier、前导零、负下标、缺段、多段、`F` 层、超出 uint 的块 id）各自只报一次且从不多匹配、身份读不到时匹配任何计划都为 false、以及一个 world 只读一次身份（`BeginWorld` 后重读）。MapContracts 与 MapSelectorDispatch 的计划夹具改成 `31:A:0` 并各自注册 level 作用域替身，其余断言不变。

**边界**：全部是托管替身与静态程序集/dump 读取，没有启动游戏，没有安装或复制任何 profile 文件。两关包的实际派发、`Global.RundownIdToLoad` 的整场恒定性、被禁用关卡下的 `expeditionIndex` 语义都未取证，清单在 `evidence/level-identity.json` 的 `unproven`。

## 门/终端 map-object 地址换成创作期可推导语法（2026-09-15）

门与终端的地址不再用运行期发号：门 `door/<dimension>/<layer>/<zone>/security`，坐标是**被守卫区域**，key 是区域入口闸的固定词元；终端 `terminal/<dimension>/<layer>/<zone>/<placementIndex>`，下标是终端在区域 `LG_Zone.TerminalsSpawnedInZone` 中的 0 起位置。三段坐标是区域自己的 `LG_Zone.m_dimensionIndex` / `m_layer.m_type` / `LocalIndex`，十进制无前导零，任一段读不到就不产生地址（不用 0 或 `?` 冒充）。旧的 `MapperDataID` / `SyncID` 地址路径与 `MapObjectReference.Unknown` 一并删除，不做双轨或兼容解析；`SyncID` 只保留为观测事实，用于把原生回调带的 id 换回终端实例。网站导出与模组读回共用网站仓的 `Tests/Viewer/fixtures/map-object-address.json`，模组测试经 `FORGE_MAP_ADDRESS_VECTORS` 读取，不复制向量。

`Native/ZoneIndex.cs`（新）是唯一的区域查找表：按 world epoch 从 `LG_LevelBuilder.Current.m_currentFloor.allZones` 重建 `SpawnedDoor → LG_Zone`、`LG_Zone → 终端列表`、`SyncID → 终端`，门读 `zone.m_sourceGate.SpawnedDoor`，终端的区域与下标同出它所在的那张列表（两者不会互相矛盾）。不可寻址的对象每类只报一次原因、不发布任何事实：不是任何区域入口闸的门（弱门/节点门/装饰门/出生区域）、bulkhead 层转换门、坐标读不到的区域里的门与终端、区域数据块声明了 `SpecificTerminalSpawnDatas` 的终端、区域列表里没有它的终端（反应堆与任务终端）。

Hook 规格文件由 `map5a-player-hooks.json` 改名为 `evidence/map-hooks.json`（按用途命名，文件名不再绑定历史批次），并补齐门/终端的读回登记：规格里的 map-object 读集与原生程序集实际读取逐项相等。门/终端自己的成员登记、地址规则与三项游戏内确认在 `evidence/door-terminal-hooks.json`。

| 检查 | 命令 | 结果 |
| --- | --- | --- |
| 宿主与 Map 原生构建 | `dotnet build ForgeRuntime/ForgeRuntime.csproj`、`dotnet build ForgeMap/Native/ForgeMap.Native.csproj`（`--artifacts-path %TEMP%\mapaddrm\verify`） | 各 0 警告 0 错误，exit 0 |
| 地址语法、发布与共享向量（MapObjectObservation） | `dotnet test ForgeMap/tests/MapObjectObservation -c Release --filter "Category!=Native"`（带 `FORGE_MAP_ADDRESS_VECTORS`） | 69 项通过，exit 0 |
| 原生替身与 `ZoneIndex`（MapNativeAdapter） | `dotnet test ForgeMap/tests/MapNativeAdapter -c Release --filter "Category!=Native"` | 56 项通过（新增“拒绝原因每类只报一次”用例），exit 0 |
| 合同 / 身份 / 派发回归 | `dotnet test ForgeMap/tests/MapContracts`、`MapIdentity`、`MapSelectorDispatch`（`--filter "Category!=Native"`） | 12 / 30 / 7 项通过，exit 0 |
| 原生读集布局（MapNativeLayout） | `dotnet test ForgeMap/tests/MapNativeLayout -c Release --filter "Category=Native"`（`FORGE_ARTIFACTS`/`FORGE_MAP_BEPINEX`/`FORGE_MAP_HOOK_SPEC`） | 1 项通过，审计内部 **101 条检查**全过，exit 0 |
| 静态原生证据（MapNativeEvidence） | `dotnet test ForgeMap/tests/MapNativeEvidence -c Release --filter "Category=Native"`（加 `FORGE_GTFO_GAME`/`FORGE_GTFO_DUMP`） | 1 项通过，exit 0 |

反假绿实测：① 不提供 `FORGE_MAP_ADDRESS_VECTORS` 时向量用例 **1 项失败**并写明需要该路径（不是跳过）；② 把向量里一条 `expected` 故意改成另一个区域，用例报 `Expected: "door/0/0/4/security" / Actual: "door/0/0/3/security"` 而失败；③ 布局审计在补齐规格前实测红：`native.exact-map-object-reads` 报实际读取里 `GateKeyItem` 的键名/键 id、终端状态、`SyncID`、交互玩家四个成员未登记，而规格里的 `SpawnNode` / `m_zone` 已不再被读取（该审计确实在比对真实读取集，不是空跑）。

**边界**：全部是托管替身与静态程序集/dump 读取，没有启动游戏，没有安装或复制任何 profile 文件。三项语义（`m_sourceGate.SpawnedDoor` 就是区域入口闸、`TerminalsSpawnedInZone` 顺序等于 `TerminalPlacements` 顺序、首次回调时 `allZones` 已填满）仍只有静态证据，游戏内确认清单在 `evidence/door-terminal-hooks.json` 的 `inGameVerificationPlan`。

## 布局审计：Hook 规格 5 条与只读查询白名单（2026-09-15）

`native.exact-hook-set` 与 `native.read-only-game-access` 两项转绿。Hook 规格 `evidence/map-hooks.json` 从 2 条玩家读回扩到原生程序集里实际安装的 5 条（加 `DoorStateReadback`、`TerminalStateReadback`、`TerminalCommandReadback`），每条自己声明 postfix 形态：`instanceParameter`（取不取 `__instance`，取了就必须是补丁目标的类型）、`arguments`（形参名与精确 interop 类型）、`reads`（postfix 体真正 ldarg/ldarga 了哪些形参，空表＝从不读参数）、`guard`（`Guard` 或 `GuardMapObjects`）；字段含义写在规格的 `postfixVocabulary` 里。`MapNativeLayout` 逐条按声明检查 `hook.<名>.postfix-parameters`、`.parameter-reads`、`.guarded-by-session`，加上 `.declared-target`、`.unique-game-method`（新比对规格声明的 `isStatic`）、`.static-postfix-only`、`.priority-last`，没有跳过分支，也不按数量放行。把声明的形态改坏（`DoorStateReadback` 去掉 `__instance`、`TerminalCommandReadback` 的 `reads` 置空）会分别报出这两条检查，已实测。

只读查询白名单按成员全名加 `LevelGeneration.LG_ComputerTerminalManager::GetTerminal`（dump.cs 683348：`public static LG_ComputerTerminal GetTerminal(uint id)`，按 SyncID 查管理器自己的注册表，不写管理器字段），算子与转换拼写仍按短名放行，两组分开列出（本批之后终端命令入口改为按 SyncID 查关卡自己的区域表，这一项白名单随之删除，map-object 读集改为与规格逐项相等，见上一节）。`MapNativeEvidence` 的钩子签名检查改为比对规格声明的 `isStatic`：终端命令入口是 `public static`，不再一律要求非 static。

去编号：`objective-hooks.json`、`encounter-wave-hooks.json` 里按旧决定编号索引的引用改成直接陈述的 `rules`，行内对旧编号的引用改写为规则本身；`VALIDATION.md`、`tests/MapContracts/TestWorld.cs` 与 `tests/MapSelectorDispatch/MapSelectorDispatchTests.cs` 注释里的决定编号一并去掉（被删的编号引用字段没有任何代码读取，证据文件也没有外部哈希锁）。

| 检查 | 命令 | 结果 |
| --- | --- | --- |
| 宿主与 Map 原生构建 | `dotnet build ForgeRuntime/ForgeRuntime.csproj`、`dotnet build ForgeMap/Native/ForgeMap.Native.csproj`（`--artifacts-path %TEMP%\maplayout\final`） | 各 0 警告 0 错误 |
| 原生读集布局（MapNativeLayout） | `dotnet test ForgeMap/tests/MapNativeLayout -c Release --filter "Category=Native"`（`FORGE_ARTIFACTS`/`FORGE_MAP_BEPINEX`/`FORGE_MAP_HOOK_SPEC`） | 1 项通过，审计内部 73 条检查全过 |
| 静态原生证据（MapNativeEvidence） | `dotnet test ForgeMap/tests/MapNativeEvidence -c Release --filter "Category=Native"`（`FORGE_GTFO_GAME`/`FORGE_GTFO_DUMP`） | 1 项通过 |
| 合同与选择器派发回归 | `dotnet test ForgeMap/tests/MapContracts`、`MapSelectorDispatch`（`--filter "Category!=Native"`） | 12 项、7 项通过，数量不变 |

**边界**：`MapNativeEvidence` 的输入目录是只读重排：QA profile 的 `BepInEx` 没有 `GameAssembly.dll` 副本，本次用 `%TEMP%\maplayout\evidence-input\BepInEx`（游戏根目录 `GameAssembly.dll` 的副本，SHA-256 与规格锁一致；`interop` 是指向 QA profile 的目录联接）跑一次。没有启动游戏，没有改动或安装任何 profile 文件；两项审计只读编译产物、interop 元数据与 dump.cs，运行时行为、主客机与回调顺序仍未核验。

## 测试工程迁移到 xUnit（2026-09-15）

**实现，未测**：本节只描述迁移后的结构与待跑命令，未运行任何测试；`dotnet build` 已逐个执行，全部退出码 0。

五个测试工程（`tests/MapContracts`、`MapIdentity`、`MapNativeAdapter`、`MapNativeEvidence`、`MapNativeLayout`）从自写 `Program.cs` 计数器改为 xUnit 测试工程：`Microsoft.NET.Test.Sdk` 17.14.1、`xunit` 2.9.3、`xunit.runner.visualstudio` 3.1.5，目标框架 `net6.0` → `net8.0`（Test.Sdk 17.14.1 明确不支持 net6.0）。旧 `Program.cs`、自写 ExitCode、`PASS`/JSON 汇总输出与 `--report` JSON 报告全部删除，断言逐条保留为 `[Fact]`（MapIdentity 的源码锁 5 行表保留为 `[Theory]`/`MemberData`），未放宽或删除任何检查。测试类之间共享进程级静态状态（Harmony patch、`Host.Runtime`、`MapPlugin.Session`、`SNet.IsMaster`、`PlayerManager.PlayerAgentsInLevel`），因此每个程序集都加了 `AssemblyAttributes.cs` 的 `[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]`。

命令行参数改为环境变量：

| 旧参数 | 现在 |
| --- | --- |
| `MapNativeEvidence <BepInEx> <game> <dump> <spec> <report>` | `FORGE_MAP_BEPINEX`（回退 `GTFO_BEPINEX_PATH`）、`FORGE_GTFO_GAME`、`FORGE_GTFO_DUMP`、`FORGE_MAP_HOOK_SPEC`（默认 `ForgeMap/evidence/map-hooks.json`）；不再写 JSON 报告 |
| `MapNativeLayout <BepInEx> <sdk> <host> <map> <map.native> <spec> <report>` | `FORGE_ARTIFACTS` + 制品相对路径 `bin/ForgeRuntime.Framework/release/ForgeRuntime.Framework.dll`、`bin/ForgeRuntime/release/ForgeRuntime.dll`、`bin/ForgeMap/release/ForgeMap.dll`、`bin/ForgeMap.Native/release/ForgeMap.Native.dll`；`FORGE_MAP_BEPINEX`、`FORGE_MAP_HOOK_SPEC` 同上 |

`MapNativeEvidence` 与 `MapNativeLayout` 需要游戏程序集，标记为 `[Trait("Category", "Native")]`，默认用 `--filter "Category!=Native"` 排除。`MapIdentity` 保留 `<AssemblyName>ForgeMap.Identity.Tests</AssemblyName>`，`tools/run_identity_checks.py` 的 TRX/日志标签不变。

迁移后用例数（原用例数 → 新 `[Fact]`/`[Theory]` 用例数，`dotnet build` 均退出码 0）：

| 工程 | 原 | 新 |
| --- | --- | --- |
| `tests/MapContracts` | 12 个场景 | 12 `[Fact]` |
| `tests/MapIdentity` | 72 条场景内检查 + 1 个源码锁 5 行循环 | 25 `[Fact]` + 1 `[Theory]`×5（新增 `every_native_identity_spec_has_tagged_managed_checks`） |
| `tests/MapNativeAdapter` | 42 个用例 | 43 `[Fact]`（新增 `plugin_suspended_host` 覆盖宿主挂起分支） |
| `tests/MapNativeEvidence` | 15 处静态 `Check(` + 程序集与调用边循环 | 1 `[Fact]`，内部断言全部检查项通过且总数 ≥ 15 |
| `tests/MapNativeLayout` | 30 处静态 `Check(` + Hook/读回循环 | 1 `[Fact]`，内部断言全部检查项通过且总数 ≥ 30 |

`MapNativeAdapter` 的 `ForgeRuntime.Plugin` 替身补上生产代码新增的 `IsSuspended`/`SuspensionCode`，否则编译不过；生产 `Native/**` 未改。`tools/verify_resource_adapter_fixtures.py` 逻辑未改（它不运行这些工程）。

待跑命令（**尚未执行**，需在设置好 `GTFO_BEPINEX_PATH`、`FORGE_ARTIFACTS` 后由人工运行）：

```powershell
$a = "$env:TEMP\dsh-xunitwm"
$env:GTFO_BEPINEX_PATH = "$env:APPDATA\r2modmanPlus-local\GTFO\profiles\Forge-MapEditor-QA\BepInEx"
dotnet test ForgeMap/tests/MapContracts/MapContracts.csproj -c Release --artifacts-path $a --filter "Category!=Native"
dotnet test ForgeMap/tests/MapIdentity/MapIdentity.csproj -c Release --artifacts-path $a --filter "Category!=Native"
dotnet test ForgeMap/tests/MapNativeAdapter/MapNativeAdapter.csproj -c Release --artifacts-path $a --filter "Category!=Native"
$env:FORGE_ARTIFACTS = $a; $env:FORGE_GTFO_GAME = "<GTFO install>"; $env:FORGE_GTFO_DUMP = "<dump.cs>"
dotnet test ForgeMap/tests/MapNativeEvidence/MapNativeEvidence.csproj -c Release --artifacts-path $a --filter "Category=Native"
dotnet test ForgeMap/tests/MapNativeLayout/MapNativeLayout.csproj -c Release --artifacts-path $a --filter "Category=Native"
python ForgeMap/tools/run_identity_checks.py --out ForgeMap/evidence/identity-checks-<新目录>
```

`tools/run_identity_checks.py` 已改为对每个工程调用一次 `dotnet test` 并解析 TRX（命令数由 6 变 3，`passed` 同时要求每个工程有用例且全部通过）；已做 `python -m py_compile` 语法检查，未实际运行。

**上次更新：2026-09-14**（2026-09-13 内容合并自原 MAP1-DELIVERY 与 MAP1-IDENTITY-CONTINUATION 两份交接记录）。

## G7 精确拼接生成取消：G0–G6 静态检查与计划测试删除（2026-09-14）

用户决定不做 G7 精确拼接生成。I-MAP-PLAN 拼装计划、原版房间描述符与模组侧 G0–G6 静态检查全部取消：`AssemblyPlanDiscovery.cs`、`AssemblyPlanReader.cs`、`AssemblyPlanContracts.cs`、`AssemblyPlanChecks.cs`、`Native/MapPlanDiagnostics.cs` 与 `tests/MapAssemblyPlan`、`tests/MapPlanDiscovery`、`tools/verify_assembly_plan_fixtures.py` 删除，插件里的计划发现调用与 `map.plan-*` 诊断接线一并移除。LGTuner 长期保留（区域内房间选择顺序与额外环境资源加载），不再计划被 ForgeMap 替代；新方向（编辑器拼房间 → 游戏内登记为 geomorph → ComplexResourceSet / `CustomGeomorph` / LGTuner 使用）见 [GENERATION-SPEC.md](GENERATION-SPEC.md) 的现行方向一节。

**本文档下方所有 MAP2 计划发现与 G0–G6 拼装计划静态检查记录仅作历史**：对应的检查器、测试与命令已经不存在，不要按旧记录复跑；它们描述的是当时确实跑过的结果，不代表现行方向。本次删除只做构建检查，测试按决定放到最终阶段统一跑。

## NativeEvidence 冻结输入改用 QA profile interop（2026-09-14）

按框架 §6 U-MAP-MOD 与合同"编译引用与冻结输入要指定同一个明确来源"，把 `tests/MapNativeEvidence` 的冻结输入统一到唯一来源：`%APPDATA%\r2modmanPlus-local\GTFO\profiles\Forge-MapEditor-QA\BepInEx\interop`（构建时的 `GTFOBepInExPath` 也指向同一 profile）。此前 `evidence/map-hooks.json`（当时的文件名）锁的是同机 `Temp` profile 的旧副本，而 `Forge-MapEditor-QA` 的 interop 于 2026-09-09 重新生成，两个程序集的 `hash.*` 与 `mvid.*` 共 4 项因此一直失败。证据等级不变：**metadata-and-static-native-call-graph-only**，没有启动游戏、没有安装或复制任何 profile 文件。

改动只有该文件 2 条 `assemblies` 记录的 `sha256` 与 `mvid`；**签名、`isVirtual`、`rva`、`readbacks` 与 7 条 `callEdges` 一字未改**，`buildId`、`gameAssemblySha256`、`dumpSha256`、`codeSection` 也未变（dump 仍是本机 build 20403457 的那份，`BF657C0E…`；`GameAssembly.dll` 仍是 `C6A5C3CD…`）。[README](README.md#复跑) 的 MAP5a 复跑段把 `$bep` 显式写成 QA profile。

换锁前的两项证明（临时 Cecil 工具，只在 `$env:TEMP` 下运行，仓库零改动）：

| 证明 | 结果 |
| --- | --- |
| Modules-ASM 逐类型/字段/属性/方法指纹 | Temp 与 QA 副本各 4283 类型、57776 字段、27357 属性、80710 方法（170127 行），除 MVID 外逐行相同 |
| SNet_ASM 逐类型/字段/属性/方法指纹 | 各 301 类型、2620 字段、805 属性、3143 方法（6870 行），除 MVID 外逐行相同 |
| 被锁成员在同 build dump 中 | 2 个 Hook RVA 各声明一次、dump 内 entries=1；7 条调用边的 8 个端点 RVA entries 全为 1 且跨度 ≤0x4000 |

| 程序集 | sha256 旧 → 新 | mvid 旧 → 新 |
| --- | --- | --- |
| Modules-ASM.dll | `A31AF38F…7943` → `E499B9C0…6D63` | `2875668a-…c926` → `6d066008-28db-4edf-9c0e-df9db732560d` |
| SNet_ASM.dll | `99175A1E…60B2` → `6DAD1168…9B2C` | `63a2b9c8-…48c5` → `143acd09-f561-44d0-9455-a78706262fb5` |

| 检查 | 命令 | 结果 |
| --- | --- | --- |
| `tests/MapNativeEvidence` 构建 | `dotnet build ForgeMap/tests/MapNativeEvidence/MapNativeEvidence.csproj -c Release --artifacts-path <临时目录> -p:GTFOBepInExPath=<QA BepInEx>` | 退出码 0，0 警告 0 错误 |
| `tests/MapNativeEvidence` 运行 | 见 [README](README.md#复跑) | QA interop 输入：退出码 0，`PASS 29/29` |
| `tests/MapNativeLayout` 运行 | 同一证据文件与 QA interop | 退出码 0，`PASS 39/39` |

本批续做（2026-09-14），把最后三份仍锁 `Temp` 的冻结输入也统一到同一来源：`evidence/map1-2026-09-12/native-api.json`、`evidence/map1-2026-09-12/native-api-recheck.json`、`evidence/map2-scope-2026-09-13/generation-api.json` 的 5 个 `interop/*.dll` 身份换成 `Forge-MapEditor-QA` 副本的 sha256 与 mvid。改动只有这 30 行（3 个文件 × 5 个程序集 × 2 个字段）：4 条 `core/*.dll` 两个 profile 字节相同、未动；`types`、`targetsSha256`、`game.*` 未动；`evidence/map1-2026-09-12/native-regions-recheck.json` 锁的是 `GameAssembly.dll` 的区段、与 profile 无关，未动；`*-result.json`、`*.log`、`solution-build-result.json` 等历史运行记录未动。`tools/verify_native_api.py` 从命令行取 capture 路径、不硬编码 profile，因此没有可改之处；复采命令的 profile 路径同步改成 QA：本文档"复跑"一节的 `$bep` 与 [GENERATION-SPEC.md](GENERATION-SPEC.md) 第 5 节。换锁后按"复跑"一节从 QA profile 复采比对：MAP1 targets 采集 32 类型 / 110 方法、`missing: 0`、build 20403457，`native-api.json` 与 `native-api-recheck.json` 各自 `verify_native_api.py --self-test` 退出码 0、`matched: true`、0 差异、自检 17 项；MAP2 targets 采集 27 类型 / 59 方法、`missing: 0`，`generation-api.json` 同样退出码 0、`matched: true`、0 差异；`verify_native_regions.py` 退出码 0、`matched: true`。

| 程序集 | sha256 旧 → 新 | mvid 旧 → 新 |
| --- | --- | --- |
| Modules-ASM.dll | `A31AF38F…7943` → `E499B9C0…6D63` | `2875668a-…c926` → `6d066008-28db-4edf-9c0e-df9db732560d` |
| GameData-ASM.dll | `3A74E665…CC7B` → `DEE52362…7106` | `7a8e1e7b-…5f3d` → `10c226b4-0ffe-41dc-8e66-7c049afdb4d9` |
| SNet_ASM.dll | `99175A1E…60B2` → `6DAD1168…9B2C` | `63a2b9c8-…48c5` → `143acd09-f561-44d0-9455-a78706262fb5` |
| UnityEngine.CoreModule.dll | `13DDFA5A…95A5` → `CB14FF81…C06C` | `e399b830-…6fe3` → `4f2d5da5-2462-491d-8c3b-a0b262e0f576` |
| UnityEngine.AIModule.dll | `46EC77EA…F693` → `B6952B1D…BF59` | `dc1f21c6-…c562` → `a9a309aa-fe5d-49fa-8dc1-655bdf053814` |

换锁前的签名等价证明（`$env:TEMP` 下的临时 Cecil 工具，只在临时目录运行，仓库零改动）：

| 证明 | 结果 |
| --- | --- |
| 5 个 interop 程序集逐类型/字段/属性/方法/参数/事件投影 | Temp 与 QA 副本的投影文件逐字节相同；投影覆盖参数名、`isOut`、`isOptional`、常量、属性读写访问器与枚举字面量，MVID 不在投影内。规模：Modules-ASM 4283 类型 / 57776 字段 / 27357 属性 / 80710 方法 / 46256 参数；GameData-ASM 102 / 8179 / 8078 / 16459 / 8179；SNet_ASM 301 / 2620 / 805 / 3143 / 2046；UnityEngine.CoreModule 2815 / 8356 / 1854 / 13191 / 18461；UnityEngine.AIModule 142 / 419 / 117 / 678 / 980 |
| 两个 profile 的 interop 同源 | 两侧 `BepInEx/interop/assembly-hash.txt` 同为 `565871abd714937729d0e74520563bec`；`GameAssembly.dll` 仍是锁定的 `C6A5C3CD…BF55`（app 493520 / build 20403457） |
| Modules-ASM、SNet_ASM 新锁值与已统一的 MAP5a 锁 | 与 `evidence/map-hooks.json` 的 sha256/MVID 一致 |

## MAP2 原生发现（2026-09-14）

按框架 §6 U-MAP-MOD 未完成项"MAP2 原生发现"与 §3.2 I-MAP-PLAN 新增：游戏无关的 `AssemblyPlanDiscovery.cs` 发现 `BepInEx/plugins/*/forge/maps/` 并只跑 G0–G6 静态检查；原生 `Native/MapPlanDiagnostics.cs` 在插件 `Load` 注册身份后调用一次，每个计划一条有界（512 字符）诊断行。**只读、只诊断**：不生成、不改游戏状态、不注册 provider、不读网络。证据等级：**本地验证（托管发现 + 合成夹具 + 替身接线）**；没有启动游戏、没有加载 bundle、没有安装到任何 profile，通过静态检查不等于生成成功。

下表命令在**工作区**运行（当时工作区另有 ForgeRuntime 在制改动），构建一律带会话临时目录的隔离 `--artifacts-path`。提交前在 HEAD `30a09d7` 的独立 worktree 只放入本节文件复验：宿主、`ForgeMap.csproj`、`ForgeMap.Native.csproj` 与三个测试工程 0 警告 0 错误；`MapPlanDiscovery` 退出码 0、`failures: []`；`MapAssemblyPlan --self-check` 退出码 0、`failures: []`；`MapNativeAdapter` `PASS 44/44`（MapNativeLayout、Identity、NativeEvidence 未在 worktree 复跑）。

| 检查 | 命令 | 结果 |
| --- | --- | --- |
| `ForgeMap.csproj` 构建 | `dotnet build ForgeMap/ForgeMap.csproj -c Release` | 退出码 0，0 警告 0 错误 |
| `Native/ForgeMap.Native.csproj` 构建 | 先构建宿主，再 `dotnet build ForgeMap/Native/ForgeMap.Native.csproj -c Release -p:ForgeRuntimeAssembly=…` | 退出码 0，0 警告 0 错误；新增类型未触发依赖方向或只读读取检查 |
| 新测试 `tests/MapPlanDiscovery` | 构建后运行 `MapPlanDiscovery.dll` | 退出码 0，18/18（无目录、空目录、无 `forge/maps/` 的目录、缺描述符、多包、只有描述符、合法计划与 blockers、非计划文件忽略、缺/错 planId、不可解析文档、seed 越界、非规范 rotation、descriptor-lock 不符、描述符文档不可解析、重复 level layout 的 ordinal 顺序、可重复性） |
| G0–G6 变异自检（包级改走生产发现） | `MapAssemblyPlan.dll --self-check` | 退出码 0，48/48（45 条计划变异 + 3 条包级），`failures: []` |
| 变异语料两侧交叉 | `MapAssemblyPlan.dll --self-check --emit <临时目录>` 后分别运行 `MapAssemblyPlan.dll --fixtures <同上>` 与 `python ForgeMap/tools/verify_assembly_plan_fixtures.py --fixtures <同上>` | 两侧退出码均 0，`manifestVerified: true`；合法 1/1、非法 44/44、包级 3/3；包级用例现在走 `AssemblyPlanDiscovery`，与 Python `check_package` 同码 |
| `tests/MapNativeAdapter` | 构建后运行 | 退出码 0，`PASS 44/44`（41 → 44：无包时不写发现日志、每个计划一行且不改注册面、包级拒绝与 512 字符上界） |
| `tests/MapNativeLayout` | 构建后运行（输入为重建后的 `ForgeMap.dll` 与 `ForgeMap.Native.dll`） | 退出码 0，`PASS 39/39`；插件 Off 门与 `MapPluginSession::Start` 的次序断言、Hook 集合、只读调用检查仍通过 |
| MapIdentity 回归 | Release 构建后运行 `ForgeMap.Identity.Tests.dll` | 退出码 0，134 项断言，`nativeScenariosExecuted: false`（未改） |
| `tests/MapNativeEvidence` | 构建后运行（游戏 build 20403457） | 退出码 1，25/29：`hash/mvid.Modules-ASM.dll`、`hash/mvid.SNet_ASM.dll` 与本机 profile 的 interop 不一致；与 2026-09-14 上一批相同的环境差异，本次未改 Hook 或读回 |

发现规则与合同未写明的点（本次采用的解释，需裁决方复核）：

- 发现根是 `BepInEx/plugins/` 的一级子目录，只检查 `<dir>/forge/maps/` 是否存在；没有这样的目录时静默跳过（沿用 I-PACK 的计划发现口径：不打开文件、不报错、不写日志）。合同只写了"恰好一个"与"多于一个全部拒绝"，**零个的语义是本次补齐的**。
- 包内有 `forge/maps/` 但缺 `rooms.descriptors.json` 时记 `assembly.package-layout`（路径为 `plugins/<dir>/forge/maps`）。合同把 `assembly.package-layout` 只写在"多于一个目录"上；这个码与路径取自 `tools/verify_assembly_plan_fixtures.py` 与 `tests/MapAssemblyPlan` 原有的同一处理。描述符文档存在但不可读或不可解析时走委派码 `descriptor.schema`（`$descriptors`）。
- 计划文件不是合法 JSON 或读不到时记 `assembly.schema`（`$`）：合同的 34 码里没有"文件读不到"这一条，取形状阶段的码。文档能解析但没有 `planId`、或文件名不等于 `planId + ".assembly.json"` 时记 `assembly.plan-file`（与两个检查器的宽松 planId 提取一致）。
- 每个计划文件单独出结果，一份被拒不影响同包其他计划（沿用 I-PACK"每个文件单独出结果"的口径）；文件身份两条码（`assembly.plan-file`、`assembly.duplicate-level-layout`）在描述符与计划文档校验之前判定，`assembly.duplicate-level-layout` 记在 ordinal 靠后的那个文件上，`assembly.package-layout` 是包级拒绝且不再出计划行。同一份计划被拒时它的 `levelLayoutId` 仍占用登记，后续同 id 文件照样报重复。
- 合同没有为 `forge/maps/` 写 I-PACK 里的链接/越界检查（`plan-path`）与单文件/合计上限（`json-size`、`plan-budget`），本次**没有实现**，等裁决方决定是否按 I-PACK 逐条补齐。
- 诊断日志暂用 BepInEx 日志的 `map.plan-accepted` / `map.plan-rejected` / `map.package-rejected` 三行（行内 `path` 为 BepInEx 相对路径），**不是** `forge.log` 记录：`plan.loaded` / `plan.rejected` 归 Runtime sink。Map 的 cfg `Logging.Level` 已在 `Native/Plugin.cs` 绑定并随 `MapPluginSession.Start` 注册，但这三行诊断仍未改走 Runtime sink。这三个码需要按 §2.5 补进 §3.2 I-DIAG 的包内诊断码表。

**边界**：发现流程只读夹具目录并跑已有静态检查；18 项与 44 项都是合成数据与替身，不能替代实机、主客机、导航或生成验证。没有真实 Geo 包、没有网站夹具（`Tests/Forge/fixtures/map-assembly/` 仍缺 `MANIFEST.json` 与 `cases.json`），也没有 G7 生成。

## G0–G6 拼装计划静态检查（2026-09-14）

按框架 §3.2 I-MAP-PLAN（r24 关闭 Q-005）新增静态检查：`tools/verify_assembly_plan_fixtures.py`（夹具仲裁检查器，描述符部分 import `verify_resource_adapter_fixtures.document(..., '$descriptors')`）、`ResourceDescriptorReader.cs`（GENERATION-SPEC §3.1 的 C# 严格解析）、`AssemblyPlanContracts.cs` / `AssemblyPlanReader.cs` / `AssemblyPlanChecks.cs`（G0–G6 与 blockers）、`tests/MapAssemblyPlan`（夹具比对与变异自检）。证据等级：**fixture-schema-only / 静态检查**；没有加载 bundle、没有调用原生、没有运行游戏，通过静态检查不等于生成成功（v1 每份合法计划恒带 `dimension-bounds-unknown`）。

全部构建使用会话临时目录的隔离 `--artifacts-path`。

| 检查 | 命令 | 结果 |
| --- | --- | --- |
| 描述符检查器（重构后） | `python ForgeMap/tools/verify_resource_adapter_fixtures.py` | 退出码 0；合法 3/3、非法 21/21；与重构前对比脚本比对 24 条既有夹具：23 条错误码与文本逐字相同，1 条把 `sharedBytes[0]…` 补成 `$.sharedBytes[0]…`（本次把根路径前缀参数化，码未变） |
| G0–G6 变异自检 | `dotnet ForgeMap/tests/MapAssemblyPlan/MapAssemblyPlan.csproj` 后运行 `MapAssemblyPlan.dll --self-check` | 构建 0 警告 0 错误；退出码 0；48/48（45 条计划单点变异 + 3 条包级） |
| Python/C# 交叉一致 | `MapAssemblyPlan.dll --self-check --emit <临时目录>` 后分别运行 `MapAssemblyPlan.dll --fixtures <临时目录>` 与 `python ForgeMap/tools/verify_assembly_plan_fixtures.py --fixtures <临时目录>` | 两侧退出码均 0，`manifestVerified: true`；同一 48 条语料（98 个文件）：合法 1/1、非法 44/44、包级 3/3，错误码与字段路径逐字相同 |
| 网站夹具交叉运行 | `python ForgeMap/tools/verify_assembly_plan_fixtures.py --fixtures ../Infini-GTFO-Model-Site/Tests/Forge/fixtures/map-assembly` | **未运行**：网站夹具目录当前只有 `generate.ts`，没有 `MANIFEST.json` 与 `cases.json`；退出码 1，输出 `must contain MANIFEST.json and cases.json`（夹具不完整按失败报告，不抛栈）。同一检查器已用 `--emit` 生成的合成语料跑通（上一行） |
| MapIdentity 回归 | Release 构建后运行 `ForgeMap.Identity.Tests.dll` | 退出码 0，134 项断言，`nativeScenariosExecuted: false`（未改） |
| MapContracts 回归 | Release 构建后运行 `MapContracts.dll` | 退出码 -532462766：未处理 `RuntimeContractException: Unsupported plan version`（并行任务正在改 `ForgeRuntime/Framework/RuntimePlan.cs`；本次改动前同样失败） |
| 架构回归 | Release 构建后运行 `Architecture.dll` | 退出码 -532462766：`FAIL: Weapon still owns its two observed wield triggers`（并行任务正在改 `ForgeTrigger`/`ForgeWeapon` 与该测试文件；本次改动前同样失败） |
| `Native/ForgeMap.Native.csproj` 构建 | 宿主与原生构建 | 退出码 0，0 警告 0 错误 |
| `tests/MapNativeAdapter` | 构建后运行 | 退出码 0，`PASS 41/41` |
| `tests/MapNativeLayout` | 构建后运行（输入包含重建后的 `ForgeMap.dll`） | 退出码 0，`PASS 39/39`；新增的静态类型没有触发依赖方向或只读读取检查 |
| `tests/MapNativeEvidence` | 构建后运行 | 退出码 1，25/29：`hash/mvid.Modules-ASM.dll` 与 `hash/mvid.SNet_ASM.dll` 与本机 `Forge-MapEditor-QA` profile 的 interop 不一致（游戏 build 20403457、`native.hash`、`dump.hash` 均通过）；环境差异，本次未改 Native 或证据文件 |

变异自检覆盖框架 §3.2 拒绝顺序的每一条：#1 用三个 `descriptor.*` 变异（`revision-mismatch`、`evidence-escalation`，以及描述符文档本身不是 JSON 时归 `descriptor.schema`，委派路径 `$descriptors`）、#2–#34 各一条、包级 3 码各一条，另有基线合法用例（blockers `colliders`、`dimension-bounds-unknown`、`navigation`、`occlusion`）。**#16 `assembly.locator-unsupported` 在当前顺序下不可达**：检查 10 已要求每个声明的 zone 是 dimension 0 / layer 0，检查 15 已要求 locator 被某个 zone 声明，因此不可能有"已声明但 dimension/layer 非 0"的 locator。自检用一条变异钉住该顺序（改 zone 与 locator 的 layer 后报的是 `assembly.zone`），并在最终报告里请裁决方确认 #16 是否保留。

## MAP5a — gtfo.player 原生实例解析（2026-09-13）

`PlayerIdentityModule` 同时登记 `EntityInstanceResolvers["gtfo.player"]`，Weapon 由此经 SDK 的 `ResolveEntityInstance` 取得装备 owner，接口见 [Runtime 验证记录](../ForgeRuntime/VALIDATION.md#原生实例解析2026-09-13)。证据等级不变：**implementation-only + 本机静态原生证据**；没有新的原生读取签名，证据文件未改。

| 套件 | 退出码 | 输出结尾 |
| --- | --- | --- |
| `Native/ForgeMap.Native.csproj` 构建 | 0 | 0 警告 0 错误 |
| `tests/MapNativeAdapter` | 0 | `PASS 41/41 Map native player identity cases; no GTFO execution.` |
| `tests/MapNativeLayout` | 0 | `PASS 39/39 Map native layout checks; no GTFO execution.` |
| `tests/MapNativeEvidence` | 0 | `PASS 29/29 Map static native evidence checks; game execution NOT tested.` |

MapNativeAdapter 从 35 增加到 41：
- `instance.sdk-lookup-returns-recorded-life`：经 SDK 查到的就是已登记的引用。
- `instance.lookup-never-allocates-a-life`：未登记的玩家返回 null，不分配编号或 life。
- `instance.observe-gate-and-native-type`：注册期、非主机、已销毁的玩家，以及 agent、`SNet_IPlayerAgent`、字符串、数字和同 Lookup 的另一个 `SNet_Player` 都返回 null。
- `instance.lookup-rereads-native-links`：agent 互链、Lookup 或 owner 变化后返回 null，且不改表。
- `instance.stop-and-wrong-thread`：模块与内核两条路径在错线程都抛 `wrong-thread` 且不改状态；停止后模块返回 null，内核抛 `runtime-not-ready`。
- `privacy.instance-lookup-results-and-errors-exclude-account-lookup`：结果与错误文本并入输出后做 Lookup 泄漏检查，结果为没搜到。

另外两个已有用例各追加一项：外部模块给 `gtfo.player` 挂实例解析器被拒（`entity-instance-resolver-owner`）；插件成功路径能经内核解析出玩家。MapNativeLayout 从 38 增加到 39：原检查改名为 `native.player-resolver-and-instance-lookup-without-observer` 并要求登记实例解析器，新增 `native.instance-lookup-never-allocates`（单个 `object` 参数、不写字段、不调用对账或增删、不读 `Lookup` 与 `PlayerAgentsInLevel`）。

本批没有重跑 MapIdentity 与 MapContracts（身份层与 SDK 消费方源码未改）。下节表中的 35/38 是加入实例解析之前的计数。

## MAP5a — 玩家实体身份（2026-09-13）

证据等级：**implementation-only + 本机静态原生证据**。没有启动 GTFO、没有安装到任何 profile、没有主客机。全部构建使用会话临时目录的隔离 `--artifacts-path`，`GTFOBepInExPath` 指向只读的 Temp profile BepInEx；复跑命令见 [README](README.md#复跑)。

| 套件 | 退出码 | 输出结尾 | 说明 |
| --- | --- | --- | --- |
| `Native/ForgeMap.Native.csproj` 构建 | 0 | 0 警告 0 错误 | 真实 interop 编译，`ForgeRuntimeAssembly` 取同一 artifacts 的宿主构建 |
| `tests/MapNativeAdapter` | 0 | `PASS 35/35 Map native player identity cases; no GTFO execution.` | 生产源码加 loader 与游戏替身：注册顺序与回滚、身份规则、世界与停止清表、bot、迟加入、冲突、伪造引用、线程、隐私 |
| `tests/MapNativeLayout` | 0 | `PASS 38/38 Map native layout checks; no GTFO execution.` | Cecil：依赖方向、单一 provider 来源、只读且精确的玩家读取、Lookup 从不格式化或装箱、Hook 形状、插件身份与依赖、Off 门、不热卸载 |
| `tests/MapNativeEvidence` | 0 | `PASS 29/29 Map static native evidence checks; game execution NOT tested.` | build、hash、MVID、2 个 Hook 签名与 dump RVA 唯一不共享可执行、5 个读回签名、7 条直接调用边 |
| MapIdentity 回归 | 0 | 134 项断言通过，`nativeScenariosExecuted: false` | 未改 |
| MapContracts 回归 | 0 | 33 项通过 | 未改 |
| 架构回归 | 0 | `PASS 36 architecture boundary assertions. No GTFO hooks, gameplay, networking or installation exercised.` | `Program.cs` 未改 |

同批复跑 ForgeWeapon（源码未改，只改文档）：NativeAdapter `PASS 26/26`，NativeLayout `PASS 51/51`，NativeEvidence `PASS 52/52`，Identity `"cases":42,"assertions":99`，IdentityAcceptance `INDEPENDENT IDENTITY: 37/37 passed`，IdentityDispatchReview `DISPATCH REVIEW: 20/20 passed`，退出码均为 0。

中间失败与修正：
- MapNativeLayout 首跑 36/37：`GameAssembly` 判定只识别 `-ASM` 后缀，漏掉 `SNet_ASM`，精确读取集合因此缺少 SNet_Player 成员。补上 `_ASM` 后通过。
- 协调方追加隐私规则后，实体 ID 从 `gtfo.player:<Lookup>` 改为本世界编号，测试随之重写并新增 2 个隐私用例与 1 项静态检查。
- IdentityAcceptance / DispatchReview 第二次运行时报告文件已存在而拒绝覆盖（测试行已全部通过），换新报告路径重跑后退出码 0。

会话临时目录里的变异检查（只改副本，不改仓库）：去掉 agent 指针替换、去掉 `SNet.IsMaster`、去掉互链复核、去掉世界清表、冲突保留首个、实体 ID 用 Lookup、每个 life 换编号，7 个变体各自被对应的具名用例检出。报告 JSON 中搜不到任何 fixture Lookup 值。

未核验（需要游戏内确认）：bot `Lookup` 跨重生稳定性；postfix 时刻 `PlayerAgentsInLevel` 的成员是否已更新；`UnregisterPlayerAgent` 与 `OnPlayerDespawned` 的运行时顺序；倒地与救起不重建 PlayerAgent；`Object.Destroy` 延迟期间旧 agent 的状态；迟加入与主机迁移。

## MAP2 定范围（2026-09-13）

输出保存在 `evidence/map2-scope-2026-09-13/`。构建使用会话临时目录的隔离 artifacts，没有写 `ForgeMap/bin` 以外的模块输出。

| 检查 | 命令 | 结果 |
| --- | --- | --- |
| 生成相关原生签名锁 | `Capture-NativeApi.ps1 -TargetsFile tools/generation-api-targets.json` | 27 个类型、59 个方法，缺失 0，build 20403457，`metadata-only` |
| 独立复采比对 + 检查器自测 | `verify_native_api.py generation-api.json <复采> --self-test` | 退出码 0，`matched: true`，差异 0，自测 17 项 |
| 资源侧描述符 fixture | `python ForgeMap/tools/verify_resource_adapter_fixtures.py` | 退出码 0，合法 3/3（阻塞项与期望一致），非法 21/21 以期望错误码拒绝，`fixture-schema-only` |
| MapIdentity（含十个规格的托管替身标记） | Release 构建后运行 `ForgeMap.Identity.Tests.dll` | 构建 0 警告 0 错误；退出码 0，134 项断言通过；每个规格 3–17 项标记断言，`nativeScenariosExecuted: false` |
| MapContracts 回归 | Release 构建后运行 `MapContracts.dll` | 构建 0 警告 0 错误；退出码 0，33 项通过 |
| 架构回归 | `Forge.Architecture.sln` Release 构建后运行 `Architecture.dll` | 构建 0 警告 0 错误；退出码 0，36 项通过 |

MapIdentity 从 126 项增加到 134 项：新增同资源两个 placement 的兄弟回调拒绝（3 项）、同一 geomorph 两个 area 的错 area 观察拒绝（3 项），以及规格文件与标记集合一一对应的两项核对。原有断言未修改，只加了规格 id 标记。

**边界**：签名锁只证明方法存在；fixture 检查只证明描述符形状与拒绝规则，数据是合成的，不代表任何真实 Geo 包；134 项仍是托管实现加原生探针替身。没有运行游戏、加载 bundle、安装或发布。MapContracts 的 `TestWorld.cs` 在本批期间由主会话为 Framework 的 `promoted` 字段修改过，本批未改它，回归结果基于修改后的文件。

## MAP1b — 内部身份实现

| 检查 | 结果 |
| --- | --- |
| 实际 Map 身份实现 + 原生探针替身 | 126 项断言通过，退出码 0 |
| 原有 Map SDK 消费方回归 | 33 项断言通过，退出码 0 |
| 跨模块架构回归 | 通过（当时记录 35 项；**现行 Program.cs 是 36 项**，见 `ForgeRuntime/tests/Architecture/Program.cs`） |
| 三组 Release 构建 | MapIdentity、MapContracts、`Forge.Architecture.sln` 均 0 警告 0 错误 |
| 构建与测试期间的源码一致性 | 相关源码集合及 SHA-256 前后一致 |

初版 118 项断言通过后，审查把 native 探针从数字 key 提升为"创建票据 + key"的精确生命见证。随后新测试失败：测试替身把迟到的 Callback 当作新的 Bind，改写了模拟的物理生命。修复只分离了测试替身的 Bind 与 Callback 并改用正确的迟到回调路径，**没有放宽生产的身份校验**。失败记录保留在 `verification-final-1` 里，最终结论以 `verification-final-2` 的 126 项为准。

126 项覆盖地址与来源锁、int64 pathId、重复观察、冲突隔离、取消与销毁、world/generation/life、指针与 Unity ID 同时复用、probe 异常与重入、跨线程、停机与注销、历史上限、不完整采集。**这些是托管实现测试，不能替代原生创建 API、权限与阶段、主客机、导航或恢复测试。**

## MAP1 — 首批原生取证

| 检查 | 结果 |
| --- | --- |
| 本机 API 元数据与独立复采 | 32 个类型、110 个方法、9 个程序集；所选目标缺失 0；两次匹配 |
| API 检查器正反自测 | 17 项通过；故意修改方法、参数、枚举、程序集、版本或验证等级均被拒绝 |
| 命令行拒绝路径 | 6 项通过；基线未被覆盖 |
| 已有原生区域字节 | 11/11 与当前 GameAssembly 匹配 |
| 实际 SDK + 合成对象表 | 33 项断言通过 |
| Map 与测试工程 Release 构建 | 0 警告 0 错误 |

GameAssembly SHA-256：`c6a5c3cd8ca5fe2a8c1a71a3d107663e8cbf01404820dbbda2ea10c4bfd7bf55`（本机 Steam app 493520 / build 20403457）。原生区域复核是重新核对旧审计记录的字节摘要，**不是重新反汇编，也没有运行游戏**。没有复制、安装或分发游戏 DLL 与资源；程序集版本是编译元数据，不冒充 Thunderstore 包版本或许可证明。

已核验的入口示例包括 zone 与 layer 的 `LG_Zone.Create` / `LocalIndex` / `DimensionIndex` / `LG_Layer.CreateZone` / `AddZoneToLayer`，geomorph 与 area 的 `AddCustomGeomorphAreas` / `SetupAreas` / `SetPlaced` / `LG_Area.Setup`，plug 与 door 的 `LG_Plug.TryPair` / `Pair` / `LG_Gate.SpawnedDoor` / `LG_SecurityDoor.Setup` 及各种锁。**这些是签名存在的证据，不是"作者布局到本次维度层区域的唯一生成映射""创建回调的顺序与次数""运行中重建拓扑与导航与复制"的保证**——枚举可写入不等于门行为正确。

33 项 SDK 测试没有运行原生对象匹配、游戏网络或游戏权限绑定；其中的客户端与主机测试是 SDK 的权威控制测试，**不是两台真实 GTFO 客户端的验证**。

## 中间失败与修正

前三次 Map 构建遇到 Runtime 并发编辑的中间态：生命周期方法尚未配齐、重复 Advance、文件尚未写完整。原始失败输出保留在 `map-build.log` 与 attempt-2、attempt-3 里；本任务没有修改 Runtime 源码。随后构建成功。测试夹具的 provider 命名空间和未知实体的拒绝阶段按实际 SDK 修正——未知对象在入队前就被拒绝，而不是等生成命令的结果；**修正只在 Map 测试代码里，没有放宽 SDK 校验**。

## 复跑

从仓库根执行，输出目录必须尚不存在且位于 `ForgeMap` 内：

```powershell
python ForgeMap/tools/run_identity_checks.py --out ForgeMap/evidence/identity-recheck-01
```

只跑 SDK 消费方：

```powershell
$artifacts = Join-Path (Resolve-Path ForgeMap) 'bin/map1-artifacts'
dotnet build ForgeMap/tests/MapContracts/MapContracts.csproj -c Release --artifacts-path $artifacts
dotnet "$artifacts/bin/MapContracts/release/MapContracts.dll"
```

原生证据复采。路径是当前机器的已核验安装，在其他机器上必须显式替换；采集工具拒绝覆盖已有文件，每次用新的输出名，只读取游戏字节和元数据，不执行游戏代码：

```powershell
$m = (Resolve-Path ForgeMap).Path
$site = (Resolve-Path ../Infini-GTFO-Model-Site).Path
$bep = "$env:APPDATA/r2modmanPlus-local/GTFO/profiles/Forge-MapEditor-QA/BepInEx"
$game = 'E:/SteamLibrary/steamapps/common/GTFO'
$out = "$m/bin/native-api-$([Guid]::NewGuid().ToString('N')).json"
& "$m/tools/Capture-NativeApi.ps1" -BepInExRoot $bep -GameRoot $game -OutFile $out
python "$m/tools/verify_native_api.py" "$m/evidence/map1-2026-09-12/native-api.json" $out --self-test
python "$m/tools/verify_native_regions.py" "$site/artifacts/framework-implementation-2026-09-12/map-api-audit/native-regions.json" "$game/GameAssembly.dll"
```

测试的逐项进度写到 stderr，最终结构化结果写到 stdout。构建、测试与来源摘要必须一起保存；共享 SDK 后续继续变化时需要重新运行，本记录不保证未来的工作树仍然相同。

各测试目录的边界说明见 [MapContracts](tests/MapContracts/README.md) 与 [MapIdentity](tests/MapIdentity/README.md)。

## Player 观察与选择器绑定

| 检查 | 命令 | 结果 |
| --- | --- | --- |
| 托管契约（MapContracts） | `dotnet test ForgeMap/tests/MapContracts -c Release --artifacts-path $env:TEMP\playerobs --filter "Category!=Native"` | 12 项通过（隔离副本复跑，见下） |
| 身份实现（MapIdentity） | `dotnet test ForgeMap/tests/MapIdentity -c Release --artifacts-path $env:TEMP\playerobs --filter "Category!=Native"` | 30 项通过（隔离副本复跑，见下） |
| 原生观察者替身（MapNativeAdapter） | `dotnet test ForgeMap/tests/MapNativeAdapter -c Release --artifacts-path $env:TEMP\playerobs --filter "Category!=Native"` | 50 项通过（隔离副本复跑，见下） |
| 静态原生证据（MapNativeEvidence） | `dotnet test ForgeMap/tests/MapNativeEvidence -c Release --artifacts-path $env:TEMP\playerobs` | 1 项通过（内部检查全部通过） |
| 原生读集布局（MapNativeLayout） | `dotnet test ForgeMap/tests/MapNativeLayout -c Release --artifacts-path $env:TEMP\playerobs` | 1 项通过（精确读集 19 项 = 18 条读回 + 内核读取 `SNetwork.SNet::get_IsMaster`） |
| 宿主与原生模块构建 | `dotnet build ForgeRuntime/ForgeRuntime.csproj`、`dotnet build ForgeMap/Native/ForgeMap.Native.csproj` | 各 0 错误 |

三项托管测试在**隔离副本**里复跑：本任务执行期间，另一个任务正在写 `ForgeMap/MapObjectObservation.cs` 等文件，其 `MapObjectAddress` 与本任务无关的 `ForgeMap/MapIdentityContracts.cs` 中的同名类型冲突，仓库根的 `ForgeMap.csproj` 因此报 CS0101 无法编译。本任务没有修改那些文件；复跑用的副本位于会话临时目录，只在副本内临时移出该并发任务尚未完成的文件，被测的 Map 源码与仓库根一致。

`evidence/player-observation.json` 记录本次取证：`ilspycmd` 对 QA profile 互操作程序集的只读读取、`dump.cs`（build 20403457，SHA-256 `BF657C0E…`）行号对照，以及 15 条读回、5 条事实、7 条未核验。`evidence/map-hooks.json` 的 18 条读回是同一批成员在 hook 规格里的登记，两条命令都用它做基准。

运行时形状：观察者按 `EntityReference(Id, WorldEpoch, LifeEpoch)` 记录成员，实体编号是**首次记录的先后顺序**，不是槽位——槽位是独立的 `player.slot-N` 事实。身份校验只接受两次采集一致的生命，替换后的旧生命由解析器拒绝（`stale-entity`）；血量读取要求 `IsSetup` 且接收者属于同一 agent，否则该次观察整体报 `entity-observation-unavailable`。

**边界**：选择器绑定（`forge.selector.target.players`）只回答 `relation: ally`，其余关系以 `relation-unsupported` 拒绝；`empty` 只读取、不在模块内施加策略。内核已经具备 `query` 档位的派发路径（`ForgeRuntime/Framework/RuntimeKernel.Control.cs` 的 `EvaluateStep` 用 `BeginQuery` 构造 `EvaluationContext`，`RuntimePlan.cs` 接受 role 为 `observe` 且带世界端口的 `query` 节点），所以该绑定可以被计划和派发；但真正的端到端计划派发**没有在本批执行**（测试直接调用求值器），也没有启动游戏验证。求值器本身不读世界：它发布模块记录的身份，快照读取由消费步骤经 `RuntimeQuerySession`/`InspectEntities` 计入查询预算。测试全部使用托管替身与静态程序集读取。**后续改动**（见顶部「玩家候选来源与选择器的单列举路径」）：直接调用求值器的测试入口已删除，端到端派发由 `MapSelectorDispatch` 覆盖，求值器改经 `gtfo.player` 候选来源读取并计入 query 预算。

`evidence/map-hooks.json` 增加了观察者新读的 14 条读回（原 4 条 → 18 条）：该文件不在本任务说明的负责文件列表内，但 `MapNativeLayout` 的 `native.exact-player-reads` 要求原生程序集的游戏读取集合与该规格逐项相等，不补登记则无法通过。改动只追加读回行，未删改既有行。

## 玩家生命治疗（玩家 heal 接收器）

| 检查 | 命令 | 结果 |
| --- | --- | --- |
| 宿主构建 | `dotnet build ForgeRuntime/ForgeRuntime.csproj -c Release --artifacts-path %TEMP%\playerheal\art` | 0 警告 0 错误 |
| 原生插件构建（含新文件） | `dotnet build ForgeMap/Native/ForgeMap.Native.csproj -c Release --artifacts-path %TEMP%\playerheal\art-all -p:ForgeRuntimeAssembly=…\ForgeRuntime.dll` | 0 警告 0 错误 |
| 玩家治疗聚焦用例（MapNativeAdapter） | `dotnet test ForgeMap/tests/MapNativeAdapter -c Release --filter "FullyQualifiedName~player_heal"` | 5 项通过（提交/多目标、外来引用拒绝、接收器拒绝、策略与上限、unknown 与部分提交） |
| MapNativeAdapter 全量 | 同上，无 filter | 89 项通过（隔离副本复跑，见下） |
| 选择器派发（MapSelectorDispatch） | `dotnet test ForgeMap/tests/MapSelectorDispatch -c Release` | 12 项通过 |
| 原生读集与写集布局（MapNativeLayout） | `dotnet test ForgeMap/tests/MapNativeLayout -c Release --filter "Category=Native"`（`FORGE_ARTIFACTS`/`FORGE_MAP_BEPINEX`/`FORGE_GTFO_GAME`/`FORGE_GTFO_DUMP`/`FORGE_MAP_HOOK_SPEC`） | 1 项通过：9 条写行逐项相等，写入类型集合 = `DoorActions`/`PlayerHealthReceiver`/`TerminalActions`，玩家读集仍逐项相等 |
| 静态原生证据（MapNativeEvidence） | `dotnet test ForgeMap/tests/MapNativeEvidence -c Release --filter "Category=Native"`（同一组环境变量，`FORGE_MAP_BEPINEX` 指向含 `GameAssembly.dll` 的只读副本） | 1 项通过 |

`forge.action.combat.heal` 的玩家实现：binding `forge.module.gtfo.map.binding.heal`、handler `gtfo.player.heal`，写入点唯一（`Dam_SyncedDamageBase.SendSetHealth`，主机门 + 玩家 `ReceiveSetHealth` 的 `Owner.Alive` 门），证据在 `evidence/player-health.json`，写行在 `evidence/map-hooks.json` 的 `writes`。结果行按 canonical 六列写，`amount` 是读回的实际变化量；`overheal` 整条拒绝、`discard` 逐目标跳过、`clamp` 收到 `min(cap, HealthMax)`，满血零变化不发变化事实。玩家快照仅在接收器可读且 agent 存活时发 `health.heal`。

**隔离副本复跑**：本任务执行期间，另一个任务正在改 `ForgeMap/ModuleDefinition.cs`、`ForgeMap/ExpeditionContract.cs`、`ForgeMap/tests/**` 等文件（把地图触发行改为绑定 canonical capability）。仓库根当时因此无法编译（`ExpeditionContract.cs` 缺 `using System;`）且 Map registration 与 canonical provider 冲突。全量数字来自工作树的临时副本：只做了两处机械修复（补上那个 `using`、去掉与 `ForgeRuntime.Framework.TriggerContracts` 重复的本地 `terminal_result` capability 行）与给该任务自己的 fixture 补 canonical provider 注册；被测的玩家治疗源码、测试与证据与仓库一致。同一副本里仍有 3 类失败与该并发改动有关，不计入本批：`MapContracts` 1 项、`MapIdentity` 1 项（两者的「声明即能力表，一行一个实现 binding」不变式，在出现 canonical binding 后不再成立）、`MapObjectObservation` 的 `ExpeditionTriggerTests` 18 项（`forge.trigger.session.expedition_ended` 仍被本地重复声明）。

**未核验**：没有启动游戏、没有安装 profile、没有主客机与迟加入验证；写入点、主机门与已死拒绝都来自元数据、静态调用边与方法体解码（`evidence/player-health.json` 的 `unverified`）。ForgeEnemy 侧未做任何改动，它照旧只接受 `gtfo.enemy` 引用（`ForgeRuntime/tests/GameBindings` 的既有断言），但该 harness 本轮因另一任务的 ForgeEnemy attack 行未完成而无法编译执行，本批**没有**复跑它。

## 边界

这些数量不相加为"已实现能力数"。架构解决方案只覆盖共享 SDK、五个模块骨架和架构测试，不含 Runtime 游戏宿主的实机加载验证。没有启动或安装游戏、迁移配置、打包发布，也没有执行 Git commit 或 push。构建其他模块时也使用 Map 自己的忽略输出目录，不写别的任务的 `bin/obj`。
