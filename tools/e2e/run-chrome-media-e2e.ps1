<#
.SYNOPSIS
  End-to-end check of the extension's "Videos on this page" with a REAL Chrome and a REAL HLS stream made by ffmpeg.

.DESCRIPTION
  A local page requests the stream's playlists the way a player does (fetch, then XHR). Chrome (throw-away profile, the
  extension loaded through the DevTools pipe) must: notice the playlist, list it for the popup (title, quality, length),
  badge the tab, and, when "Download" is pressed, hand it to ShadeNet, which downloads the pieces and joins them into an
  MP4 that decodes cleanly. Your own browser profile is never touched; the native-messaging registry entry is removed again.
#>
param([string]$Chrome = "C:\Program Files\Google\Chrome\Application\chrome.exe")

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$dotnet = "C:\Users\Shade\.dotnet"
$env:DOTNET_ROOT = $dotnet
$appExe = Join-Path $root "src\ShadeNetDownloader\bin\Debug\net10.0-windows\ShadeNetDownloader.exe"
$extension = Join-Path $root "extension\chromium"
$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) { throw "this check needs ffmpeg on the PATH" }

$work = Join-Path $env:TEMP ("sn-e2em-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$data = Join-Path $work "data"; $downloads = Join-Path $work "downloads"; $profile = Join-Path $work "chrome-profile"; $site = Join-Path $work "site"
New-Item -ItemType Directory $data, $downloads, $profile, (Join-Path $site "holiday") | Out-Null
$env:SHADENET_DATA_DIR = $data

$server = $null; $app = $null; $result = 1; $scriptStart = Get-Date
try {
    # a real VOD stream, 12 s, 2 s pieces; the page asks for the master and then one quality, like a player
    Push-Location (Join-Path $site "holiday")
    & $ffmpeg -y -loglevel error -f lavfi -i "testsrc=duration=12:size=320x240:rate=25" -f lavfi -i "sine=frequency=330:duration=12" `
        -c:v libx264 -preset ultrafast -pix_fmt yuv420p -g 50 -c:a aac -f hls -hls_time 2 -hls_playlist_type vod index.m3u8
    Pop-Location
    Set-Content (Join-Path $site "holiday\master.m3u8") "#EXTM3U`n#EXT-X-STREAM-INF:BANDWIDTH=400000,RESOLUTION=320x240`nindex.m3u8`n"
    Set-Content (Join-Path $site "player.html") @'
<!doctype html><html><head><meta charset="utf-8"><title>My Holiday Video</title></head><body><p>A page with a player.</p>
<script>
  // what a player does: ask for the master playlist, then for the chosen quality
  (async () => {
    const master = await (await fetch('holiday/master.m3u8')).text();
    const xhr = new XMLHttpRequest();
    xhr.open('GET', 'holiday/index.m3u8');
    xhr.send();
  })();
</script></body></html>
'@

    $serveOut = Join-Path $work "server.txt"
    $server = Start-Process node -ArgumentList (Join-Path $PSScriptRoot "static-server.mjs"), $site -RedirectStandardOutput $serveOut -PassThru -WindowStyle Hidden
    $port = $null
    for ($i = 0; $i -lt 40 -and -not $port; $i++) { Start-Sleep -Milliseconds 300; if (Test-Path $serveOut) { $m = Select-String -Path $serveOut -Pattern "READY (\d+)" | Select-Object -First 1; if ($m) { $port = $m.Matches[0].Groups[1].Value } } }
    if (-not $port) { throw "static server did not start" }

    @{ Theme = "Dark"; Engine = @{ DownloadRoot = $downloads; UseCategoryFolders = $false }; Browser = @{ Mode = "StartImmediately" }; General = @{ ShowTrayIcon = $false } } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $data "settings.json")

    $install = Start-Process $appExe -ArgumentList "--install-browser-integration" -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "registering the browser integration failed (exit $($install.ExitCode))" }
    $app = Start-Process $appExe -PassThru
    Start-Sleep -Seconds 4

    New-Item -ItemType Directory (Join-Path $profile "Default") | Out-Null
    @{ download = @{ default_directory = (Join-Path $work "chrome-downloads"); prompt_for_download = $false } } | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $profile "Default\Preferences")

    Write-Host "starting Chrome ..."
    $nodeOut = & node (Join-Path $PSScriptRoot "chrome-media-e2e.mjs") --chrome $Chrome --extension $extension --profile $profile --page "http://127.0.0.1:$port/player.html" --wait-ms 20000 --expect-id bcegodlanfeghhakadmalbkbhbhbejop 2>&1
    $json = ($nodeOut -join "`n" | ConvertFrom-Json)

    # the app must now be downloading the stream
    $target = $null
    for ($i = 0; $i -lt 90 -and -not $target; $i++) { $target = Get-ChildItem $downloads -Filter "*.mp4" -ErrorAction SilentlyContinue | Select-Object -First 1; if (-not $target) { Start-Sleep -Milliseconds 500 } }
    Start-Sleep -Seconds 1

    $item = $json.listed.items | Select-Object -First 1
    Write-Host ""
    Write-Host "extension id as expected              : $($json.extensionId)"
    Write-Host "streams listed for the popup          : $(@($json.listed.items).Count)  (the quality list hides its own qualities)"
    if ($item) { Write-Host "  title / quality / length            : $($item.title) / up to $($item.maxHeight)p / $($item.duration) s" }
    Write-Host "badge on the tab                      : '$($json.badge)'"
    Write-Host "handed to the app                     : $($json.sent.accepted)  ($($json.sent.reason))"
    $ok = $false
    if ($target) {
        $clean = (& $ffmpeg -hide_banner -v error -i $target.FullName -f null - 2>&1 | Out-String).Trim()
        $info = (& $ffmpeg -hide_banner -i $target.FullName 2>&1 | Out-String)
        $ok = ($clean.Length -eq 0) -and ($info -match "Video: h264") -and ($info -match "Audio: aac") -and ($info -match "Duration: 00:00:1[12]")
        Write-Host "file saved by ShadeNet                : $($target.Name) ($($target.Length) bytes), decodes cleanly with picture and sound: $ok"
    }
    else { Write-Host "file saved by ShadeNet                : none" }

    $pass = $ok -and @($json.listed.items).Count -eq 1 -and $json.badge -eq "1" -and $json.sent.accepted -and $item.title -eq "My Holiday Video"
    if ($pass) { $result = 0; Write-Host "`nPASS" -ForegroundColor Green } else { Write-Host "`nFAIL" -ForegroundColor Red; Write-Host ($nodeOut -join "`n") }
}
finally {
    try { & $appExe --uninstall-browser-integration; Start-Sleep -Milliseconds 800 } catch { }
    if ($app -and -not $app.HasExited) { $app.Kill() }
    Get-Process ShadeNetDownloader -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt $scriptStart } | ForEach-Object { $_.Kill() }
    if ($server -and -not $server.HasExited) { $server.Kill() }
    Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine -like "*$profile*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
exit $result
