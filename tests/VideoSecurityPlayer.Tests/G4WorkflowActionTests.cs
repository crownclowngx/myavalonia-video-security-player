using System.Security.Cryptography;
using System.Text.Json;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.Workflow;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Encryption;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Workflow;
using VideoSecurityPlayer.Constants;
using Xunit;

namespace VideoSecurityPlayer.Tests;

/// <summary>
/// G4 非破坏性加密 Action 的合同、适配和真实文件安全门禁。
/// </summary>
/// <remarks>
/// Host 的授权、预算和 invocation scope 已由 G1 内核测试覆盖；这里专注证明 VideoSecurityPlayer
/// Descriptor 与现有加密用例的连接没有改变源文件所有权或泄漏调用时密码。
/// </remarks>
public sealed class G4WorkflowActionTests
{
    private const string PasswordCanary = "G4-SECRET-CANARY-123456";

    [Fact]
    public void Descriptor具有精确身份风险敏感指针和共享Schema合同()
    {
        var descriptor = EncryptVideoWorkflowAction.CreateDescriptor();
        var validator = new WorkflowSchemaValidator();

        Assert.Equal(VideoSecurityPlayerContributionIds.EncryptVideoAction, descriptor.Id);
        Assert.Equal("加密视频并保留源文件", descriptor.DisplayName);
        Assert.Equal(
            WorkflowActionRiskFlags.ReadsLocalFiles |
            WorkflowActionRiskFlags.WritesLocalFiles |
            WorkflowActionRiskFlags.HandlesSecret |
            WorkflowActionRiskFlags.LongRunning,
            descriptor.Risks);
        Assert.Equal(WorkflowActionConfirmationPolicy.OncePerRun, descriptor.ConfirmationPolicy);
        Assert.Equal(["/password"], descriptor.SensitiveInputPointers);
        Assert.True(validator.ValidateDescriptor(descriptor).IsValid);

        var properties = descriptor.InputSchema.GetProperty("properties");
        Assert.Equal(32767, properties.GetProperty("inputPath").GetProperty("maxLength").GetInt32());
        Assert.Equal(32767, properties.GetProperty("outputPath").GetProperty("maxLength").GetInt32());
        Assert.Equal(6, properties.GetProperty("password").GetProperty("minLength").GetInt32());
        Assert.Equal(1024, properties.GetProperty("password").GetProperty("maxLength").GetInt32());
        Assert.Equal(200, properties.GetProperty("publicTitle").GetProperty("maxLength").GetInt32());
        Assert.Equal(10000, properties.GetProperty("publicDescription").GetProperty("maxLength").GetInt32());
        Assert.Equal(
            ["inputPath", "outputPath", "password"],
            descriptor.InputSchema.GetProperty("required")
                .EnumerateArray().Select(item => item.GetString()!).ToArray());
    }

    [Fact]
    public void Schema拒绝缺参额外字段与所有字符串越界()
    {
        var descriptor = EncryptVideoWorkflowAction.CreateDescriptor();
        var validator = new WorkflowSchemaValidator();

        AssertValid(validator, descriptor, new
        {
            inputPath = "input.mp4",
            outputPath = "output.secvid",
            password = "123456",
        });
        AssertValid(validator, descriptor, new
        {
            inputPath = "input.mp4",
            outputPath = "output.secvid",
            password = new string('密', 1024),
            publicTitle = string.Concat(Enumerable.Repeat("😀", 200)),
            publicDescription = string.Concat(Enumerable.Repeat("文", 10000)),
        });

        AssertInvalid(validator, descriptor, new { inputPath = "input.mp4", outputPath = "out" });
        AssertInvalid(validator, descriptor, new
        {
            inputPath = "input.mp4",
            outputPath = "out",
            password = "123456",
            extra = true,
        });
        AssertInvalid(validator, descriptor, new
        {
            inputPath = new string('a', 32768),
            outputPath = "out",
            password = "123456",
        });
        AssertInvalid(validator, descriptor, new
        {
            inputPath = "input",
            outputPath = "out",
            password = "12345",
        });
        AssertInvalid(validator, descriptor, new
        {
            inputPath = "input",
            outputPath = "out",
            password = new string('x', 1025),
        });
        AssertInvalid(validator, descriptor, new
        {
            inputPath = "input",
            outputPath = "out",
            password = "123456",
            publicTitle = new string('题', 201),
        });
        AssertInvalid(validator, descriptor, new
        {
            inputPath = "input",
            outputPath = "out",
            password = "123456",
            publicDescription = new string('文', 10001),
        });
    }

    [Fact]
    public async Task Handler映射可选元数据规范输出并只报告脱敏进度()
    {
        var service = new RecordingEncryptionService();
        var progress = new RecordingProgress();
        var handler = new EncryptVideoWorkflowActionHandler(service);
        var relativeOutput = Path.Combine("g4-relative", "result.secvid");
        var arguments = JsonSerializer.SerializeToElement(new
        {
            inputPath = "source.mp4",
            outputPath = relativeOutput,
            password = PasswordCanary,
        });

        var output = await handler.InvokeAsync(
            arguments,
            Context(progress),
            CancellationToken.None);

        Assert.NotNull(service.Request);
        Assert.Equal("source.mp4", service.Request.InputPath);
        Assert.Equal(relativeOutput, service.Request.OutputPath);
        Assert.Empty(service.Request.PublicTitle);
        Assert.Empty(service.Request.PublicDescription);
        Assert.Equal(PasswordCanary, service.Password);
        Assert.Equal(Path.GetFullPath(relativeOutput), output.GetProperty("outputPath").GetString());
        Assert.Equal(["outputPath"], output.EnumerateObject().Select(item => item.Name).ToArray());
        Assert.Equal(["ready", "encrypting", "succeeded"],
            progress.Items.Select(item => item.Stage).ToArray());
        Assert.Equal([0, 51, 100], progress.Items.Select(item => item.Percent).ToArray());
        Assert.DoesNotContain(PasswordCanary, output.GetRawText(), StringComparison.Ordinal);
        Assert.All(progress.Items, item =>
            Assert.DoesNotContain(PasswordCanary, item.Message ?? string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(EncryptVideoWorkflowActionHandler).GetFields(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public),
            field => field.Name.Contains("password", StringComparison.OrdinalIgnoreCase));

        var argumentsType = typeof(EncryptVideoWorkflowActionHandler).GetNestedTypes(
                System.Reflection.BindingFlags.NonPublic)
            .Single(type => type.Name == "EncryptVideoArguments");
        var privateArguments = Activator.CreateInstance(
            argumentsType,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: ["source.mp4", relativeOutput, PasswordCanary, null, null],
            culture: null);
        Assert.NotNull(privateArguments);
        Assert.DoesNotContain(
            PasswordCanary,
            privateArguments.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handler完整传递元数据取消与业务失败而不伪装成功()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelledService = new RecordingEncryptionService();
        var cancelledHandler = new EncryptVideoWorkflowActionHandler(cancelledService);
        var arguments = Arguments("source.mp4", "result.secvid", "公开标题", "公开描述");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await cancelledHandler.InvokeAsync(arguments, Context(), cancellation.Token));
        Assert.Null(cancelledService.Request);

        var expected = new VideoTaskException(
            VideoTaskFailureCode.OutputConflict,
            "输出冲突且不包含敏感输入。");
        var failing = new EncryptVideoWorkflowActionHandler(
            new ThrowingEncryptionService(expected));
        var actual = await Assert.ThrowsAsync<VideoTaskException>(async () =>
            await failing.InvokeAsync(arguments, Context(), CancellationToken.None));

        Assert.Same(expected, actual);
        Assert.DoesNotContain(PasswordCanary, actual.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 真实媒体成功认证且重复调用不覆盖输出并始终保留源文件()
    {
        var root = Path.Combine(Path.GetTempPath(), $"workflow-action-g4-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "synthetic-av-short.mp4");
        var output = Path.Combine(root, "synthetic-av-short.secvid");
        var movedOutput = Path.Combine(root, "moved.secvid");
        try
        {
            var asset = Path.Combine(
                AppContext.BaseDirectory,
                "TestAssets",
                "RealMedia",
                "synthetic-av-short.mp4");
            File.Copy(asset, source);
            var sourceBytes = await File.ReadAllBytesAsync(source);
            var sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes));
            var handler = new EncryptVideoWorkflowActionHandler(
                new VideoEncryptorService(new Secvid03Encryptor()));

            var first = await handler.InvokeAsync(
                Arguments(source, output, "G4 标题", "G4 公开描述"),
                Context(),
                CancellationToken.None);

            Assert.Equal(Path.GetFullPath(output), first.GetProperty("outputPath").GetString());
            Assert.True(File.Exists(source));
            Assert.Equal(sourceHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))));
            Assert.True(File.Exists(output));
            Assert.Empty(Directory.GetFiles(root, "*.partial-*"));
            using (var decrypted = SeekableEncryptedVideoStream.Open(output, PasswordCanary))
            {
                using var plaintext = new MemoryStream();
                await decrypted.CopyToAsync(plaintext);
                Assert.Equal(sourceBytes, plaintext.ToArray());
            }

            var outputHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(output)));
            var conflict = await Assert.ThrowsAsync<VideoTaskException>(async () =>
                await handler.InvokeAsync(
                    Arguments(source, output, "G4 标题", "G4 公开描述"),
                    Context(),
                    CancellationToken.None));

            Assert.Equal(VideoTaskFailureCode.OutputConflict, conflict.FailureCode);
            Assert.Equal(sourceHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))));
            Assert.Equal(outputHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(output))));
            Assert.Empty(Directory.GetFiles(root, "*.partial-*"));

            // 成功与冲突调用都结束后应释放文件句柄；移动成功是对真实插件资源关闭的直接证明。
            File.Move(output, movedOutput);
            File.Move(movedOutput, output);
            File.Move(source, source + ".moved");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static JsonElement Arguments(
        string inputPath,
        string outputPath,
        string? publicTitle = null,
        string? publicDescription = null) => JsonSerializer.SerializeToElement(new
        {
            inputPath,
            outputPath,
            password = PasswordCanary,
            publicTitle,
            publicDescription,
        });

    private static WorkflowActionContext Context(RecordingProgress? progress = null) => new(
        Guid.NewGuid(),
        new PluginId("myavalonia.plugin.workflow-studio"),
        progress ?? new RecordingProgress());

    private static void AssertValid(
        WorkflowSchemaValidator validator,
        WorkflowActionDescriptor descriptor,
        object value) => Assert.True(validator.ValidateInstance(
        descriptor.InputSchema,
        JsonSerializer.SerializeToElement(value),
        WorkflowSchemaProfile.MaximumInputBytes).IsValid);

    private static void AssertInvalid(
        WorkflowSchemaValidator validator,
        WorkflowActionDescriptor descriptor,
        object value) => Assert.False(validator.ValidateInstance(
        descriptor.InputSchema,
        JsonSerializer.SerializeToElement(value),
        WorkflowSchemaProfile.MaximumInputBytes).IsValid);

    private sealed class RecordingProgress : IProgress<WorkflowActionProgress>
    {
        internal List<WorkflowActionProgress> Items { get; } = [];
        public void Report(WorkflowActionProgress value) => Items.Add(value);
    }

    private sealed class RecordingEncryptionService : IVideoEncryptionService
    {
        internal VideoEncryptionRequest? Request { get; private set; }
        internal string? Password { get; private set; }

        public Task<VideoPreflightResult> PreflightAsync(
            VideoEncryptionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(VideoPreflightResult.Ready(1, 1));

        public Task EncryptAsync(
            VideoEncryptionRequest request,
            string password,
            IProgress<VideoTaskProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            Password = password;
            progress?.Report(new VideoTaskProgress(VideoTaskState.Ready, 0, 10, 0, "ready"));
            progress?.Report(new VideoTaskProgress(VideoTaskState.Running, 5, 10, 50.5, "running"));
            progress?.Report(new VideoTaskProgress(VideoTaskState.Succeeded, 10, 10, 100, "done"));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingEncryptionService(Exception exception) : IVideoEncryptionService
    {
        public Task<VideoPreflightResult> PreflightAsync(
            VideoEncryptionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(VideoPreflightResult.Ready(1, 1));

        public Task EncryptAsync(
            VideoEncryptionRequest request,
            string password,
            IProgress<VideoTaskProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException(exception);
    }
}
