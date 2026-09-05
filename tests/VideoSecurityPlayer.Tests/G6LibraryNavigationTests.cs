using System.Runtime.CompilerServices;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;
using VideoSecurityPlayer.ViewModels.SecretVideoPlayer;
using Xunit;

namespace VideoSecurityPlayer.Tests;

/// <summary>G6 媒体库导航顺序和筛选边界测试。</summary>
public sealed class G6LibraryNavigationTests
{
    [Fact]
    public void 连续播放默认关闭且不会跨文档持久化()
    {
        var firstPlayer = Assert.IsType<VideoPlayerControlViewModel>(
            RuntimeHelpers.GetUninitializedObject(typeof(VideoPlayerControlViewModel)));
        var secondPlayer = Assert.IsType<VideoPlayerControlViewModel>(
            RuntimeHelpers.GetUninitializedObject(typeof(VideoPlayerControlViewModel)));
        using var firstLifetime = new TestDocumentLifetime();
        using var secondLifetime = new TestDocumentLifetime();
        using var firstBrowser = new VideoLibraryBrowserViewModel(new FixedScanner([]), firstLifetime);
        using var secondBrowser = new VideoLibraryBrowserViewModel(new FixedScanner([]), secondLifetime);
        using var first = new SecretVideoLibraryViewModel(firstBrowser, firstPlayer, firstLifetime);
        using var second = new SecretVideoLibraryViewModel(secondBrowser, secondPlayer, secondLifetime);

        Assert.False(first.IsContinuousPlaybackEnabled);
        first.IsContinuousPlaybackEnabled = true;
        Assert.True(first.IsContinuousPlaybackEnabled);
        Assert.False(second.IsContinuousPlaybackEnabled);
    }

    [Fact]
    public async Task 相邻项使用当前可见排序而不是扫描输入顺序()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "g6-visible-order"));
        var a = Path.Combine(root, "a.secvid");
        var b = Path.Combine(root, "b.secvid");
        var c = Path.Combine(root, "c.secvid");
        using var lifetime = new TestDocumentLifetime();
        using var browser = new VideoLibraryBrowserViewModel(new FixedScanner(
        [
            Ready(c, "c", "第三项"),
            Ready(a, "a", "第一项"),
            Ready(b, "b", "第二项")
        ]), lifetime);
        await browser.LoadFolderAsync(root);

        Assert.Equal(a, browser.FindVisibleAdjacent(b, -1)?.FilePath);
        Assert.Equal(c, browser.FindVisibleAdjacent(b, 1)?.FilePath);
        Assert.Null(browser.FindVisibleAdjacent(a, -1));
        Assert.Null(browser.FindVisibleAdjacent(c, 1));
    }

    [Fact]
    public async Task 搜索隐藏当前播放项后不推断相邻项()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "g6-filter-order"));
        var a = Path.Combine(root, "a.secvid");
        var b = Path.Combine(root, "b.secvid");
        using var lifetime = new TestDocumentLifetime();
        using var browser = new VideoLibraryBrowserViewModel(new FixedScanner(
        [
            Ready(a, "a", "保留"),
            Ready(b, "b", "隐藏")
        ]), lifetime);
        await browser.LoadFolderAsync(root);

        browser.SearchText = "保留";
        await Task.Delay(250);

        Assert.Null(browser.FindVisibleAdjacent(b, -1));
        Assert.Null(browser.FindVisibleAdjacent(b, 1));
    }

    private static VideoLibraryScanResult Ready(
        string path,
        string name,
        string title) =>
        new(
            path,
            name,
            title,
            string.Empty,
            VideoLibraryMetadataState.Ready,
            string.Empty);

    private sealed class FixedScanner(IReadOnlyList<VideoLibraryScanResult> results)
        : IVideoLibraryScanner
    {
        public async IAsyncEnumerable<VideoLibraryScanResult> ScanAsync(
            string folderPath,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            foreach (var result in results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return result;
                await Task.Yield();
            }
        }
    }
}
