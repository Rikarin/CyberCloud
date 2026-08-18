// ⚠ For `Result<string>`. `CyberCloud.Core.Resources` is global in this assembly and
// `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins over the
// `Orleans.ErrorCode` this import would otherwise put back in play.
using CyberCloud.Core;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.ContainerRegistry;

/// <summary>
///     Serves <c>POST …/registries/{name}/listCredentials</c>: the endpoint, the portal URL, and the
///     administrator credential a <c>docker login</c> takes.
/// </summary>
/// <remarks>
///     <para>
///         <b>The second action handler in the tree, and the first that is not a key pair.</b>
///         <c>StorageAccountListKeysHandler</c> hands back an S3 access-key pair; this hands back a
///         <i>username and a password</i>, because that is what the tool at the other end asks for.
///     </para>
///     <para>
///         ⚠ <b>IT READS AND DOES NOT MINT.</b> The credentials are created by
///         <see cref="ContainerRegistryReconciler" /> at create time, once, under
///         <c>ISecretWriter</c>'s mint-once rule. An action that minted on demand would give the first
///         caller a password and the second caller a different one — and only one of them would be the
///         one Harbor's database holds. ⚠ On this engine that is worse than on any other in the
///         catalogue: <c>src/core/main.go</c> applies <c>HARBOR_ADMIN_PASSWORD</c> <b>only when the
///         stored salt is empty</b>, so a second mint would not even take effect. The platform would
///         hand out a password Harbor never accepted and nothing would report an error.
///     </para>
///     <para>
///         ⚠ <b>ONE FIELD OUT OF SIX.</b> The vault document behind a registry holds the
///         administrator's password, core's secret, its CSRF key, the job service's secret, the
///         registry's HTTP secret and the database password. Exactly one of those is a credential a
///         <i>caller</i> has any use for; the other five are how Harbor's components authenticate each
///         other, and returning them would put five values the tenant can do nothing with into an
///         audited response.
///     </para>
///     <para>
///         ⚠ <b>The value goes into the returned JSON and nowhere else.</b> No log line, no operation
///         record, no cache — see <c>ResourceManagerService.ActionAsync</c> for why not starting an
///         operation is what guarantees that rather than remembers it.
///     </para>
///     <para>
///         ⚠ <b>A vault that is unwired refuses here, legibly, and that is the intended answer.</b>
///         <c>UnavailableSecretResolver</c>'s message names the missing
///         <c>AddOpenBaoSecretResolver</c> call and the two configuration keys. A handler that returned
///         an empty <c>password</c> instead would publish a credential that authenticates nothing,
///         against a schema that says the field is required — and, on this type, one that a reader
///         could easily mistake for "the registry has no password".
///     </para>
/// </remarks>
public sealed class ContainerRegistryListCredentialsHandler : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => ContainerRegistries.Type;

    /// <inheritdoc />
    public string Action => ContainerRegistries.ListCredentialsAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(
        ActionContext context,
        CancellationToken cancellationToken = default
    ) {
        var password = await context.Secrets.ResolveAsync(
            ContainerRegistries.AdminPasswordRef(context.Id),
            cancellationToken
        );

        if (password.TryGetError(out var error)) {
            return Result<string>.Failure(error);
        }

        // ⚠ The property names are the response schema's pointers with the slash removed, and the
        // dispatcher checks that rather than trusting it — ContainerRegistries.ListCredentialsResponse
        // is what the OpenAPI document, the SDK and the portal form are generated from.
        return Result<string>.Success(
            new JsonObject {
                ["endpoint"] = ContainerRegistries.Endpoint(context.Namespace, context.Id.Name),
                ["portalUrl"] = ContainerRegistries.PortalUrl(context.Namespace, context.Id.Name),
                ["username"] = ContainerRegistries.AdminUsername,
                ["password"] = password.GetValueOrThrow()
            }.ToJsonString()
        );
    }
}
