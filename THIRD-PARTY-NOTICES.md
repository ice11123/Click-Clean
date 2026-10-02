# 第三方声明

本项目源码为 GPL-3.0-only。第三方组件保留其原许可证。

## Velopack 1.2.158

项目：<https://github.com/velopack/velopack>。NuGet 作者：Velopack Ltd、Caelan Sayler、Kevin Bost。包元数据声明 MIT。

Copyright © Velopack Ltd. All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.

## .NET 与 Windows Desktop Runtime

自带运行时包含 Microsoft .NET、WPF 与 Windows Forms。实际版本见 `ClickClean.runtimeconfig.json` 和 `ClickClean.deps.json`。构建脚本从已还原的运行时包收集原许可证与第三方声明到 `runtime-notices`。

官方项目：<https://github.com/dotnet/runtime>、<https://github.com/dotnet/wpf>、<https://github.com/dotnet/winforms>。

## 构建工具与系统

Inno Setup 是单独的安装包编译器，遵循其 [官方许可证](https://jrsoftware.org/isinfo.php)，不随项目分发编译器。Windows API、系统字体及操作系统组件由 Windows 提供。未使用 PCL 或 Apple 名称、图标或反编译代码。
