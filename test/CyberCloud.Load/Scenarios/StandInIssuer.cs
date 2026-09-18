using CyberCloud.Identity.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;

namespace CyberCloud.Load.Scenarios;

/// <summary>
///     An issuer the gateway's JWKS validation accepts: a discovery document, a key set and ES256
///     access tokens with the claims <c>AccessTokenClaims</c> names, served from Kestrel on a free
///     port.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Not the identity host, and the difference is stated so nobody mistakes the one for
///         the other.</b> <c>CyberCloud.Identity.Host</c> mints tokens after a sign-in, a device
///         flow or a client-credentials exchange, and its tests prove those. What the gateway's stage
///         2 needs from an issuer is a document, a key and a signature it can check, and that is all
///         this provides — so that the load numbers include the real
///         <c>JwksCallerContextResolver</c> and OpenIddict's validation on every request, rather than
///         a resolver that looks a token up in a dictionary. The token shape is
///         <c>AccessTokenContract</c>'s: <c>typ</c> <c>at+jwt</c>, <c>aud</c> <c>cyc.api</c>,
///         <c>tid</c> in <c>N</c> form, <c>sub_typ</c>, signed with
///         <c>AccessTokenPolicy.SigningAlgorithm</c>.
///     </para>
/// </remarks>
public sealed class StandInIssuer : IAsyncDisposable {
    readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    readonly string keyId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
    readonly JsonWebTokenHandler handler = new();
    static readonly string[] ResponseTypes = ["token"];
    static readonly string[] SubjectTypesSupported = ["public"];
    static readonly string[] SigningAlgorithms = [AccessTokenPolicy.SigningAlgorithm];
    WebApplication app = null!;

    /// <summary>The issuer's origin, which is what the gateway is configured with.</summary>
    public string Issuer { get; private set; } = string.Empty;

    /// <summary>Starts Kestrel and publishes the discovery document and the key set.</summary>
    public async Task StartAsync(CancellationToken cancellationToken) {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        app = builder.Build();
        app.MapGet(AccessTokenPolicy.DiscoveryPath, () => Results.Json(new {
                issuer = Issuer,
                jwks_uri = Issuer + AccessTokenPolicy.JsonWebKeySetPath,
                token_endpoint = Issuer + "/token",
                response_types_supported = ResponseTypes,
                subject_types_supported = SubjectTypesSupported,
                id_token_signing_alg_values_supported = SigningAlgorithms
            })
        );

        app.MapGet(AccessTokenPolicy.JsonWebKeySetPath, () => {
                var parameters = key.ExportParameters(false);

                return Results.Json(new {
                        keys = new[] {
                            new {
                                kty = "EC",
                                crv = "P-256",
                                use = "sig",
                                alg = AccessTokenPolicy.SigningAlgorithm,
                                kid = keyId,
                                x = Base64UrlEncoder.Encode(parameters.Q.X),
                                y = Base64UrlEncoder.Encode(parameters.Q.Y)
                            }
                        }
                    }
                );
            }
        );

        await app.StartAsync(cancellationToken);

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Issuer = address.TrimEnd('/');
    }

    /// <summary>Mints an access token for a subject in a tenant, valid for an hour.</summary>
    /// <param name="tenant">The tenant the caller acts in.</param>
    /// <param name="subjectType">A <c>SubjectTypes</c> value.</param>
    /// <param name="subjectId">The subject's id.</param>
    public string Mint(Guid tenant, string subjectType, string subjectId) {
        var now = DateTimeOffset.UtcNow;

        var descriptor = new SecurityTokenDescriptor {
            Issuer = Issuer,
            Audience = AccessTokenPolicy.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.AddHours(1).UtcDateTime,
            TokenType = "at+jwt",
            Subject = new ClaimsIdentity(
                [
                    new Claim(AccessTokenClaims.Subject, subjectId),
                    new Claim(AccessTokenClaims.TenantId, tenant.ToString("N", CultureInfo.InvariantCulture)),
                    new Claim(AccessTokenClaims.SubjectType, subjectType),
                    new Claim(AccessTokenClaims.TokenId, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture))
                ]
            ),
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(key) { KeyId = keyId }, SecurityAlgorithms.EcdsaSha256)
        };

        return handler.CreateToken(descriptor);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (app is not null) {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        key.Dispose();
    }
}
