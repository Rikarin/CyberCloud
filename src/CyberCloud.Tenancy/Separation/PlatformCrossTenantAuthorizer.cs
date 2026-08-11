using System.Globalization;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;

namespace CyberCloud.Tenancy.Separation;

/// <summary>
///     Whether the caller holds an active <c>platform:root#operator</c> relation — the condition
///     docs/plan/06 § Platform administration puts on the platform → tenant edge.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is a seam, not an implementation, and the default DENIES.</b> The relation is
///         ReBAC's and ReBAC is <c>CyberCloud.Authorization</c>'s (ADR-007), which does not exist
///         yet. Two ways to bridge that were available and only one of them is safe: a stand-in that
///         allows, so that platform administration "works" until the real check lands, or a stand-in
///         that denies, so that the edge is closed until somebody deliberately opens it. A
///         default-allow stand-in is a hole with a comment on it, and the comment is the first thing
///         to be forgotten — so this denies, and the platform-admin surface it gates does not exist
///         yet either (docs/plan/06 § Platform administration is a provider, and no provider is
///         built).
///     </para>
/// </remarks>
public interface IPlatformOperatorAuthority
{
    /// <summary>
    ///     Whether the ambient caller may act as a platform operator against
    ///     <paramref name="targetTenantId" />.
    /// </summary>
    /// <param name="targetTenantId">The tenant being reached into.</param>
    /// <returns>The operator's user id when authorized; <see langword="null" /> when not.</returns>
    /// <remarks>
    ///     Returns the operator's identity rather than a <c>bool</c> because docs/plan/06 § Platform
    ///     administration requires the edge to be logged "with the operator's user id" — an
    ///     authorizer that returned only <c>true</c> could not write that line.
    /// </remarks>
    string? OperatorFor(string targetTenantId);
}

/// <summary>The default: nothing is a platform operator. See <see cref="IPlatformOperatorAuthority" />.</summary>
public sealed class DenyPlatformOperatorAuthority : IPlatformOperatorAuthority
{
    /// <inheritdoc />
    public string? OperatorFor(string targetTenantId) => null;
}

/// <summary>
///     The tenant → tenant delegation of docs/plan/06 § Platform administration, row 2 —
///     "Lighthouse-shaped, P1".
/// </summary>
/// <remarks>
///     ⚠ <b>P1 means not now.</b> The default implementation holds nothing, so the edge is closed.
///     The interface exists because the authorizer's shape has to have somewhere to ask, and adding
///     the question later would mean changing the authorizer rather than registering a service.
/// </remarks>
public interface ICrossTenantDelegationStore
{
    /// <summary>Whether an unexpired delegation lets <paramref name="source" /> reach <paramref name="target" />.</summary>
    /// <param name="source">The tenant making the call.</param>
    /// <param name="target">The tenant being called.</param>
    bool IsDelegated(string source, string target);
}

/// <summary>The default: no delegations exist. See <see cref="ICrossTenantDelegationStore" />.</summary>
public sealed class NoCrossTenantDelegations : ICrossTenantDelegationStore
{
    /// <inheritdoc />
    public bool IsDelegated(string source, string target) => false;
}

/// <summary>
///     The <see cref="ICrossTenantAuthorizer" /> of docs/plan/04 § Silo composition and docs/plan/06
///     § Platform administration.
/// </summary>
/// <remarks>
///     <para>
///         <b>The table this implements</b> (docs/plan/06 § Platform administration):
///     </para>
///     <list type="table">
///         <item>
///             <term>platform tenant → any tenant</term>
///             <description>
///                 Allowed when the caller holds an active <c>platform:root#operator</c> relation.
///                 Always logged, with the operator's user id.
///             </description>
///         </item>
///         <item>
///             <term>tenant A → tenant B</term>
///             <description>An unexpired delegation exists (Lighthouse-shaped, P1). Always logged.</description>
///         </item>
///         <item>
///             <term>anything else</term>
///             <description>Never. <see cref="UnauthorizedAccessException" /> + a security event.</description>
///         </item>
///     </list>
///     <para>
///         <b>Plus one edge the table does not have a row for and the document requires.</b>
///         docs/plan/06 § Grain keys: <c>IClusterConnectionGrain</c> is a null-tenant grain, "the
///         grain therefore carries the owning tenant as <i>state</i> and checks it on every call, and
///         <c>PlatformCrossTenantAuthorizer</c> explicitly allows the platform → connection edge and
///         logs it. This is the single place tenancy is enforced by code rather than by key." Every
///         null-tenant grain is in that category — the tenant directory and the shard map are read by
///         tenant-scoped code as a matter of course — so the rule here is <b>any tenant → the null
///         tenant is allowed and logged</b>, and it is the one rule in this file that trades a key
///         guarantee for a code guarantee. It is stated rather than hidden because the cost is real:
///         a null-tenant grain that forgets to check its own state is reachable from any tenant.
///     </para>
///     <para>
///         The <i>reverse</i> edge — a null-tenant grain calling into a tenant — is <b>denied</b>. No
///         document describes it, and a platform grain that needs to drive a tenant's provisioning
///         does it as the platform tenant (docs/plan/06 § Platform administration makes platform
///         administration a provider under an ordinary tenant), not as a null-tenant one.
///     </para>
///     <para>
///         ⚠ <b>What this closes and what it does not.</b> It is an incoming grain-call filter, so
///         what it sees is the <i>source grain</i> of a call. A caller that is not a grain — a
///         cluster client, a gateway, anything holding an <c>IGrainFactory</c> outside a grain
///         context — has no source tenant, and <c>Orleans.Multitenant</c>'s own filter returns early
///         for it. <c>CrossTenantReachabilityTests</c> § route 7 pins that precisely, in both
///         directions.
///     </para>
/// </remarks>
public sealed class PlatformCrossTenantAuthorizer(
    IPlatformOperatorAuthority operators,
    ICrossTenantDelegationStore delegations,
    ILogger<PlatformCrossTenantAuthorizer> logger)
    : ICrossTenantAuthorizer
{
    /// <summary>
    ///     The platform tenant — docs/plan/06 § Platform administration, "<c>Guid.Empty</c>".
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>DOC DEFECT.</b> docs/plan/06 § Platform administration writes the platform tenant as
    ///     "<c>Guid.Empty</c>, the null tenant", which conflates two different things. Orleans'
    ///     <i>null tenant</i> is the absence of tenant qualification — no prefix in the grain key,
    ///     and <c>MultitenantStorageOptions.TenantIdForNullTenant</c> ("Null") in the storage layer.
    ///     <c>Guid.Empty</c> is an ordinary tenant id whose textual form is
    ///     <c>00000000-0000-0000-0000-000000000000</c>, and a grain qualified with it is
    ///     tenant-qualified like any other. They cannot both be the platform tenant: a
    ///     <c>CyberCloud.Platform/tenants</c> resource has to be an ordinary tenant-scoped resource
    ///     for docs/plan/06's "platform admin is not a second API — it is a provider" to hold, and an
    ///     ordinary tenant-scoped grain is never null-tenant. This code reads it as
    ///     <c>Guid.Empty</c>-the-tenant-id, and treats null-tenant separately below.
    /// </remarks>
    public static readonly string PlatformTenantId =
        Guid.Empty.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>How many edges this authorizer has allowed, by category. For tests and metrics.</summary>
    public long AllowedPlatformEdges { get; private set; }

    /// <summary>How many delegated tenant → tenant edges have been allowed.</summary>
    public long AllowedDelegatedEdges { get; private set; }

    /// <summary>How many edges into a null-tenant platform grain have been allowed.</summary>
    public long AllowedNullTenantEdges { get; private set; }

    /// <summary>How many edges have been denied. Each one also raised a security event.</summary>
    public long Denied { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    ///     <c>Orleans.Multitenant</c> guarantees <paramref name="sourceTenantId" /> !=
    ///     <paramref name="targetTenantId" /> — it only asks when the two differ — and passes
    ///     <see langword="null" /> for a null-tenant grain on either side.
    /// </remarks>
    public bool IsAccessAuthorized(string? sourceTenantId, string? targetTenantId)
    {
        // Edge: anything → a null-tenant platform grain. Allowed, logged, and the grain is
        // responsible for its own tenancy check. See the ⚠ on the type.
        if (targetTenantId is null)
        {
            AllowedNullTenantEdges++;
            logger.LogDebug(
                "Cross-tenant edge allowed: tenant {Source} → a null-tenant platform grain. The "
                + "grain enforces tenancy in code (docs/plan/06 § Grain keys).",
                sourceTenantId);
            return true;
        }

        // Edge: a null-tenant grain → a tenant. Denied — no document describes it.
        if (sourceTenantId is null)
        {
            return Deny(sourceTenantId, targetTenantId);
        }

        // Edge: the platform tenant → any tenant.
        if (string.Equals(sourceTenantId, PlatformTenantId, StringComparison.OrdinalIgnoreCase))
        {
            if (operators.OperatorFor(targetTenantId) is { } operatorId)
            {
                AllowedPlatformEdges++;
                logger.LogWarning(
                    "Platform operator {Operator} reached into tenant {Target}. docs/plan/06 "
                    + "§ Platform administration requires this edge to be logged, always.",
                    operatorId,
                    targetTenantId);
                return true;
            }

            return Deny(sourceTenantId, targetTenantId);
        }

        // Edge: tenant A → tenant B under a delegation.
        if (delegations.IsDelegated(sourceTenantId, targetTenantId))
        {
            AllowedDelegatedEdges++;
            logger.LogWarning(
                "Delegated cross-tenant access: tenant {Source} → tenant {Target}.",
                sourceTenantId,
                targetTenantId);
            return true;
        }

        return Deny(sourceTenantId, targetTenantId);
    }

    bool Deny(string? sourceTenantId, string? targetTenantId)
    {
        Denied++;

        // The security event of docs/plan/06 § Platform administration, row 3. The
        // UnauthorizedAccessException itself is thrown by Orleans.Multitenant's own filter, whose
        // message is `Tenant "{source}" attempted to access tenant "{target}"` — both ids, which is
        // what docs/plan/04 § Silo composition promises.
        logger.LogError(
            "DENIED cross-tenant access: tenant {Source} → tenant {Target}. No platform operator "
            + "relation and no delegation. docs/plan/06 § Platform administration.",
            sourceTenantId ?? "NULL",
            targetTenantId ?? "NULL");

        return false;
    }
}
