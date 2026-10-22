<#
.SYNOPSIS
  Fetches the helper programs ShadeNet bundles: yt-dlp (resolves YouTube and many other sites) and ffmpeg (joins streams).

.DESCRIPTION
  Both go to tools\<name>\ (not part of the source; the build copies them next to the program when present, and the
  installer ships them). Downloads come from the projects' official release pages and are verified against the
  checksums published with the release before they are kept.

    yt-dlp  https://github.com/yt-dlp/yt-dlp/releases/latest     (Unlicense: free to bundle)
    ffmpeg  https://github.com/BtbN/FFmpeg-Builds/releases        (the *LGPL* build: it may be shipped with a closed-source
                                                                   program; the GPL builds may not without GPL obligations)

  Run again at any time to update. Inside the program, Settings > Downloads > Helper programs looks for newer versions about
  once a day, asks before installing one, and puts the update in the user's own folder (the install folder is not writable);
  the copy that came with the program is never modified. tool.json next to each program records what it is.
#>
param(
    [ValidateSet("yt-dlp", "ffmpeg", "all")] [string]$Tool = "yt-dlp",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$tools = Join-Path $root "tools"

function Get-Verified([string]$url, [string]$destination, [string]$sums, [string]$assetName) {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("sn-get-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
    New-Item -ItemType Directory $tmp | Out-Null
    try {
        $file = Join-Path $tmp $assetName
        Write-Host "  downloading $url"
        Invoke-WebRequest $url -OutFile $file -UseBasicParsing
        $sumsText = (Invoke-WebRequest $sums -UseBasicParsing).Content
        if ($sumsText -is [byte[]]) { $sumsText = [Text.Encoding]::UTF8.GetString($sumsText) }
        $line = ($sumsText -split "`n" | Where-Object { $_ -match [regex]::Escape($assetName) + "\s*$" } | Select-Object -First 1)
        if (-not $line) { throw "no checksum for $assetName in $sums" }
        $expected = ($line.Trim() -split "\s+")[0].ToLowerInvariant()
        $actual = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) { throw "checksum mismatch for $assetName (expected $expected, got $actual)" }
        Write-Host "  checksum ok ($actual)"
        New-Item -ItemType Directory (Split-Path $destination) -Force | Out-Null
        Move-Item $file $destination -Force
    }
    finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}

if ($Tool -in "yt-dlp", "all") {
    Write-Host "yt-dlp"
    $exe = Join-Path $tools "yt-dlp\yt-dlp.exe"
    $base = "https://github.com/yt-dlp/yt-dlp/releases/latest/download"
    Get-Verified "$base/yt-dlp.exe" $exe "$base/SHA2-256SUMS" "yt-dlp.exe"
    $version = (& $exe --version).Trim()
    Write-Host "  version: $version"
    @{ version = $version; published = (Get-Date).ToUniversalTime().ToString("o") } | ConvertTo-Json | Set-Content (Join-Path $tools "yt-dlp\tool.json")
}

if ($Tool -in "ffmpeg", "all") {
    Write-Host "ffmpeg (LGPL build)"
    $dir = Join-Path $tools "ffmpeg"
    $base = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest"
    $asset = "ffmpeg-master-latest-win64-lgpl.zip"
    $zip = Join-Path ([IO.Path]::GetTempPath()) $asset
    Get-Verified "$base/$asset" $zip "$base/checksums.sha256" $asset
    $extract = Join-Path ([IO.Path]::GetTempPath()) ("sn-ffmpeg-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
    Expand-Archive $zip $extract
    New-Item -ItemType Directory $dir -Force | Out-Null
    Copy-Item (Get-ChildItem $extract -Recurse -Filter ffmpeg.exe | Select-Object -First 1).FullName (Join-Path $dir "ffmpeg.exe") -Force
    # the LGPL asks for its text to be passed on with the program
    $license = Get-ChildItem $extract -Recurse -Filter LICENSE.txt | Select-Object -First 1
    if ($license) { Copy-Item $license.FullName (Join-Path $dir "LICENSE.txt") -Force }
    Remove-Item $zip, $extract -Recurse -Force
    Write-Host "  $(& (Join-Path $dir 'ffmpeg.exe') -version | Select-Object -First 1)"
    # the build's date, from the release page, is what "a newer build exists" is measured against
    $release = Invoke-RestMethod "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest" -Headers @{ "User-Agent" = "ShadeNetDownloader" }
    $built = ($release.assets | Where-Object { $_.name -eq $asset } | Select-Object -First 1).updated_at
    @{ version = ([datetime]$built).ToString("yyyy-MM-dd"); published = ([datetime]$built).ToUniversalTime().ToString("o") } | ConvertTo-Json | Set-Content (Join-Path $dir "tool.json")
}

Write-Host "done: $tools"
