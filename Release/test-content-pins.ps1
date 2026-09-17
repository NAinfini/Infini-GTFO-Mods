#Requires -Version 7.0
# Drives Release/export-content-pins against %TEMP% fixtures: no game install, no packaging staging and no repository
# file is needed, and nothing here writes into the repository. Every case runs the real command line, so the exit
# codes and the messages a release operator sees are what is checked.
# Exit code 0: every case behaved as declared. Exit code 1: at least one case did not.
param([switch]$Keep)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:failures = 0
$script:passed = 0
function Fail([string]$message) { $script:failures++; Write-Host "[fail] $message" }
function Pass([string]$message) { $script:passed++; Write-Host "[ok]   $message" }
function Check([bool]$condition, [string]$name) { if ($condition) { Pass $name } else { Fail $name } }

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $env:TEMP 'forge-content-pins-build'
$workspace = Join-Path $env:TEMP ('forge-content-pins-check-' + [guid]::NewGuid().ToString('n'))
$tool = Join-Path $artifacts 'bin/export-content-pins/release/export-content-pins.exe'

function Write-Text([string]$path, [string]$text) {
    $directory = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
    Set-Content -LiteralPath $path -Value $text -Encoding utf8 -NoNewline
}
function Hash-Of([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Invoke-Pins([string[]]$Arguments) {
    $output = & $tool @Arguments 2>&1 | Out-String
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output.Trim() }
}

try {
    Write-Host "== build =="
    $build = dotnet build (Join-Path $repoRoot 'Release/export-content-pins/export-content-pins.csproj') -c Release --artifacts-path $artifacts 2>&1 | Out-String
    if (-not (Test-Path -LiteralPath $tool)) { Write-Host $build; throw "export-content-pins 没有构建出来：$tool" }

    # The fixture repository mirrors the real layout the tool derives its inputs from: Release/release.json decides the
    # packages, ForgeRuntime/GameBindings/GameRuntimeBridge.cs decides the supported game binding, and the staging root
    # holds one directory per package with the files release.json declares.
    Write-Host ''
    Write-Host "== fixtures =="
    $fixtureRoot = Join-Path $workspace 'repo'
    $staging = Join-Path $workspace 'staging'
    $gameRoot = Join-Path $workspace 'game'
    New-Item -ItemType Directory -Force -Path $gameRoot | Out-Null
    $gameAssembly = Join-Path $gameRoot 'GameAssembly.dll'
    [System.IO.File]::WriteAllBytes($gameAssembly, [byte[]](1..64))
    $supportedHash = Hash-Of $gameAssembly
    Write-Text (Join-Path $fixtureRoot 'ForgeRuntime/GameBindings/GameRuntimeBridge.cs') @"
internal static class GameRuntimeBridge
{
    internal const string GameBuild = "7";
    internal static readonly string GameAssemblySha256 = "$($supportedHash.ToUpperInvariant())";
}
"@
    $release = [ordered]@{
        kind = 'forge-base-release-identity'; schemaVersion = 1; author = 'NAinfini'
        websiteUrl = 'https://example.invalid'
        baseDependencies = @([ordered]@{ dependency = 'BepInEx-BepInExPack_GTFO-3.2.2' })
        packages = @(
            [ordered]@{ packageName = 'NAinfini-Beta'; pluginGuid = 'NAinfini.Beta'; version = '2.0.0'; audience = 'player'
                providerId = 'forge.module.beta'; providerIds = @('forge.module.beta'); dependencies = @()
                identity = [ordered]@{ csprojs = @('Beta/Beta.csproj'); plugin = 'Beta/Plugin.cs'; module = $null }
                files = @('Beta.dll', 'manifest.json', 'icon.png', 'README.md', 'CHANGELOG.md') }
            [ordered]@{ packageName = 'NAinfini-Alpha'; pluginGuid = 'NAinfini.Alpha'; version = '1.0.0'; audience = 'player'
                providerId = 'forge.module.alpha'; providerIds = @('forge.module.alpha'); dependencies = @()
                identity = [ordered]@{ csprojs = @('Alpha/Alpha.csproj', 'Alpha/Core/Alpha.Core.csproj'); plugin = 'Alpha/Plugin.cs'; module = $null }
                files = @('Alpha.dll', 'Alpha.Core.dll', 'manifest.json', 'icon.png', 'README.md', 'CHANGELOG.md') }
            [ordered]@{ packageName = 'NAinfini-Dev'; pluginGuid = 'NAinfini.Dev'; version = '1.0.0'; audience = 'author'
                providerId = $null; providerIds = @(); dependencies = @()
                identity = [ordered]@{ csprojs = @('Dev/Dev.csproj'); plugin = 'Dev/Plugin.cs'; module = $null }
                files = @('Dev.dll', 'manifest.json', 'icon.png', 'README.md', 'CHANGELOG.md') }
        )
    }
    $releasePath = Join-Path $fixtureRoot 'Release/release.json'
    Write-Text $releasePath ($release | ConvertTo-Json -Depth 10)
    $manifestPath = Join-Path $workspace 'runtime-manifest.json'
    Write-Text $manifestPath '{"runtime":{"id":"forge.runtime","version":"1.0.0","apiVersion":"1.0.0","gameBuild":"7"}}'
    $alphaAssemblies = @('Alpha.dll', 'Alpha.Core.dll')
    foreach ($file in @('Alpha.dll', 'Alpha.Core.dll', 'manifest.json')) { Write-Text (Join-Path $staging "NAinfini-Alpha/$file") "alpha $file`n" }
    foreach ($file in @('Beta.dll', 'manifest.json')) { Write-Text (Join-Path $staging "NAinfini-Beta/$file") "beta $file`n" }
    Write-Text (Join-Path $staging 'NAinfini-Dev/Dev.dll') "dev assembly`n"

    $arguments = @('--release', $releasePath, '--packages-root', $staging, '--game-root', $gameRoot,
        '--runtime-manifest', $manifestPath)
    $outputPath = Join-Path $workspace 'content-pins.json'

    Write-Host ''
    Write-Host '== 成功路径：玩家包全钉、author 包排除、小写 hex、files 顺序 = declaration order =='
    $run = Invoke-Pins ($arguments + @('--output', $outputPath))
    Check ($run.ExitCode -eq 0) "生成器退出 0（实际 $($run.ExitCode)：$($run.Output)）"
    $pins = Get-Content -LiteralPath $outputPath -Raw -Encoding utf8 | ConvertFrom-Json
    Check ($pins.gameAssembly.gameBuild -eq '7') 'gameAssembly.gameBuild 取自宿主源码常量'
    Check ($pins.gameAssembly.sha256 -ceq $supportedHash) 'gameAssembly.sha256 是小型游戏程序集的小写摘要'
    Check (($pins.plugins.packageName -join ',') -eq 'NAinfini-Alpha,NAinfini-Beta') 'plugins 按 packageName ordinal 升序，且 author 包未进入'
    $alpha = $pins.plugins | Where-Object { $_.packageName -eq 'NAinfini-Alpha' }
    Check (($alpha.files.path -join ',') -eq 'Alpha.dll,Alpha.Core.dll') 'files 保持 release.json 的声明顺序（插件程序集在前）'
    $alphaHashes = $alpha.files | ForEach-Object { $_.sha256 }
    $expectedHashes = $alphaAssemblies | ForEach-Object { Hash-Of (Join-Path $staging "NAinfini-Alpha/$_") }
    Check (($alphaHashes -join ',') -ceq ($expectedHashes -join ',')) '每个 DLL 的 sha256 与 staging 实物一致'
    Check (@($alphaHashes | Where-Object { $_ -cnotmatch '^[0-9a-f]{64}$' }).Count -eq 0) '所有 hash 为小写 64 位 hex'
    Check ($alpha.pluginGuid -eq 'NAinfini.Alpha' -and $alpha.version -eq '1.0.0') 'pluginGuid/version 原样取自 release.json'
    $text = Get-Content -LiteralPath $outputPath -Raw -Encoding utf8
    Check ($text.EndsWith("`n") -and -not $text.Contains("`r")) '输出为 LF 结尾、无 CR 的 UTF-8'

    Write-Host ''
    Write-Host '== author 包的文件不参与：删掉 Dev.dll 仍成功 =='
    Remove-Item -LiteralPath (Join-Path $staging 'NAinfini-Dev/Dev.dll')
    $run = Invoke-Pins ($arguments + @('--output', $outputPath))
    Check ($run.ExitCode -eq 0) "author 包不在 staging 里也不失败（实际 $($run.ExitCode)：$($run.Output)）"

    Write-Host ''
    Write-Host '== 稳定排序：打乱 release.json 的 packages 顺序，输出逐字节相同 =='
    $before = [System.IO.File]::ReadAllBytes($outputPath)
    $shuffled = Get-Content -LiteralPath $releasePath -Raw -Encoding utf8 | ConvertFrom-Json
    $shuffled.packages = @($shuffled.packages[2], $shuffled.packages[0], $shuffled.packages[1])
    Write-Text $releasePath ($shuffled | ConvertTo-Json -Depth 10)
    $run = Invoke-Pins ($arguments + @('--output', $outputPath))
    $after = [System.IO.File]::ReadAllBytes($outputPath)
    Check ($run.ExitCode -eq 0) '打乱 packages 顺序后仍成功'
    Check ([System.Linq.Enumerable]::SequenceEqual([byte[]]$before, [byte[]]$after)) '打乱输入顺序后输出逐字节相同'

    Write-Host ''
    Write-Host '== 缺 GameAssembly：退出 1 并指出文件 =='
    $run = Invoke-Pins (@('--release', $releasePath, '--packages-root', $staging, '--game-root', (Join-Path $workspace 'no-game'),
        '--runtime-manifest', $manifestPath, '--output', $outputPath))
    Check ($run.ExitCode -eq 1) "缺游戏根退出 1（实际 $($run.ExitCode)）"
    Check ($run.Output -match 'GameAssembly\.dll') "错误信息点名 GameAssembly.dll（$($run.Output)）"

    Write-Host ''
    Write-Host '== 游戏程序集哈希不受支持：退出 1 =='
    [System.IO.File]::WriteAllBytes($gameAssembly, [byte[]](1..63 + 99))
    $run = Invoke-Pins ($arguments + @('--output', $outputPath))
    Check ($run.ExitCode -eq 1) "哈希与宿主绑定不符时退出 1（实际 $($run.ExitCode)）"
    Check ($run.Output -match 'host binding') "错误信息说明宿主绑定（$($run.Output)）"
    [System.IO.File]::WriteAllBytes($gameAssembly, [byte[]](1..64))

    Write-Host ''
    Write-Host '== 缺 DLL：退出 1 并点名包与文件 =='
    Remove-Item -LiteralPath (Join-Path $staging 'NAinfini-Beta/Beta.dll')
    $run = Invoke-Pins ($arguments + @('--output', $outputPath))
    Check ($run.ExitCode -eq 1) "staging 缺 DLL 时退出 1（实际 $($run.ExitCode)）"
    Check ($run.Output -match 'NAinfini-Beta/Beta\.dll') "错误信息点名 NAinfini-Beta/Beta.dll（$($run.Output)）"
    Write-Text (Join-Path $staging 'NAinfini-Beta/Beta.dll') "beta Beta.dll`n"

    Write-Host ''
    Write-Host '== 改一字节被检出：pin 摘要随之改变 =='
    $stalePath = Join-Path $workspace 'stale-pins.json'
    Copy-Item -LiteralPath $outputPath -Destination $stalePath
    $target = Join-Path $staging 'NAinfini-Alpha/Alpha.Core.dll'
    $bytes = [System.IO.File]::ReadAllBytes($target)
    $bytes[0] = $bytes[0] -bxor 0xFF
    [System.IO.File]::WriteAllBytes($target, $bytes)
    $run = Invoke-Pins ($arguments + @('--output', $outputPath))
    $pins = Get-Content -LiteralPath $outputPath -Raw -Encoding utf8 | ConvertFrom-Json
    $changed = ($pins.plugins | Where-Object { $_.packageName -eq 'NAinfini-Alpha' }).files |
        Where-Object { $_.path -eq 'Alpha.Core.dll' }
    Check ($run.ExitCode -eq 0 -and $changed.sha256 -ceq (Hash-Of $target)) '改一字节后 pin 记录新摘要'
    Check ($changed.sha256 -cne ((Get-Content -LiteralPath $stalePath -Raw -Encoding utf8 | ConvertFrom-Json).plugins |
        Where-Object { $_.packageName -eq 'NAinfini-Alpha' }).files[1].sha256) '改一字节后摘要与改动前不同'

    Write-Host ''
    Write-Host '== gameBuild 与宿主源码不一致：退出 1 =='
    Write-Text $manifestPath '{"runtime":{"id":"forge.runtime","version":"1.0.0","apiVersion":"1.0.0","gameBuild":"8"}}'
    $run = Invoke-Pins ($arguments + @('--output', $outputPath))
    Check ($run.ExitCode -eq 1) "运行清单 gameBuild 不符时退出 1（实际 $($run.ExitCode)）"
    Check ($run.Output -match 'game build 8') "错误信息列出两个 gameBuild（$($run.Output)）"
    Write-Text $manifestPath '{"runtime":{"id":"forge.runtime","version":"1.0.0","apiVersion":"1.0.0","gameBuild":"7"}}'

    Write-Host ''
    Write-Host '== BepInEx 根反推游戏根；两个根互斥；缺少参数退出 2 =='
    $bepinexRoot = Join-Path $gameRoot 'BepInEx'
    New-Item -ItemType Directory -Force -Path $bepinexRoot | Out-Null
    $run = Invoke-Pins (@('--release', $releasePath, '--packages-root', $staging, '--bepinex-root', $bepinexRoot,
        '--runtime-manifest', $manifestPath, '--output', $outputPath))
    $pins = Get-Content -LiteralPath $outputPath -Raw -Encoding utf8 | ConvertFrom-Json
    Check ($run.ExitCode -eq 0 -and $pins.gameAssembly.sha256 -ceq $supportedHash) '--bepinex-root 从父目录得到同一个游戏程序集'
    $run = Invoke-Pins ($arguments + @('--bepinex-root', $bepinexRoot, '--output', $outputPath))
    Check ($run.ExitCode -eq 2) "两个游戏根同时给出时退出 2（实际 $($run.ExitCode)）"
    $run = Invoke-Pins @('--release', $releasePath)
    Check ($run.ExitCode -eq 2) "缺少参数时退出 2（实际 $($run.ExitCode)）"

    Write-Host ''
    Write-Host '== 没有玩家包可钉：退出 1，不写空 pin =='
    $authorOnlyRoot = Join-Path $workspace 'author-only'
    Write-Text (Join-Path $authorOnlyRoot 'Release/release.json') (@{ author = 'NAinfini';
        packages = @($release.packages | Where-Object { $_.audience -eq 'author' }) } | ConvertTo-Json -Depth 10)
    Copy-Item -LiteralPath (Join-Path $fixtureRoot 'ForgeRuntime') -Destination (Join-Path $authorOnlyRoot 'ForgeRuntime') -Recurse
    $emptyOutput = Join-Path $workspace 'author-only-pins.json'
    $run = Invoke-Pins (@('--release', (Join-Path $authorOnlyRoot 'Release/release.json'), '--packages-root', $staging,
        '--game-root', $gameRoot, '--runtime-manifest', $manifestPath, '--output', $emptyOutput))
    Check ($run.ExitCode -eq 1 -and -not (Test-Path -LiteralPath $emptyOutput)) '只有 author 包时退出 1 且不产出文件'
}
finally {
    if ($Keep) { Write-Host "fixtures kept at $workspace" }
    elseif (Test-Path -LiteralPath $workspace) { Remove-Item -LiteralPath $workspace -Recurse -Force }
}

Write-Host ''
if ($script:failures) { Write-Host "export-content-pins 检查：$($script:failures) 项失败，$($script:passed) 项通过。"; exit 1 }
Write-Host "export-content-pins 检查：$($script:passed) 项通过。"
exit 0
