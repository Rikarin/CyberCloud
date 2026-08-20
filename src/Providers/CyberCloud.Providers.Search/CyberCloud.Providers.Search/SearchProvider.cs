// ⚠ For `Result<decimal>`, which the quota derivations below return. `CyberCloud.Core.Resources` is
// global here and `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins
// over the `Orleans.ErrorCode` this import would otherwise put back in play.
using CyberCloud.Core;
using System.Text.Json;

namespace CyberCloud.Providers.Search;

/// <summary>
///     Managed search — docs/plan/12 § The catalogue's <c>CyberCloud.Search/services</c>.
/// </summary>
/// <remarks>
///     <para>
///         [12 § The catalogue](../../../../docs/plan/12-managed-data-services.md): <i>"OpenSearch —
///         <c>CyberCloud.Search/services</c> · M3 · 1.0 EM. <b>OpenSearch operator</b> (Apache-2.0,
///         ADR-011 — Elasticsearch is not available to us). Data/master/coordinating node roles, ISM
///         policies, snapshot repository into the tenant's bucket."</i>
///     </para>
///     <para>
///         ⚠ <b>THE SIXTH PROVIDER NAMESPACE AND THE FIRST <c>M3</c> ROW.</b> The five before it are
///         <c>M1</c> and <c>M2</c> rows plus docs/plan/15's object storage. What that changes is
///         nothing structural — four module edges, six projects, one <c>ProviderConformanceCase</c> per
///         type — and the sameness is the measurement, because a milestone is a scheduling fact rather
///         than a shape.
///     </para>
///     <para>
///         ⚠ <b>THE QUOTA METERS ARE A SUM OVER HETEROGENEOUS COMPONENTS — THE SHAPE
///         <c>CyberCloud.Storage/accounts</c> FOUND — AND THIS IS THE SECOND SIGHTING, WHICH IS WHAT
///         MAKES IT A PATTERN.</b> The three shapes <c>MeterDerivation</c> has now been asked for, in
///         the order they arrived:
///     </para>
///     <list type="number">
///         <item>
///             <c>CyberCloud.DBforPostgreSQL/servers</c> — an amount is a Kubernetes quantity
///             <i>string</i>, and <c>Meter(meter, pointer)</c> reads a number.
///         </item>
///         <item>
///             <c>CyberCloud.Messaging/natsClusters</c> — an amount is a <i>product</i> of a replica
///             count and one per-replica figure, and <c>Meter</c> multiplies by nothing.
///         </item>
///         <item>
///             <c>CyberCloud.Storage/accounts</c> — an amount is a <i>sum over populations that are
///             not the same size as each other</i>. Here it is
///             <c>(dataNodes + coordinatingNodes) × preset + masterNodes × 500m</c>: two populations,
///             only one of which the tenant sizes.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>And this type adds a fourth fact to that list, which is the one worth having: a term
///         of the sum is ZERO on the default body and the sum is not.</b>
///         <c>/properties/coordinatingNodes</c> defaults to <c>0</c>, so the coordinating term derives
///         nothing on an ordinary create.
///         <c>CyberCloud.Messaging/natsClusters</c> records that <c>QuotaGrain.TryReserveAsync</c>
///         refuses a non-positive amount — <i>"A reservation must be positive; 0 is not"</i> — and
///         concludes that a conditional meter is undeclarable. That conclusion is <b>about a whole
///         meter and not about a term</b>, and the distinction had never been tested because no
///         earlier type had an optional population. A meter whose <i>total</i> has a floor may contain
///         a term that is zero; what it may not do is <i>be</i> zero.
///         <c>OpenSearchQuotaTests.NoMeterEverDerivesZeroEvenWithNoCoordinatingNodes</c> is the pin.
///     </para>
///     <para>
///         ⚠ <b>EACH DERIVATION IS A PURE FUNCTION OF THE BODY AND MUST STAY ONE.</b> The delete path
///         re-derives committed amounts from the resource's stored body through the same step the
///         create reserved with — <c>ResourceManagerService.CommittedBy</c> — so a derivation that read
///         a clock or configuration would make a delete return a different number than the create
///         committed, and quota would drift upward on every create/delete cycle.
///     </para>
///     <para>
///         ⚠ <b><c>publicIps</c> is not declared, and the reason is the FIRST of the two this tree now
///         records rather than the second.</b> <c>CyberCloud.Storage/accounts</c> found that the two
///         reasons look identical from the meter: either the operator has no exposure field to
///         condition on (its blocker, an upstream change), or there is one and a conditional meter
///         derives zero on the default path (<c>natsClusters</c>' blocker, a
///         <i>skip-when-zero</i> on <c>MeterRegistration</c>). This type is the <c>natsClusters</c>
///         case: the operator's <c>ServiceSpec</c> does carry a service <c>type</c>, so exposure is
///         expressible — <c>conformance.yaml § owed</c>, <c>external-exposure</c>, is why it is still
///         not offered, and it is the firewall list rather than the meter that blocks it.
///     </para>
///     <para>
///         ⚠ <b>The short name is <c>opensearch</c> and the obvious <c>search</c> is a hard
///         collision.</b> <c>CliEmitter.GroupOf</c> is the provider namespace's last segment
///         lower-cased, so <c>CyberCloud.Search</c> is already the group <c>search</c> — and a short
///         name equal to its <i>own</i> group's key gives <c>cyc search search</c> two meanings, which
///         <c>System.CommandLine</c> throws on for every parse that reaches the group.
///         <c>CyberCloud.Storage/accounts</c> found this and shipped as <c>objectstore</c>; this is
///         the second namespace whose natural short name is its own group name, and the first to have
///         <b>two</b> short names to keep clear of it and of each other. <c>CliTokens</c> carries the
///         rule and <c>CliTokenTests</c> carries the measurements;
///         <c>OpenSearchDeclarationTests.NoShortNameHereGivesACycTokenTwoMeanings</c> asks the
///         derived question for this provider.
///     </para>
///     <para>
///         ⚠ <b>THIS NAMESPACE IS DESIGNED FOR TWO TYPES AND SHIPS ONE.</b> docs/plan/12 § The
///         catalogue's other row here is <i>"Qdrant — <c>CyberCloud.Search/vectorStores</c> · M3 ·
///         0.6 EM. Not an Azure row. A 2026 catalogue without a vector store is dated on arrival, and
///         Qdrant's operator model is simple enough that this is the cheapest M3 item."</i> It is not
///         declared, and what a second type in an existing namespace costs is already measured —
///         <c>CyberCloud.Messaging/natsClusters</c> put it at <i>"two case objects and four class
///         declarations, and no new project"</i>. So the reason is scope and not structure, and the
///         one thing a future author should not have to rediscover is this:
///     </para>
///     <list type="bullet">
///         <item>
///             ⚠ <b>THERE IS NO PUBLIC QDRANT OPERATOR, AND THAT SENTENCE IN docs/plan/12 IS THE ONE
///             CLAIM IN THAT ROW THAT DOES NOT HOLD.</b> <c>github.com/qdrant/qdrant-operator</c>
///             answers <c>404</c>. The operator that exists is the one Qdrant Managed Cloud, Hybrid
///             Cloud and Private Cloud run, and Qdrant's own Private Cloud documentation describes it
///             as sitting <i>"on top of the open source Qdrant database"</i> — the database is
///             Apache-2.0, the operator is not distributed. ⚠ <b>ADR-010 clause 1's survey is
///             consistent with this and reads differently once it is known:</b> that list names an
///             <i>operator</i> for most rows — <i>"CloudNativePG", "Altinity", "Strimzi",
///             "spotahome", "mariadb-operator", "RabbitMQ Cluster Operator", "OpenSearch
///             operator"</i> — and for this one it names only <i>"Qdrant"</i>.
///         </item>
///         <item>
///             So <c>vectorStores</c> is the <b>operator-less</b> shape, which is
///             <c>CyberCloud.Messaging/natsClusters</c>' and its second sighting. The public path is
///             <c>qdrant/qdrant-helm</c> (Apache-2.0, active), which renders a <c>StatefulSet</c>, a
///             headless and a client <c>Service</c>, and a <c>ConfigMap</c> — four objects, against
///             this type's one. Everything a controller would have defaulted becomes a decision, and
///             the cluster-backed suite needs <b>no</b> CRD stub at all, where this type needs one.
///         </item>
///         <item>
///             ⚠ <b>Its credential story is the third of the three this catalogue now has, and it is
///             the dangerous one.</b> This type's operator <i>generates</i> a password;
///             <c>CyberCloud.Storage/accounts</c> has no credential at all and visibly does not
///             converge. Qdrant's chart leaves <c>service.api_key</c> <b>unset by default</b>, and a
///             Qdrant with no API key serves every request on port 6333 unauthenticated. That is the
///             SeaweedFS hazard reached through a chart default rather than through an engine's
///             fallback, and it means <c>vectorStores</c> cannot ship an honest default until piece 5
///             lands — which is a harder constraint than this type had, and is the thing to settle
///             before writing any of it.
///         </item>
///         <item>
///             The short name is free: <c>qdrant</c> collides with nothing this provider declares,
///             and <c>OpenSearchDeclarationTests.NoShortNameHereGivesACycTokenTwoMeanings</c> is what
///             asks. ⚠ It no longer carries a list to check a second name against — the rule is
///             derived from what is registered, by <c>CliTokens</c>, so a new type is checked by
///             declaring it rather than by being added anywhere.
///         </item>
///     </list>
/// </remarks>
public sealed class SearchProvider : IResourceProvider {
    /// <inheritdoc />
    public string ProviderNamespace => OpenSearchServices.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType(OpenSearchServices.TypePath)
            .ApiVersion(OpenSearchServices.V2026, OpenSearchServices.Schema2026)
            .Reconciler<OpenSearchServiceReconciler>()
            .Meter(QuotaMeter.Vcpu, VcpuDrawn)
            .Meter(QuotaMeter.MemoryGb, MemoryDrawn)
            .Meter(QuotaMeter.StorageGb, StorageDrawn)
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Action(
                OpenSearchServices.ListKeysAction,
                ActionKind.Post,
                OpenSearchServices.ListKeysPermission,
                secret: true,
                response: OpenSearchServices.ListKeysResponse,
                handler: typeof(OpenSearchServiceListKeysHandler)
            )
            .Display(
                "OpenSearch service",
                "OpenSearch services",
                shortName: "opensearch",
                summary: "A managed OpenSearch cluster on the OpenSearch operator, with dedicated "
                + "cluster-manager, data and optional coordinating node pools and operator-generated "
                + "transport and HTTP TLS."
            )
            .Chart(OpenSearchServices.ChartName)
            .SupportsTags()
            .RequiresCluster(OpenSearchServices.ClusterIdPointer);
    }

    // ── What a search service draws ────────────────────────────────────────────────────────────
    //
    // ⚠ THE TENANT SIZES TWO OF THE THREE POOLS AND THE PLATFORM SIZES THE THIRD, WHICH IS WHY
    // NEITHER `replicas × preset` NOR `replicas × fixed` IS RIGHT ON ITS OWN. A derivation copied from
    // CyberCloud.Messaging/natsClusters would be right about the data nodes and would miss three JVMs
    // on the default body; one copied from the coordinating pool's side would miss every shard.
    //
    // ⚠ AND THE COORDINATING TERM IS ZERO ON THE DEFAULT BODY. That is legal here and is the finding
    // this type contributes — see the type's own remarks. What makes it legal is that the data term
    // has a floor of one node and the cluster-manager term a floor of one node, so the SUM cannot be
    // zero for any body the schema accepts.

    /// <summary>vCPU: the sized pools at their preset, plus a fixed share per cluster-manager node.</summary>
    /// <remarks>
    ///     ⚠ Refuses rather than reserving zero when the quantity does not parse. That happens only if
    ///     <c>sizing.preset</c> names a preset <see cref="OpenSearchServices.Presets" /> does not carry
    ///     — which the schema's <c>AllowedValues</c> makes unreachable from a validated body, and which
    ///     is exactly the drift worth failing on when somebody adds a preset to the enum and forgets
    ///     the table.
    /// </remarks>
    static MeterDerivation VcpuDrawn { get; } =
        MeterDerivation.Of(
            "(dataNodes + coordinatingNodes) × sizing.cpu (from sizing.preset when unset) + "
            + "masterNodes × 500m, in cores",
            [
                "/properties/dataNodes",
                "/properties/coordinatingNodes",
                "/properties/masterNodes",
                "/properties/sizing/preset",
                "/properties/sizing/cpu"
            ],
            body => KubeQuantity.TryParse(OpenSearchServices.Resources(body).Cpu, out var cores)
            && KubeQuantity.TryParse(OpenSearchServices.ControlPlaneCpu, out var share)
                ? Result<decimal>.Success(
                    (SizedNodes(body) * cores) + (OpenSearchServices.MasterNodes(body) * share)
                )
                : Unresolvable("cpu", "sizing.cpu or the sizing.preset behind it")
        );

    /// <summary>Memory: the same two populations, in gibibytes.</summary>
    /// <remarks>
    ///     ⚠ <b>The JVM heap is deliberately not added in.</b> OpenSearch derives its heap from the
    ///     container's memory limit, so the heap is a <i>share</i> of the figure this meter already
    ///     reserves rather than an addition to it. Adding it would charge the same gibibyte twice, and
    ///     the one place a customer would notice is the bill.
    /// </remarks>
    static MeterDerivation MemoryDrawn { get; } =
        MeterDerivation.Of(
            "(dataNodes + coordinatingNodes) × sizing.memory (from sizing.preset when unset) + "
            + "masterNodes × 2Gi, in GiB",
            [
                "/properties/dataNodes",
                "/properties/coordinatingNodes",
                "/properties/masterNodes",
                "/properties/sizing/preset",
                "/properties/sizing/memory"
            ],
            body => KubeQuantity.TryGibibytes(OpenSearchServices.Resources(body).Memory, out var gibibytes)
            && KubeQuantity.TryGibibytes(OpenSearchServices.ControlPlaneMemory, out var share)
                ? Result<decimal>.Success(
                    (SizedNodes(body) * gibibytes) + (OpenSearchServices.MasterNodes(body) * share)
                )
                : Unresolvable("memory", "sizing.memory or the sizing.preset behind it")
        );

    /// <summary>Storage: each data node's disk, plus the fixed volume every other node gets.</summary>
    /// <remarks>
    ///     ⚠ <b>The populations split differently here than they do for CPU and memory, and that is
    ///     the point of writing three derivations rather than one parameterised one.</b> A
    ///     coordinating node is sized like a <i>data</i> node for CPU and memory — it merges result
    ///     sets and that costs both — and like a <i>cluster-manager</i> node for disk, because it
    ///     holds no shards. A derivation that reused one split for all three would over-reserve every
    ///     coordinating node's disk by the tenant's whole <c>storage.size</c>.
    ///     <para>
    ///         ⚠ <b>Not multiplied by any replica factor.</b> OpenSearch's index-level
    ///         <c>number_of_replicas</c> decides how many copies of a shard are spread <i>across</i>
    ///         these same disks rather than adding any, and it is an index setting this type does not
    ///         own — see <c>conformance.yaml § owed</c>, <c>ism-policies</c>. This is the same
    ///         double-count <c>CyberCloud.Storage/accounts</c>' storage meter refuses.
    ///     </para>
    /// </remarks>
    static MeterDerivation StorageDrawn { get; } =
        MeterDerivation.Of(
            "dataNodes × storage.size + (masterNodes + coordinatingNodes) × 10Gi, in GiB",
            [
                "/properties/dataNodes",
                "/properties/masterNodes",
                "/properties/coordinatingNodes",
                "/properties/storage/size"
            ],
            body => KubeQuantity.TryGibibytes(OpenSearchServices.StorageSize(body), out var gibibytes)
            && KubeQuantity.TryGibibytes(OpenSearchServices.ControlPlaneVolumeSize, out var fixedVolume)
                ? Result<decimal>.Success(
                    (OpenSearchServices.DataNodes(body) * gibibytes)
                    + (UnsizedVolumes(body) * fixedVolume)
                )
                : Unresolvable("storage", "storage.size")
        );

    /// <summary>How many nodes are sized by the tenant's preset.</summary>
    /// <remarks>
    ///     ⚠ The coordinating term is <b>zero on the default body</b> and the total is not — see this
    ///     provider's own remarks for why that distinction is the thing this type establishes.
    /// </remarks>
    static int SizedNodes(JsonElement body) =>
        OpenSearchServices.DataNodes(body) + OpenSearchServices.CoordinatingNodes(body);

    /// <summary>How many nodes get the fixed <c>10Gi</c> volume rather than the tenant's disk size.</summary>
    static int UnsizedVolumes(JsonElement body) =>
        OpenSearchServices.MasterNodes(body) + OpenSearchServices.CoordinatingNodes(body);

    static Result<decimal> Unresolvable(string what, string where) =>
        Result<decimal>.Failure(
            ErrorCode.InternalError,
            $"The {what} a search service draws could not be read from {where}: the value is not a "
            + "Kubernetes quantity. The write is refused rather than reserved at zero, because a "
            + "resource that provisions against no quota is one nobody is charged for — docs/plan/06 "
            + "§ Quota."
        );
}
