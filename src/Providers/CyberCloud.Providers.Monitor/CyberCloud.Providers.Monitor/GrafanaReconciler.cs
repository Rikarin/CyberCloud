using CyberCloud.Core.Time;

namespace CyberCloud.Providers.Monitor;

/// <summary>
///     Converges one <c>CyberCloud.Dashboard/grafanas</c> resource onto the four objects it is: an
///     admin credential, a datasource provisioning file, Grafana, and the address the URL names.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>IT DOES NOT REFUSE, AND THE READING THAT DECIDES THAT IS ON <see cref="Grafanas" />.</b>
///         ADR-011 allows Grafana <i>"as a managed instance (we distribute, we do not modify)"</i>;
///         this reconciler applies upstream's image by digest and configures it through its own
///         documented surface. Had the ADR refused AGPL for a deployed component, this method would
///         return <c>ReconcileOutcome.Failed</c> naming the ADR and the type would still be
///         published, so the API and the debt were both visible; it does not, so it converges.
///     </para>
///     <para>
///         ⚠ <b>THE ONE THING IT REFUSES IS THE WORKSPACE POINTER</b>, and it refuses it before it
///         mints anything: <see cref="Grafanas.WorkspaceOf" /> checks the tenant, the type and the
///         resource group the schema's <c>ResourceId</c> format cannot, and a refusal is terminal —
///         a Grafana pointed at another tenant's workspace is not a Grafana that will start working
///         later.
///     </para>
///     <para>
///         ⚠ <b>THE CREDENTIAL IS MINTED ONCE AND READ BACK ON EVERY PASS</b>, exactly as the
///         workspace's ingest key is — <c>MonitorWorkspaceReconciler.EnsureIngestKeyAsync</c>'s
///         argument: what reaches the rendered <c>Secret</c> is what the vault returns, so the
///         document is byte-stable across passes over a generator that is not.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop hold as they do on the collector;
///         the differences are one vault round trip and four objects rather than three.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class GrafanaReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => Grafanas.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a Grafana is a Deployment in a "
                + "cluster. CyberCloud.Dashboard/grafanas declares RequiresCluster, so the driver should "
                + "have refused this pass — see ReconcileDriver."
            );
        }

        var workspace = Grafanas.WorkspaceOf(context.Id, context.Desired);

        if (workspace.TryGetError(out var refusal)) {
            return ReconcileOutcome.Failed(refusal);
        }

        var workspaceName = workspace.GetValueOrThrow().Name;
        var name = context.Id.Name;

        // ── The credential ───────────────────────────────────────────────────────────────────
        var password = await EnsureAdminPasswordAsync(context, cancellationToken);

        if (password.TryGetError(out var passwordError)) {
            return ReconcileOutcome.FromFailure(passwordError);
        }

        context.Log.Report("applying-credential", $"applying the admin credential of '{name}'", 15);

        if (await Apply(
                context,
                cluster,
                Grafanas.SecretKind,
                Grafanas.AdminSecretJson(name, password.GetValueOrThrow()),
                cancellationToken
            ) is { } secretProblem) {
            return secretProblem;
        }

        context.Log.Report(
            "applying-datasources",
            $"provisioning '{name}' with workspace '{workspaceName}' as its two datasources",
            35
        );

        if (await Apply(
                context,
                cluster,
                Grafanas.ConfigMapKind,
                Grafanas.ConfigMapJson(name, workspaceName),
                cancellationToken
            ) is { } configProblem) {
            return configProblem;
        }

        context.Log.Report("applying-grafana", $"applying '{name}' at {Grafanas.Image}", 60);

        if (await Apply(
                context,
                cluster,
                Grafanas.DeploymentKind,
                Grafanas.DeploymentJson(name, workspaceName, context.Desired),
                cancellationToken
            ) is { } deploymentProblem) {
            return deploymentProblem;
        }

        context.Log.Report("applying-service", $"applying the address of '{name}'", 80);

        if (await Apply(
                context,
                cluster,
                Grafanas.ServiceKind,
                Grafanas.ServiceJson(name),
                cancellationToken
            ) is { } serviceProblem) {
            return serviceProblem;
        }

        // ── Clause 4. ─────────────────────────────────────────────────────────────────────────
        foreach (var target in Grafanas.Objects(context.Namespace, name)) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.TryGetError(out var readError)) {
                return readError.Code == ErrorCode.ResourceNotFound
                    ? ReconcileOutcome.InProgress(
                        $"'{target}' was applied and is not readable back yet",
                        TimeSpan.FromSeconds(5)
                    )
                    : ReconcileOutcome.FromFailure(readError);
            }

            if (!Grafanas.Matches(read.GetValueOrThrow().Json, workspaceName, context.Desired)) {
                return ReconcileOutcome.InProgress(
                    $"'{target}' is readable and does not yet carry the desired instance",
                    TimeSpan.FromSeconds(5)
                );
            }
        }

        context.Log.Report(
            "ready",
            $"all four objects of '{name}' read back as desired; it answers at {Grafanas.Url(context.Namespace, name)} "
            + "once its pod is running, which is the Deployment's status and not this pass's",
            100
        );

        return ReconcileOutcome.Converged;
    }

    /// <summary>
    ///     Puts an admin password in the vault if there is not one there, and reads back whichever is
    ///     now authoritative.
    /// </summary>
    static async Task<Result<string>> EnsureAdminPasswordAsync(
        ReconcileContext context,
        CancellationToken cancellationToken
    ) {
        var minted = await context.SecretWriter.MintAsync(
            Grafanas.SecretPath(context.Id),
            new Dictionary<string, string>(StringComparer.Ordinal) {
                [Grafanas.AdminPasswordField] = Grafanas.GenerateAdminPassword()
            },
            cancellationToken
        );

        if (minted.TryGetError(out var mintError)) {
            return Result<string>.Failure(mintError);
        }

        if (minted.GetValueOrThrow().Minted) {
            context.Log.Report("minting", $"a new admin password was written to the vault for '{context.Id.Name}'");
        }

        return await context.Secrets.ResolveAsync(Grafanas.AdminPasswordRef(context.Id), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Converged;
        }

        var name = context.Id.Name;

        context.Log.Report("deleting", $"removing '{name}'");

        // The reverse of the apply order; the address goes first. ⚠ The workspace name in the
        // rendered documents is read from the body and not re-checked: a delete addresses objects by
        // kind and name, and a body that was refused on create never applied anything to remove.
        var workspaceName = Grafanas.WorkspaceOf(context.Id, context.Desired).TryGetValue(out var workspace)
            ? workspace.Name
            : "deleting";

        foreach (var (kind, json) in new[] {
                     (Grafanas.ServiceKind, Grafanas.ServiceJson(name)),
                     (Grafanas.DeploymentKind, Grafanas.DeploymentJson(name, workspaceName, context.Desired)),
                     (Grafanas.ConfigMapKind, Grafanas.ConfigMapJson(name, workspaceName)),
                     (Grafanas.SecretKind, Grafanas.AdminSecretJson(name, Placeholder))
                 }) {
            var deleted = await KubeCommand.For(cluster)
                .WithTenantId(context.Id.TenantId)
                .WithResourceId(context.Id)
                .InNamespace(context.Namespace)
                .WithKind(kind)
                .WithApiVersion(context.ApiVersion)
                .ObjectJson(json)
                .DeleteAsync(CascadePolicy.Background, cancellationToken);

            if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(deleteError);
            }
        }

        foreach (var target in Grafanas.Objects(context.Namespace, name).Reverse()) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.IsSuccess) {
                return ReconcileOutcome.InProgress($"'{target}' is still readable", TimeSpan.FromSeconds(5));
            }

            if (read.Error!.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(read.Error);
            }
        }

        context.Log.Report("deleted", $"'{name}' is gone", 100);

        return ReconcileOutcome.Converged;
    }

    /// <summary>
    ///     A non-empty stand-in for the password on the delete path — <c>MonitorWorkspaceReconciler.Placeholder</c>'s
    ///     reason.
    /// </summary>
    const string Placeholder = "deleting";

    /// <inheritdoc />
    public async Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ObservedState.Absent;
        }

        var deployment = await cluster.GetAsync(
            Grafanas.DeploymentRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        if (deployment.TryGetError(out _)) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the Grafana Deployment is absent" };
        }

        var found = deployment.GetValueOrThrow();
        var workspaceName = Grafanas.WorkspaceOf(context.Id, context.Desired).TryGetValue(out var workspace)
            ? workspace.Name
            : string.Empty;
        var matches = workspaceName.Length > 0 && Grafanas.Matches(found.Json, workspaceName, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches ? "the instance is as declared" : "the instance has drifted"
        };
    }

    static async Task<ReconcileOutcome?> Apply(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        GroupVersionKind kind,
        string objectJson,
        CancellationToken cancellationToken
    ) {
        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(kind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(objectJson)
            .ApplyAsync(cancellationToken);

        if (applied.TryGetError(out var applyError)) {
            return ReconcileOutcome.FromFailure(applyError);
        }

        var outcome = applied.GetValueOrThrow();

        switch (outcome.Result) {
            case ApplyResult.Suspended:
                context.Log.Report("waiting-for-cluster", outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Message.Length > 0 ? outcome.Message : "the cluster is unreachable",
                    TimeSpan.FromSeconds(30)
                );
            case ApplyResult.Conflict:
                var describe = outcome.Drift?.Describe()
                    ?? $"another field manager owns part of the {kind.Kind} and it was not overwritten";

                context.Log.Report("conflict", describe);

                return ReconcileOutcome.InProgress(describe, TimeSpan.FromSeconds(30));
            default:
                return null;
        }
    }
}
