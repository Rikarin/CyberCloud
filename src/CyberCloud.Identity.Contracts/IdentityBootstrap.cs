namespace CyberCloud.Identity.Contracts;

/// <summary>
///     The identities the platform seeds for itself, so that a fresh run can do the one thing nobody
///     can do before anybody exists: create the first tenant.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A well-known GUID, deliberately, and not a secret.</b>
///         <c>IScopeManager.CreateTenantAsync</c> checks <c>platform:root#operator</c> and nothing
///         else — docs/plan/06 § Platform administration — and on a fresh <c>dotnet run</c> nobody
///         holds that relation, so self-serve sign-up cannot create anything at all. The silo's
///         <c>PlatformBootstrapTask</c> writes
///         <c>platform:root#operator@servicePrincipal:{SignUpOperator}</c> when
///         <c>CyberCloud:Identity:SelfServeSignUp</c> is on, and the identity host's sign-up
///         orchestrator names that subject in the <c>CallerContext</c> it hands the scope manager. A
///         <c>CallerContext</c> is not a credential: it is a statement of who is acting that the
///         tuple store either backs or does not. What the tuple documents is that sign-up acts as an
///         operator, which is the sentence <c>IScopeManager.CreateTenantAsync</c>'s remarks predicted
///         — sign-up "ends by calling something exactly like this method with a platform-operator
///         identity of its own".
///     </para>
///     <para>
///         ⚠ <b>No <c>IServicePrincipalGrain</c> is created for it.</b> It never authenticates —
///         there is no secret, no token, no <c>/token</c> call — so a principal record would be a
///         credential-bearing object for something that must never hold a credential. The tuple is
///         the whole of its existence.
///     </para>
/// </remarks>
public static class IdentityBootstrap {
    /// <summary>
    ///     The subject self-serve sign-up creates tenants as:
    ///     <c>servicePrincipal:00000000-0000-0000-0000-00000000c1c0</c>.
    /// </summary>
    public static Guid SignUpOperator { get; } = Guid.Parse("00000000-0000-0000-0000-00000000c1c0");
}
