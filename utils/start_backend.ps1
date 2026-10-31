# Set Execution Policy (silently)
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force

# Set working directory (script is in utils/, repo root is one level up)
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot

# Define paths
$embeddedPython = Join-Path $repoRoot "TIAAgent\python\python.exe"
$mainPath = Join-Path $repoRoot "TIAAgent\run.py"

# Quick existence check
if (-not (Test-Path $embeddedPython)) {
    Write-Host "Error: Embedded Python not found at $embeddedPython" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $mainPath)) {
    Write-Host "Error: TIAAgent\run.py not found." -ForegroundColor Red
    exit 1
}

# Start backend directly (C# side handles health polling)
Write-Host "Starting Python Backend..." -ForegroundColor Green
& $embeddedPython $mainPath
