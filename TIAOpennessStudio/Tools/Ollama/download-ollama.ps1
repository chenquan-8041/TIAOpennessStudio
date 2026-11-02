<#
Downloads the standalone Ollama runtime for Windows into this folder.

Usage:
  powershell -ExecutionPolicy Bypass -File .\download-ollama.ps1
  powershell -ExecutionPolicy Bypass -File .\download-ollama.ps1 -Force

After the download finishes, pull the recommended model, e.g.:
  .\ollama.exe pull LisyNeko/qwen3.8-9b-coder

The app looks for .\ollama.exe first, then a normal Ollama installation,
then ollama.exe on PATH.
#>
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$target = Join-Path $PSScriptRoot 'ollama.exe'
$url = 'https://ollama.com/download/ollama-windows-amd64.zip'
$zip = Join-Path $env:TEMP 'ollama-windows-amd64.zip'

if ((Test-Path $target) -and (-not $Force)) {
    Write-Host "ollama.exe already exists at $target (use -Force to download again)."
    exit 0
}

Write-Host "Downloading $url ..."
Write-Host "The archive is about 1.6 GB, so this can take a while."
Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing

Write-Host "Extracting to $PSScriptRoot ..."
Expand-Archive -Path $zip -DestinationPath $PSScriptRoot -Force
Remove-Item $zip -Force

if (-not (Test-Path $target)) {
    Write-Host "ERROR: ollama.exe was not found after extraction." -ForegroundColor Red
    exit 1
}

Write-Host "Done: $target" -ForegroundColor Green
Write-Host ""
Write-Host "Next step - pull a model, for example:"
Write-Host "  & '$target' pull LisyNeko/qwen3.8-9b-coder"
Write-Host ""
Write-Host "The app starts 'ollama serve' automatically when 127.0.0.1:11434 is not listening."
