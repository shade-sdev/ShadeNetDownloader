param([string]$Out = (Join-Path $env:TEMP "shadenet-grabber-e2e"), [string]$Config = "Release")
# End to end: the link grabber and archive extraction in the real app. A page on this machine links to a zip, a PDF and another page.
# The page's address goes into the Add dialog, which offers to grab its links; the grabber lists the files (not the other page), two
# are chosen and downloaded; with "Extract archives" on, the zip is unpacked next to it and its card says so. A throw-away data
# folder; nothing reaches the network.
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$exe = Join-Path $root "src\ShadeNetDownloader\bin\$Config\net10.0-windows\ShadeNetDownloader.exe"
New-Item -ItemType Directory $Out -Force | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.IO.Compression, System.IO.Compression.FileSystem
Add-Type @'
using System; using System.Runtime.InteropServices;
public class GE {
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
    [GE]::SetForegroundWindow($h) | Out-Null; Start-Sleep -Milliseconds 400
    $r = New-Object GE+RECT; [GE]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $Out "$name.png")); $g.Dispose(); $bmp.Dispose()
}
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
function Find($r, $name, $ct) { $c = New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)), (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ct))); $r.FindFirst($TS::Descendants, $c) }
$btn = [System.Windows.Automation.ControlType]::Button; $edit = [System.Windows.Automation.ControlType]::Edit; $check = [System.Windows.Automation.ControlType]::CheckBox
function Click($win, $name) { $e = Find $win $name $btn; if (-not $e) { throw "no button $name" }; ($e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
function WaitFor([scriptblock]$what, [int]$seconds = 20) { for ($i = 0; $i -lt $seconds * 4; $i++) { $r = & $what; if ($r) { return $r }; Start-Sleep -Milliseconds 250 }; return $null }

$work = Join-Path $env:TEMP ("sn-grab-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$data = Join-Path $work "data"; $dl = Join-Path $work "dl"; $site = Join-Path $work "site"
New-Item -ItemType Directory $data, $dl, $site | Out-Null
$server = $null; $app = $null
try {
    # the site: a page linking to a zip (with a photo inside), a PDF and another page
    $photoDir = Join-Path $work "photo-src"; New-Item -ItemType Directory $photoDir | Out-Null
    Set-Content (Join-Path $photoDir "beach.txt") "a day at the beach"
    [System.IO.Compression.ZipFile]::CreateFromDirectory($photoDir, (Join-Path $site "photos.zip"))
    $pdf = New-Object byte[] 300000; (New-Object Random 7).NextBytes($pdf); [IO.File]::WriteAllBytes((Join-Path $site "notes.pdf"), $pdf)
    Set-Content (Join-Path $site "about.html") "<html><title>About</title></html>"
    Set-Content (Join-Path $site "index.html") @'
<html><head><title>Holiday photos</title></head><body>
<h1>Our holiday</h1>
<p><a href="photos.zip">All the photos</a> · <a href="notes.pdf">Travel notes</a> · <a href="about.html">About us</a></p>
</body></html>
'@

    $serverOut = Join-Path $work "server.txt"
    $server = Start-Process node -ArgumentList (Join-Path $PSScriptRoot "static-server.mjs"), $site -RedirectStandardOutput $serverOut -PassThru -WindowStyle Hidden
    $port = WaitFor { if (Test-Path $serverOut) { $m = Select-String -Path $serverOut -Pattern "READY (\d+)" | Select-Object -First 1; if ($m) { $m.Matches[0].Groups[1].Value } } } 15
    if (-not $port) { throw "the page server did not start" }
    $page = "http://127.0.0.1:$port/index.html"

    @{ Theme = "Dark"; Engine = @{ DownloadRoot = $dl; UseCategoryFolders = $false; ExtractArchives = $true };
       General = @{ ShowTrayIcon = $false; NotifyOnComplete = $false; NotifyOnFailure = $false }; Updates = @{ CheckForUpdates = $false }; Tools = @{ CheckForUpdates = $false } } |
        ConvertTo-Json -Depth 3 | Set-Content (Join-Path $data "settings.json")
    $env:SHADENET_DATA_DIR = $data
    $app = Start-Process $exe -PassThru
    Start-Sleep -Seconds 5
    $h = [GE]::ForPid($app.Id)
    [GE]::MoveWindow($h, 40, 30, 1180, 820, $true) | Out-Null; Start-Sleep -Milliseconds 700
    $win = $AE::FromHandle($h); [GE]::SetForegroundWindow($h) | Out-Null

    # the page's address in the Add dialog: it is a page, and its links can be grabbed
    Click $win "Add download"; Start-Sleep -Milliseconds 900
    ((Find $win "Web address" $edit).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($page)
    $grab = WaitFor { Find $win "Grab the links on it…" $btn }
    Check ($null -ne $grab) "the Add dialog sees a web page and offers to grab its links"
    Shot $h "grabber-add-dialog"
    ($grab.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()

    # the grabber: the files, not the other page
    $zip = WaitFor { Find $win "photos.zip" $check }
    Check ($null -ne $zip) "the grabber lists the zip"
    Check ($null -ne (Find $win "notes.pdf" $check)) "the grabber lists the PDF"
    Check ($null -eq (Find $win "about.html" $check)) "the other page is left out (files only)"
    ($zip.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
    ((Find $win "notes.pdf" $check).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
    Start-Sleep -Milliseconds 500
    Shot $h "grabber-dialog"
    Click $win "Download 2 files"

    # both arrive; the zip is unpacked next to it
    $pdfOk = WaitFor { $p = Join-Path $dl "notes.pdf"; (Test-Path $p) -and ((Get-Item $p).Length -eq 300000) } 30
    Check ([bool]$pdfOk) "the PDF arrived"
    $extracted = WaitFor { $p = Join-Path $dl "photos\beach.txt"; (Test-Path $p) -and ((Get-Content $p -Raw).Trim() -eq "a day at the beach") } 30
    Check ([bool]$extracted) "the zip was extracted into a folder named after it"
    $label = WaitFor { $win.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like "*Extracted to photos*" } | Select-Object -First 1 } 15
    Check ($null -ne $label) "the zip's card says where it was extracted"
    Start-Sleep -Milliseconds 800
    Shot $h "grabber-done"
}
finally {
    if ($app -and -not $app.HasExited) { $app.Kill() }
    if ($server -and -not $server.HasExited) { $server.Kill() }
    Start-Sleep -Milliseconds 800
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
$failed = @($results | Where-Object { $_ -like "FAIL*" }).Count
Write-Host "`ngrabber e2e: $($results.Count - $failed) passed, $failed failed; screenshots in $Out"
exit $(if ($failed -eq 0) { 0 } else { 1 })
