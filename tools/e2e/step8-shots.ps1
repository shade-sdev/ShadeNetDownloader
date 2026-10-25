<#
.SYNOPSIS
  Starts ShadeNet against the local test server with real multi-connection downloads, opens the details drawer and
  saves screenshots of each tab (docs/step8-*.png). Uses a throw-away data folder.
#>
param(
    [ValidateSet("Dark", "Light")] [string]$Theme = "Dark",
    [string]$Suffix = "",
    [int]$Width = 0, [int]$Height = 0,
    [switch]$Flyout
)

$ErrorActionPreference = "Stop"
$env:DOTNET_ROOT = "C:\Users\Shade\.dotnet"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$docs = Join-Path $root "docs"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public class S8 {
 public delegate bool EnumProc(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hgt, bool repaint);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
 [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h); public static IntPtr ForPid(uint pid){ IntPtr f=IntPtr.Zero; EnumWindows((h,_)=>{ uint x; GetWindowThreadProcessId(h,out x); if(x==pid && IsWindowVisible(h) && GetWindowTextLength(h)>0){ f=h; return false;} return true;}, IntPtr.Zero); return f; }
}
'@
function Shot([IntPtr]$h, [string]$name) {
    $r = New-Object S8+RECT; [S8]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc(); [S8]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc)
    $bmp.Save("$docs\$name$Suffix.png"); $g.Dispose(); $bmp.Dispose()
}
function ShotScreen([IntPtr]$h, [string]$name) {
    $r = New-Object S8+RECT; [S8]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
    $bmp.Save("$docs\$name$Suffix.png"); $g.Dispose(); $bmp.Dispose()
}
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
function Find($r, $name, $ct) { $c = New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)), (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ct))); $r.FindFirst($TS::Descendants, $c) }
$btn = [System.Windows.Automation.ControlType]::Button; $edit = [System.Windows.Automation.ControlType]::Edit; $radio = [System.Windows.Automation.ControlType]::RadioButton
function Click($win, $name) { ((Find $win $name $btn).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
function Pick($win, $name) { ((Find $win $name $radio).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select() }

$work = Join-Path $env:TEMP ("sn-s8-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$data = Join-Path $work "data"; $dl = Join-Path $work "dl"
New-Item -ItemType Directory $data, $dl | Out-Null
$serveOut = Join-Path $work "serve.txt"
$server = $null; $app = $null
try {
    $server = Start-Process "C:\Users\Shade\.dotnet\dotnet.exe" -ArgumentList (Join-Path $root "tools\ShadeNet.Cli\bin\Debug\net10.0\ShadeNet.Cli.dll"), "serve" -RedirectStandardOutput $serveOut -PassThru -WindowStyle Hidden
    $urls = @()
    for ($i = 0; $i -lt 40 -and $urls.Count -lt 4; $i++) { Start-Sleep -Milliseconds 400; if (Test-Path $serveOut) { $urls = @(Get-Content $serveOut | Where-Object { $_ -like "DEMO *" } | ForEach-Object { $_.Substring(5).Trim() }) } }

    $settings = @{ Theme = $Theme; Engine = @{ DownloadRoot = $dl; UseCategoryFolders = $false }; General = @{ ShowTrayIcon = $false } } | ConvertTo-Json -Depth 3
    Set-Content (Join-Path $data "settings.json") $settings
    $env:SHADENET_DATA_DIR = $data
    $app = Start-Process (Join-Path $root "src\ShadeNetDownloader\bin\Debug\net10.0-windows\ShadeNetDownloader.exe") -PassThru
    Start-Sleep -Seconds 4
    $h = [S8]::ForPid($app.Id)
    if ($Width -gt 0) { [S8]::MoveWindow($h, 60, 40, $Width, $Height, $true) | Out-Null; Start-Sleep -Milliseconds 500 }
    $win = $AE::FromHandle($h); [S8]::SetForegroundWindow($h) | Out-Null

    # two real downloads: a big one that splits into several connections, and a smaller one
    $port = ([regex]::Match($urls[0], ':(\d+)/')).Groups[1].Value
    $big = "http://127.0.0.1:$port/file/419430400?name=ubuntu-24.04-desktop-amd64.iso&etag=u&kbps=1024&u=" + [guid]::NewGuid().ToString("N").Substring(0, 8)
    $mid = "http://127.0.0.1:$port/file/157286400?name=conference-talk-2024.mp4&etag=c&kbps=768&u=" + [guid]::NewGuid().ToString("N").Substring(0, 8)
    foreach ($u in @($big, $mid)) {
        Click $win "Add download"; Start-Sleep -Milliseconds 900
        ((Find $win "Web address" $edit).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($u)
        Start-Sleep -Milliseconds 2500
        Click $win "Download now"; Start-Sleep -Milliseconds 1200
    }

    # let the engine ramp up: more connections, splits, a filled-in speed graph
    Start-Sleep -Seconds 17

    Shot $h "step8-0-list"
    # the big download is the older card (second in the list)
    $details = $win.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, "Details")))
    ($details[$details.Count - 1].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); Start-Sleep -Milliseconds 900
    Shot $h "step8-1-overview"
    Pick $win "Connections"; Start-Sleep -Milliseconds 700
    Shot $h "step8-2-connections"
    Pick $win "Properties"; Start-Sleep -Milliseconds 700
    Shot $h "step8-3-properties"
    if ($Flyout) {
        # the status-bar speed limit: open the flyout, choose a preset, see the status bar change
        Pick $win "Overview"; Start-Sleep -Milliseconds 400
        Click $win "Speed limit"; Start-Sleep -Milliseconds 900
        [S8]::SetForegroundWindow($h) | Out-Null
        ShotScreen $h "step8-4-flyout"
        $five = Find $AE::RootElement "5 MB/s" $btn   # a popup is its own top-level window
        if ($five) { ($five.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); Start-Sleep -Milliseconds 1500 }
        else { Write-Host "preset button not found in the UI tree" }
        # the popup stays open on a preset: close it with Esc, then close the drawer with Esc as well
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 800
        Shot $h "step8-5-limited"
        $label = (Find $win "Speed limit" $btn).Current.Name
        Write-Host "status bar limit button: $label"
    }
    # Esc closes the drawer, wherever the focus is
    Pick $win "Overview"; Start-Sleep -Milliseconds 300
    [S8]::SetForegroundWindow($h) | Out-Null
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 800
    $drawerOpen = [bool](Find $win "Close details" $btn)
    Write-Host "drawer still open after Esc: $drawerOpen"
    Shot $h "step8-6-closed"
    Write-Host "screenshots saved to $docs (suffix '$Suffix')"
}
finally {
    # a crash dialog in a screenshot means a bug: show what the app wrote before the folder is removed
    $crash = Join-Path $data "crash.log"
    if (Test-Path $crash) { Write-Host "=== crash.log"; Get-Content $crash | Select-Object -First 25 | ForEach-Object { Write-Host $_ } }
    if ($app -and -not $app.HasExited) { $app.Kill() }
    if ($server -and -not $server.HasExited) { $server.Kill() }
    Start-Sleep -Seconds 1
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
