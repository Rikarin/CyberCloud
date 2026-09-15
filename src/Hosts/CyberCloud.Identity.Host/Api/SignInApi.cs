using CyberCloud.Core;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Credentials;
using CyberCloud.Identity.Host.Tokens;
using CyberCloud.Identity.SignIn;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Security.Claims;

namespace CyberCloud.Identity.Host.Api;

/// <summary>
///     What an endpoint decided, before it becomes an HTTP response.
/// </summary>
/// <param name="Response">The body, ready to serialize.</param>
/// <param name="Principal">
///     The session to sign in, or <see langword="null" /> when no cookie should be issued. ⚠ Held
///     separately from <paramref name="Response" /> because minting a cookie needs an
///     <c>HttpContext</c> and deciding whether to mint one does not — which is what makes every
///     decision on this type testable without a server.
/// </param>
public sealed record SignInApiResult(
    SignInResultResponse Response,
    ClaimsPrincipal? Principal = null
);

/// <summary>
///     The decisions behind the interactive sign-in endpoints. <c>IdentityEndpoints</c> maps them.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             A class rather than seven lambdas in the mapping file, because the properties that
///             matter here are not observable from a route table.
///         </b> "Every response carries a sanitized
///         return URL", "an unknown address is answered identically", "the failure string is
///         <c>UniformFailures.SignIn</c> verbatim" — each is a claim about what a handler returns,
///         and the host's test project deliberately has no <c>TestServer</c> (see its
///         <c>.csproj</c>: the question is asked of the code directly rather than of a socket). These
///         methods return values, so the tests assert on values.
///     </para>
///     <para>
///         ⚠ <b>Every grain reference goes through <c>ForTenant</c>.</b> This host is an Orleans
///         <i>client</i>, and <c>Orleans.Multitenant</c>'s tenant-separating call filter does not run
///         for a client — docs/plan/00 § The tenant-separation row, corrected. <c>CC1006</c> is what
///         keeps it true after the next edit, which is why the analyzer is referenced by this
///         project.
///     </para>
///     <para>
///         ⚠ <b>Which tenant is decided once, at the first factor, and read back everywhere
///         else.</b> The three first-factor requests carry a <c>tenant</c> hint that
///         <see cref="TenantHint" /> resolves through the platform directory before any per-tenant
///         grain is touched — an unknown value is <see cref="Reject" /> with no grain call, because
///         a grain keyed from an unauthenticated caller's string is an activation table filled by
///         whoever is probing. The cookie is stamped with the result, the passkey ticket carries it
///         between <c>begin</c> and <c>complete</c>, and the second-factor endpoints read it off the
///         cookie through <see cref="IdentitySessionPrincipal.TenantId" /> — so no later request
///         can move a session between tenants.
///     </para>
///     <para>
///         <b>Where the uniformity actually comes from.</b> The enumeration and timing hardening is
///         <see cref="SignInService" />'s — the lockout gate before any grain call, the dummy
///         Argon2id verification on the no-such-user branch, the fixed timing floor. This class must
///         not undo it, which means two rules that look like sloppiness and are not:
///     </para>
///     <list type="number">
///         <item>
///             <b>One failure builder.</b> <see cref="Reject" /> is the only way a failure leaves
///             this class, so there is no second string to drift and no branch that answers a
///             fraction of a millisecond sooner than another.
///         </item>
///         <item>
///             <b>No status-code variation.</b> Every answer is <c>200</c> with
///             <c>succeeded: false</c>, never a <c>401</c> for "wrong password" and a <c>200</c> for
///             "no such account". A status code is as good an oracle as a message.
///         </item>
///     </list>
/// </remarks>
public sealed class SignInApi(
    SignInService signIn,
    IGrainFactory grains,
    IPasskeyService passkeys,
    ITotpSecretSeam totpSecrets,
    TenantHint tenants,
    IClock clock,
    ILogger<SignInApi> logger
) {

    /// <summary>
    ///     What <c>POST /api/signin/begin</c> offers, for every address.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A constant, and the fact that it is constant is the security property.</b>
    ///         docs/plan/11 § Credentials: sign-in "returns the same response and takes the same time
    ///         whether or not the account exists". A list derived from the user's enrolled
    ///         credentials would answer that question on every keystroke of a password-reset probe —
    ///         and it would do so <i>more</i> cheaply than the sign-in itself, because the attacker
    ///         does not even need a password guess. Deriving it and then padding the timing would
    ///         leave the content as the oracle; not deriving it removes the oracle.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It also means this endpoint touches no grain</b>, which matters for the second
    ///         reason docs/plan/11 § Credentials gives: it is reachable unauthenticated, at volume,
    ///         by anybody.
    ///     </para>
    ///     <para>
    ///         <b>Why these two and not the other six.</b> Passkey and password are the credentials
    ///         that <i>start</i> a sign-in; <see cref="CredentialKind.Totp" /> and
    ///         <see cref="CredentialKind.RecoveryCode" /> are second factors, offered by
    ///         <c>/api/signin/totp</c> once a first factor has been presented.
    ///         <see cref="CredentialKind.Certificate" /> is M2.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The three OTP kinds are still absent, and the reason has changed from "nothing
    ///             can deliver one" to "they are not first factors".
    ///         </b> This remark used to say the seam
    ///         was <c>UnavailableOtpDelivery</c> "in every host in this repository", and then that no
    ///         host registered an <c>IOtpDeliverySeam</c> at all and nothing called one. Both are now
    ///         out of date: <c>UserGrain.IssueOtpAsync</c> calls the seam, <c>CyberCloud.Silo.Host</c>
    ///         composes the identity module, and <see cref="CredentialKind.EmailOtp" /> works end to
    ///         end at <c>/api/signin/otp</c>. It stays out of <i>this</i> list because a delivered
    ///         code is a <b>second</b> factor: offering it here would let anyone who knows an address
    ///         make the platform mail that address, unauthenticated and at volume, which is both a
    ///         carrier bill and an enumeration oracle spelled in somebody's inbox.
    ///         <see cref="CredentialKind.SmsOtp" /> and <see cref="CredentialKind.WhatsAppOtp" />
    ///         additionally have no verified destination to go to —
    ///         <see cref="OtpPolicy" />'s last ⚠.
    ///     </para>
    ///     <para>
    ///         ⚠ Passkey first, and the order is read by the page rather than sorted by it —
    ///         <see cref="CredentialKind" />'s own remarks make the enum's order meaningful for
    ///         exactly this reason.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<CredentialKind> Offered { get; } = [CredentialKind.Passkey, CredentialKind.Password];

    /// <summary>
    ///     <c>POST /api/signin/begin</c> — which credentials to offer for an address.
    /// </summary>
    /// <param name="request">The address typed. ⚠ Not looked up; see <see cref="Offered" />.</param>
    /// <returns>The same list, always.</returns>
    public static SignInBeginResponse Begin(SignInBeginRequest? request) {
        // ⚠ The address is deliberately unused, and a malformed one is not rejected either. A 400 for
        // "that is not an address" is a distinguishable answer, and combined with a list of
        // candidates it is a usable oracle for which strings the platform considers addresses.
        _ = request;

        return new(CredentialKindNames.Of(Offered));
    }

    /// <summary>
    ///     <c>POST /api/signin/password</c>.
    /// </summary>
    /// <param name="request">The address, the password and where to go afterwards.</param>
    /// <param name="context">What the host knows about the request beyond the credential.</param>
    /// <param name="cancellationToken">Cancels the attempt, including its timing pad.</param>
    public async Task<SignInApiResult> SignInWithPasswordAsync(
        SignInPasswordRequest? request,
        SignInContext context,
        CancellationToken cancellationToken = default
    ) {
        // ⚠ Sanitized first, once, before anything can fail. Every `return` below reads this local,
        // so there is no path on which the caller's string reaches a response — including the paths
        // added by whoever edits this method next.
        var returnUrl = ReturnUrl.Sanitize(request?.ReturnUrl);

        // ⚠ Answered before SignInService and its timing floor, so an unknown tenant is faster than a
        // wrong password. That is a deliberate trade: a slug is a tenant's public name (docs/plan/11
        // § Hosts gives tenants subdomains), and the alternative — running the floor against a
        // tenant that does not exist — would activate lockout and index grains in a tenant named by
        // the caller, which is the amplifier docs/plan/11 § Credentials forbids.
        if (await tenants.ResolveAsync(request?.Tenant, cancellationToken) is not { } tenantId) {
            IdentityLog.SignInRefused(logger, Guid.Empty, "unknown-tenant", string.Empty);
            return Reject(returnUrl);
        }

        var outcome = await signIn.SignInWithPasswordAsync(
            tenantId,
            request?.Email ?? string.Empty,
            request?.Password ?? string.Empty,
            context,
            cancellationToken
        );

        return Complete(tenantId, outcome, returnUrl);
    }

    /// <summary>
    ///     <c>POST /api/signin/passkey/begin</c> — the WebAuthn assertion challenge.
    /// </summary>
    /// <param name="request">The address typed.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>
    ///     The options to hand to <c>navigator.credentials.get()</c>, and the ticket to protect into
    ///     <see cref="PasskeyChallengeCookie" />. <see langword="null" /> only when the library
    ///     itself refuses.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>An address with no account gets a real challenge, not a refusal.</b>
    ///     <c>IPasskeyService.BeginAssertionAsync</c> is built for it — an empty credential list
    ///     produces a discoverable-credential ("usernameless") challenge, which is the same shape a
    ///     real one has. Refusing here, or answering faster, would enumerate the tenant from an
    ///     endpoint that does not even need a password guess.
    /// </remarks>
    public async Task<(PasskeyBeginResponse Response, PasskeyChallengeTicket Ticket)?> BeginPasskeyAsync(
        PasskeyBeginRequest? request,
        CancellationToken cancellationToken = default
    ) {
        var normalized = GrainKeys.NormalizeEmail(request?.Email ?? string.Empty);

        // ⚠ A malformed address takes the same path as a well-formed unknown one: an empty
        // credential list, a real challenge. The empty string is safe as the ticket's address because
        // completion re-resolves it and finds nothing. An unknown tenant takes the same path for the
        // same reason — the ticket then names no tenant, and completion refuses it.
        var address = normalized.IsSuccess ? normalized.GetValueOrThrow() : string.Empty;
        var tenantId = await tenants.ResolveAsync(request?.Tenant, cancellationToken);

        var credentials = address.Length == 0
            || tenantId is null
            || await ResolveAsync(tenantId.Value, address, cancellationToken) is not { } userId
                ? []
                : await ListPasskeysAsync(tenantId.Value, userId);

        var challenge = await passkeys.BeginAssertionAsync(credentials);
        if (challenge.TryGetError(out var error)) {
            IdentityLog.PasskeyChallengeRefused(logger, tenantId ?? Guid.Empty, error.Message);
            return null;
        }

        var issued = challenge.GetValueOrThrow();

        return (
            new(issued.OptionsJson),
            new(issued.OptionsJson, tenantId is null ? string.Empty : address, issued.ExpiresAt, tenantId ?? Guid.Empty)
        );
    }

    /// <summary>
    ///     <c>POST /api/signin/passkey/complete</c>.
    /// </summary>
    /// <param name="request">The browser's assertion, verbatim.</param>
    /// <param name="ticket">
    ///     The challenge this server issued, from <see cref="PasskeyChallengeCookie" />, or
    ///     <see langword="null" /> when there was none. ⚠ Never from the request body — see that
    ///     type.
    /// </param>
    /// <param name="context">What the host knows about the request.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    public async Task<SignInApiResult> CompletePasskeyAsync(
        PasskeyCompleteRequest? request,
        PasskeyChallengeTicket? ticket,
        SignInContext context,
        CancellationToken cancellationToken = default
    ) {
        var returnUrl = ReturnUrl.Sanitize(request?.ReturnUrl);

        if (ticket is null || ticket.Email.Length == 0 || string.IsNullOrEmpty(request?.AssertionJson)) {
            IdentityLog.PasskeyAssertionRefused(logger, ticket?.TenantId ?? Guid.Empty, "no-challenge");
            return Reject(returnUrl);
        }

        var tenantId = ticket.TenantId;
        var userId = await ResolveAsync(tenantId, ticket.Email, cancellationToken);
        if (userId is null) {
            IdentityLog.PasskeyAssertionRefused(logger, tenantId, "unknown-address");
            return Reject(returnUrl);
        }

        var credentialId = PasskeyAssertion.CredentialIdOf(request.AssertionJson);
        if (credentialId is null) {
            IdentityLog.PasskeyAssertionRefused(logger, tenantId, "malformed-assertion");
            return Reject(returnUrl);
        }

        // ⚠ By the user id already resolved, not by the address again. Resolving twice would be a
        // second email-index activation on an unauthenticated path for a value this method already
        // holds — and the two lookups could disagree if the address were reassigned between them.
        var enrolled = await ListPasskeysAsync(tenantId, userId.Value);
        var credential = enrolled.FirstOrDefault(x => string.Equals(
                x.CredentialId,
                credentialId,
                StringComparison.Ordinal
            )
        );

        if (credential is null) {
            IdentityLog.PasskeyAssertionRefused(logger, tenantId, "credential-not-enrolled");
            return Reject(returnUrl);
        }

        var verified = await passkeys.CompleteAssertionAsync(
            new() { OptionsJson = ticket.OptionsJson, ExpiresAt = ticket.ExpiresAt },
            request.AssertionJson,
            credential
        );

        if (verified.TryGetError(out var error)) {
            // ⚠ The library's message names "wrong origin" against "bad signature" against "unknown
            // attestation format". It goes to the log and NOT to the response — Fido2PasskeyService's
            // own remarks say so, because in a body it is an oracle.
            IdentityLog.PasskeyAssertionRefused(logger, tenantId, error.Message);
            return Reject(returnUrl);
        }

        // ⚠ The counter check is the cloned-authenticator signal and it is the grain's, not this
        // host's: it is the only single-threaded place per user, so it is the only place that can
        // answer without a race.
        var recorded = await Tenant(tenantId)
            .GetGrain<IUserGrain>(GrainKeys.User(userId.Value))
            .RecordPasskeyAssertionAsync(credentialId, verified.GetValueOrThrow());

        if (recorded.TryGetError(out _) || !recorded.GetValueOrThrow()) {
            IdentityLog.PasskeyAssertionRefused(logger, tenantId, "signature-counter-regressed");
            return Reject(returnUrl);
        }

        var outcome = await signIn.OpenSessionAsync(
            tenantId,
            userId.Value,
            AuthenticationMethod.Passkey,
            context
        );

        return Complete(tenantId, outcome, returnUrl);
    }

    /// <summary>
    ///     <c>POST /api/signin/totp</c> — the second factor, for a session that owes one.
    /// </summary>
    /// <param name="request">The code typed and where to go afterwards.</param>
    /// <param name="principal">
    ///     The pending session, from the cookie. ⚠ Who is answering comes from <i>here</i> and never
    ///     from the request body — a user id in the body would let anybody who owed a second factor
    ///     name somebody else's account and complete their sign-in.
    /// </param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <remarks>
    ///     ⚠ <b>Two grain calls in this order, and swapping them is a replay hole.</b>
    ///     <c>TotpAuthenticator.Verify</c> accepts a ±1 window, so a code is valid for up to ninety
    ///     seconds and can be reused within it by anybody who read it over a shoulder.
    ///     <c>ClaimTotpCounterAsync</c> is what makes it single-use, and its <c>false</c> means "valid
    ///     <i>and</i> already spent" — which must fail the sign-in rather than pass it.
    /// </remarks>
    public async Task<SignInApiResult> VerifyTotpAsync(
        SecondFactorRequest? request,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default
    ) {
        var returnUrl = ReturnUrl.Sanitize(request?.ReturnUrl);

        if (IdentitySessionPrincipal.UserId(principal) is not { } userId
            || IdentitySessionPrincipal.TenantId(principal) is not { } tenantId) {
            return Reject(returnUrl);
        }

        var user = Tenant(tenantId).GetGrain<IUserGrain>(GrainKeys.User(userId));

        var enrollment = await user.GetTotpAsync();
        if (enrollment.TryGetError(out _)) {
            IdentityLog.SecondFactorRefused(logger, tenantId, userId, "totp-not-enrolled");
            return Reject(returnUrl);
        }

        var secret = await totpSecrets.ResolveAsync(enrollment.GetValueOrThrow().SecretRef, cancellationToken);
        if (secret.TryGetError(out var vaultError)) {
            // ⚠ Reached in every host in this repository today — see UnavailableTotpSecrets. The
            // sentence naming the missing wiring goes to the log; the caller gets the uniform
            // failure, because "the vault is down" is not a fact an unauthenticated caller may learn.
            IdentityLog.SecondFactorRefused(logger, tenantId, userId, vaultError.Message);
            return Reject(returnUrl);
        }

        var verification = TotpAuthenticator.Verify(secret.GetValueOrThrow(), request?.Code, clock.UtcNow);
        if (!verification.IsValid) {
            IdentityLog.SecondFactorRefused(logger, tenantId, userId, "totp-rejected");
            return Reject(returnUrl);
        }

        var claimed = await user.ClaimTotpCounterAsync(verification.Counter);
        if (claimed.TryGetError(out _) || !claimed.GetValueOrThrow()) {
            IdentityLog.SecondFactorRefused(logger, tenantId, userId, "totp-replayed");
            return Reject(returnUrl);
        }

        return Promoted(principal, returnUrl, AuthenticationMethod.Totp);
    }

    /// <summary>
    ///     <c>POST /api/signin/otp/send</c> — mails a one-time code to the pending session's user.
    /// </summary>
    /// <param name="request">Where to go afterwards. ⚠ No address and no channel; see the remarks.</param>
    /// <param name="principal">The pending session, from the cookie.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    ///     Always <see cref="UniformFailures.OtpSent" /> with <c>succeeded: false</c> and
    ///     <c>secondFactorRequired: true</c> — the page renders the message and shows the code field.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THIS HOST GENERATES NOTHING AND STORES NOTHING.</b> The call below is the whole
    ///         handler: <c>IUserGrain.IssueOtpAsync</c> mints the code, records a keyed digest of it
    ///         and hands the plaintext to <c>IOtpDeliverySeam</c>, all inside one grain activation on
    ///         a silo. <see cref="OtpPolicy" /> carries the four properties that decide it —
    ///         single-use, restart-survival, a per-user rather than per-replica limit, and a stored
    ///         value that is useless without the vault — and each of them is a property this process
    ///         cannot have: it is an Orleans client, it holds no state, and there are N of it.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Neither the address nor the channel is a parameter, on the wire or on the grain
    ///             call.
    ///         </b> A caller holding a pending session has proven one factor; letting it name
    ///         where the second factor is sent would let it send the second factor to itself.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The answer does not depend on whether anything was sent</b>, including when the
    ///         per-user issue cap was hit and when no delivery seam is wired at all. The reason goes
    ///         to the log — <c>IdentityLog.OtpNotSent</c> carries the seam's own sentence verbatim,
    ///         which is what an operator with an unwired silo reads.
    ///     </para>
    /// </remarks>
    public async Task<SignInApiResult> SendEmailOtpAsync(
        OtpSendRequest? request,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default
    ) {
        cancellationToken.ThrowIfCancellationRequested();

        var returnUrl = ReturnUrl.Sanitize(request?.ReturnUrl);

        if (IdentitySessionPrincipal.UserId(principal) is not { } userId
            || IdentitySessionPrincipal.TenantId(principal) is not { } tenantId) {
            // No session at all, which .RequireAuthorization() makes unreachable over HTTP. The
            // uniform sign-in failure rather than OtpSent, because there is nothing to send a code to
            // and answering "on its way" would be a lie the page would act on.
            return Reject(returnUrl);
        }

        var issued = await Tenant(tenantId)
            .GetGrain<IUserGrain>(GrainKeys.User(userId))
            .IssueOtpAsync(OtpPurpose.SignIn, CredentialKind.EmailOtp);

        if (issued.TryGetError(out var refused)) {
            IdentityLog.OtpNotSent(logger, tenantId, userId, refused.Message);
        } else {
            IdentityLog.OtpIssued(logger, tenantId, userId, OtpPurpose.SignIn, CredentialKind.EmailOtp);
        }

        return new(new(false, true, returnUrl, UniformFailures.OtpSent));
    }

    /// <summary>
    ///     <c>POST /api/signin/otp</c> — the delivered second factor.
    /// </summary>
    /// <param name="request">The code typed and where to go afterwards.</param>
    /// <param name="principal">The pending session, from the cookie. See <see cref="VerifyTotpAsync" />.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <remarks>
    ///     ⚠ <b>One grain call, and the single-use guarantee is inside it rather than here.</b>
    ///     Unlike <see cref="VerifyTotpAsync" />, which verifies in this process and then claims the
    ///     counter in a second call, redeeming a delivered code is compare-and-burn in one
    ///     single-threaded activation — so there is no window between the two halves for a second
    ///     presentation to slip through. <see cref="OtpPolicy" /> property 1.
    ///     <para>
    ///         ⚠ A wrong code, an expired one, one whose five attempts are spent and one for a
    ///         challenge that was never issued are four facts and one answer here. The grain returns
    ///         <c>false</c> for all four and this method returns <see cref="Reject" /> — the reason
    ///         is distinguishable only in the log.
    ///     </para>
    /// </remarks>
    public async Task<SignInApiResult> VerifyEmailOtpAsync(
        SecondFactorRequest? request,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default
    ) {
        cancellationToken.ThrowIfCancellationRequested();

        var returnUrl = ReturnUrl.Sanitize(request?.ReturnUrl);

        if (IdentitySessionPrincipal.UserId(principal) is not { } userId
            || IdentitySessionPrincipal.TenantId(principal) is not { } tenantId) {
            return Reject(returnUrl);
        }

        var redeemed = await Tenant(tenantId)
            .GetGrain<IUserGrain>(GrainKeys.User(userId))
            .RedeemOtpAsync(OtpPurpose.SignIn, request?.Code ?? string.Empty);

        if (redeemed.TryGetError(out _) || !redeemed.GetValueOrThrow()) {
            IdentityLog.SecondFactorRefused(logger, tenantId, userId, "email-otp-rejected");
            return Reject(returnUrl);
        }

        return Promoted(principal, returnUrl, AuthenticationMethod.EmailOtp);
    }

    /// <summary>
    ///     <c>POST /api/signin/recovery-code</c> — the second factor when the phone is gone.
    /// </summary>
    /// <param name="request">The code typed and where to go afterwards.</param>
    /// <param name="principal">The pending session, from the cookie. See <see cref="VerifyTotpAsync" />.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <remarks>
    ///     Single-use and hashed in the grain, so unlike <see cref="VerifyTotpAsync" /> this path
    ///     needs no vault and works end to end today. docs/plan/11 § Credentials calls recovery codes
    ///     "the thing that prevents 'I lost my phone' tickets", which is only true if this endpoint
    ///     exists while TOTP's secret reader does not.
    /// </remarks>
    public async Task<SignInApiResult> RedeemRecoveryCodeAsync(
        SecondFactorRequest? request,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default
    ) {
        cancellationToken.ThrowIfCancellationRequested();

        var returnUrl = ReturnUrl.Sanitize(request?.ReturnUrl);

        if (IdentitySessionPrincipal.UserId(principal) is not { } userId
            || IdentitySessionPrincipal.TenantId(principal) is not { } tenantId) {
            return Reject(returnUrl);
        }

        var redeemed = await Tenant(tenantId)
            .GetGrain<IUserGrain>(GrainKeys.User(userId))
            .RedeemRecoveryCodeAsync(request?.Code ?? string.Empty);

        if (redeemed.TryGetError(out _) || !redeemed.GetValueOrThrow()) {
            IdentityLog.SecondFactorRefused(logger, tenantId, userId, "recovery-code-rejected");
            return Reject(returnUrl);
        }

        IdentityLog.RecoveryCodeBurnt(logger, tenantId, userId);

        return Promoted(principal, returnUrl, AuthenticationMethod.RecoveryCode);
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The one failure. ⚠ Every rejecting path in this class returns this and nothing else.
    /// </summary>
    static SignInApiResult Reject(string returnUrl) => new(new(false, false, returnUrl, UniformFailures.SignIn));

    static SignInApiResult Complete(Guid tenantId, Result<SignInOutcome> outcome, string returnUrl) {
        if (outcome.TryGetError(out _)) {
            return Reject(returnUrl);
        }

        var value = outcome.GetValueOrThrow();
        if (!value.Succeeded) {
            return Reject(returnUrl);
        }

        // ⚠ The cookie is issued even when a second factor is owed, carrying `cyc:2fa=pending`. It
        // has to be: it is how /api/signin/totp knows who is answering. IdentitySessionPrincipal's
        // remarks carry the argument for one stamped cookie over two cookies.
        return new(
            new(true, value.SecondFactorRequired, returnUrl, string.Empty),
            IdentitySessionPrincipal.Build(tenantId, value)
        );
    }

    /// <summary>
    ///     A promoted session — the second factor was presented, so the cookie is re-stamped.
    /// </summary>
    /// <param name="principal">The pending session's principal, as the request carried it.</param>
    /// <param name="returnUrl">The already-sanitized destination.</param>
    /// <param name="method">The factor that was presented.</param>
    /// <remarks>
    ///     ⚠ Re-stamping is a fresh <c>SignInAsync</c> and not an edit to the cookie in flight. A
    ///     cookie is an opaque protected blob to everything but the handler that minted it, so there
    ///     is no "edit" available — and wanting one is the instinct that leads to a second cookie
    ///     holding the second-factor state, which is a second credential on this origin.
    /// </remarks>
    static SignInApiResult Promoted(
        ClaimsPrincipal principal,
        string returnUrl,
        AuthenticationMethod method
    ) =>
        new(
            new(true, false, returnUrl, string.Empty),
            IdentitySessionPrincipal.Promote(principal, method)
        );

    async Task<IReadOnlyList<PasskeyCredential>> ListPasskeysAsync(Guid tenantId, Guid userId) {
        var listed = await Tenant(tenantId).GetGrain<IUserGrain>(GrainKeys.User(userId)).ListPasskeysAsync();

        // ⚠ An empty list on failure rather than a distinguishable error. A user whose grain could
        // not be read must look exactly like a user with no passkey enrolled, which in turn must
        // look exactly like an address with no account — IPasskeyService.BeginAssertionAsync is
        // built for that case and its remarks say why.
        return listed.IsSuccess ? listed.GetValueOrThrow() : [];
    }

    // ⚠ Through SignInService rather than IEmailIndexGrain directly. The host now references
    // CyberCloud.Tenancy.Contracts for the directory and the client index (TenantHint,
    // ClientResolver), so the assembly-graph reason this used to give is gone; what remains is that
    // method's remarks on the conditions under which calling it is not an enumeration oracle, and
    // one address-resolution path rather than two.
    Task<Guid?> ResolveAsync(Guid tenantId, string address, CancellationToken cancellationToken) =>
        signIn.ResolveAddressAsync(tenantId, address, cancellationToken);

    // ⚠ TenantGrainFactory and not IGrainFactory, deliberately — CC1006's discriminator is the TYPE
    // rather than the syntax, so widening this helper would erase exactly what the analyzer reads and
    // turn every call above back into an unqualified reference. SignInService carries the same note.
    TenantGrainFactory Tenant(Guid tenantId) => grains.ForTenant(TenantHint.Qualifier(tenantId));
}
