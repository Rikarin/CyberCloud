using CyberCloud.Silo.Host.Hello;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.AppHost.Tests;

/// <summary>
///     "A <b>two-silo</b> cluster" — docs/plan/24 § Phase 0, and the word this file exists for.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two silos is not two processes.</b> Everything built in this repository so far has
///         been tested on one in-process silo, which hides activation placement, the grain directory
///         and every multi-silo failure mode. And the specific way a local two-silo cluster goes
///         wrong is <i>silently</i>: <c>UseLocalhostClustering(siloPort, gatewayPort)</c> makes each
///         silo its own primary, so two silos become two one-silo clusters, both healthy, both
///         serving, neither aware of the other. The fix is
///         <c>CyberCloudClusterOptions.LocalhostPrimarySiloPort</c>; these are the tests that would
///         have caught its absence.
///     </para>
/// </remarks>
[Collection(LocalTopologySuite.Name)]
public sealed class TwoSiloClusterTests(LocalTopology topology) {
    static readonly Guid Tenant = new("0d1f0dfe-4c7e-4f2c-9b5b-2f9b4d0a0003");

    [Fact]
    public async Task BothSilosAreInOneMembershipTable() {
        var management = topology.Client.GetGrain<IManagementGrain>(0);
        var hosts = await management.GetHosts(true);

        // ⚠ This is the assertion that distinguishes one cluster from two. A membership table is
        // per-cluster: if the two silos had not agreed on a primary, each would answer this with
        // exactly one entry — its own — and both answers would look like a healthy cluster.
        hosts.Count.ShouldBe(
            2,
            "the cluster's membership table has "
            + $"{string.Join(", ", hosts.Select(x => $"{x.Key} = {x.Value}"))}. Two silos that each "
            + "hold their own development membership table are two clusters, not one — see "
            + "CyberCloudClusterOptions.LocalhostPrimarySiloPort."
        );

        hosts.Values.ShouldAllBe(status => status == SiloStatus.Active);
    }

    /// <summary>
    ///     Waits until the cluster's membership reports both silos <see cref="SiloStatus.Active" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A PRECONDITION, NOT AN ASSERTION, AND THE TWO SAMPLING TESTS BELOW WERE MISSING
    ///         IT.</b> Each says "40 activations over 2 silos miss one silo with probability
    ///         2^-39" — which is true only once the placement director can SEE two silos. The fixture
    ///         waits for both silos to report healthy and then starts the client, and a silo is
    ///         healthy as soon as its own runtime routes messages; membership reaching the client
    ///         takes another round after that. So whichever sampling test runs first races that
    ///         round, places all forty activations on the one silo it knows about, and reports it as
    ///         a placement failure.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It was found by adding a fifteenth provider module to the silo host</b>, which
    ///         lengthened silo two's start-up enough to lose the race about two runs in three —
    ///         measured, against three clean runs on the commit before it. Nothing about placement
    ///         changed; the race was always there and the margin had simply been wide enough.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This does NOT belong in the fixture, and that is deliberate.</b> Waiting there
    ///         would make <see cref="BothSilosAreInOneMembershipTable" /> assert something the
    ///         fixture had just guaranteed — a test that passes by construction, which is worth less
    ///         than no test. Here it is the precondition of the two tests that need it, and the
    ///         membership test still converges on its own or fails.
    ///     </para>
    /// </remarks>
    async Task BothSilosAreVisibleAsync() {
        var management = topology.Client.GetGrain<IManagementGrain>(0);

        for (var attempt = 0; attempt < 50; attempt++) {
            var hosts = await management.GetHosts(true);

            if (hosts.Count(x => x.Value == SiloStatus.Active) >= 2) {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        }

        // ⚠ Not an Assert.Skip and not a silent return. If membership never converges the two tests
        // below are meaningless, and the honest report is that the cluster never formed — which is
        // BothSilosAreInOneMembershipTable's failure, reached from here.
        throw new InvalidOperationException(
            "the cluster never reported two Active silos, so placement could not be sampled. That is "
            + "a membership failure rather than a placement one — see BothSilosAreInOneMembershipTable."
        );
    }

    [Fact]
    public async Task ActivationsAreSpreadOverBothSilos() {
        await BothSilosAreVisibleAsync();

        var tenant = topology.Client.ForTenant(Id(Tenant));
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Orleans' default placement is random, so this is a sampling loop rather than an assertion
        // about any one key. 40 activations over 2 silos miss one silo with probability 2^-39.
        for (var i = 0; i < 40 && seen.Count < 2; i++) {
            var key = string.Create(CultureInfo.InvariantCulture, $"hello/spread-{i}");
            seen.Add(await tenant.GetGrain<IHelloGrain>(key).SiloAddressAsync());
        }

        seen.Count.ShouldBe(
            2,
            $"40 activations all landed on {string.Join(" and ", seen)}. Either there is one silo, "
            + "or the two are not in one cluster and the client is only reaching one of them."
        );
    }

    [Fact]
    public async Task AGrainReachesAGrainOnTheOtherSilo() {
        await BothSilosAreVisibleAsync();

        var tenant = topology.Client.ForTenant(Id(Tenant));

        var byAddress = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < 40 && byAddress.Count < 2; i++) {
            var key = string.Create(CultureInfo.InvariantCulture, $"hello/cross-{i}");
            var address = await tenant.GetGrain<IHelloGrain>(key).SiloAddressAsync();
            byAddress.TryAdd(address, key);
        }

        byAddress.Count.ShouldBe(2, "no pair of activations on different silos was found.");

        var (firstSilo, firstKey) = (byAddress.First().Key, byAddress.First().Value);
        var (secondSilo, secondKey) = (byAddress.Last().Key, byAddress.Last().Value);

        // ⚠ THE EVIDENCE. Every other assertion here is something a client can observe from outside;
        // this one is a message that leaves a grain on silo A, is routed by the cluster's grain
        // directory, crosses the silo-to-silo connection and is answered by a grain on silo B. Two
        // disjoint clusters cannot do it, and neither can one silo.
        var reached = await tenant.GetGrain<IHelloGrain>(firstKey).SiloAddressOfAsync(secondKey);

        reached.ShouldBe(secondSilo);
        reached.ShouldNotBe(firstSilo);

        var back = await tenant.GetGrain<IHelloGrain>(secondKey).SiloAddressOfAsync(firstKey);

        back.ShouldBe(firstSilo);
    }

    [Fact]
    public async Task AGrainKeepsItsStateWhenItIsReachedThroughTheOtherSilosGateway() {
        // The client holds both gateways and picks one per grain reference. Writing through one
        // reference and reading through a freshly resolved one is the client-side half of "one
        // cluster": if the two silos were separate clusters, the second reference would routinely
        // reach a different silo's activation, whose storage read would be against the same Redis
        // and the same shard and would therefore still return the value — which is exactly why this
        // is the WEAKEST of the four tests here and why AGrainReachesAGrainOnTheOtherSilo exists.
        var tenant = topology.Client.ForTenant(Id(Tenant));

        await tenant.GetGrain<IHelloGrain>("hello/gateway").SayHelloAsync("across the gateway");

        var readBack = await tenant.GetGrain<IHelloGrain>("hello/gateway").ReadBackAsync();

        readBack.DurableGreeting.ShouldBe("across the gateway");
        readBack.HotGreeting.ShouldBe("across the gateway");
    }

    static string Id(Guid tenant) => tenant.ToString("D", CultureInfo.InvariantCulture);
}
