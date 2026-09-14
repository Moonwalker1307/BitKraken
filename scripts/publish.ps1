# Publishes self-contained, single-file builds of BitKraken for every desktop platform.
# Usage: .\scripts\publish.ps1 [-Rids win-x64,linux-x64,...]
param([string[]]$Rids = @("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"))

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

foreach ($rid in $Rids) {
    Write-Host "==> Publishing $rid"
    dotnet publish src/BitKraken/BitKraken.csproj `
        -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none `
        -o "publish/$rid"
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }
}
Write-Host "Done. Output in ./publish/<rid>/"
