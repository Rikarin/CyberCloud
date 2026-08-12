// ⚠ For `Result<decimal>`, which the quota derivations below return. `CyberCloud.Core.Resources` is
// global here and `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins
// over the `Orleans.ErrorCode` this import would otherwise put back in play.
using CyberCloud.Core;
using System.Text.Json;

namespace CyberCloud.Providers.Storage;

/// <summary>
///     Managed object storage — one resource type, on SeaweedFS.
/// </summary>
/// <remarks>
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
///         ⚠ <b>No <c>SupportsSoftDelete</c>, for the reason the three providers before this one
///         give</b>: nothing in the manager reads <c>SoftDeleteDays</c>, and declaring a recovery
///         window the platform does not honour would be a promise made to the users most likely to
///         test it. ⚠ <b>It is a worse absence here than on any of them</b> — docs/plan/06 § Tags,
///         locks gives 7 days to <i>"types carrying data"</i>, and an object-storage account is the
///         type that carries the most of it. What partly stands in is the volume servers' PVCs, which
///         a <c>Background</c> cascade leaves behind unless the StorageClass reclaims them; that is a
///         property of somebody else's configuration rather than a promise this type makes, so it is
///         not offered as one.
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
            .RequiresCluster(StorageAccounts.ClusterIdPointer);
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
