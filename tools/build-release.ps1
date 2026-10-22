<#
.SYNOPSIS
  Builds the release: a self-contained ShadeNet Downloader (no .NET needed on the target computer) with yt-dlp, ffmpeg and
  the browser extension inside, the installer that carries it, and the package an installed program updates itself with.

.DESCRIPTION
  dist\app                                     the program as it will be installed (also usable as a portable folder)
  dist\ShadeNetDownloader-Setup-<v>.exe        the installer (per user, no administrator rights)
  dist\ShadeNetDownloader-Update-<v>.zip       the update only: the program WITHOUT tools\ (yt-dlp and ffmpeg have their own updates)
  dist\update.json                             what an installed program reads to learn about the newest release
  dist\SHA256SUMS.txt                          checksums of the installer and the update package

  The installer is built twice from the same project: once WITHOUT the program inside (that copy becomes Uninstall.exe in the
  installed folder, which is also what applies updates), once WITH it (the program is embedded as one zip). yt-dlp and ffmpeg
  come from tools\ (tools\get-tools.ps1 fetches them, verified, when they are missing); the build never modifies them.

  -Version is the release number (the release workflow passes the next free tag, see tools\ci\next-version.ps1); without it
  the build is <MAJOR.MINOR>.0 from Directory.Build.props, a build for your own computer.

  Run from anywhere:  pwsh tools\build-release.ps1 [-Version 1.0.7] [-UpdateRepository owner/name] [-SkipTests]
#>
param(
    [string]$Runtime = "win-x64",
    [string]$Version = "",
    [string]$UpdateRepository = "",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
# the SDK of this computer when it lives in the user's folder (a CI runner has it on the PATH already)
$userSdk = Join-Path $env:USERPROFILE ".dotnet"
if (Test-Path (Join-Path $userSdk "dotnet.exe")) { $env:DOTNET_ROOT = $userSdk; $env:PATH = "$userSdk;$env:PATH" }
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $root
$dist = Join-Path $root "dist"
$app = Join-Path $dist "app"

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Run([string]$what, [scriptblock]$cmd) {
    & $cmd
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}

$props = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup
# (a property with a Condition attribute comes back as an XML element, not as text)
function Prop([string]$name) { $n = $props.$name; if ($n -is [System.Xml.XmlElement]) { $n.InnerText } else { [string]$n } }
if (-not $Version) { $Version = "$(Prop 'VersionMajorMinor').0" }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "the version must look like 1.2.3, not '$Version'" }
if (-not $UpdateRepository) { $UpdateRepository = Prop 'UpdateRepository' }
if ($UpdateRepository -notmatch '^[\w.-]+/[\w.-]+$') { throw "the update repository must look like owner/name, not '$UpdateRepository'" }
$version = $Version
$stamp = @("-p:Version=$version", "-p:UpdateRepository=$UpdateRepository")
Write-Host "ShadeNet Downloader $version ($Runtime), updates from $UpdateRepository"

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
Run "publish of the program" { dotnet publish src\ShadeNetDownloader -c Release -r $Runtime --self-contained true -o $app --nologo -v q -p:DebugType=embedded @stamp }

Step "the browser host (self-contained, in its own folder)"
Run "publish of the browser host" { dotnet publish src\ShadeNet.NativeHost -c Release -r $Runtime --self-contained true -o (Join-Path $app "NativeHost") --nologo -v q -p:DebugType=embedded @stamp }

Step "the uninstaller (this installer without the program inside)"
$single = @("-c", "Release", "-r", $Runtime, "--self-contained", "true", "--nologo", "-v", "q", "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true", "-p:DebugType=none") + $stamp
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
$builtVersion = (Get-Item (Join-Path $app "ShadeNetDownloader.exe")).VersionInfo.ProductVersion
if (-not $builtVersion.StartsWith($version)) { throw "the program says it is version $builtVersion, not $version" }

Add-Type -AssemblyName System.IO.Compression.FileSystem
function New-Zip([string]$path, [string]$from, [scriptblock]$include) {
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $from -Recurse -File) {
            $relative = $file.FullName.Substring($from.Length).TrimStart([char]92, [char]47).Replace([char]92, [char]47)
            if (& $include $relative) { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) }
        }
    }
    finally { $zip.Dispose() }
}

Step "the program as one zip (what the installer carries)"
$payload = Join-Path $dist "payload.zip"
New-Zip $payload $app { param($r) $true }
Write-Host ("payload.zip {0:N0} MB" -f ((Get-Item $payload).Length / 1MB))

Step "the update package (the program without tools\)"
$updateName = "ShadeNetDownloader-Update-$version.zip"
$updateZip = Join-Path $dist $updateName
New-Zip $updateZip $app { param($r) -not $r.StartsWith("tools/") }
$updateHash = (Get-FileHash $updateZip -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host ("{0} {1:N0} MB" -f $updateName, ((Get-Item $updateZip).Length / 1MB))

Step "the installer"
$setupOut = Join-Path $dist "setup"
Run "publish of the installer" { dotnet publish src\ShadeNet.Setup @single "-p:PayloadZip=$payload" -o $setupOut }
$installerName = "ShadeNetDownloader-Setup-$version.exe"
$installer = Join-Path $dist $installerName
Move-Item (Join-Path $setupOut "ShadeNetSetup.exe") $installer
Remove-Item $setupOut, $uninstallOut, $payload -Recurse -Force

$installerHash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
@("$installerHash  $installerName", "$updateHash  $updateName") | Set-Content (Join-Path $dist "SHA256SUMS.txt")

# what an installed program reads (releases/latest/download/update.json): which version is newest and where its package is
$manifest = [ordered]@{
    schema     = 1
    version    = $version
    package    = $updateName
    sha256     = $updateHash
    size       = (Get-Item $updateZip).Length
    repository = $UpdateRepository
}
[IO.File]::WriteAllText((Join-Path $dist "update.json"), ($manifest | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))

Write-Host ("`n{0}`n  {1:N0} MB, sha256 {2}" -f $installer, ((Get-Item $installer).Length / 1MB), $installerHash) -ForegroundColor Green
Write-Host ("{0}`n  {1:N0} MB, sha256 {2}" -f $updateZip, ((Get-Item $updateZip).Length / 1MB), $updateHash) -ForegroundColor Green
