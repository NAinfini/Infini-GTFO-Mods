# ReportSnapshots — D1 入队快照回归

2026-09-12：生产入队快照修复后，**100/100 通过，退出码 0**。
原有 37 项已完整保存在 PreservedSnapshotTests.cs，与新增 63 项共同执行。
历史 19/37 的失败证据保留；最新源码不再排队读取可变报告。

测试直接链接 `Native/` 下的 DiagnosticsReport、ProjectObjectReferences 与 AsyncReportWriter，
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

退出码 0 才表示 100 项全部通过。测试自行清理其新建的临时报告目录，不修改游戏 profile。
缺陷历史、修复与最新独立验证见 [Development 验证记录](../../VALIDATION.md)。
这不是游戏实测或采样开销证明。
