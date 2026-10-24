; 音频设备切换面板 安装脚本（Inno Setup 6）
; 生成：build-installer.cmd，或 ISCC.exe /DAppVersion=x.y.z installer\AudioPanel.iss

#ifndef AppVersion
  #define AppVersion "0.3.0"
#endif
#define AppName "音频设备切换面板"
#define AppExe "AudioPanel.exe"
#define PublishDir "..\artifacts\publish-" + AppVersion

[Setup]
AppId={{6E0C2B7A-3F51-4C8E-9A4D-2B7F1D9E5A30}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=phong
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
; 按当前用户安装，不需要管理员权限；开机启动和配置也都是当前用户的
PrivilegesRequired=lowest
DefaultDirName={autopf}\AudioPanel
DisableProgramGroupPage=yes
DisableDirPage=auto
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
SetupIconFile=..\Resources\AudioPanel.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; 面板是托盘程序，由下方 [Code] 统一结束进程，不用重启管理器
CloseApplications=no
OutputDir=..\artifacts\installer
OutputBaseFilename=AudioPanel-Setup-{#AppVersion}

[Languages]
Name: "chs"; MessagesFile: "compiler:Default.isl,ChineseSimplified.isl"

[Tasks]
Name: "startup"; Description: "登录 Windows 后在托盘自动运行（不会自动切换设备）"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; 必须与 StartupService.Command 完全一致，托盘菜单里的“开机启动”勾选才会正确显示
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "AudioPanel"; \
    ValueData: """{app}\{#AppExe}"" --startup"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "立即打开{#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
procedure StopPanel;
var
  ResultCode: Integer;
begin
  // 安装或卸载前结束正在运行的面板（含托盘），否则 exe 被占用无法覆盖
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopPanel;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // 没选开机启动时，清掉旧版或测试版留下的启动项
  if (CurStep = ssPostInstall) and not WizardIsTaskSelected('startup') then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AudioPanel');
end;

function InitializeUninstall: Boolean;
begin
  StopPanel;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // 托盘菜单里开启的启动项不在安装记录里，卸载时一并删除；场景配置保留在 %LOCALAPPDATA%\AudioPanel
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AudioPanel');
end;
