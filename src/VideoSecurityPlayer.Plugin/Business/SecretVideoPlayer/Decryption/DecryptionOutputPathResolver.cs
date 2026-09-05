namespace VideoSecurityPlayer.Business.SecretVideoPlayer.Decryption;

using VideoSecurityPlayer.Business.SecretVideoPlayer.Operations;

/// <summary>
/// 把不可信的公开文件名转换为安全、不会覆盖现有文件的输出路径。
/// </summary>
public sealed class DecryptionOutputPathResolver
{
    private readonly IOutputPathConflictResolver _conflictResolver;

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>创建使用共享非覆盖冲突策略的解密名称解析器。</summary>
    public DecryptionOutputPathResolver()
        : this(new OutputPathConflictResolver())
    {
    }

    /// <summary>
    /// 注入计划阶段冲突解析器；公开文件名净化仍由本类型独占负责。
    /// </summary>
    public DecryptionOutputPathResolver(IOutputPathConflictResolver conflictResolver)
    {
        _conflictResolver = conflictResolver ?? throw new ArgumentNullException(nameof(conflictResolver));
    }

    /// <summary>
    /// G2 兼容入口，保持“自动数字后缀且绝不覆盖”的既有行为。
    /// </summary>
    public string GetAvailablePath(
        string outputDirectory,
        DecryptionCandidate candidate,
        ISet<string> allocatedPaths) =>
        ResolvePath(
            outputDirectory,
            candidate,
            OutputConflictPolicy.GenerateUniqueName,
            allocatedPaths).OutputPath;

    /// <summary>
    /// 先净化不可信公开名称，再应用用户明确选择的非覆盖冲突策略。
    /// </summary>
    public OutputPathResolution ResolvePath(
        string outputDirectory,
        DecryptionCandidate candidate,
        OutputConflictPolicy conflictPolicy,
        ISet<string> allocatedPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(allocatedPaths);

        var fullDirectory = Path.GetFullPath(outputDirectory);
        var fallbackName = Path.GetFileNameWithoutExtension(candidate.EncryptedFileName);
        var originalBaseName = Path.GetFileNameWithoutExtension(Path.GetFileName(candidate.OriginalFileName));
        var baseName = SanitizeFileName(originalBaseName, fallbackName);
        var extension = SanitizeExtension(candidate.OriginalExtension);

        var requestedPath = Path.Combine(fullDirectory, baseName + extension);
        return _conflictResolver.Resolve(requestedPath, conflictPolicy, allocatedPaths);
    }

    internal static string SanitizeFileName(string? requestedName, string fallbackName)
    {
        var value = string.IsNullOrWhiteSpace(requestedName) ? fallbackName : requestedName;
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value
            .Select(character => invalid.Contains(character) || character is '/' or '\\' ? '_' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.', ' ');

        if (string.IsNullOrWhiteSpace(sanitized) || WindowsReservedNames.Contains(sanitized))
        {
            sanitized = new string(fallbackName
                .Select(character => invalid.Contains(character) || character is '/' or '\\' ? '_' : character)
                .ToArray())
                .Trim()
                .TrimEnd('.', ' ');
        }

        if (string.IsNullOrWhiteSpace(sanitized) || WindowsReservedNames.Contains(sanitized))
            sanitized = "decrypted-video";

        return sanitized;
    }

    private static string SanitizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return string.Empty;

        var normalized = extension.StartsWith('.') ? extension : "." + extension;
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        if (normalized.Length > 33 || normalized.Skip(1).Any(character =>
                invalid.Contains(character) || character is '/' or '\\' or '.'))
            return string.Empty;

        return normalized;
    }
}
