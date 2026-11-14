; Inno Setup script for the GitDash Windows installer.
; Built by the "Release" GitHub workflow:  iscc /DAppVersion=1.2.3 installer\GitDash.iss
; Expects the self-contained publish output in ..\publish and writes the installer to ..\dist.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
; Never change AppId: it lets a newer installer upgrade an existing installation in place.
AppId={{B6F1C8A2-5D3E-4C7B-9A41-2E8F6D0C7A15}
AppName=GitDash
AppVersion={#AppVersion}
AppVerName=GitDash {#AppVersion}
AppPublisher=Solitontech
DefaultDirName={autopf}\GitDash
DefaultGroupName=GitDash
DisableProgramGroupPage=yes
; Installs for the current user without admin rights; the user can choose "all users" in the dialog.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist
OutputBaseFilename=GitDash-Setup-{#AppVersion}
SetupIconFile=..\src\GitDash.App\Assets\app.ico
UninstallDisplayIcon={app}\GitDash.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\GitDash"; Filename: "{app}\GitDash.exe"
Name: "{autodesktop}\GitDash"; Filename: "{app}\GitDash.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GitDash.exe"; Description: "Launch GitDash"; Flags: nowait postinstall skipifsilent
; Silent installs are what the in-app updater runs: open the new version again straight away.
Filename: "{app}\GitDash.exe"; Flags: nowait runasoriginaluser skipifnotsilent
