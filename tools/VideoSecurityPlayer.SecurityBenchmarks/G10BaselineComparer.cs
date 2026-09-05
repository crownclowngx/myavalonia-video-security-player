using System.Text;
using System.Text.Json;

/// <summary>
/// 把至少三轮 G10 结果聚合为可审核基线，并按环境指纹执行相对回归判断。
/// </summary>
internal static class G10BaselineComparer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static int RunAggregate(string[] args)
    {
        try
        {
            var inputs = ReadMany(args, "--input");
            if (inputs.Count < 3)
                throw new ArgumentException("G10 聚合必须提供至少三份 --input。");
            var reports = inputs.Select(Read<G10BenchmarkReport>).ToArray();
            var aggregate = Aggregate(reports);
            Write(Required(args, "--output"), aggregate);
            return aggregate.HardGatePassed ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    public static int RunCompare(string[] args)
    {
        try
        {
            var baseline = Read<G10AggregateReport>(Required(args, "--baseline"));
            var candidate = Read<G10AggregateReport>(Required(args, "--candidate"));
            var comparison = Compare(baseline, candidate);
            Write(Required(args, "--output"), comparison);
            if (!comparison.Comparable)
                return 3;
            return comparison.Passed ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    public static G10AggregateReport Aggregate(params G10BenchmarkReport[] reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        if (reports.Length < 2)
            throw new ArgumentException("至少需要两轮报告才能聚合。", nameof(reports));

        var first = reports[0];
        var firstScenario = CreateScenarioSignature(first.Parameters);
        if (reports.Any(report =>
                report.ComparableFingerprint != first.ComparableFingerprint))
            throw new InvalidOperationException("各轮 G10 环境指纹不一致，不能聚合。");
        if (reports.Any(report =>
                CreateScenarioSignature(report.Parameters) != firstScenario))
            throw new InvalidOperationException("各轮 G10 场景参数不一致，不能聚合。");

        var metricSets = reports
            .Select(Extract)
            .Select(metrics => metrics.ToDictionary(metric => metric.Name))
            .ToArray();
        var metrics = metricSets[0].Keys.Order(StringComparer.Ordinal)
            .Select(name =>
            {
                var samples = metricSets
                    .Select(metrics => metrics.TryGetValue(name, out var metric)
                        ? metric
                        : throw new InvalidOperationException(
                            $"G10 指标 {name} 在部分轮次中缺失。"))
                    .ToArray();
                var firstMetric = samples[0];
                return new ComparableMetric(
                    name,
                    firstMetric.Direction,
                    firstMetric.Unit,
                    Math.Round(samples.Average(metric => metric.Median), 4),
                    Math.Round(samples.Max(metric => metric.P95), 4));
            })
            .ToArray();

        return new G10AggregateReport(
            1,
            "g10-performance-baseline",
            DateTimeOffset.UtcNow,
            first.Environment,
            first.ComparableFingerprint,
            firstScenario,
            reports.All(report => report.HardGate.Passed),
            metrics);
    }

    public static G10ComparisonReport Compare(
        G10AggregateReport baseline,
        G10AggregateReport candidate)
    {
        if (!candidate.HardGatePassed)
        {
            return new G10ComparisonReport(
                1,
                "g10-performance-comparison",
                baseline.ComparableFingerprint == candidate.ComparableFingerprint,
                false,
                "candidate-hard-gate-failed",
                []);
        }

        if (baseline.ComparableFingerprint != candidate.ComparableFingerprint)
        {
            return new G10ComparisonReport(
                1,
                "g10-performance-comparison",
                false,
                false,
                "environment-fingerprint-mismatch",
                []);
        }

        if (baseline.ScenarioSignature != candidate.ScenarioSignature)
        {
            return new G10ComparisonReport(
                1,
                "g10-performance-comparison",
                false,
                false,
                "scenario-parameters-mismatch",
                []);
        }

        var baselineMetrics = baseline.Metrics.ToDictionary(metric => metric.Name);
        var results = candidate.Metrics.Select(metric =>
        {
            if (!baselineMetrics.TryGetValue(metric.Name, out var expected))
            {
                return new MetricComparison(
                    metric.Name,
                    false,
                    "baseline-metric-missing",
                    0,
                    metric.Median,
                    0,
                    metric.P95);
            }
            return CompareMetric(expected, metric);
        }).ToArray();
        return new G10ComparisonReport(
            1,
            "g10-performance-comparison",
            true,
            results.All(result => result.Passed),
            results.All(result => result.Passed) ? "passed" : "performance-regression",
            results);
    }

    internal static MetricComparison CompareMetric(
        ComparableMetric baseline,
        ComparableMetric candidate)
    {
        if (baseline.Direction == MetricDirection.HigherIsBetter)
        {
            var medianLimit = Math.Round(baseline.Median * 0.75, 4);
            return new MetricComparison(
                candidate.Name,
                candidate.Median >= medianLimit,
                candidate.Median >= medianLimit ? "passed" : "throughput-regression",
                medianLimit,
                candidate.Median,
                0,
                candidate.P95);
        }

        var medianUpper = Math.Round(
            Math.Max(baseline.Median * 1.30, baseline.Median + 2),
            4);
        var p95Upper = Math.Round(
            Math.Max(baseline.P95 * 1.50, baseline.P95 + 5),
            4);
        var passed = candidate.Median <= medianUpper && candidate.P95 <= p95Upper;
        return new MetricComparison(
            candidate.Name,
            passed,
            passed ? "passed" : "latency-regression",
            medianUpper,
            candidate.Median,
            p95Upper,
            candidate.P95);
    }

    private static IEnumerable<ComparableMetric> Extract(G10BenchmarkReport report)
    {
        foreach (var metric in ExtractCrypto("crypto.small", report.SmallCrypto))
            yield return metric;
        foreach (var metric in ExtractCrypto("crypto.large", report.LargeCrypto))
            yield return metric;

        yield return Latency(
            "library.small.first-scan",
            report.Library.SmallFirstScanMs);
        yield return Latency(
            "library.small.hot-scan",
            report.Library.SmallHotScanMs);
        yield return Latency(
            "library.large.first-scan",
            report.Library.LargeFirstScanMs);
        yield return Latency(
            "library.large.hot-scan",
            report.Library.LargeHotScanMs);
        yield return Latency(
            "library.search",
            report.Library.Projection.SearchElapsedMs);
        yield return new ComparableMetric(
            "library.sort",
            MetricDirection.LowerIsBetter,
            "ms",
            report.Library.Projection.SortElapsed.Median,
            report.Library.Projection.SortElapsed.P95);
        yield return Latency(
            "library.incremental.add",
            report.Library.Projection.Incremental.AddElapsedMs);
        yield return Latency(
            "library.incremental.modify",
            report.Library.Projection.Incremental.ModifyElapsedMs);
        yield return Latency(
            "library.incremental.rename",
            report.Library.Projection.Incremental.RenameElapsedMs);
        yield return Latency(
            "library.incremental.delete",
            report.Library.Projection.Incremental.DeleteElapsedMs);
        yield return Latency(
            "library.watcher-storm-settle",
            report.Library.Watcher.SettleElapsedMs);
    }

    private static IEnumerable<ComparableMetric> ExtractCrypto(
        string prefix,
        CryptoScenarioReport report)
    {
        yield return ToMetric(
            $"{prefix}.encrypt-elapsed",
            MetricDirection.LowerIsBetter,
            report.EncryptElapsed);
        yield return ToMetric(
            $"{prefix}.encrypt-throughput",
            MetricDirection.HigherIsBetter,
            report.EncryptThroughput);
        yield return ToMetric(
            $"{prefix}.decrypt-elapsed",
            MetricDirection.LowerIsBetter,
            report.DecryptElapsed);
        yield return ToMetric(
            $"{prefix}.decrypt-throughput",
            MetricDirection.HigherIsBetter,
            report.DecryptThroughput);
        yield return ToMetric(
            $"{prefix}.random-seek",
            MetricDirection.LowerIsBetter,
            report.RandomSeek);
    }

    private static ComparableMetric ToMetric(
        string name,
        MetricDirection direction,
        Metric metric) =>
        new(name, direction, metric.Unit, metric.Median, metric.P95);

    private static ComparableMetric Latency(string name, double value) =>
        new(name, MetricDirection.LowerIsBetter, "ms", value, value);

    private static string CreateScenarioSignature(G10Options options)
    {
        var input = string.Join(
            "\n",
            options.SmallMiB,
            options.LargeMiB,
            options.SmallIterations,
            options.LargeIterations,
            options.SeekCount,
            options.LibrarySmall,
            options.LibraryLarge,
            options.StormEvents);
        return Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input)))
            .ToLowerInvariant();
    }

    private static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"无法读取 {typeof(T).Name}。");

    private static void Write(string path, object value)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(value, JsonOptions).ReplaceLineEndings("\n") + "\n",
            new UTF8Encoding(false));
    }

    private static IReadOnlyList<string> ReadMany(string[] args, string name)
    {
        var values = new List<string>();
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                values.Add(args[++index]);
            }
        }
        return values;
    }

    private static string Required(string[] args, string name)
    {
        var index = Array.FindIndex(
            args,
            value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length)
            throw new ArgumentException($"缺少参数 {name}。");
        return args[index + 1];
    }
}

internal enum MetricDirection
{
    LowerIsBetter,
    HigherIsBetter
}

internal sealed record ComparableMetric(
    string Name,
    MetricDirection Direction,
    string Unit,
    double Median,
    double P95);

internal sealed record G10AggregateReport(
    int SchemaVersion,
    string Kind,
    DateTimeOffset CreatedUtc,
    EnvironmentReport Environment,
    string ComparableFingerprint,
    string ScenarioSignature,
    bool HardGatePassed,
    IReadOnlyList<ComparableMetric> Metrics);

internal sealed record MetricComparison(
    string Name,
    bool Passed,
    string Reason,
    double MedianLimit,
    double ActualMedian,
    double P95Limit,
    double ActualP95);

internal sealed record G10ComparisonReport(
    int SchemaVersion,
    string Kind,
    bool Comparable,
    bool Passed,
    string Reason,
    IReadOnlyList<MetricComparison> Metrics);
