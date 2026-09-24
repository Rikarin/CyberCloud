using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.Providers.Mail.Contracts;

/// <summary>
///     The <c>$6$</c> password hash — Ulrich Drepper's SHA-crypt over SHA-512, the scheme Dovecot
///     calls <c>SHA512-CRYPT</c> and glibc's <c>crypt(3)</c> implements.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>HAND-WRITTEN, FOR THE REASON <c>SmtpConnection</c> IS.</b> docs/plan/02 § Dependency
///         register admits nothing without an ADR, and .NET ships no crypt(3). The algorithm is the
///         published specification ("Unix crypt using SHA-256 and SHA-512", 2008) step for step, and
///         it is held to two independent implementations: <c>MailPasswordTests</c> pins the
///         specification's own vectors, which OpenSSL 3.5's <c>openssl passwd -6</c> reproduces
///         byte for byte, and the cluster-backed suite logs in to a real Dovecot with a hash this
///         produced.
///     </para>
///     <para>
///         ⚠ <b>SHA512-CRYPT AND NOT ARGON2 OR BCRYPT, AND THE REASON IS WHAT BOTH DOVECOT MAJORS
///         ACCEPT.</b> <c>MailDomains.Versions</c> offers 2.3 and 2.4; <c>BLF-CRYPT</c> depends on
///         the libc the image was built against and <c>ARGON2ID</c> on libsodium being linked in.
///         <c>SHA512-CRYPT</c> is the one strong scheme both images verify unconditionally.
///     </para>
/// </remarks>
public static class Sha512Crypt {
    /// <summary>The rounds the specification uses when none are named.</summary>
    public const int DefaultRounds = 5000;

    /// <summary>The rounds a mailbox's hash is made with.</summary>
    /// <remarks>
    ///     ⚠ Twenty times the default, which costs a reconcile pass roughly 30 ms per mailbox and an
    ///     attacker holding the hash twenty times more per guess. The ceiling is Dovecot's own login
    ///     latency, which pays the same rounds on every authentication.
    /// </remarks>
    public const int MailboxRounds = 100_000;

    const int MinRounds = 1000;
    const int MaxRounds = 999_999_999;
    const int MaxSaltLength = 16;

    const string Alphabet = "./0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>The byte triples of the final digest, in the order the specification encodes them.</summary>
    static readonly (int A, int B, int C)[] Order = [
        (0, 21, 42), (22, 43, 1), (44, 2, 23), (3, 24, 45), (25, 46, 4), (47, 5, 26), (6, 27, 48),
        (28, 49, 7), (50, 8, 29), (9, 30, 51), (31, 52, 10), (53, 11, 32), (12, 33, 54), (34, 55, 13),
        (56, 14, 35), (15, 36, 57), (37, 58, 16), (59, 17, 38), (18, 39, 60), (40, 61, 19), (62, 20, 41)
    ];

    /// <summary>Hashes a password under a salt, as <c>crypt(3)</c> would given <c>$6$[rounds=N$]salt</c>.</summary>
    /// <param name="password">The password, as UTF-8.</param>
    /// <param name="salt">
    ///     Up to sixteen characters from the crypt alphabet; longer is truncated, as the specification
    ///     says.
    /// </param>
    /// <param name="rounds">
    ///     The rounds, clamped to 1000–999,999,999. <see langword="null" /> means the default and
    ///     omits the <c>rounds=</c> field, which is how a hash from <c>$6$salt</c> is spelled.
    /// </param>
    /// <returns>The complete <c>$6$…</c> string.</returns>
    public static string Hash(string password, string salt, int? rounds = null) {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(salt);

        var count = rounds is { } explicitRounds ? Math.Clamp(explicitRounds, MinRounds, MaxRounds) : DefaultRounds;
        var saltText = salt.Length > MaxSaltLength ? salt[..MaxSaltLength] : salt;
        var p = Encoding.UTF8.GetBytes(password);
        var s = Encoding.UTF8.GetBytes(saltText);

        // Steps 4–8: digest B over password, salt, password.
        var b = SHA512.HashData([.. p, .. s, .. p]);

        // Steps 1–3 and 9–12: digest A.
        using var a = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        a.AppendData(p);
        a.AppendData(s);
        AppendRepeated(a, b, p.Length);

        for (var length = p.Length; length > 0; length >>= 1) {
            a.AppendData((length & 1) != 0 ? b : p);
        }

        var result = a.GetHashAndReset();

        // Steps 13–16: the P sequence.
        using (var dp = IncrementalHash.CreateHash(HashAlgorithmName.SHA512)) {
            for (var i = 0; i < p.Length; i++) {
                dp.AppendData(p);
            }

            p = Sequence(dp.GetHashAndReset(), p.Length);
        }

        // Steps 17–20: the S sequence.
        using (var ds = IncrementalHash.CreateHash(HashAlgorithmName.SHA512)) {
            for (var i = 0; i < 16 + result[0]; i++) {
                ds.AppendData(s);
            }

            s = Sequence(ds.GetHashAndReset(), s.Length);
        }

        // Step 21: the rounds.
        using var c = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);

        for (var i = 0; i < count; i++) {
            c.AppendData((i & 1) != 0 ? p : result);

            if (i % 3 != 0) {
                c.AppendData(s);
            }

            if (i % 7 != 0) {
                c.AppendData(p);
            }

            c.AppendData((i & 1) != 0 ? result : p);
            result = c.GetHashAndReset();
        }

        // Step 22: the encoding.
        var output = new StringBuilder("$6$");

        if (rounds is not null) {
            output.Append(CultureInfo.InvariantCulture, $"rounds={count}$");
        }

        output.Append(saltText).Append('$');

        foreach (var (x, y, z) in Order) {
            Encode(output, result[x], result[y], result[z], 4);
        }

        Encode(output, 0, 0, result[63], 2);

        return output.ToString();
    }

    /// <summary>
    ///     A salt that is the same for one mailbox on every pass and different for every mailbox.
    /// </summary>
    /// <param name="resourceId">The mailbox resource's GUID.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>DERIVED, NOT RANDOM, AND A RANDOM SALT WOULD BREAK THE RECONCILE LOOP.</b> The hash
    ///         is rendered on every pass (docs/plan/08 § The reconcile loop, clause 1): a fresh salt
    ///         per pass would be a different hash per pass, so the co-owned fragment would change on
    ///         every reconcile, report <c>Updated</c> forever, and make the Postfix reload loop fire
    ///         for nothing. The mint-once rule that <c>MailDomains.GenerateCredentials</c> leans on is
    ///         not available here — the password is the tenant's and changes whenever they change it.
    ///     </para>
    ///     <para>
    ///         What a salt is for survives: two mailboxes with the same password hash differently,
    ///         because their GUIDs differ, and no precomputed table covers a salt nobody could predict
    ///         before the mailbox existed. What is given up is only that one mailbox's hash is the same
    ///         across a password it changed away from and back to.
    ///     </para>
    /// </remarks>
    public static string SaltFor(Guid resourceId) {
        var digest = SHA256.HashData([.. "cybercloud.mail.salt/"u8, .. resourceId.ToByteArray()]);
        var salt = new StringBuilder(MaxSaltLength);

        for (var i = 0; i < MaxSaltLength; i++) {
            salt.Append(Alphabet[digest[i] & 0x3f]);
        }

        return salt.ToString();
    }

    static void AppendRepeated(IncrementalHash hash, byte[] block, int length) {
        var remaining = length;

        for (; remaining > 64; remaining -= 64) {
            hash.AppendData(block);
        }

        hash.AppendData(block, 0, remaining);
    }

    static byte[] Sequence(byte[] digest, int length) {
        var sequence = new byte[length];

        for (var i = 0; i < length; i++) {
            sequence[i] = digest[i % 64];
        }

        return sequence;
    }

    static void Encode(StringBuilder output, byte b2, byte b1, byte b0, int characters) {
        var word = (b2 << 16) | (b1 << 8) | b0;

        for (var i = 0; i < characters; i++) {
            output.Append(Alphabet[word & 0x3f]);
            word >>= 6;
        }
    }
}
