// ⚠ For `Result<string>`. `CyberCloud.Core.Resources` is global in this assembly and
// `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins over the
// `Orleans.ErrorCode` this import would otherwise put back in play.
using CyberCloud.Core;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Terminal;

/// <summary>
///     Serves <c>POST …/consoles/{name}/connect</c> and <c>…/terminate</c> — the two calls that start
///     and stop a shell.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THIS IS THE ONLY CODE IN THE TREE THAT APPLIES A POD, AND IT IS AN ACTION HANDLER
///         RATHER THAN A RECONCILER ON PURPOSE.</b> A reconciler converges towards a desired state
///         and is re-driven by a reminder; a shell must exist exactly when somebody is at it and not
///         one minute longer. Putting the pod behind a handler makes "a running shell" a consequence
///         of a person's click rather than of the platform's schedule — which is what lets the idle
///         reclaim delete it without the platform immediately putting it back.
///     </para>
///     <para>
///         ⚠ <b>ONE HANDLER FOR TWO ACTIONS, WHICH <see cref="IResourceActionHandler.Action" />
///         SUPPORTS BY RETURNING AN EMPTY STRING.</b> That interface calls it "the ordinary shape for
///         <c>listKeys</c> beside <c>regenerateKeys</c>". Here it is stronger than a convenience:
///         connect and terminate are two halves of one object's lifecycle and both have to agree
///         about the pod's name, its identity and what "running" means. Two classes would be two
///         places to keep that agreement.
///     </para>
///     <para>
///         ⚠ <b>IT REFUSES A CONSOLE THAT HAS NOT CONVERGED, AND THE REFUSAL IS THE SECURITY
///         BOUNDARY.</b> The network policy is the last of the three objects the reconciler applies,
///         so a console mid-provision may have a home volume and an identity and no constraint.
///         Starting a shell then would give a person an unconstrained terminal holding a managed
///         identity — for a few seconds, which is long enough. So <c>connect</c> reads all three
///         back itself rather than trusting the resource's provisioning state, which is a fact the
///         manager holds and this handler cannot see.
///     </para>
///     <para>
///         ⚠ <b>WHAT IT CANNOT DO IS CHECK WHO IS ASKING.</b> <see cref="ActionContext" /> carries no
///         <c>CallerContext</c> — deliberately, because an action reads facts about a resource — so a
///         handler cannot compare the caller against
///         <see cref="CloudConsoles.PrincipalIdPointer" />. The permission check that does happen is
///         the registry's <c>connect</c> permission through ReBAC, one layer up. The gap that leaves
///         is real and is named: <b>anyone who may connect to a console gets a shell holding that
///         console's identity</b>, whether or not they are that identity.
///         <c>charts/managed/cloud-shell/conformance.yaml § owed</c>,
///         <c>connect-cannot-see-its-caller</c>.
///     </para>
///     <para>
///         ⚠ <b>AND WHAT IT RETURNS IS NOT A SESSION — IT IS THE ADDRESS OF ONE.</b> The bytes flow
///         over <c>/hubs/terminal</c> to docs/plan/19's session grain, which does not exist:
///         <c>TerminalHub.SendAsync</c> throws by name today. So a client that calls <c>connect</c>
///         gets a running pod and a hub that refuses it. That is the honest state of this row and it
///         is written here rather than discovered by whoever builds the panel.
///     </para>
/// </remarks>
public sealed class CloudConsoleSessionHandler : IResourceActionHandler {
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
    static async Task<Result<string>> ConnectAsync(
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
        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(CloudConsoles.PodKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(CloudConsoles.PodJson(name, context.Desired))
            .ApplyAsync(cancellationToken);

        if (applied.TryGetError(out var applyError)) {
            return Result<string>.Failure(applyError);
        }

        var read = await cluster.GetAsync(CloudConsoles.PodRef(context.Namespace, name), cancellationToken);

        if (read.TryGetError(out var readError)) {
            return Result<string>.Failure(readError);
        }

        var pod = Document(read.GetValueOrThrow().Json);

        // ⚠ THE SESSION ID IS THE POD'S UID AND NOT A GUID THIS HANDLER INVENTS. A handler holds no
        // state and runs once per call, so an invented id would differ between two connects to the
        // same live shell — and a client would treat the second as a new session and throw away a
        // replay buffer that was still valid. The UID is the cluster's own answer to "is this the
        // same shell", it survives a reconnect, and it CHANGES when an idle reclaim re-creates the
        // pod, which is exactly when a client's buffer has stopped meaning anything.
        var sessionId = pod?["metadata"]?["uid"]?.GetValue<string>() ?? string.Empty;

        if (sessionId.Length == 0) {
            // Reachable against a fake that echoes an apply back without a uid, and against a real
            // API server never. Refusing rather than substituting: a session id a client cannot name
            // on the hub is worse than an error it can retry.
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"the shell pod of '{context.Id.Path}' was applied and read back with no "
                + "metadata.uid, so there is no session to name on the terminal hub."
            );
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
        var deleted = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(CloudConsoles.PodKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(
                new JsonObject {
                    ["metadata"] = new JsonObject { ["name"] = CloudConsoles.ShellName(context.Id.Name) }
                }.ToJsonString()
            )
            // ⚠ Foreground, so this call does not return until the container is actually gone. A
            // terminate that answered while the shell was still printing would be a stop button that
            // does not stop anything, which on a resource holding an identity is the one control a
            // person has to be able to trust.
            .DeleteAsync(CascadePolicy.Foreground, cancellationToken);

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

    static Result<string> Answer(bool terminated) =>
        Result<string>.Success(new JsonObject { ["terminated"] = terminated }.ToJsonString());

    static JsonObject? Document(string objectJson) {
        try {
            return JsonNode.Parse(objectJson) as JsonObject;
        }
        catch (System.Text.Json.JsonException) {
            return null;
        }
    }
}
