using CyberCloud.Core.Time;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Tests.Infrastructure;

/// <summary>
///     The world a test reconciler applies into, shared between the silo and the test.
/// </summary>
/// <remarks>
///     ⚠ <b>Static, and it has to be.</b> The reconcilers run inside the silo, which resolves its own
///     services; a test asserting "the delete path ran for what was already applied" has to see the
///     same world the reconciler wrote into. It is reset per test class through
///     <see cref="Reset" />.
///     <para>
///         Being a shared mutable singleton makes it exactly the clause-2 blind spot
///         <c>ReconcilerConformance</c>'s remarks name — a reconciler could keep state here and the
///         structural check would not find it. That is fine <i>for a test double</i> and is called out
///         so nobody copies the shape into a real provider.
///     </para>
/// </remarks>
public static class FakeWorld {
    /// <summary>What each resource has applied, keyed by resource GUID.</summary>
    public static ConcurrentDictionary<Guid, string> Applied { get; } = new();

    /// <summary>How many reconcile passes each resource has seen.</summary>
    public static ConcurrentDictionary<Guid, int> Passes { get; } = new();

    /// <summary>How many delete passes each resource has seen.</summary>
    public static ConcurrentDictionary<Guid, int> Deletes { get; } = new();

    /// <summary>Resources whose reconcile should report <c>InProgress</c> forever.</summary>
    public static ConcurrentDictionary<Guid, bool> StayInProgress { get; } = new();

    /// <summary>Resources whose reconcile should fail terminally.</summary>
    public static ConcurrentDictionary<Guid, string> FailWith { get; } = new();

    /// <summary>Resources whose <i>teardown</i> should fail, retryably.</summary>
    public static ConcurrentDictionary<Guid, string> FailTeardownWith { get; } = new();

    /// <summary>Forgets everything.</summary>
    public static void Reset() {
        Applied.Clear();
        Passes.Clear();
        Deletes.Clear();
        StayInProgress.Clear();
        FailWith.Clear();
        FailTeardownWith.Clear();
    }
}

/// <summary>
///     A conforming reconciler: idempotent, stateless, bounded, and it <b>observes</b>.
/// </summary>
/// <remarks>
///     ⚠ <b>Read <see cref="ReconcileAsync" />'s convergence test carefully — it is clause 4.</b> It
///     reports <see cref="ReconcileOutcome.Converged" /> only after reading
///     <see cref="FakeWorld.Applied" /> back and finding the desired body there. A reconciler that
///     remembered "I applied this" would pass every test except
///     <c>ReconcilerConformanceTests.TheSuiteRejectsAReconcilerThatAssumesInsteadOfObserving</c>,
///     which is why that test exists.
/// </remarks>
public sealed class ConformingReconciler(IClock clock) : IResourceReconciler {
    /// <summary>The type this test provider declares.</summary>
    public static ResourceTypeName TypeName { get; } = new("CyberCloud.Testing", "widgets");

    /// <inheritdoc />
    public ResourceTypeName Type => TypeName;

    /// <inheritdoc />
    public Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        FakeWorld.Passes.AddOrUpdate(context.Id.Id, 1, (_, count) => count + 1);

        if (FakeWorld.FailWith.TryGetValue(context.Id.Id, out var failure)) {
            context.Log.Report("applying", $"refused: {failure}");
            return Task.FromResult(ReconcileOutcome.Failed(ErrorCode.ProvisioningFailed, failure));
        }

        var desired = context.Desired.GetRawText();

        // The apply is idempotent: writing the same value twice is the same value.
        FakeWorld.Applied[context.Id.Id] = desired;
        context.Log.Report("applying", $"applied {desired.Length} bytes of desired state", 60);

        if (FakeWorld.StayInProgress.ContainsKey(context.Id.Id)) {
            return Task.FromResult(
                ReconcileOutcome.InProgress("waiting for 2 of 3 replicas to become ready", TimeSpan.FromSeconds(10))
            );
        }

        // ⚠ CLAUSE 4. Converged means READ BACK, not "the apply returned 200". The read is against
        // FakeWorld, which the conformance harness can empty behind this reconciler's back.
        if (!FakeWorld.Applied.TryGetValue(context.Id.Id, out var readBack)
            || !string.Equals(readBack, desired, StringComparison.Ordinal)) {
            return Task.FromResult(
                ReconcileOutcome.InProgress("the applied shape is not yet readable", TimeSpan.FromSeconds(5))
            );
        }

        context.Log.Report("ready", "the desired shape was read back", 100);
        return Task.FromResult(ReconcileOutcome.Converged);
    }

    /// <inheritdoc />
    public Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        FakeWorld.Deletes.AddOrUpdate(context.Id.Id, 1, (_, count) => count + 1);

        if (FakeWorld.FailTeardownWith.TryGetValue(context.Id.Id, out var failure)) {
            context.Log.Report("deleting", $"teardown failed: {failure}");
            return Task.FromResult(ReconcileOutcome.Failed(new Error(ErrorCode.ProvisioningFailed, failure), true));
        }

        FakeWorld.Applied.TryRemove(context.Id.Id, out _);

        // ⚠ Converged once the objects are GONE, read back — not once a delete was issued.
        if (FakeWorld.Applied.ContainsKey(context.Id.Id)) {
            return Task.FromResult(ReconcileOutcome.InProgress("objects are still terminating", TimeSpan.FromSeconds(5)));
        }

        context.Log.Report("deleted", "everything this resource applied is gone", 100);
        return Task.FromResult(ReconcileOutcome.Converged);
    }

    /// <inheritdoc />
    public Task<ObservedState> ObserveAsync(ObserveContext context, CancellationToken cancellationToken = default) {
        var exists = FakeWorld.Applied.TryGetValue(context.Id.Id, out var applied);

        return Task.FromResult(
            new ObservedState {
                Exists = exists,
                Json = applied ?? "{}",
                ObservedAt = clock.UtcNow,
                Summary = exists ? "applied" : "absent"
            }
        );
    }
}

/// <summary>
///     A reconciler that breaks every clause it can, so the conformance suite can be shown to reject
///     one.
/// </summary>
/// <remarks>
///     ⚠ <b>This exists to test the test.</b> A conformance suite that has only ever seen conforming
///     reconcilers has not been shown to reject anything —
///     <c>ReconcilerConformanceTests</c> runs the suite against this and asserts it finds clause 2
///     (the mutable field) and clause 4 (the remembered apply).
///     <para>
///         <see cref="applied" /> is the violation twice over: it is a mutable instance field, which
///         breaks when the grain moves silo (clause 2), and it is what
///         <see cref="ReconcileAsync" /> consults instead of reading the world back (clause 4).
///     </para>
/// </remarks>
public sealed class NonConformingReconciler : IResourceReconciler {
    // ⚠ THE VIOLATION. Not readonly, not a primary-constructor capture — a field somebody declared to
    // remember something between passes.
    bool applied;

    /// <inheritdoc />
    public ResourceTypeName Type => ConformingReconciler.TypeName;

    /// <inheritdoc />
    public Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (applied) {
            // ⚠ CLAUSE 4, VIOLATED. "I applied this, so it is there" — without looking.
            return Task.FromResult(ReconcileOutcome.Converged);
        }

        FakeWorld.Applied[context.Id.Id] = context.Desired.GetRawText();
        applied = true;

        return Task.FromResult(ReconcileOutcome.Converged);
    }

    /// <inheritdoc />
    public Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        FakeWorld.Applied.TryRemove(context.Id.Id, out _);
        applied = false;
        return Task.FromResult(ReconcileOutcome.Converged);
    }

    /// <inheritdoc />
    public Task<ObservedState> ObserveAsync(ObserveContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(
            new ObservedState { Exists = applied, Json = "{}", Summary = "remembered" }
        );
}

/// <summary>A reconciler that blows through clause 3's thirty-second budget.</summary>
public sealed class UnboundedReconciler : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => ConformingReconciler.TypeName;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        // Waits on the token so the harness's budget actually ends it — a Thread.Sleep would block the
        // test host for thirty seconds and prove the same thing thirty seconds more slowly.
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(ReconcileOutcome.Converged);

    /// <inheritdoc />
    public Task<ObservedState> ObserveAsync(ObserveContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(ObservedState.Absent);
}

/// <summary>Collects what a reconciler reported, for a test to read.</summary>
public sealed class RecordingReconcileLog : IReconcileLog {
    readonly List<(string Step, string Detail, int Percent)> entries = [];

    /// <summary>Everything reported.</summary>
    public IReadOnlyList<(string Step, string Detail, int Percent)> Entries => entries;

    /// <inheritdoc />
    public void Report(string phase, string detail) => Report(phase, detail, 0);

    /// <inheritdoc />
    public void Report(string phase, string detail, int percentComplete) =>
        entries.Add((phase, detail, percentComplete));
}

/// <summary>
///     The one provider the suite registers. Two api-versions, so the projection can be tested.
/// </summary>
/// <remarks>
///     ⚠ <b><c>2027-01-01</c> adds a field and does not change <c>2026-08-01</c>.</b> That is the
///     whole of docs/plan/08 § The provider registry's immutable-date rule as a fixture: a read at the
///     older date must keep getting the shape it was written against.
/// </remarks>
public sealed class TestingProvider : IResourceProvider {
    /// <summary>The api-version everything is written at unless a test says otherwise.</summary>
    public const string V2026 = "2026-08-01";

    /// <summary>The api-version that adds a field.</summary>
    public const string V2027 = "2027-01-01";

    /// <inheritdoc />
    public string ProviderNamespace => "CyberCloud.Testing";

    /// <summary>The 2026 schema — location, size, a label, and one secret.</summary>
    /// <remarks>
    ///     ⚠ <b><c>/properties/adminPassword</c> is the only <c>Secret</c> body property in the
    ///     codebase, and it is here because something has to prove the read path drops one.</b> Real
    ///     providers must not declare one — the write path stores the value in plaintext, which is why
    ///     <c>PostgresSecretTests</c> asserts <c>CyberCloud.DBforPostgreSQL/servers</c> has none. See
    ///     the remarks on <c>SchemaProperty</c>. A fixture is the one place the hazard is worth taking,
    ///     because <c>WritePathTests.ASecretPropertyIsNeverProjectedBackToTheCaller</c> is what would
    ///     notice if the drop stopped happening.
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new("/location", SchemaKind.Text, Required: true),
                new("/properties", SchemaKind.Nested),
                new("/properties/size", SchemaKind.WholeNumber, Required: true),
                new("/properties/label", SchemaKind.Text),
                new("/properties/adminPassword", SchemaKind.Text, Secret: true)
            ]
        );

    /// <summary>The 2027 schema — the same, plus <c>/properties/tier</c>.</summary>
    public static ResourceSchema Schema2027 { get; } =
        ResourceSchema.Of(
            [
                new("/location", SchemaKind.Text, Required: true),
                new("/properties", SchemaKind.Nested),
                new("/properties/size", SchemaKind.WholeNumber, Required: true),
                new("/properties/label", SchemaKind.Text),
                new("/properties/adminPassword", SchemaKind.Text, Secret: true),
                new("/properties/tier", SchemaKind.Text)
            ]
        );

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType("widgets")
            .ApiVersion(V2026, Schema2026)
            .ApiVersion(V2027, Schema2027)
            .Reconciler<ConformingReconciler>()
            .Meter(QuotaMeter.Vcpu, "/properties/size")
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Action("restart", ActionKind.Post, "write")
            .Action("listKeys", ActionKind.Post, "listKeys", secret: true)
            // ⚠ The one action with a declared request, and it is here so the write path's action-body
            // validation has something to refuse. An action that declares no request takes whatever it
            // is given, which is what `restart` and `listKeys` above still do — both branches are real.
            .Action(
                "resize",
                ActionKind.Post,
                "write",
                request: ResizeRequest,
                response: ResizeResponse,
                longRunning: true
            )
            .Display("Testing widget", "Testing widgets", shortName: "twidget")
            .SupportsSoftDelete(7)
            .SupportsTags()
            // ── The second type, and it exists for one reason ───────────────────────────────────
            //
            // ⚠ EVERY AMOUNT HERE IS A KUBERNETES QUANTITY STRING, WHICH IS WHAT A REAL MANAGED
            // SERVICE'S IS. `widgets` above meters `/properties/size`, a JSON number, and a number in
            // the body is the one shape no provider in the catalogue actually has: a Postgres server
            // spells its CPU "500m" and its disk "20Gi". Testing the quota path only against `widgets`
            // tested the case that does not occur.
            //
            // ⚠ NO RECONCILER, ON PURPOSE. ReconcileDriver converges a reconciler-less type on the
            // write, so a create here reaches commit and a delete reaches the quota return without any
            // FakeWorld choreography — which keeps these tests about the AMOUNTS and nothing else.
            .ResourceType(SizedType)
            .ApiVersion(V2026, SizedSchema)
            .Meter(QuotaMeter.Vcpu, VcpuDrawn)
            // ⚠ The cheap shape — one quantity at one pointer, no lambda — so both halves of the seam
            // are exercised by something.
            .Meter(QuotaMeter.StorageGb, MeterDerivation.Quantity(DiskPointer, QuantityUnit.Gibibytes))
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Display("Sized widget", "Sized widgets", shortName: "swidget")
            // ── The third type: a CHILD, and the only nested type in the codebase ────────────────
            //
            // ⚠ NO PROVIDER IN THE CATALOGUE DECLARES A NESTED TYPE YET. docs/plan/12 § The
            // catalogue names `servers/databases`, `servers/roles` and `servers/firewallRules` as
            // owed and src/Providers/README § What the third provider measured records why none has
            // shipped — so without this fixture the parent check would be code nothing can run.
            //
            // ⚠ IT DECLARES NOTHING ABOUT ITS PARENT, AND THAT IS THE POINT. docs/plan/12 § Child
            // resources interleaves the address — `/widgets/{widgetName}/gadgets/{gadgetName}` — so
            // the parent is a pure function of the id and there is nothing for a provider to declare,
            // get wrong, or forget. `ResourceId.Parent` is the whole mechanism.
            .ResourceType(ChildType)
            .ApiVersion(V2026, ChildSchema)
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Display("Gadget", "Gadgets", shortName: "gadget");
    }

    // ── The third type: a child of `widgets` ───────────────────────────────────────────────────

    /// <summary>The nested type's path. Its parent type is <c>widgets</c>.</summary>
    public const string ChildType = "widgets/gadgets";

    /// <summary>The nested type's name.</summary>
    public static ResourceTypeName ChildTypeName { get; } = new("CyberCloud.Testing", ChildType);

    /// <summary>The child's shape. Nothing in it names the parent — the address does.</summary>
    public static ResourceSchema ChildSchema { get; } =
        ResourceSchema.Of(
            [
                new("/location", SchemaKind.Text, Required: true),
                new("/properties", SchemaKind.Nested),
                new("/properties/label", SchemaKind.Text)
            ]
        );

    /// <summary>A body that satisfies <see cref="ChildSchema" />.</summary>
    public static string ChildBody(string label = "first") =>
        JsonSerializer.Serialize(
            new JsonObject {
                ["location"] = "eu-central",
                ["properties"] = new JsonObject { ["label"] = label }
            }
        );

    /// <summary>The pointer <see cref="SizedSchema" /> puts the disk quantity at.</summary>
    public const string DiskPointer = "/properties/disk";

    /// <summary>
    ///     vCPU for <see cref="SizedType" />: instances × the CPU quantity each one requests.
    /// </summary>
    /// <remarks>
    ///     ⚠ A product of two pointers, which is the thing a single-pointer meter cannot express and
    ///     the reason the seam is a function rather than a unit on a pointer.
    /// </remarks>
    public static MeterDerivation VcpuDrawn { get; } =
        MeterDerivation.Of(
            "replicas × cpu, in cores",
            ["/properties/replicas", "/properties/cpu"],
            body => MeterDerivation.TryQuantityAt(body, "/properties/cpu", QuantityUnit.Base, out var cores)
                && MeterDerivation.Resolve(body, "/properties/replicas") is { ValueKind: JsonValueKind.Number } count
                    ? Result<decimal>.Success(count.GetInt32() * cores)
                    : Result<decimal>.Failure(ErrorCode.InternalError, "cpu or replicas is not readable")
        );

    /// <summary>The shape a <c>POST …/resize</c> must satisfy.</summary>
    public static ResourceSchema ResizeRequest { get; } =
        ResourceSchema.Of(
            [
                new("/size", SchemaKind.WholeNumber, Required: true) { Minimum = 1, Maximum = 8 },
                new("/tier", SchemaKind.Text) { AllowedValues = ["basic", "standard"] }
            ]
        );

    /// <summary>What a <c>POST …/resize</c> returns.</summary>
    public static ResourceSchema ResizeResponse { get; } =
        ResourceSchema.Of([new("/accepted", SchemaKind.Boolean, Required: true)]);

    /// <summary>A body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="size">The size, which is also the vcpu quota draw.</param>
    /// <param name="label">The label.</param>
    public static string Body(int size = 2, string label = "first") =>
        JsonSerializer.Serialize(
            new JsonObject {
                ["location"] = "eu-central",
                ["properties"] = new JsonObject { ["size"] = size, ["label"] = label }
            }
        );

    /// <summary>The declared pointers of <see cref="Schema2026" />.</summary>
    public static ImmutableArray<string> Pointers2026 =>
        [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    // ── The second type: quantities, not numbers ───────────────────────────────────────────────

    /// <summary>The type path of the quantity-metered type.</summary>
    public const string SizedType = "sizedwidgets";

    /// <summary>The quantity-metered type's name.</summary>
    public static ResourceTypeName SizedTypeName { get; } = new("CyberCloud.Testing", SizedType);

    /// <summary>
    ///     The quantity-metered type's shape: instances, a CPU quantity, and an optional disk one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>/properties/disk</c> is OPTIONAL and that is the whole point of it.</b> A body may
    ///     legally leave it out, and the meter declared against it supplies no fallback — so a body that
    ///     validates can still be one whose storage draw is unknown. That is the shape of a meter that
    ///     silently stops metering: a pointer that stops resolving because a property was renamed, an
    ///     api-version moved, or the caller simply omitted it. Reserving zero, or one, would let the
    ///     resource provision unmetered; the write is refused instead, and
    ///     <c>MeteredAmountTests</c> is what holds that.
    /// </remarks>
    public static ResourceSchema SizedSchema { get; } =
        ResourceSchema.Of(
            [
                new("/location", SchemaKind.Text, Required: true),
                new("/properties", SchemaKind.Nested),
                new("/properties/replicas", SchemaKind.WholeNumber, Required: true) { Minimum = 1, Maximum = 9 },
                new("/properties/cpu", SchemaKind.Text, Required: true) { Pattern = QuantityPattern },
                new(DiskPointer, SchemaKind.Text) { Pattern = QuantityPattern }
            ]
        );

    /// <summary>
    ///     The Kubernetes quantity form, the same one <c>PostgresServers.QuantityPattern</c> declares.
    /// </summary>
    /// <remarks>
    ///     ⚠ Copied rather than referenced: a test fixture may not take a dependency on a provider, and
    ///     rule 2 of docs/plan/03 § Assembly graph rules forbids the edge in any case. If the two ever
    ///     disagree the platform has two quantity grammars, which is the defect
    ///     <c>KubeQuantity</c>'s remarks are about.
    /// </remarks>
    public const string QuantityPattern = @"\d+(\.\d+)?(m|k|M|G|T|P|E|Ki|Mi|Gi|Ti|Pi|Ei)?";

    /// <summary>A body that satisfies <see cref="SizedSchema" />.</summary>
    /// <param name="replicas">How many instances.</param>
    /// <param name="cpu">Each instance's CPU quantity.</param>
    /// <param name="disk">
    ///     Each resource's disk quantity, or <see langword="null" /> to omit the property entirely —
    ///     which is the case the storage meter refuses.
    /// </param>
    public static string SizedBody(int replicas = 2, string cpu = "500m", string? disk = "20Gi") {
        var properties = new JsonObject { ["replicas"] = replicas, ["cpu"] = cpu };

        if (disk is not null) {
            properties["disk"] = disk;
        }

        return JsonSerializer.Serialize(
            new JsonObject { ["location"] = "eu-central", ["properties"] = properties }
        );
    }
}
