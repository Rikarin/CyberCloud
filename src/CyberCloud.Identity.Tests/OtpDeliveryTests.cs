using CyberCloud.Communication;
using CyberCloud.Communication.Contracts;
using CyberCloud.Communication.Providers;
using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Seams;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Multitenant;
using Orleans.TestingHost;
using System.Globalization;

namespace CyberCloud.Identity.Tests;

/// <summary>
///     <see cref="CommunicationOtpDelivery" /> against a real <c>CyberCloud.Communication</c> silo.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FAILURE CLASS THIS SUITE EXISTS FOR IS A DUPLICATE SMS.</b>
///         <c>IMessageGrain</c> is keyed on a hash of <c>(serviceId, idempotencyKey)</c> so that a
///         retry after a timeout addresses the same activation and sends nothing. An adapter that
///         derived its key from the <i>attempt</i> rather than from the thing being notified about —
///         <c>Guid.NewGuid()</c> is the natural way to write that — would defeat the whole grain, and
///         would look correct in every test that sends once.
///         <see cref="TheSameCodeDeliveredTwiceReachesTheCarrierOnce" /> is the one that goes red;
///         <see cref="ASecondCodeForOneUserAndPurposeIsDeliveredAgain" /> is the other direction, and
///         is what a key that over-collapses (a clock window, say) would fail.
///     </para>
///     <para>
///         ⚠ <b>The real sending domain, not a double for <see cref="IMessageSender" />.</b>
///         Substituting one would leave the assertion "the carrier was called once" measuring this
///         suite's own stub rather than the grain that actually enforces it. What <i>is</i> a double
///         is <see cref="InMemoryChannelProvider" />, which stands where a carrier would — the same
///         arrangement <c>CyberCloud.Communication.Tests</c> uses, and the same ADR-018 deviation
///         (memory grain storage, no Testcontainers) that this project's <c>.csproj</c> already
///         records.
///     </para>
/// </remarks>
[Collection(OtpDeliverySuite.Name)]
public sealed class OtpDeliveryTests(OtpDeliveryCluster cluster) {
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── FAILURE CLASS: a retry sends twice ─────────────────────────────────────────────────────

    [Fact]
    public async Task TheSameCodeDeliveredTwiceReachesTheCarrierOnce() {
        cluster.Reset();

        var delivery = Sms(NewUser(), "424242");

        var first = await cluster.Delivery.DeliverAsync(delivery, Ct);

        // ⚠ The realistic shape: the caller timed out waiting for the first call and repeated it
        // verbatim. Nothing about the second call says "this is a retry" — that is the point.
        var second = await cluster.Delivery.DeliverAsync(delivery, Ct);

        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        second.IsSuccess.ShouldBeTrue(second.Error?.Message);

        cluster.Sms.Calls.ShouldBe(
            1,
            "IMessageGrain exists so that a retry addresses the same activation. A key derived from "
            + "the attempt rather than from the code would have sent a second SMS here."
        );

        cluster.Sms.Sent.Count.ShouldBe(1);
    }

    [Fact]
    public async Task TheSameCodeStillReachesTheCarrierOnceAcrossAGrainDeactivation() {
        cluster.Reset();

        var delivery = Sms(NewUser(), "424242");

        (await cluster.Delivery.DeliverAsync(delivery, Ct)).IsSuccess.ShouldBeTrue();

        // The cold-grain case, which is when an in-process "have I seen this key" set would have
        // forgotten and sent again.
        await cluster.MessageFor(delivery).DeactivateAsync();

        (await cluster.Delivery.DeliverAsync(delivery, Ct)).IsSuccess.ShouldBeTrue();

        cluster.Sms.Calls.ShouldBe(1);
    }

    // ── FAILURE CLASS: a genuinely new code is swallowed ───────────────────────────────────────
    //
    // ⚠ The more important half. A duplicate SMS is a support call; a swallowed one is a user who
    // cannot sign in while the platform reports that everything worked. A key built from a clock
    // window fails HERE rather than above, because two codes issued inside one window compute one
    // key — and, because IMessageGrain conflicts on differing content, the second would not even
    // fail quietly: it would fail with Conflict, and "resend" would be broken for everybody.

    [Fact]
    public async Task ASecondCodeForOneUserAndPurposeIsDeliveredAgain() {
        cluster.Reset();

        var user = NewUser();

        var first = await cluster.Delivery.DeliverAsync(Sms(user, "424242"), Ct);
        var second = await cluster.Delivery.DeliverAsync(Sms(user, "515151"), Ct);

        first.IsSuccess.ShouldBeTrue(first.Error?.Message);

        second.IsSuccess.ShouldBeTrue(
            second.Error?.Message
            + " — a second code for one user is a second message. A key that collapsed them would "
            + "answer Conflict here, because the content differs."
        );

        cluster.Sms.Calls.ShouldBe(2, "two genuinely different codes are two sends");
        cluster.Sms.Sent.Count.ShouldBe(2);
    }

    [Fact]
    public async Task TwoPurposesForOneUserAreTwoMessages() {
        cluster.Reset();

        // ⚠ Same user, same code, same second. Only the reason differs — which is exactly the case
        // a key omitting the purpose would collapse, turning a password-reset code into a silent
        // replay of the sign-in code that preceded it.
        var signIn = Sms(NewUser(), "424242");
        var reset = signIn with { Purpose = OtpPurpose.PasswordReset };

        (await cluster.Delivery.DeliverAsync(signIn, Ct)).IsSuccess.ShouldBeTrue();
        (await cluster.Delivery.DeliverAsync(reset, Ct)).IsSuccess.ShouldBeTrue();

        cluster.Sms.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task TwoTenantsWhoseUserIdsCollideDoNotShareAMessage() {
        cluster.Reset();

        // ⚠ Not hypothetical. A user id only has to be unique WITHIN a tenant — IUserGrain is
        // tenant-qualified — and every tenant's codes land in one service's key space here, so the
        // tenant id is the only thing keeping these two apart. Dropping it from the key would make
        // the second delivery a "retry" of the first and send Bob nothing.
        var alice = Sms(NewUser(), "424242");

        var bob = alice with {
            TenantId = OtpDeliveryCluster.OtherTenant,
            Destination = "+420777999999"
        };

        (await cluster.Delivery.DeliverAsync(alice, Ct)).IsSuccess.ShouldBeTrue();
        (await cluster.Delivery.DeliverAsync(bob, Ct)).IsSuccess.ShouldBeTrue();

        cluster.Sms.Calls.ShouldBe(2);

        cluster.Sms.Sent
            .Select(x => x.Destination)
            .ShouldBe(["+420777123456", "+420777999999"], ignoreOrder: true);
    }

    // ── The key itself, asserted rather than inferred ──────────────────────────────────────────

    [Fact]
    public void TheKeyIsAFunctionOfTheDeliveryAndOfNothingElse() {
        var delivery = Sms(NewUser(), "424242");

        // ⚠ Two calls, arbitrarily far apart in wall-clock terms, must agree. This is the property a
        // `{window:O}` component would destroy: round-trip format prints sub-second precision, so an
        // unrounded clock reading makes every call a different key — Guid.NewGuid() by another name.
        CommunicationOtpDelivery.IdempotencyKeyFor(delivery)
            .ShouldBe(CommunicationOtpDelivery.IdempotencyKeyFor(delivery));

        CommunicationOtpDelivery.IdempotencyKeyFor(delivery with { Code = "515151" })
            .ShouldNotBe(CommunicationOtpDelivery.IdempotencyKeyFor(delivery));
    }

    [Fact]
    public void TheCodeIsNotSpelledOutInTheKey() {
        // ⚠ SendRequest.IdempotencyKey's own remarks: the value "ends up in every log line and trace
        // that prints a grain id". A key carrying the six digits verbatim would put a live
        // credential in all of them. It carries a digest instead — see CommunicationOtpDelivery's
        // remarks for what that does and does not buy.
        var key = CommunicationOtpDelivery.IdempotencyKeyFor(Sms(NewUser(), "424242"));

        key.ShouldNotContain("424242");
        key.ShouldStartWith("otp-");
        key.ShouldContain(OtpPurpose.SignIn.ToString());
    }

    // ── The refusals ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADeliveryWithNoPurposeIsRefusedBeforeTheCarrier() {
        cluster.Reset();

        var refused = await cluster.Delivery.DeliverAsync(
            Sms(NewUser(), "424242") with { Purpose = OtpPurpose.Unknown },
            Ct
        );

        refused.IsFailure.ShouldBeTrue("a delivery that did not say why would share one key space "
            + "with every other reason a code goes to that user");

        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        cluster.Sms.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ACredentialKindThatIsNotDeliveredIsRefusedRatherThanGuessedAt() {
        cluster.Reset();

        var refused = await cluster.Delivery.DeliverAsync(
            Sms(NewUser(), "424242") with { Kind = CredentialKind.Passkey },
            Ct
        );

        refused.IsFailure.ShouldBeTrue("a passkey is not sent anywhere");
        cluster.Sms.Calls.ShouldBe(0);
        cluster.Email.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task EmailAndSmsCodesGoToTheirOwnCarriers() {
        cluster.Reset();

        var bySms = Sms(NewUser(), "424242");

        var byEmail = bySms with {
            Kind = CredentialKind.EmailOtp,
            Destination = "alice@example.com"
        };

        (await cluster.Delivery.DeliverAsync(bySms, Ct)).IsSuccess.ShouldBeTrue();
        (await cluster.Delivery.DeliverAsync(byEmail, Ct)).IsSuccess.ShouldBeTrue();

        cluster.Sms.Calls.ShouldBe(1);
        cluster.Email.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task TheUnwiredDefaultFailsAndNamesTheMissingCall() {
        // ⚠ The seam's contract, kept honest from this side: an OTP factor that reported delivery
        // and sent nothing would lock every user who enrolled in it out of their account.
        var refused = await new UnavailableOtpDelivery().DeliverAsync(Sms(NewUser(), "424242"), Ct);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("AddCommunicationOtpDelivery");
    }

    /// <summary>
    ///     ⚠ A fresh user per test, and it is not cosmetic. The fixture's silo — and therefore its
    ///     message grains — outlives one test, and <see cref="OtpDeliveryCluster.Reset" /> only
    ///     forgets the carrier doubles. Two tests sending the same code to the same user would be
    ///     each other's retry, and the second would correctly send nothing. A fresh user id is what
    ///     <c>CyberCloud.Communication.Tests</c> gets from a fresh service id per test.
    /// </summary>
    static Guid NewUser() => Guid.NewGuid();

    static OtpDelivery Sms(Guid userId, string code) =>
        new() {
            TenantId = OtpDeliveryCluster.UserTenant,
            UserId = userId,
            Purpose = OtpPurpose.SignIn,
            Kind = CredentialKind.SmsOtp,
            Destination = "+420777123456",
            Code = code
        };
}

/// <summary>
///     A silo running the production <c>AddCyberCloudCommunication</c> wiring, with
///     <see cref="CommunicationOtpDelivery" /> in front of it as a host would hold it.
/// </summary>
/// <remarks>
///     ⚠ Its own cluster rather than <c>IdentityCluster</c>'s, because the sending domain is not part
///     of every identity test's silo and putting it there would make an unrelated suite's failure
///     depend on a carrier double.
/// </remarks>
public sealed class OtpDeliveryCluster : IAsyncLifetime {
    TestCluster cluster = null!;

    /// <summary>The tenant that owns the communication service the platform sends its codes through.</summary>
    public static Guid PlatformTenant { get; } = Guid.Parse("aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa");

    /// <summary>The tenant the users belong to. ⚠ Deliberately not <see cref="PlatformTenant" />.</summary>
    public static Guid UserTenant { get; } = Guid.Parse("bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb");

    /// <summary>A second user tenant.</summary>
    public static Guid OtherTenant { get; } = Guid.Parse("cccccccc-3333-4333-8333-cccccccccccc");

    /// <summary>The user every test sends to.</summary>
    public static Guid Alice { get; } = Guid.Parse("dddddddd-4444-4444-8444-dddddddddddd");

    /// <summary>The service the route names. Fixed, because the route is fixed at wiring time.</summary>
    public static Guid ServiceId { get; } = Guid.Parse("eeeeeeee-5555-4555-8555-eeeeeeeeeeee");

    /// <summary>The SMS carrier double.</summary>
    public InMemoryChannelProvider Sms { get; } = new(ChannelKind.Sms);

    /// <summary>The email carrier double.</summary>
    public InMemoryChannelProvider Email { get; } = new(ChannelKind.Email);

    /// <summary>The seam under test, resolved from the silo's own container.</summary>
    public IOtpDeliverySeam Delivery { get; private set; } = null!;

    /// <summary>The message grain one delivery would address. For the deactivation case.</summary>
    /// <param name="delivery">The delivery.</param>
    public IMessageGrain MessageFor(OtpDelivery delivery) =>
        cluster.GrainFactory
            .ForTenant(PlatformTenant.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IMessageGrain>(
                CommunicationGrainKeys.Message(ServiceId, CommunicationOtpDelivery.IdempotencyKeyFor(delivery))
            );

    /// <summary>Forgets both carriers. Called at the top of every test that counts calls.</summary>
    public void Reset() {
        Sms.Reset();
        Email.Reset();
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        Instance = this;

        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        cluster = builder.Build();
        await cluster.DeployAsync();

        // ⚠ Built on the CLIENT side, which is the faithful shape: identity holds IMessageSender,
        // and identity is not a grain. GrainMessageSender is what qualifies every GetGrain with
        // ForTenant — CC1006 — so the adapter cannot get the tenancy wrong by construction.
        Delivery = new CommunicationOtpDelivery(
            new GrainMessageSender(cluster.GrainFactory),
            new() { TenantId = PlatformTenant, ServiceId = ServiceId }
        );

        var service = cluster.GrainFactory
            .ForTenant(PlatformTenant.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ICommunicationServiceGrain>(CommunicationGrainKeys.Service(ServiceId));

        (await service.CreateAsync(PlatformTenant, "platform")).IsSuccess.ShouldBeTrue();

        foreach (var channel in (ChannelKind[])[ChannelKind.Sms, ChannelKind.Email]) {
            (await service.ConfigureChannelAsync(
                new() {
                    Channel = channel,
                    Provider = "in-memory",
                    Credentials = new() { Mode = CredentialMode.PlatformAccount },
                    Limits = new() {
                        MaxMessagesPerWindow = 1000,
                        MaxSpendPerWindow = 1000m,
                        Currency = "EUR"
                    },
                    EstimatedUnitCost = 0.05m,
                    Enabled = true
                }
            )).IsSuccess.ShouldBeTrue();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (cluster is not null) {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }
    }

    /// <summary>
    ///     The live fixture, for the silo configurator to reach — <c>AddSiloBuilderConfigurator&lt;T&gt;</c>
    ///     constructs its argument with <c>new()</c>.
    /// </summary>
    internal static OtpDeliveryCluster Instance { get; private set; } = null!;

    sealed class SiloConfigurator : ISiloConfigurator {
        public void Configure(ISiloBuilder silo) {
            silo.AddMemoryGrainStorage(StorageTiers.Durable);
            silo.AddMemoryGrainStorage(StorageTiers.Hot);

            silo.ConfigureServices(services => {
                    // The doubles go in FIRST so the module's TryAdd keeps them, and BESIDE the
                    // refusing seams rather than instead of them — every channel here names
                    // "in-memory", so the registry resolves by name.
                    services.AddSingleton<IClock>(new FixedClock());
                    services.AddSingleton<IChannelProvider>(Instance.Sms);
                    services.AddSingleton<IChannelProvider>(Instance.Email);
                }
            );

            silo.AddCyberCloudCommunication();
        }
    }

    /// <summary>
    ///     ⚠ A clock that does not move, and its own rather than the suite's shared
    ///     <c>TestClock</c>. Nothing here asserts anything about time; what a moving clock could do
    ///     is roll a spend window under a test that is counting carrier calls, from a different
    ///     collection running in parallel.
    /// </summary>
    sealed class FixedClock : IClock {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 11, 14, 22, 9, TimeSpan.Zero);
    }
}

/// <summary>Binds <see cref="OtpDeliveryCluster" /> to the class that shares it.</summary>
[CollectionDefinition(Name)]
public sealed class OtpDeliverySuite : ICollectionFixture<OtpDeliveryCluster> {
    /// <summary>The collection name.</summary>
    public const string Name = "otp-delivery";
}
