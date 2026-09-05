# G3：真实媒体播放与 Dock 稳定性

> 实施日期：2026-07-24
> 状态：已完成
> 适用范围：Windows x64、MySmallTools 安全视频播放器、Avalonia Dock、LibVLCSharp 3.9.4、VideoLAN.LibVLC.Windows 3.0.21
> 兼容原则：不改变 SECVID03 磁盘格式、PBKDF2 参数、AES-GCM 分块、nonce、AAD 或 Tag

## 1. 完成目标与非目标

G3 在 G1 的安全边界和 G2 的资源闭环之上，把真实播放从“可调用代码”提升为可重复验证的产品能力：

- 真实 MP4/WebM 完成加密、认证读取、播放、暂停、停止、Seek、结束和解密 SHA-256 还原。
- 媒体切换采用候选 Lease；候选失败或过期不会破坏当前媒体。
- 用户命令、媒体提交和 Dock 恢复统一串行化；媒体、请求和视频表面各自携带代次。
- 原生 HWND 重建前同步停止旧 vout 并解绑，重建后按快照恢复位置及播放/暂停状态。
- 认证读取失败优先传播 `CorruptedContent`，不被 LibVLC 通用解码错误覆盖。
- 独立 Windows x64 真实窗口门禁覆盖真实 Dock、`EmbeddedVideoSurface`、LibVLC 和 Document Scope。
- 连续两轮 100 次生命周期压力通过，媒体、播放器、输入、加密流、恢复任务和明文缓存均归零。

G3 不增加全屏、倍速、快捷键、音轨/字幕选择、连续播放、历史存储或跨平台表面抽象；这些仍属于 G6/G9。

## 2. 实施前审计与关闭结果

| 编号 | 实施前问题 | 关闭结果 |
| --- | --- | --- |
| G3-F01 | ViewModel 依赖具体播放器并暴露原生 `MediaPlayer` | ViewModel 只依赖 `ISecureVideoPlaybackSession`；原生输出隔离到 `ILibVlcVideoOutputSource` |
| G3-F02 | 播放器同时承担编排、LibVLC、资源和错误文本 | 拆为会话 Facade、`PlaybackMediaLease`、输入适配器和错误映射器 |
| G3-F03 | Parse 结果被忽略且无限等待 | Parse 限定 15 秒并检查状态；详见第 6 节的 LibVLC 3.x 兼容事实 |
| G3-F04 | `bool + string` 与原始异常文本传播 | 使用 `PlaybackOperationResult`、`PlaybackFailure` 和稳定失败代码 |
| G3-F05 | ViewModel 给迟到原生事件补当前代次 | 事件在 Lease 创建时绑定媒体代次；会话层拒绝非当前 Lease |
| G3-F06 | Pause、Seek、Stop 和状态读取未统一串行化 | 所有控制操作经过同一会话门；Pause/Stop/Dispose 幂等，Seek 有 5 秒上限 |
| G3-F07 | 先释放旧媒体再验证候选 | 候选认证并准备成功后才原子提交，随后释放旧 Lease |
| G3-F08 | `MediaInput.LastError` 无类型、可覆盖 | 首次失败优先、线程安全、一次性消费，并在原生回调返回后异步上报 |
| G3-F09 | Dock 只有纯策略测试 | 新增真实 Win32 Avalonia 窗口、真实 Dock、HWND 和 vout 门禁 |
| G3-F10 | 没有 100 次原生生命周期证据 | 两轮各 100 次通过，六项最终资源计数均为零 |

G3-F01～G3-F10 均已关闭。

## 3. SOLID 边界

### 3.1 SRP

| 组件 | 单一职责 |
| --- | --- |
| `VideoPlayerControlViewModel` | 展示状态、命令和用户拖动意图 |
| `SecureVideoPlayer` | 串行化播放用例、候选提交、代次与恢复快照 |
| `PlaybackMediaLease` | 独占一代 `MediaPlayer → Media → MediaInput → 加密流` |
| `SeekableStreamMediaInput` | 把可 Seek 托管流适配为 LibVLC 回调并保存首个失败 |
| `PlaybackFailureMapper` | 把内部异常分类为稳定安全错误 |
| `EmbeddedVideoSurface` | 创建/销毁 HWND 并产生不可伪造的表面代次 |
| Integration Harness | 真实环境驱动、超时、断言和脱敏 JSON 报告 |

### 3.2 OCP、LSP、ISP、DIP

- **OCP**：会话依赖 `IPlaybackMediaLeaseFactory`；快速测试使用替身 Lease，真实运行使用 LibVLC Lease，无需修改 ViewModel。
- **LSP**：替身和真实 Lease 遵守相同取消、Seek 完成、事件代次和重复 Dispose 语义。
- **ISP**：业务控制与原生输出分成两个窄接口；业务测试不需要构造 `MediaPlayer`。
- **DIP**：ViewModel 依赖 `ISecureVideoPlaybackSession`；会话依赖 Lease 工厂；DI Scope 绑定真实实现。

没有引入 Repository、Mediator、全局事件总线、通用文件系统抽象或 State 类层次。状态使用小型枚举和不可变快照，避免为模式而模式。

## 4. 务实采用的模式

1. **应用服务 / Facade**：`SecureVideoPlayer` 是唯一播放用例入口，统一用户和 Dock 操作。
2. **端口与适配器**：会话契约、LibVLC 输出、MediaInput 和真实门禁各处于明确边界。
3. **资源所有者**：`PlaybackMediaLease` 集中规定停止、解绑和逆序释放。
4. **代次令牌**：媒体请求、已提交媒体和 HWND 表面使用独立单调递增代次。

这些模式分别解决竞态、原生依赖、释放顺序和迟到事件问题，没有增加无收益的抽象层。

## 5. 播放契约与状态

`ISecureVideoPlaybackSession` 提供：

- `LoadAsync`、`PlayAsync`、`PauseAsync`、`StopAsync`、`SeekAsync`、`ReleaseAsync`
- 同步 `DetachSurface`
- 异步 `AttachAndRestoreSurfaceAsync`
- 统一 `Changed` 事件和只读 `PlaybackSnapshot`

状态集合为：

```text
Empty → Ready → Playing ⇄ Paused
          ↓        ↓
        Stopped   Ended
任意活动状态 → Faulted
任意状态 → Disposed
```

`PlaybackSnapshot` 只包含媒体代次、状态、切换标志、位置、时长、Seek 能力、表面代次、音量和轨道计数。密码只作为 `LoadAsync` 参数进入同步调用链，不进入快照、恢复请求、诊断或队列。

稳定错误代码包括：

- `InvalidRequest`
- `InvalidFormat`
- `AuthenticationFailed`
- `CorruptedContent`
- `InputUnavailable`
- `ParseFailed`
- `DecodeFailed`
- `SurfaceRestoreFailed`
- `Cancelled`
- `Unknown`

UI 只接收稳定代码和预定义安全消息；路径、密码、堆栈和原始异常文本不进入报告。

## 6. 媒体切换与 LibVLC 解析边界

媒体切换顺序：

```text
分配请求意图
→ 取消旧候选
→ 在操作门内创建候选 Lease
→ 打开并认证 SECVID03
→ 15 秒受限 Parse
→ 再次检查取消和请求代次
→ 停止、解绑旧 Lease
→ 提交候选并绑定当前 HWND
→ UI 同步切换 VideoView 输出
→ 释放旧 Lease
```

失败候选只释放自己；当前媒体、位置和用户状态不被覆盖。30 个并发切换请求中，旧请求会被主动取消，只有最后请求允许提交。

### LibVLC 3.0.21 的事实边界

真实门禁确认：LibVLC 3.0.21 对回调式 `MediaInput` 的 Parse 可能返回 `MediaParsedStatus.Skipped`，即使后续真实播放、轨道发现和 Seek 可以成功。若机械拒绝 `Skipped`，所有真实 SECVID03 回调媒体都会被误判。

因此实现采用以下判定：

- `Failed`、`Timeout`：拒绝。
- `Skipped` 且 MediaInput 已记录认证/读取失败：拒绝并保留根因。
- 干净的 `Skipped`：只允许进入后续真实 Play、轨道、读取和 Seek 门禁，不把它单独宣称为解析成功。

这是对固定 LibVLC 版本的窄兼容，不改变 SECVID03，也不把任意解析失败降级为成功。

## 7. 控制语义与错误优先级

- Pause 使用 `SetPause(true)`；重复暂停不会反向恢复。
- Stop、Release、Dispose 均幂等。
- Seek 使用毫秒、夹取到合法范围，并等待 `TimeChanged`；总等待上限为 5 秒。
- 暂停态 Seek 使用 `Play → 等待 vout → Seek → SetPause(true)`，解决 LibVLC 在暂停的回调媒体上不稳定发送 `TimeChanged` 的行为。
- 回调媒体在自然 EOF 时可能只发 `Stopped`；当位置位于最后 500 ms 时，Lease 将其规范化为 `Ended`。显式 Stop 最终仍由会话发布 `Stopped`。
- 用户操作会增加意图代次并取消旧恢复，自动行为不能覆盖新操作。

错误优先级：

1. MediaInput 首次记录的认证、篡改、截断或读取失败。
2. 明确 Parse 状态失败。
3. 没有托管根因时才使用 LibVLC 解码失败。

MediaInput 不能从原生 Read 回调内同步 Stop，否则可能等待自己释放读取锁。实现先保存首个类型化失败并让 Read 返回 `-1`，再在线程池通知 Lease；会话随后停止并进入 `Faulted`。因此真实篡改块稳定报告 `CorruptedContent`。

## 8. Dock 与原生输出时序

```text
旧 HWND 即将销毁
→ 校验表面代次
→ 保存媒体代次、位置、Playing/Paused
→ 取消旧恢复
→ RequestStop
→ MediaPlayer.Stop
→ Hwnd = 0
→ NativeControlHost 销毁窗口

新 HWND 创建
→ 产生新表面代次
→ 绑定 Hwnd
→ 校验媒体代次与用户意图
→ PrepareForPlayback
→ Play 并等待 Playing + VoutCount > 0
→ Seek 并等待位置事件
→ 保持 Playing 或显式 SetPause(true)
```

恢复总超时为 5 秒。旧表面令牌不能解绑新 HWND。`VideoView` 切换原生播放器必须在 UI 线程同步完成，确保它先 Detach 旧播放器，Lease 才能释放旧原生句柄；这关闭了真实门禁发现的 native access violation 竞态。

G3 只封装当前 Windows HWND 绑定，没有提前实现 G9 的跨平台视频表面。

## 9. 资源所有权与诊断

释放顺序：

```text
取消候选和恢复
→ 阻止新命令
→ RequestStop
→ Stop
→ Hwnd = 0
→ MediaPlayer.Media = null
→ Dispose MediaPlayer
→ Dispose Media
→ Dispose MediaInput
→ Dispose SeekableEncryptedVideoStream
→ 清零回调缓冲区、明文 LRU、密文缓冲和派生密钥
→ Document Scope 释放 LibVLC 工厂
```

`SecurePlaybackDiagnostics` 只公开脱敏计数和块编号轨迹：

- LiveLeases
- LivePlayers
- LiveMediaInputs
- LiveEncryptedStreams
- ActiveSurfaceRestores
- CachedPlaintextChunks
- 最近 SECVID03 块访问编号

不记录路径、密码、明文或密钥。

## 10. 测试设计

### 10.1 快速测试

G3 新增 7 项会话专项测试：

- 失败候选保留当前媒体。
- 新请求取消旧请求。
- 旧 Lease 事件不能修改新会话。
- Pause 幂等。
- 用户 Stop 使旧表面恢复失效。
- 错误映射只产生稳定安全消息。
- MediaInput 并发失败首次优先且一次性消费。

原有格式、安全、加解密、媒体库、资源和纯恢复测试全部保留。当前结果为 `MySmallTools.Tests` 82/82，宿主插件测试 15/15。

### 10.2 独立 Windows x64 门禁

`MySmallTools.Playback.IntegrationHarness` 使用真实宿主 App、真实 Win32 Avalonia 窗口、Dock、`EmbeddedVideoSurface`、插件 Document Scope 和私有 LibVLC。它不使用 Headless 后端，因为 Headless 无法验证 HWND 和 vout。

默认矩阵：

- MP4 与 WebM 的加密、真实播放、暂停、停止、固定种子随机 Seek、结束、解密和 SHA-256 还原。
- MP4 必须发现音轨；WebM 必须无音轨，并访问至少三个 SECVID03 块。
- 暂停态和播放态分别执行 20 次 Dock 往返，恢复位置误差不超过 750 ms。
- 30 个快速媒体切换请求只有最后请求提交并可播放。
- 记录真实块轨迹、篡改访问块并断言 `CorruptedContent`。
- 文件删除后加载断言 `InputUnavailable`。
- 100 次“创建 Document—加载—真实读取—Dock 切换—关闭 Scope”。
- 每轮检查资源归零、文件可改名/删除、无意外顶层视频窗口。
- 报告包含 OS、架构、.NET、Avalonia、Dock、LibVLCSharp、原生 LibVLC、资产哈希、循环数、分阶段耗时、随机 Seek 目标、块轨迹和最终资源计数。

门禁使用固定资产、事件驱动等待和明确超时，不使用无条件长时间 Sleep。失败返回非零退出码。

## 11. 验证结果

2026-07-24 验证：

| 项目 | 结果 |
| --- | --- |
| MySmallTools Debug 快速测试 | 82/82 |
| MySmallTools Release 快速测试 | 82/82 |
| 宿主插件 Debug 测试 | 15/15 |
| 宿主插件 Release 测试 | 15/15 |
| MySmallTools Release 独立构建 | 0 警告、0 错误 |
| Windows x64 Release 门禁第 1 轮 | 通过，100 生命周期、20+20 Dock、30 切换 |
| Windows x64 Release 门禁第 2 轮 | 通过，100 生命周期、20+20 Dock、30 切换 |

两份脱敏报告：

- `TestResults/G3/g3-playback-windows-x64-run1.json`
- `TestResults/G3/g3-playback-windows-x64-run2.json`

两轮最终资源均为：

```text
LiveLeases=0
LivePlayers=0
LiveMediaInputs=0
LiveEncryptedStreams=0
ActiveSurfaceRestores=0
CachedPlaintextChunks=0
```

宿主 Release 插件测试仍显示 `DaTangAccountingHelpPlug` 的既有警告；MySmallTools 独立构建没有警告。LibVLC 会向 stderr 输出 `imem` 和 D3D11 诊断，这些上游日志不改变结构化门禁结果；运行时完整性和更完整诊断归 G4。

## 12. 验证命令

```powershell
dotnet test Plugins/MySmallTools/MySmallTools.Tests/MySmallTools.Tests.csproj -c Debug
dotnet test Plugins/MySmallTools/MySmallTools.Tests/MySmallTools.Tests.csproj -c Release
dotnet test Host/MyAvaloniaManagement.PluginTests/MyAvaloniaManagement.PluginTests.csproj -c Debug
dotnet test Host/MyAvaloniaManagement.PluginTests/MyAvaloniaManagement.PluginTests.csproj -c Release
dotnet build Plugins/MySmallTools/MySmallTools/MySmallTools.csproj -c Release
dotnet run --project Plugins/MySmallTools/MySmallTools.Playback.IntegrationHarness/MySmallTools.Playback.IntegrationHarness.csproj -c Release -- --report TestResults/G3/g3-playback-windows-x64.json
```

真实门禁必须在交互式 Windows x64 会话运行。仓库没有 CI 配置，G3 只交付可由 Windows runner 调用的独立非零退出码门禁，不虚构 CI 标签。

## 13. 实施结论

G3 已完成。真实媒体、并发切换、类型化错误、Dock HWND/vout 恢复和原生资源释放已形成可重复证据；播放器全过程不创建完整明文临时视频。

G4 可以在此基础上补齐原生运行时完整性自检和发布诊断。G6/G9 继续负责播放器体验和跨平台能力，不应回退 G3 的窄会话接口、候选 Lease 或 Document 独立所有权。
