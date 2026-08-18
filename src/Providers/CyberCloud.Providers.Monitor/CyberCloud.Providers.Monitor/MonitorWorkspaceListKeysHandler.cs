// ⚠ For `Result<string>`. `CyberCloud.Core.Resources` is global in this assembly and
// `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins over the
// `Orleans.ErrorCode` this import would otherwise put back in play.
using CyberCloud.Core;
using System.Globalization;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor;

/// <summary>
///     Serves <c>POST …/workspaces/{name}/listKeys</c>: where to send telemetry, where to read it
///     from, and the key that authenticates the writes.
/// </summary>
/// <remarks>
///     <para>
///         <b>The second action handler in the tree, and the first whose response is mostly not
///         secret.</b> <c>CyberCloud.Storage/accounts</c>' returns a credential and two facts about
///         where to use it. This one returns six endpoints and one credential, because docs/plan/16
///         gives the workspace a <c>dataSources</c> row — <i>"read-only endpoints for the tenant's
///         own Grafana or an external one"</i> — and an endpoint the platform computes from the
///         resource's own id is an output rather than a setting. Putting it here rather than in the
///         body means the portal has no property to grey out and the write path has none to refuse.
///     </para>
///     <para>
///         ⚠ <b>IT READS AND DOES NOT MINT.</b> The key is created by
///         <see cref="MonitorWorkspaceReconciler" /> at create time, once, under
///         <c>ISecretWriter</c>'s mint-once rule. An action that minted on demand would give the
///         first caller a key and the second caller a different one — and only one of them would be
///         in the <c>Secret</c> vmauth is holding. That is also why <c>rotateKeys</c> is not
///         declared: docs/plan/16 asks for rotation <i>"with a grace period"</i>, which is two live
///         credentials at once, and nothing in the platform can hold two.
///     </para>
///     <para>
///         ⚠ <b>The whole response is a pure function of the address and the desired body.</b>
///         Nothing here reaches the cluster, which is why an action carries no
///         <c>ObservedState</c> and does not need one — the endpoints are addresses rather than
///         readings, and a workspace that has not converged yet gets endpoints that do not answer
///         rather than an error that says nothing.
///     </para>
///     <para>
///         ⚠ <b>The value goes into the returned JSON and nowhere else.</b> No log line, no
///         operation record, no cache — see <c>ResourceManagerService.ActionAsync</c> for why not
///         starting an operation is what guarantees that rather than remembers it.
///     </para>
///     <para>
///         ⚠ <b>A vault that is unwired refuses here, legibly, and that is the intended answer.</b>
///         <c>UnavailableSecretResolver</c>'s message names the missing
///         <c>AddOpenBaoSecretResolver</c> call and the two configuration keys. A handler that
///         returned an empty <c>ingestKey</c> instead would publish a credential that authenticates
///         nothing, against a schema that says the field is required.
///     </para>
/// </remarks>
public sealed class MonitorWorkspaceListKeysHandler : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => MonitorWorkspaces.Type;

    /// <inheritdoc />
    public string Action => MonitorWorkspaces.ListKeysAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(
        ActionContext context,
        CancellationToken cancellationToken = default
    ) {
        var ingestKey = await context.Secrets.ResolveAsync(
            MonitorWorkspaces.IngestKeyRef(context.Id),
            cancellationToken
        );

        if (ingestKey.TryGetError(out var error)) {
            return Result<string>.Failure(error);
        }

        // ⚠ The property names are the response schema's pointers with the slash removed, and the
        // dispatcher checks that rather than trusting it — MonitorWorkspaces.ListKeysResponse is
        // what the OpenAPI document, the SDK and the portal form are generated from.
        return Result<string>.Success(
            new JsonObject {
                ["accountId"] = MonitorWorkspaces.AccountId(context.Id)
                    .ToString(CultureInfo.InvariantCulture),
                ["database"] = MonitorWorkspaces.Database(context.Id),
                ["otlpEndpoint"] = MonitorWorkspaces.OtlpEndpoint,
                ["remoteWriteEndpoint"] = MonitorWorkspaces.RemoteWriteEndpoint(context.Id),
                ["promqlEndpoint"] = MonitorWorkspaces.PromqlEndpoint(context.Id),
                ["sqlEndpoint"] = MonitorWorkspaces.SqlEndpoint(context.Id),
                ["ingestKey"] = ingestKey.GetValueOrThrow()
            }.ToJsonString()
        );
    }
}
