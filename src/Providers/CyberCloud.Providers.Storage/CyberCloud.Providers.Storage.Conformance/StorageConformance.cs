using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Core.Resources;
using CyberCloud.Providers.Storage.Contracts;
using Shouldly;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Storage.Conformance;

/// <summary>
///     <c>CyberCloud.Storage/accounts</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             One case object and two class declarations, which is the fifth time that number has
///             held.
///         </b> What this one adds is the other end of the range: the Kafka and NATS cases proved
///         the suite could host a provider rendering five objects across three API groups; this one
///         renders <b>one</b>, and it needed no change either. Between them the shape is now bounded
///         on both sides rather than only above.
///     </para>
///     <para>
///         ⚠
///         <b>
///             <see cref="ProviderConformanceCase.Objects" /> is a <c>Seaweed</c> and a <c>Secret</c>,
///             and stopping there is not an under-declaration.
///         </b> The masters, the volume servers, the
///         filer, the S3 gateway, their Services and — when monitoring is on — four
///         <c>ServiceMonitor</c>s are all created by the <i>operator</i>, and this suite asserts what
///         the <i>reconciler</i> applied. Listing them here would make the suite fail against every
///         cluster that has no SeaweedFS operator installed, which is every cluster the Docker-free
///         half runs against.
///     </para>
///     <para>
///         ⚠ <b>The <c>Secret</c> is matched on PRESENCE and not on content, and that is forced.</b>
///         Every other <c>ObjectMatchesDesired</c> in the catalogue compares an object against the
///         desired <i>body</i>; this object's content is a credential, which is deliberately not in
///         the body and never will be. <see cref="StorageAccounts.Matches" /> checks that the
///         identities key is there — which is what a deleted, emptied or never-applied <c>Secret</c>
///         all fail, and all three are a gateway that comes up granting <c>ACTION_ADMIN</c> to
///         everybody.
///     </para>
///     <para>
///         ⚠ <b>The harness supplies a vault, and without one this whole case is red.</b>
///         <c>ProviderTestCluster</c> registers an <c>InMemorySecretVault</c> as both
///         <c>ISecretResolver</c> and <c>ISecretWriter</c>, shared between the silo and the client,
///         because the mint happens inside the silo and <c>listKeys</c> resolves outside it. It
///         implements mint-once for real, so the idempotence assertions still measure the reconciler.
///     </para>
///     <para>
///         ⚠
///         <b>
///             CORRECTED 2026-08-12 — docs/plan/15's <c>buckets</c> child type IS declared now, and
///             the claim this paragraph used to make was wrong within the hour.
///         </b> It said the blocker
///         was this file: that <see cref="ProviderConformanceCase" /> was single-type and that both
///         <c>ProviderTestCluster.Address</c> and <c>ClusterConformanceHarness.Address</c> built a
///         <see cref="ResourceId" /> with no <c>ParentNames</c>, so a depth-2 <c>Case.Type</c> threw
///         in the constructor. That was true when it was written and stopped being true the same day:
///         <c>IProviderCaseSource</c> gained a <c>static virtual Ancestors</c> composing the parent's
///         own case object, and the harness refuses a depth/count mismatch by name. See
///         <see cref="StorageBucketCase" /> — one member longer than this one, and nothing else.
///     </para>
/// </remarks>
public sealed class StorageCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Storage/accounts",
            CreateProvider = () => new StorageProvider(),
            ReconcilerType = typeof(StorageAccountReconciler),
            CreateReconciler = clock => new StorageAccountReconciler(clock),
            Type = StorageAccounts.Type,
            ApiVersion = StorageAccounts.V2026,
            Body = cluster => StorageAccounts.Body(cluster),
            // ⚠ Changes `volumeServers`, which is the property the rendered object carries in TWO
            // places — `spec.volume.replicas` and, through the meters, the amount the update
            // re-reserves. A body that differed only where the reconciler ignores it would pass the
            // update test while proving the update never left the grain.
            ChangedBody = cluster => StorageAccounts.Body(cluster, volumeServers: 5),
            // Drops the required `/properties/storage/size`.
            // ⚠ Built from a valid body with one required property removed rather than hand-written:
            // a hand-written invalid body drifts out of date the day the schema gains a property and
            // then tests "invalid for the wrong reason" while still going green.
            InvalidBody = cluster => WithoutStorageSize(StorageAccounts.Body(cluster)),
            InvalidBodyTarget = "/properties/storage/size",
            ActionName = StorageAccounts.ListKeysAction,
            // ⚠ BOTH OBJECTS, IN APPLY ORDER. The identities Secret is declared rather than left
            // undeclared-but-applied: the suite's delete assertion only proves what it lists is gone,
            // and a credential Secret surviving its account is exactly the thing that must not.
            Objects = (id, ns) => [
                StorageAccounts.ConfigSecretRef(ns, id.Name),
                StorageAccounts.SeaweedRef(ns, id.Name)
            ],
            // This platform mints or computes everything this type's actions hand back, so no operator
            // writes an object any action reads. Stated rather than defaulted — see
            // ProviderConformanceCase.OperatorWritten.
            OperatorWritten = static (_, _) => [],
            ObjectMatchesDesired = match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);
                return StorageAccounts.Matches(match.ObjectJson, desired.RootElement);
            }
        };

    /// <summary>A valid body with the required data-volume size removed.</summary>
    /// <param name="body">A valid body.</param>
    static string WithoutStorageSize(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["storage"]!.AsObject().Remove("size");
        return node.ToJsonString();
    }
}

/// <summary>
///     <c>CyberCloud.Storage/accounts/buckets</c> — the first <b>child</b> type in a shipping
///     provider, registered into the same shared suite as its parent.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>ONE MEMBER LONGER THAN <see cref="StorageCase" /> AND NOTHING ELSE DIFFERS.</b>
///         <see cref="ProviderConformanceCase" /> gained nothing to host a child, the four other
///         shipping providers' case files were not touched, and the bucket inherits <b>all 28</b>
///         assertions rather than a subset — <c>ProviderTestCluster.Address</c> interleaves the
///         ancestors the harness created, so every assertion that addressed
///         <c>…/accounts/{name}</c> now addresses <c>…/accounts/ancestor-0/buckets/{name}</c> and
///         nothing else about it changes.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The count is the same as the parent's and the APPLICABLE count is not, which is the
///             point of putting a child in the tree at all.
///         </b>
///         <c>CreatingUnderAParentThatDoesNotExistIsTheSame404AsAnAbsentResource</c> self-skips at
///         <see cref="ResourceTypeName.Depth" /> 1 — a top-level type's parent is its resource group
///         and there is no parent-existence check to fire — so <see cref="StorageCase" /> runs 28 and
///         exercises 27. This case is the first in a shipping provider to exercise all 28, and the
///         one it adds is the one a child type exists to be wrong about: a create under an account
///         that does not exist must be refused with the <i>same</i> 404 as an unauthorized read,
///         because a distinct code would be an existence oracle for anyone who may write in a
///         resource group but may not read a particular account.
///     </para>
///     <para>
///         ⚠ <see cref="Ancestors" /> is the parent's own case <i>object</i> rather than a
///         description of it — see <c>IProviderCaseSource.Ancestors</c>. A second description of the
///         account, written for the bucket's benefit, would be a second thing to keep in step with
///         the account's schema.
///     </para>
/// </remarks>
public sealed class StorageBucketCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Storage/accounts/buckets",
            CreateProvider = () => new StorageProvider(),
            ReconcilerType = typeof(StorageBucketReconciler),
            CreateReconciler = clock => new StorageBucketReconciler(clock),
            Type = StorageBuckets.Type,
            ApiVersion = StorageBuckets.V2026,
            Body = cluster => StorageBuckets.Body(cluster),
            // ⚠ Changes `versioning`, which the rendered object carries as a bare boolean, rather than
            // `quota.size`, which is omitted from the object entirely when empty. A changed body whose
            // difference the renderer can DROP would pass the update test while proving nothing about
            // whether the update reached the cluster.
            ChangedBody = cluster => StorageBuckets.Body(cluster, versioning: true),
            // Drops the required `/properties/clusterId`.
            // ⚠ Built from a valid body with one required property removed rather than hand-written,
            // for the reason StorageCase gives. ⚠ And it is the CLUSTER pointer here because a
            // bucket's body has only two other leaves and neither is required — which is itself worth
            // noticing: this is the thinnest body in the catalogue, and the thing that makes it thin
            // is that a child's identity is its address.
            InvalidBody = cluster => WithoutClusterId(StorageBuckets.Body(cluster)),
            InvalidBodyTarget = StorageBuckets.ClusterIdPointer,
            ActionName = StorageBuckets.StatsAction,
            Objects = (id, ns) => [StorageBuckets.BucketRef(ns, id)],
            // ⚠ THE OPERATOR WRITES ONTO THE OBJECT THE RECONCILER APPLIED, AND THAT IS THE ONE SHAPE
            // THIS MEMBER HAD NOT MET. Every earlier use places a SEPARATE object — CloudNativePG's
            // `{cluster}-app` Secret — beside what the reconciler rendered. bucket_usage.go patches
            // `status.usage` onto the Bucket itself, so what is placed here is the Bucket, carrying
            // the status subresource an operator would have written. The fake replaces the document
            // wholesale, which is more than a status patch; what the action assertion then proves is
            // that StorageBucketStatsHandler reads `status.usage` off the object it addresses, and
            // StorageActionHandlerTests proves the projection against a full document.
            OperatorWritten = (id, ns) => [
                (
                    StorageBuckets.BucketRef(ns, id),
                    StorageBuckets.WithSampledUsage(
                        StorageBuckets.BucketJson(id, JsonDocument.Parse(StorageBuckets.Body(Guid.Empty)).RootElement),
                        objectCount: 12,
                        sizeBytes: 4096,
                        sampledAt: "2026-09-15T12:00:00Z"
                    )
                )
            ],
            // ⚠ THE WHOLE PREDICATE, WHICH IT WAS NOT UNTIL `MatchContext` CARRIED AN ADDRESS. This
            // used to be `StorageBuckets.MatchesBody` — the body half — because
            // `ObjectMatchesDesired` was `(objectJson, desiredJson) => bool` and a bucket's
            // `spec.name` is its own name while its `spec.clusterRef` is its PARENT'S, both of which
            // live in the address. That gap is the finding
            // charts/managed/seaweedfs-bucket/conformance.yaml § owed recorded as
            // `object-matches-desired-cannot-see-an-address`; it is closed, and this case now checks
            // exactly what the account's does.
            //
            // ⚠ `StorageBucketReconcilerTests` KEEPS ITS OWN ASSERTIONS ON BOTH FIELDS. The one they
            // cover that this still cannot is two accounts in ONE resource group each holding a
            // bucket called `assets` — the harness builds one parent per run, so the collision has no
            // way to happen here however much address the case is handed.
            ObjectMatchesDesired = match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);
                return StorageBuckets.Matches(match.ObjectJson, match.Id, desired.RootElement);
            }
        };

    /// <inheritdoc />
    public static ImmutableArray<ProviderConformanceCase> Ancestors { get; } = [StorageCase.ProviderCase];

    /// <summary>A valid body with the required cluster pointer removed.</summary>
    /// <param name="body">A valid body.</param>
    static string WithoutClusterId(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject().Remove("clusterId");
        return node.ToJsonString();
    }
}

/// <summary>
///     <c>CyberCloud.Storage/accounts/fileShares</c> — the RWX shape, registered into the same shared
///     suite as the account and the bucket.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>TWO OBJECTS, AND THE FIRST IS SHARED WITH EVERY OTHER SHARE OF THE ACCOUNT.</b> The
///         <c>SeaweedCSIDriver</c> is one per filer; every share applies it and the last one out
///         removes it. It is listed under <see cref="ProviderConformanceCase.Objects" /> because the
///         suite's delete assertion proves only what is listed is gone, and with one share per run
///         "last one out" is this one — so the driver <i>must</i> be gone, and a reconciler that left
///         it standing on the account's last share would fail here. What one share per run cannot
///         reach is a sibling holding the driver open, and <c>StorageFileShareReconcilerTests</c>
///         asserts that half.
///     </para>
///     <para>
///         ⚠ <b><see cref="ProviderConformanceCase.OperatorWritten" /> plants the claim BOUND, and it
///         is the second shape that member has carried.</b> The action reads <c>spec.volumeName</c>,
///         which the CSI external-provisioner writes and the fake never does. The planted claim names
///         no owner, because nothing owns a dynamically provisioned claim, and the shared claims case
///         follows an unowned planted claim through the teardown without asking it for a controller.
///     </para>
/// </remarks>
public sealed class StorageFileShareCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Storage/accounts/fileShares",
            CreateProvider = () => new StorageProvider(),
            ReconcilerType = typeof(StorageFileShareReconciler),
            CreateReconciler = clock => new StorageFileShareReconciler(clock),
            Type = StorageFileShares.Type,
            ApiVersion = StorageFileShares.V2026,
            // ⚠ A NON-CANONICAL SIZE ON PURPOSE. 102400Mi is 100Gi, and a real API server stores a
            // PersistentVolumeClaim's request in canonical form — it reads back as `100Gi`. The fake
            // stores what it was sent, so only the cluster-backed lifecycle meets the rewrite; there,
            // MatchesDesired against this body is what proves the reconciler converges on a claim the
            // API server respelled, which a byte compare never did. Every other case in this provider
            // uses a canonical size, and that is exactly why nothing noticed.
            Body = cluster => StorageFileShares.Body(cluster, quotaSize: "102400Mi"),
            // ⚠ Changes `quota.size`, which is the ONLY tenant-facing leaf and the one the claim
            // carries as `spec.resources.requests.storage`. Larger, not smaller: a shrink is refused
            // by the API server (a claim's request may not decrease) and the update test would then
            // be asserting a refusal rather than an update.
            ChangedBody = cluster => StorageFileShares.Body(cluster, quotaSize: "200Gi"),
            // Drops the required `/properties/quota/size`.
            InvalidBody = cluster => WithoutQuotaSize(StorageFileShares.Body(cluster)),
            InvalidBodyTarget = "/properties/quota/size",
            ActionName = StorageFileShares.ListMountTargetsAction,
            // ⚠ IN APPLY ORDER: the driver, then the claim. A claim against a class that does not exist
            // yet stays Pending with no event naming why.
            Objects = (id, ns) => [
                StorageFileShares.DriverRef(ns, id),
                StorageFileShares.ClaimRef(ns, id)
            ],
            OperatorWritten = (id, ns) => [
                (
                    StorageFileShares.ClaimRef(ns, id),
                    StorageFileShares.WithBoundVolume(
                        StorageFileShares.ClaimJson(ns, id, JsonDocument.Parse(StorageFileShares.Body(Guid.Empty)).RootElement),
                        "pvc-0f7d2c1e-conformance"
                    )
                )
            ],
            ObjectMatchesDesired = match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);
                return StorageFileShares.Matches(match.ObjectJson, match.Id, match.Namespace, desired.RootElement);
            }
        };

    /// <inheritdoc />
    public static ImmutableArray<ProviderConformanceCase> Ancestors { get; } = [StorageCase.ProviderCase];

    /// <summary>A valid body with the required size removed.</summary>
    /// <param name="body">A valid body.</param>
    static string WithoutQuotaSize(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["quota"]!.AsObject().Remove("size");
        return node.ToJsonString();
    }
}

/// <summary>The shared suite, run against the managed object-storage provider.</summary>
/// <param name="cluster">The harness.</param>
public sealed class StorageAccountConformance(ProviderTestCluster<StorageCase> cluster)
    : ProviderConformanceTests<StorageCase>(cluster), IClassFixture<ProviderTestCluster<StorageCase>>;

/// <summary>
///     The <b>same</b> suite, run against the bucket child type.
/// </summary>
/// <remarks>
///     ⚠ <b>The same class, not a child-shaped copy of it.</b> A separate suite for children would be
///     free to assert less and nothing would say which assertions it had dropped. Deriving from
///     <c>ProviderConformanceTests&lt;T&gt;</c> makes the count a fact of the compiler rather than of
///     anybody's diligence, and <c>StorageBucketDeclarationTests</c> counts the two classes' runnable
///     facts against each other for the same reason the reference provider's
///     <c>SuiteRejectionTests.TheChildRunsEveryAssertionTheParentDoesRatherThanASubset</c> does.
/// </remarks>
/// <param name="cluster">The harness.</param>
public sealed class StorageBucketConformance(ProviderTestCluster<StorageBucketCase> cluster)
    : ProviderConformanceTests<StorageBucketCase>(cluster),
    IClassFixture<ProviderTestCluster<StorageBucketCase>>;

/// <summary>The container-backed half, skipped loudly, against the managed object-storage provider.</summary>
public sealed class StorageClusterBackedConformance() : ClusterBackedConformanceTests(StorageCase.ProviderCase);

/// <summary>The container-backed half, skipped loudly, against the bucket child type.</summary>
public sealed class StorageBucketClusterBackedConformance()
    : ClusterBackedConformanceTests(StorageBucketCase.ProviderCase);

/// <summary>The <b>same</b> suite, run against the file-share child type.</summary>
/// <param name="cluster">The harness.</param>
public sealed class StorageFileShareConformance(ProviderTestCluster<StorageFileShareCase> cluster)
    : ProviderConformanceTests<StorageFileShareCase>(cluster),
    IClassFixture<ProviderTestCluster<StorageFileShareCase>>;

/// <summary>The container-backed half, skipped loudly, against the file-share child type.</summary>
public sealed class StorageFileShareClusterBackedConformance()
    : ClusterBackedConformanceTests(StorageFileShareCase.ProviderCase);

/// <summary>
///     What this provider's two registrations into the shared suite are <b>shaped</b> like.
/// </summary>
/// <remarks>
///     ⚠ <b>Every assertion here is about the SUITE'S shape, not about the provider.</b> It lives in
///     this project rather than in <c>CyberCloud.Providers.Storage.Tests</c> because that project
///     deliberately does not reference this one, and these two test classes are the subjects.
/// </remarks>
public sealed class StorageSuiteShapeTests {
    [Fact]
    public void TheChildRunsEveryAssertionItsParentDoesRatherThanASubset() {
        // ⚠ "The bucket runs the same suite as the account" is a claim about a COUNT, and a claim
        // about a count that nothing counts is how a suite goes green by asking less. Both classes
        // derive from ProviderConformanceTests<T>, so the count is a fact of the compiler; what this
        // pins is that neither has grown a `new` member, an override that hides one, or a second
        // base. It reads the RUNNABLE facts — public, [Fact]-attributed — off the two closed generic
        // types, which is what xUnit itself enumerates.
        var parent = RunnableFactsOf(typeof(StorageAccountConformance));
        var child = RunnableFactsOf(typeof(StorageBucketConformance));
        var share = RunnableFactsOf(typeof(StorageFileShareConformance));

        child.ShouldBe(
            parent,
            "the bucket runs a different set of assertions than the account does. A child-shaped copy "
            + "of the suite is free to assert less, and nothing but this test would say which "
            + "assertions it had dropped."
        );

        share.ShouldBe(parent, "the file share runs a different set of assertions than the account does.");

        parent.Length.ShouldBeGreaterThan(20);
    }

    [Fact]
    public void OnlyTheChildDescribesAnAncestorAndItIsTheAccountsOwnCaseObject() {
        // ⚠ IProviderCaseSource.Ancestors is a `static virtual` with a default of `[]`, and the
        // reason that optional member is not the "assertion the suite quietly stops making" the case
        // record forbids is that omitting it is LOUD: ProviderTestCluster.Ancestors refuses a
        // depth/count mismatch by name before a single test runs. This is the positive half — that
        // the account, at depth 1, describes none, and the bucket describes exactly its parent.
        //
        // ⚠ Reached through a type PARAMETER rather than as `StorageCase.Ancestors`. A
        // `static virtual` interface member is only accessible through a constrained generic, which
        // is also what stops a provider from "implementing" it as an ordinary static that the harness
        // would never call.
        AncestorsOf<StorageCase>().ShouldBeEmpty();

        var ancestors = AncestorsOf<StorageBucketCase>();

        ancestors.Length.ShouldBe(1);

        ancestors[0].ShouldBeSameAs(
            StorageCase.ProviderCase,
            "the bucket's ancestor is a SECOND DESCRIPTION of the account rather than the account's "
            + "own case object, so the two can disagree the first time either changes."
        );

        ancestors[0].Type.ShouldBe(StorageAccounts.Type);

        // ⚠ And the share's is the SAME object — not a second child describing the account its own
        // way. Two children of one parent citing two case objects would be two accounts to keep in
        // step with one schema.
        AncestorsOf<StorageFileShareCase>().Single().ShouldBeSameAs(StorageCase.ProviderCase);
    }

    static ImmutableArray<ProviderConformanceCase> AncestorsOf<TSource>()
        where TSource : IProviderCaseSource => TSource.Ancestors;

    /// <summary>Every <c>[Fact]</c> a test class runs, by name, ordered.</summary>
    /// <param name="suite">The closed test class.</param>
    static ImmutableArray<string> RunnableFactsOf(Type suite) => [
        .. suite
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(x => x.GetCustomAttributes(typeof(FactAttribute), true).Length > 0)
            .Select(x => x.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
    ];
}
