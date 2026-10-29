<#
  Builds the distributable Windows installer: dist\installer\NotiLink-Setup-<version>.exe

  Steps: fetch scrcpy if missing -> publish the self-contained single-file app into a clean
  staging folder -> drop debug-only files -> sign the app binaries -> compile installer\NotiLink.iss
  with Inno Setup (which signs the setup and uninstaller through installer\sign-file.ps1) ->
  copy the public certificate and the Android APK (if built) next to the setup.

  publish.ps1 is still the quick "update my own PC in place" path; this one is for handing the
  app to another PC or person.
#>

$ErrorActionPreference = "Stop"
$windowsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $windowsDir
$csproj = Join-Path $windowsDir "NotiLink.App\NotiLink.App.csproj"
$outDir = Join-Path $repoRoot "dist\installer"
$stagingDir = Join-Path $outDir "staging"
$requirements = Join-Path $repoRoot "docs\REQUISITOS.md"
$certFile = Join-Path $outDir "NotiLink-Signing.cer"
$signScript = Join-Path $windowsDir "installer\sign-file.ps1"

$iscc = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = "1.0.0" }
Write-Host "NotiLink $version"

Write-Host "Ensuring scrcpy is vendored..."
& (Join-Path $windowsDir "tools\fetch-scrcpy.ps1")

if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stagingDir | Out-Null

Write-Host "Publishing (Release, self-contained, single file)..."
dotnet publish $csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $stagingDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Debug symbols and API doc XML are build leftovers, not something a user needs installed.
Get-ChildItem $stagingDir -File | Where-Object { $_.Extension -in ".pdb", ".xml" } | Remove-Item -Force

Write-Host "Signing app binaries..."
& (Join-Path $windowsDir "NotiLink.App\sign.ps1") -TargetDir $stagingDir

Write-Host "Exporting public signing certificate..."
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq "CN=NotiLink Dev Signing" } | Select-Object -First 1
if (-not $cert) { throw "NotiLink Dev Signing certificate not found in Cert:\CurrentUser\My" }
Export-Certificate -Cert $cert -FilePath $certFile -Type CERT | Out-Null

Write-Host "Compiling installer..."
$signCmd = "nlsign=powershell.exe -NoProfile -ExecutionPolicy Bypass -File `$q$signScript`$q `$f"
& $iscc "/DAppVersion=$version" "/DStagingDir=$stagingDir" "/DOutputDir=$outDir" `
    "/DRequirementsFile=$requirements" "/DCertFile=$certFile" "/S$signCmd" `
    (Join-Path $windowsDir "installer\NotiLink.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed" }

$apk = Join-Path $repoRoot "android\app\build\outputs\apk\debug\app-debug.apk"
if (Test-Path $apk) {
    Copy-Item $apk (Join-Path $outDir "NotiLink-Android-$version.apk") -Force
    Write-Host "Android APK copied next to the installer."
}

Remove-Item $stagingDir -Recurse -Force
$setup = Join-Path $outDir "NotiLink-Setup-$version.exe"
$sizeMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host ""
Write-Host "Installer: $setup ($sizeMb MB)"
Write-Host "Signature: $((Get-AuthenticodeSignature $setup).Status)"
