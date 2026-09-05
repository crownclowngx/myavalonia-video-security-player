# G11 最终验收与完整测试手册

> 适用范围：VideoSecurityPlayer 安全视频子系统
> 正式平台：Windows x64、.NET 10
> 清单版本：`g11-guide-v1`

## 1. 目的与完成条件

本手册是 G11 唯一的完整测试入口。G11 不增加产品功能、不修改 SECVID03，也不以接口存在
推断跨平台支持。它在同一个 Git 提交上组合 G4、G8、G9 和 G10 的既有门禁，并把人工交互
确认与自动化技术证据分开。

只有同时满足以下条件，才能把 G11 标记为完成：

- G10 审核基线存在且当前环境可比较；
- G4/P0、G8/P1、G9 和 G10 的正式门禁全部通过；
- VideoSecurityPlayer Release 构建为 0 警告、0 错误；
- 180 项 VideoSecurityPlayer 测试和 21 项宿主插件测试全部通过；
- 敏感信息扫描为 0 命中；
- 本文第 7～11 节的人工检查由真实验收人完成；
- `g11-final-acceptance.json` 的 `finalAcceptancePassed` 为 `true`。

自动化不能替代人工签字。没有人工签字时，技术报告必须保持
`manualSignoff = "pending"`。

## 2. SOLID 与安全边界

G11 的设计刻意保持简单：

- G4、G8、G10 脚本各自拥有阶段门禁；G11 脚本只做顺序编排和结果汇总。
- 人工签字由独立脚本负责，技术验收脚本不能自动批准自己。
- 新增的 `-EvidenceRoot` 只是脚本扩展点，不改变既有命令的默认行为。
- 产品 C# 接口、DI 生命周期和 ViewModel 职责不变。
- Windows HWND 仍只能由 `EmbeddedVideoSurface` 适配，业务层和 ViewModel 不读取句柄。

以下安全约束在 G11 中保持冻结：

- SECVID03 魔数、磁盘布局、600,000 次 PBKDF2、nonce、AAD、1 MiB 块和 GCM Tag 不变；
- 不支持 SECVID02，不增加迁移或直接播放入口；
- 密码不进入队列、历史、设置、日志、诊断或验收报告；
- 播放不生成完整明文临时视频；
- 输出使用唯一 partial 和不覆盖提交，不信任公开文件名；
- Linux/macOS 原生表面、LibVLC 打包、安装器和发行验收均未交付。

## 3. 环境准备

在仓库根目录打开 Windows PowerShell，执行：

```powershell
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
git status --short
git rev-parse --short=12 HEAD
dotnet --version
dotnet --info
```

正式验收要求：

- `git status --short` 无输出；
- 当前进程为 Windows x64；
- SDK 主版本为 9；
- 使用可显示真实 Avalonia 窗口的交互式桌面会话；
- 没有另一个宿主或测试进程占用 VideoSecurityPlayer 插件目录；
- 至少预留 10 GiB 可用磁盘空间。

确认测试资产完整：

```powershell
dotnet test `
  .\Plugins\VideoSecurityPlayer\VideoSecurityPlayer.Tests\VideoSecurityPlayer.Tests.csproj `
  -c Release `
  --filter FullyQualifiedName~RealMediaAssetTests
```

仓库提供的 CC0 测试媒体位于：

```text
Plugins/VideoSecurityPlayer/VideoSecurityPlayer.Tests/TestAssets/RealMedia/
```

包含有声 MP4、无声多块 WebM、双音轨和内嵌字幕 MP4。人工测试应使用这些资产，不使用私人
视频。人工创建 `.secvid` 时使用本次测试专用密码；不要把真实密码写入本文、截图或报告。

## 4. 还原、构建与基础自动化

按顺序执行，两个测试工程不要并行运行：

```powershell
dotnet restore .\MyAvaloniaManagement.sln

dotnet build `
  .\Plugins\VideoSecurityPlayer\VideoSecurityPlayer\VideoSecurityPlayer.csproj `
  -c Release `
  -warnaserror

dotnet test `
  .\Plugins\VideoSecurityPlayer\VideoSecurityPlayer.Tests\VideoSecurityPlayer.Tests.csproj `
  -c Release `
  --no-restore

dotnet test `
  .\Host\MyAvaloniaManagement.PluginTests\MyAvaloniaManagement.PluginTests.csproj `
  -c Release `
  --no-restore
```

预期结果：

- VideoSecurityPlayer 构建为 0 警告、0 错误；
- VideoSecurityPlayer 测试为 180/180；
- 宿主插件测试为 21/21。

宿主测试构建整个解决方案时可能显示其他历史插件的既有警告，它们不属于 VideoSecurityPlayer
零警告门禁；不能因此忽略 VideoSecurityPlayer 自身警告。

只运行 G9 平台边界回归：

```powershell
dotnet test `
  .\Plugins\VideoSecurityPlayer\VideoSecurityPlayer.Tests\VideoSecurityPlayer.Tests.csproj `
  -c Release `
  --no-build `
  --no-restore `
  --filter FullyQualifiedName~G9PlatformAbstractionTests
```

## 5. 阶段门禁命令

### 5.1 G4/P0

正式运行：

```powershell
.\scripts\Release-VideoSecurityPlayerP0.ps1
```

本地 dirty-worktree 排错：

```powershell
.\scripts\Release-VideoSecurityPlayerP0.ps1 -AllowDirty
```

正式门禁通过 G12 单插件入口生成确定性 `VideoSecurityPlayer-<PluginVersion>-win-x64.zip` 与外置文件清单，
再对最终 ZIP 执行部署探针、64/512 MiB 流式内存门禁和两轮真实播放。`publishable` 必须为 `true`。

### 5.2 G8/P1

正式运行：

```powershell
.\scripts\Accept-VideoSecurityPlayerP1.ps1
```

本地排错：

```powershell
.\scripts\Accept-VideoSecurityPlayerP1.ps1 -AllowDirty
```

正式规模固定为 100 个队列项目和 1,000 个媒体库项目，真实窗口组合运行两轮。
`cleanTechnicalEvidenceReady` 必须为 `true`。

### 5.3 G10 基线

仓库从未建立基线时，只能在 clean worktree 上执行一次：

```powershell
.\scripts\Accept-VideoSecurityPlayerG10.ps1 -UpdateBaseline
```

生成后必须评审环境指纹、场景参数、正确性硬门禁和资源结果，并单独提交：

```text
Plugins/VideoSecurityPlayer/VideoSecurityPlayer/docs/secret-video-player/benchmarks/
g10-windows-x64-net10-avalonia12-dock12-reference.json
```

日常和最终验收禁止使用 `-UpdateBaseline`，必须与审核基线比较：

```powershell
.\scripts\Accept-VideoSecurityPlayerG10.ps1
```

本地排错可执行：

```powershell
.\scripts\Accept-VideoSecurityPlayerG10.ps1 -AllowDirty
```

`-AllowNonComparable` 只用于调查环境差异，不能形成正式证据。正式结果要求
`technicalAcceptancePassed=true`、`timingGate=passed`、`findingCount=0`。

## 6. G11 一键技术验收

G10 基线已经审核并提交、G11 工具和文档也已经提交后，在 clean worktree 执行：

```powershell
.\scripts\Accept-VideoSecurityPlayerG11.ps1
```

脚本顺序运行完整 G4、G8、G9、G10。各阶段证据先进入：

```text
artifacts/VideoSecurityPlayer/g11/stages/
```

该目录被 Git 忽略，可包含大文件和中间数据。只有全部门禁通过且源码状态没有变化后，脚本
才把小体积脱敏摘要写入：

```text
TestResults/G11/
```

正式技术报告必须满足：

```text
worktreeWasClean = true
technicalAcceptancePassed = true
formalSignoffReady = true
manualSignoff = pending
```

开发中可以运行：

```powershell
.\scripts\Accept-VideoSecurityPlayerG11.ps1 -AllowDirty
```

该模式仍执行完整技术场景，但强制 `formalSignoffReady=false`，签字脚本会拒绝它。

## 7. 启动宿主与公共准备

先构建插件，使托管桥接和私有 LibVLC 树部署到宿主输出：

```powershell
dotnet build `
  .\Plugins\VideoSecurityPlayer\VideoSecurityPlayer\VideoSecurityPlayer.csproj `
  -c Release `
  -warnaserror

dotnet run `
  --project .\Host\MyAvaloniaManagement\MyAvaloniaManagement.csproj `
  -c Release
```

从宿主菜单分别打开：

1. 视频文件加密器；
2. 批量视频解密器；
3. 加密视频播放器；
4. 加密视频库播放器。

人工交互在当前系统缩放下执行。G11 不要求切换 100%/150% 做重复观感验收，但 Dock、全屏、
真实 HWND 和资源释放仍由自动窗口门禁验证。

## 8. 加密与解密人工检查

### 8.1 正常批量闭环

1. 在加密器一次加入三份仓库媒体，选择新的临时输出目录。
2. 输入测试密码，检查公开标题和描述，然后开始加密。
3. 确认任务严格顺序运行，同时只有一个项目处于 Running。
4. 用解密器选择生成的三个 `.secvid`，导出到另一个空目录。
5. 比较原文件和导出文件：

```powershell
Get-FileHash -Algorithm SHA256 `
  .\Plugins\VideoSecurityPlayer\VideoSecurityPlayer.Tests\TestAssets\RealMedia\*.mp4
Get-FileHash -Algorithm SHA256 <解密输出目录>\*.mp4
```

对 WebM 同样比较。预期字节哈希完全一致，目录中没有 `.partial-*`。

### 8.2 取消、失败、重试与冲突

- 在较大 WebM 加密中途取消：当前 partial 被清理，已成功项目保留。
- 关闭正在执行的加密或解密 Document：当前任务停止，等待项放弃，宿主 UI 不冻结。
- 使用错误密码解密：项目稳定显示认证失败，且明文 partial 尚未创建。
- 修正密码后重试：只重试失败/取消项，不重复处理成功项。
- 预先创建同名目标：加密明确阻止；解密安全追加数字后缀。
- 在预检后、提交前制造同名竞争：哨兵文件不被覆盖，任务显示 `OutputConflict`。

## 9. 单文件播放器人工检查

分别用有声 MP4、无声多块 WebM、双音轨字幕 MP4 生成的 `.secvid` 执行：

1. 选择文件后确认公开标题/描述可见，但不被界面称为已认证数据。
2. 输入错误密码，确认旧媒体不被替换且提示为认证问题。
3. 输入正确密码加载并播放，确认没有完整明文视频临时文件。
4. 从开头 Seek 到中间、尾部，再连续左右 Seek；画面和声音继续工作。
5. 检查播放/暂停、停止、音量和 0.5～2.0 倍速。
6. 双音轨样本切换两条音轨，打开/关闭内嵌字幕。
7. 暂停后切换 Dock 标签、移动 Dock、进入和退出全屏；确认位置、暂停状态、倍速和轨道恢复。
8. 播放期间切换到另一媒体；确认仍是同一 Document，不出现独立原生窗口。
9. 编辑公开标题/描述；编辑前文件句柄被释放，修改后重新输入密码加载。

关闭 Document 后应能立即移动或删除 `.secvid`，不存在遗留文件占用。

## 10. 媒体库与多 Document 人工检查

1. 准备包含多层目录和若干 `.secvid` 的测试目录。
2. 检查非递归/递归扫描、文件名/标题搜索、状态筛选和四类排序。
3. 扫描后新增、修改、改名和删除文件，确认列表增量更新且没有重复项。
4. 选择项目点击“加载所选视频”：恢复历史位置但保持暂停。
5. 双击或按 Enter：恢复未完成历史并立即播放。
6. 检查上一项、下一项和连续播放；连续播放默认关闭、到末尾停止且不循环。
7. 清除单项历史和全部历史，确认二次确认和结果可见。
8. 同时打开四类 Document 各两个，分别输入不同测试密码并启动不同操作。
9. 关闭其中一个 Document，确认其他 Document 的任务、播放器和列表不受影响。
10. 关闭全部 Document 和宿主，确认没有意外顶层视频窗口或后台 UI 持续运行。

100 文件队列、1,000 文件媒体库、八 Document 资源归零属于自动化硬门禁；人工检查关注操作
目标、提示和隔离是否清楚，不重复手工制造全部规模。

## 11. 诊断导出与隐私人工检查

单文件播放器和媒体库播放器都要覆盖：

- 正常播放时导出成功；
- 错误密码后导出成功，错误域为 `authentication`；
- 使用不完整发布副本启动时导出成功，错误域为 `deployment` 或 `platform`；
- 取消保存不显示错误；
- 选择无写权限位置时只显示可行动提示，不显示异常原文或绝对路径；
- 导出期间按钮禁用，播放器、Dock 和 UI 仍可响应。

不要直接破坏工作区部署。复制发布结果到临时目录后移除副本中的一个必要原生文件，再用该
副本验证部署失败；结束后删除临时副本。

对导出的 JSON 执行：

```powershell
$diagnostic = '<诊断 JSON 的绝对路径>'
Get-Content -Raw -LiteralPath $diagnostic | ConvertFrom-Json | Out-Null
(Get-Item -LiteralPath $diagnostic).Length
Select-String -LiteralPath $diagnostic -Pattern `
  'password|derivedKey|authenticationContext|publicDescription|filePath|[A-Za-z]:\\Users\\'
```

预期：

- JSON 可解析，UTF-8 文件不超过 64 KiB；
- 不包含密码、派生密钥、认证上下文、媒体完整路径、用户名目录；
- 不包含公开标题/描述、原始 stderr、媒体明文字节或 `ftypisom` canary；
- 插件内运行时位置使用 `$PLUGIN/...`，目录逃逸只显示 `outside-plugin`。

## 12. 人工签字

第 7～11 节全部通过后，由实际验收人执行：

```powershell
.\scripts\Approve-VideoSecurityPlayerG11.ps1 `
  -Approver "实际验收人姓名" `
  -ConfirmAllManualChecks
```

脚本拒绝以下情况：

- 技术报告来自其他 Git revision；
- 技术运行不是 clean worktree 正式运行；
- `formalSignoffReady` 不是 `true`；
- 技术运行后产品代码或文档发生变化；
- 没有显式确认完成人工清单。

批准后检查：

```powershell
Get-Content -Raw .\TestResults\G11\g11-final-acceptance.json |
  ConvertFrom-Json |
  Format-List
```

只有 `finalAcceptancePassed=true` 才能更新路线图为 G11 完成。

## 13. 失败处理与证据规则

任何门禁失败时：

1. 不删除测试、不放宽规模、阈值或敏感扫描规则；
2. 查看控制台中第一个失败阶段和 `artifacts/VideoSecurityPlayer/...` 原始产物；
3. 在实际责任所有者中做最小修复，并增加对应回归测试；
4. 先重跑相关单元/阶段门禁，再从头重跑 G11；
5. 若修复要求修改 SECVID03 或增加新平台，停止 G11，单独评审升级方案。

可提交目录只保存脱敏、小体积 JSON：

```text
TestResults/G11/
```

大文件、中间容器、原始运行目录保存在：

```text
artifacts/VideoSecurityPlayer/g11/
```

不要提交后者。不要使用 `git reset --hard`、宽泛递归删除或覆盖 G10 审核基线来处理失败。

## 14. 后续平台结论

G11 完成后仍只支持 Windows x64。平台接口证明 Windows 细节已经隔离，不证明 Linux/macOS
已经可用。若开始跨平台工作，应建立独立项目，至少单独交付原生视频表面、LibVLC 包布局、
安装/升级、真实窗口门禁、性能基线和发行验收。
