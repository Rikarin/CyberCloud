namespace CyberCloud.Kubernetes.Connections;

/// <summary>
///     What <c>ClusterConnectionGrain</c> keeps durably.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every property is <c>{ get; set; }</c>, including the dictionary, and the dictionary
///         is the reason.</b> The durable tier serializes grain state with
///         <c>SystemTextJsonGrainStorageSerializer</c>, and <c>System.Text.Json</c> <b>silently</b>
///         does not populate a get-only collection property on read: it constructs the object, finds
///         no setter, and moves on — leaving an empty dictionary and no error anywhere. For
///         <see cref="InformerCursors" /> that would mean every reactivation quietly losing every
///         resume cursor and doing a full list, which is the exact stampede docs/plan/09 § Observing
///         is trying to avoid, and it would look like the feature simply not working.
///     </para>
///     <para>
///         <see cref="ClusterConnectionDescriptor" /> is a wire record with <c>init</c> setters,
///         which <c>System.Text.Json</c> does populate — <c>init</c> is a setter as far as the
///         serializer is concerned. Only <i>get-only</i> members are the trap.
///     </para>
/// </remarks>
public sealed class ClusterConnectionState
{
    /// <summary>
    ///     The cluster, its owning tenant and how to authenticate. <see langword="null" /> until the
    ///     first <c>AttachAsync</c>.
    /// </summary>
    public ClusterConnectionDescriptor? Descriptor { get; set; }

    /// <summary>
    ///     The informer cursors, keyed by <c>{group}/{version}/{plural}</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the whole point of persisting anything here.</b> docs/plan/09 § Observing:
    ///     the informer cache lives in one silo's memory and is lost when that silo dies. The
    ///     <i>cache</i> cannot be persisted — it is the whole object set — but the <i>cursor</i> is a
    ///     short string per kind, and persisting it is what turns a re-establishment from a full
    ///     list into a delta. Cheap thing kept, expensive thing rebuilt.
    /// </remarks>
    public Dictionary<string, string> InformerCursors { get; set; } = new(StringComparer.Ordinal);

    /// <summary>When the connection was first attached.</summary>
    public DateTimeOffset AttachedAt { get; set; }

    /// <summary>The last time a ping succeeded, carried across activations for continuity.</summary>
    public DateTimeOffset LastSuccessAt { get; set; }
}
