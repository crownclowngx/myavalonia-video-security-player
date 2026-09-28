# 纯合成证据自测，绝不借道 R1 或 Host 发布检查。临时目录归本次测试所有。
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../S1GateHelpers.ps1')
$root = New-S1OutputRoot (Join-Path ([IO.Path]::GetTempPath()) ('s1-gate-' + [guid]::NewGuid().ToString('N')))
$count = 0
function Reject([string]$Name, [scriptblock]$Operation) {
    $rejected = $false
    try { $null = & $Operation } catch { $rejected = $true }
    if (-not $rejected) { throw "错误地接受了负例：$Name" }
    $script:count++
}
function Write-Fixture([string]$Path, [string]$Content) {
    $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($Path)) -Force
    [IO.File]::WriteAllText($Path, $Content)
}
try {
    Reject '重复输出根' { New-S1OutputRoot $root }
    $trx = Join-Path $root 'valid.trx'
    $valid = '<TestRun><Results><UnitTestResult outcome="Passed" /></Results><ResultSummary><Counters total="1" passed="1" failed="0" notExecuted="0" /></ResultSummary></TestRun>'
    Write-Fixture $trx $valid
    $null = Assert-S1Trx $trx 0; $count++
    Reject '进程非零但报告通过' { Assert-S1Trx $trx 1 }
    Reject '缺少TRX' { Assert-S1Trx (Join-Path $root 'missing.trx') 0 }
    foreach ($bad in @('', '<broken', '<TestRun/>', $valid.Replace('total="1"', 'total="0"'),
        $valid.Replace('passed="1"', 'passed="0"'), $valid.Replace('notExecuted="0"', 'notExecuted="1"'),
        $valid.Replace('outcome="Passed"', 'outcome="Failed"'), $valid.Replace('failed="0"', 'failed="1"'),
        $valid.Replace('<Counters', '<Counters aborted="1"'))) {
        Write-Fixture $trx $bad
        Reject '无效TRX' { Assert-S1Trx $trx 0 }
    }
    Write-Fixture $trx $valid
    (Get-Item -LiteralPath $trx).LastWriteTimeUtc = [datetime]::UtcNow.AddDays(-1)
    Reject '旧TRX' { Assert-S1Trx $trx 0 ([datetime]::UtcNow) }

    $suite = Join-Path $root 'coverage'
    $report = Join-Path $suite ('00000000-0000-0000-0000-000000000001/coverage.cobertura.xml')
    $coverage = '<coverage line-rate="1" branch-rate="1"><packages><package><classes><class name="Rule" line-rate="1" branch-rate="1" /></classes></package></packages></coverage>'
    $policy = @([pscustomobject]@{ name = 'Rule'; line = 90; branch = 80 })
    Write-Fixture $report $coverage
    $null = Assert-S1Coverage $suite $policy; $count++
    (Get-Item -LiteralPath $report).LastWriteTimeUtc = [datetime]::UtcNow.AddDays(-1)
    Reject '旧覆盖率' { Assert-S1Coverage $suite $policy ([datetime]::UtcNow) }
    Write-Fixture $report $coverage
    $attachment = Join-Path $suite 'run/In/machine/coverage.cobertura.xml'
    Write-Fixture $attachment $coverage
    $null = Assert-S1Coverage $suite $policy; $count++
    Write-Fixture $attachment ($coverage.Replace('line-rate="1"', 'line-rate="0.9"'))
    Reject '附件与根报告不同' { Assert-S1Coverage $suite $policy }
    Remove-Item -LiteralPath $attachment
    $duplicate = Join-Path $suite '00000000-0000-0000-0000-000000000002/coverage.cobertura.xml'
    Write-Fixture $duplicate $coverage
    Reject '两个独立报告即使内容相同' { Assert-S1Coverage $suite $policy }
    Remove-Item -LiteralPath $duplicate
    foreach ($bad in @('NaN', 'Infinity', '-0.1', '1.1', 'bad', '')) {
        Write-Fixture $report ($coverage.Replace('line-rate="1"', "line-rate=`"$bad`""))
        Reject '非法行覆盖率' { Assert-S1Coverage $suite $policy }
        Write-Fixture $report ($coverage.Replace('branch-rate="1"', "branch-rate=`"$bad`""))
        Reject '非法分支覆盖率' { Assert-S1Coverage $suite $policy }
    }
    Write-Fixture $report ($coverage.Replace('name="Rule"', 'name="Missing"'))
    Reject '缺失组件' { Assert-S1Coverage $suite $policy }
    Write-Fixture $report ($coverage.Replace('</classes>', '<class name="Rule" line-rate="1" branch-rate="1" /></classes>'))
    Reject '重复组件' { Assert-S1Coverage $suite $policy }
    Write-Fixture $report ($coverage.Replace('line-rate="1"', 'line-rate="0.89"'))
    Reject '不足行覆盖率' { Assert-S1Coverage $suite $policy }
    Write-Fixture $report ($coverage.Replace('branch-rate="1"', 'branch-rate="0.79"'))
    Reject '不足分支覆盖率' { Assert-S1Coverage $suite $policy }
    Write-Fixture $report '<broken'
    Reject '损坏覆盖率' { Assert-S1Coverage $suite $policy }
    Remove-Item -LiteralPath $report
    Reject '缺失覆盖率' { Assert-S1Coverage $suite $policy }

    $layout = Join-Path $root 'layout'
    foreach ($file in @('VideoSecurityPlayer.Plugin.dll', 'LibVLCSharp.dll', 'LibVLCSharp.Avalonia.dll')) { Write-Fixture (Join-Path $layout $file) 'fixture' }
    $null = Assert-S1Layout $layout $false; $count++
    Reject '完整输出缺原生库' { Assert-S1Layout $layout $true }
    $native = Join-Path $layout 'native/win-x64/libvlc'
    $files = @('libvlc.dll', 'libvlccore.dll', 'plugins/demux/libmp4_plugin.dll', 'plugins/demux/libmkv_plugin.dll',
        'plugins/codec/libavcodec_plugin.dll', 'plugins/video_output/libdirect3d11_plugin.dll', 'plugins/audio_output/libmmdevice_plugin.dll')
    foreach ($file in $files) { Write-Fixture (Join-Path $native $file) 'fixture' }
    $null = Assert-S1Layout $layout $true; $count++
    Reject '精简输出有原生残留' { Assert-S1Layout $layout $false }
    foreach ($file in $files) {
        Remove-Item -LiteralPath (Join-Path $native $file)
        Reject "完整输出缺失 $file" { Assert-S1Layout $layout $true }
        Write-Fixture (Join-Path $native $file) 'fixture'
    }
    Remove-Item -LiteralPath (Join-Path $layout 'LibVLCSharp.dll')
    Reject '缺托管桥接' { Assert-S1Layout $layout $true }
    Reject '真实子进程失败不能被文字通过覆盖' {
        Invoke-S1Command (Get-Process -Id $PID).Path @('-NoProfile', '-Command', 'Write-Output passed; exit 7') (Join-Path $root 'process') $root
    }
    Reject '超时结束本次子进程' {
        Invoke-S1Command (Get-Process -Id $PID).Path @('-NoProfile', '-Command', 'Start-Sleep -Seconds 10') (Join-Path $root 'timeout') $root 1
    }
    Write-Host "S1 脚本自测通过：$count 项"
}
finally {
    # 只清除此脚本创建的、位于系统临时目录的 GUID 子树，绝不接收调用方删除路径。
    $full = [IO.Path]::GetFullPath($root)
    $temp = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath())) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notmatch '^s1-gate-[0-9a-f]{32}$') { throw '拒绝清理未知目录。' }
    Remove-Item -LiteralPath $full -Recurse -Force
}
