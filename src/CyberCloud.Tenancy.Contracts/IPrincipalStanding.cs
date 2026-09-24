using CyberCloud.Core;

namespace CyberCloud.Tenancy.Contracts;

/// <summary>
///     Answers whether a tenant's principal may still act — the principal's half of the question
///     <see cref="ITenantGrain.AreControlPlaneWritesAllowedAsync" /> answers for the tenant.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>It exists for the one write that has no token behind it.</b> A request's caller is
///         vouched for by its token, and suspending a user revokes their sessions
///         (<c>IUserGrain.SetStatusAsync</c>), so that caller can't renew a token. A deployment's
///         children are written from a reminder as the caller recorded when the deployment was
///         accepted (<c>IResourceManager.WriteChildAsync</c>), and nothing on that path reads a
///         token or a session. Without this, a user suspended, or a service principal disabled,
///         after their deployment started went on creating resources as themselves until the
///         template ran out.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Declared here because both modules that meet at it already reach this one, and
///             neither reaches the other.
///         </b> <c>module-layering.txt</c> gives the resource manager, which asks, no edge to
///         identity, which answers, and none back. Both reach <c>CyberCloud.Tenancy</c>, and a
///         principal belongs to exactly one tenant (docs/plan/11 § Sign-up and tenant creation), so
///         the standing of a tenant's principal sits beside the tenant's own. Identity supplies the
///         implementation from <c>AddCyberCloudIdentity</c>; the resource manager registers a
///         refusing default, so a silo that composes the manager without identity writes no child
///         at all rather than writing every child on trust.
///     </para>
/// </remarks>
public interface IPrincipalStanding {
    /// <summary>
    ///     Succeeds when the principal exists in the tenant and may act now; fails, naming why, when
    ///     it may not or when that can't be established.
    /// </summary>
    /// <param name="tenantId">The tenant the principal belongs to. It's the only tenant searched.</param>
    /// <param name="principalType">
    ///     The ReBAC subject type as the caller spells it: <c>user</c>, <c>servicePrincipal</c>, or
    ///     <c>managedIdentity</c>. Anything else never acts and is refused.
    /// </param>
    /// <param name="principalId">
    ///     The subject id — the principal's GUID in <c>N</c> form, as a token's <c>sub</c> carries it.
    /// </param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>
    ///     Success, or a failure: <see cref="CyberCloud.Core.ErrorCode.AuthorizationFailed" /> for a
    ///     principal that is absent, suspended, deprovisioned, disabled, or unbound, and any other code
    ///     for a question that couldn't be answered. ⚠ The caller refuses on every failure — "couldn't tell" is
    ///     never "may act".
    /// </returns>
    Task<Result> EnsureMayActAsync(
        Guid tenantId,
        string principalType,
        string principalId,
        CancellationToken cancellationToken = default
    );
}
