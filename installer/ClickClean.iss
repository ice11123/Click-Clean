#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PrivilegedDir
  #error PrivilegedDir is required
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
UninstallFilesDir={autopf}\Click-Clean\Uninstall
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
Name: "english"; MessagesFile: "compiler:Default.isl"
[CustomMessages]
chinesesimp.DesktopIcon=创建桌面快捷方式
english.DesktopIcon=Create a desktop shortcut
chinesesimp.Shortcuts=快捷方式：
english.Shortcuts=Shortcuts:
chinesesimp.Launch=启动 Click-Clean 即清
english.Launch=Launch Click-Clean
chinesesimp.Unsupported=Click-Clean 仅支持 Windows 11 x64。
english.Unsupported=Click-Clean supports Windows 11 x64 only.
chinesesimp.DeleteData=同时删除当前账户的即清设置、历史和诊断日志？选择“否”可保留。
english.DeleteData=Also delete this account's Click-Clean settings, history and diagnostic logs? Choose No to keep them.
chinesesimp.UnsafeLink=系统组件目录不能是重解析链接：
english.UnsafeLink=The system component path cannot be a reparse link:
chinesesimp.AclFailed=系统组件目录ACL设置失败。
english.AclFailed=Failed to protect the system component's directory permissions.
chinesesimp.OwnerFailed=系统组件目录所有权设置失败。
english.OwnerFailed=Failed to protect the system component's ownership.
chinesesimp.DirectoryFailed=无法创建受保护的系统组件目录。
english.DirectoryFailed=Cannot create the protected system component directory.
[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:Shortcuts}"; Flags: unchecked
[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PrivilegedDir}\ClickClean.Privileged.exe"; DestDir: "{autopf}\Click-Clean\Privileged"; Flags: ignoreversion
[Icons]
Name: "{userprograms}\Click-Clean 即清"; Filename: "{app}\current\ClickClean.exe"; WorkingDir: "{app}\current"
Name: "{userdesktop}\Click-Clean 即清"; Filename: "{app}\current\ClickClean.exe"; WorkingDir: "{app}\current"; Tasks: desktopicon
[Registry]
Root: HKCU; Subkey: "Software\ClickClean"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\ClickClean"; ValueType: string; ValueName: "InitialLanguage"; ValueData: "{code:LanguageCode}"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClickClean_is1"; ValueType: string; ValueName: "DisplayName"; ValueData: "Click-Clean 即清"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClickClean_is1"; ValueType: string; ValueName: "DisplayVersion"; ValueData: "{#AppVersion}"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClickClean_is1"; ValueType: string; ValueName: "Publisher"; ValueData: "ice11123"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClickClean_is1"; ValueType: string; ValueName: "InstallLocation"; ValueData: "{app}"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClickClean_is1"; ValueType: string; ValueName: "DisplayIcon"; ValueData: "{app}\current\ClickClean.exe"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClickClean_is1"; ValueType: string; ValueName: "UninstallString"; ValueData: """{uninstallexe}"""
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClickClean_is1"; ValueType: string; ValueName: "QuietUninstallString"; ValueData: """{uninstallexe}"" /VERYSILENT"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClickClean_is1"; ValueType: dword; ValueName: "NoModify"; ValueData: "1"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClickClean_is1"; ValueType: dword; ValueName: "NoRepair"; ValueData: "1"
[Run]
Filename: "{app}\current\ClickClean.exe"; Parameters: "{code:LanguageArgument}"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent runasoriginaluser
[UninstallDelete]
Type: files; Name: "{app}\packages\.betaId"
Type: files; Name: "{app}\packages\ClickClean-*-full.nupkg"
Type: files; Name: "{app}\packages\ClickClean-*-delta.nupkg"
Type: files; Name: "{app}\packages\ClickClean-*.nupkg.partial"
Type: dirifempty; Name: "{app}\packages"
Type: dirifempty; Name: "{autopf}\Click-Clean\Privileged"
Type: dirifempty; Name: "{autopf}\Click-Clean"
[Code]
var DeleteUserData: Boolean;
function GetCurrentProcessId(): LongWord; external 'GetCurrentProcessId@kernel32.dll stdcall';
function GetFileAttributesW(Path: String): LongWord; external 'GetFileAttributesW@kernel32.dll stdcall';
function LanguageCode(Param: String): String;
begin
  if ActiveLanguage = 'english' then Result := 'en-US' else Result := 'zh-CN';
end;
function LanguageArgument(Param: String): String;
begin
  Result := '--language=' + LanguageCode(Param);
end;
function CurrentSid(): String;
var Locator, Services, Process, Sid: Variant;
begin
  Locator := CreateOleObject('WbemScripting.SWbemLocator');
  Services := Locator.ConnectServer('', 'root\cimv2');
  Process := Services.Get('Win32_Process.Handle="' + IntToStr(GetCurrentProcessId()) + '"');
  Sid := '';
  if Process.GetOwnerSid(Sid) <> 0 then RaiseException('无法确定安装账户，未迁移旧启动任务。');
  Result := Sid;
  if Pos('S-1-5-', Result) <> 1 then RaiseException('安装账户标识无效。');
end;
procedure RemoveLegacyTasks(PreserveEnabled: Boolean);
var Service, Folder, Tasks, Task: Variant; Sid, TaskName: String; I: Integer;
begin
  Sid := CurrentSid();
  Service := CreateOleObject('Schedule.Service'); Service.Connect(); Folder := Service.GetFolder('\');
  Tasks := Folder.GetTasks(1);
  for I := Tasks.Count downto 1 do begin
    Task := Tasks.Item(I); TaskName := Task.Name;
    if (TaskName = 'ClickClean-' + Sid) or (TaskName = 'Qingqi-' + Sid) or (TaskName = 'ClickClean-Handoff-' + Sid) then begin
      if PreserveEnabled and (TaskName = 'ClickClean-' + Sid) and Task.Enabled then RegWriteDWordValue(HKCU, 'Software\ClickClean', 'StartupMigrationEnabled', 1);
      // 不运行用户可写目录内的旧程序来提权删除任务。
      Folder.DeleteTask(TaskName, 0);
    end;
  end;
end;
procedure CheckNotReparse(Path: String);
var Attr: LongWord;
begin
  Attr := GetFileAttributesW(Path);
  if (Attr <> $FFFFFFFF) and ((Attr and $400) <> 0) then RaiseException(CustomMessage('UnsafeLink') + ' ' + Path);
end;
procedure CheckProtectedTree(Path: String);
var FindRec: TFindRec;
begin
  CheckNotReparse(Path);
  if FindFirst(Path + '\*', FindRec) then begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then begin
          CheckNotReparse(Path + '\' + FindRec.Name);
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then CheckProtectedTree(Path + '\' + FindRec.Name);
        end;
      until not FindNext(FindRec);
    finally FindClose(FindRec); end;
  end;
end;
procedure ResetProtectedAcl(Path: String; Recursive: Boolean);
var ExitCode: Integer; Suffix: String;
begin
  Suffix := ''; if Recursive then Suffix := ' /T';
  if not Exec(ExpandConstant('{sys}\icacls.exe'), '"' + Path + '" /reset' + Suffix + ' /C', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then RaiseException(CustomMessage('AclFailed'));
  if not Exec(ExpandConstant('{sys}\icacls.exe'), '"' + Path + '" /setowner "*S-1-5-32-544"' + Suffix + ' /C', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then RaiseException(CustomMessage('OwnerFailed'));
end;
procedure ProtectSystemComponent();
var Root, Component, Uninstaller: String;
begin
  Root := ExpandConstant('{autopf}\Click-Clean'); Component := Root + '\Privileged'; Uninstaller := Root + '\Uninstall';
  CheckNotReparse(ExpandConstant('{autopf}')); CheckNotReparse(Root); CheckProtectedTree(Component);
  CheckProtectedTree(Uninstaller);
  if not ForceDirectories(Component) or not ForceDirectories(Uninstaller) then RaiseException(CustomMessage('DirectoryFailed'));
  // 恢复Program Files继承ACL，再将所有权交给管理员；不授予Users/Everyone写入。
  ResetProtectedAcl(Root, False); ResetProtectedAcl(Component, True); ResetProtectedAcl(Uninstaller, True);
end;
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then begin RemoveLegacyTasks(True); ProtectSystemComponent(); end;
  if CurStep = ssPostInstall then ProtectSystemComponent();
end;
function InitializeSetup(): Boolean;
var Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  Result := (Version.Major = 10) and (Version.Build >= 22000) and (Version.ProductType = VER_NT_WORKSTATION);
  if not Result then SuppressibleMsgBox(CustomMessage('Unsupported'), mbCriticalError, MB_OK, IDOK);
end;
function InitializeUninstall(): Boolean;
begin
  RemoveLegacyTasks(False);
  DeleteUserData := False;
  if not UninstallSilent then
    DeleteUserData := MsgBox(CustomMessage('DeleteData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
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
