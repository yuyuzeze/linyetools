<#
.SYNOPSIS
  One-time Python environment setup for the KikuCaption Whisper worker (Milestone 7, delivery
  approach A). Creates a venv and installs the LOCKED dependencies. The small model is downloaded on
  first run by the app (with an integrity check), not here.

.NOTES
  Requires Python 3.13.x on PATH (verified compatible; see docs/Verification.md). This script does
  not require admin rights and installs into python/whisper_worker/.venv (user-writable).
#>
param(
  [string]$Python = "python"
)

$ErrorActionPreference = "Stop"
$worker = Resolve-Path "$PSScriptRoot/../python/whisper_worker"
$venv = Join-Path $worker ".venv"

Write-Host "Checking Python ..."
& $Python --version
if ($LASTEXITCODE -ne 0) { throw "Python not found on PATH. Install Python 3.13.x first." }

if (-not (Test-Path $venv)) {
  Write-Host "Creating venv at $venv ..."
  & $Python -m venv $venv
}

$venvPy = Join-Path $venv "Scripts/python.exe"
Write-Host "Installing locked dependencies ..."
& $venvPy -m pip install --upgrade pip
& $venvPy -m pip install -r (Join-Path $worker "requirements-lock.txt")

Write-Host "Done. The app will download the faster-whisper 'small' model on first run (network required once)."
