using CyberCloud.Gateway.Host.Authentication;
using CyberCloud.Identity.Contracts;
using OpenIddict.Abstractions;
using System.Security.Claims;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     The half of the production identity seam that reads claims — <see cref="JwksCallerContextResolver.ToClaims" />
///     — driven with hand-built principals that stand in for what the validator returns.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>What this does and does not cover.</b> Everything that happens <i>after</i> a token
///         has passed signature, issuer, audience and lifetime validation is here: the claims the
///         gateway requires, the ones it refuses, and the second expiry check. The validation itself
///         is OpenIddict's and needs a key set to exercise; <c>CyberCloud.AppHost.Tests</c>'
///         <c>TenantOverHttpTests</c> takes a token the identity host signed, hands it to the gateway
///         configured to trust that host, and then hands over the same token re-signed by nobody.
///     </para>
///     <para>
///         ⚠ Every refusal here is a token the identity host would never mint — <c>tid</c> is on
///         every token by <c>AccessTokenPrincipalFactory</c>'s construction — so each is a test of
///         what the gateway does when a <i>different</i> issuer's token arrives under a key it was
///         somehow told to trust. That is the case worth being strict in.
///     </para>
/// </remarks>
public sealed class JwksCallerContextResolverTests {
    static readonly Guid Tenant = Guid.Parse("3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d");
    static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) {
        var identity = new ClaimsIdentity("test");

        foreach (var (type, value) in claims) {
            identity.AddClaim(new(type, value));
        }

        return new(identity);
    }

    static ClaimsPrincipal Valid() =>
        Principal(
            (AccessTokenClaims.TenantId, Tenant.ToString("N")),
            (AccessTokenClaims.SubjectType, "servicePrincipal"),
            (AccessTokenClaims.Subject, "7c0e3b521d4f4a8b9e6c2f1a0b3c4d5e"),
            (AccessTokenClaims.Scope, "cyc.api"),
            (AccessTokenClaims.ExpiresAt, Now.AddMinutes(10).ToUnixTimeSeconds().ToString())
        );

    [Fact]
    public void EveryFieldComesFromTheValidatedPrincipal() {
        var claims = JwksCallerContextResolver.ToClaims(Valid(), Now).GetValueOrThrow();

        claims.TenantId.ShouldBe(Tenant);
        claims.SubjectType.ShouldBe("servicePrincipal");
        claims.SubjectId.ShouldBe("7c0e3b521d4f4a8b9e6c2f1a0b3c4d5e");
        claims.Scopes.ShouldBe("cyc.api");
        claims.ImpersonatedBy.ShouldBe("");
        claims.ExpiresAt.ShouldBe(Now.AddMinutes(10));
    }

    [Fact]
    public void TheExpiryOpenIddictMappedWinsOverTheRawClaim() {
        // OpenIddict maps a validated token's `exp` onto the principal as its private expiration
        // date; the raw claim is the fallback for a principal built some other way. When both are
        // present the validator's reading is the one that was checked.
        var principal = Valid();
        principal.SetExpirationDate(Now.AddMinutes(5));

        JwksCallerContextResolver.ToClaims(principal, Now).GetValueOrThrow().ExpiresAt.ShouldBe(Now.AddMinutes(5));
    }

    [Fact]
    public void AnImpersonationClaimIsCarriedAndAnAbsentOneIsEmpty() {
        var principal = Valid();
        principal.SetClaim(AccessTokenClaims.ImpersonatedBy, "9c1e7b403f2a4d589a6c8b2d5e0f1a34");

        JwksCallerContextResolver.ToClaims(principal, Now)
            .GetValueOrThrow()
            .ImpersonatedBy.ShouldBe("9c1e7b403f2a4d589a6c8b2d5e0f1a34");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("not-a-tenant")]
    public void AMissingMalformedOrEmptyTidIsRefusedNeverDefaulted(string? tid) {
        // ⚠ THE ONE CLAIM THE TENANCY BOUNDARY RESTS ON. Guid.Empty is the platform tenant and the D
        // form is not what the factory emits; a resolver that parsed leniently would let a token
        // from another issuer, under a key this gateway was told to trust, name the platform tenant
        // by leaving the claim out.
        var principal = Valid();
        principal.RemoveClaims(AccessTokenClaims.TenantId);

        if (tid is not null) {
            principal.SetClaim(AccessTokenClaims.TenantId, tid);
        }

        var refused = JwksCallerContextResolver.ToClaims(principal, Now);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
        refused.Error.Message.ShouldContain("tid");
    }

    [Fact]
    public void AMissingSubjectTypeIsRefused() {
        var principal = Valid();
        principal.RemoveClaims(AccessTokenClaims.SubjectType);

        JwksCallerContextResolver.ToClaims(principal, Now).Error!.Message.ShouldContain("sub_typ");
    }

    [Fact]
    public void AMissingSubjectIsRefused() {
        var principal = Valid();
        principal.RemoveClaims(AccessTokenClaims.Subject);

        JwksCallerContextResolver.ToClaims(principal, Now).Error!.Message.ShouldContain("sub");
    }

    [Fact]
    public void ATokenWithNoExpiryIsRefused() {
        var principal = Valid();
        principal.RemoveClaims(AccessTokenClaims.ExpiresAt);

        JwksCallerContextResolver.ToClaims(principal, Now).Error!.Message.ShouldContain("expiry");
    }

    [Fact]
    public void TheSecondExpiryCheckRefusesWhatAValidatorMightHaveLetThrough() {
        // ICallerContextResolver's item 6: the gateway enforces ExpiresAt itself as well, so a
        // validator misconfigured to ignore exp is still caught.
        JwksCallerContextResolver.ToClaims(Valid(), Now.AddMinutes(10)).Error!.Message.ShouldContain("expired");
        JwksCallerContextResolver.ToClaims(Valid(), Now.AddMinutes(11)).IsFailure.ShouldBeTrue();
    }

    [Theory]
    [InlineData("role")]
    [InlineData("roles")]
    [InlineData("groups")]
    [InlineData("permissions")]
    [InlineData("scope")]
    [InlineData("http://schemas.microsoft.com/ws/2008/06/identity/claims/role")]
    public void AForbiddenClaimIsARefusalNotAnExtraClaim(string forbidden) {
        // ⚠ The token-policy document the identity host publishes says the gateway "should treat a
        // token carrying one as suspect rather than as a token with extra claims". A role claim in a
        // Cyber Cloud token can only mean a token minted by something other than the factory that
        // refuses to emit one.
        var principal = Valid();
        principal.SetClaim(forbidden, "Owner");

        var refused = JwksCallerContextResolver.ToClaims(principal, Now);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain(forbidden);
    }
}
