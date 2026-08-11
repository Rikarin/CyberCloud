using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.ResourceManager;

/// <summary>
///     The hash of a desired body — the projection's <c>desired_hash</c> and the command builder's
///     <c>cybercloud.io/reconcile-hash</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>One function, because the two uses have to agree.</b>
///     docs/plan/09 § The command builder stamps <c>cybercloud.io/reconcile-hash: sha256:…</c> on every
///     rendered object <i>"of the desired body — cheap no-op detection"</i>, and
///     docs/plan/08 § The resource-graph projection carries <c>desired_hash</c> as a column. The drift
///     scan compares them. Two implementations that formatted the digest differently would report
///     every object as diverged, forever, and the diff would look like a real finding.
/// </remarks>
static class DesiredHash {
    /// <summary>The <c>sha256:{hex}</c> form docs/plan/09 § The command builder shows.</summary>
    /// <param name="desired">The desired body, as JSON text.</param>
    /// <remarks>
    ///     ⚠ Over the text as stored, not over a re-serialization. The resource grain stores the
    ///     superset as <c>JsonNode.ToJsonString()</c> writes it, and that is stable across reads —
    ///     re-serializing here with different options would make the hash depend on which code path
    ///     computed it.
    /// </remarks>
    public static string Of(string desired) {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(desired), hash);
        return "sha256:" + Convert.ToHexStringLower(hash);
    }
}
