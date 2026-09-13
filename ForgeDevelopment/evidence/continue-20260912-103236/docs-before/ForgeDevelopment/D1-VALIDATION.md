# D1 诊断接线与验证记录

日期：2026-09-12。最终宿主复跑仍失败：`RuntimeKernel.cs` 的 `ReadThread` 未定义（CS0103，63/65/96/123/146 行，共 5 处）；见 `final-host.log`。下表 CS1513 是此前矩阵那一轮的结果，不能当成最新错误。通过 Remote Desktop Commander 在本机工作树实施；未直接访问 GitHub。

**状态：诊断接线及专项回归已交付；D1/R1 完整宿主集成门槛仍 OPEN。** 不等于 D2 独立插件、完整 Development 或游戏验收完成。

## 实际变更

- `ProjectChecks.Load` 返回该报告实际附着的 scan；严格清单校验和来源采样保留，不恢复旧 Observe/CompleteExpectations 接口。
- `ProjectInspectionSession` 绑定固定 report、scan 和 Runtime worldEpoch。重复开始、扫描前取消、旧世界迟到完成、无世界上下文及部分扫描分别处理。
- `RuntimeDiagnostics` 使用现有公共 Runtime 的 worldEpoch/currentTick；生成、切关、退出清理扫描与来源请求。跨报告或跨世界的旧 JobTrace 不写入新报告。
- `WorldInspection` 遍历使用同一 session；原生实例 ID 与显示路径分开。活动布局只取实际主维度已生成层；额外维度或不完整归属保持未验证/partial。
- Geomorph 的 prefab 名称不是作者来源证明；缺少创建上下文时保留 `creation_context_unverified`，不伪造房间匹配。
- 未配置项目清单时仍可采集原生诊断，但不附加一个空的“成功项目回执”。无效清单仍为 rejected；原生遍历完成也不能覆盖来源 pending/mismatch。

## 本次独立执行

| 检查 | 结果 |
| --- | --- |
| Reports | 99/99 通过 |
| ProjectChecks（包含新增 D1 用例） | 202/202 通过 |
| SceneInventory | 8/8 通过；托管遍历，不是原生耗时 |
| Telemetry | 56/56 通过 |
| Samples | 7/7 通过 |
| 完整 Runtime、Development、Framework、GameBindings | 阻塞：共享 `Framework/RuntimeKernel.cs:198` 报 CS1513（缺少 `}`） |
| Python 离线工具 | 41/41 通过 |

证据：[本批日志与源码收据](evidence/d1-2026-09-12-review/)。`regression-results.json` 保存实际命令和退出码，`source-receipt.json` 保存诊断源码哈希与退役调用检查。首轮失败日志保留，未通过删除断言换取通过。

项目检查新增用例覆盖：同一次 scan 的报告快照、导出文件不回写、旧 world 回调拒绝、取消前后/新 run 隔离、开始前 partial、拒绝清单、未配置清单、未知来源、来源 pending/mismatch、错误 scan 所有者及重复 Start。合成世界只验证 C# 合同，不是 GTFO 实测。

## 现行输入与报告合同

项目根对象必须包含 `format`、`projectId`、`experiment`、`requiredPlugins`、`sources`、`objectReferences`，不接受额外/重复字段。`format` 固定为 `gtfo-forge-project`；不接受 `schemaVersion` 或 `expectedObjects`，不做旧格式 fallback。

`experiment` 必须包含 `packageVersion`、`authoringSha256`、`dependencies`；dependencies 是字符串，可以为空。requiredPlugins 条目为 guid/minimumVersion；sources 为 path/sha256/kind，kind 只接受 DataBlock/LGTuner/ModConfig。hash 为小写 SHA-256。

对象声明仅支持 zone 与 room。Zone locator 明确 layoutId/dimension/layer/localIndex；room locator 为 unique-geomorph-in-zone，明确 zoneAuthorId、room.id/revision 和完整 Assets/ 来源身份。显示名称、Hierarchy 路径、预览模型都不代替创建证据。

ProjectManifest 配置是本地文件选择器，相对路径以 BepInEx 为基准；它与清单内 sources 的安全规则不同。sources 必须是 BepInEx 内的规范相对路径，使用正斜杠，禁止绝对路径、盘符、空段、点段、上级目录和重解析点逃逸。

清单上限 4 MiB，严格 UTF-8/JSON、深度 32。来源扫描上限：256 目录、4096 条目、256 文件、单文件 8 MiB、总计 64 MiB；报告上限 16 MiB。`sourceCoverageStatus=complete` 只表示采样覆盖结束，不表示 sourceVerification=matched，更不证明内存或游戏行为一致。

报告 format 为 `gtfo-forge-diagnostics-report`，objectReferences 独立记录 worldEpoch、simulationTick、scanStatus、sourceVerification、groups 与 overflow。遍历是分帧观察，不是原子世界快照；完整局部遍历也不证明目标/导航/远征可完成。

## 协作与后续

D1 仍由 R1 负责人复核完整宿主集成。共享 Runtime/SDK 正在修改，本批未改写 RuntimeKernel、公共宿主 API 或其他领域行为，也未通过排除报错文件伪造全构建成功。

D2 必须等 R2 的真实 readiness/world/tick 入口和 Off/Play/Authoring 配置迁移合同稳定后，再迁移诊断源、测试、脚本与唯一插件入口。没有为了绕过依赖新建时钟、SDK、空 handler 或第二注册中心。

没有安装到游戏 profile、运行 GTFO、发布、commit、push 或修改网站。Temp profile 仅作为本地构建程序集引用；构建输出在独立临时目录。主客机、连续切关、回调 GC、原生开销和关闭诊断后的玩法验证仍未执行。
