# S1：设置与维护、共用 LibVLC 可执行实施计划 v1.1

> 日期：2026-09-28。状态：用户已授权按本计划实施，实际进度见专用记录。
> 文档阶段已完成；后续用户确认：**开始实现，并按进度提交 Git，填写 title 和 desc**。
> 本文是后续执行入口，取代 [v1.0 交互提案](2026-09-28-settings-maintenance-and-shared-libvlc-v1.0.md) 中的实施与验证安排；四组功能和已确认的交互方向保持不变。
> 每批结果填写到 [S1 实施与验证记录](../reference/S1-IMPLEMENTATION-AND-VALIDATION.md)。文档中的新增类型、脚本和测试名都是待实现项，不表示文件已经存在。

## 1. 执行约束和完成定义

1. **SOLID 是首要约束**。先确定变化原因、状态所有权及生命周期，再编写功能；不以“界面能点”为完成标准。
2. 设计模式朴素使用：构造函数依赖注入、组合、不可变快照、普通事件、少量纯规则函数足够。不引入通用设置框架、反射表单、策略注册中心、Mediator、事件总线或第二套插件容器。
3. 新增和修改的 XML 注释、复杂逻辑注释使用中文，解释设计思路、使用边界、并发与取消行为。测试名称、公开标识符和稳定错误码可沿用工程命名习惯。
4. 每批包含行为测试、相关回归、文档同步及实际证据。测试必须验证用户可观察结果或关键边界，不能只对实现细节逐句照抄。
5. **不使用 AIFLOW，不使用 Windows CI，不运行发布门禁**。允许本机 Windows x64 的 Debug 和真实媒体开发验证；这与 Windows CI 不同。
6. 不自动部署到在用 Host，不修改 Common、真实用户设置或其他插件，不升级依赖/SDK/插件发布版本。按用户授权分批提交 Git，提交信息包含标题和正文；不自动推送。
7. 发现既有失败时先记录现状和原因，不删除用例、降低阈值或把未执行写成通过。缺少原生环境时明确保留待验证项，不能宣布 S1 全部完成。

本轮不包含文件处理默认值、密码持久化、全局连播、媒体队列改造、加密格式调整和通用运行库管理器。

## 2. 已核对基线与必须处理的现状

当前仓库：`D:\code\local\avalonia_dock_plug_test\myavalonia-video-security-player`。

编写本文时 HEAD：`4549c44`。工作区还包含用户已有 `.gitignore` 修改和本任务的设计文档；执行前重新检查，不能覆盖或混入用户的其他改动。

| 事实 | 实施含义 |
| --- | --- |
| `SecretVideoUserDataStore` 当前 schema version=1，路径名为 `user-data-v1.json` | 保留既有路径，内容迁移到 v2；不能另建一份历史事实源 |
| 当前后台写入失败被捕获后静默保留内存状态 | 补真实持久化反馈、修订号和重试，才能显示“已保存” |
| 当前写入快照在取得写入信号量之前捕获 | 串行提交必须防止旧快照晚落盘覆盖新修订；需确定性并发测试 |
| `PlaybackHistoryCoordinator` 已有清除抑制，但检查与最终 Upsert 分开 | 保留现有设计，在存储提交边界增加记录代次检查，防止清除/禁用后的迟到回写 |
| `PlaybackDeploymentViewModel.Check()` 成功后会初始化后端 | Tool 只调用无副作用探针，不能借用这条初始化路径 |
| `LibVlcRuntime` 当前只有成功初始化标记 | 增加加载尝试、成功/失败及冻结路径状态；失败后也不能任意热换库 |
| `PreviewRegistration.AddTool` 当前直接抛 `NotSupportedException` | Tool 注册和 Standalone 适配必须在同一批完成，保持预览与既有测试可运行 |
| `PreviewRuntime` 使用 `ValidateScopes/ValidateOnBuild` | 用于阻止 singleton Tool 捕获 scoped 播放服务；继续保留验证 |
| `Directory.Build.targets` 会从默认 Plugin 输出复制原生目录到其他项目 | 精简构建不仅修改 csproj；辅助复制也要服从开关且不能复用陈旧输出 |
| SDK 可注册 Tool，但没有插件打开指定 Tool 的公开导航端口 | 使用宿主 Tool 入口及复用视图的页内入口，不调用 Host 内部 Dock 服务 |

当前可执行本地入口为 `scripts/Test-UX1.ps1`、`scripts/Test-UX1Native.ps1`。历史 UX1 文档中的 324 项单元/8 项 UI 通过是当时的记录，后续执行需重新采集自己的基线，不能直接复制为 S1 结果。

### 2.1 相对 v1.0 的执行修订

- “两种包布局”在开发期只检查两种**本地构建输出**；正式 ZIP、压缩率、安装升级及正式 Host 发布验收留到发布阶段。
- 不引入自动保存含历史的旧 JSON 备份或自动回滚副本。沿用项目不额外保留明文历史、清空后不自动恢复的约定。迁移前后验证使用测试副本；真实用户数据的备份/降级由用户另行安排。
- 配置版本使用 JSON 内的 `version` 字段，不新造一个并行 `schemaVersion` 字段。未知更高版本不能被默认值自动覆盖。
- Standalone 的 Tool 支持、保存失败可见性、历史提交代次、辅助原生复制开关列为必做项。

## 3. 固定产品契约

### 3.1 四组配置和默认值

| 分组 | 项目 | 默认/约束 |
| --- | --- | --- |
| 播放偏好 | 音量：记住上次/固定初始值 | 默认记住；无记录时 50%；固定值 0–100 |
| 播放偏好 | 倍速：记住上次/固定初始值 | 默认记住；无记录时 1.0；固定值沿用六档 |
| 播放偏好 | 续播/从头 | 默认恢复有效历史；已完成、文件身份变化或失效历史从头 |
| 播放偏好 | 快进/后退 | 5/10/30 秒，默认 5；按键提示同步更新 |
| 视频库偏好 | 恢复上次目录 | 默认开启；关闭不影响正在浏览的目录 |
| 视频库偏好 | 默认递归 | 新用户关闭；当前库仍能单独调整 |
| 视频库偏好 | 默认排序 | 新用户文件名升序；沿用现有四个字段与两个方向 |
| 视频库偏好 | 恢复侧栏默认布局 | 新库期望宽度 400 px、打开侧栏、折叠库设置；适应窗口宽度 |
| 记录管理 | 记录观看进度 | 默认开启；关闭不删除旧记录，也不补写关闭期间进度 |
| 记录管理 | 历史数量/清空历史 | 沿用最多 1,000 条；清空前二次确认；不删除视频 |
| 记录管理 | 最近目录移除/清空 | 沿用最多 10 条；不关闭当前库，不重新加回已移除记录 |
| 播放组件 | 自动选择/指定目录 | 默认自动：Common → 有效内置；指定目录无效时不静默换源 |
| 播放组件 | 检测、保存、实际状态、环境诊断 | 检测不加载 DLL；保存不等于切换；诊断继续脱敏 |

现有用户的音量/倍速和库偏好按迁移映射保留，不全部重置成新用户默认值。当前媒体控制、当前批次密码/输出配置继续留在原页面。应用启动不自动播放，“仅加载”仍不自动起播。

### 3.2 生效时间

- 普通默认设置保存成功后，由**新建 Document**取得快照；已开 Document 的当前状态不被刷新覆盖。
- 记录开关是全插件即时策略，影响全部已有播放器。关闭后内存立即拒绝记录写入；持久化失败明确提示“本次已停止记录，重启后的偏好尚未保存”。
- 清空/移除在持久化成功后更新界面并确认成功；操作开始即使旧记录写入代次失效。失败保留原记录显示和重试入口，不伪报“已清空”。
- 运行库未尝试加载时，下次初始化采用新配置；已经成功或失败尝试过加载，都冻结本进程目录，变更仅在重启后生效。

### 3.3 界面和入口

- 一个 `视频工具 · 设置与维护` Tool：靠右、Hide；默认不强制展开，380–440 px 为期望宽度，支持 320 px。
- 顶部组件状态 + 四个折叠分组；首次展开播放偏好，故障定位到组件分组；关闭再开保留本次草稿和分组。
- 普通设置字段自动保存，显示保存中/已保存/失败重试；组件目录单独“检测目录 → 保存配置”；不增加全页面保存按钮。
- 宿主入口由 Tool 贡献提供。页内入口使用同一模型的新 View，放在原生视频表面之外的托管布局区；宽时并排、窄时上下排布，不把普通浮层盖在 HWND 上，不销毁或重新挂载播放器。
- Tool 与内嵌视图分别拥有控件实例，不把同一个 Control 挂入两个父级。进入设置不暂停播放，退出恢复入口焦点。
- Tool 只生成环境诊断。具体播放失败仍在对应播放器导出，避免单例持有文档或混淆诊断来源。

## 4. SOLID 的具体落地

| 原则 | 本功能的约束 | 评审/测试证据 |
| --- | --- | --- |
| S：单一职责 | 规则负责选值，存储负责事务和版本，探针负责检查，运行时负责加载，Tool 负责交互 | 不在 ViewModel 中读 JSON、调用 NativeLibrary 或扫描进程模块 |
| O：开闭原则 | 明确的 Auto/Custom 枚举与固定选择顺序即可；未来来源在解析器扩展 | 无动态策略注册表、反射发现或到处判断 Common 的重复分支 |
| L：里氏替换 | 自动/自定义、真实/测试存储遵守相同结果、取消、失败语义 | 同一组契约测试对适配器和可控替身成立；不新增依赖具体类型的强制转换 |
| I：接口隔离 | 播放器读取默认值、历史协调器提交记录、Tool 编辑配置，各取所需 | 不是让所有消费者依赖一个巨大的“设置服务” |
| D：依赖倒置 | 业务/规则依赖窄端口；Module 组合 JSON、文件探针和原生绑定 | 构造注入；纯规则和 Tool 行为可脱离文件系统、Avalonia HWND、LibVLC 测试 |

优先扩展现有接口；只有确有独立消费者或测试边界时才新增接口。不要为了每个类都配一个接口而增加文件。相同数据必须由一个存储实例拥有；多个窄接口可映射到它。

### 4.1 生命周期与所有权

| 对象 | 生命周期 | 可以持有 | 不可以持有 |
| --- | --- | --- | --- |
| 用户数据存储/组件设置与状态 | plugin singleton | 设置、最近值、历史、保存修订、运行库加载事实 | Document、播放器密码、原生表面 |
| `VideoSettingsTool` 与四个功能子模型 | plugin singleton，Tool 由 SDK 注册 | 窄端口、草稿、取消令牌、UI 调度与反馈 | scoped 播放会话、播放器实例、Host 内部服务 |
| 探针/解析器/纯规则 | 无状态或只读组合 | 路径来源、文件元数据端口 | UI 和当前播放状态 |
| 原有播放器/历史协调器 | Document scoped | 文档默认快照、媒体代次、已有播放资源 | 其他文档的播放器资源 |
| View | 可重建 | 控件和订阅租期 | 全局设置事实源、原生库句柄所有权 |

模块通过 `AddTool` 声明 Tool，不再在 `Services` 手工重复登记贡献根。View Detach 只退订 View 事件；Hide 不释放共享模型。插件退出才取消检查、收口存储写入并释放模型。

### 4.2 中文注释要求

公开类型、接口及非显然方法使用 `summary`；涉及状态归属、生命周期和原因使用 `remarks`。锁顺序、后台回调、取消、重启边界、版本兼容处补行内中文说明。不要逐行翻译赋值或用大段模板注释遮蔽行为。

示例仅表达注释要求，不预定最终 API 签名：

```csharp
/// <summary>根据默认策略和最近使用值，为新建播放器生成独立的初始设置。</summary>
/// <remarks>
/// 返回值在文档创建时固定，避免用户修改全局偏好时改变正在播放的视频。
/// 固定初始值与最近实际值分开保存，旧文档回写最近音量不能覆盖新默认策略。
/// </remarks>
```

```csharp
// 清除或禁用记录会提升存储代次。必须在实际提交的同一同步边界内复核代次，
// 仅在回调入口检查无法阻止已经排队的旧进度在清除之后重新写回。
```

## 5. 数据、并发和运行库设计

### 5.1 唯一用户数据文件

继续使用现有用户数据路径。v2 增加播放默认策略、视频库默认策略、记录开关和运行库选择；既有最近音量/倍速、最近目录与历史保留。文件名的 v1 是既有路径，不作为内容版本判定依据。

迁移函数须为可单测的纯转换：v1 最近值 → v2 最近值；记忆策略开启；既有库值 → 初始库默认值；记录开启；组件自动选择。读取时不立刻破坏性重写；第一次成功写入才提交完整 v2。

缺失字段、无效枚举、NaN/Infinity、非法速率、路径、过量条目和版本分别校验。未知更高版本进入不可覆盖状态并提示升级应用，不能读成空默认后在 Dispose 自动覆盖。损坏文件沿用项目约定报告恢复状态，不创建含历史的自动恢复备份。

### 5.2 默认值与最近使用值

- 默认策略与最近值分别更新，避免旧文档用完整旧对象覆盖新字段。
- 新文档取得快照时，记忆策略选择最近有效值，固定策略选择固定值。
- 最近库目录、显示宽度等当前状态与明确配置的默认递归/默认排序分开；操作当前库不反向覆盖全局默认值。
- 同时打开的多个文档按现有“最近一次有效变更”记忆最近值，不引入跨文档同步控制。

### 5.3 保存反馈

沿用一个串行写入器。内存修订、磁盘已提交修订、保存中/失败状态分开；短时间字段修改可合并，但成功提示必须对应最新需要保存的修订已提交。

在串行提交边界取得或核验最新快照，原子替换前检查取消/修订，禁止旧写入在新写入之后覆盖磁盘。失败保持待保存草稿；重试使用最新修订。保存完成回调与 ViewModel 更新通过既有 UI 调度方式执行。

组件目录的保存请求只有原子写入成功才更新“已保存选择”；失败继续展示旧选择和新草稿。目录检查带草稿修订，旧检查完成不得覆盖新路径结果。新文档使用已成功提交的默认配置；尚未保存的普通设置只在编辑草稿中展示。记录禁用的内存即时拒绝是明确例外。

### 5.4 历史提交屏障

复用 `PlaybackHistoryCoordinator` 的媒体代次和现有清理事件，在存储端增加简单递增的记录修订。写入请求携带观察到的记录修订，存储在同一同步边界内检查“是否允许记录”和“修订是否仍有效”后再更新。

关闭记录、清空全部/移除目标都会使相关旧写入失效；必要时可用全局修订使并发写入保守重取快照，不引入事务框架。成功清空后，当前媒体代次保持抑制，直至用户明确重新加载。重新开启记录时，从之后的进度事件重新取得有效记录令牌，不补写禁用期间缓存。

必须覆盖“记录从内存取出 → 用户清空 → 旧请求 Upsert”的交错，以及关页 Flush、磁盘等待队列、另一文档定时器。清理操作持久化失败不宣称成功，也不能解除旧请求抑制而让它们重新写入。

### 5.5 运行库状态与解析

自动候选：`AppContext.BaseDirectory/Common/libvlc` → 插件程序集目录 `native/win-x64/libvlc`。不依赖工作目录、不搜索 PATH、不硬编码开发机器路径。Standalone 按自身可执行目录解析 Common。

自动模式仅在 Common 文件预检不通过时回退内置，并显示原因；真实绑定已经开始后，不在同一进程继续尝试另一套库。指定目录保存前重新预检，失败保留原配置。两处都不可用时只阻止播放，设置、诊断和加解密入口仍可用。

探针分开检查托管桥接和原生候选：Windows x64、目录/文件可读、PE 架构、libvlc/libvlccore 版本、MP4/WebM 解复用、FFmpeg 解码、D3D11 与音频输出。只凭目录存在、音频模块齐全或文件名版本不能宣布视频可用。以当前锁定 3.0.23 为首批支持基线；无法静态确认的依赖留到真实初始化和媒体专项验证。

运行时用一个明确状态表达 `NotAttempted / Loading / Ready / Failed`，存储选择和当前实际加载状态分开。仅文件预检失败不算原生加载尝试，可修复后重查；进入真实绑定调用前置为 Loading 并冻结路径，任何绑定后异常都进入 Failed，修改路径需重启。

锁内只允许一个初始化流程。实际绑定应借鉴网易云的程序集级原生解析与真实来源核验，但不能引用音乐插件对象或设置。先核对两处 LibVLCSharp 私有加载上下文及解析器注册限制，再采用最小必要适配；不盲目重复注册 `DllImportResolver`，不修改全局 PATH/搜索目录，不卸载其他插件拥有的句柄。

Tool 看同一个运行库状态快照，不执行真实初始化。状态模型记录真实路径和版本；导出环境摘要脱敏。`UsesBundledRuntime` 等现有平台字段应核对消费者后修正其含义，不能在共用模式继续把实际来源恒写成内置。

### 5.6 构建选择与体积

新增 MSBuild 属性 `IncludeLibVlcRuntime`，未指定时为 `true`；`false` 只省去插件输出的原生运行库及对应资产声明，保留托管桥接和 NuGet 锁定依赖。运行时设置与构建属性互相独立：完整输出也可以优先使用 Common；仅在界面改成 Common 不会缩小已经交付的包。实际包体积收益来自后续使用精简构建产物。

属性应同时约束 VideoLAN 原生复制和 `CopyVideoRuntimeForTools`。精简构建不能复制 Common 到插件目录来伪装成功；同一输出目录由 true 切为 false 时也不能残留旧 native。开发脚本在隔离目录验证 true、false 及 true→false 切换；清理仅限明确属于该插件构建输出的原生目录。正式 ZIP 的压缩体积在发布阶段再测量。

## 6. 改动文件清单

下表中 `Business/`、`ViewModels/`、`Views/`、`Features/`、`Plugin/`、`Constants/` 及插件 csproj 相对 `src/VideoSecurityPlayer.Plugin/`；其余路径相对仓库根。新增名称可按代码组织微调，职责和测试覆盖不能减少。

| 范围 | 主要文件 | 改动 |
| --- | --- | --- |
| 存储 | `Business/SecretVideoPlayer/Library/SecretVideoUserData.cs` | v2、唯一写入器、修订与失败反馈；必要时把序列化/迁移纯规则拆到同目录 |
| 纯规则/端口 | 新增 `Business/SecretVideoPlayer/Settings/` 下配置 record、默认值规则、必要窄端口 | 不引用 Avalonia 或原生库；不复制现有历史存储接口 |
| 历史 | `Business/SecretVideoPlayer/Library/PlaybackHistoryCoordinator.cs` | 记录开关、提交代次、清除抑制；退订和关闭 Flush |
| 播放接入 | `ViewModels/SecretVideoPlayer/Playback/PlaybackCoordinatorViewModel.cs` 与同目录 `PlaybackResumePolicy.cs`；`ViewModels/SecretVideoPlayer/SingleVideo/SingleVideoFeatureViewModels.cs` | 默认快照、步长、记忆/固定、两种播放器统一续播规则 |
| 视频库接入 | `ViewModels/SecretVideoPlayer/Library/LibraryDocumentCoordinatorViewModel.cs`、同目录 `LibraryBrowserCoordinatorViewModel.cs` 及现有布局切片 | 默认策略与当前状态分离，最近目录/历史变更同步 |
| 运行库 | `Business/SecretVideoPlayer/Playback/` 下 `PlaybackPlatform.cs`、`PlaybackDeployment.cs`、`LibVlcRuntime.cs`；新增解析器/状态 record | 多来源只读检查、冻结加载尝试、实际来源核验 |
| 设置界面 | 新增 `Features/Settings/VideoSettingsTool.cs`、四个功能子模型、`VideoSettingsView.axaml(.cs)` | 四组组合；复用样式；不直接读写文件/创建播放器 |
| 页面入口 | `Views/SecretVideoPlayer/` 下两个播放器视图、`Playback/PlaybackDeploymentView.axaml`、`Library/LibrarySettingsView.axaml` | 复用设置内容，保留当前库操作及单项历史清理 |
| 组合入口 | `Plugin/VideoSecurityPlayerPluginModule.cs`、`Constants/VideoSecurityPlayerContributionIds.cs` | 新稳定 ToolId，现有 Document/Action ID 不变；正确 singleton/scoped 映射 |
| 独立预览 | `src/VideoSecurityPlayer.Standalone/PreviewRegistration.cs`、`PreviewRuntime.cs`、`MainWindow.axaml(.cs)` | 接受真实 Tool 声明、模型单例、视图重建/隐藏；不复制生产注册清单 |
| 构建 | `VideoSecurityPlayer.Plugin.csproj`、根 `Directory.Build.targets` | `IncludeLibVlcRuntime` 默认为 true；资产声明和所有辅助复制均服从开关 |
| 开发验证 | 新增 S1 测试、`scripts/Test-S1.ps1`、`scripts/tests/Test-S1Gate.ps1`、`tests/s1-coverage-policy.json` | 沿用 UX1 本地口径，补检查脚本自测和两种输出布局 |
| 原生开发验证 | 新增 `scripts/Test-S1Native.ps1` 及 Standalone 显式 smoke 场景 | 隔离数据和运行库来源，真实播放/失败重启/共存验证，非发布入口 |

## 7. 按批实施与退出条件

### S1.0：准备和基线

- 阅读本文、专用记录及相关现有代码，确认工作区边界。
- 运行现有 UX1 本地开发检查，保留本次 TRX/覆盖率；需要原生基线时单独运行现有 UX1Native。
- 记录现有 SDK 限制、已知原生基线问题；不得把发布验收的历史阻断直接视为本轮通过或失败。
- 退出：基线证据存在；新增改动与已有问题可区分；开发/发布范围明确。

### S1.1：模型、迁移、持久化与记录边界

- 先实现纯规则和 schema v1→v2 转换，再扩展唯一存储实例；先写关键失败/并发场景测试。
- 完成保存修订、失败重试、未知版本不可覆盖及记录屏障；保持旧端口兼容行为，逐步切换消费者。
- 退出：所有旧数据保留测试、保存失败/迟到提交/清除后不复活测试通过；存储不依赖 UI。

### S1.2：播放与视频库策略接入

- 两种播放器使用同一套默认值/续播规则；快捷键命令和帮助文字共享一个步长值。
- 库默认值与当前库状态分离；新文档采用新快照，旧文档不被重设；记录禁用即时作用于已有文档。
- 退出：双播放器、双视频库隔离测试通过；原 UX1/G6/G7 行为回归通过。

### S1.3：运行库来源与精简构建

- 实现无副作用探针/解析器，再接真实运行时状态，最后接构建开关。
- 检查当前所有原生文件复制路径；完整/精简布局分别使用新输出根，不能以旧 bin 的存在证明完整。
- 保留两个托管桥接程序集；省去的仅是原生运行库文件。NuGet 锁定依赖保留，外部路径不写 manifest。
- 退出：来源选择矩阵、失败冻结、加载来源核验替身测试通过；本地完整/精简输出检查通过；不执行正式 ZIP。

### S1.4：Tool、页面入口和 Standalone

- 先让 PreviewRegistration/PreviewRuntime 支持 Tool，然后在真实 Module 注册，二者同批保持可构建。
- Tool 组合四组模型；普通字段自动保存、目录草稿显式保存；失败与待重启文案统一。
- 宿主贡献入口和页内设置视图共用模型；处理 Hide、View 重建、取消和 UI 调度。
- 退出：构造验证无 singleton 捕获 scoped；单独开 Tool 零原生加载；Headless 验证显示、交互、分组恢复、主题和窄宽布局。

### S1.5：本地开发检查、原生专项及文档收口

- 完成下一节专用开发脚本及脚本自测；先定向，再全量 Tests/UiTests。
- 本机交互式桌面验证 Common/内置/指定目录及真实视频；运行音乐与视频两种加载顺序及同时播放场景。
- Dock 拖拽、浮动与真实多窗若不能在隔离开发环境实测，保留明确待验证项，不用 Headless 替代，也不借道发布脚本。
- 同步使用说明、故障排查、项目职责、开发构建说明及专用记录。
- 退出：本轮必需开发检查全部通过，原生/人工证据按真实状态记录；发布门禁仍标记“范围外，未执行”。

## 8. 单元测试与本地开发检查契约

### 8.1 业务测试矩阵

| 建议测试类 | 必测行为 |
| --- | --- |
| `S1SettingsMigrationTests` | v1 完整保留、缺字段、非法值、超限、损坏 JSON、未知高版本不可写；迁移可重复且不产生第二份历史 |
| `S1SettingsPersistenceTests` | 原子写失败、重试最新修订、取消、慢旧写/快新写、Dispose 收口；成功提示对应磁盘结果 |
| `S1PlaybackDefaultsTests` | 记忆/固定六档速率与音量边界、5/10/30 秒、续播/完成/失效历史、旧文档不回写覆盖新默认 |
| `S1LibraryDefaultsTests` | 恢复目录开关、默认递归排序、布局重置、两个已开库不受影响、移除目录后不被普通状态更新加回 |
| `S1RecordingPolicyTests` | 禁用即时拒写、再次启用不补写、Clear/Remove 与 Upsert 交错、关页 Flush、两个文档、清理失败与重试 |
| `S1RuntimeResolutionTests` | Common/内置/指定的全组合，架构/缺模块/版本不可判定，托管桥接缺失；检查从不触发加载 |
| `S1RuntimeInitializationTests` | 并发只加载一次、预检失败可重试、真实加载失败被冻结、来源不一致、重启前当前/候选分离 |
| `S1SettingsToolTests` | 旧探针晚返回、保存途中改草稿、自动保存失败、关闭/重开、初始化失败仍能打开设置、无 scoped 对象泄漏 |
| `S1EnvironmentDiagnosticsTests` | 无媒体可导出、稳定问题码、实际/候选区分、路径与敏感字段脱敏、取消导出 |
| 扩展 `StandaloneRuntimeTests` | AddTool 消费真实声明、重复 ID、模型复用/View 重建、Dispose 归属及作用域验证 |

用可控任务/同步屏障构造竞态，不依赖碰巧命中的 `Sleep`。文件测试使用本次临时根；DLL 测试先用构造的元数据样本与加载委托，不在单元进程加载不兼容原生库。真实 DLL 与 HWND 只放专项进程。

### 8.2 UI 与原生验证分工

Headless：四组显示与条件字段、保存状态、确认/取消、缺库提示、错误与重试、单模型多 View、320/400/736 px、双主题、键盘焦点及快捷键输入保护。必须用真实生产 View/绑定；截图不能替代状态断言。

本地原生：加密样本实际 MP4/WebM 播放、Seek、暂停/恢复、音轨/字幕、设置展开不换表面、冻结路径重启后生效、音乐先/视频先/同时播放、关闭释放。每个需要不同 DLL 来源的场景使用新进程，测试数据、用户配置和运行库副本均隔离。

旧 `Test-UX1Native.ps1` 只证明原有场景；新增共用库及共存必须有 S1 自己的报告。不能仅调用旧 smoke 后将 S1 所有场景标记通过。

### 8.3 待新增 `scripts/Test-S1.ps1`

公开入口建议仅 `-OutputRoot <全新目录>`，Debug 固定；按顺序执行：

1. `Test-S1Gate.ps1` 的纯报告/策略判定自测，不调用 R1 脚本或任何正式构建入口。
2. 对 `VideoSecurityPlayer.Tests` 和 `VideoSecurityPlayer.UiTests` 分别 locked restore、Debug build（warn as error、SkipPluginDeploy=true）、test，生成各自 TRX/覆盖率。
3. 校验测试总数非零、passed=total、无失败/跳过/中止；退出码、TRX 内容和缺失报告均参与判断。
4. 用独立输出根构建 Plugin 的完整/精简布局，检查相应文件清单、托管桥接、原生文件有无和路径，**不调用 BuildManagedPluginPackage 或 DeployManagedPlugin**。
5. 校验 `tests/s1-coverage-policy.json`，记录完整报告指标和新增组件指标。
6. 写本次 `summary.json`，包含配置、版本/源码标识、各步骤状态、测试数量、覆盖率、输出布局结果和证据相对路径。

结果目录不存在才允许启动；顺序运行共享工程构建，避免两种属性组合并发写同一 obj。完整/精简输出及测试输出不可串用。给外部进程设置有界超时，超时只终止本次创建的进程；异常也要落盘 failed/后续 notExecuted 状态。

脚本自测必须拒绝：空/损坏/缺失 TRX、零测试、跳过或失败、进程非零但日志称通过、旧/混杂结果、缺失或重复覆盖率、NaN/Infinity/越界比例、缺失组件、精简输出残留 native、完整输出缺模块、结果目录被复用。清理仅限本脚本创建且核对过的绝对路径，使用同一 PowerShell 原生路径操作。

### 8.4 覆盖率口径

- 沿用 UX1 本地业务测试口径，新增纯规则/状态核心要求行 ≥90%、分支 ≥80%；保留 UX1 已有组件阈值。
- S1 清单应至少列入迁移、默认值规则、记录代次判定、运行库选择和加载状态规则的实际生产类型。持久化适配器/Tool/UI 以关键行为测试补充，不因难测而把核心分支排除统计。
- 每套测试只接受一个有效覆盖率报告；缺失、重复、组件重名或非法数字均失败。无可执行分支的纯 DTO 不伪造 100% 分支覆盖。
- 全插件指标如实记录，并和同一测试集合的 S1.0 基线比较；不能降低已有政策。R1 的 74.39%/51.99% 来自另一组合口径，本轮不把它硬套到 UX1/S1，也不据此声称全插件达标。

### 8.5 待新增 `scripts/Test-S1Native.ps1`

建议入口：`-OutputRoot`、`-Scenario Playback|Coexistence`，共存场景额外要求明确的 `-MusicPluginDirectory`。仅启动本轮构建的隔离开发窗口/进程；需要用户桌面交互时明确说明，后台帮助进程用 Hidden。

Playback 必须逐项报告库来源、实际版本、真实媒体结果和释放；Coexistence 必须实际运行两种加载顺序及同时播放。来源目录先只读验证，再复制完整运行库到本次临时根，不能清理或修补 Common 原目录。没有桌面、音频输出/样本/音乐插件时记录 blocked/notExecuted，不能返回“全部通过”。

共存必须在**同一进程**内加载两个插件各自的托管桥接并真实播放；启动两个独立播放器进程不能证明同名 DLL 与程序集上下文兼容。现有 `tools/VideoSecurityPlayer.Playback.IntegrationHarness/Program.cs` 直接装配单个视频模块，不能把它原有场景当成共存证据。S1.3 先核对可复用的隔离开发装载入口，S1.5 再增加专用场景或在隔离 Debug Host 中人工复核，记录实际装载方式。不得为此运行 R1/Host 发布脚本，也不得在产品插件中加入对音乐插件或 Host 内部实现的引用。

具体进程启动参数及 smoke 实现随 S1.3/S1.5 落地；不能让脚本引用不存在的参数后以零退出冒充完成。当前脚本尚不存在。

## 9. 执行命令与明确排除项

所有命令在插件仓库根目录运行。以下为实施入口，实际执行结果以专用记录为准。

### 9.1 当前已经存在、可用于开始执行的命令

```powershell
Set-Location 'D:\code\local\avalonia_dock_plug_test\myavalonia-video-security-player'
git status --short
git log -1 --oneline

# 先采集独立结果目录中的本地基线；该脚本只跑 Debug Tests/UiTests。
pwsh -NoProfile -File scripts/Test-UX1.ps1

# 单独执行既有真实媒体基线。需要本地 Windows x64 桌面，不是 CI。
pwsh -NoProfile -File scripts/Test-UX1Native.ps1
```

脚本和内部调用已在编写本文时静态核对；环境能否通过要以执行时的结果为准。

### 9.2 实现 S1 脚本之后才执行

```powershell
if (-not (Test-Path -LiteralPath 'scripts/Test-S1.ps1')) {
    throw '先完成 S1 专用开发检查脚本，不能将不存在的入口当作已执行。'
}
pwsh -NoProfile -File scripts/Test-S1.ps1

if (-not (Test-Path -LiteralPath 'scripts/Test-S1Native.ps1')) {
    throw '先完成 S1 原生专项脚本及对应 smoke 场景。'
}
pwsh -NoProfile -File scripts/Test-S1Native.ps1 -Scenario Playback
# Coexistence 所需 MusicPluginDirectory 使用本轮明确核验的隔离开发产物；
# 在专用记录填入实际命令，不提供可能误指向在用插件的默认路径。
```

分批开发可使用 Tests/UiTests 的定向 `dotnet test --filter`，完成一批后运行全量 S1 开发检查。只有明确的新失败、新代码或未解决风险才扩大/重复验证。

### 9.3 本轮不调用

- AIFLOW 初始化或流程；`.github/workflows`、Windows CI。
- `scripts/Test-R1.ps1`（Fast/Full）、`scripts/Test-HostIntegration.ps1`、`scripts/Test-Coverage.ps1`、`scripts/Verify-Migration.ps1` 及间接触发这些入口的聚合脚本。
- `scripts/tests/Test-R1Gate.ps1`：其自测会启动 R1 Full 的负例，因此 S1 自测独立实现，不调用它。
- `VideoSecurityPlayer.ReleaseAcceptance`、正式性能/发布总验收、签名、上传、安装、更新和发布。
- `BuildManagedPluginPackage`、`DeployManagedPlugin`；不得为查看文件布局顺手部署。

现有测试工程可能引用辅助工具程序集；编译程序集不等于运行对应发布工具，但新增 S1 测试不得启动发布工具进程。开发期验证两种输出布局，正式包与安装链路待发布再验收。

## 10. 文档同步、证据与交接

每批更新 [S1 专用记录](../reference/S1-IMPLEMENTATION-AND-VALIDATION.md)：实际修改、SOLID 决策、测试命令、通过/失败/未执行、证据目录及残留问题。数量、版本、来源路径和覆盖率只填实测值，不沿用旧批次数字。

收口时同步：

- `docs/secret-video-player/README.md`：已实现的设置入口、默认值与生效时机。
- `docs/project-and-window-responsibilities.md`：Tool/Document 生命周期及 Standalone 能力。
- `docs/deployment-and-release.md`：单独的 S1 本地开发与两种输出说明；正式发布流程保持独立。
- `docs/secret-video-player/troubleshooting/integration-and-conventions.md`：Common、指定目录、版本/架构失败及重启语义。
- `docs/README.md` 与设计索引：当前实施入口和真实状态。

原始日志、TRX、覆盖率、截图和临时原生目录保留在本次 `TestResults/S1` 或独立 `artifacts/S1` 下，不提交大型产物、用户路径列表或含敏感内容的报告。版本化文档只保留必要摘要和证据索引。

### 后续执行用请求

> 按《S1：设置与维护、共用 LibVLC 可执行实施计划 v1.1》从 S1.0 开始分批实现，每批完成测试和专用记录后继续。以 SOLID 为首要约束，朴素使用设计模式，中文注释解释设计思路。只运行本地 Debug 开发检查和必要的本地原生专项，不使用 AIFLOW、Windows CI 或发布门禁，按进度提交带标题和正文的 Git 提交，不自动部署或推送，不覆盖已有无关修改。不得把未执行项记为通过。

执行者无需重新设计已确认的四组界面；遇到实现约束则修订本文并说明依据，不暗中扩大范围。分批实施状态及证据以专用记录为准。
