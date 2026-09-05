# G4：P0 部署、验收与发布基线

> 实施日期：2026-07-24
>
> 支持范围：Windows x64、`net10.0`、插件私有 LibVLC
>
> 发布标识：`p0-win-x64 + Git short revision`

## 1. 结果与边界

G4 把 P0 能力收口为一条可重复执行的发布链路：部署目录可自检，错误可操作，发布包可校验，大文件内存和真实窗口播放可作为硬门禁。

本阶段没有修改 SECVID03 的磁盘布局、KDF、nonce、AAD、块大小或认证规则，也没有增加播放器功能。Linux、Windows x86、安装器、宿主级全局任务中心和新的插件启动协议均不在范围内。

实现遵循以下设计取舍：

- 部署检查、原生运行时初始化和 Document 播放资源各自只有一个变化原因，落实 SRP。
- ViewModel 和播放器依赖窄接口，不直接定位文件或构造 LibVLC，落实 DIP 与 ISP。
- 只引入不可变 Result Object 和一个负责成套资源创建的 Abstract Factory；没有引入策略链、Service Locator 或全局 Coordinator。
- `IPlaybackDeploymentProbe` 是进程级无状态 Singleton；backend、session、dispatcher 和 reaper 仍属于 Document Scope。

## 2. 部署自检

`IPlaybackDeploymentProbe.Check()` 只读文件系统和 PE/程序集元数据，不调用 `Core.Initialize`，也不创建 `LibVLC` 或 `MediaPlayer`。插件根目录始终来自 `MySmallTools.dll` 的实际位置，不使用工作目录、宿主根目录、`PATH` 或系统 VLC。

一次检查会尽量收集所有可识别问题，避免用户修复一个文件后才发现下一个问题。结果由不可变的 `DeploymentCheckResult` 表示：

- `IsReady`：全部检查通过。
- `PluginDirectory`：实际插件目录。
- `RuntimeDirectory`：实际私有 LibVLC 目录。
- `Issues`：稳定问题码、用户摘要、实际路径和建议操作。

### 2.1 稳定问题码

| 问题码 | 含义 | 建议动作 |
| --- | --- | --- |
| `UnsupportedOperatingSystem` | 当前不是 Windows | 使用 Windows x64 宿主 |
| `UnsupportedProcessArchitecture` | 当前进程不是 x64 | 启动 x64 宿主 |
| `PluginDirectoryMissing` | 插件目录不存在 | 重新解压完整发布包 |
| `ManagedBridgeMissing` | 缺少托管桥接程序集 | 重新部署插件目录 |
| `ManagedBridgeInvalid` | 托管 DLL 无效或程序集名称不匹配 | 删除旧目录后重新部署 |
| `NativeLibraryMissing` | 缺少 `libvlc.dll` 或 `libvlccore.dll` | 重新部署完整原生树 |
| `NativeArchitectureMismatch` | 原生 DLL 的 PE 机器类型不是 AMD64 | 使用 win-x64 发布包 |
| `NativePluginSetIncomplete` | MP4、WebM、基础解码、D3D11 或 Windows 音频模块缺失 | 恢复完整冻结版 LibVLC 树 |
| `NativeInitializationFailed` | 文件检查通过但原生初始化失败 | 重新部署并重启宿主 |

检查路径和建议可进入 UI 与验收报告；原始异常文本、密码、派生密钥和明文块不可进入这些对象。

### 2.2 backend 创建时机

原计划把 backend 严格推迟到“首次播放”。真实窗口压力测试证明，若 Avalonia `VideoView` 已经完成首次绑定，再从后台线程动态替换 `MediaPlayer`，LibVLC/Avalonia 的 HWND 与 vout 生命周期在 100 次 Document 循环中存在原生崩溃风险。

最终采用更稳定的时序：

1. 创建播放 Document 和 ViewModel。
2. 立即运行只读部署探针。
3. 检查失败时保持 backend 未创建，文档仍可打开并显示诊断。
4. 检查成功时，在视图首次绑定前创建该 Document 唯一的 backend。
5. 后续加载和媒体切换复用同一个 PlayerHost，不重建播放器。

这仍满足“损坏部署不会因打开文档而触发原生崩溃”的核心目标，同时保留 G3/G3.1 已验证的 HWND 绑定顺序。`SecureVideoPlayer` 在首次加载时仍做一次幂等的 `EnsureCreatedForPlayback`，作为非标准调用路径的防线。

## 3. 用户行为

单文件播放器和媒体库文档打开时显示部署状态：

- 自检失败：显示稳定代码、原因、实际检查目录、建议动作和“重新检测”按钮。
- 自检失败期间：加载、播放和 Seek 等依赖 LibVLC 的命令不可执行；公开信息和媒体库扫描仍可使用。
- 修复目录后：点击“重新检测”，成功后恢复播放命令。
- 初始化失败：显示 `NativeInitializationFailed`，建议重新部署并重启宿主。
- 媒体解析或解码失败：显示稳定诊断代码与下一步操作，不向 UI 传播原始异常文本。

`PlaybackFailureCode.DeploymentUnavailable` 用于区分部署问题与密码、格式、解析或解码问题。`PlaybackFailure` 的 `SuggestedAction` 和 `DiagnosticCode` 为可选字段，保持现有调用方兼容。

## 4. 发布产物

唯一入口为：

```powershell
.\scripts\Release-MySmallToolsP0.ps1
```

正式发布要求 clean worktree。开发中验证未提交变更可显式执行：

```powershell
.\scripts\Release-MySmallToolsP0.ps1 -AllowDirty
```

这种产物会在验收报告中写入 `publishable: false`，不能冒充正式发布候选。

输出目录：

```text
artifacts/MySmallTools/p0-win-x64/
├─ MySmallTools-p0-win-x64-<short-commit>.zip
├─ MySmallTools-p0-win-x64-<short-commit>.manifest.json
├─ MySmallTools-p0-win-x64-<short-commit>.acceptance.json
├─ deployment-probe.json
├─ memory-gate.json
├─ playback-run1.json
└─ playback-run2.json
```

ZIP 可直接解压到宿主根目录：

```text
Controls/SmallTools/
├─ MySmallTools.dll
├─ LibVLCSharp.dll
├─ LibVLCSharp.Avalonia.dll
├─ mysmalltools.release.json
└─ native/win-x64/libvlc/...
```

构建文件集在 `MySmallTools.csproj` 中集中声明。宿主部署、发布暂存和打包复用同一目录规范，完整保留冻结版 LibVLC 的 plugins、Lua 和辅助资源，不在 P0 阶段做高风险裁剪。

Manifest 使用排序后的 `/` 相对路径，记录 schema、插件 ID、目标框架、RID、版本、源提交、文件长度和 SHA-256；不记录绝对路径、用户名、构建机目录或媒体信息。ZIP 使用稳定条目顺序和规范化时间戳。

## 5. 串行门禁

脚本按以下顺序执行，任一步失败都会返回非零退出码：

1. 检查 Windows x64、Git 状态和 .NET 10。
2. 独立构建 MySmallTools Release，并把自身警告视为失败。
3. 串行运行 MySmallTools 与宿主插件测试，避免共享 `obj/bin` 的构建锁竞争。
4. 创建干净暂存目录、Manifest 和确定性 ZIP。
5. 解压 ZIP，复算所有哈希并运行生产部署探针。
6. 运行宿主扫描回归，保证不会进入 `native`、`runtimes` 或 `libvlc`。
7. 以子进程运行大文件内存门禁。
8. 连续运行两轮 G3.1 真实窗口门禁。
9. 汇总测试、构建、部署、内存、播放、资源计数和产物哈希。

宿主其他插件的既有警告可以记录，但不计为 MySmallTools 新增警告。

## 6. 大文件内存口径

`MySmallTools.ReleaseAcceptance` 流式生成确定性源文件，不向仓库提交大文件。64 MiB 与 512 MiB 场景分别在独立子进程中执行：

- 完整加密与完整解密；
- 源文件和解密文件 SHA-256 比较；
- 128 次覆盖首尾、块边界和确定性随机位置的读取；
- `.partial-*`、文件锁和四块 1 MiB 明文缓存检查；
- 每 100 ms 采样 managed heap 与 private bytes。

硬阈值只比较文件规模扩大八倍后的峰值增量：

- managed heap 不得比 64 MiB 场景多 64 MiB 以上；
- private bytes 不得比 64 MiB 场景多 128 MiB 以上。

不同机器的耗时只进入报告，不作为硬阈值。

## 7. 自动化覆盖

`G4DeploymentTests` 覆盖完整部署、两个托管桥接缺失、无效程序集、核心原生 DLL 缺失、非 AMD64 PE、关键插件模块缺失、多问题聚合、修复后重检、并发一次初始化、结构化初始化失败、UI 阻断/恢复和 backend 单次创建。

宿主扫描测试使用大小写不敏感参数矩阵验证 `native`、`runtimes` 和 `libvlc` 在任意深度均被首次扫描与依赖解析排除。

## 8. 本次验收证据

2026-07-24 在 Windows x64、.NET 9 环境执行完整流程，结果如下：

| 门禁 | 结果 |
| --- | --- |
| MySmallTools Release 构建 | 0 警告、0 错误 |
| MySmallTools.Tests | 100/100 通过 |
| 宿主插件测试 | 20/20 通过 |
| ZIP 解压、Manifest 哈希与生产探针 | 通过 |
| 64 MiB managed/private 峰值 | 3,263,848 / 14,843,904 字节 |
| 512 MiB managed/private 峰值 | 3,247,400 / 18,182,144 字节 |
| 两轮真实播放门禁 | 均通过 |
| Dock/媒体切换 | 每轮 20/30 次 |
| UI heartbeat 最大间隔 | 26 ms / 23 ms |
| 最终八类播放资源计数 | 两轮均全部为零 |

本轮验证使用 `-AllowDirty`，因为实现尚未提交，所以报告正确标记为 `publishable: false`。代码和全部门禁已通过；正式发布前仍需提交变更，并在 clean worktree 不带 `-AllowDirty` 再执行一次脚本。

LibVLC 在测试过程中可能向 stderr 输出 `imem` 或 Direct3D11 缩略图警告；硬门禁以结构化报告中的真实播放、Seek、heartbeat 和最终资源计数为准。

## 9. 完成判定

G4 的实现与本地完整验收已经完成。正式发布候选还必须满足：

- worktree clean；
- 脚本不使用 `-AllowDirty`；
- `acceptance.json` 中 `publishable` 为 `true`；
- 人工破坏部署目录后，用户能在两个播放文档中看到准确原因、路径、建议与重新检测入口。

提交后无需人工重新拼装文件；只应重跑统一发布脚本，避免绕开构建、哈希、探针、内存或真实播放门禁。
