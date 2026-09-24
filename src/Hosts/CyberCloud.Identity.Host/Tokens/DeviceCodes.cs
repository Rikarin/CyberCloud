using CyberCloud.Identity.Credentials;
using System.Security.Cryptography;

namespace CyberCloud.Identity.Host.Tokens;

/// <summary>
///     The shape of RFC 8628's two codes on this server: the user code a person types and the device
///     code the device holds. docs/plan/11 § Protocol.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The user code is eight letters from twenty consonants, and each part of that is RFC
///         8628 § 6.1's advice.</b> No vowels, so no code spells a word somebody would rather not
///         type; no digits, so nothing reads as both <c>0</c> and <c>O</c> or <c>1</c> and <c>I</c>;
///         upper case only, and matched case-insensitively with the dash and any space ignored, so a
///         person on a phone keyboard can type it however it comes out. Twenty to the eighth is about
///         2³⁴·⁵ — against the per-IP limit on the page (<c>IdentityRateLimits.CodeVerify</c>) and a
///         ten-minute life, a guesser expects to wait years for a hit, which is the calculation the
///         same section asks a server to make.
///     </para>
///     <para>
///         ⚠ <b>The device code carries its user code, and the secret beside it is what makes it a
///         credential.</b> <c>{userCode}.{secret}</c>: the user code names the
///         <c>IDeviceAuthorizationGrain</c> (<c>GrainKeys.DeviceAuthorization</c> says why the grain
///         is keyed by it), and the secret is 256 random bits whose SHA-256 the grain compares. A
///         person who reads the user code off somebody's screen therefore holds half of a device code
///         and cannot poll for its tokens — the grain answers a wrong secret as if no authorization
///         existed, and leaves the real device's interval alone.
///     </para>
/// </remarks>
public static class DeviceCodes {
    /// <summary>The characters a user code is drawn from — RFC 8628 § 6.1's consonant set.</summary>
    public const string UserCodeAlphabet = "BCDFGHJKLMNPQRSTVWXZ";

    /// <summary>How many characters a user code has.</summary>
    public const int UserCodeLength = 8;

    /// <summary>The parameter the verification URI carries the code in, as OpenIddict names it.</summary>
    public const string UserCodeParameter = "user_code";

    /// <summary>
    ///     Ten minutes for both codes — RFC 8628 § 3.2's example is thirty, and this is shorter on
    ///     purpose: a person who walked away from the terminal starts again for a fresh code, and a
    ///     code photographed off a screen is worth ten minutes to whoever took the picture.
    /// </summary>
    public static TimeSpan Lifetime { get; } = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     The polling interval the device is told — five seconds, RFC 8628 § 3.2's default for a
    ///     server that says nothing, said out loud.
    /// </summary>
    public static TimeSpan PollingInterval { get; } = TimeSpan.FromSeconds(5);

    /// <summary>A fresh user code, uniformly from <see cref="UserCodeAlphabet" />, unformatted.</summary>
    public static string NewUserCode() =>
        string.Create(
            UserCodeLength,
            0,
            static (span, _) => {
                for (var i = 0; i < span.Length; i++) {
                    span[i] = UserCodeAlphabet[RandomNumberGenerator.GetInt32(UserCodeAlphabet.Length)];
                }
            }
        );

    /// <summary>
    ///     What a person typed, as the canonical user code, or <see langword="null" /> when it cannot
    ///     be one.
    /// </summary>
    /// <param name="typed">The code as typed or as it arrived in a query — any case, dashes and spaces allowed.</param>
    /// <remarks>
    ///     ⚠ Called before any grain is touched, so a value outside the alphabet never becomes a
    ///     grain key — the argument <c>TokenApi</c> makes for a service principal's client id.
    /// </remarks>
    public static string? NormalizeUserCode(string? typed) {
        if (string.IsNullOrWhiteSpace(typed) || typed.Length > 32) {
            return null;
        }

        Span<char> code = stackalloc char[UserCodeLength];
        var length = 0;

        foreach (var c in typed) {
            if (c is '-' or ' ') {
                continue;
            }

            var upper = c is >= 'a' and <= 'z' ? (char)(c - 32) : c;

            if (length == UserCodeLength || !UserCodeAlphabet.Contains(upper, StringComparison.Ordinal)) {
                return null;
            }

            code[length++] = upper;
        }

        return length == UserCodeLength ? new string(code) : null;
    }

    /// <summary>The code as a person reads it: <c>BCDF-GHJK</c>.</summary>
    /// <param name="userCode">A normalized user code.</param>
    public static string Display(string userCode) {
        ArgumentNullException.ThrowIfNull(userCode);

        return userCode.Length == UserCodeLength ? userCode[..4] + "-" + userCode[4..] : userCode;
    }

    /// <summary>A fresh device secret — 256 random bits, base64url.</summary>
    public static string NewSecret() => CredentialDigest.RandomHandle(32);

    /// <summary>The device code for a user code and a secret — <c>{userCode}.{secret}</c>.</summary>
    /// <param name="userCode">A normalized user code.</param>
    /// <param name="secret">What <see cref="NewSecret" /> returned.</param>
    public static string Compose(string userCode, string secret) => userCode + "." + secret;

    /// <summary>Splits a device code back into its user code and secret.</summary>
    /// <param name="deviceCode">The <c>device_code</c> parameter, verbatim.</param>
    /// <param name="userCode">The normalized user code, when this returns <see langword="true" />.</param>
    /// <param name="secret">The secret, when this returns <see langword="true" />.</param>
    /// <returns><see langword="true" /> when the value has the shape <see cref="Compose" /> produces.</returns>
    public static bool TryParse(string? deviceCode, out string userCode, out string secret) {
        userCode = string.Empty;
        secret = string.Empty;

        if (string.IsNullOrEmpty(deviceCode) || deviceCode.Length > 128) {
            return false;
        }

        var dot = deviceCode.IndexOf('.', StringComparison.Ordinal);

        if (dot != UserCodeLength
            || NormalizeUserCode(deviceCode[..dot]) is not { } normalized
            || !string.Equals(normalized, deviceCode[..dot], StringComparison.Ordinal)
            || deviceCode.Length - dot - 1 < 32) {
            return false;
        }

        userCode = normalized;
        secret = deviceCode[(dot + 1)..];

        return true;
    }
}
