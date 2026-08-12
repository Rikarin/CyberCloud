using CyberCloud.Core.Time;
using CyberCloud.Kubernetes.Apply;
using k8s;
using Microsoft.Extensions.Logging;
using System.Text;

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
///     The production factory: a kubeconfig for <see cref="ClusterConnectionKind.Kubeconfig" />, and
///     an explicit refusal for the kinds docs/plan/09 defers.
/// </summary>
/// <remarks>
///     ⚠
///     <b>
///         <see cref="ClusterConnectionKind.AgentInitiated" /> throws rather than silently
///         misbehaving
///     </b>
///     , and the message points at the document. docs/plan/09 § Cluster connections:
///     <i>
///         "AgentInitiated is not optional and is easy to defer into a crisis. The brief's 'connection
///         string to kubernetes' implies inbound reachability, and for a tenant's on-prem cluster that is
///         usually false. Budget it as part of the fabric (1.5 EM, M2) rather than discovering it at the
///         first on-prem customer."
///     </i>
///     A stub that pretended to connect would be exactly the way to
///     discover it at the first on-prem customer.
/// </remarks>
/// <param name="clock">The clock a drift event's timestamp comes from.</param>
/// <param name="logger">
///     Where an API server's refusal is written in full. ⚠ This is the <i>only</i> place the API
///     server's own message survives — a refusal's tenant-facing half deliberately drops the parts
///     that name the platform's service account, its namespaces or its hosts. See
///     <see cref="KubeRefusal" />.
/// </param>
public sealed class KubeApiClientFactory(IClock clock, ILogger<KubeApiClientFactory>? logger = null)
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
                return Result<IKubeApiClient>.Failure(
                    ErrorCode.InternalError,
                    "The AgentInitiated connection kind is not implemented. docs/plan/09 § Cluster "
                    + "connections budgets it at 1.5 EM in M2: an agent in the tenant's cluster "
                    + "dials out to the gateway over gRPC and the platform sends requests down that "
                    + "channel, with the tunnel identity bound to the cluster resource id at the "
                    + "gateway so that a compromised agent cannot act as another tenant. That "
                    + "authorization work — not the tunnel — is the expensive half. Until it exists, "
                    + "a cluster behind NAT with no inbound path cannot be connected; use "
                    + "Kubeconfig or ServiceAccountToken against a reachable endpoint."
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
