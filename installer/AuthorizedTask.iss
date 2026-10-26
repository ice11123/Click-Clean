function AuthorizedTaskName(): String;
begin
  Result := 'ClickClean-Privileged-' + CurrentSid();
end;
procedure ConfigureAuthorizedTask(Argument: String);
var Component: String; ExitCode: Integer;
begin
  Component := ExpandConstant('{autopf}\Click-Clean\Privileged\ClickClean.Privileged.exe');
  if not FileExists(Component) then RaiseException('系统整理组件未安装，无法配置授权任务。');
  // 只执行安装器已部署并保护的固定组件，不运行脚本或用户可写GUI。
  if not Exec(Component, Argument, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then
    RaiseException('系统整理授权配置失败，错误码：' + IntToStr(ExitCode));
end;
procedure RegisterAuthorizedTask();
begin
  ConfigureAuthorizedTask('--install-authorization');
end;
procedure RemoveAuthorizedTask();
var Service, Folder, Tasks, Task: Variant; I: Integer; Name: String;
begin
  if FileExists(ExpandConstant('{autopf}\Click-Clean\Privileged\ClickClean.Privileged.exe')) then begin
    ConfigureAuthorizedTask('--remove-authorization'); Exit;
  end;
  // 损坏安装的组件若已缺失，只删除当前SID的精确任务，不能扩大删除范围。
  Name := AuthorizedTaskName();
  Service := CreateOleObject('Schedule.Service'); Service.Connect(); Folder := Service.GetFolder('\');
  Tasks := Folder.GetTasks(1);
  for I := Tasks.Count downto 1 do begin
    Task := Tasks.Item(I);
    if Task.Name = Name then Folder.DeleteTask(Name, 0);
  end;
end;
