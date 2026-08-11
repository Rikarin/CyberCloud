using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.SignIn;
using CyberCloud.Identity.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace CyberCloud.Identity.Tests;

/// <summary>
///     A logger that keeps every formatted message and every structured field it was given.
/// </summary>
/// <remarks>
///     ⚠ The distinction this captures is the whole point of docs/plan/11 § Auditing's rule.
///     <see cref="Messages" /> is what a sink renders into a log line — a string, subject to no
///     redaction policy, in every file and every screen that ever shows it. <see cref="Fields" /> is
///     the structured state, which the retention and redaction policy reaches. An address may be in
///     the second and never in the first.
/// </remarks>
public sealed class CapturingLogger<T> : ILogger<T> {
    /// <summary>Every rendered message.</summary>
    public ConcurrentQueue<string> Messages { get; } = new();

    /// <summary>Every structured field, as <c>name=value</c>.</summary>
    public ConcurrentQueue<string> Fields { get; } = new();

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

        Messages.Enqueue(formatter(state, exception));

        if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs) {
            foreach (var pair in pairs) {
                Fields.Enqueue($"{pair.Key}={pair.Value}");
            }
        }
    }
}

/// <summary>
///     docs/plan/11 § Auditing: <i>"no email, name or IP in a log <b>message</b>. They go in
///     structured fields, which are subject to the retention and redaction policy; a message string
///     is not."</i>
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This drives the real sign-in path rather than inspecting the templates.</b> A test
///         that read <c>IdentityLog</c>'s message constants would prove those constants are clean and
///         nothing about the code that calls them — and the way this rule breaks in practice is a
///         call site that interpolates, not a template that names a field.
///     </para>
///     <para>
///         The address used is deliberately distinctive rather than realistic, so a substring match
///         cannot coincide with anything else in a message. Both the whole address and its local part
///         are checked: <c>ZzQx-Marker</c> appearing anywhere means somebody logged part of an
///         address, which is the same disclosure in a smaller package.
///     </para>
/// </remarks>
[Collection(IdentitySuite.Name)]
public sealed class PiiNeverReachesALogMessageTests(IdentityCluster cluster) {
    /// <summary>The runner's token, so a hung test is cancellable — xUnit1051.</summary>
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    const string LocalPart = "ZzQx-Marker";
    const string Address = LocalPart + "@pii-probe.example";
    const string DisplayName = "Wilhelmina-Marker Featherstonehaugh";

    [Fact]
    public async Task NoSignInLogMessageContainsTheAddress() {
        var logger = new CapturingLogger<SignInService>();
        var lockout = new InMemoryLockoutCounter(TestClock.Instance);
        var service = cluster.SignIn(lockout, logger: logger);

        await cluster.CreateUserAsync(Address, "a-password-value");

        // Every branch of the path: a success, a wrong password, an unknown address, a lockout, and a
        // password reset. Each of them logs, and each of them has the address in hand.
        await service.SignInWithPasswordAsync(IdentityCluster.Tenant, Address, "a-password-value", new(), Ct);
        await service.SignInWithPasswordAsync(IdentityCluster.Tenant, Address, "wrong", new(), Ct);
        await service.SignInWithPasswordAsync(IdentityCluster.Tenant, "unknown-" + Address, "wrong", new(), Ct);
        await service.RequestPasswordResetAsync(IdentityCluster.Tenant, Address, Ct);
        await service.RequestPasswordResetAsync(IdentityCluster.Tenant, "unknown-" + Address, Ct);

        var key = LockoutKey.ForIdentifier(IdentityCluster.Tenant, Address);
        for (var i = 0; i <= LockoutPolicy.FreeAttempts; i++) {
            await lockout.RecordFailureAsync(key, Ct);
        }

        await service.SignInWithPasswordAsync(IdentityCluster.Tenant, Address, "anything", new(), Ct);

        var messages = logger.Messages.ToArray();

        messages.ShouldNotBeEmpty("the sign-in path must actually log, or this test proves nothing");

        foreach (var message in messages) {
            message.ShouldNotContain(
                Address,
                Case.Insensitive,
                $"A log MESSAGE carried an email address: '{message}'. docs/plan/11 § Auditing puts "
                + "addresses in structured fields, which the retention and redaction policy reaches; "
                + "a message string is not."
            );

            message.ShouldNotContain(
                LocalPart,
                Case.Insensitive,
                $"A log MESSAGE carried part of an email address: '{message}'. A local part is the "
                + "same disclosure in a smaller package."
            );
        }
    }

    [Fact]
    public async Task TheDigestIsWhatCorrelatesAttemptsAgainstOneAddress() {
        var logger = new CapturingLogger<SignInService>();
        var lockout = new InMemoryLockoutCounter(TestClock.Instance);
        var service = cluster.SignIn(lockout, logger: logger);

        await service.SignInWithPasswordAsync(IdentityCluster.Tenant, Address, "wrong", new(), Ct);

        var expected = LockoutKey.ForIdentifier(IdentityCluster.Tenant, Address).Value;

        // ⚠ The point of the rule is not "log less", it is "log the right thing". An operator asking
        // "how many attempts hit this address in the last hour" still gets an answer, from a value
        // that is one-way and per tenant.
        logger.Messages.ShouldContain(x => x.Contains(expected, StringComparison.Ordinal));

        expected.ShouldNotContain(LocalPart, Case.Insensitive);
    }

    [Fact]
    public async Task NoDisplayNameReachesALogMessage() {
        var logger = new CapturingLogger<SignInService>();
        var lockout = new InMemoryLockoutCounter(TestClock.Instance);
        var service = cluster.SignIn(lockout, logger: logger);

        var userId = Guid.NewGuid();
        const string email = "named-user@pii-probe.example";

        (await cluster.EmailIndex(email).TryClaimAsync(email, userId)).IsSuccess.ShouldBeTrue();
        (await cluster.User(userId).CreateAsync(email, DisplayName, UserStatus.Active)).IsSuccess.ShouldBeTrue();
        (await cluster.EmailIndex(email).ConfirmAsync(userId)).IsSuccess.ShouldBeTrue();
        (await cluster.User(userId).SetPasswordAsync("a-password-value")).IsSuccess.ShouldBeTrue();

        await service.SignInWithPasswordAsync(IdentityCluster.Tenant, email, "a-password-value", new(), Ct);

        foreach (var message in logger.Messages) {
            message.ShouldNotContain("Featherstonehaugh", Case.Insensitive);
            message.ShouldNotContain("Wilhelmina", Case.Insensitive);
        }
    }

    [Fact]
    public async Task NoClientAddressReachesALogMessageOrTheSessionState() {
        const string ip = "203.0.113.199";

        var logger = new CapturingLogger<SignInService>();
        var lockout = new InMemoryLockoutCounter(TestClock.Instance);
        var service = cluster.SignIn(lockout, logger: logger);

        var email = "ip-probe@pii-probe.example";
        await cluster.CreateUserAsync(email, "a-password-value");

        var outcome = await service.SignInWithPasswordAsync(
            IdentityCluster.Tenant,
            email,
            "a-password-value",
            new() { ClientId = "portal", DeviceLabel = "Firefox", ClientAddress = ip },
            Ct
        );

        outcome.IsSuccess.ShouldBeTrue(outcome.Error?.Message);

        foreach (var message in logger.Messages) {
            message.ShouldNotContain(ip, Case.Insensitive);
        }

        // ⚠ And it is not in the durable or hot tier either. The session keeps a truncated digest,
        // not the address — grain state is JSON in a database and in every backup, so an address
        // there is an address in every backup forever (docs/plan/05 § What is not in a grain).
        var session = (await cluster.Session(outcome.GetValueOrThrow().SessionId).GetAsync()).GetValueOrThrow();

        session.ClientAddressDigest.ShouldNotBeEmpty();
        session.ClientAddressDigest.ShouldNotContain(ip, Case.Insensitive);
        session.ClientAddressDigest.Length.ShouldBe(16);
    }
}
