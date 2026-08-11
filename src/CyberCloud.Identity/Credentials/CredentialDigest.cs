using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.Identity.Credentials;

/// <summary>
///     The one-way and constant-time primitives every credential in this module is built from.
/// </summary>
/// <remarks>
///     ⚠ <b>Every comparison of anything credential-shaped goes through
///     <see cref="FixedTimeEquals(string,string)" />.</b> <see cref="string.Equals(string,string)" />
///     returns as soon as two characters differ, so the time it takes reveals how many leading
///     characters were right — which turns a 128-bit recovery code into ten sequential
///     sixteen-way guesses. This is not theoretical for a handle that is checked once per refresh.
/// </remarks>
static class CredentialDigest {
    /// <summary>The SHA-256 of <paramref name="value" />, base64url, unpadded.</summary>
    /// <param name="value">What to hash. UTF-8 encoded first.</param>
    /// <remarks>
    ///     Used for refresh handles and recovery codes — both are high-entropy random strings, so a
    ///     plain hash is the right primitive and a password KDF would be an expensive no-op. ⚠ This
    ///     is <b>not</b> for passwords, which are low-entropy and get
    ///     <see cref="Argon2idPasswordHasher" />.
    /// </remarks>
    public static string Sha256(string value) {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
        return Base64Url(hash);
    }

    /// <summary>
    ///     A truncated digest of a client address — what a session stores instead of an IP.
    /// </summary>
    /// <param name="address">The address, as text.</param>
    /// <remarks>
    ///     ⚠ Truncated to 16 hexadecimal characters, matching <c>GrainKeys</c>' index digests. The
    ///     point is not to make the address unrecoverable — the space of IPv4 addresses is small
    ///     enough to enumerate against any hash — it is that the durable and hot tiers, and therefore
    ///     every backup, hold something that is not an address. The address itself goes to the
    ///     telemetry pipeline as a structured field, where docs/plan/11 § Auditing's retention and
    ///     redaction policy reaches it.
    /// </remarks>
    public static string AddressDigest(string? address) {
        if (string.IsNullOrEmpty(address)) {
            return string.Empty;
        }

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(address), hash);
        return Convert.ToHexStringLower(hash[..8]);
    }

    /// <summary><paramref name="byteCount" /> cryptographically random bytes, base64url.</summary>
    /// <param name="byteCount">How many bytes of entropy.</param>
    public static string RandomHandle(int byteCount) => Base64Url(RandomNumberGenerator.GetBytes(byteCount));

    /// <summary>Base64url without padding — safe in a URL, a header and a JSON string.</summary>
    /// <param name="bytes">What to encode.</param>
    public static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    ///     Whether two strings are equal, in time that does not depend on where they first differ.
    /// </summary>
    /// <param name="left">One value.</param>
    /// <param name="right">The other.</param>
    /// <returns><c>true</c> when the two are byte-for-byte identical.</returns>
    /// <remarks>
    ///     ⚠ <b>The lengths still leak, and that is accepted rather than overlooked.</b>
    ///     <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte},ReadOnlySpan{byte})" />
    ///     returns immediately when the lengths differ. Everything compared here is a fixed-length
    ///     digest or a fixed-length code, so the length carries no information about the value.
    /// </remarks>
    public static bool FixedTimeEquals(string? left, string? right) {
        if (left is null || right is null) {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right)
        );
    }
}
