<#
.SYNOPSIS
  Real Chrome + real extension + real app: (1) the extension finds the app by itself (the popup says "not running" while it is
  closed, never starts it, and shows "Connected" a moment after the app starts, without a click); (2) a downloadable video is
  announced on the video itself with its name, one click sends it, the app saves it under that name; several videos on a page
  each have their own name; the popup's switch hides the button.

.DESCRIPTION
  Throw-away Chrome profile, throw-away ShadeNet data folder, a local test server. The only thing written outside the temp
  folder is the native-messaging registry entry under HKCU: whatever was there before is put back at the end.
  Screenshots: docs\overlay-*.png.
#>
param(
    [string]$Chrome = "C:\Program Files\Google\Chrome\Application\chrome.exe",
    [switch]$Keep,
    # another build of the program (default: the Debug build), e.g. src\ShadeNetDownloader\bin\Release\net10.0-windows\ShadeNetDownloader.exe
    [string]$AppExe = "",
    # also look at a real YouTube watch page (needs the internet; nothing is downloaded)
    [string]$YouTube = ""
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$env:DOTNET_ROOT = "C:\Users\Shade\.dotnet"
$appExe = if ($AppExe) { $AppExe } else { Join-Path $root "src\ShadeNetDownloader\bin\Debug\net10.0-windows\ShadeNetDownloader.exe" }
$extension = Join-Path $root "extension\chromium"
# a full ffmpeg (with the x264 encoder) makes the test video; the LGPL build that ships with the app has no x264
$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) { throw "this check needs ffmpeg with libx264 on the PATH" }

$work = Join-Path $env:TEMP ("sn-e2eo-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$data = Join-Path $work "data"; $downloads = Join-Path $work "downloads"; $profile = Join-Path $work "chrome-profile"; $site = Join-Path $work "site"
New-Item -ItemType Directory $data, $downloads, $profile, $site | Out-Null
$env:SHADENET_DATA_DIR = $data

# the registry entries the browser integration writes: remember what was there
$keys = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.shadenet.downloader", "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.shadenet.downloader", "HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.shadenet.downloader"
$saved = @{}
foreach ($k in $keys) { if (Test-Path $k) { $saved[$k] = (Get-Item $k).GetValue("") } }

$server = $null; $disguised = $null; $result = 1; $scriptStart = Get-Date
try {
    # a real 4 s mp4, and the pages that play it
    $clip = Join-Path $site "clip.mp4"
    & $ffmpeg -y -loglevel error -f lavfi -i "testsrc=duration=4:size=480x270:rate=25" -f lavfi -i "sine=frequency=330:duration=4" -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -movflags +faststart $clip
    foreach ($n in "a", "b", "c") { Copy-Item $clip (Join-Path $site "$n.mp4") }

    Set-Content (Join-Path $site "video.html") @'
<!doctype html><html><head><meta charset="utf-8"><title>Holiday in Lisbon - Travel Diary</title>
<meta property="og:title" content="Holiday in Lisbon"></head><body style="font-family:sans-serif"><h1>Travel Diary</h1>
<video src="clip.mp4" width="640" height="360" controls muted autoplay loop></video><p>More text below the video.</p></body></html>
'@
    Set-Content (Join-Path $site "many.html") @'
<!doctype html><html><head><meta charset="utf-8"><title>Video feed</title></head><body style="font-family:sans-serif">
<article><h2>Cats on a skateboard</h2><video src="a.mp4" width="420" height="236" controls muted autoplay loop></video></article>
<article><h2>Sunrise over the bay</h2><video src="b.mp4" width="300" height="170" controls muted autoplay loop></video></article>
<article><h2>How bread is made</h2><video src="c.mp4" width="300" height="170" controls muted autoplay loop></video></article>
</body></html>
'@

    # a player the way many real ones are: blob: video, playlist under a neutral name and type, pieces called .jpg, every request
    # refused without the player's own Referer. Its pieces come from a real fMP4 HLS made by ffmpeg.
    $hls = Join-Path $work "hls"
    New-Item -ItemType Directory $hls | Out-Null
    Push-Location $hls
    & $ffmpeg -y -loglevel error -f lavfi -i "testsrc=duration=8:size=320x240:rate=25" -f lavfi -i "sine=frequency=330:duration=8" -c:v libx264 -profile:v baseline -level 3.0 -pix_fmt yuv420p -g 50 -c:a aac `
        -f hls -hls_time 2 -hls_playlist_type vod -hls_segment_type fmp4 -hls_fmp4_init_filename init.mp4 index.m3u8
    Pop-Location
    # picture and sound as two playlists of their own (what some players ask for, one after the other): two real fMP4 streams
    foreach ($kind in "v", "a") {
        $dir = Join-Path $site "split\$kind"
        New-Item -ItemType Directory $dir -Force | Out-Null
        Push-Location $dir
        $map = if ($kind -eq "v") { @("-map", "0:v", "-c:v", "libx264", "-profile:v", "baseline", "-level", "3.0", "-pix_fmt", "yuv420p", "-g", "50") } else { @("-map", "1:a", "-c:a", "aac") }
        & $ffmpeg -y -loglevel error -f lavfi -i "testsrc=duration=8:size=320x240:rate=25" -f lavfi -i "sine=frequency=330:duration=8" @map `
            -f hls -hls_time 2 -hls_playlist_type vod -hls_segment_type fmp4 -hls_fmp4_init_filename init.mp4 index.m3u8
        Pop-Location
    }
    Set-Content (Join-Path $site "split.html") @'
<!doctype html><html><head><meta charset="utf-8"><title>Split streams film</title></head><body style="font-family:sans-serif"><h1>Two playlists</h1>
<video id="v" width="480" height="270" controls muted autoplay></video>
<script>
(async () => {
  const load = async (base) => {
    const text = await (await fetch(base + 'index.m3u8')).text();
    return { base, init: /URI="([^"]+)"/.exec(text)[1], pieces: text.split('\n').filter((l) => l && !l.startsWith('#')) };
  };
  const [video, sound] = await Promise.all([load('split/v/'), load('split/a/')]);
  const media = new MediaSource();
  document.getElementById('v').src = URL.createObjectURL(media);
  await new Promise((r) => media.addEventListener('sourceopen', r, { once: true }));
  const vb = media.addSourceBuffer('video/mp4; codecs="avc1.42E01E"');
  const ab = media.addSourceBuffer('audio/mp4; codecs="mp4a.40.2"');
  const feed = async (buffer, track) => {
    for (const piece of [track.init, ...track.pieces]) {
      buffer.appendBuffer(await (await fetch(track.base + piece)).arrayBuffer());
      await new Promise((r) => buffer.addEventListener('updateend', r, { once: true }));
    }
  };
  await Promise.all([feed(vb, video), feed(ab, sound)]);
  media.endOfStream();
})();
</script></body></html>
'@
    $disguisedOut = Join-Path $work "disguised.txt"
    $disguised = Start-Process node -ArgumentList (Join-Path $PSScriptRoot "disguised-player-server.mjs"), $hls -RedirectStandardOutput $disguisedOut -PassThru -WindowStyle Hidden
    $dport = $null
    for ($i = 0; $i -lt 40 -and -not $dport; $i++) { Start-Sleep -Milliseconds 300; if (Test-Path $disguisedOut) { $m = Select-String -Path $disguisedOut -Pattern "READY (\d+)" | Select-Object -First 1; if ($m) { $dport = $m.Matches[0].Groups[1].Value } } }
    if (-not $dport) { throw "the player server did not start" }
    Set-Content (Join-Path $site "outer2.html") "<!doctype html><html><head><meta charset='utf-8'><title>A page with a player</title></head><body style='font-family:sans-serif'><h1>Watch</h1><iframe src='http://localhost:$dport/player.html' width='660' height='390' style='border:0'></iframe></body></html>"

    $serveOut = Join-Path $work "server.txt"
    $server = Start-Process node -ArgumentList (Join-Path $PSScriptRoot "static-server.mjs"), $site -RedirectStandardOutput $serveOut -PassThru -WindowStyle Hidden
    $port = $null
    for ($i = 0; $i -lt 40 -and -not $port; $i++) { Start-Sleep -Milliseconds 300; if (Test-Path $serveOut) { $m = Select-String -Path $serveOut -Pattern "READY (\d+)" | Select-Object -First 1; if ($m) { $port = $m.Matches[0].Groups[1].Value } } }
    if (-not $port) { throw "static server did not start" }

    # an iframe from ANOTHER SITE (localhost is not 127.0.0.1 to the browser: its own process), and a video fed through a buffer (blob:)
    Set-Content (Join-Path $site "outer.html") "<!doctype html><html><head><meta charset='utf-8'><title>My blog</title></head><body style='font-family:sans-serif'><h1>A blog post</h1><iframe src='http://localhost:$port/inner.html' width='660' height='390' style='border:0'></iframe></body></html>"
    Set-Content (Join-Path $site "inner.html") @'
<!doctype html><html><head><meta charset="utf-8"><title>Interview with a baker</title></head><body style="margin:0">
<video src="clip.mp4" width="640" height="360" controls muted autoplay loop></video></body></html>
'@
    New-Item -ItemType Directory (Join-Path $site "reel") | Out-Null
    Set-Content (Join-Path $site "reel\12345.html") @'
<!doctype html><html><head><meta charset="utf-8"><title>Sunrise reel</title></head><body style="font-family:sans-serif"><h1>Reels</h1>
<video id="v" width="480" height="270" controls muted autoplay loop></video>
<script>fetch('../clip.mp4').then((r) => r.blob()).then((b) => { document.getElementById('v').src = URL.createObjectURL(b); });</script></body></html>
'@

    @{ Theme = "Dark"; Engine = @{ DownloadRoot = $downloads; UseCategoryFolders = $false }; Browser = @{ Mode = "StartImmediately" }; General = @{ ShowTrayIcon = $false } } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $data "settings.json")

    $install = Start-Process $appExe -ArgumentList "--install-browser-integration" -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "registering the browser integration failed (exit $($install.ExitCode))" }

    New-Item -ItemType Directory (Join-Path $profile "Default") | Out-Null
    @{ download = @{ default_directory = (Join-Path $work "chrome-downloads"); prompt_for_download = $false } } | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $profile "Default\Preferences")

    Write-Host "starting Chrome (the app is NOT running yet) ..."
    $out = & node (Join-Path $PSScriptRoot "chrome-overlay-e2e.mjs") --chrome $Chrome --extension $extension --profile $profile --base "http://127.0.0.1:$port" `
        --app $appExe --downloads $downloads --clip $clip --shots (Join-Path $root "docs") --expect-id bcegodlanfeghhakadmalbkbhbhbejop --youtube $YouTube --disguised-port $dport --ffmpeg $ffmpeg 2>&1
    $json = ($out -join "`n") | ConvertFrom-Json

    Write-Host ""
    foreach ($c in $json.checks.PSObject.Properties) {
        $mark = if ($c.Value.passed) { "ok  " } else { "FAIL" }
        Write-Host ("  [{0}] {1}  {2}" -f $mark, $c.Name, $c.Value.detail)
    }
    if ($json.error) { Write-Host "  error: $($json.error)" -ForegroundColor Red }
    if ($json.ok) { $result = 0; Write-Host "`nPASS" -ForegroundColor Green } else { Write-Host "`nFAIL" -ForegroundColor Red; Write-Host ($out -join "`n") }
}
finally {
    try { & $appExe --uninstall-browser-integration; Start-Sleep -Milliseconds 800 } catch { }
    foreach ($k in $saved.Keys) { New-Item -Path $k -Force | Out-Null; Set-Item -Path $k -Value $saved[$k] }
    Get-Process ShadeNetDownloader, ShadeNet.NativeHost -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt $scriptStart } | ForEach-Object { $_.Kill() }
    if ($server -and -not $server.HasExited) { $server.Kill() }
    if ($disguised -and -not $disguised.HasExited) { $disguised.Kill() }
    Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine -like "*$profile*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
    if ($Keep) { Write-Host "kept: $work" } else { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
}
exit $result
