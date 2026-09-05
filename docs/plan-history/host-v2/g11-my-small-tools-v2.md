# G11：MySmallTools Host V2 迁移

> 完成日期：2026-08-21。
>
> 性质：开发期迁移门禁，不是发布验收。摘要固定为 `aiflow=false`、`windowsCi=false`、
> `windowsSmoke=false`、`releaseAcceptance=false`、`releaseGate=false`、`publishable=false`。

## 1. 结果

MySmallTools 已从 Legacy/Dock 插件模型迁移到最终版
`MyAvaloniaManagement.PluginSdk` 与 `MyAvaloniaManagement.PluginSdk.UI`。插件入口现在直接实现
最终 `IPluginModule`，并启用 `ManagedPluginUseV2EntryContract`。生产程序集不再引用
Legacy Contracts、Dock、Common 或 Host；V2 Host 可以通过真实 Loader、Preflight、Registry、插件
Provider 和 `ManagedDocumentDockable` 创建、隔离及关闭四个 Document。

SECVID03 格式、LibVLC 版本、媒体库数据、单视频播放、连续播放和加解密批处理语义均未升级。
本阶段没有兼容层、服务定位器、抽象工厂或公共 Document 基类，也没有扩展文件选择 SDK。

## 2. SOLID 责任划分与设计思路

本次以所有权作为拆分依据，而不是按技术名词增加层次：

- Host 独占 Document Scope、Dock Adapter、激活、展示标题、关闭令牌和最终释放顺序；插件不能创建
  Scope，也不能主动取消宿主令牌。
- 四个根模型只实现很小的 `IPluginDocument` 展示契约；业务状态继续留在各自普通
  `ObservableObject`。少量标题代码保持局部重复，避免为“复用几行代码”制造公共继承层。
- `SecureVideoPlayer` 只编排一次播放会话；`IPlaybackPlayerHost` 只包装当前 Document 独占的
  LibVLC/MediaPlayer；View 只拥有视频表面、窗口全屏交互和事件订阅。
- 加密、解密、媒体库扫描与播放协调对象显式依赖 `IDocumentLifetime`。依赖从构造函数进入，测试与
  生产均没有隐藏的替代生命周期，符合依赖倒置和显式所有权。
- 全屏使用最终 UI SDK 的 `IWindowContentFullscreenHost`，顶层窗口仍是唯一实现者。没有新增 DI
  门面或服务定位器；Legacy 中的重复接口已经删除。

实现只使用构造注入、作用域、不可变展示状态、链接取消令牌和幂等 `Dispose` 这些朴素机制。

## 3. 贡献迁移矩阵

| 稳定 Document ID | 标题 | 根模型 | View | 持久化 |
| --- | --- | --- | --- | --- |
| `myavalonia.plugin.my-small-tools.document.secret-video-player` | 加密视频播放器 | `SecretVideoPlayerViewModel` | `SecretVideoPlayerView` | 否 |
| `myavalonia.plugin.my-small-tools.document.secret-video-library` | 加密视频库播放器 | `SecretVideoLibraryViewModel` | `SecretVideoLibraryView` | 否 |
| `myavalonia.plugin.my-small-tools.document.video-encryptor` | 视频文件加密器 | `VideoEncryptorViewModel` | `VideoEncryptorView` | 否 |
| `myavalonia.plugin.my-small-tools.document.video-decryptor` | 批量视频解密器 | `VideoDecryptorViewModel` | `VideoDecryptorView` | 否 |

插件贡献固定为 4 Document、0 Tool、0 Lifecycle。默认标题来自 Descriptor；自定义标题由
`DocumentActivationContext` 初始化，并通过 `DocumentPresentationState` 与
`PresentationChanged` 通知 Host。旧 Strategy、GUID、Dock `Document` 基类和
`IDocumentScopeFactory` 依赖均已删除。

## 4. 关闭、全屏与原生资源时序

1. Host 开始关闭 Adapter，并先取消该 Scope 的 `IDocumentLifetime.ClosingToken`。
2. 播放、预检、批处理、扫描、筛选和自动续播的链接令牌收到取消；完成回调在写回状态前再次检查
   关闭状态，迟到结果被丢弃。
3. 如果当前 View 正在全屏，View 先用占用者令牌恢复宿主原内容和窗口状态，再拆除视频表面及订阅。
4. 根模型与播放协调对象执行幂等释放；原生命令调度器停止接收工作，MediaPlayer 先 Stop、退订事件、
   解除 Media，再释放 MediaPlayer、LibVLC、SECVID03 输入流和资源回收器。
5. Host 最后释放 Document Scope。重复关闭、View 重复卸载和 `Dispose` 兜底均不得抛异常。

每次创建 Document 都产生独立 Scope，因此播放器、密码、队列、浏览位置和关闭令牌彼此隔离；关闭
一个同类型 Document 不会取消另一个。SECVID03 回调流在原生 Pause 后可能异步把时间短暂归零，
播放器适配层会等待真实 `Paused` 事件后在暂停态重申原位置；这保持了业务语义，也避免 UI 层了解
LibVLC 的回调流细节。

## 5. 不变项、失败处理与回滚

- SECVID03 标识、密钥派生、认证和文件格式不变，不需要内容迁移。
- LibVLCSharp 与原生运行库版本不变；读取元数据不会提前初始化 LibVLC。
- 媒体库索引/历史数据和批处理输入输出语义不变。
- 单个创建或初始化失败由 Host 丢弃该候选 Scope，不发布半成品 Dockable；取消不发布迟到状态。
- 包预检、贡献注册或 Provider 组合失败只隔离 MySmallTools，不影响其他插件。
- 回滚时整体恢复 G11 前的 V1 MySmallTools 源码；V2 Host 不加载 V1 ZIP，也不增加双栈兼容路径。

`LegacyPluginDocumentScopeFactory` 暂不整体删除，继续只服务尚未迁移的 BiliDownloader，留给 G12。

## 6. 专项门禁与实际证据

执行命令：

```powershell
.\scripts\Test-MySmallToolsV2.ps1 -Configuration Release -NoRestore
```

结果位于 `artifacts/test-results/MySmallToolsV2/`：

| 项目 | 本次结果 |
| --- | ---: |
| Plugin/加载/边界定向测试 | 60/60 |
| Headless UI | 16/16 |
| 最终 SDK 定向测试 | 14/14 |
| MySmallTools 完整单元测试 | 184/184 |
| 最终 ZIP 真实加载 | 1/1 |
| 专项合计 | **275/275** |

真实 G3 Harness 使用真实 `MySmallToolsPluginModule` 与 `ManagedDocumentDockable`，完成 20 轮关闭重开，
总耗时 63,488 ms；关闭后的 Document、View 和已释放加密流弱引用存活数均为 0，LibVLC Player、
Media Input、Encrypted Stream、Surface Restore、明文缓存、Native Dispatcher 与 Resource Reaper
最终计数也全部为 0。MP4/WebM、音轨、字幕、SECVID03、全屏进出、暂停/Seek、文件锁和媒体库导航
全部通过。

两次隔离构建产生相同的 431 文件测试 ZIP；文件路径、长度、逐文件 SHA 和归档 SHA 全部一致。
归档 SHA-256 为
`2B2879D28D92A8251A21674D83F5752814AE2A09D0D01EED999061E168D38126`。包中保留
LibVLCSharp 与原生运行库，不携带 Host、Legacy Contracts、最终共享 SDK、Avalonia、Dock、Common
或 Microsoft Extensions DLL。解压后的最终 ZIP 已通过真实加载器、预检、Registry 和 Provider 组合，
并发布四个 Document。

专项通过后又执行了非发布全量回归：锁定还原成功；全解决方案 Release
`-warnaserror` 为 0 警告、0 错误；Host Unit 172、UI 52、Plugin 209，共 **433/433**，行覆盖率
83.15%、分支覆盖率 68.74%；BiliDownloader **719/719**、DaTang **62/62**；Core/UI API v2
基线、7 个破坏性负例、SDK nupkg 内容/依赖与 10 个反向消费夹具全部通过。四插件包矩阵完成每插件
两次确定性构建、26 个契约负例和最终 ZIP Host 加载。文档核心及完整门禁分别通过，完整门禁检查
49 份文档、281 个本地链接、95 个脚本路径和 39 个项目路径；`git diff --check` 无错误。

全量回归同样没有启用 Windows Smoke，也没有调用任何 CI 或发布门禁。

## 7. 明确未执行的流程

本阶段没有使用 AIFLOW，没有运行 Windows CI、Windows Smoke、ReleaseAcceptance、V1/发布总门禁、
签名、上传或标签流程。历史产品脚本 `Accept-MySmallToolsG11.ps1` 与
`Approve-MySmallToolsG11.ps1` 没有被调用；它们的历史发布证据不得作为本次 Host V2 G11 证据。
