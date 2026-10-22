<#
.SYNOPSIS
  Real run of the real installer in a sandbox (throw-away install folder, shortcut folders, data folder and registry key; the
  browser integration is skipped so no browser registry entry is written): silent install, the installed program starts and
  finds its bundled yt-dlp and ffmpeg, "--quit" ends it, an upgrade over a RUNNING program works, and the uninstall command
  that Windows would run ("Apps & features") removes everything but the user's data.
#>
param([string]$Installer = (Get-ChildItem (Join-Path $PSScriptRoot "..\..\dist") -Filter "ShadeNetDownloader-Setup-*.exe" | Select-Object -First 1).FullName)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$work = Join-Path $env:TEMP ("sn-inst-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$inst = Join-Path $work "Program Files Like\ShadeNet Downloader"      # a path with spaces, like the real one
$data = Join-Path $work "data"; $sm = Join-Path $work "startmenu"; $dt = Join-Path $work "desktop"
$regRoot = "Software\ShadeNetTests\InstallE2E-" + [guid]::NewGuid().ToString("N").Substring(0, 8)
New-Item -ItemType Directory $work, $data | Out-Null
$ok = $true
function Check([string]$what, [bool]$cond, [string]$detail = "") {
    $mark = if ($cond) { "ok  " } else { "FAIL" }
    Write-Host ("  [{0}] {1} {2}" -f $mark, $what, $detail)
    if (-not $cond) { $script:ok = $false }
}
$common = @("--registry-root", $regRoot, "--start-menu-dir", $sm, "--desktop-dir", $dt, "--data-dir", $data, "--no-browser-integration")
$app = $null

try {
    Write-Host "installer: $Installer ($([math]::Round((Get-Item $Installer).Length / 1MB)) MB)"

    Write-Host "`n1. silent install"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process $Installer -ArgumentList (@("/S", "/D=`"$inst`"") + $common) -Wait -PassThru
    Write-Host "  took $([math]::Round($sw.Elapsed.TotalSeconds)) s, exit code $($p.ExitCode)"
    Check "exit code 0" ($p.ExitCode -eq 0)
    foreach ($f in "ShadeNetDownloader.exe", "NativeHost\ShadeNet.NativeHost.exe", "NativeHost\hostfxr.dll", "hostfxr.dll", "tools\yt-dlp\yt-dlp.exe", "tools\ffmpeg\ffmpeg.exe",
        "tools\ffmpeg\LICENSE.txt", "tools\ffmpeg\tool.json", "tools\yt-dlp\tool.json", "browser\chromium\manifest.json", "licenses\THIRD-PARTY-NOTICES.txt", "Uninstall.exe", "install.json") {
        Check $f (Test-Path (Join-Path $inst $f))
    }
    $size = (Get-ChildItem $inst -Recurse -File | Measure-Object Length -Sum).Sum
    Write-Host ("  installed size {0:N0} MB" -f ($size / 1MB))
    Check "Start menu shortcut" (Test-Path (Join-Path $sm "ShadeNet Downloader.lnk"))
    Check "desktop shortcut" (Test-Path (Join-Path $dt "ShadeNet Downloader.lnk"))
    $key = "HKCU:\$regRoot\Microsoft\Windows\CurrentVersion\Uninstall\ShadeNetDownloader"
    $entry = Get-ItemProperty $key -ErrorAction SilentlyContinue
    Check "Apps & features entry" ($entry.DisplayName -eq "ShadeNet Downloader") "($($entry.DisplayVersion))"

    Write-Host "`n2. the installed program starts, from a folder it cannot write to as a normal 'Program Files' would be, and finds its tools"
    $env:SHADENET_DATA_DIR = $data
    @{ Theme = "Dark"; Engine = @{ DownloadRoot = (Join-Path $work "dl"); UseCategoryFolders = $false }; General = @{ ShowTrayIcon = $false } } | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $data "settings.json")
    $app = Start-Process (Join-Path $inst "ShadeNetDownloader.exe") -PassThru
    Start-Sleep -Seconds 6
    Check "the program is running" (-not $app.HasExited)
    & (Join-Path $PSScriptRoot "step-tools-shots.ps1") -AppExe (Join-Path $inst "ShadeNetDownloader.exe") -DataDir $data -Prefix "installed" -NoLaunch -AttachPid $app.Id 2>&1 | ForEach-Object { Write-Host "    $_" }

    Write-Host "`n3. '--quit' ends it for real"
    $q = Start-Process (Join-Path $inst "ShadeNetDownloader.exe") -ArgumentList "--quit" -PassThru
    $q.WaitForExit(15000) | Out-Null
    $app.WaitForExit(15000) | Out-Null
    Check "the running program ended" $app.HasExited

    Write-Host "`n4. an upgrade over a RUNNING program"
    $app = Start-Process (Join-Path $inst "ShadeNetDownloader.exe") -PassThru
    Start-Sleep -Seconds 5
    Check "running before the upgrade" (-not $app.HasExited)
    $p = Start-Process $Installer -ArgumentList (@("/S", "/D=`"$inst`"") + $common) -Wait -PassThru
    Check "the installer asked it to quit and finished (exit 0)" ($p.ExitCode -eq 0) "(exit $($p.ExitCode))"
    Check "the old program is gone" $app.HasExited
    Check "files are there again" (Test-Path (Join-Path $inst "tools\ffmpeg\ffmpeg.exe"))

    Write-Host "`n5. uninstall with the command from 'Apps & features'"
    $cmd = (Get-ItemProperty $key).QuietUninstallString
    Write-Host "  $cmd"
    Start-Process cmd.exe -ArgumentList "/c", "`"$cmd`"" -Wait -WindowStyle Hidden
    for ($i = 0; $i -lt 60 -and (Test-Path $inst); $i++) { Start-Sleep -Milliseconds 500 }
    Check "the install folder is gone" (-not (Test-Path $inst))
    Check "the Apps & features entry is gone" (-not (Test-Path $key))
    Check "shortcuts are gone" (-not (Test-Path (Join-Path $sm "ShadeNet Downloader.lnk")))
    Check "the user's data is still there" (Test-Path (Join-Path $data "settings.json"))
    Start-Sleep -Seconds 5
    $leftovers = @(Get-ChildItem $env:TEMP -Filter "ShadeNetUninstall-*.exe" -ErrorAction SilentlyContinue)
    Check "the temporary uninstaller cleaned itself up" ($leftovers.Count -eq 0) "($($leftovers.Count) left)"
}
finally {
    if ($app -and -not $app.HasExited) { $app.Kill() }
    Get-Process ShadeNetDownloader, ShadeNetSetup -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($work, [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { $_.Kill() }
    Remove-Item "HKCU:\$regRoot" -Recurse -ErrorAction SilentlyContinue
    if ((Get-ChildItem "HKCU:\Software\ShadeNetTests" -ErrorAction SilentlyContinue).Count -eq 0) { Remove-Item "HKCU:\Software\ShadeNetTests" -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host ""
if ($ok) { Write-Host "PASS" -ForegroundColor Green; exit 0 } else { Write-Host "FAIL" -ForegroundColor Red; exit 1 }
