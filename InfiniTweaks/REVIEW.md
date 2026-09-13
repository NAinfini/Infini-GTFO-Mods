## 2.5.4 — 2026-09-09

伤害统计修复：删除仅房主采集与 HostKnown 显示门槛。房主和客户端均从本机原生 ProcessReceivedDamage 前后 Health 的实际减少量采集，不含过量伤害；不接收或广播另一端的伤害总数，避免重复。v6 握手仅同步命中率及房主观察到的机器人命中率；精确远端命中率仍需要匹配版本，伤害不再需要房主安装本模组。换房主或重新握手保留本机伤害记录。中途加入前或未通过本机原生处理的伤害不在覆盖范围。

证据：当前 GameAssembly 的 ReceiveBulletDamage（RVA 0x137EC20）调用 ProcessReceivedDamage（0x137E570），后者调用 RegisterDamage（0x161F450）；RegisterDamage 直接更新 Health，未检查 SNet.IsMaster。此前自家后置补丁的房主限制与格式器 HostKnown 门槛导致客户端显示破折号。743 项回归及 106 项 QOL 原生 hook 契约检查通过，Release 零警告／错误。客户端实机伤害对照仍待验证。



医疗图标改为红心配白色十字。

图标显示倍率保持 0.72，不整体缩小。按 alpha≥128 的实际图案主体统一最长边约224px、中心128px；剔除主体之外极淡杂点对尺寸的影响，保留宽高比和抗锯齿边缘。

显示中的自定义图标固定为不透明白色 SpriteRenderer 颜色，保留 PNG 自身颜色与透明边缘，不再继承原生或瞄准时的淡化。名称和距离仍沿用既有淡化；物品隐藏、拾取、耗尽和范围控制不变。

删除安全门锁的扫描、三处登记 hook 和 DoorLock 分类。普通门、安全门及其钥匙／电源／舱门锁不再创建本模组标记；终端路径同样排除门。钥匙卡、电池、发电机和舱门控制器保留。旧 DoorLock 配置在启动清理。door.png 仅保留在已提供的图案库，不再被运行时选择。

验证：Release 零警告／错误，740 项回归、247 项图标检查、106 项 QOL 原生 hook 契约通过。实机渲染尚未验证。

## 2.5.3 — 2026-09-09

所有自有物品／设备标记统一使用自家 PNG，不再选择原生分类图案。固定 HSU 和 HSU 插入装置按游戏资源分别绘制；C-Foam 按用户要求改为闪光弹式圆柱手雷；备用 Glow Stick ID 136 使用现有荧光棒图。未识别物品使用带问号的 unknown-item.png，不冒充已确认模型。便携屏障按用户要求不制作专用图，未确认的定向地雷也不捏造外形。

共 58 张 256px 透明 PNG，统一可见最长边 224px，显示倍率保持 0.72。原有图标、ResourceHelper 式发现／范围／淡化、开箱和落地恢复、亮色居中资源 HUD 保持现行行为。HSUActivator 单独使用同为 40 米的类别默认范围。

验证：Release 构建零警告／错误，740 项回归、232 项图标加载／映射／透明度／缓存检查、58 张 PNG 检查通过。尚未进行本版 Unity 渲染或多人实测。

## 2.5.2 — 2026-09-09

按 Localia ResourceHelper 3.0.1 的资源标记行为重做显示：资源包默认 40 米，消耗品默认 10 米，60 米硬上限；PING 临时显示 15 秒，附近已发现拾取物按上游 0.5 秒 × 7 次刷新。近于 4 米显示名称，远处使用 `20° − 距离 × 0.3` 视线锥；距离常驻。标记出现时 0.1 秒淡入，瞄准渐淡到 50%，不再完全关闭。

修复原生 `SetAlpha` 跳过未激活组件，而旧代码先改 alpha 再显示且缓存 alpha 的恢复错误。透明度现在在可见组件激活后逐帧刷新；名称折叠只改文字，不再重建原生视觉状态。自定义图案统一使用 CarryItem 图层，放大 1.8 倍，图案最长边归一到 224/256 像素。新增 11 色钥匙卡，黑色钥匙卡不再使用通用任务符号。

持包 HUD 每行以名字实际文字边界的中心对齐；覆盖继承的字体面颜色。低于 25% 为亮红，50% 亮黄，75% 起亮绿，中间连续渐变；非瞄准淡化最低保留 85% 不透明度。仍显示真实武器／工具名。瞄准时资源文字沿用单独的 AimOpacity 设置，与场景物品的 50% 淡化分开。

开箱、选中拾取物、落地及延迟数量更新继续走统一登记；拾取／耗尽移除，已发现且仍放置的物品不会因世界模型被裁剪而遗忘。设备／携带物继续使用已有可用状态与维度检查。未复制上游源码、DLL 或图案，也不复制每帧累加缩放。

离线构建与回归不是实机验收；需重启后核对开箱→移开视线→返回、瞄准、远近变化、掉落资源、钥匙卡 PING、多人和检查点。

原生证据：当前 GameAssembly 的 NavMarker.SetAlpha（RVA 0x16F91B0）仅更新 activeAndEnabled 组件；SetVisualStates（0x16FB930）会调用 SetState(Inactive)；NavMarkerComponent.SetAlphaScale（0x16F6250）按初始 alpha 缩放。测试双身加入 inactive 跳过行为后，旧实现的隐藏／恢复测试先失败；修复后通过。没有将其它模组日志异常归为本次根因。

## 2.5.1 — 2026-09-09

44 张 256px RGBA 标记图案内嵌 DLL，覆盖资源、消耗品、任务物品和交互设备。熔锁器、驱雾器按确认的结构处理；发电机使用立式电池插槽装置，新增消毒站、舱门控制器、必要门锁、Neonate HSU、硬盘、数据物品与任务箱图案。红／橙／黄／绿荧光棒分开。保留原生透明度和既有显示尺寸，图案本身不再乘类别染色。

完整 66 条拾取物与 7 类设备的图标对应、原生图标保留项及实机边界见 MARKER-COVERAGE.md。新图案不是覆盖所有变体的独立造型；C-Foam 手雷仍保留已有图案。Forge Runtime 不属于此次更新。
# Implementation review

## 2.5.0 / Forge Runtime 1.0.0 — 2026-09-09

性能采集、场景扫描、性能报告脚本和对应测试迁入 ForgeRuntime。Infini Tweaks 保留可选测量事件；无订阅者时不读取时钟、托管分配量或原生 profiler 状态。两个插件均无对方的程序集引用，制作端未添加游戏生成补丁。配置、DLL、日志前缀独立。

Release 双构建零警告／错误；742 项游玩回归、7 项迁移的帧采样、56 项可选订阅／清理、8 项场景扫描、6 项报告分析、18 项 HUD、15 项发现路由检查通过。原生元数据验收单独检查程序集职责归属及现有 hook 与嵌入图标。

这次仅完成拆分，不声称新增生成报告或修复随机／连续切关异常。[制作端范围及证据](../ForgeRuntime/AUTHORING-SCOPE.md) 列明下一阶段。未启动游戏；生成一致性、实际开销、切关和联网验收待实机。Temp 当前已安装的 2.4.5 不由这次源码拆分自动替换。

## 2.4.5 — 2026-09-09

本版发布内容以 CHANGELOG.md 与 HUD-BEHAVIOR.md 的 2.4.5 段为准。以下旧版本审查属于历史记录。离线检查不代表游戏实测；安装状态通过 Profile、mods.yml 和 DLL 哈希独立核对。

## 2.4.3 current repair build — 2026-09-08

Current source/build findings are maintained in [UPSTREAM-ACCEPTANCE.md](UPSTREAM-ACCEPTANCE.md). All older sections below describe historical source, test and installation states, not the current package. This build handles the confirmed HUD rebinding bug, shared device-ping routing, independent checkpoint slot reconstruction, late-spawn registration, native statistics row ancestry, missing NoInterruptions hooks, damage source contexts and redundant updates identified in that audit.

Executed on the final 2.4.3 DLL: Release build (zero warnings/errors), 734 regression assertions, 13 HUD-view, 19 marker-registration, 32 deposit-slot, 12 terminal-runtime checks and 109 native hook contracts. Slot doubles exercise rotated/scaled parent transforms with independent expected positions; they are not a Unity physics/renderer test. Hook contracts now validate __instance assignability as well as native methods and named parameters. No in-game session, screenshot acceptance or CPU/GPU benchmark was performed.

Package includes this review and UPSTREAM-ACCEPTANCE.md, not upstream source files or assemblies. Profile installation must be checked independently by manifest, mods.yml and file hashes; a build version is not proof of a loaded game version. Live acceptance still covers the resource/consumable open-pickup-deposit lifecycle, every stock box/locker slot, device pings, recall, multiplayer and HUD geometry. Native Actions packetIndex=47/count=47 remains unassigned to a sender and unresolved. Do not hide it or claim this update fixes all game lag.

## 2.4.2 historical repair candidate

Live 2.4.1 evidence: Temp BepInEx LogOutput.log contains two LG_GenericTerminalItem.set_SpawnNode null exceptions from RegisterTerminal, 116 registered pickups/228 terminal objects, and applied container-placement messages. Player.log contains repeated bot-send warnings and Actions packetIndex=47 with packet count=47. The latter stack only identifies native receive/replication, not a sending plugin. Own GTFO-API events use its independent 65533 replicator-key envelope; do not attribute the Actions error to own statistics without peer/transport evidence. Statistics use Dinorush's local/host-bot accuracy eligibility; nonmodded human peers and a nonmodded host cannot supply exact remote accuracy/authoritative damage.

Compared actual ItemMarker ItemScanner, ItemInLevel_Marker, LG_ComputerTerminal_Marker and container-open hook, cached ResourceHelper 3.0.1 Res_Manager/Res_Moniter, PacksHelper 3.1.4 PH_Manager, DropItemPlus 1.0.1 slot/manager/feature implementation, and Dinorush StatHandler/AccuracyPatches. Implemented native lifecycle and compartment-interaction behavior in own source, not another embedded DLL or a second display pipeline. Removed the old polled slot selector and its obsolete CanSelectSlot tests. Unsupported non-stock compartment counts are explicitly logged, not given invented geometry.

Release build: zero warnings/errors. 729 existing regression assertions, 17 production terminal-registration checks, 25 production deposit-slot checks, and 95 native hook contracts passed. The four removed assertions described the deleted slot-selection algorithm. These checks do not run IL2CPP detours, render a preview, or certify multiplayer. Candidate remains uninstalled while GTFO is running. Required live acceptance: open/ping a resource and consumable, pick up (no following pack icon), deposit into each empty stock compartment (prompt, hold, model and marker), terminal proximity/query, checkpoint reconstruction, right-aligned statistics at the current resolution. Actions packet mismatch and full-game lag attribution remain unresolved; no performance improvement is asserted.

## 2.4.1 dynamic registration (candidate, not deployed)

Evidence: ItemMarker source commit 5b147221326d48402a874e2f625c63f6ef15b871, Features/ItemMarker.cs (Reactor OnBuildDone and generic terminal Setup) and Handlers/ItemMarkerBase.cs (missing SpawnNode from LG_Area). No license file was found in that checkout; the event behavior is independently implemented in the existing Infini registration flow, with no upstream source/DLL copied. ItemMarkers.Registration.cs is a partial section of the same ItemMarkers class, separated so the actual production hooks can be tested with small native/presentation doubles.

Sixteen production registration checks cover real-area node repair, preserving native nodes, ID changes, no registration-time reveal, terminal icon/anchor metadata, live activation, repeated Setup, changed anchor preserving discovery, label refresh, replacement-safe deletion, reactor list deduplication and incomplete setup. This verifies managed control flow, not Unity object destruction semantics or a played reactor expedition. EWC was explicitly excluded by the user in this turn; older entries calling it pending are historical.

Final Release build passed with zero warnings/errors, along with 733 existing regression assertions and 92 native hook contracts. The new tests link the production registration partial directly. No profile files were changed.

## 2.4.0 source rewrite (candidate, not deployed)

Latest ownership supersedes earlier entries: own-source statistics, no embedded/loaded StatDisplay; Archive-only chat and weapon descriptions. Removed the replaced statistics collector/view/model and excluded chat/weapon-description files rather than keeping parallel paths. CombatStatsModel/Format contain the tested counts, expressions and recipient-bound v5 snapshot model; CombatStatistics/View own native hooks and presentation.

Release build: zero warnings/errors. Passed 733 regression assertions, 12 production terminal-hook tests, 9 production resource-HUD view checks and 91 native hook contracts. One native-check invocation omitted GTFOBepInExPath and failed resolving Mono.Cecil; supplying the documented path then passed. Contract checks explicitly reject embedded StatDisplay resources/references, retired chat/weapon UI types and OnGUI. These checks do not execute IL2CPP detours or render the statistics view.

Terminal tests reproduce synchronous watch replacement, input synchronization before exit, command ordering through native validation, bounded queue and cleanup. Interaction continuity preserves preexisting native flags in a finalizer; real pack/carry/LOS interaction still needs in-game validation. EWC adapter, full original presets/protocol, weapon-instance histories and all previously listed marker gaps remain unsupported/unverified as specified in REPLACEMENT-AUDIT.md. New result layout, checkpoint retention and host/client late snapshots need live acceptance. No current-build FPS claim or profile deployment is implied.

## 2.3.0 performance candidate (prepared, not deployed)

See [PERFORMANCE-REVIEW.md](../ForgeRuntime/PERFORMANCE-REVIEW.md) for the 2.2.7 log baseline, ItemMarker/ResourceHelper/HUDInfoPlus source comparisons, changed scan scope, limitations and live acceptance cases. Marker state submissions now occur only on change, ordinary lifecycle checks at 10 Hz, and scene components are collected by one cooperative hierarchy walk. Native projection, immediate input transitions and pickup/count events remain. New workload counters separate discovery from refresh; no same-scene FPS/CPU/GPU comparison has been performed.

Validation: 781 main regression assertions, 8 independently built scene-inventory checks, 87 native hook contracts and 5 analyzer tests. The scene test initially shared output with the regression project and launched the wrong entry point; it was moved to its own project directory and rerun successfully. One stable marker across 7200 managed refresh calls avoids 28800 old-path setter calls, not 28800 measured draw calls. Existing profiler ownership and missing FrameTiming support are not silently overridden.

## 2.2.9 consumable verification (prepared, not deployed)

Confirmed Temp's Consumable category is enabled with DistanceMeters=12. Classification uses the native consumable inventory slot; icon tests exercise Consumables|Title|Distance. Found that only ResourcePackPickup had a dedicated custom-data callback and depletion check. Added ConsumablePickup_Core.OnCustomDataUpdated and one shared depleted-item policy used by discovery, amount updates and pin cleanup. Native infinite-use consumables and objectives are not mistaken for empty finite stacks. Existing pickup sync removal and open-container discovery apply to both categories without a new subsystem.

Validation: Release zero warnings/errors; **749 regression assertions**, **65 native hook contracts**, diff whitespace checks passed. Five new assertions cover positive consumable counts, zero/negative/invalid finite amounts, uncounted objects and enabled default range. This does not execute the native callbacks or rendering. Live acceptance: discover glow sticks/fog repellers/other consumables, observe one icon/name/count/distance; partially transfer a stack, pick up its last item, and put it back on ground/in an open container. Confirm count refresh, no ghost and rediscovery. Closed containers must not expose their contents and cabinet/box shells must stay unmarked. GTFO remains on the previously installed build until the user exits for deployment.

## 2.2.8 source-driven marker/HUD/statistics repair (prepared, not deployed)

Compared cached ItemMarker 1.1.0, ResourceHelper 3.0.1, ColorGradingHUDInfoPlus 1.0.1, StatDisplay 1.1.8 and Booster Tweaker 1.2.6 implementation paths, not just mod descriptions. The evidence and deliberate non-equivalences are recorded in FEATURE-COMPARISON.md. New user requirements take priority: no locker/box markers, follow carried cryo/cells, remove picked-up resource pins, contextual resource HUD, compact built-in stats; the user explicitly chose Dinorush's weakspot-rate definition.

Root causes corrected: LootTitleDistance overwrote every selected icon; native carry marker ownership was missing; terminal identity/anchor and own-cabinet linecast could prevent discovery; regular pickup identity could be exchanged before cleanup; visibility was followed by nonzero alpha even for hidden pins. Carry handling now uses CarryItemPickup_Core.OnSyncStateChange/PickedUpByPlayer and reconciles its current state, including late-join held objects. Ordinary resource placement repair no longer handles carry-objective parent/culling state. Native visibility ownership is scoped to the exact carry marker and released when custom visuals stop, not a global suppression of objective/rescue/ping markers.

The current live 2.2.7 log contains MissingMethodException: System.__Canon..ctor(IntPtr) in PlacementPreview.Select's native Dictionary.TryGetValue. That exception skips subsequent overlay work in that invocation. Select now calls ItemSpawnManager.GetItemPrefabs instead; it still needs a live preview check. This is not proof that every HUD issue originated in that one exception.

Validation: Release with zero warnings/errors, **744 regression assertions**, **64 native hook contracts**, **4 Python analyzer tests**, and clean diff whitespace checks. The first native-contract invocation omitted its local reference-path property and failed to find Mono.Cecil; rerunning with the required property passed. These are offline checks, not Unity rendering or actual detour/multiplayer execution. New tests exercise actual category visual flags, native-marker ownership/restoration and HUD infinity/empty-slot behavior. Live acceptance required before declaring the replacement working:

- Discover med/ammo/tool/consumables: correct icon, one title/count/distance; take the last use or pick up: no ground ghost. Open/empty lockers and boxes: no custom cabinet pin.
- Discover/use a terminal locally and via an unmodded teammate; discover disinfection station, HSU/sample/activator, generator, controller and locked door. Device housing must not block discovery; an intervening wall and another dimension must. Completing the interaction hides its pin.
- Cryo/cell on floor → teammate carries → walks/changes dimension → drops → inserted/solved: one correct tracking marker; own carry: no additional custom icon. Late join and checkpoint rebuild must be checked. Disable/ADS/F8 must not leave a duplicate or permanently erase the game's original objective marker.
- Weapons in hand: normal HUD. Each pack: only its matching highlighted value; biotracker infinity, deployed-sentry reserve, missing slots and used-up pack counts. Disabled Resource HUD must not hide native extra info.
- Stats: known shots/misses/weakspots including piercing and shotgun pellets; grey hit/fired, yellow weakspot/hit, grey damage, compact native inventory position (Temp OffsetX remains 100). Host/client missing-data and native result pages still need visual verification.
- Deposit medical pack and glow sticks: a mesh preview, correct amount/model after placement and no System.__Canon exception in a fresh log. Booster equipped values, actual effects/conditions, consumption protection and reward multiplier still need transaction-level validation.

No game process was closed and no running-profile DLL was replaced during this work. Full upstream format editors, item-specific customization, checkpoint-memory restoration, booster template editing and fixed rewards have not been claimed as implemented.

## 2.2.7 integrated native statistics

The user's latest clarification explicitly supersedes the external StatDisplay choice: Display, boosters, resource HUD and item markers belong inside InfiniTweaks.dll. Reused our own validated cumulative snapshot state and implemented compact native TMP rows after inspecting Dinorush's native inventory/page integration. No upstream source or DLL is embedded, no independent panel/IMGUI/F7 path remains. Current booster/HUD/marker implementations already reside in this plugin; selected features and non-equivalent upstream extras remain enumerated in FEATURE-COMPARISON.md. ResourceStack stays in Hikaria Core by the user's prior explicit instruction.

Added weakspot counters with validation and owner authority; actual limb damage callbacks replace collider-only hits. Remote-human visual rays no longer masquerade as accuracy. Host effective health loss excludes overkill. Build-start/leave clear results; checkpoint/new entry reset the session; native success/failure and return lobby continue late synchronization. Text clones are owned siblings, not recursively nested in source text; unchanged strings do not rebuild TMP meshes, and native rewards/report content are preserved.

Validation before deployment: **703 regression assertions**, including linked production collector hooks, late owner packets, success/failure/lobby/reset/leave handling and the pure multiplayer protocol; **56 native hook contracts** and required-module/absent-IMGUI checks. Release compiles with zero warnings/errors. These checks do not execute actual IL2CPP detours, render HUD at real aspect ratios, or prove multiplayer/booster transactions. Verify these in game after installation. Native end-screen layout is an integration, not a claim of all upstream formatting/EWC/damage-category features.

## 2.2.6 statistics ownership change (superseded; never deployed)

The user rejected the independent panel and explicitly chose to restore original Dinorush StatDisplay. Removed TeamStatistics, StatisticPeer, StatisticRow, StatisticsSync, their tests/doubles, F7/Shift+F7 configuration, end-result callbacks, performance-section entries and all IMGUI rendering/dependency. This supersedes the 2.2.5 panel implementation and acceptance plan below, which remain historical review notes only. There is no alternate built-in display path left to enable.

Inspected cached original StatDisplay 1.1.8 code and its author package documentation. The provider uses native inventory TextMeshPro-derived HUD rows, native success/failure reports, and its own statistics/configuration/networking. Its Archive adapter explicitly routes version-major-zero Archive builds to StatDisplay.cfg. The third-party DLL is not embedded or modified; restoration must accompany deployment of this no-statistics Infini build so only one provider runs.

Validation: **628 regression assertions**, **41 native hook contracts**, Release with zero warnings/errors. The native checker additionally inspects the compiled DLL to reject retired statistics types, any OnGUI method and the IMGUI assembly dependency. Removed 70 assertions and 13 native contracts belonged to the retired implementation; existing HUD/resource/booster/performance coverage remains. Original StatDisplay startup, HUD positions and multiplayer/end-screen values still require a game run after deployment. No claim is made that the old Infini statistics protocol is compatible with StatDisplay.

## 2.2.5 statistics follow-up (packaged, pending deployment/live acceptance)

Live state logs showed `InLevel → ExpeditionSuccess → InLobby`, whereas the old Tick/OnGUI/Tracking guards accepted only InLevel and AfterLevel. This was a genuine result-visibility gap, despite EnableStats already being true. Statistics now recognizes success/failure directly, ends the attempt once, uses one CanDisplay rule in Tick and OnGUI, and retains the final table in the return lobby. Dismissal survives later AfterLevel callbacks; new expedition/checkpoint reset clears the previous attempt. Only InLevel before completion can collect combat data.

The final handshake/snapshot exchange no longer stops as soon as gameplay ends. Incoming result snapshots rebuild visible text immediately. The roster is initialized on reset and refreshed before snapshots, avoiding empty initial display and a first-packet name race. Missing host damage is explicitly unavailable. Reset/toggle/result logs make a later live check distinguish collection, presentation and network availability.

Validation: Release build with zero warnings/errors, **698 regression assertions** and **54 native hook contracts**. New regressions link the production TeamStatistics class and invoke its actual shot/ray/damage hooks, network handlers, Tick and result lifecycle. They cover piercing, miss/overkill, first roster, nickname control characters, F7/Shift+F7, gameplay menu focus, peer authority, final sending/late receipt, self-only filtering, success/failure, lobby retention, dismissal, reset and disabled/unavailable states. Existing multi-peer protocol tests still cover reconnects and host migration. No test executes Unity rendering or native detours; verify actual F7 rendering, gun/shotgun hits, host/client final totals and lobby results in-game after deployment.

## 2.2.4 marker/HUD follow-up (packaged, pending deployment and live acceptance)

Fixed the disabled-HUD finding below at the ownership boundary: disabled Tick and hooks do not modify native information. While enabled, incoming native visibility requests are tracked separately from the custom filter. Turning off or resetting restores that native state and regenerates native text; marker destruction removes tracked state. Tests cover initially disabled pack switching, enabled-to-disabled restoration of both visible/hidden native requests, held-pack-only inventory counts, empty-slot filtering, ADS, downed-player protection and untouched opacity while disabled.

Markers now use native category styles with 15 independent range/color settings, counts and explicit-ping range extension. Level-entry device metadata uses real terminal/generator/disinfection/HSU/bulkhead/door-lock references. Unavailable interactions hide and door labels reflect their current missing requirement. Native anchor/dimension/discovery restrictions remain. The old single-distance path was removed, not kept as an override layer. Incoming placement callbacks now use their supplied new state; checkpoint reload clears the owned placement preview/prompt.

Validation: **680 regression assertions**, **54 native hook contracts**, Release build with zero compiler warnings/errors. These are offline checks, not proof of Unity rendering or native detour execution. Live acceptance additionally requires each configured category's icon/color/range, explicit re-ping extension, device completion/reactivation/checkpoint, normal versus held-pack HUD, disabled HUD, every placement/booster case below and a fresh diagnostic log. No GTFO process was started or closed for these checks.

## Performance follow-up after 2.2.3 (source changes, not deployed)

The two available 2.2.1 live expeditions showed scanner-induced 3.35/4.14 second stalls, incomplete native CPU/GPU channels and an unrelated ReplayRecorder failure. Performance collection now advances inventory object processing incrementally with a cooperative 2 ms budget, records the actual maximum step and cancels scans on state changes. A native enumeration or individual Unity call can still exceed the budget; there is no claim of a hard per-frame limit. Manual captures defer scanner work until native capture ends. Continuous slow-frame runs are retried after cooldown.

Frame summaries no longer mix known state boundaries. Duplicate timestamped native timing reads are excluded; absent timestamps/samples and texture failures are explicit. Buffered percentile storage overflow retains the true full-interval minimum/maximum. The standard-library Python analyzer separates attempts, uses frame-weighted FPS, shows slow windows, instrumentation and memory trends, and states missing-channel/GC/attribution limits. Neither report nor build is a substitute for live profiling.


Validation of this follow-up: Release build with zero compiler warnings/errors using offline local references, **644 regression assertions**, **53 native hook contracts**, and **4 Python analyzer tests**. The analyzer was also run on the two-expedition live log (26,385 parsed rows, no malformed rows), reproducing 89.8/87.8 aggregate FPS and 3367/4172 ms maximum frames. The first online restore's vulnerability lookup was unavailable; the successful build used a temporary offline source configuration, without changing repository/global NuGet settings. Incremental Unity enumeration, actual native timing availability and capture behavior still require a fresh game run.

Original full-feature review finding (resolved in 2.2.4 above): `ResourceHud.Tick` called `SetExtraInfoVisible(false)` when the module was disabled and held context changed. A managed probe preserved the native marker as visible before the call, then observed visible=false and one native update with EnableResourceHUD=false. The performance-only follow-up did not change it; the subsequent HUD fix and regression tests now do.

## 2.2.3 gameplay repair

Resource stacking is now owned solely by Hikaria Core. Its installed ResourceStack implementation and startup patch were inspected; it supports both matching resource packs and consumables. Removed Infini Tweaks' merge algorithm, settings, notification protocol and associated 511 assertions rather than retaining a second disabled implementation. Placement validation remains scoped to Place interactions.

Live 2.2.1 logs showed the HUD/marker/resource Update sections running, so the FrameTiming startup exception was not evidence of a shared overlay failure. The HUD postfix instead assigned text after native rendering without resubmitting it, depended on previously visited markers, and displayed unrelated percentages. The replacement refreshes the native roster on held-context changes and explicitly submits only the matching text after native UpdateExtraInfo, preserving the player's name. The production hook is linked into offline tests covering first refresh, every pack category, shortage colors, enlargement, weapon switching and death.

Markers previously covered explicit pickup pings, not discovery or static terminals. Registration now tracks named terminal entities; proximity/line-of-sight and QUERY/PING can remember them. Native computer-terminal proximity also triggers discovery. Initial sync/container node assignment and level-entry registration address missing dimension information. A checkpoint clears remembered pins without prematurely throwing away still-live terminal registrations.

Container targeting previously depended on a ray hitting a collider under an empty container. It now selects reachable empty native slot anchors through an unobstructed ray, recognizes native logical/graphics open state, validates occupied slots on the host, and associates native placed resource/consumable objects with storage. This changes native/network behavior and is not certified by geometry-unit tests alone.

Live warnings identified unmatched historical booster templates 26 and 38. Matching now considers authored historical templates only with the same ID, category and exact existing effects. It retains conditions/effect IDs, runs on inventory/active-list events, and logs actual changed counts. Consumption hooks and currency multiplication log when invoked. Farming still multiplies earned currency; it does not implement Booster Tweaker's forced reward/custom-effect modes.

Required live acceptance: first teammate marker before any previous info callback; all four pack types, low/healthy teammates, weapons, death and late join; terminal proximity/QUERY/PING, opened versus closed containers, pickup/deposit/F8/checkpoint/dimensions; host and client deposit in empty and occupied box/locker slots with uses conserved; historical booster inventory and gameplay values, count before/after success/failure/restart, explicit consumption and reward logs. Do not claim full standalone parity or delete additional modules based on metadata checks.

Subsequent live 2.2.1 feedback confirmed placement could succeed while its model was missing, with an unreadable cue and hard-to-select slot. The native item culler maintains cached render commands; 2.2.3 refreshes bounds/shadows/command buffers after non-custom-only state changes. The preview now draws only cached pickup meshes, never instantiates an interactive/network pickup, and owns one material released on level cleanup. Native localized interaction text replaces placement IMGUI. Slot acquisition accepts a 0.65 m crosshair offset within 3 m reach and retains a small selection bias to reduce jitter. Deployed sentry HUD uses its own ammunition rather than backpack reserves.

Verification: **641 regression assertions**, Release with **zero warnings/errors**, and **53 native hook contracts** against the installed game interfaces. Counts fell after removing 511 assertions belonging to the deleted stack algorithm. Tests include actual HUD postfix submission, category filters/colors/sizing, first refresh, death, weapon switching and deployed/carried sentry values; geometry and historical booster matching are linked production rules. Mesh rendering, cull-command refresh, native prompt appearance and network/persistent outcomes remain untested in-game. The new DLL must not be claimed installed while the previous process still owns the profile DLL.

## 2.2.2 FrameTiming startup hotfix

The first live 2.2.1 launch exposed an IL2CPP interop contract that metadata-only compilation could not catch: `Il2CppStructArray<FrameTiming>` had no initialized native class pointer and threw from `PerformanceDiagnostics.Start`. The diagnostics now resolve and install the real `UnityEngine.FrameTiming` class pointer before allocating the native array. This capability is isolated so failure disables only CPU/GPU FrameTiming samples, records the exact status, and leaves all other logs operational.

## 2.2.1 full-scene coverage

Expanded the diagnostics so CPU/GPU frame timings are accumulated every frame rather than reporting only the latest available timing. Each text interval also records texture-streaming pressure and a broader 192-sampler cross-section; the complete native sampler catalog remains in the session log. Native `.raw` capture now temporarily enables and later restores all 13 profiler areas exposed by this Unity player.

Level-start/F9 snapshots now include every IL2CPP component type's loaded/active count, renderer visibility, mesh/material/static-batch/shadow proxies, cameras and command buffers, lights, particles, audio sources, animators, collider/rigidbody state and UI canvases. These totals cover every object successfully exposed by `Resources.FindObjectsOfTypeAll`; ranked per-object detail remains bounded by `MaxAssetsPerType` so logging does not become the bottleneck. Visible geometry and weights are explicitly proxies because this player lacks a runtime `ProfilerRecorder` API for official draw-call/triangle counters.

Verification: **1134 regression assertions**, Release build with **zero warnings/errors**, and **44 native hook contracts** against Temp's installed game interfaces. Live validation must still confirm which FrameTiming and native-profiler channels the retail executable actually populates.

## 2.2.0 performance diagnostics

Added a default-on diagnostic path designed for the reported sustained 120→40 FPS drop. Per-frame work is kept to in-memory sampling; structured text is written by a bounded background queue. One-second summaries record frame-time distribution, slow frames, available CPU/GPU frame timing, process/Unity/managed/graphics memory, GC collections, threads, selected native Unity samplers, and timings for Infini Tweaks' recurring/cost-sensitive paths. Plugin/Harmony inventories make each session reproducible.

Three consecutive slow in-level frames request a bounded 10-second native Unity `.raw` trace, with a 60-second cooldown. F9 also refreshes loaded resource/component inventories and requests a manual trace. Asset scans report their own elapsed time. Texture/mesh/material/audio/skinned-mesh size and complexity are evidence for memory or rendering pressure, not fabricated per-asset GPU timings. If the retail player does not support native profiling, the text log records that limitation and continues.

Verification: **1134 regression assertions**, Release build with **zero warnings/errors**, and **44 native hook contracts** against Temp's installed game interfaces. These checks do not execute Unity profiling, resource enumeration or IL2CPP detours.

Focused live acceptance: start a Temp-profile expedition and confirm a new log under `BepInEx/PerformanceLogs`; wait five seconds and confirm `asset_snapshot_end`; press F9 and confirm either paired `unity_capture_start`/`unity_capture_stop` plus a non-empty `.raw`, or an explicit `unity_capture_unavailable`; reproduce a 120→40 FPS interval and correlate `frame_summary`, `unity_sampler`, memory/GC and `infini_section` timestamps. Confirm `dropped_log_lines=0` at shutdown and compare a control run with diagnostics disabled if the measured `Diagnostics` section is material.

## 2.1.1 corrections

Fixed the three issues found in the post-install review:

- A reconnecting owner's new sequence 1 was rejected behind their old sequence 100. Receiver-issued session challenges now separate tracking incarnations, reset all that owner's weapon accuracy, retain existing host damage and reject delayed old packets/confirmations. Production protocol state is exercised directly by multi-peer regression tests.
- Exact owner accuracy no longer waits for a stats-enabled host epoch. Two participating clients exchange it directly; only damage and estimates require host authority. Host migration/reset does not erase exact owner accuracy.
- Persistent pins no longer select the closest item within 1.5 metres. Local pings use `PlayerAgent.TriggerMarkerPing`'s `targetGameObject`; terminal pings use `terminalItemId`; participating peers share the exact native pickup `SyncID`. Closed-container pickups are rejected. Coordinate-only pings from nonparticipating clients retain native behavior, without a guessed persistent pin.

Verification: **1130 regression assertions**, Release build with **zero warnings/errors**, and **44 native hook contracts** against Temp's installed game interfaces. Target-object/terminal-ID hooks are signature-checked, not executed in Unity. No live game session was run for this release.

Focused live acceptance: two clients with an unmodded/stats-disabled host; client disconnect/rejoin after 100+ snapshots; checkpoint reset; host migration; ping one of two adjacent items, a wall near a pickup, and a closed locker; compare local, participating-teammate and terminal pings. Verify ADS/distance filtering and native generic pings remain unchanged. The broader rendering, resource, booster and multiplayer checks below remain pending.

## 2.1.0 acceptance status

Implemented resource/consumable dropping, open-container deposit, host resource stacking, remembered pickup markers, contextual teammate resource text, booster options and team statistics. No MTFO, TheArchive integration framework or standalone replacement DLLs were added.

Review corrections: fixed tiny negative overflow caused by float rounding; matched one booster effect per random slot instead of entire groups; added Harmony class discovery attributes; preserved independent sequence numbers/source precedence for accuracy and damage; refreshed terminal course nodes and native marker dimensions after placement; moved booster updates to the native inventory-changed event instead of detouring its by-ref SDK payload. Used native world HUD markers, with only the optional table/placement cue rendered by a small overlay.

Offline results: **1088 regression assertions**, Release build with zero warnings/errors, and **44 native hook contracts**. Contracts inspect discoverability, target overloads and injected parameter types; they do not execute IL2CPP. The regression suite links the production rules, not a duplicate algorithm.

Live checks still required before replacing standalone mods in a gameplay profile:

- Host and client: drop/pick up each resource and consumable, place in open boxes/lockers, retain uses, reject occupied slots. Two simultaneous placements must not occupy one slot. Closed/full containers must remain unchanged.
- Host stacking: 2+2→4, 4+3→5+2, mixed types, full-pack swap, fractional uses; verify both players see identical counts and checkpoint restores do not duplicate items.
- Ping locally/from a teammate/from a terminal; move beyond 30 m and back, change dimension, ADS/release, pick up/drop/consume/merge, clear one/all, reload checkpoint. No stale or duplicated icons; merge pin transfer across peers needs explicit checking.
- Teammate text at 1080p/1440p/ultrawide: no bold/enlargement while holding guns; emphasize only the correct resource on pack switch. Verify native update/visibility behavior, dead players, bots and deployed-tool reserve semantics.
- Verify perfect-roll effects in inventory **and actual gameplay**, random-slot combinations, unchanged conditions, unknown-template warning. Verify equipped booster counts across successful/failed session and restart; intentional discard still works. Verify artifact reward multiplier without changing zero rewards.
- Two modded peers plus an unmodded peer/bot: misses, pellets, piercing, armored limbs, overkill, sentry/melee damage, delayed packets, late join, final screen and checkpoint/host resets. Confirm missing damage remains `—` without a participating host. EWC projectile accuracy is outside this release's support.

New gameplay modules default off in the distributable. Per-profile deployment can enable them and remove overlapping standalone modules, but installation and offline checks do not replace the live acceptance checks above.

## 2.0.0 review

The initial 2.0.0 draft compiled but was not ready for gameplay validation.

| Finding | Correction |
| --- | --- |
| Stamina used the clamped loss to calculate a refund, producing incorrect costs near zero. A thread-global nesting counter could also survive an exception or mix players. | Scale positive `ActionCost` inputs once in `UseStamina`; keep negative recovery intact. Remove the refund and nesting state. |
| Zero cost did not restore already-depleted stamina or address combat caps. | Restore full local stamina for zero-cost mode, including after native stamina updates. |
| Stamina and charge-recovery hooks had no local-player scope. | Restrict them to the locally owned human, excluding bots and remote players. |
| Friendly-fire documentation described incoming immunity; the patch suppressed every call. | Explicitly suppress local human shots into other players and correct the documented outgoing-only semantics. |
| Aim punch retained a permanent dictionary keyed by camera instance IDs. | Multiply each hit's punch argument, eliminating cache lifetime and repeated-application issues. |
| Sniper overwrote shared recoil block 9; HEL Gun did not restore its recoil definition. | Allocate private, fully populated recoil definitions for both selected weapon presets. |
| Recoil multiplier only changed aiming power while procedural weapon impulses and firing concussion remained. | Scale these amplitudes too, while retaining direction, recovery, spread, and baked animations. |
| Map bounds were expanded on a temporary struct value. | Expand a local Bounds value and assign it back. |
| Map render textures and mesh/command resources leaked on exceptions; render textures were released without destroying their native objects. | Use pooled temporary render textures and `finally` cleanup, and release generated mesh/renderers on success and failure. |
| Float ranges alone allowed non-finite input; hot unload left modified data behind. | Restore defaults for non-finite values and explicitly reject hot unload. |

Verification: Release build against the Temp profile's real interop assemblies, plus 555 offline assertions exercising linked production patch code and R6/R8 golden data. These assertions use managed doubles and cannot prove native detour execution, rendered map correctness, or multiplayer synchronization. Those remain in-game acceptance checks.

In-game acceptance: verify 0/0.5/1 recoil and stamina, melee/jump toggles and charge recovery, repeat damage after camera re-enable, outgoing friendly fire with both host/client roles, both weapon presets with displayed ammo and firing cadence, map rendering across dimensions and checkpoint reload, and normal flashlight toggling/equipment switching.


2.5.1 图标验证：Release 编译零警告／错误，760 项回归断言、197 项图标加载／映射／染色／透明度／缓存检查、44 张 PNG 透明度与边界检查通过；原生契约和安装状态另行验证。小尺寸图预览检查覆盖 32／48／80 像素，未运行游戏或多人验收。


最终程序集原生契约：109 项 QOL hook、44 张嵌入 PNG，以及未修改的 Forge Runtime 249 项 hook 检查通过。没有启动游戏验证 detour 或实际画面。
