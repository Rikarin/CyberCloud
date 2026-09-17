using CyberCloud.Core;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Tests.Infrastructure;

namespace CyberCloud.Identity.Tests;

/// <summary>
///     A person's consent to one tenant-registered client: recorded per scope set, widened by a
///     later allowance, emptied by a revocation, and never written under another pair's key.
///     docs/plan/11 § Protocol.
/// </summary>
[Collection(IdentitySuite.Name)]
public sealed class ConsentGrantTests(IdentityCluster cluster) {
    [Fact]
    public async Task NothingIsOnRecordUntilSomebodyConsents() {
        var consent = cluster.Consent(Guid.NewGuid(), "crm");

        var absent = await consent.GetAsync();

        absent.IsFailure.ShouldBeTrue();
        absent.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public async Task AGrantCoversWhatWasAllowedAndAWiderRequestDoesNot() {
        var user = Guid.NewGuid();
        var consent = cluster.Consent(user, "crm");

        var granted = await consent.GrantAsync(user, "crm", ["openid", "profile"]);

        granted.IsSuccess.ShouldBeTrue(granted.Error?.Message);
        granted.GetValueOrThrow().UserId.ShouldBe(user);
        granted.GetValueOrThrow().ClientId.ShouldBe("crm");
        granted.GetValueOrThrow().Scopes.ShouldBe(["openid", "profile"]);
        granted.GetValueOrThrow().Covers(["openid"]).ShouldBeTrue();
        granted.GetValueOrThrow().Covers(["openid", "profile"]).ShouldBeTrue();

        // ⚠ A person who allowed openid and profile has not allowed the API. The host asks again.
        granted.GetValueOrThrow().Covers(["openid", "cyc.api"]).ShouldBeFalse();
        granted.GetValueOrThrow().Covers(["Profile"]).ShouldBeFalse("scopes are compared ordinally");
    }

    [Fact]
    public async Task ALaterAllowanceWidensRatherThanReplaces() {
        var user = Guid.NewGuid();
        var consent = cluster.Consent(user, "crm");

        (await consent.GrantAsync(user, "crm", ["openid", "profile"])).IsSuccess.ShouldBeTrue();

        var widened = await consent.GrantAsync(user, "crm", ["openid", "cyc.api"]);

        widened.GetValueOrThrow().Scopes.ShouldBe(["openid", "profile", "cyc.api"]);
        widened.GetValueOrThrow().GrantedAt.ShouldBe((await consent.GetAsync()).GetValueOrThrow().GrantedAt, "the first grant's time is kept");
    }

    [Fact]
    public async Task RevokingEmptiesTheRecordAndTheNextGrantStartsFromScratch() {
        var user = Guid.NewGuid();
        var consent = cluster.Consent(user, "crm");

        (await consent.GrantAsync(user, "crm", ["openid", "profile", "cyc.api"])).IsSuccess.ShouldBeTrue();
        (await consent.RevokeAsync()).IsSuccess.ShouldBeTrue();

        (await consent.GetAsync()).IsFailure.ShouldBeTrue("a revoked consent still answered as on record");

        var again = await consent.GrantAsync(user, "crm", ["openid"]);

        again.GetValueOrThrow().Scopes.ShouldBe(["openid"], "scopes allowed before the revocation came back with it");

        // Revoking what is not there is nothing, not an error.
        (await cluster.Consent(Guid.NewGuid(), "erp").RevokeAsync()).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task APairOtherThanTheKeysIsRefusedNotRecorded() {
        // ⚠ The key is a digest of the person and the client, so the grain cannot read them back;
        // the first grant fixes them and a caller who built the key from one pair and the arguments
        // from another is refused rather than written.
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var consent = cluster.Consent(alice, "crm");

        (await consent.GrantAsync(alice, "crm", ["openid"])).IsSuccess.ShouldBeTrue();

        var otherPerson = await consent.GrantAsync(bob, "crm", ["openid", "cyc.api"]);
        var otherClient = await consent.GrantAsync(alice, "erp", ["openid", "cyc.api"]);

        otherPerson.IsFailure.ShouldBeTrue();
        otherPerson.Error!.Code.ShouldBe(ErrorCode.Conflict);
        otherClient.IsFailure.ShouldBeTrue();
        (await consent.GetAsync()).GetValueOrThrow().Scopes.ShouldBe(["openid"], "a refused grant widened the record");

        // An empty pair is refused before anything is written.
        (await cluster.Consent(alice, "erp").GrantAsync(Guid.Empty, "erp", ["openid"])).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task ConsentIsPerPersonPerClientPerTenant() {
        var user = Guid.NewGuid();

        (await cluster.Consent(user, "crm").GrantAsync(user, "crm", ["openid"])).IsSuccess.ShouldBeTrue();

        (await cluster.Consent(user, "erp").GetAsync()).IsFailure.ShouldBeTrue("consent to one client answered for another");
        (await cluster.Consent(Guid.NewGuid(), "crm").GetAsync()).IsFailure.ShouldBeTrue("one person's consent answered for another");
        (await cluster.Consent(user, "crm", IdentityCluster.OtherTenant).GetAsync()).IsFailure.ShouldBeTrue("a consent crossed tenants");
    }
}
