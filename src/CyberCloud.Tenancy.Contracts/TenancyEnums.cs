namespace CyberCloud.Tenancy.Contracts;

/// <summary>
///     The tenant lifecycle of docs/plan/06 § Tenant lifecycle, exactly.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The interesting member is <see cref="Suspended" />, and its meaning is deliberate:
///             the data plane keeps running.
///         </b>
///         docs/plan/06 § Tenant lifecycle — "suspending a tenant
///         should not take their production down without notice". Only control-plane writes are
///         rejected, with <c>403</c>. Anything that reads this enum and shuts a workload down on
///         <see cref="Suspended" /> has misread it; <see cref="Disabled" /> is the one that scales
///         to zero.
///     </para>
///     <para>
///         <b>No <c>[GenerateSerializer]</c>, and that is not an omission.</b> Orleans serialises
///         enums through its built-in enum codec without annotation. The <c>[Alias]</c> is what
///         matters for a rolling upgrade — it pins the name a peer looks the type up by, so the CLR
///         type can be renamed or moved without a wire break (docs/plan/04 § Failure and upgrade).
///     </para>
/// </remarks>
[Alias("CyberCloud.Tenancy.TenantStatus")]
public enum TenantStatus {
    /// <summary>Never assigned. Not a status — see the <c>default(T)</c> argument on <c>Result</c>.</summary>
    Unknown = 0,

    /// <summary>Directory entry written, shards assigned, bootstrap running. No API access yet.</summary>
    Provisioning = 1,

    /// <summary>Normal.</summary>
    Active = 2,

    /// <summary>Payment overdue. Portal banner; writes allowed.</summary>
    Warned = 3,

    /// <summary>
    ///     Overdue past grace, or by admin. <b>Data plane keeps running</b>; control-plane writes
    ///     rejected <c>403</c>.
    /// </summary>
    Suspended = 4,

    /// <summary>Explicit shutdown. Data plane scaled to zero. State retained.</summary>
    Disabled = 5,

    /// <summary>30-day tombstone. Nothing runs, nothing is billed, everything is restorable.</summary>
    PendingDeletion = 6,

    /// <summary>
    ///     Gone. Grain state deleted, shards reclaimed, directory entry tombstoned <b>forever</b> —
    ///     an id is never reused.
    /// </summary>
    Purged = 7
}

/// <summary>
///     The Azure provisioning vocabulary, from docs/plan/06 § Tags, locks, and the small stuff that
///     is not small. Every provider uses this and none invents its own.
/// </summary>
[Alias("CyberCloud.Tenancy.ProvisioningState")]
public enum ProvisioningState {
    /// <summary>Never assigned.</summary>
    Unknown = 0,

    /// <summary>Being created. The index claim is placed but may not be confirmed.</summary>
    Creating = 1,

    /// <summary>Being updated in place.</summary>
    Updating = 2,

    /// <summary>
    ///     Being torn down. ⚠ A resource stuck here is <b>still visible in listings</b> —
    ///     docs/plan/06 § Two-phase create calls that a billing-dispute prevention measure.
    /// </summary>
    Deleting = 3,

    /// <summary>Terminal success.</summary>
    Succeeded = 4,

    /// <summary>Terminal failure.</summary>
    Failed = 5,

    /// <summary>Terminal cancellation.</summary>
    Canceled = 6
}

/// <summary>
///     The quota meters of docs/plan/06 § Quota — "per-region and per-family".
/// </summary>
/// <remarks>
///     ⚠ This is a <b>meter family</b>, not a grain key. It is low-cardinality on purpose, and it is
///     safe to be low-cardinality precisely because it never becomes a key: the quota grain is keyed
///     by the <i>subscription</i> and holds one counter per meter as state. Keying a grain by a value
///     from this enum is the exact mistake docs/plan/04 § Grain taxonomy's ⚠ describes — six
///     activations serialising every create in the platform.
/// </remarks>
[Alias("CyberCloud.Tenancy.QuotaMeter")]
public enum QuotaMeter {
    /// <summary>Never assigned.</summary>
    Unknown = 0,

    /// <summary>Virtual CPUs.</summary>
    Vcpu = 1,

    /// <summary>Memory, in gibibytes.</summary>
    MemoryGb = 2,

    /// <summary>Provisioned storage, in gibibytes.</summary>
    StorageGb = 3,

    /// <summary>Public IP addresses.</summary>
    PublicIps = 4,

    /// <summary>Managed Kubernetes clusters.</summary>
    Clusters = 5,

    /// <summary>Resources of any kind.</summary>
    Resources = 6
}

/// <summary>
///     What an index entry currently is: a lease that can expire, or a permanent binding.
/// </summary>
/// <remarks>
///     The two-phase create of docs/plan/06 § Two-phase create <i>is</i> this enum. A
///     <see cref="Claimed" /> entry is step 1's five-minute lease; <see cref="Confirmed" /> is step
///     3. A silo dying between them leaves a <see cref="Claimed" /> entry that expires on its own,
///     which is what frees the name.
/// </remarks>
[Alias("CyberCloud.Tenancy.IndexEntryState")]
public enum IndexEntryState {
    /// <summary>Nothing is claimed. The name is free.</summary>
    Free = 0,

    /// <summary>Claimed under a lease. Expires unless confirmed.</summary>
    Claimed = 1,

    /// <summary>A permanent binding. Released only by an explicit release.</summary>
    Confirmed = 2
}
