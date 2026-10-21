<#
.SYNOPSIS
  Real Chrome + the extension + the app on a real YouTube page. Use a video you may download (the default is a Creative
  Commons film). The extension lists what the page's player is fetching, "Download" hands it to ShadeNet, which fetches
  the picture and the sound in parallel ranges and joins them. Needs internet and ffmpeg.

.DESCRIPTION
  Throw-away Chrome profile (your own is never touched); the native-messaging registry entry is removed again at the end.
#>
param(
    [string]$Chrome = "C:\Program Files\Google\Chrome\Application\chrome.exe",
    [string]$Url = "https://www.youtube.com/watch?v=aqz-KE-bpKQ"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$env:DOTNET_ROOT = "C:\Users\Shade\.dotnet"
$appExe = Join-Path $root "src\ShadeNetDownloader\bin\Debug\net10.0-windows\ShadeNetDownloader.exe"
$extension = Join-Path $root "extension\chromium"
$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) { throw "this check needs ffmpeg on the PATH" }

$work = Join-Path $env:TEMP ("sn-e2ey-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$data = Join-Path $work "data"; $downloads = Join-Path $work "downloads"; $profile = Join-Path $work "chrome-profile"
New-Item -ItemType Directory $data, $downloads, $profile | Out-Null
$env:SHADENET_DATA_DIR = $data
$app = $null; $result = 1; $scriptStart = Get-Date
try {
    @{ Theme = "Dark"; Engine = @{ DownloadRoot = $downloads; UseCategoryFolders = $false }; Browser = @{ Mode = "StartImmediately" }; General = @{ ShowTrayIcon = $false } } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $data "settings.json")
    $install = Start-Process $appExe -ArgumentList "--install-browser-integration" -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "registering the browser integration failed" }
    $app = Start-Process $appExe -PassThru
    Start-Sleep -Seconds 4

    New-Item -ItemType Directory (Join-Path $profile "Default") | Out-Null
    @{ download = @{ default_directory = (Join-Path $work "chrome-downloads"); prompt_for_download = $false } } | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $profile "Default\Preferences")

    Write-Host "starting Chrome on $Url ..."
    $nodeOut = & node (Join-Path $PSScriptRoot "chrome-media-e2e.mjs") --chrome $Chrome --extension $extension --profile $profile --page $Url --kind tracks --autoplay 1 --debug "$env:TEMPyt-debug.png" --wait-ms 45000 --expect-id bcegodlanfeghhakadmalbkbhbhbejop 2>&1
    $json = ($nodeOut -join "`n" | ConvertFrom-Json)
    $item = $json.listed.items | Where-Object { $_.kind -eq "tracks" } | Select-Object -First 1

    Write-Host ""
    Write-Host "page title                : $($json.pageTitle)"
    Write-Host "listed for the popup      : $(@($json.listed.items).Count) item(s)"
    if ($item) { Write-Host "  playing                 : $($item.title) | $($item.quality) | $($item.duration) s" }
    Write-Host "badge on the tab          : '$($json.badge)'"
    Write-Host "handed to the app         : $($json.sent.accepted)  ($($json.sent.reason))"

    $target = $null
    for ($i = 0; $i -lt 240 -and -not $target; $i++) {
        $target = Get-ChildItem $downloads -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in ".mp4", ".mkv" } | Select-Object -First 1
        if (-not $target) { Start-Sleep -Milliseconds 500 }
    }
    Start-Sleep -Seconds 1
    if ($target) {
        $clean = (& $ffmpeg -hide_banner -v error -i $target.FullName -f null - 2>&1 | Out-String).Trim()
        $info = (& $ffmpeg -hide_banner -i $target.FullName 2>&1 | Out-String)
        $dur = [regex]::Match($info, "Duration: (\d+:\d+:[\d.]+)").Groups[1].Value
        Write-Host "file saved                : $($target.Name) ($([math]::Round($target.Length / 1MB, 1)) MB, $dur)"
        Write-Host "decodes cleanly           : $($clean.Length -eq 0)"
        Write-Host "has picture and sound     : $(($info -match 'Video:') -and ($info -match 'Audio:'))"
        if ($clean.Length -eq 0 -and ($info -match 'Video:') -and ($info -match 'Audio:')) { $result = 0 }
    }
    else {
        Write-Host "file saved                : none"
        $host_log = Join-Path $data "host.log"
        $crash = Join-Path $data "crash.log"
        if (Test-Path $crash) { Get-Content $crash | Select-Object -First 15 }
        Write-Host ($nodeOut -join "`n")
    }
    if ($result -eq 0) { Write-Host "`nPASS" -ForegroundColor Green } else { Write-Host "`nFAIL" -ForegroundColor Red }
}
finally {
    try { & $appExe --uninstall-browser-integration; Start-Sleep -Milliseconds 800 } catch { }
    if ($app -and -not $app.HasExited) { $app.Kill() }
    Get-Process ShadeNetDownloader -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt $scriptStart } | ForEach-Object { $_.Kill() }
    Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine -like "*$profile*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
exit $result
