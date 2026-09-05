# G4 本地验收证据

执行时间：2026-07-24

执行入口：`scripts/Release-MySmallToolsP0.ps1 -AllowDirty`

本目录保存适合进入仓库的结构化证据：

- `g4-acceptance.json`：串行发布门禁汇总及 ZIP SHA-256。
- `g4-manifest.json`：发布包规范化相对路径、长度与 SHA-256。
- `g4-deployment-probe.json`：从最终 ZIP 解压目录运行生产探针的结果。
- `g4-memory-gate.json`：64/512 MiB 子进程内存与正确性门禁。
- `g4-playback-run1.json`、`g4-playback-run2.json`：两轮真实 Avalonia/LibVLC/Dock 门禁。

全部技术门禁通过。由于验证对象是尚未提交的本次实现，`g4-acceptance.json` 正确记录
`publishable: false`。这表示该 ZIP 是本地验收产物，不是正式发布候选；提交代码后必须在
clean worktree 不带 `-AllowDirty` 重跑同一脚本，只有新报告为 `publishable: true` 才可发布。

46 MiB 的 ZIP 留在被 Git 忽略的 `artifacts/MySmallTools/p0-win-x64/`，避免把可再生二进制
提交到仓库；其哈希已由验收摘要和 Manifest 留档。
