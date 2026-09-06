using VideoSecurityPlayer.Business.SecretVideoPlayer.Playback;
using Xunit;

namespace VideoSecurityPlayer.Tests;

public sealed class R1PlaybackTrackSelectionTests
{
    [Theory]
    [InlineData(20, 20, 20)]
    [InlineData(0, null, -1)]
    [InlineData(99, null, -1)]
    [InlineData(-1, -1, -1)]
    public void 使用轨道ID并仅在字幕缺失时回退关闭项(int id, int? audio, int? subtitle)
    {
        PlaybackTrackOption[] tracks = [new(20, "轨道"), new(-1, "关闭")];
        Assert.Equal(audio, PlaybackTrackSelectionPolicy.SelectAudio(tracks, id));
        Assert.Equal(subtitle, PlaybackTrackSelectionPolicy.SelectSubtitle(tracks, id));
        Assert.Equal([20, -1], tracks.Select(x => x.Id));
    }

    [Fact]
    public void 空列表与缺少关闭项时不虚构选择()
    {
        Assert.Null(PlaybackTrackSelectionPolicy.SelectAudio([], 20));
        Assert.Null(PlaybackTrackSelectionPolicy.SelectSubtitle([], -1));
        Assert.Null(PlaybackTrackSelectionPolicy.SelectSubtitle([new(20, "字幕")], 99));
    }
}
