param(
    [Parameter(Mandatory)][string]$TargetDir
)

$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq "CN=NotiLink Dev Signing" } |
    Select-Object -First 1

if (-not $cert) {
    Write-Warning "NotiLink Dev Signing certificate not found in Cert:\CurrentUser\My - skipping signing. Smart App Control may block the app from running."
    exit 0
}

foreach ($name in @("NotiLink.App.exe", "NotiLink.App.dll", "NotiLink.Core.dll")) {
    $path = Join-Path $TargetDir $name
    if (Test-Path $path) {
        $result = Set-AuthenticodeSignature -FilePath $path -Certificate $cert
        if ($result.Status -ne "Valid") {
            Write-Warning "Signing $name did not result in a valid signature: $($result.StatusMessage)"
        }
    }
}
