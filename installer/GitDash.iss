; Inno Setup script for the GitDash Windows installer.
; Built by the "Release" GitHub workflow:  iscc /DAppVersion=1.2.3 installer\GitDash.iss
; Expects the self-contained publish output in ..\publish and writes the installer to ..\dist.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

; GitHub CLI offered by the installer (downloaded from GitHub's own release, checked against this SHA-256).
; To move to a newer gh: change both lines to the values of that release's gh_<version>_windows_amd64.msi.
#define GhVersion "2.102.0"
#define GhSha256 "7944b579d76359e5b859a263bf63583e271ae3fbd427ae46e133c1f348b46371"

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
; Only offered when gh is not already on the computer. GitDash uses it to sign in with your GitHub login.
Name: "installgh"; Description: "Install &GitHub CLI (lets GitDash sign you in to GitHub; needs internet)"; Check: GhMissing

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\GitDash"; Filename: "{app}\GitDash.exe"
Name: "{autodesktop}\GitDash"; Filename: "{app}\GitDash.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GitDash.exe"; Description: "Launch GitDash"; Flags: nowait postinstall skipifsilent
; Silent installs are what the in-app updater runs: open the new version again straight away.
Filename: "{app}\GitDash.exe"; Flags: nowait runasoriginaluser skipifnotsilent

[Code]
var
  DownloadPage: TDownloadWizardPage;
  GhMsi: String;

{ True when the GitHub CLI cannot be found (the usual install folders, then PATH). }
function GhMissing: Boolean;
begin
  Result := not (
    FileExists(ExpandConstant('{commonpf64}\GitHub CLI\gh.exe')) or
    FileExists(ExpandConstant('{localappdata}\Programs\GitHub CLI\gh.exe')) or
    (FileSearch('gh.exe', GetEnv('PATH')) <> ''));
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
end;

{ Downloads the GitHub CLI installer once the user has confirmed the choices. A failure never stops GitDash itself. }
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpReady then GhMsi := '';
  if (CurPageID = wpReady) and WizardIsTaskSelected('installgh') then
  begin
    DownloadPage.Clear;
    DownloadPage.Add('https://github.com/cli/cli/releases/download/v{#GhVersion}/gh_{#GhVersion}_windows_amd64.msi', 'gh.msi', '{#GhSha256}');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
        GhMsi := ExpandConstant('{tmp}\gh.msi');
      except
        if DownloadPage.AbortedByUser then
          Result := False
        else
          MsgBox('GitHub CLI could not be downloaded (' + GetExceptionMessage + '). GitDash will still be installed; ' +
                 'you can get GitHub CLI later from cli.github.com.', mbInformation, MB_OK);
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

{ Installs the downloaded MSI after GitDash itself. Windows asks for permission because gh installs for all users. }
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep = ssPostInstall) and (GhMsi <> '') then
  begin
    WizardForm.StatusLabel.Caption := 'Installing GitHub CLI...';
    if not ShellExec('runas', 'msiexec.exe', '/i "' + GhMsi + '" /qn /norestart', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode)
       or ((ResultCode <> 0) and (ResultCode <> 3010)) then
      MsgBox('GitHub CLI was not installed. GitDash is installed; you can get GitHub CLI later from cli.github.com.', mbInformation, MB_OK);
  end;
end;
