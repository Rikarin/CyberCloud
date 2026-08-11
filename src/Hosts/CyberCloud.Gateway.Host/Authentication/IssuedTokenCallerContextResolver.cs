using CyberCloud.Core.Time;
using System.Collections.Concurrent;
using System.Globalization;

namespace CyberCloud.Gateway.Host.Authentication;

/// <summary>
///     A resolver over an in-process table of tokens this process itself issued.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the test implementation of the identity seam, and it is deliberately not a
///         JWT library with the signature check removed.</b> Two properties matter and both come from
///         the shape rather than from care:
///     </para>
///     <list type="number">
///         <item>
///             <b>A token this table did not issue resolves to nothing.</b> There is no parsing step,
///             so there is no "parse the claims, then verify" ordering to get wrong — a forged
///             <c>tid</c> is not a claim that fails validation, it is a string that is not a key in a
///             dictionary. <c>TenantFromTokenTests</c> forges one and gets <c>401</c>.
///         </item>
///         <item>
///             <b>It cannot accidentally reach production.</b> It holds no keys, trusts no issuer and
///             validates no signature, so a host that wired it by mistake authenticates nobody at
///             all — a loud failure rather than a quiet one.
///         </item>
///     </list>
///     <para>
///         The production implementation belongs with the identity host;
///         <see cref="ICallerContextResolver" />'s remarks list exactly what it needs from it.
///     </para>
/// </remarks>
sealed class IssuedTokenCallerContextResolver(IClock clock) : ICallerContextResolver {
    const string BearerPrefix = "Bearer ";

    readonly ConcurrentDictionary<string, TokenClaims> issued = new(StringComparer.Ordinal);

    /// <summary>Issues a token and returns its bearer value.</summary>
    /// <param name="claims">What the token asserts.</param>
    /// <returns>The opaque value a caller puts in <c>Authorization: Bearer …</c>.</returns>
    public string Issue(TokenClaims claims) {
        var token = "cc_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        issued[token] = claims;
        return token;
    }

    /// <inheritdoc />
    public Task<Result<TokenClaims>> ResolveAsync(
        HttpRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var header = request.Headers.Authorization.ToString();

        if (header.Length == 0) {
            return Task.FromResult(Result<TokenClaims>.Failure(Http.GatewayErrors.Unauthenticated(
                "no Authorization header"
            )));
        }

        if (!header.StartsWith(BearerPrefix, StringComparison.Ordinal)) {
            return Task.FromResult(Result<TokenClaims>.Failure(Http.GatewayErrors.Unauthenticated(
                "the Authorization header is not a bearer token"
            )));
        }

        // ⚠ The lookup IS the validation. A value this process did not issue has no claims to read,
        // so there is no path on which a caller-supplied `tid` is believed.
        if (!issued.TryGetValue(header[BearerPrefix.Length..], out var claims)) {
            return Task.FromResult(Result<TokenClaims>.Failure(Http.GatewayErrors.Unauthenticated(
                "the bearer token was not issued by this platform"
            )));
        }

        if (claims.ExpiresAt <= clock.UtcNow) {
            return Task.FromResult(Result<TokenClaims>.Failure(Http.GatewayErrors.Unauthenticated(
                "the bearer token has expired"
            )));
        }

        return Task.FromResult(Result<TokenClaims>.Success(claims));
    }
}
