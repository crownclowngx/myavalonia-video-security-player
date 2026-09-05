using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LibVLCSharp.Shared;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

/// <summary>当前 Document 向诊断导出器提供的只读、无路径状态。</summary>
internal interface IPlaybackDiagnosticState
{
    PlaybackDiagnosticState CaptureDiagnosticState();
}

/// <summary>诊断导出器的唯一产品端口。</summary>
internal interface IPlaybackDiagnosticExporter
{
    Task<ReadOnlyMemory<byte>> CreateJsonAsync(
        PlaybackFailure? lastFailure,
        CancellationToken cancellationToken = default);
}

internal sealed record PlaybackDiagnosticState(
    PlaybackSnapshot Playback,
    Secvid03DiagnosticSummary? Container);

/// <summary>
/// 创建默认脱敏的播放器诊断 JSON。
/// </summary>
/// <remarks>
/// 导出器采用显式 DTO 白名单，不接收媒体路径，也不序列化异常、ViewModel 或日志。
/// 保存位置属于 View 的职责，因此本服务既不会访问也不会记录用户选择的输出路径。
/// </remarks>
internal sealed class PlaybackDiagnosticExporter(
    IPlaybackDiagnosticState diagnosticState,
    IPlaybackPlatformStatus platformStatus,
    IPlaybackRuntimeLayoutProvider runtimeLayoutProvider) : IPlaybackDiagnosticExporter
{
    internal const int MaximumJsonBytes = 64 * 1024;
    private const string UnavailableVersion = "unavailable";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly IPlaybackDiagnosticState _diagnosticState =
        diagnosticState ?? throw new ArgumentNullException(nameof(diagnosticState));
    private readonly IPlaybackPlatformStatus _platformStatus =
        platformStatus ?? throw new ArgumentNullException(nameof(platformStatus));
    private readonly IPlaybackRuntimeLayoutProvider _runtimeLayoutProvider =
        runtimeLayoutProvider ?? throw new ArgumentNullException(nameof(runtimeLayoutProvider));

    public Task<ReadOnlyMemory<byte>> CreateJsonAsync(
        PlaybackFailure? lastFailure,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var state = _diagnosticState.CaptureDiagnosticState();
        var capabilities = _platformStatus.Capabilities;
        var layout = _runtimeLayoutProvider.Resolve();
        var deployment = CaptureDeployment(layout);
        var resources = SecurePlaybackDiagnostics.CaptureResources();
        using var process = Process.GetCurrentProcess();
        process.Refresh();

        var report = new DiagnosticReport(
            1,
            "videosecurityplayer-playback-diagnostics",
            DateTimeOffset.UtcNow,
            "default-redacted-v1",
            new PlatformReport(
                capabilities.PlatformId,
                RuntimeInformation.ProcessArchitecture.ToString(),
                RuntimeInformation.OSDescription,
                capabilities.IsSupported,
                capabilities.SupportsNativeVideoOutput,
                capabilities.SupportsEmbeddedFullscreen,
                capabilities.SupportsAudioTrackSelection,
                capabilities.SupportsSubtitleTrackSelection,
                capabilities.UsesBundledRuntime),
            CaptureVersions(layout),
            deployment,
            CapturePlayback(state.Playback, lastFailure, capabilities, deployment.IsReady),
            state.Container is null ? null : ToReport(state.Container),
            ResolveContainerUnavailableReason(
                state.Container,
                capabilities.IsSupported,
                deployment.IsReady),
            new ResourceReport(
                resources.LiveLeases,
                resources.LivePlayers,
                resources.LiveMediaInputs,
                resources.LiveEncryptedStreams,
                resources.ActiveSurfaceRestores,
                resources.CachedPlaintextChunks,
                resources.LiveNativeDispatchers,
                resources.LiveResourceReapers,
                GC.GetGCMemoryInfo().HeapSizeBytes,
                process.WorkingSet64,
                process.PrivateMemorySize64,
                SafeHandleCount(process),
                SafeThreadCount(process),
                ThreadPool.PendingWorkItemCount,
                GCSettings.IsServerGC,
                GCSettings.LatencyMode.ToString()),
            new PrivacyReport(
                [
                    "password",
                    "derived-key",
                    "file-id",
                    "media-path",
                    "output-path",
                    "complete-file-name",
                    "public-title",
                    "public-description",
                    "media-content",
                    "native-stderr",
                    "application-log",
                    "chunk-index-trace"
                ]));

        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
        if (bytes.Length > MaximumJsonBytes)
            throw new InvalidOperationException("脱敏诊断超过允许的大小上限。");

        // 统一 LF，保证用户导出、自动化扫描和提交证据使用相同的确定性文本格式。
        var normalized = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(bytes).ReplaceLineEndings("\n") + "\n");
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ReadOnlyMemory<byte>>(normalized);
    }

    private DeploymentReport CaptureDeployment(PlaybackRuntimeLayout layout)
    {
        try
        {
            var result = _platformStatus.Check();
            var runtimeLocation = DiagnosticPathPolicy.FormatRuntimeLocation(
                layout.PluginDirectory,
                result.RuntimeDirectory);
            if (runtimeLocation == "outside-plugin")
            {
                // 私有运行时逃逸插件根目录属于稳定部署错误；既不继续信任就绪结果，
                // 也不输出外部目录或叶子文件名。
                return new DeploymentReport(
                    false,
                    runtimeLocation,
                    [new DeploymentIssueReport("RUNTIME_OUTSIDE_PLUGIN", null)]);
            }
            return new DeploymentReport(
                result.IsReady,
                runtimeLocation,
                result.Issues.Select(issue => new DeploymentIssueReport(
                        issue.Code.ToString(),
                        DiagnosticPathPolicy.FormatRuntimeLocation(
                            layout.PluginDirectory,
                            issue.CheckedPath)))
                    .ToArray());
        }
        catch
        {
            // 探针自身失败时仍要能导出稳定诊断，不能把可能含路径的异常文本写出。
            return new DeploymentReport(
                false,
                DiagnosticPathPolicy.FormatRuntimeLocation(
                    layout.PluginDirectory,
                    layout.RuntimeDirectory),
                [new DeploymentIssueReport("DIAGNOSTIC_PROBE_FAILED", null)]);
        }
    }

    private static VersionReport CaptureVersions(PlaybackRuntimeLayout layout) => new(
        ReadAssemblyVersion(typeof(PlaybackDiagnosticExporter).Assembly),
        RuntimeInformation.FrameworkDescription,
        ReadAssemblyVersion(typeof(Core).Assembly),
        DiagnosticPathPolicy.FormatRuntimeLocation(
            layout.PluginDirectory,
            layout.RuntimeDirectory) == "outside-plugin"
                ? UnavailableVersion
                : ReadFileVersion(Path.Combine(layout.RuntimeDirectory, "libvlc.dll")));

    private static string ReadAssemblyVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? assembly.GetName().Version?.ToString()
        ?? UnavailableVersion;

    private static string ReadFileVersion(string path)
    {
        try
        {
            return File.Exists(path)
                ? FileVersionInfo.GetVersionInfo(path).FileVersion ?? UnavailableVersion
                : UnavailableVersion;
        }
        catch
        {
            return UnavailableVersion;
        }
    }

    private static PlaybackReport CapturePlayback(
        PlaybackSnapshot snapshot,
        PlaybackFailure? failure,
        PlaybackPlatformCapabilities capabilities,
        bool deploymentReady) => new(
        snapshot.State.ToString(),
        snapshot.Activity.ToString(),
        snapshot.HasMedia,
        snapshot.IsSeekable,
        snapshot.SurfaceGeneration > 0,
        ResolveFailureDomain(failure?.Code, capabilities.IsSupported, deploymentReady),
        failure?.Code.ToString(),
        SanitizeDiagnosticCode(failure?.DiagnosticCode));

    private static string ResolveFailureDomain(
        PlaybackFailureCode? code,
        bool platformSupported,
        bool deploymentReady)
    {
        if (!platformSupported)
            return "platform";
        if (!deploymentReady || code == PlaybackFailureCode.DeploymentUnavailable)
            return "deployment";

        return code switch
        {
            PlaybackFailureCode.InvalidFormat or PlaybackFailureCode.CorruptedContent => "format",
            PlaybackFailureCode.AuthenticationFailed => "authentication",
            PlaybackFailureCode.InputUnavailable => "io",
            PlaybackFailureCode.ParseFailed or
                PlaybackFailureCode.DecodeFailed or
                PlaybackFailureCode.SurfaceRestoreFailed => "decode",
            PlaybackFailureCode.InvalidRequest or
                PlaybackFailureCode.ControlUnavailable or
                PlaybackFailureCode.Cancelled => "operation",
            null => "none",
            _ => "unknown"
        };
    }

    private static string? ResolveContainerUnavailableReason(
        Secvid03DiagnosticSummary? summary,
        bool platformSupported,
        bool deploymentReady)
    {
        if (summary is not null)
            return null;
        if (!platformSupported)
            return "platform-unsupported";
        return deploymentReady
            ? "no-authenticated-active-container"
            : "deployment-unavailable";
    }

    private static string? SanitizeDiagnosticCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
            return null;

        foreach (var character in value)
        {
            if (!(character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'))
                return null;
        }
        return value;
    }

    private static ContainerReport ToReport(Secvid03DiagnosticSummary summary) => new(
        summary.Format,
        summary.Version,
        summary.OriginalHeaderLength,
        summary.OriginalFileLength,
        summary.ChunkSize,
        summary.ChunkCount,
        summary.TagSize,
        summary.Kdf,
        summary.KdfIterations);

    private static int SafeHandleCount(Process process)
    {
        try
        {
            return process.HandleCount;
        }
        catch
        {
            return -1;
        }
    }

    private static int SafeThreadCount(Process process)
    {
        try
        {
            return process.Threads.Count;
        }
        catch
        {
            return -1;
        }
    }

    private sealed record DiagnosticReport(
        int SchemaVersion,
        string Kind,
        DateTimeOffset CreatedUtc,
        string RedactionProfile,
        PlatformReport Platform,
        VersionReport Versions,
        DeploymentReport Deployment,
        PlaybackReport Playback,
        ContainerReport? Container,
        string? ContainerUnavailableReason,
        ResourceReport Resources,
        PrivacyReport Privacy);

    private sealed record PlatformReport(
        string PlatformId,
        string ProcessArchitecture,
        string OperatingSystem,
        bool IsSupported,
        bool SupportsNativeVideoOutput,
        bool SupportsEmbeddedFullscreen,
        bool SupportsAudioTrackSelection,
        bool SupportsSubtitleTrackSelection,
        bool UsesBundledRuntime);

    private sealed record VersionReport(
        string VideoSecurityPlayer,
        string DotNet,
        string LibVlcSharp,
        string LibVlc);

    private sealed record DeploymentReport(
        bool IsReady,
        string? RuntimeLocation,
        IReadOnlyList<DeploymentIssueReport> Issues);

    private sealed record DeploymentIssueReport(string Code, string? Location);

    private sealed record PlaybackReport(
        string State,
        string Activity,
        bool HasMedia,
        bool IsSeekable,
        bool IsSurfaceAttached,
        string FailureDomain,
        string? FailureCode,
        string? DiagnosticCode);

    private sealed record ContainerReport(
        string Format,
        int Version,
        int OriginalHeaderLength,
        long OriginalFileLength,
        int ChunkSize,
        long ChunkCount,
        int TagSize,
        string Kdf,
        int KdfIterations);

    private sealed record ResourceReport(
        int LiveLeases,
        int LivePlayers,
        int LiveMediaInputs,
        int LiveEncryptedStreams,
        int ActiveSurfaceRestores,
        int CachedPlaintextChunks,
        int LiveNativeDispatchers,
        int LiveResourceReapers,
        long ManagedHeapBytes,
        long WorkingSetBytes,
        long PrivateBytes,
        int HandleCount,
        int ThreadCount,
        long PendingThreadPoolItems,
        bool IsServerGc,
        string GcLatencyMode);

    private sealed record PrivacyReport(IReadOnlyList<string> Omitted);
}

/// <summary>把真实运行时路径限制为插件锚点下的脱敏相对位置。</summary>
internal static class DiagnosticPathPolicy
{
    public static string? FormatRuntimeLocation(string pluginDirectory, string path)
    {
        if (string.IsNullOrWhiteSpace(pluginDirectory) || string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var plugin = Path.GetFullPath(pluginDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var target = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(plugin, target);
            if (relative == "." || IsInside(relative))
            {
                return relative == "."
                    ? "$PLUGIN"
                    : "$PLUGIN/" + relative.Replace('\\', '/');
            }
        }
        catch
        {
            return null;
        }

        return "outside-plugin";
    }

    private static bool IsInside(string relative) =>
        !Path.IsPathRooted(relative) &&
        !relative.Equals("..", StringComparison.Ordinal) &&
        !relative.StartsWith(
            ".." + Path.DirectorySeparatorChar,
            StringComparison.Ordinal) &&
        !relative.StartsWith(
            ".." + Path.AltDirectorySeparatorChar,
            StringComparison.Ordinal);
}
