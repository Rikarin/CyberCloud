using CyberCloud.Identity.Contracts;

namespace CyberCloud.Identity.Host.Api;

/// <summary>
///     How a <see cref="CredentialKind" /> is spelled on the wire.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A written-out table rather than a naming policy, and the difference is the whole
///         point of the file.</b> <c>JsonNamingPolicy.CamelCase</c> over a
///         <c>JsonStringEnumConverter</c> happens to produce these eight strings today. That is a
///         coincidence of one library's rule for where a word boundary is, applied to member names
///         somebody is free to rename — and the failure it produces is silent.
///         <c>portal/apps/identity/src/app/identity-api.ts</c> compares these values
///         <b>ordinally</b>, so <c>whatsappOtp</c> against <c>whatsAppOtp</c> matches no branch, and
///         the symptom is "the passkey button never appears" rather than an error anybody can search
///         for. docs/plan/11's <c>servicePrincipal</c>-versus-<c>serviceprincipal</c> trap is the
///         same bug one layer down, and this platform has already shipped it once with
///         <c>resourcegroup</c> against <c>resourceGroup</c>.
///     </para>
///     <para>
///         ⚠ <b><see cref="Of(CredentialKind)" /> throws on an unmapped kind rather than falling
///         back.</b> A
///         <c>ToString()</c> fallback would emit <c>"Passkey"</c> for a member somebody added and
///         forgot to list here — a value that serializes, deserializes, and matches nothing. The
///         throw is unreachable while <c>CredentialKindNamesTests</c> holds, which asserts the table
///         covers every member of the enum.
///     </para>
/// </remarks>
public static class CredentialKindNames {
    /// <summary>The spelling the frontend compares against, per kind.</summary>
    static readonly Dictionary<CredentialKind, string> Names = new() {
        [CredentialKind.Passkey] = "passkey",
        [CredentialKind.Password] = "password",
        [CredentialKind.Totp] = "totp",
        [CredentialKind.RecoveryCode] = "recoveryCode",
        [CredentialKind.EmailOtp] = "emailOtp",
        [CredentialKind.SmsOtp] = "smsOtp",
        [CredentialKind.WhatsAppOtp] = "whatsAppOtp",
        [CredentialKind.Certificate] = "certificate"
    };

    /// <summary>
    ///     The wire spelling of <paramref name="kind" />.
    /// </summary>
    /// <param name="kind">The credential kind.</param>
    /// <returns>The exact string the frontend compares ordinally.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="kind" /> is not in the table — see the ⚠ block on the type for why this
    ///     throws rather than guessing.
    /// </exception>
    public static string Of(CredentialKind kind) =>
        Names.TryGetValue(kind, out var name)
            ? name
            : throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                $"{nameof(CredentialKindNames)} has no wire spelling for this kind. Add one — a "
                + "fallback would emit a value the frontend matches no branch on, which reads as a "
                + "missing button rather than as an error."
            );

    /// <summary>Every kind that has a spelling. ⚠ Exposed so a test can assert the table is total.</summary>
    public static IReadOnlyCollection<CredentialKind> Mapped => Names.Keys;

    /// <summary>Maps a list of kinds to their wire spellings, preserving order.</summary>
    /// <param name="kinds">The kinds, in the order they are offered.</param>
    public static string[] Of(IEnumerable<CredentialKind> kinds) {
        ArgumentNullException.ThrowIfNull(kinds);

        return [.. kinds.Select(Of)];
    }
}
