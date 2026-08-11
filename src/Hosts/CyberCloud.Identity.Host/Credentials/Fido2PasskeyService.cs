using CyberCloud.Core;
using CyberCloud.Core.Time;
using CyberCloud.Identity.Contracts;
using Fido2NetLib;
using Fido2NetLib.Objects;
using System.Text;
using System.Text.Json;

namespace CyberCloud.Identity.Host.Credentials;

/// <summary>
///     <see cref="IPasskeyService" /> over <c>Fido2.AspNet</c>. docs/plan/11 § Credentials.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THIS IS THE ONE FILE.</b> docs/plan/02 § Data, transport, Kubernetes asks for the
///         library to be "wrapped behind <c>IPasskeyService</c> so replacing it is one file", and
///         this is that file: no other type in the tree names <c>Fido2NetLib</c>. The interface is
///         declared in <c>CyberCloud.Identity.Contracts</c> in our own types, which is what makes the
///         claim true — a wrapper whose signatures leaked the library's types would put the library
///         in every caller.
///     </para>
///     <para>
///         ⚠ <b>Pinned at 4.0.1 stable, not the 4.0.0-beta9 docs/plan/02's register names.</b> That
///         register justified the beta with "the only maintained .NET WebAuthn library, and it is a
///         beta with no stable successor" — which is refuted: 4.0.0 and 4.0.1 are both published
///         stable. The wrapper stays regardless. Its value was never only that the library was a
///         beta; it is that WebAuthn libraries in .NET have a history of going unmaintained, and the
///         cost of finding out is one file rather than the module.
///     </para>
///     <para>
///         <b>The challenge is carried in the options JSON and handed back on completion.</b> Nothing
///         about a challenge is stored server-side, which means no state to expire, no grain to
///         activate on an unauthenticated path, and no way for a challenge to be replayed against a
///         different user — the options are echoed back and the library checks them against the
///         response. The <see cref="PasskeyRegistrationChallenge.ExpiresAt" /> the caller receives is
///         advisory for the UI; the binding guarantee is the library's.
///     </para>
/// </remarks>
public sealed class Fido2PasskeyService(IFido2 fido2, IClock clock) : IPasskeyService {
    /// <summary>How long a challenge is offered for. Short — a challenge is a nonce, not a session.</summary>
    public static TimeSpan ChallengeLifetime { get; } = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    public Task<Result<PasskeyRegistrationChallenge>> BeginRegistrationAsync(PasskeyRegistrationRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        var options = fido2.RequestNewCredential(
            new() {
                User = new() {
                    // ⚠ The user handle is the GUID's raw bytes, NOT the email. WebAuthn stores the
                    // handle on the authenticator and it is the one identifier that cannot be
                    // changed afterwards — putting an address there would mean an email change
                    // orphaned every enrolled passkey, and would print the address on the
                    // authenticator's own screens.
                    Id = request.UserId.ToByteArray(),
                    Name = request.Email,
                    DisplayName = request.DisplayName
                },
                // Enrolled credentials are excluded so the authenticator declines to register a
                // second credential for an account it already holds one for, rather than producing a
                // duplicate the user then has to tell apart in a list.
                ExcludeCredentials = [.. request.Existing.Select(x => new PublicKeyCredentialDescriptor(Decode(x.CredentialId)))],
                AuthenticatorSelection = new() {
                    // ⚠ Required, not preferred. A resident (discoverable) credential with user
                    // verification is what makes a passkey a single-step, two-factor sign-in —
                    // docs/plan/11 § Credentials makes it the default credential rather than an
                    // upsell, and a passkey that still needs a password afterwards is not that.
                    ResidentKey = ResidentKeyRequirement.Required,
                    UserVerification = UserVerificationRequirement.Required
                },
                // ⚠ None, deliberately. Requesting attestation returns a certificate that identifies
                // the authenticator model and, for some vendors, the device — which is a privacy
                // liability and needs an MDS blob store to verify against. It buys a policy we do not
                // have ("only these authenticator models"), so it is not asked for.
                AttestationPreference = AttestationConveyancePreference.None
            }
        );

        return Task.FromResult(
            Result<PasskeyRegistrationChallenge>.Success(
                new() {
                    OptionsJson = options.ToJson(),
                    UserId = request.UserId,
                    ExpiresAt = clock.UtcNow + ChallengeLifetime
                }
            )
        );
    }

    /// <inheritdoc />
    public async Task<Result<PasskeyCredential>> CompleteRegistrationAsync(
        PasskeyRegistrationChallenge challenge,
        string attestationJson
    ) {
        ArgumentNullException.ThrowIfNull(challenge);

        AuthenticatorAttestationRawResponse? response;
        try {
            response = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(attestationJson);
        } catch (JsonException) {
            return Malformed<PasskeyCredential>("attestation");
        }

        if (response is null) {
            return Malformed<PasskeyCredential>("attestation");
        }

        try {
            var credential = await fido2.MakeNewCredentialAsync(
                new() {
                    AttestationResponse = response,
                    OriginalOptions = CredentialCreateOptions.FromJson(challenge.OptionsJson),
                    // ⚠ Always unique from the library's point of view. The real uniqueness check is
                    // IUserGrain.AddPasskeyAsync, which is single-threaded per user and therefore the
                    // only place that can answer without a race. Answering here would need a lookup
                    // this service has no grain factory for, by design — see the .csproj.
                    IsCredentialIdUniqueToUserCallback = (_, _) => Task.FromResult(true)
                }
            );

            return Result<PasskeyCredential>.Success(
                new() {
                    CredentialId = Encode(credential.Id),
                    PublicKey = Encode(credential.PublicKey),
                    AaGuid = credential.AaGuid,
                    SignCount = credential.SignCount,
                    Label = string.Empty,
                    CreatedAt = clock.UtcNow,
                    LastUsedAt = clock.UtcNow
                }
            );
        } catch (Fido2VerificationException exception) {
            // ⚠ The library's message is returned as the failure detail and must NOT be returned to
            // the browser verbatim by a caller — it distinguishes "wrong origin" from "bad signature"
            // from "unknown attestation format", which is useful in a log and is an oracle in a
            // response body.
            return Result<PasskeyCredential>.Failure(
                ErrorCode.AuthorizationFailed,
                "That authenticator response could not be verified: " + exception.Message
            );
        }
    }

    /// <inheritdoc />
    public Task<Result<PasskeyAssertionChallenge>> BeginAssertionAsync(IReadOnlyList<PasskeyCredential> credentials) {
        ArgumentNullException.ThrowIfNull(credentials);

        // ⚠ An empty list is a legitimate, indistinguishable answer. docs/plan/11 § Credentials wants
        // sign-in to look the same whether or not the account exists, so a caller asking for a
        // challenge for an address with no account passes an empty list and gets a challenge of
        // exactly the same shape — a discoverable-credential flow, which is what a real usernameless
        // sign-in looks like anyway.
        var options = fido2.GetAssertionOptions(
            new() {
                AllowedCredentials = [.. credentials.Select(x => new PublicKeyCredentialDescriptor(Decode(x.CredentialId)))],
                UserVerification = UserVerificationRequirement.Required
            }
        );

        return Task.FromResult(
            Result<PasskeyAssertionChallenge>.Success(
                new() { OptionsJson = options.ToJson(), ExpiresAt = clock.UtcNow + ChallengeLifetime }
            )
        );
    }

    /// <inheritdoc />
    public async Task<Result<uint>> CompleteAssertionAsync(
        PasskeyAssertionChallenge challenge,
        string assertionJson,
        PasskeyCredential credential
    ) {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(credential);

        AuthenticatorAssertionRawResponse? response;
        try {
            response = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(assertionJson);
        } catch (JsonException) {
            return Malformed<uint>("assertion");
        }

        if (response is null) {
            return Malformed<uint>("assertion");
        }

        try {
            var result = await fido2.MakeAssertionAsync(
                new() {
                    AssertionResponse = response,
                    OriginalOptions = AssertionOptions.FromJson(challenge.OptionsJson),
                    StoredPublicKey = Decode(credential.PublicKey),
                    StoredSignatureCounter = credential.SignCount,
                    // The credential was looked up by its id before this call, so the handle-owns-id
                    // question is already answered by how we got here.
                    IsUserHandleOwnerOfCredentialIdCallback = (_, _) => Task.FromResult(true)
                }
            );

            return Result<uint>.Success(result.SignCount);
        } catch (Fido2VerificationException exception) {
            return Result<uint>.Failure(
                ErrorCode.AuthorizationFailed,
                "That authenticator response could not be verified: " + exception.Message
            );
        }
    }

    /// <summary>Base64url without padding — the encoding WebAuthn uses on the wire.</summary>
    static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static byte[] Decode(string value) {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }

    static Result<T> Malformed<T>(string what)
        where T : notnull =>
        Result<T>.Failure(
            ErrorCode.InvalidRequestBody,
            $"That is not a WebAuthn {what} response. The browser's "
            + "`navigator.credentials` result is posted as JSON, verbatim."
        );

    // Kept so the encoding helpers above are provably symmetric under test rather than by reading.
    internal static string RoundTrip(string value) => Encode(Decode(value));

    internal static string EncodeUtf8(string value) => Encode(Encoding.UTF8.GetBytes(value));
}
