# Builds a single-file, self-contained Windows .exe in .\publish\
# Usage:   pwsh -File .\publish.ps1     (or right-click > Run with PowerShell)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if (Test-Path .\publish) { Remove-Item .\publish -Recurse -Force }
    dotnet publish .\sketchup_mimic.csproj -c Release
    Write-Host ""
    Write-Host "Done. Run: .\publish\sketchup_mimic.exe" -ForegroundColor Green
} finally {
    Pop-Location
}
