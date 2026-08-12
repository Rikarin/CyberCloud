// ⚠ For `Result<decimal>`, which the quota derivations below return. `CyberCloud.Core.Resources` is
// global here and `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins
// over the `Orleans.ErrorCode` this import would otherwise put back in play.
using CyberCloud.Core;
using System.Text.Json;

namespace CyberCloud.Providers.Storage;

/// <summary>
///     Managed object storage — an account and the buckets inside it, on SeaweedFS.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FIRST PROVIDER IN THE TREE WITH TWO RESOURCE TYPES, AND THE SECOND IS A CHILD OF
///         THE FIRST.</b> docs/plan/15 § The three kinds names both halves in one row — <i>"Object ·
///         <c>CyberCloud.Storage/accounts</c> <b>+ <c>/buckets</c></b>"</i> — and until now no
///         shipping provider had declared a nested type at all. What it cost, measured rather than
///         estimated: one chained block in <see cref="Describe" />, one contracts file, one
///         reconciler, one chart, one <c>ProviderConformanceCase</c> with <b>one extra member</b>,
///         and two lines in the <c>*.Cluster.Conformance</c> project. Nothing in
///         <c>test/CyberCloud.Conformance</c>, <c>CliEmitter</c> or <c>SdkEmitter</c> changed —
///         <c>FormsEmitter</c> is the one surface that could not carry it, and that is recorded at
///         <c>charts/managed/seaweedfs-bucket/conformance.yaml § owed</c>,
///         <c>forms-cannot-address-a-child</c>, rather than worked around.
///     </para>
///     <para>
///         ⚠ <b>Nothing in the bucket half looks the account up.</b> docs/plan/12 § Child resources
///         makes the parent a pure function of the address; the parent-existence check belongs on the
///         create, in <c>ResourceManagerService.ResolveAsync</c>, where it answers the same 404 as an
///         unauthorized read. ⚠ On this provider there is a second reason: the account never reports
///         <c>Succeeded</c> — see below — so a bucket that waited for its parent to be healthy would
///         never converge for anybody.
///     </para>
///     <para>
///         ⚠ <b>THE FIRST PROVIDER IN THE TREE THAT IS NOT A ROW OF docs/plan/12.</b> That document's
///         catalogue is <i>"databases, caches, brokers, search"</i> and contains no storage row at all.
///         This one is
///         [15 § The three kinds](../../../../docs/plan/15-storage-blob-file.md) —
///         <i>"Object · <c>CyberCloud.Storage/accounts</c> + <c>/buckets</c> · SeaweedFS + S3
///         gateway"</i>, <i>"Object storage — M1 · 2.0 EM"</i> — and it is the row every other
///         document has been assuming: <c>charts/managed/postgres</c> renders a backup destination of
///         <c>s3://tenant-bucket/postgres</c>, docs/plan/12 § Backups sends every engine's native
///         backup <i>"to the tenant's bucket"</i>, and docs/plan/12's OpenSearch row wants a
///         <i>"snapshot repository into the tenant's bucket"</i>. None of that had a provider until
///         now.
///     </para>
///     <para>
///         ⚠ <b>What docs/plan/12 does own is the pattern and the operator.</b> Its § The pattern,
///         once lists the eight pieces every managed service is, and ADR-010 clause 1's survey names
///         <c>SeaweedFS</c>. Both were followed; three of the eight are not built and are named at
///         <c>charts/managed/seaweedfs/conformance.yaml § owed</c> rather than implied by an absence.
///     </para>
///     <para>
///         ⚠ <b>Piece 6 is discharged through its FIRST branch, which is that branch's second
///         sighting.</b> The corrected piece 6 reads <i>"ask the operator for the scrape object
///         wherever the operator accepts the request, and hand-write one into the chart only when
///         there is no operator to ask."</i> CloudNativePG answered with
///         <c>spec.monitoring.enablePodMonitor</c>; Kafka reached the second branch and left the object
///         owed; NATS reached it and discharged it by owning the pod labels. This operator answers the
///         first branch with <c>metricsPort</c> on each component — see
///         <see cref="StorageAccounts.MetricsPort" /> — so the platform renders no monitoring object
///         and the selector belongs to the party that owns the pods. Two services in, that branch is
///         the ordinary case rather than the lucky one.
///     </para>
///     <para>
///         ⚠ <b>Piece 5 is not built and this is the service where that costs the most.</b> The
///         argument is at <see cref="StorageAccounts.ConfigSecretName" /> and the short form is: a
///         SeaweedFS S3 gateway with no identities file authenticates nobody and answers every request
///         as an <b>administrator</b>, so the reference is rendered anyway and the account does not
///         finish. Unlike <c>CyberCloud.DBforPostgreSQL/servers</c>, whose operator generates its own
///         password, this service is <b>not usable at all</b> until there is a Vault to write the
///         identities from.
///     </para>
///     <para>
///         ⚠ <b>No <c>SupportsSoftDelete</c>, for the reason the three providers before this one give</b>:
///         the manager did not read <c>SoftDeleteDays</c>, and declaring a recovery window the platform
///         does not honour would be a promise made to the users most likely to test it. ⚠ <b>It is a worse
///         absence here than on any of them</b> — docs/plan/06 § Tags, locks gives 7 days to <i>"types
///         carrying data"</i>, and an object-storage account is the type that carries the most of it. What
///         partly stands in is the volume servers' PVCs, which a <c>Background</c> cascade leaves behind
///         unless the StorageClass reclaims them; that is a property of somebody else's configuration
///         rather than a promise this type makes, so it is not offered as one. ⚠ <b>THAT REASON HAS EXPIRED
///         AND THE DECLARATION IS NOW A ONE-LINE DECISION RATHER THAN A BLOCKED ONE.</b> docs/plan/08 §
///         Soft delete is built: a <c>DELETE</c> of a type declaring a window parks the resource at
///         <c>IndexEntryState.SoftDeleted</c> so its old address answers the canonical <c>404</c>, holds
///         its name, keeps its committed quota, moves its ReBAC parent edge to the subscription and drops
///         its direct role assignments; a restore reverses it and a purge — under its own permission — ends
///         it. So the question this type still owes an answer to is the provider's own: <i>does the data
///         this type carries deserve a recovery window, and how long</i>, which is a claim about the data
///         and not about the platform.
///     </para>
/// </remarks>
public sealed class StorageProvider : IResourceProvider {
    /// <inheritdoc />
    public string ProviderNamespace => StorageAccounts.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType(StorageAccounts.TypePath)
            .ApiVersion(StorageAccounts.V2026, StorageAccounts.Schema2026)
            .Reconciler<StorageAccountReconciler>()
            // ⚠ THREE DERIVED METERS AND THE COUNT, AND EACH OF THE THREE IS A SUM OVER COMPONENTS
            // THAT ARE NOT THE SAME SIZE AS EACH OTHER. That is the shape this type adds to what
            // MeterDerivation had already been asked for. CyberCloud.DBforPostgreSQL/servers found
            // that `Meter(meter, pointer)` reads a NUMBER and every Kubernetes amount is a string;
            // CyberCloud.Messaging/natsClusters added that every amount is a PRODUCT of a replica
            // count and a per-replica quantity. A Seaweed cluster is neither: it is
            //
            //     volumeServers × preset  +  (masters + 1 filer + gateway.replicas) × 250m/512Mi
            //
            // — two populations with different sizes, only one of which the tenant can size. A meter
            // that counted the volume servers alone would under-reserve by six pods on the default
            // body, which provisions perfectly and bills for none of them.
            //
            // ⚠ EACH DERIVATION IS A PURE FUNCTION OF THE BODY AND MUST STAY ONE. The delete path
            // re-derives committed amounts from the resource's stored body through the same step the
            // create reserved with — ResourceManagerService.CommittedBy — so a derivation that read a
            // clock or configuration would make a delete return a different number than the create
            // committed, and quota would drift upward on every create/delete cycle.
            .Meter(QuotaMeter.Vcpu, VcpuDrawn)
            .Meter(QuotaMeter.MemoryGb, MemoryDrawn)
            .Meter(QuotaMeter.StorageGb, StorageDrawn)
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Action(
                StorageAccounts.ListKeysAction,
                ActionKind.Post,
                StorageAccounts.ListKeysPermission,
                secret: true,
                response: StorageAccounts.ListKeysResponse
            )
            // ⚠ THE SHORT NAME IS `objectstore` AND THE OBVIOUS `storage` IS A HARD COLLISION, WHICH
            // NOTHING IN THE REGISTRY CHECKS. docs/plan/21 § Grammar's alias table would spell this
            // `storage`, exactly as it spells `postgres` for `dbforpostgresql server`. But
            // `CliEmitter` derives the GROUP key from the provider namespace, so this namespace's
            // group is already `storage` — and System.CommandLine's `ValidTokens` builds ONE
            // dictionary of every command token and every alias in the whole tree, so a group and an
            // alias that share a string throw `ArgumentException: An item with the same key has
            // already been added` on the first parse of any command line.
            //
            // ⚠ IT IS THE FIRST NAMESPACE WHOSE NATURAL SHORT NAME IS ITS OWN GROUP NAME, and the
            // four before it are near misses rather than a design: `postgres` against
            // `dbforpostgresql`, `valkey` against `cache`, `kafka`/`nats` against `messaging`,
            // `widget` against `sample`. `ProviderRegistry.Build` refuses a DUPLICATE short name —
            // IResourceTypeBuilder.Display's remarks say so, and say the point is that a duplicate is
            // "a silo-start failure rather than a CLI that resolves one of two verbs" — and it does
            // not compare a short name against a group name at all. `DerivedSurfaces.CliProblems`
            // does not either. What caught it is `cyc.Tests`' EveryVerbInTheTreeIsReachable, which
            // reports it as an ArgumentException out of System.CommandLine naming neither the
            // provider nor the string. Recorded at charts/managed/seaweedfs/conformance.yaml § owed,
            // `short-name-collides-with-the-group`.
            .Display(
                "Storage account",
                "Storage accounts",
                shortName: "objectstore",
                summary: "A managed S3-compatible object store on SeaweedFS, with replicated volume "
                + "servers, a filer and a scalable S3 gateway."
            )
            .Chart(StorageAccounts.ChartName)
            .SupportsTags()
            .RequiresCluster(StorageAccounts.ClusterIdPointer)
            // ── The child, docs/plan/15 § The three kinds' other half ─────────────────────────
            //
            // ⚠ THE FIRST CHILD TYPE IN A SHIPPING PROVIDER, AND IT COSTS ONE CHAINED BLOCK. Every
            // capability below is one the shared conformance suite has an assertion for — a child
            // that declared only a schema and three permissions would have no reconciler for
            // TheTypeIsRegisteredWithAReconcilerAndAllThreePermissions, own no objects for the four
            // cluster-facing assertions, declare no action for the two POST ones, and refuse tags.
            // A case for it would then pass a suite that quietly asked less.
            //
            // ⚠ THE BLOCKER THIS PROVIDER RECORDED WHEN IT SHIPPED THE PARENT IS CLOSED, AND THE
            // CORRECTION IS THE USEFUL HALF. `charts/managed/seaweedfs/conformance.yaml § owed`,
            // `bucket-child-type`, said the conformance harness could not address a depth-2 type:
            // ProviderConformanceCase was single-type, ProviderTestCluster.Address built a
            // ResourceId with no ParentNames, CliEmitter.Address had a hard-coded four-flag list and
            // SdkEmitter.AppendCollection took no ancestor parameters. All four landed on
            // 2026-08-12, the same day the note was written. What was left was scope, and this is it.
            //
            // ⚠ NO `.Meter(...)` DERIVATION, WHICH IS A DECISION RATHER THAN AN OMISSION. A bucket's
            // quota is a limit INSIDE capacity the account already reserved — StorageDrawn counts
            // every volume server's PVC — so a derived storage meter here would reserve the same
            // gibibyte twice, once as the disk it is written to and once as the ceiling on part of
            // it. That is the same double-count StorageDrawn refuses when it declines to multiply by
            // `replication`. What a bucket genuinely costs is measured rather than reserved:
            // docs/plan/15 § Metering samples `storage.object.gb_month` "hourly from SeaweedFS volume
            // stats per bucket", which is docs/plan/22's usage pipeline and not a pure function of a
            // body. `QuotaMeter.Resources` — the count of resources — is what a bucket actually draws
            // at write time.
            .ResourceType(StorageBuckets.TypePath)
            .ApiVersion(StorageBuckets.V2026, StorageBuckets.Schema2026)
            .Reconciler<StorageBucketReconciler>()
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Action(
                StorageBuckets.StatsAction,
                ActionKind.Post,
                StorageBuckets.StatsPermission,
                response: StorageBuckets.StatsResponse
            )
            // ⚠ `bucket`, and the check the parent's own comment demands is the one that made this
            // safe: `CliEmitter` derives the CLI group key from the provider namespace, so
            // `CyberCloud.Storage` is already the group `storage` — which is why the parent ships as
            // `objectstore` rather than the `storage` docs/plan/21 § Grammar would spell. `bucket` is
            // neither a group key (sample, dbforpostgresql, cache, messaging, storage) nor any
            // existing alias, and `StorageBucketDeclarationTests` asserts both halves against
            // literals. ⚠ `ProviderRegistry.Build` still refuses only a DUPLICATE short name and
            // still never compares one against a group name — `short-name-collides-with-the-group`
            // stays owed, and this type is the second one that had to satisfy it by hand.
            .Display(
                "Bucket",
                "Buckets",
                shortName: "bucket",
                summary: "An S3 bucket inside a managed object-storage account, with an optional size "
                + "limit and optional object versioning."
            )
            .Chart(StorageBuckets.ChartName)
            .SupportsTags()
            .RequiresCluster(StorageBuckets.ClusterIdPointer);
    }

    // ── What an account draws ──────────────────────────────────────────────────────────────────
    //
    // ⚠ `publicIps` IS NOT DECLARED AND FOR ONCE THAT IS NOT THE QUOTA GRAIN'S FAULT. Every other
    // service in the catalogue meets QuotaGrain.TryReserveAsync's refusal of a non-positive amount,
    // because an optional external listener makes the meter zero on the default path — the finding
    // CyberCloud.Messaging/natsClusters records. This type has no external listener to declare at
    // all: the operator's ServiceSpec carries no loadBalancerSourceRanges, so docs/plan/12
    // § Cross-cutting decisions' mandatory firewall list has nothing to render into and the axis is
    // absent rather than conditional. The two reasons look identical from the meter and are not, and
    // the difference is what a future author needs: closing THIS one is an upstream field, not a
    // skip-when-zero on MeterRegistration.

    /// <summary>vCPU: the volume servers at their preset, plus a fixed share for every other pod.</summary>
    /// <remarks>
    ///     ⚠ Refuses rather than reserving zero when the quantity does not parse. That happens only if
    ///     <c>sizing.preset</c> names a preset <see cref="StorageAccounts.Presets" /> does not carry —
    ///     which the schema's <c>AllowedValues</c> makes unreachable from a validated body, and which
    ///     is exactly the drift worth failing on when somebody adds a preset to the enum and forgets
    ///     the table.
    /// </remarks>
    static MeterDerivation VcpuDrawn { get; } =
        MeterDerivation.Of(
            "volumeServers × sizing.cpu (from sizing.preset when unset) + (masters + 1 + "
            + "gateway.replicas) × 250m, in cores",
            [
                "/properties/volumeServers",
                "/properties/sizing/preset",
                "/properties/sizing/cpu",
                "/properties/masters",
                "/properties/gateway/replicas"
            ],
            body => KubeQuantity.TryParse(StorageAccounts.Resources(body).Cpu, out var cores)
            && KubeQuantity.TryParse(StorageAccounts.ControlPlaneCpu, out var share)
                ? Result<decimal>.Success(
                    (StorageAccounts.VolumeServers(body) * cores) + (ControlPlanePods(body) * share)
                )
                : Unresolvable("cpu", "sizing.cpu or the sizing.preset behind it")
        );

    /// <summary>Memory: the same two populations, in gibibytes.</summary>
    static MeterDerivation MemoryDrawn { get; } =
        MeterDerivation.Of(
            "volumeServers × sizing.memory (from sizing.preset when unset) + (masters + 1 + "
            + "gateway.replicas) × 512Mi, in GiB",
            [
                "/properties/volumeServers",
                "/properties/sizing/preset",
                "/properties/sizing/memory",
                "/properties/masters",
                "/properties/gateway/replicas"
            ],
            body => KubeQuantity.TryGibibytes(StorageAccounts.Resources(body).Memory, out var gibibytes)
            && KubeQuantity.TryGibibytes(StorageAccounts.ControlPlaneMemory, out var share)
                ? Result<decimal>.Success(
                    (StorageAccounts.VolumeServers(body) * gibibytes) + (ControlPlanePods(body) * share)
                )
                : Unresolvable("memory", "sizing.memory or the sizing.preset behind it")
        );

    /// <summary>Storage: every volume server's data volume, plus the filer's metadata volume.</summary>
    /// <remarks>
    ///     ⚠ <b>Provisioned rather than replicated, which is a decision and the opposite of what
    ///     docs/plan/15 § Metering says about <i>block</i> storage.</b> That table bills
    ///     <c>storage.block.gb_month</c> as <i>"Provisioned × replication factor"</i>, and this meter
    ///     deliberately does not multiply by <c>replication</c>: the volume servers' PVCs are what the
    ///     cluster provisions, and a replication code decides how the copies are spread <i>across</i>
    ///     those same PVCs rather than adding any. Multiplying would charge the second copy twice —
    ///     once as the disk it is written to and once as a factor.
    ///     <para>
    ///         ⚠ The filer's volume is added because it exists — see
    ///         <see cref="StorageAccounts.FilerVolumeSize" /> — and because leaving it out is the same
    ///         under-count as leaving out its pod.
    ///     </para>
    /// </remarks>
    static MeterDerivation StorageDrawn { get; } =
        MeterDerivation.Of(
            "volumeServers × storage.size + 10Gi for the filer, in GiB",
            ["/properties/volumeServers", "/properties/storage/size"],
            body => KubeQuantity.TryGibibytes(StorageAccounts.StorageSize(body), out var gibibytes)
            && KubeQuantity.TryGibibytes(StorageAccounts.FilerVolumeSize, out var filer)
                ? Result<decimal>.Success((StorageAccounts.VolumeServers(body) * gibibytes) + filer)
                : Unresolvable("storage", "storage.size")
        );

    /// <summary>How many pods are sized by the platform rather than by the tenant.</summary>
    /// <remarks>
    ///     ⚠ The <c>+ 1</c> is the filer, which is pinned at one replica for the reason
    ///     <see cref="StorageAccounts.SeaweedJson" /> gives. It is spelled here rather than read from
    ///     the body because there is no property to read: the day an HA filer becomes expressible, this
    ///     function gains a term <i>and</i> a pointer in every <c>Reads</c> above, and a reviewer
    ///     comparing the two will see it.
    /// </remarks>
    static int ControlPlanePods(JsonElement body) =>
        StorageAccounts.Masters(body) + 1 + StorageAccounts.GatewayReplicas(body);

    static Result<decimal> Unresolvable(string what, string where) =>
        Result<decimal>.Failure(
            ErrorCode.InternalError,
            $"The {what} an object-storage account draws could not be read from {where}: the value is "
            + "not a Kubernetes quantity. The write is refused rather than reserved at zero, because a "
            + "resource that provisions against no quota is one nobody is charged for — docs/plan/06 "
            + "§ Quota."
        );
}
