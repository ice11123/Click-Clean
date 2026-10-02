#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef AppVersion
  #error AppVersion is required
#endif
[Setup]
AppId={{AB7B0ADB-A046-44BD-9EAA-1769A04A7591}
AppName=Click-Clean 即清
AppVersion={#AppVersion}
AppPublisher=ice11123
AppPublisherURL=https://github.com/ice11123/Click-Clean
AppSupportURL=https://github.com/ice11123/Click-Clean/issues
DefaultDirName={localappdata}\Programs\ClickClean
DefaultGroupName=Click-Clean 即清
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
WizardStyle=modern dynamic windows11
SetupIconFile=..\App\Assets\ClickClean.ico
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\current\ClickClean.exe
OutputDir={#OutputDir}
OutputBaseFilename=Click-Clean-{#AppVersion}-win-x64-Setup
Compression=lzma2
SolidCompression=yes
DisableProgramGroupPage=yes
CloseApplications=no
AppMutex=Local\ClickClean.InstallationActive
RestartApplications=no
CreateUninstallRegKey=no
UsePreviousAppDir=no

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked
[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{userprograms}\Click-Clean 即清"; Filename: "{app}\current\ClickClean.exe"; WorkingDir: "{app}\current"
Name: "{userdesktop}\Click-Clean 即清"; Filename: "{app}\current\ClickClean.exe"; WorkingDir: "{app}\current"; Tasks: desktopicon
[Registry]
Root: HKCU; Subkey: "Software\ClickClean"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClickClean_is1"; ValueType: string; ValueName: "DisplayName"; ValueData: "Click-Clean 即清"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClickClean_is1"; ValueType: string; ValueName: "DisplayVersion"; ValueData: "{#AppVersion}"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClickClean_is1"; ValueType: string; ValueName: "Publisher"; ValueData: "ice11123"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClickClean_is1"; ValueType: string; ValueName: "InstallLocation"; ValueData: "{app}"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClickClean_is1"; ValueType: string; ValueName: "DisplayIcon"; ValueData: "{app}\current\ClickClean.exe"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClickClean_is1"; ValueType: string; ValueName: "UninstallString"; ValueData: """{app}\unins000.exe"""
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClickClean_is1"; ValueType: string; ValueName: "QuietUninstallString"; ValueData: """{app}\unins000.exe"" /VERYSILENT"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClickClean_is1"; ValueType: dword; ValueName: "NoModify"; ValueData: "1"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClickClean_is1"; ValueType: dword; ValueName: "NoRepair"; ValueData: "1"
[Run]
Filename: "{app}\current\ClickClean.exe"; Description: "启动 Click-Clean 即清"; Flags: nowait postinstall skipifsilent
[UninstallRun]
Filename: "{app}\current\ClickClean.exe"; Parameters: "--remove-startup"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveStartupTask"
[UninstallDelete]
Type: files; Name: "{app}\packages\.betaId"
Type: files; Name: "{app}\packages\ClickClean-*-full.nupkg"
Type: files; Name: "{app}\packages\ClickClean-*-delta.nupkg"
Type: files; Name: "{app}\packages\ClickClean-*.nupkg.partial"
Type: dirifempty; Name: "{app}\packages"
[Code]
var DeleteUserData: Boolean;
function InitializeSetup(): Boolean;
var Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  Result := (Version.Major = 10) and (Version.Build >= 22000) and (Version.ProductType = VER_NT_WORKSTATION);
  if not Result then SuppressibleMsgBox('Click-Clean 仅支持 Windows 11 x64。', mbCriticalError, MB_OK, IDOK);
end;
function InitializeUninstall(): Boolean;
begin
  DeleteUserData := False;
  if not UninstallSilent then
    DeleteUserData := MsgBox('同时删除当前账户的即清设置、历史和诊断日志？选择“否”可保留。', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  Result := True;
end;
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var DataDir: String;
begin
  if (CurUninstallStep = usPostUninstall) and DeleteUserData then begin
    DataDir := ExpandConstant('{localappdata}\ClickCleanData');
    DeleteFile(DataDir + '\settings.json'); DeleteFile(DataDir + '\settings.json.tmp');
    DeleteFile(DataDir + '\history.json'); DeleteFile(DataDir + '\history.json.tmp');
    DeleteFile(DataDir + '\diagnostic.log'); DeleteFile(DataDir + '\diagnostic.log.previous');
    DeleteFile(DataDir + '\migration.json'); DeleteFile(DataDir + '\update-state.json');
    DeleteFile(DataDir + '\update-state.json.tmp');
    RemoveDir(DataDir);
  end;
end;
