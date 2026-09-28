param([string]$OutputRoot, [ValidateSet('Playback', 'Coexistence')][string]$Scenario = 'Playback', [string]$MusicPluginDirectory)
# 本地原生专项：每个来源/重启步骤启动新进程；共存则在该进程内创建两个私有托管桥接。
# 所有副本、配置、媒体和日志都在新的 OutputRoot 内，绝不读取或改写正式安装的 Common。
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'S1GateHelpers.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($Scenario -eq 'Coexistence') {
    if (-not $MusicPluginDirectory) { throw '共存验证需要明确指定包含音乐插件与 LibVLCSharp.dll 的开发输出目录。' }
    $MusicPluginDirectory = [IO.Path]::GetFullPath($MusicPluginDirectory)
    Assert-S1File (Join-Path $MusicPluginDirectory 'MusicNetEasePlugin.Plugin.dll')
    Assert-S1File (Join-Path $MusicPluginDirectory 'LibVLCSharp.dll')
}
if (-not $OutputRoot) { $OutputRoot = Join-Path $repository ('TestResults/S1/native-' + [guid]::NewGuid().ToString('N')) }
$OutputRoot = New-S1OutputRoot $OutputRoot
$cases = if ($Scenario -eq 'Playback') { @('common', 'bundled', 'custom', 'missing', 'frozen', 'restart', 'load-failure', 'failure-restart') } else { @('music-first', 'video-first') }
$steps = @($cases | ForEach-Object { [ordered]@{ name = $_; status = 'notExecuted'; report = "$_.json" } })
$summary = [ordered]@{ status = 'running'; configuration = 'Debug'; source = ''; scenario = $Scenario; build = 'notExecuted'; steps = $steps; failure = $null }
$current = $null
$app = Join-Path $OutputRoot 'app'
function Move-OwnedRuntime([string]$From, [string]$To) {
    $boundary = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($app)) + [IO.Path]::DirectorySeparatorChar
    foreach ($path in @($From, $To)) {
        if (-not [IO.Path]::GetFullPath($path).StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw '拒绝移动隔离应用之外的目录。' }
    }
    if (Test-Path -LiteralPath $From) { Move-Item -LiteralPath $From -Destination $To }
}
Push-Location $repository
try {
    $summary.source = (& git rev-parse HEAD).Trim()
    $artifacts = Join-Path $OutputRoot 'build'
    $project = 'src/VideoSecurityPlayer.Standalone/VideoSecurityPlayer.Standalone.csproj'
    $properties = @('-p:SkipPluginDeploy=true', '-p:UseArtifactsOutput=true', "-p:ArtifactsPath=$artifacts", '-p:IncludeLibVlcRuntime=true')
    $summary.build = 'running'
    Invoke-S1Command dotnet (@('restore', $project, '--locked-mode') + $properties) (Join-Path $OutputRoot 'restore') $repository
    Invoke-S1Command dotnet (@('build', $project, '-c', 'Debug', '--no-restore', '-warnaserror') + $properties) (Join-Path $OutputRoot 'build') $repository
    $built = Join-Path $artifacts 'bin/VideoSecurityPlayer.Standalone/debug'
    $null = Assert-S1Layout $built $true
    $summary.build = 'passed'
    Copy-Item -LiteralPath $built -Destination $app -Recurse
    Set-Content -LiteralPath (Join-Path $app '.s1-native-owned') -Value 'S1 isolated native development copy'
    $bundled = Join-Path $app 'native/win-x64/libvlc'
    $common = Join-Path $app 'Common/libvlc'
    $null = New-Item -ItemType Directory -Path (Join-Path $app 'Common')
    Copy-Item -LiteralPath $bundled -Destination $common -Recurse
    $custom = Join-Path $OutputRoot 'custom-libvlc'
    Copy-Item -LiteralPath $bundled -Destination $custom -Recurse
    $musicCopy = $null
    if ($Scenario -eq 'Coexistence') {
        $musicCopy = Join-Path $OutputRoot 'music-private'
        $null = New-Item -ItemType Directory -Path $musicCopy
        # 只装载播放所需的两个私有程序集，其余平台框架沿用默认上下文；不会触碰音乐登录态。
        foreach ($file in @('MusicNetEasePlugin.Plugin.dll', 'LibVLCSharp.dll')) { Copy-Item -LiteralPath (Join-Path $MusicPluginDirectory $file) -Destination $musicCopy }
        Get-ChildItem -LiteralPath $musicCopy -File | Get-FileHash | Select-Object Path, Hash |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputRoot 'music-inputs.json')
    }
    foreach ($step in $steps) {
        $current = $step; $step.status = 'running'; $case = $step.name
        Write-Host "S1 原生：$case"
        # 恢复两个候选后按场景隐藏，移动仅限已核对的隔离输出子树。
        Move-OwnedRuntime "$common.disabled" $common
        Move-OwnedRuntime "$bundled.disabled" $bundled
        if ($case -in @('bundled', 'missing')) { Move-OwnedRuntime $common "$common.disabled" }
        if ($case -eq 'missing') { Move-OwnedRuntime $bundled "$bundled.disabled" }
        $dataName = if ($case -eq 'restart') { 'frozen' } elseif ($case -eq 'failure-restart') { 'load-failure' } else { $case }
        $expected = if ($case -in @('custom', 'restart', 'failure-restart')) { 'Custom' } elseif ($case -eq 'bundled') { 'Bundled' } else { 'Common' }
        $report = Join-Path $OutputRoot "$case.json"
        $config = [ordered]@{ Scenario = $case; DataRoot = Join-Path $OutputRoot "data/$dataName"; ReportPath = $report;
            MediaRoot = Join-Path $repository 'tests/VideoSecurityPlayer.Tests/TestAssets/RealMedia'; ExpectedSource = $expected;
            CustomDirectory = $custom; MusicPluginDirectory = $musicCopy }
        $configFile = Join-Path $OutputRoot "$case.config.json"
        $config | ConvertTo-Json | Set-Content -LiteralPath $configFile -Encoding utf8
        $started = [datetime]::UtcNow
        Invoke-S1Command (Join-Path $app 'VideoSecurityPlayer.Standalone.exe') @('--smoke-s1-config', $configFile) (Join-Path $OutputRoot $case) $app 90
        Assert-S1File $report $started
        $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        if ($result.Passed -ne $true -or $result.Scenario -ne $case -or @($result.Checks).Count -lt 2) { throw "原生报告不完整或失败：$report" }
        $step.status = 'passed'
    }
    $summary.status = 'passed'
}
catch {
    if ($null -ne $current) { $current.status = 'failed' }
    if ($summary.build -eq 'running') { $summary.build = 'failed' }
    $summary.status = 'failed'; $summary.failure = $_.Exception.Message
    throw
}
finally {
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputRoot 'summary.json') -Encoding utf8
    Pop-Location
}
Write-Host "S1 原生开发验证通过：$OutputRoot"
