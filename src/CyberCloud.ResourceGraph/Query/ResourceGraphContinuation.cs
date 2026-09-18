using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.ResourceGraph.Query;

/// <summary>
///     The resource graph's <c>$skipToken</c>: an offset into one query's result, tied to that
///     query's text.
/// </summary>
/// <remarks>
///     <para>
///         <c>{offset}.{fingerprint}</c>, where the fingerprint is the first sixteen hex digits of a
///         SHA-256 over the query text. The offset is what the next page's <c>OFFSET</c> is; the
///         fingerprint is what makes a token handed out for one query a <c>400</c> when it arrives
///         with another, rather than a slice at a meaningless position of a different result. Sixteen
///         digits because this is a mismatch detector and not a credential: a caller who forges a
///         fingerprint gets their own query's rows from an offset they chose, which they could have
///         asked for anyway.
///     </para>
///     <para>
///         ⚠ <b>Not a snapshot.</b> The projection moves between two pages, and an offset into a
///         moving result can repeat or skip a row that changed its position in the order. A query
///         with <c>order by resourceId</c> pages exactly; the translator's default order over every
///         output column makes the common case stable and is documented as best effort on
///         <c>ResourceGraphQueryRequest.Continuation</c>.
///     </para>
/// </remarks>
public static class ResourceGraphContinuation {
    /// <summary>The token for the next page.</summary>
    /// <param name="query">The query text the token belongs to.</param>
    /// <param name="offset">How many rows the pages so far have covered.</param>
    public static string Encode(string query, long offset) {
        ArgumentNullException.ThrowIfNull(query);
        return string.Create(CultureInfo.InvariantCulture, $"{offset}.{Fingerprint(query)}");
    }

    /// <summary>
    ///     Reads a token back. Empty is the first page.
    /// </summary>
    /// <param name="query">The query text the token is presented with.</param>
    /// <param name="token">The token, or empty.</param>
    /// <returns>The offset, or an <see cref="ErrorCode.InvalidRequestBody" /> failure naming what is wrong with the token.</returns>
    public static Result<long> Decode(string query, string token) {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(token);

        if (token.Length == 0) {
            return Result<long>.Success(0);
        }

        var dot = token.IndexOf('.', StringComparison.Ordinal);

        if (dot <= 0
            || !long.TryParse(token.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var offset)) {
            return Result<long>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{token}' is not a resource graph $skipToken. Pass the token a previous page of this query handed out, "
                + "or leave it out for the first page."
            );
        }

        if (!token.AsSpan(dot + 1).SequenceEqual(Fingerprint(query))) {
            return Result<long>.Failure(
                ErrorCode.InvalidRequestBody,
                "The $skipToken belongs to a different query. A continuation resumes the query that handed it out; "
                + "changing the query text starts a new result, so leave the token out."
            );
        }

        return Result<long>.Success(offset);
    }

    static string Fingerprint(string query) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(query)))[..16];
}
