// ⚠ For `Result<decimal>`, which the quota derivations below return. `CyberCloud.Core.Resources` is
// global here and `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins
// over the `Orleans.ErrorCode` this import would otherwise put back in play.
using CyberCloud.Core;

namespace CyberCloud.Providers.Monitor;

/// <summary>
///     Managed observability — one resource type, over the platform's own telemetry stores.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/16 § <c>CyberCloud.Monitor/workspaces</c> · <b>M1 · 2.5 EM</b>:
///         <i>"The tenant-facing resource. Owns retention, quota, ingest keys, and the datasource
///         wiring."</i> ADR-016 chose the two engines; docs/plan/01 § The catalogue spells the row
///         <c>CyberCloud.Monitor/workspaces</c> and this is the path.
///     </para>
///     <para>
///         ⚠ <b>A WORKSPACE IS A TENANCY, NOT A DEPLOYMENT.</b> The five-part argument is on
///         <see cref="MonitorWorkspaces" /> and it is the first thing to read on this row. The short
///         form: docs/plan/16 routes ingest to an <c>accountID</c> and a database rather than to a
///         store; docs/plan/05 gives each store one row per region; and — decisively — docs/plan/16
///         puts <b>platform</b> telemetry under a workspace of this type, so a type that provisioned
///         the store would make the control plane's own reconcile depend on the store its reconcile
///         creates. <c>CyberCloud.Ingest.Host</c> is deliberately not an Orleans client to keep that
///         cycle broken, and this type is shaped to keep it broken from the other end.
///     </para>
///     <para>
///         ⚠ <b>THE TWELFTH FAMILY, AND THE FIRST WHOSE PRODUCT IS NOT A WORKLOAD AT ALL.</b>
///         <c>CyberCloud.ContainerService/managedClusters</c> established that a resource's product
///         need not be the objects it applies — there the objects are Cluster API's and the product
///         is a second Kubernetes cluster. Here the objects are the product's <i>whole</i>
///         mechanism on one half and its <i>announcement</i> on the other: the <c>VMUser</c> is
///         enforcement, and the <c>ConfigMap</c> is a row nothing reads yet. A provider whose objects
///         are configuration rather than infrastructure is a shape this catalogue had not had, and
///         it needed no fifth module edge and no seventh project.
///     </para>
///     <para>
///         ⚠ <b>IT IS THE FIRST TYPE IN THE TREE TO DECLARE <c>SupportsSoftDelete</c>, AND THAT IS A
///         DECISION RATHER THAN A DEFAULT.</b> Eleven families declined, each with the same stated
///         reason — the manager did not honour a window — and docs/plan/08 § Soft delete endorsed
///         the instinct and ended <i>"the declaration is the last step, not the first"</i>. The
///         manager honours it now, so what is left is the provider's own question: <i>does the data
///         this type carries deserve a recovery window, and how long</i>. On this type the answer is
///         the least ambiguous in the catalogue. A workspace is the tenant's <b>only</b> copy of
///         their logs — a database has a backup, an object store has versioning, and telemetry has
///         neither, because the source of truth was a process that has since exited. docs/plan/16's
///         closing sentence is <i>"a monitoring product that quietly loses data is worse than no
///         monitoring product, because it is trusted"</i>, and a delete with no window is the
///         loudest possible version of that. Seven days, which is docs/plan/06 § Tags, locks'
///         number for a type carrying data, with purge behind its own permission and a
///         purge-protection flag on the body.
///     </para>
///     <para>
///         ⚠ <b>AND THE RECOVERY WINDOW IS CHEAP HERE FOR THE SAME REASON THE TYPE IS NOT A
///         DEPLOYMENT.</b> A soft-deleted workspace is a tenancy whose <c>VMUser</c> is gone — so
///         nothing can write to it — while its partitions age out under their existing retention. It
///         costs disk that was already reserved and no compute at all. On the deployment-shaped
///         reading, the same window would mean keeping a cluster running for a week per deleted
///         workspace, and somebody would have quietly made it a soft delete that deletes.
///     </para>
///     <para>
///         ⚠ <b>What is deliberately NOT declared, each with its reason.</b> No
///         <c>workspaces/ingestKeys</c> child type, although docs/plan/16 calls ingest keys a
///         sub-resource: that row also says <i>"rotatable with a grace period"</i>, and a grace
///         period is two live credentials at once, which <c>ISecretWriter</c>'s mint-once rule
///         cannot hold — the same blocker <c>CyberCloud.Storage/accounts</c> records against
///         <c>regenerateKeys</c>, now on its second sighting and on a type where it decides a whole
///         child rather than one action. No <c>collectors</c> and no <c>alertRules</c>: both are M2
///         in docs/plan/16 and neither is in this row's scope. No <c>dataSources</c> body property,
///         for the reason on <see cref="MonitorWorkspaces.ListKeysResponse" /> — it is an output.
///     </para>
/// </remarks>
public sealed class MonitorProvider : IResourceProvider {
    /// <inheritdoc />
    public string ProviderNamespace => MonitorWorkspaces.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType(MonitorWorkspaces.TypePath)
            .ApiVersion(MonitorWorkspaces.V2026, MonitorWorkspaces.Schema2026)
            .Reconciler<MonitorWorkspaceReconciler>()
            // ⚠ ONE DERIVED METER AND THE COUNT, AND THE DERIVATION IS A FIFTH SHAPE.
            //
            //   CyberCloud.DBforPostgreSQL/servers  an amount is a quantity STRING, not a number
            //   CyberCloud.Messaging/natsClusters   a PRODUCT of a replica count and one figure
            //   CyberCloud.Storage/accounts         a SUM over HETEROGENEOUS components
            //   CyberCloud.Analytics/clickhouseCl…  a PRODUCT and a SUM at once
            //   here                                a SUM over three signals of a product whose
            //                                       FIRST FACTOR IS NOT IN THE BODY AT ALL
            //
            // ⚠ THAT LAST DIFFERENCE IS THE ONE THAT MATTERS. Every earlier derivation multiplies
            // numbers the tenant typed. This one multiplies a GB/day allowance the tenant typed by a
            // day count that comes from MonitorWorkspaces.RetentionDays — a platform table selected
            // by a tier NAME the tenant typed. So the meter reads `/properties/retention/logs` and
            // gets back the string "standard", and the number 30 is the platform's. A derivation
            // that read the pointer and expected a number would derive nothing and reserve nothing.
            //
            // ⚠ AND THIS IS WHERE docs/plan/16 § Cost and retention honesty IS SATISFIED RATHER THAN
            // AGREED WITH. That section requires retention to be "a paid property"; the only way a
            // property is paid is if it moves the amount the platform reserves, and this is the line
            // where it does. MonitorQuotaTests.MovingOnlyTheRetentionTierMovesTheStorageAmount is
            // what fails if somebody ever "simplifies" the derivation to GB/day alone.
            //
            // ⚠ EACH DERIVATION IS A PURE FUNCTION OF THE BODY AND MUST STAY ONE. The delete path
            // re-derives committed amounts from the resource's stored body through the same step the
            // create reserved with — ResourceManagerService.CommittedBy — so a derivation that read
            // a clock or a configuration would make a delete return a different number than the
            // create committed, and quota would drift upward on every create/delete cycle.
            .Meter(QuotaMeter.StorageGb, StorageDrawn)
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            // ⚠ SYNCHRONOUS, WITH A HANDLER, WHICH IS THE ONLY SHAPE THAT ANSWERS THIS QUESTION.
            // `longRunning: false` with no handler is refused by name at declaration time, and
            // `longRunning: true` with a handler is refused by ProviderBuilder.Action — a
            // long-running action goes through the operation grain and re-runs the RECONCILER, which
            // for a listKeys would answer 202 and hand back nothing. This is the second handler in
            // the tree after CyberCloud.Storage/accounts'.
            .Action(
                MonitorWorkspaces.ListKeysAction,
                ActionKind.Post,
                MonitorWorkspaces.ListKeysPermission,
                secret: true,
                response: MonitorWorkspaces.ListKeysResponse,
                handler: typeof(MonitorWorkspaceListKeysHandler)
            )
            // ⚠ `workspace`, AND THE CHECK CyberCloud.Storage/accounts DEMANDS WAS RUN BY HAND
            // AGAINST LITERALS. CliEmitter derives the CLI GROUP key from the provider namespace's
            // last segment, lower-cased, so this namespace is already the group `monitor` — which is
            // why the obvious short name is the one that could not be used. The twelve group keys in
            // the tree are sample, dbforpostgresql, dbformysql, cache, messaging, storage, analytics,
            // search, documentdb, containerservice, network and monitor, and `workspace` is none of
            // them. It is also not one of the fifteen short names already declared. Both halves are
            // asserted against typed-out literals in MonitorDeclarationTests, because
            // System.CommandLine's ValidTokens builds ONE dictionary of every command token and every
            // alias in the whole tree and a collision throws "An item with the same key has already
            // been added" on the FIRST PARSE OF ANY COMMAND LINE.
            //
            // ⚠ ProviderRegistry.Build still refuses only a DUPLICATE short name and still never
            // compares one against a group name — `short-name-collides-with-the-group` stays owed,
            // and this type is the fourth that had to satisfy it by hand.
            .Display(
                "Monitor workspace",
                "Monitor workspaces",
                shortName: "workspace",
                summary: "A tenancy in the platform's metrics, logs and traces stores, with its own "
                + "retention, quota, ingest key and read-only datasource endpoints."
            )
            .Chart(MonitorWorkspaces.ChartName)
            // ⚠ SEVEN DAYS, AND THE ARGUMENT IS IN THIS CLASS'S REMARKS RATHER THAN HERE.
            // `purgeProtection` must be a declared boolean in every api-version or the builder
            // refuses the type — a flag enforced against a property no schema declares is a
            // protection that silently never engages.
            .SupportsSoftDelete(
                SoftDeleteDays,
                purgeProtectionPointer: MonitorWorkspaces.PurgeProtectionPointer
            )
            .SupportsTags()
            .RequiresCluster(MonitorWorkspaces.ClusterIdPointer);
    }

    /// <summary>How long a deleted workspace stays recoverable.</summary>
    /// <remarks>
    ///     docs/plan/06 § Tags, locks gives 7 for a type carrying data — <i>"a dropped production
    ///     database is not a support ticket you want to have to say no to"</i>. ⚠ It is a
    ///     <b>type-level</b> number and therefore immutable by construction, which is what
    ///     docs/plan/08 § Soft delete asks for: <i>"a window a caller can shorten under their own
    ///     resource is not a recovery window"</i>. There is no per-resource retention property for a
    ///     caller to shorten, and the delete path stamps the window from this constant.
    /// </remarks>
    public const int SoftDeleteDays = 7;

    // ── What a workspace draws ────────────────────────────────────────────────────────────────
    //
    // ⚠ NO `vcpu` AND NO `memoryGb`, AND THE REASON IS THE SAME ONE CyberCloud.Network/virtualNetworks
    // GIVES RATHER THAN THE QuotaGrain ONE THREE OTHER TYPES GIVE. A workspace provisions no pods:
    // the stores it is a tenancy in were running before it existed and keep running after it is
    // gone. There is nothing attributable to derive, so the axis is ABSENT rather than conditional,
    // and QuotaGrain.TryReserveAsync's "a reservation must be positive; 0 is not" refusal — which is
    // what blocks a CONDITIONAL meter on natsClusters, kafkaClusters and managedClusters — is not
    // what is happening here. ⚠ Second sighting of that distinction on the compute axis and the
    // first on a type that is not a network object, which is what makes it a property of "does this
    // resource run anything" rather than of networking.
    //
    // ⚠ AND `storageGb` IS PRESENT WHERE virtualNetworks HAS ONLY `Resources`, WHICH IS THE
    // DIFFERENCE BETWEEN THE TWO. A Vpc is a logical router with no disk. A workspace's whole cost
    // is disk, and the tenant sets both factors of it.

    /// <summary>
    ///     Storage: the gibibytes at rest this workspace's retention and daily allowances entitle it
    ///     to, summed over the three signals.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A CEILING RATHER THAN A SIZE, WHICH IS A SHAPE ONLY ONE OTHER TYPE HAS.</b>
    ///         <c>CyberCloud.ContainerService/managedClusters/agentPools</c> reserves <c>maxCount</c>
    ///         rather than <c>count</c> because an autoscaler moves the real number. Here the real
    ///         number is what the tenant actually sends, which the platform cannot know at create
    ///         time and which docs/plan/22's usage pipeline samples separately. So this reserves what
    ///         the workspace is <i>allowed</i> to accumulate. ⚠ A reservation is not a bill, and the
    ///         two disagreeing is not a defect in either.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It cannot be zero, and the schema is what guarantees that rather than this
    ///         method.</b> Every <c>*GbPerDay</c> has <c>Minimum = 1</c> and every retention tier
    ///         maps to a positive day count, so the sum is at least three — which keeps this meter
    ///         clear of <c>QuotaGrain.TryReserveAsync</c>'s non-positive refusal on every legal
    ///         body. A tier added to <see cref="MonitorWorkspaces.Tiers" /> without a row in
    ///         <see cref="MonitorWorkspaces.RetentionDays" /> would derive zero days for that signal,
    ///         which is why the refusal below exists and why
    ///         <c>MonitorRetentionTests.EveryTierOfEverySignalHasADayCount</c> is a test rather than
    ///         a comment.
    ///     </para>
    /// </remarks>
    static MeterDerivation StorageDrawn { get; } =
        MeterDerivation.Of(
            "the sum over metrics, logs and traces of (retention tier's days) × (that signal's "
            + "quota in GiB/day), in GiB",
            [
                "/properties/retention/metrics",
                "/properties/retention/logs",
                "/properties/retention/traces",
                "/properties/quota/metricsGbPerDay",
                "/properties/quota/logsGbPerDay",
                "/properties/quota/tracesGbPerDay"
            ],
            body => MonitorWorkspaces.StorageCeilingGb(body) is > 0 and var ceiling
                ? Result<decimal>.Success(ceiling)
                : Result<decimal>.Failure(
                    ErrorCode.InternalError,
                    "The storage a monitor workspace draws came out at zero or less, which no legal "
                    + "body can produce: every quota property has a minimum of 1 and every retention "
                    + "tier has a positive day count. The likeliest cause is a tier in "
                    + "MonitorWorkspaces.Tiers with no row in MonitorWorkspaces.RetentionDays. The "
                    + "write is refused rather than reserved at zero, because a resource that "
                    + "provisions against no quota is one nobody is charged for — docs/plan/06 "
                    + "§ Quota."
                )
        );
}
