param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$projectRoot = $PSScriptRoot
if (-not (Test-Path (Join-Path $projectRoot "DecklinkSwitcher.csproj"))) {
    $projectRoot = "D:\___newp\DecklinkSwitcher"
}
$projectFile = Join-Path $projectRoot "DecklinkSwitcher.csproj"
$binDir = Join-Path $projectRoot "bin\$Configuration\net10.0-windows\win-x64"

# Close any running instances so build and cleanup don't hit file lock errors
$runningProcesses = Get-Process -Name "DecklinkSwitcher*" -ErrorAction SilentlyContinue
if ($runningProcesses) {
    foreach ($proc in $runningProcesses) {
        Write-Host "Stopping running instance: $($proc.ProcessName) (PID: $($proc.Id))..." -ForegroundColor Yellow
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 500
}

Write-Host "Building DecklinkSwitcher ($Configuration)..." -ForegroundColor Cyan
& dotnet build $projectFile -c $Configuration

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}

$origExe = Join-Path $binDir "DecklinkSwitcher.exe"
if (Test-Path $origExe) {
    $timestamp = Get-Date -Format "dd-MM-yyyy_HH-mm-ss"
    $destExe = Join-Path $binDir "DecklinkSwitcher_$timestamp.exe"
    
    # Remove previous timestamped executables
    $oldExes = Get-ChildItem -Path $binDir -Filter "DecklinkSwitcher_*.exe" -ErrorAction SilentlyContinue
    foreach ($old in $oldExes) {
        try {
            Remove-Item -Path $old.FullName -Force -ErrorAction Stop
            Write-Host "Deleted old executable: $($old.Name)" -ForegroundColor Yellow
        }
        catch {
            Write-Warning "Could not delete old executable '$($old.Name)': $($_.Exception.Message)"
        }
    }

    Copy-Item -Path $origExe -Destination $destExe -Force
    Write-Host "Created: DecklinkSwitcher_$timestamp.exe" -ForegroundColor Green
    
    Remove-Item -Path $origExe -Force
    Write-Host "Deleted original DecklinkSwitcher.exe" -ForegroundColor Yellow

    Write-Host "`nBuild complete. Latest executable:" -ForegroundColor Cyan
    Write-Host "  $destExe" -ForegroundColor White
}
