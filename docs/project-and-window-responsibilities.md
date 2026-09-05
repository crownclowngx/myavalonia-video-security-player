# 项目和窗口职责

| 项目 | 职责 |
| --- | --- |
| VideoSecurityPlayer.Plugin | 四种真实 View/Document、全部业务服务、Module 和 Workflow Action |
| VideoSecurityPlayer.Standalone | 公开 SDK 注册适配、多标签调试、独立数据目录、内容区全屏与释放 |
| VideoSecurityPlayer.Tests / UiTests | 原业务回归、真实界面布局及独立窗口生命周期 |
| VideoSecurityPlayer.HostTests / HostUiTests | 显式 HostRepositoryRoot 联调真实 Loader、Registry、Workspace、Dock |
| tools 下三个项目 | 原安全基准、部署/内存验收、真实 Windows 播放 Harness |

Standalone 通过真实 Module 收集文档和 Action 声明，不维护第二份功能列表。每次打开独立 Scope，每个 Scope 有自己的关闭令牌、模型和界面。插件服务的 singleton/scoped/transient 关系保持原实现。

标签关闭先解绑视图并发出取消信号，再释放 Scope。窗口退出关闭全部文档并释放 Provider；初始化失败同样释放候选 Scope。内容区全屏仅通过 SDK 的 IWindowContentFullscreenHost 租约适配。

独立调试数据为 `%LOCALAPPDATA%/VideoSecurityPlayer/Standalone/user-data-v1.json`。正式插件继续使用 `%LOCALAPPDATA%/MyAvaloniaManagement/MySmallTools/secret-video-player/user-data-v1.json`，不迁移或重置正式数据。

Standalone 不模拟完整 Host，不提供 Dock 拖拽、安装升级和 Workflow Gateway。真实宿主联调项目及 Harness 不进入默认独立解决方案，生产插件与 standalone 均不引用 Host 内部实现。
