using LibVLCSharp.Shared;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using Xunit;

namespace VideoSecurityPlayer.Tests;

/// <summary>
/// G6 日常控制的纯编排测试。
/// </summary>
/// <remarks>
/// 这些用例刻意不创建真实 LibVLC。它们验证会话层是否保持状态、代次和非致命失败语义；
/// 真实轨道和 HWND 行为继续由集成门禁负责。
/// </remarks>
public sealed class G6PlaybackControlTests
{
    [Fact]
    public void 轨道名称移除控制字符限制Rune长度并提供空名称回退()
    {
        var longName = " 中\u0000文\n" + string.Concat(Enumerable.Repeat("😀", 140));

        var sanitized = PlaybackTrackNamePolicy.Sanitize(longName, "音轨", 2);
        var fallback = PlaybackTrackNamePolicy.Sanitize("\r\n\t", "字幕", 3);

        Assert.DoesNotContain(
            sanitized.EnumerateRunes(),
            rune => System.Text.Rune.IsControl(rune));
        Assert.InRange(sanitized.EnumerateRunes().Count(), 1, 128);
        Assert.Equal("字幕 3", fallback);
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(0.75f)]
    [InlineData(1.0f)]
    [InlineData(1.25f)]
    [InlineData(1.5f)]
    [InlineData(2.0f)]
    public async Task 六档合法倍速会更新控制快照(float rate)
    {
        using var rig = new ControlRig();
        Assert.True((await rig.Session.LoadAsync("video.secvid", "password")).Success);

        var result = await rig.Session.SetRateAsync(rate);

        Assert.True(result.Success);
        Assert.Equal(rate, rig.Host.Rate);
        Assert.Equal(rate, rig.Session.Snapshot.Controls.Rate);
    }

    [Fact]
    public async Task 非法倍速被拒绝且不修改当前状态()
    {
        using var rig = new ControlRig();
        Assert.True((await rig.Session.LoadAsync("video.secvid", "password")).Success);

        var result = await rig.Session.SetRateAsync(3.0f);

        Assert.False(result.Success);
        Assert.Equal(PlaybackFailureCode.InvalidRequest, result.Failure?.Code);
        Assert.Equal(1.0f, rig.Session.Snapshot.Controls.Rate);
        Assert.Equal(PlaybackState.Ready, rig.Session.Snapshot.State);
    }

    [Fact]
    public async Task 倍速设置失败保持媒体可播放状态()
    {
        using var rig = new ControlRig();
        Assert.True((await rig.Session.LoadAndPlayAsync("video.secvid", "password")).Success);
        rig.Host.AcceptRate = false;

        var result = await rig.Session.SetRateAsync(1.5f);

        Assert.False(result.Success);
        Assert.Equal(PlaybackFailureCode.ControlUnavailable, result.Failure?.Code);
        Assert.Equal(PlaybackState.Playing, rig.Session.Snapshot.State);
        Assert.Equal(1.0f, rig.Session.Snapshot.Controls.Rate);
    }

    [Fact]
    public async Task 音轨和字幕使用真实ID并保持播放状态()
    {
        using var rig = new ControlRig();
        rig.Host.AudioTracks =
        [
            new PlaybackTrackOption(10, "中文"),
            new PlaybackTrackOption(20, "English")
        ];
        rig.Host.SubtitleTracks =
        [
            new PlaybackTrackOption(-1, "关闭字幕"),
            new PlaybackTrackOption(30, "中文")
        ];
        rig.Host.AudioTrack = 10;
        rig.Host.SubtitleTrack = -1;
        Assert.True((await rig.Session.LoadAndPlayAsync("video.secvid", "password")).Success);

        Assert.True((await rig.Session.SelectAudioTrackAsync(20)).Success);
        Assert.True((await rig.Session.SelectSubtitleTrackAsync(30)).Success);

        Assert.Equal(20, rig.Host.AudioTrack);
        Assert.Equal(30, rig.Host.SubtitleTrack);
        Assert.Equal(20, rig.Session.Snapshot.Controls.SelectedAudioTrackId);
        Assert.Equal(30, rig.Session.Snapshot.Controls.SelectedSubtitleTrackId);
        Assert.Equal(PlaybackState.Playing, rig.Session.Snapshot.State);
    }

    [Fact]
    public async Task 轨道切换失败保留原选择且不进入Faulted()
    {
        using var rig = new ControlRig();
        rig.Host.AudioTracks =
        [
            new PlaybackTrackOption(1, "音轨 1"),
            new PlaybackTrackOption(2, "音轨 2")
        ];
        Assert.True((await rig.Session.LoadAndPlayAsync("video.secvid", "password")).Success);
        rig.Host.AcceptAudioTrack = false;

        var result = await rig.Session.SelectAudioTrackAsync(2);

        Assert.False(result.Success);
        Assert.Equal(PlaybackFailureCode.ControlUnavailable, result.Failure?.Code);
        Assert.Equal(1, rig.Session.Snapshot.Controls.SelectedAudioTrackId);
        Assert.Equal(PlaybackState.Playing, rig.Session.Snapshot.State);
    }

    [Fact]
    public async Task 连续相对Seek基于前一条命令完成后的真实位置()
    {
        using var rig = new ControlRig();
        Assert.True((await rig.Session.LoadAndPlayAsync("video.secvid", "password")).Success);

        Assert.True((await rig.Session.SeekRelativeAsync(5_000)).Success);
        Assert.True((await rig.Session.SeekRelativeAsync(5_000)).Success);
        Assert.True((await rig.Session.SeekRelativeAsync(-5_000)).Success);

        Assert.Equal(6_000, rig.Host.PositionMs);
    }

    [Fact]
    public async Task 表面迁移恢复倍速音轨字幕和暂停状态()
    {
        using var rig = new ControlRig();
        rig.Host.AudioTracks =
        [
            new PlaybackTrackOption(1, "音轨 1"),
            new PlaybackTrackOption(2, "音轨 2")
        ];
        rig.Host.SubtitleTracks =
        [
            new PlaybackTrackOption(-1, "关闭字幕"),
            new PlaybackTrackOption(3, "字幕 1")
        ];
        Assert.True((await rig.Session.LoadAndPlayAsync("video.secvid", "password")).Success);
        Assert.True((await rig.Session.SetRateAsync(1.5f)).Success);
        Assert.True((await rig.Session.SelectAudioTrackAsync(2)).Success);
        Assert.True((await rig.Session.SelectSubtitleTrackAsync(3)).Success);
        Assert.True((await rig.Session.PauseAsync()).Success);

        var first = new VideoSurfaceIdentity(1);
        Assert.True((await rig.Session.AttachAndRestoreSurfaceAsync(first)).Success);
        rig.Session.DetachSurface(first);

        // 模拟原生 Stop/重新启动期间回到默认控制，确保恢复依赖会话快照而非偶然的
        // LibVLC 实例残留状态。
        rig.Host.Rate = 1.0f;
        rig.Host.AudioTrack = 1;
        rig.Host.SubtitleTrack = -1;
        var second = new VideoSurfaceIdentity(2);
        Assert.True((await rig.Session.AttachAndRestoreSurfaceAsync(second)).Success);

        Assert.Equal(1.5f, rig.Host.Rate);
        Assert.Equal(2, rig.Host.AudioTrack);
        Assert.Equal(3, rig.Host.SubtitleTrack);
        Assert.Equal(PlaybackState.Paused, rig.Session.Snapshot.State);
    }

    private sealed class ControlRig : IDisposable
    {
        private readonly InlineDispatcher _dispatcher = new();
        private readonly ImmediateReaper _reaper = new();
        private readonly TestDocumentLifetime _lifetime = new();

        public ControlRig()
        {
            Host = new ControlHost();
            Session = new SecureVideoPlayer(
                Host,
                new SourceFactory(),
                _dispatcher,
                _reaper,
                _lifetime);
        }

        public ControlHost Host { get; }
        public SecureVideoPlayer Session { get; }

        public void Dispose()
        {
            Session.Dispose();
            _reaper.Dispose();
            _dispatcher.Dispose();
            Host.Dispose();
            _lifetime.Dispose();
        }
    }

    private sealed class SourceFactory : IPlaybackMediaSourceFactory
    {
        public Task<IPlaybackMediaSource> CreateAsync(
            long generation,
            string filePath,
            string password,
            CancellationToken cancellationToken) =>
            Task.FromResult<IPlaybackMediaSource>(new Source(generation));
    }

    private sealed class Source(long generation) : IPlaybackMediaSource
    {
        public long Generation { get; } = generation;
        public Media NativeMedia => null!;
        public event Action<IPlaybackMediaSource, PlaybackFailure>? Failed
        {
            add { }
            remove { }
        }
        public void PrepareForPlayback() { }
        public void RequestStop() { }
        public void Dispose() { }
    }

    private sealed class ControlHost : IPlaybackPlayerHost
    {
        public event EventHandler? OutputChanged;
        public void Initialize() { }
        public void NotifyOutputChanged() => OutputChanged?.Invoke(this, EventArgs.Empty);

        private IPlaybackMediaSource? _source;

        public MediaPlayer NativePlayer => null!;
        public long NativeOutputGeneration => 1;
        public long PositionMs { get; set; } = 1_000;
        public long DurationMs => 60_000;
        public bool IsSeekable => true;
        public bool HasVideo => true;
        public bool HasAudio => AudioTracks.Count > 0;
        public int VideoTrackCount => 1;
        public int AudioTrackCount => AudioTracks.Count;
        public bool IsPlaying { get; private set; }
        public bool IsPaused { get; private set; }
        public int Volume { get; private set; } = 50;
        public float Rate { get; set; } = 1.0f;
        public int AudioTrack { get; set; } = 1;
        public int SubtitleTrack { get; set; } = -1;
        public bool AcceptRate { get; set; } = true;
        public bool AcceptAudioTrack { get; set; } = true;
        public bool AcceptSubtitleTrack { get; set; } = true;
        public IReadOnlyList<PlaybackTrackOption> AudioTracks { get; set; } =
            [new PlaybackTrackOption(1, "音轨 1")];
        public IReadOnlyList<PlaybackTrackOption> SubtitleTracks { get; set; } =
            [new PlaybackTrackOption(-1, "关闭字幕")];

        public event Action<long, PlaybackState>? StateChanged;
        public event Action<long>? PositionChanged;
        public event Action<long, PlaybackFailure>? Failed
        {
            add { }
            remove { }
        }

        public void Attach(IPlaybackMediaSource source) => _source = source;
        public void Detach() => _source = null;
        public bool Play()
        {
            IsPlaying = true;
            IsPaused = false;
            StateChanged?.Invoke(_source?.Generation ?? 0, PlaybackState.Playing);
            return true;
        }
        public void Stop()
        {
            IsPlaying = false;
            IsPaused = false;
            StateChanged?.Invoke(_source?.Generation ?? 0, PlaybackState.Stopped);
        }
        public Task PauseAtAsync(long positionMs, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PositionMs = positionMs;
            IsPaused = true;
            IsPlaying = false;
            StateChanged?.Invoke(
                _source?.Generation ?? 0,
                PlaybackState.Paused);
            return Task.CompletedTask;
        }
        public void SetPause(bool paused)
        {
            IsPaused = paused;
            IsPlaying = !paused;
        }
        public void SetVolume(int volume) => Volume = Math.Clamp(volume, 0, 100);
        public bool SetRate(float rate)
        {
            if (!AcceptRate)
            {
                return false;
            }
            Rate = rate;
            return true;
        }
        public IReadOnlyList<PlaybackTrackOption> GetAudioTracks() => AudioTracks;
        public IReadOnlyList<PlaybackTrackOption> GetSubtitleTracks() => SubtitleTracks;
        public bool SetAudioTrack(int trackId)
        {
            if (!AcceptAudioTrack)
            {
                return false;
            }
            AudioTrack = trackId;
            return true;
        }
        public bool SetSubtitleTrack(int trackId)
        {
            if (!AcceptSubtitleTrack)
            {
                return false;
            }
            SubtitleTrack = trackId;
            return true;
        }
        public Task SeekAsync(
            long positionMs,
            bool waitForFrame,
            CancellationToken cancellationToken)
        {
            PositionMs = positionMs;
            PositionChanged?.Invoke(_source?.Generation ?? 0);
            return Task.CompletedTask;
        }
        public Task<bool> RestoreSurfaceAsync(
            long positionMs,
            bool restorePaused,
            CancellationToken cancellationToken)
        {
            PositionMs = positionMs;
            IsPaused = restorePaused;
            IsPlaying = !restorePaused;
            return Task.FromResult(true);
        }
        public void Dispose()
        {
            _source = null;
            StateChanged = null;
            PositionChanged = null;
        }
    }

    private sealed class InlineDispatcher : IPlaybackNativeDispatcher
    {
        public Task InvokeAsync(
            string operation,
            Action action,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
        public Task<T> InvokeAsync<T>(
            string operation,
            Func<T> action,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(action());
        }
        public Task<T> InvokeAsync<T>(
            string operation,
            Func<CancellationToken, Task<T>> action,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return action(cancellationToken);
        }
        public void Dispose() { }
    }

    private sealed class ImmediateReaper : IPlaybackResourceReaper
    {
        public Task EnqueueAsync(
            IPlaybackMediaSource source,
            bool waitForCompletion,
            CancellationToken cancellationToken = default)
        {
            source.Dispose();
            return Task.CompletedTask;
        }
        public void Dispose() { }
    }
}
