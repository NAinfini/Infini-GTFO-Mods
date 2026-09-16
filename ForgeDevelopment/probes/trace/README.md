# probes/trace — 原生方法跟踪配置

这些文件是 `RecTracer` 的配置，运行时从 `BepInEx/config/ForgeDevelopment/trace/*.json` 读取。加一个点
= 加一行配置，不需要改代码、不需要重新发版。字段含义见仓库 `Native/RecTracer.cs` 的解析器。

## 约定

- **类型名以本机 interop 程序集为准**。全局命名空间里的类型（`GameStateManager`、`LG_PowerGeneratorCluster`、
  `ItemEquippable`、`Interact_*`、`GuiManager`、`NavMarker*`、`Dam_*`、`SurvivalWave`、`Mastermind`、
  `CellSound`、`M_*`）直接写短名，不要凭直觉加 `LevelGeneration.` / `Gear.` 前缀；带命名空间的类型写全名
  （`LevelGeneration.LG_SecurityDoor`、`Enemies.EnemyAgent`、`SNetwork.SNet_Packet`、`ChainedPuzzles.CP_*`）。
  写错的类型名与写错的方法名都会被 `tests/Capture` 的 Cecil 检查列出来。
- **泛型类型定义不写进 profile**。运行期 `RecTracerRuntime.FindTypes` 会跳过泛型类型定义与接口，所以
  `SNetwork.SNet_Packet\`1`、`SNetwork.SNet_StateReplicator\`1` 这类名字永远装不上；非泛型的基类
  （`SNetwork.SNet_Packet`、`SNetwork.SNet_ReplicationManager`）才是能装的目标。
- **默认开启的 profile 要能在正常一局里跑得动**。`Update` / `LateUpdate` / `FixedUpdate` / `OnGUI` 被跟踪器
  内置拒绝表挡住，不要写进方法列表；高频方法用 `"record": "count"` 加 `maxPerSecond`。
- **泛型方法与带指针/泛型参数的方法会被拒绝**，拒绝结果写进 `session` 通道的 skip 列表，不是静默丢弃。
  用通配符匹配到这类重载是正常的；把**具体某个这样的方法名**写进 `methods` 才是配置错误。
- `record` 只能是 `count` / `args` / `args+result` / `changes`。
- `instance` 是 `RecReflect` 读的字段路径，可以穿私有字段（例如 `m_sync.m_stateReplicator.State`）。
  路径读不到就写 null，不会抛异常。
- `maxPerSecond` 超出后按 `onOverflow`（`count`）处理，并每 5 秒写一条 `tracer/overflow` 记录。

## profile 一览

| profile | 默认 | 覆盖 |
| --- | --- | --- |
| `level` | 开 | GameStateManager / RundownManager / WardenObjectiveManager / WorldEventManager / CheckpointManager / Dimension / ElevatorRide / LG_Factory / 链式谜题 / 关卡交互管理器 |
| `door-terminal` | 开 | 安全门、弱门与弱门破坏、弱锁、门按钮、终端与终端管理器、命令解释器状态、HSU、隔断门控制器、资源容器、交互组件 |
| `objects` | 开 | 发电机与发电机集群、拾取物与关卡物品、资源容器、`Interact_*` 全族、玩家交互 |
| `player` | 开 | PlayerAgent、伤害基类与部位、背包 / 弹药 / 物品栏、locomotion 状态切换、PlayerSync、聊天与语音、复活、AgentModifierManager |
| `weapon` | 开 | BulletWeapon / Shotgun / Weapon / ItemEquippable / 近战 / 哨戒炮 / 绊雷 / 胶枪 / 生物追踪器 / 投掷物 / ArchetypeDataBlock |
| `enemy` | 开 | EnemyAgent / EnemyAI / EnemyBehaviour / ES_* 状态进出与攻防窗口 / 部位伤害 / 分配器 / 敌群 / 检测 / EnemySync |
| `damage` | 开 | `Dam_EnemyDamageBase` 与 `Dam_PlayerDamageBase` 的伤害入口、`Dam_EnemyDamageLimb`、`SurvivalWave`、`Mastermind`、静态敌人伤害 |
| `gui` | 开 | GuiManager / NavMarkerLayer / NavMarker / PlaceNavMarkerOnGO / PlayerGuiLayer / PUI_* / CM_* / WardenIntel |
| `environment` | 开 | EnvironmentStateManager / LG_Light 全族 / 雾与雾球 / 除污 / CellSound（默认只计数）/ 动画序列器 |
| `net` | 开 | 包与包缓冲收发、状态复制器、复制管理器、SNet_SyncManager、SNet_Capture、SNet_Player、`M_*` 序列化 |
| `everything` | **关** | 按命名空间通配（不含 `*` 兜底条目），只在需要地毯式确认时用 `SetProfileEnabled("everything", true)` 打开 |

## 加一个新点

```json
{ "type": "LevelGeneration.LG_SomeType", "methods": ["SomeMethod"], "record": "args+result",
  "instance": ["m_field"], "maxPerSecond": 20, "onOverflow": "count" }
```

放进对应玩法域的 profile 即可。若类型/方法名写错，`tests/Capture` 会报出具体是哪条。
