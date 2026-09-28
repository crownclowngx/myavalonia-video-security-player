#Requires -Version 7.0
<#
.SYNOPSIS
生成不携带 LibVLC 原生运行库的视频插件 ZIP。
.DESCRIPTION
复用项目锁定版本的 Managed Plugin 打包协议，保留插件清单、私有依赖和外部摘要。
只省去 libvlc.dll、libvlccore.dll 与原生 plugins；两个 LibVLCSharp 托管桥接必须保留。
打包协议会在自己的隔离暂存目录构建，不安装到实际 Host，也不运行 CI 或发布验收脚本。
.EXAMPLE
pwsh -NoProfile -File .\scripts\Build-PluginZip.ps1
.EXAMPLE
pwsh -NoProfile -File .\scripts\Build-PluginZip.ps1 -OutputDirectory .\artifacts\video-plugin-lite
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # 相对路径以本插件仓库根目录为基准；为防止误用旧包，指定目录必须尚不存在。
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repositoryRoot 'src/VideoSecurityPlayer.Plugin/VideoSecurityPlayer.Plugin.csproj'

function Invoke-PackageDotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet 执行失败，退出码：$LASTEXITCODE。未完成插件打包。" }
}

if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "找不到视频插件项目：$project" }
$null = Get-Command dotnet -CommandType Application -ErrorAction Stop
# 官方打包 target 内部通过 pwsh 启动脚本，提前检查可避免构建后才发现命令缺失。
$null = Get-Command pwsh -CommandType Application -ErrorAction Stop

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $runName = 'lite-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N')
    $OutputDirectory = Join-Path $repositoryRoot "artifacts/plugin-zips/$runName"
}
elseif (-not [IO.Path]::IsPathFullyQualified($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot $OutputDirectory
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw '输出目录已存在，请换一个新目录；脚本不会覆盖旧包。' }
$null = New-Item -ItemType Directory -Path $OutputDirectory

# Build 包 1.1.3 的 BuildManagedPluginPackage 会另起 PowerShell/dotnet 进程，
# 只传 Project、Configuration、OutputDirectory，不转发外层任意 MSBuild 属性。
# 因此同时设置命令行属性与本进程环境变量：子进程继承 false，而项目默认只在值为空时设 true。
# finally 恢复原环境值，不修改项目默认配置、用户/系统环境变量或已安装的 Common。
$previousInclude = [Environment]::GetEnvironmentVariable('IncludeLibVlcRuntime', 'Process')
Push-Location $repositoryRoot
try {
    [Environment]::SetEnvironmentVariable('IncludeLibVlcRuntime', 'false', 'Process')
    Invoke-PackageDotNet @('restore', $project, '--locked-mode', '--nologo',
        '-p:IncludeLibVlcRuntime=false', '-p:SkipPluginDeploy=true')

    # MSBuild 将逗号/分号解释为属性分隔符，路径按 MSBuild 规则转义；不拼接可执行命令字符串。
    $outputProperty = $OutputDirectory.Replace('%', '%25').Replace(';', '%3B').Replace(',', '%2C')
    Invoke-PackageDotNet @('msbuild', $project, '-nologo', '-t:BuildManagedPluginPackage',
        "-p:Configuration=$Configuration", '-p:IncludeLibVlcRuntime=false', '-p:SkipPluginDeploy=true',
        "-p:ManagedPluginPackageOutput=$outputProperty")

    $packages = @(Get-ChildItem -LiteralPath $OutputDirectory -Filter '*.zip' -File)
    $manifests = @(Get-ChildItem -LiteralPath $OutputDirectory -Filter '*.manifest.json' -File)
    if ($packages.Count -ne 1 -or $manifests.Count -ne 1) { throw '打包输出不完整：应生成一个 ZIP 和一个外部摘要清单。' }
    $zip = $packages[0]

    # 直接检查最终 ZIP，不从现有 bin 推断包内容，也不在打包后删文件破坏 SDK 生成的摘要。
    Add-Type -AssemblyName System.IO.Compression
    $archive = [IO.Compression.ZipFile]::OpenRead($zip.FullName)
    try {
        $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        $prefix = 'Controls/VideoSecurityPlayer/'
        foreach ($required in @('plugin.manifest.json', 'VideoSecurityPlayer.Plugin.dll',
            'VideoSecurityPlayer.Plugin.deps.json', 'LibVLCSharp.dll', 'LibVLCSharp.Avalonia.dll')) {
            if ($names -cnotcontains ($prefix + $required)) { throw "ZIP 缺少必需文件：$required" }
        }
        foreach ($name in $names) {
            if (-not $name.StartsWith($prefix, [StringComparison]::Ordinal) -or $name -match '(^|/)\.\.(/|$)') {
                throw "ZIP 布局不符合插件目录约定：$name"
            }
            if ($name -match '(?i)(^|/)libvlc(?:core)?\.dll$|(^|/)native/win-x64/libvlc(/|$)|(^|/)plugins/.*_plugin\.dll$') {
                throw "ZIP 意外包含 LibVLC 原生运行库：$name"
            }
        }
    }
    finally { $archive.Dispose() }

    $manifest = Get-Content -LiteralPath $manifests[0].FullName -Raw | ConvertFrom-Json
    $sha256 = (Get-FileHash -LiteralPath $zip.FullName -Algorithm SHA256).Hash
    if ($manifest.archive.file -cne $zip.Name -or $manifest.archive.sha256 -ne $sha256) {
        throw 'ZIP 与打包协议生成的外部摘要不一致。'
    }

    Write-Host '精简插件 ZIP 已生成；不含 LibVLC 原生运行库，两个托管桥接已保留。'
    Write-Host "ZIP：$($zip.FullName)"
    Write-Host "摘要清单：$($manifests[0].FullName)"
    Write-Host "SHA256：$sha256"
}
finally {
    [Environment]::SetEnvironmentVariable('IncludeLibVlcRuntime', $previousInclude, 'Process')
    Pop-Location
}
