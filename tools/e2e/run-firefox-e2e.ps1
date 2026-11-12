<#
.SYNOPSIS
  End-to-end check of the browser integration with a REAL Firefox.

.DESCRIPTION
  Starts ShadeNet (throw-away data folder), a local test server and a Firefox with a throw-away profile, registers the native
  host, loads the add-on (as a temporary add-on: no signing needed for that) and makes Firefox download a file that needs a
  session cookie (the server sets it on a redirect, like a login). Pass: ShadeNet has the file byte for byte, Firefox's own
  download was taken over (nothing complete in its folder), and the host was started by Firefox.

  Your own Firefox profile is never touched. The native-messaging registry entries are put back as they were at the end.
#>
param(
    [string]$Firefox = "C:\Program Files\Mozilla Firefox\firefox.exe",
    [int]$SizeBytes = 6000000,
    [string]$Config = "Release"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "host-registrations.ps1")
$savedHosts = Save-HostRegistrations

$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$dotnet = Join-Path $env:USERPROFILE ".dotnet"
$env:DOTNET_ROOT = $dotnet
$appDir = Join-Path $root "src\ShadeNetDownloader\bin\$Config\net10.0-windows"
$appExe = Join-Path $appDir "ShadeNetDownloader.exe"
$cli = Join-Path $root "tools\ShadeNet.Cli\bin\$Config\net10.0\ShadeNet.Cli.dll"
$extension = Join-Path $appDir "browser\firefox"      # what the app ships (the shared code with Firefox's manifest)
$work = Join-Path $env:TEMP ("sn-ff-e2e-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$data = Join-Path $work "data"; $downloads = Join-Path $work "downloads"; $profile = Join-Path $work "firefox-profile"; $ffDownloads = Join-Path $work "firefox-downloads"
New-Item -ItemType Directory $data, $downloads, $profile, $ffDownloads | Out-Null
$env:SHADENET_DATA_DIR = $data      # the app, the host and Firefox (which starts the host) share this folder and its pipe

$server = $null; $app = $null; $result = 1; $scriptStart = Get-Date
try {
    if (-not (Test-Path (Join-Path $extension "manifest.json"))) { throw "build the app first ($Config): $extension is missing" }
    $settings = @{ Theme = "Dark"; Engine = @{ DownloadRoot = $downloads; UseCategoryFolders = $false }; Browser = @{ Mode = "StartImmediately" }; General = @{ ShowTrayIcon = $false } } | ConvertTo-Json -Depth 4
    Set-Content (Join-Path $data "settings.json") $settings

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

    $install = Start-Process $appExe -ArgumentList "--install-browser-integration" -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "registering the browser integration failed (exit $($install.ExitCode))" }
    $ffKey = "HKCU:\Software\Mozilla\NativeMessagingHosts\com.shadenet.downloader"
    $ffManifest = (Get-ItemProperty $ffKey).'(default)'
    Write-Host "Firefox host manifest: $ffManifest"
    Get-Content $ffManifest | ForEach-Object { Write-Host "  $_" }

    $app = Start-Process $appExe -PassThru
    Start-Sleep -Seconds 4

    Write-Host "starting Firefox ..."
    $nodeOut = & node (Join-Path $PSScriptRoot "firefox-e2e.mjs") --firefox $Firefox --extension $extension --profile $profile --downloads $ffDownloads --url $url --wait-ms 25000 2>&1
    $nodeOut | ForEach-Object { Write-Host "  firefox-e2e: $_" }

    $target = Join-Path $downloads "archive.zip"
    for ($i = 0; $i -lt 90; $i++) { if ((Test-Path $target) -and ((Get-Item $target).Length -eq $SizeBytes)) { break }; Start-Sleep -Milliseconds 500 }

    $ok = (Test-Path $target) -and ((Get-Item $target).Length -eq $SizeBytes)
    $json = ($nodeOut | Where-Object { $_ -like "{*" } | Select-Object -Last 1 | ConvertFrom-Json)
    # Firefox's own copy: nothing complete may be left in its folder (a cancelled download leaves at most an empty or .part file)
    $firefoxFiles = @(Get-ChildItem $ffDownloads -File -ErrorAction SilentlyContinue | Where-Object { $_.Length -eq $SizeBytes })
    $hostLog = Join-Path $data "host.log"
    $hostRan = (Test-Path $hostLog) -and ((Get-Content $hostLog -Raw) -match "browser-integration@shadenet-downloader|connected|hello")

    Write-Host ""
    Write-Host "add-on loaded                          : $($json.extensionId)"
    Write-Host "file saved by ShadeNet, exact size     : $ok"
    Write-Host "Firefox kept no copy of its own        : $($firefoxFiles.Count -eq 0)"
    Write-Host "the native host was started by Firefox : $hostRan"
    if (Test-Path $hostLog) { Write-Host "--- host.log"; Get-Content $hostLog | Select-Object -Last 12 | ForEach-Object { Write-Host "  $_" } }

    if ($json.ok -and $ok -and $firefoxFiles.Count -eq 0 -and $hostRan) { $result = 0; Write-Host "`nPASS" -ForegroundColor Green } else { Write-Host "`nFAIL" -ForegroundColor Red }
}
finally {
    try { & $appExe --uninstall-browser-integration; Start-Sleep -Milliseconds 800 } catch { }
    Restore-HostRegistrations $savedHosts
    if ($app -and -not $app.HasExited) { $app.Kill() }
    Get-Process ShadeNetDownloader -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt $scriptStart } | ForEach-Object { $_.Kill() }
    if ($server -and -not $server.HasExited) { $server.Kill() }
    Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine -like "*$profile*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
exit $result
