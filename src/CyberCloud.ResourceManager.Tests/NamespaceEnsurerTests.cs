using CyberCloud.Core.Time;
using CyberCloud.Kubernetes.Contracts;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     The namespace every reconciler applies into — that something creates it, that it carries
///     ADR-013's seven labels, and that creating it does not cost a round trip per apply.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>What these prove and what only the AppHost suite can.</b> Everything here runs against
///         <see cref="RecordingConnection" />, which is a list. It shows the <i>command</i> the
///         ensurer builds — cluster-scoped, the right kind, all seven labels, no <c>spec</c> — and the
///         memo's arithmetic. It cannot show that a real API server accepts the body, that the labels
///         survive admission, or that <c>ReconcileDriver</c> actually calls this before a pass.
///         <c>CyberCloud.AppHost.Tests.ReconcileThroughTheRealHostTests</c> is where that is checked,
///         against k3s, with nothing in the test touching the namespace.
///     </para>
/// </remarks>
public sealed class NamespaceEnsurerTests {
    static Guid Tenant { get; } = new("2b6c1d3e-0000-4000-8000-000000000001");
    static Guid Subscription { get; } = new("2b6c1d3e-0000-4000-8000-000000000002");
    static Guid Cluster { get; } = new("2b6c1d3e-0000-4000-8000-000000000003");

    const string Group = "prod";

    static ResourceId Address { get; } =
        new(
            Tenant,
            Subscription,
            Group,
            new("CyberCloud.Testing", "widgets"),
            "w1",
            new("2b6c1d3e-0000-4000-8000-000000000004")
        );

    static string Namespace { get; } = ReconcileDriver.NamespaceFor(Address);

    // ── The write ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheCommandAddressesAClusterScopedNamespaceByTheDerivedName() {
        var (ensurer, connection) = Build();

        var ensured = (await EnsureAsync(ensurer, connection)).GetValueOrThrow();

        ensured.Written.ShouldBeTrue("the first call has nothing memoised and must reach the cluster.");
        connection.Applied.Count.ShouldBe(1);

        var command = connection.Applied[0];

        command.Target.IsClusterScoped.ShouldBeTrue(
            "a Namespace is cluster-scoped; a command carrying a namespace would be applied to "
            + "/api/v1/namespaces/{ns}/namespaces, which does not exist."
        );

        command.Target.Kind.Kind.ShouldBe("Namespace");
        command.Target.Kind.Plural.ShouldBe("namespaces");
        command.Target.Kind.IsCoreGroup.ShouldBeTrue();

        command.Target.Name.ShouldBe(
            Namespace,
            "the object's name must be ReconcileDriver.NamespaceFor's output. The builder's fallback "
            + "is the resource's own name, which for the group pseudo-resource is the group name "
            + "without the subscription prefix — a different namespace from the one every reconciler "
            + "applies into."
        );
    }

    [Fact]
    public async Task TheBodyCarriesNoSpec() {
        var (ensurer, connection) = Build();

        await EnsureAsync(ensurer, connection);

        using var document = JsonDocument.Parse(connection.Applied[0].Body);

        // ⚠ ABSENT, NOT NULL AND NOT EMPTY. A Namespace's spec holds `finalizers`, which the API
        // server and the namespace controller own. `"spec": null` in an apply patch is a deletion
        // instruction under server-side apply, and `"spec": {}` contests a field this platform has no
        // business holding.
        document.RootElement.TryGetProperty("spec", out _)
            .ShouldBeFalse("the namespace body declares a spec it has no value for.");

        document.RootElement.GetProperty("metadata").GetProperty("name").GetString().ShouldBe(Namespace);
    }

    [Fact]
    public async Task TheNamespaceCarriesAllSevenMandatoryLabels() {
        var (ensurer, connection) = Build();

        await EnsureAsync(ensurer, connection);

        var command = connection.Applied[0];

        foreach (var label in KubeLabels.Mandatory) {
            command.Labels.ShouldContainKey(
                label,
                $"the namespace is missing '{label}'. A namespace that exists and is unlabelled is "
                + "worse than one that does not exist: CloudConsoles' tenant-boundary NetworkPolicy "
                + "selects on cybercloud.io/tenant-id, so the isolation model degrades silently "
                + "instead of failing the reconcile."
            );
        }

        command.Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(Tenant));
        command.Labels[KubeLabels.SubscriptionId].ShouldBe(KubeLabels.GuidValue(Subscription));
        command.Labels[KubeLabels.ResourceGroup].ShouldBe(Group);
        command.Labels[KubeLabels.ManagedBy].ShouldBe(KubeLabels.ManagedByValue);
    }

    [Fact]
    public async Task TheNamespaceIsAttributedToTheGroupAndNotToTheResourceThatTriggeredIt() {
        var (ensurer, connection) = Build();

        await EnsureAsync(ensurer, connection);

        var command = connection.Applied[0];

        // ⚠ THE ASSERTION THAT REJECTS THE TEMPTING SHORTCUT. Stamping the triggering resource's id
        // would make the namespace's resource-id name a resource that can be deleted while the
        // namespace and everything else in it lives on, and DriftScanner's orphan join is on exactly
        // this label.
        command.Labels[KubeLabels.ResourceId].ShouldNotBe(
            KubeLabels.GuidValue(Address.Id),
            "the namespace is labelled with the id of whichever resource happened to reconcile first."
        );

        command.Labels[KubeLabels.ResourceId].ShouldBe(
            KubeLabels.GuidValue(NamespaceEnsurer.IdFor(Subscription, Group))
        );

        command.Labels[KubeLabels.ResourceType].ShouldBe(
            KubeLabels.ResourceTypeValue(NamespaceEnsurer.GroupType)
        );

        command.Labels[KubeLabels.ResourceId].ShouldNotBe(
            KubeLabels.GuidValue(Guid.Empty),
            "an empty resource-id folds every group's namespace into one drift finding."
        );
    }

    [Fact]
    public async Task EveryProviderWritesTheNamespaceUnderOneFieldManager() {
        var (ensurer, connection) = Build();

        await EnsureAsync(ensurer, connection);

        // ⚠ NOT cybercloud/{provider}. Twenty providers write this one object; per-provider managers
        // would co-own its labels under server-side apply and turn the next change to one of them
        // into a conflict for managers that never meant to own it.
        connection.Applied[0].FieldManager.ShouldBe(NamespaceEnsurer.FieldManager);
    }

    // ── The memo ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheSecondPassCostsNoRoundTrip() {
        var (ensurer, connection) = Build();

        await EnsureAsync(ensurer, connection);
        var second = (await EnsureAsync(ensurer, connection)).GetValueOrThrow();

        second.Written.ShouldBeFalse();

        connection.Applied.Count.ShouldBe(
            1,
            "a driver-side create runs on every apply of every resource in the group, so it must not "
            + "cost a read and a patch each time."
        );
    }

    [Fact]
    public async Task TheMemoIsPerClusterAndPerNamespace() {
        var (ensurer, connection) = Build();
        var other = new RecordingConnection(new("2b6c1d3e-0000-4000-8000-00000000000a"));

        await EnsureAsync(ensurer, connection);
        await EnsureAsync(ensurer, other);

        // ⚠ The same group on two clusters is two namespaces. A memo keyed by name alone would leave
        // the second cluster without one, which is the case a group whose resources name different
        // clusters produces.
        other.Applied.Count.ShouldBe(1);
    }

    [Fact]
    public async Task TheMemoExpires() {
        var clock = new MovableClock();
        var ensurer = new NamespaceEnsurer(clock);
        var connection = new RecordingConnection(Cluster);

        await EnsureAsync(ensurer, connection);
        clock.Advance(NamespaceEnsurer.RecheckAfter + TimeSpan.FromMinutes(1));
        await EnsureAsync(ensurer, connection);

        // A namespace deleted out of band must come back without a silo restart.
        connection.Applied.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ASuspendedClusterIsNotRemembered() {
        var (ensurer, connection) = Build();
        connection.Suspended = true;

        var suspended = (await EnsureAsync(ensurer, connection)).GetValueOrThrow();
        suspended.Result.ShouldBe(ApplyResult.Suspended);

        connection.Suspended = false;
        await EnsureAsync(ensurer, connection);

        // ⚠ Suspended means the write was never attempted. Memoising it would skip the namespace for
        // an hour after the cluster came back, and every apply in that hour would 404.
        connection.Applied.Count.ShouldBe(2);
    }

    [Fact]
    public async Task AConflictIsNotRemembered() {
        var (ensurer, connection) = Build();
        connection.Conflict = true;

        (await EnsureAsync(ensurer, connection)).GetValueOrThrow().Result.ShouldBe(ApplyResult.Conflict);

        connection.Conflict = false;
        await EnsureAsync(ensurer, connection);

        // The namespace exists, so the pass proceeds — but one of the seven labels is held by another
        // field manager, and the next pass looks again rather than trusting the label set.
        connection.Applied.Count.ShouldBe(2);
    }

    // ── The failure ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnApplyTheClusterRefusesFailsTheEnsureAndNamesTheNamespace() {
        var (ensurer, connection) = Build();
        connection.RefuseWith = ErrorCode.AuthorizationFailed;

        var refused = await EnsureAsync(ensurer, connection);

        refused.IsFailure.ShouldBeTrue(
            "an ensure that could not create the namespace must fail the pass. Letting the reconciler "
            + "run produces a 404 naming a namespace, in twenty reconcilers' words."
        );

        refused.Error!.Message.ShouldContain(Namespace);
        refused.Error.Code.ShouldBe(ErrorCode.AuthorizationFailed);

        // Not remembered, so the next pass tries again once an operator has fixed the credential.
        connection.RefuseWith = null;
        await EnsureAsync(ensurer, connection);
        connection.Applied.Count.ShouldBe(2);
    }

    // ── The derived id ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheGroupIdIsStableAcrossCallsAndDistinctPerGroupAndSubscription() {
        var first = NamespaceEnsurer.IdFor(Subscription, Group);

        first.ShouldBe(
            NamespaceEnsurer.IdFor(Subscription, Group),
            "the id is a label on an object two silos both apply; a value that differed between them "
            + "would make each write contest the other's."
        );

        first.ShouldNotBe(NamespaceEnsurer.IdFor(Subscription, "staging"));
        first.ShouldNotBe(NamespaceEnsurer.IdFor(Guid.NewGuid(), Group));
        first.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void TheGroupIdIsTheSameOnEveryMachine() =>
        // ⚠ A pinned value, because the derivation reads the hash big-endian and the Guid(byte[])
        // constructor does not. A regression there is invisible on one machine and produces two
        // labels for one group across a fleet.
        NamespaceEnsurer.IdFor(Subscription, Group)
            .ToString("D", CultureInfo.InvariantCulture)
            .ShouldBe("b23123a5-817d-896f-83f9-167e38bc9124");

    // ── The delete, and the four ways it must refuse ─────────────────────────────────────────────

    [Fact]
    public async Task AnEmptyNamespaceOfAnEmptyGroupIsDeleted() {
        var (ensurer, connection) = Build();

        var deleted = await DeleteAsync(ensurer, connection, Reclaim());

        deleted.IsSuccess.ShouldBeTrue(deleted.TryGetError(out var why) ? why.Message : string.Empty);
        connection.Deleted.Count.ShouldBe(1);

        var command = connection.Deleted[0].Command;

        command.Target.IsClusterScoped.ShouldBeTrue();
        command.Target.Kind.Kind.ShouldBe("Namespace");
        command.Target.Name.ShouldBe(Namespace);

        connection.Deleted[0].Policy.ShouldBe(
            CascadePolicy.Foreground,
            "a proven-empty namespace has no dependents, so Foreground costs nothing and makes "
            + "'deleted' mean gone rather than marked."
        );
    }

    [Fact]
    public async Task ANamespaceHoldingSomethingThisPlatformDidNotWriteIsNotDeleted() {
        // ⚠ THE SABOTAGE CASE. A tenant's own claim, in the platform's namespace, carrying none of
        // ADR-013's seven — which is also the exact shape of the volume a soft-deleted resource is
        // restored from, because a StatefulSet's volumeClaimTemplate is rendered without labels.
        var (ensurer, connection) = Build();

        var reclaim = Reclaim(
            occupants: [
                Occupant("PersistentVolumeClaim", "data-harbor-database-0", managed: false)
            ]
        );

        reclaim.Deletable.ShouldBeFalse();

        reclaim.OperatorReclaimable.ShouldBeTrue(
            "the platform holds nothing here and something else does, which is a person's decision "
            + "and not a sweeper's."
        );

        var refused = await DeleteAsync(ensurer, connection, reclaim);

        refused.TryGetError(out var error).ShouldBeTrue("a namespace with anything in it is not deletable.");
        error.Code.ShouldBe(ErrorCode.Conflict);
        error.Message.ShouldContain("data-harbor-database-0");
        error.Message.ShouldContain(KubeLabels.ManagedBy);

        connection.Deleted.ShouldBeEmpty("the refusal must happen before the API server is told anything.");
    }

    [Fact]
    public async Task ANamespaceHoldingOnlyObjectsThisPlatformWroteIsStillNotDeleted() {
        // The weaker rule — "delete when nothing lacks managed-by" — would fire here, and here is
        // where it takes a live resource's pods with it.
        var (ensurer, connection) = Build();

        var reclaim = Reclaim(occupants: [Occupant("StatefulSet", "harbor-core", managed: true)]);

        reclaim.Deletable.ShouldBeFalse();

        // ⚠ THIS LINE USED TO ASSERT FALSE, AND THE ASSERTION WAS THE NARROWING RATHER THAN A
        // REPORT OF IT. OperatorReclaimable required every occupant to be unmanaged, which was the
        // same question as "the platform is finished here" only while nothing this platform wrote
        // could outlive its resource. Once a volumeClaimTemplate's claims started carrying
        // managed-by, a group whose only remains were its own disks answered false to both verdicts
        // and reported as an unclassified refusal — the case an operator most needs told about,
        // filed as noise. The flag now means what its own remarks always said.
        reclaim.OperatorReclaimable.ShouldBeTrue(
            "the group holds no members and the namespace is not empty, so a person decides. Who "
            + "wrote the leftovers belongs in the refusal text, not in the verdict."
        );

        (await DeleteAsync(ensurer, connection, reclaim)).IsFailure.ShouldBeTrue();
        connection.Deleted.ShouldBeEmpty();
    }

    // ── What Kubernetes puts there by itself ─────────────────────────────────────────────────────

    [Fact]
    public async Task ANamespaceHoldingOnlyWhatKubernetesPutsInEveryNamespaceIsDeleted() {
        // ⚠ THE DEFECT THAT MADE Deletable UNREACHABLE IN PRODUCTION AND ALWAYS REACHABLE IN A
        // TEST. The rule was "the namespace holds nothing at all"; no conformant cluster can
        // satisfy it, because the service-account controller creates ServiceAccount/default in
        // every namespace and recreates it when deleted, and the root-CA publisher does the same
        // with ConfigMap/kube-root-ca.crt. It went unnoticed because INamespaceInventory had no
        // implementation, so the only occupant lists the rule was ever weighed against were the
        // empty ones this file supplies.
        var (ensurer, connection) = Build();

        var reclaim = Reclaim(
            occupants: [
                Occupant("ServiceAccount", "default", managed: false),
                Occupant("ConfigMap", "kube-root-ca.crt", managed: false),
                Occupant("Event", "harbor-core.17f2a", managed: false)
            ]
        );

        reclaim.Deletable.ShouldBeTrue(
            "these three are Kubernetes' own and nothing restores from one. " + reclaim.Explain()
        );

        reclaim.OperatorReclaimable.ShouldBeFalse("there is nothing here for a person to decide about.");

        (await DeleteAsync(ensurer, connection, reclaim)).IsSuccess.ShouldBeTrue();
        connection.Deleted.Count.ShouldBe(1);
    }

    [Fact]
    public void TheAmbientAllowanceIsByNameAndNotByKind() {
        // ⚠ THE SABOTAGE. `default` and `kube-root-ca.crt` are names Kubernetes reserves, so no
        // tenant object can wear one — but a kind-wide exemption for ServiceAccount or ConfigMap
        // would hide a tenant's own, which is the exact class of object this whole file refuses
        // over. A ConfigMap holding an application's configuration is not ambient.
        NamespaceReclaim.IsAmbient(Occupant("ServiceAccount", "default", managed: false)).ShouldBeTrue();
        NamespaceReclaim.IsAmbient(Occupant("ConfigMap", "kube-root-ca.crt", managed: false)).ShouldBeTrue();

        NamespaceReclaim.IsAmbient(Occupant("ServiceAccount", "harbor-core", managed: false)).ShouldBeFalse();
        NamespaceReclaim.IsAmbient(Occupant("ConfigMap", "harbor-config", managed: false)).ShouldBeFalse();

        // ⚠ NOT the auto-mounted token Secret, which Kubernetes stopped creating in 1.24. The only
        // rule that would match one is a name prefix, and a tenant can occupy a prefix — so an old
        // cluster that still has one reports as an occupant and a person decides.
        NamespaceReclaim.IsAmbient(Occupant("Secret", "default-token-x9f2b", managed: false)).ShouldBeFalse();
    }

    [Fact]
    public void OneTenantObjectAmongTheAmbientOnesIsStillARefusal() {
        var reclaim = Reclaim(
            occupants: [
                Occupant("ServiceAccount", "default", managed: false),
                Occupant("PersistentVolumeClaim", "data-harbor-database-0", managed: false)
            ]
        );

        reclaim.Deletable.ShouldBeFalse();
        reclaim.OperatorReclaimable.ShouldBeTrue();

        reclaim.Explain().ShouldContain("data-harbor-database-0");

        // An object nobody has to decide about does not belong in the sample a person reads, and
        // the count is of what is actually in the way — a refusal that says "2 objects" and names
        // one is arguing against itself.
        reclaim.Explain().ShouldNotContain("ServiceAccount/default");
        reclaim.Explain().ShouldContain("1 object");
    }

    [Fact]
    public async Task AGroupThatStillHoldsAMemberKeepsItsNamespaceEvenWhenTheNamespaceLooksEmpty() {
        // ⚠ This is the ordering, as an assertion. The namespace goes last: after every member has
        // gone, never alongside one. A member left Deleting because its teardown failed is the case
        // that matters — docs/plan/06 § Two-phase create keeps it listed precisely so that nothing
        // downstream treats it as finished.
        var (ensurer, connection) = Build();

        var reclaim = Reclaim(
            members: [
                new() {
                    ResourceId = Address.Id,
                    CanonicalPath = Address.CanonicalPath,
                    State = ProvisioningState.Deleting,
                    LastFailure = "the Harbor core StatefulSet still has a terminating pod",
                    TeardownAttempts = 3
                }
            ]
        );

        reclaim.Deletable.ShouldBeFalse();
        reclaim.OperatorReclaimable.ShouldBeFalse();

        var refused = await DeleteAsync(ensurer, connection, reclaim);

        refused.TryGetError(out var error).ShouldBeTrue();
        error.Message.ShouldContain("Deleting");
        connection.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task AVerdictAboutAnotherNamespaceDoesNotAuthorizeThisOne() {
        var (ensurer, connection) = Build();

        var elsewhere = NamespaceReclaim.Decide(Cluster, Namespace + "-staging", [], []);
        elsewhere.Deletable.ShouldBeTrue("that namespace is empty; this test is about which namespace.");

        var refused = await DeleteAsync(ensurer, connection, elsewhere);

        refused.TryGetError(out var error).ShouldBeTrue();
        error.Code.ShouldBe(ErrorCode.Conflict);
        connection.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task ADefaultVerdictAuthorizesNothing() {
        // The value a caller gets from `default`, an unassigned field, or a struct array. It must not
        // be a licence.
        var (ensurer, connection) = Build();

        default(NamespaceReclaim).Deletable.ShouldBeFalse();

        (await DeleteAsync(ensurer, connection, default)).IsFailure.ShouldBeTrue();
        connection.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeletingTheNamespaceDropsItsMemoSoTheNextPassRecreatesIt() {
        // ⚠ Without this the delete leaves a silo believing the namespace exists for the rest of
        // RecheckAfter, and every apply into it answers 404 for an hour.
        var (ensurer, connection) = Build();

        await EnsureAsync(ensurer, connection);
        connection.Applied.Count.ShouldBe(1);

        (await EnsureAsync(ensurer, connection)).GetValueOrThrow().Written.ShouldBeFalse();

        (await DeleteAsync(ensurer, connection, Reclaim())).IsSuccess.ShouldBeTrue();

        (await EnsureAsync(ensurer, connection)).GetValueOrThrow()
            .Written.ShouldBeTrue("the memo must not outlive the object it remembers.");

        connection.Applied.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ANamespaceThatIsAlreadyGoneIsASuccessfulDelete() {
        var (ensurer, connection) = Build();
        connection.DeleteRefusal = ErrorCode.ResourceNotFound;

        (await DeleteAsync(ensurer, connection, Reclaim())).IsSuccess.ShouldBeTrue(
            "absence is the goal, so a delete that finds nothing has reached it."
        );
    }

    [Fact]
    public async Task ARefusedDeleteKeepsTheClusterSCodeAndNamesTheNamespace() {
        var (ensurer, connection) = Build();
        connection.DeleteRefusal = ErrorCode.AuthorizationFailed;

        var failed = await DeleteAsync(ensurer, connection, Reclaim());

        failed.TryGetError(out var error).ShouldBeTrue();

        error.Code.ShouldBe(
            ErrorCode.AuthorizationFailed,
            "a credential that may not delete namespaces will not be able to an hour from now either; "
            + "re-coding it as retryable is the mistake NamespaceEnsurer.EnsureAsync already records."
        );

        error.Message.ShouldContain(Namespace);
    }

    [Fact]
    public async Task TheShippedNamespaceInventoryRefusesRatherThanReportingAnEmptyNamespace() {
        // ⚠ The one that would authorize a delete by saying nothing. Pinned here rather than left to
        // the composition root, because "returns empty" is the plausible stub and it is the fatal one.
        var listed = await new UnavailableNamespaceInventory()
            .ListAllAsync(Cluster, Namespace, TestContext.Current.CancellationToken);

        listed.TryGetError(out var error).ShouldBeTrue();
        error.Message.ShouldContain(Namespace);
    }

    [Fact]
    public async Task TheConnectionBackedInventoryRefusesWhenThereIsNoConnectionToTheCluster() {
        // ⚠ THE ONE-KEYSTROKE MISTAKE. `Connect` answering null and "this namespace is empty" are
        // the same shape at the call site and a recursive delete apart in effect, so the branch is
        // a failure rather than an empty array. NoClusterConnectionFactory is what every host that
        // has not wired connections registers, so this is also the shipped default's behaviour.
        var inventory = new ConnectionNamespaceInventory(new NoClusterConnectionFactory());

        var listed = await inventory.ListAllAsync(Cluster, Namespace, TestContext.Current.CancellationToken);

        listed.TryGetError(out var error).ShouldBeTrue();
        error.Code.ShouldBe(ErrorCode.ResourceNotFound);
        error.Message.ShouldContain(Namespace);
    }

    [Fact]
    public async Task TheConnectionBackedInventoryCarriesLabelsThroughUntouched() {
        // The reclaim's whole managed/unmanaged split is read off these, so a translation that
        // dropped them would report every occupant as somebody else's — safe, but it would also
        // make the refusal text say the opposite of what is true.
        var inventory = new ConnectionNamespaceInventory(new OneConnectionFactory(new ListingConnection(Cluster)));

        var occupants = (await inventory.ListAllAsync(
            Cluster,
            Namespace,
            TestContext.Current.CancellationToken
        )).GetValueOrThrow();

        occupants.Length.ShouldBe(2);
        occupants.Single(x => x.Name == "ours").IsManaged.ShouldBeTrue();
        occupants.Single(x => x.Name == "theirs").IsManaged.ShouldBeFalse();
        occupants.Single(x => x.Name == "ours").Kind.ShouldBe("PersistentVolumeClaim");
    }

    // ── The harness ──────────────────────────────────────────────────────────────────────────────

    static NamespaceReclaim Reclaim(
        IReadOnlyList<ResourceGroupMember>? members = null,
        ImmutableArray<NamespaceOccupant> occupants = default
    ) =>
        NamespaceReclaim.Decide(
            Cluster,
            Namespace,
            members ?? [],
            occupants.IsDefault ? [] : occupants
        );

    static NamespaceOccupant Occupant(string kind, string name, bool managed) =>
        new() {
            Kind = kind,
            Name = name,
            Labels = managed
                ? ImmutableDictionary<string, string>.Empty.Add(KubeLabels.ManagedBy, KubeLabels.ManagedByValue)
                : ImmutableDictionary<string, string>.Empty
        };

    static Task<Result> DeleteAsync(
        NamespaceEnsurer ensurer,
        RecordingConnection connection,
        NamespaceReclaim reclaim
    ) =>
        ensurer.DeleteAsync(
            NamespaceEnsurer.GroupAddress(Address),
            Namespace,
            connection,
            reclaim,
            TestContext.Current.CancellationToken
        );

    static (NamespaceEnsurer Ensurer, RecordingConnection Connection) Build() =>
        (new(new MovableClock()), new(Cluster));

    static Task<Result<NamespaceEnsured>> EnsureAsync(NamespaceEnsurer ensurer, RecordingConnection connection) =>
        ensurer.EnsureAsync(Address, Namespace, connection, TestContext.Current.CancellationToken);

    /// <summary>A clock a test moves by hand, private to this suite.</summary>
    /// <remarks>
    ///     ⚠ Deliberately not <c>TestClock.Instance</c>: that one is a static shared by the whole
    ///     Orleans fixture, and a suite that advanced it by an hour would move it under every other
    ///     class in the collection.
    /// </remarks>
    sealed class MovableClock : IClock {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 8, 19, 9, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    /// <summary>
    ///     An <see cref="IKubeClusterConnection" /> that records commands and answers however a test
    ///     tells it to.
    /// </summary>
    /// <remarks>
    ///     Local rather than <c>CyberCloud.Conformance</c>'s <c>FakeKubeCluster</c>, which this
    ///     project does not reference — see this class's own remarks for what a list can and cannot
    ///     show.
    /// </remarks>
    sealed class RecordingConnection(Guid cluster) : IKubeClusterConnection {
        readonly ConcurrentQueue<KubeCommand> applied = new();
        readonly ConcurrentQueue<(KubeCommand Command, CascadePolicy Policy)> deleted = new();

        public Guid ClusterId => cluster;

        public IReadOnlyList<KubeCommand> Applied => [.. applied];

        public bool Suspended { get; set; }

        public bool Conflict { get; set; }

        public ErrorCode? RefuseWith { get; set; }

        public Task<Result<ApplyOutcome>> ApplyAsync(
            KubeCommand command,
            CancellationToken cancellationToken = default
        ) {
            ArgumentNullException.ThrowIfNull(command);
            applied.Enqueue(command);

            if (RefuseWith is { } refusal) {
                return Task.FromResult(
                    Result<ApplyOutcome>.Failure(
                        refusal,
                        $"Cluster {cluster:D} refused to apply {command.Target}: namespaces is "
                        + "forbidden for the credential this connection holds."
                    )
                );
            }

            var result = Suspended
                ? ApplyResult.Suspended
                : Conflict
                    ? ApplyResult.Conflict
                    : ApplyResult.Created;

            return Task.FromResult(
                Result<ApplyOutcome>.Success(
                    new() {
                        Result = result,
                        Target = command.Target,
                        ReconcileHash = command.ReconcileHash,
                        Message = Conflict ? "cybercloud.io/tenant-id is owned by kubectl-edit" : string.Empty
                    }
                )
            );
        }

        public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Result<KubeObject>.Failure(ErrorCode.ResourceNotFound, $"{target} is not in this recorder.")
            );

        public ErrorCode? DeleteRefusal { get; set; }

        public IReadOnlyList<(KubeCommand Command, CascadePolicy Policy)> Deleted => [.. deleted];

        public Task<Result> DeleteAsync(
            KubeCommand command,
            CascadePolicy policy = CascadePolicy.Background,
            CancellationToken cancellationToken = default
        ) {
            ArgumentNullException.ThrowIfNull(command);
            deleted.Enqueue((command, policy));

            return Task.FromResult(
                DeleteRefusal is { } refusal
                    ? Result.Failure(refusal, $"Cluster {cluster:D} refused to delete {command.Target}.")
                    : Result.Success
            );
        }
    }

    /// <summary>A connection that answers one namespace listing and nothing else.</summary>
    /// <remarks>
    ///     ⚠ Separate from <see cref="RecordingConnection" />, which deliberately does <b>not</b>
    ///     override <c>ListNamespaceAsync</c>: the interface's default refuses, and every other test
    ///     in this file is about the delete rather than the listing, so inheriting the refusal is the
    ///     honest state for them.
    /// </remarks>
    sealed class ListingConnection(Guid cluster) : IKubeClusterConnection {
        public Guid ClusterId => cluster;

        public Task<Result<ApplyOutcome>> ApplyAsync(
            KubeCommand command,
            CancellationToken cancellationToken = default
        ) =>
            throw new NotSupportedException();

        public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> DeleteAsync(
            KubeCommand command,
            CascadePolicy policy = CascadePolicy.Background,
            CancellationToken cancellationToken = default
        ) =>
            throw new NotSupportedException();

        public Task<Result<IReadOnlyList<KubeObjectSummary>>> ListNamespaceAsync(
            string ns,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(
                Result<IReadOnlyList<KubeObjectSummary>>.Success(
                    [
                        new() {
                            Kind = new() {
                                Group = "",
                                Version = "v1",
                                Kind = "PersistentVolumeClaim",
                                Plural = "persistentvolumeclaims"
                            },
                            Namespace = ns,
                            Name = "ours",
                            Labels = new Dictionary<string, string>(StringComparer.Ordinal) {
                                [KubeLabels.ManagedBy] = KubeLabels.ManagedByValue
                            }
                        },
                        new() {
                            Kind = new() {
                                Group = "",
                                Version = "v1",
                                Kind = "PersistentVolumeClaim",
                                Plural = "persistentvolumeclaims"
                            },
                            Namespace = ns,
                            Name = "theirs",
                            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
                        }
                    ]
                )
            );
    }

    /// <summary>Hands out one connection, exactly as <c>GrainClusterConnectionFactory</c> does.</summary>
    sealed class OneConnectionFactory(IKubeClusterConnection connection) : IClusterConnectionFactory {
        public IKubeClusterConnection? Connect(Guid clusterId) =>
            clusterId == connection.ClusterId ? connection : null;
    }
}
