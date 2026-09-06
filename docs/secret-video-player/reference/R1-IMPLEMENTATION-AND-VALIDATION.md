# R1 实施设计与验证记录

> 日期：2026-09-06。R1.2、R1.3 已完成；R1.4 选定代码实现完成，资源与性能验收仍未通过。全过程保留原门槛，不将功能通过当作发布通过。
> 实施依据：[R1 实施计划](../plan-history/R1-SOLID-REFACTOR-AND-QUALITY-GATES.md)。全过程不使用 AIFLOW。

## 1. 初始代码与环境

| 项目 | 基线 |
| --- | --- |
| 插件 revision | `b09870cc9e62cd0921bd879cb5d4a08f301feb92` |
| Host revision | `9dac3d26dde63d6554b70f61fd7ab02aa001d318` |
| Workflow Studio revision | `6cabc57ef40e66b72de9f4699193d84647605e89` |
| SDK / 操作系统 | .NET SDK 10.0.302 / Windows 10.0.26200，x64 交互会话 |
| 工作区 | 插件只有本轮之前编制的计划与索引改动；Host 干净；Workflow Studio 的 Directory.Build.props 有已有修改，保持原样 |
| 本地证据目录 | `TestResults/R1/baseline-20260906`，被仓库忽略，不进入插件包 |

以上 revision 标记初始快照；最终验收还需记录工作区文件摘要，不能仅以 HEAD 代表未提交改动。

## 2. 职责与状态所有权清单

| 现有所有者 | 状态 / 依赖 | 迁移方向 | 线程、通知与关闭约束 |
| --- | --- | --- | --- |
| PlaybackCoordinatorViewModel | 部署可用性、问题、路径、建议；平台探针与 backend 初始化 | 独立部署功能组件 | 在页面绑定前完成受部署检查约束的初始化；通过属性通知投影；关闭后不重新初始化 |
| PlaybackCoordinatorViewModel | 诊断导出器、防重入标志、忙碌与保存提示 | 独立诊断功能组件 | UI 状态唯一所有者；导出可取消；关闭后的完成回调不得通知 UI；保存选择器仍归 View |
| PlaybackCoordinatorViewModel | 播放快照、播放/暂停命令、用户偏好、全屏意图 | 本阶段保留协调 | 捕获的 UI 同步上下文；关闭时退订会话和释放定时器；不释放注入会话 |
| 两个 BatchViewModel | 队列修订、预检修订、运行代次 | 共同有效性机制，按语义核对后确定具体范围 | UI 发起修改，后台仅读取运行有效性；新批次或关闭使旧回调失效 |
| 两个 BatchViewModel | 集合、密码、单项状态、领域预检计划 | 各自继续拥有 | ObservableCollection 仅在所属上下文更新；密码不进入公共组件、计划与诊断 |
| SequentialVideoQueueRunner | 严格顺序、取消当前与全部、RunId / ItemId | 保留 | 与 UI 项目可用性判断协作，不新增运行器 |
| SecureVideoPlayer | 当前 Source、操作门、用户意图、媒体/表面代次和取消边界 | 保留会话唯一所有权 | 换片和恢复共享串行次序，不分散锁或媒体提交事务 |
| SecureVideoPlayer | 原生输出就绪通知与初始化的具体类型判断 | 收窄为原生 Host 契约 | 测试 Host 与惰性 Host 的语义一致；创建通知可退订；不改变原生对象构造时序 |
| SecureVideoPlayer | 独立的控制选择/恢复规则 | 优先评估纯决策提取 | 纯函数不持有 Source、锁、句柄或密码；执行仍由会话负责 |

## 3. 测试映射与现有约束

| 变化点 | 现有保护 | 新增保护方向 |
| --- | --- | --- |
| 部署与初始化 | G4DeploymentTests、G9PlatformAbstractionTests、真实播放首次表面绑定 | 未支持平台、聚合问题、重检、初始化失败和关闭 |
| 诊断功能 | G10DiagnosticsTests 的脱敏与导出调用 | 重入拒绝、失败后可重试、取消、关闭后无通知和保存提示 |
| 队列有效性 | G5BatchQueueTests、VideoDecryptionTests、G2ReliabilityTests | 初始无计划、编辑失效、旧运行回调失效、取消与关闭 |
| 播放依赖 | G3PlaybackSessionTests 的换片/回滚/旧事件、G9PlatformAbstractionTests | 非 Lazy 实现也遵守初始化与输出就绪契约 |
| 播放决策 | G6PlaybackControlTests、G3PlaybackSessionTests 的表面恢复 | 纯规则输入边界与实际会话行为保持一致 |
| 门禁 | 现有业务/UI/Host/Host UI 四套及真实工具 | 缺少配置、命令失败、空报告、跳过、覆盖率不足的非零退出 |

并发测试使用受控 TaskCompletionSource、显式事件与取消令牌；不依赖增加固定休眠改变通过概率。原生行为通过真实窗口工具补充验证。

## 4. 性能与资源判定

沿用现有 ReleaseAcceptance 的内存硬门禁和 Phase4 的预热后资源门禁。Phase4 组合 G3 与 G8，覆盖真实播放、全屏、Dock、队列和媒体库规模场景，报告中记录实际参数。

G10 性能比较沿用既有 G10BaselineComparer：吞吐量中位数至少为基线的 75%；耗时中位数上限为 max(基线 × 1.30，基线 + 2)，P95 上限为 max(基线 × 1.50，基线 + 5)。比较前必须满足相同环境指纹、场景参数与硬门禁，聚合至少三轮报告。阈值在修改产品代码前固定，最终结果不可比较时不标为通过。

## 5. 验证进度

| 项目 | 结果 | 证据 |
| --- | --- | --- |
| 锁定还原 | 通过 | 基线执行日志 |
| Release 构建 | 0 警告、0 错误 | `baseline-20260906/build.log` |
| 业务测试 | 205 / 205 通过 | `baseline-20260906/tests` |
| UI 测试 | 5 / 5 通过 | `baseline-20260906/tests` |
| 四套合并覆盖率 | 行 74.39%、分支 51.99% | `baseline-20260906/coverage/merged/Cobertura.xml` |
| Host / Host UI 基线 | 原计数 7 / 3；其中一个工作流用例原本无配置直接返回，其实际场景由后续联调执行 | `baseline-20260906/coverage` |
| 真实双插件工作流 | 1 / 1 实际通过，Host 包测试与 UI 也通过 | `TestResults/Migration/integration-b958fdeaea284647b712995da120b11b` |
| Phase4 | 未通过：句柄 1583 → 1596，净增 13，超过 +10 | `baseline-20260906/phase4/report.json` |
| G3 / G8 功能矩阵 | 均通过；221 次表面创建/销毁，最终播放资源全部为 0 | Phase4 子报告 |
| Standalone 真实播放 | 通过 | `baseline-20260906/standalone.json` |
| 64 / 512 MiB 内存门禁 | 通过，文件解锁与内容哈希一致 | `baseline-20260906/memory.json` |
| 三轮 G10 性能基线 | 硬门禁通过，已聚合 | [可版本化的脱敏基线](../benchmarks/r1-windows-x64-baseline.json)，原始报告在 `baseline-20260906` |
| R1 Fast | 通过，205 个业务测试、5 个 UI 测试 | `run-f23033175dd5437987b6a082295dc06b/summary.json` |
| 门禁自身回归 | 30 项通过，包括 Full 缺配置、跳过、失败退出码、DOCTYPE 与无效报告 | `scripts/tests/Test-R1Gate.ps1` |
| Full 的测试与集成阶段 | 业务 205、UI 5、Host 6、Host UI 3 通过；覆盖率阶段明确跳过 1 个真实工作流用例，联调阶段该用例实际 1 / 1 通过；覆盖率维持 74.39% / 51.99% | `run-8a74c8e583d34b7bb588e7f7dfd2d861` |
| Full 的 Phase4 | 失败：句柄 1567 → 1616（+49，限制 +10）；私有内存净增 120.17 MiB（限制 +64 MiB）。功能矩阵通过，表面 221 / 221，播放资源归零 | 同一 Full 目录的 `phase4/report.json` |
| Full 后续步骤 | Standalone、memory、performance 明确记为 notExecuted；不沿用之前基线报告冒充本轮通过 | 同一 Full 目录的 `summary.json` |

## 6. 实施与决策记录

- 2026-09-06：开始 R1.0；修正当前架构文档中同步 Stop 与直接 Dispatcher 投递的过时描述，保持代码行为不变。
- 2026-09-06：基线 Phase4 的 Semaphore 净增 15（G3 +8、G8 +7），与历史 `TestResults/Phase4/NO-GO.md` 的类型分布一致。保持原有 +10 句柄、+64 MiB 私有内存规则，不改阈值。
- 2026-09-06：隔离实验绕过 Avalonia、Host、播放器会话和媒体，只调用原生 `libvlc_new(0, null)` / `libvlc_release`，同一线程连续 12 轮，每轮 Semaphore 增加 7。LibVLCSharp 构造/释放实验同样复现，等待 20 秒与 GC 后未消失。原生调用实验表明问题可在产品业务之外复现，尚不能仅凭类型计数确定具体原生分配堆栈。
- 2026-09-06：待明确的修复方向为插件运行时持有一个 LibVLC 引擎，MediaPlayer、媒体与状态仍按文档隔离。上游 [LibVLCSharp 3.10.0 的 LibVLC 类型说明](https://github.com/videolan/libvlcsharp/blob/3.10.0/src/LibVLCSharp/Shared/LibVLC.cs) 说明单个 LibVLC 可创建多个 MediaPlayer；这只证明 API 支持，是否满足本项目的关闭、惰性加载和文档隔离仍须专门验证。该方向改变原生命周期约定，尚未实施。
- 2026-09-06：Full 首次运行发现 VSTest 会复制覆盖率文件到 TRX 附件目录。采集检查改为允许内容哈希完全相同的副本，拒绝混入不同报告；不是减少测试范围或放宽覆盖率。
- 2026-09-06：用实际 TRX/Cobertura 验证解析器并补回归：xUnit 的 skipped 明细不一定累计到 notExecuted，按明细与 total/executed/passed 一致性校验；Cobertura 的同名 DOCTYPE 通过 DOM 根元素读取，避免 PowerShell 属性适配歧义。
- 2026-09-06：Full 最终执行确认门禁能阻断真实资源失败。第二轮还出现 +120.17 MiB 的播放进程私有内存增长；与 64/512 MiB 加解密内存门禁是不同场景，不相互替代，也不假定共享引擎一定能解决该增长。

## 7. 可重复运行的原生诊断

诊断入口位于 ReleaseAcceptance，复用 Harness 的只读句柄分类器，不进入产品 DI。它直接调用已加载运行时的 C API，报告只表达“采集完成”，不冒充资源门禁通过。

```powershell
dotnet run --project tools/VideoSecurityPlayer.ReleaseAcceptance -c Release -- --native-lifetime isolated --rounds 12 --report TestResults/R1/native-isolated.json
dotnet run --project tools/VideoSecurityPlayer.ReleaseAcceptance -c Release -- --native-lifetime overlap --rounds 12 --report TestResults/R1/native-overlap.json
```

本次实际结果：

| 模式 | 每轮行为 | 12 轮 Semaphore 变化 |
| --- | --- | --- |
| isolated | 每轮独立创建并释放，实例总数回到 0 | 4 → 88，每轮 +7 |
| overlap | 保持一个守护实例，其余实例创建后立即释放 | 守护实例存活期间保持 39，每轮 +0；最终释放守护实例后为 11 |

实验支持“原生实例总数回到零后重新初始化会造成增长”这一定位方向。保留一个运行时实例可以避免本实验中的重复增长，但仍需验证生产中的文档隔离、惰性初始化、插件退出释放及真实窗口，不能把诊断结果直接当作共享引擎方案验收。

## 8. 待明确的生命周期调整方案

建议插件级运行时成为 LibVLC 引擎的唯一所有者，按需创建一次并在插件 Provider 关闭时释放；每个 Document 仍独立持有 MediaPlayer、原生调度器、媒体 Source、文件、密码与播放状态。业务会话依旧只访问窄播放端口，原生引擎只暴露给原生工厂，不进入 ViewModel。

该方案需要新增以下验证后才能合并到 R1 执行范围：未支持平台和坏部署不创建引擎；并发首次获取只创建一次；创建失败可重试；多个文档的播放器与状态隔离；关闭一个文档不影响其他文档；最后一个文档关闭时释放全部媒体而保留插件级引擎；插件关闭按文档、播放器、引擎顺序释放。Phase4 原阈值保持不变。

当前状态：共享引擎方案尚未实施。用户随后明确授权执行 R1.2–R1.4，本轮在保留每文档 LibVLC 生命周期的条件下完成有限职责重构；资源门禁不因该授权而豁免。

## 9. R1.2–R1.4 实现设计（2026-09-06）

### R1.2：部署与诊断拥有实际职责

`PlaybackDeploymentViewModel` 只依赖 `IPlaybackPlatformStatus` 与 `IPlaybackBackendInitializer`，负责全部部署状态、聚合问题、重检命令及同步初始化。部署子 View 直接绑定它，不再经 `Presentation.Owner` 获取整个协调器。每次检查结束发布 `Checked`，协调器仅在播放会话为空或部署失败时更新总状态，避免重检覆盖正在播放的提示。

`PlaybackDiagnosticsViewModel` 只接收内部导出端口和本次失败快照。它拥有唯一的导出忙碌状态、保存提示及防重入门闩。调用者取消与组件关闭都会取消本次导出；即使导出器忽略取消并返回 JSON，也会在交还 View 前再次检查令牌。关闭只取消，导出方法的 finally 释放令牌源并清理门闩，关闭后的完成与保存提示不再通知 UI。保存选择器和文件写入仍由 View 负责。

协调器的公开类型、构造参数、旧属性及方法保持兼容。旧属性读写均转发至子组件，没有第二份字段；PropertyChanging 与 PropertyChanged 都只转发一次，可播放性变化继续刷新播放命令。协调器拥有自己创建的两个组件，Dispose 时先退订再关闭；注入的会话、探针、初始化器和导出器仍由原作用域管理。

播放、偏好、进度轮询和全屏仍由协调器负责。State/Transport/Options/Media/Presentation 兼容入口保留，不为消除所有 Owner 属性而扩大本轮改动。

### R1.3：只共享有效性机制

`BatchOperationVersion` 保存三个数字：队列修订、已接受计划修订、操作代次。开始操作和结束运行推进代次，编辑或移除项目推进修订；预检只有同时匹配二者才被接受。比较与接受在短锁内完成，组件不调用外部代码，不跨 await 持锁。

两类 ViewModel 仍各自保存领域计划、集合和密码；关闭令牌、预检取消源、RunId 检查、ItemId 查找与后台并发成员集合均保留在原边界。特别是运行期间移除未开始项，只作废计划，不使正在执行项目的合法进度过期。

| 共同机制 | 必须保留的差异 |
| --- | --- |
| 编辑后计划失效、旧代次回调失效 | 加密允许逐项编辑标题、描述与输出路径，要求公共密码与确认一致 |
| RunId 与 ItemId 限定进度归属 | 解密先重新读取候选公开信息，应用统一输出目录、名称净化与冲突策略 |
| 完成后推进代次、关闭后忽略结果 | 两类任务各自解释单项状态和批次结论，不合并领域计划 |
| 继续使用 SequentialVideoQueueRunner | 取消当前/全部、实际密码学和临时文件提交沿用既有实现 |

未提取通用批次基类、工作流框架或新运行器，也没有把密码交给有效性账本。

### R1.4：明确后端契约，保留事务边界

新增内部窄契约 `IPlaybackOutputLifecycle : IPlaybackBackendInitializer`，包含幂等同步初始化与输出变化通知。`IPlaybackPlayerHost` 继承该契约，会话不再判断 `LazyPlaybackBackend` 类型。惰性后端第一次创建完成后发布通知并转发底层后续输出事件；构造时已就绪的真实主机只检查自身未关闭，输出终生稳定，因此不重复发布通知。会话关闭与代理关闭分别退订各自持有的订阅。

首次用户加载仍在调用线程的同步段初始化，然后才将认证和媒体解析交给后台。没有移动统一操作门、候选提交、回滚、用户意图代次、表面恢复取消或 Source 解绑/释放步骤，也没有改变 LibVLC/MediaPlayer 的每文档所有权。

`PlaybackTrackSelectionPolicy` 是无副作用规则：以真实轨道 ID 匹配列表；未知音轨返回未选择，未知字幕只在列表存在 -1 时回退到关闭字幕。初次媒体控制快照与表面恢复失败共用该规则。是否恢复、发送原生命令、失败优先级和快照提交仍由会话控制。

### SOLID 与朴素设计检查

| 原则 | 本轮落实 |
| --- | --- |
| SRP | 部署、诊断、批次有效性和轨道选择各自具有单独变化原因 |
| OCP / DIP | 新后端只需满足就绪契约，会话不追加具体类型分支；替身可独立验证 |
| LSP | 已就绪与惰性主机遵守相同的同步就绪和可退订通知语义，初始化失败按原错误契约返回 |
| ISP | 部署不依赖完整播放会话，诊断不依赖协调器；新组件均无 Owner 引用 |
| 朴素使用模式 | 使用组合、端口与纯函数；兼容属性只转发，避免双状态、泛型业务框架和分散加锁 |

## 10. 本轮测试与验收

新增四个独立测试文件：`R1PlaybackFeatureTests`、`R1BatchOperationVersionTests`、`R1BatchViewModelTests`、`R1PlaybackTrackSelectionTests`；在 G3/G4 现有测试夹具上补充后端同步就绪、初始化失败重试、事件退订、兼容属性与通知测试。G2/G5 既有测试继续保护取消当前、取消全部、顺序运行、失败继续、文件提交和密码关闭清理。

四个新组件均已加入 `tests/r1-coverage-policy.json`，逐文件要求行覆盖率至少 90%、分支至少 80%。全局门槛维持 74.39% / 51.99%，不新增排除项。定向回归 82 项通过；最终 Fast/Full 和真实工具的结果在下表补录，以实际报告为准。

| 验收项 | 本轮结果 | 证据 |
| --- | --- | --- |
| 锁定还原 / Release 构建 / 脚本回归 | 通过，构建 0 警告 0 错误，脚本 30 项 | `r12-r14-validation/restore.log`、`build.log`、`summary.json` |
| 业务 / UI / Host / Host UI | 231 / 5 / 6 / 3 通过；覆盖率中的真实工作流用例明确跳过 1 项 | 同目录测试 TRX 与 `coverage` |
| 真实双插件工作流 | 1 / 1 实际通过，独立包安装与摘要校验通过 | `r12-r14-validation/host/workflow.trx`、`host.log` |
| 四套合并覆盖率 | 行 75.76%、分支 54.93%，高于 74.39% / 51.99% 基线 | `r12-r14-validation/coverage/coverage-summary.json` |
| 新增组件覆盖率 | 部署 100% / 100%；诊断 100% / 95.45%；账本 100% / 100%；轨道规则 100% / 100% | 同上，全部满足 90% / 80% 门槛 |
| G3 / G8 功能矩阵 | 均通过；20 次 Dock、30 次换片；G8 覆盖 100 队列项、1000 媒体库项、10 次全屏、50 次 Dock | `r12-r14-validation/phase4/phase4-g3.json`、`phase4-g8.json` |
| Phase4 资源 | **失败**：句柄 1600 → 1624，净增 24 超过 +10；Semaphore +15。私有内存变化 -19.49 MiB，未超过 +64 MiB；表面 221 / 221，播放资源全部归零 | `r12-r14-validation/phase4/report.json` |
| R1 Full 总结 | **失败**，后续 Standalone / memory / performance 按失败即停规则记为 notExecuted | `r12-r14-validation/summary.json` |
| 后续工具独立补充验收 | Standalone 与 64/512 MiB 内存通过，文件解锁/哈希/临时文件检查通过；三轮 G10 硬门禁通过，但历史性能比较未通过 | `TestResults/R1/r12-r14-supplemental/summary.json`、`performance-comparison.json` |
| 最终代码门禁：构建 / 测试 / 联调 | 构建 0 警告 0 错误；业务 231、UI 5、Host 6、Host UI 3 通过；双插件工作流 1 / 1 实际通过 | `TestResults/R1/r12-r14-final/summary.json` 与各阶段 TRX |
| 最终代码覆盖率 | 行 75.77%、分支 54.93%；四个组件仍为 100% 行覆盖率、分支最低 95.45% | `r12-r14-final/coverage/coverage-summary.json` |
| 最终真实播放与资源 | G3/G8 功能通过；句柄 1543 → 1577（+34，超过 +10），Semaphore +15，私有内存 +12.51 MiB（低于 +64 MiB），表面 221 / 221，播放资源归零 | `r12-r14-final/phase4/report.json` 与子报告 |
| 最终 Full 结论 | **失败**，唯一 Phase4 失败码为 PHASE4_HANDLE_LIMIT_EXCEEDED；后三步仍为 notExecuted，初轮补充验收和性能对照单独保留 | `r12-r14-final/summary.json` |
| 最终源码一致性 | src/tests/scripts/tools 下 226 个文件与最终 Full 采集 SHA-256 完全一致；之后只补录文档 | `r12-r14-final/source.json` |

本表与第 5 节的原始基线分开记录。若 Full 在 Phase4 失败，后续步骤仍保持 notExecuted；另行执行的工具报告作为补充证据，不修改 Full 的失败结论。


R1.2 与 R1.3 的实现和对应测试/Host UI 退出条件已满足；R1.4 的选定依赖与规则调整完成，功能验收通过，但资源退出条件未满足，不能标记整个 R1 验收通过。当前证据没有证明原生资源问题已修复，也不将资源差异简单归因于本轮重构。

初轮 Full 之后恢复了文件原有换行风格，并在最终审查中补齐兼容属性的 PropertyChanging 转发及退订测试。因此另行运行最终 Full，重新采集产品源码摘要、构建、覆盖率及真实播放证据。文档补录与工具对照报告不改变产品逻辑。


### 性能失败的有限对照诊断

第一组三轮 G10 的历史比较失败项为 512 MiB 解密/加密耗时：解密聚合中位值 830.93 ms，高于 714.18 ms 上限，P95 1226.04 ms 高于 860.28 ms；加密 585.93 ms 高于 577.44 ms，P95 785.71 ms 高于 684.53 ms。这里的聚合值沿用既有 G10 聚合器定义，不自行替换统计口径。

从上一轮已通过 Host 包校验的隔离安装目录取出重构前插件，复用当前同一套基准工具，以 before/candidate 顺序交替三轮。重构前 DLL 的 SHA-256 为 `F469E1B92A34D8D05E035B983061F5492B599384463E21366F06C606221EFB8A`；本轮初版候选为 `1D43AF35E148630598D0832C44351E27D75DE52585F6F5E33B553F82F6FA021F`。该候选先于最后补充 PropertyChanging 的改动，诊断目的只限于本轮未修改的密码学/媒体库性能路径。

结果：两组都相对历史基线出现热扫描超限；候选组另有大文件加密超限，相对重构前组的完整比较也未通过。随后在相同工具目录以 candidate/before/before/candidate 顺序执行 512 MiB 单项诊断，四次加密耗时依次为 405.03、411.60、449.12、417.22 ms，未复现此前候选 996–1288 ms 的耗时。

| 证据 | 作用与限制 |
| --- | --- |
| `r12-r14-performance-ab/provenance.json` | 固定旧/新 DLL 来源和摘要，不替换生产插件或 Host 安装 |
| `before-aggregate.json`、`candidate-aggregate.json` | 相同参数、环境指纹的三轮完整采集，硬门禁均通过 |
| `historical-before.json`、`historical-candidate.json`、`before-candidate.json` | 三种正式比较均未通过，保留全部失败项 |
| `same-path-diagnostic.json` | 同目录 C/B/B/C 单项诊断，仅帮助定位，不替代完整 G10 门禁 |

本轮没有修改密码学、文件写入或媒体库扫描实现，但现有证据仍不足以把超限直接归为环境噪声，也没有证明不存在性能退化。停止重复采样，将性能验收保持未通过；后续需定位 I/O、进程环境或采样稳定性后，按原场景和阈值重新验收。不能用后一次单项通过覆盖前三轮正式比较失败。


### 交付与未完成项

本轮已完成部署/诊断组件、批次有效性账本、后端生命周期契约和轨道选择规则，以及对应中文设计注释、测试与文档。没有使用 AIFLOW，没有部署到在用 Host；Host 工作树仍干净，Workflow Studio 仍只有此前已有的 Directory.Build.props 改动。实施结束时未自动提交，随后按用户明确请求将本轮代码、门禁及文档提交为 R1 检查点；资源与性能阻断不因 Git 提交而视为关闭。

后续验收阻断为：原生句柄增长，以及 G10 完整性能比较的超限与稳定性定位。共享 LibVLC 引擎仍只是第 8 节记录的待定方案。R1.4 资源退出条件仍未满足，整个 R1 的资源与性能完成标准保持未勾选，不能据本轮功能通过宣称可以发布。
