# Development plugin startup regression

编译生产的 `Native/Plugin.cs`；宿主 `ForgeRuntime.Plugin`、诊断设置、`RuntimeDiagnostics`、两个 monitor、capture 注册表与实验组件、BepInEx、Unity 与 Harmony 都是托管替身。只测启动编排，不注入游戏。

覆盖：Runtime 为 Off 或 Play 时只记 inactive 日志；Authoring 但宿主 Runtime 为 null 时抛出且无副作用；精确启动顺序（记录器 → Hook → 作者 monitor → capture → 实验 runner/面板与命令装载 → 性能 monitor）；关闭性能日志时不加 PerformanceMonitor；单次 Load，inactive 的 Load 不能被重试成 active；各阶段失败时清理与已获取阶段一致（实验组件 → capture → 记录器 → Hook → 其余组件 → 诊断停止最后执行）、所有已加组件都被销毁；单个清理失败不阻止其余清理；异常 Data 不可写时保留原始异常。

`LoaderDouble.cs`/`LoaderModes.cs` 另外按真实的 `[BepInPlugin]`/`[BepInDependency]` 元数据模型加载器门槛：开发包未安装、宿主缺失或低于声明要求时不构造插件、不产生任何诊断副作用；只有开发包类型被加载（未调用 Load）时同样零副作用；宿主已装但 Runtime 为 Off/Play 时只记 inactive；Authoring 走完整启动；软依赖 InfiniTweaks 缺失可接受，进程内注入的旧版（无 `InfiniTweaks.Telemetry`）被拒绝且不获取任何阶段。加载器本身、程序集扫描与原生 Hook 未执行，游戏内三种加载模式仍需实机确认。

从仓库根目录运行（`verify-diagnostics.py` 也会运行它）：

```powershell
$out = Join-Path $env:TEMP ('forge-dev-startup-' + [guid]::NewGuid().ToString('N'))
dotnet build ForgeDevelopment/tests/PluginStartup/PluginStartup.csproj -c Release --artifacts-path $out
if ($LASTEXITCODE -eq 0) { dotnet "$out/bin/PluginStartup/release/PluginStartup.dll" }
```

失败返回非零；退出码 0 表示 103 项断言全部通过。结果记录见 [Development 验证记录](../../VALIDATION.md)。
