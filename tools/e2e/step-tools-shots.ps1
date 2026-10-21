<#
.SYNOPSIS
  Settings > Downloads > Helper programs on the real app: where yt-dlp and ffmpeg come from, their versions, "Check" against
  the real GitHub release pages (yt-dlp: up to date or an offer; ffmpeg: found on the PATH, so ShadeNet leaves it alone).
  Screenshots: docs\tools-*.png. Throw-away data folder; nothing is installed.
#>
param([ValidateSet("Dark", "Light")] [string]$Theme = "Dark")

$ErrorActionPreference = "Stop"
$env:DOTNET_ROOT = "C:\Users\Shade\.dotnet"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$docs = Join-Path $root "docs"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public class STools {
 public delegate bool EnumProc(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hgt, bool repaint);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
 public static IntPtr ForPid(uint pid){ IntPtr f=IntPtr.Zero; EnumWindows((h,_)=>{ uint x; GetWindowThreadProcessId(h,out x); if(x==pid && IsWindowVisible(h)){ f=h; return false;} return true;}, IntPtr.Zero); return f; }
}
'@
function Shot([IntPtr]$h, [string]$name) {
    $r = New-Object STools+RECT; [STools]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc(); [STools]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc)
    $bmp.Save("$docs\$name.png"); $g.Dispose(); $bmp.Dispose()
}
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]; $ct = [System.Windows.Automation.ControlType]
function Find($r, $name, $type) { $c = New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)), (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type))); $r.FindFirst($TS::Descendants, $c) }

$work = Join-Path $env:TEMP ("sn-tools-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$data = Join-Path $work "data"
New-Item -ItemType Directory $data | Out-Null
$app = $null
try {
    @{ Theme = $Theme; Engine = @{ DownloadRoot = (Join-Path $work "dl"); UseCategoryFolders = $false }; General = @{ ShowTrayIcon = $false } } | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $data "settings.json")
    $env:SHADENET_DATA_DIR = $data
    $app = Start-Process (Join-Path $root "src\ShadeNetDownloader\bin\Debug\net10.0-windows\ShadeNetDownloader.exe") -PassThru
    Start-Sleep -Seconds 4
    $h = [STools]::ForPid($app.Id); [STools]::MoveWindow($h, 60, 20, 1180, 900, $true) | Out-Null; Start-Sleep -Milliseconds 600
    $win = $AE::FromHandle($h); [STools]::SetForegroundWindow($h) | Out-Null

    ((Find $win "Settings" $ct::Button).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); Start-Sleep -Milliseconds 800
    ((Find $win "Downloads" $ct::RadioButton).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select(); Start-Sleep -Milliseconds 800

    # scroll the page to the helper programs
    foreach ($e in $win.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        $p = $null
        if ($e.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$p) -and $p.Current.VerticallyScrollable -and $e.Current.BoundingRectangle.Width -gt 600) { $p.SetScrollPercent(-1, 100); break }
    }
    Start-Sleep -Milliseconds 800
    Shot $h "tools-0-settings"

    $checks = @($win.FindAll($TS::Descendants, (New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, "Check")), (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ct::Button))))))
    Write-Host "Check buttons: $($checks.Count)"
    foreach ($c in $checks) { ($c.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); Start-Sleep -Seconds 1 }
    Start-Sleep -Seconds 6
    Shot $h "tools-1-checked"

    $texts = $win.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ct::Text))) | ForEach-Object { $_.Current.Name } |
        Where-Object { $_ -match "yt-dlp|ffmpeg|Up to date|Update available|Default, with|Found on|your|newer|PATH|Not installed" }
    $texts | ForEach-Object { Write-Host "  $_" }
}
finally {
    $crash = Join-Path $data "crash.log"
    if (Test-Path $crash) { Write-Host "=== crash.log"; Get-Content $crash | Select-Object -First 25 | ForEach-Object { Write-Host $_ } }
    if ($app -and -not $app.HasExited) { $app.Kill() }
    Start-Sleep -Seconds 1
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
