using CyberCloud.Core.Contracts;

namespace CyberCloud.ServiceDefaults;

/// <summary>
///     What a Cyber Cloud silo or client needs to know about the cluster it belongs to.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/04:6-14 — there are <b>three</b> clusters and conflating them is named as the
///         first mistake to avoid: a regional control plane per region, one global directory, and a
///         tenant's own Kubernetes cluster (which is not an Orleans cluster at all). A process joins
///         exactly one of the first two, and <see cref="ClusterId" /> is what says which.
///     </para>
///     <para>
///         ⚠ <b><see cref="ServiceId" /> is part of the physical storage key.</b> Orleans' ADO.NET
///         grain-storage schema has a <c>ServiceId</c> column (docs/plan/05:80-82), so changing it
///         orphans every stored grain in the durable tier. It is separate from
///         <see cref="ClusterId" /> for that reason: a cluster can be rebuilt, renamed or replaced
///         and keep its state, which is what makes a blue/green cluster swap possible at all.
///     </para>
/// </remarks>
public sealed class CyberCloudClusterOptions
{
    /// <summary>The configuration section these bind from.</summary>
    public const string SectionName = "CyberCloud:Cluster";

    /// <summary>
    ///     The membership scope. Silos with the same <see cref="ClusterId" /> find each other; silos
    ///     with different ones do not, even in the same namespace.
    /// </summary>
    public string ClusterId { get; set; } = "cybercloud";

    /// <summary>
    ///     The storage scope. ⚠ Changing this orphans stored grain state — see the remarks on the
    ///     type.
    /// </summary>
    public string ServiceId { get; set; } = "cybercloud";

    /// <summary>
    ///     The grain-storage provider bound to <see cref="StorageTiers.Hot" />. Present so a test or
    ///     a local run can substitute an in-memory provider without a second code path in
    ///     <see cref="OrleansApplication.CreateSilo" />.
    /// </summary>
    public string HotStorageProvider { get; set; } = StorageTiers.Hot;

    /// <summary>The grain-storage provider bound to <see cref="StorageTiers.Durable" />.</summary>
    public string DurableStorageProvider { get; set; } = StorageTiers.Durable;

    /// <summary>The stream provider — docs/plan/04:129, one provider named <c>Events</c>.</summary>
    public string EventStreamProvider { get; set; } = StreamProviders.Events;

    /// <summary>
    ///     The silo-to-silo port used in development only. ⚠ See the remarks.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>These two exist because Orleans' defaults are not free ports on a developer's
    ///         machine.</b> docs/plan/04:60 writes <c>b.UseLocalhostClustering()</c> with no
    ///         arguments, which binds <b>11111</b> and <b>30000</b>. 30000 in particular is a
    ///         popular port — it was already taken by an unrelated process on the machine this was
    ///         written on, and the failure is <c>AddressInUseException</c> out of a socket bind deep
    ///         in <c>Orleans.Networking.Shared.SocketConnectionListener</c>, which names neither
    ///         Orleans' defaults nor the process holding the port.
    ///     </para>
    ///     <para>
    ///         It also makes running two silos on one machine — the obvious thing to do when
    ///         checking placement or a failover by hand — impossible without editing code.
    ///     </para>
    ///     <para>
    ///         Production is unaffected: these are read only on the <c>IsDevelopment()</c> branch,
    ///         where Kubernetes membership is not available.
    ///     </para>
    /// </remarks>
    public int LocalhostSiloPort { get; set; } = 11111;

    /// <summary>The client-facing gateway port used in development only. See
    ///     <see cref="LocalhostSiloPort" />.</summary>
    public int LocalhostGatewayPort { get; set; } = 30000;
}
