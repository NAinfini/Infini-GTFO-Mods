# Host plugin bootstrap regression

The executable compiles the production Plugin.cs, RuntimeSettings.cs and
RuntimeMode.cs against the real SDK. BepInEx configuration, loader, logging,
Harmony and Unity are controlled managed doubles; the tests exercise
orchestration, not native game injection. Actual config-file parsing is tested
separately by ../HostConfiguration.

Coverage: Off starts nothing; Play and Authoring start the same host with only
its framework hook and FrameworkMonitor; malformed modes; single-attempt Load;
partial host startup; harmony/component/patch/log failures; cleanup
continuation and original exception identity; failed-host publication; frozen
mode, plan path, grants and Logging.Level after configuration edits; the default
and configured Logging.Level reaching host initialization; trace or unknown
levels failing before Harmony or host initialization.
Diagnostics are not part of the host; their startup is tested by
ForgeDevelopment/tests/PluginStartup.
Only returned components can be explicitly destroyed. A failing native API may
have unknown internal effects; these tests do not claim native rollback guarantees.

From the repository root:

```powershell
$out = Join-Path $env:TEMP ('forge-bootstrap-' + [guid]::NewGuid().ToString('N'))
dotnet build ForgeRuntime/tests/PluginStartup/PluginStartup.csproj -c Release --artifacts-path $out
if ($LASTEXITCODE -eq 0) { dotnet "$out/bin/PluginStartup/release/PluginStartup.dll" }
```

Failure returns nonzero. Original red tests and final logs are summarised in
[the Runtime validation record](../../VALIDATION.md).
