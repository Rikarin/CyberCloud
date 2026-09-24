using CyberCloud.Core.Resources;
using Shouldly;
using System.Globalization;

namespace CyberCloud.Core.Tests;

/// <summary>
///     The identity addresses — <c>/tenants/{t}/providers/CyberCloud.Identity/…</c> — parse to what
///     they format from, and nothing else under the namespace parses at all. Issue #41.
/// </summary>
public sealed class IdentityAddressTests {
    static readonly Guid Tenant = Guid.Parse("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3");
    static readonly Guid Item = Guid.Parse("0a1b2c3d-4e5f-4071-8293-a4b5c6d7e8f9");

    public static TheoryData<IdentityAddressKind, string> Every =>
        new() {
            { IdentityAddressKind.Invitations, "invitations" },
            { IdentityAddressKind.Invitation, "invitations/0a1b2c3d4e5f40718293a4b5c6d7e8f9" },
            { IdentityAddressKind.InvitationResend, "invitations/0a1b2c3d4e5f40718293a4b5c6d7e8f9/resend" },
            { IdentityAddressKind.Members, "members" },
            { IdentityAddressKind.Member, "members/0a1b2c3d4e5f40718293a4b5c6d7e8f9" },
            { IdentityAddressKind.Applications, "applications" },
            { IdentityAddressKind.Application, "applications/0a1b2c3d4e5f40718293a4b5c6d7e8f9" },
            { IdentityAddressKind.ApplicationSecret, "applications/0a1b2c3d4e5f40718293a4b5c6d7e8f9/rotateSecret" },
            { IdentityAddressKind.Sessions, "sessions" },
            { IdentityAddressKind.Session, "sessions/0a1b2c3d4e5f40718293a4b5c6d7e8f9" }
        };

    [Theory]
    [MemberData(nameof(Every))]
    public void EveryAddressRoundTripsThroughItsPath(IdentityAddressKind kind, string suffix) {
        var path = "/tenants/" + Tenant.ToString("D", CultureInfo.InvariantCulture) + "/providers/CyberCloud.Identity/" + suffix;

        IdentityAddress.IsUnderNamespace(path).ShouldBeTrue();

        var parsed = IdentityAddress.ParsePath(path).GetValueOrThrow();

        parsed.Kind.ShouldBe(kind);
        parsed.TenantId.ShouldBe(Tenant);
        parsed.Id.ShouldBe(suffix.Contains('/', StringComparison.Ordinal) ? Item : Guid.Empty);
        parsed.Path.ShouldBe(path);
    }

    [Theory]
    [InlineData("users", "a collection that isn't one")]
    [InlineData("Members", "the literal is ordinal: a second spelling is a second cache key")]
    [InlineData("members/", "a trailing slash")]
    [InlineData("members/0a1b2c3d-4e5f-4071-8293-a4b5c6d7e8f9", "the D form: one member, one spelling")]
    [InlineData("members/0A1B2C3D4E5F40718293A4B5C6D7E8F9", "upper case")]
    [InlineData("members/0a1b2c3d4e5f40718293a4b5c6d7e8f9/resend", "resend is an invitation's verb")]
    [InlineData("invitations/0a1b2c3d4e5f40718293a4b5c6d7e8f9/rotateSecret", "rotateSecret is an application's verb")]
    [InlineData("sessions/0a1b2c3d4e5f40718293a4b5c6d7e8f9/resend", "a session has no verbs")]
    [InlineData("applications/0a1b2c3d4e5f40718293a4b5c6d7e8f9/rotateSecret/again", "too deep")]
    [InlineData("", "nothing after the namespace")]
    public void AnyOtherShapeUnderTheNamespaceIsRefusedWithTheListOfAddresses(string suffix, string why) {
        var path = "/tenants/" + Tenant.ToString("D", CultureInfo.InvariantCulture) + "/providers/CyberCloud.Identity/" + suffix;

        var parsed = IdentityAddress.ParsePath(path);

        parsed.IsFailure.ShouldBeTrue(why);
        parsed.Error!.Code.ShouldBe(ErrorCode.InvalidResourceId);
        parsed.Error.Message.ShouldContain("rotateSecret", Case.Sensitive, "the refusal lists every address");
    }

    [Fact]
    public void TheScopeInFrontMustBeATenant() {
        var path = "/tenants/" + Tenant.ToString("D", CultureInfo.InvariantCulture)
            + "/subscriptions/00000000-0000-4000-8000-000000000001/providers/CyberCloud.Identity/members";

        var parsed = IdentityAddress.ParsePath(path);

        parsed.IsFailure.ShouldBeTrue();
        parsed.Error!.Message.ShouldContain("must be a tenant");
    }

    [Fact]
    public void AnItemCarriesItsCollectionsTypeAndTheSessionsNameNoUser() {
        IdentityAddress.Members(Tenant).Item(Item).ItemType.ShouldBe("CyberCloud.Identity/members");
        new IdentityAddress(Tenant, IdentityAddressKind.ApplicationSecret, Item).ItemType.ShouldBe("CyberCloud.Identity/applications");

        // ⚠ "Mine" — the address has no user in it, so it can't name anybody else's.
        IdentityAddress.Sessions(Tenant).Path.ShouldEndWith("/providers/CyberCloud.Identity/sessions");

        Should.Throw<InvalidOperationException>(() => new IdentityAddress(Tenant, IdentityAddressKind.Member, Item).Item(Item));
    }
}
