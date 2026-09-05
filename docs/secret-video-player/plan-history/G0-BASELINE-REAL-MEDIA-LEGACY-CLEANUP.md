# G0：基线、真实素材与遗留清理

> 实施日期：2026-07-23
>
> 适用范围：MySmallTools 安全视频子系统、MySmallTools.Tests 与宿主插件架构测试
>
> 格式原则：SECVID03 是唯一受支持的容器；G0 不修改磁盘布局、KDF、分块、nonce 或 AAD

## 1. 完成目标

G0 为后续格式安全、加解密可靠性和真实播放回归建立了可重复验证的工程基线：

- 测试仓库直接包含版权边界清晰的真实 MP4 和 WebM，不依赖开发机私人视频。
- 资产来源、生成器、生成命令、编码、时长、长度和 SHA-256 由机器清单统一记录。
- 原有 34 项 MySmallTools 测试全部保留，并增加 3 项真实媒体资产测试。
- 宿主插件的 15 项架构与 Document Scope 测试保持通过。
- 旧容器专用提示、旧 AES-CTR/Header 实现和未落地的详细元数据入口从生产表面移除。
- 当前能力、生产入口、自动化证据和权威文档形成明确对应关系。

G0 不实现真实 LibVLC 解码、播放、跨块 Seek 或 Dock 高频切换；这些环境集成行为仍由 G3 验收。

## 2. 当前格式与产品边界

安全视频子系统只接受结构和认证均有效的 SECVID03：

- 固定头、公开区容量、原视频前缀和 1 MiB 分块布局保持冻结。
- PBKDF2-SHA256 迭代次数、AES-256-GCM、nonce 和 AAD 规则没有变化。
- 其他魔数、结构不完整或认证失败的文件由现有 SECVID03 解析链路受控拒绝。
- 播放器不再识别 SECVID02 并给出专用迁移提示，也不提供旧格式播放或迁移工具。
- 播放仍通过 `SeekableEncryptedVideoStream` 按需认证解密，不生成完整明文临时视频。

G0 没有新增格式适配器、迁移 Strategy、全局 Coordinator 或新的生产公共接口。

## 3. 真实媒体 Golden Fixture

测试资产位于 `MySmallTools.Tests/TestAssets/RealMedia/`：

| 文件 | 实际属性 | G0 覆盖目的 |
| --- | --- | --- |
| `synthetic-av-short.mp4` | 3.000 秒、320×180、H.264、AAC 单声道、130,938 字节 | MP4、有声、短时长 |
| `synthetic-silent-multiblock.webm` | 6.000 秒、640×360、VP9、无音轨、2,988,034 字节 | WebM、无声、跨至少三个 SECVID03 明文块 |

两份文件总计 3,118,972 字节。目录中的说明和清单使测试无需网络、FFmpeg 或本机媒体即可运行：

- `manifest.json` 是资产事实的唯一机器可读来源。
- `ASSET-LICENSE.md` 声明素材由项目使用合成源创建，并按 `CC0-1.0` 提供。
- FFmpeg 固定为 `8.1.2-essentials_build-www.gyan.dev`。
- MP4 使用 `testsrc2` 与 1000 Hz `sine`；WebM 使用 `testsrc2` 与固定种子噪声且明确禁用音轨。
- 编码后使用 `ffprobe` 核对容器、编码、轨道、分辨率、时长和文件长度。

测试项目只把资产复制到自身输出目录。素材不会进入 `MySmallTools` 插件部署目录，不增加生产发布体积。

## 4. 清单驱动的完整性规则

`RealMediaAssetTests` 使用清单驱动的 Golden Fixture 模式，不在测试代码中复制哈希或文件长度：

1. Theory 从 `manifest.json` 枚举资产，因此新增同类资产不需要新增专用测试方法。
2. 每个条目必须使用安全的单文件名并对应实际文件。
3. 实际字节长度和 SHA-256 必须与清单完全一致。
4. MP4 必须具有 `ftyp` 签名；WebM 必须具有 EBML 签名。
5. 资产目录不允许存在未登记的 MP4 或 WebM。
6. 整体矩阵必须同时包含 MP4、WebM、有声、无声、最多 3 秒的短素材和大于两个 1 MiB 块的素材。
7. 清单必须记录正数预期时长、来源、生成命令、生成器版本和 `CC0-1.0` 授权。

清单、文件内容、文件名或覆盖矩阵发生漂移时，测试会指出具体资产并失败。

## 5. 遗留生产表面清理

G0 删除了以下没有现行调用者的公共表面：

- `AesCtrHelper`：旧 AES-CTR 密钥流和计数器辅助实现。
- `HeaderHelper`：包含 SECVID02 魔数和旧固定 Header 布局的辅助实现。
- `MetadataExtractor`：已注册到 DI 但没有任何消费者的普通媒体解析服务。
- `VideoMetadata`：只由未使用元数据入口引用的模型。
- `SecureVideoPlayer.GetDetailedMetadata()`：始终返回 `null` 的占位方法。

同时完成以下关联收口：

- 从 `MySmallToolsPluginModule` 删除 `MetadataExtractor` 的 Transient 注册。
- 从 `MySmallTools.csproj` 删除旧实现遗留的 `System.Security.Cryptography.Algorithms 4.3.1` 包引用；SECVID03 继续使用 .NET 9 自带密码学 API。
- 删除播放器 ViewModel 的 `IsSecvid02()` 探测和专用提示；无效输入统一显示“不受支持或已经损坏”的可操作信息。
- 删除顶层 `VideoEncryptor_README.md` 迁移页，并从当前 README、格式、架构和排障文档移除旧方案操作描述。

这些删除是有意的公共表面收缩。当前仓库没有受支持的外部消费者，因此不增加空兼容层或弃用期。

## 6. SOLID 与资源所有权

G0 采用最小必要设计，没有为了形式完整增加运行时模式：

- 单一职责：播放器只负责 SECVID03 加载与播放；资产清单只描述测试输入；完整性测试只验证资产契约。
- 开闭原则：同类素材通过新增清单条目扩展，通用完整性测试无需增加格式专用流程。
- 接口隔离：删除无人消费的元数据服务和空返回方法，不把未来功能暴露为当前接口。
- 依赖倒置：生产 DI 只保留真实消费者需要的服务，不为旧格式或测试资产注册运行时服务。

既有资源所有权保持不变：

- 每个播放器、媒体库、加密器和解密器 Document 继续拥有独立 DI Scope。
- 密码只存在于当前 Document 与同步调用链，不进入资产清单、任务模型、日志或文档。
- 播放链路继续由 `SecureVideoPlayer` 逆序释放 Media、MediaInput 和随机读取流。
- 关闭 Document 时的取消、partial 清理和其他 Document 隔离行为没有变化。

## 7. 自动化测试与构建基线

验证命令必须串行执行，因为两个测试项目共享项目引用和中间输出目录：

```powershell
dotnet build MySmallTools\MySmallTools.csproj --no-restore -p:SkipPluginDeploy=true -warnaserror
dotnet test MySmallTools.Tests\MySmallTools.Tests.csproj --no-restore
dotnet test MyAvaloniaManagement.PluginTests\MyAvaloniaManagement.PluginTests.csproj --no-restore
```

2026-07-23 的结果：

- MySmallTools：0 个警告、0 个错误。
- MySmallTools.Tests：37/37 通过，其中原有 34 项全部保留，新增 2 个逐资产 Theory 用例和 1 个覆盖矩阵用例。
- MyAvaloniaManagement.PluginTests：15/15 通过。
- `git diff --check` 通过。
- 生产入口和当前用户文档的定向扫描中，不再存在旧算法、旧 Header、空元数据入口或旧内存解密操作说明。

宿主解决方案其他插件的既有警告不属于 MySmallTools G0；本功能组不得新增 MySmallTools 警告。

## 8. 文档证据与后续边界

G0 完成后，文档职责如下：

- [README](../README.md)：当前用户能力、使用方式和限制。
- [SECVID03 文件格式](../reference/secvid03-format.md)：冻结的磁盘布局和认证规则。
- [概要设计](../design/architecture-design.md)：组件、DI Scope、数据流和资源所有权。
- [接入、约定与排障](../troubleshooting/integration-and-conventions.md)：能力—生产入口—测试—文档映射及集成约束。
- [真实媒体测试资产](../reference/real-media-test-assets.md)：资产来源、授权、生成、校验和更新流程。
- [实施路线图](ROADMAP.md)：G0 完成状态和 G1～G11 后续计划。

后续阶段不得把资产存在等同于真实播放已经验收：

- G1 建立 SECVID03 威胁模型、固定测试向量和畸形输入矩阵。
- G2 统一加解密预检、失败分类、取消和资源闭环。
- G3 才使用这些真实媒体执行加密、LibVLC 解析、播放、跨块 Seek、解密还原和 Dock 高频切换回归。
- 任何需要改变 SECVID03 格式的安全问题必须停止普通功能扩展，并另立格式升级计划。
