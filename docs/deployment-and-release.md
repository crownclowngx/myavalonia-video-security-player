# 构建、部署和验收

在仓库根目录执行：

```powershell
dotnet restore VideoSecurityPlayer.slnx --locked-mode
dotnet build VideoSecurityPlayer.slnx -c Release -warnaserror
dotnet test VideoSecurityPlayer.slnx -c Release --no-build
dotnet run --project src/VideoSecurityPlayer.Standalone
dotnet msbuild src/VideoSecurityPlayer.Plugin/VideoSecurityPlayer.Plugin.csproj -t:BuildManagedPluginPackage -p:Configuration=Release
```

正式 ZIP 和外部摘要位于 `src/VideoSecurityPlayer.Plugin/artifacts/managed-plugin-packages`。ZIP 内为 `Controls/VideoSecurityPlayer`，仅包含插件及 LibVLC 私有依赖、完整 `native/win-x64/libvlc` 目录。SDK、Avalonia、Dock、Microsoft.Extensions、Standalone 和测试组件不进入插件包。


```powershell
dotnet msbuild src/VideoSecurityPlayer.Plugin/VideoSecurityPlayer.Plugin.csproj `
  -t:DeployManagedPlugin `
  -p:Configuration=Debug `
  -p:ManagedPluginDeployRoot=C:\Path\To\Host\Controls
```
## 替换旧内置插件

关闭 Host，将旧 `Controls/SmallTools` 移到扫描目录以外，再解压新 ZIP 到 Host 根目录。插件 ID 不变，新旧目录不可并存；正式用户数据保持原位置。迁移开发过程不会自动部署到正在使用的 Host。

显式开发部署：

```powershell
dotnet msbuild src/VideoSecurityPlayer.Plugin/VideoSecurityPlayer.Plugin.csproj -t:DeployManagedPlugin -p:ManagedPluginDeployRoot=C:\Path\To\Host\Controls
```

## 真实 Host 与工作流

```powershell
./scripts/Test-HostIntegration.ps1 -HostRepositoryRoot D:/code/bishe/common/avalonia_dock_simple_test -WorkflowStudioRoot D:/code/bishe/common/avalonia_management_plug/myavalonia-workflow-studio
./scripts/Test-Coverage.ps1 -HostRepositoryRoot D:/code/bishe/common/avalonia_dock_simple_test
```

联调脚本在唯一隔离目录校验 ZIP 摘要和每个文件的 SHA-256，通过真实 Host 加载四个文档，并使用 Workflow Studio 验证加密成功、重复运行不覆盖、源文件保持不变。未提供 WorkflowStudioRoot 时不执行双插件工作流测试。

覆盖率沿用原业务、UI、Host 组合测试合并的口径，仅统计正式插件；行/分支基线保持 72.59%/48.12%，不把独立调试程序和基准工具计入原业务分母。

## 真实播放、内存及安全基准

```powershell
dotnet run --project tools/VideoSecurityPlayer.Playback.IntegrationHarness -c Release -p:HostRepositoryRoot=D:/code/bishe/common/avalonia_dock_simple_test -- --suite g3 --cycles 20 --report TestResults/Migration/harness/g3.json
dotnet run --project src/VideoSecurityPlayer.Standalone -c Release -- --smoke-media tests/VideoSecurityPlayer.Tests/TestAssets/RealMedia/synthetic-av-short.mp4 --smoke-report TestResults/Migration/standalone-playback.json
dotnet run --project tools/VideoSecurityPlayer.ReleaseAcceptance -c Release -- --memory --report TestResults/Migration/memory.json
dotnet run --project tools/VideoSecurityPlayer.SecurityBenchmarks -c Release -- --output TestResults/Migration/security-benchmark.json
```

真实播放需要交互式 Windows x64 会话，Headless UI 测试不能替代 HWND/LibVLC 验收。原 G8/G10/Phase4 Harness 能力和历史基准均保留；历史文档引用的旧 scripts 目录已不在原工作区，不代表这些旧脚本仍可执行。
