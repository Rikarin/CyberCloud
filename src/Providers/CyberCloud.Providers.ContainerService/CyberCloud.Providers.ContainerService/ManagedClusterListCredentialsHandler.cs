// ⚠ For `Result<string>`. `CyberCloud.Core.Resources` is global in this assembly and
// `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins over the
// `Orleans.ErrorCode` this import would otherwise put back in play.
using CyberCloud.Core;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.ContainerService;

/// <summary>
///     Serves <c>POST …/managedClusters/{name}/listCredentials</c>: a kubeconfig for the cluster, the
///     address it points at, and when it stops working.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>WHAT LEAVES HERE IS <c>cluster-admin</c> ON A WHOLE CLUSTER, AND THAT IS WHAT THE
///         DECLARATION ALREADY PROMISED RATHER THAN SOMETHING THIS HANDLER DECIDED.</b>
///         <c>ManagedClusters.ListCredentialsResponse</c> has said so since it was written:
///         docs/plan/13 wants this short-lived and scoped, what Cluster API generates is the admin
///         credential, and narrowing it needs a certificate request against a cluster this platform
///         cannot yet reach. Serving what the document publishes is not a widening — the alternative
///         was publishing it and answering <c>500</c>, which protects nobody and tells the caller
///         nothing. The narrowing stays owed;
///         <c>charts/managed/kubernetes/conformance.yaml § owed</c> is where it is recorded.
///     </para>
///     <para>
///         ⚠ <b>IT READS AND DOES NOT MINT — and unlike the database providers, it could not mint
///         even with somewhere to put the value.</b> Minting a kubeconfig means issuing a client
///         certificate the workload cluster's own CA signs, which is a request against that cluster;
///         the platform has no connection to it, which is the same gap that stops the connection
///         descriptor from resolving.
///     </para>
///     <para>
///         ⚠ <b>Two reads, because the two facts live in different objects.</b> The credential is in
///         Cluster API's <c>{name}-kubeconfig</c> <c>Secret</c>; the API server address is on the
///         <c>Cluster</c>, where the Kamaji control-plane provider patches
///         <c>spec.controlPlaneEndpoint</c>. Parsing the address back out of the kubeconfig would be
///         a YAML parse this tree has no library for, and the point of returning it separately is
///         that a caller should not have to do one either.
///     </para>
///     <para>
///         ⚠ <b>An endpoint the <c>Cluster</c> does not carry yet is a refusal, not an empty
///         string.</b> <see cref="ManagedClusters.ApiServerEndpoint" /> answers empty while no
///         controller has assigned one, and the response schema declares <c>/apiServerEndpoint</c>
///         required and a URI. A kubeconfig handed out beside an empty address is a credential for a
///         cluster the caller cannot find — worth a message saying the control plane is not ready.
///     </para>
///     <para>
///         ⚠ <b>The same applies to the expiry, and more strongly.</b> A wrong <c>/expiresAt</c> is
///         worse than a refusal: it is the date somebody schedules a rotation against. So a
///         kubeconfig whose credential this platform cannot read an expiry out of is refused rather
///         than answered with a guess — see <see cref="ManagedClusters.CredentialExpiry" /> for the
///         one shape it can read and what it costs.
///     </para>
///     <para>
///         ⚠ <b>The kubeconfig goes into the returned JSON and nowhere else.</b> No log line, no
///         operation record, no cache — see <c>ResourceManagerService.ActionAsync</c> for why not
///         starting an operation is what guarantees that rather than remembers it. On this type that
///         matters more than on any other in the catalogue: an LRO status is durable and readable by
///         anyone holding <c>read</c>, and this credential is not a database password but the whole
///         cluster.
///     </para>
/// </remarks>
public sealed class ManagedClusterListCredentialsHandler : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => ManagedClusters.Type;

    /// <inheritdoc />
    public string Action => ManagedClusters.ListCredentialsAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(
        ActionContext context,
        CancellationToken cancellationToken = default
    ) {
        // ⚠ Not null: the type declares RequiresCluster, and ActionDispatcher refuses before a
        // handler runs when a required connection is missing.
        var cluster = context.Cluster!;

        var secretRead = await cluster.GetAsync(
            KubeSecret.Ref(
                context.Namespace,
                ManagedClusters.KubeconfigSecretName(context.Id.Name)
            ),
            cancellationToken
        );

        if (secretRead.TryGetError(out var secretError)) {
            return Result<string>.Failure(secretError);
        }

        var kubeconfig = KubeSecret.Value(secretRead.GetValueOrThrow(), ManagedClusters.KubeconfigKey);
        if (kubeconfig.TryGetError(out var kubeconfigError)) {
            return Result<string>.Failure(kubeconfigError);
        }

        var clusterRead = await cluster.GetAsync(
            ManagedClusters.ClusterRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        if (clusterRead.TryGetError(out var clusterError)) {
            return Result<string>.Failure(clusterError);
        }

        var endpoint = ManagedClusters.ApiServerEndpoint(clusterRead.GetValueOrThrow().Json);

        if (endpoint.Length == 0) {
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has a kubeconfig and its Cluster carries no "
                + "spec.controlPlaneEndpoint, so there is no address to hand out with it. The control "
                + "plane has not been assigned one yet — retry once the resource reports Succeeded."
            );
        }

        if (ManagedClusters.CredentialExpiry(kubeconfig.GetValueOrThrow()) is not { } expiry) {
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has a kubeconfig this platform cannot read an expiry out of, so "
                + "it is not handed over. The response declares when the credential stops working and "
                + "a guessed date is the one a caller would schedule a rotation against — a kubeconfig "
                + "authenticating with a token or an exec plugin rather than a client certificate is "
                + "the shape that lands here."
            );
        }

        // ⚠ The property names are the response schema's pointers with the slash removed, and the
        // dispatcher checks that rather than trusting it — ManagedClusters.ListCredentialsResponse is
        // what the OpenAPI document, the SDK and the portal form are generated from.
        return Result<string>.Success(
            new JsonObject {
                ["kubeconfig"] = kubeconfig.GetValueOrThrow(),
                ["apiServerEndpoint"] = endpoint,
                ["expiresAt"] = expiry.ToString("O")
            }.ToJsonString()
        );
    }
}
