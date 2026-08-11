namespace CyberCloud.Core.Contracts;

/// <summary>
///     The names of the Orleans stream providers. docs/plan/04 § Streams and ADR-005.
/// </summary>
/// <remarks>
///     docs/plan/04 § Streams —
///     <i>
///         "One stream provider, <c>Events</c>, over NATS JetStream,
///         multitenant-wrapped so a stream id carries the tenant."
///     </i>
///     There is deliberately only one:
///     the namespaces in docs/plan/04 § Streams (<c>resource-changed</c>, <c>operation-progress</c>,
///     <c>cluster-observed</c>, <c>metering</c>, <c>platform</c>) are stream <i>namespaces</i>
///     within this provider, not providers of their own.
/// </remarks>
public static class StreamProviders {
    /// <summary>
    ///     The one provider. Named in <c>[ImplicitStreamSubscription]</c> on every consuming grain
    ///     and in <c>GetStreamProvider(StreamProviders.Events)</c> on every producer.
    /// </summary>
    public const string Events = "Events";
}

/// <summary>
///     The stream namespaces carried by <see cref="StreamProviders.Events" />.
///     docs/plan/04 § Streams.
/// </summary>
/// <remarks>
///     ⚠ <b>Delivery is at-least-once and per-subject-ordered only</b> (docs/plan/04 § Streams). Every
///     consumer of every namespace below must be idempotent, and anything requiring global order
///     does not use a stream at all.
/// </remarks>
public static class StreamNamespaces {
    /// <summary>
    ///     <c>cc.{tenant}.res.{provider}.{type}.{id}</c> — produced by <c>IResourceGrain</c> on
    ///     every state transition; consumed by the portal fan-out, the resource-graph projection,
    ///     the audit sink and billing.
    /// </summary>
    public const string ResourceChanged = "resource-changed";

    /// <summary>
    ///     <c>cc.{tenant}.op.{id}</c> — produced by operation grains; consumed by the portal and by
    ///     <c>cyc … --wait</c>.
    /// </summary>
    public const string OperationProgress = "operation-progress";

    /// <summary>
    ///     <c>cc.{tenant}.k8s.{cluster}.{kind}</c> — produced by the informer bridge; consumed by
    ///     resource grains for drift and by the monitor provider.
    /// </summary>
    public const string ClusterObserved = "cluster-observed";

    /// <summary>
    ///     <c>cc.{tenant}.usage.{meter}</c> — produced by providers; consumed by the metering
    ///     rollup workers.
    /// </summary>
    public const string Metering = "metering";

    /// <summary>
    ///     <c>cc.platform.{topic}</c> — null-tenant. Carries the tenant-directory deltas
    ///     docs/plan/05 § The tenant directory has every gateway subscribe to.
    /// </summary>
    public const string Platform = "platform";
}
