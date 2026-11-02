param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$projectRoot = "D:\_projects\decklinkswitcher"
$projectFile = Join-Path $projectRoot "DecklinkSwitcher.csproj"
$binDir = Join-Path $projectRoot "bin\$Configuration\net10.0-windows\win-x64"

Write-Host "Building DecklinkSwitcher ($Configuration)..." -ForegroundColor Cyan
& dotnet build $projectFile -c $Configuration

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}

$origExe = Join-Path $binDir "DecklinkSwitcher.exe"
if (Test-Path $origExe) {
    $timestamp = Get-Date -Format "yyyy-MM-dd_HH-mm-ss"
    $destExe = Join-Path $binDir "DecklinkSwitcher_$timestamp.exe"
    
    Copy-Item -Path $origExe -Destination $destExe -Force
    Write-Host "Created: DecklinkSwitcher_$timestamp.exe" -ForegroundColor Green
    
    Remove-Item -Path $origExe -Force
    Write-Host "Deleted original DecklinkSwitcher.exe" -ForegroundColor Yellow

    Write-Host "`nBuild complete. Latest executable:" -ForegroundColor Cyan
    Write-Host "  $destExe" -ForegroundColor White
}
