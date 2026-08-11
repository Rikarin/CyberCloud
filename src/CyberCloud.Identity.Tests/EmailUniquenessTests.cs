using CyberCloud.Core.Resources;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Tests.Infrastructure;

namespace CyberCloud.Identity.Tests;

/// <summary>
///     docs/plan/11 § Sign-up and tenant creation: <i>"a user belongs to exactly one tenant"</i> and
///     <i>"email uniqueness is per tenant, enforced by <c>IEmailIndexGrain</c> keyed by
///     <c>hash(tenantId + normalized email)</c>"</i>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Both directions are asserted, because getting one right and the other wrong is easy
///         and each failure is expensive in a different way.</b> Global uniqueness would make the
///         same person unable to be a customer of two tenants — and would need a global index on the
///         sign-up path, which is precisely what docs/plan/05 § The two tiers is arranged to avoid.
///         No uniqueness at all would let two accounts in one tenant claim one address, which makes
///         "sign in with your email" ambiguous and password reset dangerous.
///     </para>
///     <para>
///         These run against the <b>real</b> <c>EmailIndexGrain</c> from <c>CyberCloud.Tenancy</c>
///         rather than a double. docs/plan/11 names that grain, and re-implementing the claim inside
///         identity would be a second uniqueness mechanism over the same digest — the one place a
///         duplicate is a correctness bug rather than a cosmetic one.
///     </para>
/// </remarks>
[Collection(IdentitySuite.Name)]
public sealed class EmailUniquenessTests(IdentityCluster cluster) {
    [Fact]
    public async Task TheSameAddressInTwoTenantsIsTwoUsersWithTwoIds() {
        const string email = "same-human@example.com";

        var inA = await cluster.CreateUserAsync(email, tenant: IdentityCluster.Tenant);
        var inB = await cluster.CreateUserAsync(email, tenant: IdentityCluster.OtherTenant);

        inA.ShouldNotBe(inB);

        var profileA = (await cluster.User(inA, IdentityCluster.Tenant).GetAsync()).GetValueOrThrow();
        var profileB = (await cluster.User(inB, IdentityCluster.OtherTenant).GetAsync()).GetValueOrThrow();

        profileA.Email.ShouldBe(profileB.Email);
        profileA.TenantId.ShouldBe(IdentityCluster.Tenant);
        profileB.TenantId.ShouldBe(IdentityCluster.OtherTenant);

        // ⚠ Two GUIDs, two tenants, one address — and no global index anywhere that could have told
        // us they are the same human. That is the M1 answer to Azure's guest-user problem, and the
        // portal's account switcher is a client-side list of tokens.
        profileA.UserId.ShouldNotBe(profileB.UserId);
    }

    [Fact]
    public async Task TheSameAddressTwiceInOneTenantIsAConflict() {
        const string email = "taken@example.com";

        await cluster.CreateUserAsync(email);

        var second = await cluster.EmailIndex(email).TryClaimAsync(email, Guid.NewGuid());

        second.IsFailure.ShouldBeTrue("a confirmed address in a tenant cannot be claimed again");
        // ⚠ ResourceAlreadyExists, not Conflict. docs/plan/06 § Two-phase create makes a taken
        // name the 409 that IResourceIndexGrain and IEmailIndexGrain both answer with, and the
        // two codes render to the same status but mean different things to a caller: Conflict is
        // "somebody else changed this under you", ResourceAlreadyExists is "pick another name".
        second.Error!.Code.ShouldBe(ErrorCode.ResourceAlreadyExists);
    }

    [Fact]
    public void TheIndexKeyCarriesTheTenantSoTwoTenantsCannotShareAnEntry() {
        var a = GrainKeys.EmailIndex(IdentityCluster.Tenant, "shared@example.com");
        var b = GrainKeys.EmailIndex(IdentityCluster.OtherTenant, "shared@example.com");

        a.ShouldNotBe(b);
        a.ShouldStartWith(GrainKeys.EmailIndexPrefix);
        b.ShouldStartWith(GrainKeys.EmailIndexPrefix);
    }

    [Fact]
    public async Task CaseDiffersButTheAccountDoesNot() {
        const string canonical = "mixed.case@example.com";

        var userId = await cluster.CreateUserAsync("Mixed.Case@Example.COM");

        // The stored address is the normalized one, so the account is findable by its own email —
        // storing a differently-normalized form than went into the digest is how an account becomes
        // unfindable.
        var profile = (await cluster.User(userId).GetAsync()).GetValueOrThrow();
        profile.Email.ShouldBe(canonical);

        var resolved = await cluster.EmailIndex(canonical).ResolveAsync();
        resolved.GetValueOrThrow().ShouldBe(userId);
    }

    [Fact]
    public async Task TheKelvinSignDoesNotCollapseOntoK() {
        // ⚠ THE TRAP GrainKeys.NormalizeEmail EXISTS FOR. `ToLowerInvariant` maps U+212A KELVIN SIGN
        // onto 'k', so under that rule `aK@x` and `ak@x` become one key — one account silently
        // claiming another's identity at sign-up, indistinguishable from a legitimate duplicate. The
        // rule folds only A-Z, so these are two accounts.
        var kelvin = "aK@example.com";
        const string ascii = "ak@example.com";

        var first = await cluster.CreateUserAsync(kelvin);
        var second = await cluster.CreateUserAsync(ascii);

        first.ShouldNotBe(second);

        (await cluster.EmailIndex(kelvin).ResolveAsync()).GetValueOrThrow().ShouldBe(first);
        (await cluster.EmailIndex(ascii).ResolveAsync()).GetValueOrThrow().ShouldBe(second);
    }

    [Fact]
    public async Task AUserCannotBeReCreatedUnderADifferentAddress() {
        var userId = await cluster.CreateUserAsync("original@example.com");

        var reCreate = await cluster.User(userId).CreateAsync(
            "different@example.com",
            "Test User",
            UserStatus.Active
        );

        // ⚠ A conflict rather than a rename. Renaming has to move the index claim too, and doing it
        // silently here would leave the old claim held forever — the address would be permanently
        // unusable by anybody, including its owner.
        reCreate.IsFailure.ShouldBeTrue();
        reCreate.Error!.Code.ShouldBe(ErrorCode.Conflict);
    }

    [Fact]
    public async Task ReCreatingWithTheSameAddressIsANoOp() {
        const string email = "idempotent@example.com";
        var userId = await cluster.CreateUserAsync(email);

        // This is what makes the sign-up flow re-drivable after a silo loss between the index claim
        // and the user create — docs/plan/06 § Two-phase create's retried PUT, one level down.
        var again = await cluster.User(userId).CreateAsync(email, "Test User", UserStatus.Active);

        again.IsSuccess.ShouldBeTrue();
        again.GetValueOrThrow().UserId.ShouldBe(userId);
    }
}
