#ifndef OutputDir
  #error OutputDir is required
#endif
[Setup]
AppId=ClickClean-AccountIdentityCheck
AppName=Click-Clean 安装账户只读检查
AppVersion=1.0
DefaultDirName={tmp}\ClickClean-AccountIdentityCheck
CreateAppDir=no
Uninstallable=no
CreateUninstallRegKey=no
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=AccountIdentityCheck
[Code]
#include "..\AccountIdentity.iss"
function InitializeSetup(): Boolean;
var Output: String;
begin
  Output := ExpandConstant('{param:ResultFile|}');
  if Output = '' then RaiseException('缺少只读检查结果文件路径。');
  try
    SaveStringToFile(Output, 'Sid=' + CurrentSid(), False);
  except
    SaveStringToFile(Output, 'Error=' + GetExceptionMessage(), False);
  end;
  // 不进入安装流程，不修改任务、注册表、程序或用户设置。
  Result := False;
end;
