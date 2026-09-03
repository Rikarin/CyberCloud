// ⚠ For `Result<T>` on the credential path. See StorageProvider for why this import is safe beside
// the ErrorCode alias.
using CyberCloud.Core;
using CyberCloud.Core.Time;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Storage;

/// <summary>
///     Converges one object-storage account onto the single <c>Seaweed</c> custom resource it is.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>TWO OBJECTS, AND THE SECOND ONE IS THE CREDENTIAL.</b> A <c>Seaweed</c> expands into
///         master, volume, filer and S3 Deployments/StatefulSets, their Services, and four
///         <c>ServiceMonitor</c>s when monitoring is on. None of those is applied here, and writing
///         any of them would be this provider competing with the controller that owns them — the rule
///         <c>charts/managed/valkey</c> states. What <i>is</i> applied beside the CR is the identities
///         <c>Secret</c> its S3 gateway mounts, because nothing else can write it: it is built from a
///         value that lives in the vault and reaches a manifest for the length of one pass. The
///         consequence worth having written down: object <i>count</i> is not a measure of a service's
///         size, which the NATS row's five-objects-for-no-operator and this row's
///         two-objects-for-everything still bracket from both ends.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop, and where each is satisfied:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> The apply is server-side and
///             <see cref="StorageAccounts.SeaweedJson" /> is a pure function of the name and the body.
///             Nothing here counts, appends or timestamps. ⚠ <b>The credential path looks like a
///             violation and is not:</b> <see cref="StorageAccounts.GenerateKeyPair" /> returns a
///             different pair every call, and every pass after the first has that candidate discarded
///             by mint-once and renders what it <i>resolved back</i> instead. So the rendered
///             <c>Secret</c> is byte-stable across passes over a generator that is not, and
///             <c>ASecondPassWithTheSameBodyChangesNothing</c> is what says so.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />, which is a dependency rather than a memory. ⚠ A reconciler is
///             registered <b>as a singleton, by concrete type</b> (<c>AddCyberCloudProvider</c>'s
///             remarks), so one instance serves every tenant in the process — and a <c>readonly</c>
///             field holding a mutable dictionary is the shape that gets past a structural check,
///             because the field never reassigns. <c>StorageReconcilerTests</c> asserts both halves.
///         </item>
///         <item>
///             <b>Bounded.</b> One apply and one read, on the caller's token. ⚠ There is no wait for
///             the cluster to be <i>ready</i> — a master Raft group plus volume registration takes
///             minutes and clause 3's budget is thirty seconds, so readiness is reported as
///             <see cref="ReconcileOutcome.InProgress" /> and the reminder comes back. ⚠ <b>Two applies
///             and two reads now, plus a mint and two resolves</b> — the vault round trips are the new
///             cost, and <c>OpenBaoSecretResolver</c> caches nothing on purpose, for reasons its own
///             remarks set out at length.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c> of the object, never the apply's own result.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>The <c>Seaweed</c> kind is one a cluster may not serve, and the failure now names
///         itself.</b> <c>seaweed.seaweedfs.com/v1</c> is installed by the platform bundle rather than
///         by Kubernetes, and a cluster without it answers the apply with a <c>404</c>. Until
///         2026-08-12 an unmapped 4xx escaped as <c>k8s.Autorest.HttpOperationException</c> with no
///         status code and Orleans reported <c>CodecNotFoundException</c>; it comes back naming the
///         API server's own message now, so the operator of a cluster missing the bundle finds out
///         what is missing.
///     </para>
///     <para>
///         ⚠ <b>An <see cref="ApplyResult.Conflict" /> is reported and retried rather than failed and
///         never forced.</b> ADR-013 makes a conflict <i>"a drift event with a name"</i>; forcing
///         would let the platform silently overwrite the operator, which writes <c>.status</c> on this
///         object and — the case that matters — could plausibly be given ownership of
///         <c>spec.volume.replicas</c> by a tenant's own autoscaler.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class StorageAccountReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => StorageAccounts.Type;

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
                $"'{context.Id.Path}' has no cluster connection, and an object-storage account is a "
                + "Seaweed custom resource in a cluster. CyberCloud.Storage/accounts declares "
                + "RequiresCluster, so the driver should have refused this pass — see ReconcileDriver."
            );
        }

        var name = context.Id.Name;

        // ── The credential, before anything that consumes it ────────────────────────────────────
        //
        // ⚠ THE VAULT GOES FIRST, AND THE ORDER IS CHOSEN BY WHICH FAILURE IS SURVIVABLE.
        //
        // Two orders, two partial failures:
        //
        //   • Mint, then the cluster fails → an orphaned secret at this resource's vault path. It is
        //     INERT: nothing runs, nothing is reachable, nothing is billed, and the next pass finds
        //     it and uses it, because mint-once means the retry converges on the same pair rather
        //     than a second one. The tenant ends up with exactly the credential they were always
        //     going to get.
        //   • The cluster, then the mint fails → a Seaweed applied with `spec.s3.configSecret`
        //     pointing at a Secret that does not exist. On this operator that is survivable only by
        //     accident (the gateway pod stays in ContainerCreating); the moment anything renders an
        //     EMPTY identities file instead, `weed/s3api/auth_credentials.go` sets
        //     `isAuthEnabled = len(identities) > 0` and AuthenticateRequest answers every
        //     unauthenticated caller as ACTION_ADMIN. An S3 endpoint open to the cluster network.
        //
        // So the credential exists before the thing that authenticates against it exists. A leaked KV
        // entry is a housekeeping problem; a running server nobody has to authenticate to is an
        // incident.
        context.Log.Report("minting", $"ensuring the S3 key pair of '{name}' is in the vault", 10);

        var credential = await EnsureCredentialAsync(context, cancellationToken);

        if (credential.TryGetError(out var credentialError)) {
            // ⚠ Retryable, and nothing has been applied. A vault that is sealed, unreachable or not
            // wired is a resource that has not started rather than one that failed — and the sixty
            // minute ceiling in ReconcileSchedule is what eventually turns it into the actionable
            // failure an operator can read.
            return ReconcileOutcome.FromFailure(credentialError);
        }

        var (accessKeyId, secretAccessKey) = credential.GetValueOrThrow();

        context.Log.Report(
            "applying",
            $"applying the identities Secret of '{name}' to {context.Namespace}",
            20
        );

        var secret = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(StorageAccounts.SecretKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(StorageAccounts.ConfigSecretJson(name, accessKeyId, secretAccessKey))
            .ApplyAsync(cancellationToken);

        if (secret.TryGetError(out var secretError)) {
            return ReconcileOutcome.FromFailure(secretError);
        }

        if (Unfinished(context, secret.GetValueOrThrow(), "the identities Secret") is { } waiting) {
            return waiting;
        }

        context.Log.Report("applying", $"applying the Seaweed of '{name}' to {context.Namespace}", 40);

        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(StorageAccounts.SeaweedKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(StorageAccounts.SeaweedJson(name, context.Desired))
            .ApplyAsync(cancellationToken);

        if (applied.TryGetError(out var applyError)) {
            // ⚠ The code decides, not this call site. An apply that could not reach the cluster is a
            // request that can be made again; one the API server refused — an admission policy, a
            // Seaweed CRD the bundle never installed, our own credentials — will be refused
            // identically for the next hour, and ReconcileOutcome.FromFailure is where the four codes
            // that mean that are listed.
            return ReconcileOutcome.FromFailure(applyError);
        }

        if (Unfinished(context, applied.GetValueOrThrow(), "the Seaweed") is { } stalled) {
            return stalled;
        }

        // ── Clause 4. Everything above this line is a claim; these are the readings. ────────────
        //
        // ⚠ BOTH OBJECTS, BECAUSE BOTH WERE APPLIED. StorageReconcilerTests' own
        // EveryAppliedObjectIsAlsoReadBack asserts the two sets are equal in both directions, and the
        // reason it does is this provider: a Secret applied and never read back is a credential the
        // platform believes it wrote.
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

            if (!StorageAccounts.Matches(read.GetValueOrThrow().Json, context.Desired)) {
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
    ///     Puts an S3 key pair in the vault if there is not one there, and reads back whichever pair
    ///     is now authoritative.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE MINT AND THE READ ARE BOTH HERE, AND THE READ IS WHAT MAKES THE PASS
    ///         IDEMPOTENT.</b> <see cref="StorageAccounts.GenerateKeyPair" /> produces a different pair
    ///         every call — it has to, or a credential would be derivable from a resource id. What
    ///         reaches a manifest is never that pair: it is what
    ///         <see cref="ISecretResolver.ResolveAsync" /> returns afterwards, which is the pair the
    ///         <i>first</i> pass minted, on every pass. So the rendered <c>Secret</c> is byte-stable
    ///         across passes and clause 1 holds over a generator that is not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two resolves rather than one, because the two halves are two fields.</b> A
    ///         <c>SecretRef</c> addresses one field by design — see its own remarks — so the
    ///         pair costs two round trips per pass. Worth it against the alternative, which is a
    ///         resolver that returns documents and therefore a resolver that can hand back a value
    ///         nobody asked for.
    ///     </para>
    /// </remarks>
    static async Task<Result<(string AccessKeyId, string SecretAccessKey)>> EnsureCredentialAsync(
        ReconcileContext context,
        CancellationToken cancellationToken
    ) {
        var candidate = StorageAccounts.GenerateKeyPair();

        var minted = await context.SecretWriter.MintAsync(
            StorageAccounts.SecretPath(context.Id),
            new Dictionary<string, string>(StringComparer.Ordinal) {
                [StorageAccounts.AccessKeyIdField] = candidate.AccessKeyId,
                [StorageAccounts.SecretAccessKeyField] = candidate.SecretAccessKey
            },
            cancellationToken
        );

        if (minted.TryGetError(out var mintError)) {
            return Result<(string, string)>.Failure(mintError);
        }

        if (minted.GetValueOrThrow().Minted) {
            context.Log.Report(
                "minting",
                $"a new S3 key pair was written to the vault for '{context.Id.Name}'"
            );
        }

        var accessKeyId = await context.Secrets.ResolveAsync(
            StorageAccounts.AccessKeyIdRef(context.Id),
            cancellationToken
        );

        if (accessKeyId.TryGetError(out var accessKeyError)) {
            return Result<(string, string)>.Failure(accessKeyError);
        }

        var secretAccessKey = await context.Secrets.ResolveAsync(
            StorageAccounts.SecretAccessKeyRef(context.Id),
            cancellationToken
        );

        return secretAccessKey.TryGetError(out var secretKeyError)
            ? Result<(string, string)>.Failure(secretKeyError)
            : Result<(string, string)>.Success(
                (accessKeyId.GetValueOrThrow(), secretAccessKey.GetValueOrThrow())
            );
    }

    /// <summary>
    ///     Turns an apply that did not land into the outcome that comes back for it, or
    ///     <see langword="null" /> when it landed.
    /// </summary>
    /// <param name="context">The pass, for its log.</param>
    /// <param name="outcome">What the apply reported.</param>
    /// <param name="what">The object, named in the message a tenant reads.</param>
    static ReconcileOutcome? Unfinished(ReconcileContext context, ApplyOutcome outcome, string what) {
        switch (outcome.Result) {
            case ApplyResult.Suspended:
                // docs/plan/09 § Cluster connections: an unreachable cluster suspends reconciles
                // rather than failing them. A tenant whose cluster is down has a resource that is
                // still coming, not one that broke.
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

    /// <summary>Every object an account owns, in apply order.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ Static, like <c>NatsClusters</c>' equivalent, because a reconciler is one singleton across
    ///     every tenant in the process — clause 2, and the blind spot <c>CheckNoHiddenState</c> has
    ///     around a <c>readonly</c> field holding a mutable collection.
    /// </remarks>
    static ObjectRef[] Targets(string ns, string name) => [
        StorageAccounts.ConfigSecretRef(ns, name),
        StorageAccounts.SeaweedRef(ns, name)
    ];

    /// <inheritdoc />
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            // ⚠ Converged, not Failed, and the asymmetry with ReconcileAsync is deliberate — a
            // teardown with no cluster to reach has nothing left to remove, and failing would park
            // the resource in Deleting: visible, billed and permanent, for a wiring reason.
            return ReconcileOutcome.Converged;
        }

        var name = context.Id.Name;

        context.Log.Report("deleting", $"deleting the objects of '{name}'");

        // ⚠ THE SEAWEED FIRST AND THE IDENTITIES SECOND, WHICH IS THE APPLY ORDER REVERSED AND IS
        // REVERSED FOR THE SAME REASON. Removing the identities file from under a gateway that is
        // still serving would restart it with no identities — and a SeaweedFS gateway with no
        // identities authenticates nobody and authorises everybody. So the thing that consumes the
        // credential goes before the credential, and a teardown interrupted between them leaves a
        // Secret nobody mounts rather than a window where the endpoint is open.
        foreach (var target in Targets(context.Namespace, name).Reverse()) {
            var deleted = await KubeCommand.For(cluster)
                .WithTenantId(context.Id.TenantId)
                .WithResourceId(context.Id)
                .InNamespace(context.Namespace)
                .WithKind(target.Kind)
                .WithApiVersion(context.ApiVersion)
                .ObjectJson(Placeholder(target.Name))
                // ⚠ Background, not Foreground. A Foreground cascade blocks the delete on the garbage
                // collector removing every dependent — and on this type the dependents are four
                // workloads, their pods and every volume-server PVC — while a converge loop with a
                // bounded PASS budget runs out of passes waiting for a controller it does not drive.
                // The read-back below is what makes Background safe: this returns Converged when the
                // objects are GONE, not when the deletes were issued.
                .DeleteAsync(CascadePolicy.Background, cancellationToken);

            if (deleted.TryGetError(out var deleteError)
                && deleteError.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(deleteError);
            }
        }

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

        // ⚠ THE VAULT ENTRY IS NOT REMOVED, AND SAYING SO IS BETTER THAN A LINE THAT PRETENDS
        // OTHERWISE. ISecretWriter mints and does not delete: a teardown that failed halfway and is
        // re-driven from a reminder would, with a delete, race its own retry — the retry mints a
        // second pair while the first teardown is still tearing down the gateway that trusts the
        // first. What is left behind is one KV document per deleted account, inert, addressed by a
        // GUID no resource carries any more. Sweeping it belongs to a vault lifecycle job
        // (docs/plan/18 § Rotation is the same machinery) rather than to a reconcile pass.
        // ⚠ AND THE OBJECTS THEMSELVES SURVIVE THIS, WHICH IS WHAT MAKES THE SEVEN-DAY WINDOW REAL.
        // The Seaweed CR going away cascades to the two StatefulSets and stops there: the operator
        // pins its claim retention policy's `whenDeleted` to Retain as a CONSTANT
        // (internal/controller/pv_reclaim.go), writes no owner reference onto a claim template, and
        // registers no finalizer. So the volume servers' disks and the filer's metadata store are all
        // still there — and until RetainedVolumesAsync below, so were they after a PURGE.
        context.Log.Report("deleted", $"the objects of '{name}' are gone", 100);
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>One claim per volume server plus the filer's, named the OPERATOR's way</b> — see
    ///     <see cref="StorageAccounts.RetainedClaims" />, which carries the reading of the operator's
    ///     source, the reason the claims survive a teardown at all, and the version coupling that
    ///     naming another project's objects creates. This runs on the convergence of a hard delete
    ///     and of a purge and on nothing else, so a soft delete's claims — the ones a restore hands
    ///     back — are never reached.
    /// </remarks>
    public Task<Result<ImmutableArray<RetainedVolume>>> RetainedVolumesAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            Result<ImmutableArray<RetainedVolume>>.Success(
                StorageAccounts.RetainedClaims(context.Namespace, context.Id.Name, context.Desired)
            )
        );

    /// <summary>
    ///     The smallest object a delete command will accept.
    /// </summary>
    /// <remarks>
    ///     ⚠ A name and nothing else, because a delete addresses rather than describes. The apply path
    ///     renders the real body; rendering it here would mean the identities <c>Secret</c>'s
    ///     <b>credential</b> had to be resolved from the vault to delete it, which is a read of a
    ///     secret in order to throw it away.
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

        var read = await cluster.GetAsync(
            StorageAccounts.SeaweedRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        if (read.TryGetError(out _)) {
            return new() {
                Exists = false, ObservedAt = clock.UtcNow, Summary = "the Seaweed cluster is absent"
            };
        }

        var found = read.GetValueOrThrow();
        var matches = StorageAccounts.Matches(found.Json, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches
                ? "the Seaweed cluster carries the desired spec"
                : "the Seaweed cluster has drifted"
        };
    }
}
