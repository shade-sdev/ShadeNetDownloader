<#
.SYNOPSIS
  A real public Facebook video, end to end: Chrome (throw-away profile, the extension loaded) shows the "Download video"
  button on it (Facebook's player uses a buffer, so no file address exists), one click sends the PAGE to ShadeNet, the Add
  dialog asks yt-dlp and lists the qualities, ShadeNet downloads the video and the file decodes cleanly with picture and sound.

.DESCRIPTION
  Needs internet, ffmpeg on the PATH and tools\yt-dlp. Public video, no sign-in. Throw-away data, profile and downloads
  folders; the native-messaging registry entry is put back as it was. Screenshots: docs\facebook-*.png.
#>
param(
    [string]$Chrome = "C:\Program Files\Google\Chrome\Application\chrome.exe",
    [string]$AppExe = "",
    [string]$Url ="https://www.facebook.com/facebook/videos/10153231379946729/"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$docs = Join-Path $root "docs"
$env:DOTNET_ROOT = "C:\Users\Shade\.dotnet"
$appExe = if ($AppExe) { $AppExe } else { Join-Path $root "src\ShadeNetDownloader\bin\Debug\net10.0-windows\ShadeNetDownloader.exe" }
$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) { throw "this check needs ffmpeg on the PATH" }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public class SFb {
 public delegate bool EnumProc(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
 public static IntPtr ForPid(uint pid){ IntPtr f=IntPtr.Zero; EnumWindows((h,_)=>{ uint x; GetWindowThreadProcessId(h,out x); if(x==pid && IsWindowVisible(h)){ f=h; return false;} return true;}, IntPtr.Zero); return f; }
}
'@
function Shot([IntPtr]$h, [string]$name) {
    $r = New-Object SFb+RECT; [SFb]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc(); [SFb]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc)
    $bmp.Save("$docs\$name.png"); $g.Dispose(); $bmp.Dispose()
}
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]; $ct = [System.Windows.Automation.ControlType]
function Find($r, $name, $type) { $c = New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)), (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type))); $r.FindFirst($TS::Descendants, $c) }

$work = Join-Path $env:TEMP ("sn-fb-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$data = Join-Path $work "data"; $dl = Join-Path $work "dl"; $profile = Join-Path $work "chrome-profile"
New-Item -ItemType Directory $data, $dl, $profile | Out-Null
$env:SHADENET_DATA_DIR = $data
$keys = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.shadenet.downloader", "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.shadenet.downloader", "HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.shadenet.downloader"
$saved = @{}
foreach ($k in $keys) { if (Test-Path $k) { $saved[$k] = (Get-Item $k).GetValue("") } }

$app = $null; $result = 1; $scriptStart = Get-Date
try {
    @{ Theme = "Dark"; Engine = @{ DownloadRoot = $dl; UseCategoryFolders = $false }; General = @{ ShowTrayIcon = $false } } | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $data "settings.json")
    $install = Start-Process $appExe -ArgumentList "--install-browser-integration" -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "registering the browser integration failed" }
    $app = Start-Process $appExe -PassThru
    Start-Sleep -Seconds 4
    $h = [SFb]::ForPid($app.Id)

    # one phase: Chrome does its part, then ShadeNet's dialog is driven and the downloaded file checked
    function Phase([string]$name, [string[]]$extra, [int]$wait) {
        Write-Host "`n== $name"
        $before = @(Get-ChildItem $dl -File -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
        $profile2 = Join-Path $work ("chrome-" + [guid]::NewGuid().ToString("N").Substring(0, 6))
        New-Item -ItemType Directory $profile2 | Out-Null
        $out = & node (Join-Path $PSScriptRoot "chrome-page-e2e.mjs") --chrome $Chrome --extension (Join-Path $root "extension\chromium") --profile $profile2 --page $Url `
            --shot (Join-Path $docs "facebook-$name-page.png") --wait-ms $wait --expect-id bcegodlanfeghhakadmalbkbhbhbejop @extra 2>&1
        $json = ($out -join "`n") | ConvertFrom-Json
        Write-Host "  page title : $($json.pageTitle)"
        if ($json.pill.texts) { Write-Host "  button     : $(@($json.pill.texts) -join ' | ')" }
        Write-Host "  extension  : $($json.sent)"
        if (-not $json.ok) { Write-Host ($out -join "`n"); return $false }

        $win = $AE::FromHandle($h); [SFb]::SetForegroundWindow($h) | Out-Null
        $button = $null
        for ($i = 0; $i -lt 160 -and -not $button; $i++) { Start-Sleep -Milliseconds 500; $button = Find $win "Download now" $ct::Button }
        if (-not $button) { Shot $h "facebook-$name-nodialog"; Write-Host "  no dialog with a Download button"; return $false }
        Start-Sleep -Seconds 2
        $combo = Find $win "Quality" $ct::ComboBox
        Write-Host "  quality list in the dialog: $([bool]$combo)"
        Shot $h "facebook-$name-dialog"
        $win.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ct::Text))) | ForEach-Object { $_.Current.Name } |
            Where-Object { $_ -match "facebook|Video from|\.mp4|MB|just friends" } | Select-Object -First 6 | ForEach-Object { Write-Host "    $_" }
        ((Find $win "Download now" $ct::Button).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()

        $target = $null
        for ($i = 0; $i -lt 240 -and -not $target; $i++) {
            $target = Get-ChildItem $dl -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in ".mp4", ".mkv" -and $before -notcontains $_.Name } | Select-Object -First 1
            if (-not $target) { Start-Sleep -Milliseconds 500 }
        }
        Start-Sleep -Seconds 3
        Shot $h "facebook-$name-done"
        if (-not $target) { Write-Host "  file saved : none"; return $false }
        $clean = (& $ffmpeg -hide_banner -v error -i $target.FullName -f null - 2>&1 | Out-String).Trim()
        $info = (& $ffmpeg -hide_banner -i $target.FullName 2>&1 | Out-String)
        $dur = [regex]::Match($info, "Duration: (\d+:\d+:[\d.]+)").Groups[1].Value
        $good = $clean.Length -eq 0 -and ($info -match 'Video:') -and ($info -match 'Audio:')
        Write-Host "  file saved : $($target.Name) ($([math]::Round($target.Length / 1MB, 1)) MB, $dur); decodes cleanly with picture and sound: $good"
        return $good
    }

    $a = Phase "1-button" @() 70000
    $b = Phase "2-page" @("--mode", "page") 8000
    Write-Host "`nbutton route: $a   page route (yt-dlp): $b"
    if ($a -and $b) { $result = 0; Write-Host "PASS" -ForegroundColor Green } else { Write-Host "FAIL" -ForegroundColor Red }
}
finally {
    $crash = Join-Path $data "crash.log"
    if (Test-Path $crash) { Write-Host "=== crash.log"; Get-Content $crash | Select-Object -First 20 | ForEach-Object { Write-Host $_ } }
    try { & $appExe --uninstall-browser-integration; Start-Sleep -Milliseconds 800 } catch { }
    foreach ($k in $saved.Keys) { New-Item -Path $k -Force | Out-Null; Set-Item -Path $k -Value $saved[$k] }
    if ($app -and -not $app.HasExited) { $app.Kill() }
    Get-Process ShadeNetDownloader, ShadeNet.NativeHost -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt $scriptStart } | ForEach-Object { $_.Kill() }
    Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine -like "*$profile*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
exit $result
