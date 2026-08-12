// ⚠ For `Result<decimal>`, which the quota derivations below return. `CyberCloud.Core.Resources` is
// global here and `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins
// over the `Orleans.ErrorCode` this import would otherwise put back in play.
using CyberCloud.Core;

namespace CyberCloud.Providers.DBforPostgreSQL;

/// <summary>
///     Managed PostgreSQL — one resource type, one api-version, one reconciler.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/12 § The catalogue: <i>"PostgreSQL — <c>CyberCloud.DBforPostgreSQL/servers</c>
///         · M1 · 1.2 EM"</i>, on CloudNativePG. This is the first provider that is a feature rather
///         than an instrument — <c>CyberCloud.Providers.Sample</c> is deliberately trivial and stays
///         that way (docs/plan/24 § Phase 1, docs/plan/25 § R1).
///     </para>
///     <para>
///         ⚠ <b>Six of docs/plan/12 § The pattern, once's eight pieces are here or next door, and the
///         two that are not are named rather than implied.</b> The chart (1), its generated schema
///         (2), this registration (3), the reconciler (4) and the conformance manifest (8) exist;
///         monitoring (6) is one annotated boolean rendered into the <c>Cluster</c> CR's
///         <c>spec.monitoring.enablePodMonitor</c>, which is the correction that document records, and
///         backup (7) is the <c>backup</c> block rendering into barman-cloud rather than the
///         unread <c>backup.yaml</c> the same document calls under-specified. <b>Piece 5 —
///         credential provisioning into the tenant's Vault — is not built</b>, because there is no
///         OpenBao integration and <c>ISecretResolver</c>'s only implementation refuses. What that
///         costs is on <c>PostgresServers.ClusterJson</c>, in the place a reader hits it.
///     </para>
///     <para>
///         ⚠ <b>What is deliberately not declared:</b> no <c>servers/databases</c>,
///         <c>servers/roles</c> or <c>servers/firewallRules</c> — each is its own type with its own
///         reconciler, and declaring one with no reconciler puts a type in the registry that answers
///         <c>202</c> and converges nothing; no <c>regenerateKeys</c>, for the reason on
///         <c>PostgresServers.ListKeysAction</c>; and no <c>SupportsSoftDelete</c>, which is the one
///         omission this type has an argument against — see below.
///     </para>
///     <para>
///         ⚠ <b><c>SupportsSoftDelete</c> is exactly what docs/plan/06 § Tags, locks asks for on a type
///         carrying data</b> — <i>"a dropped production database is not a support ticket you want to
///         have to say no to"</i>, with 7 days named. It is not declared because nothing in the
///         manager reads <c>SoftDeleteDays</c>: <c>DeleteTearsDownTheDataPlaneAndTheResourceIsGone</c>
///         asserts the objects are gone and the name is released, and no recovery path exists to
///         restore them from. Declaring a recovery window the platform does not honour would be a
///         promise made to the one type whose users would test it. This is a
///         <c>CyberCloud.ResourceManager</c> gap that the first data-carrying provider surfaces, which
///         is the measurement docs/plan/25 § R1 asks a provider to produce.
///     </para>
/// </remarks>
public sealed class PostgresProvider : IResourceProvider {
    /// <inheritdoc />
    public string ProviderNamespace => PostgresServers.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType(PostgresServers.TypePath)
            .ApiVersion(PostgresServers.V2026, PostgresServers.Schema2026)
            .Reconciler<PostgresServerReconciler>()
            // ⚠ THE THREE METERS A CUSTOMER ACTUALLY BUYS, AND THEY WERE UNDECLARABLE UNTIL THE
            // REGISTRY GREW A DERIVED-AMOUNT SEAM.
            //
            // What was here before was `.Meters(QuotaMeter.Resources)` alone — a count of one — with a
            // note explaining that `Meter(meter, amountPointer, fallback)` reserves the NUMBER at a
            // JSON Pointer and that none of vcpu, memoryGb or storageGb is a number in this body:
            // `storage.size` is "20Gi", `sizing.cpu` is "500m", and the ordinary body supplies neither
            // because `sizing.preset` names them indirectly. That note offered two repairs, a unit on
            // the pointer or a derived-amount seam. THE FIRST WOULD NOT HAVE WORKED HERE, and finding
            // out why is what settled it: `PostgresServers.Body` — the body the tests, the fixtures
            // and the conformance case all use — writes no `sizing` block at all, so a quantity read
            // at `/properties/sizing/cpu` resolves to nothing; and the amount is per-instance ×
            // instances, which is two pointers rather than one. `MeterDerivation` is the seam that
            // landed, and it carries an Expression and a read set so the generated document still says
            // what each meter draws — the objection a pointer was chosen over a delegate for.
            //
            // ⚠ EACH DERIVATION IS A PURE FUNCTION OF THE BODY AND MUST STAY ONE. The delete path
            // re-derives committed amounts from the resource's stored body through the same step the
            // create reserved with, so a derivation that read a clock or configuration would make a
            // delete return a different number than the create committed — quota drifting upward on
            // every create/delete cycle. See ResourceManagerService.CommittedBy.
            .Meter(QuotaMeter.Vcpu, VcpuDrawn)
            .Meter(QuotaMeter.MemoryGb, MemoryDrawn)
            .Meter(QuotaMeter.StorageGb, StorageDrawn)
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Action(
                PostgresServers.ListKeysAction,
                ActionKind.Post,
                PostgresServers.ListKeysPermission,
                secret: true,
                response: PostgresServers.ListKeysResponse
            )
            .Display(
                "PostgreSQL server",
                "PostgreSQL servers",
                shortName: "postgres",
                summary: "A managed PostgreSQL cluster on CloudNativePG, with replication, PgBouncer "
                + "and backup to the tenant's object store."
            )
            // docs/plan/12 § The pattern, once, piece 1 — and ADR-012's fifth surface, which is the
            // one binding that ties this registration to charts/managed/postgres. See the remarks on
            // PostgresServers.ChartName for why this is a declaration and not a render path.
            .Chart(PostgresServers.ChartName)
            .SupportsTags()
            .RequiresCluster(PostgresServers.ClusterIdPointer);
    }

    // ── What a server draws ────────────────────────────────────────────────────────────────────
    //
    // ⚠ ALL THREE MULTIPLY BY `replicas`, AND THAT IS THE PART A POINTER COULD NEVER HAVE SAID.
    // CloudNativePG runs `spec.instances` pods; each carries the whole `spec.resources` block and each
    // gets its own data PVC. PostgresServers.ClusterJson writes both figures once because the CR is
    // per-cluster, not because the cost is. The default body is two instances, so a per-instance
    // reservation would have been exactly half of the truth on the commonest shape there is.
    //
    // ⚠ THE POOLER IS DELIBERATELY NOT COUNTED. PostgresServers.PoolerJson renders no `resources`
    // block, so PgBouncer's pods request nothing from the scheduler and there is no declared figure to
    // reserve. Inventing one here would be quota charging for a number that appears nowhere in the
    // data plane. It is a real under-count, it is bounded by `pooling.instances`, and the honest place
    // to close it is the chart growing a pooler `resources` block — at which point this file gains a
    // second term rather than a guess.

    /// <summary>vCPU: every instance's CPU request, from the explicit override or the preset.</summary>
    /// <remarks>
    ///     ⚠ Refuses rather than reserving zero when the quantity does not parse. That happens only if
    ///     <c>sizing.preset</c> names a preset <c>PostgresServers.Presets</c> does not carry — which the
    ///     schema's <c>AllowedValues</c> makes unreachable from a validated body, and which is exactly
    ///     the drift worth failing on when somebody adds a preset to the enum and forgets the table.
    /// </remarks>
    static MeterDerivation VcpuDrawn { get; } =
        MeterDerivation.Of(
            "replicas × sizing.cpu, in cores, taking sizing.preset when the override is empty",
            ["/properties/replicas", "/properties/sizing/preset", "/properties/sizing/cpu"],
            body => KubeQuantity.TryParse(PostgresServers.Resources(body).Cpu, out var cores)
                ? Result<decimal>.Success(PostgresServers.Replicas(body) * cores)
                : Unresolvable("cpu", "sizing.cpu or the sizing.preset behind it")
        );

    /// <summary>Memory: every instance's memory request, in gibibytes.</summary>
    static MeterDerivation MemoryDrawn { get; } =
        MeterDerivation.Of(
            "replicas × sizing.memory, in GiB, taking sizing.preset when the override is empty",
            ["/properties/replicas", "/properties/sizing/preset", "/properties/sizing/memory"],
            body => KubeQuantity.TryGibibytes(PostgresServers.Resources(body).Memory, out var gibibytes)
                ? Result<decimal>.Success(PostgresServers.Replicas(body) * gibibytes)
                : Unresolvable("memory", "sizing.memory or the sizing.preset behind it")
        );

    /// <summary>Storage: every instance's data volume, plus its WAL volume when it has a separate one.</summary>
    /// <remarks>
    ///     ⚠ An empty <c>storage.walSize</c> is <b>one volume, not zero storage</b> — the WAL shares the
    ///     data volume, whose size is already counted. Parsing <c>""</c> as zero and adding it would be
    ///     right by accident here and wrong the moment the same helper is reused, so the two cases are
    ///     separated on purpose.
    /// </remarks>
    static MeterDerivation StorageDrawn { get; } =
        MeterDerivation.Of(
            "replicas × (storage.size + storage.walSize), in GiB; walSize empty means one volume",
            ["/properties/replicas", "/properties/storage/size", "/properties/storage/walSize"],
            body => {
                if (!KubeQuantity.TryGibibytes(PostgresServers.StorageSize(body), out var data)) {
                    return Unresolvable("storage", "storage.size");
                }

                var wal = PostgresServers.WalSize(body);
                if (wal.Length > 0) {
                    if (!KubeQuantity.TryGibibytes(wal, out var separate)) {
                        return Unresolvable("storage", "storage.walSize");
                    }

                    data += separate;
                }

                return Result<decimal>.Success(PostgresServers.Replicas(body) * data);
            }
        );

    static Result<decimal> Unresolvable(string what, string where) =>
        Result<decimal>.Failure(
            ErrorCode.InternalError,
            $"The {what} a server draws could not be read from {where}: the value is not a Kubernetes "
            + "quantity. The write is refused rather than reserved at zero, because a resource that "
            + "provisions against no quota is one nobody is charged for — docs/plan/06 § Quota."
        );
}
