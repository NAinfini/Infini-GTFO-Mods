## 2.5.5 — 2026-09-09

资源 HUD 的无限弹药文字改用字体支持的 ASCII `INF`，修复 U+221E 缺字刷屏。保留 2.5.4 的伤害采集、图标和 HUD 行为。743 项回归及 106 项原生 hook 契约检查通过；尚未完成本版实机验收。

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

## 2.5.1 — 2026-09-09

44 张 256px RGBA 标记图案内嵌 DLL，覆盖资源、消耗品、任务物品和交互设备。熔锁器、驱雾器按确认的结构处理；发电机使用立式电池插槽装置，新增消毒站、舱门控制器、必要门锁、Neonate HSU、硬盘、数据物品与任务箱图案。红／橙／黄／绿荧光棒分开。保留原生透明度和既有显示尺寸，图案本身不再乘类别染色。

完整 66 条拾取物与 7 类设备的图标对应、原生图标保留项及实机边界见 MARKER-COVERAGE.md。新图案不是覆盖所有变体的独立造型；C-Foam 手雷仍保留已有图案。Forge Runtime 不属于此次更新。
# Changelog

## 2.5.0

- Extract performance diagnostics into the independent Forge Runtime 1.0.0 plugin, with its own DLL, config, collector, scene inventory, report script and tests.
- Keep only optional measurement events in gameplay QOL; no consumer means no timing/allocation sampling. Neither plugin requires the other.
- Preserve existing HUD, marker, resource and gameplay behavior. Move seven frame-sampling assertions to the diagnostics tests.
- Forge Runtime 1.0.0 is a diagnostics foundation, not generation validation or a fix for unreplicated R7D2/C_CullingCluster reports.

## 2.4.5

- Respect individual red/yellow/green resource colors and display actual weapon/tool names.
- Repair locker-owned pickup discovery, native selected-pickup discovery, and late quantity updates after drops; fresh drops clear old dismissals.
- Add nine original embedded consumable icons; stop showing fake counts on infinite-use items such as flashlights.
- Use red medical, blue tool and green ammo marker defaults; supplies 40m, consumables 20m, discovered supplies visible in the same zone, with persistent distance labels.
- Preserve discovered CEL/CRYO/CARGO identities while suppressing duplicate native markers; spawning or reading terminal details no longer discovers unseen objectives.
- Offline validation is not in-game acceptance; restart and verify locker/drop, remote pickup, checkpoint and UI behavior.

## 2.4.4

- Bound combat statistics to the native inventory right edge; right-align wide values so damage expands left instead of off-screen.
- Remove extra held-resource icons. Attach resource percentages directly below the native player name so they inherit its movement and scale.
- Release build, 16 HUD checks, 734 regression assertions and 109 native hook contracts are required before installing this package. Actual Unity visuals remain pending in-game acceptance.
- Performance review only: this release does not claim to fix the unassigned 270 ms hitch or CPU/GPU profiling availability.

## 2.4.3 (upstream-lifecycle repair; live acceptance pending)

- Rebind resource HUD text/icons when the same owner receives a new native NavMarker; destroy old clones, restore their native alpha, and refresh from PlaceMarker. Reuse the text builder and stop marking native info dirty from rendering.
- Route nearby discovery, player ping and received impact pings through shared pickup/device identity; register terminal screen colliders for native ping and observe terminal PlayPing. Preserve manually hidden entries in checkpoint snapshots.
- Rebuild deposit slots independently on checkpoint recall, even without marker memory. Finalize pickup registration after SpawnItem, restore native container association and discover late-spawned contents of already-open containers. Occupancy, parenting and removal use one resolved slot and a reverse occupant index; container callbacks update only their own slots.
- Restore StatDisplay's native Background Fade row/content/text hierarchy instead of positioning a text clone in a different parent. Preserve user offsets. Track melee/sentry/mine damage sources explicitly; resume collection after failed-checkpoint continuation without clearing the attempt.
- Compile custom statistics formats once; suppress unchanged per-recipient snapshots and repeated completed handshakes. Record a snapshot as published only after the send returns. No benchmarked FPS improvement is claimed.
- Complete the checked NoInterruptions mine pickup interaction offset and missing interaction-sound origin guard. Keep native terminal validation, bounded command queue and cleanup behavior.
- Release build: zero warnings/errors. Passed 734 regression assertions, 13 HUD-view, 19 marker-registration, 32 deposit-slot, 12 terminal-runtime checks and 109 native hook contracts. These do not execute a played Unity scene or certify model placement, visuals or multiplayer. See UPSTREAM-ACCEPTANCE.md for remaining limits, including the unresolved native Actions packet mismatch.

## 2.4.2 (candidate; not installed or live-validated)

- Fixed the logged terminal SpawnNode null-setter exception. Terminal availability no longer incorrectly depends on its MonoBehaviour update being enabled.
- Container opening now discovers contents; item availability follows pickup state instead of graphics activity. Previously carried items are remembered after native placement completes. Unregistered terminal components cannot become generic pins attached to held resource models. Normal doors and storage shells remain excluded.
- Replaced the custom proximity/LOS slot search and key timer with native Interact_Timed compartment interactions, using the stock six-locker/three-box volumes checked against DropItemPlus. Native selection, prompt and hold progress own deposit; exact slot occupancy follows pickup/placement/despawn and checkpoint rebuild. Unsupported compartment layouts are logged, not guessed. Preview excludes inactive mesh variants.
- Stats HUD now has an explicit right pivot/alignment and local placement instead of inheriting the centered weapon-label rectangle. Own GTFO-API hello/ping sends exclude bots and the local player; this does not claim to fix the separate Actions packetIndex=47 receive error.
- Spatial discovery excludes world-only colliders; marker summaries include missing-node/not-placed counts. No measured FPS gain or full upstream equivalence is claimed.

## 2.4.1 (candidate; not live-validated)

- Compared ItemMarker's concrete Reactor OnBuildDone, generic-terminal key update and missing-area-node handling. Added reactor terminal zone/marker registration and shared area-node repair without overwriting native authored nodes or revealing undiscovered devices.
- Reconfigured computer metadata now uses the shared device binding path. Unchanged Setup avoids visual recreation; changed anchors preserve existing explicit discovery. Updated IDs remove old QUERY mappings; destruction only removes mappings still owned by that object, protecting replacements.
- Kept existing native Setup hooks for generators, disinfection stations, HSU/activators, controllers and required door locks. No extra per-frame/global scanner and no normal-door/container-shell markers.
- EWC custom-weapon statistics are explicitly out of scope per the user, not a pending feature. Source registration tests cover 16 cases; in-game/multiplayer acceptance remains pending.

## 2.4.0 (candidate; not installed or live-validated)

- Rewrote combat statistics in Infini source after comparing StatDisplay 1.1.8; no embedded/loaded original DLL. Removed the replaced statistics implementation. Compact native HUD defaults 100 units right; success uses native per-slot results, failure uses short native-font player columns. Damage, hit rate and weakspot-hit rate remain distinct.
- Added pellet/trigger-group/full-piercing counts, documented custom format subset, and recipient/session-bound v5 synchronization. Rejects stale snapshots, unauthorized human statistics and invalid counts; host restarts clear its bot counts too. EWC, original protocol/preset parity and per-weapon-instance history are not implemented.
- Removed own chat and weapon-description code, settings and tests per the final user choice: Archive ChatTweaks/WeaponStats remain the providers; Core owns ping helper and ResourceStack.
- Terminal input is synchronized before native exit clears ownership. Reentrant terminal state transitions preserve newly registered watches. Sustained interaction restores prior native control flags in a finalizer rather than blindly enabling them.
- Release build passed with zero warnings/errors; 733 regression assertions, 12 production terminal-hook checks, 9 production resource-HUD view checks and 91 native hook contracts passed. Unity rendering, checkpoint and multiplayer acceptance remain separate live checks, not implied by these counts.

## 2.3.1 (candidate; not live-validated)

- Matching held-pack HUD now uses separate native text/icon clones: ammo shows Main/Special, medical shows HP, tool shows Tool and disinfect shows Infection. Reserve colors continuously interpolate empty red → half yellow → full green; infection reverses the scale. Native refresh scheduling remains active while a pack is held. Names, distances and resource fading are independent; generic teammate carried inventory is removed.
- Discovered world items fold to icon-only at range unless focused; nearby details remain visible, with a short focus-release delay. Carry objectives retain their native category icon and carrier name, follow the carrier and reanchor on drop. No terminal-occupant names; no locker, box or normal-door persistent markers.
- Replaced whole-registry proximity discovery with reusable nearby collider queries at 4 Hz, deduplicating multiple colliders per object and growing saturated buffers without silently losing candidates. Added spatial workload counters; no measured FPS gain is claimed.
- Added same-thread managed allocation totals/maxima per measured scope and separated HUD presentation, placement presentation and terminal-maintenance timings. Allocation totals are not live/native memory and nested scopes must not be summed. Updated moved resources' terminal zone text alongside their node.
- Offline checks: 795 regression assertions, 9 production HUD-view checks, 87 native hook contracts and 6 performance-report tests passed. These do not execute Unity rendering, IL2CPP detours or multiplayer movement; live acceptance remains pending. See HUD-BEHAVIOR.md and REPLACEMENT-AUDIT.md for exact scope and remaining upstream gaps.

## 2.3.0 (candidate; not live-validated)

- Removed unchanged marker title/style/color/scale/alpha/visibility submissions. Ordinary marker lifecycle checks run at 10 Hz; focus/aim changes and native pickup/count hooks remain responsive, while native projection follows objects every frame. Added discovery/refresh timings and marker workload counters.
- Replaced repeated global scene-component queries with one cooperative object/component traversal. Kept native asset inventories, measured atomic enumeration and slow scan stages, and documented the changed scene-only component scope. The 2 ms budget remains cooperative, not a hard cap.
- Retry an empty native sampler registry once on level entry; record an already-enabled profiler's settings without guessing its owner or taking control. Missing native FrameTiming support remains an explicit limitation. Extended the log analyzer and isolated scene-inventory tests.
- Includes previously prepared ordinary-door exclusion, transient-ping ownership, discovered checkpoint memory, dynamic-device hooks, per-item settings, terminal fixes, weapon-description pages and reference breakpoints, and v4 per-weapon/weakspot-damage statistics with custom formats. These are selected integrations, not full upstream parity; see REPLACEMENT-AUDIT.md.
- Keeps earned booster currency multiplication (default/max 100000), perfect existing rolls and nonconsumption. No fixed rewards, condition/negative removal or custom booster editor. Existing profile settings are not automatically overwritten.
- Source-level performance comparison, offline call-count results and outstanding live acceptance are in PERFORMANCE-REVIEW.md. No FPS gain or complete CPU/GPU attribution is claimed before live measurement.

## 2.2.9

- Confirmed consumables use the existing discovered-item pipeline, native consumable icon, name/count/distance and independent range/color (default 12 m). Locker/box exclusion does not exclude their discovered contents.
- Added the native ConsumablePickup_Core.OnCustomDataUpdated callback so stack changes refresh the marker immediately. Shared depleted-item filtering now covers finite-use consumables as well as resource packs at discovery, update and cleanup; infinite-use items and objectives are not rejected for having no ammo counter.
- Preserves 2.2.8's pickup removal, carry-objective following and native-marker deduplication. No second marker subsystem or additional mod dependency.

## 2.2.8

- Corrected category visual flags that overwrote medical/ammo/tool/device icons with the generic loot square. Removed locker/box persistent markers and their category configuration; discovered contents remain eligible.
- Suppressed duplicate native carry-objective icons/distances only while our replacement is visible, retaining native visibility requests for restoration. Native carry synchronization tracks cryo/cells on remote players and reanchors on drop; completed objectives disappear and your own carried objective has no extra custom icon. Resource pickup removal uses retained sync identity before native item exchange and explicit visual teardown.
- Registered computers through their actual terminal interface and native interaction anchor; local/remote terminal use discovers the target through native replication. Device-owned colliders no longer block discovery; unrelated walls and dimensions still do. Existing disinfection/HSU/generator/controller/lock availability checks remain.
- Matched Dinorush's compact grey hit/yellow weakspot/parenthesized damage layout. Corrected weakspot percentage to weakspot hits divided by hits, as explicitly chosen by the user. Preserved native anchors and configurable offsets.
- Matched HUDInfoPlus's native infinite-ammo flags and missing-slot filtering, retaining held-pack-only information and deployed-sentry ammunition.
- Fixed the observed placement-preview IL2CPP Dictionary.TryGetValue MissingMethodException by using the native item-prefab accessor. Scoped resource container/culling repair to resources and consumables so it cannot reparent native carry objectives.
- Added source-version/method evidence, intentional differences, remaining upstream gaps and live acceptance cases in FEATURE-COMPARISON.md and REVIEW.md. These are selected integrated capabilities, not full parity with every upstream feature.

## 2.2.7

- Supersedes the undeployed 2.2.6 external-provider plan: compact statistics are now internal to InfiniTweaks.dll alongside boosters, resource HUD and item markers. No standalone StatDisplay installation.
- Native inventory-anchored player-color rows show hit percentage, weakspot-hit percentage and effective enemy damage. Configurable scale/offsets; native success/failure text integration preserves game reports and artifact information. No independent panel, F7 binding or IMGUI dependency.
- Owner/bot shot collection, once-per-pellet piercing/weakspot counting and host-authoritative damage use the plugin's validated cumulative v3 snapshots. New-attempt/reset/leave handling and late result synchronization are covered by production-collector regression tests. No unmodded remote-human accuracy estimates are presented as exact.
- Retains integrated booster rolls/consumption protection/reward multiplication, held-pack resource HUD and discovered category/device markers. Hikaria Core continues to own ResourceStack.

## 2.2.6

- Removed the independent statistics panel and its F7/Shift+F7 controls. Statistics are now delegated to the original Dinorush StatDisplay, with compact native HUD text and native success/failure display.
- Removed the duplicate accuracy/damage collector, session/snapshot protocol, statistics settings, result callbacks, IMGUI rendering dependency and corresponding obsolete tests. No hidden or disabled second implementation remains.
- Kept resource HUD, item markers, placement, boosters and performance diagnostics independent of the statistics provider. StatDisplay remains a separate mod, not embedded in this DLL.

## 2.2.5

- Fixed statistics missing the success/failure screens when the game transitions directly back to the lobby without AfterLevel. The statistics lifecycle and renderer now share one visibility rule.
- Kept the completed attempt available in the return lobby until the next expedition. Later result transitions no longer reopen a manually dismissed panel.
- Continued final snapshot/handshake exchange during result states and refreshed late peer data immediately. Initialized the roster before first display and refreshed it before incoming snapshots.
- Initialized statistics before unrelated level-entry registries; added reset, panel-toggle and final-result diagnostics. Native nickname control characters cannot introduce table rows.
- Added an explicit unavailable-damage explanation when no compatible host statistics participate. Added linked-production tests for shot/ray/damage collection, F7/details, network handlers and complete result/reset lifecycle.

## 2.2.4

- Fixed disabled Resource HUD still hiding native extra information on held-item changes. Disabled mode now makes no changes; turning it off restores the latest native visibility request and native text. Marker destruction and checkpoint cleanup release owned state.
- Added native category icons, individual category colors/ranges and resource/consumable counts. Resource range defaults to 35 m, ordinary consumables 12 m; targeted player/terminal pings extend the range to 60 m. Zero category range disables it even for explicit pings.
- Added state-aware terminal, generator, disinfection station, HSU/sample, bulkhead controller and door-lock markers. Door locks track the missing requirement; completed/inactive interactions are hidden. Discovery and dimension restrictions remain.
- Added living-teammate ADS/angle-based opacity and held-pack-only inventory use counts; no extra information when holding weapons. Downed-player indicators remain opaque. Matching health/ammo/tool/infection values remain enlarged colored percentages.
- Read incoming placement state directly and cleared the container preview on checkpoint reload.
- Included the measured performance follow-up: incremental asset scanning, timing deduplication, state-separated frame summaries, capture scheduling and a standard-library log analyzer. Retail CPU/GPU availability and exact per-object attribution remain explicit limitations.
- Packaged the existing 256×256 icon at the archive root. r2modman local imports need a version-matched cache as well as the installed plugin icon.

## 2.2.3

- Removed duplicate resource stacking, its settings, merge-notification protocol and obsolete tests. Hikaria Core's ResourceStack owns stacking; drop/deposit and item visibility remain independent.
- Changed additional teammate percentages to held-pack-only context while preserving native names/icons. Matching resources are enlarged, bold and color-coded; low values use a shortage color. Switching away clears the extra text immediately.
- Fixed post-native HUD text submission and initial roster refresh; removed the obsolete normal-resource-text scale.
- Added proximity/line-of-sight discovery for pickups and named terminal objects, explicit terminal QUERY/PING discovery and native terminal proximity discovery. Existing distance, ADS, picked-up and dismissal filtering remains.
- Restored pickup course-node registration on native sync/container spawn paths. Open-slot targeting now uses the slot geometry rather than requiring an empty container collider hit; placed resources retain container association.
- Refreshed item culling bounds and cached rendering/shadow commands after placement so moving an existing pickup does not leave its visual cached at the old state.
- Replaced the tiny placement plus and IMGUI text with a translucent pickup-mesh preview and native localized interaction prompt. Widened slot snapping and stabilized selection between adjacent anchors.
- Read deployed sentry ammunition from the actual sentry, returning to backpack ammunition after pickup.
- Added exact authored historical booster-template matching for persistent older inventory, including templates 26 and 38. Logged matching results, blocked consumption and earned-currency multiplication.
- No automatic migration/uninstallation of standalone mods. In-game rendering, placement, replication and booster persistence still require live acceptance; see REVIEW.md and FEATURE-COMPARISON.md.

## 2.2.2

- Fixed performance diagnostics failing at startup because generated IL2CPP metadata did not initialize the `FrameTiming` value-type class pointer before allocating its native array.
- Made FrameTiming initialization and runtime sampling optional: a missing/failed CPU/GPU timing channel now logs `frame_timing_status available=False` while frame, memory, sampler, scene and profiler-capture diagnostics continue.

## 2.2.1

- Changed CPU/GPU frame timing from a single latest value to interval distributions with sample count, average, P95, P99 and maximum.
- Added per-summary texture-streaming memory, pending/loading texture and mip-upload metrics.
- Expanded continuous Unity timing coverage to 192 prioritized samplers while retaining a complete sampler catalog in the log.
- Enabled and restored all 13 profiler areas during bounded `.raw` captures.
- Expanded level/F9 snapshots with every IL2CPP component type count, renderer visibility and geometry proxies, material/static-batch/shadow state, cameras, lights, particles, audio sources, animators, rigidbodies/colliders and UI canvases.

## 2.2.0

- Added default-on, asynchronous performance logs with frame percentiles, slow-frame counts, CPU/GPU timing, process and Unity memory, GC activity, native sampler summaries and measured Infini Tweaks hot paths.
- Added automatic bounded Unity `.raw` profiler captures after sustained hitches, plus an F9 manual capture. Unsupported retail-profiler builds are reported explicitly.
- Added level-start and F9 inventories for loaded textures, meshes, materials, audio clips, skinned meshes and active scene components, including scanner self-cost.
- Added installed-plugin and Harmony patch-owner inventories so later logs preserve the exact mod environment.
- Added frame-window regression coverage and documented the boundary between asset memory proxies, aggregate native timings and per-call attribution.

## 2.1.1

- Fixed accuracy freezing after reconnect: each tracking session negotiates a receiver-issued challenge; old snapshots and confirmations cannot replace the new stream.
- Removed the participating-host requirement for exact owner accuracy sharing. Host damage and estimates retain separate authority and reset without erasing exact peer accuracy.
- Replaced nearest-position pickup guessing with the native ping target object, terminal item ID and exact shared pickup SyncID. Coordinate-only pings remain vanilla; closed-container contents are not remembered.
- Added multi-peer protocol regressions for reconnects, checkpoint resets, delayed packets, unmodded hosts and host migration. Updated setting descriptions and protocol requirements. Existing configuration values are unchanged.

## 2.1.0

- Added independently configurable native resource/consumable dropping and open-container deposit with a placement cue.
- Added host-side same-type resource merging with a 1–5 use cap, fractional-amount conservation and retained overflow.
- Added remembered player/terminal pickup pings with range/dimension filtering, ADS hiding, native icon styling and clear controls.
- Added compact teammate resource information that only enlarges/bolds the relevant resource while holding its pack.
- Added current-template perfect booster rolls, independent equipped-booster non-consumption, and configurable earned artifact-currency multiplication.
- Added optional team accuracy/damage tables, weapon-category details, current-attempt lifecycle and explicit estimated/unknown values.
- Added ordered cumulative stats snapshots, random-slot booster matching and regression/native-interface checks.
- Preserved previous settings and defaults. New modules default off; live multiplayer/rendering/booster acceptance is pending, not implied by the build.

## 2.0.0

- Renamed the package to Infini Tweaks and kept it as one DLL with no MTFO dependency.
- Added numeric recoil, aim-punch, and stamina-cost multipliers.
- Added toggles for melee charge recovery, melee stamina cost, jump stamina cost, and teammate bullet damage.
- Added optional Better Maps rendering, accessible-area filtering, orientation correction, and icon priority.
- Added selectable official Original R6 and current R8 HEL Gun and Sniper presets.
- Corrected stamina scaling before clamping and limited it to the local human player.
- Added isolated full recoil definitions for weapon presets and scaled procedural firing recoil with the global multiplier.
- Scoped teammate protection to local outgoing bullets and made aim-punch scaling per hit.
- Corrected map bounds writeback and guaranteed temporary render-resource cleanup.
- Added historical-data and player-patch regression coverage.

## 1.0.0

- Added configurable positive or negative flashlight range adjustment.
- Added configurable positive or negative flashlight cone-angle adjustment.
- Added optional local first-person flashlight sway removal.
- Preserved GTFO's native flashlight toggle, equipment, detection, and network state handling.
