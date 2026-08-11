using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.Identity.Credentials;

/// <summary>
///     Recovery codes — "the thing that prevents 'I lost my phone' tickets". docs/plan/11
///     § Credentials: <i>10 × 10 chars, single-use, hashed. Shown once.</i>
/// </summary>
public static class RecoveryCodes {
    /// <summary>How many codes a batch holds.</summary>
    public const int BatchSize = 10;

    /// <summary>How many characters each code carries.</summary>
    public const int CodeLength = 10;

    /// <summary>
    ///     The alphabet. ⚠ Crockford-style: no <c>I</c>, <c>L</c>, <c>O</c>, <c>U</c>, <c>0</c> or
    ///     <c>1</c>.
    /// </summary>
    /// <remarks>
    ///     These are read off paper by somebody who has already lost their phone and is not having a
    ///     good day. <c>0</c>/<c>O</c> and <c>1</c>/<c>l</c>/<c>I</c> are the pairs that get
    ///     transcribed wrong, and <c>U</c> is dropped because removing it makes an accidental
    ///     obscenity much less likely in a code somebody has to read aloud to support. The cost is
    ///     entropy: 32 characters would be 5 bits each, 26 is about 4.7, so a ten-character code
    ///     carries roughly 47 bits. Against a per-account lockout counter that is far more than
    ///     enough, and the legibility is worth more than the three bits.
    /// </remarks>
    public const string Alphabet = "23456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>
    ///     Mints a batch. The plaintext is returned once; store <see cref="Hash" /> of each.
    /// </summary>
    /// <returns><see cref="BatchSize" /> codes, formatted as two groups of five.</returns>
    public static IReadOnlyList<string> Generate() {
        var codes = new List<string>(BatchSize);

        for (var i = 0; i < BatchSize; i++) {
            var builder = new StringBuilder(CodeLength + 1);

            for (var c = 0; c < CodeLength; c++) {
                if (c == CodeLength / 2) {
                    builder.Append('-');
                }

                // RandomNumberGenerator.GetItems is rejection-sampled, so the distribution is
                // uniform. A `% Alphabet.Length` over a random byte would not be — 256 is not a
                // multiple of 30, so six characters would be very slightly more likely, which is the
                // kind of bias that is invisible and cumulative.
                builder.Append(RandomNumberGenerator.GetItems<char>(Alphabet, 1)[0]);
            }

            codes.Add(builder.ToString());
        }

        return codes;
    }

    /// <summary>
    ///     The stored form of a code: normalized, then SHA-256.
    /// </summary>
    /// <param name="code">The code, as typed or as generated.</param>
    /// <remarks>
    ///     ⚠ A plain hash rather than Argon2id, and the difference from a password is entropy. A
    ///     recovery code is 47 bits of uniform randomness, so an offline attacker with the hash faces
    ///     2^47 work whatever the KDF; a password is maybe 20 bits of guessable structure, which is
    ///     what a memory-hard KDF is for. Spending 64 MB per code check would slow down the recovery
    ///     path for no security gain.
    /// </remarks>
    public static string Hash(string code) => CredentialDigest.Sha256(Normalize(code));

    /// <summary>
    ///     Strips formatting so that what the user types matches what was generated.
    /// </summary>
    /// <param name="code">Whatever arrived.</param>
    /// <remarks>
    ///     ⚠ Case-folding is ASCII-only, for the reason <c>GrainKeys.NormalizeEmail</c> spells out:
    ///     <see cref="string.ToUpperInvariant" /> maps some non-ASCII characters onto ASCII ones, and
    ///     a normalizer that merges two distinct inputs into one code is a normalizer that lets one
    ///     code redeem another. The alphabet is ASCII, so anything outside it cannot be a code
    ///     anyway.
    /// </remarks>
    public static string Normalize(string? code) {
        if (string.IsNullOrEmpty(code)) {
            return string.Empty;
        }

        var builder = new StringBuilder(code.Length);

        foreach (var c in code) {
            if (c is ' ' or '-' or '_') {
                continue;
            }

            builder.Append(c is >= 'a' and <= 'z' ? (char)(c - 32) : c);
        }

        return builder.ToString();
    }
}
