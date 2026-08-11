using CyberCloud.Core.Contracts.Serialization;
using CyberCloud.Kubernetes.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using System.Collections.Immutable;

namespace CyberCloud.ResourceManager.Contracts.Tests;

/// <summary>
///     Every resource-manager wire type through a real Orleans <see cref="Serializer" />, as bytes.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Not a hand-rolled round trip, and this is the one thing the grain suite cannot
///         check.</b> <c>CyberCloud.ResourceManager.Tests</c> runs against in-memory grain storage
///         (see its <c>.csproj</c> on why), which keeps the object graph rather than serializing it —
///         so a type with a missing codec, a member Orleans cannot round-trip, or an
///         <c>ImmutableArray</c> that comes back default would pass every test there and fail on the
///         first real silo. Putting the bytes through the type manifest here is what covers it.
///     </para>
///     <para>
///         The same argument, and the same shape, as
///         <c>CyberCloud.Tenancy.Contracts.Tests.TenancySerializationTests</c>.
///     </para>
/// </remarks>
public sealed class ResourceManagerSerializationTests : IDisposable {
    readonly ServiceProvider provider;
    readonly Serializer serializer;

    /// <summary>Builds a serializer over every contract assembly a silo would load.</summary>
    public ResourceManagerSerializationTests() {
        var services = new ServiceCollection();
        services.AddSerializer(builder => builder
            .AddAssembly(typeof(ResourceSnapshot).Assembly)
            .AddAssembly(typeof(ProvisioningState).Assembly)
            .AddAssembly(typeof(KubeCommand).Assembly)
            .AddAssembly(typeof(ResultSurrogate).Assembly)
        );

        provider = services.BuildServiceProvider();
        serializer = provider.GetRequiredService<Serializer>();
    }

    /// <inheritdoc />
    public void Dispose() => provider.Dispose();

    [Fact]
    public void AResourceSnapshotRoundTrips() {
        var value = new ResourceSnapshot {
            Id = Guid.Parse("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"),
            Path = "/tenants/…/providers/CyberCloud.Testing/widgets/main",
            Type = "CyberCloud.Testing/widgets",
            Name = "main",
            ApiVersion = "2026-08-01",
            ProvisioningState = ProvisioningState.Deleting,
            Properties = """{"location":"eu-central"}""",
            Tags = ImmutableDictionary<string, string>.Empty.Add("cost-centre", "eng"),
            Etag = "abc",
            Location = "eu-central",
            ClusterId = Guid.Parse("3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d"),
            CreatedBy = "user:alice@…",
            CreatedAt = DateTimeOffset.Parse("2026-01-02T03:04:05Z", null),
            ModifiedBy = "user:bob@…",
            ModifiedAt = DateTimeOffset.Parse("2026-02-03T04:05:06Z", null),
            LastFailure = "the API server refused the delete",
            OperationId = Guid.Parse("7f1c4a55-1111-4222-8333-444455556666"),
            Lock = LockLevel.CanNotDelete
        };

        var round = RoundTrip(value);

        // ⚠ Member-wise rather than `ShouldBe(value)`, and the reason is worth knowing: a record whose
        // members include an ImmutableArray or an ImmutableDictionary does NOT have value equality.
        // The compiler's synthesized Equals compares those members with THEIR Equals, which for both
        // is reference equality over the underlying storage — so a perfect round trip compares
        // unequal. `Error` hand-writes Equals with SequenceEqual for exactly this reason; these types
        // deliberately do not, because nothing in the write path compares them, and a hand-written
        // Equals nobody calls is a member that rots. The consequence for a caller is stated here so it
        // is not rediscovered as "serialization is broken".
        round.Id.ShouldBe(value.Id);
        round.Path.ShouldBe(value.Path);
        round.ProvisioningState.ShouldBe(value.ProvisioningState);
        round.Properties.ShouldBe(value.Properties);
        round.Tags["cost-centre"].ShouldBe("eng");
        round.Etag.ShouldBe(value.Etag);
        round.ClusterId.ShouldBe(value.ClusterId);
        round.CreatedAt.ShouldBe(value.CreatedAt);
        round.LastFailure.ShouldBe(value.LastFailure);
        round.Lock.ShouldBe(LockLevel.CanNotDelete);
    }

    [Fact]
    public void AnOperationSpecWithItsLeasesRoundTrips() {
        // ⚠ QuotaLeaseIds is what a resume cannot recompute, so an ImmutableArray that came back
        // default would be an operation that silently leaked its subscription's quota.
        var value = new OperationSpec {
            OperationId = Guid.NewGuid(),
            Kind = OperationKind.Create,
            ResourcePath = "/tenants/…/widgets/main",
            ResourceId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            SubscriptionId = Guid.NewGuid(),
            ApiVersion = "2026-08-01",
            Desired = """{"location":"eu-central"}""",
            QuotaLeaseIds = [Guid.NewGuid(), Guid.NewGuid()],
            IndexClaimed = true,
            Caller = new() { TenantId = Guid.NewGuid(), SubjectType = "user", SubjectId = "alice" },
            ParentOperationId = Guid.NewGuid()
        };

        var round = RoundTrip(value);

        round.OperationId.ShouldBe(value.OperationId);
        round.Kind.ShouldBe(OperationKind.Create);
        round.ResourcePath.ShouldBe(value.ResourcePath);
        round.Desired.ShouldBe(value.Desired);
        round.IndexClaimed.ShouldBeTrue();
        round.ParentOperationId.ShouldBe(value.ParentOperationId);
        round.Caller.SubjectId.ShouldBe("alice");
        round.QuotaLeaseIds.IsDefault.ShouldBeFalse();
        round.QuotaLeaseIds.ShouldBe(value.QuotaLeaseIds);
    }

    [Fact]
    public void AnOperationStatusWithItsProgressArrayRoundTrips() {
        var value = new OperationStatus {
            OperationId = Guid.NewGuid(),
            State = OperationState.Running,
            ResourcePath = "/tenants/…/widgets/main",
            StartedAt = DateTimeOffset.Parse("2026-08-11T12:00:00Z", null),
            Progress = [
                new() { At = DateTimeOffset.Parse("2026-08-11T12:00:01Z", null), Step = "applying", Detail = "1 of 3" },
                new() {
                    At = DateTimeOffset.Parse("2026-08-11T12:00:02Z", null),
                    Step = "waiting",
                    Detail = "2 of 3 replicas ready",
                    PercentComplete = 66
                }
            ],
            PercentComplete = 66,
            CancelRequested = true,
            CancelReason = "the user changed their mind",
            Attempts = 3,
            Activations = 2,
            Children = [Guid.NewGuid()]
        };

        var round = RoundTrip(value);

        round.State.ShouldBe(OperationState.Running);
        round.CancelRequested.ShouldBeTrue();
        round.CancelReason.ShouldBe(value.CancelReason);
        round.Attempts.ShouldBe(3);
        round.Activations.ShouldBe(2);
        round.Children.Length.ShouldBe(1);
        round.Progress.Length.ShouldBe(2);
        round.Progress[0].Step.ShouldBe("applying");
        round.LastProgress!.Detail.ShouldBe("2 of 3 replicas ready");
        round.LastProgress.PercentComplete.ShouldBe(66);
    }

    [Fact]
    public void EachOfReconcileOutcomesThreeCasesRoundTrips() {
        // ⚠ The private constructor means Orleans has to build these without one. A case that came
        // back as a different Kind would send the scheduler down the wrong branch.
        RoundTrip(ReconcileOutcome.Converged).Kind.ShouldBe(ReconcileOutcomeKind.Converged);

        var inProgress = RoundTrip(ReconcileOutcome.InProgress("2 of 3 replicas", TimeSpan.FromSeconds(15)));
        inProgress.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        inProgress.Reason.ShouldBe("2 of 3 replicas");
        inProgress.RetryAfter.ShouldBe(TimeSpan.FromSeconds(15));

        var failed = RoundTrip(
            ReconcileOutcome.Failed(new Error(ErrorCode.ProvisioningFailed, "rejected", "/properties/size"), true)
        );

        failed.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        failed.Retryable.ShouldBeTrue();
        failed.Error!.Code.ShouldBe(ErrorCode.ProvisioningFailed);
        failed.Error.Target.ShouldBe("/properties/size");
    }

    [Fact]
    public void AResourceChangedEventRoundTripsWithItsTagMap() {
        var value = new ResourceChangedEvent {
            Change = ResourceChangeKind.Created,
            ResourceId = Guid.NewGuid(),
            TenantId = Guid.Parse("11111111-1111-4111-8111-111111111111"),
            SubscriptionId = Guid.NewGuid(),
            ResourceGroup = "prod",
            Provider = "CyberCloud.Testing",
            Type = "widgets",
            Name = "main",
            ApiVersion = "2026-08-01",
            ProvisioningState = ProvisioningState.Creating,
            Location = "eu-central",
            ClusterId = Guid.NewGuid(),
            Tags = ImmutableDictionary<string, string>.Empty.Add("env", "prod"),
            CreatedAt = DateTimeOffset.Parse("2026-08-11T12:00:00Z", null),
            ModifiedAt = DateTimeOffset.Parse("2026-08-11T12:00:00Z", null),
            DesiredHash = "sha256:deadbeef",
            Version = 3
        };

        var round = RoundTrip(value);

        round.Change.ShouldBe(ResourceChangeKind.Created);
        round.ResourceId.ShouldBe(value.ResourceId);
        round.Provider.ShouldBe("CyberCloud.Testing");
        round.ProvisioningState.ShouldBe(ProvisioningState.Creating);
        round.DesiredHash.ShouldBe("sha256:deadbeef");
        round.Version.ShouldBe(3);
        round.Tags["env"].ShouldBe("prod");
        round.StreamNamespace.ShouldBe("cc.11111111111141118111111111111111.res");
    }

    [Fact]
    public void AWriteTraceRoundTripsAndStaysCanonical() {
        var value = new WriteTrace { Reached = WriteTrace.Canonical };
        var round = RoundTrip(value);

        round.Reached.ShouldBe(WriteTrace.Canonical);
        round.IsCanonicalPrefix().ShouldBeTrue();
        round.StoppedAt.ShouldBe(WriteStep.Accepted);
    }

    [Fact]
    public void AnObservedStateRoundTrips() {
        var value = new ObservedState {
            Exists = true,
            Json = """{"replicas":3}""",
            ObservedAt = DateTimeOffset.Parse("2026-08-11T12:00:00Z", null),
            Revision = "12345",
            ReconcileHash = "sha256:deadbeef",
            Summary = "3 of 3 replicas ready"
        };

        RoundTrip(value).ShouldBe(value);
    }

    [Fact]
    public void ADesiredSubmissionRoundTripsWithItsDeclaredPointers() {
        var value = new DesiredSubmission {
            Path = "/tenants/…/widgets/main",
            ApiVersion = "2026-08-01",
            Body = """{"location":"eu-central"}""",
            Verb = WriteVerb.Patch,
            OperationId = Guid.NewGuid(),
            IfMatch = "abc",
            Caller = new() { SubjectId = "alice" },
            Tags = ImmutableDictionary<string, string>.Empty.Add("a", "b"),
            Location = "eu-central",
            ClusterId = Guid.NewGuid(),
            DeclaredPointers = ["/location", "/properties/size"]
        };

        var round = RoundTrip(value);

        round.Verb.ShouldBe(WriteVerb.Patch);
        round.Body.ShouldBe(value.Body);
        round.IfMatch.ShouldBe("abc");
        round.ClusterId.ShouldBe(value.ClusterId);
        round.Tags["a"].ShouldBe("b");
        round.DeclaredPointers.IsDefault.ShouldBeFalse();
        round.DeclaredPointers.ShouldBe(value.DeclaredPointers);
    }

    [Fact]
    public void ADriftReportRoundTripsWithItsFindings() {
        var value = new DriftReport {
            ClusterId = Guid.NewGuid(),
            ScannedAt = DateTimeOffset.Parse("2026-08-11T12:00:00Z", null),
            ObjectsSeen = 12,
            ResourcesSeen = 10,
            Findings = [
                new() {
                    Kind = DriftKind.Orphan,
                    ResourceId = Guid.NewGuid(),
                    ResourcePath = "/tenants/…/widgets/ghost",
                    Objects = [
                        new() {
                            Kind = new() { Group = "apps", Version = "v1", Kind = "Deployment", Plural = "deployments" },
                            Namespace = "ns",
                            Name = "ghost"
                        }
                    ],
                    Detail = "running and unmetered"
                }
            ]
        };

        var round = RoundTrip(value);

        round.Findings.Length.ShouldBe(1);
        round.Findings[0].Kind.ShouldBe(DriftKind.Orphan);
        round.Findings[0].Objects.Length.ShouldBe(1);
        round.Orphans.Count().ShouldBe(1);
    }

    [Fact]
    public void ASecretRefRoundTripsAndCarriesNoValue() {
        // ⚠ The absence is the property — docs/plan/00 § Non-negotiables, "secrets never reach grain
        // state". A SecretRef is an address, and there is no member a value could ride in.
        var value = new SecretRef { Path = "tenants/x/postgres/main", Field = "adminPassword", Version = "3" };

        RoundTrip(value).ShouldBe(value);

        typeof(SecretRef)
            .GetProperties()
            .Select(x => x.Name)
            .ShouldBe(["Path", "Field", "Version"], ignoreOrder: true);
    }

    [Fact]
    public void EveryWireTypeInThisAssemblyCarriesAStableAlias() {
        // docs/plan/04 § Failure and upgrade: an [Alias] pins the name a peer looks the type up by, so
        // the CLR type can be renamed or moved without a wire break. CC1003 enforces this at build
        // time; this is the belt to its braces, and it also covers the enums, which CC1003 does not
        // reach because they carry no [GenerateSerializer].
        var offenders = typeof(ResourceSnapshot).Assembly
            .GetTypes()
            .Where(x => x.IsPublic && (x.IsEnum || x.GetCustomAttributes(typeof(GenerateSerializerAttribute), false).Length > 0))
            .Where(x => x.GetCustomAttributes(typeof(AliasAttribute), false).Length == 0)
            .Select(x => x.FullName)
            .ToArray();

        offenders.ShouldBeEmpty();
    }

    T RoundTrip<T>(T value) => serializer.Deserialize<T>(serializer.SerializeToArray(value));
}
