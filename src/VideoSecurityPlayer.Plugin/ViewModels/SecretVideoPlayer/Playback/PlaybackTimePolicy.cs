using System.Globalization;

namespace VideoSecurityPlayer.ViewModels.SecretVideoPlayer.Playback;

/// <summary>将用户输入的秒、分:秒、时:分:秒转换为媒体内的位置；不接受负数、溢出或越过片尾。</summary>
public static class PlaybackTimePolicy
{
    public static bool TryParse(string? text, long durationMs, out long positionMs)
    {
        positionMs = 0;
        if (string.IsNullOrWhiteSpace(text) || durationMs <= 0) return false;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3) return false;
        long totalSeconds = 0;
        for (var index = 0; index < parts.Length; index++)
        {
            if (!long.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
                (index > 0 && value >= 60)) return false;
            try { totalSeconds = checked(totalSeconds * 60 + value); }
            catch (OverflowException) { return false; }
        }
        if (totalSeconds > long.MaxValue / 1000) return false;
        var milliseconds = totalSeconds * 1000;
        if (milliseconds >= durationMs) return false;
        positionMs = milliseconds;
        return true;
    }
}
