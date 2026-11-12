<#
.SYNOPSIS
  End to end: a magnet link clicked in a REAL Chrome becomes a torrent download in ShadeNet.

.DESCRIPTION
  A swarm on this machine (ShadeNet.Cli swarm: a tracker and a seeder on 127.0.0.1) shares a torrent; a local page links its magnet
  link. ShadeNet runs against a throw-away data folder, set to start what the browser hands over at once, with torrents on 127.0.0.1
  only (SHADENET_TORRENT_ADDRESS; DHT, port forwarding and local peers off: nothing reaches the network, Windows asks nothing). A Chrome
  with a throw-away profile loads the extension and clicks the link. Pass: the extension hands the link over, and ShadeNet downloads the
  torrent, every file byte for byte.

  Your own browser profile is never touched. The native-messaging registry entry under HKCU exists only for the run.
#>
param(
    [string]$Chrome = "C:\Program Files\Google\Chrome\Application\chrome.exe",
    [string]$Config = "Release"
)

$ErrorActionPreference = "Stop"
# the user's own browser integration is put back as it was at the end (see host-registrations.ps1)
. (Join-Path $PSScriptRoot "host-registrations.ps1")
$savedHosts = Save-HostRegistrations
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$dotnet = if ($env:DOTNET_ROOT) { $env:DOTNET_ROOT } else { "C:\Users\Shade\.dotnet" }
$env:DOTNET_ROOT = $dotnet
$appExe = Join-Path $root "src\ShadeNetDownloader\bin\$Config\net10.0-windows\ShadeNetDownloader.exe"
$cli = Join-Path $root "tools\ShadeNet.Cli\bin\$Config\net10.0\ShadeNet.Cli.dll"
$extension = Join-Path $root "extension\chromium"
$work = Join-Path $env:TEMP ("sn-magnet-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$data = Join-Path $work "data"; $downloads = Join-Path $work "downloads"; $profile = Join-Path $work "chrome-profile"; $site = Join-Path $work "site"
New-Item -ItemType Directory $data, $downloads, $profile, $site | Out-Null
$env:SHADENET_DATA_DIR = $data      # the app, the host and Chrome (which starts the host) all share this folder and pipe
$env:SHADENET_TORRENT_ADDRESS = "127.0.0.1"

$results = New-Object System.Collections.Generic.List[string]
function Check([bool]$ok, [string]$what) { $results.Add(($(if ($ok) { "PASS" } else { "FAIL" })) + " " + $what); Write-Host ($(if ($ok) { "  PASS " } else { "  FAIL " }) + $what) }
function Hash($p) { (Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash }

$swarm = $null; $server = $null; $app = $null
try {
    $swarmOut = Join-Path $work "swarm.txt"
    $swarm = Start-Process "$dotnet\dotnet.exe" -ArgumentList $cli, "swarm", "-o", (Join-Path $work "swarm"), "--kbps", "8192" -RedirectStandardOutput $swarmOut -PassThru -WindowStyle Hidden
    $lines = @()
    for ($i = 0; $i -lt 60 -and -not ($lines | Where-Object { $_ -like "MAGNET *" }); $i++) { Start-Sleep -Milliseconds 500; if (Test-Path $swarmOut) { $lines = @(Get-Content $swarmOut) } }
    $magnet = ($lines | Where-Object { $_ -like "MAGNET *" }).Substring(7)
    $source = ($lines | Where-Object { $_ -like "FOLDER *" }).Substring(7)
    if (-not $magnet) { throw "the swarm did not start" }

    # a page that links the magnet link
    Set-Content (Join-Path $site "index.html") "<!doctype html><html><body><h1>Open Movie Pack</h1><p><a href=`"$magnet`">Download with a magnet link</a></p></body></html>" -Encoding utf8
    $serveOut = Join-Path $work "server.txt"
    $server = Start-Process node -ArgumentList (Join-Path $PSScriptRoot "static-server.mjs"), $site -RedirectStandardOutput $serveOut -PassThru -WindowStyle Hidden
    $port = $null
    for ($i = 0; $i -lt 40 -and -not $port; $i++) { Start-Sleep -Milliseconds 300; if (Test-Path $serveOut) { $m = Select-String -Path $serveOut -Pattern "READY (\d+)" | Select-Object -First 1; if ($m) { $port = $m.Matches[0].Groups[1].Value } } }
    if (-not $port) { throw "the page server did not start" }

    # ShadeNet: start what the browser hands over at once; magnet links from the browser on; torrents on this machine only
    @{ Theme = "Dark"; Engine = @{ DownloadRoot = $downloads; UseCategoryFolders = $false; TorrentUseDht = $false; TorrentPortForwarding = $false; TorrentLocalPeers = $false };
       Browser = @{ Mode = "StartImmediately"; MagnetLinks = $true }; General = @{ ShowTrayIcon = $false; NotifyOnComplete = $false }; Updates = @{ CheckForUpdates = $false }; Tools = @{ CheckForUpdates = $false } } |
        ConvertTo-Json -Depth 4 | Set-Content (Join-Path $data "settings.json")
    $install = Start-Process $appExe -ArgumentList "--install-browser-integration" -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "registering the browser integration failed (exit $($install.ExitCode))" }
    $app = Start-Process $appExe -PassThru
    Start-Sleep -Seconds 4

    $chromeDownloads = Join-Path $work "chrome-downloads"; New-Item -ItemType Directory $chromeDownloads | Out-Null
    New-Item -ItemType Directory (Join-Path $profile "Default") | Out-Null
    @{ download = @{ default_directory = $chromeDownloads; prompt_for_download = $false } } | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $profile "Default\Preferences")

    Write-Host "starting Chrome ..."
    $nodeOut = & node (Join-Path $PSScriptRoot "chrome-magnet-e2e.mjs") --chrome $Chrome --extension $extension --profile $profile --page "http://127.0.0.1:$port/index.html" 2>&1
    $nodeOut | ForEach-Object { Write-Host "  chrome: $_" }
    $report = ($nodeOut -join "`n") | ConvertFrom-Json -ErrorAction SilentlyContinue
    Check ($report.linkOnPage -eq $magnet) "the page shows the magnet link"
    Check ([bool]$report.sent) "the extension handed the clicked magnet link to ShadeNet"
    Check (@($report.failed).Count -eq 0) "the hand-over did not fail"

    # ShadeNet downloads it from the swarm
    $film = Join-Path $downloads "Open Movie Pack\Open Movie (2024) 1080p.mkv"
    $done = $false
    for ($i = 0; $i -lt 120 -and -not $done; $i++) {
        Start-Sleep -Seconds 1
        try { $done = (Test-Path -LiteralPath $film) -and ((Hash $film) -eq (Hash (Join-Path $source "Open Movie (2024) 1080p.mkv"))) } catch { $done = $false }
    }
    Check $done "ShadeNet downloaded the torrent the link names, byte for byte"
    Start-Sleep -Seconds 2
    $all = $true
    foreach ($f in Get-ChildItem -LiteralPath $source -Recurse -File) {
        $rel = $f.FullName.Substring($source.Length + 1)
        $p = Join-Path $downloads "Open Movie Pack\$rel"
        if (-not ((Test-Path -LiteralPath $p) -and ((Hash $p) -eq (Hash $f.FullName)))) { $all = $false; Write-Host "    differs: $rel" }
    }
    Check $all "every file of the torrent is complete"
}
finally {
    $crash = Join-Path $data "crash.log"
    if (Test-Path $crash) { Write-Host "=== crash.log"; Get-Content $crash | Select-Object -First 25 | ForEach-Object { Write-Host $_ } }
    if ($app -and -not $app.HasExited) { $app.Kill() }   # (only this run's own instance)
    try { & $appExe --uninstall-browser-integration; Start-Sleep -Milliseconds 800 } catch { }
    Restore-HostRegistrations $savedHosts
    foreach ($p in @($swarm, $server)) { if ($p -and -not $p.HasExited) { $p.Kill() } }
    # the native host this run's Chrome started (it ends with Chrome; one left over is this run's: started from this build's folder)
    Get-Process ShadeNet.NativeHost -ErrorAction SilentlyContinue | Where-Object { $_.Path -like (Join-Path $root "src\ShadeNetDownloader\bin\$Config\*") } | ForEach-Object { $_.Kill() }
    Remove-Item Env:SHADENET_TORRENT_ADDRESS -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
$failed = @($results | Where-Object { $_ -like "FAIL*" }).Count
Write-Host ""
Write-Host ("chrome magnet e2e: " + ($results.Count - $failed) + " passed, $failed failed")
exit $failed
