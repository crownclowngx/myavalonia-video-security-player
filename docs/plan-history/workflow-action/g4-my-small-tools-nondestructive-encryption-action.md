# Workflow Action G4：MySmallTools 非破坏性加密 Action

> 状态：已完成（2026-08-26；本地开发门禁）
>
> MySmallTools 候选版本：`3.1.0`
>
> 外部 Workflow Studio：`1.1.0`，生产源码无 G4 业务特判
>
> 总设计：[工作流执行与可选 AI 规划方案](../../../../../avalonia_dock_simple_test/docs/design/ai-workflow-plugin-exploration.md)

## 1. 结果与边界

G4 在 MySmallTools 私有 Provider 中声明
`myavalonia.plugin.my-small-tools.workflow.encrypt-video`，并由 Host 为每次调用创建 scoped Handler。
动作只表达“生成新的 SECVID03 文件并保留源文件”；没有删除参数、`File.Delete`、补偿事务或 UI
ViewModel 依赖。现有加密 Document 继续直接使用 `IVideoEncryptionService`，没有改走 Action。

Workflow Studio 仍是只依赖 Core/UI/Workflow SDK 的通用 Consumer。G4 使用 Studio `1.1.0` 和
MySmallTools `3.1.0` 两个真实 ZIP，经独立 ALC、私有 Provider、Studio 编辑器、会话 Secret、Runner、
Host 授权与 Gateway 完成无窗口闭环；Studio 正式源码没有出现 MySmallTools 名称、Action ID 或预设。

## 2. Action 合同

| 项目 | 冻结事实 |
| --- | --- |
| Action ID | `myavalonia.plugin.my-small-tools.workflow.encrypt-video` |
| 必填输入 | `inputPath`、`outputPath`、`password` |
| 可选输入 | `publicTitle`、`publicDescription`；缺省为空字符串 |
| 长度 | 路径 32767；密码 6–1024；标题 200；描述 10000 Rune |
| 风险 | `ReadsLocalFiles + WritesLocalFiles + HandlesSecret + LongRunning` |
| 确认 | `OncePerRun` |
| 敏感指针 | `/password` |
| 成功输出 | 仅规范化绝对 `outputPath` |

密码只进入本次 Handler 调用栈和 `IVideoEncryptionService.EncryptAsync` 参数，不进入 Handler 字段、
进度、输出、定义、诊断或门禁摘要。公开标题和描述属于 SECVID03 明文公开区，不应被误解为 Secret。

Caller 不能提供 InvocationId。重复相同参数时，Host 会生成新的 InvocationId；第二次调用由现有输出
冲突预检拒绝，不覆盖第一次的正式输出，也不修改或删除源文件。

## 3. SOLID 与朴素设计

| 原则 | G4 做法 |
| --- | --- |
| SRP | Descriptor 冻结 JSON 合同；Handler 只映射；应用服务预检；Encryptor 加密；事务提交文件 |
| OCP | 新 Action 通过目录进入 Studio，Studio 生产代码零修改、零业务预设 |
| LSP | Handler 只依赖 `IVideoEncryptionService`，单元测试用窄替身验证映射、取消和失败 |
| ISP | 跨插件只出现 SDK/BCL/JSON，不暴露 ViewModel、私有 DTO、Provider 或 `IServiceProvider` |
| DIP | 组合根选择真实加密服务，Handler 不构造 Encryptor、事务或存储探针 |

实现只使用构造注入、一个私有 DTO、一个结果 DTO 和一个进度适配器。没有引入 Mediator、事件总线、
反射式 Action 暴露、通用管线或第二套加密用例。双 ZIP 测试中的反射只驱动外部 Studio 已公开给 UI
绑定的属性和命令，不进入生产代码。

## 4. 验证与证据

统一入口：

```powershell
pwsh -NoProfile -File .\scripts\Test-WorkflowActionG4.ps1 -Configuration Release
```

门禁依次运行 MySmallTools 既有 SDK/Host/UI/Plugin/真实媒体开发回归、共享 Schema 与 G4 专项测试、
外部 Studio 纯公开源 locked restore/build/test/两次打包、两个真实 ZIP 的 Studio 手工路径、G4 变更文件
格式与文档检查。所有测试必须失败 0、跳过 0；MySmallTools 总覆盖率不得低于 72.59%/48.12%，G4
Action 文件行覆盖率不得低于 90%。2026-08-26 的完整单命令实测为：

| 验证层 | 结果 | 覆盖率或 SHA-256 |
| --- | ---: | --- |
| MySmallTools/Host/SDK 开发回归 | **770/770** | 行 **73.49%**；分支 **49.25%**；Action 行 **94.12%** |
| Workflow Studio `1.1.0` 公开 locked 回归 | **49/49** | 行 **87.69%**；分支 **81.93%** |
| 真实双 ZIP Studio 闭环 | **1/1** | 两个 invocation；第二次目标冲突被拒绝 |
| **合计** | **820/820** | 失败 **0**；跳过 **0**；构建警告 **0** |
| MySmallTools `3.1.0` ZIP | 两轮一致 | `CAB3E1E7D7242A3668B01B0C2A1CC23D4B955AE706E65E4BD0BA5D01C154D765` |
| Workflow Studio `1.1.0` ZIP | 两轮一致 | `00B846576B7DD912E16D694683E1618C8AC50C1CE1B490A35BBFEDDDBAC0F7B3` |

最终机器事实源是 Git 忽略的 `artifacts/test-results/WorkflowActionG4/summary.json`。Studio 门禁本轮
显式跳过候选 Host Windows Smoke，只执行公开依赖、编译、测试和打包；默认 G3 门禁行为没有改变。

文件安全矩阵覆盖真实 CC0 MP4 完整认证解密、源文件 SHA-256 不变、目标冲突不覆盖、取消/磁盘满沿用
既有事务清理门禁、`.partial-*` 归零和调用结束后文件句柄可移动。Invocation Scope 的通用异步释放
继续由 G1 内核证明；G4 不为测试向生产 Handler 增加计数器或无意义的 `IAsyncDisposable`。

## 5. 非发布与回滚

```text
aiflow=false
windowsCi=false
windowsSmoke=false
releaseAcceptance=false
releaseGate=false
publishable=false
published=false
uploaded=false
tagCreated=false
```

`Release` 只表示本地编译配置。G4 不调用任何发布类入口，不上传包、不签名、不打标签。`3.1.0` 是
本地候选插件版本，后续发布必须另行获得授权并执行届时的发布门禁。

回滚单位是 Action Descriptor、Handler、模块声明、`3.1.0` 候选版本、G4 测试/脚本和本文档。
`IVideoEncryptionService`、SECVID03、输出事务、四个 UI Document、Host/SDK 和外部 Studio 生产源码不回滚。
