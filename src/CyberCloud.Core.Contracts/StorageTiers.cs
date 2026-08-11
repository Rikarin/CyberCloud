namespace CyberCloud.Core.Contracts;

/// <summary>
///     The names of the two grain-storage providers. ADR-003 (docs/plan/02 § ADR-003) and
///     docs/plan/05 § The two tiers.
/// </summary>
/// <remarks>
///     <para>
///         These strings are the ones in <c>[PersistentState("state", StorageTiers.Hot)]</c>
///         (docs/plan/02 § ADR-003, the tier table) and in <c>AddMultitenantGrainStorage…(StorageTiers.Durable, …)</c>
///         (docs/plan/04:50-53). They live in <c>.Contracts</c> rather than in
///         <c>ServiceDefaults</c> because <b>both ends need them</b>: the silo names a provider when
///         it registers one, and a grain names the same provider from a provider-implementation
///         assembly that must not reference the host.
///     </para>
///     <para>
///         ⚠ <b>A typo here is a silent second storage provider, not a compile error.</b> Orleans
///         resolves a storage provider by name at activation; an unregistered name is a runtime
///         failure on first use of that grain, in production, on the grain nobody activated during
///         testing. That is the whole reason these are <c>const</c>s and not literals — and it is
///         why <c>docs/plan/05:107</c>'s architecture test walks <c>[PersistentState]</c> rather
///         than trusting review.
///     </para>
///     <para>
///         ⚠ <b>Changing a value is a data migration.</b> The name is part of the physical key
///         layout of neither tier, but it <i>is</i> what binds a grain to a store, so renaming
///         <c>"Durable"</c> points every durable grain at a provider that does not exist. Add a new
///         tier; never rename one.
///     </para>
/// </remarks>
public static class StorageTiers
{
    /// <summary>
    ///     Redis Cluster. Sessions, live status, observed cluster state, caches, rate counters,
    ///     terminal sessions, metric pre-aggregates. Loss costs a warm-up (docs/plan/05:68-72).
    /// </summary>
    public const string Hot = "Hot";

    /// <summary>
    ///     PostgreSQL, sharded by tenant, synchronous replica. Tenants, subscriptions, resource
    ///     desired state, users, ReBAC tuples, operations, the billing ledger. Loss tolerance is
    ///     <b>zero</b> (docs/plan/02 § ADR-003).
    /// </summary>
    public const string Durable = "Durable";
}
