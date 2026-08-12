using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Api;
using System.Security.Claims;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     A session that owes a second factor must not count as a session.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The dangerous state is representable, so it has to be checked.</b>
///         <c>SignInService</c> opens the session as soon as the first factor verifies and sets
///         <see cref="SignInOutcome.SecondFactorRequired" /> for everything but a passkey — so
///         between <c>/api/signin/password</c> and <c>/api/signin/totp</c> there is a real cookie
///         naming a real user who has presented one factor. Every read of that cookie that forgets
///         to ask about the second factor treats a half-authenticated caller as authenticated.
///     </para>
///     <para>
///         The design decision this suite pins is <b>fail closed on absence</b>: a missing
///         second-factor claim is a <see langword="false" />, not a "none was needed". The cookie
///         minted by an older build, or by a path somebody adds and forgets to stamp, must be a
///         re-prompt rather than a free pass.
///     </para>
/// </remarks>
public sealed class PartialSessionTests {
    static readonly Guid Tenant = Guid.Parse("6f2b7c14-9a3d-4e58-b061-7c2d5e8f9a10");
    static readonly Guid User = Guid.Parse("11112222-3333-4444-5555-666677778888");
    static readonly Guid Session = Guid.Parse("99990000-aaaa-bbbb-cccc-ddddeeeeffff");

    [Fact]
    public void APasswordSignInIsNotFullyAuthenticatedUntilTheSecondFactorArrives() {
        var pending = IdentitySessionPrincipal.Build(
            Tenant,
            SignInOutcome.Success(User, Session, AuthenticationMethod.Password, secondFactorRequired: true)
        );

        IdentitySessionPrincipal.IsFullyAuthenticated(pending).ShouldBeFalse(
            "A password sign-in that still owes a second factor produced a session that counts as "
            + "whole. The authorization endpoint reads this, so a true here is a sign-in that "
            + "completes with one factor."
        );

        // It still names who is answering — that is the whole reason the cookie is issued at all.
        IdentitySessionPrincipal.UserId(pending).ShouldBe(User);
        IdentitySessionPrincipal.SessionId(pending).ShouldBe(Session);
    }

    [Fact]
    public void APasskeySignInIsFullyAuthenticatedImmediately() {
        // ⚠ docs/plan/11 § Credentials, and SignInService says the same: an assertion with user
        // verification is already two factors. Asking for a TOTP afterwards is theatre that trains
        // users to reach for the weaker credential.
        var passkey = IdentitySessionPrincipal.Build(
            Tenant,
            SignInOutcome.Success(User, Session, AuthenticationMethod.Passkey)
        );

        IdentitySessionPrincipal.IsFullyAuthenticated(passkey).ShouldBeTrue();
    }

    [Fact]
    public void PresentingTheSecondFactorPromotesTheSessionAndKeepsBothMethods() {
        var pending = IdentitySessionPrincipal.Build(
            Tenant,
            SignInOutcome.Success(User, Session, AuthenticationMethod.Password, secondFactorRequired: true)
        );

        var promoted = IdentitySessionPrincipal.Promote(pending, AuthenticationMethod.Totp);

        IdentitySessionPrincipal.IsFullyAuthenticated(promoted).ShouldBeTrue();
        IdentitySessionPrincipal.UserId(promoted).ShouldBe(User);
        IdentitySessionPrincipal.SessionId(promoted).ShouldBe(Session);

        // ⚠ Both factors in `amr`, not just the second. An audit trail recording only "otp" would
        // lose the fact that a password was the first factor — and `amr` is a list precisely so it
        // does not have to choose.
        var methods = promoted.FindAll(AccessTokenClaims.AuthenticationMethods)
            .Select(x => x.Value)
            .ToList();

        methods.ShouldContain("pwd");
        methods.ShouldContain("otp");
    }

    [Fact]
    public void PromotingDoesNotMutateThePrincipalItWasGiven() {
        var pending = IdentitySessionPrincipal.Build(
            Tenant,
            SignInOutcome.Success(User, Session, AuthenticationMethod.Password, secondFactorRequired: true)
        );

        _ = IdentitySessionPrincipal.Promote(pending, AuthenticationMethod.Totp);

        // A promote that edited its argument would silently upgrade the principal on the current
        // HttpContext even on a path that then decided to reject.
        IdentitySessionPrincipal.IsFullyAuthenticated(pending).ShouldBeFalse();
    }

    [Fact]
    public void APrincipalWithNoSecondFactorClaimIsNotFullyAuthenticated() {
        // ⚠ Fails closed. This is the cookie an older build minted, or one a new code path forgot to
        // stamp — and the wrong answer here is the one that reads as "no second factor was needed".
        var identity = new ClaimsIdentity(IdentityHostAuthentication.SchemeName);
        identity.AddClaim(new(AccessTokenClaims.Subject, User.ToString("N")));

        IdentitySessionPrincipal.IsFullyAuthenticated(new(identity)).ShouldBeFalse();
    }

    [Fact]
    public void AnAnonymousPrincipalNamesNobodyAndIsNotAuthenticated() {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        IdentitySessionPrincipal.IsFullyAuthenticated(anonymous).ShouldBeFalse();
        IdentitySessionPrincipal.UserId(anonymous).ShouldBeNull();
        IdentitySessionPrincipal.SessionId(anonymous).ShouldBeNull();
        IdentitySessionPrincipal.IsFullyAuthenticated(null).ShouldBeFalse();
    }

    [Fact]
    public void AClaimsIdentityWithNoAuthenticationTypeCarriesNoAuthority() {
        // ⚠ The trap the Build() comment names. A ClaimsIdentity built with the parameterless
        // constructor has IsAuthenticated false however many claims it holds, so a principal
        // assembled that way authorizes nothing — and the symptom is a 401 that reads as a missing
        // cookie rather than as a construction bug.
        var unstamped = new ClaimsIdentity();
        unstamped.AddClaim(new(IdentitySessionPrincipal.SecondFactorClaim, IdentitySessionPrincipal.Satisfied));
        unstamped.AddClaim(new(AccessTokenClaims.Subject, User.ToString("N")));

        IdentitySessionPrincipal.IsFullyAuthenticated(new(unstamped)).ShouldBeFalse();
    }

    [Fact]
    public void TheSessionPrincipalCarriesTheTenantAndTheSubjectType() {
        var principal = IdentitySessionPrincipal.Build(
            Tenant,
            SignInOutcome.Success(User, Session, AuthenticationMethod.Passkey)
        );

        principal.FindFirst(AccessTokenClaims.TenantId)?.Value.ShouldBe(Tenant.ToString("N"));

        // Without sub_typ a ReBAC SubjectRef cannot be built, and every Check is made against a
        // guessed type — docs/plan/11 § Protocol.
        principal.FindFirst(AccessTokenClaims.SubjectType)?.Value.ShouldBe(SubjectTypes.User);
    }
}
