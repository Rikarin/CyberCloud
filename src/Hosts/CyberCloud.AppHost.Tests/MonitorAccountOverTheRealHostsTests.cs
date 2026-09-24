using CyberCloud.Providers.Monitor.Accounts;

namespace CyberCloud.AppHost.Tests;

/// <summary>
///     The accountID ledger reached from outside the silo processes, the way the gateway reaches it —
///     #41's review, docs/plan/16 § Querying a workspace.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Why a test of one grain call needs the real topology.</b> The explorer's metrics
///         handlers run in the gateway, an Orleans <i>client</i> in its own process, and ask
///         <c>IMonitorAccountGrain</c> in a silo. Every other test of the ledger runs in one
///         <c>TestCluster</c>, whose client and silo share a type manifest, and that is exactly the
///         arrangement that hid #39's shard-map confirmation: the invokable was refused only when it
///         crossed a process. Here the caller is <see cref="LocalTopology.Client" />, a client in this
///         process, and the grain activates in one of the two silo processes, with its claim written to
///         the durable tier's null-tenant shard in the AppHost's PostgreSQL.
///     </para>
///     <para>
///         ⚠ <b>Fresh accounts and GUIDs on every run</b>, because the claim is durable and never
///         released, and a volume kept between runs would otherwise answer the second run with the
///         first run's holder.
///     </para>
/// </remarks>
[Collection(LocalTopologySuite.Name)]
public sealed class MonitorAccountOverTheRealHostsTests(LocalTopology topology) {
    [Fact]
    public async Task AClaimMadeFromAnotherProcessHoldsAndASecondWorkspaceIsRefused() {
        var token = TestContext.Current.CancellationToken;
        var account = (uint)Random.Shared.Next(1, int.MaxValue);
        var holder = Guid.NewGuid();
        var collider = Guid.NewGuid();

        (await new GrainMonitorAccounts(topology.Client).ClaimAsync(account, holder, token))
            .ShouldBeTrue($"a free account {account} could not be claimed across the process boundary");

        // A second seam, so the first one's cache can't answer: every answer below is the silo's.
        var gateway = new GrainMonitorAccounts(topology.Client);

        (await gateway.IsHeldByAsync(account, holder, token)).ShouldBeTrue();
        (await gateway.IsHeldByAsync(account, collider, token)).ShouldBeFalse();
        (await gateway.ClaimAsync(account, collider, token))
            .ShouldBeFalse("a second workspace folding onto a held account was given it by the real silo");
        (await gateway.IsHeldByAsync(account, holder, token)).ShouldBeTrue("the refused claim moved the account");
    }
}
