# Builds dist\SuperScreenshot.exe: one self-contained file that runs on any 64-bit
# Windows 10 (2004 or later) or Windows 11 PC, with no .NET install needed.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet test --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not publishing.' }

dotnet publish src\SuperScreenshot\SuperScreenshot.csproj --nologo -c Release -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o dist
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Get-Item dist\SuperScreenshot.exe | Select-Object FullName, @{ n = 'MB'; e = { [Math]::Round($_.Length / 1MB, 1) } }
