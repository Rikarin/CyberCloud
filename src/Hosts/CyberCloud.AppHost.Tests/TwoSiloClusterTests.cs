using System.Globalization;
using CyberCloud.Silo.Host.Hello;
using Orleans.Multitenant;
using Orleans.Runtime;

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
public sealed class TwoSiloClusterTests(LocalTopology topology)
{
    static readonly Guid Tenant = new("0d1f0dfe-4c7e-4f2c-9b5b-2f9b4d0a0003");

    static string Id(Guid tenant) => tenant.ToString("D", CultureInfo.InvariantCulture);

    [Fact]
    public async Task BothSilosAreInOneMembershipTable()
    {
        var management = topology.Client.GetGrain<IManagementGrain>(0);
        var hosts = await management.GetHosts(onlyActive: true);

        // ⚠ This is the assertion that distinguishes one cluster from two. A membership table is
        // per-cluster: if the two silos had not agreed on a primary, each would answer this with
        // exactly one entry — its own — and both answers would look like a healthy cluster.
        hosts.Count.ShouldBe(
            2,
            "the cluster's membership table has "
            + $"{string.Join(", ", hosts.Select(x => $"{x.Key} = {x.Value}"))}. Two silos that each "
            + "hold their own development membership table are two clusters, not one — see "
            + "CyberCloudClusterOptions.LocalhostPrimarySiloPort.");

        hosts.Values.ShouldAllBe(status => status == SiloStatus.Active);
    }

    [Fact]
    public async Task ActivationsAreSpreadOverBothSilos()
    {
        var tenant = topology.Client.ForTenant(Id(Tenant));
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Orleans' default placement is random, so this is a sampling loop rather than an assertion
        // about any one key. 40 activations over 2 silos miss one silo with probability 2^-39.
        for (var i = 0; i < 40 && seen.Count < 2; i++)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"hello/spread-{i}");
            seen.Add(await tenant.GetGrain<IHelloGrain>(key).SiloAddressAsync());
        }

        seen.Count.ShouldBe(
            2,
            $"40 activations all landed on {string.Join(" and ", seen)}. Either there is one silo, "
            + "or the two are not in one cluster and the client is only reaching one of them.");
    }

    [Fact]
    public async Task AGrainReachesAGrainOnTheOtherSilo()
    {
        var tenant = topology.Client.ForTenant(Id(Tenant));

        var byAddress = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < 40 && byAddress.Count < 2; i++)
        {
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
    public async Task AGrainKeepsItsStateWhenItIsReachedThroughTheOtherSilosGateway()
    {
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
}
