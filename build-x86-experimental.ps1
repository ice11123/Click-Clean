param([Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference = 'Stop'
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$version = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
& (Join-Path $PSScriptRoot 'build-assets.ps1')
$publish = Join-Path $OutputRoot 'Click-Clean-x86-Experimental'
$arguments = 'publish App\ClickClean.csproj -c Release -r win-x86 --self-contained true --nologo -o "' + $publish + '"'
$job = Start-Process dotnet -ArgumentList $arguments -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -RedirectStandardOutput (Join-Path $OutputRoot 'publish-x86.log') -RedirectStandardError (Join-Path $OutputRoot 'publish-x86.err') -PassThru
if (-not $job.WaitForExit(240000)) { Stop-Process -Id $job.Id -ErrorAction SilentlyContinue; throw '32位构建超时。' }
if ($job.ExitCode -ne 0) { throw '32位构建失败，检查publish-x86日志。' }
foreach ($name in @('LICENSE','BRANDING.md','SECURITY.md','THIRD-PARTY-NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $publish }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\x86-experimental.md') -Destination (Join-Path $publish 'README-x86.md')
$runtimeInfo = Get-Content -LiteralPath (Join-Path $publish 'ClickClean.runtimeconfig.json') -Raw | ConvertFrom-Json
$nugetCache = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages'
$notices = Join-Path $publish 'runtime-notices'; New-Item -ItemType Directory -Path $notices -Force | Out-Null
foreach ($framework in $runtimeInfo.runtimeOptions.includedFrameworks) {
    $packageRoot = Join-Path (Join-Path $nugetCache ($framework.name.ToLowerInvariant() + '.runtime.win-x86')) $framework.version
    foreach ($notice in Get-ChildItem -LiteralPath $packageRoot -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)' }) {
        Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $notices ($framework.name + '-' + $notice.Name))
    }
}
$archivePath = Join-Path $OutputRoot ('Click-Clean-' + $version + '-win-x86-Experimental.zip')
if (Test-Path -LiteralPath $archivePath) { throw '输出包已存在，请使用新输出目录。' }
[IO.Compression.ZipFile]::CreateFromDirectory($publish, $archivePath)
Write-Output "已完成32位试验便携包：$archivePath"
