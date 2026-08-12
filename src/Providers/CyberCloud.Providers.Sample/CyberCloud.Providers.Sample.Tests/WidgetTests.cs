using CyberCloud.Core.Time;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using CyberCloud.ResourceManager.Registry;
using System.Collections.Concurrent;
using System.Text.Json;

namespace CyberCloud.Providers.Sample.Tests;

/// <summary>
///     What only this provider can be wrong about: its declaration, its schema, and its reconciler's
///     behaviour against a connection it does not control.
/// </summary>
/// <remarks>
///     The lifecycle — create, poll, read back, tag, lock, delete, drift — is
///     <c>CyberCloud.Providers.Sample.Conformance</c>'s, because it is the <i>shared</i> suite's
///     assertion. Repeating it here would be the per-provider copy docs/plan/03 § Providers warns
///     about.
/// </remarks>
public sealed class WidgetDeclarationTests {
    [Fact]
    public void TheProviderBuildsIntoARegistryTheWayASiloBuildsOneAtStart() {
        // ProviderRegistry.Build throws on a provider that declared nothing, on a duplicate namespace,
        // on a type with no api-version and on a reconciler naming a type nobody declared. Running it
        // is how a declaration mistake becomes a startup failure rather than a 404 with nothing in the
        // log.
        var registry = ProviderRegistry.Build([new SampleProvider()]);

        registry.Namespaces.ShouldContain(SampleWidgets.ProviderNamespace);
        registry.TryGetType(SampleWidgets.Type, out var registration).ShouldBeTrue();

        registration.ReconcilerType.ShouldBe(typeof(WidgetReconciler));
        registration.RequiresCluster.ShouldBeTrue();
        registration.SupportsTags.ShouldBeTrue();
        registration.ApiVersions.Length.ShouldBe(1);
        registration.TryGetAction("ping", out _).ShouldBeTrue();
    }

    [Fact]
    public void ATypeThatRequiresAClusterDeclaresThePropertyThatSuppliesOne() {
        // ⚠ THE LINK NOTHING ELSE CHECKS. RequiresCluster makes ReconcileDriver refuse a pass with no
        // connection; the connection comes from ClusterFrom(body), which reads /properties/clusterId.
        // Nothing in the platform ties the two together, so a type that declares one and not the other
        // fails once per resource at reconcile time instead of once at silo start. Asserted here so
        // this provider at least cannot be the one that does it.
        var registry = ProviderRegistry.Build([new SampleProvider()]);
        registry.TryGetType(SampleWidgets.Type, out var registration).ShouldBeTrue();

        registration.RequiresCluster.ShouldBeTrue();

        registration.ApiVersions[0].Schema.Declares("/properties/clusterId").ShouldBeTrue(
            "the type declares RequiresCluster and its schema does not declare /properties/clusterId, "
            + "which is the only pointer the write path reads a cluster id from"
        );
    }

    [Fact]
    public void TheDefaultFieldManagerIsTheOneAdr013Derives() {
        // ADR-013 wants "a stable field manager per provider", and the builder derives
        // cybercloud/{provider namespace, lower-cased}. The constant in .Contracts documents it; this
        // asserts the derivation still produces it, so a namespace rename cannot silently change the
        // manager and hand every field we own to a "new" manager on the next apply.
        var command = KubeCommand.For(new RecordingConnection())
            .WithTenantId(Guid.NewGuid())
            .WithResourceId(Address("field-manager"))
            .InNamespace("ns")
            .WithKind(SampleWidgets.ConfigMapKind)
            .ObjectJson("""{"metadata":{"name":"field-manager"},"data":{}}""")
            .Build();

        command.FieldManager.ShouldBe(SampleWidgets.FieldManager);
    }

    /// <remarks>
    ///     ⚠ The cluster id is a real GUID in every case here, and it did not have to be until the
    ///     schema could declare <c>SchemaFormat.Uuid</c>. A body whose only fault was meant to be a
    ///     missing <c>message</c> now also fails on <c>clusterId: "x"</c>, and the first problem is
    ///     the one in <see cref="Error.Target" /> — so the fixture has to be valid in every respect
    ///     but the one under test. That is the format check working, not the test being brittle.
    /// </remarks>
    [Theory]
    [InlineData("""{"properties":{"clusterId":"7b6a5c4d-0000-4000-8000-000000000001","message":"m"}}""", "/location")]
    [InlineData("""{"location":"eu-central","properties":{"message":"m"}}""", "/properties/clusterId")]
    [InlineData(
        """{"location":"eu-central","properties":{"clusterId":"7b6a5c4d-0000-4000-8000-000000000001"}}""",
        "/properties/message"
    )]
    public void EveryRequiredPropertyIsActuallyRequired(string body, string expectedTarget) {
        using var document = JsonDocument.Parse(body);

        var validated = SampleWidgets.Schema2026.Validate(document.RootElement);

        validated.IsFailure.ShouldBeTrue();
        validated.Error!.Target.ShouldBe(expectedTarget);
    }

    [Fact]
    public void TheBooleanIsRenderedAsAStringBecauseAConfigMapsDataValuesAreStrings() {
        // ⚠ The trap this exists for: a JSON boolean in a ConfigMap's `data` is rejected by the API
        // server with a type error that names `data` rather than the field, which is a slow bug to
        // read. The in-memory harness would never catch it, so it is asserted here.
        using var on = JsonDocument.Parse(SampleWidgets.Body(Guid.NewGuid(), "hi", enabled: true));
        using var off = JsonDocument.Parse(SampleWidgets.Body(Guid.NewGuid(), "hi", enabled: false));

        SampleWidgets.DataFor(on.RootElement)["enabled"].ShouldBe("true");
        SampleWidgets.DataFor(off.RootElement)["enabled"].ShouldBe("false");
        SampleWidgets.DataFor(on.RootElement)["message"].ShouldBe("hi");
    }

    [Fact]
    public void MatchesIsASubsetTestSoAnotherManagersKeysAreNotDrift() {
        // Server-side apply leaves other managers' keys in place. Demanding an exact match would turn
        // somebody else's annotation into a permanent drift report and a permanent re-apply loop.
        using var desired = JsonDocument.Parse(SampleWidgets.Body(Guid.NewGuid(), "hi"));

        SampleWidgets.Matches(
                """{"data":{"message":"hi","enabled":"true","addedByAnotherController":"x"}}""",
                desired.RootElement
            )
            .ShouldBeTrue();

        SampleWidgets.Matches("""{"data":{"message":"different","enabled":"true"}}""", desired.RootElement)
            .ShouldBeFalse();

        SampleWidgets.Matches("not json at all", desired.RootElement).ShouldBeFalse();
    }

    internal static ResourceId Address(string name) =>
        new(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            "prod",
            SampleWidgets.Type,
            name,
            Guid.Parse("33333333-3333-4333-8333-333333333333")
        );
}

/// <summary>
///     The reconciler against a connection that misbehaves in the ways a real cluster does.
/// </summary>
public sealed class WidgetReconcilerTests {
    [Fact]
    public void TheReconcilerHoldsNoMutableState() {
        // Clause 2, checked structurally. It is checked here as well as in the conformance run because
        // this is the check that costs nothing and catches the field somebody adds in a hurry.
        ReconcilerConformance.CheckNoHiddenState(new WidgetReconciler(new FixedClock())).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnUnreachableClusterSuspendsRatherThanFails() {
        // docs/plan/09 § Cluster connections: an unreachable cluster suspends reconciles rather than
        // failing them. A tenant whose cluster is down has a resource that is still coming, not one
        // that broke — and a Failed here would end the operation and strand a half-built resource.
        var connection = new RecordingConnection { Suspend = true };
        using var desired = JsonDocument.Parse(SampleWidgets.Body(Guid.NewGuid()));

        var outcome = await Reconcile(connection, desired.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldContain("cannot reach");
    }

    [Fact]
    public async Task AConflictBecomesProgressRatherThanAFailureOrAForcedOverwrite() {
        // ADR-013: a conflict is "a drift event with a name", not an error — and never a forced apply,
        // which would silently overwrite a tenant's own controller.
        var connection = new RecordingConnection { ConflictField = ".data.message" };
        using var desired = JsonDocument.Parse(SampleWidgets.Body(Guid.NewGuid()));

        var outcome = await Reconcile(connection, desired.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldContain(".data.message");
        connection.Applied.ShouldHaveSingleItem();
        connection.Applied[0].Force.ShouldBeFalse("forcing would take a field another manager owns");
    }

    [Theory]
    [InlineData(nameof(ErrorCode.PolicyViolation))]
    [InlineData(nameof(ErrorCode.InvalidRequestBody))]
    [InlineData(nameof(ErrorCode.InvalidResourceType))]
    [InlineData(nameof(ErrorCode.AuthorizationFailed))]
    public async Task ARefusedApplyIsTerminalRatherThanRetriedForAnHour(string code) {
        // ⚠ The four codes KubeFailures.Classify reports when the API server ANSWERED and said no: an
        // admission webhook or Pod Security decision, a body it will not type-check, a kind it does not
        // serve, and the platform's own credentials being refused. None of them is fixed by waiting, so
        // a retryable outcome buys the tenant sixty minutes on ReconcileSchedule's ladder and then an
        // OperationTimeout in place of the message the cluster gave on the first pass.
        ErrorCode.TryFromValue(code, out var refusal).ShouldBeTrue();

        var connection = new RecordingConnection { RefuseWith = refusal };
        using var desired = JsonDocument.Parse(SampleWidgets.Body(Guid.NewGuid()));

        var outcome = await Reconcile(connection, desired.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Retryable.ShouldBeFalse(outcome.ToString());
        outcome.IsTerminal.ShouldBeTrue("OperationGrain's terminal branch is `Failed when !Retryable`");
        outcome.Error!.Code.ShouldBe(refusal);
    }

    [Fact]
    public async Task AnApplyThatTheClusterNeverAnsweredIsStillRetryable() {
        // ⚠ The other half. ErrorCode.InternalError is what a transport fault comes back as, and it is
        // the commonest failure a reconciler sees — a terminal-by-default rule would end an operation
        // on a dropped connection, which is the mirror-image bug and the more expensive one.
        var connection = new RecordingConnection { RefuseWith = ErrorCode.InternalError };
        using var desired = JsonDocument.Parse(SampleWidgets.Body(Guid.NewGuid()));

        var outcome = await Reconcile(connection, desired.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Retryable.ShouldBeTrue(outcome.ToString());
        outcome.IsTerminal.ShouldBeFalse();
    }

    [Fact]
    public async Task ConvergedFollowsTheReadAndNotTheApply() {
        // ⚠ CLAUSE 4, isolated. The apply succeeds and the read finds nothing — a reconciler that
        // believed the apply would say Converged here.
        var connection = new RecordingConnection { SwallowApplies = true };
        using var desired = JsonDocument.Parse(SampleWidgets.Body(Guid.NewGuid()));

        var outcome = await Reconcile(connection, desired.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.IsConverged.ShouldBeFalse();
    }

    [Fact]
    public async Task ATeardownIsConvergedOnlyOnceTheObjectIsUnreadable() {
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(SampleWidgets.Body(Guid.NewGuid()));

        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();

        var reconciler = new WidgetReconciler(new FixedClock());
        var torn = await reconciler.DeleteAsync(Context(connection, desired.RootElement), TestContext.Current.CancellationToken);

        torn.IsConverged.ShouldBeTrue();
        connection.Objects.ShouldBeEmpty();
    }

    [Fact]
    public async Task ATeardownWithNoClusterIsConvergedRatherThanStuck() {
        // ⚠ The asymmetry with the create path, asserted so it is not read as an oversight. A teardown
        // with no cluster to reach has nothing left to remove, and failing would park the resource in
        // Deleting — visible, billed and permanent — for a wiring reason rather than a cluster one.
        using var desired = JsonDocument.Parse(SampleWidgets.Body(Guid.NewGuid()));

        var reconciler = new WidgetReconciler(new FixedClock());

        var torn = await reconciler.DeleteAsync(
            Context(null, desired.RootElement),
            TestContext.Current.CancellationToken
        );

        torn.IsConverged.ShouldBeTrue();
    }

    [Fact]
    public async Task ObservingReportsWhatIsThereAndNeverApplies() {
        // docs/plan/08 § The reconcile loop: ObserveAsync "must not apply anything — this runs on the
        // drift path too, where a write would turn a diff into a change."
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(SampleWidgets.Body(Guid.NewGuid()));

        var reconciler = new WidgetReconciler(new FixedClock());
        var address = WidgetDeclarationTests.Address("observed");

        // ⚠ The SAME namespace the reconcile pass used. ReconcileDriver derives it from the resource
        // id, and an observation that looked in a different namespace would report "absent" for a
        // resource that is there — which is a drift event, an orphan report and a re-apply.
        var ns = ReconcileDriver.NamespaceFor(address);

        var absent = await reconciler.ObserveAsync(
            new(address, SampleWidgets.V2026, desired.RootElement, ns, connection),
            TestContext.Current.CancellationToken
        );

        absent.Exists.ShouldBeFalse();
        connection.Applied.ShouldBeEmpty("observing applied something");

        await Reconcile(connection, desired.RootElement);

        var present = await reconciler.ObserveAsync(
            new(address, SampleWidgets.V2026, desired.RootElement, ns, connection),
            TestContext.Current.CancellationToken
        );

        present.Exists.ShouldBeTrue();
        present.Summary.ShouldContain("desired");
        connection.Applied.Count.ShouldBe(1, "observing applied something");
    }

    static async Task<ReconcileOutcome> Reconcile(RecordingConnection connection, JsonElement desired) =>
        await new WidgetReconciler(new FixedClock())
            .ReconcileAsync(Context(connection, desired), TestContext.Current.CancellationToken);

    static ReconcileContext Context(IKubeClusterConnection? connection, JsonElement desired) {
        var address = WidgetDeclarationTests.Address("observed");

        return new(
            address,
            SampleWidgets.V2026,
            desired,
            null,
            ReconcileDriver.NamespaceFor(address),
            connection,
            new UnavailableSecretResolver(),
            new NullLog()
        );
    }
}

/// <summary>A connection that records what it was asked to do and can be made to misbehave.</summary>
sealed class RecordingConnection : IKubeClusterConnection {
    /// <summary>What is in the "cluster".</summary>
    public ConcurrentDictionary<string, string> Objects { get; } = new(StringComparer.Ordinal);

    /// <summary>Every command applied.</summary>
    public List<KubeCommand> Applied { get; } = [];

    /// <summary>Whether every apply answers <c>Suspended</c>.</summary>
    public bool Suspend { get; init; }

    /// <summary>The field another manager owns, or empty.</summary>
    public string ConflictField { get; init; } = string.Empty;

    /// <summary>Whether an apply reports success and stores nothing — the clause-4 trap.</summary>
    public bool SwallowApplies { get; init; }

    /// <summary>
    ///     The code every apply fails with, or <see langword="null" /> — the API server <i>refusing</i>,
    ///     as <c>KubeFailures.Classify</c> reports one, rather than being out of reach.
    /// </summary>
    public ErrorCode? RefuseWith { get; init; }

    public Guid ClusterId => Guid.Empty;

    public Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(command);
        Applied.Add(command);

        if (RefuseWith is { } refusal) {
            return Task.FromResult(
                Result<ApplyOutcome>.Failure(
                    refusal,
                    $"Cluster {ClusterId:D} refused to apply {command.Target}. The object was not written."
                )
            );
        }

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

        return Task.FromResult(
            Objects.TryRemove(Key(command.Target), out _)
                ? Result.Success
                : Result.Failure(ErrorCode.ResourceNotFound, $"'{command.Target}' is not here.")
        );
    }

    static string Key(ObjectRef target) => target.Namespace + "/" + target.Name;
}

/// <summary>A clock that does not move. Nothing here depends on time passing.</summary>
sealed class FixedClock : IClock {
    public DateTimeOffset UtcNow => new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>A log that drops everything. These tests assert outcomes, not progress.</summary>
sealed class NullLog : IReconcileLog {
    public void Report(string phase, string detail) { }

    public void Report(string phase, string detail, int percentComplete) { }
}
