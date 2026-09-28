# 项目和窗口职责

| 项目 | 职责 |
| --- | --- |
| VideoSecurityPlayer.Plugin | 四种真实 View/Document、单例设置 Tool、全部业务服务、Module 和 Workflow Action |
| VideoSecurityPlayer.Standalone | 公开 SDK 注册适配、多标签与 Tool 调试、独立数据目录、内容区全屏与释放 |
| VideoSecurityPlayer.Tests / UiTests | 原业务回归、真实界面布局及独立窗口生命周期 |
| VideoSecurityPlayer.HostTests / HostUiTests | 显式 HostRepositoryRoot 联调真实 Loader、Registry、Workspace、Dock |
| tools 下三个项目 | 原安全基准、部署/内存验收、真实 Windows 播放 Harness |

Standalone 通过真实 Module 收集 Document、Tool 和 Action 声明，不维护第二份功能列表。每次打开文档建立独立 Scope，有自己的关闭令牌、模型和界面。Tool 由 `AddTool` 注册 singleton，关闭隐藏，View 可以重建；不捕获 scoped 文档/播放器。页内设置与 Tool 使用同一根模型的不同控件实例。

S1 设置的读取、分组编辑、历史写入和清理分别通过窄端口映射到同一个用户数据存储。默认值只在新文档创建时取快照，记录屏障跨全部文档立即生效。运行库选择、文件预检、实际绑定和状态互相分离；Common 只共享文件，各插件的托管桥接、原生引擎和会话所有权独立。详细设计与证据见 [S1 专用记录](secret-video-player/reference/S1-IMPLEMENTATION-AND-VALIDATION.md)。

标签关闭先解绑视图并发出取消信号，再释放 Scope。窗口退出关闭全部文档并释放 Provider；初始化失败同样释放候选 Scope。内容区全屏仅通过 SDK 的 IWindowContentFullscreenHost 租约适配。

独立调试数据为 `%LOCALAPPDATA%/VideoSecurityPlayer/Standalone/user-data-v1.json`。正式插件继续使用 `%LOCALAPPDATA%/MyAvaloniaManagement/MySmallTools/secret-video-player/user-data-v1.json`，不迁移或重置正式数据。

Standalone 不模拟完整 Host，不提供 Dock 拖拽、安装升级和 Workflow Gateway。真实宿主联调项目及 Harness 不进入默认独立解决方案，生产插件与 standalone 均不引用 Host 内部实现。

R1 的 `scripts/Test-R1.ps1` 是测试侧聚合入口：Fast 不要求 Host 源码，Full 显式接入 Host 与 Workflow Studio，复用既有工具并检查执行证据。报告解析与覆盖率规则在 `R1-GateSupport.ps1`，由无额外包依赖的脚本测试保护；这些实现不进入产品 DI 或插件包。现有运行时句柄阻断及其处理状态见 [R1 实施与验证记录](secret-video-player/reference/R1-IMPLEMENTATION-AND-VALIDATION.md)。


R1.2–R1.4 将部署与诊断状态交给播放器自身创建的两个功能组件；协调器负责兼容转发和退订关闭。两类批次共用无业务数据的有效性账本；播放会话通过后端就绪契约协作，并复用纯轨道选择规则。以上都是插件内部职责调整，不改变 Host/Standalone 的作用域与每文档原生资源所有权。
