using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Settings;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;

namespace VideoSecurityPlayer.Features.Settings;

/// <summary>运行库草稿、检测结果、已保存选择和实际绑定各自展示，检测不加载原生库。</summary>
public sealed partial class RuntimeSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IVideoSettingsReader _reader;
    private readonly IVideoSettingsEditor _editor;
    private readonly IPlaybackRuntimeResolver _resolver;
    private readonly IPlaybackRuntimeStatus _runtime;
    private readonly IEnvironmentDiagnostics _diagnostics;
    private readonly CapturedUiScheduler _ui = new();
    private readonly CancellationTokenSource _lifetime = new();
    private long _draftRevision;
    private bool _disposed;
    [ObservableProperty] private bool _automatic;
    [ObservableProperty] private string _directoryPath;
    [ObservableProperty] private string _checkMessage = "检测只检查文件；实际播放能力在加载后确认。";
    [ObservableProperty] private string _exportMessage = "";
    public SettingsSaveFeedback Save { get; } = new();
    public string SavedSelection => _reader.CurrentVideoSettings.Runtime is { Mode: RuntimeSourceMode.Custom } selection
        ? "已保存：指定目录 · " + selection.Directory : "已保存：自动选择（Common → 内置）";
    public string ActualState => _runtime.Snapshot is { LoadAttempted: true } value
        ? $"{(value.Status == RuntimeLoadStatus.Ready ? "已加载" : value.Status == RuntimeLoadStatus.Loading ? "正在绑定" : "加载失败")} · {SourceLabel(value.Source)}\n{value.Directory}\n版本：{value.Version}\n本进程目录已固定，变更配置需重启。"
        : "尚未尝试加载；下次初始化使用已保存配置。";
    public string Summary => _runtime.Snapshot is { LoadAttempted: true } state
        ? $"{(state.Status == RuntimeLoadStatus.Ready ? "播放组件已加载" : "播放组件需要检查")} · {SourceLabel(state.Source)}"
        : "播放组件尚未加载";

    public RuntimeSettingsViewModel(IVideoSettingsReader reader, IVideoSettingsEditor editor, IPlaybackRuntimeResolver resolver,
        IPlaybackRuntimeStatus runtime, IEnvironmentDiagnostics diagnostics)
    {
        (_reader, _editor, _resolver, _runtime, _diagnostics) = (reader, editor, resolver, runtime, diagnostics);
        var selection = reader.CurrentVideoSettings.Runtime;
        _automatic = selection.Mode == RuntimeSourceMode.Auto;
        _directoryPath = selection.Directory;
        _runtime.Changed += Refresh;
        _reader.SettingsChanged += Refresh;
    }
    private RuntimeSelection Draft => new(Automatic ? RuntimeSourceMode.Auto : RuntimeSourceMode.Custom, DirectoryPath.Trim());
    partial void OnAutomaticChanged(bool value) => Edited();
    partial void OnDirectoryPathChanged(string value) => Edited();
    private void Edited()
    {
        Interlocked.Increment(ref _draftRevision);
        CheckMessage = "草稿已修改，请重新检测；尚未保存。";
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task CheckAsync()
    {
        var revision = Interlocked.Read(ref _draftRevision);
        var selection = Draft;
        CheckMessage = "正在检查目录…";
        try
        {
            var result = await Task.Run(() => _resolver.Resolve(selection), _lifetime.Token);
            if (!_disposed && revision == Interlocked.Read(ref _draftRevision)) CheckMessage = Describe(result);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { if (!_disposed && revision == Interlocked.Read(ref _draftRevision)) CheckMessage = "检查失败，请确认目录可读后重试。"; }
    }

    [RelayCommand]
    private Task SaveConfigurationAsync()
    {
        var selection = Draft;
        var revision = Interlocked.Read(ref _draftRevision);
        return Save.RunAsync(async token =>
        {
            // 保存前复检；早先的成功检查不能证明目录在保存时仍有效。
            var check = await Task.Run(() => _resolver.Resolve(selection), token).ConfigureAwait(false);
            if (revision != Interlocked.Read(ref _draftRevision)) return new(false, "草稿已变化，请重新检测并保存。");
            if (!check.IsReady) return new(false, Describe(check));
            var result = await _editor.SaveRuntimeAsync(selection, token).ConfigureAwait(false);
            if (!result.Success) return result;
            if (revision != Interlocked.Read(ref _draftRevision)) return new(true, "此前选择已保存；当前草稿尚未保存。");
            return new(true, _runtime.Snapshot.LoadAttempted ? "配置已保存，重启应用后生效。" : "配置已保存，下次初始化时使用。");
        }, false);
    }

    public Task<byte[]> CreateDiagnosticAsync(CancellationToken token = default) => _diagnostics.CreateAsync(token);
    public void ReportExport(bool success) => ExportMessage = success ? "已导出脱敏环境诊断" : "导出失败，请检查目标位置后重试";
    private static string Describe(PlaybackRuntimeResolution result) => result.IsReady
        ? $"文件预检通过 · {SourceLabel(result.Source)}\n{result.Directory}\n{result.Note}\n尚未验证真实播放。"
        : string.Join("\n", result.Check.Issues.Select(i => i.Summary)) + "\n" + result.Note;
    private static string SourceLabel(PlaybackRuntimeSource? source) => source switch
    { PlaybackRuntimeSource.Common => "Common", PlaybackRuntimeSource.Bundled => "内置", PlaybackRuntimeSource.Custom => "指定目录", _ => "未选择" };
    private void Refresh(object? sender, EventArgs e) => _ui.Post(() =>
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(SavedSelection)); OnPropertyChanged(nameof(ActualState)); OnPropertyChanged(nameof(Summary));
    });
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel();
        _runtime.Changed -= Refresh; _reader.SettingsChanged -= Refresh; Save.Dispose();
    }
}
