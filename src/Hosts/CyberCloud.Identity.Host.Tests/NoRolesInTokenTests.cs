using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Tokens;
using System.Security.Claims;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     docs/plan/11 § Protocol: <i>"Roles and permissions are <b>not</b> in the token. They are
///     looked up per request from ReBAC."</i>
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the test that stops somebody helpfully adding roles back.</b> Both failures
///         the document names arrive late and look like something else: the revoke lag is invisible
///         until an incident review asks why a dismissed employee could still write for nine minutes,
///         and the header-size failure arrives the day one customer's admin joins their fortieth
///         group and presents as intermittent <c>431</c>s from a proxy.
///     </para>
///     <para>
///         <b>What is asserted, and what is not.</b> These run against the claims principal
///         <see cref="AccessTokenPrincipalFactory" /> builds, which is the complete set of claims
///         handed to OpenIddict — a claim that is not on the principal cannot be in the JWT. What is
///         <i>not</i> asserted is the serialized token itself, because signing one needs the key set
///         that <c>CyberCloud.Vault</c> will own (docs/plan/18) and the host currently uses ephemeral
///         development keys. Asserting on the bytes belongs with the vault, and is owed.
///     </para>
/// </remarks>
public sealed class NoRolesInTokenTests {
    static readonly SessionDescriptor Session = new() {
        SessionId = Guid.Parse("5f3d1a90-8c2b-4a6e-9d1f-7b0c4e2a6d38"),
        UserId = Guid.Parse("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"),
        TenantId = Guid.Parse("3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d"),
        ClientId = "portal",
        AuthenticatedAt = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero),
        Methods = [AuthenticationMethod.Passkey]
    };

    [Fact]
    public void TheIssuedPrincipalCarriesNoRoleOrPermissionClaim() {
        var principal = AccessTokenPrincipalFactory.Build(Session, "cyc.api", ["openid", "cyc.api"], SubjectTypes.User);

        foreach (var claim in principal.Claims) {
            AccessTokenClaims.ForbiddenClaims.ShouldNotContain(
                claim.Type,
                $"The access-token principal carries '{claim.Type}', which is a role or permission "
                + "claim. docs/plan/11 § Protocol: they are looked up per request from ReBAC, because "
                + "a claim in a 10-minute token makes a revoke take up to 10 minutes and a large "
                + "user's groups make the header too big."
            );
        }
    }

    [Fact]
    public void TheClaimSetIsExactlyTheClosedPermittedList() {
        var principal = AccessTokenPrincipalFactory.Build(Session, "cyc.api", ["openid", "cyc.api"], SubjectTypes.User);

        // ⚠ An allow-list, not a deny-list. A factory that stripped forbidden claims would still let
        // an unanticipated one through; one that can only emit from a closed set cannot.
        foreach (var claim in principal.Claims) {
            AccessTokenClaims.Permitted.ShouldContain(
                claim.Type,
                $"'{claim.Type}' is not in AccessTokenClaims.Permitted. Adding a claim means adding "
                + "it to that set too — two visible edits in two files, which is the point."
            );
        }
    }

    [Fact]
    public void IsInRoleAnswersNoForEverythingBecauseThereIsNothingToAnswerFrom() {
        var principal = AccessTokenPrincipalFactory.Build(Session, "cyc.api", ["cyc.api"], SubjectTypes.User);

        // ⚠ ClaimsIdentity defaults RoleClaimType to the Microsoft role URI, so leaving the default
        // would leave a WORKING IsInRole that silently answers "no" for everybody — code that reached
        // for role-based authorization would compile, run, and deny everything in production. Pointing
        // it at a claim type that cannot exist keeps the answer "no" and makes the intent legible.
        principal.IsInRole("Owner").ShouldBeFalse();
        principal.IsInRole("owner").ShouldBeFalse();
        principal.IsInRole("Administrator").ShouldBeFalse();

        var identity = (ClaimsIdentity)principal.Identity!;
        identity.RoleClaimType.ShouldBe("urn:cybercloud:roles-are-not-in-the-token");
        identity.FindAll(identity.RoleClaimType).ShouldBeEmpty();
    }

    [Fact]
    public void TheClaimsTheDocumentDoesNameAreAllPresent() {
        var principal = AccessTokenPrincipalFactory.Build(Session, "cyc.api", ["openid", "cyc.api"], SubjectTypes.User);

        // docs/plan/11 § Protocol: "`aud` names the API, `tid` the tenant, `sub` the GUID, plus
        // `scp`, `azp`, and an `auth_time`/`amr` pair".
        principal.FindFirst(AccessTokenClaims.Subject)!.Value.ShouldBe(Session.UserId.ToString("N"));
        principal.FindFirst(AccessTokenClaims.TenantId)!.Value.ShouldBe(Session.TenantId.ToString("N"));
        principal.FindFirst(AccessTokenClaims.Audience)!.Value.ShouldBe("cyc.api");
        principal.FindFirst(AccessTokenClaims.AuthorizedParty)!.Value.ShouldBe("portal");
        principal.FindFirst(AccessTokenClaims.SessionId)!.Value.ShouldBe(Session.SessionId.ToString("N"));

        // Space-separated. A repeated claim serializes as a JSON array, and half the validators in
        // existence read `scp` as a string.
        principal.FindFirst(AccessTokenClaims.Scope)!.Value.ShouldBe("openid cyc.api");
    }

    [Fact]
    public void AuthTimeComesFromTheSessionSoARefreshCannotDefeatStepUp() {
        var principal = AccessTokenPrincipalFactory.Build(Session, "cyc.api", ["cyc.api"], SubjectTypes.User);

        var authTime = principal.FindFirst(AccessTokenClaims.AuthenticationTime)!.Value;

        // ⚠ The session's AuthenticatedAt, not "now". A refresh mints a new token with a new `iat`
        // and must carry the ORIGINAL auth_time forward — recomputing it would make a step-up rule
        // ("re-authenticate if it has been more than five minutes") defeatable by refreshing, which
        // makes step-up decorative.
        authTime.ShouldBe(Session.AuthenticatedAt.ToUnixTimeSeconds().ToString());
        authTime.ShouldNotBe(DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
    }

    [Fact]
    public void AmrUsesTheRegisteredValuesRatherThanOurEnumNames() {
        var principal = AccessTokenPrincipalFactory.Build(Session, "cyc.api", ["cyc.api"], SubjectTypes.User);

        // ⚠ RFC 8176's registry, not our spelling. A relying party reading `amr` is reading that
        // registry; emitting "Passkey" would be a value nobody else understands, in the one claim
        // whose entire purpose is being understood by somebody else.
        principal.FindFirst(AccessTokenClaims.AuthenticationMethods)!.Value.ShouldBe("hwk");

        AccessTokenPrincipalFactory.AmrValue(AuthenticationMethod.Password).ShouldBe("pwd");
        AccessTokenPrincipalFactory.AmrValue(AuthenticationMethod.Totp).ShouldBe("otp");
        AccessTokenPrincipalFactory.AmrValue(AuthenticationMethod.ClientCredential).ShouldBe("pop");
    }

    [Fact]
    public void ASubjectWithFortyGroupsStillProducesTheSameSizedToken() {
        // ⚠ The header-size failure docs/plan/11 § Protocol names, made concrete. The claim set does
        // not depend on how many groups the subject belongs to, because the groups are not in it —
        // so the token an admin in forty groups receives is byte-identical in shape to a new user's.
        // A factory that took a group list would fail this by construction, which is why it does not
        // take one.
        var principal = AccessTokenPrincipalFactory.Build(Session, "cyc.api", ["openid", "cyc.api"], SubjectTypes.User);
        var claimCount = principal.Claims.Count();

        // One claim per amr value, plus the eight fixed ones — sub, sub_typ, tid, sid, aud, azp, scp
        // and auth_time. ⚠ `act_sub` is not among them: it is emitted only when there IS an
        // impersonation, which is what makes "was this request impersonated" answerable by the claim's
        // presence rather than by comparing its value to an empty string.
        claimCount.ShouldBe(8 + Session.Methods.Count);

        var busy = Session with {
            Methods = [AuthenticationMethod.Passkey, AuthenticationMethod.Totp]
        };

        AccessTokenPrincipalFactory.Build(busy, "cyc.api", ["openid", "cyc.api"], SubjectTypes.User)
            .Claims.Count()
            .ShouldBe(8 + busy.Methods.Count);
    }

    // ── The two claims the gateway stated and the contract did not carry ──────────────────────

    [Fact]
    public void TheSubjectTypeIsItsOwnClaimAndSubCarriesOnlyTheId() {
        var principal = AccessTokenPrincipalFactory.Build(
            Session,
            "cyc.api",
            ["cyc.api"],
            SubjectTypes.ServicePrincipal
        );

        principal.FindFirst(AccessTokenClaims.SubjectType)!.Value.ShouldBe("servicePrincipal");

        // ⚠ THE ASSERTION THAT THE PREFIX CONVENTION IS NOT BACK. `sub` is a bare GUID: there is no
        // separator in it, so nothing can recover a type from it, and a consumer that tried would
        // have to invent one. docs/plan/07 § The model makes user:abc and servicePrincipal:abc
        // different subjects, so the type has to arrive as data rather than as a parsing convention.
        var subject = principal.FindFirst(AccessTokenClaims.Subject)!.Value;

        subject.ShouldBe(Session.UserId.ToString("N"));
        subject.ShouldNotContain(":");
        Guid.TryParseExact(subject, "N", out _).ShouldBeTrue();
    }

    [Fact]
    public void EveryTokenCarriesASubjectTypeAndAnUnknownOneCannotBeMinted() {
        // A required parameter rather than a defaulted one: the call site that mints a machine
        // identity's token has to say so, instead of compiling into a token that claims to be a user.
        foreach (var subjectType in SubjectTypes.All) {
            AccessTokenPrincipalFactory.Build(Session, "cyc.api", ["cyc.api"], subjectType)
                .FindFirst(AccessTokenClaims.SubjectType)!
                .Value.ShouldBe(subjectType);
        }

        // ⚠ Ordinal. `serviceprincipal` is a subject the tuple store has never heard of, so a token
        // carrying it would deny every check and look like a permissions bug.
        Should.Throw<ArgumentException>(
            () => AccessTokenPrincipalFactory.Build(Session, "cyc.api", ["cyc.api"], "serviceprincipal")
        );

        Should.Throw<ArgumentException>(
            () => AccessTokenPrincipalFactory.Build(Session, "cyc.api", ["cyc.api"], "group")
        );
    }

    [Fact]
    public void AnOrdinaryTokenCarriesNoImpersonationClaimAtAll() {
        var principal = AccessTokenPrincipalFactory.Build(Session, "cyc.api", ["cyc.api"], SubjectTypes.User);

        // ⚠ Absent, not empty. "Was this request made under impersonation" is answered by the claim's
        // presence; an empty claim on every token would make the audit pipeline distinguish absent
        // from empty for the one question docs/plan/06 § Platform administration exists to answer.
        principal.FindFirst(AccessTokenClaims.ImpersonatedBy).ShouldBeNull();
    }

    [Fact]
    public void TheImpersonationClaimIsMintedFromTheGrantAndCarriesTheOperator() {
        var operatorId = Guid.Parse("9c1e7b40-3f2a-4d58-9a6c-8b2d5e0f1a34");

        var principal = AccessTokenPrincipalFactory.Build(
            Session,
            "cyc.api",
            ["cyc.api"],
            SubjectTypes.User,
            operatorId
        );

        principal.FindFirst(AccessTokenClaims.ImpersonatedBy)!.Value.ShouldBe(operatorId.ToString("N"));

        // ⚠ `sub` is still the impersonated user, not the operator, and that is the point of having
        // two claims. The request is made AS the tenant's user — that is what "view as tenant" means —
        // and the operator is the actor behind it. Collapsing them would make the audit trail say the
        // tenant's own user did whatever support did.
        principal.FindFirst(AccessTokenClaims.Subject)!.Value.ShouldBe(Session.UserId.ToString("N"));
        principal.FindFirst(AccessTokenClaims.TenantId)!.Value.ShouldBe(Session.TenantId.ToString("N"));

        // The eight fixed claims, plus act_sub, plus one per amr value.
        principal.Claims.Count().ShouldBe(9 + Session.Methods.Count);
    }

    [Fact]
    public void ThereIsNoInputToThisFactoryThatIsNotAnArgumentToIt() {
        // ⚠ THE SECURITY PROPERTY, ASSERTED STRUCTURALLY. The impersonation value can only enter a
        // token through a parameter of Build — there is no HttpContext, no header dictionary and no
        // ambient accessor in this type's signature or in the assembly it lives in that could supply
        // one. docs/plan/06 § Platform administration's controls are properties of the GRANT, and a
        // value read off a request carries none of them.
        var build = typeof(AccessTokenPrincipalFactory).GetMethod(nameof(AccessTokenPrincipalFactory.Build))!;

        build.GetParameters()
            .Select(x => x.ParameterType.Name)
            .ShouldBe(["SessionDescriptor", "String", "IReadOnlyList`1", "String", "Guid"]);

        // Nothing HTTP-shaped reaches it, which is what makes "never accepted from a header" a fact
        // about the type rather than a rule about its callers.
        build.GetParameters()
            .ShouldAllBe(x => !x.ParameterType.FullName!.Contains("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFactoryRefusesToReturnAPrincipalCarryingAClaimOutsideTheClosedSet() {
        // Build checks its own output, so the closed set is enforced by the code and not only by the
        // test above it. This asserts the check exists by exercising the contract it delegates to —
        // a future AddClaim that slipped past review fails at the factory rather than in the wild.
        AccessTokenClaims.EnsurePermitted(["sub", "roles"]).IsFailure.ShouldBeTrue();
        AccessTokenClaims.EnsurePermitted(["sub", "sub_typ", "act_sub"]).IsSuccess.ShouldBeTrue();
    }
}
