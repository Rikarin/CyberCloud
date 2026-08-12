using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.Identity.Credentials;

/// <summary>
///     Mints a one-time code and turns one into the value that may be stored — docs/plan/11
///     § Credentials, and <see cref="OtpPolicy" /> for why the storing happens in a grain.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The whole of this type is the difference between a hash and a keyed hash, and for a
///         six-digit code that difference is everything.</b> <see cref="OtpPolicy.Digits" /> is six,
///         so the space is a million values. An unkeyed SHA-256 of one is therefore not a one-way
///         function in any useful sense — anybody holding the digest enumerates the whole space
///         faster than they can read it. What makes the stored value useless on its own is the
///         <b>pepper</b>: an HMAC key resolved from the vault at start-up and held only in memory,
///         so a stolen durable-tier backup does not contain enough to recover a single code.
///     </para>
///     <para>
///         ⚠ <b>An empty pepper is legal, is the development default, and buys nothing.</b>
///         <c>AddCyberCloudIdentity</c> takes the pepper as a span and passes whatever it was given;
///         with no vault wired that is empty, and an HMAC under an empty key is a keyed hash with a
///         key everybody knows. The shape is unchanged so that wiring a vault later is a start-up
///         change rather than a storage migration — but a deployment that runs this way has property
///         4 of <see cref="OtpPolicy" /> in name only. That is exactly the trade
///         <c>Argon2idPasswordHasher</c> records for its own pepper, in the same words, and it is
///         written twice on purpose: the two are separate decisions that happen to share a secret.
///     </para>
///     <para>
///         ⚠ <b>Rotating the pepper invalidates every outstanding code</b>, which costs a user one
///         resend and is therefore not the deployment-lifetime constraint the password pepper is.
///         Sharing one value between the two is what makes the password's constraint bind, and that
///         is accepted here rather than solved: a second vault handle for a ten-minute credential is
///         a second thing to configure, to rotate and to get wrong.
///     </para>
/// </remarks>
/// <param name="pepper">The HMAC key, from the vault. Empty in development — see the remarks.</param>
public sealed class OtpCodeProtector(ReadOnlySpan<byte> pepper) {
    readonly byte[] pepper = pepper.ToArray();

    /// <summary>
    ///     A fresh code: <see cref="OtpPolicy.Digits" /> decimal digits, uniformly distributed.
    /// </summary>
    /// <returns>The plaintext. ⚠ The only place it exists — see <see cref="Digest" />.</returns>
    /// <remarks>
    ///     ⚠ <b><see cref="RandomNumberGenerator.GetInt32(int,int)" /> and not
    ///     <c>Random.Shared</c>, and not <c>bytes[0] % 10</c> either.</b> The first is not a
    ///     cryptographic generator and its output is predictable from a handful of samples, which an
    ///     attacker gets by asking for codes on their own account. The second is the modulo bias
    ///     every hand-rolled digit generator has: 256 is not a multiple of 10, so digits 0-5 come up
    ///     more often than 6-9, and six biased digits are meaningfully fewer than a million
    ///     candidates. <c>GetInt32</c> does the rejection sampling.
    ///     <para>
    ///         ⚠ Leading zeros are kept — the code is a <i>string</i> of digits and
    ///         <c>042317</c> is a legal one. Formatting it as a number and losing the zero would
    ///         quietly cut the space by a tenth and produce codes the user cannot type back.
    ///     </para>
    /// </remarks>
    public static string Generate() {
        var upper = 1;
        for (var i = 0; i < OtpPolicy.Digits; i++) {
            upper *= 10;
        }

        return RandomNumberGenerator.GetInt32(0, upper)
            .ToString("D" + OtpPolicy.Digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     The storable form of one code, for one user and one purpose.
    /// </summary>
    /// <param name="tenantId">The tenant the user belongs to.</param>
    /// <param name="userId">The user.</param>
    /// <param name="purpose">Why the code was issued.</param>
    /// <param name="code">The plaintext.</param>
    /// <returns>The base64url HMAC-SHA-256, unpadded.</returns>
    /// <remarks>
    ///     ⚠ <b>The tenant, the user and the purpose are inside the MAC rather than beside it.</b>
    ///     Without them the stored value is a function of six digits alone, so two users who
    ///     happened to be issued <c>424242</c> would hold the same digest — and a digest lifted from
    ///     one account's state would verify against another's. Binding them makes a stolen digest
    ///     useful only against the exact challenge it came from, which is already burnt.
    ///     <para>
    ///         ⚠ Prefix-free: every variable-length part is preceded by its own length, so no two
    ///         different inputs can produce the same material by re-cutting it at a different place.
    ///         The same argument <c>GrainKeys.EmailIndex</c> and
    ///         <c>CommunicationOtpDelivery.IdempotencyKeyFor</c> make.
    ///     </para>
    /// </remarks>
    public string Digest(Guid tenantId, Guid userId, OtpPurpose purpose, string code) {
        var material = new StringBuilder(128)
            .Append("cybercloud.identity.otp.v1\n")
            .Append(tenantId.ToString("N", CultureInfo.InvariantCulture))
            .Append('\n')
            .Append(userId.ToString("N", CultureInfo.InvariantCulture))
            .Append('\n')
            .Append((int)purpose)
            .Append('\n')
            .Append((code ?? string.Empty).Length)
            .Append('\n')
            .Append(code ?? string.Empty)
            .ToString();

        return CredentialDigest.Base64Url(
            HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes(material))
        );
    }
}
