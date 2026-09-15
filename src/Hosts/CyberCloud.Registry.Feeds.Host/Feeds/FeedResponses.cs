using CyberCloud.Registry.Feeds.Host.Authentication;
using System.Security.Cryptography;

namespace CyberCloud.Registry.Feeds.Host.Feeds;

/// <summary>
///     How a platform <see cref="Error" /> becomes a package client's HTTP answer, and the two
///     helpers every protocol shares for bytes.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The status comes from the error code and one rule on top.</b> Every
///         <see cref="ErrorCode" /> carries its HTTP status, and the one thing a package client needs
///         that the platform's mapping does not give is the difference between "you are nobody" and
///         "you are somebody who may not": <see cref="ErrorCode.AuthorizationFailed" /> is a
///         <c>403</c> at the resource manager, and here it is a <c>401</c> with a challenge when the
///         request carried no credential that validated, because the one thing an anonymous
///         <c>dotnet nuget push</c> can act on is that it needs one.
///     </para>
///     <para>
///         The body is the platform's error shape — <c>code</c> and <c>message</c> — and never a
///         stack trace, which is the rule the gateway's <c>ErrorBody</c> keeps and the reason its
///         shape is copied rather than referenced: the gateway host is an executable and this one
///         does not reference it.
///     </para>
/// </remarks>
public static class FeedResponses {
    /// <summary>
    ///     The <see cref="HttpContext.Items" /> key <c>FeedAccess</c> sets once a credential has
    ///     validated, so a later refusal can tell <c>403</c> from <c>401</c>.
    /// </summary>
    public const string AuthenticatedItem = "cybercloud.feeds.authenticated";

    /// <summary>The HTTP answer for a refusal.</summary>
    /// <param name="error">The refusal.</param>
    /// <param name="http">
    ///     The request, for whether its credential validated — <see cref="AuthenticatedItem" />,
    ///     which decides <c>401</c> against <c>403</c> for <see cref="ErrorCode.AuthorizationFailed" />.
    /// </param>
    public static IResult Refuse(Error error, HttpContext http) {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(http);

        if (error.Code == ErrorCode.AuthorizationFailed && !http.Items.ContainsKey(AuthenticatedItem)) {
            return new ChallengeResult(error);
        }

        return Results.Json(new { code = error.Code.Value, message = error.Message }, statusCode: error.Code.HttpStatus);
    }

    /// <summary>A <c>401</c> carrying both challenges — see <see cref="FeedCredentialResolver.Challenge" />.</summary>
    sealed class ChallengeResult(Error error) : IResult {
        public async Task ExecuteAsync(HttpContext httpContext) {
            httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            httpContext.Response.Headers.WWWAuthenticate = FeedCredentialResolver.Challenge;
            await httpContext.Response.WriteAsJsonAsync(new { code = error.Code.Value, message = error.Message });
        }
    }

    /// <summary>
    ///     Reads a request body whole, refusing one that is larger than the host allows.
    /// </summary>
    /// <param name="http">The request.</param>
    /// <param name="maxBytes">The cap — <see cref="FeedsOptions.MaxArtifactBytes" />.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes, or <see cref="ErrorCode.InvalidRequestBody" /> naming the cap.</returns>
    /// <remarks>
    ///     ⚠ Bounded by reading one byte past the cap rather than by trusting <c>Content-Length</c>,
    ///     which a chunked upload does not carry. The declared length is checked first so an honest
    ///     client is refused before it sends anything.
    /// </remarks>
    public static async Task<Result<byte[]>> ReadBodyAsync(HttpContext http, long maxBytes, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(http);

        if (http.Request.ContentLength is { } declared && declared > maxBytes) {
            return Result<byte[]>.Failure(ErrorCode.InvalidRequestBody, TooLarge(maxBytes));
        }

        return await ReadBoundedAsync(http.Request.Body, maxBytes, cancellationToken);
    }

    /// <summary>Reads a stream whole, refusing one that is larger than the cap.</summary>
    /// <param name="stream">The bytes.</param>
    /// <param name="maxBytes">The cap.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<Result<byte[]>> ReadBoundedAsync(Stream stream, long maxBytes, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;

        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0) {
            if (buffer.Length + read > maxBytes) {
                return Result<byte[]>.Failure(ErrorCode.InvalidRequestBody, TooLarge(maxBytes));
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return Result<byte[]>.Success(buffer.ToArray());
    }

    /// <summary>The bytes' SHA-256, lower-case hex — what a <see cref="FeedEntry.Sha256" /> carries.</summary>
    /// <param name="bytes">The artefact.</param>
    public static string Sha256Of(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    static string TooLarge(long maxBytes) =>
        $"The artefact is larger than this host accepts ({maxBytes} bytes). {FeedsOptions.SectionName}:MaxArtifactBytes is the cap.";
}
