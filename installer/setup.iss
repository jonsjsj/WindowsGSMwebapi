#define AppName      "WindowsGSM Web API"
#define AppPublisher "jonsjsj"
#define AppURL       "https://github.com/jonsjsj/WindowsGSMwebapi"
#define AppExe       "WindowsGSM.exe"
#define RegKey       "Software\WindowsGSM"
#define ServiceName  "WindowsGSMWebAPI"
#define FirewallRule "WindowsGSM Web API"
#define DefaultPort  "7876"

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef OutputMode
  #define OutputMode "full"
#endif

#if OutputMode == "full"
  #define OutputBase "WindowsGSM-WebAPI-Full-Setup-" + AppVersion
#else
  #define OutputBase "WindowsGSM-WebAPI-Addon-Setup-" + AppVersion
#endif

[Setup]
AppId={{E4A1B2C3-D4E5-F6A7-B8C9-D0E1F2A3B4C5}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={autopf}\WindowsGSM
DefaultGroupName=WindowsGSM
AllowNoIcons=no
LicenseFile=..\LICENSE
OutputDir=..\dist
OutputBaseFilename={#OutputBase}
SetupIconFile=..\WindowsGSM\Images\WindowsGSM.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
MinVersion=10.0.17763
CloseApplications=yes
CloseApplicationsFilter=WindowsGSM.exe
ShowLanguageDialog=no
LanguageDetectionMethod=locale
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} Installer

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "default"; Description: "Default installation"; Flags: iscustom

[Components]
Name: "core";   Description: "WindowsGSM application";      Types: default; Check: IsFullMode
Name: "webapi"; Description: "Web API + remote control UI";  Types: default; Flags: fixed
Name: "nssm";   Description: "Windows service (NSSM)";       Types: default; Flags: fixed

[Files]
Source: "..\publish\WindowsGSM.exe"; DestDir: "{app}"; Components: core; Flags: ignoreversion; Check: IsFullMode
Source: "..\publish\WebApi\wwwroot\*"; DestDir: "{app}\WebApi\wwwroot"; Components: webapi; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "assets\nssm.exe"; DestDir: "{app}\tools"; Components: nssm; Flags: ignoreversion

[Icons]
Name: "{autodesktop}\WindowsGSM"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"; Comment: "WindowsGSM Game Server Manager"
Name: "{group}\WindowsGSM"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[Registry]
Root: HKLM; Subkey: "{#RegKey}"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "WindowsGSM"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue

[Run]
Filename: "{app}\tools\nssm.exe"; Parameters: "install {#ServiceName} ""{app}\{#AppExe}"""; Flags: runhidden waituntilterminated; StatusMsg: "Registering Windows service..."
Filename: "{app}\tools\nssm.exe"; Parameters: "set {#ServiceName} Description ""WindowsGSM Web API remote control service"""; Flags: runhidden waituntilterminated
Filename: "{app}\tools\nssm.exe"; Parameters: "set {#ServiceName} Start SERVICE_AUTO_START"; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#FirewallRule}"" dir=in action=allow protocol=TCP localport={#DefaultPort} description=""WindowsGSM Web API"""; Flags: runhidden waituntilterminated; StatusMsg: "Adding firewall rule for port {#DefaultPort}..."
Filename: "{app}\tools\nssm.exe"; Parameters: "start {#ServiceName}"; Flags: runhidden waituntilterminated; StatusMsg: "Starting service..."
Filename: "{app}\{#AppExe}"; Description: "Launch WindowsGSM now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\tools\nssm.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopSvc"
Filename: "{app}\tools\nssm.exe"; Parameters: "remove {#ServiceName} confirm"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveSvc"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveFW"

[Code]
function IsFullMode(): Boolean;
begin
  Result := ('{#OutputMode}' = 'full');
end;

function FindExistingInstall(): String;
var
  Path: String;
begin
  Result := '';
  if RegQueryStringValue(HKLM, '{#RegKey}', 'InstallPath', Path) then
    if FileExists(Path + '\WindowsGSM.exe') then
      Result := Path;
end;

procedure InitializeWizard();
var
  ExistingPath: String;
begin
  ExistingPath := FindExistingInstall();
  if ('{#OutputMode}' = 'apionly') then
  begin
    if ExistingPath <> '' then
    begin
      WizardForm.DirEdit.Text := ExistingPath;
      WizardForm.WelcomeLabel2.Caption :=
        'This will add Web API remote control to your existing WindowsGSM.' + #13#10 + #13#10 +
        'Detected installation: ' + ExistingPath;
    end else
      WizardForm.WelcomeLabel2.Caption :=
        'No existing WindowsGSM was found. You can still install — point the ' +
        'Browse button at your WindowsGSM folder on the next page.';
  end else begin
    if ExistingPath <> '' then
      WizardForm.WelcomeLabel2.Caption :=
        'Existing WindowsGSM found at: ' + ExistingPath + #13#10 +
        'This will upgrade it with the latest version + Web API.';
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpSelectDir) and ('{#OutputMode}' = 'apionly') then
    if not FileExists(WizardForm.DirEdit.Text + '\WindowsGSM.exe') then
      if MsgBox(
        'WindowsGSM.exe was not found in the selected folder.' + #13#10 +
        'The Web API needs WindowsGSM to function. Continue anyway?',
        mbConfirmation, MB_YESNO) = IDNO then
        Result := False;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
    WizardForm.FinishedLabel.Caption :=
      'Installation complete!' + #13#10 + #13#10 +
      'Getting started:' + #13#10 +
      '  1. Open WindowsGSM' + #13#10 +
      '  2. Click "Web API" in the left menu' + #13#10 +
      '  3. Click "Generate" to create an API token' + #13#10 +
      '  4. Click "Start Web API"' + #13#10 +
      '  5. Open http://localhost:{#DefaultPort}/ui' + #13#10 + #13#10 +
      'Firewall rule added for port {#DefaultPort}.' + #13#10 +
      'Windows service registered to start app on boot.';
end;
