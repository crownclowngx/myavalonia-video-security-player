# VideoSecurityPlayer 设计文档

## 交互与功能优化方案

按日期和版本从新到旧排列。文档版本与插件发布版本独立。

| 日期 | 版本 | 文档 | 状态 |
| --- | --- | --- | --- |
| 2026-09-28 | v1.1 | [S1：设置与维护、共用 LibVLC 可执行实施计划](2026-09-28-settings-maintenance-and-shared-libvlc-v1.1.md) | 实施依据；实际进度和证据见专用记录 |
| 2026-09-28 | v1.0 | [设置与维护、共用 LibVLC 改造方案](2026-09-28-settings-maintenance-and-shared-libvlc-v1.0.md) | 保留交互提案及对话演示；实施安排以 S1 v1.1 为准 |
| 2026-09-15 | v1.1 | [UX1：实施修订](2026-09-15-ux-functional-optimization-v1.1.md) | 实施后的交互规则与 SDK 能力边界 |
| 2026-09-15 | v1.0 | [UX1：交互与功能优化方案](2026-09-15-ux-functional-optimization-v1.0.md) | 初始方案，实施进度见专用记录 |

后续实质修订采用 `YYYY-MM-DD-ux-functional-optimization-vX.Y.md` 命名，保留历史版本并更新本索引。

## 架构设计

- [安全视频子系统架构设计](architecture-design.md)：分层、播放会话、原生表面、文件处理与生命周期设计。

## 相关文档

- [S1 实施与验证记录](../reference/S1-IMPLEMENTATION-AND-VALIDATION.md)：本轮实际批次、测试、失败修复和证据记录。
- [S1 使用说明](../reference/S1-SETTINGS-AND-RUNTIME-GUIDE.md)：已实现行为、共用运行库及本地开发命令。
- [UX1 实施与验证记录](../reference/UX1-IMPLEMENTATION-AND-VALIDATION.md)：当前实施进度与本地开发验收证据。
- [功能使用与文档导航](../README.md)
- [工程文档总索引](../../README.md)
