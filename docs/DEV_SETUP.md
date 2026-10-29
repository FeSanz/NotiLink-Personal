# Dev machine setup

## Windows: Smart App Control code signing

Windows 11's Smart App Control blocks unsigned executables, including locally-built
dev binaries. `NotiLink.App.exe`/`.dll` and `NotiLink.Core.dll` get signed automatically
after every build (see the `SignOutputAssemblies` target in `NotiLink.App.csproj`), but
this requires a local self-signed code-signing certificate to already exist and be
trusted on the machine.

**One-time setup per dev machine:**

```powershell
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=NotiLink Dev Signing" -CertStoreLocation "Cert:\CurrentUser\My" -KeyExportPolicy Exportable -KeyUsage DigitalSignature -NotAfter (Get-Date).AddYears(5)

$rootStore = New-Object System.Security.Cryptography.X509Certificates.X509Store("Root","CurrentUser")
$rootStore.Open("ReadWrite")
$rootStore.Add($cert)
$rootStore.Close()

$pubStore = New-Object System.Security.Cryptography.X509Certificates.X509Store("TrustedPublisher","CurrentUser")
$pubStore.Open("ReadWrite")
$pubStore.Add($cert)
$pubStore.Close()
```

Adding to the `Root` store shows a Windows security confirmation dialog — approve it.
`sign.ps1` looks up the certificate by its subject (`CN=NotiLink Dev Signing`) at build
time; if it's missing, the build still succeeds but the app won't run under Smart App
Control until the certificate is (re)created.

This is scoped to the current Windows user (`CurrentUser` store), not machine-wide, and
does not disable Smart App Control itself — it only tells Windows to trust binaries
signed by this specific local development certificate.
