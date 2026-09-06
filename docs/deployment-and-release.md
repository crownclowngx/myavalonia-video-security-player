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

R1 提供聚合门禁；Fast 执行脚本回归、锁定还原、Release 构建及业务/UI 测试，Full 还要求覆盖率、真实 Host/工作流、Phase4、Standalone、内存与三轮性能比较：

```powershell
pwsh -NoProfile -File scripts/Test-R1.ps1 -Mode Fast
pwsh -NoProfile -File scripts/Test-R1.ps1 -Mode Full -HostRepositoryRoot D:/code/bishe/common/avalonia_dock_simple_test -WorkflowStudioRoot D:/code/bishe/common/avalonia_management_plug/myavalonia-workflow-studio -PerformanceBaseline docs/secret-video-player/benchmarks/r1-windows-x64-baseline.json
```

Full 的性能基线必须是同环境三轮 G10 聚合报告，示例引用本次已保存的脱敏基线；其他环境必须先采集可比较基线。每次运行创建唯一目录；`summary.json` 明确记录通过、失败和未执行。任何必需场景缺少证据都不能完整通过。普通覆盖率运行允许真实双插件用例明确跳过，Full 的 Host 工作流步骤必须实际执行该用例。当前 R1 仍存在原生句柄基线阻断，详见 [R1 实施与验证记录](secret-video-player/reference/R1-IMPLEMENTATION-AND-VALIDATION.md)。

```powershell
./scripts/Test-HostIntegration.ps1 -HostRepositoryRoot D:/code/bishe/common/avalonia_dock_simple_test -WorkflowStudioRoot D:/code/bishe/common/avalonia_management_plug/myavalonia-workflow-studio
./scripts/Test-Coverage.ps1 -HostRepositoryRoot D:/code/bishe/common/avalonia_dock_simple_test
```

联调脚本在唯一隔离目录校验 ZIP 摘要和每个文件的 SHA-256，通过真实 Host 加载四个文档，并使用 Workflow Studio 验证加密成功、重复运行不覆盖、源文件保持不变。未提供 WorkflowStudioRoot 时不执行双插件工作流测试。

覆盖率沿用原业务、UI、Host 组合测试合并的口径，仅统计正式插件；原始行/分支下限 72.59%/48.12% 保留，R1 同环境实测下限提升为 74.39%/51.99%，由 `tests/r1-coverage-policy.json` 约束。不把独立调试程序和基准工具计入原业务分母，R1.2–R1.4 的部署、诊断、批次有效性、轨道选择四个组件已加入独立覆盖率清单，各自要求行至少 90%、分支至少 80%。

## 真实播放、内存及安全基准

```powershell
dotnet run --project tools/VideoSecurityPlayer.Playback.IntegrationHarness -c Release -p:HostRepositoryRoot=D:/code/bishe/common/avalonia_dock_simple_test -- --suite g3 --cycles 20 --report TestResults/Migration/harness/g3.json
dotnet run --project src/VideoSecurityPlayer.Standalone -c Release -- --smoke-media tests/VideoSecurityPlayer.Tests/TestAssets/RealMedia/synthetic-av-short.mp4 --smoke-report TestResults/Migration/standalone-playback.json
dotnet run --project tools/VideoSecurityPlayer.ReleaseAcceptance -c Release -- --memory --report TestResults/Migration/memory.json
dotnet run --project tools/VideoSecurityPlayer.SecurityBenchmarks -c Release -- --output TestResults/Migration/security-benchmark.json
```

真实播放需要交互式 Windows x64 会话，Headless UI 测试不能替代 HWND/LibVLC 验收。原 G8/G10/Phase4 Harness 能力和历史基准均保留；历史文档引用的旧 scripts 目录已不在原工作区，不代表这些旧脚本仍可执行。

原生句柄诊断可使用 ReleaseAcceptance 的 `--native-lifetime isolated` 与 `--native-lifetime overlap` 对照模式，命令和实测结果见 [R1 原生诊断](secret-video-player/reference/R1-IMPLEMENTATION-AND-VALIDATION.md#7-可重复运行的原生诊断)。诊断退出码 0 只表示采集完成，不代表资源验收通过。
