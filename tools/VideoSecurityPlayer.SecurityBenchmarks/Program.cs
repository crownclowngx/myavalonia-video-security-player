using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using VideoSecurityPlayer.Business.SecretVideoPlayer.Container;

if ((args.Any(value => string.Equals(value, "--suite", StringComparison.OrdinalIgnoreCase)) &&
     args.SkipWhile(value => !string.Equals(
             value,
             "--suite",
             StringComparison.OrdinalIgnoreCase))
         .Skip(1)
         .FirstOrDefault() is { } suite &&
     string.Equals(suite, "g10", StringComparison.OrdinalIgnoreCase)) ||
    args.Contains("--g10-child", StringComparer.OrdinalIgnoreCase) ||
    args.Contains("--g10-aggregate", StringComparer.OrdinalIgnoreCase) ||
    args.Contains("--g10-compare", StringComparer.OrdinalIgnoreCase))
{
    Environment.ExitCode = await G10BenchmarkProgram.RunAsync(args);
    return;
}

var options = BenchmarkOptions.Parse(args);
var vectorPath = Path.GetFullPath(options.VectorPath);
if (!File.Exists(vectorPath))
    throw new FileNotFoundException("找不到 SECVID03 固定向量。", vectorPath);

var header = new byte[256];
await using (var file = new FileStream(vectorPath, FileMode.Open, FileAccess.Read, FileShare.Read))
    await file.ReadExactlyAsync(header);

var prefixLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(28, 4));
var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(56, 4));
var kdfIterations = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(64, 4));
var password = "SECVID03-G1-Vector!";

for (var index = 0; index < options.Warmup; index++)
{
    using var stream = SeekableEncryptedVideoStream.Open(vectorPath, password);
}

var openSamples = new double[options.Iterations];
for (var index = 0; index < openSamples.Length; index++)
{
    var started = Stopwatch.GetTimestamp();
    using var stream = SeekableEncryptedVideoStream.Open(vectorPath, password);
    openSamples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}

var buffer = new byte[chunkSize];
for (var index = 0; index < options.Warmup; index++)
{
    using var stream = SeekableEncryptedVideoStream.Open(vectorPath, password);
    stream.Position = prefixLength;
    stream.ReadExactly(buffer);
}

var coldReadSamples = new double[options.Iterations];
for (var index = 0; index < coldReadSamples.Length; index++)
{
    using var stream = SeekableEncryptedVideoStream.Open(vectorPath, password);
    stream.Position = prefixLength;
    var started = Stopwatch.GetTimestamp();
    stream.ReadExactly(buffer);
    coldReadSamples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}

var result = new BenchmarkResult
{
    SchemaVersion = 1,
    TimestampUtc = DateTimeOffset.UtcNow,
    OperatingSystem = RuntimeInformation.OSDescription,
    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    Runtime = RuntimeInformation.FrameworkDescription,
    ProcessorCount = Environment.ProcessorCount,
    BuildConfiguration = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown",
    VectorSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(vectorPath))).ToLowerInvariant(),
    KdfIterations = kdfIterations,
    ChunkSize = chunkSize,
    Warmup = options.Warmup,
    Iterations = options.Iterations,
    Open = Measurement.From(openSamples),
    ColdChunkRead = Measurement.From(coldReadSamples)
};

var json = JsonSerializer.Serialize(
    result,
    new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    });

Console.WriteLine(json);
if (!string.IsNullOrWhiteSpace(options.OutputPath))
{
    var outputPath = Path.GetFullPath(options.OutputPath);
    var outputDirectory = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrEmpty(outputDirectory))
        Directory.CreateDirectory(outputDirectory);
    await File.WriteAllTextAsync(outputPath, json + Environment.NewLine);
}

internal sealed record BenchmarkOptions(
    string VectorPath,
    int Warmup,
    int Iterations,
    string? OutputPath)
{
    public static BenchmarkOptions Parse(string[] args)
    {
        var vector = Path.Combine(
            "tests",
            "VideoSecurityPlayer.Tests",
            "TestAssets",
            "Secvid03Vectors",
            "v1",
            "g1-vector.secvid");
        var warmup = 3;
        var iterations = 10;
        string? output = null;

        for (var index = 0; index < args.Length; index++)
        {
            var value = index + 1 < args.Length ? args[index + 1] : null;
            switch (args[index])
            {
                case "--vector" when value is not null:
                    vector = value;
                    index++;
                    break;
                case "--warmup" when int.TryParse(value, out var parsedWarmup) && parsedWarmup >= 0:
                    warmup = parsedWarmup;
                    index++;
                    break;
                case "--iterations" when int.TryParse(value, out var parsedIterations) && parsedIterations > 0:
                    iterations = parsedIterations;
                    index++;
                    break;
                case "--output" when value is not null:
                    output = value;
                    index++;
                    break;
                default:
                    throw new ArgumentException(
                        $"无法识别参数“{args[index]}”。支持 --vector、--warmup、--iterations、--output。");
            }
        }

        return new BenchmarkOptions(vector, warmup, iterations, output);
    }
}

internal sealed class BenchmarkResult
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public string OperatingSystem { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string Runtime { get; set; } = string.Empty;
    public int ProcessorCount { get; set; }
    public string BuildConfiguration { get; set; } = string.Empty;
    public string VectorSha256 { get; set; } = string.Empty;
    public int KdfIterations { get; set; }
    public int ChunkSize { get; set; }
    public int Warmup { get; set; }
    public int Iterations { get; set; }
    public Measurement Open { get; set; } = new();
    public Measurement ColdChunkRead { get; set; } = new();
}

internal sealed class Measurement
{
    public string Unit { get; set; } = "ms";
    public double Median { get; set; }
    public double P95 { get; set; }
    public double[] Samples { get; set; } = [];

    public static Measurement From(double[] samples)
    {
        var sorted = samples.Order().ToArray();
        var middle = sorted.Length / 2;
        var median = sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2
            : sorted[middle];
        var p95Index = Math.Clamp((int)Math.Ceiling(sorted.Length * 0.95) - 1, 0, sorted.Length - 1);
        return new Measurement
        {
            Median = Math.Round(median, 4),
            P95 = Math.Round(sorted[p95Index], 4),
            Samples = samples.Select(value => Math.Round(value, 4)).ToArray()
        };
    }
}
