// ⚠ For `Result<string>`. `CyberCloud.Core.Resources` is global in this assembly and
// `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins over the
// `Orleans.ErrorCode` this import would otherwise put back in play.
using CyberCloud.Core;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Messaging;

/// <summary>
///     Serves <c>POST …/rabbitmqClusters/{name}/listKeys</c>: the AMQP URL, the management URL, and
///     the broker's generated default user.
/// </summary>
/// <remarks>
///     <para>
///         <b>The first handler in the tree whose credential comes from a cluster rather than from
///         the vault, and the distinction is who minted it.</b>
///         <c>CyberCloud.Storage/accounts</c>, <c>CyberCloud.Monitor/workspaces</c> and
///         <c>CyberCloud.ContainerRegistry/registries</c> mint their own through
///         <c>ISecretWriter</c> and read them back through <c>ISecretResolver</c>, because the
///         platform is what decides those credentials. Here the RabbitMQ cluster-operator decides:
///         it writes <c>{name}-default-user</c> before the cluster reports ready, and no value the
///         platform minted would be the one the broker accepts.
///     </para>
///     <para>
///         ⚠ <b>IT READS AND DOES NOT MINT — for a stronger reason than the vault-backed three
///         have.</b> Their argument is that a second mint would hand out a credential only one
///         holder of which is live. Here the platform could not mint a working credential at all:
///         the password is already in the broker's internal database by the time this action can be
///         called, and writing a different one into the <c>Secret</c> would leave the two
///         disagreeing with the broker's copy winning. That is also why <c>regenerateKeys</c> is not
///         declared — rotation is an operation against the broker, not a write to a <c>Secret</c>.
///     </para>
///     <para>
///         ⚠ <b>A cluster that has not converged yet answers <c>ResourceNotFound</c> from the read,
///         and that is the honest answer rather than a hole to paper over.</b> The operator creates
///         the <c>Secret</c> as part of bringing the cluster up, so its absence means the credential
///         does not exist yet — not that this handler looked in the wrong place. Returning an empty
///         password against a schema saying the field is required would publish a credential that
///         authenticates nothing.
///     </para>
///     <para>
///         ⚠ <b>Four of the secret's seven keys are deliberately not returned.</b> The operator
///         writes <c>username</c>, <c>password</c>, <c>default_user.conf</c>, <c>provider</c>,
///         <c>type</c>, <c>host</c> and <c>port</c>. The last two are in-cluster service coordinates
///         this platform already computes from the resource's own address — and computing them is
///         better than echoing them, because <c>ClientUrl</c> and <c>ManagementUrl</c> are what every
///         other surface shows. The other three are the operator's own bootstrap material.
///     </para>
///     <para>
///         ⚠ <b>The value goes into the returned JSON and nowhere else.</b> No log line, no operation
///         record, no cache — see <c>ResourceManagerService.ActionAsync</c> for why not starting an
///         operation is what guarantees that rather than remembers it.
///     </para>
/// </remarks>
public sealed class RabbitmqClusterListKeysHandler : IResourceActionHandler {
    /// <summary>The key the cluster-operator files the user under.</summary>
    /// <remarks>
    ///     <c>internal/resource/default_user_secret.go</c>. Recorded verbatim in
    ///     <c>charts/managed/rabbitmq/SOURCE</c>, which is where this platform writes down what an
    ///     upstream operator does rather than inferring it.
    /// </remarks>
    public const string UsernameKey = "username";

    /// <inheritdoc cref="UsernameKey" />
    public const string PasswordKey = "password";

    /// <inheritdoc />
    public ResourceTypeName Type => RabbitmqClusters.Type;

    /// <inheritdoc />
    public string Action => RabbitmqClusters.ListKeysAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(
        ActionContext context,
        CancellationToken cancellationToken = default
    ) {
        // ⚠ Not null: the type declares RequiresCluster, and ActionDispatcher refuses before a
        // handler runs when a required connection is missing — the same rule ReconcileDriver applies
        // to a pass.
        var read = await context.Cluster!.GetAsync(
            KubeSecret.Ref(
                context.Namespace,
                RabbitmqClusters.DefaultUserSecretName(context.Id.Name)
            ),
            cancellationToken
        );

        if (read.TryGetError(out var readError)) {
            return Result<string>.Failure(readError);
        }

        var secret = read.GetValueOrThrow();

        var user = KubeSecret.Value(secret, UsernameKey);
        if (user.TryGetError(out var userError)) {
            return Result<string>.Failure(userError);
        }

        var password = KubeSecret.Value(secret, PasswordKey);
        if (password.TryGetError(out var passwordError)) {
            return Result<string>.Failure(passwordError);
        }

        // ⚠ The property names are the response schema's pointers with the slash removed, and the
        // dispatcher checks that rather than trusting it — RabbitmqClusters.ListKeysResponse is what
        // the OpenAPI document, the SDK and the portal form are generated from.
        return Result<string>.Success(
            new JsonObject {
                ["url"] = RabbitmqClusters.ClientUrl(context.Namespace, context.Id.Name),
                ["managementUrl"] = RabbitmqClusters.ManagementUrl(context.Namespace, context.Id.Name),
                ["user"] = user.GetValueOrThrow(),
                ["password"] = password.GetValueOrThrow()
            }.ToJsonString()
        );
    }
}
