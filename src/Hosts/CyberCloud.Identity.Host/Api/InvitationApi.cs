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
/// <param name="DisplayName">The name they chose.</param>
/// <param name="Password">
///     The password for their user in this tenant. ⚠ In the body, never a query string — the rule
///     <c>SignInPasswordRequest</c> states.
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
    string? Password
);

/// <summary>What both invitation endpoints answer.</summary>
/// <param name="Found">Whether the link names an invitation. When false, <paramref name="Message" /> says why.</param>
/// <param name="Email">The address invited — the account being created.</param>
/// <param name="TenantName">The organisation.</param>
/// <param name="Status"><c>pending</c>, <c>accepted</c>, <c>expired</c> or <c>withdrawn</c>.</param>
/// <param name="Succeeded">Whether an accept made the person a member and signed them in.</param>
/// <param name="PortalUrl">Where to go next — the portal, from its registration; empty when none is configured.</param>
/// <param name="Message">What to render, verbatim.</param>
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
    string Message
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
///         ⚠ <b>The same page for a new person and for one who already has a Cyber Cloud
///         account.</b> docs/plan/11 § Sign-up puts a user in exactly one tenant, so a colleague who
///         signed up for their own organisation is still a new user <i>here</i>: the page asks them
///         for a name and a password for this organisation, and their account elsewhere is
///         untouched — same address, two users, as the one-user-one-tenant rule means. What makes
///         both safe without a delivered code is the link itself, which went to the address and
///         nowhere else (<see cref="IInvitationGrain" />'s remarks). A browser already signed into
///         another tenant gets a cookie for this one on accepting, as a sign-in to a second tenant
///         does.
///     </para>
///     <para>
///         ⚠ <b>The tenant is resolved through the directory before any tenant grain is
///         touched</b>, as <see cref="TenantHint" /> does for every first-factor request: the link's
///         tenant is caller input, and a made-up one must activate nothing. The ids are parsed in
///         their <c>N</c> form only, for the same reason. Every wrong link — unknown tenant, unknown
///         invitation, wrong secret — is one sentence.
///     </para>
///     <para>
///         ⚠ <b>Accepting signs the person in</b>, with a session stamped as a password and a
///         delivered code — the link is the code, as the enrolment code is at sign-up
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
public sealed class InvitationApi(
    TenantHint tenants,
    IGrainFactory grains,
    SignInService signIn,
    FirstPartyClients clients
) {
    /// <summary>The one sentence for a link that names nothing.</summary>
    public const string NotFound = "That invitation link is not valid. Check it, or ask for a new invitation.";

    /// <summary>Describes a link.</summary>
    /// <param name="request">The link's parts.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    public async Task<InvitationPageResponse> DescribeAsync(
        InvitationLookupRequest? request,
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

        return Page(invitation, false, invitation.Status switch {
            InvitationStatus.Accepted => "This invitation has already been used. Sign in instead.",
            InvitationStatus.Expired => "This invitation has expired. Ask whoever sent it for a new one.",
            InvitationStatus.Withdrawn =>
                "This invitation can no longer be used. If you have joined already, sign in; otherwise ask whoever sent it.",
            _ => string.Empty
        });
    }

    /// <summary>Accepts a link, and signs the new member in.</summary>
    /// <param name="request">The link's parts and what the person chose.</param>
    /// <param name="context">What the host knows about the request.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    public async Task<InvitationApiResult> AcceptAsync(
        InvitationAcceptRequest? request,
        SignInContext context,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(context);

        if (await GrainAsync(request?.Tenant, request?.Invitation, cancellationToken) is not { } grain
            || string.IsNullOrEmpty(request?.Token)) {
            return new(Missing(NotFound));
        }

        var accepted = await grain.AcceptAsync(
            request.Token,
            request.DisplayName ?? string.Empty,
            request.Password ?? string.Empty
        );

        if (accepted.TryGetError(out var refused)) {
            // ⚠ The grain's sentences are for the person holding the link, and say used, expired or
            // what was wrong with their input; a wrong secret is the one refusal kept to NotFound.
            return new(
                Missing(refused.Code == ErrorCode.ResourceNotFound ? NotFound : refused.Message) with {
                    Found = refused.Code != ErrorCode.ResourceNotFound
                }
            );
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
            return new(Page(invitation, true, "You are a member now. Sign in to continue."));
        }

        var principal = IdentitySessionPrincipal.Promote(
            IdentitySessionPrincipal.Build(
                invitation.TenantId,
                session.GetValueOrThrow() with { SecondFactorRequired = true }
            ),
            AuthenticationMethod.EmailOtp
        );

        return new(Page(invitation, true, $"Welcome to {invitation.TenantName}."), principal);
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

    InvitationPageResponse Page(Invitation invitation, bool succeeded, string message) =>
        new(
            true,
            invitation.Email,
            invitation.TenantName,
            invitation.Status.ToString().ToLowerInvariant(),
            succeeded,
            succeeded ? PortalUrl() : string.Empty,
            message
        );

    string PortalUrl() =>
        clients.Find(FirstPartyClients.Portal)?.PostLogoutRedirectUris.FirstOrDefault() ?? string.Empty;

    static InvitationPageResponse Missing(string message) =>
        new(false, string.Empty, string.Empty, string.Empty, false, string.Empty, message);
}
