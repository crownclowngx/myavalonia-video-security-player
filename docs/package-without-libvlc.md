# 生成不含 LibVLC 原生库的插件 ZIP

脚本：[scripts/Build-PluginZip.ps1](../scripts/Build-PluginZip.ps1)。需要 PowerShell 7（`pwsh`）和项目所需的 .NET 10 SDK，两者均应可从 PATH 调用。

在 **myavalonia-video-security-player 仓库根目录**执行：

```powershell
pwsh -NoProfile -File .\scripts\Build-PluginZip.ps1
```

默认使用 Release 配置，结果位于仓库的 `artifacts/plugin-zips/lite-<时间>-<唯一标识>/`。脚本结束时输出 ZIP 和摘要清单的完整路径及 SHA256。

也可以指定一个尚不存在的输出目录：

```powershell
pwsh -NoProfile -File .\scripts\Build-PluginZip.ps1 -OutputDirectory .\artifacts\video-plugin-lite
```

目录已存在时会拒绝执行，避免覆盖旧包或误取旧结果。需要 Debug 包时加 `-Configuration Debug`。

ZIP 内仍为 `Controls/VideoSecurityPlayer/`，保留插件、清单及声明的私有依赖，包括：

- `LibVLCSharp.dll`
- `LibVLCSharp.Avalonia.dll`

排除 `libvlc.dll`、`libvlccore.dll` 和 `native/win-x64/libvlc/` 原生模块。这两个托管桥接仍是视频插件运行所需依赖，不应跟随原生库一起去掉。运行时使用应用目录的 `Common/libvlc`，或在“设置与维护”中指定已有的完整运行库，详见 [S1 使用说明](secret-video-player/reference/S1-SETTINGS-AND-RUNTIME-GUIDE.md)。

脚本先锁定还原，再调用已有 Managed Plugin 打包协议，不压缩开发目录中的全部文件。`IncludeLibVlcRuntime=false` 同时通过外层参数和临时进程环境变量传递，确保打包协议内部启动的子构建也省略原生库；完成或失败后恢复原环境值。最终检查 ZIP 中没有原生库、两个桥接仍存在，且 ZIP 哈希与协议生成的摘要一致。

脚本只生成本地包；打包协议的复制目标是自己的隔离暂存目录，不安装到实际 Host，不修改 Common，不运行 Windows CI、R1/Host 发布门禁或上传发布。

本次交付只编写脚本并做 PowerShell 静态语法检查，**未执行脚本、还原、构建或生成 ZIP**。实际打包结果以之后手动运行的输出为准。
