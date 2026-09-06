param(
    [Parameter(Mandatory)][string]$HostRepositoryRoot,
    [string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'R1-GateSupport.ps1')
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$OutputRoot) { $OutputRoot = Join-Path $repositoryRoot ('TestResults/Migration/coverage-' + [Guid]::NewGuid().ToString('N')) }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw '覆盖率必须使用新的结果目录，防止混入旧程序集证据。' }
Push-Location $repositoryRoot
try {
    foreach ($suite in @('Tests','UiTests','HostTests','HostUiTests')) {
        & dotnet test "tests/VideoSecurityPlayer.$suite/VideoSecurityPlayer.$suite.csproj" -c Release -m:1 "-p:HostRepositoryRoot=$HostRepositoryRoot" -p:SkipPluginDeploy=true --settings tests/coverage.runsettings --collect:'XPlat Code Coverage' --results-directory (Join-Path $OutputRoot $suite) --logger "trx;LogFileName=$suite.trx"
        if ($LASTEXITCODE -ne 0) { throw "Coverage suite failed: $suite" }
        $allowedSkip = if ($suite -eq 'HostTests') { 'WorkflowActionG4IntegrationTests' } else { '' }
        $null = Assert-R1Trx (Join-Path $OutputRoot "$suite/$suite.trx") $allowedSkip
        $reports = @(Get-ChildItem -LiteralPath (Join-Path $OutputRoot $suite) -Filter 'coverage.cobertura.xml' -Recurse)
        if ($reports.Count -eq 0) { throw "覆盖率采集缺失：$suite" }
        # VSTest 会把采集器原件复制到 TRX 附件目录，同一次采集可能出现两份相同内容。
        # 接受字节一致的附件副本，但拒绝把多份不同采集结果混成当前测试的证据。
        $hashes = @($reports | ForEach-Object {
            $null = Read-R1Evidence $_.FullName
            (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        } | Sort-Object -Unique)
        if ($hashes.Count -ne 1) { throw "覆盖率包含不一致的采集报告：$suite" }
    }
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Tool restore failed' }
    & dotnet reportgenerator "-reports:$OutputRoot/**/coverage.cobertura.xml" "-targetdir:$OutputRoot/merged" '-reporttypes:Cobertura;TextSummary'
    if ($LASTEXITCODE -ne 0) { throw 'Coverage merge failed' }
    [xml]$coverage = Get-Content -LiteralPath "$OutputRoot/merged/Cobertura.xml" -Raw
    $baseline = Get-Content tests/VideoSecurityPlayer.Tests/coverage-baseline.json -Raw | ConvertFrom-Json
    $line = [Math]::Round(100 * [double]$coverage.DocumentElement.GetAttribute('line-rate'), 2)
    $branch = [Math]::Round(100 * [double]$coverage.DocumentElement.GetAttribute('branch-rate'), 2)
    if ($line -lt $baseline.line -or $branch -lt $baseline.branch) { throw "Coverage below preserved baseline: $line%/$branch%" }
    $r1Coverage = Assert-R1Coverage "$OutputRoot/merged/Cobertura.xml" (Join-Path $repositoryRoot 'tests/r1-coverage-policy.json')
    Write-R1Json (Join-Path $OutputRoot 'coverage-summary.json') $r1Coverage
    Write-Output "Coverage passed: line $line%, branch $branch%; $OutputRoot/merged/Cobertura.xml"
}
finally { Pop-Location }
