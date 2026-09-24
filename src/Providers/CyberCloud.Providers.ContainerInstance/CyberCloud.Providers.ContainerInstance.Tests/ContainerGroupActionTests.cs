using CyberCloud.ResourceManager.Reconcile;
using System.Text.Json;

namespace CyberCloud.Providers.ContainerInstance.Tests;

/// <summary><c>logs</c> and <c>restart</c>, against a connection that holds a pod and what it wrote.</summary>
/// <remarks>
///     The same two actions run against a real kubelet in
///     <c>CyberCloud.Providers.ContainerInstance.Cluster.Conformance</c> § <c>ContainerGroupLifecycleConformance</c>;
///     what this class adds is the refusals, which a real cluster makes slow to reach.
/// </remarks>
public sealed class ContainerGroupActionTests {
    [Fact]
    public async Task LogsReadsTheFirstContainerByDefaultANamedOneOnRequestAndATailOfIt() {
        var (connection, address, body) = await Provisioned(["web=nginx:1.27", "log=busybox:1.37"]);
        connection.Logs["web"] = "one\ntwo\nthree\n";
        connection.Logs["log"] = "sidecar\n";

        var first = await Invoke(connection, address, body, ContainerGroups.LogsAction);

        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        using (var answer = JsonDocument.Parse(first.GetValueOrThrow())) {
            ContainerGroups.LogsResponse.Validate(answer.RootElement).IsSuccess.ShouldBeTrue();
            answer.RootElement.GetProperty("container").GetString().ShouldBe("web");
            answer.RootElement.GetProperty("log").GetString().ShouldBe("one\ntwo\nthree\n");
            answer.RootElement.GetProperty("readAt").GetString().ShouldStartWith("2026-09-23T12:00:00");
        }

        var tail = await Invoke(connection, address, body, ContainerGroups.LogsAction, """{"container":"web","tailLines":1}""");
        JsonDocument.Parse(tail.GetValueOrThrow()).RootElement.GetProperty("log").GetString().ShouldBe("three\n");

        var sidecar = await Invoke(connection, address, body, ContainerGroups.LogsAction, """{"container":"log"}""");
        JsonDocument.Parse(sidecar.GetValueOrThrow()).RootElement.GetProperty("log").GetString().ShouldBe("sidecar\n");
    }

    [Fact]
    public async Task LogsOfAContainerTheGroupDoesNotHaveIsRefusedAndNamesTheOnesItHas() {
        var (connection, address, body) = await Provisioned(["web=nginx:1.27"]);

        var refused = await Invoke(connection, address, body, ContainerGroups.LogsAction, """{"container":"other"}""");

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Message.ShouldContain("web");
    }

    [Fact]
    public async Task LogsOfAContainerNotYetStartedIsRetryable() {
        var (connection, address, body) = await Provisioned(["web=nginx:1.27"]);

        var waiting = await Invoke(connection, address, body, ContainerGroups.LogsAction);

        waiting.IsFailure.ShouldBeTrue();
        waiting.Error!.Code.ShouldBe(ErrorCode.OperationInProgress, "a container being pulled is a retry, not a failure");
    }

    [Fact]
    public async Task RestartReplacesThePodUnderTheSameNameAndAnswersBothUids() {
        var (connection, address, body) = await Provisioned(["web=nginx:1.27"]);
        var pod = ContainerGroups.PodRef(ReconcileDriver.NamespaceFor(address), "web");
        var before = ContainerGroups.UidOf(connection.Objects[GroupConnection.Key(pod)]);

        var restarted = await Invoke(connection, address, body, ContainerGroups.RestartAction);

        restarted.IsSuccess.ShouldBeTrue(restarted.Error?.Message);
        using var answer = JsonDocument.Parse(restarted.GetValueOrThrow());
        ContainerGroups.RestartResponse.Validate(answer.RootElement).IsSuccess.ShouldBeTrue();
        answer.RootElement.GetProperty("podUidBefore").GetString().ShouldBe(before);
        answer.RootElement.GetProperty("podUid").GetString().ShouldNotBe(before);

        connection.Deleted.ShouldBe([pod]);
        connection.Applied.Count(static x => x.Target.Kind.Kind == "Pod").ShouldBe(2, "the reconciler's and the restart's");
        connection.Applied.Last().FieldManager.ShouldBe(connection.Applied.First(static x => x.Target.Kind.Kind == "Pod").FieldManager);
    }

    [Theory]
    [InlineData(ContainerGroups.LogsAction)]
    [InlineData(ContainerGroups.RestartAction)]
    public async Task AnActionOnAGroupWithNoPodIsRefusedRatherThanCreating(string action) {
        var connection = new GroupConnection();
        using var body = Groups.Body(ContainerGroups.Body(Groups.ClusterId));

        var result = await Invoke(connection, Groups.Address("web"), body, action);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCode.OperationInProgress);
        connection.Applied.ShouldBeEmpty("an action never creates — docs/plan/08 § The write path, end to end");
    }

    [Fact]
    public void ARestartGivesUpInsideTheDispatchersBudgetSoItsOwnAnswerIsTheOneReturned() {
        // ⚠ #28's review: the wait was 45 s and the dispatcher cancels at 30, so a slow pod came back as
        // the dispatcher's InternalError rather than this handler's retryable OperationInProgress.
        (ContainerGroupActionHandler.RestartBudget + TimeSpan.FromSeconds(5)).ShouldBeLessThan(ReconcileDriver.PassBudget);
        ContainerGroupActionHandler.RestartBudget.ShouldBeGreaterThan(TimeSpan.FromSeconds(ContainerGroups.TerminationGracePeriodSeconds));
    }

    [Fact]
    public void OneHandlerServesBothActionsAndNeitherIsLongRunning() {
        var registry = CyberCloud.ResourceManager.Registry.ProviderRegistry.Build([new ContainerInstanceProvider()]);
        registry.TryGetType(ContainerGroups.Type, out var registration).ShouldBeTrue();

        registration.TryGetAction(ContainerGroups.LogsAction, out var logs).ShouldBeTrue();
        logs.HandlerType.ShouldBe(typeof(ContainerGroupActionHandler));
        logs.Permission.ShouldBe("read");
        logs.Request.ShouldBe(ContainerGroups.LogsRequest);
        logs.LongRunning.ShouldBeFalse();

        registration.TryGetAction(ContainerGroups.RestartAction, out var restart).ShouldBeTrue();
        restart.HandlerType.ShouldBe(typeof(ContainerGroupActionHandler));
        restart.Permission.ShouldBe("write");
    }

    static async Task<(GroupConnection Connection, ResourceId Address, JsonDocument Body)> Provisioned(string[] containers) {
        var connection = new GroupConnection();
        var address = Groups.Address("web");
        var body = Groups.Body(ContainerGroups.Body(Groups.ClusterId, containers));

        (await Groups.Reconcile(connection, address, body)).Kind.ShouldBe(ReconcileOutcomeKind.Converged);

        return (connection, address, body);
    }

    static Task<Result<string>> Invoke(
        GroupConnection connection,
        ResourceId address,
        JsonDocument body,
        string action,
        string request = "{}"
    ) =>
        new ContainerGroupActionHandler(new FixedClock()).InvokeAsync(
            Groups.Action(connection, address, body.RootElement, action, request),
            TestContext.Current.CancellationToken
        );
}
