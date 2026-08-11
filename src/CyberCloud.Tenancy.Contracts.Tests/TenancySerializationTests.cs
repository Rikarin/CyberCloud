using CyberCloud.Core;
using CyberCloud.Core.Contracts.Serialization;
using CyberCloud.Core.Resources;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Shouldly;

namespace CyberCloud.Tenancy.Contracts.Tests;

/// <summary>
///     Every tenancy wire type through a real Orleans <see cref="Serializer" />, as bytes.
/// </summary>
/// <remarks>
///     ⚠ Not a hand-rolled round trip. docs/plan/04 § Failure and upgrade makes the serializer the
///     thing a rolling upgrade depends on, and the failure modes that matter — an enum with no codec,
///     a <c>ResourceId</c> whose surrogate lives in a <i>different</i> assembly, an
///     <c>IReadOnlyList</c> member that Orleans cannot round-trip — are all invisible unless the
///     bytes actually go through the type manifest.
/// </remarks>
public sealed class TenancySerializationTests : IDisposable {
    readonly ServiceProvider provider;
    readonly Serializer serializer;

    /// <summary>Builds a serializer over both contract assemblies, the way a silo does.</summary>
    public TenancySerializationTests() {
        var services = new ServiceCollection();
        services.AddSerializer(builder => builder
            .AddAssembly(typeof(TenantDescriptor).Assembly)
            .AddAssembly(typeof(ResultSurrogate).Assembly)
        );

        provider = services.BuildServiceProvider();
        serializer = provider.GetRequiredService<Serializer>();
    }

    /// <inheritdoc />
    public void Dispose() => provider.Dispose();

    [Fact]
    public void ATenantDescriptorRoundTrips() {
        var value = new TenantDescriptor {
            Id = Guid.Parse("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"),
            Slug = "contoso",
            DisplayName = "Contoso Ltd",
            HomeRegion = "eu-central",
            Status = TenantStatus.Suspended,
            CreatedAt = DateTimeOffset.Parse("2026-01-02T03:04:05Z", null),
            ModifiedAt = DateTimeOffset.Parse("2026-02-03T04:05:06Z", null),
            Version = 42
        };

        RoundTrip(value).ShouldBe(value);
    }

    [Fact]
    public void ADirectoryDeltaWithEntriesRoundTrips() {
        var value = new TenantDirectoryDelta {
            Version = 7,
            IsFullSnapshot = true,
            Entries = [
                new() {
                    TenantId = Guid.NewGuid(),
                    Slug = "a",
                    HomeRegion = "us-east",
                    HotShard = "cc:t:abc",
                    DurableShard = "durable-03",
                    Status = TenantStatus.Active,
                    DirectoryVersion = 7
                }
            ]
        };

        var back = RoundTrip(value);

        back.Version.ShouldBe(7);
        back.IsFullSnapshot.ShouldBeTrue();
        back.Entries.Count.ShouldBe(1);
        back.Entries[0].ShouldBe(value.Entries[0]);
    }

    [Fact]
    public void AShardMapSnapshotRoundTrips() {
        var value = new ShardMapSnapshot {
            Version = 3,
            DurableShards = ["durable-00", "durable-01"],
            Assignments = [
                new() {
                    TenantId = Guid.NewGuid(),
                    DurableShard = "durable-01",
                    HotHashTag = "cc:t:x",
                    Region = "eu-central",
                    AssignedAt = DateTimeOffset.UnixEpoch,
                    Version = 3
                }
            ]
        };

        var back = RoundTrip(value);

        back.DurableShards.ShouldBe(value.DurableShards);
        back.Assignments[0].ShouldBe(value.Assignments[0]);
    }

    [Fact]
    public void AQuotaLeaseRoundTripsIncludingItsDecimal() {
        // decimal is the one primitive here whose codec is routinely got wrong, and a quota figure
        // that came back as a double would be a billing figure that does not add up.
        var value = new QuotaLease {
            LeaseId = Guid.NewGuid(),
            SubscriptionId = Guid.NewGuid(),
            Meter = QuotaMeter.StorageGb,
            Amount = 1234.5678m,
            OperationId = Guid.NewGuid(),
            ReservedAt = DateTimeOffset.UnixEpoch,
            ExpiresAt = DateTimeOffset.UnixEpoch.AddHours(1)
        };

        var back = RoundTrip(value);

        back.ShouldBe(value);
        back.Amount.ShouldBe(1234.5678m);
    }

    [Fact]
    public void AQuotaUsageRoundTrips() {
        var value = new QuotaUsage { Meter = QuotaMeter.Vcpu, Committed = 3m, Reserved = 2m, Limit = 100m };

        RoundTrip(value).ShouldBe(value);
    }

    [Fact]
    public void AnIndexEntryRoundTrips() {
        var value = new IndexEntry {
            State = IndexEntryState.Confirmed,
            BoundTo = Guid.NewGuid(),
            IndexedValue = "/tenants/x/subscriptions/y/resourcegroups/prod",
            LeaseExpiresAt = DateTimeOffset.MaxValue,
            ModifiedAt = DateTimeOffset.UnixEpoch
        };

        RoundTrip(value).ShouldBe(value);
    }

    [Fact]
    public void AResourceGroupMemberRoundTrips() {
        var value = new ResourceGroupMember {
            ResourceId = Guid.NewGuid(),
            CanonicalPath = "/tenants/x/subscriptions/y/resourcegroups/prod/providers/p/t/n",
            State = ProvisioningState.Deleting,
            LastFailure = "the cluster refused the delete",
            TeardownAttempts = 3
        };

        RoundTrip(value).ShouldBe(value);
    }

    [Fact]
    public void ASubscriptionDescriptorRoundTripsItsGroupList() {
        var value = new SubscriptionDescriptor {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            DisplayName = "prod",
            State = ProvisioningState.Succeeded,
            ResourceGroups = ["a", "b", "c"],
            CreatedAt = DateTimeOffset.UnixEpoch,
            Version = 2
        };

        RoundTrip(value).ResourceGroups.ShouldBe(value.ResourceGroups);
    }

    [Fact]
    public void AResourceGroupDescriptorRoundTrips() {
        var value = new ResourceGroupDescriptor {
            Name = "prod",
            SubscriptionId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Region = "eu-central",
            State = ProvisioningState.Succeeded,
            CreatedAt = DateTimeOffset.UnixEpoch,
            Version = 1
        };

        RoundTrip(value).ShouldBe(value);
    }

    [Theory]
    [InlineData(TenantStatus.Provisioning)]
    [InlineData(TenantStatus.Active)]
    [InlineData(TenantStatus.Warned)]
    [InlineData(TenantStatus.Suspended)]
    [InlineData(TenantStatus.Disabled)]
    [InlineData(TenantStatus.PendingDeletion)]
    [InlineData(TenantStatus.Purged)]
    public void EveryTenantStatusRoundTrips(TenantStatus status) =>
        // Enums carry no [GenerateSerializer] here; this is the assertion that Orleans' built-in
        // enum codec really does cover them, rather than the assumption that it does.
        RoundTrip(status).ShouldBe(status);

    [Fact]
    public void AResultOfATenancyTypeRoundTripsThroughTheCoreSurrogate() {
        // The cross-assembly case: Result<T>'s surrogate is in CyberCloud.Core.Contracts and T is
        // here. A generic instantiation across two assemblies is the shape most likely not to work,
        // and it is the shape every grain method in this domain returns.
        var value = Result<TenantDescriptor>.Success(
            new() { Id = Guid.NewGuid(), Slug = "s", HomeRegion = "r", Status = TenantStatus.Active }
        );

        var back = RoundTrip(value);

        back.IsSuccess.ShouldBeTrue();
        back.GetValueOrThrow().Slug.ShouldBe("s");
    }

    [Fact]
    public void AFailedResultOfATenancyTypeCarriesItsErrorCode() {
        var value = Result<ShardAssignment>.Failure(
            ErrorCode.TenantNotFound,
            "Tenant has never been assigned a shard."
        );

        var back = RoundTrip(value);

        back.IsFailure.ShouldBeTrue();
        back.Error!.Code.ShouldBe(ErrorCode.TenantNotFound);
    }

    [Fact]
    public void AResourceIdTravelsAsAGrainArgument() {
        // IResourceGroupGrain.BeginCreateAsync and IResourceIndexGrain.TryClaimAsync both take one,
        // and its surrogate is in the other contracts assembly.
        var value = new ResourceId(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "prod",
            new("CyberCloud.DBforPostgreSQL", "servers"),
            "orders-db",
            Guid.NewGuid()
        );

        RoundTrip(value).ShouldBe(value);
    }

    T RoundTrip<T>(T value) => serializer.Deserialize<T>(serializer.SerializeToArray(value));
}
