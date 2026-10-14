# 平台支持与试验范围

正式推荐版本为 Windows 11 x64。32位包指x86进程运行在64位Windows 11的WOW64环境，不是32位Windows系统版本。

| 平台 | 构建与发布 | 验证边界 |
| --- | --- | --- |
| Windows 11 x64 | 正式安装/便携/更新包 | 核心模拟、WPF界面与更新测试；本轮不重做全系统整理 |
| Windows 11 x64上的x86进程 | 独立试验便携包 | 本机可做32位启动、界面及只读探针；不提供高级系统组件 |
| Linux / macOS | 当前不能直接打包发布 | 现有WPF界面及kernel32、user32、ntdll等API不可直接迁移 |

[Microsoft WPF说明](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)明确WPF仅在Windows运行；[Windows位数说明](https://support.microsoft.com/en-au/windows/experience/compatibility/32-bit-and-64-bit-windows-frequently-asked-questions)说明Windows 11没有32位版本。

32位窗口样式读取改用GetWindowLongW，依据[官方GetWindowLongPtrW文档](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowlongptrw)。原生头文件的32位宏映射不会自动应用到C#的DllImport。

试验包将数据放在独立目录，关闭登录任务入口和x64更新通道，避免改写已安装正式版。常规用户仍应使用x64正式版。没有创建Linux/macOS空壳安装包或将Windows程序重命名后伪装成跨平台版。

真正跨平台需要替换WPF、为各系统重新实现内存读数与权限/整理策略，并在对应系统上核对行为。Windows的工作集/修改页/待机三步不能机械照搬到Linux或macOS；移植应先明确各平台实际可实现的功能，再选择GUI框架和测试环境。
