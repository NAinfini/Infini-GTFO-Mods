param(
 [string]$BepInExPath = $env:GTFO_BEPINEX_PATH,
 [string]$ArtifactRoot = (Join-Path ([IO.Path]::GetTempPath()) ('forge-host-integration-' + [guid]::NewGuid().ToString('N')))
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
if (!(Test-Path (Join-Path $BepInExPath 'core/Mono.Cecil.dll'))) {
 throw 'Provide -BepInExPath or GTFO_BEPINEX_PATH with existing compile references.'
}
$ArtifactRoot = [IO.Path]::GetFullPath($ArtifactRoot)
if (Test-Path $ArtifactRoot) { throw 'ArtifactRoot must be new to exclude stale binaries.' }
New-Item -ItemType Directory -Path $ArtifactRoot | Out-Null
$artifacts = Join-Path $ArtifactRoot 'artifacts'
function Get-SourceSnapshot {
 $snapshot = @{}
 Get-ChildItem (Join-Path $root 'ForgeRuntime') -Recurse -File |
  Where-Object { $_.Extension -in @('.cs','.csproj') -and $_.FullName -notmatch '[\\/](bin|obj|dist)[\\/]' } |
  Sort-Object FullName | ForEach-Object {
   $snapshot[$_.FullName.Substring($root.Length + 1)] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
  }
 return $snapshot
}
$before = Get-SourceSnapshot
$before | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $ArtifactRoot 'sources-before.json') -Encoding UTF8
$results = @(); $code = 0
Push-Location $root
try {
 $projects = @('ForgeRuntime/ForgeRuntime.csproj', 'ForgeRuntime/tests/HostIntegration/HostIntegration.csproj')
 foreach ($project in $projects) {
  $name = [IO.Path]::GetFileNameWithoutExtension($project)
  $log = Join-Path $ArtifactRoot ($name + '.log')
  $ErrorActionPreference = 'Continue'
  & dotnet build $project -c Release --artifacts-path $artifacts "-p:GTFOBepInExPath=$BepInExPath" *> $log
  $buildExit = $LASTEXITCODE; $ErrorActionPreference = 'Stop'
  $results += [pscustomobject]@{ step=('build-' + $name); exitCode=$buildExit; log=$log }
  if ($buildExit -ne 0) { $code = 1; break }
 }
 if ($code -eq 0) {
  $hostDll = Join-Path $artifacts 'bin/ForgeRuntime/release/ForgeRuntime.dll'
  $testDll = Join-Path $artifacts 'bin/HostIntegration/release/HostIntegration.dll'
  $hostSdk = Join-Path $artifacts 'bin/ForgeRuntime/release/ForgeRuntime.Framework.dll'
  $testSdk = Join-Path $artifacts 'bin/HostIntegration/release/ForgeRuntime.Framework.dll'
  if ((Get-FileHash $hostSdk).Hash -ne (Get-FileHash $testSdk).Hash) { throw 'Host/test SDK binary hashes differ.' }
  $log = Join-Path $ArtifactRoot 'tests.log'
  $ErrorActionPreference = 'Continue'
  & dotnet $testDll --host $hostDll *> $log
  $testExit = $LASTEXITCODE; $ErrorActionPreference = 'Stop'
  $results += [pscustomobject]@{ step='compiled-sdk-and-host'; exitCode=$testExit; log=$log }
  if ($testExit -ne 0) { $code = 1 }
 }
} catch {
 $code = 1
 $results += [pscustomobject]@{ step='verification-error'; exitCode=1; detail=$_.Exception.Message }
} finally { Pop-Location }
$after = Get-SourceSnapshot
$after | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $ArtifactRoot 'sources-after.json') -Encoding UTF8
$keys = @(@($before.Keys) + @($after.Keys) | Sort-Object -Unique)
$changed = @($keys | Where-Object { $before[$_] -ne $after[$_] })
if ($changed.Count -ne 0) { $code = 1 }
$binaries = @{}
Get-ChildItem $artifacts -Recurse -File -Filter '*.dll' -ErrorAction SilentlyContinue |
 Where-Object { $_.Name -in @('ForgeRuntime.dll','ForgeRuntime.Framework.dll','HostIntegration.dll') } |
 ForEach-Object { $binaries[$_.FullName.Substring($ArtifactRoot.Length + 1)] = (Get-FileHash $_.FullName).Hash }
[pscustomobject]@{ exitCode=$code; sourceChangedDuringRun=$changed; results=$results; binaries=$binaries } |
 ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ArtifactRoot 'result.json') -Encoding UTF8
$results | Format-Table -AutoSize
Write-Output "SOURCE_CHANGES=$($changed.Count) EXIT=$code EVIDENCE=$ArtifactRoot"
exit $code
