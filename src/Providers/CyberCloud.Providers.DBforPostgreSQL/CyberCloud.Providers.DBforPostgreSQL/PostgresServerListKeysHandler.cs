// ⚠ For `Result<string>`. `CyberCloud.Core.Resources` is global in this assembly and
// `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins over the
// `Orleans.ErrorCode` this import would otherwise put back in play.
using CyberCloud.Core;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.DBforPostgreSQL;

/// <summary>
///     Serves <c>POST …/servers/{name}/listKeys</c>: where to connect, which database, and the
///     owning role's password.
/// </summary>
/// <remarks>
///     <para>
///         <b>The action this provider's own contracts said it could not serve.</b>
///         <c>PostgresServers.ListKeysResponse</c> was declared with no handler behind it — the
///         remark on it says as much — so the type published a credential export in the OpenAPI
///         document, the <c>cyc</c> verb tree, the .NET SDK and the portal form, and every call
///         answered <c>500</c>. A working database whose password the tenant cannot fetch is a
///         database they cannot use.
///     </para>
///     <para>
///         ⚠ <b>IT READS AND DOES NOT MINT, and here the platform never had the value to mint.</b>
///         <c>ClusterJson</c> deliberately does not render <c>bootstrap.initdb.secret</c>, so
///         CloudNativePG generates the owner's password itself into
///         <c>{cluster}-app</c> — a <c>kubernetes.io/basic-auth</c> Secret — before the cluster
///         reports ready. Nothing this platform minted would be the password the server accepts.
///         That is also why <c>regenerateKeys</c> is not declared: rotation is an operation against
///         the cluster with a grace period, which is two live credentials at once, and nothing here
///         can hold two.
///     </para>
///     <para>
///         ⚠ <b><c>/host</c> is computed and not taken from the Secret, and the two disagree on
///         purpose.</b> CloudNativePG writes its own <c>host</c> key naming the read-write service.
///         <see cref="PostgresServers.Host" /> answers with the <i>pooler's</i> service while pooling
///         is on, which is what a client should actually connect to and what the response schema
///         says this field is. Echoing the secret's copy would hand out the address that bypasses
///         the pooler the tenant is paying for.
///     </para>
///     <para>
///         ⚠ <b>A cluster that has not converged yet answers <c>ResourceNotFound</c> from the read.</b>
///         The operator creates the Secret while bringing the cluster up, so its absence means the
///         credential does not exist yet rather than that this looked in the wrong place. Returning
///         an empty <c>password</c> against a schema saying the field is required would publish a
///         credential that authenticates nothing.
///     </para>
///     <para>
///         ⚠ <b>The value goes into the returned JSON and nowhere else.</b> No log line, no operation
///         record, no cache — see <c>ResourceManagerService.ActionAsync</c> for why not starting an
///         operation is what guarantees that rather than remembers it.
///     </para>
/// </remarks>
public sealed class PostgresServerListKeysHandler : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => PostgresServers.Type;

    /// <inheritdoc />
    public string Action => PostgresServers.ListKeysAction;

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
                PostgresServers.CredentialSecretName(context.Id.Name)
            ),
            cancellationToken
        );

        if (read.TryGetError(out var readError)) {
            return Result<string>.Failure(readError);
        }

        var secret = read.GetValueOrThrow();

        var password = KubeSecret.Value(secret, PostgresServers.PasswordKey);
        if (password.TryGetError(out var passwordError)) {
            return Result<string>.Failure(passwordError);
        }

        // ⚠ The username comes from the Secret rather than from the desired body, and it is the one
        // field where the operator's copy is the authority. `bootstrap.owner` is what this platform
        // ASKED for; the Secret is the role CloudNativePG actually created, and a body edited after
        // creation does not rename a role.
        var username = KubeSecret.Value(secret, PostgresServers.UsernameKey);
        if (username.TryGetError(out var usernameError)) {
            return Result<string>.Failure(usernameError);
        }

        // ⚠ The property names are the response schema's pointers with the slash removed, and the
        // dispatcher checks that rather than trusting it — PostgresServers.ListKeysResponse is what
        // the OpenAPI document, the SDK and the portal form are generated from.
        return Result<string>.Success(
            new JsonObject {
                ["host"] = PostgresServers.Host(context.Id.Name, context.Desired),
                ["port"] = PostgresServers.Port,
                ["database"] = PostgresServers.Database(context.Desired),
                ["username"] = username.GetValueOrThrow(),
                ["password"] = password.GetValueOrThrow()
            }.ToJsonString()
        );
    }
}
