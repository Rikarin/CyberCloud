// ⚠ For `Result<string>`. `CyberCloud.Core.Resources` is global in this assembly and
// `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins over the
// `Orleans.ErrorCode` this import would otherwise put back in play — the same note StorageProvider
// carries.
using CyberCloud.Core;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Storage;

/// <summary>
///     Serves <c>POST …/accounts/{name}/listKeys</c>: the S3 endpoint, the region, and the key pair
///     the account's gateway authenticates.
/// </summary>
/// <remarks>
///     <para>
///         <b>The first action handler in the tree, and the reason it is this one.</b> Twelve actions
///         across nine provider namespaces were declared and none could run. On most of them the gap
///         is an inconvenience — <c>CyberCloud.DBforPostgreSQL/servers</c> has a working database
///         whose password the tenant cannot fetch, because CloudNativePG generates its own. Here the
///         credential the action hands out is the <i>only</i> access control the data plane has, so
///         the action and the service arrived together.
///     </para>
///     <para>
///         ⚠ <b>IT READS AND DOES NOT MINT.</b> The pair is created by
///         <see cref="StorageAccountReconciler" /> at create time, once, under
///         <c>ISecretWriter</c>'s mint-once rule. An action that minted on demand would give the
///         first caller a credential and the second caller a different one — and only one of them
///         would be in the file the gateway is holding. That is also why <c>regenerateKeys</c> is
///         not declared: rotation needs two live credentials at once, which nothing in the platform
///         can hold yet.
///     </para>
///     <para>
///         ⚠ <b>The value goes into the returned JSON and nowhere else.</b> No log line, no operation
///         record, no cache — see <c>ResourceManagerService.ActionAsync</c> for why not starting an
///         operation is what guarantees that rather than remembers it, and
///         <c>ActionDispatchTests</c> for the assertion.
///     </para>
///     <para>
///         ⚠ <b>A vault that is unwired refuses here, legibly, and that is the intended answer.</b>
///         <c>UnavailableSecretResolver</c>'s message names the missing
///         <c>AddOpenBaoSecretResolver</c> call and the two configuration keys. A handler that
///         returned an empty <c>secretAccessKey</c> instead would publish a credential that
///         authenticates nothing, against a schema that says the field is required.
///     </para>
/// </remarks>
public sealed class StorageAccountListKeysHandler : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => StorageAccounts.Type;

    /// <inheritdoc />
    public string Action => StorageAccounts.ListKeysAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(
        ActionContext context,
        CancellationToken cancellationToken = default
    ) {
        var accessKeyId = await context.Secrets.ResolveAsync(
            StorageAccounts.AccessKeyIdRef(context.Id),
            cancellationToken
        );

        if (accessKeyId.TryGetError(out var accessKeyError)) {
            return Result<string>.Failure(accessKeyError);
        }

        var secretAccessKey = await context.Secrets.ResolveAsync(
            StorageAccounts.SecretAccessKeyRef(context.Id),
            cancellationToken
        );

        if (secretAccessKey.TryGetError(out var secretKeyError)) {
            return Result<string>.Failure(secretKeyError);
        }

        // ⚠ The property names are the response schema's pointers with the slash removed, and the
        // dispatcher checks that rather than trusting it — StorageAccounts.ListKeysResponse is what
        // the OpenAPI document, the SDK and the portal form are generated from.
        return Result<string>.Success(
            new JsonObject {
                ["endpoint"] = StorageAccounts.Endpoint(context.Namespace, context.Id.Name),
                ["region"] = StorageAccounts.Region(context.Desired),
                ["accessKeyId"] = accessKeyId.GetValueOrThrow(),
                ["secretAccessKey"] = secretAccessKey.GetValueOrThrow()
            }.ToJsonString()
        );
    }
}
