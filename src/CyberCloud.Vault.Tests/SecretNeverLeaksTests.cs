using CyberCloud.Vault.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace CyberCloud.Vault.Tests;

/// <summary>
///     A real secret is read out of a real OpenBao, and then every place it could have been written
///     is searched for it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FAILURE CLASS IS A SECRET IN A LOG, A TRACE OR AN EXCEPTION MESSAGE, AND ALL
///         THREE ARE PLACES THE VALUE ARRIVES BY ACCIDENT RATHER THAN BY DESIGN.</b> Nobody writes
///         <c>logger.LogInformation(password)</c>. What happens is that a diagnostic grows: an
///         operator detail starts quoting a response body, an <c>Activity</c> tag is added "to see
///         what came back", an exception message includes the JSON it failed on. docs/plan/18 §
///         Platform security asks for exactly this — <i>"never in a log — analyzer + admission policy
///         + a log-scanning canary"</i> — and this is the canary at the source.
///     </para>
///     <para>
///         ⚠ <b>The value is distinctive on purpose.</b> A password of <c>hunter2</c> could appear in
///         a log by coincidence and a search for it would be noise. The one below cannot appear
///         anywhere except by having travelled.
///     </para>
///     <para>
///         ⚠ <b>The success path is checked, not only the failure paths, and that is the harder
///         half.</b> A failure never has the value; the successful read is the one call where the
///         value exists in the process, and it is the call that writes the audit line.
///     </para>
/// </remarks>
[Collection(OpenBaoSuite.Name)]
public sealed class SecretNeverLeaksTests(OpenBaoFixture vault) {
    const string Path = "tenants/leak-canary/postgres/main";
    const string Password = "zQ7-canary-must-not-appear-anywhere-4Xp";

    [Fact]
    public async Task ASuccessfulReadWritesAnAuditLineAndTheAuditLineIsNotTheSecret() {
        var (token, logs, activities) = await Read("adminPassword");

        token.IsSuccess.ShouldBeTrue(token.Error?.Message);
        token.GetValueOrThrow().ShouldBe(Password, "the test is worthless if the value never arrived");

        // ⚠ The audit line must EXIST — docs/plan/18 audits every read "with the caller, the
        // correlation id and the secret name (never the value)". A leak test that passed because
        // nothing was logged at all would be asserting the absence of the feature.
        var audit = logs.ShouldHaveSingleItem();
        audit.ShouldContain(Path);
        audit.ShouldContain("adminPassword");

        Assert(logs, activities);
    }

    [Fact]
    public async Task AMissingFieldRefusalNamesTheKeysAndNotTheirValues() {
        // ⚠ VaultFailures.FieldMissing deliberately lists the field names PRESENT at the path,
        // because the fault is nearly always a spelling. The names sit right beside the values in the
        // same JSON object, so this is the refusal most likely to reach for one.
        var (result, logs, activities) = await Read("admin_password");

        result.IsFailure.ShouldBeTrue();
        string.Join("\n", logs).ShouldContain(
            "adminPassword",
            Case.Sensitive,
            "the operator detail lists the keys that ARE there, which is what turns a hunt into a "
            + "glance"
        );

        Assert(logs, activities);
    }

    [Fact]
    public async Task AResponseThisClientCannotParseIsNotQuotedBack() {
        await Seed();

        // ⚠ THE PATH THAT WOULD LEAK THE WHOLE SECRET AT ONCE, NOT ONE FIELD OF IT. Pointing the
        // client at the kv-v1-shaped read of a kv-v2 mount gives it a 200 whose body it cannot make
        // sense of — and that body is the entire secret. A malformed-response handler that echoed
        // what it could not parse would put every field and every value into the operator log.
        var options = vault.Options();
        options.KvMountPath = "sys";

        var logs = new CapturingLogger();

        var resolved = await vault.Resolver(OpenBaoFixture.RootToken, options, logs).ResolveAsync(
            new() { Path = "health", Field = "adminPassword" },
            TestContext.Current.CancellationToken
        );

        resolved.IsFailure.ShouldBeTrue();

        foreach (var line in logs.Lines) {
            line.ShouldNotContain("cluster_id", Case.Insensitive, "the response body must not be quoted");
        }
    }

    [Fact]
    public async Task NoExceptionEscapesTheResolverAtAll() {
        await Seed();

        // ⚠ An exception message is the third place a value travels, and the reason it is asserted
        // by ABSENCE OF THROWING rather than by inspecting a message: ISecretResolver returns a
        // Result, so a throw is already a contract violation. What it would additionally be is an
        // unredacted string crossing a grain boundary into an Orleans exception, which no
        // ReconcileOutcome would ever get the chance to sanitise.
        var options = vault.Options();
        options.Address = "http://127.0.0.1:1";

        await Should.NotThrowAsync(
            async () => await vault.Resolver("x", options).ResolveAsync(
                new() { Path = Path, Field = "adminPassword" },
                TestContext.Current.CancellationToken
            )
        );
    }

    static void Assert(IReadOnlyList<string> logs, IReadOnlyList<string> activities) {
        foreach (var line in logs) {
            line.ShouldNotContain(Password, Case.Insensitive, "a secret reached a log line");
        }

        foreach (var tag in activities) {
            tag.ShouldNotContain(Password, Case.Insensitive, "a secret reached a trace tag");
        }
    }

    async Task<(Result<string> Result, IReadOnlyList<string> Logs, IReadOnlyList<string> Activities)> Read(
        string field
    ) {
        await Seed();

        var logs = new CapturingLogger();
        var tags = new List<string>();

        // ⚠ A listener with Sample set to AllData, because a trace tag written on an unsampled
        // Activity is still a tag the exporter would have taken had the request been sampled. A test
        // that let the default sampling decide would pass on the runs where nothing was recorded.
        using var listener = new ActivityListener {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => {
                foreach (var tag in activity.TagObjects) {
                    tags.Add($"{tag.Key}={tag.Value}");
                }

                tags.Add(activity.DisplayName);
            },
        };

        ActivitySource.AddActivityListener(listener);

        using var source = new ActivitySource("CyberCloud.Vault.Tests");
        using var activity = source.StartActivity("resolve");

        var token = (await vault.IssueTokenAsync(["canary-reader"])).Token;

        var result = await vault.Resolver(token, logger: logs).ResolveAsync(
            new() { Path = Path, Field = field },
            TestContext.Current.CancellationToken
        );

        activity?.Stop();

        return (result, logs.Lines, tags);
    }

    async Task Seed() {
        await vault.WriteSecretAsync(
            Path,
            new Dictionary<string, string> { ["adminPassword"] = Password, ["username"] = "cc_admin" }
        );

        await vault.WritePolicyAsync(
            "canary-reader",
            $"path \"{OpenBaoFixture.KvMount}/data/{Path}\" {{ capabilities = [\"read\"] }}"
        );
    }
}

/// <summary>An <see cref="ILogger{T}" /> that keeps every formatted line.</summary>
/// <remarks>
///     ⚠ Keeps the FORMATTED line rather than the template, because a structured field is where a
///     value would ride: <c>LogInformation("read {Path}", path)</c> leaks nothing through its
///     template and everything through its argument. Formatting is what a console or an OTLP
///     exporter does with it.
/// </remarks>
public sealed class CapturingLogger : ILogger<OpenBaoSecretResolver> {
    readonly List<string> lines = [];

    /// <summary>Every line written, formatted.</summary>
    public IReadOnlyList<string> Lines => lines;

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull =>
        null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    ) {
        ArgumentNullException.ThrowIfNull(formatter);

        lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}
