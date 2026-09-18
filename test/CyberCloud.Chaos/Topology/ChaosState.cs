using CyberCloud.Core.Time;
using CyberCloud.ServiceDefaults.Storage;

namespace CyberCloud.Chaos.Topology;

/// <summary>
///     What the silo configurator reads, because Orleans constructs it with <c>new()</c> and nothing
///     can be handed to it.
/// </summary>
/// <remarks>
///     ⚠ Static for the reason <c>ClusterConformanceState&lt;T&gt;</c> gives, and safe for the reason
///     <c>AssemblyInfo.cs</c> gives: one topology per process, one test at a time. Set once by
///     <see cref="ChaosTopology.InitializeAsync" /> before the first silo starts and never written
///     again — a silo started later by a test (invariants 1 and 7) reads the same values, which is
///     what makes it a member of the same cluster over the same stores.
/// </remarks>
static class ChaosState {
    /// <summary>The two-tier storage configuration every silo binds — hot Redis, three PostgreSQL shards.</summary>
    public static CyberCloudStorageOptions Storage { get; set; } = new();

    /// <summary>The Redis the reminder table lives in. The same server as the hot tier, as CyberCloud.Silo.Host wires it.</summary>
    public static string RedisConnectionString { get; set; } = string.Empty;

    /// <summary>
    ///     The k3s kubeconfig, handed to every silo's <c>KubeApiClientFactory</c> as the answer to any
    ///     credential reference — the topology's stand-in for the vault docs/plan/09 § Cluster
    ///     connections keeps kubeconfigs in.
    /// </summary>
    public static string Kubeconfig { get; set; } = string.Empty;

    /// <summary>The clock. The system's, because a chaos suite that advanced a fake clock would be testing its own arithmetic.</summary>
    public static IClock Clock { get; } = new SystemClock();
}
