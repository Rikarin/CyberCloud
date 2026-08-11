using CyberCloud.Cli.Configuration;

namespace CyberCloud.Cli.Tests;

/// <summary>
///     The two rows of docs/plan/21 § Decisions that are about trust rather than about function.
/// </summary>
/// <remarks>
///     ⚠ <i>"Telemetry | <b>Opt-in, off by default, and asked once.</b> Opt-out telemetry in a
///     developer tool is a trust cost that is never worth the data."</i> and <i>"Update check | Once a
///     day, non-blocking, never auto-installs."</i> Both are easy to write down and easy to implement
///     slightly wrong, which is what these assert.
/// </remarks>
public sealed class TelemetryAndUpdateTests {
    [Fact]
    public async Task TelemetryIsOffWithoutAnyConfiguration() {
        using var host = TestHost.Create();

        await host.RunAsync("config", "telemetry", "--output", "json");

        using var document = host.StdoutAsJson();
        document.RootElement.GetProperty("telemetry").GetString().ShouldBe("off");
    }

    [Fact]
    public async Task TheQuestionIsRecordedWithoutBeingAskedWhenNobodyCanAnswer() {
        using var host = TestHost.Create();

        // stderr is redirected in every test, which is what a CI job looks like. ⚠ A prompt here
        // would be a hung build, so the answer is recorded as "off" and never asked again.
        await host.RunAsync("config", "list", "--output", "none");

        var written = CycConfigFile.Read(Path.Combine(host.StateDirectory, "config"));
        written.Value(CycConfigFile.DefaultProfileName, TelemetryConsent.Key).ShouldBe("off");
    }

    [Fact]
    public void TheQuestionIsPutOnceAndTheAnswerSticks() {
        using var host = TestHost.Create();
        var asked = 0;

        var settings = CycSettings.Resolve(host.Host.Config, host.Host.Environment, profileFlag: null);

        TelemetryConsent.EnsureAsked(host.Host, settings, interactive: true, () => {
            asked++;

            return "y";
        });

        asked.ShouldBe(1);
        host.Stderr.ShouldContain("Send telemetry?");

        // Re-resolve against the file that was just written; the question is not put again.
        var after = CycSettings.Resolve(
            CycConfigFile.Read(Path.Combine(host.StateDirectory, "config")),
            host.Host.Environment,
            profileFlag: null);

        TelemetryConsent.IsEnabled(after).ShouldBeTrue();

        TelemetryConsent.EnsureAsked(host.Host, after, interactive: true, () => {
            asked++;

            return "y";
        });

        asked.ShouldBe(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("n")]
    [InlineData("no")]
    [InlineData(null)]
    [InlineData("maybe")]
    public void AnythingThatIsNotYesIsNo(string? answer) {
        using var host = TestHost.Create();
        var settings = CycSettings.Resolve(host.Host.Config, host.Host.Environment, profileFlag: null);

        TelemetryConsent.EnsureAsked(host.Host, settings, interactive: true, () => answer);

        var after = CycSettings.Resolve(
            CycConfigFile.Read(Path.Combine(host.StateDirectory, "config")),
            host.Host.Environment,
            profileFlag: null);

        TelemetryConsent.IsEnabled(after).ShouldBeFalse();
    }

    [Fact]
    public void TheUpdateCheckRunsAtMostOnceADay() {
        var clock = new TestClock(DateTimeOffset.Parse("2026-08-11T09:00:00Z", CultureInfo.InvariantCulture));
        using var host = TestHost.Create(time: clock);

        UpdateCheck.IsDue(host.Host).ShouldBeTrue();

        UpdateCheck.Start(host.Host, "1.0.0");
        UpdateCheck.IsDue(host.Host).ShouldBeFalse();

        clock.Advance(TimeSpan.FromHours(23));
        UpdateCheck.IsDue(host.Host).ShouldBeFalse();

        clock.Advance(TimeSpan.FromHours(2));
        UpdateCheck.IsDue(host.Host).ShouldBeTrue();
    }

    [Fact]
    public async Task TheUpdateCheckReportsAndNeverInstalls() {
        var clock = new TestClock(DateTimeOffset.Parse("2026-08-11T09:00:00Z", CultureInfo.InvariantCulture));
        using var host = TestHost.Create(time: clock);

        await UpdateCheck.Start(host.Host, "1.0.0", _ => Task.FromResult<string?>("1.1.0"));

        host.Stderr.ShouldContain("1.1.0 is available");

        // ⚠ Nothing else happened. The one thing this feature must never do is write an executable,
        // and the only file it leaves is the stamp.
        Directory.GetFiles(host.StateDirectory).Select(Path.GetFileName).ShouldBe(["update-check"]);
    }

    [Fact]
    public async Task AFailingUpdateCheckIsNotNews() {
        var clock = new TestClock(DateTimeOffset.Parse("2026-08-11T09:00:00Z", CultureInfo.InvariantCulture));
        using var host = TestHost.Create(time: clock);

        await UpdateCheck.Start(host.Host, "1.0.0", _ => throw new HttpRequestException("no route to host"));

        host.Stderr.ShouldBeEmpty();
    }

    [Fact]
    public void TheUpdateCheckCanBeTurnedOffEntirely() {
        using var host = TestHost.Create(
            environment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["CYC_NO_UPDATE_CHECK"] = "1" });

        UpdateCheck.Start(host.Host, "1.0.0", _ => throw new ShouldAssertException("the probe ran"));

        Directory.GetFiles(host.StateDirectory).ShouldBeEmpty();
    }
}
