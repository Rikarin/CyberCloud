using System.Text.Json.Serialization;

namespace CyberCloud.Identity.Host.Api;

// ── The JSON the sign-up page exchanges with this host — docs/plan/11 § Sign-up and tenant creation
//
// ⚠ EVERY MEMBER CARRIES AN EXPLICIT [JsonPropertyName], for the reason IdentityApiContracts.cs
// gives at length: the counterparty is portal/apps/identity/src/app/identity-api.ts, hand-written,
// and a serializer option changed for some other endpoint would otherwise rename these silently.
//
// ⚠ Four endpoints, four request records, and the shape of each is a security decision:
//
//   begin     carries an address and a return URL and NOTHING that names a sign-up — the ticket
//             cookie is what names one, and the browser cannot read it.
//   verify    carries the code alone. WHICH sign-up is answering comes from the ticket.
//   passkey   carries the display name the authenticator shows; the challenge goes into the passkey
//   /begin    cookie and never into a body, exactly as at sign-in — PasskeyChallengeCookie.
//   complete  carries what the person chose and their credential; the address, the ids and the
//             proof come from the ticket and the grain, never from the body.

/// <summary>The body of <c>POST /api/signup/begin</c>.</summary>
/// <param name="Email">The address typed. Sent as-is; this host normalizes it.</param>
/// <param name="ReturnUrl">Where to go afterwards. Sanitized before it appears in any response.</param>
public sealed record SignUpBeginRequest(
    [property: JsonPropertyName("email")]
    string? Email,
    [property: JsonPropertyName("returnUrl")]
    string? ReturnUrl
);

/// <summary>What <c>POST /api/signup/begin</c> answers — the same thing for every input.</summary>
/// <param name="Sent">
///     Always <c>true</c>. ⚠ A malformed address, a taken one and a free one all produce it, on the
///     same timing floor; the code that follows is what differs. docs/plan/11 § Credentials.
/// </param>
/// <param name="ReturnUrl">Where to go afterwards — always the output of <c>ReturnUrl.Sanitize</c>.</param>
public sealed record SignUpBeginResponse(
    [property: JsonPropertyName("sent")]
    bool Sent,
    [property: JsonPropertyName("returnUrl")]
    string ReturnUrl
);

/// <summary>The body of <c>POST /api/signup/verify</c>.</summary>
/// <param name="Code">The six digits typed.</param>
public sealed record SignUpVerifyRequest(
    [property: JsonPropertyName("code")]
    string? Code);

/// <summary>What <c>POST /api/signup/verify</c> answers.</summary>
/// <param name="Verified">
///     Whether the address is now proven. ⚠ One <c>false</c> for a wrong code, an expired one, a
///     burnt one and a sign-up that never began — <c>ISignUpGrain.VerifyAsync</c>.
/// </param>
public sealed record SignUpVerifyResponse(
    [property: JsonPropertyName("verified")]
    bool Verified);

/// <summary>The body of <c>POST /api/signup/passkey/begin</c>.</summary>
/// <param name="DisplayName">What the authenticator's own prompt calls the person.</param>
public sealed record SignUpPasskeyBeginRequest(
    [property: JsonPropertyName("displayName")]
    string? DisplayName);

/// <summary>The credential a sign-up completes with — one of two shapes.</summary>
/// <param name="Kind"><c>passkey</c> or <c>password</c>.</param>
/// <param name="AttestationJson">
///     For <c>passkey</c>: the browser's <c>navigator.credentials.create()</c> result, serialized
///     verbatim. ⚠ The challenge it answers is in the passkey cookie, never here.
/// </param>
/// <param name="Password">
///     For <c>password</c>: the password typed. ⚠ In the body, never a query string — the same
///     discipline <c>SignInPasswordRequest.Password</c> records.
/// </param>
public sealed record SignUpCredential(
    [property: JsonPropertyName("kind")]
    string? Kind,
    [property: JsonPropertyName("attestationJson")]
    string? AttestationJson,
    [property: JsonPropertyName("password")]
    string? Password
) {
    /// <summary>The <see cref="Kind" /> spelling for a passkey.</summary>
    public const string PasskeyKind = "passkey";

    /// <summary>The <see cref="Kind" /> spelling for a password.</summary>
    public const string PasswordKind = "password";
}

/// <summary>The body of <c>POST /api/signup/complete</c>.</summary>
/// <param name="DisplayName">What to call the person.</param>
/// <param name="OrganizationName">
///     The tenant's display name. Its slug is <c>ResourceNaming.Slugify</c> of it, and the slug
///     has to be free.
/// </param>
/// <param name="Credential">The passkey attestation or the password.</param>
/// <param name="ReturnUrl">
///     Where to go afterwards — the <c>/authorize</c> request that started this. The response
///     carries it back with <c>tenant=&lt;new tenant&gt;</c> set in its query.
/// </param>
public sealed record SignUpCompleteRequest(
    [property: JsonPropertyName("displayName")]
    string? DisplayName,
    [property: JsonPropertyName("organizationName")]
    string? OrganizationName,
    [property: JsonPropertyName("credential")]
    SignUpCredential? Credential,
    [property: JsonPropertyName("returnUrl")]
    string? ReturnUrl
);

/// <summary>What <c>POST /api/signup/complete</c> answers, and what the closed surface answers to everything.</summary>
/// <param name="Succeeded">Whether the tenant exists and the caller is signed into it.</param>
/// <param name="TenantId">The new tenant, in <c>D</c> form. Empty on failure.</param>
/// <param name="ReturnUrl">
///     Where to go next: the request's return URL with <c>tenant=&lt;TenantId&gt;</c> set in its
///     query on success, so the resumed <c>/authorize</c> names the tenant the cookie is for.
///     ⚠ Always sanitized, so a same-origin path or <c>/</c>.
/// </param>
/// <param name="Message">
///     What to render on failure, verbatim. ⚠ Distinguishable on purpose — "verify your address
///     first", "that organisation name is taken", the naming rule's sentence, the password rule's
///     sentence, or "something went wrong" — because by this point the caller has proven an address
///     and the answers are about their own input, not about whether an account exists.
/// </param>
public sealed record SignUpCompleteResponse(
    [property: JsonPropertyName("succeeded")]
    bool Succeeded,
    [property: JsonPropertyName("tenantId")]
    string TenantId,
    [property: JsonPropertyName("returnUrl")]
    string ReturnUrl,
    [property: JsonPropertyName("message")]
    string Message
);
