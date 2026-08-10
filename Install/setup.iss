; GamepadEmuHost Installer
; Requires InnoSetup 6+  https://jrsoftware.org/isdl.php

#define MyAppName "GamepadEmuHost"
#define MyAppVersion "3.0.2"
#define MyAppPublisher "4zyz4"
#define MyAppURL ""
#define MyAppExeName "GamepadEmuHost.exe"

#define BuildDir "..\bin\Release\net10.0-windows\win-x64"
#define VigemDir "VigemDrv"

[Setup]
AppName=GamepadEmuHost
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\GamepadEmuHost
DefaultGroupName=GamepadEmuHost
UninstallDisplayName=GamepadEmuHost
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

[Files]
; App files - 直接覆盖安装，不需要手动卸载
Source: "{#BuildDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#BuildDir}\*.json"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#BuildDir}\*.pdb"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

; ViGEM Bus driver installer (bundled, installed by main program)
Source: "{#VigemDir}\ViGEmBus_1.22.0_x64_x86_arm64.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\GamepadEmuHost"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\GamepadEmuHost"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall GamepadEmuHost"; Filename: "{uninstallexe}"

[Code]
const
  VIGEM_DRIVER_PATH = 'C:\Windows\System32\drivers\ViGEmBus.sys';
  MyAppName = 'GamepadEmuHost';
  MyAppVersion = '3.0.2';

var
  InfoPage: TWizardPage;
  InfoLabel: TNewStaticText;
  InstalledVersion: String;
  IsUpgradeMode: Boolean;
  PageCreated: Boolean;

function IsVigemInstalled: Boolean;
begin
  Result := FileExists(VIGEM_DRIVER_PATH);
end;

function GetUninstallKey: String;
var
  Key, Uninstaller: String;
begin
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + MyAppName + '_is1';
  if RegQueryStringValue(HKLM, Key, 'UninstallString', Uninstaller) or
     RegQueryStringValue(HKCU, Key, 'UninstallString', Uninstaller) then
    Result := Key
  else
    Result := '';
end;

function IsAppInstalled: Boolean;
begin
  Result := GetUninstallKey <> '';
end;

function GetInstalledVersion: String;
var
  Key: String;
begin
  Key := GetUninstallKey;
  if Key <> '' then
  begin
    if not RegQueryStringValue(HKLM, Key, 'DisplayVersion', Result) then
      RegQueryStringValue(HKCU, Key, 'DisplayVersion', Result);
  end;
end;

// 修复后的版本比较函数
function CompareVersion(const V1, V2: String): Integer;
var
  N1, N2: Integer;
  S1, S2: String;
  C1, C2: Integer;
begin
  S1 := V1;
  S2 := V2;
  
  while (S1 <> '') or (S2 <> '') do
  begin
    // 获取第一个数字
    C1 := Pos('.', S1);
    C2 := Pos('.', S2);
    
    if C1 = 0 then
    begin
      N1 := StrToIntDef(S1, 0);
      S1 := '';
    end else
    begin
      N1 := StrToIntDef(Copy(S1, 1, C1 - 1), 0);
      Delete(S1, 1, C1);
    end;
    
    if C2 = 0 then
    begin
      N2 := StrToIntDef(S2, 0);
      S2 := '';
    end else
    begin
      N2 := StrToIntDef(Copy(S2, 1, C2 - 1), 0);
      Delete(S2, 1, C2);
    end;
    
    if N1 < N2 then
    begin
      Result := -1;
      Exit;
    end;
    if N1 > N2 then
    begin
      Result := 1;
      Exit;
    end;
  end;
  
  Result := 0;
end;

procedure CreateInfoPage;
var
  Title, Desc: String;
begin
  if PageCreated then exit;
  
  // 检测已安装版本
  InstalledVersion := GetInstalledVersion;
  
  // 显示检测到的版本用于调试
  if InstalledVersion <> '' then
    Log('检测到已安装版本: ' + InstalledVersion);
  
  IsUpgradeMode := (InstalledVersion <> '') and 
                   (CompareVersion(InstalledVersion, MyAppVersion) < 0);
  
  if IsAppInstalled then
  begin
    if IsUpgradeMode then
    begin
      Title := '正在升级';
      Desc := '检测到已安装版本 v' + InstalledVersion +
        '，将升级到 v' + MyAppVersion + #13#10#13#10 +
        '点击"下一步"开始升级。';
    end else
    begin
      Title := '已安装相同或更新版本版本';
      Desc := MyAppName + ' v' + InstalledVersion + ' 已安装。' + #13#10#13#10 +
        '点击"下一步"进行修复。';
    end;
  end else
  begin
    Title := '准备安装';
    Desc := '将在您的计算机上安装 ' + MyAppName + ' v' + MyAppVersion + #13#10#13#10 +
      '点击"下一步"开始安装。';
  end;

  InfoPage := CreateCustomPage(wpWelcome, Title, Desc);
  
  InfoLabel := TNewStaticText.Create(InfoPage);
  InfoLabel.Parent := InfoPage.Surface;
  InfoLabel.Top := 80;
  InfoLabel.Width := InfoPage.SurfaceWidth;
  InfoLabel.Height := 60;
  InfoLabel.AutoSize := False;
  InfoLabel.WordWrap := True;
  
  if IsAppInstalled then
    InfoLabel.Caption := '提示：安装程序将覆盖现有文件。'
  else
    InfoLabel.Caption := '提示：安装完成后，建议重启计算机以确保驱动正常工作。';
  
  PageCreated := True;
end;

procedure InitializeWizard;
begin
  CreateInfoPage;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  // 如果没有安装任何版本，跳过信息页面（直接显示欢迎页和安装页）
  if PageID = InfoPage.ID then
    Result := not IsAppInstalled;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  // 当到达完成页面时，显示相应的消息
  if CurPageID = wpFinished then
  begin
    if IsUpgradeMode then
      WizardForm.FinishedLabel.Caption := MyAppName + ' v' + MyAppVersion + ' 升级完成！'
    else if IsAppInstalled then
      WizardForm.FinishedLabel.Caption := MyAppName + ' v' + MyAppVersion + ' 修复完成！'
    else
      WizardForm.FinishedLabel.Caption := MyAppName + ' v' + MyAppVersion + ' 安装完成！';
  end;
end;