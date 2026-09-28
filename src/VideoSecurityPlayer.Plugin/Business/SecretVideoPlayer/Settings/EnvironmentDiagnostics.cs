using System.Runtime.InteropServices;
using System.Text.Json;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Settings;

/// <summary>不依赖当前文档的环境诊断；原有媒体诊断仍由各播放器独立导出。</summary>
public interface IEnvironmentDiagnostics
{
    Task<byte[]> CreateAsync(CancellationToken token = default);
}

/// <summary>只导出平台、来源、状态和稳定问题码，绝对路径、历史与媒体内容不进入摘要。</summary>
internal sealed class EnvironmentDiagnostics(IVideoSettingsReader settings, IPlaybackRuntimeResolver resolver,
    IPlaybackRuntimeStatus runtime) : IEnvironmentDiagnostics
{
    public Task<byte[]> CreateAsync(CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var selection = settings.CurrentVideoSettings.Runtime;
        var candidate = resolver.Resolve(selection);
        var actual = runtime.Snapshot;
        token.ThrowIfCancellationRequested();
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            pluginVersion = typeof(EnvironmentDiagnostics).Assembly.GetName().Version?.ToString(),
            platform = OperatingSystem.IsWindows() ? "windows" : "unsupported",
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            savedMode = selection.Mode.ToString(),
            candidateSource = candidate.Source.ToString(), candidateReady = candidate.IsReady,
            issues = candidate.Check.Issues.Select(i => i.Code.ToString()).ToArray(),
            actualState = actual.Status.ToString(), actualSource = actual.Source?.ToString(), actualVersion = actual.Version
        }, new JsonSerializerOptions { WriteIndented = true });
    }, token);
}
