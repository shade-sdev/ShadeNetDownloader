<#
.SYNOPSIS
  The real app with a real YouTube link (default: a Creative Commons film): the Add dialog asks yt-dlp, lists the
  qualities, ShadeNet downloads the picture and the sound itself (parallel ranges) and joins them with ffmpeg.
  Needs internet, ffmpeg on the PATH and tools\yt-dlp (tools\get-tools.ps1). Screenshots: docs\youtube-*.png.
#>
param(
    [string]$Url = "https://www.youtube.com/watch?v=aqz-KE-bpKQ",
    [string]$Quality = "360p"
)

$ErrorActionPreference = "Stop"
$env:DOTNET_ROOT = "C:\Users\Shade\.dotnet"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$docs = Join-Path $root "docs"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public class SYt {
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
    $r = New-Object SYt+RECT; [SYt]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc(); [SYt]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc)
    $bmp.Save("$docs\$name.png"); $g.Dispose(); $bmp.Dispose()
}
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
$ct = [System.Windows.Automation.ControlType]
function Find($r, $name, $type) { $c = New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)), (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type))); $r.FindFirst($TS::Descendants, $c) }
function Click($win, $name) { ((Find $win $name $ct::Button).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }

$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) { throw "this check needs ffmpeg on the PATH" }
$bundled = Join-Path $root "src\ShadeNetDownloader\bin\Debug\net10.0-windows\tools\yt-dlp\yt-dlp.exe"
Write-Host "yt-dlp next to the program: $(Test-Path $bundled)"

$work = Join-Path $env:TEMP ("sn-yt-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$data = Join-Path $work "data"; $dl = Join-Path $work "dl"
New-Item -ItemType Directory $data, $dl | Out-Null
$app = $null; $result = 1
try {
    @{ Theme = "Dark"; Engine = @{ DownloadRoot = $dl; UseCategoryFolders = $false }; General = @{ ShowTrayIcon = $false } } | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $data "settings.json")
    $env:SHADENET_DATA_DIR = $data
    $app = Start-Process (Join-Path $root "src\ShadeNetDownloader\bin\Debug\net10.0-windows\ShadeNetDownloader.exe") -PassThru
    Start-Sleep -Seconds 4
    $h = [SYt]::ForPid($app.Id); $win = $AE::FromHandle($h); [SYt]::SetForegroundWindow($h) | Out-Null

    Click $win "Add download"; Start-Sleep -Milliseconds 900
    ((Find $win "Web address" $ct::Edit).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($Url)

    # yt-dlp takes a few seconds: wait for the quality list
    $combo = $null
    for ($i = 0; $i -lt 90 -and -not $combo; $i++) { Start-Sleep -Milliseconds 500; $combo = Find $win "Quality" $ct::ComboBox }
    Write-Host "quality list appeared: $([bool]$combo)"
    if (-not $combo) { Shot $h "youtube-fail"; throw "no quality list" }
    Shot $h "youtube-0-dialog"

    # choose the small one
    ($combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand(); Start-Sleep -Milliseconds 500
    $item = $AE::RootElement.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ct::ListItem))) |
        Where-Object { $_.Current.Name -like "*$Quality*" -or $_.Current.Name -like "ShadeNetDownloader.*$Quality*" } | Select-Object -First 1
    if ($item) {
        Write-Host "choosing the entry for $Quality"
        try { ($item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select() }
        catch { $item.SetFocus(); Start-Sleep -Milliseconds 300; [System.Windows.Forms.SendKeys]::SendWait("{ENTER}") }
        Start-Sleep -Milliseconds 800
    }
    else { Write-Host "(no '$Quality' entry found in the list: downloading the default quality)" }
    Shot $h "youtube-1-quality"

    Click $win "Download now"; Start-Sleep -Seconds 6
    Shot $h "youtube-2-downloading"

    $target = $null
    for ($i = 0; $i -lt 480 -and -not $target; $i++) {
        $target = Get-ChildItem $dl -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in ".mp4", ".mkv" } | Select-Object -First 1
        if (-not $target) { Start-Sleep -Milliseconds 500 }
    }
    Start-Sleep -Seconds 2
    Shot $h "youtube-3-done"
    if ($target) {
        $clean = (& $ffmpeg -hide_banner -v error -i $target.FullName -f null - 2>&1 | Out-String).Trim()
        $info = (& $ffmpeg -hide_banner -i $target.FullName 2>&1 | Out-String)
        $dur = [regex]::Match($info, "Duration: (\d+:\d+:[\d.]+)").Groups[1].Value
        Write-Host "file saved     : $($target.Name) ($([math]::Round($target.Length / 1MB, 1)) MB, $dur)"
        Write-Host "decodes cleanly: $($clean.Length -eq 0)"
        Write-Host "picture + sound: $(($info -match 'Video:') -and ($info -match 'Audio:'))"
        Write-Host ("streams        : " + ((($info -split "`n") | Where-Object { $_ -match "Stream #" } | ForEach-Object { $_.Trim() -replace '\s+', ' ' }) -join " | "))
        if ($clean.Length -eq 0 -and ($info -match 'Video:') -and ($info -match 'Audio:')) { $result = 0 }
    }
    else { Write-Host "file saved     : none" }
    if ($result -eq 0) { Write-Host "`nPASS" -ForegroundColor Green } else { Write-Host "`nFAIL" -ForegroundColor Red }
}
finally {
    $crash = Join-Path $data "crash.log"
    if (Test-Path $crash) { Write-Host "=== crash.log"; Get-Content $crash | Select-Object -First 25 | ForEach-Object { Write-Host $_ } }
    if ($app -and -not $app.HasExited) { $app.Kill() }
    Start-Sleep -Seconds 1
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
exit $result
