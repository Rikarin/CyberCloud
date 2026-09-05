using CyberCloud.Core.Resources;
using Shouldly;

namespace CyberCloud.Core.Contracts.Tests;

/// <summary>
///     <see cref="ResourceId" /> and <see cref="ResourceTypeName" /> through Orleans' own
///     serializer.
/// </summary>
public sealed class IdentifierSerializationTests(OrleansSerializerFixture orleans)
    : IClassFixture<OrleansSerializerFixture> {
    static readonly Guid TenantId = Guid.Parse("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3");
    static readonly Guid SubscriptionId = Guid.Parse("6f0f1f0e-1234-4c8b-9a3d-aabbccddeeff");
    static readonly Guid ResourceGuid = Guid.Parse("11112222-3333-4444-5555-666677778888");

    // ── ResourceTypeName ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void AResourceTypeNameRoundTrips() {
        var original = new ResourceTypeName("CyberCloud.DBforPostgreSQL", "servers");
        var round = orleans.RoundTrip(original);

        round.ShouldBe(original);
        round.Namespace.ShouldBe("CyberCloud.DBforPostgreSQL");
        round.Type.ShouldBe("servers");
    }

    [Fact]
    public void ANestedResourceTypeNameRoundTrips() {
        var original = new ResourceTypeName("CyberCloud.DBforPostgreSQL", "servers/databases");

        orleans.RoundTrip(original).ShouldBe(original);
        orleans.RoundTrip(original).Depth.ShouldBe(2);
    }

    [Fact]
    public void ResourceTypeNamePreservesCasingRatherThanCanonicalising() {
        // Equality on this type is case-insensitive, so ShouldBe would pass even if the wire folded
        // the case. The portal and the CLI display Namespace/Type verbatim, so assert the strings.
        var round = orleans.RoundTrip(new ResourceTypeName("CyberCloud.Compute", "virtualMachines"));

        round.Namespace.ShouldBe("CyberCloud.Compute");
        round.Type.ShouldBe("virtualMachines");
    }

    [Fact]
    public void DefaultResourceTypeNameRoundTripsWithoutThrowing() {
        var round = orleans.RoundTrip(default(ResourceTypeName));

        round.IsEmpty.ShouldBeTrue();
        round.ShouldBe(default);
    }

    // ── ResourceId ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AResolvedResourceIdRoundTrips() {
        var original = new ResourceId(
            TenantId,
            SubscriptionId,
            "prod",
            new("CyberCloud.DBforPostgreSQL", "servers"),
            "orders-db",
            ResourceGuid
        );

        var round = orleans.RoundTrip(original);

        round.ShouldBe(original);
        round.Path.ShouldBe(original.Path);
        round.Id.ShouldBe(ResourceGuid);
    }

    [Fact]
    public void AnUnresolvedResourceIdKeepsItsEmptyGuid() {
        // docs/plan/06 § Identifiers — an id parsed from a path carries Guid.Empty until the index resolves it.
        // Guid.Empty here is a value, not an absence, and must not be confused with default(ResourceId).
        ResourceId.TryParsePath(
                "/tenants/2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"
                + "/subscriptions/6f0f1f0e-1234-4c8b-9a3d-aabbccddeeff"
                + "/resourceGroups/prod/providers/CyberCloud.DBforPostgreSQL/servers/orders-db",
                out var parsed
            )
            .ShouldBeTrue();

        var round = orleans.RoundTrip(parsed);

        round.ShouldBe(parsed);
        round.Id.ShouldBe(Guid.Empty);
        round.Name.ShouldBe("orders-db");
    }

    [Fact]
    public void ANestedTypeResourceIdRoundTrips() {
        var original = new ResourceId(
            TenantId,
            SubscriptionId,
            "prod",
            new("CyberCloud.DBforPostgreSQL", "servers/databases"),
            "orders",
            ResourceGuid,
            "pg-main"
        );

        var round = orleans.RoundTrip(original);

        round.Path.ShouldBe(original.Path);

        // ⚠ The parent name is its own surrogate member, so assert it survives rather than trusting
        // the path comparison — a Path that dropped it would still be a legal path, one level
        // shallower, for a different resource.
        round.ParentNames.ShouldBe("pg-main");
        round.Parent.ShouldNotBeNull().Name.ShouldBe("pg-main");
    }

    [Fact]
    public void DefaultResourceIdRoundTripsWithoutThrowing() {
        // ResourceId's constructor rejects the null name that default() implies, so the surrogate
        // has to recognise the empty payload. Without this, every failed Result<ResourceId> would
        // throw on deserialization instead of arriving as a failure.
        orleans.RoundTrip(default(ResourceId)).ShouldBe(default);
    }

    // ── ResourceCollectionId — the address of a collection, which now travels ──────────────────
    //
    // ⚠ IT DID NOT TRAVEL UNTIL IParkedResourceRegistryGrain.ListOfTypeAsync, whose question is "what
    // is recoverable in this group, of this type" — docs/plan/08 § Soft delete, issue #71. So these
    // cases are the first thing standing between that grain call and a surrogate nobody exercised.

    [Fact]
    public void ATopLevelResourceCollectionIdRoundTrips() {
        var original = new ResourceCollectionId(
            TenantId,
            SubscriptionId,
            "prod",
            new("CyberCloud.DBforPostgreSQL", "servers")
        );

        var round = orleans.RoundTrip(original);

        round.ShouldBe(original);
        round.Path.ShouldBe(original.Path);
        round.ParentNames.ShouldBeEmpty();
    }

    [Fact]
    public void ANestedResourceCollectionIdKeepsTheAncestorNameThatMakesItAnAddress() {
        // ⚠ The ancestor name is what distinguishes one server's databases from another's, and it is
        // its own surrogate member — so assert it rather than trusting the Path comparison, which a
        // payload that dropped it would still satisfy one level shallower for a different collection.
        var original = new ResourceCollectionId(
            TenantId,
            SubscriptionId,
            "prod",
            new("CyberCloud.DBforPostgreSQL", "servers/databases"),
            "pg-main"
        );

        var round = orleans.RoundTrip(original);

        round.ShouldBe(original);
        round.ParentNames.ShouldBe("pg-main");
        round.Path.ShouldBe(original.Path);
    }

    [Fact]
    public void ACollectionAndTheResourceItWouldContainStayDistinctAcrossTheWire() {
        // The property ResourceCollectionId exists for: the two grammars partition every path, so a
        // collection must not arrive as a resource named after its own type. Both are round-tripped
        // and then re-parsed by the OTHER parser, which must refuse.
        var member = new ResourceId(
            TenantId,
            SubscriptionId,
            "prod",
            new("CyberCloud.DBforPostgreSQL", "servers"),
            "orders-db",
            ResourceGuid
        );

        var collection = orleans.RoundTrip(ResourceCollectionId.Of(member));

        collection.Member("orders-db").Path.ShouldBe(member.Path);
        ResourceId.TryParsePath(collection.Path, out _).ShouldBeFalse();
        ResourceCollectionId.TryParsePath(member.Path, out _).ShouldBeFalse();
    }

    [Fact]
    public void DefaultResourceCollectionIdRoundTripsWithoutThrowing() {
        // Same reason as default(ResourceId)'s: the constructor validates the group name and refuses
        // an empty type, and a failed Result<ResourceCollectionId> — what ParsePath returns for every
        // malformed path — holds default and writes it.
        orleans.RoundTrip(default(ResourceCollectionId)).ShouldBe(default);

        var failure = ResourceCollectionId.ParsePath("/nope");
        failure.IsFailure.ShouldBeTrue();

        var round = orleans.RoundTrip(failure);

        round.IsFailure.ShouldBeTrue();
        round.Error!.Code.ShouldBe(ErrorCode.InvalidResourceId);
        round.Error.Message.ShouldBe(failure.Error!.Message);
    }

    // ── Grain keys, which are derived on arrival and never sent ────────────────────────────────

    [Fact]
    public void AGrainKeyIsDerivedFromTheArrivedIdentifierAndNotCarriedAsAValue() {
        // ⚠ There is no grain-key surrogate, deliberately: ResourceKey and ResourceKeySurrogate were
        // deleted rather than ported when ADR-002 settled IResourceGrain on res/{resourceId:N}
        // (docs/plan/02 § ADR-002). A grain key travelling on the wire as a value is exactly the
        // coupling that removes — a peer on an older build would be sending an address it composed
        // itself. What crosses is the ResourceId; GrainKeys composes the key on the far side. This
        // test is that contract: same key either side, composed twice, never transmitted.
        var id = new ResourceId(
            TenantId,
            SubscriptionId,
            "prod",
            new("CyberCloud.DBforPostgreSQL", "servers"),
            "orders-db",
            ResourceGuid
        );

        var round = orleans.RoundTrip(id);

        GrainKeys.Resource(round.Id).ShouldBe(GrainKeys.Resource(id.Id));
        GrainKeys.PathIndex(round).ShouldBe(GrainKeys.PathIndex(id));
        GrainKeys.IsTenantQualificationSafe(GrainKeys.Resource(round.Id)).ShouldBeTrue();
    }

    // ── Errors carrying identifiers ────────────────────────────────────────────────────────────

    [Fact]
    public void AResultHoldingAResourceIdRoundTrips() {
        var original = Result<ResourceId>.Success(
            new(
                TenantId,
                SubscriptionId,
                "prod",
                new("CyberCloud.Compute", "virtualMachines"),
                "web-01",
                ResourceGuid
            )
        );

        orleans.RoundTrip(original).ShouldBe(original);
    }

    [Fact]
    public void AParseFailureRoundTripsAsAFailure() {
        // The real shape ResourceId.ParsePath returns: a failed Result<ResourceId> whose value slot
        // holds default(ResourceId) and whose error names the offending value.
        var original = ResourceId.ParsePath("/nope");
        original.IsFailure.ShouldBeTrue();

        var round = orleans.RoundTrip(original);

        round.IsFailure.ShouldBeTrue();
        round.Error!.Code.ShouldBe(ErrorCode.InvalidResourceId);
        round.Error.Message.ShouldBe(original.Error!.Message);
    }
}
