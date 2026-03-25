#define AppName      "WGSM"
#define AppPublisher "jonsjsj"
#define AppURL       "https://github.com/jonsjsj/WGSMwebapi"
#define AppExe       "WGSM.exe"
#define RegKey       "Software\WGSM"
#define FirewallRule "WGSM Web API"
#define DefaultPort  "7876"

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef OutputMode
  #define OutputMode "full"
#endif

#if OutputMode == "full"
  #define OutputBase "WGSM-Full-Setup-" + AppVersion
#else
  #define OutputBase "WGSM-Addon-Setup-" + AppVersion
#endif

[Setup]
AppId={{E4A1B2C3-D4E5-F6A7-B8C9-D0E1F2A3B4C5}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={autopf}\WGSM
DefaultGroupName=WGSM
AllowNoIcons=no
LicenseFile=..\LICENSE
OutputDir=..\dist
OutputBaseFilename={#OutputBase}
SetupIconFile=..\WGSM\Images\WGSM.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
MinVersion=10.0.17763
CloseApplications=yes
CloseApplicationsFilter=WGSM.exe
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
Name: "core";   Description: "WGSM application";            Types: default; Check: IsFullMode
Name: "webapi"; Description: "Web API + remote control UI";  Types: default; Flags: fixed

[Files]
Source: "..\publish\WGSM.exe"; DestDir: "{app}"; Components: core; Flags: ignoreversion; Check: IsFullMode
Source: "..\publish\WebApi\wwwroot\*"; DestDir: "{app}\WebApi\wwwroot"; Components: webapi; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autodesktop}\WGSM"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"; Comment: "WGSM Game Server Manager"
Name: "{group}\WGSM"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[Registry]
Root: HKLM; Subkey: "{#RegKey}"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "WGSM"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue

[Run]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#FirewallRule}"" dir=in action=allow protocol=TCP localport={#DefaultPort} description=""WGSM Web API"""; Flags: runhidden waituntilterminated; StatusMsg: "Adding firewall rule for port {#DefaultPort}..."
Filename: "{app}\{#AppExe}"; Description: "Launch WGSM now"; Flags: nowait postinstall skipifsilent shellexec runascurrentuser

[UninstallRun]
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
    if FileExists(Path + '\WGSM.exe') then
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
        'This will add Web API remote control to your existing WGSM.' + #13#10 + #13#10 +
        'Detected installation: ' + ExistingPath;
    end else
      WizardForm.WelcomeLabel2.Caption :=
        'No existing WGSM was found. You can still install — point the ' +
        'Browse button at your WGSM folder on the next page.';
  end else begin
    if ExistingPath <> '' then
      WizardForm.WelcomeLabel2.Caption :=
        'Existing WGSM found at: ' + ExistingPath + #13#10 +
        'This will upgrade it with the latest version + Web API.';
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpSelectDir) and ('{#OutputMode}' = 'apionly') then
    if not FileExists(WizardForm.DirEdit.Text + '\WGSM.exe') then
      if MsgBox(
        'WGSM.exe was not found in the selected folder.' + #13#10 +
        'The Web API needs WGSM to function. Continue anyway?',
        mbConfirmation, MB_YESNO) = IDNO then
        Result := False;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
    WizardForm.FinishedLabel.Caption :=
      'Installation complete!' + #13#10 + #13#10 +
      'Getting started:' + #13#10 +
      '  1. Open WGSM' + #13#10 +
      '  2. Click "Web API" in the left menu' + #13#10 +
      '  3. Click "Generate" to create an API token' + #13#10 +
      '  4. Click "Start Web API"' + #13#10 +
      '  5. Open http://localhost:{#DefaultPort}/ui' + #13#10 + #13#10 +
      'Firewall rule added for port {#DefaultPort}.' + #13#10 +
      'WGSM added to Windows startup (runs on login).';
end;
