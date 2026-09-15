param([string]$OutputRoot)

# 本地开发专项：启动独立窗口与真实媒体，绝不调用 Windows CI、发布验收或部署。
# 和普通开发检查分开运行，因为本场景依赖交互式桌面及原生输出设备。
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputRoot) { $OutputRoot = Join-Path $repositoryRoot ('TestResults/UX1/native-' + [guid]::NewGuid().ToString('N')) }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw '请使用新的结果目录。' }
$null = New-Item -ItemType Directory -Path $OutputRoot
Push-Location $repositoryRoot
try {
    dotnet restore src/VideoSecurityPlayer.Standalone/VideoSecurityPlayer.Standalone.csproj --locked-mode -p:SkipPluginDeploy=true
    if ($LASTEXITCODE -ne 0) { throw '独立窗口依赖恢复失败。' }
    dotnet build src/VideoSecurityPlayer.Standalone/VideoSecurityPlayer.Standalone.csproj -c Debug --no-restore -warnaserror -p:SkipPluginDeploy=true
    if ($LASTEXITCODE -ne 0) { throw '独立窗口 Debug 构建失败。' }
    $executable = Join-Path $repositoryRoot 'src/VideoSecurityPlayer.Standalone/bin/Debug/net10.0/VideoSecurityPlayer.Standalone.exe'
    $media = Join-Path $repositoryRoot 'tests/VideoSecurityPlayer.Tests/TestAssets/RealMedia/synthetic-multitrack-subtitles.mp4'
    $report = Join-Path $OutputRoot 'report.json'
    $arguments = @('--smoke-media', ('"' + $media + '"'), '--smoke-report', ('"' + $report + '"'), '--smoke-ux1')
    $process = Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $OutputRoot 'stdout.log') -RedirectStandardError (Join-Path $OutputRoot 'stderr.log')
    try {
        if (-not $process.WaitForExit(60000)) { $process.Kill(); throw '本地原生验证超过 60 秒，已终止本次验证进程。' }
        if (-not (Test-Path -LiteralPath $report)) { throw '未生成原生验证报告。' }
        $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        if ($process.ExitCode -ne 0 -or -not $result.Passed) { throw "原生验证失败，详见 $report" }
        Write-Host "UX1 本地原生验证通过：$report"
    }
    finally { $process.Dispose() }
}
finally { Pop-Location }
