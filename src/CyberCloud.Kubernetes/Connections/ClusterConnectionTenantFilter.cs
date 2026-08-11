using Orleans.Multitenant;

namespace CyberCloud.Kubernetes.Connections;

/// <summary>
///     Establishes <see cref="CallerTenant.Current" /> for every incoming grain call, from the
///     runtime's own view of who is calling.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/06 § Grain keys requires <c>IClusterConnectionGrain</c> to check its owning
///         tenant "on every call". Something has to tell it who is calling, and the only unforgeable
///         source is <see cref="IGrainCallContext.SourceId" /> — the calling activation's
///         <c>GrainId</c>, set by the Orleans runtime from the message, not by the caller's code.
///         <c>Orleans.Multitenant</c>'s <c>GrainIdExtensions.GetTenantId</c> extracts the tenant from
///         it, exactly as that library's own <c>TenantSeparatingCallFilter</c> does.
///     </para>
///     <para>
///         ⚠ <b>Registered for all grain calls, not just this one grain type.</b> Narrowing it to
///         <c>IClusterConnectionGrain</c> by interface name would be a filter whose correctness
///         depends on a string, and the cost of the general form is one
///         <see cref="AsyncLocal{T}" /> assignment per call. The <i>use</i> is narrow; the
///         establishment is not.
///     </para>
///     <para>
///         <b>What <see cref="IGrainCallContext.SourceId" /> is null for</b>, and why that is
///         <see cref="CallerTenant.Client" /> rather than an error: a call from a cluster client, the
///         gateway, or a test holding an <c>IGrainFactory</c> has no source activation.
///         <c>Orleans.Multitenant</c>'s own filter returns early in that case — which is exactly what
///         <c>CrossTenantReachabilityTests</c> § route 7 pins — so a client is *not* subject to
///         Orleans' tenant separation at all. That makes it the caller this grain must be most
///         careful about, and it is handled explicitly: <see cref="CallerKind.Client" /> is a
///         distinct kind, and the grain's policy decides what it may do rather than inheriting a
///         default.
///     </para>
/// </remarks>
public sealed class ClusterConnectionTenantFilter : IIncomingGrainCallFilter {
    /// <inheritdoc />
    public async Task Invoke(IIncomingGrainCallContext context) {
        ArgumentNullException.ThrowIfNull(context);

        using (CallerTenant.Enter(Resolve(context.SourceId))) {
            await context.Invoke().ConfigureAwait(false);
        }
    }

    /// <summary>Classifies the calling activation.</summary>
    /// <param name="source">The source of the call, from the runtime.</param>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         The client check comes FIRST, and leaving it out is a real hole rather than a
    ///         tidiness point.
    ///     </b>
    ///     An Orleans client is not "no source" — it has a <see cref="GrainId" />
    ///     of its own, whose key carries no tenant, so <c>GetTenantId()</c> returns
    ///     <see langword="null" /> for it exactly as it does for a genuine null-tenant platform
    ///     grain. Classifying on the tenant id alone therefore promotes
    ///     <i>
    ///         every cluster client in
    ///         the platform
    ///     </i>
    ///     — the gateway, a hosted service, a test — to
    ///     <see cref="CallerKind.NullTenant" />, which
    ///     <c>ClusterConnectionGrain.EnsureCallerMayReach</c> allows. That is a total bypass of the
    ///     only tenancy check this grain has.
    ///     <para>
    ///         This was not hypothetical: the first run of
    ///         <c>ClusterConnectionTenancyTests.AClientCallIsRefusedBecauseNothingElseChecksIt</c>
    ///         failed for precisely this reason, with a client reaching a cluster it did not own.
    ///         <c>GrainTypePrefix.IsClient</c> distinguishes the two, and it is the runtime's own
    ///         predicate rather than a string comparison on the key.
    ///     </para>
    /// </remarks>
    static CallerTenant Resolve(GrainId? source) {
        if (source is not { } id) {
            return CallerTenant.Client;
        }

        if (id.IsClient()) {
            return CallerTenant.Client;
        }

        // A system target is Orleans' own infrastructure (the catalog, the directory, the
        // management grain). It is not a tenant and it is not platform code of ours, so it gets the
        // same "not a tenant" classification a client does rather than being trusted.
        return id.IsSystemTarget()
            ? CallerTenant.Client
            : CallerTenant.FromTenantId(id.GetTenantId());
    }
}
