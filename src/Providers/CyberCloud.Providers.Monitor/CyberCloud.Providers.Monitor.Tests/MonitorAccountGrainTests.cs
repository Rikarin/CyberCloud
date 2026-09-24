using CyberCloud.Providers.Monitor.Accounts;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.Providers.Monitor.Tests;

/// <summary>
///     The accountID ledger against a real silo: first claim wins, the loser learns nothing but "no",
///     and a tenant-qualified activation refuses to be a ledger at all. #41's review.
/// </summary>
/// <remarks>
///     ⚠ Every test takes accounts of its own from <see cref="NextAccount" />, because the silo is the
///     collection's and a claim is never released.
/// </remarks>
[Collection(AlertSilo.Name)]
public sealed class MonitorAccountGrainTests(AlertTestCluster cluster) {
    static int next = 4_000;

    static CancellationToken Ct => AlertTestCluster.Ct;

    static uint NextAccount() => (uint)Interlocked.Increment(ref next);

    [Fact]
    public async Task TheFirstWorkspaceToClaimAnAccountHoldsItAndASecondIsRefused() {
        var ledger = new GrainMonitorAccounts(cluster.Grains);
        var account = NextAccount();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        (await ledger.IsHeldByAsync(account, first, Ct)).ShouldBeFalse("nobody has claimed it yet");

        (await ledger.ClaimAsync(account, first, Ct)).ShouldBeTrue();
        (await ledger.ClaimAsync(account, first, Ct)).ShouldBeTrue("a claim is idempotent for its holder — every reconcile pass makes it");

        (await ledger.ClaimAsync(account, second, Ct)).ShouldBeFalse("a second workspace folding onto a held account was let in");
        (await ledger.IsHeldByAsync(account, second, Ct)).ShouldBeFalse();
        (await ledger.IsHeldByAsync(account, first, Ct)).ShouldBeTrue("the refused claim took the account from its holder");
    }

    [Fact]
    public async Task ANoIsNotRememberedSoAWorkspaceThatClaimsLaterIsAnswered() {
        // ⚠ The seam caches every "yes" and no "no". A cached "no" would keep refusing the explorer to
        // a workspace whose create simply hadn't converged when the first query came in.
        var ledger = new GrainMonitorAccounts(cluster.Grains);
        var account = NextAccount();
        var workspace = Guid.NewGuid();

        (await ledger.IsHeldByAsync(account, workspace, Ct)).ShouldBeFalse();
        (await ledger.ClaimAsync(account, workspace, Ct)).ShouldBeTrue();
        (await ledger.IsHeldByAsync(account, workspace, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task TwoProcessesSeeOneLedger() {
        // The reconciler's claim is made from the silo and the explorer's check from the gateway; two
        // seam instances stand in for the two processes' caches, and the grain is what they share.
        var silo = new GrainMonitorAccounts(cluster.Grains);
        var gateway = new GrainMonitorAccounts(cluster.Grains);
        var account = NextAccount();
        var holder = Guid.NewGuid();

        (await silo.ClaimAsync(account, holder, Ct)).ShouldBeTrue();
        (await gateway.IsHeldByAsync(account, holder, Ct)).ShouldBeTrue();
        (await gateway.ClaimAsync(account, Guid.NewGuid(), Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task ATenantQualifiedLedgerRefusesToActivate() {
        // ⚠ The mistake this shape exists to make impossible: ForTenant(a) and ForTenant(b) would be two
        // ledgers, each answering "free" for an account the other holds.
        var qualified = cluster.Grains.ForTenant(AlertTestCluster.Tenant.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IMonitorAccountGrain>(GrainKeys.MetricsAccount(NextAccount()));

        var thrown = await Should.ThrowAsync<Exception>(() => qualified.ClaimAsync(Guid.NewGuid()));

        thrown.ToString().ShouldContain("WITHOUT ForTenant");
    }

    [Fact]
    public async Task AnEmptyGuidCannotClaim() {
        var ledger = new GrainMonitorAccounts(cluster.Grains);

        await Should.ThrowAsync<ArgumentException>(() => ledger.ClaimAsync(NextAccount(), Guid.Empty, Ct));
    }
}
