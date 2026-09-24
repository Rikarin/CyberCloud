using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     <see cref="IResourceCreator" />: an action creates another resource through the whole write
///     path, as the action's caller — the seam #30's <c>recover</c> creates a PostgreSQL server
///     through.
/// </summary>
/// <remarks>
///     ⚠ <b>Through the real manager, never a double of it.</b> <c>clone</c> on
///     <c>CyberCloud.Testing/widgets</c> is a handler that does nothing but ask the creator, so what
///     these assert is <c>ResourceManagerService.CreateForActionAsync</c>: that the write is authorised
///     against the caller, that it is a real create with an operation that converges, and that an
///     existing name is refused rather than replaced.
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class ActionCreatesAsTheCallerTests(ResourceManagerCluster cluster) {
    [Fact]
    public async Task AnActionCreatesAResourceThatConvergesAndRecordsTheCallerAsItsAuthor() {
        ResourceManagerCluster.ResetDoubles();
        await CreateAsync("clone-source");

        var answer = await CloneAsync("clone-source", "clone-target", ResourceManagerCluster.Caller(subject: "bob"));
        answer.IsSuccess.ShouldBeTrue(answer.Error?.Message);

        using var response = JsonDocument.Parse(answer.GetValueOrThrow().ActionResponse);
        var operationId = Guid.Parse(response.RootElement.GetProperty("operationId").GetString()!);
        response.RootElement.GetProperty("resourceId").GetString()
            .ShouldBe(ResourceManagerCluster.Address("clone-target").Path);

        var status = await DriveAsync(operationId);
        status.State.ShouldBe(OperationState.Succeeded, status.Error?.Message);

        var read = await cluster.Manager.ReadAsync(
            new() {
                Path = ResourceManagerCluster.Address("clone-target").Path,
                ApiVersion = TestingProvider.V2026,
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        read.IsSuccess.ShouldBeTrue(read.Error?.Message);
        read.GetValueOrThrow().CreatedBy.ShouldContain("bob", Case.Sensitive, "the create is the caller's write, not the platform's");
        read.GetValueOrThrow().Body.ShouldContain("cloned");
    }

    [Fact]
    public async Task ACallerWhoMayActButMayNotWriteIsRefusedTheCreate() {
        ResourceManagerCluster.ResetDoubles();
        await CreateAsync("clone-guarded");

        // ⚠ The action's own permission and read, and NOT write: the vault's `recover` with a caller
        // who may use the vault and may not create a server.
        SwitchableAuthorizer.GrantOnly("clone", "read");

        var answer = await CloneAsync("clone-guarded", "clone-refused", ResourceManagerCluster.Caller());

        answer.IsFailure.ShouldBeTrue("an action created a resource its caller may not write");
        answer.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
        SwitchableAuthorizer.Asked.ShouldContain("write");

        SwitchableAuthorizer.Reset();
        (await cluster.Index(ResourceManagerCluster.Address("clone-refused")).ResolveAsync())
            .IsFailure.ShouldBeTrue("the refused create claimed the name anyway");
    }

    [Fact]
    public async Task TheCreateIsAuthorisedForTheCreatedTypeAndNotForTheActionsOwn() {
        ResourceManagerCluster.ResetDoubles();
        await CreateAsync("clone-typed");

        // ⚠ `write` on widgets and not on gauges. #30's review found the claim above tested only by
        // cloning a widget into a widget, against a double that granted by permission name alone, so a
        // create authorised against the ACTION's type would have passed. This caller may write the type
        // the action is on and may not write the type the action creates.
        SwitchableAuthorizer.GrantOnly("clone", "read", SwitchableAuthorizer.On(Widgets, "write"));

        var refused = await CloneAsync("clone-typed", "clone-typed-gauge", ResourceManagerCluster.Caller(), gauge: true);

        refused.IsFailure.ShouldBeTrue("an action created a gauge for a caller who may write widgets only");
        refused.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
        SwitchableAuthorizer.AskedOn.ShouldContain(SwitchableAuthorizer.On(TestingProvider.PeriodicTypeName, "write"));

        var allowed = await CloneAsync("clone-typed", "clone-typed-widget", ResourceManagerCluster.Caller());
        allowed.IsSuccess.ShouldBeTrue($"the same caller may create a widget: {allowed.Error?.Message}");

        SwitchableAuthorizer.Reset();
    }

    [Fact]
    public async Task APropertyOnlyAnActionMaySetIsRefusedOnACallersOwnWrite() {
        ResourceManagerCluster.ResetDoubles();

        // ── A caller's own PUT may not choose it: the shape of PUTting a server with somebody else's
        //    recovery point, which #30's review found went round the vault's recover entirely ───────
        var direct = await WriteGaugeAsync("origin-direct", TestingProvider.GaugeBody("stolen-point"), WriteVerb.Put);

        direct.IsFailure.ShouldBeTrue("a caller's own PUT set a property only an action may set");
        direct.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        direct.Error.Target.ShouldBe(TestingProvider.OriginPointer);
        (await cluster.Index(Gauge("origin-direct")).ResolveAsync()).IsFailure
            .ShouldBeTrue("the refused create claimed the name anyway");

        // The default is not a choice.
        var empty = await WriteGaugeAsync("origin-empty", TestingProvider.GaugeBody(""), WriteVerb.Put);
        empty.IsSuccess.ShouldBeTrue(empty.Error?.Message);

        // ── The action may ───────────────────────────────────────────────────────────────────────
        await CreateAsync("origin-source");
        var cloned = await CloneAsync("origin-source", "origin-restored", ResourceManagerCluster.Caller(), gauge: true, origin: "point-1");
        cloned.IsSuccess.ShouldBeTrue(cloned.Error?.Message);

        using (var response = JsonDocument.Parse(cloned.GetValueOrThrow().ActionResponse)) {
            var status = await DriveAsync(Guid.Parse(response.RootElement.GetProperty("operationId").GetString()!));
            status.State.ShouldBe(OperationState.Succeeded, status.Error?.Message);
        }

        // ── Afterwards a write may send it back, and may not change it ───────────────────────────
        var echoed = await WriteGaugeAsync("origin-restored", TestingProvider.GaugeBody("point-1", "cloned"), WriteVerb.Put);
        echoed.IsSuccess.ShouldBeTrue($"a client that read the resource and sent it back was refused: {echoed.Error?.Message}");

        var moved = await WriteGaugeAsync("origin-restored", TestingProvider.GaugeBody("point-2", "cloned"), WriteVerb.Put);
        moved.IsFailure.ShouldBeTrue("a PUT moved a property only an action may set");
        moved.Error!.Target.ShouldBe(TestingProvider.OriginPointer);

        var patched = await WriteGaugeAsync("origin-restored", """{"properties":{"origin":"point-2"}}""", WriteVerb.Patch);
        patched.IsFailure.ShouldBeTrue("a PATCH moved a property only an action may set");
        patched.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);

        var read = await cluster.Manager.ReadAsync(
            new() { Path = Gauge("origin-restored").Path, ApiVersion = TestingProvider.V2026, Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );
        read.GetValueOrThrow().Body.ShouldContain("point-1");
    }

    [Fact]
    public async Task AnExistingNameIsRefusedAndNeverReplaced() {
        ResourceManagerCluster.ResetDoubles();
        await CreateAsync("clone-over");
        var taken = await CreateAsync("clone-taken");

        var answer = await CloneAsync("clone-over", "clone-taken", ResourceManagerCluster.Caller());

        answer.IsFailure.ShouldBeTrue("a create replaced a resource that was already there");
        answer.Error!.Code.ShouldBe(ErrorCode.ResourceAlreadyExists);

        var after = await cluster.Resource(ResourceManagerCluster.Tenant, taken)
            .GetAsync(TestingProvider.V2026, TestingProvider.Pointers2026);
        after.GetValueOrThrow().Body.ShouldNotContain("cloned");
    }

    async Task<Guid> CreateAsync(string name) {
        var accepted = await cluster.Manager.WriteAsync(
            new() {
                Path = ResourceManagerCluster.Address(name).Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);
        (await DriveAsync(accepted.GetValueOrThrow().OperationId)).State.ShouldBe(OperationState.Succeeded);
        return accepted.GetValueOrThrow().Resource.Id;
    }

    static ResourceTypeName Widgets { get; } = new("CyberCloud.Testing", "widgets");

    Task<Result<WriteAccepted>> CloneAsync(
        string source,
        string name,
        CallerContext caller,
        bool gauge = false,
        string? origin = null
    ) {
        var body = new JsonObject { ["name"] = name };

        if (gauge) {
            body["gauge"] = true;
        }

        if (origin is not null) {
            body["origin"] = origin;
        }

        return cluster.Manager.ActionAsync(
            new() {
                Path = ResourceManagerCluster.Address(source).Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Post,
                Action = "clone",
                Body = body.ToJsonString(),
                Caller = caller
            },
            TestContext.Current.CancellationToken
        );
    }

    static ResourceId Gauge(string name) {
        var widget = ResourceManagerCluster.Address(name);
        return new(widget.TenantId, widget.SubscriptionId, widget.ResourceGroup, TestingProvider.PeriodicTypeName, name, Guid.Empty);
    }

    Task<Result<WriteAccepted>> WriteGaugeAsync(string name, string body, WriteVerb verb) =>
        cluster.Manager.WriteAsync(
            new() {
                Path = Gauge(name).Path,
                ApiVersion = TestingProvider.V2026,
                Verb = verb,
                Body = body,
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

    async Task<OperationStatus> DriveAsync(Guid operationId) {
        var operation = cluster.Operation(ResourceManagerCluster.Tenant, operationId);
        OperationStatus? last = null;

        for (var i = 0; i < 20; i++) {
            last = (await operation.DriveAsync()).GetValueOrThrow();

            if (last.IsTerminal) {
                return last;
            }
        }

        return last!;
    }
}
