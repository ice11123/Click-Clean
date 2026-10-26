#ifndef OutputDir
  #error OutputDir is required
#endif
[Setup]
AppId=ClickClean-AuthorizedTaskCheck
AppName=Click-Clean 整理授权任务检查
AppVersion=1.0
DefaultDirName={tmp}\ClickClean-AuthorizedTaskCheck
CreateAppDir=no
Uninstallable=no
CreateUninstallRegKey=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
OutputDir={#OutputDir}
OutputBaseFilename=AuthorizedTaskCheck
[Code]
#include "..\AccountIdentity.iss"
#include "..\AuthorizedTask.iss"
function InitializeSetup(): Boolean;
var Output, Action: String;
begin
  Output := ExpandConstant('{param:ResultFile|}'); Action := ExpandConstant('{param:Action|}');
  if Output = '' then RaiseException('缺少检查结果路径。');
  try
    if Action = 'register' then RegisterAuthorizedTask()
    else if Action = 'remove' then RemoveAuthorizedTask()
    else RaiseException('无效检查动作。');
    SaveStringToFile(Output, 'Passed', False);
  except
    SaveStringToFile(Output, 'Error=' + GetExceptionMessage(), False);
  end;
  Result := False;
end;
