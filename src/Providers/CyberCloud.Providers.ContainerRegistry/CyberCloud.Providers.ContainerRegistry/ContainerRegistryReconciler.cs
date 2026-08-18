// ⚠ For `Result<T>` on the credential path. See ContainerRegistryProvider for why this import is safe
// beside the ErrorCode alias.
using CyberCloud.Core;
using CyberCloud.Core.Time;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.ContainerRegistry;

/// <summary>
///     Converges one container registry onto the fifteen objects Harbor is when nobody is running an
///     operator for it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>FIFTEEN OBJECTS, AND THE NUMBER IS A CONSEQUENCE RATHER THAN A DESIGN.</b>
///         <c>goharbor/harbor-operator</c> is archived — see <see cref="ContainerRegistries" /> — so
///         every object a controller would have expanded a <c>HarborCluster</c> into is applied here:
///         one <c>Secret</c>, one <c>ConfigMap</c>, six <c>Service</c>s, three <c>StatefulSet</c>s,
///         three <c>Deployment</c>s and, when monitoring is on, one <c>PodMonitor</c>. The catalogue's
///         range of rendered object counts now runs from one (<c>CyberCloud.Storage/accounts</c>, a
///         <c>Seaweed</c> that expands into a dozen workloads) to fifteen, and the two ends are the same
///         measurement from opposite sides.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop, and where each is satisfied:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> Every render is a pure function of the name and the body. Nothing
///             counts, appends or timestamps, and every credential reaches a workload as a
///             <c>secretKeyRef</c> rather than as a value — so the fourteen non-<c>Secret</c> documents
///             do not change when the vault changes. ⚠ <b>The credential path looks like a violation
///             and is not:</b> <see cref="ContainerRegistries.GenerateCredentials" /> returns a
///             different set every call, and every pass after the first has that candidate discarded by
///             mint-once and renders what it <i>resolved back</i> instead.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's <see cref="IClock" />,
///             which is a dependency rather than a memory. ⚠ A reconciler is registered <b>as a
///             singleton, by concrete type</b>, so one instance serves every tenant in the process — and
///             a <c>readonly</c> field holding a mutable dictionary is the shape that gets past a
///             structural check, because the field never reassigns.
///             <c>ContainerRegistryReconcilerTests</c> asserts both halves.
///         </item>
///         <item>
///             <b>Bounded.</b> Fifteen applies and fifteen reads on the caller's token, plus a mint and
///             one resolve. ⚠ That is by some distance the most work any pass in the catalogue does, and
///             it is why <see cref="ReconcileOutcome.InProgress" /> is returned the moment one object is
///             not readable back yet rather than waiting for it: the pass budget is thirty seconds and a
///             Harbor takes minutes to come up.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c> of every object, never any apply's own result.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>WHAT CONVERGED MEANS HERE, AND WHAT IT DOES NOT.</b> It means the fifteen objects are in
///         the cluster carrying what this provider rendered. It does not mean Harbor works: core runs a
///         database migration on first start, and a registry whose objects are all present can still be
///         a core crash-looping against a PostgreSQL that has not finished recovery. This provider reads
///         no <c>status</c> — unlike <c>ManagedClusterReconciler</c>, which is the one reconciler in the
///         tree that does — because a <c>Deployment</c>'s <c>availableReplicas</c> would make the
///         resource's convergence depend on a scheduler having capacity, and docs/plan/08's budget is a
///         pass rather than an outage. <c>charts/managed/harbor/conformance.yaml § owed</c>,
///         <c>converged-is-not-serving</c>.
///     </para>
///     <para>
///         ⚠ <b>An <see cref="ApplyResult.Conflict" /> is reported and retried rather than failed and
///         never forced.</b> ADR-013 makes a conflict <i>"a drift event with a name"</i>. On this type
///         the plausible one is a <c>Deployment</c>'s <c>spec.replicas</c>, which any horizontal
///         autoscaler a tenant runs over their own cluster ends up owning; forcing would fight it every
///         pass.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class ContainerRegistryReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => ContainerRegistries.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            // Unreachable through the driver, which refuses a RequiresCluster type with no connection
            // and names the type. Kept because a reconciler is also callable directly.
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a container registry is fifteen "
                + "objects in a cluster. CyberCloud.ContainerRegistry/registries declares "
                + "RequiresCluster, so the driver should have refused this pass — see ReconcileDriver."
            );
        }

        var name = context.Id.Name;

        // ── The credentials, before anything that consumes them ─────────────────────────────────
        //
        // ⚠ THE VAULT GOES FIRST, AND THE ORDER IS CHOSEN BY WHICH FAILURE IS SURVIVABLE — the same
        // argument StorageAccountReconciler makes, and it lands harder here.
        //
        //   • Mint, then the cluster fails → one orphaned KV document. INERT: nothing runs, nothing is
        //     reachable, nothing is billed, and the next pass finds it and uses it, because mint-once
        //     means the retry converges on the same credentials rather than a second set.
        //   • The cluster, then the mint fails → six workloads referencing a Secret that does not
        //     exist. On this operator-less shape that is survivable only because a missing
        //     `secretKeyRef` holds the pod in CreateContainerConfigError — and the moment anything
        //     renders a DEFAULT instead, goharbor/harbor-helm's own `harborAdminPassword:
        //     "Harbor12345"` is what a reader would reach for, and the registry comes up with an
        //     administrator credential printed in a public values.yaml.
        //
        // So the credentials exist before the thing that authenticates against them exists.
        context.Log.Report("minting", $"ensuring the credentials of '{name}' are in the vault", 5);

        var credentials = await EnsureCredentialsAsync(context, cancellationToken);

        if (credentials.TryGetError(out var credentialError)) {
            // ⚠ Retryable, and nothing has been applied. A vault that is sealed, unreachable or not
            // wired is a resource that has not started rather than one that failed.
            return ReconcileOutcome.FromFailure(credentialError);
        }

        var secrets = credentials.GetValueOrThrow();

        // ── The fifteen applies, in dependency order ───────────────────────────────────────────
        var targets = Targets(context.Namespace, name, context.Desired);
        var percent = 10;

        foreach (var (target, body) in Documents(name, context.Desired, secrets)) {
            context.Log.Report(
                "applying",
                $"applying {target.Kind.Kind} '{target.Name}' to {context.Namespace}",
                percent
            );

            percent = Math.Min(percent + 5, 85);

            var applied = await KubeCommand.For(cluster)
                .WithTenantId(context.Id.TenantId)
                .WithResourceId(context.Id)
                .InNamespace(context.Namespace)
                .WithKind(target.Kind)
                .WithApiVersion(context.ApiVersion)
                .ObjectJson(body)
                .ApplyAsync(cancellationToken);

            if (applied.TryGetError(out var applyError)) {
                // ⚠ The code decides, not this call site. An apply that could not reach the cluster is
                // a request that can be made again; one the API server refused — an admission policy,
                // a PodMonitor CRD the bundle never installed, our own credentials — will be refused
                // identically for the next hour.
                return ReconcileOutcome.FromFailure(applyError);
            }

            if (Unfinished(context, applied.GetValueOrThrow(), target.Kind.Kind + " " + target.Name)
                is { } waiting) {
                return waiting;
            }
        }

        // ── Clause 4. Everything above this line is a claim; these are the readings. ────────────
        //
        // ⚠ EVERY OBJECT, BECAUSE EVERY OBJECT WAS APPLIED.
        // ContainerRegistryReconcilerTests.EveryAppliedObjectIsAlsoReadBack asserts the two sets are
        // equal in both directions. On a fifteen-object type that is not a formality: an object
        // applied and never read back is one the loop reports Converged without having observed, and
        // fourteen right ones make the fifteenth invisible.
        foreach (var target in targets) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.TryGetError(out var readError)) {
                return readError.Code == ErrorCode.ResourceNotFound
                    ? ReconcileOutcome.InProgress(
                        $"'{target}' was applied and is not readable back yet",
                        TimeSpan.FromSeconds(5)
                    )
                    : ReconcileOutcome.FromFailure(readError);
            }

            if (!ContainerRegistries.Matches(read.GetValueOrThrow().Json, context.Desired)) {
                return ReconcileOutcome.InProgress(
                    $"'{target}' is readable and does not yet carry the desired spec",
                    TimeSpan.FromSeconds(5)
                );
            }
        }

        context.Log.Report("ready", $"the objects of '{name}' read back as desired", 100);

        return ReconcileOutcome.Converged;
    }

    /// <summary>
    ///     Puts a full credential set in the vault if there is not one there, and reads back whichever
    ///     set is now authoritative.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE MINT AND THE READ ARE BOTH HERE, AND THE READ IS WHAT MAKES THE PASS
    ///         IDEMPOTENT.</b> <see cref="ContainerRegistries.GenerateCredentials" /> produces a
    ///         different set every call — it has to, or an administrator password would be derivable
    ///         from a resource id. What reaches a manifest is never that set: it is what
    ///         <see cref="ISecretResolver.ResolveAsync" /> returns afterwards, which is the set the
    ///         <i>first</i> pass minted, on every pass.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>SIX FIELDS IN ONE MINT, WHICH IS THE WHOLE REASON THEY ARE ONE VAULT DOCUMENT.</b>
    ///         <c>ISecretWriter.MintAsync</c>'s <c>cas=0</c> is per <i>path</i>, so six paths would be
    ///         six independent races and a pass interrupted between two of them would leave a registry
    ///         with three credentials from one attempt and three from another — which is a Harbor whose
    ///         core and job service disagree about the shared secret and reject each other's requests
    ///         forever. One path makes the whole set atomic.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Six resolves, one per field, because a <c>SecretRef</c> addresses one field by
    ///         design.</b> That is six round trips per pass and it is the cost of a resolver that
    ///         cannot hand back a value nobody asked for — see <c>SecretRef</c>'s own remarks.
    ///     </para>
    /// </remarks>
    static async Task<Result<Dictionary<string, string>>> EnsureCredentialsAsync(
        ReconcileContext context,
        CancellationToken cancellationToken
    ) {
        var path = ContainerRegistries.SecretPath(context.Id);

        var minted = await context.SecretWriter.MintAsync(
            path,
            ContainerRegistries.GenerateCredentials(),
            cancellationToken
        );

        if (minted.TryGetError(out var mintError)) {
            return Result<Dictionary<string, string>>.Failure(mintError);
        }

        if (minted.GetValueOrThrow().Minted) {
            context.Log.Report(
                "minting",
                $"a new credential set was written to the vault for '{context.Id.Name}'"
            );
        }

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var field in ContainerRegistries.CredentialFields) {
            var value = await context.Secrets.ResolveAsync(
                ContainerRegistries.CredentialRef(context.Id, field),
                cancellationToken
            );

            if (value.TryGetError(out var resolveError)) {
                return Result<Dictionary<string, string>>.Failure(resolveError);
            }

            resolved[field] = value.GetValueOrThrow();
        }

        return Result<Dictionary<string, string>>.Success(resolved);
    }

    /// <summary>
    ///     Turns an apply that did not land into the outcome that comes back for it, or
    ///     <see langword="null" /> when it landed.
    /// </summary>
    static ReconcileOutcome? Unfinished(ReconcileContext context, ApplyOutcome outcome, string what) {
        switch (outcome.Result) {
            case ApplyResult.Suspended:
                // docs/plan/09 § Cluster connections: an unreachable cluster suspends reconciles rather
                // than failing them. A tenant whose cluster is down has a resource that is still
                // coming, not one that broke.
                context.Log.Report("waiting-for-cluster", outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Message.Length > 0 ? outcome.Message : "the cluster is unreachable",
                    TimeSpan.FromSeconds(30)
                );

            case ApplyResult.Conflict:
                context.Log.Report("conflict", outcome.Drift?.Describe() ?? outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Drift?.Describe()
                    ?? $"another field manager owns part of {what} and it was not overwritten",
                    TimeSpan.FromSeconds(30)
                );

            default:
                return null;
        }
    }

    /// <summary>Every object a registry owns, in apply order.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE ORDER IS A DEPENDENCY ORDER AND NOT AN ALPHABETICAL ONE.</b> The credentials
    ///         <c>Secret</c> and the <c>ConfigMap</c> come first because every workload mounts or
    ///         references one of them; the six <c>Service</c>s come next because each
    ///         <c>StatefulSet</c>'s <c>serviceName</c> names one and a set whose governing service does
    ///         not exist produces pods with no DNS record; the data plane comes before core because
    ///         core's first action is a schema migration.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>None of that is enforced by Kubernetes and none of it needs to be.</b> An apply
    ///         order is a convergence <i>speed</i> rather than a correctness property — a
    ///         <c>StatefulSet</c> applied before its <c>Service</c> is created and simply lacks DNS
    ///         until the next object lands. What the order buys is that the common case reaches a
    ///         working Harbor in one pass instead of three.
    ///     </para>
    ///     <para>
    ///         ⚠ Static, like <c>NatsClusters</c>' equivalent, because a reconciler is one singleton
    ///         across every tenant in the process — clause 2, and the blind spot
    ///         <c>CheckNoHiddenState</c> has around a <c>readonly</c> field holding a mutable
    ///         collection.
    ///     </para>
    /// </remarks>
    public static ObjectRef[] Targets(string ns, string name, JsonElement desired) {
        ObjectRef[] always = [
            ContainerRegistries.CredentialsSecretRef(ns, name),
            ContainerRegistries.ConfigMapRef(ns, name),
            ContainerRegistries.DatabaseServiceRef(ns, name),
            ContainerRegistries.RedisServiceRef(ns, name),
            ContainerRegistries.RegistryServiceRef(ns, name),
            ContainerRegistries.CoreServiceRef(ns, name),
            ContainerRegistries.PortalServiceRef(ns, name),
            ContainerRegistries.JobServiceServiceRef(ns, name),
            ContainerRegistries.DatabaseSetRef(ns, name),
            ContainerRegistries.RedisSetRef(ns, name),
            ContainerRegistries.RegistrySetRef(ns, name),
            ContainerRegistries.CoreDeploymentRef(ns, name),
            ContainerRegistries.PortalDeploymentRef(ns, name),
            ContainerRegistries.JobServiceDeploymentRef(ns, name)
        ];

        return ContainerRegistries.MonitoringEnabled(desired)
            ? [.. always, ContainerRegistries.PodMonitorRef(ns, name)]
            : always;
    }

    /// <summary>Every object a registry owns, paired with the document it is.</summary>
    /// <remarks>
    ///     ⚠ <b>Built beside <see cref="Targets" /> and asserted against it.</b> A fifteen-object type
    ///     has two lists that must stay the same length and the same order — one the reconciler applies
    ///     and one it reads back — and
    ///     <c>ContainerRegistryReconcilerTests.EveryTargetHasADocumentAndEveryDocumentHasATarget</c> is
    ///     what says so. A sixteenth object added to one and not the other is either an object nobody
    ///     observes or a read of something nothing wrote.
    /// </remarks>
    static (ObjectRef Target, string Body)[] Documents(
        string name,
        JsonElement desired,
        IReadOnlyDictionary<string, string> credentials
    ) {
        // ⚠ The namespace is empty here and is supplied by the command builder's InNamespace. These
        // refs are used for their KIND and their NAME only, which is what the apply loop reads off
        // them; the read-back loop uses Targets, which carries the real namespace.
        (ObjectRef Target, string Body)[] always = [
            (ContainerRegistries.CredentialsSecretRef(string.Empty, name),
                ContainerRegistries.CredentialsSecretJson(name, credentials)),
            (ContainerRegistries.ConfigMapRef(string.Empty, name),
                ContainerRegistries.ConfigMapJson(name, desired)),
            (ContainerRegistries.DatabaseServiceRef(string.Empty, name),
                ContainerRegistries.DatabaseServiceJson(name)),
            (ContainerRegistries.RedisServiceRef(string.Empty, name),
                ContainerRegistries.RedisServiceJson(name)),
            (ContainerRegistries.RegistryServiceRef(string.Empty, name),
                ContainerRegistries.RegistryServiceJson(name)),
            (ContainerRegistries.CoreServiceRef(string.Empty, name),
                ContainerRegistries.CoreServiceJson(name, desired)),
            (ContainerRegistries.PortalServiceRef(string.Empty, name),
                ContainerRegistries.PortalServiceJson(name)),
            (ContainerRegistries.JobServiceServiceRef(string.Empty, name),
                ContainerRegistries.JobServiceServiceJson(name)),
            (ContainerRegistries.DatabaseSetRef(string.Empty, name),
                ContainerRegistries.DatabaseSetJson(name, desired)),
            (ContainerRegistries.RedisSetRef(string.Empty, name),
                ContainerRegistries.RedisSetJson(name, desired)),
            (ContainerRegistries.RegistrySetRef(string.Empty, name),
                ContainerRegistries.RegistrySetJson(name, desired)),
            (ContainerRegistries.CoreDeploymentRef(string.Empty, name),
                ContainerRegistries.CoreDeploymentJson(name, desired)),
            (ContainerRegistries.PortalDeploymentRef(string.Empty, name),
                ContainerRegistries.PortalDeploymentJson(name, desired)),
            (ContainerRegistries.JobServiceDeploymentRef(string.Empty, name),
                ContainerRegistries.JobServiceDeploymentJson(name, desired))
        ];

        return ContainerRegistries.MonitoringEnabled(desired)
            ?
            [
                .. always,
                (ContainerRegistries.PodMonitorRef(string.Empty, name),
                    ContainerRegistries.PodMonitorJson(name))
            ]
            : always;
    }

    /// <inheritdoc />
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            // ⚠ Converged, not Failed, and the asymmetry with ReconcileAsync is deliberate — a teardown
            // with no cluster to reach has nothing left to remove, and failing would park the resource
            // in Deleting: visible, billed and permanent, for a wiring reason.
            return ReconcileOutcome.Converged;
        }

        var name = context.Id.Name;

        context.Log.Report("deleting", $"deleting the objects of '{name}'");

        var targets = Targets(context.Namespace, name, context.Desired);

        // ⚠ THE APPLY ORDER REVERSED, AND IT IS REVERSED FOR A SECURITY REASON RATHER THAN A TIDINESS
        // ONE. The credentials Secret is applied FIRST and therefore removed LAST: taking it away from
        // under six running components would restart every one of them without the values they
        // authenticate each other with, and a Harbor core that cannot read HARBOR_ADMIN_PASSWORD does
        // not refuse callers — src/core/main.go applies the environment value only when the stored
        // salt is empty, so what a restart with an absent variable produces is a core still holding
        // whatever the database says, with the platform no longer able to say what that is. A teardown
        // interrupted before the last step leaves a Secret nobody mounts, which is inert.
        foreach (var target in targets.Reverse()) {
            var deleted = await KubeCommand.For(cluster)
                .WithTenantId(context.Id.TenantId)
                .WithResourceId(context.Id)
                .InNamespace(context.Namespace)
                .WithKind(target.Kind)
                .WithApiVersion(context.ApiVersion)
                .ObjectJson(Placeholder(target.Name))
                // ⚠ Background, not Foreground. A Foreground cascade blocks each delete on the garbage
                // collector removing every dependent — here, three ReplicaSets, three controller
                // revisions and their pods — while a converge loop with a bounded PASS budget runs out
                // of passes waiting for a controller it does not drive. The read-back below is what
                // makes Background safe: this returns Converged when the objects are GONE.
                .DeleteAsync(CascadePolicy.Background, cancellationToken);

            if (deleted.TryGetError(out var deleteError)
                && deleteError.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(deleteError);
            }
        }

        foreach (var target in targets) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.IsSuccess) {
                return ReconcileOutcome.InProgress(
                    $"'{target}' is still readable",
                    TimeSpan.FromSeconds(5)
                );
            }

            if (read.Error!.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(read.Error);
            }
        }

        // ⚠ THE IMAGES SURVIVE THIS, AND THAT IS WHAT MAKES THIS TYPE'S RECOVERY WINDOW HONEST RATHER
        // THAN ADVERTISED. Deleting a StatefulSet does not delete the PersistentVolumeClaims its
        // volumeClaimTemplate created — that is Kubernetes' own behaviour and the reason
        // ContainerRegistries.StatefulSetKind's remarks say a StatefulSet was chosen over a Deployment
        // with a claim beside it. So a soft-deleted registry's layers, its database and its job queue
        // are all still on disk for the window docs/plan/08 promises, and a restore has something to
        // restore. ⚠ What ends them is the StorageClass's reclaim policy once somebody removes the
        // claims, which nothing in this platform does yet — charts/managed/harbor/conformance.yaml
        // § owed, `purge-leaves-the-volumes-behind`.
        //
        // ⚠ THE VAULT ENTRY IS NOT REMOVED EITHER, for the reason StorageAccountReconciler gives: a
        // teardown re-driven from a reminder would race its own retry, minting a second credential set
        // while the first teardown is still tearing down the components that trust the first. On a
        // soft-deletable type there is a second reason and it is stronger — the credentials have to
        // outlive the delete or a restore hands the tenant a registry they cannot log in to.
        context.Log.Report("deleted", $"the objects of '{name}' are gone", 100);

        return ReconcileOutcome.Converged;
    }

    /// <summary>The smallest object a delete command will accept.</summary>
    /// <remarks>
    ///     ⚠ A name and nothing else, because a delete addresses rather than describes. Rendering the
    ///     real body here would mean resolving six credentials out of the vault in order to throw them
    ///     away.
    /// </remarks>
    static string Placeholder(string name) =>
        new JsonObject { ["metadata"] = new JsonObject { ["name"] = name } }.ToJsonString();

    /// <inheritdoc />
    public async Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ObservedState.Absent;
        }

        // ⚠ CORE AND NOT ALL FIFTEEN. ObserveAsync feeds the drift scanner, which runs on a timer over
        // every resource in the platform; fifteen reads per registry per tick is a cost the scanner
        // pays for nothing, because a registry whose core Deployment is intact and undrifted is one
        // whose other fourteen objects the next reconcile pass will check anyway. Core is the one that
        // carries the version, the replica count and every credential reference.
        var read = await cluster.GetAsync(
            ContainerRegistries.CoreDeploymentRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        if (read.TryGetError(out _)) {
            return new() {
                Exists = false, ObservedAt = clock.UtcNow, Summary = "the registry's core is absent"
            };
        }

        var found = read.GetValueOrThrow();
        var matches = ContainerRegistries.Matches(found.Json, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches
                ? "the registry's core carries the desired spec"
                : "the registry's core has drifted"
        };
    }
}
