using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Storage.Tests;

/// <summary>
///     The bucket reconciler, and the two things about a child type that no shared suite can assert.
/// </summary>
/// <remarks>
///     ⚠ <b>THIS FILE EXISTS BECAUSE <c>ProviderConformanceCase.ObjectMatchesDesired</c> CANNOT SEE AN
///     ADDRESS.</b> That member is <c>(objectJson, desiredJson) =&gt; bool</c>, so the shared suite's
///     comparison for a bucket is <see cref="StorageBuckets.MatchesBody" /> — versioning and quota —
///     and the two fields that make a bucket a <i>child</i>, <c>spec.name</c> and
///     <c>spec.clusterRef</c>, are outside it by construction. They are asserted here against real
///     addresses instead, including the collision the harness could never build.
/// </remarks>
public sealed class StorageBucketReconcilerTests {
    // ── Failure class (b): a reconciler with a field, and the blind spot above it ────────────────

    [Fact]
    public void TheReconcilerHoldsNoMutableState() {
        // Clause 2, checked structurally, in the cheap place.
        ReconcilerConformance.CheckNoHiddenState(new StorageBucketReconciler(new FixedClock()))
            .ShouldBeEmpty();
    }

    [Fact]
    public void TheStructuralCheckStillMissesAReadonlyMutableCacheOnAChildToo() {
        // ⚠ THE BLIND SPOT, PINNED AGAINST A COUNTER-EXAMPLE THAT SHOULD FAIL AND DOES NOT — AND THIS
        // IS THE FOURTH CONFIRMED SIGHTING. ReconcilerConformance.CheckNoHiddenState skips a field
        // that is `readonly`, and a `readonly` Dictionary is mutable forever, which is exactly the
        // shape a per-tenant cache takes when somebody adds one for performance.
        //
        // ⚠ IT IS WORSE ON A CHILD THAN ON A TOP-LEVEL TYPE, which is why this is not a copy of the
        // account's version of it. The obvious cache key on a child is `context.Id.Name` — the
        // BUCKET'S name — and two accounts in one resource group may each hold a bucket called
        // `assets`. So the same defect that leaks between tenants on the account leaks between
        // ACCOUNTS OF ONE TENANT here, which no cross-tenant test would catch. The counter-example
        // below keys on exactly that, and the collision test further down is what catches it.
        ReconcilerConformance.CheckNoHiddenState(new BucketReconcilerWithAReadonlyCache()).ShouldBeEmpty(
            "the structural check now catches a readonly mutable collection. That is an improvement — "
            + "delete this test and say so in ReconcilerConformance's remarks, which currently promise "
            + "the opposite."
        );
    }

    [Fact]
    public async Task OneReconcilerInstanceServesTwoTenantsWithoutMixingThem() {
        // ⚠ AddCyberCloudProvider registers a reconciler as a SINGLETON BY CONCRETE TYPE, so in a real
        // silo ONE instance serves every tenant in the process.
        var reconciler = new StorageBucketReconciler(new FixedClock());
        var connection = new RecordingConnection();

        // ⚠ The same bucket name AND the same account name in both tenants. Each brings its own
        // subscription, because ReconcileDriver.NamespaceFor is `{subscriptionId:N}-{resourceGroup}`
        // and the TENANT ID IS NOT IN IT.
        var alice = Address("assets", "media", TenantA, SubscriptionA);
        var bob = Address("assets", "media", TenantB, SubscriptionB);

        using var aliceBody = JsonDocument.Parse(StorageBuckets.Body(ClusterId, "10Gi"));
        using var bobBody = JsonDocument.Parse(StorageBuckets.Body(ClusterId, "999Gi", versioning: true));

        // Interleaved, so a cache written on the first pass is read on the third.
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);
        await Pass(reconciler, connection, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, bob, bobBody.RootElement);

        var applied = connection.Applied;
        applied.Count.ShouldBe(4);

        Spec(applied[2].Body)["quota"]!.GetValue<string>()
            .ShouldBe("10Gi", "tenant A's quota came back as tenant B's");

        Spec(applied[3].Body)["versioning"]!.GetValue<bool>()
            .ShouldBe(true, "tenant B's versioning came back as tenant A's");

        applied[0].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantA));
        applied[1].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantB));
        applied[0].Target.Namespace.ShouldNotBe(applied[1].Target.Namespace);
    }

    // ── The thing a child type exists to get wrong ──────────────────────────────────────────────

    [Fact]
    public async Task TwoAccountsInOneResourceGroupHoldTwoDifferentBucketsOfTheSameName() {
        // ⚠ THE ASSERTION THIS TYPE EXISTS FOR, AND THE ONE NO OTHER SUITE IN THE TREE MAKES.
        // ReconcileDriver.NamespaceFor is `{subscriptionId:N}-{resourceGroup}`, so a parent RESOURCE
        // lives inside a namespace rather than being one. One tenant, one subscription, one resource
        // group, two accounts, one bucket name — which is the ordinary case, not an edge one.
        //
        // A renderer that ignored ResourceId.ParentNames would put both into ONE Bucket object, and
        // both would converge: each pass overwrites the other's spec and then reads back exactly what
        // it wrote. Nothing anywhere reports an error, and the two tenants' — here, the two teams' —
        // data ends up in one bucket.
        var reconciler = new StorageBucketReconciler(new FixedClock());
        var connection = new RecordingConnection();

        var media = Address("assets", "media", TenantA, SubscriptionA);
        var logs = Address("assets", "logs", TenantA, SubscriptionA);

        using var body = JsonDocument.Parse(StorageBuckets.Body(ClusterId));

        (await Pass(reconciler, connection, media, body.RootElement)).ShouldBe(ReconcileOutcome.Converged);
        (await Pass(reconciler, connection, logs, body.RootElement)).ShouldBe(ReconcileOutcome.Converged);

        var applied = connection.Applied;

        applied[0].Target.Namespace.ShouldBe(
            applied[1].Target.Namespace,
            "the two accounts are in one resource group, so they share a namespace. If this ever "
            + "stops being true the rest of this test is asserting nothing."
        );

        applied[0].Target.Name.ShouldBe("media-assets");
        applied[1].Target.Name.ShouldBe("logs-assets");

        applied[0].Target.Name.ShouldNotBe(
            applied[1].Target.Name,
            "two accounts' buckets of the same name rendered into ONE Kubernetes object. Both would "
            + "converge — each pass overwrites the other and then reads back what it wrote — and "
            + "nothing anywhere would report it."
        );

        // ⚠ And the S3 name a tenant addresses is the bucket's OWN name in both, not the qualified
        // object name. Rendering `media-assets` into spec.name would hand the tenant a bucket at an
        // address neither they nor docs/plan/15 named.
        Spec(applied[0].Body)["name"]!.GetValue<string>().ShouldBe("assets");
        Spec(applied[1].Body)["name"]!.GetValue<string>().ShouldBe("assets");
    }

    [Fact]
    public async Task TheClusterRefNamesTheAccountFromTheAddressAndNotFromTheBody() {
        // ⚠ THE FIELD THE SHARED SUITE CANNOT CHECK. docs/plan/12 § Child resources makes the parent a
        // pure function of the address; nothing in a bucket's body names its account, and a body
        // property that did would be a second spelling of the same fact that disagrees the first time
        // a body is sent under the wrong path.
        var connection = new RecordingConnection();
        var address = Address("assets", "media", TenantA, SubscriptionA);

        using var body = JsonDocument.Parse(StorageBuckets.Body(ClusterId));

        await Pass(new StorageBucketReconciler(new FixedClock()), connection, address, body.RootElement);

        Spec(connection.Applied[0].Body)["clusterRef"]!.GetValue<string>().ShouldBe(
            "media",
            "the Bucket does not reference the Seaweed of the account it is addressed under, so the "
            + "operator would reconcile it against a different account's cluster or against none."
        );

        // ⚠ A LITERAL, not StorageBuckets.ClusterRefOf(address). Deriving the expectation the same way
        // the renderer does would compare the renderer to itself, which is the shape that let an
        // earlier provider's casing sabotage stay green.
        body.RootElement.GetRawText().ShouldNotContain(
            "media",
            Case.Sensitive,
            "the body carries the account's name. A child's parent belongs in the address only — two "
            + "spellings of one fact is one fact and one thing to keep in step with it."
        );
    }

    [Fact]
    public async Task NothingInAPassEverReadsTheAccountsOwnObject() {
        // ⚠ docs/plan/08 § Deleting a parent resource that has children: the platform "must not
        // re-check the parent on every write to a child" — the check belongs on the CREATE, in
        // ResourceManagerService.ResolveAsync, where it runs before the enforcement seam and answers
        // the same 404 as an unauthorized read.
        //
        // ⚠ AND ON THIS TYPE THERE IS A SECOND, SHARPER REASON: THE ACCOUNT NEVER CONVERGES. Its S3
        // gateway mounts a Secret nothing writes — StorageAccounts.ConfigSecretName — so it stays
        // InProgress indefinitely and deliberately. A bucket reconciler that read its parent's
        // Seaweed back and waited for it would therefore never converge for anybody, and the symptom
        // would read as a bug in the bucket rather than as the account's known gap.
        var connection = new RecordingConnection();
        var address = Address("assets", "media", TenantA, SubscriptionA);

        using var body = JsonDocument.Parse(StorageBuckets.Body(ClusterId));

        await Pass(new StorageBucketReconciler(new FixedClock()), connection, address, body.RootElement);

        connection.Read.ShouldAllBe(
            x => x.Kind.Kind == "Bucket",
            "a pass read an object that is not this bucket's own. The only kind a bucket reconciler "
            + "may touch is Bucket: reading the account's Seaweed would be the parent re-check "
            + "docs/plan/08 forbids, and on this type it would also never finish."
        );

        connection.Applied.ShouldAllBe(x => x.Target.Kind.Kind == "Bucket");
    }

    // ── Failure class (c), at the only level a provider can assert it ────────────────────────────

    [Fact]
    public async Task DeletingABucketRemovesItsOwnObjectAndNothingOfItsAccounts() {
        // ⚠ WHAT A BUCKET'S DELETE MAY DO, WHICH IS THE HALF OF FAILURE CLASS (c) THAT IS A
        // PROVIDER'S. The other half — what happens to buckets when the ACCOUNT is deleted — is
        // docs/plan/08 § Deleting a parent resource that has children's already-decided 409 and is
        // NOT implemented, because the platform cannot enumerate children. See
        // charts/managed/seaweedfs-bucket/conformance.yaml § owed, `parent-delete-orphans-buckets`.
        //
        // What this pins is that the sequence which DOES work today — delete the buckets, then the
        // account — is clean: a bucket's teardown touches one object, and a delete that tidied up its
        // parent, or that waited for it, would be this type reaching outside its own resource.
        var connection = new RecordingConnection();
        var address = Address("assets", "media", TenantA, SubscriptionA);
        var reconciler = new StorageBucketReconciler(new FixedClock());

        using var body = JsonDocument.Parse(StorageBuckets.Body(ClusterId));

        // A Seaweed the account owns, sitting in the same namespace, exactly as it would be.
        var ns = ReconcileDriver.NamespaceFor(address);
        var seaweed = StorageAccounts.SeaweedRef(ns, "media");
        connection.Objects[RecordingConnection.Key(seaweed)] = "{\"kind\":\"Seaweed\"}";

        await Pass(reconciler, connection, address, body.RootElement);

        var deleted = await reconciler.DeleteAsync(
            Context(connection, address, body.RootElement),
            TestContext.Current.CancellationToken
        );

        deleted.ShouldBe(ReconcileOutcome.Converged);
        connection.Deleted.Count.ShouldBe(1);
        connection.Deleted[0].Kind.Kind.ShouldBe("Bucket");

        connection.Objects.ShouldContainKey(
            RecordingConnection.Key(seaweed),
            "deleting a bucket tore down its account's Seaweed. Every other bucket in that account, "
            + "and the account itself, would go with it."
        );
    }

    // ── The four clauses, isolated ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConvergedFollowsTheReadAndNotTheApply() {
        // Clause 4. An apply that reports success and stores nothing is what a swallowing admission
        // webhook looks like from here.
        var connection = new RecordingConnection { SwallowApplies = true };
        var address = Address("assets", "media", TenantA, SubscriptionA);

        using var body = JsonDocument.Parse(StorageBuckets.Body(ClusterId));

        var outcome = await Pass(
            new StorageBucketReconciler(new FixedClock()),
            connection,
            address,
            body.RootElement
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
    }

    [Fact]
    public async Task ASecondPassWithTheSameBodyChangesNothing() {
        // Clause 1.
        var connection = new RecordingConnection();
        var address = Address("assets", "media", TenantA, SubscriptionA);
        var reconciler = new StorageBucketReconciler(new FixedClock());

        using var body = JsonDocument.Parse(StorageBuckets.Body(ClusterId));

        await Pass(reconciler, connection, address, body.RootElement);
        var first = connection.Applied.Select(x => x.Body).ToArray();

        await Pass(reconciler, connection, address, body.RootElement);
        connection.Applied.Skip(first.Length).Select(x => x.Body).ToArray().ShouldBe(first);
    }

    [Fact]
    public async Task AnEmptyQuotaRendersNoQuotaFieldRatherThanAnEmptyOne() {
        // ⚠ The CRD's own default has to stand. A `quota: ""` would be this chart inventing a value
        // for a field it means to leave alone, and — because Matches is CONTAINMENT — nothing here
        // would ever report the difference.
        var connection = new RecordingConnection();
        var address = Address("assets", "media", TenantA, SubscriptionA);

        using var body = JsonDocument.Parse(StorageBuckets.Body(ClusterId, quotaSize: string.Empty));

        await Pass(new StorageBucketReconciler(new FixedClock()), connection, address, body.RootElement);

        Spec(connection.Applied[0].Body).ContainsKey("quota").ShouldBeFalse(
            "an unset quota rendered a quota field anyway"
        );
    }

    // ── Failure class (f), at the object: a bucket carries no credential and no public switch ────

    [Fact]
    public async Task TheRenderedObjectCarriesNoCredentialAndNoAnonymousReadSwitch() {
        // ⚠ TWO ABSENCES, AND BOTH ARE THE POINT OF THIS TYPE'S ACCESS STORY.
        //
        // There is no per-bucket credential in SeaweedFS to render: data-plane access to a bucket is
        // its ACCOUNT'S S3 identities, and those do not exist — the account's gateway mounts a Secret
        // nothing writes, which is what keeps `isAuthEnabled = len(identities) > 0` from handing every
        // anonymous caller an ACTION_ADMIN identity.
        //
        // And `anonymousRead` is absent because docs/plan/15 requires a TWO-step opt-in for public
        // access; the first step is an account-level switch that does not exist, so shipping the
        // bucket half alone would be the one-step opt-in that document forbids in as many words.
        // ⚠ It is absent rather than rendered `false`, so the day the first step lands, turning it on
        // is a new field rather than a changed value.
        var connection = new RecordingConnection();
        var address = Address("assets", "media", TenantA, SubscriptionA);

        using var body = JsonDocument.Parse(StorageBuckets.Body(ClusterId));

        await Pass(new StorageBucketReconciler(new FixedClock()), connection, address, body.RootElement);

        foreach (var forbidden in new[] {
                     "anonymousRead", "accessKey", "secretKey", "secretAccessKey", "stringData",
                     "configSecret", "owner"
                 }) {
            connection.Applied[0].Body.ShouldNotContain(forbidden, Case.Sensitive, forbidden);
        }
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");
    static readonly Guid TenantA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid TenantB = Guid.Parse("44444444-4444-4444-8444-444444444444");
    static readonly Guid SubscriptionA = Guid.Parse("22222222-2222-4222-8222-222222222222");
    static readonly Guid SubscriptionB = Guid.Parse("55555555-5555-4555-8555-555555555555");

    static async Task<ReconcileOutcome> Pass(
        StorageBucketReconciler reconciler,
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired
    ) =>
        await reconciler.ReconcileAsync(
            Context(connection, address, desired),
            TestContext.Current.CancellationToken
        );

    static ReconcileContext Context(
        IKubeClusterConnection? connection,
        ResourceId address,
        JsonElement desired
    ) =>
        new(
            address,
            StorageBuckets.V2026,
            desired,
            null,
            ReconcileDriver.NamespaceFor(address),
            connection,
            new UnavailableSecretResolver(),
            new NullLog()
        );

    /// <summary>A bucket's address inside a named account.</summary>
    static ResourceId Address(string name, string account, Guid tenant, Guid subscription) =>
        new(
            tenant,
            subscription,
            "prod",
            StorageBuckets.Type,
            name,
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
            account
        );

    static JsonObject Spec(string objectJson) => JsonNode.Parse(objectJson)!["spec"]!.AsObject();
}

/// <summary>
///     A bucket reconciler that <c>CheckNoHiddenState</c> passes and that is not stateless.
/// </summary>
/// <remarks>
///     ⚠ <b>Keyed on the BUCKET'S own name, which is what makes this shape worse on a child than on a
///     top-level type.</b> Two accounts in one resource group may each hold a bucket called
///     <c>assets</c>, so this cache collides between two resources of <i>one</i> tenant — and no
///     cross-tenant test would ever see it. The field is <see langword="readonly" />, so
///     <c>CheckNoHiddenState</c> skips it and the dictionary is mutable forever.
/// </remarks>
sealed class BucketReconcilerWithAReadonlyCache : IResourceReconciler {
    readonly Dictionary<string, string> lastRendered = new(StringComparer.Ordinal);

    public ResourceTypeName Type => StorageBuckets.Type;

    public Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        lastRendered[context.Id.Name] = context.Desired.GetRawText();
        return Task.FromResult(ReconcileOutcome.Converged);
    }

    public Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(ReconcileOutcome.Converged);

    public Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(ObservedState.Absent);
}
