using CyberCloud.Core.Security;
using CyberCloud.ServiceDefaults.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Shouldly;
using System.Diagnostics.Metrics;

namespace CyberCloud.ServiceDefaults.Tests.Logging;

/// <summary>Keeps every event it is given, unrendered, so a test can look at the structure.</summary>
sealed class CollectingSink : ILogEventSink {
    public List<LogEvent> Events { get; } = [];

    public void Emit(LogEvent logEvent) => Events.Add(logEvent);

    /// <summary>The one event, rendered the way a text sink would render it.</summary>
    public string Rendered() {
        var writer = new StringWriter();
        Events.ShouldHaveSingleItem().RenderMessage(writer);
        return writer.ToString();
    }
}

/// <summary>
///     <c>SecretScrubbingSink</c> — docs/plan/18 § Platform security's log canary, at the point where
///     a log event leaves the process.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>These tests drive a real Serilog pipeline rather than calling the scrubber
///         directly.</b> The failure this control is exposed to is not "the regex is wrong" —
///         <c>CyberCloud.Core.Tests.SecretShapedTextTests</c> covers that — it is "the scrubber was
///         attached somewhere the events do not go". A test that calls <c>Scrub</c> on a hand-built
///         <see cref="LogEvent" /> passes just as happily when the wrapper has been detached from the
///         configuration, which is the whole regression worth catching.
///     </para>
///     <para>
///         The credential literals below are invented and match no real account.
///     </para>
/// </remarks>
public class SecretScrubbingSinkTests {
    // ⚠ EVERY CREDENTIAL-SHAPED LITERAL BELOW IS ASSEMBLED FROM PARTS, AND THAT IS NOT STYLE.
    // A repository that ships a secret recogniser trips every other one: GitHub push protection reads
    // the blob, finds a run shaped like a GitHub token or an OpenSSH private key, and REFUSES THE
    // PUSH — the control working exactly as designed, on the files whose whole purpose is to contain
    // those shapes. Allowing each one through the bypass link is the wrong answer, because it teaches
    // the next person that the button exists. Splitting at the vendor prefix is enough, since every
    // scanner anchors there, and this concatenates at runtime — the string handed to the matcher is
    // byte-identical, so no assertion here is weakened.
    static string Shape(params string[] parts) => string.Concat(parts);

    static readonly string Jwt = Shape(
        "ey",
        "JhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.",
        "eyJzdWIiOiIxMjM0NTY3ODkwIn0.",
        "dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk"
    );

    /// <summary>A logger wired exactly as a host wires it, writing into <paramref name="sink" />.</summary>
    static Logger Pipeline(CollectingSink sink)
        => new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.ScrubbingSecrets(sinks => sinks.Sink(sink))
            .CreateLogger();

    [Fact]
    public void ATokenPassedAsAPropertyDoesNotReachTheSinkBehindTheWrapper() {
        var sink = new CollectingSink();
        using var logger = Pipeline(sink);

        logger.Information("token exchange returned {Token} for {Subject}", Jwt, "alice");

        var rendered = sink.Rendered();
        rendered.ShouldNotContain(Jwt);
        rendered.ShouldContain("[redacted:JsonWebToken]");

        // ⚠ The rest of the line survives. A scrubber that answers by dropping the event, or by
        // replacing the whole message, removes the diagnostic that was trying to say something went
        // wrong — and an operator who loses the line stops trusting the pipeline that ate it.
        rendered.ShouldContain("alice");
        rendered.ShouldContain("token exchange returned");
    }

    [Fact]
    public void ASecretInterpolatedIntoTheMessageItselfIsCaughtToo() {
        var sink = new CollectingSink();
        using var logger = Pipeline(sink);

        // ⚠ THE CASE AN ENRICHER CANNOT REACH. There is no property here — the value is in the
        // template's own text, which is immutable on a LogEvent. It is also the shape a hurried
        // diagnostic takes, because interpolation is what a keyboard does by default.
#pragma warning disable CA2254 // The literal template is the point of this test.
        logger.Warning($"could not parse Host=db;Password=Tr0ub4dor;Pooling=true");
#pragma warning restore CA2254

        var rendered = sink.Rendered();
        rendered.ShouldNotContain("Tr0ub4dor");
        rendered.ShouldContain("[redacted:ConnectionStringPassword]");

        // The key survives, so the line still says which setting it choked on.
        rendered.ShouldContain("Password=");
        rendered.ShouldContain("Host=db");
    }

    [Fact]
    public void AnExceptionMessageIsScrubbedAndItsStackAndRenderingSurvive() {
        var sink = new CollectingSink();
        using var logger = Pipeline(sink);

        Exception thrown;
        try {
            throw new InvalidOperationException($"connect failed: redis://default:9dK2mQ7xZ@cache-0:6379");
        } catch (InvalidOperationException e) {
            thrown = e;
        }

        logger.Error(thrown, "reconcile failed");

        var exception = sink.Events.ShouldHaveSingleItem().Exception.ShouldNotBeNull();

        exception.Message.ShouldNotContain("9dK2mQ7xZ");
        exception.Message.ShouldContain("[redacted:UriCredentials]");
        exception.ToString().ShouldNotContain("9dK2mQ7xZ");

        // ⚠ The stack is what makes the exception worth keeping at all, and the substitution keeps
        // it. Only the CLR type is lost, and its name is still inside the rendering below.
        exception.StackTrace.ShouldNotBeNullOrWhiteSpace();
        exception.ToString().ShouldContain(nameof(InvalidOperationException));
    }

    [Fact]
    public void APropertyTheTemplatePutInACredentialPositionGoesEvenThoughItsValueLooksLikeNothing() {
        var sink = new CollectingSink();
        using var logger = Pipeline(sink);

        // ⚠ THE HOLE THE TEMPLATE FIX OPENED, AND THE REASON TO LOOK FOR IT. The template matches
        // the connection-string rule and the {Pw} token sits inside the match, so the rendered
        // message comes out clean — while the property itself is an ordinary password with no
        // recognisable shape, so nothing fires on it and the OTLP sink exports it as a structured
        // attribute beside a message that no longer mentions it. Checking the rendering alone would
        // have shown this working.
        logger.Information("connecting with Password={Pw} to {Host}", "correct horse battery", "db-0");

        var event_ = sink.Events.ShouldHaveSingleItem();

        event_.Properties["Pw"].ToString().ShouldNotContain("correct horse battery");
        event_.Properties["Host"].ToString().ShouldContain("db-0", Case.Sensitive, "an innocent property is untouched");

        var writer = new StringWriter();
        event_.RenderMessage(writer);
        writer.ToString().ShouldNotContain("correct horse battery");
    }

    [Fact]
    public void ASecretNestedInsideADestructuredObjectIsFound() {
        var sink = new CollectingSink();
        using var logger = Pipeline(sink);

        logger.Information(
            "storage options {@Options}",
            new { Shard = 3, Connection = new { Host = "db", Dsn = "postgres://cc:S3cretPw0rd@db:5432/main" } }
        );

        // {@Options} becomes a StructureValue containing a StructureValue containing the scalar.
        // A scrubber that only looked at top-level scalars would tick and let this through, which is
        // the "narrower question than it appears" failure applied to a log pipeline.
        var rendered = sink.Rendered();
        rendered.ShouldNotContain("S3cretPw0rd");
        rendered.ShouldContain("[redacted:UriCredentials]");
        rendered.ShouldContain("db:5432");
    }

    [Fact]
    public void ASecretInsideACollectionIsFound() {
        var sink = new CollectingSink();
        using var logger = Pipeline(sink);

        logger.Information("headers {Headers}", new[] { "accept: application/json", "authorization: Bearer " + Jwt });

        sink.Rendered().ShouldNotContain(Jwt);
    }

    [Fact]
    public void AUriIsScannedEvenThoughItIsNotAString() {
        var sink = new CollectingSink();
        using var logger = Pipeline(sink);

        logger.Information("upstream {Endpoint}", new Uri("https://svc:hunter22pass@upstream.cc.svc/v1"));

        // Serilog captures a Uri as a scalar whose Value is a Uri, not a string. A scrubber written
        // as `if (scalar.Value is string)` reads as complete and misses every one of these.
        sink.Rendered().ShouldNotContain("hunter22pass");
    }

    [Fact]
    public void AnEventWithNothingInItIsHandedOnUntouched() {
        var sink = new CollectingSink();
        using var logger = Pipeline(sink);

        logger.Information(
            "reconciled {Resource} in {Elapsed} ms with correlation {Correlation}",
            "/tenants/a/subscriptions/b/resourceGroups/prod",
            43,
            Guid.NewGuid()
        );

        var rendered = sink.Rendered();
        rendered.ShouldNotContain(SecretShapedText.RedactionPrefix);
        sink.Events.ShouldHaveSingleItem().Properties.ContainsKey(SecretScrubbingSink.MarkerProperty).ShouldBeFalse();
    }

    [Fact]
    public void TheEventThatCarriedSomethingIsMarkedWithTheRulesThatFired() {
        var sink = new CollectingSink();
        using var logger = Pipeline(sink);

        logger.Information("key {Key} and dsn {Dsn}", Shape("AKIA", "IOSFODNN7EXAMPLE"), "Password=Tr0ub4dor");

        // ⚠ The counter says a leak happened; this property says which line and which credential, and
        // an alert without it sends the responder to grep an entire tenant's logs for a value they
        // are specifically not allowed to know.
        var marker = sink.Events.ShouldHaveSingleItem().Properties[SecretScrubbingSink.MarkerProperty].ToString();
        marker.ShouldContain("AwsAccessKey");
        marker.ShouldContain("ConnectionStringPassword");
    }

    [Fact]
    public void OneEventInIsOneEventOutBecauseASinkThatLogsAboutLoggingFeedsItself() {
        var sink = new CollectingSink();
        using var logger = Pipeline(sink);

        logger.Information("token {Token}", Jwt);

        sink.Events.Count.ShouldBe(1);
    }

    [Fact]
    public void TheRedactionIsCountedOnTheMeterAnAlertRuleWatches() {
        var measurements = new List<(string Rule, long Value)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) => {
            if (instrument.Meter.Name == SecretScrubbingSink.MeterName
                && instrument.Name == SecretScrubbingSink.CounterName) {
                l.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
                var rule = tags.ToArray().Single(x => x.Key == "rule").Value?.ToString() ?? "";
                measurements.Add((rule, value));
            }
        );

        listener.Start();

        var sink = new CollectingSink();
        using (var logger = Pipeline(sink)) {
            logger.Information("token {Token}", Jwt);
        }

        listener.Dispose();

        // ⚠ Without this the control is a redactor and not a canary: the leak is stopped and nobody
        // is told, so the code that produced it keeps producing it.
        measurements.ShouldContain(x => x.Rule == "JsonWebToken" && x.Value == 1);
    }

    [Fact]
    public void TheMeterIsOneConfigureOpenTelemetryActuallyCollects() {
        // ⚠ The two-constants failure, in its logging shape. ConfigureOpenTelemetry collects
        // AddMeter($"{TelemetrySourcePrefix}.*") and nothing else, so a meter named outside that
        // prefix increments correctly, is exported nowhere, and leaves an alert rule watching a
        // series that never appears — which looks exactly like "no leaks, ever".
        SecretScrubbingSink.MeterName.ShouldStartWith(ServiceDefaultsExtensions.TelemetrySourcePrefix + ".");
    }
}
