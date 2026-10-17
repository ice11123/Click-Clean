function GetCurrentProcessId(): LongWord; external 'GetCurrentProcessId@kernel32.dll stdcall';
function CurrentSid(): String;
var Locator, Services, Process, Owner: Variant;
begin
  Locator := CreateOleObject('WbemScripting.SWbemLocator');
  Services := Locator.ConnectServer('', 'root\cimv2');
  Process := Services.Get('Win32_Process.Handle="' + IntToStr(GetCurrentProcessId()) + '"');
  // Pascal Script的动态COM调用不回写此输出参数；读取WMI显式返回的输出对象。
  Owner := Process.ExecMethod_('GetOwnerSid');
  if Owner.ReturnValue <> 0 then RaiseException('无法确定安装账户，未迁移旧启动任务。');
  Result := Owner.Sid;
  if Pos('S-1-5-', Result) <> 1 then RaiseException('安装账户标识无效。');
end;
