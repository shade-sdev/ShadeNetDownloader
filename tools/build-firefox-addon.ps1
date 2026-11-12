<#
.SYNOPSIS
  Packs the Firefox add-on (the extension's code with extension/firefox/manifest.json) and, on request, has Mozilla sign it.

.DESCRIPTION
  Release Firefox keeps only add-ons signed by Mozilla. Signing needs an addons.mozilla.org account and its API key
  (https://addons.mozilla.org/developers/addon/api/key/). With -Sign the add-on is signed "unlisted": Mozilla signs it but does
  not publish it on its site; ShadeNet ships the signed file itself.

    pwsh tools/build-firefox-addon.ps1                     # artifacts/shadenet-firefox-unsigned.xpi (for checking, or to upload by hand)
    $env:AMO_JWT_ISSUER = "user:…"; $env:AMO_JWT_SECRET = "…"
    pwsh tools/build-firefox-addon.ps1 -Sign               # also extension/firefox/shadenet-firefox.xpi, which the app build then ships

  Every new version of the extension (the "version" in both manifests) has to be signed again: Mozilla signs a version once.
#>
param([switch]$Sign)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$source = Join-Path $root "extension\chromium"
$out = Join-Path $root "artifacts"
$stage = Join-Path ([IO.Path]::GetTempPath()) ("sn-firefox-" + [guid]::NewGuid().ToString("N").Substring(0, 8))

New-Item -ItemType Directory $stage, $out -Force | Out-Null
try {
    # the shared code, without Chrome's manifest, the tests and the notes
    Get-ChildItem $source -Force | Where-Object { $_.Name -notin "manifest.json", "test", "node_modules", "README.md" } |
        ForEach-Object { Copy-Item $_.FullName -Destination $stage -Recurse }
    Copy-Item (Join-Path $root "extension\firefox\manifest.json") $stage

    $version = (Get-Content (Join-Path $stage "manifest.json") -Raw | ConvertFrom-Json).version
    $xpi = Join-Path $out "shadenet-firefox-unsigned.xpi"
    if (Test-Path $xpi) { Remove-Item $xpi }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # (.NET writes "/" in the names inside, which Firefox requires)
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $xpi)
    Write-Host "Firefox add-on $version`: $xpi"

    if ($Sign) {
        if (-not $env:AMO_JWT_ISSUER -or -not $env:AMO_JWT_SECRET) { throw "Set AMO_JWT_ISSUER and AMO_JWT_SECRET (your addons.mozilla.org API key) first." }
        $signed = Join-Path $out "signed"
        New-Item -ItemType Directory $signed -Force | Out-Null
        npx --yes web-ext@8 sign --channel=unlisted --source-dir $stage --artifacts-dir $signed `
            --api-key $env:AMO_JWT_ISSUER --api-secret $env:AMO_JWT_SECRET
        if ($LASTEXITCODE -ne 0) { throw "web-ext sign failed ($LASTEXITCODE)" }
        $file = Get-ChildItem $signed -Filter *.xpi | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $file) { throw "Mozilla returned no signed file." }
        Copy-Item $file.FullName (Join-Path $root "extension\firefox\shadenet-firefox.xpi") -Force
        Write-Host "Signed: extension\firefox\shadenet-firefox.xpi (the next app build ships it as browser\shadenet-firefox.xpi)"
    }
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
