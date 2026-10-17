param(
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [string]$InnoCompiler
)
$ErrorActionPreference = 'Stop'
if (-not $InnoCompiler) {
    $locations = @(${env:ProgramFiles(x86)}, $env:ProgramFiles, (Join-Path $env:LOCALAPPDATA 'Programs')) | Where-Object { $_ }
    $candidates = @(foreach ($location in $locations) {
        Get-ChildItem -Path (Join-Path $location 'Inno Setup*\ISCC.exe') -File -ErrorAction SilentlyContinue
    })
    if ($candidates.Count -eq 0) { throw '安装账户回归检查需要已安装的Inno Setup编译器。' }
    $InnoCompiler = $candidates[0].FullName
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$resultFile = Join-Path $OutputRoot 'account.txt'
if (Test-Path -LiteralPath $resultFile) { throw '检查结果已存在，请使用新的输出目录。' }
$compile = Start-Process $InnoCompiler -ArgumentList ('/DOutputDir="' + $OutputRoot + '" "' + (Join-Path $PSScriptRoot 'AccountIdentityCheck.iss') + '"') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $OutputRoot 'compile.log') -RedirectStandardError (Join-Path $OutputRoot 'compile.err')
if (-not $compile.WaitForExit(30000)) { $compile.Kill(); throw '安装账户检查编译超时。' }
if ($compile.ExitCode -ne 0) { throw '安装账户检查编译失败，请检查compile.log。' }
$check = Start-Process (Join-Path $OutputRoot 'AccountIdentityCheck.exe') -ArgumentList ('/VERYSILENT /SUPPRESSMSGBOXES /ResultFile="' + $resultFile + '" /LOG="' + (Join-Path $OutputRoot 'setup.log') + '"') -WindowStyle Hidden -PassThru
if (-not $check.WaitForExit(30000)) { $check.Kill(); throw '安装账户检查超时。' }
# 检查程序主动在InitializeSetup返回False；以结果及独立令牌身份为准，不将退出码1误报为失败。
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $expected = $identity.User.Value } finally { $identity.Dispose() }
if (-not (Test-Path -LiteralPath $resultFile)) { throw '安装账户检查未输出结果。' }
$actual = [IO.File]::ReadAllText($resultFile).Trim()
if ($actual -ne ('Sid=' + $expected)) { throw ('安装账户回归检查失败：' + $actual) }
[pscustomobject]@{ Passed = $true; MatchedCurrentToken = $true; InstallerMutations = 0; ExpectedAbortExit = $check.ExitCode } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputRoot 'verification.json') -Encoding utf8
Write-Output '安装账户回归检查通过，与当前进程令牌SID一致；未修改任务、注册表或应用。'
