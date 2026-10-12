param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot 'dist'),
    [string]$Version,
    [string]$InnoCompiler,
    [string]$SigningThumbprint,
    [string]$SignToolPath,
    [string]$TimestampUrl,
    [switch]$SkipInstaller,
    [switch]$IncludePreview
)
$ErrorActionPreference = 'Stop'
if (-not $Version) { $Version = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw '版本必须是 x.y.z。' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
function Run-Tool([string]$Tool, [string]$Arguments, [string]$LogName, [int]$Seconds = 240) {
    $job = Start-Process -FilePath $Tool -ArgumentList $Arguments -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -RedirectStandardOutput (Join-Path $OutputRoot $LogName) -RedirectStandardError (Join-Path $OutputRoot ($LogName + '.err')) -PassThru
    if (-not $job.WaitForExit($Seconds * 1000)) { Stop-Process -Id $job.Id -ErrorAction SilentlyContinue; throw "构建工具超时，请检查 $LogName。" }
    if ($job.ExitCode -ne 0) { throw "构建失败，请检查 $LogName。" }
}
& (Join-Path $PSScriptRoot 'build-assets.ps1')
Run-Tool 'dotnet' 'tool restore' 'tools.log'
Run-Tool 'dotnet' 'run --project Tests\ClickClean.Tests.csproj -c Release' 'tests.log' 90
$publish = Join-Path $OutputRoot 'publish'
Run-Tool 'dotnet' ('publish App\ClickClean.csproj -c Release -r win-x64 --self-contained true --nologo -p:Version=' + $Version + ' -o "' + $publish + '"') 'publish.log'
$signParams = ''
if ($SigningThumbprint) {
    if ($SigningThumbprint -notmatch '^[0-9a-fA-F]{40}$') { throw '签名证书指纹必须为40位十六进制字符。' }
    $signingCertificate = Get-Item -LiteralPath ('Cert:\CurrentUser\My\' + $SigningThumbprint)
    $usageOids = @($signingCertificate.Extensions | Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } | ForEach-Object { foreach ($oid in $_.EnhancedKeyUsages) { $oid.Value } })
    if (-not $signingCertificate.HasPrivateKey -or $signingCertificate.NotAfter -lt (Get-Date) -or $usageOids -notcontains '1.3.6.1.5.5.7.3.3') { throw '需要有效且包含私钥的代码签名证书。' }
    if (-not $SignToolPath -or -not (Test-Path -LiteralPath $SignToolPath)) { throw '签名构建需提供 Windows SDK SignTool.exe 的真实路径。' }
    $timestampUri = $null
    if (-not [Uri]::TryCreate($TimestampUrl, [UriKind]::Absolute, [ref]$timestampUri) -or $timestampUri.Scheme -ne 'https' -or $TimestampUrl -match '[\s"`]' ) { throw '需提供证书服务商的 HTTPS 时间戳地址。' }
    $signParams = '/sha1 ' + $SigningThumbprint + ' /s My /fd SHA256 /tr ' + $TimestampUrl + ' /td SHA256'
    foreach ($name in @('ClickClean.exe','ClickClean.dll','ClickClean.Core.dll')) {
        Run-Tool $SignToolPath ('sign ' + $signParams + ' "' + (Join-Path $publish $name) + '"') ('sign-' + $name + '.log')
        if ((Get-AuthenticodeSignature -LiteralPath (Join-Path $publish $name)).Status -ne 'Valid') { throw "签名核验失败：$name" }
    }
    # Velopack 使用同一官方 SignTool 为自己的更新器与引导程序签名。
    $env:PATH = (Split-Path -Parent $SignToolPath) + [IO.Path]::PathSeparator + $env:PATH
}
foreach ($file in @('README.md','LICENSE','BRANDING.md','SECURITY.md','THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $publish
}
$notices = Join-Path $publish 'runtime-notices'
New-Item -ItemType Directory -Path $notices -Force | Out-Null
$runtimeInfo = Get-Content -LiteralPath (Join-Path $publish 'ClickClean.runtimeconfig.json') -Raw | ConvertFrom-Json
$nugetCache = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages'
foreach ($framework in $runtimeInfo.runtimeOptions.includedFrameworks) {
    $package = $framework.name.ToLowerInvariant() + '.runtime.win-x64'
    $packageRoot = Join-Path (Join-Path $nugetCache $package) $framework.version
    foreach ($notice in Get-ChildItem -LiteralPath $packageRoot -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)' }) {
        Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $notices ($framework.name + '-' + $notice.Name))
    }
}
$velopack = Join-Path $OutputRoot 'releases'
$packArguments = 'tool run vpk -- pack --packId ClickClean --packVersion ' + $Version + ' --packDir "' + $publish + '" --mainExe ClickClean.exe --packTitle "Click-Clean 即清" --packAuthors ice11123 --channel win --runtime win-x64 --shortcuts StartMenuRoot --icon App\Assets\ClickClean.ico --releaseNotes CHANGELOG.md --outputDir "' + $velopack + '"'
if ($signParams) { $packArguments += ' --signParams "' + $signParams + '"' }
Run-Tool 'dotnet' $packArguments 'pack.log'
$portable = Get-ChildItem -LiteralPath $velopack -Filter '*Portable.zip' | Select-Object -First 1
if (-not $portable) { throw '未找到 Velopack 便携包。' }
$portableTarget = Join-Path $OutputRoot ('Click-Clean-' + $Version + '-win-x64-Portable.zip')
Copy-Item -LiteralPath $portable.FullName -Destination $portableTarget -Force
$installStage = Join-Path $OutputRoot 'installer-stage'
Expand-Archive -LiteralPath $portableTarget -DestinationPath $installStage -Force
if (-not $SkipInstaller) {
    if (-not $InnoCompiler) {
        foreach ($candidate in @((Join-Path ${env:ProgramFiles} 'Inno Setup 7\ISCC.exe'),(Join-Path ${env:LOCALAPPDATA} 'Programs\Inno Setup 7\ISCC.exe'))) {
            if (Test-Path -LiteralPath $candidate) { $InnoCompiler = $candidate; break }
        }
    }
    if (-not $InnoCompiler) { throw '需要官方 Inno Setup 7 编译器；可先使用 -SkipInstaller。' }
    Run-Tool $InnoCompiler ('/DAppVersion=' + $Version + ' /DPublishDir="' + $installStage + '" /DOutputDir="' + $OutputRoot + '" installer\ClickClean.iss') 'installer.log'
    if ($signParams) {
        $installerFile = Join-Path $OutputRoot ('Click-Clean-' + $Version + '-win-x64-Setup.exe')
        Run-Tool $SignToolPath ('sign ' + $signParams + ' "' + $installerFile + '"') 'sign-installer.log'
        if ((Get-AuthenticodeSignature -LiteralPath $installerFile).Status -ne 'Valid') { throw '安装包签名核验失败。' }
    }
}
if ($IncludePreview) {
    $previewDir = Join-Path $OutputRoot 'Click-Clean-Preview'
    Run-Tool 'dotnet' ('publish Diagnostics\ClickClean.Preview.csproj -c Release -r win-x64 --self-contained true --nologo -o "' + $previewDir + '"') 'preview.log'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $previewDir
    Compress-Archive -LiteralPath $previewDir -DestinationPath (Join-Path $OutputRoot 'Click-Clean-Preview.zip') -Force
}
$source = Join-Path $OutputRoot ('Click-Clean-' + $Version + '-Source')
New-Item -ItemType Directory -Path $source -Force | Out-Null
$exclude = @('bin','obj','.tools','.git','dist')
Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File -Force | Where-Object {
    $parts = $_.FullName.Substring($PSScriptRoot.Length + 1).Split([IO.Path]::DirectorySeparatorChar)
    -not ($parts | Where-Object { $_ -in $exclude }) -and $_.Extension -notin '.ico','.log','.err','.pfx','.key','.p12' -and -not $_.FullName.StartsWith($OutputRoot + [IO.Path]::DirectorySeparatorChar)
} | ForEach-Object {
    $target = Join-Path $source $_.FullName.Substring($PSScriptRoot.Length + 1)
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $target
}
# ZipFile 保证包含 .github/.config 等可构建文件。
$sourceZip = Join-Path $OutputRoot ('Click-Clean-' + $Version + '-Source.zip')
$archiveTemporary = $sourceZip + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
[IO.Compression.ZipFile]::CreateFromDirectory($source, $archiveTemporary)
[IO.File]::Move($archiveTemporary, $sourceZip, $true)
$releaseAssets = @(Get-ChildItem -LiteralPath $OutputRoot -File | Where-Object { $_.Extension -in '.exe','.zip' })
$releaseAssets += @(Get-ChildItem -LiteralPath $velopack -File | Where-Object { $_.Extension -eq '.nupkg' -or $_.Name -eq 'releases.win.json' })
$checksums = $releaseAssets | ForEach-Object {
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name
}
[IO.File]::WriteAllLines((Join-Path $OutputRoot 'SHA256SUMS.txt'), $checksums, [Text.UTF8Encoding]::new($false))
Write-Output "已完成 Click-Clean $Version。"
