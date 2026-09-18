using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Chaos.Topology;

/// <summary>What a chaos test concluded about its invariant.</summary>
public enum InvariantStatus {
    /// <summary>The fault was induced and every assertion passed. ✔ in the build's table.</summary>
    Held,

    /// <summary>The fault was induced and an assertion failed. ✘, and the build fails.</summary>
    Violated,

    /// <summary>
    ///     The fault could not be induced here, for a reason the row states. ○ — green because of
    ///     what was not tested, and the build says so rather than printing a tick.
    /// </summary>
    Vacuous
}

/// <summary>One invariant's row in the results file.</summary>
/// <param name="Number">Its position in docs/plan/23 § The chaos invariants.</param>
/// <param name="Status">See <see cref="InvariantStatus" />.</param>
/// <param name="Detail">One sentence, with the numbers that matter in it, for the build's log line.</param>
/// <param name="Numbers">Every number the test measured, by name, for the dated table in docs/plan/23.</param>
public sealed record InvariantOutcome(
    int Number,
    InvariantStatus Status,
    string Detail,
    IReadOnlyDictionary<string, double> Numbers
);

/// <summary>
///     Collects the seven outcomes and writes them where <c>build/Build.Chaos.cs</c> reads them.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The file is the contract, not the process exit code.</b> A skipped test and a passed
///         test both leave the host's exit code at zero, and "invariant 6 skipped because no silo
///         speaks NATS" is not the same news as "invariant 6 held". The build reads this file, prints
///         ✔ ○ ✘ per row, and treats a missing row as the loudest failure of all — a test that
///         crashed before it could say what it found.
///     </para>
///     <para>
///         Written on the topology's dispose so that a test which threw mid-way still leaves the
///         rows the others recorded. Where it goes is <c>CYBERCLOUD_CHAOS_RESULTS</c> when the build
///         set it, and <c>chaos-results.json</c> beside the test host otherwise, so a hand run has
///         the same file to read.
///     </para>
/// </remarks>
public sealed class ChaosReport {
    /// <summary>The environment variable the build passes with the results path.</summary>
    public const string ResultsPathVariable = "CYBERCLOUD_CHAOS_RESULTS";

    readonly ConcurrentDictionary<int, InvariantOutcome> outcomes = new();

    /// <summary>Every outcome recorded so far, by invariant number.</summary>
    public IReadOnlyDictionary<int, InvariantOutcome> Outcomes => outcomes;

    /// <summary>Records that the fault was induced and every assertion passed.</summary>
    /// <param name="number">The invariant, 1 to 7.</param>
    /// <param name="detail">The sentence for the build's log line — say the numbers.</param>
    /// <param name="numbers">Everything measured, for the dated table.</param>
    public void Held(int number, string detail, IReadOnlyDictionary<string, double> numbers) =>
        Record(new(number, InvariantStatus.Held, detail, numbers));

    /// <summary>Records that the fault was induced and the invariant did not hold.</summary>
    /// <param name="number">The invariant, 1 to 7.</param>
    /// <param name="detail">What was asserted and what was measured instead.</param>
    /// <param name="numbers">Everything measured, for the dated table.</param>
    public void Violated(int number, string detail, IReadOnlyDictionary<string, double> numbers) =>
        Record(new(number, InvariantStatus.Violated, detail, numbers));

    /// <summary>Records that the fault could not be induced here, and why.</summary>
    /// <param name="number">The invariant, 1 to 7.</param>
    /// <param name="reason">What the machine lacks, precisely enough that the reader knows what would change it.</param>
    /// <param name="numbers">Anything that was measured on the way to that conclusion, or empty.</param>
    public void Vacuous(int number, string reason, IReadOnlyDictionary<string, double>? numbers = null) =>
        Record(
            new(
                number,
                InvariantStatus.Vacuous,
                reason,
                numbers ?? new Dictionary<string, double>(StringComparer.Ordinal)
            )
        );

    /// <summary>Where the file goes: the build's variable, or beside the host.</summary>
    public static string ResultsPath =>
        Environment.GetEnvironmentVariable(ResultsPathVariable) is { Length: > 0 } configured
            ? configured
            : Path.Combine(AppContext.BaseDirectory, "chaos-results.json");

    /// <summary>Writes the rows as JSON, creating the directory if the build has not.</summary>
    /// <param name="topology">A few facts about what the invariants ran against, for the table's caption.</param>
    public void Write(IReadOnlyDictionary<string, string> topology) {
        ArgumentNullException.ThrowIfNull(topology);

        var invariants = new JsonObject();

        foreach (var outcome in outcomes.Values.OrderBy(static x => x.Number)) {
            var numbers = new JsonObject();

            foreach (var (name, value) in outcome.Numbers.OrderBy(static x => x.Key, StringComparer.Ordinal)) {
                numbers[name] = value;
            }

            invariants[outcome.Number.ToString(CultureInfo.InvariantCulture)] = new JsonObject {
                ["status"] = outcome.Status.ToString(), ["detail"] = outcome.Detail, ["numbers"] = numbers
            };
        }

        var facts = new JsonObject();

        foreach (var (name, value) in topology.OrderBy(static x => x.Key, StringComparer.Ordinal)) {
            facts[name] = value;
        }

        var root = new JsonObject {
            ["measuredAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["topology"] = facts,
            ["invariants"] = invariants
        };

        var path = ResultsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[CyberCloud.Chaos] {outcomes.Count} invariant outcome(s) written to {path}");
    }

    void Record(InvariantOutcome outcome) {
        outcomes[outcome.Number] = outcome;

        Console.WriteLine($"[CyberCloud.Chaos] invariant {outcome.Number}: {outcome.Status} — {outcome.Detail}");
    }
}
