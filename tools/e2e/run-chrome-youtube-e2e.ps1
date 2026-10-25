<#
.SYNOPSIS
  Real Chrome + the extension + the app on a real YouTube page. Use a video you may download (the default is a Creative
  Commons film). The extension lists the video ONCE (its page), "Download" hands the page to ShadeNet, which accepts it
  (the quality dialog and the download itself belong to the page route that run-chrome-facebook-e2e.ps1 drives). Needs internet.

.DESCRIPTION
  YouTube's player asks for its media in a form that names no tracks (SABR: no itag, no mime), so what is playing cannot be
  read from the network any more; the page, which yt-dlp reads, is what is offered. This script used to wait for a "tracks"
  entry, which no longer exists, and for a duplicate entry (the page again, spelled with an extra parameter) it did not notice.
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
    $nodeOut = & node (Join-Path $PSScriptRoot "chrome-media-e2e.mjs") --chrome $Chrome --extension $extension --profile $profile --page $Url --kind page --autoplay 1 --debug (Join-Path $env:TEMP "yt-debug.png") --wait-ms 45000 --expect-id bcegodlanfeghhakadmalbkbhbhbejop 2>&1
    $json = ($nodeOut -join "`n" | ConvertFrom-Json)
    $items = @($json.listed.items)
    $item = $items | Where-Object { $_.kind -eq "page" } | Select-Object -First 1

    Write-Host ""
    Write-Host "page title                : $($json.pageTitle)"
    Write-Host "listed for the popup      : $($items.Count) item(s)"
    if ($item) { Write-Host "  offered                 : $($item.title) | $($item.quality) | $($item.url)" }
    Write-Host "badge on the tab          : '$($json.badge)'"
    Write-Host "handed to the app         : $($json.sent.accepted)  ($($json.sent.reason))"

    # the video is listed ONCE, as the page itself (not a guess), and the app took it
    $once = $items.Count -eq 1 -and $item -and -not $item.guess
    $taken = $json.sent.accepted -eq $true
    Write-Host "the video is listed once  : $once"
    Write-Host "the app accepted it       : $taken"
    if ($once -and $taken) { $result = 0 }
    else {
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
