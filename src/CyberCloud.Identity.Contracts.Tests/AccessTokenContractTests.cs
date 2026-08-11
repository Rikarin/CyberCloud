namespace CyberCloud.Identity.Contracts.Tests;

/// <summary>
///     The token contract the gateway is built against. docs/plan/11 § Protocol, § Sessions and
///     revocation.
/// </summary>
/// <remarks>
///     ⚠ <b>These assertions look trivial and are not.</b> Every one of them is a number or a flag
///     that a second team is building against on a different branch, and the failure mode of getting
///     one wrong is not a compile error — it is a gateway that accepts a token it should have
///     rejected, or an identity host that issues one nobody can validate. Pinning them in a test
///     makes a change to any of them a visible, deliberate act.
/// </remarks>
public sealed class AccessTokenContractTests {
    [Fact]
    public void TheAccessTokenLifetimeIsTenMinutesBecauseThatIsTheRevocationStory() {
        // docs/plan/11 § Sessions and revocation: "access tokens are not revocable and are not made
        // so. They live 10 minutes." Lengthening this widens the window a revoked grant still works
        // in; that is a security decision, not a tuning knob.
        AccessTokenPolicy.AccessTokenLifetime.ShouldBe(TimeSpan.FromMinutes(10));
        AccessTokenPolicy.AccessTokensAreRevocable.ShouldBeFalse();
    }

    [Fact]
    public void ThereIsNoIntrospectionOnTheRequestPath() {
        // ⚠ docs/plan/11 § Sessions and revocation: "an introspection call per request would put the
        // identity system on the hot path of every request, which is precisely what a short token is
        // for." The gateway reads this flag; an endpoint that exists gets used, so there is not one.
        AccessTokenPolicy.SupportsIntrospection.ShouldBeFalse();

        // The gateway validates locally, against these.
        AccessTokenPolicy.DiscoveryPath.ShouldBe("/.well-known/openid-configuration");
        AccessTokenPolicy.JsonWebKeySetPath.ShouldBe("/.well-known/jwks");
        AccessTokenPolicy.SigningAlgorithm.ShouldBe("ES256");
    }

    [Fact]
    public void TheKeyRotationScheduleIsThirtyDaysWithSixtyDaysOfOverlap() {
        // docs/plan/11 § Protocol: "a rotating key set (30-day rotation, both keys published for 60)".
        AccessTokenPolicy.SigningKeyRotation.ShouldBe(TimeSpan.FromDays(30));
        AccessTokenPolicy.SigningKeyOverlap.ShouldBe(TimeSpan.FromDays(60));

        // ⚠ The overlap must exceed the rotation, or a key is retired while tokens signed with it are
        // still live and every one of them fails validation at once.
        AccessTokenPolicy.SigningKeyOverlap.ShouldBeGreaterThan(AccessTokenPolicy.SigningKeyRotation);
    }

    [Fact]
    public void ARefreshChainDiesOfOldAgeEvenIfItIsUsedEveryDay() {
        // ⚠ Without an absolute cap a rotating chain is immortal: every use extends it, so a stolen
        // chain exercised daily never expires on its own.
        AccessTokenPolicy.AbsoluteSessionLifetime.ShouldBeGreaterThan(AccessTokenPolicy.RefreshTokenLifetime);
        AccessTokenPolicy.RefreshTokenLifetime.ShouldBe(TimeSpan.FromDays(14));
        AccessTokenPolicy.AbsoluteSessionLifetime.ShouldBe(TimeSpan.FromDays(90));
    }

    [Fact]
    public void ThePermittedClaimSetIsExactlyWhatTheDocumentNames() {
        // docs/plan/11 § Protocol: "`aud` names the API, `tid` the tenant, `sub` the GUID, plus
        // `scp`, `azp`, and an `auth_time`/`amr` pair so step-up authentication can be required".
        AccessTokenClaims.Permitted.ShouldBe(
            ["sub", "tid", "aud", "iss", "scp", "azp", "auth_time", "amr", "sid", "iat", "exp", "jti"],
            ignoreOrder: true
        );
    }

    [Fact]
    public void NoRoleOrPermissionClaimIsPermitted() {
        // ⚠ THE ONE THAT IS EASY TO "HELPFULLY" ADD BACK. docs/plan/11 § Protocol: roles "are looked
        // up per request from ReBAC. Putting role claims in a 10-minute token means a revoke takes up
        // to 10 minutes, and packing a large user's groups into a JWT produces the header-size
        // failures every large enterprise hits."
        foreach (var forbidden in AccessTokenClaims.ForbiddenClaims) {
            AccessTokenClaims.Permitted.ShouldNotContain(
                forbidden,
                $"'{forbidden}' must never be an access-token claim — docs/plan/11 § Protocol."
            );
        }
    }

    [Fact]
    public void TheForbiddenListCoversTheSpellingsThatArriveByAccident() {
        // Each of these is what a role claim is called somewhere in the .NET, Azure or OIDC world,
        // and the last is the trap: `identity.AddClaim(ClaimTypes.Role, …)` produces that URI without
        // anybody typing it, and ClaimsPrincipal.IsInRole reads it.
        AccessTokenClaims.ForbiddenClaims.ShouldContain("role");
        AccessTokenClaims.ForbiddenClaims.ShouldContain("roles");
        AccessTokenClaims.ForbiddenClaims.ShouldContain("groups");
        AccessTokenClaims.ForbiddenClaims.ShouldContain("permissions");
        AccessTokenClaims.ForbiddenClaims.ShouldContain("scope");
        AccessTokenClaims.ForbiddenClaims.ShouldContain(
            "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"
        );

        // Matched case-insensitively, because a claim named `Roles` is the same disclosure.
        AccessTokenClaims.ForbiddenClaims.ShouldContain("ROLES");
    }

    [Fact]
    public void ScopeIsCarriedAsScpAndNotAlsoAsScope() {
        // Carrying both would be two sources of truth for the same thing, and a validator that read
        // the one we did not intend.
        AccessTokenClaims.Scope.ShouldBe("scp");
        AccessTokenClaims.Permitted.ShouldNotContain("scope");
    }

    [Fact]
    public void AuthTimeIsSeparateFromIssuedAtSoStepUpCannotBeRefreshedAway() {
        AccessTokenClaims.AuthenticationTime.ShouldBe("auth_time");
        AccessTokenClaims.IssuedAt.ShouldBe("iat");
        AccessTokenClaims.AuthenticationTime.ShouldNotBe(AccessTokenClaims.IssuedAt);
    }

    [Fact]
    public void TheGrantTableHasNoResourceOwnerPasswordAndNoImplicit() {
        // ⚠ Both were removed in OAuth 2.1, and password credentials additionally defeat MFA —
        // docs/plan/11 § Protocol. Leaving the names out of the enum is what makes an application
        // registration unable to ask for them; a validation rule could be relaxed, an absent enum
        // member cannot.
        var names = Enum.GetNames<GrantType>();

        names.ShouldNotContain("Password");
        names.ShouldNotContain("ResourceOwnerPassword");
        names.ShouldNotContain("Implicit");
        names.ShouldNotContain("Hybrid");

        names.ShouldBe(
            ["AuthorizationCode", "DeviceAuthorization", "ClientCredentials", "RefreshToken", "TokenExchange"],
            ignoreOrder: true
        );
    }

    [Fact]
    public void PasskeyIsTheFirstCredentialKindBecauseItIsTheDefaultOffered() {
        // docs/plan/11 § Credentials: "the DEFAULT offered credential at sign-up, not an upsell. A
        // platform starting in 2026 that leads with passwords is choosing the worse security posture
        // on purpose." The enrolment page reads this order, so the order carries meaning.
        Enum.GetValues<CredentialKind>()[0].ShouldBe(CredentialKind.Passkey);
        ((int)CredentialKind.Passkey).ShouldBeLessThan((int)CredentialKind.Password);
    }

    [Fact]
    public void TheUniformFailuresAreConstantsRatherThanPerCallSiteStrings() {
        // "The same response" is a property that has to survive the next person adding a branch, and
        // it only does if there is one string.
        UniformFailures.SignIn.ShouldNotBeNullOrWhiteSpace();
        UniformFailures.PasswordReset.ShouldNotBeNullOrWhiteSpace();
        UniformFailures.SignUp.ShouldNotBeNullOrWhiteSpace();

        // ⚠ And none of them may say whether the account exists.
        foreach (var message in new[] { UniformFailures.SignIn, UniformFailures.PasswordReset, UniformFailures.SignUp }) {
            message.ShouldNotContain("no such", Case.Insensitive);
            message.ShouldNotContain("not found", Case.Insensitive);
            message.ShouldNotContain("does not exist", Case.Insensitive);
            message.ShouldNotContain("unknown user", Case.Insensitive);
        }

        UniformFailures.RejectSignIn().Error!.Message.ShouldBe(UniformFailures.SignIn);
    }

    [Fact]
    public void TotpParametersMatchTheDocument() {
        // docs/plan/11 § Credentials: "RFC 6238 in-house, ~200 lines, ±1 window".
        TotpParameters.Digits.ShouldBe(6);
        TotpParameters.PeriodSeconds.ShouldBe(30);
        TotpParameters.DriftSteps.ShouldBe(1);
        TotpParameters.SecretBytes.ShouldBe(20);
    }
}
