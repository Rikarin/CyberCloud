// ⚠ For `Result<string>`. `CyberCloud.Core.Resources` is global in this assembly and
// `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins over the
// `Orleans.ErrorCode` this import would otherwise put back in play.
using CyberCloud.Core;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Search;

/// <summary>
///     Serves <c>POST …/services/{name}/listKeys</c>: the REST endpoint and the admin credential the
///     operator generated for it.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is the handler <c>OpenSearchServices</c>' own remarks said could not exist.</b>
///         Leaving <c>spec.security.config.adminCredentialsSecret</c> unset makes opensearch-operator
///         generate a random admin password — <c>pkg/helpers/helpers.go</c>,
///         <c>EnsureAdminCredentialsSecret</c> — and that decision was recorded with the sentence
///         <i>"the platform simply cannot hand the credential out"</i>. That was a statement about
///         the platform and not about the cluster: the <c>Secret</c> is in the tenant's own namespace
///         and the reconcile path could always reach it. What did not exist was anywhere to put the
///         read.
///     </para>
///     <para>
///         ⚠ <b>IT READS AND DOES NOT MINT, and here minting is not merely wrong but ineffective.</b>
///         The operator writes the credential into the security plugin's internal user database at
///         bootstrap. A password minted afterwards would live in the <c>Secret</c> and nowhere else,
///         and the cluster would keep accepting the old one — the platform would hand out something
///         that authenticates nothing while everything reported success. That is also why
///         <c>regenerateKeys</c> is not declared: rotation is a security-plugin operation, not a
///         write to a <c>Secret</c>.
///     </para>
///     <para>
///         ⚠ <b>The username is read rather than assumed.</b> It is always <c>admin</c> today, and
///         the operator writes it into the same object as the password; taking the platform's word
///         for it would be one hard-coded half of a credential whose other half is read, which is the
///         asymmetry that survives an upstream change silently.
///     </para>
///     <para>
///         ⚠ <b>The endpoint's certificate is the operator's own CA and the caller still has to trust
///         it.</b> <see cref="OpenSearchServices.Endpoint" /> is genuinely <c>https</c>, and the CA
///         bundle is <b>not</b> part of this response —
///         <c>charts/managed/opensearch/conformance.yaml § owed</c>,
///         <c>ca-bundle-is-not-handed-out</c>. Returning a credential over an endpoint whose trust
///         anchor the caller cannot obtain is the remaining half of making this service usable.
///     </para>
///     <para>
///         ⚠ <b>A service that has not converged yet answers <c>ResourceNotFound</c> from the read</b>,
///         which means the credential does not exist yet. Returning an empty <c>password</c> against
///         a schema saying the field is required would publish a credential that authenticates
///         nothing.
///     </para>
///     <para>
///         ⚠ <b>The value goes into the returned JSON and nowhere else.</b> No log line, no operation
///         record, no cache — see <c>ResourceManagerService.ActionAsync</c>.
///     </para>
/// </remarks>
public sealed class OpenSearchServiceListKeysHandler : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => OpenSearchServices.Type;

    /// <inheritdoc />
    public string Action => OpenSearchServices.ListKeysAction;

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
                OpenSearchServices.AdminCredentialsSecretName(context.Id.Name)
            ),
            cancellationToken
        );

        if (read.TryGetError(out var readError)) {
            return Result<string>.Failure(readError);
        }

        var secret = read.GetValueOrThrow();

        var username = KubeSecret.Value(secret, OpenSearchServices.UsernameKey);
        if (username.TryGetError(out var usernameError)) {
            return Result<string>.Failure(usernameError);
        }

        var password = KubeSecret.Value(secret, OpenSearchServices.PasswordKey);
        if (password.TryGetError(out var passwordError)) {
            return Result<string>.Failure(passwordError);
        }

        // ⚠ The property names are the response schema's pointers with the slash removed, and the
        // dispatcher checks that rather than trusting it — OpenSearchServices.ListKeysResponse is
        // what the OpenAPI document, the SDK and the portal form are generated from.
        return Result<string>.Success(
            new JsonObject {
                ["endpoint"] = OpenSearchServices.Endpoint(context.Namespace, context.Id.Name),
                ["username"] = username.GetValueOrThrow(),
                ["password"] = password.GetValueOrThrow()
            }.ToJsonString()
        );
    }
}
