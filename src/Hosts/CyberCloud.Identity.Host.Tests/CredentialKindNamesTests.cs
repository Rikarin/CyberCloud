using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Api;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     The eight strings <c>portal/apps/identity/src/app/identity-api.ts</c> compares against.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This suite is the only mechanical link between a C# enum and a TypeScript union.</b>
///         The frontend's <c>CredentialKind</c> is a hand-written literal type in another language;
///         nothing compiles the two together, and the comparison is <b>ordinal</b>, so a
///         one-character difference matches no branch and produces "the passkey button never appears"
///         rather than an error. docs/plan/11's <c>servicePrincipal</c>-versus-<c>serviceprincipal</c>
///         trap is the same bug, and this platform has already shipped it once as
///         <c>resourcegroup</c> against <c>resourceGroup</c>.
///     </para>
///     <para>
///         So the expected values below are written out as literals rather than derived. A test that
///         computed the expectation with the same rule the implementation uses would agree with any
///         bug they shared.
///     </para>
/// </remarks>
public sealed class CredentialKindNamesTests {
    /// <summary>
    ///     Every kind and its wire spelling, copied by hand from the TypeScript union.
    /// </summary>
    public static TheoryData<CredentialKind, string> Spellings =>
        new() {
            { CredentialKind.Passkey, "passkey" },
            { CredentialKind.Password, "password" },
            { CredentialKind.Totp, "totp" },
            { CredentialKind.RecoveryCode, "recoveryCode" },
            { CredentialKind.EmailOtp, "emailOtp" },
            { CredentialKind.SmsOtp, "smsOtp" },
            { CredentialKind.WhatsAppOtp, "whatsAppOtp" },
            { CredentialKind.Certificate, "certificate" }
        };

    [Theory]
    [MemberData(nameof(Spellings))]
    public void EachKindHasTheSpellingTheFrontendCompares(CredentialKind kind, string expected) =>
        CredentialKindNames.Of(kind).ShouldBe(
            expected,
            $"portal/apps/identity/src/app/identity-api.ts declares '{expected}' and compares it "
            + "ordinally. A different spelling here matches no branch in the page, and the symptom is "
            + "a missing button rather than an error."
        );

    [Fact]
    public void TheTableCoversEveryMemberOfTheEnum() {
        // ⚠ What makes CredentialKindNames.Of's throw unreachable. A member added to the enum without
        // a spelling would otherwise surface as a 500 from /api/signin/begin the first time it was
        // offered — at runtime, in production, on an unauthenticated endpoint.
        foreach (var kind in Enum.GetValues<CredentialKind>()) {
            CredentialKindNames.Mapped.ShouldContain(
                kind,
                $"{kind} has no wire spelling. Add one to CredentialKindNames and to the "
                + "CredentialKind union in portal/apps/identity/src/app/identity-api.ts — both, or "
                + "the page will not match it."
            );
        }
    }

    [Fact]
    public void NoTwoKindsShareASpelling() {
        // A duplicate would make two credentials indistinguishable on the wire, and the page would
        // offer whichever branch it happened to check first.
        var spellings = CredentialKindNames.Of(Enum.GetValues<CredentialKind>());

        spellings.Distinct(StringComparer.Ordinal).Count().ShouldBe(spellings.Length);
    }

    [Fact]
    public void EverySpellingIsCamelCaseWithNoSeparators() {
        // The shape the union uses. Catches the two mistakes that survive a careless review: an
        // upper-case first letter (`RecoveryCode`) and a separator (`recovery-code`, `recovery_code`).
        foreach (var spelling in CredentialKindNames.Of(Enum.GetValues<CredentialKind>())) {
            spelling.ShouldNotBeNullOrEmpty();
            char.IsLower(spelling[0]).ShouldBeTrue($"'{spelling}' does not start lower-case");
            spelling.ShouldAllBe(x => char.IsAsciiLetterOrDigit(x), $"'{spelling}' carries a separator");
        }
    }

    [Fact]
    public void AnUnmappedKindThrowsRatherThanGuessing() =>
        // ⚠ Cast from a value no member has. A ToString() fallback would answer "999" here, and a
        // caller would ship a JSON body carrying it.
        Should.Throw<ArgumentOutOfRangeException>(() => CredentialKindNames.Of((CredentialKind)999));
}
