using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.Kubernetes.Contracts.Tunnel;

/// <summary>
///     A freshly minted agent credential: the plaintext that leaves the platform once, and the hash
///     the platform keeps.
/// </summary>
/// <param name="Plaintext">The value the agent presents. Shown once and never stored.</param>
/// <param name="Hash">What the platform stores and compares — <see cref="AgentCredentials.Hash" />.</param>
/// <remarks>
///     ⚠ Deliberately not <c>[GenerateSerializer]</c>. This record exists only inside the process
///     that minted it — the gateway, or the action handler — and never crosses the Orleans wire.
///     What crosses is <see cref="Hash" /> alone.
/// </remarks>
public readonly record struct MintedCredential(string Plaintext, string Hash);

/// <summary>
///     How agent credentials are minted, hashed, and told apart.
/// </summary>
/// <remarks>
///     <para>
///         <b>Two credentials, one shape.</b> The <i>enrollment token</i> is what the install
///         command carries: one-time, short-lived, spent on the first successful connection. The
///         <i>agent credential</i> is what the agent gets in exchange and keeps in a Secret in its
///         own namespace: long-lived, revoked with the cluster. Both are 32 random bytes,
///         base64url, behind a prefix that says which is which — so a support engineer reading a
///         log can tell an install token pasted into the wrong place from a credential that leaked.
///     </para>
///     <para>
///         ⚠ <b>The platform never holds either plaintext, and the grain never sees one.</b> The
///         gateway hashes what the agent presents <i>before</i> calling
///         <c>IAgentTunnelGrain.AcceptAsync</c>; the action handler hashes what it minted before
///         calling <c>ArmAsync</c>. So the Orleans wire, the grain's durable state, and every trace
///         of a grain call carry a SHA-256 and nothing else — which is what keeps CC1005 (no
///         secret-shaped member in grain state) true by construction rather than by naming.
///     </para>
///     <para>
///         ⚠ <b>SHA-256 and not a password hash, on purpose.</b> A password hash is slow to defend a
///         low-entropy secret against guessing. These are 256 random bits: guessing is not the
///         threat, and a slow hash on every agent reconnect would put a deliberate CPU cost on the
///         one path a NAT flap exercises a thousand times.
///     </para>
/// </remarks>
public static class AgentCredentials {
    /// <summary>The prefix an enrollment token carries.</summary>
    public const string EnrollmentPrefix = "cca-enroll-";

    /// <summary>The prefix a long-lived agent credential carries.</summary>
    public const string CredentialPrefix = "cca-agent-";

    /// <summary>How long an enrollment token is valid for after <c>listInstallCommand</c> minted it.</summary>
    public static TimeSpan EnrollmentLifetime { get; } = TimeSpan.FromHours(24);

    /// <summary>Mints an enrollment token.</summary>
    public static MintedCredential MintEnrollment() => Mint(EnrollmentPrefix);

    /// <summary>Mints a long-lived agent credential.</summary>
    public static MintedCredential MintCredential() => Mint(CredentialPrefix);

    /// <summary>Whether a presented value has the shape of one of ours, before it is hashed.</summary>
    /// <param name="presented">The bearer value from the upgrade request.</param>
    /// <remarks>
    ///     A cheap gate in front of the grain call: a JWT, an empty header, or a kubeconfig pasted
    ///     into the wrong field is refused here with no round trip and no log line naming a tenant.
    /// </remarks>
    public static bool IsWellFormed(string? presented) =>
        presented is not null
        && (presented.StartsWith(EnrollmentPrefix, StringComparison.Ordinal)
            || presented.StartsWith(CredentialPrefix, StringComparison.Ordinal))
        && presented.Length is >= 40 and <= 128;

    /// <summary>Whether a presented value is an enrollment token rather than a credential.</summary>
    /// <param name="presented">The bearer value.</param>
    public static bool IsEnrollment(string presented) {
        ArgumentNullException.ThrowIfNull(presented);
        return presented.StartsWith(EnrollmentPrefix, StringComparison.Ordinal);
    }

    /// <summary>The hash the platform stores and compares — lower-case hex SHA-256 of the UTF-8 plaintext.</summary>
    /// <param name="plaintext">The presented or minted value.</param>
    public static string Hash(string plaintext) {
        ArgumentNullException.ThrowIfNull(plaintext);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));
    }

    /// <summary>Compares two hashes in constant time.</summary>
    /// <param name="left">One hash.</param>
    /// <param name="right">The other.</param>
    public static bool HashesMatch(string left, string right) {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    }

    static MintedCredential Mint(string prefix) {
        var plaintext = prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        return new(plaintext, Hash(plaintext));
    }

    static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
