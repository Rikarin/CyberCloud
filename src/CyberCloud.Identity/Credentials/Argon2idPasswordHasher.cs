using Konscious.Security.Cryptography;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.Identity.Credentials;

/// <summary>
///     Argon2id's cost parameters. docs/plan/11 § Credentials fixes the defaults.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>These are options rather than constants because the sign-in path's constant-time
///         guarantee is written against whatever they are.</b> The timing test has to run a real
///         verification on both the exists and does-not-exist branches, and at the production
///         parameters that is tens of milliseconds each; a suite that does it a hundred times would
///         take a minute for one assertion. Lowering them for a test lowers <i>both</i> branches
///         identically, so the property under test — that the two take the same time — is unchanged.
///     </para>
///     <para>
///         ⚠ <b>Changing a parameter in production does not invalidate stored hashes.</b> The encoded
///         form carries the parameters it was produced with, so verification uses the stored ones and
///         a raised cost applies to the next password change. What it does mean is that
///         <see cref="Argon2idPasswordHasher.NeedsRehash" /> starts returning <see langword="true" />, and a sign-in path
///         that ignores it will keep every old hash at the old cost forever.
///     </para>
/// </remarks>
public sealed record Argon2idOptions {
    /// <summary>The production parameters — <c>m=64MB, t=3, p=4</c>, docs/plan/11 § Credentials.</summary>
    public static Argon2idOptions Default { get; } = new();

    /// <summary>Memory in kibibytes. 65 536 is the 64 MB docs/plan/11 asks for.</summary>
    public int MemoryKibibytes { get; init; } = 65_536;

    /// <summary>Passes over memory. Three.</summary>
    public int Iterations { get; init; } = 3;

    /// <summary>Lanes. Four.</summary>
    public int Parallelism { get; init; } = 4;

    /// <summary>Salt length in bytes. Sixteen, RFC 9106 § 4's recommendation.</summary>
    public int SaltBytes { get; init; } = 16;

    /// <summary>Output length in bytes. Thirty-two.</summary>
    public int HashBytes { get; init; } = 32;
}

/// <summary>
///     Hashes and verifies a password. docs/plan/11 § Credentials.
/// </summary>
/// <remarks>
///     ⚠ The interface exists so the sign-in path can be handed a <i>cheap</i> hasher in a timing
///     test without the test also replacing the code under test. It is not an abstraction over
///     "which algorithm" — there is one, and swapping Argon2id for something else is a decision with
///     an ADR attached, not a registration change.
/// </remarks>
public interface IPasswordHasher {
    /// <summary>Hashes a password into its self-describing encoded form.</summary>
    /// <param name="candidate">The plaintext.</param>
    /// <returns>The PHC-format string to store.</returns>
    string Hash(string candidate);

    /// <summary>
    ///     Whether <paramref name="candidate" /> produces <paramref name="encoded" />.
    /// </summary>
    /// <param name="candidate">The plaintext typed.</param>
    /// <param name="encoded">The stored PHC-format string.</param>
    /// <returns><c>true</c> when the password is right.</returns>
    bool Verify(string candidate, string encoded);

    /// <summary>
    ///     A hash of the configured shape over a value nobody knows, for the branch where there is
    ///     no user to verify against.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the whole enumeration defence and it is not decoration.</b> Sign-in for an
    ///     address with no account must cost the same as sign-in for one with an account and a wrong
    ///     password — docs/plan/11 § Credentials. The only way to make that true is to actually do
    ///     the work, so the no-such-user branch verifies against this instead of returning early.
    /// </remarks>
    string DummyHash { get; }
}

/// <summary>
///     Argon2id over <c>Konscious.Security.Cryptography.Argon2</c>. docs/plan/11 § Credentials.
/// </summary>
/// <remarks>
///     <para>
///         <b>The stored form is PHC</b> — <c>$argon2id$v=19$m=65536,t=3,p=4$salt$hash</c>, the
///         format the reference implementation defines and every other Argon2 library reads. Storing
///         a bare hash and keeping the parameters in configuration is the mistake that makes raising
///         the cost a migration; storing them alongside makes it a no-op.
///     </para>
///     <para>
///         ⚠ <b>The pepper is <c>KnownSecret</c>, which is a real KDF input rather than a
///         hash-the-hash wrapper.</b> docs/plan/11 § Credentials asks for a "pepper from Vault", and
///         RFC 9106 has a secret input for exactly this. Its value: an attacker with the database
///         but not the vault cannot even begin a dictionary attack, because they cannot compute a
///         single candidate hash. It is <b>not</b> stored in the encoded form — a pepper written next
///         to the hash is not a pepper — so rotating it invalidates every stored password, which is
///         why it is a deployment-lifetime value and not a rotating one.
///     </para>
///     <para>
///         ⚠ <b>The pepper is a byte array held in memory and never a member of grain state.</b>
///         docs/plan/00 § Non-negotiables' rule is about <c>[Id]</c>-annotated members; this is a
///         constructor argument resolved from the vault at start-up, which is the "resolved at the
///         data plane" half of the same sentence.
///     </para>
/// </remarks>
public sealed class Argon2idPasswordHasher : IPasswordHasher {
    const string Prefix = "$argon2id$v=19$";

    readonly Argon2idOptions options;
    readonly byte[] pepper;

    /// <inheritdoc />
    /// <remarks>
    ///     Computed once at construction, so the no-such-user branch pays only the verification and
    ///     not a second hashing. Verifying against it costs exactly what verifying a real hash costs,
    ///     because it <i>is</i> a real hash of the configured shape.
    /// </remarks>
    public string DummyHash { get; }

    /// <summary>Creates a hasher.</summary>
    /// <param name="options">The cost parameters. <see cref="Argon2idOptions.Default" /> in production.</param>
    /// <param name="pepper">
    ///     The secret KDF input, from the vault. Pass an empty span when no vault is wired — the
    ///     hasher still works, it just loses the property that a stolen database is useless on its
    ///     own.
    /// </param>
    public Argon2idPasswordHasher(Argon2idOptions? options = null, ReadOnlySpan<byte> pepper = default) {
        this.options = options ?? Argon2idOptions.Default;
        this.pepper = pepper.ToArray();

        // ⚠ Hashed from a random value rather than from a constant. A constant dummy would hash to
        // the same string in every process, so anyone who obtained it once could recognise the
        // no-such-user branch by the value being compared — which would reintroduce the enumeration
        // this exists to close.
        DummyHash = Hash(CredentialDigest.RandomHandle(32));
    }

    /// <inheritdoc />
    public string Hash(string candidate) {
        ArgumentNullException.ThrowIfNull(candidate);

        var salt = RandomNumberGenerator.GetBytes(options.SaltBytes);
        var hash = Derive(candidate, salt, options);

        return Prefix
            + string.Create(
                CultureInfo.InvariantCulture,
                $"m={options.MemoryKibibytes},t={options.Iterations},p={options.Parallelism}"
            )
            + "$"
            + CredentialDigest.Base64Url(salt)
            + "$"
            + CredentialDigest.Base64Url(hash);
    }

    /// <inheritdoc />
    public bool Verify(string candidate, string encoded) {
        if (candidate is null || string.IsNullOrEmpty(encoded) || !encoded.StartsWith(Prefix, StringComparison.Ordinal)) {
            return false;
        }

        var parts = encoded[Prefix.Length..].Split('$');
        if (parts.Length != 3 || !TryParseParameters(parts[0], out var stored)) {
            return false;
        }

        byte[] salt;
        try {
            salt = FromBase64Url(parts[1]);
        } catch (FormatException) {
            // A stored hash that is not base64 is corruption, not a wrong password. It still answers
            // "no" — there is no candidate that could verify against it — and the corruption shows up
            // as a user who cannot sign in, which is the failure that gets reported.
            return false;
        }

        var actual = Derive(candidate, salt, stored);
        return CredentialDigest.FixedTimeEquals(CredentialDigest.Base64Url(actual), parts[2]);
    }

    /// <summary>
    ///     Whether <paramref name="encoded" /> was produced with weaker parameters than the current
    ///     ones, so the caller should re-hash while it holds the plaintext.
    /// </summary>
    /// <param name="encoded">The stored form.</param>
    /// <returns><c>true</c> when any stored parameter is below the configured one.</returns>
    /// <remarks>
    ///     ⚠ The only moment a re-hash is possible is immediately after a <i>successful</i>
    ///     verification, because that is the only moment the plaintext exists. A cost increase that
    ///     is not acted on there never applies to anybody who does not change their password.
    /// </remarks>
    public bool NeedsRehash(string encoded) {
        if (string.IsNullOrEmpty(encoded) || !encoded.StartsWith(Prefix, StringComparison.Ordinal)) {
            return true;
        }

        var parts = encoded[Prefix.Length..].Split('$');
        if (parts.Length != 3 || !TryParseParameters(parts[0], out var stored)) {
            return true;
        }

        return stored.MemoryKibibytes < options.MemoryKibibytes
            || stored.Iterations < options.Iterations
            || stored.Parallelism < options.Parallelism;
    }

    /// <summary>
    ///     The bytes actually fed to the KDF: a version byte, then the UTF-8 password.
    /// </summary>
    /// <param name="candidate">The plaintext.</param>
    /// <remarks>
    ///     ⚠ <b>The prefix exists because <c>Konscious</c> throws on an empty password, and that
    ///     throw lands on the sign-in path.</b> <c>new Argon2id([])</c> is an
    ///     <see cref="ArgumentException" /> — "Argon2 needs a password set" — so a caller who posts
    ///     an empty password would get a <c>500</c> and a paged engineer instead of the uniform
    ///     failure docs/plan/11 § Credentials requires. Returning early for an empty candidate would
    ///     fix the crash and reintroduce a timing difference; a one-byte prefix makes the array never
    ///     empty and keeps every branch doing identical work.
    ///     <para>
    ///         It doubles as domain separation and as a version marker: if the pre-processing ever
    ///         has to change (a normalization step, a length cap), the byte says which rule produced
    ///         a stored hash. It is <b>not</b> a secret and adds no security on its own — the pepper
    ///         is <c>KnownSecret</c>, which is a different input.
    ///     </para>
    /// </remarks>
    static byte[] Encode(string candidate) {
        var text = Encoding.UTF8.GetBytes(candidate);
        var buffer = new byte[text.Length + 1];

        buffer[0] = 1;
        text.CopyTo(buffer, 1);

        return buffer;
    }

    byte[] Derive(string candidate, byte[] salt, Argon2idOptions with) {
        using var argon = new Argon2id(Encode(candidate)) {
            Salt = salt,
            MemorySize = with.MemoryKibibytes,
            Iterations = with.Iterations,
            DegreeOfParallelism = with.Parallelism,
            KnownSecret = pepper.Length == 0 ? null : pepper
        };

        // ⚠ GetBytes, not GetBytesAsync. This runs inside a grain call, and Konscious' async form
        // fans the lanes out onto the thread pool — which from inside an Orleans activation means
        // work leaving the grain's scheduler and coming back, for a computation that is CPU-bound
        // either way. The blocking form is the honest one here; CC1001 does not fire because this is
        // a synchronous method rather than a `.Result` inside an async one.
        return argon.GetBytes(with.HashBytes);
    }

    static bool TryParseParameters(string text, out Argon2idOptions parsed) {
        parsed = Argon2idOptions.Default;

        int memory = 0, iterations = 0, parallelism = 0;

        foreach (var pair in text.Split(',')) {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0
                || !int.TryParse(pair[(equals + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var value)) {
                return false;
            }

            switch (pair[..equals]) {
                case "m":
                    memory = value;
                    break;
                case "t":
                    iterations = value;
                    break;
                case "p":
                    parallelism = value;
                    break;
                default:
                    return false;
            }
        }

        if (memory <= 0 || iterations <= 0 || parallelism <= 0) {
            return false;
        }

        parsed = new() { MemoryKibibytes = memory, Iterations = iterations, Parallelism = parallelism };
        return true;
    }

    static byte[] FromBase64Url(string value) {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }
}
