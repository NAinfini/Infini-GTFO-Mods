# ForgeDevelopment

作者诊断、性能/空间检查、错误聚合与报告。正常玩家默认不依赖此包。

当前状态：**可编译的架构骨架，尚无独立游戏插件入口或新增玩法**。`ForgeDevelopment.csproj` 引用唯一 `ForgeRuntime.Framework` SDK；[ModuleDefinition.cs](ModuleDefinition.cs) 使用既有公开 RuntimeModule 合同登记一个空 provider。没有可执行 capability/binding/handler，没有自动加载、Hook、定时任务或安装。

诊断源/测试/脚本仍暂留 `ForgeRuntime`。D1 的真实扫描接线及诊断专项回归已交付，完整宿主集成门槛仍 OPEN；最新阻塞是共享 SDK 的 ReadThread 缺失。见 [D1 验证与交接](D1-VALIDATION.md)。尚未完成 D2 独立插件迁移。

完整职责、分阶段任务、输入输出、失败处理、生命周期、测试与机制工作队列见 [详细实施计划](IMPLEMENTATION-PLAN.md)。接手前读 [仓库架构](../ARCHITECTURE.md) 和 [Agent 交接](../AGENT-HANDOFF.md)。上位依据是网站 [唯一总案](../../Infini-GTFO-Model-Site/Docs/rework/FORGE-COMPLETE-PLAN.md)，本目录不另立能力目录。

在模组仓库根目录验证：

```powershell
dotnet build ForgeDevelopment/ForgeDevelopment.csproj -c Release
dotnet run --project ForgeRuntime/tests/Architecture/Architecture.csproj -c Release
```

本轮整体架构构建和 35 项边界检查通过，具体记录见仓库架构。本项目当前 DLL 不应作为玩家发行物；真实 BepInEx 入口、发布身份、游戏依赖、原生能力和多人验证均在后续计划中。InfiniTweaks 的 QoL 行为不属于本模块。
