// ⚠ For `Result<decimal>`, which the quota derivations below return. `CyberCloud.Core.Resources` is
// global here and `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins
// over the `Orleans.ErrorCode` this import would otherwise put back in play.
using CyberCloud.Core;

namespace CyberCloud.Providers.DocumentDB;

/// <summary>
///     Managed MongoDB-compatible document storage — FerretDB over CloudNativePG.
/// </summary>
/// <remarks>
///     <para>
///         [12 § The catalogue](../../../../docs/plan/12-managed-data-services.md):
///         <i>"MongoDB-compatible — <c>CyberCloud.DocumentDB/accounts</c> · M2 · 1.2 EM. FerretDB
///         (Apache-2.0) over a CloudNativePG cluster."</i> The sixth provider namespace and the
///         eighth resource type.
///     </para>
///     <para>
///         ⚠ <b>THE FIRST ROW THAT IS TWO WORKLOADS RATHER THAN ONE, AND IT IS WHY EVERY OTHER
///         FINDING HERE IS PAIRED.</b> An account is a CloudNativePG <c>Cluster</c> — operator-run,
///         with failover, backup and its own scrape object — <i>and</i> a FerretDB <c>Deployment</c>,
///         which has no operator at all. Four objects across four API groups. The consequences run
///         through the whole provider: piece 6 takes both of its branches at once, the quota meters
///         sum two populations with different sizes, and <c>Matches</c> needs containment for three
///         independent reasons instead of one.
///     </para>
///     <para>
///         ⚠ <b>ADR-011 IS THE REASON THIS ROW EXISTS AND THE HONESTY RULE IS ENFORCED RATHER THAN
///         WRITTEN DOWN.</b> Real MongoDB is SSPL — <i>"offering software as a service is exactly the
///         use that several 2023–2025 licence changes exist to prevent"</i> — so the catalogue's
///         document database is FerretDB, which is Apache-2.0 (verified at that repository's own
///         <c>LICENSE</c>, not at a badge), over the <c>documentdb</c> extension, which is MIT.
///         docs/plan/12 requires the row to publish a supported-subset table;
///         <see cref="DocumentDbAccounts.UnsupportedCommands" /> is that table, in the registry, so it
///         reaches the CLI and the portal through the same generation the schema does. The
///         <see cref="DocumentDbAccounts.CompatibilityStatement" /> is in this type's own
///         <c>summary</c>, which is what <c>cyc documentdb accounts --help</c> prints.
///     </para>
///     <para>
///         ⚠ <b>ADR-010 CLAUSE 1's SURVEY NAMES "FerretDB" AMONG THE OPERATORS AND THERE IS NO
///         FERRETDB OPERATOR.</b> Checked against the GitHub API on 2026-08-12 rather than a README:
///         no operator, no CRD, no Helm chart in that organisation. <b>SECOND SIGHTING</b> —
///         <c>charts/managed/nats</c> found <c>nats-operator</c> archived — so clause 1 is a survey of
///         <i>software choices</i> that is only sometimes a survey of <i>operators</i>. Two of the six
///         rows built from it have found this; it is a property of the clause now rather than of
///         either service, and it belongs in ADR-010 rather than in a provider.
///     </para>
///     <para>
///         ⚠ <b>docs/plan/12 SAYS THE POSTGRES HALF IS "ALREADY BUILT FOR THE ROW ABOVE" AND NOTHING
///         WAS REUSABLE.</b> <c>src/Providers/README.md § Hard rule</c> forbids the assembly
///         reference, so this provider renders the <c>Cluster</c> CRD independently — and writing that
///         second rendering found a defect in the first, which is the strongest available argument for
///         the rule. <c>CyberCloud.DBforPostgreSQL/servers</c> puts
///         <c>shared_preload_libraries</c> inside <c>spec.postgresql.parameters</c>, where
///         CloudNativePG's validating webhook refuses it as a fixed configuration parameter; the
///         correct field is the sibling list <c>spec.postgresql.shared_preload_libraries</c>. See
///         <see cref="DocumentDbAccounts.SharedPreloadLibraries" /> for the two source files that
///         say so, and <c>charts/managed/ferretdb/conformance.yaml § owed</c> for what it costs that
///         row. It is <b>not fixed here</b>, because this provider does not own that one.
///     </para>
///     <para>
///         ⚠ <b>PIECE 5 IS NOT BUILT AND THIS IS THE MILDEST OF THE THREE ANSWERS THE CATALOGUE HAS
///         GIVEN.</b> CloudNativePG generates the credential, and FerretDB neither stores nor invents
///         one — it forwards a client's credentials to PostgreSQL and returns PostgreSQL's verdict, so
///         an anonymous caller connects and can do nothing. The service therefore works and
///         <c>listKeys</c> merely has nowhere to read the password back from, which is the
///         <c>CyberCloud.DBforPostgreSQL/servers</c> answer rather than
///         <c>CyberCloud.Cache/redis</c>' (does not start) or <c>CyberCloud.Storage/accounts</c>'
///         (starts open). It is the first row to <i>reproduce</i> an earlier answer instead of adding
///         a worse one.
///     </para>
///     <para>
///         ⚠ <b>No <c>SupportsSoftDelete</c>, for the reason the five providers before this one give</b>:
///         the manager did not read <c>SoftDeleteDays</c>, and declaring a recovery window the platform
///         does not honour is a promise made to the users most likely to test it. ⚠ <b>THAT REASON HAS
///         EXPIRED AND THE DECLARATION IS NOW A ONE-LINE DECISION RATHER THAN A BLOCKED ONE.</b>
///         docs/plan/08 § Soft delete is built: a <c>DELETE</c> of a type declaring a window parks the
///         resource at <c>IndexEntryState.SoftDeleted</c> so its old address answers the canonical
///         <c>404</c>, holds its name, keeps its committed quota, moves its ReBAC parent edge to the
///         subscription and drops its direct role assignments; a restore reverses it and a purge — under
///         its own permission — ends it. So the question this type still owes an answer to is the
///         provider's own: <i>does the data this type carries deserve a recovery window, and how long</i>,
///         which is a claim about the data and not about the platform.
///     </para>
/// </remarks>
public sealed class DocumentDbProvider : IResourceProvider {
    /// <inheritdoc />
    public string ProviderNamespace => DocumentDbAccounts.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType(DocumentDbAccounts.TypePath)
            .ApiVersion(DocumentDbAccounts.V2026, DocumentDbAccounts.Schema2026)
            .Reconciler<DocumentDbAccountReconciler>()
            // ⚠ THREE DERIVED METERS AND THE COUNT, AND EACH SUMS TWO POPULATIONS THE TENANT SIZES
            // DIFFERENTLY. CyberCloud.Storage/accounts was the first type whose meters were a sum over
            // heterogeneous components; this is the second, and the shape is not the same one.
            // There it was `volumeServers × preset + (masters + filer + gateways) × 250m` — one sized
            // population and one fixed one, both inside a single operator's object. Here the two
            // populations are in DIFFERENT OBJECTS, applied through different controllers:
            //
            //     postgres.instances × preset  +  gateway.replicas × 250m/512Mi
            //
            // — which matters because a reader checking the derivation against "the rendered object"
            // has two objects to check, and a derivation copied from the row above would count the
            // Cluster and miss the Deployment entirely. On the default body that is two whole pods.
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
                DocumentDbAccounts.ListKeysAction,
                ActionKind.Post,
                DocumentDbAccounts.ListKeysPermission,
                secret: true,
                response: DocumentDbAccounts.ListKeysResponse,
                handler: typeof(DocumentDbAccountListKeysHandler)
            )
            // ⚠ `docdb`, AND THE THREE OBVIOUS ALTERNATIVES ARE EACH WRONG FOR A DIFFERENT REASON.
            //
            // `documentdb` is the CLI GROUP this namespace already produces — CliEmitter.GroupOf is
            // the provider namespace's last segment, lower-cased — and System.CommandLine's
            // ValidTokens builds ONE dictionary over every command token and every alias in the whole
            // tree, so a group and an alias sharing a string throw `An item with the same key has
            // already been added` on the first parse of ANY command line. That is
            // CyberCloud.Storage/accounts' finding, and this is the SECOND namespace whose natural
            // short name is its own group name — which turns it from a near miss into a pattern:
            // whenever docs/plan/21 § Grammar's alias table would spell an alias the same way the
            // namespace does, it collides.
            //
            // `mongo` and `mongodb` are refused on ADR-011 grounds rather than mechanical ones. A
            // short name is what a human types and what appears in every example, and this service is
            // not MongoDB — DocumentDbDeclarationTests asserts against both strings.
            //
            // ⚠ ProviderRegistry.Build still refuses only a DUPLICATE short name and still never
            // compares one against a group name; DerivedSurfaces.CliProblems does not either. Both
            // checks below are by hand, against literals.
            .Display(
                "Document database account",
                "Document database accounts",
                shortName: "docdb",
                summary: DocumentDbAccounts.CompatibilityStatement
                + " Runs on a CloudNativePG cluster, so failover, backup and point-in-time recovery "
                + "are the operator's."
            )
            .Chart(DocumentDbAccounts.ChartName)
            .SupportsTags()
            .RequiresCluster(DocumentDbAccounts.ClusterIdPointer);
    }

    // ── What an account draws ──────────────────────────────────────────────────────────────────
    //
    // ⚠ `publicIps` IS NOT DECLARED AND THE REASON IS THE THIRD DISTINCT ONE IN THE CATALOGUE.
    // CyberCloud.Messaging/natsClusters and kafkaClusters cannot declare it because
    // QuotaGrain.TryReserveAsync refuses a non-positive amount and an optional listener derives zero
    // on the default path. CyberCloud.Storage/accounts cannot because its operator's ServiceSpec has
    // no loadBalancerSourceRanges to firewall with. Here the field EXISTS — a core Service carries
    // loadBalancerSourceRanges — and external exposure is still not offered, because the credential
    // behind the endpoint is a PostgreSQL role this platform cannot rotate: docs/plan/12 makes
    // `regenerateKeys` part of the credential story and nothing implements it, so a public endpoint
    // would be a database whose password can never be changed. That is a product decision rather than
    // a registry gap or an upstream one, and it closes differently from both.
    // charts/managed/ferretdb/conformance.yaml § owed, `external-exposure`.

    /// <summary>
    ///     vCPU: the PostgreSQL instances at their preset, plus a fixed share per FerretDB pod.
    /// </summary>
    /// <remarks>
    ///     ⚠ Refuses rather than reserving zero when the quantity does not parse. That happens only if
    ///     <c>sizing.preset</c> names a preset <see cref="DocumentDbAccounts.Presets" /> does not
    ///     carry — which the schema's <c>AllowedValues</c> makes unreachable from a validated body, and
    ///     which is exactly the drift worth failing on when somebody adds a preset to the enum and
    ///     forgets the table.
    /// </remarks>
    static MeterDerivation VcpuDrawn { get; } =
        MeterDerivation.Of(
            "postgres.instances × sizing.cpu (from sizing.preset when unset) + gateway.replicas × "
            + "250m, in cores",
            [
                "/properties/postgres/instances",
                "/properties/sizing/preset",
                "/properties/sizing/cpu",
                "/properties/gateway/replicas"
            ],
            body => KubeQuantity.TryParse(DocumentDbAccounts.Resources(body).Cpu, out var cores)
            && KubeQuantity.TryParse(DocumentDbAccounts.GatewayCpu, out var share)
                ? Result<decimal>.Success(
                    (DocumentDbAccounts.Instances(body) * cores)
                    + (DocumentDbAccounts.GatewayReplicas(body) * share)
                )
                : Unresolvable("cpu", "sizing.cpu or the sizing.preset behind it")
        );

    /// <summary>Memory: the same two populations, in gibibytes.</summary>
    static MeterDerivation MemoryDrawn { get; } =
        MeterDerivation.Of(
            "postgres.instances × sizing.memory (from sizing.preset when unset) + gateway.replicas × "
            + "512Mi, in GiB",
            [
                "/properties/postgres/instances",
                "/properties/sizing/preset",
                "/properties/sizing/memory",
                "/properties/gateway/replicas"
            ],
            body => KubeQuantity.TryGibibytes(DocumentDbAccounts.Resources(body).Memory, out var gibibytes)
            && KubeQuantity.TryGibibytes(DocumentDbAccounts.GatewayMemory, out var share)
                ? Result<decimal>.Success(
                    (DocumentDbAccounts.Instances(body) * gibibytes)
                    + (DocumentDbAccounts.GatewayReplicas(body) * share)
                )
                : Unresolvable("memory", "sizing.memory or the sizing.preset behind it")
        );

    /// <summary>Storage: every PostgreSQL instance's data volume.</summary>
    /// <remarks>
    ///     ⚠ <b>Multiplied by the instance count, and the FerretDB pods add nothing.</b> CloudNativePG
    ///     gives every instance its own PVC of the declared size — a replica is a full physical copy,
    ///     not a share of one volume — so a two-instance account provisions twice what its
    ///     <c>storage.size</c> says. The gateway pods have no volume at all: FerretDB writes nothing
    ///     durable, which is the same reason it is a <c>Deployment</c>.
    /// </remarks>
    static MeterDerivation StorageDrawn { get; } =
        MeterDerivation.Of(
            "postgres.instances × storage.size, in GiB",
            ["/properties/postgres/instances", "/properties/storage/size"],
            body => KubeQuantity.TryGibibytes(DocumentDbAccounts.StorageSize(body), out var gibibytes)
                ? Result<decimal>.Success(DocumentDbAccounts.Instances(body) * gibibytes)
                : Unresolvable("storage", "storage.size")
        );

    static Result<decimal> Unresolvable(string what, string where) =>
        Result<decimal>.Failure(
            ErrorCode.InternalError,
            $"The {what} a document-database account draws could not be read from {where}: the value "
            + "is not a Kubernetes quantity. The write is refused rather than reserved at zero, "
            + "because a resource that provisions against no quota is one nobody is charged for — "
            + "docs/plan/06 § Quota."
        );
}
