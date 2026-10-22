<#
.SYNOPSIS
  The version of the next release: MAJOR.MINOR from Directory.Build.props, and the patch number is one more than the highest
  existing tag v<MAJOR.MINOR>.N (the first release of a MAJOR.MINOR is .1: there is never a release ".0", so a branch or tag
  called v1.0.0 cannot clash with one).

.DESCRIPTION
  Bump MAJOR.MINOR in Directory.Build.props when a release deserves a bigger number; the patch starts again at 1.

  Prints  version=1.0.7  and  tag=v1.0.7. With -Output (the GITHUB_OUTPUT file of a workflow step) it also appends them there.
  -Root is the repository folder (default: this one); the tags are read with git, so the checkout must have them (fetch-depth: 0).
#>
param(
    [string]$Root = (Join-Path $PSScriptRoot "..\.."),
    [string]$Output = ""
)

$ErrorActionPreference = "Stop"
$Root = (Resolve-Path $Root).Path
$props = ([xml](Get-Content (Join-Path $Root "Directory.Build.props"))).Project.PropertyGroup
$majorMinor = [string]$props.VersionMajorMinor
if ($majorMinor -notmatch '^\d+\.\d+$') { throw "VersionMajorMinor in Directory.Build.props must look like 1.0, not '$majorMinor'" }

$tags = @(& git -C $Root tag --list "v$majorMinor.*")
if ($LASTEXITCODE -ne 0) { throw "git could not list the tags" }

$escaped = [regex]::Escape($majorMinor)
$patches = @($tags | ForEach-Object { if ($_ -match "^v$escaped\.(\d+)$") { [int]$Matches[1] } })
$next = if ($patches.Count -gt 0) { ($patches | Measure-Object -Maximum).Maximum + 1 } else { 1 }

$version = "$majorMinor.$next"
Write-Host "version=$version"
Write-Host "tag=v$version"
if ($Output) {
    Add-Content -Path $Output -Value "version=$version"
    Add-Content -Path $Output -Value "tag=v$version"
}
