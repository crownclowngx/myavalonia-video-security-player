# 真实媒体测试资产

## 1. 适用范围

仓库内固定媒体位于：

```text
Plugins/VideoSecurityPlayer/VideoSecurityPlayer.Tests/TestAssets/RealMedia/
├─ manifest.json
├─ ASSET-LICENSE.md
├─ synthetic-av-short.mp4
├─ synthetic-silent-multiblock.webm
├─ synthetic-multitrack-subtitles.srt
└─ synthetic-multitrack-subtitles.mp4
```

这些文件用于替代开发者私人视频，为 SECVID03 加密/解密、真实 LibVLC Parse/播放、跨块读取、Seek、Dock 表面恢复和发布验收提供可复现输入。测试不得联网下载媒体，也不得在测试运行时动态生成媒体。

这里的“真实媒体”表示真实可解码的 MP4/WebM 二进制文件，不表示来自真人、摄像机或第三方影视素材。三份资产均由 FFmpeg 合成源生成。

## 2. 当前资产矩阵

| 文件 | 当前媒体属性 | 主要覆盖 |
| --- | --- | --- |
| `synthetic-av-short.mp4` | 3 秒、320×180、H.264、AAC 单声道 | MP4 demux、音视频轨、短时长播放 |
| `synthetic-silent-multiblock.webm` | 6 秒、640×360、VP9、无音轨 | Matroska/WebM demux、无声媒体、至少 3 个 SECVID03 明文块、跨块 Seek |
| `synthetic-multitrack-subtitles.mp4` | 4 秒、320×180、H.264、两条 AAC、mov_text 字幕 | G6 六档倍速、双音轨切换、字幕启用与关闭 |

以下机器可读值只以 [`manifest.json`](../../../tests/VideoSecurityPlayer.Tests/TestAssets/RealMedia/manifest.json) 为准，不在本文复制：

- FFmpeg 名称、版本和下载来源；
- 每个文件的用途、容器、视频/音频编码、分辨率和音轨标志；
- 预期时长、精确字节数和 SHA-256；
- 来源描述、SPDX 标识和完整生成命令。

`RealMediaAssetTests` 会要求清单中声明的媒体集合与目录中的 `.mp4`/`.webm` 集合完全相同，防止未声明素材被误提交。

## 3. 清单格式

当前 `manifest.json` 的 `schemaVersion` 为 `1`：

```text
root
├─ schemaVersion
├─ generator
│  ├─ name
│  ├─ version
│  └─ source
└─ assets[]
   ├─ fileName / purpose
   ├─ container / videoCodec / audioCodec
   ├─ width / height / hasAudio
   ├─ expectedDurationMs / byteLength / sha256
   ├─ sourceDescription / spdxLicense
   └─ generationCommand
```

约定：

- `fileName` 只能是文件名，不能包含目录。
- `container` 当前只允许 `mp4` 或 `webm`。
- `sha256` 使用大写十六进制。
- `spdxLicense` 当前必须为 `CC0-1.0`。
- `generationCommand` 必须可独立复现对应资产。
- WebM 的 `byteLength` 必须大于 `2 × Secvid03Format.ChunkSize`，确保主体覆盖至少 3 个块。

## 4. 固定生成环境

当前清单冻结的生成器为：

- FFmpeg：`8.1.2-essentials_build-www.gyan.dev`
- Windows 构建：清单 `generator.source` 指向的 gyan.dev release essentials
- 视频源：`testsrc2`
- WebM 额外滤镜：固定种子 `noise`（`all_seed=1`）
- MP4 音频源：48 kHz、1000 Hz `sine`，编码为 AAC 单声道
- G6 多轨 MP4：48 kHz、600/1200 Hz 两个 `sine` 音源和仓库内固定 SRT，字幕编码为 `mov_text`
- 编码时使用 `-map_metadata -1` 移除外部元数据

完整 FFmpeg 命令只从清单复制执行，不能根据本文手工还原。生成后使用同一发行包中的 `ffprobe` 检查：

```powershell
ffprobe -v error -show_entries "format=format_name,duration,size:stream=index,codec_name,codec_type,width,height,channels" -of json .\Plugins\VideoSecurityPlayer\VideoSecurityPlayer.Tests\TestAssets\RealMedia\synthetic-av-short.mp4

ffprobe -v error -show_entries "format=format_name,duration,size:stream=index,codec_name,codec_type,width,height,channels" -of json .\Plugins\VideoSecurityPlayer\VideoSecurityPlayer.Tests\TestAssets\RealMedia\synthetic-silent-multiblock.webm

ffprobe -v error -show_entries "format=format_name,duration,size:stream=index,codec_name,codec_type,width,height,channels:stream_tags=language,title" -of json .\Plugins\VideoSecurityPlayer\VideoSecurityPlayer.Tests\TestAssets\RealMedia\synthetic-multitrack-subtitles.mp4
```

## 5. 自动化如何使用资产

| 消费者 | 使用方式 |
| --- | --- |
| `VideoSecurityPlayer.Tests.csproj` | 把 `TestAssets/RealMedia/**` 复制到测试输出目录 |
| `RealMediaAssetTests` | 校验 schema、声明集合、容器签名、精确长度、SHA-256、来源和授权 |
| `Secvid03Tests` / G2、G3 测试 | 使用真实媒体执行加密、解密、按块读取、Seek 和资源关闭 |
| `VideoSecurityPlayer.Playback.IntegrationHarness` | 链接并复制同一资产，运行真实 LibVLC、六档倍速、双音轨/字幕、全屏、Dock 和生命周期门禁 |
| `Release-VideoSecurityPlayerP0.ps1` | 通过测试、内存门禁和两轮 Harness 间接把资产纳入正式发布验收 |

G0 证明资产来源、授权、清单和字节完整性；G3/G3.1 证明同一输入在真实 LibVLC 和 HWND 生命周期中可用；G4 把这些检查纳入统一发布门禁；G6 增加双音轨、字幕和全屏迁移验证。

## 6. 授权与隐私边界

三份媒体由 FFmpeg 合成视频、音频、噪声源和仓库内字幕文本生成，不包含私人视频、外部素材或第三方视听作品。项目按 `CC0-1.0` 提供这些资产；权威声明见 [`ASSET-LICENSE.md`](../../../tests/VideoSecurityPlayer.Tests/TestAssets/RealMedia/ASSET-LICENSE.md)。

更新资产时不得：

- 使用个人、客户或生产环境视频；
- 使用来源或再分发授权不清楚的素材；
- 仅替换二进制而不更新清单；
- 在报告、Manifest 或日志中写入构建机私人媒体路径。

## 7. 更新流程

1. 确认测试覆盖确实需要替换或新增媒体。
2. 使用清单冻结的 FFmpeg 版本和完整命令生成资产。
3. 用 `ffprobe` 核对容器、编码、轨道、分辨率和时长。
4. 更新 `manifest.json` 中的声明、精确字节数和大写 SHA-256。
5. 若来源、用途或授权变化，同步更新 `ASSET-LICENSE.md` 和本文。
6. 确保多块资产仍大于 `2 MiB`，并覆盖有声/无声、MP4/WebM、短时长/多块矩阵。
7. 从仓库根目录串行运行验证。

```powershell
dotnet test .\Plugins\VideoSecurityPlayer\VideoSecurityPlayer.Tests\VideoSecurityPlayer.Tests.csproj -c Release --filter "FullyQualifiedName~RealMediaAssetTests"

dotnet test .\Plugins\VideoSecurityPlayer\VideoSecurityPlayer.Tests\VideoSecurityPlayer.Tests.csproj -c Release

dotnet test .\Host\MyAvaloniaManagement.PluginTests\MyAvaloniaManagement.PluginTests.csproj -c Release
```

需要验证真实播放时再运行：

```powershell
dotnet run --project .\Plugins\VideoSecurityPlayer\VideoSecurityPlayer.Playback.IntegrationHarness\VideoSecurityPlayer.Playback.IntegrationHarness.csproj -c Release -- --report .\TestResults\manual-real-media.json
```

正式发布必须使用：

```powershell
.\scripts\Release-VideoSecurityPlayerP0.ps1
```

## 8. 相关实现与文档

- [RealMediaAssetTests.cs](../../../tests/VideoSecurityPlayer.Tests/RealMediaAssetTests.cs)
- [VideoSecurityPlayer.Tests.csproj](../../../tests/VideoSecurityPlayer.Tests/VideoSecurityPlayer.Tests.csproj)
- [Playback Integration Harness](../../../tools/VideoSecurityPlayer.Playback.IntegrationHarness/Program.cs)
- [SECVID03 格式](secvid03-format.md)
- [架构设计](../design/architecture-design.md)
- [G3 真实播放与 Dock 稳定性](../plan-history/G3-REAL-MEDIA-PLAYBACK-DOCK-STABILITY.md)
- [G4 发布基线](../plan-history/G4-P0-DEPLOYMENT-ACCEPTANCE-RELEASE-BASELINE.md)
- [G6 播放器日常控制](../plan-history/G6-PLAYER-DAILY-CONTROLS.md)
