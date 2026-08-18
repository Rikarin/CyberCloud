using CyberCloud.Core.Time;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.Tests;

/// <summary>
///     The workspace reconciler against a connection that misbehaves in the ways a real cluster does.
/// </summary>
/// <remarks>
///     ⚠ <b>The harness below is a fresh copy and it has to be.</b>
///     <c>src/Providers/README.md § Hard rule</c> forbids a <c>Providers.*</c> assembly referencing
///     another, so <c>RecordingConnection</c>, <c>FixedClock</c> and <c>NullLog</c> cannot be shared
///     with any sibling however identical they look. That duplication is the price of the rule and is
///     worth naming rather than apologising for: the alternative is a line in
///     <c>module-layering.txt</c> between two providers, which rule 2 refuses.
/// </remarks>
public sealed class MonitorReconcilerTests {
    // ── Failure class (a): a reconciler with a field ─────────────────────────────────────────────

    [Fact]
    public void TheReconcilerHoldsNoMutableState() {
        // Clause 2, checked structurally, in the cheap place. The conformance run checks it too; this
        // is the one that catches the field somebody adds in a hurry, before a suite has to boot.
        ReconcilerConformance.CheckNoHiddenState(new MonitorWorkspaceReconciler(new FixedClock()))
            .ShouldBeEmpty();
    }

    [Fact]
    public void TheStructuralCheckCatchesAReadonlyMutableCache() {
        // ⚠ CALIBRATION, AND IT NOW POINTS THE OTHER WAY. This test used to assert that
        // CheckNoHiddenState MISSED the counter-example below, because it skipped every
        // `field.IsInitOnly` — and `readonly` stops the FIELD being reassigned while stopping
        // nothing about the dictionary, so a per-tenant cache passed clause 2 while accumulating
        // state on a singleton every tenant shares. Seven families each pinned that blind spot
        // and it is now closed; this is what holds it closed.
        //
        // ⚠ THE CROSS-TENANT TEST BELOW STAYS, AND IS NOT MADE REDUNDANT BY THIS. This one reads
        // a field's declared TYPE. That one drives ONE reconciler instance through TWO tenants and
        // compares what each got, which is the only way to catch mixing no field type could show.
        var findings = ReconcilerConformance.CheckNoHiddenState(new ReconcilerWithAReadonlyCache());

        findings.ShouldContain(
            x => x.Clause == ReconcilerClause.NoHiddenState,
            "a readonly field holding a mutable Dictionary is state on a shared singleton, and the "
            + "structural check is what catches it before the behavioural test has to"
        );

        findings.ShouldContain(x => x.Detail.Contains("lastRendered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OneReconcilerInstanceServesTwoTenantsWithoutMixingThem() {
        // ⚠ THE TEST A SINGLE-TENANT TEST CANNOT BE, AND THE ONLY ONE THAT CATCHES THE CACHE ABOVE.
        // AddCyberCloudProvider registers a reconciler as a SINGLETON BY CONCRETE TYPE, so in a real
        // silo ONE instance serves every tenant in the process.
        var reconciler = new MonitorWorkspaceReconciler(new FixedClock());

        // ⚠ THE SAME RESOURCE NAME IN BOTH TENANTS. Two tenants naming a workspace `prod` is the
        // ordinary case. ⚠ Each brings its OWN subscription, because ReconcileDriver.NamespaceFor is
        // `{subscriptionId:N}-{resourceGroup}` and the TENANT ID IS NOT IN IT — two tenants sharing a
        // subscription id would share a namespace and this test would fail for the harness's reason.
        var alice = Address("prod", TenantA, SubscriptionA, WorkspaceA);
        var bob = Address("prod", TenantB, SubscriptionB, WorkspaceB);

        var connection = new RecordingConnection();
        var vault = new InMemorySecretVault();

        using var aliceBody = JsonDocument.Parse(
            MonitorWorkspaces.Body(ClusterId, logsTier: "standard", logsGbPerDay: 10)
        );

        using var bobBody = JsonDocument.Parse(
            MonitorWorkspaces.Body(ClusterId, logsTier: "extended", logsGbPerDay: 250)
        );

        // Interleaved, so a cache written on the first pass is read on the third.
        await Pass(reconciler, connection, vault, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, vault, bob, bobBody.RootElement);
        await Pass(reconciler, connection, vault, alice, aliceBody.RootElement);
        await Pass(reconciler, connection, vault, bob, bobBody.RootElement);

        var rows = connection.Applied.Where(x => x.Target.Kind.Kind == "ConfigMap").ToList();

        rows.Count.ShouldBe(4);

        Data(rows[0].Body)["retentionLogsDays"]!.GetValue<string>().ShouldBe("30");
        Data(rows[1].Body)["retentionLogsDays"]!.GetValue<string>().ShouldBe("90");
        Data(rows[2].Body)["retentionLogsDays"]!.GetValue<string>()
            .ShouldBe("30", "tenant A's retention came back as tenant B's");
        Data(rows[3].Body)["quotaLogsGbPerDay"]!.GetValue<string>()
            .ShouldBe("250", "tenant B's allowance came back as tenant A's");

        rows[0].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantA));
        rows[1].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantB));

        // ⚠ And the two tenants' objects are in different namespaces, so the third pass read back
        // Alice's row rather than Bob's. Without this the assertions above would hold for a
        // reconciler that wrote both tenants into one namespace and let the second overwrite the
        // first.
        rows[0].Target.Namespace.ShouldNotBe(rows[1].Target.Namespace);
    }

    // ── The thing this type sells: one workspace cannot reach another's data ─────────────────────

    [Fact]
    public async Task TwoWorkspacesInTwoTenantsGetTwoAccountIdsAndTwoDatabases() {
        // ⚠⚠ THE ASSERTION THE SHARED SUITE CANNOT MAKE, AND THE ONE THIS TYPE EXISTS FOR.
        // ProviderConformanceCase.ObjectMatchesDesired is handed an object and a body and NO ADDRESS
        // — the finding StorageBuckets records — so a render that put every workspace on one
        // accountID would go green through the whole suite and would let every tenant read every
        // other tenant's metrics. The same mistake on CyberCloud.Storage/accounts/buckets produces
        // two buckets fighting over one object; here it produces a data breach.
        var reconciler = new MonitorWorkspaceReconciler(new FixedClock());
        var connection = new RecordingConnection();
        var vault = new InMemorySecretVault();

        using var body = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId));

        await Pass(reconciler, connection, vault, Address("prod", TenantA, SubscriptionA, WorkspaceA), body.RootElement);
        await Pass(reconciler, connection, vault, Address("prod", TenantB, SubscriptionB, WorkspaceB), body.RootElement);

        var accounts = connection.Applied.Where(x => x.Target.Kind.Kind == "ConfigMap")
            .Select(x => Data(x.Body)["accountId"]!.GetValue<string>())
            .ToList();

        accounts[0].ShouldNotBe(
            accounts[1],
            "two workspaces in two tenants were given the same VictoriaMetrics accountID, which means "
            + "either can read the other's metrics."
        );

        var databases = connection.Applied.Where(x => x.Target.Kind.Kind == "ConfigMap")
            .Select(x => Data(x.Body)["database"]!.GetValue<string>())
            .ToList();

        databases[0].ShouldNotBe(databases[1], "two workspaces were given the same ClickHouse database");

        // And the routing carries the same two accounts, so the VMUser and the row cannot disagree.
        var suffixes = connection.Applied.Where(x => x.Target.Kind.Kind == "VMUser")
            .Select(x => Suffix(x.Body, 0))
            .ToList();

        suffixes[0].ShouldBe($"/insert/{accounts[0]}/prometheus");
        suffixes[1].ShouldBe($"/insert/{accounts[1]}/prometheus");
    }

    [Fact]
    public void NoAccountIdIsEverZero() {
        // ⚠ VictoriaMetrics treats accountID=0 as a legal tenant, and it is the account every
        // misconfigured client writes to by default. A fold that could produce it would hand one
        // workspace everybody else's stray metrics.
        for (var attempt = 0; attempt < 2000; attempt++) {
            MonitorWorkspaces.AccountId(Address("w", TenantA, SubscriptionA, Guid.NewGuid()))
                .ShouldNotBe(0u);
        }
    }

    [Fact]
    public void TheTargetPathSuffixIsSpelledTheWayTheGoTagSpellsIt() {
        // ⚠⚠ SNAKE_CASE, AND THIS TEST IS THE ONLY THING IN THE TREE THAT CATCHES THE OTHER
        // SPELLING. api/operator/v1beta1/vmuser_types.go tags TargetRef's field
        // `target_path_suffix`; the operator's own PROSE calls it "targetPathSuffix". A document
        // written from the prose is accepted by the API server — the cluster-backed harness installs
        // an OPEN CRD stub, and a real VMUser CRD does not refuse an unknown key here either — and is
        // then IGNORED by vmauth, which means the workspace's writes land on whatever tenant the
        // url_prefix defaulted to. Nothing in an apply, a read-back, an admission check or either
        // conformance suite would notice.
        using var body = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId));

        var rendered = MonitorWorkspaces.VmUserJson(
            Address("prod", TenantA, SubscriptionA, WorkspaceA),
            body.RootElement
        );

        rendered.ShouldContain(
            "target_path_suffix",
            Case.Sensitive,
            "the VMUser no longer spells the suffix the way the Go tag spells it. `targetPathSuffix` "
            + "is the operator's prose and is not a field; vmauth would route this workspace to "
            + "whatever tenant the url_prefix defaulted to."
        );

        rendered.ShouldNotContain(
            "targetPathSuffix",
            Case.Sensitive,
            "the VMUser carries the operator's PROSE spelling of the suffix, which is not a field."
        );
    }

    [Fact]
    public void TheMetricsRoutingNamesTheClusterOfTheRequestedRetentionTier() {
        // ⚠ PER-TENANT RETENTION IS ENTERPRISE IN VICTORIAMETRICS — see SOURCE — so a workspace's
        // metrics tier is which vmstorage group it is routed to. A VMUser that named one cluster
        // whatever the tier would give every workspace the same retention, would converge, and would
        // be billed at whatever tier the tenant chose.
        foreach (var tier in MonitorWorkspaces.Tiers) {
            using var body = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId, metricsTier: tier));

            var rendered = MonitorWorkspaces.VmUserJson(
                Address("prod", TenantA, SubscriptionA, WorkspaceA),
                body.RootElement
            );

            JsonNode.Parse(rendered)!["spec"]!["targetRefs"]!.AsArray()
                .Select(x => x!["crd"]!["name"]!.GetValue<string>())
                .ShouldAllBe(x => x == "telemetry-" + tier);
        }
    }

    // ── Failure class (c): a retention a tenant can shorten ─────────────────────────────────────

    [Fact]
    public async Task ShorteningARetentionIsRefusedAndAppliesNothing() {
        // ⚠⚠ THE DATA-LOSS PATH, REFUSED. docs/plan/16 prices retention, so it is settable;
        // shortening it destroys everything already outside the new window, and on ClickHouse that is
        // not slow drift — expiry runs at the next merge and the engine schedules an off-schedule
        // merge when it detects expired data. The API cannot refuse it (ResourceSchema validates one
        // body against constants), so the reconciler does — BEFORE it applies anything, so a refused
        // shrink leaves the workspace exactly as it was.
        var reconciler = new MonitorWorkspaceReconciler(new FixedClock());
        var connection = new RecordingConnection();
        var vault = new InMemorySecretVault();
        var address = Address("prod", TenantA, SubscriptionA, WorkspaceA);

        using var wide = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId, logsTier: "extended"));
        (await Pass(reconciler, connection, vault, address, wide.RootElement))
            .ShouldBe(ReconcileOutcome.Converged);

        var appliedBefore = connection.Applied.Count;

        using var narrow = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId, logsTier: "short"));
        var outcome = await Pass(reconciler, connection, vault, address, narrow.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Retryable.ShouldBeFalse("a shrink refused once is refused identically forever");

        // ⚠ BOTH DAY COUNTS IN THE MESSAGE. A refusal a tenant cannot act on is a refusal they will
        // retry, and the tier names alone do not say how much data is at stake.
        outcome.Error!.Message.ShouldContain("90");
        outcome.Error.Message.ShouldContain("7");

        connection.Applied.Count.ShouldBe(
            appliedBefore,
            "the refused pass applied something. The whole point of checking before the applies is "
            + "that a refused shrink changes nothing — a pass that had already written the Secret and "
            + "the VMUser would leave the workspace half-updated."
        );

        // And the old window is still what the cluster carries.
        Data(connection.Objects[RecordingConnection.Key(MonitorWorkspaces.RowRef(
                ReconcileDriver.NamespaceFor(address), address.Name))])["retentionLogsDays"]!
            .GetValue<string>()
            .ShouldBe("90");
    }

    [Fact]
    public async Task LengtheningARetentionIsAllowed() {
        // The other side of the same check. A refusal that also blocked growth would make the priced
        // property unusable, which is the failure that would be found by a customer rather than by a
        // test.
        var reconciler = new MonitorWorkspaceReconciler(new FixedClock());
        var connection = new RecordingConnection();
        var vault = new InMemorySecretVault();
        var address = Address("prod", TenantA, SubscriptionA, WorkspaceA);

        using var narrow = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId, logsTier: "short"));
        await Pass(reconciler, connection, vault, address, narrow.RootElement);

        using var wide = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId, logsTier: "extended"));

        (await Pass(reconciler, connection, vault, address, wide.RootElement))
            .ShouldBe(ReconcileOutcome.Converged);
    }

    [Fact]
    public async Task AFirstPassIsNotAShrink() {
        // ⚠ Every create has no previous window. A check that read a missing row as "retention zero"
        // would refuse every create of every workspace, which is the shape of bug that gets shipped
        // because the shrink test passes.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId, logsTier: "short"));

        (await Reconcile(connection, body.RootElement)).ShouldBe(ReconcileOutcome.Converged);
    }

    // ── The four clauses, isolated ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConvergedFollowsTheReadAndNotTheApply() {
        // Clause 4. An apply that reports success and stores nothing is what a swallowing admission
        // webhook looks like from here — and a reconciler that trusted the apply's own result would
        // report Converged for a workspace with nothing published.
        var connection = new RecordingConnection { SwallowApplies = true };
        using var body = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId));

        var outcome = await Reconcile(connection, body.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
    }

    [Fact]
    public async Task EveryAppliedObjectIsAlsoReadBack() {
        // ⚠ THREE OBJECTS MAKE THIS THE ASSERTION THAT MATTERS MOST ON THIS TYPE. A reconciler that
        // applied all three and read back only the row would report Converged for a workspace whose
        // VMUser apply was swallowed — which is a workspace the ingest host believes in and vmauth
        // refuses every write to. Set equality in BOTH directions is the only form that catches any
        // one of the three going missing.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId));

        (await Reconcile(connection, body.RootElement)).ShouldBe(ReconcileOutcome.Converged);

        var applied = connection.Applied.Select(x => RecordingConnection.Key(x.Target))
            .ToHashSet(StringComparer.Ordinal);

        // ⚠ The shrink check reads the row before anything is applied, so the read set is a SUPERSET
        // rather than an equal — which is why this asserts containment in one direction and a count
        // in the other rather than set equality.
        var read = connection.Read.Select(RecordingConnection.Key).ToHashSet(StringComparer.Ordinal);

        applied.Count.ShouldBe(3);

        foreach (var target in applied) {
            read.ShouldContain(
                target,
                $"'{target}' was applied and never read back. An object applied and not read back is "
                + "one the loop reports Converged without ever having observed."
            );
        }
    }

    [Fact]
    public async Task TheKeyIsAppliedFirstAndTheRowLast() {
        // ⚠ NOT DECORATION, AND THE TWO ENDS ARE TWO DIFFERENT ARGUMENTS. The Secret is first because
        // the VMUser names it in passwordRef and vmauth resolving a missing secret is a user that
        // authenticates nothing. The row is LAST because it is what announces the workspace to the
        // ingest host, and a row that appeared first would advertise a workspace as ready while every
        // write to it was refused.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        connection.Applied.Select(x => x.Target.Kind.Kind).ShouldBe(["Secret", "VMUser", "ConfigMap"]);
    }

    [Fact]
    public async Task EachRenderedBodyNamesTheSameKindTheCommandTargets() {
        // ⚠ THE TWO-SPELLINGS CHECK. KubeCommandBuilder injects `kind` into the body from the
        // GroupVersionKind it is handed, and all three renders write `kind` themselves — because ONE
        // Matches serves THREE kinds and a document with no kind would have to be guessed at from its
        // shape. The builder OVERWRITES, so a disagreement would be resolved silently in its favour:
        // the render would be judged as the wrong kind by Matches and applied as the right one, and
        // the resource would never converge with nothing saying why.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId));

        await Reconcile(connection, body.RootElement);

        foreach (var applied in connection.Applied) {
            JsonNode.Parse(applied.Body)!["kind"]!.GetValue<string>().ShouldBe(applied.Target.Kind.Kind);
        }
    }

    [Fact]
    public async Task ASecondPassWithTheSameBodyChangesNothing() {
        // Clause 1, and on this type it is not free: GenerateIngestKey returns a different value on
        // every call. What reaches the Secret is what the vault returns AFTER the mint-once write, so
        // the rendered document is byte-stable over a generator that is not. A reconciler that
        // rendered the candidate instead would produce a different Secret every pass, would never
        // converge, and would rotate the tenant's credential on every reminder.
        var connection = new RecordingConnection();
        var vault = new InMemorySecretVault();
        using var body = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId));

        await Reconcile(connection, body.RootElement, vault);
        var first = connection.Applied.Select(x => x.Body).ToArray();

        await Reconcile(connection, body.RootElement, vault);
        var second = connection.Applied.Skip(first.Length).Select(x => x.Body).ToArray();

        second.ShouldBe(first);
    }

    [Fact]
    public async Task AnUnreachableClusterSuspendsRatherThanFails() {
        // docs/plan/09 § Cluster connections. A tenant whose region is unreachable has a workspace
        // that is still coming, not one that broke.
        var connection = new RecordingConnection { Suspend = true };
        using var body = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId));

        (await Reconcile(connection, body.RootElement)).Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
    }

    [Fact]
    public async Task AConflictBecomesProgressRatherThanAFailureOrAForcedOverwrite() {
        // ADR-013 makes a conflict "a drift event with a name". ⚠ Forcing here would be worse than on
        // most types: a second manager owning part of a VMUser is somebody's emergency edit to
        // routing, and overwriting it every thirty seconds would make the emergency permanent.
        var connection = new RecordingConnection { ConflictField = ".spec.targetRefs" };
        using var body = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId));

        var outcome = await Reconcile(connection, body.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldContain("targetRefs");
    }

    [Fact]
    public async Task DeleteReportsConvergedOnlyOnceAllThreeObjectsAreGone() {
        var connection = new RecordingConnection();
        var vault = new InMemorySecretVault();
        using var body = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId));

        await Reconcile(connection, body.RootElement, vault);

        var reconciler = new MonitorWorkspaceReconciler(new FixedClock());
        var deleted = await reconciler.DeleteAsync(
            Context(connection, body.RootElement, vault),
            TestContext.Current.CancellationToken
        );

        deleted.ShouldBe(ReconcileOutcome.Converged);

        connection.Deleted.Select(x => x.Kind.Kind).ShouldBe(
            ["ConfigMap", "VMUser", "Secret"],
            "the row is withdrawn first, so the ingest host stops believing in the workspace before "
            + "its key and routing disappear underneath it"
        );
    }

    // ── Failure class (f), at the object: no credential is ever rendered where it should not be ──

    [Fact]
    public async Task TheIngestKeyIsInTheSecretAndInNothingElse() {
        // docs/plan/05: credentials never in grain state, and never in an object that is not a Secret.
        // ⚠ A ConfigMap is readable by anything with a namespace-scoped role and a VMUser is an
        // ordinary custom resource, so the key going into either would be the credential leaving the
        // one object Kubernetes protects. The VMUser NAMES the Secret through passwordRef; that is
        // the whole of what it may carry.
        var connection = new RecordingConnection();
        var vault = new InMemorySecretVault();
        using var body = JsonDocument.Parse(MonitorWorkspaces.Body(ClusterId));

        await Reconcile(connection, body.RootElement, vault);

        var key = await vault.ResolveAsync(
            MonitorWorkspaces.IngestKeyRef(Address("observed", TenantA, SubscriptionA, WorkspaceA)),
            TestContext.Current.CancellationToken
        );

        key.IsSuccess.ShouldBeTrue();

        foreach (var applied in connection.Applied.Where(x => x.Target.Kind.Kind != "Secret")) {
            applied.Body.ShouldNotContain(
                key.GetValueOrThrow(),
                Case.Sensitive,
                $"the live ingest key is rendered into a {applied.Target.Kind.Kind}"
            );
        }
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-00000000000c");
    static readonly Guid TenantA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid TenantB = Guid.Parse("44444444-4444-4444-8444-444444444444");
    static readonly Guid SubscriptionA = Guid.Parse("22222222-2222-4222-8222-222222222222");
    static readonly Guid SubscriptionB = Guid.Parse("55555555-5555-4555-8555-555555555555");
    static readonly Guid WorkspaceA = Guid.Parse("33333333-3333-4333-8333-333333333333");
    static readonly Guid WorkspaceB = Guid.Parse("66666666-6666-4666-8666-666666666666");

    static async Task<ReconcileOutcome> Reconcile(
        RecordingConnection connection,
        JsonElement desired,
        InMemorySecretVault? vault = null
    ) =>
        await new MonitorWorkspaceReconciler(new FixedClock())
            .ReconcileAsync(Context(connection, desired, vault), TestContext.Current.CancellationToken);

    static async Task<ReconcileOutcome> Pass(
        MonitorWorkspaceReconciler reconciler,
        RecordingConnection connection,
        InMemorySecretVault vault,
        ResourceId address,
        JsonElement desired
    ) =>
        await reconciler.ReconcileAsync(
            new(
                address,
                MonitorWorkspaces.V2026,
                desired,
                null,
                ReconcileDriver.NamespaceFor(address),
                connection,
                vault,
                new NullLog()
            ) {
                SecretWriter = vault
            },
            TestContext.Current.CancellationToken
        );

    /// <summary>
    ///     A pass over a working vault, which every clause assertion in this file needs.
    /// </summary>
    /// <remarks>
    ///     ⚠ The default is <b>not</b> <c>UnavailableSecretResolver</c>: this reconciler mints before
    ///     it applies, so a refusing writer makes every pass fail for a wiring reason and no clause is
    ///     exercised at all. <c>InMemorySecretVault</c> implements mint-once for real, so the
    ///     idempotence assertion is still measuring the reconciler.
    /// </remarks>
    static ReconcileContext Context(
        IKubeClusterConnection? connection,
        JsonElement desired,
        InMemorySecretVault? vault = null
    ) {
        var address = Address("observed", TenantA, SubscriptionA, WorkspaceA);
        var store = vault ?? new InMemorySecretVault();

        return new(
            address,
            MonitorWorkspaces.V2026,
            desired,
            null,
            ReconcileDriver.NamespaceFor(address),
            connection,
            store,
            new NullLog()
        ) {
            SecretWriter = store
        };
    }

    /// <summary>An address in a named tenant, its own subscription and its own GUID.</summary>
    static ResourceId Address(string name, Guid tenant, Guid subscription, Guid id) =>
        new(tenant, subscription, "prod", MonitorWorkspaces.Type, name, id);

    static JsonObject Data(string objectJson) =>
        JsonNode.Parse(objectJson)!["data"]!.AsObject();

    static string Suffix(string objectJson, int index) =>
        JsonNode.Parse(objectJson)!["spec"]!["targetRefs"]!.AsArray()[index]!["target_path_suffix"]!
            .GetValue<string>();
}

/// <summary>
///     A reconciler that <c>CheckNoHiddenState</c> passes and that is not stateless.
/// </summary>
/// <remarks>
///     The field is <see langword="readonly" />, so <c>CheckNoHiddenState</c> skips it, and the
///     dictionary it holds is mutable forever. This is the shape a per-tenant cache takes when
///     somebody adds one for performance, and the only test in the sibling file that would catch it is
///     the cross-tenant one.
/// </remarks>
sealed class ReconcilerWithAReadonlyCache : IResourceReconciler {
    readonly Dictionary<string, string> lastRendered = new(StringComparer.Ordinal);

    public ResourceTypeName Type => MonitorWorkspaces.Type;

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

/// <summary>A connection that records what it was asked to do and can be made to misbehave.</summary>
sealed class RecordingConnection : IKubeClusterConnection {
    /// <summary>What is in the "cluster", keyed by kind, namespace and name.</summary>
    public ConcurrentDictionary<string, string> Objects { get; } = new(StringComparer.Ordinal);

    /// <summary>Every command applied, in order.</summary>
    public List<KubeCommand> Applied { get; } = [];

    /// <summary>Every object deleted, in order.</summary>
    public List<ObjectRef> Deleted { get; } = [];

    /// <summary>Every object <i>read</i>, in order — clause 4's evidence.</summary>
    public List<ObjectRef> Read { get; } = [];

    /// <summary>Whether every apply answers <c>Suspended</c>.</summary>
    public bool Suspend { get; init; }

    /// <summary>The field another manager owns, or empty.</summary>
    public string ConflictField { get; init; } = string.Empty;

    /// <summary>Whether an apply reports success and stores nothing — the clause-4 trap.</summary>
    public bool SwallowApplies { get; init; }

    public Guid ClusterId => Guid.Parse("eeeeeeee-0000-4000-8000-00000000000c");

    public Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(command);
        Applied.Add(command);

        if (Suspend) {
            return Task.FromResult(
                Result<ApplyOutcome>.Success(
                    new() {
                        Result = ApplyResult.Suspended,
                        Target = command.Target,
                        Message = "We cannot reach your cluster; this will resume automatically."
                    }
                )
            );
        }

        if (ConflictField.Length > 0) {
            return Task.FromResult(
                Result<ApplyOutcome>.Success(
                    new() {
                        Result = ApplyResult.Conflict,
                        Target = command.Target,
                        Drift = new() {
                            Target = command.Target,
                            FieldManager = command.FieldManager,
                            Conflicts = [new() { Field = ConflictField, OwnedBy = "kubectl-edit" }]
                        }
                    }
                )
            );
        }

        if (!SwallowApplies) {
            Objects[Key(command.Target)] = command.Body;
        }

        return Task.FromResult(
            Result<ApplyOutcome>.Success(new() { Result = ApplyResult.Created, Target = command.Target })
        );
    }

    public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(target);
        Read.Add(target);

        return Task.FromResult(
            Objects.TryGetValue(Key(target), out var json)
                ? Result<KubeObject>.Success(new() { Ref = target, Json = json })
                : Result<KubeObject>.Failure(ErrorCode.ResourceNotFound, $"'{target}' is not here.")
        );
    }

    public Task<Result> DeleteAsync(
        KubeCommand command,
        CascadePolicy policy = CascadePolicy.Background,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(command);

        var removed = Objects.TryRemove(Key(command.Target), out _);

        if (removed) {
            Deleted.Add(command.Target);
        }

        return Task.FromResult(
            removed
                ? Result.Success
                : Result.Failure(ErrorCode.ResourceNotFound, $"'{command.Target}' is not here.")
        );
    }

    /// <summary>
    ///     ⚠ Keyed by kind, namespace AND name. The namespace is in it because the cross-tenant test
    ///     puts the same resource name in two tenants, which is the only shape in which one singleton
    ///     reconciler serving both can be caught mixing them. ⚠ The KIND is in it because this type
    ///     applies a <c>ConfigMap</c> and a <c>VMUser</c> that share a name — a key without it would
    ///     make the second apply overwrite the first and every read-back return the wrong document.
    /// </summary>
    internal static string Key(ObjectRef target) =>
        target.Kind.Kind + "/" + target.Namespace + "/" + target.Name;
}

/// <summary>A clock that does not move. Nothing here depends on time passing.</summary>
sealed class FixedClock : IClock {
    public DateTimeOffset UtcNow => new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>A log that drops everything. These tests assert outcomes, not progress.</summary>
sealed class NullLog : IReconcileLog {
    public void Report(string phase, string detail) { }

    public void Report(string phase, string detail, int percent) { }
}
