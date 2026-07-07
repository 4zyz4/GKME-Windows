; GamepadEmuHost Installer
; Requires InnoSetup 6+  https://jrsoftware.org/isdl.php

#define MyAppName "GamepadEmuHost"
#define MyAppVersion "1.3.0"
#define MyAppPublisher "4zyz4"
#define MyAppURL ""
#define MyAppExeName "GamepadEmuHost.exe"

#define PublishDir "..\bin\Release\net8.0-windows\win-x64\publish"
#define VigemDir "VigemDrv"

[Setup]
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayName={#MyAppName}
OutputBaseFilename=GamepadEmuHost_Setup_{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
DisableProgramGroupPage=yes
DisableWelcomePage=yes
DisableDirPage=no
DisableFinishedPage=no
CloseApplications=yes

[Languages]
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"

; no tasks - desktop icon created automatically

[Files]
; App files
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#PublishDir}\*.json"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#PublishDir}\*.pdb"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

; ViGEM Bus driver installer (bundled)
Source: "{#VigemDir}\ViGEmBus_1.22.0_x64_x86_arm64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Run]
; Install ViGEM Bus driver silently if task selected
Filename: "{tmp}\ViGEmBus_1.22.0_x64_x86_arm64.exe"; Parameters: "/qn"; StatusMsg: "正在安装 ViGEM Bus 驱动 ..."; Flags: waituntilterminated; Check: not IsVigemInstalled


[Code]
const
  VIGEM_DRIVER_PATH = 'C:\Windows\System32\drivers\ViGEmBus.sys';

procedure ExitProcess(exitCode: Integer);
  external 'ExitProcess@kernel32.dll stdcall';

function GetUninstallKey: String;
var
  Key, Uninstaller: String;
begin
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#MyAppName}_is1';
  if RegQueryStringValue(HKLM, Key, 'UninstallString', Uninstaller) or
     RegQueryStringValue(HKCU, Key, 'UninstallString', Uninstaller) then
    Result := Key
  else
    Result := '';
end;

function IsVigemInstalled: Boolean;
begin
  Result := FileExists(VIGEM_DRIVER_PATH);
end;

function IsAppInstalled: Boolean;
begin
  Result := GetUninstallKey <> '';
end;

function InitializeSetup: Boolean;
begin
  Result := True;
end;

procedure UninstallApp;
var
  Uninstaller, Key: String;
  ResultCode: Integer;
begin
  Key := GetUninstallKey;
  if Key = '' then exit;
  if RegQueryStringValue(HKLM, Key, 'UninstallString', Uninstaller) or
     RegQueryStringValue(HKCU, Key, 'UninstallString', Uninstaller) then
  begin
    Uninstaller := RemoveQuotes(Uninstaller);
    if FileExists(Uninstaller) then
      Exec(Uninstaller, '/VERYSILENT /NORESTART /SUPPRESSMSGBOXES', '',
           SW_SHOW, ewWaitUntilTerminated, ResultCode);
  end;
end;

var
  RepairPage: TWizardPage;
  RepairRadioRepair: TRadioButton;
  RepairRadioUninstall: TRadioButton;
  UninstallDone: Boolean;

procedure InitializeWizard;
begin
  RepairPage := CreateCustomPage(wpWelcome, '检测到已安装',
    '{#MyAppName} 已经安装在您的计算机上，请选择操作：');

  RepairRadioRepair := TRadioButton.Create(RepairPage);
  RepairRadioRepair.Parent := RepairPage.Surface;
  RepairRadioRepair.Caption := '重新安装（修复模式）';
  RepairRadioRepair.Top := 24;
  RepairRadioRepair.Width := RepairPage.SurfaceWidth;
  RepairRadioRepair.Checked := True;

  RepairRadioUninstall := TRadioButton.Create(RepairPage);
  RepairRadioUninstall.Parent := RepairPage.Surface;
  RepairRadioUninstall.Caption := '卸载程序';
  RepairRadioUninstall.Top := RepairRadioRepair.Top + RepairRadioRepair.Height + 12;
  RepairRadioUninstall.Width := RepairPage.SurfaceWidth;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = RepairPage.ID then
    Result := not IsAppInstalled
  else if UninstallDone and (PageID <> wpFinished) then
    Result := True;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if UninstallDone and (CurPageID = wpFinished) then
  begin
    WizardForm.FinishedLabel.Caption := '{#MyAppName} 已成功卸载。';
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Reply: Integer;
begin
  Result := True;
  if CurPageID = RepairPage.ID then
  begin
    if RepairRadioUninstall.Checked then
    begin
      Reply := SuppressibleMsgBox(
        '是否一并卸载 ViGEM Bus 驱动？' + #13#10 +
        '（如果其他程序也在使用该驱动，请选择"否"）',
        mbConfirmation, MB_YESNO, IDNO);
      UninstallApp;
      if Reply = IDYES then
      begin
        Exec(ExpandConstant('{tmp}\ViGEmBus_1.22.0_x64_x86_arm64.exe'),
             '/uninstall /exenoui /exelog "' + ExpandConstant('{tmp}') + '\vigem_uninstall.log"',
             '', SW_SHOW, ewWaitUntilTerminated, Reply);
      end;
      SuppressibleMsgBox('卸载完成。', mbInformation, MB_OK, IDOK);
      Result := False;
      ExitProcess(0);
    end;
  end;
end;
