; DataJackUI Modern Inno Setup Script
; Provides the modern dynamic dark UI, EULA, directory selection, and custom tasks.

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
SetupIconFile=..\src\DataJackUIGui\icon.ico
OutputDir=..\Releases
OutputBaseFilename=DataJackUI-Inno-Setup
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern dynamic
WizardImageFile=wizard-banner.png
WizardImageFileDynamicDark=wizard-banner.png
WizardSmallImageFile=wizard-small.png
WizardSmallImageFileDynamicDark=wizard-small.png
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
// ── Ensure readable path / group selection under modern dark mode ─────
procedure InitializeWizard();
begin
  // Disable automatic full selection on focus so text doesn't invert into a white highlight block
  WizardForm.DirEdit.AutoSelect := False;
  WizardForm.GroupEdit.AutoSelect := False;

  if IsDarkInstallMode then
  begin
    WizardForm.DirEdit.Color := $1F1F1F;
    WizardForm.DirEdit.Font.Color := $D6D6D6;
    WizardForm.GroupEdit.Color := $1F1F1F;
    WizardForm.GroupEdit.Font.Color := $D6D6D6;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  // When entering Select Directory or Select Program Group, clear full highlight so text is crisp and readable
  if CurPageID = wpSelectDir then
  begin
    WizardForm.DirEdit.SelLength := 0;
    WizardForm.DirEdit.SelStart := Length(WizardForm.DirEdit.Text);
  end
  else if CurPageID = wpSelectProgramGroup then
  begin
    WizardForm.GroupEdit.SelLength := 0;
    WizardForm.GroupEdit.SelStart := Length(WizardForm.GroupEdit.Text);
  end;
end;

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
