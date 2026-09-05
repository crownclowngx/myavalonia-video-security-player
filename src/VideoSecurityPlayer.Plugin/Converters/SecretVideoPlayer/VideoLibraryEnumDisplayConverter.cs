using System.Globalization;
using Avalonia.Data.Converters;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Library;

namespace VideoSecurityPlayer.Converters.SecretVideoPlayer;

/// <summary>把持久化使用的稳定枚举值转换为中文界面文本。</summary>
public sealed class VideoLibraryEnumDisplayConverter : IValueConverter
{
    public object Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        value switch
        {
            VideoLibrarySortField.FileName => "文件名",
            VideoLibrarySortField.PublicTitle => "公开标题",
            VideoLibrarySortField.ModifiedTime => "修改时间",
            VideoLibrarySortField.LastPlayedTime => "上次播放",
            VideoLibrarySortDirection.Ascending => "升序",
            VideoLibrarySortDirection.Descending => "降序",
            VideoLibraryStatusFilter.All => "全部",
            VideoLibraryStatusFilter.Available => "可用",
            VideoLibraryStatusFilter.MetadataFailed => "元数据失败",
            VideoLibraryStatusFilter.Unplayed => "未播放",
            VideoLibraryStatusFilter.InProgress => "播放中",
            VideoLibraryStatusFilter.Completed => "已看完",
            _ => value?.ToString() ?? string.Empty
        };

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException("显示转换器不负责从文本反向解析枚举。");
}
