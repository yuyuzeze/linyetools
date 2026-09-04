<#
.SYNOPSIS
  Builds a KikuCaption portable release (Milestone 7, delivery approach A: self-contained .NET +
  scripted Python). Produces a portable folder + zip + SHA-256, excluding all user/secret/dev data.

.PARAMETER Edition
  Full          — the complete app. Speech recognition defaults ON at first launch.
  RecordingOnly — first-launch default is recording only (screen + system audio + optional mic).
                  It EXCLUDES only the large runtime assets that neither edition bundles anyway — the
                  Python venv, the Whisper small/medium models, and any Hugging Face cache — but KEEPS
                  the small, upgrade-enabling payload: the python/whisper_worker source,
                  requirements*.txt, setup-python.ps1, and the speech-enable instructions. A user can
                  later run setup-python.ps1, install the model, and turn recognition on in Settings.

.NOTES
  Approach A never bundles the Python runtime or the Whisper model itself — both editions ship a
  root-level setup-python.ps1 that creates a local venv after extraction. Neither edition ships
  secrets, user settings, meetings, logs, caches, a venv, models, or DPAPI ciphertext. The only
  packaged difference is the first-launch speech default (edition.txt).
#>
param(
  [ValidateSet("Full", "RecordingOnly")]
  [string]$Edition = "Full",
  [string]$Configuration = "Release",
  [string]$Runtime = "win-x64",
  [string]$OutputRoot = "$PSScriptRoot/../publish"
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path "$PSScriptRoot/.."
$version = "0.1.0"
$recordingOnly = $Edition -eq "RecordingOnly"
$editionTag = if ($recordingOnly) { "recordingonly" } else { "full" }
$stage = Join-Path $OutputRoot "KikuCaption-$version-$editionTag-$Runtime"

Write-Host "Publishing $Edition edition, self-contained $Runtime ($Configuration) ..."
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# Self-contained .NET app (no trimming: WPF + reflection). PDBs excluded from the user package.
dotnet publish "$repo/src/KikuCaption.App/KikuCaption.App.csproj" `
  -c $Configuration -r $Runtime --self-contained true `
  -p:DebugType=None -p:DebugSymbols=false `
  -o $stage

# Edition marker: read on first launch to pick the speech-recognition default (RecordingOnly = off).
Set-Content -Path (Join-Path $stage "edition.txt") -Value $Edition -Encoding ascii

# Bundle FFmpeg (both editions record). GPL v3 — see THIRD_PARTY_NOTICES.md.
$ffmpeg = Join-Path $repo "tools/ffmpeg"
if (Test-Path (Join-Path $ffmpeg "ffmpeg.exe")) {
  New-Item -ItemType Directory -Force -Path (Join-Path $stage "tools/ffmpeg") | Out-Null
  Copy-Item (Join-Path $ffmpeg "ffmpeg.exe")  (Join-Path $stage "tools/ffmpeg") -Force
  Copy-Item (Join-Path $ffmpeg "ffprobe.exe") (Join-Path $stage "tools/ffmpeg") -Force -ErrorAction SilentlyContinue
  Write-Host "Bundled FFmpeg (GPL v3)."
} else {
  Write-Host "FFmpeg not found under tools/ffmpeg — user must configure Recording:FFmpegPath."
}

# Docs + notices (both editions).
Copy-Item (Join-Path $repo "README.md") $stage -Force
Copy-Item (Join-Path $repo "THIRD_PARTY_NOTICES.md") $stage -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path $stage "docs") | Out-Null
Copy-Item (Join-Path $repo "docs/UserGuide.md") (Join-Path $stage "docs") -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $repo "docs/Delivery.md")  (Join-Path $stage "docs") -Force -ErrorAction SilentlyContinue
if (Test-Path (Join-Path $repo "licenses")) { Copy-Item (Join-Path $repo "licenses") $stage -Recurse -Force }

# Upgrade-enabling speech payload (BOTH editions): the small Whisper worker source + requirements +
# the venv bootstrap script. These are tiny and let a RecordingOnly user enable recognition later.
# The heavy assets (venv, model.bin, HF cache) are never copied here and are also scrubbed below.
Copy-Item (Join-Path $repo "scripts/setup-python.ps1") (Join-Path $stage "setup-python.ps1") -Force
if (Test-Path (Join-Path $repo "python/whisper_worker")) {
  New-Item -ItemType Directory -Force -Path (Join-Path $stage "python/whisper_worker") | Out-Null
  Get-ChildItem (Join-Path $repo "python/whisper_worker") -File | Where-Object { $_.Extension -in ".py",".txt" } |
    Copy-Item -Destination (Join-Path $stage "python/whisper_worker") -Force
}

if ($recordingOnly) {
  # A short, packaged note so a RecordingOnly user knows how to turn on speech recognition later.
  $note = @"
# 启用本地语音识别 / Enable local speech recognition / ローカル音声認識を有効にする

本安装包默认只录屏录音（不含 Python venv 与 Whisper 模型）。要启用实时字幕：
1. 运行 setup-python.ps1 创建本地 Python 环境（安装 python/whisper_worker/requirements.txt）。
2. 按 docs 中的说明下载并放置 Whisper 模型（small；如需会后校正再放 medium）。
3. 在“设置 → 常规”中勾选“启用本地语音识别”。依赖不完整时字幕会议不会启动，环境页会指出缺少的组件。

This RecordingOnly build ships recording only. Run setup-python.ps1, install the Whisper model,
then enable local speech recognition in Settings → General. Captioned meetings will not start until
the dependencies are complete; the Environment page shows exactly what is missing.
"@
  Set-Content -Path (Join-Path $stage "ENABLE-SPEECH.md") -Value $note -Encoding utf8
  Write-Host "RecordingOnly: kept worker source + setup-python.ps1; added ENABLE-SPEECH.md."
}

# Hard exclusions: never ship secrets, user data, logs, caches, venv, models, symbols. The model /
# venv / python globs are also what a RecordingOnly package must not contain.
$excludeGlobs = @("*.pdb","secrets","settings.json","Meetings","logs",".venv","venv","models","*.key",".huggingface","huggingface","__pycache__","*.corrupt-*.bak")
foreach ($g in $excludeGlobs) {
  Get-ChildItem $stage -Recurse -Force -Filter $g -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

# Zip + SHA-256.
$zip = "$stage.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage/*" -DestinationPath $zip
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash
Set-Content -Path "$zip.sha256" -Value "$sha  $(Split-Path $zip -Leaf)" -Encoding ascii

# --- Actual size report (measured, never estimated) --------------------------------------------
function Measure-DirMb([string]$path) {
  if (-not (Test-Path $path)) { return 0.0 }
  [math]::Round(((Get-ChildItem $path -Recurse -File -Force -ErrorAction SilentlyContinue |
    Measure-Object Length -Sum).Sum / 1MB), 1)
}
function Measure-FileMb([string]$path) {
  if (-not (Test-Path $path)) { return 0.0 }
  [math]::Round(((Get-Item $path).Length / 1MB), 2)
}

$publishMb = Measure-DirMb $stage
$zipMb     = Measure-FileMb $zip
$ffmpegMb  = Measure-FileMb (Join-Path $stage "tools/ffmpeg/ffmpeg.exe")
$ffprobeMb = Measure-FileMb (Join-Path $stage "tools/ffmpeg/ffprobe.exe")
# .NET self-contained runtime footprint: coreclr + the host/framework/WPF native+managed set. Report
# the aggregate of every DLL (this is dominated by the .NET + WPF runtime) plus coreclr on its own.
$coreclrMb = Measure-FileMb (Join-Path $stage "coreclr.dll")
$libvlcMb  = Measure-DirMb  (Join-Path $stage "libvlc")
if ($libvlcMb -eq 0.0) { $libvlcMb = Measure-FileMb (Join-Path $stage "libvlc.dll") }
# LibVLC also ships plugins + libvlccore next to the exe; include any libvlc*.dll in the aggregate.
$libvlcMb += [math]::Round(((Get-ChildItem $stage -Recurse -File -Filter "libvlc*.dll" -ErrorAction SilentlyContinue |
  Measure-Object Length -Sum).Sum / 1MB), 1)
$dllCount  = (Get-ChildItem $stage -Recurse -File -Filter *.dll -ErrorAction SilentlyContinue | Measure-Object).Count
$dllTotMb  = [math]::Round(((Get-ChildItem $stage -Recurse -File -Filter *.dll -ErrorAction SilentlyContinue |
  Measure-Object Length -Sum).Sum / 1MB), 1)
$workerMb  = Measure-DirMb (Join-Path $stage "python/whisper_worker")
$venvMb    = Measure-DirMb (Join-Path $stage ".venv")
$modelMb   = Measure-DirMb (Join-Path $stage "models")

Write-Host ""
Write-Host "==== $Edition edition — measured sizes ===="
Write-Host ("Publish folder    : {0} ({1} MB)" -f $stage, $publishMb)
Write-Host ("Zip               : {0} ({1} MB)" -f $zip, $zipMb)
Write-Host ("Zip SHA-256       : {0}" -f $sha)
Write-Host ("coreclr.dll       : {0} MB" -f $coreclrMb)
Write-Host ("All DLLs (.NET+WPF+deps runtime): {0} files, {1} MB" -f $dllCount, $dllTotMb)
Write-Host ("ffmpeg.exe        : {0} MB" -f $ffmpegMb)
Write-Host ("ffprobe.exe       : {0} MB" -f $ffprobeMb)
Write-Host ("LibVLC            : {0} MB" -f $libvlcMb)
Write-Host ("Python worker src : {0} MB" -f $workerMb)
Write-Host ("Python venv       : {0} MB (must be 0)" -f $venvMb)
Write-Host ("Whisper models    : {0} MB (must be 0)" -f $modelMb)
if ($publishMb -gt 10240) {
  Write-Warning "Publish folder exceeds 10 GB — investigate before distributing."
} else {
  Write-Host ("Under 10 GB       : yes ({0} MB)" -f $publishMb)
}

# --- Content scan: prove the package's inclusions/exclusions (never estimated) ------------------
function HasAny([string]$pattern) {
  [bool](Get-ChildItem $stage -Recurse -File -Force -Filter $pattern -ErrorAction SilentlyContinue | Select-Object -First 1)
}
$hasVenv    = (Test-Path (Join-Path $stage ".venv")) -or (Test-Path (Join-Path $stage "venv"))
$hasModelBin= HasAny "model.bin"
$hasHf      = (Test-Path (Join-Path $stage "huggingface")) -or (Test-Path (Join-Path $stage ".huggingface")) -or (HasAny "*.hf")
$hasLogs    = (Test-Path (Join-Path $stage "logs")) -or (HasAny "*.log")
$hasMeetings= (Test-Path (Join-Path $stage "Meetings"))
$hasKeys    = (HasAny "*.key") -or (HasAny "secrets*") -or (HasAny "settings.json")
$hasWorker  = Test-Path (Join-Path $stage "python/whisper_worker/main.py")
$hasSetup   = Test-Path (Join-Path $stage "setup-python.ps1")
$hasFfmpeg  = Test-Path (Join-Path $stage "tools/ffmpeg/ffmpeg.exe")
$hasFfprobe = Test-Path (Join-Path $stage "tools/ffmpeg/ffprobe.exe")
$hasLibvlc  = ($libvlcMb -gt 0)

Write-Host ""
Write-Host "==== $Edition edition — content scan ===="
Write-Host ("EXCLUDED  venv           : {0}" -f (@{$true='PRESENT (FAIL)';$false='absent OK'}[$hasVenv]))
Write-Host ("EXCLUDED  model.bin      : {0}" -f (@{$true='PRESENT (FAIL)';$false='absent OK'}[$hasModelBin]))
Write-Host ("EXCLUDED  HF cache       : {0}" -f (@{$true='PRESENT (FAIL)';$false='absent OK'}[$hasHf]))
Write-Host ("EXCLUDED  logs           : {0}" -f (@{$true='PRESENT (FAIL)';$false='absent OK'}[$hasLogs]))
Write-Host ("EXCLUDED  meetings       : {0}" -f (@{$true='PRESENT (FAIL)';$false='absent OK'}[$hasMeetings]))
Write-Host ("EXCLUDED  keys/DPAPI     : {0}" -f (@{$true='PRESENT (FAIL)';$false='absent OK'}[$hasKeys]))
Write-Host ("INCLUDED  worker source  : {0}" -f (@{$true='present OK';$false='MISSING (FAIL)'}[$hasWorker]))
Write-Host ("INCLUDED  setup-python   : {0}" -f (@{$true='present OK';$false='MISSING (FAIL)'}[$hasSetup]))
Write-Host ("INCLUDED  ffmpeg.exe     : {0}" -f (@{$true='present OK';$false='MISSING (FAIL)'}[$hasFfmpeg]))
Write-Host ("INCLUDED  ffprobe.exe    : {0}" -f (@{$true='present OK';$false='MISSING (FAIL)'}[$hasFfprobe]))
Write-Host ("INCLUDED  LibVLC         : {0}" -f (@{$true='present OK';$false='MISSING (FAIL)'}[$hasLibvlc]))
