# 2026-09-05 独立插件迁移记录

## 结果与兼容边界

MySmallTools 已从 avalonia_dock_simple_test 内置插件迁入本仓库，统一改名为 VideoSecurityPlayer。程序集为 VideoSecurityPlayer.Plugin，入口为 VideoSecurityPlayer.Plugin.VideoSecurityPlayerPluginModule，部署目录为 Controls/VideoSecurityPlayer。

Plugin/Document/Action ID、插件版本 3.1.0、SECVID03 格式、安全规则和正式用户数据位置保持原值。四种视频功能及非破坏性 Workflow Action 均保留；密码、队列、播放器等文档局部状态继续由独立 Scope 拥有。

## 文件与业务核对

[source-inventory.json](source-inventory.json) 记录 253 个原文件的来源、目标、源摘要、目标摘要及改动原因，其中覆盖原插件的全部版本控制文件和迁移前已修改的 Harness 锁文件。额外保留了[原 Harness 锁文件](original-harness-packages.lock.json)。

118 个生产 C#/AXAML 文件在归一化换行及允许的命名、程序集资源 URI 替换后与原实现一致。AssemblyInfo 单独记录开发友元适配，项目文件单独记录 SDK NuGet、构建协议和原生资产配置变更。没有改写加解密、认证格式、播放、队列、媒体库、工作流或安全决策代码。

核对命令：

```powershell
./scripts/Verify-Migration.ps1
```

该命令始终核对迁移快照的目标摘要；原工作区备份存在时，还验证源摘要和生产文件的机械替换边界。后续业务开发有意修改文件时，应将这份清单视为迁移时刻的快照。

所有原设计、阶段决策、黄金向量、媒体授权、性能基准及专项证据均已迁入。链接按新位置更新；确实不存在的历史入口记录在 [historical-link-inventory.json](historical-link-inventory.json)。综合宿主历史继续留在主仓库，视频专属记录迁出；混合 Phase4 证据在两边保留。

## 必要适配

- 默认独立解决方案仅通过 SDK/UI 3.3.0 和 Build 1.1.2 包接入；保留原业务依赖版本、LibVLC 私有托管桥接和完整 Windows x64 原生目录。
- 原测试、三个工具和专属 Host/UI 验收迁入新仓库。HostTests、HostUiTests、播放 Harness 显式要求 HostRepositoryRoot，不进入默认解决方案；仅测试侧具有 Host 友元访问权限。
- Standalone 消费真实 Module 注册信息，支持四功能多标签、多实例隔离、退出取消、失败释放、独立用户数据和内容区全屏覆盖层。添加 Windows supportedOS 应用清单，以支持真实 NativeControlHost 子窗口。
- 新增显式原生调试验收参数：`--smoke-media <普通视频> --smoke-report <JSON>`；不属于正式插件交付内容。
- 主仓库移除旧源码、解决方案条目、项目引用、专属测试及旧 LibVLC 版本声明。通用兼容测试使用自身夹具；跨仓库 Gate 消费新仓库、真实 ZIP、迁出的 Harness 和完整合并覆盖率。

## 验证结果

| 项目 | 结果 |
| --- | --- |
| 原业务测试与新增关闭/失败测试 | 205/205 |
| 独立界面及多标签/全屏租约 | 5/5 |
| Host 组合及真实 ZIP 加载 | 6/6 |
| Host Dock UI | 3/3 |
| 两个真实 ZIP 的 Workflow Studio 加密闭环 | 1/1 |
| 主仓库 Host Unit / Plugin / UI / Gate Tests | 292 / 209 / 61 / 21，全部通过 |
| 插件合并行/分支覆盖率 | 74.39% / 51.99%，高于 72.59% / 48.12% 原门槛 |
| 真实 Windows x64 播放 Harness | 20 轮通过，最终资源计数全部归零 |
| Standalone 真实原生窗口 | 播放、进出全屏、标签切换、Seek、关闭后文件解锁通过 |
| 64/512 MiB 大文件内存门禁 | 通过，临时文件与明文缓存归零，输出摘要一致 |
| SECVID03 安全基准工具 | 成功运行，固定向量校验通过 |
| 插件 ZIP | 431 文件，稳定 ID、入口、摘要、私有原生目录及共享程序集排除通过 |

机器证据见 [validation-summary.json](validation-summary.json) 和 [package-summary.json](package-summary.json)。本地详细报告及浅色/深色截图保存在 TestResults/Migration。历史验收数字没有改写为本轮结果。

本轮没有执行完整跨仓库 seal、G8/G10/Phase4 的全部历史长时性能矩阵、签名、上传或正式发布；相应工具、基线和历史记录保留。

## 原工作区保护与清理

旧 Plugins/MySmallTools、专属宿主测试、视频专项文档/证据以及旧 Controls/SmallTools 已移出主仓库活动路径。永久删除曾被自动审批以策略阻止，最终采用可逆迁出，备份位于新仓库 TestResults/Migration/retired-originals，不参与构建、打包和版本控制。

主仓库其他插件的未提交修改保留，没有重置用户工作区、提交 Git、推送、修改正式用户数据或部署到正在使用的 Host。正式安装仍须在 Host 关闭后用新目录替换旧目录，防止相同插件 ID 重复加载。
