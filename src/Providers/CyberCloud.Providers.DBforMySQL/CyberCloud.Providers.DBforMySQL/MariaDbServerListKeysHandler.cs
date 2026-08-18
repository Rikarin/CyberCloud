// ⚠ For `Result<string>`. `CyberCloud.Core.Resources` is global in this assembly and
// `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins over the
// `Orleans.ErrorCode` this import would otherwise put back in play.
using CyberCloud.Core;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.DBforMySQL;

/// <summary>
///     Serves <c>POST …/servers/{name}/listKeys</c>: where to connect, which database and account,
///     and that account's password.
/// </summary>
/// <remarks>
///     <para>
///         <b>This closes <c>conformance.yaml</c> § owed, <c>listkeys-has-no-handler</c>.</b> That row
///         said what the gap cost: <i>a tenant with a running MariaDB has no supported way to learn
///         its password, and the two Secrets the operator generated are readable only with cluster
///         access the tenant does not have</i>. Both halves are answered here — the action reads the
///         Secret on the tenant's behalf, under a permission that is deliberately not <c>read</c>.
///     </para>
///     <para>
///         ⚠ <b>THE APPLICATION ACCOUNT, NOT ROOT, AND THAT IS THE POINT OF THE HANDLER RATHER THAN A
///         DETAIL OF IT.</b> mariadb-operator generates both — <see cref="MariaDbServers.RootSecretName" />
///         and <see cref="MariaDbServers.PasswordSecretName" /> — and this reads only the second. A
///         credential with <c>GRANT OPTION</c> over every schema is not what an application connects
///         with, and an API that returned it would make the safe choice the harder one.
///     </para>
///     <para>
///         ⚠ <b>IT READS AND DOES NOT MINT.</b> <c>ServerJson</c> renders
///         <c>passwordSecretKeyRef</c> with <c>generate: true</c>, so the operator creates the value
///         and puts it in the database at bootstrap. A password minted here would be one the server
///         never accepted. That is also why <c>regenerateKeys</c> is not declared.
///     </para>
///     <para>
///         ⚠ <b><c>/host</c> moves with the topology and is computed rather than assumed.</b>
///         <see cref="MariaDbServers.EndpointName" /> answers with the operator's
///         <c>{name}-primary</c> Service under Galera and <c>{name}</c> otherwise — which is why
///         <c>/properties/highAvailability</c> is immutable rather than merely awkward to change.
///     </para>
///     <para>
///         ⚠ <b>A server that has not converged yet answers <c>ResourceNotFound</c> from the read.</b>
///         The operator generates the Secret while bringing the server up, so its absence means the
///         credential does not exist yet. Returning an empty <c>password</c> against a schema saying
///         the field is required would publish a credential that authenticates nothing.
///     </para>
///     <para>
///         ⚠ <b>The value goes into the returned JSON and nowhere else.</b> No log line, no operation
///         record, no cache — see <c>ResourceManagerService.ActionAsync</c>.
///     </para>
/// </remarks>
public sealed class MariaDbServerListKeysHandler : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => MariaDbServers.Type;

    /// <inheritdoc />
    public string Action => MariaDbServers.ListKeysAction;

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
                MariaDbServers.PasswordSecretName(context.Id.Name)
            ),
            cancellationToken
        );

        if (read.TryGetError(out var readError)) {
            return Result<string>.Failure(readError);
        }

        var password = KubeSecret.Value(read.GetValueOrThrow(), MariaDbServers.PasswordKey);
        if (password.TryGetError(out var passwordError)) {
            return Result<string>.Failure(passwordError);
        }

        // ⚠ The property names are the response schema's pointers with the slash removed, and the
        // dispatcher checks that rather than trusting it — MariaDbServers.ListKeysResponse is what
        // the OpenAPI document, the SDK and the portal form are generated from.
        //
        // ⚠ The account and database come from the desired body and not from the Secret, unlike
        // PostgreSQL's: mariadb-operator's generated Secret carries the password alone, and the
        // account it belongs to is the one this platform asked for in `ServerJson`.
        return Result<string>.Success(
            new JsonObject {
                ["host"] = MariaDbServers.EndpointName(
                    context.Id.Name,
                    MariaDbServers.IsHighlyAvailable(context.Desired)
                ),
                ["port"] = MariaDbServers.Port,
                ["database"] = MariaDbServers.Database(context.Desired),
                ["username"] = MariaDbServers.Username(context.Desired),
                ["password"] = password.GetValueOrThrow(),
                ["authenticationPlugin"] = MariaDbServers.AuthenticationPlugin
            }.ToJsonString()
        );
    }
}
