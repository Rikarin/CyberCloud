using CyberCloud.Core.Time;
using CyberCloud.Kubernetes.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CyberCloud.Kubernetes;

/// <summary>Registers the Kubernetes fabric on a silo — docs/plan/09.</summary>
public static class KubernetesSiloBuilderExtensions {
    /// <summary>
    ///     Adds the cluster-connection grain, its tenancy filter and the default seams.
    /// </summary>
    /// <param name="silo">The silo being built.</param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             <see cref="ClusterConnectionTenantFilter" /> is not optional and is registered
    ///             here so it cannot be forgotten.
    ///         </b>
    ///         It is what establishes
    ///         <see cref="CallerTenant.Current" />, which is the only input to the one tenancy check
    ///         a null-tenant grain gets (docs/plan/06 § Grain keys). Without it every call reads
    ///         <see cref="CallerKind.Unknown" /> and is refused — the grain fails closed rather than
    ///         open — but a cluster fabric that refuses everything is still an outage, so the
    ///         registration lives next to the grain's rather than in a host's startup where it could
    ///         be reordered away.
    ///     </para>
    ///     <para>
    ///         <c>TryAddSingleton</c> for the two seams, so a host
    ///         that has already registered a real
    ///         <see cref="IClusterOperatorAuthority" /> or <see cref="IKubeApiClientFactory" /> wins.
    ///         The defaults deny and refuse respectively.
    ///     </para>
    /// </remarks>
    public static ISiloBuilder AddCyberCloudKubernetes(this ISiloBuilder silo) {
        ArgumentNullException.ThrowIfNull(silo);

        return silo.ConfigureServices(services => {
                services.AddSingleton<IIncomingGrainCallFilter, ClusterConnectionTenantFilter>();

                services.TryAddSingleton<IClock, SystemClock>();
                services.TryAddSingleton<IClusterOperatorAuthority, DenyClusterOperatorAuthority>();
                services.TryAddSingleton<IKubeApiClientFactory>(sp =>
                    new KubeApiClientFactory(sp.GetRequiredService<IClock>())
                );

                services.AddOptions<KubernetesOptions>()
                    .BindConfiguration(KubernetesOptions.SectionName);
            }
        );
    }
}
