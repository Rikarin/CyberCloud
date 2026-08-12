// ⚠ For `Result<decimal>`, which the quota derivations below return. `CyberCloud.Core.Resources` is
// global here and `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins
// over the `Orleans.ErrorCode` this import would otherwise put back in play.
using CyberCloud.Core;

namespace CyberCloud.Providers.Messaging;

/// <summary>
///     Managed message brokers — <b>three</b> resource types in one provider namespace, each with one
///     api-version and one reconciler.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/12 § The catalogue: <i>"Kafka — <c>CyberCloud.Messaging/kafkaClusters</c>"</i>,
///         on Strimzi in KRaft mode, <i>"NATS — <c>CyberCloud.Messaging/natsClusters</c> · M1 ·
///         0.8 EM"</i>, and <i>"RabbitMQ — <c>CyberCloud.Messaging/rabbitmqClusters</c> · M2 ·
///         0.8 EM"</i> on the RabbitMQ Cluster Operator. ⚠ <b>The Kafka row is marked <c>M2</c>, not
///         <c>M1</c></b> — the M1 rows of that table are PostgreSQL, Valkey and NATS. Kafka was built
///         ahead of its milestone deliberately and the milestone was recorded rather than quietly
///         changed; NATS closes the M1 gap that left. RabbitMQ is an M2 row built in its own
///         milestone.
///     </para>
///     <para>
///         ⚠ <b>THIS NAMESPACE IS NOW THE PLATFORM'S FIRST WITH THREE RESOURCE TYPES IN IT, AND THE
///         THIRD MEASURED SOMETHING THE SECOND COULD ONLY PREDICT.</b> <c>MessagingSdkTests</c> named
///         <c>rabbitmqClusters</c> as the change most likely to break the generated surfaces, because
///         <c>SdkEmitter</c>'s first disambiguation tier prefixes the provider namespace and every
///         type here shares one. ⚠ <b>The prediction was right about the hazard and wrong about the
///         surface</b>: the SDK ladder is still not entered, because <c>Pascal("RabbitMQ cluster")</c>
///         is <c>RabbitMQCluster</c> and collides with neither sibling. What the third type <i>did</i>
///         exercise for the first time is <c>CliEmitter</c>'s group map at width three — a
///         <c>JsonObject</c> keyed by command name, whose indexer replaces silently — and that is now
///         asserted by count as well as by membership.
///     </para>
///     <para>
///         What the second type found remains true: three things held and one was false, and the
///         false one is the useful half:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>The registry, the router and the OpenAPI document needed nothing.</b>
///             <c>ProviderRegistry</c> keys on the full canonical name, so two types under one
///             namespace are two <c>FrozenDictionary</c> entries; the gateway resolves through
///             <c>ResourceId.ParsePath</c> rather than by counting; the emitted document gains one
///             more path group.
///         </item>
///         <item>
///             <b><c>SdkEmitter</c>'s collision ladder was the thing to check, and it holds.</b> Its
///             first tier prefixes the provider namespace's last segment, which cannot separate two
///             colliding types in the <i>same</i> namespace — the case this provider is the first to
///             have. The second tier falls back to the type path and the third throws. Nothing
///             collides here because <c>Kafka cluster</c> and <c>NATS cluster</c> are distinct
///             display names, so the ladder is not even entered; <c>MessagingSdkTests</c> asserts
///             both models are emitted and distinct, which is the claim that matters and is the one
///             a future third type in this namespace can break.
///         </item>
///         <item>
///             <b>One <c>.Application</c> module, one <c>.Conformance</c> project and one
///             <c>.Cluster.Conformance</c> project serve all three.</b> docs/plan/03's five-project
///             shape is per <i>namespace</i>, not per type, and this is the first evidence for that
///             rather than a restatement of it: a second type cost two case objects and four class
///             declarations, and no new project. ⚠ <b>The third cost the same again</b>, so the
///             marginal cost of a type in an existing namespace is flat rather than growing —
///             <c>CyberCloud.slnx</c>, <c>CyberCloud.Providers.slnf</c> and <c>module-layering.txt</c>
///             gained <b>no project rows at all</b> for this row, which is what the shape claimed and
///             nothing had shown twice.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>What was false: the reason <c>KafkaClusters</c> gives for declaring no sub-resources
///         no longer holds, and it is refuted here rather than left to rot.</b> That type's remarks
///         say <i>"this platform's resource-id grammar does not carry a parent instance"</i> and cite
///         <c>OpenApiEmitter</c> refusing Azure's interleaved shape. Both statements were true when
///         they were written and neither is true now: docs/plan/12 § Child resources landed on
///         2026-08-12, <see cref="ResourceId.Parent" /> is a pure function of the address, the parent
///         is checked at create, and <c>OpenApiEmitter.PathOf</c> emits the interleaved template.
///         <b>A child type is still not declared here, and the reasons are different ones</b> — see
///         the note on <see cref="NatsClusters" /> and <c>charts/managed/nats/conformance.yaml</c>
///         § owed, <c>child-types</c>.
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
            .RequiresCluster(KafkaClusters.ClusterIdPointer)

            // ── The second type, chained off the first exactly as docs/plan/08's example does ───
            .ResourceType(NatsClusters.TypePath)
            .ApiVersion(NatsClusters.V2026, NatsClusters.Schema2026)
            .Reconciler<NatsClusterReconciler>()
            // ⚠ THREE DERIVED METERS AND THE COUNT, WHERE THE TYPE ABOVE DECLARES THE COUNT ALONE.
            // The Kafka registration's note says the four meters it draws are undeclarable, and it
            // was right on the day it was written: `Meter(meter, pointer, fallback)` reserves the
            // NUMBER at a pointer, and every amount here is a Kubernetes quantity string, is usually
            // absent because `sizing.preset` names it indirectly, and is a PRODUCT of the server
            // count and a per-server quantity. `MeterDerivation` landed for
            // CyberCloud.DBforPostgreSQL/servers and closes all three of those, so the honest
            // under-declaration above is no longer the honest choice here.
            //
            // ⚠ EACH DERIVATION IS A PURE FUNCTION OF THE BODY AND MUST STAY ONE. The delete path
            // re-derives committed amounts from the resource's stored body through the same step the
            // create reserved with — ResourceManagerService.CommittedBy — so a derivation that read
            // a clock or configuration would make a delete return a different number than the create
            // committed, and quota would drift upward on every create/delete cycle.
            .Meter(QuotaMeter.Vcpu, NatsVcpuDrawn)
            .Meter(QuotaMeter.MemoryGb, NatsMemoryDrawn)
            .Meter(QuotaMeter.StorageGb, NatsStorageDrawn)
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Action(
                NatsClusters.ListKeysAction,
                ActionKind.Post,
                NatsClusters.ListKeysPermission,
                secret: true,
                response: NatsClusters.ListKeysResponse
            )
            .Display(
                "NATS cluster",
                "NATS clusters",
                shortName: "nats",
                summary: "A managed NATS cluster with JetStream on file storage, optional leaf-node "
                + "connectivity and an optional firewalled external listener."
            )
            .Chart(NatsClusters.ChartName)
            .SupportsTags()
            .RequiresCluster(NatsClusters.ClusterIdPointer)

            // ── The third type ──────────────────────────────────────────────────────────────────
            .ResourceType(RabbitmqClusters.TypePath)
            .ApiVersion(RabbitmqClusters.V2026, RabbitmqClusters.Schema2026)
            .Reconciler<RabbitmqClusterReconciler>()
            // ⚠ THE SAME FOUR THE NATS ROW DECLARES, AND THE THIRD SIGHTING IS WHERE THE PATTERN
            // STOPS BEING A FINDING AND BECOMES THE SHAPE. Every clustered data service in
            // docs/plan/12 draws vcpu, memoryGb and storageGb as a PRODUCT of a replica count and a
            // per-replica Kubernetes quantity, and none of the three is a number at a pointer. That
            // is what MeterDerivation exists for, and this row needed no new seam to declare them —
            // which is the first evidence that the seam generalises rather than fitting one type.
            //
            // ⚠ THE THREE ARE PREFIXED `Rabbitmq` AND THE NATS ONES GAINED A `Nats` PREFIX IN THE
            // SAME CHANGE, WHICH IS THE ONE EDIT THIS ROW MADE TO ANOTHER TYPE. Both sets were
            // called `VcpuDrawn`/`MemoryDrawn`/`StorageDrawn`/`Unresolvable`, which is a compile
            // error rather than a style problem — one class cannot declare a member twice. Prefixing
            // only the new set would have left a reader guessing which type an unqualified
            // `VcpuDrawn` belonged to, and a fourth type in this namespace would face the same
            // question again.
            .Meter(QuotaMeter.Vcpu, RabbitmqVcpuDrawn)
            .Meter(QuotaMeter.MemoryGb, RabbitmqMemoryDrawn)
            .Meter(QuotaMeter.StorageGb, RabbitmqStorageDrawn)
            .Meters(QuotaMeter.Resources)
            // ⚠ `publicIps` IS NOT DECLARED AND — UNIQUELY AMONG THE THREE TYPES HERE — THAT IS NOT A
            // GAP. The NATS registration above records that the blocker is
            // QuotaGrain.TryReserveAsync refusing a reservation of 0, so a meter deriving zero on the
            // default path would refuse every ordinary create. That argument does not need to be made
            // for this type at all: it declares no external listener, so there is no body that
            // consumes a public IP and no amount to derive. The reason is at
            // RabbitmqClusters.Schema2026 and it is about the operator's Service carrying three ports
            // on one type field, not about quota.
            .Permissions("read", "write", "delete")
            .Action(
                RabbitmqClusters.ListKeysAction,
                ActionKind.Post,
                RabbitmqClusters.ListKeysPermission,
                secret: true,
                response: RabbitmqClusters.ListKeysResponse
            )
            .Display(
                "RabbitMQ cluster",
                "RabbitMQ clusters",
                // ⚠ CHECKED AGAINST EVERY CLI GROUP KEY AS A LITERAL, NOT AGAINST THE SHORT NAMES.
                // ProviderRegistry.Build refuses a duplicate short name and never compares one to a
                // group NAME, and CliEmitter.GroupOf derives a group from the provider namespace's
                // last segment lower-cased. System.CommandLine's ValidTokens is one dictionary over
                // the whole tree, so a short name equal to any group would make EVERY `cyc` parse
                // throw — not just this type's. The groups in the tree today are `messaging`,
                // `dbforpostgresql`, `cache`, `storage` and `sample`; `rabbitmq` is none of them, and
                // RabbitmqDeclarationTests pins the comparison against typed-out literals so that
                // re-casing a namespace constant cannot leave it green.
                shortName: "rabbitmq",
                summary: "A managed RabbitMQ cluster on the RabbitMQ Cluster Operator, with quorum "
                + "queues as the default queue type and the management UI reachable only in-cluster."
            )
            .Chart(RabbitmqClusters.ChartName)
            .SupportsTags()
            .RequiresCluster(RabbitmqClusters.ClusterIdPointer);
    }

    // ── What a NATS cluster draws ──────────────────────────────────────────────────────────────
    //
    // ⚠ ALL THREE MULTIPLY BY `servers`, AND THAT IS THE PART A POINTER COULD NEVER HAVE SAID. The
    // StatefulSet runs `spec.replicas` pods; each carries the whole `resources` block and each gets
    // its own JetStream volume from the claim template. NatsClusters.StatefulSetJson writes both
    // figures once because a pod template is per-set, not because the cost is.
    //
    // ⚠ `publicIps` IS STILL NOT DECLARED, AND THE REASON THE KAFKA TYPE GIVES FOR THAT IS WRONG.
    // That note says publicIps "is derived from a flag, which no pointer can express however it is
    // read" — true of a pointer and false of the seam that exists now:
    //
    //     MeterDerivation.Of("external.enabled ? 1 : 0", ["/properties/external/enabled"], …)
    //
    // compiles, is pure, and declares its read set. It was written, and then not shipped, because of
    // something one layer down: QuotaGrain.TryReserveAsync refuses a non-positive amount outright —
    // "A reservation must be positive; 0 is not" — so a meter whose amount is zero in the ordinary
    // case would refuse EVERY CREATE that did not ask for external exposure, which is the default
    // and the overwhelming majority. A MeterRegistration has no "skip when the amount is zero", and
    // adding one is a change to CyberCloud.ResourceManager plus a decision about whether a zero
    // reservation should be a no-op lease or no lease at all — docs/plan/25 § R1 keeps commits to
    // that assembly made by a provider PR at zero, so this is the finding rather than the fix.
    //
    // The correction matters beyond this type: EVERY service in docs/plan/12 § Cross-cutting
    // decisions has an optional external listener, so all ten of them meet this, and the reason
    // written down until now would send the next author looking at the wrong file.

    /// <summary>vCPU: every server's CPU request, from the explicit override or the preset.</summary>
    /// <remarks>
    ///     ⚠ Refuses rather than reserving zero when the quantity does not parse. That happens only if
    ///     <c>sizing.preset</c> names a preset <see cref="NatsClusters.Presets" /> does not carry —
    ///     which the schema's <c>AllowedValues</c> makes unreachable from a validated body, and which
    ///     is exactly the drift worth failing on when somebody adds a preset to the enum and forgets
    ///     the table.
    /// </remarks>
    static MeterDerivation NatsVcpuDrawn { get; } =
        MeterDerivation.Of(
            "servers × sizing.cpu, in cores, taking sizing.preset when the override is empty",
            ["/properties/servers", "/properties/sizing/preset", "/properties/sizing/cpu"],
            body => KubeQuantity.TryParse(NatsClusters.Resources(body).Cpu, out var cores)
                ? Result<decimal>.Success(NatsClusters.Servers(body) * cores)
                : NatsUnresolvable("cpu", "sizing.cpu or the sizing.preset behind it")
        );

    /// <summary>Memory: every server's memory request, in gibibytes.</summary>
    static MeterDerivation NatsMemoryDrawn { get; } =
        MeterDerivation.Of(
            "servers × sizing.memory, in GiB, taking sizing.preset when the override is empty",
            ["/properties/servers", "/properties/sizing/preset", "/properties/sizing/memory"],
            body => KubeQuantity.TryGibibytes(NatsClusters.Resources(body).Memory, out var gibibytes)
                ? Result<decimal>.Success(NatsClusters.Servers(body) * gibibytes)
                : NatsUnresolvable("memory", "sizing.memory or the sizing.preset behind it")
        );

    /// <summary>Storage: every server's JetStream file-store volume, in gibibytes.</summary>
    /// <remarks>
    ///     ⚠ <b><c>jetstream.maxMemoryStore</c> is deliberately not added in.</b> It is a ceiling on
    ///     memory-backed streams and it is taken out of the container's memory limit, which
    ///     <see cref="NatsMemoryDrawn" /> has already reserved. Adding it here would charge the same
    ///     gibibyte to two meters, and the one place a customer would notice is the bill.
    /// </remarks>
    static MeterDerivation NatsStorageDrawn { get; } =
        MeterDerivation.Of(
            "servers × storage.size, in GiB",
            ["/properties/servers", "/properties/storage/size"],
            body => KubeQuantity.TryGibibytes(NatsClusters.StorageSize(body), out var gibibytes)
                ? Result<decimal>.Success(NatsClusters.Servers(body) * gibibytes)
                : NatsUnresolvable("storage", "storage.size")
        );

    static Result<decimal> NatsUnresolvable(string what, string where) =>
        Result<decimal>.Failure(
            ErrorCode.InternalError,
            $"The {what} a NATS cluster draws could not be read from {where}: the value is not a "
            + "Kubernetes quantity. The write is refused rather than reserved at zero, because a "
            + "resource that provisions against no quota is one nobody is charged for — docs/plan/06 "
            + "§ Quota."
        );

    // ── What a RabbitMQ cluster draws ──────────────────────────────────────────────────────────
    //
    // ⚠ ALL THREE MULTIPLY BY `nodes`, AND ON THIS TYPE THE STORAGE ONE IS THE LEAST OBVIOUS. A
    // quorum queue is a Raft group whose every member holds the WHOLE log, so `storage.size` is the
    // volume each node needs rather than a share of one cluster-wide figure — the same arithmetic as
    // the NATS row and for a different reason. Splitting it by node count, which is what a reader
    // used to a sharded store would expect, would under-reserve by a factor of the replica count.
    //
    // ⚠ EACH DERIVATION IS A PURE FUNCTION OF THE BODY AND MUST STAY ONE. The delete path re-derives
    // committed amounts from the resource's stored body through the same step the create reserved
    // with — ResourceManagerService.CommittedBy — so a derivation that read a clock or configuration
    // would make a delete return a different number than the create committed, and quota would drift
    // upward on every create/delete cycle.

    /// <summary>vCPU: every node's CPU request, from the explicit override or the preset.</summary>
    /// <remarks>
    ///     ⚠ Refuses rather than reserving zero when the quantity does not parse. That happens only if
    ///     <c>sizing.preset</c> names a preset <see cref="RabbitmqClusters.Presets" /> does not carry
    ///     — which the schema's <c>AllowedValues</c> makes unreachable from a validated body.
    ///     <para>
    ///         ⚠ <b>The refusal is load-bearing on this type in a way it is not on the other two.</b>
    ///         <c>RabbitmqClusters.ClusterJson</c> writes no <c>resources</c> block when the preset
    ///         does not resolve, and this CRD <i>defaults</i> that field to a Burstable block at
    ///         quantities nobody chose. So the branch this meter refuses is the branch that would
    ///         otherwise provision a differently-sized, differently-classed pod against no quota at
    ///         all. Refusing the write is what keeps that unreachable.
    ///     </para>
    /// </remarks>
    static MeterDerivation RabbitmqVcpuDrawn { get; } =
        MeterDerivation.Of(
            "nodes × sizing.cpu, in cores, taking sizing.preset when the override is empty",
            ["/properties/nodes", "/properties/sizing/preset", "/properties/sizing/cpu"],
            body => KubeQuantity.TryParse(RabbitmqClusters.Resources(body).Cpu, out var cores)
                ? Result<decimal>.Success(RabbitmqClusters.Nodes(body) * cores)
                : RabbitmqUnresolvable("cpu", "sizing.cpu or the sizing.preset behind it")
        );

    /// <summary>Memory: every node's memory request, in gibibytes.</summary>
    static MeterDerivation RabbitmqMemoryDrawn { get; } =
        MeterDerivation.Of(
            "nodes × sizing.memory, in GiB, taking sizing.preset when the override is empty",
            ["/properties/nodes", "/properties/sizing/preset", "/properties/sizing/memory"],
            body => KubeQuantity.TryGibibytes(RabbitmqClusters.Resources(body).Memory, out var gibibytes)
                ? Result<decimal>.Success(RabbitmqClusters.Nodes(body) * gibibytes)
                : RabbitmqUnresolvable("memory", "sizing.memory or the sizing.preset behind it")
        );

    /// <summary>Storage: every node's message-store volume, in gibibytes.</summary>
    static MeterDerivation RabbitmqStorageDrawn { get; } =
        MeterDerivation.Of(
            "nodes × storage.size, in GiB",
            ["/properties/nodes", "/properties/storage/size"],
            body => KubeQuantity.TryGibibytes(RabbitmqClusters.StorageSize(body), out var gibibytes)
                ? Result<decimal>.Success(RabbitmqClusters.Nodes(body) * gibibytes)
                : RabbitmqUnresolvable("storage", "storage.size")
        );

    static Result<decimal> RabbitmqUnresolvable(string what, string where) =>
        Result<decimal>.Failure(
            ErrorCode.InternalError,
            $"The {what} a RabbitMQ cluster draws could not be read from {where}: the value is not a "
            + "Kubernetes quantity. The write is refused rather than reserved at zero, because a "
            + "resource that provisions against no quota is one nobody is charged for — docs/plan/06 "
            + "§ Quota."
        );
}
