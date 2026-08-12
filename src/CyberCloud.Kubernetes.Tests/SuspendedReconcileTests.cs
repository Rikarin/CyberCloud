using CyberCloud.Core.Resources;
using CyberCloud.Kubernetes.Health;
using CyberCloud.Kubernetes.Tests.Infrastructure;
using k8s.Autorest;
using Orleans.Serialization;
using Shouldly;
using System.Net;
using System.Net.Http;

namespace CyberCloud.Kubernetes.Tests;

/// <summary>
///     "Suspended, not failed" — docs/plan/09 § Cluster connections, through the real grain.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/09 § Cluster connections:
///         <i>
///             "A cluster that has not answered a ping in 90
///             seconds is <c>Degraded</c>; its resources' reconciles are <b>suspended (not failed)</b>
///             and the portal says 'cannot reach your cluster' instead of 'provisioning failed'. The
///             distinction between our failure and unreachable is what stops a tenant's network outage
///             from looking like a platform bug."
///         </i>
///     </para>
///     <para>
///         <c>ClusterHealthTests</c> covers the transition in isolation; this covers the consequence
///         — that an apply against a degraded cluster comes back as a <b>successful</b>
///         <c>Result</c> carrying <see cref="ApplyResult.Suspended" />, so that nothing upstream
///         records a failed operation.
///     </para>
/// </remarks>
[Collection(KubeClusterSuite.Name)]
public sealed class SuspendedReconcileTests(KubeTestCluster cluster) {
    static readonly Guid Tenant = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    static int next;

    [Fact]
    public async Task AnApplyAgainstADegradedClusterIsSuspendedAndNotFailed() {
        // ⚠ THE FAILURE CLASS. A failed Result here would become a failed operation, a
        // "provisioning failed" in the portal, and somebody paged about a tenant's network.
        var clusterId = NewClusterId();
        var reacher = cluster.Reacher(Tenant);

        (await reacher.ReachAttachAsync(Descriptor(clusterId))).ShouldBe("ok");

        // A successful ping puts the cluster in Healthy and starts the window.
        (await reacher.ReachPingAsync(clusterId)).ShouldBe(nameof(ClusterHealthState.Healthy));

        // Nothing answers for longer than the staleness window.
        SharedTestClock.Instance.Advance(ClusterHealthTracker.StalenessWindow + TimeSpan.FromSeconds(1));

        (await reacher.ReachHealthAsync(clusterId)).ShouldBe(nameof(ClusterHealthState.Degraded));

        // The apply is a SUCCESS carrying Suspended — not a failure.
        var applied = await reacher.ReachApplyAsync(clusterId, Command());

        applied.ShouldBe(
            nameof(ApplyResult.Suspended),
            "a degraded cluster suspends reconciles; it does not fail them."
        );
        // A "<Code>" answer would mean a failed Result — see IKubeReacherGrain.
        applied.StartsWith('<').ShouldBeFalse();
    }

    [Fact]
    public async Task TheSuspendedOutcomeCarriesTheCannotReachYourClusterMessage() {
        var clusterId = NewClusterId();
        var reacher = cluster.Reacher(Tenant);

        await reacher.ReachAttachAsync(Descriptor(clusterId));
        await reacher.ReachPingAsync(clusterId);
        SharedTestClock.Instance.Advance(ClusterHealthTracker.StalenessWindow + TimeSpan.FromSeconds(1));

        var message = await reacher.ReachApplyMessageAsync(clusterId, Command());

        message.ShouldContain("Cannot reach your cluster");
        message.ShouldContain("suspended, not failed");
        message.ShouldNotContain("provisioning failed");
    }

    [Fact]
    public async Task AHealthyClusterAppliesNormally() {
        // The control: the same path, without the elapsed time, must not be suspended — otherwise
        // the test above would pass for the wrong reason.
        var clusterId = NewClusterId();
        var reacher = cluster.Reacher(Tenant);

        await reacher.ReachAttachAsync(Descriptor(clusterId));
        await reacher.ReachPingAsync(clusterId);

        (await reacher.ReachApplyAsync(clusterId, Command()))
            .ShouldNotBe(nameof(ApplyResult.Suspended));
    }

    [Fact]
    public async Task PingRecoversTheClusterAndReconcilesResume() {
        // "…and will resume automatically when the cluster is reachable again."
        var clusterId = NewClusterId();
        var reacher = cluster.Reacher(Tenant);

        await reacher.ReachAttachAsync(Descriptor(clusterId));
        await reacher.ReachPingAsync(clusterId);
        SharedTestClock.Instance.Advance(ClusterHealthTracker.StalenessWindow + TimeSpan.FromSeconds(1));

        (await reacher.ReachApplyAsync(clusterId, Command())).ShouldBe(nameof(ApplyResult.Suspended));

        (await reacher.ReachPingAsync(clusterId)).ShouldBe(nameof(ClusterHealthState.Healthy));

        (await reacher.ReachApplyAsync(clusterId, Command()))
            .ShouldNotBe(nameof(ApplyResult.Suspended));
    }

    [Fact]
    public async Task AnUnreachableClusterBecomesDegradedRatherThanFailingTheApply() {
        // The other route into Degraded: the cluster answers with an error rather than going quiet.
        var clusterId = NewClusterId();
        var reacher = cluster.Reacher(Tenant);

        await reacher.ReachAttachAsync(Descriptor(clusterId));
        await reacher.ReachPingAsync(clusterId);

        FakeApiClientFactory.Client.PingResult =
            Result<string>.Failure(ErrorCode.InternalError, "connection refused");

        try {
            SharedTestClock.Instance.Advance(ClusterHealthTracker.StalenessWindow + TimeSpan.FromSeconds(1));

            (await reacher.ReachPingAsync(clusterId)).ShouldBe(nameof(ClusterHealthState.Degraded));
            (await reacher.ReachApplyAsync(clusterId, Command()))
                .ShouldBe(nameof(ApplyResult.Suspended));
        } finally {
            FakeApiClientFactory.Client.PingResult = Result<string>.Success("v1.35.0");
        }
    }

    [Fact]
    public async Task ARefusedApplyFailsTheOperationAndLeavesTheClusterHealthy() {
        // ⚠ THE MIRROR IMAGE OF THIS WHOLE SUITE, and the failure class the 4xx mapping created the
        // moment it started returning failed Results.
        //
        // "Suspended, not failed" is right for an unreachable cluster and catastrophic for a refused
        // write: the grain folded every failed apply into the health window, so one admission
        // rejection would push a perfectly healthy cluster to Degraded, turn every later apply into
        // a SUCCESSFUL Suspended, and leave the operation rescheduling forever against an API server
        // that will refuse it every time. A hot loop that also tells the tenant "cannot reach your
        // cluster" about a cluster answering in milliseconds.
        //
        // ClusterConnectionGrain.Answered is the fix, and this is the assertion that it holds.
        var clusterId = NewClusterId();
        var reacher = cluster.Reacher(Tenant);

        await reacher.ReachAttachAsync(Descriptor(clusterId));
        (await reacher.ReachPingAsync(clusterId)).ShouldBe(nameof(ClusterHealthState.Healthy));

        FakeApiClientFactory.Client.NextApply = Result<ApplyOutcome>.Failure(
            ErrorCode.PolicyViolation,
            "the cluster's admission control refused it"
        );

        try {
            // ⚠ The window is measured from the last SUCCESS, so the discriminator is whether the
            // refusal renewed it. Two thirds of the window, a refusal, then another two thirds: past
            // the limit from the ping, well inside it from the refusal.
            var twoThirds = ClusterHealthTracker.StalenessWindow * 2 / 3;

            SharedTestClock.Instance.Advance(twoThirds);

            (await reacher.ReachApplyAsync(clusterId, Command()))
                .ShouldBe($"<{ErrorCode.PolicyViolation}>", "a refusal is a failed Result.");

            SharedTestClock.Instance.Advance(twoThirds);

            (await reacher.ReachHealthAsync(clusterId)).ShouldBe(
                nameof(ClusterHealthState.Healthy),
                "a cluster that answers 'no' is answering. Letting a refusal age the health window "
                + "would degrade it, suspend the reconcile, and reschedule the refused write forever."
            );
        } finally {
            FakeApiClientFactory.Client.NextApply = null;
        }
    }

    [Fact]
    public async Task AnUnreachableApplyStillDegradesTheCluster() {
        // The control for the test above: the SAME path with the SAME shape of failure, differing
        // only in the code, has to keep the old behaviour. Without this, Answered() could return
        // true for everything and the test above would pass for the wrong reason.
        var clusterId = NewClusterId();
        var reacher = cluster.Reacher(Tenant);

        await reacher.ReachAttachAsync(Descriptor(clusterId));
        await reacher.ReachPingAsync(clusterId);

        FakeApiClientFactory.Client.NextApply = Result<ApplyOutcome>.Failure(
            ErrorCode.InternalError,
            $"Cluster {clusterId:D} did not answer: HttpRequestException: connection refused"
        );

        try {
            var twoThirds = ClusterHealthTracker.StalenessWindow * 2 / 3;

            SharedTestClock.Instance.Advance(twoThirds);

            (await reacher.ReachApplyAsync(clusterId, Command())).ShouldBe($"<{ErrorCode.InternalError}>");

            SharedTestClock.Instance.Advance(twoThirds);

            (await reacher.ReachHealthAsync(clusterId)).ShouldBe(
                nameof(ClusterHealthState.Degraded),
                "an apply that could not reach the cluster is not evidence the cluster is up."
            );
        } finally {
            FakeApiClientFactory.Client.NextApply = null;
        }
    }

    [Fact]
    public async Task AnUnmappedClientExceptionReachesTheCallerAsACodecNotFoundException() {
        // ⚠ THE SYMPTOM, MEASURED, and the reason KubeFailures has to be exhaustive rather than
        // best-effort.
        //
        // k8s.Autorest.HttpOperationException carries no Orleans codec, so an exception that escapes
        // KubeApiClient does not reach the caller as itself — Orleans fails to serialise it and the
        // caller gets this instead, naming a type and nothing else. No status code, no reason, no
        // object: a provider's suite going red this way has no thread to pull.
        //
        // This is a characterisation test, not a wish. Nothing here catches exceptions on purpose:
        // docs/plan/00 § Coding standards keeps exceptions for bugs and infrastructure, so a grain
        // -level catch-all would bury real faults. The guarantee is that KubeApiClient maps every
        // answer the API server can give, which KubeFailureMappingTests asserts against a real k3s;
        // this test says what it costs when something slips through.
        var clusterId = NewClusterId();
        var reacher = cluster.Reacher(Tenant);

        await reacher.ReachAttachAsync(Descriptor(clusterId));
        await reacher.ReachPingAsync(clusterId);

        FakeApiClientFactory.Client.ThrowOnApply = new HttpOperationException(
            "Operation returned an invalid status code 'Forbidden'"
        ) {
            Response = new HttpResponseMessageWrapper(
                new HttpResponseMessage(HttpStatusCode.Forbidden),
                """{"kind":"Status","code":403,"reason":"Forbidden","message":"admission webhook denied the request"}"""
            )
        };

        try {
            var thrown = await Record.ExceptionAsync(() => reacher.ReachApplyAsync(clusterId, Command()));

            thrown.ShouldBeOfType<CodecNotFoundException>();
            thrown.Message.ShouldBe("Could not find a codec for type k8s.Autorest.HttpOperationException.");

            // And there is the whole problem: the 403, the reason and the webhook's own message are
            // all in the exception that was thrown, and none of them survive the grain call.
            thrown.Message.ShouldNotContain("403");
            thrown.Message.ShouldNotContain("Forbidden");
            thrown.Message.ShouldNotContain("webhook");
        } finally {
            FakeApiClientFactory.Client.ThrowOnApply = null;
        }
    }

    static Guid NewClusterId() =>
        Guid.Parse(FormattableString.Invariant($"dddddddd-0000-0000-0000-{Interlocked.Increment(ref next):D12}"));

    static ClusterConnectionDescriptor Descriptor(Guid clusterId) =>
        new() {
            ClusterId = clusterId,
            OwningTenantId = Tenant,
            Kind = ClusterConnectionKind.Kubeconfig,
            CredentialRef = "vault://clusters/test",
            DisplayName = "suspended-test"
        };

    static KubeCommand Command() {
        var id = new ResourceId(
            Tenant,
            Guid.Parse("77de4a10-1b2c-4d3e-8f90-a1b2c3d4e5f6"),
            "prod",
            new("CyberCloud.DBforPostgreSQL", "servers"),
            "main",
            Guid.Parse("3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d")
        );

        return KubeCommand.For(new UnusedConnection())
            .WithTenantId(Tenant)
            .WithResourceId(id)
            .WithKind(new() { Group = "apps", Version = "v1", Kind = "Deployment", Plural = "deployments" })
            .InNamespace("ns")
            .ObjectJson("""{"metadata":{"name":"main"}}""")
            .Build();
    }
}
