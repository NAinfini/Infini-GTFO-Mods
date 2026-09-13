# Development plugin startup regression

编译生产的 `Native/Plugin.cs`；宿主 `ForgeRuntime.Plugin`、诊断设置、`RuntimeDiagnostics`、两个 monitor、BepInEx、Unity 与 Harmony 都是托管替身。只测启动编排，不注入游戏。

覆盖：Runtime 为 Off 或 Play 时只记 inactive 日志；Authoring 但宿主 Runtime 为 null 时抛出且无副作用；精确启动顺序；关闭性能日志时不加 PerformanceMonitor；单次 Load，inactive 的 Load 不能被重试成 active；各阶段失败时清理与已获取阶段一致、诊断停止最后执行；单个清理失败不阻止其余清理；异常 Data 不可写时保留原始异常。

从仓库根目录运行（`verify-diagnostics.py` 也会运行它）：

```powershell
$out = Join-Path $env:TEMP ('forge-dev-startup-' + [guid]::NewGuid().ToString('N'))
dotnet build ForgeDevelopment/tests/PluginStartup/PluginStartup.csproj -c Release --artifacts-path $out
if ($LASTEXITCODE -eq 0) { dotnet "$out/bin/PluginStartup/release/PluginStartup.dll" }
```

失败返回非零。结果记录见 [Development 验证记录](../../VALIDATION.md)。
