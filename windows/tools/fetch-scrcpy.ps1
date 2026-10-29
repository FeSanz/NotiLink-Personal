# Downloads and verifies the official scrcpy Windows release into
# windows/NotiLink.App/tools/scrcpy/ (gitignored — see .gitignore and CLAUDE.md).
# scrcpy (https://github.com/Genymobile/scrcpy) is what powers "Compartir pantalla" —
# screen mirroring/control over ADB, launched as an external process by MainWindow.
#
# Run once after a fresh clone, or whenever bumping $Version. Safe to re-run: skips
# the download if the target folder already has scrcpy.exe.

$ErrorActionPreference = "Stop"

$Version = "5.0.1"
$Sha256 = "b12a2c4ee8be317422451fc7dcf8ee20a71b5ea7ef9ad73ddd825a25316227a5"
$ZipName = "scrcpy-win64-v$Version.zip"
$Url = "https://github.com/Genymobile/scrcpy/releases/download/v$Version/$ZipName"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$TargetDir = Join-Path $RepoRoot "NotiLink.App\tools\scrcpy"

if (Test-Path (Join-Path $TargetDir "scrcpy.exe")) {
    Write-Host "scrcpy already present at $TargetDir - nothing to do."
    exit 0
}

$TempZip = Join-Path $env:TEMP $ZipName
Write-Host "Downloading scrcpy v$Version..."
Invoke-WebRequest -Uri $Url -OutFile $TempZip

$ActualHash = (Get-FileHash -Path $TempZip -Algorithm SHA256).Hash.ToLower()
if ($ActualHash -ne $Sha256) {
    Remove-Item $TempZip -Force
    throw "SHA256 mismatch for $ZipName - expected $Sha256, got $ActualHash. Aborting, did not extract."
}
Write-Host "Checksum verified."

$TempExtract = Join-Path $env:TEMP "scrcpy-extract-$Version"
if (Test-Path $TempExtract) { Remove-Item $TempExtract -Recurse -Force }
Expand-Archive -Path $TempZip -DestinationPath $TempExtract

New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
Copy-Item -Path (Join-Path $TempExtract "scrcpy-win64-v$Version\*") -Destination $TargetDir -Recurse -Force

Remove-Item $TempZip -Force
Remove-Item $TempExtract -Recurse -Force

Write-Host "scrcpy v$Version installed to $TargetDir"
