; DataJackUI Modern Inno Setup Script (Wrapping Velopack Bootstrap)
; Provides the modern dynamic dark UI, EULA, directory selection, and custom tasks
; while installing Velopack-managed files so that in-app 300KB delta updates work seamlessly.

#define MyAppName "DataJackUI"
#ifndef MyAppVersion
  #define MyAppVersion "2.19.0"
#endif
#define MyAppPublisher "NetrunnerGames"
#define MyAppURL "https://github.com/NetrunnerGames/DataJackUI"
#define MyAppExeName "DataJackUI.exe"

[Setup]
AppId={{D37A5C2B-5C8F-4B9F-8AE1-62021A5A9B8D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableWelcomePage=no
DisableProgramGroupPage=no
DisableDirPage=no
AllowNoIcons=yes
LicenseFile=..\LICENSE
InfoBeforeFile=welcome-info.txt
OutputDir=..\Releases
OutputBaseFilename=DataJackUI-Inno-Setup
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern dynamic
WizardImageFile=wizard-banner.bmp
WizardSmallImageFile=..\src\DataJackUIGui\icon.ico
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no
SetupMutex=DataJackUISetupMutex_{#MyAppName}
CreateUninstallRegKey=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startmenuicon"; Description: "Create a Start Menu shortcut"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; 1. Application files
Source: "..\bin\PublishFrameworkDependent\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; 2. Embed Velopack update manager assets so the app is recognized as installed by Velopack
Source: "..\Releases\assets.win.json"; DestDir: "{app}\.."; Flags: ignoreversion
Source: "..\Releases\releases.win.json"; DestDir: "{app}\.."; Flags: ignoreversion
Source: "..\Releases\DataJackUI-{#MyAppVersion}-full.nupkg"; DestDir: "{app}\.."; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startmenuicon
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"; Tasks: startmenuicon
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Register datajackui:// custom URL protocol for browser / discord launch
Root: HKCU; Subkey: "Software\Classes\datajackui"; ValueType: string; ValueName: ""; ValueData: "URL:DataJackUI Protocol"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\datajackui"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\datajackui\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\datajackui\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// ── Check if DataJackUI is running before install ──────────────────────
function InitializeSetup(): Boolean;
begin
  Result := True;
  while CheckForMutexes('DataJackUIGui.SingleInstance') do
  begin
    if MsgBox('DataJackUI is currently running.' + #13#10 + #13#10 +
              'Please close DataJackUI before continuing with the installation.',
              mbConfirmation, MB_OKCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

// ── Optional Wipe of AppData & Cache on Uninstall ─────────────────────
procedure InitializeUninstallProgressForm();
var
  DataDir: String;
begin
  DataDir := ExpandConstant('{userappdata}\DataJackUIGui');
  if DirExists(DataDir) then
  begin
    if MsgBox('Do you also want to remove all personal settings, cache, and profile data?' + #13#10 + #13#10 +
              'Location: ' + DataDir, mbConfirmation, MB_YESNO) = IDYES then
    begin
      DelTree(DataDir, True, True, True);
    end;
  end;
end;
