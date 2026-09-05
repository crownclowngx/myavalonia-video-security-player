# G8：P1 集成验收

> 实施日期：2026-07-25
> 平台：Windows x64、.NET 10
> 状态：技术实现与本地自动化完成；正式 clean worktree 与人工窗口签字待执行

## 1. 目标与非目标

G8 不增加产品功能，而是把 G5～G7.1 的规模、组合、隔离和敏感信息边界变成可重复证据。
SECVID03、密码生命周期、事务式不覆盖提交、用户数据版本和 Document Scope 均未改变。

不包含并行队列、断点恢复、后台队列、跨平台抽象或新的持久化字段。验收发现缺陷时，修复
必须留在拥有该职责的服务或 ViewModel 中，不建立跨加密、解密、媒体库和播放的总协调器。

## 2. SOLID 边界与验收分层

| 层次 | 单一职责 | 不承担 |
| --- | --- | --- |
| xUnit | 真实百文件闭环、千文件扫描、TOCTOU、确定性取消和模型脱敏 | HWND、视觉树和 LibVLC |
| 宿主插件测试 | 八个 Document 的真实 DI Scope 创建与释放 | 视频解码和人工布局判断 |
| IntegrationHarness G3 | 保持原播放、Seek、资源和生命周期门禁 | P1 规模组合 |
| IntegrationHarness G8 | 真实窗口、虚拟化、Dock、全屏、多 Document 和资源归零 | 复制产品状态机 |
| P1 脚本 | 串行编排、两轮报告、canary 扫描和证据汇总 | 代替人工可用性判断 |

验收工具只有一个内部套件端口。宿主启动、临时工作区、G3 和 G8 各自拥有生命周期；没有新增
生产公开 API。媒体库设置和历史仍是有意共享的进程级单例，密码、任务、扫描会话、当前媒体、
播放器和 HWND 必须按 Document 隔离。

## 3. 验收矩阵

| 编号 | 场景 | 自动退出条件 |
| --- | --- | --- |
| G8-Q100 | 100 文件真实加密、解密 | 最大活动项 1，100/100 成功，SHA-256 相同，无 partial |
| G8-Q-FAIL | 失败、取消、重试、移除 | 单项不阻断后续，取消语义与 G5 一致 |
| G8-Q-RACE | 严格阻止、安全改名、计划后竞争 | 哨兵不被覆盖，稳定返回 `OutputConflict` |
| G8-L1000 | 三层目录 1,000 个真实 SECVID03 | 递归扫描无重复，搜索、四类排序和虚拟化可用 |
| G8-L-WATCH | 新增、改名、删除、140 路径事件风暴 | 列表无重复，风暴回退为恰好 1,140 项完整快照 |
| G8-DOC8 | 四类 Document 各两个 | 密码、队列、Browser、Player 均独立，释放一个不影响另一个 |
| G8-PLAY | 倍速、50 次 Dock、10 次全屏 | Player 身份不变，表面恢复，UI heartbeat 活跃 |
| G8-RESOURCE | 关闭全部 Document | 八类播放资源计数全部为零，无意外顶层窗口 |
| G8-SECRET | 用户数据、报告、日志和 DTO | canary、密码字段、派生密钥和明文标记零命中 |

真实目录会话在初扫后继续持有 watcher，所以 Harness 等待 `IsScanning == false` 和目标数量，
不等待 `LoadFolderAsync` 的长生命周期任务结束；关闭 Document 才取消该任务。这一等待方式
与产品所有权一致，也避免把“监听仍然存活”误判为超时。

## 4. 命令与报告

旧 G3 命令保持兼容，无 `--suite` 时仍运行 G3。G8 单轮入口：

```powershell
dotnet run --project .\Plugins\MySmallTools\MySmallTools.Playback.IntegrationHarness\MySmallTools.Playback.IntegrationHarness.csproj -c Release -- --suite g8 --queue-items 100 --library-items 1000 --report .\TestResults\G8\g8-window-manual.json
```

完整串行入口：

```powershell
.\scripts\Accept-MySmallToolsP1.ps1 -AllowDirty
```

脚本依次构建插件和 Harness、运行 157 项插件测试、21 项宿主测试、缩短规模的 G3 命令兼容
门禁、两轮完整 G8 窗口门禁及敏感信息扫描。G3 的正式 100 次生命周期压力仍由 P0 发布脚本
负责，G8 不重复执行同一长压测。原始日志和临时资产位于被忽略的
`artifacts/MySmallTools/p1-acceptance`；仓库只保存脱敏 JSON 和人工签字说明。

## 5. 安全与报告约束

- 脚本为每轮注入不同 canary，套件据此派生不同 Document 密码与公开描述；轮次标识和密码
  只存在于进程环境、对应 ViewModel 和同步调用链，报告不保存这些值。
- 报告只写场景代码、数量、耗时、资源计数和运行版本，不写绝对临时路径或异常原文。
- canary 扫描覆盖结构化报告、用户数据和待提交证据；LibVLC 原始 stderr 不持久化，
  避免重定向原生输出改变真实窗口时序。失败报告只记录命中数量。
- 用户数据仍允许明文保存媒体路径和播放位置，这是已文档化的本地隐私边界；不得包含密码、
  派生密钥、公开描述全文或明文媒体内容。

## 6. 当前结论

- `MySmallTools` Release 和 IntegrationHarness Release 均为 0 警告、0 错误。
- `MySmallTools.Tests`：157/157。
- `MyAvaloniaManagement.PluginTests`：21/21。
- G3 拆分后兼容运行通过。
- G8 100 队列、1,000 媒体库、8 Document、10 次全屏和 50 次 Dock 组合连续两轮通过。
- 两轮总耗时分别为 18,759 ms、17,990 ms；最大 UI heartbeat 间隔分别为 78 ms、74 ms。
- 两轮关闭全部 Document 后，租约、播放器、媒体输入、解密流、缓存块、原生调度器和回收器均归零。
- 敏感信息扫描 9 个结构化文本文件，0 命中。

正式 P1 签字仍要求从 clean worktree 执行完整脚本并完成人工窗口清单；在此之前路线图只能标记
“G8 技术门禁完成、正式签字待办”，不能宣称发布候选完成。
