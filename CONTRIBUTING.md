# 贡献指南

请先阅读 README、SECURITY 与 docs/architecture.md。行为改变附上相应测试和使用说明；UI 调整说明浅色、深色及键盘表现。

开发环境为 Windows 11 x64、.NET SDK 10.0.301 或允许的补丁版本。运行 `build-assets.ps1` 生成图标，再执行 `dotnet run --project Tests/ClickClean.Tests.csproj -c Release`。完整构建见 README。

C# 使用有意义的命名、4 空格缩进，XAML 和项目文件 2 空格。说明与注释优先简体中文，API 名称保留原文。让清理引擎、触发策略、更新服务和 UI 保持各自职责。

CI 使用模拟 API，真实系统整理仅在明确选择 Diagnostics 并确认管理员权限后测试。不提交用户日志、凭据、签名密钥或构建缓存。

贡献意味着你有权提交相关内容，并同意本项目范围内的贡献以 GPL-3.0-only 发布。保留第三方声明；不接受未经授权复制的资源。贡献者保留其版权。

PR 说明问题、修改后的行为与验证结果；不要机械为每个视觉数值添加断言。
