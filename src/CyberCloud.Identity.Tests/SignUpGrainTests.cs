using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Credentials;
using CyberCloud.Identity.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Multitenant;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.TestingHost;
using System.Globalization;
using System.Text.Json;

namespace CyberCloud.Identity.Tests;

/// <summary>
///     The pre-tenant half of a self-serve sign-up — <c>SignUpGrain</c> — held to the same four
///     properties <see cref="OtpPolicy" /> holds <c>UserGrain</c> to, plus the two that are its own:
///     nothing may be recorded for an unproven address, and what was recorded survives the
///     activation.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Organised by failure class, as <c>OtpIssuanceTests</c> is</b>, because each is a
///         defect a person meets rather than a test: a code that verifies twice
///         (<see cref="ACodeIsRedeemableExactlyOnce" />), a challenge that survives its five guesses
///         (<see cref="FiveWrongCodesBurnTheChallenge" />), a code that outlives its ten minutes
///         (<see cref="VerifyAfterExpiryIsFalse" />), a tenant created for an address nobody proved
///         (<see cref="CompleteBeforeVerifyIsRefused" />), a retry that creates a second tenant
///         because the first attempt's steps were forgotten
///         (<see cref="RecordedStepsSurviveDeactivation" />), and a code sent under the wrong purpose
///         so that a sign-in and an enrolment deduplicate against each other
///         (<see cref="TheDeliverySeamIsCalledWithTheEnrolmentPurpose" />).
///     </para>
///     <para>
///         ⚠ <b>A recording seam rather than the sending domain</b>, unlike <c>OtpIssuanceTests</c>.
///         What is under test here is the grain's bookkeeping — which purpose, which destination,
///         which code, how many times — and the seam is the one place all four are visible. The
///         seam's own idempotency over the real domain is that suite's subject, not this one's.
///     </para>
/// </remarks>
[Collection(SignUpSuite.Name)]
public sealed class SignUpGrainTests(SignUpCluster cluster) {
    // ── FAILURE CLASS: a code verifies twice ───────────────────────────────────────────────────

    [Fact]
    public async Task ACodeIsRedeemableExactlyOnce() {
        var (grain, _) = await cluster.BeginAsync();
        var code = cluster.LastCode;

        (await grain.VerifyAsync(code)).GetValueOrThrow().ShouldBeTrue("the right code, live, first try");

        // ⚠ The same correct code, presented again. A double-submitted form and an attacker racing
        // the legitimate user look identical here, and both must find nothing — the challenge was
        // burnt inside the single-threaded activation before the first answer returned.
        (await grain.VerifyAsync(code)).GetValueOrThrow().ShouldBeFalse("a burnt challenge answers nothing");

        var described = (await grain.GetAsync()).GetValueOrThrow();
        described.Verified.ShouldBeTrue("the first presentation proved the address, and that fact stays");
    }

    // ── FAILURE CLASS: the guess budget ────────────────────────────────────────────────────────

    [Fact]
    public async Task FiveWrongCodesBurnTheChallenge() {
        var (grain, _) = await cluster.BeginAsync();
        var code = cluster.LastCode;
        var wrong = code == "000000" ? "000001" : "000000";

        for (var attempt = 0; attempt < OtpPolicy.MaxAttempts; attempt++) {
            (await grain.VerifyAsync(wrong)).GetValueOrThrow().ShouldBeFalse();
        }

        // ⚠ The sixth answer is the RIGHT one, and it fails. OtpPolicy.MaxAttempts: the challenge is
        // destroyed on the fifth wrong answer rather than merely refusing further ones, so "attempts
        // exhausted" and "wrong code" are not two states a probe can tell apart by whether a later
        // correct code works.
        (await grain.VerifyAsync(code)).GetValueOrThrow().ShouldBeFalse("five wrong answers burn the challenge");

        (await grain.GetAsync()).GetValueOrThrow().Verified.ShouldBeFalse();

        // A resend after the cooldown mints a fresh code the person can still answer.
        cluster.Clock.Advance(OtpPolicy.ResendCooldown + TimeSpan.FromSeconds(1));
        (await grain.BeginAsync(cluster.AddressOf(grain))).IsSuccess.ShouldBeTrue();
        cluster.LastCode.ShouldNotBe(code);
        (await grain.VerifyAsync(cluster.LastCode)).GetValueOrThrow().ShouldBeTrue();
    }

    // ── FAILURE CLASS: a code outlives its ten minutes ─────────────────────────────────────────

    [Fact]
    public async Task VerifyAfterExpiryIsFalse() {
        var (grain, _) = await cluster.BeginAsync();
        var code = cluster.LastCode;

        cluster.Clock.Advance(OtpPolicy.Lifetime + TimeSpan.FromSeconds(1));

        (await grain.VerifyAsync(code)).GetValueOrThrow().ShouldBeFalse("ten minutes is the lifetime — docs/plan/11 § Credentials");

        // ⚠ And the whole sign-up, not only the code, after SignUpPolicy.Lifetime: the grain forgets
        // itself, so the ticket a browser still holds names nothing.
        cluster.Clock.Advance(SignUpPolicy.Lifetime);

        var gone = await grain.GetAsync();
        gone.IsFailure.ShouldBeTrue("an expired sign-up reads as absent");
        gone.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    // ── FAILURE CLASS: something is created for an unproven address ────────────────────────────

    [Fact]
    public async Task CompleteBeforeVerifyIsRefused() {
        var (grain, _) = await cluster.BeginAsync();

        // ⚠ The second gate in front of every create. The host checks Verified too; this is the one
        // a host bug — or a host somebody else wrote — cannot get past.
        var recorded = await grain.RecordStepAsync(SignUpStep.TenantCreated);
        recorded.IsFailure.ShouldBeTrue("no step may be recorded before the address is proven");
        recorded.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);

        var completed = await grain.CompleteAsync();
        completed.IsFailure.ShouldBeTrue();
        completed.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);

        (await grain.GetAsync()).GetValueOrThrow().CompletedSteps.ShouldBeEmpty();

        // And a sign-up that never began is refused too, with the not-found code rather than the
        // not-verified one — nothing to record against.
        var never = cluster.Grain(Guid.NewGuid());
        (await never.RecordStepAsync(SignUpStep.TenantCreated)).Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    // ── FAILURE CLASS: a retry creates a second tenant ─────────────────────────────────────────

    [Fact]
    public async Task RecordedStepsSurviveDeactivation() {
        var (grain, signupId) = await cluster.BeginAsync();
        (await grain.VerifyAsync(cluster.LastCode)).GetValueOrThrow().ShouldBeTrue();

        var before = (await grain.GetAsync()).GetValueOrThrow();

        (await grain.RecordStepAsync(SignUpStep.TenantCreated)).IsSuccess.ShouldBeTrue();
        (await grain.RecordStepAsync(SignUpStep.UserCreated)).IsSuccess.ShouldBeTrue();
        // Recording a step twice is one entry: the orchestrator may re-record a step it skipped.
        (await grain.RecordStepAsync(SignUpStep.UserCreated)).IsSuccess.ShouldBeTrue();

        // ⚠ The activation is dropped and the next call reads the hot tier cold. This is the case an
        // in-memory step list fails: the retry after a host restart would see no steps, run the
        // tenant step again, and CreateTenantAsync would answer Conflict on the second slug — or
        // worse, succeed under a fresh id if the ids were not in state either.
        await grain.DeactivateAsync();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        var after = (await cluster.Grain(signupId).GetAsync()).GetValueOrThrow();

        after.CompletedSteps.ShouldBe([SignUpStep.TenantCreated, SignUpStep.UserCreated]);
        after.Verified.ShouldBeTrue();
        after.TenantId.ShouldBe(before.TenantId, "the tenant id is allocated once, at begin");
        after.UserId.ShouldBe(before.UserId);
        after.SubscriptionId.ShouldBe(before.SubscriptionId);
        after.Email.ShouldBe(before.Email);
    }

    [Fact]
    public async Task ACompletedSignUpRecordsNothingFurtherAndCannotBeginAgain() {
        var (grain, _) = await cluster.BeginAsync();
        (await grain.VerifyAsync(cluster.LastCode)).GetValueOrThrow().ShouldBeTrue();
        (await grain.CompleteAsync()).IsSuccess.ShouldBeTrue();

        (await grain.RecordStepAsync(SignUpStep.SubscriptionCreated)).Error!.Code.ShouldBe(ErrorCode.Conflict);
        (await grain.BeginAsync(cluster.AddressOf(grain))).Error!.Code.ShouldBe(ErrorCode.Conflict);

        // ⚠ Idempotent: the browser retries a complete whose response was lost, and finds it done.
        (await grain.CompleteAsync()).Error!.Code.ShouldBe(ErrorCode.Conflict);
        (await grain.GetAsync()).GetValueOrThrow().CompletedSteps.ShouldContain(SignUpStep.Completed);
    }

    // ── FAILURE CLASS: the wrong purpose, the wrong destination, or a second message ───────────

    [Fact]
    public async Task TheDeliverySeamIsCalledWithTheEnrolmentPurpose() {
        var (grain, _) = await cluster.BeginAsync();
        var described = (await grain.GetAsync()).GetValueOrThrow();
        var delivery = cluster.Deliveries[^1];

        // ⚠ Enrolment, and not SignIn. OtpPurpose is part of the idempotency key
        // CyberCloud.Communication deduplicates on, so a sign-up code sent under SignIn would
        // collapse against a sign-in code to the same address seconds later.
        delivery.Purpose.ShouldBe(OtpPurpose.Enrolment);
        delivery.Kind.ShouldBe(CredentialKind.EmailOtp);
        delivery.Destination.ShouldBe(described.Email);
        delivery.TenantId.ShouldBe(Guid.Empty, "the sign-up lives in the platform tenant");
        delivery.UserId.ShouldBe(described.UserId, "the pre-allocated user id, so the message keys to the user it will become");
        delivery.Code.Length.ShouldBe(OtpPolicy.Digits);
    }

    [Fact]
    public async Task ARetriedBeginRedeliversTheSameCodeAndAResendMintsANewOne() {
        var (grain, _) = await cluster.BeginAsync();
        var address = cluster.AddressOf(grain);
        var first = cluster.LastCode;

        // Inside the cooldown: a retry, the same code.
        (await grain.BeginAsync(address)).IsSuccess.ShouldBeTrue();
        cluster.LastCode.ShouldBe(first);

        // After it: a resend, a fresh code, and the old one no longer answers.
        cluster.Clock.Advance(OtpPolicy.ResendCooldown + TimeSpan.FromSeconds(1));
        (await grain.BeginAsync(address)).IsSuccess.ShouldBeTrue();
        cluster.LastCode.ShouldNotBe(first);

        (await grain.VerifyAsync(first)).GetValueOrThrow().ShouldBeFalse("a resend invalidates the code it replaces");
        (await grain.VerifyAsync(cluster.LastCode)).GetValueOrThrow().ShouldBeTrue();
    }

    [Fact]
    public async Task TheIssueCapIsPerSignUp() {
        var (grain, _) = await cluster.BeginAsync();
        var address = cluster.AddressOf(grain);

        for (var sent = 1; sent < OtpPolicy.MaxIssuesPerWindow; sent++) {
            cluster.Clock.Advance(OtpPolicy.ResendCooldown + TimeSpan.FromSeconds(1));
            (await grain.BeginAsync(address)).IsSuccess.ShouldBeTrue($"resend {sent + 1} is inside the cap");
        }

        cluster.Clock.Advance(OtpPolicy.ResendCooldown + TimeSpan.FromSeconds(1));
        var refused = await grain.BeginAsync(address);

        refused.IsFailure.ShouldBeTrue("the sixth code inside the window is over the cap");
        refused.Error!.Code.ShouldBe(ErrorCode.QuotaExceeded);
        cluster.Deliveries.Count.ShouldBe(OtpPolicy.MaxIssuesPerWindow, "nothing reached the seam for the refused issue");
    }

    // ── FAILURE CLASS: the code reaches somewhere it should not ────────────────────────────────

    [Fact]
    public async Task TheCodeIsNowhereInGrainState() {
        // ⚠ The same reflective guard OtpIssuanceTests.TheCodeIsNowhereInGrainState is, for the
        // same reason: CC1005's suffix list does not name `Code`, so a plaintext field would
        // compile clean. SignUpGrainState's remarks cite this method.
        var (grain, signupId) = await cluster.BeginAsync();
        var code = cluster.LastCode;

        var state = await cluster.ReadStateAsync(signupId);
        state.Digest.ShouldNotBeNullOrEmpty("the challenge must actually be recorded");

        foreach (var property in typeof(SignUpGrainState).GetProperties()) {
            var rendered = JsonSerializer.Serialize(property.GetValue(state));
            rendered.ShouldNotContain(code, Case.Sensitive, $"{property.Name} carries the plaintext code");
        }

        // And the descriptor the host reads carries nothing credential-shaped either.
        JsonSerializer.Serialize((await grain.GetAsync()).GetValueOrThrow()).ShouldNotContain(code, Case.Sensitive);
    }

    [Fact]
    public async Task ADifferentAddressBeforeVerificationRestartsTheChallengeWithTheSameIds() {
        var (grain, _) = await cluster.BeginAsync();
        var before = (await grain.GetAsync()).GetValueOrThrow();
        var first = cluster.LastCode;

        // A person correcting a typo on the first step. Nothing has been created, so the ids stay —
        // a completion still re-drives one tenant — and only the address and its challenge change.
        var corrected = $"corrected-{Guid.NewGuid():N}@example.com";
        (await grain.BeginAsync(corrected)).IsSuccess.ShouldBeTrue();

        var after = (await grain.GetAsync()).GetValueOrThrow();
        after.Email.ShouldBe(corrected);
        after.TenantId.ShouldBe(before.TenantId);
        after.UserId.ShouldBe(before.UserId);

        (await grain.VerifyAsync(first)).GetValueOrThrow().ShouldBeFalse("the old address's code is gone");
        (await grain.VerifyAsync(cluster.LastCode)).GetValueOrThrow().ShouldBeTrue();

        // ⚠ And not after verification: a proven address cannot be swapped for an unproven one.
        (await grain.BeginAsync($"other-{Guid.NewGuid():N}@example.com")).Error!.Code.ShouldBe(ErrorCode.Conflict);
    }
}

/// <summary>Binds <see cref="SignUpCluster" /> to the classes that share it.</summary>
[CollectionDefinition(Name)]
public sealed class SignUpSuite : ICollectionFixture<SignUpCluster> {
    /// <summary>The collection name.</summary>
    public const string Name = "signup-cluster";
}

/// <summary>
///     An <see cref="IOtpDeliverySeam" /> that remembers every delivery and sends nothing.
/// </summary>
/// <remarks>
///     ⚠ The one place in this suite the plaintext code exists, and it exists because a carrier is
///     the one party that legitimately sees it — the same argument <c>OtpIssuanceCluster.Codes</c>
///     makes for reading it out of the message body.
/// </remarks>
public sealed class RecordingOtpDelivery : IOtpDeliverySeam {
    readonly List<OtpDelivery> deliveries = [];

    /// <summary>Everything delivered, in order.</summary>
    public IReadOnlyList<OtpDelivery> Deliveries {
        get {
            lock (deliveries) {
                return [.. deliveries];
            }
        }
    }

    /// <inheritdoc />
    public Task<Result> DeliverAsync(OtpDelivery delivery, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(delivery);

        lock (deliveries) {
            deliveries.Add(delivery);
        }

        return Task.FromResult(Result.Success);
    }

    /// <summary>Forgets everything.</summary>
    public void Reset() {
        lock (deliveries) {
            deliveries.Clear();
        }
    }
}

/// <summary>
///     An in-process silo with the identity module wired as production wires it, a recording
///     delivery seam, an in-memory reminder service and a clock the tests drive.
/// </summary>
/// <remarks>
///     ⚠ <b>Its own cluster rather than <c>IdentityCluster</c>, for the reminder.</b>
///     <c>SignUpGrain</c> registers one at <c>BeginAsync</c> and that throws on a silo with no
///     reminder service — late, inside the grain call — so the fixture wires
///     <c>UseInMemoryReminderService</c>, which every fixture that activates a reminding grain has to.
/// </remarks>
public sealed class SignUpCluster : IAsyncLifetime {
    readonly Dictionary<Guid, string> addresses = [];

    TestCluster cluster = null!;

    /// <summary>The clock the whole silo reads.</summary>
    public TestClock Clock => TestClock.Instance;

    /// <summary>The delivery seam the grain hands codes to.</summary>
    public RecordingOtpDelivery Delivery { get; } = new();

    /// <summary>Every delivery since the last <see cref="BeginAsync" />.</summary>
    public IReadOnlyList<OtpDelivery> Deliveries => Delivery.Deliveries;

    /// <summary>The most recent code.</summary>
    public string LastCode => Deliveries[^1].Code;

    /// <summary>A sign-up grain, in the platform tenant.</summary>
    /// <param name="signupId">Which sign-up.</param>
    public ISignUpGrain Grain(Guid signupId) =>
        cluster.GrainFactory
            .ForTenant(Guid.Empty.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ISignUpGrain>(GrainKeys.SignUp(signupId));

    /// <summary>The address a sign-up was begun with.</summary>
    /// <param name="grain">The sign-up.</param>
    public string AddressOf(ISignUpGrain grain) =>
        addresses[GrainKeys.Parse(grain.GetKeyWithinTenant()).GetValueOrThrow().Id];

    /// <summary>Begins a fresh sign-up with a fresh address, forgetting earlier deliveries.</summary>
    public async Task<(ISignUpGrain Grain, Guid SignupId)> BeginAsync() {
        Delivery.Reset();

        var signupId = Guid.NewGuid();
        var address = $"signup-{signupId:N}@example.com";
        var grain = Grain(signupId);

        (await grain.BeginAsync(address)).IsSuccess.ShouldBeTrue();
        addresses[signupId] = address;

        return (grain, signupId);
    }

    /// <summary>The sign-up's hot-tier state, as the tier holds it.</summary>
    /// <param name="signupId">Which sign-up.</param>
    public async Task<SignUpGrainState> ReadStateAsync(Guid signupId) {
        var storage = cluster.Silos
            .OfType<InProcessSiloHandle>()
            .First()
            .SiloHost
            .Services
            .GetRequiredKeyedService<IGrainStorage>(StorageTiers.Hot);

        var state = new GrainState<SignUpGrainState>(new());
        await storage.ReadStateAsync("signup", Grain(signupId).GetGrainId(), state);

        return state.State;
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        Instance = this;
        Clock.Reset();

        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        cluster = builder.Build();
        await cluster.DeployAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (cluster is not null) {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }
    }

    /// <summary>The live fixture, for the configurator, which is constructed with <c>new()</c>.</summary>
    internal static SignUpCluster Instance { get; private set; } = null!;

    sealed class SiloConfigurator : ISiloConfigurator {
        public void Configure(ISiloBuilder silo) {
            silo.AddMemoryGrainStorage(StorageTiers.Durable);
            silo.AddMemoryGrainStorage(StorageTiers.Hot);
            silo.UseInMemoryReminderService();

            silo.ConfigureServices(services => {
                    // FIRST, so the module's TryAdd keeps them.
                    services.AddSingleton<IClock>(TestClock.Instance);
                    services.AddSingleton<IPasswordHasher>(CheapArgon2.Hasher);
                    services.AddSingleton<IOtpDeliverySeam>(Instance.Delivery);
                    services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
                }
            );

            silo.AddCyberCloudIdentity();
        }
    }
}
