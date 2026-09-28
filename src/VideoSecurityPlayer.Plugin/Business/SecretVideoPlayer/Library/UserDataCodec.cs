using System.Text.Json;
using System.Text.Json.Serialization;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Settings;

namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Library;

/// <summary>磁盘文档只保存数据，不携带服务或 UI 状态；文件路径保留旧名，版本由内容决定。</summary>
internal sealed record UserDataDocument(int Version, PlaybackPreferences Preferences,
    VideoLibrarySettings LibrarySettings, IReadOnlyList<VideoPlaybackHistoryEntry> History,
    VideoSettings? Settings = null)
{
    public static UserDataDocument Default { get; } = new(2, PlaybackPreferences.Default,
        VideoLibrarySettings.Default, [], VideoSettings.Default);
}

/// <summary>版本迁移只转换内存快照，不创建备份，也不在读取时重写用户文件。</summary>
internal static class UserDataMigration
{
    public static VideoSettings ResolveSettings(int version, VideoSettings? settings, VideoLibrarySettings library) =>
        version == 1 ? new VideoSettings(new(), new LibraryDefaults(true, library.IncludeSubdirectories,
            library.SortField, library.SortDirection, library.IsLibraryPaneOpen,
            library.IsLibrarySettingsExpanded, library.LibraryPaneWidth), true, new())
            : settings ?? VideoSettings.Default;
}

/// <summary>集中处理有界 JSON、容错字段及迁移；无法识别的更高版本始终只读。</summary>
internal static class UserDataCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new SafeEnumConverter<VideoLibrarySortField>(), new SafeEnumConverter<VideoLibrarySortDirection>(),
            new SafeEnumConverter<VideoLibraryStatusFilter>(), new SafeEnumConverter<RuntimeSourceMode>() }
    };

    public static byte[] Encode(UserDataDocument document) => JsonSerializer.SerializeToUtf8Bytes(document, Options);

    public static UserDataDocument Load(string path, out string warning, out bool readOnly)
    {
        warning = ""; readOnly = false;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return UserDataDocument.Default;
            if (file.Length is <= 0 or > 2 * 1024 * 1024) throw new JsonException();
            var bytes = File.ReadAllBytes(path);
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            if (json.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) && number > 2)
            {
                // 先读取版本再反序列化内容，避免未来格式中的未知字段导致误判损坏并覆盖。
                readOnly = true;
                warning = "播放设置由更新版本创建，当前仅使用默认值；请升级应用后修改设置";
                return UserDataDocument.Default;
            }
            var document = JsonSerializer.Deserialize<UserDataDocument>(bytes, Options);
            if (document is null || document.Version is not (1 or 2)) throw new JsonException();
            var library = Normalize(document.LibrarySettings);
            var histories = new List<VideoPlaybackHistoryEntry>();
            foreach (var entry in document.History ?? [])
            {
                try { if (entry is not null && !string.IsNullOrWhiteSpace(entry.FileId)) histories.Add(Normalize(entry)); }
                catch (ArgumentException) { /* 单条坏记录不使其他历史丢失。 */ }
                catch (NotSupportedException) { }
            }
            var settings = UserDataMigration.ResolveSettings(document.Version, document.Settings, library);
            return new(2, Normalize(document.Preferences), library,
                histories.OrderByDescending(e => e.LastPlayedUtc).DistinctBy(Key, StringComparer.OrdinalIgnoreCase).Take(1000).ToArray(),
                Normalize(settings));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            warning = "播放设置文件无效或无法读取，已使用默认设置";
            return UserDataDocument.Default;
        }
    }

    public static PlaybackPreferences Normalize(PlaybackPreferences? value) =>
        new(Math.Clamp(value?.Volume ?? 50, 0, 100), VideoDefaultsPolicy.NormalizeRate(value?.Rate ?? 1));

    public static VideoSettings Normalize(VideoSettings settings) => new(
        VideoDefaultsPolicy.Normalize(settings.Playback ?? new()), VideoDefaultsPolicy.Normalize(settings.Library ?? new()),
        settings.RecordHistory, Normalize(settings.Runtime ?? new()));

    public static RuntimeSelection Normalize(RuntimeSelection value)
    {
        var mode = Enum.IsDefined(value.Mode) ? value.Mode : RuntimeSourceMode.Auto;
        var directory = "";
        try { if (Path.IsPathFullyQualified(value.Directory)) directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Directory)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        return new(mode, directory);
    }

    public static VideoLibrarySettings Normalize(VideoLibrarySettings? value)
    {
        if (value is null) return VideoLibrarySettings.Default;
        var recent = "";
        try { if (!string.IsNullOrWhiteSpace(value.RecentFolder)) recent = Path.GetFullPath(value.RecentFolder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        var folders = RecentFolderPolicy.Update(recent, value.RecentFolders);
        return value with { RecentFolder = recent, RecentFolders = folders.Count == 0 ? null : folders,
            SortField = Enum.IsDefined(value.SortField) ? value.SortField : VideoLibrarySortField.FileName,
            SortDirection = Enum.IsDefined(value.SortDirection) ? value.SortDirection : VideoLibrarySortDirection.Ascending,
            StatusFilter = Enum.IsDefined(value.StatusFilter) ? value.StatusFilter : VideoLibraryStatusFilter.All,
            LibraryPaneWidth = double.IsFinite(value.LibraryPaneWidth) ? Math.Clamp(value.LibraryPaneWidth, 340, 600) : 400 };
    }

    public static VideoPlaybackHistoryEntry Normalize(VideoPlaybackHistoryEntry entry)
    {
        var duration = Math.Max(0, entry.DurationMs);
        return entry with { FilePath = Path.GetFullPath(entry.FilePath), FileId = entry.FileId.Trim().ToUpperInvariant(),
            OriginalFileLength = Math.Max(0, entry.OriginalFileLength), DurationMs = duration,
            PositionMs = entry.IsCompleted ? 0 : Math.Clamp(entry.PositionMs, 0, duration),
            LastPlayedUtc = entry.LastPlayedUtc == default ? DateTimeOffset.UtcNow : entry.LastPlayedUtc };
    }

    public static string Key(VideoPlaybackHistoryEntry entry) => Key(entry.FilePath, entry.FileId, entry.OriginalFileLength);
    public static string Key(string path, string id, long length) => $"{Path.GetFullPath(path)}\n{id.Trim()}\n{length}";

    /// <summary>未知枚举只恢复该字段，不丢弃同一文件中的历史和其他有效设置。</summary>
    private sealed class SafeEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && Enum.TryParse<T>(reader.GetString(), out var value) && Enum.IsDefined(value)) return value;
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number) && Enum.IsDefined(typeof(T), number)) return (T)Enum.ToObject(typeof(T), number);
            reader.Skip();
            return default;
        }
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
}
