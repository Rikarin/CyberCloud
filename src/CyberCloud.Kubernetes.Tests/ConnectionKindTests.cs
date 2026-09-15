using CyberCloud.Kubernetes.Connections;
using CyberCloud.Kubernetes.Tests.Infrastructure;
using CyberCloud.Kubernetes.Tunnel;
using Shouldly;

namespace CyberCloud.Kubernetes.Tests;

/// <summary>
///     The connection kinds of docs/plan/09 § Cluster connections, including the one that was
///     deliberately not built until #36.
/// </summary>
[Collection(KubeClusterSuite.Name)]
public sealed class ConnectionKindTests(KubeTestCluster cluster) {
    static readonly KubeApiClientFactory Factory = new(new TestClock());

    [Fact]
    public async Task AgentInitiatedIsRoutedThroughTheTunnelGrainWhenThereIsASilo() {
        // ⚠ BUILT, BY #36, AND THIS USED TO ASSERT THE REFUSAL. docs/plan/09 § Cluster connections
        // budgets AgentInitiated at 1.5 EM in M2 and warns that it "is not optional and is easy to
        // defer into a crisis"; the refusal this test pinned was the honest answer until the tunnel
        // existed. What it pins now is that the factory hands the connection grain a client whose
        // route is the tunnel grain for THIS cluster — the same IKubeApiClient surface, so the
        // grain's tenancy check and health window run over it unchanged.
        var withSilo = new KubeApiClientFactory(new TestClock(), grains: cluster.Grains);

        var outcome = await withSilo.ConnectAsync(
            Descriptor(ClusterConnectionKind.AgentInitiated),
            TestContext.Current.CancellationToken
        );

        outcome.IsSuccess.ShouldBeTrue(outcome.Error?.Message);

        var client = outcome.GetValueOrThrow().ShouldBeOfType<TunnelKubeApiClient>();
        client.Route.ShouldBeOfType<GrainTunnelRoute>().ClusterId.ShouldBe(Descriptor(ClusterConnectionKind.AgentInitiated).ClusterId);
    }

    [Fact]
    public async Task AgentInitiatedIsRefusedByNameInAProcessWithNoGrainFactory() {
        // A stub that pretended to connect would still be exactly how the gap is discovered at the
        // first on-prem customer, so a factory that cannot reach a silo says so.
        var outcome = await Factory.ConnectAsync(
            Descriptor(ClusterConnectionKind.AgentInitiated),
            TestContext.Current.CancellationToken
        );

        outcome.IsFailure.ShouldBeTrue();
        outcome.Error!.Message.ShouldContain("IAgentTunnelGrain");
        outcome.Error.Message.ShouldContain("no grain factory");
    }

    [Fact]
    public async Task AKindlessDescriptorIsRefused() {
        var outcome = await Factory.ConnectAsync(
            Descriptor(ClusterConnectionKind.Unknown),
            TestContext.Current.CancellationToken
        );

        outcome.IsFailure.ShouldBeTrue();
        outcome.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
    }

    [Fact]
    public async Task AKubeconfigConnectionNeedsAResolverAndSaysWhereItComesFrom() {
        // The vault seam. CyberCloud.KeyVault (docs/plan/18) does not exist, so the refusal names
        // the missing assembly rather than failing with "not found".
        var outcome = await Factory.ConnectAsync(
            Descriptor(ClusterConnectionKind.Kubeconfig),
            TestContext.Current.CancellationToken
        );

        outcome.IsFailure.ShouldBeTrue();
        outcome.Error!.Message.ShouldContain("CyberCloud.KeyVault");
        outcome.Error.Message.ShouldContain("Vault");
    }

    [Fact]
    public async Task AResolvedKubeconfigProducesAClient() {
        // The seam works when filled — the same shape the k3s fixture uses in production form.
        var factory = new KubeApiClientFactory(new TestClock()) {
            ResolveKubeconfig = (_, _) => Task.FromResult(
                Result<string>.Success(
                    """
                    apiVersion: v1
                    kind: Config
                    clusters:
                    - cluster: { server: https://cluster.example:6443 }
                      name: c
                    contexts:
                    - context: { cluster: c, user: u }
                      name: ctx
                    current-context: ctx
                    users:
                    - name: u
                      user: { token: abc }
                    """
                )
            )
        };

        var outcome = await factory.ConnectAsync(
            Descriptor(ClusterConnectionKind.Kubeconfig),
            TestContext.Current.CancellationToken
        );

        outcome.IsSuccess.ShouldBeTrue(outcome.Error?.Message);
        outcome.GetValueOrThrow().Dispose();
    }

    [Fact]
    public void TheKindsAreTheDocumentsFourPlusTheDefault() {
        Enum.GetValues<ClusterConnectionKind>()
            .Length.ShouldBe(
                5,
                "docs/plan/09 § Cluster connections has four rows, plus Unknown for default(enum)."
            );
    }

    static ClusterConnectionDescriptor Descriptor(ClusterConnectionKind kind) =>
        new() {
            ClusterId = Guid.Parse("3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d"),
            OwningTenantId = Guid.Parse("9f2c1b7e-3d4a-4f21-9c6b-0a1e2d3c4b5a"),
            Kind = kind,
            CredentialRef = "vault://clusters/x"
        };
}
