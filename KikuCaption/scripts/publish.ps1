<#
.SYNOPSIS
  Builds the KikuCaption portable release (Milestone 7, delivery approach A: self-contained .NET +
  scripted Python). Produces a portable folder + zip + SHA-256, excluding all user/secret/dev data.

.NOTES
  Approach A does NOT bundle the Python runtime or the Whisper model. The published app expects a
  Python venv (see scripts/setup-python.ps1 / docs/UserGuide.md) and downloads the small model on
  first run. FFmpeg is included from tools/ffmpeg if present, else the user configures a path.
#>
param(
  [string]$Configuration = "Release",
  [string]$Runtime = "win-x64",
  [string]$OutputRoot = "$PSScriptRoot/../publish"
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path "$PSScriptRoot/.."
$version = "0.1.0"
$stage = Join-Path $OutputRoot "KikuCaption-$version-$Runtime"

Write-Host "Publishing self-contained $Runtime ($Configuration) ..."
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# Self-contained .NET app (no trimming: WPF + reflection). PDBs excluded from the user package.
dotnet publish "$repo/src/KikuCaption.App/KikuCaption.App.csproj" `
  -c $Configuration -r $Runtime --self-contained true `
  -p:DebugType=None -p:DebugSymbols=false `
  -o $stage

# Bundle FFmpeg if present (GPL v3 — see THIRD_PARTY_NOTICES.md), else document a configured path.
$ffmpeg = Join-Path $repo "tools/ffmpeg"
if (Test-Path (Join-Path $ffmpeg "ffmpeg.exe")) {
  New-Item -ItemType Directory -Force -Path (Join-Path $stage "tools/ffmpeg") | Out-Null
  Copy-Item (Join-Path $ffmpeg "ffmpeg.exe")  (Join-Path $stage "tools/ffmpeg") -Force
  Copy-Item (Join-Path $ffmpeg "ffprobe.exe") (Join-Path $stage "tools/ffmpeg") -Force -ErrorAction SilentlyContinue
  Write-Host "Bundled FFmpeg (GPL v3)."
} else {
  Write-Host "FFmpeg not found under tools/ffmpeg — user must configure Recording:FFmpegPath."
}

# Docs + notices + Python scripts for the user.
Copy-Item (Join-Path $repo "README.md") $stage -Force
Copy-Item (Join-Path $repo "THIRD_PARTY_NOTICES.md") $stage -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path $stage "docs") | Out-Null
Copy-Item (Join-Path $repo "docs/UserGuide.md") (Join-Path $stage "docs") -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $repo "docs/Delivery.md")  (Join-Path $stage "docs") -Force -ErrorAction SilentlyContinue
if (Test-Path (Join-Path $repo "licenses")) { Copy-Item (Join-Path $repo "licenses") $stage -Recurse -Force }
if (Test-Path (Join-Path $repo "python/whisper_worker")) {
  New-Item -ItemType Directory -Force -Path (Join-Path $stage "python/whisper_worker") | Out-Null
  Get-ChildItem (Join-Path $repo "python/whisper_worker") -File | Where-Object { $_.Extension -in ".py",".txt" } |
    Copy-Item -Destination (Join-Path $stage "python/whisper_worker") -Force
}

# Hard exclusions: never ship secrets, user data, logs, caches, venv, models, symbols.
$excludeGlobs = @("*.pdb","secrets","settings.json","Meetings","logs",".venv","models","*.key",".huggingface","huggingface","__pycache__","*.corrupt-*.bak")
foreach ($g in $excludeGlobs) {
  Get-ChildItem $stage -Recurse -Force -Filter $g -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

# Zip + SHA-256.
$zip = "$stage.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage/*" -DestinationPath $zip
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash
Set-Content -Path "$zip.sha256" -Value "$sha  $(Split-Path $zip -Leaf)" -Encoding ascii

$sizeMb = [math]::Round(((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "Portable folder: $stage ($sizeMb MB)"
Write-Host "Zip: $zip"
Write-Host "SHA-256: $sha"
