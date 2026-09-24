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
///         ⚠
///         <b>
///             Not a hand-rolled round trip, and this is the one thing the grain suite cannot
///             check.
///         </b> <c>CyberCloud.ResourceManager.Tests</c> runs against in-memory grain storage
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
        services.AddSerializer(static builder => builder
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
            Body = """{"location":"eu-central"}""",
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
            Lock = LockLevel.CanNotDelete,
            Version = 7
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
        round.Body.ShouldBe(value.Body);
        round.Tags["cost-centre"].ShouldBe("eng");
        round.Etag.ShouldBe(value.Etag);
        round.ClusterId.ShouldBe(value.ClusterId);
        round.CreatedAt.ShouldBe(value.CreatedAt);
        round.LastFailure.ShouldBe(value.LastFailure);
        // ⚠ The member appended at [Id(18)] for #54, and the one number the projection keys on: a
        // snapshot that came back at 0 would put every event under the version the first one took.
        round.Version.ShouldBe(7);
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
            Children = [Guid.NewGuid(), Guid.NewGuid()],
            ParentOperationId = Guid.NewGuid()
        };

        var round = RoundTrip(value);

        // ⚠ Both directions of #39's nesting. ParentOperationId is appended at [Id(14)] and Children
        // was on the wire empty until a deployment filled it; a child whose status came back with an
        // empty parent would be a child nothing can walk back from.
        round.ParentOperationId.ShouldBe(value.ParentOperationId);
        round.Children.ShouldBe(value.Children);
        round.State.ShouldBe(OperationState.Running);
        round.CancelRequested.ShouldBeTrue();
        round.CancelReason.ShouldBe(value.CancelReason);
        round.Attempts.ShouldBe(3);
        round.Activations.ShouldBe(2);
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

    /// <summary>
    ///     The three types the query half of #54 added — a request, a column and a page. Nothing
    ///     carries them over a wire today (<c>IResourceGraphQuery</c> is in-process at the gateway),
    ///     so the suite is what keeps them serializable until something does; the page's two
    ///     <see cref="ImmutableArray{T}" /> members, one of them of a record struct, are the exact
    ///     shape this suite exists to catch (#54 review).
    /// </summary>
    [Fact]
    public void AResourceGraphQueryRequestRoundTrips() {
        var value = new ResourceGraphQueryRequest {
            Query = "resources | where tags has 'prod' | project name",
            Caller = new() {
                TenantId = Guid.Parse("11111111-1111-4111-8111-111111111111"), SubjectType = "user", SubjectId = "alice"
            },
            Top = 25,
            Continuation = "25.0123456789abcdef"
        };

        var round = RoundTrip(value);

        round.Query.ShouldBe(value.Query);
        round.Caller.TenantId.ShouldBe(value.Caller.TenantId);
        round.Caller.SubjectId.ShouldBe("alice");
        round.Top.ShouldBe(25);
        round.Continuation.ShouldBe("25.0123456789abcdef");
        round.PageSize.ShouldBe(25);
    }

    [Fact]
    public void AResourceGraphQueryPageRoundTripsWithItsColumnsAndRows() {
        var value = new ResourceGraphQueryPage {
            Columns = [new("name", "string"), new("n", "long"), new("tags", "dynamic")],
            Rows = ["""{"name":"pg-main","n":1,"tags":{"env":"prod"}}""", """{"name":"pg-replica","n":2,"tags":{}}"""],
            Continuation = "2.0123456789abcdef"
        };

        var round = RoundTrip(value);

        round.Columns.IsDefault.ShouldBeFalse(
            "an ImmutableArray that comes back default is the failure this suite is for"
        );
        round.Columns.ShouldBe(value.Columns);
        round.Rows.IsDefault.ShouldBeFalse();
        round.Rows.ShouldBe(value.Rows);
        round.Continuation.ShouldBe("2.0123456789abcdef");
        round.HasMore.ShouldBeTrue();

        // And an empty last page, whose arrays must come back empty and not default.
        var last = RoundTrip(new ResourceGraphQueryPage());
        last.Columns.IsDefaultOrEmpty.ShouldBeTrue();
        last.Columns.IsDefault.ShouldBeFalse();
        last.Rows.IsDefault.ShouldBeFalse();
        last.HasMore.ShouldBeFalse();
    }

    [Fact]
    public void AResourceGraphColumnRoundTripsOnItsOwn() {
        var round = RoundTrip(new ResourceGraphColumn("createdAt", "datetime"));

        round.Name.ShouldBe("createdAt");
        round.Type.ShouldBe("datetime");
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
                            Kind = new() {
                                Group = "apps", Version = "v1", Kind = "Deployment", Plural = "deployments"
                            },
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
    public void ARoleAssignmentPageRoundTripsWithItsRows() {
        // ⚠ Not a defect today — IRoleAssignmentManager is gateway-side and a page never crosses a
        // grain boundary — but the type carries [GenerateSerializer] and an ImmutableArray, which is
        // exactly the pair this file's remarks say only a real serializer can vouch for. The day the
        // listing is served from a grain, this is the row that already says the page survives it.
        var value = new RoleAssignmentPage {
            Assignments = [
                new() {
                    Path =
                        "/tenants/t/subscriptions/s/providers/CyberCloud.Authorization/roleAssignments/owner-user-7f3c2a1e0b4d4f6a8c9d1e2f3a4b5c6d",
                    Name = "owner-user-7f3c2a1e0b4d4f6a8c9d1e2f3a4b5c6d",
                    Scope = "/tenants/t/subscriptions/s",
                    RoleDefinitionId = "owner",
                    PrincipalType = "user",
                    PrincipalId = "7f3c2a1e0b4d4f6a8c9d1e2f3a4b5c6d",
                    Inherited = true
                },
                new() {
                    Path =
                        "/tenants/t/subscriptions/s/resourceGroups/rg/providers/CyberCloud.Authorization/roleAssignments/reader-group-2b4a1c662e704a9d9d0a1f7ec1f1a4b3",
                    Name = "reader-group-2b4a1c662e704a9d9d0a1f7ec1f1a4b3",
                    Scope = "/tenants/t/subscriptions/s/resourceGroups/rg",
                    RoleDefinitionId = "reader",
                    PrincipalType = "group",
                    PrincipalId = "2b4a1c662e704a9d9d0a1f7ec1f1a4b3"
                }
            ],
            Continuation =
                "/tenants/t/subscriptions/s/resourceGroups/rg/providers/CyberCloud.Authorization/roleAssignments/reader-group-2b4a1c662e704a9d9d0a1f7ec1f1a4b3"
        };

        var round = RoundTrip(value);

        round.Assignments.IsDefault.ShouldBeFalse();
        round.Assignments.Length.ShouldBe(2);
        round.Assignments[0].ShouldBe(value.Assignments[0]);
        round.Assignments[0].Inherited.ShouldBeTrue();
        round.Assignments[1].ShouldBe(value.Assignments[1]);
        round.Assignments[1].Inherited.ShouldBeFalse();
        round.Continuation.ShouldBe(value.Continuation);
        round.HasMore.ShouldBeTrue();

        var request = new RoleAssignmentListRequest {
            Path = value.Assignments[1].Scope,
            Caller = new() { TenantId = Guid.NewGuid(), SubjectType = "user", SubjectId = "alice" },
            Top = 25,
            Continuation = value.Continuation
        };

        RoundTrip(request).ShouldBe(request);
    }

    [Fact]
    public void ASecretRefRoundTripsAndCarriesNoValue() {
        // ⚠ The absence is the property — docs/plan/00 § Non-negotiables, "secrets never reach grain
        // state". A SecretRef is an address, and there is no member a value could ride in.
        var value = new SecretRef { Path = "tenants/x/postgres/main", Field = "adminPassword", Version = "3" };

        RoundTrip(value).ShouldBe(value);

        typeof(SecretRef)
            .GetProperties()
            .Where(static x => x.SetMethod is not null)
            .Select(static x => x.Name)
            .ShouldBe(["Path", "Field", "Version"], true);

        new SecretRef().IsEmpty.ShouldBeTrue();
        new SecretRef { Path = "p" }.IsEmpty.ShouldBeTrue("an address with no field addresses nothing");
        value.IsEmpty.ShouldBeFalse();
    }

    /// <summary>
    ///     ⚠ <b>The move, asserted from the assembly that used to own it.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="SecretRef" /> is a platform-wide concept — docs/plan/00 § Non-negotiables
    ///         states the rule for the whole platform, not for the resource manager — and while it
    ///         lived here, obeying it cost a module either a dependency on this assembly (and through
    ///         it on the Kubernetes and tenancy contracts) or a copy.
    ///         <c>CyberCloud.Identity.Contracts</c> took the copy, and its remarks say so.
    ///     </para>
    ///     <para>
    ///         ⚠ The second assertion is the one that matters more. The <c>[Alias]</c> is what a peer
    ///         looks the type up by (docs/plan/04 § Failure and upgrade), so it records where the
    ///         concept was <i>published</i> and not where the file currently sits. Re-spelling it to
    ///         <c>CyberCloud.Core.SecretRef</c> would compile, pass a round-trip test, and fail to
    ///         deserialize every payload written before the move.
    ///     </para>
    /// </remarks>
    [Fact]
    public void SecretRefLivesInCoreContractsAndKeptTheAliasItWasPublishedUnder() {
        typeof(SecretRef).Assembly.GetName()
            .Name.ShouldBe(
                "CyberCloud.Core.Contracts",
                "a platform-wide concept in one module's contracts is a concept every other module "
                + "either takes a dependency for or re-declares"
            );

        typeof(SecretRef).GetCustomAttributes(typeof(AliasAttribute), false)
            .Cast<AliasAttribute>()
            .Single()
            .Alias.ShouldBe(
                "CyberCloud.ResourceManager.SecretRef",
                "an alias is a wire identifier; changing it on a move is a data-loss bug"
            );
    }

    [Fact]
    public void EveryWireTypeInThisAssemblyCarriesAStableAlias() {
        // docs/plan/04 § Failure and upgrade: an [Alias] pins the name a peer looks the type up by, so
        // the CLR type can be renamed or moved without a wire break. CC1003 enforces this at build
        // time; this is the belt to its braces, and it also covers the enums, which CC1003 does not
        // reach because they carry no [GenerateSerializer].
        var offenders = typeof(ResourceSnapshot).Assembly
            .GetTypes()
            .Where(static x => x.IsPublic
                && (x.IsEnum || x.GetCustomAttributes(typeof(GenerateSerializerAttribute), false).Length > 0)
            )
            .Where(static x => x.GetCustomAttributes(typeof(AliasAttribute), false).Length == 0)
            .Select(static x => x.FullName)
            .ToArray();

        offenders.ShouldBeEmpty();
    }

    T RoundTrip<T>(T value) => serializer.Deserialize<T>(serializer.SerializeToArray(value));
}
