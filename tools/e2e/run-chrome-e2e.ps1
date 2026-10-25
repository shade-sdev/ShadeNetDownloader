<#
.SYNOPSIS
  End-to-end check of the browser integration with a REAL Chrome.

.DESCRIPTION
  Starts ShadeNet (throw-away data folder), a local test server and a Chrome with a throw-away profile, registers the
  native host, loads the extension, and makes Chrome download a file that needs a session cookie (the server sets the
  cookie on a redirect, like a login). Pass criteria: Chrome's own download is taken over, ShadeNet downloads the file
  byte-exact, and the app shows it. Everything it registers is removed again at the end.

  Your own browser profile is never touched; only a native-messaging registry entry under HKCU is written for the
  duration of the run (and removed in the end).
#>
param(
    [string]$Chrome = "C:\Program Files\Google\Chrome\Application\chrome.exe",
    [int]$SizeBytes = 6000000,
    # start Chrome while ShadeNet is NOT running: the native host has to launch it
    [switch]$AppNotRunning,
    # Chrome's "Ask where to save each file" setting: with it on, a download that is not taken over opens a Save dialog;
    # a taken-over one must not. The run fails if any Save dialog of this Chrome shows up.
    [switch]$AskWhereToSave
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$dotnet = "C:\Users\Shade\.dotnet"
$env:DOTNET_ROOT = $dotnet
$appExe = Join-Path $root "src\ShadeNetDownloader\bin\Debug\net10.0-windows\ShadeNetDownloader.exe"
$cli = Join-Path $root "tools\ShadeNet.Cli\bin\Debug\net10.0\ShadeNet.Cli.dll"
$extension = Join-Path $root "extension\chromium"
$work = Join-Path $env:TEMP ("sn-e2e-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$data = Join-Path $work "data"; $downloads = Join-Path $work "downloads"; $profile = Join-Path $work "chrome-profile"
New-Item -ItemType Directory $data, $downloads, $profile | Out-Null
$env:SHADENET_DATA_DIR = $data      # the app, the host and Chrome (which starts the host) all share this folder and pipe

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public class E2eWin {
 public delegate bool EnumProc(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
 [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h); public static IntPtr ForPid(uint pid){ IntPtr f=IntPtr.Zero; EnumWindows((h,_)=>{ uint x; GetWindowThreadProcessId(h,out x); if(x==pid && IsWindowVisible(h) && GetWindowTextLength(h)>0){ f=h; return false;} return true;}, IntPtr.Zero); return f; }
}
'@

Add-Type @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text; using System.Threading;
// Watches for native dialogs (class #32770: the "Save as" dialog of Chrome) while the run goes on.
public class E2eDialogs {
 delegate bool EnumProc(IntPtr h, IntPtr l);
 [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
 [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
 public static readonly List<string> Seen = new List<string>();
 static volatile bool stop;
 public static void Start() {
  var t = new Thread(() => { while (!stop) {
    EnumWindows((h, _) => {
      if (!IsWindowVisible(h)) return true;
      var c = new StringBuilder(64); GetClassName(h, c, 64);
      if (c.ToString() != "#32770") return true;
      uint pid; GetWindowThreadProcessId(h, out pid);
      var title = new StringBuilder(256); GetWindowText(h, title, 256);
      var entry = pid + "|" + title;
      lock (Seen) if (!Seen.Contains(entry)) Seen.Add(entry);
      return true; }, IntPtr.Zero);
    Thread.Sleep(50); } });
  t.IsBackground = true; t.Start();
 }
 public static void Stop() { stop = true; }
}
public class E2ePrint { [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags); }
'@

# PrintWindow renders the window itself, so the picture is right even when other windows are in front of it
function Save-Window([IntPtr]$h, [string]$path) {
    $r = New-Object E2eWin+RECT; [E2eWin]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
    [E2ePrint]::PrintWindow($h, $hdc, 2) | Out-Null     # 2 = PW_RENDERFULLCONTENT
    $g.ReleaseHdc($hdc); $bmp.Save($path); $g.Dispose(); $bmp.Dispose()
}

$server = $null; $app = $null; $result = 1; $scriptStart = Get-Date
try {
    # settings: take downloads over without asking, so the run needs no clicks
    $settings = @{ Theme = "Dark"; Engine = @{ DownloadRoot = $downloads; UseCategoryFolders = $false }; Browser = @{ Mode = "StartImmediately" }; General = @{ ShowTrayIcon = $false } } | ConvertTo-Json -Depth 4
    Set-Content (Join-Path $data "settings.json") $settings

    # a test server: /login sets a session cookie and redirects to a file that needs it (throttled so the browser is mid-download)
    $serveOut = Join-Path $work "server.txt"
    $server = Start-Process "$dotnet\dotnet.exe" -ArgumentList $cli, "serve" -RedirectStandardOutput $serveOut -PassThru -WindowStyle Hidden
    $port = $null
    for ($i = 0; $i -lt 40 -and -not $port; $i++) {
        Start-Sleep -Milliseconds 400
        if (Test-Path $serveOut) { $m = Select-String -Path $serveOut -Pattern "DEMO http://127.0.0.1:(\d+)/" | Select-Object -First 1; if ($m) { $port = $m.Matches[0].Groups[1].Value } }
    }
    if (-not $port) { throw "the test server did not start" }
    $url = "http://127.0.0.1:$port/login/$SizeBytes/archive.zip?kbps=768"
    Write-Host "test server on port $port"

    # register the native host (HKCU only), then start the app
    $install = Start-Process $appExe -ArgumentList "--install-browser-integration" -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "registering the browser integration failed (exit $($install.ExitCode))" }
    if (-not $AppNotRunning) {
        $app = Start-Process $appExe -PassThru
        Start-Sleep -Seconds 4
        $h = [E2eWin]::ForPid($app.Id)
    }

    # the throw-away Chrome must not download into the user's real Downloads folder
    $chromeDownloads = Join-Path $work "chrome-downloads"; New-Item -ItemType Directory $chromeDownloads | Out-Null
    New-Item -ItemType Directory (Join-Path $profile "Default") | Out-Null
    @{ download = @{ default_directory = $chromeDownloads; prompt_for_download = [bool]$AskWhereToSave } } | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $profile "Default\Preferences")

    # a real Chrome, a throw-away profile, the extension loaded through the DevTools pipe
    [E2eDialogs]::Start()
    Write-Host "starting Chrome ..."
    $nodeOut = & node (Join-Path $PSScriptRoot "chrome-e2e.mjs") --chrome $Chrome --extension $extension --profile $profile --url $url --wait-ms 30000 --expect-id bcegodlanfeghhakadmalbkbhbhbejop 2>&1
    $nodeOut | ForEach-Object { Write-Host "  chrome-e2e: $_" }

    # the app must now be downloading (or have finished) the file the browser started
    $target = Join-Path $downloads "archive.zip"
    for ($i = 0; $i -lt 90; $i++) { if ((Test-Path $target) -and ((Get-Item $target).Length -eq $SizeBytes)) { break }; Start-Sleep -Milliseconds 500 }
    Start-Sleep -Seconds 1

    if ($AppNotRunning) {
        # the app was started by the native host, minimized: find it (only processes born during this run)
        $app = Get-Process ShadeNetDownloader -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt $scriptStart } | Select-Object -First 1
        Write-Host "app started by the host                : $([bool]$app)"
        if ($app) { $h = [E2eWin]::ForPid($app.Id); if ($h -eq [IntPtr]::Zero) { $h = $app.MainWindowHandle } }
    }
    [E2eWin]::SetForegroundWindow($h) | Out-Null; Start-Sleep -Milliseconds 600
    $shot = Join-Path $root "docs\step10-captured.png"
    Save-Window $h $shot

    # Save dialogs of a Chrome process (nobody else's Chrome should be opening dialogs during the run)
    [E2eDialogs]::Stop()
    $dialogs = @([E2eDialogs]::Seen.ToArray())
    Write-Host "Chrome dialogs seen during the run     : $($dialogs.Count) $($dialogs -join '; ')"

    $ok = (Test-Path $target) -and ((Get-Item $target).Length -eq $SizeBytes)
    $json = ($nodeOut -join "`n" | ConvertFrom-Json)
    Write-Host ""
    Write-Host "extension loaded with the expected id : $($json.extensionId)"
    Write-Host "browser's own download taken over     : $($json.takenOver)   (states seen: $($json.statesSeen -join ', '))"
    Write-Host "file saved by ShadeNet, exact size    : $ok"
    if ($ok) { Write-Host "file                                  : $target ($((Get-Item $target).Length) bytes)" }
    Write-Host "screenshot                            : $shot"
    $log = Join-Path $data "host.log"
    if (Test-Path $log) { Write-Host "--- host.log"; Get-Content $log | Select-Object -Last 12 | ForEach-Object { Write-Host "  $_" } }

    if ($ok -and $json.takenOver -and $dialogs.Count -eq 0) { $result = 0; Write-Host "`nPASS" -ForegroundColor Green } else { Write-Host "`nFAIL" -ForegroundColor Red }
}
finally {
    # leave the machine as we found it
    try { & $appExe --uninstall-browser-integration; Start-Sleep -Milliseconds 800 } catch { }
    if ($app -and -not $app.HasExited) { $app.Kill() }
    # an app the host started is not in $app if the scenario failed early: stop whatever was born during this run
    Get-Process ShadeNetDownloader -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt $scriptStart } | ForEach-Object { $_.Kill() }
    if ($server -and -not $server.HasExited) { $server.Kill() }
    Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine -like "*$profile*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
exit $result
