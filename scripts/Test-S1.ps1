param([string]$OutputRoot)
# 仅本地 Debug 开发门禁。只构建明确列出的项目，不遍历 Solution，也不调用发布/部署 target。
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'S1GateHelpers.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputRoot) { $OutputRoot = Join-Path $repository ('TestResults/S1/dev-' + [guid]::NewGuid().ToString('N')) }
$OutputRoot = New-S1OutputRoot $OutputRoot
$started = [datetime]::UtcNow
$names = @('gate-selftest', 'Tests', 'UiTests', 'coverage', 'full-layout', 'thin-layout', 'full-to-thin')
$steps = @($names | ForEach-Object { [ordered]@{ name = $_; status = 'notExecuted'; evidence = $null } })
$summary = [ordered]@{ status = 'running'; configuration = 'Debug'; startedUtc = $started; source = ''; steps = $steps; failure = $null }
$current = $null
Push-Location $repository
try {
    $summary.source = (& git rev-parse HEAD).Trim()
    $current = $steps[0]; $current.status = 'running'
    Invoke-S1Command (Get-Process -Id $PID).Path @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'tests/Test-S1Gate.ps1')) (Join-Path $OutputRoot 'gate-selftest') $repository
    $current.status = 'passed'
    foreach ($suite in @('Tests', 'UiTests')) {
        $current = $steps | Where-Object { $_.name -eq $suite }; $current.status = 'running'
        $project = "tests/VideoSecurityPlayer.$suite/VideoSecurityPlayer.$suite.csproj"
        Write-Host "S1：$suite 恢复、Debug 编译及测试"
        Invoke-S1Command dotnet @('restore', $project, '--locked-mode', '-p:SkipPluginDeploy=true') (Join-Path $OutputRoot "$suite-restore") $repository
        Invoke-S1Command dotnet @('build', $project, '-c', 'Debug', '--no-restore', '-warnaserror', '-p:SkipPluginDeploy=true') (Join-Path $OutputRoot "$suite-build") $repository
        $suiteRoot = Join-Path $OutputRoot $suite
        Invoke-S1Command dotnet @('test', $project, '-c', 'Debug', '--no-build', '--no-restore', '--settings', 'tests/coverage.runsettings',
            '--collect:XPlat Code Coverage', '--results-directory', $suiteRoot, '--logger', "trx;LogFileName=$suite.trx") (Join-Path $OutputRoot "$suite-test") $repository
        $current.evidence = Assert-S1Trx (Join-Path $suiteRoot "$suite.trx") 0 $started
        $current.status = 'passed'
    }
    $current = $steps[3]; $current.status = 'running'
    $components = @((Get-Content tests/ux1-coverage-policy.json -Raw | ConvertFrom-Json).components) + @((Get-Content tests/s1-coverage-policy.json -Raw | ConvertFrom-Json).components)
    $current.evidence = [ordered]@{
        Tests = Assert-S1Coverage (Join-Path $OutputRoot 'Tests') $components $started
        UiTests = Assert-S1Coverage (Join-Path $OutputRoot 'UiTests') @() $started
    }
    $current.status = 'passed'
    $standalone = 'src/VideoSecurityPlayer.Standalone/VideoSecurityPlayer.Standalone.csproj'
    foreach ($layout in @('full', 'thin', 'full-to-thin')) {
        $index = if ($layout -eq 'full') { 4 } elseif ($layout -eq 'thin') { 5 } else { 6 }
        $current = $steps[$index]; $current.status = 'running'
        $folder = if ($layout -eq 'full-to-thin') { 'full' } else { $layout }
        $artifacts = Join-Path $OutputRoot $folder
        $include = if ($layout -eq 'full') { 'true' } else { 'false' }
        $properties = @('-p:SkipPluginDeploy=true', '-p:UseArtifactsOutput=true', "-p:ArtifactsPath=$artifacts", "-p:IncludeLibVlcRuntime=$include")
        Write-Host "S1：$layout 隔离输出"
        Invoke-S1Command dotnet (@('restore', $standalone, '--locked-mode') + $properties) (Join-Path $OutputRoot "$layout-restore") $repository
        Invoke-S1Command dotnet (@('build', $standalone, '-c', 'Debug', '--no-restore', '-warnaserror') + $properties) (Join-Path $OutputRoot "$layout-build") $repository
        $current.evidence = @(
            Assert-S1Layout (Join-Path $artifacts 'bin/VideoSecurityPlayer.Plugin/debug') ($include -eq 'true')
            Assert-S1Layout (Join-Path $artifacts 'bin/VideoSecurityPlayer.Standalone/debug') ($include -eq 'true')
        )
        $current.status = 'passed'
    }
    $summary.status = 'passed'
}
catch {
    if ($null -ne $current) { $current.status = 'failed' }
    $summary.status = 'failed'; $summary.failure = $_.Exception.Message
    throw
}
finally {
    $summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutputRoot 'summary.json') -Encoding utf8
    Pop-Location
}
Write-Host "S1 本地开发检查通过：$OutputRoot"
