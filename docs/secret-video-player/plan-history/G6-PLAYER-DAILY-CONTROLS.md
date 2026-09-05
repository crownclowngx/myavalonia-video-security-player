# G6：播放器日常控制完成记录

> 完成日期：2026-07-25  
> 生产基线：Windows x64、LibVLCSharp 3.9.4、LibVLC 3.0.21、SECVID03  
> 结论：G6 代码、自动化测试和两轮真实窗口门禁均已完成

## 1. 交付范围

G6 在不改变 SECVID03、安全播放流和 Document 隔离方式的前提下交付：

- 覆盖当前宿主窗口内容区、保留系统标题栏的全屏模式；
- `Esc` 安全退出和播放器作用域快捷键；
- `0.5 / 0.75 / 1.0 / 1.25 / 1.5 / 2.0` 六档倍速；
- 音轨、字幕轨发现与切换，以及稳定的“关闭字幕”选项；
- 媒体库上一项、下一项和默认关闭、不循环的连续播放；
- 倍速、轨道、位置和播放/暂停状态的 Dock/全屏表面恢复；
- 真实双音轨、内嵌字幕资产和真实 LibVLC 门禁。

G6 没有实现磁盘设置、播放历史、目录监听、递归扫描或 SECVID02。

## 2. SOLID 边界

| 类型 | 单一职责 |
| --- | --- |
| `SecureVideoPlayer` | 串行化播放用例，维护媒体/用户意图代次，发布不可变快照 |
| `IPlaybackPlayerHost` | 把领域命令适配为 LibVLC API，不决定界面交互 |
| `VideoPlayerControlViewModel` | 把播放快照投影为属性和命令，不引用 Window、OverlayLayer 或 HWND |
| `VideoPlayerControl` | 处理 Avalonia 焦点、按键和视觉树迁移 |
| `IPlaybackNavigationContext` | 只向播放器暴露可选列表导航，不泄漏媒体库实现 |
| `SecretVideoLibraryViewModel` | 维护当前播放路径、可见相邻项和连续播放 |
| `EmbeddedVideoSurface` | 创建/销毁 HWND，并提供不可伪造的表面代次 |

播放器 ViewModel 依赖播放会话接口；媒体库通过可选导航端口接入同一个播放器控件。
单文件播放器没有导航上下文，因此不会显示上一项、下一项和连续播放。

## 3. 控制快照与原生适配

`PlaybackSnapshot.Controls` 持有不可变的 `PlaybackControlSnapshot`：

- 当前倍速；
- 净化后的音轨和字幕轨；
- 当前选中的真实 LibVLC 轨道 ID。

轨道显示名会去除控制字符并限制为 128 个 Unicode Rune。空名称使用稳定回退文本。
字幕集合总是包含 `Id = -1` 的“关闭字幕”；密码、文件路径和原生异常文本不会进入控制快照。

轨道发现发生在媒体提交后和首个真实 `Playing` 事件后。原因是 `MediaPlayer.Play()` 返回成功
只表示 LibVLC 接受命令，部分容器需要解复用器真正进入播放后才公开完整轨道。首次
`Playing` 刷新以媒体代次去重，旧媒体的迟到结果不会写入新快照。

合法控制被 LibVLC 拒绝时返回 `ControlUnavailable`，保持媒体处于可播放状态，不进入
`Faulted`。倍速失败回退 `1.0`；轨道失败保留旧选择。

## 4. 相对 Seek 与快捷键

| 按键 | 行为 |
| --- | --- |
| `Space` | 播放/暂停 |
| `Left` / `Right` | 后退/前进 5 秒 |
| `Up` / `Down` | 音量增加/降低 5% |
| `Esc` | 仅在全屏时退出 |

相对 Seek 在获得播放操作门后读取 PlayerHost 的真实当前位置，再计算并限制目标。
因此连续按键不会反复基于过期 UI 快照计算。

快捷键策略不注册应用级热键；包含 Ctrl、Alt 或 Meta 时直接放行。焦点位于文本框、
密码输入、组合框、滑块、列表或按钮时不拦截。全屏 `Esc` 由 TopLevel 的临时隧道路由
处理，退出后立即解除订阅。

## 5. 窗口内容区全屏

全屏只迁移一份 `PlayerShell`：

```text
普通模式：NormalPlaceholder → PlayerShell
全屏模式：OverlayLayer → FullscreenHost → PlayerShell
```

不会创建第二个 `VideoPlayerControl`、`EmbeddedVideoSurface` 或 `MediaPlayer`。

Avalonia 11.3 的 `NativeControlHost` 在离开视觉树时把 HWND 销毁延迟到
`DispatcherPriority.Background`，以支持跨 TopLevel 重设父级。协调器在加入新父容器前
等待该后台队列，确保旧 HWND 先销毁、恢复快照先保存，然后再创建新表面代次。

进入和退出由异步门串行化；视觉树迁移完成后，View 以请求修订号向 ViewModel 提交结果。
卸载、DataContext 替换、Document 关闭、TopLevel 关闭或恢复失败都会执行幂等清理。

表面恢复顺序为：

```text
绑定新 HWND
→ 恢复媒体、位置及播放/暂停
→ 应用倍速
→ 刷新轨道
→ 恢复仍存在的音轨/字幕 ID
→ 发布最终快照
```

高级控制恢复失败不会否定位置和播放状态恢复。

## 6. 媒体库导航与连续播放

媒体库使用规范化绝对路径 `CurrentPlayingPath` 表示播放身份，不长期保存扫描产生的
列表项引用。相邻项从当前 `VisibleItems` 计算，因此遵循当前搜索结果和文件名排序。

规则如下：

- 第一项没有上一项，最后一项没有下一项；
- 当前播放路径被搜索隐藏时，两个导航命令均禁用；
- 选择列表项不会改变播放身份；
- 候选播放失败时保留旧播放身份；
- 刷新后相同路径重新出现时自动恢复导航；
- 连续播放默认关闭，只响应当前媒体代次第一次自然 `Ended`；
- 手动停止和播放失败不推进，列表末尾不循环；
- 搜索、切换文件夹、关闭连续播放或任何手动播放意图都会取消未提交的自动推进；
- 公共密码仍只保存在当前 Document，不复制到导航模型或结束事件。

## 7. 安全与资源不变量

- 不修改 SECVID03，不支持 SECVID02。
- 不创建完整明文临时视频。
- 倍速、轨道和导航模型不包含密码。
- 错误不透传原始轨道名、私有路径或 LibVLC 异常文本。
- 每个 Document 仍只有一个长期存在的 `MediaPlayer`。
- 普通媒体切换不重建 HWND；全屏和 Dock 迁移按设计创建新表面代次。
- Dispose 会取消恢复/自动推进、退订事件并释放覆盖层。

## 8. 自动化与真实门禁

新增测试组：

- `G6PlaybackControlTests`：倍速、相对 Seek、轨道、失败保持和表面恢复；
- `G6PresentationPolicyTests`：按键映射、修饰键和 Esc 作用域；
- `G6LibraryNavigationTests`：当前筛选结果中的相邻项和路径身份。

2026-07-25 最终结果：

| 门禁 | 结果 |
| --- | --- |
| MySmallTools Release 构建 | 0 警告、0 错误 |
| `MySmallTools.Tests` | 132/132 |
| `MyAvaloniaManagement.PluginTests` | 20/20 |
| 真实资产完整性测试 | 4/4 |
| 真实窗口完整门禁第 1 轮 | 100 生命周期、20 Dock、30 媒体切换，通过 |
| 真实窗口完整门禁第 2 轮 | 100 生命周期、20 Dock、30 媒体切换，通过 |

两轮报告均为 `Success=true`、失败项 0；最终播放器、媒体输入、加密流、表面恢复、
明文缓存、原生命令调度器和回收器计数全部为 0。

真实 Harness 还验证了：

- 六档倍速均被 LibVLC 接受；
- 两条 AAC 音轨逐条切换；
- mov_text 内嵌字幕启用及 `-1` 关闭；
- 全屏进出创建新 HWND，但保持同一个 Document 级 `MediaPlayer`；
- 后续普通媒体切换不重建 HWND。

LibVLC 在压力运行中仍会输出既有 `imem`、D3D11 thumbnail 和 paused prefetch 诊断；
这些内容没有进入产品错误模型，结构化门禁与资源快照均正常。

## 9. 相关入口

- [播放会话](../../../src/VideoSecurityPlayer.Plugin/Business/SecretVideoPlayer/Playback/SecureVideoPlayer.cs)
- [播放模型](../../../src/VideoSecurityPlayer.Plugin/Business/SecretVideoPlayer/Playback/PlaybackModels.cs)
- [播放器 ViewModel](../../../src/VideoSecurityPlayer.Plugin/ViewModels/SecretVideoPlayer/VideoPlayerControlViewModel.cs)
- [播放器 View](../../../src/VideoSecurityPlayer.Plugin/Views/SecretVideoPlayer/VideoPlayerControl.axaml.cs)
- [媒体库 ViewModel](../../../src/VideoSecurityPlayer.Plugin/ViewModels/SecretVideoPlayer/SecretVideoLibraryViewModel.cs)
- [真实资产说明](../reference/real-media-test-assets.md)
- [实施路线图](ROADMAP.md)
