# S1 本地开发证据校验。函数只解释本次报告及本次构建输出，不调用发布或部署入口。
Set-StrictMode -Version Latest

function New-S1OutputRoot([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (Test-Path -LiteralPath $resolved) { throw '结果目录已经存在，请使用全新目录。' }
    $null = New-Item -ItemType Directory -Path $resolved
    return $resolved
}

function Assert-S1File([string]$Path, [datetime]$NotBefore = [datetime]::MinValue) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "缺少证据：$Path" }
    $file = Get-Item -LiteralPath $Path
    if ($file.Length -eq 0 -or ($NotBefore -gt [datetime]::MinValue.AddDays(1) -and
        $file.LastWriteTimeUtc -lt $NotBefore.ToUniversalTime().AddSeconds(-2))) {
        throw "证据为空或不是本次生成：$Path"
    }
}

function Assert-S1Trx([string]$Path, [int]$ExitCode, [datetime]$NotBefore = [datetime]::MinValue) {
    if ($ExitCode -ne 0) { throw "测试进程退出码非零：$ExitCode" }
    Assert-S1File $Path $NotBefore
    [xml]$xml = Get-Content -LiteralPath $Path -Raw
    $counters = @($xml.SelectNodes("//*[local-name()='Counters']"))
    if ($counters.Count -ne 1) { throw 'TRX 必须恰有一组计数。' }
    $total = 0; $passed = 0
    if (-not [int]::TryParse($counters[0].GetAttribute('total'), [ref]$total) -or
        -not [int]::TryParse($counters[0].GetAttribute('passed'), [ref]$passed) -or $total -le 0 -or $passed -ne $total) {
        throw '测试必须非空且全部通过，不允许失败、跳过或中止。'
    }
    foreach ($name in @('failed', 'error', 'timeout', 'aborted', 'inconclusive', 'notExecuted', 'disconnected')) {
        $value = $counters[0].GetAttribute($name)
        if ($value -and $value -ne '0') { throw "TRX 存在非成功计数：$name" }
    }
    $results = @($xml.SelectNodes("//*[local-name()='UnitTestResult']"))
    if ($results.Count -ne $total -or @($results | Where-Object { $_.GetAttribute('outcome') -ne 'Passed' }).Count) {
        throw 'TRX 计数与逐项结果不一致。'
    }
    return [ordered]@{ total = $total; passed = $passed; failed = 0; skipped = 0; aborted = 0 }
}

function Get-S1Rate([string]$Value) {
    $number = 0.0
    if (-not [double]::TryParse($Value, [Globalization.NumberStyles]::Float,
        [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -or
        -not [double]::IsFinite($number) -or $number -lt 0 -or $number -gt 1) { throw "非法覆盖率：$Value" }
    return $number * 100
}

function Assert-S1Coverage([string]$SuiteRoot, [object[]]$Components, [datetime]$NotBefore = [datetime]::MinValue) {
    $reports = @(Get-ChildItem -LiteralPath $SuiteRoot -Filter coverage.cobertura.xml -Recurse)
    # VSTest 会把同一个 collector 报告再复制为 TRX 的 In 附件。只承认一个 GUID 根报告，
    # 附件必须位于已知的 In 子树且逐字节相同；两个独立 collector 报告即使相同也不能混用。
    $canonical = @($reports | Where-Object { [IO.Path]::GetRelativePath($SuiteRoot, $_.FullName) -match '^[0-9a-fA-F-]{36}[\\/]coverage\.cobertura\.xml$' })
    if ($canonical.Count -ne 1) { throw '覆盖率必须恰有一个本次 collector 根报告。' }
    Assert-S1File $canonical[0].FullName $NotBefore
    $hash = (Get-FileHash -LiteralPath $canonical[0].FullName).Hash
    foreach ($report in $reports) {
        if ($report.FullName -eq $canonical[0].FullName) { continue }
        $relative = [IO.Path]::GetRelativePath($SuiteRoot, $report.FullName)
        if ($relative -notmatch '^[^\\/]+[\\/]In[\\/][^\\/]+[\\/]coverage\.cobertura\.xml$' -or
            (Get-FileHash -LiteralPath $report.FullName).Hash -ne $hash) { throw '覆盖率有重复或混杂证据。' }
    }
    [xml]$xml = Get-Content -LiteralPath $canonical[0].FullName -Raw
    $overall = [ordered]@{ line = Get-S1Rate $xml.coverage.GetAttribute('line-rate'); branch = Get-S1Rate $xml.coverage.GetAttribute('branch-rate') }
    $measured = foreach ($component in $Components) {
        $nodes = @($xml.SelectNodes('//class') | Where-Object { $_.GetAttribute('name') -ceq $component.name })
        if ($nodes.Count -ne 1) { throw "覆盖率组件缺失或重复：$($component.name)" }
        $line = Get-S1Rate $nodes[0].GetAttribute('line-rate')
        $branch = Get-S1Rate $nodes[0].GetAttribute('branch-rate')
        if ($line -lt $component.line -or $branch -lt $component.branch) {
            throw "覆盖率不足：$($component.name)，行 $line%，分支 $branch%"
        }
        [ordered]@{ name = $component.name; line = $line; branch = $branch }
    }
    return [ordered]@{ report = [IO.Path]::GetRelativePath($SuiteRoot, $canonical[0].FullName); overall = $overall; components = @($measured) }
}

function Assert-S1Layout([string]$Directory, [bool]$IncludeNative) {
    foreach ($file in @('VideoSecurityPlayer.Plugin.dll', 'LibVLCSharp.dll', 'LibVLCSharp.Avalonia.dll')) {
        Assert-S1File (Join-Path $Directory $file)
    }
    $native = Join-Path $Directory 'native/win-x64/libvlc'
    if ($IncludeNative) {
        foreach ($file in @('libvlc.dll', 'libvlccore.dll', 'plugins/demux/libmp4_plugin.dll', 'plugins/demux/libmkv_plugin.dll',
            'plugins/codec/libavcodec_plugin.dll', 'plugins/video_output/libdirect3d11_plugin.dll', 'plugins/audio_output/libmmdevice_plugin.dll')) {
            Assert-S1File (Join-Path $native $file)
        }
    } elseif (Test-Path -LiteralPath $native) { throw '精简输出残留原生运行库目录。' }
    $nativeFiles = @(if (Test-Path -LiteralPath $native) { Get-ChildItem -LiteralPath $native -File -Recurse })
    $nativeBytes = 0L
    foreach ($file in $nativeFiles) { $nativeBytes += $file.Length }
    return [ordered]@{ directory = $Directory; includeNative = $IncludeNative; passed = $true;
        nativeFiles = $nativeFiles.Count; nativeBytes = $nativeBytes }
}

function Invoke-S1Command([string]$Executable, [string[]]$Arguments, [string]$LogPrefix,
    [string]$WorkingDirectory, [int]$TimeoutSeconds = 600) {
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $info.WindowStyle = 'Hidden'
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $info.WorkingDirectory = $WorkingDirectory
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $info
    try {
        $null = $process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true); $process.WaitForExit()
            [IO.File]::WriteAllText("$LogPrefix.stdout.log", $stdout.GetAwaiter().GetResult())
            [IO.File]::WriteAllText("$LogPrefix.stderr.log", $stderr.GetAwaiter().GetResult())
            throw "命令超时，已结束本次创建的进程树：$LogPrefix"
        }
        [IO.File]::WriteAllText("$LogPrefix.stdout.log", $stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText("$LogPrefix.stderr.log", $stderr.GetAwaiter().GetResult())
        if ($process.ExitCode -ne 0) { throw "命令失败，退出码 $($process.ExitCode)，日志：$LogPrefix" }
    }
    finally { $process.Dispose() }
}
