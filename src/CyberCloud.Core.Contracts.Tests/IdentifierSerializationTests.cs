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
        // docs/plan/06:44 — an id parsed from a path carries Guid.Empty until the index resolves it.
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
            ResourceGuid
        );

        orleans.RoundTrip(original).Path.ShouldBe(original.Path);
    }

    [Fact]
    public void DefaultResourceIdRoundTripsWithoutThrowing() {
        // ResourceId's constructor rejects the null name that default() implies, so the surrogate
        // has to recognise the empty payload. Without this, every failed Result<ResourceId> would
        // throw on deserialization instead of arriving as a failure.
        orleans.RoundTrip(default(ResourceId)).ShouldBe(default);
    }

    // ── Grain keys, which are derived on arrival and never sent ────────────────────────────────

    [Fact]
    public void AGrainKeyIsDerivedFromTheArrivedIdentifierAndNotCarriedAsAValue() {
        // ⚠ There is no grain-key surrogate, deliberately: ResourceKey and ResourceKeySurrogate were
        // deleted rather than ported when ADR-002 settled IResourceGrain on res/{resourceId:N}
        // (docs/plan/02:153-163). A grain key travelling on the wire as a value is exactly the
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
