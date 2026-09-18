using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor;

/// <summary>
///     Serves <c>POST …/grafanas/{name}/url</c>: where the instance answers, and how to sign in.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The URL is the integration ADR-011 permits, and the only one.</b> A portal page shows
///         a Grafana panel by putting <c>{url}/d-solo/…</c> in an <c>iframe</c>; nothing of Grafana's
///         is linked into the portal to do it, and <c>GF_SECURITY_ALLOW_EMBEDDING</c> on the
///         Deployment is what lets the frame render.
///     </para>
///     <para>
///         ⚠ <b>It reads the password and does not mint it</b> — <c>MonitorWorkspaceListKeysHandler</c>'s
///         rule: the reconciler minted once, and an action that minted on demand would hand the
///         second caller a password the pod does not hold. A vault that is unwired refuses here by
///         name, which is the intended answer.
///     </para>
/// </remarks>
public sealed class GrafanaUrlHandler : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => Grafanas.Type;

    /// <inheritdoc />
    public string Action => Grafanas.UrlAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(
        ActionContext context,
        CancellationToken cancellationToken = default
    ) {
        var password = await context.Secrets.ResolveAsync(Grafanas.AdminPasswordRef(context.Id), cancellationToken);

        if (password.TryGetError(out var error)) {
            return Result<string>.Failure(error);
        }

        return Result<string>.Success(
            new JsonObject {
                ["url"] = Grafanas.Url(context.Namespace, context.Id.Name),
                ["adminUser"] = Grafanas.AdminUser,
                ["adminPassword"] = password.GetValueOrThrow(),
                ["workspace"] = Grafanas.WorkspacePath(context.Desired)
            }.ToJsonString()
        );
    }
}
