using CyberCloud.Core.Time;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.ContainerInstance;

/// <summary>
///     Converges one container group onto its <c>Pod</c>, the Secrets its vault handles resolve into,
///     and — when the body names a public address — the Kube-OVN floating IP onto it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>IT READS THE POD BEFORE IT APPLIES, AND REPLACES ONE THAT DOES NOT MATCH.</b> A pod's spec
///         is almost entirely immutable, so an apply that changes a container, a variable or the
///         resource block is refused by the API server rather than rolled out. The pass therefore
///         compares the live pod with the desired one first (<see cref="ContainerGroups.Matches" />),
///         deletes a pod that differs, and answers <c>InProgress</c>; the next pass finds nothing and
///         creates the new one. A pod still terminating is waited for, because the new one takes its
///         name.
///     </para>
///     <para>
///         ⚠ <b>The body's relations are refused before anything is read or resolved</b> —
///         <see cref="ContainerGroups.BodyProblem" />: the element grammar the four arrays cannot carry,
///         and every vault path outside the tenant's own prefix, as
///         <see cref="ErrorCode.AuthorizationFailed" /> before the resolver is asked.
///     </para>
///     <para>
///         ⚠ <b><c>Converged</c> follows the kubelet.</b> A <c>Pending</c> pod is <c>InProgress</c>
///         with the reason a container or the scheduler gave — <c>ErrImagePull</c> and the registry's
///         own sentence, most often — and a pod that is running, or ran to an end, converges;
///         <see cref="ContainerGroups.ReadinessOf" /> says why a crash loop converges too.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop: idempotent (every document is a pure
///         function of the address, the body and the vault's values); no hidden state (the one field is
///         the <see cref="IClock" />); bounded (one vault read per handle, two reads and at most four
///         applies); observes, never assumes (<c>Converged</c> follows a read of every object).
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class ContainerGroupReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => ContainerGroups.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a container group is a pod in a cluster. "
                + "CyberCloud.ContainerInstance/containerGroups declares RequiresCluster, so the driver should "
                + "have refused this pass — see ReconcileDriver."
            );
        }

        var name = context.Id.Name;
        var ns = context.Namespace;

        if (ContainerGroups.BodyProblem(context.Desired, context.Id.TenantId) is { } problem) {
            return ReconcileOutcome.FromFailure(problem);
        }

        // ── The vault: every handle resolved once, into a Secret and nowhere else ──────────────
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (variable, handle) in ContainerGroups.SecureEnvironment(context.Desired)) {
            var reference = ContainerGroups.ParseSecretRef(handle, context.Id.TenantId, "/properties/secureEnvironment")
                .GetValueOrThrow();
            var resolved = await context.Secrets.ResolveAsync(reference, cancellationToken);

            if (resolved.TryGetError(out var resolveError)) {
                return ReconcileOutcome.FromFailure(resolveError);
            }

            environment[variable] = resolved.GetValueOrThrow();
        }

        string? password = null;

        if (ContainerGroups.HasPullSecret(context.Desired)) {
            var reference = ContainerGroups.ParseSecretRef(
                    ContainerGroups.RegistryPassword(context.Desired),
                    context.Id.TenantId,
                    "/properties/registry/password"
                )
                .GetValueOrThrow();
            var resolved = await context.Secrets.ResolveAsync(reference, cancellationToken);

            if (resolved.TryGetError(out var resolveError)) {
                return ReconcileOutcome.FromFailure(resolveError);
            }

            password = resolved.GetValueOrThrow();
        }

        if (environment.Count > 0) {
            context.Log.Report("applying-environment", $"writing the secure environment of '{name}' into its Secret", 20);

            if (await Apply(context, cluster, KubeSecret.Kind, ContainerGroups.EnvironmentSecretJson(name, environment), cancellationToken)
                is { } secretProblem) {
                return secretProblem;
            }
        }

        if (password is not null) {
            context.Log.Report("applying-pull-secret", $"writing the registry credential of '{name}' into its pull Secret", 30);

            if (await Apply(
                    context,
                    cluster,
                    KubeSecret.Kind,
                    ContainerGroups.PullSecretJson(
                        name,
                        ContainerGroups.RegistryServer(context.Desired),
                        ContainerGroups.RegistryUsername(context.Desired),
                        password
                    ),
                    cancellationToken
                ) is { } pullProblem) {
                return pullProblem;
            }
        }

        // ── The pod: replaced when it differs, because it cannot be updated ────────────────────
        var existing = await cluster.GetAsync(ContainerGroups.PodRef(ns, name), cancellationToken);

        if (existing.TryGetError(out var existingError) && existingError.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(existingError);
        }

        if (existing.IsSuccess) {
            var live = existing.GetValueOrThrow().Json;

            if (ContainerGroups.IsTerminating(live)) {
                context.Log.Report("waiting-for-pod", $"the previous pod of '{name}' is still shutting down", 40);

                return ReconcileOutcome.InProgress(
                    $"the previous pod of '{name}' is still shutting down, and the new one takes its name",
                    TimeSpan.FromSeconds(3)
                );
            }

            if (!ContainerGroups.Matches(live, ns, context.Desired)) {
                context.Log.Report("replacing-pod", $"the pod of '{name}' does not carry the desired spec and is replaced", 40);

                var deleted = await Delete(context, cluster, ContainerGroups.PodKind, ContainerGroups.PodName(name), CascadePolicy.Background, cancellationToken);

                if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
                    return ReconcileOutcome.FromFailure(deleteError);
                }

                // ⚠ READ AGAIN RATHER THAN ASSUME. An object with nothing running behind it — a
                // hand-written stand-in with no kubelet, a pod never scheduled — is gone the moment
                // the delete returns, and this pass creates its replacement; a pod with containers is
                // terminating for its grace period, and the pass waits for the next one.
                var after = await cluster.GetAsync(ContainerGroups.PodRef(ns, name), cancellationToken);

                if (after.IsSuccess) {
                    return ReconcileOutcome.InProgress(
                        $"the pod of '{name}' is being replaced: a pod's containers, environment and resources "
                        + "cannot be changed in place",
                        TimeSpan.FromSeconds(3)
                    );
                }

                if (after.Error!.Code != ErrorCode.ResourceNotFound) {
                    return ReconcileOutcome.FromFailure(after.Error);
                }
            }
        }

        context.Log.Report("applying-pod", $"applying the pod of '{name}'", 60);

        if (await Apply(context, cluster, ContainerGroups.PodKind, ContainerGroups.PodJson(ns, name, context.Desired), cancellationToken)
            is { } podProblem) {
            return podProblem;
        }

        if (ContainerGroups.HasPublicIpAddress(context.Desired)) {
            context.Log.Report("applying-public-address", $"translating '{ContainerGroups.PublicIpAddress(context.Desired)}' onto '{name}'", 70);

            if (await Apply(
                    context,
                    cluster,
                    ContainerGroups.OvnFipKind,
                    ContainerGroups.FloatingIpJson(ns, name, context.Desired),
                    cancellationToken
                ) is { } fipProblem) {
                return fipProblem;
            }
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ──────────────
        if (environment.Count > 0) {
            var secret = await cluster.GetAsync(ContainerGroups.EnvironmentSecretRef(ns, name), cancellationToken);

            if (secret.TryGetError(out var secretError)) {
                return Unread(secretError, "the secure-environment Secret");
            }

            foreach (var (variable, value) in environment) {
                var carried = KubeSecret.Value(secret.GetValueOrThrow(), variable);

                if (!carried.IsSuccess || carried.GetValueOrThrow() != value) {
                    return ReconcileOutcome.InProgress(
                        "the secure-environment Secret is readable and does not yet carry the vault's values",
                        TimeSpan.FromSeconds(5)
                    );
                }
            }
        }

        if (password is not null) {
            var secret = await cluster.GetAsync(ContainerGroups.PullSecretRef(ns, name), cancellationToken);

            if (secret.TryGetError(out var secretError)) {
                return Unread(secretError, "the pull Secret");
            }

            var carried = KubeSecret.Value(secret.GetValueOrThrow(), ContainerGroups.DockerConfigKey);

            if (!carried.IsSuccess
                || carried.GetValueOrThrow()
                != ContainerGroups.DockerConfig(
                    ContainerGroups.RegistryServer(context.Desired),
                    ContainerGroups.RegistryUsername(context.Desired),
                    password
                )) {
                return ReconcileOutcome.InProgress(
                    "the pull Secret is readable and does not yet carry the vault's credential",
                    TimeSpan.FromSeconds(5)
                );
            }
        }

        if (ContainerGroups.HasPublicIpAddress(context.Desired)) {
            var fip = await cluster.GetAsync(ContainerGroups.FloatingIpRef(ns, name), cancellationToken);

            if (fip.TryGetError(out var fipError)) {
                return Unread(fipError, "the OvnFip");
            }

            var spec = JsonNode.Parse(fip.GetValueOrThrow().Json)?["spec"];

            if (spec?["ovnEip"]?.GetValue<string>() != ContainerGroups.OvnEipOf(ns, context.Desired)
                || spec?["ipName"]?.GetValue<string>() != ContainerGroups.PodIpObjectName(ns, name)) {
                return ReconcileOutcome.InProgress(
                    "the OvnFip is readable and does not yet carry the desired address",
                    TimeSpan.FromSeconds(5)
                );
            }
        }

        var read = await cluster.GetAsync(ContainerGroups.PodRef(ns, name), cancellationToken);

        if (read.TryGetError(out var readError)) {
            return Unread(readError, "the pod");
        }

        var json = read.GetValueOrThrow().Json;

        if (!ContainerGroups.Matches(json, ns, context.Desired)) {
            return ReconcileOutcome.InProgress(
                "the pod is readable and does not yet carry the desired spec",
                TimeSpan.FromSeconds(5)
            );
        }

        var readiness = ContainerGroups.ReadinessOf(json);

        if (readiness.Kind == ContainerGroups.ReadinessKind.NotReady) {
            context.Log.Report("waiting-for-kubelet", readiness.Detail, 80);
            return ReconcileOutcome.InProgress(readiness.Detail, TimeSpan.FromSeconds(5));
        }

        context.Log.Report(
            "ready",
            readiness.Kind == ContainerGroups.ReadinessKind.Ready
                ? readiness.Detail
                // ⚠ Said in the tenant's own progress log — conformance.yaml § owed, `converged-is-not-ready`.
                : $"the pod of '{name}' reads back as desired; the kubelet has not reported on it yet",
            100
        );

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The pod first and Foreground</b>, for <c>CloudConsoleReconciler</c>'s reason: a background
    ///     delete returns once the pod is marked, and a read-back that reported it gone while its
    ///     containers still ran would stop billing a workload that is still working. The Secrets are
    ///     deleted whether or not the current body names them, because an earlier body may have. The
    ///     floating IP is deleted only when the body names a public address: on a cluster with no
    ///     Kube-OVN the kind is not served at all, and a delete of it would fail on the kind rather than
    ///     find nothing.
    /// </remarks>
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Converged;
        }

        var name = context.Id.Name;
        var ns = context.Namespace;

        context.Log.Report("deleting", $"stopping the containers of '{name}' and removing its objects");

        var targets = new List<(GroupVersionKind Kind, string Name, ObjectRef Ref, CascadePolicy Policy)> {
            (ContainerGroups.PodKind, ContainerGroups.PodName(name), ContainerGroups.PodRef(ns, name), CascadePolicy.Foreground),
            (KubeSecret.Kind, ContainerGroups.EnvironmentSecretName(name), ContainerGroups.EnvironmentSecretRef(ns, name), CascadePolicy.Background),
            (KubeSecret.Kind, ContainerGroups.PullSecretName(name), ContainerGroups.PullSecretRef(ns, name), CascadePolicy.Background)
        };

        if (ContainerGroups.HasPublicIpAddress(context.Desired)) {
            targets.Insert(
                0,
                (ContainerGroups.OvnFipKind, ContainerGroups.FloatingIpName(ns, name), ContainerGroups.FloatingIpRef(ns, name), CascadePolicy.Background)
            );
        }

        foreach (var (kind, objectName, _, policy) in targets) {
            var deleted = await Delete(context, cluster, kind, objectName, policy, cancellationToken);

            if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(deleteError);
            }
        }

        foreach (var (_, _, target, _) in targets) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.IsSuccess) {
                return ReconcileOutcome.InProgress($"'{target}' is still readable", TimeSpan.FromSeconds(3));
            }

            if (read.Error!.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(read.Error);
            }
        }

        context.Log.Report("deleted", $"the pod and the Secrets of '{name}' are gone", 100);
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ObservedState.Absent;
        }

        var read = await cluster.GetAsync(ContainerGroups.PodRef(context.Namespace, context.Id.Name), cancellationToken);

        if (read.TryGetError(out _)) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the container group's pod is absent" };
        }

        var found = read.GetValueOrThrow();
        var matches = ContainerGroups.Matches(found.Json, context.Namespace, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = (matches ? "the container group carries the desired spec" : "the container group has drifted")
                + "; " + ContainerGroups.ReadinessOf(found.Json).Detail
                + $", {ContainerGroups.RestartCount(found.Json)} container restart(s)"
        };
    }

    /// <summary>Applies one object, or answers the outcome that ends the pass.</summary>
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
            .InNamespace(kind.Kind == ContainerGroups.OvnFipKind.Kind ? string.Empty : context.Namespace)
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
                context.Log.Report("conflict", outcome.Drift?.Describe() ?? outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Drift?.Describe()
                    ?? $"another field manager owns part of the {kind.Kind} and it was not overwritten",
                    TimeSpan.FromSeconds(30)
                );

            default:
                return null;
        }
    }

    /// <summary>Deletes one object by name.</summary>
    internal static Task<Result> Delete(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        GroupVersionKind kind,
        string objectName,
        CascadePolicy policy,
        CancellationToken cancellationToken
    ) =>
        KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(kind.Kind == ContainerGroups.OvnFipKind.Kind ? string.Empty : context.Namespace)
            .WithKind(kind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(new JsonObject { ["metadata"] = new JsonObject { ["name"] = objectName } }.ToJsonString())
            .DeleteAsync(policy, cancellationToken);

    static ReconcileOutcome Unread(Error error, string what) =>
        error.Code == ErrorCode.ResourceNotFound
            ? ReconcileOutcome.InProgress($"{what} was applied and is not readable back yet", TimeSpan.FromSeconds(5))
            : ReconcileOutcome.FromFailure(error);
}
