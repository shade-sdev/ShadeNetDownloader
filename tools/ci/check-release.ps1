<#
.SYNOPSIS
  Looks at what tools\build-release.ps1 made before it is published: the right version everywhere, an update package that is
  really "the program without tools\", a manifest whose checksum is the package's, an installer of a believable size.

.DESCRIPTION
  A release that an installed program would refuse (wrong hash, wrong name) or that would break it (a package that carries or
  lacks the wrong files) must not reach anybody: this stops the workflow before it publishes.
#>
param(
    [Parameter(Mandatory)] [string]$Version,
    [string]$Dist = (Join-Path $PSScriptRoot "..\..\dist")
)

$ErrorActionPreference = "Stop"
$Dist = (Resolve-Path $Dist).Path
$problems = New-Object System.Collections.Generic.List[string]
function Fail([string]$text) { $script:problems.Add($text); Write-Host "  [FAIL] $text" -ForegroundColor Red }
function Ok([string]$text) { Write-Host "  [ok]   $text" }

$installer = Join-Path $Dist "ShadeNetDownloader-Setup-$Version.exe"
$package = Join-Path $Dist "ShadeNetDownloader-Update-$Version.zip"
$manifestPath = Join-Path $Dist "update.json"
$sums = Join-Path $Dist "SHA256SUMS.txt"
foreach ($f in $installer, $package, $manifestPath, $sums) { if (-not (Test-Path $f)) { Fail "$(Split-Path $f -Leaf) is missing" } }
if ($problems.Count -gt 0) { throw "the release is incomplete" }

# the manifest an installed program will read
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schema -eq 1) { Ok "update.json: schema 1" } else { Fail "update.json has schema $($manifest.schema), programs read schema 1" }
if ($manifest.version -eq $Version) { Ok "update.json: version $Version" } else { Fail "update.json says version $($manifest.version), not $Version" }
if ($manifest.package -eq (Split-Path $package -Leaf)) { Ok "update.json names the package file" } else { Fail "update.json names '$($manifest.package)'" }
$hash = (Get-FileHash $package -Algorithm SHA256).Hash.ToLowerInvariant()
if ($manifest.sha256 -eq $hash) { Ok "update.json: the package's SHA-256" } else { Fail "update.json has a different SHA-256 than the package ($($manifest.sha256) / $hash)" }
if ($manifest.size -eq (Get-Item $package).Length) { Ok "update.json: the package's size" } else { Fail "update.json has a different size than the package" }

# SHA256SUMS: both files, correct
$lines = Get-Content $sums
foreach ($f in $installer, $package) {
    $name = Split-Path $f -Leaf
    $expected = (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($lines -contains "$expected  $name") { Ok "SHA256SUMS: $name" } else { Fail "SHA256SUMS has no matching line for $name" }
}

# the package: the program, the uninstaller (it applies the next update), no tools\
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $names = $zip.Entries | Where-Object { $_.Name } | ForEach-Object { $_.FullName }
    foreach ($must in "ShadeNetDownloader.exe", "Uninstall.exe", "NativeHost/ShadeNet.NativeHost.exe", "browser/chromium/manifest.json", "licenses/THIRD-PARTY-NOTICES.txt") {
        if ($names -contains $must) { Ok "the package has $must" } else { Fail "the package lacks $must" }
    }
    $tools = @($names | Where-Object { $_.StartsWith("tools/") })
    if ($tools.Count -eq 0) { Ok "the package carries no tools\ (yt-dlp and ffmpeg update themselves)" } else { Fail "the package carries $($tools.Count) file(s) of tools\ (e.g. $($tools[0]))" }
    $bad = @($names | Where-Object { $_ -match '(^|/)\.\.(/|$)' -or $_.StartsWith("/") -or $_ -eq "install.json" -or $_.StartsWith(".update") })
    if ($bad.Count -eq 0) { Ok "no file name an update would refuse" } else { Fail "the package has names an update refuses: $($bad -join ', ')" }

    # the program inside says the version it is
    $exe = $zip.Entries | Where-Object { $_.FullName -eq "ShadeNetDownloader.exe" }
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("sn-check-" + [guid]::NewGuid().ToString("N").Substring(0, 8) + ".exe")
    [IO.Compression.ZipFileExtensions]::ExtractToFile($exe, $tmp)
    try {
        $said = (Get-Item $tmp).VersionInfo.ProductVersion
        if ($said.StartsWith($Version)) { Ok "the program in the package says version $said" } else { Fail "the program in the package says $said, not $Version" }
    }
    finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
}
finally { $zip.Dispose() }

$mb = (Get-Item $installer).Length / 1MB
if ($mb -gt 50) { Ok ("the installer is {0:N0} MB" -f $mb) } else { Fail ("the installer is only {0:N1} MB: the program is not inside" -f $mb) }

if ($problems.Count -gt 0) { throw "the release is not fit to publish ($($problems.Count) problem(s))" }
Write-Host "`nrelease $Version looks right" -ForegroundColor Green
