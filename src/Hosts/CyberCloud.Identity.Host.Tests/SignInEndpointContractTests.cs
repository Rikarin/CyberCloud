using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Api;
using CyberCloud.Identity.Host.Tests.Infrastructure;
using CyberCloud.Identity.SignIn;
using System.Security.Claims;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     The four properties the sign-in endpoints owe their callers, asserted on what the handlers
///     actually return.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>ReturnUrlTests</c> in <c>CyberCloud.Identity.Tests</c> asserts that the validator
///         is correct. This suite asserts that the endpoints <i>call</i> it, which is a different
///         claim and the one that has actually been got wrong in shipped software.</b> A perfect
///         validator next to one handler that returns <c>request.ReturnUrl</c> is an open redirect,
///         and it is invisible to every test of the validator.
///     </para>
///     <para>
///         The hostile corpus is <c>ReturnUrlTests</c>' own, deliberately duplicated rather than
///         shared. Sharing it would couple two projects for a string array, and the duplication is
///         load-bearing in the other direction: <c>portal/apps/identity/src/app/return-url.spec.ts</c>
///         carries a third copy for the client-side check, and the commit that introduced it says
///         why all three exist.
///     </para>
///     <para>
///         ⚠ Every case here is driven through a harness whose grain factory <b>throws</b>, so a
///         handler that reached for a grain on one of these paths fails loudly rather than passing.
///         See <see cref="SignInApiHarness" />.
///     </para>
/// </remarks>
public sealed class SignInEndpointContractTests {
    /// <summary>The runner's token, so a hung test is cancellable — xUnit1051.</summary>
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    ///     Return URLs that must never survive into a response.
    /// </summary>
    /// <remarks>
    ///     ⚠ The rows are the ones a block-list gets wrong: a scheme-relative <c>//host</c>, the
    ///     backslash spellings a browser normalizes, and the control characters a browser strips
    ///     before parsing. See <c>ReturnUrl</c> for why the rule is an allow-list of one shape.
    /// </remarks>
    public static TheoryData<string> Hostile =>
    [
        "https://evil.example",
        "https://evil.example/signin",
        "HTTPS://EVIL.EXAMPLE",
        "//evil.example",
        "//evil.example/path",
        "///evil.example",
        "/\\evil.example",
        "\\\\evil.example",
        "https:/\\evil.example",
        "javascript:alert(document.cookie)",
        "data:text/html,<script>alert(1)</script>",
        "/\tevil",
        "/\nevil",
        "java\nscript:alert(1)",
        "evil.example",
        "../admin",
        ""
    ];

    /// <summary>Return URLs that are same-origin paths and must survive unchanged.</summary>
    public static TheoryData<string> Safe =>
    [
        "/",
        "/signin",
        "/authorize?client_id=portal&response_type=code",
        "/resource-groups/prod?tab=access",
        "/path#fragment"
    ];

    // ── The return URL ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Hostile))]
    public async Task NoEndpointEverReturnsAHostileReturnUrl(string candidate) {
        foreach (var (endpoint, response) in await EveryResponseAsync(candidate)) {
            response.ReturnUrl.ShouldBe(
                ReturnUrl.Default,
                $"{endpoint} put a caller-supplied value into its response. Every handler must pass "
                + "the request's returnUrl through ReturnUrl.Sanitize before it reaches a response "
                + "body — the page assigns it to window.location, so a value that survives here is "
                + "an open redirect on the sign-in page, which is the classic phishing primitive."
            );
        }
    }

    [Theory]
    [MemberData(nameof(Safe))]
    public async Task ASameOriginPathSurvivesEveryEndpointUnchanged(string candidate) {
        // ⚠ The other half, and the half a rejection-only suite would ship broken: a sanitizer that
        // returned "/" for everything would pass every row above and break every real sign-in.
        foreach (var (endpoint, response) in await EveryResponseAsync(candidate)) {
            response.ReturnUrl.ShouldBe(candidate, $"{endpoint} rewrote a legitimate same-origin path.");
        }
    }

    [Fact]
    public async Task ANullReturnUrlBecomesTheDefaultRatherThanNull() {
        // The page does `window.location.assign(sanitizeReturnUrl(result.returnUrl))`, so a null
        // here is a navigation to the string "null" rather than a type error anybody notices.
        foreach (var (endpoint, response) in await EveryResponseAsync(null)) {
            response.ReturnUrl.ShouldBe(ReturnUrl.Default, $"{endpoint} answered with no destination.");
        }
    }

    // ── Enumeration ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BeginOffersTheSameListForEveryAddress() {
        // ⚠ docs/plan/11 § Credentials: sign-in "returns the same response and takes the same time
        // whether or not the account exists". These five addresses differ in every way an attacker
        // can vary one — registered-looking, absent, malformed, empty, absurd — and the answer may
        // not vary with any of them.
        var answers = new[] {
                "someone@example.com",
                "nobody-has-this-address@example.com",
                "not-an-address",
                "",
                new string('x', 400) + "@example.com"
            }
            .Select(x => SignInApi.Begin(new(x)).Offered)
            .ToList();

        foreach (var offered in answers) {
            offered.ShouldBe(
                answers[0],
                "The offered list varied with the address. A page rendering a different set for an "
                + "unknown address enumerates the tenant's users on the platform's behalf — and this "
                + "endpoint is cheaper to probe than the sign-in itself, because it needs no password "
                + "guess."
            );
        }

        // A null body is the same answer too — a 400 here would be a distinguishable response.
        SignInApi.Begin(null).Offered.ShouldBe(answers[0]);
    }

    [Fact]
    public void BeginOffersPasskeyFirst() {
        // docs/plan/11 § Credentials makes a passkey "the default offered credential at sign-up, not
        // an upsell", and the page renders the list in the order it arrives.
        SignInApi.Begin(new("someone@example.com")).Offered[0].ShouldBe("passkey");
    }

    [Fact]
    public void BeginOffersNoCredentialThatCannotWork() {
        var offered = SignInApi.Begin(new("someone@example.com")).Offered;

        // ⚠ THE REASON THIS ROW EXISTS HAS CHANGED, AND THE OLD REASON IS WORTH KEEPING BECAUSE IT
        // WAS TWICE WRONG. It first read "IOtpDeliverySeam is UnavailableOtpDelivery in every host";
        // the truth was emptier — no host registered an IOtpDeliverySeam at all and nothing called
        // one, so the refusal an operator was meant to read did not exist in any container. Both
        // halves have since landed: UserGrain.IssueOtpAsync calls the seam, CyberCloud.Silo.Host
        // composes the identity module, and EmailOtp works end to end at /api/signin/otp.
        //
        // The list still excludes all three, and now for a reason about WHAT A FIRST FACTOR IS. This
        // endpoint is unauthenticated and reachable at volume by anybody. Offering EmailOtp here
        // would let a stranger make the platform mail any address they name — a carrier bill, and an
        // enumeration oracle delivered to somebody's inbox. A delivered code is a SECOND factor,
        // collected after /api/signin/password or a passkey has already resolved the user, which is
        // why it lives behind the pending-session cookie instead.
        //
        // SmsOtp and WhatsAppOtp fail an additional test: IUserGrain holds no verified number to send
        // one to, so IssueOtpAsync refuses them rather than guessing a destination — OtpPolicy's
        // last ⚠.
        foreach (var kind in new[] { CredentialKind.EmailOtp, CredentialKind.SmsOtp, CredentialKind.WhatsAppOtp }) {
            offered.ShouldNotContain(
                CredentialKindNames.Of(kind),
                $"{kind} is a second factor (and SMS has no verified destination), so it must not be "
                + "offered as a way to START a sign-in on an unauthenticated endpoint."
            );
        }
    }

    // ── The uniform failure ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EverySignInFailureCarriesUniformFailuresSignInVerbatim() {
        var api = SignInApiHarness.Build();

        var failures = new[] {
            (Endpoint: "password", Result: await api.SignInWithPasswordAsync(
                new("not-an-address", "hunter2", "/"), new(), Ct)),
            (Endpoint: "passkey/complete (no challenge)", Result: await api.CompletePasskeyAsync(
                new("{}", "/"), null, new(), Ct)),
            (Endpoint: "totp (no session)", Result: await api.VerifyTotpAsync(
                new("000000", "/"), new ClaimsPrincipal(new ClaimsIdentity()), Ct)),
            (Endpoint: "recovery-code (no session)", Result: await api.RedeemRecoveryCodeAsync(
                new("aaaa-bbbb", "/"), new ClaimsPrincipal(new ClaimsIdentity()), Ct)),
            // ⚠ The delivered code joins the same list rather than getting its own wording. A wrong
            // code, an expired one, one whose five attempts are spent and one that was never issued
            // are four facts inside IUserGrain and one string out here.
            (Endpoint: "otp (no session)", Result: await api.VerifyEmailOtpAsync(
                new("000000", "/"), new ClaimsPrincipal(new ClaimsIdentity()), Ct))
        };

        foreach (var (endpoint, result) in failures) {
            result.Response.Succeeded.ShouldBeFalse(endpoint);

            result.Response.Message.ShouldBe(
                UniformFailures.SignIn,
                $"{endpoint} answered with its own wording. Every reason a sign-in can fail — wrong "
                + "password, no such user, locked out, suspended, no credential enrolled, a replayed "
                + "code — must produce this one string, or the message becomes the oracle the dummy "
                + "Argon2id hash is paid for to remove."
            );

            // ⚠ No cookie on a failure. A principal here would be a session minted for a caller who
            // proved nothing.
            result.Principal.ShouldBeNull(endpoint);
        }
    }

    [Fact]
    public async Task SignUpAnswersUniformFailuresSignUpVerbatim() {
        var api = SignInApiHarness.Build();

        // ⚠ Both of these must produce the identical body. docs/plan/11 § Sign-up and tenant
        // creation: the response is the same "whether or not the address was free — the mail that
        // follows is what differs, and it goes to the address either way".
        var free = await api.SignUpAsync(new("not-an-address", "/"), Ct);
        var malformed = await api.SignUpAsync(new("", "/"), Ct);

        free.Response.ShouldBe(malformed.Response);

        free.Response.Message.ShouldBe(UniformFailures.SignUp);
        free.Response.Message.ShouldNotBe(
            UniformFailures.SignIn,
            "Sign-up and sign-in have different uniform answers, and swapping them leaks which "
            + "endpoint was reached."
        );

        free.Principal.ShouldBeNull("sign-up authenticates nobody");
    }

    // ── The second factor ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ASecondFactorIsRefusedWhenTheSessionNamesNobody() {
        var api = SignInApiHarness.Build();

        // ⚠ An unauthenticated principal, which is what context.User is when the cookie is missing or
        // expired. RequireAuthorization() should stop this reaching the handler at all — this asserts
        // the handler is safe anyway, because a route metadata change is one line and this is the
        // path that mints a fully-authenticated session.
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        (await api.VerifyTotpAsync(new("000000", "/"), anonymous, Ct)).Principal.ShouldBeNull();
        (await api.RedeemRecoveryCodeAsync(new("code", "/"), anonymous, Ct)).Principal.ShouldBeNull();
        (await api.VerifyEmailOtpAsync(new("000000", "/"), anonymous, Ct)).Principal.ShouldBeNull();

        // ⚠ And the SEND half, which is the one that would otherwise mail somebody on the say-so of
        // an anonymous request. The harness's grain factory throws, so a handler that reached for a
        // grain here fails with a stack trace rather than passing quietly.
        (await api.SendEmailOtpAsync(new("/"), anonymous, Ct)).Principal.ShouldBeNull();
    }

    [Fact]
    public async Task SendingACodeNeverTellsTheCallerWhetherOneWentOut() {
        var api = SignInApiHarness.Build();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        // With no session there is nothing to send to, so this is the uniform sign-in refusal rather
        // than "on its way" — answering OtpSent here would be a lie the page acts on by showing a
        // code field that can never be satisfied.
        var noSession = await api.SendEmailOtpAsync(new("/"), anonymous, Ct);
        noSession.Response.Message.ShouldBe(UniformFailures.SignIn);

        // ⚠ And UniformFailures.OtpSent is deliberately not UniformFailures.SignIn: the two answer
        // different questions, and a page that could not tell "we tried to send you a code" from
        // "that did not work" would show the wrong screen. What OtpSent hides is which of sent,
        // refused-for-quota and no-seam-wired happened — see its own remarks.
        UniformFailures.OtpSent.ShouldNotBe(UniformFailures.SignIn);
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Drives every endpoint that returns a <see cref="SignInResultResponse" /> with one
    ///     candidate return URL.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Every one of them, by construction rather than by a list somebody maintains.</b> The
    ///     failure this guards against is a ninth endpoint added later whose author sanitizes
    ///     nothing — so a new handler that is not driven here is the gap, and the only defence is
    ///     that adding one to <see cref="SignInApi" /> without adding it here is visibly incomplete.
    /// </remarks>
    static async Task<IReadOnlyList<(string Endpoint, SignInResultResponse Response)>> EveryResponseAsync(
        string? candidate
    ) {
        var api = SignInApiHarness.Build();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        // ⚠ Each of these takes a path that answers before touching a grain — a malformed address, a
        // passkey completion with no issued challenge, a second factor with no session. That is what
        // lets the harness refuse to hand out grain references.
        return [
            ("/api/signin/password", (await api.SignInWithPasswordAsync(
                new("not-an-address", "hunter2", candidate), new(), Ct)).Response),
            ("/api/signup", (await api.SignUpAsync(new("not-an-address", candidate), Ct)).Response),
            ("/api/signin/passkey/complete", (await api.CompletePasskeyAsync(
                new("{}", candidate), null, new(), Ct)).Response),
            ("/api/signin/totp", (await api.VerifyTotpAsync(
                new("000000", candidate), anonymous, Ct)).Response),
            ("/api/signin/recovery-code", (await api.RedeemRecoveryCodeAsync(
                new("code", candidate), anonymous, Ct)).Response),
            ("/api/signin/otp", (await api.VerifyEmailOtpAsync(
                new("000000", candidate), anonymous, Ct)).Response),
            ("/api/signin/otp/send", (await api.SendEmailOtpAsync(
                new(candidate), anonymous, Ct)).Response)
        ];
    }
}
