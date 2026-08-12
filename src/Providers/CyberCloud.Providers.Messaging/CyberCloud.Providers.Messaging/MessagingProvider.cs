namespace CyberCloud.Providers.Messaging;

/// <summary>
///     Managed Apache Kafka — one resource type, one api-version, one reconciler.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/12 § The catalogue: <i>"Kafka — <c>CyberCloud.Messaging/kafkaClusters</c>"</i>,
///         on Strimzi in KRaft mode. ⚠ <b>That row is marked <c>M2</c>, not <c>M1</c></b> — the M1
///         rows of that table are PostgreSQL, Valkey and NATS, and the M1 event-streaming service is
///         <c>CyberCloud.Messaging/natsClusters</c>. This is built ahead of its milestone
///         deliberately, and the milestone is recorded here rather than quietly changed in the plan.
///     </para>
///     <para>
///         ⚠ <b>The catalogue's sub-resources are not declared, and the reason is the platform's
///         resource-id grammar rather than scope.</b> docs/plan/12 says <i>"Topics and users as
///         sub-resources — Strimzi's <c>KafkaTopic</c> and <c>KafkaUser</c> CRDs map to resource
///         types almost one to one, which is why this is 1.2 and not 2.5"</i>. The mapping is one to
///         one at the CRD; it is not one to one at the <i>address</i>. See the remarks on
///         <see cref="KafkaClusters" /> and src/Providers/README.md § What the third provider
///         measured for what a nested type would and would not be here.
///     </para>
///     <para>
///         ⚠ <b>Two of docs/plan/12 § The pattern, once's eight pieces are not built and are named
///         rather than implied.</b> Piece 5 — credential provisioning into the tenant's Vault — needs
///         an OpenBao integration that does not exist, so <c>listKeys</c> has a declared response
///         shape and no handler. Piece 6 — the scrape object — is the case that document's own
///         correction describes as the fallback: <b>Strimzi does not emit a <c>PodMonitor</c> or a
///         <c>ServiceMonitor</c> of its own</b>, unlike CloudNativePG, so "ask the operator for the
///         scrape object wherever the operator accepts the request" has no request to make here. What
///         the operator does accept is <c>spec.kafkaExporter</c>, which makes the metrics <i>exist</i>;
///         the object that scrapes them is ours and is owed. That is the first time the second branch
///         of the corrected piece 6 has had a service to be true of.
///     </para>
///     <para>
///         ⚠ <b>No <c>SupportsSoftDelete</c>, for the reason
///         <c>CyberCloud.DBforPostgreSQL/servers</c> gives</b>: nothing in the manager reads
///         <c>SoftDeleteDays</c>, and declaring a recovery window the platform does not honour would
///         be a promise made to the users most likely to test it. <c>/properties/storage/deleteClaim</c>
///         defaulting to <see langword="false" /> is the honest partial answer — the volumes outlive a
///         mistaken delete even though the resource does not.
///     </para>
/// </remarks>
public sealed class MessagingProvider : IResourceProvider {
    /// <inheritdoc />
    public string ProviderNamespace => KafkaClusters.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType(KafkaClusters.TypePath)
            .ApiVersion(KafkaClusters.V2026, KafkaClusters.Schema2026)
            .Reconciler<KafkaClusterReconciler>()
            // ⚠ ONE UNIT OF `resources`, AND THE FOUR METERS THIS TYPE OBVIOUSLY DRAWS ARE NOT
            // DECLARED. THIS IS THE SECOND PROVIDER TO REPORT THE SAME REGISTRY GAP, WHICH IS WHAT
            // TURNS IT FROM AN ANECDOTE INTO A MEASUREMENT.
            //
            // docs/plan/06 § Quota's families are vcpu, memoryGb, storageGb, publicIps, clusters and
            // resources, and `Meter(meter, amountPointer, fallback)` reserves the NUMBER it finds at
            // a JSON Pointer. A Kafka cluster draws all four of vcpu, memoryGb, storageGb and —
            // uniquely so far — publicIps, and only one of the amounts is a number in this body:
            //
            //   vcpu       = nodes × sizing.cpu     — "1" or "500m", a string, and usually absent
            //                                         because sizing.preset names it indirectly
            //   memoryGb   = nodes × sizing.memory  — "2Gi", a string
            //   storageGb  = nodes × storage.size   — "100Gi", a string
            //   publicIps  = external.enabled ? 1 : 0 — a BOOLEAN, and the arithmetic is a condition
            //                                         rather than a quantity
            //
            // What CyberCloud.DBforPostgreSQL/servers found was that a pointer needs a UNIT so a
            // Kubernetes quantity string can be read as a number. This type adds two facts to that
            // finding. First, every amount here is also a PRODUCT — one node's quantity times the
            // node count — and `Meter` multiplies by nothing, so a unit alone would still reserve a
            // third of what a three-node cluster costs. Second, `publicIps` is not a quantity at all:
            // it is derived from a flag, which no pointer can express however it is read.
            //
            // Declaring `Meter(QuotaMeter.StorageGb, "/properties/storage/size")` would reserve
            // against a pointer holding a string; declaring it against "/properties/nodes" would
            // reserve the node count as gigabytes. Both are worse than the honest under-declaration
            // below, which reserves one `resources` unit and says so. Closing it is a change to
            // CyberCloud.ResourceManager — docs/plan/25 § R1 keeps "commits to
            // CyberCloud.ResourceManager made by a provider PR" at zero, and this note is how the
            // finding survives without one.
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Action(
                KafkaClusters.ListKeysAction,
                ActionKind.Post,
                KafkaClusters.ListKeysPermission,
                secret: true,
                response: KafkaClusters.ListKeysResponse
            )
            .Display(
                "Kafka cluster",
                "Kafka clusters",
                shortName: "kafka",
                summary: "A managed Apache Kafka cluster on Strimzi in KRaft mode, with Cruise "
                + "Control, configurable retention and an optional firewalled external listener."
            )
            .Chart(KafkaClusters.ChartName)
            .SupportsTags()
            .RequiresCluster(KafkaClusters.ClusterIdPointer);
    }
}
