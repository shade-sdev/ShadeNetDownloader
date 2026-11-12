param([string]$Out = (Join-Path $env:TEMP "shadenet-torrent-e2e"), [string]$Config = "Release")
# End to end: a torrent in the real app, against a swarm on this machine (ShadeNet.Cli swarm: a tracker and a seeder on 127.0.0.1).
# Opens the .torrent file in the Add dialog, leaves one file out, downloads, checks every chosen file byte for byte, checks the file left
# out was not downloaded, and that the finished torrent is being shared. The app listens on 127.0.0.1 only (SHADENET_TORRENT_ADDRESS),
# with DHT, port forwarding and local peer discovery off: nothing reaches the network, and Windows asks nothing.
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$exe = Join-Path $root "src\ShadeNetDownloader\bin\$Config\net10.0-windows\ShadeNetDownloader.exe"
$cli = Join-Path $root "tools\ShadeNet.Cli\bin\$Config\net10.0\ShadeNet.Cli.dll"
$dotnet = if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT "dotnet.exe" } else { "dotnet" }
New-Item -ItemType Directory $Out -Force | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public class TE {
 public delegate bool EnumProc(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hgt, bool repaint);
 [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
 public static IntPtr ForPid(uint pid){ IntPtr f=IntPtr.Zero; EnumWindows((h,_)=>{ uint x; GetWindowThreadProcessId(h,out x); if(x==pid && IsWindowVisible(h) && GetWindowTextLength(h)>0){ f=h; return false;} return true;}, IntPtr.Zero); return f; }
}
'@
$results = New-Object System.Collections.Generic.List[string]
function Check([bool]$ok, [string]$what) { $results.Add(($(if ($ok) { "PASS" } else { "FAIL" })) + " " + $what); Write-Host ($(if ($ok) { "  PASS " } else { "  FAIL " }) + $what) }
function Shot([IntPtr]$h, [string]$name) {
    [TE]::SetForegroundWindow($h) | Out-Null; Start-Sleep -Milliseconds 400
    $r = New-Object TE+RECT; [TE]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $Out "$name.png")); $g.Dispose(); $bmp.Dispose()
}
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
function Find($r, $name, $ct) { $c = New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)), (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ct))); $r.FindFirst($TS::Descendants, $c) }
$btn = [System.Windows.Automation.ControlType]::Button; $edit = [System.Windows.Automation.ControlType]::Edit; $check = [System.Windows.Automation.ControlType]::CheckBox
function Click($win, $name) { $e = Find $win $name $btn; if (-not $e) { throw "no button $name" }; ($e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
function Hash($p) { (Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash }

$work = Join-Path $env:TEMP ("sn-torrent-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$data = Join-Path $work "data"; $dl = Join-Path $work "dl"
New-Item -ItemType Directory $data, $dl | Out-Null
$swarmOut = Join-Path $work "swarm.txt"
$swarm = $null; $app = $null
try {
    $swarm = Start-Process $dotnet -ArgumentList $cli, "swarm", "-o", (Join-Path $work "swarm"), "--kbps", "2048" -RedirectStandardOutput $swarmOut -PassThru -WindowStyle Hidden
    $lines = @()
    for ($i = 0; $i -lt 60 -and -not ($lines | Where-Object { $_ -like "MAGNET *" }); $i++) { Start-Sleep -Milliseconds 500; if (Test-Path $swarmOut) { $lines = @(Get-Content $swarmOut) } }
    $torrentFile = ($lines | Where-Object { $_ -like "TORRENT *" }).Substring(8)
    $source = ($lines | Where-Object { $_ -like "FOLDER *" }).Substring(7)
    if (-not $torrentFile) { throw "the swarm did not start" }
    Write-Host "swarm: $torrentFile"

    @{ Theme = "Dark"; Engine = @{ DownloadRoot = $dl; UseCategoryFolders = $false; TorrentUseDht = $false; TorrentPortForwarding = $false; TorrentLocalPeers = $false; TorrentSeedRatio = 1.0 };
       General = @{ ShowTrayIcon = $false; NotifyOnComplete = $false; NotifyOnFailure = $false }; Updates = @{ CheckForUpdates = $false }; Tools = @{ CheckForUpdates = $false } } |
        ConvertTo-Json -Depth 3 | Set-Content (Join-Path $data "settings.json")
    $env:SHADENET_DATA_DIR = $data
    $env:SHADENET_TORRENT_ADDRESS = "127.0.0.1"
    $app = Start-Process $exe -PassThru
    Start-Sleep -Seconds 5
    $h = [TE]::ForPid($app.Id)
    [TE]::MoveWindow($h, 40, 30, 1180, 820, $true) | Out-Null; Start-Sleep -Milliseconds 700
    $win = $AE::FromHandle($h); [TE]::SetForegroundWindow($h) | Out-Null

    # the Add dialog with the .torrent file
    Click $win "Add download"; Start-Sleep -Milliseconds 900
    ((Find $win "Web address" $edit).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($torrentFile)
    $extras = $null
    for ($i = 0; $i -lt 40 -and -not $extras; $i++) { Start-Sleep -Milliseconds 250; $extras = Find $win "Extras\Making of.mp4" $check }
    Check ($null -ne $extras) "the dialog lists the torrent's files"
    Check ($null -ne (Find $win "Open Movie (2024) 1080p.mkv" $check)) "the film is in the list"
    ($extras.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()   # leave the extras out
    Start-Sleep -Milliseconds 500
    Shot $h "torrent-add-dialog"
    Click $win "Download now"

    # downloading, then finished
    $film = Join-Path $dl "Open Movie Pack\Open Movie (2024) 1080p.mkv"
    $want = (Get-Item -LiteralPath (Join-Path $source "Open Movie (2024) 1080p.mkv")).Length
    Start-Sleep -Seconds 4
    Shot $h "torrent-downloading"

    # the details drawer while it downloads: the piece map, the seeder among the peers, the files, the tracker
    $radio = [System.Windows.Automation.ControlType]::RadioButton
    function Pick($name) { $e = Find $win $name $radio; if (-not $e) { throw "no tab $name" }; ($e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select(); Start-Sleep -Milliseconds 1300 }
    function HasText($pattern) { @($win.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))) | Where-Object { $_.Current.Name -like $pattern }).Count -gt 0 }
    $detailsButtons = $win.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, "Details")))
    ($detailsButtons[$detailsButtons.Count - 1].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke(); Start-Sleep -Milliseconds 1500
    Check (HasText "Pieces") "the drawer shows the piece map"
    Shot $h "torrent-details-overview"
    Pick "Peers"
    Check (HasText "127.0.0.1:*") "the Peers tab lists the seeder"
    Shot $h "torrent-details-peers"
    Pick "Files"
    Check (HasText "5 of 6 files chosen*") "the Files tab shows the files chosen in the dialog"
    Shot $h "torrent-details-files"
    Pick "Trackers"
    Check ((HasText "Working") -or (HasText "Contacting*")) "the Trackers tab shows the tracker"
    Shot $h "torrent-details-trackers"
    Pick "Overview"
    $sw = [Diagnostics.Stopwatch]::StartNew(); $done = $false
    while ($sw.Elapsed.TotalSeconds -lt 120 -and -not $done) {
        Start-Sleep -Seconds 1
        try { $done = (Test-Path -LiteralPath $film) -and ((Hash $film) -eq (Hash (Join-Path $source "Open Movie (2024) 1080p.mkv"))) } catch { $done = $false }
    }
    Check $done ("the film arrived byte for byte (" + [math]::Round($sw.Elapsed.TotalSeconds, 1) + " s)")
    Start-Sleep -Seconds 3
    foreach ($f in "Subtitles\English.srt", "Subtitles\Deutsch.srt", "poster.jpg", "README.txt") {
        $p = Join-Path $dl "Open Movie Pack\$f"
        Check ((Test-Path -LiteralPath $p) -and ((Hash $p) -eq (Hash (Join-Path $source $f)))) "$f arrived byte for byte"
    }
    $left = Join-Path $dl "Open Movie Pack\Extras\Making of.mp4"
    $same = (Test-Path -LiteralPath $left) -and ((Hash $left) -eq (Hash (Join-Path $source "Extras\Making of.mp4")))
    Check (-not $same) "the file left out was not downloaded"
    $sharing = $null
    for ($i = 0; $i -lt 20 -and -not $sharing; $i++) { Start-Sleep -Milliseconds 300; $sharing = $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, "Sharing"))) }
    Check ($null -ne $sharing) "the finished torrent's card says Sharing"
    Shot $h "torrent-sharing"

    # the same torrent again: the dialog says it is in the list
    Click $win "Add download"; Start-Sleep -Milliseconds 900
    ((Find $win "Web address" $edit).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($torrentFile)
    Start-Sleep -Seconds 2
    $texts = $win.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
    $already = @($texts | Where-Object { $_.Current.Name -like "This torrent is already in your list as*Open Movie Pack*" })
    Check ($already.Count -gt 0) "adding it twice is refused with its name"
    Click $win "Cancel"; Start-Sleep -Milliseconds 600

    # Windows opens a magnet link with ShadeNet while it runs: the second launch hands the link to this one, whose Add dialog shows it
    $magnet = ($lines | Where-Object { $_ -like "MAGNET *" }).Substring(7)
    $second = Start-Process $exe -ArgumentList "`"$magnet`"" -PassThru
    $second.WaitForExit(15000) | Out-Null
    Check ($second.HasExited) "the second launch handed over and ended"
    $address = $null
    for ($i = 0; $i -lt 30 -and -not $address; $i++) { Start-Sleep -Milliseconds 300; $address = Find $win "Web address" $edit }
    $value = if ($address) { ($address.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value } else { "" }
    Check ($value.Trim() -eq $magnet) "the running ShadeNet opened the Add dialog with the magnet link"
    Start-Sleep -Seconds 2
    Shot $h "torrent-second-launch"
    if ($address) { Click $win "Cancel"; Start-Sleep -Milliseconds 600 }
}
finally {
    $crash = Join-Path $data "crash.log"
    if (Test-Path $crash) { Write-Host "=== crash.log"; Get-Content $crash | Select-Object -First 25 | ForEach-Object { Write-Host $_ } }
    if ($app -and -not $app.HasExited) { $app.Kill() }   # (only this test's own instance: never --quit, which could reach another)
    if ($swarm -and -not $swarm.HasExited) { $swarm.Kill() }
    Remove-Item Env:SHADENET_TORRENT_ADDRESS -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
$failed = @($results | Where-Object { $_ -like "FAIL*" }).Count
Write-Host ""
Write-Host ("torrent e2e: " + ($results.Count - $failed) + " passed, $failed failed; screenshots in $Out")
exit $failed
