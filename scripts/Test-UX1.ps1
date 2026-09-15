param([string]$OutputRoot)

# UX1 开发检查仅运行本地 Debug 构建、单元与 Headless 界面测试。
# 发布打包、真实 Host 发布验收、Windows CI 和部署均不属于本脚本的执行范围。
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputRoot) {
    $OutputRoot = Join-Path $repositoryRoot ('TestResults/UX1/dev-' + [Guid]::NewGuid().ToString('N'))
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw '请使用新的结果目录，避免混入以前的测试证据。' }
$null = New-Item -ItemType Directory -Path $OutputRoot
$results = [Collections.Generic.List[object]]::new()
$summary = [ordered]@{ status = 'running'; configuration = 'Debug'; suites = $results; coverage = @(); failure = $null }

function Invoke-DevelopmentCommand([string]$Step, [string[]]$Arguments) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $OutputRoot "$Step.log")
    if ($LASTEXITCODE -ne 0) { throw "$Step 失败，退出码 $LASTEXITCODE" }
}

Push-Location $repositoryRoot
try {
    foreach ($suite in @('Tests', 'UiTests')) {
        $project = "tests/VideoSecurityPlayer.$suite/VideoSecurityPlayer.$suite.csproj"
        Invoke-DevelopmentCommand "$suite-restore" @('restore', $project, '--locked-mode', '-p:SkipPluginDeploy=true')
        Invoke-DevelopmentCommand "$suite-build" @('build', $project, '-c', 'Debug', '--no-restore', '-warnaserror', '-p:SkipPluginDeploy=true')
        $suiteRoot = Join-Path $OutputRoot $suite
        Invoke-DevelopmentCommand "$suite-test" @('test', $project, '-c', 'Debug', '--no-build', '--no-restore',
            '--settings', 'tests/coverage.runsettings', '--collect:XPlat Code Coverage',
            '--results-directory', $suiteRoot, '--logger', "trx;LogFileName=$suite.trx")
        [xml]$trx = Get-Content -LiteralPath (Join-Path $suiteRoot "$suite.trx") -Raw
        $counter = $trx.SelectSingleNode("//*[local-name()='Counters']")
        if ($null -eq $counter -or [int]$counter.total -eq 0 -or [int]$counter.passed -ne [int]$counter.total) {
            throw "$suite 必须包含实际执行的测试，且不允许失败或跳过。"
        }
        $results.Add([ordered]@{ suite = $suite; passed = [int]$counter.passed; total = [int]$counter.total })
    }

    # 本轮覆盖率与 R1 含 Host 的口径独立。纯规则类要求行 90%、分支 80%，
    # 全插件行/分支值只记录，不借用不同测试集合的阈值制造通过结论。
    $reports = @(Get-ChildItem -LiteralPath (Join-Path $OutputRoot 'Tests') -Filter 'coverage.cobertura.xml' -Recurse)
    if ($reports.Count -eq 0) { throw '缺少单元测试覆盖率文件。' }
    $hashes = @($reports | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName).Hash } | Sort-Object -Unique)
    if ($hashes.Count -ne 1) { throw '单元测试覆盖率存在不同内容的报告。' }
    [xml]$coverage = Get-Content -LiteralPath $reports[0].FullName -Raw
    $policy = Get-Content -LiteralPath 'tests/ux1-coverage-policy.json' -Raw | ConvertFrom-Json
    $measured = foreach ($component in $policy.components) {
        $nodes = @($coverage.SelectNodes('//class') | Where-Object { $_.name -eq $component.name })
        if ($nodes.Count -ne 1) { throw "覆盖率缺少或重复组件：$($component.name)" }
        $line = 100 * [double]$nodes[0].GetAttribute('line-rate')
        $branch = 100 * [double]$nodes[0].GetAttribute('branch-rate')
        if ($line -lt $component.line -or $branch -lt $component.branch) {
            throw "组件覆盖率不足：$($component.name)，行 $line%，分支 $branch%"
        }
        [ordered]@{ name = $component.name; line = $line; branch = $branch }
    }
    $summary.coverage = @($measured)
    $summary.status = 'passed'
}
catch {
    $summary.status = 'failed'
    $summary.failure = $_.Exception.Message
    throw
}
finally {
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputRoot 'summary.json') -Encoding utf8
    Pop-Location
}
Write-Output "UX1 本地开发检查通过：$OutputRoot"
