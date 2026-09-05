# V3 G11：MySmallTools 验收

> 完成日期：2026-08-22
>
> 状态：已完成；本记录是开发期非发布证据，不是发布批准。
>
> 前置基线：[G10 DaTangAccountingHelpPlug 验收](../../../../../avalonia_dock_simple_test/docs/plan-history/host-v3/g10-datang-accounting-help-v3-acceptance.md)

## 1. 结论

MySmallTools 的播放器、媒体库、加密器和解密器继续作为四个非持久化 Document。SECVID03、
AES-256-GCM、LibVLC、批处理和媒体库算法没有升级。New 激活、Restore 拒绝、Scope 隔离、关闭令牌、
View 解绑与全屏租约均通过 V3 Host 验收；最终 3.0.0 ZIP 通过真实 Loader、Provider 和 Workspace
看到四个 Document。

20 轮本地真实媒体 Harness 是本阶段必要的资源测试，不是 Windows CI、Smoke 或发布门禁。它在全屏
租约仍有效时直接关闭 Document，并证明后续新实例仍可获取租约。

## 2. 设计思路与资源释放顺序

四个 Document 各自拥有 Scope、模型和 View；播放器/媒体库 Scope 还拥有 PlayerHost、Native
Dispatcher、Surface Restore、Reaper、媒体输入和加密流。全屏只持有 Host 返回的幂等
`IDisposable` 租约，不保留 owner，也不调用恢复式 Host API。

关闭顺序固定为：停止接受新命令并取消 ClosingToken，释放全屏租约并把唯一 PlayerShell 移回原位，
销毁视频表面/原生输出，停止播放器，释放媒体输入和加密流，排空 Dispatcher/Reaper，解绑 View，最后
释放 Document Scope。重复释放租约或模型不会影响后续实例。

真实 Harness 结束前主动排空 Avalonia Dispatcher、等待终结器并压缩 LOH；因此弱引用仍存活表示真实
视觉树或异步回调持有，而不是一次尚未发生的自然 GC。

## 3. SOLID 对照

| 原则 | G11 落点 |
| --- | --- |
| SRP | Document 拥有会话状态，播放组件各管一种原生资源，Host 租约会话只管窗口内容排他所有权。 |
| OCP | 四个 Document 和播放器复用现有激活、关闭与租约契约，Host 不增加 MySmallTools 特判。 |
| LSP | 四个非持久化 Document 一致拒绝 Restore；所有租约都支持幂等 Dispose。 |
| ISP | 播放器只依赖全屏展示窄端口和 Document 生命周期，不接收 Window owner 或 Dock。 |
| DIP | 高层播放协调依赖播放器、Dispatcher、表面和资源回收端口，LibVLC 细节停留在适配层。 |

沿用普通构造注入、不可变快照和 `IDisposable`；没有引入通用资源框架、Manager 或服务定位器。

## 4. 兼容边界与删除面

- 保持 SECVID03 格式、加解密结果、媒体库和批处理行为；
- 保持插件 3.0.0、manifest schema 2 与 SDK `[3.0.0,4.0.0)`；
- 删除活动 MySmallTools V2 测试/脚本入口，不恢复 owner 式全屏 API；
- 覆盖率基线固化在 `MySmallTools.Tests/coverage-baseline.json`，不得降低；
- 不调用历史 MySmallTools ReleaseAcceptance、Accept、Approve 或发布脚本。

## 5. 实际自动化证据

```powershell
.\scripts\Test-MySmallToolsV3.ps1 -Configuration Release -NoRestore -HarnessCycles 20
```

| 套件 | 通过 | 失败 | 跳过 |
| --- | ---: | ---: | ---: |
| Plugin SDK | 37 | 0 | 0 |
| Host Unit | 188 | 0 | 0 |
| Headless UI | 62 | 0 | 0 |
| Plugin / Dock | 204 | 0 | 0 |
| MySmallTools | 184 | 0 | 0 |
| 最终 ZIP → Workspace | 1 | 0 | 0 |
| 合计 | **676** | **0** | **0** |

Host 覆盖率为 **84.39% / 70.58%**；MySmallTools 基线为 **72.59% / 48.12%**。真实媒体报告
`success=true`，播放器、媒体输入、加密流、Surface Restore、Native Dispatcher、缓存等最终计数全部
为 0，关闭后的 Document/View 以及已释放加密流弱引用也全部为 0。

两次隔离构建均生成 431 文件 ZIP，SHA-256 为
`8C017E7059FFFB62156E19AAC18E86BF5170184FA0E9DABB048019B668CC13BF`。机器证据位于
`artifacts/test-results/MySmallToolsV3/summary.json` 与同目录 `real-media-harness.json`。

## 6. 非发布声明与回滚

```text
aiflow=false
windowsCi=false
windowsSmoke=false
releaseAcceptance=false
releaseGate=false
publishable=false
```

本阶段没有运行 AIFLOW、Windows CI/Smoke 或发布入口。G11 的回滚单位是 MySmallTools 活动测试、覆盖率
基线、V3 专项脚本和当前文档；不得恢复 owner API、缩短默认 20 轮资源测试或把测试 ZIP 描述为可发布制品。
