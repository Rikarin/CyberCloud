using CyberCloud.Core.Time;
using CyberCloud.Kubernetes.Apply;
using CyberCloud.Kubernetes.Tunnel;
using Microsoft.Extensions.Logging;
using System.Text;
using k8s;

namespace CyberCloud.Kubernetes.Connections;

/// <summary>
///     Turns a <see cref="ClusterConnectionDescriptor" /> into a live API client.
/// </summary>
/// <remarks>
///     ⚠ A seam rather than a call, for two reasons. The credential resolution behind
///     <see cref="ClusterConnectionDescriptor.CredentialRef" /> is <c>CyberCloud.KeyVault</c>'s job
///     (docs/plan/18) and does not exist yet; and the grain has to be testable against a k3s
///     container whose kubeconfig is a local file, which is not how production resolves one.
/// </remarks>
public interface IKubeApiClientFactory {
    /// <summary>Connects to a cluster.</summary>
    /// <param name="descriptor">Which cluster and how to authenticate.</param>
    /// <param name="cancellationToken">The activation's token.</param>
    Task<Result<IKubeApiClient>> ConnectAsync(
        ClusterConnectionDescriptor descriptor,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
///     The production factory: a kubeconfig for <see cref="ClusterConnectionKind.Kubeconfig" />, the
///     agent tunnel for <see cref="ClusterConnectionKind.AgentInitiated" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             <see cref="ClusterConnectionKind.AgentInitiated" /> used to be refused here with a
///             message pointing at the document, and the refusal was the right thing until #36.
///         </b>
///         docs/plan/09 § Cluster connections:
///         <i>
///             "AgentInitiated is not optional and is easy to defer into a crisis. The brief's 'connection
///             string to kubernetes' implies inbound reachability, and for a tenant's on-prem cluster that is
///             usually false."
///         </i>
///         It now hands back a <see cref="TunnelKubeApiClient" /> over a
///         <see cref="GrainTunnelRoute" /> — the same <see cref="IKubeApiClient" /> surface, with the
///         socket in the gateway and the multiplexer in <c>AgentTunnelGrain</c>. The connection grain
///         cannot tell the two kinds apart, and its tenancy check, health window and suspend rule run
///         identically over both. ⚠ A stub that pretended to connect would still be exactly the way
///         to discover the gap at the first on-prem customer, which is why the route is refused by
///         name when this factory was built without a grain factory.
///     </para>
/// </remarks>
/// <param name="clock">The clock a drift event's timestamp comes from.</param>
/// <param name="logger">
///     Where an API server's refusal is written in full. ⚠ This is the <i>only</i> place the API
///     server's own message survives — a refusal's tenant-facing half deliberately drops the parts
///     that name the platform's service account, its namespaces or its hosts. See
///     <see cref="KubeRefusal" />.
/// </param>
/// <param name="grains">
///     The grain factory an agent tunnel is routed through, or <see langword="null" /> in a process
///     that has no silo — in which case an <see cref="ClusterConnectionKind.AgentInitiated" />
///     descriptor is refused rather than half-served.
/// </param>
public sealed class KubeApiClientFactory(
    IClock clock,
    ILogger<KubeApiClientFactory>? logger = null,
    IGrainFactory? grains = null
)
    : IKubeApiClientFactory {
    /// <summary>
    ///     Resolves a credential reference to kubeconfig YAML.
    /// </summary>
    /// <remarks>
    ///     ⚠ The vault seam. docs/plan/09 § Cluster connections keeps the kubeconfig "in Vault", and
    ///     <c>CyberCloud.KeyVault</c> (docs/plan/18) does not exist. Until it does this resolves
    ///     nothing, and a <see cref="ClusterConnectionKind.Kubeconfig" /> connection therefore has to
    ///     be supplied by whoever registers the factory. The refusal names the assembly rather than
    ///     failing with "not found", so the missing piece is legible.
    /// </remarks>
    public Func<string, CancellationToken, Task<Result<string>>>? ResolveKubeconfig { get; init; }

    /// <inheritdoc />
    public async Task<Result<IKubeApiClient>> ConnectAsync(
        ClusterConnectionDescriptor descriptor,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(descriptor);

        switch (descriptor.Kind) {
            case ClusterConnectionKind.AgentInitiated:
                if (grains is null) {
                    return Result<IKubeApiClient>.Failure(
                        ErrorCode.InternalError,
                        $"Cluster {descriptor.ClusterId:D} is agent-initiated and this process has no "
                        + "grain factory to route the tunnel through. An agent tunnel is multiplexed by "
                        + "IAgentTunnelGrain on a silo; a KubeApiClientFactory built without one can "
                        + "serve kubeconfig connections only."
                    );
                }

                return Result<IKubeApiClient>.Success(
                    new TunnelKubeApiClient(descriptor.ClusterId, new GrainTunnelRoute(grains, descriptor.ClusterId))
                );

            case ClusterConnectionKind.ServiceAccountToken:
            case ClusterConnectionKind.InHouse:
            case ClusterConnectionKind.Kubeconfig:
                break;

            case ClusterConnectionKind.Unknown:
            default:
                return Result<IKubeApiClient>.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"Cluster {descriptor.ClusterId:D} has no connection kind. The kinds are the "
                    + "table at docs/plan/09 § Cluster connections."
                );
        }

        if (ResolveKubeconfig is null) {
            return Result<IKubeApiClient>.Failure(
                ErrorCode.InternalError,
                $"No kubeconfig resolver is registered, so cluster {descriptor.ClusterId:D} cannot "
                + "be reached. docs/plan/09 § Cluster connections keeps the kubeconfig in Vault and "
                + "CyberCloud.KeyVault (docs/plan/18) is not built yet, so the resolver has to be "
                + "supplied at registration time — see KubeApiClientFactory.ResolveKubeconfig."
            );
        }

        var resolved = await ResolveKubeconfig(descriptor.CredentialRef, cancellationToken)
            .ConfigureAwait(false);

        if (resolved.TryGetError(out var error)) {
            return Result<IKubeApiClient>.Failure(error);
        }

        try {
            using var yaml = new MemoryStream(Encoding.UTF8.GetBytes(resolved.GetValueOrThrow()));

            var config = await KubernetesClientConfiguration
                .BuildConfigFromConfigFileAsync(yaml)
                .ConfigureAwait(false);

            return Result<IKubeApiClient>.Success(
                new KubeApiClient(new k8s.Kubernetes(config), descriptor.ClusterId, clock, logger: logger)
            );
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return Result<IKubeApiClient>.Failure(
                ErrorCode.InternalError,
                $"The kubeconfig for cluster {descriptor.ClusterId:D} could not be used: "
                + $"{ex.GetType().Name}: {ex.Message}"
            );
        }
    }
}
