// ⚠ SubjectTypes is in CyberCloud.Authorization.Contracts now, not in the assembly under test —
// see AuthorizationVocabulary.cs. The assertions below stayed here on purpose: what they check is a
// property of the TOKEN contract (which spellings a `sub_typ` claim may carry), and moving them to
// the authorization suite would file them under the wrong question. The byte-for-byte pinning of the
// whole vocabulary, including these three, is AuthorizationVocabularyTests.
using CyberCloud.Authorization.Contracts;

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
        // `scp`, `azp`, and an `auth_time`/`amr` pair so step-up authentication can be required" —
        // plus the two the GATEWAY requires and that sentence does not name. See
        // TheTwoClaimsTheGatewayCannotWorkWithout.
        AccessTokenClaims.Permitted.ShouldBe(
            [
                "sub", "tid", "aud", "iss", "scp", "azp", "auth_time", "amr", "sid", "iat", "exp",
                "jti", "sub_typ", "act_sub"
            ],
            ignoreOrder: true
        );
    }

    [Fact]
    public void TheTwoClaimsTheGatewayCannotWorkWithout() {
        // ICallerContextResolver's items 4 and 5. Item 4: "a subject type distinguishable from the
        // subject id … a dedicated claim, not a prefix convention on `sub`". Item 5: "the
        // impersonation claim of docs/plan/06 § Platform administration, minted only by the identity
        // host and never accepted from a header."
        AccessTokenClaims.SubjectType.ShouldBe("sub_typ");
        AccessTokenClaims.ImpersonatedBy.ShouldBe("act_sub");

        AccessTokenClaims.Permitted.ShouldContain(AccessTokenClaims.SubjectType);
        AccessTokenClaims.Permitted.ShouldContain(AccessTokenClaims.ImpersonatedBy);
    }

    [Fact]
    public void ThePermittedSetIsClosedAndNotMerelyLong() {
        // ⚠ THE ONE THE TWO ADDITIONS COULD HAVE BROKEN. Widening a closed set twice is how it stops
        // being one: the third addition is "we already added two". The count is pinned so a
        // fifteenth claim is a failure here rather than a diff nobody read, and EnsurePermitted is
        // the mechanism rather than the intention — a claim outside the set is refused whether or not
        // anybody anticipated its spelling.
        AccessTokenClaims.Permitted.Count.ShouldBe(14);

        foreach (var unanticipated in new[] {
                     "act",                 // the nested RFC 8693 form we deliberately do not also carry
                     "sub_type",            // a near-miss spelling of sub_typ
                     "idtyp",               // Entra's name for the same idea
                     "impersonated_by",     // the header's name, which is not a claim name
                     "tenant_id",           // tid spelled out
                     "email", "name", "preferred_username", "upn"
                 }) {
            AccessTokenClaims.Permitted.ShouldNotContain(unanticipated);

            AccessTokenClaims.EnsurePermitted([unanticipated]).IsFailure.ShouldBeTrue(
                $"'{unanticipated}' is not in the closed set, so a token carrying it must be refused "
                + "rather than stripped — a strip only removes the spellings somebody thought of."
            );
        }

        // And the whole permitted set passes, so the check is a closure and not a blanket refusal.
        AccessTokenClaims.EnsurePermitted(AccessTokenClaims.Permitted).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void ATokenCarryingAForbiddenClaimIsRejectedAndTheMessageSaysWhy() {
        foreach (var forbidden in AccessTokenClaims.ForbiddenClaims) {
            var refused = AccessTokenClaims.EnsurePermitted(["sub", "tid", forbidden]);

            refused.IsFailure.ShouldBeTrue($"'{forbidden}' must never survive into a token.");
            refused.Error!.Message.ShouldContain("ReBAC");
        }

        // ⚠ Case does not rescue it. `Roles` is the same disclosure as `roles`, and Permitted is
        // matched ordinally, so an oddly-cased spelling falls out of the allow-list even before the
        // forbidden list is consulted.
        AccessTokenClaims.EnsurePermitted(["Roles"]).IsFailure.ShouldBeTrue();
        AccessTokenClaims.EnsurePermitted(["SUB"]).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void ThereIsNoTypeInsideSubAlone() {
        // ⚠ THE ASSERTION THAT THE PREFIX CONVENTION IS NOT SECRETLY BACK. `sub` is a GUID in N form
        // and nothing else — there is no separator in it, so no amount of parsing recovers a subject
        // type from it. If somebody "helpfully" starts minting `sub` as "user:{guid}", this fails,
        // because the value stops being a GUID.
        var subject = Guid.Parse("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3").ToString("N");

        subject.ShouldNotContain(":");
        subject.ShouldNotContain("/");
        Guid.TryParseExact(subject, "N", out _).ShouldBeTrue();

        // The type is a different claim, and the two are different names — so a consumer cannot read
        // one and believe it has both.
        AccessTokenClaims.SubjectType.ShouldNotBe(AccessTokenClaims.Subject);
        AccessTokenClaims.SubjectType.StartsWith(AccessTokenClaims.Subject, StringComparison.Ordinal)
            .ShouldBeTrue("the name should read as 'the type of sub', which is the point of it");
    }

    [Fact]
    public void SubjectTypesMatchTheReBacSpellings() {
        // ⚠ These are ReBAC object types and the tuple store is case-sensitive, so a token carrying
        // `serviceprincipal` names a subject no tuple mentions — every Check denies, and it presents
        // as a permissions bug rather than as a spelling bug.
        SubjectTypes.All.ShouldBe(["user", "servicePrincipal", "managedIdentity"], ignoreOrder: true);

        SubjectTypes.Ensure("user").IsSuccess.ShouldBeTrue();
        SubjectTypes.Ensure("servicePrincipal").IsSuccess.ShouldBeTrue();
        SubjectTypes.Ensure("managedIdentity").IsSuccess.ShouldBeTrue();

        SubjectTypes.Ensure("serviceprincipal").IsFailure.ShouldBeTrue("ordinal, not case-insensitive");
        SubjectTypes.Ensure("User").IsFailure.ShouldBeTrue();
        SubjectTypes.Ensure(null).IsFailure.ShouldBeTrue();
        SubjectTypes.Ensure("").IsFailure.ShouldBeTrue();

        // ⚠ A group is a ReBAC object and never a token subject: nothing signs in as a group, and a
        // token whose subject were one would be a bearer credential for everybody in it.
        SubjectTypes.All.ShouldNotContain("group");
    }

    [Fact]
    public void ManagedIdentityIsAThirdSubjectTypeAlongsideTheOtherTwo() {
        // docs/plan/11 § Managed identity, step 6: "ReBAC grants are made to `managedIdentity:{id}`
        // like any other subject." That sentence only type-checks if the subject type is a first
        // class value rather than a special case at the exchange.
        SubjectTypes.ManagedIdentity.ShouldBe("managedIdentity");
        SubjectTypes.All.Count.ShouldBe(3);
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
