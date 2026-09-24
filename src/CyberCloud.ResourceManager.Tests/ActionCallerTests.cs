namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     A synchronous action's handler is handed the caller the request carried, through the real
///     manager and the real dispatcher.
/// </summary>
/// <remarks>
///     ⚠ <b>A handler that re-checks access asks about this caller, so a caller lost on the way is a
///     handler that asks about nobody.</b> <c>BudgetStatusHandler</c> refuses an empty caller, which
///     turns a dropped caller into a <c>403</c> for everyone rather than a leak — but only a test that
///     reads what arrived tells a dropped caller from a refused one. <c>ActionContext.Caller</c>
///     defaults to an empty subject, so the compiler says nothing when a call site forgets it.
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class ActionCallerTests(ResourceManagerCluster cluster) {
    [Fact]
    public async Task TheHandlerIsHandedTheCallerTheRequestCarried() {
        ResourceManagerCluster.ResetDoubles();
        RestartHandler.Reset();

        var address = ResourceManagerCluster.Address("caller-echo");
        var created = await cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        // Not the suite's default subject, so an answer that came from a default isn't this one.
        var caller = ResourceManagerCluster.Caller(subject: "grace-the-caller") with { CorrelationId = "caller-echo" };

        var action = await cluster.Manager.ActionAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Post,
                Action = "restart",
                Body = "{}",
                Caller = caller
            },
            TestContext.Current.CancellationToken
        );

        action.IsSuccess.ShouldBeTrue(action.Error?.Message);
        RestartHandler.Invocations.ShouldBe(1);

        var handed = RestartHandler.LastCaller.ShouldNotBeNull();
        handed.TenantId.ShouldBe(ResourceManagerCluster.Tenant);
        handed.SubjectType.ShouldBe("user");
        handed.SubjectId.ShouldBe("grace-the-caller", "the handler re-checks access as this subject, and would otherwise ask about nobody");
        handed.CorrelationId.ShouldBe("caller-echo");
    }
}
