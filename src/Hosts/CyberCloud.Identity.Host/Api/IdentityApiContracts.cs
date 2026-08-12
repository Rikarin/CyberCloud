using System.Text.Json.Serialization;

namespace CyberCloud.Identity.Host.Api;

// ── The JSON the sign-in and sign-up pages exchange with this host ────────────────────────────
//
// ⚠ EVERY MEMBER CARRIES AN EXPLICIT [JsonPropertyName], INCLUDING THE ONES THE DEFAULT POLICY
// WOULD SPELL THE SAME WAY. Minimal APIs serialize with JsonSerializerDefaults.Web, whose camelCase
// policy produces these names today — so the attributes look redundant, and they are not. The
// counterparty is portal/apps/identity/src/app/identity-api.ts, a hand-written TypeScript interface
// in another repository tree with its own review; a host-wide serializer option changed for some
// other endpoint would silently rename these, and the symptom is a page whose fields are all
// `undefined` rather than a failure anybody can search for. Pinning the names here makes this
// contract independent of how the host is configured.
//
// ⚠ There is no request type carrying a tenant. See IdentityHostOptions for why the tenant is
// configured rather than asked for.

/// <summary>The body of <c>POST /api/signin/begin</c>.</summary>
/// <param name="Email">The address typed. Sent as-is; this host normalizes it.</param>
public sealed record SignInBeginRequest([property: JsonPropertyName("email")] string? Email);

/// <summary>What <c>POST /api/signin/begin</c> answers.</summary>
/// <param name="Offered">
///     The credential kinds to offer, passkey first. ⚠ <b>Identical for an address with no
///     account</b> — docs/plan/11 § Credentials.
/// </param>
public sealed record SignInBeginResponse([property: JsonPropertyName("offered")] string[] Offered);

/// <summary>The body of <c>POST /api/signin/password</c>.</summary>
/// <param name="Email">The address typed.</param>
/// <param name="Password">
///     The password typed. ⚠ In the body, never a query string — docs/plan/00 § Non-negotiables'
///     "secrets are handles" discipline keeps a credential out of the access log, the browser's
///     history and the next navigation's <c>Referer</c>.
/// </param>
/// <param name="ReturnUrl">Where to go afterwards. Sanitized before it appears in any response.</param>
public sealed record SignInPasswordRequest(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("password")] string? Password,
    [property: JsonPropertyName("returnUrl")] string? ReturnUrl
);

/// <summary>The body of <c>POST /api/signup</c>.</summary>
/// <param name="Email">The address typed.</param>
/// <param name="ReturnUrl">Where to go afterwards.</param>
public sealed record SignUpRequest(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("returnUrl")] string? ReturnUrl
);

/// <summary>The body of <c>POST /api/signin/passkey/begin</c>.</summary>
/// <param name="Email">The address typed.</param>
public sealed record PasskeyBeginRequest([property: JsonPropertyName("email")] string? Email);

/// <summary>What <c>POST /api/signin/passkey/begin</c> answers.</summary>
/// <param name="OptionsJson">
///     The WebAuthn request options. ⚠ Handed to <c>navigator.credentials.get()</c> <b>without being
///     parsed or rebuilt</b> — the challenge binding is the library's, and reserializing breaks it.
/// </param>
public sealed record PasskeyBeginResponse(
    [property: JsonPropertyName("optionsJson")] string OptionsJson
);

/// <summary>The body of <c>POST /api/signin/passkey/complete</c>.</summary>
/// <param name="AssertionJson">
///     The browser's <c>navigator.credentials.get()</c> result, serialized verbatim.
/// </param>
/// <param name="ReturnUrl">Where to go afterwards.</param>
/// <remarks>
///     ⚠ <b>The challenge is not here, and its absence is the security property.</b> An endpoint
///     that accepted the options JSON back from the caller would let an attacker present a challenge
///     they generated themselves, which reduces the assertion to a signature over data they chose.
///     <see cref="PasskeyChallengeCookie" /> is where the issued challenge actually lives.
/// </remarks>
public sealed record PasskeyCompleteRequest(
    [property: JsonPropertyName("assertionJson")] string? AssertionJson,
    [property: JsonPropertyName("returnUrl")] string? ReturnUrl
);

/// <summary>The body of <c>POST /api/signin/totp</c> and <c>POST /api/signin/recovery-code</c>.</summary>
/// <param name="Code">The code typed.</param>
/// <param name="ReturnUrl">Where to go afterwards.</param>
public sealed record SecondFactorRequest(
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("returnUrl")] string? ReturnUrl
);

/// <summary>
///     What every credential endpoint answers — <c>/api/signin/password</c>,
///     <c>/api/signin/passkey/complete</c>, <c>/api/signin/totp</c>,
///     <c>/api/signin/recovery-code</c> and <c>/api/signup</c>.
/// </summary>
/// <param name="Succeeded">Whether the caller is now authenticated.</param>
/// <param name="SecondFactorRequired">
///     Whether a second factor is still owed before the session may be used. ⚠ A passkey sets this
///     <see langword="false" /> on its own — an assertion with user verification is already two
///     factors.
/// </param>
/// <param name="ReturnUrl">
///     Where to go next. ⚠ <b>Always the output of <c>ReturnUrl.Sanitize</c></b>, so it is a
///     same-origin path or <c>/</c>, never the caller's string.
/// </param>
/// <param name="Message">
///     What to render on failure, verbatim — <c>UniformFailures.SignIn</c> or
///     <c>UniformFailures.SignUp</c>. Empty on success.
/// </param>
public sealed record SignInResultResponse(
    [property: JsonPropertyName("succeeded")] bool Succeeded,
    [property: JsonPropertyName("secondFactorRequired")] bool SecondFactorRequired,
    [property: JsonPropertyName("returnUrl")] string ReturnUrl,
    [property: JsonPropertyName("message")] string Message
);
