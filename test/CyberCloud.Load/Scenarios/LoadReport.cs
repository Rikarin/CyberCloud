using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Load.Scenarios;

/// <summary>
///     A latency distribution, in milliseconds, with the rate it was measured at.
/// </summary>
/// <param name="P50">The median.</param>
/// <param name="P95">The 95th percentile.</param>
/// <param name="P99">The 99th percentile — the number every budget in docs/plan/23 § The load scenarios is written about.</param>
/// <param name="Max">The slowest sample.</param>
/// <param name="Samples">How many requests the percentiles are over. A p99 over 40 samples is the 40th sample.</param>
/// <param name="Errors">Requests that did not succeed. Excluded from the percentiles and reported beside them.</param>
/// <param name="Seconds">How long the measurement window was.</param>
/// <param name="TargetRate">What the driver was asked for, per second.</param>
public sealed record Distribution(
    double P50,
    double P95,
    double P99,
    double Max,
    int Samples,
    int Errors,
    double Seconds,
    double TargetRate
) {
    /// <summary>What the driver achieved, per second, successes and errors together.</summary>
    public double AchievedRate => Seconds <= 0 ? 0 : (Samples + Errors) / Seconds;

    /// <summary>Builds the distribution from raw latencies.</summary>
    /// <param name="latencies">One entry per successful request, in milliseconds. Sorted here.</param>
    /// <param name="errors">Requests that failed.</param>
    /// <param name="window">The measurement window.</param>
    /// <param name="targetRate">The requested rate.</param>
    public static Distribution Of(List<double> latencies, int errors, TimeSpan window, double targetRate) {
        ArgumentNullException.ThrowIfNull(latencies);
        latencies.Sort();

        return new(
            Percentile(latencies, 0.50),
            Percentile(latencies, 0.95),
            Percentile(latencies, 0.99),
            latencies.Count == 0 ? 0 : latencies[^1],
            latencies.Count,
            errors,
            window.TotalSeconds,
            targetRate
        );
    }

    /// <summary>The nearest-rank percentile, which is the conservative reading for a p99 budget.</summary>
    static double Percentile(List<double> sorted, double p) {
        if (sorted.Count == 0) {
            return 0;
        }

        var rank = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"p50 {P50:F1} ms, p95 {P95:F1} ms, p99 {P99:F1} ms, max {Max:F0} ms over {Samples} requests "
            + $"({Errors} errors) at {AchievedRate:F0}/s of {TargetRate:F0}/s asked for {Seconds:F0} s"
        );
}

/// <summary>
///     Collects the numbers and writes them where <c>build/Build.Load.cs</c> reads them.
/// </summary>
/// <remarks>
///     <para>
///         The file is a bare <c>{ "metrics": { name: number } }</c> map the build already knows how
///         to read, plus three things it did not carry before this suite existed and now reads:
///         <c>scale</c> (0.1 — one tenth of the row in docs/plan/23), <c>vacuous</c> (a metric name
///         to the sentence saying why this machine could not measure it), and <c>detail</c> (the full
///         distribution behind each p99, for the dated table).
///     </para>
///     <para>
///         ⚠ A metric this suite measured goes under <c>metrics</c>; one it could not goes under
///         <c>vacuous</c>; a metric under neither is a scenario that crashed, and the build treats it
///         as the failure it is. Nothing here writes a number it did not measure.
///     </para>
/// </remarks>
public sealed class LoadReport {
    /// <summary>The environment variable the build passes with the results path.</summary>
    public const string ResultsPathVariable = "CYBERCLOUD_LOAD_RESULTS";

    /// <summary>The fraction of docs/plan/23 § The load scenarios' rates this suite drives.</summary>
    public const double Scale = 0.1;

    readonly ConcurrentDictionary<string, double> metrics = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, string> vacuous = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, JsonObject> detail = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, string> notes = new(StringComparer.Ordinal);

    /// <summary>Records a measured number for a metric the build gates.</summary>
    /// <param name="metric">The key in <c>Build.Load.cs § LoadMetrics</c>.</param>
    /// <param name="value">What was measured.</param>
    /// <param name="distribution">The distribution behind it, when the number is a percentile.</param>
    public void Measured(string metric, double value, Distribution? distribution = null) {
        metrics[metric] = value;

        if (distribution is not null) {
            detail[metric] = new JsonObject {
                ["p50"] = distribution.P50,
                ["p95"] = distribution.P95,
                ["p99"] = distribution.P99,
                ["max"] = distribution.Max,
                ["samples"] = distribution.Samples,
                ["errors"] = distribution.Errors,
                ["seconds"] = distribution.Seconds,
                ["targetRate"] = distribution.TargetRate,
                ["achievedRate"] = Math.Round(distribution.AchievedRate, 1)
            };
        }

        Console.WriteLine($"[CyberCloud.Load] {metric} = {value.ToString("0.###", CultureInfo.InvariantCulture)}" + (distribution is null ? "" : $" — {distribution}"));
    }

    /// <summary>Records a number that is not gated but belongs in the dated table.</summary>
    /// <param name="metric">The metric the note belongs beside.</param>
    /// <param name="name">The number's name.</param>
    /// <param name="value">The number.</param>
    public void Aside(string metric, string name, double value) {
        var block = detail.GetOrAdd(metric, _ => new JsonObject());
        block[name] = value;
    }

    /// <summary>Records a fact that is not a number — a list, a breakdown — beside a metric.</summary>
    /// <param name="metric">The metric it belongs beside.</param>
    /// <param name="name">The fact's name.</param>
    /// <param name="text">The fact.</param>
    public void Aside(string metric, string name, string text) {
        var block = detail.GetOrAdd(metric, _ => new JsonObject());
        block[name] = text;
    }

    /// <summary>Records that a metric could not be measured here, and why.</summary>
    /// <param name="metric">The key in <c>Build.Load.cs § LoadMetrics</c>.</param>
    /// <param name="reason">What the machine lacks, precisely enough that the reader knows what would change it.</param>
    public void Vacuous(string metric, string reason) {
        vacuous[metric] = reason;
        Console.WriteLine($"[CyberCloud.Load] {metric}: VACUOUS — {reason}");
    }

    /// <summary>A sentence about how a scenario was run, for the table's caption.</summary>
    /// <param name="metric">The metric it is about.</param>
    /// <param name="note">The sentence.</param>
    public void Note(string metric, string note) => notes[metric] = note;

    /// <summary>Where the file goes: the build's variable, or beside the host.</summary>
    public static string ResultsPath =>
        Environment.GetEnvironmentVariable(ResultsPathVariable) is { Length: > 0 } configured
            ? configured
            : Path.Combine(AppContext.BaseDirectory, "load-results.json");

    /// <summary>Writes the file.</summary>
    /// <param name="topology">Facts about what the numbers were measured against.</param>
    public void Write(IReadOnlyDictionary<string, string> topology) {
        ArgumentNullException.ThrowIfNull(topology);

        var root = new JsonObject {
            ["measuredAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["scale"] = Scale,
            ["topology"] = Sorted(topology),
            ["metrics"] = new JsonObject(),
            ["vacuous"] = Sorted(vacuous),
            ["notes"] = Sorted(notes),
            ["detail"] = new JsonObject()
        };

        foreach (var (name, value) in metrics.OrderBy(x => x.Key, StringComparer.Ordinal)) {
            root["metrics"]![name] = value;
        }

        foreach (var (name, block) in detail.OrderBy(x => x.Key, StringComparer.Ordinal)) {
            root["detail"]![name] = block;
        }

        var path = ResultsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[CyberCloud.Load] {metrics.Count} metric(s) and {vacuous.Count} vacuous row(s) written to {path}");
    }

    static JsonObject Sorted(IReadOnlyDictionary<string, string> values) {
        var block = new JsonObject();

        foreach (var (name, value) in values.OrderBy(x => x.Key, StringComparer.Ordinal)) {
            block[name] = value;
        }

        return block;
    }
}
