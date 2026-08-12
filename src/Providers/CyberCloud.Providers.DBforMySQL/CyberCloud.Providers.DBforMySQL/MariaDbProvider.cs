// ⚠ For `Result<decimal>`, which the quota derivations below return. `CyberCloud.Core.Resources` is
// global here and `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins
// over the `Orleans.ErrorCode` this import would otherwise put back in play.
using CyberCloud.Core;

namespace CyberCloud.Providers.DBforMySQL;

/// <summary>
///     Managed MariaDB — one resource type, one api-version, one reconciler.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/12 § The catalogue: <i>"MariaDB — <c>CyberCloud.DBforMySQL/servers</c> · M3 ·
///         0.8 EM"</i>, on mariadb-operator (ADR-010 clause 1).
///     </para>
///     <para>
///         ⚠ <b>THE DISPLAY SUMMARY BELOW IS THE PRODUCT PAGE, AND IT IS THE ROW'S CENTRAL
///         OBLIGATION RATHER THAN COPY.</b> docs/plan/12 line 310 says this row is <i>"positioned as
///         MySQL-compatible; the same honesty rule as FerretDB applies to the compatibility
///         claim"</i>, and that rule requires the page to say it is a compatibility layer and to carry
///         a supported-subset table. The sentence is <c>MariaDbServers.CompatibilityClaim</c>, the
///         table is <c>MariaDbServers.SupportedSubset</c>, and <c>MariaDbCompatibilityTests</c>
///         asserts that both reach this <c>Display</c> and the emitted document — a summary that
///         called this "managed MySQL" would be one line's edit and a churn event.
///     </para>
///     <para>
///         ⚠ <b>What is deliberately not declared:</b> no <c>servers/databases</c>,
///         <c>servers/roles</c> or <c>servers/firewallRules</c> — each is its own type with its own
///         reconciler, and declaring one with no reconciler puts a type in the registry that answers
///         <c>202</c> and converges nothing; no <c>regenerateKeys</c>, for the reason on
///         <c>MariaDbServers.ListKeysAction</c>; no <c>replicas</c> property, for the reason on
///         <c>MariaDbServers.GaleraReplicas</c>; and no <c>SupportsSoftDelete</c>.
///     </para>
///     <para>
///         ⚠ <b><c>SupportsSoftDelete</c> is what docs/plan/06 § Tags, locks asks for on a type
///         carrying data</b> — <i>"a dropped production database is not a support ticket you want to
///         have to say no to"</i>, with 7 days named. It is not declared because nothing in the
///         manager reads <c>SoftDeleteDays</c>, and declaring a recovery window the platform does not
///         honour would be a promise made to the one kind of type whose users would test it. This is
///         the second data-carrying provider to report the same gap, which is what turns the
///         PostgreSQL row's observation into a measurement — docs/plan/25 § R1.
///     </para>
///     <para>
///         ⚠ <b>That reasoning has since been read back and endorsed rather than overruled, which is
///         worth knowing before "fixing" it.</b> docs/plan/08 § Soft delete now records that all the
///         providers before this one declined for the same stated reason, calls the instinct right,
///         and ends: <i>"No provider should declare <c>SupportsSoftDelete</c> until the above is
///         built. The five stated reasons in the tree are correct and stay correct; the declaration is
///         the last step, not the first."</i> So this absence is a sixth instance of a decision, not a
///         sixth oversight — and what it now waits on is a named design (a deleted resource moving to
///         a different address, a separate purge permission, retention immutable after create) rather
///         than on nobody having got round to it.
///     </para>
/// </remarks>
public sealed class MariaDbProvider : IResourceProvider {
    /// <summary>
    ///     What the CLI's alias table would spell this row.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>NOT <c>mysql</c>, and the reason is this row's whole obligation rather than a naming
    ///     preference.</b> docs/plan/21 § Grammar's alias table maps a short form onto a long one, and
    ///     the long one here is <c>dbformysql server</c> — which <c>CliEmitter.GroupOf</c> derives from
    ///     the provider namespace and which nothing in this provider chooses. The SHORT form is the
    ///     name a person types and reads back in their own shell history, and <c>cyc mysql server
    ///     create</c> is a sentence that says the platform runs MySQL. <c>cyc mariadb server create</c>
    ///     says what is actually running, in the one place a tenant sees it most often.
    ///     <para>
    ///         ⚠ <b>It is also checked against every CLI group key as a literal</b>, in
    ///         <c>MariaDbDeclarationTests</c>. <c>CyberCloud.Storage</c> nearly shipped a short name of
    ///         <c>storage</c>, which is the group its own namespace already derives, and
    ///         System.CommandLine builds one dictionary over every command token and every alias — so
    ///         every <c>cyc</c> parse would have thrown before any verb ran. <c>dbformysql</c> is this
    ///         namespace's group key; <c>mariadb</c> is not it, and is not any other provider's either.
    ///     </para>
    /// </remarks>
    public const string ShortName = "mariadb";

    /// <summary>
    ///     The one-sentence summary the CLI, the portal blade and the generated document carry.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>It names MariaDB before it says compatible, and it points at the subset table.</b> A
    ///     summary reading "managed MySQL-compatible database" is the one that produces the churn
    ///     event docs/plan/12 § MongoDB-compatible describes: "compatible" is read as a synonym for
    ///     "is", and the correction arrives at the first <c>caching_sha2_password</c> handshake.
    /// </remarks>
    public const string Summary =
        "A managed MariaDB server on mariadb-operator, speaking the MySQL wire protocol, with Galera "
        + "for high availability. MariaDB is MySQL-compatible on a documented subset and is not "
        + "MySQL — see the supported-subset table before migrating.";

    /// <inheritdoc />
    public string ProviderNamespace => MariaDbServers.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType(MariaDbServers.TypePath)
            .ApiVersion(MariaDbServers.V2026, MariaDbServers.Schema2026)
            .Reconciler<MariaDbServerReconciler>()
            // ⚠ THE SAME FOUR METERS THE POSTGRESQL ROW DECLARES, AND THE MULTIPLIER IS HARDER HERE
            // RATHER THAN EASIER.
            //
            // That row's finding was that the true amount is `replicas × per-instance`, which is not
            // one value at one pointer — the per-instance figure is a Kubernetes quantity string named
            // indirectly by a sizing preset, and the count is a second pointer. On this type the count
            // is not a pointer AT ALL: `MariaDbServers.GaleraReplicas` explains why the instance count
            // could not be a body property (the CRD refuses an even Galera count and SchemaProperty
            // cannot spell "odd"), so the multiplier is derived from `/properties/highAvailability`, a
            // STRING naming a topology. A `Meter(meter, amountPointer, fallback)` had nothing to point
            // at in either factor.
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
                MariaDbServers.ListKeysAction,
                ActionKind.Post,
                MariaDbServers.ListKeysPermission,
                secret: true,
                response: MariaDbServers.ListKeysResponse
            )
            .Display("MariaDB server", "MariaDB servers", shortName: ShortName, summary: Summary)
            // docs/plan/12 § The pattern, once, piece 1 — and ADR-012's fifth surface, which is the
            // one binding that ties this registration to charts/managed/mariadb.
            .Chart(MariaDbServers.ChartName)
            .SupportsTags()
            .RequiresCluster(MariaDbServers.ClusterIdPointer);
    }

    // ── What a server draws ────────────────────────────────────────────────────────────────────
    //
    // ⚠ ALL THREE MULTIPLY BY THE INSTANCE COUNT, AND THE DEFAULT SHAPE IS THREE INSTANCES. Every
    // Galera member runs the whole `spec.resources` block and gets its own data PVC;
    // MariaDbServers.ServerJson writes both figures once because the CR is per-server, not because
    // the cost is. A per-instance reservation would be a third of the truth on the default body,
    // which is the worst ratio of any provider in the tree — the PostgreSQL row's default is two.
    //
    // ⚠ WHAT IS NOT COUNTED, AND IT IS NOT NOTHING. Under Galera the operator also runs an agent
    // container in each pod and, on recovery, a Job; with monitoring on it runs a mysqld-exporter
    // sidecar. None of them carries a `resources` block this provider writes, so there is no declared
    // figure to reserve and inventing one would be quota charging for a number that appears nowhere
    // in the data plane. It is a real under-count, it is bounded by the instance count, and the
    // honest place to close it is the chart growing those blocks — at which point this file gains
    // terms rather than a guess. The Valkey row made the same call about its pooler-shaped gap.

    /// <summary>vCPU: every instance's CPU request, from the explicit override or the preset.</summary>
    /// <remarks>
    ///     ⚠ Refuses rather than reserving zero when the quantity does not parse. That happens only if
    ///     <c>sizing.preset</c> names a preset <c>MariaDbServers.Presets</c> does not carry — which the
    ///     schema's <c>AllowedValues</c> makes unreachable from a validated body, and which is exactly
    ///     the drift worth failing on when somebody adds a preset to the enum and forgets the table.
    /// </remarks>
    static MeterDerivation VcpuDrawn { get; } =
        MeterDerivation.Of(
            "instances × sizing.cpu, in cores, where instances is 3 under Galera and 1 otherwise, "
            + "taking sizing.preset when the override is empty",
            [
                "/properties/highAvailability",
                "/properties/sizing/preset",
                "/properties/sizing/cpu"
            ],
            body => KubeQuantity.TryParse(MariaDbServers.Resources(body).Cpu, out var cores)
                ? Result<decimal>.Success(MariaDbServers.Replicas(body) * cores)
                : Unresolvable("cpu", "sizing.cpu or the sizing.preset behind it")
        );

    /// <summary>Memory: every instance's memory request, in gibibytes.</summary>
    static MeterDerivation MemoryDrawn { get; } =
        MeterDerivation.Of(
            "instances × sizing.memory, in GiB, where instances is 3 under Galera and 1 otherwise, "
            + "taking sizing.preset when the override is empty",
            [
                "/properties/highAvailability",
                "/properties/sizing/preset",
                "/properties/sizing/memory"
            ],
            body => KubeQuantity.TryGibibytes(MariaDbServers.Resources(body).Memory, out var gibibytes)
                ? Result<decimal>.Success(MariaDbServers.Replicas(body) * gibibytes)
                : Unresolvable("memory", "sizing.memory or the sizing.preset behind it")
        );

    /// <summary>Storage: every instance's data volume.</summary>
    /// <remarks>
    ///     ⚠ One term rather than the PostgreSQL row's two. That type has a separate write-ahead-log
    ///     volume; a <c>MariaDB</c> has one <c>spec.storage</c> and InnoDB's redo log lives inside it,
    ///     so there is no second volume to add and no empty-string case to get wrong.
    /// </remarks>
    static MeterDerivation StorageDrawn { get; } =
        MeterDerivation.Of(
            "instances × storage.size, in GiB, where instances is 3 under Galera and 1 otherwise",
            ["/properties/highAvailability", "/properties/storage/size"],
            body => KubeQuantity.TryGibibytes(MariaDbServers.StorageSize(body), out var data)
                ? Result<decimal>.Success(MariaDbServers.Replicas(body) * data)
                : Unresolvable("storage", "storage.size")
        );

    static Result<decimal> Unresolvable(string what, string where) =>
        Result<decimal>.Failure(
            ErrorCode.InternalError,
            $"The {what} a server draws could not be read from {where}: the value is not a Kubernetes "
            + "quantity. The write is refused rather than reserved at zero, because a resource that "
            + "provisions against no quota is one nobody is charged for — docs/plan/06 § Quota."
        );
}
