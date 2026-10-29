param([Parameter(Mandatory)][string]$Path)

# Signing hook for Inno Setup ("nlsign" tool, see build-installer.ps1): signs one file with the
# NotiLink code-signing certificate, timestamped so the signature stays valid after the
# certificate itself expires. Fails loudly — an unsigned installer is worse than a failed build.
$ErrorActionPreference = "Stop"

$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq "CN=NotiLink Dev Signing" -and $_.HasPrivateKey } |
    Select-Object -First 1
if (-not $cert) { Write-Error "NotiLink Dev Signing certificate not found in Cert:\CurrentUser\My"; exit 1 }

$result = $null
try {
    $result = Set-AuthenticodeSignature -FilePath $Path -Certificate $cert -HashAlgorithm SHA256 `
        -TimestampServer "http://timestamp.digicert.com"
} catch {
    Write-Warning "Timestamped signing failed ($($_.Exception.Message)); signing without timestamp."
}
if (-not $result -or $result.Status -ne "Valid") {
    $result = Set-AuthenticodeSignature -FilePath $Path -Certificate $cert -HashAlgorithm SHA256
}
if ($result.Status -ne "Valid") { Write-Error "Signing $Path failed: $($result.StatusMessage)"; exit 1 }
exit 0
