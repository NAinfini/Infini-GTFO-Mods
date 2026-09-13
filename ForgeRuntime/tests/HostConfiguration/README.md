# Real BepInEx host configuration regression

This executable compiles the production RuntimeSettings.cs and RuntimeMode.cs
against the existing local BepInEx.Core.dll. Unlike PluginStartup's orchestration
doubles, these checks use real ConfigFile loading, binding, saving and reopening.
All config files are unique temporary fixtures; no installed profile is edited.

Checks preserve named Off/Authoring/Play and numeric 0/1/2 values, original key
names, the existing default, explicit plan path and grants, empty defaults and
saved roundtrips. Malformed values must fail rather than silently select Authoring
or combine enum flags. Only Runtime/Framework keys may be bound by the host binder.
The mode entry is parsed from raw text to avoid the ConfigFile enum fallback.
The default remains Authoring at this intermediate split stage; changing the final
new-install policy is separate from preserving existing user configuration.

From the repository root, using existing legal local compile references:

```powershell
$env:GTFO_BEPINEX_PATH = '<existing profile BepInEx directory>'
$out = Join-Path $env:TEMP ('forge-config-' + [guid]::NewGuid().ToString('N'))
dotnet build ForgeRuntime/tests/HostConfiguration/HostConfiguration.csproj -c Release --artifacts-path $out
if ($LASTEXITCODE -eq 0) { dotnet "$out/bin/HostConfiguration/release/HostConfiguration.dll" }
```

A missing dependency or failed assertion returns nonzero. No skip or fallback
library is supplied. This is configuration evidence, not game loading verification.
See [the Runtime validation record](../../VALIDATION.md).
