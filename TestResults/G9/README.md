# G9 平台抽象验收证据

本目录保存 G9 重构后的脱敏真实窗口报告：

- `g9-g3-regression.json`：完整 G3 Windows x64、100 次生命周期、Dock、全屏和快速换片回归。
- `g9-g8-run1.json`、`g9-g8-run2.json`：两轮 G8 百/千规模、八 Document 和播放组合回归。

本次结果：

- 三份报告均为 `success: true`。
- G3 最大 UI heartbeat 间隔 27 ms；最终八类资源均为 0。
- G8 两轮耗时 20,594 ms / 19,538 ms，最大 UI heartbeat 间隔 54 ms / 45 ms；
  最终八类资源均为 0。
- 人工窗口观感确认不由自动化报告代替，仍应独立记录签字人、日期和显示缩放。
