<#
.SYNOPSIS
  Step 11 check on the real app with a REAL stream made by ffmpeg: the Add dialog recognises the playlist (quality
  picker, length, estimated size), the download runs piece by piece, the pieces are joined into one playable MP4
  (docs/step11-*.png). Uses a throw-away data folder and a local server.
#>
param([ValidateSet("Dark", "Light")] [string]$Theme = "Dark")

$ErrorActionPreference = "Stop"
$env:DOTNET_ROOT = "C:\Users\Shade\.dotnet"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$docs = Join-Path $root "docs"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public class S11 {
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
    $r = New-Object S11+RECT; [S11]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc(); [S11]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc)
    $bmp.Save("$docs\$name.png"); $g.Dispose(); $bmp.Dispose()
}
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
function Find($r, $name, $ct) { $c = New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)), (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ct))); $r.FindFirst($TS::Descendants, $c) }
$btn = [System.Windows.Automation.ControlType]::Button; $edit = [System.Windows.Automation.ControlType]::Edit
function Click($win, $name) { ((Find $win $name $btn).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }

$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) { throw "this check needs ffmpeg on the PATH" }

$work = Join-Path $env:TEMP ("sn-s11-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$data = Join-Path $work "data"; $dl = Join-Path $work "dl"; $site = Join-Path $work "site"
New-Item -ItemType Directory $data, $dl, (Join-Path $site "holiday") | Out-Null
$app = $null; $server = $null
try {
    # a real stream: three qualities, 40 s of picture and sound, 4 s pieces
    foreach ($q in @(@("360", "480x360", "400k"), @("720", "1280x720", "1500k"))) {
        $dir = Join-Path $site "holiday\$($q[0])"; New-Item -ItemType Directory $dir | Out-Null
        & $ffmpeg -y -loglevel error -f lavfi -i "testsrc2=duration=40:size=$($q[1]):rate=25" -f lavfi -i "sine=frequency=440:duration=40" `
            -c:v libx264 -preset ultrafast -b:v $q[2] -pix_fmt yuv420p -g 100 -c:a aac -b:a 96k -f hls -hls_time 4 -hls_playlist_type vod (Join-Path $dir "index.m3u8")
    }
    Set-Content (Join-Path $site "holiday\master.m3u8") "#EXTM3U`n#EXT-X-STREAM-INF:BANDWIDTH=500000,RESOLUTION=480x360`n360/index.m3u8`n#EXT-X-STREAM-INF:BANDWIDTH=1700000,RESOLUTION=1280x720`n720/index.m3u8`n"

    $serverOut = Join-Path $work "server.txt"
    $server = Start-Process node -ArgumentList (Join-Path $PSScriptRoot "static-server.mjs"), $site, "3000" -RedirectStandardOutput $serverOut -PassThru -WindowStyle Hidden
    $port = $null
    for ($i = 0; $i -lt 40 -and -not $port; $i++) { Start-Sleep -Milliseconds 300; if (Test-Path $serverOut) { $m = Select-String -Path $serverOut -Pattern "READY (\d+)" | Select-Object -First 1; if ($m) { $port = $m.Matches[0].Groups[1].Value } } }
    if (-not $port) { throw "static server did not start" }
    $url = "http://127.0.0.1:$port/holiday/master.m3u8"

    @{ Theme = $Theme; Engine = @{ DownloadRoot = $dl; UseCategoryFolders = $false }; General = @{ ShowTrayIcon = $false } } | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $data "settings.json")
    $env:SHADENET_DATA_DIR = $data
    $app = Start-Process (Join-Path $root "src\ShadeNetDownloader\bin\Debug\net10.0-windows\ShadeNetDownloader.exe") -PassThru
    Start-Sleep -Seconds 4
    $h = [S11]::ForPid($app.Id); $win = $AE::FromHandle($h); [S11]::SetForegroundWindow($h) | Out-Null

    Click $win "Add download"; Start-Sleep -Milliseconds 900
    ((Find $win "Web address" $edit).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($url)
    Start-Sleep -Milliseconds 3500
    Shot $h "step11-0-add-dialog"
    Write-Host "quality picker shown: $([bool]($win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'Quality')))))"

    Click $win "Download now"; Start-Sleep -Seconds 5
    Shot $h "step11-1-downloading"

    $target = Join-Path $dl "holiday.mp4"
    for ($i = 0; $i -lt 120 -and -not (Test-Path $target); $i++) { Start-Sleep -Milliseconds 500 }
    Start-Sleep -Seconds 1
    Shot $h "step11-2-completed"

    Write-Host "file saved: $(Test-Path $target)"
    if (Test-Path $target) {
        $info = & $ffmpeg -hide_banner -v error -i $target -f null - 2>&1
        Write-Host "decodes cleanly: $([string]::IsNullOrWhiteSpace(($info | Out-String)))"
        $probe = (& $ffmpeg -hide_banner -i $target 2>&1 | Out-String)
        Write-Host ("streams: " + (($probe -split "`n" | Where-Object { $_ -match "Duration|Stream #" } | ForEach-Object { $_.Trim() }) -join " | "))
    }
    Write-Host "work folder gone: $(-not (Test-Path (Join-Path $dl 'holiday.mp4.sndhls')))"
}
finally {
    $crash = Join-Path $data "crash.log"
    if (Test-Path $crash) { Write-Host "=== crash.log"; Get-Content $crash | Select-Object -First 25 | ForEach-Object { Write-Host $_ } }
    foreach ($p in @($app, $server)) { if ($p -and -not $p.HasExited) { $p.Kill() } }
    Start-Sleep -Seconds 1
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
