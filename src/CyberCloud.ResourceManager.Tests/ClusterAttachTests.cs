using CyberCloud.Kubernetes.Contracts;
using CyberCloud.ResourceManager.Tests.Infrastructure;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     Registering a cluster a reconcile pass produced — <see cref="IClusterConnectionRegistrar" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The half that made <c>CyberCloud.ContainerService/managedClusters</c> a type whose
///         product nothing could use.</b> <c>IClusterConnectionGrain.AttachAsync</c> was called by
///         tests and by nothing else, so a cluster converged and was then unreachable as a
///         <c>clusterId</c> forever — which is step 4 of docs/plan/24's M1 exit story, <i>"create a
///         VPC and a Postgres server <b>in it</b>"</i>.
///     </para>
///     <para>
///         ⚠ <b>The seam is above the provider because the rule that keeps it there is right.</b>
///         <c>module-layering.txt</c> refuses <c>CyberCloud.Providers.ContainerService</c> an edge to
///         <c>CyberCloud.Kubernetes</c>: a provider may reference <c>.Contracts</c> only, and
///         attaching a connection is the resource manager's job. So a reconciler reports and the
///         driver writes, which is what these tests are about.
///     </para>
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class ClusterAttachTests(ResourceManagerCluster cluster) {
    const string Endpoint = "https://10.0.0.7:6443";

    [Fact]
    public async Task AClusterReportedByAConvergedPassIsAttached() {
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("attaches");

        var created = await Create(address);
        var accepted = created.GetValueOrThrow();

        FakeWorld.ProduceClusterAt[accepted.Resource.Id] = Endpoint;

        await Converge(accepted);

        var attached = RecordingClusterRegistrar.Attached.ShouldHaveSingleItem();

        attached.Endpoint.ShouldBe(Endpoint);

        // ⚠ STAMPED BY THE DRIVER, NOT BY THE PROVIDER. The reconciler left both empty; a provider
        // that could set them would be a provider able to register a cluster under a tenant that does
        // not own it, and the grain checks that owner on every later call.
        attached.ClusterId.ShouldBe(accepted.Resource.Id);
        attached.OwningTenantId.ShouldBe(ResourceManagerCluster.Tenant);
    }

    [Fact]
    public async Task AClusterReportedByAPassThatDidNotConvergeIsNotAttached() {
        // ⚠ FAILURE CLASS (g). A connection registered before the control plane serves requests is a
        // connection every later placement fails against — and it fails against the NEXT resource the
        // tenant creates, with an error naming that resource rather than this one. docs/plan/09
        // § Kubernetes in Kubernetes budgets six to eight minutes before there is an API server, so
        // the window is minutes wide.
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("attaches-early");

        var created = await Create(address);
        var accepted = created.GetValueOrThrow();

        FakeWorld.ProduceClusterAt[accepted.Resource.Id] = Endpoint;
        FakeWorld.StayInProgress[accepted.Resource.Id] = true;

        await Converge(accepted);

        FakeWorld.Passes[accepted.Resource.Id].ShouldBeGreaterThan(0, "the reconciler ran and reported");
        RecordingClusterRegistrar.Attached.ShouldBeEmpty();
    }

    [Fact]
    public async Task AConvergedClusterWhoseConnectionCannotBeRegisteredDoesNotSucceed() {
        // ⚠ THE HARSHER OF THE TWO ANSWERS, ON PURPOSE. The reconciler is right that the cluster
        // exists; what does not exist is any way to reach it. Reporting Succeeded here would move the
        // failure to whoever tries to place something in it — one resource later, with an error about
        // the second resource.
        //
        // ⚠ NOT TERMINAL, AND THAT IS ReconcileOutcome.FromFailure's ladder rather than a weaker
        // assertion. An attach failure arrives as ErrorCode.InternalError — the code a transport
        // fault comes back under — which IsRetryable treats as retryable, so the operation backs off
        // and tries again rather than ending a create over a grain that was moving between silos.
        // What must not happen is Succeeded, and that is what this pins.
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("attach-refused");

        var created = await Create(address);
        var accepted = created.GetValueOrThrow();

        FakeWorld.ProduceClusterAt[accepted.Resource.Id] = Endpoint;
        RecordingClusterRegistrar.FailWith = "the connection grain refused the descriptor";

        await Converge(accepted);

        var status = (await cluster
            .Operation(ResourceManagerCluster.Tenant, accepted.OperationId)
            .GetAsync()).GetValueOrThrow();

        status.State.ShouldNotBe(OperationState.Succeeded);

        status.Progress.ShouldContain(
            x => x.Detail.Contains("refused the descriptor", StringComparison.Ordinal),
            "the tenant's own progress log is where the reason has to appear — a create that stalls "
            + "with nothing said is the failure this whole seam exists to stop repeating"
        );
    }

    [Fact]
    public async Task APassThatReportsNoClusterAttachesNothing() {
        // The anti-vacuity check: every other type in the catalogue reports nothing, and the driver
        // must not invent a descriptor for them.
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("no-cluster");

        await Converge((await Create(address)).GetValueOrThrow());

        RecordingClusterRegistrar.Attached.ShouldBeEmpty();
    }

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

    async Task Converge(WriteAccepted accepted) {
        var operation = cluster.Operation(ResourceManagerCluster.Tenant, accepted.OperationId);

        for (var i = 0; i < 5; i++) {
            var status = await operation.DriveAsync();

            if (status.GetValueOrThrow().IsTerminal) {
                return;
            }
        }
    }
}
