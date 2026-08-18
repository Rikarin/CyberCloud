using CyberCloud.Core.Time;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Terminal.Tests;

/// <summary>
///     The console reconciler against a connection that misbehaves in the ways a real cluster does.
/// </summary>
public sealed class ConsoleReconcilerTests {
    // ── Failure class (a): a readonly mutable field on the reconciler ─────────────────────────

    [Fact]
    public void TheReconcilerHoldsNoMutableState() {
        // Clause 2, checked structurally, in the cheap place. The conformance run checks it too; this
        // is the one that catches the field somebody adds in a hurry, before a suite has to boot.
        ReconcilerConformance.CheckNoHiddenState(new CloudConsoleReconciler(new FixedClock())).ShouldBeEmpty();
    }

    [Fact]
    public void TheStructuralCheckStillMissesAReadonlyMutableCache() {
        // ⚠ THE CALIBRATION FOR THE TEST ABOVE, AND IT IS WHY THE ONE BELOW EXISTS. CheckNoHiddenState
        // SKIPS readonly fields, because an injected dependency assigned once in a constructor is the
        // normal shape — so a `readonly Dictionary<,>` used as a per-pass cache passes it and is a
        // cross-tenant bug. Six sightings across six provider families.
        //
        // Without this assertion, "CheckNoHiddenState is empty" would read as "the reconciler holds no
        // state", which is a stronger claim than the check makes.
        ReconcilerConformance.CheckNoHiddenState(new ReconcilerWithAReadonlyCache()).ShouldBeEmpty(
            "the structural check has started catching readonly fields, so the cross-tenant test below "
            + "is no longer the only thing that can"
        );
    }

    [Fact]
    public async Task OneReconcilerInstanceServesTwoTenantsWithoutMixingThem() {
        // ⚠ THE TEST A SINGLE-TENANT TEST CANNOT BE, AND THE ONE THAT COVERS THE STRUCTURAL CHECK'S
        // BLIND SPOT. AddCyberCloudProvider registers a reconciler as a SINGLETON BY CONCRETE TYPE, so
        // in a real silo ONE instance serves every tenant in the process.
        //
        // ⚠ ON THIS TYPE THE FIELD THAT WOULD BE CACHED IS A SECURITY CONTROL. Every other family's
        // cross-tenant test asks whether tenant B got tenant A's replica count. Here the rendered
        // objects include a NetworkPolicy whose selectors carry the TENANT'S OWN GUID, so a reconciler
        // that remembered one would give tenant B a policy naming tenant A — which, the day namespaces
        // are labelled, is egress from B's shell into A's namespaces.
        var reconciler = new CloudConsoleReconciler(new FixedClock());

        // ⚠ THE SAME RESOURCE NAME IN BOTH TENANTS. Two tenants naming a console `shell` is the
        // ordinary case; the namespaces differ and nothing else does, so a reconciler that keyed
        // anything on the name alone would serve one of them the other's spec.
        var alice = Address("shell", TenantA, SubscriptionA);
        var bob = Address("shell", TenantB, SubscriptionB);

        var world = new RecordingConnection();

        // The two bodies differ in three independently rendered things: the home size (the claim), the
        // egress posture (the policy's rule count) and the principal (the service account's
        // annotation). A reconciler that kept one would be caught; one that kept a whole rendered
        // document would be caught three times over.
        using var aliceBody = JsonDocument.Parse(
            CloudConsoles.Body(ClusterId, principalId: PrincipalA, homeSize: "5Gi", egress: "Internet")
        );

        using var bobBody = JsonDocument.Parse(
            CloudConsoles.Body(ClusterId, principalId: PrincipalB, homeSize: "50Gi", egress: "TenantOnly")
        );

        // Interleaved on purpose: A, B, A. A reconciler that remembered anything from its first pass
        // would answer the third with B's values.
        await Pass(reconciler, world, alice, aliceBody.RootElement);
        await Pass(reconciler, world, bob, bobBody.RootElement);
        var third = await Pass(reconciler, world, alice, aliceBody.RootElement);

        third.IsConverged.ShouldBeTrue(third.ToString());

        var claims = Applied(world, "PersistentVolumeClaim");
        claims.Count.ShouldBe(3);
        Size(claims[0]).ShouldBe("5Gi");
        Size(claims[1]).ShouldBe("50Gi");
        Size(claims[2]).ShouldBe("5Gi");

        var accounts = Applied(world, "ServiceAccount");
        Principal(accounts[0]).ShouldBe(PrincipalA.ToString("D"));
        Principal(accounts[1]).ShouldBe(PrincipalB.ToString("D"));
        Principal(accounts[2]).ShouldBe(PrincipalA.ToString("D"));

        var policies = Applied(world, "NetworkPolicy");

        // Alice asked for Internet (four rules), Bob for TenantOnly (three).
        Egress(policies[0]).Count.ShouldBe(4);
        Egress(policies[1]).Count.ShouldBe(3, "tenant B asked for no public egress and got tenant A's");
        Egress(policies[2]).Count.ShouldBe(4);

        // ⚠ AND THE TENANT SELECTOR IS THE TENANT'S OWN. This is the assertion that makes the test a
        // security test rather than a rendering one.
        TenantSelector(policies[0]).ShouldBe(TenantA.ToString("D"));
        TenantSelector(policies[1]).ShouldBe(TenantB.ToString("D"));
        TenantSelector(policies[2]).ShouldBe(TenantA.ToString("D"));

        // And the two tenants' objects are in different namespaces, so the third pass read back
        // Alice's claim rather than Bob's.
        world.Applied[0].Target.Namespace.ShouldNotBe(world.Applied[3].Target.Namespace);
    }

    // ── The four clauses ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConvergedFollowsTheReadAndNotTheApply() {
        // ⚠ CLAUSE 4, isolated. The applies succeed and the reads find nothing — a reconciler that
        // believed its own apply would say Converged here.
        var connection = new RecordingConnection { SwallowApplies = true };
        using var desired = JsonDocument.Parse(CloudConsoles.Body(ClusterId));

        var outcome = await Reconcile(connection, desired.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.IsConverged.ShouldBeFalse();
    }

    [Fact]
    public async Task EveryAppliedObjectIsAlsoReadBack() {
        // ⚠ CLAUSE 4 OVER THE WHOLE SET, WHICH IS THE HALF A SINGLE-OBJECT ASSERTION MISSES. The
        // service account carries no tenant-facing setting, so it is the one a reconciler would most
        // plausibly apply and not read — and a console whose pod has no identity to run under either
        // does nothing or falls back to the namespace's `default` account.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(CloudConsoles.Body(ClusterId));

        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();

        foreach (var applied in connection.Applied) {
            connection.Read.ShouldContain(
                x => RecordingConnection.Key(x) == RecordingConnection.Key(applied.Target),
                $"'{applied.Target}' was applied and never read back"
            );
        }
    }

    [Fact]
    public async Task ASecondPassOnAConvergedConsoleWritesNothingNew() {
        // Clause 1. The applies are server-side, so a repeat is an Unchanged, and nothing here counts,
        // appends or timestamps.
        //
        // ⚠ THE TRAP THIS TYPE INVITES IS SPECIFIC: the two session numbers are DURATIONS, and the
        // obvious mistake is to render them as instants — `activeDeadlineSeconds` computed from a
        // clock would produce a different body on every pass and re-apply forever.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(CloudConsoles.Body(ClusterId));

        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();
        var first = connection.Applied.Select(x => x.Body).ToList();

        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();
        var second = connection.Applied.Skip(first.Count).Select(x => x.Body).ToList();

        second.ShouldBe(first, "a rendered object changed between two identical passes");
    }

    [Fact]
    public async Task ConvergedDoesNotWaitForTheHomeVolumeToBind() {
        // ⚠ THE DECISION THIS ROW'S `Converged` RESTS ON, ASSERTED SO IT CANNOT BE "TIGHTENED" LATER
        // BY SOMEBODY WHO HAS NOT READ WHY.
        //
        // The obvious readiness gate is `status.phase == "Bound"`. It deadlocks: k3s' default
        // StorageClass binds WaitForFirstConsumer, so a claim with no pod scheduled against it stays
        // Pending forever — and a console deliberately has no pod until somebody connects, and connect
        // refuses a console that has not converged. So converge would wait for the pod and the pod
        // would wait for converge.
        //
        // The world here has the claim with NO status at all, which is what a fresh apply looks like.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(CloudConsoles.Body(ClusterId));

        var outcome = await Reconcile(connection, desired.RootElement);

        outcome.IsConverged.ShouldBeTrue(outcome.ToString());

        var claim = connection.Objects[
            RecordingConnection.Key(CloudConsoles.HomeClaimRef(Namespace, "observed"))
        ];

        JsonNode.Parse(claim)!.AsObject().ContainsKey("status").ShouldBeFalse(
            "the harness supplied a status, so this test no longer proves convergence ignores one"
        );
    }

    [Fact]
    public async Task AnUnreachableClusterSuspendsRatherThanFails() {
        // docs/plan/09 § Cluster connections: an unreachable cluster suspends reconciles rather than
        // failing them. A tenant whose cluster is down has a console that is still coming, not one
        // that broke.
        var connection = new RecordingConnection { Suspend = true };
        using var desired = JsonDocument.Parse(CloudConsoles.Body(ClusterId));

        var outcome = await Reconcile(connection, desired.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldContain("cannot reach");
    }

    [Fact]
    public async Task AConflictBecomesProgressRatherThanAFailureOrAForcedOverwrite() {
        // ADR-013: a conflict is "a drift event with a name", not an error — and never a forced apply.
        // ⚠ On this type the plausible rival is a mutating admission policy editing security contexts,
        // which is exactly the kind of cluster a shell is deployed into. Forcing would take a field
        // back from it once per reminder, forever.
        var connection = new RecordingConnection { ConflictField = ".spec.template.spec.securityContext" };
        using var desired = JsonDocument.Parse(CloudConsoles.Body(ClusterId));

        var outcome = await Reconcile(connection, desired.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldContain(".spec.template.spec.securityContext");
        connection.Applied[0].Force.ShouldBeFalse("forcing would take a field another manager owns");
    }

    // ── Order, which on this type is the security argument ────────────────────────────────────

    [Fact]
    public async Task TheNetworkPolicyIsAppliedLastSoAHalfDoneConsoleCannotBeAttachedTo() {
        // ⚠ A pass that dies half way should leave a console with a home volume and NO WAY TO START A
        // SHELL, rather than a shell with no constraint. The order is what makes that true, and the
        // connect handler's refusal is what makes the window nonexistent rather than short.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(CloudConsoles.Body(ClusterId));

        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();

        connection.Applied.Select(x => x.Target.Kind.Kind).ShouldBe(
            ["PersistentVolumeClaim", "ServiceAccount", "NetworkPolicy"]
        );
    }

    [Fact]
    public async Task ATeardownRemovesTheShellFirstAndTheHomeVolumeLast() {
        // ⚠ THE REVERSE OF THE APPLY ORDER, AND NOT COSMETIC. Deleting a PersistentVolumeClaim a
        // running pod has mounted does not delete it: the claim gets a deletionTimestamp and sits
        // behind kubernetes.io/pvc-protection until the pod goes. A teardown that started with the
        // volume would return InProgress forever while a shell nobody could see kept it alive.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(CloudConsoles.Body(ClusterId));

        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();
        (await Connect(connection, desired.RootElement)).IsSuccess.ShouldBeTrue();

        var torn = await new CloudConsoleReconciler(new FixedClock())
            .DeleteAsync(Context(connection, desired.RootElement), TestContext.Current.CancellationToken);

        torn.IsConverged.ShouldBeTrue(torn.ToString());
        connection.Objects.ShouldBeEmpty();

        connection.Deleted.Select(x => x.Kind.Kind).ShouldBe(
            ["Pod", "NetworkPolicy", "ServiceAccount", "PersistentVolumeClaim"]
        );

        // ⚠ FOREGROUND. A background cascade returns as soon as the object is marked, so the read-back
        // could report "not found" for a shell whose container was still running — and a console that
        // stops being billed while somebody is still typing into it is the failure the read-back
        // exists to prevent.
        connection.Cascades.ShouldAllBe(x => x == CascadePolicy.Foreground);
    }

    [Fact]
    public async Task ATeardownDeletesTheShellEvenThoughNoReconcilePassEverAppliedOne() {
        // ⚠ THE ASYMMETRY, ASSERTED SO IT IS NOT READ AS AN OVERSIGHT. The create path never applies a
        // pod; the delete path must remove one, because the session handler may have started a shell
        // seconds ago and leaving it would be a pod holding a deleted console's identity.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(CloudConsoles.Body(ClusterId));

        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();

        connection.Applied.ShouldNotContain(
            x => x.Target.Kind.Kind == "Pod",
            "the reconciler applied a pod, which would fight the idle reclaim on every reminder"
        );

        (await Connect(connection, desired.RootElement)).IsSuccess.ShouldBeTrue();
        connection.Objects.Keys.ShouldContain(RecordingConnection.Key(CloudConsoles.PodRef(Namespace, "observed")));

        await new CloudConsoleReconciler(new FixedClock())
            .DeleteAsync(Context(connection, desired.RootElement), TestContext.Current.CancellationToken);

        connection.Objects.ShouldBeEmpty();
    }

    [Fact]
    public async Task ATeardownWithNoClusterIsConvergedRatherThanStuck() {
        // ⚠ The asymmetry with the create path, asserted so it is not read as an oversight. Failing
        // here would park the resource in Deleting — visible, billed and permanent — for a wiring
        // reason rather than a cluster one.
        using var desired = JsonDocument.Parse(CloudConsoles.Body(ClusterId));

        var torn = await new CloudConsoleReconciler(new FixedClock())
            .DeleteAsync(Context(null, desired.RootElement), TestContext.Current.CancellationToken);

        torn.IsConverged.ShouldBeTrue();
    }

    // ── Observing ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ObservingReportsWhatIsThereAndNeverApplies() {
        // docs/plan/08: ObserveAsync "must not apply anything — this runs on the drift path too, where
        // a write would turn a diff into a change." ⚠ On this type that rule has teeth: an observer
        // that ensured the pod existed would start a shell, with an identity, on the drift scanner's
        // schedule, for a console nobody had opened.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(CloudConsoles.Body(ClusterId));

        var reconciler = new CloudConsoleReconciler(new FixedClock());
        var address = Address("observed", TenantA, SubscriptionA);
        var ns = ReconcileDriver.NamespaceFor(address);

        var absent = await reconciler.ObserveAsync(
            new(address, CloudConsoles.V2026, desired.RootElement, ns, connection),
            TestContext.Current.CancellationToken
        );

        absent.Exists.ShouldBeFalse();
        connection.Applied.ShouldBeEmpty("observing applied something");

        await Reconcile(connection, desired.RootElement);
        var appliedByReconciling = connection.Applied.Count;

        var present = await reconciler.ObserveAsync(
            new(address, CloudConsoles.V2026, desired.RootElement, ns, connection),
            TestContext.Current.CancellationToken
        );

        present.Exists.ShouldBeTrue();
        connection.Applied.Count.ShouldBe(appliedByReconciling, "observing applied something");
    }

    [Fact]
    public async Task AConsoleWithNoShellRunningExistsRatherThanBeingAbsent() {
        // ⚠ THE ASSERTION THAT KEEPS THE DRIFT SCANNER OFF THIS TYPE'S BACK. An idle console has no
        // pod, and an observer that folded the pod into Exists would report every idle console as gone
        // — and the drift scanner, which reads this, would repair a resource working exactly as
        // designed.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(CloudConsoles.Body(ClusterId));

        await Reconcile(connection, desired.RootElement);

        var reconciler = new CloudConsoleReconciler(new FixedClock());
        var address = Address("observed", TenantA, SubscriptionA);

        var idle = await reconciler.ObserveAsync(
            new(address, CloudConsoles.V2026, desired.RootElement, ReconcileDriver.NamespaceFor(address), connection),
            TestContext.Current.CancellationToken
        );

        idle.Exists.ShouldBeTrue();
        idle.Summary.ShouldContain("no shell is running");

        (await Connect(connection, desired.RootElement)).IsSuccess.ShouldBeTrue();

        var attached = await reconciler.ObserveAsync(
            new(address, CloudConsoles.V2026, desired.RootElement, ReconcileDriver.NamespaceFor(address), connection),
            TestContext.Current.CancellationToken
        );

        attached.Summary.ShouldContain("a shell is running");
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────

    internal static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");
    internal static readonly Guid TenantA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    internal static readonly Guid TenantB = Guid.Parse("44444444-4444-4444-8444-444444444444");
    internal static readonly Guid SubscriptionA = Guid.Parse("22222222-2222-4222-8222-222222222222");
    internal static readonly Guid SubscriptionB = Guid.Parse("55555555-5555-4555-8555-555555555555");
    internal static readonly Guid PrincipalA = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000a");
    internal static readonly Guid PrincipalB = Guid.Parse("bbbbbbbb-0000-4000-8000-00000000000b");

    internal static string Namespace => ReconcileDriver.NamespaceFor(Address("observed", TenantA, SubscriptionA));

    internal static async Task<ReconcileOutcome> Reconcile(RecordingConnection connection, JsonElement desired) =>
        await new CloudConsoleReconciler(new FixedClock())
            .ReconcileAsync(Context(connection, desired), TestContext.Current.CancellationToken);

    internal static async Task<Result<string>> Connect(
        RecordingConnection connection,
        JsonElement desired,
        string action = CloudConsoles.ConnectAction
    ) {
        var address = Address("observed", TenantA, SubscriptionA);

        using var empty = JsonDocument.Parse("{}");

        return await new CloudConsoleSessionHandler().InvokeAsync(
            new(
                address,
                CloudConsoles.V2026,
                action,
                empty.RootElement,
                desired,
                ReconcileDriver.NamespaceFor(address),
                connection,
                new UnavailableSecretResolver()
            ),
            TestContext.Current.CancellationToken
        );
    }

    static async Task<ReconcileOutcome> Pass(
        CloudConsoleReconciler reconciler,
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired
    ) =>
        await reconciler.ReconcileAsync(
            new(
                address,
                CloudConsoles.V2026,
                desired,
                null,
                ReconcileDriver.NamespaceFor(address),
                connection,
                new UnavailableSecretResolver(),
                new NullLog()
            ),
            TestContext.Current.CancellationToken
        );

    internal static ReconcileContext Context(IKubeClusterConnection? connection, JsonElement desired) {
        var address = Address("observed", TenantA, SubscriptionA);

        return new(
            address,
            CloudConsoles.V2026,
            desired,
            null,
            ReconcileDriver.NamespaceFor(address),
            connection,
            // ⚠ A REFUSING RESOLVER, AND UNLIKE THE STORAGE PROVIDER THAT IS NOT A COMPROMISE. This
            // reconciler mints nothing and resolves nothing: a console's authority is the pod's
            // identity, which is a platform identity rather than a secret in a vault. A resolver that
            // worked would prove nothing here, and one that refuses proves the reconciler never
            // reaches for one.
            new UnavailableSecretResolver(),
            new NullLog()
        );
    }

    /// <summary>An address in a named tenant and its own subscription.</summary>
    internal static ResourceId Address(string name, Guid tenant, Guid subscription) =>
        new(
            tenant,
            subscription,
            "prod",
            CloudConsoles.Type,
            name,
            Guid.Parse("33333333-3333-4333-8333-333333333333")
        );

    static List<string> Applied(RecordingConnection world, string kind) =>
        world.Applied.Where(x => x.Target.Kind.Kind == kind).Select(x => x.Body).ToList();

    static string Size(string claimJson) =>
        JsonNode.Parse(claimJson)!["spec"]!["resources"]!["requests"]!["storage"]!.GetValue<string>();

    static string Principal(string accountJson) =>
        JsonNode.Parse(accountJson)!["metadata"]!["annotations"]![CloudConsoles.PrincipalAnnotation]!
            .GetValue<string>();

    internal static JsonArray Egress(string policyJson) =>
        JsonNode.Parse(policyJson)!["spec"]!["egress"]!.AsArray();

    static string TenantSelector(string policyJson) =>
        Egress(policyJson)
            .Select(x => x!["to"]![0]!["namespaceSelector"]?["matchLabels"]?[KubeLabels.TenantId])
            .First(x => x is not null)!
            .GetValue<string>();
}
