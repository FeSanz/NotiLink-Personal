<#
  Publishes NotiLink.App as a self-contained single-file exe and installs it in place at a
  stable location (%LocalAppData%\Programs\NotiLink), with Start Menu + Startup shortcuts.

  Run this any time you want to push out a code change as an "update" - it republishes over
  the same install path, so there's nothing to uninstall first and no separate installer to
  click through. Re-run it, close and reopen the app (or let it pick up next time Windows
  starts it), and the new build is live.

  This intentionally stops short of a real auto-update feed (checking a server, downloading
  deltas, etc.) - that's real infrastructure meant for shipping to other people. For a single
  personal machine, "re-run this script" is the right amount of tooling.
#>

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$installDir = Join-Path $env:LOCALAPPDATA "Programs\NotiLink"

Write-Host "Publishing NotiLink.App (Release, self-contained, single file)..."
dotnet publish (Join-Path $scriptDir "NotiLink.App\NotiLink.App.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $installDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "Signing the published exe (Smart App Control needs this on this machine)..."
& (Join-Path $scriptDir "NotiLink.App\sign.ps1") -TargetDir $installDir

$exePath = Join-Path $installDir "NotiLink.App.exe"
if (-not (Test-Path $exePath)) { throw "Published exe not found at $exePath" }

function New-AppShortcut([string]$LinkPath) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($LinkPath)
    $shortcut.TargetPath = $exePath
    $shortcut.WorkingDirectory = $installDir
    $shortcut.IconLocation = $exePath
    $shortcut.Save()
}

$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
New-AppShortcut (Join-Path $startMenuDir "NotiLink.lnk")
Write-Host "Start Menu shortcut created."

$startupDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Startup"
New-AppShortcut (Join-Path $startupDir "NotiLink.lnk")
Write-Host "Startup shortcut created - NotiLink will open automatically at sign-in."
Write-Host "  Disable any time from Task Manager, Startup Apps tab, NotiLink, Disable."
Write-Host "  Windows just won't launch it - nothing on the app's side depends on that."

Write-Host ""
Write-Host "Installed to: $installDir"
Write-Host "Done."
