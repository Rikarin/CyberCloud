// ⚠ For `Result<string>`, which EnsurePasswordAsync returns. `CyberCloud.Core.Resources` is global
// here and `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins over the
// `Orleans.ErrorCode` this import would otherwise put back in play.
using CyberCloud.Core;
using CyberCloud.Core.Time;
using System.Collections.Immutable;

namespace CyberCloud.Providers.Cache;

/// <summary>
///     Converges one cache onto a spotahome <c>RedisFailover</c>.
/// </summary>
/// <remarks>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop, and where each is satisfied:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> The apply is server-side, so a second pass with the same body is an
///             <c>Unchanged</c>. Nothing here counts, appends or timestamps.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />, which is a dependency rather than a memory. ⚠ A reconciler is
///             registered <b>as a singleton, by concrete type</b> (<c>AddCyberCloudProvider</c>'s
///             remarks), so one instance serves every tenant in the process — a field caching, say, the
///             last rendered spec would hand tenant B tenant A's eviction policy, and a single-tenant
///             test could not see it. <c>ValkeyReconcilerTests</c> holds both checks: the structural
///             one, and the cross-tenant one that catches what the structural one cannot.
///         </item>
///         <item>
///             <b>Bounded.</b> One vault round trip, two applies and two reads, on the caller's
///             token. ⚠ There is no wait for
///             the cache to be <i>ready</i>: the operator has a StatefulSet, three Sentinels and a
///             failover election to run, and clause 3's budget is thirty seconds, so readiness is
///             <see cref="ReconcileOutcome.InProgress" /> and the reminder comes back.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c> of the object, never the apply's own result.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>Converged here means "the CR is applied and reads back", not "Valkey is answering
///         PING".</b> The honest stronger check is the operator's own readiness, and it is not made
///         because nothing in this repository can produce it: the Docker-free harness is a dictionary
///         and no operator runs anywhere either suite reaches. A readiness gate written against a world
///         that never sets the condition would make every resource in every test hang and then fail.
///         <c>charts/managed/valkey/conformance.yaml</c>'s <c>connect-through-sentinel</c> assertion is
///         where that is written down as owed.
///     </para>
///     <para>
///         ⚠ <b>Two objects, and one of them is the reason the other works.</b> A
///         <c>RedisFailover</c> expands into a StatefulSet, a Deployment, three Services and two
///         ConfigMaps — all of them the operator's, none of them this provider's. What this provider
///         owns is the CR and the <c>Secret</c> the CR's <c>spec.auth.secretPath</c> names; the rest is
///         the operator's business and is exactly what <see cref="ReconcileOutcome.Converged" /> is
///         careful not to claim.
///     </para>
///     <para>
///         ⚠ <b>THE <c>Secret</c> IS APPLIED FIRST, AND IT IS NOT AN ORDERING PREFERENCE.</b>
///         spotahome generates nothing: <c>service/k8s/util.go</c>'s <c>GetRedisPassword</c> reads
///         <c>secret.Data["password"]</c> out of the namespace and returns an error when the object is
///         not there, so a <c>RedisFailover</c> applied ahead of its <c>Secret</c> is a cache whose
///         pods fail from the first reconcile the operator performs. This is <b>the one data type in
///         the tree whose credential this platform mints</b> rather than reads back from an operator —
///         see <c>ValkeyCaches.RedisFailoverJson</c> for why the exception is spotahome's doing.
///     </para>
///     <para>
///         ⚠ <b>A SECOND PASS MUST NOT ROTATE THE PASSWORD, AND THAT IS WHAT
///         <see cref="EnsurePasswordAsync" /> IS FOR.</b> <see cref="ValkeyCaches.GeneratePassword" />
///         answers differently every call; what reaches the rendered <c>Secret</c> is never that value
///         but what <see cref="ISecretResolver.ResolveAsync" /> returns afterwards, which is the value
///         the <i>first</i> pass minted, on every pass. A reconciler that rendered its own candidate
///         would hand a running Valkey a <c>requirepass</c> its clients have never been told, once per
///         reminder, forever — and every surface would report success.
///     </para>
///     <para>
///         ⚠ <b>An <see cref="ApplyResult.Conflict" /> is reported and retried rather than failed and
///         never forced</b>, and on this type the rival is plausibly the operator itself: spotahome's
///         <c>Validate()</c> fills in images, ports and exporter images and prepends its own
///         <c>replica-priority 100</c> to <c>spec.redis.customConfig</c>. ADR-013 makes a conflict "a
///         drift event with a name"; forcing would take a list field back from the controller that
///         maintains it, once per reminder, forever.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class ValkeyCacheReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => ValkeyCaches.Type;

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
                $"'{context.Id.Path}' has no cluster connection, and a Valkey cache is a RedisFailover "
                + "in a cluster. CyberCloud.Cache/redis declares RequiresCluster, so the driver should "
                + "have refused this pass — see ReconcileDriver."
            );
        }

        var name = context.Id.Name;

        // ── The credential, BEFORE the CR that names it. See the remarks. ──────────────────────
        var password = await EnsurePasswordAsync(context, cancellationToken);

        if (password.TryGetError(out var passwordError)) {
            return ReconcileOutcome.FromFailure(passwordError);
        }

        context.Log.Report(
            "applying-credential",
            $"applying the requirepass of '{name}' to {context.Namespace}",
            20
        );

        if (await Apply(
                context,
                cluster,
                ValkeyCaches.SecretKind,
                ValkeyCaches.CredentialSecretJson(name, password.GetValueOrThrow()),
                "the credential Secret",
                cancellationToken
            ) is { } credentialProblem) {
            return credentialProblem;
        }

        context.Log.Report(
            "applying",
            $"applying the RedisFailover '{name}' to {context.Namespace}",
            50
        );

        if (await Apply(
                context,
                cluster,
                ValkeyCaches.FailoverKind,
                ValkeyCaches.RedisFailoverJson(name, context.Desired),
                "the RedisFailover",
                cancellationToken
            ) is { } failoverProblem) {
            return failoverProblem;
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ───────────────
        //
        // ⚠ BOTH. A read-back of the RedisFailover alone would report Converged for a cache whose
        // Secret apply was silently swallowed — which is precisely the state this whole change exists
        // to end: a resource the platform calls done and an operator that cannot start a single pod.
        foreach (var target in Targets(context.Namespace, name)) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.TryGetError(out var readError)) {
                return readError.Code == ErrorCode.ResourceNotFound
                    ? ReconcileOutcome.InProgress(
                        $"'{target}' was applied and is not readable back yet",
                        TimeSpan.FromSeconds(5)
                    )
                    : ReconcileOutcome.FromFailure(readError);
            }

            if (!ValkeyCaches.Matches(read.GetValueOrThrow().Json, context.Desired)) {
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
    ///     Puts a <c>requirepass</c> in the vault if there is not one there, and reads back whichever
    ///     one is now authoritative.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE MINT AND THE READ ARE BOTH HERE, AND THE READ IS WHAT MAKES THE PASS
    ///         IDEMPOTENT.</b> <see cref="ValkeyCaches.GeneratePassword" /> produces a different value
    ///         every call — it has to, or a credential would be derivable from a resource id. What
    ///         reaches the rendered <c>Secret</c> is never that value: it is what
    ///         <see cref="ISecretResolver.ResolveAsync" /> returns afterwards, which is the password
    ///         the <i>first</i> pass minted, on every pass. So the rendered document is byte-stable
    ///         across passes and clause 1 holds over a generator that is not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>On this type that property is load-bearing rather than tidy.</b> Everywhere else a
    ///         re-minted credential produces a <c>listKeys</c> that hands out the wrong value;
    ///         here it would also <i>overwrite</i> the <c>Secret</c> a running Valkey read its
    ///         <c>requirepass</c> from at start-up, so every already-connected client keeps working,
    ///         every new one is rejected, and nothing in the platform reports anything but success.
    ///         <c>ISecretWriter.MintAsync</c>'s <c>cas=0</c> is the vault-side half of that and this
    ///         resolve is the reconciler-side half; neither alone is enough.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One field, so one mint and one resolve.</b> <c>ContainerRegistryReconciler</c>'s
    ///         remarks record that <c>cas=0</c> is per <i>path</i>, so a credential set has to be one
    ///         document; a cache has exactly one secret and the question does not arise.
    ///     </para>
    /// </remarks>
    static async Task<Result<string>> EnsurePasswordAsync(
        ReconcileContext context,
        CancellationToken cancellationToken
    ) {
        var minted = await context.SecretWriter.MintAsync(
            ValkeyCaches.SecretPath(context.Id),
            new Dictionary<string, string>(StringComparer.Ordinal) {
                [ValkeyCaches.PasswordField] = ValkeyCaches.GeneratePassword()
            },
            cancellationToken
        );

        if (minted.TryGetError(out var mintError)) {
            return Result<string>.Failure(mintError);
        }

        if (minted.GetValueOrThrow().Minted) {
            context.Log.Report(
                "minting",
                $"a new requirepass was written to the vault for '{context.Id.Name}'"
            );
        }

        return await context.Secrets.ResolveAsync(
            ValkeyCaches.PasswordRef(context.Id),
            cancellationToken
        );
    }

    /// <summary>
    ///     Applies one object and turns anything that did not land into the outcome that comes back
    ///     for it, or <see langword="null" /> when it landed.
    /// </summary>
    /// <param name="context">The pass, for its address, its api-version and its log.</param>
    /// <param name="cluster">The connection.</param>
    /// <param name="kind">The kind being applied.</param>
    /// <param name="objectJson">The rendered document.</param>
    /// <param name="what">The object, named in the message a tenant reads.</param>
    /// <param name="cancellationToken">Cancels the apply.</param>
    static async Task<ReconcileOutcome?> Apply(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        GroupVersionKind kind,
        string objectJson,
        string what,
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
            // ⚠ The code decides, not this call site — and this provider is where the reason was
            // written down. The comment here used to report that the CyberCloud.Kubernetes gap was
            // open: every 4xx other than a 409 escaped KubeApiClient.ApplyAsync as a raw
            // k8s.Autorest.HttpOperationException, which Orleans could not serialize, so a missing
            // RedisFailover CRD reached the operation as "CodecNotFoundException" with the status and
            // the API server's message nowhere in it. Found by running this provider's own
            // .Cluster.Conformance suite against a k3s with no such CRD. KubeFailures.Classify closed
            // it, so there is now a code to read, and ReconcileOutcome.FromFailure is what reads it:
            // the four refusals end the operation on this pass and everything else still comes back.
            return ReconcileOutcome.FromFailure(applyError);
        }

        var outcome = applied.GetValueOrThrow();

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

    /// <summary>Every object a cache owns, in apply order.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ Static, and rebuilt on every call, because a reconciler is one singleton across every
    ///     tenant in the process — clause 2. Memoising this into a <c>readonly</c> field is the shape
    ///     that gets past a structural no-hidden-state check and hands tenant B tenant A's namespace.
    /// </remarks>
    static ObjectRef[] Targets(string ns, string name) => [
        ValkeyCaches.CredentialSecretRef(ns, name),
        ValkeyCaches.FailoverRef(ns, name)
    ];

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

        context.Log.Report("deleting", $"deleting the RedisFailover '{name}'");

        // ⚠ THE REVERSE OF THE APPLY ORDER, AND IT IS THE SAME ARGUMENT. The CR goes first, so the
        // operator is still able to read the password it authenticates to the pods with while it tears
        // them down — spotahome's checker calls GetRedisPassword on every loop, and a Secret removed
        // underneath a live RedisFailover makes the controller error out mid-teardown against an object
        // it is trying to remove.
        //
        // ⚠ THE VAULT ENTRY IS NOT DELETED, AND THAT IS ISecretWriter's RULE RATHER THAN AN OVERSIGHT.
        // It mints and does not delete: a teardown that failed halfway and is retried must not be able
        // to hand the next pass a different credential from the one already rendered.
        foreach (var (kind, json, cascade) in new[] {
                     (
                         ValkeyCaches.FailoverKind,
                         ValkeyCaches.RedisFailoverJson(name, context.Desired),
                         // ⚠ Foreground rather than Background, and this is the one place this provider
                         // differs from the first on a platform default. The operator's StatefulSet,
                         // Deployment, Services and ConfigMaps are the RedisFailover's OWNED children,
                         // and `keepAfterDeletion` on the volume claim means the claim outlives them by
                         // design. A background cascade returns as soon as the CR is gone, so the
                         // teardown below would read "not found" while the pods were still running —
                         // and a resource that stops being billed while it is still serving traffic is
                         // the failure the read-back exists to prevent.
                         CascadePolicy.Foreground
                     ),
                     (
                         ValkeyCaches.SecretKind,
                         ValkeyCaches.CredentialSecretJson(name, Placeholder),
                         // ⚠ Background, because a Secret owns nothing. A Foreground cascade here would
                         // block on a dependent set that is always empty.
                         CascadePolicy.Background
                     )
                 }) {
            var deleted = await KubeCommand.For(cluster)
                .WithTenantId(context.Id.TenantId)
                .WithResourceId(context.Id)
                .InNamespace(context.Namespace)
                .WithKind(kind)
                .WithApiVersion(context.ApiVersion)
                .ObjectJson(json)
                .DeleteAsync(cascade, cancellationToken);

            if (deleted.TryGetError(out var deleteError)
                && deleteError.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(deleteError);
            }
        }

        // ⚠ Converged once the objects are GONE, read back — not once the deletes were issued.
        foreach (var target in Targets(context.Namespace, name)) {
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

        // ⚠ AND THE DATA DIRECTORIES SURVIVE THIS, WHICH IS WHAT `keepAfterDeletion` ABOVE ASKED FOR
        // AND WHAT NOTHING USED TO FINISH. The flag makes the operator withhold the owner reference
        // it would otherwise write onto the claim, so the claim outlives the RedisFailover it was
        // rendered under; this type declares no recovery window, so nothing was ever coming back for
        // it. RetainedVolumesAsync below names those claims and VolumeReclaimer removes them, on the
        // convergence of the hard delete and on nothing else.
        context.Log.Report("deleted", $"the objects of '{name}' are gone", 100);

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One claim per Valkey replica, named the OPERATOR's way rather than this
    ///         platform's</b> — see <see cref="ValkeyCaches.RetainedClaims" />. This provider renders
    ///         a custom resource and no <c>StatefulSet</c>, so the set whose
    ///         <c>volumeClaimTemplate</c> made the claims is spotahome's; its name and its selector
    ///         labels are read out of the operator's source and are recorded there with the reason
    ///         that is safe here and would not be elsewhere.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>ProviderConformanceTests</c> cannot cover this family and skips it with a
    ///         reason.</b> Its retained-volume case plants claims read out of the
    ///         <c>volumeClaimTemplates</c> of the documents a provider <i>applied</i>, and the
    ///         document this provider applies is a <c>RedisFailover</c> — the templates exist only
    ///         after a controller this platform does not run has expanded it. So the naming is
    ///         asserted in <c>ValkeyReconcilerTests</c> instead, against the operator convention
    ///         written down beside it.
    ///     </para>
    /// </remarks>
    public Task<Result<ImmutableArray<RetainedVolume>>> RetainedVolumesAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            Result<ImmutableArray<RetainedVolume>>.Success(
                ValkeyCaches.RetainedClaims(context.Namespace, context.Id.Name, context.Desired)
            )
        );

    /// <summary>A non-empty stand-in for the <c>requirepass</c> on the delete path.</summary>
    /// <remarks>
    ///     ⚠ <b>A delete addresses an object; it does not need the object's contents.</b>
    ///     <c>KubeCommand</c> takes a body on every path, and reaching into the vault to fill one in
    ///     would make a teardown fail for a cache whose vault entry is already unreachable — which is
    ///     exactly the cache most likely to be being torn down. The value is never sent: the builder
    ///     addresses by kind, namespace and name.
    /// </remarks>
    const string Placeholder = "deleting";

    /// <inheritdoc />
    public async Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ObservedState.Absent;
        }

        // ⚠ THE RedisFailover IS THE OBSERVATION, because it is applied LAST — so a cache whose CR is
        // present is one whose credential was applied on some pass. Observing the Secret instead would
        // report a half-built cache as existing.
        var read = await cluster.GetAsync(
            ValkeyCaches.FailoverRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        if (read.TryGetError(out _)) {
            return new() {
                Exists = false, ObservedAt = clock.UtcNow, Summary = "the RedisFailover is absent"
            };
        }

        var found = read.GetValueOrThrow();
        var matches = ValkeyCaches.Matches(found.Json, context.Desired);

        // ⚠ THE CREDENTIAL IS OBSERVED TOO, AND ITS ABSENCE IS DRIFT RATHER THAN ABSENCE. A cache
        // whose Secret was deleted out from under it still exists and is still billed, and the next
        // pod the operator restarts comes up unable to read a requirepass — which is the state drift
        // detection is for, and the exact failure this type shipped with before the reconciler minted.
        var credential = await cluster.GetAsync(
            ValkeyCaches.CredentialSecretRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        var authenticated = credential.IsSuccess
            && ValkeyCaches.Matches(credential.GetValueOrThrow().Json, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = (matches, authenticated) switch {
                (true, true) => "the RedisFailover carries the desired spec",
                (true, false) => "the RedisFailover carries the desired spec and its requirepass "
                    + "Secret is missing, so the operator cannot start a pod",
                _ => "the RedisFailover has drifted"
            }
        };
    }
}
