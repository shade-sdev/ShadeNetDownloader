<#
.SYNOPSIS
  The whole self-update, with the real installer and the real program, in a sandbox:
    1. an OLD release is installed (silently, into a throw-away folder and registry key),
    2. a local server stands in for GitHub and serves the NEWER release's update.json and update package,
    3. a package with a flipped byte is offered first: the program must refuse it, say why, and keep running,
    4. then the genuine one: Settings > About > "Check for updates" > "Update to x": the program quits, the update window replaces
       its files, the new version starts by itself; yt-dlp/ffmpeg (tools\), the shortcuts, the data and the registry entry are as they
       were, the temporary updater cleans up after itself.
  Nothing outside the throw-away folders and the throw-away registry key is touched; the browser connection is not registered.
  Screenshots: docs\update-*.png.

  Needs two releases built with tools\build-release.ps1 -Version x.y.z (the script says how when they are missing).
#>
param(
    [string]$OldDir = (Join-Path $env:TEMP "sn-rel\1.0.1"),
    [string]$NewDir = (Join-Path $env:TEMP "sn-rel\1.0.2"),
    [string]$OldVersion = "1.0.1",
    [string]$NewVersion = "1.0.2"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public class SUpd {
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
 [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hgt, bool repaint);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
'@
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$docs = Join-Path $root "docs"
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]; $ct = [System.Windows.Automation.ControlType]

$installer = Join-Path $OldDir "ShadeNetDownloader-Setup-$OldVersion.exe"
foreach ($f in $installer, (Join-Path $NewDir "update.json"), (Join-Path $NewDir "ShadeNetDownloader-Update-$NewVersion.zip")) {
    if (-not (Test-Path $f)) { throw "missing $f. Build both releases first:  pwsh tools\build-release.ps1 -Version $OldVersion -SkipTests  (copy dist\* to $OldDir), then -Version $NewVersion (copy to $NewDir)" }
}

$work = Join-Path $env:TEMP ("sn-upd-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$inst = Join-Path $work "Program Files Like\ShadeNet Downloader"
$data = Join-Path $work "data"; $sm = Join-Path $work "startmenu"; $dt = Join-Path $work "desktop"
$bad = Join-Path $work "tampered"
$regRoot = "Software\ShadeNetTests\UpdateE2E-" + [guid]::NewGuid().ToString("N").Substring(0, 8)
New-Item -ItemType Directory $work, $data, $bad | Out-Null
$ok = $true
function Check([string]$what, [bool]$cond, [string]$detail = "") {
    Write-Host ("  [{0}] {1} {2}" -f $(if ($cond) { "ok  " } else { "FAIL" }), $what, $detail)
    if (-not $cond) { $script:ok = $false }
}
function Shot($win, [string]$name) {
    $h = [IntPtr]$win.Current.NativeWindowHandle
    $r = New-Object SUpd+RECT; [SUpd]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc(); [SUpd]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc)
    $bmp.Save((Join-Path $docs "$name.png")); $g.Dispose(); $bmp.Dispose()
}
function MainWindow([int]$processId, [int]$seconds = 30) {
    for ($i = 0; $i -lt $seconds * 4; $i++) {
        foreach ($w in $AE::RootElement.FindAll($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $processId)))) {
            if ($w.Current.NativeWindowHandle -ne 0 -and $w.Current.Name) { return $w }
        }
        Start-Sleep -Milliseconds 250
    }
    return $null
}
function ById($win, [string]$id) { $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id))) }
function ByName($win, [string]$name, $type) {
    $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)), (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type)))))
}
function Press($e) { ($e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
function Status($win) { $e = ById $win "UpdateStatus"; if ($e) { $e.Current.Name } else { "" } }
function WaitStatus($win, [string]$pattern, [int]$seconds = 60) {
    for ($i = 0; $i -lt $seconds * 2; $i++) { $s = Status $win; if ($s -match $pattern) { return $s }; Start-Sleep -Milliseconds 500 }
    return (Status $win)
}
function OpenAbout($win) {
    Press (ByName $win "Settings" $ct::Button); Start-Sleep -Milliseconds 700
    ((ByName $win "About" $ct::RadioButton).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select(); Start-Sleep -Milliseconds 700
}
function StartServer([string]$folder) {
    $out = Join-Path $work ("srv-" + [guid]::NewGuid().ToString("N").Substring(0, 6) + ".txt")
    $p = Start-Process node -ArgumentList (Join-Path $PSScriptRoot "static-server.mjs"), $folder -RedirectStandardOutput $out -PassThru -WindowStyle Hidden
    for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 300; if (Test-Path $out) { $m = Select-String -Path $out -Pattern "READY (\d+)" | Select-Object -First 1; if ($m) { return @{ Process = $p; Port = $m.Matches[0].Groups[1].Value } } } }
    throw "the stand-in server did not start"
}
function StartApp([int]$port) {
    $env:SHADENET_DATA_DIR = $data
    $env:SHADENET_UPDATE_URL = "http://127.0.0.1:$port/update.json"
    Start-Process (Join-Path $inst "ShadeNetDownloader.exe") -PassThru
}

$servers = @(); $apps = @(); $scriptStart = Get-Date
try {
    Write-Host "1. install release $OldVersion (silently, in a sandbox)"
    $p = Start-Process $installer -ArgumentList @("/S", "/D=`"$inst`"", "--registry-root", $regRoot, "--start-menu-dir", "`"$sm`"", "--desktop-dir", "`"$dt`"", "--data-dir", "`"$data`"", "--no-browser-integration") -Wait -PassThru
    Check "installed (exit 0)" ($p.ExitCode -eq 0)
    $exe = Join-Path $inst "ShadeNetDownloader.exe"
    Check "the program is version $OldVersion" ((Get-Item $exe).VersionInfo.ProductVersion -like "$OldVersion*") ((Get-Item $exe).VersionInfo.ProductVersion)
    $oldSetupHash = (Get-FileHash (Join-Path $inst "Uninstall.exe")).Hash
    $ytdlpHash = (Get-FileHash (Join-Path $inst "tools\yt-dlp\yt-dlp.exe")).Hash
    $ffmpegHash = (Get-FileHash (Join-Path $inst "tools\ffmpeg\ffmpeg.exe")).Hash
    Set-Content (Join-Path $data "marker.txt") "the user's own data"
    @{ Theme = "Dark"; General = @{ ShowTrayIcon = $false } } | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $data "settings.json")

    Write-Host "`n2. a package that was tampered with is refused"
    Copy-Item (Join-Path $NewDir "update.json") $bad
    $zipName = "ShadeNetDownloader-Update-$NewVersion.zip"
    Copy-Item (Join-Path $NewDir $zipName) (Join-Path $bad $zipName)
    $stream = [IO.File]::Open((Join-Path $bad $zipName), "Open", "ReadWrite")
    try { $stream.Seek(5000000, "Begin") | Out-Null; $b = $stream.ReadByte(); $stream.Seek(5000000, "Begin") | Out-Null; $stream.WriteByte([byte](($b + 1) % 256)) } finally { $stream.Dispose() }
    $srv = StartServer $bad; $servers += $srv.Process
    $app = StartApp $srv.Port; $apps += $app
    $win = MainWindow $app.Id
    Check "the program starts" ([bool]$win)
    [SUpd]::MoveWindow([IntPtr]$win.Current.NativeWindowHandle, 60, 20, 1180, 800, $true) | Out-Null
    OpenAbout $win
    Press (ById $win "CheckForUpdates")
    $status = WaitStatus $win "is available|could not|Could not"
    Check "it finds version $NewVersion" ($status -like "*$NewVersion is available*") "($status)"
    Press (ById $win "InstallUpdate")
    $failure = WaitStatus $win "checksum|could not|Could not|did not" 90
    Check "the damaged download is refused, with the reason" ($failure -like "*checksum*") "($failure)"
    Shot $win "update-0-refused"
    Check "the program is still running (nothing was replaced)" (-not $app.HasExited)
    Check "it is still version $OldVersion" ((Get-Item $exe).VersionInfo.ProductVersion -like "$OldVersion*")
    Check "no damaged package is kept" (@(Get-ChildItem (Join-Path $data "updates") -Recurse -File -ErrorAction SilentlyContinue).Count -eq 0)
    $app.Kill(); $app.WaitForExit(5000) | Out-Null
    $servers[0].Kill(); $servers = @()
    Start-Sleep -Seconds 2

    Write-Host "`n3. the real update"
    $srv = StartServer $NewDir; $servers += $srv.Process
    $app = StartApp $srv.Port; $apps += $app
    $win = MainWindow $app.Id
    [SUpd]::MoveWindow([IntPtr]$win.Current.NativeWindowHandle, 60, 20, 1180, 800, $true) | Out-Null
    OpenAbout $win
    Shot $win "update-1-about"
    Press (ById $win "CheckForUpdates")
    $status = WaitStatus $win "is available|could not|Could not"
    Check "it finds version $NewVersion" ($status -like "*$NewVersion is available*") "($status)"
    Shot $win "update-2-available"
    $install = ById $win "InstallUpdate"
    Check "the Update button says 'Update to $NewVersion'" ($install -and $install.Current.Name -eq "Update to $NewVersion") ($install.Current.Name)

    $quitWatch = [Diagnostics.Stopwatch]::StartNew()
    Press $install
    Check "the program quits for the update" ($app.WaitForExit(120000)) ("after {0:N0} s" -f $quitWatch.Elapsed.TotalSeconds)

    # the update window (a copy of the uninstaller in the temp folder)
    $helper = $null; $shot = $false
    for ($i = 0; $i -lt 240 -and -not $helper; $i++) {
        $helper = Get-Process | Where-Object { $_.ProcessName -like "ShadeNetUpdate-*" } | Select-Object -First 1
        if (-not $helper) { Start-Sleep -Milliseconds 250 }
    }
    Check "the update window appears" ([bool]$helper)
    if ($helper) {
        $uw = MainWindow $helper.Id 10
        if ($uw) { Shot $uw "update-3-updating"; Check "it says it is updating" ((($uw.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) -join "|") -like "*Updating ShadeNet Downloader*") }
    }

    $new = $null
    for ($i = 0; $i -lt 240 -and -not $new; $i++) {
        $new = Get-Process ShadeNetDownloader -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($inst, [StringComparison]::OrdinalIgnoreCase) -and $_.StartTime -gt $scriptStart.AddSeconds(-1) -and $_.Id -ne $app.Id } | Select-Object -First 1
        if (-not $new) { Start-Sleep -Milliseconds 500 }
    }
    Check "the new version starts by itself" ([bool]$new)
    if ($new) { $apps += $new }

    Write-Host "`n4. what the update left"
    Check "the program is now version $NewVersion" ((Get-Item $exe).VersionInfo.ProductVersion -like "$NewVersion*") ((Get-Item $exe).VersionInfo.ProductVersion)
    Check "the uninstaller was updated too" ((Get-FileHash (Join-Path $inst "Uninstall.exe")).Hash -ne $oldSetupHash)
    Check "yt-dlp is the one that was there (tools\ untouched)" ((Get-FileHash (Join-Path $inst "tools\yt-dlp\yt-dlp.exe")).Hash -eq $ytdlpHash)
    Check "ffmpeg is the one that was there" ((Get-FileHash (Join-Path $inst "tools\ffmpeg\ffmpeg.exe")).Hash -eq $ffmpegHash)
    Check "the user's data is untouched" ((Get-Content (Join-Path $data "marker.txt")) -eq "the user's own data")
    Check "shortcuts are still there" ((Test-Path (Join-Path $sm "ShadeNet Downloader.lnk")) -and (Test-Path (Join-Path $dt "ShadeNet Downloader.lnk")))
    $manifest = Get-Content (Join-Path $inst "install.json") -Raw | ConvertFrom-Json
    Check "install.json: version $NewVersion, still listing tools\" (($manifest.Version -eq $NewVersion) -and (@($manifest.Files | Where-Object { $_ -like "tools\*" }).Count -gt 0)) "($($manifest.Version), $(@($manifest.Files).Count) files)"
    $entry = Get-ItemProperty "HKCU:\$regRoot\Microsoft\Windows\CurrentVersion\Uninstall\ShadeNetDownloader" -ErrorAction SilentlyContinue
    Check "Apps & features says $NewVersion" ($entry.DisplayVersion -eq $NewVersion) "($($entry.DisplayVersion))"
    Check "no working folder is left in the installation" (-not (Test-Path (Join-Path $inst ".update")))
    Start-Sleep -Seconds 6
    Check "the temporary updater is gone" (-not (Get-Process | Where-Object { $_.ProcessName -like "ShadeNetUpdate-*" }))
    Check "and its file" (@(Get-ChildItem $env:TEMP -Filter "ShadeNetUpdate-*.exe" -ErrorAction SilentlyContinue).Count -eq 0)

    Write-Host "`n5. the new program says so, and has nothing newer to offer"
    if ($new) {
        $win2 = MainWindow $new.Id
        [SUpd]::MoveWindow([IntPtr]$win2.Current.NativeWindowHandle, 60, 20, 1180, 800, $true) | Out-Null
        OpenAbout $win2
        $texts = ($win2.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) -join "|"
        Check "About says Version $NewVersion" ($texts -like "*Version $NewVersion*")
        Press (ById $win2 "CheckForUpdates")
        $status = WaitStatus $win2 "newest|could not|Could not"
        Check "'Check for updates' says it is the newest" ($status -like "*newest version*") "($status)"
        Shot $win2 "update-4-updated"
    }
}
catch {
    Write-Host "  error: $($_.Exception.Message)" -ForegroundColor Red
    $ok = $false
}
finally {
    foreach ($a in $apps) { if ($a -and -not $a.HasExited) { try { $a.Kill() } catch { } } }
    Get-Process ShadeNetDownloader, ShadeNet.NativeHost, ShadeNetSetup -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($work, [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { $_.Kill() }
    Get-Process | Where-Object { $_.ProcessName -like "ShadeNetUpdate-*" -and $_.StartTime -gt $scriptStart } | ForEach-Object { $_.Kill() }
    foreach ($s in $servers) { if ($s -and -not $s.HasExited) { $s.Kill() } }
    Remove-Item Env:SHADENET_UPDATE_URL -ErrorAction SilentlyContinue; Remove-Item Env:SHADENET_DATA_DIR -ErrorAction SilentlyContinue
    Remove-Item "HKCU:\$regRoot" -Recurse -ErrorAction SilentlyContinue
    if ((Get-ChildItem "HKCU:\Software\ShadeNetTests" -ErrorAction SilentlyContinue).Count -eq 0) { Remove-Item "HKCU:\Software\ShadeNetTests" -ErrorAction SilentlyContinue }
    Get-ChildItem $env:TEMP -Filter "ShadeNetUpdate-*.exe" -ErrorAction SilentlyContinue | Where-Object { $_.CreationTime -gt $scriptStart } | Remove-Item -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host ""
if ($ok) { Write-Host "PASS" -ForegroundColor Green; exit 0 } else { Write-Host "FAIL" -ForegroundColor Red; exit 1 }
