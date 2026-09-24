using CyberCloud.Authorization;
using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Credentials;
using CyberCloud.Identity.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Multitenant;
using Orleans.TestingHost;
using System.Collections.Concurrent;
using System.Globalization;

namespace CyberCloud.Identity.Tests;

/// <summary>
///     An invitation past its seven days: what it reads, and what a resend of it does. Issue #41's
///     review.
/// </summary>
/// <remarks>
///     ⚠ <b>A cluster and a clock of this class's own.</b> Expiry is caused by time passing, and the
///     suite's <see cref="TestClock" /> is shared with <c>SignUpGrainTests</c>' collection, which
///     runs in parallel. Moving it by a week would expire sign-ups under a test that isn't looking.
///     This clock moves nothing but these invitations.
/// </remarks>
[Collection(InvitationExpirySuite.Name)]
public sealed class InvitationExpiryTests(InvitationExpiryCluster cluster) {
    [Fact]
    public async Task AnExpiredInvitationWhoseUserIsStillInvitedIsResentWithAFreshWeek() {
        var (invitation, userId) = await cluster.InviteAsync("expired-still-invited@example.com");

        cluster.Clock.Advance(InvitationPolicy.Lifetime + TimeSpan.FromMinutes(1));

        (await invitation.GetAsync()).GetValueOrThrow().Status.ShouldBe(InvitationStatus.Expired);

        var resent = await invitation.ResendAsync("second-link-secret");

        resent.IsSuccess.ShouldBeTrue(resent.Error?.Message);
        resent.GetValueOrThrow().Status.ShouldBe(InvitationStatus.Pending);
        resent.GetValueOrThrow().ExpiresAt.ShouldBe(cluster.Clock.UtcNow + InvitationPolicy.Lifetime);
        cluster.Mail.Count(x => x.Email == "expired-still-invited@example.com").ShouldBe(2);

        // The counterpart the withdrawn cases below need: the user is untouched and still invited.
        (await cluster.User(userId).GetAsync()).GetValueOrThrow().Status.ShouldBe(UserStatus.Invited);
    }

    [Theory]
    [InlineData(UserStatus.Deprovisioned)]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Active)]
    public async Task AnExpiredInvitationWhoseUserIsNoLongerInvitedIsWithdrawnAndNotResent(UserStatus status) {
        // ⚠ THE REVIEW'S TRACE. The invitation expires; the owner removes the invited person (DELETE
        // on the member, which deprovisions them), or they join through a second link and are Active.
        // ViewAsync asked the user only for a Pending invitation, so this one read `expired`, passed
        // the resend's guard, and a fresh link went to the removed person's address.
        var email = $"expired-{status.ToString().ToLowerInvariant()}@example.com";
        var (invitation, userId) = await cluster.InviteAsync(email);

        cluster.Clock.Advance(InvitationPolicy.Lifetime + TimeSpan.FromMinutes(1));

        var moved = status == UserStatus.Active
            ? await cluster.User(userId).AcceptInvitationAsync("Joined Elsewhere", "a-password-from-the-second-link-1")
            : await cluster.User(userId).SetStatusAsync(status);

        moved.IsSuccess.ShouldBeTrue(moved.Error?.Message);

        (await invitation.GetAsync()).GetValueOrThrow().Status
            .ShouldBe(InvitationStatus.Withdrawn, $"an expired invitation for a {status} user reads as expired");

        var resent = await invitation.ResendAsync("a-link-nobody-should-get");

        resent.IsSuccess.ShouldBeFalse($"an expired invitation was resent to a {status} user");
        resent.Error!.Code.ShouldBe(ErrorCode.Conflict);
        cluster.Mail.Count(x => x.Email == email).ShouldBe(1, "the refused resend mailed the address");

        // Refused before anything was written: the old record, and its old expiry, stand.
        var after = (await invitation.GetAsync()).GetValueOrThrow();

        after.Sendings.ShouldBe(1);
        after.ExpiresAt.ShouldBeLessThan(cluster.Clock.UtcNow);
    }

    [Fact]
    public async Task AnInvitationIsMailedAtMostMaxSendingsTimes() {
        var (invitation, _) = await cluster.InviteAsync("resent-too-often@example.com");

        for (var sending = 2; sending <= InvitationPolicy.MaxSendings; sending++) {
            var resent = await invitation.ResendAsync($"link-{sending}");

            resent.IsSuccess.ShouldBeTrue(resent.Error?.Message);
            resent.GetValueOrThrow().Sendings.ShouldBe(sending);
        }

        var refused = await invitation.ResendAsync("one-link-too-many");

        refused.Error!.Code.ShouldBe(ErrorCode.QuotaExceeded);
        cluster.Mail.Count(x => x.Email == "resent-too-often@example.com").ShouldBe(InvitationPolicy.MaxSendings);

        // The last link mailed still works: the refusal replaced nothing.
        var last = cluster.Mail.Last(x => x.Email == "resent-too-often@example.com");

        (await invitation.DescribeAsync(last.Secret)).GetValueOrThrow().Status.ShouldBe(InvitationStatus.Pending);
    }
}

/// <summary>
///     One silo with the identity module, a clock this class moves and an invitation mail that keeps
///     what it's handed.
/// </summary>
public sealed class InvitationExpiryCluster : IAsyncLifetime {
    TestCluster cluster = null!;

    /// <summary>The tenant every invitation here is into.</summary>
    public static Guid Tenant { get; } = Guid.Parse("41414141-4141-4141-8141-414141414141");

    /// <summary>The clock the silo reads. Forward only; nothing here resets it.</summary>
    public ExpiryClock Clock { get; } = new();

    /// <summary>Every invitation mail, oldest first.</summary>
    public ConcurrentQueue<InvitationDelivery> Mail { get; } = new();

    /// <summary>
    ///     The live fixture, for the silo configurator to reach — <c>AddSiloBuilderConfigurator&lt;T&gt;</c>
    ///     constructs its argument with <c>new()</c>.
    /// </summary>
    internal static InvitationExpiryCluster Instance { get; private set; } = null!;

    /// <summary>A user grain in <see cref="Tenant" />.</summary>
    /// <param name="userId">Which user.</param>
    public IUserGrain User(Guid userId) => For().GetGrain<IUserGrain>(GrainKeys.User(userId));

    /// <summary>Invites <paramref name="email" /> and answers the invitation's grain and its user.</summary>
    /// <param name="email">A lower-case address no other test in the class uses.</param>
    public async Task<(IInvitationGrain Invitation, Guid UserId)> InviteAsync(string email) {
        var invitation = For().GetGrain<IInvitationGrain>(GrainKeys.Invitation(Guid.NewGuid()));

        var created = await invitation.CreateAsync(
            new() { Email = email, InvitedBy = Guid.NewGuid(), TenantName = "Expiry" },
            "first-link-secret"
        );

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        return (invitation, created.GetValueOrThrow().UserId);
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        Instance = this;

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

    TenantGrainFactory For() => cluster.GrainFactory.ForTenant(Tenant.ToString("D", CultureInfo.InvariantCulture));

    sealed class SiloConfigurator : ISiloConfigurator {
        public void Configure(ISiloBuilder silo) {
            silo.AddMemoryGrainStorage(StorageTiers.Durable);
            silo.AddMemoryGrainStorage(StorageTiers.Hot);
            silo.UseInMemoryReminderService();

            silo.ConfigureServices(static services => {
                    // FIRST, so the module's TryAdd keeps them.
                    services.AddSingleton<IClock>(Instance.Clock);
                    services.AddSingleton<IPasswordHasher>(CheapArgon2.Hasher);
                    services.AddSingleton<IInvitationDeliverySeam>(new KeepingDelivery(Instance.Mail));
                    services.TryAddSingleton<ILoggerFactory>(static _ => NullLoggerFactory.Instance);
                }
            );

            silo.AddCyberCloudIdentity();
            silo.AddCyberCloudAuthorization();
        }
    }

    sealed class KeepingDelivery(ConcurrentQueue<InvitationDelivery> mail) : IInvitationDeliverySeam {
        public Task<Result> DeliverAsync(InvitationDelivery delivery, CancellationToken cancellationToken = default) {
            mail.Enqueue(delivery);

            return Task.FromResult(Result.Success);
        }
    }
}

/// <summary>A clock <see cref="InvitationExpiryTests" /> moves, and nothing else reads.</summary>
public sealed class ExpiryClock : IClock {
    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Moves time forward.</summary>
    /// <param name="by">How far.</param>
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Binds <see cref="InvitationExpiryCluster" /> to the class that shares it.</summary>
[CollectionDefinition(Name)]
public sealed class InvitationExpirySuite : ICollectionFixture<InvitationExpiryCluster> {
    /// <summary>The collection name.</summary>
    public const string Name = "invitation-expiry";
}
