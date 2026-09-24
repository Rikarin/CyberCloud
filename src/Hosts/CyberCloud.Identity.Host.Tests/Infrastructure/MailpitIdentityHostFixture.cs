using CyberCloud.Communication;
using CyberCloud.Communication.Contracts;
using CyberCloud.Communication.Providers.Smtp;
using CyberCloud.Identity;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Orleans.Multitenant;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace CyberCloud.Identity.Host.Tests.Infrastructure;

/// <summary>
///     <see cref="IdentityHostFixture" /> with a real mail path behind it: Mailpit in a container,
///     the communication module and its SMTP carrier in the silo, the platform's own communication
///     service in the platform tenant, and <c>CommunicationInvitationDelivery</c> routed through it —
///     the arrangement <c>CyberCloud.Silo.Host</c> makes on a development run (#93, #43).
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing on the mail path is substituted.</b> The invitation grain calls the seam, the
///         seam calls <c>IMessageSender</c>, the message grain in the platform tenant dispatches
///         through the SMTP carrier, and the bytes land in Mailpit, whose HTTP API the tests read
///         the link back out of — the same image and tag <c>CyberCloud.Communication.Tests</c>' SMTP
///         suite and the AppHost run.
///     </para>
///     <para>
///         A fixture of its own, in a collection of its own, so the suites that need no container
///         do not start one; it runs beside <see cref="IdentityHostSuite" /> under its own cluster
///         name.
///     </para>
/// </remarks>
public sealed class MailpitIdentityHostFixture : IdentityHostFixture {
    /// <summary>The image, as <c>CyberCloud.Communication.Tests.SmtpChannelProviderTests.Image</c> pins it.</summary>
    public const string Image = "axllent/mailpit:v1.31.1";

    /// <summary>The platform's communication service the invitations go out through.</summary>
    public static Guid PlatformServiceId { get; } = Guid.Parse("7a11e0aa-0000-4000-8000-0000000c0e00");

    const int SmtpPort = 1025;
    const int HttpPort = 8025;

    IContainer mailpit = null!;
    SmtpRelayOptions relay = new();

    /// <summary>Mailpit's HTTP API.</summary>
    public HttpClient Mailpit { get; private set; } = null!;

    /// <inheritdoc />
    protected override string ClusterName => "cybercloud-identity-host-mail-tests";

    /// <inheritdoc />
    protected override async Task StartDependenciesAsync() {
        mailpit = new ContainerBuilder(Image)
            .WithPortBinding(SmtpPort, true)
            .WithPortBinding(HttpPort, true)
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(static x => x.ForPort(HttpPort).ForPath("/readyz"))
            )
            .Build();

        await mailpit.StartAsync();

        relay = new() {
            Host = mailpit.Hostname,
            Port = mailpit.GetMappedPublicPort(SmtpPort),
            Security = SmtpSecurity.None,
            From = "no-reply@cybercloud.example",
            FromName = "Cyber Cloud",
            UnsubscribeMailbox = "unsubscribe@cybercloud.example",
            Timeout = TimeSpan.FromSeconds(20)
        };

        Mailpit = new() {
            BaseAddress = new(
                $"http://{mailpit.Hostname}:{mailpit.GetMappedPublicPort(HttpPort).ToString(CultureInfo.InvariantCulture)}/"
            )
        };
    }

    /// <inheritdoc />
    protected override void ConfigureSilo(ISiloBuilder silo) {
        // The production wiring, in SiloComposition's order: the module, the carrier, the route.
        silo.AddCyberCloudCommunication();
        silo.AddSmtpEmailCarrier(relay);
        silo.AddCommunicationInvitationDelivery(Guid.Empty, PlatformServiceId, new Uri(SignInPageBaseUri));
    }

    /// <inheritdoc />
    protected override async Task AfterClusterAsync() {
        // What PlatformBootstrapTask writes on a silo with a relay: the platform's service, with an
        // email channel on the platform's own account.
        var service = For(Guid.Empty).GetGrain<ICommunicationServiceGrain>(CommunicationGrainKeys.Service(PlatformServiceId));

        (await service.CreateAsync(Guid.Empty, "platform")).IsSuccess.ShouldBeTrue();
        (await service.ConfigureChannelAsync(
                new() {
                    Channel = ChannelKind.Email,
                    Provider = SmtpChannelProvider.ProviderName,
                    Credentials = new() { Mode = CredentialMode.PlatformAccount },
                    Limits = new() { MaxMessagesPerWindow = 1_000, MaxSpendPerWindow = 0m, Currency = "EUR" },
                    EstimatedUnitCost = 0m,
                    Enabled = true
                }
            )).IsSuccess.ShouldBeTrue();
    }

    /// <inheritdoc />
    protected override async ValueTask StopDependenciesAsync() {
        Mailpit?.Dispose();

        if (mailpit is not null) {
            await mailpit.DisposeAsync();
        }
    }

    /// <summary>Every message Mailpit holds for <paramref name="address" />, waiting briefly for the first.</summary>
    /// <param name="address">The recipient.</param>
    /// <param name="atLeast">How many to wait for.</param>
    public async Task<IReadOnlyList<JsonElement>> MessagesToAsync(string address, int atLeast = 1) {
        var token = TestContext.Current.CancellationToken;

        for (var attempt = 0; attempt < 50; attempt++) {
            var listing = await Mailpit.GetFromJsonAsync<JsonElement>($"api/v1/search?query=to:{address}", token);
            var messages = listing.GetProperty("messages").EnumerateArray().ToList();

            if (messages.Count >= atLeast) {
                var full = new List<JsonElement>();

                foreach (var message in messages) {
                    full.Add(
                        await Mailpit.GetFromJsonAsync<JsonElement>(
                            $"api/v1/message/{message.GetProperty("ID").GetString()}",
                            token
                        )
                    );
                }

                return full;
            }

            await Task.Delay(100, token);
        }

        return [];
    }
}

/// <summary>The collection the mail suites join.</summary>
[CollectionDefinition(Name)]
public sealed class MailpitIdentityHostSuite : ICollectionFixture<MailpitIdentityHostFixture> {
    /// <summary>The collection's name.</summary>
    public const string Name = "identity-host-mail";
}
