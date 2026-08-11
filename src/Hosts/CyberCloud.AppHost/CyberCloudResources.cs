namespace CyberCloud.AppHost;

/// <summary>
///     The names and ports the local topology is built from.
/// </summary>
/// <remarks>
///     <para>
///         These are constants rather than literals in <c>Program.cs</c> because the exit-criterion
///         test in <c>CyberCloud.AppHost.Tests</c> has to reach the same silos through the same
///         gateway ports. A test that re-declared them would pass on a topology that no longer
///         exists.
///     </para>
///     <para>
///         ⚠ <b>The Orleans ports are fixed, and everything else Aspire allocates.</b> Aspire hands
///         each project a free HTTP port and injects it as <c>ASPNETCORE_URLS</c>; it knows nothing
///         about Orleans' silo-to-silo and gateway sockets, which are opened by the Orleans runtime
///         long after the process has started. So those four have to be chosen here, and they must
///         not be Orleans' defaults — <c>CyberCloudClusterOptions.LocalhostGatewayPort</c> explains
///         why 30000 in particular is a bad bet on a developer's machine.
///     </para>
/// </remarks>
public static class CyberCloudResources
{
    /// <summary>The Redis container that backs the hot tier — docs/plan/05 § Hot.</summary>
    public const string Redis = "hot";

    /// <summary>The PostgreSQL server that carries every durable shard — docs/plan/05 § Durable.</summary>
    public const string Postgres = "durable";

    /// <summary>The NATS server — ADR-005, docs/plan/04 § Streams.</summary>
    public const string Nats = "nats";

    /// <summary>The k3s container — ADR-014, and the data plane of ADR-001.</summary>
    public const string K3s = "k3s";

    /// <summary>The one-shot job that creates the Orleans grain-storage schema on every shard.</summary>
    public const string DurableSchema = "durable-schema";

    /// <summary>The first silo. It holds the development membership table.</summary>
    public const string SiloOne = "silo-1";

    /// <summary>The second silo. It joins <see cref="SiloOne" />.</summary>
    public const string SiloTwo = "silo-2";

    /// <summary>The first tenant-carrying durable shard.</summary>
    public const string ShardA = "durable00";

    /// <summary>The second tenant-carrying durable shard.</summary>
    public const string ShardB = "durable01";

    /// <summary>
    ///     The shard that carries every null-tenant platform grain — the tenant directory and the
    ///     shard map (docs/plan/04 § Grain taxonomy, the Platform row).
    /// </summary>
    public const string PlatformShard = "platform00";

    /// <summary>Silo 1's silo-to-silo port. Also the cluster's primary-silo endpoint.</summary>
    public const int SiloOnePort = 11111;

    /// <summary>Silo 1's client-facing gateway port.</summary>
    public const int SiloOneGatewayPort = 30011;

    /// <summary>Silo 2's silo-to-silo port.</summary>
    public const int SiloTwoPort = 11112;

    /// <summary>Silo 2's client-facing gateway port.</summary>
    public const int SiloTwoGatewayPort = 30012;

    /// <summary>The host port k3s' API server is published on.</summary>
    /// <remarks>
    ///     Fixed rather than allocated because the kubeconfig k3s writes names a port, and a
    ///     kubeconfig whose port changes on every run is a kubeconfig nobody can use.
    /// </remarks>
    public const int K3sApiPort = 6443;
}
