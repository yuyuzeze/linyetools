<#
.SYNOPSIS
  FALLBACK / admin-diagnostic setup for the KikuCaption speech-recognition Python environment.

.DESCRIPTION
  The normal way to install is the in-app one-click "Install speech recognition environment" on the
  Environment page (R7C). This script exists only for when the UI cannot start, or for a headless /
  admin diagnostic. It follows the SAME rules as the app installer so the two never diverge:
    - target venv     : %LOCALAPPDATA%\KikuCaption\python\venv (user-writable; survives app updates)
    - dependencies    : python/whisper_worker/requirements-lock.txt (pinned)
    - supported Python : 64-bit CPython 3.12 or 3.13
    - verification     : import faster_whisper, ctranslate2, av, numpy
  It does not require admin rights and never installs system Python.

.PARAMETER Python
  The system Python to build the venv from (default: "py" launcher, else "python").

.PARAMETER VenvTarget
  Override the venv target directory (default: the managed %LOCALAPPDATA% path above).

.PARAMETER LockName
  Override the cross-process install lock name (default: the same per-user name the app computes). The
  app and this script share ONE named mutex so the two never replace the managed venv at the same time.
#>
param(
  [string]$Python = "",
  [string]$VenvTarget = "",
  [string]$LockName = ""
)

$ErrorActionPreference = "Stop"
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }  # stable UTF-8 output (never parse for mojibake)

# --- Concurrency safety (R7C.1) -------------------------------------------------------------------
# Refuse to run while KikuCaption is installing/using the managed venv. PRIMARY guard: the SAME named
# mutex the app holds during an install (per-user, Local\ namespace, auto-released by the OS on crash).
# SECONDARY guard: a running KikuCaption process. Either one → refuse and ask the user to close the app.
function Get-InstallLockName {
  if (-not [string]::IsNullOrWhiteSpace($LockName)) { return $LockName }
  $sha = [System.Security.Cryptography.SHA256]::Create()
  try {
    $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($env:USERNAME))
    $hex = ([BitConverter]::ToString($bytes) -replace '-', '').Substring(0, 16)
    return "Local\KikuCaption.PythonEnvironmentInstall.$hex"
  } finally { $sha.Dispose() }
}

if (Get-Process -Name "KikuCaption" -ErrorAction SilentlyContinue) {
  Write-Error "KikuCaption is running. Please close it before running this script (it manages the same Python environment)."
  exit 2
}

$installMutex = New-Object System.Threading.Mutex($false, (Get-InstallLockName))
$haveLock = $false
try { $haveLock = $installMutex.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $haveLock = $true }
if (-not $haveLock) {
  Write-Error "Another KikuCaption Python install is already in progress. Please wait for it to finish, or close KikuCaption."
  $installMutex.Dispose()
  exit 2
}

try {

# Locate requirements-lock.txt (release: beside the exe; repo: ../python/whisper_worker).
$releaseWorker = Join-Path $PSScriptRoot "python/whisper_worker"
$repositoryWorker = Join-Path $PSScriptRoot "../python/whisper_worker"
$worker = if (Test-Path (Join-Path $releaseWorker "requirements-lock.txt")) { $releaseWorker }
          elseif (Test-Path (Join-Path $repositoryWorker "requirements-lock.txt")) { $repositoryWorker }
          else { throw "Cannot find python/whisper_worker/requirements-lock.txt. Run this from the extracted KikuCaption folder." }
$worker = (Resolve-Path $worker).Path
$lock = Join-Path $worker "requirements-lock.txt"

if ([string]::IsNullOrWhiteSpace($VenvTarget)) {
  $VenvTarget = Join-Path $env:LOCALAPPDATA "KikuCaption/python/venv"
}

# Choose a system Python: prefer the py launcher for a supported version, else the given/PATH python.
function Resolve-SystemPython {
  if (-not [string]::IsNullOrWhiteSpace($Python)) { return $Python }
  foreach ($v in @("-3.13", "-3.12")) {
    try { $exe = & py $v -c "import sys;print(sys.executable)" 2>$null; if ($LASTEXITCODE -eq 0 -and $exe) { return $exe.Trim() } } catch { }
  }
  return "python"
}

$sysPython = Resolve-SystemPython
Write-Host "Using system Python: checking version ..."
$facts = & $sysPython -c "import json,sys;print(json.dumps({'ma':sys.version_info[0],'mi':sys.version_info[1],'x64':sys.maxsize>2**32}))" 2>$null
if ($LASTEXITCODE -ne 0 -or -not $facts) { throw "No usable Python found. Install 64-bit CPython 3.12 or 3.13 first." }
$info = $facts | ConvertFrom-Json
if (-not ($info.ma -eq 3 -and ($info.mi -eq 12 -or $info.mi -eq 13) -and $info.x64)) {
  throw "Unsupported Python $($info.ma).$($info.mi) (x64=$($info.x64)). Requires 64-bit CPython 3.12 or 3.13."
}

# Build into a staging dir, then swap in — mirrors the app installer's atomic behavior.
$root = Split-Path -Parent $VenvTarget
New-Item -ItemType Directory -Force -Path $root | Out-Null
$staging = Join-Path $root (".venv-staging-" + [Guid]::NewGuid().ToString("N"))

Write-Host "Creating venv (staging) ..."
& $sysPython -m venv $staging
if ($LASTEXITCODE -ne 0) { throw "Failed to create the virtual environment." }
$venvPy = Join-Path $staging "Scripts/python.exe"

Write-Host "Installing locked dependencies ..."
& $venvPy -m pip install --disable-pip-version-check --no-input -r $lock
if ($LASTEXITCODE -ne 0) { Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue; throw "Failed to install dependencies." }

Write-Host "Verifying imports ..."
& $venvPy -c "import faster_whisper, ctranslate2, av, numpy; print('imports OK')"
if ($LASTEXITCODE -ne 0) { Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue; throw "Dependency verification failed." }

# Atomic-ish swap: back up any existing venv, move staging in, restore on failure.
$backup = $null
if (Test-Path $VenvTarget) { $backup = Join-Path $root (".venv-backup-" + [Guid]::NewGuid().ToString("N")); Move-Item $VenvTarget $backup }
try { Move-Item $staging $VenvTarget }
catch { if ($backup) { Move-Item $backup $VenvTarget }; throw }
if ($backup) { Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host "Done. Managed venv is at $VenvTarget. Restart KikuCaption and re-check the Environment page."
}
finally {
  # Release the shared install lock on this (the acquiring) thread, then dispose it.
  if ($haveLock) { try { $installMutex.ReleaseMutex() } catch { } }
  $installMutex.Dispose()
}
