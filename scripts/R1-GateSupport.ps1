# 门禁只负责解释命令与证据，不包含播放器业务。本文件可被无外部依赖的脚本测试加载，
# 使“缺报告也通过”“退出码丢失”等错误有独立回归保护。
Set-StrictMode -Version Latest

function Write-R1Json {
    param([string]$Path, [object]$Value)
    $directory = Split-Path -Parent ([IO.Path]::GetFullPath($Path))
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding utf8
}

function Read-R1Evidence {
    param([string]$Path)
    if (!(Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -eq 0) {
        throw "必需证据缺失或为空：$Path"
    }
    Get-Content -LiteralPath $Path -Raw
}

function Assert-R1Trx {
    param([string]$Path, [string]$AllowedSkippedTest = '')
    [xml]$report = Read-R1Evidence $Path
    $counter = $report.SelectSingleNode('//*[local-name()="ResultSummary"]/*[local-name()="Counters"]')
    if (!$counter) { throw "TRX 缺少计数器：$Path" }
    foreach ($name in @('total', 'executed', 'passed', 'failed', 'notExecuted')) {
        if (!$counter.HasAttribute($name)) { throw "TRX 缺少 $name：$Path" }
    }
    foreach ($name in @('error', 'timeout', 'aborted', 'inconclusive', 'notRunnable', 'disconnected', 'inProgress', 'pending')) {
        if ($counter.HasAttribute($name) -and [int]$counter.GetAttribute($name) -ne 0) {
            throw "TRX 运行未正常完成：$name，$Path"
        }
    }
    $results = @($report.SelectNodes('//*[local-name()="UnitTestResult"]'))
    $skipped = @($results | Where-Object { $_.outcome -eq 'NotExecuted' })
    $invalid = @($results | Where-Object { $_.outcome -notin @('Passed', 'NotExecuted') })
    if ([int]$counter.total -le 0 -or [int]$counter.executed -le 0 -or
        [int]$counter.passed -le 0 -or [int]$counter.failed -ne 0 -or $invalid.Count -gt 0 -or
        $results.Count -ne [int]$counter.total -or
        [int]$counter.executed -ne [int]$counter.passed -or
        [int]$counter.total -ne ([int]$counter.passed + $skipped.Count) -or
        [int]$counter.notExecuted -notin @(0, $skipped.Count)) {
        throw "TRX 存在失败、未完成或计数不一致：$Path"
    }
    # VSTest 的 xUnit 适配器可能把 skipped 只写入 UnitTestResult，不累计 notExecuted。
    # 明细是执行事实源：总数必须等于 passed + 明确的 NotExecuted，不能因此放过缺失结果。
    foreach ($test in $skipped) {
        if (!$AllowedSkippedTest -or $test.testName -notlike "*$AllowedSkippedTest*") {
            throw "必需测试未执行：$($test.testName)"
        }
    }
    [pscustomobject]@{total=[int]$counter.total; passed=[int]$counter.passed; skipped=$skipped.Count}
}

function Assert-R1JsonPassed {
    param([string]$Path, [string]$Property = 'success')
    $report = Read-R1Evidence $Path | ConvertFrom-Json
    $value = $report
    foreach ($part in $Property.Split('.')) {
        $member = $value.PSObject.Properties[$part]
        if (!$member) { throw "JSON 缺少验收字段 $Property：$Path" }
        $value = $member.Value
    }
    if ($value -isnot [bool] -or !$value) { throw "验收报告未通过：$Path ($Property)" }
}

function Invoke-R1Command {
    param([string]$FilePath, [string[]]$Arguments, [string]$LogPath)
    # 子进程独立退出码必须在任何后续命令之前捕获。日志保留具体诊断，控制台只输出步骤摘要。
    & $FilePath @Arguments *> $LogPath
    $commandExitCode = $LASTEXITCODE
    if ($commandExitCode -ne 0) {
        Get-Content -LiteralPath $LogPath -Tail 20 | Write-Host
        throw "子命令失败，退出码 $commandExitCode；日志：$LogPath"
    }
}

function Assert-R1Coverage {
    param([string]$Path, [string]$PolicyPath)
    [xml]$report = Read-R1Evidence $Path
    $policy = Read-R1Evidence $PolicyPath | ConvertFrom-Json
    $culture = [Globalization.CultureInfo]::InvariantCulture
    # Cobertura 的 DOCTYPE 与根元素同名；用 DOM 根节点读取，避免 PowerShell 属性适配器
    # 把文档类型声明和元素一起返回，导致严格模式把合法报告误判为缺少属性。
    $root = $report.DocumentElement
    if (!$root -or $root.LocalName -ne 'coverage') { throw '缺少 Cobertura 根元素。' }
    $line = [Math]::Round(100 * [double]::Parse($root.GetAttribute('line-rate'), $culture), 2)
    $branch = [Math]::Round(100 * [double]::Parse($root.GetAttribute('branch-rate'), $culture), 2)
    if (![double]::IsFinite($line) -or ![double]::IsFinite($branch) -or $line -gt 100 -or $branch -gt 100 -or
        $line -lt $policy.line -or $branch -lt $policy.branch) {
        throw "覆盖率低于 R1 基线：行 $line%，分支 $branch%"
    }
    $componentResults = @()
    foreach ($component in $policy.components) {
        # SourceLink 可把根目录替换成 /_/，因此按仓库内完整相对文件后缀定位。
        # 同一源码行可能出现在多个生成类型中，合并行号后取最高命中，防止重复计算分母。
        $classes = @($report.SelectNodes('//class') | Where-Object {
            $_.filename.Replace('\', '/').EndsWith('/' + $component.file, [StringComparison]::Ordinal)
        })
        $lines = @($classes | ForEach-Object { $_.SelectNodes('./lines/line') } | Group-Object number)
        if ($lines.Count -eq 0) { throw "新增组件缺少覆盖率证据：$($component.file)" }
        $hit = 0; $coveredBranches = 0; $totalBranches = 0
        foreach ($group in $lines) {
            if (@($group.Group | Where-Object { [int]$_.hits -gt 0 }).Count -gt 0) { $hit++ }
            $branchLines = @($group.Group | Where-Object { $_.GetAttribute('branch') -eq 'true' })
            $bestCovered = 0; $bestTotal = 0
            foreach ($item in $branchLines) {
                if ($item.GetAttribute('condition-coverage') -notmatch '\((\d+)/(\d+)\)') {
                    throw "组件分支报告格式无效：$($component.file):$($group.Name)"
                }
                if ([int]$Matches[1] -gt [int]$Matches[2]) { throw '覆盖分支数超过总分支数。' }
                $bestCovered = [Math]::Max($bestCovered, [int]$Matches[1])
                $bestTotal = [Math]::Max($bestTotal, [int]$Matches[2])
            }
            $coveredBranches += $bestCovered; $totalBranches += $bestTotal
        }
        $componentLine = [Math]::Round(100 * $hit / $lines.Count, 2)
        $componentBranch = if ($totalBranches -eq 0) { $null } else { [Math]::Round(100 * $coveredBranches / $totalBranches, 2) }
        if ($componentLine -lt $component.line -or ($null -ne $componentBranch -and $componentBranch -lt $component.branch)) {
            throw "组件覆盖率不足：$($component.file)，行 $componentLine%，分支 $componentBranch%"
        }
        $componentResults += [pscustomobject]@{file=$component.file; line=$componentLine; branch=$componentBranch}
    }
    [pscustomobject]@{line=$line; branch=$branch; components=$componentResults}
}
