param(
    [Parameter(Mandatory)][string]$HostRepositoryRoot,
    [string]$WorkflowStudioRoot,
    [switch]$SkipBuildPackage,
    [string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$pluginRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$HostRepositoryRoot = (Resolve-Path -LiteralPath $HostRepositoryRoot).Path
$runRoot = if ($OutputRoot) { [IO.Path]::GetFullPath($OutputRoot) } else { Join-Path $pluginRoot ('TestResults/Migration/integration-' + [Guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $runRoot) { throw 'Integration output must be a new directory.' }
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE): $Arguments" }
}
function Expand-VerifiedPackage([string]$PackageDirectory, [string]$Destination) {
    $manifestFile = Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.manifest.json' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (!$manifestFile) { throw "Package manifest missing: $PackageDirectory" }
    $manifest = Get-Content -LiteralPath $manifestFile.FullName -Raw | ConvertFrom-Json
    $archive = Join-Path $PackageDirectory $manifest.archive.file
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $manifest.archive.sha256) { throw 'Archive digest mismatch' }
    foreach ($file in $manifest.files) {
        $target = [IO.Path]::GetFullPath((Join-Path $Destination $file.path))
        if (!$target.StartsWith([IO.Path]::GetFullPath($Destination) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package path escapes destination' }
    }
    Expand-Archive -LiteralPath $archive -DestinationPath $Destination
    foreach ($file in $manifest.files) {
        if ((Get-FileHash -LiteralPath (Join-Path $Destination $file.path) -Algorithm SHA256).Hash -ne $file.sha256) { throw "File digest mismatch: $($file.path)" }
    }
    return $manifest
}
Push-Location $pluginRoot
$previousPackage = $env:MYAVALONIA_G11_V3_PACKAGE_ROOT
$previousWorkflow = $env:MYAVALONIA_WORKFLOW_G4_PLUGIN_ROOT
$previousMedia = $env:MYAVALONIA_WORKFLOW_G4_MEDIA_PATH
try {
    if (!$SkipBuildPackage) { Invoke-Dotnet @('msbuild','src/VideoSecurityPlayer.Plugin/VideoSecurityPlayer.Plugin.csproj','-t:BuildManagedPluginPackage','-p:Configuration=Release','-v:minimal') }
    $package = Expand-VerifiedPackage (Join-Path $pluginRoot 'src/VideoSecurityPlayer.Plugin/artifacts/managed-plugin-packages') (Join-Path $runRoot 'player')
    if ($package.pluginId -ne 'myavalonia.plugin.my-small-tools' -or $package.entryPoint.assembly -ne 'VideoSecurityPlayer.Plugin.dll') { throw 'Unexpected plugin identity' }
    # V6.1 的纯图标资源属于插件私有依赖，精确放行后仍保留其余共享程序集门禁。
    $forbidden = $package.files | Where-Object { [IO.Path]::GetFileName($_.path) -match '^(MyAvaloniaManagement(?!\.Icons\.dll$)|Avalonia|Dock|Microsoft\.Extensions)|Standalone|\.Tests\.' }
    if ($forbidden) { throw 'Shared or development assemblies leaked into package' }
    $env:MYAVALONIA_G11_V3_PACKAGE_ROOT = Join-Path $runRoot 'player/Controls'
    Invoke-Dotnet @('test','tests/VideoSecurityPlayer.HostTests/VideoSecurityPlayer.HostTests.csproj','-c','Release',"-p:HostRepositoryRoot=$HostRepositoryRoot",'-p:SkipPluginDeploy=true','--filter','FullyQualifiedName!~WorkflowActionG4IntegrationTests','--logger','trx;LogFileName=host-package.trx','--results-directory',$runRoot)
    Invoke-Dotnet @('test','tests/VideoSecurityPlayer.HostUiTests/VideoSecurityPlayer.HostUiTests.csproj','-c','Release',"-p:HostRepositoryRoot=$HostRepositoryRoot",'-p:SkipPluginDeploy=true','--logger','trx;LogFileName=host-ui.trx','--results-directory',$runRoot)
    if ($WorkflowStudioRoot) {
        $workflowProject = Join-Path $WorkflowStudioRoot 'src/WorkflowStudio.Plugin/WorkflowStudio.Plugin.csproj'
        if (!$SkipBuildPackage) { Invoke-Dotnet @('msbuild',$workflowProject,'-t:BuildManagedPluginPackage','-p:Configuration=Release','-v:minimal') }
        $workflowStage = Join-Path $runRoot 'workflow'
        $null = Expand-VerifiedPackage (Join-Path $pluginRoot 'src/VideoSecurityPlayer.Plugin/artifacts/managed-plugin-packages') $workflowStage
        $null = Expand-VerifiedPackage (Join-Path $WorkflowStudioRoot 'src/WorkflowStudio.Plugin/artifacts/managed-plugin-packages') $workflowStage
        $env:MYAVALONIA_WORKFLOW_G4_PLUGIN_ROOT = Join-Path $workflowStage 'Controls'
        $env:MYAVALONIA_WORKFLOW_G4_MEDIA_PATH = Join-Path $pluginRoot 'tests/VideoSecurityPlayer.Tests/TestAssets/RealMedia/synthetic-av-short.mp4'
        Invoke-Dotnet @('test','tests/VideoSecurityPlayer.HostTests/VideoSecurityPlayer.HostTests.csproj','-c','Release','--no-build','--no-restore',"-p:HostRepositoryRoot=$HostRepositoryRoot",'--filter','FullyQualifiedName~WorkflowActionG4IntegrationTests','--logger','trx;LogFileName=workflow.trx','--results-directory',$runRoot)
    }
    Write-Output "Verified integration evidence: $runRoot"
}
finally {
    $env:MYAVALONIA_G11_V3_PACKAGE_ROOT = $previousPackage
    $env:MYAVALONIA_WORKFLOW_G4_PLUGIN_ROOT = $previousWorkflow
    $env:MYAVALONIA_WORKFLOW_G4_MEDIA_PATH = $previousMedia
    Pop-Location
}
