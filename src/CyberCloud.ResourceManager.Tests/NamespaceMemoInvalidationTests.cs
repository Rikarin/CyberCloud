using CyberCloud.Core.Time;
using CyberCloud.Kubernetes.Contracts;
using CyberCloud.ResourceManager.Reconcile;
using CyberCloud.ResourceManager.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System.Collections.Concurrent;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     The namespace memo's invalidation channel, driven through the real
///     <see cref="ReconcileDriver" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE MEMO HAS ALREADY HIDDEN ONE LIVE DEFECT FROM THIS SUITE, WHICH IS WHY THIS IS A
///         DRIVER TEST AND NOT AN ENSURER TEST.</b> While the memo was invisible to the harness, an
///         ensure failure was hard-coded retryable and nothing noticed. What is under test here is
///         the wiring — that a pass coming back
///         <see cref="ErrorCode.ResourceNotFound" /> is read as evidence the namespace is gone — and
///         that only exists in the driver. <c>NamespaceEnsurerTests</c> covers the forget itself.
///     </para>
///     <para>
///         ⚠ <b>The driver is constructed here rather than resolved from the silo.</b> The suite's
///         silo registers <c>NoClusterConnectionFactory</c>, so a pass there never has a connection
///         and never ensures a namespace at all; giving the whole silo one would put a namespace
///         apply in front of every other class's assertions. This builds the one under test, with
///         the one connection it needs, and drives the same <c>RunAsync</c> the operation grain
///         drives.
///     </para>
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class NamespaceMemoInvalidationTests(ResourceManagerCluster cluster) {
    static Guid Cluster { get; } = new("9e2b0000-0000-4000-8000-000000000001");

    [Fact]
    public async Task APassThatComesBackNotFoundDropsTheBeliefThatTheNamespaceExists() {
        ResourceManagerCluster.ResetDoubles();

        var address = ResourceManagerCluster.Address("memo-invalidated");

        var accepted = (await cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        )).GetValueOrThrow();

        var connection = new CountingConnection(Cluster);
        var ensurer = new NamespaceEnsurer(TestClock.Instance);
        var driver = Driver(connection, ensurer);

        var spec = new OperationSpec {
            OperationId = accepted.OperationId,
            Kind = OperationKind.Create,
            ResourcePath = address.Path,
            ResourceId = accepted.Resource.Id,
            TenantId = ResourceManagerCluster.Tenant,
            SubscriptionId = ResourceManagerCluster.Subscription,
            ApiVersion = TestingProvider.V2026,
            Desired = TestingProvider.Body()
        };

        // Pass 1: the namespace is applied and the memo goes warm.
        (await driver.RunAsync(spec, false, TestContext.Current.CancellationToken))
            .Outcome.Kind.ShouldNotBe(ReconcileOutcomeKind.Failed);

        connection.Namespaces.ShouldBe(1);

        // Pass 2: the memo answers, so the namespace is not re-applied — and the pass fails the way
        // an apply into a namespace that is not there fails. ⚠ This is the state the issue describes:
        // the namespace was removed by an operator or by a group delete on ANOTHER silo, and this one
        // still believes in it.
        FakeWorld.FailWith[accepted.Resource.Id] = "namespaces \"…\" not found";
        FakeWorld.FailCode[accepted.Resource.Id] = ErrorCode.ResourceNotFound;

        (await driver.RunAsync(spec, false, TestContext.Current.CancellationToken))
            .Outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);

        connection.Namespaces.ShouldBe(1, "the memo answered, which is the whole problem.");

        // Pass 3: the belief was dropped, so the namespace is applied again — within one pass rather
        // than after NamespaceEnsurer.RecheckAfter.
        FakeWorld.FailWith.TryRemove(accepted.Resource.Id, out _);
        FakeWorld.FailCode.TryRemove(accepted.Resource.Id, out _);

        (await driver.RunAsync(spec, false, TestContext.Current.CancellationToken))
            .Outcome.Kind.ShouldNotBe(ReconcileOutcomeKind.Failed);

        connection.Namespaces.ShouldBe(
            2,
            "the 404 is the memo's only invalidation channel — without it this silo would apply into "
            + "a namespace that is not there for the rest of NamespaceEnsurer.RecheckAfter."
        );
    }

    [Fact]
    public async Task AnOrdinaryFailureLeavesTheBeliefAlone() {
        // ⚠ THE OTHER HALF. The forget is deliberately imprecise — it fires on any ResourceNotFound,
        // including one about the reconciler's own object — but it must not fire on every failure, or
        // the memo would be worthless and every failing resource would cost a namespace apply per
        // pass.
        ResourceManagerCluster.ResetDoubles();

        var address = ResourceManagerCluster.Address("memo-kept");

        var accepted = (await cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        )).GetValueOrThrow();

        var connection = new CountingConnection(Cluster);
        var driver = Driver(connection, new(TestClock.Instance));

        var spec = new OperationSpec {
            OperationId = accepted.OperationId,
            Kind = OperationKind.Create,
            ResourcePath = address.Path,
            ResourceId = accepted.Resource.Id,
            TenantId = ResourceManagerCluster.Tenant,
            SubscriptionId = ResourceManagerCluster.Subscription,
            ApiVersion = TestingProvider.V2026,
            Desired = TestingProvider.Body()
        };

        await driver.RunAsync(spec, false, TestContext.Current.CancellationToken);
        connection.Namespaces.ShouldBe(1);

        FakeWorld.FailWith[accepted.Resource.Id] = "the operator declined the shape";

        (await driver.RunAsync(spec, false, TestContext.Current.CancellationToken))
            .Outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);

        FakeWorld.FailWith.TryRemove(accepted.Resource.Id, out _);

        await driver.RunAsync(spec, false, TestContext.Current.CancellationToken);

        connection.Namespaces.ShouldBe(
            1,
            "a ProvisioningFailed says nothing about the namespace, and re-applying it on every "
            + "failed pass would defeat the memo."
        );
    }

    ReconcileDriver Driver(IKubeClusterConnection connection, NamespaceEnsurer namespaces) {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(TestClock.Instance);
        services.AddSingleton<ConformingReconciler>();

        return new(
            cluster.Registry,
            services.BuildServiceProvider(),
            cluster.Grains,
            new OneConnectionFactory(connection),
            new UnavailableClusterConnectionRegistrar(),
            new UnavailableSecretResolver(),
            new UnavailableSecretWriter(),
            namespaces,
            TestClock.Instance
        );
    }

    /// <summary>Counts the namespace applies and lets everything else through.</summary>
    sealed class CountingConnection(Guid cluster) : IKubeClusterConnection {
        readonly ConcurrentQueue<KubeCommand> applied = new();

        public Guid ClusterId => cluster;

        /// <summary>How many cluster-scoped <c>Namespace</c> applies this connection has seen.</summary>
        public int Namespaces => applied.Count(x => x.Target.Kind.Kind == "Namespace");

        public Task<Result<ApplyOutcome>> ApplyAsync(
            KubeCommand command,
            CancellationToken cancellationToken = default
        ) {
            ArgumentNullException.ThrowIfNull(command);
            applied.Enqueue(command);

            return Task.FromResult(
                Result<ApplyOutcome>.Success(new() { Result = ApplyResult.Created, Target = command.Target })
            );
        }

        public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<KubeObject>.Failure(ErrorCode.ResourceNotFound, $"{target} is not here."));

        public Task<Result> DeleteAsync(
            KubeCommand command,
            CascadePolicy policy = CascadePolicy.Background,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(Result.Success);
    }

    /// <summary>Answers with one connection whatever cluster is named.</summary>
    /// <remarks>
    ///     ⚠ Deliberately not keyed on the id, unlike the production factory: the test bodies carry no
    ///     <c>clusterId</c>, and what is under test is the namespace's lifecycle rather than the
    ///     placement. The driver keys the memo and the forget on <c>connection.ClusterId</c> for
    ///     exactly this reason — it is the id the write actually went through.
    /// </remarks>
    sealed class OneConnectionFactory(IKubeClusterConnection connection) : IClusterConnectionFactory {
        public IKubeClusterConnection? Connect(Guid clusterId) => connection;
    }
}
