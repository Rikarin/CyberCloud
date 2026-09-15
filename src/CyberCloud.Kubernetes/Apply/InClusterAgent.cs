using CyberCloud.Core.Time;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;

namespace CyberCloud.Kubernetes.Apply;

/// <summary>Where an agent keeps the long-lived credential it was handed on enrollment.</summary>
/// <remarks>
///     ⚠ <b>Kept in the cluster, not in the pod.</b> A pod restarts; a credential held only in memory
///     would make every restart a fresh enrollment, and the enrollment token is spent after one. The
///     store is a <c>Secret</c> in the agent's own namespace, which is why <c>charts/agent</c> grants
///     the agent a <i>namespaced</i> <c>Role</c> over that one Secret and nothing else in the
///     namespace.
/// </remarks>
public interface IAgentCredentialStore {
    /// <summary>The stored credential, or <see langword="null" /> when none has been stored.</summary>
    /// <param name="cancellationToken">The caller's budget.</param>
    Task<string?> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores the credential, replacing any previous one.</summary>
    /// <param name="credential">The plaintext the platform handed over in its welcome.</param>
    /// <param name="cancellationToken">The caller's budget.</param>
    Task WriteAsync(string credential, CancellationToken cancellationToken = default);
}

/// <summary>
///     What the agent host needs from inside a pod, built where <c>k8s.*</c> is allowed to be named.
/// </summary>
/// <remarks>
///     ⚠ <b>In <c>CyberCloud.Kubernetes.Apply</c> and not beside the tunnel, because
///     <c>AssemblyGraphTests.OnlyTheApplyLayerNamesKubernetesTypes</c> confines every <c>k8s.*</c>
///     signature to this namespace.</b> The tunnel namespace stays free of them, which is what
///     lets <c>TunnelAgent</c> be the same bytes in a test and in the pod.
///     <para>
///     ⚠ <b>The agent host binds none of this assembly's Kubernetes types, on purpose.</b>
///     docs/plan/03 § Assembly graph rules, rule 3 forbids every shipping assembly outside this
///     family — a host included — from binding <c>k8s.Models</c>, and the Architecture gate reads
///     the AssemblyRef table to check. So the in-cluster configuration, the API client and the
///     Secret store are all built here, and the host sees an <see cref="IKubeApiClient" /> and an
///     <see cref="IAgentCredentialStore" />.
///     </para>
/// </remarks>
public static class InClusterAgent {
    /// <summary>The key inside the credential Secret.</summary>
    public const string CredentialKey = "credential";

    /// <summary>
    ///     An <see cref="IKubeApiClient" /> over the pod's own service account — the client the agent
    ///     serves tunnel requests with.
    /// </summary>
    /// <param name="clusterId">The connected-cluster resource's id, for messages.</param>
    /// <param name="clock">The clock a drift event's timestamp comes from.</param>
    /// <param name="logger">Where the API server's refusals are written in full.</param>
    /// <param name="credentialSecretName">
    ///     The Secret the credential is kept in. ⚠ Must be the name the chart's <c>Role</c> scopes
    ///     to — <c>cluster.credentialSecretName</c> in <c>charts/agent/templates/rbac.yaml</c> —
    ///     which is why the chart passes it in rather than trusting a default to agree. Empty means
    ///     <see cref="DefaultSecretName" />.
    /// </param>
    /// <returns>The client, and the namespace the pod runs in.</returns>
    public static (IKubeApiClient Api, IAgentCredentialStore Credentials, string Namespace) FromPod(
        Guid clusterId,
        IClock clock,
        ILogger? logger = null,
        string? credentialSecretName = null
    ) {
        var config = KubernetesClientConfiguration.InClusterConfig();
        var client = new k8s.Kubernetes(config);
        var ns = string.IsNullOrEmpty(config.Namespace) ? "default" : config.Namespace;

        return (
            new KubeApiClient(client, clusterId, clock, ownsClient: false, logger: logger),
            new SecretAgentCredentialStore(
                client,
                ns,
                string.IsNullOrWhiteSpace(credentialSecretName) ? DefaultSecretName : credentialSecretName
            ),
            ns
        );
    }

    /// <summary>
    ///     The Secret the chart lets the agent write when nothing says otherwise — the default of
    ///     <c>cluster.credentialSecretName</c> in <c>charts/agent/values.yaml</c>, which
    ///     <c>templates/rbac.yaml</c> scopes the agent's <c>Role</c> to.
    /// </summary>
    public const string DefaultSecretName = "cybercloud-agent-credential";

    /// <summary>An <see cref="IAgentCredentialStore" /> over one Secret.</summary>
    /// <param name="client">The API client.</param>
    /// <param name="ns">The namespace.</param>
    /// <param name="name">The Secret's name.</param>
    public static IAgentCredentialStore SecretStore(IKubernetes client, string ns, string name) =>
        new SecretAgentCredentialStore(client, ns, name);

    sealed class SecretAgentCredentialStore(IKubernetes client, string ns, string name) : IAgentCredentialStore {
        public async Task<string?> ReadAsync(CancellationToken cancellationToken = default) {
            try {
                var secret = await client.CoreV1.ReadNamespacedSecretAsync(name, ns, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                return secret.Data is { } data && data.TryGetValue(CredentialKey, out var bytes)
                    ? Encoding.UTF8.GetString(bytes)
                    : null;
            } catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound) {
                return null;
            }
        }

        public async Task WriteAsync(string credential, CancellationToken cancellationToken = default) {
            ArgumentException.ThrowIfNullOrEmpty(credential);

            var secret = new V1Secret {
                ApiVersion = "v1",
                Kind = "Secret",
                Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns },
                Type = "Opaque",
                Data = new Dictionary<string, byte[]>(StringComparer.Ordinal) {
                    [CredentialKey] = Encoding.UTF8.GetBytes(credential)
                }
            };

            try {
                await client.CoreV1.ReplaceNamespacedSecretAsync(secret, name, ns, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            } catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound) {
                await client.CoreV1.CreateNamespacedSecretAsync(secret, ns, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
