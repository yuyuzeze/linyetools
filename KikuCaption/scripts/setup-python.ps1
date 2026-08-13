<#
.SYNOPSIS
  One-time Python environment setup for the KikuCaption Whisper worker. Creates a venv and installs
  the locked dependencies. It can be run from the repository's scripts directory or directly from
  the root of an extracted KikuCaption release.

.NOTES
  Requires 64-bit Python 3.12 or 3.13 on PATH. This script does
  not require admin rights and installs into python/whisper_worker/.venv (user-writable).
#>
param(
  [string]$Python = "python"
)

$ErrorActionPreference = "Stop"

# In a release this script is copied beside KikuCaption.exe. In the source repository it lives in
# scripts/. Supporting both locations keeps one setup script and prevents release-only path bugs.
$releaseWorker = Join-Path $PSScriptRoot "python/whisper_worker"
$repositoryWorker = Join-Path $PSScriptRoot "../python/whisper_worker"
$workerCandidate = if (Test-Path (Join-Path $releaseWorker "requirements-lock.txt")) {
  $releaseWorker
} elseif (Test-Path (Join-Path $repositoryWorker "requirements-lock.txt")) {
  $repositoryWorker
} else {
  throw "Cannot find python/whisper_worker/requirements-lock.txt. Run this script from the extracted KikuCaption folder."
}

$worker = Resolve-Path $workerCandidate
$venv = Join-Path $worker ".venv"

Write-Host "Checking Python ..."
& $Python --version
if ($LASTEXITCODE -ne 0) { throw "Python not found on PATH. Install Python 3.12 or 3.13 (64-bit) first." }

if (-not (Test-Path $venv)) {
  Write-Host "Creating venv at $venv ..."
  & $Python -m venv $venv
  if ($LASTEXITCODE -ne 0) { throw "Failed to create the Python virtual environment." }
}

$venvPy = Join-Path $venv "Scripts/python.exe"
Write-Host "Installing locked dependencies ..."
& $venvPy -m pip install --upgrade pip
if ($LASTEXITCODE -ne 0) { throw "Failed to update pip in the virtual environment." }
& $venvPy -m pip install -r (Join-Path $worker "requirements-lock.txt")
if ($LASTEXITCODE -ne 0) { throw "Failed to install the Whisper worker dependencies." }

Write-Host "Done. Restart KikuCaption and run the environment check again."
