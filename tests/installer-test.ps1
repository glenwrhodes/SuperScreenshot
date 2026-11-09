# Installs the SuperScreenshot installer silently for the current user, checks what it set up,
# then uninstalls it and checks that everything is gone again. Used by publish.ps1 -TestInstaller and CI.
#
#   pwsh -File tests\installer-test.ps1 -Setup dist\installer\SuperScreenshot-Setup-1.0.0-x64.exe -Version 1.0.0
#   ...add -Keep to leave it installed (skips the uninstall half).
#
# Note: uninstalling runs "SuperScreenshot --exit", which closes any running copy of SuperScreenshot.
param([Parameter(Mandatory)][string]$Setup, [Parameter(Mandatory)][string]$Version, [switch]$Keep)
$ErrorActionPreference = 'Stop'

$failures = 0
function Check([string]$what, [bool]$ok) {
    if ($ok) { Write-Host "  ok    $what" } else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:failures++ }
}

$dir = Join-Path $env:LOCALAPPDATA 'Programs\SuperScreenshot'
$exe = Join-Path $dir 'SuperScreenshot.exe'
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\SuperScreenshot.lnk'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{237197A2-6051-4E27-A935-58CCFA768B76}_is1'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$log = Join-Path (Split-Path (Resolve-Path $Setup)) 'install.log'
function RunEntry { (Get-ItemProperty $runKey -Name SuperScreenshot -ErrorAction Ignore).SuperScreenshot }

Write-Host "Installing $Setup"
$p = Start-Process $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/LOG=`"$log`"" -Wait -PassThru
Check "setup exit code 0 (got $($p.ExitCode))" ($p.ExitCode -eq 0)
Check "SuperScreenshot.exe in $dir" (Test-Path $exe)
if (Test-Path $exe) {
    Check "exe version is $Version" ((Get-Item $exe).VersionInfo.ProductVersion -like "$Version*")
}
Check 'Start menu shortcut' (Test-Path $shortcut)
if (Test-Path $shortcut) {
    $lnk = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
    Check 'shortcut points at SuperScreenshot.exe' ($lnk.TargetPath -eq $exe)
}
$entry = Get-ItemProperty $uninstallKey -ErrorAction Ignore
Check 'Installed apps entry' ($null -ne $entry -and $entry.DisplayName -eq 'SuperScreenshot' -and $entry.DisplayVersion -eq $Version)

# Start it in the tray like Windows would at sign-in, and check it registers itself to start with Windows.
$app = Start-Process $exe -ArgumentList '--tray' -PassThru
$deadline = (Get-Date).AddSeconds(20)
while (-not ((RunEntry) -like "*$exe*") -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
Check 'app starts and adds itself to start with Windows' ((RunEntry) -like "*$exe*")
Check 'app still running in the tray' (-not $app.HasExited)

if (-not $Keep) {
    Write-Host 'Uninstalling'
    $uninstaller = Join-Path $dir 'unins000.exe'
    Check 'uninstaller present' (Test-Path $uninstaller)
    if (Test-Path $uninstaller) {
        Start-Process $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
        # The uninstaller hands over to a copy of itself in %TEMP%; wait for that to finish too.
        $deadline = (Get-Date).AddSeconds(60)
        while ((Test-Path $uninstallKey) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    }
    $app.Refresh()
    Check 'running app was closed' ($app.HasExited)
    Check 'SuperScreenshot.exe removed' (-not (Test-Path $exe))
    Check 'shortcut removed' (-not (Test-Path $shortcut))
    Check 'Installed apps entry removed' (-not (Test-Path $uninstallKey))
    Check 'start-with-Windows entry removed' ($null -eq (RunEntry))
    Check 'settings removed' (-not (Test-Path 'HKCU:\Software\SuperScreenshot'))
}

if ($failures) { Write-Host "$failures installer check(s) failed. Log: $log" -ForegroundColor Red; exit 1 }
Write-Host 'Installer checks passed.'
