using System.Text.Json;

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

    Task<Result<WriteAccepted>> CloneAsync(string source, string name, CallerContext caller) =>
        cluster.Manager.ActionAsync(
            new() {
                Path = ResourceManagerCluster.Address(source).Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Post,
                Action = "clone",
                Body = $$"""{"name":"{{name}}"}""",
                Caller = caller
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
