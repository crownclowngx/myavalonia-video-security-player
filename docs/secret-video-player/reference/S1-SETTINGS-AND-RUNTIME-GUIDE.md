# S1 设置与维护、共用 LibVLC 使用说明

S1 增加“视频工具 · 设置与维护”Tool，包含播放偏好、视频库偏好、记录管理、播放组件与诊断四组。Tool 关闭时隐藏，重新打开保留草稿和展开状态；插件退出才释放共享模型。实现与本轮证据见 [实施与验证记录](S1-IMPLEMENTATION-AND-VALIDATION.md)。

## 打开和保存

- Host 从工具入口打开“视频工具 · 设置与维护”；Standalone 在顶部选择该 Tool 后点击打开。
- 播放器的“设置与维护”按钮在页面内打开同一设置模型。宽窗口并排显示，窄窗口在下方显示；播放器保持在原布局节点，关闭设置后焦点回到入口。
- 播放/视频库修改后自动保存，每组独立显示“保存中 / 已保存 / 保存失败、重试”。短时间连续修改会合并；关闭应用会等待最后一次已接受的编辑提交。
- 播放组件先编辑草稿，可点击“检测目录”，再点击“保存组件配置”。保存时重新检测。检测成功仅表示文件预检通过，真实播放结果单独判断。

## 四组配置及生效范围

| 分组 | 配置/操作 | 生效范围 |
| --- | --- | --- |
| 播放偏好 | 记忆或固定音量；记忆或固定倍速；恢复上次位置；快进/后退 5、10、30 秒 | 保存后新建的两个播放器文档读取快照；已有播放不改变 |
| 视频库偏好 | 恢复上次目录；默认递归；默认排序字段/方向；恢复侧栏默认布局 | 新建视频库；不会重排当前库或清除最近目录 |
| 记录管理 | 记录观看进度；观看记录数量/清空；最近目录移除/清空 | 记录开关立即影响所有文档；清理只改记录，保留视频文件和当前打开的库 |
| 播放组件与诊断 | 自动/指定目录；检测、保存；已保存选择、实际来源/版本/状态；脱敏环境导出 | 未加载时下一次初始化使用；已尝试加载后修改需重启应用 |

清空观看记录和清空最近目录都先显示确认。持久化成功才显示清理完成；失败保留原列表并可重试。关闭记录不删除旧记录；重新启用后不会补写禁用期间缓存的进度。清空后当前媒体代次被抑制，避免定时回调或文档关闭把刚删除的历史写回来；重新加载该视频可形成新的记录。

## 共用运行库

自动模式按以下顺序做文件预检：

1. `<应用目录>/Common/libvlc`，其中应用目录是 `AppContext.BaseDirectory`，不是当前工作目录。
2. `<视频插件目录>/native/win-x64/libvlc`。

指定目录模式只检测该绝对目录，不自动替换成其他来源。所选目录必须包含 `libvlc.dll`、`libvlccore.dll` 和完整 `plugins`，第一批支持 Windows x64、核心版本 3.0.23；检查 MP4/WebM、FFmpeg、D3D11 和音频输出的必要模块。不能只借用能播音乐的部分 DLL。两个托管桥接 `LibVLCSharp.dll` 与 `LibVLCSharp.Avalonia.dll` 始终留在视频插件私有目录。

Common 只读复用。程序不自动下载、复制、升级或删除 Common，不设置全局 PATH，不引用网易云插件对象。音乐插件可以把自己的指定目录指向同一份完整运行库；各插件保持私有托管桥接和各自的播放器生命周期。

自动模式只有**文件预检失败**才能尝试下一候选。一旦进入真实绑定，目录就固定：加载成功为 Ready，失败为 Failed，都必须重启后才能换库。Tool 同时显示“已保存配置”和“本次实际使用”，避免把保存成功误解为热切换成功。Common 缺失但内置可用时能回退；Common 文件预检通过但真实加载失败时不会继续加载内置。

## 精简构建与包体积

界面选择 Common 不会改变已安装的文件体积。实际减少交付内容需要构建时显式设置 `IncludeLibVlcRuntime=false`，默认值为 `true`。

```powershell
# 本地 Debug 精简输出示例，使用新的隔离目录，不执行正式打包或部署。
$s1Artifacts = Join-Path (Get-Location) ('artifacts/S1/thin-' + [guid]::NewGuid().ToString('N'))
dotnet restore src/VideoSecurityPlayer.Standalone/VideoSecurityPlayer.Standalone.csproj --locked-mode `
    -p:SkipPluginDeploy=true -p:UseArtifactsOutput=true "-p:ArtifactsPath=$s1Artifacts" -p:IncludeLibVlcRuntime=false
dotnet build src/VideoSecurityPlayer.Standalone/VideoSecurityPlayer.Standalone.csproj -c Debug --no-restore -warnaserror `
    -p:SkipPluginDeploy=true -p:UseArtifactsOutput=true "-p:ArtifactsPath=$s1Artifacts" -p:IncludeLibVlcRuntime=false
```

精简输出仍有两个托管桥接。没有可用 Common/指定库时，可以打开设置以及使用加解密；播放入口显示部署问题。同一输出从完整切精简，只会清理本构建有归属标记的原生子目录；遇到没有归属标记的旧目录会报错，应换新输出目录。正式 ZIP 及压缩体积在发布阶段再验证。

## 数据和排障

用户数据文件仍名为 `user-data-v1.json`，内容版本升级为 2。读取旧文件先在内存迁移，第一次有效修改时才保存；音量、倍速、目录和历史保留。更高版本按只读处理，退出也不会覆盖；不会通过备份恢复已清理历史。密码、标题、描述和解密材料不进入设置。

“导出环境诊断”无需打开文档，不加载 DLL。只导出插件版本、平台、来源类型、加载状态、版本及问题码，不包含用户路径、观看记录或媒体内容。具体播放错误仍由对应播放器的诊断入口导出。

| 现象 | 处理 |
| --- | --- |
| 已保存指定目录，实际仍显示原库 | 当前进程已尝试加载，重启应用后生效 |
| 检测通过但播放加载失败 | 检查完整 3.0.23 x64 库及其依赖；真实加载失败后重启，不在进程内重试其他来源 |
| Common 检测失败后用了内置 | 自动模式的预检回退；检测结果保留 Common 失败原因 |
| 关闭记录后提示偏好未保存 | 本进程已停止记录；修复数据目录权限后重试，确保下次启动也保持关闭 |
| 设置由更新版本创建 | 使用相应新版本修改，当前版本不覆盖该文件 |

## 本地开发检查

```powershell
pwsh -NoProfile -File scripts/Test-S1.ps1
pwsh -NoProfile -File scripts/Test-S1Native.ps1 -Scenario Playback
# 显式目录需要同时包含 MusicNetEasePlugin.Plugin.dll 和音乐自己的 LibVLCSharp.dll。
pwsh -NoProfile -File scripts/Test-S1Native.ps1 -Scenario Coexistence -MusicPluginDirectory C:/Path/To/MusicDebugOutput
```

脚本使用新结果根，失败立即停止，尚未执行的步骤保持 `notExecuted`。普通门禁包含脚本自测、锁定还原、Debug 警告视为错误、单元/UI、覆盖率、完整/精简/切换布局。原生脚本在隔离应用副本中验证真实播放；共存调用同进程音乐的真实 Runtime/AudioOutput 及单独的托管程序集上下文。

本轮不使用 AIFLOW、Windows CI、R1/Host 发布门禁、正式打包或部署。Standalone 多窗验证不等价于真实 Host Dock 拖拽，尚未覆盖项明确列在实施记录中。
