# VideoSecurityPlayer

从 MyAvaloniaManagement 内置 MySmallTools 完整迁出的加密视频插件，包含加密视频播放、媒体库、批量加密、批量解密，以及保留源文件的加密 Workflow Action。

业务、界面和注册只有一份，位于 `src/VideoSecurityPlayer.Plugin`。独立调试窗口提供多标签承载，支持重复打开四种功能；正式插件继续通过公开 SDK 接入 Host。

```powershell
dotnet restore VideoSecurityPlayer.slnx --locked-mode
dotnet build VideoSecurityPlayer.slnx -c Release -warnaserror
dotnet test VideoSecurityPlayer.slnx -c Release --no-build
dotnet run --project src/VideoSecurityPlayer.Standalone
dotnet msbuild src/VideoSecurityPlayer.Plugin/VideoSecurityPlayer.Plugin.csproj -t:BuildManagedPluginPackage -p:Configuration=Release
```

运行基线：Windows x64、.NET 10、Avalonia 12.1.0、SDK/UI 3.3.0、Build 1.1.2、LibVLCSharp 3.10.0、VideoLAN 3.0.23.1。插件版本保持 3.1.0，容器格式保持 SECVID03。

- [功能使用与格式文档](docs/secret-video-player/README.md)
- [R1 SOLID 优化实施计划](docs/secret-video-player/plan-history/R1-SOLID-REFACTOR-AND-QUALITY-GATES.md)
- [项目和独立窗口职责](docs/project-and-window-responsibilities.md)
- [部署、替换安装和验收命令](docs/deployment-and-release.md)
- [迁移记录与文件核对](docs/migration/2026-09-05-independent-plugin.md)

稳定 Plugin/Document/Action ID 继续使用原 `myavalonia.plugin.my-small-tools` 前缀。正式用户数据路径保持不变；独立窗口使用 `%LOCALAPPDATA%/VideoSecurityPlayer/Standalone/user-data-v1.json`。

正式包位于 `src/VideoSecurityPlayer.Plugin/artifacts/managed-plugin-packages`。更新安装时关闭 Host，将旧 `Controls/SmallTools` 移出插件扫描目录，再安装新 `Controls/VideoSecurityPlayer`；相同 Plugin ID 的新旧副本不能同时存在。
