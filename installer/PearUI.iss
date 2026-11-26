; PearUI installer (Inno Setup 6). Built by installer\build-installer.ps1 / the GitHub release workflow.
;   ISCC.exe /DAppVersion=1.0.0 /DSourceDir=..\dist\publish PearUI.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\publish"
#endif

[Setup]
AppId={{7B1F6D2E-4C1A-4E7B-9A63-5E0C2D4F8A11}
AppName=PearUI
AppVersion={#AppVersion}
AppVerName=PearUI {#AppVersion}
AppPublisher=Guru Swarupa
VersionInfoVersion={#AppVersion}
VersionInfoCompany=Guru Swarupa
VersionInfoDescription=PearUI Setup
VersionInfoProductName=PearUI
; Per-user install: no administrator rights needed for PearUI itself.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\PearUI\app
DisableDirPage=yes
DisableProgramGroupPage=yes
DefaultGroupName=PearUI
UninstallDisplayName=PearUI
UninstallDisplayIcon={app}\PearUI.exe
SetupIconFile=..\src\PearUI\PearUI.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=PearUI-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
MinVersion=10.0.19041

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup";    Description: "Start PearUI as soon as I sign in (highest priority)"; GroupDescription: "Startup:"
Name: "terminal";   Description: "macOS-style Terminal: Windows Terminal with Command Prompt as the default, translucent look"; GroupDescription: "Terminal and Linux commands:"
Name: "linuxtools"; Description: "Linux commands in Command Prompt (ls, grep, cat, touch, sed, awk, ...) - installs Git for Windows if needed"; GroupDescription: "Terminal and Linux commands:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "configure.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\src\PearUI\PearUI.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\PearUI"; Filename: "{app}\PearUI.exe"; WorkingDir: "{app}"; IconFilename: "{app}\PearUI.ico"; Comment: "macOS-style menu bar, Dock, search and Control Center"

[Run]
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\configure.ps1"" -InstallDir ""{app}""{code:ConfigureArgs}"; \
  StatusMsg: "Setting up Terminal, Linux commands and start-up..."; Flags: runhidden waituntilterminated
; An automatic update (PearUI itself starts the installer with /restart=1) brings PearUI back up when it is done.
Filename: "{app}\PearUI.exe"; Flags: nowait; Check: RestartAfterUpdate
Filename: "{app}\PearUI.exe"; Description: "Start PearUI now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\configure.ps1"" -InstallDir ""{app}"" -Uninstall"; \
  RunOnceId: "PearUIConfigureUninstall"; Flags: runhidden waituntilterminated

[Code]
function RestartAfterUpdate: Boolean;
begin
  Result := ExpandConstant('{param:restart|0}') = '1';
end;

function ConfigureArgs(Param: String): String;
begin
  Result := '';
  if not WizardIsTaskSelected('startup')    then Result := Result + ' -NoAutoStart';
  if not WizardIsTaskSelected('terminal')   then Result := Result + ' -SkipTerminal';
  if not WizardIsTaskSelected('linuxtools') then Result := Result + ' -SkipLinuxTools';
end;

// Upgrading: stop the running PearUI so its files can be replaced (this also gives the taskbar back).
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\PearUI.exe')) then
  begin
    Exec(ExpandConstant('{app}\PearUI.exe'), '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(2500);
  end;
end;
