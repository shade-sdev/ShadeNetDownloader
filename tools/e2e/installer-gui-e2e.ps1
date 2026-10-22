<#
.SYNOPSIS
  The installer's WINDOWS, driven like a person would (UI Automation), in a sandbox: the welcome window opens (not an error box),
  the choices are there, "Install" runs and ends on the "All done" page, a second start offers an update, and the uninstaller's
  two questions end in a removed program that keeps the user's data. Nothing outside throw-away folders and a throw-away registry
  key is touched: the browser connection and "open ShadeNet" are switched off, and every folder is redirected.
  Screenshots: docs\installer-gui-*.png.
#>
param([string]$Installer = (Get-ChildItem (Join-Path $PSScriptRoot "..\..\dist") -Filter "ShadeNetDownloader-Setup-*.exe" | Select-Object -First 1).FullName)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public class SGui {
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
'@
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$docs = Join-Path $root "docs"
$AE = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]; $ct = [System.Windows.Automation.ControlType]

$work = Join-Path $env:TEMP ("sn-gui-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
$inst = Join-Path $work "Program Files Like\ShadeNet Downloader"
$data = Join-Path $work "data"; $sm = Join-Path $work "startmenu"; $dt = Join-Path $work "desktop"
$regRoot = "Software\ShadeNetTests\InstallGui-" + [guid]::NewGuid().ToString("N").Substring(0, 8)
New-Item -ItemType Directory $work, $data | Out-Null
Set-Content (Join-Path $data "settings.json") "{}"          # something of the user's, that must survive an uninstall
$common = @("--dir", "`"$inst`"", "--registry-root", $regRoot, "--start-menu-dir", "`"$sm`"", "--desktop-dir", "`"$dt`"", "--data-dir", "`"$data`"")
$ok = $true
function Check([string]$what, [bool]$cond, [string]$detail = "") {
    Write-Host ("  [{0}] {1} {2}" -f $(if ($cond) { "ok  " } else { "FAIL" }), $what, $detail)
    if (-not $cond) { $script:ok = $false }
}

function WindowsOf([int]$processId) { $AE::RootElement.FindAll($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $processId))) }
function WaitWindow([int]$processId, [string]$title, [int]$seconds = 25) {
    for ($i = 0; $i -lt $seconds * 4; $i++) {
        foreach ($w in WindowsOf $processId) { if ($w.Current.Name -like $title) { return $w } }
        Start-Sleep -Milliseconds 250
    }
    return $null
}
function ById($win, [string]$id) { $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id))) }
function ByName($win, [string]$name, $type) {
    $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)), (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type)))))
}
# a message box's buttons are not always reported as buttons: find by name alone, and press with whatever pattern there is
function ByNameAny($win, [string]$name) { $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name))) }
function Press($element) {
    $pattern = $null
    if (-not $element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { throw "'$($element.Current.Name)' cannot be pressed" }
    $pattern.Invoke()
}
# the element of that name that CAN be pressed (a message box has a pane and a button both called "OK")
function PressNamed($win, [string]$name) {
    foreach ($e in $win.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)))) {
        $pattern = $null
        if ($e.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); return }
    }
    throw "nothing named '$name' can be pressed in '$($win.Current.Name)': $((Texts $win) -join ' | ')"
}
# Enter in a message box chooses its default button (the first question: OK; the second: No). Only sent when the keyboard focus is
# really in that process's window, so a keystroke can never land in anything else.
function EnterIn($win, [int]$processId) {
    [SGui]::SetForegroundWindow([IntPtr]$win.Current.NativeWindowHandle) | Out-Null
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 150
        $focus = $AE::FocusedElement
        if ($focus -and $focus.Current.ProcessId -eq $processId) { [System.Windows.Forms.SendKeys]::SendWait("{ENTER}"); return }
    }
    throw "the keyboard focus did not reach '$($win.Current.Name)'; nothing was sent"
}
function PressOld($element) { ($element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
function SetChecked($box, [bool]$on) {
    $p = $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if (($p.Current.ToggleState -eq "On") -ne $on) { $p.Toggle() }
}
function Shot($win, [string]$name) {
    $h = [IntPtr]$win.Current.NativeWindowHandle
    $r = New-Object SGui+RECT; [SGui]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc(); [SGui]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc)
    $bmp.Save((Join-Path $docs "$name.png")); $g.Dispose(); $bmp.Dispose()
}
function Texts($win) { $win.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } | Where-Object { $_ } }
$started = @()

try {
    Write-Host "installer: $Installer"

    Write-Host "`n1. the welcome window"
    $p = Start-Process $Installer -ArgumentList $common -PassThru; $started += $p
    $win = WaitWindow $p.Id "ShadeNet Downloader Setup"
    if (-not $win) {
        $err = (WindowsOf $p.Id | ForEach-Object { "'" + $_.Current.Name + "': " + ((Texts $_) -join " / ") }) -join "; "
        Check "the window opens" $false "(instead: $err)"
        throw "the installer window did not open"
    }
    Check "the window opens" $true
    Start-Sleep -Milliseconds 600
    Shot $win "installer-gui-0-welcome"
    Check "it says Install ShadeNet Downloader" ((Texts $win) -contains "Install ShadeNet Downloader")
    Check "the folder box holds the chosen folder" ((ById $win "DirBox").GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq $inst)

    Write-Host "`n2. Install"
    SetChecked (ById $win "BrowserBox") $false              # no browser registry entry from a test
    Press (ById $win "InstallButton")
    $done = $null
    for ($i = 0; $i -lt 240 -and -not $done; $i++) {
        Start-Sleep -Milliseconds 500
        $t = ById $win "DoneText"
        if ($t -and $t.Current.Name -and -not $t.Current.IsOffscreen) { $done = $t.Current.Name }
    }
    Check "it ends on the 'installed' page" ($done -like "*is installed*") "($done)"
    Shot $win "installer-gui-1-done"
    foreach ($f in "ShadeNetDownloader.exe", "tools\yt-dlp\yt-dlp.exe", "tools\ffmpeg\ffmpeg.exe", "Uninstall.exe", "install.json") { Check $f (Test-Path (Join-Path $inst $f)) }
    Check "Start menu shortcut" (Test-Path (Join-Path $sm "ShadeNet Downloader.lnk"))
    Check "desktop shortcut" (Test-Path (Join-Path $dt "ShadeNet Downloader.lnk"))
    $key = "HKCU:\$regRoot\Microsoft\Windows\CurrentVersion\Uninstall\ShadeNetDownloader"
    Check "Apps & features entry" ((Get-ItemProperty $key -ErrorAction SilentlyContinue).DisplayName -eq "ShadeNet Downloader")
    SetChecked (ById $win "LaunchBox") $false               # do not start the installed program
    Press (ById $win "CloseButton")
    Check "the window closes and the program ends" ($p.WaitForExit(10000))

    Write-Host "`n3. starting it again offers an update"
    $p2 = Start-Process $Installer -ArgumentList $common -PassThru; $started += $p2
    $win2 = WaitWindow $p2.Id "ShadeNet Downloader Setup"
    Check "the window opens" ([bool]$win2)
    if ($win2) {
        Start-Sleep -Milliseconds 500
        Shot $win2 "installer-gui-2-update"
        Check "it says Update, and that data is kept" ((Texts $win2) -contains "Update ShadeNet Downloader") ((Texts $win2 | Where-Object { $_ -like "Version*installed*" }) -join "")
        PressNamed $win2 "Cancel"
        Check "Cancel closes it without changing anything" ($p2.WaitForExit(10000))
    }

    Write-Host "`n4. the uninstaller, with its two questions"
    $remove = (Get-ItemProperty $key).UninstallString
    Write-Host "  $remove"
    $u = Start-Process (Join-Path $inst "Uninstall.exe") -ArgumentList ($common + @("--uninstall")) -PassThru; $started += $u
    $q1 = WaitWindow $u.Id "Uninstall ShadeNet Downloader"
    Check "the first question appears" ([bool]$q1) ((Texts $q1) -join " ")
    if ($q1) { Shot $q1 "installer-gui-3-uninstall-question"; EnterIn $q1 $u.Id }
    Start-Sleep -Milliseconds 800
    $q2 = WaitWindow $u.Id "Uninstall ShadeNet Downloader"
    Check "the second question (delete the data too?) appears" ([bool]$q2) ((Texts $q2) -join " ")
    if ($q2) { EnterIn $q2 $u.Id }
    # the second stage (a copy of the uninstaller in the temp folder) removes everything, then says so and waits for OK
    $stage2 = $null
    for ($i = 0; $i -lt 80 -and -not $stage2; $i++) {
        Start-Sleep -Milliseconds 500
        $stage2 = Get-CimInstance Win32_Process -Filter "Name like 'ShadeNetUninstall-%'" | Where-Object { $_.CommandLine -like "*$work*" } | Select-Object -First 1
    }
    Check "the temporary uninstaller runs" ([bool]$stage2)
    if ($stage2) {
        $q3 = WaitWindow $stage2.ProcessId "Uninstall ShadeNet Downloader" 60
        Check "it ends with 'was removed'" ([bool]$q3) ((Texts $q3) -join " ")
        if ($q3) { Shot $q3 "installer-gui-4-removed"; EnterIn $q3 $stage2.ProcessId }
        $gone = $false
        for ($i = 0; $i -lt 40 -and -not $gone; $i++) { Start-Sleep -Milliseconds 500; $gone = -not (Get-Process -Id $stage2.ProcessId -ErrorAction SilentlyContinue) }
        Check "and the temporary uninstaller ends" $gone
        Start-Sleep -Seconds 5
        Check "its file in the temp folder is deleted" (-not (Test-Path $stage2.ExecutablePath))
    }
    Check "the program folder is gone" (-not (Test-Path $inst))
    Check "the Apps & features entry is gone" (-not (Test-Path $key))
    Check "shortcuts are gone" (-not (Test-Path (Join-Path $sm "ShadeNet Downloader.lnk")))
    Check "the user's data is still there (answered No)" (Test-Path (Join-Path $data "settings.json"))
}
catch {
    Write-Host "  error: $($_.Exception.Message)" -ForegroundColor Red
    $ok = $false
}
finally {
    foreach ($s in $started) { if ($s -and -not $s.HasExited) { try { $s.Kill() } catch { } } }
    # a temporary uninstaller of THIS run that is still waiting (the test failed before it): end it and remove its copy
    Get-CimInstance Win32_Process -Filter "Name like 'ShadeNetUninstall-%'" | Where-Object { $_.CommandLine -like "*$work*" } | ForEach-Object {
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500; Remove-Item $_.ExecutablePath -Force -ErrorAction SilentlyContinue }
    Get-Process ShadeNetDownloader, ShadeNetSetup -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($work, [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { $_.Kill() }
    Remove-Item "HKCU:\$regRoot" -Recurse -ErrorAction SilentlyContinue
    if ((Get-ChildItem "HKCU:\Software\ShadeNetTests" -ErrorAction SilentlyContinue).Count -eq 0) { Remove-Item "HKCU:\Software\ShadeNetTests" -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host ""
if ($ok) { Write-Host "PASS" -ForegroundColor Green; exit 0 } else { Write-Host "FAIL" -ForegroundColor Red; exit 1 }
