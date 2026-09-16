# Development native layout check

用 BepInEx 自带的 Mono.Cecil 读取编译后的宿主、SDK 与 `ForgeDevelopment.Native.dll`，检查 D2 切换后的程序集边界：宿主无诊断类型且不引用 Development；诊断类型只有一份实现；插件只引用 `ForgeRuntime` 与同一份 SDK，不内嵌内核；插件 GUID、版本与 Runtime 依赖；Load 读取 `ConfiguredMode`、`Runtime` 并调用 `PatchAll`；只有四个组件有帧循环（`AuthoringMonitor`、`CaptureMonitor`、`ExperimentRunner` 的 `Update` 与 `PerformanceMonitor`）。

Hook 检查读的是编译后程序集里带 `[HarmonyPatch]` 的静态补丁集合，而不是一个固定数字：诊断批自己的补丁类（`GenerationHooks`、`CombatSampling`）和 capture 批的 `CaptureRegistry` 都在这个集合里，新增一个就在 `Program.cs` 的基准表里加一个名字。跟踪器的补丁不在里面——它由 `BepInEx/config/ForgeDevelopment/trace/*.json` 在运行时用 HarmonyX 手工装载，所以审计断言的是这一点：`RecTracerPatches` 有一对 `Prefix`/`Postfix` 且不带 `[HarmonyPatch]`，`RecTracerRuntime` 调用 `RecTracer.Parse` 与 `Harmony.CreateProcessor` 并暴露 `SetProfileEnabled` 运行时开关。写盘部分断言 `RecSession` 的分段与 gzip 路径，以及通用补丁确实把记录写进 `RecSession.Write`。每个静态补丁的目标仍在本地 GTFO interop 程序集中唯一解析。

参数：`<BepInEx 目录> <ForgeRuntime.dll> <ForgeRuntime.Framework.dll> <ForgeDevelopment.Native.dll> <report.json>`。通常由 `python ForgeDevelopment/scripts/verify-diagnostics.py --bepinex <BepInEx 目录>` 构建输入并运行，报告写 `native-layout.json`。

这是 metadata-only 证据：不执行 GTFO，不证明 detour 安全或运行行为。失败返回非零。
