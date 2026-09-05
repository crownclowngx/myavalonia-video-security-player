# G10 验收证据

本目录由 `scripts/Accept-MySmallToolsG10.ps1` 写入可提交的小体积证据。大文件、中间 SECVID03 容器和原始运行目录位于 Git 忽略的 `artifacts/MySmallTools/g10/`。

预期证据：

- 两份 `g10-performance-run*.json`
- 两轮短/长真实窗口报告与规模比较
- 两份产品脱敏诊断样本
- `g10-performance-candidate.json`
- `g10-performance-comparison.json`（存在已审核基线时生成）
- `g10-sensitive-scan.json`
- `g10-acceptance.json`

当前状态：2026-07-26 已从 clean worktree 完成两轮正式技术运行并建立审核基线；
`technicalAcceptancePassed = true`，敏感扫描 0 命中。人工导出签字仍待按 G11 完整测试
手册执行。没有真实人工签字前，不得把 G10/G11 宣称为最终完成。

## 人工验收记录

- [ ] 单文件播放器可选择位置并保存 JSON。
- [ ] 媒体库播放器可选择位置并保存 JSON。
- [ ] 正常播放时导出成功。
- [ ] 错误密码时导出成功且错误域为 `authentication`。
- [ ] 部署不可用时导出成功且错误域为 `deployment` 或 `platform`。
- [ ] 保存取消不显示错误。
- [ ] 无写权限时只显示“无法写入所选位置”，不显示异常原文或路径。
- [ ] 导出期间按钮禁用，播放器和 Dock 保持响应。
- [ ] JSON 不包含媒体名、用户目录、公开标题/描述、密码、密钥或明文 canary。

签字：

- 验收人：
- 日期：
- 结果：pending
