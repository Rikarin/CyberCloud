using CyberCloud.Core.Time;
using CyberCloud.Kubernetes.Contracts.Tunnel;
using System.Globalization;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.ContainerService;

/// <summary>
///     Converges a connected cluster onto its agent's first heartbeat, and registers the agent
///     tunnel as the cluster's connection when it arrives.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             THE ONLY RECONCILER IN THE TREE THAT APPLIES NOTHING, AND THE ONLY ONE WHOSE
///             <c>Converged</c> WAITS ON THE TENANT.
///         </b> Every other family renders objects into a cluster the platform reaches. This one
///         has no cluster to reach until the tenant runs the install command, and the resource is
///         a promise the agent keeps — <see cref="ConnectedClusters" /> says why. So the pass reads
///         <see cref="IAgentTunnels.GetStatusAsync" /> and reports one of three things: nobody has
///         asked for an install command, the agent has not connected yet, or it has.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop, and where each is satisfied:
///     </para>
///     <list type="number">
///         <item><b>Idempotent.</b> A status read and a report. Nothing is written by a pass.</item>
///         <item><b>No hidden state.</b> The only field is the constructor's <see cref="IClock" />.</item>
///         <item><b>Bounded.</b> One grain read, on the caller's token.</item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             heartbeat the grain recorded, never the action having been called.
///         </item>
///     </list>
///     <para>
///         ⚠
///         <b>
///             The connection is reported on the converging pass and attached by the driver, the
///             way a managed cluster's is.
///         </b> <c>ReconcileContext.ClusterConnections</c> carries an
///         <c>AgentInitiated</c> descriptor with no credential reference — the credential is the
///         agent's, hashed in the tunnel grain — and <c>ReconcileDriver</c> stamps the cluster id
///         and the owning tenant and attaches after <c>Converged</c>. From then on the cluster's id
///         is a <c>clusterId</c> other resources can be placed in.
///     </para>
///     <para>
///         ⚠ <b>A later disconnection is not a failed resource.</b> Once the first heartbeat has
///         arrived the resource stays <c>Succeeded</c>: what changes is the connection's health,
///         which docs/plan/09 § Cluster connections makes <c>Degraded</c> — "cannot reach your
///         cluster" — and which <see cref="ObserveAsync" /> reports as drift.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class ConnectedClusterReconciler(IClock clock) : IResourceReconciler {
    /// <summary>How long a pass waits before asking again while no install command has been issued.</summary>
    public static TimeSpan WaitingForInstall { get; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a pass waits before asking again while the agent has not connected.</summary>
    public static TimeSpan WaitingForAgent { get; } = TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    public ResourceTypeName Type => ConnectedClusters.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        var status = await context.Agents.GetStatusAsync(context.Id.Id, cancellationToken);

        if (status.TryGetError(out var error)) {
            return ReconcileOutcome.FromFailure(error);
        }

        var agent = status.GetValueOrThrow();
        var name = context.Id.Name;

        if (!agent.Armed || agent.Revoked) {
            // ⚠ InProgress and not Failed: a tenant who created the resource and has not yet asked for
            // the install command is a tenant mid-way through the documented flow, not a broken one.
            context.Log.Report(
                "waiting-for-install-command",
                $"'{name}' has no install command yet. POST …/{ConnectedClusters.ListInstallCommandAction} "
                + "on it, run the command in the cluster, and the agent will connect.",
                10
            );

            return ReconcileOutcome.InProgress(
                $"no install command has been issued for '{name}'",
                WaitingForInstall
            );
        }

        if (!agent.HasHeartbeated) {
            context.Log.Report(
                "waiting-for-agent",
                agent.EnrollmentOpen
                    ? $"the install command for '{name}' has been issued and the agent has not connected yet"
                    : $"the install token for '{name}' has expired or been spent without a heartbeat; "
                    + $"POST …/{ConnectedClusters.ListInstallCommandAction} for a fresh one",
                40
            );

            return ReconcileOutcome.InProgress(
                $"the agent for '{name}' has not sent its first heartbeat",
                WaitingForAgent
            );
        }

        // ⚠ ClusterId and OwningTenantId are deliberately left unset: ReconcileDriver stamps both
        // from the resource and its operation, so a provider cannot register a cluster under a
        // tenant that does not own it. CredentialRef is empty on purpose — there is no credential
        // the platform holds for this kind; see ClusterConnectionKind.AgentInitiated.
        context.ClusterConnections.Produced(
            new() {
                Kind = ClusterConnectionKind.AgentInitiated,
                Endpoint = "agent://" + context.Id.Id.ToString("D", CultureInfo.InvariantCulture),
                DisplayName = name
            }
        );

        context.Log.Report(
            "connected",
            $"the agent for '{name}' ({agent.AgentVersion}) first reported at {agent.FirstHeartbeatAt:O}"
            + (agent.Connected ? " and is connected" : " and is not connected right now"),
            100
        );

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        context.Log.Report("revoking", $"revoking the agent of '{context.Id.Name}'");

        // ⚠ Revoked, and the agent is told. Its credential stops working, its socket is closed, and
        // an agent still running in the cluster will fail to reconnect until the tenant uninstalls
        // it — which is the tenant's cluster and the tenant's act. Nothing here reaches into it.
        var revoked = await context.Agents.RevokeAsync(context.Id.Id, cancellationToken);

        if (revoked.TryGetError(out var error)) {
            return ReconcileOutcome.FromFailure(error);
        }

        context.Log.Report(
            "revoked",
            $"the agent of '{context.Id.Name}' is revoked; uninstall the chart in the cluster to stop it retrying",
            100
        );

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) {
        var status = await context.Agents.GetStatusAsync(context.Id.Id, cancellationToken);

        if (status.TryGetError(out _)) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the agent tunnel could not be read" };
        }

        var agent = status.GetValueOrThrow();

        if (!agent.HasHeartbeated) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the agent has not connected yet" };
        }

        // ⚠ THE SUMMARY IS WHERE "cannot reach your cluster" LIVES FOR THIS TYPE. The connection
        // grain's health says the same thing to reconciles placed IN the cluster; this is what the
        // cluster's own row shows.
        return new() {
            Exists = true,
            Json = new JsonObject {
                ["agentVersion"] = agent.AgentVersion,
                ["kubernetesVersion"] = agent.KubernetesVersion,
                ["connected"] = agent.Connected,
                ["firstHeartbeatAt"] = agent.FirstHeartbeatAt.ToString("O", CultureInfo.InvariantCulture),
                ["lastHeartbeatAt"] = agent.LastHeartbeatAt.ToString("O", CultureInfo.InvariantCulture)
            }.ToJsonString(),
            ObservedAt = clock.UtcNow,
            Revision = agent.LastHeartbeatAt.ToString("O", CultureInfo.InvariantCulture),
            Summary = agent.Connected
                ? $"the agent ({agent.AgentVersion}) is connected; Kubernetes {agent.KubernetesVersion}"
                : $"cannot reach your cluster — the agent last reported at {agent.LastHeartbeatAt:O}"
        };
    }
}
