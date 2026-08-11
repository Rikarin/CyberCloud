using CyberCloud.Core;
using CyberCloud.Core.Resources;
using Shouldly;

namespace CyberCloud.Core.Contracts.Tests;

/// <summary>
///     <see cref="ResourceId" />, <see cref="ResourceKey" /> and <see cref="ResourceTypeName" />
///     through Orleans' own serializer.
/// </summary>
public sealed class IdentifierSerializationTests(OrleansSerializerFixture orleans)
    : IClassFixture<OrleansSerializerFixture>
{
    static readonly Guid TenantId = Guid.Parse("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3");
    static readonly Guid SubscriptionId = Guid.Parse("6f0f1f0e-1234-4c8b-9a3d-aabbccddeeff");
    static readonly Guid ResourceGuid = Guid.Parse("11112222-3333-4444-5555-666677778888");

    // ── ResourceTypeName ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void AResourceTypeNameRoundTrips()
    {
        var original = new ResourceTypeName("CyberCloud.DBforPostgreSQL", "servers");
        var round = orleans.RoundTrip(original);

        round.ShouldBe(original);
        round.Namespace.ShouldBe("CyberCloud.DBforPostgreSQL");
        round.Type.ShouldBe("servers");
    }

    [Fact]
    public void ANestedResourceTypeNameRoundTrips()
    {
        var original = new ResourceTypeName("CyberCloud.DBforPostgreSQL", "servers/databases");

        orleans.RoundTrip(original).ShouldBe(original);
        orleans.RoundTrip(original).Depth.ShouldBe(2);
    }

    [Fact]
    public void ResourceTypeNamePreservesCasingRatherThanCanonicalising()
    {
        // Equality on this type is case-insensitive, so ShouldBe would pass even if the wire folded
        // the case. The portal and the CLI display Namespace/Type verbatim, so assert the strings.
        var round = orleans.RoundTrip(new ResourceTypeName("CyberCloud.Compute", "virtualMachines"));

        round.Namespace.ShouldBe("CyberCloud.Compute");
        round.Type.ShouldBe("virtualMachines");
    }

    [Fact]
    public void DefaultResourceTypeNameRoundTripsWithoutThrowing()
    {
        var round = orleans.RoundTrip(default(ResourceTypeName));

        round.IsEmpty.ShouldBeTrue();
        round.ShouldBe(default(ResourceTypeName));
    }

    // ── ResourceId ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AResolvedResourceIdRoundTrips()
    {
        var original = new ResourceId(
            TenantId,
            SubscriptionId,
            "prod",
            new ResourceTypeName("CyberCloud.DBforPostgreSQL", "servers"),
            "orders-db",
            ResourceGuid);

        var round = orleans.RoundTrip(original);

        round.ShouldBe(original);
        round.Path.ShouldBe(original.Path);
        round.Id.ShouldBe(ResourceGuid);
    }

    [Fact]
    public void AnUnresolvedResourceIdKeepsItsEmptyGuid()
    {
        // docs/plan/06:44 — an id parsed from a path carries Guid.Empty until the index resolves it.
        // Guid.Empty here is a value, not an absence, and must not be confused with default(ResourceId).
        ResourceId.TryParsePath(
                "/tenants/2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"
                + "/subscriptions/6f0f1f0e-1234-4c8b-9a3d-aabbccddeeff"
                + "/resourceGroups/prod/providers/CyberCloud.DBforPostgreSQL/servers/orders-db",
                out var parsed)
            .ShouldBeTrue();

        var round = orleans.RoundTrip(parsed);

        round.ShouldBe(parsed);
        round.Id.ShouldBe(Guid.Empty);
        round.Name.ShouldBe("orders-db");
    }

    [Fact]
    public void ANestedTypeResourceIdRoundTrips()
    {
        var original = new ResourceId(
            TenantId,
            SubscriptionId,
            "prod",
            new ResourceTypeName("CyberCloud.DBforPostgreSQL", "servers/databases"),
            "orders",
            ResourceGuid);

        orleans.RoundTrip(original).Path.ShouldBe(original.Path);
    }

    [Fact]
    public void DefaultResourceIdRoundTripsWithoutThrowing()
    {
        // ResourceId's constructor rejects the null name that default() implies, so the surrogate
        // has to recognise the empty payload. Without this, every failed Result<ResourceId> would
        // throw on deserialization instead of arriving as a failure.
        orleans.RoundTrip(default(ResourceId)).ShouldBe(default(ResourceId));
    }

    // ── ResourceKey ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AResourceKeyRoundTripsToTheSameGrainKey()
    {
        var original = new ResourceKey(
            SubscriptionId,
            "prod",
            new ResourceTypeName("CyberCloud.DBforPostgreSQL", "servers"),
            ResourceGuid);

        var round = orleans.RoundTrip(original);

        round.ShouldBe(original);

        // The assertion that matters: the same grain, not merely an equal-looking record. A key
        // that round-trips into a different string addresses a different activation.
        round.ToKeyWithinTenant().ShouldBe(original.ToKeyWithinTenant());
        ResourceKey.IsTenantQualificationSafe(round.ToKeyWithinTenant()).ShouldBeTrue();
    }

    [Fact]
    public void DefaultResourceKeyRoundTripsWithoutThrowing() =>
        orleans.RoundTrip(default(ResourceKey)).ShouldBe(default(ResourceKey));

    [Fact]
    public void AResourceKeyDerivedFromAResourceIdSurvivesTheWire()
    {
        var id = new ResourceId(
            TenantId,
            SubscriptionId,
            "prod",
            new ResourceTypeName("CyberCloud.DBforPostgreSQL", "servers"),
            "orders-db",
            ResourceGuid);

        var expected = ResourceKey.From(id).ToKeyWithinTenant();

        ResourceKey.From(orleans.RoundTrip(id)).ToKeyWithinTenant().ShouldBe(expected);
    }

    // ── Errors carrying identifiers ────────────────────────────────────────────────────────────

    [Fact]
    public void AResultHoldingAResourceKeyRoundTrips()
    {
        var original = Result<ResourceKey>.Success(
            new ResourceKey(
                SubscriptionId,
                "prod",
                new ResourceTypeName("CyberCloud.Compute", "virtualMachines"),
                ResourceGuid));

        orleans.RoundTrip(original).ShouldBe(original);
    }

    [Fact]
    public void AParseFailureRoundTripsAsAFailure()
    {
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
