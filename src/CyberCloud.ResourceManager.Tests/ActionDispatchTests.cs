using CyberCloud.ResourceManager.Tests.Infrastructure;
using System.Text.Json;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     The action path, end to end through the manager: a handler runs, its result comes back, and
///     the value it carries goes to exactly one place.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every assertion here was unreachable before the handler seam existed.</b>
///         <c>IProviderBuilder.Action</c> took no handler and <c>OperationKind.Action</c> was written
///         by <c>ResourceManagerService</c> and read by nothing, so a <c>POST</c> answered <c>202</c>
///         and <c>OperationGrain</c> re-ran the resource type's <i>reconciler</i> — twelve declared
///         actions across nine provider namespaces, none of which could execute.
///     </para>
///     <para>
///         ⚠ <b>The containment assertions are the ones worth reading first.</b> An action's obvious
///         result channel is the operation it starts, and that channel is durable and readable by
///         anyone holding <c>read</c> on the resource — while <c>listKeys</c> deliberately checks a
///         permission that is not <c>read</c>. Putting the credential there would defeat the
///         permission split without touching the permission.
///     </para>
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class ActionDispatchTests(ResourceManagerCluster cluster) {
    // ── The seam works at all ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ASynchronousActionRunsItsHandlerAndReturnsTheResult() {
        ResourceManagerCluster.ResetDoubles();
        RestartHandler.Reset();

        var address = ResourceManagerCluster.Address("action-runs");
        await Create(address);

        var before = RestartHandler.Invocations;
        var action = await Invoke(address, "restart");

        action.IsSuccess.ShouldBeTrue(action.Error?.Message);
        RestartHandler.Invocations.ShouldBe(before + 1, "the handler was never reached");

        action.GetValueOrThrow().Completed.ShouldBeTrue(
            "a synchronous action answers 200 with its own body; Completed is what the gateway keys "
            + "its status code on"
        );

        action.GetValueOrThrow().ActionResponse.ShouldContain("restarted");
    }

    [Fact]
    public async Task ADeclaredActionWithNoHandlerRefusesAndNamesItself() {
        // ⚠ THE STATE EVERY ACTION IN THE CATALOGUE WAS IN, PRESERVED AS A REAL BRANCH. `orphaned` is
        // declared, reaches the generated document, and can be POSTed — and the honest answer is a
        // refusal naming the action rather than a 202 for an operation that re-runs a reconciler.
        ResourceManagerCluster.ResetDoubles();

        var address = ResourceManagerCluster.Address("action-orphaned");
        await Create(address);

        var action = await Invoke(address, "orphaned");

        action.IsFailure.ShouldBeTrue();
        action.Error!.Message.ShouldContain("orphaned");
        action.Error.Message.ShouldContain("handler");

        // ⚠ A 500 and not a 4xx: the caller POSTed something this platform publishes, so the gap is
        // ours and a 4xx would send them looking at their own request.
        action.Error.Code.ShouldBe(ErrorCode.InternalError);
    }

    // ── Failure class (a): the secret reaches exactly one of two places ────────────────────────

    [Fact]
    public async Task ASecretActionsValueComesBackInTheResponse() {
        ResourceManagerCluster.ResetDoubles();

        var address = ResourceManagerCluster.Address("action-secret-response");
        await Create(address);

        var action = await Invoke(address, "listKeys");

        action.IsSuccess.ShouldBeTrue(action.Error?.Message);
        action.GetValueOrThrow().ActionResponse.ShouldContain(ListKeysHandler.Secret);
    }

    [Fact]
    public async Task ASecretActionStartsNoOperationAtAll() {
        // ⚠ FAILURE CLASS (a), AND NOT STARTING AN OPERATION IS WHAT GUARANTEES IT RATHER THAN
        // REMEMBERS IT. OperationSpec and OperationStatus are durable and are served to any caller
        // who can READ the resource; listKeys checks its own permission precisely because `read` is
        // not enough for a key export. A result on the operation would hand the credential to every
        // reader and write it into the durable tier — so the operation does not exist.
        ResourceManagerCluster.ResetDoubles();

        var address = ResourceManagerCluster.Address("action-no-operation");
        await Create(address);

        var action = await Invoke(address, "listKeys");
        var accepted = action.GetValueOrThrow();

        accepted.OperationId.ShouldBe(
            Guid.Empty,
            "a synchronous action must not advertise an operation; the id would be a polling URL "
            + "that answers 404, and a real one would be a durable row the credential could reach"
        );

        accepted.OperationUri.ShouldBeEmpty();

        // And nothing is there to poll even if a client invented the id.
        var status = await cluster.For(ResourceManagerCluster.Tenant)
            .GetGrain<IOperationGrain>(GrainKeys.Operation(accepted.OperationId))
            .GetAsync();

        status.IsFailure.ShouldBeTrue("an operation grain exists for an action that started none");
    }

    [Fact]
    public async Task NoOperationAnywhereInTheTenantCarriesTheSecret() {
        // The other direction: rather than trusting that one id is empty, drive the action and then
        // read back the operation the CREATE started — the one durable operation this resource has —
        // and assert the credential is nowhere in it. A handler that logged its result onto the live
        // operation through IOperationGrain.ReportAsync would land exactly here.
        ResourceManagerCluster.ResetDoubles();

        var address = ResourceManagerCluster.Address("action-secret-leak");
        var created = await Create(address);
        var operationId = created.GetValueOrThrow().OperationId;

        (await Invoke(address, "listKeys")).IsSuccess.ShouldBeTrue();

        var status = await cluster.For(ResourceManagerCluster.Tenant)
            .GetGrain<IOperationGrain>(GrainKeys.Operation(operationId))
            .GetAsync();

        status.IsSuccess.ShouldBeTrue();

        var rendered = JsonSerializer.Serialize(status.GetValueOrThrow());

        // ⚠ AND THE ACTION'S OWN OPERATION IF IT EVER STARTS ONE. Today it does not — the assertion
        // above this one pins that — but the two together are what make this suite catch the leak in
        // BOTH shapes: a result appended onto the resource's live operation, and a result carried on
        // an operation the action started for itself. A sabotage run that put the credential on the
        // action's own spec passed this test until this block existed.
        var invoked = (await Invoke(address, "listKeys")).GetValueOrThrow();

        if (invoked.OperationId != Guid.Empty) {
            var own = await cluster.For(ResourceManagerCluster.Tenant)
                .GetGrain<IOperationGrain>(GrainKeys.Operation(invoked.OperationId))
                .GetAsync();

            if (own.IsSuccess) {
                rendered += JsonSerializer.Serialize(own.GetValueOrThrow());
            }
        }

        rendered.ShouldNotContain(
            ListKeysHandler.Secret,
            Case.Sensitive,
            "the credential reached the operation's public status, which is durable and is readable "
            + "by anyone holding `read` on the resource — a permission listKeys deliberately does not "
            + "use"
        );
    }

    [Fact]
    public async Task TheResourceSnapshotAnActionReturnsDoesNotCarryTheSecret() {
        // The action's reply carries the resource alongside the action body, and the resource is
        // projected from grain state. If a handler had written the credential into desired state on
        // its way past, this is where it would surface.
        ResourceManagerCluster.ResetDoubles();

        var address = ResourceManagerCluster.Address("action-snapshot");
        await Create(address);

        var accepted = (await Invoke(address, "listKeys")).GetValueOrThrow();

        JsonSerializer.Serialize(accepted.Resource)
            .ShouldNotContain(ListKeysHandler.Secret, Case.Sensitive);
    }

    // ── Failure class (c): the handler's shape against the declared schema ─────────────────────

    [Fact]
    public async Task AHandlerWhoseShapeDriftsFromItsDeclaredResponseIsRefused() {
        // ⚠ NOTHING ELSE CATCHES THIS. A handler returns JSON text and a response schema is data, so
        // a handler that stopped returning `/secretAccessKey` would compile, run, answer 200, and
        // make the OpenAPI document — and every SDK generated from it — a lie. The dispatcher runs
        // the same ResourceSchema.Validate a resource body goes through.
        ResourceManagerCluster.ResetDoubles();
        ListKeysHandler.Reset();

        var address = ResourceManagerCluster.Address("action-shape-drift");
        await Create(address);

        (await Invoke(address, "listKeys")).IsSuccess.ShouldBeTrue("the honest shape must pass");

        ListKeysHandler.ReturnTheWrongShape = true;

        try {
            var drifted = await Invoke(address, "listKeys");

            drifted.IsFailure.ShouldBeTrue(
                "a handler returning a body its provider never declared was accepted"
            );

            drifted.Error!.Code.ShouldBe(ErrorCode.InternalError);
            drifted.Error.Message.ShouldContain("listKeys");
        } finally {
            ListKeysHandler.Reset();
        }
    }

    [Fact]
    public async Task AShapeRefusalDoesNotQuoteTheBodyItRefused() {
        // ⚠ The diagnostics are a leak path too. A validation message that quoted the offending body
        // to explain what was wrong would put a secret: true action's credential into whatever logged
        // the error — the same value the whole path is careful about, reached sideways.
        ResourceManagerCluster.ResetDoubles();
        ListKeysHandler.ReturnTheWrongShape = true;

        try {
            var address = ResourceManagerCluster.Address("action-refusal-hygiene");
            await Create(address);

            var drifted = await Invoke(address, "listKeys");

            drifted.IsFailure.ShouldBeTrue();
            drifted.Error!.Message.ShouldNotContain(ListKeysHandler.KeyId, Case.Sensitive);
        } finally {
            ListKeysHandler.Reset();
        }
    }

    // ── The long-running branch is unchanged, and that is deliberate ───────────────────────────

    [Fact]
    public async Task ALongRunningActionStillAnswersWithAnOperationToPoll() {
        // ⚠ `resize` is longRunning and names no handler, because the platform cannot run one for a
        // long-running action — ProviderBuilder.Action refuses the combination rather than accepting
        // a handler it would silently ignore. What is owed is OperationGrain driving a handler for
        // OperationKind.Action instead of the type's reconciler.
        ResourceManagerCluster.ResetDoubles();

        var address = ResourceManagerCluster.Address("action-long-running");
        await Create(address);

        var action = await cluster.Manager.ActionAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Post,
                Action = "resize",
                Body = """{"size":4}""",
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        action.IsSuccess.ShouldBeTrue(action.Error?.Message);

        var accepted = action.GetValueOrThrow();

        accepted.Completed.ShouldBeFalse();
        accepted.OperationId.ShouldNotBe(Guid.Empty);
        accepted.ActionResponse.ShouldBeEmpty();
    }

    Task<Result<WriteAccepted>> Invoke(ResourceId address, string action) =>
        cluster.Manager.ActionAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Post,
                Action = action,
                Body = "{}",
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

    Task<Result<WriteAccepted>> Create(ResourceId address) =>
        cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );
}
