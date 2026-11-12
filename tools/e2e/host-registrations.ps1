# The browsers' native-messaging entries for ShadeNet (HKCU), saved before an e2e run registers its own and put back after it:
# an e2e run must leave the user's own browser integration exactly as it found it. (`--uninstall-browser-integration` alone
# would delete the entries even when they were the user's before the run.)
#
#   . (Join-Path $PSScriptRoot "host-registrations.ps1")
#   $savedHosts = Save-HostRegistrations
#   try { ... --install-browser-integration ... } finally { ... ; Restore-HostRegistrations $savedHosts }

$script:ShadeNetHostKeys = @(
    "Google\Chrome", "Microsoft\Edge", "BraveSoftware\Brave-Browser", "Chromium", "Mozilla"
) | ForEach-Object { "HKCU:\Software\$_\NativeMessagingHosts\com.shadenet.downloader" }

function Save-HostRegistrations {
    $saved = @{}
    foreach ($key in $script:ShadeNetHostKeys) {
        $saved[$key] = if (Test-Path $key) { (Get-ItemProperty $key).'(default)' } else { $null }
    }
    return $saved
}

function Restore-HostRegistrations([hashtable]$saved) {
    if (-not $saved) { return }
    foreach ($key in $script:ShadeNetHostKeys) {
        $before = $saved[$key]
        if ($null -ne $before) {
            New-Item $key -Force | Out-Null
            Set-ItemProperty $key -Name "(default)" -Value $before
        }
        elseif (Test-Path $key) {
            Remove-Item $key -Force
        }
    }
}
