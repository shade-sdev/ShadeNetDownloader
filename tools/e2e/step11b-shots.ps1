<#
.SYNOPSIS
  Live recording, DASH and subtitles on the real app with REAL streams made by ffmpeg:
    1. a live HLS stream (ffmpeg -re, a sliding window of 2 s pieces): the dialog says it is recorded, the card shows
       "Recording", "Stop and save" ends it, the MP4 plays for about as long as it was recorded;
    2. a DASH stream (video + audio kept apart) with a WebVTT subtitle track: one MP4 with picture and sound, and a
       .vtt file next to it.
  Screenshots go to docs/step11b-*.png. Uses a throw-away data folder and a local server.
#>
param([ValidateSet("Dark", "Light")] [string]$Theme = "Dark")

$ErrorActionPreference = "Stop"
$env:DOTNET_ROOT = "C:\Users\Shade\.dotnet"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$docs = Join-Path $root "docs"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public class S11b {
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
    $r = New-Object S11b+RECT; [S11b]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc(); [S11b]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc)
    $bmp.Save("$docs\$name.png"); $g.Dispose(); $bmp.Dispose()
}
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
function Find($r, $name, $ct) { $c = New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)), (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ct))); $r.FindFirst($TS::Descendants, $c) }
$btn = [System.Windows.Automation.ControlType]::Button; $edit = [System.Windows.Automation.ControlType]::Edit
function Click($win, $name) { $e = Find $win $name $btn; if (-not $e) { throw "button '$name' not found" }; ($e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
function AddLink($win, $url, $wait = 3500) {
    Click $win "Add download"; Start-Sleep -Milliseconds 900
    ((Find $win "Web address" $edit).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($url)
    Start-Sleep -Milliseconds $wait
}
function Probe($file) { (& $ffmpeg -hide_banner -i $file 2>&1 | Out-String) }

$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) { throw "this check needs ffmpeg on the PATH" }

$work = Join-Path $env:TEMP ("sn-s11b-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$data = Join-Path $work "data"; $dl = Join-Path $work "dl"; $site = Join-Path $work "site"
New-Item -ItemType Directory $data, $dl, (Join-Path $site "live"), (Join-Path $site "dash") | Out-Null
$app = $null; $server = $null; $encoder = $null
try {
    # 1. a live HLS stream, produced in real time
    $encoder = Start-Process $ffmpeg -ArgumentList @("-loglevel", "error", "-re", "-f", "lavfi", "-i", "testsrc2=size=480x360:rate=25", "-f", "lavfi", "-i", "sine=frequency=500",
        "-t", "70", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "50", "-c:a", "aac", "-b:a", "64k",
        "-f", "hls", "-hls_time", "2", "-hls_list_size", "6", "-hls_flags", "delete_segments", (Join-Path $site "live\index.m3u8")) -PassThru -WindowStyle Hidden

    # 2. a DASH stream with separate audio, and a subtitle track
    Push-Location (Join-Path $site "dash")
    & $ffmpeg -y -loglevel error -f lavfi -i "testsrc=duration=8:size=320x240:rate=25" -f lavfi -i "sine=frequency=300:duration=8" -map 0:v -map 1:a `
        -c:v libx264 -preset ultrafast -pix_fmt yuv420p -g 50 -c:a aac -f dash -seg_duration 2 -use_template 1 -use_timeline 1 `
        -init_seg_name 'init-$RepresentationID$.m4s' -media_seg_name 'chunk-$RepresentationID$-$Number%05d$.m4s' -adaptation_sets "id=0,streams=v id=1,streams=a" stream.mpd
    Pop-Location
    Set-Content (Join-Path $site "dash\en.vtt") "WEBVTT`n`n00:00:01.000 --> 00:00:03.000`nHello from the subtitles`n"
    $mpd = Get-Content (Join-Path $site "dash\stream.mpd") -Raw
    $mpd = $mpd.Replace("</Period>", '<AdaptationSet id="2" contentType="text" mimeType="text/vtt" lang="en"><Representation id="9" bandwidth="1000"><BaseURL>en.vtt</BaseURL></Representation></AdaptationSet></Period>')
    Set-Content (Join-Path $site "dash\stream.mpd") $mpd

    $serverOut = Join-Path $work "server.txt"
    $server = Start-Process node -ArgumentList (Join-Path $PSScriptRoot "static-server.mjs"), $site -RedirectStandardOutput $serverOut -PassThru -WindowStyle Hidden
    $port = $null
    for ($i = 0; $i -lt 40 -and -not $port; $i++) { Start-Sleep -Milliseconds 300; if (Test-Path $serverOut) { $m = Select-String -Path $serverOut -Pattern "READY (\d+)" | Select-Object -First 1; if ($m) { $port = $m.Matches[0].Groups[1].Value } } }
    if (-not $port) { throw "static server did not start" }

    @{ Theme = $Theme; Engine = @{ DownloadRoot = $dl; UseCategoryFolders = $false }; General = @{ ShowTrayIcon = $false } } | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $data "settings.json")
    $env:SHADENET_DATA_DIR = $data
    $app = Start-Process (Join-Path $root "src\ShadeNetDownloader\bin\Debug\net10.0-windows\ShadeNetDownloader.exe") -PassThru
    Start-Sleep -Seconds 4
    $h = [S11b]::ForPid($app.Id); $win = $AE::FromHandle($h); [S11b]::SetForegroundWindow($h) | Out-Null

    # ---- live ----
    for ($i = 0; $i -lt 30 -and -not (Test-Path (Join-Path $site "live\index.m3u8")); $i++) { Start-Sleep -Milliseconds 300 }
    Start-Sleep -Seconds 4   # a few pieces exist
    AddLink $win "http://127.0.0.1:$port/live/index.m3u8"
    Shot $h "step11b-0-live-dialog"
    Click $win "Download now"; Start-Sleep -Seconds 11
    Shot $h "step11b-1-recording"
    Click $win "Stop and save the recording"
    $liveFile = Join-Path $dl "live.mp4"
    for ($i = 0; $i -lt 60 -and -not (Test-Path $liveFile); $i++) { Start-Sleep -Milliseconds 500 }
    Start-Sleep -Seconds 1
    Shot $h "step11b-2-saved"
    Write-Host "live recording saved: $(Test-Path $liveFile)"
    if (Test-Path $liveFile) {
        $p = Probe $liveFile
        Write-Host ("  " + ((($p -split "`n") | Where-Object { $_ -match "Duration|Video:|Audio:" } | ForEach-Object { $_.Trim() }) -join " | "))
        $clean = (& $ffmpeg -hide_banner -v error -i $liveFile -f null - 2>&1 | Out-String).Trim()
        Write-Host "  decodes cleanly: $($clean.Length -eq 0)"
    }

    # ---- DASH + subtitles ----
    AddLink $win "http://127.0.0.1:$port/dash/stream.mpd"
    Shot $h "step11b-3-dash-dialog"
    Click $win "Download now"
    $dashFile = Join-Path $dl "dash.mp4"
    for ($i = 0; $i -lt 60 -and -not (Test-Path $dashFile); $i++) { Start-Sleep -Milliseconds 500 }
    Start-Sleep -Seconds 2
    Shot $h "step11b-4-dash-saved"
    Write-Host "dash file saved: $(Test-Path $dashFile)"
    if (Test-Path $dashFile) {
        Write-Host ("  " + ((((Probe $dashFile) -split "`n") | Where-Object { $_ -match "Duration|Video:|Audio:" } | ForEach-Object { $_.Trim() }) -join " | "))
        $sub = Join-Path $dl "dash.en.vtt"
        Write-Host "  subtitle file next to it: $(Test-Path $sub)  ($(if (Test-Path $sub) { (Get-Content $sub -Raw) -replace '\s+', ' ' }))"
    }
    Write-Host "work folders gone: $(-not (Get-ChildItem $dl -Directory -ErrorAction SilentlyContinue))"
}
finally {
    $crash = Join-Path $data "crash.log"
    if (Test-Path $crash) { Write-Host "=== crash.log"; Get-Content $crash | Select-Object -First 25 | ForEach-Object { Write-Host $_ } }
    foreach ($p in @($app, $server, $encoder)) { if ($p -and -not $p.HasExited) { $p.Kill() } }
    Start-Sleep -Seconds 1
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
