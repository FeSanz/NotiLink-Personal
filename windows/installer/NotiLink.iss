; NotiLink — Windows installer (Inno Setup 6.1+). Built by windows\build-installer.ps1, which
; passes AppVersion / StagingDir / OutputDir / RequirementsFile and the "nlsign" signing tool.
;
; Packaging decision: the app itself is light enough to ship whole — the self-contained .NET
; single-file exe, its web UI and the vendored scrcpy all go inside this one setup .exe. The
; only heavy dependency, the Microsoft Edge WebView2 Runtime (~130 MB, already present on every
; Windows 11), is NOT bundled: the wizard checks for it and downloads Microsoft's own installer
; only if it's actually missing.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef StagingDir
  #error StagingDir must be defined (run build-installer.ps1)
#endif
#ifndef OutputDir
  #define OutputDir "..\..\dist\installer"
#endif

#define AppName "NotiLink"
#define AppExe "NotiLink.App.exe"
#define WebView2ClientKey "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"

[Setup]
AppId={{6E2B1C34-9A57-4E0F-B7D2-3F8C5A1E9D40}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=NotiLink
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Instalador de {#AppName}
; Per-user install, no admin prompt — the same fixed path publish.ps1 has always used, so this
; installer upgrades an existing copy in place.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=NotiLink-Setup-{#AppVersion}
SetupIconFile=..\NotiLink.App\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
InfoBeforeFile={#RequirementsFile}
SignTool=nlsign
SignedUninstaller=yes

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "startup"; Description: "Abrir NotiLink automáticamente al iniciar sesión en Windows"; GroupDescription: "Opciones:"
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; GroupDescription: "Opciones:"; Flags: unchecked
Name: "trustcert"; Description: "Confiar en el certificado de firma de NotiLink (solo si Smart App Control bloquea la app; Windows pedirá confirmación)"; GroupDescription: "Seguridad:"; Flags: unchecked

[Files]
Source: "{#StagingDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RequirementsFile}"; DestDir: "{app}"; DestName: "REQUISITOS.md"; Flags: ignoreversion
Source: "{#CertFile}"; DestDir: "{app}"; DestName: "NotiLink-Signing.cer"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: startup

[Run]
; Only adds this one public certificate to the CURRENT USER's trust stores (Windows itself shows
; a confirmation dialog for the Root store) — it never disables Smart App Control.
Filename: "{sys}\certutil.exe"; Parameters: "-user -f -addstore Root ""{app}\NotiLink-Signing.cer"""; Flags: runhidden waituntilterminated; Tasks: trustcert
Filename: "{sys}\certutil.exe"; Parameters: "-user -f -addstore TrustedPublisher ""{app}\NotiLink-Signing.cer"""; Flags: runhidden waituntilterminated; Tasks: trustcert
Filename: "{app}\{#AppExe}"; Description: "Abrir {#AppName} ahora"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; WebView2's own cache/profile for the app (lives beside the exe). Paired phones, logs and
; settings in %LocalAppData%\NotiLink are left alone on purpose — reinstalling keeps them.
Type: filesandordirs; Name: "{app}\NotiLink.App.exe.WebView2"

[Code]
var
  DownloadPage: TDownloadWizardPage;

function WebView2Version(RootKey: Integer; SubKey: String): String;
begin
  if not RegQueryStringValue(RootKey, SubKey, 'pv', Result) then Result := '';
end;

function IsWebView2Installed: Boolean;
var
  V: String;
begin
  V := WebView2Version(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{#WebView2ClientKey}');
  if (V = '') or (V = '0.0.0.0') then
    V := WebView2Version(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{#WebView2ClientKey}');
  if (V = '') or (V = '0.0.0.0') then
    V := WebView2Version(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{#WebView2ClientKey}');
  Result := (V <> '') and (V <> '0.0.0.0');
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing),
    'Descargando el componente Microsoft Edge WebView2 Runtime (solo se descarga porque falta en esta PC)...', nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if (CurPageID = wpReady) and not IsWebView2Installed then
  begin
    DownloadPage.Clear;
    // Microsoft's official Evergreen bootstrapper (tiny; it fetches the runtime itself).
    DownloadPage.Add('https://go.microsoft.com/fwlink/p/?LinkId=2124703', 'MicrosoftEdgeWebview2Setup.exe', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
        DownloadPage.SetText('Instalando Microsoft Edge WebView2 Runtime...', '');
        if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '',
                    SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
          MsgBox('No se pudo instalar WebView2 Runtime (código ' + IntToStr(ResultCode) + '). ' +
                 'NotiLink se instalará igual, pero necesitará ese componente para abrir. ' +
                 'Puedes instalarlo después desde https://developer.microsoft.com/microsoft-edge/webview2/',
                 mbError, MB_OK);
      except
        MsgBox('No se pudo descargar WebView2 Runtime: ' + GetExceptionMessage + #13#10 +
               'NotiLink se instalará igual; instala WebView2 después para poder abrirla.', mbError, MB_OK);
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

procedure CloseRunningApp;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  // The app lives in the tray/background, so it's usually running during an upgrade.
  CloseRunningApp;
  Result := '';
end;

function InitializeUninstall: Boolean;
begin
  CloseRunningApp;
  Result := True;
end;
