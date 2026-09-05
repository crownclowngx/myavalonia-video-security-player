param(
    [Parameter(Mandatory)][string]$HostRepositoryRoot,
    [string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$OutputRoot) { $OutputRoot = Join-Path $repositoryRoot ('TestResults/Migration/coverage-' + [Guid]::NewGuid().ToString('N')) }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
Push-Location $repositoryRoot
try {
    foreach ($suite in @('Tests','UiTests','HostTests','HostUiTests')) {
        & dotnet test "tests/VideoSecurityPlayer.$suite/VideoSecurityPlayer.$suite.csproj" -c Release -m:1 "-p:HostRepositoryRoot=$HostRepositoryRoot" -p:SkipPluginDeploy=true --settings tests/coverage.runsettings --collect:'XPlat Code Coverage' --results-directory (Join-Path $OutputRoot $suite) --logger "trx;LogFileName=$suite.trx"
        if ($LASTEXITCODE -ne 0) { throw "Coverage suite failed: $suite" }
    }
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Tool restore failed' }
    & dotnet reportgenerator "-reports:$OutputRoot/**/coverage.cobertura.xml" "-targetdir:$OutputRoot/merged" '-reporttypes:Cobertura;TextSummary'
    if ($LASTEXITCODE -ne 0) { throw 'Coverage merge failed' }
    [xml]$coverage = Get-Content -LiteralPath "$OutputRoot/merged/Cobertura.xml" -Raw
    $baseline = Get-Content tests/VideoSecurityPlayer.Tests/coverage-baseline.json -Raw | ConvertFrom-Json
    $line = [Math]::Round(100 * [double]$coverage.coverage.'line-rate', 2)
    $branch = [Math]::Round(100 * [double]$coverage.coverage.'branch-rate', 2)
    if ($line -lt $baseline.line -or $branch -lt $baseline.branch) { throw "Coverage below preserved baseline: $line%/$branch%" }
    Write-Output "Coverage passed: line $line%, branch $branch%; $OutputRoot/merged/Cobertura.xml"
}
finally { Pop-Location }
