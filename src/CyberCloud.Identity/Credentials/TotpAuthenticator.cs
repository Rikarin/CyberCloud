using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.Identity.Credentials;

/// <summary>
///     What verifying a TOTP code produced: whether it was right, and which step it was right for.
/// </summary>
/// <remarks>
///     ⚠ The step number is the point. A Boolean would be enough to decide the sign-in and not enough
///     to block the replay — docs/plan/11 § Credentials asks for "replay-blocked per (user,
///     counter)", so the caller has to know which counter to burn.
/// </remarks>
public readonly record struct TotpVerification {
    /// <summary>Whether the code matched a step inside the drift window.</summary>
    public bool IsValid { get; init; }

    /// <summary>The RFC 6238 step the code was generated for. Meaningless when invalid.</summary>
    public long Counter { get; init; }

    /// <summary>The failure.</summary>
    public static TotpVerification Invalid { get; } = new() { IsValid = false, Counter = 0 };
}

/// <summary>
///     RFC 6238 TOTP, in-house. docs/plan/11 § Credentials — "~200 lines, ±1 window, replay-blocked
///     per (user, counter)".
/// </summary>
/// <remarks>
///     <para>
///         <b>Why in-house rather than a package.</b> TOTP is HMAC-SHA1 over a big-endian counter,
///         dynamic truncation, and a modulo — about eighty lines including the base32 codec. The
///         interesting parts are the two policy decisions around it (the drift window, and where the
///         replay block lives), and those are ours whichever library computes the HMAC. A dependency
///         here would be a supply-chain surface on the authentication path in exchange for eighty
///         lines.
///     </para>
///     <para>
///         ⚠ <b>HMAC-SHA1, and that is correct rather than an oversight.</b> RFC 6238 permits
///         SHA-256 and SHA-512, and essentially no authenticator app implements them — a QR code that
///         says <c>algorithm=SHA256</c> is silently generated as SHA1 by several popular apps, which
///         produces codes that never verify and a support queue nobody can debug. SHA1's weaknesses
///         are collision weaknesses; HMAC-SHA1's preimage security is unaffected and 160 bits of
///         shared secret is the security parameter that matters here.
///     </para>
/// </remarks>
public static class TotpAuthenticator {
    /// <summary>A fresh 160-bit shared secret, base32-encoded for a QR code.</summary>
    /// <remarks>
    ///     ⚠ This value goes to the vault and to the user's screen, and to nowhere else. In
    ///     particular it does not go into grain state — docs/plan/11 § Credentials: "secret stored as
    ///     a Vault <c>SecretRef</c>, never in grain state".
    /// </remarks>
    public static string GenerateSecret() =>
        Base32Encode(RandomNumberGenerator.GetBytes(TotpParameters.SecretBytes));

    /// <summary>
    ///     The <c>otpauth://</c> URI an authenticator app scans.
    /// </summary>
    /// <param name="issuer">The tenant or product name shown in the app.</param>
    /// <param name="account">The account label — usually the email address.</param>
    /// <param name="base32Secret">The shared secret from <see cref="GenerateSecret" />.</param>
    /// <remarks>
    ///     ⚠ The issuer appears twice, in the label and as a parameter, and both are required. Older
    ///     apps read only the label prefix and newer ones only the parameter; omitting either gives
    ///     some fraction of users an entry called "Unknown".
    /// </remarks>
    public static string BuildProvisioningUri(string issuer, string account, string base32Secret) {
        var label = Uri.EscapeDataString(issuer) + ":" + Uri.EscapeDataString(account);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"otpauth://totp/{label}?secret={base32Secret}&issuer={Uri.EscapeDataString(issuer)}"
            + $"&algorithm=SHA1&digits={TotpParameters.Digits}&period={TotpParameters.PeriodSeconds}"
        );
    }

    /// <summary>The step number for an instant. RFC 6238's <c>T</c>.</summary>
    /// <param name="instant">When.</param>
    public static long CounterFor(DateTimeOffset instant) =>
        instant.ToUnixTimeSeconds() / TotpParameters.PeriodSeconds;

    /// <summary>The code for one step. RFC 4226's HOTP, with RFC 6238's counter.</summary>
    /// <param name="base32Secret">The shared secret.</param>
    /// <param name="counter">The step.</param>
    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification =
            "RFC 6238's default and the only algorithm authenticator apps interoperate on. SHA-1's "
            + "published weaknesses are collision weaknesses; HMAC-SHA1 depends on neither collision "
            + "resistance nor preimage resistance of the compression function, and no attack on it is "
            + "known. The alternative is worse in practice rather than in theory: several widely used "
            + "apps silently ignore `algorithm=SHA256` in an otpauth URI and generate SHA-1 codes "
            + "anyway, which produces codes that never verify and a failure nobody can debug from "
            + "either end. The security parameter that matters here is the 160-bit shared secret."
    )]
    public static string Compute(string base32Secret, long counter) {
        var secret = Base32Decode(base32Secret);

        Span<byte> message = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(message, counter);

        Span<byte> mac = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(secret, message, mac);

        // Dynamic truncation, RFC 4226 § 5.3. The low nibble of the last byte picks the offset, and
        // the top bit of the selected word is masked off so the result is positive on every platform.
        var offset = mac[^1] & 0x0F;
        var binary = ((mac[offset] & 0x7F) << 24)
            | ((mac[offset + 1] & 0xFF) << 16)
            | ((mac[offset + 2] & 0xFF) << 8)
            | (mac[offset + 3] & 0xFF);

        var modulus = (int)Math.Pow(10, TotpParameters.Digits);
        return (binary % modulus).ToString(CultureInfo.InvariantCulture).PadLeft(TotpParameters.Digits, '0');
    }

    /// <summary>
    ///     Whether <paramref name="candidate" /> is a live code, and for which step.
    /// </summary>
    /// <param name="base32Secret">The user's shared secret, resolved from the vault.</param>
    /// <param name="candidate">The digits typed.</param>
    /// <param name="now">The current instant, from <c>IClock</c>.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>±1 step, per docs/plan/11 § Credentials, and every step in the window is
    ///         evaluated even after a match.</b> Returning early on the first hit would make the
    ///         verification time depend on <i>which</i> step matched, and the step is a function of
    ///         the victim's clock skew — a small leak, but a free one to close and the kind that
    ///         compounds with the ones that are not free.
    ///     </para>
    ///     <para>
    ///         ⚠ A valid answer here is <b>not</b> a successful authentication. The counter must
    ///         still be claimed through <c>IUserGrain.ClaimTotpCounterAsync</c>, which is what stops
    ///         the same code being used twice inside its 90-second window.
    ///     </para>
    /// </remarks>
    public static TotpVerification Verify(string base32Secret, string? candidate, DateTimeOffset now) {
        if (string.IsNullOrEmpty(base32Secret) || string.IsNullOrEmpty(candidate)) {
            return TotpVerification.Invalid;
        }

        if (candidate.Length != TotpParameters.Digits) {
            return TotpVerification.Invalid;
        }

        var current = CounterFor(now);
        var result = TotpVerification.Invalid;

        for (var drift = -TotpParameters.DriftSteps; drift <= TotpParameters.DriftSteps; drift++) {
            var counter = current + drift;

            if (CredentialDigest.FixedTimeEquals(Compute(base32Secret, counter), candidate) && !result.IsValid) {
                result = new() { IsValid = true, Counter = counter };
            }
        }

        return result;
    }

    // ── Base32, RFC 4648 § 6 ───────────────────────────────────────────────────────────────────
    //
    // ⚠ Not base64, and the two are routinely confused here. Every authenticator app reads the
    // `secret` parameter of an otpauth URI as unpadded uppercase base32; handing one base64 produces
    // an app that shows six digits and never verifies, with no error anywhere.

    const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    static string Base32Encode(ReadOnlySpan<byte> bytes) {
        var builder = new StringBuilder((bytes.Length * 8 + 4) / 5);

        var buffer = 0;
        var bits = 0;

        foreach (var b in bytes) {
            buffer = (buffer << 8) | b;
            bits += 8;

            while (bits >= 5) {
                builder.Append(Base32Alphabet[(buffer >> (bits - 5)) & 0x1F]);
                bits -= 5;
            }
        }

        if (bits > 0) {
            builder.Append(Base32Alphabet[(buffer << (5 - bits)) & 0x1F]);
        }

        return builder.ToString();
    }

    static byte[] Base32Decode(string value) {
        var bytes = new List<byte>(value.Length * 5 / 8);

        var buffer = 0;
        var bits = 0;

        foreach (var c in value) {
            if (c == '=') {
                continue;
            }

            var index = Base32Alphabet.IndexOf(char.ToUpperInvariant(c), StringComparison.Ordinal);
            if (index < 0) {
                throw new FormatException(
                    $"'{c}' is not a base32 character. A TOTP shared secret is RFC 4648 base32 — the "
                    + "uppercase alphabet plus the digits 2 to 7 — which is what every authenticator "
                    + "app reads the otpauth 'secret' parameter as."
                );
            }

            buffer = (buffer << 5) | index;
            bits += 5;

            if (bits < 8) {
                continue;
            }

            bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
            bits -= 8;
        }

        return [.. bytes];
    }
}
