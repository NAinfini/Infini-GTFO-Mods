# ReportSnapshots — D1 入队快照回归

2026-09-12：当前为已知失败的回归，**19/37 通过，18 项失败，退出码 1**。
失败反映现有 AsyncReportWriter 排队后仍读取可变报告，不是测试环境缺少游戏引用。
生产修复尚未写入；不得删改断言以获得绿色结果。

测试直接链接 ForgeRuntime 的 DiagnosticsReport、ProjectObjectReferences 与 AsyncReportWriter，
无需 Unity/GTFO/BepInEx，不创建第二份生产实现，不参与模块 DLL 的默认编译。
使用一次预期写盘错误的回调闸门暂停后台 writer，确定性地改变排队报告后再释放。
覆盖同一报告不同阶段、world/tick、source verification、异常/事件/检查和聚合计数，
以及容量 8、超限、同路径合并、关闭与临时文件清理。

从模组仓库根目录运行；隔离输出避免碰到其他会话的编译目录：

```powershell
$out = Join-Path $env:TEMP ('forge-snapshot-review-' + [guid]::NewGuid().ToString('N'))
dotnet build ForgeDevelopment/tests/ReportSnapshots/ReportSnapshots.csproj -c Release --artifacts-path $out
if ($LASTEXITCODE -eq 0) {
    dotnet (Join-Path $out 'bin/ReportSnapshots/release/ReportSnapshots.dll')
}
```

退出码 0 才表示 37 项全部通过。测试自行清理其新建的临时报告目录，不修改游戏 profile。
完整缺陷、修复门槛和其他测试结果见 [D1-CONTINUATION](../../D1-CONTINUATION.md)。
这不是游戏实测、独立插件拆分完成或采样开销证明。
