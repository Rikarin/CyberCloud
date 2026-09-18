// ⚠ For `Result<string>`. `CyberCloud.Core.Resources` is global in this assembly and
// `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins over the
// `Orleans.ErrorCode` this import would otherwise put back in play.

using CyberCloud.Core;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.ContainerService;

/// <summary>
///     Serves <c>POST …/connectedClusters/{name}/listInstallCommand</c>: mints a one-time enrollment
///     token, arms the cluster's tunnel with its hash, and answers with the helm command that
///     carries it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>WHAT LEAVES HERE ADMITS ONE AGENT TO THIS CLUSTER, AND IT LEAVES EXACTLY ONCE.</b>
///         The token goes into the returned JSON and nowhere else — no log line, no operation
///         record, no grain state (the grain holds a hash). <c>ResourceManagerService.ActionAsync</c>
///         serves a non-long-running action inline, which is what guarantees no record rather than
///         remembers not to write one. The action is declared <c>secret: true</c> and every member of
///         the response that carries the token is a <c>Secret</c> property.
///     </para>
///     <para>
///         ⚠ <b>Each call voids the previous token.</b> <c>IAgentTunnelGrain.ArmAsync</c> replaces the
///         enrollment hash, so an install command that was pasted somewhere it should not have been
///         is dead the moment a new one is asked for. A connected agent's own credential is not
///         touched: re-issuing an install command is not a revocation.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The owning tenant is <c>context.Id.TenantId</c> — a manager fact, never a body
///             field.
///         </b> It is what the grain records on the first arm and checks on every later
///         one, so it has to come from the address the pipeline already established rather than
///         from anything the caller sent.
///     </para>
/// </remarks>
public sealed class ConnectedClusterInstallCommandHandler : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => ConnectedClusters.Type;

    /// <inheritdoc />
    public string Action => ConnectedClusters.ListInstallCommandAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(
        ActionContext context,
        CancellationToken cancellationToken = default
    ) {
        // ⚠ The body's heartbeatSeconds goes to the grain here and comes back in the enrollment for
        // the chart, so the welcome the agent adopts and the value the chart starts with are the
        // same number. Read once, sent once.
        var enrolled = await context.Agents.EnrollAsync(
            context.Id.Id,
            context.Id.TenantId,
            TimeSpan.FromSeconds(ConnectedClusters.HeartbeatSeconds(context.Desired)),
            cancellationToken
        );

        if (enrolled.TryGetError(out var error)) {
            return Result<string>.Failure(error);
        }

        var enrollment = enrolled.GetValueOrThrow();

        // ⚠ The property names are the response schema's pointers with the slash removed, and the
        // dispatcher checks that rather than trusting it — ConnectedClusters.ListInstallCommandResponse
        // is what the OpenAPI document, the SDK and the portal form are generated from.
        return Result<string>.Success(
            new JsonObject {
                ["command"] = ConnectedClusters.InstallCommand(enrollment),
                ["token"] = enrollment.EnrollmentToken,
                ["expiresAt"] = enrollment.ExpiresAt.ToString("O"),
                ["tunnelEndpoint"] = enrollment.TunnelEndpoint,
                ["chart"] = enrollment.ChartReference
            }.ToJsonString()
        );
    }
}
