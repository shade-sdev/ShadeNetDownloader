<#
.SYNOPSIS
  The tests a release must pass: every project's tests (the ones marked Slow excluded) and the browser extension's. Stops at the
  first failure. Run by the release workflow; run it yourself before merging to master.

.DESCRIPTION
  The tests that make real video with ffmpeg need an ffmpeg with the x264 encoder on the PATH (the LGPL build that is bundled
  with the program has none): the workflow installs one; on your computer any full ffmpeg will do. Without one those tests
  return early instead of failing.
#>
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
Set-Location $root
$userSdk = Join-Path $env:USERPROFILE ".dotnet"
if (Test-Path (Join-Path $userSdk "dotnet.exe")) { $env:DOTNET_ROOT = $userSdk; $env:PATH = "$userSdk;$env:PATH" }

function Run([string]$what, [scriptblock]$cmd) {
    Write-Host "`n== $what" -ForegroundColor Cyan
    & $cmd
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}

Run "installer tests" { dotnet test tests\ShadeNet.Setup.Tests -c Release --nologo -v q }
Run "browser bridge tests" { dotnet test tests\ShadeNet.Bridge.Tests -c Release --nologo -v q }
Run "program tests" { dotnet test tests\ShadeNetDownloader.Tests -c Release --nologo -v q }
Run "engine tests" { dotnet test tests\ShadeNet.Core.Tests -c Release --nologo -v q --filter "Category!=Slow" }
Run "browser extension tests" { Push-Location extension\chromium; try { node --test "test/*.test.mjs" } finally { Pop-Location } }
Write-Host "`nall tests passed" -ForegroundColor Green
