# Development native layout check

用 BepInEx 自带的 Mono.Cecil 读取编译后的宿主、SDK 与 `ForgeDevelopment.Native.dll`，检查 D2 切换后的程序集边界：宿主无诊断类型且不引用 Development；诊断类型只有一份实现；插件只引用 `ForgeRuntime` 与同一份 SDK，不内嵌内核；插件 GUID、版本与 Runtime 依赖；Load 读取 `ConfiguredMode`、`Runtime` 并调用 `PatchAll`；只有两个 monitor 有 Update 循环；Hook 集合精确为 10 个，每个目标在本地 GTFO interop 程序集中唯一解析。

参数：`<BepInEx 目录> <ForgeRuntime.dll> <ForgeRuntime.Framework.dll> <ForgeDevelopment.Native.dll> <report.json>`。通常由 `python ForgeDevelopment/scripts/verify-diagnostics.py --bepinex <BepInEx 目录>` 构建输入并运行，报告写 `native-layout.json`。

这是 metadata-only 证据：不执行 GTFO，不证明 detour 安全或运行行为。失败返回非零。
