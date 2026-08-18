// ⚠ For `Result<string>`. `CyberCloud.Core.Resources` is global in this assembly and
// `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins over the
// `Orleans.ErrorCode` this import would otherwise put back in play.
using CyberCloud.Core;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.DocumentDB;

/// <summary>
///     Serves <c>POST …/accounts/{name}/listKeys</c>: the MongoDB endpoint, the database, and the
///     account the gateway authenticates.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>IT READS AND DOES NOT MINT.</b> This type is a FerretDB gateway over a CloudNativePG
///         cluster, and the credential is the PostgreSQL superuser CloudNativePG generated —
///         <c>internal/controller/cluster_create.go</c>, <c>password.Generate(64, 10, 0, false,
///         true)</c> — into <see cref="DocumentDbAccounts.SuperuserSecretName" />. Nothing this
///         platform minted would be the password the cluster accepts, so <c>regenerateKeys</c> is
///         not declared either.
///     </para>
///     <para>
///         ⚠ <b>The same two keys <c>DeploymentJson</c> projects, and that is the property worth
///         keeping.</b> The gateway's environment is built from <c>username</c> and <c>password</c>
///         of this Secret, so reading the same two is what makes the credential this action returns
///         the credential the gateway is actually using. ⚠ The Secret's own <c>uri</c> key is
///         <b>not</b> returned and must not be: <c>cluster_create.go</c> passes <c>"*"</c> as its
///         dbname, so that URI names a database that does not exist — the distinction
///         <see cref="DocumentDbAccounts.SuperuserSecretName" />'s remarks say costs a non-starting
///         pod to learn.
///     </para>
///     <para>
///         ⚠ <b><c>/endpoint</c> is the MongoDB address and not the PostgreSQL one.</b> The
///         credential comes from the Postgres cluster's Secret and the endpoint is the FerretDB
///         Service, because what a caller connects to is the gateway. Handing back the Secret's own
///         <c>host</c> key would send a MongoDB driver at a PostgreSQL port.
///     </para>
///     <para>
///         ⚠ <b>An account that has not converged yet answers <c>ResourceNotFound</c> from the read</b>,
///         which means the credential does not exist yet rather than that this looked in the wrong
///         place. Returning an empty <c>password</c> against a schema saying the field is required
///         would publish a credential that authenticates nothing.
///     </para>
///     <para>
///         ⚠ <b>The value goes into the returned JSON and nowhere else.</b> No log line, no operation
///         record, no cache — see <c>ResourceManagerService.ActionAsync</c>.
///     </para>
/// </remarks>
public sealed class DocumentDbAccountListKeysHandler : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => DocumentDbAccounts.Type;

    /// <inheritdoc />
    public string Action => DocumentDbAccounts.ListKeysAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(
        ActionContext context,
        CancellationToken cancellationToken = default
    ) {
        // ⚠ Not null: the type declares RequiresCluster, and ActionDispatcher refuses before a
        // handler runs when a required connection is missing.
        var read = await context.Cluster!.GetAsync(
            KubeSecret.Ref(
                context.Namespace,
                DocumentDbAccounts.SuperuserSecretName(context.Id.Name)
            ),
            cancellationToken
        );

        if (read.TryGetError(out var readError)) {
            return Result<string>.Failure(readError);
        }

        var secret = read.GetValueOrThrow();

        var username = KubeSecret.Value(secret, DocumentDbAccounts.UsernameKey);
        if (username.TryGetError(out var usernameError)) {
            return Result<string>.Failure(usernameError);
        }

        var password = KubeSecret.Value(secret, DocumentDbAccounts.PasswordKey);
        if (password.TryGetError(out var passwordError)) {
            return Result<string>.Failure(passwordError);
        }

        // ⚠ The property names are the response schema's pointers with the slash removed, and the
        // dispatcher checks that rather than trusting it — DocumentDbAccounts.ListKeysResponse is
        // what the OpenAPI document, the SDK and the portal form are generated from.
        return Result<string>.Success(
            new JsonObject {
                ["endpoint"] = DocumentDbAccounts.Endpoint(context.Namespace, context.Id.Name),
                ["database"] = DocumentDbAccounts.Database,
                ["username"] = username.GetValueOrThrow(),
                ["password"] = password.GetValueOrThrow()
            }.ToJsonString()
        );
    }
}
