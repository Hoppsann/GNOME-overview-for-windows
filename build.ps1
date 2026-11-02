# Builds dist/Overview-Setup.exe and dist/Overview-standalone.exe.
# Both are the same self-contained program; the "Setup" in the name makes it run as the installer.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

dotnet publish src/Overview -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:PublishReadyToRun=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o publish

New-Item -ItemType Directory -Force dist | Out-Null
Copy-Item publish/Overview.exe dist/Overview-Setup.exe -Force
Copy-Item publish/Overview.exe dist/Overview-standalone.exe -Force
Write-Host "Done: dist/Overview-Setup.exe and dist/Overview-standalone.exe"
