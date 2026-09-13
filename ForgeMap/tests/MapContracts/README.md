# Map SDK 身份边界测试

本工程引用真正编译的 `ForgeMap` 与 `ForgeRuntime.Framework`，不链接另一模块的私有源码。
`TestWorld` 是明确的测试替身：提供已区分的字符串实例 ID 和测试生命期表，
测试实际 SDK 的注册、执行前重新验证、去重、世界切换、取消、权限与客户端拒绝。
它不证明 GTFO 原生对象能被解析，也不证明网站作者 ID 已映射到这些实例。

生产 `ModuleDefinition` 仍是空 provider；合成 capability、handler 和对象表只在测试进程中。
`ForgeMap.csproj` 排除 `tests/**/*.cs`，测试同时验证替身没有进入正式 Map 程序集。
没有加载游戏 DLL、Harmony/BepInEx 插件或游戏 API；成功不提高 binding 验证等级。

从模组仓库根运行，构建输出隔离在 Map 自己的忽略目录：

```powershell
$artifacts = Join-Path (Resolve-Path ForgeMap) 'bin/map1-artifacts'
dotnet build ForgeMap/tests/MapContracts/MapContracts.csproj -c Release --artifacts-path $artifacts
dotnet "$artifacts/bin/MapContracts/release/MapContracts.dll"
```

公共 SDK 有并发未完成改动时，完整构建应真实失败；不能引用旧 DLL 来伪装当前构建通过。
原生身份十个待实施场景见 [native-identity-scenarios.json](../fixtures/native-identity-scenarios.json)。
它们是未来原生 resolver/创建 Hook 的验收输入，**没有由本测试执行或标为通过**。
