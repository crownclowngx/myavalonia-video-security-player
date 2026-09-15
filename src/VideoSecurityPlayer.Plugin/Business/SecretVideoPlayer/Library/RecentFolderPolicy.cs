namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Library;

/// <summary>最近目录按使用顺序保留最多 10 项，统一规范化并去重；目录暂时离线时不自动删除记录。</summary>
public static class RecentFolderPolicy
{
    public static IReadOnlyList<string> Update(string? current, IEnumerable<string>? previous)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in new[] { current }.Concat(previous ?? []))
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            try
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(item));
                if (seen.Add(full)) paths.Add(full);
                if (paths.Count == 10) break;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        }
        return paths.AsReadOnly();
    }
}
