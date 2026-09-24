// ⚠ For `Result<string>`. `CyberCloud.Core.Resources` is global in this assembly and
// `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins over the
// `Orleans.ErrorCode` this import would otherwise put back in play.

using CyberCloud.Core;
using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Terminal;

/// <summary>
///     Serves <c>POST …/consoles/{name}/connect</c> and <c>…/terminate</c> — the two calls that start
///     and stop a shell.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             THIS IS THE ONLY CODE IN THE TREE THAT APPLIES A POD, AND IT IS AN ACTION HANDLER
///             RATHER THAN A RECONCILER ON PURPOSE.
///         </b> A reconciler converges towards a desired state
///         and is re-driven by a reminder; a shell must exist exactly when somebody is at it and not
///         one minute longer. Putting the pod behind a handler makes "a running shell" a consequence
///         of a person's click rather than of the platform's schedule — which is what lets the idle
///         reclaim delete it without the platform immediately putting it back.
///     </para>
///     <para>
///         ⚠
///         <b>
///             ONE HANDLER FOR TWO ACTIONS, WHICH <see cref="IResourceActionHandler.Action" />
///             SUPPORTS BY RETURNING AN EMPTY STRING.
///         </b> That interface calls it "the ordinary shape for
///         <c>listKeys</c> beside <c>regenerateKeys</c>". Here it is stronger than a convenience:
///         connect and terminate are two halves of one object's lifecycle and both have to agree
///         about the pod's name, its identity and what "running" means. Two classes would be two
///         places to keep that agreement.
///     </para>
///     <para>
///         ⚠
///         <b>
///             IT REFUSES A CONSOLE THAT HAS NOT CONVERGED, AND THE REFUSAL IS THE SECURITY
///             BOUNDARY.
///         </b> The network policy is the last of the three objects the reconciler applies,
///         so a console mid-provision may have a home volume and an identity and no constraint.
///         Starting a shell then would give a person an unconstrained terminal holding a managed
///         identity — for a few seconds, which is long enough. So <c>connect</c> reads all three
///         back itself rather than trusting the resource's provisioning state, which is a fact the
///         manager holds and this handler cannot see.
///     </para>
///     <para>
///         ⚠ <b>IT BINDS THE SESSION TO WHOEVER ASKED, AND DECIDES NOTHING ABOUT THEM.</b> The
///         permission check is the registry's <c>connect</c> permission through ReBAC, one layer up.
///         What this handler adds is ownership: <see cref="ActionContext.Caller" /> is handed to
///         <see cref="ITerminalSessionGrain.OpenAsync" />, and from then on the session grain refuses
///         every other person — a second person with <c>connect</c> on the same console gets a
///         <c>409</c> here and a <c>404</c> on the hub. ⚠ What is still NOT checked is that the
///         caller is <see cref="CloudConsoles.PrincipalIdPointer" />: that names a managed identity,
///         not a person, and whoever owns the session holds it.
///         <c>charts/managed/cloud-shell/conformance.yaml § owed</c>,
///         <c>connect-cannot-see-its-caller</c>, records what closed and what did not.
///     </para>
///     <para>
///         ⚠ <b>AND WHAT IT RETURNS IS NOT A SESSION — IT IS THE ADDRESS OF ONE.</b> The bytes flow
///         over <c>/hubs/terminal</c> to <see cref="ITerminalSessionGrain" />, keyed by the pod's UID
///         that this handler returns as <c>sessionId</c>. The handler registers the session before it
///         answers, so the id a client holds always names a grain that knows its console, its pod and
///         its owner.
///     </para>
/// </remarks>
public sealed class CloudConsoleSessionHandler : IResourceActionHandler {
    readonly CloudShellImageOptions images;

    /// <summary>Creates the handler.</summary>
    /// <param name="images">
    ///     The deployment's shell image, or <see langword="null" /> where nothing configured one — the
    ///     placeholder digests then stand and the pod fails to pull by name.
    /// </param>
    public CloudConsoleSessionHandler(IOptions<CloudShellImageOptions>? images = null) =>
        this.images = images?.Value ?? new();

    /// <inheritdoc />
    public ResourceTypeName Type => CloudConsoles.Type;

    /// <inheritdoc />
    /// <remarks>Empty: this handler serves every action <c>TerminalProvider</c> declares.</remarks>
    public string Action => string.Empty;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(
        ActionContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a shell is a pod in a cluster. "
                + "CyberCloud.Terminal/consoles declares RequiresCluster, so the dispatcher should "
                + "have refused this invocation."
            );
        }

        return string.Equals(context.Action, CloudConsoles.TerminateAction, StringComparison.OrdinalIgnoreCase)
            ? await TerminateAsync(context, cluster, cancellationToken)
            : await ConnectAsync(context, cluster, cancellationToken);
    }

    /// <summary>Starts the shell if it is not running, and describes it either way.</summary>
    async Task<Result<string>> ConnectAsync(
        ActionContext context,
        IKubeClusterConnection cluster,
        CancellationToken cancellationToken
    ) {
        var name = context.Id.Name;

        // ── The refusal that is the boundary. See this class's remarks. ───────────────────────
        foreach (var target in CloudConsoles.Objects(context.Namespace, name)) {
            var durable = await cluster.GetAsync(target, cancellationToken);

            if (durable.TryGetError(out var durableError)) {
                return durableError.Code == ErrorCode.ResourceNotFound
                    ? Result<string>.Failure(
                        ErrorCode.PreconditionFailed,
                        $"'{context.Id.Path}' cannot be attached to yet: '{target}' does not exist. A "
                        + "shell is only started once the home volume, the service account AND the "
                        + "network policy are all in place, because a shell started without the "
                        + "policy would be an unconstrained terminal holding a managed identity."
                    )
                    : Result<string>.Failure(durableError);
            }

            if (!CloudConsoles.Matches(durable.GetValueOrThrow().Json, context.Desired)) {
                return Result<string>.Failure(
                    ErrorCode.PreconditionFailed,
                    $"'{context.Id.Path}' cannot be attached to yet: '{target}' does not carry the "
                    + "desired spec. A shell started against a stale network policy would be "
                    + "constrained by a posture the tenant has already changed."
                );
            }
        }

        // ⚠ AN APPLY RATHER THAN A CREATE, WHICH IS WHAT MAKES RECONNECT AND CONNECT THE SAME CALL.
        // The pod's name is derived from the console's, so a second browser tab applies the same
        // object and gets it back unchanged. A create would answer 409 for the ordinary case of
        // re-joining a live shell.
        //
        // ⚠ AND IT GOES THROUGH KubeCommand LIKE EVERY OTHER APPLY IN THE TREE, so the pod carries
        // ADR-013's seven labels and both annotations — including cybercloud.io/resource-type, which
        // is the label the console's OWN NetworkPolicy selects on. A pod applied by any other route
        // would be a shell no policy governs.
        var read = await ApplyShellAsync(context, cluster, cancellationToken);

        if (read.TryGetError(out var readError)) {
            return Result<string>.Failure(readError);
        }

        var pod = read.GetValueOrThrow();

        // ⚠ A FINISHED SHELL IS DELETED AND APPLIED AGAIN, BECAUSE AN APPLY CANNOT RESTART IT. With
        // restartPolicy: Never a pod whose shell exited stays Succeeded, and applying the same spec
        // over it changes nothing — so without this, a console whose person typed `exit` while the
        // session grain was not there to clean up would answer "Starting" to every connect, forever.
        if (pod?["status"]?["phase"]?.GetValue<string>() is "Succeeded" or "Failed") {
            var cleared = await DeleteShellAsync(cluster, context.Id, context.Namespace, context.ApiVersion, cancellationToken);

            if (cleared.TryGetError(out var clearError) && clearError.Code != ErrorCode.ResourceNotFound) {
                return Result<string>.Failure(clearError);
            }

            read = await ApplyShellAsync(context, cluster, cancellationToken);

            if (read.TryGetError(out var againError)) {
                return Result<string>.Failure(againError);
            }

            pod = read.GetValueOrThrow();
        }

        // ⚠ THE SESSION IS BOUND TO WHOEVER CALLED connect, AND A HANDLER WITHOUT A CALLER REFUSES.
        // The manager hands one over (ActionContext.Caller); a dispatcher composed without one leaves
        // it empty, which is a composition bug, and a session with no owner would be a shell anybody
        // holding its id could type into — the one outcome the grain exists to prevent.
        var caller = context.Caller;

        if (string.IsNullOrEmpty(caller.SubjectId)) {
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' was connected to with no caller on the action context, so the "
                + "session has nobody to belong to. The resource manager passes the request's caller "
                + "to ActionDispatcher.InvokeAsync; a dispatcher composed without it cannot open a shell."
            );
        }

        var sessionId = SessionId(pod);
        if (sessionId.Length == 0) {
            return NoUid(context);
        }

        var registered = await RegisterAsync(context, cluster, sessionId, caller, cancellationToken);

        // ⚠ A SESSION THAT HAS ENDED OVER A POD THAT HASN'T IS REPLACED, OR THE CONSOLE IS LOCKED OUT.
        // The grain answers PreconditionFailed once its session is over, and the session id is the
        // pod's UID, so while that pod stands every connect would name the same ended grain and get
        // the same refusal. A session ends and leaves its pod when the idle reclaim or the start budget
        // couldn't delete it (a cluster that stopped answering), or when the grain's activation
        // outlived the pod's replacement by another route. The pod belongs to a session that is over,
        // so it goes the way a finished one does, and the next shell has the same home directory.
        if (registered.TryGetError(out var endedError) && endedError.Code == ErrorCode.PreconditionFailed) {
            var cleared = await DeleteShellAsync(cluster, context.Id, context.Namespace, context.ApiVersion, cancellationToken);

            if (cleared.TryGetError(out var clearError) && clearError.Code != ErrorCode.ResourceNotFound) {
                return Result<string>.Failure(clearError);
            }

            read = await ApplyShellAsync(context, cluster, cancellationToken);

            if (read.TryGetError(out var againError)) {
                return Result<string>.Failure(againError);
            }

            pod = read.GetValueOrThrow();
            sessionId = SessionId(pod);

            if (sessionId.Length == 0) {
                return NoUid(context);
            }

            registered = await RegisterAsync(context, cluster, sessionId, caller, cancellationToken);
        }

        if (registered.TryGetError(out var sessionError)) {
            // ⚠ Over the tenant's cap, the pod this call just started belongs to nobody and is
            // removed — a refused connect that left a running shell behind would be the cost the cap
            // exists to stop. ONLY then: a Conflict means the pod is another person's live shell.
            if (sessionError.Code == ErrorCode.QuotaExceeded) {
                await DeleteShellAsync(cluster, context.Id, context.Namespace, context.ApiVersion, cancellationToken);
            }

            return Result<string>.Failure(sessionError);
        }

        var phase = pod?["status"]?["phase"]?.GetValue<string>();

        return Result<string>.Success(
            new JsonObject {
                [CloudConsoles.SessionIdField] = sessionId,
                ["hub"] = CloudConsoles.HubPath,
                // ⚠ TWO STATES AND NOT FIVE. A pod has Pending, Running, Succeeded, Failed and
                // Unknown; a terminal panel has "open the socket" and "open the socket and say it is
                // still coming". Passing the pod's own phase through would make the portal switch on a
                // Kubernetes vocabulary this API has never otherwise exposed.
                ["state"] = string.Equals(phase, "Running", StringComparison.Ordinal) ? "Ready" : "Starting",
                ["idleTimeoutSeconds"] = CloudConsoles.IdleTimeoutSeconds(context.Desired),
                ["maxDurationSeconds"] = CloudConsoles.MaxDurationSeconds(context.Desired),
                ["recording"] = CloudConsoles.SessionRecording(context.Desired)
            }.ToJsonString()
        );
    }

    /// <summary>Stops the shell if one is running.</summary>
    /// <remarks>
    ///     ⚠ <b>It removes the pod and nothing else</b>, which is the same thing the idle reclaim
    ///     does and is why the two can coexist. The home volume, the identity and the policy survive,
    ///     so the next <c>connect</c> is a warm start rather than a re-provision.
    /// </remarks>
    static async Task<Result<string>> TerminateAsync(
        ActionContext context,
        IKubeClusterConnection cluster,
        CancellationToken cancellationToken
    ) {
        var deleted = await DeleteShellAsync(
            cluster,
            context.Id,
            context.Namespace,
            context.ApiVersion,
            cancellationToken
        );

        if (deleted.TryGetError(out var deleteError)) {
            // ⚠ NOT-FOUND IS A SUCCESS CARRYING `false`, not a 404. The caller's goal is that no shell
            // is running, and a console that was already idle has achieved it. Answering 404 would
            // make the ordinary case — clicking "close" on a session that timed out while the tab was
            // in the background — look like a failure.
            return deleteError.Code == ErrorCode.ResourceNotFound
                ? Answer(false)
                : Result<string>.Failure(deleteError);
        }

        return Answer(true);
    }

    /// <summary>
    ///     Deletes a console's shell pod and nothing else — what <c>terminate</c>, the idle reclaim and a
    ///     finished shell all come down to.
    /// </summary>
    /// <param name="cluster">The console's cluster.</param>
    /// <param name="console">The console, for the command's tenant and resource labels.</param>
    /// <param name="ns">The console's namespace.</param>
    /// <param name="apiVersion">The api-version the command is labeled with.</param>
    /// <param name="cancellationToken">Stops the delete.</param>
    /// <returns>Success, or <see cref="ErrorCode.ResourceNotFound" /> when no shell was running.</returns>
    /// <remarks>
    ///     ⚠ Through <see cref="KubeCommand" />, which is what keeps the delete labeled for the tenant
    ///     it is made on behalf of — the spelling the session grain's idle reclaim uses too, from the
    ///     pod and api-version this handler registered with it.
    /// </remarks>
    static Task<Result> DeleteShellAsync(
        IKubeClusterConnection cluster,
        ResourceId console,
        string ns,
        string apiVersion,
        CancellationToken cancellationToken
    ) =>
        KubeCommand.For(cluster)
            .WithTenantId(console.TenantId)
            .WithResourceId(console)
            .InNamespace(ns)
            .WithKind(CloudConsoles.PodKind)
            .WithApiVersion(apiVersion)
            .ObjectJson(
                new JsonObject {
                    ["metadata"] = new JsonObject { ["name"] = CloudConsoles.ShellName(console.Name) }
                }.ToJsonString()
            )
            // ⚠ Foreground, so this call does not return until the container is actually gone. A
            // terminate that answered while the shell was still printing would be a stop button that
            // does not stop anything, which on a resource holding an identity is the one control a
            // person has to be able to trust.
            .DeleteAsync(CascadePolicy.Foreground, cancellationToken);

    /// <summary>Applies the shell pod and reads it back.</summary>
    async Task<Result<JsonObject>> ApplyShellAsync(
        ActionContext context,
        IKubeClusterConnection cluster,
        CancellationToken cancellationToken
    ) {
        var image = images.For(CloudConsoles.ImageVariant(context.Desired), CloudConsoles.Image(context.Desired));

        if (!CloudConsoles.IsPinned(image)) {
            return Result<JsonObject>.Failure(
                ErrorCode.InternalError,
                $"The shell image '{image}' is not pinned by digest, so no shell was started. Set "
                + $"{CloudShellImageOptions.SectionName} to a reference ending in @sha256:<digest> — "
                + "docs/plan/18 § Platform security: a pinned digest, never a tag."
            );
        }

        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(CloudConsoles.PodKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(CloudConsoles.PodJson(context.Id.Name, context.Desired, image))
            .ApplyAsync(cancellationToken);

        if (applied.TryGetError(out var applyError)) {
            return Result<JsonObject>.Failure(applyError);
        }

        var read = await cluster.GetAsync(CloudConsoles.PodRef(context.Namespace, context.Id.Name), cancellationToken);

        return read.TryGetError(out var readError)
            ? Result<JsonObject>.Failure(readError)
            : Document(read.GetValueOrThrow().Json) is { } pod
                ? Result<JsonObject>.Success(pod)
                : Result<JsonObject>.Failure(
                    ErrorCode.InternalError,
                    $"the shell pod of '{context.Id.Path}' read back as something that is not a JSON object."
                );
    }

    /// <summary>Registers the session for the pod just applied, bound to the caller.</summary>
    /// <remarks>
    ///     ⚠ Through the context's seam, because the session grain is the platform's: it re-asks ReBAC
    ///     and holds a cluster stream, the two things docs/plan/03 § Assembly graph rules, rule 8,
    ///     keeps out of a provider. What the provider contributes is the spec — which pod, which
    ///     container, and which permission a person needs to attach.
    /// </remarks>
    static Task<Result> RegisterAsync(
        ActionContext context,
        IKubeClusterConnection cluster,
        string sessionId,
        CallerContext caller,
        CancellationToken cancellationToken
    ) =>
        context.Terminals.OpenAsync(
            new() {
                Resource = context.Id,
                ApiVersion = context.ApiVersion,
                ClusterId = cluster.ClusterId,
                Pod = CloudConsoles.PodRef(context.Namespace, context.Id.Name),
                Container = CloudConsoles.ShellContainer,
                PodUid = sessionId,
                IdleTimeoutSeconds = CloudConsoles.IdleTimeoutSeconds(context.Desired),
                Permission = CloudConsoles.ConnectPermission,
                ReadPermission = "read"
            },
            caller,
            cancellationToken
        );

    /// <summary>The session id for a shell pod: its UID, or empty when it has none.</summary>
    /// <remarks>
    ///     ⚠ <b>The pod's UID and not a GUID this handler invents.</b> A handler holds no state and runs
    ///     once per call, so an invented id would differ between two connects to the same live shell —
    ///     and a client would treat the second as a new session and throw away a replay buffer that
    ///     was still valid. The UID is the cluster's own answer to "is this the same shell", it
    ///     survives a reconnect, and it changes when the pod is re-created, which is exactly when a
    ///     client's buffer has stopped meaning anything.
    /// </remarks>
    static string SessionId(JsonObject? pod) => pod?["metadata"]?["uid"]?.GetValue<string>() ?? string.Empty;

    /// <summary>
    ///     The refusal for a pod read back with no UID — reachable against a fake that echoes an apply
    ///     back without one, and against a real API server never.
    /// </summary>
    /// <remarks>
    ///     Refusing rather than substituting: a session id a client can't name on the hub is worse than
    ///     an error it can retry.
    /// </remarks>
    static Result<string> NoUid(ActionContext context) =>
        Result<string>.Failure(
            ErrorCode.InternalError,
            $"the shell pod of '{context.Id.Path}' was applied and read back with no "
            + "metadata.uid, so there is no session to name on the terminal hub."
        );

    static Result<string> Answer(bool terminated) =>
        Result<string>.Success(new JsonObject { ["terminated"] = terminated }.ToJsonString());

    static JsonObject? Document(string objectJson) {
        try {
            return JsonNode.Parse(objectJson) as JsonObject;
        } catch (System.Text.Json.JsonException) {
            return null;
        }
    }
}
