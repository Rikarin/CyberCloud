using CyberCloud.Identity.Validation;

namespace CyberCloud.Gateway.Host.Authentication;

/// <summary>
///     The production identity seam at stage 2: reads the <c>Authorization</c> header's bearer
///     token and hands it to <see cref="IBearerTokenValidator" />. docs/plan/11 § Protocol.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The validation moved out of this class and into <c>CyberCloud.Identity.Validation</c>,
///             and what is left here is deliberately only the part that is the gateway's.
///         </b> Everything <see cref="ICallerContextResolver" />'s remarks asked the identity host
///         for — discovery and JWKS, the pinned issuer and audience, <c>tid</c>, <c>sub_typ</c>,
///         <c>act_sub</c>, the second expiry check — is met by <see cref="JwksBearerTokenValidator" />,
///         which the feeds host now shares (docs/plan/13 § Artifact feeds, issue #29). What the two
///         hosts do <i>not</i> share is where the token is read from: stage 2 reads the
///         <c>Authorization</c> header and nothing else, because reading anything else would put a
///         caller-controlled surface inside authentication, and the feeds host has to read three
///         places because <c>mvn</c> and <c>dotnet nuget push</c> do not send a <c>Bearer</c> header.
///         Keeping the read here and the validation there is what lets each host be strict about
///         its own half.
///     </para>
///     <para>
///         <c>HostCompositionTests.TheGatewayValidatesBearerTokensAgainstTheIdentityHostsKeySet</c> asserts this
///         type by name, because "something resolves" is exactly the assertion the in-process token
///         table would also satisfy.
///     </para>
/// </remarks>
/// <param name="validator">The shared validator — the JWKS one in production.</param>
sealed class JwksCallerContextResolver(IBearerTokenValidator validator) : ICallerContextResolver {
    const string BearerPrefix = "Bearer ";

    /// <inheritdoc />
    public Task<Result<TokenClaims>> ResolveAsync(HttpRequest request, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(request);

        var header = request.Headers.Authorization.ToString();

        if (header.Length == 0) {
            return Task.FromResult(
                Result<TokenClaims>.Failure(BearerTokenErrors.Unauthenticated("no Authorization header"))
            );
        }

        if (!header.StartsWith(BearerPrefix, StringComparison.Ordinal)) {
            return Task.FromResult(
                Result<TokenClaims>.Failure(
                    BearerTokenErrors.Unauthenticated("the Authorization header is not a bearer token")
                )
            );
        }

        return validator.ValidateAsync(header[BearerPrefix.Length..], request.HttpContext, cancellationToken);
    }
}
