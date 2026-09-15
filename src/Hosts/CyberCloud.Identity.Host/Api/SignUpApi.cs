using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.SignUp;
using CyberCloud.Identity.SignIn;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Orleans.Multitenant;
using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;

namespace CyberCloud.Identity.Host.Api;

/// <summary>
///     What a sign-up endpoint decided, before it becomes an HTTP response.
/// </summary>
/// <param name="Body">The JSON body, ready to serialize — one of the <c>SignUpContracts.cs</c> responses.</param>
/// <param name="Ticket">A sign-up ticket to issue, or <see langword="null" />.</param>
/// <param name="Challenge">A passkey registration challenge to issue, or <see langword="null" />.</param>
/// <param name="Principal">The session to sign in, or <see langword="null" /> when no cookie should be issued.</param>
/// <param name="ClearTicket">Whether the sign-up ticket is spent.</param>
/// <param name="Unauthorized">
///     Whether the call carried no usable ticket and answers <c>401</c> with no body — the one
///     status-code variation on this surface, and the one a page can act on (start again).
/// </param>
/// <remarks>
///     ⚠ Everything a handler needs an <c>HttpContext</c> for is a field here rather than an
///     action, for the reason <c>SignInApiResult</c> gives: deciding whether to mint a cookie needs
///     no server, so the decision is testable without one.
/// </remarks>
public sealed record SignUpApiResult(
    object Body,
    SignUpTicket? Ticket = null,
    PasskeyChallengeTicket? Challenge = null,
    ClaimsPrincipal? Principal = null,
    bool ClearTicket = false,
    bool Unauthorized = false
);

/// <summary>
///     The decisions behind <c>/api/signup/*</c>. <c>IdentityEndpoints.MapSignUp</c> maps them.
///     docs/plan/11 § Sign-up and tenant creation.
/// </summary>
/// <remarks>
///     <para>
///         <b>Four calls, and what each one may learn.</b> <c>begin</c> is unauthenticated and
///         answers the same body for every address on <c>SignInService</c>'s timing floor —
///         docs/plan/11 § Credentials' enumeration rule, applied to the one endpoint that touches a
///         grain for a stranger's input. It may, because the grain it touches is keyed by a random
///         id the host minted and not by the address: a stranger cannot choose which activation is
///         created, so the amplifier that rule names does not apply. <c>verify</c>, <c>passkey/begin</c>
///         and <c>complete</c> are authenticated by the ticket cookie and answer only about the
///         sign-up the ticket names, which the caller started, so their answers may be specific.
///     </para>
///     <para>
///         ⚠ <b>The address is never taken from a request after <c>begin</c>.</b> <c>complete</c>
///         creates a user for the address the grain holds — the one that was proven — and a body
///         field would let a caller enrol an address nobody has proven. The same rule keeps the
///         tenant id, the user id and the subscription id in the grain.
///     </para>
///     <para>
///         ⚠ <b>Closed is a body, not a status.</b> With <c>IdentityHostOptions.SelfServeSignUp</c>
///         off every call answers <see cref="ClosedMessage" /> in a <c>200</c> and touches
///         nothing, so a page renders one sentence and a probe learns nothing about tickets.
///     </para>
///     <para>
///         ⚠ <b>Every grain reference goes through <c>ForTenant</c>.</b> The sign-up grain is a
///         platform-tenant grain, so the qualification is <see cref="Guid.Empty" />; CC1006 keeps
///         the discipline after the next edit.
///     </para>
/// </remarks>
public sealed class SignUpApi(
    IGrainFactory grains,
    IPasskeyService passkeys,
    SignUpOrchestrator orchestrator,
    IOptions<IdentityHostOptions> options,
    SignInOptions signInOptions,
    IClock clock,
    ILogger<SignUpApi> logger
) {
    /// <summary>What every call answers while sign-up is closed.</summary>
    public const string ClosedMessage = "Sign-up is not open on this deployment.";

    /// <summary><c>complete</c> before <c>verify</c> answered <c>true</c>.</summary>
    public const string NotVerifiedMessage = "Verify your email address first.";

    /// <summary>The organisation's slug is held by another tenant.</summary>
    public const string SlugTakenMessage = "That organisation name is taken.";

    /// <summary>A create step failed. The ticket stays valid and <c>complete</c> resumes at that step.</summary>
    public const string StepFailedMessage = "Something went wrong creating your organisation. Try again.";

    /// <summary>A password sign-up with no password — refused before anything is created.</summary>
    public const string PasswordRequiredMessage = "A password is required.";

    /// <summary>The query parameter the rewritten return URL carries the new tenant in.</summary>
    public const string TenantParameter = "tenant";

    readonly IdentityHostOptions options = options.Value;

    /// <summary>
    ///     <c>POST /api/signup/begin</c> — starts a sign-up, or re-sends its code.
    /// </summary>
    /// <param name="request">The address and where to go afterwards.</param>
    /// <param name="ticket">The caller's existing ticket, or <see langword="null" />.</param>
    /// <param name="cancellationToken">Cancels the attempt, including its timing pad.</param>
    /// <returns>
    ///     <see cref="SignUpBeginResponse" /> with <c>sent: true</c>, always — a malformed address
    ///     touches no grain and issues no ticket and still answers it, on the same floor.
    /// </returns>
    public async Task<SignUpApiResult> BeginAsync(
        SignUpBeginRequest? request,
        SignUpTicket? ticket,
        CancellationToken cancellationToken = default
    ) {
        var returnUrl = ReturnUrl.Sanitize(request?.ReturnUrl);

        if (!options.SelfServeSignUp) {
            return Closed(returnUrl);
        }

        var started = Stopwatch.GetTimestamp();

        try {
            var normalized = GrainKeys.NormalizeEmail(request?.Email ?? string.Empty);
            if (normalized.TryGetError(out _)) {
                // ⚠ No grain, no ticket, the same body. A 400 here would be a distinguishable answer
                // for which strings the platform considers addresses — SignInApi.Begin.
                return new(new SignUpBeginResponse(true, returnUrl));
            }

            var address = normalized.GetValueOrThrow();
            var signupId = ticket?.SignupId ?? Guid.NewGuid();
            var begun = await SignUp(signupId).BeginAsync(address);

            if (begun.TryGetError(out var refused) && ticket is not null && refused.Code == ErrorCode.Conflict) {
                // The ticket names a sign-up that has completed, or proved a different address. A
                // person who came back to the first step wants a new one, not a refusal.
                signupId = Guid.NewGuid();
                begun = await SignUp(signupId).BeginAsync(address);
            }

            if (begun.TryGetError(out var failed)) {
                // ⚠ The reason — the per-sign-up issue cap, or the delivery seam's sentence — goes
                // to the log and not to the caller, who is told the code is on its way either way.
                // Telling them "you have used your five codes" would say when the window rolls over.
                logger.LogInformation(
                    "Sign-up {SignupId} could not issue a code: {Reason}",
                    signupId,
                    failed.Message
                );

                return new(new SignUpBeginResponse(true, returnUrl), ticket);
            }

            return new(
                new SignUpBeginResponse(true, returnUrl),
                new SignUpTicket(signupId, clock.UtcNow + SignUpPolicy.Lifetime)
            );
        } finally {
            await PadAsync(started, cancellationToken);
        }
    }

    /// <summary><c>POST /api/signup/verify</c> — answers the enrolment code.</summary>
    /// <param name="request">The code typed.</param>
    /// <param name="ticket">The caller's ticket, or <see langword="null" /> for <c>401</c>.</param>
    public async Task<SignUpApiResult> VerifyAsync(SignUpVerifyRequest? request, SignUpTicket? ticket) {
        if (!options.SelfServeSignUp) {
            return Closed(ReturnUrl.Default);
        }

        if (ticket is null) {
            return Unauthorized();
        }

        var verified = await SignUp(ticket.SignupId).VerifyAsync(request?.Code ?? string.Empty);

        return new(new SignUpVerifyResponse(verified.IsSuccess && verified.GetValueOrThrow()));
    }

    /// <summary>
    ///     <c>POST /api/signup/passkey/begin</c> — the WebAuthn registration challenge.
    /// </summary>
    /// <param name="request">The display name the authenticator shows.</param>
    /// <param name="ticket">The caller's ticket, or <see langword="null" /> for <c>401</c>.</param>
    /// <returns>
    ///     The options for <c>navigator.credentials.create()</c> and the challenge to protect into
    ///     <see cref="PasskeyChallengeCookie" />; an empty options string, and no challenge, for a
    ///     sign-up that has not proven its address or a library that refused.
    /// </returns>
    public async Task<SignUpApiResult> BeginPasskeyAsync(SignUpPasskeyBeginRequest? request, SignUpTicket? ticket) {
        if (!options.SelfServeSignUp) {
            return Closed(ReturnUrl.Default);
        }

        if (ticket is null) {
            return Unauthorized();
        }

        var described = await SignUp(ticket.SignupId).GetAsync();
        if (described.TryGetError(out _) || !described.GetValueOrThrow().Verified) {
            return new(new PasskeyBeginResponse(string.Empty));
        }

        var signup = described.GetValueOrThrow();

        var challenge = await passkeys.BeginRegistrationAsync(
            new() { UserId = signup.UserId, Email = signup.Email, DisplayName = request?.DisplayName ?? string.Empty }
        );

        if (challenge.TryGetError(out var error)) {
            IdentityLog.PasskeyChallengeRefused(logger, Guid.Empty, error.Message);
            return new(new PasskeyBeginResponse(string.Empty));
        }

        var issued = challenge.GetValueOrThrow();

        return new(
            new PasskeyBeginResponse(issued.OptionsJson),
            Challenge: new(issued.OptionsJson, signup.Email, issued.ExpiresAt, Guid.Empty, PasskeyChallengeKind.Registration)
        );
    }

    /// <summary>
    ///     <c>POST /api/signup/complete</c> — verifies the credential, then creates everything.
    /// </summary>
    /// <param name="request">What the person chose and their credential.</param>
    /// <param name="ticket">The caller's ticket, or <see langword="null" /> for <c>401</c>.</param>
    /// <param name="challenge">
    ///     The registration challenge from <see cref="PasskeyChallengeCookie" />, taken whatever
    ///     the credential kind, or <see langword="null" />.
    /// </param>
    /// <param name="context">What the host knows about the request, for the session it opens.</param>
    /// <param name="cancellationToken">Cancels the run between steps.</param>
    public async Task<SignUpApiResult> CompleteAsync(
        SignUpCompleteRequest? request,
        SignUpTicket? ticket,
        PasskeyChallengeTicket? challenge,
        SignInContext context,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(context);

        var returnUrl = ReturnUrl.Sanitize(request?.ReturnUrl);

        if (!options.SelfServeSignUp) {
            return Closed(returnUrl);
        }

        if (ticket is null) {
            return Unauthorized();
        }

        var described = await SignUp(ticket.SignupId).GetAsync();
        if (described.TryGetError(out _)) {
            return Failed(returnUrl, StepFailedMessage);
        }

        var signup = described.GetValueOrThrow();
        if (!signup.Verified) {
            return Failed(returnUrl, NotVerifiedMessage);
        }

        // ── The name, before anything is created ───────────────────────────────────────────────
        var organizationName = (request?.OrganizationName ?? string.Empty).Trim();
        var slug = ResourceNaming.Slugify(organizationName);

        var named = ResourceNaming.Validate(slug, "organisation name");
        if (named.TryGetError(out var badName)) {
            return Failed(returnUrl, badName.Message);
        }

        // ── a. The credential, before anything is created ──────────────────────────────────────
        string? password = null;
        PasskeyCredential? passkey = null;

        switch (request?.Credential?.Kind) {
            case SignUpCredential.PasskeyKind:
                if (challenge is null
                    || challenge.Kind != PasskeyChallengeKind.Registration
                    || string.IsNullOrEmpty(request.Credential.AttestationJson)) {
                    IdentityLog.PasskeyAssertionRefused(logger, Guid.Empty, "no-registration-challenge");
                    return Failed(returnUrl, StepFailedMessage);
                }

                var registered = await passkeys.CompleteRegistrationAsync(
                    new() { OptionsJson = challenge.OptionsJson, UserId = signup.UserId, ExpiresAt = challenge.ExpiresAt },
                    request.Credential.AttestationJson
                );

                if (registered.TryGetError(out var refused)) {
                    // ⚠ The library's message goes to the log and not to the response, as at sign-in.
                    IdentityLog.PasskeyAssertionRefused(logger, Guid.Empty, refused.Message);
                    return Failed(returnUrl, StepFailedMessage);
                }

                passkey = registered.GetValueOrThrow();
                break;

            case SignUpCredential.PasswordKind:
                password = request.Credential.Password ?? string.Empty;

                // ⚠ The one rule UserGrain.SetPasswordAsync enforces, checked here for the reason
                // the passkey is verified here: the credential step runs after the tenant and the
                // user exist, and a refusal there would leave a tenant holding the slug with a
                // credential-less user — the retry resumes at the credential step, so the person
                // could no longer change the organisation name, and an abandoned attempt would
                // reserve the slug until the sweep that is still owed. Nothing is created for a
                // credential that cannot be set.
                if (password.Length == 0) {
                    return Failed(returnUrl, PasswordRequiredMessage);
                }

                break;

            default:
                return Failed(returnUrl, StepFailedMessage);
        }

        // ── b–h ────────────────────────────────────────────────────────────────────────────────
        var outcome = await orchestrator.CompleteAsync(
            new(
                signup,
                (request.DisplayName ?? string.Empty).Trim(),
                organizationName,
                slug,
                password,
                passkey,
                context
            ),
            cancellationToken
        );

        if (!outcome.Succeeded) {
            return Failed(returnUrl, MessageFor(outcome));
        }

        var tenantId = signup.TenantId.ToString("D", CultureInfo.InvariantCulture);

        return new(
            new SignUpCompleteResponse(true, tenantId, WithTenant(returnUrl, tenantId), string.Empty),
            Principal: Principal(signup.TenantId, outcome.Session!, passkey is not null),
            ClearTicket: true
        );
    }

    /// <summary>
    ///     Sets <c>tenant=&lt;tenantId&gt;</c> in a same-origin return URL's query, replacing any
    ///     value it carried.
    /// </summary>
    /// <param name="returnUrl">The already-sanitized destination.</param>
    /// <param name="tenantId">The new tenant, in <c>D</c> form.</param>
    /// <remarks>
    ///     The destination is the <c>/authorize</c> request that sent the person here, and the
    ///     resumed request has to name the tenant the cookie is for: the host resolves the
    ///     <c>tenant</c> hint before it reads the cookie, and a cookie for a different tenant is a
    ///     re-prompt. Replacing rather than appending, because the original request may have
    ///     carried the remembered tenant of the previous sign-in.
    /// </remarks>
    public static string WithTenant(string returnUrl, string tenantId) {
        ArgumentNullException.ThrowIfNull(returnUrl);

        var fragmentAt = returnUrl.IndexOf('#', StringComparison.Ordinal);
        var fragment = fragmentAt < 0 ? string.Empty : returnUrl[fragmentAt..];
        var withoutFragment = fragmentAt < 0 ? returnUrl : returnUrl[..fragmentAt];

        var queryAt = withoutFragment.IndexOf('?', StringComparison.Ordinal);
        var path = queryAt < 0 ? withoutFragment : withoutFragment[..queryAt];

        var query = queryAt < 0
            ? new Dictionary<string, StringValues>(StringComparer.Ordinal)
            : QueryHelpers.ParseQuery(withoutFragment[queryAt..]);

        query[TenantParameter] = tenantId;

        var rebuilt = QueryString.Create(
            query.SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, string?>(pair.Key, value)))
        );

        return path + rebuilt.ToUriComponent() + fragment;
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The session cookie's principal, stamped as fully authenticated.</summary>
    /// <remarks>
    ///     ⚠ A password sign-up is <c>["pwd", "otp"]</c> and satisfied, not pending: the address was
    ///     proven by the enrolment code seconds ago, which is the delivered second factor — asking
    ///     for it again would be asking the same question twice. A passkey is two factors on its own
    ///     and stays <c>["swk"]</c>, exactly as at sign-in.
    /// </remarks>
    static ClaimsPrincipal Principal(Guid tenantId, SignInOutcome session, bool byPasskey) =>
        byPasskey
            ? IdentitySessionPrincipal.Build(tenantId, session with { SecondFactorRequired = false })
            : IdentitySessionPrincipal.Promote(
                IdentitySessionPrincipal.Build(tenantId, session with { SecondFactorRequired = true }),
                AuthenticationMethod.EmailOtp
            );

    /// <summary>The sentence a failed step answers — distinguishable, on purpose, see the type's remarks.</summary>
    static string MessageFor(SignUpOutcome outcome) {
        if (outcome.Error is not { } error) {
            return StepFailedMessage;
        }

        if (outcome.FailedStep == SignUpStep.TenantCreated && error.Code == ErrorCode.Conflict) {
            return SlugTakenMessage;
        }

        if (outcome.FailedStep == SignUpStep.TenantCreated && error.Code == ErrorCode.InvalidResourceName) {
            return error.Message;
        }

        if (outcome.FailedStep == SignUpStep.CredentialSet && error.Code == ErrorCode.InvalidRequestBody) {
            return error.Message;
        }

        return StepFailedMessage;
    }

    static SignUpApiResult Failed(string returnUrl, string message) =>
        new(new SignUpCompleteResponse(false, string.Empty, returnUrl, message));

    static SignUpApiResult Closed(string returnUrl) =>
        new(new SignUpCompleteResponse(false, string.Empty, returnUrl, ClosedMessage));

    static SignUpApiResult Unauthorized() => new(new SignUpCompleteResponse(false, string.Empty, ReturnUrl.Default, string.Empty), Unauthorized: true);

    ISignUpGrain SignUp(Guid signupId) =>
        grains.ForTenant(Guid.Empty.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ISignUpGrain>(GrainKeys.SignUp(signupId));

    // ⚠ The same fixed floor SignInService pads to, for the same reason: a malformed address that
    // answers in microseconds beside a real one that waits for a grain and a delivery is a timing
    // oracle for which strings are addresses. SignInService.PadAsync carries the argument for a
    // fixed floor over a random delay.
    async Task PadAsync(long startedAt, CancellationToken cancellationToken) {
        if (signInOptions.MinimumDuration <= TimeSpan.Zero) {
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        if (elapsed >= signInOptions.MinimumDuration) {
            return;
        }

        try {
            await Task.Delay(signInOptions.MinimumDuration - elapsed, cancellationToken);
        } catch (OperationCanceledException) {
            // A cancelled pad is a cancelled request; the answer already decided stands.
        }
    }
}
