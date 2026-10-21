<#
.SYNOPSIS
  Step 9 check on the real app: every Settings section and the Queued page (docs/step9-*.png), then close-to-tray
  (the window hides, the process and its downloads keep going) and coming back through a second launch.
  Uses a throw-away data folder; the tray icon is real, so it is removed when the process ends.
#>
param(
    [ValidateSet("Dark", "Light")] [string]$Theme = "Dark",
    [string]$Suffix = ""
)

$ErrorActionPreference = "Stop"
$env:DOTNET_ROOT = "C:\Users\Shade\.dotnet"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$docs = Join-Path $root "docs"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public class S9 {
 public delegate bool EnumProc(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
 // the first top-level window of a process that is visible (the main window; hidden ones are skipped)
 public static IntPtr VisibleFor(uint pid){ IntPtr f=IntPtr.Zero; EnumWindows((h,_)=>{ uint x; GetWindowThreadProcessId(h,out x); if(x==pid && IsWindowVisible(h)){ f=h; return false;} return true;}, IntPtr.Zero); return f; }
}
'@
function Shot([IntPtr]$h, [string]$name) {
    $r = New-Object S9+RECT; [S9]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc(); [S9]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc)
    $bmp.Save("$docs\$name$Suffix.png"); $g.Dispose(); $bmp.Dispose()
}
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
function Find($r, $name, $ct) { $c = New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)), (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ct))); $r.FindFirst($TS::Descendants, $c) }
$btn = [System.Windows.Automation.ControlType]::Button; $edit = [System.Windows.Automation.ControlType]::Edit; $radio = [System.Windows.Automation.ControlType]::RadioButton
function Click($win, $name) { ((Find $win $name $btn).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
function Pick($win, $name) { $e = Find $win $name $radio; if (-not $e) { $all = $win.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $radio))); throw "radio '$name' not found; radios: " + (($all | ForEach-Object { "[" + $_.Current.Name + "]" }) -join " ") }; ($e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select() }

$work = Join-Path $env:TEMP ("sn-s9-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$data = Join-Path $work "data"; $dl = Join-Path $work "dl"
New-Item -ItemType Directory $data, $dl | Out-Null
$serveOut = Join-Path $work "serve.txt"
$server = $null; $app = $null; $second = $null
try {
    $server = Start-Process "C:\Users\Shade\.dotnet\dotnet.exe" -ArgumentList (Join-Path $root "tools\ShadeNet.Cli\bin\Debug\net10.0\ShadeNet.Cli.dll"), "serve" -RedirectStandardOutput $serveOut -PassThru -WindowStyle Hidden
    $urls = @()
    for ($i = 0; $i -lt 40 -and $urls.Count -lt 4; $i++) { Start-Sleep -Milliseconds 400; if (Test-Path $serveOut) { $urls = @(Get-Content $serveOut | Where-Object { $_ -like "DEMO *" } | ForEach-Object { $_.Substring(5).Trim() }) } }
    $port = ([regex]::Match($urls[0], ':(\d+)/')).Groups[1].Value

    # one download at a time, so the others wait in the queue; a custom category, a per-site limit and a schedule to look at
    $settings = @{
        Theme = $Theme
        Engine = @{
            DownloadRoot = $dl; UseCategoryFolders = $false; MaxConcurrentJobs = 1
            CustomCategories = @(@{ Name = "Design"; Extensions = @("psd", "ai", "sketch") })
            CategoryExtensions = @{ Video = @("mp4", "mkv", "avi", "webm", "mov") }
            HostConnectionLimits = @{ "files.example.com" = 2 }
        }
        General = @{ ShowTrayIcon = $true; CloseToTray = $true; TrayHintShown = $true }
        Schedule = @{ Enabled = $true; Start = (Get-Date).AddHours(-1).ToString("HH:mm:00"); Stop = (Get-Date).AddHours(3).ToString("HH:mm:00") }
    } | ConvertTo-Json -Depth 5
    Set-Content (Join-Path $data "settings.json") $settings
    $env:SHADENET_DATA_DIR = $data
    $exe = Join-Path $root "src\ShadeNetDownloader\bin\Debug\net10.0-windows\ShadeNetDownloader.exe"
    $app = Start-Process $exe -PassThru
    Start-Sleep -Seconds 4
    $h = [S9]::VisibleFor($app.Id)
    if ($h -eq [IntPtr]::Zero) { throw "no window" }
    $win = $AE::FromHandle($h); [S9]::SetForegroundWindow($h) | Out-Null

    # three downloads: one runs, two wait
    foreach ($n in 1..3) {
        $u = "http://127.0.0.1:$port/file/$((50 + $n * 20) * 1048576)?name=archive-part-$n.zip&etag=p$n&kbps=512&u=" + [guid]::NewGuid().ToString("N").Substring(0, 8)
        Click $win "Add download"; Start-Sleep -Milliseconds 900
        ((Find $win "Web address" $edit).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($u)
        Start-Sleep -Milliseconds 2500
        if ($n -eq 1) { Click $win "Download now" } else { Click $win "Add to queue" }; Start-Sleep -Milliseconds 1200
    }
    Start-Sleep -Seconds 3

    # the Queued page: start/stop button, order
    $queued = $win.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, "Queued")))
    foreach ($q in $queued) { try { ($q.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); break } catch { } }
    Start-Sleep -Milliseconds 900
    Shot $h "step9-0-queue"
    $stop = Find $win "Stop queue" $btn
    Write-Host "queue page has a 'Stop queue' button: $([bool]$stop)"
    if ($stop) { ($stop.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); Start-Sleep -Milliseconds 1200 }
    Shot $h "step9-1-queue-stopped"
    Write-Host "after stopping, 'Start queue' is offered: $([bool](Find $win 'Start queue' $btn))"
    $start = Find $win "Start queue" $btn
    if ($start) { ($start.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); Start-Sleep -Milliseconds 800 }

    # settings, section by section
    Click $win "Settings"; Start-Sleep -Milliseconds 900
    $i = 2
    foreach ($section in "General", "Downloads", "Categories", "Queue", "Network", "Browser", "Appearance", "About") {
        Pick $win $section; Start-Sleep -Milliseconds 700
        Shot $h ("step9-{0}-settings-{1}" -f $i, $section.ToLower())
        $i++
    }

    # close-to-tray: the window goes, the program stays
    Pick $win "General"
    ($win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close(); Start-Sleep -Milliseconds 1500
    $app.Refresh()
    $hidden = [S9]::VisibleFor($app.Id) -eq [IntPtr]::Zero
    Write-Host "after the close button: process alive = $(-not $app.HasExited), window hidden = $hidden"

    # a second launch brings the first one back (and exits itself)
    $second = Start-Process $exe -PassThru
    $second.WaitForExit(15000) | Out-Null
    Start-Sleep -Milliseconds 1200
    $back = [S9]::VisibleFor($app.Id)
    Write-Host "second launch exited = $($second.HasExited), first window visible again = $($back -ne [IntPtr]::Zero)"
}
finally {
    $crash = Join-Path $data "crash.log"
    if (Test-Path $crash) { Write-Host "=== crash.log"; Get-Content $crash | Select-Object -First 25 | ForEach-Object { Write-Host $_ } }
    foreach ($p in @($second, $app, $server)) { if ($p -and -not $p.HasExited) { $p.Kill() } }
    Start-Sleep -Seconds 1
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
