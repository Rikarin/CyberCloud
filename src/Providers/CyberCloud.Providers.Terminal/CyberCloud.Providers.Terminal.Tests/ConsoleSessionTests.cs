using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Terminal.Tests;

/// <summary>
///     The two calls that start and stop a shell — the half of this row the shared conformance suite
///     never reaches.
/// </summary>
public sealed class ConsoleSessionTests {
    [Fact]
    public async Task ConnectRefusesAConsoleWhoseNetworkPolicyIsNotThereYet() {
        // ⚠ THE SECURITY BOUNDARY OF THIS ROW, ASSERTED DIRECTLY. The policy is the LAST of the three
        // objects the reconciler applies, so a console mid-provision has a home volume and an identity
        // and no constraint. Starting a shell then would give a person an unconstrained terminal
        // holding a managed identity — for a few seconds, which is long enough.
        //
        // The world here has had one pass with the applies swallowed, so nothing durable exists.
        var connection = new RecordingConnection { SwallowApplies = true };
        using var desired = JsonDocument.Parse(CloudConsoles.Body(ConsoleReconcilerTests.ClusterId));

        await ConsoleReconcilerTests.Reconcile(connection, desired.RootElement);

        var connected = await ConsoleReconcilerTests.Connect(connection, desired.RootElement);

        connected.IsSuccess.ShouldBeFalse();
        connected.Error!.Code.ShouldBe(ErrorCode.PreconditionFailed);
        connection.Applied.ShouldNotContain(x => x.Target.Kind.Kind == "Pod", "a shell was started anyway");
    }

    [Fact]
    public async Task ConnectRefusesWhenOnlyTheNetworkPolicyIsMissing() {
        // ⚠ THE SHARPER HALF: the volume and the identity are there and only the constraint is not,
        // which is the exact state a half-finished pass leaves and the one a "does the console exist"
        // check would wave through.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(CloudConsoles.Body(ConsoleReconcilerTests.ClusterId));

        (await ConsoleReconcilerTests.Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();

        connection.Objects.TryRemove(
            RecordingConnection.Key(
                CloudConsoles.NetworkPolicyRef(ConsoleReconcilerTests.Namespace, "observed")
            ),
            out _
        ).ShouldBeTrue();

        var connected = await ConsoleReconcilerTests.Connect(connection, desired.RootElement);

        connected.IsSuccess.ShouldBeFalse();
        connected.Error!.Message.ShouldContain("NetworkPolicy");
        connection.Applied.ShouldNotContain(x => x.Target.Kind.Kind == "Pod");
    }

    [Fact]
    public async Task ConnectStartsOneShellAndDescribesIt() {
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(CloudConsoles.Body(ConsoleReconcilerTests.ClusterId));

        (await ConsoleReconcilerTests.Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();

        var connected = await ConsoleReconcilerTests.Connect(connection, desired.RootElement);
        connected.IsSuccess.ShouldBeTrue(connected.Error?.Message);

        var answer = JsonNode.Parse(connected.GetValueOrThrow())!.AsObject();

        answer["hub"]!.GetValue<string>().ShouldBe("/hubs/terminal");
        answer[CloudConsoles.SessionIdField]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        answer["idleTimeoutSeconds"]!.GetValue<int>().ShouldBe(1200);
        answer["maxDurationSeconds"]!.GetValue<int>().ShouldBe(28800);
        answer["recording"]!.GetValue<bool>().ShouldBeFalse();

        // ⚠ `Starting`, because the harness accepts the pod with no status.phase — which is what a real
        // API server answers until the kubelet reports one. A panel opens the socket either way.
        answer["state"]!.GetValue<string>().ShouldBe("Starting");
    }

    [Fact]
    public async Task ARunningShellIsReportedReady() {
        var connection = new RecordingConnection { PodPhase = "Running" };
        using var desired = JsonDocument.Parse(CloudConsoles.Body(ConsoleReconcilerTests.ClusterId));

        await ConsoleReconcilerTests.Reconcile(connection, desired.RootElement);

        var connected = await ConsoleReconcilerTests.Connect(connection, desired.RootElement);

        JsonNode.Parse(connected.GetValueOrThrow())!["state"]!.GetValue<string>().ShouldBe("Ready");
    }

    [Fact]
    public async Task ReconnectingReturnsTheSameSessionRatherThanStartingASecondShell() {
        // ⚠ THE ORDINARY CASE, AND THE ONE A `create` WOULD ANSWER 409 TO. A second browser tab, or a
        // dropped Wi-Fi connection resuming, applies the same object and gets it back unchanged —
        // docs/plan/19: "the difference between a feature people use and one they do not".
        //
        // The session id is the pod's own UID, so "same shell" is the cluster's answer rather than
        // bookkeeping this handler would have to hold — and a handler holds no state.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(CloudConsoles.Body(ConsoleReconcilerTests.ClusterId));

        await ConsoleReconcilerTests.Reconcile(connection, desired.RootElement);

        var first = await ConsoleReconcilerTests.Connect(connection, desired.RootElement);
        var second = await ConsoleReconcilerTests.Connect(connection, desired.RootElement);

        Session(first).ShouldBe(Session(second));

        connection.Objects.Keys.Count(x => x.StartsWith("Pod/", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task ASessionIdIsRefusedRatherThanInventedWhenTheClusterDoesNotSupplyOne() {
        // ⚠ A session id a client cannot name on the hub is worse than an error it can retry. This is
        // the branch a handler would be tempted to paper over with a Guid.NewGuid(), which would differ
        // between two connects to the same live shell and would make a client throw away a replay
        // buffer that was still valid.
        var connection = new RecordingConnection { AssignsUids = false };
        using var desired = JsonDocument.Parse(CloudConsoles.Body(ConsoleReconcilerTests.ClusterId));

        await ConsoleReconcilerTests.Reconcile(connection, desired.RootElement);

        var connected = await ConsoleReconcilerTests.Connect(connection, desired.RootElement);

        connected.IsSuccess.ShouldBeFalse();
        connected.Error!.Message.ShouldContain("metadata.uid");
    }

    [Fact]
    public async Task TerminateRemovesTheShellAndLeavesEverythingElse() {
        // ⚠ The same thing the idle reclaim does, which is why the two can coexist. The home volume,
        // the identity and the policy survive, so the next connect is a warm start rather than a
        // re-provision.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(CloudConsoles.Body(ConsoleReconcilerTests.ClusterId));

        await ConsoleReconcilerTests.Reconcile(connection, desired.RootElement);
        await ConsoleReconcilerTests.Connect(connection, desired.RootElement);

        var terminated = await ConsoleReconcilerTests.Connect(
            connection,
            desired.RootElement,
            CloudConsoles.TerminateAction
        );

        JsonNode.Parse(terminated.GetValueOrThrow())!["terminated"]!.GetValue<bool>().ShouldBeTrue();

        connection.Objects.Keys.ShouldNotContain(x => x.StartsWith("Pod/", StringComparison.Ordinal));
        connection.Objects.Count.ShouldBe(3);

        // ⚠ Foreground, so the call does not return until the container is gone. A stop button that
        // does not stop anything is, on a resource holding an identity, the one control a person has
        // to be able to trust.
        connection.Cascades.ShouldBe([CascadePolicy.Foreground]);
    }

    [Fact]
    public async Task TerminatingAConsoleWithNoShellIsASuccessCarryingFalse() {
        // ⚠ NOT A 404. The caller's goal is that no shell is running, and a console that was already
        // idle has achieved it. Answering 404 would make the ordinary case — clicking "close" on a
        // session that timed out while the tab was in the background — look like a failure.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(CloudConsoles.Body(ConsoleReconcilerTests.ClusterId));

        await ConsoleReconcilerTests.Reconcile(connection, desired.RootElement);

        var terminated = await ConsoleReconcilerTests.Connect(
            connection,
            desired.RootElement,
            CloudConsoles.TerminateAction
        );

        terminated.IsSuccess.ShouldBeTrue();
        JsonNode.Parse(terminated.GetValueOrThrow())!["terminated"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task AShellStartedAfterATerminateFindsTheSameHomeVolume() {
        // The reclaim story, one layer in: what survives is the claim, and the new pod names it.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(CloudConsoles.Body(ConsoleReconcilerTests.ClusterId));

        await ConsoleReconcilerTests.Reconcile(connection, desired.RootElement);
        await ConsoleReconcilerTests.Connect(connection, desired.RootElement);
        await ConsoleReconcilerTests.Connect(connection, desired.RootElement, CloudConsoles.TerminateAction);

        var again = await ConsoleReconcilerTests.Connect(connection, desired.RootElement);
        again.IsSuccess.ShouldBeTrue();

        var pod = connection.Applied.Last(x => x.Target.Kind.Kind == "Pod");

        JsonNode.Parse(pod.Body)!["spec"]!["volumes"]!.AsArray()[0]!["persistentVolumeClaim"]!["claimName"]!
            .GetValue<string>()
            .ShouldBe(CloudConsoles.HomeClaimName("observed"));
    }

    [Fact]
    public async Task RecordingReachesTheConnectResponseSoThePortalCanBeLoudAboutIt() {
        // docs/plan/19 § Auditing: full-session recording is "loud in the UI when it is on". A panel
        // that had to fetch the resource to find out would render one frame of a terminal that lies.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(
            CloudConsoles.Body(ConsoleReconcilerTests.ClusterId, recording: true)
        );

        await ConsoleReconcilerTests.Reconcile(connection, desired.RootElement);

        var connected = await ConsoleReconcilerTests.Connect(connection, desired.RootElement);

        JsonNode.Parse(connected.GetValueOrThrow())!["recording"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task TheAnswersPropertyNamesAreTheDeclaredResponsesPointersWithTheSlashRemoved() {
        // ⚠ The dispatcher validates a handler's JSON against the action's declared Response before it
        // reaches a caller, so a shape that drifted from what the provider published fails loudly. This
        // is the same check one layer earlier, where the failure names the field.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(CloudConsoles.Body(ConsoleReconcilerTests.ClusterId));

        await ConsoleReconcilerTests.Reconcile(connection, desired.RootElement);

        var connected = await ConsoleReconcilerTests.Connect(connection, desired.RootElement);

        JsonNode.Parse(connected.GetValueOrThrow())!.AsObject().Select(x => x.Key).Order(StringComparer.Ordinal)
            .ShouldBe(
                CloudConsoles.ConnectResponse.Properties
                    .Select(x => x.JsonPointer[1..])
                    .Order(StringComparer.Ordinal)
            );

        var terminated = await ConsoleReconcilerTests.Connect(
            connection,
            desired.RootElement,
            CloudConsoles.TerminateAction
        );

        JsonNode.Parse(terminated.GetValueOrThrow())!.AsObject().Select(x => x.Key)
            .ShouldBe(CloudConsoles.TerminateResponse.Properties.Select(x => x.JsonPointer[1..]));
    }

    static string Session(Result<string> connected) =>
        JsonNode.Parse(connected.GetValueOrThrow())![CloudConsoles.SessionIdField]!.GetValue<string>();
}
