namespace CyberCloud.ResourceManager;

/// <summary>
///     Builds the <c>resource-changed</c> event of step 11 from a resource's address and the snapshot
///     the grain returned — the one function both emitters call.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two emitters, one shape, and this is what keeps them one shape.</b> The write path in
///         <c>ResourceManagerService</c> emits <c>Created</c>, <c>Updated</c> and <c>Deleting</c> from
///         the gateway's process at step 11; <c>OperationGrain</c> emits <c>StateChanged</c>,
///         <c>SoftDeleted</c> and <c>Deleted</c> from the silo when a reconcile reaches a terminal
///         state, a teardown parks the resource, or a teardown clears the grain. docs/plan/04 § Streams names <i>"<c>IResourceGrain</c> on every state
///         transition"</i> as the producer, and until #54 only the first half of that sentence had an
///         emitter: a resource that went <c>Creating → Succeeded</c> never told the projection, so a
///         list built from the stream would have shown every resource creating forever. The columns
///         are docs/plan/08 § The resource-graph projection's, in its order, and a second copy of this
///         object initializer in the grain is how the two would have drifted.
///     </para>
///     <para>
///         The event's <see cref="ResourceChangedEvent.Version" /> is the grain's own count, carried
///         on <see cref="ResourceSnapshot.Version" />; the projector drops any event at or below the
///         version it holds, which is what makes a gateway emission that lands after the silo's
///         harmless.
///     </para>
/// </remarks>
static class ResourceChangedEvents {
    /// <summary>One event, from the address and the snapshot the grain just returned.</summary>
    /// <param name="change">What happened.</param>
    /// <param name="id">The resource's address, with its GUID resolved.</param>
    /// <param name="apiVersion">The api-version the change was written at.</param>
    /// <param name="snapshot">The grain's answer to the call that made the change.</param>
    public static ResourceChangedEvent From(
        ResourceChangeKind change,
        ResourceId id,
        string apiVersion,
        ResourceSnapshot snapshot
    ) {
        ArgumentNullException.ThrowIfNull(apiVersion);
        ArgumentNullException.ThrowIfNull(snapshot);

        return new() {
            Change = change,
            ResourceId = id.Id,
            TenantId = id.TenantId,
            SubscriptionId = id.SubscriptionId,
            ResourceGroup = id.ResourceGroup,
            Provider = id.Type.Namespace,
            Type = id.Type.Type,
            Name = id.Name,
            ApiVersion = apiVersion,
            ProvisioningState = snapshot.ProvisioningState,
            Location = snapshot.Location,
            ClusterId = snapshot.ClusterId,
            Tags = snapshot.Tags,
            CreatedAt = snapshot.CreatedAt,
            ModifiedAt = snapshot.ModifiedAt,
            DesiredHash = DesiredHash.Of(snapshot.Body),
            Version = snapshot.Version,
            // ⚠ The path is what #90's watch seam keys on (IWatchIndexGrain matches a subscriber's
            // type against it); the two branches that added Path (#90) and this builder (#54) met at
            // the merge, and a builder that dropped it would have left every silo-side emission
            // invisible to a watching provider.
            Path = id.Path
        };
    }
}
