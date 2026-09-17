using CyberCloud.Core;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Seams;
using CyberCloud.Identity.SignIn;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CyberCloud.Identity.Tests;

/// <summary>
///     The Development-only delivery seam: refuses to exist anywhere else, writes the code where a
///     developer reads it, and keeps the address out of the message.
/// </summary>
/// <remarks>
///     ⚠ <b>The first row is the whole safety story and the other two are what it protects.</b> A
///     seam that logs one-time codes is acceptable on a laptop (#93) and a disaster on a silo
///     serving customers; the constructor is the gate, keyed on the environment rather than a
///     setting for the reason <c>DevelopmentOtpDelivery</c> gives. The third row is
///     docs/plan/11 § Auditing's rule applied to the one template in <c>IdentityLog</c> that
///     renders a credential: the code is in the line and the address is not. The last two rows are
///     the relay half: with Mailpit on the AppHost the code is mailed as well as logged, and a mail
///     the relay refuses is a warning beside the code rather than a failed sign-up.
/// </remarks>
public sealed class DevelopmentOtpDeliveryTests {
    static readonly OtpDelivery Delivery = new() {
        TenantId = Guid.Empty,
        UserId = Guid.Parse("7f3a1c2e-5d4b-4a69-8c7d-0e1f2a3b4c5d"),
        Purpose = OtpPurpose.Enrolment,
        Kind = CredentialKind.EmailOtp,
        Destination = "wilhelmina.featherstonehaugh@example.com",
        Code = "482913"
    };

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("")]
    public void RefusesToConstructOutsideDevelopment(string environmentName) {
        var refusal = Should.Throw<InvalidOperationException>(() => new DevelopmentOtpDelivery(
                new FixedEnvironment(environmentName),
                new CapturingLogger()
            )
        );

        // The sentence names the seam an operator should have wired instead.
        refusal.Message.ShouldContain("CyberCloud:Identity:OtpDelivery");
        refusal.Message.ShouldContain(nameof(CommunicationOtpDelivery));
    }

    [Fact]
    public async Task LogsTheCodeAtWarning() {
        var logger = new CapturingLogger();
        var seam = new DevelopmentOtpDelivery(new FixedEnvironment(Environments.Development), logger);

        var delivered = await seam.DeliverAsync(Delivery, TestContext.Current.CancellationToken);

        delivered.IsSuccess.ShouldBeTrue("a logged code is a delivered code, on a development run");

        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning, "it has to stand out in a console");
        entry.EventId.ShouldBe(1113);
        entry.Message.ShouldContain("DEVELOPMENT OTP", Case.Sensitive, "the marker the dashboard filter finds");
        entry.Message.ShouldContain(Delivery.Code);
        entry.Message.ShouldContain(Delivery.UserId.ToString());
        entry.Message.ShouldContain(nameof(OtpPurpose.Enrolment));
        entry.Message.ShouldContain(nameof(CredentialKind.EmailOtp));
    }

    [Fact]
    public async Task TheAddressIsAStructuredPropertyAndNeverInTheMessage() {
        var logger = new CapturingLogger();
        var seam = new DevelopmentOtpDelivery(new FixedEnvironment(Environments.Development), logger);

        await seam.DeliverAsync(Delivery, TestContext.Current.CancellationToken);

        var entry = logger.Entries.ShouldHaveSingleItem();

        // ⚠ docs/plan/11 § Auditing: "no email, name or IP in a log message. They go in structured
        // fields". Both halves: the rendered line carries no part of the address, and the scope the
        // seam opened carries the whole of it under the property name a redaction policy can target.
        entry.Message.ShouldNotContain("wilhelmina", Case.Insensitive);
        entry.Message.ShouldNotContain("featherstonehaugh", Case.Insensitive);
        entry.Message.ShouldNotContain("example.com", Case.Insensitive);

        entry.State.ShouldNotContainKey(DevelopmentOtpDelivery.DestinationProperty, "the address is not a template argument");
        entry.Scope.ShouldContainKeyAndValue(DevelopmentOtpDelivery.DestinationProperty, Delivery.Destination);
    }

    // ── The relay half (#93): logged AND mailed, and the mail failing does not fail the delivery ──

    [Fact]
    public async Task WithARelayTheCodeIsLoggedAndMailedThroughTheInnerSeam() {
        var logger = new CapturingLogger();
        var mail = new RecordingSeam(Result.Success);
        var seam = new DevelopmentOtpDelivery(new FixedEnvironment(Environments.Development), logger, mail);

        seam.AlsoMails.ShouldBeTrue();

        var delivered = await seam.DeliverAsync(Delivery, TestContext.Current.CancellationToken);

        delivered.IsSuccess.ShouldBeTrue();
        mail.Deliveries.ShouldHaveSingleItem().ShouldBeSameAs(Delivery, "the same delivery, code and all, reaches the inbox");

        // ⚠ The console line is kept: a person reading the dashboard should still find the code.
        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.EventId.ShouldBe(1113);
        entry.Message.ShouldContain(Delivery.Code);
    }

    [Fact]
    public async Task AMailThatIsRefusedIsAWarningBesideTheCodeAndNotAFailedDelivery() {
        var logger = new CapturingLogger();
        var mail = new RecordingSeam(Result.Failure(ErrorCode.InternalError, "The relay 127.0.0.1:1025 could not be spoken to: SocketException"));
        var seam = new DevelopmentOtpDelivery(new FixedEnvironment(Environments.Development), logger, mail);

        var delivered = await seam.DeliverAsync(Delivery, TestContext.Current.CancellationToken);

        // ⚠ In Development the log line IS a delivery. A Mailpit that has not come up yet must not
        // turn a sign-up into "something went wrong" while the code sits on the console.
        delivered.IsSuccess.ShouldBeTrue();

        logger.Entries.Count.ShouldBe(2);
        logger.Entries[0].EventId.ShouldBe(1113, "the code first");
        logger.Entries[0].Message.ShouldContain(Delivery.Code);

        logger.Entries[1].EventId.ShouldBe(1122);
        logger.Entries[1].Level.ShouldBe(LogLevel.Warning);
        logger.Entries[1].Message.ShouldContain("NOT mailed");
        logger.Entries[1].Message.ShouldContain("could not be spoken to");
        logger.Entries[1].Message.ShouldNotContain("wilhelmina", Case.Insensitive, "still no address in a message");
    }

    /// <summary>An inner seam that records what it was handed and answers what it was told to.</summary>
    sealed class RecordingSeam(Result answer) : IOtpDeliverySeam {
        public List<OtpDelivery> Deliveries { get; } = [];

        public Task<Result> DeliverAsync(OtpDelivery delivery, CancellationToken cancellationToken = default) {
            Deliveries.Add(delivery);
            return Task.FromResult(answer);
        }
    }

    /// <summary>An <see cref="IHostEnvironment" /> with one name and nothing else.</summary>
    sealed class FixedEnvironment(string name) : IHostEnvironment {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = ".";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>One captured log call — its level, id, rendered message, template state and scope.</summary>
    public sealed record CapturedEntry(
        LogLevel Level,
        int EventId,
        string Message,
        IReadOnlyDictionary<string, object?> State,
        IReadOnlyDictionary<string, object?> Scope
    );

    /// <summary>
    ///     Captures every log call with the scope that was open around it — the only way to see the
    ///     structured property the seam puts the address in.
    /// </summary>
    sealed class CapturingLogger : ILogger<DevelopmentOtpDelivery> {
        readonly Stack<IReadOnlyDictionary<string, object?>> scopes = new();

        public List<CapturedEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull {
            var properties = state is IEnumerable<KeyValuePair<string, object>> pairs
                ? pairs.ToDictionary(x => x.Key, x => (object?)x.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);

            scopes.Push(properties);
            return new Pop(scopes);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) {
            ArgumentNullException.ThrowIfNull(formatter);

            var properties = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);

            var scope = scopes.Count == 0
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : scopes.Peek();

            Entries.Add(new(logLevel, eventId.Id, formatter(state, exception), properties, scope));
        }

        sealed class Pop(Stack<IReadOnlyDictionary<string, object?>> scopes) : IDisposable {
            public void Dispose() => scopes.Pop();
        }
    }
}
