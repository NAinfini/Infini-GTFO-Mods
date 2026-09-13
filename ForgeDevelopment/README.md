# ForgeDevelopment

可选的作者诊断包：观察生成、空间、性能、异常与运行证据，关联到作者对象并输出报告。

**普通玩家默认不依赖这个包。** 它不调度正常玩法，也不把"没有报错"解释为地图正确。按总案第 1 节的定位，需要玩家自己启用诊断回传才能用起来的能力不算完成——诊断是作者型用户的第二层入口，不是玩家必经路径。

仓库整体状态见 [ARCHITECTURE.md](../ARCHITECTURE.md)，未完成批次见 [IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md)，验证结果见 [VALIDATION.md](VALIDATION.md)。

## 当前状态：骨架，源码还在 Runtime 里

`ForgeDevelopment.csproj` 只引用唯一的 `ForgeRuntime.Framework` SDK，`ModuleDefinition.Create()` 用既有的公开 `RuntimeModule` 合同登记一个**空 provider**：没有可执行的 capability、binding、handler，没有自动加载、Hook、定时任务或安装。

**全部诊断实现仍在 `ForgeRuntime/` 里**，等 D2 迁过来。这是 B 批唯一没做完的事，见 [ARCHITECTURE.md 第 4 节](../ARCHITECTURE.md#4-批次位置)。

D1 已经交付：诊断扫描接线修好了（`ProjectChecks.Load` 现在带 worldEpoch，`WorldInspection` 不再调用退役的 `Observe` / `CompleteExpectations`），报告在入队时捕获不可变快照，完整宿主构建通过。

## D1 修好的具体行为

`ProjectChecks.Load` 返回该报告实际附着的 scan，严格清单校验和来源采样保留。`ProjectInspectionSession` 绑定固定的 report、scan 与 Runtime worldEpoch，分别处理重复开始、扫描前取消、旧世界的迟到完成、无世界上下文和部分扫描。`RuntimeDiagnostics` 使用现有公共 Runtime 的 worldEpoch 与 currentTick，在生成、切关、退出时清理扫描与来源请求；跨报告或跨世界的旧 JobTrace 不写入新报告。`WorldInspection` 的遍历使用同一个 session，原生实例 ID 与显示路径分开；活动布局只取实际主维度的已生成层，额外维度或不完整归属保持未验证或 partial。

**Geomorph 的 prefab 名称不是作者来源的证明。** 缺少创建上下文时保留 `creation_context_unverified`，不伪造房间匹配。未配置项目清单时仍可采集原生诊断，但不附加一个空的"成功项目回执"；无效清单仍然 rejected，原生遍历完成也不能覆盖来源的 pending 或 mismatch。

报告在 Enqueue 时就捕获不可变的托管证据：metadata、事件与检查、异常与聚合计数、overflow、objectReferences 与导出时间来自同一次捕获。新世界、扫描完成、来源验证完成或之后的取消，都不能改写已经排队的旧快照。序列化、字节预算裁剪和原子写盘仍在 writer 线程，后台不读取 Unity 或 IL2CPP 对象。队列保留 8 个 pending 路径上限；同路径替换整个快照，满队列的新路径明确拒绝。

`ShutdownSequence` 现在会继续执行所有清理阶段并返回一份只读、有序的失败收据，一个阶段抛异常不阻止其余阶段。

## 现行输入与报告合同

项目清单的根对象必须包含 `format`、`projectId`、`experiment`、`requiredPlugins`、`sources`、`objectReferences`，不接受额外或重复字段。`format` 固定为 `gtfo-forge-project`；**不接受 `schemaVersion` 或 `expectedObjects`，不做旧格式 fallback**。

`experiment` 必须包含 `packageVersion`、`authoringSha256`、`dependencies`，dependencies 是字符串可以为空。`requiredPlugins` 条目是 guid 与 minimumVersion；`sources` 是 path、sha256、kind，kind 只接受 DataBlock / LGTuner / ModConfig；hash 是小写 SHA-256。

对象声明只支持 zone 与 room。Zone locator 明确 layoutId、dimension、layer、localIndex；room locator 是 unique-geomorph-in-zone，明确 zoneAuthorId、room 的 id 与 revision 和完整的 `Assets/` 来源身份。**显示名称、Hierarchy 路径、预览模型都不代替创建证据。**

`ProjectManifest` 配置是本地文件选择器，相对路径以 BepInEx 为基准；它与清单内 `sources` 的安全规则不同——sources 必须是 BepInEx 内的规范相对路径，用正斜杠，禁止绝对路径、盘符、空段、点段、上级目录和重解析点逃逸。

清单上限 4 MiB，严格 UTF-8 与 JSON、深度 32。来源扫描上限：256 目录、4096 条目、256 文件、单文件 8 MiB、总计 64 MiB；报告上限 16 MiB。`sourceCoverageStatus=complete` **只表示采样覆盖结束**，不表示 `sourceVerification=matched`，更不证明内存或游戏行为一致。

报告的 format 是 `gtfo-forge-diagnostics-report`，`objectReferences` 独立记录 worldEpoch、simulationTick、scanStatus、sourceVerification、groups 与 overflow。遍历是分帧观察，**不是原子世界快照**；完整的局部遍历也不证明目标、导航或远征可以完成。

## 复跑

```powershell
dotnet build ForgeDevelopment/ForgeDevelopment.csproj -c Release
dotnet run --project ForgeRuntime/tests/Architecture/Architecture.csproj -c Release
python ForgeDevelopment/scripts/verify-diagnostics.py --bepinex "$env:GTFO_BEPINEX_PATH"
```

`verify-diagnostics.py` 默认新建系统临时目录，也可以指定一个尚不存在的 `--output`；源码变化或任何失败都返回非零。完整的套件清单与结果见 [VALIDATION.md](VALIDATION.md)。

诊断源码迁过来之后，测试路径必须随迁移更新——**不能把计划里的未来路径当成已存在的命令**。

## 边界

当前 DLL 不应作为玩家发行物。真实的 BepInEx 入口、发布身份、游戏依赖、原生能力和多人验证都在后续计划中。没有 GTFO 加载、多人、原生 Hook 安全或采集开销的验收。InfiniTweaks 的 QoL 行为不属于本模块。
