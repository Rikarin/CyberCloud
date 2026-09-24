using CyberCloud.Core.Resources;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Tokens;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Security.Claims;
using System.Text.Json.Serialization;

namespace CyberCloud.Identity.Host.Api;

/// <summary>The body of <c>POST /api/device/lookup</c> — the code the person typed.</summary>
/// <param name="UserCode">The code, as typed: any case, the dash optional.</param>
public sealed record DeviceLookupRequest(
    [property: JsonPropertyName("userCode")]
    string? UserCode
);

/// <summary>The body of <c>POST /api/device/decision</c> — the person's answer.</summary>
/// <param name="UserCode">The code the page looked up.</param>
/// <param name="Decision"><c>allow</c> or <c>deny</c>. Anything else is refused.</param>
public sealed record DeviceDecisionRequest(
    [property: JsonPropertyName("userCode")]
    string? UserCode,
    [property: JsonPropertyName("decision")]
    string? Decision
);

/// <summary>What both device endpoints answer — what the device page renders next.</summary>
/// <param name="Found">Whether a device sign-in is waiting for this code. When false, <paramref name="Message" /> says why.</param>
/// <param name="UserCode">The code as a person reads it — <c>BCDF-GHJK</c> — or empty.</param>
/// <param name="ClientName">
///     The client's <i>registered</i> display name. ⚠ Never a value from the request: the device
///     that asked is whoever ran the command, and a name it chose would be a phisher's.
/// </param>
/// <param name="Scopes">What the device asked for.</param>
/// <param name="SignedIn">
///     Whether the cookie is a complete sign-in. When false the page sends the person to sign in,
///     with the device page as the return URL, before it asks anything.
/// </param>
/// <param name="Account">The address the person is signed in as, so they can see whose account they are lending.</param>
/// <param name="Status">
///     <c>pending</c>, <c>approved</c> or <c>denied</c> — the last two after an answer, or when the
///     code was answered already.
/// </param>
/// <param name="Message">What to render, verbatim. Empty when there is nothing to say.</param>
public sealed record DevicePageResponse(
    [property: JsonPropertyName("found")]
    bool Found,
    [property: JsonPropertyName("userCode")]
    string UserCode,
    [property: JsonPropertyName("clientName")]
    string ClientName,
    [property: JsonPropertyName("scopes")]
    string[] Scopes,
    [property: JsonPropertyName("signedIn")]
    bool SignedIn,
    [property: JsonPropertyName("account")]
    string Account,
    [property: JsonPropertyName("status")]
    string Status,
    [property: JsonPropertyName("message")]
    string Message
);

/// <summary>
///     The device page's API — look a user code up, and answer it. RFC 8628 § 3.3, docs/plan/11
///     § Protocol's Device Authorization row.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Enter the code, sign in, consent — in that order, and each step on this host.</b>
///         The person arrives from the <c>verification_uri</c> (OpenIddict's verification endpoint
///         redirects to the page) with or without the code in the link, types or confirms it, and
///         the lookup answers what is waiting and whether the cookie is a complete sign-in. If it is
///         not, the page sends the person through the existing sign-in with the device page as the
///         return URL — the sign-in asks for the organisation, because the device named none —
///         and the lookup runs again. Then the page shows what the device asked for, the account it
///         will act as, and two buttons.
///     </para>
///     <para>
///         ⚠ <b>The lookup is anonymous and metered; the answer is signed in and metered.</b> A
///         person must be able to type the code before signing in, so the lookup takes no cookie —
///         and is therefore a guess anybody can make, which is why both routes are in the per-IP
///         <c>code-verify</c> bucket with every other route that takes a code
///         (<c>IdentityRateLimits.CodeVerify</c>). A code that does not normalize, that no
///         authorization holds, and that has expired are one sentence, so the lookup says nothing
///         to a guesser but "no".
///     </para>
///     <para>
///         ⚠ <b>The answer is bound to the cookie, and only a complete one.</b> Approving is lending
///         the device the person's session, so the tenant, the person, <c>auth_time</c> and
///         <c>amr</c> come off the session grain and the cookie — <c>AuthorizeApi.WithCookieMethods</c>'
///         union, for its reason — and never from the body. A session pending its second factor,
///         or revoked, answers "sign in first". The host also refuses an answer whose <c>Origin</c>
///         is not the page's (<c>IdentityEndpoints.MapDevicePage</c>): the flow's known weakness
///         is a device started by somebody else whose code the victim is talked into approving, and
///         a cross-site request that approved it without the page would remove the one screen that
///         shows the victim what they are approving.
///     </para>
///     <para>
///         Every outcome is a <c>200</c> with <see cref="DevicePageResponse.Found" /> and a
///         sentence, for the reason <c>SignInApi</c> gives every failure a <c>200</c>.
///     </para>
/// </remarks>
/// <param name="devices">The grains a code names.</param>
/// <param name="clients">The first-party registrations, for the registered display name.</param>
/// <param name="grains">The cluster, for the session and the profile. ⚠ Every reference through <c>ForTenant</c>.</param>
/// <param name="logger">Where an answer is recorded.</param>
public sealed class DeviceApi(
    DeviceFlow devices,
    FirstPartyClients clients,
    IGrainFactory grains,
    ILogger<DeviceApi> logger
) {
    /// <summary>
    ///     The identity app's device page, under <c>IdentityHostOptions.SignInPageBaseUri</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Not <c>/device</c>: that path is OpenIddict's device authorization endpoint, which
    ///     claims every method on it, so a page there would be a <c>GET</c> OpenIddict answers with
    ///     an error before the app is reached.
    /// </remarks>
    public const string PagePath = "/device-code";

    /// <summary>The one sentence for a code that is malformed, unknown or expired.</summary>
    public const string NotFound = "That code is not valid. Check it, or run the sign-in again on your device.";

    /// <summary>The code was answered already — a user code is used once.</summary>
    public const string AlreadyUsed = "That code has already been used. Run the sign-in again on your device.";

    /// <summary>The cookie is missing, pending a second factor, or its session was revoked.</summary>
    public const string SignInFirst = "Sign in to continue.";

    /// <summary>The answer was neither <c>allow</c> nor <c>deny</c>.</summary>
    public const string NoAnswer = "Choose Allow or Deny.";

    /// <summary>What the page says after an approval.</summary>
    public const string Approved = "Done. Return to your device — it is signing in.";

    /// <summary>What the page says after a refusal.</summary>
    public const string Denied = "Denied. The device was not signed in.";

    /// <summary>Looks a typed code up.</summary>
    /// <param name="request">What the page posted.</param>
    /// <param name="user">The cookie principal, or an anonymous one.</param>
    /// <param name="cancellationToken">Cancels the grain calls.</param>
    public async Task<DevicePageResponse> LookupAsync(
        DeviceLookupRequest? request,
        ClaimsPrincipal? user,
        CancellationToken cancellationToken = default
    ) {
        if (DeviceCodes.NormalizeUserCode(request?.UserCode) is not { } userCode) {
            return Missing(NotFound);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var described = await devices.Grain(userCode).DescribeAsync();

        if (described.TryGetError(out _)) {
            return Missing(NotFound);
        }

        var session = await SignedInAsync(user);

        return Describe(userCode, described.GetValueOrThrow(), session?.Profile.Email, string.Empty);
    }

    /// <summary>Records the person's answer.</summary>
    /// <param name="request">What the page posted.</param>
    /// <param name="user">The cookie principal.</param>
    /// <param name="cancellationToken">Cancels the grain calls.</param>
    public async Task<DevicePageResponse> DecideAsync(
        DeviceDecisionRequest? request,
        ClaimsPrincipal? user,
        CancellationToken cancellationToken = default
    ) {
        if (DeviceCodes.NormalizeUserCode(request?.UserCode) is not { } userCode) {
            return Missing(NotFound);
        }

        bool allow;

        switch (request?.Decision) {
            case "allow":
                allow = true;
                break;
            case "deny":
                allow = false;
                break;
            default:
                return Missing(NoAnswer);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (await SignedInAsync(user) is not { } session) {
            return Missing(SignInFirst);
        }

        var approval = allow
            ? new DeviceApproval {
                TenantId = session.Session.TenantId,
                UserId = session.Session.UserId,
                InteractiveSessionId = session.Session.SessionId,
                AuthenticatedAt = session.Session.AuthenticatedAt,
                Methods = [.. session.Methods],
                Email = session.Profile.Email,
                DisplayName = session.Profile.DisplayName
            }
            : null;

        var decided = await devices.Grain(userCode).DecideAsync(approval);

        if (decided.TryGetError(out var refused)) {
            return Missing(refused.Code == ErrorCode.Conflict ? AlreadyUsed : NotFound);
        }

        GrantLog.DeviceAuthorizationDecided(logger, session.Session.TenantId, session.Session.UserId, allow);

        return Describe(userCode, decided.GetValueOrThrow(), session.Profile.Email, allow ? Approved : Denied);
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A complete, live cookie session, with the person's profile and the union of both factors.</summary>
    sealed record SignedInSession(SessionDescriptor Session, UserProfile Profile, List<AuthenticationMethod> Methods);

    async Task<SignedInSession?> SignedInAsync(ClaimsPrincipal? user) {
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

        if (profile.TryGetError(out _)) {
            return null;
        }

        // ⚠ The grain records the first factor and the cookie both — AuthorizeApi.WithCookieMethods.
        var methods = new List<AuthenticationMethod>(session.GetValueOrThrow().Methods);

        foreach (var claim in user!.FindAll(AccessTokenClaims.AuthenticationMethods)) {
            if (AuthenticationMethodNames.Parse(claim.Value) is { } method && !methods.Contains(method)) {
                methods.Add(method);
            }
        }

        return new(session.GetValueOrThrow(), profile.GetValueOrThrow(), methods);
    }

    DevicePageResponse Describe(
        string userCode,
        DeviceAuthorizationDescriptor described,
        string? account,
        string message
    ) {
        var status = described.Status switch {
            DeviceAuthorizationStatus.Pending => "pending",
            DeviceAuthorizationStatus.Denied => "denied",
            _ => "approved"
        };

        return new(
            true,
            DeviceCodes.Display(userCode),
            clients.Find(described.ClientId)?.DisplayName ?? described.ClientId,
            [.. described.Scopes],
            account is not null,
            account ?? string.Empty,
            status,
            message.Length == 0 && described.Status != DeviceAuthorizationStatus.Pending ? AlreadyUsed : message
        );
    }

    static DevicePageResponse Missing(string message) =>
        new(false, string.Empty, string.Empty, [], false, string.Empty, string.Empty, message);
}
