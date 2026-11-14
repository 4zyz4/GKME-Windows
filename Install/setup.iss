; GKME Installer
; Requires Inno Setup 7+  https://jrsoftware.org/isdl.php

#define MyAppName "GKME"
#define MyAppPublisher "4zyz4"
#define MyAppURL ""
#define MyAppExeName "GKME.exe"
; Single source for every icon this script bakes in: the installer exe, the
; uninstaller and the shortcuts all reuse the application icon.
#define MyAppIcon AddBackslash(SourcePath) + "..\icon.ico"

; Build-Installer.ps1 passes the publish folder with /DBuildDir=<absolute path>.
; Without it (e.g. when compiling from the IDE), fall back to the Release output.
#ifndef BuildDir
  #define BuildDir AddBackslash(SourcePath) + "..\bin\Release\net10.0-windows\win-x64\publish"
#endif

; Build-Installer.ps1 selects the target architecture. The installer binary
; stays x64 (which also runs on Arm64 Windows 11 through x64 emulation); the
; directives below decide where the ARM64 payload may be installed.
#ifndef SetupArch
  #define SetupArch "x64"
#endif
#ifndef OutputSuffix
  #define OutputSuffix "_x64"
#endif
#if SetupArch == "arm64"
  #define AllowedArch "arm64"
  #define Install64Arch "arm64"
#else
  ; The x64 build runs under emulation on Windows on Arm, but its bundled
  ; usbip-win2 installer is x64 and can never load there (kernel drivers must
  ; match the OS architecture), so send those machines to the ARM64 package.
  #define AllowedArch "x64compatible and not arm64"
  #define Install64Arch "x64compatible"
#endif

; Read the version straight from the published executable, which is stamped from
; <Version> in GKME.csproj, so no generated .iss include is needed anymore.
#define MyAppVersion GetVersionNumbersString(AddBackslash(BuildDir) + MyAppExeName)
#if MyAppVersion == ""
  #error Could not read the version from the published GKME.exe. Run dotnet publish first or pass /DBuildDir=<publish folder>.
#endif

[Setup]
SetupArchitecture=x64
ArchitecturesAllowed={#AllowedArch}
; 深色外观：强制使用深色主题，并把标题栏/边框也交给样式绘制，避免亮色边框。
WizardStyle=modern dark includetitlebar
AppName=GKME
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\GKME
DefaultGroupName=GKME
UninstallDisplayName=GKME
; Icon shown for GKME in Settings > Apps (falls back to the exe icon).
UninstallDisplayIcon={app}\{#MyAppExeName}
; Icon of Setup.exe and of the uninstaller (Setup icon is copied to the uninstaller).
SetupIconFile={#MyAppIcon}
OutputBaseFilename=GKME_Setup_{#MyAppVersion}{#OutputSuffix}
VersionInfoVersion={#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesInstallIn64BitMode={#Install64Arch}
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
DisableProgramGroupPage=yes
DisableWelcomePage=yes
DisableDirPage=no
DisableFinishedPage=no
CloseApplications=yes

[Languages]
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"

[CustomMessages]
UpgradingTitle=正在升级
UpgradingDesc=检测到已安装版本 v%1%n将升级到 v%2%n点击"下一步"开始升级。
InstalledTitle=已安装相同或更新版本版本
InstalledDesc=%1 v%2 已安装。%n点击"下一步"进行修复。
InstallTitle=准备安装
InstallDesc=将在您的计算机上安装 %1 v%2%n点击"下一步"开始安装。
NoteOverwrite=提示：安装程序将覆盖现有文件。
NoteRestart=提示：安装完成后，建议重启计算机以确保驱动正常工作。
FinishedUpgrade=%1 v%2 升级完成！
FinishedRepair=%1 v%2 修复完成！
FinishedInstall=%1 v%2 安装完成！

[Files]
Source: "{#BuildDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\icon.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#BuildDir}\*.json"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#BuildDir}\platform-tools\windows\*"; DestDir: "{app}\platform-tools\windows"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "{#BuildDir}\platform-tools\NOTICE.txt"; DestDir: "{app}\platform-tools"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#BuildDir}\web\*"; DestDir: "{app}\web"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "{#BuildDir}\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\GKME"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\icon.ico"
Name: "{autodesktop}\GKME"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\icon.ico"
Name: "{group}\Uninstall GKME"; Filename: "{uninstallexe}"; IconFilename: "{app}\icon.ico"

[Code]
const
  MyAppName = 'GKME';
  MyAppVersion = '{#MyAppVersion}';

var
  InfoPage: TWizardPage;
  InfoLabel: TNewStaticText;
  InstalledVersion: String;
  IsUpgradeMode: Boolean;
  PageCreated: Boolean;

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
  
  InstalledVersion := GetInstalledVersion;
  
  IsUpgradeMode := (InstalledVersion <> '') and 
                   (CompareVersion(InstalledVersion, MyAppVersion) < 0);
  
  if IsAppInstalled then
  begin
    if IsUpgradeMode then
    begin
      Title := ExpandConstant('{cm:UpgradingTitle}');
      Desc := FmtMessage(CustomMessage('UpgradingDesc'), [InstalledVersion, MyAppVersion]);
    end else
    begin
      Title := ExpandConstant('{cm:InstalledTitle}');
      Desc := FmtMessage(CustomMessage('InstalledDesc'), [MyAppName, InstalledVersion]);
    end;
  end else
  begin
    Title := ExpandConstant('{cm:InstallTitle}');
    Desc := FmtMessage(CustomMessage('InstallDesc'), [MyAppName, MyAppVersion]);
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
    InfoLabel.Caption := ExpandConstant('{cm:NoteOverwrite}')
  else
    InfoLabel.Caption := ExpandConstant('{cm:NoteRestart}');
  
  PageCreated := True;
end;

procedure InitializeWizard;
begin
  CreateInfoPage;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = InfoPage.ID then
    Result := not IsAppInstalled;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
  begin
    if IsUpgradeMode then
      WizardForm.FinishedLabel.Caption := FmtMessage(CustomMessage('FinishedUpgrade'), [MyAppName, MyAppVersion])
    else if IsAppInstalled then
      WizardForm.FinishedLabel.Caption := FmtMessage(CustomMessage('FinishedRepair'), [MyAppName, MyAppVersion])
    else
      WizardForm.FinishedLabel.Caption := FmtMessage(CustomMessage('FinishedInstall'), [MyAppName, MyAppVersion]);
  end;
end;