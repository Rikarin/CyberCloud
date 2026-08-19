using CyberCloud.Core.Time;
using CyberCloud.Kubernetes.Contracts;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Concurrent;
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

    // ── The harness ──────────────────────────────────────────────────────────────────────────────

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

        public Task<Result> DeleteAsync(
            KubeCommand command,
            CascadePolicy policy = CascadePolicy.Background,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(
                Result.Failure(ErrorCode.InternalError, "The recorder is never asked to delete a namespace.")
            );
    }
}
