# 发行签名

2.4.0构建支持Authenticode签名，但开发机的当前用户及系统证书库没有可用代码签名证书，因此此次发行仍为未签名。没有购买、生成自签证书或更改系统信任设置。

维护者需准备当前用户My证书库中有效的代码签名证书（含私钥）、Windows SDK的SignTool.exe，以及证书服务商提供的HTTPS RFC3161时间戳地址。不要把私钥、PFX、密码或硬件令牌凭据提交到仓库。

```powershell
# 替换成实际证书指纹、已安装工具路径与服务商时间戳地址。
.\build.ps1 -SigningThumbprint YOUR_CERTIFICATE_THUMBPRINT `
  -SignToolPath 'YOUR_WINDOWS_SDK_SIGNTOOL_PATH' `
  -TimestampUrl 'YOUR_CERTIFICATE_PROVIDER_HTTPS_TIMESTAMP_URL'
```

流程检查代码签名用途、有效期与私钥；为应用exe/dll使用SHA-256及时间戳，核验Valid状态后交给Velopack打包。Velopack使用同一签名参数为更新组件签名，Inno外层安装包在生成后签名，再计算最终分发哈希。没有证书时保持普通构建；填写了签名参数但条件不满足时应失败，不能静默降级未签名。

实际签名分支尚未用真实证书执行；购入证书后需核验安装包、便携包内程序及更新后程序的证书链、时间戳和发布者身份。SignTool文档：[微软说明](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool)。签名与同源哈希用途不同，签名不能承诺消除所有杀毒误报。
