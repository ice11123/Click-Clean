# 3.0.3 安装账户回归

日期：2026-10-17。

## 故障复现与根因

将3.0.2原有CurrentSid函数原样提取到`installer/AccountIdentity.iss`，正式安装器和不安装任何文件的检查程序共用该函数。本机Inno Setup 7编译并运行原实现，得到与用户截图相同的`Error=安装账户标识无效。`。

原调用`Process.GetOwnerSid(Sid)`返回成功，但Pascal Script动态COM调用没有将SID回写到变量。只将这一调用改为`Process.ExecMethod_('GetOwnerSid')`，读取输出对象的ReturnValue和Sid，保留返回码及SID前缀校验。相同账户、相同检查程序重新运行后得到有效SID，与独立Windows进程令牌SID一致；无需放宽校验或修改用户账户。

微软[SWbemObject.ExecMethod_说明](https://learn.microsoft.com/en-us/windows/win32/wmisdk/swbemobject-execmethod-)明确支持通过返回对象取得方法输出参数，适用于不支持直接输出参数的脚本调用。

## 自动化方法

`installer/tests/test-account-identity.ps1`编译并实际运行与正式安装器共享函数的Inno脚本，与WindowsIdentity当前令牌对照。检查程序仅输出测试文件，在InitializeSetup主动返回False，不进入安装流程，不修改任务、注册表、程序或设置；因此退出码1不是回归失败，以实际身份对照为准。编译与运行各有30秒上限，旧结果存在时拒绝复用。

本机普通权限检查已通过。测试加入Windows CI和完整安装包构建门禁；不下载执行脚本，不忽略账户识别失败。核心、界面与更新测试继续运行，但不能替代这一安装器运行时检查。

## 验收边界

完整安装包发布前另行进行用户UAC授权的本机覆盖安装，记录安装日志、程序版本、系统组件ACL、登录任务及偏好保留情况。上述只读检查本身不证明安装覆盖或卸载成功；未授权或失败时不将其标记为通过。不同账户授权、不同系统、卸载及真实重启仍需分别验证。

本次未改变内存整理引擎或系统组件协议，不执行本机全系统内存清理，也不宣称性能收益。3.0.2的其他回归范围见[上版验证](verification-3.0.2.md)。
