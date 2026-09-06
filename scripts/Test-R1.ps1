param(
    [ValidateSet('Fast', 'Full')][string]$Mode = 'Fast',
    [string]$HostRepositoryRoot,
    [string]$WorkflowStudioRoot,
    [string]$PerformanceBaseline = '',
    [string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'R1-GateSupport.ps1')
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$OutputRoot) { $OutputRoot = Join-Path $repositoryRoot ('TestResults/R1/run-' + [Guid]::NewGuid().ToString('N')) }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw 'R1 门禁必须使用新的证据目录，不能复用旧报告。' }
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
$stepNames = @('preconditions', 'script-tests', 'restore', 'build', 'unit-ui')
if ($Mode -eq 'Full') { $stepNames += @('coverage', 'host-workflow', 'phase4', 'standalone', 'memory', 'performance') }
$summary = [ordered]@{
    schemaVersion=1; mode=$Mode; success=$false; startedUtc=[DateTimeOffset]::UtcNow.ToString('o')
    steps=@($stepNames | ForEach-Object { [pscustomobject]@{name=$_; status='notExecuted'; detail=''; seconds=0} })
}

function Invoke-Step([string]$Name, [scriptblock]$Action) {
    $step = $summary.steps | Where-Object name -eq $Name
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Write-Host "R1：$Name 开始"
    try { & $Action; $step.status = 'passed' }
    catch { $step.status = 'failed'; $step.detail = $_.Exception.Message; throw }
    finally {
        $step.seconds = [Math]::Round($watch.Elapsed.TotalSeconds, 2)
        Write-R1Json (Join-Path $OutputRoot 'summary.json') $summary
    }
    Write-Host "R1：$Name 通过"
}

function Invoke-Dotnet([string[]]$CommandArguments, [string]$LogName) {
    Invoke-R1Command 'dotnet' $CommandArguments (Join-Path $OutputRoot "$LogName.log")
}

Push-Location $repositoryRoot
try {
    Invoke-Step 'preconditions' {
        if ($Mode -eq 'Full') {
            if (!$HostRepositoryRoot -or !$WorkflowStudioRoot -or !$PerformanceBaseline) {
                throw 'Full 需要 HostRepositoryRoot、WorkflowStudioRoot 和同环境 PerformanceBaseline。'
            }
            foreach ($path in @(
                (Join-Path $HostRepositoryRoot 'Host/MyAvaloniaManagement/MyAvaloniaManagement.csproj'),
                (Join-Path $WorkflowStudioRoot 'src/WorkflowStudio.Plugin/WorkflowStudio.Plugin.csproj'),
                'tests/VideoSecurityPlayer.Tests/TestAssets/RealMedia/synthetic-av-short.mp4')) {
                if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "门禁输入缺失：$path" }
            }
            Assert-R1JsonPassed $PerformanceBaseline 'hardGatePassed'
            if (![OperatingSystem]::IsWindows() -or ![Environment]::UserInteractive -or ![Environment]::Is64BitProcess) {
                throw 'Full 需要交互式 Windows x64 会话。'
            }
        }
        # 只记录受版本控制或未忽略源码的摘要，不收集用户文件、密码或环境变量内容。
        $sourceFiles = @(git ls-files --cached --others --exclude-standard)
        if ($LASTEXITCODE -ne 0 -or $sourceFiles.Count -eq 0) { throw '无法采集源码清单。' }
        $hashes = @($sourceFiles | Sort-Object -Unique | ForEach-Object {
            if (Test-Path -LiteralPath $_ -PathType Leaf) { @{path=$_; sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash} }
        })
        Write-R1Json (Join-Path $OutputRoot 'source.json') @{revision=(git rev-parse HEAD); status=@(git status --short); sdk=(dotnet --version); files=$hashes}
    }
    Invoke-Step 'script-tests' { & (Join-Path $PSScriptRoot 'tests/Test-R1Gate.ps1') }
    Invoke-Step 'restore' { Invoke-Dotnet @('restore', 'VideoSecurityPlayer.slnx', '--locked-mode') 'restore' }
    Invoke-Step 'build' { Invoke-Dotnet @('build', 'VideoSecurityPlayer.slnx', '-c', 'Release', '--no-restore', '-warnaserror', '-p:SkipPluginDeploy=true') 'build' }
    Invoke-Step 'unit-ui' {
        foreach ($suite in @('Tests', 'UiTests')) {
            $resultRoot = Join-Path $OutputRoot $suite
            Invoke-Dotnet @('test', "tests/VideoSecurityPlayer.$suite/VideoSecurityPlayer.$suite.csproj", '-c', 'Release', '--no-build', '--no-restore', '--logger', "trx;LogFileName=$suite.trx", '--results-directory', $resultRoot) $suite
            $null = Assert-R1Trx (Join-Path $resultRoot "$suite.trx")
        }
    }
    if ($Mode -eq 'Full') {
        Invoke-Step 'coverage' {
            $coverageRoot = Join-Path $OutputRoot 'coverage'
            & (Join-Path $PSScriptRoot 'Test-Coverage.ps1') -HostRepositoryRoot $HostRepositoryRoot -OutputRoot $coverageRoot *> (Join-Path $OutputRoot 'coverage.log')
            foreach ($suite in @('Tests', 'UiTests', 'HostTests', 'HostUiTests')) {
                # 四套覆盖率运行中只允许这个需要实体 ZIP 的场景跳过；下一步必须实际执行它。
                $allowed = if ($suite -eq 'HostTests') { 'WorkflowActionG4IntegrationTests' } else { '' }
                $null = Assert-R1Trx (Join-Path $coverageRoot "$suite/$suite.trx") $allowed
            }
            $metrics = Assert-R1Coverage (Join-Path $coverageRoot 'merged/Cobertura.xml') 'tests/r1-coverage-policy.json'
            Write-R1Json (Join-Path $OutputRoot 'coverage-summary.json') $metrics
        }
        Invoke-Step 'host-workflow' {
            $hostEvidence = Join-Path $OutputRoot 'host'
            & (Join-Path $PSScriptRoot 'Test-HostIntegration.ps1') -HostRepositoryRoot $HostRepositoryRoot -WorkflowStudioRoot $WorkflowStudioRoot -OutputRoot $hostEvidence *> (Join-Path $OutputRoot 'host.log')
            foreach ($file in @('host-package.trx', 'host-ui.trx', 'workflow.trx')) { $null = Assert-R1Trx (Join-Path $hostEvidence $file) }
        }
        Invoke-Step 'phase4' {
            $reportPath = Join-Path $OutputRoot 'phase4/report.json'
            Invoke-Dotnet @('run', '--project', 'tools/VideoSecurityPlayer.Playback.IntegrationHarness', '-c', 'Release', "-p:HostRepositoryRoot=$HostRepositoryRoot", '-p:SkipPluginDeploy=true', '--', '--suite', 'phase4', '--cycles', '20', '--report', $reportPath) 'phase4'
            Assert-R1JsonPassed $reportPath
            Assert-R1JsonPassed (Join-Path $OutputRoot 'phase4/phase4-g3.json')
            Assert-R1JsonPassed (Join-Path $OutputRoot 'phase4/phase4-g8.json')
        }
        Invoke-Step 'standalone' {
            $reportPath = Join-Path $OutputRoot 'standalone.json'
            Invoke-Dotnet @('run', '--project', 'src/VideoSecurityPlayer.Standalone', '-c', 'Release', '--no-build', '--', '--smoke-media', 'tests/VideoSecurityPlayer.Tests/TestAssets/RealMedia/synthetic-av-short.mp4', '--smoke-report', $reportPath) 'standalone'
            Assert-R1JsonPassed $reportPath 'passed'
        }
        Invoke-Step 'memory' {
            $reportPath = Join-Path $OutputRoot 'memory.json'
            Invoke-Dotnet @('run', '--project', 'tools/VideoSecurityPlayer.ReleaseAcceptance', '-c', 'Release', '--no-build', '--', '--memory', '--report', $reportPath) 'memory'
            Assert-R1JsonPassed $reportPath 'passed'
        }
        Invoke-Step 'performance' {
            $prefix = @('run', '--project', 'tools/VideoSecurityPlayer.SecurityBenchmarks', '-c', 'Release', '--no-build', '--')
            $aggregateArguments = @('--g10-aggregate')
            foreach ($round in 1..3) {
                $reportPath = Join-Path $OutputRoot "performance-$round.json"
                Invoke-Dotnet ($prefix + @('--suite', 'g10', '--output', $reportPath)) "performance-$round"
                Assert-R1JsonPassed $reportPath 'hardGate.passed'
                $aggregateArguments += @('--input', $reportPath)
            }
            $candidate = Join-Path $OutputRoot 'performance-candidate.json'
            Invoke-Dotnet ($prefix + $aggregateArguments + @('--output', $candidate)) 'performance-aggregate'
            Assert-R1JsonPassed $candidate 'hardGatePassed'
            $comparison = Join-Path $OutputRoot 'performance-comparison.json'
            Invoke-Dotnet ($prefix + @('--g10-compare', '--baseline', $PerformanceBaseline, '--candidate', $candidate, '--output', $comparison)) 'performance-compare'
            Assert-R1JsonPassed $comparison 'comparable'
            Assert-R1JsonPassed $comparison 'passed'
        }
    }
    $summary.success = $true
}
catch { Write-Host "R1 门禁失败：$($_.Exception.Message)"; throw }
finally {
    $summary.finishedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Write-R1Json (Join-Path $OutputRoot 'summary.json') $summary
    Pop-Location
    Write-Host "R1 证据：$OutputRoot"
}
