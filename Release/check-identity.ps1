#Requires -Version 7.0
# Checks every package identity in Release/release.json against the files that repeat it
# (csproj <Version>, Plugin.cs constants, ModuleDefinition.cs constants, manifest.json).
# Exit code 0: every declared identity agrees. Exit code 1: at least one file disagrees or is missing.
# Read-only: this script never writes to the repository.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$releasePath = Join-Path $PSScriptRoot 'release.json'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not (Test-Path -LiteralPath $releasePath)) {
    Write-Host "[fail] 缺少 $releasePath"
    exit 1
}

try {
    $release = Get-Content -LiteralPath $releasePath -Raw -Encoding utf8 | ConvertFrom-Json
} catch {
    Write-Host "[fail] $releasePath 无法解析：$($_.Exception.Message)"
    exit 1
}

$script:failures = 0
$script:passed = 0

function Fail([string]$message) { $script:failures++; Write-Host "[fail] $message" }
function Pass([string]$message) { $script:passed++; Write-Host "[ok]   $message" }

# First line matching $pattern (exactly one capture group), or $null when the file is missing or nothing matches.
function Find-Line([string]$relativePath, [string]$pattern) {
    $full = Join-Path $repoRoot $relativePath
    if (-not (Test-Path -LiteralPath $full)) { return $null }
    $lines = Get-Content -LiteralPath $full -Encoding utf8
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -match $pattern) {
            return [pscustomobject]@{ Value = $Matches[1]; Line = $index + 1 }
        }
    }
    return $null
}

function Test-Constant([string]$relativePath, [string]$pattern, [string]$label, [string]$expected) {
    if ($null -eq $expected) { return }
    $found = Find-Line $relativePath $pattern
    if ($null -eq $found) {
        Fail "$relativePath：缺少 $label（期望 $expected）"
    } elseif ($found.Value -ne $expected) {
        Fail "$relativePath`:$($found.Line)：$label 为 $($found.Value)，release.json 为 $expected"
    } else {
        Pass "$relativePath`:$($found.Line) $label $($found.Value)"
    }
}

$versionsByName = @{}
foreach ($package in $release.packages) { $versionsByName[$package.packageName] = $package.version }

foreach ($package in $release.packages) {
    Write-Host ''
    Write-Host "== $($package.packageName) $($package.version) ($($package.audience)) =="

    foreach ($csproj in $package.identity.csprojs) {
        $found = Find-Line $csproj '^\s*<Version>([^<]+)</Version>'
        if ($null -eq $found) {
            Fail "$csproj：缺少 <Version> 或文件不存在（期望 $($package.version)）"
        } elseif ($found.Value -ne $package.version) {
            Fail "$csproj`:$($found.Line)：<Version> 为 $($found.Value)，release.json 为 $($package.version)"
        } else {
            Pass "$csproj`:$($found.Line) <Version> $($found.Value)"
        }
    }

    $pluginPath = $package.identity.plugin
    if ($null -ne $pluginPath) {
        if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $pluginPath))) {
            Fail "$pluginPath：缺插件入口（应为 $($package.pluginGuid) $($package.version)）"
        } else {
            Test-Constant $pluginPath 'PluginGuid\s*=\s*"([^"]+)"' 'PluginGuid' $package.pluginGuid
            Test-Constant $pluginPath 'PluginVersion\s*=\s*"([^"]+)"' 'PluginVersion' $package.version
        }
    }

    $modulePath = $package.identity.module
    if ($null -ne $modulePath) {
        if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $modulePath))) {
            Fail "$modulePath：缺模块定义"
        } else {
            if ($null -ne $package.providerId) {
                Test-Constant $modulePath 'ProviderId\s*=\s*"([^"]+)"' 'ProviderId' $package.providerId
            }
            Test-Constant $modulePath 'public const string Version\s*=\s*"([^"]+)"' 'Version' $package.version
        }
    }

    # manifest.json lives in the package directory, which is the package name without the author prefix.
    $packageDir = $package.packageName.Substring('NAinfini-'.Length)
    $manifestPath = "$packageDir/manifest.json"
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $manifestPath))) {
        Fail "$manifestPath：缺少 Thunderstore 清单（期望 $($package.packageName) $($package.version)）"
        continue
    }
    try {
        $manifest = Get-Content -LiteralPath (Join-Path $repoRoot $manifestPath) -Raw -Encoding utf8 | ConvertFrom-Json
    } catch {
        Fail "$manifestPath：无法解析：$($_.Exception.Message)"
        continue
    }

    $versionLine = Find-Line $manifestPath '"version_number"'
    $versionAt = if ($null -eq $versionLine) { $manifestPath } else { "$manifestPath`:$($versionLine.Line)" }
    if ($manifest.version_number -ne $package.version) {
        Fail "$versionAt：version_number 为 $($manifest.version_number)，release.json 为 $($package.version)"
    } else {
        Pass "$versionAt version_number $($manifest.version_number)"
    }

    $nameLine = Find-Line $manifestPath '"name"'
    $nameAt = if ($null -eq $nameLine) { $manifestPath } else { "$manifestPath`:$($nameLine.Line)" }
    if ($manifest.name -ne $packageDir) {
        Fail "$nameAt：name 为 $($manifest.name)，应为 $packageDir"
    } else {
        Pass "$nameAt name $($manifest.name)"
    }

    $dependencyLine = Find-Line $manifestPath '"dependencies"\s*:\s*\['
    $dependencyAt = if ($null -eq $dependencyLine) { $manifestPath } else { "$manifestPath`:$($dependencyLine.Line)" }
    $expected = @($package.dependencies)
    $actual = @($manifest.dependencies)
    $missing = @($expected | Where-Object { $_ -notin $actual })
    $extra = @($actual | Where-Object { $_ -notin $expected })
    if ($missing.Count -gt 0 -or $extra.Count -gt 0) {
        $detail = @()
        if ($missing.Count -gt 0) { $detail += '缺少 ' + ($missing -join ', ') }
        if ($extra.Count -gt 0) { $detail += '多余 ' + ($extra -join ', ') }
        Fail "$dependencyAt：dependencies 与 release.json 不一致（$($detail -join '；')）"
    } else {
        Pass "$dependencyAt dependencies 与 release.json 一致（$($actual.Count) 条）"
    }

    # A dependency on another package in this release set must name that package's declared version.
    foreach ($dependency in $actual) {
        if ($dependency -match '^(NAinfini-.+)-(\d+\.\d+\.\d+)$') {
            $dependencyName = $Matches[1]
            $dependencyVersion = $Matches[2]
            if (-not $versionsByName.ContainsKey($dependencyName)) {
                Fail "$dependencyAt：依赖 $dependency 不在 release.json 的包列表里"
            } elseif ($versionsByName[$dependencyName] -ne $dependencyVersion) {
                Fail "$dependencyAt：依赖 $dependency 的版本与 release.json 的 $($versionsByName[$dependencyName]) 不一致"
            } else {
                Pass "$dependencyAt 依赖 $dependency 与 $dependencyName 的发行版本一致"
            }
        }
    }
}

Write-Host ''
$total = $script:passed + $script:failures
if ($script:failures -gt 0) {
    Write-Host "核对 $total 项：$($script:passed) 通过，$($script:failures) 失败。"
    exit 1
}
Write-Host "核对 $total 项：$($script:passed) 通过，0 失败。"
exit 0
