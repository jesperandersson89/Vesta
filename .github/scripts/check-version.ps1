<#
.SYNOPSIS
  Verifies the three published Vesta SDKs (Vesta.Core/Vesta.Client, vesta-client-ts,
  vesta-client-py) all declare the same version, and optionally match a release tag.
.DESCRIPTION
  The three SDKs are released together under one version, committed in each
  ecosystem's manifest (Directory.Build.props VersionPrefix, package.json version,
  vesta_client/__init__.py __version__). This script is the single source of truth
  for "do the manifests agree" — run it before tagging a release and as the first
  step of the release workflow so a drifted manifest fails fast instead of
  publishing mismatched packages.
.PARAMETER Tag
  Optional. A git ref like 'v0.1.0' (or bare '0.1.0'). When supplied, the resolved
  manifest version must equal the tag with any leading 'v' stripped.
#>
[CmdletBinding()]
param(
    [string]$Tag
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '../..')

$buildPropsPath = Join-Path $repoRoot 'Directory.Build.props'
[xml]$buildProps = Get-Content $buildPropsPath
$dotnetVersion = $buildProps.Project.PropertyGroup.VersionPrefix | Where-Object { $_ } | Select-Object -First 1
if (-not $dotnetVersion) {
    throw "Could not find <VersionPrefix> in $buildPropsPath"
}

$tsPackagePath = Join-Path $repoRoot 'clients/vesta-client-ts/package.json'
$tsVersion = (Get-Content $tsPackagePath -Raw | ConvertFrom-Json).version
if (-not $tsVersion) {
    throw "Could not find version in $tsPackagePath"
}

$pyInitPath = Join-Path $repoRoot 'clients/vesta-client-py/vesta_client/__init__.py'
$pyMatch = Select-String -Path $pyInitPath -Pattern '__version__\s*=\s*"([^"]+)"' | Select-Object -First 1
if (-not $pyMatch) {
    throw "Could not find __version__ in $pyInitPath"
}
$pyVersion = $pyMatch.Matches[0].Groups[1].Value

Write-Host "Directory.Build.props (NuGet): $dotnetVersion"
Write-Host "vesta-client-ts (npm):         $tsVersion"
Write-Host "vesta-client-py (PyPI):        $pyVersion"

$versions = @(@($dotnetVersion, $tsVersion, $pyVersion) | Select-Object -Unique)
if ($versions.Count -ne 1) {
    throw "Version mismatch across manifests: NuGet=$dotnetVersion npm=$tsVersion PyPI=$pyVersion"
}

if ($Tag) {
    $tagVersion = $Tag -replace '^v', ''
    if ($tagVersion -ne $versions[0]) {
        throw "Tag '$Tag' (=$tagVersion) does not match manifest version '$($versions[0])'"
    }
    Write-Host "Tag '$Tag' matches manifest version '$($versions[0])'."
}

Write-Host "OK: all manifests agree on version $($versions[0])."
