# Builds SuperScreenshot for release.
#
#   .\publish.ps1                          tests, then dist\SuperScreenshot.exe (one self-contained file, no .NET needed)
#   .\publish.ps1 -Installer               ...and dist\installer\SuperScreenshot-Setup-<version>-x64.exe
#   .\publish.ps1 -Installer -TestInstaller ...then installs it silently, checks it, and uninstalls it
#   -Version 1.2.3 overrides the version in SuperScreenshot.csproj (CI passes the tag).
#
# Needs the .NET 9 SDK; -Installer needs Inno Setup 6 (installed automatically on GitHub Actions).
param([string]$Version, [switch]$Installer, [switch]$TestInstaller)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $Version) {
    $Version = ([xml](Get-Content src\SuperScreenshot\SuperScreenshot.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
Write-Host "SuperScreenshot $Version"

dotnet test --nologo -c Release
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not publishing.' }

dotnet publish src\SuperScreenshot\SuperScreenshot.csproj --nologo -c Release -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:Version=$Version `
    -o dist
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Get-Item dist\SuperScreenshot.exe | Select-Object FullName, @{ n = 'MB'; e = { [Math]::Round($_.Length / 1MB, 1) } }

if ($Installer -or $TestInstaller) {
    $iscc = (Get-Command ISCC.exe -ErrorAction Ignore).Source
    foreach ($candidate in "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") {
        if (-not $iscc -and (Test-Path $candidate)) { $iscc = $candidate }
    }
    if (-not $iscc -and $env:GITHUB_ACTIONS) {
        choco install innosetup -y --no-progress | Out-Null
        $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    }
    if (-not $iscc -or -not (Test-Path $iscc)) { throw 'Inno Setup 6 not found: winget install --id JRSoftware.InnoSetup -e --scope user' }

    & $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$PSScriptRoot\dist" "/DOutputDir=$PSScriptRoot\dist\installer" packaging\windows\superscreenshot.iss
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
    $setup = "dist\installer\SuperScreenshot-Setup-$Version-x64.exe"
    Get-Item $setup | Select-Object FullName, @{ n = 'MB'; e = { [Math]::Round($_.Length / 1MB, 1) } }

    if ($TestInstaller) {
        pwsh -NoProfile -File tests\installer-test.ps1 -Setup $setup -Version $Version
        if ($LASTEXITCODE -ne 0) { throw 'Installer test failed.' }
    }
}
