using System.Text.Json;
using System.Text.Json.Serialization;
using MyAvaloniaManagement.PluginSdk;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;
using VideoSecurityPlayer.Constants;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Workflow;

/// <summary>
/// 集中创建 VideoSecurityPlayer 非破坏性视频加密动作的不可变目录描述。
/// </summary>
/// <remarks>
/// 本类型只拥有跨插件 JSON 合同，不执行文件操作。输入 DTO、应用服务和 SECVID03 实现继续
/// 留在插件私有边界内，Workflow Studio 只需要读取描述符即可编辑和验证参数。
/// </remarks>
internal static class EncryptVideoWorkflowAction
{
    /// <summary>创建可安全交给 Host 冻结的动作描述符。</summary>
    internal static WorkflowActionDescriptor CreateDescriptor()
    {
        // Schema 使用冻结 Profile 的最小关键字集合。路径上限覆盖 Windows 长路径；标题和描述
        // 上限与 SECVID03 公开区的 Rune 约束一致，避免编辑器接受必然被业务层拒绝的常见输入。
        using var inputSchema = JsonDocument.Parse("""
            {
              "type": "object",
              "description": "加密一个本地视频并始终保留源文件。",
              "properties": {
                "inputPath": {
                  "type": "string",
                  "description": "需要读取的源视频路径。",
                  "minLength": 1,
                  "maxLength": 32767
                },
                "outputPath": {
                  "type": "string",
                  "description": "需要新建且不得覆盖的 SECVID03 输出路径。",
                  "minLength": 1,
                  "maxLength": 32767
                },
                "password": {
                  "type": "string",
                  "description": "仅在本次调用内使用的加密密码。",
                  "minLength": 6,
                  "maxLength": 1024
                },
                "publicTitle": {
                  "type": "string",
                  "description": "无需密码即可读取的可选公开标题。",
                  "maxLength": 200
                },
                "publicDescription": {
                  "type": "string",
                  "description": "无需密码即可读取的可选公开描述。",
                  "maxLength": 10000
                }
              },
              "required": ["inputPath", "outputPath", "password"],
              "additionalProperties": false
            }
            """);
        using var outputSchema = JsonDocument.Parse("""
            {
              "type": "object",
              "description": "完整提交后的非破坏性加密结果。",
              "properties": {
                "outputPath": {
                  "type": "string",
                  "description": "已成功提交的 SECVID03 文件绝对路径。",
                  "minLength": 1,
                  "maxLength": 32767
                }
              },
              "required": ["outputPath"],
              "additionalProperties": false
            }
            """);

        return new WorkflowActionDescriptor(
            VideoSecurityPlayerContributionIds.EncryptVideoAction,
            "加密视频并保留源文件",
            "使用 SECVID03 流式加密本地视频；成功或失败都不会删除源文件。",
            inputSchema.RootElement,
            outputSchema.RootElement,
            WorkflowActionRiskFlags.ReadsLocalFiles |
            WorkflowActionRiskFlags.WritesLocalFiles |
            WorkflowActionRiskFlags.HandlesSecret |
            WorkflowActionRiskFlags.LongRunning,
            WorkflowActionConfirmationPolicy.OncePerRun,
            ["/password"]);
    }
}

/// <summary>
/// 把受 Host 治理的一次 JSON 调用适配到现有单文件加密应用服务。
/// </summary>
/// <remarks>
/// Handler 由 Host 按调用创建 scoped 实例，只做参数映射和进度翻译。预检、密码学、原子输出、
/// 冲突处理和失败分类仍由 <see cref="IVideoEncryptionService"/> 及其下层实现负责。
/// </remarks>
internal sealed class EncryptVideoWorkflowActionHandler(
    IVideoEncryptionService encryptionService) : IWorkflowActionHandler
{
    private readonly IVideoEncryptionService _encryptionService =
        encryptionService ?? throw new ArgumentNullException(nameof(encryptionService));

    /// <inheritdoc />
    public async ValueTask<JsonElement> InvokeAsync(
        JsonElement arguments,
        WorkflowActionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        // 正常调用在进入 Handler 前已经由 Host 按同一 Descriptor 校验。这里仍拒绝无法反序列化
        // 的输入，保证直接单元测试或未来替代执行器不会把 null 传给业务服务；错误信息只描述
        // 合同，不拼接 JSON 正文或密码值。
        var input = arguments.Deserialize<EncryptVideoArguments>() ??
                    throw new ArgumentException("加密动作参数无法解析。", nameof(arguments));
        ArgumentException.ThrowIfNullOrWhiteSpace(input.InputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.OutputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Password);

        var request = new VideoEncryptionRequest(
            input.InputPath,
            input.OutputPath,
            input.PublicTitle ?? string.Empty,
            input.PublicDescription ?? string.Empty);
        var progress = new WorkflowProgressAdapter(context.Progress);

        // 密码只存在于本次调用栈和既有加密服务的参数中，不保存为 Handler 字段、结果字段或
        // 进度消息。业务异常有意原样越过本层，最终由 Host 投影为稳定、脱敏的 Action 失败。
        await _encryptionService
            .EncryptAsync(request, input.Password, progress, cancellationToken)
            .ConfigureAwait(false);

        return JsonSerializer.SerializeToElement(new EncryptVideoResult(
            Path.GetFullPath(input.OutputPath)));
    }

    /// <summary>插件私有输入模型；不会进入公共 SDK 或工作流定义类型系统。</summary>
    /// <remarks>
    /// 刻意不用 record：record 的默认 ToString 会展开全部属性，调试器或未来日志一旦误用就可能
    /// 带出密码。本类只在反序列化后的短调用栈内存活，并把字符串表示固定为不含值的类型名。
    /// </remarks>
    private sealed class EncryptVideoArguments(
        string inputPath,
        string outputPath,
        string password,
        string? publicTitle,
        string? publicDescription)
    {
        [JsonPropertyName("inputPath")]
        public string InputPath { get; } = inputPath;

        [JsonPropertyName("outputPath")]
        public string OutputPath { get; } = outputPath;

        [JsonPropertyName("password")]
        public string Password { get; } = password;

        [JsonPropertyName("publicTitle")]
        public string? PublicTitle { get; } = publicTitle;

        [JsonPropertyName("publicDescription")]
        public string? PublicDescription { get; } = publicDescription;

        public override string ToString() => nameof(EncryptVideoArguments);
    }

    /// <summary>成功结果刻意只返回正式输出路径，不回显源路径或敏感输入。</summary>
    private sealed record EncryptVideoResult(
        [property: JsonPropertyName("outputPath")] string OutputPath);

    /// <summary>把插件私有字节进度转换为受 Host 限流的公共进度快照。</summary>
    private sealed class WorkflowProgressAdapter(IProgress<WorkflowActionProgress> target)
        : IProgress<VideoTaskProgress>
    {
        private readonly IProgress<WorkflowActionProgress> _target =
            target ?? throw new ArgumentNullException(nameof(target));

        public void Report(VideoTaskProgress value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var stage = value.State switch
            {
                VideoTaskState.Pending => "pending",
                VideoTaskState.Preflighting => "preflighting",
                VideoTaskState.Ready => "ready",
                VideoTaskState.Running => "encrypting",
                VideoTaskState.Succeeded => "succeeded",
                VideoTaskState.Failed => "failed",
                VideoTaskState.Cancelled => "cancelled",
                _ => "unknown",
            };
            int? percent = double.IsFinite(value.Percentage)
                ? Math.Clamp(
                    (int)Math.Round(value.Percentage, MidpointRounding.AwayFromZero),
                    0,
                    100)
                : null;
            var message = value.State switch
            {
                VideoTaskState.Ready => "预检通过，准备加密。",
                VideoTaskState.Running => "正在加密视频。",
                VideoTaskState.Succeeded => "加密文件已完整提交。",
                VideoTaskState.Cancelled => "加密已取消。",
                VideoTaskState.Failed => "加密未完成。",
                _ => "正在处理加密任务。",
            };
            _target.Report(new WorkflowActionProgress(stage, percent, message));
        }
    }
}
