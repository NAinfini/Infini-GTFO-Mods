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

# === Release versions of the package assemblies and of their plugin dependencies ===
# Independent section: every Forge assembly project inside a package must be declared in identity.csprojs, and every
# BepInDependency a plugin entry declares must name a guid and a minimum version that release.json's dependency graph
# repeats. BepInEx 6 parses the version argument as a SemVer range, so a bare version is an exact match and would
# refuse the whole Forge stack the moment any package in it is patched: every hard dependency is written `>=x.y.z`,
# and this section requires exactly that spelling rather than a plain version. Later checks can be appended after this
# section; it reads only $release and repository files.

# The projects that produce a package's shipped assemblies: every project under the package directory except the
# test, tool and build output projects, which never hold a shipped assembly.
function Get-ForgeAssemblyProject([string]$packageDir) {
    $excluded = @('tests', 'tools', 'obj', 'bin', 'artifacts', '.artifacts')
    $root = Join-Path $repoRoot $packageDir
    $projects = @()
    foreach ($file in Get-ChildItem -LiteralPath $root -Filter *.csproj -File -Recurse) {
        $relative = $file.FullName.Substring($root.Length + 1) -replace '\\', '/'
        $segments = @($relative.Split('/'))
        if ($segments.Count -gt 1 -and ($segments[0..($segments.Count - 2)] | Where-Object { $_ -in $excluded })) { continue }
        $projects += "$packageDir/$relative"
    }
    return @($projects | Sort-Object)
}

# The BepInDependency payloads a plugin entry declares, with string constant arguments resolved in the same file.
function Get-BepInDependency([string]$relativePath) {
    $lines = Get-Content -LiteralPath (Join-Path $repoRoot $relativePath) -Encoding utf8
    $constants = @{}
    foreach ($line in $lines) {
        if ($line -match 'const\s+string\s+(\w+)\s*=\s*"([^"]*)"') { $constants[$Matches[1]] = $Matches[2] }
    }
    $rows = @()
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -notmatch '\[BepInDependency\(([^)]*)\)\]') { continue }
        $values = @()
        $soft = $false
        foreach ($argument in @($Matches[1].Split(',') | ForEach-Object { $_.Trim() })) {
            if ($argument -match '^"([^"]*)"$') { $values += $Matches[1] }
            elseif ($argument -match 'SoftDependency') { $soft = $true }
            elseif ($constants.ContainsKey($argument)) { $values += $constants[$argument] }
            else { $values += $null }
        }
        $rows += [pscustomobject]@{
            Line = $index + 1
            Guid = if ($values.Count -ge 1) { $values[0] } else { $null }
            Version = if ($values.Count -ge 2) { $values[1] } else { $null }
            Soft = $soft
        }
    }
    return @($rows)
}

# The guid -> version map a package's declared dependencies imply: another package of this release set through its
# pluginGuid, or a base dependency through the providers it ships. Entries that resolve to neither are reported.
function Resolve-DependencyGraph($package) {
    $graph = @{}
    $unresolved = @()
    foreach ($dependency in @($package.dependencies)) {
        if ($dependency -match '^(NAinfini-.+)-(\d+\.\d+\.\d+)$' -and $guidByPackage.ContainsKey($Matches[1])) {
            $graph[$guidByPackage[$Matches[1]]] = $Matches[2]
        } elseif ($providedByBaseDependency.ContainsKey($dependency)) {
            foreach ($provider in $providedByBaseDependency[$dependency].Keys) {
                $graph[$provider] = $providedByBaseDependency[$dependency][$provider]
            }
        } else {
            $unresolved += $dependency
        }
    }
    return [pscustomobject]@{ Graph = $graph; Unresolved = $unresolved }
}

$guidByPackage = @{}
foreach ($package in $release.packages) { $guidByPackage[$package.packageName] = $package.pluginGuid }

# "guid@version" pairs of the providers a base dependency ships, keyed by the exact Thunderstore dependency string.
$providedByBaseDependency = @{}
foreach ($base in $release.baseDependencies) {
    $provided = $base.PSObject.Properties['provides']
    if ($null -eq $provided) { continue }
    $providers = @{}
    foreach ($entry in @($provided.Value)) {
        if ($entry -match '^([^@]+)@(.+)$') { $providers[$Matches[1]] = $Matches[2] }
    }
    $providedByBaseDependency[$base.dependency] = $providers
}

foreach ($package in $release.packages) {
    Write-Host ''
    Write-Host "== $($package.packageName) $($package.version) 程序集与依赖版本 =="

    $packageDir = $package.packageName.Substring('NAinfini-'.Length)
    $required = @(Get-ForgeAssemblyProject $packageDir)
    $declared = @($package.identity.csprojs)
    $omitted = @($required | Where-Object { $_ -notin $declared })
    $extra = @($declared | Where-Object { $_ -notin $required })
    foreach ($project in $omitted) { Fail "$project：包内 Forge 程序集工程没有写进 release.json 的 identity.csprojs" }
    foreach ($project in $extra) { Fail "$project：release.json 的 identity.csprojs 列的不是 $packageDir 的 Forge 程序集工程" }
    if ($omitted.Count -eq 0 -and $extra.Count -eq 0) {
        Pass "$packageDir：包内 $($required.Count) 个 Forge 程序集工程都在 identity.csprojs 里（$($required -join ', ')）"
    }

    $pluginPath = $package.identity.plugin
    $resolved = Resolve-DependencyGraph $package
    foreach ($dependency in $resolved.Unresolved) {
        Fail "$($package.packageName)：依赖 $dependency 无法解析成 guid 与版本（既不是本发行集的包，也不在 baseDependencies 里）"
    }

    $hardDependencies = @()
    if ($null -ne $pluginPath -and (Test-Path -LiteralPath (Join-Path $repoRoot $pluginPath))) {
        foreach ($row in @(Get-BepInDependency $pluginPath)) {
            $at = "$pluginPath`:$($row.Line)"
            if ($row.Soft) {
                if ($null -ne $row.Version) {
                    Fail "$at：软依赖 $($row.Guid) 带了版本字面量，应保持不带版本"
                } else {
                    Pass "$at 软依赖 $($row.Guid) 不带版本"
                }
                continue
            }
            if ($null -eq $row.Guid) { Fail "$at：BepInDependency 的 guid 不是字符串字面量或本文件的常量"; continue }
            if ($null -eq $row.Version) { Fail "$at：硬依赖 $($row.Guid) 没有版本，应写成 `">=版本`" 的最低版本"; continue }
            if (-not $resolved.Graph.ContainsKey($row.Guid)) {
                Fail "$at：依赖 $($row.Guid) 不在 release.json 的依赖图里"
            } elseif (">=" + $resolved.Graph[$row.Guid] -ne $row.Version) {
                Fail "$at：依赖 $($row.Guid) 的字面量为 $($row.Version)，应为 `">=$($resolved.Graph[$row.Guid])`""
            } else {
                Pass "$at 依赖 $($row.Guid) 最低版本 $($row.Version)"
                $hardDependencies += $row.Guid
            }
        }
    }

    foreach ($dependency in @($package.dependencies)) {
        if ($dependency -notmatch '^(NAinfini-.+)-(\d+\.\d+\.\d+)$') { continue }
        if (-not $guidByPackage.ContainsKey($Matches[1])) { continue }
        if ($guidByPackage[$Matches[1]] -notin $hardDependencies) {
            Fail "$pluginPath：release.json 依赖 $dependency，插件入口里没有对应的带版本硬依赖"
        } else {
            Pass "$pluginPath 已声明发行集内依赖 $dependency"
        }
    }
}

# === Mount kinds a package declares and the matchers its own sources register ===
# Independent section: the website writes a plan's dependency closure from the `attachmentKinds` release.json
# declares — a plan whose mount kind has no matcher in the installed packages is refused when it loads — so a kind
# declared without a matcher, or a matcher registered without being declared, is a release set that would break
# dispatch in game. Both directions are checked against the package's own non-test sources.

# The string constants a package's own sources declare, keyed by name: a matcher's dictionary key is often a
# constant (`ModuleDefinition.GearBlockAttachmentKind`), and the kind it stands for is what the declaration means.
function Get-PackageConstant([string]$packageDir) {
    $excluded = @('tests', 'tools', 'obj', 'bin', 'artifacts', '.artifacts')
    $root = Join-Path $repoRoot $packageDir
    $constants = @{}
    foreach ($file in Get-ChildItem -LiteralPath $root -Filter *.cs -File -Recurse) {
        $relative = $file.FullName.Substring($root.Length + 1) -replace '\\', '/'
        $segments = @($relative.Split('/'))
        if ($segments.Count -gt 1 -and ($segments[0..($segments.Count - 2)] | Where-Object { $_ -in $excluded })) { continue }
        foreach ($line in Get-Content -LiteralPath $file.FullName -Encoding utf8) {
            if ($line -match 'const\s+string\s+(\w+)\s*=\s*"([^"]*)"') { $constants[$Matches[1]] = $Matches[2] }
        }
    }
    return $constants
}

# The mount kinds a package's sources register, resolved through its own string constants: every dictionary entry
# whose value is built by an `AttachmentMatcherRegistration` factory. A key that is neither a literal nor a constant
# of this package is reported unresolved instead of being counted as a match.
function Get-RegisteredAttachmentKind([string]$packageDir, $constants) {
    $excluded = @('tests', 'tools', 'obj', 'bin', 'artifacts', '.artifacts')
    $root = Join-Path $repoRoot $packageDir
    $kinds = @(); $unresolved = @()
    foreach ($file in Get-ChildItem -LiteralPath $root -Filter *.cs -File -Recurse) {
        $relative = $file.FullName.Substring($root.Length + 1) -replace '\\', '/'
        $segments = @($relative.Split('/'))
        if ($segments.Count -gt 1 -and ($segments[0..($segments.Count - 2)] | Where-Object { $_ -in $excluded })) { continue }
        $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding utf8
        foreach ($entry in [regex]::Matches($text, '\[\s*([^\]\r\n]+?)\s*\]\s*=\s*(?:\r?\n\s*)?AttachmentMatcherRegistration\.')) {
            $key = $entry.Groups[1].Value.Trim()
            if ($key -match '^"([^"]*)"$') { $kinds += $Matches[1]; continue }
            $name = $key.Substring($key.LastIndexOf('.') + 1)
            if ($constants.ContainsKey($name)) { $kinds += $constants[$name] } else { $unresolved += $key }
        }
    }
    return [pscustomobject]@{
        Kinds = @($kinds | Sort-Object -Unique)
        Unresolved = @($unresolved | Sort-Object -Unique)
    }
}

foreach ($package in $release.packages) {
    Write-Host ''
    Write-Host "== $($package.packageName) $($package.version) 挂载种类 =="

    $packageDir = $package.packageName.Substring('NAinfini-'.Length)
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $packageDir))) {
        Fail "$packageDir：包目录不存在，无法核对 attachmentKinds"
        continue
    }
    $registered = Get-RegisteredAttachmentKind $packageDir (Get-PackageConstant $packageDir)
    foreach ($key in $registered.Unresolved) {
        Fail "$packageDir：挂载匹配器的键 $key 既不是字符串字面量，也不是本包声明过的常量"
    }
    $declaredKinds = @()
    $declaration = $package.PSObject.Properties['attachmentKinds']
    if ($null -ne $declaration) { $declaredKinds = @($declaration.Value) }

    foreach ($kind in $declaredKinds) {
        if ($kind -notin $registered.Kinds) {
            Fail "$packageDir：release.json 声明挂载种类 $kind，包内源码没有为它注册匹配器（实际注册：$($registered.Kinds -join ', ')）"
        } else {
            Pass "$packageDir：声明的挂载种类 $kind 在包内源码里确有匹配器"
        }
    }
    foreach ($kind in $registered.Kinds) {
        if ($kind -notin $declaredKinds) {
            Fail "$packageDir：包内源码注册了挂载种类 $kind，release.json 的 attachmentKinds 没有声明它"
        }
    }
    if ($declaredKinds.Count -eq 0 -and $registered.Kinds.Count -eq 0) {
        Pass "$packageDir：不注册挂载匹配器，release.json 也没有声明 attachmentKinds"
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
