# 无 Pester 或额外包依赖的门禁回归；失败直接抛异常，聚合入口不会吞掉断言。
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../R1-GateSupport.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('video-r1-gate-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$testCount = 0
function Expect-Rejection([scriptblock]$Action) {
    $rejected = $false
    try { & $Action 6>$null | Out-Null } catch { $rejected = $true }
    if (!$rejected) { throw '无效证据被错误接受。' }
    $script:testCount++
}
try {
    $json = Join-Path $testRoot 'report.json'
    Expect-Rejection { Assert-R1JsonPassed $json }
    Set-Content -LiteralPath $json -Value ''
    Expect-Rejection { Assert-R1JsonPassed $json }
    foreach ($value in @(@{}, @{success=$false}, @{success='true'})) {
        Write-R1Json $json $value
        Expect-Rejection { Assert-R1JsonPassed $json }
    }
    Write-R1Json $json @{success=$true}
    Assert-R1JsonPassed $json
    $testCount++

    $trx = Join-Path $testRoot 'report.trx'
    $valid = '<TestRun><Results><UnitTestResult testName="one" outcome="Passed" /></Results><ResultSummary><Counters total="1" executed="1" passed="1" failed="0" notExecuted="0" /></ResultSummary></TestRun>'
    Set-Content -LiteralPath $trx -Value $valid
    $null = Assert-R1Trx $trx
    $testCount++
    foreach ($invalid in @('<TestRun />', $valid.Replace('total="1"', 'total="0"'), $valid.Replace('outcome="Passed"', 'outcome="Failed"'), $valid.Replace('passed="1"', 'passed="0"'), $valid.Replace('<Counters ', '<Counters aborted="1" '))) {
        Set-Content -LiteralPath $trx -Value $invalid
        Expect-Rejection { Assert-R1Trx $trx }
    }
    $skipped = $valid.Replace('</Results>', '<UnitTestResult testName="WorkflowActionG4IntegrationTests.test" outcome="NotExecuted" /></Results>').Replace('total="1"', 'total="2"').Replace('notExecuted="0"', 'notExecuted="1"')
    Set-Content -LiteralPath $trx -Value $skipped
    Expect-Rejection { Assert-R1Trx $trx }
    $null = Assert-R1Trx $trx 'WorkflowActionG4IntegrationTests'
    $testCount++
    Set-Content -LiteralPath $trx -Value ($skipped.Replace('notExecuted="1"', 'notExecuted="0"'))
    $null = Assert-R1Trx $trx 'WorkflowActionG4IntegrationTests'
    $testCount++
    Expect-Rejection { Assert-R1Trx $trx }

    $coverage = Join-Path $testRoot 'coverage.xml'
    $policy = Join-Path $testRoot 'policy.json'
    Write-R1Json $policy @{line=70; branch=50; components=@()}
    Set-Content -LiteralPath $coverage -Value '<coverage line-rate="0.69" branch-rate="0.90" />'
    Expect-Rejection { Assert-R1Coverage $coverage $policy }
    Set-Content -LiteralPath $coverage -Value '<coverage line-rate="0.90" branch-rate="0.49" />'
    Expect-Rejection { Assert-R1Coverage $coverage $policy }
    Set-Content -LiteralPath $coverage -Value '<coverage line-rate="NaN" branch-rate="0.90" />'
    Expect-Rejection { Assert-R1Coverage $coverage $policy }
    Set-Content -LiteralPath $coverage -Value '<coverage line-rate="1.50" branch-rate="0.90" />'
    Expect-Rejection { Assert-R1Coverage $coverage $policy }
    Set-Content -LiteralPath $coverage -Value '<coverage line-rate="0.90" branch-rate="0.90" />'
    $null = Assert-R1Coverage $coverage $policy
    $testCount++
    Set-Content -LiteralPath $coverage -Value '<!DOCTYPE coverage SYSTEM "http://cobertura.sourceforge.net/xml/coverage-04.dtd"><coverage line-rate="0.90" branch-rate="0.90" />'
    $null = Assert-R1Coverage $coverage $policy
    $testCount++
    Write-R1Json $policy @{line=70; branch=50; components=@(@{file='src/Rule.cs'; line=90; branch=80})}
    Expect-Rejection { Assert-R1Coverage $coverage $policy }
    $componentXml = '<coverage line-rate="0.90" branch-rate="0.90"><packages><package><classes><class filename="/_/src/Rule.cs"><lines><line number="1" hits="1" branch="true" condition-coverage="100% (2/2)" /></lines></class></classes></package></packages></coverage>'
    Set-Content -LiteralPath $coverage -Value $componentXml
    $null = Assert-R1Coverage $coverage $policy
    $testCount++
    Set-Content -LiteralPath $coverage -Value ($componentXml.Replace('(2/2)', '(1/2)'))
    Expect-Rejection { Assert-R1Coverage $coverage $policy }
    Set-Content -LiteralPath $coverage -Value ($componentXml.Replace('(2/2)', '(3/2)'))
    Expect-Rejection { Assert-R1Coverage $coverage $policy }
    Set-Content -LiteralPath $coverage -Value ($componentXml.Replace('hits="1"', 'hits="0"'))
    Expect-Rejection { Assert-R1Coverage $coverage $policy }

    # 真正启动失败的子进程，证明检查的是退出码，而不是日志里有没有“失败”字样。
    $shellPath = (Get-Process -Id $PID).Path
    Expect-Rejection { Invoke-R1Command $shellPath @('-NoProfile', '-Command', 'exit 7') (Join-Path $testRoot 'command.log') }
    # 从公开脚本入口验证 Full 的前置条件与落盘结果，后续步骤必须保持 notExecuted。
    $fullRoot = Join-Path $testRoot 'missing-full-inputs'
    $entry = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../Test-R1.ps1'))
    Expect-Rejection { Invoke-R1Command $shellPath @('-NoProfile', '-File', $entry, '-Mode', 'Full', '-OutputRoot', $fullRoot) (Join-Path $testRoot 'full.log') }
    $failedSummary = Read-R1Evidence (Join-Path $fullRoot 'summary.json') | ConvertFrom-Json
    if ($failedSummary.success -or $failedSummary.steps[0].status -ne 'failed' -or
        @($failedSummary.steps | Where-Object status -eq 'notExecuted').Count -ne ($failedSummary.steps.Count - 1)) {
        throw 'Full 缺少配置时错误记录了执行状态。'
    }
    $testCount++
    Write-Host "R1 门禁回归通过：$testCount 项。"
}
finally {
    # 只清理由本测试创建的已验证绝对目录，不跨 Shell 拼接删除命令。
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (!$resolvedTestRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        !(Split-Path -Leaf $resolvedTestRoot).StartsWith('video-r1-gate-')) { throw '测试清理路径越界。' }
    Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
}
