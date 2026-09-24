using CyberCloud.Core.Resources;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Tokens;
using CyberCloud.Identity.SignIn;
using Orleans.Multitenant;
using System.Security.Claims;

namespace CyberCloud.Identity.Host.Api;

/// <summary>A complete, live cookie session of an active user, with both factors' methods.</summary>
/// <param name="Session">The session grain's record.</param>
/// <param name="Profile">The user the session is for.</param>
/// <param name="Methods">The grain's methods, then the cookie's — <c>AuthorizeApi.WithCookieMethods</c>' union.</param>
public sealed record CookieSession(SessionDescriptor Session, UserProfile Profile, IReadOnlyList<AuthenticationMethod> Methods);

/// <summary>
///     A person's sign-in as their <see cref="HomeAccount" />: reading it off the cookie, finding
///     the member it joined another tenant as, and opening that member's session. docs/plan/11
///     § Sign-up and tenant creation, the invited path; issue #43.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two callers, one rule.</b> The invitation page links a member to the account whose
///         cookie is on the request (<c>InvitationApi</c>), and <c>/authorize</c> for the member's
///         tenant opens the member's session from that cookie (<c>AuthorizeApi</c>). Both read the
///         cookie through <see cref="SignedInAsync" />, so what counts as a sign-in is decided once:
///         a complete one (no second factor pending), whose session grain still says live, for a
///         user who is <see cref="UserStatus.Active" />.
///     </para>
///     <para>
///         ⚠ <b>Found through the member's tenant, never through a global index.</b>
///         <see cref="MemberAsync" /> resolves the home account's address in the other tenant's own
///         email index, and then requires the member's <see cref="UserProfile.HomeAccount" /> to
///         name this very account. Another user with the same address there — somebody who signed
///         up with it, or joined with a password — is not this person's, and the answer is no. The
///         lookup is <c>SignInService.ResolveAddressAsync</c>'s enumeration oracle only in the
///         sense that the signed-in person learns whether their own address has a user in a tenant
///         they named, which proving the address already let them find out.
///     </para>
///     <para>
///         ⚠ <b>The home tenant decides whether a session opens, and the member's tenant decides
///         whether it lasts.</b> Suspending the home account or signing it out everywhere revokes
///         the home session, so no new session opens here; a session already open here is the
///         member's, which this tenant's owners suspend or remove as they would any other. It is
///         the same split two separate sign-ins would have.
///     </para>
/// </remarks>
/// <param name="grains">The cluster. ⚠ Every reference through <c>ForTenant</c>.</param>
/// <param name="signIn">The address lookup and the session opener every sign-in shares.</param>
public sealed class HomeAccounts(IGrainFactory grains, SignInService signIn) {
    /// <summary>The cookie's sign-in, when it is one this host may act on.</summary>
    /// <param name="user">The cookie principal, or <see langword="null" />.</param>
    /// <returns>The session, or <see langword="null" /> for anything short of a complete, live sign-in of an active user.</returns>
    public async Task<CookieSession?> SignedInAsync(ClaimsPrincipal? user) {
        if (!IdentitySessionPrincipal.IsFullyAuthenticated(user)
            || IdentitySessionPrincipal.TenantId(user) is not { } tenantId
            || IdentitySessionPrincipal.UserId(user) is not { } userId
            || IdentitySessionPrincipal.SessionId(user) is not { } sessionId) {
            return null;
        }

        var tenant = grains.ForTenant(TenantHint.Qualifier(tenantId));
        var session = await tenant.GetGrain<ISessionGrain>(GrainKeys.Session(sessionId)).GetAsync();

        if (session.TryGetError(out _) || !session.GetValueOrThrow().IsLive) {
            return null;
        }

        var profile = await tenant.GetGrain<IUserGrain>(GrainKeys.User(userId)).GetAsync();

        if (profile.TryGetError(out _) || profile.GetValueOrThrow().Status != UserStatus.Active) {
            return null;
        }

        var methods = new List<AuthenticationMethod>(session.GetValueOrThrow().Methods);

        foreach (var claim in user!.FindAll(AccessTokenClaims.AuthenticationMethods)) {
            if (AuthenticationMethodNames.Parse(claim.Value) is { } method && !methods.Contains(method)) {
                methods.Add(method);
            }
        }

        return new(session.GetValueOrThrow(), profile.GetValueOrThrow(), methods);
    }

    /// <summary>The active member of <paramref name="tenantId" /> that <paramref name="home" /> joined as, or <see langword="null" />.</summary>
    /// <param name="home">The cookie's sign-in, from <see cref="SignedInAsync" />.</param>
    /// <param name="tenantId">The tenant a request named. Never the home account's own.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    public async Task<UserProfile?> MemberAsync(CookieSession home, Guid tenantId, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(home);

        if (tenantId == home.Session.TenantId
            || await signIn.ResolveAddressAsync(tenantId, home.Profile.Email, cancellationToken) is not { } memberId) {
            return null;
        }

        var member = await grains.ForTenant(TenantHint.Qualifier(tenantId))
            .GetGrain<IUserGrain>(GrainKeys.User(memberId))
            .GetAsync();

        return member.IsSuccess
            && member.GetValueOrThrow() is { Status: UserStatus.Active, HomeAccount: { } linked } profile
            && linked.TenantId == home.Session.TenantId
            && linked.UserId == home.Session.UserId
                ? profile
                : null;
    }

    /// <summary>Opens the member's session from the home account's sign-in.</summary>
    /// <param name="home">The cookie's sign-in.</param>
    /// <param name="member">The member, from <see cref="MemberAsync" />.</param>
    /// <param name="context">The request, for the device record.</param>
    /// <returns>
    ///     The member's new session, carrying every method the home sign-in presented, or
    ///     <see langword="null" /> when the session grain refused.
    /// </returns>
    /// <remarks>
    ///     ⚠ The session grain records the first method, as every sign-in's does, and the rest are
    ///     put back on the descriptor here — the union <c>AuthorizeApi.WithCookieMethods</c> makes
    ///     for a cookie. A home sign-in of password plus TOTP opens a member session that says the
    ///     same, never a bare password. <c>auth_time</c> is the home sign-in's too: that is when the
    ///     person authenticated, and a client asking for a recent sign-in must not be told "now".
    /// </remarks>
    public async Task<SessionDescriptor?> OpenAsync(CookieSession home, UserProfile member, SignInContext context) {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(member);

        var opened = await signIn.OpenSessionAsync(member.TenantId, member.UserId, home.Methods[0], context);

        if (opened.TryGetError(out _)) {
            return null;
        }

        var session = await grains.ForTenant(TenantHint.Qualifier(member.TenantId))
            .GetGrain<ISessionGrain>(GrainKeys.Session(opened.GetValueOrThrow().SessionId))
            .GetAsync();

        return session.IsSuccess
            ? session.GetValueOrThrow() with { Methods = [.. home.Methods], AuthenticatedAt = home.Session.AuthenticatedAt }
            : null;
    }
}
