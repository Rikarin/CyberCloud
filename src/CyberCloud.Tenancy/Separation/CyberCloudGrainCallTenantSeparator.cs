using Orleans.Multitenant;

namespace CyberCloud.Tenancy.Separation;

/// <summary>
///     Which incoming grain calls are subject to tenant separation.
/// </summary>
/// <remarks>
///     <para>
///         <c>Orleans.Multitenant</c>'s default is
///         <c>!context.InterfaceName.StartsWith("Orleans.")</c> — everything except Orleans' own
///         system grains. That is kept, and the ⚠ in <c>Orleans.Multitenant</c>'s own documentation
///         is why nothing may be added to the exclusion list casually:
///         <i>
///             "this is called for ALL
///             grain calls in Orleans - including the internal Orleans grains. A registered
///             implementation should include similar logic to exclude internal Orleans grains."
///         </i>
///         Drop
///         that clause and the silo's own management grains start failing authorization at startup.
///     </para>
///     <para>
///         ⚠ <b>Nothing else is excluded, deliberately.</b> The tempting exclusion is "let anyone
///         call the tenant directory and the shard map", and it is not needed: those are null-tenant
///         grains and <see cref="PlatformCrossTenantAuthorizer" /> already allows the edge <i>into</i>
///         the null tenant, with a log line. Doing it here instead would remove the log line and
///         make the allowance invisible, and an exclusion list is the thing that grows until the
///         separation is decorative.
///     </para>
/// </remarks>
public sealed class CyberCloudGrainCallTenantSeparator : IGrainCallTenantSeparator {
    /// <inheritdoc />
    public bool IsTenantSeparatedCall(IIncomingGrainCallContext context) {
        ArgumentNullException.ThrowIfNull(context);

        return !context.InterfaceName.StartsWith("Orleans.", StringComparison.Ordinal);
    }
}
