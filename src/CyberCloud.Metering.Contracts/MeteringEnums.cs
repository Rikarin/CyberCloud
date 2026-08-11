namespace CyberCloud.Metering.Contracts;

/// <summary>
///     The two kinds of meter — docs/plan/22 § Two kinds of meter, and the distinction that prevents
///     most bugs.
/// </summary>
/// <remarks>
///     ⚠ <b>Choosing the wrong one is not a style error, it is a revenue error, and it is wrong in a
///     different direction each way.</b> An event-based treatment of a state-based meter "would miss
///     a resource that exists but never changes" — a disk nobody has touched since it was created
///     produces no events and is billed at zero. A sampled treatment of an event-based meter "would
///     miss everything between samples" — 4 999 of every 5 000 requests. The catalogue
///     (<see cref="MeterCatalog" />) fixes the kind per meter so the decision is made once, in one
///     place, rather than per provider.
/// </remarks>
[Alias("CyberCloud.Metering.MeterKind")]
public enum MeterKind {
    /// <summary>Never assigned. The zero value a default-constructed wire type carries.</summary>
    Unknown = 0,

    /// <summary>
    ///     A stock integrated over a window — vCPU-hours, GB-months, IP-hours. Produced by the
    ///     sampler, from the platform's own record of what exists.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Never from Kubernetes metrics</b> (docs/plan/22 § Two kinds of meter). "A stopped VM
    ///     still has a disk; a <c>Deployment</c> scaled to zero still has a
    ///     <c>PersistentVolumeClaim</c>. Metrics know about running pods; the resource graph knows
    ///     what exists." The type system carries that rule as far as it can:
    ///     <see cref="IMeteredResourceSource" /> is a view of the resource graph, and the one field
    ///     on <c>MeteredResource</c> that describes running-ness is marked as an output.
    /// </remarks>
    StateBased = 1,

    /// <summary>
    ///     A count of things that happened — requests, egress GB, SMS sent, scans run. Emitted by
    ///     the provider at the moment, through <see cref="IUsageEmitter" />.
    /// </summary>
    EventBased = 2
}

/// <summary>
///     A billing meter — the quantity docs/plan/22's pipeline carries, priced in M2.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>These names are part of the idempotency key and are therefore immutable.</b>
///         <see cref="UsageEvent.KeyFor" /> hashes the member's name, so renaming a member here
///         renames every key derived from it and a redelivery across the rename would no longer
///         collapse — the exact double-count docs/plan/22 opens by forbidding. Adding a member is
///         free; renaming one is a data migration. The name is also the <c>{meter}</c> in
///         <c>cc.{tenant}.usage.{meter}</c> (docs/plan/04 § Streams).
///     </para>
///     <para>
///         <b>This is not <see cref="QuotaMeter" /> and it must not become it.</b> A quota family is
///         a <i>stock</i> — a quantity that exists at an instant and can be reserved. A billing
///         meter is a <i>flow</i> — that stock integrated over time, or a count of events. "A
///         GB-month is not a reservable quantity", which is why <c>IQuotaGrain</c> takes families
///         and this enum is separate. <see cref="MeterCatalog" /> is the total function between
///         them, and it is the only place the two vocabularies meet.
///     </para>
/// </remarks>
[Alias("CyberCloud.Metering.BillingMeter")]
public enum BillingMeter {
    /// <summary>Never assigned, and rejected everywhere. The zero value.</summary>
    Unknown = 0,

    /// <summary>Virtual CPUs × hours. Integrates <see cref="QuotaMeter.Vcpu" />.</summary>
    VCpuHours = 1,

    /// <summary>Gibibytes of memory × hours. Integrates <see cref="QuotaMeter.MemoryGb" />.</summary>
    MemoryGbHours = 2,

    /// <summary>
    ///     Gibibytes of provisioned storage × months. Integrates <see cref="QuotaMeter.StorageGb" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Provisioned, not used, and that is the whole point of the state-based rule.</b> A
    ///     100 GB volume attached to a stopped VM is 100 GB-months. Deriving this from a metric that
    ///     reports zero for a pod that is not running is the under-billing bug docs/plan/22 § Two
    ///     kinds of meter names.
    /// </remarks>
    StorageGbMonths = 3,

    /// <summary>Gibibytes of backup × months. Integrates <see cref="QuotaMeter.StorageGb" />.</summary>
    BackupGbMonths = 4,

    /// <summary>Public IP addresses × hours. Integrates <see cref="QuotaMeter.PublicIps" />.</summary>
    PublicIpHours = 5,

    /// <summary>Managed Kubernetes clusters × hours. Integrates <see cref="QuotaMeter.Clusters" />.</summary>
    ClusterHours = 6,

    /// <summary>Resources of any kind × hours. Integrates <see cref="QuotaMeter.Resources" />.</summary>
    /// <remarks>
    ///     Not a meter anybody prices — <see cref="QuotaMeter.Resources" /> is a safety limit, not a
    ///     product. It is here because <see cref="MeterCatalog" /> is total over
    ///     <see cref="QuotaMeter" />: a family the write path can reserve and no meter can express is
    ///     a resource that is quota'd and unbilled, which is the shape of a free-compute exploit.
    /// </remarks>
    ResourceHours = 7,

    /// <summary>API requests served. Event-based.</summary>
    Requests = 8,

    /// <summary>Gibibytes leaving the platform. Event-based.</summary>
    EgressGb = 9,

    /// <summary>Messages sent. Event-based, and the doc's own example of one.</summary>
    MessagesSent = 10
}

/// <summary>What the rollup did with one <see cref="UsageEvent" />.</summary>
/// <remarks>
///     ⚠ <b><see cref="Duplicate" /> is a success, not a failure, and the distinction is load-bearing.</b>
///     Delivery is at-least-once (docs/plan/04 § Streams), so a redelivery is the <i>expected</i>
///     path after a silo restart, not an error. An emitter that retried on <see cref="Duplicate" />
///     would retry forever.
/// </remarks>
[Alias("CyberCloud.Metering.UsageIngestOutcome")]
public enum UsageIngestOutcome {
    /// <summary>Never assigned.</summary>
    Unknown = 0,

    /// <summary>New usage. It is in the ledger's working record and will be aggregated.</summary>
    Accepted = 1,

    /// <summary>
    ///     An idempotency key already seen. Nothing was recorded and nothing was lost — the first
    ///     copy is already accounted for.
    /// </summary>
    Duplicate = 2
}
