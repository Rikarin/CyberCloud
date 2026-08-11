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
        var principal = AccessTokenPrincipalFactory.Build(Session, "cyc.api", ["openid", "cyc.api"]);

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
        var principal = AccessTokenPrincipalFactory.Build(Session, "cyc.api", ["openid", "cyc.api"]);

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
        var principal = AccessTokenPrincipalFactory.Build(Session, "cyc.api", ["cyc.api"]);

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
        var principal = AccessTokenPrincipalFactory.Build(Session, "cyc.api", ["openid", "cyc.api"]);

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
        var principal = AccessTokenPrincipalFactory.Build(Session, "cyc.api", ["cyc.api"]);

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
        var principal = AccessTokenPrincipalFactory.Build(Session, "cyc.api", ["cyc.api"]);

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
        var principal = AccessTokenPrincipalFactory.Build(Session, "cyc.api", ["openid", "cyc.api"]);
        var claimCount = principal.Claims.Count();

        // One claim per amr value, plus the seven fixed ones.
        claimCount.ShouldBe(7 + Session.Methods.Count);

        var busy = Session with {
            Methods = [AuthenticationMethod.Passkey, AuthenticationMethod.Totp]
        };

        AccessTokenPrincipalFactory.Build(busy, "cyc.api", ["openid", "cyc.api"])
            .Claims.Count()
            .ShouldBe(7 + busy.Methods.Count);
    }
}
