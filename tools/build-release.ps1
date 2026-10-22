<#
.SYNOPSIS
  Builds the release: a self-contained ShadeNet Downloader (no .NET needed on the target computer) with yt-dlp, ffmpeg and
  the browser extension inside, and the installer that carries it.

.DESCRIPTION
  dist\app                                     the program as it will be installed (also usable as a portable folder)
  dist\ShadeNetDownloader-Setup-<version>.exe  the installer (per user, no administrator rights)
  dist\SHA256SUMS.txt                          checksum of the installer

  The installer is built twice from the same project: once WITHOUT the program inside (that copy becomes Uninstall.exe in the
  installed folder), once WITH it (the program is embedded as one zip). yt-dlp and ffmpeg come from tools\ (tools\get-tools.ps1
  fetches them, verified, when they are missing); the build never modifies them.

  Run from anywhere:  pwsh tools\build-release.ps1
#>
param(
    [string]$Runtime = "win-x64",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$env:DOTNET_ROOT = "C:\Users\Shade\.dotnet"
$env:PATH = "C:\Users\Shade\.dotnet;$env:PATH"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $root
$dist = Join-Path $root "dist"
$app = Join-Path $dist "app"

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Run([string]$what, [scriptblock]$cmd) {
    & $cmd
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}

$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version
Write-Host "ShadeNet Downloader $version ($Runtime)"

Step "helper programs"
if (-not (Test-Path "tools\yt-dlp\yt-dlp.exe") -or -not (Test-Path "tools\ffmpeg\ffmpeg.exe")) {
    & (Join-Path $PSScriptRoot "get-tools.ps1") -Tool all
}
foreach ($t in "tools\yt-dlp\yt-dlp.exe", "tools\ffmpeg\ffmpeg.exe") { if (-not (Test-Path $t)) { throw "$t is missing" } }
Write-Host "yt-dlp $(& tools\yt-dlp\yt-dlp.exe --version)"
Write-Host (& tools\ffmpeg\ffmpeg.exe -version | Select-Object -First 1)

if (-not $SkipTests) {
    Step "tests"
    foreach ($p in "ShadeNet.Setup.Tests", "ShadeNetDownloader.Tests", "ShadeNet.Bridge.Tests") {
        Run "tests of $p" { dotnet test "tests\$p" -c Release --nologo -v q }
    }
}

Step "clean"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory $app | Out-Null

Step "the program (self-contained)"
Run "publish of the program" { dotnet publish src\ShadeNetDownloader -c Release -r $Runtime --self-contained true -o $app --nologo -v q -p:DebugType=embedded }

Step "the browser host (self-contained, in its own folder)"
Run "publish of the browser host" { dotnet publish src\ShadeNet.NativeHost -c Release -r $Runtime --self-contained true -o (Join-Path $app "NativeHost") --nologo -v q -p:DebugType=embedded }

Step "the uninstaller (this installer without the program inside)"
$single = @("-c", "Release", "-r", $Runtime, "--self-contained", "true", "--nologo", "-v", "q", "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true", "-p:DebugType=none")
$uninstallOut = Join-Path $dist "uninstaller"
Run "publish of the uninstaller" { dotnet publish src\ShadeNet.Setup @single -o $uninstallOut }
Copy-Item (Join-Path $uninstallOut "ShadeNetSetup.exe") (Join-Path $app "Uninstall.exe")

Step "what is inside"
foreach ($must in "ShadeNetDownloader.exe", "NativeHost\ShadeNet.NativeHost.exe", "NativeHost\hostfxr.dll", "tools\yt-dlp\yt-dlp.exe",
    "tools\ffmpeg\ffmpeg.exe", "tools\ffmpeg\LICENSE.txt", "browser\chromium\manifest.json", "licenses\THIRD-PARTY-NOTICES.txt", "Uninstall.exe", "hostfxr.dll") {
    if (-not (Test-Path (Join-Path $app $must))) { throw "the release folder lacks $must" }
}
$size = (Get-ChildItem $app -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("{0} files, {1:N0} MB" -f (Get-ChildItem $app -Recurse -File).Count, ($size / 1MB))

Step "the program as one zip"
$payload = Join-Path $dist "payload.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($app, $payload, [IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host ("payload.zip {0:N0} MB" -f ((Get-Item $payload).Length / 1MB))

Step "the installer"
$setupOut = Join-Path $dist "setup"
Run "publish of the installer" { dotnet publish src\ShadeNet.Setup @single "-p:PayloadZip=$payload" -o $setupOut }
$installer = Join-Path $dist "ShadeNetDownloader-Setup-$version.exe"
Move-Item (Join-Path $setupOut "ShadeNetSetup.exe") $installer
Remove-Item $setupOut, $uninstallOut, $payload -Recurse -Force

$hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $installer -Leaf)" | Set-Content (Join-Path $dist "SHA256SUMS.txt")
Write-Host ("`n{0}`n  {1:N0} MB, sha256 {2}" -f $installer, ((Get-Item $installer).Length / 1MB), $hash) -ForegroundColor Green
