using VideoSecurityPlayer.Business.SecretVideoPlayer.Settings;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;

/// <summary>来源与可用性分开表达，Common 不是插件自带运行库。</summary>
public enum PlaybackRuntimeSource { Common, Bundled, Custom }
public sealed record PlaybackRuntimeCandidate(PlaybackRuntimeSource Source, string Directory);
public sealed record PlaybackRuntimeResolution(PlaybackRuntimeSource Source, DeploymentCheckResult Check, string Note)
{
    public string Directory => Check.RuntimeDirectory;
    public bool IsReady => Check.IsReady;
}

/// <summary>设置只调用无副作用解析，不通过播放器的部署检查去创建原生后端。</summary>
public interface IPlaybackRuntimeResolver
{
    PlaybackRuntimeResolution Resolve(RuntimeSelection? selection = null);
}

/// <summary>明确的两种选择策略，无 PATH 搜索、动态策略注册或目录拼接副作用。</summary>
public static class RuntimeSelectionPolicy
{
    public static IReadOnlyList<PlaybackRuntimeCandidate> Candidates(RuntimeSelection selection, string common, string bundled) =>
        selection.Mode == RuntimeSourceMode.Custom
            ? [new(PlaybackRuntimeSource.Custom, selection.Directory)]
            : [new(PlaybackRuntimeSource.Common, common), new(PlaybackRuntimeSource.Bundled, bundled)];
}

/// <summary>读取已保存选择并检查候选。预检失败可以降级，加载开始之后不能降级。</summary>
internal sealed class PlaybackRuntimeResolver : IPlaybackRuntimeResolver, IPlaybackDeploymentProbe
{
    private readonly IVideoSettingsReader _settings;
    private readonly PlaybackRuntimeLayout _layout;
    private readonly string _common;
    private readonly Func<PlaybackRuntimeLayout, DeploymentCheckResult> _check;

    public PlaybackRuntimeResolver(IVideoSettingsReader settings, IPlaybackRuntimeLayoutProvider layout, PlaybackDeploymentProbe probe)
        : this(settings, layout.Resolve(), Path.Combine(AppContext.BaseDirectory, "Common", "libvlc"),
            candidate => probe.Check(candidate, validateVersions: true)) { }

    internal PlaybackRuntimeResolver(IVideoSettingsReader settings, PlaybackRuntimeLayout layout, string common,
        Func<PlaybackRuntimeLayout, DeploymentCheckResult> check)
    { _settings = settings; _layout = layout; _common = common; _check = check; }

    public PlaybackRuntimeResolution Resolve(RuntimeSelection? selection = null)
    {
        selection ??= _settings.CurrentVideoSettings.Runtime;
        PlaybackRuntimeResolution? last = null;
        foreach (var candidate in RuntimeSelectionPolicy.Candidates(selection, _common, _layout.RuntimeDirectory))
        {
            DeploymentCheckResult result;
            if (string.IsNullOrWhiteSpace(candidate.Directory) || !Path.IsPathFullyQualified(candidate.Directory))
                result = new(_layout.PluginDirectory, candidate.Directory, [new(DeploymentIssueCode.InvalidRuntimeDirectory,
                    "请选择运行库的绝对目录。", "", "目录中应包含 libvlc.dll、libvlccore.dll 和完整 plugins。")]);
            else
            {
                try { result = _check(_layout with { RuntimeDirectory = Path.GetFullPath(candidate.Directory) }); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    result = new(_layout.PluginDirectory, candidate.Directory, [new(DeploymentIssueCode.InvalidRuntimeDirectory,
                        "运行库目录无法读取。", candidate.Directory, "检查路径和读取权限后重新检测。")]);
                }
            }
            var note = last is null ? "" : "Common 预检未通过：" + string.Join("；", last.Check.Issues.Select(i => i.Summary));
            last = new(candidate.Source, result, note);
            if (result.IsReady) return last;
        }
        return last!;
    }

    public DeploymentCheckResult Check() => Resolve().Check;
}

public enum RuntimeLoadStatus { NotAttempted, Loading, Ready, Failed }
/// <summary>实际绑定事实；与可编辑的运行库选择互相独立。</summary>
public sealed record PlaybackRuntimeSnapshot(RuntimeLoadStatus Status, string Directory = "",
    PlaybackRuntimeSource? Source = null, string Version = "")
{
    public bool LoadAttempted => Status != RuntimeLoadStatus.NotAttempted;
}

public interface IPlaybackRuntimeStatus
{
    PlaybackRuntimeSnapshot Snapshot { get; }
    event EventHandler? Changed;
}

/// <summary>加载尝试的单向状态，真实绑定失败后也不允许本进程重新选库。</summary>
/// <remarks>所有转移由运行时的初始化锁保护；不可变快照可以被设置界面无阻塞读取。</remarks>
internal sealed class RuntimeLoadState
{
    private PlaybackRuntimeSnapshot _snapshot = new(RuntimeLoadStatus.NotAttempted);
    public PlaybackRuntimeSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public void Begin(PlaybackRuntimeResolution resolution)
    {
        if (Snapshot.LoadAttempted) throw new InvalidOperationException("已经尝试绑定运行库，请重启应用。");
        Volatile.Write(ref _snapshot, new(RuntimeLoadStatus.Loading, resolution.Directory, resolution.Source));
    }
    public void Complete(bool success, string version = "")
    {
        if (Snapshot.Status != RuntimeLoadStatus.Loading) throw new InvalidOperationException("尚未开始绑定运行库。");
        Volatile.Write(ref _snapshot, Snapshot with { Status = success ? RuntimeLoadStatus.Ready : RuntimeLoadStatus.Failed, Version = version });
    }
}
