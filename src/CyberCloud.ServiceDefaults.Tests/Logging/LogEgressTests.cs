using CyberCloud.ServiceDefaults.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CyberCloud.ServiceDefaults.Tests.Logging;

/// <summary>
///     That a host has exactly one way of getting a log line out of the process, and that
///     <c>SecretScrubbingSink</c> is on it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THIS FILE IS THE DIFFERENCE BETWEEN A SCRUBBER AND A CONTROL.</b>
///         <see cref="SecretScrubbingSinkTests" /> proves the scrubber scrubs what it is given.
///         Neither that file nor any amount of coverage over it can tell you whether the process
///         actually routes its log lines through it — and a second egress beside the scrubbed one is
///         not a partial control, it is no control, because the leak takes whichever path is not
///         watched. docs/plan/18 § Platform security asks for a canary "in the log pipeline",
///         singular, and these tests are what makes the singular true.
///     </para>
///     <para>
///         Both host factories are exercised. The wiring is shared, which is exactly the reason to
///         check both: a shared helper is the kind of thing that gets bypassed in one caller.
///     </para>
///     <para>
///         ⚠ The host builders cannot be <c>Build()</c>, because ABP demands a registered module at
///         that point (see <see cref="OrleansApplication" />). The service collection is built
///         directly instead, which is enough: everything asserted here is a property of the
///         registrations, and the one behavioural assertion resolves the logger from them and uses
///         it.
///     </para>
/// </remarks>
public class LogEgressTests {
    /// <summary>Every host builder in the platform, by the name a failure should name.</summary>
    public static TheoryData<string> Hosts => ["silo", "client"];

    static WebApplicationBuilder Build(string host, params string[] args)
        => host == "silo"
            ? OrleansApplication.CreateSilo([.. Args(args)])
            : OrleansApplication.CreateClient([.. Args(args)]);

    static IEnumerable<string> Args(IEnumerable<string> extra) => ["--environment", "Development", .. extra];

    const string Jwt =
        "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk";

    [Theory]
    [MemberData(nameof(Hosts))]
    public void EveryLoggerInTheProcessIsASerilogLogger(string host) {
        var builder = Build(host);

        using var services = builder.Services.BuildServiceProvider();

        // ⚠ THE FACTORY, NOT THE PROVIDER LIST, IS WHAT MAKES THE EGRESS SINGULAR — and finding
        // that out is what corrected this file. `AddSerilog` with the default
        // `writeToProviders: false` REPLACES `ILoggerFactory` with Serilog's, so
        // Microsoft.Extensions.Logging's own factory is never constructed and no registered
        // `ILoggerProvider` is ever consumed. That is why `ConfigureOpenTelemetry`'s
        // `builder.Logging.AddOpenTelemetry(...)` had never exported a single log record: it was not
        // a second unscrubbed egress, it was dead configuration that read like a live one. Asserting
        // the factory says the true thing; asserting the provider count alone would have been a
        // green tick over a claim nobody had checked.
        services.GetRequiredService<ILoggerFactory>()
            .GetType()
            .FullName
            .ShouldBe(
                "Serilog.Extensions.Logging.SerilogLoggerFactory",
                $"the {host} host's ILogger calls must all land in Serilog, which is the only pipeline "
                + "SecretScrubbingSink is on"
            );

        // ClearProviders() as belt to that brace. The descriptors it removes are inert for the
        // reason above, so this is not what closes the hole — it keeps the service collection from
        // describing egress paths that do not exist, so that the next reader of this wiring is not
        // told the same untrue thing this comment used to tell.
        builder.Services.Count(x => x.ServiceType == typeof(ILoggerProvider)).ShouldBe(0);
    }

    [Fact]
    public void ASecretLoggedThroughAHostsOwnLoggerDoesNotReachStandardOutput() {
        // ⚠ THE ONE END-TO-END ASSERTION, AND THE ONLY ONE THAT WOULD NOTICE THE WRAPPER BEING
        // DETACHED. Everything above is about wiring; this drives the wiring. `ILogger` in, the
        // process's real stdout out — which in a cluster is what the node's log collector reads and
        // is therefore the pipeline docs/plan/18 § Platform security names.
        var captured = new StringWriter();
        var real = Console.Out;

        try {
            Console.SetOut(captured);

            var builder = Build("client");
            using var services = builder.Services.BuildServiceProvider();

            services.GetRequiredService<ILoggerFactory>()
                .CreateLogger("canary")
                .LogInformation("upstream returned {Token} for {Subject}", Jwt, "alice");
        } finally {
            Console.SetOut(real);
        }

        var stdout = captured.ToString();

        // ⚠ The line must be THERE. A test that only asserted the token's absence would pass
        // identically if nothing had been logged at all, which is the shape of a security test that
        // is really asserting the absence of the feature.
        stdout.ShouldContain("upstream returned");
        stdout.ShouldContain("alice");

        stdout.ShouldNotContain(Jwt);
        stdout.ShouldContain("[redacted:JsonWebToken]");
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void AHostRefusesToStartWhenConfigurationDeclaresASink(string host) {
        // The regression this exists for: somebody adds a file sink or a second collector through a
        // Helm values override, because that is how every other Serilog setting is changed. It would
        // be added beside the wrapper rather than behind it and would export unredacted, and nothing
        // would have gone red.
        var refusal = Should.Throw<InvalidOperationException>(
            () => Build(host, "--Serilog:WriteTo:0:Name=Console")
        );

        refusal.Message.ShouldContain("Serilog:WriteTo");
        refusal.Message.ShouldContain("docs/plan/18");
    }

    [Fact]
    public void AuditToIsRefusedToo() {
        // AuditTo is the other spelling that creates a sink. A check that knew only about WriteTo
        // would be a control over the common case wearing the clothes of a control.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new("Serilog:AuditTo:0:Name", "Console")])
            .Build();

        Should.Throw<InvalidOperationException>(() => LogEgress.RefuseConfiguredSinks(configuration))
            .Message.ShouldContain("Serilog:AuditTo");
    }

    [Fact]
    public void EverythingElseSerilogReadsFromConfigurationStillWorks() {
        // ⚠ The refusal must be narrow. Levels, per-source overrides, enrichers, filters and
        // destructuring are how an operator tunes logging in an incident, and a control that took
        // those away as collateral would be worked around rather than lived with.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([
                    new("Serilog:MinimumLevel:Default", "Debug"),
                    new("Serilog:MinimumLevel:Override:Orleans", "Warning"),
                    new("Serilog:Properties:Application", "cybercloud")
                ]
            )
            .Build();

        Should.NotThrow(() => LogEgress.RefuseConfiguredSinks(configuration));
    }

    [Fact]
    public void TheShippedAppsettingsFilesDeclareNoSink() {
        // The two files this change edited, read as a host reads them. Without this, the assertion
        // above is about a configuration a test invented, and the actual hosts could ship a
        // Serilog:WriteTo that only ever fails at deploy time.
        foreach (var file in ShippedAppsettings()) {
            var configuration = new ConfigurationBuilder().AddJsonFile(file).Build();

            Should.NotThrow(
                () => LogEgress.RefuseConfiguredSinks(configuration),
                $"{file} declares a Serilog sink, so the host that reads it cannot start"
            );
        }
    }

    /// <summary>Every <c>appsettings.json</c> under <c>src/Hosts</c>, found from the test binary.</summary>
    static List<string> ShippedAppsettings() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Hosts"))) {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("the repository root was not found above the test binary");

        var files = Directory
            .GetFiles(Path.Combine(directory.FullName, "src", "Hosts"), "appsettings.json", SearchOption.AllDirectories)
            .ToList();

        // ⚠ Vacuity guard. A glob that matched nothing would make this test a green tick over an
        // empty set, which is the failure this repository prints as ○ Vacuous rather than ✔.
        files.Count.ShouldBeGreaterThan(1);
        return files;
    }
}
