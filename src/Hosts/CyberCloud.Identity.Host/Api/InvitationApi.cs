using CyberCloud.Core;
using CyberCloud.Core.Resources;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Tokens;
using CyberCloud.Identity.SignIn;
using Orleans.Multitenant;
using System.Security.Claims;
using System.Text.Json.Serialization;

namespace CyberCloud.Identity.Host.Api;

/// <summary>The link's three parts, as the invitation page read them off its query.</summary>
/// <param name="Tenant">The tenant id, <c>N</c> form.</param>
/// <param name="Invitation">The invitation id, <c>N</c> form.</param>
/// <param name="Token">The secret.</param>
public sealed record InvitationLookupRequest(
    [property: JsonPropertyName("tenant")]
    string? Tenant,
    [property: JsonPropertyName("invitation")]
    string? Invitation,
    [property: JsonPropertyName("token")]
    string? Token
);

/// <summary>The body of <c>POST /api/invitations/accept</c> — the link, and what the person chose.</summary>
/// <param name="Tenant">The tenant id, <c>N</c> form.</param>
/// <param name="Invitation">The invitation id, <c>N</c> form.</param>
/// <param name="Token">The secret.</param>
/// <param name="DisplayName">The name they chose. Ignored with <paramref name="WithSignedInAccount" />.</param>
/// <param name="Password">
///     The password for their user in this tenant. ⚠ In the body, never a query string — the rule
///     <c>SignInPasswordRequest</c> states. Ignored with <paramref name="WithSignedInAccount" />.
/// </param>
/// <param name="WithSignedInAccount">
///     Join with the account this browser is signed into rather than with a new name and password —
///     <see cref="InvitationPageResponse.CanJoinWithAccount" />.
/// </param>
public sealed record InvitationAcceptRequest(
    [property: JsonPropertyName("tenant")]
    string? Tenant,
    [property: JsonPropertyName("invitation")]
    string? Invitation,
    [property: JsonPropertyName("token")]
    string? Token,
    [property: JsonPropertyName("displayName")]
    string? DisplayName,
    [property: JsonPropertyName("password")]
    string? Password,
    [property: JsonPropertyName("withSignedInAccount")]
    bool WithSignedInAccount = false
);

/// <summary>What both invitation endpoints answer.</summary>
/// <param name="Found">Whether the link names an invitation. When false, <paramref name="Message" /> says why.</param>
/// <param name="Email">The address invited — the account being created.</param>
/// <param name="TenantName">The organisation.</param>
/// <param name="Status"><c>pending</c>, <c>accepted</c>, <c>expired</c>, <c>withdrawn</c> or <c>revoked</c>.</param>
/// <param name="Succeeded">Whether an accept made the person a member and signed them in.</param>
/// <param name="PortalUrl">Where to go next — the portal, from its registration; empty when none is configured.</param>
/// <param name="Message">What to render, verbatim.</param>
/// <param name="Account">
///     The address this browser is signed in as, or empty — so the page can
///     say whose account it would join with. The cookie's, never the link's.
/// </param>
/// <param name="CanJoinWithAccount">
///     Whether the person may join with that account: the sign-in is complete and live, and its
///     address is the invited one.
/// </param>
public sealed record InvitationPageResponse(
    [property: JsonPropertyName("found")]
    bool Found,
    [property: JsonPropertyName("email")]
    string Email,
    [property: JsonPropertyName("tenantName")]
    string TenantName,
    [property: JsonPropertyName("status")]
    string Status,
    [property: JsonPropertyName("succeeded")]
    bool Succeeded,
    [property: JsonPropertyName("portalUrl")]
    string PortalUrl,
    [property: JsonPropertyName("message")]
    string Message,
    [property: JsonPropertyName("account")]
    string Account = "",
    [property: JsonPropertyName("canJoinWithAccount")]
    bool CanJoinWithAccount = false
);

/// <summary>What <see cref="InvitationApi.AcceptAsync" /> decided, and the session to issue.</summary>
/// <param name="Body">The response body.</param>
/// <param name="Principal">The cookie to issue, or <see langword="null" />.</param>
public sealed record InvitationApiResult(InvitationPageResponse Body, ClaimsPrincipal? Principal = null);

/// <summary>
///     The invitation page's API — describe a link, and accept it. docs/plan/11 § Sign-up and tenant
///     creation, the invited path; issue #43, step 7.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two ways to accept: as somebody new, or with the account the person already
///         has.</b> docs/plan/11 § Sign-up: <i>"The invitee either signs in (if they already have a
///         user in another tenant) or signs up."</i> Signing up is a name and a password for this
///         organisation. Signing in is the ordinary sign-in page, for the person's own organisation,
///         with this page as the return URL; back here, <see cref="DescribeAsync" /> reads the
///         cookie and offers <see cref="InvitationPageResponse.CanJoinWithAccount" /> when the
///         sign-in is complete and live and its address is the invited one, and accepting with it
///         makes the invited user a member linked to that account
///         (<see cref="IInvitationGrain.AcceptWithHomeAccountAsync" />) with no credential of its
///         own. That is still two users, as the one-user-one-tenant rule means, and one sign-in:
///         <c>AuthorizeApi</c> opens the member's session here from the home account's cookie
///         (<see cref="HomeAccounts" />' remarks). The cookie stays the home account's — it
///         already opens both.
///     </para>
///     <para>
///         ⚠ <b>Two proofs of the address, and both are required.</b> The link proves the person
///         can read the invited mailbox; the sign-in proves the account is theirs; the address match
///         ties the two. A signed-in person holding a link for another address is told so and
///         offered the new-person form, and a link forwarded to somebody signed in as themselves
///         links nothing, because their address is not the invited one. The accept that uses the
///         cookie counts only from the page's origin (<c>IdentityEndpoints.MapInvitationPage</c>),
///         for the reason the device page's answer does.
///     </para>
///     <para>
///         What makes the new-person path safe without a delivered code is the link itself, which
///         went to the address and nowhere else (<see cref="IInvitationGrain" />'s remarks). A
///         browser already signed into another tenant gets a cookie for this one on accepting that
///         way, as a sign-in to a second tenant does.
///     </para>
///     <para>
///         ⚠ <b>The tenant is resolved through the directory before any tenant grain is
///         touched</b>, as <see cref="TenantHint" /> does for every first-factor request: the link's
///         tenant is caller input, and a made-up one must activate nothing. The ids are parsed in
///         their <c>N</c> form only, for the same reason. Every wrong link — unknown tenant, unknown
///         invitation, wrong secret — is one sentence.
///     </para>
///     <para>
///         ⚠ <b>Accepting as somebody new signs the person in</b>, with a session stamped as a
///         password and a delivered code — the link is the code, as the enrolment code is at sign-up
///         (<c>SignUpApi</c>'s principal says why that is two factors and not one). The member has
///         no role; the portal they land on shows them nothing until somebody grants them one.
///         ⚠ That stamp is only honest for a user who was <see cref="UserStatus.Invited" /> until
///         this request — nobody has enrolled a second factor it could skip — and
///         <see cref="IUserGrain.AcceptInvitationAsync" /> is what guarantees it: a link naming a
///         member, a suspended account or a deprovisioned one is refused before any session opens.
///     </para>
/// </remarks>
/// <param name="tenants">The directory, through the tenant hint.</param>
/// <param name="grains">The cluster. ⚠ Every reference through <c>ForTenant</c>.</param>
/// <param name="signIn">Opens the session an accepted invitation signs in with.</param>
/// <param name="clients">The portal's registration, for where to go next.</param>
/// <param name="homes">The cookie's sign-in, for joining with an existing account.</param>
public sealed class InvitationApi(
    TenantHint tenants,
    IGrainFactory grains,
    SignInService signIn,
    FirstPartyClients clients,
    HomeAccounts homes
) {
    /// <summary>The one sentence for a link that names nothing.</summary>
    public const string NotFound = "That invitation link is not valid. Check it, or ask for a new invitation.";

    /// <summary>Joining with an account needs a complete, live sign-in on the page's origin.</summary>
    public const string SignInFirst = "Sign in with your existing account first.";

    /// <summary>Describes a link.</summary>
    /// <param name="request">The link's parts.</param>
    /// <param name="user">The cookie principal, or an anonymous one — for whether the person can join with it.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    public async Task<InvitationPageResponse> DescribeAsync(
        InvitationLookupRequest? request,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default
    ) {
        if (await GrainAsync(request?.Tenant, request?.Invitation, cancellationToken) is not { } grain
            || string.IsNullOrEmpty(request?.Token)) {
            return Missing(NotFound);
        }

        var described = await grain.DescribeAsync(request.Token);

        if (described.TryGetError(out _)) {
            return Missing(NotFound);
        }

        var invitation = described.GetValueOrThrow();
        var home = await homes.SignedInAsync(user);

        return Page(invitation, false, home, invitation.Status switch {
            InvitationStatus.Accepted => "This invitation has already been used. Sign in instead.",
            InvitationStatus.Expired => "This invitation has expired. Ask whoever sent it for a new one.",
            InvitationStatus.Withdrawn =>
                "This invitation can no longer be used. If you have joined already, sign in; otherwise ask whoever sent it.",
            InvitationStatus.Revoked => "This invitation was withdrawn by whoever sent it. Ask them if you should still join.",
            _ => string.Empty
        });
    }

    /// <summary>Accepts a link, and signs the new member in.</summary>
    /// <param name="request">The link's parts and what the person chose.</param>
    /// <param name="context">What the host knows about the request.</param>
    /// <param name="user">
    ///     The cookie principal, for <see cref="InvitationAcceptRequest.WithSignedInAccount" />. ⚠ The
    ///     endpoint passes <see langword="null" /> unless the request came from the page's origin.
    /// </param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    public async Task<InvitationApiResult> AcceptAsync(
        InvitationAcceptRequest? request,
        SignInContext context,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(context);

        if (await GrainAsync(request?.Tenant, request?.Invitation, cancellationToken) is not { } grain
            || string.IsNullOrEmpty(request?.Token)) {
            return new(Missing(NotFound));
        }

        if (request.WithSignedInAccount) {
            return new(await JoinWithAccountAsync(grain, request.Token, user));
        }

        var accepted = await grain.AcceptAsync(
            request.Token,
            request.DisplayName ?? string.Empty,
            request.Password ?? string.Empty
        );

        if (accepted.TryGetError(out var refused)) {
            return new(Refused(refused));
        }

        var invitation = accepted.GetValueOrThrow();
        var session = await signIn.OpenSessionAsync(
            invitation.TenantId,
            invitation.UserId,
            AuthenticationMethod.Password,
            context
        );

        if (session.TryGetError(out _)) {
            // The membership stands; only the sign-in did not happen, and the sign-in page works.
            return new(Page(invitation, true, null, "You are a member now. Sign in to continue."));
        }

        var principal = IdentitySessionPrincipal.Promote(
            IdentitySessionPrincipal.Build(
                invitation.TenantId,
                session.GetValueOrThrow() with { SecondFactorRequired = true }
            ),
            AuthenticationMethod.EmailOtp
        );

        return new(Page(invitation, true, null, $"Welcome to {invitation.TenantName}."), principal);
    }

    /// <summary>
    ///     Accepts a link with the account the cookie is signed into — the invited path's
    ///     <i>"signs in"</i>. The type's remarks give the rule.
    /// </summary>
    /// <remarks>
    ///     ⚠ No cookie is issued. The home account's cookie is what signs the member in from now on
    ///     (<c>AuthorizeApi</c>), and replacing it with one for this tenant would sign the person out
    ///     of their own organisation for no gain.
    /// </remarks>
    async Task<InvitationPageResponse> JoinWithAccountAsync(IInvitationGrain grain, string secret, ClaimsPrincipal? user) {
        if (await homes.SignedInAsync(user) is not { } home) {
            return Missing(SignInFirst) with { Found = true };
        }

        var described = await grain.DescribeAsync(secret);

        if (described.TryGetError(out _)) {
            return Missing(NotFound);
        }

        var invitation = described.GetValueOrThrow();

        if (!CanJoin(invitation, home)) {
            // ⚠ Checked here, before the grain spends anything: the grain can't read an account in
            // another tenant, so the address match is this host's to make or nobody's.
            return Page(
                invitation,
                false,
                home,
                home.Session.TenantId == invitation.TenantId
                    ? "You are signed into this organisation already. Sign out, then open the link again."
                    : $"You are signed in as {home.Profile.Email}, and this invitation is for {invitation.Email}. "
                    + "Sign in with that address, or accept with a new name and password."
            );
        }

        var accepted = await grain.AcceptWithHomeAccountAsync(
            secret,
            home.Profile.DisplayName,
            new() { TenantId = home.Session.TenantId, UserId = home.Session.UserId }
        );

        if (accepted.TryGetError(out var refused)) {
            return Refused(refused);
        }

        var joined = accepted.GetValueOrThrow();

        return Page(joined, true, home, $"Welcome to {joined.TenantName}. You sign in with your account at {home.Profile.Email}.");
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    async Task<IInvitationGrain?> GrainAsync(string? tenant, string? invitation, CancellationToken cancellationToken) {
        if (!Guid.TryParseExact(tenant, "N", out var tenantId)
            || !Guid.TryParseExact(invitation, "N", out var invitationId)
            || tenantId == Guid.Empty
            || invitationId == Guid.Empty) {
            return null;
        }

        var resolved = await tenants.ResolveAsync(tenant, cancellationToken);

        return resolved == tenantId
            ? grains.ForTenant(TenantHint.Qualifier(tenantId)).GetGrain<IInvitationGrain>(GrainKeys.Invitation(invitationId))
            : null;
    }

    InvitationPageResponse Page(Invitation invitation, bool succeeded, CookieSession? home, string message) =>
        new(
            true,
            invitation.Email,
            invitation.TenantName,
            invitation.Status.ToString().ToLowerInvariant(),
            succeeded,
            succeeded ? PortalUrl() : string.Empty,
            message,
            home?.Profile.Email ?? string.Empty,
            home is not null && invitation.Status == InvitationStatus.Pending && CanJoin(invitation, home)
        );

    /// <summary>
    ///     Whether <paramref name="home" /> may join with <paramref name="invitation" />: an account in
    ///     another tenant, with the invited address.
    /// </summary>
    /// <remarks>
    ///     ⚠ Both addresses were normalized by <c>GrainKeys.NormalizeEmail</c> when they were stored,
    ///     so an ordinal comparison is the right one.
    /// </remarks>
    static bool CanJoin(Invitation invitation, CookieSession home) =>
        home.Session.TenantId != invitation.TenantId
        && string.Equals(home.Profile.Email, invitation.Email, StringComparison.Ordinal);

    /// <summary>What the page is told about a refusal from the grain.</summary>
    /// <remarks>
    ///     ⚠ The grain's sentences are for the person holding the link, and say used, expired or what
    ///     was wrong with their input; a wrong secret is the one refusal kept to <see cref="NotFound" />.
    /// </remarks>
    static InvitationPageResponse Refused(Error refused) =>
        Missing(refused.Code == ErrorCode.ResourceNotFound ? NotFound : refused.Message) with {
            Found = refused.Code != ErrorCode.ResourceNotFound
        };

    string PortalUrl() =>
        clients.Find(FirstPartyClients.Portal)?.PostLogoutRedirectUris.FirstOrDefault() ?? string.Empty;

    static InvitationPageResponse Missing(string message) =>
        new(false, string.Empty, string.Empty, string.Empty, false, string.Empty, message);
}
