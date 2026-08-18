// ⚠ For `Result<string>`, which EnsureIngestKeyAsync returns. `CyberCloud.Core.Resources` is global
// here and `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins over the
// `Orleans.ErrorCode` this import would otherwise put back in play.
using CyberCloud.Core;
using CyberCloud.Core.Time;
using System.Globalization;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor;

/// <summary>
///     Converges one monitor workspace onto the three objects its tenancy is: an ingest key, a
///     routing rule, and the row that announces it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>NOTHING HERE PROVISIONS A STORE, AND THAT IS THE POINT OF THE TYPE.</b> The metrics,
///         logs and traces stores are per-region platform components installed by the platform
///         bundle; a workspace is a tenancy in them. See <see cref="MonitorWorkspaces" /> for the
///         five-part argument and for why the alternative is a dependency cycle through
///         <c>CyberCloud.Ingest.Host</c>.
///     </para>
///     <para>
///         ⚠ <b>THREE OBJECTS, AND THE ORDER THEY ARE APPLIED IN IS PART OF THE DESIGN.</b> The
///         <c>Secret</c> first, because the <c>VMUser</c> names it in <c>passwordRef</c> and vmauth
///         resolving a missing secret is a user that authenticates nothing. The <c>VMUser</c> next,
///         because it is what makes the workspace <i>reachable</i>. The <c>ConfigMap</c> row
///         <b>last</b>, because it is what makes the workspace <i>announced</i> — the ingest host
///         reads it to learn a workspace exists, and a row that appears before the key and the
///         routing it names is a workspace advertised as ready and refusing every write.
///     </para>
///     <para>
///         ⚠ <b>A RETENTION A TENANT CAN SHORTEN IS A DATA-LOSS PATH, AND IT IS REFUSED HERE
///         BECAUSE IT CANNOT BE REFUSED AT THE API.</b> docs/plan/16 § Cost and retention honesty
///         prices retention, so it has to be settable; shortening it deletes everything already
///         outside the new window, and on the ClickHouse half that is not a slow drift — expiry runs
///         at the next merge, and ClickHouse performs an off-schedule merge when it detects expired
///         data. <b>The API cannot refuse it</b>: <c>ResourceSchema</c> validates one body against
///         constants and has no access to the previous body, and there is no provider-supplied
///         predicate anywhere on <c>ResourceManagerService</c>'s write path —
///         <c>IResourceTypeBuilder</c> declares eleven things and none of them is a validator.
///         ⚠ <b>Fifth sighting of that limit</b> after <c>CyberCloud.Network/virtualNetworks</c>'
///         address-space overlap, <c>CyberCloud.ContainerService</c>'s version skew,
///         <c>CyberCloud.Storage/accounts</c>' bucket cluster and <c>clickhouseClusters</c>' volume
///         shrink — and the first where the consequence is <b>irreversible destruction of tenant
///         data authorised by a request the platform already answered <c>202</c> to</b>. So this
///         reconciler reads the existing row before it applies anything and fails the pass by name,
///         with both day counts in the message, rather than converging onto a smaller window. The
///         resource parks in a failed state that a <c>PUT</c> of the old tier reverses; the data is
///         still there. <c>conformance.yaml § owed</c>, <c>retention-shrink-is-refused-after-202</c>.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop, and where each is satisfied:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> All three documents are pure functions of the address and the
///             body — including the <c>Secret</c>, because the key that reaches it is the one
///             <c>ISecretResolver</c> returns rather than the one this pass minted. Nothing counts,
///             appends or timestamps.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />, which is a dependency rather than a memory. ⚠ A reconciler is
///             registered <b>as a singleton, by concrete type</b>, so one instance serves every
///             tenant in the process — and a <c>readonly</c> field holding a mutable dictionary is
///             the shape that gets past a structural check, because the field never reassigns.
///             <c>MonitorReconcilerTests</c> asserts both halves.
///         </item>
///         <item>
///             <b>Bounded.</b> One vault round trip, three applies and four reads, on the caller's
///             token.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c> of <b>all three</b> objects, never any apply's own result.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>One of the three kinds is one a cluster may not serve.</b>
///         <c>operator.victoriametrics.com/v1beta1</c> is installed by the platform bundle rather
///         than by Kubernetes, and a cluster without it answers the <c>VMUser</c> apply with a
///         <c>404</c> naming the group. The other two are core and are served everywhere.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class MonitorWorkspaceReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => MonitorWorkspaces.Type;

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
                $"'{context.Id.Path}' has no cluster connection, and a monitor workspace publishes "
                + "its tenancy into the region's cluster. CyberCloud.Monitor/workspaces declares "
                + "RequiresCluster, so the driver should have refused this pass — see ReconcileDriver."
            );
        }

        var name = context.Id.Name;

        // ── The shrink check, BEFORE anything is applied. See the remarks. ────────────────────
        if (await ShrinkAsync(context, cluster, cancellationToken) is { } refusal) {
            return refusal;
        }

        // ── The credential ───────────────────────────────────────────────────────────────────
        var credential = await EnsureIngestKeyAsync(context, cancellationToken);

        if (credential.TryGetError(out var credentialError)) {
            return ReconcileOutcome.FromFailure(credentialError);
        }

        context.Log.Report("applying-key", $"applying the ingest key of '{name}'", 20);

        if (await Apply(
                context,
                cluster,
                MonitorWorkspaces.SecretKind,
                MonitorWorkspaces.KeySecretJson(name, credential.GetValueOrThrow()),
                cancellationToken
            ) is { } keyProblem) {
            return keyProblem;
        }

        context.Log.Report("applying-routing", $"applying the metrics routing of '{name}'", 45);

        if (await Apply(
                context,
                cluster,
                MonitorWorkspaces.VmUserKind,
                MonitorWorkspaces.VmUserJson(context.Id, context.Desired),
                cancellationToken
            ) is { } routingProblem) {
            return routingProblem;
        }

        context.Log.Report("announcing", $"announcing '{name}' to the region's ingest", 70);

        if (await Apply(
                context,
                cluster,
                MonitorWorkspaces.ConfigMapKind,
                MonitorWorkspaces.RowJson(context.Id, context.Desired),
                cancellationToken
            ) is { } rowProblem) {
            return rowProblem;
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ─────────────
        //
        // ⚠ ALL THREE. A read-back of the row alone would report Converged for a workspace whose
        // VMUser apply was silently swallowed — which is a workspace the ingest host believes in and
        // vmauth refuses every write to.
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

            if (!MonitorWorkspaces.Matches(read.GetValueOrThrow().Json, context.Id, context.Desired)) {
                return ReconcileOutcome.InProgress(
                    $"'{target}' is readable and does not yet carry the desired tenancy",
                    TimeSpan.FromSeconds(5)
                );
            }
        }

        context.Log.Report("ready", $"all three objects of '{name}' read back as desired", 100);

        return ReconcileOutcome.Converged;
    }

    /// <summary>
    ///     Refuses the pass when the desired body shortens a retention the workspace already has.
    /// </summary>
    /// <returns>The outcome that ends the pass, or <see langword="null" /> to carry on.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>It reads the CLUSTER rather than <see cref="ReconcileContext.Observed" />.</b>
    ///         That property is <i>"what the last observation saw"</i> and its own remarks warn it is
    ///         not a substitute for observing. A refusal that destroys nothing has to be made against
    ///         what is actually there, and a stale reading would let a shrink through on the one pass
    ///         where the cache had not caught up.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The row's absence is a create and is allowed.</b> Every first pass has no
    ///         previous window to shorten, and a check that treated a missing row as retention zero
    ///         would refuse every create.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It compares DAYS and not tier names.</b> The tiers are ordered today and there is
    ///         nothing that keeps them ordered — a fourth tier inserted in the middle of
    ///         <see cref="MonitorWorkspaces.Tiers" /> would silently reverse a comparison on
    ///         position. The day count is the thing that decides whether data is deleted, so it is
    ///         the thing that is compared.
    ///     </para>
    /// </remarks>
    static async Task<ReconcileOutcome?> ShrinkAsync(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        CancellationToken cancellationToken
    ) {
        var existing = await cluster.GetAsync(
            MonitorWorkspaces.RowRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        if (existing.TryGetError(out var error)) {
            return error.Code == ErrorCode.ResourceNotFound
                ? null
                : ReconcileOutcome.FromFailure(error);
        }

        if (JsonNode.Parse(existing.GetValueOrThrow().Json) is not JsonObject document
            || document["data"] is not JsonObject data) {
            return null;
        }

        foreach (var signal in MonitorWorkspaces.Signals) {
            var key = "retention"
                + char.ToUpperInvariant(signal[0])
                + signal[1..]
                + "Days";

            if (data[key]?.GetValue<string>() is not { } text
                || !int.TryParse(text, CultureInfo.InvariantCulture, out var already)) {
                continue;
            }

            var wanted = MonitorWorkspaces.Days(context.Desired, signal);

            if (wanted >= already) {
                continue;
            }

            return ReconcileOutcome.Failed(
                ErrorCode.InvalidRequestBody,
                $"'{context.Id.Path}' keeps {signal} for {already} days and the request asks for "
                + $"{wanted}. Shortening a retention destroys every {signal} record already outside "
                + "the shorter window, permanently and as soon as the store next compacts, so the "
                + "change is refused rather than applied. Nothing has been deleted and nothing else "
                + "in the request has been applied. To lengthen a retention, or to leave it alone, "
                + "resubmit with a tier of at least the current one; to genuinely discard the data, "
                + "delete the workspace, which is reversible for "
                + $"{MonitorProvider.SoftDeleteDays} days."
            );
        }

        return null;
    }

    /// <summary>
    ///     Puts an ingest key in the vault if there is not one there, and reads back whichever key is
    ///     now authoritative.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>THE MINT AND THE READ ARE BOTH HERE, AND THE READ IS WHAT MAKES THE PASS
    ///     IDEMPOTENT.</b> <see cref="MonitorWorkspaces.GenerateIngestKey" /> produces a different
    ///     value every call — it has to, or a credential would be derivable from a resource id. What
    ///     reaches the rendered <c>Secret</c> is never that value: it is what
    ///     <see cref="ISecretResolver.ResolveAsync" /> returns afterwards, which is the key the
    ///     <i>first</i> pass minted, on every pass. So the rendered document is byte-stable across
    ///     passes and clause 1 holds over a generator that is not.
    /// </remarks>
    static async Task<Result<string>> EnsureIngestKeyAsync(
        ReconcileContext context,
        CancellationToken cancellationToken
    ) {
        var minted = await context.SecretWriter.MintAsync(
            MonitorWorkspaces.SecretPath(context.Id),
            new Dictionary<string, string>(StringComparer.Ordinal) {
                [MonitorWorkspaces.IngestKeyField] = MonitorWorkspaces.GenerateIngestKey()
            },
            cancellationToken
        );

        if (minted.TryGetError(out var mintError)) {
            return Result<string>.Failure(mintError);
        }

        if (minted.GetValueOrThrow().Minted) {
            context.Log.Report(
                "minting",
                $"a new ingest key was written to the vault for '{context.Id.Name}'"
            );
        }

        return await context.Secrets.ResolveAsync(
            MonitorWorkspaces.IngestKeyRef(context.Id),
            cancellationToken
        );
    }

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

        context.Log.Report("deleting", $"withdrawing the tenancy of '{name}'");

        // ⚠ THE REVERSE OF THE APPLY ORDER, AND IT IS THE SAME ARGUMENT. The row goes first, so the
        // ingest host stops believing in the workspace before its key and routing disappear
        // underneath it — the alternative is a window in which a live row points at a VMUser that is
        // gone, which is a tenant's collector getting authentication failures instead of a clean
        // "this workspace no longer exists".
        //
        // ⚠ THE VAULT ENTRY IS NOT DELETED, AND THAT IS ISecretWriter's RULE RATHER THAN AN
        // OVERSIGHT. It mints and does not delete: a teardown that failed halfway and is retried
        // must not be able to hand the next pass a different credential from the one already
        // rendered.
        foreach (var (kind, json) in new[] {
                     (MonitorWorkspaces.ConfigMapKind, MonitorWorkspaces.RowJson(context.Id, context.Desired)),
                     (MonitorWorkspaces.VmUserKind, MonitorWorkspaces.VmUserJson(context.Id, context.Desired)),
                     (MonitorWorkspaces.SecretKind, MonitorWorkspaces.KeySecretJson(name, Placeholder))
                 }) {
            var deleted = await KubeCommand.For(cluster)
                .WithTenantId(context.Id.TenantId)
                .WithResourceId(context.Id)
                .InNamespace(context.Namespace)
                .WithKind(kind)
                .WithApiVersion(context.ApiVersion)
                .ObjectJson(json)
                // ⚠ Background. Nothing this type applies owns anything, so a Foreground cascade
                // would block on a dependent set that is always empty. The read-back below is what
                // makes it safe: this returns Converged when the objects are GONE.
                .DeleteAsync(CascadePolicy.Background, cancellationToken);

            if (deleted.TryGetError(out var deleteError)
                && deleteError.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(deleteError);
            }
        }

        foreach (var target in Targets(context.Namespace, name).Reverse()) {
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

        context.Log.Report("deleted", $"the tenancy of '{name}' is withdrawn", 100);

        return ReconcileOutcome.Converged;
    }

    /// <summary>
    ///     A non-empty stand-in for the ingest key on the delete path.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A delete addresses an object; it does not need the object's contents.</b>
    ///     <c>KubeCommand</c> takes a body on every path, and reaching into the vault to fill one in
    ///     would make a teardown fail for a workspace whose vault entry is already gone — which is
    ///     exactly the workspace most likely to be being torn down. The value is never sent: the
    ///     builder addresses by kind, namespace and name.
    /// </remarks>
    const string Placeholder = "deleting";

    /// <summary>Every object a workspace owns, in apply order.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ Static, like <c>StorageAccounts</c>' equivalent, because a reconciler is one singleton
    ///     across every tenant in the process — clause 2. Memoising this into a <c>readonly</c>
    ///     dictionary is the shape that used to get past <c>CheckNoHiddenState</c>; it no longer does.
    /// </remarks>
    static ObjectRef[] Targets(string ns, string name) => [
        MonitorWorkspaces.KeySecretRef(ns, name),
        MonitorWorkspaces.VmUserRef(ns, name),
        MonitorWorkspaces.RowRef(ns, name)
    ];

    /// <inheritdoc />
    public async Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ObservedState.Absent;
        }

        // ⚠ THE ROW IS THE OBSERVATION, because it is applied LAST — so a workspace whose row is
        // present is one whose key and routing were both applied on some pass. Observing the Secret
        // instead would report a half-built workspace as existing.
        var row = await cluster.GetAsync(
            MonitorWorkspaces.RowRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        if (row.TryGetError(out _)) {
            return new() {
                Exists = false,
                ObservedAt = clock.UtcNow,
                Summary = "the workspace's ingest row is absent"
            };
        }

        var found = row.GetValueOrThrow();

        // ⚠ THE ROUTING IS OBSERVED TOO, AND ITS ABSENCE IS DRIFT RATHER THAN ABSENCE. A workspace
        // whose VMUser was deleted out from under it still exists, is still billed, still holds
        // everything it has ever received, and has quietly stopped accepting metrics — which is the
        // state drift detection is for.
        var routing = await cluster.GetAsync(
            MonitorWorkspaces.VmUserRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        var matches = MonitorWorkspaces.Matches(found.Json, context.Id, context.Desired)
            && routing.IsSuccess
            && MonitorWorkspaces.Matches(routing.GetValueOrThrow().Json, context.Id, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches
                ? "the workspace's tenancy and its metrics routing are as declared"
                : "the workspace's tenancy has drifted"
        };
    }

    /// <summary>
    ///     Applies one object, returning the outcome that ends the pass or <see langword="null" /> to
    ///     carry on.
    /// </summary>
    /// <remarks>
    ///     ⚠ Shared by all three applies rather than written three times, because the branches below
    ///     are a policy — retryable, refused, owned by somebody else — and three copies of a policy
    ///     is two that get missed when it changes.
    /// </remarks>
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
            // ⚠ The code decides, not this call site. An apply that could not reach the cluster is a
            // request that can be made again; one the API server refused — an admission policy, a
            // VictoriaMetrics CRD the bundle never installed, our own credentials — will be refused
            // identically for the next hour, and ReconcileOutcome.FromFailure is where the four
            // codes that mean that are listed.
            return ReconcileOutcome.FromFailure(applyError);
        }

        var outcome = applied.GetValueOrThrow();

        return outcome.Result switch {
            // docs/plan/09 § Cluster connections: an unreachable cluster suspends reconciles rather
            // than failing them. A tenant whose region is unreachable has a workspace that is still
            // coming, not one that broke.
            ApplyResult.Suspended => Suspended(context, outcome),
            ApplyResult.Conflict => Conflicted(context, kind, outcome),
            _ => null
        };
    }

    static ReconcileOutcome Suspended(ReconcileContext context, ApplyOutcome outcome) {
        context.Log.Report("waiting-for-cluster", outcome.Message);

        return ReconcileOutcome.InProgress(
            outcome.Message.Length > 0 ? outcome.Message : "the cluster is unreachable",
            TimeSpan.FromSeconds(30)
        );
    }

    static ReconcileOutcome Conflicted(
        ReconcileContext context,
        GroupVersionKind kind,
        ApplyOutcome outcome
    ) {
        var describe = outcome.Drift?.Describe()
            ?? $"another field manager owns part of the {kind.Kind} and it was not overwritten";

        context.Log.Report("conflict", describe);

        return ReconcileOutcome.InProgress(describe, TimeSpan.FromSeconds(30));
    }
}
