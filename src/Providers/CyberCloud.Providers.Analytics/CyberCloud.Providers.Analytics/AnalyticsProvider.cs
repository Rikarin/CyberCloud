// ⚠ For `Result<decimal>`, which the quota derivations below return. `CyberCloud.Core.Resources` is
// global here and `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins
// over the `Orleans.ErrorCode` this import would otherwise put back in play.
using CyberCloud.Core;

namespace CyberCloud.Providers.Analytics;

/// <summary>
///     Managed analytics — one resource type, on the Altinity ClickHouse operator.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/12 § The catalogue: <i>"ClickHouse — <c>CyberCloud.Analytics/clickhouseClusters</c>
///         · M2 · 1.2 EM. <b>Altinity operator.</b>"</i> — ADR-010 clause 1's survey names the same
///         operator, and ADR-011 clears ClickHouse itself (Apache-2.0; it is not one of the four rows
///         that document refuses).
///     </para>
///     <para>
///         ⚠ <b>THE PLATFORM RUNS CLICKHOUSE AND THIS TYPE IS NOT THE PLATFORM'S CLICKHOUSE.</b> The
///         four-part argument is on <see cref="ClickHouseClusters" /> and it is the first thing to read
///         on this row; the short form is that docs/plan/16 and docs/plan/22 describe a per-region,
///         database-per-tenant, platform-schema store on the ingest write path, and this is a
///         single-tenant cluster in a tenant namespace whose schema docs/plan/12 says the tenant owns.
///     </para>
///     <para>
///         ⚠ <b>The sixth provider family, and the first whose namespace comes from the parity
///         catalogue rather than from a service name.</b> docs/plan/03 § Providers plans a
///         <c>Data</c> namespace holding six engines including this one; docs/plan/12 and docs/plan/01
///         both spell the row <c>CyberCloud.Analytics/clickhouseClusters</c>. The catalogue wins for
///         the reason <c>CyberCloud.DBforPostgreSQL</c> already established — the resource path is the
///         Azure-parity one — and the cost is recorded rather than hidden: this is a fifth family with
///         the same four module edges, which is now the fifth data point for docs/plan/25 § R1.
///     </para>
///     <para>
///         ⚠ <b>Two of docs/plan/12 § The pattern, once's eight pieces are not built and one is built
///         halfway, each named rather than implied.</b> Piece 5 — credential provisioning into the
///         tenant's Vault — needs an OpenBao integration that does not exist, so <c>listKeys</c> has a
///         declared response shape and no handler; ⚠ <b>on this service the consequence is a cluster
///         that is secure and unreachable rather than one that is open</b>, which is the opposite of
///         what <c>CyberCloud.Storage/accounts</c> found and is the better half of the same gap. Piece
///         6 reaches an answer neither of its branches describes — the operator scrapes every
///         installation itself through one cluster-wide exporter and offers no per-installation scrape
///         switch — so the metrics are turned on here and the object that scrapes them is owed. Piece
///         7 is declined with a reason. All four are at
///         <c>charts/managed/clickhouse/conformance.yaml § owed</c>.
///     </para>
///     <para>
///         ⚠ <b>No <c>SupportsSoftDelete</c>, for the reason the five providers before this one give</b>:
///         nothing in the manager reads <c>SoftDeleteDays</c>, and declaring a recovery window the
///         platform does not honour would be a promise made to the users most likely to test it.
///     </para>
/// </remarks>
public sealed class AnalyticsProvider : IResourceProvider {
    /// <inheritdoc />
    public string ProviderNamespace => ClickHouseClusters.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType(ClickHouseClusters.TypePath)
            .ApiVersion(ClickHouseClusters.V2026, ClickHouseClusters.Schema2026)
            .Reconciler<ClickHouseClusterReconciler>()
            // ⚠ THREE DERIVED METERS AND THE COUNT, AND EACH OF THE THREE IS A PRODUCT *AND* A SUM.
            // That is a fourth shape, after the three MeterDerivation had already been asked for:
            //
            //   CyberCloud.DBforPostgreSQL/servers  an amount is a quantity STRING, not a number
            //   CyberCloud.Messaging/natsClusters   an amount is a PRODUCT of a replica count and one
            //                                       per-replica figure
            //   CyberCloud.Storage/accounts         an amount is a SUM over HETEROGENEOUS components
            //   here                                shards × replicas × preset + keeperNodes × 250m
            //
            // ⚠ AND THE PRODUCT HAS TWO FACTORS THE TENANT SETS SEPARATELY, WHICH IS WHERE A
            // DERIVATION COPIED FROM natsClusters GOES WRONG AND STAYS PLAUSIBLE. That one is
            // `servers × figure` and reads one count; a ClickHouseInstallation's layout carries
            // shardsCount AND replicasCount, and the operator creates one StatefulSet per (shard,
            // replica) pair. A meter that multiplied by `replicas` alone would reserve a third of what
            // a three-shard cluster costs — and the resource would provision perfectly, read back
            // perfectly and converge. ClickHouseClusters.Servers is the one place that multiplication
            // is spelled, and AnalyticsQuotaTests.ShardsAndReplicasBothMoveTheAmounts is what fails on
            // the copy.
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
                ClickHouseClusters.ListKeysAction,
                ActionKind.Post,
                ClickHouseClusters.ListKeysPermission,
                secret: true,
                response: ClickHouseClusters.ListKeysResponse
            )
            // ⚠ `clickhouse`, AND THE CHECK CyberCloud.Storage/accounts DEMANDS WAS RUN BY HAND
            // AGAINST LITERALS. CliEmitter derives the CLI GROUP key from the provider namespace's
            // last segment, lower-cased, so this namespace is already the group `analytics`; the six
            // group keys in the tree are sample, dbforpostgresql, cache, messaging, storage and
            // analytics, and `clickhouse` is none of them. It is also not one of the seven short names
            // already declared — widget, postgres, valkey, kafka, nats, objectstore, bucket. Both
            // halves are asserted against typed-out literals in ClickHouseDeclarationTests, because
            // System.CommandLine's ValidTokens builds ONE dictionary of every command token and every
            // alias in the whole tree and a collision throws "An item with the same key has already
            // been added" on the FIRST PARSE OF ANY COMMAND LINE.
            //
            // ⚠ ProviderRegistry.Build still refuses only a DUPLICATE short name and still never
            // compares one against a group name — `short-name-collides-with-the-group` stays owed, and
            // this type is the third that had to satisfy it by hand.
            .Display(
                "ClickHouse cluster",
                "ClickHouse clusters",
                shortName: "clickhouse",
                summary: "A managed ClickHouse cluster on the Altinity operator, with declared shards "
                + "and replicas and a ClickHouse Keeper quorum. The schema is the tenant's."
            )
            .Chart(ClickHouseClusters.ChartName)
            .SupportsTags()
            .RequiresCluster(ClickHouseClusters.ClusterIdPointer);
    }

    // ── What a ClickHouse cluster draws ────────────────────────────────────────────────────────
    //
    // ⚠ `publicIps` IS NOT DECLARED, AND THE REASON IS THE ONE CyberCloud.Storage/accounts RECORDS
    // RATHER THAN THE ONE natsClusters DOES. There is no external listener to condition on: this type
    // declares no exposure at all, because docs/plan/12 § Cross-cutting decisions requires an explicit
    // CIDR allow-list on any exposure and the operator's own ServiceTemplate has nowhere to put one
    // that this provider can reach. So the axis is ABSENT rather than conditional, and the
    // QuotaGrain.TryReserveAsync "a reservation must be positive; 0 is not" refusal that blocks it on
    // three other types is not what blocks it here. Second sighting of that distinction, which is what
    // makes it a distinction rather than an anecdote.

    /// <summary>vCPU: every server's CPU request, plus a fixed share for every Keeper node.</summary>
    /// <remarks>
    ///     ⚠ Refuses rather than reserving zero when the quantity does not parse. That happens only if
    ///     <c>sizing.preset</c> names a preset <see cref="ClickHouseClusters.Presets" /> does not carry
    ///     — which the schema's <c>AllowedValues</c> makes unreachable from a validated body, and which
    ///     is exactly the drift worth failing on when somebody adds a preset to the enum and forgets
    ///     the table.
    /// </remarks>
    static MeterDerivation VcpuDrawn { get; } =
        MeterDerivation.Of(
            "shards × replicas × sizing.cpu (from sizing.preset when unset) + keeperNodes × 250m, in "
            + "cores",
            [
                "/properties/shards",
                "/properties/replicas",
                "/properties/sizing/preset",
                "/properties/sizing/cpu",
                "/properties/keeperNodes"
            ],
            body => KubeQuantity.TryParse(ClickHouseClusters.Resources(body).Cpu, out var cores)
            && KubeQuantity.TryParse(ClickHouseClusters.KeeperCpu, out var keeper)
                ? Result<decimal>.Success(
                    (ClickHouseClusters.Servers(body) * cores)
                    + (ClickHouseClusters.KeeperNodes(body) * keeper)
                )
                : Unresolvable("cpu", "sizing.cpu or the sizing.preset behind it")
        );

    /// <summary>Memory: the same two populations, in gibibytes.</summary>
    static MeterDerivation MemoryDrawn { get; } =
        MeterDerivation.Of(
            "shards × replicas × sizing.memory (from sizing.preset when unset) + keeperNodes × 512Mi, "
            + "in GiB",
            [
                "/properties/shards",
                "/properties/replicas",
                "/properties/sizing/preset",
                "/properties/sizing/memory",
                "/properties/keeperNodes"
            ],
            body => KubeQuantity.TryGibibytes(ClickHouseClusters.Resources(body).Memory, out var gibibytes)
            && KubeQuantity.TryGibibytes(ClickHouseClusters.KeeperMemory, out var keeper)
                ? Result<decimal>.Success(
                    (ClickHouseClusters.Servers(body) * gibibytes)
                    + (ClickHouseClusters.KeeperNodes(body) * keeper)
                )
                : Unresolvable("memory", "sizing.memory or the sizing.preset behind it")
        );

    /// <summary>Storage: every server's data volume, plus every Keeper node's own volume.</summary>
    /// <remarks>
    ///     ⚠ <b>The replica factor is IN the product and is not applied twice.</b> A replica in
    ///     ClickHouse is a whole second copy of the shard's data on its own volume, unlike SeaweedFS'
    ///     replication codes — which is why <c>CyberCloud.Storage/accounts</c>' meter deliberately does
    ///     <i>not</i> multiply and this one deliberately does. The two look like opposite decisions and
    ///     are the same decision: charge for the disks the cluster provisions, once each.
    ///     <para>
    ///         ⚠ The Keeper volumes are added because they exist, and because leaving them out is the
    ///         same under-count as leaving out their pods.
    ///     </para>
    /// </remarks>
    static MeterDerivation StorageDrawn { get; } =
        MeterDerivation.Of(
            "shards × replicas × storage.size + keeperNodes × 10Gi, in GiB",
            ["/properties/shards", "/properties/replicas", "/properties/storage/size", "/properties/keeperNodes"],
            body => KubeQuantity.TryGibibytes(ClickHouseClusters.StorageSize(body), out var gibibytes)
            && KubeQuantity.TryGibibytes(ClickHouseClusters.KeeperVolumeSize, out var keeper)
                ? Result<decimal>.Success(
                    (ClickHouseClusters.Servers(body) * gibibytes)
                    + (ClickHouseClusters.KeeperNodes(body) * keeper)
                )
                : Unresolvable("storage", "storage.size")
        );

    static Result<decimal> Unresolvable(string what, string where) =>
        Result<decimal>.Failure(
            ErrorCode.InternalError,
            $"The {what} a ClickHouse cluster draws could not be read from {where}: the value is not a "
            + "Kubernetes quantity. The write is refused rather than reserved at zero, because a "
            + "resource that provisions against no quota is one nobody is charged for — docs/plan/06 "
            + "§ Quota."
        );
}
