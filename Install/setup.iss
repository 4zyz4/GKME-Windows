; GamepadEmuHost Installer
; Requires InnoSetup 6+  https://jrsoftware.org/isdl.php

#define MyAppName "GamepadEmuHost"
#define MyAppVersion "1.3.0"
#define MyAppPublisher "GamepadEmu"
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
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "Create desktop shortcut"; GroupDescription: "Additional icons:"; Flags: checkedonce

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
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Run]
; Install ViGEM Bus driver silently if task selected
Filename: "{tmp}\ViGEmBus_1.22.0_x64_x86_arm64.exe"; Parameters: "/S /NoCancel /NoClose"; StatusMsg: "正在安装 ViGEM Bus 驱动 ..."; Flags: waituntilterminated; Check: not IsVigemInstalled
; Launch app after install
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; Flags: postinstall nowait skipifsilent unchecked

[Code]
const
  VIGEM_SERVICE_NAME = 'ViGEmBus';
  VIGEM_DRIVER_PATH = 'C:\Windows\System32\drivers\ViGEmBus.sys';

function IsVigemInstalled: Boolean;
begin
  Result := FileExists(VIGEM_DRIVER_PATH);
end;

function InitializeSetup: Boolean;
begin
  Result := True;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
end;
