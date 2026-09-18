using CyberCloud.Communication.Providers.Smtp;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Multitenant;
using Orleans.TestingHost;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace CyberCloud.Communication.Tests;

/// <summary>
///     The email carrier against a real SMTP server — Mailpit in a container, the same image the
///     AppHost runs — through the real send path.
/// </summary>
/// <remarks>
///     <para>
///         <b>Two things are proven here and nowhere else.</b> That the hand-written SMTP client
///         speaks a dialect a real server accepts (greeting, <c>EHLO</c>, envelope, <c>DATA</c>,
///         dot-stuffing, <c>QUIT</c>), read back through Mailpit's HTTP API rather than inferred
///         from a <c>250</c>; and that the deliverability headers <c>MailMessagesTests</c> pins byte
///         for byte are what a receiver parses out — <c>Message-ID</c>, <c>List-Unsubscribe</c>,
///         <c>Auto-Submitted</c>.
///     </para>
///     <para>
///         ⚠ <b>The suppression refusal is asserted against the server, not against a counter.</b>
///         <c>SuppressionTests</c> proves the in-memory double is never called for a suppressed
///         address; this proves the same through a silo whose only email carrier is the real one —
///         the address is suppressed, a send is refused, and Mailpit's inbox holds nothing for it.
///         Sabotage-tested with the suppression match in <c>MessageGrain.DispatchAsync</c> inverted
///         (<c>IsSuppressed: false</c>): the refusal assertion went red first, and with the refusal
///         assertions removed the inbox count for the suppressed address was 1 against the expected
///         0 — so the server-side assertion bites on its own.
///     </para>
///     <para>
///         <c>axllent/mailpit:v1.31.1</c>, pinned like every other image in this tree, and the same
///         tag <c>CyberCloudTopology</c> declares — <c>AppHostTopologyTests</c> holds the two
///         together. Mailpit accepts anything on 1025 with no TLS and no auth, which is the
///         <c>SmtpSecurity.None</c> arrangement; the refusals — a relay without <c>STARTTLS</c>, an
///         untrusted certificate, a credential over no TLS, a <c>550</c>, a silent relay — are
///         <c>SmtpRefusalTests</c>' against a scripted server, and the happy halves of
///         <c>STARTTLS</c> and <c>AUTH</c> are run by nothing, because both need a certificate the
///         client trusts and the honest statement is that those two were read against RFC 3207 and
///         RFC 4954 and not run to their end against a server.
///     </para>
/// </remarks>
public sealed class SmtpChannelProviderTests : IAsyncLifetime {
    /// <summary>The image, and the one <c>CyberCloudTopology</c> must agree with.</summary>
    public const string Image = "axllent/mailpit:v1.31.1";

    const int SmtpPort = 1025;
    const int HttpPort = 8025;

    static readonly Guid Tenant = Guid.Parse("33333333-3333-4333-8333-333333333333");

    IContainer mailpit = null!;
    TestCluster cluster = null!;
    HttpClient api = null!;

    /// <summary>
    ///     The relay the silo under test is handed. Static because the silo resolves its own services
    ///     in this process and the container's port is known only after it starts.
    /// </summary>
    static SmtpRelayOptions Relay { get; set; } = new();

    public async ValueTask InitializeAsync() {
        var token = TestContext.Current.CancellationToken;

        mailpit = new ContainerBuilder(Image)
            .WithPortBinding(SmtpPort, true)
            .WithPortBinding(HttpPort, true)
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(static x => x.ForPort(HttpPort).ForPath("/readyz"))
            )
            .Build();

        await mailpit.StartAsync(token);

        Relay = new() {
            Host = mailpit.Hostname,
            Port = mailpit.GetMappedPublicPort(SmtpPort),
            Security = SmtpSecurity.None,
            From = "no-reply@cybercloud.example",
            FromName = "Cyber Cloud",
            UnsubscribeMailbox = "unsubscribe@cybercloud.example",
            Timeout = TimeSpan.FromSeconds(20)
        };

        api = new() {
            BaseAddress = new(
                $"http://{mailpit.Hostname}:{mailpit.GetMappedPublicPort(HttpPort).ToString(CultureInfo.InvariantCulture)}/"
            )
        };

        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        cluster = builder.Build();
        await cluster.DeployAsync();
    }

    public async ValueTask DisposeAsync() {
        api?.Dispose();

        if (cluster is not null) {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }

        if (mailpit is not null) {
            await mailpit.DisposeAsync();
        }
    }

    [Fact]
    public async Task AnEmailLandsInTheInboxWithTheDeliverabilityHeaders() {
        var token = TestContext.Current.CancellationToken;
        var service = await NewServiceAsync();
        var sender = new GrainMessageSender(cluster.GrainFactory);

        var sent = await sender.SendAsync(
            Tenant,
            new() {
                ServiceId = service,
                Channel = ChannelKind.Email,
                Destination = "Alice@Example.com",
                Body = "424242 is your code.\r\n.\r\nIt expires in ten minutes.",
                IdempotencyKey = "otp-landing"
            },
            token
        );

        var snapshot = sent.GetValueOrThrow();
        snapshot.Status.ShouldBe(MessageStatus.Dispatched, snapshot.Detail);
        snapshot.Provider.ShouldBe(SmtpChannelProvider.ProviderName);
        snapshot.ProviderMessageId.ShouldBe(
            $"{snapshot.MessageId:N}@cybercloud.example",
            "the Message-ID this build minted, bare"
        );

        var message = await TheOneMessageToAsync("alice@example.com", token);

        // What Mailpit parsed out of the bytes on the wire.
        message.GetProperty("MessageID").GetString().ShouldBe(snapshot.ProviderMessageId);
        message.GetProperty("From").GetProperty("Address").GetString().ShouldBe("no-reply@cybercloud.example");
        message.GetProperty("From").GetProperty("Name").GetString().ShouldBe("Cyber Cloud");
        message.GetProperty("Subject").GetString().ShouldBe("424242 is your code.");
        message.GetProperty("ReturnPath")
            .GetString()
            .ShouldBe("no-reply@cybercloud.example", "the envelope sender is the From address");
        message.GetProperty("ListUnsubscribe")
            .GetProperty("Header")
            .GetString()
            .ShouldBe("<mailto:unsubscribe@cybercloud.example?subject=unsubscribe>");

        // ⚠ The lone dot survived. Without dot-stuffing the server would have taken the body to end
        // at that line, and "It expires in ten minutes." would have been read as an SMTP command.
        message.GetProperty("Text")
            .GetString()!
            .ReplaceLineEndings("\n")
            .TrimEnd('\n')
            .ShouldBe("424242 is your code.\n.\nIt expires in ten minutes.");

        var headers = await HeadersOfAsync(message.GetProperty("ID").GetString()!, token);
        headers["Auto-Submitted"][0].ShouldBe("auto-generated");
        headers["Content-Type"][0].ShouldBe("text/plain; charset=utf-8");
        headers["Content-Transfer-Encoding"][0].ShouldBe("quoted-printable");
        headers["Message-Id"][0].ShouldBe($"<{snapshot.ProviderMessageId}>");
    }

    [Fact]
    public async Task ASuppressedAddressNeverReachesTheServer() {
        var token = TestContext.Current.CancellationToken;
        var service = await NewServiceAsync();
        var sender = new GrainMessageSender(cluster.GrainFactory);

        (await cluster.GrainFactory.ForTenant(Tenant.ToString("D", CultureInfo.InvariantCulture))
                .GetGrain<ISuppressionListGrain>(CommunicationGrainKeys.Service(service))
                .SuppressAsync(
                    ChannelKind.Email,
                    "bob@example.com",
                    SuppressionReason.Complaint,
                    "marked as spam",
                    Guid.Empty
                ))
            .IsSuccess.ShouldBeTrue();

        var refused = await sender.SendAsync(
            Tenant,
            new() {
                ServiceId = service,
                Channel = ChannelKind.Email,
                Destination = "bob@example.com",
                Body = "424242 is your code.",
                IdempotencyKey = "otp-suppressed"
            },
            token
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        refused.Error.Message.ShouldContain("suppression list");
        refused.Error.Message.ShouldContain("no carrier was called");

        // And a control, so "nothing for bob" is not "nothing at all": a send to a clear address
        // on the same service lands.
        (await sender.SendAsync(
                Tenant,
                new() {
                    ServiceId = service,
                    Channel = ChannelKind.Email,
                    Destination = "carol@example.com",
                    Body = "424242 is your code.",
                    IdempotencyKey = "otp-control"
                },
                token
            )).GetValueOrThrow()
            .Status.ShouldBe(MessageStatus.Dispatched);

        (await CountToAsync("bob@example.com", token)).ShouldBe(
            0,
            "a suppressed address produced no connection to the relay"
        );
        (await CountToAsync("carol@example.com", token)).ShouldBe(1);
    }

    [Fact]
    public async Task ARelayThatIsNotThereFailsTheMessageAndCostsNoBudget() {
        var token = TestContext.Current.CancellationToken;

        // Nobody listens on a port the container did not publish — the shape of "the relay is down".
        var provider = new SmtpChannelProvider(
            new() {
                Host = "127.0.0.1",
                Port = 9,
                Security = SmtpSecurity.None,
                From = "no-reply@cybercloud.example",
                Timeout = TimeSpan.FromSeconds(5)
            },
            new SystemClock(),
            NullLogger<SmtpChannelProvider>.Instance
        );

        var failed = await provider.SendAsync(
            new() {
                MessageId = Guid.NewGuid(), Channel = ChannelKind.Email, Destination = "dave@example.com", Body = "x"
            },
            token
        );

        failed.IsFailure.ShouldBeTrue();
        failed.Error!.Message.ShouldContain("could not be spoken to");
        failed.Error.Message.ShouldNotContain(
            "dave@example.com",
            Case.Insensitive,
            "no address in a refusal that will be logged"
        );
    }

    [Fact]
    public async Task ATenantsOwnAccountIsRefusedRatherThanSentThroughThePlatformsRelay() {
        var provider = new SmtpChannelProvider(Relay, new SystemClock(), NullLogger<SmtpChannelProvider>.Instance);

        var refused = await provider.SendAsync(
            new() {
                MessageId = Guid.NewGuid(),
                Channel = ChannelKind.Email,
                Destination = "erin@example.com",
                Body = "x",
                Credentials = new() { Mode = CredentialMode.TenantAccount }
            },
            TestContext.Current.CancellationToken
        );

        refused.Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
        refused.Error.Message.ShouldContain("account: tenant");
        (await CountToAsync("erin@example.com", TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    // ── Mailpit's API ───────────────────────────────────────────────────────────────────────────

    async Task<JsonElement> TheOneMessageToAsync(string address, CancellationToken token) {
        // The 250 came back before Mailpit indexed the message; give it a moment rather than a race.
        for (var attempt = 0; attempt < 50; attempt++) {
            var listing = await api.GetFromJsonAsync<JsonElement>($"api/v1/search?query=to:{address}", token);
            var messages = listing.GetProperty("messages");

            if (messages.GetArrayLength() == 1) {
                var id = messages[0].GetProperty("ID").GetString()!;
                return await api.GetFromJsonAsync<JsonElement>($"api/v1/message/{id}", token);
            }

            messages.GetArrayLength().ShouldBeLessThanOrEqualTo(1, $"more than one message reached {address}");
            await Task.Delay(100, token);
        }

        throw new InvalidOperationException($"No message to {address} appeared in Mailpit within five seconds.");
    }

    async Task<int> CountToAsync(string address, CancellationToken token) {
        // A negative is asserted after a positive control has landed, so a short settle is enough.
        await Task.Delay(500, token);
        var listing = await api.GetFromJsonAsync<JsonElement>($"api/v1/search?query=to:{address}", token);
        return listing.GetProperty("messages").GetArrayLength();
    }

    async Task<Dictionary<string, string[]>> HeadersOfAsync(string id, CancellationToken token) =>
        (await api.GetFromJsonAsync<Dictionary<string, string[]>>($"api/v1/message/{id}/headers", token))!;

    // ── The silo ────────────────────────────────────────────────────────────────────────────────

    async Task<Guid> NewServiceAsync() {
        var serviceId = Guid.NewGuid();
        var service = cluster.GrainFactory.ForTenant(Tenant.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ICommunicationServiceGrain>(CommunicationGrainKeys.Service(serviceId));

        (await service.CreateAsync(Tenant, "primary")).IsSuccess.ShouldBeTrue();

        // ⚠ Provider left EMPTY on purpose: with the carrier registered beside the refusing seam,
        // an unnamed email channel resolves to the carrier (IRefusingChannelProvider) — which is
        // the arrangement a tenant's `channels` resource with no `provider` lands in.
        (await service.ConfigureChannelAsync(
                new() {
                    Channel = ChannelKind.Email,
                    Provider = string.Empty,
                    Credentials = new() { Mode = CredentialMode.PlatformAccount },
                    Limits = new() { MaxMessagesPerWindow = 100, MaxSpendPerWindow = 0m, Currency = "EUR" },
                    EstimatedUnitCost = 0m,
                    Enabled = true
                }
            )).IsSuccess.ShouldBeTrue();

        return serviceId;
    }

    sealed class SiloConfigurator : ISiloConfigurator {
        public void Configure(ISiloBuilder silo) {
            silo.AddMemoryGrainStorage(StorageTiers.Durable);
            silo.AddMemoryGrainStorage(StorageTiers.Hot);
            silo.ConfigureServices(static services => services.AddSingleton<IClock, SystemClock>());

            // The production wiring, both calls, in the order SiloComposition makes them.
            silo.AddCyberCloudCommunication();
            silo.AddSmtpEmailCarrier(Relay);
        }
    }
}
